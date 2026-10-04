using System;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>The engine-invoked video pump for one binding (see <see cref="VideoSurfaceRegistry.RegisterPump"/>).
/// Given the current DIP→device <paramref name="scale"/>, it reads the live laid-out area and writes video intents /
/// drives <see cref="IMediaPlayer.PumpVideo"/> — so the control's <c>Render</c> stays a pure function.</summary>
public delegate void VideoPump(float scale);

/// <summary>
/// The UI-thread-owned video-surface intent table (F070 / F183). A component (via the <c>UseVideoSurface</c> hook) or a media
/// player declares a video surface — its rect, visibility, and the DirectComposition surface handle to bind — as POD intents on
/// the UI thread. The registry never touches the presenter: at every publication the host copies the table into a POD snapshot
/// (<see cref="SnapshotInto"/>) that the published frame carries, and the render thread's
/// <see cref="VideoPlacementApplier"/> is the sole consumer, applying it to the render-thread-confined
/// <see cref="IVideoPresenter"/> in the turn that presents that publication, so no ComPtr is ever touched off the render
/// thread and the video is placed for the frame it is presented with. Keeps the portable core TerraFX-free: it references only the
/// <see cref="IVideoPresenter"/> seam, never a D3D/DComp type.
/// </summary>
/// <remarks>
/// <para>Ownership: every member below is UI-thread-only EXCEPT the four thread-safe channels - the content mailbox the early
/// structural drain reads (<see cref="CopyMail"/>), the results the applier hands back (<see cref="PostResult"/>, folded into the
/// signals by the UI thread), the live-surface mask (<see cref="HasLiveSurface"/>) and the structural version
/// (<see cref="StructuralVersion"/>). The slot table, the pump counters and every <see cref="Signal{T}"/> are never touched by the
/// render thread.</para>
/// <para>Writes are value-gated: re-declaring an unchanged rect/visibility/handle is a no-op, so a page that calls
/// <see cref="Place"/> every render produces zero redundant presenter calls. The apply only runs on a real composited
/// device (the host guards it on a non-null <see cref="IVideoPresenter"/>), never on the headless seam — so this type
/// is outside the zero-alloc gate surface by construction.</para>
/// </remarks>
public sealed class VideoSurfaceRegistry
{
    internal const int MaxSurfaces = 16;

    private struct Entry
    {
        public bool InUse;
        public long Gen;              // this slot's incarnation (every Acquire is a new one)
        public long HandleSeq;        // advances on every handle change / forced re-bind (see VideoPresentIntent.HandleSeq)
        public RectF RectDip;         // desired content rect in DIP; the render side scales it to device px when it applies it
        public RectF ViewportDip;     // visible container; smaller than RectDip for center-crop
        public float RadiusDip;         // corner radius in DIP (0 = square). Scaled to device px at apply, like the rect.
        public uint ContentW, ContentH; // the content's native pixel size (e.g. decoder swapchain) — the presenter scales it to fill the rect (0 = unknown)
        public bool Visible;
        public int Z;
        public nuint DesiredHandle;   // the DComp surface handle to bind (0 = none produced yet)
        public bool ReleasePending;   // token released on the UI side; the render side destroys the surface, then reports it free
        public bool Presenting;       // diagnostic playback state; does NOT affect the presenter apply or host cadence
        public bool PumpPending;      // one coalesced UI-thread pump is requested for this slot
        public bool PumpFull;         // that pump was requested by a native/transport/activation edge (RequestPump), not only by moved geometry

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
    private readonly Signal<bool>[] _boundSignals;   // UI-thread-written mirror of the bound readiness the applier reports (see ImportResults)
    private readonly Signal<bool> _alwaysBound = new(true);   // per registry (never written): what Bound reports when there is no presenter to wait for
    // Any-thread census mirror (F235): per slot, the handle a LIVE (in use, not release-pending) slot carries, else 0. The UI thread is the
    // only writer (Acquire / Bind / Release / FreeSlot / DestroyAll); MediaCensus reads it from whatever thread samples, so the dual-handle
    // count never touches the UI-thread slot table.
    private readonly nuint[] _liveHandles = new nuint[MaxSurfaces];
    private long _seq;                // UI: the incarnation / handle-sequence counter
    private bool _publishDirty;       // UI: the table changed since the last snapshot, so a publication is owed (see HasUnpublishedChanges)
    private long _snapshotSeq;        // UI: counts SnapshotInto calls; stamps every intent of a snapshot (VideoPresentIntent.Seq)
    private int _presentingCount;   // diagnostic census of slots a media player is actively presenting into
    private int _pendingPumpCount;  // slots with one coalesced pump awaiting the next host frame
    private int _geometryOnlyToken;  // the token whose geometry-only pump is running right now (0 outside one); see IsGeometryOnlyPump

    // ── per-binding pump callbacks (engine-invoked each frame; replaces the control's side-effecting Render) ──────────
    private struct PumpReg { public bool InUse; public int Token; public object? Owner; public VideoPump? Pump; }
    private readonly PumpReg[] _pumps = new PumpReg[MaxSurfaces];
    private long _pumpInvocations;         // total owner pumps actually invoked (tracks requests, not renders/frames)
    private long _suppressedNonOwnerPumps; // non-owner pumps suppressed by the single-writer contract (ownership diag)

    // ── the thread-safe channels between the UI thread (this class) and the render thread's applier ───────────────────
    // ONE lock guards the two small arrays below. It is held only for a fixed-size copy (never across a Signal write or a
    // presenter call), by the UI thread on a content change / an import and by the render thread on a mailbox read / a post.
    private readonly Lock _gate = new();
    // The content mailbox: per slot, the handle / sequence / release state, WITHOUT geometry. It is what the early structural drain
    // (a handle arriving with no publication behind it, F208) adopts, so a bound surface never waits for a UI frame.
    private readonly VideoPresentIntent[] _mail = new VideoPresentIntent[MaxSurfaces];
    private int _structuralVersion;   // Interlocked: bumped on every handle the render side must create/bind (Bind, Rebind)
    // The results the applier posts: per slot the surface id it created, whether it is bound and composed, and whether a release
    // completed. The UI thread folds them into the signals and frees the slot (ImportResults).
    private struct SlotResult { public int Slot; public long Gen; public uint SurfaceId; public bool Bound; public bool ReleaseDone; public bool Dirty; }
    private readonly SlotResult[] _results = new SlotResult[MaxSurfaces];
    private int _resultsVersion;      // Interlocked: bumped on every post; the UI thread compares it with _resultsSeen
    private int _resultsSeen;         // UI-thread-only
    private int _liveMask;            // Volatile: slots that are live on screen (created, bound, visible), published by the applier
    private VideoPlacementApplier? _shim;   // the same-thread applier behind Drain/DrainStructural (see Drain)

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

    /// <summary>Reports the monitor this registry's window is on and whether it is fullscreen there (F089), for the stream sizing
    /// (<see cref="VideoStreamSizing"/>). The host sets it once at construction; unset (headless, tests) reads as
    /// <c>default</c>: no monitor size, so a stream is never upscaled. UI thread only.</summary>
    public Func<VideoDisplay>? DisplayProvider { get; set; }

    /// <summary>The current <see cref="VideoDisplay"/> (<c>default</c> when no <see cref="DisplayProvider"/> is set).</summary>
    public VideoDisplay Display => DisplayProvider?.Invoke() ?? default;

    /// <summary>Which window this registry belongs to, for the logs only: 0 is the main window, 1, 2, ... a detached pop-out (the same
    /// target id its <c>[render.pace]</c> <c>child=</c> token and <c>[detached] attach</c> line carry). The host sets it when the child
    /// is attached. With <see cref="VideoBinding.Token"/> it names WHICH slot of WHICH window wrote a stream size or a pump (F235): two
    /// windows' pumps used to differ only by <c>scale=</c>.</summary>
    public int HostOrdinal { get; set; }

    /// <summary>Copy the handle of every live slot (in use, not release-pending, handle non-zero) into <paramref name="destination"/>
    /// and return how many were written (at most <see cref="MaxSurfaces"/>, bounded by the span). Any thread, allocation-free: reads
    /// the census mirror, never the UI-thread slot table, so it is safe against a window being reaped mid-scan (a torn read sees a
    /// slot just freed or just bound, never a corrupt value).</summary>
    internal int CopyLiveHandles(Span<nuint> destination)
    {
        int n = 0;
        for (int i = 0; i < MaxSurfaces && n < destination.Length; i++)
        {
            nuint h = Volatile.Read(ref _liveHandles[i]);
            if (h != 0) destination[n++] = h;
        }
        return n;
    }

    // ── UI-thread API (the hook / the media-player façade) ─────────────────────────────────────────────────────────

    /// <summary>Reserve a surface slot. Returns a token (>0) or 0 when the pool is exhausted.</summary>
    public int Acquire()
    {
        ThreadGuard.AssertNotRender();
        ImportResults();   // a slot the render side finished releasing comes back before the search
        for (int i = 0; i < MaxSurfaces; i++)
        {
            if (_entries[i].InUse) continue;
            long gen = ++_seq;
            _entries[i] = new Entry { InUse = true, Visible = true, Gen = gen, HandleSeq = gen };
            Volatile.Write(ref _liveHandles[i], 0);
            _surfaceSignals[i].Value = default;
            _boundSignals[i].Value = false;
            lock (_gate) _mail[i] = new VideoPresentIntent { Token = i + 1, Gen = gen, HandleSeq = gen, Visible = true };
            _publishDirty = true;
            return i + 1;
        }
        return 0;
    }

    /// <summary>Set the surface's rect (DIP) and draw order. Value-gated.</summary>
    public void Place(int token, RectF rectDip, int z = 0)
    {
        ThreadGuard.AssertNotRender();
        ref Entry e = ref Slot(token);
        if (e.RectDip == rectDip && e.Z == z) return;
        e.RectDip = rectDip; e.Z = z;
        MarkDirty();
    }

    public void SetViewport(int token, RectF viewportDip)
    {
        ThreadGuard.AssertNotRender();
        ref Entry e = ref Slot(token);
        if (e.ViewportDip == viewportDip) return;
        e.ViewportDip = viewportDip;
        MarkDirty();
    }

    /// <summary>Set the content's native pixel size (decoder swapchain size) so the presenter can scale it to fill the
    /// rect instead of showing it 1:1 (cropped). Value-gated.</summary>
    public void SetContentSize(int token, uint width, uint height)
    {
        ThreadGuard.AssertNotRender();
        ref Entry e = ref Slot(token);
        if (e.ContentW == width && e.ContentH == height) return;
        e.ContentW = width; e.ContentH = height;
        MarkDirty();
    }

    /// <summary>Round the composited surface's corners (DIP; 0 = square). The video child visual composites outside the
    /// UI back buffer, so a UI-side rounded parent cannot clip it — this is the only way to get a non-rectangular video.
    /// Value-gated.</summary>
    public void SetCornerRadius(int token, float radiusDip)
    {
        ThreadGuard.AssertNotRender();
        ref Entry e = ref Slot(token);
        if (e.RadiusDip == radiusDip) return;
        e.RadiusDip = radiusDip;
        MarkDirty();
    }

    /// <summary>Show/hide the surface. Value-gated.</summary>
    public void SetVisible(int token, bool visible)
    {
        ThreadGuard.AssertNotRender();
        ref Entry e = ref Slot(token);
        if (e.Visible == visible) return;
        e.Visible = visible;
        MarkDirty();
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
    /// the frame moves under it. The applier publishes the mask after every apply, so it is safe to read from any thread (the
    /// render thread reads it for the weak tier's upload budget); it describes the last applied frame. O(1), zero-alloc.</summary>
    public bool HasLiveSurface => Volatile.Read(ref _liveMask) != 0;

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
        Volatile.Write(ref _liveHandles[token - 1], e.ReleasePending ? 0 : dcompSurfaceHandle);
        NoteHandle(token - 1, ref e);
        MarkDirty();
        if (dcompSurfaceHandle != 0) NoteStructural();
        if (OneSurfacePerPlayerGuard.CompiledIn && OneSurfacePerPlayerGuard.Enabled && dcompSurfaceHandle != 0)
            CheckOneSurfacePerPlayer(token, dcompSurfaceHandle);
    }

    /// <summary>Called on the UI thread when a handle arrives that the render side must create/bind (<see cref="Bind"/>,
    /// <see cref="Rebind"/>): the host wires it to wake the render thread that owns this registry's presenter, so its early
    /// structural drain (<see cref="VideoPlacementApplier.ApplyStructural"/>) runs on the next turn even when no UI frame
    /// publication follows (F208). Null = nothing to wake (headless / single-thread host). Must be cheap and thread-safe.</summary>
    public Action? StructuralWake { get; set; }

    /// <summary>Bumped (Interlocked) on every handle the render side must create or bind. The applier compares it with the version it
    /// last adopted from the mailbox: the O(1), any-thread gate of the early drain.</summary>
    internal int StructuralVersion => Volatile.Read(ref _structuralVersion);

    /// <summary>True while the same-thread shim still owes an early (structural) drain: a handle it has not adopted, or a live slot
    /// whose surface or bind is still pending. Test / single-thread surface; the threaded host asks its own applier
    /// (<see cref="VideoPlacementApplier.HasStructuralWork"/>).</summary>
    public bool HasStructuralWork => Shim.HasStructuralWork;

    private void NoteStructural()
    {
        Interlocked.Increment(ref _structuralVersion);
        StructuralWake?.Invoke();
    }

    // The handle half of the content mailbox: what an early structural drain adopts without waiting for a publication.
    private void NoteHandle(int i, ref Entry e)
    {
        e.HandleSeq = ++_seq;
        lock (_gate)
        {
            _mail[i].DesiredHandle = e.DesiredHandle;
            _mail[i].HandleSeq = e.HandleSeq;
        }
    }

    /// <summary>Make the next apply bind this slot's current handle again, even though it did not change (the producer
    /// rebuilt the surface behind the same handle value, or a failed bind should be retried at once). No-op while no
    /// handle has been bound.</summary>
    public void Rebind(int token)
    {
        ref Entry e = ref Slot(token);
        if (e.DesiredHandle == 0) return;
        NoteHandle(token - 1, ref e);   // the same handle under a newer sequence: the render side wraps it again
        MarkDirty();
        NoteStructural();
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

    /// <summary>Release the token: the render side tears down the presenter surface on the turn that presents this release, then
    /// reports it complete and the UI thread frees the slot (the next <see cref="Acquire"/> or <see cref="PumpPending"/>).</summary>
    public void Release(int token)
    {
        ThreadGuard.AssertNotRender();
        int i = token - 1;
        if ((uint)i >= MaxSurfaces || !_entries[i].InUse) return;
        if (_entries[i].Presenting) { _entries[i].Presenting = false; _presentingCount--; }
        ClearPumpPending(ref _entries[i]);
        _entries[i].ReleasePending = true;
        Volatile.Write(ref _liveHandles[i], 0);
        lock (_gate) _mail[i].ReleasePending = true;
        MarkDirty();
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

    /// <summary>True when at least one coalesced video pump must run on the next host frame, or the render side posted results
    /// the UI thread has not folded in yet (a surface became bound, a release completed). O(1), zero-alloc.</summary>
    public bool HasPendingPumps => _pendingPumpCount > 0 || (RequireSlotBinding && Volatile.Read(ref _resultsVersion) != _resultsSeen);

    /// <summary>True when the table changed since the last <see cref="SnapshotInto"/>: a publication is owed so the render side sees the
    /// change even when nothing else in the frame moved (a native event that only changes the video's rect or visibility).</summary>
    internal bool HasUnpublishedChanges => _publishDirty;

    /// <summary>True while any slot's release is waiting for the render side to destroy it. A destroy stays coupled to the UI
    /// frame's present (the two-clock tear lock), so such a table never rides a video-only post (F098).</summary>
    internal bool HasReleasePending
    {
        get
        {
            for (int i = 0; i < MaxSurfaces; i++)
                if (_entries[i].InUse && _entries[i].ReleasePending) return true;
            return false;
        }
    }

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
    /// published with this same frame.
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
    /// settled and before the publication that carries the result. Clearing the pending bit before invoking allows a re-entrant native
    /// event to request exactly one FOLLOWING pump. Zero managed allocation: fixed arrays and mount-registered delegates.</summary>
    public void PumpPending(float scale)
    {
        ThreadGuard.AssertNotRender();
        ImportResults();
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

    // ── Publication snapshot (UI thread, at PublishScene / Publish) ────────────────────────────────────────────────

    /// <summary>Copy every live slot's intent into <paramref name="destination"/> (the published frame's block, capacity
    /// <see cref="MaxSurfaces"/>) and clear <see cref="HasUnpublishedChanges"/>. The snapshot is STATE: every publication carries the
    /// full intent of every live slot, so a publication the render thread skips is superseded by the next one and a release or a
    /// bind is never dropped (see <see cref="VideoPresentIntent"/>). Returns the number of entries written. The publisher calls it on the
    /// UI thread (it asserts that); the same-thread shim calls it on the presenting thread. Zero-alloc.</summary>
    internal int SnapshotInto(Span<VideoPresentIntent> destination)
    {
        long seq = ++_snapshotSeq;
        int n = 0;
        for (int i = 0; i < MaxSurfaces && n < destination.Length; i++)
        {
            ref Entry e = ref _entries[i];
            if (!e.InUse) continue;
            ref VideoPresentIntent it = ref destination[n++];
            it = new VideoPresentIntent
            {
                Token = i + 1, Gen = e.Gen, HandleSeq = e.HandleSeq, DesiredHandle = e.DesiredHandle,
                ReleasePending = e.ReleasePending, HasGeometry = true, Seq = seq, Visible = e.Visible,
                RectDip = e.RectDip, ViewportDip = e.ViewportDip, RadiusDip = e.RadiusDip,
                ContentW = e.ContentW, ContentH = e.ContentH, Z = e.Z,
            };
            // The hole this surface sits behind, as the UI sees it NOW: only when the tracked node's rect is the rect the owner
            // placed (the hole node itself - the usual Uniform / Fill case). An owner that places a different rect than the node it
            // follows (a refit, a clamp, a crop) gets no pose correction rather than a constant offset.
            if (!e.GeomNode.IsNull && MathF.Abs(e.GeomRect.X - e.RectDip.X) <= 0.5f && MathF.Abs(e.GeomRect.Y - e.RectDip.Y) <= 0.5f)
            {
                it.HasHoleOrigin = true;
                it.HoleX = e.GeomRect.X;
                it.HoleY = e.GeomRect.Y;
                it.HoleW = e.GeomRect.W;
                it.HoleH = e.GeomRect.H;   // tells this hole from another that shares the token (the applier's posed-hole lookup)
            }
        }
        _publishDirty = false;
        return n;
    }

    // ── Render → UI channels (thread-safe) ─────────────────────────────────────────────────────────────────────────

    /// <summary>The content mailbox, for the render thread's early structural drain (<see cref="VideoPlacementApplier.ApplyStructural"/>):
    /// copies the live slots' handle / sequence / release state (no geometry) into <paramref name="destination"/> and returns the
    /// structural version they are current to, read under the same lock. Any thread.</summary>
    internal int CopyMail(VideoPresentIntent[] destination, out int count)
    {
        int n = 0;
        lock (_gate)
        {
            int version = Volatile.Read(ref _structuralVersion);
            for (int i = 0; i < MaxSurfaces && n < destination.Length; i++)
                if (_mail[i].Token != 0) destination[n++] = _mail[i];
            count = n;
            return version;
        }
    }

    /// <summary>The render thread's report on one slot of incarnation <paramref name="gen"/>: the presenter surface it created (0 =
    /// none), whether that surface is bound AND composed, and - <paramref name="releaseDone"/> - that the slot's release completed so
    /// the UI thread may free it. Latest state per slot wins; a completed release is sticky. The UI thread folds it in
    /// (<see cref="ImportResults"/>); this never touches a signal. Any thread.</summary>
    internal void PostResult(int slot, long gen, uint surfaceId, bool bound, bool releaseDone)
    {
        lock (_gate)
        {
            ref SlotResult r = ref _results[slot];
            r.Slot = slot;
            r.Gen = gen;
            r.SurfaceId = surfaceId;
            r.Bound = bound;
            r.ReleaseDone |= releaseDone;
            r.Dirty = true;
        }
        Interlocked.Increment(ref _resultsVersion);
    }

    /// <summary>The applier's live-surface mask (slot bits): created, bound and visible as of the frame it last applied.</summary>
    internal void SetLiveMask(int mask) => Volatile.Write(ref _liveMask, mask);

    /// <summary>UI thread: fold the render thread's posted results into the signals a media element subscribes to, and free the slots
    /// whose release completed. A result whose incarnation is no longer the slot's own (the slot was freed and re-acquired) is dropped.</summary>
    private void ImportResults()
    {
        int version = Volatile.Read(ref _resultsVersion);
        if (version == _resultsSeen) return;
        Span<SlotResult> local = stackalloc SlotResult[MaxSurfaces];
        int n = 0;
        lock (_gate)
        {
            for (int i = 0; i < MaxSurfaces; i++)
            {
                ref SlotResult r = ref _results[i];
                if (!r.Dirty) continue;
                r.Dirty = false;
                local[n++] = r;
                if (r.ReleaseDone) r = default;
            }
        }
        _resultsSeen = version;
        for (int k = 0; k < n; k++)
        {
            ref SlotResult r = ref local[k];
            ref Entry e = ref _entries[r.Slot];
            if (!e.InUse || e.Gen != r.Gen) continue;
            _surfaceSignals[r.Slot].Value = new VideoSurfaceId(r.SurfaceId);
            if (RequireSlotBinding) _boundSignals[r.Slot].Value = r.Bound;
            if (r.ReleaseDone) FreeSlot(r.Slot);
        }
    }

    /// <summary>Free a slot whose release the render side completed (UI thread): keeps the wake counters and readiness mirror balanced.</summary>
    private void FreeSlot(int i)
    {
        ref Entry e = ref _entries[i];
        if (e.Presenting) _presentingCount--;   // keep the wake counter balanced when a presenting slot is freed
        ClearPumpPending(ref e);
        _surfaceSignals[i].Value = default;
        _boundSignals[i].Value = false;
        lock (_gate) _mail[i] = default;
        Volatile.Write(ref _liveHandles[i], 0);
        e = default;   // free the slot
    }

    // ── Same-thread shim (the single-thread host, and the direct test callers) ─────────────────────────────────────────

    private VideoPlacementApplier Shim => _shim ??= new VideoPlacementApplier(this);

    private int _shimCount;
    private readonly VideoPresentIntent[] _shimIntents = new VideoPresentIntent[MaxSurfaces];

    /// <summary>Apply the table AS IT IS NOW to <paramref name="presenter"/> and issue at most one
    /// <see cref="IVideoPresenter.Commit"/>: a same-thread convenience for the single-thread host (the UI thread IS the presenting
    /// thread there, so the live table and the frame cannot disagree) and for tests that drive a presenter by hand. It snapshots the
    /// live table and runs its own <see cref="VideoPlacementApplier"/>, exactly the code the threaded host runs on the render
    /// thread against the PUBLISHED snapshot - the threaded host never calls this. Not for use while a render thread consumes this
    /// registry's publications.
    /// <paramref name="scale"/> is the window DIP→device-px factor. Returns true when this call changed the published per-slot readiness
    /// (<see cref="Bound"/> edge).
    /// <para><paramref name="deferCommit"/> (F080): apply through <see cref="IVideoPresenter.ApplyPending"/> and leave the
    /// device-level commit to the host, which makes ONE per render turn after the parent and every detached child drained and
    /// then calls <see cref="PublishCommitted"/> on each registry. <paramref name="placement"/> false is the turn whose
    /// present stood down (cloaked / minimized / occluded: nothing visible): only token releases are applied (a hidden window
    /// must not hold its slots), every placement stays pending for the next real present.</para></summary>
    public bool Drain(IVideoPresenter presenter, float scale, bool deferCommit = false, bool placement = true)
    {
        int version = StructuralVersion;
        _shimCount = SnapshotInto(_shimIntents);
        return Shim.ApplyTurn(presenter, _shimIntents.AsSpan(0, _shimCount), default, scale,
            placement ? VideoApplyScope.Full : VideoApplyScope.ReleasesOnly, deferCommit, version);
    }

    /// <summary>The early half of the apply (F208), same-thread form: create the surface and bind the handle for entries that have
    /// a handle but no (or a different) bound one, and place a surface the instant it is created. NOT applied here (they stay coupled to
    /// the presenting turn, the two-clock tear lock): <see cref="Place"/>-class moves of an existing surface and <see cref="Release"/>
    /// destroys. A presenter that cannot attach yet (<see cref="IVideoPresenter.CanAttachSurfaces"/>) is left to the first coupled
    /// apply. The threaded host runs <see cref="VideoPlacementApplier.ApplyStructural"/> instead, off the content mailbox.
    /// Returns true on a <see cref="Bound"/> readiness edge, like <see cref="Drain"/>.</summary>
    public bool DrainStructural(IVideoPresenter presenter, float scale, bool deferCommit = false)
    {
        if (!Shim.HasStructuralWork) return false;
        if (!presenter.CanAttachSurfaces) return false;
        int version = StructuralVersion;
        _shimCount = SnapshotInto(_shimIntents);
        return Shim.ApplyTurn(presenter, _shimIntents.AsSpan(0, _shimCount), default, scale,
            VideoApplyScope.Structural, deferCommit, version);
    }

    /// <summary>After the host's device-level commit for changes applied with <c>deferCommit</c>: publish which slots are now
    /// bound AND composed (the readiness only counts once the commit ran). Returns true on a <see cref="Bound"/> edge, so the
    /// host wakes the UI loop. No-op when nothing was applied since the last call. Same-thread shim form.</summary>
    public bool PublishCommitted() => _shim is not null && _shim.PublishCommitted();

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

    /// <summary>Tear down every live surface (device teardown / host dispose) and free every slot. Same-thread shim form: the caller
    /// is the presenting thread and no render thread consumes this registry any more.</summary>
    public void DestroyAll(IVideoPresenter presenter)
    {
        Shim.DestroyAll(presenter);
        for (int i = 0; i < MaxSurfaces; i++)
        {
            _surfaceSignals[i].Value = default;
            _boundSignals[i].Value = false;
            _entries[i] = default;
            Volatile.Write(ref _liveHandles[i], 0);
        }
        lock (_gate)
        {
            Array.Clear(_mail);
            Array.Clear(_results);
        }
        _publishDirty = false;
        _presentingCount = 0;
        _pendingPumpCount = 0;
        Volatile.Write(ref _liveMask, 0);
    }

    private void MarkDirty() => _publishDirty = true;
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

    /// <summary>Which window the surface is presented in, for the logs: 0 main, 1, 2, ... a pop-out (see
    /// <see cref="VideoSurfaceRegistry.HostOrdinal"/>); 0 for an inert binding.</summary>
    public int HostOrdinal => _registry?.HostOrdinal ?? 0;

    /// <summary>The monitor this binding's window is on and whether it is fullscreen (F089): what the stream sizing needs to
    /// upscale in Media Foundation rather than DirectComposition. <c>default</c> (monitor unknown) for an inert binding or a
    /// host that reports none. UI thread only.</summary>
    public VideoDisplay Display => _registry?.Display ?? default;

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
/// <para>
/// The shipping build has the same question answered by <see cref="MediaCensus.CountDualHandleSlots()"/> (F235): an always-on,
/// allocation-free scan of every window's registry (the main window's AND each pop-out's, which this per-registry tripwire can never
/// see across), shown in <c>mem.sample</c> and the <c>[memcensus]</c> media line.
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
