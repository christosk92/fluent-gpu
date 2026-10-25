using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi;

namespace FluentGpu.Hosting;

/// <summary>How long a thaw cross-dissolves from the frozen snapshot to the live frame. <see cref="Cut"/> (0 ms) presents the new
/// frame at once. The dissolve is linear in t: <c>out = (1-t)·snapshot + t·frame</c> on premultiplied colour and alpha
/// (<see cref="FrameDissolve"/>).</summary>
public readonly record struct DissolveSpec(float Ms)
{
    /// <summary>No dissolve: the new frame replaces the snapshot at the first present after the thaw.</summary>
    public static DissolveSpec Cut => new(0f);
}

/// <summary>
/// The DWM changes a thaw must order with its presents (technical-plan E2). The engine invokes <see cref="AtThawStart"/> on the UI
/// thread immediately before the FIRST dissolve frame is published (the opaque-to-translucent and translucent-to-translucent rows:
/// the snapshot still covers or tints the window) and <see cref="AtThawEnd"/> immediately before the LAST frame is published
/// (the translucent-to-opaque row: the new frame fully covers the window). Both run for a cut too, so the end state is always
/// reached. EP9's present hook moves the invocation onto the render thread's present index; the contract (start before the first
/// dissolve present, end before the last) is the same.
/// </summary>
public sealed record DwmPlan(Action? AtThawStart = null, Action? AtThawEnd = null)
{
    /// <summary>No DWM change: the same material and dark flag on both sides (an exact dissolve).</summary>
    public static DwmPlan None { get; } = new();
}

/// <summary>Where a freeze is in its life, per host.</summary>
public enum FreezePhase : byte
{
    /// <summary>Not frozen.</summary>
    None = 0,
    /// <summary>Frozen: the outgoing frame is captured, presents are held, input is swallowed, video releases are deferred.</summary>
    Frozen = 1,
    /// <summary>Thawing: presents resume as a dissolve from the snapshot to the live frame; the freeze ends when it completes.</summary>
    Dissolving = 2,
}

/// <summary>How a freeze ended.</summary>
public enum FreezeEnd : byte
{
    /// <summary>Still active.</summary>
    Active = 0,
    /// <summary>Thawed: the dissolve (or the cut of <see cref="DissolveSpec.Cut"/>) completed.</summary>
    Thawed = 1,
    /// <summary>A resize or DPI change cut the freeze (an immediate thaw with no dissolve; the snap scope ended with it).</summary>
    CutByResize = 2,
    /// <summary>The watchdog (<see cref="AppHost.FreezeWatchdogMs"/>) thawed it.</summary>
    CutByWatchdog = 3,
}

/// <summary>One freeze (E2): returned by <see cref="AppHost.Freeze"/>, handed back to <see cref="AppHost.Thaw"/>. It also tells the
/// owner when the outgoing frame has been captured (<see cref="WhenCaptured"/>: apply the change after that) and how the freeze ended.</summary>
public sealed class FreezeToken
{
    private readonly TaskCompletionSource _captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _isCaptured;
    private int _capturesOwed = 1;   // this host's capture frame, plus one per frozen pop-out

    internal FreezeToken(AppHost host, long id) { Host = host; Id = id; }

    /// <summary>The host that owns this freeze (a thaw is called on it). Pop-outs freeze and thaw with it.</summary>
    public AppHost Host { get; }
    public long Id { get; }

    /// <summary>How the freeze ended; <see cref="FreezeEnd.Active"/> until it does.</summary>
    public FreezeEnd End { get; internal set; }
    public bool IsActive => End == FreezeEnd.Active;
    /// <summary>True when a resize or the watchdog ended the freeze before <see cref="AppHost.Thaw"/>.</summary>
    public bool WasCut => End is FreezeEnd.CutByResize or FreezeEnd.CutByWatchdog;

    /// <summary>True once the OUTGOING frame (the tree on screen when <see cref="AppHost.Freeze"/> was called) has been published
    /// as the capture frame of this window and of every pop-out frozen with it. Changes the app makes before this are part of that frame.</summary>
    public bool IsCaptured => Volatile.Read(ref _isCaptured) != 0;

    /// <summary>Completes (on the UI thread's next turn) when <see cref="IsCaptured"/> becomes true - or when the freeze ends
    /// first, so a waiter is never stranded. A transaction awaits it, THEN applies its changes.</summary>
    public Task WhenCaptured => _captured.Task;

    internal void OweCapture() => Interlocked.Increment(ref _capturesOwed);

    /// <summary>One participating host published its capture frame; the token is captured when every one has.</summary>
    internal void NoteCaptured()
    {
        if (Interlocked.Decrement(ref _capturesOwed) > 0) return;
        Volatile.Write(ref _isCaptured, 1);
        _captured.TrySetResult();
    }

    internal void MarkEnded(FreezeEnd how)
    {
        End = how;
        _captured.TrySetResult();
    }
}

/// <summary>
/// E2 - the frame freeze. A theme transaction applies a whole-app change that must not show a hybrid frame: <see cref="Freeze"/>
/// captures the frame on screen as an immutable GPU snapshot WITH ALPHA, holds every present of this window and its pop-outs,
/// swallows input and defers video releases; the transaction rebuilds the UI underneath (in the snap scope the freeze owns), and
/// <see cref="Thaw"/> presents a premultiplied cross-dissolve from the snapshot to the new frame over the live DWM backdrop.
/// <list type="bullet">
/// <item><b>Capture.</b> The first frame published after <see cref="Freeze"/> is the CAPTURE frame: it is the outgoing tree,
/// submitted and presented as usual, and the presenting side snapshots its back buffer before the present. Everything published
/// after it is HELD (recorded and submitted so uploads and the atlas land, never flipped). The app waits for
/// <see cref="FreezeToken.WhenCaptured"/> before changing anything.</item>
/// <item><b>Snap scope.</b> The freeze owns a <see cref="SnapScope"/> on every frozen host (Enter/Exit/Layout/brush land at their
/// end values, FrameClock and timers stand still). It ends at <see cref="Thaw"/> and at a cut, so the animations the new theme
/// owns run normally afterwards.</item>
/// <item><b>While frozen.</b> Pointer, key, wheel, char and touch input is dropped before the dispatcher (no hit-testing, cursor or
/// IME change against the invisible tree) and windowed popups cannot open. Video placement is applied by nobody: releases wait for
/// the thaw frame. A resize or DPI change cuts the freeze at once (<see cref="FreezeEnd.CutByResize"/>, on the precedent of the
/// restore hold's resize release), and so does the watchdog (<see cref="FreezeWatchdogMs"/>, 2 s).</item>
/// <item><b>Backends.</b> A backend that cannot snapshot or dissolve (<see cref="ISwapchain.SupportsFrameDissolve"/> false) still
/// freezes; its thaw is a cut.</item>
/// <item>Detached pop-outs freeze and thaw in the same call, each on its own swapchain. "Same frame" is best effort: a refused
/// non-blocking present can put one window a frame behind.</item>
/// </list>
/// </summary>
public sealed partial class AppHost
{
    private FreezePhase _fzPhase;
    private FreezeToken? _fzToken;
    private double _fzStartMs, _fzThawStartMs;
    private float _fzDissolveMs;
    private DwmPlan? _fzDwm;
    private bool _fzCapturePending, _fzDwmStartRun;
    private FreezeEnd _fzCut;                  // a cut waiting for its Release frame (Active = none)
    private FreezeKind _fzLastPublished;       // UI: the last publication's directive (the release is owed a publication)
    private SnapScope _fzSnap;
    private bool _fzSnapOpen;
    private long _freezeSeq;

    // Presenting side (the render thread, or this thread inline): the retained snapshot of the outgoing frame.
    private IFrameSnapshot? _frozenSnapshot;
    private ulong _frozenSnapshotSeq;

    /// <summary>Raised on the UI thread when a freeze this host owns ends: thawed, cut by a resize, or cut by the watchdog. A
    /// transaction subscribes to learn it was cut (a cut has no <see cref="Thaw"/>; the DWM plan never ran).</summary>
    public event Action<FreezeToken, FreezeEnd>? FreezeEnded;

    /// <summary>The watchdog: a freeze that has not thawed after this long is thawed anyway (default 2000 ms, on the host timer clock).</summary>
    public int FreezeWatchdogMs { get; set; } = 2000;

    /// <summary>Where this host's freeze is.</summary>
    public FreezePhase FreezeState => _fzPhase;

    /// <summary>True while presents are held (the <see cref="FreezePhase.Frozen"/> phase).</summary>
    public bool IsFrozen => _fzPhase == FreezePhase.Frozen;

    /// <summary>Freeze this window and every detached pop-out: capture the outgoing frame, hold presents, swallow input, defer video
    /// releases and enter the snap scope. UI thread. Throws if this host is already frozen. Apply the change after
    /// <see cref="FreezeToken.WhenCaptured"/>, then call <see cref="Thaw"/>.</summary>
    public FreezeToken Freeze()
    {
        Threading.ThreadGuard.AssertUi();
        if (_fzPhase != FreezePhase.None) throw new InvalidOperationException("This host is already frozen; thaw (or let the watchdog cut) the current freeze first.");
        var token = new FreezeToken(this, ++_freezeSeq);
        BeginFreezeLocal(token);
        for (int i = 0; i < _detachedHosts.Count; i++)
        {
            var child = _detachedHosts[i];
            if (child._fzPhase == FreezePhase.None && !child._window.IsClosed) { token.OweCapture(); child.BeginFreezeLocal(token); }
        }
        return token;
    }

    /// <summary>Thaw <paramref name="token"/>: end the snap scope and present the dissolve from the snapshot to the live frame over
    /// <paramref name="dissolve"/>, then release the snapshot. <paramref name="dwm"/> orders the DWM change with the dissolve's first and
    /// last frame. A token a resize or the watchdog already cut is a no-op. UI thread.</summary>
    public void Thaw(FreezeToken token, DissolveSpec dissolve = default, DwmPlan? dwm = null)
    {
        Threading.ThreadGuard.AssertUi();
        ArgumentNullException.ThrowIfNull(token);
        if (!ReferenceEquals(token.Host, this)) throw new ArgumentException("The token belongs to another host.", nameof(token));
        if (!token.IsActive || _fzPhase != FreezePhase.Frozen || !ReferenceEquals(_fzToken, token)) return;
        ThawLocal(dissolve.Ms, dwm);
        for (int i = 0; i < _detachedHosts.Count; i++)
        {
            var child = _detachedHosts[i];
            if (ReferenceEquals(child._fzToken, token) && child._fzPhase == FreezePhase.Frozen) child.ThawLocal(dissolve.Ms, null);
        }
    }

    private void BeginFreezeLocal(FreezeToken token)
    {
        _fzToken = token;
        _fzPhase = FreezePhase.Frozen;
        _fzStartMs = _timers.NowMs;
        _fzCapturePending = true;
        _fzCut = FreezeEnd.Active;
        _fzDwm = null;
        _fzDwmStartRun = false;
        _fzSnap = SnapScope.Enter(this);
        _fzSnapOpen = true;
        _dispatcher.CancelInteractions();   // no hover/pressed/drag state is left behind the invisible tree
        WakeFrame();                        // the capture frame is owed
    }

    private void ThawLocal(float ms, DwmPlan? dwm)
    {
        _fzPhase = FreezePhase.Dissolving;
        _fzThawStartMs = _timers.NowMs;
        _fzDissolveMs = ms > 0f ? ms : 0f;
        _fzDwm = dwm;
        _fzDwmStartRun = false;
        CloseFreezeSnap();   // the transaction's frames are done: the new theme's motion runs normally from here
        WakeFrame();
    }

    private void CloseFreezeSnap()
    {
        if (!_fzSnapOpen) return;
        _fzSnapOpen = false;
        _fzSnap.Dispose();
    }

    /// <summary>Cut this host's freeze now (a resize, DPI change or the watchdog): the snap scope ends at once, and the next published
    /// frame is the Release frame. The owner's cut also cuts the pop-outs that share its token.</summary>
    private void CutLocal(FreezeEnd how)
    {
        if (_fzPhase == FreezePhase.None || _fzCut != FreezeEnd.Active) return;
        _fzCut = how;
        CloseFreezeSnap();
        WakeFrame();
        if (ReferenceEquals(_fzToken?.Host, this))
            for (int i = 0; i < _detachedHosts.Count; i++)
                if (ReferenceEquals(_detachedHosts[i]._fzToken, _fzToken)) _detachedHosts[i].CutLocal(how);
    }

    /// <summary>Paint, right after the size is known: the resize cut and the watchdog. A resized frame (a size or DPI/zoom change) ends
    /// the freeze before the frame's flush so the snap scope is closed for the crossing the resize owes.</summary>
    private void FreezeFrameStart(bool resized)
    {
        if (_fzCut != FreezeEnd.Active) return;
        if (resized) CutLocal(FreezeEnd.CutByResize);
        else if (_fzPhase == FreezePhase.Frozen && _timers.NowMs - _fzStartMs >= FreezeWatchdogMs) CutLocal(FreezeEnd.CutByWatchdog);
    }

    /// <summary>Paint, where the publication is built: this frame's directive. Runs the DWM plan's edges and advances the phase.</summary>
    private FreezeKind ResolveFreezeDirective(out float t)
    {
        t = 0f;
        if (_fzPhase == FreezePhase.None) return FreezeKind.None;
        if (_fzCut != FreezeEnd.Active)
        {
            // A cut: the DWM plan (when the thaw had been called) still reaches its end state, then the Release frame.
            if (_fzPhase == FreezePhase.Dissolving) RunDwmEdges(start: true, end: true);
            EndFreezeLocal(_fzCut);
            return FreezeKind.Release;
        }
        if (_fzPhase == FreezePhase.Frozen)
        {
            if (_fzCapturePending)
            {
                _fzCapturePending = false;
                _fzToken?.NoteCaptured();
                return FreezeKind.Capture;
            }
            return FreezeKind.Hold;
        }
        // Dissolving.
        if (!_fzDwmStartRun) RunDwmEdges(start: true, end: false);
        float u = _fzDissolveMs <= 0f ? 1f : (float)((_timers.NowMs - _fzThawStartMs) / _fzDissolveMs);
        // A backend that cannot dissolve thaws as a cut: the new frame at once.
        if (u >= 1f || !_swapchain.SupportsFrameDissolve)
        {
            RunDwmEdges(start: false, end: true);
            EndFreezeLocal(FreezeEnd.Thawed);
            FrameTrace.Theme("dissolve.end", null, u);
            return FreezeKind.Release;
        }
        t = u < 0f ? 0f : u;
        FrameTrace.Theme("dissolve", null, t);
        WakeFrame();   // the next dissolve frame is owed (the loop keeps running until the dissolve completes)
        return FreezeKind.Dissolve;
    }

    private void RunDwmEdges(bool start, bool end)
    {
        if (start && !_fzDwmStartRun) { _fzDwmStartRun = true; _fzDwm?.AtThawStart?.Invoke(); }
        if (end && _fzDwm is { } plan) { _fzDwm = null; plan.AtThawEnd?.Invoke(); }
    }

    private void EndFreezeLocal(FreezeEnd how)
    {
        var token = _fzToken;
        _fzPhase = FreezePhase.None;
        _fzToken = null;
        _fzCut = FreezeEnd.Active;
        _fzCapturePending = false;
        _fzDwm = null;
        CloseFreezeSnap();
        if (token is not null && ReferenceEquals(token.Host, this))
        {
            token.MarkEnded(how);
            FreezeEnded?.Invoke(token, how);
        }
    }

    /// <summary>The frame loop has freeze work: the capture frame, a running dissolve, a cut waiting for its Release frame, or an overdue watchdog.</summary>
    private bool FreezeNeedsFrame()
    {
        if (_fzPhase == FreezePhase.None) return false;
        if (_fzPhase == FreezePhase.Dissolving || _fzCapturePending || _fzCut != FreezeEnd.Active) return true;
        return _timers.NowMs - _fzStartMs >= FreezeWatchdogMs;
    }

    /// <summary>Shorten an idle wait so the watchdog fires on time even when nothing else wakes the loop.</summary>
    private int ClampWaitToFreeze(int w)
    {
        if (_fzPhase != FreezePhase.Frozen) return w;
        int dueIn = (int)Math.Ceiling(Math.Max(1.0, _fzStartMs + FreezeWatchdogMs - _timers.NowMs));
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    /// <summary>Presenting side: act on the freeze directive of the frame about to be presented (after its submit, before the flip).
    /// Capture snapshots the back buffer; Hold holds the present; Dissolve arms the cross-dissolve; Release drops the snapshot.</summary>
    private void FreezeBeforePresent(in Threading.RenderFrame rf, ref bool hold)
    {
        switch (rf.Freeze)
        {
            case FreezeKind.None:
                // A snapshot that outlived its freeze (the Release frame was superseded before the render side adopted it).
                ReleaseFrozenSnapshot();
                break;
            case FreezeKind.Capture:
                // A retried owed present re-enters here with the same frame: capture once per publication.
                if (_frozenSnapshot is null || _frozenSnapshotSeq != rf.PublishSeq)
                {
                    ReleaseFrozenSnapshot();
                    if (_swapchain.SupportsFrameDissolve) _frozenSnapshot = _swapchain.CaptureSnapshot();
                    _frozenSnapshotSeq = rf.PublishSeq;
                }
                break;
            case FreezeKind.Hold:
                hold = true;
                break;
            case FreezeKind.Dissolve:
                if (_frozenSnapshot is { } s) _swapchain.DissolveNextPresent(s, rf.DissolveT);
                break;
            case FreezeKind.Release:
                ReleaseFrozenSnapshot();
                break;
        }
    }

    private void ReleaseFrozenSnapshot()
    {
        var s = _frozenSnapshot;
        if (s is null) return;
        _frozenSnapshot = null;
        s.Dispose();
    }

    /// <summary>Test seam: the snapshot the presenting side currently retains.</summary>
    internal bool HasFrozenSnapshotForTest => _frozenSnapshot is not null;
}
