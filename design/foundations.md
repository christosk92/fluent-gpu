# fluent-gpu — ARCHITECT-OF-RECORD: Shared Contracts (v1, AUTHORITATIVE)

These contracts are binding. The 8 specialist agents design *within* them. Where an agent
needs a new type, it goes in the namespace/assembly fixed in §7 and obeys the handle/alloc
rules in §1. Disputes resolve toward: **footprint, zero per-frame alloc, NativeAOT, cross-platform seam**.

Decisions already locked by the user (not relitigated): own batched 2D renderer on **D3D12**
(D3D11 dropped as reference — ComputeSharp is DX12-only and is our COM-interop seed), DirectWrite
glyphs, DComp present, pure Win32 (no WinRT/WinAppSDK), NativeAOT, keep Reactor programming model
but patch OUR RenderNode tree.

Legend: `▽` = a value with sub-pixel coverage (alpha mask). All structs are `[StructLayout(Sequential)]`
unless noted `Explicit`. Sizes are x64.

---

## 1. CORE PRIMITIVES & MEMORY (`FluentGpu.Foundation`)

### 1.1 Generational handles — the universal hot-path reference

> RULE: **On hot paths (per-frame: scene mutation, layout, drawlist record, GPU submit, input
> hit-test, animation tick) NOTHING holds a GC reference into a pooled collection.** Everything
> refers to slab/arena/pool entries by a generational handle. GC references appear ONLY at the
> "edges": user delegates (event handlers, effects), `Element` records (immutable, short-lived,
> reconcile-only), `Component` instances, and COM `ComPtr<T>` ownership roots.

A handle is a 64-bit POD: **32-bit index + 32-bit generation** (canonical: `architecture-spec.md` §4.1;
see [`SPEC-INDEX.md`](./SPEC-INDEX.md)). Generation is bumped on alloc AND free and defeats ABA (a freed
slot reused for a different node yields a stale handle that fails validation). **The kind tag is NOT
packed into the handle bits** — it lives on the zero-cost typed wrapper as a `[Conditional("DEBUG")]`
assert (the wrapper statically knows its kind). This buys the full 32-bit generation back (2^31 alloc/free
cycles per slot ≈ 400 days/slot at 60 fps) and removes the park-slot-forever-on-wrap policy that the
earlier handle form forced — a policy that violated the no-growth invariant.

```csharp
namespace FluentGpu.Foundation;

// 8 bytes. {u32 index, u32 gen}. Kind is on the typed wrapper, NOT in the bits.
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct Handle : IEquatable<Handle>
{
    public readonly uint Index;     // slot index into the owning slab column (0 = null sentinel)
    public readonly uint Gen;       // 32-bit generation; bumped on alloc AND free (ABA defense)

    public bool   IsNull => Index == 0 && Gen == 0;          // index 0 / gen 0 reserved = Null
    public ulong  Packed => Unsafe.BitCast<Handle, ulong>(this);   // for DepKey/sortkey packing
    public bool   IsLive(uint slotGen) => Gen == slotGen;    // the one validation, in DEBUG hot paths
    public bool   Equals(Handle o) => Index == o.Index && Gen == o.Gen;
    public override int GetHashCode() => (int)(Index * 2654435761u ^ Gen);
    public static readonly Handle Null = default;
}

// HandleKind is carried by the typed wrapper's DEBUG assert (below), NOT packed into Handle.
public enum HandleKind : byte
{ None=0, Node=1, Brush=2, Clip=3, Layer=4, GlyphRunRef=5, ImageRef=6, GpuBuffer=7,
  Texture=8, Pipeline=9, FontFace=10, AnimTrack=11, EffectChain=12, PathGeom=13 }
```

Strongly-typed wrappers (zero-cost; `readonly struct` over `Handle`) prevent cross-kind mixups
at compile time. Specialists MUST use the typed forms in public signatures:

```csharp
public readonly struct NodeHandle    { internal readonly Handle H; }  // Kind=Node
public readonly struct BrushHandle   { internal readonly Handle H; }
public readonly struct ClipHandle    { internal readonly Handle H; }
public readonly struct TextureHandle { internal readonly Handle H; }
public readonly struct BufferHandle  { internal readonly Handle H; }
public readonly struct PipelineHandle{ internal readonly Handle H; }
public readonly struct FontFaceHandle{ internal readonly Handle H; }
// ... one per HandleKind. All blittable, all 8 bytes.
```

### 1.2 The four allocator archetypes

All live in `FluentGpu.Foundation`. All are class instances owned at the edge (per-window/per-device),
but their *contents* are addressed by handle/offset on hot paths.

```
┌─ SlabAllocator<T> where T : unmanaged ────────────────────────────────────┐
│ Stable, generationally-versioned storage for long-lived hot structs        │
│ (RenderNode SoA columns, Brush table, Clip table, GlyphRun table).         │
│   T[] _items;  uint[] _generations;  int[] _freeList;  int _freeHead;      │
│   int _count, _capacity;                                                   │
│   Handle Alloc(in T value);              // pop free slot or grow; gen++    │
│   ref T   Get(Handle h);                 // validates gen; throws on stale  │
│   bool    TryGet(Handle h, out ...);     // hot path: returns false stale   │
│   void    Free(Handle h);                // push slot to free list; gen++   │
│ Growth: geometric ×2; never shrinks (footprint stable across frames).      │
└────────────────────────────────────────────────────────────────────────────┘

┌─ ArenaAllocator (bump / linear) ───────────────────────────────────────────┐
│ Per-frame scratch. ONE contiguous byte region; bump pointer; O(1) Reset().  │
│ Backs the DrawList command stream, transient layout scratch, LIS scratch.   │
│   byte[] _buffer; int _head;                                                │
│   Span<byte> Alloc(int bytes, int align);                                   │
│   ref T AllocStruct<T>() where T: unmanaged;                                │
│   int  Mark(); void ResetTo(int mark); void Reset();   // frame boundary    │
│ NEVER stores cross-frame state. Cleared (head=0) at frame start.            │
└────────────────────────────────────────────────────────────────────────────┘

┌─ ObjectPool<T> where T : class, new() ─────────────────────────────────────┐
│ For the unavoidable GC objects we want to recycle: Component RenderContext, │
│ HookState boxes, ChildReconciler scratch dictionaries. Per-type stack,      │
│ cap 32 (Reactor ElementPool contract). Explicit Reset() on return — no      │
│ reflection. Used at the EDGE, not the per-frame hot loop.                   │
└────────────────────────────────────────────────────────────────────────────┘

┌─ HandleTable<T> (= SlabAllocator specialization for COM/GPU resources) ─────┐
│ Maps Texture/Buffer/Pipeline/FontFace handles → owned ComPtr<T> + metadata. │
│ The ONLY place GC/COM ownership meets handles. RHI hands out handles;       │
│ internally keeps ComPtr alive. Disposed at device teardown.                 │
└────────────────────────────────────────────────────────────────────────────┘
```

Free-list strategy: **intrusive singly-linked free list inside the slab** (`_freeList[i]` = next
free index, `_freeHead` = head). Alloc pops head; Free pushes. `_generations[i]++` on *both*
Alloc and Free so any handle captured before a Free is invalidated. Generation wraps at 2^24
(~16M reuses of one slot — practically unreachable in a session; documented limitation).

### 1.3 Strings & interning

- **No per-frame string allocation.** Text content reaches the renderer as already-shaped glyph
  runs (§5), never as `string` on the paint path.
- `StringId` = interned 32-bit id. `StringInterner` (in Foundation) is an append-only
  `Dictionary<string,int>` + `string[]` reverse table, populated at the *edge* (theme keys,
  font family names, accessibility names, attached-property keys). Hot paths compare `StringId`
  (int) not `string`.
- Element `Key` stays `string?` (reconcile-only, edge), but the keyed reconciler hashes to
  `StringId` for its scratch map to avoid string hashing churn.
- Font family / theme resource keys: interned once at registration; resolved to `FontFaceHandle`
  / `BrushHandle` thereafter.

### 1.4 Span-based mutation conventions (binding on all specialists)

- Public mutation APIs take `ReadOnlySpan<T>`/`Span<T>`, never `params T[]` on hot paths.
  Hook dependencies: `ReadOnlySpan<DepKey>` (see §6.4), NOT `params object[]`.
- "Get a ref, mutate in place": `ref T Get(Handle)` returns a `ref` into the slab; callers mutate
  fields directly (no read-modify-write copy). Marked `[MethodImpl(AggressiveInlining)]`.
- COM: `ComPtr<T>` (vendored from ComputeSharp, `unsafe struct`, `T : unmanaged`) is the ONLY
  COM lifetime primitive. Raw `T*` only inside the OS backend (`FluentGpu.Windows` D3D12/, Pal/, DirectWrite/).
- No `IEnumerable`/LINQ/iterator state machines on per-frame paths. Index loops only.
- `EqualityComparer<T>.Default` is allowed (AOT-safe singleton); custom struct `Equals` preferred.

---

## 2. RETAINED TREE — `RenderNode` storage (`FluentGpu.Scene`)

### Decision: **Struct-of-Arrays (SoA), columns in a SlabAllocator-backed `SceneStore`.**

Justification vs Array-of-Structs:
- Layout, render-walk, and dirty-scan each touch *disjoint subsets* of fields. SoA streams only
  the hot columns (e.g. dirty-scan reads only `Flags[]`), maximizing cache-line utilization —
  AoS would drag full ~160B nodes through cache for a 4-byte flag read.
- SoA columns are plain `T[]` → trivially `Span<T>`-iterable, SIMD-friendly (transform compose,
  bounds union), and resize independently.
- A single shared generation/free-list (one `SlabAllocator` "spine") indexes all columns by
  `NodeHandle.Index`, so handle validation is one check regardless of column count.
- Matches WinUI's lesson (avoid per-element COM objects / sparse property bags): our node is flat
  data, no inheritance, no property system.

```csharp
namespace FluentGpu.Scene;

public sealed class SceneStore
{
    // ── spine: generations + free list (the SlabAllocator core) ──
    uint[]  _generation;     // [i] bumped on alloc/free
    int[]   _freeNext;       // intrusive free list
    int     _freeHead, _count, _capacity;

    // ── topology columns (handles, not refs) ──
    int[]   Parent;          // NodeHandle.Index of parent, -1 root
    int[]   FirstChild;      // -1 none
    int[]   NextSibling;     // -1 none  (singly-linked sibling list; prev not needed for paint)
    int[]   PrevSibling;     // kept for O(1) reorder during reconcile
    int[]   ChildCount;

    // ── identity / reconcile linkage (edge-ish, but cheap to colocate) ──
    StringId[]  Key;             // 0 = none
    ushort[]    ElementTypeId;   // source-gen'd per Element record type (no System.Type on hot path)
    int[]       ComponentSlot;   // -1, or index into ComponentTable for Component/Func nodes

    // ── LAYOUT INPUT (mirrors YogaStyle subset that affects this node) ──
    // Co-located here so the layout pass reads node→style without chasing a YogaNode object.
    LayoutInput[] Layout;        // struct: width/height/min/max, margin/padding (4 edges),
                                 // flex dir/grow/shrink/basis, align, justify, position kind, gap.
                                 // ~96B; see FluentGpu.Layout. Index-parallel to YogaNode if used.

    // ── LAYOUT RESULT (arrange output; the bridge layout→paint) ──
    LayoutRect[] Bounds;         // struct { float X,Y,W,H } absolute, post-arrange, pixel-snapped
    // (margin/border/padding result edges live in LayoutResult side-table only if needed;
    //  paint needs final content rect = Bounds.)

    // ── PAINT columns (consumed by the render-walk → DrawList) ──
    Affine2D[]   LocalTransform; // 2x3 float affine (rotation/scale/translate/skew); identity common
    float[]      Opacity;        // 0..1; 1 common
    BrushHandle[] Fill;          // background/fill brush; Null = none
    BrushHandle[] Stroke;        // border brush;          Null = none
    float[]      StrokeWidth;
    CornerRadius4[] Corners;     // float TL,TR,BR,BL  (rounded-rect/border)
    ClipHandle[] Clip;           // explicit geometry clip; Null = inherit/none
    NodeVisual   VisualKind;     // enum: Container, RoundRect, Text, Path, Image, Backdrop

    // ── per-kind payload reference (into side tables, by handle/index) ──
    int[]        PayloadRef;     // Text→GlyphRunSet index; Path→PathGeom; Image→ImageRef; else -1

    // ── DIRTY / STATE flags (the hot scan column) ──
    NodeFlags[]  Flags;          // see below; single 32-bit read per node for invalidation
}
```

```csharp
[Flags] public enum NodeFlags : uint
{
    None            = 0,
    LayoutDirty     = 1<<0,   // measure/arrange needed for this subtree
    LayoutSelfDirty = 1<<1,   // only own measure (size-affecting prop changed)
    TransformDirty  = 1<<2,
    PaintDirty      = 1<<3,   // visual prop changed; re-record drawlist for subtree
    ClipDirty       = 1<<4,
    OpacityDirty    = 1<<5,
    SubtreeDirty    = 1<<6,   // a descendant is dirty (propagated UPWARD, lazy — WinUI lesson)
    NeedsLayer      = 1<<7,   // opacity<1 OR backdrop OR clip-with-AA → push offscreen layer
    Hidden          = 1<<8,   // Visibility.Collapsed (skip layout+paint)
    Hittable        = 1<<9,   // participates in hit-test
    Focusable       = 1<<10,
    HasHandlers     = 1<<11,  // pointer/key handlers present (input dispatch gate)
    Realized        = 1<<12,  // has a cached realization (text/path) still valid
}
```

### Memory layout of the key co-located structs (x64)

```
LayoutRect      16B : float X, Y, W, H
Affine2D        24B : float m11,m12,m21,m22,dx,dy           (2x3 row-major)
CornerRadius4   16B : float TL,TR,BR,BL
LayoutInput    ~96B : sizing + flex + spacing (see §Layout owner)
```

**How layout data and paint data co-locate (and stay decoupled):**
`LayoutInput` (read by measure) and `Bounds` (written by arrange) are columns in the SAME store as
the paint columns. The layout pass writes `Bounds[i]`; the render-walk reads `Bounds[i]` plus
`LocalTransform/Opacity/Fill/...`. One `NodeHandle.Index` indexes all. No separate "layout tree" /
"visual tree" duality (WinUI's HWCompNode mirror is the heaviness we explicitly reject) — there is
ONE node store; the *DrawList* (§3) is the only derived per-frame artifact.

Yoga integration: the ported `YogaAlgorithm` (full reuse, WinUI deps stripped) operates over a thin
`YogaNode` facade whose `Style` getters read `SceneStore.Layout[i]` and whose `LayoutResults` writer
writes `SceneStore.Bounds[i]`. Measure callbacks for text route through the Text seam (§5). The
8-entry `CachedMeasurement` ring (confirmed in `LayoutResults.cs`) is preserved per node in a
parallel `LayoutCache[]` side-column owned by `FluentGpu.Layout`.

---

## 3. DRAWLIST ENCODING (`FluentGpu.Render`)

### Decision: **Flat, contiguous, POD command stream in the per-frame `ArenaAllocator`,
### rebuilt-per-dirty-subtree; brushes/clips/glyphruns referenced by handle (stable tables).**

The DrawList is the seam between the render-walk (CPU, produces commands) and the batcher (CPU,
sorts/instances) → RHI (GPU). It is *retained per top-level layer* and only the dirty subtrees are
re-recorded (incremental); clean subtrees keep their previously-recorded command span (the
"rebuild-vs-incremental" story below).

### 3.1 Stream physical format

```
Arena byte region (one per layer/window):
┌──────────┬───────────────────────────┬──────────┬─────────────────────────┐
│ DrawCmd  │ payload (cmd-specific POD) │ DrawCmd  │ payload ...             │
│  (header)│  fixed size per opcode     │ (header) │                         │
└──────────┴───────────────────────────┴──────────┴─────────────────────────┘
```

```csharp
namespace FluentGpu.Render;

public enum DrawOp : byte
{
    FillRoundRect, FillRect, StrokeRoundRect,
    DrawGlyphRun, FillPath, StrokePath,
    DrawImage, DrawBackdrop,
    PushClipRect, PushClipPath, PopClip,
    PushLayer, PopLayer,         // layer carries opacity + optional effect chain
    PushTransform, PopTransform, // 2x3 affine; stack composed on CPU but emitted for GPU clip math
    SetScissor,
}

// 8-byte header; payloads are separate fixed-size POD structs, arena-adjacent.
[StructLayout(LayoutKind.Sequential)]
public struct DrawCmd
{
    public DrawOp Op;       // 1
    public byte   Flags;    // 1  (e.g. AA mode, premultiplied)
    public ushort PayloadSz;// 2  (bytes following header)
    public int    SortKey;  // 4  (layer/z + pipeline class for batch sorting)
}
```

### 3.2 Representative payloads (all blittable, handle/offset references only)

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct FillRoundRectCmd          // 56B
{ public LayoutRect Rect; public CornerRadius4 Radii; public BrushHandle Brush; public float AA; }

[StructLayout(LayoutKind.Sequential)]
public struct DrawGlyphRunCmd           // glyphs already shaped+atlased
{ public int GlyphRunRef;   // index into GlyphRunTable (StringId-free; ids+positions+atlas page)
  public BrushHandle Brush; // text color
  public float BaselineX, BaselineY; }

public struct FillPathCmd
{ public int PathGeomRef;   // tessellated vertex span in the frame's vertex arena
  public BrushHandle Brush; public byte FillRule; }

public struct PushLayerCmd
{ public float Opacity; public int EffectChainRef; /* -1 none */ public LayoutRect Bounds; }

public struct PushClipRectCmd { public LayoutRect Rect; public CornerRadius4 Radii; }
public struct DrawImageCmd    { public TextureHandle Tex; public LayoutRect Dst; public LayoutRect SrcUV; public float Opacity; }
```

### 3.3 Reference model (zero embedded heap data)

- **Brushes** → `BrushHandle` into `SceneStore`-adjacent `BrushTable` (a `SlabAllocator<BrushData>`).
  `BrushData` is a tagged union POD: `{ BrushKind kind; ColorF solid; GradientRef grad; TextureHandle img; }`.
  Gradients store stops in a side slab; referenced by `GradientRef` (offset+count). No GC.
- **Clips** → `ClipHandle` into `ClipTable` (`SlabAllocator<ClipData>`: rect+radii, or PathGeomRef).
- **GlyphRuns** → `GlyphRunRef` int index into the frame `GlyphRunTable` (built during render-walk
  from cached shaping; see §5). Contains: `FontFaceHandle, float fontSize, AtlasPage, Span<PackedGlyph>`
  where `PackedGlyph { ushort atlasX, atlasY, w, h; float advanceX, offsetX, offsetY; }`.
- **Paths** → `PathGeomRef` index into the frame vertex arena (tessellated triangles) OR a retained
  `PathGeom` slab entry if the geometry is stable across frames (realization cache).
- **Effects** → `EffectChainRef` into `EffectTable` (blur radius, drop-shadow params, backdrop kind).

### 3.4 Zero-alloc rebuild-vs-incremental story

```
Per frame:
  1. arena.Reset()                          // O(1), reclaims all last-frame command bytes
  2. For each ROOT LAYER:
       if (layer.RootNode flags has SubtreeDirty | PaintDirty)
            RecordLayer(layer)              // walk subtree, append DrawCmds into arena
            layer.CommandSpan = (markStart..arena.head)   // remember span
       else
            // CLEAN: copy retained command bytes? NO — instead keep last frame's arena alive.
```

Refinement (the actual contract): we keep **two arenas, double-buffered** (`_front`, `_back`).
Clean layers' command spans are *memcpy'd* from back→front arena (a single `Buffer.MemoryCopy`,
no per-command work, no allocation — handles/offsets inside remain valid because tables are
retained slabs, not arena data). Dirty layers are re-recorded fresh into the front arena. At
frame end, swap. This gives:
- Zero managed allocation per frame (arenas are pre-grown `byte[]`, reused).
- Incremental cost ∝ dirty subtree size + a bulk copy of clean spans (cache-friendly, no branching).
- Full rebuild = special case where every layer is dirty (first frame, resize, theme change).

GPU upload: the batcher walks the final front-arena command stream once, sorts by `SortKey`,
coalesces `FillRoundRect`/`DrawGlyphRun` into **instanced quad batches** (one instance buffer per
pipeline class), and emits RHI draw calls. Instance data (per-quad: rect, radii, brush params,
atlas UV) is written into a `BufferHandle`-backed upload ring (§4).

---

## 4. PAL + RHI SEAM (`FluentGpu.Pal`, `FluentGpu.Rhi`)

> Portability rule: **the `Seams/Pal` and `Seams/Rhi` folders (namespaces `FluentGpu.Pal`/`FluentGpu.Rhi`)
> are interface-only (+ POD descs); the OS backend `FluentGpu.Windows` (Pal/ / D3D12/) holds the reference
> impls. Engine code (Scene, Render, Layout, Reconciler) depends ONLY on the interfaces.** A Metal/CoreText backend later
> implements the SAME interfaces with zero engine changes.

### 4.1 PAL — platform/OS seam

```csharp
namespace FluentGpu.Pal;

public interface IPlatformApp                // app/process lifetime + factory
{
    IPlatformWindow CreateWindow(in WindowDesc desc);
    IPlatformAppLoop CreateAppLoop();
    IClipboard Clipboard { get; }
    double GetDisplayScale(IPlatformWindow w);  // DPI; portable concept
}

public interface IPlatformWindow
{
    nuint NativeHandle { get; }              // HWND on Win; NSView* on mac. Opaque to engine.
    Size2 ClientSize { get; }
    double Scale { get; }
    event Action<Size2> Resized;
    event Action<InputEvent> Input;          // unified InputEvent (see §6)
    event Action Closed;
    void SetTitle(ReadOnlySpan<char> t);
    void Invalidate();                        // request a frame
}

public interface IPlatformAppLoop            // message pump / run loop
{
    // Drives frames; calls back the registered frame callback at vsync cadence.
    void Run(Action<FrameTime> onFrame);
    void RequestStop();
    void Post(Action work);                   // marshal to UI thread (Reactor's dispatcher analog)
    bool HasThreadAccess { get; }
}

public interface IClipboard
{ bool TryGetText(out string text); void SetText(ReadOnlySpan<char> text); /* + data formats */ }

public interface IImeSession                 // composition/candidate window
{ void SetCompositionRect(in LayoutRect caretRect); void Enable(bool on);
  event Action<ImeCompositionEvent> Composition; }
```

| PAL piece            | Windows-specific impl                       | Portable contract |
|----------------------|---------------------------------------------|-------------------|
| `IPlatformWindow`    | HWND, `RegisterClassEx`, `WndProc`          | size/scale/input/handle |
| `IPlatformAppLoop`   | `GetMessage`/`PeekMessage`, `DispatcherQueue`-free custom pump | run/post/stop |
| `IClipboard`         | `OpenClipboard`/`GetClipboardData`          | text + format blobs |
| `IImeSession`        | `ITfThreadMgr` / `WM_IME_*`                 | composition rect + events |
| DPI                  | `GetDpiForWindow`                           | `double Scale` |

Windows-specific (NOT in portable interface, lives in `FluentGpu.Windows` Pal/): HWND, WndProc, raw `WM_*`,
TSF, win32 cursor/caret. Everything the engine sees is the portable `InputEvent`/`Size2`/scale.

### 4.2 RHI — render hardware seam (graphics pipeline; D3D12 reference)

> Designed graphics-first (vertex+pixel+swapchain+present), which ComputeSharp does NOT provide —
> ComputeSharp seeds only the COM bindings + `ComPtr<T>` + device/queue/fence/pool patterns. RHI is
> a hand-rolled graphics layer over those bindings.

```csharp
namespace FluentGpu.Rhi;

public interface IGpuDevice : IDisposable
{
    ISwapchain      CreateSwapchain(in SwapchainDesc d, IPlatformWindow window);
    IGpuBuffer      CreateBuffer(in BufferDesc d);          // vertex/index/instance/constant/upload
    ITexture        CreateTexture(in TextureDesc d);        // atlas pages, layer RTs, images
    IPipeline       CreatePipeline(in GraphicsPipelineDesc d);// PSO (vs+ps+blend+raster+rootsig)
    IShaderModule   CreateShader(ReadOnlySpan<byte> dxil);  // precompiled bytecode (AOT: embedded)
    ICommandEncoder BeginFrame();                            // rents a command list from pool
    void            Submit(ICommandEncoder enc);             // execute + signal fence
    void            WaitIdle();
    bool            IsDeviceLost { get; }                    // triggers resource recreation
    GpuLimits       Limits { get; }
}

public interface ISwapchain : IDisposable
{
    ITexture AcquireBackbuffer();        // current RT
    void     Present(PresentMode mode);  // DXGI Present → DComp commit on Windows
    void     Resize(Size2 px);
    Size2    Size { get; }
}

public interface ICommandEncoder        // records ONE frame's GPU work; not retained
{
    void BeginRenderPass(ITexture target, in ClearValue clear, in Viewport vp);
    void SetPipeline(IPipeline p);
    void SetScissor(in RectI r);
    void BindVertexBuffer(IGpuBuffer vb, int stride);
    void BindInstanceBuffer(IGpuBuffer ib, int stride);
    void BindIndexBuffer(IGpuBuffer ib, IndexFormat fmt);
    void BindConstants(int slot, IGpuBuffer cb);
    void BindTexture(int slot, ITexture t);      // glyph atlas, image, layer-as-input
    void DrawInstanced(int vtxPerInst, int instCount, int baseVtx, int baseInst);
    void EndRenderPass();
    // layer compositing handled as render-pass-to-texture then a textured-quad draw.
}

public interface IGpuBuffer : IDisposable
{ BufferKind Kind { get; } int SizeBytes { get; }
  Span<byte> Map();         // upload-heap ring; no per-frame alloc
  void Unmap(); }

public interface ITexture : IDisposable
{ Size2 Size { get; } TextureFormat Format { get; } bool IsRenderTarget { get; } }

public interface IPipeline : IDisposable { }     // opaque PSO handle
public interface IShaderModule : IDisposable { }
```

| RHI concept     | D3D12 reference (Windows D3D12/)                           | Metal later (Windows.Mac Metal/) |
|-----------------|-----------------------------------------------------------|----------------------------|
| `IGpuDevice`    | `ID3D12Device` + queues + fences (ComputeSharp pattern)   | `MTLDevice` |
| `ISwapchain`    | `IDXGISwapChain3` + `IDCompositionTarget/Visual` present  | `CAMetalLayer` |
| `ICommandEncoder`| `ID3D12GraphicsCommandList` (pooled per ComputeSharp)    | `MTLRenderCommandEncoder` |
| `IGpuBuffer`    | `ID3D12Resource` (upload/default heap) + D3D12MA optional | `MTLBuffer` |
| `ITexture`      | `ID3D12Resource` + RTV/SRV descriptor (NEW vs ComputeSharp's SRV/UAV-only) | `MTLTexture` |
| `IPipeline`     | `ID3D12PipelineState` + `ID3D12RootSignature`             | `MTLRenderPipelineState` |
| `IShaderModule` | DXIL bytecode (embedded at build; no runtime fxc)          | MSL/metallib |
| present compose | **DirectComposition** (`IDCompositionDevice`, flat C API) | CoreAnimation |

Windows-specific (in `FluentGpu.Windows` D3D12/ / Pal/ only): all `ComPtr<T>`, raw COM vtables, DXGI,
D3D12, **DComp** (NEW binding we author), **DWrite** (NEW, in the DirectWrite/ impl). RHI/PAL interfaces above
contain ZERO COM types — only POD descs, handles, spans. This is the swap line.

Seed-from-ComputeSharp (vendored into `FluentGpu.Windows` D3D12/ + Interop/): `ComPtr<T>`, DXGI + D3D12 bindings, command-list
pool, fence/device-lost pattern, ILLink substitutions, source-gen scaffolding. NEW (author): RTV/DSV
descriptor mgmt, `GraphicsPipelineDesc`→PSO, vertex/instance/index buffers, swapchain+DComp present.

---

## 5. TEXT SEAM (seam `FluentGpu.Text` = Engine Seams/Text/, impl `FluentGpu.Windows` DirectWrite/)

> All text is shaped+rasterized into a GPU glyph atlas; the paint path never sees `string`.
> DirectWrite implements the seam on Windows; CoreText implements the SAME seam on macOS later.

```csharp
namespace FluentGpu.Text;

public interface IFontSystem : IDisposable
{
    FontFaceHandle GetFace(StringId family, FontWeight w, FontStyle s, FontStretch st);
    FontFaceHandle GetFallbackFace(uint codepoint, in FontQuery q);   // fallback chain
    IFontFace      Resolve(FontFaceHandle h);
    ITextShaper    Shaper { get; }
    IGlyphRasterizer Rasterizer { get; }
    IGlyphAtlas    Atlas { get; }
}

public interface IFontFace
{ FontFaceHandle Handle { get; } FontMetrics Metrics { get; }   // ascent/descent/lineGap/unitsPerEm
  bool TryGetGlyphIndex(uint codepoint, out ushort glyphId); }

// Shaping: text + run props -> shaped glyphs. Caller owns output buffers (span, zero-alloc).
public interface ITextShaper
{
    void Shape(ReadOnlySpan<char> text, in ShapeParams p, ref ShapedRunBuilder outRun);
    // ShapeParams: FontFaceHandle, fontSizePx, ScriptTag, Direction (LTR/RTL), language, features.
}

// ShapedRunBuilder fills caller-provided spans:
public ref struct ShapedRunBuilder
{
    public Span<ushort> GlyphIds;     // out count via Written
    public Span<float>  Advances;     // per-glyph advance (x)
    public Span<float>  OffsetsX;     // per-glyph x offset
    public Span<float>  OffsetsY;     // per-glyph y offset
    public Span<int>    Clusters;     // glyph→source char-cluster map (caret/selection/hit-test)
    public int Written;
}

// Rasterization: glyph -> alpha (or RGB ClearType) mask. Caller passes target span or atlas page.
public interface IGlyphRasterizer
{
    GlyphRasterResult Rasterize(FontFaceHandle face, ushort glyphId, float sizePx,
                                in SubpixelOffset sp, GlyphRenderMode mode, Span<byte> dst);
    // mode: Alpha8 (grayscale AA) | ClearType (RGB ▽ subpixel) | SDF (scalable, for transforms)
}

public interface IGlyphAtlas
{
    // Returns atlas placement for a (face, glyph, size, subpixel, mode) key; rasterizes on miss.
    bool TryGetOrAdd(in GlyphKey key, out GlyphSlot slot);   // slot: AtlasPage, x,y,w,h, bearing
    TextureHandle Page(int index);                            // GPU texture for binding
    void Compact();                                           // evict LRU when full (edge, not hot)
}
```

```csharp
public readonly struct GlyphKey            // 24B, hashable; the realization cache key
{ public FontFaceHandle Face; public ushort GlyphId; public ushort SizeQ; // quantized px*4
  public byte SubpixelBucket; public GlyphRenderMode Mode; }

public struct GlyphSlot { public int Page; public ushort X,Y,W,H; public float BearingX, BearingY; }
public struct PackedGlyph { public ushort AtlasX,AtlasY,W,H; public float AdvanceX, OffX, OffY; }
```

Data flow (text node → DrawGlyphRun):
```
TextElement.Content (string, edge)
  └─ reconcile → SceneStore node (VisualKind=Text, PayloadRef→TextRunSpec{StringId,FontFaceHandle,size})
       └─ layout MEASURE: ITextShaper.Shape → advances → line metrics → node Bounds
            └─ render-walk: for each shaped glyph, IGlyphAtlas.TryGetOrAdd → PackedGlyph[]
                 └─ append DrawGlyphRunCmd { GlyphRunRef → GlyphRunTable, Brush, baseline }
                      └─ batcher: instanced textured quads sampling atlas page
```

| Seam method        | Windows (DirectWrite/)                        | macOS later (CoreText/) |
|--------------------|-----------------------------------------------|-------------------------------|
| `IFontSystem`      | `IDWriteFactory`, `IDWriteFontCollection`     | `CTFontCollection` |
| `IFontFace`        | `IDWriteFontFace`                             | `CTFont` |
| `ITextShaper`      | `IDWriteTextAnalyzer` (script/bidi/shaping)   | `CTLine`/`CTRun` |
| `IGlyphRasterizer` | `IDWriteGlyphRunAnalysis` → alpha/ClearType   | `CGContext` glyph draw |
| `IGlyphAtlas`      | engine-owned (portable); uploads via RHI      | same (portable) |

DWrite + DComp COM bindings are NOT in ComputeSharp → authored fresh in the `FluentGpu.Windows`
DirectWrite/ + D3D12/ folders using the ComputeSharp `ComPtr<T>` + **hand-vtable `IComObject` pattern on the per-frame
hot path**; cold/warm COM (DWrite *setup*, UIA, TSF, OLE) uses source-generated
`[GeneratedComInterface]`/`[GeneratedComClass]` (canonical COM ruling: `dotnet10-csharp14-zero-alloc.md` §4).

---

## 6. FRAME LIFECYCLE & THREADING

> **⊳ Canonical threading model.** The single-thread decision below is **build-order step 1** (`hardened-v1-plan.md` §6), not the shipping topology. The canonical model is the **render-thread seam** — `hardened-v1-plan.md` §2 + [`subsystems/threading-render-seam.md`](./subsystems/threading-render-seam.md) §14 (see [`SPEC-INDEX.md`](./SPEC-INDEX.md)): record/batch/submit/present move to a dedicated render thread fed by an immutable `SceneFrame` snapshot, gated by a consume-based `QUARANTINE = RenderInFlightDepth`. The DrawList POD stream is the hand-off, exactly as designed here.

### Decision: **Single UI/render thread for v1** (reconcile + layout + record + submit all on the
UI thread); present completion + vsync signaling on the OS compositor thread (DComp, out of our
control). Optional future: move render-walk+record+submit to a dedicated render thread fed by an
immutable snapshot. Designed so the threading boundary is the DrawList (already immutable POD).

Justification: Reactor's proven model is UI-thread-affine with an async coalescing dispatcher;
SceneStore mutation + Yoga layout are not thread-safe by design (mutable SoA). The DrawList POD
stream is the clean hand-off point IF we later pipeline. Keeping v1 single-threaded removes
data-race surface, matches NativeAOT/zero-alloc goals, and still hits 60fps for UI workloads.

### 6.1 Ordered phases (one frame)

```
            UI THREAD (frame N)                                  OS COMPOSITOR THREAD
 ┌──────────────────────────────────────────────┐
 │ 0. PUMP        IPlatformAppLoop drains WM_*    │
 │                → InputEvent queue              │
 │ 1. INPUT       dispatch InputEvents:           │
 │    DISPATCH      hit-test SceneStore (Bounds+  │
 │                  Clip+Flags.Hittable), route   │
 │                  to handlers; gestures FSM     │
 │                  → user delegates set state    │
 │ 2. HOOK FLUSH  apply queued UseState setters;  │
 │                run UseReducer; mark components │
 │                dirty (coalesced, CAS-guarded)  │
 │ 3. RENDER      for each dirty Component:       │
 │                  Component.Render() → Element  │
 │                  (immutable record tree, edge) │
 │ 4. RECONCILE   diff old vs new Element tree;   │
 │                patch SceneStore via            │
 │                ISceneBackend (Mount/Update/    │
 │                Unmount); ChildReconciler LIS;  │
 │                sets NodeFlags dirty bits       │
 │ 5. LAYOUT      Yoga measure/arrange over dirty │
 │                subtrees (LayoutDirty scope);   │
 │                text measure via ITextShaper;   │
 │                writes Bounds[]                 │
 │ 6. ANIMATION   advance AnimTrack timelines by  │
 │                FrameTime.Delta; write animated │
 │                Opacity/Transform/Bounds;       │
 │                mark Paint/TransformDirty       │
 │ 7. RECORD      render-walk dirty layers →      │
 │                DrawList (arena); clean layers  │
 │                memcpy'd from back arena        │
 │ 8. BATCH       sort by SortKey, build instance │
 │                buffers, glyph atlas uploads    │
 │ 9. SUBMIT      ICommandEncoder: render passes, │
 │                draws → IGpuDevice.Submit ──────┼──► GPU executes
 │10. PRESENT     ISwapchain.Present ─────────────┼──► DXGI → DComp commit ──► vsync scanout
 │11. EFFECTS     run UseEffect bodies + pending  │
 │    FLUSH         cleanups (AFTER paint, like   │        (compositor signals next vsync,
 │                  React commit-then-effect)     │         re-arms IPlatformAppLoop.onFrame)
 │12. ARENA SWAP  swap front/back command arenas; │
 │                arena.Reset()                   │
 └──────────────────────────────────────────────┘
```

Key ordering contracts (binding):
- **Effects run AFTER present (phase 11), never mid-render** — matches Reactor/React commit
  semantics; effect bodies that call setState schedule frame N+1 (coalesced), never re-enter frame N.
- **Hook state flush (2) precedes Render (3)**; setters during Render/Reconcile/Layout are queued
  for N+1 (reentrancy guard `_isRendering`, Reactor pattern).
- **Layout (5) is dirty-scoped**: only subtrees with `LayoutDirty`/`SubtreeDirty` re-measure;
  Yoga's generation-counter cache (confirmed in `LayoutResults`) validates the rest.
- **Animation (6) is between layout and record** so animated transforms/opacity land in this frame's
  DrawList without a reconcile (composition-style independent animation — WinUI lesson).
- Render priority policy (Reactor `RenderPriorityPolicy`): 16ms budget; if a frame overruns,
  next frame enqueued at lower priority to yield to input pump. No frame skipping; state changes
  coalesce.

### 6.2 Threading/affinity model
- `ThreadAffinity` (Reactor pattern): all SceneStore/Component/Hook mutation asserts UI-thread.
- `IPlatformAppLoop.Post(Action)` marshals off-thread state changes (async effect completions,
  background data) onto the UI thread before phase 2.
- The ONLY cross-thread surface is GPU submit→present (RHI owns its fence; engine never blocks on
  GPU except `WaitIdle` at resize/teardown).

### 6.3 InputEvent (portable, §4 PAL emits it)
```csharp
public readonly struct InputEvent
{ public InputKind Kind;            // PointerDown/Move/Up/Wheel, KeyDown/Up, Char, Focus, ...
  public Point2 Position;           // client px
  public PointerId Pointer; public ModifierKeys Mods; public int Delta; public uint KeyOrChar;
  public long TimestampUs; }
```

### 6.4 Hook dependency contract (zero-alloc, AOT)
No `params object[]`. Dependencies are a `ReadOnlySpan<DepKey>` where `DepKey` is a 16-byte tagged
value (kind + inline scalar bits, or interned `StringId`, or a `Handle`). Source generator emits
`UseEffect(Action, ReadOnlySpan<DepKey>)` overloads and a `DepKey.From(...)` set covering common
scalar types. RenderContext compares dep spans field-wise (no boxing). This supersedes Reactor's
`params object[]` (the one explicit divergence the explore reports flagged as AOT-hostile).

---

## 7. PROJECT / MODULE LAYOUT (4 libs + 4 satellites, acyclic, inward-only deps)

The repo is **4 libraries + 4 satellites = 8 projects** (`src/FluentGpu.slnx`). The portable engine that
once spanned ~14 projects is now FOLDERS inside the single **`FluentGpu.Engine`** library (`RootNamespace=
FluentGpu`; namespaces unchanged — `FluentGpu.Scene`, `FluentGpu.Rhi`, `FluentGpu.Dsl`, …). The boxes below
are those folders; the inward-only dependency direction is now a **review-enforced folder/namespace
discipline** inside `Engine`, except the one load-bearing edge that stays a real `.csproj` boundary:
**`Engine` never references `FluentGpu.Windows`** (the OS backend), so the backend stays swappable.

```
Dependency direction: arrows point to dependencies. NO cycles. The OS backend depends INWARD only.

  FluentGpu.Engine  (ONE library; the boxes are FOLDERS, namespaces verbatim)
  ─────────────────────────────────────────────────────────────────────────────────────
                         ┌────────────────────────┐
                         │  Foundation/            │  Handle, allocators, StringInterner,
                         │  (no deps)              │  math (Affine2D, LayoutRect, ColorF), spans
                         └────────────┬───────────┘
        ┌───────────────┬────────────┼─────────────┬───────────────┬───────────────┐
        ▼               ▼            ▼             ▼               ▼               ▼
  ┌──────────┐   ┌──────────┐  ┌──────────┐  ┌──────────┐   ┌──────────┐   ┌──────────┐
  │ Seams/Rhi│   │ Seams/Pal│  │Seams/Text│  │ Scene/   │   │ Layout/  │   │ Input/   │
  │ (iface)  │   │ (iface)  │  │ (seam)   │  │ SceneStore│  │ Yoga port│   │ events,  │
  └────┬─────┘   └────┬─────┘  └────┬─────┘  └────┬─────┘   └────┬─────┘   │ hit-test │
       │              │             │            │              │         └────┬─────┘
       │              │             │            └──────┬───────┘              │
       │              │             │                   ▼                      │
       │              │             │            ┌──────────────┐              │
       │              │             └───────────►│  Render/     │◄─────────────┘
       │              │                          │  DrawList,   │  sees: Scene, Text(seam),
       │              │                          │  batcher     │  Rhi(iface), Layout, Foundation
       │              │                          └──────┬───────┘
       │              │                                 ▼
       │              │                          ┌──────────────┐
       │              │                          │ Reconciler/  │  sees: Scene, Render,
       │              │                          │ ISceneBackend│  Layout, Foundation, Dsl
       │              │                          └──────┬───────┘  (declares VirtualListEl)
       │              │            ┌────────────┐ ┌─────┴────────┐  ┌──────────────┐
       │              │            │ Hooks/     │ │ Dsl/         │  │ Animation/   │
       │              │            │ RenderCtx  │ │ Element recs,│  │ Curve, track │
       │              │            └─────┬──────┘ │ factories,   │  └──────┬───────┘
       │              │                  │        │ modifiers    │         │
       │              │                  └────────┴──────┬───────┴─────────┘
       │              │                                  ▼
       │              │                          ┌──────────────┐
       │              │                          │ Hosting/     │  app/window/run-loop wiring,
       │              │                          │ (composition │  frame lifecycle orchestrator;
       │              │                          │  root)       │  sees ALL above + the seam ifaces
       │              │                          └──────┬───────┘  (+ Media/ and Headless/{Rhi,Pal,Text}/
       │              │                                 │           test backends also live in Engine)
  ─────┼──────────────┼─────────────────────────────────┼───────────────────────────────────────────
       │              │                                 │   bound at the composition root (WindowsApp)
       ▼              ▼                                 ▼
  ┌──────────────────────────────────────┐   ┌────────────────────┐   ┌────────────────────┐
  │ FluentGpu.Windows (the OS BACKEND;    │   │ FluentGpu.Controls │   │ FluentGpu.WindowsApi│
  │  refs Engine + 1 TerraFX package)     │   │ SDK control kit;   │   │  OS-services scaffold│
  │  D3D12/ (ComPtr,DXGI,D3D12,DComp)     │   │ refs Engine ONLY;  │   │  (Notifications/    │
  │  Pal/ (HWND,WndProc,TSF,clip)         │   │ TerraFX-free       │   │   Credentials/      │
  │  DirectWrite/ · Uia/ · Wic/ · Interop/│   │ Button/Slider/Nav  │   │   Packaging/        │
  └──────────────────────────────────────┘   └────────────────────┘   │   Activation/)      │
   the OS backend implements the Seams/Pal/Rhi/Text interfaces;        └────────────────────┘
   Engine NEVER references it (compiler-enforced) — swappable wholesale.

  Satellites: FluentGpu.SourceGen + FluentGpu.Interop.SourceGen (Roslyn analyzers; build-time only;
  netstandard2.0 — cannot merge into the net10 libs), FluentGpu.VerticalSlice (AOT harness exe),
  FluentGpu.WindowsApp (gallery exe / composition root — refs Engine+Controls+Windows).
```

Project list (trimmable, AOT):
- **`FluentGpu.Engine`** — the portable engine core (`RootNamespace=FluentGpu`, no NuGet). One folder per
  former project, namespaces verbatim: `Foundation/` (primitives, allocators, math, interner — the root),
  `Seams/Rhi/` `Seams/Pal/` `Seams/Text/` (interface-only seams + POD descs/`InputEvent`/glyph-atlas POD),
  `Scene/` (SceneStore SoA + Brush/Clip/GlyphRun tables + `VirtualLayout`), `Layout/` (Yoga port + cache +
  text-measure bridge), `Render/` (DrawList encoding, render-walk, batcher), `Dsl/` (Element records,
  factories, modifiers — Reactor model verbatim), `Hooks/` (RenderContext, hook states, Context),
  `Reconciler/` (`ISceneBackend`, reconciler, ChildReconciler — declares `VirtualListEl`), `Animation/`
  (Curve/Easing/AnimTrack), `Input/` (gesture FSM, focus, hit-test), `Media/`, `Hosting/` (composition
  root / frame loop), and `Headless/{Rhi,Pal,Text}/` (the test backends; referenced by test hosts only).
- **`FluentGpu.Controls`** — the SDK controls layer (Button/IconButton/ToggleButton/Slider/ScrollBar/
  NavigationView + Navigator/PageHost/Route/Nav + Repeater + the `Virtual` factory + `Icons`). Refs
  **Engine only**; stays **TerraFX-free**. **Acyclic**: `VirtualListEl` is declared in `Engine` (Reconciler/),
  so `Controls → Reconciler` is one-way with no back-edge. Content owned by `subsystems/controls.md`.
  (Aspirational lookless `ControlTemplate`/`ControlTheme`/`VisualState` kit is the stated future target;
  as-shipped Phase 0 is the composition-factory hoist + per-control `Style` records — see that doc.)
- **`FluentGpu.Windows`** — the swappable Windows OS backend. Refs Engine + the **one**
  `TerraFX.Interop.Windows` package. Folders: `Interop/` (vendored ComputeSharp bindings + DComp/DWrite/UIA/
  WIC), `Pal/` (Win32 windowing/input/IME), `D3D12/` (GPU backend, ComputeSharp-seeded COM), `DirectWrite/`
  (DWrite shape+raster), `Wic/` (image codecs), `Uia/` (UIA bridge). Implements the `Seams/*` interfaces.
- **`FluentGpu.WindowsApi`** — empty scaffold for OS services (`Notifications/`/`Credentials/`/`Packaging/`/
  `Activation/`); refs Engine. (MSIX packaging is **app-side** — `.wapproj`/packaging props — not here.)
- Satellites: **`FluentGpu.SourceGen`** (portable Roslyn analyzer: ElementTypeId table, DepKey overloads,
  HLSL→DXIL embed, property-dispatch; referenced by Engine) + **`FluentGpu.Interop.SourceGen`** (the
  COM-binding generator; referenced by `FluentGpu.Windows` only) — both netstandard2.0, build-time only.
  **`FluentGpu.VerticalSlice`** (AOT validation harness exe; refs Engine+Controls; closure stays
  TerraFX-free). **`FluentGpu.WindowsApp`** (WinExe gallery / composition root; refs Engine+Controls+Windows).

**Cycle-prevention invariants (binding):**
1. **Compiler-enforced:** `FluentGpu.Engine` references **no** OS backend — `FluentGpu.Windows` is bound only
   at the composition root, so the backend swaps wholesale (macOS = a future `FluentGpu.Windows.Mac`).
   `FluentGpu.Controls` references `Engine` only and stays TerraFX-free.
2. **Review-enforced (intra-`Engine` folder discipline):** `Render/` sees the `Seams/Rhi`/`Seams/Text`
   interfaces, never the OS impls (which live in a library it cannot reference). The OS-backend folders
   implement the `Seams/Pal`/`/Rhi`/`/Text` interfaces and are bound only by `Hosting/`.
3. `Dsl/`/`Hooks/` know nothing of `Scene`/`Render`/`Rhi` (programming-model layer is GPU-agnostic;
   `Reconciler/` is the only bridge from Element-world to SceneStore-world via `ISceneBackend`).
4. `Foundation/` depends on nothing. Everything depends on `Foundation/`.

### 7.1 ISceneBackend — the reconciler↔scene bridge (replaces Reactor's IRenderBackend)
```csharp
namespace FluentGpu.Reconciler;
public interface ISceneBackend
{
    NodeHandle Mount(Element e, in MountContext ctx);                 // ctx = readonly ref struct
    NodeHandle Update(Element old, Element @new, NodeHandle n, in UpdateContext ctx); // Null = patched in place
    void       Unmount(NodeHandle n);
    void       SetKey(NodeHandle n, StringId key);
    IChildSlots Children(NodeHandle n);   // Insert/Move/Replace/Remove by index → SceneStore topology
}
```
`MountContext`/`UpdateContext` are `readonly ref struct` (Reactor zero-alloc pattern). The Windows
backend is the only `ISceneBackend` impl in v1; it writes SceneStore columns directly. The
ChildReconciler LIS algorithm (Reactor, host-agnostic) and RenderContext hooks are reused verbatim.

---

## CONTRACT SUMMARY (the load-bearing decisions specialists must honor)
1. `Handle` = 8B `{u32 index, u32 gen}` (kind on the typed wrapper's DEBUG assert, not in the bits — see §1.1); typed wrappers per kind. Handles on hot paths; GC refs only at edges.
2. Four allocators: `SlabAllocator<T>` (versioned stable), `ArenaAllocator` (per-frame bump),
   `ObjectPool<T>` (edge GC recycle), `HandleTable<T>` (COM/GPU). Intrusive free list, gen-bump on alloc+free.
3. SceneStore is **SoA**; one generation/free-list spine; layout-input/result + paint columns co-located, indexed by NodeHandle.
4. DrawList = flat POD `DrawCmd`+payload stream in double-buffered arenas; brushes/clips/glyphs by handle/index;
   incremental via dirty-subtree re-record + memcpy of clean spans; zero per-frame managed alloc.
5. PAL + RHI are **interface-only** (Engine `Seams/`); D3D12 + Win32 + DComp are reference impls in the swappable `FluentGpu.Windows` backend (D3D12/, Pal/, Interop/); DrawList POD is the swap line.
6. Text seam: IFontSystem/IFontFace/ITextShaper(→spans)/IGlyphRasterizer/IGlyphAtlas; DWrite now, CoreText later; no `string` on paint path.
7. Frame phases fixed (input→hookflush→render→reconcile→layout→anim→record→batch→submit→present→effects→arenaswap); single UI thread v1; effects after present.
8. **4 libraries + 4 satellites = 8 projects** (`src/FluentGpu.slnx`): the portable engine is folders in `FluentGpu.Engine`, plus `FluentGpu.Controls`, the swappable `FluentGpu.Windows` backend, `FluentGpu.WindowsApi`, and the SourceGen/Interop.SourceGen analyzers + VerticalSlice/WindowsApp exes. Acyclic; `Engine` never references `Windows` (compiler-enforced); `Foundation/` is the root with no deps. (Earlier passes counted "27 projects"/"18 assemblies" before the consolidation <!-- canon-allow: superseded assembly count, narrating the old layout -->; restated with `architecture-spec.md` §3 and `design/README.md`. `FluentGpu.Controls` sits above `Reconciler/` — see §7.)
9. Hook deps = `ReadOnlySpan<DepKey>` (not `params object[]`); source-gen'd. Reactor Element/Hooks/ChildReconciler reused verbatim; reconciler bridges via `ISceneBackend`.
10. ComputeSharp: vendor `ComPtr<T>` + DXGI/D3D12 bindings + pools + source-gen + AOT config into `FluentGpu.Windows` (D3D12/ + Interop/); author DWrite + DComp + graphics-PSO/swapchain ourselves; do NOT take D2D1 for core; D3D12MA optional.
```
