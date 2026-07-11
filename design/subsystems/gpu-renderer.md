# FluentGpu — Subsystem Design: GPU 2D Rendering Engine (the custom batched renderer)

> **ACTUALIZED v1 (hardened).** This is the current, self-contained design for the batched 2D
> renderer. It supersedes the original synthesis: it folds the [hardened-v1-plan](../hardened-v1-plan.md)
> threading seam, the §4 spec amendments from [architecture-spec](../architecture-spec.md), the
> [.NET 10 / C# 14 zero-alloc patterns](../dotnet10-csharp14-zero-alloc.md), the WaveeMusic media
> fold-ins ([app-requirements](../app-requirements-waveemusic.md)), and the
> [painpoints](../winui-painpoints-assessment.md) overclaim corrections. A *Changed vs the original
> synthesis* section at the end lists every amendment.

**Primary assembly:** `FluentGpu.Render` (portable C# math + POD + RHI/Text/PAL interface calls).
**Leaf impl:** `FluentGpu.Windows` (D3D12/ folder; Windows-only; ComPtr/D3D12/DXGI/DComp). **New collaborators:**
`FluentGpu.Media` (image/video residency, portable), leaf `FluentGpu.Windows` (Wic/ folder), `FluentGpu.Theme`
(brush derivation), `FluentGpu.Validation` (golden/structural gates, `[Conditional]`-erased from ship).

This renderer **runs on the RENDER thread** (phases 8–11) reading an immutable `SceneFrame` snapshot
published by the UI thread; it owns every `ComPtr` (single-writer refcount). The build order ships
**single-thread-correct first** (UI thread produces+consumes; quarantine=0) and flips parallelism only
behind a green race gate — see hardened-v1-plan §6. **Cross-cutting contracts (threading, COM, memory,
scene/drawlist, RHI/PAL seam, color, hooks/reconcile, language/AOT) are owned by the referenced docs;
this doc designs strictly within them and does not relitigate them.**

Decisions are stated as **MADE** with the losing option and reason. Residual unknowns flagged `OQ-n`.

---

## 0. What this subsystem owns (authority map)

| Category | This doc is authoritative for |
|---|---|
| **DrawList opcode PAYLOAD STRUCT SHAPES** | `FillRoundRectCmd`, `FillRoundRectStrokeCmd`, `DrawShadowCmd`, `DrawGlyphRunCmd` (consume), `FillPathCmd`/`StrokePathCmd`, **`DrawImageCmd`** (the UNION shape: `ImageHandle` + `Dst` + `Radii` + `PlaceholderFill` + `CrossFade` + `Clip` + `Stretch` + `Flags`; §3.1 is the authority — `media-pipeline.md` references it), **`DrawVideoCmd`** (the 7-field hole-punch shape; §3.1 authority), `PushLayerCmd`/`PopLayerCmd`, `PushClipRectCmd`/`PopClipCmd`, `PushStencilClipCmd`/`PopStencilClipCmd`, `PushTransformCmd`/`PopTransformCmd`, **`DrawSelectionRectCmd`** (text-selection highlight; the UNION shape: `Rect` + `Radii` + `SelectionBrush` + `Affinity` + `Clip` + `Flags`; §3.6 authority — `text.md` owns the geometry source, `input-a11y.md` owns the `SelectionState` semantics), **`DrawScrimCmd`** (overlay dismiss-layer fill; §3.6 authority — `input-a11y.md` owns the light-dismiss FSM), `DrawAccessKeyBadgeCmd`. **`DrawFocusRingCmd`:** the *struct shape* AND its **rasterization** are owned here (§3.6 + §4.4 — the focus-ring SDF + overlay/portal composition); `input-a11y.md` §8.4 only EMITS it. It is the single production focus-visual opcode (the rounded, clip-chain-anchored Fluent focus ring); the rectangular `DrawFocusRect(Cmd)` is a superseded debug placeholder. **NOT owned here:** `ImageRealization`/`ImageRefTable` + small-image-atlas residency/packing/`AcquireAtlasPage` (→ `media-pipeline.md`); `SelectionState`/`GetSelectionRects` geometry (→ `text.md`); overlay light-dismiss FSM + placement-flip (→ `input-a11y.md`/`layout.md`). |
| **GPU instance structs** | `QuadInstance` (80B; rect/shadow/border/image), `GlyphInstance` (48B) |
| **Render-thread algorithms** | `DrawListRecorder` (clean-span memcpy), `RenderLane` classifier, `Batcher` (LSD radix over `ulong[]`), `OverlapGrid` painter-order break, `PathTessellator` (monotone/trapezoidal sweep), `DamageAccumulator`, `LayerPool`, `UploadRing`, `TextureStagingRing` |
| **RHI methods I drive** | `SubmitDrawList` (PRIMARY hot path), `ICommandEncoder.*` (incl. **`CopyBufferToTexture`**), `CreateGraphicsPipeline`/`CreatePipeline`, the multi-visual present tree |
| **Shaders** | the entire HLSL VS/PS set (authored HLSL→DXC→DXIL `byte[]`) |
| **Color contract** | UNORM buffer / `_UNORM_SRGB` RTV / linear blend / premul output / text gamma exception (designed-to; pinned in architecture-spec §5.2) |
| **Hooks** | none of its own. It *consumes* `UseImage`/`UseMosaic`/`UseDerivedBrush` realizations (owned by `FluentGpu.Media`/`FluentGpu.Theme`) via handle tables. |

What it does **not** own: handle/allocator primitives (`foundations.md`), SceneStore columns
(`FluentGpu.Scene`), the publish/quarantine seam mechanics (`hardened-v1-plan §4.1`), COM binding
generation (`dotnet10 §4` + `hardened §4.2`), text shaping/atlas (`FluentGpu.Text`), image decode/
residency (`FluentGpu.Media`).

---

## 1. Where this subsystem sits (data-flow + thread)

```
                  UI THREAD (phases 0–7, PUBLISH 13a)          RENDER THREAD (phases 8–11)        GPU
 ┌────────────────────────────────────────────────┐    ┌──────────────────────────────────┐
 │ reconcile→layout→animation patch SceneStore SoA │    │ 8  DRAIN(workers, atlas evict→    │
 │   (Bounds, WorldTransform, NodePaintLite, Flags)│    │     epoch bump) → RECORD          │
 │ PUBLISH(13a): value-copy SnapshotColumns into a │───►│     DrawListRecorder: walk dirty, │
 │   triple-buffered immutable SceneFrame; release-│    │     clean-span memcpy from its OWN │
 │   store _publishedIdx; tick consume-gated       │    │     ≥3-deep PRIVATE prior arena    │
 │   quarantine                                    │    │ 9  BATCH: RenderLane classify →    │
 └────────────────────────────────────────────────┘    │     LSD radix(ulong[]) → OverlapGrid│
        immutable SceneFrame (POD)                       │     break → InstanceBatch[]; resolve│
        + stable refs into retained tables               │     glyph/image UVs at batch time  │
        (Brush/Clip/GlyphRun/ImageRef/TessCache,          │ 10 SUBMIT: SubmitDrawList → encoder │──► ID3D12
         content-epoch stamped)                           │     ExecuteCommandLists→Signal(fence)│   queue
                                                          │ 11 PRESENT: canvas-RT → DComp      │──► DComp
 ┌─ WORKER POOL ─────────────────────┐                   │     multi-visual Commit            │   scanout
 │ pure decode/tessellate-cold/glyph-│──results by handle►│ (RENDER THREAD OWNS EVERY ComPtr)  │
 │ raster (DESCOPED until seam green) │                   └──────────────────────────────────┘
 └────────────────────────────────────┘
```

The renderer NEVER touches `ComPtr`, `ID3D12*`, DXGI, or DComp — those live in `FluentGpu.Windows` (D3D12/ folder). It speaks
the `FluentGpu.Rhi` interface (POD descs/handles/spans, zero COM) and consumes the DrawList POD stream +
retained tables. **Portability boundary in one sentence:** everything in `FluentGpu.Render` is portable
C# math + POD + RHI/Text/PAL interface calls; only `FluentGpu.Windows` (D3D12/, Pal/, DirectWrite/ folders) and
optional `Effects.D2D1` are Windows-specific leaves (referenced only by `Hosting`).

**ComputeSharp reuse (verified ground truth, unchanged):** seed `FluentGpu.Windows` (D3D12/ folder) interop by forking
ComputeSharp's D3D12 COM-binding shproj (it has DXGI + device + command-list vtables + `ComPtr<T>` but
**only compute pipeline state — no graphics PSO / RTV / input-layout / blend / rasterizer descs**); reuse
**D3D12MA** as-is for all GPU buffers/textures/atlas pages; **author graphics shaders as HLSL+DXC**
(ComputeSharp's C#→HLSL transpiler is compute/D2D1-only). The codegen template + `ComPtr<T>` is the prize,
not the surface. Per the hardened COM ruling, the ~25 graphics structs + device/encoder vtbl slots are now
**GENERATED from a harvested `*.comabi.json` with a runtime self-check** (no human-typed vtable slots),
not hand-typed — see hardened-v1-plan §4.2.

---

## 2. RHI graphics surface this subsystem requires

The graphics-specific members of `FluentGpu.Rhi` (interface assembly, portable, zero COM). The seam
shape is fixed by architecture-spec §4.7; the members below are the ones this subsystem drives.
**`SubmitDrawList` is the PRIMARY hot path** — the leaf walks the POD opcode stream with concrete
devirtualized types; per-call `ICommandEncoder` use is the secondary/explicit path (layers, stencil,
texture upload).

```csharp
// FluentGpu.Rhi  (interface assembly; portable; [assembly: DisableRuntimeMarshalling] on Render/Pal)
public enum RhiFormat : byte { BGRA8_UNorm, BGRA8_UNorm_sRGB, RGBA8_UNorm, R8_UNorm,
                               R16G16B16A16_Float, R32_UInt }
public enum RhiPrimitive : byte { TriangleList, TriangleStrip }
public enum BlendPreset : byte { Opaque, SrcOverPremul, Additive, Multiply, Screen, DstOver, Clear, Custom }
public enum LoadOp : byte { Load, Clear, DontCare }   public enum StoreOp : byte { Store, DontCare, Resolve }

public readonly struct VertexAttr { public byte Location; public RhiFormat Fmt; public byte Offset; }
public readonly ref struct GraphicsPipelineDesc {
    public ShaderModuleHandle Vs, Ps;
    public ReadOnlySpan<VertexAttr> PerVertex;    // slot 0 (unit quad)
    public ReadOnlySpan<VertexAttr> PerInstance;  // slot 1 (QuadInstance / GlyphInstance)
    public RhiPrimitive Topology; public BlendPreset Blend;
    public byte SampleCount;                       // 1 = analytic AA / fringe; 4 = MSAA path fallback only
    public bool StencilEnable; public StencilOpDesc Stencil;
    public RhiFormat ColorFormat;                  // _UNORM_SRGB for canvas/layer RTs
}

public interface IGpuDevice : IDisposable {
    GpuDeviceCaps Caps { get; }  DeviceLostToken LostToken { get; }
    PipelineHandle      CreatePipeline(in GraphicsPipelineDesc d);     // hash-deduped immutable PSO
    ShaderModuleHandle  CreateShaderModule(in ShaderModuleDesc d);     // embedded DXIL byte[]
    BufferHandle  CreateBuffer(in BufferDesc d);  TextureHandle CreateTexture(in TextureDesc d);
    SamplerHandle CreateSampler(in SamplerDesc d);
    void Destroy(TextureHandle h);  void Destroy(BufferHandle h);      // gen-bumped, deferred to fence retire
    ICommandEncoder BeginFrame(in FrameContext ctx);  void Submit(ICommandEncoder enc);
    void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameContext ctx); // PRIMARY
    void WaitIdle();
}

public interface ICommandEncoder {
    void BeginRenderPass(in RenderPassDesc p);  void EndRenderPass();
    void SetPipeline(PipelineHandle p);  void SetViewportScissor(in RectPx vp, in RectPx scissor);
    void BindConstants(uint slot, ReadOnlySpan<byte> data);            // root constants (viewport/sRGB/alpha/clip)
    void BindBuffer(uint slot, BufferHandle b, uint off);
    void BindTexture(uint slot, TextureHandle t, SamplerHandle s);     // atlas/gradient/image/layer-source
    void DrawInstanced(uint vtxPerInst, uint instCount, uint baseVtx, uint baseInst);
    void DrawIndexedInstanced(uint idxCount, uint instCount, uint baseIdx, int baseVtx, uint baseInst);
    void Barrier(ReadOnlySpan<ResourceBarrier> b);
    void ResolveTexture(TextureHandle src, TextureHandle dst);          // MSAA path only
    void CopyBufferToTexture(BufferHandle staging, TextureHandle dst, in TextureRegion region); // IMAGE UPLOAD
}
```

`FluentGpu.Windows` (D3D12/ folder) implements these by adding the missing D3D12 graphics structs/methods over the ComputeSharp
seed. The interface is the exact substitution point for a future `Rhi.Metal` leaf
(`MTLRenderPipelineState`/`MTLRenderCommandEncoder`/`CAMetalLayer`).

**Present tree (amended, multi-visual):** the swapchain is **NOT** a single DComp visual. It is a
multi-visual DComp present tree — a UI swapchain/canvas visual z-**above** a **video child visual**;
`DrawVideoCmd.Dst` is hole-punched by clearing a transparent (premultiplied-0) region in the UI canvas so
the video child shows through. A window-Mica/Acrylic backdrop sibling visual sits **below** everything via
`IBackdropSource` (PAL). The hole, the UI present, and `IVideoPresenter.Place` commit in **one DComp
Commit** (§7.3, §11).

---

## 3. DrawList → batches: recorder, command stream, batcher

### 3.1 Command stream (consumed; physical format pinned by architecture-spec §4.5)

8-byte `DrawCmd` header + fixed POD payload, in **render-thread-private, ≥3-deep arenas** (the keystone
hardening fix — the UI thread never swaps or resets a DrawList arena; the render thread reads its own
prior arena for clean-span memcpy). **64-bit `SortKey` lives in a parallel `ulong[]` arena** (folds FA-2:
the header `SortKey` field is only 32-bit). Backing byte/`ulong[]` arenas are
`GC.AllocateUninitializedArray(cap, pinned: true)` (skip memset at multi-KB sizes; pinned removes GC
fix-up before native submit). The recorder writes through the **`IBufferWriter<byte>` contract over the
arena cursor** — never `ArrayBufferWriter` (hidden grow+copy), never `Pipe`/`ReadOnlySequence`.

```csharp
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct DrawCmd { public DrawOp Op; public byte Flags; public ushort PayloadSz; public uint _resv; }

public enum DrawOp : byte {
    FillRoundRect, FillRoundRectStroke, DrawShadow,             // rect family → RenderLane.AnalyticSdf
    DrawGlyphRun, DrawImage, DrawVideo, FillPath, StrokePath, FillGradient,
    DrawGradientStroke,                                        // = 11 (SHIPPED): gradient-tinted SDF outline (§3.1a)
    PushClipRect, PushClipRoundRect, PushStencilClip, PopStencilClip, PopClip,
    PushLayer, PopLayer, PushTransform, PopTransform,
    DrawFocusRect,                                             // superseded rectangular debug placeholder
    DrawFocusRing, DrawAccessKeyBadge,                         // DrawFocusRing = the production focus-visual opcode (§3.6, §4.4)
    DrawSelectionRect, DrawScrim                               // overlay/selection family (§3.6, raster §4.4)
}
// NOTE: the DrawOp enum LIST is registered/owned by scene-memory.md §4.1 (the encoding framework). The folded
// entries DrawFocusRing + DrawSelectionRect + DrawScrim are registered there; this doc owns only their PAYLOAD
// STRUCT SHAPES (§3.6) and their RASTERIZATION (§4.4). The enum reprinted here is for local readability and
// must stay in lockstep. DrawFocusRect is the superseded rectangular placeholder; DrawFocusRing is production.
```

Representative payloads (POD; handle/index refs only; never GC pointers):

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct FillRoundRectCmd {           // fill; FillRoundRectStroke reuses + StrokeWidth>0
    public RectF Rect; public CornerRadius4 Radii;             // device-space AABB + per-corner radii
    public BrushHandle Fill; public BrushHandle Stroke; public float StrokeWidth; public ClipHandle Clip;
}
public struct DrawShadowCmd {              // analytic rounded-box Gaussian; NO offscreen, NO blur pass
    public RectF GeomRect; public CornerRadius4 Radii; public float Sigma;   // device px
    public BrushHandle Color; public ClipHandle Clip;
}
public struct DrawGlyphRunCmd {            // references by handle; NEVER bakes atlas UVs (resolved at batch)
    public GlyphRunHandle Run; public Vector2 Origin; public BrushHandle Color; public ClipHandle Clip;
}
// AUTHORITY (this doc owns the struct SHAPE — the UNION form). media-pipeline.md REFERENCES it (§3.1) and
// owns only the residency/ImageRealization detail. ImageHandle indirection, never a raw TextureHandle.
public struct DrawImageCmd {
    public ImageHandle Image;              // → ImageRefTable (Foundation); resolve to Texture+AtlasUv at batch
    public RectF Dst;                      // device-space destination AABB
    public CornerRadius4 Radii;            // rounded/circle art is everywhere (circle = all = min(w,h)/2)
    public BrushHandle PlaceholderFill;    // dominant-color tint quad used when State != Resident (derived from realization tint)
    public float CrossFade;                // 0..1 placeholder→image fade (driven by ContentEpoch bump)
    public ClipHandle Clip;
    public byte Stretch;                   // Stretch enum below
    public byte Flags;                     // bit0 = isAtlasPacked (batcher hint), bit1 = premultiplied src
}
// THE single canonical Stretch enum (one shape across all docs):
public enum Stretch : byte { UniformToFill = 0, Uniform = 1, None = 2, Fill = 3 }
// AUTHORITY (this doc owns the struct SHAPE). Hole-punch only; no video pixel work ever on our thread.
// media-pipeline.md / pal-rhi.md REFERENCE this shape; they own the present/consume logic.
public struct DrawVideoCmd {               // sortkey PassClass below chrome
    public VideoSurfaceId Surface;         // which DComp child visual
    public RectF Dst;                      // device-space hole (also the IVideoPresenter.Place rect)
    public ImageHandle PosterBlur;         // lower crossfade layer (blurred poster) while VideoReady<1
    public ImageHandle AlbumArt;           // lowest crossfade layer (art) before poster
    public float VideoReady;               // 0..1, 3-layer crossfade (art→poster→live)
    public CornerRadius4 Radii;            // rounded PiP corners (clip the hole + chrome)
    public ClipHandle Clip;
}
public struct FillPathCmd { public PathRef Path; public BrushHandle Fill; public ClipHandle Clip; public byte FillRule; }
// AUTHORITY (this doc owns the SHAPE + raster posture). `DrawIconMask` = a ThemedIcon vector-layer mask (controls.md).
// A CPU-rasterized COLORLESS R8 coverage mask — geometry interned in `IconGeometryTable.Shared` (a `.Shared` side-table
// crossing the render seam by int id, `SpanRunTable` precedent), keyed by `PathId` — tinted PER-INSTANCE by `Tint` and
// drawn through the EXISTING glyph atlas + glyph PSO/instance pipeline (§11): the backend rasterizes lazily on a
// (PathId, device-px) atlas MISS and appends ONE tinted GlyphInstance, so it batches in the glyph pass (text-like z
// within a layer scope). No new shader/PSO/texture/RHI method. `Tint` rides the command (colorless mask) so a retheme
// recolors with NO re-raster. **Deliberately NOT the §5 FillPath/StrokePath tessellation lane** — icons are tiny,
// static, glyph-shaped workloads that ride the R8 atlas exactly like text (the same non-tessellation-sibling posture as
// DrawTabShape), which keeps the §5 tessellation-fraction tripwire honest.
public struct DrawIconMaskCmd { public RectF Rect; public ColorF Tint; public int PathId; public Affine2D Transform; public float Opacity; }
public struct PushLayerCmd { public RectF DeviceBounds; public float Opacity; public BlendPreset Blend;
                             public EffectHandle Effect; public ClipHandle Clip; }
// AUTHORITY (this doc owns the SHAPE + raster). `DrawGradientStroke` = a gradient-tinted SDF OUTLINE — the WinUI
// (Accent)ControlElevationBorder. Payload = the gradient-rect command + a stroke width; the gradient SPEC comes from
// the sparse `_borderBrushes` side-table (scene-memory.md, mirrors `_gradients`), keyed by the `BoxEl.BorderBrush`
// (`GradientSpec?`) DSL field. NOT a new pipeline — REUSES the GradientPipeline.
public struct DrawGradientStrokeCmd {      // = DrawGradientRectCmd + StrokeWidth (the band width, device px)
    public RectF Rect; public CornerRadius4 Radii; public GradientRef Brush; public ClipHandle Clip;
    public float StrokeWidth;               // >0 ⇒ draw the gradient as a band centered on the edge (bw*0.5 inset)
}
```

> **`DrawGradientStroke` raster (reuses the GradientPipeline; stride/root-sig UNCHANGED).** The 160-byte
> `GradientInstance` gains a `float Stroke` reusing a spare pad (`Pad0`) — **160-byte stride preserved**. In the
> gradient PS, when `Stroke > 0` the fill-coverage line is swapped for the stroke-band formula already used by the
> rounded-rect border (`cov = clamp(0.5 - (abs(d) - stroke*0.5)/fw)`); the gradient `t`/color math is untouched (the
> gradient is sampled along the local axis exactly as the fill path does). It is `RenderLane.AnalyticSdf` (see
> `Classify`), composes with the existing gradient-fill branch, and the recorder emits it (in the `VisualKind.Box`
> case, at the edge-centered ring rect) only when a border brush exists. `D3D12Device` and `HeadlessGpuDevice` both
> decode it; `HeadlessGpuDevice` exposes `LastGradientStrokes` for golden checks. Corner radius uses `radii.x`
> (uniform) — fine for the 4 px control radius.

### 3.2 SortKey layout (64-bit) — folds the painter-order BLOCKER

**MADE: the primary key is a monotonic paint-order record sequence (tree pre-order emit index), with
`PassClass` demoted BELOW it.** The original (PassClass-primary) reorders translucent primitives across
nodes and can paint a later translucent shape under an earlier one. Fixed layout:

```
bit 63..40  RecordSeq        (24b: tree pre-order emit index — PRIMARY for translucent correctness)
bit 39..36  PassClass        (Shadow=0, Fill=1, Border=2, Image=3, Glyph=4, Video=5, Effect=6 — intra-node z)
bit 35..20  PipelineId       (16b: RenderLane × blend × sampleCount → PSO)
bit 19..04  TextureBindId    (16b: atlas page / gradient tex / image atlas page; 0 = solid)
bit 03..00  ClipBucket       (4b: scissor-compatible clips share a bucket; SDF/stencil unique)
```

- **Opaque** primitives may be freely reordered/coalesced (RecordSeq ignored for them at break time).
- **Translucent** primitives **must** respect submission order where they overlap → the `OverlapGrid`
  break (§3.4). Intra-node `shadow→fill→border→image→glyph` order is preserved (`PassClass`, safe within
  one widget).
- **MADE: hand-written stable LSD radix sort over `ulong[]`** (4×16-bit passes into arena scratch — zero
  alloc, ~O(n)). Rejected `Array.Sort` (comparer delegate = GC + not AOT-ideal + unstable). The "3–5×
  fewer batches" claim from the original is **revised down but correct** (shadows/glyphs from different
  widgets still merge within a paint-order window).

### 3.3 Batch-break rules (authoritative)

A new `InstanceBatch` starts when, scanning sorted cmds, ANY changes vs the open batch:
1. **PipelineId** (RenderLane class, blend, sample count) → `SetPipeline`.
2. **Bound texture** (atlas page / gradient / image atlas page / layer-source) → `BindTexture`.
3. **Clip id** when not scissor-compatible (rounded/path → SDF uniform / stencil ref change). Scissor
   clips do **not** break (pass state).
4. **Layer boundary** (`PushLayer`/`PopLayer`) → hard break + offscreen pass boundary.
5. **`PushStencilClip`/`PopStencilClip`** → non-reorderable pass boundary (stencil mask pre-pass).
6. **OverlapGrid painter-order break** (§3.4) — a later differently-pipelined **translucent** primitive
   overlaps an un-flushed earlier one.

Everything else (rect, color, radii, transform, gradient stops, image dst) is **per-instance data**, not
a break. Solid fills, same-atlas gradients, same-page images, and shadows of arbitrary geometry merge.

### 3.4 OverlapGrid — painter-order break (folds the hardened fix)

**MADE: a per-layer coarse occupancy structure (bounding-interval list / coarse tile grid over expanded
device bounds) that stores the LAST WRITER per cell and breaks the batch when a later differently-
pipelined translucent primitive overlaps an un-flushed earlier one.** Complexity is **O(n·tiles)** —
SAFE-by-construction (no O(n²) path). Painter-order correctness is **gated + bounded by grid resolution**,
not proven: `CanMergePreservingPainterOrder` consults the grid's stored last-writer, and **both the grid
break and the radix stable-sort tie-break derive from the SAME `RecordSeq`** (so the two mechanisms can
never disagree). Expanded bounds include effect extent (shadow blur radius, AA pad).

```csharp
public ref struct OverlapGrid {                      // arena-backed; per layer; render-thread-private
    public OverlapGrid(ArenaAllocator scratch, RectF layerBounds, int tilePx);
    public bool WouldBreak(in RectF expandedDevBounds, ushort pipelineId, uint recordSeq); // translucent test
    public void Mark(in RectF expandedDevBounds, uint recordSeq);   // store last-writer per touched tile
}
```

### 3.5 Instance structs (the per-quad GPU records)

```csharp
[StructLayout(LayoutKind.Sequential, Size = 80)]     // rect / shadow / border / image
public struct QuadInstance {
    public Vector4 BoundsDev;     // device-space xy0,xy1 of the EXPANDED quad (shadow blur / AA pad)
    public Vector4 GeomRect;      // the actual shape rect (unexpanded) for SDF eval
    public Vector4 Radii;         // TL,TR,BR,BL device px
    public Vector4 FillRGBA;      // PREMULTIPLIED, LINEAR-space color (solid path); textured paths use UV
    public uint    Params0;       // packed: lane(4)|blendId(4)|hasTex(1)|gradientKind(2)|aaMode(1)|stretch(2)…
    public float   StrokeWidth;   // 0 = fill; >0 = border ring via two SDFs
    public float   Softness;      // shadow blur sigma (device px); 0 for crisp shapes; or CrossFade for image
    public uint    TexOrGradId;   // atlas/gradient/image page+slice + uv packing selector
}
[StructLayout(LayoutKind.Sequential, Size = 48)]     // glyphs: never need radii/stroke/softness
public struct GlyphInstance {
    public Vector4 DestRectDev;   // FINAL device-space dest (text seam already resolved BiDi + subpixel phase)
    public Vector4 AtlasUv;       // u0,v0,u1,v1 (resolved at batch time, NOT baked in the command)
    public Vector4 ColorRGBA;     // premultiplied linear; gamma applied in PS (text exception)
    // page index packed into a 16-aligned tail via Params (kept dense for branchless glyph PS)
}
```

### 3.6 Selection / overlay opcode shapes (this doc is the opcode-shape authority)

These three opcode families are the **selection-highlight + overlay-composition** shapes pulled into core
(folds gap rows **L1** selection highlight, **L4** overlay scrim/dismiss-layer, and the **L1/L4 raster of
the focus ring**). All three are **POD, handle/index refs only** (memcpy-safe, no GC pointer, satisfies the
scene-memory §4.1 framework contract), all three sort BEHIND or AROUND content per §3.6.1, and none breaks
the zero-offscreen budget (no opcode here forces a layer RT). The `DrawOp` enum entries are registered in
`scene-memory.md` §4.1; the device-space geometry that fills them is produced by `text.md`
(`GetSelectionRects`) and `input-a11y.md` (overlay manager / `FocusEngine`).

```csharp
// AUTHORITY: this doc owns the struct SHAPE + raster. text.md owns GetSelectionRects geometry; input-a11y.md
// owns SelectionState (anchor/extent/affinity) semantics + the SelectionBrush theme source. One DrawSelectionRectCmd
// is emitted PER VISUAL FRAGMENT a logical range maps to under BiDi (text.md §8: a logical range → N disjoint rects).
[StructLayout(LayoutKind.Sequential)]
public struct DrawSelectionRectCmd {       // RenderLane.AnalyticSdf — a tinted rounded-rect BEHIND text
    public RectF        Rect;              // ONE visual fragment, device-space AABB (from GetSelectionRects[i],
                                           //   projected through the SAME Push/Transform/Clip run as its text run)
    public CornerRadius4 Radii;            // 0 for the classic block highlight; >0 for the rounded "pill" selection
                                           //   used on the leading/trailing fragment ends (Fluent reveal selection)
    public BrushHandle  SelectionBrush;    // theme selection brush (focused vs unfocused tint); HC → SystemColors
                                           //   highlight; premul-linear like every solid (§8). NOT a per-frame pow().
    public byte         Affinity;          // 0 = none, bit0 = isLeadingFragment, bit1 = isTrailingFragment
                                           //   (round only the outer corners of the run via Radii selection)
    public ClipHandle   Clip;              // the text run's clip chain (so selection scrolls/clips WITH the text)
    public byte         Flags;             // bit0 = behindText(default 1), bit1 = activeSelection(else inactive tint)
}

// AUTHORITY: this doc owns the struct SHAPE + raster. input-a11y.md §8.3 owns the light-dismiss FSM + WHEN a scrim
// is pushed; layout.md owns the device rect (it is the overlay-root's full content bounds). A scrim is the modal
// dimming/dismiss layer UNDER a popup/dialog/flyout and OVER everything below it in the overlay z-stack.
[StructLayout(LayoutKind.Sequential)]
public struct DrawScrimCmd {               // RenderLane.AnalyticSdf — one translucent fill (or transparent hit-only)
    public RectF        Rect;              // device-space; the overlay-root content rect (full window for a modal)
    public CornerRadius4 Radii;            // 0 for window-modal; >0 only for an inset themed scrim (rare)
    public BrushHandle  ScrimBrush;        // premul-linear translucent (e.g. SmokeFill 0.3α); BrushHandle 0 = a
                                           //   FULLY-TRANSPARENT hit-only dismiss layer (light-dismiss, no dim)
    public ClipHandle   Clip;              // overlay-root clip (normally none/window)
    public byte         Flags;             // bit0 = isModalDim(else light-dismiss transparent), bit1 = blurBackdrop
                                           //   (bit1 set ⇒ recorder promotes to a PushLayer{Effect} acrylic — §7.2;
                                           //    NOT rastered here, it routes to the backdrop layer path)
}
// AUTHORITY: this doc owns the DrawFocusRingCmd struct SHAPE + raster (§4.4). input-a11y.md §8.4 EMITS it
// (anchors it to the focused node's clip chain) and sources its Brush from ISystemColors; it does not own the shape.
// DrawFocusRingCmd is the single PRODUCTION focus-visual opcode — a rounded, clip-chain-anchored focus RING that
// supports the Fluent dashed reveal style. It SUPERSEDES the old rectangular DrawFocusRect(Cmd) placeholder
// (retained only as a debug rectangle, scene-memory.md §4.1).
[StructLayout(LayoutKind.Sequential)]
public struct DrawFocusRingCmd {           // RenderLane.AnalyticSdf — a stroked rounded-rect ring (shape_border PSO)
    public RectF        OuterRect;         // device-space ring outer AABB; positioned by the enclosing Push/Transform/Clip run
    public RectF        InnerRect;         // device-space ring inner AABB (OuterRect inset by Thickness) — baked geometry
    public CornerRadius4 Radii;            // match the focused node's corners
    public BrushHandle  Brush;             // HC-aware focus color; input-a11y sources it from PAL ISystemColors
    public float        Thickness;         // device px (Fluent 2px default)
    public float        DashPeriod;        // 0 = solid; >0 = the Fluent dashed/dotted reveal ring (one Params0 bit)
    public ClipHandle   Clip;              // the focused node's clip chain (so the ring clips/scrolls WITH the anchor)
}
```

#### 3.6.1 SortKey placement for the new opcodes (reuses the §3.2 layout — no new bits)

The §3.2 64-bit SortKey is unchanged; the new opcodes slot into the existing `PassClass` field and the
`RecordSeq` discipline that already governs translucent correctness:

| Opcode | `PassClass` | Where it sorts | Why |
|---|---|---|---|
| `DrawSelectionRect` | **`Fill` (1)** with `RecordSeq` = the text run's seq **− 1** (emitted immediately before its run) | one record *behind* the glyph run it highlights, in the SAME node's intra-node order | guarantees the tint paints UNDER text without a separate pass; the OverlapGrid (§3.4) sees it as an ordinary translucent fill so painter-order stays correct against neighbours |
| `DrawScrim` (modal dim) | **`Fill` (1)** at the overlay-root's `RecordSeq` | above all content below the overlay, below the overlay's own chrome (overlay chrome has a higher `RecordSeq` ∵ deeper in pre-order) | one translucent fill; the dim is just an alpha quad |
| `DrawScrim` (light-dismiss transparent) | **`Fill` (1)**, `ScrimBrush=0` | same slot; **culled from the GPU batch** (zero-area-of-color), kept only as the input-a11y hit-target rect | a transparent dismiss layer is an *input* concern (input-a11y owns the hit test); it must not cost a draw |
| `DrawFocusRing` (in overlay/portal layer) | **`Effect` (6)** — TOP intra-node | above the anchor's own content but inside the anchor's clip chain (input-a11y §8.4) | a focus ring must never be occluded by sibling fills; `PassClass=Effect` puts it last within the node, and the overlay layer's own `RecordSeq` puts the whole overlay above the page |

Because all three reuse `PassClass`+`RecordSeq` (the §3.2 primary), **the OverlapGrid break (§3.4) and the
radix tie-break see them as ordinary translucent fills** — there is no new batch-break rule and no new
painter-order mechanism. `DrawSelectionRect` and `DrawScrim` are `RenderLane.AnalyticSdf` (§4), so they
**batch with every other rounded-rect fill in the frame** when their brush/clip permit (selection
highlights across a multi-line range are one batch; the modal scrim is one instance). This is the small,
surgical property: selection + scrim + focus-ring add **zero new pipelines** and **zero new passes**.

---

## 4. RenderLane classifier — SDF default, paths the exception

**MADE: a `RenderLane` classifier routes every primitive; analytic SDF is the default, genuine Bézier
paths are the only tessellated exception.** (Folds the hardened §4.3 classifier + the painpoints
correction "prefer analytic-SDF over tessellation wherever possible.")

```csharp
public enum RenderLane : byte {
    AnalyticSdf,   // rect, rounded-rect, ellipse, capsule, border, drop-shadow → übershader, sample=1, NO resolve
    Glyph,         // pre-rasterized coverage atlas + gamma blend, sample=1
    Image,         // sampled texture (atlas page or standalone), sample=1
    Path,          // genuine Bézier/arc → CPU tessellation → AA-fringe, sample=1 (MSAA4 fallback only)
}
public static RenderLane Classify(DrawOp op, in NodePaintLite p)
    => op switch {
        DrawOp.FillRoundRect or DrawOp.FillRoundRectStroke or DrawOp.DrawShadow
            or DrawOp.DrawFocusRect or DrawOp.DrawFocusRing or DrawOp.FillGradient
            or DrawOp.DrawGradientStroke                                                 // §3.1a: gradient SDF outline
            or DrawOp.DrawSelectionRect or DrawOp.DrawScrim => RenderLane.AnalyticSdf,   // §3.6: tint quad / ring / dim
        DrawOp.DrawGlyphRun                                 => RenderLane.Glyph,
        DrawOp.DrawImage or DrawOp.DrawVideo                => RenderLane.Image,   // video = hole-punch fill, image lane
        DrawOp.FillPath or DrawOp.StrokePath               => RenderLane.Path,
        _                                                   => RenderLane.AnalyticSdf,
    };
```

A **tessellation-fraction tripwire** (validation gate) fails CI if the Path lane exceeds a budgeted
fraction of primitives on the curated UI corpus — keeping "paths are the exception" honest.

### 4.1 The SDFs (pixel shader; vertex shader only positions the expanded quad)

Rounded-rect SDF (Inigo Quilez per-corner form), device-space, per fragment:

```hlsl
float sdRoundRect(float2 p, float2 b, float4 radii) {        // p: frag vs center; b: half-extents
    float r = (p.x > 0) ? ((p.y > 0) ? radii.z : radii.y)    // BR : TR
                        : ((p.y > 0) ? radii.w : radii.x);   // BL : TL
    float2 q = abs(p) - b + r;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}
```
- **Fill:** `coverage = 1 - smoothstep(-aa, +aa, sd)`, `aa = fwidth(sd)` (≈0.5px, tracks scale/rotation).
- **Border:** ring `abs(sd) - halfStroke`; inner+outer edges AA'd in one eval. Per-side widths via an
  anisotropic variant selected by `Params0.lane` — still one PS, one batch.
- **Drop shadow:** **analytic Gaussian-of-a-rounded-box** (closed-form `erf` per axis + corner
  correction) in ONE instanced quad expanded by `~3σ`, **zero** offscreen, zero blur kernel. This is the
  single most important footprint/perf win (Fluent UI is shadow-heavy; the naive shape→blur-RT→composite
  path is 3 passes + RT churn per shadow). Large/irregular shadows on **arbitrary paths** fall back to the
  offscreen blur via `IEffectRunner` (D2D dropshadow on Win; separable-Gaussian compute elsewhere) —
  rect/rrect shadows (the overwhelming majority) stay analytic.

```hlsl
float boxShadowRRect(float2 p, float2 halfExt, float4 radii, float sigma) {
    float2 lo = erf_approx((p + halfExt) / (1.41421356 * sigma));
    float2 hi = erf_approx((p - halfExt) / (1.41421356 * sigma));
    float a = 0.25 * (lo.x - hi.x) * (lo.y - hi.y);
    return a * cornerAtten(p, halfExt, radii, sigma);
}
```

### 4.2 One PS family, 3 compile-time variants

**MADE: 3 PSO variants from one `shapes.hlsl` via `#define KIND` (fill/border/shadow), NOT a per-fragment
runtime branch.** PSO switch happens at batch granularity (PipelineId in SortKey) so each variant is free
without per-pixel branching. `Custom`/effect/`FallbackD2D` blend PSOs are **runtime-warmed with a one-time
hitch** (the build-enumerated native set is pre-warmed; PSO claim scoped to the native set — hardened
§4.3).

### 4.3 Selection-rect + scrim raster — pure `KIND=fill` reuse (zero new PSO)

`DrawSelectionRect` and `DrawScrim` **add no shader and no PSO**. Both lower at batch time into ordinary
`QuadInstance`s on the **existing `shape_fill.ps.hlsl` `KIND=fill` variant** (§4.2) — the same übershader
that draws every rounded card. The lowering is a pure field copy, done in the batcher's instance-pack step:

```csharp
// batch-time lowering (render-thread, in the §9 batcher); NO allocation, NO new pipeline.
static QuadInstance LowerSelection(in DrawSelectionRectCmd c, in BrushEntry brush) => new() {
    BoundsDev = Expand(c.Rect, aaPad: 0.5f),          // selection has no blur/shadow extent → tiny AA pad only
    GeomRect  = c.Rect,
    Radii     = ResolveAffinityRadii(c.Radii, c.Affinity), // round only leading/trailing outer corners (§3.6)
    FillRGBA  = brush.SolidLinearPremul,              // theme selection brush, premul-linear (§8) — never a per-frame pow
    Params0   = Pack(lane: AnalyticSdf, hasTex: 0, aaMode: Fwidth),
    StrokeWidth = 0, Softness = 0,                    // fill, crisp
    TexOrGradId = 0,                                  // solid → biggest/cheapest batch
};
```
- **Selection highlight** is therefore literally a rounded-rect fill behind text (§3.6.1 sortkey). A
  multi-line / BiDi-split selection is **N fragment rects from `text.md`'s `GetSelectionRects`**, each one
  `DrawSelectionRectCmd`, all sharing the one selection brush + the run's clip → **they coalesce into a
  single `DrawInstanced`** (the §3.3 break rules see identical pipeline/texture/clip). `ResolveAffinityRadii`
  rounds only the run's outer corners when `Affinity` flags a leading/trailing fragment, giving the Fluent
  "rounded selection pill" without any geometry work.
- **Modal scrim** (`Flags.isModalDim`) is one translucent fill instance over the overlay-root rect; the dim
  alpha lives in the premultiplied brush. **Light-dismiss scrim** (`ScrimBrush == 0`) is **culled before the
  GPU batch** (`IsZeroAreaOfColor` — fully-transparent fills never reach `DrawInstanced`); it survives only
  as the `input-a11y.md` hit-target rect. **`Flags.blurBackdrop`** is NOT rastered here: the recorder
  promotes it to a `PushLayer{Effect}` acrylic pass on the **backdrop layer RT path** (§7.2, owned with
  `backdrop-effects-animation.md`) — keeping the heavy in-app-Acrylic case on its existing gated route, not
  on this lane.

### 4.4 Focus-ring raster — `shape_border.ps.hlsl` + dash, composes in overlay/portal layers

The `DrawFocusRingCmd` **struct shape is owned by this doc (§3.6)** — gpu-renderer is the opcode-shape
authority; `input-a11y.md` §8.4 only EMITS it. This doc also owns its **raster**. A
focus ring is a **stroked rounded-rect ring**, so it lowers onto the existing **`shape_border.ps.hlsl`**
variant (§4.1 border eval: `abs(sd) - halfStroke`, both edges AA'd in one pass) — **no new PSO** for the
solid ring:

```hlsl
// inside shape_border.ps.hlsl, KIND=border — the ring coverage is the existing border eval:
float sd       = sdRoundRect(p, halfExt, radii);
float ring     = abs(sd) - (thickness * 0.5);          // thickness from DrawFocusRingCmd.Thickness (device px)
float cov      = 1.0 - smoothstep(-aa, +aa, ring);     // analytic AA, aa = fwidth(ring)
```
- **Dashed/dotted ring** (`DrawFocusRingCmd.DashPeriod > 0`, the Fluent dotted reveal-focus ring) is the
  ONE focus-specific addition: a perimeter-parameter `s` (arc length around the ring, computed in the PS
  from the per-corner SDF) modulates coverage by `step(frac(s / DashPeriod), 0.5)`. This is gated by a
  `Params0` bit so the **same border PSO** serves solid and dashed — **no second pipeline**; the dash is a
  per-fragment multiply, free at batch granularity. Solid rings (`DashPeriod == 0`) skip the modulation.
- **Overlay / portal composition (the L4 raster guarantee).** `input-a11y.md` §8.4 records the focus ring
  **anchored to the focused node's clip chain** (recorded as if a child of the focused node) inside the
  `FocusEngine`'s transient overlay layer. Two composition facts this raster guarantees:
  1. **Z within the node:** the ring's sortkey uses `PassClass = Effect` (§3.6.1), the TOP intra-node class,
     so it paints over the node's own fills/glyphs but still inside the node's `Push/PopClip` run — it
     clips and scrolls with the anchor exactly as `input-a11y` specifies.
  2. **Z across a portal/overlay:** when the focused node lives in an **overlay/portal** subtree (a popup,
     flyout, or dialog rendered out-of-tree via the §7.1 layer or the overlay-root), its whole subtree carries
     a higher `RecordSeq` (deeper in the published pre-order), so the ring composes **above the page beneath
     the overlay** and **above any `DrawScrim`** — the ring is never dimmed by its own modal scrim. No special
     case in the batcher: the ring rides the overlay layer's `PushLayer`/`PopLayer` (or the overlay-root's
     transform/clip run) like any other primitive, and `PassClass=Effect` keeps it last within its node.
- **HC / theme:** color comes from `DrawFocusRingCmd.Brush`, which `input-a11y` sources from PAL
  `ISystemColors` (accent / High-Contrast focus color); like every solid it is realized **premul-linear once**
  per color change (§8), never a per-frame `pow()`.
- **Clean-span reuse:** the focus ring, selection rects, and scrim are ordinary clean-span citizens (§11.1):
  their baked-geometry hash covers `Rect`/`Radii`, their `BrushHandle`/`ClipHandle` degenerate to
  `IsLive`-only (content-hash deduped, no realization epoch), so a frame that only moves focus re-records the
  tiny overlay span and memcpy-reuses every clean sibling.

---

## 5. Path tessellation — vetted O(n log n) monotone/trapezoidal sweep

**MADE: CPU tessellation into an arena, AA-fringe (feather), MSAA off by default; flows through the same
instanced/batched path as everything else.** Rejected stencil-then-cover (stencil contention with
clipping, breaks instanced batching, still needs flattening, non-portable to Metal). Paths are NOT the
hot path in Fluent UI; a correct, allocation-free tessellator **cached by geometry hash** is the right
cost point.

**The two claims are SEPARATED (hardened §4.3, replacing the original's ear-clip language):**
- **Complexity-bound = SAFE-by-construction.** **DELETE ear-clipping.** Use **one vetted O(n log n)
  monotone/trapezoidal sweep** — there is no O(n²) path in the codebase. (Ear-clipping was O(n²) worst
  case and is gone.)
- **Geometric correctness = MANAGED, fuzz-gated + differential-rasterizer cross-check.** FP degeneracy
  can produce a watertight-but-wrong result; robust-predicate failures are **fuzz-gated** (watertightness
  + winding + robust-area cross-check vs an independent higher-precision scanline rasterizer). **D2D
  fallback on golden failure** (`IPrimitiveFallback`, Windows-only crutch → per-primitive Metal-milestone
  debt list).

```csharp
public ref struct PathTessellator {                  // outputs into caller arena spans — ZERO heap alloc
    public PathTessellator(ArenaAllocator vtxArena, ArenaAllocator idxArena, float deviceScale);
    public PathRef Tessellate(in PathData path, FillRule rule, float strokeWidth, in StrokeStyle stroke);
}
public readonly struct PathRef { public int VtxStart, VtxCount, IdxStart, IdxCount; public RectF Bounds; }
```
Algorithm:
1. **Flatten** cubics/quadratics/arcs by adaptive subdivision to `tol = 0.25px / deviceScale`. Use
   **Wang's-formula segment count up front** (no recursion, no stack alloc — write straight into the
   arena). Arcs → cubic spans ≤90°.
2. **Fill triangulation:** the monotone/trapezoidal sweep honoring `FillRule` (nonzero/evenodd) via
   winding accumulation. Trapezoidation is the robust default for any winding; a cheaply-detected
   convex/simple fast path uses monotone decomposition.
3. **Stroke:** offset the flattened polyline by `±strokeWidth/2`; miter/round/bevel joins + butt/round/
   square caps tessellated to the same `tol`. Output is a triangle list (SDF stroke is rect-family only).
4. **Edge AA:** **MADE: AA-fringe (feather) — extruded edge triangles with a 0→1 coverage vertex
   attribute; MSAA off.** Keeps the whole renderer single-sample (no resolve, no MSAA RT) and matches the
   analytic-rect look. `RenderConfig.PathAaMode = Fringe | Msaa4` is a fallback flag for pathological thin
   concentric curves. `OQ-1`: validate fringe vs MSAA4 on real icon-as-paths / Bézier logos via the
   golden gate before locking MSAA out.

### 5.1 Geometry realization cache

`PathRealizationKey = (PathGeometryId, quantizedDeviceScale, strokeWidthQ, ruleByte)` **with the
content-epoch in the key**, over an immutable `PathData` whose ctor **requires** the epoch → a missed
bump is a **compile error** (hardened §4.3). Resolves to a `PathRef` into a **retained** vertex/index slab
(not the per-frame arena). Tessellate once on first paint or scale change; thereafter the recorder emits
`FillPathCmd`/`StrokePathCmd` referencing the cached `PathRef`. LRU eviction by slab pressure. This is the
"static SVG/icon costs nothing per frame" guarantee; it makes steady-state on-UI-thread tessellation
zero-pending-work, which is **why on-UI tessellation ships first** (off-thread is descoped — §15).

**Hit-test shares the fill RULE (nonzero winding default), not just the vertices** (folds the input-a11y
fix) — the same `FillRule` is exposed to `FluentGpu.Input` so a click inside a complex path's hole behaves
consistently with what's painted.

---

## 6. Clip stack — 3-tier, chosen per `PushClip*`

| Clip kind | Mechanism | Cost | Batch impact |
|---|---|---|---|
| Axis-aligned rect | **Scissor** (`SetViewportScissor`) | free (HW) | does NOT break batch; pass state |
| Rounded rect / single rounded | **SDF clip uniform** (root constant) | ~free (1 extra `sdRoundRect`, `coverage *= clipCoverage`) | breaks only when clip uniform changes |
| Arbitrary path / overlapping non-rect / deep nesting | **Stencil mask** | 1 mask pre-pass + ref-test | breaks batch; non-reorderable `PushStencilClipCmd`/`PopStencilClipCmd` pass boundary |

```csharp
public struct ClipEntry {                  // ClipTable slab (Foundation), consumed here
    public ClipKind Kind;                  // ScissorRect | SdfRoundRect | StencilPath
    public RectF Rect; public CornerRadius4 Radii; public byte StencilRef; public ClipHandle Parent;
}
```
- Intersection: scissor∩scissor = min/max rect (HW); scissor∩sdf = scissor + SDF uniform; anything∩path =
  promote to stencil (same DSV, `INCR_SAT`/`DECR_SAT`, documented max depth).
- **Stencil sub-protocol (folded):** sample-count-matched DSV resource; mask written in a dedicated
  pre-pass emitted as the non-reorderable stencil pass boundary; nested clips via INCR/DECR_SAT.
- Scissor-compatible clips producing the same rect share a `ClipBucket` (no false breaks); SDF/stencil get
  unique buckets.
- **Why not stencil-for-everything** (WinUI's heavier approach): stencil forces a mask pass + breaks every
  batch + binds a DSV all frame. We pay that only for genuine path clips; 99% of UI clipping (panels, list
  viewports, rounded cards) is scissor or SDF.

---

## 7. Layers / offscreen RTs, video hole-punch, backdrop

### 7.1 Push-layer / opacity groups

A `PushLayer` is emitted only when a node needs **group** semantics that cannot fold into per-instance
state: group opacity < 1 over **overlapping** children (per-instance alpha double-blends overlaps); a
non-`SrcOver` blend on a subtree; an effect (backdrop blur, group drop-shadow, color matrix); or a
clip-to-path applied to a whole subtree with its own AA.

As-built `LayerKind`s on the `PushLayer`/`PopLayer` opcode pair: **`Acrylic`** (backdrop blur+tint recipe),
**`Opacity`** (flat group alpha — the overlap case above), and **`Blur`** (per-node **self-blur**, the Expressive
Motion Kit — `NodePaint.BlurSigma > 0`): the subtree renders to a pooled offscreen RT, a separable **dynamic-σ**
Gaussian runs over it, and it composites once at the group alpha. The `Blur` kind reuses the `Opacity` group's
`OpacityLayerCompositor` RT pool + composite (it IS an opacity group that blurs first), so it is the cheapest path that
supports an animating blur. Semantics + the curve/token vocabulary: `backdrop-effects-animation.md` FA-2. The
cross-frame retained self-blur **pin cache** and its position-independent key (and the `PushLayerCmd.InMotion` payload
field — 1 = the self-blur node's world transform moved this frame; drives the compositor's settle re-mint, and is **not**
folded into the pin key) are owned by `backdrop-effects-animation.md` §FA-2a.

```
PushLayer → BeginRenderPass(layerRT, Clear transparent) → [children draw into layerRT]
PopLayer  → EndRenderPass → (optional IEffectRunner on layerRT) →
            BeginRenderPass(parentRT) → draw a quad sampling layerRT, alpha = Opacity, blend = Blend
```
- **`LayerPool`**: pooled RT textures keyed by quantized power-of-two-ish size buckets, reused across
  frames (no per-frame texture alloc), from **D3D12MA placed resources** via the **deferred-release queue**
  (keyed by in-flight fence). Layers are the ONLY offscreen RTs — the analytic shadow path deliberately
  avoids them, so the common case has **zero offscreen passes**.
- **Shimmer/skeleton is explicitly NOT a layer** (WaveeMusic fold-in): it's a per-row animated gradient
  FILL (gradient-atlas row + animated UV in phase 7), preserving the zero-offscreen-pass budget.
- Nesting: a stack of active layer RTs in the `FrameGraph`; `RecordSeq`/`PassClass` keep each layer's
  draws contiguous and ordered.

### 7.2 Effects (separable + non-separable blend, backdrop)

- Default **SrcOver premultiplied** (the dominant PSO). Separable modes (Multiply/Screen/Additive/DstOver/
  Clear) are fixed-function blend PSOs (cheap, no PS change), selected via PipelineId.
- **Non-separable** modes (Overlay, ColorDodge, Hue/Sat/Lum) cannot be fixed-function. **MADE: route them
  through a PushLayer + an `IEffectRunner` blend kernel** (read dst, composite in shader). `OQ-2`: v1 ships
  the separable set + Overlay.
- **Window Mica/Acrylic = PAL, not our pixels** (WaveeMusic): `IBackdropSource.SetWindowBackdrop(...)`
  (`FluentGpu.Windows` Pal/ → `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` or a DComp backdrop sibling visual
  **below** our visual). Our root clears transparent (premul 0); DWM composes Mica through. **Zero renderer
  change.** HC → opaque fill. macOS → `NSVisualEffectView`.
- **In-app live Acrylic** (toast/add-to-playlist sampling content behind) = a backdrop layer that samples
  the persistent canvas RT (§13) + blur via `IEffectRunner`. It is the heaviest GPU item and **wants the
  render thread before it is stress-safe under simultaneous scroll+video** — gated, not v1-default-on.

### 7.3 Video hole-punch (`DrawVideoCmd`)

**FluentGpu never touches video pixels.** The app binds its external `MediaPlayer` to a DComp **sibling
child visual** via `IVideoPresenter` (PAL → DComp; POD `VideoSurfaceId`):

```csharp
public interface IVideoPresenter {            // PAL seam; FluentGpu.Windows Pal/ → DComp
    VideoSurfaceId CreateSurface(in VideoSurfaceDesc d);
    void Place(VideoSurfaceId id, in RectPx deviceRect, float opacity, int z);
    void SetVisible(VideoSurfaceId id, bool on);  void Destroy(VideoSurfaceId id);
    nint GetMediaPlayerSink(VideoSurfaceId id);   // app binds MediaPlayer/MediaPlayerElement here
}
```
Render-thread cost per frame: re-record the scrubber (tiny damage), emit a `DrawVideoCmd` whose batch step
**clears `Dst` to premultiplied-0** in the UI canvas (the hole, sortkey `PassClass=Video` below chrome so
chrome draws OVER), and poke `IVideoPresenter.Place`. **The hole, the canvas present, and `Place` commit in
ONE DComp Commit** so there is never a one-frame gap where the hole shows black or the video shows over
chrome. PiP persists across nav as a retained visual; re-punch on overlapping damage. The 3-layer crossfade
(art→poster→live) is opacity-only over stacked image/video draws.

---

## 8. Color / gamma correctness (designed-to; pinned in architecture-spec §5.2)

**The color contract (folds the sRGB BLOCKER):**
- Swapchain **buffer `BGRA8_UNORM`**; back-buffer **RTV `BGRA8_UNORM_SRGB`** (HW does linear→sRGB on
  write, sRGB→linear on sample). **Blend + MSAA-resolve in LINEAR.** Renderer outputs **premultiplied
  linear-alpha** for DComp `PREMULTIPLIED`.
- Brush colors enter sRGB 8-bit (markup/theme) → realized to **linear premultiplied on the CPU once** per
  brush change → stored linear in `FillRGBA`. The GPU never `pow()`s solids per frame.
- Gradients baked into a shared **`RGBA16F` linear gradient-texture atlas** (each gradient = one 256-texel
  row) — all gradients share one bind and **batch together**; re-bake only on stop change.
- Layer RTs (engine-owned, not flip-model) may be `*_UNORM_SRGB` resources directly.
- **Text gamma is a DELIBERATE exception** (folds the text-blend fold-in): glyph coverage is blended in a
  gamma/perceptual space with a DWrite-style gamma + enhanced-contrast curve (per-target `gamma`/contrast
  constant in `GlyphKey`), A/B-validated against native DWrite/WinUI text — naive linear coverage blend
  makes thin stems too thin. **Grayscale-only v1**; ClearType (a 2nd dual-source-blend PSO, opaque-only,
  transform-breaking) deferred to v2 (`GlyphAaMode` flag reserved, one glyph PSO provisioned). `OQ-3`.

---

## 9. Brushes: solid, gradient, image

```csharp
public enum BrushKind : byte { Solid, LinearGradient, RadialGradient, Image }
public struct BrushEntry {                 // BrushTable slab (Foundation), realized lazily, content-hash deduped
    public BrushKind Kind;
    public Vector4 SolidLinearPremul;
    public int GradientTexSlice;           // row in the RGBA16F gradient atlas
    public Vector4 GradientGeom;           // p0,p1 (linear) or center,radius,focal (radial)
    public ImageHandle Image;              // image brush → ImageRefTable indirection (NOT a raw TextureHandle)
    public Affine2D ImageTransform; public byte ExtendMode;
}
```
- **Solid** → color in `QuadInstance.FillRGBA`; no texture bind; biggest/cheapest batch.
- **Gradient** → PS computes `t` from `GradientGeom`, samples the shared atlas row → HW-filtered, all
  gradients batch together.
- **Image brush** → resolves through the same `ImageHandle → ImageRealization` indirection as
  `DrawImageCmd` (§10); UV resolves at batch time (reconciles the original §9 "baked TextureHandle" path
  with the amended image pipeline — there is now ONE image indirection, never a baked `TextureHandle`).
- **T2 dynamic palette brushes** (album-art → 4-stop hero gradients, right-panel tint) converge on one
  `BrushHandle` into the existing `BrushTable` + shared gradient atlas → the hot paint path is **unchanged**
  and **no new theming opcode is needed**. Recolor crossfade is **opacity-only over two pre-derived endpoint
  brushes** (no per-tick gradient-atlas re-bake). Derived-brush eviction runs at **frame START**.

---

## 10. Image pipeline — `DrawImageCmd` indirection + small-image atlas (OQ-4 → v1)

**MADE: `DrawImageCmd` references an `ImageHandle → ImageRealization` (Foundation `ImageRefTable`,
content-epoch-stamped) — NEVER a raw `TextureHandle`** — exactly mirroring `GlyphRunRealization`, so the
§4.5 clean-span rule reuses its machinery (§11). `FluentGpu.Media` owns decode/residency/eviction; the
renderer owns the **record→resolve→batch→upload** path.

**`ImageRealization` / `ImageRefTable` shape is owned by `media-pipeline.md` §1** (the residency authority) —
this doc does NOT redefine it. The renderer consumes the realization fields it needs at batch time:
`Texture`, `AtlasUv`, `AtlasPage`, `State`, `ContentEpoch`. **Placeholder reconciliation:** the realization
carries the dominant-color tint; the recorder derives `DrawImageCmd.PlaceholderFill` (a `BrushHandle`) from
it and draws a placeholder quad while `State != Resident` (see the record step below).

- **Record (P8):** resolve `ImageHandle→ImageRealization`. If `State != Resident`, emit a
  `FillRoundRectCmd` with the `PlaceholderFill` brush derived from the realization tint (one quad, zero
  texture bind). If `Resident`, emit `DrawImageCmd`. **Pin authority lives in P8** — the node *recorded this
  frame* pins (resolving the P4-request-vs-P8-pin ambiguity).
- **Batch (P9):** the UV-resolve gains an **`ImageRef` branch** (resolve atlas UVs at batch time like
  glyphs — never baked). Small-image instances merge by atlas page. **This batch-time UV-resolve `ImageRef`
  branch is the ONLY thing this doc owns about the small-image atlas** — residency, packing, and
  `AcquireAtlasPage` are owned by `media-pipeline.md` §4.1.
- **Small-image atlas (`OQ-4` PROMOTED to v1 required):** images ≤128px pack into a shared `BGRA8`
  image-atlas page. This is the only thing that hits the "shelf row = 1–2 draws" / "400-thumbnail Home ≈
  80 draw calls" target. **Residency + packing + `AcquireAtlasPage` are owned by `media-pipeline.md`;** the
  renderer only consumes the resolved page/UV at batch time (above).
- **Upload (P13):** the RHI delta `CopyBufferToTexture(staging, dst, region)` via a **dedicated MB-sized
  texture-staging ring** in `FluentGpu.Windows` (D3D12/ folder) (fence-gated reset — **NOT** the instance `UploadRing`). Textures
  come from a **startup-allocated per-bucket pool**; P13 only does `CopyBufferToTexture` into a recycled
  bucket texture + `ContentEpoch++`. **`CreateTexture` NEVER runs in phases 6–13 steady state** (it
  allocates a `HandleTable` slot + a `ComPtr` root) — it is cold-path pool growth only. Upload is
  **byte-budgeted in two lanes** (small thumbs vs large art/bakes) so a fling fills in 1–2 frames while
  512px bakes are rate-limited. Eviction (frame START) bumps `ContentEpoch`, sets `Evicted`, frees the GPU
  texture through the **deferred-delete ring keyed by in-flight fence**, pinning-before-trim.
- **No GPU `ReadbackImage`** for palette extraction — the decoder already holds CPU pixels (a readback is
  a UI/render-thread device stall). Palette stays app-side fed the worker's staging block.

---

## 11. Glyph-run draw consuming the glyph atlas (Text seam integration)

The Text seam hands a `GlyphRunHandle` → `GlyphRunTable` entry. **Glyph positions reaching the renderer
are FINAL device-space dest rects in VISUAL order** (BiDi reorder, cluster mapping, mark positioning,
subpixel phase all resolved by the text seam) — the renderer treats them as opaque positioned quads.

```
DrawGlyphRunCmd → batch time: for each PackedGlyph in the run:
    IGlyphAtlas.GetOrAdd(key) → {Page, U0V0U1V1, Bearing}   // UVs resolved NOW, never baked in the command
    emit GlyphInstance { DestRectDev = origin + bearing+advance, AtlasUv, ColorRGBA (linear premul), page }
batcher: glyph instances sort into PassClass=Glyph runs keyed by (atlas page, clip) → one DrawInstanced/page
```
- **Glyph PSO:** samples the atlas (`R8_UNORM` grayscale coverage), applies text-gamma (§8), multiplies by
  premul color, blends SrcOver. No SDF eval. A separate `BGRA8` color page + `IDWriteColorGlyphRunEnumerator1`
  handles COLR/SVG/bitmap emoji.
- **Atlas residency is owned by the Text seam** (eviction at frame START; any glyph referenced by a live
  command this frame is ineligible; a batch-time UV-resolve miss rasterizes into a reserved **overflow
  region**, never faults). `GlyphRunRealization` carries a **content-epoch**; atlas repack bumps it,
  forcing re-record of any clean span referencing the run.
- **Why not SDF text (Slug/MSDF):** DWrite rasterizes superbly with zero footprint; SDF text adds
  generation cost + a second technique. `OQ-5` future for extreme-scale/3D text.

### 11.1 Clean-span reuse rule (amended — folds the content-epoch + baked-geometry fixes)

A memcpy'd clean DrawList span is **valid IFF**:
1. **every handle it references `IsLive`**, AND
2. **for `GlyphRunRef` and `ImageRef` handles, the backing realization `ContentEpoch` is unchanged**
   (brush/clip epochs degenerate to `IsLive`-only via content-hash dedup), AND
3. **its BAKED-GEOMETRY hash is unchanged** — device-space rects live inside command payloads
   (`FillRoundRectCmd.Rect`, `DrawGlyphRunCmd.Origin`), so a Bounds-move-without-PaintDirty would otherwise
   pass the handle/epoch check while shipping stale geometry. A single **`Mutate()` epoch chokepoint** and
   a DEBUG **`CleanSpanWitness`** (records a Bounds/rect hash; validator recomputes dest rects from current
   `Bounds[]`/`WorldTransform[]` and asserts equality) close this. Epoch validation is **render-thread-
   LOCAL** (compare the live epoch against the per-span epoch recorded into the render's own back arena —
   zero cross-thread epoch staleness). A second independent oracle hashes the actual realization backing
   bytes (catches forgotten-bump + untracked-reference).

`TransformDirty`-only nodes reuse their span; the **batcher re-applies the new `WorldTransform[node]` to the
cached instanced quads at submit (no re-record)** — composition-style independent animation.

---

## 12. AA quality — corpus-gated regression net (NOT a "validated property")

**MADE: an AA-fringe → MSAA(4) → D2D fallback ladder, gated by a golden-image + perceptual gate, honestly
labeled a "corpus-gated regression net," not a proven property** (folds the hardened §4.3 + painpoints
overclaim correction).

| Content | AA method | Sample count | Resolve? |
|---|---|---|---|
| Rounded rects, borders, shadows | Analytic SDF (`fwidth` smoothstep / `erf` shadow) | 1 | no |
| Glyphs | Pre-rasterized coverage atlas + gamma blend | 1 | no |
| Images / video hole | bilinear sample / scissored clear | 1 | no |
| Tessellated path fill/stroke | AA-fringe (feather) | 1 (default) | no |
| Path fallback (pathological) | MSAA | 4 | yes (per layer only) |

The gate: a **16× supersampled CPU reference**, **CIEDE2000 + edge-shift** perceptual comparison, and
A/B-vs-DWrite for text. Explicit caveat: **uncovered DPI/rotation/color/script combinations are ungated**;
WARP-vs-hardware forces a perceptual tolerance (WARP is not bit-identical to hardware). **Whole renderer is
single-sample by default → no global MSAA RT, no per-frame resolve, minimal RT memory** — the footprint-
optimal choice.

---

## 13. Per-frame GPU buffer management — zero per-frame alloc

```csharp
public sealed class UploadRing {                  // INSTANCE/VERTEX/INDEX ONLY (not texture upload)
    public Span<byte> Reserve(int bytes, int align, out uint gpuByteOffset);   // bump; no GPU alloc
    public void ResetWhenFenced(ulong completedFence);
}
public sealed class TextureStagingRing { /* MB-sized, fence-gated; backs CopyBufferToTexture (images) */ }
```
- **Instance/vertex/index** → the batcher writes into a persistently-mapped `UPLOAD` buffer per
  frame-in-flight, sized to a high-water mark, **grown only on overflow** (geometric 2×, old ring
  deferred-freed behind its fence), never freed. `BindBuffer` points at the ring with a byte offset → zero
  alloc, zero copy. (A `DEFAULT` copy is an optional optimization `OQ-6`.)
- **Texture upload** rides the **separate `TextureStagingRing`** (the original wrongly claimed images ride
  the instance ring — corrected).
- **Root constants** (viewport size, sRGB flag, global alpha, current clip params) via `BindConstants` —
  no CB churn.
- **Frames-in-flight = 2 (configurable 2–3)** (`OQ-8`); tables (RTV/PSO/textures) are retained slabs
  (handles stay valid); rings reset on fence completion.
- **Allocator:** all GPU resources from **D3D12MA** placed resources/pools → low fragmentation, AOT-proven.
- **Managed side:** recorder/batcher/sort scratch are arena; `InstanceBatch[]` is a pooled
  `SlabAllocator<InstanceBatch>` reset each frame. **No `new` on the paint path.** Per-frame managed
  allocations in phases 6–13: **0** (verified by the alloc-tripwire + process-wide BDN backstop, since
  `GC.GetAllocatedBytesForCurrentThread` does not follow work across the seam).

### 13.1 Damage / partial present — persistent canvas RT

**MADE: v1 = engine-owned persistent canvas RT** (folds the partial-present MAJOR; the original's `OQ-7`
is now decided):
1. **Incremental record** (P8): dirty subtrees re-record into the front arena; clean spans memcpy from the
   render-thread-private back arena (per §11.1). Recording cost ∝ changed subtree.
2. **Damage region** (`DamageAccumulator`, ≤16 merged `IntRect`): old∪new **world AABBs from all four
   transformed corners** (handles rotation/skew); each node's damage **inflated by its effect extent**
   (shadow blur radius, backdrop margin); repaint includes **all nodes intersecting that region in
   z-order** (not just the dirty node).
3. **Partial repaint:** damaged regions scissor-repainted into the **persistent canvas RT** with
   `LoadOp.Load` (valid because WE own the RT, unlike a `FLIP_DISCARD` back buffer), then DComp-composited
   to the back buffer. `Present1` dirty-rects are a **pure DWM hint layered on top, NOT the correctness
   mechanism**. World-space float damage converts to integer back-buffer pixels **at the RHI leaf,
   rounding OUT** (DPI applied once, Windows-side).
4. **Full-redraw fallback:** >16 rects, >~60% window coverage, layer resize, DPI/swapchain resize, or
   first frame.

The canvas RT is also the natural sample source for in-app Acrylic (§7.2). Animated transforms dirty only
old∪new bounds → a spinner repaints a tiny region.

**Structural-track cancellation damages the last-presented extent.** A layout transition (FLIP translate/scale,
or a `SizeMode.Reveal` presented-size) draws a node at a translated / size-inflated extent that lies OUTSIDE its
model bounds. When such an in-flight track is CANCELLED rather than allowed to settle — a drag-suppression snap or a
window-resize snap collapses it straight to final bounds (`AnimEngine.SnapStructuralToLayout` / `CancelStructuralAll`)
— the node stops covering the band it drew last frame, and, unlike natural completion (which ends AT the target, so
the settle is continuous), nothing re-touches that vacated band. The damage accumulator must therefore be seeded with
each cancelled node's **last-presented absolute rect** (its `AbsoluteRect` origin — which already folds in the node's
own composited translate — at its presented `PresentedW/H` extent, +AA pad) so the vacated region repaints; otherwise
the region-aware canvas/Acrylic cache freezes last frame's pixels there (a persistent "ghost" band). Cancellation is
the only discontinuous path that needs this — natural settles do not, and must not blanket-damage.

---

## 14. Shaders — HLSL → DXC → DXIL `byte[]`

**MADE: author graphics shaders as hand-written `.hlsl` (SM 6.0), compiled OFFLINE by DXC to DXIL
(Windows) and to SPIR-V (`-spirv`) → SPIRV-Cross → MSL (future Metal). NOT via ComputeSharp's C#→HLSL
transpiler** (compute/D2D1-only).

| Module (.hlsl) | Stages | Purpose |
|---|---|---|
| `quad.vs.hlsl` | VS | unit-quad × per-instance expand to device AABB; pass geom/uv/params to PS |
| `shape_fill.ps.hlsl` | PS | SDF rounded-rect fill, analytic AA, solid/gradient brush select — **also rasters `DrawSelectionRect` + `DrawScrim` (§4.3): same variant, zero new PSO** |
| `shape_border.ps.hlsl` | PS | SDF ring border (uniform + per-side), analytic AA — **also rasters `DrawFocusRing` (§4.4): ring = existing border eval; one `Params0` bit adds the dashed/dotted focus ring (arc-length dash multiply), still one PSO** |
| `shape_shadow.ps.hlsl` | PS | closed-form rounded-box Gaussian shadow (`erf`), analytic |
| `glyph.vs/ps.hlsl` | VS/PS | atlas-uv quad; coverage × gamma × premul color |
| `image.ps.hlsl` | PS | atlas/standalone sample, rounded clip, crossfade, stretch |
| `path.vs/ps.hlsl` | VS/PS | tessellated geometry + AA-fringe coverage attribute; brush select |
| `composite.ps.hlsl` | PS | layer RT sample × group opacity × blend (PopLayer) |
| `clip_stencil.ps.hlsl` | PS | stencil-mask write (color write off) |
| `shapes_common.hlsli` | — | shared `sdRoundRect`, `erf_approx`, gradient eval, gamma, brush select |

```
*.hlsl ──DXC(-T vs_6_0/ps_6_0 -Fo)──► *.dxil ──┐  (Windows: source-gen'd byte[] const in FluentGpu.Windows D3D12/)
       └─DXC(-spirv)──► *.spv ──SPIRV-Cross──► *.metal (future Rhi.Metal)
```
- Bytecode **embedded as source-gen'd `byte[]` const** (NativeAOT-friendly — no runtime compile, no
  reflection, trimmable). `CreateShaderModule` takes the span at device init.
- DXC at **build time** (MSBuild target), not runtime (no `dxcompiler.dll` shipped). The D2D1/FXC effect
  path is the only runtime compile, optional + leaf.
- **One shared root signature** baked into every PSO: root constants for per-draw; one descriptor table for
  the glyph atlas + brush/gradient/image textures → maps to Metal argument buffers later.
- `shapes_common.hlsli` single-sources the SDF/gamma/brush math; DXIL and SPIR-V come from the same text →
  no per-backend drift.

---

## 15. Thread placement, build order, off-thread descope

**This subsystem runs on the RENDER thread (phases 8–11)** reading the immutable `SceneFrame` (per
hardened-v1-plan §2.2). It is the **SOLE ComPtr owner** (single-writer refcount — the COM refcount race is
structurally impossible, not audited). DrawList arenas are render-thread-private and **≥3-deep**; the UI
thread never swaps/resets them.

**Render-frame ordering invariant (P8 entry):** `DRAIN(worker results)` → **atlas eviction (bump
epochs)** → clean-span validation/record. The eviction liveness set is computed from the snapshot's
command stream; epoch validation is render-thread-LOCAL.

**Off-thread tessellation + glyph raster are DESCOPED from v1 and sequenced behind the seam** (hardened
§4.3/§6 — the critical sequencing correction): a `DrainAll()` barrier would re-import the UI-thread stall,
and cache eviction is a slab ABA against in-flight readers at quarantine=0. **On-UI-thread tessellation
with the §5.1 geometry cache (zero steady-state cost) ships FIRST.** When off-thread lands (only after
`seam.race` is green at quarantine≥2): snapshots **copy** verb/point spans into worker-owned arena slices
(not aliasable `ReadOnlyMemory` views); the slab uses the deferred-free fence ring; glyph raster needs the
probe→raster→pack→upload re-architecture (do not assume the GetOrAdd-at-batch shape).

**Build order for this subsystem (mirrors hardened §6):**
1. Single-thread-correct: UI thread produces+consumes the SceneFrame shape; quarantine=0; on-UI
   tessellation; geometry cache; epoch chokepoint; `CleanSpanWitness` with baked-geometry capture; canvas
   RT + damage; image pipeline + `CopyBufferToTexture` + bucket pool (unblocks every WaveeMusic screen).
2. Move record/batch/submit/present to the render thread; migrate ComPtr ownership; ≥3 private arenas;
   retire-fence handshake; force-sync drain (no slot reuse in flight).
3. Flip quarantine 0 → `RenderInFlightDepth` only after `seam.race` (swept channel-cap + reader-stall) is
   green for the nightly streak; add the present-stall bench.
4. Off-thread tessellation + glyph raster.

---

## 16. NativeAOT + zero-alloc + thread-confinement story

- **Zero runtime reflection / codegen.** PSO descs, vertex layouts, root signatures from POD descriptors at
  init; shaders precompiled `byte[]`. Hot-path COM bindings GENERATED from `*.comabi.json`
  (runtime-self-checked), hand-vtable `calli` only on the generated hot-path consume + in-loop CCWs;
  `[LibraryImport]` for flat C exports (`D3D12CreateDevice`/`CreateDXGIFactory2`/`DCompositionCreateDevice`);
  `[GeneratedComInterface]`/`[GeneratedComClass]` for all cold/warm COM. **No `ComWrappers` on the hot
  path.** (Owned by dotnet10 §4 + hardened §4.2 — referenced, not redefined.)
- **No delegates on the paint path.** Recorder/batcher are static methods over spans + handles; the DrawList
  walk is `Walk<TSink>(ReadOnlySpan<byte>, ref TSink) where TSink : IDrawSink, allows ref struct`
  (devirtualized, no box — never reach `TSink` members through the interface type). The only delegates are
  at the user edge (`Component.Render`).
- **C# 14 user-defined compound assignment** for SoA accumulators (`RectAccum += rect`, dirty-rect/clip
  unions) — audited so the result is discarded and the target is a real variable (else silent re-alloc).
  `[InlineArray]` for small fixed buffers (`Ring8`, `Edges4`). `Unsafe.BitCast` for value reinterprets
  (`Handle↔ulong`, color/sortkey packing); `Unsafe.As` only for ref/`void**`. `SearchValues` for text
  classification (text seam). `FrozenDictionary` for build-once PSO/format tables. Dirty-flag column scan
  via guarded `Vector256` + `Vector128` fallback.
- **Thread confinement:** the render thread is the SOLE writer of every ComPtr / RhiHandleTable / PSO
  cache / UPLOAD ring / staging ring / glyph atlas page / tessellation slab / GPU fence / deferred-delete
  ring / swapchain / private DrawList arenas. `ThreadGuard.AssertWriter` throws deterministically in asserts
  builds (`[Conditional]`-erased from ship → production safety == CI coverage). `SceneFrame` transfers
  ComPtr ownership by **Move**, never shares by reference.
- **Trimming:** optional `Effects.D2D1` (FXC + transpiler) leaf referenced only by `Hosting`; a no-effects
  app trims it. RHI structs are blittable `[StructLayout(Sequential)]` POD; spans marshal as pointers; zero
  managed marshalling stubs at the seam (`[assembly: DisableRuntimeMarshalling]` on `Render`/`Pal`).

---

## 17. Cross-platform (macOS) boundary

**Portable (in `FluentGpu.Render`, pure C#):** DrawList encoding, recorder, RenderLane classifier, radix
batcher, OverlapGrid, SortKey, instance packing, tessellator, damage accumulator, layer/clip/blend policy,
gradient bake, image-resolve, all math (`Affine2D`, SDF parameterization), shader *source* (HLSL → cross-
compiled). Speaks only RHI/Text/PAL interfaces + POD DrawList.

**Windows leaves (Hosting-only):** `FluentGpu.Windows` D3D12/ (ComPtr/D3D12/DXGI/**DComp multi-visual present**/D3D12MA/
RTV/DSV/PSO cache/DXIL/`Present1`/`CopyBufferToTexture`/texture-staging ring); `FluentGpu.Windows` Pal/
(HWND/swapchain surface/`ISystemColors`/`IBackdropSource` Mica/`IVideoPresenter` DComp sibling visual);
`FluentGpu.Windows` DirectWrite/ (glyph raster into the portable atlas); optional `Effects.D2D1`.

**To add Metal:** implement `Rhi.Metal` (`MTLDevice`/`MTLRenderCommandEncoder`/`MTLRenderPipelineState`/
`CAMetalLayer` present + child layers for video), `Text.CoreText` (same atlas), `Effects.Metal` (MPS for
blur/backdrop), and a CoreAnimation video presenter. SPIR-V→MSL gives the shaders for free. The portable
`Render` assembly recompiles unchanged. **Per-primitive D2D-fallback debt is a tracked Metal-milestone
list** (D2D is a Windows-only crutch).

---

## 18. Failure / edge cases

- **Device-removed (TDR):** `DeviceLostToken` from `Submit`/`Present` HRESULT (sync, primary) + async wait
  (backstop). The render thread `Volatile.Write`s a reason word; UI/render rendezvous; Hosting recreates
  device + all RHI resources from retained POD descs + handle table (handles preserved, native rebuilt). No
  managed-tree loss.
- **Sustained GPU stall:** after buffers exhaust, a bounded per-frame UI block of timeout T (= one vsync);
  irreducible (same ultimate limit WinUI's compositor has). Transient hiccups are absorbed (compositor
  presents the last good frame).
- **Upload-ring / staging-ring overflow:** grow to new high-water (one-time alloc, old ring deferred-freed),
  re-record capped once/frame then multi-pass. Never silently truncate.
- **Glyph/image atlas eviction mid-frame:** live references pinned (frame START eviction); a batch-time
  UV-resolve miss uses the reserved overflow region; a run/image may split into N page-batches (tolerated).
- **Layer RT OOM:** degrade group opacity to per-instance approximate alpha (visually wrong for overlaps,
  no crash); log; full-redraw next frame.
- **Image self-eviction race:** pin-before-trim (the documented WaveeMusic race, fixed in the contract);
  request-epoch survives slot recycle (a late callback whose epoch ≠ the cell's current epoch is dropped →
  no wrong-art flash).
- **Video hole / `Place` desync:** hole-punch clear + canvas present + `IVideoPresenter.Place` commit in one
  DComp Commit → never a black hole or chrome-under-video frame.
- **Degenerate geometry** (zero-size rect, NaN radii, σ=0 shadow): clamped at record; zero-area quads culled
  before batching. **Huge σ** expanding beyond viewport: clamp expansion to viewport+margin (analytic `erf`
  still correct).
- **Self-intersecting / open paths:** trapezoidation handles any winding; FP degeneracy is fuzz-gated with a
  D2D golden fallback; open subpaths implicitly closed for fill, left open for stroke.
- **DPI change:** invalidate `PathRealizationCache` (scale in key), re-bake gradients if needed, full
  redraw; back buffer is physical px (DPI change without client-size change does not resize the swapchain).
- **Damage overflow / occluded:** full redraw; occluded window → 1Hz test-present.
- **Selection spanning many visual fragments (huge BiDi range):** `text.md`'s `GetSelectionRects` is
  caller-sized + bounded (`E_NOT_SUFFICIENT_BUFFER` → arena-grow-retry on its side); the recorder emits one
  `DrawSelectionRectCmd` per returned fragment, all coalescing into one `DrawInstanced` (§4.3). A selection
  larger than the viewport is naturally damage-clipped — only on-screen fragments record.
- **Zero-area / collapsed selection (caret only):** `Rect.Width≈0` from `GetSelectionRects` → culled before
  batching like any zero-area quad; the caret itself is the editable seam's concern (text §13, v2), not a
  selection rect.
- **Transparent light-dismiss scrim:** `ScrimBrush == 0` → culled from the GPU batch (no `DrawInstanced`),
  retained only as the `input-a11y` hit-target. A scrim is never silently dimmed.
- **Focus ring on a freed / scrolled-out node:** `input-a11y` §8.4 owns the lifecycle (stale gen → ring
  hidden; clip chain hides it when the node scrolls out of its viewport clip). The raster never sees a ring
  for a dead node — the overlay span simply re-records empty.
- **Dashed-ring degeneracy:** `DashPeriod` clamped ≥ 1 device px at record; a sub-pixel period collapses to a
  solid ring (the dash multiply saturates) — no shimmer, no divide-by-zero.

---

## Implemented from the gap analysis

These rows of [`core-fundamentals-gap-analysis.md`](../core-fundamentals-gap-analysis.md) §4 are folded
into CORE here — **no deferral**. This doc is the opcode-shape + rasterization authority for them; the
semantics/geometry/lifecycle live in the cross-referenced owners.

| Gap | Folded as | Where |
|---|---|---|
| **L1** — text-selection highlight render (`DrawSelectionRectCmd` over `GetSelectionRects`) | New POD opcode shape `DrawSelectionRectCmd` (per-visual-fragment, BiDi-correct, theme selection brush, behind-text z) + its batch-time lowering onto the existing `shape_fill` übershader (zero new PSO/pass) | §0 authority map, §3.6 shape, §3.6.1 sortkey, §4.3 raster, §18 edge cases |
| **L4** — overlay/portal scrim + dismiss-layer raster, and **focus-ring composition** in overlay/portal layers | New POD opcode shape `DrawScrimCmd` (modal dim / transparent light-dismiss / blur-promote) + the **shape + rasterization** of `DrawFocusRingCmd` (owned here; `input-a11y.md` emits it): ring on `shape_border`, one-bit dashed variant, and the `PassClass=Effect` + overlay-`RecordSeq` composition rule that guarantees a focus ring paints above the page-beneath and above its own scrim | §0 authority map, §3.6 shapes, §3.6.1 sortkey, §4.4 raster, §18 edge cases |

**What this doc deliberately does NOT own (referenced, not redesigned):** `SelectionState`
(anchor/extent/affinity) semantics + the selection-brush theme source → `input-a11y.md`; the
`GetSelectionRects` visual-fragment geometry → `text.md`; the overlay light-dismiss FSM, scrim push/pop
timing, and `FocusEngine` ring lifecycle/clip-anchoring → `input-a11y.md`; anchor placement-flip/nudge
geometry → `layout.md`; the `DrawOp` enum registration → `scene-memory.md`; the blur-backdrop acrylic pass a
scrim may promote to → `backdrop-effects-animation.md` (`PushLayer{Effect}` on the backdrop RT path).

**Surgical-addition invariants preserved:** every opcode here is `RenderLane.AnalyticSdf`, reuses an
existing PSO (zero new pipelines), forces zero offscreen passes (the blur-scrim case routes to the existing
backdrop layer path), respects the §8 color contract (premul-linear brushes, one CPU realize), and is a
clean-span citizen under the §11.1 reuse rule. **No part of the renderer was rewritten.**

---

## 19. Open questions

- `OQ-1` AA-fringe vs MSAA(4) on complex Bézier/icon paths — validate via the golden gate before locking
  MSAA out.
- `OQ-2` Non-separable blend coverage for v1 (separable + Overlay only vs full PDF/CSS set).
- `OQ-3` Grayscale glyph AA + good gamma vs subpixel ClearType in v1 (PSO provisioned for v2).
- `OQ-5` MSDF/Slug text for extreme scale / 3D — future.
- `OQ-6` Copy instance/vertex UPLOAD→DEFAULT vs read-from-UPLOAD (measure on real GPUs).
- `OQ-8` Frames-in-flight 2 vs 3 default (latency vs throughput).

*(`OQ-4` small-image atlas and `OQ-7` partial-present mechanism are now DECIDED — atlas is v1-required,
partial present is the persistent canvas RT.)*

---

## 20. Changed vs the original synthesis

Amendments folded into this actualization (everything else preserved from the original synthesis):

1. **Thread placement.** Renderer moved from "on the UI thread" to the **RENDER thread (phases 8–11)**,
   reading an immutable triple-buffered `SceneFrame`; render thread is the **sole ComPtr owner**; DrawList
   arenas are render-thread-private and **≥3-deep** (was 2-deep, UI-swapped). Single-thread-correct ships
   first; parallelism flips behind the `seam.race` gate. (hardened §2/§4.1/§6)
2. **Tessellator.** **DELETED ear-clipping**; replaced with one vetted **O(n log n) monotone/trapezoidal
   sweep**. Separated the **complexity-bound (SAFE-by-construction)** from **geometric correctness
   (fuzz-gated + differential-rasterizer cross-check + D2D golden fallback)**. (hardened §4.3)
3. **RenderLane classifier** added — SDF default, paths the exception — plus a tessellation-fraction
   tripwire. (hardened §4.3)
4. **OverlapGrid painter-order batching** added (stored last-writer; grid break + radix tie-break derive
   from one `RecordSeq`); **SortKey re-laid-out** with paint-order sequence PRIMARY and `PassClass`
   demoted (folds the painter-order BLOCKER). (architecture-spec §5.2, hardened §4.3)
5. **Color contract** made explicit and pinned: `BGRA8_UNORM` buffer / `BGRA8_UNORM_SRGB` RTV / linear
   blend + resolve / premultiplied linear output / **text gamma as a deliberate exception**.
   (architecture-spec §5.2)
6. **3-tier clip** retained but with the stencil sub-protocol (DSV, dedicated non-reorderable pre-pass,
   INCR/DECR_SAT nesting) and `PushStencilClipCmd`/`PopStencilClipCmd` opcodes. (architecture-spec §5.2)
7. **`DrawImageCmd` amended** to an **`ImageHandle → ImageRealization` indirection** + `Radii` + `CrossFade`
   + `Stretch` + `Clip` (was a raw `TextureHandle`); batcher gains the **`ImageRef` UV-resolve branch**;
   **small-image atlas promoted `OQ-4` → v1 required**; image brushes reconciled onto the same indirection.
   (WaveeMusic §3.1)
8. **`DrawVideoCmd` added** (hole-punch, sortkey `PassClass` below chrome) + the **multi-visual DComp
   present tree** + `IVideoPresenter`/`IBackdropSource`/`ISystemColors` PAL seams; hole + present + `Place`
   in one DComp Commit. (WaveeMusic §3.4)
9. **RHI delta `CopyBufferToTexture` + dedicated texture-staging ring + startup per-bucket texture pool**
   (no `CreateTexture` in phases 6–13); corrected the original claim that texture upload rides the instance
   `UploadRing`. (WaveeMusic §3.1)
10. **Clean-span reuse rule amended** to require `ContentEpoch` unchanged for `GlyphRunRef`/`ImageRef` AND a
    **baked-geometry hash** unchanged, via a single `Mutate()` chokepoint + DEBUG `CleanSpanWitness`;
    epoch validation render-thread-LOCAL. (architecture-spec §4.5/§5.4, hardened §4.4)
11. **Partial present DECIDED** (`OQ-7`): engine-owned **persistent canvas RT** with `LoadOp.Load`
    scissored repaint, DComp-composited; `Present1` dirty-rects are a DWM hint only; damage from four
    transformed corners, inflated by effect extent, repainting all z-order intersectors, ≤16 rects →
    full-frame, rounding OUT at the RHI leaf. (architecture-spec §5.2)
12. **AA quality** re-labeled a **"corpus-gated regression net"** (16× supersampled CPU reference + CIEDE2000
    + edge-shift + A/B-vs-DWrite), **not** a "validated property"; uncovered-input caveat stated.
    (hardened §4.3, painpoints §5)
13. **Shaders** confirmed HLSL→DXC→DXIL `byte[]` (source-gen embedded, build-time, AOT-clean), shared root
    signature; PSO pre-warm scoped to the native set (Custom/effect/D2D runtime-warmed). (architecture-spec
    §5.2)
14. **Off-thread tessellation/glyph-raster DESCOPED** behind the render-thread seam; on-UI tessellation +
    geometry cache ships first (folds the painpoints "new synchronous work on the one thread" critique with
    the honest sequencing). (hardened §4.3/§6, painpoints §5/§95)
15. **Allocator substrate** updated to ChunkedArena-aware language, `GC.AllocateUninitializedArray(pinned)`
    backing, `IBufferWriter<byte>`-over-arena writer, the `allows ref struct` DrawList walk, C# 14 SoA
    compound-assignment accumulators, and the explicit "0 alloc in phases 6–13, verified by alloc-tripwire +
    BDN backstop" claim. (dotnet10 §A/§B/§G)
16. **COM hardening** referenced (generated-from-`*.comabi.json`, runtime-self-checked, ComPtr render-thread-
    confined + Move-only) rather than the original "ComWrappers source-generated" hand-wave. (hardened §4.2,
    dotnet10 §4)
17. **`OQ-4` and `OQ-7` removed from open questions** (now decided); `FA-1`/`FA-2` folded as accepted
    contract amendments (sRGB pin, parallel `ulong[]` SortKeys arena).
18. **Selection/overlay opcode shapes folded into core** (gap rows L1/L4): new POD opcodes
    `DrawSelectionRectCmd` (text-selection highlight, per-BiDi-visual-fragment, behind-text z) and
    `DrawScrimCmd` (modal-dim / light-dismiss / blur-promote overlay layer), plus the **shape + rasterization** of
    `DrawFocusRingCmd` (owned here; `input-a11y.md` §8.4 emits it) including the dashed reveal-focus ring and the
    overlay/portal composition rule. All three lower onto the **existing `shape_fill`/`shape_border`
    übershaders** — zero new PSO, zero new pass, `RenderLane.AnalyticSdf`, clean-span citizens. (§3.6, §4.3,
    §4.4; gap-analysis §4 L1/L4.)

---

### Cross-references (shared contracts — not duplicated here)
- **Threading / publish / quarantine / retire-fence:** [hardened-v1-plan §2, §4.1](../hardened-v1-plan.md)
- **COM binding generation / ComPtr confinement:** [hardened-v1-plan §4.2](../hardened-v1-plan.md) + [dotnet10 §4](../dotnet10-csharp14-zero-alloc.md)
- **Handles / allocators / ChunkedArena / `IVirtualMemory`:** [foundations.md](../foundations.md)
- **SceneStore SoA columns / dirty axes / DrawList physical format:** [architecture-spec §4.1–4.5](../architecture-spec.md)
- **RHI/PAL seam shape, present tree, new PAL seams:** [architecture-spec §4.7, §5.1](../architecture-spec.md)
- **Hooks / reconcile / memo-skip / effect timing:** [architecture-spec §5.6](../architecture-spec.md) + [hardened §4.4](../hardened-v1-plan.md) + [subsystems/reconciler-hooks.md](./reconciler-hooks.md)
- **Text shaping / glyph atlas / `GlyphRunTable` / `PackedGlyph`:** [subsystems/text.md](./text.md)
- **Image decode/residency, video registry, lyrics:** [app-requirements-waveemusic.md §3.1, §3.4](../app-requirements-waveemusic.md) (`FluentGpu.Media`)
- **.NET 10 / C# 14 zero-alloc + AOT patterns:** [dotnet10-csharp14-zero-alloc.md](../dotnet10-csharp14-zero-alloc.md)
