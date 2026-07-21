using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>Which panel the right rail is showing.</summary>
public enum RailMode { Lyrics, Queue, Details, Friends }

/// <summary>
/// UI-only chrome state for the WaveeMusic-style right rail. Kept off <see cref="PlaybackBridge"/> so the bridge stays
/// about playback, not chrome. Created and provided by <c>WaveeShell</c> via <see cref="Slot"/>; the player-bar toggle
/// writes it, while <c>RightRail</c> and the panel views read it.
/// </summary>
public sealed class ShellUi
{
    public static readonly Context<ShellUi?> Slot = new(null);

    /// <summary>The rail is open. When false the rail slot animates its width to 0.</summary>
    public Signal<bool> RailOpen { get; } = new(false);

    /// <summary>Which panel the rail shows when open.</summary>
    public Signal<RailMode> Mode { get; } = new(RailMode.Lyrics);

    /// <summary>The rail's expanded width in DIP.</summary>
    public FloatSignal RailWidth { get; } = new(340f);

    /// <summary>
    /// Whether the rail can currently reserve inline layout width alongside the sidebar and content region. When false,
    /// the shell floats the rail over content instead of allocating row width for it.
    /// </summary>
    public Signal<bool> RailFits { get; } = new(true);

    /// <summary>
    /// Defer responsive breakpoint reactions while a rail open/close reflow is in flight. This prevents intermediate
    /// spring widths from churning multiple remounts.
    /// </summary>
    public Signal<bool> RailLayoutLocked { get; } = new(false);

    long _railLockArmedAt;

    /// <summary>Arm the layout-defer lock, stamping the arm time for the fail-safe below.</summary>
    public void ArmRailLock()
    {
        _railLockArmedAt = System.Environment.TickCount64;
        RailLayoutLocked.Value = true;
    }

    /// <summary>Time-bounded fail-safe for breakpoint gates in case the timer-driven clear is lost.</summary>
    public bool RailLockActive => RailLayoutLocked.Peek() && System.Environment.TickCount64 - _railLockArmedAt < 900;

    /// <summary>Clicking the already-showing mode closes the rail; otherwise switch to that mode and open.</summary>
    public void Toggle(RailMode mode)
    {
        if (RailOpen.Peek() && Mode.Peek() == mode) { RailOpen.Value = false; return; }
        Mode.Value = mode;
        RailOpen.Value = true;
    }

    /// <summary>Viewport-fit test for sidebar + rail + a minimum usable content region.</summary>
    public static bool CanFitRail(float viewportW, float sidebarW, float railW = 340f, float minContentW = 480f)
        => sidebarW + railW + minContentW <= viewportW;
}
