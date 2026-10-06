using System.Threading;

namespace FluentGpu.Hosting;

/// <summary>How far the host has released memory since its window stopped being visible. Ordered: a later stage releases
/// strictly more than an earlier one.
/// <list type="bullet">
/// <item><see cref="Visible"/> - nothing released (the window is shown, or has been hidden for less than the stage delay).</item>
/// <item><see cref="Shallow"/> - everything no visible frame needs is released: retained tiles, scratch / retained / group / blur
/// surfaces, the stencil target, free texture pools, the staging ring and the image textures nothing on screen holds. Every one of
/// them is rebuilt on demand by the first frame after the restore, from content that is still in memory (the scene) or from the
/// disk-backed decode path, so a restored window never shows a placeholder, a blank or a stale tile.</item>
/// </list>
/// A deeper stage (pinned image textures) is deliberately absent: it needs a presentation hold on restore and is designed
/// separately.</summary>
public enum HiddenStage : byte
{
    /// <summary>Nothing released.</summary>
    Visible = 0,
    /// <summary>Tiles, scratch, stencil, pools and unpinned image textures released.</summary>
    Shallow = 1,
}

/// <summary>Why the window is parked, in increasing order of certainty that nobody is looking at it. The delay before
/// <see cref="HiddenStage.Shallow"/> depends on it: a window the OS minimized or hid (tray) comes back through a DWM animation that
/// covers a re-raster, while one merely covered by another window comes back the instant that window moves, with no animation and
/// very often (alt-tab), so it must not pay a full re-raster on every return.</summary>
public enum HiddenPark : byte
{
    /// <summary>The window is not parked.</summary>
    None = 0,
    /// <summary>Completely covered by another window (cover-park). Still presentable the instant the occluder moves.</summary>
    Cover = 1,
    /// <summary>Minimized or hidden (tray).</summary>
    Os = 2,
}

/// <summary>Live tunables of the hidden-window memory stages (never an environment variable: <c>--fg hidden=...</c> or code).
/// Each property is individually atomic; the host reads them once per park decision.</summary>
public static class HiddenMemoryBudget
{
    /// <summary>Default park time before <see cref="HiddenStage.Shallow"/> for a minimized / hidden window (2 s: a flap shorter than
    /// this costs nothing).</summary>
    public const long DefaultShallowDelayMs = 2_000;
    /// <summary>Default park time before <see cref="HiddenStage.Shallow"/> for a window that is only covered by another window
    /// (30 s: alt-tab round trips stay free).</summary>
    public const long DefaultCoverShallowDelayMs = 30_000;

    private static long s_shallowDelayMs = DefaultShallowDelayMs;
    private static long s_coverShallowDelayMs = DefaultCoverShallowDelayMs;

    /// <summary>Park time (ms) of a minimized / hidden window before Shallow. &lt;= 0 means at the park edge itself;
    /// <see cref="long.MaxValue"/> disables the stage.</summary>
    public static long ShallowDelayMs
    {
        get => Volatile.Read(ref s_shallowDelayMs);
        set => Volatile.Write(ref s_shallowDelayMs, value);
    }

    /// <summary>Park time (ms) of a window covered by another window before Shallow. &lt;= 0 means at the park edge itself;
    /// <see cref="long.MaxValue"/> disables the stage.</summary>
    public static long CoverShallowDelayMs
    {
        get => Volatile.Read(ref s_coverShallowDelayMs);
        set => Volatile.Write(ref s_coverShallowDelayMs, value);
    }

    /// <summary>Restore both delays to their defaults (tests).</summary>
    public static void Reset()
    {
        ShallowDelayMs = DefaultShallowDelayMs;
        CoverShallowDelayMs = DefaultCoverShallowDelayMs;
    }

    /// <summary>Parse a <c>--fg hidden=SHALLOW[:COVER]</c> value: each term a millisecond count or <c>max</c> (never).
    /// Unparseable terms leave the matching tunable alone and the call returns false.</summary>
    public static bool TryApply(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string[] parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length > 2) return false;
        if (!TryParseDelay(parts[0], out long shallow)) return false;
        long cover = CoverShallowDelayMs;
        if (parts.Length == 2 && !TryParseDelay(parts[1], out cover)) return false;
        ShallowDelayMs = shallow;
        CoverShallowDelayMs = cover;
        return true;
    }

    private static bool TryParseDelay(string s, out long ms)
    {
        if (string.Equals(s, "max", StringComparison.OrdinalIgnoreCase)) { ms = long.MaxValue; return true; }
        return long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ms);
    }
}

/// <summary>The pure stage machine behind the host's hidden-window memory release: feed it the park state once per frame (parked
/// frames included) and it says when a stage is reached. No device, no clock of its own - the caller passes <c>nowMs</c>.
/// <para><c>Visible --park--&gt; (Visible, timer) --delay--&gt; Shallow</c>; any un-park returns to <see cref="HiddenStage.Visible"/>
/// at once. A park shorter than its delay never leaves <see cref="HiddenStage.Visible"/>.</para></summary>
public struct HiddenMemoryPolicy
{
    private long _parkedSinceMs;
    private bool _parked;

    /// <summary>The stage in force.</summary>
    public HiddenStage Stage { get; private set; }

    /// <summary>The park delay (ms) that applies to <paramref name="park"/> under the current tunables.</summary>
    public static long DelayFor(HiddenPark park) => park == HiddenPark.Os ? HiddenMemoryBudget.ShallowDelayMs : HiddenMemoryBudget.CoverShallowDelayMs;

    /// <summary>Pure: the stage a window parked for <paramref name="parkedForMs"/> reaches given the Shallow delay.</summary>
    public static HiddenStage StageFor(long parkedForMs, long shallowDelayMs)
        => shallowDelayMs != long.MaxValue && parkedForMs >= shallowDelayMs ? HiddenStage.Shallow : HiddenStage.Visible;

    /// <summary>Feed the park state for this frame. Returns the NEW stage when it changed, else null. The caller treats a change
    /// to <see cref="HiddenStage.Visible"/> as the restore edge for memory.</summary>
    public HiddenStage? Advance(HiddenPark park, long nowMs)
    {
        HiddenStage before = Stage;
        if (park == HiddenPark.None)
        {
            _parked = false;
            Stage = HiddenStage.Visible;
        }
        else
        {
            if (!_parked) { _parked = true; _parkedSinceMs = nowMs; }
            if (Stage == HiddenStage.Visible) Stage = StageFor(nowMs - _parkedSinceMs, DelayFor(park));
        }
        return Stage == before ? null : Stage;
    }

    /// <summary>Milliseconds until the next stage is reached (0 when due now), or -1 when none is pending (not parked, already at
    /// the last stage, or the stage is disabled). The host clamps its parked wait with it, so a hidden window wakes exactly once.</summary>
    public readonly long NextDueInMs(HiddenPark park, long nowMs)
    {
        if (!_parked || park == HiddenPark.None || Stage != HiddenStage.Visible) return -1;
        long delay = DelayFor(park);
        if (delay == long.MaxValue) return -1;
        return Math.Max(0, _parkedSinceMs + delay - nowMs);
    }
}
