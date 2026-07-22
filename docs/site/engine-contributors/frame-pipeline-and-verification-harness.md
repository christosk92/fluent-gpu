# Frame pipeline and verification harness

> **✅ Animation engine — signals-first rework landed + verified.** The phase-7 animation tick is one `AnimScheduler` (`min(next-due)` wake, per-source `CadenceClass`) over a unified POD `AnimValue` slab — the former N independent tickers (interaction, brush, …) are folded in. The new gates are live and green: spring/gesture dt-determinism replay, the scheduler-tick alloc tripwire, the ambient-cadence idle check. Design, now implemented + the per-phase gate list: [`../../plans/animation-engine-rework-design.md`](../../plans/animation-engine-rework-design.md) §11.

This page is for people **changing engine internals** — the frame loop, a seam, the recorder, the reconciler — and
who then need to *prove* the change didn't regress a cross-seam contract. If you are building an app, read
[Performance rules and zero-alloc boundaries](../app-authors/performance-rules-and-zero-alloc-boundaries.md) and the
guide's [rendering & performance page](../../guide/rendering-and-performance.md) first — they own the *mental model*
(the three update paths, the compositor bypass). This page is the *machine*: the ordered phases, the alloc tripwire
that brackets them, and the headless golden harness (`src/FluentGpu.VerticalSlice`) that runs the whole engine without
a GPU or a window so a regression fails in seconds.

The harness is the single source of truth for "does the engine still work." After **any** engine change:

```bash
dotnet build src/FluentGpu.VerticalSlice   # must be clean
dotnet run   --project src/FluentGpu.VerticalSlice   # must print: ALL CHECKS PASSED
# Local subset while iterating (do NOT set FG_SUITE in CI):
dotnet run   --project src/FluentGpu.VerticalSlice -- --suite scroll
```

> Honesty note: the harness asserts *structure* — what the recorder emitted, where layout placed nodes, what re-rendered,
> how many bytes the paint half allocated — **not pixels**. Pixel-level AA/gamma/rasterization correctness is a separate
> concern that lives behind the [Validation and Gates](./validation-and-gates.md) golden-image gate and a manual
> real-D3D12 run; see [GPU pixels are out of scope here](#gpu-pixels-are-out-of-scope-here) below.

**Where the code is** (the engine where-to-change map for this page):

| If you're changing… | Edit |
|---|---|
| The frame loop / phase order / `FrameStats` | `src/FluentGpu.Engine/Hosting/AppHost.cs` |
| The alloc-tripwire bracket (`HotPhaseAllocBytes`) | `src/FluentGpu.Engine/Hosting/AppHost.cs` (`Paint`) |
| The headless GPU device (decoded DrawList) | `src/FluentGpu.Engine/Headless/Rhi/HeadlessGpuDevice.cs` |
| The headless window / platform app / input | `src/FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs` |
| The deterministic font advance model | `src/FluentGpu.Engine/Headless/Text/HeadlessFontSystem.cs` |
| The checks themselves (add/modify a group) | `src/FluentGpu.VerticalSlice/Suites/*Suite.cs` (+ `Probes/`, `Harness/`) |
| `InputEvent` / `WindowDesc` / the PAL seam | `src/FluentGpu.Engine/Seams/Pal/Pal.cs` |

The phase order and the SoA scene it patches are *described* (not re-derived) in the design canon: the 13-phase loop and
the minimum vertical slice live in
[`architecture-spec.md`](../../../design/architecture-spec.md), and the validation spine in
[`subsystems/validation.md`](../../../design/subsystems/validation.md). This page documents the *as-built* loop in
`AppHost.cs` and the harness under `src/FluentGpu.VerticalSlice/` (`Program.cs` + `Harness/` + `Suites/` + `Probes/`).

## The 13-phase frame loop

One method drives a frame: `AppHost.RunFrame()` (`src/FluentGpu.Engine/Hosting/AppHost.cs`). It does the *pump* (read OS input)
and then delegates the rest to `AppHost.Paint()` — the split exists so the window's `PaintRequested` callback can redraw
*without* re-pumping during the OS modal move/resize loop (calling `Paint` from a `WndProc` is safe; calling `RunFrame`
would re-enter the pump).

```
1    pump            window.PumpInto(ring)         — drain OS input into the host-owned InputEventRing  [RunFrame]
2    input dispatch  dispatcher.Dispatch(...)       — hit-test, gestures; handlers WRITE SIGNALS         [RunFrame]
                     (a handler writing a signal schedules the owning component's render-effect)
── Paint() ──
3–5  reactive flush  runtime.Flush()                — run scheduled render-effects (reconcile) + bindings
                     + ReRealizeVirtuals()          — re-realize virtual windows that crossed a scroll boundary
6    layout          full Run() on first-frame/resize/DPI/root-change; else invalidator.RunDirty() (scoped)
6.5  layout effects  DrainLayoutEffects()           — UseLayoutEffect bodies (Bounds are valid; seed animations)
7    animation       anim.Tick(dt) — the one AnimScheduler over the slab (hover/press/brush are slab channels) + scroll/repeat/caret/dragdrop ticks; FLIP projections; sticky pins
8    record          SceneRecorder.Record(scene → DrawList)  — clips, world transforms, glyphs, images
10   submit          device.SubmitDrawList(bytes, sortKeys, FrameInfo)
11   present         swapchain.Present()
12   passive effects DrainPassiveEffects()          — UseEffect bodies
12.5 string reclaim  strings.Tick()                 — reclaim released text ids behind the reader quarantine
```

The numbering is the canon's (phase 9 = batch and PUBLISH/13a = the render-thread-seam publish point are present in the
hardened design but collapse into the single-thread `Paint` body today — the seam is built single-thread-correct first,
then flips behind a green race gate; see [Validation and Gates](./validation-and-gates.md)).

The shape of `Paint` that matters when you change phase order — note the **alloc bracket** straddling phases 3–11:

```csharp
public FrameStats Paint(int clicks = 0)
{
    // … FLIP "First" capture (presented rects of layout-animated nodes) before the reconcile that moves them …
    long before = GC.GetAllocatedBytesForCurrentThread();      // ← alloc bracket opens

    _runtime.Flush();                                          // 3–5 render-effects reconcile + bindings
    bool virtualsChanged = _reconciler.ReRealizeVirtuals();
    bool reconciled = _reconciler.ConsumeReconciled() || virtualsChanged;

    bool layoutNeeded = _needFullLayout || reconciled || _scene.AnyLayoutDirty;
    if (layoutNeeded && !_scene.Root.IsNull)
    {
        if (_needFullLayout || !_everLaidOut) _layout.Run(_scene.Root, layoutSize);  // 6 full
        else _invalidator.RunDirty(layoutSize);                                      // 6 scoped
        _scene.ClearLayoutDirty();
    }
    DrainLayoutEffects();                                      // 6.5
    _anim.Tick(dtMs);                                          // 7 the AnimScheduler over the slab (+ scroll/sticky/… ticks)

    var recordStats = SceneRecorder.Record(_scene, _drawList, _images, /* … */);     // 8 record
    _device.SubmitDrawList(_drawList.Bytes, _drawList.SortKeys, new FrameInfo(/* … */)); // 10 submit
    _swapchain.Present();                                      // 11 present
    long hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;   // ← alloc bracket closes

    DrainPassiveEffects();                                     // 12
    _strings.Tick();                                          // 12.5
    // … assemble FrameStats { HotPhaseAllocBytes = hotAlloc, Rendered = reconciled || layoutNeeded, … } …
}
```

A frame whose only work was a **bound** transform/opacity/fill write (the compositor bypass) does **nothing in 3/6** —
no `Flush` work to apply, `layoutNeeded` is false — so it skips render, reconcile, and layout and just re-records.
`FrameStats.Rendered` comes back `false`. That is the cheapest update path; see
[`rendering-and-performance.md`](../../guide/rendering-and-performance.md#the-compositor-bypass-the-slider-tank-killed).

## The alloc tripwire (`HotPhaseAllocBytes`)

`AppHost.Paint` brackets the paint half with two reads of `GC.GetAllocatedBytesForCurrentThread()` — one before the
reactive flush (phase 3), one after present (phase 11) — and publishes the delta as
**`FrameStats.HotPhaseAllocBytes`**. On a steady frame this **must be 0**: the zero-allocation contract for phases
6–13 (`Foundation` / `SPEC-INDEX.md`: "0 managed allocations in frame phases 6–13"). The harness asserts it directly
(inline check #9):

```csharp
for (int i = 0; i < 6; i++) host.RunFrame();   // warm caches/pools
var steady = host.RunFrame();
Check("8. steady frame does no work (memoized)", !steady.Rendered);
Check("9. ZERO managed alloc on the paint half (phases 6–11)", steady.HotPhaseAllocBytes == 0,
      $"{steady.HotPhaseAllocBytes} bytes");
```

`FrameStats` (returned from every `RunFrame`/`Paint`, defined at the top of `AppHost.cs`) is the diagnostics surface
you assert against:

| Field | Meaning |
|---|---|
| `Rendered` | a reconcile or layout happened this frame (`false` ⇒ a compositor-only or idle frame) |
| `ComponentsRendered` | how many component render-effects ran — the granularity metric (ideally 1 after a localized `setState`) |
| `HotPhaseAllocBytes` | managed bytes allocated in the bracketed paint half — **must be 0** on steady frames |
| `ClicksHandled` | clicks dispatched this frame (the input round-trip) |
| `DrawCommandCount` / `NodesVisited` / `DrawNodeCount` / `CulledNodeCount` | record-phase stats |
| `Fps` / `FrameMs` | smoothed timing |

> Honesty + scope note: this per-thread delta is the *localizer*, not the whole story. `GC.GetAllocatedBytesForCurrentThread()`
> does **not** follow work across the render-thread seam — once tessellation/raster fan out to workers, a per-UI-thread
> delta reads 0 while a worker allocates. The *load-bearing* zero-alloc gate is the process-wide BenchmarkDotNet
> gen0/1/2==0 backstop owned by [Validation and Gates](./validation-and-gates.md) (`validation.md` §3.1). In the
> single-thread harness here there are no workers, so the per-thread bracket is exact — but don't read it as the
> production proof on its own.
>
> The bracket covers phases 3–11 (`before` opens at the flush, `hotAlloc` closes after present); reconcile/layout work in
> phases 3–6 is *measured* and is expected to be 0 on a steady frame but **bounded Gen0 at the reconcile edge** on a
> structural change (a `setState` that mounts a subtree). The contract is zero on the *steady* paint half, not zero
> always — say "near-zero-allocation", never "zero".

## Running the harness

```bash
dotnet run --project src/FluentGpu.VerticalSlice
```

It constructs the engine on the headless seams, drives a deterministic sequence of frames + synthetic input, and prints
one `[PASS]`/`[FAIL]` line per assertion, ending with a verdict. The exact success line (emitted by `Program.cs`):

```text
FluentGpu — minimum vertical slice (headless RHI/PAL/Text)

  [PASS] 1. window + GPU clear + present  (backend=Headless, clear=#…)
  [PASS] 2. rounded-rect primitives (2 accent buttons × fill + gradient elevation border)
  …
ALL CHECKS PASSED — the vertical slice exercises every seam end-to-end.
```

Exit code is `0` on all-pass, `1` if any check failed (`s_failures` is the running count; `Main` returns it). That exit
code is what a pre-commit hook / CI step keys on.

The run is **fast (seconds) and deterministic** — no GPU, no window, no wall-clock timing (the headless path uses a
fixed frame-time source so animation ticks are reproducible). That determinism is the whole point: it catches
cross-seam regressions a focused unit test won't, every time, with no flake.

## How the harness works

The harness is not a mock of the engine — it is the **real `AppHost`** running the **real** reconciler, layout, recorder,
animation, and image pipeline, with only the three *outermost* seams swapped for headless implementations:

| Seam | Real (Windows) | Headless (harness) | What it gives the test |
|---|---|---|---|
| PAL (platform/window/input) | `FluentGpu.Windows` (`Pal/`) | `HeadlessPlatformApp` / `HeadlessWindow` | synthetic window + `QueueInput` to inject events |
| RHI (GPU device/swapchain) | `FluentGpu.Windows` (`D3D12/`) | `HeadlessGpuDevice` | **decoded** DrawList command lists to assert against |
| Text (font system) | `FluentGpu.Windows` (`DirectWrite/`) | `HeadlessFontSystem` | deterministic glyph advances → reproducible layout |

**`HeadlessGpuDevice`** (`src/FluentGpu.Engine/Headless/Rhi/HeadlessGpuDevice.cs`) is a CPU/null encoder: its `SubmitDrawList`
walks the POD command stream and *decodes each opcode into a reusable typed list* — `LastRects`, `LastGlyphs`,
`LastClips`, `LastImages`, `LastStrokes`, `LastShadows`, `LastArcs`, `LastPolylines`, `LastGradients`,
`LastGradientStrokes`, `LastLayers`, `LastTabShapes`. So a test asserts **what the recorder actually emitted this frame**,
without a single pixel. It also tracks `ClipBalance`/`LayerBalance` (push/pop must net to 0 in a well-formed frame),
`Uploads`/`ResidentImages`/`Evictions` (residency assertions), and `FrameCount`/`LastClear`. The lists `Clear()` (retaining
capacity) each submit, so after warmup the device itself adds no per-frame allocation — which is why the alloc tripwire
stays honest.

**`HeadlessFontSystem`** (`src/FluentGpu.Engine/Headless/Text/HeadlessFontSystem.cs`) replaces DirectWrite shaping with a
deterministic uniform-advance model the checks are written against by exact constant:

```text
advance/char = SizeDip × 0.55 (weight < 600) | SizeDip × 0.62 (weight ≥ 600) + SizeDip × CharSpacing/1000
line height  = SizeDip × 1.4 ; baseline = SizeDip × 1.1
wrap         = greedy word-wrap on ' ' runs
```

Because `Measure`, `HitTestText`, `GetCaret`, and `GetRangeRects` all run the *same* line walk, a headless hit-test agrees
with headless layout exactly — so the layout/text checks are reproducible across machines.

**`HeadlessWindow`** (`src/FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs`) is a synthetic window: `QueueInput(InputEvent)`
enqueues an event that `PumpInto` drains into the host ring on the next `RunFrame`. Its `ClientSizePx` and `Scale` are
settable mid-run, so a test can simulate a resize or a per-monitor DPI hop and assert the host re-lays-out. The headless
path also enables the full **windowed-popup** pipeline (its swapchains are independent), so out-of-bounds overlays are
verifiable here even though they're `needs-pixels` on D3D12 (see the `PopupWindowSlot` doc in `AppHost.cs`).

## The check categories

As of this writing the harness runs **hundreds of assertions** (all green, including the animation-rework dt-determinism and
scheduler-tick alloc gates): **9 inline core-slice checks** in `Main` (numbered 1–9, the minimum-vertical-slice
acceptance: window→clear→present, two rounded-rect buttons, three text runs, flex bounds, reconcile+`UseState`, a
clickable button round-trip, the steady idle frame, and the zero-alloc paint half), plus the rest spread across the
`…Checks(StringTable)` group methods (one per feature area, each called once from `Main`). The groups walk every seam
end-to-end — a non-exhaustive sample of the shape:

- **Reactivity & granularity** — `GranularityChecks` (a leaf `setState` re-renders *only* its owner: `ComponentsRendered == 1`),
  `SliderSignalChecks` (the compositor bypass: a bound slider value moves the thumb transform with `Rendered == false`),
  `FlowChecks` / `FlowReorderChecks` (`Flow.For`/`Flow.Show` restructure with no parent re-render).
- **Layout** — `FlexChecks`, `WrapChecks`, `GridChecks`, `GridStretchChecks`, `AutoGridChecks`.
- **Scroll & virtualization** — `ScrollChecks`, `VirtualChecks` (10k rows, windowed + recycled), `VariableChecks`
  (Fenwick extent table), `ZeroAllocScrollChecks` (an in-window scroll frame allocates 0).
- **Hooks & context** — `HookChecks` (`UseReducer`/`UseMemo`/`UseRef`), `NestedChecks`, `ContextChecks`.
- **Animation** — `AnimChecks`, `ProjectionChecks` (FLIP), `EnterExitChecks`, `SizeModeChecks`, `ReflowChecks`,
  `BrushTransitionChecks`.
- **Images** — `ImageCacheChecks`, `DecodeSchedulerChecks`, `BlurHashChecks`, `ImageEvictChecks`, `UseImageChecks`.
- **Controls & input** — `ControlsChecks`, `RepeatButtonChecks`, `TextInputChecks`, `OverlayChecks`,
  `E5DragDropChecks`, `FocusNavChecks`, plus the WinUI-parity waves (`W1ControlsChecks`, `Wave2ControlChecks`,
  `WaveCTextPipelineChecks`, the `D1…`/`D5…` defect-fix groups, `E11VirtChecks` for the virtualization substrate).
- **Navigation** — `NavigationChecks`, `NavigationViewChecks`, `NavHierarchyChecks`, `PipsPagerOutputChecks`.

> Keeping the counts honest: totals **will drift** as checks are added — that's expected and good. Don't hard-code a
> count anywhere that has to match; the verdict line and the exit code are the contract, not the total. The live number
> is printed on the success line (`ALL CHECKS PASSED … (N checks; …)`).

## Layout

| Path | Role |
|---|---|
| `Program.cs` | Thin runner: `FG_PROBE`, `--suite` / `FG_SUITE`, `CoreSuite` + `SuiteRegistry` |
| `Harness/Gate.cs` | `Check`, failure counters, arena summary |
| `Harness/Asserts.cs` | Scene / draw-list / input helpers (`Child`, `HasGlyph`, `Near`, …) |
| `Harness/HeadlessFixture.cs` | Optional using-friendly `AppHost` bootstrap |
| `Harness/SuiteRegistry.cs` | Explicit ordered suite list (no reflection — AOT-safe) |
| `Suites/*Suite.cs` | Domain check groups (`ScrollSuite.Run`, `HooksSuite.Run`, …) |
| `Probes/` | Probe components + `FG_PROBE` drivers |

## The `Check(...)` primitive and the fixture pattern

Every assertion goes through one tiny primitive in `Harness/Gate.cs` (imported via `using static`):

```csharp
public static void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $"  ({detail})")}");
    Total++;
    if (!ok) Failures++;
}
```

`name` is a stable, numbered description; `ok` is the boolean assertion; `detail` is an *always-evaluated* diagnostic
string (printed on PASS and FAIL alike) so a failure tells you the observed values, not just "false." Write the `detail`
to show the numbers you compared — `$"thumbDx {dx0:0}→{dx1:0} rendered={f.Rendered}"` — because a bare FAIL is useless
when the harness is green except for your one line.

Each group method builds its **own** `AppHost` fixture from scratch (isolated state, no shared host between groups), drives
frames, and asserts. The canonical fixture:

```csharp
static void SliderSignalChecks(StringTable strings)
{
    using var app    = new HeadlessPlatformApp();
    var       window = new HeadlessWindow(new WindowDesc("slidersig", new Size2(320, 120), 1f));
    window.Show();
    var       device = new HeadlessGpuDevice();
    var       fonts  = new HeadlessFontSystem(strings);
    SliderSignalProbe.Renders = 0;                 // reset the probe's static counter
    var       root   = new SliderSignalProbe();    // a Probe component (see below)
    using var host   = new AppHost(app, window, device, fonts, strings, root);

    host.RunFrame();                               // frame 1: mount
    int renders0 = SliderSignalProbe.Renders;

    var thumbRow = Child(host.Scene, host.Scene.Root, 1);   // navigate the scene by structure
    var thumb    = Child(host.Scene, thumbRow, 0);
    float dx0    = host.Scene.Paint(thumb).LocalTransform.Dx;

    root.Sig!.Value = 0.7f;                         // a real drag would write exactly this signal
    var f   = host.RunFrame();                      // frame 2: the bound write re-records only
    float dx1 = host.Scene.Paint(thumb).LocalTransform.Dx;

    Check("60. signal-bound slider: value→transform, NO re-render/reconcile/layout (the slider tank, fixed)",
        MathF.Abs(dx1 - dx0) > 50f                  // the thumb moved
        && SliderSignalProbe.Renders == renders0    // the owning component did NOT re-render
        && !f.Rendered,                             // no reconcile + no layout this frame
        $"thumbDx {dx0:0}→{dx1:0} renders+{SliderSignalProbe.Renders - renders0} rendered={f.Rendered}");
}
```

**The Probe pattern.** A *Probe* is a small `Component` under `Probes/` (or nested in the owning suite) that
exposes observable state for the assertion — a `static int Renders` it bumps in `Render()`, a `Signal<T>` field the test
writes to drive it, or a captured hook value. The test resets the static, mounts the probe as the root, runs a frame,
pokes a signal, runs another frame, and reads the probe back. Existing probes to copy: `Counter` (the slice root),
`GranParent`/`Gran` (per-component render counters), `SliderSignalProbe`, `FlowProbe`, `HookProbe` (captures
`UseReducer`/`UseMemo`/`UseRef`), `VirtualProbe`/`BoundVirtualProbe` (template-call counters), `ControlsProbe`. Reach for
an existing probe before writing a new one.

## The finder/comparer toolbox

`Harness/Asserts.cs` carries a small set of static helpers for navigating the scene and comparing what got drawn. Use these
instead of re-deriving tree walks:

| Helper | Signature | Use |
|---|---|---|
| `Child` | `Child(SceneStore s, NodeHandle parent, int index)` | the *n*-th child by sibling order (structural navigation) |
| `FindRole` | `FindRole(SceneStore s, NodeHandle n, AutomationRole role)` | DFS for the first node with an automation role — locate a control without knowing its exact position |
| `CenterOf` | `CenterOf(SceneStore s, NodeHandle n)` | absolute-rect center as a `Point2` — feed it to a synthetic `PointerDown`/`PointerUp` |
| `ColorClose` | `ColorClose(ColorF a, ColorF b, float tol)` | **full-ARGB** comparison (R, G, B *and* A within `tol`) |
| `Near` | `Near(float a, float b)` / `Near(float a, float b, float tol)` | float compare (default tolerance 0.5) |
| `HasGlyph` / `CountGlyph` / `GlyphColor` | over `device.LastGlyphs` + `StringTable` | "was this text drawn / how many / what color" |

> Why `ColorClose` is **full-ARGB**, not RGB-only: many WinUI foreground state changes (disabled, pressed, secondary
> text) are **alpha-only** — same RGB, different `A`. An RGB-only comparison silently passes a broken disabled state.
> Always compare the alpha channel. This is a real, recurring control-parity failure mode.

The two halves you assert against — *the scene* and *the DrawList* — come from:

- **`host.Scene`** (a `SceneStore`): post-layout topology + per-node data. `host.Scene.Root`, `FirstChild`/`NextSibling`
  (and the `Child` helper), `host.Scene.Bounds(n)` (local rect), `host.Scene.AbsoluteRect(n)` (summed up the chain),
  `host.Scene.Paint(n)` (`LocalTransform`/`Opacity`/`Fill`/`Text`/`VisualKind`), `host.Scene.Flags(n)` (`Focused`,
  `Scrollable`, `Visible`, the dirty axes), `host.Scene.Interaction(n)` (automation role, hit-testability).
- **`device.LastRects` / `device.LastGlyphs` / `device.Last*`**: the decoded command lists from this frame's submit — the
  ground truth of *what was drawn*. (`device.FrameCount`, `device.ClipBalance`, `device.ResidentImages`, swapchain
  `PresentCount` round out the render-side asserts.)

Synthetic input is a `QueueInput` of an `InputEvent` (`src/FluentGpu.Engine/Seams/Pal/Pal.cs`):

```csharp
var c = CenterOf(host.Scene, plus);   // some clickable node
window.QueueInput(new InputEvent(InputKind.PointerDown, c, button: 0, keyCode: 0));
window.QueueInput(new InputEvent(InputKind.PointerUp,   c, button: 0, keyCode: 0));
var f = host.RunFrame();              // the click is pumped, dispatched, and handled this frame
Check("clickable", f.ClicksHandled == 1);
```

`InputKind` covers `PointerMove`/`PointerDown`/`PointerUp`, `Key`/`KeyUp`/`Char`, `Wheel`, `PointerCancel`,
`WindowBlur`/`WindowFocus`/`WindowStateChanged`. `InputEvent` is a POD record:
`InputEvent(InputKind Kind, Point2 PositionPx, int Button, int KeyCode, float ScrollDelta = 0, KeyModifiers Mods = None, …)`.

## Adding a check group

The workflow is small and mechanical:

1. **Write a Probe** under `Probes/` (or nest it in the owning suite) that exposes the state your assertion reads — a
   static counter, a `Signal<T>` field, or a captured value.
2. **Write a `static void MyFeatureChecks(StringTable strings)`** on the domain suite class (`Suites/ScrollSuite.cs`, …)
   following the fixture pattern: build the headless app + window + device + fonts, reset the probe's statics,
   `new AppHost(...)`, `RunFrame()` to mount, drive input/signals, `RunFrame()` again, then one or more `Check(...)`
   calls with a numbered name and a numeric `detail`.
3. **Register the call** in that suite's `Run(StringTable)` method. If you add a *new* suite type, also add one line to
   `Harness/SuiteRegistry.All` (explicit ordered list — no reflection).
4. **Verify**: `dotnet build src/FluentGpu.VerticalSlice` clean, then
   `dotnet run --project src/FluentGpu.VerticalSlice` → `ALL CHECKS PASSED`. For a faster local loop while editing one
   area: `dotnet run --project src/FluentGpu.VerticalSlice -- --suite scroll` (or `FG_SUITE=hooks`). CI must run the
   full suite (no `FG_SUITE`). Confirm your new lines print `[PASS]` (and *make them fail once* on purpose to confirm
   the assertion actually bites — a check that can't fail is worse than no check).

Keep each group self-contained (its own host) so a failure is isolated and the suite stays order-independent.

## GPU pixels are out of scope here

The harness deliberately does **not** render pixels — it decodes the DrawList and asserts on commands. AA fringing,
gamma/premultiplied-blend correctness, the erf shadow, tessellation watertightness, and ClearType subpixel placement are
**`needs-pixels`**: they cannot be judged from the command stream and are not asserted by the headless suite. Those are
owned by the golden-image perceptual diff (CIEDE2000 + edge-shift) and the `render.aa` spike in
[Validation and Gates](./validation-and-gates.md) (`validation.md` §2.3/§3.3), which run against `Rhi.Headless`'s WARP
rasterizer and, nightly, real D3D12 hardware.

For a quick human eyeball on the real D3D12 path, run the windowed gallery app (`FluentApp.Run` — see
[Getting started](../../guide/getting-started.md)) and capture a screenshot manually; that is the manual companion to the
automated structural suite, not part of `dotnet run --project src/FluentGpu.VerticalSlice`. Treat a green harness as
"structure is correct" and the pixel pass as a separate, orthogonal gate — a shader change must pass *both*.

## The pre-commit self-check and the canon gate

Before committing an engine change, the discipline (from the guide's
[pitfalls page](../../guide/pitfalls.md#quick-self-check-before-committing-an-engine-change)) is:

1. `dotnet build src/FluentGpu.VerticalSlice` — clean.
2. `dotnet run --project src/FluentGpu.VerticalSlice` — `ALL CHECKS PASSED`.
3. If you touched reactivity / layout / render, confirm `FrameStats.Rendered` / `ComponentsRendered` /
   `HotPhaseAllocBytes` are what you expect on the relevant interaction — and **add a `Check`** that pins it, so the
   next person can't regress it silently.
4. If you edited `design/` (not `docs/`), the **canon gate** must pass:
   `powershell -File design/check-canon.ps1` exits `0`. It fails if a stale/superseded token reappears in the live design
   tree; fix the token or annotate the line with `<!-- canon-allow: reason -->`. (Usage docs like this page live under
   `docs/` and are *not* scanned — the gate only reads `design/`.)

> 🤖 For agents especially: "it builds, ship it" is the #1 way a seam regresses unnoticed. Run the harness and show its
> output (or the relevant `FrameStats`) before claiming a change works. Evidence before assertions.

## Canon link: the validation subsystem

This page covers the *fast, portable, structural* tier — the headless golden harness that gates every PR-sized engine
change in seconds. The full maturity program (the two clocks: capability **spikes** and per-PR regression **gates**, the
process-wide alloc backstop, the COM net-refcount/leak gate, the epoch fault-injection witness, the `[Capability]`
trust-ring analyzer, the `seam.race` soak, and the honest list of what is *not* guarded) is the design-of-record in
[`subsystems/validation.md`](../../../design/subsystems/validation.md) and is summarized for contributors in
[Validation and Gates](./validation-and-gates.md). The blunt invariant that ties them together: every runtime guard is
`[Conditional]`-erased from the shipping NativeAOT binary, so **in the customer's hands, production safety *is* CI
coverage** — which is exactly why the harness on this page has to stay green and exhaustive.

---

**Related pages:** [Contributor Map: where to change what](./contributor-map.md) ·
[Signals and reactivity internals](./signals-and-reactivity-internals.md) ·
[Validation and Gates](./validation-and-gates.md) · guide:
[Getting started](../../guide/getting-started.md) ·
[Rendering & performance](../../guide/rendering-and-performance.md) ·
[Pitfalls](../../guide/pitfalls.md)
