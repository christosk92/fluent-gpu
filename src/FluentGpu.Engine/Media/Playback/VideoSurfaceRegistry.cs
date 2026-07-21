using System;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>The engine-invoked per-frame video pump for one binding (see <see cref="VideoSurfaceRegistry.RegisterPump"/>).
/// Given the current DIP→device <paramref name="scale"/>, it reads the live laid-out area and writes video intents /
/// drives <see cref="IMediaPlayer.PumpVideo"/> — so the control's <c>Render</c> stays a pure function.</summary>
public delegate void VideoPump(float scale);

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
        public RectF RectDip;         // desired content rect in DIP; the host scales to device px at drain time
        public RectF ViewportDip;     // visible container; smaller than RectDip for center-crop
        public uint ContentW, ContentH; // the content's native pixel size (e.g. decoder swapchain) — the presenter scales it to fill the rect (0 = unknown)
        public bool Visible;
        public int Z;
        public nuint DesiredHandle;   // the DComp surface handle to bind (0 = none produced yet)
        public bool ReleasePending;   // token released on the UI side; the host destroys the surface then frees the slot
        public bool Presenting;       // a media player is actively presenting into this surface (playing / ramping) — drives the host wake reason; does NOT affect the presenter drain

        // host-resolved (render thread):
        public VideoSurfaceId SurfaceId;   // none until first created
        public nuint BoundHandle;          // last handle actually bound (detects a change)
        public bool Dirty;                 // an intent changed → the next Drain re-applies it

        // single-writer pump ownership (UI thread): the ONE owner whose registered pump drives this slot each frame.
        public object? PumpOwner;
    }

    private readonly Entry[] _entries = new Entry[MaxSurfaces];
    private readonly Signal<VideoSurfaceId>[] _surfaceSignals;
    private bool _anyDirty;
    private int _presentingCount;   // number of slots a media player is actively presenting into (O(1) wake read)
    private static readonly bool s_diag = Environment.GetEnvironmentVariable("FG_DRM_DIAG") == "1";

    // ── per-binding pump callbacks (engine-invoked each frame; replaces the control's side-effecting Render) ──────────
    private struct PumpReg { public bool InUse; public int Token; public object? Owner; public VideoPump? Pump; }
    private readonly PumpReg[] _pumps = new PumpReg[MaxSurfaces];
    private long _pumpInvocations;         // total OWNER pumps actually invoked (fix: pump count tracks FRAMES, not renders)
    private long _suppressedNonOwnerPumps; // non-owner pumps suppressed by the single-writer contract (ownership diag)

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

    public void SetViewport(int token, RectF viewportDip)
    {
        ref Entry e = ref Slot(token);
        if (e.ViewportDip == viewportDip) return;
        e.ViewportDip = viewportDip;
        MarkDirty(ref e);
    }

    /// <summary>Set the content's native pixel size (decoder swapchain size) so the presenter can scale it to fill the
    /// rect instead of showing it 1:1 (cropped). Value-gated.</summary>
    public void SetContentSize(int token, uint width, uint height)
    {
        ref Entry e = ref Slot(token);
        if (e.ContentW == width && e.ContentH == height) return;
        e.ContentW = width; e.ContentH = height;
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

    /// <summary>Mark whether a media player is actively presenting new frames into this surface (playing, or ramping to
    /// play). This is the source of truth for <see cref="HasActivePresentation"/> — the host folds it into a frame-loop
    /// wake reason so a playing video keeps the loop ticking at display rate. Value-gated + O(1); it does NOT mark the
    /// entry dirty (presenting has no effect on the presenter drain). Cleared automatically on <see cref="Release"/> /
    /// <see cref="DestroyAll"/>. A media player must set it back to <c>false</c> on pause/stop/unbind.</summary>
    public void SetPresenting(int token, bool presenting)
    {
        int i = token - 1;
        if ((uint)i >= MaxSurfaces || !_entries[i].InUse) return;
        ref Entry e = ref _entries[i];
        if (e.Presenting == presenting) return;
        e.Presenting = presenting;
        _presentingCount += presenting ? 1 : -1;
    }

    /// <summary>True when at least one surface has an active presentation in flight (a media player is playing / ramping
    /// into it). Read once per frame by the host wake computation; O(1), zero-alloc.</summary>
    public bool HasActivePresentation => _presentingCount > 0;

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
        if (_entries[i].Presenting) { _entries[i].Presenting = false; _presentingCount--; }   // stop keeping the loop awake the instant the owner releases
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

    // ── per-binding pump seam (UI thread; engine-invoked per frame — the video pump leaves the control's Render) ──────

    /// <summary>Register a per-frame pump for a surface slot, owned by <paramref name="owner"/>. The host invokes it
    /// each frame (via <see cref="PumpAll"/>) with the current DIP→device scale, AFTER layout settles — so the pump
    /// reads the live laid-out area and writes video intents with NO side effect in the control's Render. The FIRST
    /// registrant of a slot becomes its initial pump owner. Returns a registration id (>0), or 0 when the slot is dead
    /// or the pump pool is exhausted.</summary>
    public int RegisterPump(int token, object owner, VideoPump pump)
    {
        int ti = token - 1;
        if ((uint)ti >= MaxSurfaces || !_entries[ti].InUse || owner is null || pump is null) return 0;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            if (_pumps[i].InUse) continue;
            _pumps[i] = new PumpReg { InUse = true, Token = token, Owner = owner, Pump = pump };
            _entries[ti].PumpOwner ??= owner;   // first registrant claims the slot
            return i + 1;
        }
        return 0;
    }

    /// <summary>Drop a pump registration. If it was the slot's current owner, ownership passes to any surviving
    /// registration for the same slot (keeps a shared slot single-writer without a gap when an owner unmounts).</summary>
    public void UnregisterPump(int regId)
    {
        int i = regId - 1;
        if ((uint)i >= MaxSurfaces || !_pumps[i].InUse) return;
        int token = _pumps[i].Token;
        object? owner = _pumps[i].Owner;
        _pumps[i] = default;
        int ti = token - 1;
        if ((uint)ti < MaxSurfaces && _entries[ti].InUse && ReferenceEquals(_entries[ti].PumpOwner, owner))
        {
            object? next = null;
            for (int j = 0; j < MaxSurfaces; j++)
                if (_pumps[j].InUse && _pumps[j].Token == token) { next = _pumps[j].Owner; break; }
            _entries[ti].PumpOwner = next;
        }
    }

    /// <summary>Transfer single-writer pump ownership of a slot to <paramref name="owner"/> (the first-class fullscreen
    /// hand-off, replacing the "exactly one instance pumps" convention). Only the owner's registered pump runs each
    /// frame; a non-owner pump is a no-op counted in <see cref="SuppressedNonOwnerPumpCount"/>.</summary>
    public void TransferOwnership(int token, object owner)
    {
        int ti = token - 1;
        if ((uint)ti >= MaxSurfaces || !_entries[ti].InUse || owner is null) return;
        _entries[ti].PumpOwner = owner;
    }

    /// <summary>True when <paramref name="owner"/> currently owns the slot's pump.</summary>
    public bool IsPumpOwner(int token, object owner)
    {
        int ti = token - 1;
        return (uint)ti < MaxSurfaces && _entries[ti].InUse && ReferenceEquals(_entries[ti].PumpOwner, owner);
    }

    /// <summary>Invoke every registered pump whose owner holds its slot; a non-owner pump is suppressed (counted). The
    /// host calls this once per frame on the UI thread after layout is settled and before <see cref="Drain"/>. Zero
    /// managed allocation: iterates a fixed array and invokes mount-registered delegates. Runs on every backend
    /// (headless too) so the pump cadence is frame-driven, not render-driven.</summary>
    public void PumpAll(float scale)
    {
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref PumpReg p = ref _pumps[i];
            if (!p.InUse || p.Pump is null) continue;
            int ti = p.Token - 1;
            if ((uint)ti >= MaxSurfaces || !_entries[ti].InUse) continue;
            if (ReferenceEquals(p.Owner, _entries[ti].PumpOwner))
            {
                p.Pump(scale);
                _pumpInvocations++;
            }
            else
            {
                _suppressedNonOwnerPumps++;   // single-writer contract: a non-owner pump does nothing this frame
                if (s_diag) Console.Error.WriteLine($"[drm-reg] non-owner pump suppressed for token {p.Token}");
            }
        }
    }

    /// <summary>Total owner-pump invocations since construction — the frame-phase pump cadence probe (pure-render gate:
    /// this tracks FRAMES, not component renders).</summary>
    public long PumpInvocationCount => _pumpInvocations;
    /// <summary>Non-owner pump attempts suppressed by the single-writer contract (ownership-transfer gate probe).</summary>
    public long SuppressedNonOwnerPumpCount => _suppressedNonOwnerPumps;

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
                if (e.Presenting) _presentingCount--;   // keep the wake counter balanced when a presenting slot is freed
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
            RectF viewportDip = e.ViewportDip.W > 0f && e.ViewportDip.H > 0f ? e.ViewportDip : e.RectDip;
            var viewportDev = new RectF(viewportDip.X * scale, viewportDip.Y * scale, viewportDip.W * scale, viewportDip.H * scale);
            presenter.SetContentSize(e.SurfaceId, e.ContentW, e.ContentH);   // so it scales the frame to fill `dev` (not 1:1-cropped)
            presenter.Place(e.SurfaceId, dev, 1f, e.Z);
            presenter.SetViewport(e.SurfaceId, viewportDev);
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
        _presentingCount = 0;
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
    /// <summary>Set the visible container. Oversized content is center-clipped to this rect.</summary>
    public void SetViewport(RectF rectDip) { if (_registry is { } r) r.SetViewport(Token, rectDip); }
    /// <summary>Set the content's native pixel size (decoder swapchain size) so the frame scales to fill the rect.</summary>
    public void SetContentSize(SizeI px) { if (_registry is { } r) r.SetContentSize(Token, (uint)Math.Max(0, px.Width), (uint)Math.Max(0, px.Height)); }
    /// <summary>Show/hide the surface.</summary>
    public void SetVisible(bool visible) { if (_registry is { } r) r.SetVisible(Token, visible); }
    /// <summary>Mark whether a media player is actively presenting new frames into this surface (playing / ramping to
    /// play). Keeps the host frame loop ticking at display rate while true (see
    /// <see cref="VideoSurfaceRegistry.HasActivePresentation"/>); set back to <c>false</c> on pause/stop.</summary>
    public void SetPresenting(bool presenting) { if (_registry is { } r) r.SetPresenting(Token, presenting); }
    /// <summary>Bind the DirectComposition surface handle produced by a video source (the DRM attach point).</summary>
    public void Bind(nuint dcompSurfaceHandle) { if (_registry is { } r) r.Bind(Token, dcompSurfaceHandle); }
    /// <summary>Tear the surface down (also done automatically when the owning component unmounts).</summary>
    public void Release() { if (_registry is { } r) r.Release(Token); }

    /// <summary>Register a per-frame pump owned by <paramref name="owner"/> (the engine invokes it each frame — the
    /// video pump leaves the control's Render). Returns a registration id, or 0 when this binding is inert.</summary>
    public int RegisterPump(object owner, VideoPump pump) => _registry?.RegisterPump(Token, owner, pump) ?? 0;
    /// <summary>Drop a pump registration returned by <see cref="RegisterPump"/>.</summary>
    public void UnregisterPump(int regId) { if (_registry is { } r) r.UnregisterPump(regId); }
    /// <summary>Transfer single-writer pump ownership of this slot to <paramref name="owner"/> (fullscreen hand-off).</summary>
    public void TransferOwnershipTo(object owner) { if (_registry is { } r) r.TransferOwnership(Token, owner); }
    /// <summary>True when <paramref name="owner"/> currently owns this slot's pump.</summary>
    public bool IsPumpOwner(object owner) => _registry is { } r && r.IsPumpOwner(Token, owner);
}
