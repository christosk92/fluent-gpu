using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The Recents page's PURE half (<c>Features/Recents/RecentsView.cs</c>) — the decisions a reviewer wants
/// pinned: which chips exist, what a chip filters, which URIs a realized window owes the network, which rows may claim
/// the shared-element tag, and how a play instant reads. All engine-free, so these drive the REAL production rules
/// rather than a copy of them.</summary>
public class RecentsViewTests
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static RecentsRow Group(string id, string? uri, string? contentType = "music", int childCount = 1,
                            long playedAtMs = 1_700_000_000_000, IReadOnlyList<string>? children = null)
        => new(RecentsRowKind.Group, id, uri ?? "", uri, null, null, null, childCount, playedAtMs,
            uri is null ? RecentsEntityKind.Unknown : RecentsList.EntityKindOf(uri),
            RecentsReason.Played, contentType, children);

    // ── chips ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContentTypes_AreDerivedFromTheRows_InWireOrder_WithoutDuplicates()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:show:2", "podcasts"),
            Group("c", "spotify:album:3"),
            Group("d", "spotify:show:4", "PODCASTS"),   // the set is case-insensitive; first spelling wins
        ];
        Assert.Equal(["music", "podcasts"], RecentsView.ContentTypes(rows));
    }

    [Fact]
    public void ContentTypes_OfAMusicOnlyList_OffersNoPodcastChipToFilterToNothing()
    {
        RecentsRow[] rows = [Group("a", "spotify:playlist:1"), Group("b", "spotify:album:2")];
        Assert.Equal(["music"], RecentsView.ContentTypes(rows));
    }

    [Fact]
    public void ContentTypes_IgnoresRowsCarryingNone_AndIsEmptyForAnUntypedList()
    {
        RecentsRow[] rows = [Group("a", "spotify:playlist:1", contentType: null), Group("b", "spotify:album:2", "")];
        Assert.Empty(RecentsView.ContentTypes(rows));
    }

    // ── the filter predicate ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllMatchesEverything_IncludingRowsTheServerGaveNoContentType()
    {
        Assert.True(RecentsView.Matches(Group("a", "spotify:playlist:1", contentType: null), null));
        Assert.True(RecentsView.Matches(Group("b", "spotify:album:2"), null));
    }

    [Fact]
    public void ASelectedChipMatchesOnlyItsOwnToken_AndNeverAnUntypedRow()
    {
        var music = Group("a", "spotify:playlist:1");
        var podcast = Group("b", "spotify:show:2", "podcasts");
        var untyped = Group("c", "spotify:album:3", contentType: null);
        Assert.True(RecentsView.Matches(music, "music"));
        Assert.False(RecentsView.Matches(podcast, "music"));
        Assert.False(RecentsView.Matches(untyped, "music"));
    }

    [Fact]
    public void Filter_ProducesADisplayMapIntoTheUnfilteredRows_InWireOrder()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:show:2", "podcasts"),
            Group("c", "spotify:album:3"),
        ];
        Assert.Equal([0, 1, 2], RecentsView.Filter(rows, null));
        Assert.Equal([0, 2], RecentsView.Filter(rows, "music"));
        Assert.Equal([1], RecentsView.Filter(rows, "podcasts"));
    }

    // ── hydration targets ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HydrationUri_PrefersTheContextUri_ThenTheFirstChild_ThenTheRowsOwnUri()
    {
        Assert.Equal("spotify:playlist:1", RecentsView.HydrationUri(Group("a", "spotify:playlist:1")));

        // A single-context group header carries no uri of its own — those rows render from group_metadata's children.
        var headless = new RecentsRow(RecentsRowKind.Group, "b", "", null, null, null, null, 3, 1,
            RecentsEntityKind.Track, RecentsReason.Played, "music", ["", "spotify:track:9", "spotify:track:8"]);
        Assert.Equal("spotify:track:9", RecentsView.HydrationUri(headless));

        var single = new RecentsRow(RecentsRowKind.Single, "c", "spotify:track:7", null, null, null, null, 0, 1,
            RecentsEntityKind.Track);
        Assert.Equal("spotify:track:7", RecentsView.HydrationUri(single));

        var nothing = new RecentsRow(RecentsRowKind.Group, "d", "", null, null, null, null, 0, 1,
            RecentsEntityKind.Unknown);
        Assert.Null(RecentsView.HydrationUri(nothing));
    }

    [Fact]
    public void CollectRange_TakesOnlyTheRealizedWindow_WithAnExclusiveEnd()
    {
        var rows = new RecentsRow[6];
        for (int i = 0; i < rows.Length; i++) rows[i] = Group("id" + i, "spotify:playlist:" + i);
        int[] map = RecentsView.Filter(rows, null);

        var into = new List<string>();
        RecentsView.CollectRange(rows, map, 1, 4, static _ => true, into);
        Assert.Equal(["spotify:playlist:1", "spotify:playlist:2", "spotify:playlist:3"], into);
    }

    [Fact]
    public void CollectRange_RequestsMissesOnly_AndCollapsesTheRepeatedUrisARecentsListIsFullOf()
    {
        // ~1,388 uris repeat across a real recents list: play one playlist on three days and it heads three groups.
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:playlist:1"),
            Group("c", "spotify:album:2"),
            Group("d", "spotify:playlist:3"),
        ];
        int[] map = RecentsView.Filter(rows, null);
        var have = new HashSet<string> { "spotify:album:2" };

        var into = new List<string>();
        int added = RecentsView.CollectRange(rows, map, 0, 4, u => !have.Contains(u), into);
        Assert.Equal(2, added);
        Assert.Equal(["spotify:playlist:1", "spotify:playlist:3"], into);
    }

    [Fact]
    public void CollectRange_ClampsAStaleRange_AndHonoursTheCap()
    {
        RecentsRow[] rows = [Group("a", "spotify:playlist:1"), Group("b", "spotify:playlist:2")];
        int[] map = RecentsView.Filter(rows, null);

        var into = new List<string>();
        // A range from a list that has since shrunk must clamp, never throw.
        Assert.Equal(2, RecentsView.CollectRange(rows, map, -5, 900, static _ => true, into));

        into.Clear();
        Assert.Equal(1, RecentsView.CollectRange(rows, map, 0, 2, static _ => true, into, cap: 1));
        Assert.Equal(["spotify:playlist:1"], into);
    }

    [Fact]
    public void CollectRange_FollowsTheFilterMap_NotTheRawRowOrder()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:show:2", "podcasts"),
            Group("c", "spotify:album:3"),
        ];
        int[] podcasts = RecentsView.Filter(rows, "podcasts");
        var into = new List<string>();
        RecentsView.CollectRange(rows, podcasts, 0, podcasts.Length, static _ => true, into);
        Assert.Equal(["spotify:show:2"], into);
    }

    // ── shared-element eligibility ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstOccurrence_TagsOnlyTheMostRecentRowOfEachUri_SoNoTwoLiveNodesShareAMorphId()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:album:2"),
            Group("c", "spotify:playlist:1"),   // same entity, played again later — must NOT be tagged
            Group("d", null, children: null),   // names nothing → never tagged
        ];
        Assert.Equal([true, true, false, false], RecentsView.FirstOccurrence(rows));
    }

    // ── played-at formatting (culture tables only — the page authors no copy) ─────────────────────────────────────────

    [Fact]
    public void PlayedAt_TodayIsATime_ThisWeekIsAWeekday_ThisYearIsAnAbbreviatedMonthDay_OlderIsAShortDate()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);

        Assert.Equal(now.AddHours(-3).ToString("t", Inv),
            RecentsView.PlayedAt(now.AddHours(-3), now, Inv));

        Assert.Equal(Inv.DateTimeFormat.GetAbbreviatedDayName(now.AddDays(-3).DayOfWeek),
            RecentsView.PlayedAt(now.AddDays(-3), now, Inv));

        // Invariant MonthDayPattern is "MMMM dd"; the abbreviated cut keeps the culture's ORDER and narrows the month.
        Assert.Equal("Jun 12", RecentsView.PlayedAt(new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero), now, Inv));

        var older = new DateTimeOffset(2024, 3, 2, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(older.ToString("d", Inv), RecentsView.PlayedAt(older, now, Inv));
    }

    [Fact]
    public void PlayedAt_OfAnUnknownTimestamp_IsEmpty_NotAnEpochDate()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);
        Assert.Equal("", RecentsView.PlayedAt(0L, now, Inv));
        Assert.Equal("", RecentsView.PlayedAt(-1L, now, Inv));
    }

    [Fact]
    public void Summary_StatesTheCount_AndTheWindowThePlaysSpan()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);
        long Ms(int month, int day) => new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1", playedAtMs: Ms(8, 1)),
            Group("b", "spotify:album:2", playedAtMs: Ms(6, 3)),
        ];
        Assert.Equal("2 · Jun 03 – Aug 01", RecentsView.Summary(rows, now, Inv));
    }

    [Fact]
    public void Summary_OfAnEmptyList_IsEmpty_AndATimestampLessListStillStatesItsCount()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);
        Assert.Equal("", RecentsView.Summary(Array.Empty<RecentsRow>(), now, Inv));
        Assert.Equal("1", RecentsView.Summary([Group("a", "spotify:playlist:1", playedAtMs: 0)], now, Inv));
    }

    [Fact]
    public void ChipLabel_IsTheWireTokenItself_SoAContentTypeAddedTomorrowStaysRenderable()
    {
        Assert.Equal("Music", RecentsView.ChipLabel("music", Inv));
        Assert.Equal("Audiobooks", RecentsView.ChipLabel("audiobooks", Inv));
        Assert.Equal("", RecentsView.ChipLabel("", Inv));
    }
}
