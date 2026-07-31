using System;
using Wavee.Core.Sidebar;

namespace Wavee;

// THE ONE ROW-GEOMETRY LADDER, in the engine-free layer so it is unit-testable.
//
// WHY IT MOVED HERE. `SidebarRowMetrics` (Shared/SidebarEntityRow.cs) used to own the height/indent ladder outright, but
// that file is engine-bound (`SidebarCover`, `Icon`, `BoxEl`) and is therefore NOT source-included by Wavee.Tests — so the
// single most load-bearing number in the sidebar ("Cozy+subtitle = 44 = Classic's row") could not be asserted anywhere.
// The arithmetic now lives here (System + Wavee.Core.Sidebar only) and `SidebarRowMetrics` DELEGATES, so there is still
// exactly one ladder and a test can now pin Classic's document and a Curated template to the same number.
//
// It also owns the two PURE geometry primitives the pane needs over a planned row list: the cumulative content-space Y of
// a row (the analytic offset a selection overlay / bring-into-view would position against) and the selection TRAVEL
// DIRECTION between two plan indices, which is what makes the row's selection indicator move *toward* the new selection
// instead of cross-fading in place.
static class SidebarRowGeometry
{
    /// <summary>The Classic entity-row height (44) — the number every landed sidebar row already uses.</summary>
    public const float ClassicHeight = 44f;

    /// <summary>Row height by density. Compact suppresses subtitles outright (no room for a second line), so the three
    /// canonical heights are 32 (compact) / 40 (cozy) / 44 (cozy with subtitle); Comfortable adds 4 DIP on top of the
    /// cozy pair (44 without a subtitle — Classic's shortcut row — and 48 with one).</summary>
    public static float HeightFor(SidebarDensity density, bool hasSubtitle) => density switch
    {
        SidebarDensity.Compact => 32f,
        SidebarDensity.Comfortable => hasSubtitle ? 48f : 44f,
        _ => hasSubtitle ? 44f : 40f,
    };

    /// <summary>A section's uniform row height straight from its persisted display options — the shape a document /
    /// template comparison needs (the renderer's <c>SidebarPaneMetrics.RowHeight</c> is this call).</summary>
    public static float HeightFor(SidebarDisplayOptions? opts)
    {
        var o = opts ?? SidebarDisplayOptions.Default;
        return HeightFor(o.Density, o.Subtitles);
    }

    /// <summary>Left padding for a nesting depth: 6 DIP base + 12 per level, clamped at 4 levels.</summary>
    public static float IndentFor(int depth) => 6f + (depth < 0 ? 0 : depth > 4 ? 4 : depth) * 12f;

    /// <summary>Subtitles are never rendered at Compact density.</summary>
    public static bool SubtitleVisible(SidebarDensity density, string? subtitle)
        => density != SidebarDensity.Compact && subtitle is { Length: > 0 };

    // ── pure plan geometry ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The CONTENT-SPACE top of plan row <paramref name="index"/>: the sum of every earlier row's extent. The
    /// pane's rows are contiguous inside one virtualized list (no inter-row spacing), so this prefix sum IS the row's Y —
    /// which is what lets a selection overlay be positioned analytically instead of measured (a measured position cannot
    /// survive recycling). Returns 0 for a negative index and clamps an index past the end to the total extent.
    /// <para><paramref name="extentOf"/> must report the row's MEASURED height including any rhythm padding the slot
    /// wraps around it, or the result drifts from the rendered layout row by row.</para></summary>
    public static float ContentYOf(int index, int count, Func<int, float> extentOf)
    {
        if (extentOf is null) throw new ArgumentNullException(nameof(extentOf));
        if (index <= 0) return 0f;
        int stop = index < count ? index : count;
        float y = 0f;
        for (int i = 0; i < stop; i++)
        {
            float e = extentOf(i);
            if (float.IsNaN(e) || e <= 0f) continue;   // a zero/degenerate row contributes nothing
            y += e;
        }
        return y;
    }

    /// <summary>The first plan index whose row resolves to <paramref name="route"/>, or -1. <paramref name="routeAt"/> is
    /// the caller's row→route projection (a projected entry's RouteKey, or a hand-placed Route item's key); null/empty
    /// means "this row is not a navigation target".</summary>
    public static int IndexOfRoute(int count, Func<int, string?> routeAt, string? route)
    {
        if (routeAt is null) throw new ArgumentNullException(nameof(routeAt));
        if (string.IsNullOrEmpty(route)) return -1;
        for (int i = 0; i < count; i++)
        {
            string? r = routeAt(i);
            if (r is { Length: > 0 } && string.Equals(r, route, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>Which way the selection TRAVELLED: +1 when the new row sits BELOW the old one, -1 when it sits above,
    /// 0 when the direction is unknowable (either row is off-plan, or it is the same row).
    /// <para>0 is a first-class answer, not a failure: a selection that arrives from off-plan (a deep link, a row inside a
    /// collapsed section, the first paint) has no travel direction, and the indicator must then simply fade in rather
    /// than slide from an invented side.</para></summary>
    public static int DirectionOf(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return 0;
        return toIndex > fromIndex ? 1 : -1;
    }
}
