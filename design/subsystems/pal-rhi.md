# FluentGpu — Subsystem Design: PAL + RHI + Windowing / Present

> **Owning subsystem:** the platform-abstraction layer (`FluentGpu.Pal` + `FluentGpu.Windows` Pal/), the
> render-hardware interface (`FluentGpu.Rhi` + `FluentGpu.Windows` D3D12/), and the windowing/present plumbing
> (Win32 window, DXGI flip-model swapchain, the multi-visual DirectComposition present-tree).
> This is the **swap line**: everything above it sees only POD descriptors, generational handles,
> spans, and an opaque `NativeHandle`; everything below it is OS/GPU-specific and lives in leaf
> assemblies referenced **only** by `Hosting`.
>
> **This doc is authoritative for** the PAL/RHI interface vocabulary, the Win32 reference impl, the
> D3D12 backend (device/queue/fence/heaps/D3D12MA, `SubmitDrawList`, `CopyBufferToTexture` + the
> texture-staging ring + the per-bucket texture pool), the DXGI+DComp present-tree, device-lost
> handling on the render thread, and the NEW PAL seams (`ISystemColors`, `IBackdropSource`,
> `IVideoPresenter`, `IVirtualMemory`, `IImageCodec`, the L10 cursor seam
> `IPlatformWindow.SetCursor`/`RegisterCustomCursor`, and `IPlatformLocale`). It **references, does not duplicate**, the
> shared contracts: threading (`hardened-v1-plan.md` §2/§4.1), COM (`dotnet10-csharp14-zero-alloc.md`
> §4 + `hardened-v1-plan.md` §4.2), memory/handles (`foundations.md`), scene/drawlist
> (`architecture-spec.md` §4.4/§4.5/§5.4), and the cross-subsystem frame loop (`architecture-spec.md`
> §4.8). Where this doc and a cross-cutting doc disagree, the cross-cutting doc wins and this doc is
> wrong; none are known to disagree (see CONTRADICTIONS at the end of the manifest).

---

## 0. Position in the stack & thread ownership (the single most important rule)

Per `hardened-v1-plan.md` §2.1: **the RENDER thread is the SOLE owner of every `ComPtr`, the
`RhiHandleTable`, the PSO cache, the upload/texture-staging rings, the GPU fence, the deferred-delete
ring, and the swapchain/DComp visuals.** The UI thread *touches zero COM, ever* — it cannot acquire a
backbuffer, map a buffer, or call `Present`. This is confinement-as-safety: refcount races are not
audited, they are structurally impossible because exactly one thread can `AddRef`/`Release`/`Dispose`.

Consequence for this subsystem:
- The **Win32 window + message pump runs on the UI thread** (`FluentGpu.Windows` Pal/). `WndProc` and DPI live
  on the UI thread because they are coupled to input and to the OS modal loops (`WM_ENTERSIZEMOVE`).
- The **D3D12 device, swapchain, DComp tree, and `Present` run on the RENDER thread** (`FluentGpu.Windows` D3D12/).
- The seam between them is the immutable `SceneFrame` snapshot (UI→render publish) and a small set of
  cross-thread single-aligned-word channels (device-lost reason, present-ack seq, resize request).
- `IVirtualMemory`, `IImageCodec`, `ISystemColors`, `IBackdropSource`, and `IVideoPresenter` placement
  vary by seam and are each pinned to a thread below (§9). `IImageCodec` runs on **worker threads**.

**Build-order note (`hardened-v1-plan.md` §6):** v1 ships single-thread-correct FIRST — the UI thread
both produces and consumes the snapshot (`QUARANTINE=0`), so "render thread owns ComPtr" is satisfied
trivially by there being one thread. The interfaces and ownership boundaries below are authored so the
render-thread split (build-order step 4) is a thread move, not a redesign.

```
        UI THREAD (FluentGpu.Windows/Pal/)             RENDER THREAD (FluentGpu.Windows/D3D12/)
 ┌───────────────────────────────┐            ┌──────────────────────────────────────────┐
 │ HWND · WndProc · PerMonitorV2  │  publish   │ ID3D12Device · DIRECT queue · ID3D12Fence │
 │ MsgWaitForMultipleObjectsEx    │  SceneFrame│ RTV/DSV/CBV-SRV-UAV heaps · D3D12MA       │
 │ InputEventRing (POD, drained)  │ ─────────► │ SubmitDrawList · UploadRing · StagingRing │
 │ ISystemColors / IBackdropSource│            │ per-bucket TexturePool · PSO cache        │
 │ (read OS state, no COM device) │ ◄───────── │ IDXGISwapChain3 · IDCompositionDevice     │
 └───────────────────────────────┘ present-ack │ IVideoPresenter (DComp child visuals)     │
                                    devlost-word└──────────────────────────────────────────┘
                                                          │  results-by-handle
                                                          ▼
                                                 WORKER POOL: IImageCodec (WIC) decode
```

Assemblies (per `architecture-spec.md` §3, `app-requirements-waveemusic.md` §5): interfaces in
`FluentGpu.Pal` and `FluentGpu.Rhi` (dep: `Foundation` only). Reference impls `FluentGpu.Windows` (Pal/ folder)
and `FluentGpu.Windows` (D3D12/ folder) are **leaves** (dep: their iface + `Foundation` + `FluentGpu.Windows` Interop/), referenced
**only by `Hosting`**. The WIC codec is a separate leaf `FluentGpu.Windows` (Wic/ folder) behind portable `IImageCodec`
(owned by `FluentGpu.Media` iface). `IVirtualMemory` is consumed by `Foundation`'s `ChunkedArena`.

---

## 1. PAL — platform / OS seam (`FluentGpu.Pal`, impl `FluentGpu.Windows` Pal/)

### 1.1 Core window / app / loop (folds `architecture-spec.md` §4.7; POD-only across the seam)

The one `object`-typed seam field is dead: present targets cross as the 8-byte POD `NativeHandle`.
Input crosses as a host-owned POD ring (`InputEventRing`), **never C# events** — the window
move-coalesces the >1 kHz `WM_INPUT`/`WM_POINTERUPDATE` flood and the host drains a
`ReadOnlySpan<InputEvent>` once per frame in phase 2. Zero delegate/closure allocation.

```csharp
namespace FluentGpu.Pal;

public enum NativeHandleKind : byte { None = 0, Hwnd = 1, NsView = 2, Headless = 3 }
public readonly struct NativeHandle              // 16B POD; the ONLY present-target representation
{ public readonly nint Value; public readonly NativeHandleKind Kind; }

public interface IPlatformApp : IDisposable
{
    IPlatformWindow CreateWindow(in WindowDesc d);
    IPlatformAppLoop CreateAppLoop();
    IClipboard Clipboard { get; }
    ISystemColors SystemColors { get; }          // NEW seam (§8.1) — process-lifetime singleton
    IBackdropSource Backdrop { get; }            // NEW seam (§8.2)
    Scale GetScaleForPoint(PointPx p);           // DPI for a virtual-desktop point (pre-window)
    void PostToUiThread(nint token);             // marshal a single aligned token (no closure)
}

public interface IPlatformWindow : IDisposable
{
    NativeHandle Handle { get; }                 // HWND on Win; NSView* on mac. Opaque to engine.
    Size2 ClientSizePx { get; }
    Scale Scale { get; }                         // effective post-WM_DPICHANGED DPI
    bool IsOccluded { get; }
    bool IsMinimized { get; }                     // window-visibility source for the component activation lifecycle:
                                                  //   AppHost derives the Activation.IsActive ambient from the minimized
                                                  //   state (reconciler-hooks.md §0bis). As-built the Win/headless PAL
                                                  //   exposes WindowState State; IsMinimized == State==Minimized.
                                                  //   Occlusion is deliberately NOT an activation input (not portable).
    int  PumpInto(ref InputEventRing ring);      // drain WM_*/NSEvent → InputEvent/WindowEvent POD
    void SetTitle(StringId t);
    void Show(); void Activate(); void RequestClose();
    void RequestFrame();                         // coalesced "produce a frame" (sets a dirty flag)
    void SetCursor(CursorId id);                 // L10 — apply the resolved cursor (Win32: SetCursor/LoadCursor,
                                                 //   re-asserted on WM_SETCURSOR). CursorId is the
                                                 //   InteractionInfo.CursorId column (scene-memory.md); not redefined here.
    void RegisterCustomCursor(CursorId id, ReadOnlySpan<byte> rgbaPremul,   // app cursor → CursorId range
                              Size2 sizePx, Point2 hotspot);
}
// L10 cursor seam: `Pal.Cocoa` mirrors `SetCursor`/`RegisterCustomCursor` via `NSCursor` (set()/addCursorRect).
// Consumed by input-a11y.md §17 (the CursorResolver walks the L2 hit route and calls SetCursor only on change).

public interface IPlatformAppLoop
{
    void RunBlocking(FrameCallback onFrame);     // one stable callback; no per-frame closure
    bool PumpOnce(out bool quit);
    void RequestFrame();
    void Post<TState>(TState s, Action<TState> cb);   // struct-state marshal to UI thread, no box
}

public delegate void FrameCallback(in FrameTime t);
public interface IClipboard { bool TryGetText(out string text); void SetText(ReadOnlySpan<char> t); }
public interface IImeSession { void SetCompositionRect(in RectPx caret); void Enable(bool on); }
```

`WindowEvent` (POD, in the same ring as `InputEvent`) carries `Resized`, `DpiChanged`, `Activated`,
`Occluded`, `ThemeChanged` (from `WM_SETTINGCHANGE("ImmersiveColorSet")` — drives `ISystemColors.Epoch`),
`CloseRequested`, and `DeviceLost` (mirrored from the render-thread word — see §6). The host reads these
in phase 1 (pump) and phase 2 (dispatch).

`IPlatformApp` also exposes `event Action<string>? ActivationRedirected` — the **inbound twin of `OpenUri`**
(the outbound launch seam). It fires when a *second* launch of a single-instance app is redirected to this
already-running instance, carrying that launch's activation payload (a deep-link URI like `wavee://callback?…`,
or the empty string for a focus-only relaunch). Unlike `WindowEvent`, the payload is a managed string, so it
rides this side channel rather than the POD ring. The contract is **UI-thread delivery**: the Win32 backend
raises it synchronously from a `WM_COPYDATA` case in its `WndProc` (the OS dispatches a sent message on the
window's own thread), so subscribers may touch non-thread-safe host state (`AppHost.WakeFrame`) directly — no
marshal hop. A cross-thread producer (a future toast-COM activator firing on an agile-COM thread) MUST
`PostMessage` to the window first. It is a default-interface-method event whose default never fires, so the
headless PAL stays test-neutral and only the Win32 backend opts in. The producer lives outside the PAL in
`FluentGpu.WindowsApi.Activation` (`SingleInstanceGate` sends the `WM_COPYDATA`; `ProtocolRegistrar` writes the
`HKCU` scheme keys); `AppHost` subscribes next to the `OpenUri` wiring, stashes the payload, wakes a frame, and
re-raises its own `AppHost.ActivationRedirected` to app code at the top of `Paint` (see input-a11y / hosting).

### 1.2 Win32 reference impl (`FluentGpu.Windows` Pal/) — UI thread

- **Window class:** `RegisterClassExW` once. Own redraw via DXGI/DComp, so `CS_HREDRAW|CS_VREDRAW`
  are **off** (no `WM_PAINT` storm on resize). `CreateWindowExW`.
- **DPI:** `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` programmatically (manifest-free, AOT-
  friendly). `Scale` = current effective DPI; `WM_DPICHANGED` updates it and emits a `DpiChanged`
  `WindowEvent`. PerMonitorV2 snaps a straddling window to a single value, so `Scale` is one scalar
  (kept a `struct` so a future `DpiX/DpiY` extension is non-breaking). **DIP→px happens once** at the
  pump boundary using the window's current effective DPI.
- **WndProc:** a single `static [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]` thunk in
  `WNDCLASSEXW.lpfnWndProc`. Per-window dispatch via a **`GCHandle` (Normal, lifetime = window) stored
  in `GWLP_USERDATA`** — set in `WM_NCCREATE` from the `CREATESTRUCT.lpCreateParams`, freed
  deterministically in `WM_NCDESTROY` (no `Weak`/null-target race). The thunk recovers the managed
  `Win32Window` via `GCHandle.FromIntPtr` and writes POD into the ring. No managed allocation per message.
- **Pump:** `MsgWaitForMultipleObjectsEx(handles:[latencyWaitable], QS_ALLINPUT, MWMO_INPUTAVAILABLE)`
  in single-thread v1 (wakes on the present latency waitable **or** an incoming message — keeps the
  pump responsive). On the render-thread split the UI thread waits only on input + the snapshot-consume
  signal; the latency waitable migrates to the render thread's present loop.
- **Modal loops:** during `WM_ENTERSIZEMOVE` the OS owns the pump; we render via a `WM_TIMER`/`WM_PAINT`
  tick. `WM_EXITSIZEMOVE` triggers the real resize (§5.3).
- **Flat C exports:** `[LibraryImport]` for `D3D12CreateDevice`, `CreateDXGIFactory2`,
  `DCompositionCreateDevice`, `DWriteCreateFactory`, `RegisterClassExW`, `CreateWindowExW`,
  `SetProcessDpiAwarenessContext`, `GetDpiForWindow` (blittable `nint`/`Guid*`/`void**` no-marshal
  stubs; IIDs passed as `in iid` over pinned `"…"u8`). `[assembly: DisableRuntimeMarshalling]` proves
  blittability (CA1421) — per `dotnet10-csharp14-zero-alloc.md` §1.

### 1.3 Cross-platform boundary (macOS)

`Pal.Cocoa` implements the SAME interfaces: `NSWindow`/`NSView`, `CVDisplayLink` frame callback,
`NSEvent`→`InputEvent` POD ring, `NativeHandle{Kind=NsView}`, `backingScaleFactor`→`Scale`. Nothing
above `Hosting` recompiles; the per-OS factory in `Hosting` selects leaves with `#if WINDOWS`.

---

## 2. RHI — render-hardware seam (`FluentGpu.Rhi`, impl `FluentGpu.Windows` D3D12/) — render thread

Graphics-first. **Zero COM/pointers cross the interface** — generational handles + POD descs + spans
only. `SubmitDrawList` is the PRIMARY hot path; the per-opcode `ICommandEncoder` is the secondary API.

```csharp
namespace FluentGpu.Rhi;

public interface IGpuDevice : IDisposable
{
    GpuDeviceCaps Caps { get; }                  // PreferredBackBufferFormat, SupportsTearing, MsaaModes,
                                                 //   max texture dim, UMA flag, RootSignature tier
    DeviceLostToken LostToken { get; }           // render thread reads HRESULT; word mirrored to UI

    ISwapchain     CreateSwapchain(in SwapchainDesc d);          // d.PresentTarget is NativeHandle (POD)
    ShaderModuleHandle CreateShaderModule(in ShaderModuleDesc d);// DXIL bytes / .metallib
    PipelineHandle CreatePipeline(in GraphicsPipelineDesc d);    // immutable PSO; hash-deduped
    BufferHandle   CreateBuffer(in BufferDesc d);
    TextureHandle  CreateTexture(in TextureDesc d);              // COLD only: startup pools, atlas, layer RTs
    SamplerHandle  CreateSampler(in SamplerDesc d);

    void Destroy(TextureHandle h); void Destroy(BufferHandle h); // gen-bump → deferred-delete ring

    ICommandEncoder BeginFrame(in FrameContext ctx);
    void Submit(ICommandEncoder enc);
    void SubmitDrawList(ReadOnlySpan<byte> drawList,             // ── PRIMARY hot path
                        ReadOnlySpan<ulong> sortKeys,
                        in FrameContext ctx);
    void WaitIdle();
}

public interface ISwapchain : IDisposable
{
    Size2 SizePx { get; }
    TextureHandle CurrentBackBuffer { get; }
    void Resize(Size2 px);
    PresentResult Present(in PresentParams p);                  // DXGI Present → DComp Commit
}

public interface ICommandEncoder
{
    void BeginRenderPass(in RenderPassDesc p); void EndRenderPass();
    void SetPipeline(PipelineHandle p);
    void SetViewportScissor(in RectPx vp, in RectPx scissor);
    void BindConstants(uint slot, ReadOnlySpan<byte> data);
    void BindBuffer(uint slot, BufferHandle b, uint off);
    void BindTexture(uint slot, TextureHandle t, SamplerHandle s);
    void DrawInstanced(uint vtxPerInst, uint instCount, uint baseVtx, uint baseInst);
    void DrawIndexedInstanced(uint idxCount, uint instCount, uint baseIdx, int baseVtx, uint baseInst);
    void Barrier(ReadOnlySpan<ResourceBarrier> b);
    void ResolveTexture(TextureHandle src, TextureHandle dst);  // MSAA → single-sampled, in linear
    void UpdateBuffer(BufferHandle b, uint dstOff, ReadOnlySpan<byte> src);   // BUFFER ONLY
    void CopyBufferToTexture(BufferHandle staging, TextureHandle dst,         // ── NEW (image/video poster)
                             in TextureRegion region);
}
```

### 2.1 POD descriptors (selected; all blittable, sequential)

```csharp
public enum NativeHandleKind : byte { ... }            // (Pal — shared shape)
public readonly struct DeviceLostToken { public readonly uint Reason; public bool IsLost => Reason != 0; }

public struct SwapchainDesc {                          // NO COM; ColorSpace + format split folded
    public NativeHandle PresentTarget;                 // Kind==Hwnd → DComp-for-composition path
    public Size2 SizePx; public uint BufferCount;      // 2 (FLIP_DISCARD)
    public TextureFormat BackBufferFormat;             // B8G8R8A8_UNORM (flip-model + DComp reject _SRGB)
    public TextureFormat RtvFormat;                    // B8G8R8A8_UNORM_SRGB (blend/resolve in linear)
    public AlphaMode AlphaMode;                         // Premultiplied (DComp requirement)
    public PresentFlags Flags;                          // FrameLatencyWaitable | AllowTearing
    public ColorSpace ColorSpace;                       // sRGB
}

public struct GraphicsPipelineDesc {                   // immutable PSO; hash → dedup in PSO cache
    public ShaderModuleHandle Vs, Ps;                  // DXIL blobs (embedded; no runtime compile)
    public BlendDesc Blend; public RasterizerDesc Raster;
    public InputLayoutDesc Input; public uint SampleCount; // back buffer always 1
    public TextureFormat[] RtvFormats; public RootSigClass RootSig;
}

public struct BufferDesc   { public BufferKind Kind; public uint SizeBytes; public HeapKind Heap; }
public struct TextureDesc  { public Size2 SizePx; public TextureFormat Format; public TextureUsage Usage; }
public struct TextureRegion{ public uint DstX, DstY, Width, Height, Mip; public uint SrcRowPitchBytes; }
public struct PresentParams{ public uint SyncInterval; public PresentFlags Flags; public bool CompositionDirty; }
public struct FrameContext { public ulong FrameIndex; public TextureHandle Target; public Size2 SizePx; }
```

### 2.2 Where it lands in the 13-phase loop (and on which thread)

Per `architecture-spec.md` §4.8 re-cut by `hardened-v1-plan.md` §2.2:

| Phase | RHI/PAL action | Thread |
|---|---|---|
| 1 pump | `IPlatformWindow.PumpInto(ring)`; **read device-lost word + present-ack seq** (single Volatile reads) | UI |
| 2 input dispatch | drain ring; `WindowEvent.Resized/DpiChanged/ThemeChanged/DeviceLost` consumed | UI |
| 10 submit | leaf walks POD opcodes (devirtualized) → `ID3D12GraphicsCommandList` → `ExecuteCommandLists` → `Signal(fence)` | RENDER |
| 11 present | wait latency waitable → `Present(SyncInterval, Flags)` → DComp `Commit` (only if composition dirty) → `Volatile.Write(present-ack)` | RENDER |
| 13 arena swap | drain deferred-delete ring behind retired fence; `StagingRing`/`UploadRing` reset behind fence | RENDER |

In single-thread v1 every row is the UI thread (one thread). Submit/present become the render thread at
build-order step 4. **The device-lost word + present-ack are the only render→UI channel** (single
aligned words, `Volatile`); the resize request is the only UI→render side channel besides `SceneFrame`.

---

## 3. D3D12 device, queues, fences, heaps, allocator (`FluentGpu.Windows` D3D12/)

Forked from ComputeSharp's compute-only `GraphicsDevice` (per `architecture-spec.md` §3b.3): keep
fence-wait, device-lost (`fence→MaxValue` + `RegisterWaitForSingleObject`), per-LUID device cache,
D3D12MA wiring, command-list pool. **Add** the DIRECT queue, RTV+DSV heaps, graphics command-list
recording, and graphics PSO path.

- **Device:** `D3D12CreateDevice(adapter, FL_11_0)` via `[LibraryImport]`. Adapter chosen via
  `IDXGIFactory6` high-performance enum, WARP fallback for VMs/RDP (and the headless test path).
- **Queue:** one DIRECT `ID3D12CommandQueue`. `ID3D12Fence` + an event; per-frame `Signal(++v)`.
- **Heaps:** RTV heap (back buffers + layer RTs), DSV heap (stencil-clip), one shader-visible
  CBV/SRV/UAV heap (atlas/image/glyph SRVs). All authored from scratch (not in the seed).
- **Allocator:** D3D12MA for textures/buffers we create (atlas pages, layer RTs, the per-bucket image
  textures, staging/upload heaps). **DXGI back buffers are NOT D3D12MA-managed** — they release through
  the swapchain and are special-cased in the deferred-delete ring.
- **PSO cache:** `GraphicsPipelineDesc` hashed → `ID3D12PipelineState` deduped. Native fixed set is
  pre-warmed at startup (SDF rect/rrect/shadow/glyph/image variants); Custom/effect/D2D-fallback blends
  warm lazily with a one-time hitch (`hardened-v1-plan.md` §4.3).
- **`HandleTable<TNative>`** (the one place COM ownership meets handles; `foundations.md` §1.2/§4.7):
  each handle → `{ ComPtr<...> Res, cached descriptors, current D3D12_RESOURCE_STATES }`. Uses the
  **separate `int[] _next` free-link array** (NOT the intrusive-first-4-bytes trick, which would stomp
  `ComPtr.ptr_` — `foundations.md` §4.2 BLOCKER). `Free` runs typed teardown (`slot.Res.Dispose()`)
  **before** reclaiming the slot. ALL of this is render-thread-confined.
- **COM bindings:** hot path (`ID3D12GraphicsCommandList`/`CommandQueue`/swapchain `Present`/DComp) =
  **GENERATED hand-vtable `calli`** from the harvested `*.comabi.json` (no human-typed slot;
  `dotnet10-csharp14-zero-alloc.md` §4 + `hardened-v1-plan.md` §4.2). DWrite **setup** / UIA / TSF / OLE
  = `[GeneratedComInterface]`/`[GeneratedComClass]` in `PlatformIntegration`. `ComPtr<T>` is
  render-thread-confined and **Move-only across the seam** (never shared by reference). No
  `System.Runtime.InteropServices.ComWrappers` on the hot path.

### 3.1 SubmitDrawList — the hot path (render thread)

`SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameContext ctx)` walks
the POD opcode stream (see `architecture-spec.md` §4.5 / `gpu-renderer.md` for the opcodes) with
**concrete, devirtualized, inlinable** types — no per-draw interface dispatch:

```
record into a pooled DIRECT command list:
  Barrier(backbuffer: PRESENT → RENDER_TARGET)
  BeginRenderPass(RTV = _UNORM_SRGB, clear: transparent premul-0 in DrawVideoCmd.Dst regions)  // hole-punch
  for each batch (radix-sorted by ulong sortKey):
      SetPipeline(PSO for batch.Kind)            // SDF rrect / shadow / glyph / image / video-hole
      BindConstants(viewport/transform CB)
      BindTexture(glyph atlas | image atlas | bucket texture)   // UVs resolved at batch time, never baked
      DrawInstanced(quad, batch.InstanceCount, ...)
  Barrier(backbuffer: RENDER_TARGET → PRESENT)
ExecuteCommandLists → queue.Signal(fence, ++v)
```

`DrawVideoCmd` is recorded with a sortkey `PassClass` ordering its transparent alpha-punch **below all
chrome**, so chrome composites over the hole (§7). The batcher's UV-resolve has glyph + **`ImageRef`**
branches (atlas UVs resolved at batch time, never baked into the command — keeps eviction transparent;
`app-requirements-waveemusic.md` §3.1).

---

## 4. Image / texture upload — `CopyBufferToTexture` + staging ring + per-bucket pool (NEW)

The RHI seam had **no** texture upload (`UpdateBuffer` is buffer-only). This is the single biggest
RHI gap WaveeMusic load-bears on. Three additions, all render-thread-owned:

### 4.1 `CopyBufferToTexture` (the encoder primitive)

```csharp
void CopyBufferToTexture(BufferHandle staging, TextureHandle dst, in TextureRegion region);
```
D3D12: `ID3D12GraphicsCommandList::CopyTextureRegion(dst=PlacedSubresourceFootprint, src=staging)` with
the row-pitch aligned to `D3D12_TEXTURE_DATA_PITCH_ALIGNMENT (256)`. Barriers
`COPY_DEST` → `PIXEL_SHADER_RESOURCE` are emitted around the copy. Metal: `MTLBlitCommandEncoder
copyFromBuffer:toTexture:`.

### 4.2 The texture-staging ring (separate from the instance `UploadRing`)

A dedicated, **MB-sized, fence-gated** ring of `UPLOAD`-heap `ID3D12Resource`s used **only** for
`CopyBufferToTexture` source bytes. It is **not** the instance/vertex/index `UploadRing` (which stays
per-frame-in-flight and serves the batcher). The decode worker writes CPU bytes into the staging ring's
mapped span; phase 13 drains copies; the ring slot is reclaimed when the in-flight fence that consumed
it retires. Sizing is explicit: `StagingBytes = maxUploadsPerFrame × maxBucketBytes × framesInFlight`.

```csharp
public sealed class TextureStagingRing {            // render-thread-private
    BufferHandle[] _blocks;                          // persistently-mapped UPLOAD heaps
    ulong[]        _retireFence;                      // per-block fence value gating reuse
    int            _head;
    Span<byte> Acquire(int bytes, out BufferHandle block, out uint offset); // bump; wraps behind fence
    void        Reset(ulong completedFence);          // phase 13, behind retired fence
}
```

### 4.3 The per-bucket texture pool (no `CreateTexture` in phases 6–13)

`CreateTexture` allocates a `HandleTable` slot **and** a `ComPtr` root — a managed allocation — so it
**must not run in the steady-state hot loop**. At startup, pre-allocate a pool of textures per decode
bucket (64/128/256/512 px, matching `ImageCacheService`). Phase 13 only does `CopyBufferToTexture` into
a **recycled** bucket texture + `ContentEpoch++` on the `ImageRealization`. `CreateTexture` runs only as
cold-path pool growth. Budget is stated: `Σ_bucket (bucket² × 4 bytes × pool-depth)` — so the phase-13
loop row is *honestly* 0-alloc.

```csharp
public sealed class TexturePool {                    // render-thread-private; per bucket index 0..3
    readonly Stack<TextureHandle>[] _free;            // pre-allocated at startup
    TextureHandle Rent(int bucket);                   // pop; grow (cold) if empty
    void          Return(TextureHandle h, int bucket);// on eviction, after deferred-delete fence retire
}
```

**13-phase placement (folds `app-requirements-waveemusic.md` §3.1):** P13 = byte-budgeted **two-lane**
`CopyBufferToTexture` drain (small thumbs vs large art/bakes) so a fling realizing ~30 thumbs fills in
1–2 frames while 512px bakes are rate-limited. P1 = `ResidencyManager` eviction sweep frees bucket
textures through the **deferred-delete ring keyed by in-flight fence** (§4.7 of the spec) and returns
them to the pool. The codec runs on workers (§9.5); the RHI never sees `string`/URI.

---

## 5. DXGI flip-model swapchain + the multi-visual DComp present-tree

### 5.1 Single-visual creation (the §5.1 base path)

On the render thread: `IDXGIFactory2.CreateSwapChainForComposition(queue, desc)` with
`B8G8R8A8_UNORM`, `BufferCount=2`, **`FLIP_DISCARD`** (preferred over `FLIP_SEQUENTIAL` for full-frame
UI), `PREMULTIPLIED`, `STRETCH`, `FRAME_LATENCY_WAITABLE | ALLOW_TEARING` → QI `IDXGISwapChain3`.
Back-buffer **RTVs created as `B8G8R8A8_UNORM_SRGB`** (RTV format independent of buffer format — folds
the flip-model/DComp sRGB BLOCKER; blend+resolve in linear, hardware sRGB-encodes on write, output
premultiplied — the COLOR contract). Then DComp: `DCompositionCreateDevice(&IDCompositionDesktopDevice)`
→ `CreateTargetForHwnd(HWND)` → `CreateVisual` → `visual.SetContent(swapchain)` → `target.SetRoot(visual)`
→ `Commit()`.

### 5.2 The multi-visual present-tree (NEW — for video; `app-requirements-waveemusic.md` §3.4)

The single opaque swapchain visual cannot composite externally-decoded video. The present-tree becomes
a **root visual with N children**:

```
target.Root = rootVisual
  ├─ videoChildVisual   (z-below)  ← IVideoPresenter surface (external MediaPlayer/PlayReady)
  └─ uiSwapchainVisual  (z-above)  ← our IDXGISwapChain3 content (PREMULTIPLIED)
```

**Transparency protocol (hole-punch):** in the `DrawVideoCmd.Dst` region the UI back buffer is cleared
**transparent, premultiplied-0**, so the video child shows through. Scrim, transport controls, and
rounded-PiP corners are normal DrawList quads in the topmost UI visual, composited over by z-order.
`DrawVideoCmd` (shape owned by `gpu-renderer.md` §3.1 — `{ VideoSurfaceId Surface; RectF Dst; ImageHandle
PosterBlur; ImageHandle AlbumArt; float VideoReady; CornerRadius4 Radii; ClipHandle Clip }`) carries its own
sortkey `PassClass` ordering the alpha-punch below all chrome. The art/poster lower layers + the 3-layer
crossfade (art→poster→live, 180/220ms) are phase-7 composition animations.

**Window Mica/Acrylic** is a *third* meaning of backdrop and is PAL, not our pixels (§8.2): the root
clears transparent (premul-0) and DWM/`IBackdropSource` composes Mica through a DComp backdrop **sibling
visual below** the swapchain visual. Zero renderer change.

### 5.3 Per-frame present (render thread, zero-alloc)

```
wait MsgWaitForMultipleObjectsEx(latencyWaitable, QS_ALLINPUT)   // single-thread v1; render-thread waits on
                                                                  // present-latency only post-split
backIdx = swapchain.GetCurrentBackBufferIndex()
SubmitDrawList(...)                                               // §3.1 — barriers + render pass + draws
Present(SyncInterval, Flags)                                      // DXGI_PRESENT_ALLOW_TEARING when unsynced
if (compositionDirty) dcompDevice.Commit()                       // ONLY when a visual prop changed
                                                                  //   (video Place, backdrop, resize)
Volatile.Write(ref _presentAckSeq, frameSeq)                     // phase-12 passive effects read this on UI
```

**Occlusion:** `Present(... DXGI_PRESENT_TEST)` throttled to ~1 Hz (marked needs-validation against
DComp present-test semantics; fall back to `WM_ACTIVATEAPP` if unreliable). When occluded/minimized,
skip submit+present entirely.

### 5.4 Resize (folds the device-wide-WaitIdle BLOCKER)

On `WM_EXITSIZEMOVE` (live drag) / `WM_SIZE` (programmatic), the UI thread posts a resize request word;
the render thread: **CPU-wait only the in-flight fence values that referenced THIS swapchain's back
buffers** (NOT device-wide `WaitIdle` — other windows on the shared device keep rendering) →
gen-bump back-buffer handles → `IDXGISwapChain3.ResizeBuffers` → re-`GetBuffer`/re-create RTVs →
DComp `Commit` reasserts size + re-`Place`s the video child. During live drag, present-stretch for
smoothness; snap on exit. DPI change without a client-size change does **not** resize the swapchain
(back buffer is physical px); it bumps `ConfigVersion` to force relayout.

### 5.5 Multi-window

One `IGpuDevice` (one device/queue/heaps/D3D12MA) shared; each window = one `ISwapchain` + a DComp
`Target`/`Visual` subtree. Per-window fence values, per-swapchain deferred-delete ring, per-swapchain
video-child set.

---

## 6. Device-lost — detected on the RENDER thread, marshaled to UI (folds the threading BLOCKER)

Two detectors, render-thread-resident:
1. **Primary:** the synchronous `Present()`/`Submit()`/`ExecuteCommandLists` HRESULT
   (`DXGI_ERROR_DEVICE_REMOVED`/`_RESET`/`_HUNG`), checked on the render thread immediately.
2. **Backstop:** the `RegisterWaitForSingleObject(fence→MaxValue)` callback runs on an **OS wait
   thread** (not UI, not render). It does **only** `Volatile.Write(ref _deviceLostReason, code)` +
   `IPlatformWindow.RequestFrame()` — it **never** touches a `ComPtr` or calls
   `GetDeviceRemovedReason`. Its `GCHandle` context is **Normal** (Hosting lifetime, freed
   deterministically on teardown — no `Weak`/null-target race).

**Marshal path:** the render thread, on detecting loss, sets `_deviceLostReason` and emits a
`DeviceLost` `WindowEvent` into the ring; the UI thread reads the word **once at phase 1** (a single
aligned `Volatile.Read`), pauses publishing, and rendezvouses with the render thread via the off-hot-path
`System.Threading.Lock` (the only place a real lock is used; `hardened-v1-plan.md` §7).

**Recovery (render thread, since it owns every ComPtr):** dispose device → recreate (next-best adapter /
WARP) → recreate per-window swapchains + DComp trees + video children → **re-realize all GPU resources
from retained CPU state** (SceneStore SoA untouched; BrushTable/ClipTable re-upload; GlyphAtlas
re-rasterize from GlyphRunTable; the per-bucket image textures re-decode/re-upload from the
`ImageCache`'s retained source or re-request; PSOs recompile from the cached DXIL blobs) → mark whole
tree dirty → resume publishing. **Invariant:** the RHI stores only *realizations*; every GPU object is
reconstructible from CPU-side retained data. The UI thread's `SceneStore` is the source of truth and is
never touched by a device-lost event.

---

## 7. NEW PAL seam: `IVideoPresenter` (DComp sibling visual; FluentGpu never touches video pixels)

```csharp
namespace FluentGpu.Pal;

public readonly struct VideoSurfaceId { public readonly uint Value; }   // POD; opaque to engine

public interface IVideoPresenter
{
    VideoSurfaceId CreateSurface(NativeHandle window);          // adds a DComp child visual below the swapchain
    void Place(VideoSurfaceId id, in RectPx deviceRect, float opacity, int z); // off-loop transform poke
    void SetVisible(VideoSurfaceId id, bool visible);
    void Destroy(VideoSurfaceId id);
    nint GetMediaPlayerSink(VideoSurfaceId id);                 // app binds its MediaPlayer/Element to this
}
```

`FluentGpu.Windows` Pal/ → DComp child visual whose content is the external `IDCompositionSurface`/swapchain the
app's `MediaPlayer` (PlayReady `MediaPlayerElement` / SpoutDx cross-process texture) renders into.
FluentGpu records only `DrawVideoCmd` (the hole-punch) and pokes `Place`. **The heaviest continuous work
is off our thread by construction** — this is the one surface where single-thread v1 is an advantage:
video composites on the OS compositor thread independent of our loop.

- `VideoSurfaceRegistry` (`FluentGpu.Media`, portable) arbitrates a single surface by priority
  (theatre > PiP > sidebar); atomic handoff = no black frame.
- **Two-clock tear fix:** PiP drag moves the DComp child via `Place` **with the canvas-RT hole committed
  in lockstep in the SAME phase-11 DComp `Commit`**. Partial present: re-punch the hole whenever any node
  overlapping the video rect is in the damage set (inflate the video node's damage to its own rect).
- macOS: `AVPlayerLayer`/`CALayer` sibling under the `CAMetalLayer`.

13-phase: P11 `IVideoPresenter.Place` + canvas hole committed in one DComp `Commit`; P8 emits the
hole-punch quads; no video pixel work, no relayout, ever on our thread.

---

## 8. NEW PAL seams: system colors, backdrop, virtual memory, image codec

### 8.1 `ISystemColors` (T1 live theming — UI thread read)

```csharp
public interface ISystemColors
{
    uint Epoch { get; }                          // bumped on WM_SETTINGCHANGE("ImmersiveColorSet")
    SystemColorSnapshot Snapshot { get; }        // fat (~112B) by-value: accent, HC palette, dark/light
    bool IsHighContrast { get; }
}
```
`FluentGpu.Windows` Pal/ reads `HKCU\…\Personalize` + `SPI_GETHIGHCONTRAST` + accent registry once at startup and
per `WM_SETTINGCHANGE`, bumping `Epoch`. The reactive context is **`Context<uint>` over `Epoch`**
(DepKey-projectable, boxless) — NOT `Context<SystemColorSnapshot>` (a 112B struct cannot project into a
16B `DepKey`; that would box). `UseSystemColors()` reads the fat snapshot by value from the stable
instance; the re-render trigger is the Epoch context change. Flow reuses the existing
`WM_SETTINGCHANGE → ThemeChanged WindowEvent → dirty-everything` path. HC bypasses the album-art
palette → `sys.HcHotlight`. macOS → `NSAppearance`/HC colors.

### 8.2 `IBackdropSource` (window Mica / Acrylic — PAL, not our pixels)

```csharp
public enum HostBackdropKind : byte { None, Mica, MicaAlt, Acrylic }
public interface IBackdropSource
{
    void SetWindowBackdrop(NativeHandle window, HostBackdropKind kind, ColorF tint);
}
```
`FluentGpu.Windows` Pal/ → `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` and/or a DComp backdrop **sibling
visual below** our swapchain visual. Our root clears transparent (premul-0); DWM composes Mica through.
HC → `None` + opaque fill. macOS → `NSVisualEffectView`. **In-app Acrylic** (toast/add-to-playlist over
content) is NOT this seam — it is a renderer two-pass FrameGraph step (snapshot behind-region into a
layer RT → blur → composite), owned by the renderer subsystem, and is BLOCKED on `OQ-7` (canvas-RT path).

### 8.3 `IVirtualMemory` (backs `ChunkedArena` — `foundations.md` §6 supersede)

```csharp
public interface IVirtualMemory
{
    nint Reserve(nuint bytes);                   // reserve address space, no commit
    void Commit(nint addr, nuint bytes);         // commit pages (O(1) add-chunk, addresses stable)
    void Decommit(nint addr, nuint bytes);       // EWMA high-water reclaim
    void Release(nint addr);
}
```
`FluentGpu.Windows` Pal/ → `VirtualAlloc(MEM_RESERVE)` / `VirtualAlloc(MEM_COMMIT)` / `VirtualFree`. macOS →
`mmap(PROT_NONE)` + `mmap(MAP_FIXED)` / `madvise(MADV_FREE)`. Supersedes the single-buffer per-frame
arena with the segmented `ChunkedArena` (reserve-then-commit, native-backed, no copy, **no LOH cliff**,
GC never sees it). A **native high-water counter — NOT the GC byte tripwire — gates chunk growth** (the
GC API cannot see `VirtualAlloc`'d memory). System-OOM on `Commit` is fatal (clean exception, not
corruption). Used by every arena: DrawList arenas (render-private, ≥3-deep), layout/LIS scratch.

### 8.4 `IImageCodec` (worker-thread decode → CPU bytes; no GPU readback)

```csharp
public interface IImageCodec
{
    bool Decode(ReadOnlySpan<byte> source, int targetBucketPx, Span<byte> dstBgra,
                out ushort naturalW, out ushort naturalH);   // constrained-decode to bucket
    bool TryDownsample16x16(ReadOnlySpan<byte> source, Span<byte> dst256);  // palette-extraction feed
}
```
Leaf `FluentGpu.Windows` (Wic/ folder) (WIC: `IWICImagingFactory` → `CreateDecoderFromStream` → `IWICBitmapScaler`
constrained to the bucket → `CopyPixels` into a **recycled CPU staging slab**). Runs entirely on the
**worker pool** (`hardened-v1-plan.md` §2.1) — pure function over immutable bytes, results by handle,
touches no SceneStore/RhiTable/fence. Palette extraction (`FluentGpu.Theme`) consumes the worker's CPU
staging block (or the optional 16×16 downsample) — **no GPU `ReadbackImage`** (a readback is a UI/render-
thread device stall and unnecessary since the decoder holds the pixels). macOS → ImageIO/`CGImageSource`.

### 8.5 `IPlatformLocale` (current-culture OS fact — UI thread read, versioned external-store shape)

```csharp
public enum MeasurementSystem : byte { Metric = 0, UsCustomary = 1 }

public readonly struct LocaleSnapshot                // fat by-value; re-read on demand, never put in a context
{
    public readonly StringId CurrentBcp47;           // culture name, e.g. "de-DE"; interned; feeds text.md TextLayoutRequest.locale
    public readonly char     DecimalSeparator;       // edge formatting (text.md / dsl-aot consume)
    public readonly char     GroupSeparator;
    public readonly DayOfWeek FirstDayOfWeek;
    public readonly MeasurementSystem Measurement;
    public readonly bool     IsRtlDefault;           // UI default reading order for the culture
}

public interface IPlatformLocale                     // modeled EXACTLY on ISystemColors (§8.1): Epoch + fat snapshot
{
    uint           Epoch       { get; }              // bumped on WM_SETTINGCHANGE (locale/region change)
    LocaleSnapshot GetSnapshot();                    // current-culture fact by value
}
```
`FluentGpu.Windows` Pal/ reads `GetUserDefaultLocaleName` + the regional `GetLocaleInfoEx` formatting fields once at
startup and per `WM_SETTINGCHANGE`, bumping `Epoch`. Like `ISystemColors`, the reactive context is
**`Context<uint>` over `Epoch`** (DepKey-projectable, boxless) — consumers subscribe to the Epoch and re-read
the fat `LocaleSnapshot` on demand; a fat `CultureInfo`/snapshot is **never** placed in a context (it would
box). This is a **versioned external-store-shaped seam** (`threading-render-seam.md` §12bis
`IExternalStore<TSnapshot>` shape, same as `ISystemColors`). `text.md`/`dsl-aot` consume it for edge
formatting + the baked-culture pick. **macOS:** `Pal.Cocoa` mirrors it via `NSLocale` (and the
`NSCurrentLocaleDidChangeNotification`); the formatter + baked blobs stay portable.

---

## 9. Thread-confinement & zero-alloc summary (which thread owns what)

| State / resource | Owning thread | Why safe |
|---|---|---|
| HWND, WndProc, GCHandle-in-USERDATA, DPI, `InputEventRing` writer | **UI** | input-coupled; OS calls WndProc on the creating thread |
| `ISystemColors`/`IBackdropSource` reads | **UI** | OS-state reads; no GPU device touch |
| Every `ComPtr`, `RhiHandleTable`, PSO cache, fence, deferred-delete ring | **RENDER** | single-writer refcount (confinement); `hardened-v1-plan.md` §2.1 |
| Swapchain, DComp tree, `IVideoPresenter` visuals, `Present` | **RENDER** | DComp/DXGI device-affine |
| `UploadRing`, `TextureStagingRing`, `TexturePool` | **RENDER** | mapped GPU heaps; fence-gated reset |
| `IImageCodec` decode + palette extraction | **WORKER** | pure fn over immutable bytes → CPU staging by handle |
| `ChunkedArena` / `IVirtualMemory` | UI for UI arenas; RENDER for DrawList arenas | each arena single-writer |
| device-lost word, present-ack seq, resize request | cross-thread single aligned words | `Volatile` both-directions; the only lock-free seam besides `SceneFrame` |

Zero per-frame managed allocation on phases 6–13: `Present`/`SubmitDrawList`/`CopyBufferToTexture` take
spans + handles + POD; the staging/upload rings and texture pool are pre-allocated and recycled;
`CreateTexture`/`CreateBuffer`/`CreatePipeline` are **cold-path only** (startup + pool growth +
device-lost rebuild). The only permitted per-frame GC is freshly-captured user closures at the edge
(phases 2/4). `ComPtr` is Move-only across the seam; nothing on the hot path uses ComWrappers.

---

## 10. Failure / edge cases

- **Device-lost mid-frame:** §6 — render thread aborts the frame, sets the word, rebuilds from CPU
  state. UI never touches a stale ComPtr.
- **Swapchain `ResizeBuffers` fails / 0-area window:** clamp to 1×1, skip present while minimized;
  re-`Place` video on restore.
- **Staging-ring exhaustion in one frame:** the two-lane byte budget (§4.3) caps uploads; overflow rolls
  to next frame (cross-fade hides the late thumb). Never a synchronous device stall.
- **Texture-pool exhaustion:** cold-path `CreateTexture` grows the bucket pool (one-time hitch),
  budget-capped; under hard budget the residency manager evicts LRU first.
- **DComp `Commit` storm:** `Commit` only when a visual prop changed (`compositionDirty`) — a pure
  scrubber redraw within the same swapchain visual does NOT `Commit`.
- **PerMonitorV2 straddle / monitor-DPI change:** `WM_DPICHANGED` snaps to one `Scale`, bumps
  `ConfigVersion`, forces relayout; back buffer resizes only on a client-size change.
- **Occlusion / RDP / WARP:** test-present throttle; WARP adapter is the fallback and the headless test
  path. AA goldens use a perceptual tolerance vs hardware (WARP is not bit-identical).
- **Video surface lost (external decoder dies):** `IVideoPresenter.SetVisible(false)`, fall back to the
  poster/art lower layer (`DrawVideoCmd.PosterBlur`); `VideoReady` drives the crossfade back.
- **Two-clock PiP tear:** the `Place` move and the canvas hole-punch commit in the SAME phase-11 DComp
  `Commit` (§7).
- **OOM on `IVirtualMemory.Commit`:** clean fatal exception (not corruption); native high-water gates
  growth before this in normal operation.

---

## 11. Cross-platform (macOS) boundary

| Seam | macOS leaf | Maps from Windows |
|---|---|---|
| `IPlatformApp/Window/AppLoop` (`Pal.Cocoa`) | `NSWindow`/`NSView`, `CVDisplayLink`, `NSEvent`→ring, `NativeHandle{NsView}` | HWND/WM_*/PerMonitorV2 |
| `IGpuDevice/ISwapchain/ICommandEncoder` (`Rhi.Metal`) | `MTLDevice/CommandQueue/RenderPipelineState`, `CAMetalLayer`, `id<CAMetalDrawable>`=`CurrentBackBuffer`, `MTLRenderPassDescriptor`=`RenderPassDesc`, `MTLBlit copyFromBuffer:toTexture:`=`CopyBufferToTexture` | D3D12/DXGI flip + DComp |
| present compose | `CALayer` tree: `CAMetalLayer` + sibling video/backdrop layers (z-ordered) | multi-visual DComp tree |
| `IVideoPresenter` | `AVPlayerLayer`/`CALayer` sibling | DComp child visual |
| `ISystemColors` | `NSAppearance` / HC colors | `WM_SETTINGCHANGE` accent/HC |
| `IPlatformLocale` | `NSLocale` + `NSCurrentLocaleDidChangeNotification` | `GetUserDefaultLocaleName`/`GetLocaleInfoEx` + `WM_SETTINGCHANGE` |
| `IPlatformWindow.SetCursor`/`RegisterCustomCursor` | `NSCursor.set()`/`addCursorRect` | `SetCursor`/`LoadCursor`, `WM_SETCURSOR` |
| `IBackdropSource` | `NSVisualEffectView` | Mica via DWM/DComp |
| `IVirtualMemory` | `mmap`/`madvise` | `VirtualAlloc`/`VirtualFree` |
| `IImageCodec` | ImageIO/`CGImageSource` | WIC |
| Color | `CAMetalLayer.colorspace` (linear); atlas+linear-blend invariant maps cleanly | UNORM buffer + `_UNORM_SRGB` RTV + DComp PREMULTIPLIED |

**Must NOT leak above the seam:** `HWND/NSWindow/HRESULT/NSError/ComPtr/id<...>/ID3D12*/MTL*/IDXGI*/
IDComposition*/CAMetalLayer/WM_*/NSEvent`. Above sees only `Size2/Scale/PointPx`, opaque `NativeHandle`,
POD `InputEvent`/`WindowEvent`, the DrawList POD stream, generational handles, POD RHI descs,
`ISwapchain.Present`. The D2D1 fallback (`IEffectRunner`) is a Windows-only crutch and carries a
per-primitive Metal-milestone debt list (`hardened-v1-plan.md` §4.3).

---

## Changed vs the original synthesis

Amendments folded into this subsystem (each traceable to a cross-cutting doc):

1. **Render thread owns every ComPtr / swapchain / fence / heaps; UI thread touches zero COM.** Window +
   pump stay on the UI thread; device + present move to the render thread. (`hardened-v1-plan.md` §2.1,
   §4.1) — supersedes `architecture-spec.md` §6's "single UI/render thread"; v1 still ships single-thread
   first via `QUARANTINE=0`.
2. **Device-lost detected on the render thread (synchronous HRESULT primary), marshaled to UI by a single
   `Volatile` word + `WindowEvent`; OS-wait-thread backstop touches no ComPtr.** (`architecture-spec.md`
   §5.1 + `hardened-v1-plan.md` §2.3) — was an undifferentiated UI-thread concern.
3. **`ICommandEncoder.CopyBufferToTexture` + a dedicated MB-sized texture-staging ring + a startup
   per-bucket texture pool — NEW.** Texture upload no longer (incorrectly) rides the instance `UploadRing`;
   `CreateTexture` never runs in phases 6–13. (`app-requirements-waveemusic.md` §3.1/§5)
4. **Multi-visual DComp present-tree** (UI swapchain z-above a video child visual) + the **transparent
   premultiplied-0 hole-punch** clear protocol for `DrawVideoCmd.Dst`, replacing the single-visual §5.1
   present path. `DrawVideoCmd` sortkey orders the alpha-punch below all chrome.
   (`app-requirements-waveemusic.md` §3.4)
5. **Five NEW PAL seams:** `ISystemColors` (accent+HC+`Epoch`, reactive via `Context<uint>` not a fat
   struct), `IBackdropSource` (Mica/Acrylic), `IVideoPresenter` (POD `VideoSurfaceId`, DComp sibling),
   `IVirtualMemory` (reserve/commit backing `ChunkedArena`, native high-water gated — supersedes the
   single-buffer arena, `foundations.md` §6), `IImageCodec` (WIC leaf, worker-thread decode, no GPU
   readback). (`app-requirements-waveemusic.md` §5, `architecture-spec.md` §4.7)
6. **Resize CPU-waits only this swapchain's in-flight fences, not device-wide `WaitIdle`** — other windows
   on the shared device keep rendering. (`architecture-spec.md` §5.1)
7. **COM is generated-and-gated:** hot path (cmd-list/queue/`Present`/DComp) = GENERATED hand-vtable
   `calli` from a harvested `*.comabi.json` (no human-typed slot); cold (UIA/TSF/OLE/DWrite-setup) =
   `[GeneratedComInterface]`/`[GeneratedComClass]`; flat C exports = `[LibraryImport]`; no ComWrappers on
   the hot path; `ComPtr` render-thread-confined + Move-only across the seam.
   (`dotnet10-csharp14-zero-alloc.md` §4, `hardened-v1-plan.md` §4.2)
8. **Buffer `BGRA8_UNORM` / RTV `BGRA8_UNORM_SRGB` split, single-sampled back buffer, `ResolveTexture`
   for opt-in MSAA in linear, premultiplied output** — the COLOR contract folded as a hard
   present-creation requirement. (`architecture-spec.md` §4.7/§5.1)
9. **`HandleTable` pointer-bearing slabs use a separate `int[] _next` free-link** (not the
   intrusive-first-4-bytes trick, which stomps `ComPtr.ptr_`); `Free` disposes before slot reclaim.
   (`foundations.md` §4.2 BLOCKER)
10. **Input crosses as a POD `InputEventRing`, not C# events; present targets as POD `NativeHandle`** —
    the one `object`-typed seam field is eliminated. (`architecture-spec.md` §4.7)
