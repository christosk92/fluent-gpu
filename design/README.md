# fluent-gpu — Architecture Spec

> A from-scratch, **near-zero-allocation**, NativeAOT, GPU-rendered UI engine with Reactor-style fluent C# markup + hooks.
>
> *"Near-zero" is precise: **zero per-frame managed allocation on the paint/submit half of the frame (phases 6–13)**; bounded, short-lived Gen0 allocation at the render/reconcile edge (Element records, child arrays, user closures). See [`winui-painpoints-assessment.md`](./winui-painpoints-assessment.md) for the honest accounting and the v1 limitations.*

> **Naming.** Repository / engine name: **`fluent-gpu`**. Root namespace: **`FluentGpu.*`** (e.g. `FluentGpu.Foundation`, `FluentGpu.Scene`, `FluentGpu.Rhi.D3D12`). References to **WinUI** / **WinUI 3** in these docs mean Microsoft's product (the thing we're replacing), not this engine.

## This folder

| File | What |
|---|---|
| [`SPEC-INDEX.md`](./SPEC-INDEX.md) | **Canonical spec index — read first when two docs disagree.** Per-contract precedence + the current canonical value of every cross-cutting contract (handle layout, COM, threading, quarantine, DepKey, …), the superseded/archived list, and the [`check-canon.ps1`](./check-canon.ps1) drift gate. |
| **`README.md`** (this file) | The digestible full overview — read this first. |
| [`architecture-spec.md`](./architecture-spec.md) | The exhaustive, authoritative spec (1,221 lines): every C# signature, byte layout, ASCII diagram, the end-to-end data flow, cross-cutting concerns, risk register, and the minimum vertical slice. |
| [`foundations.md`](./foundations.md) | The architect-of-record shared contracts: handles, allocators, the `SceneStore` SoA columns, DrawList encoding, PAL/RHI/Text seams, frame lifecycle, the assembly layout (the design-core acyclic graph — now incl. `Animation`/`Hooks`/`Controls` — plus OS/test leaves; 27 projects in the repo; the old "18 assemblies" count is stale). |
| [`subsystems/`](./subsystems/README.md) | **The 16 standalone subsystem designs** — full, current, cross-referenced. See [`subsystems/README.md`](./subsystems/README.md) for the index + the **contract-ownership map** (every DrawList opcode / RHI method / PAL seam / hook / generator → its one authoritative doc). Core engine: `pal-rhi` · `scene-memory` · `layout` · `gpu-renderer` · `text` · `reconciler-hooks` · `dsl-aot` · `input-a11y`. Features (WaveeMusic-driven): `media-pipeline` · `theming` · `virtualization` · `backdrop-effects-animation` · `window-backdrop-mica`. Hardening: `threading-render-seam` · `com-interop` · `validation`. |
| [`winui-painpoints-assessment.md`](./winui-painpoints-assessment.md) | **Honest** answer to "does this actually fix WinUI's GC pressure / slow UI thread / slowness?" — scorecard, what still bites, new risks, overclaims, and the v2 work to make the UI thread itself fast. |
| [`dotnet10-csharp14-zero-alloc.md`](./dotnet10-csharp14-zero-alloc.md) | The .NET 10 / C# 14 zero-alloc & AOT playbook: feature→subsystem matrix, the ComWrappers-vs-hand-vtable ruling, the `System.IO.Pipelines` fit check, and a spec-amendments checklist. |
| [`app-requirements-waveemusic.md`](./app-requirements-waveemusic.md) | The **driving app**: how FluentGpu builds WaveeMusic (Spotify client). Screen→workload inventory, the **four subsystems to ADD** (image/media pipeline, virtualization, theming/accent reactivity, backdrop/effects/video), the new hooks (`UseImage`/`UseVirtual`/`UseDynamicColor`/`UseVideoSurface`/…), a precise spec fold-in checklist, and the v1-vs-v2 build order. |
| [`hardened-v1-plan.md`](./hardened-v1-plan.md) | **Folds the v2 program into v1 and makes every risk safe-by-construction or CI-gated.** The render-thread seam (UI thread stops stalling present), generated-and-confined COM, vetted tessellation, O(Δ) edge + no-cliff arenas + airtight epochs, and the validation spine — with a safety-by-construction ledger, the honest "concurrent ≠ risk-free" accounting, and a build order that ships single-thread-correct *first* and only flips on parallelism behind a race gate. |
| [`core-fundamentals-gap-analysis.md`](./core-fundamentals-gap-analysis.md) | **What the core was missing vs React/Flutter/Compose/SwiftUI/Solid — now all folded into CORE.** The vdom-vs-signals verdict (keep vdom+reconcile), and a prioritized gap register: Tier-1 = lanes + automatic batching, a Suspense boundary, external-store consistency, the gesture arena, the text-selection + `ITextRangeProvider` seam, the overlay/portal manager, RTL layout mirroring; Tier-2/3 = selectors/derived state, springs+retarget, optimistic updates, UIA collection relations + virtualized realization, the control kit, i18n formatting, cursor contract, edge-autoscroll, live devtools, app-author test harness, inline-flow/container-queries/shared-element/motion-tokens. **The "defer / Tier-3 / out-of-scope for v1" framing in that register is historical — every gap there is now a fully-specified, buildable CORE design in its owning subsystem doc (see the [`SPEC-INDEX.md`](./SPEC-INDEX.md) §2 contracts and the [`subsystems/`](./subsystems/README.md) ownership map). No v2 deferral remains.** |
| [`budgets.md`](./budgets.md) | **Consolidated runtime resource budgets** — every native/GPU/bandwidth budget, eviction policy, and failure behavior in one table, plus the 8 budget gaps to close before implementation. |
| [`macos-debt-ledger.md`](./macos-debt-ledger.md) | **Consolidated macOS / cross-platform debt** — every Windows-specific decision, its macOS (Metal/CoreText/Cocoa) plan, and a Designed/Deferred/Unaddressed status (33/9/6). |
| [`archive/`](./archive/) | Superseded docs kept for history only (e.g. `dsl-aot-toolchain.md`) — **do not cite or implement from them**. |

PAL/RHI, scene/memory, layout, and input/accessibility are now **full standalone docs** under [`subsystems/`](./subsystems/README.md) (they began life consolidated in `architecture-spec.md §5`, which remains the original end-to-end narrative — the subsystem docs are the current, deeper, authoritative versions).

> **v1 honesty (read this).** The *lean* v1 runs all 13 phases on one UI thread, so a single over-budget frame still stalls present (same failure shape as WinUI, hit far less often), and it trades GC-correctness for hand-rolled-COM / managed-tessellation / atlas-epoch risk — full accounting in [`winui-painpoints-assessment.md`](./winui-painpoints-assessment.md). The [**hardened-v1 plan**](./hardened-v1-plan.md) folds the parallel fix and every risk mitigation *into* v1 (render-thread seam so present no longer stalls; COM generated + thread-confined; epochs CI-validated). Its honest verdict stands: slowness is **decoupled and substantially faster, not invincible** (a sustained GPU stall still bounds back to the UI thread), and the engine is **safe-by-construction where confinement/immutability allow and CI-gated everywhere else** — a correct single-thread v1 ships first, parallelism flips on only behind a green race gate.

---

## Context

**The ask.** "If you could implement WinUI 3 from scratch as a lightweight, near-zero-allocation, GPU-rendered engine — how would you do it?" Keep the parts developers love about `microsoft-ui-reactor` (Fluent C# markup + React-style hooks co-located with render), but throw away the heavy native core of `microsoft-ui-xaml` (the C++ XAML/Composition object model, WinRT/COM property system, dependency properties, per-element allocations).

**The key insight from the references.** Reactor's programming model is *already mostly host-agnostic*: `Element` is an immutable `record` (the "virtual DOM"), `Component.Render()` returns an `Element`, hooks live on a `RenderContext`, and a keyed reconciler diffs old→new. **The only thing tying it to WinUI is the patch backend** — today `Reconciler.Mount/Update` set properties on real `FrameworkElement`s and add them to `Children`. Swap that backend for our own retained GPU tree and the entire authoring model carries over unchanged. That is the whole bet.

**What this engine is.** A retained-mode, GPU-composited UI engine in **100% C# / .NET, NativeAOT** (no reflection, no runtime codegen, fully trimmable). App code is written exactly as in Reactor. The reconciler patches **our** `SceneStore` (a struct-of-arrays RenderNode tree) instead of WinUI controls; our layout engine measures/arranges it; our custom batched 2D renderer paints it on **D3D12**, with **DirectWrite** glyphs and **DirectComposition** present — all behind a swappable **PAL / RHI / Text** seam so a macOS **Metal + CoreText + Cocoa** backend drops in later without touching anything above the seam.

**Locked-in decisions.** Custom batched 2D renderer on Direct3D (SDF rects/shadows, tessellated paths) + DirectWrite glyph atlas + DComp present · pure Win32 + D3D/DXGI, **no WinRT / no Windows App SDK**, behind a portable seam for macOS · DirectWrite behind a text seam · NativeAOT + small footprint · Reactor `Element`/`Component`/hooks preserved · **C# / .NET**, reusing parts of `ComputeSharp`.

**How this spec was produced.** A 24-agent design workflow: 6 parallel explorers (winui-xaml core, Reactor DSL, Reactor reconciler/hooks, Reactor layout/loop/input, AOT/perf, ComputeSharp reuse) → an architect-of-record fixing shared contracts → 8 subsystem designs, each adversarially critiqued (every design returned `sound-with-fixes`; 15 blockers + 44 majors found and folded in) → a lead-architect synthesis. The factual claims below were re-verified against the actual reference source (ComputeSharp is compute-only at runtime; it has no DComp/DWrite bindings; Reactor's Yoga port carries the 8-entry measurement-cache ring).

> **Scope note.** This is a **v1 architecture**, and every gap in [`core-fundamentals-gap-analysis.md`](./core-fundamentals-gap-analysis.md) (Tier-1..Tier-3) is **folded into CORE** — lanes + automatic batching, the Suspense boundary, the external-store consistency contract, the gesture arena, the text-**selection + read-side + editable buffer seam** + `ITextRangeProvider`, the overlay/portal manager, RTL layout mirroring, selectors/derived state, springs+retarget+shared-element+motion-tokens, optimistic updates, UIA collection relations + virtualized realization, the control kit (`FluentGpu.Controls`), edge localization, the cursor/`Pal.SetCursor` contract, edge-autoscroll, the live devtools inspector (`FluentGpu.Devtools`), the app-author test harness (`FluentGpu.Testing`), and inline-flow rich text / container queries. The remaining named v1 deferrals are **narrower and non-architectural**: the single-thread *build-order step* (the render-thread seam is *designed and folded in* — it flips on only behind the race gate, §[`hardened-v1-plan.md`](./hardened-v1-plan.md) §6); grayscale text AA (ClearType v2); the **text-buffer-mutation/editing** path (the `ITextStoreACP2`-shaped commit-lock *seam* and `SelectionState` are core; Imm32-v1 drives it and full TSF editing wires in later); IME via Imm32 v1 (TSF CCW v2); 2.5D/perspective transforms out (2×3 affine only). None of these change the load-bearing architecture.

---

## 1. The stack & module layout

```
 ┌───────────────────────────────────────────────────────────────────────────────────┐
 │  APPLICATION  — Components, Hooks, fluent DSL  (Element records; pure C#)            │
 ├───────────────────────────────────────────────────────────────────────────────────┤
 │  RECONCILER   — keyed-LIS diff → ISceneBackend (handle-in / handle-out, POD-only)    │
 ├───────────────────────────────────────────────────────────────────────────────────┤
 │  SCENE (SoA)  — RenderNode columns + 3-axis dirty flags + generational handles       │
 │  LAYOUT       — struct flexbox + grid (Yoga algorithm ported onto the SoA substrate)  │
 │  INPUT/A11Y   — hit-test, focus, gestures, commands; UIA/TSF projection of the tree   │
 │  ANIMATION    — composition-style transform/opacity timelines (no relayout)           │
 ├───────────────────────────────────────────────────────────────────────────────────┤
 │  RENDER       — DrawList POD command stream, batcher (instanced quads), tessellator   │
 ├──────────────────────────── the swap line (POD only) ──────────────────────────────┤
 │  RHI  (iface)  IGpuDevice / ISwapchain / ICommandEncoder / IPipeline … generational  │
 │  PAL  (iface)  IPlatformApp / IPlatformWindow / IPlatformAppLoop / IImeSession …      │
 │  TEXT (iface)  IFontSystem / ITextShaper / IGlyphRasterizer / IGlyphAtlas             │
 ├───────────────────────────────────────────────────────────────────────────────────┤
 │  WINDOWS LEAVES (reference impl)        │  macOS LEAVES (future, nothing above changes)│
 │  Rhi.D3D12 · Pal.Windows (HWND/WM_*/    │  Rhi.Metal · Pal.Cocoa · Text.CoreText ·    │
 │  DXGI flip + DComp) · Text.DirectWrite  │  Accessibility.NSAccessibility               │
 │  · Accessibility.Uia · Win32.Interop    │                                             │
 └───────────────────────────────────────────────────────────────────────────────────┘
```

**18 strictly-acyclic assemblies.** `Foundation` (root, no deps) → seam interfaces (`Rhi`, `Pal`, `Text`) + portable core (`Scene`, `Layout`, `Input`, `Animation`, `Dsl`, `Hooks`) → `Render` → `Reconciler` → `Hosting` (composition root — the **only** assembly that references the OS leaves). Leaves: `Rhi.D3D12`, `Pal.Windows`, `Text.DirectWrite`, `Accessibility.Uia`, `Win32.Interop` (+ `Rhi.Headless`/`Pal.Headless` for CI). Two build-time analyzer DLLs: `FluentGpu.SourceGen` (portable, Win32-free) and `FluentGpu.Interop.SourceGen` (COM bindings, leaf-only).

**Acyclicity invariants (enforced):** `Render` references `Rhi`/`Text` *interfaces*, never the leaves · `Dsl`/`Hooks` depend only on `Foundation` (know nothing of Scene/Render/RHI) · the **`Reconciler` is the only bridge** to `SceneStore`, via `ISceneBackend` (this replaces Reactor's `IRenderBackend`) · **scene-writing generated code lives in `Reconciler`/leaf, never in `Dsl`**.

---

## 2. Eight enforceable design principles

| # | Rule | Enforcement |
|---|------|-------------|
| P1 | **Zero / near-zero per-frame managed allocation.** Hot paths use structs, arenas, slabs, pools, generational handles, spans. No `params object[]`, LINQ, `IEnumerable` iterators, or per-node delegates on hot paths. | `[Conditional("DEBUG")]` alloc-tripwire (`GC.GetAllocatedBytesForCurrentThread()` delta == 0 per hot phase) + CI benchmark gate; `ref struct` enumerators. |
| P2 | **NativeAOT-clean.** No reflection/`Activator`/`MakeGenericType`/IL-emit. **COM is tiered** (canonical: [`SPEC-INDEX.md`](./SPEC-INDEX.md) → `dotnet10-csharp14-zero-alloc.md` §4): hand-vtable `calli` on the per-frame hot path + any CCW invoked inside the frame loop; `[GeneratedComInterface]`/`[GeneratedComClass]` (source-gen, the AOT-recommended path) for all cold/warm COM (UIA/TSF/OLE/DWrite setup). `ComWrappers` is rejected **on the hot path only** (avoids the RCW-cache lookup + keeps call-site control). Source generators do all "codegen" at build time. | `<PublishAot>`, `<IsTrimmable>`, zero `[DynamicallyAccessedMembers]` in portable assemblies; `sizoscope`/`.mstat` gate. |
| P3 | **Small footprint.** One graphics backend, one binding set, no WinRT, no WinAppSDK. | Ratcheted exe-size CI gate (start ~8 MB, tighten); `[SkipLocalsInit]`; per-leaf trim switches. |
| P4 | **Swappable platform seam.** Everything OS/GPU-specific lives **below** PAL/RHI/Text in leaves referenced **only** by `Hosting`. Seam vocabulary is POD: generational handles, blittable descs, spans, opaque `NativeHandle`. No `HWND`/`HRESULT`/`ComPtr`/`ID3D12*`/`WM_*` crosses it. | Acyclic reference graph; headless PAL+RHI prove portability in CI. |
| P5 | **Reactor model preserved where possible.** `Element` stays an immutable record at the edge; `Component`, hooks, keyed reconciler, context, error boundaries, hot reload kept. Divergences (effect timing, deps representation, re-authored LIS) are **explicitly labeled**, not smuggled as "verbatim." | Reuse table (§4) marks each Reactor file take-as-is / port / re-author. |
| P6 | **GC refs only at the edges.** Permitted: user delegates, `Element`/`Component`, hook cells (mount-only), `ComPtr<T>` roots inside leaf `HandleTable`s, prev-frame `Element` retention. Everything else is a handle or POD. | Confinement asserts; `HandleTable.Free` Disposes the `ComPtr` before slot reclaim. |
| P7 | **Single source of truth.** One RenderNode tree (no WinUI dual visual/logical tree). UIA, hit-test, IME, layout, paint all read the same `SceneStore` columns. | SoA columns; UIA `Navigate` is a topology walk, not a peer tree. |
| P8 | **Color / coordinate / DPI conventions are contractual, set once.** Brush color = straight-alpha sRGB float4 → renderer converts to linear-premultiplied at shader input. DPI applied once at layout→world. `Bounds` is node-**LOCAL**; `LocalTransform` maps local→parent. | One shared transform-accumulation helper used by hit-test, UIA bounding-rect, IME caret. |

---

## 3. Foundations — the shared vocabulary (authoritative)

**Handle = 8 bytes `{index:u32, gen:u32}`.** Generation bumped on alloc *and* free (ABA defense). Zero-cost typed wrappers (`NodeHandle`/`BrushHandle`/`TextureHandle`/`ClipHandle`/`GlyphRunHandle`/`PipelineHandle`/…) each wrap one `Handle` with an `implicit operator Handle` + a `[Conditional("DEBUG")]` kind assert (kind is **not** stored in the handle — the wrapper type knows it).

**Four allocators.** `SlabAllocator<T:unmanaged>` (stable, gen-versioned, `ref T Get(Handle)`, `DenseSpan` for SIMD), `ArenaAllocator` (per-frame bump, O(1) `Reset`, 16-byte aligned), `ObjectPool<T:class>` (edge GC recycle — `Component`/`RenderContext`; cap 32, explicit `Reset` — the Reactor pattern), `HandleTable<TResource>` (the one place COM/GPU ownership meets handles).
- *Folded blocker:* the intrusive-first-4-bytes free-list trick is **forbidden for any slab holding a pointer/`ComPtr`** (it stomps `ComPtr.ptr_`); pointer-bearing slabs use a side `int[] _next` free-link.
- *Folded blocker:* `ref`-returning accessors must never be held across a `CreateNode`/`Alloc` that can `Array.Resize`. Enforced **structurally** via a two-phase reconcile (structural sub-phase with growth allowed under `_growthLocked=false`, then a non-allocating edit sub-phase under `_growthLocked=true`; edit scopes capture+revalidate the backing array on Dispose).

**Interning tables (in `Foundation`, retained, slab-backed).** `StringId` (4-byte interned int — **no `string` on the paint path**); `BrushTable`/`ClipTable` (content-hash dedup → stable handle, which is *why* clean-span `memcpy` is sound); `GlyphRunTable` (content-epoch stamped). `Element.Key` interns to `StringId` **at Element construction** (avoids a per-reconcile dictionary probe per keyed child).

**`SceneStore` — struct-of-arrays RenderNode tree (one node store).** One gen/free-list spine (the topology slab) indexes all parallel columns; `EnsureCapacity` resizes in lockstep. Columns + the phase that reads each:

| Column | ~Bytes | Read by |
|---|---|---|
| Topology `{Parent,FirstChild,LastChild,PrevSibling,NextSibling,ChildCount}` (doubly-linked → O(1) keyed Move, reverse-z hit) | 24 | reconcile (mutate), layout/record/hit (walk) |
| Identity `{Key:StringId, ElementTypeId:u16, ComponentSlot:u16, ElementEpoch, PayloadRef}` | 16 | reconcile, record (type dispatch) |
| `LayoutInput` (hot; cold `LayoutAux` split out via separate slab for the ~10% of nodes that need padding/border/percent min-max) | 96 | layout measure inner loop |
| `Bounds` `{X,Y,W,H}` **LOCAL** + flags | ~32 | layout (write), record/hit/UIA (read) |
| `NodePaint` `{LocalTransform:Affine2D(2×3), Opacity, Fill/Stroke:BrushHandle, StrokeWidth, Corners:CornerRadius4, Clip:ClipHandle, VisualKind, Layer}` (one cache line) | 64 | animation (write xform), record (read) |
| `InteractionInfo` `{HitCorners, HandlerMask:u16, CursorId, HitShape, HitGeometry}` | 16 | hit-test (hot) |
| `A11yInfo` `{Name,AutomationId,HelpText:StringId, ControlType, Patterns, …}` | 24 | UIA only (cold; gated by `UiaClientsAreListening`) |
| `FocusNav` `{TabIndex, IsTabStop, XY{L,R,U,D}}` | ~24 | focus engine |
| `NodeFlags` (single 32-bit dirty/state column) | 4 | every phase pre-filters on this |

**Dirty tracking.** Primary mechanism = an **arena-backed dirty-node worklist** that `MarkXDirty` appends to (per-frame cost = O(dirty), **idle frames truly O(0)**). Three orthogonal self-axes — `LayoutDirty` / `PaintDirty` / `TransformDirty` — so animation moves a node without re-recording draws or relaying out. `Subtree*` aggregate bits + `Vector256` SIMD flag-scan kept **only** for the full-rebuild path (recomputed in one bottom-up sweep, never cleared piecemeal). *Folded major:* the reconciler distinguishes **intrinsic-affecting** dirt (width/height/min/max/content → `LayoutSelfDirty`) from **participation-affecting** dirt (flex-grow/shrink/basis/align-self/margin/order/position → `LayoutParticipationDirty`, which always re-runs the parent container's flex/grid algorithm).

**DrawList encoding (`Render`).** Flat contiguous POD stream in **double-buffered arenas**. 8-byte `DrawCmd` header `{Op:u8, Flags:u8, PayloadSz:u16, _:u32}` + fixed-size POD payload per opcode; **all references by handle/index** (never inline data); **SortKeys in a parallel `ulong[]` arena** (64-bit). Opcodes: `FillRoundRect(Stroke)Cmd`, `DrawShadowCmd`, `DrawGlyphRunCmd` (refs `{GlyphRunHandle, origin, BrushHandle}` — **never bakes atlas UVs**, resolved at batch time so eviction stays transparent), `FillPathCmd`, `DrawImageCmd`, `Push/PopLayer`, `Push/PopClipRect`, `Push/PopTransform`, `Push/PopStencilClip`, `DrawFocusRingCmd` (production focus visual; rectangular `DrawFocusRect` is a superseded placeholder). **Incremental linchpin:** re-record dirty subtrees into the front arena; `memcpy` clean layers' spans from the back arena; swap at frame end → **zero per-frame managed allocation**. A memcpy'd clean span is valid **iff** every handle it references is `IsLive` **and** its realization's content-epoch is unchanged. `TransformDirty`-only nodes reuse their span; the batcher applies the new `WorldTransform` to the cached quads at submit (no re-record).

**PAL + RHI seam — POD across the seam.** The one `object` leak is killed: `NativeHandle {nint Value, NativeHandleKind Kind}` (D3D12 leaf switches on `Kind==Hwnd`, Metal on `Kind==NsView`). **C# events replaced by a host-owned POD `InputEventRing`** (slab-backed; the window move-coalesces the >1 kHz `WM_INPUT` flood; host drains a `ReadOnlySpan<InputEvent>` once/frame). RHI is **graphics-first** (zero COM in the interface): `IGpuDevice/ISwapchain/ICommandEncoder/IGpuBuffer/ITexture/IPipeline/IShaderModule` over generational handles + POD descs + spans. **`SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, ctx)` is the PRIMARY hot path** (the leaf walks POD opcodes with concrete, devirtualized, inlinable types — no per-draw interface dispatch); the per-opcode `ICommandEncoder` is a secondary API. *Folded:* `ResolveTexture` added (MSAA→single-sampled in linear); `SwapchainDesc.{ColorSpace,BackBufferFormat}` added; back buffer always single-sampled (flip-model + DComp).

**Text seam (`Text`).** `IFontSystem / IFontFace / ITextShaper / IGlyphRasterizer / IGlyphAtlas / ITextLayoutEngine`. The shaper fills **caller-owned spans** via `ShapedRunBuilder` (ref struct: `GlyphIds/Advances/OffsetsX/OffsetsY/Clusters/GlyphProps`) — zero-alloc, no `string` on paint. `GlyphKey` (24B) = `{FontFaceHandle, glyphId, sizeQ, aaMode, subpixelPhase, gamma}`. **Glyph positions reaching the renderer are FINAL device-space dest rects in VISUAL order** (BiDi reorder, clusters, mark positioning, subpixel phase all resolved by the seam; the renderer treats them as opaque positioned quads). Atlas eviction runs **only at frame start**; any glyph referenced by a live command this frame is ineligible; a batch-time UV miss rasterizes into a reserved overflow region (never faults).

> **⊳ Canonical threading model.** This single-thread 13-phase loop is **build-order step 1** (`hardened-v1-plan.md` §6), not the shipping topology. The canonical model is the **render-thread seam** — `hardened-v1-plan.md` §2 + [`subsystems/threading-render-seam.md`](./subsystems/threading-render-seam.md) §14 (see [`SPEC-INDEX.md`](./SPEC-INDEX.md)) — which inserts a **PUBLISH(13a)** phase, moves record/batch/submit/present onto a dedicated render thread, and sets `QUARANTINE = RenderInFlightDepth` (not the literal `0`/`2` shown here).

**Frame lifecycle — single UI thread v1, 13 ordered phases.**
```
1 pump        IPlatformWindow.PumpInto(ring)  — WM_*/NSEvent → POD ring; read device-lost flag ONCE
2 input       InputDispatcher.Drain(span)     — hit-test + tunnel/bubble + focus + gestures + accel
3 hook flush  apply queued setState
4 render      Component.Render() → Element     (per dirty component, pre-order)
5 reconcile   keyed-LIS diff → ISceneBackend   (structural sub-phase, then growth-locked edit)
6 layout      FlexLayout.Run / Grid (dirty-scoped) — measure+arrange in one descent; pixel-snap
6.5 layout-effects  UseLayoutEffect            — Bounds[] valid; setState ⇒ mark dirty + N+1
7 animation   timelines write LocalTransform/Opacity — marks Transform/PaintDirty, NEVER LayoutDirty
8 record      walk → DrawList POD (front arena) — clean spans memcpy'd from back arena
9 batch       radix-sort by ulong SortKey → instanced quad batches (overlap-aware)
10 submit     IGpuDevice.SubmitDrawList → ExecuteCommandLists → Signal(fence)
11 present     wait latency waitable (MsgWait…), Present(), DComp Commit if dirty
12 passive-effects  UseEffect — AFTER present (React commit); setState ⇒ N+1
13 arena swap  swap front/back DrawList arenas; Reset per-frame arenas; drain deferred-free
```
**Effect timing is a deliberate divergence from Reactor (labeled, not "verbatim"):** we adopt React-commit semantics — `UseLayoutEffect` at 6.5 (reads valid `Bounds`), `UseEffect` at 12 (after present); a `setState` in either marks dirty and requests frame N+1 (**no synchronous re-loop** — this eliminates Reactor's mid-drain mutation hazard and the unmount-during-reloop handle-recycle race). Per-cell try/catch around effect bodies is an explicit improvement. **Threading:** `SceneStore` is lockless by confinement (only the reconciler mutates, on the UI thread; off-thread `setState` auto-marshals via `IPlatformAppLoop.Post`). The DrawList POD stream + an immutable `SceneFrame` snapshot + a slot-reuse quarantine constant (0 in v1, 2 for render-thread) is the **designed** render-thread boundary.

---

## 4. Reuse strategy (the two reference repos + ComputeSharp)

**D3D12 is the sole Windows backend; D3D11 is dropped** *(verified: ComputeSharp's runtime calls `CreateComputePipelineState` only; the graphics methods exist in its `ID3D12Device` binding vtable but are never called — so we get the graphics vtable entries for free and only add the call sites)*. DComp composes any swapchain, so D3D11 buys nothing for present. WARP (`IDXGIFactory6` fallback, already in ComputeSharp's `DeviceHelper`) covers VMs/RDP and is the headless test path.

**Graphics-pipeline-shader gap (resolved).** ComputeSharp's C#→HLSL transpiler emits *compute* only. Decision: **author the SDF/quad/shadow/glyph shaders as `.hlsl` + `shapes_common.hlsli`, compiled offline by DXC → DXIL** (and `-spirv` → SPIRV-Cross → MSL for Metal later), embedded as source-generated `byte[]` blobs (no runtime compile, AOT-clean). The transpiler is retained **only** for an optional `Effects.D2D1` leaf (blur/backdrop). A compute rasterizer was rejected (our batcher needs a graphics VS+PS pipeline + DXGI swapchain + DComp present).

| Component | Decision | Source |
|---|---|---|
| `ComPtr<T>`, `IComObject`, `[NativeTypeName]`/`[VtblIndex]` | **Vendor verbatim** into `Win32.Interop` (only COM lifetime primitive) | `ComputeSharp.Core` / `ComputeSharp.Win32.D3D12` |
| `ID3D12*`/`IDXGI*`/`ID2D1*` COM bindings (ClangSharp, calli, AOT-proven) | **Reuse as-is (extract source)** | `ComputeSharp.Win32.D3D12`, `…Win32.D2D1` |
| `GraphicsDevice` device/queue/fence/descriptor-heap/command-pool plumbing | **Extract + fork** — has COMPUTE+COPY queues & CBV/SRV/UAV heap; **fork adds** DIRECT queue, RTV+DSV heaps, `ID3D12GraphicsCommandList` recording, graphics PSO path; keep fence-wait, device-lost (`fence→MaxValue`+`RegisterWaitForSingleObject`), per-LUID device cache | `ComputeSharp/Graphics` |
| D3D12MA memory allocator | **Reuse** (depend if NuGet AOT-stable in the spike, else vendor) | `ComputeSharp.D3D12MemoryAllocator` |
| C#→HLSL transpiler + incremental-gen helpers (`EquatableArray`, `HierarchyInfo`) | **Inspire / partial** (compute-only → effects leaf + packaging discipline) | `ComputeSharp.SourceGeneration[.Hlsl]`, `…SourceGenerators` |
| DXGI flip-model present (`SwapChainManager`) | **Build, seed shape only** (it's `SwapChainPanel`/`DispatcherQueue`-coupled and UAV-copies) | `ComputeSharp.UI/Helpers/SwapChainManager.cs` |
| **DComp + DWrite + UIA/TSF/OLE bindings** | **Build (generate)** — *verified absent in ComputeSharp*; same hand-vtable `IComObject` pattern; callee-side via static `[UnmanagedCallersOnly(CallConvMemberFunction)]` vtables + `GCHandle` (the verified `PixelShaderEffect` CCW recipe) | `FluentGpu.Interop.SourceGen` (new) |
| Reactor `Element` / DSL / `ElementExtensions` | **Port shape; re-author modifiers** (`el with { Modifiers = … }` is O(nodes×modifiers) record copies) | `Reactor/Core/Element.cs`, `Reactor/Elements/*` |
| Reactor `Reconciler`/`ChildReconciler` (keyed LIS) | **Port control flow; re-author keyed-middle + `ComputeLIS`** (original allocates 6+ collections per reorder → arena `Span<>` scratch) | `Reactor/Core/Reconciler*.cs`, `ChildReconciler.cs` |
| Reactor `RenderContext`/Hooks | **Port (mount-only edge alloc)**; `params object[]`+boxing → `ReadOnlySpan<DepKey>`; re-phase effects | `Reactor/Core/RenderContext.cs`, `Reactor/Hooks/*` |
| Reactor host coalescer + `RenderPriorityPolicy` | **Port** (`_isRendering` guard + `Interlocked.CompareExchange` gate + low-priority re-enqueue; transport → `IPlatformAppLoop`) | `Reactor/Hosting/ReactorHost.cs` |
| Reactor Yoga flexbox | **Port algorithm verbatim; rewrite substrate** (*verified: class-based with `MaxCachedMeasurements = 8` ring + arrays + `[ThreadStatic] Stack<>`* → SoA columns + `ref struct LayoutNode` + arena scratch) | `Reactor/Yoga/*` |
| Reactor Input (gesture/focus/command vocab) | **Port `Command` record unchanged; re-type structs off `Windows.Foundation.Point`→`Vec2`; re-implement FSM + from-scratch inertia** | `Reactor/Input/*`, `Reactor/Core/Command.cs` |
| WinUI 3 core | **Reference only** — learn the present/text/layout architecture, copy nothing; explicitly avoid its dual visual/logical tree, dependency-property system, per-element COM | `microsoft-ui-xaml/dxaml/xcp` |

**Depend-vs-vendor:** vendor (source-extract) the COM bindings + `ComPtr<T>` so we can fork/extend and pin trimming; NuGet-depend only on D3D12MA if it proves AOT-stable. **No NuGet dependency may drag WinRT or `SwapChainPanel`.**

---

## 5. Subsystem highlights (key decisions + folded fixes)

*(Full standalone designs in [`subsystems/`](./subsystems); PAL/RHI, scene/memory, layout, and input/a11y are detailed in [`architecture-spec.md` §5](./architecture-spec.md).)*

**PAL/RHI/present.** `RegisterClassExW` + static `[UnmanagedCallersOnly]` `WndProc` via `GCHandle` in `GWLP_USERDATA`; `PER_MONITOR_AWARE_V2`. Device: `D3D12CreateDevice` → DIRECT queue → fence (+ device-lost wait) → D3D12MA → RTV/DSV/CBV-SRV-UAV heaps. Present: `CreateSwapChainForComposition` (`B8G8R8A8_UNORM`, `FLIP_DISCARD`, 2 buffers, `PREMULTIPLIED`, `FRAME_LATENCY_WAITABLE|ALLOW_TEARING`) → `IDXGISwapChain3` → `DCompositionCreateDevice` → target/visual/`Commit`; **RTV created as `B8G8R8A8_UNORM_SRGB`**. `MsgWaitForMultipleObjectsEx(latencyWaitable, QS_ALLINPUT)` keeps the pump responsive. *Folded:* resize CPU-waits **only this swapchain's** fences (not device-wide); device-lost callback on an OS wait thread does **only** `Volatile.Write+RequestFrame` (never touches a `ComPtr`); synchronous `Present`/`Submit` HRESULT is primary detection. **Recovery invariant: the RHI stores only realizations — every GPU object is reconstructible from CPU-side retained data.** The D3D12 graphics-fork + present + the entire DComp binding set is honestly **the single largest implementation item.**

**GPU 2D renderer.** Analytic **SDF AA** for the rect family via true gradient magnitude `length(float2(ddx,ddy))` (correct under rotation). **Closed-form erf drop shadows in one quad** — uniform corner radius only; per-corner/arbitrary route to an offscreen `IEffectRunner` blur. Glyphs use atlas coverage (not SDF). **Batching:** 64-bit SortKey with a **paint-order-primary** key for translucent content; opaque reorders freely, translucent respects submission order where it overlaps (overlap-aware batch break); hand-written stable LSD radix sort. **Color:** buffer `UNORM` + RTV `_UNORM_SRGB` → blend/resolve linear, output premultiplied; text is a deliberate gamma exception. **3-tier clip:** scissor → SDF uniform → stencil (paths). **Partial present:** engine-owned persistent canvas RT (damaged regions scissor-repainted with `LoadOp.Load`). Per-frame managed allocations: 0.

**Text.** ~25 hand-authored vtbl structs against `IDWriteFactory2+`; **callee-side CCW** for `IDWriteTextAnalysisSource/Sink` (pin both the source struct and the UTF-16 `char[]` during `Analyze*`). **Itemize (BiDi+script+linebreak) → font-fallback → shape**; one run = single face+script+direction. **Two-level cache:** L1 shaped-run (constraint-free) + L2 wrap (constraint-bearing) → resize re-wraps without re-shaping. `MeasureFunc` is one `static readonly` delegate recovering its slot via a generational handle. Atlas: `R8_UNORM` grayscale + `BGRA8` color page for emoji; skyline packer. **v1 = display-only.**

**Scene/Memory.** Mutation = `ref`-return + handle via `ISceneBackend`; `EditPaint` diff-on-Dispose marks dirty only on a real delta. Slot recycling absorbs mount churn with zero array growth; `Component`/`RenderContext` recycle via `ObjectPool`. Deferred-free + `Detached` flag keep nodes alive for exit animations.

**Layout.** Port the Yoga algorithm body verbatim; rewrite the substrate. Single-pass (no WinUI two-pass). `YogaNode` class → `ref struct LayoutNode` over SoA columns; `LayoutResults` → `LayoutInput` (96B hot) + `LayoutCacheEntry` slab (`Ring8` preserves the 8-entry ring + `RawDim`). `LayoutScratch` is a pre-sized non-relocating bump region. Bit-for-bit numeric parity; incremental pixel-snap; **scroll is layout-free** (offset is the viewport-child `LocalTransform`). **Grid** is a distinct true-tracks algorithm sharing the substrate. Per-frame managed allocations: 0.

**Reconciler + Hooks.** Keep React's model + Reactor's diffing control flow; change the patch target (`UIElement`→`NodeHandle` via `ISceneBackend`), bookkeeping (`Dictionary`→`ComponentSlot` column), deps, effect timing. **Keyed reconcile re-authored** against arena `Span<>` scratch → zero managed alloc per reorder; doubly-linked O(1) `Move`. **`DepKey` is a pure-scalar blittable 16-byte struct** (a `[FieldOffset]` union of GC-ref + scalar is illegal CLR layout); reference deps via interned identity or a `ReferenceEquals` side-buffer. Generator lowers `UseEffect(fn, deps…)` to a ≤4-arity `unmanaged` generic capture (PRIMARY); interceptors opt-in/OFF. Preserved: self-trigger/memo skip, re-entrancy cap, error boundaries, boxless context, hot reload. **Entire subsystem is portable.**

**DSL + AOT toolchain.** `Element` stays an immutable record at the edge. **Modifier accumulation re-architected:** 16-byte `ModifierOp`s in a bump arena, `Element` holds an 8-byte `ModifierRef`; primary mechanism is a `[FastPath]` stack-bound `ref struct` builder carrying the arena cursor explicitly; the record `with` is the materialization step. Retained modifiers re-homed into a stable slab (never silently dropped). **Seven build-time generators:** ElementTypeId, Modifier-extension, bitmask `DiffProps`, scene-writer (in Reconciler/leaf, not Dsl), HookDeps, Theme, and the leaf-only COM-binding generator. `WGPU####` analyzers/codefixers (modeled on ComputeSharp's `CMPS####`).

**Input/Focus/IME/A11y.** **Input is 100% portable, COM-free.** Hit-test: reverse-z, `Inverse(LocalTransform)`, shape-accurate self-test (paint's fill rule), route into a `stackalloc Span<NodeHandle>`; one shared transform helper for hit-test + UIA bounds + IME caret. Dispatch args are `ref struct`; handlers are **named by-ref delegates** (`Action<refstruct>` is illegal C#); `HandlerMask` pre-filters. `OnClick` = Tapped ∪ Space/Enter ∪ UIA Invoke. **Focus** `FocusEngine` (pinned Tab buckets + projection XYFocus). **A11y = UIA over the retained tree, no peer control** (the `PixelShaderEffect` CCW recipe); `UiaClientsAreListening()` gates all a11y work; `Navigate` is a topology walk; strict `UseComThreading`; `GetRuntimeId` from stable logical identity. **IME v1 = Imm32**; TSF = v2.

---

## 6. End-to-end data flow — a Button click (one frame)

```
click → 1 WndProc(WM_POINTERDOWN/UP) → InputEvent POD into ring (DIP-converted once)
      → 2 InputDispatcher.Drain: hit-test (reverse-z, SDF self-test) → Button; HandlerMask pre-filter;
          tunnel→bubble (by-ref args, no box); OnClick (the GC edge) → setCount(c+1) → coalescer marks
          the owning Component dirty for THIS frame (_isRendering=false), SubtreeDirty propagates up
      → 3 hook flush (UseState cell = c+1)
      → 4 Component.Render() → new Element tree ([FastPath] builder → arena ModifierOps → materialize)
      → 5 reconcile: structural sub-phase (Button identity stable → no CreateNode); growth-locked edit:
          EditPaint detects label glyph-run changed → MarkPaint+LayoutDirty; generated switch writes columns
      → 6 layout (dirty-scoped): MeasureText (only seam call) → ITextShaper.Shape (L1 miss → shape once);
          Bounds[Button] (LOCAL) updated; incremental pixel-snap. Unchanged size ⇒ stops at node.
      → 7 animation: optional press-scale writes LocalTransform (TransformDirty only, no relayout)
      → 8 record: clean siblings memcpy'd from back arena; Button subtree re-recorded
          (Shadow→FillRoundRect→Stroke→GlyphRun, no UVs); SortKeys to parallel ulong[]
      → 9 batch: stable radix sort; overlap-aware coalesce; glyph UVs resolved from atlas
      → 10 submit: SubmitDrawList → leaf walks POD (devirtualized) → barrier/BeginRenderPass(RTV _SRGB)/
           SetPipeline(SDF)/DrawInstanced × batches/barrier → ExecuteCommandLists → Signal(fence)
      → 11 present: MsgWaitForMultipleObjectsEx → Present(1) → DComp Commit if visual prop changed
      → 12 passive UseEffect (after present); setState here ⇒ N+1
      → 13 arena swap; Reset; drain deferred-free behind retired fences
  Pixels on screen one frame after the click; ZERO managed allocations in phases 6–13.
```
If an AT is attached, the Button's UIA `Invoke` (a `[UnmanagedCallersOnly]` vtable method on an OS RPC thread, marshaled to the UI thread) re-enters the same `OnClick` path — one declaration, three modalities.

---

## 7. Cross-cutting, AOT/footprint, macOS, risks

**Cross-cutting.** Single UI thread v1; off-thread `setState` auto-marshals. Device-lost re-realizes all GPU objects from retained CPU state. DPI: PerMonitorV2, `Scale` = effective DPI, DIP→px once at pump + once at layout→world. Theming: baked Light/Dark blobs through `RenderContext`, HighContrast via PAL `ISystemColors`, `WM_SETTINGCHANGE` → re-render. Animation writes transform/opacity only (composition-style, no relayout/re-record). Hot reload `MetadataUpdater`-gated. **Testing (the seam payoff):** `Pal.Headless`+`Rhi.Headless` run the **entire 13-phase loop with no window and no GPU** — tier 1 = CPU/null encoder *structural* asserts (draw-call counts, batch coalescing, barrier ordering, PSO dedup, `Bounds` parity vs Reactor Yoga golden values), tier 2 = WARP readback *smoke* only.

**NativeAOT & footprint.** COM strategy is now split by call frequency (see [`dotnet10-csharp14-zero-alloc.md` §4](./dotnet10-csharp14-zero-alloc.md)): **hand-built vtable `calli` on the per-frame hot path and any CCW invoked inside the frame loop** (consume: `lpVtbl[n]` calli; implement: static `[UnmanagedCallersOnly(CallConvMemberFunction)]` + `GCHandle` + `Interlocked` refcount; `ComPtr<T>` the lifetime primitive) — **but `[GeneratedComInterface]`/`[GeneratedComClass]` for cold/warm COM (UIA, TSF, OLE, DWrite setup)**, the AOT-recommended path, in a separate assembly. The hot-path rejection of `ComWrappers` is justified by call-site control + avoiding the RCW-cache lookup, *not* a per-call allocation (which .NET 10 does not incur). Flat C exports via `[LibraryImport]`. `<PublishAot>`/`<IsTrimmable>`/`<TrimMode>full>`, per-leaf feature switches, `[SkipLocalsInit]`, zero `[DynamicallyAccessedMembers]` in portable assemblies. **Per-frame budget: 0 managed allocations in phases 6–13.** **Binary budget: ratcheted CI gate (start ~8 MB whole-exe, tighten);** ≤5.5 MB aspirational.

**macOS.** A port adds leaf assemblies; **nothing above `Hosting` recompiles**. `Pal.Cocoa` (`NSWindow`/`NSView`, `CVDisplayLink`, `NSEvent`→POD ring), `Rhi.Metal` (`CAMetalLayer` ≙ DComp visual, `id<CAMetalDrawable>` ≙ `CurrentBackBuffer`, argument buffers ≙ the shared binding model), `Text.CoreText`, `IA11yBackend`→`NSAccessibility`, `IImeSession`→`NSTextInputClient`. Shaders authored once (HLSL→DXIL now; `-spirv`→SPIRV-Cross→MSL later). BiDi/line-break move into a portable `FluentGpu.Text.Unicode` at this milestone.

**Top risks (full register in the spec).** Text correctness (BiDi/scripts/fallback/gamma/emoji) — High/High. COM-under-AOT hand-vtable correctness — Med/High. Zero-alloc hooks without `params object[]` — Med/High. Flip-model+DComp+sRGB/MSAA mismatch — High-if-unaddressed. Painter-order under batch reordering — Med/High. D3D12 graphics+present+DComp fork scope — High/Med.

---

## 8. Minimum vertical slice (the build order & the verification harness)

Each step is a checkpoint that proves a seam; **passing #6 IS the acceptance test for the architecture.**

1. **Window + GPU clear** — `Pal.Windows.CreateWindow` → `Rhi.D3D12` device → `CreateSwapChainForComposition` → DComp target/visual/`Commit` → RTV `_UNORM_SRGB` → per-frame wait/barrier/`BeginRenderPass(clear)`/barrier/present. *Proves: PAL/RHI seam, hand-vtable COM under AOT, DComp present, color contract, device-lost backstop.*
2. **One rounded rect** — SDF quad VS+PS in HLSL → DXC → DXIL; one PSO; one `FillRoundRectCmd` → `SubmitDrawList` → instanced quad, analytic SDF AA, linear blend, sRGB scan-out. *Proves: the graphics-pipeline fork, the DrawList POD path, SDF AA, the linear/sRGB pipeline.*
3. **One text run** — `Text.DirectWrite` → shape "Click me" (itemize→shape, callee CCW) → rasterize into the `R8_UNORM` atlas → `DrawGlyphRunCmd`. *Proves: text seam, DWrite callee CCW under AOT, atlas, glyph blend exception, visual-order contract.*
4. **Flex layout** — `SceneStore` SoA with a `VStack` + the Button; `FlexLayout.Run` (ported Yoga, `ref struct LayoutNode`, arena scratch, `MeasureText` seam, pixel-snap) → `Bounds[]` LOCAL. *Proves: SoA store, generational handles, the layout port + substrate, the measure seam.*
5. **Reconciler + UseState** — a `Component` with `UseState<int>` rendering `Button($"Clicked {n}")`; `Reconciler`→`ISceneBackend`; `DepKey` hooks; `[FastPath]` builder; prev-Element retention. *Proves: the Reactor model retargeted, zero-alloc hooks, scene-write-out-of-Dsl, two-phase reconcile ref-safety.*
6. **Clickable Button** — `InputDispatcher`: hit-test, `HandlerMask` pre-filter, bubble route, `OnClick` → `setN(n+1)` → coalescer → next-frame re-render → dirty-scoped layout/record/present updates the label. *Proves: the full 13-phase loop, the click→setState→repaint round-trip, zero per-frame allocation in hot phases.*

**Verification.** Passing #6 with **(a)** the DEBUG alloc-tripwire green on phases 6–13 (`GC.GetAllocatedBytesForCurrentThread()` delta == 0), **(b)** on both the real D3D12 path **and** the `Pal.Headless`+`Rhi.Headless` CPU path (structural asserts), and **(c)** layout `Bounds` matching Reactor's Yoga golden values at scale 1.0/1.25/1.5/1.75. Add a `dotnet publish -r win-x64 /p:PublishAot=true` size check against the ratcheted budget + a `sizoscope` report as the footprint gate.
