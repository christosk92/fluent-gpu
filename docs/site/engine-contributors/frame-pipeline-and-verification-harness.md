# Frame pipeline and headless seams

> **✅ Animation engine — signals-first rework landed + verified.** The phase-7 animation tick is one `AnimScheduler` (`min(next-due)` wake, per-source `CadenceClass`) over a unified POD `AnimValue` slab — the former N independent tickers (interaction, brush, …) are folded in. Design, now implemented: [`../../plans/animation-engine-rework-design.md`](../../plans/animation-engine-rework-design.md) §11.

This page is for people **changing engine internals** — the frame loop, a seam, the recorder, the reconciler. If you
are building an app, read
[Performance rules and zero-alloc boundaries](../app-authors/performance-rules-and-zero-alloc-boundaries.md) and the
guide's [rendering & performance page](../../guide/rendering-and-performance.md) first — they own the *mental model*
(the three update paths, the compositor bypass). This page is the *machine*: the ordered phases, the alloc counter
that brackets them, and the headless seams that run the whole engine without a GPU or a window.

After **any** engine change, build Debug (`dotnet build src/FluentGpu.slnx`) clean. There are
no tests; for pixels use `FluentGpu.WindowsApp -- --screenshot <path>`, and for the app the headless inspector in
`WaveeMusic`.

**Where the code is** (the engine where-to-change map for this page):

| If you're changing… | Edit |
|---|---|
| The frame loop / phase order / `FrameStats` | `src/FluentGpu.Engine/Hosting/AppHost.cs` |
| The alloc-tripwire bracket (`HotPhaseAllocBytes`) | `src/FluentGpu.Engine/Hosting/AppHost.cs` (`Paint`) |
| The headless GPU device (decoded DrawList) | `src/FluentGpu.Engine/Headless/Rhi/HeadlessGpuDevice.cs` |
| The headless window / platform app / input | `src/FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs` |
| The deterministic font advance model | `src/FluentGpu.Engine/Headless/Text/HeadlessFontSystem.cs` |
| `InputEvent` / `WindowDesc` / the PAL seam | `src/FluentGpu.Engine/Seams/Pal/Pal.cs` |

The phase order and the SoA scene it patches are *described* (not re-derived) in the design canon: the 13-phase loop and
the minimum vertical slice live in
[`architecture-spec.md`](../../../design/architecture-spec.md), and the validation spine in
[`subsystems/validation.md`](../../../design/subsystems/validation.md). This page documents the *as-built* loop in
`AppHost.cs` and the headless seams under `src/FluentGpu.Engine/Headless/`.

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

## The alloc counter (`HotPhaseAllocBytes`)

`AppHost.Paint` brackets the paint half with two reads of `GC.GetAllocatedBytesForCurrentThread()` — one before the
reactive flush (phase 3), one after present (phase 11) — and publishes the delta as
**`FrameStats.HotPhaseAllocBytes`**. On a steady frame this **must be 0**: the zero-allocation contract for phases
6–13 (`Foundation` / `SPEC-INDEX.md`: "0 managed allocations in frame phases 6–13"). It is read after warming caches and pools with a few
`RunFrame()` calls.

`FrameStats` (returned from every `RunFrame`/`Paint`, defined at the top of `AppHost.cs`) is the diagnostics surface:

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
> delta reads 0 while a worker allocates. The process-wide gen0/1/2==0 backstop is described in
> [Validation and Gates](./validation-and-gates.md) (`validation.md` §3.1). On the headless seams there are no
> workers, so the per-thread bracket is exact — but don't read it as the production proof on its own.
>
> The bracket covers phases 3–11 (`before` opens at the flush, `hotAlloc` closes after present); reconcile/layout work in
> phases 3–6 is *measured* and is expected to be 0 on a steady frame but **bounded Gen0 at the reconcile edge** on a
> structural change (a `setState` that mounts a subtree). The contract is zero on the *steady* paint half, not zero
> always — say "near-zero-allocation", never "zero".

## The headless seams

The headless path is not a mock of the engine — it is the **real `AppHost`** running the **real** reconciler, layout, recorder,
animation, and image pipeline, with only the three *outermost* seams swapped for headless implementations:

| Seam | Real (Windows) | Headless | What it gives you |
|---|---|---|---|
| PAL (platform/window/input) | `FluentGpu.Windows` (`Pal/`) | `HeadlessPlatformApp` / `HeadlessWindow` | synthetic window + `QueueInput` to inject events |
| RHI (GPU device/swapchain) | `FluentGpu.Windows` (`D3D12/`) | `HeadlessGpuDevice` | **decoded** DrawList command lists to assert against |
| Text (font system) | `FluentGpu.Windows` (`DirectWrite/`) | `HeadlessFontSystem` | deterministic glyph advances → reproducible layout |

**`HeadlessGpuDevice`** (`src/FluentGpu.Engine/Headless/Rhi/HeadlessGpuDevice.cs`) is a CPU/null encoder: its `SubmitDrawList`
walks the POD command stream and *decodes each opcode into a reusable typed list* — `LastRects`, `LastGlyphs`,
`LastClips`, `LastImages`, `LastStrokes`, `LastShadows`, `LastArcs`, `LastPolylines`, `LastGradients`,
`LastGradientStrokes`, `LastLayers`, `LastTabShapes`. So you can read **what the recorder actually emitted this frame**,
without a single pixel. It also tracks `ClipBalance`/`LayerBalance` (push/pop must net to 0 in a well-formed frame),
`Uploads`/`ResidentImages`/`Evictions` (residency), and `FrameCount`/`LastClear`. The lists `Clear()` (retaining
capacity) each submit, so after warmup the device itself adds no per-frame allocation — which is why the alloc counter
stays honest.

**`HeadlessFontSystem`** (`src/FluentGpu.Engine/Headless/Text/HeadlessFontSystem.cs`) replaces DirectWrite shaping with a
deterministic uniform-advance model:

```text
advance/char = SizeDip × 0.55 (weight < 600) | SizeDip × 0.62 (weight ≥ 600) + SizeDip × CharSpacing/1000
line height  = SizeDip × 1.4 ; baseline = SizeDip × 1.1
wrap         = greedy word-wrap on ' ' runs
```

Because `Measure`, `HitTestText`, `GetCaret`, and `GetRangeRects` all run the *same* line walk, a headless hit-test agrees
with headless layout exactly — so layout/text are reproducible across machines.

**`HeadlessWindow`** (`src/FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs`) is a synthetic window: `QueueInput(InputEvent)`
enqueues an event that `PumpInto` drains into the host ring on the next `RunFrame`. Its `ClientSizePx` and `Scale` are
settable mid-run, so a driver can simulate a resize or a per-monitor DPI hop and assert the host re-lays-out. The headless
path also enables the full **windowed-popup** pipeline (its swapchains are independent), so out-of-bounds overlays are
visible here even though they're `needs-pixels` on D3D12 (see the `PopupWindowSlot` doc in `AppHost.cs`).

## Pixels are out of scope here

The headless seams deliberately do **not** render pixels — the device decodes the DrawList into commands. AA fringing,
gamma/premultiplied-blend correctness, the erf shadow, tessellation watertightness, and ClearType subpixel placement
cannot be judged from the command stream. For those, run the windowed gallery app (`FluentApp.Run` — see
[Getting started](../../guide/getting-started.md)) or capture a screenshot
(`FluentGpu.WindowsApp -- --screenshot <path>`) and look at it.

## Before committing, and the canon gate

Before committing an engine change, the discipline (from the guide's
[pitfalls page](../../guide/pitfalls.md#quick-self-check-before-committing-an-engine-change)) is:

1. `dotnet build src/FluentGpu.slnx` — clean (Debug).
2. If you touched reactivity / layout / render, confirm `FrameStats.Rendered` / `ComponentsRendered` /
   `HotPhaseAllocBytes` are what you expect on the relevant interaction.
3. If you edited `design/` (not `docs/`), the **canon gate** must pass:
   `powershell -File design/check-canon.ps1` exits `0`. It fails if a stale/superseded token reappears in the live design
   tree; fix the token or annotate the line with `<!-- canon-allow: reason -->`. (Usage docs like this page live under
   `docs/` and are *not* scanned — the gate only reads `design/`.)

## Canon link: the validation subsystem

The full maturity program (the two clocks: capability **spikes** and per-PR regression **gates**, the
process-wide alloc backstop, the COM net-refcount/leak gate, the epoch fault-injection witness, the `[Capability]`
trust-ring analyzer, the `seam.race` soak, and the honest list of what is *not* guarded) is the design-of-record in
[`subsystems/validation.md`](../../../design/subsystems/validation.md) and is summarized for contributors in
[Validation and Gates](./validation-and-gates.md). The test-based parts of it are not built: tests are banned. The
blunt invariant that ties it together: every runtime guard is `[Conditional]`-erased from the shipping NativeAOT
binary, so **in the customer's hands there are no guards**.

---

**Related pages:** [Contributor Map: where to change what](./contributor-map.md) ·
[Signals and reactivity internals](./signals-and-reactivity-internals.md) ·
[Validation and Gates](./validation-and-gates.md) · guide:
[Getting started](../../guide/getting-started.md) ·
[Rendering & performance](../../guide/rendering-and-performance.md) ·
[Pitfalls](../../guide/pitfalls.md)
