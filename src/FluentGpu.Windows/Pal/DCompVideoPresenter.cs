using System;
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
/// with <c>IDCompositionDevice::CreateSurfaceFromHandle</c> → <c>IDCompositionVisual::SetContent</c>. Every ComPtr here
/// is render-thread-sole-owned (<c>AssertRenderThread</c> on every method); mutations queue and flush on one
/// <see cref="Commit"/> per frame (phase 11 — the "two-clock tear" lock with the UI hole's Present).
/// </summary>
public sealed unsafe class DCompVideoPresenter : IVideoPresenter, IDisposable
{
    private const int MaxSurfaces = 16;   // preallocated slots ⇒ zero per-frame managed alloc

    private struct Slot
    {
        public bool InUse;
        public IDCompositionVisual* Child;   // the video child visual (owned)
        public IUnknown* Content;            // the surface wrapped from the external handle (owned)
        public RectF Rect;
        public RectF Viewport;
        public uint ContentW, ContentH;      // the content's native pixel size (decoder swapchain) — scale source (0 = unknown → 1:1)
        public float Opacity;
        public int Z;
        public bool Visible;
        public bool InTree;                  // AddVisual'd under the current root
        public bool Dirty;                   // Place/SetVisible pending for the next Commit
    }

    private readonly D3D12Device _device;
    private readonly Slot[] _slots = new Slot[MaxSurfaces];
    private int _dirtyCount;
    private bool _graphDirty;   // AddVisual/RemoveVisual must Commit even when no live slot needs placement

    // Diagnostic opt-in only: place the video child z-ABOVE the UI (the M3 spike shortcut that skipped the hole-punch).
    // The PRODUCTION path is z-BELOW the UI, revealed through the premultiplied-0 hole (IVideoPresenter contract).
    private static readonly bool s_zAbove = Environment.GetEnvironmentVariable("FG_VIDEO_ZABOVE") == "1";

    // The swapchain whose DirectComposition root hosts THIS presenter's video children. The primary window's presenter
    // targets the primary swapchain; a detached/secondary video window gets its OWN presenter targeting ITS swapchain
    // (see D3D12Device.GetVideoPresenter(ISwapchain)). Every presenter shares the device's one IDCompositionDevice, so
    // one IDCompositionDevice::Commit flushes all windows' trees — a per-presenter Commit is correct, just redundant.
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

        IDCompositionVisual* child;
        Check(Dcomp->CreateVisual(&child), "CreateVisual(video child)");
        _slots[idx] = new Slot { InUse = true, Child = child, Opacity = 1f, Visible = true };
        AttachChild(ref _slots[idx]);   // insert z-BELOW the UI visual under the current root
        return new VideoSurfaceId((uint)(idx + 1));   // id 0 == none
    }

    public void BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        IUnknown* surface;
        // The single DRM attach point (DRM-free here): wrap the external shareable surface handle and bind it as content.
        Check(Dcomp->CreateSurfaceFromHandle((HANDLE)(nint)dcompSurfaceHandle, &surface), "CreateSurfaceFromHandle");
        if (s.Content != null) s.Content->Release();
        s.Content = surface;
        Check(s.Child->SetContent(surface), "video child SetContent");
        s.Dirty = true; _dirtyCount++;
    }

    public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z)
    {
        _device.AssertRenderThread();
        ref Slot s = ref Get(id);
        s.Rect = deviceRect; s.Opacity = opacity; s.Z = z;
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
        s.Visible = visible;
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
        if (s.Content != null) { s.Content->Release(); s.Content = null; }
        if (s.Child != null) { s.Child->Release(); s.Child = null; }
        if (s.Dirty) { s.Dirty = false; _dirtyCount--; }
        s.InUse = false;
    }

    public void Commit()
    {
        _device.AssertRenderThread();
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
        Check(Dcomp->Commit(), "video presenter Commit");
        _graphDirty = false;
    }

    // Re-attach every live child under a freshly-(re)built root (device recover rebinds the DComp graph in BindDComp).
    // Only responds to ITS OWN target's rebind — BindDComp calls the per-swapchain presenter, and this guard makes a
    // stray cross-swapchain call a no-op (a second video window's presenter must not re-parent under another's root).
    internal void OnSwapchainRebound(D3D12Swapchain target)
    {
        if (!ReferenceEquals(target, _target)) return;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse) continue;
            s.InTree = false;
            AttachChild(ref s);
            if (s.Content != null) s.Child->SetContent(s.Content);
            ApplyPlacement(ref s);
        }
        Check(Dcomp->Commit(), "video presenter Commit(rebound)");
    }

    private void AttachChild(ref Slot s)
    {
        if (s.InTree || Target is not { DcompRoot: not null, DcompVisual: not null } sc) return;
        // PRODUCTION (default): z-BELOW the UI visual — AddVisual(child, insertAbove=FALSE, reference=uiVisual). The video
        // child renders BENEATH the UI swapchain and is revealed at its rect through the premultiplied-0 hole-punch the
        // UI back buffer draws there (the IVideoPresenter contract). This lets UI chrome (rounded corners, overlays,
        // transport) composite OVER the video edge — which z-above forgoes.
        //
        // FG_VIDEO_ZABOVE=1 (diagnostic only) restores the M3 spike's z-ABOVE shortcut: the child renders ON TOP of the
        // UI at its clipped rect, so it shows over an OPAQUE page background without a hole-punch — useful for isolating
        // a hole-punch problem from a compositing problem, never the shipping path.
        BOOL above = s_zAbove ? BOOL.TRUE : BOOL.FALSE;
        Check(sc.DcompRoot->AddVisual(s.Child, above, sc.DcompVisual),
            s_zAbove ? "Root.AddVisual(video child, above UI [diagnostic])" : "Root.AddVisual(video child, below UI)");
        s.InTree = true;
    }

    private void DetachChild(ref Slot s)
    {
        if (!s.InTree || Target is not { DcompRoot: not null } sc) { s.InTree = false; return; }
        sc.DcompRoot->RemoveVisual(s.Child);
        s.InTree = false;
    }

    private void ApplyPlacement(ref Slot s)
    {
        if (s.Child == null) return;
        Check(s.Child->SetOffsetX(s.Rect.X), "video child SetOffsetX");
        Check(s.Child->SetOffsetY(s.Rect.Y), "video child SetOffsetY");

        // Scale the native-resolution content (e.g. a 1920×1080 decoder swapchain) to exactly fill the placed device
        // rect. The rect is already aspect-fit by the caller (MediaPlayerElement.FitVideoRect), so this letterboxes
        // correctly. Without the scale the swapchain composites 1:1 and the bottom-right is cropped (the fit bug).
        // DComp applies the offset AFTER the transform, so a pure scale about the origin lands the content's top-left at
        // the rect's top-left. Unknown content size (0) ⇒ identity (the 1:1 fallback, e.g. before natural size resolves).
        bool scaled = s.ContentW > 0 && s.ContentH > 0 && s.Rect.W > 0f && s.Rect.H > 0f;
        float sx = scaled ? s.Rect.W / s.ContentW : 1f;
        float sy = scaled ? s.Rect.H / s.ContentH : 1f;
        D2D_MATRIX_3X2_F m = default;
        m.m11 = sx; m.m22 = sy;
        Check(s.Child->SetTransform(&m), "video child SetTransform(scale)");

        // Belt-and-suspenders clip in the visual's LOCAL (pre-transform) space: the full content when scaling (a no-op
        // guard that maps to the rect), else the device rect. Hidden ⇒ empty rect (the M0 SetVisible semantics).
        RectF viewport = s.Viewport.W > 0f && s.Viewport.H > 0f ? s.Viewport : s.Rect;
        float localW = scaled ? s.ContentW : s.Rect.W;
        float localH = scaled ? s.ContentH : s.Rect.H;
        float left = scaled ? Math.Clamp((viewport.X - s.Rect.X) / sx, 0f, localW) : 0f;
        float top = scaled ? Math.Clamp((viewport.Y - s.Rect.Y) / sy, 0f, localH) : 0f;
        float right = scaled ? Math.Clamp((viewport.X + viewport.W - s.Rect.X) / sx, 0f, localW) : MathF.Min(localW, viewport.W);
        float bottom = scaled ? Math.Clamp((viewport.Y + viewport.H - s.Rect.Y) / sy, 0f, localH) : MathF.Min(localH, viewport.H);
        D2D_RECT_F clip = s.Visible
            ? new D2D_RECT_F { left = left, top = top, right = right, bottom = bottom }
            : new D2D_RECT_F { left = 0f, top = 0f, right = 0f, bottom = 0f };
        Check(s.Child->SetClip(&clip), "video child SetClip");
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
            if (s.Content != null) { s.Content->Release(); s.Content = null; }
            if (s.Child != null) { s.Child->Release(); s.Child = null; }
            s.InUse = false;
        }
        _dirtyCount = 0;
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }
}
