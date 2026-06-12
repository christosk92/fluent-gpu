# Render pipeline and SceneRecorder

[← Contributing to the engine](./index.md) · [Reconciler and the retained scene](./reconciler-and-scene.md) · [Layout internals](./layout-internals.md)

This page is for engine contributors changing the **record half** of the frame — phase 8 and its neighbours. It tells you
how [`SceneRecorder`](../../../src/FluentGpu.Engine/Render/SceneRecorder.cs) walks the retained scene into a `DrawList`, what the
DrawList POD stream is (and who owns its opcode shapes), the rounded-rect SDF pipeline rules that every `BoxEl` rasterizes
through, the color/coordinate contract those pipelines honour, why a bound transform re-records without relayout, how
record stays allocation-free, and exactly how to verify a change in the headless harness.

It does **not** restate opcode payload shapes or the color/DPI contract — those are owned by the design corpus and linked
inline. The rule on this page is the same one the whole corpus runs on: **define an artifact in its owner; reference it
everywhere else.** A `DrawList` opcode's fields live in [`gpu-renderer.md`](../../../design/subsystems/gpu-renderer.md);
the byte container that holds them lives in [`scene-memory.md`](../../../design/subsystems/scene-memory.md); this page is
the working view of the code that fills the container.

> **Where to change what.** Record walk + DrawList emit: `src/FluentGpu.Engine/Render/SceneRecorder.cs`. The POD command stream
> + `DrawOp` enum + `DrawList` writer: `src/FluentGpu.Engine/Render/DrawList.cs`. The rounded-rect / gradient SDF pipelines and
> their HLSL: `src/FluentGpu.Windows/D3D12/RoundRectPipeline.cs` and `GradientPipeline.cs`. The headless decoder you assert
> against: `src/FluentGpu.Engine/Headless/Rhi/HeadlessGpuDevice.cs`. The harness that drives them: `src/FluentGpu.VerticalSlice/Program.cs`.

---

## The frame loop, phases 6–12

The record pass does not stand alone — it consumes a scene the earlier phases have already settled. The full single-UI-thread
loop is owned by [rendering-and-performance.md](../../guide/rendering-and-performance.md) (the as-built map) and the
[frame pipeline page](./frame-pipeline-and-verification-harness.md); the slice that ends in pixels is phases 6–12, driven
from `AppHost.RunFrame` → `Paint`:

```
6    layout          full Run only on first-frame/resize/DPI/root change; else SCOPED relayout of dirty subtrees
6.5  layout effects  UseLayoutEffect (Bounds now valid) — seeds animations on nodes
7    animation        AnimEngine.Tick: writes Transform / Opacity / presented-size / FLIP projections / smooth scroll
8    record           SceneRecorder.Record walks the scene → DrawList (clips, world transforms, glyphs, images)
10   submit           DrawList bytes → GPU command list (RHI SubmitDrawList)
11   present          swapchain commit
12   passive effects  UseEffect
```

Two facts about phase 8 set everything below:

- **Record reads only the settled scene.** By the time `Record` runs, layout has filled every node's `Bounds` and animation
  has written every node's `LocalTransform`/`Opacity`. The recorder *derives* world transforms top-down during the walk; it
  never mutates the scene. That is why it is safe for the render thread to own this phase reading an immutable snapshot in
  the hardened topology ([`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) §2, threading row) — record is pure over its input.
- **Animation writes Transform/Paint, never Layout.** A frame whose only change was a bound transform/opacity (or an
  animation tick) sets `TransformDirty`/`PaintDirty` only, so phases 6/6.5 do no work and the frame is "record-only". This
  is the compositor bypass (see [below](#compositor-bypass-re-record-without-relayout)).

---

## SceneRecorder: walking the scene into a DrawList

`SceneRecorder.Record` resets the `DrawList`, then recurses the tree from `scene.Root` via the private `Walk`, carrying the
**parent world transform**, the **cumulative opacity**, the **active device-space clip rect**, and a **painter-order depth**
down each branch. It composites *like a browser*: every node's own geometry is emitted in **local space** with a world
transform and a cumulative alpha, so a transform/opacity change re-records cheap matrices without re-recording content.

### World transform: `parent ∘ translate(pos) ∘ LocalTransform` about the origin

The core composition in `Walk` (`SceneRecorder.cs`) is:

```csharp
// node-local → device: parent ∘ translate(node pos) ∘ (local transform about the node's transform-origin)
Affine2D world = parentWorld.Multiply(Affine2D.Translation(b.X, b.Y));
float ox = b.W * p.OriginX, oy = b.H * p.OriginY;        // transform origin (default centre)
if (!p.LocalTransform.IsIdentity)
    world = world.Multiply(Affine2D.Translation(ox, oy)).Multiply(p.LocalTransform).Multiply(Affine2D.Translation(-ox, -oy));
```

`Affine2D` is the engine's 2×3 affine ([`src/FluentGpu.Engine/Foundation/Geometry.cs`](../../../src/FluentGpu.Engine/Foundation/Geometry.cs)).
Its `Multiply` is `this ∘ other` (apply `other` first), and `TransformBounds` gives the device-space AABB of a local rect —
the recorder uses it for every cull/clip test. `Bounds` are **node-LOCAL** ([`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md)
§2, color/coordinate row), and `LocalTransform` maps local→parent, which is exactly why the recorder rebuilds `world` on the
way down instead of reading a stored absolute matrix.

On top of the local transform, `Walk` also composes (in order): an **interaction-driven scale** (hover/press thumb-grow,
eased by the node's own or nearest interactive ancestor's progress via `TryResolveInteractionProgress`), a **`CounterScaled`**
inverse for children that opted out of an ancestor's animated scale (Framer-Motion-style projection), and a **child-group
shift** (`ChildShiftX/Y`, the `SizeMode.Reflow` trailing-anchor slide) applied only to the children's world, never the node's
own fill.

### Opacity: cumulative, with flat opacity groups for overlap

Opacity multiplies down the tree: `opacity = parentOpacity * ResolveOpacity(...)`. `ResolveOpacity` eases hover/press opacity
targets when present, else returns the resting `Opacity`. The default model is **per-node multiplied alpha** (WinUI's plain
`Visual.Opacity`).

When a node sets `OpacityGroup` and its cumulative alpha is `< 1`, the recorder instead emits a **flat opacity layer**: the
subtree renders at full alpha into a pooled offscreen and composites **once** at the group alpha, so overlapping children
(a fading dialog plate + its buttons) don't double-blend. `Walk` pushes the layer, resets local `opacity` to `1f` for
everything inside, and pops it last:

```csharp
bool isOpacityGroup = p.OpacityGroup && opacity < 0.999f && deviceBounds.Overlaps(clip);
if (isOpacityGroup) { dl.PushOpacityLayer(deviceBounds, p.Corners, opacity, key); opacity = 1f; }
```

This is `LayerKind.Opacity` on the shared `PushLayer`/`PopLayer` opcode pair (acrylic is `LayerKind.Acrylic` on the same
pair) — see `DrawList.cs` and the layer-semantics note in [gpu-renderer.md §7.1](../../../design/subsystems/gpu-renderer.md).

### Clip intersection: scissor, plus a tier-2 rounded clamp

A clipping node intersects the active clip with its own device bounds (and with an authored `ClipRect` if present) and
pushes the result. The recorder always feeds the RHI a clip rect that is **already intersected with the enclosing clip**, so
the backend just sets the scissor — it never has to walk a clip stack:

```csharp
if ((flags & NodeFlags.ClipsToBounds) != 0)  childClip = childClip.Intersect(deviceBounds);
if (!p.ClipRect.IsInfinite)                   childClip = childClip.Intersect(world.TransformBounds(p.ClipRect));
```

When the clipping node has rounded corners, `Walk` emits `PushClipRounded` instead of `PushClip`: the scissor still clamps
rectangularly, *and* RoundRect-pipeline primitives additionally clamp to the node's rounded-box SDF. The honest scope is
documented on `ClipCmd` — the rounded clamp covers the rounded-rect pipeline only; glyphs, images, gradients, arcs, and
polylines still clip by scissor. That caveat is real; don't paper over it when you touch the clip path.

### Emit order within a node (subtle, load-bearing)

`Walk` emits a node's primitives in a deliberate order, each justified by a real bug. Keep it:

1. **Shadow** — *before* this node pushes its own clip (a `ClipsToBounds` surface would otherwise scissor its own soft halo
   away), still bounded by the parent clip.
2. **Arc** (ProgressRing) when present.
3. **Push clip** (scissor, or tier-2 rounded).
4. **Acrylic layer** (`PushLayer` with the AcrylicBrush recipe) when present.
5. **Self visual** — the `VisualKind` switch (`Box` fill, `Text` glyph run + decorations + selection/caret, `Image`,
   `PolylineStroke`). The **border ring is deferred** here (its parameters are stashed in `pending*` locals).
6. **Children** (unpinned first, then `StickyPinned` children, so sticky content paints over what scrolls beneath it).
7. **Border ring** — *after* descendants, so a control border stays visible over filled child regions.
8. **Pop acrylic / pop clip.**
9. **Focus ring** — *after the clip pops*, because the WinUI focus visual lives outside the bounds (`FocusVisualMargin -3`),
   so a `ClipsToBounds` field must not scissor away its own ring. Ancestor clips still apply.
10. **Scrollbar overlay** — after the content clip pops, so the expanded gutter/thumb aren't chopped at the viewport edge.
11. **Pop opacity group** (last).

`Record` also handles three things outside the main walk: **exit orphans** (removed subtrees kept alive for their exit
animation, drawn behind the live tree at their frozen parent origin), the **drag ghost** (re-walked last in an *infinite*
clip so a row dragged out of a clipped list keeps drawing above everything), and **windowed-popup skip-roots** (`RecordSubtree`
re-origins one subtree into its own popup swapchain's DrawList; the subtree stays in the one `SceneStore`).

---

## The DrawList POD command stream

The DrawList is a flat byte stream: `[int op][POD payload]` per command, with a **parallel `ulong[]` of sort keys** (one per
command). `DrawList.Record` resets, `WriteOp` writes the 4-byte opcode, `WritePayload<T>` blits the unmanaged payload, and
`PushSort` appends the painter-order key:

```csharp
public ReadOnlySpan<byte>  Bytes     => _buf.AsSpan(0, _len);
public ReadOnlySpan<ulong> SortKeys  => _sort.AsSpan(0, _sortLen);
public int CommandCount { get; private set; }
public void Reset() { _len = 0; _sortLen = 0; CommandCount = 0; }
```

The `DrawOp` enum and the per-opcode writer methods (`FillRoundRect`, `DrawGlyphRun`, `PushClip`/`PushClipRounded`/`PopClip`,
`DrawImage`, `StrokeRoundRect`/`StrokeRoundRectDashed`, `Shadow`, `GradientRect`, `GradientStroke`, `PushLayer`/`PushOpacityLayer`/`PopLayer`,
`Arc`, `PolylineStroke`, `TabShape`) all live in `DrawList.cs`. Each payload is a `readonly record struct` of POD only —
geometry, colors, the **composited world `Affine2D`**, the cumulative `Opacity`, and **handle/index references** (e.g.
`StringId Text`, `int ImageId`), never a GC pointer. That POD-and-handles-only rule is what lets the threading seam `memcpy`
a clean span across threads.

### Ownership: where opcode shapes are defined (do not restate them here)

This is the single most important canon rule for this page. The split is exact:

- [`scene-memory.md` §4](../../../design/subsystems/scene-memory.md) owns the **encoding framework** — the command header,
  the render-private byte arenas, the parallel `ulong[]` sort arena, and the **`DrawOp` enum list registration**.
- [`gpu-renderer.md` §3](../../../design/subsystems/gpu-renderer.md) owns each opcode's **payload struct shape** and the
  **64-bit SortKey layout** and the **rasterization** of each lane.

So when you add or change an opcode: register the enum entry in `scene-memory.md`, define/own its payload shape + raster in
`gpu-renderer.md`, then implement the writer in `DrawList.cs` and the emit in `SceneRecorder.cs`. **Never** paste a payload's
field list into this page or any working doc — that restatement is exactly the drift the canon gate and reviews catch. (Note
the production stream in this repo uses a 4-byte `int` opcode tag, not the design's 8-byte `DrawCmd` header; the design's
SortKey bit-packing is the render-thread batcher's contract — the slice's parallel `ulong[]` is the same idea at slice scope.)

---

## The rounded-rect SDF pipeline rules

Every `BoxEl` rasterizes through **one** SDF rounded-rect pipeline: [`RoundRectPipeline`](../../../src/FluentGpu.Windows/D3D12/RoundRectPipeline.cs)
for solid, [`GradientPipeline`](../../../src/FluentGpu.Windows/D3D12/GradientPipeline.cs) for gradient. A unit quad is drawn
instanced; the VS positions it and the PS evaluates the analytic rounded-box SDF with single-pass derivative AA. Three rules
govern how the recorder feeds them — each is the fix for a real, named bug; **do not regress them** (they are also the rules
in [control-fidelity.md §1](../../guide/control-fidelity.md)):

### 1. Borders are a hollow SDF ring, never a filled donut

`SceneRecorder` always `FillRoundRect`s the **full** interior with the fill, then draws **one** ring on top of the fill edge
(`EmitBorderRing` solid / `EmitGradientBorderRing` gradient). The old "fill the whole box with the border colour, then overlay
an inset interior" donut **bled the border through any translucent fill** (the unchecked-CheckBox grey-chip). A hollow ring
composites correctly over *any* fill opacity. If you ever see a border-coloured fill, the donut is back.

### 2. The stroke ring's corner radius shrinks by `bw/2` (`InsetCorners`)

A centerline SDF stroke insets the rect by `bw/2`; to keep the band **concentric** with the box's rounded corner, the corner
radius must shrink by the **same** `bw/2`, else 1px corners read rough/uneven. That is `EmitBorderRing` + the private
`InsetCorners` helper in `SceneRecorder.cs`:

```csharp
private static CornerRadius4 InsetCorners(in CornerRadius4 c, float d)
    => new(MathF.Max(0f, c.TopLeft - d), MathF.Max(0f, c.TopRight - d), MathF.Max(0f, c.BottomRight - d), MathF.Max(0f, c.BottomLeft - d));

private static void EmitBorderRing(DrawList dl, in RectF local, in RectF b, in CornerRadius4 corners, float bw, in ColorF border, in Affine2D world, float opacity, ulong key)
    => dl.StrokeRoundRect(new RectF(bw * 0.5f, bw * 0.5f, MathF.Max(0f, b.W - bw), MathF.Max(0f, b.H - bw)),
                          InsetCorners(corners, bw * 0.5f), border, bw, world, opacity, key);
```

The same `InsetCorners` discipline governs the dual focus ring (`EmitFocusRing`): the 2px primary band and the 1px secondary
band each inset by half their thickness, and the corner radius **grows** with the focus-rect expansion so the arcs stay
concentric with the control corner.

### 3. The SDF quad is inflated by `stroke/2 + AA margin` in the vertex shader

A bare rect quad clips the outer half of the stroke band plus its AA feather — invisible on straight edges, but it slices the
square quad corner through a rounded band, so corners and pill-ends read rough. Both pipelines inflate the quad outward in
`VSMain`:

```hlsl
// RoundRectPipeline.cs / GradientPipeline.cs — VSMain
float margin = (it.stroke > 0.0 ? it.stroke * 0.5 : 0.0) + 2.0;   // contain the outline's outer half + the AA feather
float2 dir = corner * 2.0 - 1.0;                                  // -1 at corner 0, +1 at corner 1 (outward)
float2 lp  = it.pos + corner * it.size + dir * margin;            // inflated local-space point
```

The PS then evaluates `SdRoundBox`, derives `aa = fwidth(d)`, and computes coverage. For a **fill** it is a crisp ~1px linear
AA (`cov = clamp(0.5 - d/fw)`); for a **ring** it is a band centred on the edge (`cov = clamp(0.5 - (abs(d) - stroke*0.5)/fw)`)
— the same formula the gradient PS swaps in when `Stroke > 0`. The single `RoundRectPipeline` PS also carries the E9 variants
in the same shader: dashed outlines (perimeter arc-length modulation), the checkerboard fill (ColorPicker alpha lane), the
WinUI selected-tab shape, and the **tier-2 rounded-clip coverage clamp** (`i.clip.z > 0` multiplies coverage by the clipping
rounded-box SDF). One PS family, variants selected per-instance/per-batch — not a per-fragment runtime tree.

The `InsetCorners`-and-inflation pair is also why `EmitGradientBorderRing` draws its band on a rect inset by `bw*0.5` with
`InsetCorners(corners, bw*0.5)` and a stroke width of `bw`: the gradient elevation border (WinUI `ControlElevationBorderBrush`)
sits *inside* the bounds, exactly like the solid ring.

---

## The color, coordinate, and DPI contract

The canonical color/coordinate/DPI contract is owned by [foundations.md P8 + architecture-spec §1.3 bet 4](../../../design/SPEC-INDEX.md)
(see the `SPEC-INDEX.md` color row). This page only notes **where the code honours it** so you don't break it by accident:

- **Buffer / RTV.** Both pipelines create the PSO with `RTVFormats[0] = DXGI_FORMAT_B8G8R8A8_UNORM` (`BuildPipeline` in
  `RoundRectPipeline.cs` / `GradientPipeline.cs`). The canon is a `BGRA8_UNORM` buffer with a `BGRA8_UNORM_SRGB` RTV so the
  hardware does the sRGB encode on write and blend/resolve happen in linear — the swapchain/RTV wiring is the backend's job;
  the pipeline only fixes the buffer format here.
- **Premultiplied, linear blend.** The blend state is `SrcBlend = ONE`, `DestBlend = INV_SRC_ALPHA` (premultiplied SrcOver),
  and the PS returns premultiplied output: `float aOut = baseCol.a * cov * opacity; return float4(baseCol.rgb * aOut, aOut);`.
  Every cross-fade the recorder computes (hover/press ramps, implicit `BrushTransition`, gradient stop morphs) goes through
  `ColorF.LerpLinear` — **linear-light, alpha-weighted** premultiplied blend (`Geometry.cs`), not a straight sRGB lerp. That
  weighting is load-bearing: a translucent white card fill cross-fading to an opaque dark solid must stay dark mid-flight; a
  straight per-channel lerp would flash bright half-transparent grey. `ColorF` itself stores **straight-alpha sRGB** (the
  authoring/dedup convention); linearization happens at blend/shader.
- **Coordinates / DPI.** `Bounds` are node-LOCAL; the recorder builds device-space world transforms during the walk. DPI is
  applied once at the layout→world step (the canon), not in the recorder. The VS maps the inflated local point through the
  2×3 affine, then to NDC: `ndc = float2(world.x/gViewport.x*2-1, 1 - world.y/gViewport.y*2)`.
- **Text gamma is the deliberate exception.** Glyph coverage blends with the text-gamma exception the canon calls out; the
  rounded-rect/gradient lanes are plain linear-premultiplied.
- **Acrylic.** In-app acrylic math is portable and headless-verifiable in [`AcrylicBackdropMath`](../../../src/FluentGpu.Engine/Render/AcrylicBackdropMath.cs)
  (`DownsampleFactor`, `SnapshotRegion`, `BucketDim`, the fixed `KernelSigma = 7.5` separable kernel taps) so the GPU leaf's
  blur is a thin consumer; the COM/HLSL stays render-thread-confined. The recorder emits the frosted surface via the
  acrylic `PushLayer` (the WinUI `AcrylicBrush` recipe fields: tint / fallback / tint-opacity / blur-sigma / noise / luminosity).

---

## Compositor bypass: re-record without relayout

This is the payoff of the local-space + world-transform model, and it is the thing you must not accidentally defeat when you
edit the recorder. The three dirty axes (`NodeFlags`, owned by [scene-memory.md §2.3](../../../design/subsystems/scene-memory.md))
decide how cheap a frame is:

| Flag | Set by | Frame does |
|---|---|---|
| `TransformDirty` / `PaintDirty` | a `Transform`/`Opacity`/`Fill` bind, an animation tick, hover/press | **re-record only** — no layout, no reconcile |
| `LayoutDirty` | a component re-render, a width/height/text bind, a structural change | scoped relayout of the affected subtree |
| (structural) | mount/remove/reorder in reconcile | reconcile that subtree + scoped relayout |

Because the recorder emits content in local space and re-derives world matrices on the walk, a frame whose only change is a
bound transform/opacity write does **nothing** in phases 3/6 — it skips render, reconcile, and layout, and just re-records
(`FrameStats.Rendered == false`). High-frequency scalars (slider scrub, scroll offset, hover glow) should be bound to a
`Transform`/`Opacity`, never pushed through `setState`. The app-side mental model and the `Prop.Of(() => Affine2D.Translation(...))`
pattern are in [rendering-and-performance.md → compositor bypass](../../guide/rendering-and-performance.md); from the engine
side, the contract you preserve is: **the recorder must read the freshest `LocalTransform`/`Opacity`/`Fill` every frame and
must not require any layout state that a transform-only tick failed to update.** It already does — keep it that way.

---

## Zero-alloc in record

Phases 6–13 must not allocate managed memory on a steady frame (`FrameStats.HotPhaseAllocBytes == 0`, asserted by the
harness — the near-zero-allocation guarantee; see the [performance-rules page](../app-authors/performance-rules-and-zero-alloc-boundaries.md)
and the `SPEC-INDEX.md` "0 managed allocations in frame phases 6–13" binding). The record path holds to this by construction:

- **`Walk` is a `static` recursion over `ref`-returning column accessors** (`scene.Bounds(node)`, `scene.Paint(node)`,
  `scene.Flags(node)`) — it reads SoA rows by `ref`, boxing nothing.
- **Per-node scratch is `stackalloc`/stack locals.** `Record` builds the skip-root span with `stackalloc NodeHandle[...]`;
  `RemapAbsoluteAxis` uses `stackalloc float[4]`/`ColorF[4]`; gradient stop blends (`LerpStops`) interpolate four stack locals,
  never a new `GradientSpec`. The comments call this out explicitly ("stack-only, zero alloc").
- **The `DrawList` reuses its buffers.** `Reset` keeps capacity; the byte buffer is `GC.AllocateUninitializedArray<byte>`
  (grown by doubling only when a frame genuinely overflows it — a warmup cost, not a steady-state one); `MemoryMarshal.Write`
  blits each POD payload in place. In the full engine these are render-thread-private, ≥3-deep `ChunkedArena`s
  ([`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) DrawList-arenas row); the slice grows one contiguous buffer, same contract.
- **The leaf upload ring is persistently mapped.** `RoundRectPipeline`/`GradientPipeline` keep an upload-heap structured
  buffer mapped once (`_mapped`) and memcpy instances into it per frame — no per-frame GPU allocation.

The practical rule when you edit the recorder: **do not `new`/box/LINQ inside `Walk` or any `Emit*` helper.** If you need a
buffer, `stackalloc` it or stash it in a pre-sized field. A regression here is caught — see below.

---

## Verifying: balance counters, per-opcode logs, the named checks

Verify with the **headless harness**, never by eye. [`HeadlessGpuDevice`](../../../src/FluentGpu.Engine/Headless/Rhi/HeadlessGpuDevice.cs)
is the structural test backend: `SubmitDrawList` decodes the POD stream into reusable, inspectable command lists (capacity
retained → no per-frame alloc after warmup) and tracks push/pop balances. The workflow in the harness
(`src/FluentGpu.VerticalSlice/Program.cs`) is: build a scene, `SceneRecorder.Record(scene, dl)`, `dev.SubmitDrawList(dl.Bytes, dl.SortKeys, frame)`,
then assert on the decoded lists.

### Per-opcode command logs

The device exposes one list per opcode family — assert *what was drawn* without pixels:

| Property | What it logs |
|---|---|
| `LastRects` | `FillRoundRectCmd`s (solid fills) |
| `LastStrokes` / `LastStrokeClipDepths` | `DrawRoundRectStrokeCmd`s (focus rings / borders) + the clip-stack depth at each stroke |
| `LastGlyphs` | `DrawGlyphRunCmd`s |
| `LastClips` | every `PushClip` (rounded clips visible via `ClipCmd.CornerRadius`/`RoundedRect`) |
| `LastImages` | `DrawImageCmd`s (`Ready==0` ⇒ placeholder) |
| `LastShadows`, `LastArcs`, `LastPolylines` | shadows, ProgressRing arcs, stroked polylines |
| `LastGradients`, `LastGradientStrokes` | gradient fills, gradient elevation-border bands |
| `LastLayers` | `PushLayerCmd`s — acrylic (Kind 0) **and** flat opacity groups (Kind 1, `GroupAlpha`) |
| `LastTabShapes` | WinUI selected-tab shapes |

### Clip / layer balance counters

`ClipBalance` and `LayerBalance` must each be **0** at the end of a well-formed frame — the device increments on `PushClip`/`PushLayer`
and decrements on `PopClip`/`PopLayer` while decoding. They are the cheap structural guard that the recorder's push/pop
pairing is sound (a missing `PopClip` would scissor the rest of the frame). `LastStrokeClipDepths` is the parallel guard for
the focus-ring-outside-its-clip rule: it records the clip depth at the moment each stroke decoded, so a check can assert a
`ClipsToBounds` field's focus ring records at its *parent's* depth, not inside its own clip.

### The named checks to read and extend

Two harness functions are the model for any rounded-rect/border/stroke change you make — read them before adding your own:

- **`GradientBorderChecks`** (`Program.cs`) — check **57** asserts the gradient elevation border emits exactly one
  `DrawGradientStroke` band with the ring inset by `bw/2` (`Near(gs.Rect.X, 0.5f) && Near(gs.Rect.W, 119f)` for a 120-wide
  box, `bw=1`) **and** the fill drew once over the full bounds (the hollow-ring rule). **57b** covers a gradient-only fill,
  **57c** the chrome paint order (parent border records *after* descendant fills — it walks `dl.Bytes` opcode-by-opcode),
  and **57e** the absolute-axis (`MappingMode=Absolute`) elevation band including the `AnchorEnd` mirror.
- **`PolylineStrokeChecks`** (`Program.cs`) — check **57d** asserts a `PolylineStroke` emits one `DrawPolylineStroke` with the
  expected `PointCount`/`TrimEnd`, then drives `AnimEngine` keyframes on `AnimChannel.StrokeTrimEnd` and asserts the recorded
  `TrimEnd` tracks the eased value mid-flight (`t16 > 0 && t16 < 0.35`) — proving the recorder reads the animated paint
  channel each frame.

Run the whole spine after any change:

```bash
dotnet build src/FluentGpu.VerticalSlice          # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice # expect: ALL CHECKS PASSED
```

Add a `Check("…", condition, detail)` for the behavior you changed and call it from `Main`. If your change is record-only,
also assert on `FrameStats` (`Rendered == false` for a compositor-bypass frame, `HotPhaseAllocBytes == 0` for a steady one).
On-screen D3D12 pixels are a separate manual "needs-pixels" pass on the real Windows path
([control-fidelity.md §4](../../guide/control-fidelity.md) describes the `--screenshot`/`--shot` route and the slow-motion
proof for animations) — the harness proves the *recorded DrawList* is correct, not that a control looks right on screen.
Keep both bars distinct.

---

## Canon links

- **Encoding framework** (DrawList container, `DrawOp` enum registration, byte arenas, sort arena, clean-span reuse rule):
  [`scene-memory.md` §4](../../../design/subsystems/scene-memory.md).
- **Opcode payload struct shapes + SortKey layout + per-lane rasterization** (`FillRoundRectCmd`, `DrawGlyphRunCmd`,
  `DrawImageCmd`, `PushLayerCmd`, `DrawGradientStrokeCmd`, the SDFs, the RenderLane classifier, the clip tiers, layers/acrylic):
  [`gpu-renderer.md`](../../../design/subsystems/gpu-renderer.md).
- **Scene columns & `NodeFlags`** (the 3 dirty axes, `NodePaint`, `VisualKind`): [`scene-memory.md` §2](../../../design/subsystems/scene-memory.md).
- **Color / coordinate / DPI contract**: the `SPEC-INDEX.md` color row → [foundations.md P8 + architecture-spec §1.3](../../../design/SPEC-INDEX.md).
- **Frame loop & the compositor-bypass mental model** (app-author view): [rendering-and-performance.md](../../guide/rendering-and-performance.md);
  engine-side neighbours: [reconciler & scene](./reconciler-and-scene.md), [layout internals](./layout-internals.md),
  [the seams](./seams-rhi-pal-text.md), [Windows backends](./windows-backends.md), and the
  [frame pipeline & verification harness](./frame-pipeline-and-verification-harness.md).
- **Rounded-rect rendering rules in control terms** (the three bug-fix rules, the state ramps, the motion engines):
  [control-fidelity.md](../../guide/control-fidelity.md).

Do not restate an opcode shape, a payload's fields, or the SortKey bit layout on this page — link to the owner. That is the
single-owner discipline the whole corpus runs on.
