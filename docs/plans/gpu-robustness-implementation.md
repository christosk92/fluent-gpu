# GPU Robustness Plan — adapter selection, hybrid-GPU correctness, frame pacing (all vendors)

## Context

A field report (AMD Ryzen desktop: RDNA2 iGPU + RX 6700 XT, "6/10 smoothness") triggered a 12-agent investigation
workflow (code audit + web research + adversarial verify, 79 facts). Two structural engine defects confirmed, both
vendor-agnostic — they hit AMD iGPU+dGPU desktops, NVIDIA Optimus laptops, Intel+NVIDIA/AMD mixes, and high-refresh
displays on any vendor:

1. **No adapter selection.** `D3D12Device.InitDevice` calls `D3D12CreateDevice(null,…)` — DXGI adapter 0 = the GPU
   driving the primary monitor, not the fastest. On hybrid machines the app can land on the weak GPU; the UMA check
   then sets `GpuPowerTier.Weak`, degrading visuals too. Nothing logs which adapter was chosen. The MF video engine's
   D3D11 device has the same defect. Canon (pal-rhi.md §3:348) already *specifies* the IDXGIFactory6 HIGH_PERFORMANCE
   pick — this closes a documented canon gap (windows-backends.md:95-99 records the deviation).
2. **Zero pipelining slack.** FRAME_COUNT=2 + `SetMaximumFrameLatency(1)` serializes CPU-record and GPU-execute
   against refresh; any frame whose CPU+GPU cost exceeds one refresh period (6.06 ms @165 Hz, 6.9 ms @144 Hz)
   quantizes to half rate. The ":24 every site keys off this const" comment is FALSE — 8 pipelines + 3 compositors
   carry independent depth-2 assumptions (the compositors as hidden `& 1` parity banks that silently corrupt at
   depth 3).

Plus telemetry so hybrid/driver pathologies (Win11 24H2 MPO churn, RX 6000 MPO regression, cross-adapter present)
stop being invisible.

**User decisions:** all four workstreams; **3 buffers + latency 2 everywhere** (no hardware fork — DecodeScheduler
covers the historical Adreno DEVICE_HUNG suspect and stays on). House rules: no env switches (always-on lines), no
legacy paths, no source-text tests; cold COM = raw TerraFX `__uuidof` QI (no comabi.json change).

Workstreams: **G** groundwork · **A** adapter selection + identity log · **B** present topology + glitch telemetry ·
**C** 3 buffers/latency 2 · **D** video decode adapter pinning · **Docs**.

---

## WS-G: Groundwork (A/B/D depend on it)

### G1. `Diag.Line` — always-on line writer — `src/FluentGpu.Engine/Foundation/Diag.cs` (insert after `Sink`, :30)

```csharp
/// <summary>ALWAYS-ON operational line writer — the small set of load-bearing evidence lines
/// (<c>[device-lost]</c>, <c>[d3d12.adapter]</c>, <c>[d3d12.present]</c>, <c>[video.d3d11]</c>). Deliberately
/// NOT <c>[Conditional]</c>: these lines are the Release-build proof trail (budgets.md "always-on plain counter"
/// posture). Routes to <see cref="Sink"/> when a harness installed one, else stderr — never stdout, so app
/// output stays clean. Callers own the cadence contract: never per-present / per-frame.</summary>
public static void Line(string line)
{
    if (Sink is { } sink) sink(line);
    else Console.Error.WriteLine(line);
}
```

`AppHost.cs:851-855`: `private static void WriteDeviceLostLine(string line) => Diag.Line(line);` (replace outright).

### G2. `GpuProfile` adapter identity — `src/FluentGpu.Engine/Foundation/GpuProfile.cs` (after `IsWeak`, :38)

```csharp
/// <summary>Marketing description of the adapter the device was created on (DXGI adapter description), published
/// once at device init (and re-published on device recovery) by the backend — app-read. Empty until set.
/// TerraFX-free: a plain string crosses the seam.</summary>
public static string AdapterName { get; set; } = "";

/// <summary>True when the device landed on a software rasterizer (WARP / the DXGI software-adapter flag).
/// Complements <see cref="IsWeak"/>: software implies Weak, but Weak (an iGPU) does not imply software.</summary>
public static bool IsSoftwareAdapter { get; set; }
```

### G3. `GpuAdapterInfo` — NEW `src/FluentGpu.Windows/D3D12/GpuAdapterInfo.cs` (A→D bridge)

```csharp
using TerraFX.Interop.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Process-global identity of the adapter the D3D12 device was created on — published in InitDevice
/// (re-published on recovery), read by sibling device creators that must land on the SAME GPU (the D3D11
/// video-decode device). GpuProfile-shaped, but lives in FluentGpu.Windows because LUID is Windows interop and
/// the Engine stays TerraFX-free. Torn-read-free: both halves pack into ONE long via Volatile.</summary>
public static class GpuAdapterInfo
{
    private static long s_adapterLuid;   // (HighPart << 32) | LowPart; 0 = no device yet

    internal static void Publish(LUID luid)
        => System.Threading.Volatile.Write(ref s_adapterLuid, ((long)luid.HighPart << 32) | luid.LowPart);

    public static bool TryGetAdapterLuid(out LUID luid)
    {
        long v = System.Threading.Volatile.Read(ref s_adapterLuid);
        luid = default;
        if (v == 0) return false;
        luid.LowPart = unchecked((uint)v);
        luid.HighPart = (int)(v >> 32);
        return true;
    }
}
```

---

## WS-A: HIGH_PERFORMANCE adapter selection + always-on `[d3d12.adapter]` line

All in `src/FluentGpu.Windows/D3D12/D3D12Device.cs`. Design choice: NO separate probe-create — attempt the real
`D3D12CreateDevice` per candidate and continue the loop on failure (one driver call, same terminal behavior). This
also satisfies pal-rhi.md §6:544 "next-best adapter on recovery": RecoverDevice re-runs InitDevice, the loop re-runs,
a dead adapter falls out naturally (document as such — no blacklist).

### A1. Replace the device-creation block (:824-836, `ID3D12Device* device = null;` … WARP branch)

```csharp
// ── Adapter selection (pal-rhi.md §3, as-built): IDXGIFactory6.EnumAdapterByGpuPreference walks adapters in
// HIGH_PERFORMANCE order (1803+). Cold path ⇒ raw TerraFX __uuidof QI per com-interop.md. Software adapters are
// SKIPPED (WARP is the explicit terminal fallback, not a "winner"); a candidate whose device create fails falls
// out of the loop — which is what makes the RecoverDevice re-run land on the next-best adapter when the previous
// one is truly gone (§6). Pre-1803 (Factory6 QI fails) keeps the historical null-adapter default; terminal ⇒ WARP.
ID3D12Device* device = null;
DXGI_ADAPTER_DESC1 chosenDesc = default;
bool haveDesc = false;
string selectionMode = "default";

IDXGIFactory6* f6 = null;
if ((int)_factory->QueryInterface(__uuidof<IDXGIFactory6>(), (void**)&f6) >= 0 && f6 != null)
{
    for (uint i = 0; ; i++)
    {
        IDXGIAdapter1* candidate = null;
        if ((int)f6->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                __uuidof<IDXGIAdapter1>(), (void**)&candidate) < 0 || candidate == null)
            break;   // DXGI_ERROR_NOT_FOUND — list exhausted
        DXGI_ADAPTER_DESC1 desc = default;
        bool usable = (int)candidate->GetDesc1(&desc) >= 0
                      && (desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) == 0;
        if (usable && (int)D3D12CreateDevice((IUnknown*)candidate, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                __uuidof<ID3D12Device>(), (void**)&device) >= 0 && device != null)
        {
            chosenDesc = desc; haveDesc = true; selectionMode = "high-performance";
            candidate->Release();
            break;
        }
        candidate->Release();
    }
    f6->Release();
}

if (device == null)
{
    HRESULT hr = D3D12CreateDevice(null, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device);
    if ((int)hr < 0)
    {
        // WARP fallback (VMs / RDP / no hardware GPU) — per pal-rhi.md §3b.1.
        IDXGIAdapter* warp;
        Check(_factory->EnumWarpAdapter(__uuidof<IDXGIAdapter>(), (void**)&warp), "EnumWarpAdapter");
        Check(D3D12CreateDevice((IUnknown*)warp, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device),
            "D3D12CreateDevice(WARP)");
        warp->Release();
        selectionMode = "warp";
    }
}
// BUG FIX (was WARP-sticky): assign on EVERY path. RecoverDevice re-runs InitDevice; a recovery that lands back
// on hardware must drop " (WARP)" — previously the suffix was only ever SET, never reset.
BackendNameSuffix = selectionMode == "warp" ? " (WARP)" : "";
_device = device;
SetName(_device, "FluentGpu.Device");
```

(The `#if DEBUG` info-queue block :838-867 stays where it is, after `SetName`.)

### A2. Replace the GpuProfile tier block (:869-882) — resolve identity, publish, log

```csharp
// Resolve the chosen adapter's identity by the DEVICE's own LUID — covers the default/WARP paths (which never
// held a DXGI_ADAPTER_DESC1) and is definitionally consistent with EnsureAdapter3's resolution.
LUID adapterLuid = _device->GetAdapterLuid();
if (!haveDesc)
{
    IDXGIAdapter1* byLuid = null;
    if ((int)_factory->EnumAdapterByLuid(adapterLuid, __uuidof<IDXGIAdapter1>(), (void**)&byLuid) >= 0 && byLuid != null)
    {
        haveDesc = (int)byLuid->GetDesc1(&chosenDesc) >= 0;
        byLuid->Release();
    }
}

// UMA ⇒ Weak; dedicated VRAM ⇒ Strong. Best-effort (failure ⇒ Unknown). Kept — but now LOGGED.
bool uma = false;
try
{
    D3D12_FEATURE_DATA_ARCHITECTURE arch = default;
    if ((int)_device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_ARCHITECTURE, &arch, (uint)sizeof(D3D12_FEATURE_DATA_ARCHITECTURE)) >= 0)
    {
        uma = arch.UMA != 0;
        FluentGpu.Foundation.GpuProfile.Tier = uma
            ? FluentGpu.Foundation.GpuPowerTier.Weak
            : FluentGpu.Foundation.GpuPowerTier.Strong;
    }
}
catch { /* detection is best-effort; Unknown ⇒ balanced default */ }

bool software = selectionMode == "warp"
    || (haveDesc && (chosenDesc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0);
string adapterName = haveDesc ? AdapterDescription(ref chosenDesc)
                   : selectionMode == "warp" ? "Microsoft Basic Render Driver (WARP)" : "<unknown>";
FluentGpu.Foundation.GpuProfile.AdapterName = adapterName;
FluentGpu.Foundation.GpuProfile.IsSoftwareAdapter = software;
GpuAdapterInfo.Publish(adapterLuid);   // sibling device creators (D3D11 video decode) pin to this GPU

// ALWAYS-ON identity line (one per device init/recovery — the Release-build evidence of WHICH GPU ran).
FluentGpu.Foundation.Diag.Line(
    $"[d3d12.adapter] mode={selectionMode} desc=\"{adapterName}\"" +
    $" vendorId=0x{chosenDesc.VendorId:X4} deviceId=0x{chosenDesc.DeviceId:X4}" +
    $" vramMB={(long)(chosenDesc.DedicatedVideoMemory / (1024 * 1024))} sharedMB={(long)(chosenDesc.SharedSystemMemory / (1024 * 1024))}" +
    $" luid=0x{adapterLuid.HighPart:X8}:{adapterLuid.LowPart:X8} uma={uma} software={software}" +
    $" tier={FluentGpu.Foundation.GpuProfile.Tier}");
```

### A3. Helper (near `SetName`, ~:590)

```csharp
// DXGI_ADAPTER_DESC1.Description is WCHAR[128] — in TerraFX 10.0.26100.6 an [InlineArray(128)] char buffer
// (verified against the pinned package source), which converts implicitly to ReadOnlySpan<char>.
private static string AdapterDescription(ref DXGI_ADAPTER_DESC1 desc)
{
    ReadOnlySpan<char> s = desc.Description;
    int n = s.IndexOf('\0');
    return new string(n >= 0 ? s[..n] : s);
}
```

Existing usings suffice. `EnsureAdapter3`/`RecoverDevice` need NO change. TerraFX typings VERIFIED against the pinned
v10.0.26100.6 source: `Flags` is `uint` (the `(uint)DXGI_ADAPTER_FLAG` casts in A1/A2 are correct as written);
`EnumAdapterByGpuPreference(uint, DXGI_GPU_PREFERENCE, Guid*, void**)` matches A1; `LUID` = `{ uint LowPart, int
HighPart }` matches GpuAdapterInfo.

---

## WS-B: Present topology + DWM glitch telemetry (`D3D12Device.cs`)

No seam change (PresentStats/IGpuDevice untouched — headless unaffected by construction). No WM_DISPLAYCHANGE
handler: a 1 Hz re-check with a cached HMONITOR early-out covers monitor moves at zero steady-state DXGI cost.
`D3D12Swapchain.Hwnd` exists (:4626).

### B1. `D3D12Swapchain` fields (after `Disposed`, :4645)

```csharp
// ── Present-topology attribution (WS-B) ── cached per swapchain so [d3d12.present] emits on CHANGE only.
internal HMONITOR TopologyMonitor;     // monitor at last check (NULL = never checked)
internal int PresentTopologyState;     // D3D12Device.Topology* — 0 unknown / 1 owned / 2 cross-adapter / 3 no-outputs
```

### B2. `SamplePresentTopology` + constants (after `SamplePresentStats`, ~:3960)

```csharp
// Does the RENDER adapter own a DXGI output containing the window's monitor? When not, every present crosses
// adapters (DWM cross-adapter scan-out): correct output, but an extra compositor copy + latency — the classic
// "high FPS, bad feel" confound this line names. A dGPU with ZERO outputs presenting through the iGPU is the
// designed-in shape of a hybrid laptop: a CONDITION to record, not an error. Runs at InitSwapChain and inside
// the existing 1 Hz DWM branch only; HMONITOR early-out ⇒ steady state is one user32 call/s, zero DXGI calls.
// Emits [d3d12.present] on a state CHANGE only — never per present.
internal const int TopologyUnknown = 0, TopologyOwned = 1, TopologyCross = 2, TopologyNoOutputs = 3;
private const uint MonitorDefaultToNearest = 2;   // MONITOR_DEFAULTTONEAREST — same local-const shape as Win32Platform.cs:380

private void SamplePresentTopology(D3D12Swapchain target)
{
    if (_device == null || _factory == null || target.Hwnd == HWND.NULL) return;
    HMONITOR mon = MonitorFromWindow(target.Hwnd, MonitorDefaultToNearest);
    if (mon == HMONITOR.NULL) return;
    if (mon == target.TopologyMonitor && target.PresentTopologyState != TopologyUnknown) return;   // steady state

    LUID luid = _device->GetAdapterLuid();
    IDXGIAdapter1* adapter = null;
    if ((int)_factory->EnumAdapterByLuid(luid, __uuidof<IDXGIAdapter1>(), (void**)&adapter) < 0 || adapter == null)
        return;   // stale factory mid-topology-change: keep old state; the next 1 Hz sample retries
    bool sawOutput = false, owns = false;
    for (uint i = 0; ; i++)
    {
        IDXGIOutput* output = null;
        if ((int)adapter->EnumOutputs(i, &output) < 0 || output == null) break;
        DXGI_OUTPUT_DESC od = default;
        if ((int)output->GetDesc(&od) >= 0)
        {
            sawOutput = true;
            if (od.Monitor == mon) owns = true;
        }
        output->Release();
        if (owns) break;
    }
    adapter->Release();

    int state = owns ? TopologyOwned : sawOutput ? TopologyCross : TopologyNoOutputs;
    target.TopologyMonitor = mon;
    if (state == target.PresentTopologyState) return;
    target.PresentTopologyState = state;
    Diag.Line($"[d3d12.present] topology={(state == TopologyOwned ? "render-adapter-owns-output"
            : state == TopologyCross ? "cross-adapter" : "render-adapter-has-no-outputs")}" +
        $" hwnd=0x{(nint)target.Hwnd:X}" +
        (state == TopologyOwned
            ? " note=direct-scan-out-path-available"
            : " note=presents-cross-adapters-via-DWM-(expected-on-hybrid-laptops;-adds-a-compositor-copy)"));
}
```

### B3. Hooks

- `InitSwapChain`, after `target.FrameIndex = target.SwapChain->GetCurrentBackBufferIndex();` (:992):
  `SamplePresentTopology(target);`
- `SamplePresentStats` 1 Hz branch, after `_dwmBaselined = true;` (:3936):
  `if (_primarySwapchain is { } psc) SamplePresentTopology(psc);` and `MaybeReportGlitches(now);`

### B4. Always-on glitch counters + minute line

Fields (with present-stats fields, after :3892):

```csharp
// Always-on PLAIN counters (deliberately NOT Diag.Count — Diag.* is [Conditional] and compiles out of Release,
// Diag.cs:64-84; budgets.md "always-on plain counter" precedent). Accumulated ONLY in the 1 Hz DWM branch
// (fresh deltas) ⇒ per-present cost zero; nothing allocates outside the 1 Hz / 60 s branches.
private ulong _glitchDroppedTotal, _glitchMissedTotal, _glitchLateTotal;
private long _glitchSampledSeconds;   // 1 Hz samples folded in ≈ seconds of PRESENTED time observed
private long _lastGlitchReportQpc;
private ulong _glitchDroppedAtReport, _glitchMissedAtReport, _glitchLateAtReport;
private long _glitchSecondsAtReport;
```

In the 1 Hz branch after the delta computations (:3928-3930): `_glitchDroppedTotal += dropped; _glitchMissedTotal += missed;
_glitchLateTotal += late; _glitchSampledSeconds++;`

```csharp
// Once per minute, ONLY when nonzero: silence is the healthy steady state. Normalized to PRESENTED time —
// skip-submit idle frames never reach SamplePresentStats, so "sampled" seconds are the honest denominator.
private void MaybeReportGlitches(long nowQpc)
{
    if (_lastGlitchReportQpc == 0) { _lastGlitchReportQpc = nowQpc; return; }
    if (nowQpc - _lastGlitchReportQpc < 60 * System.Diagnostics.Stopwatch.Frequency) return;
    _lastGlitchReportQpc = nowQpc;
    ulong d = _glitchDroppedTotal - _glitchDroppedAtReport;
    ulong m = _glitchMissedTotal - _glitchMissedAtReport;
    ulong l = _glitchLateTotal - _glitchLateAtReport;
    long s = _glitchSampledSeconds - _glitchSecondsAtReport;
    _glitchDroppedAtReport = _glitchDroppedTotal; _glitchMissedAtReport = _glitchMissedTotal;
    _glitchLateAtReport = _glitchLateTotal; _glitchSecondsAtReport = _glitchSampledSeconds;
    if (d == 0 && m == 0 && l == 0) return;
    Diag.Line($"[d3d12.present] dwmGlitches dropped={d} missed={m} late={l} presentedSeconds={s}" +
              " note=main-monitor-global-counters;-sampled-only-while-presenting");
}
```

### B5. Device-lost dump (in `DumpDeviceLostDiagnostics`, :681, after the existing `[d3d12] device-lost` line)

```csharp
write($"[d3d12] present dwmGlitchTotals dropped={_glitchDroppedTotal} missed={_glitchMissedTotal} late={_glitchLateTotal}" +
      $" sampledSeconds={_glitchSampledSeconds} topology={(_primarySwapchain?.PresentTopologyState ?? TopologyUnknown)}" +
      " (main-monitor-global; sampled only while presenting)");
```

---

## WS-C: FRAME_COUNT 3 + SetMaximumFrameLatency(2), everywhere

### C1. Core constants — `D3D12Device.cs:24-26`

```csharp
// Back buffers == per-frame command allocators == CPU-written GPU banks (pipelines' instance uploads, compositor
// SRV banks, query banks). 3 buffers + SetMaximumFrameLatency(2) buy ONE frame of CPU/GPU pipelining slack so a
// frame that costs slightly over one refresh stops quantizing to half rate at 144/165 Hz; the extra latency is
// bounded at one refresh and only materializes under backpressure (see threading-render-seam.md §latency).
// HISTORY: a working-tree triple-buffering EXPERIMENT (never landed — the const was never 3 in any commit)
// correlated with a DXGI_ERROR_DEVICE_HUNG on the Adreno after ~6.5 min of then-UNTHROTTLED image-upload bursts;
// verdict circumstantial (docs/plans/gpu-robustness-implementation.md §Adreno). The DecodeScheduler scroll-time
// upload throttle (the actual burst bound) landed since and STAYS ON; async device-lost recovery (also landed
// since) turns any recurrence into a logged reset, not a dead app. NOT every bank keys off this constant
// automatically: the formerly parity-banked
// compositors (Acrylic/OpacityLayer/BakedBlur) index by frameIndex % FRAME_COUNT and size heaps from FrameBankDepth —
// FrameBankingTests (FluentGpu.Windows.Tests) asserts the derived values so the depths cannot drift apart.
internal const uint FRAME_COUNT = 3;
internal const int FrameBankDepth = (int)FRAME_COUNT;       // CPU-written per-frame bank depth
internal const uint MAX_FRAME_LATENCY = FRAME_COUNT - 1;    // DXGI SetMaximumFrameLatency argument (= 2)
```

Free sites (already key off FRAME_COUNT — no edit): :37-39, :892, :931/:1067, :957 (switch to `MAX_FRAME_LATENCY`),
:963-:1176, :4020-4183, :4419/:4429, :4480-4612, :4630.

Parity sites in D3D12Device.cs: :2866 `(_frameIndex & 1)` → `(_frameIndex % FRAME_COUNT)` (rename parity→bank through
:2922/:2930); same at :2964, :2978, :2984, :2988; comment :2955. Baker :1228 already passes full `_frameIndex`.
New member near :4004: `public int MaxFrameLatency => (int)MAX_FRAME_LATENCY;`

### C2. Eight pipelines — one line each

RoundRectPipeline.cs:64, ShadowPipeline.cs:29, ArcPipeline.cs:30, PolylineStrokePipeline.cs:25, GradientPipeline.cs:35,
ImagePipeline.cs:38, PathPipeline.cs:66, GlyphRenderer.cs:222:

```csharp
private const int FrameCount = D3D12Device.FrameBankDepth;   // compile-time alias — zero drift, bodies untouched
```

Ring math `((frameIndex % FrameCount) + FrameCount) % FrameCount` is depth-agnostic. Update "double-buffered" comments
to "banked per frame-in-flight (depth = D3D12Device.FrameBankDepth)".

### C3. AcrylicCompositor.cs — parity → bank

`_parity` → `_bank`; heap :270 `sh.NumDescriptors = 1 + (uint)(D3D12Device.FrameBankDepth * MaxPool);` (via the new
static `SrvHeapDescriptorCount`); :437 `PoolSrvSlot(int i) => 1 + _bank * MaxPool + i;`; :487-489/:631-633/:653-656
`_bank = bank % D3D12Device.FrameBankDepth;`. RTV heap :262 unchanged. Acrylic rewrites slot SRVs on every acquire
(:538/:594/:798) — bank-correct at any depth.

### C4. OpacityLayerCompositor.cs — two-field → array

PoolEntry tail :76-77 → `public fixed ulong SrvResByBank[D3D12Device.FrameBankDepth];` (cleared by existing `= default`).
Fields: `_timestampPending = new bool[D3D12Device.FrameBankDepth]`, `private int _bank;`
InitTimestamps: `qd.Count = 2u * D3D12Device.FRAME_COUNT;` readback width `2UL * FRAME_COUNT * sizeof(ulong)` (track :488).

```csharp
private void CollectGpuTime(int bank)
{
    if (!_timestampPending[bank] || _timestampData == null || _timestampFrequency == 0) return;
    int query = bank * 2;
    ulong begin = _timestampData[query], end = _timestampData[query + 1];
    _timestampPending[bank] = false;
    if (end >= begin) LastBlurGpuMs = (end - begin) * 1000.0 / _timestampFrequency;
}
// BuildHeaps: sh.NumDescriptors = (uint)(D3D12Device.FrameBankDepth * MaxPool);
private void EnsureBankSrv(ref PoolEntry e, int slot)   // renamed from EnsureParitySrv; callers :880, :1467, FindPin
{
    if (e.SrvResByBank[_bank] == (ulong)e.Res) return;
    CreateSrv(e.Res, SrvCpu(PoolSrvSlot(slot)));
    e.SrvResByBank[_bank] = (ulong)e.Res;
}
private int PoolSrvSlot(int i) => _bank * MaxPool + i;
public void BeginFrame(ulong completedFence, int bank)
{
    _bank = bank % D3D12Device.FrameBankDepth;
    CollectGpuTime(_bank);
    // ...rest unchanged
}
// BeginBlurTiming/EndFrame: query index (uint)(_bank * 2); _timestampPending[_bank] = true;
```

### C5. BakedBlurCompositor.cs — scratch generalization

```csharp
private const int Banks = D3D12Device.FrameBankDepth;
private readonly bool[] _timestampPending = new bool[Banks];
// Per-bank ping-pong scratch pair. Bank b owns SRV slots {b*3=source, b*3+1=A, b*3+2=B} and RTV slots {b*3=A, b*3+1=B}.
private readonly ID3D12Resource*[] _scratchA = new ID3D12Resource*[Banks];
private readonly ID3D12Resource*[] _scratchB = new ID3D12Resource*[Banks];
private readonly D3D12_RESOURCE_STATES[] _stateA = new D3D12_RESOURCE_STATES[Banks];
private readonly D3D12_RESOURCE_STATES[] _stateB = new D3D12_RESOURCE_STATES[Banks];
```

Init: loop `b in 0..Banks`: CreateTarget `ScratchA{b}`/`B{b}`, states = PIXEL_SHADER_RESOURCE,
`CreateSrv(A, SrvCpu(b*3+1)); CreateSrv(B, SrvCpu(b*3+2)); CreateRenderTargetView(A, Rtv(b*3)); (B, Rtv(b*3+1));`
DrainOne (:99-105): `int bank = frameIndex % Banks, slot = bank * 3;` scratch/state by `[bank]`; `int query = bank * 2;`
(RecordCopy/RecordBlur `Rtv(slot)`/`Rtv(slot+1)` calls stay verbatim.) Heaps: `sh = 3 * Banks; rh = 3 * Banks` (keeps
`Rtv(slot)` arithmetic verbatim at 1 unused slot/bank). InitTimestamps: `qd.Count = 2u * (uint)Banks;` readback
`2UL * Banks * sizeof(ulong)`. CollectGpuTime: `int bank = frameIndex % Banks;` Dispose: loop the arrays.

### C6. GlyphRenderer.cs — bank `_texUpload` (live race at latency 2)

`WaitForFrame` only proves frame N−FRAME_COUNT retired, never N−1 — consecutive dirty-atlas frames race on the single
staging buffer. Latent at latency 1; live at latency 2.

```csharp
private readonly ID3D12Resource*[] _texUpload = new ID3D12Resource*[FrameCount];   // :170
// Init :1172: per-bank CreateUpload($"Glyph.AtlasUpload[{f}]"); UploadIfDirty :1197: var up = _texUpload[_active];
// Copy is full-atlas re-recorded per dirty frame — other banks' staleness irrelevant; _atlasDirty semantics unchanged.
// Dispose :1472: loop. Cost: +2 × 1 MiB (ATLAS=1024, R8).
```

### C7. Engine seam plumbing

`src/FluentGpu.Engine/Seams/Rhi/Rhi.cs` (in IGpuDevice, near :89):

```csharp
/// <summary>How many completed presents the swapchain may queue before frame production blocks (DXGI
/// SetMaximumFrameLatency). Pacing predicts the presented vblank as FrameQpc + (1 + MaxFrameLatency)·refresh
/// (RefreshLattice.Build). Default 1 — the classic latency-1 contract; HeadlessGpuDevice keeps it so the
/// deterministic gates keep PresentQpc = FrameQpc + 2·refresh. D3D12 overrides with FRAME_COUNT − 1 (= 2).</summary>
int MaxFrameLatency => 1;
```

`RefreshLattice.Build` (:24-36): add `int maxFrameLatency = 1` param;
`presentQpc = frameQpc + (1 + Math.Clamp(maxFrameLatency, 1, 3)) * refresh;` — L=1 reproduces the historical "+2".
Stale-tick window stays `refreshQpc * 2` (parked-clock heuristic, NOT latency-derived — comment says so). Update the
XML doc (:21-22).

`AppHost.cs`: cache `_maxFrameLatency = device.MaxFrameLatency;` in ctor; pass at the :2584 Build call.
`ScrollClock.PresentSec` (:2971) flows the new prediction (intended); DM lead clamp `[0, refreshMs]`
(Win32DirectManipulation.cs:330-332) saturates identically — unaffected, confirmed.
`AppHost.cs:1123` `GpuGovernorMaxSubmitAge = 6` → `7` (comment: FRAME_COUNT=3 resolve lag + slack).
`AppHost.cs:3316` + `DecodeScheduler.cs:69-77` comment rewrites: throttle stays as the retained Adreno mitigation;
drop "OFF-LIMITS"; fix "max-latency-1 couples the UI thread" → "the frame-latency waitable couples production to
present retirement". `QuarantinePolicy.RenderInFlightDepth = 1` — UNTOUCHED (CPU publish/consume seam, different axis).

### C8. Gates & regression guard

ScrollSuite `gate.pace.frame-clock-from-tick` (:2933-2955): existing asserts compile unchanged (default L=1, still
`+2*refresh`). Extend:

```csharp
var lat2 = FluentGpu.Hosting.RefreshLattice.Build(true, tick, refresh, tick + refresh / 4, 0, 9, maxFrameLatency: 2);
bool latScales = lat2.PresentQpc == tick + 3 * refresh && lat2.FrameQpc == tick
    && FluentGpu.Hosting.RefreshLattice.Build(true, tick, refresh, tick + refresh / 4, 0, 10, maxFrameLatency: 1).PresentQpc == tick + 2 * refresh;
```

New `src/FluentGpu.Windows.Tests/FrameBankingTests.cs` (InternalsVisibleTo confirmed; VERIFIED: none of the three
compositors declares a constructor — field-initializer-only, so headless `new` is safe; NO source-text asserts). Internal accessors: `TimestampBankCount` (Opacity +
BakedBlur), `AcrylicCompositor.SrvHeapDescriptorCount` (static, used by BuildHeaps), `BakedBlurCompositor.ScratchBankCount`.

```csharp
[Fact]
public void FrameBanksMatchFrameCount()
{
    Assert.Equal(3u, D3D12Device.FRAME_COUNT);
    Assert.Equal(D3D12Device.FRAME_COUNT - 1, D3D12Device.MAX_FRAME_LATENCY);
    Assert.Equal((int)D3D12Device.FRAME_COUNT, D3D12Device.FrameBankDepth);
    Assert.Equal(D3D12Device.FrameBankDepth, new OpacityLayerCompositor().TimestampBankCount);
    Assert.Equal(1 + D3D12Device.FrameBankDepth * 12, AcrylicCompositor.SrvHeapDescriptorCount);   // MaxPool = 12
    var baked = new BakedBlurCompositor();
    Assert.Equal(D3D12Device.FrameBankDepth, baked.ScratchBankCount);
    Assert.Equal(D3D12Device.FrameBankDepth, baked.TimestampBankCount);
    Assert.Equal(1, new HeadlessGpuDevice().MaxFrameLatency);   // headless keeps the +2·refresh gate contract
}
```

### Latency accounting (why this doesn't reintroduce input lag)

Pipeline keeping up ⇒ production paced by the engine's one-frame-per-tick phase gate, present queue never reaches
depth 2, presentation stays N+2, added latency **zero**. The second slot fills only under backpressure (frame cost
≳ 1 refresh), adding ≤ one refresh (6.9 ms @144) on exactly the frames that previously paid a full extra refresh +
cadence judder. DM lead clamp caps prediction influence at one refresh.

---

## WS-D: Video decode on the chosen adapter — `src/FluentGpu.Windows/Media/VideoMediaEngine.cs`

Replace `CreateD3D11AndManager` :195-204 (through `if (ctx != null) ctx->Release();`):

```csharp
ID3D11DeviceContext* ctx = null;
bool noVidSup = Environment.GetEnvironmentVariable("FG_VIDEO_NOVIDSUP") == "1";
uint flags = (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;
if (!noVidSup) flags |= (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;

// Land decode on the SAME adapter the D3D12 renderer chose (GpuAdapterInfo, published at device init). On a
// hybrid machine the D3D11 default adapter can be the OTHER GPU. No texture sharing (DComp surface handle only) —
// this is decode/present locality, not interop correctness. Cold path on the MTA engine thread. Fallbacks: LUID
// unset or enum failure ⇒ the historical default-adapter path, unchanged.
IDXGIAdapter1* adapter = null;
if (FluentGpu.Rhi.D3D12.GpuAdapterInfo.TryGetAdapterLuid(out LUID renderLuid))
{
    IDXGIFactory4* factory = null;
    if ((int)CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) >= 0 && factory != null)
    {
        if ((int)factory->EnumAdapterByLuid(renderLuid, __uuidof<IDXGIAdapter1>(), (void**)&adapter) < 0)
            adapter = null;
        factory->Release();
    }
}

// Explicit adapter REQUIRES D3D_DRIVER_TYPE_UNKNOWN (HARDWARE + adapter is E_INVALIDARG).
ID3D11Device* d3d = null;
int hr = D3D11CreateDevice((IDXGIAdapter*)adapter,
                           adapter != null ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                           HMODULE.NULL, flags, null, 0, 7 /*D3D11_SDK_VERSION*/, &d3d, null, &ctx);
if (adapter != null && (hr < 0 || d3d == null))
{
    // Pinned adapter refused a D3D11 device (driver quirk / feature gap): fall back rather than fail video —
    // decode on the wrong GPU beats no decode.
    Diag.Line($"[video.d3d11] adapter-pinned D3D11CreateDevice failed hr=0x{(uint)hr:X8}; falling back to default adapter");
    hr = D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.NULL, flags,
                           null, 0, 7 /*D3D11_SDK_VERSION*/, &d3d, null, &ctx);
    adapter->Release(); adapter = null;
}
bool pinned = adapter != null;
if (adapter != null) adapter->Release();
if (hr < 0 || d3d == null) return Log("D3D11CreateDevice", hr);
Diag.Line($"[video.d3d11] decodeAdapter={(pinned ? $"pinned-to-render-luid 0x{renderLuid.HighPart:X8}:{renderLuid.LowPart:X8}" : "default")}");
_d3d = d3d;
if (ctx != null) ctx->Release();
```

Rest of the method (multithread-protect, MFCreateDXGIDeviceManager, ResetDevice) unchanged. Known limitation (record
in docs): if RecoverDevice lands on a different adapter, a running video engine keeps its old D3D11 device; the next
playback session pins correctly.

---

## Docs reconciliation (then `powershell -File docs\design\check-canon.ps1` → exit 0)

1. `docs/site/engine-contributors/windows-backends.md` :95-99 — as-built is now the Factory6 HIGH_PERFORMANCE pick
   (skip software → null-adapter pre-1803 → WARP); mention `[d3d12.adapter]`/`[d3d12.present]` lines + non-sticky
   suffix fix. :101-123 — drop the false "one constant / one-line change" claim; FRAME_COUNT=3, MAX_FRAME_LATENCY=2,
   compositor banks index `% FRAME_COUNT`, guarded by FrameBankingTests; update literal "BufferCount = 2".
2. `docs/design/subsystems/pal-rhi.md` §3:348 — append "(as-built 2026-08)". §6:544 — "next-best adapter" = the
   selection loop re-runs; a dead adapter falls out naturally (no blacklist). :462 — `BufferCount=3`.
3. `docs/design/budgets.md` :44 — back-buffers row: 3 (FLIP_DISCARD), latency 2; cost: +1 back buffer
   (~8.3 MB @1080p / 14.7 @1440p / 33 @4K per swapchain) + ~5 MB wider banks. Two new rows: adapter selection +
   identity line (one-time, zero steady-state); topology + glitch counters (always-on plain counters per :32
   precedent; 1 user32 call/s steady state; stderr on change / minute-when-nonzero).
4. `docs/design/subsystems/gpu-renderer.md` :1336 — OQ-8: frames-in-flight = 3.
5. `docs/design/subsystems/threading-render-seam.md` :704-707 — `SetMaximumFrameLatency(2)`; waitable blocks only at
   two queued presents; the publish-seam invariant is unchanged (lives on the engine seam, not DXGI queue depth).
6. `docs/design/macos-debt-ledger.md` :41 — flip IDXGIFactory6 status to as-built (Metal column unchanged).
7. `docs/design/architecture-spec.md` :225 + `README.md` :167 — verified consistent already; no edit.
8. Copy this plan (final form) to `docs/plans/gpu-robustness-implementation.md`.

---

## Execution (parallel Opus subagents, disjoint files; ONLY I build/test/verify — never subagents; no git stash)

Pinned cross-agent contracts (agents must use these EXACT names — they compile against each other):
`D3D12Device.FRAME_COUNT = 3u` / `FrameBankDepth` / `MAX_FRAME_LATENCY` · `IGpuDevice.MaxFrameLatency => 1` ·
`RefreshLattice.Build(..., int maxFrameLatency = 1)` · `Diag.Line(string)` · `GpuProfile.AdapterName` /
`IsSoftwareAdapter` · `GpuAdapterInfo.Publish(LUID)` / `TryGetAdapterLuid(out LUID)` ·
compositor internals `TimestampBankCount`, `ScratchBankCount`, `AcrylicCompositor.SrvHeapDescriptorCount`.

All five agents run in parallel (files disjoint):

- **E1 (Engine):** Diag.cs (G1), GpuProfile.cs (G2), Rhi.cs (C7 member), RefreshLattice.cs (C7), AppHost.cs
  (WriteDeviceLostLine G1; `_maxFrameLatency` ctor + :2584; :1123 governor 7; :3316 comment), DecodeScheduler.cs
  (comment), ScrollSuite.cs (C8 gate extension).
- **W1 (Device):** D3D12Device.cs entire (A1-A3, B1-B5, C1 constants + parity sites + `MaxFrameLatency` override)
  + new GpuAdapterInfo.cs (G3).
- **W2 (Pipelines/compositors):** the 8 pipeline one-liners (C2), AcrylicCompositor.cs (C3),
  OpacityLayerCompositor.cs (C4), BakedBlurCompositor.cs (C5), GlyphRenderer.cs `_texUpload` banking (C6).
- **W3 (Video + tests):** VideoMediaEngine.cs (WS-D), new FluentGpu.Windows.Tests/FrameBankingTests.cs (C8).
- **D1 (Docs):** items 1-6 + 8 above.

Commit strategy (after my verification of the merged tree): commit 1 = engine seam + groundwork (zero behavior with
L=1 default), commit 2 = the atomic Windows change (adapter + telemetry + FRAME_COUNT 3 + banks + tests), commit 3 =
docs. A partial FRAME_COUNT landing is silent frame corruption — never split commit 2.

## Verification (orchestrator only, on the merged tree)

1. `dotnet build src/FluentGpu.slnx` AND `dotnet build src/FluentGpu.slnx -c Release` — both clean (diag-gate arms).
2. `dotnet run --project src/FluentGpu.VerticalSlice` → "ALL CHECKS PASSED" incl. the extended pace gate
   (baseline: 1202 gates, two known alloc-tripwire intermittents per memory).
3. `dotnet test src/FluentGpu.Windows.Tests` → FrameBankingTests + existing suite green.
4. `powershell -File docs\design\check-canon.ps1` → exit 0.
5. Gallery ON SCREEN (`--screenshot` captures black by design): exactly one `[d3d12.adapter]` boot line with
   plausible vendor/VRAM/LUID; one `[d3d12.present] topology=` line; no per-frame spam. Scroll long lists +
   acrylic/blur pages — no flicker/tear in acrylic pools, glyphs, baked blurs (the three touched bank classes).
6. FG_FPS_LOG spot-check: `latW` was ≈refresh every frame at latency 1 (waitable = metronome); at latency 2 expect
   `latW ≈ 0` while ahead, rising only under real backpressure.
7. Device-lost drill (existing injection hook): recovery re-emits `[d3d12.adapter]`; `backend=` no longer WARP-sticky;
   dump includes `dwmGlitchTotals`.
8. Video page: `[video.d3d11] decodeAdapter=pinned-to-render-luid …`; Task Manager per-engine shows decode on the
   render GPU.
9. Soak ≥10 min image-heavy scrolling (historical Adreno repro ~6.5 min) watching for device-removed.

## Adreno DEVICE_HUNG history — investigated: verdict CIRCUMSTANTIAL

Full git-history + docs sweep (plan-time evidence):

- **FRAME_COUNT was NEVER 3 in any landed commit.** `git log -S DXGI_ERROR_DEVICE_HUNG` finds exactly two hits, both
  COMMENT additions: e4b89431a (2026-06-21) attached the "Reverted from 3" comment to an already-`2` constant, and
  fd753a726 (2026-07-16, the touchpad-jitter campaign) added the DecodeScheduler "OFF-LIMITS" comment. The "3"
  experiment lived only on a working tree.
- **No captured evidence exists**: no DRED dump, no recorded device-removed reason, zero hits for
  Adreno/DEVICE_HUNG anywhere in docs/. One device, one occurrence class, no repro protocol.
- **The workload was itself the bug being fixed.** fd753a726's message: "upload bursts fed the double-buffer fence
  stall; the safe lever — triple-buffering previously hung the Adreno." The hang happened under then-UNTHROTTLED
  image-upload bursts; the plausible mechanism is deeper buffering letting more upload work pile in-flight on the
  Qualcomm WDDM driver until TDR — and the DecodeScheduler throttle (landed) bounds exactly that burst class at any
  buffer depth.
- **The blast radius has shrunk since**: async device-lost detection + recovery landed 2026-07-01 (1bb713791, AFTER
  the experiment). A recurrence today = an automatic recovery with an always-on `[device-lost]` line carrying the
  removal reason (0x887A0006) plus WS-B's `dwmGlitchTotals`/topology context — not a dead app, and fully diagnosable
  from a user's stderr log. No env switch involved anywhere.

**Conclusion:** correlation from a single uninstrumented working-tree run under a since-fixed pathological workload.
3-everywhere proceeds; the Adreno-class soak (verification step 9) is the honest check, and if a hang ever recurs in
the field the always-on evidence trail identifies it — the response would be diagnosing the driver interaction, not
re-forking buffer depth.

## Risks — resolved at plan time

1. **Adreno DEVICE_HUNG** → investigated above: circumstantial; throttle + recovery + always-on evidence + soak. Accepted.
2. **Missed parity site** → audit DONE: exhaustive `& 1` sweep over src/FluentGpu.Windows/D3D12/*.cs = 14 hits; every
   frame-parity hit is enumerated in WS-C (D3D12Device :2866/:2955/:2964/:2978/:2984/:2988; Acrylic :88/:489/:633/:656;
   Opacity :795; BakedBlur :99/:189); the one non-parity hit is AcrylicCompositor.cs:191 — HLSL vertex-corner math
   (`id & 1`), untouched. Implementer re-runs the same sweep after the change expecting only :191 to remain.
3. **TerraFX typing** → VERIFIED against pinned v10.0.26100.6 source: `Flags` uint; `Description` [InlineArray(128)]
   char (helper A3 written for it); `EnumAdapterByGpuPreference(uint, DXGI_GPU_PREFERENCE, Guid*, void**)`;
   `LUID {uint LowPart, int HighPart}`. No surprises left.
4. **OpacityLayerCompositor fixed-buffer conversion** → VERIFIED safe: every retire/evict/recreate path clears the
   whole entry (`e = default` / `_pool[…] = default` at :747, :787, :866, :1337, :1409, :1681, :1829), which zeroes
   the new `fixed ulong SrvResByBank[…]` exactly like the two fields it replaces. Keep the invariant comment.
5. **FrameBankingTests instantiation** → VERIFIED: no compositor declares a ctor; headless `new` in the test is safe.
6. **HIGH_PERFORMANCE on hybrid laptops** picks the dGPU ⇒ cross-adapter presents on iGPU-driven panels — the
   intended trade (decode+render locality beats iGPU rendering); WS-B names the condition on the diag line.
7. **VRAM growth** — inherent, quantified in the budgets.md row (accepted with the 3-buffer decision).
