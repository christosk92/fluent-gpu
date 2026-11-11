using System;
using System.Collections.Generic;
using System.Numerics;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Rhi.D3D12;
using TerraFX.Interop.Windows;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The Windows <see cref="IVideoPresenter"/> on <c>Windows.UI.Composition</c> (<c>--fg video-wuc</c>, <see cref="EngineSwitches.VideoWuc"/>).
/// Each slot's picture is a SpriteVisual whose <c>CompositionSurfaceBrush</c> (Stretch = Fill, linear) wraps the swap-chain handle
/// (<c>ICompositorInterop::CreateCompositionSurfaceForHandle</c>): the compositor stretches whatever buffer the swap chain holds on the
/// frame it composes to the sprite's size, so no content size is told to anyone and a stream-size change can never be drawn at a
/// believed scale (<see cref="SetContentSize"/> is deliberately the interface's no-op).
/// <para>A WUC visual cannot be parented under a DComp visual, so the tree lives on the device's own video compositor
/// (<see cref="D3D12Device.EnsureVideoCompositor"/>, a CompositorController committed by the render thread) in a second, NON-topmost
/// <c>DesktopWindowTarget</c> on this window's HWND: beneath the DComp target that holds the UI swap chain, revealed through the UI's
/// premultiplied-0 hole exactly like the DComp presenter's underlay. One presenter per composited swap chain (the primary window, a
/// pop-out, the mini player), each with its own target and root container.</para>
/// <para>Per slot: <c>Node</c> (a container at the placed rect's whole-px origin, the rect's size, the viewport as an
/// <c>InsetClip</c>) → four nested corner containers (the rounded clip: one <c>CompositionGeometricClip</c> on the first when the four
/// radii are equal, else one per rounded corner, each a uniform-radius rounded rect extended past the two edges that do not touch its
/// corner, so their intersection is the per-corner shape; SOFT border mode, so the arcs anti-alias) → an opaque black backing sprite
/// under the video sprite (both the placed rect, HARD edges). A hidden slot is removed from the root collection; Z order among slots is
/// (Z, slot) ascending.</para>
/// <para>Render-thread confined like the DComp presenter. Place / SetViewport / SetCornerRadii / SetVisible only record;
/// <see cref="ApplyPending"/> first lets the device pump the compositor's queue (only on a turn that moves no picture), then writes
/// only what changed (a pure move writes one Offset) and
/// notes that the controller owes a Commit, which the host makes once per turn right after the Present that carries the hole
/// (<see cref="D3D12Device.CommitVideoComposition"/>). Fixed slot table, no steady-state managed allocation, and nothing throws into the
/// commit path: every native call goes through <see cref="Ok"/>. Its cold lines read <c>[video.surface] [video.wuc] ...</c>: the
/// <c>[video.surface]</c> head is the one an app's log gate already lets through (Wavee's Info file), so a field log carries them.</para>
/// </summary>
public sealed unsafe class CompositionVideoPresenter : IVideoPresenter, IDisposable
{
    private const int MaxSurfaces = 16;   // preallocated slots ⇒ zero per-frame managed alloc
    private const int Corners = 4;        // the nested corner containers: 0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left

    private struct Slot
    {
        public bool InUse;
        public IVisual* Node;                      // the slot's container in the root collection (owned)
        public IInsetClip* Inset;                  // the viewport clip on Node (owned)
        public IVisual* Backing;                   // opaque black sprite under the video (owned)
        public IVisual* Video;                     // the picture's sprite (owned)
        public ICompositionSurfaceBrush* Brush;    // Stretch = Fill over Surface (owned)
        public ICompositionSurface* Surface;       // the wrapped swap-chain handle (owned; null = no content)
        public RectF Rect;
        public RectF Viewport;
        public CornerRadius4 Radii;
        public int Z;
        public bool Visible;
        public bool InTree;
        public bool Restack;                       // Z changed while in the tree: ApplyPending re-inserts it
        public bool Dirty;
        // What the visuals hold now (WUC defaults are all 0, like a fresh slot): ApplyPlacement writes only what differs.
        public float X, Y, W, H, InL, InT, InR, InB;
        public RectF AppliedRect, AppliedViewport;   // the geometry ApplyPlacement last wrote from: a differing one is a geometry write
        public CornerRadius4 AppliedRadii;
    }

    private struct Corner
    {
        public IVisual* Visual;                            // the nested container (owned)
        public ICompositionRoundedRectangleGeometry* Geo;  // created with Clip on first use (owned)
        public ICompositionClip* Clip;                     // the geometric clip over Geo (owned)
        public bool On;                                    // Clip is Visual's clip now
        public Vector2 Offset, Size, Radius;               // what Geo holds now
    }

    private static bool s_overlayNoted;

    private readonly D3D12Device _device;
    private readonly D3D12Swapchain _target;
    private readonly Slot[] _slots = new Slot[MaxSurfaces];
    private readonly Corner[] _corners = new Corner[MaxSurfaces * Corners];
    private bool _anyDirty;
    private bool _graphDirty;      // a tree edit or a content change outside ApplyPlacement owes the controller a Commit
    private bool _attachPending;   // a visible slot is not in the tree yet (the window's UI target is not bound): ApplyPending retries

    // This window's video tree (created on first use; owned).
    private IDesktopWindowTarget* _windowTarget;
    private IContainerVisual* _root;
    private IVisualCollection* _rootKids;
    private ICompositor5* _comp5;   // CreateRoundedRectangleGeometry
    private ICompositor6* _comp6;   // CreateGeometricClipWithGeometry
    private ICompositionBrush* _black;   // the backing sprites' shared opaque black brush
    private bool _rootFailed;      // the window's target could not be built: logged once, not retried

    // Fault-logging dedup for Ok(): per presenter, keyed on the raw HRESULT (the DComp presenter's rule).
    private readonly HashSet<uint> _loggedHrs = new();

    public CompositionVideoPresenter(D3D12Device device, D3D12Swapchain target)
    {
        _device = device;
        _target = target;
        if (EngineSwitches.VideoOverlay && !s_overlayNoted)
        {
            s_overlayNoted = true;
            Diag.Line("[video.surface] [video.wuc] --fg video-overlay is not supported by the composition presenter (the video tree sits under the UI's target)");
        }
    }

    private ICompositor* Comp => _device.VideoCompositor;

    // The window's non-topmost target and root container, built once. False when the compositor or the target was refused.
    private bool EnsureRoot()
    {
        if (_rootKids != null) return true;
        if (_rootFailed || !_device.EnsureVideoCompositor()) return false;
        _rootFailed = true;   // every early return below is a failure; cleared at the end
        IDesktopWindowTarget* wt = null;
        ICompositionTarget* ct = null;
        IContainerVisual* root = null;
        IVisual* rootVisual = null;
        IVisualCollection* kids = null;
        ICompositor5* c5 = null;
        ICompositor6* c6 = null;
        ICompositionColorBrush* black = null;
        ICompositionBrush* blackBrush = null;
        bool ok = Ok(((IUnknown*)Comp)->QueryInterface(__uuidof<ICompositor5>(), (void**)&c5), "QI ICompositor5")
            && Ok(((IUnknown*)Comp)->QueryInterface(__uuidof<ICompositor6>(), (void**)&c6), "QI ICompositor6")
            && Ok(Comp->CreateColorBrushWithColor(new Color { A = 255, R = 0, G = 0, B = 0 }, &black), "CreateColorBrushWithColor(backing)")
            && Ok(((IUnknown*)black)->QueryInterface(__uuidof<ICompositionBrush>(), (void**)&blackBrush), "QI ICompositionBrush(backing)")
            && Ok(_device.VideoCompositorDesktop->CreateDesktopWindowTarget(_target.Hwnd, BOOL.FALSE, &wt), "CreateDesktopWindowTarget(non-topmost)")
            && Ok(((IUnknown*)wt)->QueryInterface(__uuidof<ICompositionTarget>(), (void**)&ct), "QI ICompositionTarget")
            && Ok(Comp->CreateContainerVisual(&root), "CreateContainerVisual(root)")
            && Ok(((IUnknown*)root)->QueryInterface(__uuidof<IVisual>(), (void**)&rootVisual), "QI IVisual(root)")
            && Ok(ct->put_Root(rootVisual), "target put_Root")
            && Ok(root->get_Children(&kids), "root get_Children");
        if (rootVisual != null) rootVisual->Release();
        if (ct != null) ct->Release();
        if (black != null) black->Release();
        if (!ok)
        {
            Diag.Line($"[video.surface] [video.wuc] window target failed hwnd=0x{(nint)_target.Hwnd:X} (video surfaces cannot be created for this window)");
            if (kids != null) kids->Release();
            if (root != null) root->Release();
            if (wt != null) { CloseTarget(wt); wt->Release(); }
            if (blackBrush != null) blackBrush->Release();
            if (c6 != null) c6->Release();
            if (c5 != null) c5->Release();
            return false;
        }
        _windowTarget = wt; _root = root; _rootKids = kids; _comp5 = c5; _comp6 = c6; _black = blackBrush;
        _rootFailed = false;
        Diag.Line($"[video.surface] [video.wuc] window target created hwnd=0x{(nint)_target.Hwnd:X} (non-topmost, under the UI's DComp target)");
        return true;
    }

    // True once the window's UI target is bound (its first Present): a slot attached before that would show with no UI over it.
    private bool UiBound => _target is { DcompRoot: not null, DcompVisual: not null };

    public bool CanAttachSurfaces => UiBound && EnsureRoot();

    public VideoSurfaceId CreateSurface()
    {
        _device.AssertRenderThread();
        int idx = -1;
        for (int i = 0; i < MaxSurfaces; i++)
            if (!_slots[i].InUse) { idx = i; break; }
        if (idx < 0) throw new InvalidOperationException($"CompositionVideoPresenter: out of surface slots (max {MaxSurfaces}).");
        if (!EnsureRoot() || !BuildSlot(idx))
            return default;   // none id: VideoSurfaceRegistry.Drain keeps the slot dirty and retries CreateSurface on the next drain
        _anyDirty = true;     // attached (at its Z) by the next ApplyPending
        return new VideoSurfaceId((uint)(idx + 1));   // id 0 == none
    }

    // Build one slot's visuals (cold). False leaves nothing behind.
    private bool BuildSlot(int idx)
    {
        ref Slot s = ref _slots[idx];
        s = default;
        IVisualCollection* kids = null;
        IInsetClip* inset = null;
        ICompositionClip* clip = null;
        bool ok = Container("CreateContainerVisual(slot)", out s.Node, out kids)
            && Ok(Comp->CreateInsetClip(&inset), "CreateInsetClip")
            && Ok(((IUnknown*)inset)->QueryInterface(__uuidof<ICompositionClip>(), (void**)&clip), "QI ICompositionClip(inset)")
            && Ok(s.Node->put_Clip(clip), "slot put_Clip(inset)")
            && Ok(s.Node->put_BorderMode(CompositionBorderMode.CompositionBorderMode_Hard), "slot put_BorderMode(Hard)");
        if (clip != null) clip->Release();
        s.Inset = inset;
        // The corner chain: each container clips one corner (or, with equal radii, the first clips all four). SOFT anti-aliases the
        // arcs; the sprites below are HARD, so the video's own edge stays on the whole-px rect.
        for (int k = 0; ok && k < Corners; k++)
        {
            ref Corner c = ref _corners[idx * Corners + k];
            c = default;
            IVisualCollection* inner;
            ok = Container("CreateContainerVisual(corner)", out c.Visual, out inner)
                && Ok(c.Visual->put_BorderMode(CompositionBorderMode.CompositionBorderMode_Soft), "corner put_BorderMode(Soft)")
                && Ok(kids->InsertAtTop(c.Visual), "InsertAtTop(corner)");
            kids->Release();
            kids = inner;
        }
        ok = ok && Sprite("CreateSpriteVisual(backing)", _black, out s.Backing) && Ok(kids->InsertAtTop(s.Backing), "InsertAtTop(backing)");
        if (ok)
        {
            ICompositionSurfaceBrush* sb;
            ICompositionBrush* brush = null;
            ok = Ok(Comp->CreateSurfaceBrush(&sb), "CreateSurfaceBrush");
            if (ok)
            {
                s.Brush = sb;
                ok = Ok(sb->put_Stretch(CompositionStretch.CompositionStretch_Fill), "brush put_Stretch(Fill)")
                    && Ok(sb->put_BitmapInterpolationMode(CompositionBitmapInterpolationMode.CompositionBitmapInterpolationMode_Linear), "brush put_BitmapInterpolationMode(Linear)")
                    && Ok(((IUnknown*)sb)->QueryInterface(__uuidof<ICompositionBrush>(), (void**)&brush), "QI ICompositionBrush(video)")
                    && Sprite("CreateSpriteVisual(video)", brush, out s.Video)
                    && Ok(kids->InsertAtTop(s.Video), "InsertAtTop(video)");
                if (brush != null) brush->Release();
            }
        }
        // The frosted plates (SetBackdrops, not built yet) go into this innermost collection directly above the video sprite, so they
        // share the slot's offset, viewport clip and rounded corners.
        if (kids != null) kids->Release();
        if (!ok)
        {
            ReleaseSlot(idx);
            return false;
        }
        s.InUse = true;
        s.Visible = true;
        return true;
    }

    private bool Container(string what, out IVisual* visual, out IVisualCollection* kids)
    {
        visual = null;
        kids = null;
        IContainerVisual* cv;
        if (!Ok(Comp->CreateContainerVisual(&cv), what)) return false;
        IVisual* v = null;
        IVisualCollection* k = null;
        bool ok = Ok(((IUnknown*)cv)->QueryInterface(__uuidof<IVisual>(), (void**)&v), "QI IVisual(container)");
        if (ok && !Ok(cv->get_Children(&k), "container get_Children")) { v->Release(); ok = false; }
        cv->Release();
        if (!ok) return false;
        visual = v;
        kids = k;
        return true;
    }

    private bool Sprite(string what, ICompositionBrush* brush, out IVisual* visual)
    {
        visual = null;
        ISpriteVisual* sv;
        if (!Ok(Comp->CreateSpriteVisual(&sv), what)) return false;
        IVisual* v = null;
        bool ok = Ok(sv->put_Brush(brush), "sprite put_Brush")
            && Ok(((IUnknown*)sv)->QueryInterface(__uuidof<IVisual>(), (void**)&v), "QI IVisual(sprite)");
        sv->Release();
        if (!ok) return false;
        if (!Ok(v->put_BorderMode(CompositionBorderMode.CompositionBorderMode_Hard), "sprite put_BorderMode(Hard)")) { v->Release(); return false; }
        visual = v;
        return true;
    }

    public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        // The single DRM attach point. True only when the handle is the brush's surface; false drops the previous content (a stale
        // frame of the last source must never stay up under a new one) and the registry retries on a later drain.
        ICompositionSurface* surface;
        if (!Ok(_device.VideoCompositorInterop->CreateCompositionSurfaceForHandle((HANDLE)(nint)dcompSurfaceHandle, &surface), "CreateCompositionSurfaceForHandle"))
        {
            DropContent(ref s);
            return false;
        }
        if (!Ok(s.Brush->put_Surface(surface), "brush put_Surface"))
        {
            surface->Release();
            DropContent(ref s);
            return false;
        }
        if (s.Surface != null) s.Surface->Release();
        s.Surface = surface;
        _graphDirty = true;
        Diag.Line($"[video.surface] [video.wuc] bound handle 0x{dcompSurfaceHandle:X} id={id.Value} hwnd=0x{(nint)_target.Hwnd:X}");
        return true;
    }

    private void DropContent(ref Slot s)
    {
        Ok(s.Brush->put_Surface(null), "brush put_Surface(null)");
        if (s.Surface != null) { s.Surface->Release(); s.Surface = null; }
        _graphDirty = true;
    }

    public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        if (s.Z != z && s.InTree) s.Restack = true;   // a place in the root collection is fixed at insertion: a Z change re-inserts
        s.Rect = deviceRect; s.Z = z;                 // opacity is retained metadata the DComp presenter ignores too (UI-side reveal)
        s.Dirty = true; _anyDirty = true;
    }

    public void SetViewport(VideoSurfaceId id, RectF deviceRect)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        if (s.Viewport == deviceRect) return;
        s.Viewport = deviceRect;
        s.Dirty = true; _anyDirty = true;
    }

    public void SetVisible(VideoSurfaceId id, bool visible)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        if (s.Visible == visible) return;
        // A real hide: the slot leaves the root collection (ApplyPending); show re-inserts it at its Z with its content still bound.
        s.Visible = visible;
        s.Dirty = true; _anyDirty = true;
    }

    public void SetCornerRadii(VideoSurfaceId id, CornerRadius4 radiiPx)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        var r = new CornerRadius4(MathF.Max(0f, radiiPx.TopLeft), MathF.Max(0f, radiiPx.TopRight),
            MathF.Max(0f, radiiPx.BottomRight), MathF.Max(0f, radiiPx.BottomLeft));
        if (s.Radii == r) return;
        s.Radii = r;
        s.Dirty = true; _anyDirty = true;
    }

    public void Destroy(VideoSurfaceId id)
    {
        _device.AssertRenderThread();
        int idx = (int)id.Value - 1;
        ref Slot s = ref Get(id);
        Detach(ref s);
        ReleaseSlot(idx);
        _graphDirty = true;
    }

    public void ApplyPending()
    {
        _device.AssertRenderThread();
        // First, before any write of this turn's apply, and only on a turn that moves no picture (see the method).
        bool geometryPending = false;
        for (int i = 0; i < MaxSurfaces && !geometryPending; i++)
        {
            ref Slot g = ref _slots[i];
            geometryPending = g.InUse && g.Dirty
                && (g.Rect != g.AppliedRect || g.Viewport != g.AppliedViewport || g.Radii != g.AppliedRadii);
        }
        _device.PumpVideoCompositorQueue(geometryPending);
        if (!_anyDirty && !_graphDirty && !_attachPending) return;
        bool wrote = _graphDirty, wroteGeometry = false;
        bool canAttach = UiBound && _rootKids != null;
        _attachPending = false;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse) continue;
            if (s.Dirty)
            {
                s.Dirty = false;
                if (ApplyPlacement(i)) wrote = wroteGeometry = true;
            }
            if (s.InTree && (!s.Visible || s.Restack)) { Detach(ref s); wrote = true; }
            s.Restack = false;
            if (!s.InTree && s.Visible)
            {
                if (canAttach && Attach(i)) wrote = true;
                else _attachPending = true;
            }
        }
        _anyDirty = false;
        _graphDirty = false;
        _device.NoteVideoCompositorApply(wroteGeometry);
        if (wrote) _device.NoteVideoCompositorCommitDue();   // the host's one CommitVideoComposition per turn flushes it
    }

    public void Commit()
    {
        ApplyPending();
        _device.CommitVideoComposition();
    }

    // Insert the slot into the root collection in (Z, slot) order: below the in-tree sibling with the next-higher key, else on top.
    private bool Attach(int idx)
    {
        ref Slot s = ref _slots[idx];
        int best = -1;
        for (int j = 0; j < MaxSurfaces; j++)
        {
            if (j == idx) continue;
            ref Slot o = ref _slots[j];
            if (!o.InUse || !o.InTree) continue;
            if (o.Z < s.Z || (o.Z == s.Z && j < idx)) continue;   // below us in the stack
            if (best < 0 || o.Z < _slots[best].Z || (o.Z == _slots[best].Z && j < best)) best = j;
        }
        bool ok = best >= 0
            ? Ok(_rootKids->InsertBelow(s.Node, _slots[best].Node), "root InsertBelow(slot)")
            : Ok(_rootKids->InsertAtTop(s.Node), "root InsertAtTop(slot)");
        s.InTree = ok;
        return ok;
    }

    private void Detach(ref Slot s)
    {
        if (!s.InTree) return;
        Ok(_rootKids->Remove(s.Node), "root Remove(slot)");
        s.InTree = false;
    }

    // Write what differs from what the visuals hold. True when anything was written.
    private bool ApplyPlacement(int idx)
    {
        ref Slot s = ref _slots[idx];
        s.AppliedRect = s.Rect; s.AppliedViewport = s.Viewport; s.AppliedRadii = s.Radii;
        bool wrote = false;
        // The registry hands the rect and viewport snapped to whole device px, so Node's offset and clip sit on the pixel grid the
        // UI hole's erase rect uses.
        float w = MathF.Max(0f, s.Rect.W), h = MathF.Max(0f, s.Rect.H);
        if (s.X != s.Rect.X || s.Y != s.Rect.Y)
        {
            Ok(s.Node->put_Offset(new Vector3(s.Rect.X, s.Rect.Y, 0f)), "slot put_Offset");
            s.X = s.Rect.X; s.Y = s.Rect.Y;
            wrote = true;
        }
        if (s.W != w || s.H != h)
        {
            // Node's size is what its inset clip is measured from; the sprites fill the placed rect (the brush stretches the
            // swap chain's current buffer to it).
            var size = new Vector2(w, h);
            Ok(s.Node->put_Size(size), "slot put_Size");
            Ok(s.Backing->put_Size(size), "backing put_Size");
            Ok(s.Video->put_Size(size), "video put_Size");
            s.W = w; s.H = h;
            wrote = true;
        }

        // The viewport, clamped into the placed rect (the content only exists inside it), as insets from Node's edges.
        RectF viewport = s.Viewport.W > 0f && s.Viewport.H > 0f ? s.Viewport : s.Rect;
        float l = Math.Clamp(viewport.X - s.Rect.X, 0f, w);
        float t = Math.Clamp(viewport.Y - s.Rect.Y, 0f, h);
        float r = Math.Clamp(viewport.X + viewport.W - s.Rect.X, l, w);
        float b = Math.Clamp(viewport.Y + viewport.H - s.Rect.Y, t, h);
        if (s.InL != l) { Ok(s.Inset->put_LeftInset(l), "inset put_LeftInset"); s.InL = l; wrote = true; }
        if (s.InT != t) { Ok(s.Inset->put_TopInset(t), "inset put_TopInset"); s.InT = t; wrote = true; }
        if (s.InR != w - r) { Ok(s.Inset->put_RightInset(w - r), "inset put_RightInset"); s.InR = w - r; wrote = true; }
        if (s.InB != h - b) { Ok(s.Inset->put_BottomInset(h - b), "inset put_BottomInset"); s.InB = h - b; wrote = true; }

        // The rounded clip, in Node-local device px (the containers carry no transform), so device-px radii apply directly. Each
        // radius is clamped to half the clip's extent per axis (the DComp presenter's rule); all zero = no geometric clip at all.
        float cw = r - l, ch = b - t, hw = cw * 0.5f, hh = ch * 0.5f;
        CornerRadius4 rad = s.Radii;
        Vector2 tl = new(MathF.Min(rad.TopLeft, hw), MathF.Min(rad.TopLeft, hh));
        Vector2 tr = new(MathF.Min(rad.TopRight, hw), MathF.Min(rad.TopRight, hh));
        Vector2 br = new(MathF.Min(rad.BottomRight, hw), MathF.Min(rad.BottomRight, hh));
        Vector2 bl = new(MathF.Min(rad.BottomLeft, hw), MathF.Min(rad.BottomLeft, hh));
        int c0 = idx * Corners;
        if (rad.TopLeft <= 0f && rad.TopRight <= 0f && rad.BottomRight <= 0f && rad.BottomLeft <= 0f)
        {
            for (int k = 0; k < Corners; k++) wrote |= SetCorner(ref _corners[c0 + k], false, default, default, default);
        }
        else if (tl == tr && tl == br && tl == bl)
        {
            // One radius: the first container clips all four corners with the clip rect itself.
            wrote |= SetCorner(ref _corners[c0], true, new Vector2(l, t), new Vector2(cw, ch), tl);
            for (int k = 1; k < Corners; k++) wrote |= SetCorner(ref _corners[c0 + k], false, default, default, default);
        }
        else
        {
            // Per corner: a rounded rect of that corner's radius at that corner, twice the clip's extent, so its other three corners
            // (whose arcs reach at most half the extent inward) lie beyond the clip's far edges. The nested clips intersect.
            Vector2 size = new(2f * cw, 2f * ch);
            wrote |= SetCorner(ref _corners[c0 + 0], rad.TopLeft > 0f, new Vector2(l, t), size, tl);
            wrote |= SetCorner(ref _corners[c0 + 1], rad.TopRight > 0f, new Vector2(l - cw, t), size, tr);
            wrote |= SetCorner(ref _corners[c0 + 2], rad.BottomRight > 0f, new Vector2(l - cw, t - ch), size, br);
            wrote |= SetCorner(ref _corners[c0 + 3], rad.BottomLeft > 0f, new Vector2(l, t - ch), size, bl);
        }
        return wrote;
    }

    // One corner container's clip: on with this geometry, or off. True when anything was written.
    private bool SetCorner(ref Corner c, bool on, Vector2 offset, Vector2 size, Vector2 radius)
    {
        if (!on)
        {
            if (!c.On) return false;
            Ok(c.Visual->put_Clip(null), "corner put_Clip(null)");
            c.On = false;
            return true;
        }
        if (c.Clip == null && !BuildCornerClip(ref c)) return false;
        bool wrote = false;
        if (c.Offset != offset) { Ok(c.Geo->put_Offset(offset), "corner geometry put_Offset"); c.Offset = offset; wrote = true; }
        if (c.Size != size) { Ok(c.Geo->put_Size(size), "corner geometry put_Size"); c.Size = size; wrote = true; }
        if (c.Radius != radius) { Ok(c.Geo->put_CornerRadius(radius), "corner geometry put_CornerRadius"); c.Radius = radius; wrote = true; }
        if (!c.On && Ok(c.Visual->put_Clip(c.Clip), "corner put_Clip(rounded)")) { c.On = true; wrote = true; }
        return wrote;
    }

    // The rounded-rectangle geometry and its geometric clip for one corner container (cold: once per container).
    private bool BuildCornerClip(ref Corner c)
    {
        ICompositionRoundedRectangleGeometry* geo;
        if (!Ok(_comp5->CreateRoundedRectangleGeometry(&geo), "CreateRoundedRectangleGeometry")) return false;
        ICompositionGeometry* g = null;
        ICompositionGeometricClip* gc = null;
        ICompositionClip* clip = null;
        bool ok = Ok(((IUnknown*)geo)->QueryInterface(__uuidof<ICompositionGeometry>(), (void**)&g), "QI ICompositionGeometry")
            && Ok(_comp6->CreateGeometricClipWithGeometry(g, &gc), "CreateGeometricClipWithGeometry")
            && Ok(((IUnknown*)gc)->QueryInterface(__uuidof<ICompositionClip>(), (void**)&clip), "QI ICompositionClip(geometric)");
        if (gc != null) gc->Release();
        if (g != null) g->Release();
        if (!ok) { geo->Release(); return false; }
        c.Geo = geo;
        c.Clip = clip;
        c.Offset = default; c.Size = default; c.Radius = default;   // a new geometry's defaults
        return true;
    }

    private ref Slot Get(VideoSurfaceId id)
    {
        uint v = id.Value;
        if (v == 0 || v > MaxSurfaces || !_slots[v - 1].InUse)
            throw new ArgumentException($"CompositionVideoPresenter: no live surface for id {v}.");
        return ref _slots[v - 1];
    }

    // Release every object of slot idx (it must be out of the tree) and reset it.
    private void ReleaseSlot(int idx)
    {
        ref Slot s = ref _slots[idx];
        for (int k = 0; k < Corners; k++)
        {
            ref Corner c = ref _corners[idx * Corners + k];
            if (c.Clip != null) c.Clip->Release();
            if (c.Geo != null) c.Geo->Release();
            if (c.Visual != null) c.Visual->Release();
            c = default;
        }
        if (s.Surface != null) s.Surface->Release();
        if (s.Brush != null) s.Brush->Release();
        if (s.Video != null) s.Video->Release();
        if (s.Backing != null) s.Backing->Release();
        if (s.Inset != null) s.Inset->Release();
        if (s.Node != null) s.Node->Release();
        s = default;
    }

    private static void CloseTarget(IDesktopWindowTarget* wt)
    {
        // Close the target now rather than at the last release, so a presenter rebuilt for the same window (a device recovery)
        // can create the window's non-topmost target again.
        IClosable* closable;
        if ((int)((IUnknown*)wt)->QueryInterface(__uuidof<IClosable>(), (void**)&closable) < 0) return;
        closable->Close();
        closable->Release();
    }

    public void Dispose()
    {
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse) continue;
            Detach(ref s);
            ReleaseSlot(i);
        }
        _anyDirty = false;
        _graphDirty = false;
        _attachPending = false;
        if (_rootKids != null) { _rootKids->Release(); _rootKids = null; }
        if (_root != null) { _root->Release(); _root = null; }
        if (_windowTarget != null)
        {
            CloseTarget(_windowTarget);
            _windowTarget->Release();
            _windowTarget = null;
            _device.NoteVideoCompositorCommitDue();   // the window's video tree is gone: the next commit shows it
        }
        if (_black != null) { _black->Release(); _black = null; }
        if (_comp6 != null) { _comp6->Release(); _comp6 = null; }
        if (_comp5 != null) { _comp5->Release(); _comp5 = null; }
    }

    /// <summary>Non-throwing HRESULT check (the DComp presenter's rule): nothing here throws into the render thread's commit path.
    /// Logs the first occurrence of each distinct HRESULT and returns false so the caller degrades in place.</summary>
    private bool Ok(HRESULT hr, string what)
    {
        if ((int)hr >= 0) return true;
        if (_loggedHrs.Add((uint)hr))
            Diag.Line($"[video.surface] [video.wuc] {what} failed: 0x{(uint)hr:X8}");
        return false;
    }
}
