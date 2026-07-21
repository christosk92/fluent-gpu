# FluentGpu

A from-scratch, near-zero-allocation, **NativeAOT**, **GPU-rendered** UI engine for **.NET 10** — a React/Reactor
programming model (immutable `Element` records + `Component` + hooks + a keyed reconciler) over a **signals-first**
reactive core and a custom **Direct3D 12 + DirectWrite + DirectComposition** renderer.

```xml
<PackageReference Include="FluentGpu" Version="0.1.0" />
```

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

`FluentApp.Run` is the whole SDK in one call: it creates a DPI-aware window, brings up D3D12, applies Mica + the real
system accent, wires the font system + frame loop, and renders your root component.

## What's in the package

One package, all the assemblies inside it:

- **Engine** — signals, hooks, the DSL, the keyed reconciler, layout, the retained scene, the render pipeline.
- **Controls** — Button, Slider, ScrollView, NavigationView, virtualization, and the rest of the kit.
- **Windows backend** — the D3D12 / Win32 / DirectWrite / WIC implementation behind the engine's PAL/RHI/Text seam,
  plus `FluentApp.Run`.
- **Windows OS services** — toasts, credential vault, SMTC, file pickers, taskbar, activation, and more.
- **Source generators** (opt-in) — compile-safe localization keys and `[Validatable]` form validators.

## Requirements

- **.NET 10 SDK** (the package targets `net10.0`).
- **Windows 10 21H2 or later**, x64 or arm64 (Direct3D 12 / WDDM 2.0 + the OS HLSL compiler).
- For a self-contained native build: `dotnet publish -c Release -r win-x64 -p:PublishAot=true`.

## Links

- Guide: https://github.com/christosk92/fluent-gpu/tree/main/docs/guide
- Source & issues: https://github.com/christosk92/fluent-gpu

Licensed under the MIT License.
