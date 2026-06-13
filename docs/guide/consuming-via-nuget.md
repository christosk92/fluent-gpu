# Consuming FluentGpu via NuGet

[← Guide index](./README.md)

FluentGpu ships as **one package**. Add it, write a component, call `FluentApp.Run` — that's a rendering app.

```xml
<PackageReference Include="FluentGpu" Version="0.1.0" />
```

## A complete app

`MyApp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>   <!-- WinExe = no console window; use Exe if you want one -->
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FluentGpu" Version="0.1.0" />
  </ItemGroup>
</Project>
```

`Program.cs`:

```csharp
using FluentGpu;                 // FluentApp
using FluentGpu.Dsl;             // Element
using FluentGpu.Hooks;           // Component
using FluentGpu.Controls;        // Button
using static FluentGpu.Dsl.Ui;   // VStack, Heading, Text…

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

```powershell
dotnet run
```

`FluentApp.Run` is the whole SDK in one call: a DPI-aware window, D3D12 + DirectComposition, Mica + the real system
accent, the font + image systems, and the frame loop.

## What's in the one package

| Inside | Namespaces you use |
|---|---|
| **Engine** — signals, hooks, DSL, reconciler, layout, scene, render | `FluentGpu`, `FluentGpu.Dsl`, `FluentGpu.Hooks`, `FluentGpu.Signals`, `FluentGpu.Foundation`, `FluentGpu.Animation`, `FluentGpu.Media` |
| **Controls** — Button, Slider, ScrollView, NavigationView, virtualization, … | `FluentGpu.Controls` |
| **Windows backend** — D3D12 / Win32 / DirectWrite / WIC + `FluentApp.Run` | `FluentGpu` (`FluentApp`) |
| **Windows OS services** — toasts, credentials, SMTC, pickers, taskbar, activation | `FluentGpu.WindowsApi.*` |
| **Source generators** (opt-in) — localization keys, `[Validatable]` validators | (build-time) |

`TerraFX.Interop.Windows` is pulled in automatically as the package's single dependency.

## Requirements

- **.NET 10 SDK.** The package targets `net10.0`; consumers need the .NET 10 toolchain.
- **Windows 10 21H2 or later**, **x64** or **arm64** — Direct3D 12 (WDDM 2.0) and the OS HLSL compiler (`d3dcompiler.dll`)
  must be present. There is no Windows 7/8.1 path.
- Nothing else to deploy: the renderer compiles its shaders at runtime via the OS, so there are no native binaries,
  `.cso` blobs, or shader files to ship alongside your app.

## Publishing a self-contained native app (NativeAOT)

The whole engine is AOT-clean (no reflection, no `ResourceManager`), so a consuming app publishes to a single native
executable with no warnings:

```powershell
dotnet publish -c Release -r win-x64 -p:PublishAot=true       # or -r win-arm64
```

This produces a self-contained `.exe` (~12 MB) with no .NET runtime to install. AOT compiles only at **publish** —
`dotnet build` / `dotnet run` stay fast JIT for iteration.

## Optional: the source generators

The package bundles `FluentGpu.SourceGen` as an analyzer; its generators stay dormant until you opt in.

**Compile-safe localization keys** — feed your base-culture JSON as an `AdditionalFiles` item and the generator emits a
typed `Strings` class:

```xml
<ItemGroup>
  <AdditionalFiles Include="assets\loc\en-US.json" FluentGpuLocBase="true" />
</ItemGroup>
<ItemGroup>
  <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="FluentGpuLocBase" />
</ItemGroup>
```

**Form validators** — annotate a `partial` type with `[FluentGpu.Forms.Validatable]` and the generator emits its
`Validator<T>[]`. Both are no-ops if you don't use them.

## Runtime assets you provide

The engine ships no fonts, icons, or localization files — those are app content:

- **Fonts** are resolved from the OS (DirectWrite); ship a custom `.ttf` only if you load one explicitly.
- **Localization** JSON is loaded from disk at runtime — `Localization.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "loc"))`. Mark your JSON `CopyToOutputDirectory="PreserveNewest"`.
- **Your window/app icon** is your project's `<ApplicationIcon>`.

## Next

- [Getting started](./getting-started.md) — the frame loop, headless runs, the verification harness.
- [Reactivity](./reactivity.md) — the signals model everything builds on.
