# fluent-gpu — FINAL ARCHITECTURE SPECIFICATION (Architect-of-Record)

> A from-scratch, GPU-rendered, NativeAOT C# UI engine that keeps the Reactor programming model
> (immutable `Element` records + `Component` + Hooks + keyed reconciler) but replaces the heavy
> C++ XAML/Composition core with **our own batched 2D renderer** on **D3D12**, **DirectWrite**
> glyphs, **DirectComposition** present — all behind a swappable **PAL / RHI / Text** seam so a
> macOS **Metal + CoreText + Cocoa** backend drops in later. This document folds in the eight
> subsystem designs and every accepted adversarial revision into one coherent, buildable spec.

---

## 1. EXECUTIVE SUMMARY

### 1.1 What it is

fluent-gpu is a retained-mode, GPU-composited UI engine written in 100% C# targeting the latest
.NET with **NativeAOT** (no reflection, no runtime codegen, fully trimmable). Application code is
written exactly as in Reactor: immutable `Element` records assembled with a fluent C# DSL, rendered
by `Component.Render()`, with React-style Hooks and a keyed diffing reconciler. The **only** change
to the programming model is the *patch target*: the reconciler no longer mutates WinUI
`FrameworkElement`s — it patches **our** retained `SceneStore` (a struct-of-arrays RenderNode tree),
which our layout engine measures/arranges and our GPU renderer paints.

### 1.2 The stack

```
 ┌──────────────────────────────────────────────────────────────────────────────────────┐
 │  APPLICATION  — Components, Hooks, fluent DSL  (Element records; pure C#)               │
 ├──────────────────────────────────────────────────────────────────────────────────────┤
 │  RECONCILER   — keyed LIS diff → ISceneBackend (handle-in/handle-out, POD-only)         │
 ├──────────────────────────────────────────────────────────────────────────────────────┤
 │  SCENE (SoA)  — RenderNode columns + 3-axis dirty flags + generational handles          │
 │  LAYOUT       — struct-based flexbox + grid (Yoga algorithm ported, SoA substrate)      │
 │  INPUT/A11Y   — hit-test, focus, gestures, commands; UIA/TSF projection of the tree     │
 │  ANIMATION    — composition-style transform/opacity timelines (no relayout)             │
 ├──────────────────────────────────────────────────────────────────────────────────────┤
 │  RENDER       — DrawList POD command stream, batcher (instanced quads), tessellator     │
 ├───────────────────────────── the swap line (POD only) ─────────────────────────────────┤
 │  RHI (iface)  IGpuDevice / ISwapchain / ICommandEncoder / IPipeline ... — generational  │
 │  PAL (iface)  IPlatformApp / IPlatformWindow / IPlatformAppLoop / IImeSession ...        │
 │  TEXT (iface) IFontSystem / ITextShaper / IGlyphRasterizer / IGlyphAtlas                 │
 ├──────────────────────────────────────────────────────────────────────────────────────┤
 │  WINDOWS LEAVES (reference impl)         │  macOS LEAVES (future, no code above changes) │
 │  Rhi.D3D12   (ComPtr<ID3D12*>, PSO,      │  Rhi.Metal   (id<MTLDevice/...>)              │
 │              RTV/DSV heaps, D3D12MA)     │                                               │
 │  Pal.Windows (HWND, WM_*, raw input,     │  Pal.Cocoa   (NSWindow/NSView, NSEvent,       │
 │              DXGI flip + DComp present,  │              CVDisplayLink, NSTextInputClient)│
 │              PerMonitorV2, TSF/Imm32)    │                                               │
 │  Text.DirectWrite (DWrite shape+raster)  │  Text.CoreText                                │
 │  Accessibility.Uia                       │  Accessibility.NSAccessibility                │
 │  Win32.Interop (vendored ComputeSharp    │                                               │
 │              bindings + DComp/DWrite/UIA)│                                               │
 └──────────────────────────────────────────────────────────────────────────────────────┘
```

### 1.3 Headline design bets (each justified later, against footprint / zero-alloc / AOT / seam)

1. **D3D12 is the sole Windows backend; D3D11 is dropped.** ComputeSharp's reusable value
   (device/fence/descriptor/command-pool plumbing, D3D12MA, C#→HLSL transpiler) is DX12-only;
   DComp composes any swapchain so D3D11 buys nothing for present. The RHI seam is drawn at the
   **modern-explicit-graphics-API** altitude (explicit barriers, immutable PSOs) so Metal slots in 1:1.
2. **COM is tiered — hand-vtable on the hot path, source-generated cold** (canonical:
   `dotnet10-csharp14-zero-alloc.md` §4; see [`SPEC-INDEX.md`](./SPEC-INDEX.md)). The per-frame hot path
   (D3D12 command list/queue/swapchain Present, DComp, and any CCW invoked inside the frame loop) uses
   ComputeSharp's verified pattern: `unsafe partial struct : IComObject` + `void** lpVtbl` +
   `delegate* unmanaged[MemberFunction]` calli with a `static abstract Guid* IID`, and the implement
   (callee) direction uses hand-built static vtables + `[UnmanagedCallersOnly(CallConvMemberFunction)]` +
   `GCHandle` (the verified `PixelShaderEffect` recipe). **All cold/warm COM (UIA, TSF, OLE, DWrite
   *setup*) uses `[GeneratedComInterface]`/`[GeneratedComClass]`** — the source-generated, AOT-recommended
   path — isolated in `FluentGpu.PlatformIntegration`. **`System.Runtime.InteropServices.ComWrappers` is
   rejected on the hot path only** (to avoid the RCW-cache `GetOrCreateObjectForComInstance` lookup and
   keep call-site control — *not* a per-call alloc, which .NET 10 does not incur), and is adopted via the
   source generators for everything cold.
3. **Custom batched 2D renderer**: analytic **SDF** anti-aliasing for the rect family (rounded rects,
   borders), **closed-form erf drop shadows in one quad** (no offscreen blur for the common case),
   **pre-rasterized glyph coverage** in a GPU atlas, **CPU tessellation** of Bezier/arc paths with AA
   fringe. Single-sampled flip-model back buffer; MSAA (if ever needed) is an opt-in offscreen +
   resolve path, not the hot path.
4. **Linear blending, sRGB on scan-out.** Swapchain buffer is `BGRA8_UNORM` (flip-model + DComp reject
   `_SRGB` buffers); the **RTV is created as `BGRA8_UNORM_SRGB`** so blend + MSAA-resolve happen in
   linear and the hardware sRGB-encodes on write. Renderer outputs **premultiplied** alpha to satisfy
   DComp `PREMULTIPLIED`. Text is a *deliberate exception*: glyph coverage is blended with a DWrite-style
   gamma/contrast curve to match native text.
5. **One SoA RenderNode store, generational handles, zero per-frame managed allocation.** A node is a
   32-bit slot index across parallel columns; no GC ref lives on a per-frame path. Three orthogonal
   dirty axes (Layout / Paint / Transform) let animation move a node without re-recording its draw
   commands and without relayout.
6. **Reactor model preserved; reconciler retargeted via `ISceneBackend`.** Diffing control flow
   (self-trigger/memo skip, re-entrancy cap, error boundaries, context shadow-stack) is preserved; the
   keyed-LIS *algorithm* is re-authored against arena scratch (the original allocates 6+ collections per
   reorder). `params object[]` hook deps → `ReadOnlySpan<DepKey>` (16-byte blittable, no boxing).
7. **Reuse-not-rebuild where it pays.** Depend on ComputeSharp's `Win32.D3D12`/`Win32.D2D1` bindings,
   `ComPtr<T>`, `D3D12MemoryAllocator`, and the C#→HLSL transpiler (for compute/effects). Fork
   `GraphicsDevice`/command-pool/descriptor-heap/fence plumbing (add DIRECT queue + RTV/DSV heaps +
   graphics PSO path). Build ourselves: the graphics-first RHI, DXGI-flip+DComp present, DComp/DWrite/
   TSF/UIA bindings, the 2D batcher, the layout engine, PAL, headless backends.

### 1.4 Threading & frame model (one line)

> **⊳ Canonical threading model.** The single-thread loop is **build-order step 1** (`hardened-v1-plan.md` §6), not the shipping topology. The canonical model is the **render-thread seam** — `hardened-v1-plan.md` §2 + [`subsystems/threading-render-seam.md`](./subsystems/threading-render-seam.md) §14 (see [`SPEC-INDEX.md`](./SPEC-INDEX.md)): a **PUBLISH(13a)** phase splits the loop, record/batch/submit/present run on the render thread, and `QUARANTINE = RenderInFlightDepth` (compile-asserted, consume-gated) supersedes the literal `0`/`2`. §4.8 below is the lean step-1 form.

**Single UI thread v1.** 13 ordered frame phases (pump → input → hook-flush → render → reconcile →
layout → layout-effects → animation → record → batch → submit → present → passive-effects → arena-swap).
The DrawList POD stream is the *designed* render-thread boundary for the future (slot-reuse quarantine
constant flips it on); v1 keeps the quarantine at 0.

---

## 2. DESIGN PRINCIPLES & CONSTRAINTS (enforceable rules)

| # | Rule | Enforcement |
|---|------|-------------|
| **P1** | **Zero / near-zero per-frame managed allocation.** Hot paths use struct data, arenas, slabs, object pools, generational handles, span APIs. No `params object[]`, no LINQ, no `IEnumerable` iterators, no per-node delegates on hot paths. | CI per-frame alloc budget gate (§8); `ref struct` enumerators; arena-backed scratch; `[Conditional("DEBUG")]` alloc-tripwires around frame phases. |
| **P2** | **NativeAOT-clean.** No reflection, no `MakeGenericType`/`Activator`, no runtime IL emit. **COM is tiered** (canonical: [`SPEC-INDEX.md`](./SPEC-INDEX.md) → `dotnet10-csharp14-zero-alloc.md` §4): hand-vtable `calli` on the per-frame hot path + in-loop CCWs; `[GeneratedComInterface]`/`[GeneratedComClass]` (source-gen) for all cold/warm COM. `ComWrappers` rejected **on the hot path only** (cache-lookup + call-site control, not per-call alloc). Source generators do all "codegen" at build time. | `<PublishAot>true</PublishAot>`, `<IsTrimmable>true</IsTrimmable>`, zero `[DynamicallyAccessedMembers]` in portable assemblies; `sizoscope`/`.mstat` CI gate. |
| **P3** | **Small footprint.** One graphics backend, one binding set, no WinRT projection, no Windows App SDK. | Ratcheted exe-size CI gate (start ~8 MB, tighten); `[SkipLocalsInit]`; trimming feature-switches per leaf. |
| **P4** | **Swappable platform seam.** Everything OS/GPU-specific lives **below** the PAL/RHI/Text interfaces in leaf assemblies referenced **only** by `Hosting`. The seam vocabulary is POD: generational handles, blittable descriptor structs, spans, opaque `NativeHandle`. No `HWND`/`HRESULT`/`ComPtr`/`ID3D12*`/`IDXGI*`/`WM_*`/`DXGI_*` crosses it. | Assembly reference graph is acyclic (§3); the only `object`-typed seam field is eliminated (`NativeHandle` POD). Headless PAL+RHI prove portability in CI. |
| **P5** | **Reactor model preserved verbatim where possible.** `Element` stays an immutable record at the edge; `Component`, Hooks, keyed reconciler, context, error boundaries, hot reload kept. Divergences (effect timing, deps representation, re-authored LIS) are **explicitly labeled**, not smuggled as "verbatim." | The reuse table (§3b) marks each Reactor file take-as-is / port / re-author / inspire. |
| **P6** | **GC refs only at the edges.** Permitted: user delegates, `Element`/`Component` instances, `RenderContext`/hook cells (mount-only), `ComPtr<T>` roots inside leaf `HandleTable`s, prev-frame `Element` retention. Everything else is a handle or POD. | Confinement asserts; `HandleTable.Free` runs typed teardown (Dispose `ComPtr`) before slot reclaim. |
| **P7** | **Single source of truth.** One RenderNode tree (no WinUI dual visual/logical tree). UIA, hit-test, IME, layout, paint all read the same `SceneStore` columns. | `SceneStore` SoA columns; UIA `Navigate` is a topology walk, not a peer tree. |
| **P8** | **Color/coordinate/DPI conventions are contractual, set once.** Brush color = straight-alpha sRGB float4 → renderer converts to linear-premultiplied at shader input. DPI applied once at the layout→world transform composition. `Bounds` is node-LOCAL; `LocalTransform` maps local→parent. | A shared transform-accumulation helper used by hit-test, UIA bounding-rect, and IME caret. |

---

## 3. MODULE / ASSEMBLY LAYOUT

18 assemblies; strictly acyclic. **NEW** = built from scratch; **EXTRACT** = forked/vendored from a
reference repo; **PORT** = algorithm reused, substrate rewritten.

```
                         ┌──────────────┐
                         │  Foundation  │  NEW  (no deps) — Handle, SlabAllocator, ArenaAllocator,
                         └──────┬───────┘       ObjectPool, HandleTable, StringId/StringTable,
                                │               Size2/Scale/PointPx, Affine2D/Edges4/CornerRadius4,
        ┌──────────┬───────────┼───────────┬──────────┬──────────┐  interning tables, NodePaintSpec POD
        ▼          ▼           ▼           ▼          ▼          ▼
     ┌─────┐   ┌─────┐    ┌───────┐   ┌──────┐   ┌──────┐   ┌──────┐
     │ Pal │   │ Rhi │    │ Text  │   │Scene │   │Layout│   │ Input│   (Pal/Rhi/Text = iface-only)
     │iface│   │iface│    │ iface │   │ SoA  │   │      │   │      │   Scene/Layout/Input/Anim/Dsl/
     └──┬──┘   └──┬──┘    └───┬───┘   └──┬───┘   └──┬───┘   └──┬───┘   Hooks = portable, NEW/PORT
        │         │           │          │          │          │
        │      ┌──┴──┐     ┌──┴───┐   ┌──┴────────┐ │       ┌──┴────┐
        │      │     │     │      │   │ Animation │ │       │  Dsl  │ NEW (deps: Foundation only)
        │      ▼     ▼     ▼      ▼   └─────┬─────┘ │       └──┬────┘
        │   ┌─────────────────────────┐    │       │       ┌──┴────┐
        │   │        Render           │ NEW│       │       │ Hooks │ PORT (Reactor RenderContext)
        │   │ DrawList POD + batcher   │◄───┘       │       └──┬────┘
        │   │ + tessellator (refs Rhi  │            │          │
        │   │ iface + Text iface only) │            └────┐  ┌──┘
        │   └────────────┬─────────────┘                 ▼  ▼
        │                │                          ┌──────────────┐
        │                │                          │  Reconciler  │ PORT (control flow) + re-author
        │                │                          │ ISceneBackend│  (keyed LIS, deps, scene write)
        │                │                          └──────┬───────┘
        │                └──────────────┬──────────────────┘
        │                               ▼
        │                        ┌──────────────┐
        └───────────────────────►│   Hosting    │ NEW (composition root; the ONLY assembly that
                                 │ (frame loop) │      references the leaves below)
                                 └──────┬───────┘
        ┌──────────────┬───────────────┼───────────────┬──────────────────┐
        ▼              ▼               ▼               ▼                  ▼
 ┌────────────┐ ┌───────────┐ ┌──────────────┐ ┌──────────────┐ ┌─────────────────┐
 │ Pal.Windows│ │ Rhi.D3D12 │ │Text.DirectWr.│ │Accessibility.│ │ Win32.Interop   │
 │   (LEAF)   │ │  (LEAF)   │ │   (LEAF)     │ │ Uia (LEAF)   │ │ EXTRACT+NEW LEAF │
 │  NEW+EXTR  │ │ EXTRACT+  │ │ NEW          │ │ NEW          │ │ vendored CS      │
 │            │ │   FORK    │ │              │ │              │ │ bindings + DComp │
 │            │ │           │ │              │ │              │ │ /DWrite/TSF/UIA  │
 └────────────┘ └───────────┘ └──────────────┘ └──────────────┘ └─────────────────┘
       └──────────────┴───────────────┴───────────────┴── all reference Win32.Interop ──┘
 ┌────────────┐   plus testing leaves referenced only by test hosts:
 │Rhi.Headless│   NEW — WARP-backed + CPU/null encoder (golden structural + smoke pixel tests)
 │Pal.Headless│   NEW — synthetic window/input/DPI events
 └────────────┘
```

**Acyclicity invariants (enforced):**
- `Render` references `Rhi` (interface) and `Text` (interface) — **never** `Rhi.D3D12` / `Text.DirectWrite`.
- `Dsl` and `Hooks` know nothing of `Scene`/`Render`/`Rhi`; they depend only on `Foundation`. The
  **`Reconciler` is the only bridge** to `SceneStore` via `ISceneBackend`.
- **Scene-writing generated code lives in `Reconciler`/leaf-backend, NOT in `Dsl`** (corrects the DSL
  agent's layering cycle). `Element` records carry data only (`[Prop]`s + `ModifierRef` + `Key`); the
  per-element column writer is a generated `switch(ElementTypeId)` in the reconciler, or writes a
  Foundation-level `NodePaintSpec` POD the reconciler copies into `SceneStore`.
- The `SourceGen` analyzers are build-time only. **Two analyzer DLLs**: `FluentGpu.SourceGen` (portable:
  Element/Modifier/Diff/Theme/HookDeps/ElementTypeId, Win32-free) and `FluentGpu.Interop.SourceGen`
  (the COM-binding generator), referenced **only** by the Windows leaves — keeping the portable toolchain
  free of Win32 COM concepts.

---

## 3b. REUSE STRATEGY

### 3b.1 The DX12-primary decision (resolved)

**D3D12 is the sole Windows RHI backend; D3D11 dropped.** Verified: ComputeSharp's device is
compute-only (`CreateComputePipelineState`/`CreateDescriptorHeap`/`CreateCommittedResource`/
`CreateRootSignature`; **no** `CreateGraphicsPipelineState`, **no** `CreateRenderTargetView`,
**no** `OMSetRenderTargets` — confirmed by grep). Its reuse value is DX12-bound. DComp composes any
`IDXGISwapChain`, so D3D11 adds nothing for present. WARP (via `IDXGIFactory6` enum fallback, already in
ComputeSharp's `DeviceHelper`) covers VMs/RDP and is also the headless test path. Cost accepted: no
D3D11on12 fallback for pre-2012 GPUs.

### 3b.2 The graphics-pipeline-shader gap (resolved)

ComputeSharp's C#→HLSL transpiler emits **compute** (`[numthreads]`/`SV_DispatchThreadID`) only — it
cannot author VS+PS graphics stages. Decision: **author the SDF/quad/shadow/glyph shaders as `.hlsl`
+ `shapes_common.hlsli`, compiled offline by DXC → DXIL** (and `-spirv` → SPIRV-Cross → MSL for Metal
later), embedded as source-generated `byte[]` blobs (no runtime compile, AOT-clean, trimmable). The
C#→HLSL transpiler is **retained only** for the optional `Effects.D2D1` leaf (blur/backdrop pixel
shaders behind `IEffectRunner`) — never the hot path. A **compute rasterizer was rejected**: our quad
batcher needs a graphics VS+PS pipeline + DXGI swapchain + DComp present, which the graphics path
delivers with deterministic barriers.

### 3b.3 The reuse table

| Component | Decision | Depend / Vendor | Rationale |
|---|---|---|---|
| `ComPtr<T>`, `IComObject`, `[NativeTypeName]`/`[VtblIndex]` attrs | **EXTRACT verbatim** into `Win32.Interop` | **Vendor** | Only COM lifetime primitive (per contract). `unsafe struct`, no GC alloc, AOT-trivial. |
| `ComputeSharp.Win32.D3D12` / `.Win32.D2D1` bindings (`ID3D12*`, `IDXGI*`, `ID2D1*`) | **Reuse as-is** (extract source) | **Vendor** | ClangSharp-generated, calli-based, AOT-proven (ships in Store/Paint.NET). |
| `GraphicsDevice` device/queue/fence/descriptor-heap/command-list-pool plumbing | **EXTRACT + FORK** | **Vendor+fork** | ComputeSharp has COMPUTE+COPY queues, CBV/SRV/UAV heap only. **Fork adds:** DIRECT queue, RTV+DSV descriptor heaps, graphics `ID3D12GraphicsCommandList` recording, DIRECT-typed command-list pool. Keep fence-wait, device-lost (`fence→MaxValue`+`RegisterWaitForSingleObject`), per-LUID device cache. |
| `ComputeSharp.D3D12MemoryAllocator` (D3D12MA) | **Reuse as-is** | **Depend (NuGet)** if stable, else vendor | Pooled GPU resource alloc; wire via `DeviceHelper.ConfigureAllocatorFactory`. Manages textures/buffers we create — **not** DXGI back buffers (special-cased in the deferred-delete ring). |
| `ComputeSharp.SourceGeneration.Hlsl` + `.SourceGenerators` (C#→HLSL) | **Inspire / partial reuse** | n/a | Compute-only. Reuse the incremental-gen helpers (`EquatableArray`, `HierarchyInfo`, `DiagnosticInfo`) and packaging discipline. Graphics shaders authored HLSL+DXC (see §3b.2). Effects path may reuse the transpiler for D2D1 pixel shaders. |
| `ComputeSharp.D2D1` (+ `.Dxc`) pixel-shader path | **Inspire / optional leaf** | n/a | FXC `ps_x_x`, Windows-only → reserved for `Effects.D2D1` behind `IEffectRunner`, never hot path. |
| DXGI flip-model **present** (swapchain rotate, frame-latency waitable, resize-pending) | **BUILD (seed shape from `SwapChainManager`)** | n/a | ComputeSharp's `SwapChainManager` is `SwapChainPanel`/`DispatcherQueue`-coupled and copies a compute UAV → back buffer. Reuse the *shape*; build HWND/DComp flip-model present of our rendered RTV. |
| **DirectComposition** bindings (`IDCompositionDevice/DesktopDevice/Target/Visual`) | **BUILD (generate)** | n/a | Zero in ComputeSharp (verified). Same hand-vtable `IComObject` pattern. |
| **DWrite** bindings + analyzer CCWs | **BUILD (generate)** | n/a | Zero in ComputeSharp. Consume direction = hand vtable; **callee direction** (`IDWriteTextAnalysisSource/Sink`, font loaders, geometry sink) = hand-built static vtables (no ComWrappers template exists). |
| **UIA / TSF / OLE** bindings | **BUILD (generate)** | n/a | Implement direction via the verified `PixelShaderEffect` recipe (POD struct + static vtable + `[UnmanagedCallersOnly]` + `GCHandle` + `Interlocked` refcount + this+offset QI). |
| **D3D11 backend** | **DROP** | — | §3b.1. |
| Reactor `Element` / DSL / `ElementExtensions` | **PORT (shape) + re-author modifiers** | n/a | `Element` stays a record. Modifier merge re-authored to arena-backed `ModifierOp`s (Reactor's `el with { Modifiers = … }` is O(nodes×modifiers) record copies). Event/a11y delegate *signatures* redefined portably (Reactor's are WinRT-typed). |
| Reactor `Reconciler` / `ChildReconciler` (keyed LIS) | **PORT control flow; RE-AUTHOR keyed-middle + `ComputeLIS`** | n/a | Self-trigger/memo-skip/error-boundary/re-entrancy cap preserved. `ComputeLIS`+`Filter`+keyed-middle allocate 6+ collections per reorder → re-authored against arena `Span<>` scratch. `IChildCollection`→`NodeChildCollection` (incl. `RemoveAt`); keys read from the `Key` column via `backend.GetKey`. All WinUI/Composition calls stripped. |
| Reactor `RenderContext` / Hooks | **PORT (mount-only edge alloc)** | n/a | `List<HookCell>` kept (edge). `params object[]`+`deps.ToArray()`+boxing `Equals` → `ReadOnlySpan<DepKey>`. Effect timing re-phased (§5.6). |
| Reactor `ReactorHost` coalescer + `RenderPriorityPolicy` | **PORT** | n/a | `_isRendering` guard + `Interlocked.CompareExchange` gate + low-priority re-enqueue; transport `DispatcherQueue`→`IPlatformAppLoop`. |
| Reactor Yoga (pure-C# flexbox) | **PORT algorithm; REWRITE substrate** | n/a | 11-step `CalculateLayoutImpl`, free-space distribution, baseline, abs-pos, §4.5 min, pixel-snap math, 8-entry measurement ring **kept**. `YogaNode`/`YogaStyle`/`LayoutResults` classes + `List<YogaNode>` + `[ThreadStatic] Stack<>` pool → SoA columns + `ref struct LayoutNode` facade + arena scratch. |
| Reactor Input (gesture/focus/command vocab) | **PORT (re-type to `Vec2`); re-implement engines** | n/a | `Command` record reused unchanged (pure data). Gesture structs re-typed off `Windows.Foundation.Point`; FSM + **from-scratch inertia integrator** built. Focus/announce hooks keep names, reimplemented over `FocusEngine`/`IA11yBackend`. |

**Depend-on vs vendor:** vendor (source-extract into `Win32.Interop`) the COM bindings + `ComPtr<T>` so
we can fork/extend freely and pin trimming. Depend-on (NuGet) is acceptable only for D3D12MA if its
package proves AOT-stable in the spike; otherwise vendor it too. No NuGet dependency that drags WinRT
or `SwapChainPanel`.

---

## 4. FOUNDATIONS (shared vocabulary — authoritative)

### 4.1 Handle = 8 bytes {index:32, gen:32}

```csharp
public readonly struct Handle : IEquatable<Handle>
{
    private readonly uint _index;   // slot index into the owning slab column (0 = null sentinel)
    private readonly uint _gen;     // 32-bit generation; bumped on alloc AND free (ABA defense)
    public bool IsLive(uint slotGen) => _gen == slotGen;
}
```

**Amendment folded:** generation widened to **32 bits** (handle stays 8 bytes); the `kind` byte is
**dropped from the handle** and moved to the typed wrapper's `[Conditional("DEBUG")]` assert (the wrapper
statically knows its kind). This removes the "park slot forever on wrap" policy that violated the
no-growth invariant — 2^31 alloc/free cycles per slot (~400 days/slot at 60 fps) and on the practically-
unreachable wrap we reset the slot gen and accept astronomically-low ABA risk.

Zero-cost typed wrappers: `readonly struct NodeHandle/BrushHandle/TextureHandle/ClipHandle/GlyphRunHandle/
PathHandle/PipelineHandle/...` each wrap one `Handle` with `implicit operator Handle` + a debug kind
assert. **Rule (P6):** nothing on a per-frame path holds a GC ref into a pooled collection — only
generational handles. GC refs live at the permitted edges only.

### 4.2 Four allocators

```csharp
SlabAllocator<T> where T : unmanaged    // stable, gen-versioned, intrusive free list, O(1) alloc/free,
                                        //   ref T Get(Handle) (gen-checked in DEBUG), DenseSpan for SIMD
ArenaAllocator                          // per-frame bump, O(1) Reset, 16-byte aligned; AllocSpan<T>
ObjectPool<T> where T : class           // edge GC recycle (RenderContext, Component); cap 32, explicit Reset
HandleTable<TResource>                  // the ONE place COM/GPU ownership meets handles (see §4.7)
```

**Amendment folded (BLOCKER):** the **intrusive-first-4-bytes free-list trick is forbidden for any slab
whose element struct contains a pointer/`ComPtr`** (it stomps the low 32 bits of `ComPtr.ptr_`). Pure-
numeric POD columns (Topology, LayoutInput, …) keep the trick; pointer-bearing slabs (`HandleTable`
resource slots) use a separate `int[] _next` free-link array.

**Amendment folded (slab growth safety):** `ref`-returning accessors (`ref T Get`) must **never** be held
across a `CreateNode`/`Alloc` that can `Array.Resize`. Enforced structurally (not DEBUG-only): the
reconcile is **two-phase** — a structural sub-phase (all `CreateNode`/`Insert`/`Move`, growth allowed)
under a `_growthLocked=false`, then a non-allocating edit sub-phase under `_growthLocked=true`; `EditPaint`/
`EditLayout` assert the lock and the scope **captures+revalidates the backing array reference on Dispose**
so a release build cannot write through a stale `ref`. Plus `EnsureCapacity(count + newChildBatch)` before
edit blocks as cheap insurance.

### 4.3 Strings / brushes / clips — interning tables (Foundation, retained, slab-backed)

```csharp
public readonly struct StringId { internal readonly int Value; }   // 4 bytes, interned int
StringTable  : Intern(ReadOnlySpan<char>) → StringId ; Resolve(StringId) → ReadOnlySpan<char>
BrushTable   : SlabAllocator<BrushData>, content-hash dedup → stable BrushHandle
ClipTable    : SlabAllocator<ClipData>, content-hash dedup → stable ClipHandle
GlyphRunTable: SlabAllocator<GlyphRunRealization>, content-epoch stamped (see §4.6)
```

`BrushData` color convention (P8): **straight-alpha sRGB float4**; the renderer converts to
**linear-premultiplied** at shader input. Dedup by content hash means content cannot change under a stable
handle — the precondition that makes clean-span `memcpy` sound for brushes/clips. Keys (Reactor
`Element.Key : string?`) intern to `StringId`; **the DSL/source-gen interns keys at `Element`
construction** so the `Element` carries a `StringId` directly (avoids a per-reconcile dictionary probe
per keyed child). The interning tables live in `Foundation` so `Render` can resolve handles without
depending on `Scene`/`Reconciler` (keeps the DAG acyclic — folds FA-3).

### 4.4 RenderNode store — `SceneStore`, struct-of-arrays

One gen/free-list spine (the topology slab) indexes all parallel columns; `EnsureCapacity` resizes them
in lockstep. **One node store — no dual visual/logical tree.**

```
Column (parallel, index = node slot)        Bytes   Phase that reads it
─────────────────────────────────────────   ─────   ─────────────────────────────────────────
Topology  {Parent,FirstChild,LastChild,        24    reconcile (mutate), layout/record/hit (walk)
           PrevSibling,NextSibling,ChildCount}        ← doubly-linked: O(1) keyed Move; reverse-z hit
Identity  {Key:StringId, ElementTypeId:ushort, 16    reconcile, record (type dispatch)
           ComponentSlot:ushort, ElementEpoch,
           PayloadRef:NodeUserData}
LayoutInput  (96B hot; cold LayoutAux split)   96    layout measure inner loop
LayoutResult/Bounds {X,Y,W,H local + flags}    ~32   layout (write), record/hit/UIA (read)  ← P8: LOCAL
NodePaint {LocalTransform:Affine2D(24),         64    animation (write xform), record (read)
           Opacity, Fill/Stroke:BrushHandle,            ← one cache line
           StrokeWidth, Corners:CornerRadius4(16),
           Clip:ClipHandle, VisualKind:byte, Layer:byte}
InteractionInfo {HitCorners, HandlerMask:ushort,16    hit-test (hot)
           CursorId, HitShape, HitGeometry}
A11yInfo  {Name,AutomationId,HelpText:StringId, 24    UIA only (cold; gated by UiaClientsAreListening)
           ControlType, Patterns:ushort, Heading,
           Landmark, LabeledBy, LiveSetting}
FocusNav  {TabIndex, IsTabStop, XY{L,R,U,D}}    ~24   focus engine (Tab/arrow nav)
NodeFlags (single 32-bit dirty/state column)     4    every phase pre-filters on this
```

**Amendment folded:** `InteractionInfo`, `A11yInfo`, `FocusNav` are ratified as SoA columns on the shared
spine (cold ones scanned separately). `LayoutAux` (padding/border/insets/percent-precision min-max) is a
separate `SlabAllocator<LayoutAux>` referenced from the hot `LayoutInput` only for the ~10% of nodes that
use it (shared zero-sentinel for the common leaf) — resolving the "~96B vs ~220B" contradiction by a
hot/cold split (ratified).

**`NodeFlags` (3 orthogonal dirty axes + state):**

```csharp
[Flags] enum NodeFlags : uint {
  // self dirty axes
  LayoutDirty=1<<0, PaintDirty=1<<1, TransformDirty=1<<2,
  LayoutSelfDirty=1<<3,        // own size-affecting prop changed (intrinsic)
  LayoutParticipationDirty=1<<4, // flex-grow/shrink/basis/align-self/margin/order/position changed
  // subtree aggregates (lazy upward propagation, early-out on already-set) — OPTIONAL (see note)
  SubtreeLayout=1<<5, SubtreePaint=1<<6, SubtreeTransform=1<<7,
  // state
  Visible=1<<8, HitTestVisible=1<<9, ClipsToBounds=1<<10, PointerTransparent=1<<11,
  WantsPointer=1<<12, WantsKey=1<<13, WantsWheel=1<<14,
  Focusable=1<<16, IsTabStop=1<<17, FocusScope=1<<18, IsFocused=1<<19,
  Passthrough=1<<20,           // ComponentAnchor — skipped by layout/paint/child-snapshot
  A11yPresent=1<<24, A11yRaw=1<<25, A11yLiveRegion=1<<26, A11yOffscreen=1<<27,
  Detached=1<<28, NewThisFrame=1<<29, StaleModifiers=1<<30,
}
```

**Amendment folded (idle-frame cost + clearing-phase hazard):** the primary dirty-tracking mechanism is an
**arena-backed dirty-node worklist** that `MarkXDirty` appends to (per-frame cost = O(dirty), not
O(capacity)). The `Subtree*` aggregate bits and the `Vector256` SIMD `FlagsSpan` scan are kept **only**
for the full-rebuild / overflow path. This makes idle frames truly O(0) and removes the cross-phase
aggregate-clearing correctness hazard (aggregates, if maintained at all, are recomputed in a single
bottom-up sweep, never cleared piecemeal per consuming phase).

**Dirty taxonomy (amendment folded, MAJOR):** the reconciler distinguishes **intrinsic-affecting** dirt
(width/height/min/max/aspect/content → `LayoutSelfDirty`) from **participation-affecting** dirt (flex-grow/
shrink/basis/align-self/margin/order/position-type → `LayoutParticipationDirty`). Participation dirt
**always** forces the parent container's flex/grid algorithm to re-run regardless of whether the child's
measured size changed; only intrinsic dirt that leaves measured size identical may stop at the node.

### 4.5 DrawList encoding (`FluentGpu.Render`)

Flat contiguous POD command stream in **double-buffered arenas**. 8-byte `DrawCmd` header
`{Op:byte, Flags:byte, PayloadSz:ushort, _:uint}` + fixed-size POD payload per opcode. **All references
are by handle/index** (brushes→`BrushTable`, clips→`ClipTable`, glyph runs→`GlyphRunTable`, paths→vertex
arena). **SortKeys live in a parallel `ulong[]` arena** (folds FA-2: contract `DrawCmd.SortKey` is 32-bit;
batcher needs 64 bits).

Opcodes: `FillRoundRectCmd`, `FillRoundRectStrokeCmd`, `DrawShadowCmd`, `DrawGlyphRunCmd`, `FillPathCmd`,
`DrawImageCmd`, `PushLayerCmd`/`PopLayerCmd`, `PushClipRectCmd`/`PopClipCmd`, `PushTransformCmd`/
`PopTransformCmd`, `PushStencilClipCmd`/`PopStencilClipCmd`, `DrawFocusRingCmd` (the production focus visual;
the rectangular `DrawFocusRect` is a superseded debug placeholder), `DrawAccessKeyBadgeCmd`.
`DrawGlyphRunCmd` references by `{GlyphRunHandle, origin, BrushHandle}` and **never bakes atlas UVs** —
UVs resolve at batch time (keeps eviction transparent).

**Incremental story (the linchpin, with invariants):** re-record dirty subtrees into the front arena;
`memcpy` clean layers' command spans from the back arena; swap at frame end → zero per-frame managed
allocation. **A memcpy'd clean span is valid IFF every handle it references is `IsLive` AND its backing
realization's content-epoch is unchanged** (folds the glyph-atlas correctness fix). `TransformDirty`-only
nodes reuse their span; the batcher applies the new `WorldTransform[node]` to the cached instanced quads
at submit (no re-record).

### 4.6 Text seam (`FluentGpu.Text`) — vocabulary

`IFontSystem / IFontFace / ITextShaper / IGlyphRasterizer / IGlyphAtlas / ITextLayoutEngine`. The shaper
fills **caller-owned spans** via `ShapedRunBuilder` (ref struct: `GlyphIds/Advances/OffsetsX/OffsetsY/
Clusters/GlyphProps`) — zero-alloc, no `string` on the paint path. `GlyphKey` (24B) is the realization-
cache key `{FontFaceHandle, glyphId, sizeQ, aaMode, subpixelPhase, gamma}`. Glyphs rasterize into a GPU
atlas (`PackedGlyph` instances).

**Contract clauses (folded from critiques):**
- **Glyph positions reaching the renderer are FINAL device-space dest rects in VISUAL order** — BiDi
  reorder, cluster mapping, mark positioning, and subpixel phase all resolved by the text seam. The
  renderer treats them as opaque positioned quads.
- **`GlyphRunRealization` carries a content-epoch**; atlas repack bumps it, forcing re-record of any clean
  span referencing the run. Glyph-run slots referenced by retained spans are refcounted/quarantined.
- **Atlas eviction runs only at frame START**; any glyph referenced by a live command (dirty OR clean) this
  frame is ineligible. A batch-time UV-resolve miss rasterizes into a reserved overflow region, never faults.
- **Color/emoji**: a separate `BGRA8` color-atlas page + `IDWriteColorGlyphRunEnumerator1` for COLR/SVG/
  bitmap emoji (the `R8` atlas is monochrome-only), or explicitly deferred and documented.

### 4.7 PAL + RHI seam — interface-only; POD across the seam

**The one `object` leak is killed (folded):** `SwapchainDesc.PresentTarget` and
`IPlatformWindow.PresentTarget` are replaced by the 8-byte POD `NativeHandle {nint Value, NativeHandleKind
Kind}`. The D3D12 leaf switches on `Kind==Hwnd`; Metal on `Kind==NsView`. No boxing, no cross-leaf
concrete-type downcast.

```csharp
// PAL (portable)
public readonly struct NativeHandle { public readonly nint Value; public readonly NativeHandleKind Kind; }
public interface IPlatformApp : IDisposable { IPlatformWindow CreateWindow(in WindowDesc d);
    IPlatformAppLoop CreateAppLoop(); IClipboard Clipboard { get; } Scale GetScaleForPoint(PointPx p);
    void PostToUiThread(nint token); }
public interface IPlatformWindow : IDisposable { NativeHandle Handle { get; } Size2 ClientSizePx { get; }
    Scale Scale { get; } bool IsOccluded { get; } bool IsMinimized { get; }
    int PumpInto(ref InputEventRing ring);   // WindowEvent + InputEvent POD drained once/frame (NOT C# events)
    void SetTitle(StringId t); void Show(); void Activate(); void RequestClose(); }
public interface IPlatformAppLoop { void RunBlocking(FrameCallback onFrame); bool PumpOnce(out bool quit);
    void RequestFrame(); void Post<TState>(TState s, Action<TState> cb); }   // struct-state overload, no closure
```

**Amendment folded (MAJOR):** **C# events replaced by a host-owned POD ring** (`InputEventRing`, slab-
backed). The window writes `InputEvent`/`WindowEvent` POD into the ring (move-coalescing the >1 kHz
`WM_INPUT`/`WM_POINTERUPDATE` flood); the host drains a `ReadOnlySpan<InputEvent>` once per frame in the
input-dispatch phase. Zero delegate/closure allocation. One stable `FrameCallback` for the loop.

```csharp
// RHI (graphics-first; zero COM/pointers; generational handles + POD descs + spans)
public interface IGpuDevice : IDisposable {
    GpuDeviceCaps Caps { get; }                 // incl. PreferredBackBufferFormat, SupportsTearing, MsaaModes
    DeviceLostToken LostToken { get; }
    ISwapchain CreateSwapchain(in SwapchainDesc d);   // d.PresentTarget is NativeHandle (POD)
    ShaderModuleHandle CreateShaderModule(in ShaderModuleDesc d);   // DXIL bytes / .metallib
    PipelineHandle CreatePipeline(in GraphicsPipelineDesc d);       // immutable PSO; hash-deduped
    BufferHandle  CreateBuffer(in BufferDesc d);  TextureHandle CreateTexture(in TextureDesc d);
    SamplerHandle CreateSampler(in SamplerDesc d);
    void Destroy(TextureHandle h); void Destroy(BufferHandle h);    // gen-bumped, deferred to fence retire
    ICommandEncoder BeginFrame(in FrameContext ctx);  void Submit(ICommandEncoder enc);
    void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameContext ctx); // PRIMARY
    void WaitIdle(); }
public interface ISwapchain : IDisposable { Size2 SizePx { get; } TextureHandle CurrentBackBuffer { get; }
    void Resize(Size2 px); PresentResult Present(in PresentParams p); }
public interface ICommandEncoder { void BeginRenderPass(in RenderPassDesc p); void EndRenderPass();
    void SetPipeline(PipelineHandle p); void SetViewportScissor(in RectPx vp, in RectPx scissor);
    void BindConstants(uint slot, ReadOnlySpan<byte> data); void BindBuffer(uint slot, BufferHandle b, uint off);
    void BindTexture(uint slot, TextureHandle t, SamplerHandle s);
    void DrawInstanced(uint vtxPerInst, uint instCount, uint baseVtx, uint baseInst);
    void DrawIndexedInstanced(uint idxCount, uint instCount, uint baseIdx, int baseVtx, uint baseInst);
    void Barrier(ReadOnlySpan<ResourceBarrier> b); void ResolveTexture(TextureHandle src, TextureHandle dst);
    void UpdateBuffer(BufferHandle b, uint dstOff, ReadOnlySpan<byte> src); }
```

**Amendments folded:**
- **`SubmitDrawList` is the PRIMARY hot path** (the leaf walks the POD opcodes with concrete, devirtualized,
  inlinable types — removes per-draw interface dispatch). The per-opcode `ICommandEncoder` is a secondary
  API for non-DrawList callers.
- **`ResolveTexture` added** (was missing); MSAA renders to an offscreen MSAA RT, resolves into the
  single-sampled back buffer **in linear space** before the PRESENT barrier.
- **`SwapchainDesc.ColorSpace` + `BackBufferFormat`** added; mandate buffer=`UNORM`, RTV=`_UNORM_SRGB`
  (`RtvDesc.Format` independent of `TextureDesc.Format` — folds the flip-model/DComp sRGB blocker).
- **Back buffer is always single-sampled** (flip-model + DComp ⇒ `SampleDesc.Count==1`).

`HandleTable<TNative>` lives in the leaf; maps each handle to `{ComPtr<...>, cached descriptors, current
`D3D12_RESOURCE_STATES`}`. `Destroy(h)` gen-bumps and pushes onto a **deferred-delete ring keyed by
in-flight fence value** (per-swapchain for DXGI-owned back buffers, which release via the swapchain, never
D3D12MA). `HandleTable.Free` runs typed teardown (`slot.Res.Dispose()`) **before** reclaiming the slot
(folds the ComPtr-leak blocker). The deferred-release ring is a required Rhi.D3D12 facility used for ring
growth, layer-RT recycle, and device-lost rebuild.

### 4.8 Frame lifecycle & threading model (13 phases)

> **⊳ Canonical threading model.** The 13-phase single-thread loop below is **build-order step 1** (`hardened-v1-plan.md` §6). The canonical shipping model is the **render-thread seam** — `hardened-v1-plan.md` §2 + [`subsystems/threading-render-seam.md`](./subsystems/threading-render-seam.md) §14 (see [`SPEC-INDEX.md`](./SPEC-INDEX.md)): insert **PUBLISH(13a)** between phases 7 and 8, move record/batch/submit/present (8–11) to the render thread reading an immutable `SceneFrame`, make DrawList arenas render-private and **≥3-deep**, and replace the bare quarantine constant with `QUARANTINE = RenderInFlightDepth`. (Amendment checklist: `hardened-v1-plan.md` §7.)

```
 1 pump           IPlatformWindow.PumpInto(ring)         — WM_*/NSEvent → InputEvent/WindowEvent POD ring
                  (read device-lost reason flag ONCE here, before any RHI call)
 2 input dispatch InputDispatcher.Drain(ring)            — hit-test + tunnel/bubble + focus + gestures + accel
                  (handlers' setState queued for N+1 via _isRendering guard; UIA mutations posted here too)
 3 hook flush     apply queued setState
 4 render         Component.Render() → Element            (per dirty component, tree pre-order)
 5 reconcile      keyed-LIS diff → ISceneBackend writes Scene columns (structural sub-phase, then edit)
 6 layout         FlexLayout.Run / Grid (dirty-scoped)    — measure+arrange in one descent; pixel-snap
 6.5 layout-effects  UseLayoutEffect cleanups+effects     — Bounds[] valid; setState here ⇒ mark dirty + N+1
 7 animation      timelines write LocalTransform/Opacity  — marks Transform/PaintDirty, NEVER LayoutDirty
 8 record         walk tree → DrawList POD (front arena)  — clean spans memcpy'd from back arena
 9 batch          radix-sort by ulong SortKey → instanced quad batches (overlap-aware, see §5.2)
10 submit         IGpuDevice.SubmitDrawList → ExecuteCommandLists → Signal(fence)
11 present        wait latency waitable (MsgWait...), Present(syncInterval, flags), DComp Commit if dirty
12 passive-effects UseEffect cleanups+effects             — AFTER present (React commit); setState ⇒ N+1
13 arena swap     swap front/back DrawList arenas; Reset per-frame arenas; drain deferred-free
```

**Effect-timing decision (folds the BLOCKER; labeled as a deliberate divergence, NOT "verbatim Reactor"):**
Reactor flushes effects synchronously, interleaved per-component, before layout/paint. We adopt **React-
commit semantics**: `UseLayoutEffect` → phase 6.5 (after layout, before present, so a focus/scroll-into-view
effect reads valid `Bounds`); `UseEffect` → phase 12 (after present). **A `setState` in either effect marks
dirty and requests frame N+1** (same as the passive path) — there is **no synchronous bounded re-loop** in
v1 (this eliminates the EffectScheduler mid-drain mutation hazard and the unmount-during-reloop handle-
recycle race). The `_isRendering` re-entrancy guard is re-derived for this control flow; per-cell try/catch
around effect bodies is an explicit **improvement** over Reactor (which lets them propagate). Hook list
mutation is forbidden between effect enqueue (phase 5) and drain (6.5/12); `EffectRef` carries a
RenderContext generation stamp and skips stale entries.

**Threading:** single UI thread; `SceneStore` is lockless by **confinement** (only the reconciler mutates,
on the UI thread; off-thread `setState` auto-marshals via `IPlatformAppLoop.Post`). The future render-thread
seam publishes an immutable `SceneFrame` (front DrawList byte span + retained handle tables + damage rects);
a **slot-reuse quarantine** constant (0 in v1, 2 for render-thread) parks freed slots so the consumer can't
observe a reallocated slot. UIA/TSF callbacks from the OS are marshaled onto the UI thread via
`ProviderOptions.UseComThreading` + the HWND pump (§5.8).

---

## 5. SUBSYSTEM SPECS

### 5.1 PAL / RHI / Windowing + Present (Pal.Windows + Rhi.D3D12)

**Window (Pal.Windows):** `RegisterClassExW` once (own redraw via DXGI; `CS_HREDRAW|CS_VREDRAW` off),
`CreateWindowExW`, `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` (manifest-free). Static
`[UnmanagedCallersOnly]` `WndProc` registered in `WNDCLASSEXW.lpfnWndProc`, dispatching via a `GCHandle`
in `GWLP_USERDATA` (Normal handle, lifetime = window). DIP→px conversion happens **once** at the pump
boundary using the window's current effective DPI; `Scale` is documented as the post-`WM_DPICHANGED`
effective DPI (PerMonitorV2 snaps a straddling window to one value; `Scale` stays a struct so a future
`DpiX/DpiY` extension is non-breaking).

**Device + present creation:** `D3D12CreateDevice(adapter, FL_11_0)` → DIRECT `ID3D12CommandQueue` →
`ID3D12Fence` (+ device-lost `fence→MaxValue` + `RegisterWaitForSingleObject`) → D3D12MA factory → RTV/DSV
heaps + shader-visible CBV/SRV/UAV heap. Swapchain via **`IDXGIFactory2.CreateSwapChainForComposition`**
(queue arg, `B8G8R8A8_UNORM`, `BufferCount=2`, **`FLIP_DISCARD`** [folded: preferred over `FLIP_SEQUENTIAL`
for full-frame UI], `PREMULTIPLIED`, `STRETCH`, `FRAME_LATENCY_WAITABLE|ALLOW_TEARING`) → QI
`IDXGISwapChain3` → `DCompositionCreateDevice(&IDCompositionDesktopDevice)` →
`CreateTargetForHwnd(HWND)` → `CreateVisual` → `visual.SetContent(swapchain)` → `target.SetRoot(visual)` →
`Commit()`. Back-buffer RTVs created as **`B8G8R8A8_UNORM_SRGB`**.

**Per-frame present (zero-alloc):** `MsgWaitForMultipleObjectsEx(latencyWaitable, QS_ALLINPUT)` (folded:
**not** blocking `WaitForSingleObjectEx` — wakes on the waitable OR an incoming message, keeping the pump
responsive in single-thread v1; during `WM_ENTERSIZEMOVE` the modal loop owns the pump and we render via
`WM_PAINT`/timer). Then: get back-buffer index → `SubmitDrawList` records into a pooled command list from a
DIRECT-typed pool (barrier PRESENT→RT, render pass, barrier RT→PRESENT) → `ExecuteCommandLists` →
`Signal(fence)` → `Present(syncInterval, flags)` → `DComp Commit()` **only when a visual prop changed**.
Occlusion: `Present(... DXGI_PRESENT_TEST)` → throttle to ~1 Hz test-present (marked **needs-validation**
against DComp present-test semantics; fall back to `WM_ACTIVATEAPP` heuristics if unreliable).

**Resize:** on `WM_EXITSIZEMOVE` (live drag) / `WM_SIZE` (programmatic): **CPU-wait only the in-flight fence
values that referenced THIS swapchain's back buffers** (folded: **not** device-wide `WaitIdle` — other
windows on the shared device keep rendering) → gen-bump back-buffer handles → `ResizeBuffers` → re-`GetBuffer`/
re-create RTVs → DComp `Commit` reasserts size. During live drag, present-stretch for smoothness, snap on
exit.

**Multi-window:** one `IGpuDevice` (one device/queue/heaps/D3D12MA) shared; each window = one `ISwapchain`
+ DComp `Target`/`Visual`. Per-window fence values + per-swapchain deferred-delete ring.

**Device-lost (folds the threading BLOCKER):** the `RegisterWaitForSingleObject` callback runs on an **OS
wait thread**, not the UI thread. It does **only** `Volatile.Write(reasonCode)` + `RequestFrame()` — it
**never** touches a `ComPtr` or calls `GetDeviceRemovedReason`. The UI thread reads the flag **once** at
phase 1. **Primary detection is the synchronous `Present()`/`Submit()` HRESULT**; the async wait is a hang
backstop. GCHandle for the device-lost context is **Normal** (tied to Hosting lifetime), freed
deterministically on teardown (no `Weak`/null-target race). Recovery: dispose device → recreate (next-best
adapter / WARP) → recreate per-window swapchains → **re-realize GPU resources from retained CPU state**
(SceneStore SoA untouched; BrushTable/ClipTable re-upload; GlyphAtlas re-rasterize from GlyphRunTable; PSOs
recompile from cached DXIL blobs) → mark whole tree dirty. Invariant: **the RHI stores only realizations;
every GPU object is reconstructible from CPU-side retained data.**

**Fork scope (stated honestly):** Rhi.D3D12 hand-authors against the verified d3d12.h ABI (each vtable index
checked): **Device** += `CreateGraphicsPipelineState`, `CreateRenderTargetView`, `CreateDepthStencilView`;
**GraphicsCommandList** += `OMSetRenderTargets`, `RSSetViewports`, `RSSetScissorRects`, `IASetVertexBuffers`,
`IASetIndexBuffer`, `ClearRenderTargetView`, `ClearDepthStencilView`; ~30–40 graphics structs/enums
(`D3D12_GRAPHICS_PIPELINE_STATE_DESC`, `INPUT_ELEMENT_DESC`, `RASTERIZER_DESC`, `BLEND_DESC`, RTV/DSV descs,
`SHADER_BYTECODE`, `VIEWPORT`, `RECT`, …); **`Present1` + `DXGI_PRESENT_PARAMETERS`** (not in seed); the
**entire DComp binding set**. This is the single largest implementation item.

**Cross-platform boundary:** above the seam sees `Size2/Scale/PointPx`, `NativeHandle` (opaque), `InputEvent`
(POD), DrawList (POD), generational handles, POD RHI descs, `ISwapchain.Present`. It **never** sees
`HWND/HRESULT/ComPtr/ID3D12*/IDXGI*/IDComposition*/WM_*/DXGI_*`. Metal mapping: `CAMetalLayer` ≙ DComp
visual-with-swapchain; `id<CAMetalDrawable>` ≙ `CurrentBackBuffer`; `MTLRenderPassDescriptor` ≙
`RenderPassDesc`.

### 5.2 GPU 2D Rendering Engine (Render — portable; Rhi.D3D12 executes)

**Primitives & AA:** analytic **SDF AA for the rect family** (rounded rects, borders) — `length(float2(ddx(sd),
ddy(sd)))` true gradient magnitude (folded: not `fwidth` L1) so rotated content is correct. **Glyphs use
pre-rasterized atlas coverage**, not SDF (§4 header corrected). **Drop shadows = closed-form rounded-box
Gaussian (erf, A&S 7.1.26 approx, sigma floored at 0.5px) in ONE instanced quad** — **valid for UNIFORM
corner radius only** (folded); per-corner-radius and arbitrary paths route to the `IEffectRunner` offscreen
blur fallback. The übershader has 3 PSO variants (compile-time `#define KIND`, switched at batch granularity,
no per-fragment branch).

**Paths:** zero-alloc CPU tessellator into an arena, AA-fringe (feather), MSAA off; cached by geometry×scale
hash in a retained slab; flows through the same instanced batch path. **Hit-test shares the fill RULE
(nonzero winding default), not just the vertices** (folds the input-a11y fix).

**Batching & SortKey (folds the painter-order BLOCKER):** 64-bit SortKey, but the **primary key is a
monotonic paint-order sequence (tree pre-order emit index)** for translucent content — `PassClass` is
demoted **below** it. Opaque primitives may be freely reordered/coalesced; **translucent primitives must
respect submission order where they overlap**. An **overlap-aware batch break** maintains a per-layer cheap
occupancy structure (bounding-interval list / coarse tile grid over expanded device bounds) and breaks the
batch when a later differently-pipelined translucent primitive overlaps an un-flushed earlier one. Intra-node
`shadow→fill→border→glyph` ordering is kept (safe within one widget). Hand-written stable LSD radix sort over
`ulong[]` (no `Array.Sort`+comparer delegate). The "3–5× fewer batches" claim is revised down but correct.

**Color pipeline (folds the sRGB BLOCKER, ties to §4.7):** swapchain buffer `BGRA8_UNorm`; back-buffer
**RTV `BGRA8_UNorm_SRGB`**; blend + MSAA-resolve in linear; renderer outputs **premultiplied linear-alpha**
for DComp `PREMULTIPLIED`. Brush colors realized to linear-premul on the CPU once; gradients baked into a
shared `RGBA16F` gradient-texture atlas so all gradients share one bind and batch together. Layer RTs
(engine-owned, not flip-model) may be `*_UNorm_SRGB` resources directly.

**Text blend (deliberate exception to linear-blend, folded):** glyph coverage is blended in a gamma/
perceptual space with a DWrite-style gamma + enhanced-contrast curve (a per-target `gamma`/contrast constant
in `GlyphKey`), A/B-validated against native DWrite/WinUI text. Grayscale-only v1; ClearType (2nd dual-source
PSO, opaque-only, transform-breaking) deferred to v2 (`GlyphAaMode` flag reserved, one glyph PSO provisioned).

**3-tier clip:** scissor (AA rects, free, no batch break) → SDF uniform (rounded, ~free) → **stencil
(arbitrary paths only)**. Stencil sub-protocol (folded): a sample-count-matched DSV resource; clip mask
written in a dedicated pre-pass emitted as a non-reorderable `PushStencilClipCmd`/`PopStencilClipCmd` pass
boundary; nested clips via `INCR_SAT`/`DECR_SAT` with a documented max depth.

**Layers** are the only offscreen RTs (group opacity over overlaps, non-SrcOver blend, effects), pooled by
size bucket from D3D12MA via the deferred-release queue.

**Partial present (folds the MAJOR):** v1 = **engine-owned persistent canvas RT** — damaged regions
scissor-repainted into it with `LoadOp.Load` (valid because WE own the RT, unlike a `FLIP_DISCARD` back
buffer), then DComp-composited / copied to the back buffer. `Present1` dirty-rects are a pure DWM hint
layered on top, **not** the correctness mechanism. **Damage** (folds MAJOR): dirty rects select which region
to repaint; repaint includes **all nodes intersecting that region in z-order** (not just the dirty node);
each node's damage is inflated by its effect extent (shadow blur radius, backdrop margin); world AABB is
computed from **all four transformed corners** (handles rotation/skew); >16 rects collapse to full-frame;
world-space float damage converts to integer back-buffer pixels **at the RHI leaf, rounding OUT** (DPI
applied once, on the Windows side).

**Zero-alloc:** persistently-mapped UPLOAD ring (geometric 2× growth, old ring deferred-freed behind its
fence; re-record capped once/frame then multi-pass), arena-backed recorder/batcher/sort scratch, retained
slab tables for PSO/RTV/textures. Per-frame managed allocations: **0**.

**Shaders:** authored HLSL + `shapes_common.hlsli`, DXC → DXIL (now) / `-spirv` → SPIRV-Cross → MSL (later),
embedded as source-gen'd `byte[]`.

**Root-signature/binding contract (cross-agent, with RHI):** one shared root signature — root constants for
per-draw, one descriptor table for the glyph atlas + brush/gradient textures — baked into every PSO; maps to
Metal argument buffers later.

### 5.3 Text & Glyph Subsystem (Text iface; Text.DirectWrite leaf)

**Seam:** as §4.6. **DWrite leaf:** ~25 hand-authored vtbl structs against **`IDWriteFactory2+`** (folded:
pinned for `IDWriteFontFallback` + `IDWriteFontFace2` color support). **Callee-side CCW** (DWrite calls back
into our `IDWriteTextAnalysisSource`/`Sink`) via static `[UnmanagedCallersOnly(CallConvMemberFunction)]`
vtables + a pooled pinned `GCHandle` for the analyze-call duration — **no ComWrappers** (no template exists;
the hand-vtable pattern is the AOT-clean way). **CCW hardening (folded):** during `Analyze*`, pin **both**
the source-state struct **and** the source UTF-16 `char[]` (`GetTextAtPosition` returns a live `WCHAR*`);
`QueryInterface` returns `S_OK` only for `IUnknown`+`IDWriteTextAnalysisSource`, else `E_NOINTERFACE`;
`AddRef`/`Release` no-op return 1 (synchronous, single-threaded, DWrite doesn't retain). The same CCW
machinery serves the `GetGlyphRunOutline` geometry sink (oversized-glyph fallback). Prefer the
`IDWriteGlyphRunAnalysis::CreateAlphaTexture` path to minimize callbacks.

**Itemization → shaping pipeline (folds the BiDi BLOCKER):** **itemize (BiDi+script+linebreak) → font-
fallback segment → per-segment `GetGlyphs`/`GetGlyphPlacements`**. One shaped run = single face + single
script + single direction. **L1 shaped-run cache key includes itemization context:** `(text-span hash,
resolved-post-fallback face, sizeQ, script tag, resolved BiDi level parity, locale StringId, feature-set id)`.
A node's text may produce **multiple visual runs** (BiDi-split), not one run per `StringId`.

**Two-level cache:** L1 shaped-run (constraint-free) + L2 layout/wrap (constraint-bearing) ⇒ resize re-wraps
without re-shaping. **Quantize `MaxWidth` UP to an integer device-pixel grid BEFORE it enters Yoga** so
Yoga's 8-entry ring sees stable keys; document drag-resize cost = O(glyphs) re-wrap, never re-shape. Honest
claim: **one SHAPE per content; re-WRAP on constraint change**.

**Line-breaking (folds the correctness fix):** **not** greedy-break on raw advances. Either push line-
breaking behind `ITextShaper` (DWrite analyzer/`IDWriteTextLayout`) or implement a BiDi-level-run +
cluster-boundary breaker in a portable `FluentGpu.Text.Unicode` — **deferred to the CoreText milestone**;
v1 Windows uses DWrite `Analyze*`. Unicode tables budgeted honestly (~100KB+ trimmed, UAX #9/#14/#24
conformance-tested).

**ShapedRunCache → Render bridge (folds the MAJOR):** entries stamped with the layout `GenerationCount` + a
**`Committed`** bit set only at `CommitBounds` (Render reads only committed runs); eviction tied to node
handle/generation with an LRU cap.

**MeasureFunc (folds the BLOCKER):** **one `static readonly YogaMeasureFunc s_textMeasure = MeasureTextStatic`**
set once at node creation; `MeasureTextStatic` recovers the `TextLayoutSlot` via a generational handle in
the YogaNode context slot — **never** a captured closure. (Amendment: `LayoutNode` exposes a handle
user-data slot — granted in §5.5.)

**Atlas:** `R8_UNORM` grayscale pages + a separate `BGRA8` color page for emoji; skyline bottom-left packer
over `Span<SkyNode>`; eviction at frame START with the live-reference pin (§4.6).

**Editing scope:** **v1 = display-only text.** Editable text + IME + the caret/selection model
(`HitTestPoint→(textPos,isTrailing)`, `HitTestTextPosition→caretRect`, `GetSelectionRects(start,len)→
ReadOnlySpan<RectF>` with multiple rects across BiDi runs, UAX #29 grapheme navigation) is a named follow-up
milestone leaning on the existing cluster map.

**Cross-platform:** ~70% portable (atlas, both caches, wrap/align/trim, hit-testing, emission). macOS
implements `CTFontCollection`/`CTFontCreateForString` + `CTFontGetGlyphsForCharacters` + `CTFontDrawGlyphs`→
`CGBitmapContext`. `TextBlockPayload` holds the source `char[]` at the `Element` edge, exposing a stable,
**pinnable** `ReadOnlySpan<char>` for the frame (the one legitimate edge GC ref).

### 5.4 Scene / Memory (Scene SoA + Foundation primitives)

As §4.1–4.5. **Mutation API** is `ref`-return + handle-based via `ISceneBackend`; `PaintScope`/`EditPaint`
diff-on-Dispose marks dirty only on a real delta (mirrors Reactor's `CanSkipUpdate`/`ShallowEquals` short-
circuit) — an unchanged node contributes zero damage and its DrawList span is `memcpy`'d clean. Slot
recycling (the slab free list) satisfies steady-state mount churn with **zero array growth**; the managed
`Component`/`RenderContext` recycle through `ObjectPool` (cap 32, explicit `Reset`). **No WinUI
`ElementPool.ForceDetach` COM-detach pain** — `FreeNode` is a free-list push. Deferred-free + `Detached`
flag keep a node alive for exit animations; cascade-drained if the parent frees first.

**Damage** (§5.2 algorithm) reads `Flags[]`+`Bounds[]`+composed `WorldTransform[]` (computed once top-down in
the animation phase); old∪new world AABBs from four transformed corners; `prev` `FrameCache` retains last
frame's `WorldBounds[]`/`SubtreeWorldBounds[]` (double-buffered, not per-frame allocated).

**`HandleTable<TResource>` ownership (§4.7):** typed `Free` Disposes the `ComPtr` before slot reclaim;
pointer-bearing slabs use a side `int[] _next` free-link (no intrusive trick). `Affine2D` is 2×3 affine
(2.5D/perspective explicitly out of scope for v1).

### 5.5 Layout Engine (Layout — fully portable)

**Decision:** **port the Yoga algorithm body verbatim; rewrite the substrate.** Single-pass Yoga (no WinUI
two-pass Measure/Arrange — eliminates the `FlexPanel` `_arranging`/`LayoutCycleException`/`Dictionary<UIElement,
YogaNode>` machinery). Results land in `SceneStore.Bounds[i]` (LOCAL space, P8). Text intrinsic measure is
the **only** external call (`ITextShaper`); DPI arrives as a plain `float scale`. **Zero Windows/GPU
dependency** — the cleanest seam in the system.

**Substrate substitutions (the keystone):** `YogaNode` class → **`ref struct LayoutNode`** (16B stack view
over SoA columns; algorithm's `node.Style.X`/`node.Layout.SetY` map to inlinable column accessors).
`YogaStyle`+`LayoutResults` classes → `LayoutInput` (96B hot column, exact byte layout asserted via
`Unsafe.SizeOf`; `InlineArray` `Edges4`) + `LayoutCacheEntry` slab struct (**includes `RawDim`** — the
seventh `_rawDimensions` array the original carries; `Ring8` `InlineArray` preserves the 8-entry
`CachedMeasurement` ring + `CachedLayout` + generation/config-version invalidation, field-complete vs
`LayoutResults`). `List<YogaNode>` + `[ThreadStatic] Stack<>` pool → arena `Span<int>` child lists +
`ref struct FlexLine`. `IEnumerable GetLayoutChildren()` iterators → index loops over `FirstChild/
NextSibling`. `YogaMeasureFunc` delegate-per-node → central `MeasureKind` dispatcher keyed by `VisualKind`.

**Scratch arena invariant (folds the BLOCKER):** `LayoutScratch` is a **pre-sized, NON-relocating** bump
region for the duration of one `FlexLayout.Run` (sized at pass entry from live-node count; grown only
*between* passes, never during). Child lists held up the recursion spine stay valid; stored as `(offset,len)`
into the stable buffer (a defensive grow is detectable/forbidden); a DEBUG assert checks the base pointer is
unchanged across the descent.

**Numeric parity (folded):** `LengthValue.Resolve` uses `/ 100.0f` (matches source bit-for-bit); `default
(LengthValue)` unambiguously means Undefined (every ported NaN-on-`Value` check switched to test
`Unit==Undefined`); generation counter detects wrap (epoch sweep) rather than calling 2^32 unreachable.
`MaxLayoutDepth=256` cap kept; reconciler validates topology cycle-free.

**Incremental layout:** Yoga generation-counter cache (single-thread, plain `uint`, no `Interlocked`) +
`NodeFlags` dirty scope. Layout cost ∝ dirty subtree. The **"measured-size-unchanged ⇒ stop" rule applies
only to intrinsic dirt** (§4.4 taxonomy). **Pixel-snap is incremental too (folds MAJOR):** per-node
`absLeft/absTop` cached in `LayoutCacheEntry`; re-snap only the changed-position subtree + moved later-
siblings, seeded from the parent's committed snapped origin; the actual nearest-rounding expression and the
text `hasFractionalWidth` right/bottom ceil/floor coupling are reproduced verbatim; golden parity tests at
scale 1.25/1.5/1.75.

**Containing-block model (folds MAJOR):** `FormsContainingBlock(int node)` predicate (PositionType≠Static ∨
transform/filter ∨ `AlwaysFormsContainingBlock`) drives the ported `LayoutAbsoluteDescendants` deep recursion
with accumulated left/top offsets (real ported routine, not a flattened loop).

**Scroll (folds MAJOR):** layout arranges content at the **content-box origin** and publishes `ContentSize`;
the **`-ScrollOffset` translation is the viewport-child `LocalTransform`** set by Input/Animation, marking
`TransformDirty/PaintDirty` only — so scrolling is layout-free. `ScrollOffset`/`ContentSize` ownership pinned
(Input owns offset; Layout writes content size; Render reads the clip).

**Grid:** a distinct algorithm `FluentGpu.Layout.Grid` (true tracks/spanning/star, Auto/Pixel/Star), **not**
nested-flex faking; shares `LayoutNode`/`LayoutCache`/snap/text-measure/`Bounds`. `GridDefinition` is a
**struct** with arena/slab-backed `TrackDef` storage (folded: not a `class` in `SlabAllocator<T:unmanaged>`);
removed from the "value-typed instantiation" list correctly. Budgeted as a separate deliverable with its own
correctness surface. Stack/VStack = trivial flex projection (zero new engine code).

**Custom measure callbacks (folded):** stored in an `ObjectPool`/delegate-array indexed by `PayloadRef`
(not `SlabAllocator<unmanaged>` — a delegate is managed); invocation is reentrancy-guarded (no tree mutation
during measure; only `(availW,wMode,availH,hMode)` struct in/out).

**Zero-alloc:** per-frame layout managed allocations = **0** (all scratch arena-backed; all stable state slab
columns).

### 5.6 Reconciler + Hooks (Reconciler — portable, PORT + re-author)

**Thesis:** keep React's programming model and Reactor's diffing control flow; change the patch target
(`UIElement`→`NodeHandle` via `ISceneBackend`), node→component bookkeeping (`Dictionary`→`ComponentSlot`
SoA column), deps (`params object[]`→`ReadOnlySpan<DepKey>`), and effect timing (§4.8).

**`ISceneBackend`** (renames `IRenderBackend`): handle-in/handle-out, POD-only — `CreateNode/FreeNode`,
`AppendChild/InsertBefore/Move/Detach`, mask-based `WriteVisual/Layout/Text/Payload`, `MarkDirty`, `SetKey/
GetKey`, `SetComponentSlot/GetComponentSlot`, `EditPaint/EditLayout` (capture+revalidate on Dispose),
`Children/FirstChild/NextSibling`. Zero COM, zero GC-ref-into-pool. **`NodeChildCollection` implements the
full `IChildCollection` incl. `RemoveAt(int)`** (folded — was omitted).

**Keyed reconcile (folds MAJOR — re-authored, NOT "byte-for-byte"):** the portable core is the 4-phase
structure (prefix/suffix/middle) + the LIS idea + `KeyMatch`/`GetKey` semantics. `ReconcileKeyedMiddle` is
**re-authored** to read keys via `backend.GetKey(childHandle)` (not WinUI `GetElementTag(fe)`) and to use
**arena scratch**: keys interned to `StringId` up front; the two `Dictionary<string,int>` → arena open-
addressing probe span; `int[]/bool[]/List<int>` → `arena.AllocSpan<int>/<byte>`; `HashSet<int> inLIS` →
`Span<bool>` bitset. `ComputeLIS` re-authored to fill caller-provided `Span<int>` scratch (tails/tailIdx/pred)
and return the in-LIS set as a bitset — **zero managed alloc per reorder**. `Filter` → arena index-map (no
element copy). All `FrameworkElement`/`GetElementTag`/`AnimationAmbient`/Composition calls stripped;
structural enter/move/exit animations re-routed through the Animation subsystem's handle-based API. The
doubly-linked sibling `Move` (O(1) splice) is the genuine SceneStore-side win.

**Key integer space (folds the collision fix):** synthesized positional keys are discriminated from interned
`StringId` keys (reserve a high bit, or route positional fallbacks through the same `StringId` table) so the
`Dictionary<int,int>` cannot collide the two spaces.

**Hooks:** `List<HookCell>` kept (mount-only edge alloc). **`DepKey`** is a **pure-scalar blittable 16-byte
struct** (folds the BLOCKER — the `[FieldOffset]` union of a GC ref with scalar bits is illegal CLR layout
and would `TypeLoadException`):

```csharp
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct DepKey : IEquatable<DepKey> {
    public readonly DepTag Tag; private readonly uint _pad; public readonly ulong Bits;
    public bool Equals(DepKey o) => Tag == o.Tag && Bits == o.Bits;
}
```

Reference/delegate deps are **NOT** stored inline — the generator wraps them to an interned `StringId`/
`Handle` identity, **OR** the effect/memo cell carries a small managed `object?[]`/`Delegate?[]` side-buffer
(parallel to the inline `DepDeps`) compared via `ReferenceEquals` (folds the lifetime bug — a moving-GC
pointer-as-token and cross-frame ReferenceEquals against a reset table is unimplementable). Persistent
prev-deps live in the hook cell as an inline `DepDeps` (`[InlineArray(4)]` for value deps + the side-buffer
for refs), **never** a stack-only `ReadOnlySpan`. The source generator lowers `UseEffect(fn, deps...)` to a
**fixed small-N capture** (≤4 `unmanaged` type params, funneled through a non-generic `DepKey.From`) — this
generic-overload path is **PRIMARY** (it is what ComputeSharp-class AOT actually relies on); **C#
interceptors are demoted to experimental/opt-in** (`stackalloc DepKey[N]` capped ≤8/128B, never inside a
loop body — arena-rent above the cap; interceptors gated behind a build switch defaulting OFF, using the
opaque versioned `InterceptsLocation` form if ever used).

**`ComponentTable`** = `SlabAllocator<ComponentSlotData>` (unmanaged: `HostNode`, `MemoDeps`, `SelfTriggered`,
gen) + parallel edge arrays (`Component?[]`, `RenderContext[]`). O(1) `ComponentSlot` column read.
**Previous-Element retention (folds MAJOR):** a per-host-node side array (indexed by `ComponentSlot`) holds
the previous rendered `Element` subtree for **all container nodes** (not just component hosts) so the keyed
diff has its `oldChildren` source; released on unmount (edge GC retention — allowed, named).

**`ComponentAnchor` (folds the inflation fix):** anchor nodes carry `NodeFlags.Passthrough` and are skipped
by layout, paint, and the ChildReconciler child snapshot (an arena `Span<NodeHandle>` taken once per parent
reconcile, O(n), collapsing anchors). Resolves the `ChildAt` O(index) concern.

**Preserved verbatim (control flow):** self-trigger/memo skip; `t_rerenderDepth` re-entrancy cap (50);
error-boundary try/catch + fallback into the same host node; context shadow-stack (`ContextCell` stores
**either** a `DepKey` for value contexts **or** an `object` ref for reference contexts — discriminated, never
both, so value-context change-checks are boxless on both store and compare); hot-reload (`ResetForHotReload`/
`MigrateHooksForHotReload` — AOT-dead via `MetadataUpdater.IsSupported`; value-carrying cell field stays named
`Value` and the `ValueHookState<>`/`MemoHookState<>`/`PersistedHookState<>` generic shapes intact so the
reflection-by-field-name copier still matches). `SubtreeDirty` upward propagation makes Reactor's
`_dirtyAncestorPath` `HashSet` free.

**Coalescer + priority** ported from `ReactorHost` (`_isRendering` guard + `Interlocked.CompareExchange` gate
+ low-priority re-enqueue + `RenderPriorityPolicy` 16ms budget); transport `DispatcherQueue`→
`IPlatformAppLoop`. Off-thread `setState` marshals via `IPlatformAppLoop.Post<TState>(state, staticCallback)`
(struct-state overload — no closure box; the rare off-thread path is documented as a tolerable small alloc if
the closure form is ever used).

**Cross-platform:** the entire subsystem is portable C# over `ISceneBackend` + `Element` records + PAL
`IPlatformAppLoop` — zero Windows/D3D/`ComPtr`. The macOS port ships it unchanged.

### 5.7 DSL + AOT Toolchain + Source Generators (Dsl + FluentGpu.SourceGen)

**Element stays an immutable record at the edge** (preserves null children, `?:` ternaries, value-equality
diff). **Modifier accumulation re-architected** (Reactor's `el with { Modifiers = el.Modifiers.Merge(mods) }`
is O(nodes×modifiers) record copies): modifiers bump-allocated as 16-byte `ModifierOp`s; `Element` holds an
8-byte `ModifierRef {start,count,arenaEpoch}`. **The primary mechanism is the `[FastPath]` stack-bound
`readonly ref struct` builder that carries the arena cursor explicitly** (folds two issues: no `[ThreadStatic]`
global, no fragile tail-detection heuristic); the record `with` is the **materialization step** at the
container boundary (and the only path for `?:`/null). The per-render arena is passed **explicitly through the
RenderContext** (a `readonly ref struct`), not a `[ThreadStatic]`. Allocation accounting assumes slow-copy is
common, not rare.

**Stashed-Element modifiers are durable (folds the silent-drop footgun, now mandatory):** because the
reconciler holds prev-frame `Element` records by construction (§4.5), their `ModifierRef`s point at a reset
arena every frame — so the reconciler **re-homes** `ModifierOps` into a stable `SlabAllocator` segment when an
Element is retained across frames. Never silently dropped; a `NodeFlags.StaleModifiers` DEBUG assert path
guards regressions.

**Concrete type preserved** via shared-generic `T : Element` extensions funneling to one non-generic core
(reference-`T` shared generics avoid AOT explosion). The generator emits a covariant-return `WithModifiers`
per element to drop the `(T)` cast where possible; where the cast remains it is type-safe but **not**
"identity-preserving / no-op" — it is one record copy of the same sealed runtime type (corrected framing).

**`ChildList` backing = managed `Element[]` chunks** (children are GC refs — folds the arena-type confusion),
not the unmanaged byte arena. `params ReadOnlySpan<Element>` for the static case; an explicit
`Children(IReadOnlyList<Element>)` API for data-bound lists (one edge alloc, reconcile-only).

**Scene-writing generated OUT of Dsl** (folds the layering BLOCKER): `Element` carries data only; the
generated per-element column writer is a `switch(ElementTypeId)` in the Reconciler/backend, or writes a
Foundation `NodePaintSpec` POD. `Dsl` references only `Foundation`.

**Theme resolution through `RenderContext`** (folds the ambient-global leak): baked `ReadOnlySpan<byte>`
blobs back Light/Dark; **HighContrast routes through a PAL `ISystemColors` seam by default** (per-window
theming; HC must come from live OS system colors). `WGPU0008` (missing token) = Error for Light/Dark, Warning
for HC.

**Six generators in one portable netstandard2.0 analyzer DLL** (`FluentGpu.SourceGen`: Element/Modifier/
Diff/Theme/HookDeps/ElementTypeId) + the **separate** `FluentGpu.Interop.SourceGen` (COM bindings, leaf-only).
`WGPU####` analyzer + codefixer modeled on ComputeSharp's `CMPS####` discipline; **`WGPU0005` (rules-of-hooks)
is a heuristic Warning** (full static enforcement is undecidable — flag hooks syntactically inside `if/for/
while/&&/?:`/after a conditional return), with the runtime `HookOrderException` authoritative.

**The no-reflection bitmask `DiffProps` generator** emits idiomatic code (grounded in Reactor's hand-written
`TextBlockElement.DiffProps`). **Footprint budget reframed** as a ratcheted CI target (start ~8 MB, tighten as
the renderer lands; validate authored DWrite/DComp vtbl IL against a spike before pinning per-leaf figures);
`sizoscope`/`.mstat` gate kept; `[SkipLocalsInit]`, size-tuned `Directory.Build.props`.

### 5.8 Input, Focus, IME, Hit-Testing, Accessibility (Input portable; Pal.Windows + Accessibility.Uia leaves)

**Input is 100% portable, COM-free** — operates on `SceneStore` + `InputEvent`. TSF/UIA/OLE confined to
`Pal.Windows`/`Accessibility` leaves via seam interfaces (`IImeSession`/`IClipboard`/`IA11yBackend`/
`IDragDropBackend`), referenced only by `Hosting`.

**Hit-testing** (folds the coordinate-convention fix, P8): reverse-z descent over topology; `ptLocal =
Inverse(LocalTransform[n]) * ptParent`; clip/self-test against **node-LOCAL** `Bounds[n]`. `ReverseChildren`
via `LastChild → PrevSibling` (O(children), zero alloc). Shape-accurate self-test: Rect / SDF-RoundRect /
Ellipse / **Path with the paint's fill rule (nonzero winding default)** + bbox/grid acceleration for hot
complex paths. Route captured into a **stackalloc `Span<NodeHandle>`** during descent. **One shared forward/
inverse transform-accumulation helper** serves hit-test, UIA `get_BoundingRectangle`, and IME caret
positioning (all three must compose ancestor transform + clip). Hover cache invalidated on **any** topology/
z-order/overlay-push change (folds the stuck-hover fix), not just layout commits.

**Dispatch (folds the ref-struct/delegate BLOCKER):** args are `ref struct PointerEventArgs`/`KeyEventArgs`
(zero box); handlers stored as **named delegates taking args by ref** (`delegate void PointerHandler(ref
PointerEventArgs e)` in `PointerHandler?[]`) — **not** `Action<TRefStruct>` (illegal C#). `HandlerMask`
bitfield folded into `NodeFlags` pre-filters non-interactive subtrees (one `Flags[]` load + one mask test).
Tunnel = forward span, bubble = reversed span, common-ancestor enter/leave diff over the same span. `OnClick`
→ `Tapped` ∪ Space/Enter ∪ UIA Invoke (one declaration, three modalities). Input runs in phases 1–2 against
the **committed previous-frame Scene**; handler `setState` queued to N+1 via `_isRendering`.

**DSL framing corrected:** this is a **shape-compatible PORT**, not "verbatim" — Reactor's
`ElementModifiers.OnPointerPressed` is `Action<object, WinRT.PointerRoutedEventArgs>` (leaks WinRT, violates
the no-WinRT constraint), so handler **signatures are redefined portably**; the `OnX`/`.X()` **naming** is
preserved. Gesture structs re-typed off `Windows.Foundation.Point` to `Vec2`; the per-pointer FSM +
**from-scratch inertia integrator** (fling velocity from a sample window + friction-decay extrapolation, run
in the animation phase) are real work (WinUI's `ManipulationInertiaStarting` is gone).

**Focus:** `FocusEngine` (portable). **Tab order pinned (folds the open '?'):** bucket A = positive `TabIndex`
ascending then doc order; bucket B = `TabIndex==0`/default in pre-order; B follows A; negative = focusable
programmatically, skipped by Tab. XYFocus = projection-based candidate scoring over `Bounds` (gamepad
DPad→arrows, A→Invoke, B→Escape). Focus scopes/trapping via `_scopeStack` (modal push/pop, restore prior
focus). **Focus visual** = synthesized `DrawFocusRingCmd` (shape+raster owned by `gpu-renderer.md`; the
rectangular `DrawFocusRect` is a superseded placeholder) **anchored to the focused node's clip chain** (so
it clips/scrolls correctly), its overlay layer marked dirty on focus move so the incremental DrawList
re-records it. `UseFocus`/`UseElementRef` keep names/return-shapes, reimplemented over `FocusEngine`
(`ElementRef` now wraps a `NodeHandle`; stale gen → null).

**Commands/accelerators:** `Command` record reused unchanged (pure data). `AcceleratorRegistry`
(`SlabAllocator<AcceleratorEntry>`) matched **before** routed key dispatch (WinUI order). KeyDown order:
IME-if-composing → accelerators → access-keys (Alt) → routed tunnel/bubble `OnKeyDown` → built-ins
(Tab/arrows/Space-Enter/Esc). Access keys (Alt) enter KeyTips overlay mode.

**Accessibility — UIA over the retained tree, no peer control:** the verified `PixelShaderEffect` recipe is
the implement-direction template — POD native-memory `NodeProvider` struct, shared static vtable of
`delegate* unmanaged[MemberFunction]` → `[UnmanagedCallersOnly(CallConvs=[CallConvMemberFunction])]` statics,
`GCHandle` bridge to managed `A11yBackend`, `Interlocked` refcount, `Unsafe.AsPointer(ref this)+offset`
multi-interface QI tear-off, `NativeMemory.Alloc/Free`, lazy per-materialized-provider, **`nodeGen` ABA
guard**. **`UiaClientsAreListening()` gates ALL a11y work** (cached, refreshed on `WM_GETOBJECT` + low-freq
timer — **not** per-frame P/Invoke); zero providers materialized when no AT is attached. `IRawElementProvider
Fragment.Navigate` is a **topology walk** (skip `A11yRaw`, collapse non-`A11yPresent`) — the single-tree
payoff. `PatternFlags`→vtable inclusion (Invoke/Value/Toggle/Selection/RangeValue/ExpandCollapse/Scroll/Text/
Grid). **UIA threading = strict `ProviderOptions.UseComThreading`** (folds the unsafe-read MAJOR): ALL provider
calls (read AND write) marshal to the UI thread via the HWND pump (HWND must be STA/pumping) — no "read on
calling thread." **`GetRuntimeId` derived from STABLE logical identity** (keyed path hash / persistent
per-logical-node id), **not** slot+gen (folds the AT-tracking fix); slot+gen is the internal provider→node
binding only. **`get_BoundingRectangle`** uses the shared ancestor-transform-chain helper (not just client→
screen offset). **Live regions:** the reconciler captures pre-write `StringId` for `A11yLiveRegion` nodes and
compares post-write, raising `RaiseLiveRegionChanged`; the **UIA Notification event** (`UiaRaiseNotification
Event` / `IRawElementProviderSimple3`) is added to `IA11yBackend` for `UseAnnounce`. Auto-name derivation
(the `AccessibilityScanner` heuristics) is extracted into a shared helper consumed by both the DEBUG lint and
runtime `get_Name`.

**IME (folds the re-scope):** **v1 = `WM_IME_*`/Imm32** (`ImmGetCompositionString`/`ImmSetCompositionWindow`
— covers CJK, far simpler); **`ITextStoreACP2`/TSF = v2** (a major workstream, not a footnote — document lock
arbitration, sinks, ACP ranges; composition mutation must respect TSF write-lock state). Caret rect computed
via the shared transform helper.

**Clipboard / drag-drop:** `IClipboard` (OLE `CF_UNICODETEXT` + custom formats; `TryGetText(out string)` edge
GC allowed). **`DoDragDrop` runs on the UI thread** (it pumps its own modal loop — folds the worker-thread
bug); `IDropTarget` callbacks (the OS calls them on the UI thread during that loop) route into the single-
threaded input path, **not** free-threaded `event Action`. Drag source via `IDropSource`/`IDataObject`
(hand-vtable CCW). Drop feedback = transient DrawList overlay.

**Win32 map fixes (folded):** `WM_POINTERLEAVE` + `TrackMouseEvent(TME_LEAVE|TME_HOVER)` for leave tracking
(not `RegisterTouchHitTestingWindow`); `WM_CHAR` after `TranslateMessage` is the committed-text path
(`WM_UNICHAR` optional).

**COM standardization (folded):** hand-built vtable / `ComPtr<T>` for **both** directions (consume via
`lpVtbl[n]`; implement via static CCW vtables) — matches what `Win32.Interop` vendors; no ComWrappers.

---

## 6. END-TO-END DATA FLOW — user clicks a Button

```
T0  USER CLICKS (mouse down+up over a Button)                                   [OS / display]
 │
 1  Pal.Windows WndProc ([UnmanagedCallersOnly] static) gets WM_POINTERDOWN/UP   [OS wait? NO: UI thread,
    → GetPointerInfo → translate to InputEvent{PointerDown/Up, pos in px}         message pump]
    → DIP-convert once → write POD into InputEventRing (move-coalesced)
 │  (phase 1: IPlatformWindow.PumpInto(ring) returns; device-lost flag read = clear)
 ▼
 2  InputDispatcher.Drain(ReadOnlySpan<InputEvent>)                              [UI thread]
    a. HitTest(SceneStore, ptDIP): reverse-z descend topology, Inverse(LocalTransform),
       SDF-RoundRect self-test on the Button node → Hit(buttonNode), route captured
       into stackalloc Span<NodeHandle> (Root..Button)
    b. HandlerMask pre-filter: Button has Tapped|PointerPressed bits set
    c. tunnel (Root→Button) then bubble (Button→Root): resolve PointerHandler? from
       HandlerTable[slot] only where mask bit set; invoke by ref (PointerEventArgs, no box)
    d. Button's OnClick delegate (the GC edge) runs → calls a hook setter:
         setCount(count + 1)   →  RerenderToken.Request()
       _isRendering guard is FALSE (we're in dispatch, not render) → coalescer gate
       (Interlocked.CompareExchange) marks the owning Component dirty, enqueues for THIS frame's
       render phase; SubtreeDirty propagates up NodeFlags to the component-host ancestor path
 ▼
 3  hook-state flush: pending setState applied (the UseState cell's value = count+1)   [UI thread]
 ▼
 4  render(Component): the dirty Button's Component.Render() runs → returns a new          [UI thread]
    immutable Element tree (Button(label = $"Clicked {count+1}")). [FastPath] builder
    accumulates modifier ops into the per-render arena; materializes to Element records.
 ▼
 5  reconcile (Reconciler → ISceneBackend):                                                [UI thread]
    - structural sub-phase (growth allowed): keyed-LIS diff vs prev-frame Element (held in the
      per-host side array). Button node identity stable (same Key/ElementTypeId) → no CreateNode.
    - edit sub-phase (growth locked): EditPaint(buttonNode) diff-on-Dispose detects the glyph-run
      content changed (label text) → MarkPaintDirty + MarkLayoutDirty (text width may change);
      the generated switch(ElementTypeId) writes NodePaint/Text columns; new StringId for the label.
    - dirty-node worklist appended (O(dirty)); A11yLiveRegion? no.
 ▼
 6  layout (dirty-scoped): FlexLayout.Run over the dirty subtree. MeasureText (the only seam     [UI thread]
    call) → ITextShaper.Shape fills ShapedRunBuilder spans (DWrite); L1 shaped-run cache miss →
    shape once; line-break/metrics engine-side; Bounds[buttonNode] (LOCAL) updated; pixel-snap
    seeded from parent's committed absLeft/absTop. If measured size unchanged, stops at the node.
 6.5 layout-effects: none on this path.
 ▼
 7  animation: a press-ripple/scale timeline (if any) writes LocalTransform[buttonNode],          [UI thread]
    marking TransformDirty only — NO relayout. WorldTransform[] composed top-down.
 ▼
 8  record (DrawList → front arena): walk tree. Clean siblings' command spans memcpy'd from        [UI thread]
    the back arena (their handles IsLive, content-epoch unchanged). Button subtree re-recorded:
    PushTransform → DrawShadowCmd(uniform-radius erf) → FillRoundRectCmd(fill, SDF AA) →
    FillRoundRectStrokeCmd(border) → DrawGlyphRunCmd{GlyphRunHandle, origin, BrushHandle}
    (NO atlas UVs baked). SortKeys written to the parallel ulong[] arena (paint-order primary).
 ▼
 9  batch: stable LSD radix sort over ulong[]; overlap-aware coalesce into instanced quad          [UI thread]
    batches (translucent Button-over-background respects submission order). Glyph-run UVs resolved
    now from the GlyphRunTable (atlas pinned this frame).
 ▼
10  submit: IGpuDevice.SubmitDrawList(frontArenaSpan, sortKeysSpan, ctx)                            [UI thread →
    → Rhi.D3D12 leaf walks POD opcodes with concrete devirtualized types → pooled                  GPU queue]
    ID3D12GraphicsCommandList: Barrier(backbuffer PRESENT→RT), BeginRenderPass(RTV=_UNORM_SRGB,
    clear), SetPipeline(SDF PSO), BindConstants/BindTexture(glyph atlas), DrawInstanced × batches,
    Barrier(RT→PRESENT) → ExecuteCommandLists → queue.Signal(fence, ++v)
 ▼
11  present: MsgWaitForMultipleObjectsEx(latencyWaitable, QS_ALLINPUT) →                            [UI thread]
    IDXGISwapChain3.Present(1, flags) → DComp Commit() only if a visual prop changed.
    PresentResult.Ok. (Occluded → 1Hz test-present; DeviceLost → recovery §5.1.)
 ▼
12  passive-effects (UseEffect): any effect depending on count runs its cleanup+effect AFTER        [UI thread]
    present (React commit). setState here → marks dirty + RequestFrame() for N+1.
 ▼
13  arena swap: front/back DrawList arenas swapped; per-frame arenas Reset (O(1));                  [UI thread]
    deferred-free drained behind retired fence values.
 │
 ▼  Pixels on screen one frame after the click; zero managed allocations in phases 6–13.
```

If an AT is attached, the Button's `Invoke` UIA pattern (a `[UnmanagedCallersOnly]` vtable method, called on
an OS RPC thread, **marshaled to the UI thread** via `UseComThreading`) would re-enter the same `OnClick`
delegate path at step 2d — one declaration, three modalities.

---

## 7. CROSS-CUTTING CONCERNS

- **Threading / affinity.** Single UI thread v1; `SceneStore` lockless by confinement; off-thread `setState`
  auto-marshals (`IPlatformAppLoop.Post<TState>`). Device-lost callback on an OS wait thread does only a
  `Volatile.Write` + `RequestFrame` (§5.1). UIA/TSF/OLE callbacks marshal to the UI thread. The DrawList POD
  + immutable `SceneFrame` snapshot + slot-reuse quarantine (constant 0→2) is the render-thread seam, designed
  not built.
- **Error handling / device-lost.** Synchronous `Present`/`Submit` HRESULT is primary detection; async wait is
  a hang backstop. Recovery re-realizes all GPU objects from retained CPU state (RHI stores only
  realizations). Per-cell try/catch isolates effect-body exceptions (a labeled improvement). Reconcile error
  boundaries unchanged (Reactor).
- **DPI.** PerMonitorV2; `Scale` = current effective DPI (post-`WM_DPICHANGED`); DIP→px once at the pump
  boundary and once at layout→world composition; back buffer is physical px (DPI change without client-size
  change does **not** resize the swapchain). `ConfigVersion` bump on scale change forces relayout.
- **Theming / dark mode.** Baked `ReadOnlySpan<byte>` Light/Dark token blobs; **HighContrast via PAL
  `ISystemColors`** (per-window theming, live OS colors); resolved through `RenderContext`, not an ambient
  global. `WM_SETTINGCHANGE("ImmersiveColorSet")` → `ThemeChanged` `WindowEvent` → re-render. Focus visuals/
  access-key badges/IME underlines pull HC colors.
- **Animation / compositor timelines.** Animation phase (7) writes `LocalTransform`/`Opacity`, marks
  `Transform/PaintDirty` (never `LayoutDirty`) → composition-style independent animation with no relayout and
  no re-record (the batcher re-applies the cached transform). Inertia integrator (from scratch) ticks here.
  Animating a *layout* property is the rare opt-in that sets `LayoutSelfDirty` per tick.
- **Hot reload.** `MetadataUpdater.IsSupported`-gated (AOT-dead); `ResetForHotReload`/
  `MigrateHooksForHotReload` reuse requires keeping cell field names (`Value`) and generic cell shapes stable.
  Dev-loop only.
- **Diagnostics / devtools.** The `AccessibilityScanner` DEBUG lint (`A11yDiagnostic` JSON via
  `JsonSerializerContext`); a Scene/DrawList inspector (dump SoA columns, dirty worklist, batch counts, PSO
  dedup hits); per-phase frame timing (QPC); the alloc-tripwire (P1) flags any phase that allocates.
- **Testing strategy (headless).** Two tiers: **(1) CPU/null encoder = deterministic STRUCTURAL asserts**
  (draw-call count, batch coalescing, barrier ordering, PSO dedup, layout `Bounds` parity against Reactor
  Yoga golden values at scale 1.25/1.5/1.75) — the primary regression net; **(2) WARP readback = tolerance-
  based SMOKE only** (did it render, rough histograms — **not** exact golden hashes; WARP is not bit-identical
  to hardware). `Pal.Headless` + `Rhi.Headless` run the **entire 13-phase frame lifecycle** with no window and
  no GPU on a CI build agent — the payoff of the seam.

---

## 8. NATIVEAOT & FOOTPRINT

**Source generators to build** (all build-time, no runtime codegen):
1. `ElementTypeId` generator — stable `ushort` per `Element` record type → integer type-dispatch (replaces
   `GetType()`/`Type`-keyed `switch`).
2. Modifier extension generator — `T:Element` fluent → arena `ModifierOp` accumulation + covariant-return
   `WithModifiers`.
3. Bitmask `DiffProps` generator — per-element prop-change mask (idiomatic, grounded in Reactor's
   hand-written precedent).
4. Scene-writer generator — `switch(ElementTypeId)` column writer in the Reconciler/backend (NOT Dsl).
5. HookDeps generator — `DepKey.From` capture (≤4 unmanaged params; interceptors opt-in OFF).
6. Theme generator — baked `ReadOnlySpan<byte>` Light/Dark blobs.
7. (`FluentGpu.Interop.SourceGen`, leaf-only) COM-binding generator — hand-vtable `IComObject` structs for
   DComp/DWrite/TSF/UIA/OLE against pinned interface versions (`IDWriteFactory2+`, etc.).

**COM dispatch strategy (canonical: `dotnet10-csharp14-zero-alloc.md` §4):** **tiered.** The per-frame
hot path uses hand-built vtables (consume: `lpVtbl[n]` calli; implement: static
`[UnmanagedCallersOnly(CallConvMemberFunction)]` vtables + `GCHandle` + `Interlocked` refcount); **all
cold/warm COM (UIA/TSF/OLE/DWrite setup) uses `[GeneratedComInterface]`/`[GeneratedComClass]`.**
`ComWrappers` is rejected on the hot path only (cache-lookup + call-site control). `ComPtr<T>` is the only
lifetime primitive; `[UnmanagedCallersOnly]` for `WndProc` and the
device-lost wait callback; `GCHandle` (Normal for window/device-lost, pooled-pinned for DWrite analyze)
the only managed-side roots. VARIANT/BSTR marshaling hand-rolled (`SysAllocString`/`VariantInit`), no
reflection marshalers.

**Trimming config:** `<PublishAot>true>`, `<IsTrimmable>true>`, `<TrimMode>full>`, per-leaf feature switches
(e.g. trim Accessibility when UIA unused), `[SkipLocalsInit]` on hot modules, size-tuned
`Directory.Build.props`. Portable assemblies carry zero `[DynamicallyAccessedMembers]`/`MakeGenericType`/
`Activator`. Generic instantiation is bounded (`SlabAllocator<T>` over a small fixed set of engine-internal
`unmanaged` value structs; reference-`T` shared generics for the DSL).

**Per-frame allocation budget:** target **0 managed allocations in phases 6–13** (steady state); the only
permitted per-frame GC is freshly-captured user closures at the edge (phases 2/4). Measured by the
`[Conditional("DEBUG")]` alloc-tripwire (`GC.GetAllocatedBytesForCurrentThread()` delta per phase, asserted
== 0 for hot phases) + a CI benchmark gate.

**Binary/IL budget:** ratcheted CI gate via `sizoscope`/`.mstat` — start ~8 MB whole-exe (CoreCLR-AOT base
~1.2–2 MB + the authored DWrite/DComp/UIA vtbl IL + renderer/atlas/tessellator/batcher), tighten as the
renderer lands. The aspirational ≤5.5 MB is a target, not a guarantee; the largest IL items are
`Win32.Interop` (graphics + present + DComp/DWrite/UIA bindings — trims to only the vtable methods actually
called) and the DWrite binding surface (validated against a spike before pinning a figure).

---

## 9. CROSS-PLATFORM (macOS)

A macOS port adds leaf assemblies; **nothing above `Hosting` recompiles** (the swap line is `Hosting`'s
per-OS factory: `#if WINDOWS` selects Windows leaves; macOS build selects Cocoa/Metal/CoreText).

| Seam | macOS leaf implements | Maps from Windows |
|---|---|---|
| `IPlatformApp/Window/AppLoop` (`Pal.Cocoa`) | `NSWindow`/`NSView`, `CVDisplayLink` frame callback, `NSEvent`→`InputEvent` POD ring, `NativeHandle{Kind=NsView}` | HWND/WM_*/PerMonitorV2/raw input |
| `IGpuDevice/ISwapchain/ICommandEncoder` (`Rhi.Metal`) | `id<MTLDevice/CommandQueue/RenderPipelineState>`, `CAMetalLayer` present, `id<CAMetalDrawable>` = `CurrentBackBuffer`, `MTLRenderPassDescriptor` = `RenderPassDesc`, `MTLHeap` allocs, argument buffers = the shared binding model | D3D12/DXGI flip + DComp |
| Text seam (`Text.CoreText`) | `CTFontCollection`/`CTFontCreateForString`, `CTFontGetGlyphsForCharacters`, `CTFontDrawGlyphs`→`CGBitmapContext` (grayscale, simpler — no ClearType) | DWrite shape+raster |
| `IImeSession` | `NSTextInputClient` | TSF/Imm32 |
| `IA11yBackend` | `NSAccessibility`-conforming objects reading the same `A11yInfo`/topology columns | UIA providers |
| `ISystemColors` | macOS HC/appearance colors | Windows HC tokens |
| `IClipboard`/`IDragDropBackend` | `NSPasteboard` / `NSDraggingSource`/`Destination` | OLE clipboard / `DoDragDrop` |
| Color compositing | `CAMetalLayer.colorspace` (linear) — the atlas + linear-blend invariant map cleanly | sRGB-RTV-over-UNORM + DComp PREMULTIPLIED |

**What must NOT leak above the seam:** any `HWND`/`NSWindow`/`HRESULT`/`NSError`/`ComPtr`/`id<...>`/`ID3D12*`/
`MTL*`/`IDXGI*`/`CAMetalLayer`/`WM_*`/`NSEvent`/`Windows.Foundation.Point`/WinRT type. The portable layers see
only `Size2/Scale/PointPx/Vec2`, opaque `NativeHandle`, POD `InputEvent`/`WindowEvent`, the DrawList POD
stream, generational handles, POD RHI descriptors. Shaders authored once (HLSL→DXIL now; `-spirv`→SPIRV-Cross
→MSL later). BiDi/line-break moves into portable `FluentGpu.Text.Unicode` at this milestone (deferred until
now so DWrite's `Analyze*` carries v1).

---

## 10. RISK REGISTER

| Risk | Likelihood | Impact | Mitigation / fallback |
|---|---|---|---|
| **Text correctness** (BiDi reorder, complex scripts, font fallback, gamma matching DWrite, emoji) | High | High | Itemize→fallback→shape pipeline (one run = single face/script/direction); L1 key includes BiDi/script/locale; line-break behind the shaper (DWrite v1) or conformance-tested portable Unicode (macOS milestone); text-gamma exception A/B-validated vs native; BGRA color atlas + `IDWriteColorGlyphRunEnumerator1` for emoji (or defer). v1 display-only (no editing/IME-edit). |
| **Managed path tessellation** (quality, perf, AA matching atlas glyphs at the size boundary) | Med | Med | Zero-alloc CPU tessellator + AA fringe, cached by geometry×scale; oversized glyphs → dedicated large-glyph atlas page (one AA model) rather than tessellate where possible; document the AA boundary; analytic SDF is the default (paths are the exception). |
| **COM under AOT** (hand-vtable correctness — vtable indices, callee CCW, device-lost threading, TSF locks) | Med | High | Vendor ComputeSharp's proven calli pattern + `PixelShaderEffect` CCW recipe; verify every vtable index against d3d12.h/dwrite.h ABI; CCW pins both source struct AND char[]; device-lost callback touches no `ComPtr`; TSF deferred to v2 (Imm32 v1) and gated by write-lock state. |
| **Zero-alloc hooks without `params object[]`** (DepKey legality, reference deps, generic explosion) | Med | High | Pure-scalar 16-byte `DepKey` (no GC-ref union — illegal CLR layout avoided); reference deps via interned identity or a managed side-buffer compared by `ReferenceEquals`; ≤4-arity generic-overload capture as PRIMARY (ComputeSharp-class), interceptors opt-in OFF; inline `DepDeps` for persistence. |
| **Analytic AA quality** (erf shadow per-corner-radius, fwidth on rotated content, sub-pixel snap parity) | Med | Med | erf shadow restricted to uniform corner radius (A&S approx, sigma floor); per-corner/arbitrary → `IEffectRunner` offscreen blur; true gradient magnitude (not fwidth L1) for rotated content; golden parity tests at non-integer DPI. |
| **Painter-order under batch reordering** (translucent overlapping Fluent surfaces) | Med | High | Paint-order-primary SortKey for translucent; opaque freely reordered; overlap-aware batch break (per-layer occupancy); revised (smaller but correct) batch-reduction claim. |
| **Flip-model + DComp + sRGB / MSAA mismatch** (startup `INVALID_CALL`, gamma-wrong AA) | High if unaddressed | High | Buffer `UNORM` + RTV `_UNORM_SRGB`; single-sampled back buffer + explicit `ResolveTexture` (linear) for opt-in MSAA; `FLIP_DISCARD`; documented as a hard cross-agent color contract. |
| **Fork scope underestimate** (D3D12 graphics + present + DComp bindings) | High | Med | Stated honestly as the largest item (§5.1); budgeted; vtable indices verified; `Present1`/`DXGI_PRESENT_PARAMETERS`/DComp authored from scratch (not seeded). |
| **Multi-window resize / device-shared stalls** | Med | Med | Per-swapchain fence waits (not device-wide `WaitIdle`); per-swapchain deferred-delete ring for DXGI back buffers. |
| **UIA cross-thread SoA corruption** | Med | High | Strict `UseComThreading` (all provider calls marshaled to UI thread); `UiaClientsAreListening` gates all work; logical-identity RuntimeId. |
| **Footprint overrun** (DWrite/DComp/UIA vtbl IL + AOT base) | Med | Med | Ratcheted CI gate (start 8 MB); trimming feature switches; validate binding IL against a spike before pinning. |

---

## 11. MINIMUM VERTICAL SLICE

The smallest end-to-end proof that validates the architecture (one window, one clickable, styled,
text-bearing, flex-laid-out, reconciled Button). Build order, each step a checkpoint:

1. **Window + GPU clear.** `Pal.Windows.CreateWindow` (HWND, PerMonitorV2, static `WndProc`+`GCHandle`) →
   `Rhi.D3D12` device (DIRECT queue, RTV heap, D3D12MA) → `CreateSwapChainForComposition` (`BGRA8_UNORM`,
   `FLIP_DISCARD`, frame-latency waitable) → DComp `Target`/`Visual`/`Commit` → RTV as `_UNORM_SRGB` →
   per-frame: `MsgWaitForMultipleObjectsEx`, barrier, `BeginRenderPass(clear)`, barrier, present. *Proves:
   PAL/RHI seam, hand-vtable COM under AOT, DComp present, color contract, device-lost backstop.*
2. **One rounded rect.** Author the SDF quad VS+PS in HLSL → DXC → DXIL `byte[]`; one PSO; one
   `FillRoundRectCmd` → `SubmitDrawList` → instanced quad with analytic SDF AA, linear blend, sRGB scan-out.
   *Proves: the graphics-pipeline fork, the DrawList POD path, SDF AA, the linear/sRGB pipeline.*
3. **One text run.** `Text.DirectWrite`: `IDWriteFactory2` → shape "Click me" (itemize→shape, `ShapedRunBuilder`
   spans, callee CCW for analysis) → rasterize into the `R8_UNORM` glyph atlas → `DrawGlyphRunCmd` (UVs
   resolved at batch, gamma-correct coverage blend). *Proves: text seam, DWrite callee CCW under AOT, atlas,
   glyph blend exception, BiDi/visual-order contract.*
4. **Flex layout.** `SceneStore` SoA with two nodes (a `VStack` container + the Button); `LayoutInput`
   columns; `FlexLayout.Run` (ported Yoga, `ref struct LayoutNode`, arena scratch, `MeasureText` seam, pixel-
   snap) → `Bounds[]` (LOCAL). *Proves: SoA store, generational handles, the layout port + substrate, the
   measure seam, the only Layout↔Text call.*
5. **Reconciler + UseState.** A `Component` with `UseState<int>` rendering `Button($"Clicked {n}")`;
   `Reconciler` → `ISceneBackend` writes columns; `DepKey`-based hooks; `[FastPath]` modifier builder; prev-
   Element retention. *Proves: the Reactor model retargeted, zero-alloc hooks, the scene-write-out-of-Dsl
   layering, two-phase reconcile ref-safety.*
6. **Clickable Button.** `InputDispatcher`: hit-test (SDF self-test, LOCAL bounds), `HandlerMask` pre-filter,
   bubble route, `OnClick` (ref-arg named delegate) → `setN(n+1)` → coalescer → next-frame re-render →
   re-reconcile → dirty-scoped layout/record/present updates the label. *Proves: the full 13-phase frame loop,
   input pipeline, the click→setState→repaint round-trip, zero per-frame allocation in the hot phases.*

Passing #6 with the DEBUG alloc-tripwire green on phases 6–13, on both the real D3D12 path **and** the
`Pal.Headless`+`Rhi.Headless` CPU path (structural asserts), is the architecture sanity anchor: it exercises
every seam (PAL, RHI, Text), the SoA store, the ported layout, the retargeted reconciler/hooks, the custom
GPU renderer, and the present path — end to end, NativeAOT-clean, zero per-frame GC.
