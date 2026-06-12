# API reference

The pages under `api/` are **generated**, not hand-written. `docfx metadata` reads the engine projects and their XML
doc comments and emits the YAML this section is built from. The conceptual guides — [App authors](../app-authors/index.md)
and [Engine contributors](../engine-contributors/index.md) — explain *how* to use the surface; this reference is the
exhaustive *what*. If you arrived here looking for "how do I…", the conceptual docs are faster.

```text
docfx metadata docs/site/docfx.json     # regenerate api/*.yml before building the site
```

The set of projects this reference covers, and the `net10.0` target it builds against, is pinned in
[`docs/site/docfx.json`](../docfx.json) (the `metadata.src.files` list). Adding a project to that list is what makes it
appear here.

## How the API is grouped (Public / Contributor / Internal)

The solution ([`src/FluentGpu.slnx`](../../../src/FluentGpu.slnx)) is **8 projects** (4 libraries + 2 analyzers + 2 exes). They split into three concentric
rings, and the ring tells you what you are *expected* to touch:

- **Public** — what app authors call. You build a whole app out of this ring and rarely look further.
- **Contributor** — the engine internals you extend when you change behavior. One owner per artifact (see
  [the contract-ownership map](../../../design/subsystems/README.md)); edit the owner, reference it elsewhere.
- **Internal** — backend plumbing *behind* the seams (the real Windows leaves). You read it to understand a backend;
  you rarely call it directly.

The split is a *reading* aid, not an access-modifier boundary — the assemblies form a strict **acyclic** graph
(canonically owned by [`design/foundations.md`](../../../design/foundations.md)), and an assembly may reference only
those below it. The full where-to-change-what routing table lives in the
[contributor map](../engine-contributors/contributor-map.md) and the guide's
[file & ownership map](../../guide/README.md#-agent--file--ownership-map-where-to-change-what); the precedence authority
for every cross-cutting contract is [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md).

## Public API — what app authors call

This is the ring the [60-second cheat sheet](../app-authors/index.md#60-second-cheat-sheet) imports. The four
namespaces a typical app `using`s are `FluentGpu` (the entry point), `FluentGpu.Hooks`, `FluentGpu.Signals`, and
`static FluentGpu.Dsl.Ui` — plus `FluentGpu.Controls` for the control set.

| Assembly | Headline namespace(s) | What it gives you |
| --- | --- | --- |
| `FluentGpu.Engine` (the `Dsl/` folder) | `FluentGpu.Dsl` | The immutable element records (`Element`, `BoxEl`/`TextEl`/`ImageEl`/`ScrollEl`) and the `Ui.*` builders (`VStack`/`HStack`/`Text`/`Heading`/`Icon`/`ScrollView`/`Image`/`Grid`), the fluent `Modifiers`, and the theme tokens (`Tok`, `Theme`, `Radii`). |
| `FluentGpu.Engine` (the `Hooks/` folder) | `FluentGpu.Hooks` | `Component` and `ReactiveComponent`, the state/effect/motion hooks surfaced as `protected` methods (`UseState`, `UseSignal`, `UseFloatSignal`, `UseComputed`, `UseEffect`, `UseSpring`, …), `Flow.For`/`Flow.Show`, `Ctx`, and `Embed`. |
| `FluentGpu.Controls` | `FluentGpu.Controls` | The Fluent control kit — `Button`, `Slider`, `CheckBox`, `ToggleSwitch`, `ComboBox`, `TextBox`, `NavigationView`, `TabView`, `Virtual`, `ItemsView`, and ~70 more. Controls are **pure composition** over the rings below — they add no opcode, column, PAL seam, or hook. |
| `FluentGpu.Engine` (the `Hosting/` folder) | `FluentGpu.Hosting` | `AppHost` — the single-UI-thread frame loop you normally drive through `FluentApp.Run` — and the per-frame `FrameStats` (`Rendered`, `ComponentsRendered`, `HotPhaseAllocBytes`, …) you assert on in tests. |
| `FluentGpu.Engine` (the `Foundation/` folder) | `FluentGpu.Signals`, `FluentGpu.Foundation` | The signals reactive core (`Signal<T>`, `FloatSignal`, `Memo<T>`, `Effect`) and the unified bindable channel `Prop<T>` / `Prop.Of` — plus the shared value types (`ColorF`, `Affine2D`, geometry, `Edges4`). |
| `FluentGpu.Engine` (the `Media/` folder) | `FluentGpu.Media` | The async image pipeline surface — `IImageFetcher`, `IImageCodec`, the `DecodeScheduler`/disk-cache machinery behind `Ui.Image`. |

> **Namespace gotcha (verified in source):** `Signal<T>` and `Prop<T>` live *physically* under the
> `FluentGpu.Engine` assembly (`src/FluentGpu.Engine/Foundation/Signals/`), but they are declared in the namespace
> **`FluentGpu.Signals`** — so you author them with `using FluentGpu.Signals;`. Likewise the batteries-included
> `FluentApp.Run` is in the **`FluentGpu`** namespace but ships from the `FluentGpu.WindowsApp` app project (see the
> [Excluded](#excluded-from-api-metadata-the-apps-and-the-verification-harness) note below), so it does not appear in
> this generated reference.

The one entry point, for orientation (`src/FluentGpu.WindowsApp/FluentApp.cs`):

```csharp
namespace FluentGpu;

public static class FluentApp
{
    public static void Run(Func<Component> root, string title = "FluentGpu", int width = 800, int height = 600,
                           bool mica = true, int frames = -1, string? screenshot = null, bool customFrame = false);
}
```

## Contributor API — engine internals you extend

The ring you edit when you change *how the engine behaves*. Each assembly has a headline type that is one stage of the
single mechanism the engine is built on (a signal write → re-run the computations that read it → reconcile → layout →
record). The full reasoning is in [Contributing to the engine](../engine-contributors/index.md).

| Assembly | Headline type | What it owns |
| --- | --- | --- |
| `FluentGpu.Engine` (the `Foundation/` folder) | `ReactiveCore` (`Reactive`/`Computation`/`ReactiveRuntime`) | The Solid/Preact-style reactivity graph — set→notify→flush. The notify path must stay allocation-free. |
| `FluentGpu.Engine` (the `Scene/` folder) | `SceneStore` (`: ISceneBackend`) | The retained struct-of-arrays RenderNode tree, its parallel columns, the 3-axis dirty flags, `ImageCache`, and the `VirtualLayout` participant. |
| `FluentGpu.Engine` (the `Layout/` folder) | `FlexLayout`, `LayoutInvalidator` | Flexbox + grid measure/arrange over the SoA columns; `Run` (full tree) vs `RunSubtree` (the scoped relayout) and the boundary walk that stops it. |
| `FluentGpu.Engine` (the `Render/` folder) | `SceneRecorder` | Phase 8 (record): walk the retained scene and emit the POD `DrawList` — local-space geometry + world transform, so transform/opacity animate without relayout or re-record. |
| `FluentGpu.Engine` (the `Reconciler/` folder) | `TreeReconciler` | The heart — render-effects, the keyed positional+type diff, `Prop<T>` binding wiring, `Flow.For`/`Flow.Show` boundaries, context, `VirtualListEl`. `ConsumeReconciled()`/`ConsumeRenderCount()` prove granularity. |
| `FluentGpu.Engine` (the `Seams/Rhi/` folder) | `IGpuDevice` (+ `ISwapchain`, `ICommandEncoder`) | The GPU seam every render backend implements — POD descs, `SubmitDrawList`, `DeviceLostToken`. |
| `FluentGpu.Engine` (the `Seams/Pal/` folder) | `IPlatformApp`, `IPlatformWindow` | The platform seam — windowing (`WindowDesc`, `NativeHandle`), the input/window event stream, and the system-service seams. |
| `FluentGpu.Engine` (the `Seams/Text/` folder) | `IFontSystem` | The text seam — itemize/shape/raster/measure. The Yoga measure bridge feeds the same advances the GPU glyph path renders. |
| `FluentGpu.Engine` (the `Input/` folder) | `InputDispatcher` | Input dispatch + routing over the PAL event stream (hit-test, focus, gestures). |
| `FluentGpu.Engine` (the `Animation/` folder) | `AnimEngine` | The authored timeline/keyframe/easing machinery the motion hooks and control motion drive. |

> **Authority, not duplication.** These types *implement* contracts that are specified once in the design corpus. Do
> not restate a struct shape, an enum, or a seam in code or docs — reference the owner. The DrawList opcode shapes are
> owned by [`gpu-renderer.md`](../../../design/subsystems/gpu-renderer.md), the scene columns by
> [`scene-memory.md`](../../../design/subsystems/scene-memory.md), the reconciler/hooks/signals model by
> [`reconciler-hooks.md` §0bis](../../../design/subsystems/reconciler-hooks.md), and the threading model by
> [`threading-render-seam.md`](../../../design/subsystems/threading-render-seam.md). The map from each artifact to its
> one owner is [`subsystems/README.md` §2](../../../design/subsystems/README.md).

## Internal API — backends, source generators, accessibility, codecs

The leaves *behind* the seams. They are included in this reference because the real Windows backends are useful to read,
but app authors never call them and contributors touch them only when changing a backend.

| Assembly / folder | What it is |
| --- | --- |
| `FluentGpu.Windows` (the `D3D12/` folder) | The Direct3D 12 RHI leaf — flip-model swap chain, the DirectComposition present tree, the rounded-rect/gradient/glyph pipelines. |
| `FluentGpu.Windows` (the `DirectWrite/` folder) | The DirectWrite text backend — the same design advances and line-break math the GPU glyph path uses, so measured wrap matches rendered wrap. |
| `FluentGpu.Windows` (the `Pal/` folder) | Win32 windowing + theme — the custom frame, Mica/Acrylic material, accent pickup (`Win32App`, `Win32Window`, `Win32Theme`). |
| `FluentGpu.Windows` (the `Interop/` folder) | The shared Win32/COM interop layer the Windows backends bind through. |
| `FluentGpu.Windows` (the `Wic/` folder) | The WIC image codec (`IImageCodec` leaf) the real image pipeline decodes with. |
| `FluentGpu.Windows` (the `Uia/` folder) | The UI Automation accessibility integration (the `IA11yBackend` Windows leaf). |

**Source generators and analyzers are intentionally absent from this reference.** The two tooling projects —
`FluentGpu.SourceGen` and `FluentGpu.Interop.SourceGen` — run at *build* time (they emit `ApplyToScene`, `DiffProps`,
the hook-deps lowering, the COM vtables, and the `FGCOM*`/`WGPU*` analyzers) and expose no useful runtime surface, so
they are not in `docfx.json`'s metadata list. Their contracts live in
[`dsl-aot.md`](../../../design/subsystems/dsl-aot.md) and [`com-interop.md`](../../../design/subsystems/com-interop.md).

## Excluded from API metadata: the apps and the verification harness

A few projects in the solution are deliberately **not** generated into this reference, because they are applications or
test-only seams, not library surface:

- **`FluentGpu.VerticalSlice`** — the headless golden-check harness (`dotnet run` here prints `ALL CHECKS PASSED`). It
  is an *app*, not a library, but its source is the most-cited code in the conceptual docs: every engine claim is
  anchored to a `Check(...)` in `src/FluentGpu.VerticalSlice/Program.cs`. See
  [Engine contributors → verify with the harness](../engine-contributors/index.md#the-golden-rule-verify-with-the-headless-harness-before-claiming-done).
- **`FluentGpu.WindowsApp`** — the gallery app (and the home of `FluentApp.Run`). It is where every control's verified,
  copy-pasteable snippet lives, but it is an application, so it carries no API metadata.
- **The headless seams** — the `FluentGpu.Engine/Headless/Rhi/`, `Headless/Pal/`, and `Headless/Text/` folders inside
  `FluentGpu.Engine`. These are test/CI backends behind the same seams the real leaves implement; they exist so the
  harness can record the DrawList and assert *what was drawn* without a GPU or window. No public surface worth
  publishing.

Because `FluentApp.Run` ships from the excluded gallery app, the entry point you call first is **documented in the
guides, not generated here** — the canonical signature and behavior are in
[getting-started](../app-authors/index.md#60-second-cheat-sheet) and at the top of this page.

## Generated from XML doc comments (`GenerateDocumentationFile`)

Everything in `api/` comes from the engine's own `///` comments. The solution-wide baseline
([`src/Directory.Build.props`](../../../src/Directory.Build.props)) turns this on for *every* project and silences the
"missing doc comment" warning, so building the solution produces the XML files `docfx metadata` consumes:

```xml
<!-- src/Directory.Build.props -->
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<NoWarn>$(NoWarn);CS1591</NoWarn>
```

What this means in practice:

- **Improving the reference = improving the `///` comments in `src/`.** There is no separate prose to maintain for the
  generated pages; edit the doc comment on the type or member and regenerate. Many of the most useful summaries already
  live there — e.g. `Prop<T>`'s comment in `src/FluentGpu.Engine/Foundation/Signals/Prop.cs` documents the static-vs-thunk-vs-
  signal-direct discrimination, and `Component`'s hook methods in `src/FluentGpu.Engine/Hooks/Component.cs` document each
  hook's subscribe semantics.
- **`CS1591` is suppressed, not satisfied.** A member with no doc comment still generates an (empty-summary) entry; it
  is not a build error. Treat an empty summary in the rendered reference as a doc gap to fill at the source, not a
  reason the page is broken.
- **Regenerate after touching public surface.** `docfx metadata docs/site/docfx.json` re-reads the projects; the site
  build (`docfx build` / the full `docfx docs/site/docfx.json`) then renders `api/*.yml` alongside this `index.md`.

## Where to go from here

- **Build a UI →** [App authors](../app-authors/index.md) · the [signals model](../../guide/reactivity.md) (the most
  important read) · [components, elements & layout](../../guide/components-elements-layout.md) ·
  [pitfalls](../../guide/pitfalls.md).
- **Change the engine →** [Engine contributors](../engine-contributors/index.md) · the
  [contributor map](../engine-contributors/contributor-map.md) (where to change what) ·
  [reconciler & scene](../engine-contributors/reconciler-and-scene.md).
- **Architecture canon →** [`design/SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) (precedence authority) ·
  [`subsystems/README.md`](../../../design/subsystems/README.md) (contract-ownership map) ·
  [`architecture-spec.md`](../../../design/architecture-spec.md) (the end-to-end engine).
