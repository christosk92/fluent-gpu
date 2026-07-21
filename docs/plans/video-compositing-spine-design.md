# Video compositing spine — Phase 1 design (DRM-free)

> **VERIFIED CORRECTION (see `docs/plans/video-phase1-plan.md §2`).** This doc names
> `ICompositorInterop::CreateCompositionSurfaceForHandle` as the surface-handoff primitive throughout — that is a
> **Windows.UI.Composition** call and is **wrong** for this engine, which is **raw DirectComposition** (no WUC
> `Compositor`). The correct call is **`IDCompositionDevice::CreateSurfaceFromHandle(handle, out surface)` →
> `childVisual->SetContent(surface)`** (exposed by the repo's pinned `TerraFX.Interop.Windows 10.0.26100.6`, so no
> WinRT and no `[GeneratedComInterface]` are needed). Wherever the text below says `ICompositorInterop`/
> `CreateCompositionSurfaceForHandle`, read `IDCompositionDevice::CreateSurfaceFromHandle`. Also: the graded hole
> is flushed by `Present` and the child placement by a **new** per-frame `Commit` at phase 11 (two flushes on the
> same frame-turn, not one Commit), and snap-`VideoReady`-to-0 during interactive resize is the **default**. The
> consolidated, verified plan of record is `docs/plans/video-phase1-plan.md`.

**Status:** design, not implemented. **Owner doc:** this refines `design/subsystems/media-pipeline.md §8`
(which stays canon for the video present-tree). **Scope:** the DRM-free spine only — the multi-visual
DirectComposition tree, the `IVideoPresenter` PAL seam, the `DrawVideoCmd` opcode + hole-punch, the
art→poster→live crossfade, and `VideoSurfaceRegistry`/`UseVideoSurface`. **DRM is explicitly out of scope**
and lands in a later phase; the one seam it will attach to (`IVideoPresenter.CreateSurfaceForHandle`) is
called out below so the DRM phase adds a protected surface with **zero renderer change**.

Read alongside: `design/subsystems/media-pipeline.md §6/§8/§10/§11`, `design/architecture-spec.md §5.1`
(present tree) and its 13-phase loop, `design/subsystems/gpu-renderer.md §3.1` (owns the `DrawVideoCmd`
struct shape), `design/subsystems/threading-render-seam.md §1/§2` (render-thread COM ownership).

---

## 1. Goals

- Composite externally-decoded video as a **sibling DirectComposition visual the engine never paints into**
  — the "FluentGpu touches no video pixels" bet from `media-pipeline.md §8`. The heavy continuous work runs
  on the OS compositor thread by construction; our per-frame cost is a transparent clear + a `Place` poke.
- Turn today's **single opaque DComp visual** into a **root visual with the UI swapchain z-above N video
  child visuals**, revealed through a **premultiplied-0 hole-punch** in the UI back buffer.
- Reuse the shipped image pipeline **unchanged** for the poster/album-art crossfade layers (no black first
  frame).
- Arbitrate exactly one live surface (theatre ↔ PiP ↔ sidebar) with **atomic, black-frame-free handoff** and
  **persistence across navigation** (the mini-player survives page nav).
- Ship **single-thread-correct first** (quarantine 0), matching `media-pipeline.md §11` build order item 3.
- Prove the entire spine end-to-end with **zero DRM and, at the first milestone, zero media stack at all**.

## 2. Non-goals (this phase)

- **No DRM.** No PlayReady, no `MediaProtectionManager`, no Widevine/EME, no license acquisition. Those are a
  later phase. The graded reveal, the registry, and the present tree are all DRM-agnostic; a protected surface
  is *just another opaque surface handle* to this spine.
- **No decode/demux/network of video.** We do not own an MP4/WebM demuxer or a video decoder. The live surface
  is produced by an external owner (milestone 0: an engine-owned test surface; later: an OS media object).
- **No transport-control chrome logic.** Scrim, play/pause, scrubber, and rounded-PiP corners are ordinary
  `DrawList` quads/glyphs the app already knows how to emit; the spine only guarantees they composite *over*
  the video by z-order.
- **No lyrics** (`media-pipeline.md §9`) — separate build-order item.

---

## 3. The present-tree redesign (one visual → root + children)

### 3.1 What exists today

`src/FluentGpu.Windows/D3D12/D3D12Device.cs` `BindDComp` (≈L609) builds a **single-visual** tree, render-thread
confined (`AssertSubmitThread`), deferred to the presenting thread via `DcompBindPending`:

```
CreateTargetForHwnd → CreateVisual(v) → v.SetContent(swapChain) → target.SetRoot(v) → Commit()
```

The composited swapchain (`InitSwapChain`, ≈L520): `DXGI_FORMAT_B8G8R8A8_UNORM`, `SWAP_EFFECT_FLIP_DISCARD`,
`FRAME_COUNT=2`, and **`AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED`** on the composited path
(`CreateSwapChainForComposition`). This premultiplied-alpha composited swapchain is *the reason the hole-punch
works*: a fully-transparent (premultiplied-0) region in the UI back buffer lets DWM composite whatever visual
sits **beneath** the swapchain visual straight through.

### 3.2 What Phase 1 changes

Replace the single `CreateVisual` with a **root visual + an explicit child list**:

```
Root (IDCompositionVisual)
 ├─ VideoChild[0..N]   (z BELOW; content = external surface via CreateCompositionSurfaceForHandle)
 └─ UiChild            (z ABOVE;  content = the existing swapchain)   ← topmost, painted every frame
```

Concretely `BindDComp` becomes (render-thread confined, same deferral):

```
CreateTargetForHwnd
CreateVisual(root)
CreateVisual(uiVisual);   uiVisual.SetContent(swapChain)
root.AddVisual(uiVisual, /*insertAbove:*/ TRUE, /*ref:*/ null)   // UI on top
target.SetRoot(root); Commit()
// video children are added lazily by IVideoPresenter.CreateSurface, each AddVisual(child, insertAbove:FALSE, uiVisual)
```

- **Z-order:** `IDCompositionVisual::AddVisual(child, insertAbove=FALSE, reference=uiVisual)` puts every video
  child strictly beneath the UI visual. The UI visual is painted opaque everywhere *except* the hole regions.
- **Back-compat:** with zero video children the tree is behaviorally identical to today (root wrapping one UI
  visual); the existing present path and `ResizeBuffers`-survives-`SetContent` property are unchanged. The UI
  visual still owns the swapchain; nothing about the swapchain, RTVs, or present changes.
- **Ownership:** `DcompRoot`, `DcompUiVisual`, and every `DcompVideoChild` ComPtr are **render-thread-sole-owned**
  (`threading-render-seam.md §1`), created/committed on the presenting thread exactly like the current visual.
  `IVideoPresenter` runs on that same thread.

### 3.3 Resize / move correctness (the jitter risk — call it out)

Two clocks can tear at the video edge: the **UI hole** (rendered into the swapchain, presented on our frame
clock) and the **child visual position/size** (a DComp transform, committed on `Commit`). If they update on
different commits, the hole and the video slip by a frame during a resize or a PiP drag.

Rules that keep them locked:

1. **One commit per frame, at phase 11.** The hole (a clear baked into the frame we're about to Present) and
   every `IVideoPresenter.Place`/`SetVisible` for that frame are flushed in the **same** `IDCompositionDevice::Commit`.
   `media-pipeline.md §8.3` calls this the "two-clock tear" fix; it is mandatory, not optional.
2. **Window resize:** on `ResizeBuffers`, the UI swapchain re-creates its back buffers; the DComp `SetContent`
   binding survives (per the existing comment at `BindDComp`). The video child's `deviceRect` is recomputed by
   layout for the new size and re-`Place`d in the same phase-11 commit as the first post-resize present. Do
   **not** independently resize the child on the window message thread — that is exactly the desync that makes
   XAML's `SwapChainPanel`/`MediaPlayerElement` jitter on resize.
3. **DWM clip vs. our clip:** the child visual gets a DComp clip to its `deviceRect` so an over-large external
   surface can't bleed past the hole for one commit if the sizes momentarily disagree; the hole is the source
   of truth for the visible rect.

**Uncertain / verify:** whether a graded (partial-alpha) hole during a resize *animation* looks clean under
DWM, or whether we should snap `VideoReady` to 0 (poster-only, no hole) for the duration of an interactive
resize and re-open after. Decide with a `--screenshot` A/B; default to snap-to-poster-during-resize if in doubt.

---

## 4. `IVideoPresenter` PAL seam

Interface in the engine's PAL seam folder, Windows impl in `FluentGpu.Windows/Pal/`. Portable core references
the interface only (stays TerraFX-free); every `IDCompositionVisual`/`IDCompositionSurface`/`ICompositorInterop`
ComPtr lives behind the Windows leaf.

**File:** `src/FluentGpu.Engine/Seams/Pal/IVideoPresenter.cs`

```csharp
namespace FluentGpu.Pal;

public readonly record struct VideoSurfaceId(uint Value);   // POD, opaque across the seam; 0 = none

public interface IVideoPresenter   // Windows → IDCompositionDevice child visual; macOS → AVPlayerLayer
{
    VideoSurfaceId CreateSurface();                                   // allocate a child visual (hidden until Placed)
    void Place(VideoSurfaceId id, in IntRect deviceRect, float opacity, int z);   // transform/clip poke; queued for the frame's Commit
    void SetVisible(VideoSurfaceId id, bool visible);
    void Destroy(VideoSurfaceId id);

    // The surface-handoff seam. An external owner produces a DComposition surface HANDLE
    // (DCompositionCreateSurfaceHandle on its side); we wrap it via
    // ICompositorInterop::CreateCompositionSurfaceForHandle and bind it as the child visual's content.
    // Phase 1 (DRM-free) passes an UNPROTECTED handle; the DRM phase passes a PROTECTED handle here —
    // and NOTHING else in this seam or the renderer changes. This is the single DRM attach point.
    void BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle);

    void Commit();   // flush all queued Place/SetVisible/Bind into one IDCompositionDevice::Commit (phase 11)
}
```

Notes:
- **Replaces the spec's `GetMediaPlayerSink`** (`media-pipeline.md §8.1`) with `BindSurfaceHandle`. Rationale:
  the engine has **no WinAppSDK/CsWinRT dependency**, so we cannot hand back a WinRT `MediaPlayer` sink object.
  We take the OS-neutral primitive — a DComp surface **handle** — which both an unprotected Media Foundation
  swapchain (Phase 1) and a protected MF/PlayReady swapchain (later) can produce. This is *exactly* the
  `HWCompMediaNode::SetMedia` path in WinUI (`ICompositorInterop::CreateCompositionSurfaceForHandle` →
  `SurfaceBrush` on the child visual), and it is why we sidestep WinUI issue #10918 entirely (see §9). **This is
  a deliberate divergence from the §8.1 signature — reconcile `media-pipeline.md §8.1` to `BindSurfaceHandle`
  when this lands** (canon owner is `media-pipeline.md`; see §11).
- **Windows impl** (`src/FluentGpu.Windows/Pal/DCompVideoPresenter.cs`): holds the `IDCompositionDevice` (shared
  with `D3D12Device`, one device), a small `VideoSurfaceId → (IDCompositionVisual child, IDCompositionSurface
  content, IntRect rect)` table, and queues Place/Bind mutations applied on `Commit`. `ICompositorInterop`
  (a.k.a. `IDCompositionDevice`'s `CreateSurfaceFromHandle`, or the WinRT compositor interop) wraps the handle.
  All render-thread-confined; `AssertSubmitThread` on every method like the rest of `D3D12Device`.
- **macOS boundary** (documented, not built): `IVideoPresenter` → `AVPlayerLayer` as a `CALayer` sibling under
  the `CAMetalLayer`; hole-punch protocol identical (premul-0 clear + z-order). No portable-core change.

---

## 5. The `DrawVideoCmd` opcode + hole-punch

### 5.1 Opcode

Add `DrawVideo = 17` to `DrawOp` in `src/FluentGpu.Engine/Render/DrawList.cs`, and a command record next to
`DrawImageCmd`. **The struct SHAPE is owned by `gpu-renderer.md §3.1`** — this doc owns the present/crossfade
*behavior*. Reconciling the spec's 7-field form to shipped reality:

| `media-pipeline.md §8.2` field | Shipped-reality type | Why the drift |
|---|---|---|
| `VideoSurfaceId Surface` | `int SurfaceId` | matches how `DrawImageCmd` carries `int ImageId`, not a handle struct; the recorder maps `VideoSurfaceId.Value`→`int` |
| `RectF Dst` | `RectF Dst` | same |
| `ImageHandle PosterBlur` | **dropped from the struct** | `ImageHandle` type does not exist in the engine; poster/art are emitted as **separate `DrawImageCmd`s** underneath (see §6) — no need to thread ids through `DrawVideoCmd` |
| `ImageHandle AlbumArt` | **dropped from the struct** | same |
| `float VideoReady` | `float VideoReady` | drives the graded reveal (§5.2) |
| `CornerRadius4 Radii` | `CornerRadius4 Radii` | rounded-PiP corners of the hole |
| `ClipHandle Clip` | **dropped** | `ClipHandle` does not exist; clipping uses the ambient `PushClip`/`PopClip` (`ClipCmd`) like every other op |

Resulting record (final shape is `gpu-renderer.md §3.1`'s call; proposed):

```csharp
// A video hole: clears Dst to premultiplied-(VideoReady) transparency so the DComp child beneath shows
// through at weight VideoReady. The poster/album-art crossfade underneath is ordinary DrawImageCmd (§6).
public readonly record struct DrawVideoCmd(RectF Dst, CornerRadius4 Radii, int SurfaceId,
    float VideoReady, Affine2D Transform, float Opacity);
```

Add the `DrawVideo` case to `DrawListOpcodeStats.Add` and the parallel counter field, matching every other op.

### 5.2 Hole-punch + graded reveal (the crux)

The composited swapchain is **premultiplied-alpha, z-above** the video child. DWM composites top-over-bottom:
`final = topRGB + bottomRGB·(1 − topA)`. So to reveal the video at weight `w = VideoReady`:

- Set the UI pixel in `Dst` to **alpha `(1 − w)`, premultiplied**. Then `(1 − topA) = w`, and the video child
  shows through at exactly weight `w`. At `w = 1` the region is fully transparent (a true hole); at `w = 0` it
  is fully opaque (no hole, poster only).
- The premultiplied poster/art drawn into `Dst` at group opacity `(1 − w)` *is* that top pixel: premultiplied
  `topRGB = posterColor·(1 − w)`, `topA = (1 − w)`. Substituting gives
  `final = poster·(1 − w) + video·w` — a **true art/poster → live cross-dissolve, for free**, purely from
  premultiplied compositing. **No black first frame**, no offscreen pass, no new shader.

Replay of `DrawVideoCmd` therefore does exactly one thing: **clear `Dst` to premultiplied-0** (a
transparent-black fill honoring `Radii` for rounded PiP), *after* the poster/art layers have been drawn at
`(1 − VideoReady)` opacity into the same region. Because the poster is drawn first (opaque-ish) and the clear
would erase it, the implementation is inverted in practice:

1. Recorder emits the art + poster `DrawImageCmd`s into `Dst` with `Opacity *= (1 − VideoReady)` (§6).
2. Recorder emits `DrawVideoCmd`, whose replay does **not** clear on top — instead the *residual* alpha left by
   the `(1 − VideoReady)`-opacity poster already equals `(1 − VideoReady)`, and everywhere the poster does not
   cover, the region must be forced to premul-0. So `DrawVideoCmd` replay draws a premultiplied-0 **rounded
   rect that writes only where the poster did not** — i.e. it is a *destination-alpha floor*, not an overwrite.

**Simplest correct implementation (recommended):** render the whole `Dst` region into a **known-cleared**
state first. Concretely, `DrawVideoCmd` replay: (a) clear `Dst` to premul-0 (rounded), then (b) the poster/art
`DrawImageCmd`s — emitted *after* the video command in painter order but *targeting the same rect* — paint back
at `(1 − VideoReady)` opacity over the transparent hole. That yields `topA = (1 − VideoReady)` and
`topRGB = poster·(1 − VideoReady)` with no special dest-alpha logic. **Emit order: `DrawVideoCmd` (clear)
first, poster/art `DrawImageCmd`s second, chrome last.** This keeps `DrawVideoCmd` a plain clear and reuses
`ImagePipeline` verbatim.

- **Blend interplay:** the poster/art draw with the existing premultiplied `ONE`/`INV_SRC_ALPHA` blend over the
  transparent hole — correct premultiplied result. The `BGRA8_UNORM` + `ALPHA_MODE_PREMULTIPLIED` swapchain
  makes the residual alpha meaningful to DWM. No blend-state change anywhere.
- **Scrim / controls / rounded corners:** ordinary `DrawList` quads/glyphs emitted *after* everything above,
  painting opaque over the composited region — they land in the topmost UI visual and are never transparent, so
  they always occlude the video. The rounded-PiP corner *of the hole itself* is the `Radii` on the clear.

### 5.3 Ordering without a `PassClass` enum

`media-pipeline.md §8.2` posits `PassClass = VideoHole (=0, below Shadow)`. **Reality:** `SceneRecorder` has no
`PassClass` enum — it sorts by `key = (ulong)depth << 32` (painter order by tree depth; see `SceneRecorder.cs`
≈L586). So "below all chrome" is achieved by **painter/tree order**, not a pass bucket: the video node is an
ancestor/behind sibling of its scrim+controls, so its clear + poster naturally paint before the chrome that
draws over them. **Design decision:** do *not* introduce a `PassClass` enum for one opcode — emit
`DrawVideoCmd` + poster/art at the video node's normal depth slot, and rely on the app placing scrim/controls as
later-painting descendants/siblings (which the layout already does). Note this as a **spec drift to reconcile**:
`media-pipeline.md §8.2`'s `PassClass=VideoHole` should be rewritten as "emitted at the video node's paint slot,
before its chrome descendants" (canon owner: `gpu-renderer.md §3.1` for the sort key; `media-pipeline.md` for
the behavior). If a future case needs a video hole to punch *below unrelated shallower nodes*, revisit — but
Wavee's theatre/PiP/sidebar cases are all self-contained subtrees where depth order suffices.

- **Partial present / damage:** re-punch the hole whenever ANY node overlapping the video rect is in the damage
  set — inflate the video node's damage to its full `Dst` so the transparent clear is re-emitted every frame the
  region is touched (`media-pipeline.md §8.3`). Under `FLIP_DISCARD` the back buffer is discarded post-present
  anyway, so in the single-thread Phase-1 build the whole surface is re-rendered each frame and the hole is
  always re-punched; the damage-inflation rule matters once partial present is on.

---

## 6. The 3-layer crossfade (reuses the image pipeline unchanged)

Layers, bottom → top, all inside `Dst`, all multiplied by group opacity `(1 − VideoReady)`:

1. **Album art** — a normal `DrawImageCmd` (sharp cover), its own `FadeStartMs`/`FadeDurationMs` handling the
   initial placeholder→resident fade (existing path).
2. **Poster-blur** — a normal `DrawImageCmd` (the blurred first-frame/poster), cross-fading in over the art via
   its own fade fields.
3. **Live video** — the DComp child beneath the hole, revealed at weight `VideoReady` by §5.2.

Everything in layers 1–2 flows through the **already-shipped** `DecodeScheduler` → `ImageTextureStore`
(atlas/pool) → `ImagePipeline` (`DrawImageCmd`) path with **no changes**: the poster and album art are just two
more images that decode/reside/evict like any album cover. The only new ingredient is that the recorder scales
their `Opacity` by `(1 − VideoReady)` and emits the `DrawVideoCmd` clear beneath them.

`VideoReady` itself is a **phase-7 composition animation** (opacity-only, no re-bake) driven onto the video
node's `EffectAux`, owned by the Animation engine (the `AnimValue` slab); this subsystem only *reads* it —
identical to how `DrawImageCmd.CrossFade` is animated for images (`media-pipeline.md §7`). The external surface
owner raises "first frame decoded" → the app flips `VideoReady`'s target 0→1 → the graded hole opens.

---

## 7. `VideoSurfaceRegistry` + `UseVideoSurface`

**File:** `src/FluentGpu.Engine/Media/VideoSurfaceRegistry.cs` (portable; a clean-room port of Wavee's
`ActiveVideoSurfaceService` behavior, no Wavee code copied).

```csharp
namespace FluentGpu.Media;

public enum VideoOwner { None = 0, Theatre, Pip, Sidebar }   // priority: Theatre=20 > Pip=10 > Sidebar=5

public readonly struct VideoBinding
{
    public VideoSurfaceId Surface;   // 0 when not the active owner
    public bool  IsActive;           // true ⇒ this owner drives the live child; false ⇒ draw poster/art only
    public float VideoReady;         // read by the recorder for the graded reveal
}

// Hook (Hooks/): returns a by-value binding; edge-allocs only on first slot creation.
public VideoBinding UseVideoSurface(VideoOwner owner, int priority);

public sealed class VideoSurfaceRegistry   // UI-thread arbitration; touches ZERO COM
{
    public VideoBinding Acquire(VideoOwner owner, int priority);
    public void Release(VideoOwner owner);
}
```

Behavior (all decisions on the **UI thread**; execution on the **render thread** at phase 11):

- **Exactly one live surface.** Highest-priority owner wins (the OS has one hardware-overlay path worth using).
  A lower-priority owner gets `IsActive=false` and draws only its poster/art `DrawImageCmd`s (still smooth, just
  no live pixels).
- **Atomic handoff (no black frame):** on a higher-priority `Acquire`, the registry queues, for the **same
  phase-11 `IVideoPresenter.Commit`**: `Place` + `SetVisible(true)` on the new owner's rect, *then*
  `SetVisible(false)` on the old. New shows before old hides → no gap.
- **PiP drag = hole + visual in lockstep:** the drag moves the child via `Place` **and** the poster/hole rect
  moves in the same commit — the §3.3 two-clock lock. Off-loop `Place` pokes are fine as long as the matching
  hole clear rides the same commit.
- **Persistence across nav:** the PiP `VideoSurfaceId` is a retained registry entry; tab/page switch does not
  `Destroy` it, so the mini-player survives navigation (Wavee requirement). `Destroy` happens only on explicit
  close or app teardown.

**Thread ownership:**

| State / action | Thread | Notes |
|---|---|---|
| `VideoSurfaceRegistry` arbitration (who is active, priorities, `VideoReady` target) | **UI** | decides; zero COM |
| `UseVideoSurface` hook cell | **UI** | edge-alloc on first mount only |
| `IVideoPresenter` ComPtrs, `CreateSurface`/`BindSurfaceHandle`/`Place`/`SetVisible`/`Destroy`/`Commit` | **RENDER** | executed at phase 11; single `Commit`/frame |
| the `DrawVideoCmd` clear + poster/art `DrawImageCmd`s | **RENDER (record/replay)** | published from UI at 13a like all draw data |

In the single-thread Phase-1 build, "RENDER thread" phases run on the UI thread (quarantine 0) — the split is a
later flip behind the race gate (`media-pipeline.md §11`), and nothing here blocks it.

---

## 8. Phase-1 non-DRM driver (prove the spine, zero DRM)

Milestones, each independently demoable, **canvas-first** and DRM-free throughout:

- **M0 — engine-owned test surface (no media at all).** `IVideoPresenter.CreateSurface` +
  `BindSurfaceHandle` of an `IDCompositionSurface` the engine itself paints (a solid color / animated test
  pattern via `BeginDraw`/`EndDraw`). Proves the multi-visual tree, z-order, the graded hole-punch, `Place`,
  resize-lock, and the registry — with **no decoder, no MF, no DRM**. This is the acceptance milestone for the
  spine; everything above M0 is "feed a different surface into the same seam."
- **M1 — Spotify canvas (unprotected).** Canvas loops are plain, **unencrypted** MP4/GIF and need **no
  license**. Drive an OS media object (or a minimal MF `IMFMediaEngine`) that renders into a DComp surface,
  hand its handle to `BindSurfaceHandle`. Exercises the art→poster→live crossfade end-to-end on real content
  with zero DRM. This is the ideal first *real-content* milestone.
- **M2 — unprotected general video.** A plain (non-DRM) music-video/podcast stream through the same surface
  path, exercising theatre↔PiP↔sidebar handoff, PiP drag, and nav persistence.

**The load-bearing statement:** M1/M2 use the *same* `BindSurfaceHandle(id, handle)` seam that the later DRM
phase will use. When DRM lands, the only difference is that the external owner produces a **protected** DComp
surface handle (MF + PlayReady, or a WebView2/EME child visual) — the present tree, the hole-punch, the
crossfade, the registry, and every renderer path are **unchanged**. That is the whole point of "the engine
touches no video pixels": protection is enforced entirely below the surface handle by MF/DWM/GPU, so a
protected surface is indistinguishable from M0's test surface to this spine.

---

## 9. #10918 avoidance ("do it properly")

WinUI issue microsoft/microsoft-ui-xaml#10918 reports that `MediaPlayerElement` + `MediaProtectionManager` do
not work under the Windows App SDK (the DRM pipeline UWP had was never ported), and a real user hits
`MediaFailed SourceNotSupported hresult=0xC00D715B` trying to drive protected content through the WinAppSDK
`MediaPlayer`. Our design is structurally immune because **we never depend on WinAppSDK's media path**:

- The spine's only handoff is a raw **DComp surface handle** wrapped by `ICompositorInterop::
  CreateCompositionSurfaceForHandle` — the exact primitive WinUI's own compositor uses under the hood
  (`HWCompMediaNode::SetMedia`), one level below the broken `MediaPlayerElement` framework layer.
- No packaged-identity assumption, no `MediaPlayerElement`, no WinAppSDK NuGet, no CsWinRT (consistent with the
  engine's AOT constraints). The Phase-1 spine has no media object at all beyond M1's optional plain surface.
- Because the surface handle is DRM-agnostic, when the DRM phase arrives it can drive the OS `MediaPlayer` /
  Media Foundation **directly** (owning the `MediaProtectionManager.ServiceRequested` license flow itself), or
  fall back to a WebView2/EME child visual — either way producing a surface handle for `BindSurfaceHandle`. We
  are not blocked on Microsoft shipping the #10918 fix.

`0xC00D715B` (`MF_E_UNSUPPORTED_BYTESTREAM_TYPE` family — "source not supported") is a *media-source/DRM*
failure, not a compositing failure; it lives entirely in the (later) surface-owner, never in this spine. Phase
1 cannot regress into it because Phase 1 has no protected source.

---

## 10. Zero-alloc posture

- **`DrawVideoCmd`** is a blittable POD appended to the existing command-stream arena — same mechanism as every
  other opcode, **zero managed alloc** in phases 6–13.
- **`Place`/`SetVisible`/`Commit`** take POD args (`VideoSurfaceId`, `IntRect`, `float`, `int`) — no boxing; the
  presenter's id→visual table is a preallocated array indexed by `VideoSurfaceId.Value`. The only allocations
  are **cold**: `CreateSurface`/`CreateVisual`/`CreateCompositionSurfaceForHandle` ComPtr roots on first use of
  a surface (not per frame), analogous to `CreateTexture` on cold pool growth.
- **`UseVideoSurface`** edge-allocs only on first hook-slot creation; steady-state returns `VideoBinding` by
  value.
- Poster/art ride the existing image pipeline's zero-alloc path (`media-pipeline.md §10.1`).

So the new per-frame work — one clear + one `Place` + one `Commit` — is **0 managed alloc**, preserving the
phases 6–13 gate.

## 11. Canon, ownership, and gates

**Docs to reconcile (canon owner in parens):**
- `design/subsystems/media-pipeline.md §8` (**owner** of video present behavior) — update §8.1 signature
  `GetMediaPlayerSink` → `BindSurfaceHandle`; update §8.2 `PassClass=VideoHole` → "emitted at the video node's
  paint slot before its chrome" (see §5.3); note the graded-reveal math (§5.2) as the crossfade mechanism.
- `design/subsystems/gpu-renderer.md §3.1` (**owner** of the `DrawVideoCmd` struct shape) — register the final
  field list (§5.1), reconciled to shipped `int ImageId`-style reality.
- `design/architecture-spec.md §5.1` (present tree) — amend the single-visual description to root+children (§3.2).
- `design/SPEC-INDEX.md §2` + `design/subsystems/README.md` ownership map — register the new opcode `DrawVideo`,
  the new PAL seam `IVideoPresenter`, and `VideoSurfaceRegistry`/`UseVideoSurface` to their owning docs. Run
  `powershell -File docs\design\check-canon.ps1` after (exit 0). No superseded tokens should reappear in live prose.

**Gates (per `design/subsystems/validation.md`):**
- **VerticalSlice headless golden check:** a `Rhi.Headless`/`Pal.Headless` `IVideoPresenter` fake records
  `CreateSurface`/`Place`/`SetVisible`/`Commit` calls; assert the multi-visual tree shape, one-commit-per-frame,
  and the graded-reveal invariant (poster opacity == `1 − VideoReady`, hole clear present). No GPU/window.
- **Alloc tripwire:** `GC.GetAllocatedBytesForCurrentThread()` delta == 0 across the video record/present phases
  (steady state, surface already created).
- **Handoff golden:** theatre→PiP→sidebar `Acquire`/`Release` sequence asserts exactly-one-live-surface and the
  atomic new-before-old order (both in the same simulated commit).
- **`--screenshot` visual checks (real GPU):** (a) M0 test-surface visible through the hole with correct
  z-order; (b) graded reveal at `VideoReady ∈ {0, 0.5, 1}` matches `poster·(1−w)+video·w`; (c) rounded-PiP hole
  corners; (d) resize does not tear (or, if snap-to-poster-during-resize is chosen, the poster is clean).
- **Headless Pal seam:** add `Pal.Headless` `IVideoPresenter` alongside the existing headless Rhi/Pal so the
  slice's TerraFX-free transitive closure still builds without the Windows leaf.

## 12. Build order (canvas-first)

1. **Present tree + `IVideoPresenter` (Windows) + headless fake + M0 test surface.** No opcode changes to the
   image path; prove the compositing spine and the seam. Gate: M0 screenshot + headless tree-shape check.
2. **`DrawVideoCmd` opcode + graded hole-punch + poster/art crossfade wiring.** Reuse `ImagePipeline`. Gate:
   graded-reveal screenshot + alloc tripwire.
3. **`VideoSurfaceRegistry` + `UseVideoSurface` + handoff/PiP/nav-persistence.** Gate: handoff golden + PiP-drag
   two-clock-lock screenshot.
4. **M1 canvas (unprotected real content)** end-to-end on the above. Gate: canvas plays, crossfade clean, zero
   DRM.

Single-thread-correct throughout (quarantine 0); the render-thread flip of `Place`/`SetVisible`/`Commit` +
upload drain is the later gate-guarded step, not part of Phase 1.

## 13. Open questions / risks

1. **Graded hole under DWM (verify first).** The §5.2 math assumes DWM does straight premultiplied
   top-over-bottom of the swapchain visual over the child visual. High confidence (it is the documented DComp
   model), but **must be confirmed with an M0 `--screenshot` before building on it**. If DWM clamps or treats
   the composited swapchain specially, fall back to binary reveal (hole fully open at `VideoReady==1`, poster
   until then) — a graceful degrade with a harder cut.
2. **Where does M1's unprotected surface handle come from without WinRT?** Options: a minimal hand-vtable MF
   `IMFMediaEngine` rendering to a DComp surface, or an engine-owned decode→`IDCompositionSurface` blit. Needs a
   spike; M0 (engine-painted surface) de-risks the spine independently of this answer.
3. **Interactive-resize appearance** (§3.3) — snap-to-poster vs. graded during resize; decide by screenshot.
4. **Partial-present hole re-punch** (§5.3) — inert while `FLIP_DISCARD` re-renders the whole surface each
   frame; must be wired when partial present lands so the hole survives damage-scoped repaints.
5. **`ICompositorInterop` interop surface** — confirm the AOT-clean hand-vtable path for
   `CreateCompositionSurfaceForHandle` (TerraFX exposes the DComp/DXGI interop; the WinRT compositor-interop
   variant may need a `[GeneratedComInterface]` cold binding). Isolated to the Windows leaf.
6. **One DComp device** — `IVideoPresenter` must share `D3D12Device`'s `IDCompositionDevice` (one device, one
   commit). Ensure the presenter is constructed with, not alongside, that device to keep a single `Commit`.
```
