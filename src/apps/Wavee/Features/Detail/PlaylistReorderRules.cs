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
}
