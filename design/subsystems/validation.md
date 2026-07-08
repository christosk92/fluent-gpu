# FluentGpu — Validation & Maturity Program (spikes + CI gates)

> **Subsystem design-of-record for `FluentGpu.Validation` and the CI configuration.**
> This is the deep design behind [hardened-v1-plan.md](../hardened-v1-plan.md) §4.5 ("Validation — the
> spine that keeps it true").
>
> **Not to be confused with [form-validation.md](./form-validation.md)** — *data/form* validation (`FluentGpu.Forms`:
> `Validator<T>`/`Field`/`UseForm`). This doc is the *engine-correctness* CI machinery (spikes + per-PR gates); that
> one is the app-facing "is this input valid?" feature. It owns the machinery that turns every *claim* the other subsystems make
> into an *enforced property*: the two clocks (SPIKES = capability gates, GATES = per-PR regression nets),
> the trust-ring `[Capability]` attribute + analyzer + runtime assert, the fault-injection harness, and the
> WaveeMusic worst-case perf benchmarks with regression budgets.
>
> **The one blunt invariant this whole doc serves** (hardened-v1 §8 bottom line, restated and OWNED here):
> every `ThreadGuard`, alloc-tripwire, `ComTracker`, `CleanSpanWitness`, and `IsSpikeCaller` assert is
> `[Conditional]`-erased from the shipping NativeAOT binary. **So in the customer's hands, production safety
> *is* CI coverage — nothing else.** A hazard not covered by a green gate or a retired spike is, in
> production, unguarded. This doc's job is to make the coverage exhaustive, non-suppressible, and honest
> about its own gaps.
>
> Cross-cutting contracts are referenced, not re-derived: threading/quarantine/retire-fence
> ([hardened-v1 §2, §4.1](../hardened-v1-plan.md)); COM ruling
> ([dotnet10-csharp14-zero-alloc §4](../dotnet10-csharp14-zero-alloc.md), [hardened-v1 §4.2](../hardened-v1-plan.md));
> the epoch chokepoint + clean-span witness ([architecture-spec §4.5/§5.4](../architecture-spec.md),
> [reconciler-hooks.md](./reconciler-hooks.md), [hardened-v1 §4.4/§4.6](../hardened-v1-plan.md)); the
> 3-signal memo skip ([reconciler-hooks.md](./reconciler-hooks.md)); the renderer goldens
> ([gpu-renderer.md](./gpu-renderer.md), [hardened-v1 §4.3](../hardened-v1-plan.md)); the WaveeMusic
> worst-cases ([app-requirements-waveemusic §6](../app-requirements-waveemusic.md)).

---

## 0. Where this lives, and what it can touch

| Property | Value |
|---|---|
| **Assembly** | `FluentGpu.Validation` (NEW). Test-host + CI-only; **never referenced by `Hosting`** and never shipped. |
| **Assembly (NEW, shipped)** | `FluentGpu.Testing` (NEW). The **app-author test harness** (L13), folded into core (§11). Unlike `Validation`, this **is a shipped, consumer-facing NuGet** that app authors reference from *their* test projects. It is a thin assert/simulate/snapshot API layered atop the same headless leaves (`FluentGpu.Engine` Headless/Pal/ and Headless/Rhi/) that `Validation` uses, but it carries **no `[Conditional]`-erased guards** and **no spike/ledger machinery** — those stay CI-internal to `FluentGpu.Validation`. `FluentGpu.Testing` depends only on the public seam interfaces + the headless leaves + `Foundation`; it never touches `FluentGpu.Windows` (D3D12/, Pal/, or DirectWrite/). It is itself **not shipped into the app's product binary** (it is a test-time dependency), but it is a *supported public surface* and is versioned as such. |
| **Deps** | `Foundation`, the **interface** seams (`Pal`, `Rhi`, `Text`), `Scene`, `Render`, `Reconciler`, `Hooks`, `Layout`, `Animation`, plus the **headless leaves** `FluentGpu.Engine` (Headless/Pal/) + `FluentGpu.Engine` (Headless/Rhi/). May reference `Media`/`Theme` for their worst-case benches. **Must not** reference `FluentGpu.Windows` (D3D12/, Pal/, or DirectWrite/) except inside the explicitly Windows-gated `text.conformance` / `com.aot.roundtrip` / `render.aa` spike hosts (these are `#if WINDOWS` and excluded from the portable test pass). |
| **Acyclicity** | Validation is a *sink*: everything depends inward, nothing depends on Validation. It adds no edge to the production DAG ([foundations §7](../foundations.md), [architecture-spec §3](../architecture-spec.md)). `FluentGpu.Testing` (§11) is likewise a *sink* on the production DAG — it depends inward on seams + headless leaves; no production assembly references it (app *test* projects do, off the product graph). |
| **Analyzers** | The trust-ring rules **FG0001–FG0003** ship in `FluentGpu.SourceGen` (portable analyzer DLL — it is referenced by everything, so the `[Capability]` fence is enforced at *every* call site, including app code). The `FGCOM####` COM rules live in `FluentGpu.Interop.SourceGen` and are out of scope here (owned by the COM subsystem); this doc only consumes their non-suppressible-floor guarantee. |
| **Erasure** | All runtime guards are `[Conditional("DEBUG")]` or `[Conditional("FGVALIDATE")]`. Shipping AOT defines neither ⇒ the JIT/ILC drops the call sites and the methods become unreferenced and trimmed. Working-set/footprint cost in ship: **~0** (verified by the footprint ratchet itself; see §5.3). |

**Two build configurations this doc assumes exist:**
- **`Debug` / `FGVALIDATE`** — asserts live, tripwires armed, `CleanSpanWitness` validates, `ComTracker`
  counts, `IsSpikeCaller` fires. This is the *only* configuration in which any safety guard executes.
- **`CI-RELEASE`** — `PublishAot` ON, `Optimize` ON, but with `FGVALIDATE` still defined so the
  *non-suppressible* gates (COM QI-roundtrip, leak, ABI-golden, `seam.race`) run against optimized,
  AOT-shaped code. This is the configuration that catches "works in Debug, miscompiles in AOT" — the gap
  the `com.aot.roundtrip` spike formalizes. Shipping `Release` defines neither symbol.

---

## 1. The two clocks (the program's shape)

```
        ┌──────────────────────────────────────── SPIKES (capability clock) ────────────────────────────────────────┐
        │  Run: ONCE to retire an unknown + promote a [Capability] ring; then NIGHTLY to re-characterize.              │
        │  Output: a TRUST-LEDGER row {capability, ring, evidence-hash, date, owner} → gates [Capability] promotion.   │
        │  Members: text.conformance · com.aot.roundtrip (vs the PublishAot'd binary) · render.aa (golden) ·           │
        │           seam.race (soak; SWEPT channel-cap + reader-stall)                                                  │
        └───────────────────────────────────────────────────────────────────────────────────────────────────────────┘
                                                          │  promotes a ring (Experimental→Spiked→Proven)
                                                          ▼
        ┌──────────────────────────────────────── GATES (per-PR regression clock) ──────────────────────────────────┐
        │  Run: EVERY PR (blocking) + nightly (extended). Output: pass/fail + a delta-vs-baseline artifact.            │
        │  Members: alloc-tripwire (per-phase Δ==0, per-worker AllocScope-aggregated)                                  │
        │           + the LOAD-BEARING process-wide BDN gen0/1/2==0 backstop                                          │
        │         · footprint .mstat ratchet · golden-image perceptual diff                                           │
        │         · headless structural (draw-call / batch / barrier / Bounds) asserts on Engine/Headless/Rhi+Pal     │
        │         · COM net-refcount / leak gate · epoch fault-injection · data-race gate                             │
        │   FOLDED-GAP GATES (§12, one per core-folded gap, mapped in §12.13):                                         │
        │         · lane/transition determinism (P1) · auto-batching semantics (P2a) · Suspense reveal+keep-stale     │
        │           (P2b) · external-store tear (P3) · discard-restart byte-identical golden (P4)                      │
        │         · gesture-arena determinism (L2) · text-selection + ITextRangeProvider conformance (L1/L3)          │
        │         · RTL mirror golden ×locale (L5) · spring/retarget stability (L7) · overlay light-dismiss/restore   │
        │           (L4) · virtualized-a11y realization (L6) · + P5/P6/P7/P8/P9/L8/L9/L10/L11 companions               │
        │   PLUS the SHIPPED app-author harness FluentGpu.Testing (§11, L13) consuming the same headless leaves       │
        └───────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**The discipline that makes this a "maturity program" not a test folder:** the two clocks are coupled by the
**trust ledger** (§4). A spike does not just "pass" — it *promotes a named capability one ring* and writes an
evidence row. The analyzer then physically forbids shipping code from calling into a capability whose ring is
below `Proven` unless the caller is itself marked. This is how "claim ≠ property" ([hardened-v1 §4.4](../hardened-v1-plan.md))
is closed at compile time: you cannot *accidentally* depend on an unproven mechanism — the build breaks.

---

## 2. SPIKES — capability gates (the unknown-retirement clock)

A spike is a **standalone executable test target** (its own entry point, its own `.csproj`) so it can be run
against the *real* PublishAot'd output, not just the unit-test host. Each spike implements:

```csharp
public interface ISpike
{
    static abstract SpikeId Id { get; }              // e.g. "com.aot.roundtrip"
    static abstract Capability Gates { get; }        // which [Capability] ring this promotes
    SpikeVerdict Run(in SpikeEnv env);               // Green / Amber(evidence) / Red(reason)
    SpikeEvidence Characterize(in SpikeEnv env);     // nightly: numbers, not pass/fail
}

public readonly record struct SpikeVerdict(SpikeOutcome Outcome, string Reason, ulong EvidenceHash);
```

A **Green** verdict from a spike is the *only* thing that may promote a capability ring upward in the trust
ledger (§4). **Amber** (e.g. a perceptual delta inside tolerance but trending) holds the ring and files a
nightly-watch row. **Red** blocks promotion and, if the capability was already `Proven`, *demotes* it and
fails the nightly — a regression in a property previously proven.

### 2.1 `text.conformance` — text correctness vs DWrite + Unicode (gates `Text.Shaping`, `Text.BiDi`, `Text.LineBreak`)

**The unknown it retires** ([architecture-spec §10 risk "Text correctness"](../architecture-spec.md)): our
shaping/BiDi/line-break must be *byte-for-byte where defined* and *perceptually-correct where DWrite is the
oracle*. There are two distinct oracles and they are kept separate:

1. **Unicode-conformance oracle (deterministic, portable):** drive the official `BidiCharacterTest.txt`,
   `BidiTest.txt`, `LineBreakTest.txt`, `GraphemeBreakTest.txt`, `WordBreakTest.txt` corpora through the
   portable `FluentGpu.Text.Unicode` engine (the one that carries the macOS milestone, [architecture-spec §9](../architecture-spec.md)).
   Assertion is **exact**: produced levels / break-opportunities / cluster boundaries == the corpus, every row.
   No tolerance. A miss is a hard Red. This oracle does not need Windows and runs in the portable pass.
2. **DWrite shaping oracle (perceptual, Windows-only, `#if WINDOWS`):** for a curated script corpus (Latin,
   CJK, Arabic with marks/kashida, Devanagari conjuncts, Hebrew niqqud, Thai, emoji ZWJ sequences), shape the
   same text through `FluentGpu.Windows` (DirectWrite/ folder) and through our pipeline and assert: same glyph count per cluster,
   advance-width within ε, BiDi visual order identical, subpixel phase bucket identical. Positions are the
   final device-space dest rects the renderer contract promises ([architecture-spec §4.6](../architecture-spec.md)),
   so this is an end-of-pipeline comparison.

```csharp
sealed class TextConformanceSpike : ISpike
{
    static SpikeId Id => "text.conformance";
    static Capability Gates => Capability.Text_Shaping;     // + BiDi + LineBreak via sub-verdicts

    public SpikeVerdict Run(in SpikeEnv env)
    {
        foreach (var row in UnicodeCorpus.BidiCharacterTest())
            if (!BidiEngine.Resolve(row.Text).SequenceEqual(row.ExpectedLevels))
                return Red($"BiDi level mismatch at {row.LineNo}: {row.Text}");
        // … LineBreak/Grapheme/Word identical-or-fail …
        if (env.HasWindows)
            foreach (var s in ScriptCorpus.All)
            {
                var ours = ShapeOurs(s); var dw = ShapeDWrite(s);
                if (!ClusterEqual(ours, dw, advanceEps: 0.5f))
                    return Red($"shaping divergence: script={s.Script} text={s.Sample}");
            }
        return Green();
    }
}
```

**Concurrent clause (the `seam.race` overlap):** because off-thread glyph raster is sequenced behind the seam
([hardened-v1 §4.3](../hardened-v1-plan.md)), `text.conformance` also runs a **concurrent variant** that
shapes the same corpus from N worker threads against a SHARED DWrite factory (serialized) to validate the
"DWrite multithread no-retain" MANAGED assumption ([hardened-v1 §3 §4.1 ledger](../hardened-v1-plan.md)). If
this variant is not Green, off-thread raster stays descoped — the spike *is* the gate on phase 6 of the build
order.

**Thread/phase:** runs offline (not in the frame loop). The DWrite oracle uses `FluentGpu.Windows` (DirectWrite/ folder) which owns
its CCWs on one thread (the COM thread-pinning rule); the concurrent variant deliberately violates the
single-thread *assumption* to test it, never the single-writer *confinement* (each worker has its own
analysis source/sink).

### 2.2 `com.aot.roundtrip` — generated COM ABI vs the live system DLL, AGAINST THE PublishAot'd BINARY (gates `Com.GeneratedAbi`)

**The unknown it retires** ([hardened-v1 §4.2 + §4.5 "ABI generator gets a runtime self-check"](../hardened-v1-plan.md),
[dotnet10 §4](../dotnet10-csharp14-zero-alloc.md)): the hot-path COM bindings are generated from a harvested
`*.comabi.json`; a SHA over the JSON proves "the extraction didn't change," but it **cannot** prove "the
generated slot index + calling convention actually match the system DLL we load at runtime under AOT." The two
header-derived oracles (winmd, ClangSharp) share header-level errors, so they are **not independent**. The only
header-independent oracle is **calling a known method on a live object and observing the result.**

This spike is special in one way that the others are not: **it must run against the actual `PublishAot`-produced
executable**, because ILC can lay out generics, devirtualize `calli`, and strip metadata differently than the
JIT. A Debug-host pass here is worthless. The CI job builds `CI-RELEASE`, then *runs the produced binary* in a
self-test mode.

Three layers, fail-safe ([hardened-v1 §4.2 "AbiVerify made fail-safe"](../hardened-v1-plan.md)):

```csharp
// Runs INSIDE the PublishAot'd binary under --self-test=com.aot.roundtrip
static SpikeVerdict ComAotRoundtrip()
{
    // L1: anchor on the ABI-FROZEN IUnknown slots (0,1,2) only — never trust an unverified slot.
    //     QueryInterface(ownIID) must return a pointer that is reference-equal to `this` (pointer identity).
    foreach (var iid in HotPathInterfaces.Iids)
    {
        if (!TryCreateLiveObject(iid, out var p)) return Red($"cannot instantiate {iid}");
        if (IUnknownOps.QueryInterface(p, iid) != p)        // header-independent identity oracle
            return Red($"QI({iid}) identity failed — slot 0 wrong or RCW confusion");
        // L2: net-refcount roundtrip — AddRef then Release must return to baseline (see §3.5 leak gate).
        uint r0 = IUnknownOps.AddRef(p); uint r1 = IUnknownOps.Release(p);
        if (r1 != r0 - 1) return Red($"refcount ABI off for {iid}");
    }
    // L3: behavioral self-check — call ONE known method through the GENERATED slot and assert the
    //     observed behavior matches the loaded system DLL. e.g. IDWriteFactory::GetSystemFontCollection
    //     returns S_OK + a non-null collection; ID3D12Device::GetNodeCount returns >=1.
    foreach (var probe in BehavioralProbes.All)
        if (!probe.Observe(out var why)) return Red($"behavioral ABI mismatch: {probe.Name}: {why}");
    return Green();
}
```

All three layers are wrapped in a **vectored-exception guard** so a wrong slot faults into a Red verdict rather
than corrupting the process. `ComTracker` is **re-keyed by (pointer, generation)** here, not raw `nint`, so the
roundtrip cannot false-green across a snapshot-slot recycle (ABA) ([hardened-v1 §4.2](../hardened-v1-plan.md)).

**Promotion rule:** this is a **non-suppressible CI-RELEASE gate** as well as a spike — it is the floor under
the FGCOM analyzers. If the analyzers are suppressed under schedule pressure, this gate is what still catches a
broken binding ([hardened-v1 §4.2 "safety floor under schedule pressure"](../hardened-v1-plan.md)).

### 2.3 `render.aa` — anti-aliasing & rasterization golden (gates `Render.AnalyticAA`, `Render.Tessellation`)

**The unknown it retires** ([hardened-v1 §4.3, gpu-renderer §11](../hardened-v1-plan.md)): analytic-SDF AA, the
erf shadow, and the tessellator are a *corpus-gated regression net, not a proof* — explicitly. This spike owns
the golden corpus and the perceptual metric.

- **Reference:** a 16× supersampled CPU rasterizer (independent of the GPU path; this is also the differential
  oracle the tessellator's geometric-correctness fuzz cross-checks against — watertightness + winding +
  robust-area at higher precision, [hardened-v1 §4.3](../hardened-v1-plan.md)).
- **Metric:** **CIEDE2000** per-pixel ΔE + an **edge-shift** metric (sub-pixel boundary displacement), both
  with a tolerance table keyed by `{primitive, dpi, rotation, color}`. The tolerance is *deliberately loose
  enough to absorb WARP-vs-hardware* differences (we cannot demand bit-equality across rasterizers) and
  *tight enough to catch a gamma/premul regression*.
- **Coverage caveat (OWNED, stated honestly):** uncovered DPI/rotation/color/script is **ungated**. The spike
  emits a coverage manifest so the gap is visible, not hidden. A new primitive lands with its golden row or it
  cannot promote `Render.AnalyticAA`.
- **Disposition ladder:** AA-fringe → MSAA → D2D fallback is selected by the **golden disposition table** — if
  the analytic path fails the golden for a primitive class, that class routes to the fallback and the table
  records it as a Metal-milestone debt item ([gpu-renderer.md](./gpu-renderer.md)).

Runs against both `FluentGpu.Engine` (Headless/Rhi/) (WARP, deterministic, the CI default) and, nightly, real D3D12 hardware to
characterize the WARP-vs-hardware delta.

### 2.4 `seam.race` — concurrency soak with SWEPT params (gates `Seam.Quarantine`, `Seam.RetireFence`)

**The unknown it retires** ([hardened-v1 §4.1, §4.5, §5, §6 build steps 4–5](../hardened-v1-plan.md)): the
render-thread seam has exactly *one* lock-free fence whose acquire/release correctness is **sampled, not
proven** (no managed TSan). The soak is that sampling, and the headline correction it folds in is:

> **The soak must SWEEP channel-capacity and reader-stall as fuzz parameters — it must NOT hard-code
> `quarantine=2`.** Hard-coding the constant validates the off-by-one, not the *relationship* the proof needs.
> And it must run on the CURRENT (quarantine=0) build first; **you do not flip 0→2 on the soak.** The flip
> happens only after the soak is green for the required nightly streak ([hardened-v1 §6 step 5](../hardened-v1-plan.md)).

The swept parameter space:

```csharp
readonly record struct SeamRaceParams(
    int ChannelCap,         // {1, 2, 3}  — the cap-1 DropOldest coalesce vs deeper
    int ReaderStallFrames,  // {0, 1, 2, 3, 5} — render thread paused N publishes (forces quarantine pressure)
    int Quarantine,         // {0, RenderInFlightDepth, RenderInFlightDepth+1} — derived, not magic 2
    int WorkerCount,        // {2..6}
    bool ForceSyncDrain);   // build-order step 4: render thread but no slot reuse in flight

// The product space is enumerated; each cell runs a 10^6-frame deterministic schedule-fuzz.
```

Two assertions per cell, both load-bearing:

1. **`Render_NeverObserves_RecycledSlot`** — pause the render thread for `ReaderStallFrames + 1` publishes and
   assert no SceneStore slot is reclaimed until `_lastConsumedSeq > freedSeq` (the consume-gated quarantine
   invariant). A reclaim-too-early is the UAF the original design actually had ([hardened-v1 §2.3](../hardened-v1-plan.md)).
2. **`ConcurrentRecord_MatchesSingleThreadedGolden`** — the concurrent run must be **byte-identical** to the
   single-thread golden DrawList for the same input schedule. This is the strongest possible correctness
   statement for the seam: any divergence is a torn-DrawList or stale-epoch read.

The soak runs under `ThreadGuard` (single-writer deterministic throw) and the consume-gated `QuarantineLedger`
(also deterministic throw). The **retire-fence handshake** replaces DropOldest's unbounded run-ahead for slot
recycling, giving the one documented acquire/release happens-before; `seam.race` is what exercises it under
reader stall. Because there is no managed TSan, the honest grade stays **MANAGED-sampled** — this spike reduces
risk, it does not eliminate it, and the doc says so.

**Build-order coupling (the program's spine):** `seam.race` gates the *transition* between build-order steps,
not a feature. Step 4 (render thread spawned, `ForceSyncDrain=true`, quarantine logically 0) must be green
before step 5 (flip quarantine to derived depth) is even attempted. This is why `seam.race` is a spike (a
capability gate) and not merely a per-PR test.

### 2.5 Nightly re-characterization

Every spike's `Characterize()` runs nightly and emits *numbers* (perceptual ΔE distributions, refcount deltas,
soak frame-counts-to-first-divergence==∞, shaping advance histograms) to a time-series. A spike that *passes*
but *trends* toward its tolerance edge files an Amber watch row — this is how a slowly-rotting golden corpus or
a creeping refcount imbalance is caught before it flips a PR gate Red.

---

## 3. GATES — per-PR regression nets (the blocking clock)

Every gate below runs on every PR (blocking) and again nightly (extended corpus / longer soak). Each emits a
**delta-vs-baseline artifact** so a reviewer sees *what moved*, not just pass/fail.

### 3.1 Alloc-tripwire — per-phase Δ==0, per-worker AllocScope-aggregated + the load-bearing BDN backstop

**The headline correction folded in** ([hardened-v1 §4.5](../hardened-v1-plan.md)):
`GC.GetAllocatedBytesForCurrentThread()` **does not follow work across the seam.** Once tessellation/raster
fan out to workers (build step 6), a per-UI-thread delta reads 0 while the workers happily allocate. So the
tripwire is **two-layer**, and the *process-wide BDN gate is the load-bearing one*; the per-thread tripwire is
only the **localizer** (it tells you *which phase/worker* regressed once BDN says *something* did).

**Layer 1 — per-phase, per-thread tripwire (`[Conditional("FGVALIDATE")]`, the localizer):**

```csharp
public readonly ref struct AllocScope               // ref struct: stack-only, zero heap
{
    readonly long _start;
    readonly PhaseId _phase;
    readonly AllocAggregator _agg;                  // per-FRAME summing aggregator, MPSC across workers
    public AllocScope(PhaseId phase, AllocAggregator agg)
    { _phase = phase; _agg = agg; _start = GC.GetAllocatedBytesForCurrentThread(); }

    public void Dispose()
    {
        long delta = GC.GetAllocatedBytesForCurrentThread() - _start;
        _agg.Add(_phase, Environment.CurrentManagedThreadId, delta);   // worker-private slot, no contention
        // HOT phases (6..13) assert Δ==0 PER THREAD immediately for fast localization:
        if (_phase.IsHot && delta != 0)
            FgAssert.Fail($"alloc in hot phase {_phase} on tid {Environment.CurrentManagedThreadId}: {delta}B");
    }
}
```

**Every fan-out worker is independently `AllocScope`-wrapped** ([hardened-v1 §2.1 "Each AllocScope-wrapped"](../hardened-v1-plan.md)),
and the `AllocAggregator` sums per-frame across the UI thread + all workers into one per-phase number. The
aggregator slots are per-`(phase, threadId)` so workers never contend.

**Layer 2 — process-wide BenchmarkDotNet gen0/1/2==0 backstop (LOAD-BEARING):**

```csharp
[MemoryDiagnoser]                                   // captures Gen0/Gen1/Gen2 collection counts + bytes
public class SteadyStateFrameBench
{
    [Benchmark] public void IdleFrame()    => _host.RunOneFrame();        // O(0) — must be Gen0==0
    [Benchmark] public void ScrollFrame()  => _host.RunScrollFrame();     // O(visible window) only
    [Benchmark] public void AnimateFrame() => _host.RunTransformAnimFrame();
}
// CI assertion (BlazingDeltaGate): Gc.Gen0Collections + Gen1 + Gen2 == 0 over the measured iterations
// for IdleFrame; for ScrollFrame/AnimateFrame, bytes/op <= ratcheted budget (NOT 0 — bounded edge alloc).
```

The BDN gate is load-bearing precisely because it observes the **whole process** — it sees worker allocations,
finalizer queue churn, and any hidden boxing the per-thread tripwire's seam-blindness would miss. The
per-thread tripwire localizes; **BDN decides.** Both must be green.

**Phase map** (which thread, [hardened-v1 §2.2](../hardened-v1-plan.md)): tripwire scopes wrap phases 6–13.
Phases 6–7, 6.5, and PUBLISH run on the **UI thread**; phases 8–11 (record/batch/submit/present) run on the
**RENDER thread**; worker decode/tessellate/raster scopes run on the **WORKER pool**. The aggregator must sum
all three thread classes for the frame to read a true total — this is the entire point of the correction.

**The irreducible-residual the gate does NOT claim to fix** ([hardened-v1 §1, painpoints §5](../hardened-v1-plan.md)):
user closures at the edge (phases 2/4) are *expected* Gen0 and are budgeted, not asserted-zero. A legitimate
huge-subtree `setState` mints that subtree in one frame with no GC backstop on the frame thread — the gate
measures it and ratchets a budget, it does not pretend it is zero.

### 3.2 Footprint `.mstat` ratchet

Owns the binary-size gate ([architecture-spec §8](../architecture-spec.md), [hardened-v1 §5](../hardened-v1-plan.md)).

- Builds the shipping `Release` AOT binary (no `FGVALIDATE`), emits `--mstat` + runs `sizoscope`.
- Asserts whole-exe size ≤ the checked-in `footprint.baseline.json` (start ~8 MB, ratchets *down* only).
- **Also asserts the erasure claim:** the baseline records that `FluentGpu.Validation` contributes **0 bytes**
  to the shipping binary and that no `FgAssert`/`AllocScope`/`ComTracker`/`CleanSpanWitness` symbol survives
  trimming. This is the mechanical proof of "production safety == CI coverage" — if a guard leaks into ship,
  this gate fails, telling us a `[Conditional]` was mis-applied (it would mean the guard runs in production,
  which is *also* wrong, just in the other direction).
- The ratchet is one-directional: a PR may *lower* the baseline (with the artifact showing what shrank); a PR
  that *raises* it must explicitly bump `footprint.baseline.json` with a justification, which is reviewer-visible.

### 3.3 Golden-image perceptual diff

The per-PR consumer of the `render.aa` corpus (§2.3). Renders the curated scene corpus through
`FluentGpu.Engine` (Headless/Rhi/) (WARP) and asserts CIEDE2000 + edge-shift within the disposition-table tolerance. Distinct from
the **structural** gate (§3.4): this one looks at *pixels*, that one looks at *commands*. A PR that changes a
shader or the batcher must pass both. New goldens are added with the PR that introduces the primitive; a golden
can only be *re-baselined* with an explicit reviewer-approved artifact diff (no silent golden updates).

### 3.4 Headless structural gate — draw-call / batch / barrier / Bounds asserts on `FluentGpu.Engine` Headless/Rhi/ + Headless/Pal/

This is the cheapest, fastest, most deterministic gate and the one that runs in the *portable* pass (no
Windows, no GPU). `FluentGpu.Engine` (Headless/Rhi/) is a CPU/null encoder that records *what was asked of it*; `FluentGpu.Engine` (Headless/Pal/)
synthesizes window/input/DPI events ([architecture-spec §3](../architecture-spec.md)).

```csharp
public sealed class StructuralLedger : ICommandEncoder    // the Engine/Headless/Rhi recording encoder
{
    public int DrawCalls, BatchCount, BarrierCount, PsoSwitches;
    public readonly List<RectPx> EmittedBounds = new();
    // … records every SubmitDrawList opcode + every Barrier span + every SetPipeline …
}
```

Per-scenario assertions (exact integers, the strongest regression net we have because it is deterministic):

- **Draw-call count**: e.g. "the 400-thumb album wall packs to ≤ ~80 draws" ([waveemusic §6](../app-requirements-waveemusic.md)).
- **Batch count + PSO switches**: the overlap-grid batcher must not regress its merge ratio; a painter-order
  break that fragments batches shows up here as a batch-count spike.
- **Barrier count**: a redundant PRESENT↔RT barrier or a missing one is a structural defect — asserted exact.
- **Bounds**: the emitted device-space dest rects (recomputed from `Bounds[]`/`WorldTransform[]`) match the
  expected layout — this is the structural twin of the epoch fault-injection (§3.6); a layout regression that
  the pixel golden might absorb within tolerance is caught here exactly.

Because it is deterministic and portable, this gate is also the **CI sanity anchor**: the minimum-vertical-slice
Button ([architecture-spec §11](../architecture-spec.md)) passes here with the alloc-tripwire green on phases
6–13 before any real-GPU gate even runs.

### 3.5 COM net-refcount / leak gate (non-suppressible)

Owns the runtime COM-lifetime net under CI-RELEASE ([hardened-v1 §4.2, dotnet10 §4](../hardened-v1-plan.md)).
`ComTracker`, **keyed by (pointer, generation)** (mandatory before the seam — raw `nint` ABA-false-greens
across snapshot recycle):

```csharp
[Conditional("FGVALIDATE")]
static void TrackAddRef(nint p, uint gen) => ComTracker.Delta((p, gen), +1);
[Conditional("FGVALIDATE")]
static void TrackRelease(nint p, uint gen) => ComTracker.Delta((p, gen), -1);

// At a quiescent point (frame boundary, teardown) the gate asserts the net map is empty:
//   every tracked (pointer,gen) has net refcount delta 0 ⇒ no leak, no over-release.
public static void AssertNetZero() { foreach (var kv in ComTracker.Map) FgAssert.Eq(kv.Value, 0, kv.Key); }
```

Two assertions: **net-zero at teardown** (no leak) and **never-negative mid-run** (no over-release / double-free,
which under AOT is a use-after-free). Runs in `CI-RELEASE` so it observes the AOT-shaped, render-thread-confined
ComPtr lifetimes — the only place the confinement claim is actually exercised against optimized code. This gate
is **non-suppressible** and is part of the floor under the FGCOM analyzers (§2.2).

### 3.6 Epoch fault-injection (inject BOTH failure modes)

Owns the airtight-epoch property ([hardened-v1 §4.4/§4.6, architecture-spec §4.5/§5.4](../hardened-v1-plan.md),
[reconciler-hooks.md](./reconciler-hooks.md)). The clean-span reuse rule is: a memcpy'd clean span is valid
**IFF** every handle `IsLive` **AND** its realization content-epoch is unchanged **AND** its baked-geometry hash
is unchanged. The validator (`CleanSpanWitness`) is the production-erased guard; this gate proves the validator
*actually catches* the two failure modes by **deliberately injecting them** and asserting the witness throws.

```csharp
// FAULT A — Bounds-move-without-PaintDirty (the critique's real bug, hardened-v1 §4.4):
//   move a node's Bounds but DO NOT set PaintDirty. A naive (handle,epoch)-only validator passes this
//   while shipping STALE device-space geometry. Our witness captures a BAKED-GEOMETRY hash and recomputes
//   dest rects from current Bounds[]/WorldTransform[] — so it MUST throw. The gate asserts it throws.
[Fact] void Witness_Catches_BoundsMove_Without_PaintDirty()
{
    var n = Scene.CreateNode(); RecordCleanFrame();
    Scene.Bounds[n].X += 10;                    // moved...
    /* deliberately NOT MarkPaintDirty(n) */
    Assert.Throws<CleanSpanStale>(() => RecordReusingCleanSpan());   // baked-geometry hash mismatch
}

// FAULT B — Mutate-without-dirty (the forgotten-epoch-bump, hardened-v1 §4.6):
//   mutate a realization's backing bytes WITHOUT routing through the single Mutate() epoch chokepoint.
//   The independent content-byte oracle (hash of the actual backing bytes, not the epoch counter) MUST
//   catch it even though the epoch counter is (wrongly) unchanged.
[Fact] void ContentByteOracle_Catches_Mutate_Without_EpochBump()
{
    var g = GlyphRunTable.Realize(run); RecordCleanFrame();
    UnsafeMutateBackingBytesBypassingMutate(g);  // simulates an untracked reference / forgotten bump
    Assert.Throws<CleanSpanStale>(() => RecordReusingCleanSpan());   // content-byte hash mismatch
}
```

The two oracles are deliberately *independent*: the epoch counter catches *tracked* mutation; the content-byte
hash catches *untracked* mutation and forgotten bumps. The baked-geometry hash catches Bounds-move-without-
PaintDirty. **The only residual is a double-fault** (two errors that cancel in both the geometry hash and the
content hash) — CI-caught only probabilistically, and named as such ([hardened-v1 §3 ledger §4.6 residual](../hardened-v1-plan.md)).
This gate also injects the `ImageRef` content-epoch path ([waveemusic §5](../app-requirements-waveemusic.md))
since the clean-span invariant was amended to cover `GlyphRunRef` AND `ImageRef`.

### 3.7 Data-race gate

The per-PR companion to the `seam.race` *spike* (§2.4). The spike is the long soak that gates the build-order
transition; this gate is the fast per-PR version that runs the deterministic schedule-fuzz for a bounded
frame-count (10^4, not 10^6) at the *current* committed quarantine value, plus the `ThreadGuard` confinement
assertions:

- **`ThreadGuard.AssertWriter`** fires a deterministic throw if any non-owning thread writes a confined
  structure (SceneStore column, RhiHandleTable, a ComPtr). This is the confinement-violation net — and because
  it is deterministic (not a probabilistic race detector), it is *reliable* where it covers, and *blind* where
  it does not. The doc states honestly: there is **no managed TSan**; the one lock-free retire-fence is covered
  by review + the soak's sampling, not by this gate.
- **Slot-recycle observation:** the bounded `Render_NeverObserves_RecycledSlot` check (the per-PR subset of the
  spike's assertion).

Until the seam lands (build steps 1–3), this gate runs in the degenerate quarantine=0 single-consumer
configuration and asserts the *shape* is correct (the publisher produces and the same thread consumes) — it is
green trivially, which is the point of build-order step 1 shipping single-thread-correct first.

---

## 4. The trust ring — `[Capability]` + analyzer (FG0001–3) + the runtime `IsSpikeCaller` assert

This is the mechanism that makes "claim ≠ property" a *compile error* rather than a code-review hope
([hardened-v1 §4.4/§4.5](../hardened-v1-plan.md)).

### 4.1 The attribute and the rings

```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly)]
public sealed class CapabilityAttribute : Attribute
{
    public CapabilityAttribute(Capability cap, TrustRing ring) { Cap = cap; Ring = ring; }
    public Capability Cap { get; }
    public TrustRing  Ring { get; }     // Experimental < Spiked < Proven
}

public enum TrustRing : byte { Experimental = 0, Spiked = 1, Proven = 2 }
```

A capability is the *named property a spike retires*: `Com.GeneratedAbi`, `Text.Shaping`, `Render.AnalyticAA`,
`Seam.Quarantine`, etc. A mechanism is tagged with the ring it has *earned* (per the trust ledger, §4.3).

### 4.2 The analyzer rules

| Rule | Severity | Meaning |
|---|---|---|
| **FG0001** | **Error** | Shipping code (a member NOT itself `[Capability(_, Experimental\|Spiked)]` and NOT in a spike assembly) calls a member marked `[Capability(_, Experimental)]`. *Shipping code is physically unable to compile against an Experimental capability* ([hardened-v1 §4.5](../hardened-v1-plan.md)). |
| **FG0002** | **Error** | A member's declared ring exceeds the ring justified by the trust ledger for its capability (e.g. tagged `Proven` but the ledger row says `Spiked`). The ledger is the source of truth; the attribute may not over-claim. |
| **FG0003** | **Warning→Error-in-CI** | A `[Capability(_, Spiked)]` capability is used outside a `[SpikeGated]` region without an explicit opt-in. `Spiked` means "a spike proved it once, nightly re-characterizes it, but it is not yet `Proven`" — usable, but flagged. |

The analyzer enforces FG0001/2 **at direct call sites**. Its honestly-stated gap ([hardened-v1 §4.4
"Transitive forwarding is an analyzer gap"](../hardened-v1-plan.md)): a *forwarding shim* (shipping method →
innocuous wrapper → Experimental capability) defeats static call-site analysis. That gap is closed at runtime,
not by the compiler:

### 4.3 The runtime `IsSpikeCaller` assert (closes the forwarding gap)

```csharp
[Conditional("FGVALIDATE")]
public static void AssertSpikeGated(Capability cap)
{
    // Walk the (FGVALIDATE-only) call stack; require that SOME frame is inside a spike-gated region for `cap`,
    // OR that `cap`'s ledger ring is Proven. A forwarding shim that reaches an Experimental/Spiked capability
    // from ungated shipping code fails HERE, in CI, even though FG0001 didn't see the transitive edge.
    if (!StackHasSpikeGate(cap) && TrustLedger.Ring(cap) < TrustRing.Proven)
        FgAssert.Fail($"capability {cap} reached from non-spike caller via forwarding (FG-runtime)");
}
```

This is the third leg: **analyzer (direct edges) + runtime `IsSpikeCaller` (transitive edges, CI-only) + the
trust ledger (the data both consult).** It is `[Conditional("FGVALIDATE")]`-erased from ship — consistent with
the doc's invariant that production safety is CI coverage. A weak spike sitting behind a `Proven` ring is the
remaining judgment residual (a human decided the spike was strong enough); the program names it and makes it a
reviewer responsibility, not a mechanism ([hardened-v1 §3 ledger §4.4 residual](../hardened-v1-plan.md)).

### 4.4 The trust ledger (the artifact coupling the two clocks)

A checked-in `trust-ledger.json`, one row per capability:

```json
{ "capability": "Com.GeneratedAbi", "ring": "Proven", "spike": "com.aot.roundtrip",
  "evidence_hash": "…sha256 over the spike's Green evidence…", "date": "2026-06-01", "owner": "…",
  "nightly_streak_required": 14, "nightly_streak_current": 31 }
```

- A spike's **Green** verdict is the *only* event that may write/raise a ring (CI enforces: the ledger commit
  must reference a Green spike run hash).
- FG0002 reads this file to bound declared rings.
- A spike going **Red** in nightly **demotes** the row, which then makes any shipping caller fail FG0001/2 on
  the next build — a regression in a *proven* property propagates back into a compile break, automatically.
- The `nightly_streak_required` is how `seam.race` gates the build-order flip ([hardened-v1 §6 step 5](../hardened-v1-plan.md)):
  `Seam.Quarantine` cannot reach `Proven` (and the quarantine constant cannot flip 0→derived) until the soak's
  streak is met.

---

## 5. WaveeMusic worst-case perf benchmarks + regression budgets

These are the **application-level** benches that turn [waveemusic §6 worst-cases](../app-requirements-waveemusic.md)
into ratcheted budgets. They are BenchmarkDotNet jobs (so they feed the §3.1 process-wide gen0/1/2 backstop)
*and* structural-ledger asserts (§3.4). Each has a **regression budget**: `bytes/op` and `ms/op` ceilings,
ratcheted *down*, raised only with reviewer-visible justification.

| Bench | Scenario ([waveemusic §6](../app-requirements-waveemusic.md)) | Budget asserts |
|---|---|---|
| `Scroll10kTracks` | 10k-track scroll-to-bottom; ~50 rows realized; in-window frames transform-only (P7) | In-window frame: Gen0==0, structural draw-calls flat (transform-only, no re-record); ENTER-boundary frame: bytes/op ≤ budget (50-key window diff + ≤5 Free/Create + the `ArrayPool<Element>` window, **not** the cap-32 `ObjectPool`); ms/op ≤ budget |
| `AlbumWall400` | Home album-art wall, 400 thumbs, horizontally virtualized | Resident art → atlas; structural draw-calls ≤ ~80; off-screen evicted (residency byte budget held); no `CreateTexture` in phases 6–13 (only `CopyBufferToTexture` into recycled bucket textures) |
| `RapidRecolor` | Rapid track-change recolor | Worker decode+extract off-thread (AllocScope-measured separately); ≤7 derives with cache-hit-instant on revisit; chrome-root PaintDirty only; recolor = opacity-only crossfade (assert **no atlas thrash** via structural atlas-upload count==0); `TargetGen` guard drops stale (assert stale results never paint) |
| `VideoPlusAnimUI` | 60fps video + animated UI | Our per-frame cost = scrubber re-record + hole-punch + `IVideoPresenter.Place`; assert video opcodes **never enter** the batcher/upload (structural: `DrawVideoCmd` count==expected, texture-upload count==0); hole + visual move in one DComp Commit |
| `ColdNowPlaying` | Cold now-playing open (first 512px decode + 60-DIP backdrop bake) | **Honestly a one-frame hitch in single-thread v1** — the bench measures it and asserts the *bound* (one frame, placeholder-tint→crossfade hides it), it does NOT assert zero. This is the bench that documents the v1 residual the seam fixes in v2 ([waveemusic §6, painpoints §3.2](../app-requirements-waveemusic.md)). |
| `LikedSongsRegroup50k` | 50k Liked-Songs regroup | v1 eats it as one N+1 frame with a skeleton; bench asserts the skeleton frame is bounded and the regroup does not exceed the huge-subtree Gen0 budget (the irreducible §3.1 residual, measured not denied) |

**Regression-budget mechanics:** budgets live in `perf.baseline.json`. A PR that regresses a budget fails the
gate with a before/after artifact. Budgets ratchet down as the renderer/virtualizer mature. The `ColdNowPlaying`
and `LikedSongsRegroup50k` budgets are explicitly *bound* budgets, not *zero* budgets — they encode the v1
honest residuals so a future seam landing can be measured as the categorical improvement it is.

---

## 6. Where each piece lands in the 13-phase loop, and on which thread

Validation guards are *interleaved* into the production phases (in `FGVALIDATE` builds only) — they do not add
phases. Mapping (phases + threads per [hardened-v1 §2.2](../hardened-v1-plan.md)):

| Phase | Thread | Validation guard active (FGVALIDATE only) |
|---|---|---|
| 1 pump | UI | — (device-lost reason read is production code) |
| 2 input | UI | edge-alloc *tolerated* (closures) — measured by BDN, not asserted-zero; **arena tentative-capture + resolution-trace capture (L2 §12.6); light-dismiss FSM observation (L4 §12.10); cursor-resolution along hit route (L10 §12.12); edge-autoscroll `ScrollOffset` write (L11 §12.12)** |
| 3 update-queue drain | UI | **lane-flush determinism trace (P1 §12.1); committed-frame-count for auto-batching (P2a §12.2)** |
| 4 render | UI | memo-skip 3-signal correctness checked by the reconcile fault-injection corpus (see §7); **`UseDerived` notify-on-change check (P6 §12.12)** |
| 5 reconcile | UI | `ThreadGuard.AssertWriter` (SceneStore single-writer); two-phase growth-lock asserts; **external-store snapshot-version tear check (P3 §12.4); discard-restart no-pre-PUBLISH-mutation assert (P4 §12.5); `WriteLayout` logical→physical RTL resolution check (L5 §12.8); `SelectionState` write (L1); type-flip handle-release net-zero (P8 §12.12)** |
| 6 layout | UI | `AllocScope(Layout)` Δ==0; **overlay flip/nudge placement assert (L4 §12.10); RTL `Bounds[]` mirror-equality (L5 §12.8)** |
| 6.5 layout-effects | UI | effect-timing assert (ratified phase 6.5; setState⇒mark-dirty+N+1, no sync re-loop); **carry-forward gen-stamp gate — drain only after complete reconcile (P5 §12.12); bottom-up child-before-parent drain (P9 §12.12)** |
| 7 animation | UI | `AllocScope(Animation)` Δ==0; Transform/Paint-dirty-only assert (never LayoutDirty); **spring/retarget numerical-stability battery (L7 §12.9); edge-autoscroll velocity write (L11)** |
| **PUBLISH (13a)** | UI | consume-gated `QuarantineLedger` tick assert; `_consumeIdx` acquire-before-pick assert; **lane-trace + frame-count commit observation (P1/P2a); external-store pre-PUBLISH re-check / demote-to-blocking (P3); Suspense atomic-swap frame-count==... (P2b)** |
| 8 record | RENDER | `CleanSpanWitness` (baked-geometry + epoch + content-byte oracle); `AllocScope(Record)`; **`DrawSelectionRectCmd` multi-rect structural assert (L1 §12.7); `AutoMirror` icon-flip record check (L9 §12.12); discard-restart byte-identical DrawList compare (P4 §12.5)** |
| 9 batch | RENDER | structural `BatchStats` ledger; painter-order last-writer assert; `AllocScope(Batch)` |
| 10 submit | RENDER | structural barrier-count ledger; `ComTracker` confinement (all ComPtr on render thread) |
| 11 present | RENDER | present-ack volatile-write ordering assert; `AllocScope(Present)` |
| 12 passive-effects | UI | effect-timing assert; setState⇒N+1; **carry-forward gen-stamp + bottom-up drain (P5/P9 §12.12)** |
| (UIA COM thread) | COM | **`ITextRangeProvider` Narrator-conformance + shared-read-side offset equality (L3 §12.7); virtualized-provider collection relations + realize-on-Navigate (L6 §12.11)** — Windows-gated halves |
| (workers) | WORKER | per-worker `AllocScope`; pure-function assert (touch NO SceneStore/RhiTable/fence) |

**Note:** the folded-gap guards above are the *interleaved `[Conditional]` guards* (run inside the loop during
`FGVALIDATE` test runs); the §12 **gates** that consume them are offline CI jobs, exactly like the §3 gates. The
new phase **3 (update-queue drain)** row reflects that the reserved phase-3 no-op is now a real lane-carrying
update queue (reconciler-hooks.md §7) — the gate observes its drain, it does not own it.

The **spikes** and the **gates** themselves run *outside* the frame loop (offline CI jobs); only the
`[Conditional]` guards above run *inside* it during `FGVALIDATE` test runs.

---

## 7. Reconcile / memo-skip correctness (a fault-injection corpus, not a gate of its own)

The 3-signal memo skip — `SelfTriggered || propsChanged || HasConsumedContextChanged(slot)` — is the highest-
leverage correctness surface in the reconciler ([reconciler-hooks.md](./reconciler-hooks.md), [hardened-v1
§4.4](../hardened-v1-plan.md)). The validation program owns a **fault-injection corpus** that asserts the two
ways this goes wrong:

1. **`SubtreeDirty` must NOT be a skip-decision input** (only traversal scope). The corpus includes a context
   consumer NOT on a `setState` path: if a (wrong) implementation gates on `SubtreeDirty`, the consumer is
   wrongly skipped and a dropped update is observable — the corpus asserts the update *is delivered*. This
   directly tests the folded critique that the `SubtreeDirty`-substitution made v1 *less* safe than the v1 it
   started from ([hardened-v1 §4.4](../hardened-v1-plan.md)).
2. **`DepKey` legality + reference-dep identity:** the corpus exercises the `GcDepTable` (reference deps
   compared by `ReferenceEquals`, NOT the illegal `[FieldOffset]` GC-ref/scalar union) and asserts a reference
   dep change re-runs the effect while an unchanged reference dep does not. It also asserts the pure-scalar
   16-byte `DepKey` round-trips without a `TypeLoadException` under AOT (this is partly a `com.aot.roundtrip`
   sibling — illegal layout would fail to load).

This corpus is consumed by the structural gate (§3.4) and the epoch fault-injection harness (§3.6) rather than
being a separate CI job — it is a *scenario library*, not a clock.

---

## 8. Failure & edge cases (what the program does NOT catch, stated honestly)

- **The one lock-free retire-fence is sampled, not proven.** No managed TSan exists. `seam.race` shrinks the
  lock-free surface to exactly one documented acquire/release fence and fuzzes it; a race *inside* that fence
  that the schedule-fuzz never hits is, in production, unguarded ([hardened-v1 §3 §5](../hardened-v1-plan.md)).
- **Epoch double-fault** (two errors cancelling in both the baked-geometry hash and the content-byte hash) is
  caught only probabilistically (§3.6).
- **Uncovered AA inputs are ungated** (§2.3) — the coverage manifest makes the gap visible, it does not close it.
- **A weak spike behind a `Proven` ring** is a human judgment the ledger records but cannot validate (§4.3).
- **Production has no guards.** Every assert is erased. A hazard not covered by a green gate or retired spike is
  unguarded in the customer's hands. This is *the* honest bottom line and it is restated wherever a reader might
  forget it.
- **Suppression floor:** if the FGCOM analyzers and FG0001–3 are suppressed under schedule pressure, the
  **non-suppressible** CI-RELEASE gates (`com.aot.roundtrip` behavioral roundtrip, COM net-refcount/leak,
  ABI-golden, `seam.race`) are the floor that still fires ([hardened-v1 §4.2/§4.5](../hardened-v1-plan.md)).
- **Folded-gap gate coverage caveats (§12), stated honestly:**
  - **`ITextRangeProvider`/virtualized-provider conformance (L3/L6) is split.** The *offset-equality / collection-
    relation-math / realize-on-Navigate* halves are portable and deterministic. The *real-UIA-client Narrator*
    conformance half is **Windows-gated** and runs in the nightly leg against a live UIA client — uncovered UIA
    methods are ungated (a coverage manifest, mirroring §2.3, lists which patterns the Narrator script exercises).
  - **Lane/discard-restart determinism (P1/P4) is `SeededScheduler`-deterministic, not exhaustively proven.** The
    gate fixes one seed per scenario; the **soak variant** sweeps seeds + the §2.4 reader-stall/worker space, but a
    schedule the fuzz never visits is, in production, unguarded — the same MANAGED-sampled honesty as `seam.race`.
  - **Spring numerical stability (L7) is gated over a swept `dt`+stiffness/damping grid, not the continuum.** A
    pathological stiff system outside the swept grid could blow up un-gated; the grid bounds are reviewer-visible.
  - **RTL mirror-equality (L5) gates the layout *geometry* mirror, not every glyph-level interaction** — glyph BiDi
    is covered by `text.conformance` (§2.1); composition bugs at their seam are caught only where the two corpora
    overlap (the §12.8 per-locale pixel golden is the overlap check).
  - **The app-author harness (`FluentGpu.Testing`, §11) exercises production paths but is not the engine's own
    gate** — an app author's missing test is their gap, not the engine's. The harness *enables* author coverage;
    it does not *enforce* it. The engine's own §12 gates are what guard the framework's folded features.

---

## 9. Cross-platform (macOS) boundary

The validation program is **mostly portable** and is the lever that *proves* portability:

- **Portable, runs on both OSes:** the Unicode-conformance half of `text.conformance`; the structural gate
  (`FluentGpu.Engine` Headless/Rhi/ + Headless/Pal/ are portable CPU/synthetic leaves — they are the macOS-portability proof in
  CI, [architecture-spec §3](../architecture-spec.md)); the alloc-tripwire + BDN backstop; the footprint
  ratchet (per-target baseline); the epoch fault-injection; the trust-ring analyzer (FG0001–3 are Roslyn,
  OS-agnostic); the reconcile/memo corpus.
- **Windows-gated (`#if WINDOWS`), and they grow a Metal/CoreText sibling at the macOS milestone:**
  - `com.aot.roundtrip` → a `metal.aot.roundtrip` against the generated `id<MTL*>`/`CAMetalLayer` bindings (the
    same header-independent behavioral-probe pattern: call `MTLDevice.maxBufferLength`, assert sane).
  - the DWrite-oracle half of `text.conformance` → a `CoreText` oracle (`Text.CoreText`, grayscale, no
    ClearType — the tolerance table gets a CoreText column).
  - `render.aa` → re-runs against `Rhi.Metal`; the **D2D-fallback dispositions become the Metal-milestone debt
    list** ([hardened-v1 §4.3 "per-primitive Metal-milestone debt list"](../hardened-v1-plan.md)) — the
    disposition table literally *is* the porting backlog.
- **Color invariant** is validated identically: the linear-blend + premultiplied-output golden corpus is the
  same; only the scan-out surface differs (`CAMetalLayer.colorspace` vs sRGB-RTV-over-UNORM), and the perceptual
  metric absorbs that ([architecture-spec §9](../architecture-spec.md)).

**The folded-gap gates (§12) are deliberately almost-entirely portable** — they consume the headless leaves and
the shared read-side, so they run on both OSes:
- **Portable, both OSes:** lane/transition determinism (P1), auto-batching (P2a), Suspense reveal (P2b), external-
  store tear (P3), discard-restart byte-identical golden (P4), gesture-arena determinism (L2), RTL mirror-equality
  geometry (L5), spring numerical stability (L7), overlay light-dismiss/focus-restore (L4), the *math/realization*
  halves of L6, the *selection-geometry / offset-equality* halves of L1/L3, and **the entire `FluentGpu.Testing`
  app-author harness** (§11 — it references only seams + headless leaves, so an app author's suite is cross-platform
  by construction).
- **Windows-gated (`#if WINDOWS`) → grow a `NSAccessibility`/CoreText sibling at the macOS milestone:** the
  real-UIA-client Narrator conformance half of L1/L3 → `NSAccessibility` `AXTextMarker`/`AXTextMarkerRange`
  conformance; the live-UIA virtualized-provider half of L6 → `NSAccessibility` `accessibilityVisibleChildren`/
  index-of-child realization; the per-locale RTL *pixel* golden of L5 re-runs against `Rhi.Metal` with the CoreText
  shaping column. The disposition/coverage manifests carry these as the macOS porting backlog, exactly like §2.3's
  `render.aa` D2D-fallback list.

Nothing in `FluentGpu.Validation` references a Windows concrete type except inside the `#if WINDOWS` spike
hosts, so the macOS build of the validation assembly compiles and runs the portable clock unchanged. The shipped
`FluentGpu.Testing` assembly contains **no** `#if WINDOWS` at all — it is unconditionally portable.

---

## 10. Zero-alloc & thread-confinement story (for the validation code itself)

- **`AllocScope` is a `ref struct`** — stack-only, zero heap; it cannot itself perturb the measurement it takes.
  The `AllocAggregator` uses per-`(phase, threadId)` slots (no contention, no boxing); its only allocation is a
  one-time startup array sized to `maxThreads × phaseCount`.
- **All guards are `[Conditional]`** — in ship they are not merely cheap, they are *absent* (call sites erased,
  methods trimmed). The footprint ratchet (§3.2) mechanically asserts this.
- **Thread-confinement:** the validation guards *assert* confinement (`ThreadGuard.AssertWriter`) but do not
  *violate* it — `ComTracker`/`AllocAggregator`/`StructuralLedger` are written only from the thread that owns
  the structure being checked (the aggregator's cross-thread sum uses worker-private slots reconciled at the
  frame boundary, an MPSC pattern, never a shared mutable counter). The `seam.race` soak is the *only* place
  that deliberately drives multiple threads, and it does so to test the production confinement, with each worker
  owning its own analysis state.
- **BDN benches** run in their own process (BenchmarkDotNet forks) so their harness allocation never pollutes
  the measured numbers — this is why the process-wide gen0/1/2 backstop is trustworthy.

---

## 11. The app-author test harness (`FluentGpu.Testing`) — L13 folded into core

> **Gap folded: L13 ("App-author test harness", was Tier-3 "defer").** The gap analysis observed the *hard part*
> already exists (the headless leaves `FluentGpu.Engine` Headless/Rhi/ + Headless/Pal/, the structural ledger, the alloc gates) and
> only a thin consumer API was absent. For a TIER-1 framework that absence is unacceptable: an app author cannot
> ship an accessible, interaction-correct app they cannot *test* off-screen in CI. So the harness is **core**, a
> shipped public assembly (`FluentGpu.Testing`), not a deferred fast-follow. It is the consumer-facing twin of the
> internal `Validation` machinery: same headless substrate, public ergonomic surface, **no** `[Conditional]` guards.

### 11.1 What it is, and the one rule it obeys

`FluentGpu.Testing` lets an app author mount a component subtree on the headless backend, drive a deterministic
frame clock, **simulate** pointer/key/gesture/IME/focus, **assert** semantics (the same projection the screen
reader sees), and capture **golden snapshots** (both structural command-ledger goldens *and* pixel goldens via
`FluentGpu.Engine` (Headless/Rhi/) WARP). The one rule: it composes **only public seams and the headless leaves** — it instantiates a
real `Hosting` composition root parameterized with `FluentGpu.Engine` (Headless/Pal/ and Headless/Rhi/), so the harness exercises the
*actual* 13-phase loop, reconciler, layout, arena, overlay manager, and UIA projection. It does **not** fork a
shadow engine. A test that passes here ran the production code paths with synthetic platform leaves.

```csharp
namespace FluentGpu.Testing;

public sealed class TestHost : IDisposable        // owns a headless Hosting root + a manual frame clock
{
    public static TestHost Mount(Element root, TestHostOptions? opts = null);   // builds Hosting on Headless leaves
    // ── deterministic clock ────────────────────────────────────────────────
    public void PumpFrame();                 // runs ONE full 13-phase loop (incl. PUBLISH→record→…→present on the
                                             //   render thread, then joins it) — synchronous, deterministic
    public void PumpFrames(int n);
    public void AdvanceTime(TimeSpan dt);     // advances the DrivenClock / animation clock by an exact delta
    public void DrainTransitions();           // flush all transition-lane work to quiescence (P1, §12.1)
    public void DrainAsync();                 // await all pending UseResource/Suspense promises, then PumpFrame
    // ── queries (read-only views over committed scene + a11y projection) ────
    public TestElement Find(string automationId);
    public TestElement FindByText(string text);
    public IReadOnlyList<TestElement> FindAll(Predicate<TestElement> p);
    public SemanticsSnapshot Semantics();     // the merged/compacted UIA tree the AT would see (input-a11y.md)
    public StructuralSnapshot Structure();    // the §3.4 StructuralLedger view: draw-calls/batches/barriers/bounds
    public FrameImage Pixels();               // WARP-rendered BGRA8 framebuffer (for pixel goldens, §3.3 metric)
}
```

`TestHostOptions` carries the knobs an interaction test needs deterministically: `Dpi`, `Size`, `FlowDirection`
(for the RTL gate, §12.7), `Locale` (for the i18n gate, §12.7), `ReducedMotion`, `HighContrast`, and a
**`SeededScheduler`** (a fixed-seed schedule for any transition/worker fan-out so async tests are reproducible).

### 11.2 Simulate — pointer / key / gesture / IME / focus

The harness writes synthetic events into the **same `InputEventRing`** the real `Pal` would (input-a11y.md owns
the ring shape; the harness is just another producer). Gestures go through the **real gesture arena** (input-a11y.md
§5.4) so an author tests *actual* disambiguation, not a mock:

```csharp
public readonly struct Pointer                     // a fluent pointer-sequence builder
{
    public Pointer Down(PointF p);  public Pointer MoveTo(PointF p, int steps = 1);
    public Pointer Up();            public Pointer Wheel(float dx, float dy);
    public Pointer Pen(float pressure);  public Pointer Touch(int id);
}
public void Tap(TestElement e);                     // Down+Up at e's center, pumps to arena resolution
public void Drag(PointF from, PointF to, int steps = 8);
public void Gesture(Action<Pointer> script);        // arbitrary multi-pointer/move script → arena → recognizers
public void Key(VirtualKey k, KeyModifiers m = 0);  // through FocusEngine + accelerator table
public void TypeText(string s);                     // through the IImeSession seam (composition + commit)
public void FocusNext();  public void FocusPrev();  // Tab nav through the real FocusEngine
public void SelectText(TestElement e, int start, int len);  // drives SelectionState via the arena (L1)
```

Every simulate call **pumps frames to quiescence** by default (arena resolution defers across pointer-move
frames, §12.2; light-dismiss is an FSM; transitions need draining) so the author writes `Tap(button)` not a
manual pump loop — but each has a `noPump:true` overload for tests that must observe an intermediate frame (e.g.
"tentative capture not yet resolved", the §12.2 determinism gate).

### 11.3 Assert-semantics — the accessibility surface as a first-class assertion

`SemanticsSnapshot` is the **exact merged/compacted UIA projection** (input-a11y.md §11), serialized to a stable
tree the author asserts against. This is what makes "accessible-by-default" *testable by the author*, not just
linted by `AccessibilityScanner`:

```csharp
var s = host.Semantics();
s.Node("track-12").AssertRole(UiaControlType.ListItem)
                  .AssertName("Bohemian Rhapsody")
                  .AssertPositionInSet(12).AssertSizeOfSet(50_000)   // L6 collection relations
                  .AssertDescribedBy("now-playing-hint");            // L6 DescribedBy
host.AssertNoAccessibilityViolations();   // runs the AccessibilityScanner ruleset as a hard assert (name/role/
                                          //   contrast/focusability), failing the author's test, not just DEBUG-linting
```

`AssertNoAccessibilityViolations()` reuses the existing DEBUG `AccessibilityScanner` ruleset (gap analysis §6,
"DevX is not bare") but promotes it from a lint to a **fail-the-test assertion** that an app author can wire into
their own CI — closing the "world-class engine validation, no consumer-facing API" gap directly.

### 11.4 Golden snapshots — structural and pixel, author-owned

The author gets the *same two golden mechanisms* the engine's own gates use (§3.3 pixel / §3.4 structural),
exposed as ergonomic assertions with author-local baseline files:

```csharp
host.Structure().MatchGolden("album-wall.structural.json");  // draw-call/batch/barrier/Bounds exact integers
host.Pixels().MatchGolden("album-wall.png", metric: Ciede2000(tol: DispositionTable.Default));  // perceptual
```

Author goldens follow the engine's **no-silent-rebaseline** discipline: a mismatch emits a side-by-side diff
artifact; rebaselining requires an explicit `--update-goldens` opt-in flag, mirroring §3.3. The author harness
ships a `dotnet fluentgpu test --update-goldens` entry point.

### 11.5 Interaction testing — the composed scenarios

The harness exposes scenario helpers for the interactions the folded Tier-1 gaps introduce, so an author tests
the *contract*, not the mechanism:

| Helper | Exercises (gap) | What it asserts |
|---|---|---|
| `host.StartTransition(() => setState(...))` then `DrainTransitions()` | P1 lanes | urgent input stays responsive while the transition coalesces; final state correct |
| `host.Batch(() => { a(); b(); })` | P2a auto-batching | both setStates fold into **one** committed frame (assert frame count == 1) |
| `await host.RevealAsync(boundary)` | P2b Suspense | fallback shown while pending; atomic content swap on ready; keep-stale during transition |
| `host.Optimistic(() => like(track))` | P7 useOptimistic | optimistic value visible immediately; rollback on simulated failure |
| `host.LightDismiss(overlay)` | L4 overlay | outside-press/Esc dismisses; focus restored to the anchor |
| `host.NavigateTo("track-9999")` | L6 virtualized-a11y | provider *realizes* the off-screen row on AT navigation |

### 11.6 Zero-alloc / thread / macOS posture of the harness

- The harness drives the **real** loop, so its frames inherit the production zero-alloc posture; the harness's own
  query/serialize code is **not** on the hot path (it runs between `PumpFrame` calls, at quiescence) so it may
  allocate freely without perturbing what the author is measuring. An author who *wants* to assert frame allocation
  uses `host.AssertSteadyStateGen0Zero()` which wraps the §3.1 BDN-style measurement.
- **Thread:** `PumpFrame` runs the render thread and **joins it before returning**, so the harness presents a
  single-threaded API over the real multi-thread loop — the author never races. `DrainTransitions`/`DrainAsync`
  quiesce all worker/transition fan-out before returning. The `SeededScheduler` makes any fan-out order
  deterministic for the determinism gates (§12.1/§12.4).
- **macOS:** `FluentGpu.Testing` is **fully portable** — it references only `FluentGpu.Engine` (Headless/Pal/ and Headless/Rhi/) and the
  seams, never a Windows concrete. An app author's test suite runs identically on the macOS CI leg. (The DWrite/
  CoreText *shaping* oracle stays in `Validation`'s Windows-gated spike host, not here.)

---

## 12. Always-on gates for every folded gap (each mapped to the gap it guards)

> Every gap the framework now folds into **core** earns a **blocking, always-on per-PR gate** here — because of
> the doc's load-bearing invariant (§0: *production has no guards; safety == CI coverage*). A folded feature with
> no gate is, in the customer's hands, an unverified claim. Each gate below names the P*/L* gap it guards, the
> deterministic oracle it uses, and the thread/phase it observes. All run in the **portable headless pass** unless
> marked Windows-gated, and all consume the new public scene columns / opcodes / hooks **by reference** (this doc
> never re-defines them; ownership per the directive's map and §"Implemented from the gap analysis" below).

These are **GATES** (the per-PR regression clock, §3), not SPIKES — they guard already-folded core features, not
unknown-retirement. Where a gate also needs a long soak to promote a `[Capability]` ring (e.g. lane scheduling
determinism under worker fan-out), the soak variant is noted and feeds the trust ledger (§4) exactly like §2.4.

### 12.1 Lane / transition scheduling determinism — guards **P1**

**Property:** for a fixed input schedule + fixed `SeededScheduler`, the sequence of *committed frames* (which
lanes flushed in which frame, in what order) is **byte-identical run-to-run**. Lanes decouple ordering from
batching (reconciler-hooks.md §6–7 owns the lane bitmask + update queue); this gate proves the *executor*
(`RenderPriorityPolicy` recast as lane executor) is deterministic and that urgent lanes never starve behind a
transition.

```csharp
[Fact] void Lanes_Deterministic_And_UrgentNeverStarvedByTransition()
{
    using var h = TestHost.Mount(App(), new(){ SeededScheduler = Seed(42) });
    h.StartTransition(() => h.Find("list").SetState(Regroup50k));   // transition lane
    h.Key(VirtualKey.Down);                                         // urgent (input) lane mid-transition
    var trace = h.CaptureLaneTrace(() => h.DrainTransitions());     // ordered (frame#, lanesFlushed) list
    Assert.Equal(GoldenLaneTrace("regroup-with-input.lanes.json"), trace);  // deterministic, exact
    // urgent commit must land BEFORE the transition fully drains (no priority inversion):
    Assert.True(trace.FrameOf(Lane.Input) < trace.LastFrameOf(Lane.Transition));
}
```

- **Oracle:** an exact golden over the **lane trace** (a deterministic ordered list of `(frameIndex, laneMask)`),
  not pixels — the strongest, cheapest regression net.
- **Thread/phase:** observes the **PUBLISH(13a)** boundary on the UI thread (the lane-flush decision) plus the
  phase-3 drain (reconciler-hooks.md). Portable.
- **Soak variant (feeds ledger):** under worker fan-out + `ReaderStallFrames` sweep (the §2.4 `seam.race` param
  space), assert the lane trace is *still* schedule-deterministic — promotes a `Sched.Lanes` capability ring.

### 12.2 Automatic-batching semantics — guards **P2a**

**Property:** all `setState`s within one event handler **and across an `await` continuation** fold into exactly
**one** committed frame (React-18 batching). The reserved phase-3 no-op is now a real per-update enqueue
(reconciler-hooks.md §7); this gate is the semantic proof.

```csharp
[Fact] void AllSetStates_In_Handler_And_After_Await_Batch_To_One_Frame()
{
    using var h = TestHost.Mount(Form());
    int frames = h.CountCommittedFrames(() => h.Tap(h.Find("submit")));  // handler does 3 setStates
    Assert.Equal(1, frames);                                             // batched, not 3
    frames = h.CountCommittedFrames(() => h.DrainAsync());               // post-await does 2 more setStates
    Assert.Equal(1, frames);                                             // also one frame (the async-continuation case)
}
```

- **Oracle:** **committed-frame count** (the structural ledger increments a frame counter at PUBLISH). Exact integer.
- **Edge case asserted:** a `setState` from *different* components in the same handler still batches (the gap
  analysis's explicit failure mode "multiple setStates from different components apply in-place").
- **Thread/phase:** phase 2/4 (enqueue) → phase 3 (drain) → PUBLISH. Portable.

### 12.3 Suspense reveal golden + keep-stale — guards **P2b**

**Property:** a `Suspense`/`Boundary` element (reconciler-hooks.md new §) (a) mounts fallback as a unit while a
descendant `UseResource` is pending, (b) **atomically** swaps to content on ready (no partial reveal frame), (c)
during a transition (P1 lane) **keeps already-revealed content visible** instead of flashing the fallback, and (d)
supports **nested** progressive reveal.

```csharp
[Fact] async Task Suspense_AtomicReveal_And_KeepStale_DuringTransition()
{
    using var h = TestHost.Mount(NowPlaying());
    h.Structure().MatchGolden("nowplaying.fallback.json");     // (a) fallback as a unit
    await h.RevealAsync(h.Find("art-boundary"));               // resolve the promise
    h.Structure().MatchGolden("nowplaying.content.json");      // (b) atomic swap — assert NO intermediate frame
    Assert.Equal(0, h.PartialRevealFrameCount);                //     showed both fallback+content
    // keep-stale: change track inside a transition; old art must stay until new is ready
    h.StartTransition(() => h.Find("art-boundary").SetState(NewTrack));
    h.PumpFrame();
    h.FindByText(/*old*/ "Previous Album").AssertVisible();    // (c) stale kept, no fallback flash
}
```

- **Oracle:** structural goldens at each reveal state + a `PartialRevealFrameCount==0` assertion (atomicity) +
  a keep-stale visibility assertion. The keep-stale path rides the `DetachedAnim` slab (backdrop-effects-animation.md);
  this gate asserts the committed visual survives the swap without relayout (structural `LayoutDirty==false`).
- **Thread/phase:** reconcile (5) + record (8). Portable.

### 12.4 External-store tear test — guards **P3**

**Property:** the `UseObservable`/`UseResource` external-store contract (threading-render-seam.md §12 / reconciler-
hooks.md data-hooks own the `(subscribe, getSnapshot)` shape) is **tear-free**: within one committed frame, every
consumer of a store reads the **same snapshot version**, even if the store mutates mid-reconcile; on a detected
mismatch the frame **demotes to a blocking single pass** (the `useSyncExternalStore` re-check).

```csharp
[Fact] void ExternalStore_NoTear_Under_MidReconcile_Mutation()
{
    using var h = TestHost.Mount(TwoConsumersOfStore(store), new(){ SeededScheduler = Seed(7) });
    h.InjectStoreMutationDuringReconcile(store, atDescentIndex: 1);  // mutate AFTER consumer A read, BEFORE B
    h.PumpFrame();
    var a = h.Find("consumer-a").ReadSnapshotVersion();
    var b = h.Find("consumer-b").ReadSnapshotVersion();
    Assert.Equal(a, b);                                              // no tear: identical version
    Assert.True(h.LastFrameDemotedToBlocking);                      // the pre-PUBLISH re-check fired
}
```

- **Oracle:** snapshot-version equality across all consumers in a frame + a `LastFrameDemotedToBlocking` flag the
  pre-PUBLISH re-check sets. Deterministic via the seeded mid-reconcile injection.
- **Why it matters even in single-pass v1:** the gap analysis (§5.3) calls this *dormant in single-pass, latent
  the instant slicing flips on*. The gate is **always on** so the contract is enforced from day one and a future
  slicing flip cannot silently introduce a tear. This is the direct analog of the §3.6 epoch fault-injection
  (inject the hazard, assert the guard catches it).
- **Thread/phase:** reconcile (5) → PUBLISH(13a). Portable.

### 12.5 Discard-restart byte-identical-to-single-thread golden — guards **P4**

**Property:** an **interruptible discard-and-restart** reconcile (abandon partial work, restart from root — safe
*because* the render phase is pure) produces a committed `DrawList` that is **byte-identical** to the single-thread,
no-interruption golden for the same input. This extends the §2.4 `seam.race` `ConcurrentRecord_MatchesSingleThreadedGolden`
assertion to the discard-restart case the gap analysis (P4) names, and enforces the precondition **no `ISceneBackend`
mutation is observable before PUBLISH**.

```csharp
[Fact] void DiscardRestart_Produces_ByteIdentical_DrawList()
{
    using var h = TestHost.Mount(BigTree(), new(){ SeededScheduler = Seed(13) });
    var golden = h.RecordSingleThreadGolden(input);                 // no interruption baseline
    h.InjectReconcileInterrupt(afterDescentIndex: 37);              // force a discard-restart-from-root
    h.PumpFrame();
    Assert.Equal(golden.Bytes, h.LastCommittedDrawList.Bytes);      // byte-identical
    Assert.Equal(0, h.PrePublishSceneBackendMutationCount);         // precondition: nothing observable pre-PUBLISH
}
```

- **Oracle:** byte-equality of the committed `DrawList` arena + a `PrePublishSceneBackendMutationCount==0` assertion
  enforced by the `ThreadGuard.AssertWriter` discipline (threading-render-seam.md §12, §18). This is the gap
  analysis's explicit P4 recommendation ("enforce precondition now") turned into an always-on gate.
- **Thread/phase:** reconcile (5, interruptible) → PUBLISH(13a) → record (8). Portable.
- **Soak variant:** repeated random interrupt points across a fuzz corpus; first-divergence-frame must be ∞,
  feeding the same `Seam.Quarantine`/`Sched.Lanes` ledger discipline.

### 12.6 Gesture-arena resolution determinism — guards **L2**

**Property:** for a fixed multi-pointer script, the gesture arena (input-a11y.md §5.4 owns the coordinator) resolves
to the **same winner** every run, with the documented semantics: eager-win, pointer-up sweep, hold/release for
double-tap, `GestureArenaTeam` for selection, and **tentative capture** (capture is provisional until resolution).
This gate also pins the *capture/`Handled` semantics shift* the gap analysis flags as "not purely additive."

```csharp
[Fact] void Arena_DragInScroll_Deterministic_Winner_And_TentativeCapture()
{
    using var h = TestHost.Mount(ScrollableRowWithSwipe());
    // ambiguous gesture: vertical drag (scroll) vs horizontal swipe (row action) compete
    h.Gesture(p => p.Down(rowCenter).MoveTo(rowCenter + new PointF(2, 30), steps: 6).Up(), noPump: true);
    Assert.True(h.Arena.CaptureIsTentative);                        // capture provisional mid-move (the shift)
    h.PumpFrames(3);                                                // let the arena resolve across move frames
    Assert.Equal(Recognizer.Scroll, h.Arena.Winner);               // deterministic: vertical wins
    Assert.Equal(GestureArenaTrace("drag-in-scroll.arena.json"), h.Arena.ResolutionTrace);  // exact
}
```

- **Oracle:** exact golden over the **arena resolution trace** (ordered accept/reject events) + the winner +
  a `CaptureIsTentative` assertion at the intermediate frame (proving the capture-becomes-tentative shift).
- **Thread/phase:** phase 2 (input) across multiple frames (resolution defers). Portable.
- **Edge cases:** simultaneous eager-win on two pointers; loser-rejection delivers no stray `Handled`; arena
  sweep on pointer-up with no winner falls back deterministically.

### 12.7 Text-selection + `ITextRangeProvider` Narrator conformance — guards **L1 / L3**

**Two coupled gates over the single shared read-side** (`GetSelectionRects`/`HitTestTextPosition`, text.md §8) that
backs both on-screen selection and the UIA range provider:

- **L1 on-screen selection golden:** drive `SelectText` through the arena (L1 selection-drag), assert the
  `SelectionState` column (anchor/extent/affinity; scene-memory.md owns the column storage, text.md the semantics)
  produces the correct multi-rect highlight via `DrawSelectionRectCmd` (gpu-renderer.md owns the opcode shape).
  BiDi multi-rect + affinity at run boundaries is asserted structurally (exact rect list) **and** perceptually.

```csharp
[Fact] void Selection_BiDi_MultiRect_And_Affinity()
{
    using var h = TestHost.Mount(Label("Hello عربي world"));        // mixed LTR/RTL
    h.SelectText(h.Find("lbl"), start: 3, len: 8);                  // crosses the bidi boundary
    var rects = h.Structure().SelectionRects("lbl");
    Assert.Equal(GoldenRects("bidi-selection.rects.json"), rects);  // exact multi-rect (one per visual run)
    h.Pixels().MatchGolden("bidi-selection.png", Ciede2000(DispositionTable.Default));
}
```

- **L3 `ITextRangeProvider` Narrator conformance:** assert the UIA Text document surface (input-a11y.md §11.4 owns
  the provider) is **conformant** to the patterns Narrator actually calls — `GetSelection`, `RangeFromPoint`,
  `GetEnclosingElement`, `Move`/`MoveEndpointByUnit` (char/word/line/paragraph), text attributes — and that read-by-
  line/word and caret-tracking return the **same offsets** the on-screen `SelectionState` uses (single read-side
  invariant). The conformance corpus is the curated Narrator-interaction script set.

```csharp
[Fact] void TextRangeProvider_Conformance_SharesReadSide_With_OnScreenSelection()
{
    using var h = TestHost.Mount(Paragraph(loremBidi));
    var tp = h.Semantics().TextPattern("para");
    tp.AssertMoveByUnit(TextUnit.Word, +3, expectedOffset: 17);     // Narrator read-by-word
    tp.AssertRangeFromPoint(p, expectedOffset: 17);
    h.SelectText(h.Find("para"), 17, 5);
    Assert.Equal(h.SelectionState("para").Range, tp.GetSelection().Range);  // ONE read-side, no drift
}
```

- **Thread/phase:** L1 selection writes at phase 5/6 (SelectionState column) and records at phase 8
  (`DrawSelectionRectCmd`); L3 provider runs on the UIA COM thread (`UseComThreading`). The provider half is
  **Windows-gated** (`#if WINDOWS`, real UIA client harness in the nightly leg); the offset-equality + selection-
  geometry halves are **portable** (they read the shared read-side directly, no UIA client needed). The macOS
  sibling re-targets `NSAccessibility` AXTextMarker (§9).

### 12.8 RTL mirroring golden at multiple locales — guards **L5**

**Property:** with `FlowDirection` resolved logical→physical at the `WriteLayout` boundary (layout.md §4.1 owns the
resolution; the ported Yoga `CalculateLayoutImpl` stays *physical* and bit-for-bit with the §5 golden-parity gate),
a tree laid out RTL is the **exact mirror** of its LTR layout for row direction, justify/align start↔end, and
`Edges4` margin/padding — and glyph BiDi (already solid) composes correctly on top.

```csharp
[Theory]
[InlineData("en-US", FlowDirection.LeftToRight)]
[InlineData("ar-SA", FlowDirection.RightToLeft)]
[InlineData("he-IL", FlowDirection.RightToLeft)]
[InlineData("fa-IR", FlowDirection.RightToLeft)]   // RTL + Arabic-Indic digits (composes with L9)
void Rtl_Layout_Is_Exact_Mirror_At_Locale(string locale, FlowDirection dir)
{
    using var h = TestHost.Mount(Toolbar(), new(){ Locale = locale, FlowDirection = dir });
    var bounds = h.Structure().AllBounds();
    if (dir == FlowDirection.RightToLeft)
        Assert.Equal(MirrorX(GoldenBounds("toolbar.ltr.bounds.json")), bounds);  // physical mirror, exact
    h.Pixels().MatchGolden($"toolbar.{locale}.png", Ciede2000(DispositionTable.Default));
}
```

- **Oracle:** exact `Bounds[]` mirror-equality (the structural twin) — the strongest RTL net because it is
  deterministic — plus a per-locale pixel golden (catches icon/`AutoMirror` mistakes, L9). The mirror-equality
  assertion also **protects the Yoga golden-parity contract**: because resolution happens at `WriteLayout` (not
  inside the ported core), the LTR golden bytes are unchanged, and this gate asserts the RTL output is exactly
  their X-mirror — so a regression that leaked direction into the ported numerics shows up here.
- **Thread/phase:** `WriteLayout` (reconciler phase 5 boundary) → layout (6) → record (8). Portable.

### 12.9 Spring / retarget numerical stability — guards **L7**

**Property:** the spring integration **mode** on `AnimTrack` (a velocity field carried on the track; Tick
integrates — backdrop-effects-animation.md owns the integrator) is **numerically stable** across the frame-time
range, **never overshoots into NaN/Inf**, **settles** within a bounded frame count, and **retarget mid-flight
seeds velocity from the instantaneous value** (no discontinuity). `animateContentSize` seeded from the
double-buffered prev-frame `WorldBounds[]` produces a continuous tween.

```csharp
[Fact] void Spring_Retarget_Is_Stable_Continuous_And_Settles()
{
    using var h = TestHost.Mount(SpringBox());
    h.Find("box").SetTarget(x: 300);
    var trace = new List<float>();
    for (int i = 0; i < 600; i++) { h.AdvanceTime(TimeSpan.FromMilliseconds(16.67)); trace.Add(h.Find("box").X); }
    Assert.All(trace, v => Assert.True(float.IsFinite(v)));         // never NaN/Inf
    h.Find("box").SetTarget(x: 50);                                 // RETARGET mid-flight
    float vBefore = h.Find("box").Velocity;                        // velocity must be carried, not reset
    h.AdvanceTime(TimeSpan.FromMilliseconds(16.67));
    Assert.True(MathF.Abs(h.Find("box").Velocity - vBefore) < kSeedTolerance);  // seeded from instantaneous
    Assert.True(SettlesWithin(trace, frames: 90));                 // bounded settle
}
```

- **Oracle:** a **numerical-stability assertion battery** over the position/velocity trace: finite at every step,
  monotone-decreasing settle energy, bounded settle frame-count, velocity-continuity across retarget, **and** a
  determinism check (same target+timestep ⇒ identical trace). Swept across `dt ∈ {8.33, 16.67, 33.3} ms` and a
  stiffness/damping grid (the spring params) to catch stiff-system blow-up at large `dt`.
- **Thread/phase:** phase 7 (animation), Transform/Paint-dirty-only (never LayoutDirty) — the §6 phase-7 assert
  already covers the dirtiness; this gate covers the *numerics*. Portable.

### 12.10 Overlay light-dismiss / focus-restore — guards **L4**

**Property:** the Overlay/Portal manager (input-a11y.md owns the manager; layout.md owns placement geometry)
composes the existing overlay-root + z-stack + modal focus-trap into a correct **light-dismiss FSM** (outside-press
/ Esc / focus-loss) with **anchor-relative placement-with-flip/nudge** and **focus push-on-open / restore-on-close**.

```csharp
[Fact] void Overlay_LightDismiss_FlipNudge_And_FocusRestore()
{
    using var h = TestHost.Mount(PageWithMenuButton());
    h.Focus(h.Find("menu-btn"));
    h.Tap(h.Find("menu-btn"));                                       // open flyout
    Assert.True(h.Find("flyout").IsVisible);
    h.Structure().AssertPlacement("flyout", flippedAbove: true);     // near-bottom anchor ⇒ flip-up + nudge in-bounds
    Assert.Equal("flyout-first-item", h.FocusedAutomationId);        // focus pushed into the scope
    h.Tap(h.PointOutside());                                         // light-dismiss via outside-press (through arena)
    Assert.False(h.Find("flyout").IsVisible);
    Assert.Equal("menu-btn", h.FocusedAutomationId);                 // focus RESTORED to anchor
    // and Esc path:
    h.Tap(h.Find("menu-btn")); h.Key(VirtualKey.Escape);
    Assert.Equal("menu-btn", h.FocusedAutomationId);
}
```

- **Oracle:** visibility + placement assertions (flip/nudge geometry from layout.md) + **focus-restore equality**
  (the focused automation id returns to the opener) + light-dismiss via all three triggers (outside-press routed
  **through the arena**, Esc, focus-loss). Stacked-overlay z-order is asserted via the structural ledger paint order,
  including the focus-ring composition guarantee: the emitted **`DrawFocusRingCmd`** (the production focus-visual
  opcode, shape+raster owned by gpu-renderer.md §3.6/§4.4) paints above the page-beneath the overlay and above its
  own `DrawScrimCmd` (never dimmed by its own modal scrim).
- **Thread/phase:** phase 2 (input/light-dismiss FSM) + layout (6, placement) + focus engine. Portable.

### 12.11 Virtualized-a11y realization — guards **L6**

**Property:** the UIA provider for a virtualized collection exposes correct **collection relations**
(`SetSize`/`PositionInSet`/`Level` — scene-memory.md owns the columns, input-a11y.md the provider semantics) for
items the virtualizer knows the index+count of, **and** the **virtualized-provider realization contract** holds: an
AT `Navigate` to an off-screen item **causes realization** (scroll-to-realize) rather than hitting the ~50-realized-
row wall.

```csharp
[Fact] void VirtualizedA11y_Relations_And_RealizeOnNavigate()
{
    using var h = TestHost.Mount(TrackList(count: 50_000));         // ~50 rows realized
    var s = h.Semantics();
    s.Node("row-12").AssertPositionInSet(13).AssertSizeOfSet(50_000); // relation correct without realizing all
    Assert.False(h.IsRealized(9999));                                // off-screen, not realized yet
    h.NavigateTo("row-9999");                                        // AT navigation past the realized window
    Assert.True(h.IsRealized(9999));                                 // realization CAUSED by navigate (the contract)
    h.Semantics().Node("row-9999").AssertName("Track 10000").AssertPositionInSet(10_000);
}
```

- **Oracle:** collection-relation assertions on a *non*-realized item (count/index come from the virtualizer, not
  from a realized node) + an `IsRealized(index)` transition assertion proving Navigate *caused* realization. This
  closes the gap analysis's "AT hits a wall — no scroll-to-realize-on-navigate contract."
- **Thread/phase:** UIA COM thread (provider) coordinating with the virtualizer's realize-window (phase 5/6); the
  realization it triggers is consume-gated (`DestroyNode`/`CreateNode` discipline, virtualization.md). The provider
  half is **Windows-gated**; the relation-math + realization-trigger halves are portable (driven through the
  harness's `NavigateTo` against the real provider state machine).

### 12.11bis Butter-smooth resize gates (`FluentGpu.VerticalSlice` — `ButterSmoothResizeChecks`)

Headless PAL models Win32 modal policy (`Composited`, `SizedInModalLoop`, `InModalLoop`). Wavee breakpoint
hysteresis is covered separately in `app/Wavee.Tests` (`DetailLayoutBreakpointTests`).

| Gate | Assert |
|---|---|
| `RZ-DEFER.` | Composited + modal edge grow → viewport unchanged; exit modal → final size applied |
| `RZ-LIVE.` | Non-composited + modal → keep-alive applies new client size (no defer bug) |
| `RZ-SETTLE.` | Settle presents; span reuse disabled for `Resize`; `HintSettlePresent` honored |
| `RZ-THROTTLE.` | `ModalPaintThrottle`: 10 steps @8 ms → ≤4 paints; 40 ms gap → paint |
| `RZ-MOVE.` | Composited move (`SizedInModalLoop=false`) → ambient modal tick submits; no `ModalPaint` span disable |
| `RZ-MOVE2.` | Composited edge resize (`SizedInModalLoop=true`) → ambient-only tick idle-skips |
| `RZ-TIER.` / `RZ-MODAL.` | Detail resize flicker contract (tier remount + modal warming refill); composited move modeling |

On-device: screen-record Wavee (composited) + gallery (non-composited) per `docs/plans/butter-smooth-resize-v2.md` §2.2.

### 12.12 Companion folded-gap gates (P5, P6, L8, L9, L10, L11, P8, P9)

These folded gaps get gates too — leaner, but always-on:

| Gate | Guards | Oracle (deterministic) | Thread/phase |
|---|---|---|---|
| **Carry-forward effect gen-stamp** | **P5** | inject a partial reconcile spanning a frame boundary; assert effects **drain only after a complete reconcile** and a stale gen-stamped effect is skipped, never run against a re-rendered owner | reconcile (5) → drain (6.5/12) |
| **`UseDerived`/`UseContextSelector` notify-on-change** | **P6** | assert a derived node notifies **iff its projected result changes** (e.g. `Epoch`-as-`uint` collapse); an unchanged projection re-read does not re-render the consumer | reconcile (4/5) |
| **Type-flip remount releases retained handles** | **P8** | A→B type-flip; assert atlas pins / present-tree nodes / DComp slabs / `DrivenClock`s are released at the **same sync-cleanup point** as the recycle path (`ComTracker`/handle net-zero for the flipped subtree) | reconcile (5) cleanup |
| **Bottom-up intra-phase effect drain** | **P9** | assert layout/passive effects drain **child-before-parent** (or that descent-order is provably safe given sync unmount-cleanup ran in phase 5) — a child effect never reads a parent's freed GPU/native handle | effects (6.5/12) |
| **Control-kit accessible-by-default** | **L8** | each control in `controls.md` runs `AssertNoAccessibilityViolations()` + its pattern (Checkbox⇒Toggle, Slider⇒RangeValue, etc.) + a structural golden; **sequenced after** L1/L2/L4/L6 gates are green (controls are the integration test for those seams) | full loop |
| **Locale value-formatting + RTL icon mirror** | **L9** | format number/date/currency at the edge → `StringId` (assert **no string alloc on the paint path**); `AutoMirror` icon flips with `FlowDirection` (composes with §12.8 multi-locale golden) | edge (pre-reconcile) + record (8) |
| **Cursor resolution + arbitration** | **L10** | move pointer along a hit route; assert the resolved `CursorId` (I-beam over selectable text, resize over splitters) and that `Pal.SetCursor` (pal-rhi.md seam) is called exactly when it changes (no per-frame churn) | phase 2 (input), along the L2 hit route |
| **Edge-autoscroll driver** | **L11** | selection-drag / drag-reorder past the viewport edge; assert the shared edge-autoscroll driver writes `ScrollOffset` with velocity-from-edge in phase 2/7 and **stops** at content bounds | phase 2/7 |

Each row is a real `[Fact]`/`[Theory]` in the gate suite; the table is a map, not a deferral. L8's control gates
land **with each control** in `controls.md` (the same no-silent-rebaseline golden discipline), and are explicitly
sequenced so a control cannot bake in a display-only / no-arena / direction-blind assumption — the very risk the
gap analysis names.

### 12.13 Gate → gap → owning-doc traceability matrix

The single table a reviewer consults to confirm **every folded gap has a guarding gate**:

| Gap | Gate (this doc) | Feature owner (referenced, not redefined) |
|---|---|---|
| **P1** lanes + transition | §12.1 lane/transition determinism (+ soak) | reconciler-hooks.md §6–7 (lane bitmask, update queue, `UseTransition`/`startTransition`) |
| **P2a** automatic batching | §12.2 batching semantics | reconciler-hooks.md §7 (phase-3 update queue) |
| **P2b** Suspense | §12.3 reveal golden + keep-stale | reconciler-hooks.md new § (`Suspense`/`Boundary` element); backdrop-effects-animation.md (`DetachedAnim` keep-stale) |
| **P3** external store | §12.4 tear test | reconciler-hooks.md data-hooks + threading-render-seam.md §12 (`(subscribe,getSnapshot)`) |
| **P4** discard-restart | §12.5 byte-identical golden | threading-render-seam.md §12/§18 (pure-reconcile precondition, `ThreadGuard`) |
| **P5** carry-forward effects | §12.12 gen-stamp | reconciler-hooks.md (EffectRef gen-stamp) / threading-render-seam.md §12 |
| **P6** derived/selector | §12.12 notify-on-change | reconciler-hooks.md §8 (`UseDerived`/`UseContextSelector`) |
| **P7** optimistic | §11.5 `Optimistic` helper + rollback assert | reconciler-hooks.md data-hooks (`UseOptimistic` over P1 lanes) |
| **P8** type-flip handle release | §12.12 remount-releases-handles | reconciler-hooks.md §5.5/§17.8 |
| **P9** effect order | §12.12 bottom-up drain | reconciler-hooks.md §4.3 |
| **L1** text selection | §12.7 selection golden | text.md §8 (`SelectionState` semantics) / scene-memory.md (column) / gpu-renderer.md (`DrawSelectionRectCmd`) |
| **L2** gesture arena | §12.6 arena determinism | input-a11y.md §5.4 (arena coordinator) |
| **L3** `ITextRangeProvider` | §12.7 Narrator conformance | input-a11y.md §11.4 (provider) / text.md §8 (shared read-side) |
| **L4** overlay manager | §12.10 light-dismiss/focus-restore | input-a11y.md (overlay/portal manager) / layout.md (placement geometry) |
| **L5** RTL mirroring | §12.8 multi-locale mirror golden | layout.md §4.1 (`FlowDirection` at `WriteLayout`) |
| **L6** virtualized a11y | §12.11 relations + realize-on-navigate | input-a11y.md §11 (`SetSize`/`PositionInSet`/`Level` + realization) / scene-memory.md (columns) |
| **L7** springs/retarget | §12.9 numerical stability | backdrop-effects-animation.md (spring mode + retarget + `animateContentSize`) |
| **L8** control kit | §12.12 accessible-by-default | controls.md (each control) |
| **L9** i18n + icon mirror | §12.12 format-at-edge + `AutoMirror` | dsl-aot.md (`Directory.Build.props` localization) / text.md §4.4 |
| **L10** cursor | §12.12 cursor resolution | input-a11y.md §5/§17 (resolution) / pal-rhi.md (`Pal.SetCursor` seam) |
| **L11** edge-autoscroll | §12.12 autoscroll driver | input-a11y.md §7/§12 (shared driver) |
| **L13** test harness | **§11 (this doc owns it)** | `FluentGpu.Testing` (this doc) |

---

## Implemented from the gap analysis

Folded into **core** by this doc (no deferral, no "out-of-scope"); the framing for these items is changed from
"defer/Tier-3" to a buildable design:

- **L13 — App-author test harness** → now a **shipped, public core assembly `FluentGpu.Testing`** (§11): mount on
  the headless leaves, deterministic frame clock, simulate pointer/key/gesture/IME/focus through the *real* arena
  and ring, assert-semantics over the actual UIA projection (`AssertNoAccessibilityViolations` promoted from lint
  to test assertion), structural + pixel golden snapshots, and interaction-test helpers for every Tier-1 folded
  feature. Was Tier-3 "defer: thin author API atop headless"; the thin part is now built, not deferred.
- **New always-on GATES, one per folded gap** (§12), each mapped to its gap + owning doc in the §12.13 matrix:
  - **P1** lane/transition scheduling determinism (§12.1, + ledger soak)
  - **P2a** automatic-batching semantics (§12.2)
  - **P2b** Suspense reveal golden + keep-stale (§12.3)
  - **P3** external-store tear test (§12.4)
  - **P4** discard-restart byte-identical-to-single-thread golden (§12.5)
  - **L2** gesture-arena resolution determinism (§12.6)
  - **L1/L3** text-selection + `ITextRangeProvider` Narrator conformance (§12.7)
  - **L5** RTL mirroring golden at multiple locales (§12.8)
  - **L7** spring/retarget numerical stability (§12.9)
  - **L4** overlay light-dismiss/focus-restore (§12.10)
  - **L6** virtualized-a11y realization (§12.11)
  - **P5/P6/P7/P8/P9/L8/L9/L10/L11** companion folded-gap gates (§11.5 + §12.12)

These gates consume new scene columns (`SelectionState`, `FlowDirection`, A11y `SetSize`/`PositionInSet`/`Level`/
`DescribedBy`, lane/queue storage), new opcodes (`DrawSelectionRectCmd`, `DrawFocusRingCmd`, `DrawScrimCmd`), new hooks
(`UseTransition`/`startTransition`, `Suspense`/`Boundary`, `UseOptimistic`, `UseDerived`/`UseContextSelector`,
external-store `(subscribe,getSnapshot)`), and new PAL seams (`Pal.SetCursor`) **by reference** — they are defined
in the owning docs per the directive's ownership map (§12.13). This doc owns only `FluentGpu.Testing` and the gates
themselves.

---

## Changed vs the original synthesis

The original synthesis ([foundations §6, architecture-spec §8/§10](../foundations.md)) had a single
`[Conditional("DEBUG")]` alloc-tripwire + a CI benchmark gate + a footprint ratchet, and treated validation as
"tests." This doc folds in the [hardened-v1 §4.5](../hardened-v1-plan.md) corrections and makes validation a
*maturity program*:

1. **Two clocks made explicit.** Spikes (capability gates, run against the **PublishAot'd binary**, retire-once
   + nightly re-characterize) vs Gates (per-PR regression nets). Coupled by a **trust ledger** — new vs the
   original's flat test list.
2. **Alloc-tripwire seam-blindness corrected.** `GC.GetAllocatedBytesForCurrentThread()` does **not** follow
   work across the seam; every worker is independently `AllocScope`-wrapped with a per-frame summing aggregator,
   and the **process-wide BDN gen0/1/2==0 gate is the load-bearing one** (the per-thread tripwire is only the
   localizer). The original implied a single per-thread delta sufficed.
3. **`seam.race` sweeps channel-cap + reader-stall as fuzz parameters** rather than hard-coding `quarantine=2`
   (which would validate the off-by-one, not the relationship), and explicitly **does not flip 0→2 on the
   current soak** — the flip is gated on a nightly streak via the ledger.
4. **`com.aot.roundtrip` runs against the actual AOT-produced binary** with a **behavioral self-check** (call a
   known method, observe vs the loaded system DLL) — closing the extraction-vs-runtime-ABI gap a SHA over the
   `*.comabi.json` cannot. Fail-safe `AbiVerify` (anchor on frozen IUnknown slots, QI pointer-identity first,
   vectored-exception guard). `ComTracker` re-keyed by **(pointer, generation)** to kill ABA false-greens.
5. **Epoch fault-injection injects BOTH modes** — `Bounds`-move-without-`PaintDirty` (caught by the
   **baked-geometry hash**, not just handle/epoch) AND `Mutate`-without-bump (caught by the **independent
   content-byte oracle**). The original clean-span validator was handle+epoch only and would have shipped stale
   geometry.
6. **Trust-ring forwarding gap closed by a runtime `IsSpikeCaller` assert** (FGVALIDATE-only), since the FG0001–3
   analyzer only sees direct call sites.
7. **The retire-fence handshake replaces DropOldest's unbounded run-ahead** for slot recycling as the validated
   acquire/release surface (mirrors the GPU deferred-delete ring) — the original `Channel<SceneFrame>`
   DropOldest-as-data path is downgraded to a wakeup/coalesce signal only.
8. **"Production safety == CI coverage" stated as the load-bearing invariant**, and the footprint ratchet now
   *mechanically asserts the erasure* (0 bytes of Validation in ship; no guard symbol survives trimming).
9. **Lands in a new `FluentGpu.Validation` assembly** + CI config + the `FluentGpu.SourceGen` FG0001–3 rules
   (the original had no dedicated validation assembly).
10. **WaveeMusic worst-cases turned into ratcheted regression budgets** (incl. the *honest bound* budgets for
    `ColdNowPlaying`/`LikedSongsRegroup50k` that encode the v1 single-thread residuals rather than pretending
    them away), and the structural gate extended to **draw-call/batch/barrier/Bounds** on the portable headless
    pair (folding the [waveemusic §5/§6](../app-requirements-waveemusic.md) amendments incl. the `ImageRef`
    clean-span epoch and the no-`CreateTexture`-in-6–13 / `CopyBufferToTexture` structural assertions).
11. **The L13 app-author test harness is folded into core as a shipped public assembly `FluentGpu.Testing`** (§11),
    not deferred — mount-on-headless, deterministic frame clock, simulate-through-the-real-arena, assert-semantics
    over the actual UIA projection, structural + pixel goldens, and per-folded-feature interaction helpers. The
    original treated it as Tier-3 "defer: thin author API atop headless."
12. **Every folded gap earns an always-on per-PR GATE** (§12), mapped one-to-one to its gap and owning doc in the
    §12.13 traceability matrix — lanes/transition determinism (P1), auto-batching (P2a), Suspense reveal+keep-stale
    (P2b), external-store tear (P3), discard-restart byte-identical golden (P4), gesture-arena determinism (L2),
    text-selection + `ITextRangeProvider` conformance (L1/L3), RTL mirror golden ×locale (L5), spring/retarget
    numerical stability (L7), overlay light-dismiss/focus-restore (L4), virtualized-a11y realization (L6), plus the
    P5/P6/P7/P8/P9/L8/L9/L10/L11 companions. This is the direct application of the doc's load-bearing invariant
    (*production safety == CI coverage*) to the newly-core features: a folded feature with no gate would be an
    unguarded claim in the customer's hands. All gates **reference** the owning docs' artifacts (columns/opcodes/
    hooks/seams) and **redefine none** of them.
