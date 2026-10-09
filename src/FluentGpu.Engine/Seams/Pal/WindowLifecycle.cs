namespace FluentGpu.Pal;

/// <summary>Why a top-level window is being asked to close (<see cref="IPlatformWindow.CloseRequested"/>).</summary>
public enum CloseReason : byte
{
    /// <summary>The user or the app asked: the caption close button, Alt+F4, the system menu's Close, or
    /// <see cref="IPlatformWindow.CloseWindow"/>. A handler may veto it (hide to a notification-area icon instead).</summary>
    User = 0,

    /// <summary>Windows is ending the session or the Restart Manager is closing the app (Win32:
    /// <c>WM_QUERYENDSESSION</c> arrived and was not cancelled by a later <c>WM_ENDSESSION(FALSE)</c>). The handler is
    /// still called — it may flush — but a veto is ignored: a window that hides instead of closing here only gets its
    /// process killed with its state unsaved.</summary>
    SessionEnding = 1,
}

/// <summary>
/// The close decision a platform window applies to one close request — pure, so the rule is unit-tested without a
/// window. The Win32 backend holds one per window: <c>WM_QUERYENDSESSION</c> → <see cref="OnQueryEndSession"/>,
/// <c>WM_ENDSESSION</c> → <see cref="OnEndSession"/>, <c>WM_CLOSE</c> → <see cref="ShouldDestroy"/>.
/// </summary>
public struct WindowCloseGate
{
    private bool _sessionEnding;

    /// <summary>The reason the NEXT close request carries.</summary>
    public readonly CloseReason Reason => _sessionEnding ? CloseReason.SessionEnding : CloseReason.User;

    /// <summary>Windows asked whether the session may end. Every close from here on is a session-end close.</summary>
    public void OnQueryEndSession() => _sessionEnding = true;

    /// <summary><c>WM_ENDSESSION</c>: <paramref name="ending"/> false means another application cancelled the end, so
    /// closes are ordinary user closes again.</summary>
    public void OnEndSession(bool ending) => _sessionEnding = ending;

    /// <summary>Ask <paramref name="handler"/> (true = "I handled it, keep the window") and decide. No handler, or a
    /// handler that declines, destroys the window; a session end destroys it whatever the handler answers.</summary>
    public readonly bool ShouldDestroy(Func<CloseReason, bool>? handler)
    {
        CloseReason reason = Reason;
        bool vetoed = handler is not null && handler(reason);
        return Decide(reason, vetoed);
    }

    /// <summary>The rule itself: destroy unless the request was vetoed, and never honour a veto of a session end.</summary>
    public static bool Decide(CloseReason reason, bool vetoed) => reason == CloseReason.SessionEnding || !vetoed;
}

/// <summary>A window's lifecycle as the host sees it: its placement and whether it is shown at all. A window that is
/// minimized OR hidden is <see cref="Parked"/> — the host produces no frames for it.</summary>
public readonly record struct WindowStatus(WindowState Placement, bool Visible)
{
    /// <summary>Nothing of the window is on screen: minimized or hidden. The host's frame loop parks exactly as it does
    /// for a minimized window (no reconcile, layout, record or present; <c>UseIsActive</c> reads false).</summary>
    public bool Parked => !Visible || Placement == WindowState.Minimized;
}

/// <summary>One observed change between two consecutive <see cref="WindowStatus"/> samples, raised by the host as
/// <c>AppHost.WindowStateChanged</c>. A sample can carry several edges at once (a hidden window restored and shown in one
/// frame), so the edges are independent flags rather than one enum.</summary>
public readonly record struct WindowStateChange(WindowStatus Previous, WindowStatus Current)
{
    /// <summary>The window became minimized (the caption minimize, Win+Down, the system menu).</summary>
    public bool Minimized => Current.Placement == WindowState.Minimized && Previous.Placement != WindowState.Minimized;

    /// <summary>The window left the minimized placement (to normal or maximized).</summary>
    public bool Restored => Previous.Placement == WindowState.Minimized && Current.Placement != WindowState.Minimized;

    /// <summary>The window became maximized.</summary>
    public bool Maximized => Current.Placement == WindowState.Maximized && Previous.Placement != WindowState.Maximized;

    /// <summary>The window was hidden (<see cref="IPlatformWindow.Hide"/>, or anything else that cleared its visibility).</summary>
    public bool Hidden => Previous.Visible && !Current.Visible;

    /// <summary>The window was shown again.</summary>
    public bool Shown => !Previous.Visible && Current.Visible;

    /// <summary>The window went from on screen to parked.</summary>
    public bool Parked => Current.Parked && !Previous.Parked;

    /// <summary>The window went from parked to on screen.</summary>
    public bool Unparked => Previous.Parked && !Current.Parked;
}

/// <summary>
/// Turns per-frame <see cref="WindowStatus"/> samples into <see cref="WindowStateChange"/> edges. The first sample seeds
/// silently — a relay reports changes, and a consumer reads the current state from the window itself. Pure and
/// allocation-free (the host samples it every frame, parked frames included).
/// </summary>
public struct WindowStateRelay
{
    private WindowStatus _last;
    private bool _seeded;

    /// <summary>The last sample fed in (meaningless before the first).</summary>
    public readonly WindowStatus Last => _last;

    /// <summary>Feed this frame's sample. True, with the edge, when it differs from the previous sample.</summary>
    public bool TryAdvance(WindowStatus now, out WindowStateChange change)
    {
        if (!_seeded)
        {
            _seeded = true;
            _last = now;
            change = default;
            return false;
        }
        if (now == _last)
        {
            change = default;
            return false;
        }
        change = new WindowStateChange(_last, now);
        _last = now;
        return true;
    }
}

/// <summary>
/// Debounces <see cref="IPlatformWindow.IsCloaked"/> into a park decision for a host (the primary window or a detached pop-out). A window is parked only
/// once it has stayed cloaked for <see cref="EnterDelayMs"/> - shell transitions (virtual-desktop slides, Alt+Tab, window
/// animations) cloak a window for a few frames and must not flap the host between parked and live - and it un-parks the
/// instant a sample reads uncloaked, so a window the user switches back to is never held dark. Pure and allocation-free
/// (sampled once per host frame; the clock is the host's own timer clock).
/// </summary>
public struct CloakParkGate
{
    /// <summary>How long the window must have been continuously cloaked before the host parks it.</summary>
    public const double EnterDelayMs = 250.0;

    private double _cloakedSinceMs;
    private bool _cloaked;
    private bool _parked;

    /// <summary>The decision the last <see cref="Advance"/> returned.</summary>
    public readonly bool Parked => _parked;

    /// <summary>Milliseconds until a still-pending debounce parks the host (0 when already due), or
    /// <see cref="double.PositiveInfinity"/> when uncloaked or already parked. A pure query: the host uses it to wake an
    /// otherwise idle loop at the end of the debounce, because nothing else would sample the gate again.</summary>
    public readonly double MsUntilPark(double nowMs) =>
        _cloaked && !_parked ? Math.Max(0.0, _cloakedSinceMs + EnterDelayMs - nowMs) : double.PositiveInfinity;

    /// <summary>Feed this frame's sample (<paramref name="nowMs"/> on the host timer clock). True while the host should park.</summary>
    public bool Advance(bool cloaked, double nowMs)
    {
        if (!cloaked)
        {
            _cloaked = false;
            _parked = false;
            return false;
        }
        if (!_cloaked)
        {
            _cloaked = true;
            _cloakedSinceMs = nowMs;
        }
        if (!_parked && nowMs - _cloakedSinceMs >= EnterDelayMs) _parked = true;
        return _parked;
    }
}
