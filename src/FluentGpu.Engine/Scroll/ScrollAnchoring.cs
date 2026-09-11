namespace FluentGpu.Scroll;

/// <summary>
/// The scroll-anchoring suppression rule (CSS Scroll Anchoring, <c>overflow-anchor</c>) for an EXPLICIT anchor
/// correction — a list that knows content was inserted or removed ABOVE its first visible row and shifts its offset so
/// that row keeps its screen position (<c>ItemsViewController.PreserveAnchor</c>).
///
/// <para>A scroller resting at its START edge does not anchor. Gecko skips every anchor adjustment while the scroll
/// position is zero (<c>layout/generic/ScrollAnchorContainer.cpp</c>, <c>ApplyAdjustments</c>: "zeroScrollPos"), and
/// it equally ignores adjustments while a scroll restoration is still pending — the initial-load case, where the offset
/// is still 0 because the restore has not landed. The reason is the whole point: at offset 0 the user is looking at the
/// TOP of the content, so growth above the first row (a list whose preview rows were replaced by the loaded ones, a
/// banner arriving late) must push the rows down under a still header — not scroll the list to chase a row and throw
/// the header off the top (the 120 px daylist header jump of the 2026-09 visual-continuity audit).</para>
///
/// <para>The layout engine's own measured re-pin (<c>FlexLayout</c> ArrangeVirtualMeasured) already satisfies this by
/// construction: at offset 0 its anchor is item 0 with no sub-item offset, so it can never produce a shift. This class
/// is the same rule for the corrections a page computes itself. Pure and allocation-free.</para>
/// </summary>
public static class ScrollAnchoring
{
    /// <summary>How close to the start edge still counts as "at the start": half a DIP. The kernel clamps a resting
    /// scroller to exactly 0, so this only absorbs float noise, never a real user scroll.</summary>
    public const float StartEdgeEpsilon = 0.5f;

    /// <summary>True when an anchor correction of <paramref name="delta"/> should be applied to a scroller at
    /// <paramref name="scrollOffset"/> along its scroll axis; false at the start edge (see the type remarks) and for a
    /// zero delta.</summary>
    public static bool ShouldAdjust(float scrollOffset, float delta)
        => delta != 0f && float.IsFinite(delta) && scrollOffset > StartEdgeEpsilon;
}
