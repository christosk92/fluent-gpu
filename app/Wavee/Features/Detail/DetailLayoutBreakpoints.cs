namespace Wavee.Features.Detail;

/// <summary>Shared detail-page width breakpoints with resize hysteresis (butter-smooth resize v2 §5). Pure static —
/// source-included by Wavee.Tests.</summary>
public static class DetailLayoutBreakpoints
{
    public const float TierHysteresisDip = 24f;
    public const float ModeHysteresisDip = 24f;

    public static int NominalTierFor(float w) =>
        w <= 0f ? 0 : w >= 860f ? 0 : w >= 720f ? 1 : w >= 560f ? 2 : w >= 440f ? 3 : w >= 340f ? 4 : 5;

    /// <summary>Widen immediately; narrow only after <see cref="TierHysteresisDip"/> past the threshold.</summary>
    public static int TierFor(float w, int prev)
    {
        if (w <= 0f) return prev;
        int nominal = NominalTierFor(w);
        if (nominal >= prev) return nominal;
        int dipped = NominalTierFor(w - TierHysteresisDip);
        return dipped < prev ? dipped : prev;
    }

    public const int VerticalMode = 3;
    public const float VerticalEnterW = 540f;
    public const float VerticalExitW = 580f;

    public static int NominalModeFor(float w) =>
        w <= 0f ? 0 : w >= 820f ? 0 : w >= 660f ? 1 : w >= 560f ? 2 : VerticalMode;

    /// <summary>820/660 crossings use <see cref="ModeHysteresisDip"/>; the 540/580 vertical band is unchanged.</summary>
    public static int ModeFor(float w, int currentMode, bool initialized)
    {
        if (w <= 0f) return currentMode;
        if (!initialized) return NominalModeFor(w);
        if (currentMode == VerticalMode) return w >= VerticalExitW ? NominalModeFor(w) : VerticalMode;
        if (w < VerticalEnterW) return VerticalMode;
        int nominal = NominalModeFor(w);
        if (nominal == VerticalMode) return 2;
        if (nominal >= currentMode) return nominal;
        int dipped = NominalModeFor(w - ModeHysteresisDip);
        if (dipped == VerticalMode) dipped = 2;
        return dipped < currentMode ? dipped : currentMode;
    }
}
