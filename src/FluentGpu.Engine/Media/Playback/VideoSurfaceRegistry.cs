using System;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>The engine-invoked video pump for one binding (see <see cref="VideoSurfaceRegistry.RegisterPump"/>).
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
        public float RadiusDip;         // corner radius in DIP (0 = square). Scaled to device px at flush, like the rect.
        public uint ContentW, ContentH; // the content's native pixel size (e.g. decoder swapchain) — the presenter scales it to fill the rect (0 = unknown)
        public bool Visible;
        public int Z;
        public nuint DesiredHandle;   // the DComp surface handle to bind (0 = none produced yet)
        public bool ReleasePending;   // token released on the UI side; the host destroys the surface then frees the slot
        public bool Presenting;       // diagnostic playback state; does NOT affect the presenter drain or host cadence
        public bool PumpPending;      // one coalesced UI-thread pump is requested for this slot
        public bool PumpFull;         // that pump was requested by a native/transport/activation edge (RequestPump), not only by moved geometry

        // host-resolved (render thread):
        public VideoSurfaceId SurfaceId;   // none until first created
        public nuint BoundHandle;          // last handle actually bound (set only once the presenter reports success)
        public bool Dirty;                 // an intent changed → the next Drain re-applies it
        public int BindFailures;           // consecutive failed BindSurfaceHandle calls for the current DesiredHandle
        public uint NextBindDrain;         // the first drain sequence number allowed to retry a failed bind (backoff)

        // single-writer pump ownership (UI thread): the ONE owner whose registered pump drives this slot.
        public object? PumpOwner;

        // ── geometry-change auto-pump (UI thread; see RequestGeometryPumps) ──────────────────────────────────────────
        // The scene node whose ABSOLUTE rect defines where this surface should composite, plus the rect it was last
        // pumped at. Held as opaque data — the registry never interprets them; RequestGeometryPumps does the compare.
        public NodeHandle GeomNode;
        public RectF GeomRect;
    }

    private readonly Entry[] _entries = new Entry[MaxSurfaces];
    private readonly Signal<VideoSurfaceId>[] _surfaceSignals;
    private readonly Signal<bool>[] _boundSignals;   // UI-thread-written mirror of _boundState (see SyncBoundSignals)
    private readonly Signal<bool> _alwaysBound = new(true);   // per registry (never written): what Bound reports when there is no presenter to wait for
    private bool _anyDirty;
    private IVideoPresenter? _drainedBy;   // the presenter the last Drain targeted; a different instance means the old one's DComp objects are gone
    private uint _drainSeq;                // Drain calls that did work; the clock the failed-bind backoff counts in
    private int _presentingCount;   // diagnostic census of slots a media player is actively presenting into
    private int _pendingPumpCount;  // slots with one coalesced pump awaiting the next host frame
    private int _geometryOnlyToken;  // the token whose geometry-only pump is running right now (0 outside one); see IsGeometryOnlyPump

    // ── per-binding pump callbacks (engine-invoked each frame; replaces the control's side-effecting Render) ──────────
    private struct PumpReg { public bool InUse; public int Token; public object? Owner; public VideoPump? Pump; }
    private readonly PumpReg[] _pumps = new PumpReg[MaxSurfaces];
    private long _pumpInvocations;         // total owner pumps actually invoked (tracks requests, not renders/frames)
    private long _suppressedNonOwnerPumps; // non-owner pumps suppressed by the single-writer contract (ownership diag)

    // Per-slot "surface bound" readiness, the one thing the render thread tells the UI thread. Signals are not
    // thread-safe, so the render thread never writes one: Drain publishes bit i of _boundState (Interlocked), and the
    // UI thread folds the difference into _boundSignals at the top of PumpPending. HasPendingPumps reports the edge so
    // the host runs that frame, and Drain returns true on an edge so the host also wakes the (possibly blocked) UI loop
    // from the render thread. _boundSeen is UI-thread-only; _boundChanged is render-thread-only.
    private int _boundState;
    private int _boundSeen;
    private bool _boundChanged;   // this Drain changed _boundState (set by PublishBound / ResetRenderSide / FreeReleased)

    /// <summary>True when the host drains this registry into a real presenter (set once by the host, UI thread). Only
    /// then does <see cref="Bound"/> track a slot's real binding; with no presenter (headless seam) nothing could ever
    /// bind, so <see cref="Bound"/> reports ready and a media element keeps its player-wide readiness behaviour.</summary>
    public bool RequireSlotBinding { get; set; }

    public VideoSurfaceRegistry()
    {
        _surfaceSignals = new Signal<VideoSurfaceId>[MaxSurfaces];
        _boundSignals = new Signal<bool>[MaxSurfaces];
        for (int i = 0; i < MaxSurfaces; i++)
        {
            _surfaceSignals[i] = new Signal<VideoSurfaceId>(default);
            _boundSignals[i] = new Signal<bool>(false);
        }
    }

    /// <summary>The native handle of the window this registry's surfaces are presented in (an HWND on Windows; 0 = none known:
    /// a headless host, or not yet set). The host sets it once at construction (the main window, or a pop-out's own); a protected
    /// session reads it through <see cref="VideoBinding.WindowHandle"/> to tie output protection to that window's monitor.</summary>
    public nuint WindowHandle { get; set; }

    // ── UI-thread API (the hook / the media-player façade) ─────────────────────────────────────────────────────────

    /// <summary>Reserve a surface slot. Returns a token (>0) or 0 when the pool is exhausted.</summary>
    public int Acquire()
    {
        for (int i = 0; i < MaxSurfaces; i++)
        {
            if (_entries[i].InUse) continue;
            _entries[i] = new Entry { InUse = true, Visible = true };
            _surfaceSignals[i].Value = default;
            _boundSignals[i].Value = false;
            _boundSeen &= ~(1 << i);
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

    /// <summary>Round the composited surface's corners (DIP; 0 = square). The video child visual composites outside the
    /// UI back buffer, so a UI-side rounded parent cannot clip it — this is the only way to get a non-rectangular video.
    /// Value-gated.</summary>
    public void SetCornerRadius(int token, float radiusDip)
    {
        ref Entry e = ref Slot(token);
        if (e.RadiusDip == radiusDip) return;
        e.RadiusDip = radiusDip;
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

    /// <summary>Record whether a media player is actively presenting new frames into this surface (playing, or ramping
    /// to play). This is diagnostic state only: native DirectComposition video presents decoded frames independently;
    /// it no longer makes the host redraw at display rate. Value-gated + O(1); it does NOT mark the entry dirty.
    /// Cleared automatically on <see cref="Release"/> / <see cref="DestroyAll"/>.</summary>
    public void SetPresenting(int token, bool presenting)
    {
        int i = token - 1;
        if ((uint)i >= MaxSurfaces || !_entries[i].InUse) return;
        ref Entry e = ref _entries[i];
        if (e.Presenting == presenting) return;
        e.Presenting = presenting;
        _presentingCount += presenting ? 1 : -1;
    }

    /// <summary>True when at least one surface has active presentation state. Retained for diagnostics; it does not
    /// control host wake cadence.</summary>
    public bool HasActivePresentation => _presentingCount > 0;

    /// <summary>True when at least one surface is live on screen — created, bound to a handle, and visible. The host
    /// pushes this to the window (<c>IWindow.SetHasLiveVideo</c>) so a composited window carrying video can opt out of
    /// the modal edge-resize paint defer, which would otherwise leave the video child at its pre-resize geometry while
    /// the frame moves under it. O(MaxSurfaces), zero-alloc.</summary>
    public bool HasLiveSurface
    {
        get
        {
            for (int i = 0; i < MaxSurfaces; i++)
            {
                ref Entry e = ref _entries[i];
                if (e.InUse && !e.ReleasePending && e.Visible && !e.SurfaceId.IsNone && e.BoundHandle != 0) return true;
            }
            return false;
        }
    }

    /// <summary>Bind the DirectComposition surface handle produced by a video source (the single DRM attach point).
    /// Value-gated: re-binding the same handle is a no-op, unless <paramref name="force"/> is set — a producer that
    /// re-raises an UNCHANGED handle (its swap chain was rebuilt behind the same value) must make the presenter wrap it
    /// again, which is <see cref="Rebind"/>.</summary>
    public void Bind(int token, nuint dcompSurfaceHandle, bool force = false)
    {
        ref Entry e = ref Slot(token);
        if (e.DesiredHandle == dcompSurfaceHandle)
        {
            if (force) Rebind(token);
            return;
        }
        e.DesiredHandle = dcompSurfaceHandle;
        MarkDirty(ref e);
        if (OneSurfacePerPlayerGuard.CompiledIn && OneSurfacePerPlayerGuard.Enabled && dcompSurfaceHandle != 0)
            CheckOneSurfacePerPlayer(token, dcompSurfaceHandle);
    }

    /// <summary>Make the next drain bind this slot's current handle again, even though it did not change (the producer
    /// rebuilt the surface behind the same handle value, or a failed bind should be retried at once). No-op while no
    /// handle has been bound.</summary>
    public void Rebind(int token)
    {
        ref Entry e = ref Slot(token);
        if (e.DesiredHandle == 0) return;
        e.BoundHandle = 0;
        e.BindFailures = 0;
        e.NextBindDrain = 0;
        MarkDirty(ref e);
    }

    /// <summary>E4 tripwire body: scan the fixed slot array for another LIVE slot already carrying the SAME desired
    /// handle — two elements bound to one player's MF swapchain, a size fight the moment both pump (each calls
    /// SetVideoStreamRect with its own laid-out rect). O(MaxSurfaces), zero-alloc on the passing path (struct-field
    /// compares only; the message is built inside <see cref="OneSurfacePerPlayerGuard.Violation"/>, never here).</summary>
    private void CheckOneSurfacePerPlayer(int token, nuint handle)
    {
        int ti = token - 1;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            if (i == ti) continue;
            ref Entry other = ref _entries[i];
            if (!other.InUse || other.ReleasePending || other.DesiredHandle != handle) continue;
            OneSurfacePerPlayerGuard.Violation(token, i + 1, handle);
        }
    }

    /// <summary>Release the token: the host tears down the presenter surface on the next drain, then frees the slot.</summary>
    public void Release(int token)
    {
        int i = token - 1;
        if ((uint)i >= MaxSurfaces || !_entries[i].InUse) return;
        if (_entries[i].Presenting) { _entries[i].Presenting = false; _presentingCount--; }
        ClearPumpPending(ref _entries[i]);
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

    /// <summary>True once this token's OWN presenter surface exists AND has a handle bound and committed — the readiness a
    /// media element must wait for before it punches its hole or drops its poster (the player-wide
    /// <c>VideoSurface</c> says only that SOME element's slot presented a frame). Flips back to false when the presenter is
    /// replaced (device recovery) until the rebuilt surface is bound again. Always true when
    /// <see cref="RequireSlotBinding"/> is off (no presenter to wait for). UI-thread signal.</summary>
    public IReadSignal<bool> Bound(int token)
    {
        int i = token - 1;
        if (!RequireSlotBinding || (uint)i >= MaxSurfaces) return _alwaysBound;
        return _boundSignals[i];
    }

    // ── per-binding pump seam (UI thread; engine-invoked per frame — the video pump leaves the control's Render) ──────

    /// <summary>Register an on-demand pump for a surface slot, owned by <paramref name="owner"/>. The host invokes it
    /// after a coalesced <see cref="RequestPump"/> and layout settlement, with the current DIP→device scale — so the
    /// pump reads the live laid-out area and writes video intents with NO side effect in the control's Render. The FIRST
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
            RequestPump(token);                  // establish geometry / handle binding once after mount
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
            if (next is null) ClearPumpPending(ref _entries[ti]);
            else RequestPump(token);             // hand-off writes the new owner's geometry immediately
        }
    }

    /// <summary>Transfer single-writer pump ownership of a slot to <paramref name="owner"/> (the first-class fullscreen
    /// hand-off, replacing the "exactly one instance pumps" convention). Only the owner's registered pump runs for a
    /// request; alternates are counted in <see cref="SuppressedNonOwnerPumpCount"/>.</summary>
    public void TransferOwnership(int token, object owner)
    {
        int ti = token - 1;
        if ((uint)ti >= MaxSurfaces || !_entries[ti].InUse || owner is null) return;
        _entries[ti].PumpOwner = owner;
        RequestPump(token);
    }

    /// <summary>True when <paramref name="owner"/> currently owns the slot's pump.</summary>
    public bool IsPumpOwner(int token, object owner)
    {
        int ti = token - 1;
        return (uint)ti < MaxSurfaces && _entries[ti].InUse && ReferenceEquals(_entries[ti].PumpOwner, owner);
    }

    /// <summary>Request one UI-thread video pump after layout settles. Requests are value-gated per slot: a burst of
    /// native state events, geometry changes, or transport commands yields one pump, not a permanent frame-loop reason.
    /// The caller must be on the UI thread; background media callbacks post through their control first.</summary>
    public void RequestPump(int token)
    {
        int ti = token - 1;
        if ((uint)ti >= MaxSurfaces || !_entries[ti].InUse) return;
        ref Entry e = ref _entries[ti];
        e.PumpFull = true;   // upgrades a pending geometry-only request: the full turn also covers the placement
        if (e.PumpPending) return;
        e.PumpPending = true;
        _pendingPumpCount++;
    }

    /// <summary>The geometry request: the same coalesced pump as <see cref="RequestPump"/>, but WITHOUT the full-pump
    /// reason, so the owner may take its placement-only path (<see cref="IsGeometryOnlyPump"/>). It is what
    /// <see cref="RequestGeometryPumps"/> raises for a moved rect and what the owner raises for a layout-driven rect change
    /// (a resize, a reflow), the same motion by another route. A native / transport / activation request for the same slot
    /// (<see cref="RequestPump"/>) still upgrades it to a full pump. A free, released or out-of-range token is ignored.</summary>
    public void RequestGeometryPump(int token)
    {
        int ti = token - 1;
        if ((uint)ti >= MaxSurfaces || !_entries[ti].InUse || _entries[ti].ReleasePending) return;
        ref Entry e = ref _entries[ti];
        if (e.PumpPending) return;
        e.PumpPending = true;
        _pendingPumpCount++;
    }

    /// <summary>True while the pump for <paramref name="token"/> that is running RIGHT NOW was requested ONLY because the
    /// surface's absolute rect moved (a drag, a resize, an animated placement) — no native event, transport command,
    /// activation edge or source/fit change rode along. The owner's pump may then place the surface and skip the
    /// session publish (state, buffering, position, cue, ABR bookkeeping), which none of those inputs changed. False
    /// outside a pump invocation.</summary>
    public bool IsGeometryOnlyPump(int token) => token > 0 && _geometryOnlyToken == token;

    /// <summary>The content size (device px) last written for <paramref name="token"/> by the session that owns the
    /// surface — <see cref="SizeI.Zero"/> until one has (the surface is not bound / sized yet). A geometry-only pump
    /// reads it to decide that the cached content size is still the right one for the rect it is placing.</summary>
    public SizeI ContentSize(int token)
    {
        int i = token - 1;
        if ((uint)i >= MaxSurfaces || !_entries[i].InUse || _entries[i].ReleasePending) return SizeI.Zero;
        return new SizeI((int)_entries[i].ContentW, (int)_entries[i].ContentH);
    }

    /// <summary>True when at least one coalesced video pump must run on the next host frame. O(1), zero-alloc.</summary>
    public bool HasPendingPumps => _pendingPumpCount > 0 || (RequireSlotBinding && Volatile.Read(ref _boundState) != _boundSeen);

    /// <summary>Declare the scene node whose ABSOLUTE rect this surface should follow, so a move that changes only a
    /// composited transform still re-places the video child. Pass <see cref="NodeHandle.Null"/> to stop tracking.
    /// <para>Why this exists: a surface's on-screen rect can change with NO layout change at all — a compositor-only
    /// <c>Transform</c> translation (Wavee's draggable mini-player), a page transition, a FLIP projection. Those move
    /// the punched hole (it is painted through the node's transform) but fire no bounds-changed edge, so nothing
    /// requested a pump and the DirectComposition child stayed at its last-pumped offset until some unrelated event
    /// (a native state change, a transport command) happened to request one. That is the video visibly trailing the
    /// card during a drag.</para></summary>
    public void SetGeometryNode(int token, NodeHandle node)
    {
        int ti = token - 1;
        if ((uint)ti >= MaxSurfaces || !_entries[ti].InUse) return;
        ref Entry e = ref _entries[ti];
        if (e.GeomNode == node) return;
        e.GeomNode = node;
        e.GeomRect = default;   // force the next compare to fire, so a re-pointed node re-places immediately
    }

    /// <summary>Request a pump for every tracked surface whose absolute rect MOVED since its last pump. Called by the
    /// host once per frame at phase 7.2, immediately before <see cref="PumpPending"/>, so the placement it produces is
    /// drained on this same frame turn.
    /// <para>Deliberately NOT a wake reason: this only observes a frame that is already being produced (a drag, an
    /// animation, a relayout), and it requests nothing when nothing moved — so a playing video still does not turn
    /// every host frame into a repaint. Zero managed allocation: a fixed-array scan over at most
    /// <c>MaxSurfaces</c> slots with struct-only math.</para></summary>
    public void RequestGeometryPumps(FluentGpu.Scene.SceneStore scene)
    {
        if (scene is null) return;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Entry e = ref _entries[i];
            if (!e.InUse || e.ReleasePending || !e.Visible || e.GeomNode.IsNull) continue;
            if (!scene.IsLive(e.GeomNode)) continue;
            // The TRANSFORMED rect: a scale on the node or an ancestor (an entrance, a page transition, a zoom FLIP) moves
            // and resizes the painted hole with no layout change, and the owner places the surface from this same rect.
            RectF now = scene.AbsoluteTransformedRect(e.GeomNode);
            if (now == e.GeomRect) continue;
            e.GeomRect = now;
            RequestGeometryPump(i + 1);
        }
    }

    /// <summary>Invoke each requested slot's current owner once. The host calls this on the UI thread after layout is
    /// settled and before <see cref="Drain"/>. Clearing the pending bit before invoking allows a re-entrant native event
    /// to request exactly one FOLLOWING pump. Zero managed allocation: fixed arrays and mount-registered delegates.</summary>
    public void PumpPending(float scale)
    {
        SyncBoundSignals();
        if (_pendingPumpCount == 0) return;
        for (int ti = 0; ti < MaxSurfaces; ti++)
        {
            ref Entry e = ref _entries[ti];
            if (!e.InUse || !e.PumpPending) continue;

            // Clear first: the callback may synchronously receive a native event and queue its next settled turn.
            e.PumpPending = false;
            _pendingPumpCount--;
            bool geometryOnly = !e.PumpFull;   // only RequestGeometryPumps asked for this turn
            e.PumpFull = false;

            int ownerIndex = -1;
            int alternates = 0;
            for (int i = 0; i < MaxSurfaces; i++)
            {
                ref PumpReg p = ref _pumps[i];
                if (!p.InUse || p.Token != ti + 1 || p.Pump is null) continue;
                if (ReferenceEquals(p.Owner, e.PumpOwner)) ownerIndex = i;
                else alternates++;
            }
            if (alternates != 0)
            {
                _suppressedNonOwnerPumps += alternates;
                if (Diag.CompiledIn && Diag.Enabled) Diag.Event("drm-reg", $"{alternates} non-owner pump(s) suppressed for token {ti + 1}");
            }
            if (ownerIndex < 0) continue;    // registration vanished; a later mount will request again
            _geometryOnlyToken = geometryOnly ? ti + 1 : 0;
            try { _pumps[ownerIndex].Pump!(scale); }
            finally { _geometryOnlyToken = 0; }
            _pumpInvocations++;
        }
    }

    /// <summary>Total owner-pump invocations since construction — the coalesced pump cadence probe (pure-render gate:
    /// this tracks requests, not component renders or host frames).</summary>
    public long PumpInvocationCount => _pumpInvocations;
    /// <summary>Non-owner pump attempts suppressed by the single-writer contract (ownership-transfer gate probe).</summary>
    public long SuppressedNonOwnerPumpCount => _suppressedNonOwnerPumps;

    // ── Host drain (render/submit thread, phase 11) ────────────────────────────────────────────────────────────────

    /// <summary>Apply all pending intents to <paramref name="presenter"/> and issue at most one
    /// <see cref="IVideoPresenter.Commit"/>. <paramref name="scale"/> is the window DIP→device-px factor. No-op when
    /// nothing is dirty. MUST run on the render thread (the host calls it at phase 11 only when the device exposes a
    /// non-null presenter — i.e. never headless). Returns true when this call changed the published per-slot readiness
    /// (<see cref="Bound"/> edge), so a host draining off the UI thread knows to wake the UI loop.</summary>
    public bool Drain(IVideoPresenter presenter, float scale)
    {
        _boundChanged = false;
        // A different presenter instance means the previous one was disposed (device recovery rebuilt the DirectComposition
        // device): every surface it held is gone. Reset BEFORE the nothing-dirty early-out, because a recovered device
        // brings no new intent — the live entries must re-create, re-bind and re-place on the new presenter by themselves.
        if (!ReferenceEquals(presenter, _drainedBy))
        {
            if (_drainedBy is not null) ResetRenderSide();
            _drainedBy = presenter;
        }
        if (!_anyDirty) return _boundChanged;
        _drainSeq++;
        if (scale <= 0f) scale = 1f;
        bool changed = false;
        bool stillDirty = false;

        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Entry e = ref _entries[i];
            if (!e.InUse || !e.Dirty) continue;
            bool announce = false;   // this drain created or (re)bound the surface: say where it was placed, once

            if (e.ReleasePending)
            {
                if (!e.SurfaceId.IsNone) { presenter.Destroy(e.SurfaceId); changed = true; }
                FreeReleased(i);
                continue;
            }

            // Create the child visual on first use, once a handle exists to bind.
            if (e.SurfaceId.IsNone)
            {
                if (e.DesiredHandle == 0) { e.Dirty = false; continue; }   // nothing to show yet; wait for a handle
                e.SurfaceId = presenter.CreateSurface();
                if (e.SurfaceId.IsNone)
                {
                    // The presenter's own native call failed (device-lost/removed, out of DComp resources, ...); it
                    // is non-throwing (DCompVideoPresenter.Ok) and already logged once for this HRESULT. Leave the
                    // entry Dirty so THIS slot retries CreateSurface on the next Drain instead of the surface silently
                    // never appearing.
                    continue;
                }
                _surfaceSignals[i].Value = e.SurfaceId;
                changed = true;
                announce = true;
                // Always-on (one line per surface, ever): a pop-out that stays black cannot be told apart from one
                // whose child visual was never created without this — 2026-09-22.
                Diag.Line($"[video.surface] create token={i + 1} id={e.SurfaceId.Value}");
                if (Diag.CompiledIn && Diag.Enabled) Diag.Event("drm-reg", $"CreateSurface -> id={e.SurfaceId.Value}");
            }

            if (e.DesiredHandle != 0 && e.DesiredHandle != e.BoundHandle && _drainSeq >= e.NextBindDrain)
            {
                // BoundHandle records only a bind the presenter reports as done: a transient CreateSurfaceFromHandle /
                // SetContent failure leaves the entry dirty and retries with a growing backoff (2, 4 ... 64 drains), so
                // a handle that is never re-raised still reaches the screen once the device lets it.
                if (presenter.BindSurfaceHandle(e.SurfaceId, e.DesiredHandle))
                {
                    e.BoundHandle = e.DesiredHandle;
                    changed = true;
                    announce = true;
                    // Always-on, one line per HANDLE CHANGE (the native engine swaps to a new swap chain on a resolution
                    // change): the line that says whether the visual follows it or keeps showing the first, now-dead one.
                    Diag.Line($"[video.surface] bind token={i + 1} id={e.SurfaceId.Value} handle=0x{e.DesiredHandle:X}"
                        + (e.BindFailures != 0 ? $" (after {e.BindFailures} failed attempt(s))" : ""));
                    if (Diag.CompiledIn && Diag.Enabled) Diag.Event("drm-reg", $"BindSurfaceHandle id={e.SurfaceId.Value} handle=0x{e.DesiredHandle:X}");
                    e.BindFailures = 0;
                    e.NextBindDrain = 0;
                }
                else
                {
                    // The presenter dropped the old content so no stale frame stays under the new session; commit that.
                    e.BindFailures++;
                    e.NextBindDrain = _drainSeq + (1u << Math.Min(e.BindFailures, 6));
                    changed = true;
                    if (e.BindFailures == 1)
                        Diag.Line($"[video.surface] bind FAILED token={i + 1} id={e.SurfaceId.Value} handle=0x{e.DesiredHandle:X}; retrying with backoff");
                }
            }

            // Whole device pixels (rule R, SnapToDevicePixels): the presenter composites the frame and its clip on the pixel
            // grid, so the video edge shares a boundary with the UI hole's snapped erase rect instead of landing on a
            // fractional offset that DWM resamples into a one-pixel halo.
            var dev = SnapToDevicePixels(new RectF(e.RectDip.X * scale, e.RectDip.Y * scale, e.RectDip.W * scale, e.RectDip.H * scale));
            RectF viewportDip = e.ViewportDip.W > 0f && e.ViewportDip.H > 0f ? e.ViewportDip : e.RectDip;
            var viewportDev = SnapToDevicePixels(new RectF(viewportDip.X * scale, viewportDip.Y * scale, viewportDip.W * scale, viewportDip.H * scale));
            presenter.SetContentSize(e.SurfaceId, e.ContentW, e.ContentH);   // so it scales the frame to fill `dev` (not 1:1-cropped)
            presenter.Place(e.SurfaceId, dev, 1f, e.Z);
            presenter.SetViewport(e.SurfaceId, viewportDev);
            presenter.SetCornerRadius(e.SurfaceId, e.RadiusDip * scale);
            presenter.SetVisible(e.SurfaceId, e.Visible);
            changed = true;
            e.Dirty = e.DesiredHandle != 0 && e.DesiredHandle != e.BoundHandle;   // an unbound handle (failed bind) retries next drain
            // Only on the drain that created/bound (a resize re-places every frame; that stays Debug-only below).
            if (announce)
                Diag.Line($"[video.surface] place token={i + 1} id={e.SurfaceId.Value} dev=({dev.X:0},{dev.Y:0},{dev.W:0},{dev.H:0}) content={e.ContentW}x{e.ContentH} visible={e.Visible} scale={scale:0.##}");
            if (Diag.CompiledIn && Diag.Enabled) Diag.Event("drm-reg", $"Place id={e.SurfaceId.Value} dev=({dev.X:0},{dev.Y:0},{dev.W:0},{dev.H:0}) visible={e.Visible} scale={scale:0.##}");
        }

        // Recompute the dirty flag (an entry with no handle yet stays dirty and retries next frame).
        for (int i = 0; i < MaxSurfaces; i++)
            if (_entries[i].InUse && _entries[i].Dirty) { stillDirty = true; break; }
        _anyDirty = stillDirty;

        if (changed)
        {
            presenter.Commit();
            PublishBound();   // after the Commit: the bound surface is now actually composed
        }
        return _boundChanged;
    }

    /// <summary>The device-pixel snap rule (rule R) for every rect the video path hands the compositor: round X, Y, Right
    /// and Bottom INDEPENDENTLY with <see cref="MidpointRounding.AwayFromZero"/>, then rebuild W = Right - X and
    /// H = Bottom - Y. Independent edges (not a rounded origin plus a rounded size) keep two rects that share a fractional
    /// edge on the SAME pixel boundary, so a width can legitimately differ from <c>round(W)</c> by one. The composite's
    /// hole-erase rect applies the same rule, so the UI hole and the video visual agree to the pixel.</summary>
    public static RectF SnapToDevicePixels(RectF deviceRect)
    {
        float l = MathF.Round(deviceRect.X, MidpointRounding.AwayFromZero);
        float t = MathF.Round(deviceRect.Y, MidpointRounding.AwayFromZero);
        float r = MathF.Round(deviceRect.Right, MidpointRounding.AwayFromZero);
        float b = MathF.Round(deviceRect.Bottom, MidpointRounding.AwayFromZero);
        return new RectF(l, t, r - l, b - t);
    }

    /// <summary>Presenter swap (render thread): forget everything the old presenter held. Live entries drop their surface
    /// id and bound handle and go dirty so the normal first-use path rebuilds them; a release-pending entry is simply
    /// freed (its surface died with the old presenter, so there is nothing to Destroy).</summary>
    private void ResetRenderSide()
    {
        int reset = 0;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Entry e = ref _entries[i];
            if (!e.InUse) continue;
            if (e.ReleasePending) { FreeReleased(i); continue; }
            if (!e.SurfaceId.IsNone) reset++;
            e.SurfaceId = default;
            e.BoundHandle = 0;
            e.BindFailures = 0;
            e.NextBindDrain = 0;
            _surfaceSignals[i].Value = default;
            ClearBound(i);
            MarkDirty(ref e);
        }
        Diag.Line($"[video.surface] presenter changed -> reset {reset} live surfaces");
    }

    /// <summary>Free a slot whose token was released (render thread): keeps the wake counters and readiness mirror balanced.</summary>
    private void FreeReleased(int i)
    {
        ref Entry e = ref _entries[i];
        if (e.Presenting) _presentingCount--;   // keep the wake counter balanced when a presenting slot is freed
        ClearPumpPending(ref e);
        _surfaceSignals[i].Value = default;
        ClearBound(i);
        e = default;   // free the slot
    }

    /// <summary>Publish which slots now have a bound, committed surface (render thread; bits are only ever cleared when a
    /// slot is freed or the presenter is replaced).</summary>
    private void PublishBound()
    {
        int bits = 0;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Entry e = ref _entries[i];
            if (e.InUse && !e.ReleasePending && !e.SurfaceId.IsNone && e.BoundHandle != 0) bits |= 1 << i;
        }
        if (bits == 0) return;
        int prev = Interlocked.Or(ref _boundState, bits);
        if ((prev | bits) != prev) _boundChanged = true;
    }

    private void ClearBound(int i)
    {
        int prev = Interlocked.And(ref _boundState, ~(1 << i));
        if ((prev & (1 << i)) != 0) _boundChanged = true;
    }

    /// <summary>UI thread: fold the render thread's published readiness into the signals a media element subscribes to.</summary>
    private void SyncBoundSignals()
    {
        if (!RequireSlotBinding) return;
        int now = Volatile.Read(ref _boundState);
        int diff = now ^ _boundSeen;
        if (diff == 0) return;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            int bit = 1 << i;
            if ((diff & bit) != 0) _boundSignals[i].Value = (now & bit) != 0;
        }
        _boundSeen = now;
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
        _pendingPumpCount = 0;
        _drainedBy = null;
        Interlocked.Exchange(ref _boundState, 0);
        if (changed) presenter.Commit();
    }

    private void MarkDirty(ref Entry e) { e.Dirty = true; _anyDirty = true; }
    private void ClearPumpPending(ref Entry e)
    {
        e.PumpFull = false;
        if (!e.PumpPending) return;
        e.PumpPending = false;
        _pendingPumpCount--;
    }

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
    private static readonly Signal<bool> s_bound = new(true);
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

    /// <summary>The native handle of the window this binding's surface is presented in (an HWND), or 0 for an inert binding or a
    /// host with no window (headless). See <see cref="VideoSurfaceRegistry.WindowHandle"/>.</summary>
    public nuint WindowHandle => _registry?.WindowHandle ?? 0;

    /// <summary>The live surface id — <see cref="VideoSurfaceId.IsNone"/> until the host creates the child visual.</summary>
    public IReadSignal<VideoSurfaceId> Surface => _registry?.Surface(Token) ?? s_none;

    /// <summary>True once THIS slot's presenter surface exists with a handle bound and committed (see
    /// <see cref="VideoSurfaceRegistry.Bound"/>). Always true for an inert binding and on a host with no presenter.</summary>
    public IReadSignal<bool> Bound => _registry?.Bound(Token) ?? s_bound;

    /// <summary>Set the surface rect (DIP) + draw order.</summary>
    public void Place(RectF rectDip, int z = 0) { if (_registry is { } r) r.Place(Token, rectDip, z); }
    /// <summary>Set the visible container. Oversized content is center-clipped to this rect.</summary>
    public void SetViewport(RectF rectDip) { if (_registry is { } r) r.SetViewport(Token, rectDip); }
    /// <summary>Set the content's native pixel size (decoder swapchain size) so the frame scales to fill the rect.</summary>
    /// <summary>Round this surface's corners (DIP). Half the shorter side gives a circle.</summary>
    public void SetCornerRadius(float radiusDip) { if (_registry is { } r) r.SetCornerRadius(Token, radiusDip); }
    public void SetContentSize(SizeI px) { if (_registry is { } r) r.SetContentSize(Token, (uint)Math.Max(0, px.Width), (uint)Math.Max(0, px.Height)); }
    /// <summary>Show/hide the surface.</summary>
    public void SetVisible(bool visible) { if (_registry is { } r) r.SetVisible(Token, visible); }
    /// <summary>Declare the scene node whose absolute rect this surface follows, so a compositor-only move (a drag, a
    /// page transition, a FLIP projection) re-places the video child even though it changes no layout bounds.
    /// See <see cref="VideoSurfaceRegistry.SetGeometryNode"/>.</summary>
    public void SetGeometryNode(NodeHandle node) { if (_registry is { } r) r.SetGeometryNode(Token, node); }
    /// <summary>Record whether a media player is actively presenting new frames into this surface (playing / ramping to
    /// play). Diagnostic only; native video presentation does not force the host's frame cadence.</summary>
    public void SetPresenting(bool presenting) { if (_registry is { } r) r.SetPresenting(Token, presenting); }
    /// <summary>Bind the DirectComposition surface handle produced by a video source (the DRM attach point). With
    /// <paramref name="force"/> an UNCHANGED handle is wrapped again (the producer re-raised it after rebuilding its surface).</summary>
    public void Bind(nuint dcompSurfaceHandle, bool force = false) { if (_registry is { } r) r.Bind(Token, dcompSurfaceHandle, force); }
    /// <summary>Tear the surface down (also done automatically when the owning component unmounts).</summary>
    public void Release() { if (_registry is { } r) r.Release(Token); }

    /// <summary>Register an on-demand pump owned by <paramref name="owner"/> (the engine invokes it after a coalesced
    /// request — the video pump leaves the control's Render). Returns a registration id, or 0 when this binding is inert.</summary>
    public int RegisterPump(object owner, VideoPump pump) => _registry?.RegisterPump(Token, owner, pump) ?? 0;
    /// <summary>Request one coalesced FULL pump after layout settles (native event, transport, activation, source/fit change).</summary>
    public void RequestPump() { if (_registry is { } r) r.RequestPump(Token); }
    /// <summary>Request one coalesced GEOMETRY-class pump: the rect changed and nothing else did, so the owner may place the
    /// surface and skip the session publish (see <see cref="VideoSurfaceRegistry.RequestGeometryPump"/>). A full request in
    /// the same turn upgrades it. No-op for an inert binding.</summary>
    public void RequestGeometryPump() { if (_registry is { } r) r.RequestGeometryPump(Token); }
    /// <summary>True while the pump running now was requested only because this surface's rect moved (see
    /// <see cref="VideoSurfaceRegistry.IsGeometryOnlyPump"/>). False for an inert binding or outside a pump.</summary>
    public bool IsGeometryOnlyPump => _registry is { } r && r.IsGeometryOnlyPump(Token);
    /// <summary>The content size last written to this slot, or <see cref="SizeI.Zero"/> when none has been.</summary>
    public SizeI ContentSize => _registry?.ContentSize(Token) ?? SizeI.Zero;
    /// <summary>Drop a pump registration returned by <see cref="RegisterPump"/>.</summary>
    public void UnregisterPump(int regId) { if (_registry is { } r) r.UnregisterPump(regId); }
    /// <summary>Transfer single-writer pump ownership of this slot to <paramref name="owner"/> (fullscreen hand-off).</summary>
    public void TransferOwnershipTo(object owner) { if (_registry is { } r) r.TransferOwnership(Token, owner); }
    /// <summary>True when <paramref name="owner"/> currently owns this slot's pump.</summary>
    public bool IsPumpOwner(object owner) => _registry is { } r && r.IsPumpOwner(Token, owner);
}

/// <summary>
/// A DEBUG-only correctness tripwire (modeled on <see cref="FluentGpu.Hooks.ReuseGuard"/> /
/// <see cref="FluentGpu.Reconciler.BindContract"/> / <see cref="FluentGpu.Signals.BackwardsWriteGuard"/>). The
/// registry enforces single-writer PER SLOT (<see cref="VideoSurfaceRegistry.TransferOwnership"/>, the non-owner
/// suppression in <see cref="VideoSurfaceRegistry.PumpPending"/>) but nothing enforces ONE SLOT PER PLAYER: two
/// mounted elements bound to the SAME <c>IMediaPlayer</c> acquire DIFFERENT tokens (<see cref="VideoSurfaceRegistry.Acquire"/>
/// is slot-blind to the caller), both <see cref="VideoSurfaceRegistry.Bind"/> the SAME MF DComp swapchain handle, and
/// both end up asking the presenter to place that one swapchain at two different rects — a size fight, one repaint
/// per flip. Hazard documented at <c>MediaPlayerElement.cs:120-124</c> (the <c>PresentationBinding</c> doc comment:
/// sharing ONE <see cref="VideoBinding"/> is the sanctioned way two views present one player), never checked until
/// now. This surfaces exactly that: two LIVE (in-use, not release-pending) slots sharing one nonzero
/// <c>DesiredHandle</c>.
/// <para>
/// Cost discipline (matches the sibling guards): gated by the const <see cref="CompiledIn"/> (<c>false</c> unless
/// <c>DEBUG</c>/<c>FLUENTGPU_DIAG</c>), so <c>VideoSurfaceRegistry.Bind</c>'s
/// <c>if (OneSurfacePerPlayerGuard.CompiledIn &amp;&amp; OneSurfacePerPlayerGuard.Enabled) { ... }</c> guard folds
/// away entirely in the shipping AOT binary. When compiled in it defaults ON (kill-switch
/// <c>--fg no-guards</c>) and is report-only unless <c>--fg guards-throw</c>. This
/// registry's own remarks already place it OUTSIDE the zero-alloc gate surface, but the check costs nothing extra
/// anyway: a fixed <c>MaxSurfaces</c>-slot scan over struct fields, no allocation on the passing path (the message
/// is built only inside <see cref="Violation"/>).
/// </para>
/// </summary>
public static class OneSurfacePerPlayerGuard
{
    /// <summary>Compile-time master switch — <c>false</c> in release so the registry's guard folds away.</summary>
    public const bool CompiledIn =
#if DEBUG || FLUENTGPU_DIAG
        true;
#else
        false;
#endif

    /// <summary>Runtime gate (only consulted when <see cref="CompiledIn"/>): defaults ON; <c>--fg no-guards</c> or code
    /// turns it off.</summary>
    public static bool Enabled = CompiledIn;

    /// <summary>When set, a detected violation THROWS <see cref="OneSurfacePerPlayerException"/> instead of only
    /// reporting — <c>--fg guards-throw</c>, or a gate scoping the strict path. Default report-only.</summary>
    public static bool ThrowOnViolation;

    /// <summary>Count of violations since the last <see cref="Reset"/> — the VerticalSlice gate accessor for
    /// <c>gate.media.el.one-surface-per-player</c>.</summary>
    public static int Violations { get; private set; }

    /// <summary>The most recent violation message (gate accessor).</summary>
    public static string? LastViolation { get; private set; }

    /// <summary>Reset the accumulators (between gate scenarios).</summary>
    public static void Reset() { Violations = 0; LastViolation = null; }

    /// <summary>Report two live registry slots (<paramref name="tokenA"/>, <paramref name="tokenB"/>) bound to the
    /// same <paramref name="handle"/>. Reports to <see cref="Diag.Sink"/>/stderr; throws when
    /// <see cref="ThrowOnViolation"/>.</summary>
    public static void Violation(int tokenA, int tokenB, nuint handle)
    {
        Violations++;
        string msg = $"[one-surface-per-player] tokens {tokenA} and {tokenB} are both bound to DComp handle 0x{handle:X} "
                   + "— two LIVE registry slots writing one MF swapchain (a size fight: each pump will call "
                   + "SetVideoStreamRect with its own rect). The registry enforces single-writer PER SLOT, not per "
                   + "player — share ONE VideoBinding (the fullscreen PresentationBinding hand-off idiom in "
                   + "MediaPlayerElement) instead of acquiring a second slot for the same player.";
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
        if (ThrowOnViolation) throw new OneSurfacePerPlayerException(msg);
    }
}

/// <summary>Thrown by <see cref="OneSurfacePerPlayerGuard"/> in strict mode
/// (<c>--fg guards-throw</c>) when two live registry slots were bound to the same DComp handle. Never
/// thrown in release (the guard is compiled out).</summary>
public sealed class OneSurfacePerPlayerException(string message) : InvalidOperationException(message);
