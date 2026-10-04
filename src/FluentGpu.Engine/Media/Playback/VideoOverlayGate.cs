using System.Diagnostics;

namespace FluentGpu.Media;

/// <summary>
/// F087: the per-surface promotion state of the overlay mode - whether the video visual sits ABOVE the UI plane (nothing paints over
/// its rect) or below it (the hole-punched underlay, the only placement before). Promotion needs the overlay switch, a presenter
/// that reports support and an occlusion verdict of "clear"; demotion happens the SAME turn something paints over the rect (the
/// chrome must never be hidden by the video). After a demotion the gate holds the underlay for <see cref="HoldQpc"/>, so a chrome that
/// toggles faster than that never flips the visual's z-order back and forth (Chromium's promotion delay). The UI hole is not part
/// of this: it stays punched in both modes, so the hole itself never flaps. Pure and idempotent for one (eligible, clear, now): the
/// applier evaluates it on every geometry turn.
/// </summary>
internal struct VideoOverlayGate
{
    /// <summary>How long an underlay that was just demoted from overlay stays an underlay.</summary>
    public const int HoldMs = 500;

    /// <summary>The hold in <see cref="Stopwatch.GetTimestamp"/> ticks.</summary>
    public static long HoldQpc => Stopwatch.Frequency * HoldMs / 1000;

    private long _holdUntilQpc;

    /// <summary>True while the video is promoted above the UI plane.</summary>
    public bool Above { get; private set; }

    /// <summary>Fold one turn's inputs in and return whether the video is above the UI plane after it.
    /// <paramref name="eligible"/>: switch on and the presenter reports overlay support. <paramref name="clear"/>: nothing paints over
    /// the video rect on this turn's composite. <paramref name="nowQpc"/>: <see cref="Stopwatch.GetTimestamp"/>.</summary>
    public bool Update(bool eligible, bool clear, long nowQpc)
    {
        if (!eligible)
        {
            Above = false;
            return false;
        }
        if (!clear)
        {
            if (Above)
            {
                Above = false;
                _holdUntilQpc = nowQpc + HoldQpc;
            }
            return false;
        }
        if (!Above && nowQpc >= _holdUntilQpc) Above = true;
        return Above;
    }

    /// <summary>Forget the state (the presenter was replaced: its visuals, and so the z-order, are gone).</summary>
    public void Reset()
    {
        Above = false;
        _holdUntilQpc = 0;
    }
}
