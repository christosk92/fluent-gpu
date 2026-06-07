# fluent-gpu

**A from-scratch, near-zero-allocation, NativeAOT, GPU-rendered UI engine for .NET 10 — Reactor-style fluent C# markup, React-style hooks, on a signals-first reactive core.**

> **Status (June 2026): the engine is built and runs.** A fine-grained **signals-first** reactive core, keyed
> reconciler, flex/grid layout, the control kit, virtualization (10k+ lists), the async image pipeline, theming +
> Mica, and animation all pass **60+ end-to-end golden checks** on the headless backends — including a full
> Wavee-shell acceptance test. The real Windows path (Direct3D 12 + DirectComposition + Mica + WIC images) is wired
> via `FluentApp.Run`; on-screen pixel polish is the ongoing edge. Full design corpus in [`design/`](./design/README.md);
> developer & agent guide in [`docs/guide/`](./docs/guide/README.md).

<img width="1064" height="687" alt="image" src="https://github.com/user-attachments/assets/097d8df5-56db-4b2e-80c4-0dbb9e1b919c" />

---

## What it is

fluent-gpu keeps the part of [WinUI](https://github.com/microsoft/microsoft-ui-xaml) developers actually want — and throws away the part that hurts. You write UI like [Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor): immutable `Element` records assembled with a fluent C# DSL, rendered by `Component.Render()`, with co-located hooks and a keyed diffing reconciler. **No XAML, no ViewModels.**

The difference is what's underneath — two things. **(1)** Instead of patching WinUI's heavy C++ XAML/Composition core, the reconciler patches **our own** retained struct-of-arrays render tree, which a custom batched 2D renderer paints on **Direct3D 12 + DirectWrite + DirectComposition** — pure Win32, **no WinRT, no Windows App SDK** — behind a swappable seam so a macOS (Metal + CoreText) backend can drop in later. **(2)** The reactivity is **signals-first** (Solid-style): a `setState` re-renders *only* the owning component's subtree, and a high-frequency value (a slider drag, scroll) can be **bound straight to a node transform** so it updates with *zero* render/reconcile/layout. There is no full-app re-render and no global dirty flag.

```csharp
using static FluentGpu.Dsl.Ui;     // VStack, HStack, Heading, Text…
using FluentGpu.Hooks;             // Component, UseState
using FluentGpu.Controls;          // Button, Slider…

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

// run it:  FluentApp.Run(() => new Counter());
```

For the slider tank — a value that changes 90×/second — bind it instead of `setState`:

```csharp
var vol = UseFloatSignal(0.5f);
Slider.Bind(vol);                  // a drag updates the thumb/fill transform only — no render, no reconcile, no layout
```

## Why

WinUI 3 is slow in ways that are *structural*, not tunable: every `DependencyProperty` boxes through an `object`-typed store, every control is a finalizable COM object, the visual+logical trees are doubled, layout/composition run on one thread, and `setState` fans out to a broad re-render. The result is GC stutter under scroll, a UI thread that blocks easily, and a heavy footprint.

fluent-gpu attacks the *causes*: unmanaged SoA columns instead of dependency properties; generational handles + arenas + slabs instead of GC objects on the hot path; hand-vtable `calli` instead of COM RCW churn; a single render tree; fine-grained signals so updates are surgical, not tree-wide; and a GPU-batched paint path that targets **zero per-frame managed allocation**. The honest grades (see [the painpoints assessment](./design/winui-painpoints-assessment.md)): **GC pressure — largely solved; over-rendering — solved (granular re-render + a compositor bypass for hot values); slow UI thread — decoupled (not invincible); footprint + startup — substantially better.** It is *not* a risk-free engine — it trades GC-correctness for hand-rolled-COM and renderer correctness, made safe-by-construction where it can and CI-gated everywhere else.

The driving app is [**WaveeMusic**](https://github.com/christosk92/WaveeMusic), a Spotify desktop client — media-heavy, list-heavy (10k+ track lists), theming-heavy (album-art dynamic color, Mica), with video and synced lyrics. If fluent-gpu can run Wavee at 60fps with no GC hitch, it works.

## How it works (one diagram)

```
 Components + Hooks + fluent DSL  (Element records, pure C#)
        │  state lives in SIGNALS — a read subscribes; a write re-runs only what read it
        │  keyed reconcile → ISceneBackend (handle-in/handle-out, POD)   (granular: one component's subtree)
 Scene (SoA RenderNode tree) · Layout (ported Yoga, scoped relayout) · Input/A11y · Animation
        │  DrawList (POD command stream)
 Renderer (batched quads, SDF, glyph atlas)  →  RHI / PAL / Text seams
        │
 Windows: D3D12 · DXGI flip + DComp · DirectWrite · WIC · UIA   (macOS: Metal · CoreText · Cocoa, later)
```

Three update paths, cheapest first: a **binding** (signal → node transform/paint, compositor-only); a **granular
re-render** (one component's subtree + a scoped relayout firewalled at a layout boundary); **reactive control-flow**
(`For`/`Show`, a keyed diff of one boundary, no parent re-render). See [`docs/guide/reactivity.md`](./docs/guide/reactivity.md).

Read the [architecture spec](./design/architecture-spec.md) for the full picture, the [subsystem index](./design/subsystems/README.md) for the component designs, or [`reconciler-hooks.md §0bis`](./design/subsystems/reconciler-hooks.md) for the as-built signals model.

## Running it

```powershell
# The verification gate — ~60 cross-seam golden checks on the headless backends (no GPU/window needed):
dotnet run --project src/FluentGpu.VerticalSlice        # expect: "ALL CHECKS PASSED"

# The real Windows app (D3D12 + DirectComposition + Mica + real system accent + WIC album art):
dotnet run --project src/FluentGpu.WindowsApp
```

```
[PASS] window · rounded-rect · text · flex/grid · reconciler + UseState ·
       granular re-render (componentsRendered == 1) · signal-bound slider (no re-render/reconcile/layout) ·
       For/Show · scroll + 10k-row virtualization · async images · navigation · Wavee shell · ZERO paint-half alloc
```

Authoring an app is one call — `FluentApp.Run(() => new App())` brings up a DPI-aware window, D3D12, Mica + the OS
accent, the font + image systems, and the frame loop. The 18-assembly solution (`src/FluentGpu.slnx`) is .NET 10 /
C# 14 / NativeAOT-ready; see [`src/README.md`](./src/README.md).

## Documentation & agents

- **[`docs/guide/`](./docs/guide/README.md)** — the developer **and** agent guide: the signals model, hooks,
  controls, layout, the render pipeline, performance, and a **symptom → cause → fix** pitfalls page. Start at the
  [hub](./docs/guide/README.md).
- **[`AGENTS.md`](./AGENTS.md)** — guidance for AI agents (Codex et al.): build/test commands, the rules, the
  where-to-change-what file map, and the verification gate. Claude Code users also get the `.claude/skills/fluentgpu`
  skill and the design-corpus discipline in [`CLAUDE.md`](./CLAUDE.md).

## Roadmap (relative phases)

Built in an order where **safety is never speculative** (full detail in the [hardened-v1 plan](./design/hardened-v1-plan.md) §6):

1. **Vertical slice** — ✅ window → GPU clear → rounded rect → text → flex → reconciler + `UseState` → clickable button, zero per-frame alloc on the paint half.
2. **Core engine** — ✅ renderer, flex + grid layout, text, the **signals-first** reconciler/hooks runtime (granular re-render + compositor bypass + scoped relayout), the DSL + modifiers, input.
3. **App subsystems** — ✅ async image/media pipeline (off-thread WIC decode, residency, cross-fade), virtualization (10k+ lists), theming + album-art accent + Mica, the control kit, navigation, animation — everything WaveeMusic needs, exercised by the headless Wavee-shell check.
4. **Hardening** — 🚧 generated + thread-confined COM, the validation spine (alloc tripwire ✅, golden-image, leak gates), then the render-thread seam behind a green race gate.
5. **Cross-platform** — ⏳ the macOS (Metal/CoreText/Cocoa) backend behind the existing seam.

The current edge: on-screen D3D12 pixel polish (the logic is verified headlessly; GPU pixels are a separate manual pass), the source generators, and the hardening spine.

## Built on

- **[ComputeSharp](https://github.com/Sergio0694/ComputeSharp)** — vendored DX12/DXGI COM bindings, `ComPtr<T>`, the C#→HLSL transpiler, and the NativeAOT interop patterns.
- **[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)** — the programming model (Element/Component/hooks) and the pure-C# Yoga flexbox port.
- **[microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml)** — studied for the rendering/text/layout architecture (and what to avoid).
- **[SolidJS](https://www.solidjs.com/)** — the signals / fine-grained reactivity model the reactive core is built on.

## License

MIT.
