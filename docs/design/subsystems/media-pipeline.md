# fluent-gpu — Subsystem Design: **Image & Media Asset Pipeline (`FluentGpu.Media`)**

*Images · the video present-tree · synced lyrics.* The NEW portable subsystem from
`app-requirements-waveemusic.md` §3.1 + §3.4 (video) + the lyrics fold-in. Designed strictly inside the
AUTHORITATIVE cross-subsystem contracts. This doc is the authority for the types/opcodes/RHI methods/PAL
seams/hooks listed in its manifest; it **references** (never re-specifies) the shared contracts:

- Threading & the render-thread seam: `hardened-v1-plan.md` §2 / §4.1 (CANONICAL).
- COM rules: `dotnet10-csharp14-zero-alloc.md` §4 + `hardened-v1-plan.md` §4.2.
- Handles / allocators / interning / ChunkedArena: `foundations.md` §1 + `hardened-v1-plan.md` §4.4/§4.7.
- SceneStore SoA + DrawList POD + clean-span rule: `architecture-spec.md` §4.4/§4.5/§5.4 + `gpu-renderer.md`.
- Glyph realization / atlas / DrawGlyphRun that the image realization MIRRORS: `subsystems/text.md` §3/§5.
- Hooks / DepKey / phase placement / effect timing: `subsystems/reconciler-hooks.md` §3/§4/§7.
- RHI graphics surface, UploadRing, LayerPool, blend/gamma: `gpu-renderer.md` §2/§4/§7/§8/§12.
- **The unified media *playback* API** (`IMediaPlayer`/`MediaPlayer`/`MediaRouter`, the audio graph, DRM `WithDrm`
  relay): `../../docs/plans/media-playback-api-spec.md` (deep API, LANDED M0–M5) + `../../docs/plans/video-drm-layer-design.md`
  (DRM), registered in `SPEC-INDEX.md §2`. **This doc owns the image/asset decode + video *present-tree* + lyrics;
  the playback spec owns the transport/audio/DRM contract.** They meet at the `IVideoPresenter` seam (§8) and the
  present-tree placement below.

Where I add a contract surface (a DrawList opcode, an RHI method, a PAL seam, a Foundation column), it is
filed as a **spec amendment** consistent with `app-requirements-waveemusic.md` §5 and stated as MADE.

---

## 0. Scope, thesis, and the four corrected overclaims I fold in

**Thesis.** Everything visual in WaveeMusic that is not a glyph or a vector primitive is *an externally
sourced pixel surface*: 64–512px album-art thumbnails, the editorial blurred backdrop, the now-playing art,
and externally-decoded video. This subsystem turns "a URI + a desired size" into "a resident GPU texture
referenced by a stable handle, cross-faded in, evicted under budget, and decoded entirely off the UI/render
threads" — and, for video, into "a sibling DComp visual the engine never paints into." The linchpin is a
**handle→realization indirection that is structurally identical to the glyph path** (`text.md` §5.5), so the
§4.5 clean-span machinery, the batcher, and the deferred-delete fence ring are reused with *no special-casing
beyond one `ImageRef` branch in the batcher's UV-resolve*.

**The four overclaims this doc corrects (per `app-requirements-waveemusic.md` §1):**
1. **Texture upload is a NEW RHI primitive**, not a free ride on the instance/vertex `UploadRing`
   (`gpu-renderer.md` §12 UploadRing = instance/vertex/index only). I add
   `ICommandEncoder.CopyBufferToTexture` + a dedicated texture-staging ring + a startup per-bucket texture
   pool (no `CreateTexture` in phases 6–13).
2. **`CreateTexture` in the hot window allocates** (a `HandleTable` slot + a `ComPtr` root). It is cold-path
   pool growth only; steady-state phase-13 does only `CopyBufferToTexture` + `ContentEpoch++`.
3. **Video needs a present-tree redesign**, not a fold onto the single-visual `architecture-spec.md` §5.1
   present path. I add `IVideoPresenter` + a multi-visual DComp tree + a transparent-hole-punch protocol.
4. **Per-frame lyric color must be per-INSTANCE glyph color**, not a per-tick `BrushHandle` re-bake (which
   would mint a handle every frame and break the clean-span invariant) nor a gradient-atlas row lerp (which
   needs a texture-upload path on the glyph atlas it does not have).

**Assembly.** Portable **`FluentGpu.Media`** (deps `Foundation` + `Rhi`/`Text` *interfaces* only) +
leaf **`FluentGpu.Windows` (Wic/ folder)** (behind portable `IImageCodec`). Referenced **only by `Hosting`**; acyclic.
Palette extraction lives in **`FluentGpu.Theme`** (a sibling subsystem) and is fed by *this* pipeline's CPU
staging block — see §4.6 for the handshake (no GPU readback).

> **As built (2026-07, G6c — the app-binding architecture correction).** The flagship overhaul's Wavee sweep (S8)
> originally proposed binding the app UI *directly* to the engine's `IMediaPlayer` signals and deleting the app's
> `PlaybackBridge`. **That was refused at execution with evidence and is recorded here so it is not re-litigated:**
> in a Spotify-Connect client the authoritative playback state is **`NowPlayingProjection`** — the LOCAL+REMOTE fold
> (cluster deltas + local playback events + audio signals). `PlaybackBridge` (`src/apps/Wavee/App/PlaybackBridge.cs`, the
> one boundary between framework-neutral `Wavee.Core` `IObservable<T>` and engine `Signal<T>`) mirrors *that
> projection*, not the engine player; binding the UI straight to `IMediaPlayer` would go dark during remote-device
> playback. The engine's coordinator (synthetic clock + engine `PlayQueue`) is likewise incompatible with the WASAPI
> live clock + Connect queue without a session rewrite. **What DID land (G6c):** SMTC wired end-to-end from the
> unified projection (display/status/buttons/timeline/media-keys, remote-correct) + the ticker now quiesces on pause.
> Re-homing the Spotify queue/audio path onto the engine coordinator is a **possible future dedicated phase**, not a
> sweep. (See `docs/plans/luminous-dancing-cascade.md` §WS-Media "CORRECTED".)

**Thread ownership at a glance (obeys `hardened-v1-plan.md` §2):**

| State | Sole writer | Notes |
|---|---|---|
| `ImageRefTable` (realization slab), `ResidencyManager` LRU/pin bookkeeping, `UseImage` hook cells, request-epoch | **UI thread** | mutated in phases 1/4/8/12 only |
| `DecodeScheduler` request `Channel`, result MPSC ring, recycled CPU `StagingBlock` slab | **WORKER pool** writes pixels; **render thread** drains the ring | pure decode jobs; touch no Scene/RhiTable/fence |
| Every `ComPtr` (bucket textures, staging ring, `IDCompositionVisual`), `RhiHandleTable`, GPU fence, deferred-delete ring, `CopyBufferToTexture` | **RENDER thread** | the §2 confinement keystone — UI thread touches ZERO COM |
| `VideoSurfaceRegistry` arbitration state | **UI thread** | decides; the render thread executes `Place`/`SetVisible` at phase 11 |

The build-order rule (`hardened-v1-plan.md` §6) applies verbatim: ship **single-thread-correct first**
(UI thread produces decode requests AND drains results AND uploads, quarantine = 0), then flip the
producer/consumer split behind the green race gate. §11 states exactly which lines move.

---

## 1. The indirection: `ImageHandle` → `ImageRealization` (mirrors `GlyphRunRealization`)

`DrawImageCmd` references an **`ImageHandle`** (an indirection into a Foundation slab), **never** a raw
`TextureHandle`. This is the exact shape of `text.md` §5.5 (the DrawList stores no UVs / no page; they resolve
at batch time), so the §4.5 clean-span rule — *valid IFF every handle IsLive AND, for `GlyphRunRef` and
`ImageRef` handles, the realization `ContentEpoch` is unchanged AND the baked-geometry hash is unchanged*
(`hardened-v1-plan.md` §4.6 + §7) — applies with no new validator logic for images beyond the `ImageRef` arm.

```csharp
// FluentGpu.Foundation — ImageRefTable : SlabAllocator<ImageRealization>, HandleKind.ImageRef (already reserved, foundations §1.1)
namespace FluentGpu.Foundation;

public readonly record struct ImageHandle(Handle H);     // typed wrapper over Handle, Kind=ImageRef, 8B

public enum ImageState : byte { Placeholder, Decoding, Resident, Failed, Evicted }

[StructLayout(LayoutKind.Sequential)]                    // 48 B; unmanaged; lives in a SlabAllocator column
public struct ImageRealization
{
    public TextureHandle Texture;     // 8  the resident bucket texture (Null until Resident)
    public RectF         AtlasUv;     // 16 standalone (0,0,1,1) OR the sub-rect of a small-image atlas page
    public ushort        NaturalW;    // 2  pre-decode source dims (for app aspect math — see the feedback-loop guard §6)
    public ushort        NaturalH;    // 2
    public ImageState    State;       // 1
    public byte          DecodeBucket;// 1  0..3 → 64/128/256/512 (matches Wavee ImageCacheService)
    public byte          AtlasPage;    // 1  0xFF = standalone texture, else small-image atlas page index
    public byte          _pad;        // 1
    public uint          ContentEpoch;// 4  bumped on (re)resident OR evict → invalidates clean spans, drives cross-fade
    public uint          LastUsedFrame;// 4 LRU recency
    public int           PinCount;    // 4  visible nodes pin; pinned ⇒ ineligible for eviction
    public uint          MemBytes;    // 4  W*H*4 (resident GPU cost; 0 if atlas-packed shares page accounting)
}
```

Why this is the right `ImageHandle`-not-`TextureHandle` decision (KEEP, blessed by the synthesis critique):
- The retained `DrawImageCmd` survives decode completion / eviction / re-residency **without re-recording**:
  the recorder emitted a stable `ImageHandle`; only `ImageRealization.ContentEpoch` changes, and the
  clean-span validator already keys on it. One node re-records on a content-epoch bump, exactly like a glyph
  whose atlas slot moved.
- The batcher resolves `(Texture, AtlasUv, page)` *at batch time* (one slab read), so a small image that gets
  promoted into / evicted from the atlas is transparent to the command stream — identical to how the glyph
  atlas resolves UVs late (`text.md` §5.5). This is the *single* batcher amendment images require.

**`Mutate()` chokepoint (per `hardened-v1-plan.md` §4.4).** Every write that changes pixels or geometry of a
realization goes through `ImageRefTable.Mutate(ImageHandle, ref ImageRealization)` which bumps `ContentEpoch`
and registers the handle in the per-layer `RealizationDirtySet`. There is exactly one writer (UI thread on
admit/evict callbacks). DEBUG `CleanSpanWitness` validates that no clean span references a bumped epoch and
recomputes the dest rect from current `Bounds[]/WorldTransform[]` (catches a Bounds-move-without-PaintDirty).

---

## 2. DrawList opcode: amended `DrawImageCmd` (+ no `DrawMosaicCmd`)

The contract's original `DrawImageCmd { TextureHandle Tex; RectF Dst; RectF SrcUV; float Opacity; }`
(`architecture-spec` §3.2) is AMENDED to the indirection form plus the visual knobs WaveeMusic needs
everywhere (circle/rounded art, cross-fade, fit).

**The `DrawImageCmd` struct SHAPE is owned by `gpu-renderer.md` §3.1** — the UNION form
`{ ImageHandle Image; RectF Dst; CornerRadius4 Radii; BrushHandle PlaceholderFill; float CrossFade;
ClipHandle Clip; byte Stretch; byte Flags }` with the single canonical `Stretch` enum
`{ 0=UniformToFill, 1=Uniform, 2=None, 3=Fill }`. This doc does NOT restate the struct body; it owns only
the **residency / `ImageRealization`** behavior below (how `Image` resolves, what `PlaceholderFill`/
`CrossFade` mean against `ImageState`/`ContentEpoch`, and the atlas-packing the `Flags.isAtlasPacked` hint
reflects).

**Record-time placeholder substitution (phase 8).** If `ImageRefTable.Get(Image).State != Resident`, the
recorder emits a `FillRoundRectCmd { Rect=Dst, Radii, Fill=PlaceholderFill }` instead — **one quad, zero
texture bind**. On the upload that flips the state to `Resident`, `Mutate()` bumps `ContentEpoch`, the one node
re-records to a real `DrawImageCmd`, and the cross-fade animates (§7). This is the "placeholder→image
cross-fade" with no special blit machinery.

**SortKey (per `gpu-renderer` §3.2).** Images are `PassClass=Image (=4)`; `TextureBindId` = the resolved page
or standalone texture id; atlas-packed images on the same page share `TextureBindId` ⇒ one
`DrawIndexedInstanced` for a whole shelf row (the "shelf row = 1–2 draws" target). Image instances reuse the
80B `QuadInstance` (`gpu-renderer` §3.4) with `Params0.hasTex=1`, `TexOrGradId=page`, and the resolved
`AtlasUv` packed into the uv selector.

**No `DrawMosaicCmd`.** A 2×2 mosaic is a `Grid` of 4 `UseImage` calls (4 normal `DrawImageCmd`s). But the
four tiles share **one `MosaicGroup` residency unit** (§3.5): one pin, one priority, one cancel ticket, one
coalesced decode job, so the channel-drop policy drops the whole group or none — never a permanent 3-of-4
mosaic. This is decisive: a `DrawMosaicCmd` would duplicate the entire fill/atlas/clip machinery for zero gain.

**Small-image atlas is v1 (`OQ-4` promoted). This doc OWNS the atlas residency + packing + `AcquireAtlasPage`
(§4.1); `gpu-renderer.md` owns ONLY the batch-time UV-resolve `ImageRef` branch that consumes the resolved
page/UV** (the split is stated in both docs). Images with both bucket dims ≤128 pack into a shared
`BGRA8_UNORM` atlas page (the *swapchain* buffer format per the COLOR contract; sampled in linear via the SRV
being viewed as `_SRGB` only where appropriate — album art is content, treated as authored sRGB → linearized
on sample). The page is a render-thread-owned `ITexture` from the texture pool. The batcher's UV-resolve gains
the `ImageRef` branch; UVs are **never baked into the command** (reconciles the `gpu-renderer` §9 image-brush
"baked TextureHandle" note — for the *node* image path we always indirect).

---

## 3. `DecodeScheduler` — bounded Channel → workers → constrained-bucket decode

Off-thread, allocation-disciplined, priority+cancel aware. Lives in `FluentGpu.Media`; the worker pool is
owned by `Hosting` (`hardened-v1-plan.md` §2 WORKER POOL = `clamp(ProcessorCount-2, 2..6)`; this subsystem
uses N=min(4,cores) of them for decode, the rest shared with palette/fetch).

```csharp
namespace FluentGpu.Media;

public enum DecodePriority : byte { Visible = 0, Overscan = 1, Prefetch = 2 }   // 0 = highest

[StructLayout(LayoutKind.Sequential)]                    // POD; crosses the Channel with no boxing
public struct DecodeRequest
{
    public ImageHandle  Target;      // the realization slot to fill
    public StringId     Src;         // interned uri ("wavee-artwork://…" | "https://i.scdn.co/…")
    public uint         RequestEpoch;// stamped from the UseImage hook cell — survives slot recycle (§5)
    public byte         Bucket;      // 0..3
    public DecodePriority Priority;
    public byte         GroupId;     // 0 = standalone, else MosaicGroup coalescing id
    public ushort       CancelTicket;// index into the scheduler's CTS table
}

[StructLayout(LayoutKind.Sequential)]
public struct UploadRequest          // worker → render thread, by handle
{
    public ImageHandle  Target;
    public uint         RequestEpoch;// validated again at admit (defeats stale late upload)
    public int          StagingSlab; // index into the recycled StagingBlock slab (CPU pixels)
    public ushort       W, H;        // decoded (bucket) dims
    public ushort       NaturalW, NaturalH;
    public byte         Bucket;
    public byte         Status;      // 0 ok | 1 decode-failed | 2 fetch-failed
}
```

```csharp
public sealed class DecodeScheduler
{
    readonly Channel<DecodeRequest> _in;     // bounded: cap ~256, custom drop-lowest-priority overflow
    readonly /*MPSC*/ UploadResultRing _out; // lock-free ring drained by the render thread (§2)
    readonly SlabAllocator<StagingBlock> _staging;   // recycled CPU pixel blocks, bucket-sized
    readonly IImageCodec _codec;             // FluentGpu.Windows Wic/ leaf behind the portable interface
    readonly CancellationTokenSource[] _cts; // per CancelTicket; pooled, reset on recycle

    public ushort Enqueue(in DecodeRequest r);   // returns CancelTicket; bounded write or priority-drop
    public void   Cancel(ushort ticket);         // row-recycle / unmount
    public void   Reprioritize(ushort ticket, DecodePriority p);  // overscan→visible on scroll settle
}
```

> **As built (2026-07):** the `SlabAllocator<StagingBlock>` shipped as `PixelBufferPool` (`FluentGpu.Engine/Media/`) —
> a thread-safe pow2-bucket (16 KB–16 MB) pool with a **retained cap of 32 MB** (`DefaultRetainedCapBytes`, hardcoded,
> no env knob): `Rent` always succeeds (fresh alloc on a bucket miss, exact-size unpooled past 16 MB), `Return` parks
> only while retained bytes stay under the cap and otherwise drops the array for the GC, and `Trim` releases all parked
> arrays on the ~30 s idle cadence. ONE pool is shared by the decode workers (dst BGRA buffers) and the async-upload
> copies (`AppHost.PixelPool` → `ImageUploadQueue.BufferPool` → render-side `ReturnUploadBuffer`), so both draw on one
> budget. The interim `ArrayPool<byte>.Shared` shortcut retained ~300 MB (8 arrays/bucket × per-core partitions); only
> the encoded **fetch** buffer (`FetchResult`, contract-bound) still rides `ArrayPool.Shared`.

**Worker job (pure; touches no Scene, no RhiTable, no fence — `hardened-v1-plan.md` §2 WORKER rule):**
```
DecodeJob(req):
  if cts[req.CancelTicket].IsCancellationRequested: return            // dropped before any work
  bytes = Fetch(req.Src)                  // disk via IVirtualMemory-mapped file, or HTTPS (app-provided client)
  block = staging.RentBucketBlock(req.Bucket)        // recycled; W*H*4 BGRA8, no per-job alloc
  ok = _codec.DecodeConstrained(bytes, req.Bucket, block.Span, out W, out H, out natW, out natH)
       // CONSTRAINED decode: WIC IWICBitmapScaler / decode-to-target so we never materialize the full-res bitmap
  if req.GroupId != 0: coalesce — hold until all 4 group members decoded, then publish 4 UploadRequests atomically
  _out.Publish(new UploadRequest{ Target=req.Target, RequestEpoch=req.RequestEpoch, StagingSlab=block.Index, W,H,natW,natH, Bucket=req.Bucket, Status=ok?0:1 })
```

**`IImageCodec` (portable seam; PAL-adjacent, lives in `Rhi`/`Pal`-style interface namespace within Media):**
```csharp
public interface IImageCodec      // FluentGpu.Windows Wic/ implements on Windows; CGImageSource on macOS
{
    bool DecodeConstrained(ReadOnlySpan<byte> encoded, byte bucket, Span<byte> dstBgra8,
                           out ushort w, out ushort h, out ushort naturalW, out ushort naturalH);
    bool ProbeSize(ReadOnlySpan<byte> encoded, out ushort naturalW, out ushort naturalH);  // cheap, no decode
}
```
The leaf uses `[GeneratedComInterface]` for WIC (`IWICImagingFactory`/`IWICBitmapDecoder`/`IWICBitmapScaler`/
`IWICFormatConverter`) — this is **cold/warm COM** (decode is human-timescale, off the frame loop), so the
COM ruling (`dotnet10-csharp14-zero-alloc.md` §4) says `[GeneratedComInterface]`, NOT hand-vtable. WIC COM
objects are created and used *entirely on the worker thread* and never cross into the render thread; the worker
hands across only POD (`UploadRequest` + a staging-slab index) — so the per-thread-COM FGCOM rule holds.

**`DecodeConstrained` is the WIC overclaim fix:** decode directly to the bucket size via the scaler so a 3000px
cover never materializes full-res in CPU memory; `block.Span` is exactly `bucket*bucket*4` bytes.

**Channel overflow policy:** on a bounded-write failure the scheduler evicts the **lowest-priority off-screen**
pending request (Prefetch first, then Overscan), never a Visible one. This is the "fast scroll drops the
blown-past requests" behavior; combined with `Cancel` on recycle it bounds the worker queue under a fling.

**Zero-alloc:** `DecodeRequest`/`UploadRequest` are POD across the `Channel`/ring; `StagingBlock` is recycled
from a `SlabAllocator`; the only worker allocation is the OS fetch buffer (app-owned `HttpClient` /
memory-mapped file via `IVirtualMemory`), explicitly off the frame loop and per-worker `AllocScope`-wrapped for
the alloc-tripwire (`hardened-v1-plan.md` §4.5).

---

## 4. `ResidencyManager` — LRU, pin-before-admit, frame-START eviction, deferred-free by fence

```csharp
namespace FluentGpu.Media;

public sealed class ResidencyManager       // UI-thread bookkeeping; render-thread executes GPU free
{
    long  _residentBytes;   int _residentCount;
    const long SoftBytes = 192L<<20, HardBytes = 384L<<20;   // 192MB soft / 384MB hard
    const int  SoftCount = 1024;

    public void Touch(ImageHandle h, uint frame);          // phase 8: LastUsedFrame = frame
    public void Pin(ImageHandle h);    public void Unpin(ImageHandle h);   // phase 8 record / phase 12 unmount
    public void EvictSweep(uint frame);                    // phase 1 (frame START)
    public bool Admit(in UploadRequest u, RhiUploadContext rhi); // phase 13 (render thread), pin-before-trim
}
```

**Decision: LRU, not LFU.** Scroll working sets are recency-dominated; LFU would keep a once-hot off-screen
cover resident over a just-scrolled-in one. LRU keyed on `LastUsedFrame` (set in phase-8 `Touch`).

**Frame-START eviction (phase 1), mirroring the glyph-atlas rule (`text.md` §5.3):** the sweep runs before any
recording, so nothing referenced by a live command *this frame* can be evicted mid-frame. Eligibility:
`PinCount == 0 AND State == Resident AND LastUsedFrame < frame - Hysteresis (≈90 frames ≈1.5s)`. Sweep until
`_residentBytes < SoftBytes && _residentCount < SoftCount` (oldest first; a small arena dirty-WORKLIST of
candidates, not a full scan — idle = O(0)).

**Pin-before-admit (the documented WaveeMusic self-eviction race, fixed in the contract):** in phase-13
`Admit`, the just-uploaded entry is `Pin`ned **before** the trim consideration runs, so a fresh upload can
never be the victim of its own admit sweep. (Admit does not itself sweep — sweep is phase 1 — but the pin
guards the boundary case where admit pushes over hard budget and forces an inline emergency trim.)

**Eviction action (split-resource, two ordered fences per `hardened-v1-plan.md` §2):**
```
Evict(h):                                    // UI thread
  ImageRefTable.Mutate(h, r => { r.State = Evicted; r.Texture = Null; r.ContentEpoch++; });   // clean spans invalidated
  back-ref walk: any node whose ImageHandle == h and that is on-screen → MarkPaintDirty (re-records to placeholder)
  enqueue RetireRequest{ Texture, seq = currentPublishSeq }                                   // CPU-side
// later, RENDER thread:
  when _lastConsumedSeq > seq AND in-flight submit fence >= seq:                              // BOTH fences
     return the bucket texture to the per-bucket POOL (NOT Dispose — pooled), clear its pool-busy bit
```
The texture is **returned to the pool**, not destroyed — `CreateTexture` never runs in steady state (§5). The
deferred-free ring is the *existing* one from `gpu-renderer` §12 / `hardened-v1-plan` §2, keyed on the GPU
fence; we additionally gate on `_lastConsumedSeq` so a snapshot still recording last frame's `DrawImageCmd`
(which resolves the realization at batch time) can never read a recycled bucket texture.

**Album-art-wall safety:** 50 visible 64px thumbs = 50×64²×4 ≈ 0.8MB; a full Home of ~400 across all shelves,
if all 128px standalone, ≈ 25MB — far under soft. Off-screen shelves age out; returning to a shelf
re-requests at `Visible`. The hard cap exists only for pathological 512px-everywhere bakes.

### 4.1 The RHI delta — `CopyBufferToTexture` + texture-staging ring + per-bucket pool (REQUIRED)

The RHI seam (`gpu-renderer` §2, `architecture-spec` §4.7) has **no** texture upload. ADD to `FluentGpu.Rhi`
(interface) and `FluentGpu.Windows` (D3D12/ folder) (leaf):

```csharp
// FluentGpu.Rhi — ICommandEncoder, NEW
public readonly struct TextureRegion { public uint DstX, DstY, W, H, RowPitchBytes; public uint SrcByteOffset; }
void CopyBufferToTexture(BufferHandle staging, TextureHandle dst, in TextureRegion region);

// FluentGpu.Rhi — IGpuDevice, NEW (pool growth is COLD path only)
TextureHandle AcquireBucketTexture(byte bucket);   // pops from the startup per-bucket pool; grows pool only if empty
void          ReleaseBucketTexture(TextureHandle); // returns to pool (does NOT Dispose)
TextureHandle AcquireAtlasPage();                  // BGRA8 small-image atlas page (also pooled)
```

- **Dedicated texture-staging ring** in `FluentGpu.Windows` D3D12/: an MB-sized `D3D12_HEAP_TYPE_UPLOAD` buffer,
  bump-allocated, **fence-gated reset** — separate from the instance `UploadRing` (which stays
  vertex/instance/index only; texture rows have different alignment `D3D12_TEXTURE_DATA_PITCH_ALIGNMENT=256`
  and a much larger byte footprint, so sharing the instance ring would thrash it). `D3D12MA` backs both
  (`gpu-renderer` §0/§12).
- **Startup per-bucket texture pool:** at device init, allocate a fixed count of `BGRA8_UNORM` textures per
  bucket (e.g. 64px×256, 128px×128, 256px×48, 512px×12) + K atlas pages. `Acquire`/`Release` are pool
  pop/push. Phase 13 does ONLY `CopyBufferToTexture` into a pooled texture + `ContentEpoch++`. `CreateTexture`
  (which allocates a `HandleTable` slot + a `ComPtr` root = a managed alloc) runs **only on cold pool growth**,
  never in phases 6–13 steady state — so the phase-13 loop row is *honestly* 0-managed-alloc.
- **Texture-pool budget (stated explicitly, the capacity-planning cliff from `app-requirements` §5 risk
  register):** Σ over buckets of `bucket² × 4 × poolDepth` + `atlasPages × pageW×pageH×4`. With the depths
  above: 64²·4·256 (4MB) + 128²·4·128 (8MB) + 256²·4·48 (12MB) + 512²·4·12 (12MB) + 4×(1024²·4) (16MB)
  ≈ **52MB** reserved GPU. This is the floor; `ResidencyManager` soft/hard budgets (192/384MB) gate growth
  above it. Pool exhaustion → cold `CreateTexture` growth (logged perf counter) or, under hard budget, an
  emergency evict-then-acquire.

**PAL delta for images: none** — the codec leaf produces CPU bytes; the upload is pure RHI.

### 4.6 Palette feed (handshake with `FluentGpu.Theme`) — NO GPU readback

`UseDynamicColor` / `UseImage(wantPalette:true)` need a `Palette`. The decode worker already holds the CPU
pixels, so palette extraction is fed the **CPU staging block** (or an optional 16×16 downsample emitted
alongside the bucket decode). **No `IGpuDevice.ReadbackImage`** — a GPU readback is a UI/render-thread device
stall and is unnecessary since the decoder has the bytes. The worker, after publishing the `UploadRequest`,
also publishes a `PaletteRequest{ ImageHandle, RequestEpoch, StagingSlab, TargetGen }` to `FluentGpu.Theme`'s
`PaletteExtractor` (same worker pool). `Theme` owns the extraction (5 longs × 4096 buckets ≈ 160KB worker-pooled
accumulator) and the `Palette` POD; this subsystem owns only the *trigger* and the staging block's lifetime
(the block is recycled only after BOTH upload-admit AND palette-extract have consumed it — a 2-bit refcount on
the `StagingBlock`). This doc does not specify the palette model — see `FluentGpu.Theme` (the §3.3 subsystem).

---

## 5. The hooks this subsystem OWNS: `UseImage`, `UseMosaic`

Thin `UseRef`/`UseMemo`/`UseEffect` compositions over the reconciler runtime (`reconciler-hooks.md` §3), with
`ReadOnlySpan<DepKey>` deps (boxless; `dotnet10` §3 `DepKey`). They live in `FluentGpu.Media` and call into the
host-injected `ImageCache`/`DecodeScheduler`/`ResidencyManager` via a context the `Hosting` composition root
provides.

```csharp
namespace FluentGpu.Media;

public readonly struct ImageBinding   // returned by value; what the dev binds into Image(...)
{
    public ImageHandle Handle; public ImageState State; public ushort NaturalW, NaturalH;
    public Palette?    Palette;       // non-null only if requested via UseDynamicColor / wantPalette
}
public readonly struct MosaicBinding  { public ImageBinding T0, T1, T2, T3; }   // a 2×2 Grid of 4

// The public hooks (DepKey-span deps; alloc only on cache-miss slot creation at mount):
ImageBinding  UseImage (StringId src, int decodePx, Vector4 placeholderTint = default, bool wantPalette = false);
MosaicBinding UseMosaic(StringId mosaicUri);    // 4 coalesced ImageBindings sharing ONE residency unit
```

**`UseImage` flow (UI thread):**
```
UseImage(src, decodePx, tint, wantPalette):
  cell = ctx.UseRef<ImageCell>()                          // persists request-epoch across recycles
  bucket = BucketFor(decodePx)                            // 48→64, 96→128, 200→256, 400→512 (round up)
  deps = ctx.Deps(src, bucket, wantPalette)               // ReadOnlySpan<DepKey>, boxless
  ctx.UseEffect(static (in ImageEffectArgs a) => {        // struct-state, no closure box (dotnet10 §3.3)
        // on (re)mount or deps change:
        cell.RequestEpoch++;                              // <-- survives slot recycle, defeats stale callback
        var h = ImageCache.GetOrRequest(a.Src, a.Bucket, cell.RequestEpoch, out bool isNew, a.WantPalette);
        cell.Handle = h;
        // cleanup (deps change / UNMOUNT): scheduler.Cancel(cell.Ticket); residency.Unpin(h);
     }, deps);
  var r = ImageRefTable.Get(cell.Handle);                 // by-value read of the realization snapshot
  return new ImageBinding{ Handle=cell.Handle, State=r.State, NaturalW=r.NaturalW, NaturalH=r.NaturalH,
                           Palette = wantPalette ? PaletteCache.TryGet(cell.Handle) : null };
```

`ImageCache.GetOrRequest(src, bucket, epoch, …)` is an O(1) `(StringId src, byte bucket)` probe over a
`Dictionary`-style map (using `CollectionsMarshal.GetValueRefOrAddDefault`, `dotnet10` §3) → returns an existing
`ImageHandle` synchronously in whatever state it is (cache hit = instant, possibly already `Resident`), or on
**miss** creates a `Placeholder` slot (the one mount-time edge alloc: a slab row + map entry) and `Enqueue`s a
`DecodeRequest` at `Visible`. **In-flight dedupe:** a second `GetOrRequest` for the same `(src,bucket)` while
`Decoding` returns the same handle and does not re-enqueue.

**`UseMosaic`** = 4 `GetOrRequest` calls bound into one `MosaicGroup` (one `GroupId`, one CTS/ticket, one pin
target). The decode worker coalesces the 4 into a single job that publishes 4 `UploadRequest`s atomically (§3),
so the channel-drop policy is all-or-nothing per group.

**The request-epoch defeats the recycle-flash bug (the load-bearing zero-alloc correctness story).** When a
virtualized row recycles to a new track, the reconciler re-runs the component (deps change → `cell.RequestEpoch++`,
new `GetOrRequest`). A late `UploadRequest` from the *previous* track carries the *old* `RequestEpoch`; at
phase-13 `Admit` we compare `u.RequestEpoch` against `ImageRefTable.Get(u.Target)`'s currently-expected epoch
(stored in the cell, mirrored into the realization on request) and **drop the stale upload** (return its staging
block, do not bump ContentEpoch). This does NOT rely on `NodeHandle` generation — which `reconciler-hooks` §5.5
confirms bumps only on *free*, not on recycle-to-new-key — so a row reused for a new track without a free/alloc
cycle is still protected. The epoch lives in the `UseImage` hook cell (`UseRef`), which persists across renders.

---

## 6. The 13-phase placement (and on which thread)

Phase numbering and thread per `hardened-v1-plan.md` §2.2 (UI thread phases 1–7 + PUBLISH(13a) + 12; RENDER
thread phases 8record/9batch/10submit/11present; WORKER pool decode/fetch). **Important:** in the
single-thread-first build (§11), "RENDER thread" phases run on the UI thread, quarantine 0.

| Phase | Thread | What this subsystem does |
|---|---|---|
| **1 pump** | UI | `ResidencyManager.EvictSweep(frame)` (frame-START LRU, pin-protected). Drain the worker-completion flag (a count, not the data) so phase 13 knows there is work. |
| **2 input** | UI | (scroll handled by virtualization; this subsystem reads nothing) |
| **4 render** | UI | `UseImage`/`UseMosaic`/`UseDynamicColor` cache probes; **edge alloc only on cache-miss slot creation**. Returns `ImageBinding` by value. |
| **6 layout** | UI | nothing image-specific. **Critical anti-loop:** the dev binds row height to the *fixed decode bucket*, never the late `NaturalSize`, or decode→measure→relayout loops (§6 guard, also stated in virtualization). |
| **7 animation** | UI | `CrossFade` 0→1 on newly-`Resident` images (PaintDirty, opacity-only — no re-bake). Lyric per-instance color advance (§9) on the **playback clock**, not the frame clock. |
| **PUBLISH 13a** | UI | snapshot copies the realization-referencing `DrawImageCmd` spans + captured `ContentEpoch` array (the §4.6 "captured-epoch arrays" of the SceneFrame). |
| **8 record** | RENDER | resolve `ImageHandle→ImageRealization`; `Resident` ⇒ `DrawImageCmd`, else placeholder quad; `ResidencyManager.Touch`. **PIN AUTHORITY LIVES HERE** — the node *recorded this frame* is the one that `Pin`s its image (resolves the phase-4-request-vs-phase-8-pin ambiguity: pin follows what actually paints, not what was requested). |
| **9 batch** | RENDER | atlas-packed image instances merge by page (`TextureBindId` in SortKey); a shelf row coalesces to 1–2 draws. |
| **11 present** | RENDER | `IVideoPresenter.Place` + the canvas-RT hole committed in the SAME DComp `Commit` (§8). |
| **12 effects** | UI | mount: pins are already established at record; **unmount: `scheduler.Cancel(ticket)` + `residency.Unpin`** (synchronous in reconcile remove per `reconciler-hooks` §4.4 — a late callback must not fire into a freed slot, but the request-epoch is the deeper backstop). Submit decode requests for newly-visible nodes that missed cache. |
| **13 / drain** | RENDER | **`UploadDrain`**: time-sliced `CopyBufferToTexture` into pooled bucket textures, **byte-budgeted in two lanes** (small thumbs vs large art/bakes); `ResidencyManager.Admit` (pin-before-trim, request-epoch validate, stale-drop); deferred-free evicted textures behind both fences. |

**Two-lane byte budget (replaces the unjustified flat "4 textures/frame"):** lane A (thumbs ≤128px) gets a
high per-frame byte budget so a fling realizing ~30 thumbs fills in 1–2 frames; lane B (256/512px art + the
60-DIP backdrop bakes) is rate-limited (e.g. 1–2 per frame) so a single large bake never blows the upload
budget and drops a frame. Both lanes drain from the texture-staging ring; the ring's fence-gated reset bounds
in-flight upload memory.

---

## 7. Placeholder → cross-fade (composition, no offscreen, no re-bake)

- On cache miss the realization is `Placeholder` with `PlaceholderRGBA` = the app's dominant-color tint
  (passed via `UseImage(placeholderTint)`). The recorder draws one rounded quad (§2).
- On `Resident` flip, `ContentEpoch++` → one-node re-record → phase 7 animates `DrawImageCmd.CrossFade` 0→1
  (a `UseImplicitTransition`-style track on the node's `EffectAux`, owned by Animation; this subsystem only
  reads it). The shader blends `lerp(placeholderColor, sampledImage, CrossFade)` in linear premul space — one
  PS, one instance, no `PushLayer`, no offscreen RT. This keeps the zero-offscreen-pass budget (`gpu-renderer`
  §11) and matches Wavee's `CrossFadeImage`.
- A *re-residency* after eviction (scroll back) goes Placeholder→cross-fade again, because the evict bumped
  `ContentEpoch` and cleared `Texture`.

---

## 8. Video — the present-tree redesign (FluentGpu touches no video pixels)

The single best decision (KEEP): externally-decoded video (PlayReady `MediaPlayerElement`) composites as a
**sibling DComp visual the engine never paints into**, turning the heaviest continuous work into a non-issue on
the single-thread v1 — it runs on the OS compositor thread by construction.

### 8.1 PAL seam `IVideoPresenter` (ADD; `FluentGpu.Windows` Pal/ → DirectComposition)

```csharp
namespace FluentGpu.Pal;
public readonly record struct VideoSurfaceId(uint Value);   // POD, opaque across the seam

public interface IVideoPresenter           // FluentGpu.Windows Pal/ → IDCompositionDevice child visual; macOS → AVPlayerLayer
{
    VideoSurfaceId CreateSurface();
    void  BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle);   // the surface-handoff + DRM attach point
    void  Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z);   // off-loop transform poke
    void  SetVisible(VideoSurfaceId id, bool visible);
    void  Destroy(VideoSurfaceId id);
    void  Commit();                                // flush queued Place/SetVisible/BindSurfaceHandle at phase 11
}
```
`IVideoPresenter` is the ONLY new PAL surface for video. All its `IDCompositionVisual`/`IDCompositionDevice`
ComPtrs are render-thread-confined (`hardened-v1-plan.md` §2/§4.2); `Place`/`SetVisible`/`Commit` execute on
the render thread at phase 11. **`BindSurfaceHandle` is the surface-handoff seam** (as-built,
`src/FluentGpu.Engine/Seams/Pal/IVideoPresenter.cs`; supersedes the earlier `GetMediaPlayerSink` sink-pull
shape <!-- canon-allow: names the superseded seam method on purpose -->): an external owner produces a
DirectComposition surface HANDLE (`DCompositionCreateSurfaceHandle` / `IMFMediaEngineEx::GetVideoSwapchainHandle`
on its side), and the Windows impl wraps it via `IDCompositionDevice::CreateSurfaceFromHandle` and binds it as
the child visual's content. FluentGpu never sees a decoded video frame. The **DRM path passes a PROTECTED
handle here — nothing else in this seam or the renderer changes** (§8.4).

### 8.2 Present-tree amendment to `architecture-spec` §5.1 (one visual → multi-visual)

§5.1 builds ONE opaque swapchain visual. Video needs a **root DComp visual with N children**: the **UI
swapchain visual z-ABOVE a video child visual** whose content is the external surface.

**Transparency protocol (the hole-punch):** the UI back buffer is **cleared transparent (premultiplied 0) in
the `DrawVideoCmd.Dst` region**, so DComp composes the video child through the hole. Scrim, transport controls,
and rounded-PiP corners are normal DrawList quads in the topmost UI visual, composited over by z-order. The
COLOR contract (output premultiplied; swapchain `BGRA8_UNORM`) makes premul-0 a clean see-through.

**The `DrawVideoCmd` struct SHAPE is owned by `gpu-renderer.md` §3.1** — the 7-field form
`{ VideoSurfaceId Surface; RectF Dst; ImageHandle PosterBlur; ImageHandle AlbumArt; float VideoReady;
CornerRadius4 Radii; ClipHandle Clip }`. This doc does NOT restate the struct body; it owns the
present/crossfade behavior below (the `VideoReady` 3-layer art→poster→live crossfade, the registry
arbitration, and the hole-punch present logic).
- **SortKey:** a new `PassClass = VideoHole (= 0, below Shadow)` so the transparent clear in `Dst` is emitted
  *before* any chrome; the art/poster lower layers are normal `DrawImageCmd`s (PassClass Image) drawn into the
  hole region while `VideoReady < 1`, then the hole opens.
- **The 3-layer crossfade** (art → poster → live) is phase-7 composition animation over `VideoReady`; art and
  poster are this subsystem's `ImageHandle`s (so they decode/reside/evict like any image), the live layer is
  the DComp child. There is no black frame because the lower layers paint until `VideoReady` hits 1.

### 8.3 `VideoSurfaceRegistry` + `UseVideoSurface` (priority arbitration)

```csharp
public readonly struct VideoBinding { public VideoSurfaceId Surface; public bool IsActive; public float VideoReady; }
VideoBinding UseVideoSurface(VideoOwner owner, int priority);   // theatre=20 > PiP=10 > sidebar=5

public sealed class VideoSurfaceRegistry    // UI-thread arbitration; portable port of Wavee ActiveVideoSurfaceService
{
    // Exactly ONE live surface at a time (the OS has one hardware overlay path worth using).
    // Highest-priority owner wins; handoff is ATOMIC (the new owner Places + SetVisible before the old hides)
    //   so there is no black frame on theatre↔PiP↔sidebar transitions.
    public VideoBinding Acquire(VideoOwner owner, int priority);
    public void Release(VideoOwner owner);
}
```

> **As built (2026-07, G5g — the pump/ownership seam).** The rebuilt `MediaPlayerElement` (SPEC-INDEX §2, the
> unified-media control) turned the registry into the **single-writer video-pump seam**, so that per-frame
> `Player.PumpVideo`/`SetViewport` no longer runs as a *side effect inside `Render`* (the old anti-pattern) but on
> a dedicated engine frame phase. As-built (`src/FluentGpu.Engine/Media/Playback/VideoSurfaceRegistry.cs`):
> `delegate void VideoPump(float scale)`; `RegisterPump(token, owner, pump)` (first registrant claims the slot) /
> `UnregisterPump(regId)` / **`TransferOwnership(token, owner)`** (the first-class fullscreen hand-off — enforces
> **exactly one pumping owner**, replacing the old "exactly one pumps" convention + conditional hook) /
> `IsPumpOwner(token, owner)` / `PumpAll(scale)` (invokes only the owner's pump; non-owner calls are suppressed and
> counted in `SuppressedNonOwnerPumpCount`, owner calls in `PumpInvocationCount`). The `VideoBinding` façade mirrors
> them (`RegisterPump`/`TransferOwnershipTo`/`IsPumpOwner`). **Driven from `AppHost`** at **phase 7.2** —
> `_videoSurfaces.PumpAll(_scene.DeviceScale)` in the paint prelude *after* `RunAfterAnimations()` (7.1) and
> incremental relayout, *before* the phase-11.5 `Drain`. This is the ownership-transfer contract WS-MediaUI fix #2/#3
> asked for; `Render` becomes pure.

- **Atomic handoff:** on a higher-priority `Acquire`, the registry first `Place`s+`SetVisible(true)` the new
  owner's rect, then `SetVisible(false)` the old — committed in one render-thread DComp `Commit` (phase 11).
- **PiP drag** moves the DComp child off-loop via `Place`, **with the canvas-RT hole committed in lockstep in
  the same phase-11 DComp Commit** — otherwise the two clocks (UI hole vs DComp visual position) tear at the
  PiP edge. This is the folded "two-clock tear" fix.
- **Partial present interaction (`gpu-renderer` §13):** re-punch the hole whenever ANY node overlapping the
  video rect is in the damage set — i.e. inflate the video node's damage to its own full `Dst` rect, so the
  transparent clear is re-emitted and the canvas-RT region stays a hole under partial repaint.
- **Persistence across nav:** the PiP `VideoSurfaceId` is a retained registry entry; tab switch / page nav does
  not `Destroy` it, so the mini player survives navigation (the Wavee requirement).

**macOS boundary:** `IVideoPresenter` → `AVPlayerLayer` as a `CALayer` sibling under the `CAMetalLayer`; the
hole-punch protocol is identical (premul-0 clear + z-order). No engine change.

### 8.4 DRM — one seam, a protected handle, the same hole-punch (SHIPPED)

DRM adds **no** renderer, present-tree, hole-punch, or registry change: it attaches at the single
`BindSurfaceHandle` point with a **PROTECTED** DirectComposition surface handle instead of a clear one. A
protected surface is byte-for-byte the same primitive to the compositor — DWM/the GPU enforce output
protection *below* the handle, so a protected region captures **BLACK** in a screen capture (expected DRM
behavior, not a bug; the golden-image gate cannot validate live protected pixels, only the chrome around the
hole). This is *why* DRM requires zero spine change. **Posture: the engine never sees a decrypted pixel or a
content key.**

- **Native in-process PlayReady is the shipped v1 DRM path** (`docs/plans/media-playback-api-spec.md` §9.2 +
  `docs/plans/video-drm-layer-design.md`, superseding finding 2026-07-19; on-box proven, no UWP sidecar). A
  custom CENC `IMFMediaSource` → the modern MF-CDM decryptor → decode → a non-zero protected windowless-swapchain
  handle → `BindSurfaceHandle`. It is the DRM code path of the unified spec's `MfMediaPlayer`
  (`FluentGpu.Windows/Media/`), built from `FluentGpu.PlayReady.Native.dll`
  (`ops/tools/playready-native/{PlayReadyNative.cpp,CencMediaSource.h}`). Three proven native fixes make it work: the
  right license server for the content, `MFWrapMediaType(MFMediaType_Protected)` + `MF_SD_PROTECTED` (the
  modern EME wrap, NOT the raw `MF_MT_PROTECTED` attribute that triggers the legacy ITA/OTA topology →
  `MF_E_TOPOLOGY_VERIFICATION_FAILED 0xC00D715B` of microsoft-ui-xaml#10918), and a persistent-license EME
  session — all guarded by `FG_CENC_*` A/B env vars.
- **License acquisition is the managed `WithDrm` relay.** The native CDM raises the challenge; a managed
  `Func<LicenseRequest, ValueTask<LicenseResponse>>` (spec §9.2) performs the license POST (the app supplies
  the server + token per source) and returns the license bytes for the native `Update()`. Native keeps only
  CDM/CENC/decrypt; the CDM sees no key or decrypted pixel. The relay is the direct analogue of how
  `SystemMediaControls.ButtonDispatcher` injects host behavior into a generic OS-services component.
- **Widevine via WebView2 is an optional later fallback for genuinely Widevine-only content** (there is no
  embeddable self-provisioned native Widevine CDM), not the v1 path.
- **A DRM shortfall is `MediaError{Category.Drm, Recovery.NeedsLicense/PickLowerQuality}` — never a silent drop
  to black** (`gpu-renderer.md` treats the protected surface identically to any composited surface).

Canon: `IVideoPresenter`/`VideoSurfaceId` seam shape is owned by `pal-rhi.md`; this doc owns the present-tree
placement + the DRM attach behavior above; the unified `IMediaPlayer`/`MediaPlayer`/`MediaRouter` + `MfMediaPlayer`
DRM path + the `WithDrm` relay are registered in `SPEC-INDEX.md §2` + `README.md` §2.

---

## 9. `LyricsLayoutEngine` — per-instance glyph color on the playback clock

Lives in `FluentGpu.Media`, over the Text seam (`text.md`). It does NOT own shaping, the glyph atlas, or
`DrawGlyphRunCmd` (those are Text/Render); it owns *line composition for lyrics* and *the per-instance color
animation*.

```csharp
public readonly struct LyricsBinding { public TextLayoutHandle ActiveLine; public int ActiveLineIndex; }
LyricsBinding UseSyncedLyrics(LyricsModel lyrics, IPlaybackClock clock);
```

- **Layout:** each line is shaped once via `ITextShaper` → cached `GlyphRunRealization` (Text's two-level
  cache; lyrics lines are stable). Line scroll = `LocalTransform` translate-Y (TransformDirty), never
  LayoutDirty.
- **Per-syllable color = per-INSTANCE glyph color** (the corrected overclaim #4). The active line is
  **PaintDirty every frame** and re-records its single `DrawGlyphRun` so the phase-7 `AnimTrack.DrivenClock`
  (a `ref float` driven by `IPlaybackClock` ms, NOT the frame clock) writes a per-glyph color into the
  `GlyphInstance` data at *batch* time. We deliberately drop "clean-span reuse for the active line" — caching
  applies to *shaping*, not to per-frame instance emission. Re-recording one glyph run/frame is trivially in
  budget (it is one small node).
  - Why not a `BrushHandle` re-bake: it would mint a new content-hashed handle every tick (`gpu-renderer` §9)
    and break the clean-span invariant for the whole line.
  - Why not a gradient-atlas row lerp: the glyph atlas has no per-frame texture-upload path, and the gradient
    atlas is `RGBA16F` for *fills*, not for per-glyph coverage tint.
- **Foreground-over-backdrop:** the lyrics backdrop is a `UseImageBackdrop` (owned by the Backdrop/Effects
  subsystem); this engine only positions glyph runs over it. 3D fan / perspective is out of core 2.5D scope
  (a `PushLayer{Effect=Transform3D}` via the optional effects leaf, not this subsystem).
- **`IPlaybackClock`** is an app-provided seam (a `ref float CurrentMs` source the `AnimTrack.DrivenClock`
  reads). It is the 1Hz-interpolated Wavee playback clock; the engine never assumes frame-clock cadence, so
  syllable timing stays correct under dropped frames.

The `GlyphInstance` already carries a per-glyph `colorRGBA` (`text.md` §5.5 instance layout) — this is the
exact field the playback clock writes. No new opcode (no `DrawLyricsRun`); no Text-seam change.

---

## 10. Zero-alloc, thread-confinement, and failure/edge ledger

### 10.1 Zero-alloc story
| Path | Mechanism |
|---|---|
| Decode requests/results across threads | POD `DecodeRequest`/`UploadRequest` over a bounded `Channel` + lock-free MPSC ring; no boxing |
| CPU pixels | recycled `PixelBufferPool` (as-built `SlabAllocator<StagingBlock>`), pow2 buckets 16 KB–16 MB, 32 MB retained cap; warm rent/return is zero-alloc (bucket hit), over-cap Return drops to GC; the only off-loop alloc is the OS fetch buffer (per-worker AllocScope) |
| `UseImage` deps | `ReadOnlySpan<DepKey>` over `[InlineArray]` `DepDeps` (`dotnet10` §3) — 0 heap ≤4 deps |
| `UseImage` effect state | struct-state via `IPlatformAppLoop.Post<TState>` (no closure box) |
| Phase-13 upload | `CopyBufferToTexture` into a POOLED texture; `CreateTexture`/`ComPtr` root only on cold pool growth → phase 13 is 0-managed-alloc |
| Realization reads | `ref readonly ImageRealization` from the slab; by-value `ImageBinding` return (blittable) |
| Image instances | reuse `QuadInstance` in the existing per-frame arena / `UploadRing` (`gpu-renderer` §3.4/§12) |

### 10.2 Thread-confinement (obeys `hardened-v1-plan.md` §2)
- UI thread is the SOLE writer of `ImageRefTable`, `ResidencyManager` LRU/pin state, hook cells, request-epoch,
  `VideoSurfaceRegistry` decisions. It touches ZERO COM.
- The RENDER thread is the SOLE owner of every bucket-texture/atlas/staging-ring/DComp `ComPtr`, the GPU fence,
  the deferred-delete ring, and `CopyBufferToTexture`. It executes `Admit`, `Place`, `SetVisible`, `Commit`.
- The WORKER pool runs pure decode/fetch jobs over immutable inputs → POD by handle; touches no Scene, no
  RhiTable, no fence; WIC COM objects are worker-thread-local and never cross the seam (only POD + a
  staging-slab index crosses). This satisfies the FGCOM per-thread-COM rule.
- Slot reuse is consume-gated quarantine; split-resource (bucket texture) retire clears BOTH fences
  (`_lastConsumedSeq > freedSeq` AND in-flight submit fence ≥ seq) — §4 Evict.

### 10.3 Failure / edge cases
- **Decode fail / fetch 404:** `UploadRequest.Status≠0` → realization → `Failed`; the recorder keeps drawing
  the placeholder tint (no broken-image glyph by default; the app may opt into a fallback). Logged perf
  counter.
- **Stale late upload (recycle flash):** request-epoch mismatch at `Admit` → drop, return staging block, no
  ContentEpoch bump (§5). The deepest backstop, independent of NodeHandle generation.
- **Self-eviction race:** pin-before-admit (§4) makes a fresh upload ineligible for its own admit-sweep.
- **Pool exhaustion (capacity cliff):** emergency evict-then-acquire under hard budget, else cold
  `CreateTexture` pool growth (one-time hitch, logged). Stated pool budget (§4.1) is the planning floor.
- **Channel saturation under fling:** bounded write drops lowest-priority off-screen requests; `Cancel` on
  recycle bounds the worker queue.
- **Decode→measure→relayout loop:** PREVENTED by binding row height to the fixed bucket, never `NaturalSize`
  (§6). `NaturalSize` is reported only for app-level aspect math that must not feed layout of a virtualized row.
- **Mosaic 3-of-4:** the `MosaicGroup` coalesced job + all-or-nothing channel-drop prevents a permanent
  partial mosaic (§2/§3.5).
- **Video: no surface available / handoff:** `VideoSurfaceRegistry` keeps exactly one live surface; a
  lower-priority owner gets `IsActive=false` and draws its art/poster `ImageHandle`s only. Atomic handoff → no
  black frame.
- **Device-lost (TDR):** bucket-texture pool + atlas pages + staging ring are render-thread `ComPtr`s recreated
  from POD descs (`gpu-renderer` §17); realizations flip to `Evicted` (ContentEpoch bump) → re-request on next
  paint; the managed `ImageRefTable`/cache survives (handles preserved).
- **DComp Commit/Present-stall:** video keeps presenting on the compositor thread independent of our loop
  (the §8 win); our only cost is a hole-punch clear + a `Place` poke.

---

## 11. Build order — single-thread-correct first, then flip (per `hardened-v1-plan.md` §6)

**Ships single-thread (quarantine 0; UI thread produces decode requests, drains the result ring, AND uploads):**
1. `ImageRefTable` + `DrawImageCmd` indirection + the `ImageRef` clean-span/batcher arm + the RHI
   `CopyBufferToTexture` + texture-staging ring + per-bucket pool. **This is the prerequisite for every screen**
   (no album art, no backdrop, no video poster without it). Decode is already off-thread on workers; only the
   *upload drain* runs on the UI thread at this stage.
2. `DecodeScheduler` + `ResidencyManager` + `UseImage`/`UseMosaic` + request-epoch recycle + the small-image
   atlas. Frame-START eviction, pin-before-admit, two-lane byte-budgeted upload.
3. Video present-tree + `IVideoPresenter` + `DrawVideoCmd` + `VideoSurfaceRegistry`. v1-safe precisely because
   the heavy continuous work is off our thread by construction — single-thread is an *advantage* here.
4. `LyricsLayoutEngine` (per-instance color on the playback clock) — one tiny re-recorded node/frame.

**Flips behind the green race gate (the producer/consumer split moves these lines onto the RENDER thread):**
- The result-ring DRAIN + `CopyBufferToTexture` upload move from the UI thread to the render thread's
  phase-13 drain (the §2 topology). The `ResidencyManager` GPU-free executes there behind the retire-fence
  handshake. `Place`/`SetVisible`/`Commit` move to render-thread phase 11.
- Nothing in `ImageRefTable`/hook/cache bookkeeping moves — it stays UI-thread-confined. The only thing that
  flips is *who owns the ComPtrs and runs the GPU copy*, which is exactly the seam the §2 design draws. Off-thread
  decode is already v1; off-thread *upload* during a UI stall is the v2 categorical fix for the cold-open hitch.

---

## 12. Cross-platform (macOS) boundary

| Concern | Portable (`FluentGpu.Media`) | Windows leaf | macOS leaf |
|---|---|---|---|
| `ImageHandle`/`ImageRealization`/`DrawImageCmd`/`DrawVideoCmd` | **100% portable** | — | — |
| `DecodeScheduler`/`ResidencyManager`/`UseImage`/`UseMosaic`/`VideoSurfaceRegistry`/`LyricsLayoutEngine` | **100% portable** | — | — |
| Decode (`IImageCodec`) | seam | `FluentGpu.Windows` Wic/ (WIC `IWIC*`, `[GeneratedComInterface]`) | `Media.Codecs.CG` (`CGImageSource` constrained decode) |
| Texture upload (`CopyBufferToTexture` + staging ring + pool) | RHI interface | `FluentGpu.Windows` D3D12/ (`UpdateTileMappings`-free placed upload) | `Rhi.Metal` (`MTLBlitCommandEncoder.copyFromBuffer:toTexture:`) |
| Video present (`IVideoPresenter`) | PAL interface | `FluentGpu.Windows` Pal/ (DirectComposition child visual) | `Pal.Mac` (`AVPlayerLayer` under `CAMetalLayer`) |
| Palette feed | trigger only (extraction in `FluentGpu.Theme`) | — | — |

~all of this subsystem's LOC is portable C# over POD + the RHI/PAL/Text *interfaces*; only the codec, the
texture-upload mechanics, and the video-presenter visual are leaf concerns, each behind a seam the architecture
already draws.

---

## Changed vs the original synthesis

These are the amendments this doc folds in (consistent with `app-requirements-waveemusic.md` §1 corrected
overclaims and §5 checklist), stated explicitly:

1. **`DrawImageCmd` is `ImageHandle`-indirected, not raw `TextureHandle`** — and the clean-span rule is
   AMENDED so validity requires `ImageRef` `ContentEpoch` unchanged **and** the baked-geometry hash unchanged
   (folding `hardened-v1-plan.md` §4.6's "validator must capture baked geometry" fix). New clean-span-validator
   + batcher `ImageRef` arm, NOT a "verbatim reuse."
2. **Texture upload is a NEW RHI primitive** (`ICommandEncoder.CopyBufferToTexture`) + a **dedicated
   texture-staging ring** + a **startup per-bucket texture pool with a stated byte budget** — explicitly NOT a
   ride on the instance `UploadRing`. `CreateTexture` is cold-path pool growth only, so phase 13 is honestly
   0-managed-alloc.
3. **Phase-13 upload is two-lane byte-budgeted** (thumbs vs large art/bakes), replacing the unjustified flat
   "4 textures/frame."
4. **Request-epoch (in the `UseImage` hook cell) survives slot recycle** and is the stale-callback backstop —
   explicitly NOT relying on `NodeHandle` generation (which bumps only on free, not recycle-to-new-key).
5. **Pin authority is at RECORD (phase 8)** — the node that paints pins, resolving the
   phase-4-request-vs-phase-8-pin ambiguity. **Eviction at frame START (phase 1); pin-before-admit** fixes the
   self-eviction race in the contract.
6. **Mosaic = Grid of 4 `UseImage` sharing ONE `MosaicGroup` residency unit** (one pin/priority/cancel,
   coalesced decode, all-or-nothing drop) — no `DrawMosaicCmd`, no permanent 3-of-4 mosaic.
7. **Palette is fed the CPU staging block (or a 16×16 downsample), never a GPU `ReadbackImage`** — and palette
   extraction is owned by `FluentGpu.Theme`, not here (this doc owns only the trigger + staging-block lifetime).
8. **Video is a present-tree redesign**, NOT a fold onto §5.1: `IVideoPresenter` + multi-visual DComp tree +
   premul-0 hole-punch + `DrawVideoCmd` with its own `PassClass=VideoHole` below all chrome; PiP-drag hole +
   visual committed in **one** phase-11 DComp `Commit` (two-clock-tear fix); re-punch on overlapping damage.
9. **Per-syllable lyric color is per-INSTANCE glyph color on the playback clock** (active line PaintDirty +
   re-recorded each frame), NOT a `BrushHandle` re-bake (breaks clean-span) nor a gradient-atlas lerp (no
   per-frame glyph-atlas upload path). No `DrawLyricsRun` opcode.
10. **Small-image atlas (`gpu-renderer` `OQ-4`) is promoted to v1 required** (the only thing that hits "shelf
    row = 1–2 draws").
11. **Constrained-to-bucket WIC decode** (decode-to-target via the scaler) so a full-res cover never
    materializes in CPU memory; WIC bound via `[GeneratedComInterface]` (cold/warm COM ruling) and confined to
    the worker thread.
12. **Build order is single-thread-first**: decode is off-thread in v1, but the upload-drain/`CopyBufferToTexture`/
    GPU-free run on the UI thread at quarantine 0 and only flip to the render thread behind the green race gate
    — the only lines that move are the ComPtr-owning ones, exactly the §2 seam.
