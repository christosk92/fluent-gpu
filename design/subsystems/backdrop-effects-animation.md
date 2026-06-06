# fluent-gpu — Subsystem Design: Backdrop, Effects, and Connected/Implicit Animation

**Author scope:** ONE subsystem — the three meanings of "backdrop" (window Mica/Acrylic, editorial baked
backdrop, in-app live Acrylic), the `IEffectRunner` offscreen-effect seam, and connected/implicit/driven
animation built on the phase-7 `AnimTrack`. Synced-lyrics *animation timing* lives here; lyrics *layout/
shaping* belongs to `FluentGpu.Media.LyricsLayoutEngine` (text seam) and is referenced, not redesigned.
**Video is out of scope** — the present-tree (`IVideoPresenter`, `DrawVideoCmd`, hole-punch, the multi-
visual DComp amendment to §5.1) is owned by `subsystems/media-pipeline.md`. This doc references the video
contract only where backdrop/animation must coexist with it (z-order, the same-Commit hole handshake).

**Authoritative inputs honored verbatim (referenced, never re-litigated):**
- THREADING: `hardened-v1-plan.md` §2 (13-phase→thread map, the PUBLISH seam, consume-gated quarantine,
  render-private ≥3 DrawList arenas, the two-fence split-resource retire). The render thread owns every
  `ComPtr`; the UI thread touches zero COM.
- SCENE/DRAWLIST: `architecture-spec.md` §4.4/§4.5, §5.4 (SoA columns, the 3 dirty axes + arena dirty-
  worklist, `WorldTransform[]` computed top-down in phase 7, the clean-span rule, `Detached` flag +
  deferred-free for exit animations).
- HOOKS/RECONCILE: `subsystems/reconciler-hooks.md` (the hook cell table, `DepKey`/`DepDeps`, effect
  timing, synchronous-on-unmount `RunCleanups`), `architecture-spec.md` §5.6 (structural enter/move/exit
  animations re-routed through this subsystem's handle-based API).
- RENDERER: `subsystems/gpu-renderer.md` §7 (PushLayer/PopLayer + LayerPool offscreen RTs), §0/§5.2
  (`IEffectRunner` = D2D1/FXC behind a seam, the optional `Effects.D2D1` leaf, premul-alpha trap),
  `architecture-spec.md` §5.2 (`OQ-7` resolved to the engine-owned **persistent canvas RT**).
- ZERO-ALLOC/AOT: `dotnet10-csharp14-zero-alloc.md` (`[InlineArray]`, `allows ref struct` sink walk,
  C#14 compound assignment, `[LibraryImport]` for flat C, `[GeneratedComInterface]` for cold/warm COM,
  hand-vtable only on the generated hot path).
- APP FOLD-IN: `app-requirements-waveemusic.md` §3.4 (the requirement source; every "ADD" below is from it).

This document is decisive. Where a choice is made it is stated as MADE with the losing option and the
reason. Open questions are flagged `OQ-n`. New cross-subsystem contract additions are flagged `FA-n`.

---

## 0. The one-paragraph thesis

> Keep "backdrop" as **three physically different mechanisms that never share an implementation**: (1)
> *window* Mica/Acrylic is the OS compositor's pixels behind a transparent root — a pure PAL seam
> (`IBackdropSource`), **zero renderer change**; (2) the *editorial* backdrop is a **persistent baked
> texture** the app addresses as an ordinary `ImageHandle`, owned by a private cap-2 LRU and a separately-
> keyed blurred-source cache, **not** the shared per-frame `LayerPool`; (3) *in-app* Acrylic is a live
> two-pass FrameGraph step that snapshots the persistent **canvas RT** behind the overlay, blurs it, and
> composites — gated on the `OQ-7` canvas-RT decision (resolved in favor of the canvas RT). All offscreen
> pixel work routes through one `IEffectRunner` seam (D2D1 on Windows, MPS later), so Metal is a leaf swap.
> **Connected and implicit animation** are entirely phase-7 `AnimTrack` writes into `LocalTransform`,
> `Opacity`, and a new cold `EffectAux` column — **always Transform/PaintDirty, never LayoutDirty** — with
> a dedicated `DetachedAnim` slab that survives the *synchronous-on-unmount* hook-cleanup contract and pins
> the animated `ImageHandle` for the animation's life. The `AnimTrack.DrivenClock` decouples the timeline
> source from the frame clock for scroll-fade and playback-synced lyrics.

---

## 1. Where this subsystem lives (assemblies) and the data-flow

```
                         user hooks (UseImageBackdrop / UseImplicitTransition / UseConnectedAnimation /
                                     UseDrivenAnimation / UseAcrylic / UseReducedMotion)
                                            │ thin UseRef/UseMemo/UseEffect compositions
        ┌───────────── FluentGpu.Media (portable) ──────────────┐   ┌──── FluentGpu.Animation (portable) ────┐
        │ BackdropBaker, BackdropRtCache(cap-2), BlurredSrcCache │   │ AnimTrack, AnimEngine, DetachedAnimSlab │
        │  BakeTicket, AcrylicPass (FrameGraph step authoring)    │   │  Curve/Easing, DrivenClock, ReducedMotion│
        └───────────────────────┬────────────────────────────────┘   └──────────────────┬──────────────────────┘
                                │ writes EffectAux/LocalTransform/Opacity columns + ImageRefTable bakes
                                ▼  (UI thread phase 4/6.5/7 authoring → PUBLISH → render thread phase 8/13 execution)
        ┌──────────────── FluentGpu.Scene (SoA) ────────────────┐   columns: NodePaint, NEW EffectAux (cold slab),
        │ SceneStore + EffectAuxTable + BackdropRef + ImageRefTbl │   BackdropRef; opcodes via DrawList arenas
        └───────────────────────┬────────────────────────────────┘
                                ▼  (render thread, phase 8 record / 13 bake-drain — owns every ComPtr)
        ┌──────────────── FluentGpu.Render ─────────────────────┐   PushLayerCmd{Effect}, DrawImageCmd(baked RT),
        │ DrawListRecorder, Batcher, FrameGraph, LayerPool        │   DrawShadowCmd; AcrylicPass two-pass schedule
        └───────────────────────┬────────────────────────────────┘
              IEffectRunner seam │ (POD: EffectChain handle + src/dst TextureHandle + params; ZERO COM)
        ┌───────────────────────▼────────────────────────────────┐   ┌──── FluentGpu.Pal (iface) ────┐
        │ FluentGpu.Effects.D2D1 (OPTIONAL leaf, Hosting-only)    │   │ IBackdropSource, ISystemColors │
        │  D2D1 effect graph (GaussianBlur/Shadow), FXC noise PS,  │   └──────────────┬─────────────────┘
        │  ComputeSharp.D2D1 transpiler reuse (the noise kernel)   │     impl: FluentGpu.Pal.Windows
        └──────────────────────────────────────────────────────────┘     (DWM/DComp backdrop sibling visual)
```

**Assembly placement (honors the §7 acyclic DAG + `app-requirements` §5 new-assembly fold-in):**

| Type / seam | Assembly | Why |
|---|---|---|
| `AnimTrack`, `AnimEngine`, `DetachedAnimSlab`, `Curve`/`Easing`, `DrivenClock`, `ReducedMotionState` | **`FluentGpu.Animation`** (exists; dep Scene+Foundation) | Phase-7 timelines already specced to live here (§7 DAG). |
| `EffectAux` column, `BackdropRef`, `EffectChainTable` | **`FluentGpu.Scene`** (cold SoA slab + side table) | Co-located with `SceneStore`; read by record (phase 8). |
| `BackdropBaker`, `BackdropRtCache`, `BlurredSrcCache`, `BakeTicket`, `AcrylicPass` author-side | **`FluentGpu.Media`** (NEW, portable; dep Foundation + Rhi/Text iface) | Editorial backdrop shares the image-pipeline residency machinery from `media-pipeline.md`. |
| `UseImageBackdrop`/`UseAcrylic`/`UseImplicitTransition`/`UseConnectedAnimation`/`UseDrivenAnimation`/`UseSyncedLyrics`(timing)/`UseReducedMotion` | thin compositions in **`FluentGpu.Media`** + **`FluentGpu.Hooks`** convenience | `UseRef`/`UseMemo`/`UseEffect` over the above; `DepKey`-span deps. |
| `PushLayerCmd{Effect}` recording, `FrameGraph` two-pass scheduling, `LayerPool` | **`FluentGpu.Render`** | Offscreen RT machinery already owns layers (§7). |
| `IEffectRunner` (seam) | **`FluentGpu.Render`** interface; impl in **`FluentGpu.Effects.D2D1`** (OPTIONAL leaf) | Windows-only D2D1/FXC behind the seam; Metal supplies MPS. |
| `IBackdropSource`, `ISystemColors` (referenced) | **`FluentGpu.Pal`** iface; **`FluentGpu.Pal.Windows`** impl | DWM/DComp; OS leaf referenced only by Hosting. |

All acyclic; `FluentGpu.Media`/`FluentGpu.Effects.D2D1`/`FluentGpu.Pal.Windows` are referenced **only by
`Hosting`**. `Animation` already sits below `Hosting` in the §7 graph.

**Portability boundary in one sentence:** everything above the `IEffectRunner`/`IBackdropSource` seams is
portable C# (math, POD, column writes, FrameGraph policy); the only Windows code is `Effects.D2D1`
(D2D1/FXC) and `Pal.Windows` (DWM/DComp). macOS reimplements those two leaves (MPS + `NSVisualEffectView`)
and recompiles the rest unchanged.

---

## 2. The three backdrops, kept hard-separated

### 2.1 Window Mica / Acrylic — PAL only, zero renderer pixels (MADE)

The window background blur/tint is **the OS compositor's pixels behind our transparent root**, not anything
we paint. We expose one PAL seam and clear our root transparent so DWM composes Mica through.

```csharp
namespace FluentGpu.Pal;

public enum HostBackdropKind : byte { None, MicaBase, MicaAlt, AcrylicHost, Blurred }

public interface IBackdropSource          // owned by Pal.Windows; referenced via Hosting
{
    // Idempotent; safe to call on theme/HC/accent change. tint is sRGB straight-alpha.
    void SetWindowBackdrop(NativeHandle window, HostBackdropKind kind, ColorF tint);
    bool TryGetEffective(NativeHandle window, out HostBackdropKind kind);   // HC may force None
    uint Epoch { get; }   // bumps on a host-backdrop capability change (rare); projects into DepKey
}
```

- **Windows impl (`Pal.Windows`):** `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_*)` for
  Mica/Mica-Alt/host-Acrylic on Win11; on older builds (no `DWMWA_SYSTEMBACKDROP_TYPE`) fall back to a
  **DComp backdrop sibling visual placed BELOW our swapchain visual** carrying a `CreateBackdropBrush`/
  blur effect group. Either way the **swapchain back buffer is cleared transparent (premul 0)** in the
  window-chrome region so the composited Mica shows through (the existing `BGRA8_UNORM` + `PREMULTIPLIED`
  DComp present from §5.1 already supports this; the only change is the *clear color is transparent*, set
  once at window setup — not per frame).
- **HC:** `ISystemColors` reports HighContrast → `IBackdropSource.SetWindowBackdrop(..., None)` + the root
  paints an **opaque** HC `sys.Window` fill (no transparency under HC, per the theming contract).
- **macOS:** `NSVisualEffectView` (`.material = .hudWindow`/`.sidebar`) behind a layer-backed transparent
  `CAMetalLayer`. Same seam, leaf swap.

**13-phase placement:** none. `SetWindowBackdrop` is called from a `UseEffect` (passive, phase 12) keyed on
`(HostBackdropKind, ISystemColors.Epoch, HighContrast)` — human-timescale, off the paint path. **Zero
DrawList opcode, zero renderer cost, zero per-frame work.** This is the cheapest of the three and the only
one with no GPU footprint of ours.

```csharp
// FluentGpu.Hooks convenience (Media-thin)
void UseWindowBackdrop(HostBackdropKind kind, ColorF tint);   // → UseEffect(SetWindowBackdrop, deps:(kind,tint,epoch,hc))
```

### 2.2 Editorial baked backdrop — a persistent baked RT, NOT the LayerPool (MADE)

The now-playing 6-layer editorial backdrop (blur + radial + linear + noise + vignette) recolors on every
track and must not stutter the 10k-row grid behind it. The losing option — baking through the shared per-
frame `LayerPool` — would let an expensive 60-DIP blur **starve** the group-opacity/clip RTs every other
node needs. MADE: **a private, persistent baked texture addressed as an ordinary `ImageHandle`**, so the
record path is a single `DrawImageCmd` over the baked RT and reuses the §4.5 clean-span machinery with **no
batcher special-casing**.

```csharp
namespace FluentGpu.Media;

// 32B POD recipe — the procedural backdrop description, hand-authored static-friendly.
public readonly struct BackdropRecipe
{
    public readonly float BlurDip;       // source Gaussian sigma in DIPs (~60 for now-playing)
    public readonly float Exposure;      // -1..+1 multiply on the blurred source
    public readonly Palette Radial;      // radial wash stops (from FluentGpu.Theme)
    public readonly Palette Linear;      // linear wash stops
    public readonly bool   Noise;        // film-grain overlay (optional Effects.D2D1 leaf)
    public readonly bool   Vignette;
    public BackdropDirty FieldsHash();   // for the bake key + debounce equality
}

// What UseImageBackdrop returns; the node binds it like any image.
public readonly record struct BackdropBinding(ImageHandle Baked, BackdropState State, BakeTicket Ticket);
public enum BackdropState : byte { FlatFill, BakingSource, Compositing, Ready, Failed }
```

**Two caches, two keys (folded — the load-bearing split):**

1. **`BlurredSrcCache`** — keyed `(uri StringId, sizeBucket)`. Holds the **expensive** product: the art
   downscaled and Gaussian-blurred. A track *recolor that keeps the same art* (e.g. theme flip, accent
   change) hits this cache and **never re-blurs**.
2. **`BackdropRtCache`** — cap-2 LRU, keyed `(uri, sizeBucket, recipeHash, themeKind)`. Holds the **final**
   composited editorial RT (blurred source + 5 cheap procedural quads). A genuine **art change** misses both
   and pays the full blur; a recolor misses only this cache and recomposites 5 cheap quads over the cached
   blurred source.

```csharp
public sealed class BackdropBaker          // FluentGpu.Media; bake JOBS are render-thread (own ComPtr)
{
    BackdropRtCache  _rt   = new(capacity: 2);            // LRU of final RTs (own pool, NOT LayerPool)
    BlurredSrcCache  _blur = new(capacity: 4);            // LRU of blurred sources keyed (uri,size)
    // each bake bucket = downscale ~0.5x then upsample at draw (blur is low-frequency) → memory ~0.25x
    public BakeTicket RequestBake(in BackdropKey key, ImageHandle artSrc, in BackdropRecipe r, BakePriority p);
}
```

- **Downscaled bake (MADE):** bake the source at **~0.5×** then upsample at draw. Blur is low-frequency; the
  visual delta is invisible and the RT memory drops ~4×. A 1024² now-playing backdrop bakes into a 512² RT.
- **`BakeTicket` — cancel / debounce / priority:**

```csharp
public readonly struct BakeTicket
{
    readonly Handle _h;                 // into a SlabAllocator<BakeJob> (gen-versioned → ABA-safe)
    public void Cancel();               // drop the in-flight bake (track changed before it landed)
    public void SetPriority(BakePriority p);
}
// Debounce: rapid track-changes coalesce — N skips → 1 bake. The job stamps a uint TargetGen
// (now-playing track sequence, shared with media-pipeline's palette TargetGen); a stale-gen result
// is dropped at publish (defeats the fix-artist-nav-flicker class of bug).
```

**The bake pipeline (where the work runs):**

```
phase 4 (UI): UseImageBackdrop(art, palette, sizePx, recipe)
   → probe BackdropRtCache(key). HIT → return Ready binding (one DrawImageCmd next frame). DONE.
   → MISS → return {FlatFill, dominant-color} binding NOW (alloc only on cache-miss slot create, mount-time)
            + RequestBake(...) → BakeTicket. Debounce: if a bake for this node is in-flight, Cancel old, re-arm.
phase 13 (RENDER thread, time-sliced — it owns every ComPtr):
   BackdropBaker.Drain():
     1. blurred-source MISS? IEffectRunner.Blur(art → blurredSrc RT)  [the expensive 60-DIP pass, rate-limited]
        else reuse cached blurred source
     2. composite: 5 procedural quads (radial, linear, exposure-multiply, noise, vignette) over blurred src
        into a fresh BackdropRtCache RT (via IEffectRunner gradient/SDF passes + optional noise PS)
     3. ImageRefTable.Get(bakedHandle).ContentEpoch++  → one-node re-record + cross-fade next frame
     4. evict LRU if cap-2 exceeded; free RT through the deferred-delete fence ring (§4.7, render-thread-only)
```

**The bake is render-thread-confined** — it allocates/uses RT textures and runs `IEffectRunner` (D2D1
ComPtr), both of which only the render thread may touch (`hardened-v1-plan.md` §2.1). The UI thread only
*requests* (a POD `BackdropKey` into the bake-job slab) and *consumes* the `ImageHandle`. **Cross-fade** on
a newly-`Ready` backdrop is the standard `ImageRealization.ContentEpoch`-bump → `CrossFade 0→1` in phase 7
(media-pipeline owns the image cross-fade; this subsystem just bakes into the same `ImageRefTable` slot).

- **Recolor cross-fade (MADE — opacity-only, no atlas thrash):** a track *recolor that keeps the art* derives
  the start+end backdrop RTs once and animates `Opacity` between two stacked `DrawImageCmd`s (exactly like
  the image cross-fade), so the 220 ms recolor **re-bakes nothing** mid-tick — it interpolates two already-
  baked RTs. This mirrors the theming-subsystem's "recolor = opacity crossfade" contract.
- **Failure / OOM:** RT alloc failure → `BackdropState.Failed`, fall back to a **flat dominant-color fill**
  (a `FillRoundRectCmd` with `PlaceholderRGBA`), **not** the spec's old "visually-wrong per-instance alpha."
  Logged once; the node stays renderable.

**`BackdropRef` Scene column / `DrawImageCmd`:** the node is `VisualKind.Image` carrying the baked
`ImageHandle`; **no new opcode** beyond the amended `DrawImageCmd` already owned by media-pipeline.
`BackdropRef` is a tiny cold side-table row `{BakeTicket ticket; ImageHandle baked; BackdropState state}`
attached only to the handful of backdrop nodes (not a per-node column).

### 2.3 In-app live Acrylic — an explicit two-pass FrameGraph step (MADE, BLOCKED on the canvas RT)

Toast / add-to-playlist / overlay Acrylic samples **live content behind the overlay**. The losing option —
"sample the live canvas RT under the overlay in the same pass" — has a read-after-write hazard (you cannot
SRV-sample the RT you are simultaneously rendering into). MADE: an explicit two-pass step.

```
PushLayer{Effect=AcrylicBlur, Bounds=behindRegion}
   ── pass A: SNAPSHOT the behind-region of the persistent CANVAS RT into a small layer RT
              (the existing child-into-layer-RT machinery from gpu-renderer §7)
   ── BARRIER: layer RT  RTV → SRV
   ── pass B: IEffectRunner.Blur(small sigma, cheap)  +  tint quad  → composite the overlay above
PopLayer
```

```csharp
// FluentGpu.Render — authored as a FrameGraph step; recorded behind a PushLayerCmd.Effect chain.
public readonly struct AcrylicParams        // packed into EffectChain payload (POD)
{ public readonly float SigmaPx; public readonly ColorF Tint; public readonly float TintOpacity;
  public readonly float LuminosityOpacity; public readonly float NoiseOpacity; }
```

- **Dependency on `OQ-7` (resolved):** this requires the **engine-owned persistent canvas RT** (`gpu-
  renderer.md` §13/`architecture-spec.md` §5.2 ruled in favor of it). The canvas RT is the partial-present
  surface **and** the live-Acrylic snapshot source. Until the canvas RT lands, `UseAcrylic` is **BLOCKED**;
  it degrades to a flat tint fill (`VisualKind.Backdrop` / `DrawBackdropCmd` stub carries the flat case).
- **Cost / v2 note:** this is the heaviest of the three at scroll velocity (per-frame behind-region blur).
  Per `hardened-v1-plan.md` §6, live Acrylic at scroll velocity wants the render-thread seam before it is
  stress-safe under simultaneous scroll+video. v1 ships it at **rest only** (overlay is modal/static), with
  the snapshot taken once on the overlay's `PushLayer` and re-taken only when the behind-region is in the
  damage set.
- `VisualKind.Backdrop` + `DrawBackdropCmd` (already stubs in `architecture-spec.md` §4.5) carry the live
  case; this subsystem fills in the two-pass schedule, not a new opcode.

---

## 3. The `IEffectRunner` seam — one offscreen-effect surface

All three backdrops' GPU pixel work (Gaussian blur, drop-shadow-on-a-group, noise, color-matrix) routes
through one seam so the heavy non-portable code stays in an **optional leaf** that a no-effects app trims.

```csharp
namespace FluentGpu.Render;     // INTERFACE here; impl is the Effects.D2D1 leaf

public enum EffectKind : byte { GaussianBlur, DropShadow, ColorMatrix, Noise, Transform3D, Composite }

public readonly struct EffectChain { internal readonly Handle H; }   // EffectChainTable row, POD params

public interface IEffectRunner            // render-thread-confined (owns ComPtr); POD across the seam
{
    // src/dst are engine ITexture handles (layer RTs / bucket RTs). No COM crosses this seam.
    void Run(in EffectChainDesc chain, TextureHandle src, TextureHandle dst, in EffectRect region);
    bool Supports(EffectKind kind);       // Metal MPS may not support Transform3D → caller falls back
}

public readonly struct EffectChainDesc    // POD; built from EffectChainTable rows
{ public readonly EffectKind Kind; public readonly float SigmaPx, Exposure;
  public readonly ColorMatrix Matrix; public readonly bool PremulInput; /* the alpha-trap flag */ }
```

- **Windows impl = `FluentGpu.Effects.D2D1`** (OPTIONAL leaf, Hosting-only): D2D1 `GaussianBlur`/`Shadow`
  effect graph for large-radius blur; the **noise** kernel is a `ComputeSharp.D2D1` FXC pixel shader (the
  one place the C#→HLSL transpiler is reused, per `gpu-renderer.md` §0). Carries the **documented straight-
  vs-premultiplied-alpha trap**: D2D1 effects default to straight alpha; our pipeline is premultiplied —
  `EffectChainDesc.PremulInput` selects the un-premultiply/re-premultiply wrap so the bake doesn't darken
  edges. Validated against a golden in `FluentGpu.Validation`.
- **Analytic shadows do NOT use this seam.** Rect/rrect drop shadows are the closed-form erf `DrawShadowCmd`
  (one instanced quad, no offscreen — `gpu-renderer.md` §4). `IEffectRunner.DropShadow` is the **fallback
  only** for per-corner-radius / arbitrary-path shadows (`architecture-spec.md` §5.2). So the common Fluent
  shadow case never touches an offscreen RT.
- **macOS:** `Effects.Metal` supplies `MPSImageGaussianBlur` + own kernels; `Supports(Transform3D)` may
  return false → the lyrics 3D fan degrades to 2D (below).
- **AOT/trim:** an app that uses no editorial backdrop, no live Acrylic, and no arbitrary-path shadows
  trims `Effects.D2D1` entirely (Hosting-only reference + `IEffectRunner` null-impl). Keeps the default
  binary small.

---

## 4. The `EffectAux` Scene column — the animatable effect parameters

Animation must move blur sigma / exposure / wash-alpha **without** re-recording geometry and **without**
LayoutDirty. These live in a **cold** SoA slab referenced by the hot `NodePaint` only for the ~handful of
nodes that animate effects (hero, now-playing, lyrics) — exactly the hot/cold split the §4.4 `LayoutAux`
amendment established.

```csharp
namespace FluentGpu.Scene;

// NEW cold column: SlabAllocator<EffectAux>, referenced from NodePaint by an EffectAuxRef (-1 = none).
[StructLayout(LayoutKind.Sequential)]                       // 32B; 0 for the common node (no slab row)
public struct EffectAux
{
    public float BlurSigma;        // live-acrylic / backdrop animated sigma
    public float Exposure;         // backdrop exposure tick
    public float WashAlpha;        // gradient-wash cross-fade alpha (recolor)
    public float CrossFade;        // 0..1 image/backdrop cross-fade (shared with media-pipeline)
    public ColorF Tint;            // animatable tint (premul-linear, realized once)
    public EffectChain Chain;      // -1 none; else the EffectChainTable row for the layer's IEffectRunner
}
```

- Writing `EffectAux` fields marks **`PaintDirty`** (the layer must re-composite) — never `LayoutDirty`.
- `BlurSigma`/`Exposure`/`WashAlpha`/`Tint` are read by the **batcher** (instance params) or the
  **FrameGraph** (layer effect params) at phase 8/9 — they do **not** force geometry re-record for the
  TransformDirty-only fast path; a sigma change is a `PaintDirty` re-composite of the one layer.
- `CrossFade` is the same field media-pipeline writes for image cross-fade; this subsystem reuses it for
  backdrop cross-fade so there is one cross-fade mechanism, not two.

**FA-1 (contract addition):** `EffectAux` (cold slab) + an `EffectAuxRef:int` in `NodePaint`, and
`EffectChainTable` (`SlabAllocator<EffectChainDesc>`) in `Scene`. Filed as net-new amendments per
`app-requirements` §3.4's "all net-new contract additions" note (these are NOT pre-existing stubs; only
`DrawBackdropCmd`/`PushLayer{Effect}`/`VisualKind.Backdrop`/`NeedsLayer` are the real existing stubs).

---

## 5. Connected / implicit / driven animation — the phase-7 `AnimTrack`

### 5.1 `AnimTrack` and the engine (owned here)

```csharp
namespace FluentGpu.Animation;

public enum AnimChannel : byte { TranslateX, TranslateY, ScaleX, ScaleY, Rotation,
                                 Opacity, BlurSigma, Exposure, WashAlpha, CrossFade, Tint }
public enum ClockKind   : byte { Frame, Driven }     // Frame = FrameTime.Delta; Driven = external ref

[StructLayout(LayoutKind.Sequential)]                // ~48B POD; lives in a SlabAllocator<AnimTrack>
public struct AnimTrack
{
    public NodeHandle Target;        // node whose column this writes (or a DetachedAnim slot)
    public AnimChannel Channel;
    public float From, To;
    public float ElapsedMs, DurationMs;
    public Easing Easing;            // enum → static fn-ptr table (no delegate, AOT-clean)
    public ClockKind Clock;
    public int   DrivenRef;          // -1 (frame clock) or index into the DrivenClockTable
    public AnimFlags Flags;          // Detached, PinsImage, Superseded, Completed, ReducedMotionExempt
}
```

```csharp
public sealed class AnimEngine             // ticked in phase 7 (UI thread); writes Scene columns
{
    SlabAllocator<AnimTrack> _active;       // attached tracks (target node is live in topology)
    DetachedAnimSlab         _detached;     // tracks whose target node has unmounted (§5.3)
    DrivenClockTable         _clocks;       // ref-float sources (scroll offset, playback ms)

    public void Tick(in FrameTime ft);      // advance both lists, compose, write columns, mark dirty
}
```

**Tick algorithm (phase 7, after layout, before record — `architecture-spec.md` §4.8 line 7):**

```
AnimEngine.Tick(ft):
  for each track t in _active ∪ _detached:           // index loops, no IEnumerable
     dt   = (t.Clock == Frame) ? ft.DeltaMs : _clocks[t.DrivenRef].DeltaMsSince(t.lastSample)
     u    = t.ElapsedMs / t.DurationMs              // 0..1 (Driven: u = clamp(drivenValue, 0, 1) directly)
     e    = ReducedMotion && !t.ReducedMotionExempt ? snapEnd(u) : Easing.Eval(t.Easing, u)
     v    = lerp(t.From, t.To, e)
     applyChannel(t.Target, t.Channel, v)           // see channel→column map below
     if u >= 1: t.Flags |= Completed                // retired in phase 13 (see §5.3)
```

**Channel → column → dirty axis (the load-bearing invariant — folded verbatim):**

| Channel | Writes | Dirty axis |
|---|---|---|
| TranslateX/Y, ScaleX/Y, Rotation | `NodePaint.LocalTransform` (Affine2D) | **TransformDirty** (no re-record; batcher re-applies `WorldTransform`) |
| Opacity | `NodePaint.Opacity` | **PaintDirty** (or TransformDirty if no overlap → batcher composite) |
| BlurSigma, Exposure, WashAlpha, CrossFade, Tint | `EffectAux.*` (§4) | **PaintDirty** (layer re-composite) |

**NEVER LayoutDirty.** Pop-in is `ScaleX/ScaleY` from 0.92→1.0, **not** a size change; a hero scale never
relayouts. (Animating a genuine layout property is the rare opt-in `LayoutSelfDirty`-per-tick path
documented in §7 cross-cutting — not used by any animation this subsystem owns.)

**Compose order for concurrent tracks on one node (MADE — fixed-order matrix multiply):** when a node has
both a pop-in scale and a connected-anim transform, `AnimEngine` composes them in a **fixed channel order**
(Translate → Scale → Rotation) into one `LocalTransform`, so the result is deterministic and order-
independent of track insertion. Opacity and effect channels are scalar and just multiply/overwrite.

### 5.2 Implicit transitions (`UseImplicitTransition`)

```csharp
// FluentGpu.Hooks convenience
void UseImplicitTransition(in TransitionSpec onShow, in TransitionSpec onHide);
public readonly struct TransitionSpec       // POD, stackalloc-friendly
{ public readonly AnimChannel Channel; public readonly float From, To, DurationMs; public readonly Easing Easing; }
```

- **Mount (show):** on first mount of a node carrying a transition, `AnimEngine` seeds the `onShow` tracks
  (e.g. `Opacity 0→1`, `ScaleY 0.96→1`) in phase 6.5 (after the node's first layout, so `Bounds` are valid).
- **Unmount (hide) — the lifecycle pin (folded, the critical correction):** the reconciler runs hook
  cleanups **synchronously on unmount** (`reconciler-hooks.md` §4.4) and recycles the handle. We do NOT
  free-run a node in the recycled topology slot. Instead, on unmount with a live `onHide`:
  1. Hook cleanups still run synchronously (subscriptions die — correct).
  2. The node's **paint columns** (`LocalTransform`, `Opacity`, `EffectAux`, `Bounds`, `Fill`/`Stroke`/
     `Corners`) are **copied into a `DetachedAnim` slab row** (see §5.3) and its **`ImageHandle` (if any)
     is refcount-pinned** so the texture survives the exit animation.
  3. The topology node sets the `Detached` `NodeFlag` and is deferred-freed (cascade-drained if the parent
     frees first — `architecture-spec.md` §5.4 already specs this).
  4. `AnimEngine` advances the detached `onHide` tracks from the **separate `_detached` list**; on
     completion (phase 13) the slot is freed, the gen bumped, and the `ImageHandle` un-pinned.

### 5.3 The `DetachedAnim` slab (owned here)

```csharp
namespace FluentGpu.Animation;

public sealed class DetachedAnimSlab
{
    // gen-versioned (ABA-safe); each row is a self-contained renderable snapshot, NOT a topology node.
    SlabAllocator<DetachedNode> _slab;       // DetachedNode = the copied paint columns + WorldTransform
    public Handle Detach(NodeHandle src, in TransitionSpec hide, ImageHandle pin /*or Null*/);
    public void   Retire(Handle h);          // phase 13: free row, gen++, un-pin ImageHandle via deferred-delete
}

[StructLayout(LayoutKind.Sequential)]        // the renderable snapshot (no GC ref)
public struct DetachedNode
{ public Affine2D LocalTransform; public float Opacity; public EffectAux Aux; public LayoutRect Bounds;
  public BrushHandle Fill, Stroke; public CornerRadius4 Corners; public ImageHandle PinnedImage;
  public Handle TrackHead; /* first AnimTrack in _detached for this node */ }
```

- **Why a separate slab (MADE):** the recycled topology slot may be immediately reused by a *new* mounted
  node next frame (the slab free-list pushes/pops). A detached exit animation must own an **independent,
  stable** renderable that cannot be overwritten by topology churn. The detached set is tiny (count of
  currently-exiting nodes — typically 0–3), so a small slab is right.
- **Recording detached nodes (phase 8):** the render-walk, after walking live topology, walks
  `DetachedAnimSlab` and emits the same opcodes (`DrawImageCmd`/`FillRoundRectCmd`/...) from the snapshot
  columns, with `WorldTransform` composed from the detached node's own `LocalTransform` (it has no live
  parent). They sort into the DrawList at the z they had at detach time (carried in the snapshot's sortkey).
- **Cascade + gen-bump at completion (phase 13):** when an exit track completes, `Retire` frees the row,
  bumps the gen (any stale `Handle` fails validation), and pushes the pinned `ImageHandle` un-pin onto the
  **deferred-delete fence ring** (render-thread-only) so the GPU texture is freed only after the last
  in-flight frame that referenced it (`hardened-v1-plan.md` §2.3 split-resource retire).

### 5.4 Connected animation (`UseConnectedAnimation`)

The album-art card flies from a grid thumbnail to the now-playing hero.

```csharp
public readonly record struct ConnectedHandle(StringId Key);
ConnectedHandle UseConnectedAnimation();         // dest registers; source tags with .Key("art:"+uri)
```

```
SOURCE side (grid card, the frame before navigation):
  phase 5 (reconcile): on the navigation that unmounts the source, snapshot its world-rect
     (from prev-frame WorldBounds[]) into a ConnectedRegistry keyed by the StringId.
     PIN the source ImageHandle for the animation's lifetime (MediaService.Pin) — released on
     completion/cancel. (Fixes the fast-track-change "overlay samples a dead slot" bug.)
DEST side (now-playing hero):
  phase 6.5 (layout-effect): read the dest node's laid-out Bounds → world-rect.
     If a ConnectedRegistry entry exists for the same Key:
        create a TRANSIENT OVERLAY node (a DetachedAnim row) that OWNS the pinned source texture,
        seed transform tracks: TranslateX/Y + ScaleX/Y from source-rect → dest-rect, 300ms, Easing.Smooth.
philosophy: the real source unmounts and the real dest is hidden until the overlay track completes;
            at completion the overlay retires and the dest art reveals (its own CrossFade).
```

- **Superseding navigation (MADE):** a second navigation while the first connected anim is in flight
  **cancels the prior track + releases its pin** before seeding the new one (no two overlays fighting; no
  leaked pin). The `ConnectedRegistry` entry is single-slot per Key.
- **Concurrent with pop-in:** if the dest hero also has an `onShow` pop-in, the two transform tracks on the
  overlay/dest compose via the fixed-order multiply (§5.1).
- **Pin lifetime:** the pin is held by the `DetachedNode.PinnedImage` field for the overlay's life; released
  by `Retire` (completion) or by the supersede-cancel path. The pin **survives the source node's
  synchronous unmount** — that is the whole point of routing it through the detached slab, not the recycled
  topology slot.

### 5.5 Driven clock (`AnimTrack.DrivenClock`) — scroll-fade and playback-synced lyrics

```csharp
namespace FluentGpu.Animation;

public sealed class DrivenClockTable           // ref-float sources; index = AnimTrack.DrivenRef
{
    // A driven clock is a value SOURCE, not a wall-clock. Sampled in phase 7.
    public int Register(GetDrivenValue source);          // returns DrivenRef
    public float Sample(int drivenRef);                  // current normalized/absolute value
}
public delegate float GetDrivenValue();        // e.g. () => scrollOffsetPx, () => playbackMs
```

- **Scroll-fade:** `DrivenRef → () => ScrollOffset`. The shy-banner / header fade is an `Opacity` (or
  `BlurSigma`) track whose `u` is `clamp(scrollOffset / fadeDistance, 0, 1)` — **time-independent**, driven
  by scroll position. Writes `Opacity`/`EffectAux` → PaintDirty, never LayoutDirty. (Sticky-header *pin* is
  a transform write owned by the virtualization subsystem, not here.)
- **Playback-synced lyrics:** `DrivenRef → () => IPlaybackClock.PositionMs`. The active lyric line's color
  ramp is driven by **playback ms, not frame ms** (so it stays synced across seek/pause/buffering). See §6.
- `UseDrivenAnimation`:

```csharp
void UseDrivenAnimation(NodeHandle target, AnimChannel ch, float from, float to,
                        GetDrivenValue source, float domainMin, float domainMax,
                        ReadOnlySpan<DepKey> deps);
```

### 5.6 Reduced motion

```csharp
public readonly struct ReducedMotionState { public readonly bool Enabled; public readonly uint Epoch; }
ReducedMotionState UseReducedMotion();         // reads PAL (SPI_GETCLIENTAREAANIMATION); Epoch projects to DepKey
```

- **Honors the OS setting** (`SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)` via PAL; macOS
  `NSWorkspace.accessibilityDisplayShouldReduceMotion`). Reactive via an `Epoch` `Context<uint>` (boxless,
  same pattern as `ISystemColors.Epoch` in the theming subsystem) — **not** a fat struct context (would box
  into `DepKey`).
- **Policy (MADE):** when `Enabled`, `AnimEngine.Tick` **snaps every non-exempt track to its end value**
  (no motion) but **keeps opacity cross-fades** (fades are not "motion" and aid orientation — matches the
  platform reduced-motion convention). `AnimFlags.ReducedMotionExempt` marks the rare track that must still
  animate (e.g. a loading spinner). Connected animation under reduced motion = an instant opacity cross-fade
  at the dest (no fly).

---

## 6. Synced lyrics — animation timing (layout/shaping deferred to LyricsLayoutEngine)

This subsystem owns the **timing**, not the shaping. `FluentGpu.Media.LyricsLayoutEngine` (over the Text
seam) shapes lines via `ITextShaper` → cached `GlyphRunRealization`. The animation contract:

- **Per-syllable color = per-INSTANCE glyph color written by the phase-7 `AnimTrack` into the glyph instance
  data at batch time (MADE — folded).** It is **NOT** a `BrushHandle` re-bake (would mint a new handle every
  tick and break the clean-span invariant), and **NOT** a gradient-atlas row lerp (needs the missing texture-
  upload path). The active line's `DrivenClock` (playback ms) drives a per-syllable `t`; the batcher writes
  the lerped color into the glyph instance's `color` field directly.
- **The active line is `PaintDirty` and re-records its single glyph run every frame** — trivially within
  budget (one tiny node). Caching applies to **shaping** (the `GlyphRunRealization` is stable), not to
  per-frame instance emission. We explicitly drop the "clean-span reuse for the active line" framing.
- **Line scroll** = `LocalTransform` translate-Y track (TransformDirty).
- **Backdrop** = `UseImageBackdrop` (§2.2).
- **3D fan** (optional) = `PushLayer{Effect=Transform3D}` via the optional effects leaf;
  `IEffectRunner.Supports(Transform3D)` gates it — perspective is out of the core 2.5D `Affine2D` scope
  (`architecture-spec.md` §5.4 says 2.5D/perspective is out of scope for v1's `Affine2D`), so this is an
  effects-leaf-only capability that degrades to a flat 2D list on MPS/unsupported backends.

```csharp
LyricsBinding UseSyncedLyrics(LyricLines lines, IPlaybackClock clock);   // timing only; layout in Media
```

---

## 7. 13-phase placement + thread map (this subsystem's rows)

Per `hardened-v1-plan.md` §2.2, authoring is on the **UI thread**; execution (record/bake/effect-run) is on
the **RENDER thread** (the sole `ComPtr` owner). The PUBLISH(13a) seam sits between phase 7 and 8.

| Phase | Thread | This subsystem |
|---|---|---|
| 1 pump | UI | (window-backdrop epoch / reduced-motion epoch read from PAL flag words — cheap) |
| 4 render | UI | `UseImageBackdrop`/`UseAcrylic`/`UseImplicit/Connected`/`UseDriven` hook bodies; cache probes; mount-time edge alloc only |
| 5 reconcile | UI | connected-anim **source snapshot + pin**; unmount → **detach to `DetachedAnimSlab`** (copy paint columns, pin ImageHandle) |
| 6.5 layout-effects | UI | connected-anim **dest seed** (reads laid-out `Bounds`); implicit `onShow` seed (Bounds valid) |
| **7 animation** | UI | **`AnimEngine.Tick`**: advance active + detached + driven tracks → write `LocalTransform`/`Opacity`/`EffectAux`; mark Transform/PaintDirty; **never LayoutDirty** |
| PUBLISH (13a) | UI | `EffectAux`/`LocalTransform`/`Opacity` columns + `DetachedAnim` snapshots value-copied into `SnapshotColumns` |
| 8 record | RENDER | emit `DrawImageCmd`(baked backdrop) / `PushLayerCmd{Effect}` (acrylic/3D) / detached-node opcodes; clean-span memcpy for unchanged backdrop |
| 9 batch | RENDER | apply animated `WorldTransform` to cached quads (TransformDirty fast path); write per-instance lyric glyph color; layer effect params from `EffectAux` |
| 10–11 submit/present | RENDER | (acrylic two-pass barrier ordering; the window-backdrop transparent clear is part of the normal pass) |
| **13 bake-drain** | RENDER | `BackdropBaker.Drain` (time-sliced `IEffectRunner` blur+composite, byte-budgeted vs the image-upload ring); `DetachedAnimSlab.Retire` for completed exit tracks (free + gen-bump + deferred-delete un-pin) |
| 12 passive-effects | UI | `UseWindowBackdrop` `SetWindowBackdrop` PAL call (human-timescale) |

**Co-existence with video (referenced, owned by media-pipeline):** the live-Acrylic snapshot must read the
canvas RT *after* the video hole-punch has been resolved into it; the FrameGraph orders the acrylic
snapshot pass **after** chrome-over-video composition. The window-backdrop transparent clear and the video
hole-punch are different regions and do not conflict.

---

## 8. Zero-alloc + thread-confinement story

- **Phase 7 `AnimEngine.Tick` is 0-managed-alloc:** index loops over `SlabAllocator<AnimTrack>` + the
  detached slab; `Easing.Eval` is a static fn-ptr table (`enum`→pointer, no delegate); channel apply is a
  `ref`-into-column store. No `IEnumerable`, no LINQ, no closure.
- **`TransitionSpec`/`AnimTrack`/`EffectAux`/`DetachedNode` are blittable POD** in slabs; `TransitionSpec`
  arrays passed to `UseImplicitTransition` are `stackalloc` spans (the `app-requirements` example uses
  `.Transition(onShow: stackalloc[]{...})`). Multi-spec buffers use `[InlineArray]` where fixed-arity.
- **C#14 compound assignment** for the transform compose accumulator (the `LocalTransform *= channelMatrix`
  fixed-order multiply) — the per-tick math mutates in place on the SoA struct, no temporaries.
- **`allows ref struct` sink** for the detached-node DrawList walk (phase 8) — same devirtualized POD walk
  as the main record (`dotnet10` §leaf-walk).
- **Edge alloc only at mount:** a backdrop cache-miss creates one `BackdropRtCache`/`BlurredSrcCache` slot
  + one bake-job slab row; a connected/implicit anim creates `AnimTrack` slab rows. All slab pushes (no GC
  array growth in steady state); the managed convenience hooks reuse the existing `List<HookCell>` edge.
- **Thread confinement (the keystone):** **the render thread owns every `ComPtr`** — so all `IEffectRunner`
  runs, all bake RT allocation/eviction, all `LayerPool` use, and all deferred-delete un-pins happen **on
  the render thread**, phase 8/13. The UI thread (phase 4–7) only writes POD columns and POD cache keys; it
  **never** touches a texture, an effect, or a ComPtr. `BakeTicket`/`EffectChain`/`AnimTrack`/
  `DetachedNode` are POD that cross the PUBLISH seam by value-copy; an `ImageHandle` is moved (pin transfers
  ownership intent, the actual refcount lives render-side). This makes the backdrop/effect path
  **safe-by-construction** under the seam, not audited.
- **Stale-result discipline:** every bake/palette job carries a `uint TargetGen` (now-playing track
  sequence, shared with media-pipeline); a result whose gen ≠ the node's current gen is dropped at publish.
  `DetachedAnim`/`AnimTrack` handles are gen-versioned so a captured stale handle fails validation.

---

## 9. Failure / edge cases

1. **Backdrop RT OOM:** `BackdropState.Failed` → flat dominant-color `FillRoundRectCmd`; logged once; node
   stays renderable. No per-instance-alpha "visually-wrong" fallback.
2. **Rapid track-change storm:** debounce (N skips → 1 bake) + `BakeTicket.Cancel` on each change + the
   `TargetGen` drop-at-publish guard. Worst case: one blur per ~settled track, recolors reuse the blurred
   source.
3. **Unmount during an exit animation already in flight:** the node is already detached; a second unmount of
   the same source is a no-op (topology slot already recycled). The detached track runs to completion.
4. **Connected-anim source unmounts before dest mounts (fast nav):** the pinned `ImageHandle` survives in
   the `ConnectedRegistry`/detached slot; if no dest appears within a timeout, the registry entry expires,
   the pin releases, and no overlay is created (clean no-op, no leak).
5. **Superseding connected nav:** prior track cancelled + pin released before the new track seeds.
6. **Reduced motion toggled mid-animation:** the `Epoch` context change re-renders; in-flight tracks snap to
   end on the next `Tick` (motion stops immediately; opacity fades complete).
7. **`IEffectRunner.Supports(kind)` false (Metal MPS lacks Transform3D):** 3D lyrics fan degrades to flat
   2D; backdrop noise degrades to no-noise (the `Noise` recipe flag is ignored). No crash, documented
   downgrade.
8. **Live-Acrylic before the canvas RT lands (`OQ-7` not yet implemented):** `UseAcrylic` returns a flat-
   tint `DrawBackdropCmd`; BLOCKED status surfaced in DEBUG.
9. **Device-lost during a bake:** the bake RTs are GPU realizations reconstructible from CPU state
   (`architecture-spec.md` §5.1 invariant) — recovery re-bakes from the retained `BackdropRecipe` + art
   `ImageHandle`; no managed-tree loss.
10. **Detached-slab exhaustion (pathological exit-animation flood):** the slab grows geometrically (never
    shrinks); a hard cap on concurrent detached nodes (e.g. 64) snaps the oldest exit animations to their
    end + retires them, bounding memory.
11. **Driven clock source throws / NaN:** `Sample` clamps NaN to the last good value; a thrown source is
    isolated (try/catch-log) and the track holds its last value — never propagates into the frame loop.

---

## 10. Cross-platform (macOS) boundary

| Concern | Windows | macOS (leaf swap) |
|---|---|---|
| Window backdrop | `IBackdropSource` → DWM `DWMWA_SYSTEMBACKDROP_TYPE` / DComp backdrop visual | `NSVisualEffectView` material; same `IBackdropSource` seam |
| Offscreen effects | `Effects.D2D1` (D2D1 blur/shadow + FXC noise) | `Effects.Metal` (`MPSImageGaussianBlur` + kernels) behind `IEffectRunner` |
| Reduced motion | `SPI_GETCLIENTAREAANIMATION` | `accessibilityDisplayShouldReduceMotion` |
| 3D lyrics fan | `IEffectRunner.Supports(Transform3D)`=true (D2D1 3D transform) | likely false → flat 2D degrade |
| Everything else | — | **unchanged**: `AnimEngine`, `DetachedAnimSlab`, `DrivenClock`, `BackdropBaker` policy, the `EffectAux` column, the FrameGraph schedule, all hook authoring — pure portable C# over the seams |

The portable surface (`FluentGpu.Animation`, `FluentGpu.Media` backdrop policy, `FluentGpu.Scene`
`EffectAux`, `FluentGpu.Render` FrameGraph) recompiles unchanged. Only `Effects.D2D1` and `Pal.Windows`
have Metal/AppKit counterparts.

---

## 11. Worst case — 60fps animated UI + recolor + lyrics + (video coexisting)

Per `app-requirements` §6: video composites on the OS compositor thread (media-pipeline), independent of
our loop. This subsystem's per-frame cost on a busy now-playing screen:

- **Recolor:** 0 per-frame *except* the 220 ms cross-fade, which is **opacity-only over two pre-baked RTs**
  (re-bakes nothing). A genuine art change pays one downscaled 60-DIP blur off the frame loop (phase-13
  render-thread, byte-budgeted) → a one-frame cold hitch hidden by the flat-fill → cross-fade.
- **Lyrics:** the active line re-records one tiny glyph run/frame with per-instance color from the playback
  clock — within budget.
- **Connected/implicit/scroll-fade:** all phase-7 column writes; transform tracks are TransformDirty (no
  re-record, batcher re-applies). Detached exit anims add 0–3 tiny renderables.
- **Live Acrylic:** at rest only in v1 (overlay static) → one snapshot+blur on `PushLayer`, re-taken only on
  behind-region damage. Scroll-velocity Acrylic is a v2 render-thread item.

No relayout, no video pixel work, no per-tick gradient/atlas re-bake. The only render-thread GPU spend is
the rate-limited backdrop blur and the at-rest acrylic snapshot.

---

## 12. Open questions / amendment requests

- `FA-1` — ratify `EffectAux` (cold `SlabAllocator` column + `EffectAuxRef:int` in `NodePaint`) +
  `EffectChainTable` in `Scene`, and `BackdropRef` side-table. Net-new contract additions (§4).
- `FA-2` — ratify `AnimTrack.DrivenClock` / `ClockKind`/`DrivenClockTable` in `FluentGpu.Animation` and the
  `ISceneBackend.WriteAnim` path (phase-7 column write without a reconcile). Named net-new in
  `app-requirements` §3.4.
- `FA-3` — confirm `DetachedAnim` lifetime: detach-on-unmount copies paint columns to a separate slab and
  pins the `ImageHandle`; retire on completion frees + gen-bumps + deferred-delete un-pins. This is the
  fold against the synchronous-on-unmount `RunCleanups` contract (`reconciler-hooks.md` §4.4).
- `OQ-1` (inherited, resolved) — live-Acrylic depends on the **persistent canvas RT** (`gpu-renderer.md`
  `OQ-7`), ruled in favor of the canvas RT. v1 ships acrylic at rest; scroll-velocity acrylic is v2.
- `OQ-2` — connected-anim source-rect provenance: prev-frame `WorldBounds[]` (double-buffered `FrameCache`,
  `architecture-spec.md` §5.4) is the source; confirm it is published to the UI thread at phase 5 (it is, as
  a UI-owned double buffer). Default: yes.
- `OQ-3` — backdrop bake budget vs image-upload budget share the same phase-13 render-thread byte budget;
  confirm the two-lane split (thumbs/large-art from media-pipeline + backdrop bakes) gives backdrop a
  bounded slice so a fling does not starve a now-playing bake. Default: a dedicated low-priority backdrop
  lane, rate-limited to ~1 bake/frame.

---

## 13. Changed vs the original synthesis

Amendments folded from `app-requirements-waveemusic.md` §3.4 and the cross-cutting hardenings, vs a naive
first-pass synthesis:

1. **Three backdrops are three mechanisms, never one.** Window Mica = PAL only (zero renderer pixels);
   editorial = persistent baked RT with its **own cap-2 LRU** (NOT the shared `LayerPool`, which it would
   starve); in-app Acrylic = explicit two-pass FrameGraph step (the naive "sample the live RT in-pass" has a
   read-after-write hazard).
2. **Editorial bake caches the blurred SOURCE separately, keyed `(uri,size)`**, from the final composited RT
   (keyed `(uri,size,recipe,theme)`) — so a **recolor reuses the blurred source** and only an **art change**
   pays the expensive 60-DIP blur. Bake is **downscaled ~0.5×** (blur is low-frequency).
3. **Recolor is opacity-only over two pre-baked endpoint RTs** — re-bakes nothing mid cross-fade (kills the
   per-tick atlas/gradient thrash the naive recolor implied).
4. **`BakeTicket` cancel/debounce + `TargetGen` drop-at-publish** — fixes the `fix-artist-nav-flicker` class
   of stale-result bug for backdrops, sharing the media-pipeline `TargetGen` sequence.
5. **`IEffectRunner` is the one offscreen seam; `Effects.D2D1` is an OPTIONAL trimmable leaf**, carrying the
   documented **straight-vs-premultiplied-alpha trap** (`EffectChainDesc.PremulInput`). Analytic erf shadows
   do NOT use it (only per-corner/arbitrary-path shadows fall back to it).
6. **Animation lifecycle is pinned against the synchronous-on-unmount hook contract** — the naive "free-run a
   Detached node" is wrong because `reconciler-hooks` §4.4 runs cleanups synchronously and recycles the slot.
   Fix: a dedicated **`DetachedAnim` slab** holds a stable renderable snapshot; the exit anim runs from a
   separate list; gen-bump + deferred-delete at completion.
7. **Connected animation pins the source `ImageHandle` for the animation's lifetime** (released on
   completion/cancel) — fixes the fast-track-change "overlay samples a dead slot" bug; superseding nav
   cancels the prior track + releases the pin.
8. **All transform/opacity/effect-aux writes are Transform/PaintDirty, NEVER LayoutDirty** — pop-in is a
   `Scale`, not a size change; effect sigma/exposure/wash are `EffectAux` (PaintDirty). The TransformDirty
   fast path means transform tracks do not re-record.
9. **`AnimTrack.DrivenClock`** decouples the timeline from the frame clock for **scroll-fade** (driven by
   `ScrollOffset`) and **playback-synced lyrics** (driven by `IPlaybackClock.PositionMs`, NOT frame ms — so
   lyrics stay synced across seek/pause/buffering).
10. **Per-syllable lyric color = per-INSTANCE glyph data at batch time**, NOT a `BrushHandle` re-bake (would
    break the clean-span invariant) and NOT a gradient-atlas lerp (needs the missing texture-upload path).
    The active line re-records its single glyph run every frame.
11. **`EffectAux` is a cold slab column** (hot/cold split, §4.4 pattern), referenced only by the handful of
    effect-animating nodes — not a per-node hot column.
12. **Reduced-motion via a boxless `Context<uint>` Epoch** (snaps non-exempt tracks to end, keeps opacity
    fades) — matching the theming subsystem's `ISystemColors.Epoch` pattern, not a fat-struct context.
13. **Execution is render-thread-confined** (every `ComPtr`, every `IEffectRunner` run, every bake RT, every
    deferred-delete) per `hardened-v1-plan.md` §2; the UI thread only writes POD. This makes the path safe-
    by-construction under the parallel seam, not audited — and is single-thread-correct first (UI thread
    produces+consumes, quarantine=0) per the build order.
14. **Live Acrylic is explicitly BLOCKED on the canvas-RT (`OQ-7`) decision** (resolved in favor of the
    canvas RT) and ships **at rest only** in v1; scroll-velocity Acrylic is a named v2 render-thread item.
