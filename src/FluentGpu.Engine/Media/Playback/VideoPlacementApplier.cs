using System;
using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Media;

/// <summary>
/// One video slot's intent as of ONE publication (F070 / F183): the POD copy <see cref="VideoSurfaceRegistry.SnapshotInto"/>
/// makes at publish and the published frame carries to the render thread, which never reads the UI-owned registry again.
/// A snapshot is STATE, not an event log: every publication carries the full intent of every live slot, so a publication the
/// render thread skipped loses nothing (the next one supersedes it), and a <c>ReleasePending</c> or a handle bind can never be
/// dropped by a skip. The two event-like intents are made idempotent by a sequence number: <see cref="HandleSeq"/> advances on
/// every handle change or forced re-bind, <see cref="Gen"/> names the slot's incarnation.
/// </summary>
internal struct VideoPresentIntent
{
    public int Token;                // slot index + 1; 0 = unused entry
    public long Gen;                 // the incarnation (every Acquire is a new one): a stale snapshot of a freed slot never resurrects it
    public long HandleSeq;           // advances on every handle change / forced re-bind; the render side adopts a handle only from a newer one
    public nuint DesiredHandle;      // the DComp surface handle to bind (0 = none produced yet)
    public bool ReleasePending;      // the UI released the token: the render side destroys the surface, then reports the slot free
    public bool HasGeometry;         // the fields below are authoritative (a publication snapshot); false for the content-only mailbox
    public bool Visible;
    public bool HasHoleOrigin;       // HoleX/HoleY: the window-DIP origin of the hole this surface sits behind, as of this publication
    public float HoleX, HoleY;
    public float HoleW, HoleH;       // the hole's size: tells the owner's hole from another hole that shares the token (fullscreen hand-off)
    public RectF RectDip;           // desired content rect in DIP
    public RectF ViewportDip;        // visible container; smaller than RectDip for center-crop
    public float RadiusDip;
    public uint ContentW, ContentH;
    public int Z;
}

/// <summary>One video hole as the render thread's composite placed it this turn (<c>SliceRecorder.PlaceVideoErases</c>): the hole's
/// UNCLIPPED rect at the slot's posed offset and the composite clip it was cut by (window DIP).
/// <see cref="VideoPlacementApplier"/> moves the video by the hole's own travel since the publication, so a scroll or compositor
/// animation the render thread posed after the UI published moves the video exactly as far as the hole.</summary>
internal struct VideoPosedHole
{
    public int Token;
    public RectF Hole;
    public RectF EffClip;
}

/// <summary>What one <see cref="VideoPlacementApplier.ApplyTurn"/> may touch.</summary>
internal enum VideoApplyScope : byte
{
    /// <summary>A turn that presents (or whose frame is already on the glass): content AND geometry AND releases.</summary>
    Full,
    /// <summary>A turn that elided its record (same publication, nothing moved): create / bind only, a just-created surface placed at
    /// the published rect (never left visible and unplaced). Moves of an existing surface and destroys are held for the turn that
    /// presents the matching hole.</summary>
    ContentOnly,
    /// <summary>A turn whose present stood down (cloaked / minimized / occluded): only releases, so a hidden window still frees its
    /// slots; every placement stays pending for the next real present.</summary>
    ReleasesOnly,
    /// <summary>The early drain (F208): create the surface, bind the handle and place a surface the instant it is created when a
    /// publication has delivered its geometry; a surface created before that is kept hidden until the first publication places it.
    /// Moves of an existing surface and destroys stay coupled to the UI frame's present (the two-clock tear lock).</summary>
    Structural,
}

/// <summary>
/// The render thread's SOLE consumer of video placement (F070 / F183). It owns everything the old registry drain wrote from the
/// render thread - the presenter surface ids, the handle each surface is bound to, the failed-bind backoff, the bound-readiness
/// bits - in a render-private shadow, and it is fed only POD: the intents the published frame carries
/// (<see cref="VideoPresentIntent"/>), the holes this turn's composite posed (<see cref="VideoPosedHole"/>), and the registry's
/// content mailbox for the early structural drain. It hands results back to the UI thread through
/// <see cref="VideoSurfaceRegistry.PostResult"/> (surface id, bound readiness, release complete), which the UI thread folds into
/// its signals; it never touches a Signal, the registry's slot table or its pump counters.
/// <para>Geometry is applied from the frame being presented: the intent's rect is moved by the hole's posed travel
/// (<c>posed hole origin - the hole origin the UI published</c>) and its viewport re-clipped by the posed composite clip.</para>
/// <para>Thread: render only. The host asserts that at each call site; the registry's same-thread shim
/// (<see cref="VideoSurfaceRegistry.Drain"/>) drives its own instance for the single-thread host and for tests.</para>
/// </summary>
internal sealed class VideoPlacementApplier
{
    private const int MaxSurfaces = VideoSurfaceRegistry.MaxSurfaces;
    // Position changes smaller than this (DIP) are the same placement: a pose that only jitters in the last float digit is no motion.
    private const float PoseEpsilonDip = 0.01f;
    // Two holes are the same size when their width and height agree within this (DIP): how a shared token's owner hole is told apart.
    private const float HoleSizeToleranceDip = 0.5f;

    private struct Slot
    {
        public bool InUse;
        public VideoPresentIntent Intent;   // the adopted intent: content merged by HandleSeq, geometry by publication
        public bool HasGeometry;            // a publication has delivered Intent's geometry
        public VideoSurfaceId SurfaceId;    // none until first created
        public nuint BoundHandle;           // last handle actually bound (set only once the presenter reports success)
        public int BindFailures;            // consecutive failed BindSurfaceHandle calls for the current DesiredHandle
        public uint NextBindDrain;          // the first apply sequence number allowed to retry a failed bind (backoff)
        public bool Dirty;                  // an intent changed (or a bind is owed): the next full apply re-applies it
        public bool GeomDirty;              // the placement itself changed (rect, viewport, pose, scale...): a geometry move is owed
        public bool Placed;                 // the presenter has been given a placement at least once
        public float AppliedDx, AppliedDy, AppliedScale;
        public RectF AppliedClip;           // the composite clip the placement was last cut by (Infinite = none)
    }

    private readonly VideoSurfaceRegistry _registry;
    private readonly Slot[] _slots = new Slot[MaxSurfaces];
    private readonly long[] _freedGen = new long[MaxSurfaces];   // the newest incarnation of each slot this side finished with
    private readonly VideoPresentIntent[] _mail = new VideoPresentIntent[MaxSurfaces];
    private IVideoPresenter? _drainedBy;   // the presenter the last apply targeted; a different instance means the old one's DComp objects are gone
    private uint _drainSeq;                // applies that did work; the clock the failed-bind backoff counts in
    private int _boundBits;                // slots with a bound, committed surface (render-owned; the UI sees it through PostResult)
    private bool _boundChanged;            // this apply changed _boundBits
    private bool _publishDue;              // changes were applied with the device Commit deferred: readiness waits for PublishCommitted
    private bool _anyDirty;
    private bool _structuralPending;       // a live slot still needs its surface created or its handle bound
    private int _seenStructuralVersion;    // the registry's structural version this side has adopted
    private bool _lastTurnMoved;

    internal VideoPlacementApplier(VideoSurfaceRegistry registry) { _registry = registry; }

    /// <summary>Counter (render thread writes): applies that moved an already-placed surface.</summary>
    internal long MotionTurns { get; private set; }

    /// <summary>The O(1) gate of the early drain: a handle the UI raised that this side has not adopted yet, or a live slot whose
    /// surface or bind is still owed (a failed create or bind, a presenter swap). A hint: the full apply does the same work.</summary>
    internal bool HasStructuralWork => _structuralPending || _registry.StructuralVersion != _seenStructuralVersion;

    /// <summary>True when the last <see cref="ApplyTurn"/> moved a surface that was already placed (a drag, a scroll pose, a resize):
    /// the turn the host commits at once, right after the present that carries the matching hole (Stage B).</summary>
    internal bool LastTurnMovedGeometry => _lastTurnMoved;

    // ── intake ────────────────────────────────────────────────────────────────────────────────────────────────────

    // Merge a batch of intents into the shadow. Idempotent: the same publication may be adopted again on an elided turn, and an
    // OLDER source (a stale publication, the mailbox) never overwrites what a newer one delivered.
    private void Adopt(ReadOnlySpan<VideoPresentIntent> intents)
    {
        for (int n = 0; n < intents.Length; n++)
        {
            ref readonly VideoPresentIntent src = ref intents[n];
            int i = src.Token - 1;
            if ((uint)i >= MaxSurfaces) continue;
            ref Slot s = ref _slots[i];
            if (!s.InUse || src.Gen > s.Intent.Gen)
            {
                if (src.Gen <= _freedGen[i]) continue;   // a snapshot of an incarnation this side already finished must not resurrect the slot
                s = default;
                s.InUse = true;
                s.Dirty = true;
                s.Intent.Token = src.Token;
                s.Intent.Gen = src.Gen;
                s.Intent.Visible = true;
                _anyDirty = true;
            }
            else if (src.Gen < s.Intent.Gen) continue;

            if (src.HandleSeq > s.Intent.HandleSeq)
            {
                // The same non-zero handle value under a newer sequence is a forced re-bind (the producer rebuilt its surface behind
                // an unchanged handle): the presenter must wrap it again. A different handle just becomes the desired one.
                bool rebind = src.DesiredHandle != 0 && src.DesiredHandle == s.Intent.DesiredHandle;
                s.Intent.DesiredHandle = src.DesiredHandle;
                s.Intent.HandleSeq = src.HandleSeq;
                if (rebind) { s.BoundHandle = 0; s.BindFailures = 0; s.NextBindDrain = 0; }
                s.Dirty = true;
                _anyDirty = true;
            }
            if (src.ReleasePending && !s.Intent.ReleasePending)
            {
                s.Intent.ReleasePending = true;
                s.Dirty = true;
                _anyDirty = true;
            }
            if (src.HasGeometry && (!s.HasGeometry || GeometryChanged(in s.Intent, in src)))
            {
                CopyGeometry(ref s.Intent, in src);
                s.HasGeometry = true;
                s.Dirty = true;
                s.GeomDirty = true;
                _anyDirty = true;
            }
        }
    }

    private static bool GeometryChanged(in VideoPresentIntent a, in VideoPresentIntent b)
        => a.RectDip != b.RectDip || a.ViewportDip != b.ViewportDip || a.RadiusDip != b.RadiusDip
           || a.ContentW != b.ContentW || a.ContentH != b.ContentH || a.Visible != b.Visible || a.Z != b.Z
           || a.HasHoleOrigin != b.HasHoleOrigin || a.HoleX != b.HoleX || a.HoleY != b.HoleY || a.HoleW != b.HoleW || a.HoleH != b.HoleH;

    private static void CopyGeometry(ref VideoPresentIntent dst, in VideoPresentIntent src)
    {
        dst.RectDip = src.RectDip; dst.ViewportDip = src.ViewportDip; dst.RadiusDip = src.RadiusDip;
        dst.ContentW = src.ContentW; dst.ContentH = src.ContentH; dst.Visible = src.Visible; dst.Z = src.Z;
        dst.HasHoleOrigin = src.HasHoleOrigin; dst.HoleX = src.HoleX; dst.HoleY = src.HoleY; dst.HoleW = src.HoleW; dst.HoleH = src.HoleH;
        dst.HasGeometry = true;
    }

    // The posed hole this intent's surface sits behind. A token normally names ONE hole; the sanctioned fullscreen hand-off shares one
    // binding between the inline element and the overlay and both holes are live, so several holes can carry it. Then the owner's hole
    // is the one whose size is the size the UI published; with no unique match there is no pose (and no clip) rather than a guess that
    // would drag the video by another hole's travel.
    private static bool TryGetPosed(ReadOnlySpan<VideoPosedHole> posed, in VideoPresentIntent it, out VideoPosedHole hole)
    {
        int tokenCount = 0, sizeCount = 0;
        VideoPosedHole any = default, sized = default;
        for (int k = 0; k < posed.Length; k++)
        {
            ref readonly VideoPosedHole h = ref posed[k];
            if (h.Token != it.Token) continue;
            tokenCount++;
            any = h;
            if (MathF.Abs(h.Hole.W - it.HoleW) <= HoleSizeToleranceDip && MathF.Abs(h.Hole.H - it.HoleH) <= HoleSizeToleranceDip)
            {
                sizeCount++;
                sized = h;
            }
        }
        if (tokenCount == 1) { hole = any; return true; }
        if (tokenCount > 1 && sizeCount == 1) { hole = sized; return true; }
        hole = default;
        return false;
    }

    // The posed hole moved (or the scale changed) since the placement this slot was last given: owe it a geometry move. The pose is
    // read off THIS turn's composite, so a composite-only scroll turn or a compositor-animation turn moves the video with the hole.
    private void MarkPosedDirty(ReadOnlySpan<VideoPosedHole> posed, float scale)
    {
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse || !s.Placed || !s.HasGeometry || s.SurfaceId.IsNone || s.Intent.ReleasePending) continue;
            float dx = 0f, dy = 0f;
            RectF clip = RectF.Infinite;
            if (s.Intent.HasHoleOrigin && TryGetPosed(posed, in s.Intent, out VideoPosedHole ph))
            {
                dx = ph.Hole.X - s.Intent.HoleX;
                dy = ph.Hole.Y - s.Intent.HoleY;
                clip = ph.EffClip;
            }
            if (MathF.Abs(dx - s.AppliedDx) <= PoseEpsilonDip && MathF.Abs(dy - s.AppliedDy) <= PoseEpsilonDip
                && s.AppliedScale == scale && clip == s.AppliedClip) continue;
            s.Dirty = true;
            s.GeomDirty = true;
            _anyDirty = true;
        }
    }

    /// <summary>Before the present: adopt the frame's intents and work out whether the apply that follows will MOVE a surface that is
    /// already placed. The host uses it to arm the present's fence wait (Stage B), so the new hole and the new video geometry reach
    /// DWM in one composition. Idempotent with the <see cref="ApplyTurn"/> that follows.</summary>
    internal bool PrepareGeometry(ReadOnlySpan<VideoPresentIntent> intents, ReadOnlySpan<VideoPosedHole> posed, float scale)
    {
        Adopt(intents);
        if (scale <= 0f) scale = 1f;
        MarkPosedDirty(posed, scale);
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (s.InUse && s.Placed && s.GeomDirty && s.HasGeometry && !s.Intent.ReleasePending && !s.SurfaceId.IsNone) return true;
        }
        return false;
    }

    // ── apply ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The early structural drain (F208): adopt the registry's content mailbox (handle, sequence, release - never geometry)
    /// and create / bind what is owed, placing a surface only when a publication has already delivered its geometry (a surface
    /// created without it is hidden until the first publication places it). A presenter
    /// that cannot attach yet (<see cref="IVideoPresenter.CanAttachSurfaces"/>) is left to the first coupled apply. Returns true on a
    /// bound-readiness edge (the host wakes the UI loop), like <see cref="ApplyTurn"/>.</summary>
    internal bool ApplyStructural(IVideoPresenter presenter, float scale, bool deferCommit)
    {
        if (!HasStructuralWork) return false;
        if (!presenter.CanAttachSurfaces) return false;
        return ApplyMailbox(presenter, scale, VideoApplyScope.Structural, deferCommit, default);
    }

    /// <summary>Apply the registry's content mailbox (handle, sequence, release - no geometry) under <paramref name="scope"/>. The
    /// early structural drain's body, and the way a render-failed pop-out that publishes no more frames still creates, binds and
    /// RELEASES its surfaces (its shadow keeps the geometry of the last frame it drew).</summary>
    internal bool ApplyMailbox(IVideoPresenter presenter, float scale, VideoApplyScope scope, bool deferCommit, ReadOnlySpan<VideoPosedHole> posed)
    {
        int version = _registry.CopyMail(_mail, out int count);   // the version is read in the same lock as the copy
        bool edge = ApplyTurn(presenter, _mail.AsSpan(0, count), posed, scale, scope, deferCommit);
        _seenStructuralVersion = version;
        return edge;
    }

    /// <summary>Apply this turn's intents to <paramref name="presenter"/>. <paramref name="intents"/> is the presented frame's snapshot;
    /// <paramref name="posed"/> the holes the turn's composite placed (empty when the turn recorded none); <paramref name="scale"/> the
    /// presented frame's DIP-to-device factor. At most one <see cref="IVideoPresenter.Commit"/> (or, with
    /// <paramref name="deferCommit"/>, one <see cref="IVideoPresenter.ApplyPending"/> for the host's one device commit and a
    /// <see cref="PublishCommitted"/> after it). Returns true when this call changed the published per-slot readiness (the bound edge).
    /// <para><paramref name="liveStructuralVersion"/> of 0 or more marks <paramref name="intents"/> as read live from the registry at
    /// that structural version (the same-thread shim), so the early-drain hint can settle.</para></summary>
    internal bool ApplyTurn(IVideoPresenter presenter, ReadOnlySpan<VideoPresentIntent> intents, ReadOnlySpan<VideoPosedHole> posed,
        float scale, VideoApplyScope scope, bool deferCommit, int liveStructuralVersion = -1)
    {
        _boundChanged = false;
        _lastTurnMoved = false;
        Adopt(intents);
        if (liveStructuralVersion >= 0) _seenStructuralVersion = liveStructuralVersion;
        // A different presenter instance means the previous one was disposed (device recovery rebuilt the DirectComposition
        // device): every surface it held is gone. Reset BEFORE the nothing-dirty early-out, because a recovered device brings no new
        // intent - the live slots must re-create, re-bind and re-place on the new presenter by themselves.
        if (!ReferenceEquals(presenter, _drainedBy))
        {
            if (_drainedBy is not null) ResetRenderSide();
            _drainedBy = presenter;
        }
        if (scale <= 0f) scale = 1f;
        if (scope == VideoApplyScope.Full) MarkPosedDirty(posed, scale);
        if (!_anyDirty)
        {
            if (scope == VideoApplyScope.Structural) _structuralPending = AnyNeedsStructural();
            PublishLive();
            return _boundChanged;
        }
        _drainSeq++;
        bool changed = false;

        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse || !s.Dirty) continue;
            bool announce = false;   // this apply created or (re)bound the surface: say where it was placed, once

            if (s.Intent.ReleasePending)
            {
                if (scope is VideoApplyScope.Structural or VideoApplyScope.ContentOnly) continue;   // destroy stays coupled to the presenting turn (else a backdrop flash)
                if (!s.SurfaceId.IsNone) { presenter.Destroy(s.SurfaceId); changed = true; }
                FreeSlot(i);
                continue;
            }
            if (scope == VideoApplyScope.ReleasesOnly) continue;   // a stood-down present: placements stay dirty for the next real one
            if ((scope == VideoApplyScope.Structural || scope == VideoApplyScope.ContentOnly) && !NeedsStructural(in s)) continue;

            bool created = false;
            // Create the child visual on first use, once a handle exists to bind.
            if (s.SurfaceId.IsNone)
            {
                if (s.Intent.DesiredHandle == 0) { s.Dirty = false; continue; }   // nothing to show yet; wait for a handle
                s.SurfaceId = presenter.CreateSurface();
                if (s.SurfaceId.IsNone)
                {
                    // The presenter's own native call failed (device-lost/removed, out of DComp resources, ...); it is non-throwing
                    // (DCompVideoPresenter.Ok) and already logged once for this HRESULT. Leave the slot Dirty so THIS slot retries
                    // CreateSurface on the next apply instead of the surface silently never appearing.
                    continue;
                }
                PostState(i);
                changed = true;
                announce = true;
                created = true;
                // Always-on (one line per surface, ever): a pop-out that stays black cannot be told apart from one
                // whose child visual was never created without this - 2026-09-22.
                Diag.Line($"[video.surface] create token={i + 1} id={s.SurfaceId.Value}");
                if (Diag.CompiledIn && Diag.Enabled) Diag.Event("drm-reg", $"CreateSurface -> id={s.SurfaceId.Value}");
            }

            if (s.Intent.DesiredHandle != 0 && s.Intent.DesiredHandle != s.BoundHandle && _drainSeq >= s.NextBindDrain)
            {
                // BoundHandle records only a bind the presenter reports as done: a transient CreateSurfaceFromHandle /
                // SetContent failure leaves the slot dirty and retries with a growing backoff (2, 4 ... 64 applies), so
                // a handle that is never re-raised still reaches the screen once the device lets it.
                if (presenter.BindSurfaceHandle(s.SurfaceId, s.Intent.DesiredHandle))
                {
                    s.BoundHandle = s.Intent.DesiredHandle;
                    changed = true;
                    announce = true;
                    // Always-on, one line per HANDLE CHANGE (the native engine swaps to a new swap chain on a resolution
                    // change): the line that says whether the visual follows it or keeps showing the first, now-dead one.
                    Diag.Line($"[video.surface] bind token={i + 1} id={s.SurfaceId.Value} handle=0x{s.Intent.DesiredHandle:X}"
                        + (s.BindFailures != 0 ? $" (after {s.BindFailures} failed attempt(s))" : ""));
                    if (Diag.CompiledIn && Diag.Enabled) Diag.Event("drm-reg", $"BindSurfaceHandle id={s.SurfaceId.Value} handle=0x{s.Intent.DesiredHandle:X}");
                    s.BindFailures = 0;
                    s.NextBindDrain = 0;
                }
                else
                {
                    // The presenter dropped the old content so no stale frame stays under the new session; commit that.
                    s.BindFailures++;
                    s.NextBindDrain = _drainSeq + (1u << Math.Min(s.BindFailures, 6));
                    changed = true;
                    if (s.BindFailures == 1)
                        Diag.Line($"[video.surface] bind FAILED token={i + 1} id={s.SurfaceId.Value} handle=0x{s.Intent.DesiredHandle:X}; retrying with backoff");
                }
            }

            // Geometry waits for the turn that presents the matching hole. The two content-only scopes (the early drain, an elided
            // turn) place only a surface they just created and whose geometry a publication already delivered (so it is where the
            // glass already shows its hole the moment it is committed); a re-bound existing surface keeps its placement, and every
            // later move waits for the presenting turn. The slot stays dirty either way, so that turn still applies the authoritative
            // placement.
            bool contentOnlyScope = scope is VideoApplyScope.Structural or VideoApplyScope.ContentOnly;
            if (contentOnlyScope && !created) continue;
            if (!s.HasGeometry)
            {
                // Content only so far (the mailbox): the first publication places it. A surface created this apply must not sit in
                // the tree at the presenter's default (visible, at the window origin, at the frame's native size) until then.
                if (created) presenter.SetVisible(s.SurfaceId, false);
                continue;
            }

            PlaceSlot(presenter, ref s, i, scale, posed, scope, announce);
            changed = true;
            // The content-only scopes keep the slot dirty (the presenting turn owns the authoritative placement); otherwise an
            // unbound handle (failed bind) retries next apply.
            s.Dirty = contentOnlyScope || (s.Intent.DesiredHandle != 0 && s.Intent.DesiredHandle != s.BoundHandle);
        }

        // Recompute the dirty flags (a slot with no handle yet stays dirty and retries next frame; a slot still needing a surface or
        // a bind keeps the structural hint up).
        bool stillDirty = false, stillStructural = false;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            if (_slots[i].InUse && _slots[i].Dirty) stillDirty = true;
            if (NeedsStructural(in _slots[i])) stillStructural = true;
        }
        _anyDirty = stillDirty;
        _structuralPending = stillStructural;

        if (changed)
        {
            if (deferCommit)
            {
                presenter.ApplyPending();
                _publishDue = true;   // PublishCommitted, after the host's one device commit: the surface is not composed before it
            }
            else
            {
                presenter.Commit();
                PublishBound();   // after the Commit: the bound surface is now actually composed
            }
        }
        if (_lastTurnMoved) MotionTurns++;
        PublishLive();
        return _boundChanged;
    }

    private void PlaceSlot(IVideoPresenter presenter, ref Slot s, int i, float scale, ReadOnlySpan<VideoPosedHole> posed,
        VideoApplyScope scope, bool announce)
    {
        VideoPresentIntent it = s.Intent;
        // The posed travel of the hole this surface sits behind: the same slice pose the composite just applied, so the video moves
        // exactly as far as the hole. Only a presenting turn poses (the early drain places at the published rect).
        float dx = 0f, dy = 0f;
        RectF clip = RectF.Infinite;
        bool posedHole = false;
        if (scope == VideoApplyScope.Full && it.HasHoleOrigin && TryGetPosed(posed, in it, out VideoPosedHole ph))
        {
            dx = ph.Hole.X - it.HoleX;
            dy = ph.Hole.Y - it.HoleY;
            clip = ph.EffClip;
            posedHole = true;
        }
        RectF rectDip = it.RectDip;
        RectF viewportDip = it.ViewportDip.W > 0f && it.ViewportDip.H > 0f ? it.ViewportDip : it.RectDip;
        if (dx != 0f || dy != 0f)
        {
            rectDip = new RectF(rectDip.X + dx, rectDip.Y + dy, rectDip.W, rectDip.H);
            viewportDip = new RectF(viewportDip.X + dx, viewportDip.Y + dy, viewportDip.W, viewportDip.H);
        }
        bool visible = it.Visible;
        bool viewportEmpty = false;
        if (posedHole && !clip.IsInfinite)
        {
            // The composite cut the hole to this clip; the video's own clip follows it (a hole scrolled under a header shows no video there).
            viewportDip = viewportDip.Intersect(clip);
            viewportEmpty = viewportDip.IsEmpty;
            if (viewportEmpty) visible = false;
        }

        // Whole device pixels (rule R, SnapToDevicePixels): the presenter composites the frame and its clip on the pixel
        // grid, so the video edge shares a boundary with the UI hole's snapped erase rect instead of landing on a
        // fractional offset that DWM resamples into a one-pixel halo.
        var dev = VideoSurfaceRegistry.SnapToDevicePixels(new RectF(rectDip.X * scale, rectDip.Y * scale, rectDip.W * scale, rectDip.H * scale));
        var viewportDev = VideoSurfaceRegistry.SnapToDevicePixels(new RectF(viewportDip.X * scale, viewportDip.Y * scale, viewportDip.W * scale, viewportDip.H * scale));
        presenter.SetContentSize(s.SurfaceId, it.ContentW, it.ContentH);   // so it scales the frame to fill `dev` (not 1:1-cropped)
        presenter.Place(s.SurfaceId, dev, 1f, it.Z);
        if (!viewportEmpty) presenter.SetViewport(s.SurfaceId, viewportDev);
        presenter.SetCornerRadius(s.SurfaceId, it.RadiusDip * scale);
        presenter.SetVisible(s.SurfaceId, visible);

        if (s.Placed && s.GeomDirty) _lastTurnMoved = true;
        s.Placed = true;
        s.GeomDirty = false;
        s.AppliedDx = dx; s.AppliedDy = dy; s.AppliedScale = scale; s.AppliedClip = clip;
        // Only on the apply that created/bound (a resize re-places every frame; that stays Debug-only below).
        if (announce)
            Diag.Line($"[video.surface] place token={i + 1} id={s.SurfaceId.Value} dev=({dev.X:0},{dev.Y:0},{dev.W:0},{dev.H:0}) content={it.ContentW}x{it.ContentH} visible={visible} scale={scale:0.##}");
        if (Diag.CompiledIn && Diag.Enabled) Diag.Event("drm-reg", $"Place id={s.SurfaceId.Value} dev=({dev.X:0},{dev.Y:0},{dev.W:0},{dev.H:0}) visible={visible} scale={scale:0.##} pose=({dx:0.##},{dy:0.##})");
    }

    /// <summary>After the host's device-level commit for changes applied with <c>deferCommit</c>: publish which slots are now bound
    /// AND composed (the readiness only counts once the commit ran). Returns true on a bound edge, so the host wakes the UI loop.
    /// No-op when nothing was applied since the last call.</summary>
    internal bool PublishCommitted()
    {
        if (!_publishDue) return false;
        _publishDue = false;
        _boundChanged = false;
        PublishBound();
        return _boundChanged;
    }

    /// <summary>Tear down every live surface (device teardown / host dispose) and forget the shadow. Render thread.</summary>
    internal void DestroyAll(IVideoPresenter presenter)
    {
        bool changed = false;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse) continue;
            if (!s.SurfaceId.IsNone) { presenter.Destroy(s.SurfaceId); changed = true; }
            _freedGen[i] = Math.Max(_freedGen[i], s.Intent.Gen);
            _registry.PostResult(i, s.Intent.Gen, 0, false, releaseDone: false);
            s = default;
        }
        _anyDirty = false;
        _structuralPending = false;
        _publishDue = false;
        _drainedBy = null;
        _boundBits = 0;
        PublishLive();
        if (changed) presenter.Commit();
    }

    // ── shadow helpers ────────────────────────────────────────────────────────────────────────────────────────────

    // The slot has a handle the presenter must still create a surface for or bind (the structural drain's work list).
    private static bool NeedsStructural(in Slot s)
        => s.InUse && !s.Intent.ReleasePending && s.Intent.DesiredHandle != 0 && (s.SurfaceId.IsNone || s.Intent.DesiredHandle != s.BoundHandle);

    private bool AnyNeedsStructural()
    {
        for (int i = 0; i < MaxSurfaces; i++)
            if (NeedsStructural(in _slots[i])) return true;
        return false;
    }

    /// <summary>Presenter swap: forget everything the old presenter held. Live slots drop their surface id and bound handle and go
    /// dirty so the normal first-use path rebuilds them; a release-pending slot is simply freed (its surface died with the old
    /// presenter, so there is nothing to Destroy).</summary>
    private void ResetRenderSide()
    {
        int reset = 0;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.InUse) continue;
            if (s.Intent.ReleasePending) { FreeSlot(i); continue; }
            if (!s.SurfaceId.IsNone) reset++;
            s.SurfaceId = default;
            s.BoundHandle = 0;
            s.BindFailures = 0;
            s.NextBindDrain = 0;
            s.Placed = false;
            s.GeomDirty = s.HasGeometry;
            s.Dirty = true;
            ClearBound(i);
            PostState(i);
        }
        _anyDirty = true;
        _structuralPending = true;   // every live slot must re-create and re-bind on the new presenter
        Diag.Line($"[video.surface] presenter changed -> reset {reset} live surfaces");
    }

    /// <summary>Free a slot whose token was released: report it complete to the UI thread, which frees its own slot.</summary>
    private void FreeSlot(int i)
    {
        ref Slot s = ref _slots[i];
        long gen = s.Intent.Gen;
        ClearBound(i);
        _freedGen[i] = Math.Max(_freedGen[i], gen);
        _registry.PostResult(i, gen, 0, false, releaseDone: true);
        s = default;
    }

    /// <summary>Publish which slots now have a bound, committed surface (bits are only ever cleared when a slot is freed or the
    /// presenter is replaced).</summary>
    private void PublishBound()
    {
        int bits = 0;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (s.InUse && !s.Intent.ReleasePending && !s.SurfaceId.IsNone && s.BoundHandle != 0) bits |= 1 << i;
        }
        if (bits == 0) return;
        int prev = _boundBits;
        int now = prev | bits;
        if (now == prev) return;
        _boundBits = now;
        _boundChanged = true;
        for (int i = 0; i < MaxSurfaces; i++)
            if ((((now ^ prev) >> i) & 1) != 0) PostState(i);
    }

    private void ClearBound(int i)
    {
        int prev = _boundBits;
        _boundBits &= ~(1 << i);
        if ((prev & (1 << i)) != 0) _boundChanged = true;
    }

    private void PostState(int i)
    {
        ref Slot s = ref _slots[i];
        _registry.PostResult(i, s.Intent.Gen, s.SurfaceId.Value, ((_boundBits >> i) & 1) != 0, releaseDone: false);
    }

    // The live-surface flag the window reads (SetHasLiveVideo) and the weak tier's upload budget: created, bound, visible.
    private void PublishLive()
    {
        int mask = 0;
        for (int i = 0; i < MaxSurfaces; i++)
        {
            ref Slot s = ref _slots[i];
            if (s.InUse && !s.Intent.ReleasePending && s.Intent.Visible && !s.SurfaceId.IsNone && s.BoundHandle != 0) mask |= 1 << i;
        }
        _registry.SetLiveMask(mask);
    }
}
