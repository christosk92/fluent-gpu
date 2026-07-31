using System;

namespace Wavee;

/// <summary>Pure whole-shell breakpoints. The bands are hysteretic so a boundary crossing during pointer resize
/// commits one structural change instead of oscillating on adjacent frames.</summary>
public static class ShellResponsiveLayout
{
    public const float NarrowEnterW = 720f;
    public const float NarrowLeaveW = 760f;
    public const float CompactRailW = 56f;
    public const float DrawerMinW = 240f;
    public const float DrawerViewportInset = 32f;

    public const float ToolbarNarrowEnterW = 520f;
    public const float ToolbarNarrowLeaveW = 560f;

    // ── nav-pane (sidebar) width ─────────────────────────────────────────────────────────────────────────────────────
    // The single clamp bounds for the expanded pane. Every writer (the seam drag, the probe seam, the responsive default)
    // must clamp through these — a second literal pair is how the drag and the probe drifted apart.
    public const float NavPaneMinW = 240f, NavPaneMaxW = 460f;

    // Stock NavigationView authors OpenPaneLength per window class rather than one fixed number: the 240-DIP floor for
    // ordinary windows and 320 once the window is wide enough that a 240 pane reads as a cramped gutter beside a very wide
    // content card. These three tiers are that ladder; they are the DEFAULT only — a user who drags the seam pins their own
    // width (SidebarPreferences.WidthUserSet, per design) and the ladder stops applying for THAT design.
    // Each sidebar design has its own triple (see NavPaneTiers); these three consts are Classic's.
    public const float NavPaneMidEnterW = 1400f;    // ≥ this → the MID tier
    public const float NavPaneWideEnterW = 1800f;   // ≥ this → the WIDE tier
    public const float NavPaneNarrowW = 240f, NavPaneMidW = 280f, NavPaneWideW = 320f;   // Classic's ladder
    public const float NavPaneHysteresisDip = 24f;

    /// <summary>Classic's tier triple — the values every no-triple overload below forwards with, so an existing call site
    /// that knows nothing about sidebar designs keeps its exact behavior.</summary>
    public static (float Narrow, float Mid, float Wide) ClassicTiers => (NavPaneNarrowW, NavPaneMidW, NavPaneWideW);

    /// <summary>The nav-pane width tiers for a sidebar DESIGN (locked decision 14: Classic 240/280/320 · Library V3
    /// 300/340/380 · Curated 280/320/360). One indirection to <c>SidebarDesignInfo.Tiers</c>, which is the single owner of
    /// the values; this is the name the shell and the mode surfaces call. The breakpoints (1400/1800), the 24-DIP shrink
    /// hysteresis, the 240/460 clamp and the 720/760 narrow band are IDENTICAL for all three designs — only the three tier
    /// values differ.</summary>
    public static (float Narrow, float Mid, float Wide) NavPaneTiers(SidebarDesign design)
        => SidebarDesignInfo.Tiers(design);

    public static float NominalNavPaneDefaultFor(float viewportWidth)
        => NominalNavPaneDefaultFor(viewportWidth, ClassicTiers);

    public static float NominalNavPaneDefaultFor(float viewportWidth, in (float Narrow, float Mid, float Wide) tiers) =>
        viewportWidth >= NavPaneWideEnterW ? tiers.Wide
        : viewportWidth >= NavPaneMidEnterW ? tiers.Mid
        : tiers.Narrow;

    /// <summary>Pre-measure seed. A zero/unknown viewport (the shell constructor, before the first bounds callback) takes
    /// the narrow tier; the viewport effect commits the real tier before the first layout.</summary>
    public static float InitialNavPaneDefaultForViewport(float viewportWidth)
        => InitialNavPaneDefaultForViewport(viewportWidth, ClassicTiers);

    /// <inheritdoc cref="InitialNavPaneDefaultForViewport(float)"/>
    public static float InitialNavPaneDefaultForViewport(float viewportWidth, in (float Narrow, float Mid, float Wide) tiers)
        => viewportWidth <= 0f ? tiers.Narrow : NominalNavPaneDefaultFor(viewportWidth, tiers);

    /// <summary>Widen immediately; shrink only after <see cref="NavPaneHysteresisDip"/> past the threshold — the
    /// <c>DetailLayoutBreakpoints.TierFor</c> idiom, with the dip ADDED because here a LARGER number is the wider tier
    /// (there it is a tier index, where larger is narrower). So 1400 widens to the mid tier at once, and the mid tier holds
    /// down to 1376.</summary>
    public static float NavPaneDefaultFor(float viewportWidth, float current, bool initialized)
        => NavPaneDefaultFor(viewportWidth, current, initialized, ClassicTiers);

    /// <inheritdoc cref="NavPaneDefaultFor(float, float, bool)"/>
    public static float NavPaneDefaultFor(float viewportWidth, float current, bool initialized,
                                          in (float Narrow, float Mid, float Wide) tiers)
    {
        if (viewportWidth <= 0f) return current;
        if (!initialized) return NominalNavPaneDefaultFor(viewportWidth, tiers);
        float nominal = NominalNavPaneDefaultFor(viewportWidth, tiers);
        if (nominal >= current) return nominal;
        float dipped = NominalNavPaneDefaultFor(viewportWidth + NavPaneHysteresisDip, tiers);
        return dipped < current ? dipped : current;
    }

    public static bool NarrowFor(float width, bool current, bool initialized)
    {
        if (width <= 0f) return current;
        if (!initialized) return width <= NarrowEnterW;
        return current ? width < NarrowLeaveW : width <= NarrowEnterW;
    }

    public static bool ToolbarNarrowFor(float width, bool current, bool initialized)
    {
        if (width <= 0f) return current;
        if (!initialized) return width <= ToolbarNarrowEnterW;
        return current ? width < ToolbarNarrowLeaveW : width <= ToolbarNarrowEnterW;
    }

    public static float DrawerWidth(float viewportWidth, float preferredWidth)
    {
        float cap = MathF.Max(CompactRailW, viewportWidth - DrawerViewportInset);
        return MathF.Min(MathF.Max(DrawerMinW, preferredWidth), cap);
    }

    public static float DrawerRestingOpacity(bool open) => open ? 1f : 0f;
    public static float DrawerRestingTranslateX(bool open, float width) => open ? 0f : -width;
}
