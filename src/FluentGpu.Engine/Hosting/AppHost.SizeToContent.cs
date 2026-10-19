using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Hosting;

/// <summary>Which window axes follow the content's natural size (WPF <c>Window.SizeToContent</c>). <see cref="Manual"/>
/// (the default) leaves the window size to the user and the app.</summary>
public enum SizeToContent : byte
{
    /// <summary>The window keeps whatever size it has; content lays out inside it.</summary>
    Manual = 0,
    /// <summary>The client width follows the content's natural (unconstrained) width; the height is left alone.</summary>
    Width = 1,
    /// <summary>The client height follows the content's natural height at the current width; the width is left alone.</summary>
    Height = 2,
    /// <summary>Both: the natural width, then the natural height at that width.</summary>
    WidthAndHeight = 3,
}

// Size the window to its content (app-authoring-ergonomics plan §3). One phase, right after the flex solve + layout effects,
// and only on frames where layout ran: probe the root's natural size (FlexLayout.MeasureNatural — a pure query that leaves the
// laid-out frame untouched), convert at the EFFECTIVE scale (zoom included, so N DIP of content get N DIP of layout space),
// clamp to the minimum client size and the monitor work area, and resize through IPlatformWindow.SetClientSizePx when the
// target differs by ≥ 1 px. The resulting WM_SIZE is picked up by the next frame's EnsureSize (the re-entrant paint it may
// raise is already guarded by _inPaint), which re-lays-out at the new size — and that frame's probe then finds nothing to do.
public sealed partial class AppHost
{
    private SizeToContent _sizeToContent;
    private long _fitUserResizeEpoch;   // the IPlatformWindow.UserResizeEpoch last seen — a change = the user resized ⇒ Manual
    private long _fitFrame;             // Paint calls while a size-to-content mode is on (the oscillation window's clock)
    // The last two APPLIED targets (physical px) and the _fitFrame they were applied on: a target equal to the one before the
    // last within OscillationFrames is an A→B→A ping-pong.
    private Size2 _fitHist0, _fitHist1;
    private long _fitHist0Frame = long.MinValue / 2, _fitHist1Frame = long.MinValue / 2;
    // Consecutive ping-pongs. ONE is followed: an app toggling its content A→B→A on purpose must land on A, and a content
    // version cannot tell that apart from a window-driven flip (a bound Width change bumps no subtree version). A second one
    // in a row is content whose natural size depends on the window size (a window-size breakpoint) — latched, so a
    // self-oscillating tree costs at most three resizes.
    private int _fitPingPongs;
    // Latched after a ping-pong: the window stays at B and no resize is issued while the measured target is still A or B; any
    // other target (the content changed) clears it.
    private bool _fitLatched;
    private Size2 _fitLatchA, _fitLatchB;
    // The last issued target and the client size the platform actually gave it (read back right after SetClientSizePx —
    // WM_SIZE is synchronous on Win32, headless applies at once). The OS may clamp (USER32's minimum track size, frame
    // rounding): the same target against that same client size is settled — never re-issued every laid-out frame.
    private Size2 _fitIssued, _fitIssuedGot;
    private bool _fitIssuedValid;
    // ShowWhenFitted: the window is still hidden, waiting for its first fit (so it never flashes at the AppOptions size). While
    // pending the host is exempt from the hidden-window park, so its frames run and lay out into the hidden window.
    private bool _fitShowPending;
    private int _fitShowResizes;
    // Safety net: a pending show is released after FitShowMaxFrames RunFrame turns or FitShowMaxMs since ShowWhenFitted,
    // whatever layout does (an empty root, a device-lost / gated frame that never reaches the 6.6 phase).
    private int _fitShowFrames;
    private double _fitShowArmedMs;
    // The app took visibility over explicitly (CancelShowWhenFitted): a later ShowWhenFitted does nothing.
    private bool _fitShowCancelled;
    private const int OscillationFrames = 4;
    private const int MaxResizesBeforeShow = 8;   // a show-pending window is shown after this many fits even if not settled
    private const int FitShowMaxFrames = 30;
    private const double FitShowMaxMs = 500.0;

    /// <summary>How many resizes the size-to-content phase has issued (diagnostics / gates: a frame with nothing to fit
    /// must issue none).</summary>
    public int SizeToContentResizes { get; private set; }

    /// <summary>True while a ping-pong (A→B→A inside <c>4</c> frames) holds the window at its last size until the content
    /// changes. Diagnostics / gates.</summary>
    public bool SizeToContentLatched => _fitLatched;

    /// <summary>True while the window is hidden waiting for its first size-to-content fit (<see cref="ShowWhenFitted"/>).
    /// Diagnostics / gates.</summary>
    public bool SizeToContentShowPending => _fitShowPending;

    /// <summary>Size the window to its content on the chosen axes (WPF <c>SizeToContent</c>). Applied after every frame
    /// whose layout ran, so content that grows or shrinks resizes the window by itself; the natural size is converted at
    /// the effective scale (DPI × zoom), clamped to <see cref="WindowDesc.MinClientSizeDip"/> and the monitor work area.
    /// An axis whose content fills the window instead of sizing itself (Grow / Stretch / a non-content-sized scroll view —
    /// natural size 0) is left alone. Skipped while maximized, fullscreen, parked or inside the OS move/size loop. The user
    /// resizing the frame switches it back to <c>SizeToContent.Manual</c>. UI thread.</summary>
    public SizeToContent SizeToContent
    {
        get => _sizeToContent;
        set
        {
            if (value == _sizeToContent) return;
            _sizeToContent = value;
            _fitLatched = false;
            _fitPingPongs = 0;
            _fitIssuedValid = false;
            _fitHist0Frame = _fitHist1Frame = long.MinValue / 2;
            _fitUserResizeEpoch = _window.UserResizeEpoch;
            if (value == SizeToContent.Manual) { ReleaseFitShow(); return; }
            _needFullLayout = true;   // the phase only runs on frames whose layout ran: make the next one lay out
            WakeFrame();
        }
    }

    /// <summary>Show the (still hidden) window once its first size-to-content fit has landed — no flash at the initial size.
    /// With <see cref="SizeToContent"/> at <c>SizeToContent.Manual</c> it shows at once. The window is shown
    /// at the latest after a few fits, ~500 ms / 30 frames, or as soon as fitting does not apply (maximized, fullscreen, …).
    /// A no-op once the app took visibility over (<see cref="CancelShowWhenFitted"/>). UI thread.</summary>
    public void ShowWhenFitted()
    {
        if (_fitShowCancelled) return;
        if (_sizeToContent == SizeToContent.Manual) { _window.Show(); return; }
        _fitShowPending = true;
        _fitShowResizes = 0;
        _fitShowFrames = 0;
        _fitShowArmedMs = _timers.NowMs;
        _needFullLayout = true;
        WakeFrame();
    }

    /// <summary>The app set the window's visibility itself (<c>FluentApp.SetWindowVisible</c>, a host-level show/hide):
    /// drop a pending <see cref="ShowWhenFitted"/> without showing, and ignore any later one — an app that hides its window
    /// during mount keeps it hidden. UI thread.</summary>
    public void CancelShowWhenFitted()
    {
        _fitShowCancelled = true;
        _fitShowPending = false;
    }

    private void ReleaseFitShow()
    {
        if (!_fitShowPending) return;
        _fitShowPending = false;
        _window.Show();
        // A frame presented while the window was hidden may have been stood down (occluded): present one in full now that
        // it is on screen, as the pop-out reveal does.
        RequestFullRepaintOnce();
    }

    /// <summary>Every RunFrame turn (parked and gated frames included, before the park decision): the show-pending safety
    /// net. Zero work unless a show is pending.</summary>
    private void TickFitShowSafety()
    {
        if (!_fitShowPending) return;
        if (++_fitShowFrames >= FitShowMaxFrames || _timers.NowMs - _fitShowArmedMs >= FitShowMaxMs) ReleaseFitShow();
    }

    /// <summary>Shorten an idle wait so a pending show-when-fitted reaches its safety deadline even if nothing else wakes
    /// the loop. Unchanged when no show is pending.</summary>
    private int ClampWaitToFitShow(int w)
    {
        if (!_fitShowPending) return w;
        int dueIn = (int)Math.Ceiling(Math.Max(1.0, FitShowMaxMs - (_timers.NowMs - _fitShowArmedMs)));
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    /// <summary>Phase 6.6 (Paint, after the 6.5 layout effects). <paramref name="laidOut"/> = this frame ran the flex solve.
    /// Zero work with <see cref="SizeToContent"/> at Manual; zero allocation always.</summary>
    private void FitWindowToContent(bool laidOut, Size2 layoutSize)
    {
        if (_sizeToContent == SizeToContent.Manual) return;
        long userEpoch = _window.UserResizeEpoch;
        if (userEpoch != _fitUserResizeEpoch)
        {
            _fitUserResizeEpoch = userEpoch;
            SizeToContent = SizeToContent.Manual;   // the user took over the size (WPF semantics)
            return;
        }
        _fitFrame++;
        if (!laidOut || _scene.Root.IsNull) return;
        if (_window.InModalLoop || IsParked || _window.IsFullscreen || _window.State != WindowState.Normal)
        {
            ReleaseFitShow();
            return;
        }

        float scale = _window.Scale <= 0f ? 1f : _window.Scale;
        var mode = _sizeToContent;
        bool fitW = ((byte)mode & 1) != 0;   // Width | WidthAndHeight
        bool fitH = ((byte)mode & 2) != 0;   // Height | WidthAndHeight
        var root = _scene.Root;
        float natW = fitW ? _layout.MeasureNatural(root, float.PositiveInfinity).Width : float.NaN;
        bool useW = natW > 0f && !float.IsInfinity(natW);
        // The height the content needs at the width the window WILL have (its natural width, or the current one).
        float natH = fitH ? _layout.MeasureNatural(root, useW ? natW : layoutSize.Width).Height : float.NaN;
        bool useH = natH > 0f && !float.IsInfinity(natH);

        var cur = _window.ClientSizePx;
        float tw = useW ? FitPx(natW, scale) : cur.Width;
        float th = useH ? FitPx(natH, scale) : cur.Height;

        // Monitor work area (the monitor the window is on), less the window's own frame.
        var outer = _window.OuterBoundsPx;
        var origin = _window.ClientOriginPx;
        bool haveOuter = outer.W > 0f && outer.H > 0f;
        var probe = haveOuter
            ? new Point2(outer.X + outer.W * 0.5f, outer.Y + outer.H * 0.5f)
            : new Point2(origin.X + cur.Width * 0.5f, origin.Y + cur.Height * 0.5f);
        var work = _app.GetWorkArea(probe);
        if (work.W > 0f && work.W < 1e8f && useW) tw = MathF.Min(tw, MathF.Max(1f, MathF.Floor(work.W - (haveOuter ? MathF.Max(0f, outer.W - cur.Width) : 0f))));
        if (work.H > 0f && work.H < 1e8f && useH) th = MathF.Min(th, MathF.Max(1f, MathF.Floor(work.H - (haveOuter ? MathF.Max(0f, outer.H - cur.Height) : 0f))));

        // Minimum client size (DIP at the raw OS DPI — the WM_GETMINMAXINFO rule; zoom does not move the floor). Wins.
        var minDip = _window.MinClientSizeDip;
        float zoom = _window.Zoom <= 0f ? 1f : _window.Zoom;
        float raw = scale / zoom;
        if (useW && minDip.Width > 0f) tw = MathF.Max(tw, FitPx(minDip.Width, raw));
        if (useH && minDip.Height > 0f) th = MathF.Max(th, FitPx(minDip.Height, raw));

        if (MathF.Abs(tw - cur.Width) < 1f && MathF.Abs(th - cur.Height) < 1f)
        {
            _fitPingPongs = 0;   // a settled frame ends any run of ping-pongs
            ReleaseFitShow();    // settled: the window already has the content's size
            return;
        }

        var target = new Size2(tw, th);
        // Asked for exactly this before and the platform answered with the size the window still has (an OS clamp, frame
        // rounding): the window is as close as it gets — settled, not re-issued.
        if (_fitIssuedValid && target == _fitIssued && cur == _fitIssuedGot)
        {
            ReleaseFitShow();
            return;
        }
        if (_fitLatched)
        {
            if (target == _fitLatchA || target == _fitLatchB) { ReleaseFitShow(); return; }
            _fitLatched = false;   // the content changed: fit again
            _fitPingPongs = 0;
        }
        if (target == _fitHist1 && _fitFrame - _fitHist1Frame <= OscillationFrames)
        {
            // A→B→A. Followed once (a deliberate content toggle); the second ping-pong in a row latches.
            if (++_fitPingPongs >= 2)
            {
                _fitLatched = true;
                _fitLatchA = target;
                _fitLatchB = _fitHist0;
                _fitPingPongs = 0;
                ReleaseFitShow();
                return;
            }
        }
        else _fitPingPongs = 0;
        _fitHist1 = _fitHist0; _fitHist1Frame = _fitHist0Frame;
        _fitHist0 = target; _fitHist0Frame = _fitFrame;
        SizeToContentResizes++;
        _window.SetClientSizePx(target);
        var got = _window.ClientSizePx;   // synchronous on every backend (Win32 WM_SIZE inside SetWindowPos; headless at once)
        _fitIssued = target; _fitIssuedGot = got; _fitIssuedValid = true;
        _frameAfterPaint = true;   // the new size is picked up by the next frame's EnsureSize
        if (_fitShowPending && (got != target || ++_fitShowResizes >= MaxResizesBeforeShow)) ReleaseFitShow();
    }

    /// <summary><c>ceil(dip × scale)</c>, forgiving the float noise of an exact product (200 × 1.25 = 250.00002 → 250).</summary>
    internal static float FitPx(float dip, float scale) => MathF.Max(1f, MathF.Ceiling(dip * scale - 1e-3f));
}
