using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// End-to-end paging-walk coverage for the browseSection "Show all" axis (style of ShowEpisodePagingTests): a fake
// IBrowseService serves a scripted sequence of pages and WalkAsync below drives them through the EXACT same
// arithmetic HomeSectionPage.LoadMoreAsync / BrowsePage.LoadMoreAsync use — HomeSectionPaging.BrowseSectionNextOffset
// for the cursor, HomeSectionPaging.CanAdvance + a dedup-progress check for termination — with no FluentGpu engine
// dependency (no Component, no Signal, no Element) anywhere in this file.
//
// THE regression this pins: Weekly Song Charts (totalCount 74) used to stop at 20 items ("El Salvador") because
// nothing ever asked for offset 20. Walking the real captured sequence (20/20/20/14) must land at all 74.
public class BrowseSectionPagingWalkTests
{
    sealed class FakeBrowseService : IBrowseService
    {
        readonly IReadOnlyDictionary<int, BrowseSection?> _pagesByOffset;
        public readonly List<int> RequestedOffsets = new();

        public FakeBrowseService(IReadOnlyDictionary<int, BrowseSection?> pagesByOffset) => _pagesByOffset = pagesByOffset;

        public Task<IReadOnlyList<BrowseCategory>> GetCategoriesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<BrowsePageModel?> GetPageAsync(string pageUri, int sectionOffset = 0, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<BrowseSection?> GetSectionAsync(string sectionUri, int offset, CancellationToken ct = default)
        {
            RequestedOffsets.Add(offset);
            return Task.FromResult(_pagesByOffset.TryGetValue(offset, out var page) ? page : null);
        }
    }

    static BrowseCard[] Cards(int start, int count) =>
        Enumerable.Range(start, count).Select(i => new BrowseCard("spotify:playlist:p" + i, "Track " + i, null, null)).ToArray();

    sealed record WalkResult(IReadOnlyList<BrowseCard> Cards, bool Exhausted);

    // Replicates HomeSectionPage.LoadInitialAsync + the LoadMoreAsync loop: fetch at the current offset, fold new
    // (by-uri, ordinal) cards in, resolve the next cursor via BrowseSectionNextOffset, and stop on any of the three
    // terminators production uses — no cursor, a cursor that cannot advance (CanAdvance), or a page that made no
    // progress (every item already seen).
    static async Task<WalkResult> WalkAsync(IBrowseService svc, string sectionUri)
    {
        var cards = new List<BrowseCard>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int offset = 0;
        while (true)
        {
            var page = await svc.GetSectionAsync(sectionUri, offset);
            if (page is null || page.Cards.Count == 0) return new WalkResult(cards, true);

            int before = cards.Count;
            foreach (var c in page.Cards) if (seen.Add(c.Uri)) cards.Add(c);
            bool progressed = cards.Count > before;

            int? next = HomeSectionPaging.BrowseSectionNextOffset(offset, page);
            bool advances = HomeSectionPaging.CanAdvance(offset, next) && progressed;
            if (!advances) return new WalkResult(cards, true);
            offset = next!.Value;
        }
    }

    // THE regression: the exact captured sequence (20/20/20/14, totalCount 74) must accumulate all 74 cards and walk
    // the cursor 0 → 20 → 40 → 60 before exhausting on the explicit terminator.
    [Fact]
    public async Task Walk_TheCapturedWeeklyChartsSequence_Accumulates74CardsAcrossFourPages()
    {
        var svc = new FakeBrowseService(new Dictionary<int, BrowseSection?>
        {
            [0] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(0, 20), [], 74, 20),
            [20] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(20, 20), [], 74, 40),
            [40] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(40, 20), [], 74, 60),
            [60] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(60, 14), [], 74, BrowseSection.PagingComplete),
        });

        var result = await WalkAsync(svc, "spotify:section:weekly");

        Assert.Equal(74, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 0, 20, 40, 60 }, svc.RequestedOffsets);
        // …and every card exactly once: a page boundary that double-counted would show up here as duplicates.
        Assert.Equal(74, result.Cards.Select(c => c.Uri).Distinct().Count());
    }

    // Pathological terminator #1: a "complete" answer of nextOffset: 0 must not be honoured as a cursor (0 is not
    // > the requested offset), even on the very first page — CanAdvance(0, 0) is false, so the walk stops after one
    // request instead of re-asking offset 0 forever.
    [Fact]
    public async Task Walk_NextOffsetZero_LatchesExhaustedInsteadOfLoopingOnPageOne()
    {
        var svc = new FakeBrowseService(new Dictionary<int, BrowseSection?>
        {
            [0] = new("spotify:section:s", "Section", BrowseSectionKind.Shelf, Cards(0, 6), [], 6, 0),
        });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(6, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Single(svc.RequestedOffsets);   // never re-requested offset 0
    }

    // Pathological terminator #2: the second page's cursor looks perfectly healthy (a forward-advancing value), but
    // every item it returned was already seen — the dedup-progress check must stop the walk anyway, or a server that
    // repeats a page forever would spin this loop forever too.
    [Fact]
    public async Task Walk_AnAllDuplicatePage_StaysLatched_EvenWithAHealthyLookingCursor()
    {
        var repeated = Cards(0, 2);
        var svc = new FakeBrowseService(new Dictionary<int, BrowseSection?>
        {
            [0] = new("spotify:section:s", "Section", BrowseSectionKind.Shelf, repeated, [], 40, 2),
            [2] = new("spotify:section:s", "Section", BrowseSectionKind.Shelf, repeated, [], 40, 4),   // same 2 URIs again
        });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(2, result.Cards.Count);        // the duplicates never landed a second time
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 0, 2 }, svc.RequestedOffsets);   // asked once more, then stopped — not forever
    }

    // An explicit PagingComplete terminator wins even when Total still claims more than what has landed — the same
    // guarantee HomeSectionPagingTests pins for BrowseSectionNextOffset directly, exercised here end to end through
    // the walk loop.
    [Fact]
    public async Task Walk_ExplicitTerminator_StopsEvenThoughTotalClaimsMore()
    {
        var svc = new FakeBrowseService(new Dictionary<int, BrowseSection?>
        {
            [0] = new("spotify:section:s", "Section", BrowseSectionKind.Shelf, Cards(0, 20), [], 74, BrowseSection.PagingComplete),
        });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(20, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Single(svc.RequestedOffsets);
    }
}
