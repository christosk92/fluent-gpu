# Validation and gates

You are changing the engine. This page is the map of *how a change is proven correct before it ships* — the CI
gates that block a regression, the always-on per-gap gates, the headless harness that is your green light, the
canon drift gate, and the trust-ring analyzers. It also states, plainly, what is **not** covered.

This is the working view for contributors. The **design-of-record** — the full machinery, the two clocks, the
trust ledger, every gate's deterministic oracle — is owned by
[`design/subsystems/validation.md`](../../../design/subsystems/validation.md). This page links into it rather
than restating it; where the two differ, the source and the design corpus win and this page is the bug.

> **The one invariant that governs everything below.** Every `ThreadGuard`, alloc-tripwire, `ComTracker`,
> `CleanSpanWitness`, and `IsSpikeCaller` assert is `[Conditional]`-erased from the shipping NativeAOT binary.
> So **in the customer's hands, production safety *is* CI coverage — nothing else.** A hazard not covered by a
> green gate or a retired spike is, in production, unguarded. This is a truth, not a gap to "fix" (see the
> [engine index honesty discipline](./index.md#canon--single-owner-discipline-design-is-authority-these-docs-are-the-working-view)),
> and it is why the gate set has to be exhaustive, non-suppressible, and honest about its own holes. The
> canonical statement lives in [`SPEC-INDEX.md` §2 "Validation / safety floor"](../../../design/SPEC-INDEX.md).

## production safety == CI coverage (`[Conditional]`-erased guards in the shipping AOT binary)

Two facts, held together, are the whole posture:

1. **Debug builds carry guards.** Under `Debug`/`FGVALIDATE`, the asserts fire: the alloc-tripwire arms,
   `ThreadGuard.AssertWriter` throws on a cross-thread write to a confined structure, `ComTracker` counts
   refcounts, `CleanSpanWitness` validates clean-span reuse, `IsSpikeCaller` walks the stack. This is the
   *only* configuration in which any safety guard executes.
2. **The shipping binary carries none.** The guards are `[Conditional("DEBUG")]` / `[Conditional("FGVALIDATE")]`.
   The shipping `Release` AOT build defines neither symbol, so ILC drops every call site, the guard methods
   become unreferenced, and the trimmer deletes them. Working-set cost in ship is **~0** — and that "~0" is
   itself mechanically asserted by the footprint ratchet (below), which fails if any guard symbol survives
   trimming.

The consequence for you as a contributor: **a guard is not protection for users — it is a localizer for CI.**
When you add a hot-path invariant, you do not "add a runtime check and ship it." You add a `[Conditional]`
guard that fires in CI *and* you make sure a green gate (or a retired spike) covers the hazard, because the
guard is gone the moment the binary ships. If the only thing standing between a hazard and a user is a debug
assert, the hazard is unguarded in production. The design-of-record restates this wherever a reader might
forget it; so does this page.

## The per-PR gates (alloc tripwire, footprint, golden-image, COM-refcount, epoch-fault, data-race)

These are the **blocking regression nets**: they run on every PR and again nightly with a longer corpus. Each
emits a delta-vs-baseline artifact so a reviewer sees *what moved*, not just pass/fail. The deep design of each
is in [`validation.md` §3](../../../design/subsystems/validation.md); the contributor-facing summary:

| Gate | What it catches | Where it lives in design |
|---|---|---|
| **Alloc tripwire** (per-phase Δ==0 + the load-bearing process-wide BenchmarkDotNet gen0/1/2==0 backstop) | A `new`/LINQ/boxing/closure that allocates inside the paint phases (6–13). Two-layer: the per-thread Δ is the **localizer** (which phase/worker), the process-wide BDN gen0==0 is the **decider** (it follows work across the render-thread seam, which `GetAllocatedBytesForCurrentThread()` does not). | [§3.1](../../../design/subsystems/validation.md) |
| **Footprint `.mstat` ratchet** | A binary-size regression — and, critically, **a guard that leaked into ship**: the baseline asserts `FluentGpu.Validation` contributes 0 bytes and no `FgAssert`/`AllocScope`/`ComTracker`/`CleanSpanWitness` symbol survives trimming. This is the mechanical proof of "production safety == CI coverage." | [§3.2](../../../design/subsystems/validation.md) |
| **Golden-image perceptual diff** | A shader/gamma/premul/batcher regression that changes *pixels*. Renders the curated scene corpus through `Rhi.Headless` (WARP) and asserts CIEDE2000 + edge-shift within the disposition-table tolerance. No silent re-baselining. | [§3.3](../../../design/subsystems/validation.md) |
| **Headless structural gate** (draw-call / batch / barrier / `Bounds`) | A regression in *commands*, not pixels — a batch-count spike from a broken painter order, a missing/redundant barrier, a layout shift the pixel tolerance would absorb. Exact integers on `Rhi.Headless` + `Pal.Headless`. The cheapest, most deterministic, **portable** gate (no Windows, no GPU). This is the one the VerticalSlice harness embodies. | [§3.4](../../../design/subsystems/validation.md) |
| **COM net-refcount / leak gate** (non-suppressible) | A leak (net-nonzero at teardown) or an over-release / double-free (net-negative mid-run — a use-after-free under AOT). `ComTracker` keyed by `(pointer, generation)` to kill ABA false-greens. Runs under `CI-RELEASE` so it observes the AOT-shaped, render-thread-confined `ComPtr` lifetimes. | [§3.5](../../../design/subsystems/validation.md) |
| **Epoch fault-injection** | A stale clean-span reuse. It *injects both* failure modes — `Bounds`-move-without-`PaintDirty` (caught by the baked-geometry hash) and `Mutate`-without-epoch-bump (caught by the independent content-byte oracle) — and asserts `CleanSpanWitness` throws. A handle+epoch-only validator would have shipped stale geometry; this proves it does not. | [§3.6](../../../design/subsystems/validation.md) |
| **Data-race gate** | A confinement violation: `ThreadGuard.AssertWriter` deterministically throws if a non-owning thread writes a confined structure (a SceneStore column, the RHI handle table, a `ComPtr`). The fast per-PR companion to the `seam.race` soak spike. | [§3.7](../../../design/subsystems/validation.md) |

The alloc tripwire is the one you will trip most. The honest contributor mental model for it:

- **The paint phases (6–13) are asserted Δ==0.** In `AppHost.RunFrame()` the host captures
  `before = GC.GetAllocatedBytesForCurrentThread()` just before phase 3 (`_runtime.Flush()`), then computes
  `hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before` after Present (phase 11) and stores it as
  `FrameStats.HotPhaseAllocBytes` (`src/FluentGpu.Hosting/AppHost.cs`). On a steady frame this must be `0`.
- **The reconcile/render *edge* (phases 2/4) is budgeted, not zeroed.** A legitimate huge-subtree `setState`
  mints that subtree in one frame; that Gen0 is *measured and ratcheted*, never pretended to be zero. This is
  why the engine is "**near-zero-allocation**" — zero on the paint half, bounded at the edge — and the gate
  encodes exactly that distinction.

If `HotPhaseAllocBytes > 0` fails, the cause is almost always an allocation inside a bind thunk or a hot effect
body (`new`, LINQ, boxing, a per-call closure). The fix is to capture everything once at mount and have the
thunk only read + write existing state — see the
[pitfalls performance row](../../guide/pitfalls.md#performance) for the symptom→cause→fix.

> **Caveat the gate states about itself** ([`validation.md` §3.1](../../../design/subsystems/validation.md)):
> `GC.GetAllocatedBytesForCurrentThread()` **does not follow work across the render-thread seam**. The
> single-thread `FrameStats.HotPhaseAllocBytes` you read in the harness is the *localizer*; once tessellation
> and raster fan out to workers, the **process-wide BenchmarkDotNet gen0/1/2==0 backstop** is the load-bearing
> decider, because it sees the whole process. Both must be green. The threading model itself is canon in
> [`SPEC-INDEX.md` §2 "Threading / frame phases"](../../../design/SPEC-INDEX.md).

## The always-on per-gap gates (lanes/transition determinism, Suspense reveal, external-store tear, gesture-arena, RTL mirror, spring stability, …)

Every gap the framework folds into **core** — there is no v2/deferred carve-out (see
[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md)) — earns its own **blocking, always-on per-PR gate**. This is
the load-bearing invariant applied to the newly-core features: a folded feature with no gate is, in the
customer's hands, an unverified claim. Each gate names the gap it guards, uses a **deterministic oracle** (an
exact golden over a trace/integer/`Bounds` mirror, not a flaky pixel), and references the owning doc's
columns/opcodes/hooks rather than redefining them.

The full set is [`validation.md` §12](../../../design/subsystems/validation.md), with a gap→gate→owning-doc
traceability matrix in [§12.13](../../../design/subsystems/validation.md). The headline gates:

- **Lane / transition scheduling determinism (P1)** — for a fixed input schedule + seeded scheduler, the
  sequence of committed frames (which lanes flushed in which frame) is byte-identical run-to-run, and urgent
  input never starves behind a transition. Exact golden over the lane trace. ([§12.1](../../../design/subsystems/validation.md))
- **Automatic-batching semantics (P2a)** — all `setState`s in one handler *and across an `await`* fold into
  exactly **one** committed frame. Oracle: committed-frame count == 1. ([§12.2](../../../design/subsystems/validation.md))
- **Suspense reveal golden + keep-stale (P2b)** — fallback mounts as a unit, content swaps **atomically** (no
  partial-reveal frame), and a transition **keeps already-revealed content** instead of flashing fallback.
  ([§12.3](../../../design/subsystems/validation.md))
- **External-store tear test (P3)** — every consumer of a store reads the same snapshot version within a frame
  even under mid-reconcile mutation; a mismatch demotes the frame to a blocking single pass. Always on *even in
  single-pass v1* so a future slicing flip can't silently introduce a tear. ([§12.4](../../../design/subsystems/validation.md))
- **Discard-restart byte-identical golden (P4)** — an interrupted-and-restarted reconcile produces a
  `DrawList` byte-identical to the single-thread golden, and asserts no `ISceneBackend` mutation is observable
  before PUBLISH. ([§12.5](../../../design/subsystems/validation.md))
- **Gesture-arena resolution determinism (L2)** — a fixed multi-pointer script resolves to the same winner
  every run; pins the tentative-capture semantics. ([§12.6](../../../design/subsystems/validation.md))
- **Text-selection + `ITextRangeProvider` Narrator conformance (L1/L3)** — on-screen selection rects and the
  UIA range provider share **one** read-side (no offset drift); BiDi multi-rect asserted structurally *and*
  perceptually. ([§12.7](../../../design/subsystems/validation.md))
- **RTL mirroring golden ×locale (L5)** — an RTL layout is the exact X-mirror of its LTR `Bounds[]`, which also
  protects the ported-Yoga golden-parity contract (resolution happens at the `WriteLayout` boundary, not inside
  the numerics). ([§12.8](../../../design/subsystems/validation.md))
- **Spring / retarget numerical stability (L7)** — the spring integrator never overshoots into NaN/Inf,
  settles in a bounded frame count, and seeds velocity from the instantaneous value on mid-flight retarget;
  swept across a `dt` + stiffness/damping grid. ([§12.9](../../../design/subsystems/validation.md))
- **Overlay light-dismiss / focus-restore (L4)**, **virtualized-a11y realization (L6)**, and the
  **P5/P6/P7/P8/P9/L8/L9/L10/L11 companions** (carry-forward effect gen-stamp, derived/selector notify, type-flip
  handle release, bottom-up effect drain, control-kit accessible-by-default, locale formatting + icon mirror,
  cursor resolution, edge-autoscroll). ([§12.10–§12.12](../../../design/subsystems/validation.md))

When you fold a new gap into core, the rule is the same as for any contract: register the artifact in its
owner, then add the always-on gate here. A folded feature with no gate is the exact thing this section exists
to prevent.

## The headless golden-check harness as the green signal (`ALL CHECKS PASSED`)

The structural gate above is not an abstraction — for a contributor it *is* the
[`FluentGpu.VerticalSlice`](../../../src/FluentGpu.VerticalSlice/Program.cs). It runs ~60 end-to-end golden
checks across every seam (layout, reconcile, signals, scroll, virtualization, images, controls, navigation,
animation) with **no GPU and no window**, in seconds, and prints `[PASS]`/`[FAIL]` lines and a final verdict:

```bash
dotnet build src/FluentGpu.VerticalSlice                 # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice       # expect: ALL CHECKS PASSED
```

After **any** engine change, run it and confirm `ALL CHECKS PASSED` before you claim success. The full
walkthrough of the harness wiring lives in the sibling
[Frame Pipeline and Verification Harness](./frame-pipeline-and-verification-harness.md) page; what matters here
is *how it is your gate*.

The harness is a single deterministic program. Its core is a `Check` helper that counts failures and a
`Main` that returns a process exit code — so a red check fails CI:

```csharp
static int s_failures;

static void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) s_failures++;
}

// … at the end of Main, after every check has run:
if (s_failures == 0) { Console.WriteLine("ALL CHECKS PASSED — the vertical slice exercises every seam end-to-end."); return 0; }
Console.WriteLine($"{s_failures} CHECK(S) FAILED."); return 1;
```

Each check mounts a real `AppHost` on the **headless leaves** and drives it one frame at a time. The
`HeadlessGpuDevice` records the DrawList into inspectable lists (`LastRects`, `LastGlyphs`,
`LastGradientStrokes`, …) so you assert *what was drawn* without pixels; `host.Scene` exposes post-layout
bounds. The vertical-slice acceptance test reads almost exactly like its spec:

```csharp
var strings = new StringTable();
using var app = new HeadlessPlatformApp();
var window = new HeadlessWindow(new WindowDesc("FluentGpu slice", new Size2(480, 320), 1f));
window.Show();
var device = new HeadlessGpuDevice();
var fonts  = new HeadlessFontSystem(strings);
var root   = new Counter();
using var host = new AppHost(app, window, device, fonts, strings, root);

// Frame 1 — mount: window → clear → two button rects (SDF) + three text runs, flex-laid-out.
var f1 = host.RunFrame();
Check("1. window + GPU clear + present", device.FrameCount == 1);
Check("3. text runs (heading + 2 labels)", device.LastGlyphs.Count == 3);
Check("5. reconciler + UseState (initial render)", f1.Rendered && HasGlyph(device, strings, "Count: 0"));

// … queue a click on the "+" button, then:
var f2 = host.RunFrame();
Check("6. clickable Button → OnClick fired", f2.ClicksHandled == 1);
Check("7. setState re-rendered the label", f2.Rendered && HasGlyph(device, strings, "Count: 1"));
```

`AppHost.RunFrame()` returns a `FrameStats` — your instrument panel. It is a `readonly record struct` defined in
`src/FluentGpu.Hosting/AppHost.cs`:

```csharp
public readonly record struct FrameStats(int DrawCommandCount, int ClicksHandled, long HotPhaseAllocBytes, bool Rendered)
{
    public int NodesVisited { get; init; }
    public int DrawNodeCount { get; init; }
    public int CulledNodeCount { get; init; }
    public double Fps { get; init; }
    public double FrameMs { get; init; }
    public int ComponentsRendered { get; init; }
}
```

The three numbers you assert on when you touch reactivity, layout, or the renderer:

- **`Rendered`** — was there a reconcile/layout this frame? For a slider drag or scroll you bound (the
  compositor-bypass path), you want `Rendered == false`. The harness asserts exactly this:

  ```csharp
  bool compositorOnly = !f.Rendered;                        // no reconcile + no layout this frame
  Check("…slider drag is compositor-only", compositorOnly, $"rendered={f.Rendered}");
  ```

- **`ComponentsRendered`** — how many component render-effects ran. This is the **granularity proof**: a leaf
  state change must not re-render the page. A real check asserts a single owning component re-rendered:

  ```csharp
  Check("…granular re-render", only0 && f.ComponentsRendered == 1 && HasGlyph(device, strings, "c0:1"),
        $"componentsRendered={f.ComponentsRendered}");
  ```

- **`HotPhaseAllocBytes`** — managed bytes allocated on the paint half. On a steady frame this **must be 0** —
  the near-zero-allocation guarantee, asserted, not asserted-by-vibes. The canonical shape of the assertion:

  ```csharp
  // Warm, then assert the steady paint half (phases 6–13) is zero managed allocation.
  for (int i = 0; i < 6; i++) host.RunFrame();
  var steady = host.RunFrame();
  Check("8. steady frame does no work (memoized)", !steady.Rendered);
  Check("9. ZERO managed alloc on the paint half", steady.HotPhaseAllocBytes == 0, $"{steady.HotPhaseAllocBytes} bytes");
  ```

  The same pattern guards the hot interactions — in-window scroll, a slider drag, a caret blink — each warming
  a few frames and then asserting `HotPhaseAllocBytes == 0` on the steady frame.

**To add coverage**, write a new `Check("…", condition, detail)` in `Program.cs` and call it from `Main` (or
from one of the grouped `…Checks(strings)` methods it dispatches to). If you changed an interaction, assert on
the `FrameStats` for *that* interaction. The harness is `dotnet run` — `[PASS]`/`[FAIL]` per check and a single
exit code — so a new red check fails CI exactly like every existing one.

> **What the harness does *not* assert** is on-screen D3D12 pixels — those are a separate, manual
> "needs-pixels" pass on the real Windows path. "The harness is green" means the logic and the recorded DrawList
> are correct; it does not by itself mean a control looks right on screen. The golden-**image** gate (above,
> `Rhi.Headless` WARP) and the manual pixel pass cover that. Keep the two bars distinct.

## The canon drift gate (`check-canon.ps1`; `canon-allow`; the single-owner model)

The design corpus is large and heavily cross-referenced; consistency is enforced by a **strict single-owner
model** and a drift gate. The gate is design-time only (it scans `design/`, not `docs/`), and you run it after
editing any `design/` doc:

```powershell
powershell -File design/check-canon.ps1                  # exit 0 = clean
```

`check-canon.ps1` (`design/check-canon.ps1`) is a deny-list of known-stale/superseded tokens; it fails (exit 1)
if any of them reappears anywhere in the **live** design tree (`design/archive/` is excluded — historical docs
may carry superseded forms). The rules it currently protects, each tied to a canonical value in
[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md):

- **`handle-layout`** — the `{index32, gen24, kind8}` form is superseded; the handle is `{u32 index, u32 gen}`. <!-- canon-allow: documents the gate rule itself -->
- **`com-blanket`** — the blanket "all COM via hand-vtable / no `ComWrappers` anywhere" rule is superseded by <!-- canon-allow: documents the gate rule itself -->
  the tiered COM ruling.
- **`depkey-union`** — a `[FieldOffset]` GC-ref/scalar union is illegal CLR layout (`TypeLoadException`);
  `DepKey` is pure-scalar + a side `GcDepTable`.
- **`bind-props`** — the dual static + `*Bind` element surface (`TransformBind`, `OpacityBind`, …) is <!-- canon-allow: documents the gate rule itself -->
  superseded by one `Prop<T>` per bindable channel.

To intentionally mention a superseded form in live prose (e.g. to explain a correction), put the marker
`canon-allow` on that line — the convention is an HTML comment `<!-- canon-allow: reason -->`. The gate skips
any line containing it.

The single-owner discipline the gate backs:

- **[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) is the precedence authority.** When two docs disagree on a
  cross-cutting contract, §2 gives the canonical value and the *one* owning doc.
- **Every shared artifact has exactly one owner.**
  [`subsystems/README.md`](../../../design/subsystems/README.md) is the contract-ownership map — every DrawList
  opcode, RHI method, PAL seam, hook, source generator, scene column, and assembly maps to its one
  authoritative doc. Define it in its owner; everywhere else *reference* it.
- **Superseding a value** means: edit `SPEC-INDEX.md` first, then the owning doc, add a `check-canon.ps1` rule
  for the old token, move the superseded doc to `design/archive/`, and re-run the gate. Adding a new
  cross-cutting contract means registering it in `SPEC-INDEX.md` §2 + the ownership map *before* two docs can
  disagree about it.

The same `[PASS]`-or-fail rigor as the harness: if the canon gate is red after a `design/` edit, a stale token
reappeared — fix the token or add `canon-allow`, and re-run. See the
[pitfalls workflow row](../../guide/pitfalls.md#workflow--agents-especially) for the symptom→fix.

## The trust ring and `[Capability]` analyzers

The hardest design problem the program solves is "claim ≠ property": a doc can *assert* a mechanism works, but
that assertion isn't enforced. The trust ring makes the assertion a **compile error** instead of a
code-review hope. The full mechanism is [`validation.md` §4](../../../design/subsystems/validation.md); the
contributor-facing shape:

- A **`[Capability]`** attribute tags a mechanism with the trust ring it has *earned* —
  `Experimental < Spiked < Proven`. A capability is the named property a spike retires (`Com.GeneratedAbi`,
  `Text.Shaping`, `Render.AnalyticAA`, `Seam.Quarantine`, …).
- **Roslyn analyzer rules FG0001–FG0003** (shipped in `FluentGpu.SourceGen`, referenced by everything, so the
  fence is enforced at *every* call site including app code):
  - **FG0001 (Error)** — shipping code physically cannot compile against an `Experimental` capability.
  - **FG0002 (Error)** — a member's declared ring may not exceed the ring the **trust ledger**
    (`trust-ledger.json`) justifies; the ledger is the source of truth, the attribute may not over-claim.
  - **FG0003 (Warning→Error-in-CI)** — using a `Spiked` capability outside a `[SpikeGated]` region without an
    explicit opt-in is flagged.
- The analyzer sees **direct** call sites only. A forwarding shim (shipping method → innocuous wrapper →
  Experimental capability) defeats static analysis — so that gap is closed at **runtime** by the
  `[Conditional("FGVALIDATE")]` `IsSpikeCaller` / `AssertSpikeGated(cap)` assert, which walks the CI-only call
  stack and fails if an unproven capability is reached from a non-spike caller. Three legs: analyzer (direct
  edges) + runtime stack-walk (transitive edges, CI-only) + the trust ledger (the data both consult).

The two clocks couple here: a **spike** (capability gate, run against the actual `PublishAot`'d binary, not the
JIT host) is the *only* event that may promote a capability ring upward and write an evidence row to the
ledger; a spike going **Red** in nightly *demotes* the row, which makes every shipping caller fail FG0001/2 on
the next build — a regression in a *proven* property propagates back into a compile break, automatically. The
canonical floor statement is [`SPEC-INDEX.md` §2 "Validation / safety floor"](../../../design/SPEC-INDEX.md).

## The public `FluentGpu.Testing` app-author harness (`TestHost`/simulate/assert/goldens)

The internal `FluentGpu.Validation` machinery is CI-only and never shipped. Its **consumer-facing twin** is
`FluentGpu.Testing` — a shipped, public assembly app authors reference from *their* test projects. It is the
same headless substrate (`Pal.Headless` + `Rhi.Headless`, the structural ledger, the WARP pixel path) with an
ergonomic surface and **no** `[Conditional]` guards or spike/ledger machinery. The design-of-record is
[`validation.md` §11](../../../design/subsystems/validation.md).

The one rule it obeys: it composes **only public seams and the headless leaves** — it instantiates a real
`Hosting` composition root parameterized with the headless leaves, so a test exercises the *actual* 13-phase
loop, reconciler, layout, arena, overlay manager, and UIA projection. It does **not** fork a shadow engine. A
test that passes here ran the production code paths with synthetic platform leaves — the same property that
makes the VerticalSlice harness trustworthy, exposed to app authors.

The entry point is `TestHost`: it owns a headless `Hosting` root + a manual, deterministic frame clock.

```csharp
namespace FluentGpu.Testing;

public sealed class TestHost : IDisposable        // owns a headless Hosting root + a manual frame clock
{
    public static TestHost Mount(Element root, TestHostOptions? opts = null);   // builds Hosting on Headless leaves
    // ── deterministic clock ──────────────────────────────────────────────────
    public void PumpFrame();                 // runs ONE full 13-phase loop (incl. render thread, then joins it)
    public void PumpFrames(int n);
    public void AdvanceTime(TimeSpan dt);     // advances the animation clock by an exact delta
    public void DrainTransitions();           // flush all transition-lane work to quiescence
    public void DrainAsync();                 // await pending UseResource/Suspense promises, then PumpFrame
    // ── queries (read-only views over committed scene + a11y projection) ──────
    public TestElement Find(string automationId);
    public TestElement FindByText(string text);
    public SemanticsSnapshot Semantics();     // the merged/compacted UIA tree the AT would see
    public StructuralSnapshot Structure();    // draw-calls/batches/barriers/bounds (the §3.4 ledger view)
    public FrameImage Pixels();               // WARP-rendered BGRA8 framebuffer (for pixel goldens)
}
```

The author surface mirrors the engine's own gates, made ergonomic:

- **Simulate** writes synthetic events into the *same* input ring the real `Pal` would, and gestures go through
  the *real* gesture arena: `Tap(e)`, `Drag(from, to)`, `Gesture(script)`, `Key(k, m)`, `TypeText(s)`,
  `FocusNext()`, `SelectText(e, start, len)`. Each pumps frames to quiescence by default (with a `noPump:true`
  overload to observe an intermediate frame).
- **Assert-semantics** makes the accessibility surface a first-class assertion — `host.Semantics()` returns the
  exact merged UIA projection an AT sees, and `host.AssertNoAccessibilityViolations()` promotes the DEBUG
  accessibility-scanner ruleset from a lint to a fail-the-test assertion an author can wire into their own CI.
- **Goldens** expose the same two mechanisms the engine uses —
  `host.Structure().MatchGolden("…structural.json")` (exact integers) and
  `host.Pixels().MatchGolden("…png", metric: Ciede2000(…))` (perceptual) — with the same no-silent-rebaseline
  discipline (`dotnet fluentgpu test --update-goldens`).

`FluentGpu.Testing` is fully portable (it never references a Windows concrete type), so an app author's suite
runs identically on the macOS CI leg. As an engine contributor, the relationship to keep straight: the **§12
gates** guard the *framework's* folded features; `FluentGpu.Testing` *enables* an app author's coverage — it
does not *enforce* it. An app author's missing test is their gap, not the engine's.

## What is honestly NOT covered (physical limits: GPU stall bounds back to the UI thread)

The program is exhaustive about what it can mechanize, and explicit about what it cannot. These are the named
residuals from [`validation.md` §8](../../../design/subsystems/validation.md) — truths, not TODOs:

- **A sustained GPU stall still bounds back to the UI thread.** Decoupling the render thread makes slowness
  *decoupled, not invincible*: the seam absorbs a transient hitch, but a sustained GPU stall eventually applies
  backpressure to the UI thread. No gate makes this go away; it is a physical limit, stated as one.
- **The one lock-free retire-fence is sampled, not proven.** There is no managed TSan. The `seam.race` soak
  shrinks the lock-free surface to exactly one documented acquire/release fence and fuzzes it (sweeping channel
  capacity + reader-stall), but a race *inside* that fence the schedule-fuzz never hits is, in production,
  unguarded. The honest grade is **MANAGED-sampled** — the soak reduces risk, it does not eliminate it.
- **Epoch double-fault** (two errors that cancel in *both* the baked-geometry hash and the content-byte hash)
  is caught only probabilistically.
- **Uncovered AA / DPI / rotation / locale inputs are ungated.** The `render.aa` golden emits a coverage
  manifest so the gap is *visible*, not hidden; a new primitive lands with its golden row or it cannot promote
  the capability. Likewise the real-UIA-client Narrator conformance half of L1/L3/L6 is Windows-gated and
  carries its own coverage manifest.
- **A weak spike behind a `Proven` ring** is a human judgment the ledger records but cannot validate.
- **Production has no guards.** Every assert is erased. This is *the* bottom line, restated wherever a reader
  might forget it: a hazard not covered by a green gate or a retired spike is unguarded in the customer's
  hands.

There is one suppression floor worth knowing: if the FGCOM analyzers and FG0001–3 are suppressed under schedule
pressure, the **non-suppressible** `CI-RELEASE` gates (the `com.aot.roundtrip` behavioral roundtrip, the COM
net-refcount/leak gate, the ABI-golden, `seam.race`) are what still fire. The build *order* is part of the
posture too: it ships single-thread-correct first, then flips parallelism behind the green `seam.race` streak
([`SPEC-INDEX.md` §2 "Quarantine constant"](../../../design/SPEC-INDEX.md)).

## Canon link: validation subsystem (design-of-record)

This page is the working view. The authority is:

- **[`design/subsystems/validation.md`](../../../design/subsystems/validation.md)** — the validation &
  maturity program design-of-record: the two clocks (spikes + gates), the trust ledger, every gate's
  deterministic oracle, the `FluentGpu.Testing` harness spec, the per-folded-gap §12 gate set, and the honest
  non-coverage ledger.
- **[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md)** — the precedence authority; §2 owns the canonical
  "Validation / safety floor", the threading model, the quarantine constant, and the handle/COM/`DepKey`/`Prop<T>`
  contracts the canon gate protects.
- **[`subsystems/README.md`](../../../design/subsystems/README.md)** — the contract-ownership map (which doc
  owns which opcode/column/seam/hook the §12 gates reference).

**Within this section:** [Engine Contributors overview](./index.md) ·
[Contributor Map: Where to Change What](./contributor-map.md) ·
[Frame Pipeline and Verification Harness](./frame-pipeline-and-verification-harness.md) (the harness
walkthrough) · [Signals and Reactivity Internals](./signals-and-reactivity-internals.md) ·
[pitfalls](../../guide/pitfalls.md) (symptom → cause → fix, including the `HotPhaseAllocBytes > 0` and canon-gate
rows).
