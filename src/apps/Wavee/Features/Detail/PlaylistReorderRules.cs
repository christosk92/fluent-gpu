namespace Wavee;

/// <summary>When a same-playlist drag may reorder membership. Engine-free by design so the rule is pinned by
/// PlaylistReorderRulesTests against production code rather than a copy of it.</summary>
static class PlaylistReorderRules
{
    /// <summary>A same-list move addresses ORIGINAL membership rows through the DISPLAYED order, so it is only
    /// unambiguous while the displayed order IS the membership order. Sorting was already gated; a text query or an
    /// advanced filter is the same hazard — the display→original map then skips rows, so "insert before the row at
    /// display slot N" no longer names the membership position the user aimed at.
    /// <paramref name="naturalOrder"/> = the list is sorted by its own index, ascending.</summary>
    public static bool AllowsSameListMove(bool naturalOrder, string query, in TrackFilterState filters)
        => naturalOrder && query.Length == 0 && filters.IsDefault;

    /// <summary>Invert the display→original view map for ONE dragged row: the drag payload carries ORIGINAL membership
    /// indices, while the framework's virtual-removal math counts DISPLAY positions. A same-list move is only legal in
    /// natural order (see <see cref="AllowsSameListMove"/>), so the map is normally the identity and the O(1) probe
    /// answers; the scan is the defensive fallback for a view that is not. −1 = the row is not displayed.</summary>
    public static int DisplayRowOf(int originalIndex, ReadOnlySpan<int> view)
    {
        if ((uint)originalIndex < (uint)view.Length && view[originalIndex] == originalIndex) return originalIndex;
        for (int d = 0; d < view.Length; d++)
            if (view[d] == originalIndex) return d;
        return -1;
    }
}
