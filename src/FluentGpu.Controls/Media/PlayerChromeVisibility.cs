using System;
using FluentGpu.Animation;

namespace FluentGpu.Controls.Media;

/// <summary>What revealed the chrome — it picks the dwell. Touch and keyboard reveals get the longer one: neither has a
/// hover that re-arms it while the user reads the controls.</summary>
public enum ChromeActivity : byte { Pointer, Touch, Keyboard }

/// <summary>The playback facts the chrome reacts to. Opening / Buffering / Stalled are deliberately ABSENT — the element
/// maps them to <see cref="ChromePlayback.Playing"/> (<c>MediaPlayerElement.ChromePlaybackOf</c>): a rebuffer, an ABR switch or a slow
/// open neither reveals nor pins the controls; the spinner speaks for them (Chromium hides through buffering; Media3's
/// timeout runs "with playback or buffering in progress").</summary>
public enum ChromePlayback : byte { Playing, Paused, Ended, Failed, AudioOnly }

/// <summary>Why VISIBLE chrome may not hide right now. A hold never reveals hidden chrome — only activity does; that split
/// is the whole fix for "the controls pop up by themselves".</summary>
[Flags]
public enum ChromeHold : ushort
{
    None = 0,
    /// <summary>Auto-hide off, or no transport drawn on this surface.</summary>
    Disabled = 1 << 0,
    /// <summary>An AT client is navigating the controls.</summary>
    Accessibility = 1 << 1,
    /// <summary>Paused / ended / failed / audio-only.</summary>
    Stopped = 1 << 2,
    /// <summary>The pointer is resting on the control panel.</summary>
    OverControls = 1 << 3,
    /// <summary>The primary button is held on the player.</summary>
    Pressed = 1 << 4,
    /// <summary>A seek drag, until the player confirms the committed seek.</summary>
    Scrubbing = 1 << 5,
    /// <summary>A picker / the ⋯ menu / any input-blocking overlay is up.</summary>
    Menu = 1 << 6,
    /// <summary>Tab focus is inside the controls (WCAG 2.4.7).</summary>
    KeyboardFocus = 1 << 7,
    /// <summary>The OS move loop a drag on the picture started.</summary>
    WindowMove = 1 << 8,
}

/// <summary>The timing table (ms / DIP). <see cref="Default"/> reads <see cref="MotionTok"/>; a host overrides the mouse
/// dwell through <c>MediaPlayerElement.TransportControlsHideDelayMs</c>.</summary>
public readonly record struct PlayerChromeTiming(
    double IdleHideMs, double TouchIdleHideMs, double KeyboardIdleHideMs,
    double LeaveHideMs, double CursorTrailMs, float DeadzoneDip)
{
    public static PlayerChromeTiming Default { get; } = new(
        MotionTok.MediaChromeIdleDelayMs, MotionTok.MediaChromeIdleDelayTouchMs, MotionTok.MediaChromeIdleDelayAfterFocusMs,
        MotionTok.MediaChromeLeaveHideMs, MotionTok.MediaChromeFadeOutMs, MotionTok.MediaChromeMoveThresholdDip);
}

/// <summary>
/// The media transport's show/hide policy as a pure clocked state machine: inputs in (with the host timer clock's
/// <c>now</c>), <see cref="ChromeVisible"/> / <see cref="CursorHidden"/> / <see cref="NextWakeMs"/> out. No scene, no
/// hooks, no timers and no allocation per input — the owner arms ONE timer at <see cref="NextWakeMs"/> and calls
/// <see cref="Tick"/> when it fires (and after every input).
/// <list type="bullet">
/// <item>ACTIVITY reveals and restarts the dwell: entering, a move that leaves the rest-point deadzone, a press, the
/// wheel, a handled key, a tap on hidden chrome, keyboard focus / AT gained, a user-visible stop, an OS move loop's
/// start/end.</item>
/// <item>HOLDS keep visible chrome up and never reveal hidden chrome; releasing one restarts the dwell.</item>
/// <item>Leaving hides after a short debounce; the cursor hides when the conceal fade ends, only while the pointer is
/// inside, only if the surface's policy allows it.</item>
/// </list>
/// </summary>
public sealed class PlayerChromeVisibility
{
    // What a touch tap may NOT override: the user is mid-task in the controls (or a screen reader is). Paused, the pointer
    // over the controls and a held press are SOFT — an explicit tap may still put the controls away.
    private const ChromeHold HardHolds = ChromeHold.Disabled | ChromeHold.Accessibility | ChromeHold.Scrubbing
                                       | ChromeHold.Menu | ChromeHold.KeyboardFocus | ChromeHold.WindowMove;

    private readonly PlayerChromeTiming _t;
    private bool _visible = true, _cursorHidden;
    private bool _enabled = true, _a11y, _overControls, _pressed, _scrubbing, _menu, _keyboardFocus, _moving;
    private bool _pointerInside, _left, _cursorMayHide;
    private ChromePlayback _playback;                          // Playing
    private double _dwell, _hideAt, _cursorAt = double.PositiveInfinity;
    private float _lastX = float.NaN, _lastY = float.NaN;       // last sample — same-point re-delivery is not activity
    private float _anchorX = float.NaN, _anchorY = float.NaN;   // where the pointer rested when the chrome hid

    public PlayerChromeVisibility(in PlayerChromeTiming timing, double nowMs)
    {
        _t = timing;
        _dwell = timing.IdleHideMs;
        _hideAt = nowMs + _dwell;   // a fresh surface shows its controls, then idles away like any reveal
    }

    public bool ChromeVisible => _visible;
    public bool CursorHidden => _cursorHidden;
    /// <summary>Why the last visibility edge happened — a static literal for the always-on <c>[media.chrome]</c> line.</summary>
    public string LastCause { get; private set; } = "mount";

    public ChromeHold Holds
    {
        get
        {
            var h = ChromeHold.None;
            if (!_enabled) h |= ChromeHold.Disabled;
            if (_a11y) h |= ChromeHold.Accessibility;
            if (_playback != ChromePlayback.Playing) h |= ChromeHold.Stopped;
            if (_overControls) h |= ChromeHold.OverControls;
            if (_pressed) h |= ChromeHold.Pressed;
            if (_scrubbing) h |= ChromeHold.Scrubbing;
            if (_menu) h |= ChromeHold.Menu;
            if (_keyboardFocus) h |= ChromeHold.KeyboardFocus;
            if (_moving) h |= ChromeHold.WindowMove;
            return h;
        }
    }

    /// <summary>The next instant <see cref="Tick"/> can change an output; +∞ = nothing pending (the loop may idle — a
    /// hold, or hidden chrome whose cursor is already hidden or must stay, schedules nothing).</summary>
    public double NextWakeMs
        => _visible
            ? (Holds == ChromeHold.None ? _hideAt : double.PositiveInfinity)
            : (!_cursorHidden && _cursorMayHide && _pointerInside ? _cursorAt : double.PositiveInfinity);

    // ── activity (the ONLY inputs that reveal) ───────────────────────────────────────────────────────────────────────

    /// <summary>A mouse/pen move over the player, in any stable coordinate space (the element passes frame-local DIP).</summary>
    public void PointerMoved(float x, float y, double nowMs)
    {
        if (x == _lastX && y == _lastY) return;          // re-delivered at the same point (show/hide/move, hover re-resolve)
        bool entering = !_pointerInside;
        _pointerInside = true; _left = false;
        _lastX = x; _lastY = y;
        if (!_visible && !entering)
        {
            // Jitter is measured from where the pointer RESTED when the chrome hid, never from the previous sample: slow,
            // deliberate motion is many sub-threshold samples, and a per-sample test ignores every one of them.
            if (float.IsNaN(_anchorX)) { _anchorX = x; _anchorY = y; return; }
            float dx = x - _anchorX, dy = y - _anchorY, dz = _t.DeadzoneDip;
            if (dx * dx + dy * dy < dz * dz) return;
        }
        Reveal(ChromeActivity.Pointer, nowMs, entering ? "enter" : "move");
    }

    /// <summary>The pointer is already resting over the player at (<paramref name="x"/>, <paramref name="y"/>) without
    /// having moved — a surface MOUNTED under a still cursor (a pop-out opening beneath it, the remount a fullscreen
    /// toggle makes). NOT activity: it reveals nothing and restarts no dwell. It only stops the first idle cycle from
    /// treating the pointer as outside (so the cursor may hide with the chrome) and makes the dispatcher's re-delivery of
    /// that same point a no-op.</summary>
    public void PointerPresent(float x, float y)
    {
        if (_pointerInside) return;
        _pointerInside = true; _left = false;
        _lastX = x; _lastY = y;
    }

    /// <summary>Wheel, a handled key, a middle-click mute, an aspect/fullscreen command — explicit user activity.</summary>
    public void Activity(ChromeActivity kind, double nowMs)
        => Reveal(kind, nowMs, kind switch { ChromeActivity.Keyboard => "key", ChromeActivity.Touch => "tap", _ => "pointer" });

    /// <summary>A touch tap on the video TOGGLES (Media3 <c>hide_on_touch</c>, Chromium <c>kGestureTap</c>). A hard hold
    /// keeps the chrome up; paused is a SOFT hold — an explicit tap may still put the controls away.</summary>
    public void Tapped(double nowMs)
    {
        if (!_visible || (Holds & HardHolds) != 0) Reveal(ChromeActivity.Touch, nowMs, "tap");
        else Conceal(nowMs, "tap");
    }

    public void SetPressed(bool pressed, double nowMs)
    {
        if (pressed) { _pressed = true; Reveal(ChromeActivity.Pointer, nowMs, "press"); }
        else SetHold(ref _pressed, false, nowMs);
    }

    /// <summary>A press on the picture became an OS window move. The loop owns the button now — no release will come —
    /// so the press hold becomes the move hold until <see cref="WindowMoveEnded"/>.</summary>
    public void WindowMoveStarted(double nowMs)
    {
        _pressed = false;
        _moving = true;
        Reveal(ChromeActivity.Pointer, nowMs, "window-move");
    }

    /// <summary>Every OS move/size loop ends through here (edge resizes too) — inert unless a move WE started is running.</summary>
    public void WindowMoveEnded(double nowMs)
    {
        if (!_moving) return;
        _moving = false;
        Reveal(ChromeActivity.Pointer, nowMs, "window-move-end");
    }

    // ── pointer levels ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pointer really left the player: the window, onto the non-client resize band, or the window blurred.</summary>
    public void PointerLeft(double nowMs)
    {
        _pointerInside = false; _left = true;
        _lastX = _lastY = float.NaN;
        _overControls = false; _pressed = false;
        ShowCursor();                                      // never leave a hidden cursor behind outside the video
        if (_visible) _hideAt = Math.Min(_hideAt, nowMs + _t.LeaveHideMs);
    }

    /// <summary>Hover was TAKEN (an overlay scrim, the move loop's capture cancel) while the pointer is still over the
    /// player: drop the pointer holds, schedule nothing.</summary>
    public void PointerCovered(double nowMs)
    {
        bool wasHeld = _overControls || _pressed;
        _overControls = false; _pressed = false;
        if (wasHeld) Rearm(nowMs);
    }

    public void SetPointerOverControls(bool over, double nowMs) => SetHold(ref _overControls, over, nowMs);

    // ── holds ────────────────────────────────────────────────────────────────────────────────────────────────────────

    public void SetScrubbing(bool active, double nowMs) => SetHold(ref _scrubbing, active, nowMs);
    public void SetMenuOpen(bool open, double nowMs) => SetHold(ref _menu, open, nowMs);

    public void SetKeyboardFocusInControls(bool focused, double nowMs)
    {
        if (focused == _keyboardFocus) return;
        _keyboardFocus = focused;
        if (focused) Reveal(ChromeActivity.Keyboard, nowMs, "focus"); else Rearm(nowMs);
    }

    public void SetAccessibility(bool active, double nowMs)
    {
        if (active == _a11y) return;
        _a11y = active;
        if (active) Reveal(ChromeActivity.Keyboard, nowMs, "a11y"); else Rearm(nowMs);
    }

    public void SetEnabled(bool enabled, double nowMs)
    {
        if (enabled == _enabled) return;
        _enabled = enabled;
        if (!enabled) { _visible = true; ShowCursor(); LastCause = "disabled"; }
        else Rearm(nowMs);
    }

    /// <summary>A user-visible stop REVEALS and pins (the user needs the controls — Chromium, Firefox PiP, Media3);
    /// resuming starts a fresh dwell from the resume edge.</summary>
    public void SetPlayback(ChromePlayback playback, double nowMs)
    {
        if (playback == _playback) return;
        _playback = playback;
        if (playback != ChromePlayback.Playing) Reveal(ChromeActivity.Pointer, nowMs, "stopped");
        else Rearm(nowMs);
    }

    /// <summary>Policy × presentation (the element computes it: Always, or FullscreenOnly ∧ presenting fullscreen).
    /// Allowing it while the chrome is already hidden hides the cursor NOW, not on the next jiggle.</summary>
    public void SetCursorMayHide(bool mayHide, double nowMs)
    {
        if (mayHide == _cursorMayHide) return;
        _cursorMayHide = mayHide;
        if (!mayHide) ShowCursor();
        else if (!_visible) _cursorAt = Math.Min(_cursorAt, nowMs);
    }

    // ── clock ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Advance to <paramref name="nowMs"/>. Idempotent; the owner calls it after every input and on every wake.
    /// Returns true when an output changed.</summary>
    public bool Tick(double nowMs)
    {
        bool visible = _visible, cursor = _cursorHidden;
        if (_visible && Holds == ChromeHold.None && nowMs >= _hideAt) Conceal(nowMs, _left ? "leave" : "idle");
        if (!_visible && !_cursorHidden && _cursorMayHide && _pointerInside && nowMs >= _cursorAt) _cursorHidden = true;
        return visible != _visible || cursor != _cursorHidden;
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Reveal(ChromeActivity kind, double nowMs, string cause)
    {
        _dwell = kind switch
        {
            ChromeActivity.Touch => _t.TouchIdleHideMs,
            ChromeActivity.Keyboard => _t.KeyboardIdleHideMs,
            _ => _t.IdleHideMs,
        };
        _hideAt = nowMs + _dwell;
        _anchorX = _anchorY = float.NaN;
        ShowCursor();
        if (!_visible) { _visible = true; LastCause = cause; }
    }

    private void Conceal(double nowMs, string cause)
    {
        _visible = false;
        LastCause = cause;
        _hideAt = double.PositiveInfinity;
        _anchorX = _lastX; _anchorY = _lastY;              // NaN if outside — then the next in-window sample is an ENTER
        _cursorAt = nowMs + _t.CursorTrailMs;              // the cursor goes when the conceal fade has finished
    }

    private void Rearm(double nowMs) { if (_visible) _hideAt = Math.Max(_hideAt, nowMs + _dwell); }

    private void SetHold(ref bool hold, bool on, double nowMs)
    {
        if (hold == on) return;
        hold = on;
        if (!on) Rearm(nowMs);
    }

    private void ShowCursor() { _cursorHidden = false; _cursorAt = double.PositiveInfinity; }
}
