# fluent-gpu / src

The engine code. Design lives in [`../design`](../design); this is the scaffold + the minimum vertical slice that is the architecture's acceptance test.

## Solution layout (`FluentGpu.slnx`, solution folders)

- **Core** — `Foundation` · `Scene` · `Layout` · `Render` · `Dsl` · `Hooks` · `Reconciler` · `Input` · `Animation` · `Hosting`
- **Seams** (interface-only) — `Rhi` · `Pal` · `Text`
- **Backends.Windows** (the reference D3D12 path — scaffold) — `Win32.Interop` · `Rhi.D3D12` · `Pal.Windows` · `Text.DirectWrite` · `Accessibility.Uia`
- **Backends.Headless** (the CPU test path — real) — `Rhi.Headless` · `Pal.Headless` · `Text.Headless`
- **Tooling** (Roslyn analyzers/generators) — `SourceGen` (one netstandard2.0 assembly: the `Localization/` loc-keys + `Validation/` `[Validatable]` generators, plus the engine-DSL/Theme + COM-binding scaffolds)
- **Apps** — `VerticalSlice`

### Solution filters (`.slnf`)
- **`FluentGpu.Slice.slnf`** — the headless slice closure (no Windows leaves, no analyzers). Builds + runs without a GPU.
- **`FluentGpu.Windows.slnf`** — the Windows-backend closure (for implementing the D3D12/DWrite/UIA path).

## Build & run

```powershell
dotnet build FluentGpu.slnx                 # whole solution
dotnet build FluentGpu.Slice.slnf           # just the runnable headless slice
dotnet run  --project FluentGpu.VerticalSlice
```

The slice runs the full 13-phase frame loop on the headless RHI/PAL/Text backends and asserts, end to end:
**window → GPU clear → rounded-rect → text → flex layout → reconciler + `UseState` → clickable Button** (Count 0→1 on click),
plus **zero managed allocation on the paint half (phases 6–11)** — the architecture's acceptance test.

> Status: the **portable core is real**; the slice runs on the **headless** backends. The Windows D3D12/DWrite/UIA leaves
> and the source generators are scaffolded (see each project's `Placeholder.cs`) — the next code milestone is the real
> D3D12 backend so the same `Counter` renders to an actual window. Baseline: `net10.0`, C# 14, AOT-compatible (`Directory.Build.props`).
