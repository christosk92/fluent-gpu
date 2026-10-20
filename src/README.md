# fluent-gpu / src

The engine code. Design lives in [`../design`](../design); this is the scaffold.

## Solution layout (`FluentGpu.slnx`, solution folders)

- **Core** — `Foundation` · `Scene` · `Layout` · `Render` · `Dsl` · `Hooks` · `Reconciler` · `Input` · `Animation` · `Hosting`
- **Seams** (interface-only) — `Rhi` · `Pal` · `Text`
- **Backends.Windows** (the reference D3D12 path — scaffold) — `Win32.Interop` · `Rhi.D3D12` · `Pal.Windows` · `Text.DirectWrite` · `Accessibility.Uia`
- **Backends.Headless** (the CPU path — real) — `Rhi.Headless` · `Pal.Headless` · `Text.Headless`
- **Tooling** (Roslyn analyzers/generators) — `SourceGen` (one netstandard2.0 assembly: the `Localization/` loc-keys + `Validation/` `[Validatable]` generators, plus the engine-DSL/Theme + COM-binding scaffolds)
- **Apps** — `WindowsApp` (the gallery)

### Solution filters (`.slnf`)
- **`FluentGpu.Windows.slnf`** — the Windows-backend closure (for implementing the D3D12/DWrite/UIA path).

## Build & run

```powershell
dotnet build FluentGpu.slnx                 # whole solution
dotnet build FluentGpu.slnx -c Release
```

> Status: the **portable core is real**. The Windows D3D12/DWrite/UIA leaves
> and the source generators are scaffolded (see each project's `Placeholder.cs`) — the next code milestone is the real
> D3D12 backend so the same `Counter` renders to an actual window. Baseline: `net10.0`, C# 14, AOT-compatible (`Directory.Build.props`).
