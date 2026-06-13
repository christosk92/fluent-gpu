# FluentGpu Subsystem Design Docs — Index

This directory holds the per-subsystem design docs for **FluentGpu** (from-scratch C#/.NET 10, NativeAOT, GPU UI
engine: Reactor model — Element records + Component + hooks + keyed reconciler — over a retained SoA RenderNode tree,
painted by a custom batched 2D renderer on D3D12 + DirectWrite + DirectComposition, behind a swappable PAL/RHI/Text
seam).

These docs sit **under** the root design docs, which remain canonical for cross-cutting contracts:

- `../foundations.md` — shared vocabulary (Handle/SlabAllocator/HandleTable/ChunkedArena/StringId, ISceneBackend, acyclic assemblies).
- `../architecture-spec.md` — §4 contracts + per-subsystem §5.x sections.
- `../hardened-v1-plan.md` — **canonical threading model** (§2), the five hardenings (§4), spec-amendments (§7).
- `../dotnet10-csharp14-zero-alloc.md` — .NET 10/C#14 patterns + the **canonical COM ruling** (§4).
- `../winui-painpoints-assessment.md`, `../app-requirements-waveemusic.md` — fold-ins and amendment sources.

Where an older root-doc section conflicts with a hardening/amendment, the hardened/amendment wins (see each doc's
Contradictions section). The recurring supersessions are: render-thread seam over "single UI/render thread";
generated-from-comabi.json COM over "hand-written vtables both directions"; pure-scalar DepKey + GcDepTable over the
illegal `[FieldOffset]` union; **canonical 13-phase loop** (PUBLISH at 13a) over foundations.md's older 12-phase
numbering; phase 6.5 RATIFIED; setState-in-effect ⇒ N+1 (no synchronous re-loop in v1).

---

## 1. One line per doc (all 19 subsystem docs)

| Doc | Owns (in one line) |
|-----|--------------------|
| [pal-rhi.md](./pal-rhi.md) | PAL seams (incl. 5 new: ISystemColors/IBackdropSource/IVideoPresenter/IVirtualMemory/IImageCodec) + RHI seam + `FluentGpu.Windows` D3D12/ internals + Pal/ windowing + DXGI flip-model & multi-visual DComp present-tree + CopyBufferToTexture/staging-ring/per-bucket-pool *mechanism*. |
| [scene-memory.md](./scene-memory.md) | The SoA SceneStore + DrawList ENCODING FRAMEWORK: the DrawCmd header, the byte arenas, the parallel `ulong[]` sortkey array, the opcode ENUM list, the 3 dirty axes + arena dirty-worklist, the clean-span+epoch rule, the column layout that SnapshotColumns snapshot (references gpu-renderer.md for per-opcode payload fields). |
| [gpu-renderer.md](./gpu-renderer.md) | The batched 2D renderer: every DrawList opcode's PAYLOAD STRUCT SHAPE (incl. DrawImageCmd union + DrawVideoCmd 7-field), instance structs, SortKey layout + batch-break rules, render-thread record/batch/tessellate types, the HLSL shader set, color contract, clip mechanism, AA ladder, clean-span reuse rule, the batch-time `ImageRef` UV-resolve branch, video hole-punch. |
| [text.md](./text.md) | Text/glyph subsystem: itemize/shape/font/raster/atlas/layout seams, GlyphKey/PackedGlyph, DrawGlyphRunCmd emission, the R8+BGRA glyph atlas + eviction, shaped/wrap caches, Yoga measure bridge, DWrite leaf, lyrics layout + UseSyncedLyrics. |
| [layout.md](./layout.md) | Incremental layout: the Yoga/Flex + Grid engines, measure/arrange passes, MeasureFunc bridge, scroll-ownership split (Input owns offset, Layout writes ContentSize), LengthValue/percent numeric parity, the virtualization layout participant (VirtualLayout.Run), producing the Bounds phase-6.5 effects + hit-testing read. |
| [virtualization.md](./virtualization.md) | Virtualized lists/grids/grouping + incremental load: UseVirtual/UseInfiniteCollection/UseVisibleRange consumers, recycle (O(1) per row), realize-window ArrayPool<Element>, the decode→measure→relayout anti-loop guard (bind row height to the fixed bucket), consume-gated DestroyNode. |
| [reconciler-hooks.md](./reconciler-hooks.md) | Reconciler + keyed-LIS ChildReconciler, RenderContext + hook cells, DepKey/GcDepTable, EffectScheduler/timing (6.5 + 12), 3-signal memo skip (SelfTriggered‖propsChanged‖HasConsumedContextChanged), the full core hook set, the `Mutate()` epoch chokepoint, ISceneBackend op set + NodeChildCollection. |
| [dsl-aot.md](./dsl-aot.md) | Fluent C# DSL (Element record, modifiers, UI factory, BrushSpec), the authoring attributes, the source generators (ElementTypeId/Modifier/DiffProps/HookDeps/ThemeBlob/SceneWriter), WgpuAnalyzer, and the root AOT/build baseline + footprint ratchet. |
| [input-a11y.md](./input-a11y.md) | Input/focus/IME/hit-testing/accessibility: InputEvent(Ring), dispatcher, HitTester + TransformChain, gesture/inertia FSM, FocusEngine, accelerators/commands, UIA NodeProvider, emits DrawFocusRingCmd (shape owned by gpu-renderer.md)/DrawAccessKeyBadgeCmd, IME/clipboard/dragdrop/a11y PAL seams, the interaction hooks. |
| [media-pipeline.md](./media-pipeline.md) | Image/video/lyrics pipeline: ImageHandle/**ImageRealization/ImageRefTable** (authority), DecodeScheduler, ResidencyManager, mosaic, the small-image-atlas **residency+packing+AcquireAtlasPage** (authority), CopyBufferToTexture/staging-ring/bucket-pool *policy*, VideoSurfaceRegistry, the media hooks; REFERENCES gpu-renderer.md §3.1 for DrawImageCmd/DrawVideoCmd shapes. |
| [theming.md](./theming.md) | Theming/system+accent reactivity/album-art dynamic color: Palette/BrushRecipe/DerivedKey, PaletteExtractor/BrushDeriver, palette/brush caches + back-refs, IBrushSink + ISystemColors, EpochContext, the theme hooks, opacity-only recolor cross-fade contract, HC bypass. |
| [backdrop-effects-animation.md](./backdrop-effects-animation.md) | In-app backdrop + effects + connected/implicit animation: IEffectRunner + EffectChain, EffectAux column, BackdropBaker/RT caches, AnimTrack/AnimEngine/DetachedAnimSlab/DrivenClock, ReducedMotion, PushLayerCmd{Effect} recording semantics, the backdrop/animation hooks. (Window-level Mica/Acrylic: see window-backdrop-mica.md.) |
| [threading-render-seam.md](./threading-render-seam.md) | The 3-thread topology + single-writer table, ThreadGuard, SceneFramePublisher (triple-buffer), **SceneFrame/SnapshotColumns/CopyInto POD shape** (authority), QuarantinePolicy + ledger, DrawListArenaRing, render-frame ordering invariant, retire-fence handshake, device-lost rendezvous, backpressure, WorkerPool, phase→thread map + seam build order. |
| [com-interop.md](./com-interop.md) | Generated/confined/gated COM: AbiHarvest (*.comabi.json + self-check), ComInteropGenerator + ComCalleeGenerator, ComPtr<T> (render-confined, Move-only), ComTracker, AbiVerify, FGCOM0001-0008 analyzers, the [GeneratedComInterface]/[GeneratedComClass]-only cold-COM policy, the residual hot-path hand-vtable surface, [LibraryImport] policy. |
| [controls.md](./controls.md) | Accessible-by-default control kit (`FluentGpu.Controls`): the control-template/styling system (`ControlTemplate`/`ControlTheme`/`VisualState`/`ControlShell` — lookless behavior/appearance split) + every control (Button/Checkbox/Radio/Switch/Slider/Progress/TextBox/ComboBox/ListView/GridView/TreeView/Tabs/Menu/Dialog/Flyout/ToolTip/Scrollbar/Expander/InfoBar) wired to the real seams (arena/overlay/UIA-patterns+collection-relations/selection-editable/focus/cursor/RTL/Suspense/springs); adds NO new opcode/column/PAL-seam/hook — pure composition. References Input/Text/Layout/Animation/Theme/Reconciler seams. |
| [form-validation.md](./form-validation.md) | Native form/input validation (`FluentGpu.Forms`): *validity is a derived value, never stored error state* — a gated `Memo<FieldError>` over a field's controlled signal; `Validator<T>` delegate rules (`Rules.Required/MinLength/Matches/Range/When/Equals`) returning a loc-key `MsgId`; `UseField`/`UseForm`/`FormScope`/`FieldBinding`; cross-field/conditional FREE (a rule reads a sibling signal in the memo), async race-immune, default `OnTouched`; the ONE new `Prop<ValidationState> BoxEl.Validation` channel → `NodePaint.ValidationBorder` (recorder solid error ring, theme-agnostic); the per-control `Field` prop + shared `FieldVisuals.MessageRow` (reveal-animated, zero space when valid); the OPTIONAL `[Validatable]` source-gen (`FluentGpu.Validation.SourceGen`) → `Validators.Member` arrays. Reflection-free/AOT, 0-alloc keystroke path, i18n messages. **Distinct from validation.md** (CI gates). |
| [devtools.md](./devtools.md) | Live devtools/inspector/profiler (`FluentGpu.Devtools`, dev-only, release-trimmed): `IDevtoolsObserver`+`DevtoolsBus` in-process protocol, the view-projection POD structs (`ComponentNodeView`/`HookCellView`/`RenderEventView`/`LayoutOverlayItem`/`LedgerView`/`SemanticsNodeView`/`UpdateRecordView`), the per-thread SPSC ring bus + opt-in localhost named-pipe wire, the devtools overlay z-layer (drawn with EXISTING fill/stroke/text ops), time-travel. Read-mostly observer: adds NO production opcode/hook/column/PAL-seam/RHI-method/control. Behind `FluentGpu.EnableDevtools`/`[Conditional("FG_DEVTOOLS")]` ⇒ 0 bytes release; quarantine participation `DevtoolsRenderInFlightDepth=1`. |
| [validation.md](./validation.md) | Validation & maturity program: the SPIKES (text.conformance/com.aot.roundtrip/render.aa/seam.race) + per-PR GATES (alloc/footprint/golden-image/structural/COM-refcount/epoch-fault/data-race) + the NEW per-gap always-on gates (lanes/transition determinism, auto-batching frame-count, Suspense reveal/keep-stale, external-store tear, discard-restart byte-golden, gesture-arena resolution trace, text-selection+`ITextRangeProvider` Narrator conformance, RTL `Bounds[]` mirror-equality, spring/retarget stability, overlay light-dismiss/focus-restore, virtualized-a11y realization, …), the trust ring + [Capability]/FG0001-3, the fault-injection corpora, regression budgets, the [Conditional]-erasure invariant. Ships the NEW public `FluentGpu.Testing` app-author harness (`TestHost`/simulate/assert/goldens). |

> **Related (not one of the 15 core docs):** [window-backdrop-mica.md](./window-backdrop-mica.md) — the *host-window*
> DWM system-backdrop (Mica/MicaAlt/DesktopAcrylic without WinRT); consumes `IBackdropSource` (pal-rhi.md) and the
> transparent-root present contract. In-app Acrylic/effects live in backdrop-effects-animation.md.
> [`../archive/dsl-aot-toolchain.md`](../archive/dsl-aot-toolchain.md) is **superseded** by `dsl-aot.md` and has been **moved to `design/archive/`** (historical only — do not cite or implement from it; it contains a known-illegal `DepKey` layout).

---

## 2. Contract ownership map

Each shared artifact has exactly ONE authority doc. If you need to change the artifact, change it there first.

### 2.1 DrawList opcodes

The **encoding framework** (DrawCmd header, byte arenas, parallel `ulong[]` sortkey array, opcode ENUM list, the
clean-span+epoch rule) is owned by **scene-memory.md**, which references **gpu-renderer.md** for each opcode's
**payload struct shape**.

| Opcode (payload struct shape) | Authority |
|--------|-----------|
| FillRoundRectCmd / FillRoundRectStrokeCmd | gpu-renderer.md |
| DrawShadowCmd | gpu-renderer.md |
| FillPathCmd / StrokePathCmd / FillGradientCmd | gpu-renderer.md |
| **DrawGradientStrokeCmd** (= 11, SHIPPED) — gradient-tinted SDF outline (the WinUI (Accent)ControlElevationBorder); reuses the GradientPipeline (`GradientInstance.Stroke` spare-pad field; 160-byte stride unchanged); carried by the sparse `_borderBrushes` side-table, driven by `BoxEl.BorderBrush` (`GradientSpec?`) | gpu-renderer.md §3.1a (struct shape + raster) / scene-memory.md §4.1 (`DrawGradientStroke` enum registration + `_borderBrushes` side-table column) |
| Push/PopLayerCmd, Push/PopClipRectCmd, Push/PopStencilClipCmd, Push/PopTransformCmd | gpu-renderer.md |
| PushLayerCmd **{Effect}** authoring (effect-chain recording semantics) | backdrop-effects-animation.md |
| DrawGlyphRunCmd — **emission** (device-space, visual order, late-UV) | text.md |
| DrawGlyphRunCmd — **consume** (batch-by-page, glyph PSO) | gpu-renderer.md |
| **DrawImageCmd** — struct SHAPE (union: ImageHandle + Dst + Radii + PlaceholderFill + CrossFade + Clip + Stretch + Flags; one Stretch enum {0=UniformToFill,1=Uniform,2=None,3=Fill}) | **gpu-renderer.md §3.1** |
| DrawImageCmd — residency / ImageRealization / placeholder-tint semantics it indirects through | media-pipeline.md |
| **DrawVideoCmd** — struct SHAPE (7-field: Surface + Dst + PosterBlur + AlbumArt + VideoReady + Radii + Clip; PassClass=VideoHole) | **gpu-renderer.md §3.1** |
| DrawVideoCmd — present/crossfade/registry consume logic | media-pipeline.md (+ pal-rhi.md present-tree) |
| **DrawFocusRingCmd** — struct SHAPE + raster (production focus visual: rounded clip-chain-anchored ring + Fluent dashed reveal; rectangular `DrawFocusRect` is a superseded placeholder) | **gpu-renderer.md §3.6/§4.4** (emitted by input-a11y.md §8.4) |
| DrawAccessKeyBadgeCmd | input-a11y.md |
| **DrawSelectionRectCmd** — struct SHAPE + raster (per-BiDi-visual-fragment text-selection highlight; `Rect`+`Radii`+`SelectionBrush`+`Affinity`+`Clip`+`Flags`; behind-text z; solid premul-linear quad, lowers onto `shape_fill`) | **gpu-renderer.md** |
| DrawSelectionRectCmd — `SelectionState` driving column + semantics | text.md (semantics) / scene-memory.md (column storage + enum registration) / input-a11y.md (selection-drag wiring) |
| **DrawFocusRing** — struct SHAPE + raster (the real Fluent focus ring on `shape_border`; one `Params0`-bit dashed/dotted reveal variant; overlay/portal composition rule) | **gpu-renderer.md** (struct/raster) / scene-memory.md (enum registration; rectangular `DrawFocusRect` retained as debug placeholder) |
| **DrawScrimCmd** — struct SHAPE + raster (overlay dismiss-layer: modal-dim / transparent light-dismiss / blur-promote; `Rect`+`Radii`+`ScrimBrush`+`Clip`+`Flags`) | **gpu-renderer.md** (struct/raster) / scene-memory.md (enum registration) / input-a11y.md (push/pop timing via overlay FSM) |
| DrawBackdropCmd (VisualKind.Backdrop stub) | backdrop-effects-animation.md |

(DrawCmd header + opcode ENUM + parallel `ulong[]` SortKeys arena + clean-span+epoch encoding: **scene-memory.md**.
QuadInstance / GlyphInstance struct layout, SortKey 64-bit field layout + batch-break rules: **gpu-renderer.md**.
The per-glyph color field of GlyphInstance: **text.md**.)

### 2.2 RHI methods

| Method / desc | Authority |
|---------------|-----------|
| Seam shapes: IGpuDevice/ISwapchain/ICommandEncoder, all POD descs, DeviceLostToken | pal-rhi.md |
| SubmitDrawList (primary hot path — consume site of opcodes) | pal-rhi.md (driven by gpu-renderer.md) |
| ICommandEncoder.CopyBufferToTexture (new) | pal-rhi.md (seam) / media-pipeline.md + text.md (drivers) |
| ResolveTexture, Present, Resize, Create*/Destroy* | pal-rhi.md |
| CreatePipeline / GraphicsPipelineDesc, root signature | pal-rhi.md (seam) / gpu-renderer.md (PSO set + shaders) |
| Texture-staging ring + startup per-bucket texture pool (AcquireBucketTexture/ReleaseBucketTexture) | pal-rhi.md (mechanism) / media-pipeline.md (residency policy) |
| AcquireAtlasPage + small-image-atlas residency/packing | media-pipeline.md (gpu-renderer.md owns only the batch-time UV-resolve ImageRef branch) |
| Multi-visual DComp present-tree + transparent premul-0 hole-punch | pal-rhi.md |
| Device-lost detection/recovery (render thread) + cross-thread rendezvous | pal-rhi.md (detection) / threading-render-seam.md (rendezvous protocol) |
| Deferred-delete / retire-fence handshake | threading-render-seam.md |

### 2.3 PAL seams

| Seam | Authority |
|------|-----------|
| IPlatformApp, IPlatformWindow, IPlatformAppLoop, NativeHandle(Kind), InputEventRing/WindowEvent shape | pal-rhi.md |
| IClipboard, IImeSession | pal-rhi.md (seam definition) / input-a11y.md (consumer + IME caret/clipboard/dragdrop use) — *AS-BUILT: `IClipboard` (SetText/TryGetText/SequenceNumber) on `IPlatformApp`; the IME session ships as `IPlatformTextInput` + `ITextInputSink`/`ImeClause` on `IPlatformWindow` (Imm32 impl, event-shaped so TSF/ITextStoreACP can replace it; full in-place TSF remains the hardening item). Selection/caret/IME-underline rendering rides the as-built `TextEditState` scene side-table + pooled rect slabs emitted as plain fills/clipped glyph re-emits (the spec'd dedicated opcodes remain the production target).* |
| **IPlatformWindow.SetCursor(CursorId) + RegisterCustomCursor** | pal-rhi.md (seam) / input-a11y.md (CursorResolver arbitration along the hit route) |
| **IPlatformLocale** (Epoch/snapshot, modeled on ISystemColors) | pal-rhi.md (seam) / text.md + dsl-aot.md (edge-localization consumer) |
| ISystemColors (accent + HC + Epoch) | pal-rhi.md (seam) / theming.md (consumer + EpochContext) |
| IBackdropSource (Mica/Acrylic) | pal-rhi.md (seam) / window-backdrop-mica.md (host-window consumer) / backdrop-effects-animation.md (in-app bake/recipe consumer) |
| IVideoPresenter (+ VideoSurfaceId) | pal-rhi.md (seam) / media-pipeline.md (VideoSurfaceRegistry + present-tree placement) |
| IVirtualMemory (reserve/commit) | pal-rhi.md (seam) / scene-memory.md + foundations.md (ChunkedArena consumer) |
| IImageCodec (WIC; `FluentGpu.Windows` Wic/) | pal-rhi.md (seam) / media-pipeline.md (decode driver) |
| IDragDropBackend, IA11yBackend, IFocusSink | input-a11y.md |
| IEffectRunner (Render seam, not PAL) | backdrop-effects-animation.md |
| IBrushSink (Scene seam, not PAL) | theming.md |
| ISceneBackend (op set + NodeChildCollection) | reconciler-hooks.md |

### 2.4 Hooks

| Hook | Authority |
|------|-----------|
| **Signals reactive core** (`Signal<T>`/`FloatSignal`/`Memo<T>`/`Effect`/`ReactiveRuntime`; AS-BUILT signals-first runtime, shipped in `FluentGpu.Foundation`) | reconciler-hooks.md §0bis |
| UseSignal / UseFloatSignal / UseComputed (signals hooks) + ReactiveComponent.Setup() + Flow.For/Flow.Show + the `Prop<T>` reactive element props (Transform/Opacity/Fill/Width/Height/Text/Color/Source/Placeholder; record shape co-owned by dsl-aot.md) | reconciler-hooks.md §0bis |
| UseState / UseReducer / UseMemo / UseCallback / UseEffect / UseLayoutEffect / UseContext / UseRef | reconciler-hooks.md |
| UseVirtual / UseInfiniteCollection / UseVisibleRange | virtualization.md (DepKey/cell semantics: reconciler-hooks.md) |
| IVirtualLayout / IMeasuredVirtualLayout (E11-L0 seam) + built-in layouts (Stack/Grid/HorizontalGrid/LinedFlow/SpanningGrid/MeasuredStack/GroupedList) | virtualization.md (as-built: src\FluentGpu.Engine\Scene\VirtualLayout.cs) |
| VirtualListEl realize lifecycle (OnItemPrepared/Clearing/IndexChanged/OnVisibleRange/OnRealized) | virtualization.md (as-built: src\FluentGpu.Engine\Reconciler\VirtualListEl.cs + Reconciler RealizeWindow) |
| SelectionModel / ItemContainer / ItemsView (E11-L3) | controls.md (selection semantics cite WinUI controls\dev\ItemsView selectors; as-built: src\FluentGpu.Controls) |
| UseImage / UseMosaic / UseVideoSurface / UseSyncedLyrics | media-pipeline.md (UseSyncedLyrics timing: backdrop-effects-animation.md) |
| UseTheme / UseSystemColors / UseHighContrast / UseDerivedBrush / UseDynamicColor | theming.md (UseDynamicColor's wantPalette trigger half: media-pipeline.md) |
| UseFocus / UseElementRef / UseCommand / UseAccelerator / UseGesture / UseAnnounce | input-a11y.md |
| **UseTransition / UseDeferredValue / StartTransition** (lanes P1) | reconciler-hooks.md (lowering: dsl-aot.md `LaneCaptureGenerator`) |
| **UseDerived / UseContextSelector** (P6 derived-state node) | reconciler-hooks.md (lowering: dsl-aot.md `DerivedCaptureGenerator`) |
| **UseOptimistic / UseActionState** (P7 optimistic UX over transition lanes) | reconciler-hooks.md (lowering: dsl-aot.md `DerivedCaptureGenerator`) |
| **UseOverlay / UsePointerCursor / UseSelectable / UseDescribedBy / UseFlowsTo** | input-a11y.md |
| **UseSpring / UseAnimatedValue / UseSharedElement / UseContentSizeAnimation / UseItemPlacementAnimation** | backdrop-effects-animation.md |
| **UseContainerSize** (container queries consumer cell) | reconciler-hooks.md (cell) / layout.md (physical resolution at WriteLayout) |
| **UseHover / UsePressed** (pure-composition, ratification-flagged) | controls.md authors; homed in FluentGpu.Hooks (reconciler-hooks.md to confirm/absorb) |
| UseWindowBackdrop | window-backdrop-mica.md (seam: pal-rhi.md IBackdropSource) |
| UseImageBackdrop / UseAcrylic / UseImplicitTransition / UseConnectedAnimation / UseDrivenAnimation / UseReducedMotion | backdrop-effects-animation.md |
| DepKey-span hook authoring surface (interceptor lowering) | dsl-aot.md (lowering) / reconciler-hooks.md (DepKey/GcDepTable semantics) |

### 2.5 Source generators & analyzers

| Generator / analyzer | Authority |
|----------------------|-----------|
| ElementTypeIdGenerator, ModifierGenerator, DiffPropsGenerator, HookDepsGenerator, ThemeBlobGenerator | dsl-aot.md |
| SceneWriterGenerator (ApplyToScene) + ApplyModifier resolver | dsl-aot.md (authored) / reconciler-hooks.md (homed in Reconciler leaf) |
| WgpuAnalyzer + CodeFixer (WGPU0001-0011 + WGPU0012-0016: transition stack-capture, self-suspending boundary, impure derived, optimistic value-struct, format-on-paint-path) | dsl-aot.md |
| LaneCaptureGenerator (UseTransition/startTransition/UseDeferredValue → LaneScope capture, await-safe ResumeLane) | dsl-aot.md (lowering) / reconciler-hooks.md (runtime semantics) |
| BoundaryGenerator (BoundaryElement [Element] + boundary-aware MountWriter) | dsl-aot.md (lowering) / reconciler-hooks.md (SuspenseElement semantics) |
| DerivedCaptureGenerator (UseDerived/UseContextSelector/UseOptimistic projection + DepKey capture) | dsl-aot.md (lowering) / reconciler-hooks.md (semantics) |
| LocalizationGenerator (declared-culture CLDR slices baked like theme blobs) | dsl-aot.md |
| ComInteropGenerator (hot-path vtable structs), ComCalleeGenerator (CCW vtables), AbiHarvest, *.comabi.json | com-interop.md |
| FGCOM0001-0008 analyzer family | com-interop.md |
| FG0001/FG0002/FG0003 (trust-ring / [Capability]) | validation.md (rules) / dsl-aot.md (FluentGpu.SourceGen hosts them) |

### 2.6 New assemblies

The repo physically ships **4 libraries + 4 satellites = 8 projects** (`src/FluentGpu.slnx`); the portable
engine modules below are **folders inside `FluentGpu.Engine`** (namespaces verbatim), the OS leaves are
**folders inside `FluentGpu.Windows`**. The **Authority** column (doc ownership of each contract) is
unchanged. Design-only assemblies not yet split out physically (Theme/Validation/Testing/Devtools/
Localization) live where their owning doc places them — Engine folders or CI-only — and are noted inline.

| Contract / module → physical home | Authority |
|-----------------|-----------|
| FluentGpu.Foundation (Handle/Slab/HandleTable/ChunkedArena/StringId) → `FluentGpu.Engine` Foundation/ | scene-memory.md (usage) / `../foundations.md` (root primitives) |
| FluentGpu.Scene (SoA SceneStore + DrawList encoding) → `FluentGpu.Engine` Scene/ | scene-memory.md |
| FluentGpu.Render (renderer + DrawList arenas + render-frame body) → `FluentGpu.Engine` Render/ | gpu-renderer.md (renderer) / threading-render-seam.md (frame body) |
| FluentGpu.Media → `FluentGpu.Engine` Media/ (+ WIC codec leaf → `FluentGpu.Windows` Wic/) | media-pipeline.md |
| FluentGpu.Theme (theming engine; design-only, lands in `FluentGpu.Engine` when split out) | theming.md |
| FluentGpu.Validation (CI-only; not a shipped library) | validation.md |
| **FluentGpu.Testing** (shipped public app-author harness — `TestHost`/simulate/assert/goldens; portable, no `#if WINDOWS`; design-only target) | validation.md |
| **FluentGpu.Controls** (the SDK controls layer; apps + Dsl's `Ui` re-export reference it; pay-per-reference trim; **refs `FluentGpu.Engine` only, TerraFX-free**). **Deps (as-shipped):** Foundation, Dsl, Hooks, **Animation, Scene, Reconciler** — *not* "Dsl/Hooks/Foundation only" (ratification: NavigationView/PageHost are `Component`s and Repeater/`Virtual` need Reconciler types; `IVirtualLayout` is in Scene). Stays **acyclic** — `VirtualListEl` is declared in Engine's Reconciler/, so `Controls → Reconciler` is one-way with no back-edge. | controls.md (content) / dsl-aot.md (assembly-graph + trim placement) |
| **FluentGpu.Devtools** (dev-only live inspector; `EnableDevtools`-gated, 0 bytes release; design-only target) | devtools.md (content) / dsl-aot.md (EnableDevtools FeatureSwitch + trim placement) |
| **FluentGpu.Localization** (CLDR slices; dropped in `Invariant` mode; design-only target) | dsl-aot.md (build placement) / text.md (ILocaleFormatter consumer) |
| FluentGpu.SourceGen (portable analyzer satellite) | dsl-aot.md |
| FluentGpu.Interop.SourceGen (+ FGCOM rules; analyzer satellite, referenced by `FluentGpu.Windows`) | com-interop.md |
| FluentGpu.Rhi + FluentGpu.Pal seams → `FluentGpu.Engine` Seams/Rhi/ + Seams/Pal/; D3D12 + Win32 impls → `FluentGpu.Windows` D3D12/ + Pal/ (+ Interop/) | pal-rhi.md |
| FluentGpu.Text seam → `FluentGpu.Engine` Seams/Text/; DWrite impl → `FluentGpu.Windows` DirectWrite/ | text.md |
| IA11yBackend leaves: UIA → `FluentGpu.Windows` Uia/; NSAccessibility → future `FluentGpu.Windows.Mac` | input-a11y.md |
| FluentGpu.Hosting (composition root, device-lost rendezvous lock, worker pool wiring) → `FluentGpu.Engine` Hosting/ (the OS backend is bound by the app composition root, `FluentGpu.WindowsApp`) | threading-render-seam.md (topology) / pal-rhi.md (device-lost lock) |
| OS-services scaffold (Notifications/Credentials/Packaging/Activation) → `FluentGpu.WindowsApi` (MSIX packaging is app-side) | — (new scaffold; no subsystem contract yet) |

### 2.7 Other cross-cutting artifacts

| Artifact | Authority |
|----------|-----------|
| 3-thread topology, SceneFramePublisher, QuarantinePolicy, retire-fence, phase→thread map, seam build order | threading-render-seam.md |
| **SceneFrame / SnapshotColumns / CopyInto POD shape** (publisher-side) | threading-render-seam.md |
| The COLUMN LAYOUT those snapshots capture (SceneStore SoA columns) | scene-memory.md |
| **Quarantine constant** = `RenderInFlightDepth + 1` (belt-and-suspenders, compile-asserted; =0 single-thread step 1) | threading-render-seam.md |
| **Canonical 13-phase loop** (…6 layout, 6.5 layout-effects, 7 animation, PUBLISH(13a), 8 record, 9 batch, 10 submit, 11 present, 12 passive-effects, 13 arena-swap) | threading-render-seam.md / `../hardened-v1-plan.md` §2.2 |
| Clean-span reuse rule (IsLive ∧ realization ContentEpoch ∧ baked-geom hash) | scene-memory.md (rule) / gpu-renderer.md (DrawList application) / reconciler-hooks.md (Mutate() chokepoint) / validation.md (witness/fault-injection) |
| Color contract (UNORM buffer / _UNORM_SRGB RTV / linear blend / premul output / text-gamma exception) | gpu-renderer.md |
| ComPtr render-thread confinement + Move-only-across-seam | com-interop.md + threading-render-seam.md |
| Footprint ratchet budgets (.mstat / sizoscope) | dsl-aot.md (budgets) / validation.md (CI gate) |
| Foundation: Handle/SlabAllocator/HandleTable/ChunkedArena/ObjectPool/StringId | `../foundations.md` (root) / scene-memory.md (engine usage) |
| **Lane bitmask (`Lane`/`Lanes`) + phase-3 update queue (`UpdateRecord`/`UpdateQueue` MPSC ring; auto-batching)** | reconciler-hooks.md (semantics) / scene-memory.md (`UpdateQueueSlab`/`UpdatePayloadTable` storage; GC-ref updater via `GcDepTable`) / dsl-aot.md (`LaneCaptureGenerator`) |
| **Suspense boundary** (`SuspenseElement`/`SuspenseReveal`/`SuspenseSlot`/`SuspenseState`; `VisualKind.SuspenseAnchor` + `SuspenseAnchor` NodeFlags) | reconciler-hooks.md (semantics) / scene-memory.md (`SuspenseSlot` column + VisualKind/NodeFlags storage) / dsl-aot.md (`BoundaryGenerator`) / backdrop-effects-animation.md (keep-stale cross-fade) |
| **External-store snapshot/version contract** (`IExternalStore<TSnapshot>` (subscribe,getSnapshot,Version) + `StoreReadLedger` pre-PUBLISH tear re-check + demote-to-blocking) | threading-render-seam.md (§12bis) / reconciler-hooks.md (UseObservable/UseResource consume) / pal-rhi.md (ISystemColors/IPlatformLocale Epoch instances) |
| **SelectionState** (anchor/extent/affinity; sparse side-table, NOT a NodePaint field; `Mutate(SelectionHandle,…)` chokepoint + selection clean-span witness) | scene-memory.md (column storage) / text.md (semantics + read-side) / input-a11y.md (drag wiring) / gpu-renderer.md (DrawSelectionRectCmd raster) |
| **FlowDirection** (enum + `FlowState` hot column + `LayoutPacked.ResolvedFlowIsRtl` bit; RTL resolved at WriteLayout) | scene-memory.md (column storage) / layout.md (resolution + `ResolveLogical`/`MirrorEdges`) / reconciler-hooks.md (`Context<FlowDirection>` plumbing) |
| **A11y collection relations** (`SetSize/PositionInSet/Level/DescribedBy/FullDescription/FlowsTo/HeadingLevel/LandmarkType` via cold `A11yRel` slab) + virtualized-provider realization-on-Navigate | scene-memory.md (column storage) / input-a11y.md (UIA projection + realization) / layout.md (virtualizer index+count feed) |
| **Gesture arena** (`GestureArena`/`ArenaMember`/`ArenaVote`/`ArenaTeam`; tentative-capture-until-resolution; `e.Handled` becomes an Accept vote) | input-a11y.md |
| **Overlay/portal manager** (`OverlayManager`/`OverlayEntry`/`OverlayKind`/`DismissPolicy`; light-dismiss FSM; focus push/restore; UIA Window/Menu/ToolTip) | input-a11y.md (manager + FSM) / layout.md (`OverlayPlacement.Resolve` flip→nudge→constrain geometry) |
| **Springs/retarget/shared-element/motion-tokens** (`SpringParams`, `AnimTrack` 48→64B, `SharedElementRegistry`, `MotionToken`/`MotionTokenTable`, `ReducedMotionPolicy`; rides existing LocalTransform/Opacity/EffectAux columns) | backdrop-effects-animation.md (semantics) / theming.md (token table pattern) / dsl-aot.md (`MotionTokenId` gen) |
| **`FluentGpu.Testing` app-author harness** (`TestHost`/`TestHostOptions`/simulate/assert/MatchGolden; shipped public) | validation.md |
| **`FluentGpu.Devtools` quarantine participation** (`QUARANTINE = RenderInFlightDepth + (devtools ? 1 : 0) + 1`; reverts on detach; never in release) | devtools.md (derives, never hard-codes) / threading-render-seam.md (canonical quarantine relationship) |

---

## 3. Reading order for an implementer (15 steps)

1. **`../foundations.md` §1-7** — the shared vocabulary (handles, allocators, ChunkedArena, StringId, ISceneBackend, acyclic-assembly invariants). Nothing below makes sense without it.
2. **`../hardened-v1-plan.md` §2 + §4** — the canonical 13-phase threading model and the five hardenings. This overrides the older "single UI/render thread" prose and foundations.md's 12-phase numbering everywhere.
3. **threading-render-seam.md** — the concrete 3-thread topology, SceneFrame/SnapshotColumns publish, consume-gated quarantine (`RenderInFlightDepth + 1`), retire-fence, phase→thread map, and the **seam build order** (single-thread-correct first, quarantine=0, flip parallelism only behind the green race gate). Read this before any subsystem so you know which thread your code runs on.
4. **`../dotnet10-csharp14-zero-alloc.md` §4 + com-interop.md** — the COM ruling (generated-from-comabi.json, ComPtr confinement) plus the AOT/zero-alloc baseline. These constrain how every leaf binds native APIs.
5. **scene-memory.md** — the SoA SceneStore, the Foundation memory model in use, the DrawList encoding framework (header/arenas/sortkey array/opcode enum/clean-span rule) and the column layout the snapshot captures. The data substrate everything else reads and writes.
6. **dsl-aot.md** — the authoring surface (Element/modifiers/UI factory) and the source-generator/AOT baseline you build against day to day.
7. **reconciler-hooks.md** — the Reactor runtime: reconciler, hooks, DepKey/GcDepTable, memo skip, effect timing (6.5 + 12), the `Mutate()` epoch chokepoint, ISceneBackend op set.
8. **layout.md** — the incremental Yoga/Grid engine that produces the Bounds phase-6.5 layout effects and hit-testing read, plus the scroll-ownership split and the virtualization layout participant.
9. **virtualization.md** — lists/grids/grouping/incremental-load: the UseVirtual family consumers, O(1) recycle, the decode→measure→relayout anti-loop guard, consume-gated DestroyNode.
10. **pal-rhi.md** — the platform/window/device seams, the present tree, CopyBufferToTexture/staging-ring/bucket-pool mechanism, and device-lost detection that everything renders through.
11. **gpu-renderer.md** — the batched renderer that consumes the DrawList: opcode payload shapes (incl. DrawImageCmd/DrawVideoCmd), SortKey, batcher, shaders, color contract, clip/AA, clean-span application, the `ImageRef` UV-resolve branch. The hot path SubmitDrawList lives here (driving pal-rhi).
12. **text.md** — glyph pipeline + DWrite leaf + lyrics, feeding DrawGlyphRunCmd into the renderer.
13. **media-pipeline.md → theming.md → backdrop-effects-animation.md → window-backdrop-mica.md** — the WaveeMusic-driven feature layers (images/video + ImageRealization/atlas residency, dynamic color, in-app backdrop/effects/animation, host-window Mica), in dependency order (media → theme palette → in-app backdrop bakes → window backdrop).
14. **input-a11y.md** — input/focus/IME/UIA, which read committed Bounds and feed events back through the ring.
15. **validation.md** — the spikes + CI gates that keep all of the above honest; read last but wire early, since "production safety == CI coverage."
