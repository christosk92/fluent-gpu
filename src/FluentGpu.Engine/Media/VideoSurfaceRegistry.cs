using System;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// The portable arbitration buffer between the UI thread and the render-thread-confined <see cref="IVideoPresenter"/>.
/// A component (via the <c>UseVideoSurface</c> hook) or a media player declares a video surface — its rect, visibility,
/// and the DirectComposition surface handle to bind — as POD intents on the UI thread; the host drains them into the
/// presenter at phase 11 (<see cref="Drain"/>), so no ComPtr is ever touched off the render thread. Keeps the portable
/// core TerraFX-free: it references only the <see cref="IVideoPresenter"/> seam, never a D3D/DComp type.
/// </summary>
/// <remarks>
/// Writes are value-gated: re-declaring an unchanged rect/visibility/handle is a no-op, so a page that calls
/// <see cref="Place"/> every render produces zero redundant presenter calls. The drain only runs on a real composited
/// device (the host guards it on a non-null <see cref="IVideoPresenter"/>), never on the headless seam — so this type
/// is outside the zero-alloc gate surface by construction.
/// </remarks>
public sealed class VideoSurfaceRegistry
{
    private const int MaxSurfaces = 16;

    private struct Entry
    {
        public bool InUse;
        public RectF RectDip;         // desired rect in DIP; the host scales to device px at drain time
        public bool Visible;
        public int Z;
        public nuint DesiredHandle;   // the DComp surface handle to bind (0 = none produced yet)
        public bool ReleasePending;   // token released on the UI side; the host destroys the surface then frees the slot

        // host-resolved (render thread):
        public VideoSurfaceId SurfaceId;   // none until first created
        public nuint BoundHandle;          // last handle actually bound (detects a change)
        public bool Dirty;                 // an intent changed → the next Drain re-applies it
    }

    private readonly Entry[] _entries = new Entry[MaxSurfaces];
    private readonly Signal<VideoSurfaceId>[] _surfaceSignals;
    private bool _anyDirty;
    private static readonly bool s_diag = Environment.GetEnvironmentVariable("FG_DRM_DIAG") == "1";

    public VideoSurfaceRegistry()
    {
        _surfaceSignals = new Signal<VideoSurfaceId>[MaxSurfaces];
        for (int i = 0; i < MaxSurfaces; i++) _surfaceSignals[i] = new Signal<VideoSurfaceId>(default);
    }

    // ── UI-thread API (the hook / the media-player façade) ─────────────────────────────────────────────────────────

    /// <summary>Reserve a surface slot. Returns a token (>0) or 0 when the pool is exhausted.</summary>
    public int Acquire()
    {
        for (int i = 0; i < MaxSurfaces; i++)
        {
            if (_entries[i].InUse) continue;
            _entries[i] = new Entry { InUse = true, Visible = true };
            _surfaceSignals[i].Value = default;
            return i + 1;
        }
        return 0;
    }

    /// <summary>Set the surface's rect (DIP) and draw order. Value-gated.</summary>
    public void Place(int token, RectF rectDip, int z = 0)
    {
        ref Entry e = ref Slot(token);
        if (e.RectDip == rectDip && e.Z == z) return;
        e.RectDip = rectDip; e.Z = z;
        MarkDirty(ref e);
    }

    /// <summary>Show/hide the surface. Value-gated.</summary>
    public void SetVisible(int token, bool visible)
    {
        ref Entry e = ref Slot(token);
        if (e.Visible == visible) return;
        e.Visible = visible;
        MarkDirty(ref e);
    }

    /// <summary>Bind the DirectComposition surface handle produced by a video source (the single DRM attach point).
    /// Value-gated: re-binding the same handle is a no-op.</summary>
    public void Bind(int token, nuint dcompSurfaceHandle)
    {
        ref Entry e = ref Slot(token);
        if (e.DesiredHandle == dcompSurfaceHandle) return;
        e.DesiredHandle = dcompSurfaceHandle;
        MarkDirty(ref e);
    }

    /// <summary>Release the token: the host tears down the presenter surface on the next drain, then frees the slot.</summary>
    public void Release(int token)
    {
        int i = token - 1;
        if ((uint)i >= MaxSurfaces || !_entries[i].InUse) return;
        _entries[i].ReleasePending = true;
        MarkDirty(ref _entries[i]);
    }

    /// <summary>The surface-id signal for a token — <see cref="VideoSurfaceId.IsNone"/> until the host creates it, then
    /// the live id. A component binds this to know when the video child exists.</summary>
    public IReadSignal<VideoSurfaceId> Surface(int token)
    {
        int i = token - 1;
        return (uint)i < MaxSurfaces ? _surfaceSignals[i] : _surfaceSignals[0];
    }

    // ── Host drain (render/submit thread, phase 11) ────────────────────────────────────────────────────────────────

    /// <summary>Apply all pending intents to <paramref name="presenter"/> and issue at most one
    /// <see cref="IVideoPresenter.Commit"/>. <paramref name="scale"/> is the window DIP→device-px factor. No-op when
    /// nothing is dirty. MUST run on the render thread (the host calls it at phase 11 only when the device exposes a
    /// non-null presenter — i.e. never headless).</summary>
    public void Drain(IVideoPresenter presenter, float scale)
    {
        if (!_anyDirty) return;
        if (scale <= 0f) scale = 1f;
        bool changed = false;
        bool stillDirty = false;

        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Entry e = ref _entries[i];
            if (!e.InUse || !e.Dirty) continue;

            if (e.ReleasePending)
            {
                if (!e.SurfaceId.IsNone) { presenter.Destroy(e.SurfaceId); changed = true; }
                _surfaceSignals[i].Value = default;
                e = default;   // free the slot
                continue;
            }

          try
          {
            // Create the child visual on first use, once a handle exists to bind.
            if (e.SurfaceId.IsNone)
            {
                if (e.DesiredHandle == 0) { e.Dirty = false; continue; }   // nothing to show yet; wait for a handle
                e.SurfaceId = presenter.CreateSurface();
                _surfaceSignals[i].Value = e.SurfaceId;
                changed = true;
                if (s_diag) Console.Error.WriteLine($"[drm-reg] CreateSurface -> id={e.SurfaceId.Value}");
            }

            if (e.DesiredHandle != 0 && e.DesiredHandle != e.BoundHandle)
            {
                presenter.BindSurfaceHandle(e.SurfaceId, e.DesiredHandle);
                e.BoundHandle = e.DesiredHandle;
                changed = true;
                if (s_diag) Console.Error.WriteLine($"[drm-reg] BindSurfaceHandle id={e.SurfaceId.Value} handle=0x{e.DesiredHandle:X}");
            }

            var dev = new RectF(e.RectDip.X * scale, e.RectDip.Y * scale, e.RectDip.W * scale, e.RectDip.H * scale);
            presenter.Place(e.SurfaceId, dev, 1f, e.Z);
            presenter.SetVisible(e.SurfaceId, e.Visible);
            changed = true;
            e.Dirty = false;
            if (s_diag) Console.Error.WriteLine($"[drm-reg] Place id={e.SurfaceId.Value} dev=({dev.X:0},{dev.Y:0},{dev.W:0},{dev.H:0}) visible={e.Visible} scale={scale:0.##}");
          }
          catch (Exception ex) when (s_diag)
          {
            Console.Error.WriteLine($"[drm-reg] EXCEPTION at slot {i}: {ex.GetType().Name}: {ex.Message}");
            e.Dirty = false;
          }
        }

        // Recompute the dirty flag (an entry with no handle yet stays dirty and retries next frame).
        for (int i = 0; i < MaxSurfaces; i++)
            if (_entries[i].InUse && _entries[i].Dirty) { stillDirty = true; break; }
        _anyDirty = stillDirty;

        if (changed) presenter.Commit();
    }

    /// <summary>Tear down every live surface (device teardown / host dispose). Render thread.</summary>
    public void DestroyAll(IVideoPresenter presenter)
    {
        bool changed = false;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Entry e = ref _entries[i];
            if (e.InUse && !e.SurfaceId.IsNone) { presenter.Destroy(e.SurfaceId); changed = true; }
            _surfaceSignals[i].Value = default;
            e = default;
        }
        _anyDirty = false;
        if (changed) presenter.Commit();
    }

    private void MarkDirty(ref Entry e) { e.Dirty = true; _anyDirty = true; }

    private ref Entry Slot(int token)
    {
        int i = token - 1;
        if ((uint)i >= MaxSurfaces || !_entries[i].InUse)
            throw new ArgumentException($"VideoSurfaceRegistry: no live surface for token {token}.");
        return ref _entries[i];
    }
}

/// <summary>
/// The result of the <c>UseVideoSurface</c> hook (or a manual registry acquisition): a light handle over one video
/// surface slot. A media player writes the surface's placement + bound handle through it; a component binds
/// <see cref="Surface"/> to know when the video child exists. Invalid (a no-op) when video compositing is unavailable
/// (headless / non-composited window) — every method is then a safe no-op, so callers need no null checks.
/// </summary>
public readonly struct VideoBinding
{
    private static readonly Signal<VideoSurfaceId> s_none = new(default);
    private readonly VideoSurfaceRegistry? _registry;

    internal VideoBinding(VideoSurfaceRegistry? registry, int token)
    {
        _registry = token > 0 ? registry : null;
        Token = token;
    }

    /// <summary>The surface slot token (>0 when valid).</summary>
    public int Token { get; }
    /// <summary>True when this binding drives a real registry slot (video compositing is available).</summary>
    public bool IsValid => _registry is not null && Token > 0;

    /// <summary>The live surface id — <see cref="VideoSurfaceId.IsNone"/> until the host creates the child visual.</summary>
    public IReadSignal<VideoSurfaceId> Surface => _registry?.Surface(Token) ?? s_none;

    /// <summary>Set the surface rect (DIP) + draw order.</summary>
    public void Place(RectF rectDip, int z = 0) { if (_registry is { } r) r.Place(Token, rectDip, z); }
    /// <summary>Show/hide the surface.</summary>
    public void SetVisible(bool visible) { if (_registry is { } r) r.SetVisible(Token, visible); }
    /// <summary>Bind the DirectComposition surface handle produced by a video source (the DRM attach point).</summary>
    public void Bind(nuint dcompSurfaceHandle) { if (_registry is { } r) r.Bind(Token, dcompSurfaceHandle); }
    /// <summary>Tear the surface down (also done automatically when the owning component unmounts).</summary>
    public void Release() { if (_registry is { } r) r.Release(Token); }
}
