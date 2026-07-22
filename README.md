# fluent-gpu

**A from-scratch, near-zero-allocation, NativeAOT, GPU-rendered UI framework for .NET 10 — fluent C# markup, React-style hooks, a signals-first reactive core, and a control kit with one consistent contract.**

<p align="center">
  <a href="https://github.com/christosk92/fluent-gpu/actions/workflows/msix.yml"><img src="https://github.com/christosk92/fluent-gpu/actions/workflows/msix.yml/badge.svg" alt="MSIX build" /></a>
  <a href="https://github.com/christosk92/fluent-gpu/releases/latest"><img src="https://img.shields.io/github/v/release/christosk92/fluent-gpu?include_prereleases&label=release&color=512BD4" alt="Latest release" /></a>
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet" alt=".NET 10" />
  <img src="https://img.shields.io/badge/NativeAOT-~7MB%20MSIX-0078D4?logo=windows" alt="NativeAOT MSIX" />
  <a href="./LICENSE"><img src="https://img.shields.io/badge/License-MIT-green" alt="MIT License" /></a>
</p>

## Download

The FluentGpu Gallery ships as a signed **MSIX** from GitHub Releases — a single **~7 MB NativeAOT** package with **no .NET runtime to install**. The button installs it through Windows App Installer and keeps it current with background auto‑updates.

<p align="center">
  <a style="text-decoration:none" href="https://github.com/christosk92/fluent-gpu/releases/latest/download/FluentGpu.x64.appinstaller">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="./docs/media/download/download-x64-dark.png" />
      <img src="./docs/media/download/download-x64-light.png" height="56" alt="Download for Windows x64" /></picture></a>
  &nbsp;&nbsp;
  <a style="text-decoration:none" href="https://github.com/christosk92/fluent-gpu/releases/latest/download/FluentGpu.arm64.appinstaller">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="./docs/media/download/download-arm64-dark.png" />
      <img src="./docs/media/download/download-arm64-light.png" height="56" alt="Download for Windows ARM64" /></picture></a>
</p>

> Or build it yourself: `pwsh ops/build/pack-msix.ps1 -Install` (NativeAOT, self‑signed dev cert). Pipeline docs: [`ops/build/README.md`](./ops/build/README.md).

> **Status (July 2026): the engine is built, and the API surface just went through a full flagship overhaul.**
> One component model, live props, call-site-keyed hooks (conditionals and loops are legal), auto-tracked effects,
> one controlled-input contract and one creation idiom across the whole control kit, a source-generated router,
> a localized control kit, and a signals-first media pipeline — verified by **800+ end-to-end golden checks** on the
> headless backends (including the zero-alloc frame gates) plus three xUnit test projects. The driving app — a real
> Spotify desktop client — lives in-tree at [`src/apps/Wavee`](./src/apps/) and runs on every one of these APIs.
> Full design corpus in [`docs/design/`](./docs/design/README.md); developer & agent guide in
> [`docs/guide/`](./docs/guide/README.md).

https://github.com/user-attachments/assets/8e82a2cc-b908-40df-88f9-30438b44c02c

---

## What it is

fluent-gpu keeps the part of [WinUI](https://github.com/microsoft/microsoft-ui-xaml) developers actually want — Fluent visuals, a real control kit, Mica, DirectWrite text — and throws away the part that hurts. You write UI in pure C#: immutable `Element` records assembled with a fluent DSL, rendered by `Component.Render()`, with co-located hooks and a keyed diffing reconciler. **No XAML, no ViewModels, no code-behind.**

The difference is what's underneath — two things.

1. Instead of patching WinUI's heavy C++ XAML/Composition core, the reconciler patches **our own** retained struct-of-arrays render tree, which a custom batched 2D renderer paints on **Direct3D 12 + DirectWrite + DirectComposition** — pure Win32, **no WinRT, no Windows App SDK** — behind a swappable seam so a macOS (Metal + CoreText) backend can drop in later.
2. The reactivity is **signals-first** (Solid-style). A `setState` re-renders *only* the owning component's subtree. A high-frequency value (a slider drag, a scroll offset, a playhead) can be **bound straight to a node transform** so it updates with *zero* render, reconcile, or layout work. There is no full-app re-render and no global dirty flag.

```csharp
using FluentGpu.Dsl;               // Element
using static FluentGpu.Dsl.Ui;     // VStack, HStack, Heading, Text…
using FluentGpu.Hooks;             // Component, UseState
using FluentGpu.Controls;          // Button, Slider, ToggleSwitch…

sealed class Counter : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);                 // reading `count` subscribes THIS component
        return VStack(12,
            Heading($"Count: {count}"),
            HStack(8,
                Button.Standard("–", () => setCount(count - 1)),
                Button.Accent("+",  () => setCount(count + 1))));   // re-renders only Counter's subtree
    }
}

// run it:
FluentApp.Run(() => new Counter(), new AppOptions { Title = "Counter", Mica = true });
```

For a value that changes 90×/second, bind it instead of `setState`:

```csharp
var vol = UseFloatSignal(0.5f);
Slider.Create(vol);                // a drag updates the thumb/fill transform only — no render, no reconcile, no layout
```

## The API, quickly

The whole kit follows a handful of rules — learn them once, they hold everywhere.

**One creation idiom, one controlled-input contract.** Every control is `X.Create(...)`. Every stateful control takes its value as a concrete `Signal<T>` plus an optional `onChange`; the control writes the signal on interaction, then fires `onChange` once — programmatic writes never echo. Pass no signal and the control materializes its own ("uncontrolled" is just "the control made its own signal" — one code path):

```csharp
var isOn = UseSignal(false);
ToggleSwitch.Create(isOn, header: "Wi-Fi");           // controlled
TextBox.Create(query, onChange: q => Search(q));       // signal in, control writes it
Button.Create("Save", Save, ButtonAppearance.Accent, ControlSize.Small);  // orthogonal appearance × size axes
```

**Live props — no frozen components.** Component props re-push on parent re-render (`Embed.Comp(props, factory)` delivers them through the reconciler's reuse seam, equality-gated). Mark a component `[Props]` and a source generator emits the signal-backed storage, per-field delivery, and delegate forwarders — a fresh lambda from the parent never causes a re-render, yet handlers always invoke the newest delegate:

```csharp
[Props]
sealed partial class UserCard : Component
{
    [Prop] public partial string Name { get; }        // updates live; only this field's readers re-run
    [Prop] public partial Action? OnOpen { get; }     // latest-write forwarder — never re-renders
    public override Element Render() => /* … */;
}
// parent: Embed.Comp(UserCard.Of(name: user.Name, onOpen: Open), () => new UserCard());
```

**Hooks without the rules-of-hooks.** Hook state is keyed by call site, not call order — hooks in conditionals and loops are legal. Effects are auto-tracked by default (re-run when any signal they read changes) and can return their own cleanup; `DepKey` is the explicit opt-in for "run only when THIS changes":

```csharp
UseEffect(() => {                        // auto-tracked: re-runs when playing.Value changes
    var reg = smtc.Attach(playing.Value);
    return () => reg.Dispose();          // cleanup runs before re-run and at unmount
});

var results = UseResource(ct => Api.SearchAsync(term.Value, ct));   // stale-while-revalidate, race-proof
var debounced = UseDebouncedValue(term, 300);                        // + UseThrottled/UseTimeout/UseInterval
```

**Keyed reactive lists.** `Flow.For<T>` diffs by key at one boundary — no parent re-render — and `ItemsView` virtualizes 10k+ rows with zero-allocation recycling:

```csharp
Flow.For(tracks, t => t.Id, (t, i) => TrackRow(t, i))   // thunk or signal-direct source
```

**Batteries included.** A source-generated router (`[Route]` → compile-time route table, `Navigator` with back/deep-links), `Toast.Show(...)` with a queueing host, a controlled `Popup` primitive with flip/nudge/anchor-follow, `InteractionRecipe` for hover/press styling that rides the compositor, a full design-token set (`Tok.*`, spacing/radii/typography scales, on-media + contrast-checked on-accent colors), and a **localized control kit** (every kit string routes through generated loc keys with a JSON source of truth — ship your own locales or none; English works with zero config). A signals-first media pipeline (`IMediaPlayer` exposes position/volume/state as signals; `MediaPlayerElement` binds them compositor-only) plays audio and video with an allocation-free realtime audio path.

## Why

WinUI 3 is slow in ways that are *structural*, not tunable: every `DependencyProperty` boxes through an `object`-typed store, every control is a finalizable COM object, the visual+logical trees are doubled, layout/composition run on one thread, and `setState` fans out to a broad re-render. The result is GC stutter under scroll, a UI thread that blocks easily, and a heavy footprint.

fluent-gpu attacks the *causes*: unmanaged SoA columns instead of dependency properties; generational handles + arenas + slabs instead of GC objects on the hot path; hand-vtable `calli` instead of COM RCW churn; a single render tree; fine-grained signals so updates are surgical, not tree-wide; and a GPU-batched paint path that enforces **zero per-frame managed allocation** across the hot frame phases — as a tripwire gate in CI, not an aspiration. The honest grades (see [the painpoints assessment](./docs/design/winui-painpoints-assessment.md)): **GC pressure — largely solved; over-rendering — solved (granular re-render + a compositor bypass for hot values); slow UI thread — decoupled (not invincible); footprint + startup — substantially better.** It is *not* a risk-free engine — it trades GC-correctness for hand-rolled COM and renderer correctness, made safe-by-construction where it can and CI-gated everywhere else.

The proof workload is [**Wavee**](./src/apps/), a Spotify desktop client that lives in this repo — media-heavy, list-heavy (10k+ track lists), theming-heavy (album-art dynamic color, Mica), with video and synced lyrics. It builds on exactly the public API you get, with no private escape hatches.

## How it works (one diagram)

```
 Components + Hooks + fluent DSL  (Element records, pure C#)
        │  state lives in SIGNALS — a read subscribes; a write re-runs only what read it
        │  keyed reconcile → ISceneBackend (handle-in/handle-out, POD)   (granular: one component's subtree)
 Scene (SoA RenderNode tree) · Layout (ported Yoga, scoped relayout) · Input/A11y · Animation slab
        │  DrawList (POD command stream)
 Renderer (batched quads, SDF, glyph atlas)  →  RHI / PAL / Text seams
        │
 Windows: D3D12 · DXGI flip + DComp · DirectWrite · WIC · WASAPI · UIA   (macOS: Metal · CoreText · Cocoa, later)
```

Three update paths, cheapest first: a **binding** (signal → node transform/paint, compositor-only); a **granular re-render** (one component's subtree + a scoped relayout firewalled at a layout boundary); **reactive control-flow** (`For`/`Show`, a keyed diff of one boundary, no parent re-render). See [`docs/guide/reactivity.md`](./docs/guide/reactivity.md).

Read the [architecture spec](./docs/design/architecture-spec.md) for the full picture, the [subsystem index](./docs/design/subsystems/README.md) for the component designs, or [`reconciler-hooks.md`](./docs/design/subsystems/reconciler-hooks.md) for the as-built signals model.

## Use it in your app (NuGet)

One package brings the whole SDK — the engine, the control kit, the Windows (D3D12) backend with `FluentApp.Run`, the OS-services surface (notifications, credentials, SMTC, dialogs, power, network…), and the source generators (`[Props]`, `[Route]`, loc keys, validation) as analyzers:

```xml
<PackageReference Include="FluentGpu" Version="0.1.0" />
```

```csharp
using FluentGpu;                 // FluentApp, AppOptions
using FluentGpu.Hooks;           // Component
using static FluentGpu.Dsl.Ui;   // VStack, Text, Button…

FluentApp.Run<App>(new AppOptions { Title = "My app", CustomFrame = true });
```

Needs the **.NET 10 SDK** and **Windows 10 21H2+** (x64/arm64). Publish a single self-contained native exe with `dotnet publish -c Release -r win-x64 -p:PublishAot=true`. Full walkthrough: [`docs/guide/consuming-via-nuget.md`](./docs/guide/consuming-via-nuget.md).

## Running it

```powershell
# The verification gate — 800+ cross-seam golden checks on the headless backends (no GPU/window needed),
# including the zero-managed-allocation tripwires on the hot frame phases:
dotnet run --project src/FluentGpu.VerticalSlice        # expect: "ALL CHECKS PASSED"

# The gallery — 100+ pages, every control + pattern, live perf readouts:
dotnet run --project src/FluentGpu.WindowsApp

# Render a deterministic scene to a PNG (the visual-diff loop), or audit every gallery page headlessly:
dotnet run --project src/FluentGpu.WindowsApp -- --screenshot shot.png
dotnet run --project src/FluentGpu.WindowsApp -- --gallery-audit
```

The solution (`src/FluentGpu.slnx`) is **five libraries + tooling + apps + tests**:

| Project | What it is |
|---|---|
| `FluentGpu.Engine` | The portable core — signals, reconciler, hooks, layout, scene, renderer, animation, input, media. TerraFX-free. |
| `FluentGpu.Controls` | The portable control kit (localized, one contract). TerraFX-free — the macOS port consumes it unchanged. |
| `FluentGpu.Windows` | The swappable Windows backend: D3D12, DirectWrite, DComp, WIC, UIA, `FluentApp.Run`. |
| `FluentGpu.WindowsApi` | OS services: notifications, credentials, SMTC, dialogs, shell, power, network, storage. AOT-clean interop, no CsWinRT. |
| `FluentGpu.GalleryKit` | Showcase scaffolding: `[GalleryPage]`/`[Sample]` attributes, example cards, knobs, perf badges. |
| `FluentGpu.SourceGen` | The analyzer/generator assembly: `[Props]`, `[Route]`, loc keys, token accessors, glyph tables, sample extraction, FGRP lints. |
| `FluentGpu.VerticalSlice` | The headless acceptance harness (the 800+ checks + alloc gates). Its transitive closure must stay TerraFX-free. |
| `FluentGpu.WindowsApp` | The gallery — composition root, screenshot/audit harness. |
| `src/apps/Wavee*` | The driving app: a full Spotify desktop client on the public API. |
| `*.Tests` ×3 | xUnit projects: engine (incl. media playback), Windows backend, source generators/analyzers. |

`FluentGpu.Package` assembles all of it into the single `FluentGpu` NuGet package. Details: [`src/README.md`](./src/README.md).

## The gallery is the documentation

Every gallery page is registered with a `[GalleryPage]` attribute and routed through the source-generated registry — the nav tree, search index, and deep links are all derived, never hand-synced. Every code sample on every page is a `[Sample]` method whose **displayed code is extracted from the compiled method body at build time** — the sample you read is the sample that runs, by construction (166 of them and counting). Interactive knob panels drive control props through live signals, so you watch props update without remounts — with per-card perf badges showing the engine skipping render/reconcile/layout as you scrub.

## Documentation & agents

- **[`docs/guide/`](./docs/guide/README.md)** — the developer **and** agent guide: getting started, the signals model, hooks, controls, layout, rendering & performance, motion recipes, [localizing the control kit](./docs/guide/localizing-the-control-kit.md), and a **symptom → cause → fix** [pitfalls page](./docs/guide/pitfalls.md).
- **[`docs/design/`](./docs/design/README.md)** — the architecture corpus: the canon authority for every cross-cutting contract, with a strict single-owner model and a drift gate (`docs/design/check-canon.ps1`).
- **[`AGENTS.md`](./AGENTS.md)** — guidance for AI agents: build/test commands, the rules, the where-to-change-what file map, and the verification gate. Claude Code users also get the `.claude/skills/fluentgpu` skill and the discipline in [`CLAUDE.md`](./CLAUDE.md).

## Where it stands

Built in an order where **safety is never speculative** (full detail in the [hardened-v1 plan](./docs/design/hardened-v1-plan.md)):

1. **Vertical slice** — ✅ window → GPU clear → rounded rect → text → flex → reconciler + `UseState` → clickable button, zero per-frame alloc on the paint half. Standing acceptance test, run on every change.
2. **Core engine** — ✅ renderer, flex + grid layout, text, the signals-first reconciler/hooks runtime, DSL + modifiers, input, the animation slab (analytical springs, dt-deterministic).
3. **App subsystems** — ✅ async image pipeline, audio/video playback, virtualization, theming + dynamic accent + Mica, the control kit, navigation, localization — everything Wavee needs, exercised by headless acceptance checks.
4. **Flagship API overhaul** — ✅ one component model, live props, call-site-keyed hooks, auto-tracked effects, the controlled-input contract, one creation idiom, router, kit localization, six source generators, eight enforcement analyzers. Clean break, no legacy shims left in the tree.
5. **Hardening** — 🚧 the alloc tripwire, golden checks, and COM discipline are in force; the parallel render-thread seam ships behind a green race gate.
6. **Cross-platform** — ⏳ the macOS (Metal/CoreText/Cocoa) backend behind the existing seam.

## Built on

- **[ComputeSharp](https://github.com/Sergio0694/ComputeSharp)** — vendored DX12/DXGI COM bindings, `ComPtr<T>`, and the NativeAOT interop patterns.
- **[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)** — the original programming model (Element/Component/hooks) and the pure-C# Yoga flexbox port.
- **[microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml)** — studied for the rendering/text/layout architecture, control fidelity (and what to avoid).
- **[SolidJS](https://www.solidjs.com/)** — the signals / fine-grained reactivity model the reactive core is built on.

## License

MIT.
