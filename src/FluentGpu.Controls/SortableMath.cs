namespace FluentGpu.Controls;

/// <summary>
/// THE insertion/reorder geometry — pure, static and allocation-free, so the framework (not the app) owns every
/// coordinate a sortable list needs. <see cref="ItemsView"/> drives it from its OWN geometry (viewport rect, live
/// scroll offset, the virtual layout's measured item bands, the persistent prefix); an app declares only intent
/// through <see cref="InsertionOptions"/> and never computes a slot, a gap or a preview position again.
///
/// <para>Three families, deliberately kept in ONE file so they cannot drift apart:</para>
/// <list type="number">
/// <item>SLOT — <see cref="SlotFromPointer"/> / <see cref="SlotFromOffset(float,float,float,int)"/> and the
/// variable-extent overloads. The trigger is the NN/g rule the audit accepted: the insertion point advances when the
/// pointer crosses an item's CENTRE (uniform form <c>floor((contentY + e/2) / e)</c>).</item>
/// <item>DISPLACEMENT + GAP — <see cref="InsertionPlan"/>. One value type answers BOTH "how far does row i move" and
/// "how tall is the opened gap", so the reflow and the preview drawn inside it can never disagree (the A4/A5 bug
/// class: a capped gap under an uncapped preview, a preview positioned in the wrong space).</item>
/// <item>PREVIEW/LINE POSITION — <see cref="PreviewOffset"/> / <see cref="PreviewY"/>, in CONTENT and VIEWPORT space
/// respectively, both derived from the same plan.</item>
/// </list>
///
/// <para><b>Virtual removal (design ruling (a)).</b> A SAME-LIST move hides its source rows — they are "in the chip",
/// WinUI move semantics — so every row below a hidden source shifts UP by one extent while the target gap opens by
/// exactly <c>N·extent</c>:</para>
/// <code>dy(i) = extent · ( N·[i ≥ slot] − removedBefore(i) )</code>
/// <para>Σremoval = N, so the content height is invariant: the gap is EXACT, never double-counted, and the set of
/// dragged indices may be NON-CONTIGUOUS (a ctrl-click multi-selection) without any special case. A CROSS-LIST copy
/// has no removal and caps the gap at <c>min(N, previewCap)·extent</c> — an exact-N gap for a 500-track copy would
/// blow the viewport — with the preview reading that SAME capped extent.</para>
///
/// <para><b>Insertion index correction is NOT the caller's job.</b> A same-list move that removes rows above the
/// insertion point shifts the destination index by <c>removedBefore(slot)</c>; the backend's move convention already
/// applies that correction (pinned by <c>MoveRowsConventionTests</c>, Wave 1B), so <see cref="InsertionOptions.OnDeposit"/>
/// receives the RAW display slot the user aimed at. Correcting it again here would move rows twice.</para>
///
/// <para><b>Measured extents.</b> The uniform overloads take one extent; the span overloads take content-space item
/// STARTS (prefix sums, <c>starts.Length ≥ count+1</c> with <c>starts[count]</c> = the content end) so a list whose
/// rows differ in height — an expanded versions drawer, a hero + chrome prefix — resolves the slot exactly. The
/// LEADING extent is likewise never estimated: it is <c>starts[0]</c>, i.e. the measured content offset of the first
/// insertable item.</para>
/// </summary>
public static class SortableMath
{
    /// <summary>Preview rows rendered in the gap (and the cross-list gap cap) — the researched "≤3 cards + a +N
    /// spacer" shape. <see cref="InsertionOptions.PreviewCap"/> overrides it per list.</summary>
    public const int DefaultPreviewCap = 3;

    // ── slot ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Insertion slot (0..<paramref name="count"/>) for a pointer in VIEWPORT space.
    /// <paramref name="pointerInViewport"/> is the pointer's main-axis coordinate MINUS the viewport's own origin;
    /// <paramref name="scrollOffset"/> converts it to content space; <paramref name="leadingExtent"/> is the measured
    /// content offset of the FIRST insertable item (a persistent hero/chrome prefix, a section header). An empty list
    /// resolves to slot 0 — a configured destination must accept an append, never silently discard the drop.</summary>
    public static int SlotFromPointer(float pointerInViewport, float scrollOffset, float leadingExtent,
                                      float itemExtent, int count)
        => SlotFromOffset(pointerInViewport + scrollOffset, leadingExtent, itemExtent, count);

    /// <summary>Uniform-extent slot for a CONTENT-space offset. Centre-crossing trigger (NN/g).</summary>
    public static int SlotFromOffset(float contentOffset, float leadingExtent, float itemExtent, int count)
    {
        if (count <= 0 || !(itemExtent > 0f) || !float.IsFinite(contentOffset)) return 0;
        float local = contentOffset - leadingExtent;
        int slot = (int)MathF.Floor((local + itemExtent * 0.5f) / itemExtent);
        return Math.Clamp(slot, 0, count);
    }

    /// <summary>Variable-extent slot for a CONTENT-space offset over item STARTS (prefix sums; length ≥
    /// <paramref name="count"/>+1, <c>starts[count]</c> = content end). O(log n), allocation-free. Reduces to the
    /// uniform overload exactly when the starts are an arithmetic sequence.</summary>
    public static int SlotFromOffset(float contentOffset, ReadOnlySpan<float> starts, int count)
    {
        if (count <= 0 || starts.Length < count + 1 || !float.IsFinite(contentOffset)) return 0;
        if (contentOffset <= starts[0]) return 0;
        if (contentOffset >= starts[count]) return count;
        int lo = 0, hi = count - 1;
        while (lo < hi)                                  // largest i with starts[i] <= contentOffset
        {
            int mid = (lo + hi + 1) >> 1;
            if (starts[mid] <= contentOffset) lo = mid; else hi = mid - 1;
        }
        float centre = (starts[lo] + starts[lo + 1]) * 0.5f;
        return Math.Clamp(contentOffset >= centre ? lo + 1 : lo, 0, count);
    }

    /// <summary>The single-band form of the variable-extent slot: the host has already resolved (via its measured
    /// virtual layout's O(log n) index-at-offset) that <paramref name="contentOffset"/> falls in item
    /// <paramref name="index"/>'s band <c>[bandStart, bandStart+bandExtent)</c>. Same centre-crossing trigger, no
    /// prefix-sum array required — the shape a virtualized list with 10k measured rows can afford per pointer move.</summary>
    public static int SlotFromBand(float contentOffset, int index, float bandStart, float bandExtent, int count)
    {
        if (count <= 0) return 0;
        if (!(bandExtent > 0f)) return Math.Clamp(index, 0, count);
        int slot = contentOffset >= bandStart + bandExtent * 0.5f ? index + 1 : index;
        return Math.Clamp(slot, 0, count);
    }

    // ── plan (displacement + gap, from one source of truth) ─────────────────────────────────────────

    /// <summary>Build the reflow plan for a live insertion. <paramref name="firstItem"/>/<paramref name="count"/> bound
    /// the INSERTABLE sub-range of the host's item model (a persistent hero/chrome prefix leads it; appended section
    /// rows — a "Recommended" header and its rows — trail it and must never ride the gap down).
    /// <paramref name="slot"/> is 0..<paramref name="count"/> RELATIVE to <paramref name="firstItem"/>;
    /// <paramref name="itemExtent"/> is the REPRESENTATIVE row extent at the insertion point (a measured host passes
    /// the measured extent of the row at the slot); <paramref name="sameList"/> selects virtual-removal (move) vs
    /// capped-gap (copy) semantics.</summary>
    public static InsertionPlan Plan(int firstItem, int count, int slot, int draggedCount, float itemExtent,
                                     bool sameList, int previewCap = DefaultPreviewCap)
    {
        count = Math.Max(0, count);
        return new(Math.Max(0, firstItem), count,
                   slot < 0 ? -1 : Math.Clamp(slot, 0, count), Math.Max(0, draggedCount),
                   float.IsFinite(itemExtent) ? MathF.Max(0f, itemExtent) : 0f,
                   sameList, Math.Max(1, previewCap));
    }

    // ── preview / line position ─────────────────────────────────────────────────────────────────────

    /// <summary>CONTENT-space main-axis offset of the opened gap's leading edge — where the insertion line sits and
    /// where the in-gap preview starts. <paramref name="removedAboveSlot"/> is the number of virtually-removed source
    /// rows above the slot (0 for a cross-list copy).</summary>
    public static float PreviewOffset(int slot, float leadingExtent, float itemExtent, int removedAboveSlot)
        => leadingExtent + (slot - removedAboveSlot) * itemExtent;

    // (The viewport-space form is <see cref="InsertionPlan.PreviewY"/> — it is only ever wanted with a plan in hand,
    //  so there is deliberately no free-function twin here to drift from it.)

    // ── shared helpers over a SORTED, de-duplicated source-index set ─────────────────────────────────

    /// <summary>Count of dragged source indices strictly below <paramref name="index"/> (binary search over the
    /// sorted set; allocation-free).</summary>
    public static int RemovedBefore(int index, ReadOnlySpan<int> sortedSources)
    {
        int lo = 0, hi = sortedSources.Length;           // lower bound of `index`
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (sortedSources[mid] < index) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>True when <paramref name="index"/> is one of the dragged rows (binary search).</summary>
    public static bool IsSource(int index, ReadOnlySpan<int> sortedSources)
    {
        int at = RemovedBefore(index, sortedSources);
        return at < sortedSources.Length && sortedSources[at] == index;
    }

    /// <summary>Sort + de-duplicate + drop out-of-range indices IN PLACE, returning the retained length. The one
    /// normalization every displacement/removal query assumes; run it once per gesture, never per move.</summary>
    public static int Normalize(Span<int> indices, int count)
    {
        int n = 0;
        for (int i = 0; i < indices.Length; i++)
            if ((uint)indices[i] < (uint)count) indices[n++] = indices[i];
        if (n == 0) return 0;
        var live = indices[..n];
        live.Sort();
        int unique = 1;
        for (int i = 1; i < n; i++)
            if (live[i] != live[unique - 1]) live[unique++] = live[i];
        return unique;
    }
}

/// <summary>
/// The reflow plan for ONE live insertion — the single value that answers per-row displacement AND gap extent, so a
/// gap can never be a different size from the preview drawn in it. Produced by <see cref="SortableMath.Plan"/>.
/// A POD record struct: copyable, comparable, and free to hold across a frame.
/// </summary>
/// <param name="FirstItem">First ITEM index of the insertable range (the host's persistent prefix count).</param>
/// <param name="Count">Insertable item count — rows at or past <c>FirstItem+Count</c> (appended sections) never move.</param>
/// <param name="Slot">Insertion slot RELATIVE to <see cref="FirstItem"/>, 0..<see cref="Count"/> (−1 = inactive).</param>
/// <param name="DraggedCount">N — how many rows the payload carries.</param>
/// <param name="ItemExtent">The representative (measured, at the slot) row extent.</param>
/// <param name="SameList">True ⇒ move semantics: sources hide and the gap is EXACTLY N·extent.</param>
/// <param name="PreviewCap">Preview rows, and the cross-list gap cap.</param>
public readonly record struct InsertionPlan(int FirstItem, int Count, int Slot, int DraggedCount,
                                            float ItemExtent, bool SameList, int PreviewCap)
{
    /// <summary>A gap is open (a slot was resolved against a list with a usable extent and a non-empty payload).
    /// An EMPTY list stays active at slot 0: a configured destination must accept an append, not discard the drop.</summary>
    public bool IsActive => Slot >= 0 && Slot <= Count && ItemExtent > 0f && DraggedCount > 0;

    /// <summary>Rows the gap spans: EXACTLY N for a same-list move (virtual removal balances it), capped for a copy.</summary>
    public int GapRows => !IsActive ? 0 : SameList ? DraggedCount : Math.Min(DraggedCount, PreviewCap);

    /// <summary>Main-axis extent of the opened gap. The preview MUST read this value, not recompute one.</summary>
    public float GapExtent => GapRows * ItemExtent;

    /// <summary>Preview cards rendered in the gap (the last one carries the "+N−cap" pill).</summary>
    public int PreviewRows => !IsActive ? 0 : Math.Min(DraggedCount, PreviewCap);

    /// <summary>The absolute ITEM index the gap opens before.</summary>
    public int SlotItem => FirstItem + Slot;

    /// <summary>Rows hidden above the slot by virtual removal (0 for a cross-list copy).</summary>
    public int RemovedAboveSlot(ReadOnlySpan<int> sortedSources)
        => !IsActive || !SameList ? 0 : SortableMath.RemovedBefore(SlotItem, sortedSources);

    /// <summary>THE displacement of resting ITEM <paramref name="item"/> (absolute index — sources are absolute too):
    /// <c>extent·(N·[i ≥ slot] − removedBefore(i))</c> for a move, <c>gapExtent·[i ≥ slot]</c> for a copy.
    /// <para>A LEADING item (a sticky hero/chrome prefix, <c>item &lt; FirstItem</c>) is never displaced — C1.</para>
    /// <para>A TRAILING item (an appended section — a "Recommended songs" header and its cards — at or past
    /// <c>FirstItem+Count</c>) rides the range's NET growth, i.e. exactly what the insertable range's own tail moved
    /// by. For a same-list move that is <b>0</b> (Σremoval == N, the content height is invariant) which is A12 as
    /// written; for a CROSS-LIST copy the content genuinely grows by <see cref="GapExtent"/>, so the section moves
    /// down with it. Returning a flat 0 there opened the gap UNDERNEATH the section: the in-gap preview — drawn at
    /// the gap's leading edge, which for a bottom slot is the section's own top — painted straight over its
    /// header.</para></summary>
    public float DisplacementFor(int item, ReadOnlySpan<int> sortedSources)
    {
        if (!IsActive || item < FirstItem) return 0f;
        // Clamp (rather than special-case) the trailing rows onto the range's exclusive end: the displacement of the
        // hypothetical row at FirstItem+Count IS the net growth, so the section can never disagree with the last
        // insertable row it sits under.
        if (item > FirstItem + Count) item = FirstItem + Count;
        float dy = item >= SlotItem ? GapExtent : 0f;
        if (SameList) dy -= SortableMath.RemovedBefore(item, sortedSources) * ItemExtent;
        return dy;
    }

    /// <summary>True when ITEM <paramref name="item"/> is virtually removed for this gesture (a same-list source —
    /// it is "in the chip" and must render hidden, not merely dimmed).</summary>
    public bool IsHiddenSource(int item, ReadOnlySpan<int> sortedSources)
        => IsActive && SameList && item >= FirstItem && item < FirstItem + Count
           && SortableMath.IsSource(item, sortedSources);

    /// <summary>CONTENT-space leading edge of the gap. <paramref name="leadingExtent"/> is the MEASURED content
    /// offset of item <see cref="FirstItem"/> — never an app-side estimate.</summary>
    public float PreviewOffset(float leadingExtent, ReadOnlySpan<int> sortedSources)
        => SortableMath.PreviewOffset(Slot, leadingExtent, ItemExtent, RemovedAboveSlot(sortedSources));

    /// <summary>VIEWPORT-space leading edge of the gap (the line/preview transform).</summary>
    public float PreviewY(float leadingExtent, float scrollOffset, ReadOnlySpan<int> sortedSources)
        => PreviewOffset(leadingExtent, sortedSources) - scrollOffset;
}
