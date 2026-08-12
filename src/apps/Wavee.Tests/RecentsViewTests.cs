using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Localization;
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

    static string Localize(string key) => key == Strings.Detail.Today ? "Today"
        : key == Strings.Detail.Yesterday ? "Yesterday" : key;

    static long Ms(DateTimeOffset now, int daysAgo, int hour = 12)
    {
        var local = now.AddDays(daysAgo).Date.AddHours(hour);
        return new DateTimeOffset(local, now.Offset).ToUnixTimeMilliseconds();
    }

    static void AssertRowDatesAlignWithHeaders(IReadOnlyList<RecentsRow> rows, RecentsSections sections,
                                               DateTimeOffset now)
    {
        foreach (var item in sections.Items)
        {
            if (item.Kind != RecentsFlatItemKind.Row) continue;
            Assert.Equal(
                RecentsView.DateOf(rows[item.OriginalRowIndex].PlayedAtMs, now.Offset),
                sections.HeaderDates[item.DayIndex]);
        }
    }

    /// <summary>The label BuildSections actually stamps for a header date (DateOnly midnight → DayBucketLabel).</summary>
    static string SectionLabel(DateOnly date, DateTimeOffset now)
        => date == DateOnly.MinValue ? ""
            : RecentsView.DayBucketLabel(date.ToDateTime(TimeOnly.MinValue), now, Inv, Localize);

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

    [Fact]
    public void DayBucketLabel_CoversTodayYesterdayWeekdayAndMonthDayAcrossAYearBoundary()
    {
        var now = new DateTimeOffset(2027, 1, 2, 18, 0, 0, TimeSpan.FromHours(2));
        string Localize(string key) => key == Strings.Detail.Today ? "Today"
            : key == Strings.Detail.Yesterday ? "Yesterday" : key;

        Assert.Equal("Today", RecentsView.DayBucketLabel(now.AddHours(-2), now, Inv, Localize));
        Assert.Equal("Yesterday", RecentsView.DayBucketLabel(now.AddDays(-1), now, Inv, Localize));
        Assert.Equal(Inv.DateTimeFormat.GetDayName(now.AddDays(-2).DayOfWeek),
            RecentsView.DayBucketLabel(now.AddDays(-2), now, Inv, Localize));
        Assert.Equal("Dec 25", RecentsView.DayBucketLabel(now.AddDays(-8), now, Inv, Localize));
    }

    [Fact]
    public void BuildSections_InsertsHeadersAfterFiltering_AndDropsAnEmptiedDayBucket()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:a", "music", playedAtMs: now.ToUnixTimeMilliseconds()),
            Group("b", "spotify:show:b", "podcasts", playedAtMs: now.AddDays(-1).ToUnixTimeMilliseconds()),
            Group("c", "spotify:album:c", "music", playedAtMs: now.AddDays(-2).ToUnixTimeMilliseconds()),
        ];

        var all = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, static k => k);
        Assert.Equal([0, 2, 4], all.HeaderIndices);
        Assert.Equal(6, all.Items.Length);

        var music = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "music"), now, Inv, static k => k);
        Assert.Equal([0, 2], music.HeaderIndices);
        Assert.Equal([1, -1, 3], music.RowToFlat);
        Assert.Equal([0, -1, 1], music.RowToDay);
        Assert.DoesNotContain(DateOnly.FromDateTime(now.AddDays(-1).Date), music.HeaderDates);
    }

    [Fact]
    public void BuildSections_EveryRowDateMatchesItsHeaderDate_AcrossTodayYesterdayWeekdayAndMonthDay()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("today-a", "spotify:playlist:a", playedAtMs: Ms(now, 0)),
            Group("today-b", "spotify:album:b", playedAtMs: Ms(now, 0, hour: 9)),
            Group("yesterday", "spotify:playlist:c", playedAtMs: Ms(now, -1)),
            Group("weekday", "spotify:album:d", playedAtMs: Ms(now, -3)),
            Group("may-a", "spotify:playlist:e", playedAtMs: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            Group("may-b", "spotify:album:f", playedAtMs: new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
        ];

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        AssertRowDatesAlignWithHeaders(rows, sections, now);
        Assert.Equal(
        [
            DateOnly.FromDateTime(now.Date),
            DateOnly.FromDateTime(now.AddDays(-1).Date),
            DateOnly.FromDateTime(now.AddDays(-3).Date),
            new DateOnly(2026, 5, 4),
        ], sections.HeaderDates);
    }

    [Fact]
    public void BuildSections_RowsBetweenTwoHeaders_AllCarryThePrecedingHeaderDayIndex()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("today-a", "spotify:playlist:a", playedAtMs: Ms(now, 0)),
            Group("today-b", "spotify:album:b", playedAtMs: Ms(now, 0, hour: 9)),
            Group("yesterday", "spotify:playlist:c", playedAtMs: Ms(now, -1)),
            Group("weekday", "spotify:album:d", playedAtMs: Ms(now, -3)),
            Group("may", "spotify:playlist:e", playedAtMs: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
        ];

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        for (int h = 0; h < sections.HeaderIndices.Length; h++)
        {
            int start = sections.HeaderIndices[h] + 1;
            int end = h + 1 < sections.HeaderIndices.Length ? sections.HeaderIndices[h + 1] : sections.Items.Length;
            for (int i = start; i < end; i++)
            {
                Assert.Equal(RecentsFlatItemKind.Row, sections.Items[i].Kind);
                Assert.Equal(h, sections.Items[i].DayIndex);
            }
        }
    }

    [Fact]
    public void BuildSections_DoesNotSort_NonContiguousSameDayRowsGetInterleavedHeaders()
    {
        // Product-level newest-first ordering lives on the wire / page. BuildSections walks the display map
        // as given and opens a new header whenever the calendar day changes — it must not silently sort.
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        long today = now.ToUnixTimeMilliseconds();
        long july = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        RecentsRow[] rows =
        [
            Group("today-a", "spotify:playlist:a", playedAtMs: today),
            Group("july", "spotify:album:b", playedAtMs: july),
            Group("today-b", "spotify:playlist:c", playedAtMs: today),
        ];

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        // Date buckets are Today / 6 Jul / Today — interleaved, not merged. Labels follow those
        // dates (via DateOnly midnight) and must not collapse the two Today runs.
        Assert.Equal(
        [
            DateOnly.FromDateTime(now.Date),
            new DateOnly(2026, 7, 6),
            DateOnly.FromDateTime(now.Date),
        ], sections.HeaderDates);
        Assert.Equal(3, sections.HeaderLabels.Length);
        Assert.Equal(sections.HeaderLabels[0], sections.HeaderLabels[2]);
        Assert.NotEqual(sections.HeaderLabels[0], sections.HeaderLabels[1]);
        Assert.Equal([0, 2, 4], sections.HeaderIndices);
        AssertRowDatesAlignWithHeaders(rows, sections, now);
    }

    [Fact]
    public void BuildSections_SameCountRegroup_ReplacesHeaderDatesAndLabels()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("today", "spotify:playlist:a", "music", playedAtMs: Ms(now, 0)),
            Group("yesterday", "spotify:show:b", "podcasts", playedAtMs: Ms(now, -1)),
            Group("may", "spotify:album:c", "music",
                playedAtMs: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            Group("july", "spotify:show:d", "podcasts",
                playedAtMs: new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
        ];

        var music = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "music"), now, Inv, Localize);
        var podcasts = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "podcasts"), now, Inv, Localize);

        Assert.Equal(2, RecentsView.Filter(rows, "music").Length);
        Assert.Equal(2, RecentsView.Filter(rows, "podcasts").Length);
        Assert.Equal(
        [
            DateOnly.FromDateTime(now.Date),
            new DateOnly(2026, 5, 4),
        ], music.HeaderDates);
        Assert.Equal(
        [
            DateOnly.FromDateTime(now.AddDays(-1).Date),
            new DateOnly(2026, 7, 6),
        ], podcasts.HeaderDates);
        Assert.NotEqual(music.HeaderDates, podcasts.HeaderDates);
        Assert.NotEqual(music.HeaderLabels, podcasts.HeaderLabels);
        Assert.Equal(SectionLabel(music.HeaderDates[0], now), music.HeaderLabels[0]);
        Assert.Equal(SectionLabel(music.HeaderDates[1], now), music.HeaderLabels[1]);
        Assert.Equal(SectionLabel(podcasts.HeaderDates[0], now), podcasts.HeaderLabels[0]);
        Assert.Equal(SectionLabel(podcasts.HeaderDates[1], now), podcasts.HeaderLabels[1]);
    }

    [Fact]
    public void BuildSections_PendingSeedShape_IsASingleTodayHeader_NeverJuly()
    {
        // Mirrors RecentsPage.CreatePendingSeed: 8 rows stamped minutes apart. Frozen clock so Today is Aug 12.
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = new RecentsRow[8];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new RecentsRow(
                RecentsRowKind.Group, "pending:" + i, "", null, null, null, null, 1,
                now.AddMinutes(-i).ToUnixTimeMilliseconds(), RecentsEntityKind.Unknown,
                RecentsReason.Played);

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        DateOnly today = DateOnly.FromDateTime(now.Date);
        Assert.Equal([today], sections.HeaderDates);
        Assert.All(sections.HeaderDates, d => Assert.True(d == DateOnly.MinValue || d == today));
        Assert.DoesNotContain(sections.HeaderDates, static d => d.Month == 7);
        Assert.DoesNotContain(sections.HeaderLabels, static l => l.Contains("Jul", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroTimestamp_IsMinValue_EmptyLabel_AndDoesNotBecomeToday()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(DateOnly.MinValue, RecentsView.DateOf(0, now.Offset));
        Assert.Equal("", RecentsView.DayBucketLabel(0L, now, Inv, Localize));

        RecentsRow[] rows =
        [
            Group("today", "spotify:playlist:a", playedAtMs: now.ToUnixTimeMilliseconds()),
            Group("unknown", "spotify:album:b", playedAtMs: 0),
        ];
        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal("", sections.HeaderLabels[1]);
        Assert.Equal([DateOnly.FromDateTime(now.Date), DateOnly.MinValue], sections.HeaderDates);
        Assert.NotEqual(DateOnly.FromDateTime(now.Date), RecentsView.DateOf(rows[1].PlayedAtMs, now.Offset));
        AssertRowDatesAlignWithHeaders(rows, sections, now);
    }

    [Fact]
    public void DayBucketLabel_FourTiersAndPreviousYear_FromTheAugustClock()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal("Today", RecentsView.DayBucketLabel(now.AddHours(-2), now, Inv, Localize));
        Assert.Equal("Yesterday", RecentsView.DayBucketLabel(now.AddDays(-1), now, Inv, Localize));
        Assert.Equal(Inv.DateTimeFormat.GetDayName(now.AddDays(-3).DayOfWeek),
            RecentsView.DayBucketLabel(now.AddDays(-3), now, Inv, Localize));
        Assert.Equal("May 04", RecentsView.DayBucketLabel(
            new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero), now, Inv, Localize));
        Assert.Equal("Dec 15", RecentsView.DayBucketLabel(
            new DateTimeOffset(2025, 12, 15, 12, 0, 0, TimeSpan.Zero), now, Inv, Localize));
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

    [Fact]
    public void CollectChildUris_UsesAvailableDecodedChildren_NotTheAuthoritativeChildCount()
    {
        var row = Group("a", null, childCount: 11,
            children: ["spotify:track:1", "", "spotify:track:1", "spotify:track:2"]);
        var into = new List<string>();
        Assert.Equal(2, RecentsView.CollectChildUris(row, static _ => true, into));
        Assert.Equal(["spotify:track:1", "spotify:track:2"], into);
    }

    [Fact]
    public void MetaFor_BranchesPlayedSavedAndUnknown_WithSavedAtLeastOne()
    {
        var played = Group("p", "spotify:playlist:p", childCount: 3);
        var saved = played with { ItemId = "s", Reason = RecentsReason.Saved, ChildCount = 0 };
        var unknown = played with { ItemId = "u", Reason = RecentsReason.Unknown };

        Assert.Equal(new RecentsMeta(RecentsMetaKind.PlayedCount, 3), RecentsView.MetaFor(played));
        Assert.Equal(new RecentsMeta(RecentsMetaKind.SavedCount, 1), RecentsView.MetaFor(saved));
        Assert.Equal(new RecentsMeta(RecentsMetaKind.PlayedAt, 0), RecentsView.MetaFor(unknown));
    }

    [Fact]
    public void DayDensity_CountsPlayedOnly_UsesLogLevels_AndKeepsFirstTopItemOnATie()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var first = Group("first", "spotify:playlist:first", childCount: 3,
            playedAtMs: now.ToUnixTimeMilliseconds());
        var tied = Group("tied", "spotify:playlist:tied", childCount: 3,
            playedAtMs: now.AddHours(-1).ToUnixTimeMilliseconds());
        var saved = Group("saved", "spotify:playlist:saved", childCount: 99,
            playedAtMs: now.AddHours(-2).ToUnixTimeMilliseconds()) with { Reason = RecentsReason.Saved };
        var single = new RecentsRow(RecentsRowKind.Single, "single", "spotify:track:1", null, null, null, null,
            0, now.AddDays(-1).ToUnixTimeMilliseconds(), RecentsEntityKind.Track, RecentsReason.Played, "music");
        RecentsRow[] rows = [first, tied, saved, single];

        var calendar = RecentsView.DayDensity(rows, RecentsView.Filter(rows, null), now, Inv);
        var month = Assert.Single(calendar.Months);
        var today = month.Days[now.Day - 1];
        var yesterday = month.Days[now.Day - 2];
        Assert.Equal(6, today.PlayCount);
        Assert.Equal(5, today.DensityLevel);
        Assert.Equal("first", today.TopItem?.ItemId);
        Assert.Equal(1, yesterday.PlayCount);
        Assert.InRange(yesterday.DensityLevel, 1, 4);
        Assert.Equal(7, month.TotalPlays);
        Assert.Equal(DateOnly.FromDateTime(now.Date), month.BusiestDay);
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
