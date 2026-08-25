using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The Liked Songs rail facts (<c>Features/Detail/LikedFactsRules.cs</c>, source-included because it is
/// engine-free). Every function here takes its clock as a parameter, which is the only reason the week bucketing, the
/// DST and year boundaries, and the future-stamp clamp can be pinned at all — a rule that read
/// <c>DateTimeOffset.UtcNow</c> internally would be testable only on the day it happened to be written.
///
/// <para>The other standing rule is honesty about missing data: a track with no usable <c>AddedAt</c> is EXCLUDED from
/// every fact rather than counted at a guessed date, and a statistic without enough evidence behind it is not returned
/// at all so the caller mounts no card.</para></summary>
public class LikedFactsRulesTests
{
    static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    static Track T(DateTimeOffset? addedAt, string id = "t", IReadOnlyList<ArtistRef>? artists = null,
                   IReadOnlyList<string>? tags = null)
        => new(id, "spotify:track:" + id, "Title " + id,
            artists ?? Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
            180_000, false, null, AddedAt: addedAt, Tags: tags);

    static ArtistRef A(string name) => new(name, "spotify:artist:" + name, name);

    static IReadOnlyList<Track> Repeat(int n, Func<int, Track> make)
    {
        var list = new List<Track>(n);
        for (int i = 0; i < n; i++) list.Add(make(i));
        return list;
    }

    /// <summary>The bucket (in the returned oldest-first order) that a single like lands in, or -1 when it is excluded
    /// altogether. Asserts that exactly one bucket claims it — a like counted twice would inflate the sparkline.</summary>
    static int SoleBucket(DateTimeOffset? addedAt, DateTimeOffset now, int weeks = 12)
    {
        var buckets = LikedFactsRules.LikesPerWeek([T(addedAt)], now, weeks);
        Assert.Equal(weeks, buckets.Count);

        int found = -1, total = 0;
        for (int i = 0; i < buckets.Count; i++)
        {
            total += buckets[i].Count;
            if (buckets[i].Count > 0) found = i;
        }
        Assert.InRange(total, 0, 1);
        return found;
    }

    // ── LikesPerWeek: the rolling sparkline ─────────────────────────────────────────────────────────────────────────

    /// <summary>Always exactly <c>weeks</c> buckets, oldest first, each window start exactly seven days after the
    /// last. An empty week is a zero-height bar, never a missing one — a sparkline that omitted quiet weeks would
    /// compress time and read as a busier library than it is.</summary>
    [Fact]
    public void TheWindowIsTwelveContiguousSevenDayBucketsOldestFirst()
    {
        var buckets = LikedFactsRules.LikesPerWeek(Array.Empty<Track>(), Now);

        Assert.Equal(12, buckets.Count);
        Assert.Equal(Now - TimeSpan.FromDays(84), buckets[0].WindowStart);
        Assert.Equal(Now - TimeSpan.FromDays(7), buckets[11].WindowStart);
        for (int i = 1; i < buckets.Count; i++)
        {
            Assert.Equal(TimeSpan.FromDays(7), buckets[i].WindowStart - buckets[i - 1].WindowStart);
            Assert.Equal(0, buckets[i].Count);
        }
    }

    /// <summary>Bucket k is the HALF-OPEN window <c>(now - 7(k+1)d, now - 7k d]</c>: a like exactly seven days old
    /// belongs to LAST week, not this one. "This week" is the last bucket by construction, whatever weekday it is.</summary>
    [Theory]
    [InlineData(0.0, 11)]        // right now
    [InlineData(0.5, 11)]
    [InlineData(6.99, 11)]
    [InlineData(7.0, 10)]        // the rung: exactly one week old is last week's bucket
    [InlineData(13.99, 10)]
    [InlineData(14.0, 9)]
    [InlineData(83.99, 0)]       // the oldest bucket still on the chart
    [InlineData(84.0, -1)]       // one tick past the window: off the chart entirely, not squashed into the last bar
    [InlineData(400.0, -1)]
    public void LikesLandInTheirRollingWeek(double daysAgo, int expectedBucket)
        => Assert.Equal(expectedBucket, SoleBucket(Now - TimeSpan.FromDays(daysAgo), Now));

    /// <summary>The rung is exact to the tick, not to the day.</summary>
    [Fact]
    public void TheWeekRungIsExact()
    {
        var oneWeek = TimeSpan.FromDays(7);
        Assert.Equal(11, SoleBucket(Now - oneWeek + TimeSpan.FromTicks(1), Now));
        Assert.Equal(10, SoleBucket(Now - oneWeek, Now));
    }

    /// <summary>A stamp from the future — clock skew on whichever device wrote it — is clamped to now and counted in
    /// THIS week rather than silently falling off the right edge of the chart (E12).</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(45.0)]
    [InlineData(4000.0)]
    public void FutureStampsClampIntoTheNewestBucket(double daysAhead)
        => Assert.Equal(11, SoleBucket(Now + TimeSpan.FromDays(daysAhead), Now));

    /// <summary>The windows are absolute instants, so an hour appearing or disappearing from the wall clock inside the
    /// span changes nothing: the bars stay exactly seven days wide across a DST transition.</summary>
    [Fact]
    public void DaylightSavingDoesNotMoveTheBuckets()
    {
        // Europe ends summer time on 2025-10-26; this window straddles it.
        var now = new DateTimeOffset(2025, 11, 2, 12, 0, 0, TimeSpan.FromHours(1));
        var buckets = LikedFactsRules.LikesPerWeek(
        [
            T(now - TimeSpan.FromDays(3)),     // this week
            T(now - TimeSpan.FromDays(9)),     // last week — across the transition
            T(now - TimeSpan.FromDays(7)),     // exactly a week: last week too
        ], now);

        for (int i = 1; i < buckets.Count; i++)
            Assert.Equal(TimeSpan.FromDays(7), buckets[i].WindowStart - buckets[i - 1].WindowStart);
        Assert.Equal(1, buckets[11].Count);
        Assert.Equal(2, buckets[10].Count);
    }

    /// <summary>And a year boundary inside the window is a non-event, because these are not calendar weeks.</summary>
    [Fact]
    public void AYearBoundaryDoesNotMoveTheBuckets()
    {
        var now = new DateTimeOffset(2026, 1, 3, 9, 30, 0, TimeSpan.Zero);
        var buckets = LikedFactsRules.LikesPerWeek(
        [
            T(new DateTimeOffset(2025, 12, 30, 9, 30, 0, TimeSpan.Zero)),   // 4 days back
            T(new DateTimeOffset(2025, 12, 20, 9, 30, 0, TimeSpan.Zero)),   // 14 days back
        ], now);

        Assert.Equal(1, buckets[11].Count);
        Assert.Equal(1, buckets[9].Count);
        Assert.Equal(0, buckets[10].Count);
    }

    [Fact]
    public void AskingForNoWeeksAsksForNothing()
    {
        Assert.Empty(LikedFactsRules.LikesPerWeek([T(Now)], Now, 0));
        Assert.Empty(LikedFactsRules.LikesPerWeek([T(Now)], Now, -4));
        Assert.Equal(4, LikedFactsRules.LikesPerWeek([T(Now)], Now, 4).Count);
    }

    // ── E12: the AddedAt anomaly table ──────────────────────────────────────────────────────────────────────────────

    public static TheoryData<DateTimeOffset?> UnusableStamps() => new()
    {
        (DateTimeOffset?)null,                 // never stamped — a curated/editorial row, or a thin write
        DateTimeOffset.UnixEpoch,              // a zero timestamp deserialised into a real type
        default(DateTimeOffset),               // an unpopulated struct (== DateTimeOffset.MinValue)
        DateTimeOffset.UnixEpoch - TimeSpan.FromDays(1),
        DateTimeOffset.UnixEpoch - TimeSpan.FromDays(3650),
    };

    /// <summary>A like we cannot date is excluded from EVERY fact — not defaulted to today, not to 1970. Half of these
    /// would otherwise put the since-line in the Nixon administration.</summary>
    [Theory]
    [MemberData(nameof(UnusableStamps))]
    public void AnUndatableLikeIsExcludedFromEveryFact(DateTimeOffset? addedAt)
    {
        IReadOnlyList<Track> tracks = [T(addedAt, "x", [A("Aphex")], ["Ambient"])];

        Assert.Equal(-1, SoleBucket(addedAt, Now));
        Assert.Empty(LikedFactsRules.LikedInWindow(tracks, Now - TimeSpan.FromDays(4000), Now + TimeSpan.FromDays(1)));
        Assert.Empty(LikedFactsRules.TopArtists(tracks));
        Assert.Empty(LikedFactsRules.BlendShares(tracks));
        Assert.Null(LikedFactsRules.LikingSince(tracks));
        Assert.Null(LikedFactsRules.OldestLike(tracks));
        Assert.Null(LikedFactsRules.DominantDecade(tracks));
        Assert.False(LikedFactsRules.TryStamp(tracks[0], out _));
    }

    /// <summary>The epoch floor is a floor, not a year filter: a like genuinely saved the day after the epoch is still
    /// a like.</summary>
    [Fact]
    public void OnlyTheEpochSentinelItselfIsRejected()
    {
        var justAfter = DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(1);
        Assert.True(LikedFactsRules.TryStamp(T(justAfter), out var at));
        Assert.Equal(justAfter, at);
    }

    /// <summary>An undatable like does not poison the ones around it — the facts are computed from whatever IS
    /// datable.</summary>
    [Fact]
    public void UndatableLikesDoNotSuppressTheDatableOnes()
    {
        IReadOnlyList<Track> tracks =
        [
            T(null, "a", [A("Aphex")], ["Ambient"]),
            T(Now - TimeSpan.FromDays(2), "b", [A("Boards")], ["Ambient"]),
            T(DateTimeOffset.UnixEpoch, "c", [A("Clark")], ["Ambient"]),
        ];

        Assert.Equal("b", LikedFactsRules.OldestLike(tracks)!.Id);
        var top = LikedFactsRules.TopArtists(tracks);
        Assert.Single(top);
        Assert.Equal("Boards", top[0].Artist.Name);
    }

    // ── This week, last year ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whole weeks back, not a calendar year: the window covers the same seven weekdays the user is living
    /// through now, which is what makes the fact read as "this week, last year".</summary>
    [Theory]
    [InlineData("2026-03-15T12:00:00Z")]
    [InlineData("2026-01-01T00:00:00Z")]
    [InlineData("2024-02-29T18:45:00Z")]   // a leap day, so the arithmetic cannot be a naive year subtraction
    public void TheLastYearWindowIsSevenSameWeekdayDays(string nowIso)
    {
        var now = DateTimeOffset.Parse(nowIso, System.Globalization.CultureInfo.InvariantCulture);
        var (start, end) = LikedFactsRules.ThisWeekLastYearWindow(now);

        Assert.Equal(now - TimeSpan.FromDays(371), start);
        Assert.Equal(now - TimeSpan.FromDays(364), end);
        Assert.Equal(TimeSpan.FromDays(7), end - start);
        Assert.Equal(now.DayOfWeek, start.DayOfWeek);
        Assert.Equal(now.DayOfWeek, end.DayOfWeek);
    }

    /// <summary>Half-open <c>[start, end)</c>, so the window's own end instant belongs to the NEXT window and a like
    /// can never be counted by two adjacent windows.</summary>
    [Fact]
    public void TheWindowIsHalfOpenAndKeepsInputOrder()
    {
        var (start, end) = LikedFactsRules.ThisWeekLastYearWindow(Now);
        IReadOnlyList<Track> tracks =
        [
            T(start - TimeSpan.FromTicks(1), "before"),
            T(start, "atStart"),
            T(start + TimeSpan.FromDays(3), "inside"),
            T(end - TimeSpan.FromTicks(1), "justInside"),
            T(end, "atEnd"),
        ];

        var hits = LikedFactsRules.LikedInWindow(tracks, start, end);
        Assert.Equal(["atStart", "inside", "justInside"], Ids(hits));
    }

    [Fact]
    public void AnEmptyOrInvertedWindowSelectsNothing()
    {
        IReadOnlyList<Track> tracks = [T(Now)];
        Assert.Empty(LikedFactsRules.LikedInWindow(tracks, Now, Now));
        Assert.Empty(LikedFactsRules.LikedInWindow(tracks, Now, Now - TimeSpan.FromDays(1)));
        Assert.Empty(LikedFactsRules.LikedInWindow(Array.Empty<Track>(), Now - TimeSpan.FromDays(1), Now));
    }

    static string[] Ids(IReadOnlyList<Track> tracks)
    {
        var ids = new string[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) ids[i] = tracks[i].Id;
        return ids;
    }

    // ── Most liked artists ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every credited artist counts, not just the billed one: a feature credit is a real reason the track is
    /// in the library, and collapsing to the primary would hide exactly the collaborations this fact exists to
    /// surface.</summary>
    [Fact]
    public void EveryCreditCountsNotJustTheFirst()
    {
        IReadOnlyList<Track> tracks =
        [
            T(Now, "1", [A("Solo"), A("Guest")]),
            T(Now, "2", [A("Solo")]),
            T(Now, "3", [A("Guest")]),
            T(Now, "4", [A("Guest")]),
        ];

        var top = LikedFactsRules.TopArtists(tracks);
        Assert.Equal(["Guest", "Solo"], Names(top));
        Assert.Equal(3, top[0].Count);
        Assert.Equal(2, top[1].Count);
    }

    /// <summary>A tie is broken by name, so a refresh that changes nothing cannot reorder the face pile.</summary>
    [Fact]
    public void TiesAreBrokenByNameSoThePileNeverTwitches()
    {
        IReadOnlyList<Track> tracks = [T(Now, "1", [A("Zeta"), A("Alpha"), A("Mid")])];
        Assert.Equal(["Alpha", "Mid", "Zeta"], Names(LikedFactsRules.TopArtists(tracks)));
    }

    [Fact]
    public void TopArtistsIsCappedAndSurvivesEmptyInput()
    {
        var tracks = Repeat(20, i => T(Now, "t" + i, [A("A" + i.ToString("00"))]));
        Assert.Equal(5, LikedFactsRules.TopArtists(tracks).Count);
        Assert.Equal(3, LikedFactsRules.TopArtists(tracks, 3).Count);
        Assert.Empty(LikedFactsRules.TopArtists(tracks, 0));
        Assert.Empty(LikedFactsRules.TopArtists(Array.Empty<Track>()));
        Assert.Empty(LikedFactsRules.TopArtists([T(Now, "x")]));
    }

    static string[] Names(IReadOnlyList<LikedFactsRules.ArtistCount> counts)
    {
        var names = new string[counts.Count];
        for (int i = 0; i < counts.Count; i++) names[i] = counts[i].Artist.Name;
        return names;
    }

    // ── Your blend ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Only the PRIMARY descriptor counts, so the slices partition the tagged likes and can be stacked in one
    /// bar. Counting every tag would count a three-descriptor track three times and push the bar past 100%.</summary>
    [Fact]
    public void OnlyThePrimaryTagContributesSoTheBarPartitions()
    {
        var tracks = new List<Track>();
        tracks.AddRange(Repeat(5, i => T(Now, "p" + i, tags: ["Pop", "Chill"])));
        tracks.AddRange(Repeat(4, i => T(Now, "c" + i, tags: ["Chill"])));
        tracks.AddRange(Repeat(3, i => T(Now, "j" + i, tags: ["Jazz"])));

        var shares = LikedFactsRules.BlendShares(tracks);
        Assert.Equal(["Pop", "Chill", "Jazz"], Titles(shares));
        Assert.Equal([5, 4, 3], Counts(shares));

        // 12 tagged likes, all of them represented: the slices sum to exactly one bar.
        Assert.InRange(Sum(shares), 0.9999f, 1.0001f);
        Assert.InRange(shares[0].Fraction, 5f / 12f - 0.0001f, 5f / 12f + 0.0001f);
    }

    /// <summary>Capping the legend leaves a remainder, which is the caller's "Other" — the slices must therefore never
    /// sum past 1.0 whatever the take.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(50)]
    public void SlicesNeverSumPastOneBar(int take)
    {
        var tracks = new List<Track>();
        for (int t = 0; t < 8; t++)
            tracks.AddRange(Repeat(3 + t, i => T(Now, $"t{t}_{i}", tags: ["tag" + t])));
        tracks.AddRange(Repeat(4, i => T(Now, "untagged" + i)));

        var shares = LikedFactsRules.BlendShares(tracks, take);
        Assert.Equal(Math.Min(take, 8), shares.Count);
        Assert.InRange(Sum(shares), 0f, 1.0001f);
        foreach (var s in shares) Assert.InRange(s.Fraction, 0f, 1.0001f);
    }

    /// <summary>E14: below the evidence floor there is no card at all. "60% Ambient" derived from two tracks is a made
    /// up statistic, and the floor is the SAME one the content-filter chips use so the two cannot disagree.</summary>
    [Fact]
    public void BelowTheEvidenceFloorThereIsNoBlend()
    {
        Assert.Equal(3, ContentFilterTags.MinTrackCount);

        var justUnder = Repeat(ContentFilterTags.MinTrackCount - 1, i => T(Now, "t" + i, tags: ["Ambient"]));
        Assert.Empty(LikedFactsRules.BlendShares(justUnder));

        var atTheFloor = Repeat(ContentFilterTags.MinTrackCount, i => T(Now, "t" + i, tags: ["Ambient"]));
        var shares = LikedFactsRules.BlendShares(atTheFloor);
        Assert.Single(shares);
        Assert.Equal("Ambient", shares[0].Title);
        Assert.InRange(shares[0].Fraction, 0.9999f, 1.0001f);
    }

    /// <summary>Null Tags means "descriptor enrichment has not landed", empty means "this track genuinely has none".
    /// Neither is a blend, and neither may be presented as one.</summary>
    [Fact]
    public void UnenrichedAndUntaggedLikesAreNotABlend()
    {
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "t" + i))));
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "t" + i, tags: Array.Empty<string>()))));
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "t" + i, tags: ["  "]))));
        Assert.Empty(LikedFactsRules.BlendShares(Array.Empty<Track>()));
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "t" + i, tags: ["Pop"])), 0));
    }

    /// <summary>Casing variants are one concept, exactly as they are for the chips — a descriptor arriving without a
    /// display name comes through as its lowercase wire token.</summary>
    [Fact]
    public void CasingVariantsCollapseToOneSlice()
    {
        IReadOnlyList<Track> tracks =
        [
            T(Now, "a", tags: ["K-Pop"]), T(Now, "b", tags: ["k-pop"]), T(Now, "c", tags: ["K-POP"]),
        ];

        var shares = LikedFactsRules.BlendShares(tracks);
        Assert.Single(shares);
        Assert.Equal(3, shares[0].Count);
    }

    // ── Your blend: what "Other" pools ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Eight descriptors above the floor (3…10 carriers), two below it, four untagged likes. 52 + 3 = 55
    /// tagged likes; the untagged four are outside the partition entirely, exactly as the bar draws it.</summary>
    static IReadOnlyList<Track> BlendLibrary()
    {
        var tracks = new List<Track>();
        for (int t = 0; t < 8; t++)
            tracks.AddRange(Repeat(3 + t, i => T(Now, $"t{t}_{i}", tags: ["tag" + t])));
        tracks.AddRange(Repeat(2, i => T(Now, "rare" + i, tags: ["Rare"])));      // below ContentFilterTags.MinTrackCount
        tracks.Add(T(Now, "rarest", tags: ["Rarest"]));                            // …and so is this one
        tracks.AddRange(Repeat(4, i => T(Now, "untagged" + i)));
        return tracks;
    }

    /// <summary>The tail is a strict CONTINUATION of the bar, not a second statistic: same ranking, same denominator.
    /// A tooltip that re-derived its percentages over the pooled likes alone would name shares that visibly contradict
    /// the bar the pointer is resting on.</summary>
    [Fact]
    public void TheOtherTailContinuesTheSameRankingAndDenominator()
    {
        var tracks = BlendLibrary();
        var bar = LikedFactsRules.BlendShares(tracks, 5);
        var tail = LikedFactsRules.BlendOther(tracks, 5, 3);

        // The bar names the five biggest; the tail picks up at the sixth.
        Assert.Equal(["tag7", "tag6", "tag5", "tag4", "tag3"], Titles(bar));
        Assert.Equal(["tag2", "tag1", "tag0"], Titles(tail.Named));
        Assert.Equal([5, 4, 3], Counts(tail.Named));

        // 55 tagged likes, 40 of them named by the bar → 15 pooled, and every fraction is over the same 55.
        Assert.Equal(15, tail.Count);
        Assert.InRange(tail.Fraction, 15f / 55f - 0.0001f, 15f / 55f + 0.0001f);
        Assert.InRange(tail.Named[0].Fraction, 5f / 55f - 0.0001f, 5f / 55f + 0.0001f);
        Assert.InRange(bar[0].Fraction, 10f / 55f - 0.0001f, 10f / 55f + 0.0001f);

        // …and the two together are the whole bar.
        Assert.InRange(Sum(bar) + tail.Fraction, 0.9999f, 1.0001f);
    }

    /// <summary>"and N more" counts the descriptors nothing named — INCLUDING the ones below the evidence floor. They
    /// are exactly what the remainder pools, so omitting them from the count would understate the tail while the bar
    /// keeps drawing their likes.</summary>
    [Theory]
    [InlineData(5, 3, 3, 2)]    // bar names 5, a 3-deep tail names 3 → 2 left, and both are below-floor
    [InlineData(5, 0, 0, 5)]    // no detail at all → the 3 ranked leftovers plus the 2 below-floor
    [InlineData(0, 2, 2, 8)]    // nothing named by the bar → the whole partition is the tail
    [InlineData(1, 3, 3, 6)]    // the tail always picks up where the bar stopped, whatever the bar named
    public void TheMoreCountCoversEveryDescriptorNothingNames(int shown, int detail, int named, int more)
    {
        var tail = LikedFactsRules.BlendOther(BlendLibrary(), shown, detail);
        Assert.Equal(named, tail.Named.Count);
        Assert.Equal(more, tail.MoreTags);
    }

    /// <summary>An UNBOUNDED tail enumerates the remainder completely — every descriptor named, nothing left over —
    /// which is what lets the card draw one tick per descriptor with no pooled slab behind them.
    ///
    /// <para>Below-evidence-floor descriptors are named HERE and nowhere else. The floor exists to stop the bar
    /// INFERRING ("60 % Ambient" off three tracks); listing "Rarest · 1 song" inside a region explicitly labelled "the
    /// other N" infers nothing — it is an enumeration with an exact count. <see cref="LikedFactsRules.BlendShares"/> is
    /// the surface that makes claims, and it still refuses to name them (the row below re-asserts that).</para></summary>
    [Fact]
    public void AnUnboundedTailNamesEveryRemainingDescriptor()
    {
        var tracks = BlendLibrary();
        var bar = LikedFactsRules.BlendShares(tracks, 5);
        var tail = LikedFactsRules.BlendOther(tracks, bar.Count, int.MaxValue);

        // Five named by the bar, ten distinct descriptors in all → five in the tail, and NOTHING unnamed after them.
        Assert.Equal(["tag2", "tag1", "tag0", "Rare", "Rarest"], Titles(tail.Named));
        Assert.Equal([5, 4, 3, 2, 1], Counts(tail.Named));
        Assert.Equal(0, tail.MoreTags);

        // The tail's own counts add up to the pooled count exactly — that identity is what makes a tick strip drawn
        // from these widths a true partition of the remainder rather than an approximation of it.
        int summed = 0;
        for (int i = 0; i < tail.Named.Count; i++) summed += tail.Named[i].Count;
        Assert.Equal(tail.Count, summed);

        // …and the evidence floor still governs the BAR: "Rare" and "Rarest" can never become slices of it.
        Assert.DoesNotContain("Rare", Titles(LikedFactsRules.BlendShares(tracks, 50)));
        Assert.DoesNotContain("Rarest", Titles(LikedFactsRules.BlendShares(tracks, 50)));
        Assert.Equal(8, LikedFactsRules.BlendShares(tracks, 50).Count);
    }

    /// <summary>The tail legend's cut: at or above the floor is a ROW, below it is a NUMBER. Exactly on the floor is
    /// named — a descriptor sitting on 1 % is at 1 %, not under it — and the rows keep the tail's own rank order.</summary>
    [Fact]
    public void TailSplitNamesAtOrAboveTheFloorAndCountsTheRest()
    {
        IReadOnlyList<LikedFactsRules.TagShare> tail =
        [
            new("EDM", 9, 0.07f), new("R&B", 8, 0.02f), new("Chill", 3, 0.01f),   // 0.01 is ON the floor → named
            new("Trap", 2, 0.009f), new("Ska", 1, 0.004f),
        ];

        var (named, under) = LikedFactsRules.TailSplit(tail);
        Assert.Equal(["EDM", "R&B", "Chill"], Titles(named));
        Assert.Equal(2, under);

        // The floor is a parameter, not a constant: raising it moves rows into the count, never loses them.
        var (fewer, moreUnder) = LikedFactsRules.TailSplit(tail, 0.05f);
        Assert.Equal(["EDM"], Titles(fewer));
        Assert.Equal(4, moreUnder);

        // Everything above the floor ⇒ the input list comes straight back (no copy, no reordering).
        var (all, none) = LikedFactsRules.TailSplit(tail, 0f);
        Assert.Same(tail, all);
        Assert.Equal(0, none);
    }

    /// <summary>No tail, no split — and never a throw: an empty tail is the normal state of a bar that named
    /// everything, and a tail entirely under the floor is a legend of one caption.</summary>
    [Fact]
    public void TailSplitOfNothingIsNothing()
    {
        Assert.Empty(LikedFactsRules.TailSplit(Array.Empty<LikedFactsRules.TagShare>()).Named);
        Assert.Equal(0, LikedFactsRules.TailSplit(Array.Empty<LikedFactsRules.TagShare>()).UnderFloor);
        Assert.Empty(LikedFactsRules.TailSplit(null!).Named);

        IReadOnlyList<LikedFactsRules.TagShare> tiny = [new("Ska", 1, 0.004f), new("Emo", 1, 0.004f)];
        var (named, under) = LikedFactsRules.TailSplit(tiny);
        Assert.Empty(named);
        Assert.Equal(2, under);
    }

    /// <summary>Nothing pooled means no answer: when the named slices already cover every tagged like there is no
    /// "Other" segment for the tooltip to open up, and a zero-count bubble would be worse than none.</summary>
    [Fact]
    public void AFullyNamedBarHasNoTail()
    {
        var tracks = new List<Track>();
        tracks.AddRange(Repeat(6, i => T(Now, "a" + i, tags: ["Pop"])));
        tracks.AddRange(Repeat(4, i => T(Now, "b" + i, tags: ["Jazz"])));
        tracks.AddRange(Repeat(3, i => T(Now, "c" + i)));            // untagged — outside the partition, not the tail

        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(tracks, 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(Array.Empty<Track>(), 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(null!, 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(Repeat(20, i => T(Now, "t" + i)), 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(tracks, -1, 3));
    }

    /// <summary>The tagged population recovered from any one slice. The share is a float, so the division is
    /// reconstructive: it must ROUND, not truncate — 5/(5/12f) lands a hair under 12 in single precision and a cast
    /// would print the card's header as "11 songs" over a bar drawn from twelve.</summary>
    [Theory]
    [InlineData(12)]
    [InlineData(55)]
    [InlineData(3)]
    [InlineData(9999)]
    public void TheTaggedTotalIsRecoveredFromAnySlice(int tagged)
    {
        var tracks = new List<Track>();
        int rest = tagged;
        for (int t = 0; t < 3 && rest > 4; t++, rest -= 4)
            tracks.AddRange(Repeat(4, i => T(Now, $"t{t}_{i}", tags: ["tag" + t])));
        int carry = rest;
        tracks.AddRange(Repeat(carry, i => T(Now, "z" + i, tags: ["ZZ"])));

        var shares = LikedFactsRules.BlendShares(tracks, 50);
        Assert.Equal(tagged, LikedFactsRules.TaggedTotal(shares));
    }

    /// <summary>No slices, no population — and never a throw: the card is not mounted in that case, but the helper is
    /// public and must answer rather than fault.</summary>
    [Fact]
    public void TheTaggedTotalOfNothingIsZero()
    {
        Assert.Equal(0, LikedFactsRules.TaggedTotal(Array.Empty<LikedFactsRules.TagShare>()));
        Assert.Equal(0, LikedFactsRules.TaggedTotal(null!));
        Assert.Equal(0, LikedFactsRules.TaggedTotal([new LikedFactsRules.TagShare("Pop", 4, 0f)]));
    }

    static string[] Titles(IReadOnlyList<LikedFactsRules.TagShare> shares)
    {
        var titles = new string[shares.Count];
        for (int i = 0; i < shares.Count; i++) titles[i] = shares[i].Title;
        return titles;
    }

    static int[] Counts(IReadOnlyList<LikedFactsRules.TagShare> shares)
    {
        var counts = new int[shares.Count];
        for (int i = 0; i < shares.Count; i++) counts[i] = shares[i].Count;
        return counts;
    }

    static float Sum(IReadOnlyList<LikedFactsRules.TagShare> shares)
    {
        float sum = 0f;
        for (int i = 0; i < shares.Count; i++) sum += shares[i].Fraction;
        return sum;
    }

    // ── The since-line ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSinceLineIsTheOldestDatableLike()
    {
        var oldest = new DateTimeOffset(2019, 4, 2, 8, 0, 0, TimeSpan.Zero);
        IReadOnlyList<Track> tracks =
        [
            T(Now, "new"),
            T(DateTimeOffset.UnixEpoch, "bogus"),
            T(oldest, "oldest"),
            T(null, "unstamped"),
        ];

        Assert.Equal(oldest, LikedFactsRules.LikingSince(tracks));
        Assert.Equal("oldest", LikedFactsRules.OldestLike(tracks)!.Id);
    }

    [Fact]
    public void NothingDatableMeansNoSinceLine()
    {
        Assert.Null(LikedFactsRules.LikingSince(Array.Empty<Track>()));
        Assert.Null(LikedFactsRules.OldestLike(Array.Empty<Track>()));
        Assert.Null(LikedFactsRules.LikingSince(null!));
        Assert.Null(LikedFactsRules.OldestLike(null!));
    }

    // ── DominantDecade ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The mode of the decades likes were SAVED in — the only decade the data can honestly speak to, since
    /// Track carries no release year.</summary>
    [Fact]
    public void TheDominantDecadeIsTheModeOfTheSaveDates()
    {
        var tracks = new List<Track>();
        tracks.AddRange(Repeat(6, i => T(new DateTimeOffset(2021, 5, 1, 0, 0, 0, TimeSpan.Zero), "a" + i)));
        tracks.AddRange(Repeat(4, i => T(new DateTimeOffset(2015, 5, 1, 0, 0, 0, TimeSpan.Zero), "b" + i)));

        Assert.Equal(2020, LikedFactsRules.DominantDecade(tracks));
    }

    /// <summary>A dead heat goes to the more recent decade: a library split evenly is better described by the one it
    /// is still growing into.</summary>
    [Fact]
    public void ATieGoesToTheMoreRecentDecade()
    {
        var tracks = new List<Track>();
        tracks.AddRange(Repeat(5, i => T(new DateTimeOffset(2016, 5, 1, 0, 0, 0, TimeSpan.Zero), "a" + i)));
        tracks.AddRange(Repeat(5, i => T(new DateTimeOffset(2022, 5, 1, 0, 0, 0, TimeSpan.Zero), "b" + i)));

        Assert.Equal(2020, LikedFactsRules.DominantDecade(tracks));
    }

    /// <summary>The mode of a handful is trivia, not a pattern, so under the evidence floor there is no answer and the
    /// clause is simply not rendered.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(40, true)]
    public void TheDecadeNeedsEnoughStampedLikes(int stamped, bool answered)
    {
        Assert.Equal(10, LikedFactsRules.MinDecadeEvidence);

        var tracks = Repeat(stamped, i => T(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), "t" + i));
        Assert.Equal(answered ? 2020 : (int?)null, LikedFactsRules.DominantDecade(tracks));
    }

    /// <summary>And undatable likes do not count toward the floor — twenty unstamped rows are not evidence of
    /// anything.</summary>
    [Fact]
    public void UndatableLikesDoNotCountTowardTheDecadeFloor()
        => Assert.Null(LikedFactsRules.DominantDecade(Repeat(20, i => T(null, "t" + i))));

    // ── The facts as LENSES over the track list ────────────────────────────────────────────────────────────────────

    /// <summary>THE invariant behind the sparkline lens: clicking bar k returns exactly the likes bar k counted.
    /// Asserted against the real filter predicate, so the histogram and the filter cannot drift apart — they are two
    /// readings of one interval and this is the test that keeps them one.</summary>
    [Fact]
    public void EachBarsWindowSelectsExactlyTheLikesThatBarCounted()
    {
        // Twenty-eight likes spread one per six hours back from `Now`, so several bars are non-empty and one like sits
        // exactly on a bucket seam.
        var tracks = Repeat(28, i => T(Now.AddHours(-6 * i), "t" + i));
        var buckets = LikedFactsRules.LikesPerWeek(tracks, Now);

        int selectedTotal = 0;
        for (int b = 0; b < buckets.Count; b++)
        {
            var (after, before) = LikedFactsRules.WeekWindowMs(buckets[b]);
            var lens = TrackFilterState.Default.WithAddedWindow(after, before);

            int selected = 0;
            foreach (var t in tracks)
                if (TrackFilterModel.Matches(t, "", lens, false, false, Now)) selected++;

            Assert.Equal(buckets[b].Count, selected);
            selectedTotal += selected;
        }
        Assert.Equal(28, selectedTotal);   // and between them the twelve lenses partition the stamped likes
    }

    /// <summary>Consecutive bars ABUT: bucket k's upper bound is bucket k+1's lower bound to the millisecond. That is
    /// what makes the half-open <c>(after, before]</c> rule a partition rather than an approximation.</summary>
    [Fact]
    public void ConsecutiveBarWindowsAbutExactly()
    {
        var buckets = LikedFactsRules.LikesPerWeek([], Now);
        for (int i = 1; i < buckets.Count; i++)
        {
            var (_, prevEnd) = LikedFactsRules.WeekWindowMs(buckets[i - 1]);
            var (nextStart, _) = LikedFactsRules.WeekWindowMs(buckets[i]);
            Assert.Equal(prevEnd, nextStart);
        }
    }

    /// <summary>The lit bar is identified by its WINDOW, never by its index: the histogram re-rolls on every clock
    /// read, so bar 7 an hour from now is a different week.</summary>
    [Fact]
    public void OnlyTheLensedBarReadsAsLit()
    {
        var buckets = LikedFactsRules.LikesPerWeek([], Now);
        var (after, before) = LikedFactsRules.WeekWindowMs(buckets[4]);
        var lens = TrackFilterState.Default.WithAddedWindow(after, before);

        for (int i = 0; i < buckets.Count; i++)
            Assert.Equal(i == 4, LikedFactsRules.IsWeekLens(lens, buckets[i]));

        // The same twelve bars, an hour later: every window has slid, so none of them is the one that is on.
        var later = LikedFactsRules.LikesPerWeek([], Now.AddHours(1));
        for (int i = 0; i < later.Count; i++) Assert.False(LikedFactsRules.IsWeekLens(lens, later[i]));
    }

    /// <summary>Why the panel floors its clock: two renders inside the same hour must produce the SAME twelve windows,
    /// or the bar that was clicked stops matching the window the lens stored and never reads as lit.</summary>
    [Fact]
    public void ABarsWindowIsStableAcrossRendersWithinTheHour()
    {
        var early = LikedFactsRules.BucketClock(new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero));
        var late = LikedFactsRules.BucketClock(new DateTimeOffset(2026, 3, 15, 12, 59, 59, TimeSpan.Zero));
        Assert.Equal(early, late);

        var drawn = LikedFactsRules.LikesPerWeek([], early);
        var redrawn = LikedFactsRules.LikesPerWeek([], late);
        var (after, before) = LikedFactsRules.WeekWindowMs(drawn[4]);
        var lens = TrackFilterState.Default.WithAddedWindow(after, before);

        Assert.True(LikedFactsRules.IsWeekLens(lens, redrawn[4]));
    }

    /// <summary>And nothing is lost at the near end: a like saved during the current PARTIAL hour is stamped after the
    /// floored clock, and the future-stamp clamp puts it in the newest bar — which is the bar it belongs to.</summary>
    [Fact]
    public void ALikeSavedSinceTheFlooredHourStillLandsInTheNewestBar()
    {
        var wall = new DateTimeOffset(2026, 3, 15, 12, 40, 0, TimeSpan.Zero);
        var buckets = LikedFactsRules.LikesPerWeek([T(wall)], LikedFactsRules.BucketClock(wall));

        Assert.Equal(1, buckets[buckets.Count - 1].Count);
    }

    /// <summary>The identity ladder the pile ranks by and the filter matches by — uri, else id, else name, else
    /// nothing at all (a credit that cannot be a lens).</summary>
    [Theory]
    [InlineData("i", "u", "n", "u")]
    [InlineData("i", "", "n", "i")]
    [InlineData("", "", "n", "n")]
    [InlineData("", "", "", "")]
    public void ArtistKeyPrefersUriThenIdThenName(string id, string uri, string name, string expected)
        => Assert.Equal(expected, LikedFactsRules.ArtistKey(new ArtistRef(id, uri, name)));

    [Fact]
    public void ArtistKeyOfNothingIsEmpty() => Assert.Equal("", LikedFactsRules.ArtistKey(null));

    [Fact]
    public void TheArtistLensIsRecognisedByTheSameKeyItWasSetFrom()
    {
        var artist = A("vaultboy");
        var lens = TrackFilterState.Default.WithArtist(LikedFactsRules.ArtistKey(artist), artist.Name);

        Assert.True(LikedFactsRules.IsArtistLens(lens, artist));
        Assert.False(LikedFactsRules.IsArtistLens(lens, A("Henry Moodie")));
        Assert.False(LikedFactsRules.IsArtistLens(TrackFilterState.Default, artist));
    }

    /// <summary>Case-insensitive, like the chip bar and the filter: a descriptor with no display name arrives as its
    /// lowercase wire token, and "K-Pop"/"k-pop" are one concept.</summary>
    [Theory]
    [InlineData("K-Pop", "K-Pop", true)]
    [InlineData("k-pop", "K-Pop", true)]
    [InlineData("Pop", "K-Pop", false)]
    [InlineData("K-Pop", "", false)]
    public void TheTagLensMatchesTheChipsCaseInsensitively(string active, string title, bool expected)
        => Assert.Equal(expected, LikedFactsRules.IsTagLens(TrackFilterState.Default with { Tag = active }, title));

    /// <summary>The lenses are independent facets that combine, so the header has to be able to name each of them.</summary>
    [Fact]
    public void ActiveLensesReportsEveryRailFacetThatIsOn()
    {
        Assert.Equal(LikedFactsRules.LikedLens.None, LikedFactsRules.ActiveLenses(TrackFilterState.Default));

        var all = TrackFilterState.Default
            .WithAddedWindow(1_000L, 2_000L)
            .WithArtist("spotify:artist:a", "vaultboy") with { Tag = "Pop" };

        Assert.Equal(LikedFactsRules.LikedLens.Week | LikedFactsRules.LikedLens.Artist | LikedFactsRules.LikedLens.Tag,
                     LikedFactsRules.ActiveLenses(all));

        // The flyout's own facets are the flyout's to describe — the header must not claim a lens the rail never offered.
        Assert.Equal(LikedFactsRules.LikedLens.None,
                     LikedFactsRules.ActiveLenses(TrackFilterState.Default with { Duration = TrackDurationRange.OverFiveMinutes }));
    }

    /// <summary>The header's clear is a PER-FACET undo, not a reset: dropping the week from "this week, by vaultboy"
    /// means "vaultboy, all time" — not "start over".</summary>
    [Fact]
    public void ClearingOneLensLeavesTheOthersStanding()
    {
        var all = TrackFilterState.Default
            .WithAddedWindow(1_000L, 2_000L)
            .WithArtist("spotify:artist:a", "vaultboy") with { Tag = "Pop", Duration = TrackDurationRange.OverFiveMinutes };

        var noWeek = LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.Week);
        Assert.Equal(0L, noWeek.AddedAfterMs);
        Assert.Equal("spotify:artist:a", noWeek.ArtistId);
        Assert.Equal("Pop", noWeek.Tag);
        Assert.Equal(TrackDurationRange.OverFiveMinutes, noWeek.Duration);   // and the flyout's facets are untouched

        var noArtist = LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.Artist);
        Assert.Null(noArtist.ArtistId);
        Assert.Null(noArtist.ArtistName);
        Assert.Equal(1_000L, noArtist.AddedAfterMs);

        Assert.Null(LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.Tag).Tag);
        Assert.Equal(all, LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.None));
    }
}
