# fluent-gpu

**A from-scratch, near-zero-allocation, NativeAOT, GPU-rendered UI engine for .NET 10 — with Reactor-style fluent C# markup and React-style hooks.**

> **Status (June 2026): architecture complete; engine scaffold + a *running* vertical slice in [`src/`](./src/README.md).** The full design corpus is in [`design/`](./design/README.md); the 18-assembly solution builds on **.NET 10 / C# 14 / NativeAOT-ready**, and the minimum vertical slice passes the architecture's acceptance test on the headless backends. The on-screen Direct3D 12 backend is the next milestone.
<img width="732" height="614" alt="image" src="https://github.com/user-attachments/assets/88adc595-7ff5-4ed6-afe7-a00b1d0ef6d6" />

---

## What it is

fluent-gpu keeps the part of [WinUI](https://github.com/microsoft/microsoft-ui-xaml) developers actually want — and throws away the part that hurts. You write UI exactly like [Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor): immutable `Element` records assembled with a fluent C# DSL, rendered by `Component.Render()`, with co-located hooks (`UseState`/`UseEffect`/…) and a keyed diffing reconciler. **No XAML, no ViewModels.**

The difference is what's underneath. Instead of patching WinUI's heavy C++ XAML/Composition core, the reconciler patches **our own** retained struct-of-arrays render tree, which a custom batched 2D renderer paints on **Direct3D 12 + DirectWrite + DirectComposition** — pure Win32, **no WinRT, no Windows App SDK** — behind a swappable platform seam so a macOS (Metal + CoreText) backend can drop in later.

```csharp
class Counter : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return VStack(
            Heading($"Count: {count}"),
            HStack(8,
                Button("-", () => setCount(count - 1)),
                Button("+", () => setCount(count + 1))));
    }
}
```

## Why

WinUI 3 is slow in ways that are *structural*, not tunable: every `DependencyProperty` boxes through an `object`-typed store, every control is a finalizable COM object, the visual+logical trees are doubled, and layout/composition all run on one thread. The result is GC stutter under scroll, a UI thread that blocks easily, and a heavy footprint.

fluent-gpu attacks the *causes*: unmanaged SoA columns instead of dependency properties; generational handles + arenas + slabs instead of GC objects on the hot path; hand-vtable `calli` instead of COM RCW churn; a single render tree; and a GPU-batched paint path that targets **zero per-frame managed allocation**. The honest grades (see [the painpoints assessment](./design/winui-painpoints-assessment.md)): **GC pressure — largely solved; slow UI thread — decoupled (not invincible); footprint + startup — substantially better.** It is *not* a risk-free engine — it trades GC-correctness for hand-rolled-COM and renderer correctness, which the design makes safe-by-construction where it can and CI-gated everywhere else.

The driving app is [**WaveeMusic**](https://github.com/christosk92/WaveeMusic), a Spotify desktop client — media-heavy, list-heavy (10k+ track lists), theming-heavy (album-art dynamic color, Mica), with video and synced lyrics. If fluent-gpu can run Wavee at 60fps with no GC hitch, it works.

## How it works (one diagram)

```
 Components + Hooks + fluent DSL  (Element records, pure C#)
        │  keyed reconcile → ISceneBackend (handle-in/handle-out, POD)
 Scene (SoA RenderNode tree) · Layout (ported Yoga) · Input/A11y · Animation
        │  DrawList (POD command stream) — published to a render thread
 Renderer (batched quads, SDF, glyph atlas)  →  RHI / PAL / Text seams
        │
 Windows: D3D12 · DXGI flip + DComp · DirectWrite · UIA   (macOS: Metal · CoreText · Cocoa, later)
```

Read the [architecture spec](./design/architecture-spec.md) for the full picture, or the [subsystem index](./design/subsystems/README.md) for the 16 component designs.

## Running it

The portable core is implemented and the **minimum vertical slice runs green** on the headless backends (the architecture's acceptance test). The Windows D3D12/DirectWrite/UIA leaves and the source generators are scaffolded.

```powershell
cd src
dotnet build FluentGpu.slnx                  # whole 18-assembly solution (.NET 10)
dotnet build FluentGpu.Slice.slnf            # just the headless, runnable subset
dotnet run  --project FluentGpu.VerticalSlice
```

```
[PASS] window + GPU clear · rounded-rect · text runs · flex layout ·
       reconciler + UseState · clickable Button (Count 0 → 1) ·
       steady frame memoized · ZERO managed alloc on the paint half (0 bytes)
```

The `Counter` shown above is the actual slice component. The solution (`FluentGpu.slnx`) uses solution folders (Core · Seams · Backends.Windows · Backends.Headless · Tooling · Apps) with `.slnf` filters; see [`src/README.md`](./src/README.md).

## Roadmap (relative phases)

Built in an order where **safety is never speculative** (full detail in the [hardened-v1 plan](./design/hardened-v1-plan.md) §6):

1. **Vertical slice** — ✅ *done (headless)*: window → GPU clear → rounded rect → text → flex → reconciler + `UseState` → clickable button — the architecture's acceptance test, **zero per-frame alloc on the paint half**, running on the headless RHI/PAL/Text backends. The on-screen **real D3D12** path is the next step.
2. **Core engine** — full renderer, layout, text, the reconciler/hooks runtime, the DSL + source generators, input + UIA.
3. **App subsystems** — image/media pipeline, virtualization (10k+ lists), theming + album-art dynamic color, Mica/backdrops — i.e. everything WaveeMusic needs.
4. **Hardening, folded into v1** — generated + thread-confined COM, the validation spine (alloc tripwire, golden-image, leak gates), then the **render-thread seam** (so a busy UI thread no longer stalls present), flipped on only behind a green race gate.
5. **Cross-platform** — the macOS (Metal/CoreText/Cocoa) backend behind the existing seam.

## Built on

- **[ComputeSharp](https://github.com/Sergio0694/ComputeSharp)** — vendored DX12/DXGI COM bindings, `ComPtr<T>`, the C#→HLSL transpiler, and the NativeAOT interop patterns.
- **[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)** — the programming model (Element/Component/hooks) and the pure-C# Yoga flexbox port.
- **[microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml)** — studied for the rendering/text/layout architecture (and what to avoid).

## License

MIT.
