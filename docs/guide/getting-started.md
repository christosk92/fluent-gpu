# Getting started

[← Guide index](./README.md)

## Run an app (Windows, batteries-included)

The entire SDK is one call — it creates a DPI-aware window, brings up D3D12, applies Mica + the real system accent,
wires the font system + frame loop, and renders your root component:

```csharp
using FluentGpu;                 // FluentApp
using FluentGpu.Hooks;           // Component
using static FluentGpu.Dsl.Ui;   // VStack, Text, Button…

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

`FluentApp.Run` signature (`src/FluentGpu.WindowsApp/FluentApp.cs`):

```csharp
public static void Run(Func<Component> root, string title = "FluentGpu",
                       int width = 800, int height = 600, bool mica = true, int frames = -1);
public static void Run<T>(string title = "FluentGpu", int width = 800, int height = 600) where T : Component, new();
```

That's all you need to build apps. The rest of this page is for when you want to drive the loop yourself, or run
headless (tests/CI).

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
