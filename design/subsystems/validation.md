# FluentGpu — Validation & Maturity Program (spikes + CI gates)

> **Subsystem design-of-record for `FluentGpu.Validation` and the CI configuration.**
> This is the deep design behind [hardened-v1-plan.md](../hardened-v1-plan.md) §4.5 ("Validation — the
> spine that keeps it true"). It owns the machinery that turns every *claim* the other subsystems make
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
| **Deps** | `Foundation`, the **interface** seams (`Pal`, `Rhi`, `Text`), `Scene`, `Render`, `Reconciler`, `Hooks`, `Layout`, `Animation`, plus the **headless leaves** `Pal.Headless` + `Rhi.Headless`. May reference `Media`/`Theme` for their worst-case benches. **Must not** reference `Rhi.D3D12`/`Pal.Windows`/`Text.DirectWrite` except inside the explicitly Windows-gated `text.conformance` / `com.aot.roundtrip` / `render.aa` spike hosts (these are `#if WINDOWS` and excluded from the portable test pass). |
| **Acyclicity** | Validation is a *sink*: everything depends inward, nothing depends on Validation. It adds no edge to the production DAG ([foundations §7](../foundations.md), [architecture-spec §3](../architecture-spec.md)). |
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
        │         · headless structural (draw-call / batch / barrier / Bounds) asserts on Rhi.Headless+Pal.Headless   │
        │         · COM net-refcount / leak gate · epoch fault-injection · data-race gate                             │
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
   same text through `Text.DirectWrite` and through our pipeline and assert: same glyph count per cluster,
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

**Thread/phase:** runs offline (not in the frame loop). The DWrite oracle uses `Text.DirectWrite` which owns
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

Runs against both `Rhi.Headless` (WARP, deterministic, the CI default) and, nightly, real D3D12 hardware to
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
`Rhi.Headless` (WARP) and asserts CIEDE2000 + edge-shift within the disposition-table tolerance. Distinct from
the **structural** gate (§3.4): this one looks at *pixels*, that one looks at *commands*. A PR that changes a
shader or the batcher must pass both. New goldens are added with the PR that introduces the primitive; a golden
can only be *re-baselined* with an explicit reviewer-approved artifact diff (no silent golden updates).

### 3.4 Headless structural gate — draw-call / batch / barrier / Bounds asserts on `Rhi.Headless` + `Pal.Headless`

This is the cheapest, fastest, most deterministic gate and the one that runs in the *portable* pass (no
Windows, no GPU). `Rhi.Headless` is a CPU/null encoder that records *what was asked of it*; `Pal.Headless`
synthesizes window/input/DPI events ([architecture-spec §3](../architecture-spec.md)).

```csharp
public sealed class StructuralLedger : ICommandEncoder    // the Rhi.Headless recording encoder
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
| 2 input | UI | edge-alloc *tolerated* (closures) — measured by BDN, not asserted-zero |
| 4 render | UI | memo-skip 3-signal correctness checked by the reconcile fault-injection corpus (see §7) |
| 5 reconcile | UI | `ThreadGuard.AssertWriter` (SceneStore single-writer); two-phase growth-lock asserts |
| 6 layout | UI | `AllocScope(Layout)` Δ==0 |
| 6.5 layout-effects | UI | effect-timing assert (ratified phase 6.5; setState⇒mark-dirty+N+1, no sync re-loop) |
| 7 animation | UI | `AllocScope(Animation)` Δ==0; Transform/Paint-dirty-only assert (never LayoutDirty) |
| **PUBLISH (13a)** | UI | consume-gated `QuarantineLedger` tick assert; `_consumeIdx` acquire-before-pick assert |
| 8 record | RENDER | `CleanSpanWitness` (baked-geometry + epoch + content-byte oracle); `AllocScope(Record)` |
| 9 batch | RENDER | structural `BatchStats` ledger; painter-order last-writer assert; `AllocScope(Batch)` |
| 10 submit | RENDER | structural barrier-count ledger; `ComTracker` confinement (all ComPtr on render thread) |
| 11 present | RENDER | present-ack volatile-write ordering assert; `AllocScope(Present)` |
| 12 passive-effects | UI | effect-timing assert; setState⇒N+1 |
| (workers) | WORKER | per-worker `AllocScope`; pure-function assert (touch NO SceneStore/RhiTable/fence) |

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

---

## 9. Cross-platform (macOS) boundary

The validation program is **mostly portable** and is the lever that *proves* portability:

- **Portable, runs on both OSes:** the Unicode-conformance half of `text.conformance`; the structural gate
  (`Rhi.Headless` + `Pal.Headless` are portable CPU/synthetic leaves — they are the macOS-portability proof in
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

Nothing in `FluentGpu.Validation` references a Windows concrete type except inside the `#if WINDOWS` spike
hosts, so the macOS build of the validation assembly compiles and runs the portable clock unchanged.

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
