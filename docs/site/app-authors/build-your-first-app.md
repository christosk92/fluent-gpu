# Build your first app

This is the app-author's on-ramp to FluentGpu. By the end you will have a window on screen, a
live counter wired to a signal, and a clear picture of what the one-line entry point actually
brings up — plus the lower-level frame loop you can drive yourself when you need to.

FluentGpu is a near-zero-allocation, NativeAOT, GPU-rendered .NET 10 UI engine with a React/Reactor
authoring surface (immutable `Element` records + `Component` + hooks) over a **signals-first**
reactive core. The mental model in one sentence: *a change reaches pixels through one mechanism — a
signal* — and the cheapest updates (a bound prop) never touch reconcile or layout at all. If that
sentence is new to you, read [reactivity](../../guide/reactivity.md) next; this page stays focused
on getting an app running.

> All the symbols below are the real surface in `src/FluentGpu.WindowsApp/FluentApp.cs` and
> `src/FluentGpu.Engine/Hosting/AppHost.cs`. The signatures here are copied from source, not paraphrased.

## Hello, FluentGpu (`FluentApp.Run`)

The entire SDK is one call. It creates a DPI-aware window, brings up Direct3D 12, applies Mica plus
the real system accent color, wires the DirectWrite font system and the frame loop, and renders your
root component:

```csharp
using FluentGpu;                 // FluentApp
using FluentGpu.Hooks;           // Component
using static FluentGpu.Dsl.Ui;   // VStack, Heading, Text, Button…

FluentApp.Run(() => new App());

sealed class App : Component
{
    public override Element Render()
    {
        var (n, setN) = UseState(0);
        return VStack(gap: 12,
            Heading("Hello, FluentGpu"),
            Text($"Clicked {n} times"),
            Button.Accent("Click me", () => setN(n + 1)));
    }
}
```

That is a complete, runnable app. `App` derives from `Component`, overrides `Render()`, and returns
an `Element` tree built with the `Ui.*` factories you brought in with `using static FluentGpu.Dsl.Ui`.
`Render()` is the one method you must implement — `public abstract Element Render()` on
`Component` (`src/FluentGpu.Engine/Hooks/Component.cs`).

To run the verification harness or any non-windowed scenario, see
[Run headless](#running-headless-tests--ci) at the end of this page.

## The `FluentApp.Run` options

`FluentApp.Run` has two overloads (`src/FluentGpu.WindowsApp/FluentApp.cs`):

```csharp
public static void Run(Func<Component> root, string title = "FluentGpu",
                       int width = 800, int height = 600, bool mica = true, int frames = -1,
                       string? screenshot = null, bool customFrame = false);

public static void Run<T>(string title = "FluentGpu", int width = 800, int height = 600)
    where T : Component, new();
```

The generic overload is sugar for a parameterless root: `FluentApp.Run<App>()` is exactly
`FluentApp.Run(() => new App())` with the same `title`/`width`/`height` defaults.

| Parameter | Default | What it does |
|---|---|---|
| `root` | — | A factory that builds your root `Component`. Called once, at mount. |
| `title` | `"FluentGpu"` | The window title. |
| `width` / `height` | `800` / `600` | Initial client size in DIPs (logical pixels at scale 1.0). |
| `mica` | `true` | Apply the Mica system backdrop + a transparent window background + the composited swapchain. `false` gives an opaque window. |
| `frames` | `-1` | Run forever (`-1`), or auto-exit after exactly `frames` frames — handy for deterministic screenshots and smoke tests. |
| `screenshot` | `null` | When set, paces the loop at a fixed ~8 ms/frame, then reads the last back buffer back to the CPU and writes a PNG to this path for visual diffing. |
| `customFrame` | `false` | Opt in to drawing your own title bar (caption stripped, engine caption buttons, snap layouts). Apps that don't pass this keep the standard OS frame. The gallery app uses this. |

A few things worth knowing:

- **`frames` + `screenshot` together** are how the test/gallery tooling captures a frame
  deterministically: `FluentApp.Run(() => new App(), frames: 1, screenshot: "out.png")` renders one
  frame, writes the PNG, and exits.
- **`customFrame: true`** is an explicit opt-in because it changes window chrome ownership. Don't
  set it unless you are actually rendering a title bar (FluentGpu ships a WinUI-style one — see the
  controls guide).
- **Diagnostics**: set the `FG_DIAG` (or `FG_DIAG_CONSOLE`) environment variable to route engine
  diagnostics to stderr; `FG_ALLOC_DIAG=1` prints a once-per-second per-phase allocation/CPU
  attribution. These are read inside `FluentApp.Run` at startup. `FG_DUMP=1` dumps the post-layout
  scene tree once.

## What the one-liner wires up

`FluentApp.Run` is deliberately thin — it is the composition root, and reading it top to bottom is
the fastest way to understand the engine's moving parts. Here is the same sequence the source runs:

1. **`Win32App`** — the Windows platform layer (PAL): window creation, the message pump, clipboard,
   cursors, monitor work areas. `app.CreateWindow(new WindowDesc(title, new Size2(width, height), 1f, mica, CustomFrame: customFrame))`
   creates the `Win32Window`.
2. **Accent + Mica** — the real system accent is pulled via `Win32Theme.AccentLight2()` /
   `Win32Theme.Accent()` and assigned to `Theme.Accent`; `Win32Theme.ApplyWindowMaterial(...)`
   applies the Mica/dark material. With `mica: true` the window background becomes
   `ColorF.Transparent` so the backdrop shows through.
3. **DirectWrite** — `new DirectWriteFontSystem(strings)`. Text *measurement* runs through the same
   DirectWrite advances and line-break math the D3D12 glyph renderer uses, so measured wrap/height
   matches rendered wrap/height exactly.
4. **D3D12** — `new D3D12Device(strings, composited: mica)`. The GPU device + swapchain;
   `composited` ties it to the Mica/DirectComposition path.
5. **Image pipeline** — a WIC decoder on a worker pool behind a disk-cached HTTP/2 fetcher
   (`DefaultImageFetcher` → `DecodeScheduler` → `ImageCache`). This is what `Ui.Image` and the
   `UseImage` hook draw from.
6. **`AppHost`** — `new AppHost(app, window, device, fonts, strings, root(), images)`. The
   composition root and the single-UI-thread frame loop. `host.SmoothScroll = true` turns on
   inertial wheel scrolling + auto-hiding scrollbars (the real-app default).
7. **The loop** — `window.Show()` then a `while (!window.IsClosed)` loop that calls
   `host.RunFrame()` and sleeps until there's work (see the next sections).

You never need to assemble this yourself for a normal app — but when something at the seam misbehaves
(text metrics, GPU bring-up, Mica), this list tells you which object owns it. For the architecture
behind the seam, see the design corpus: [`architecture-spec.md`](../../../design/architecture-spec.md)
and the [precedence authority](../../../design/SPEC-INDEX.md).

## A live counter (`UseState`)

The counter in the first example is the whole reactive model in miniature. `UseState<T>` is a hook on
`Component` (`src/FluentGpu.Engine/Hooks/Component.cs`):

```csharp
protected (T Value, Action<T> Set) UseState<T>(T initial) => Context.UseState(initial);
```

It returns the current value and a setter. Calling the setter writes a signal that schedules **only
this component's render-effect** — not the whole app. That is the signals-first promise: state lives
where it's used, and a write re-runs exactly the computations that read it.

```csharp
sealed class Counter : Component
{
    public override Element Render()
    {
        var (n, setN) = UseState(0);
        return HStack(gap: 8,
            Button("−", () => setN(n - 1)),
            Text($"{n}"),
            Button("+", () => setN(n + 1)));
    }
}
```

Two rules that matter from day one (both from [pitfalls](../../guide/pitfalls.md)):

- **Hooks run in stable order.** Call `UseState` (and every other `Use*` hook) unconditionally at the
  top of `Render()` — never inside an `if`/loop. The hook cells are slot-indexed, so changing the
  call order corrupts state. This is the React rules-of-hooks.
- **Never write a signal during render.** A `setState` inside the same component's `Render()`
  re-schedules that render and you'll see `Flush exceeded 1000 iterations`. Writes belong in event
  handlers (the `() => setN(...)` lambda above) or in `UseEffect`.

If you want a counter with **no re-render at all** — the value bound straight to a node channel — that
is the `ReactiveComponent` + bound-prop path. `ReactiveComponent.Setup()` runs *once*; everything
dynamic is a bound prop, a `Flow.For`, or a `Flow.Show`. That distinction (re-render vs. bind vs.
control-flow) is the core of [reactivity](../../guide/reactivity.md); start with `Component` +
`UseState` and reach for binds when a profiler (or `FrameStats.Rendered`) tells you to.

## Driving the frame loop yourself

`FluentApp.Run` owns an `AppHost` and pumps it for you. When you need the loop — a custom host, an
embedded surface, your own idle policy — open it up. This is exactly what `FluentApp.Run` does
internally:

```csharp
using var host = new AppHost(app, window, device, fonts, strings, root(), images);
host.SmoothScroll = true;
window.Show();
while (!window.IsClosed)
{
    host.RunFrame();                                  // pump → input → flush reactive work → layout → record → present
    window.WaitForWork(host.HasActiveWork ? 0 : -1); // sleep until input/animation/IO when idle (no busy spin)
}
```

The three members you actually call:

- **`host.RunFrame()`** runs one full frame and returns a [`FrameStats`](#reading-framestats). It
  pumps the OS message queue, dispatches input, flushes the reactive runtime, does (scoped) layout
  only when something changed, records the DrawList, submits, and presents. When nothing is pending
  it does almost nothing and returns a `Rendered: false` stats with no work done.
- **`host.HasActiveWork`** is `true` when there's pending reactive work, a running animation,
  in-flight image IO, exit orphans, or an active drag. When it's `false`, block in
  `WaitForWork(-1)` until the OS delivers input — the loop is **event-driven, not a busy spin**.
  (`HasActiveWork` is an OR over the reactive runtime, animators, the image cache, orphans, and the
  drag controllers — see `AppHost.HasActiveWork`.)
- **`host.Paint(int clicks = 0)`** is the pump-free half — phases 3–12 only, no OS pump, so it's
  safe to call from a window-proc callback. The window's `PaintRequested` callback calls
  `Paint(0)` so the surface keeps redrawing during the OS modal move/resize loop (when your normal
  `RunFrame` loop is blocked inside the OS).

Two more knobs on `AppHost` you'll meet early:

- **`host.SmoothScroll`** (`bool`, get/set) — inertial smooth scrolling + auto-hiding scrollbars.
  The real app turns this on; off means scrolling is immediate. (`FluentApp.Run` sets it `true`.)
- **`host.Images`** — the `ImageCache` the host renders from, if you need to prefetch or inspect it.
- **`host.Scene`** — the retained, post-layout `SceneStore`. Headless tests assert on this (node
  bounds/flags); see below.

> Pacing: in the `FluentApp.Run` loop, active frames are paced by the swapchain present path, so the
> idle branch waits with `WaitForWork(host.HasActiveWork ? 0 : -1)` — `0` when there's work (run the
> next frame now), `-1` (block) when idle. Don't add a fixed timed wait on active frames; it skews
> animation timing and the FPS diagnostics. (The one exception is `screenshot` mode, which paces at
> a fixed ~8 ms so time-driven animations advance deterministically.)

### `AppHost.SmoothScroll` and inertial scrolling

`SmoothScroll` is the only loop knob most apps touch. It's a passthrough to the input dispatcher
(`public bool SmoothScroll { get => _dispatcher.SmoothScroll; set => _dispatcher.SmoothScroll = value; }`).
Leave it `true` for the WinUI-like feel; set it `false` if you want wheel deltas applied immediately
with no fade or inertia.

## Reading `FrameStats`

Every `RunFrame()`/`Paint()` returns a `FrameStats` (and the last one is on `host.LastStats`). It is
your diagnostics window into what the frame actually did — and the primary way you *verify* a
performance change. The shape (`src/FluentGpu.Engine/Hosting/AppHost.cs`):

```csharp
public readonly record struct FrameStats(
    int DrawCommandCount, int ClicksHandled, long HotPhaseAllocBytes, bool Rendered)
{
    public int NodesVisited { get; init; }
    public int DrawNodeCount { get; init; }
    public int CulledNodeCount { get; init; }
    public double Fps { get; init; }
    public double FrameMs { get; init; }
    public int ComponentsRendered { get; init; }
}
```

| Field | Meaning |
|---|---|
| `Rendered` | A reconcile or layout happened this frame. `false` ⇒ a compositor-only frame (a bound transform/opacity moved pixels with no render/reconcile/layout). |
| `ComponentsRendered` | How many component render-effects ran — the granularity metric. One interaction should re-render the owning subtree, not the world. |
| `HotPhaseAllocBytes` | Managed bytes allocated in the paint half. **Must be 0** on steady frames — that's the near-zero-allocation contract for the hot phases. |
| `DrawCommandCount` | DrawList commands recorded this frame. |
| `NodesVisited` / `DrawNodeCount` / `CulledNodeCount` | Record-phase stats: scene nodes walked, nodes that drew, nodes culled. |
| `ClicksHandled` | Pointer clicks dispatched this frame. |
| `Fps` / `FrameMs` | Smoothed timing over a 1-second window. |

The two you'll lean on most when tuning, straight from [pitfalls](../../guide/pitfalls.md):

- Dragging a slider should show **`Rendered == false`** (compositor-only). If it's `true`, you're
  re-rendering per pointer-move — bind the value instead of `setState`.
- The zero-alloc check is **`HotPhaseAllocBytes == 0`** on steady frames. A non-zero value means an
  allocation crept into a bind thunk or hot effect (a `new`, LINQ, boxing, or a per-call closure).

`FrameStats` is also surfaced to components as ambient context: a HUD can read it via
`UseContext(FrameDiagnostics.Current)` — the host only boxes the stats when something is actually
subscribed.

## Running headless (tests / CI)

No GPU or window required: swap the `Win32`/`D3D12`/`DirectWrite` seams for their `Headless`
counterparts and drive `RunFrame()` by hand. This is exactly how the golden-check harness runs, and
it's the recommended way to verify app logic deterministically:

```csharp
using FluentGpu.Foundation;      // StringTable
using FluentGpu.Hosting;         // AppHost
using FluentGpu.Pal.Headless;    // HeadlessPlatformApp, HeadlessWindow  (src/FluentGpu.Engine/Headless/Pal/)
using FluentGpu.Rhi.Headless;    // HeadlessGpuDevice                    (src/FluentGpu.Engine/Headless/Rhi/)
using FluentGpu.Text.Headless;   // HeadlessFontSystem                   (src/FluentGpu.Engine/Headless/Text/)

var strings = new StringTable();
using var app = new HeadlessPlatformApp();
var window = new HeadlessWindow(new WindowDesc("test", new Size2(1280, 800), 1f));
window.Show();
var device = new HeadlessGpuDevice();
var fonts  = new HeadlessFontSystem(strings);
using var host = new AppHost(app, window, device, fonts, strings, new App());

host.RunFrame();                                                     // drive frames deterministically
window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0));  // synthesize input
host.RunFrame();
// assert on host.Scene (post-layout bounds/flags) and device.LastGlyphs/LastRects (what was recorded)
```

The headless device records the DrawList into inspectable lists (`LastGlyphs`, `LastRects`,
`LastGradientStrokes`, …) so a test can assert *what was drawn* without pixels, and `host.Scene`
exposes the post-layout tree. The single source of truth for "does the engine still work" is the
cross-seam harness:

```bash
dotnet run --project src/FluentGpu.VerticalSlice
```

It runs ~60 end-to-end golden checks across every seam and prints `ALL CHECKS PASSED`. Add a check
with a `Check("…", condition, detail)` in `src/FluentGpu.VerticalSlice/Program.cs`. See
[getting-started](../../guide/getting-started.md#the-verification-harness) for the full harness
notes. (GPU pixels are *not* asserted here — those are a separate, manual check on the real D3D12
path.)

## The project layout (what you touch most)

The solution (`src/FluentGpu.slnx`) is **8 projects**: four libraries (`FluentGpu.Engine`,
`FluentGpu.Controls`, `FluentGpu.Windows`, `FluentGpu.WindowsApi`), two Roslyn source-generator
analyzers (`FluentGpu.SourceGen`, `FluentGpu.Interop.SourceGen`), and two app projects
(`FluentGpu.VerticalSlice` validation harness, `FluentGpu.WindowsApp` gallery). As an app author
you reference a handful and almost never open the rest:

| Project / folder | What's in it — and when you touch it |
|---|---|
| `FluentGpu.WindowsApp` | `FluentApp.Run` (your entry point) **and** the gallery app that demonstrates every control. Start here. |
| `FluentGpu.Engine` / `Dsl/` | `Element` records, the `Ui.*` builders, `Modifiers`, and theming (`Tok`/`Theme`). This is the authoring surface you compose with. |
| `FluentGpu.Engine` / `Hooks/` | `Component` / `ReactiveComponent`, the hooks (`UseState`, `UseSignal`, `UseEffect`, …), context and control-flow. |
| `FluentGpu.Controls` | Button / IconButton / ToggleButton / Slider / ScrollBar / NavigationView / Repeater / Virtual / Navigator — composition over the DSL, no rendering. |
| `FluentGpu.Engine` / `Foundation/` | Handles, allocators, `ColorF`/`Affine2D`/geometry, the **Signals reactive core** (`Foundation/Signals/`), `StringTable`. |
| `FluentGpu.Engine` / `Hosting/` | `AppHost` (the frame loop), `FrameStats`, `FrameDiagnostics` — this page's subject. |
| `FluentGpu.Windows` | The Windows backend: `Pal/` (Win32 windowing + theme), `D3D12/` (GPU), `DirectWrite/` (text). `FluentGpu.Engine/Headless/` holds the test counterparts. You swap Engine's headless folders for tests; otherwise leave them alone. |

The internals — `FluentGpu.Engine`'s `Reconciler/`, `Layout/`, `Scene/`, `Render/` folders, and
the seams' real backends in `FluentGpu.Windows` — are *engine* territory. You don't edit them to
build an app; if you're changing them, the where-to-change map and the verification harness live in
the engine docs and the [design corpus](../../../design/SPEC-INDEX.md).

## Next steps

- **[Reactivity](../../guide/reactivity.md)** — the signals model everything else builds on:
  re-render vs. bind vs. `Flow.For`/`Flow.Show`, and why props don't flow through constructors.
- **[Components, elements & layout](../../guide/components-elements-layout.md)** — the `Element`
  records, flex layout, and how components compose.
- **[Rendering & performance](../../guide/rendering-and-performance.md)** — scoped relayout, the
  compositor bypass, and the zero-alloc hot phases behind `HotPhaseAllocBytes`.
- **[Pitfalls](../../guide/pitfalls.md)** — symptom → cause → fix for the failure modes above. Read
  it before you debug.
- **[Getting started](../../guide/getting-started.md)** — the condensed version of this page plus the
  headless harness in full.
