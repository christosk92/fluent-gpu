using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>Which panel the right rail is showing. Lyrics ships first; Queue/Details are future tabs.</summary>
public enum RailMode { Lyrics, Queue, Details }

/// <summary>
/// UI-only chrome state for the WaveeMusic-style right rail (the now-playing / lyrics sidebar). Kept off
/// <see cref="PlaybackBridge"/> so the bridge stays about playback, not chrome. Created and provided by
/// <c>WaveeShell</c> via <see cref="Slot"/>; the player-bar toggle writes it, the <c>RightRail</c> + lyrics view read it.
/// Independent of <see cref="PlaybackBridge.Expanded"/> (the fullscreen now-playing takeover) — the rail is the side panel.
/// </summary>
public sealed class ShellUi
{
    public static readonly Context<ShellUi?> Slot = new(null);

    /// <summary>The rail is open (visible). When false the rail slot animates its width to 0.</summary>
    public Signal<bool> RailOpen { get; } = new(false);

    /// <summary>Which panel the rail shows when open.</summary>
    public Signal<RailMode> Mode { get; } = new(RailMode.Lyrics);

    /// <summary>The rail's expanded width in DIP (WaveeMusic default 300, range 200..500; seeded a touch wider for lyric readability).</summary>
    public FloatSignal RailWidth { get; } = new(340f);

    /// <summary>Whether the rail can currently fit alongside the sidebar + a usable content region (maintained by
    /// WaveeShell from the live viewport / sidebar / rail widths). The player-bar lyrics button reads it to decide
    /// rail-vs-fullscreen; when false, opening the rail would clip content, so the fullscreen now-playing is used instead.</summary>
    public Signal<bool> RailFits { get; } = new(true);

    /// <summary>Defer responsive breakpoint reactions while a rail open/close reflow is in flight. Set true whenever the
    /// rail toggles (via <see cref="ArmRailLock"/>) and cleared once the RailReflow spring settles; the track-list tier +
    /// detail mode handlers skip writing during the lock so intermediate spring widths don't churn multiple remounts
    /// (the open/close flash).</summary>
    public Signal<bool> RailLayoutLocked { get; } = new(false);

    long _railLockArmedAt;

    /// <summary>Arm the layout-defer lock, stamping the arm time for the fail-safe below.</summary>
    public void ArmRailLock() { _railLockArmedAt = System.Environment.TickCount64; RailLayoutLocked.Value = true; }

    /// <summary>FAIL-SAFE lock read for the breakpoint gates (detail mode / track-list tier): active only within a
    /// bounded window after arming. The clear rides a one-shot Timer→post; if that post is ever lost the raw signal
    /// sticks TRUE and every breakpoint freezes — a stale tier then feeds the grid a width far below its column set's
    /// minimum and the overflow guard crushes columns into overlapping glyphs (the "detail page breaks completely at
    /// narrow width" failure). Time-bounding the GATES (the flush effects still key on the signal edge) caps any stuck
    /// lock at under a second.</summary>
    public bool RailLockActive => RailLayoutLocked.Peek() && System.Environment.TickCount64 - _railLockArmedAt < 900;

    /// <summary>WaveeMusic <c>ToggleRightPanel(mode)</c> semantics: clicking the already-showing mode closes the rail;
    /// otherwise switch to that mode and open.</summary>
    public void Toggle(RailMode mode)
    {
        if (RailOpen.Peek() && Mode.Peek() == mode) { RailOpen.Value = false; return; }
        Mode.Value = mode;
        RailOpen.Value = true;
    }

    /// <summary>Viewport-fit test: does the sidebar + rail + a minimum usable content region fit in the viewport width?
    /// Pass the live <see cref="RailWidth"/> for <paramref name="railW"/> so a user-widened rail is checked accurately.</summary>
    public static bool CanFitRail(float viewportW, float sidebarW, float railW = 340f, float minContentW = 480f)
        => sidebarW + railW + minContentW <= viewportW;
}
