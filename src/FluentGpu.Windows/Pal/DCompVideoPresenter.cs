using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Rhi.D3D12;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The Windows <see cref="IVideoPresenter"/> — the DRM-free video-compositing spine (M0,
/// <c>docs/plans/video-compositing-spine-design.md §4</c>). Manages video child visuals under the primary swapchain's
/// DirectComposition ROOT, strictly z-BELOW the UI swapchain visual, using the SAME <c>IDCompositionDevice</c> as
/// <see cref="D3D12Device"/> (one device, one Commit). <c>BindSurfaceHandle</c> wraps an external DComp surface handle
/// with <c>IDCompositionDevice::CreateSurfaceFromHandle</c> → <c>IDCompositionVisual::SetContent</c>.
/// <para>Each slot is Chromium's clip-visual / transform-visual split: an UNTRANSFORMED device-space parent
/// carries the whole-pixel offset, the viewport clip and the rounded-corner clip (a DComp clip lives in the visual's
/// pre-transform space, so on the scaled visual it would be scaled with the content), and a child under it carries the
/// scale transform, the LINEAR resampling and the content. Under the child, inside the same clip parent, sits an opaque
/// BLACK backing visual (the device's one shared solid-black content scaled to the placed rect), so wherever the video does
/// not paint an opaque pixel (no first frame yet, a swapchain rebuild, a size mismatch) the hole shows black, never
/// Mica or the desktop. Z order is structural (siblings are inserted in
/// <c>(Z, slot)</c> order, always below the UI visual) and a hidden slot is REMOVED from the tree, not clipped to empty.</para>
/// Every ComPtr here
/// is render-thread-sole-owned (<c>AssertRenderThread</c> on every method); mutations queue, are applied by
/// <see cref="ApplyPending"/> and flushed by ONE device commit per render turn (phase 11 — the "two-clock tear" lock with the
/// UI hole's Present; <see cref="D3D12Device.CommitVideoComposition"/> after the parent and every pop-out applied theirs).
/// </summary>
public sealed unsafe class DCompVideoPresenter : IVideoPresenter, IDisposable
{
    private const int MaxSurfaces = 16;   // preallocated slots ⇒ zero per-frame managed alloc

    private struct Slot
    {
        public bool InUse;
        public IDCompositionVisual* Parent;  // the untransformed device-space offset + clip visual (owned); the slot's tree node
        public IDCompositionVisual* Child;   // the video child visual under Parent: scale transform + content (owned)
        public IDCompositionVisual* Backing; // opaque black backing under Child (owned; null if the shared content could not be created)
        public IUnknown* Content;            // the surface wrapped from the external handle (owned)
        public IDCompositionRectangleClip* RoundClip;   // owned; created lazily, only while Radius > 0
        public RectF Rect;
        public RectF Viewport;
        public uint ContentW, ContentH;      // the content's native pixel size (decoder swapchain) — scale source (0 = unknown → 1:1)
        public float Opacity;
        public float Radius;                 // device-px corner radius (0 = square → the plain rect clip)
        public int Z;
        public bool Visible;
        public bool InTree;                  // Parent AddVisual'd under the current root (false while hidden)
        public bool Dirty;                   // Place/SetVisible pending for the next Commit
        public bool PlacementFaulted;        // last ApplyPlacement hit a failing native call and bailed early (diagnostic;
                                              // cleared at the top of the next ApplyPlacement attempt)
    }

    private readonly D3D12Device _device;
    private readonly Slot[] _slots = new Slot[MaxSurfaces];
    private int _dirtyCount;
    private bool _graphDirty;   // AddVisual/RemoveVisual must Commit even when no live slot needs placement
    private bool _attachPending;   // a live child could not be AddVisual'd yet (root not bound / AddVisual failed): Commit retries

    // Fault-logging dedup for Ok(): PER-PRESENTER (not per-slot) — a shared native call (Commit, AttachChild's
    // AddVisual, CreateSurface's CreateVisual) has no slot to key on, and coalescing failures across slots by hr alone
    // is the conservative (quieter) choice for what would otherwise be a per-Commit-retry log storm. Keyed on the raw
    // HRESULT only (not the "what" string), so a first occurrence of a given hr logs once and any later occurrence —
    // same call site or a different one — stays silent until the presenter is recreated (device-lost rebuild).
    private readonly HashSet<uint> _loggedHrs = new();

    // The swapchain whose DirectComposition root hosts THIS presenter's video children. The primary window's presenter
    // targets the primary swapchain; a detached/secondary video window gets its OWN presenter targeting ITS swapchain
    // (see D3D12Device.GetVideoPresenter(ISwapchain)). Every presenter shares the device's one IDCompositionDevice, so
    // one IDCompositionDevice::Commit flushes all windows' trees: ApplyPending only notes that the device owes a commit
    // (D3D12Device.NoteVideoCommitDue) and the host makes it once per render turn (F080); Commit() = ApplyPending + that flush.
    private readonly D3D12Swapchain _target;

    public DCompVideoPresenter(D3D12Device device, D3D12Swapchain target)
    {
        _device = device;
        _target = target;
    }

    private IDCompositionDevice* Dcomp => _device.DcompDevice;
    private D3D12Swapchain Target => _target;

    public VideoSurfaceId CreateSurface()
    {
        _device.AssertRenderThread();
        int idx = -1;
        for (int i = 0; i < MaxSurfaces; i++)
            if (!_slots[i].InUse) { idx = i; break; }
        if (idx < 0) throw new InvalidOperationException($"DCompVideoPresenter: out of surface slots (max {MaxSurfaces}).");

        IDCompositionVisual* parent;
        if (!Ok(Dcomp->CreateVisual(&parent), "CreateVisual(video clip parent)"))
            return default;   // none id (Value 0) — VideoSurfaceRegistry.Drain sees SurfaceId still IsNone, keeps the
                               // slot dirty, and retries CreateSurface on the next drain (VideoSurfaceRegistry.cs)
        IDCompositionVisual* child;
        if (!Ok(Dcomp->CreateVisual(&child), "CreateVisual(video child)"))
        {
            parent->Release();
            return default;
        }
        if (!Ok(parent->AddVisual(child, BOOL.FALSE, null), "Parent.AddVisual(video child)"))
        {
            child->Release();
            parent->Release();
            return default;
        }
        // The resampling quality is the child's: the decoder swapchain is almost always UPSCALED to the placed rect, and
        // the DComp default is nearest-neighbour (blocky). HARD keeps the content's own edge from bleeding a soft
        // fractional-coverage fringe past the whole-pixel rect; the PARENT keeps SOFT so a rounded clip anti-aliases its
        // corners. Quality-only properties: a failure is logged once and the surface still works.
        Ok(child->SetBitmapInterpolationMode(DCOMPOSITION_BITMAP_INTERPOLATION_MODE.DCOMPOSITION_BITMAP_INTERPOLATION_MODE_LINEAR), "video child SetBitmapInterpolationMode(LINEAR)");
        Ok(child->SetBorderMode(DCOMPOSITION_BORDER_MODE.DCOMPOSITION_BORDER_MODE_HARD), "video child SetBorderMode(HARD)");
        Ok(parent->SetBorderMode(DCOMPOSITION_BORDER_MODE.DCOMPOSITION_BORDER_MODE_SOFT), "video parent SetBorderMode(SOFT)");
        _slots[idx] = new Slot { InUse = true, Parent = parent, Child = child, Backing = CreateBacking(parent, child), Opacity = 1f, Visible = true };
        AttachChild(idx);   // insert z-BELOW the UI visual under the current root
        return new VideoSurfaceId((uint)(idx + 1));   // id 0 == none
    }

    // The opaque black backing visual: the device's shared solid-black content on its own visual, inserted directly
    // BEHIND the video child (insertAbove=FALSE, reference=child; a NULL reference would put it on TOP) so the video stays
    // above it and both share the parent's clip and hide/show with it. Best effort: null (the slot works, just without a backing) when the shared content or the visual
    // cannot be created; ApplyPlacement retries. HARD border mode keeps its scaled edge from blending into a translucent fringe.
    private IDCompositionVisual* CreateBacking(IDCompositionVisual* parent, IDCompositionVisual* child)
    {
        IUnknown* content = _device.EnsureVideoBacking();
        if (content == null) return null;
        IDCompositionVisual* backing;
        if (!Ok(Dcomp->CreateVisual(&backing), "CreateVisual(video backing)")) return null;
        if (!Ok(backing->SetContent(content), "video backing SetContent")
            || !Ok(parent->AddVisual(backing, BOOL.FALSE, child), "Parent.AddVisual(video backing)"))
        {
            backing->Release();
            return null;
        }
        Ok(backing->SetBorderMode(DCOMPOSITION_BORDER_MODE.DCOMPOSITION_BORDER_MODE_HARD), "video backing SetBorderMode(HARD)");
        return backing;
    }

    public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        IUnknown* surface;
        // The single DRM attach point (DRM-free here): wrap the external shareable surface handle and bind it as content.
        // Non-throwing, and honest about the outcome: false means the handle is NOT the slot's content, and the previous
        // content has been dropped (a stale frame of the last source or session must never stay up under a new one).
        // VideoSurfaceRegistry records the handle as bound only on true and retries a false on a later drain with backoff.
        if (!Ok(Dcomp->CreateSurfaceFromHandle((HANDLE)(nint)dcompSurfaceHandle, &surface), "CreateSurfaceFromHandle"))
        {
            DropContent(ref s);
            return false;
        }
        if (!Ok(s.Child->SetContent(surface), "video child SetContent"))
        {
            surface->Release();
            DropContent(ref s);
            return false;
        }
        if (s.Content != null) s.Content->Release();
        s.Content = surface;
        if (!s.Dirty) { s.Dirty = true; _dirtyCount++; }
        return true;
    }

    // Detach whatever the visual shows and release the wrapped surface; the slot is marked dirty so the next Commit
    // flushes the now-empty visual.
    private void DropContent(ref Slot s)
    {
        Ok(s.Child->SetContent((IUnknown*)null), "video child SetContent(null)");
        if (s.Content != null) { s.Content->Release(); s.Content = null; }
        if (!s.Dirty) { s.Dirty = true; _dirtyCount++; }
    }

    public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        bool zChanged = s.Z != z;
        s.Rect = deviceRect; s.Opacity = opacity; s.Z = z;
        if (zChanged && s.InTree)
        {
            // A visual's place in the root's stack is fixed at AddVisual, so a Z change re-inserts it (queued for this
            // frame's Commit like every other DComp edit).
            DetachChild(ref s);
            AttachChild((int)id.Value - 1);
            _graphDirty = true;
        }
        if (!s.Dirty) { s.Dirty = true; _dirtyCount++; }
    }

    public void SetViewport(VideoSurfaceId id, RectF deviceRect)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        if (s.Viewport == deviceRect) return;
        s.Viewport = deviceRect;
        if (!s.Dirty) { s.Dirty = true; _dirtyCount++; }
    }

    public void SetVisible(VideoSurfaceId id, bool visible)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        if (s.Visible == visible) return;
        s.Visible = visible;
        // A REAL hide: the parent leaves the composition tree (no clipped-to-empty visual left for DWM to evaluate, and
        // the decoder swapchain is no longer referenced from the tree). Show re-inserts it at its Z; the content stays
        // bound on the detached visual, so the frame is there the moment it returns.
        if (visible) AttachChild((int)id.Value - 1); else DetachChild(ref s);
        _graphDirty = true;   // a tree edit needs a Commit even when no slot needs placement
        if (!s.Dirty) { s.Dirty = true; _dirtyCount++; }
    }

    public void SetCornerRadius(VideoSurfaceId id, float radiusPx)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        float r = radiusPx > 0f ? radiusPx : 0f;
        if (s.Radius == r) return;
        s.Radius = r;
        if (!s.Dirty) { s.Dirty = true; _dirtyCount++; }
    }

    public void SetContentSize(VideoSurfaceId id, uint width, uint height)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        if (s.ContentW == width && s.ContentH == height) return;
        s.ContentW = width; s.ContentH = height;
        if (!s.Dirty) { s.Dirty = true; _dirtyCount++; }
    }

    public void Destroy(VideoSurfaceId id)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        DetachChild(ref s);
        _graphDirty = true;
        if (s.RoundClip != null) { s.RoundClip->Release(); s.RoundClip = null; }
        if (s.Content != null) { s.Content->Release(); s.Content = null; }
        if (s.Backing != null) { s.Backing->Release(); s.Backing = null; }
        if (s.Child != null) { s.Child->Release(); s.Child = null; }
        if (s.Parent != null) { s.Parent->Release(); s.Parent = null; }
        if (s.Dirty) { s.Dirty = false; _dirtyCount--; }
        s.InUse = false;
    }

    /// <summary>True once the target swapchain's DirectComposition root and UI visual exist (its first Present binds them), so
    /// a created surface can be attached at once rather than waiting for a later retry.</summary>
    public bool CanAttachSurfaces => Target is { DcompRoot: not null, DcompVisual: not null };

    public void ApplyPending()
    {
        _device.AssertRenderThread();
        if (_attachPending) RetryAttach();
        // Removing the last video child only mutates the DComp visual graph. There may be no live dirty slot left,
        // but that RemoveVisual still needs a Commit or DWM keeps showing the released child across navigation.
        if (_dirtyCount == 0 && !_graphDirty) return;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse || !s.Dirty) continue;
            ApplyPlacement(ref s);
            s.Dirty = false;
        }
        _dirtyCount = 0;
        _graphDirty = false;
        _device.NoteVideoCommitDue();   // the device-level Commit (Commit() below, or the host's once-per-turn one) flushes them
    }

    public void Commit()
    {
        ApplyPending();
        _device.CommitVideoComposition();   // non-throwing; a failure just leaves DWM showing the prior frame's composition —
                                            // the next dirty placement retries
    }

    // Retry the AddVisual of every live child that is not in the tree yet: a child created before the swapchain's DComp
    // root exists (the first Present binds it lazily) or whose AddVisual failed attaches on the next Commit. There is no
    // cross-device re-parenting any more: a device recovery disposes this presenter (D3D12Device.RecoverDevice) and the
    // registry rebuilds its surfaces on the fresh one, because DComp visuals cannot be moved between IDCompositionDevices.
    private void RetryAttach()
    {
        _attachPending = false;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse || s.InTree || s.Parent == null) continue;   // AttachChild itself skips a hidden slot
            AttachChild(i);
            if (s.InTree) _graphDirty = true;
        }
    }

    private void AttachChild(int idx)
    {
        ref Slot s = ref _slots[idx];
        if (s.InTree || !s.Visible) return;   // a hidden slot stays out of the tree until SetVisible(true)
        if (Target is not { DcompRoot: not null, DcompVisual: not null } sc) { _attachPending = true; return; }
        // z-BELOW the UI visual — AddVisual(parent, insertAbove=FALSE, reference). The video renders BENEATH the UI
        // swapchain and is revealed at its rect through the premultiplied-0 hole-punch the UI back buffer draws there
        // (the IVideoPresenter contract), so UI chrome (rounded corners, overlays, transport) composites OVER the video
        // edge. Among video siblings the stack is (Z, slot) ascending: the reference is the in-tree sibling with the
        // next-higher key, else the UI visual itself (so a video can never land above the UI).
        IDCompositionVisual* above = sc.DcompVisual;
        int best = -1;
        for (int j = 0; j < MaxSurfaces; j++)
        {
            if (j == idx) continue;
            ref Slot o = ref _slots[j];
            if (!o.InUse || !o.InTree) continue;
            if (o.Z < s.Z || (o.Z == s.Z && j < idx)) continue;   // below us in the stack
            if (best < 0 || o.Z < _slots[best].Z || (o.Z == _slots[best].Z && j < best)) best = j;
        }
        if (best >= 0) above = _slots[best].Parent;
        if (!Ok(sc.DcompRoot->AddVisual(s.Parent, BOOL.FALSE, above), "Root.AddVisual(video parent, below UI)"))
        {
            _attachPending = true;   // s.InTree stays false; the next Commit retries (RetryAttach)
            return;
        }
        s.InTree = true;
    }

    private void DetachChild(ref Slot s)
    {
        if (!s.InTree || Target is not { DcompRoot: not null } sc) { s.InTree = false; return; }
        sc.DcompRoot->RemoveVisual(s.Parent);
        s.InTree = false;
    }

    private void ApplyPlacement(ref Slot s)
    {
        if (s.Parent == null || s.Child == null) return;
        s.PlacementFaulted = false;
        // Every native call below is Ok()-guarded and bails out (marking PlacementFaulted + returning) at the first
        // failure — no throw into the render thread's Commit path. A bailed-out placement leaves the visuals at their
        // PREVIOUS offset/transform/clip; the slot stays Dirty==false (Commit already cleared it before calling this),
        // so the stale placement persists until the NEXT Place()/SetVisible()/etc. call marks the slot dirty again.
        //
        // The registry hands the rect and viewport already snapped to whole device pixels (rule R), so the parent's offset
        // and clip sit exactly on the pixel grid the UI hole's erase rect uses.
        if (!Ok(s.Parent->SetOffsetX(s.Rect.X), "video parent SetOffsetX")) { s.PlacementFaulted = true; return; }
        if (!Ok(s.Parent->SetOffsetY(s.Rect.Y), "video parent SetOffsetY")) { s.PlacementFaulted = true; return; }

        // Scale the native-resolution content (e.g. a 1920×1080 decoder swapchain) to exactly fill the placed device
        // rect. The rect is already aspect-fit by the caller (MediaPlayerElement.FitVideoRect), so this letterboxes
        // correctly. Without the scale the swapchain composites 1:1 and the bottom-right is cropped (the fit bug).
        // The child sits at the parent's origin, so a pure scale about the origin lands the content's top-left at the
        // rect's top-left. Unknown content size (0) ⇒ identity (the 1:1 fallback, e.g. before natural size resolves).
        bool scaled = s.ContentW > 0 && s.ContentH > 0 && s.Rect.W > 0f && s.Rect.H > 0f;
        float sx = scaled ? s.Rect.W / s.ContentW : 1f;
        float sy = scaled ? s.Rect.H / s.ContentH : 1f;
        D2D_MATRIX_3X2_F m = default;
        m.m11 = sx; m.m22 = sy;
        if (!Ok(s.Child->SetTransform(&m), "video child SetTransform(scale)")) { s.PlacementFaulted = true; return; }

        // The black backing fills the placed rect exactly: the shared VideoBackingSize-px solid content scaled to Rect, under
        // the same parent clip as the video. An empty rect keeps identity (the parent's clip is empty then, as for the child).
        if (s.Backing == null) s.Backing = CreateBacking(s.Parent, s.Child);   // the shared content was not available at CreateSurface
        if (s.Backing != null)
        {
            bool filled = s.Rect.W > 0f && s.Rect.H > 0f;
            D2D_MATRIX_3X2_F bm = default;
            bm.m11 = filled ? s.Rect.W / D3D12Device.VideoBackingSize : 1f;
            bm.m22 = filled ? s.Rect.H / D3D12Device.VideoBackingSize : 1f;
            if (!Ok(s.Backing->SetTransform(&bm), "video backing SetTransform(scale)")) { s.PlacementFaulted = true; return; }
        }

        // The viewport clip lives on the UNTRANSFORMED parent, so it is in parent-local DEVICE px (viewport minus the
        // rect's origin) and the scale never touches it. Clamped into the placed rect: the content only exists inside
        // it, and for an oversized UniformToFill/Native frame the viewport crop is what remains of it. Hidden slots are
        // not in the tree at all (SetVisible detaches), so there is no empty-clip stand-in for "hidden" here.
        RectF viewport = s.Viewport.W > 0f && s.Viewport.H > 0f ? s.Viewport : s.Rect;
        float rw = MathF.Max(0f, s.Rect.W), rh = MathF.Max(0f, s.Rect.H);
        float left = Math.Clamp(viewport.X - s.Rect.X, 0f, rw);
        float top = Math.Clamp(viewport.Y - s.Rect.Y, 0f, rh);
        float right = Math.Clamp(viewport.X + viewport.W - s.Rect.X, left, rw);
        float bottom = Math.Clamp(viewport.Y + viewport.H - s.Rect.Y, top, rh);
        D2D_RECT_F clip = new D2D_RECT_F { left = left, top = top, right = right, bottom = bottom };

        if (s.Radius <= 0f)
        {
            if (s.RoundClip != null) { s.Parent->SetClip((IDCompositionClip*)null); s.RoundClip->Release(); s.RoundClip = null; }
            if (!Ok(s.Parent->SetClip(&clip), "video parent SetClip")) s.PlacementFaulted = true;
            return;
        }

        // Rounded: the clip is in device px on the untransformed parent, so the device-px radius applies directly (no
        // division by the content scale) and a circle stays circular whatever the scale. The parent's SOFT border mode
        // anti-aliases the corner arcs.
        if (s.RoundClip == null)
        {
            IDCompositionRectangleClip* created;   // via a local: s is a ref into the slot array, so &s.RoundClip is unfixed
            if (!Ok(Dcomp->CreateRectangleClip(&created), "CreateRectangleClip(video parent)")) { s.PlacementFaulted = true; return; }
            s.RoundClip = created;
        }
        float rx = MathF.Min(s.Radius, (clip.right - clip.left) * 0.5f);
        float ry = MathF.Min(s.Radius, (clip.bottom - clip.top) * 0.5f);
        var rc = s.RoundClip;
        if (!Ok(rc->SetLeft(clip.left), "round clip SetLeft")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetTop(clip.top), "round clip SetTop")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetRight(clip.right), "round clip SetRight")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetBottom(clip.bottom), "round clip SetBottom")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetTopLeftRadiusX(rx), "round clip TL x")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetTopLeftRadiusY(ry), "round clip TL y")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetTopRightRadiusX(rx), "round clip TR x")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetTopRightRadiusY(ry), "round clip TR y")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetBottomLeftRadiusX(rx), "round clip BL x")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetBottomLeftRadiusY(ry), "round clip BL y")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetBottomRightRadiusX(rx), "round clip BR x")) { s.PlacementFaulted = true; return; }
        if (!Ok(rc->SetBottomRightRadiusY(ry), "round clip BR y")) { s.PlacementFaulted = true; return; }
        if (!Ok(s.Parent->SetClip((IDCompositionClip*)rc), "video parent SetClip(rounded)")) s.PlacementFaulted = true;
    }

    private ref Slot Get(VideoSurfaceId id)
    {
        uint v = id.Value;
        if (v == 0 || v > MaxSurfaces || !_slots[v - 1].InUse)
            throw new ArgumentException($"DCompVideoPresenter: no live surface for id {v}.");
        return ref _slots[v - 1];
    }

    public void Dispose()
    {
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse) continue;
            DetachChild(ref s);
            if (s.RoundClip != null) { s.RoundClip->Release(); s.RoundClip = null; }
            if (s.Content != null) { s.Content->Release(); s.Content = null; }
            if (s.Backing != null) { s.Backing->Release(); s.Backing = null; }
            if (s.Child != null) { s.Child->Release(); s.Child = null; }
            if (s.Parent != null) { s.Parent->Release(); s.Parent = null; }
            s.InUse = false;
        }
        _dirtyCount = 0;
    }

    /// <summary>
    /// Non-throwing replacement for the presenter's former throwing <c>Check</c>: nothing in this class may throw a
    /// failing HRESULT into the render thread's Commit/present path (a device-lost or transient DComp/DXGI failure here
    /// must degrade the video surface, never crash the frame). Returns <c>true</c> on a succeeding HRESULT; on failure,
    /// logs the FIRST occurrence of each distinct <paramref name="hr"/> via <see cref="Diag.Line"/> (always-on —
    /// routes to <see cref="Diag.Sink"/> or stderr, never gated behind an opt-in env var) and stays silent for any
    /// later repeat of the same hr (see <see cref="_loggedHrs"/>), then returns <c>false</c> so the caller can degrade
    /// in place — mark a slot faulted and bail out of placement, return a none <see cref="VideoSurfaceId"/>, or leave
    /// previously-bound content untouched. Every call site decides its own degrade; this helper only decides whether
    /// to throw (never) and whether to log (once per hr).
    /// </summary>
    private bool Ok(HRESULT hr, string what)
    {
        if ((int)hr >= 0) return true;
        if (_loggedHrs.Add((uint)hr))
            Diag.Line($"[video.presenter] {what} failed: 0x{(uint)hr:X8}");
        return false;
    }
}
