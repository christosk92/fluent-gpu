# Getting started

[← Guide index](./README.md)

> Building a **new app from the published package**? See **[Consuming FluentGpu via NuGet](./consuming-via-nuget.md)**.
> Starting an app against an engine **checkout**? Use the `fluentgpu-app` template — **[Starting a new app](./new-app.md)**.
> This page covers working inside the repo (project references) and driving the loop yourself.

## Run an app (Windows, batteries-included)

The entire SDK is one call — it creates a DPI-aware window, brings up D3D12, applies Mica + the real system accent,
wires the font system + frame loop, and renders your root component:

```csharp
using FluentGpu;                 // FluentApp
using FluentGpu.Dsl;             // Element
using FluentGpu.Hooks;           // Component
using FluentGpu.Controls;        // Button
using static FluentGpu.Dsl.Ui;   // VStack, Text…

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

`FluentApp.Run` signature (`src/FluentGpu.Windows/Hosting/FluentApp.cs`):

```csharp
public static void Run(Func<Component> root, AppOptions? options = null);
public static void Run<T>(AppOptions? options = null) where T : Component, new();
```

That's all you need to build apps. The rest of this page is for when you want to drive the loop yourself, or run
headless (tests/CI).

### Size the window to its content

A small window (an overlay, a widget, a dialog-like tool) does not need hand-computed DIP heights. Set
`AppOptions.SizeToContent` (the enum is in `FluentGpu.Hosting`) and the window follows the content's natural size:

```csharp
FluentApp.Run(() => new Widget(), new AppOptions
{
    Title = "Usage", Width = 340, Height = 200,       // only the size the first layout happens at
    SizeToContent = SizeToContent.Height,              // Manual (default) | Width | Height | WidthAndHeight
    MinWidth = 240, MinHeight = 120,                   // the fit never goes below these
});
FluentApp.SetSizeToContent(SizeToContent.Manual);     // live: stop (or start again) from code
```

- `Width` fits the natural (unconstrained) width; `Height` fits the natural height at the current width;
  `WidthAndHeight` takes the natural width, then the height at that width.
- It runs after every frame whose layout ran, so content that grows or shrinks (a row appears, a text wraps) resizes the
  window by itself. The size is converted at the effective scale, app zoom included (`ceil(dip × Scale)`), so N DIP of
  content gets N DIP of layout space at any zoom. The target is clamped to `MinWidth`/`MinHeight` and the work area of
  the monitor the window is on.
- The root must be content-sized on the fitted axis. Content that fills its parent (`Grow`, stretch, a plain
  `ScrollView`) has no natural size: that axis measures 0 and is left alone.
- The window is shown after its first fit, so it never flashes at `Width`/`Height` (with `StartHidden` it stays hidden).
- The user dragging the frame switches the mode to `Manual`, as in WPF. Fitting is skipped while the window is
  maximized, fullscreen, minimized or hidden, and during the OS move/size loop.
- Content whose size depends on the window size (a responsive layout that is wider in a narrow window) would resize
  forever; an A→B→A flip within four frames holds the window at its size until the content changes.
- `FluentApp.ResizeWindow(w, h)` is still there for a one-off size. It uses the raw DPI scale (app zoom does not apply).
- Under the hood: `AppHost.SizeToContent` and `AppHost.ShowWhenFitted()` (a custom host calls these itself),
  `FlexLayout.MeasureNatural(root, availW)`, `IPlatformWindow.SetClientSizePx`. Gates: `--suite size-to-content`.

## The frame loop you actually call

Under the hood, `FluentApp.Run` owns an `AppHost` and pumps it:

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

- **`host.RunFrame()`** runs one full frame and returns a `FrameStats`. It does nothing expensive when idle.
- **`host.HasActiveWork`** is `true` when there's pending reactive work, animation, image IO, or exit orphans. When
  it's `false`, block in `WaitForWork(-1)` until the OS delivers input — the loop is event-driven, not a busy spin.
- **`host.Paint(clicks)`** is the pump-free half (phases 3–12); the window's `PaintRequested` callback calls it so the
  window keeps redrawing during the OS modal move/resize loop.

`FrameStats` (returned each frame, `src/FluentGpu.Engine/Hosting/AppHost.cs`) is your diagnostics window:

| Field | Meaning |
|---|---|
| `Rendered` | a reconcile or layout happened this frame (false ⇒ a compositor-only frame) |
| `ComponentsRendered` | how many component render-effects ran (granularity metric) |
| `HotPhaseAllocBytes` | managed bytes allocated in the paint half — **must be 0** on steady frames |
| `DrawCommandCount` / `NodesVisited` / `DrawNodeCount` / `CulledNodeCount` | record-phase stats |
| `Fps` / `FrameMs` | smoothed timing |

## Run headless (tests / CI / agents)

No GPU or window required — swap in the headless backends. This is exactly how the golden-check harness runs:

```csharp
using FluentGpu.Foundation;      // StringTable
using FluentGpu.Hosting;         // AppHost
using FluentGpu.Pal.Headless;    // HeadlessPlatformApp, HeadlessWindow
using FluentGpu.Rhi.Headless;    // HeadlessGpuDevice
using FluentGpu.Text.Headless;   // HeadlessFontSystem

var strings = new StringTable();
using var app = new HeadlessPlatformApp();
var window = new HeadlessWindow(new WindowDesc("test", new Size2(1280, 800), 1f));
window.Show();
var device = new HeadlessGpuDevice();
var fonts  = new HeadlessFontSystem(strings);
using var host = new AppHost(app, window, device, fonts, strings, new App());

host.RunFrame();                                          // drive frames deterministically
window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0));   // synthesize input
host.RunFrame();
// assert on host.Scene (bounds/flags/paint) and device.LastGlyphs/LastRects (what was recorded)
```

The headless `HeadlessGpuDevice` records the DrawList into inspectable lists (`LastGlyphs`, `LastRects`,
`LastGradientStrokes`, …) so tests assert *what was drawn* without pixels. `host.Scene` exposes post-layout bounds.

## The verification harness

The single source of truth for "does the engine still work":

```bash
dotnet run --project src/FluentGpu.VerticalSlice
```

It runs ~60 end-to-end golden checks across every seam (layout, reconcile, signals, scroll, virtualization, images,
controls, navigation, animation) and prints `[PASS]`/`[FAIL]` + `ALL CHECKS PASSED`. Add a check by writing a
`Check("…", condition, detail)` in `src/FluentGpu.VerticalSlice/Program.cs` and calling it from `Main`.

> 🤖 **AGENT:** after any engine change, run the harness and confirm `ALL CHECKS PASSED` before claiming success. It's
> fast (seconds), deterministic, and catches cross-seam regressions that a focused test won't. GPU pixels are *not*
> asserted here — those are a separate, manual "needs-pixels" check on the real D3D12 path.

## Project layout (assemblies)

The engine ships as 4 libraries + 2 Roslyn analyzers + 2 executables (`src/FluentGpu.slnx`). The ones you touch most:

| Assembly | What's in it |
|---|---|
| `FluentGpu.Engine` | the portable engine core — Foundation (handles, allocators, `ColorF`/`Affine2D`, Signals), Seams (Rhi/Pal/Text interfaces), Scene, Render, Layout, Dsl, Hooks, Reconciler, Animation, Input, Media, Hosting, and the Headless backends; one folder per former project, namespaces verbatim |
| `FluentGpu.Controls` | Button/IconButton/ToggleButton/Slider/ScrollBar/NavigationView/Repeater/Virtual/Navigator — refs Engine only, TerraFX-free |
| `FluentGpu.Windows` | the swappable Windows backend (D3D12/, Pal/, DirectWrite/, Wic/, Uia/, Interop/) — holds the one TerraFX.Interop.Windows reference |
| `FluentGpu.WindowsApi` | OS-service scaffold (Notifications, Credentials, Packaging, Activation) |
| `FluentGpu.SourceGen` / `FluentGpu.Interop.SourceGen` | netstandard2.0 Roslyn analyzers (cannot merge) |
| `FluentGpu.VerticalSlice` | NativeAOT validation harness exe (refs Engine + Controls; TerraFX-free) |
| `FluentGpu.WindowsApp` | gallery / composition root (refs Engine + Controls + Windows) |

Next: **[reactivity.md](./reactivity.md)** — the signals model that everything else builds on.
