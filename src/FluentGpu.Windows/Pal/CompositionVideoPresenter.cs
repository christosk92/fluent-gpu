using System;
using System.Collections.Generic;
using System.Numerics;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Rhi.D3D12;
using TerraFX.Interop.Windows;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.WinRT.WinRT;

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
/// <para>Per slot: <c>Node</c> (a container at the placed rect's whole-px origin, the rect's size, SOFT border mode so the arcs
/// anti-alias) clipped by one <c>RectangleClip</c> (the viewport within the rect, with the four corner radii: the DComp presenter's
/// rounded <c>IDCompositionRectangleClip</c>) → an opaque black backing sprite under the video sprite (both the placed rect, HARD
/// edges). One clip, because nested rounded <c>CompositionGeometricClip</c>s do not intersect: the composed result keeps the arc of
/// only one of them (measured: a (12,12,0,0) slot built as a top-left and a top-right clip showed the top-left arc alone, in either
/// nesting order), while nested <c>RectangleClip</c>s do. A hidden slot is removed from the root collection; Z order among slots is
/// (Z, slot) ascending.</para>
/// <para>Frosted plates (<see cref="SetBackdrops"/>): per slot up to <see cref="MaxPlates"/>, each a SOFT container clipped by its own
/// <c>RectangleClip</c> (the plate's rounded rect) holding one sprite over the plate grown by the blur's reach, inserted into the slot's
/// collection directly above the video sprite, so a plate is slot-local and inherits the slot's offset, viewport clip and corners. The
/// sprite's brush is a <c>CompositionEffectBrush</c> from one factory per presenter (<see cref="CompositionVideoEffects"/>): a
/// <c>CompositionBackdropBrush</c> (what is already composed beneath the sprite: the video and its backing) through a Gaussian blur
/// (border HARD) and the acrylic's luminosity step (a LinearTransfer); sigma and the step are the brush's animatable properties, so a
/// fading plate writes scalars, never a brush. The plate's opacity is the sprite's.</para>
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
    private const int MaxPlates = 8;      // the engine's VideoHoleBackdrops.Max: the plates one hole can list
    private const float MaxBlurSigma = 250f;   // D2D's Gaussian blur ceiling

    private struct Slot
    {
        public bool InUse;
        public IVisual* Node;                      // the slot's container in the root collection (owned)
        public IRectangleClip* Clip;               // Node's clip: the viewport within the rect, rounded (owned)
        public IVisual* Backing;                   // opaque black sprite under the video (owned)
        public IVisual* Video;                     // the picture's sprite (owned)
        public IVisualCollection* Kids;            // Node's children: backing, video, plates (owned)
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
        public float X, Y, W, H;
        public RoundedBox Held;                    // what Clip holds now
        public RectF AppliedRect, AppliedViewport;   // the geometry ApplyPlacement last wrote from: a differing one is a geometry write
        public CornerRadius4 AppliedRadii;
    }

    // A RectangleClip's state (a fresh clip's defaults are all 0): its edges in its visual's px and its four corner radii.
    private struct RoundedBox
    {
        public float L, T, R, B;
        public Vector2 TL, TR, BR, BL;
    }

    private struct Plate
    {
        public IVisual* Holder;                  // the SOFT container the plate's rounded clip sits on, in the slot's Kids (owned)
        public IRectangleClip* Clip;             // Holder's clip: the plate's rounded rect, slot-local (owned)
        public RoundedBox Held;                  // what Clip holds now
        public IVisual* Sprite;                  // the blurred backdrop over the plate grown by the reach, in Holder (owned)
        public ICompositionPropertySet* Props;   // its effect brush's animatable properties (owned)
        public bool InTree;                      // Holder is in the slot's Kids
        // What the sprite and the brush hold now: a fresh sprite's defaults, NaN for the brush's scalars (the first apply writes them).
        public Vector2 Offset, Size;
        public float Opacity, Sigma, Slope, Lift;
    }

    private static bool s_overlayNoted;

    private readonly D3D12Device _device;
    private readonly D3D12Swapchain _target;
    private readonly Slot[] _slots = new Slot[MaxSurfaces];
    private readonly Plate[] _plates = new Plate[MaxSurfaces * MaxPlates];
    private readonly VideoBackdropPx[] _want = new VideoBackdropPx[MaxSurfaces * MaxPlates];
    private readonly int[] _wantCount = new int[MaxSurfaces];
    private readonly bool[] _platesDirty = new bool[MaxSurfaces];
    private ICompositor2* _comp2;                     // CreateBackdropBrush (owned, with the factory)
    private ICompositionEffectFactory* _plateFactory; // the plates' effect graph, created with the first plate (owned)
    private bool _noPlates;                           // the factory or a plate's brush was refused: logged once, plates stay off
    private bool _anyDirty;
    private bool _graphDirty;      // a tree edit or a content change outside ApplyPlacement owes the controller a Commit
    private bool _attachPending;   // a visible slot is not in the tree yet (the window's UI target is not bound): ApplyPending retries

    // This window's video tree (created on first use; owned).
    private IDesktopWindowTarget* _windowTarget;
    private IContainerVisual* _root;
    private IVisualCollection* _rootKids;
    private ICompositor7* _comp7;   // CreateRectangleClip
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
        ICompositor7* c7 = null;
        ICompositionColorBrush* black = null;
        ICompositionBrush* blackBrush = null;
        bool ok = Ok(((IUnknown*)Comp)->QueryInterface(__uuidof<ICompositor7>(), (void**)&c7), "QI ICompositor7")
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
            if (c7 != null) c7->Release();
            return false;
        }
        _windowTarget = wt; _root = root; _rootKids = kids; _comp7 = c7; _black = blackBrush;
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
        // Node's clip is the slot's whole clip (viewport and corners); the sprites below it are HARD, so the video's own edge stays on
        // the whole-px rect.
        bool ok = Container("CreateContainerVisual(slot)", out s.Node, out s.Kids)
            && RoundedClip(s.Node, out s.Clip)
            && Sprite("CreateSpriteVisual(backing)", _black, out s.Backing) && Ok(s.Kids->InsertAtTop(s.Backing), "InsertAtTop(backing)");
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
                    && Ok(s.Kids->InsertAtTop(s.Video), "InsertAtTop(video)");
                if (brush != null) brush->Release();
            }
        }
        // The frosted plates (SetBackdrops) go into Kids directly above the video sprite, so they share the slot's offset, viewport
        // clip and rounded corners.
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

    // A new RectangleClip as `visual`'s clip, SOFT border mode so its arcs anti-alias (cold). The clip starts empty (all edges 0): the
    // first apply writes it.
    private bool RoundedClip(IVisual* visual, out IRectangleClip* clip)
    {
        clip = null;
        IRectangleClip* rc;
        if (!Ok(_comp7->CreateRectangleClip(&rc), "CreateRectangleClip")) return false;
        ICompositionClip* c = null;
        bool ok = Ok(((IUnknown*)rc)->QueryInterface(__uuidof<ICompositionClip>(), (void**)&c), "QI ICompositionClip(rectangle)")
            && Ok(visual->put_Clip(c), "put_Clip(rectangle)")
            && Ok(visual->put_BorderMode(CompositionBorderMode.CompositionBorderMode_Soft), "put_BorderMode(Soft)");
        if (c != null) c->Release();
        if (!ok) { rc->Release(); return false; }
        clip = rc;
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

    public void SetBackdrops(VideoSurfaceId id, ReadOnlySpan<VideoBackdropPx> backdrops)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        if (_noPlates) return;
        int idx = (int)id.Value - 1, n = Math.Min(backdrops.Length, MaxPlates);
        bool same = n == _wantCount[idx];
        for (int k = 0; same && k < n; k++) same = _want[idx * MaxPlates + k] == backdrops[k];
        if (same) return;
        if (n != _wantCount[idx]) Diag.Line($"[video.surface] [video.wuc] plates id={id.Value} n={n}");   // cold: a plate comes or goes, never per move
        for (int k = 0; k < n; k++) _want[idx * MaxPlates + k] = backdrops[k];
        _wantCount[idx] = n;
        _platesDirty[idx] = true;
        s.Dirty = true; _anyDirty = true;
    }

    // Place every wanted plate of slot idx (slot-local, so a plate that moved with its slot writes nothing) and take the rest out of
    // the tree. Runs inside ApplyPlacement, after the slot's clip is current. True when anything was written.
    private bool ApplyPlates(int idx)
    {
        _platesDirty[idx] = false;
        ref Slot s = ref _slots[idx];
        int want = _wantCount[idx];
        bool wrote = false;
        // The slot's visible box (viewport within the placed rect), slot-local: a sprite never reaches past it, where the backdrop holds
        // no picture.
        float vl = s.Held.L, vt = s.Held.T, vr = s.Held.R, vb = s.Held.B;
        for (int k = 0; k < MaxPlates; k++)
        {
            int pi = idx * MaxPlates + k;
            ref Plate p = ref _plates[pi];
            if (k >= want)
            {
                if (p.InTree) { Ok(s.Kids->Remove(p.Holder), "Kids Remove(plate)"); p.InTree = false; wrote = true; }
                continue;
            }
            if (p.Sprite == null && !BuildPlate(pi))
            {
                PlatesOff();
                return true;
            }
            VideoBackdropPx b = _want[pi];
            float l = b.Rect.X - s.Rect.X, t = b.Rect.Y - s.Rect.Y, w = MathF.Max(0f, b.Rect.W), h = MathF.Max(0f, b.Rect.H);
            wrote |= ApplyClip(p.Clip, ref p.Held, l, t, l + w, t + h, b.Radii);
            // The blur's input: the plate's neighbourhood (3 sigma reaches 99.7 % of the kernel), never another plate (the DComp
            // presenter's rule; here it also keeps one plate's backdrop free of another's frost), clamped to the visible box. The
            // brush's HARD border clamps at the sprite's edge, so a plate with no reach reads the same at its own border.
            float sigma = Math.Clamp(b.Sigma, 0f, MaxBlurSigma);
            float reach = PlateReach(idx, k, want, 3f * sigma);
            float x0 = Math.Clamp(l - reach, vl, vr), y0 = Math.Clamp(t - reach, vt, vb);
            float x1 = Math.Clamp(l + w + reach, x0, vr), y1 = Math.Clamp(t + h + reach, y0, vb);
            var offset = new Vector2(x0, y0);
            var size = new Vector2(x1 - x0, y1 - y0);
            if (p.Offset != offset) { Ok(p.Sprite->put_Offset(new Vector3(offset, 0f)), "plate put_Offset"); p.Offset = offset; wrote = true; }
            if (p.Size != size) { Ok(p.Sprite->put_Size(size), "plate put_Size"); p.Size = size; wrote = true; }
            float opacity = Math.Clamp(b.Opacity, 0f, 1f);
            if (p.Opacity != opacity) { Ok(p.Sprite->put_Opacity(opacity), "plate put_Opacity"); p.Opacity = opacity; wrote = true; }
            if (p.Sigma != sigma && SetScalar(p.Props, CompositionVideoEffects.BlurAmount, sigma)) { p.Sigma = sigma; wrote = true; }
            LuminosityStep(b.Tint, b.Luminosity, out float slope, out float lift);
            if (p.Slope != slope)
            {
                bool ok = true;
                for (int c = 0; c < 3; c++) ok &= SetScalar(p.Props, CompositionVideoEffects.LumSlopes[c], slope);
                if (ok) p.Slope = slope;
                wrote = true;
            }
            if (p.Lift != lift)
            {
                bool ok = true;
                for (int c = 0; c < 3; c++) ok &= SetScalar(p.Props, CompositionVideoEffects.LumOffsets[c], lift);
                if (ok) p.Lift = lift;
                wrote = true;
            }
            if (!p.InTree && Ok(s.Kids->InsertAbove(p.Holder, s.Video), "Kids InsertAbove(plate, video)"))
            {
                p.InTree = true;
                wrote = true;
            }
        }
        return wrote;
    }

    // How far plate k's blur input may reach past its rect without touching another wanted plate of the slot (at most `max`).
    private float PlateReach(int idx, int k, int want, float max)
    {
        RectF a = _want[idx * MaxPlates + k].Rect;
        float reach = max;
        for (int j = 0; j < want; j++)
        {
            if (j == k) continue;
            RectF o = _want[idx * MaxPlates + j].Rect;
            float gx = MathF.Max(o.X - (a.X + a.W), a.X - (o.X + o.W));   // separation along x (negative: the projections overlap)
            float gy = MathF.Max(o.Y - (a.Y + a.H), a.Y - (o.Y + o.H));
            float room = MathF.Max(gx, gy) - 2f;                          // the inflated rect stays 2 px clear of the other plate
            reach = MathF.Min(reach, MathF.Max(0f, room));
        }
        return reach;
    }

    // The acrylic recipe's LUMINOSITY step (the DComp presenter's LuminosityMatrix, as the LinearTransfer's slope and offset): scaling
    // RGB toward black (a dark tint) or toward white (a light one) moves lightness while keeping hue and saturation, by the factor that
    // takes a typical video's lightness (LumRef) to the tint's: out = in * slope + lift. Pure, allocation-free.
    private const float LumRef = 0.45f;

    private static void LuminosityStep(ColorF tint, float luminosity, out float slope, out float lift)
    {
        float l = Math.Clamp(luminosity, 0f, 1f);
        float lt = Math.Clamp(0.2126f * tint.R + 0.7152f * tint.G + 0.0722f * tint.B, 0f, 1f);
        if (lt <= LumRef) { slope = 1f - l * (1f - lt / LumRef); lift = 0f; }
        else { slope = 1f - l * (1f - (1f - lt) / (1f - LumRef)); lift = 1f - slope; }
    }

    // One animatable scalar of a plate's brush, named through a stack HSTRING reference (no allocation).
    private bool SetScalar(ICompositionPropertySet* props, string name, float value)
    {
        fixed (char* p = name)
        {
            HSTRING_HEADER header;
            HSTRING hs;
            return Ok(WindowsCreateStringReference(p, (uint)name.Length, &header, &hs), "WindowsCreateStringReference(plate property)")
                && Ok(props->InsertScalar(hs, value), "plate InsertScalar");
        }
    }

    // The plates' effect factory (cold: once per presenter). False when the compositor refused it (logged once; plates stay off).
    private bool EnsurePlateFactory()
    {
        if (_plateFactory != null) return true;
        if (_noPlates) return false;
        ICompositor2* c2 = null;
        ICompositionEffectFactory* factory = null;
        CompositionEffectFactoryLoadStatus status = CompositionEffectFactoryLoadStatus.CompositionEffectFactoryLoadStatus_Other;
        bool ok = Ok(((IUnknown*)Comp)->QueryInterface(__uuidof<ICompositor2>(), (void**)&c2), "QI ICompositor2")
            && Ok(CompositionVideoEffects.CreatePlateFactory(Comp, &factory, &status), "CreateEffectFactoryWithProperties(plate)");
        if (ok && status is not (CompositionEffectFactoryLoadStatus.CompositionEffectFactoryLoadStatus_Success
                or CompositionEffectFactoryLoadStatus.CompositionEffectFactoryLoadStatus_Pending))
        {
            Diag.Line($"[video.surface] [video.wuc] plate effect factory load status {status}");
            ok = false;
        }
        if (!ok)
        {
            if (factory != null) factory->Release();
            if (c2 != null) c2->Release();
            return false;
        }
        _comp2 = c2;
        _plateFactory = factory;
        Diag.Line($"[video.surface] [video.wuc] plate effect factory created (backdrop -> GaussianBlur HARD -> LinearTransfer, status {status})");
        return true;
    }

    // Build plate pi (cold: once per plate for its slot's life): its clipped holder with a sprite whose brush is a new effect brush over
    // a backdrop brush. False leaves nothing behind.
    private bool BuildPlate(int pi)
    {
        if (!EnsurePlateFactory()) return false;
        ICompositionEffectBrush* eb = null;
        ICompositionBackdropBrush* bb = null;
        ICompositionBrush* backdrop = null, brush = null;
        ICompositionObject* obj = null;
        ICompositionPropertySet* props = null;
        IVisual* sprite = null, holder = null;
        IRectangleClip* clip = null;
        IVisualCollection* inner = null;
        bool ok = Ok(_plateFactory->CreateBrush(&eb), "plate CreateBrush")
            && Ok(_comp2->CreateBackdropBrush(&bb), "CreateBackdropBrush")
            && Ok(((IUnknown*)bb)->QueryInterface(__uuidof<ICompositionBrush>(), (void**)&backdrop), "QI ICompositionBrush(backdrop)")
            && SetSource(eb, backdrop)
            && Ok(((IUnknown*)eb)->QueryInterface(__uuidof<ICompositionObject>(), (void**)&obj), "QI ICompositionObject(plate brush)")
            && Ok(obj->get_Properties(&props), "plate brush get_Properties")
            && Ok(((IUnknown*)eb)->QueryInterface(__uuidof<ICompositionBrush>(), (void**)&brush), "QI ICompositionBrush(plate)")
            && Sprite("CreateSpriteVisual(plate)", brush, out sprite)
            && Container("CreateContainerVisual(plate)", out holder, out inner)
            && RoundedClip(holder, out clip)
            && Ok(inner->InsertAtTop(sprite), "InsertAtTop(plate sprite)");
        if (inner != null) inner->Release();
        if (brush != null) brush->Release();
        if (obj != null) obj->Release();
        if (backdrop != null) backdrop->Release();
        if (bb != null) bb->Release();
        if (eb != null) eb->Release();
        if (!ok)
        {
            if (clip != null) clip->Release();
            if (holder != null) holder->Release();
            if (sprite != null) sprite->Release();
            if (props != null) props->Release();
            return false;
        }
        _plates[pi] = new Plate { Holder = holder, Clip = clip, Sprite = sprite, Props = props, Opacity = 1f, Sigma = float.NaN, Slope = float.NaN, Lift = float.NaN };
        return true;
    }

    private bool SetSource(ICompositionEffectBrush* eb, ICompositionBrush* source)
    {
        string name = CompositionVideoEffects.BackdropSource;
        fixed (char* p = name)
        {
            HSTRING_HEADER header;
            HSTRING hs;
            return Ok(WindowsCreateStringReference(p, (uint)name.Length, &header, &hs), "WindowsCreateStringReference(plate source)")
                && Ok(eb->SetSourceParameter(hs, source), "plate SetSourceParameter(backdrop)");
        }
    }

    // A plate could not be built: no plates anywhere from now on (the veil then lies over the sharp video), logged once.
    private void PlatesOff()
    {
        _noPlates = true;
        Diag.Line("[video.surface] [video.wuc] frosted plates over video are off (the veil lies over the sharp picture)");
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse) continue;
            for (int k = 0; k < MaxPlates; k++)
            {
                int pi = i * MaxPlates + k;
                if (!_plates[pi].InTree) continue;
                Ok(s.Kids->Remove(_plates[pi].Holder), "Kids Remove(plate)");
                _plates[pi].InTree = false;
            }
            _wantCount[i] = 0;
            _platesDirty[i] = false;
        }
    }

    private void ReleasePlates(int idx)
    {
        for (int k = 0; k < MaxPlates; k++)
        {
            int pi = idx * MaxPlates + k;
            ref Plate p = ref _plates[pi];
            if (p.Clip != null) p.Clip->Release();
            if (p.Holder != null) p.Holder->Release();
            if (p.Sprite != null) p.Sprite->Release();
            if (p.Props != null) p.Props->Release();
            p = default;
        }
        _wantCount[idx] = 0;
        _platesDirty[idx] = false;
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
                && (g.Rect != g.AppliedRect || g.Viewport != g.AppliedViewport || g.Radii != g.AppliedRadii || _platesDirty[i]);
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

        // The viewport, clamped into the placed rect (the content only exists inside it), rounded, in Node-local device px (the
        // containers carry no transform), so device-px radii apply directly. A box of four zero radii is the plain rect clip.
        RectF viewport = s.Viewport.W > 0f && s.Viewport.H > 0f ? s.Viewport : s.Rect;
        float l = Math.Clamp(viewport.X - s.Rect.X, 0f, w);
        float t = Math.Clamp(viewport.Y - s.Rect.Y, 0f, h);
        float r = Math.Clamp(viewport.X + viewport.W - s.Rect.X, l, w);
        float b = Math.Clamp(viewport.Y + viewport.H - s.Rect.Y, t, h);
        wrote |= ApplyClip(s.Clip, ref s.Held, l, t, r, b, s.Radii);
        // The plates after the slot's own clip: their sprites are clamped to it.
        if (_wantCount[idx] > 0 || _platesDirty[idx]) wrote |= ApplyPlates(idx);
        return wrote;
    }

    // A rounded clip (l, t, r, b) with each radius clamped to half the box's extent per axis (the DComp presenter's rule), writing only
    // what differs from `held`. True when anything was written.
    private bool ApplyClip(IRectangleClip* rc, ref RoundedBox held, float l, float t, float r, float b, CornerRadius4 rad)
    {
        float hw = (r - l) * 0.5f, hh = (b - t) * 0.5f;
        Vector2 tl = new(MathF.Min(rad.TopLeft, hw), MathF.Min(rad.TopLeft, hh));
        Vector2 tr = new(MathF.Min(rad.TopRight, hw), MathF.Min(rad.TopRight, hh));
        Vector2 br = new(MathF.Min(rad.BottomRight, hw), MathF.Min(rad.BottomRight, hh));
        Vector2 bl = new(MathF.Min(rad.BottomLeft, hw), MathF.Min(rad.BottomLeft, hh));
        bool wrote = false;
        if (held.L != l) { Ok(rc->put_Left(l), "clip put_Left"); held.L = l; wrote = true; }
        if (held.T != t) { Ok(rc->put_Top(t), "clip put_Top"); held.T = t; wrote = true; }
        if (held.R != r) { Ok(rc->put_Right(r), "clip put_Right"); held.R = r; wrote = true; }
        if (held.B != b) { Ok(rc->put_Bottom(b), "clip put_Bottom"); held.B = b; wrote = true; }
        if (held.TL != tl) { Ok(rc->put_TopLeftRadius(tl), "clip put_TopLeftRadius"); held.TL = tl; wrote = true; }
        if (held.TR != tr) { Ok(rc->put_TopRightRadius(tr), "clip put_TopRightRadius"); held.TR = tr; wrote = true; }
        if (held.BR != br) { Ok(rc->put_BottomRightRadius(br), "clip put_BottomRightRadius"); held.BR = br; wrote = true; }
        if (held.BL != bl) { Ok(rc->put_BottomLeftRadius(bl), "clip put_BottomLeftRadius"); held.BL = bl; wrote = true; }
        return wrote;
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
        ReleasePlates(idx);
        if (s.Kids != null) s.Kids->Release();
        if (s.Surface != null) s.Surface->Release();
        if (s.Brush != null) s.Brush->Release();
        if (s.Video != null) s.Video->Release();
        if (s.Backing != null) s.Backing->Release();
        if (s.Clip != null) s.Clip->Release();
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
        if (_plateFactory != null) { _plateFactory->Release(); _plateFactory = null; }
        if (_comp2 != null) { _comp2->Release(); _comp2 = null; }
        if (_comp7 != null) { _comp7->Release(); _comp7 = null; }
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
