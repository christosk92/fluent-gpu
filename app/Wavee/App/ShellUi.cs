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

    /// <summary>WaveeMusic <c>ToggleRightPanel(mode)</c> semantics: clicking the already-showing mode closes the rail;
    /// otherwise switch to that mode and open.</summary>
    public void Toggle(RailMode mode)
    {
        if (RailOpen.Peek() && Mode.Peek() == mode) { RailOpen.Value = false; return; }
        Mode.Value = mode;
        RailOpen.Value = true;
    }
}
