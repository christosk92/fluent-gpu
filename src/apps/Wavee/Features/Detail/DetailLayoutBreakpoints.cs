namespace Wavee.Features.Detail;

/// <summary>Shared detail-page width breakpoints with resize hysteresis (butter-smooth resize v2 §5). Pure static —
/// source-included by Wavee.Tests.</summary>
public static class DetailLayoutBreakpoints
{
    public const float TierHysteresisDip = 24f;
    public const float ModeHysteresisDip = 24f;

    public static int NominalTierFor(float w) =>
        w <= 0f ? 0 : w >= 860f ? 0 : w >= 720f ? 1 : w >= 560f ? 2 : w >= 440f ? 3 : w >= 340f ? 4 : w >= 300f ? 5 : 6;

    /// <summary>Safe pre-measure seed from the window viewport, so a 360-DIP launch never composes the wide table for
    /// its first frame.</summary>
    public static int InitialTierForViewport(float viewportWidth) => NominalTierFor(viewportWidth);

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
    public const float TwoColumnContentMinW = 300f;

    /// <summary>The ultra-narrow vertical page may use the track table's tier-6 layout all the way down. Retain the
    /// 300-DIP guard only for transient/two-column frames whose active column set still needs it.</summary>
    public static float ContentMinWidthForMode(int mode)
        => mode == VerticalMode ? 0f : TwoColumnContentMinW;

    public static int NominalModeFor(float w) =>
        w <= 0f ? 0 : w >= 820f ? 0 : w >= 660f ? 1 : w >= 560f ? 2 : VerticalMode;

    /// <summary>Pre-measure page-system seed. Ultra-narrow launches choose Vertical before the first bounds callback.</summary>
    public static int InitialModeForViewport(float viewportWidth) => NominalModeFor(viewportWidth);

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
