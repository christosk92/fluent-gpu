using FluentGpu.Controls.Media;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>The media transport's show/hide policy (<see cref="PlayerChromeVisibility"/>) — the pure clocked state
/// machine every video surface runs. Each test names the player behaviour it pins (docs/plans/wavee/
/// popout-player-drag-autohide-plan.md §3 in the Wavee repo); the D-numbers are that plan's defects.</summary>
public sealed class PlayerChromeVisibilityTests
{
    static readonly PlayerChromeTiming T = new(IdleHideMs: 3000, TouchIdleHideMs: 4000, KeyboardIdleHideMs: 4000,
        LeaveHideMs: 150, CursorTrailMs: 400, DeadzoneDip: 3f);

    /// Playing, cursor allowed, pointer resting inside at (100,100), revealed at t.
    static PlayerChromeVisibility Resting(double t = 0)
    {
        var m = new PlayerChromeVisibility(T, t);
        m.SetCursorMayHide(true, t);
        m.PointerMoved(100, 100, t);
        return m;
    }

    [Fact]
    public void AFreshSurfaceShowsItsControlsThenIdlesAway()
    {
        var m = new PlayerChromeVisibility(T, 0);
        Assert.True(m.ChromeVisible);
        Assert.Equal(3000, m.NextWakeMs);
        m.Tick(2999); Assert.True(m.ChromeVisible);
        Assert.True(m.Tick(3000)); Assert.False(m.ChromeVisible);
        Assert.Equal("idle", m.LastCause);
    }

    [Fact]
    public void EveryRealMoveRestartsTheDwell()
    {
        var m = Resting();
        m.PointerMoved(101, 100, 2500);
        m.Tick(3000); Assert.True(m.ChromeVisible);
        m.Tick(5500); Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void ARedeliveredSamePointIsNotActivity()          // spurious WM_MOUSEMOVE on show/hide/move
    {
        var m = Resting();
        m.PointerMoved(100, 100, 2500);
        m.Tick(3000); Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void JitterInsideTheDeadzoneNeverReveals()
    {
        var m = Resting(); m.Tick(3000);                                // hidden; rest point (100,100)
        m.PointerMoved(101, 101, 3100); m.PointerMoved(99, 101, 3200); m.PointerMoved(102, 99, 3300);
        Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void ASlowCreepRevealsOnceItLeavesTheRestPoint()   // D3: the per-sample test never fired here
    {
        var m = Resting(); m.Tick(3000);
        m.PointerMoved(101, 100, 3100); m.PointerMoved(102, 100, 3200);
        Assert.False(m.ChromeVisible);
        m.PointerMoved(103, 100, 3300);
        Assert.True(m.ChromeVisible);
        Assert.Equal("move", m.LastCause);
    }

    [Fact]
    public void EnteringRevealsImmediately()
    {
        var m = Resting();
        m.PointerLeft(100); m.Tick(250); Assert.False(m.ChromeVisible);
        m.PointerMoved(5, 5, 1000);
        Assert.True(m.ChromeVisible);
        Assert.Equal("enter", m.LastCause);
    }

    [Fact]
    public void LeavingHidesAfterTheDebounceNotTheDwell()     // D4
    {
        var m = Resting();
        m.PointerLeft(500);
        Assert.Equal(650, m.NextWakeMs);
        m.Tick(649); Assert.True(m.ChromeVisible);
        m.Tick(650); Assert.False(m.ChromeVisible);
        Assert.Equal("leave", m.LastCause);
    }

    [Fact]
    public void HoldsNeverRevealHiddenChrome()                // D2: a suppressor used to REVEAL
    {
        var m = Resting(); m.Tick(3000);
        m.SetScrubbing(true, 3100); m.SetMenuOpen(true, 3200); m.SetPlayback(ChromePlayback.Playing, 3300);
        m.SetPointerOverControls(true, 3400);
        Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void RestingOnTheControlsHoldsIndefinitely_LeavingThemRestartsTheDwell()
    {
        var m = Resting();
        m.SetPointerOverControls(true, 500);
        m.Tick(60_000); Assert.True(m.ChromeVisible);
        m.SetPointerOverControls(false, 60_000);
        m.Tick(62_999); Assert.True(m.ChromeVisible);
        m.Tick(63_000); Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void MenuHoldsThroughALeave_CloseRestartsTheDwell() // windowed popup: the pointer left the client for it
    {
        var m = Resting();
        m.SetMenuOpen(true, 100); m.PointerLeft(200);
        m.Tick(10_000); Assert.True(m.ChromeVisible);
        m.SetMenuOpen(false, 10_000);
        m.Tick(12_999); Assert.True(m.ChromeVisible);
        m.Tick(13_000); Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void PressHolds_ReleaseRestartsTheDwell()
    {
        var m = Resting();
        m.SetPressed(true, 1000);
        m.Tick(50_000); Assert.True(m.ChromeVisible);
        m.SetPressed(false, 50_000);
        m.Tick(52_999); Assert.True(m.ChromeVisible);
        m.Tick(53_000); Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void APressRevealsHiddenChrome()                   // reveal-only click
    {
        var m = Resting(); m.Tick(3000);
        m.SetPressed(true, 4000);
        Assert.True(m.ChromeVisible);
        Assert.Equal("press", m.LastCause);
    }

    [Fact]
    public void PauseRevealsAndPins_ResumeStartsAFreshDwell()
    {
        var m = Resting(); m.Tick(3000);
        m.SetPlayback(ChromePlayback.Paused, 4000); Assert.True(m.ChromeVisible);
        m.Tick(1_000_000); Assert.True(m.ChromeVisible);
        Assert.Equal(double.PositiveInfinity, m.NextWakeMs);        // a hold schedules nothing — the loop may idle
        m.SetPlayback(ChromePlayback.Playing, 1_000_000);
        m.Tick(1_002_999); Assert.True(m.ChromeVisible);
        m.Tick(1_003_000); Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void TapToggles_ButAHardHoldKeepsItUp()
    {
        var m = new PlayerChromeVisibility(T, 0);
        m.Tapped(100); Assert.False(m.ChromeVisible);
        m.Tapped(200); Assert.True(m.ChromeVisible);
        Assert.Equal(4200, m.NextWakeMs);                           // the touch dwell
        m.SetMenuOpen(true, 300);
        m.Tapped(400); Assert.True(m.ChromeVisible);                // a picker is open: not a dismiss
        m.SetMenuOpen(false, 500);
        m.SetPlayback(ChromePlayback.Paused, 600);
        m.Tapped(700); Assert.False(m.ChromeVisible);               // paused is SOFT — an explicit tap still hides
    }

    [Fact]
    public void KeyboardFocusRevealsWithTheLongDwell_AndHolds()
    {
        var m = Resting(); m.Tick(3000);
        m.SetKeyboardFocusInControls(true, 5000); Assert.True(m.ChromeVisible);
        m.Tick(100_000); Assert.True(m.ChromeVisible);
        m.SetKeyboardFocusInControls(false, 100_000);
        m.Tick(103_999); Assert.True(m.ChromeVisible);
        m.Tick(104_000); Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void AHandledKeyRevealsWithTheKeyboardDwell()
    {
        var m = Resting(); m.Tick(3000);
        m.Activity(ChromeActivity.Keyboard, 5000);
        Assert.True(m.ChromeVisible);
        Assert.Equal(9000, m.NextWakeMs);
    }

    [Fact]
    public void CursorHidesWhenTheFadeEnds_OnlyInsideAndOnlyWhenAllowed()
    {
        var m = Resting();
        m.Tick(3000); Assert.False(m.CursorHidden);
        Assert.Equal(3400, m.NextWakeMs);
        m.Tick(3400); Assert.True(m.CursorHidden);

        var inline = new PlayerChromeVisibility(T, 0);              // policy: no (inline, windowed)
        inline.PointerMoved(1, 1, 0);
        inline.Tick(3000); inline.Tick(10_000);
        Assert.False(inline.CursorHidden);
        Assert.Equal(double.PositiveInfinity, inline.NextWakeMs);
    }

    [Fact]
    public void CursorReturnsOnActivityAndOnLeave()
    {
        var m = Resting(); m.Tick(3000); m.Tick(3400);
        m.PointerMoved(110, 100, 5000); Assert.False(m.CursorHidden);
        m.Tick(8000); m.Tick(8400); Assert.True(m.CursorHidden);
        m.PointerLeft(9000); Assert.False(m.CursorHidden);
    }

    [Fact]
    public void AllowingTheCursorWhileHiddenHidesItNow()       // entering fullscreen with idle chrome
    {
        var m = new PlayerChromeVisibility(T, 0);
        m.PointerMoved(100, 100, 0); m.Tick(3000);
        m.SetCursorMayHide(true, 5000);
        Assert.True(m.NextWakeMs <= 5000);
        m.Tick(5000); Assert.True(m.CursorHidden);
    }

    [Fact]
    public void APointerRestingAtMountLetsTheCursorHideWithoutRevealing()
    {
        var m = new PlayerChromeVisibility(T, 0);
        m.SetCursorMayHide(true, 0);
        m.PointerPresent(50, 50);
        m.PointerMoved(50, 50, 1000);                               // the dispatcher re-delivering the same point
        m.Tick(3000); Assert.False(m.ChromeVisible);                 // idled from the MOUNT, not from 1000
        m.Tick(3400); Assert.True(m.CursorHidden);
    }

    [Fact]
    public void WindowMoveHoldsUntilTheLoopEnds_ThenDwells()  // D7: nothing stays pinned after a drag
    {
        var m = Resting();
        m.SetPressed(true, 100);
        m.WindowMoveStarted(150);
        m.PointerCovered(160);                                      // the loop's capture cancel — not a leave
        m.Tick(60_000); Assert.True(m.ChromeVisible);
        m.WindowMoveEnded(60_000);
        m.Tick(62_999); Assert.True(m.ChromeVisible);
        m.Tick(63_000); Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void AStrayMoveSizeEndIsInert()                    // edge resizes raise the same event
    {
        var m = Resting(); m.Tick(3000);
        m.WindowMoveEnded(3500);
        Assert.False(m.ChromeVisible);
    }

    [Fact]
    public void DisabledPinsTheChromeAndNeverHidesTheCursor()
    {
        var m = Resting();
        m.SetEnabled(false, 0);
        m.Tick(100_000);
        Assert.True(m.ChromeVisible); Assert.False(m.CursorHidden);
        Assert.Equal(ChromeHold.Disabled, m.Holds);
    }

    [Fact]
    public void AScrimTakingHoverDropsThePointerHoldsWithoutHiding()
    {
        var m = Resting();
        m.SetPointerOverControls(true, 100);
        m.PointerCovered(200);
        Assert.Equal(ChromeHold.None, m.Holds);
        Assert.Equal(3200, m.NextWakeMs);                           // the dwell restarts; no leave debounce
        m.PointerMoved(100, 100, 300);                              // the same point re-delivered once the scrim goes
        m.Tick(3199); Assert.True(m.ChromeVisible);
        m.Tick(3200); Assert.False(m.ChromeVisible);
    }
}
