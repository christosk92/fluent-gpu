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
// a row (the analytic offset drop placement / bring-into-view uses) and the selection TRAVEL
// DIRECTION between two plan indices, which is what makes the row's selection indicator move *toward* the new selection
// instead of cross-fading in place.
static class SidebarRowGeometry
{
    /// <summary>The Classic entity-row height (44) — the number every landed sidebar row already uses.</summary>
    public const float ClassicHeight = 44f;

    // ── THE ONE CONTENT LANE ─────────────────────────────────────────────────────────────────────────────────────────
    // The pane had a RAGGED LEFT EDGE because two families of surface computed their own inset from raw literals: the
    // virtualized ROWS landed at PanePad.Left + IndentFor(0) = 14, while every FIXED CHROME BAND mounted above the list
    // (Library V3's header / toolbar / chip rail / rule / breadcrumb) padded to a bare 8 and landed 6 DIP short of them.
    // Naming the lane once — here, in the engine-free layer, where a test can reach it — is what stops the next band
    // from inventing a third number: a band expresses its padding as ContentLane/ContentLaneEnd, never as a literal.

    /// <summary>The pane's horizontal edge inset (8) — <c>SidebarPaneMetrics.PanePad</c>'s left/right. It is applied
    /// ONCE, around the virtualized list, so a band mounted ABOVE that list sits outside it and must reproduce it as
    /// part of <see cref="ContentLane"/> rather than padding to this number on its own.</summary>
    public const float PaneEdge = 8f;

    /// <summary>A row's OWN leading padding at depth 0 (6) — the base term of <see cref="IndentFor"/>.</summary>
    public const float RowInsetLeft = 6f;

    /// <summary>A row's OWN trailing padding (8) — the right-hand half of <c>SidebarPaneMetrics.RowInset</c>.</summary>
    public const float RowInsetRight = 8f;

    /// <summary>THE CONTENT LANE (14): the single x at which pane content begins — a row's selection gutter, a section
    /// header's title, a chrome band's first glyph, a divider's hairline. Rows reach it as
    /// <see cref="PaneEdge"/> + <see cref="IndentFor"/>(0); a fixed band above the list pads to it directly.</summary>
    public const float ContentLane = PaneEdge + RowInsetLeft;

    /// <summary>The lane's TRAILING twin (16): <see cref="PaneEdge"/> + <see cref="RowInsetRight"/>. It is 2 DIP wider
    /// than <see cref="ContentLane"/> because the landed row padding is asymmetric (6 leading / 8 trailing) — carried
    /// forward as-is, not re-derived, so nothing shifts horizontally while the lane is being named.</summary>
    public const float ContentLaneEnd = PaneEdge + RowInsetRight;

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

    /// <summary>Left padding for a nesting depth: <see cref="RowInsetLeft"/> base + 12 per level, clamped at 4 levels.</summary>
    public static float IndentFor(int depth) => RowInsetLeft + (depth < 0 ? 0 : depth > 4 ? 4 : depth) * 12f;

    /// <summary>Subtitles are never rendered at Compact density.</summary>
    public static bool SubtitleVisible(SidebarDensity density, string? subtitle)
        => density != SidebarDensity.Compact && subtitle is { Length: > 0 };

    // ── pure plan geometry ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The CONTENT-SPACE top of plan row <paramref name="index"/>: the sum of every earlier row's extent. The
    /// pane's rows are contiguous inside one virtualized list (no inter-row spacing), so this prefix sum IS the row's Y —
    /// which is what lets drop placement and navigation geometry survive recycling. Returns 0 for a negative index and
    /// clamps an index past the end to the total extent.
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

    /// <summary>Resolve the contiguous BODY owned by one planned section header. A different section at the same or a
    /// shallower depth is a structural sibling and terminates the band even when that sibling is a divider or has no
    /// header of its own. Deeper rows belong to a nested CustomGroup subtree and stay inside the parent disclosure.</summary>
    public static bool TrySectionBodyRange(IReadOnlyList<SidebarRow> rows, string sectionId,
                                           out int firstIndex, out int count)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrEmpty(sectionId))
        {
            firstIndex = count = 0;
            return false;
        }

        int header = -1;
        byte depth = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind != SidebarRowKind.SectionHeader
                || !string.Equals(row.SectionId, sectionId, StringComparison.Ordinal)) continue;
            header = i;
            depth = row.Depth;
            break;
        }

        if (header < 0)
        {
            firstIndex = count = 0;
            return false;
        }

        int end = header + 1;
        while (end < rows.Count)
        {
            var row = rows[end];
            if (row.Depth <= depth && !string.Equals(row.SectionId, sectionId, StringComparison.Ordinal)) break;
            end++;
        }

        firstIndex = header + 1;
        count = end - firstIndex;
        return count > 0;
    }
}
