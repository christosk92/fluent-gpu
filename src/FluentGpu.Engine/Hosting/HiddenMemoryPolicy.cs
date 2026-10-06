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
/// <item><see cref="Deep"/> - additionally the image textures the window's own content held: pinned covers, row cells, every id the
/// scene still names. Each is restarted through the ordinary decode path by the restore, and the first frame back is HELD (recorded
/// and submitted but not presented) until they are resident again, so nothing a user saw before the hide returns as a placeholder.
/// Reached only by a minimized / tray-hidden window, never by one merely covered by another window.</item>
/// </list></summary>
public enum HiddenStage : byte
{
    /// <summary>Nothing released.</summary>
    Visible = 0,
    /// <summary>Tiles, scratch, stencil, pools and unpinned image textures released.</summary>
    Shallow = 1,
    /// <summary>Shallow, plus the held image textures (see the type remarks); a restore holds the first frame back until they return.</summary>
    Deep = 2,
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
    /// <summary>Default time a window must be minimized / hidden before <see cref="HiddenStage.Deep"/> (5 minutes). A window that is
    /// only covered never reaches Deep.</summary>
    public const long DefaultDeepDelayMs = 300_000;
    /// <summary>Default longest (ms) a Deep restore holds its first frame back waiting for the released images. About the length of
    /// the OS minimize / restore animation, which hides the held frame behind the window's own motion.</summary>
    public const long DefaultRestoreHoldMaxMs = 200;

    private static long s_shallowDelayMs = DefaultShallowDelayMs;
    private static long s_coverShallowDelayMs = DefaultCoverShallowDelayMs;
    private static long s_deepDelayMs = DefaultDeepDelayMs;
    private static long s_restoreHoldMaxMs = DefaultRestoreHoldMaxMs;

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

    /// <summary>Time (ms) a window must have been minimized / hidden (counted from the moment it became so) before Deep. &lt;= 0
    /// means at the park edge itself; <see cref="long.MaxValue"/> disables Deep.</summary>
    public static long DeepDelayMs
    {
        get => Volatile.Read(ref s_deepDelayMs);
        set => Volatile.Write(ref s_deepDelayMs, value);
    }

    /// <summary>Longest (ms) the first frame after a Deep restore is held back. 0 disables the hold: the restore then presents at
    /// once and whatever is still decoding fades in through the normal warm reveal.</summary>
    public static long RestoreHoldMaxMs
    {
        get => Volatile.Read(ref s_restoreHoldMaxMs);
        set => Volatile.Write(ref s_restoreHoldMaxMs, Math.Max(0, value));
    }

    /// <summary>Restore every tunable to its default (tests).</summary>
    public static void Reset()
    {
        ShallowDelayMs = DefaultShallowDelayMs;
        CoverShallowDelayMs = DefaultCoverShallowDelayMs;
        DeepDelayMs = DefaultDeepDelayMs;
        RestoreHoldMaxMs = DefaultRestoreHoldMaxMs;
    }

    /// <summary>Parse a <c>--fg hidden=SHALLOW[:COVER[:DEEP[:HOLD]]]</c> value. Each term is a millisecond count (or <c>max</c> =
    /// never; HOLD 0 = no hold) and is positional, or named <c>shallow=</c> / <c>cover=</c> / <c>deep=</c> / <c>hold=</c> in any
    /// order (<c>hidden=deep=max</c> disables Deep alone). Nothing is applied unless every term parses.</summary>
    public static bool TryApply(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string[] parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length > 4) return false;
        long shallow = ShallowDelayMs, cover = CoverShallowDelayMs, deep = DeepDelayMs, hold = RestoreHoldMaxMs;
        for (int i = 0; i < parts.Length; i++)
        {
            string term = parts[i];
            int slot = i;
            int eq = term.IndexOf('=');
            if (eq >= 0)
            {
                slot = term[..eq].Trim().ToLowerInvariant() switch { "shallow" => 0, "cover" => 1, "deep" => 2, "hold" => 3, _ => -1 };
                if (slot < 0) return false;
                term = term[(eq + 1)..].Trim();
            }
            if (!TryParseDelay(term, out long ms)) return false;
            switch (slot)
            {
                case 0: shallow = ms; break;
                case 1: cover = ms; break;
                case 2: deep = ms; break;
                default: hold = ms == long.MaxValue ? 60_000 : ms; break;   // "max" hold = as long as the guard allows
            }
        }
        ShallowDelayMs = shallow;
        CoverShallowDelayMs = cover;
        DeepDelayMs = deep;
        RestoreHoldMaxMs = hold;
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
/// <para><c>Visible --park--&gt; (Visible, timer) --delay--&gt; Shallow --deep delay--&gt; Deep</c>; any un-park returns to
/// <see cref="HiddenStage.Visible"/> at once. A park shorter than its delay never leaves <see cref="HiddenStage.Visible"/>.
/// <see cref="HiddenStage.Deep"/> is reached only while the park is <see cref="HiddenPark.Os"/> (a minimize or tray hide), counted
/// from the moment it became one: a window covered for an hour and then minimized has not been hidden for an hour. A window that
/// reached Deep and is then merely covered (restored beneath a maximized window) keeps its stage until it is un-parked.</para></summary>
public struct HiddenMemoryPolicy
{
    private long _parkedSinceMs;
    private long _osSinceMs;
    private bool _parked;
    private bool _os;

    /// <summary>The stage in force.</summary>
    public HiddenStage Stage { get; private set; }

    /// <summary>The park delay (ms) that applies to <paramref name="park"/> under the current tunables.</summary>
    public static long DelayFor(HiddenPark park) => park == HiddenPark.Os ? HiddenMemoryBudget.ShallowDelayMs : HiddenMemoryBudget.CoverShallowDelayMs;

    /// <summary>Pure: the stage a window parked for <paramref name="parkedForMs"/> reaches given the Shallow delay.</summary>
    public static HiddenStage StageFor(long parkedForMs, long shallowDelayMs)
        => shallowDelayMs != long.MaxValue && parkedForMs >= shallowDelayMs ? HiddenStage.Shallow : HiddenStage.Visible;

    /// <summary>Pure: the stage reached when the window has been parked for <paramref name="parkedForMs"/> in total, of which
    /// <paramref name="osForMs"/> as a minimize / hide. <see cref="HiddenStage.Deep"/> needs the park to be
    /// <see cref="HiddenPark.Os"/> NOW.</summary>
    public static HiddenStage StageFor(long parkedForMs, long osForMs, HiddenPark park, long shallowDelayMs, long deepDelayMs)
        => park == HiddenPark.Os && shallowDelayMs != long.MaxValue && deepDelayMs != long.MaxValue && osForMs >= deepDelayMs
            ? HiddenStage.Deep
            : StageFor(parkedForMs, shallowDelayMs);

    /// <summary>Feed the park state for this frame. Returns the NEW stage when it changed, else null. The caller treats a change
    /// to <see cref="HiddenStage.Visible"/> as the restore edge for memory. While parked a stage never decreases.</summary>
    public HiddenStage? Advance(HiddenPark park, long nowMs)
    {
        HiddenStage before = Stage;
        if (park == HiddenPark.None)
        {
            _parked = false;
            _os = false;
            Stage = HiddenStage.Visible;
        }
        else
        {
            if (!_parked) { _parked = true; _parkedSinceMs = nowMs; }
            if (park == HiddenPark.Os) { if (!_os) { _os = true; _osSinceMs = nowMs; } }
            else _os = false;
            HiddenStage reached = StageFor(nowMs - _parkedSinceMs, _os ? nowMs - _osSinceMs : 0, park, DelayFor(park),
                HiddenMemoryBudget.DeepDelayMs);
            if (reached > Stage) Stage = reached;
        }
        return Stage == before ? null : Stage;
    }

    /// <summary>Milliseconds until the next stage is reached (0 when due now), or -1 when none is pending (not parked, already at
    /// the last stage this park can reach, or the stage is disabled). The host clamps its parked wait with it, so a hidden window
    /// wakes once per stage.</summary>
    public readonly long NextDueInMs(HiddenPark park, long nowMs)
    {
        if (!_parked || park == HiddenPark.None || Stage == HiddenStage.Deep) return -1;
        long due = long.MaxValue;
        if (Stage == HiddenStage.Visible)
        {
            long delay = DelayFor(park);
            if (delay != long.MaxValue) due = _parkedSinceMs + delay;
        }
        long deep = HiddenMemoryBudget.DeepDelayMs;
        if (park == HiddenPark.Os && deep != long.MaxValue && DelayFor(park) != long.MaxValue)
        {
            long osSince = _os ? _osSinceMs : nowMs;   // an Os park not yet fed to Advance counts from now
            due = Math.Min(due, osSince + deep);
        }
        return due == long.MaxValue ? -1 : Math.Max(0, due - nowMs);
    }
}
