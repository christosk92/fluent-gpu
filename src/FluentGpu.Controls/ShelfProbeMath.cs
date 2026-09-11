using System;

namespace FluentGpu.Controls;

// The two PURE decisions behind PagedShelf's card-mount budget, in their own file (System-only, no engine types) for
// the same reason SortableMath / SplitterMath / ToastCoalescing are: a pure decision belongs somewhere a headless test
// can compile it directly, not inside the engine-bound control that consumes it.

/// <summary>The measured shelf's PROGRESSIVE PROBE budget — pure arithmetic over ints, so the mount-budget decision is
/// unit-testable headlessly, exactly as <c>LazyGridMath</c> is for the lazy grid. (Source-included by Wavee.Tests, so
/// nothing here may reference an engine type — not even in a cref.)
///
/// <para>A <c>measured: true</c> shelf learns its strip height by mounting a bounded sample of REAL cards at the fitted
/// width and measuring the tallest (see <c>PagedShelf.Create(measured:)</c>). Those sample cards are the same full card
/// subtrees the live strip renders — cover, overlay, shimmer, tooltip, context menu — so mounting the WHOLE sample in
/// one frame is a mount avalanche: eight measured shelves appearing in one publication (the artist page's extras) put
/// ~190 card subtrees into a single reconcile. The sample is therefore taken in <see cref="Chunk"/>-sized PASSES: the
/// cells are KEYED by index, so a pass that widens the prefix REUSES every already-realized cell and only realizes the
/// new ones, and each pass is armed on a later host tick so the work lands in a later frame.</para>
///
/// <para>The ANSWER is unchanged: the completed progression's locked height is the max over the same <c>min(count,
/// SampleCap)</c> cells one eager pass would have measured. Only the FRAMES it is spread over differ. A shelf that has
/// never measured publishes the running max as soon as the FIRST chunk knows a height, so its strip appears a frame
/// after mount rather than after the whole sample (uniform cards — the common measured shelf — make that value the
/// final one, so nothing moves when the later chunks land). A RE-probe publishes nothing until it completes: its
/// contract is that the last good strip stays visible and its lock is replaced only by a complete measurement.</para>
/// </summary>
public static class ShelfProbeMath
{
    /// <summary>The most cells a measured shelf will ever sample. Measuring a bounded sample is what keeps an unbounded
    /// catalog from being realized; app measured shelves cap their items below this, and an unbounded caller should
    /// supply <c>cardHeight</c> instead of <c>measured: true</c>.</summary>
    public const int SampleCap = 24;

    /// <summary>Cells added per pass — the per-frame mount budget for ONE shelf. Small enough that every measured shelf
    /// on a page can take a pass in the same frame and stay inside an 8.3 ms budget; large enough that a shelf of a
    /// handful of cards (the common measured shelf) completes in one or two passes.</summary>
    public const int Chunk = 4;

    /// <summary>The full sample size for a collection of <paramref name="count"/> items.</summary>
    public static int Target(int count) => count <= 0 ? 0 : Math.Min(count, SampleCap);

    /// <summary>The prefix length the FIRST pass of a fresh progression emits.</summary>
    public static int FirstSample(int target) => target <= 0 ? 0 : Math.Min(target, Chunk);

    /// <summary>The prefix length the NEXT pass emits, given how many cells the current progression has already
    /// emitted. Monotonic and saturating at <paramref name="target"/> — a continuation can never shrink the prefix
    /// (which would unmount a cell mid-progression and lose its measurement).</summary>
    public static int NextSample(int mounted, int target)
        => target <= 0 ? 0
         : mounted <= 0 ? FirstSample(target)
         : Math.Min(target, mounted + Chunk);

    /// <summary>Has the progression covered the whole sample (so the running max IS the final locked height)?</summary>
    public static bool IsComplete(int mounted, int target) => target <= 0 || mounted >= target;

    /// <summary>How many passes a collection of <paramref name="count"/> items takes end to end.</summary>
    public static int Passes(int count)
    {
        int target = Target(count);
        return target <= 0 ? 0 : (target + Chunk - 1) / Chunk;
    }
}

/// <summary>Whether a shelf sitting inside a page <c>ScrollView</c> is close enough to the scroll window to be worth
/// MOUNTING its cards — pure arithmetic, testable headlessly, and the whole of the viewport gate's decision.
///
/// <para>This is RENDERING virtualization, not a fetch window: the shelf's model is complete and its strip height is
/// already locked (a measured shelf's probe, or the caller's <c>cardHeight</c>), so a gated-off shelf reserves EXACTLY
/// the same box it will occupy when it mounts — nothing above it moves, and the page's scroll extent is identical
/// either way. Only the cards inside the box wait. The gate LATCHES: once a shelf has mounted its strip it keeps it,
/// so scrolling past a shelf and back never unmounts and remounts a card.</para></summary>
public static class ShelfViewportBand
{
    /// <summary>The floor on the lookahead band, for a viewport too small to be a useful unit (a docked/compact
    /// window).</summary>
    public const float MinLookahead = 240f;

    /// <summary>The band grown on EACH side of the scroll window: one full viewport, so a shelf mounts its cards a
    /// whole screen before it can be seen and the user never scrolls into an empty band.</summary>
    public static float Lookahead(float viewportH)
        => viewportH > 1f && float.IsFinite(viewportH) ? MathF.Max(MinLookahead, viewportH) : MinLookahead;

    /// <summary>Does the shelf box <c>[topInContent, topInContent + height]</c> (CONTENT space) intersect the scroll
    /// window grown by <paramref name="lookahead"/> on both sides? Unknown/degenerate geometry answers TRUE — a gate
    /// that cannot see where it is must never withhold content.</summary>
    public static bool Intersects(float topInContent, float height, float scrollOffset, float viewportH, float lookahead)
    {
        if (!float.IsFinite(viewportH) || viewportH <= 1f) return true;
        if (!float.IsFinite(topInContent) || !float.IsFinite(scrollOffset)) return true;
        float band = float.IsFinite(lookahead) && lookahead > 0f ? lookahead : 0f;
        float h = float.IsFinite(height) && height > 0f ? height : 0f;
        return topInContent + h >= scrollOffset - band
            && topInContent <= scrollOffset + viewportH + band;
    }

    /// <summary>Whether the live strip may mount. First-frame <c>contentH</c> often still equals the viewport because
    /// below-fold sections have not reserved their boxes yet — treating that as "the page does not scroll" latches
    /// every shelf in one flush (the artist-page 9-shelf / 38-card first-content frame). A page that truly fits still
    /// latches every shelf, because each reserved box then sits inside the viewport band.</summary>
    public static bool ShouldLatch(float topInContent, float height, float scrollOffset, float viewportH)
        => Intersects(topInContent, height, scrollOffset, viewportH, Lookahead(viewportH));
}
