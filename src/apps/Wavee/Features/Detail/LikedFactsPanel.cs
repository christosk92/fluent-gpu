using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The Liked Songs rail's facts bento — the mount seam. <see cref="Has"/> answers whether there is anything
/// honest to say; <see cref="Panel"/> hands back the stack. Both are the ONLY surface the detail rail sees, so the
/// component below can change shape without touching the rail.</summary>
internal static class LikedFacts
{
    /// <summary>True when this is the Liked collection AND at least one track carries a stamp we can reason about.
    /// Every card in the panel is derived from <c>AddedAt</c>, so "no usable stamps" means "no panel" — not a stack of
    /// empty cards, and not a zero-height row the rail's keying has to carry (E1).
    ///
    /// <para>Returns on the FIRST usable stamp: on a 10k liked list the common answer costs one iteration, and the
    /// worst case (a completely unstamped list) is one pass of a struct predicate.</para></summary>
    internal static bool Has(DetailModel m)
    {
        if (m is null || !LikedSongsArtwork.IsLikedUri(m.ContextUri)) return false;
        var tracks = m.Tracks;
        if (tracks is null) return false;
        for (int i = 0; i < tracks.Count; i++)
            if (LikedFactsRules.TryStamp(tracks[i], out _)) return true;
        return false;
    }

    /// <summary>The panel. Props are RE-PUSHED (Embed.Comp's props overload), so a like/unlike — which refreshes
    /// <c>LibraryStore.Liked</c> in place and hands the page a new track list — flows straight through to a recompute,
    /// while a parent re-render carrying the SAME list is coalesced to nothing (E10).
    ///
    /// <para><paramref name="outerPadding"/> false is the rail's arm: the rail owns the column's side padding, exactly
    /// as it does for <c>AlbumTrailing.ReleasePanel</c>.</para></summary>
    internal static Element Panel(DetailModel m, DetailHandlers h, bool outerPadding = true)
        => Embed.Comp(new LikedFactsPanel.Props(m.Tracks, m.ContextUri, h, outerPadding),
                      static () => new LikedFactsPanel()) with { Key = "liked-facts" };
}

/// <summary>The facts stack itself: This week (the +N and a 12-week bar strip), Most liked (a face pile and the names
/// behind it), Your blend (the primary-descriptor partition as one bar plus its legend), Rediscover (what you liked
/// this week last year, and a way to play it), and the since-line.
///
/// <para>ALL arithmetic lives in <see cref="LikedFactsRules"/> — this file decides only what to draw and when not to.
/// The one thing the rules cannot supply is the clock: <c>now</c> is read ONCE here, at the panel boundary, and passed
/// down, which is what keeps every rule a pure function of (tracks, now) and testable without a page.</para>
///
/// <para>The panel OWNS its entrance (the <c>ReleasePanel</c> contract, DetailTrailing.cs:229-231): one fade-up as it
/// appears, a stagger down the cards, and a FLIP when a sibling shoves it. The caller mounts it with <c>Row</c>, never
/// <c>LateRow</c> — a second entrance on top of this one double-fades the whole column.</para>
///
/// <para>Honesty rules that are load-bearing, not decoration: a card that has no evidence is NOT MOUNTED (no zeroed
/// bars, no invented percentages, no disabled button); the blend bar is drawn in THEME ink rather than cover-palette
/// colour, because this column sits on the page surface and palette belongs on imagery; and there is no play-recency
/// card at all, because the play log is a 200-entry ring that cannot answer one.</para></summary>
sealed class LikedFactsPanel : Component
{
    /// <summary>What the rail re-pushes each render. A record so an unchanged list is an equal Props and the child
    /// never re-renders; <c>DetailHandlers</c> is the shell's ONE mount-stable instance, so it compares equal too.</summary>
    internal sealed record Props(IReadOnlyList<Track> Tracks, string? ContextUri, DetailHandlers Handlers, bool OuterPadding);

    // ── The prototype's numbers (docs/plans/wavee/liked-songs-cover-mica.html, `.fact` / `.spark` / `.bar`) ──────────
    const int SparkWeeks = 12;
    const float SparkHeight = 38f;
    const float SparkBarFloor = 3f;      // a silent week is still a visible baseline, never a gap in the strip
    const float SparkGap = 3f;
    const int TopArtistCount = 5;
    const int BlendSlices = 5;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var svc = UseContext(Services.Slot);
        var tracks = p.Tracks ?? Array.Empty<Track>();
        var culture = CultureInfo.CurrentCulture;
        // THE ONE CLOCK READ. Local (not UTC): the week the user is living through is the local one, and the since-line
        // names a local month. Every rule below takes this as a parameter and reads no clock of its own.
        //
        // FLOORED TO THE HOUR (LikedFactsRules.BucketClock): the rolling windows have to be the same twelve intervals
        // on every render of the same hour, or a bar's window — which the LENS stores as two absolute instants — would
        // stop matching the bar the moment the panel re-rendered, and the clicked bar would never read as lit.
        var now = LikedFactsRules.BucketClock(DateTimeOffset.Now);

        // The rail facts are LENSES, so the panel has to know which one is lit: this READ is the subscription that
        // repaints the selected bar / face / slice when the list's filter changes from anywhere — the chip bar, the
        // filter flyout's Clear all, the list header's clear affordance, or a click in this very panel.
        var filters = p.Handlers.Filters?.Value ?? TrackFilterState.Default;

        var cards = new List<Element>(5);

        // (a) This week — present as soon as ANY like is stamped. A quiet week is a real answer ("+0"), which is why
        // this card, unlike the others, has no evidence floor beyond "the data exists at all".
        if (AnyStamped(tracks))
            cards.Add(ThisWeekCard(LikedFactsRules.LikesPerWeek(tracks, now, SparkWeeks), culture, in filters, p.Handlers));

        // (b) Most liked.
        var top = LikedFactsRules.TopArtists(tracks, TopArtistCount);
        if (top.Count > 0) cards.Add(MostLikedCard(top, svc?.RealStore, p.Handlers, culture, in filters));

        // (c) Your blend — BlendShares self-gates on ContentFilterTags.MinTrackCount and returns empty when the
        // descriptors were never fetched, so "empty ⇒ no card" is the whole of E14 (no fabricated percentages).
        var shares = LikedFactsRules.BlendShares(tracks, BlendSlices);
        // Its OWN component: the card owns an open/closed state, and a hook declared under this `if` would shift every
        // later hook slot the first time the descriptors land. (It reads the filter signal itself, so a lens click
        // repaints 8 DIP of bar instead of the whole bento.)
        if (shares.Count > 0) cards.Add(LikedBlendCard.Create(tracks, shares, p.Handlers));

        // (d) Rediscover — the same seven weekdays a year ago. Mounted only when that window actually holds something.
        var (start, end) = LikedFactsRules.ThisWeekLastYearWindow(now);
        var lastYear = LikedFactsRules.LikedInWindow(tracks, start, end);
        if (lastYear.Count > 0) cards.Add(RediscoverCard(lastYear, svc, p.ContextUri));

        // (e) The since-line.
        if (SinceLine(tracks, culture) is { } since) cards.Add(since);

        if (cards.Count == 0) return new BoxEl();

        // Reduced motion is a VALUE (DetailTrailing.cs:214), never a branch that changes what is authored: the stagger
        // goes to zero, the cards still fade through the engine's KeepFade policy.
        float stagger = Motion.ReducedMotion ? 0f : WaveeMotion.MastheadStaggerMs;
        return new BoxEl
        {
            Key = "liked-facts-panel", Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Direction = 1, Gap = Spacing.S, MinWidth = 0f, Stagger = stagger,
            Padding = p.OuterPadding ? new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.L) : Edges4.All(0f),
            Children = cards.ToArray(),
        };
    }

    // ── This week ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same first-usable-stamp scan <see cref="LikedFacts.Has"/> runs, asked again here because the panel
    /// must be correct when mounted directly (a caller that skipped the gate) and because a list can lose its stamps
    /// between the gate and this render.</summary>
    static bool AnyStamped(IReadOnlyList<Track> tracks)
    {
        for (int i = 0; i < tracks.Count; i++)
            if (LikedFactsRules.TryStamp(tracks[i], out _)) return true;
        return false;
    }

    static Element ThisWeekCard(IReadOnlyList<LikedFactsRules.WeekBucket> weeks, CultureInfo culture,
        in TrackFilterState filters, DetailHandlers h)
    {
        int thisWeek = weeks.Count > 0 ? weeks[weeks.Count - 1].Count : 0;
        // Scale to the tallest bucket, floor 1: twelve bars against a fixed ceiling would render most libraries as a
        // flat line, and the strip's job is the SHAPE of the last twelve weeks, not their absolute rate.
        int peak = 1;
        for (int i = 0; i < weeks.Count; i++) if (weeks[i].Count > peak) peak = weeks[i].Count;

        var bars = new Element[weeks.Count];
        for (int i = 0; i < weeks.Count; i++)
        {
            var week = weeks[i];
            bool newest = i == weeks.Count - 1;
            bool lit = LikedFactsRules.IsWeekLens(filters, week);
            Element bar = new BoxEl
            {
                Height = MathF.Max(SparkBarFloor, SparkHeight * week.Count / peak),
                Corners = new CornerRadius4(2f, 2f, 1f, 1f),
                // Full accent for the newest week (it is "this week", the number beside the strip) and for the LENSED
                // week (it is what the list is showing). Everything else keeps the recessive shade.
                Fill = newest || lit ? Tok.AccentDefault : (Tok.AccentDefault with { A = 0.38f }),
                // The bar answers the hover its COLUMN caught: the column authors HoverFill of its own, so it owns an
                // InteractionAnim row and the recorder hands that eased progress down to this non-interactive child
                // (SceneRecorder.ResolveSurface). The accent's bright shade says which of the twelve the bubble is about.
                HoverFill = Tok.AccentTextPrimary,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                HitTestVisible = false,
            };
            // A 2-DIP-wide, 3-DIP-tall bar is not a hit target. The column is: full strip height, its share of the
            // strip's width, bottom-aligned so the bar keeps sitting on the baseline — hovering the EMPTY air above a
            // quiet week is still hovering that week, which is the only way twelve of these are reachable by mouse.
            //
            // The column is also the LENS: clicking it filters the list to exactly the interval this bar counted, and
            // clicking the lit one clears it (the chip bar's "a second tap means switch, or off" grammar). So it paints
            // its own hover/selected plate — a chart's hover band — rather than being an invisible hit box. Without a
            // cursor change and a plate the strip read as decoration, which is exactly what it used to be.
            void Toggle()
            {
                // Decided against the LIVE state, not this render's snapshot: the filter can have moved under us (the
                // list header's clear, the flyout's Clear all) between the render that drew this bar and the click.
                var live = h.Filters?.Peek() ?? TrackFilterState.Default;
                var (after, before) = LikedFactsRules.WeekWindowMs(week);
                h.SetFilters?.Invoke(LikedFactsRules.IsWeekLens(live, week)
                    ? live.WithAddedWindow(0L, 0L)
                    : live.WithAddedWindow(after, before));
            }
            Element column = new BoxEl
            {
                Direction = 1, Justify = FlexJustify.End, Height = SparkHeight, Basis = 0f, MinWidth = 0f,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
                Corners = CornerRadius4.All(3f),
                Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
                HoverFill = lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                OnClick = Toggle,
                Children = [bar],
            };
            bars[i] = ToolTip.Wrap(column, WeekTip(week, culture), grow: 1f, showDelayMs: LikedLens.TipDelayMs)
                with { Key = "wk:" + i };
        }

        var spark = new BoxEl
        {
            Direction = 0, Gap = SparkGap, Height = SparkHeight, Grow = 1f, Basis = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.End, HitTestPassThrough = true, Children = bars,
        };

        var big = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f,
            Children =
            [
                // The changing number is a value-KEYED box inside a ZStack (CompactStatTile's idiom): a like landing
                // while the page is open swaps "+11" for "+12" as a rise-and-blur, never an in-place relabel (E10).
                ZStack(new BoxEl
                {
                    Key = "v:" + thisWeek,
                    Animate = MotionRecipes.TextSwap,
                    Children = [Title(Strings.Detail.LikedFacts.LikedDelta(thisWeek)) with { MaxLines = 1 }],
                }),
                Caption(Strings.Detail.LikedFacts.SongsLiked(thisWeek)) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };

        return Card("fact:week",
            Head(Loc.Get(Strings.Detail.LikedFacts.ThisWeek), Strings.Detail.LikedFacts.WindowCaption(weeks.Count)),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [big, spark],
            });
    }

    /// <summary>"Aug 11 – Aug 18 · 3 songs liked" — what one bar of the strip stands for.
    ///
    /// <para>BOTH endpoints are named, and the second one is <c>start + 7d</c> rather than <c>start + 6d</c>: the
    /// buckets are ROLLING windows anchored on the moment the panel read its clock (<c>LikesPerWeek</c>), not calendar
    /// weeks, so <c>(Aug 11 12:00, Aug 18 12:00]</c> is literally the interval counted. Printing "Aug 11–17" would name
    /// a calendar span the histogram does not use and would misplace every like saved after noon on the 17th.</para>
    ///
    /// <para>Local time, like the since-line: the week the user is living through is the local one. The month/day
    /// pattern is the culture's own ("MMM d"), so the order and the abbreviation follow the locale.</para></summary>
    static string WeekTip(in LikedFactsRules.WeekBucket week, CultureInfo culture)
    {
        var (after, before) = LikedFactsRules.WeekWindowMs(week);
        // ONE range formatter, shared with the list's lens header (LikedLens.RangeParts): the bubble and the header
        // describe the same filter, so a second copy of this wording is a second chance for them to disagree.
        var (start, end) = LikedLens.RangeParts(after, before, culture);
        return Strings.Detail.LikedFacts.WeekTip(start, end, week.Count);
    }

    // ── Most liked ──────────────────────────────────────────────────────────────────────────────────────────────────

    static Element MostLikedCard(IReadOnlyList<LikedFactsRules.ArtistCount> top, IStore? store, DetailHandlers h,
        CultureInfo culture, in TrackFilterState filters)
    {
        // The face and the name are the SAME affordance: they lens the list to that artist's liked songs, and a second
        // click clears it. They deliberately no longer NAVIGATE to the artist page — a statistic about this collection
        // should answer itself here, and the rail sits beside the list precisely so the answer is one click away with
        // no page change. (The artist page stays a click away from any of that artist's rows.)
        void Toggle(ArtistRef who)
        {
            var live = h.Filters?.Peek() ?? TrackFilterState.Default;
            h.SetFilters?.Invoke(LikedFactsRules.IsArtistLens(live, who)
                ? live.WithArtist(null)
                : live.WithArtist(LikedFactsRules.ArtistKey(who), who.Name));
        }

        var faces = new FacePiles.Face[top.Count];
        // One run of spans, not a row of boxes: "TOTO 8 · Billy Idol 6" has to WRAP as a sentence, and a flex row of
        // per-artist boxes wraps as blocks with a ragged right edge. Each name is a hyperlink span (TextSpan.OnClick →
        // hand cursor + click, resolved by the engine over the span's own laid rects).
        var spans = new List<TextSpan>(top.Count * 3);
        for (int i = 0; i < top.Count; i++)
        {
            var artist = top[i].Artist;
            string name = artist.Name;
            // A credit with no uri, no id and no name cannot be a lens — there is nothing to match rows against — so it
            // stays inert rather than becoming a click that empties the list.
            bool lensable = LikedFactsRules.ArtistKey(artist).Length > 0;
            bool lit = LikedFactsRules.IsArtistLens(filters, artist);
            Action? click = lensable ? () => Toggle(artist) : null;
            // Portrait: RESIDENT ONLY. A facts card does not get to fire a hydration batch — the initials fallback is
            // an honest answer, a network round trip from a statistic is not. (Bounded to `top.Count` lookups, and the
            // panel re-renders only when its props actually change.)
            faces[i] = new FacePiles.Face(name, store?.GetArtist(artist.Uri)?.Image?.Url, click, lit,
                                          Strings.Detail.LikedFacts.ArtistTip(name, top[i].Count));

            if (i > 0) spans.Add(new TextSpan(Sep, Color: Tok.TextTertiary));
            spans.Add(new TextSpan(name, Weight: 600, Color: lit ? Tok.AccentTextPrimary : Tok.TextPrimary,
                OnClick: click));
            spans.Add(new TextSpan(" " + top[i].Count.ToString(culture), Color: Tok.TextTertiary));
        }

        var who = new SpanTextEl(spans.ToArray())
        {
            Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary,
            Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            Grow = 1f, Basis = 0f, MinWidth = 0f,
        };

        return Card("fact:artists",
            Head(Loc.Get(Strings.Detail.LikedFacts.MostLiked), null),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = [FacePiles.Strip(faces, TopArtistCount), who],
            });
    }

    // ── Rediscover ──────────────────────────────────────────────────────────────────────────────────────────────────

    static Element RediscoverCard(IReadOnlyList<Track> window, Services? svc, string? contextUri)
    {
        var body = new List<Element>(2)
        {
            Caption(Strings.Detail.LikedFacts.LastYear(window.Count)) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                Grow = 1f, Basis = 0f, MinWidth = 0f,
            },
        };

        // The button exists only when there is a real player and a real context to play into. PlayOrderedAsync IS the
        // subset seam (PlaybackController.cs:737): it carries the embedded page, so both the local host and a remote
        // device receive THESE tracks in THIS order rather than the whole collection. No player ⇒ no button; the fact
        // still reads on its own, and a dead control would be worse than none.
        if (svc?.Player is { } player && contextUri is { Length: > 0 } uri)
        {
            void PlayThem()
            {
                // Built on the click, not on every render: the window can be hundreds of tracks and this is a cold path.
                var ordered = new PlaybackContextTrack[window.Count];
                for (int i = 0; i < window.Count; i++)
                    ordered[i] = new PlaybackContextTrack(window[i].Uri, window[i].ContextUid ?? string.Empty);
                _ = player.PlayOrderedAsync(uri, ordered, 0);
            }

            body.Add(Button.Create(Loc.Get(Strings.Detail.LikedFacts.PlayThem), PlayThem,
                ButtonAppearance.Standard, ControlSize.Small) with { Shrink = 0f });
        }

        return Card("fact:lastyear",
            Head(Loc.Get(Strings.Detail.LikedFacts.Rediscover), null),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = body.ToArray(),
            });
    }

    // ── The since-line ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Liking since March 2019 · mostly the 2010s · oldest like Africa" — each clause PRESENT-ONLY, so a
    /// young library says only what it can (the decade needs ten stamped likes; the whole line needs one).</summary>
    static Element? SinceLine(IReadOnlyList<Track> tracks, CultureInfo culture)
    {
        var clauses = new List<string>(3);
        if (LikedFactsRules.LikingSince(tracks) is { } since)
            clauses.Add(Strings.Detail.LikedFacts.Since(since.ToLocalTime().ToString("MMMM yyyy", culture)));
        if (LikedFactsRules.DominantDecade(tracks) is { } decade)
            clauses.Add(Strings.Detail.LikedFacts.Decade(decade.ToString(culture)));
        // Title only. The prototype's "Africa (1982)" is a RELEASE year, and Track carries none — the only year this
        // model owns is the year it was saved, which the since-clause has already said. Printing it again would either
        // repeat that year or, worse, be read as the release year the data cannot answer.
        if (LikedFactsRules.OldestLike(tracks) is { Title.Length: > 0 } oldest)
            clauses.Add(Strings.Detail.LikedFacts.Oldest(oldest.Title));
        if (clauses.Count == 0) return null;

        return new BoxEl
        {
            Key = "caption:since", Direction = 1, MinWidth = 0f,
            Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Padding = new Edges4(Spacing.XXS, Spacing.XXS, Spacing.XXS, 0f),
            Children =
            [
                Caption(string.Join(Sep, clauses)) with
                {
                    Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    // ── Shared card shell ───────────────────────────────────────────────────────────────────────────────────────────

    const string Sep = " · ";

    /// <summary>The prototype's `.fact`: card fill, one-pixel card stroke, card radius, card elevation. Keyed and
    /// self-entering — a card that arrives late (the descriptors landing, the first like of the week) fades up and
    /// FLIPs its siblings down rather than snapping the column.</summary>
    internal static Element Card(string key, Element head, Element body) => new BoxEl
    {
        Key = key, Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
        Direction = 1, Gap = 6f, MinWidth = 0f,
        Padding = new Edges4(Spacing.M, 10f, Spacing.M, 11f),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
        Children = [head, body],
    };

    /// <summary>A card header: the eyebrow, and an optional right-hand fact. The eyebrow GROWS so the fact is pushed to
    /// the trailing edge (the prototype's `margin-left:auto`) without a spacer node.</summary>
    internal static Element Head(string title, string? trailing)
    {
        var label = WaveeType.Eyebrow(title) with
        {
            Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        var kids = trailing is { Length: > 0 } fact
            ? new Element[] { label, Caption(fact) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 } }
            : new Element[] { label };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children = kids,
        };
    }
}


/// <summary>"Your blend" — the primary-descriptor partition as one stacked bar, and what the bar's remainder is
/// actually made of.
///
/// <para>THE PROBLEM THIS SHAPE SOLVES (docs/plans/wavee/liked-blend-card-mica.html, card B′). The partition is
/// correct — every tagged like counted once, by its primary descriptor, so the slices stack to one bar — but on a real
/// library the five named slices cover barely half of it and the rest used to be ONE flat grey slab with a tooltip.
/// Half the card said "other" and did nothing. So the remainder is now drawn as WHAT IT IS: one thin proportional TICK
/// per descriptor, and every tick is a real tag — it names itself on hover and lenses the list on click. No dead
/// pixels, and the texture itself says "many small things" where the slab said "one big unknown".</para>
///
/// <para>AND IT OPENS. The legend ends in a disclosure button ("44 more · 47 %") that cracks the card open IN PLACE:
/// the body reveals below with the same tail redrawn at FULL WIDTH — so a descriptor worth 3 % of the library is a
/// segment you can actually hit instead of six pixels — plus a legend of the ones above 1 % and an honest count of the
/// ones below. The reveal is the WinUI Expander's motion, borrowed structurally rather than by reference
/// (<c>Expander.cs:242-256</c>): an always-mounted clip host whose declared height toggles 0 ↔ auto under a
/// <see cref="SizeMode.Reflow"/> transition, so the cards BELOW this one reflow down through real layout rather than
/// being overlapped by a revealing panel.</para>
///
/// <para>WHY A COMPONENT. <c>open</c> is a hook, and the panel mounts this card under an <c>if (shares.Count > 0)</c>
/// gate — a hook declared there would shift every later hook slot the first time the descriptors land. Its own
/// component also scopes the re-render: a lens click repaints the bar, not the whole bento.</para></summary>
sealed class LikedBlendCard : Component
{
    /// <summary>Re-pushed by the panel every render. <c>Shares</c> is rebuilt each time so the props never compare
    /// equal — which is what we want here: the card must recompute when a like/unlike changes the partition, and the
    /// panel itself only re-renders when ITS props or the filter signal changed.</summary>
    internal sealed record Props(IReadOnlyList<Track> Tracks, IReadOnlyList<LikedFactsRules.TagShare> Shares,
                                 DetailHandlers Handlers);

    internal static Element Create(IReadOnlyList<Track> tracks, IReadOnlyList<LikedFactsRules.TagShare> shares,
                                   DetailHandlers h)
        => Embed.Comp(new Props(tracks, shares, h), static () => new LikedBlendCard()) with { Key = "fact:blend" };

    // ── The prototype's numbers (`.bar` / `.seg` / `.tk` / `.legend`) ────────────────────────────────────────────────
    const float BlendBarHeight = 8f;
    const float BlendBarRadius = 4f;
    const float BlendSegGap = 2f;
    const float LegendDot = 8f;
    /// <summary>Gap and floor for the tail ticks. A tick narrower than this is not a pointer target at all, so the floor
    /// is a hit-target minimum rather than a style: below it the strip would be a texture you cannot touch. When the
    /// floors stop fitting (a very long tail in a 240-DIP rail) the sub-floor ticks paint at the floor and eat their
    /// gap — see <see cref="TailTicks"/> — and the strip clips its own overflow so the five named slices keep their
    /// share whatever the tail's length.</summary>
    const float TickGap = 1f;
    const float TickMinWidth = 2f;
    /// <summary>A descriptor under this share of the tagged likes gets a COUNT in the tail legend instead of a row —
    /// see <c>LikedFactsRules.TailSplit</c>, which owns the rule.</summary>
    const float TailLegendFloor = 0.01f;

    /// <summary>The blend bar's ink: ONE accent stepped down in alpha, not five hues. Five distinct colours here would
    /// read as five categories with meanings; five weights of the same accent read as what this actually is — a ranked
    /// share of one quantity.</summary>
    static readonly float[] SliceAlpha = [1f, 0.8f, 0.6f, 0.45f, 0.3f];

    /// <summary>The tail's ink: THEME text ink at three low alphas, cycled by index (the prototype's .16/.22/.30). Not
    /// accent — the tail is deliberately not one of the named slices — and not literal white, because this card sits on
    /// the page surface in both themes and <c>Tok.TextPrimary</c> is the ink that flips with it. The cycle is what makes
    /// forty adjacent ticks read as forty things rather than one dithered block.</summary>
    static readonly float[] TickAlpha = [0.16f, 0.22f, 0.30f];
    /// <summary>The OPENED tail bar's ink: the same theme ink, alternating, at the weights a full-width bar can carry
    /// (the prototype's .55/.38). Brighter than the ticks because at full width these are segments, not texture.</summary>
    static readonly float[] TailSegAlpha = [0.55f, 0.38f];

    /// <summary>The body host's motion — the WinUI Expander open/close, applied to the host's LAYOUT height.
    /// <see cref="SizeMode.Reflow"/> (not Reveal) so the facts cards BELOW this one glide down through real layout;
    /// <see cref="SizeAnchor.Leading"/> so the body wipes downward from the legend instead of sliding up from under it.
    /// Expand 333 ms / collapse 167 ms are the Disclosure tokens, which also carry the reduced-motion policy.</summary>
    static readonly LayoutTransition BodyReveal = new(
        TransitionChannels.Size,
        MotionTok.DisclosureExpand.ToDynamics(),
        Size: SizeMode.Reflow,
        ExitDynamics: MotionTok.DisclosureCollapse.ToDynamics(),
        Anchor: SizeAnchor.Leading);

    /// <summary>The opened tail bar's entrance: it wipes out from its left edge (scaleX .55 → 1) as it fades in, the
    /// prototype's `.exp .bar{transform-origin:left;transform:scaleX(.55)}`. The dynamics live IN the LayoutTransition
    /// rather than in <c>Element.Transition</c> because a node that declares <c>Layout</c> has its <c>Transition</c>
    /// IGNORED (Reconciler.SynthesizeDeclarative) — the RecentsPage `DrawerReveal` shape.</summary>
    static readonly LayoutTransition TailBarReveal = new(
        TransitionChannels.Opacity,
        MotionTok.DisclosureExpand.ToDynamics(),
        Enter: new EnterExit(Sx: 0.55f, Opacity: 0f, Active: true));

    /// <summary>One tail-legend row's entrance. Delayed per row by <c>WaveeEntrance.DelayMs</c> — the app's ONE stagger
    /// rung, which is capped at <c>WaveeEntrance.StaggerCap</c> and returns 0 under reduced motion. (The engine's
    /// parent-side <c>Element.Stagger</c> spelling is index × ms with NO ceiling, so a fifteen-row tail authored that
    /// way would still be arriving 600 ms after the card opened.)</summary>
    static readonly LayoutTransition TailRowReveal = new(
        TransitionChannels.Opacity,
        MotionTok.DisclosureExpand.ToDynamics(),
        Enter: new EnterExit(Dy: Spacing.XS, Opacity: 0f, Active: true));

    public override Element Render()
    {
        var p = UseProps<Props>();
        var culture = CultureInfo.CurrentCulture;
        // The lens READ, done here rather than inherited as a prop: it is the subscription that re-inks the lit slice /
        // tick / legend row when the filter changes from anywhere (the chip bar, the flyout's Clear all, the list
        // header's per-facet clear, or a click in this very card).
        var filters = p.Handlers.Filters?.Value ?? TrackFilterState.Default;

        var open = UseSignal(false);
        // The body's MOUNT lags `open` on collapse: the clip has to shrink OVER real content, exactly as WinUI keyframes
        // Visibility=Collapsed at t=167ms (Expander.xaml:81-83). The watcher below flips this off at settle.
        var shown = UseSignal(false);
        var hostRef = UseRef<NodeHandle>(default);

        bool isOpen = open.Value;              // subscribe
        bool showBody = shown.Value;           // subscribe: the watcher's write re-renders us
        bool closing = showBody && !isOpen;    // mid collapse-reflow: body mounted, host shrinking, watcher armed

        void Toggle()
        {
            bool next = !open.Peek();
            open.Value = next;
            if (next) shown.Value = true;      // mounting FIRST is what lets the reflow seed from a real content height
        }

        // The blend drives the CHIPS' facet, not one of its own: "Pop" in the bar and "Pop" in the chip bar are the
        // same question, so clicking the slice lights the chip and clicking the chip lights the slice. Exclusive, and a
        // second click clears — the chip bar's grammar, inherited rather than re-invented.
        void ToggleTag(string title)
        {
            // The LIVE state, not this render's snapshot: the filter can have moved under us (the list header's clear,
            // the flyout's Clear all) between the render that drew this tick and the click.
            var live = p.Handlers.Filters?.Peek() ?? TrackFilterState.Default;
            p.Handlers.SetFilters?.Invoke(live with { Tag = LikedFactsRules.IsTagLens(live, title) ? null : title });
        }

        var shares = p.Shares;
        float listed = 0f;
        for (int i = 0; i < shares.Count; i++) listed += shares[i].Fraction;
        float other = MathF.Max(0f, 1f - listed);

        // The FULL tail: same partition, same denominator, every remaining descriptor named (detail unbounded ⇒
        // MoreTags == 0), so the ticks account for the whole remainder with nothing pooled behind them.
        var tail = LikedFactsRules.BlendOther(p.Tracks, shares.Count, int.MaxValue);
        // The tail is drawn only when it is actually there: at five slices covering everything, a phantom sliver would
        // be a lie the bar tells at one pixel wide.
        bool hasTail = other > 0.005f && tail.Named.Count > 0;

        var segments = new List<Element>(shares.Count + 1);
        var legend = new List<Element>(shares.Count + 1);
        for (int i = 0; i < shares.Count; i++)
        {
            string title = shares[i].Title;
            bool lit = LikedFactsRules.IsTagLens(filters, title);
            // A lensed slice goes to FULL accent whatever its rank: the bar's alpha ladder ranks shares, and the one the
            // list is actually showing has to read as chosen rather than as merely first.
            var ink = lit ? Tok.AccentDefault : Tok.AccentDefault with { A = SliceAlpha[Math.Min(i, SliceAlpha.Length - 1)] };
            // ONE string per slice, shown by BOTH the segment and its legend row: a bar this thin is hard to hit and a
            // legend row is not, so either affordance has to answer the same question, and answering it twice from two
            // formatters is how the two drift apart.
            string tip = Strings.Detail.LikedFacts.ShareTip(title, shares[i].Count,
                                                            shares[i].Fraction.ToString("P0", culture));
            void Click() => ToggleTag(title);
            segments.Add(Segment("seg:" + title, shares[i].Fraction, ink, Tok.AccentTextPrimary, tip, Click));
            legend.Add(LegendEntry("leg:" + title, ink, title, shares[i].Fraction.ToString("P0", culture), tip, lit, Click));
        }
        if (hasTail)
        {
            segments.Add(TailTicks(tail.Named, other, culture, in filters, ToggleTag));
            legend.Add(MoreButton(tail, other, isOpen, culture, Toggle, () => open.Value));
        }

        var bar = new BoxEl
        {
            Direction = 0, Height = BlendBarHeight, Gap = BlendSegGap, MinWidth = 0f,
            Corners = CornerRadius4.All(BlendBarRadius), ClipToBounds = true, HitTestPassThrough = true,
            Children = segments.ToArray(),
        };

        // The body host — ALWAYS MOUNTED, the transition's node. The declared Height toggle 0 ↔ NaN(auto) IS the whole
        // trigger: the commit snap-solves the new target, the host's FLIP projection diffs old vs new size, and the
        // Reflow track eases the LAYOUT height while the cards below reflow each tick.
        var host = new BoxEl
        {
            Key = "blend-body-host",
            Direction = 1, ClipToBounds = true, MinWidth = 0f,
            Height = isOpen ? float.NaN : 0f,
            Animate = BodyReveal,
            OnRealized = h => hostRef.Value = h,
            Children = showBody && hasTail
                ? [Body(tail, culture, in filters, ToggleTag)]
                : [],
        };

        // The COLLAPSED card — bar over legend, on the column's own rhythm.
        Element collapsed = new BoxEl
        {
            Key = "blend-top", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                bar,
                new BoxEl { Key = "blend-legend", Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f,
                            Children = legend.ToArray() },
            ],
        };
        // …and the body host hangs off an UNGAPPED outer column. A flex gap is charged per child BOUNDARY, not per
        // painted pixel (FlexLayout: gap × (n-1)), so a zero-height host inside the gapped column would leave 8 DIP of
        // dead air under the legend while the card is shut — and the collapse watcher below, which is a real child for
        // the ~167 ms it exists, would add 8 more at exactly the moment the fold has to read as smooth. The open
        // state's breathing room lives INSIDE the body instead (its top padding), which is also what makes that space
        // part of the revealed height rather than a step that appears before the reveal starts.
        var stack = new List<Element>(3) { collapsed, host };
        // The watcher is mounted ONLY while the collapse reflow runs (the ~167 ms the body stays mounted under a
        // shrinking clip); the rest of the time this card has no per-frame subscriber at all.
        if (closing) stack.Add(Embed.Comp(() => new BlendCollapseWatcher { Host = () => hostRef.Value, Shown = shown })
                               with { Key = "blend-collapse-watch" });

        return LikedFactsPanel.Card("fact:blend",
            // The header count is the TAGGED population, not the library: the bar partitions the likes that carry a
            // descriptor, and labelling it with the whole collection would silently claim coverage it does not have.
            LikedFactsPanel.Head(Loc.Get(Strings.Detail.LikedFacts.Blend),
                                 Strings.Detail.SongCount(LikedFactsRules.TaggedTotal(shares))),
            new BoxEl { Direction = 1, MinWidth = 0f, Children = stack.ToArray() });
    }

    // ── The collapsed bar ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One stacked slice, wrapped in its own tooltip. The FILL lives on a non-hit-testable child rather than on
    /// the tooltip's trigger box because the trigger is the kit's node, not ours to paint — and because that is the same
    /// ancestor-hover-drives-descendant shape the sparkline's bars use, so the two data surfaces on this card respond
    /// identically.</summary>
    static Element Segment(string key, float fraction, ColorF ink, ColorF hoverInk, string tip, Action onClick)
    {
        Element fill = new BoxEl
        {
            Grow = 1f, Basis = 0f, MinWidth = 0f, Fill = ink, HoverFill = hoverInk,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            HitTestVisible = false,
        };
        // The hit box is a SEPARATE node from the painted slice for the same reason the sparkline's column is: the slice
        // is 8 DIP tall and its own paint must not also be the thing that answers the pointer (the fill is a cross-fade
        // target, and a click plate on it would fight that).
        Element target = new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            OnClick = onClick,
            Children = [fill],
        };
        // grow: the slice's share — the tooltip wrapper is the bar's real flex child, so it is the node that has to
        // carry the proportion. Its target is grown to match (ToolTip.Fill), which is what keeps the painted slice
        // exactly as wide as the share it is drawn from.
        return ToolTip.Wrap(target, tip, grow: fraction, showDelayMs: LikedLens.TipDelayMs) with { Key = key };
    }

    /// <summary>The tail region of the collapsed bar: ONE tick per remaining descriptor, each proportional to its own
    /// count, each a real tag (instant bubble, lens click, hand cursor, hover lift to the bright accent).
    ///
    /// <para>The strip takes the bar's whole remainder (<paramref name="other"/>) and divides it by count, so the ticks
    /// together are exactly as wide as the slab they replaced and each one is exactly as wide as it deserves — down to
    /// the 2-DIP hit floor. HONESTLY: on a long tail in a 240-DIP rail the rarest ticks' shares fall UNDER that floor,
    /// so they are painted at the floor and crowd into their neighbours' 1-DIP gap; the last stretch of the strip reads
    /// as a dense band rather than as separable ticks. That is the prototype's behaviour too, and it is the right
    /// trade: the strip clips its OWN overflow rather than the bar's, so a fifty-descriptor tail never squeezes the five
    /// named slices out of shape, and a descriptor worth one song is exactly what the "more" button exists to make
    /// reachable — at full width, where it is a segment instead of a floor.</para></summary>
    static Element TailTicks(IReadOnlyList<LikedFactsRules.TagShare> tail, float other, CultureInfo culture,
                             in TrackFilterState filters, Action<string> toggleTag)
    {
        var ticks = new Element[tail.Count];
        for (int i = 0; i < tail.Count; i++)
        {
            string title = tail[i].Title;
            bool lit = LikedFactsRules.IsTagLens(filters, title);
            var ink = lit ? Tok.AccentDefault : Tok.TextPrimary with { A = TickAlpha[i % TickAlpha.Length] };
            string tip = Strings.Detail.LikedFacts.ShareTip(title, tail[i].Count,
                                                            tail[i].Fraction.ToString("P0", culture));
            void Click() => toggleTag(title);

            Element fill = new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Fill = ink, HoverFill = Tok.AccentTextPrimary,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                HitTestVisible = false,
            };
            Element target = new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = TickMinWidth,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                OnClick = Click,
                Children = [fill],
            };
            ticks[i] = ToolTip.Wrap(target, tip, grow: tail[i].Count, showDelayMs: LikedLens.TipDelayMs)
                       with { Key = "tick:" + title };
        }

        return new BoxEl
        {
            Key = "seg:tail", Direction = 0, Gap = TickGap,
            Grow = other, Basis = 0f, MinWidth = 0f,
            ClipToBounds = true, HitTestPassThrough = true,
            Children = ticks,
        };
    }

    /// <summary>The legend's last entry: the disclosure. Chevron (ChevronRight → 90° on the Disclosure token) + how many
    /// descriptors the tail holds + what share of the bar they are.
    ///
    /// <para>The LABEL is what flips when the card opens ("44 more" → "Show less"), not a hidden automation string: the
    /// engine has no separate accessible-name channel, so a name that flipped invisibly would be a name that flipped for
    /// nobody. The share stays put on the right through both states, which is also what keeps the legend from reflowing
    /// as the card opens.</para></summary>
    static Element MoreButton(in LikedFactsRules.BlendTail tail, float other, bool isOpen, CultureInfo culture,
                              Action toggle, Func<bool> open)
    {
        int count = tail.Named.Count + tail.MoreTags;
        string label = isOpen ? Loc.Get(Strings.Detail.LikedFacts.ShowLess)
                              : Strings.Detail.LikedFacts.MoreTags(count);
        return new BoxEl
        {
            Key = "leg:more", Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
            Padding = new Edges4(4f, 2f, 4f, 2f), Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = toggle,
            Children =
            [
                // The chevron is its own component (it owns an AnimEngine rotation track and therefore hooks); `open` is
                // a Func so the delegate's signal read happens inside ITS render — props freeze at mount.
                SidebarChevron.Disclosure(open),
                Caption(label) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Caption(other.ToString("P0", culture)) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };
    }

    /// <summary>A legend row — the same fact as its segment, at a size a pointer can actually land on. It carries the
    /// tooltip too (and its own subtle hover plate), because a 2-DIP slice of a 5-slice bar is not a realistic target and
    /// the legend is where a mouse naturally goes.</summary>
    static Element LegendEntry(string key, ColorF ink, string label, string share, string tip, bool lit, Action onClick)
    {
        Element row = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
            // The 4/2 pad is the hover plate's inset AND the row's hit padding, so the rhythm between entries
            // (container gap 4 + 4 + 4) is what it was before the plate existed.
            Padding = new Edges4(4f, 2f, 4f, 2f), Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
            HoverFill = lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = onClick,
            Children =
            [
                new BoxEl { Width = LegendDot, Height = LegendDot, Shrink = 0f, Corners = CornerRadius4.All(2f), Fill = ink },
                Caption(label) with { Color = lit ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Caption(share) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };
        return ToolTip.Wrap(row, tip, showDelayMs: LikedLens.TipDelayMs) with { Key = key };
    }

    // ── The opened body ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the card reveals: a hairline, an eyebrow naming the tail, the tail redrawn at full width, its
    /// legend down to <see cref="TailLegendFloor"/>, and a count of everything under it.
    ///
    /// <para>Every percentage on the full-width bar is "of the tail" and SAYS SO — the bar's own 100 % is the tail, so
    /// quoting library shares on it would put "3 %" on a segment occupying a tenth of the width. The legend rows below
    /// go back to library shares (matching the top five), which is why the two carry different tooltip wordings rather
    /// than one shared one.</para></summary>
    static Element Body(in LikedFactsRules.BlendTail tail, CultureInfo culture, in TrackFilterState filters,
                        Action<string> toggleTag)
    {
        var named = tail.Named;
        var segs = new Element[named.Count];
        for (int i = 0; i < named.Count; i++)
        {
            string title = named[i].Title;
            bool lit = LikedFactsRules.IsTagLens(filters, title);
            var ink = lit ? Tok.AccentDefault : Tok.TextPrimary with { A = TailSegAlpha[i % TailSegAlpha.Length] };
            // Share OF THE TAIL: this bar's whole width is the tail, so its segments have to be quoted against it.
            string tip = Strings.Detail.LikedFacts.TailShareTip(title, named[i].Count,
                (named[i].Count / (float)Math.Max(1, tail.Count)).ToString("P0", culture));
            void Click() => toggleTag(title);
            segs[i] = Segment("tseg:" + title, named[i].Count, ink, Tok.AccentTextPrimary, tip, Click);
        }

        var tailBar = new BoxEl
        {
            Key = "tail-bar", Direction = 0, Height = BlendBarHeight, Gap = BlendSegGap, MinWidth = 0f,
            Corners = CornerRadius4.All(BlendBarRadius), ClipToBounds = true, HitTestPassThrough = true,
            TransformOriginX = 0f,                       // …so the wipe grows FROM the left edge, not from the middle
            Enter = TailBarReveal.Enter, Layout = TailBarReveal,
            Children = segs,
        };

        var (rows, underFloor) = LikedFactsRules.TailSplit(named, TailLegendFloor);
        var legendRows = new List<Element>(rows.Count + 1);
        for (int i = 0; i < rows.Count; i++)
        {
            string title = rows[i].Title;
            bool lit = LikedFactsRules.IsTagLens(filters, title);
            var ink = lit ? Tok.AccentDefault : Tok.TextPrimary with { A = TailSegAlpha[i % TailSegAlpha.Length] };
            string share = rows[i].Fraction.ToString("P0", culture);
            string tip = Strings.Detail.LikedFacts.ShareTip(title, rows[i].Count, share);
            void Click() => toggleTag(title);
            // The Enter terminal lives on a BoxEl wrapper because the legend row itself is a ToolTip component element,
            // and the declarative enter/exit fields bake onto NODES.
            legendRows.Add(new BoxEl
            {
                Key = "tlw:" + title, Direction = 0, Shrink = 0f,
                Animate = TailRowReveal with { DelayMs = WaveeEntrance.DelayMs(i) },
                Children = [LegendEntry("tl:" + title, ink, title, share, tip, lit, Click)],
            });
        }
        if (underFloor > 0)
            legendRows.Add(new BoxEl
            {
                Key = "tl:underfloor", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Padding = new Edges4(4f, 2f, 4f, 2f),
                Animate = TailRowReveal with { DelayMs = WaveeEntrance.DelayMs(rows.Count) },
                Children =
                [
                    Caption(Strings.Detail.LikedFacts.UnderFloor(underFloor)) with
                    { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            });

        return new BoxEl
        {
            Key = "blend-body", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            // The gap that separates the body from the legend above it — INSIDE the body, so it is revealed by the same
            // clip that reveals the hairline (the prototype's `.fact.open .exp{margin-top:8px}`, which is likewise a
            // property of the OPEN state). Hung outside as a flex gap it would be permanent dead air while shut.
            Padding = new Edges4(0f, Spacing.S, 0f, 0f),
            Children =
            [
                // The hairline is the prototype's `border-top` on the eyebrow — a real 1-DIP rule rather than a border,
                // so the gap above and below it stays the column's own rhythm.
                new BoxEl { Key = "tail-rule", Height = 1f, Fill = Tok.StrokeCardDefault, HitTestVisible = false },
                LikedFactsPanel.Head(Strings.Detail.LikedFacts.TailHeader(tail.Named.Count + tail.MoreTags),
                                     Strings.Detail.LikedFacts.TailSongs(tail.Count)),
                tailBar,
                new BoxEl
                {
                    Key = "tail-legend", Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f,
                    Children = legendRows.ToArray(),
                },
            ],
        };
    }
}

/// <summary>Per-frame poller, mounted ONLY while the body's collapse reflow runs: the moment the host's
/// <see cref="SizeMode.Reflow"/> track settles (the AnimEngine reclaims it), flip the mount signal off. The host is
/// already at its declared 0 height by then, so the unmount itself moves nothing.
///
/// <para>REPLICATED, not reused: the kit's own <c>ExpanderCollapseWatcher</c> is <c>internal</c> to
/// <c>FluentGpu.Controls</c> (InternalsVisibleTo names only FluentGpu.VerticalSlice and FluentGpu.Windows.Tests), so it
/// is not reachable from the app. Twenty lines of poller is a smaller cost than widening a kit assembly's internals for
/// one app surface — and the shape is the kit's, so the two stay honest about being the same idiom.</para></summary>
sealed class BlendCollapseWatcher : Component
{
    public required Func<NodeHandle> Host;
    public required Signal<bool> Shown;

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted (only during the ~167 ms reflow)
        UseEffect(() =>
        {
            if (!Shown.Peek()) return;
            var anim = Context.Anim;
            var scene = Context.Scene;
            var node = Host();
            // Settled (the reflow track completed and was reclaimed) — or the node vanished: unmount now.
            if (anim is null || scene is null || node.IsNull || !scene.IsLive(node) || !anim.HasTracks(node))
                Shown.Value = false;
        }, tick);
        return new BoxEl { HitTestVisible = false };
    }
}

/// <summary>The rail facts, seen from the TRACK LIST: what a lens is called, and the header row that says which one is
/// on and lets you take it off again.
///
/// <para>It lives beside the panel that SETS the lenses so the two halves of one feature stay in one file — the panel
/// writes <c>TrackFilterState</c>, this reads it back — and so there is exactly one place the week wording is built.
/// <see cref="RangeParts"/> is called by the sparkline bar's tooltip and by <see cref="Header"/>: a bubble that says
/// "Jul 27 – Aug 3" and a header that says something else would be two bugs wearing one feature.</para>
///
/// <para>The arithmetic behind all of it is <c>LikedFactsRules</c> (pure, tested); this file owns only the localized
/// wording and the elements.</para></summary>
internal static class LikedLens
{
    /// <summary>The facts' tooltips open IMMEDIATELY rather than after <c>ToolTip.MouseShowDelayMs</c> (800ms).
    ///
    /// <para>That delay is right for a toolbar button, whose glyph already says what it does and whose tooltip is a
    /// reminder. It is wrong here: a sparkline column is ~10 DIP wide and carries no label at all, so the bubble IS the
    /// label — and a pointer sweeping the strip leaves each column long before 800ms, which cancels the pending open
    /// (ToolTip.OnLeave) and starts the next one from zero. Twelve bars, twelve restarts, no bubble ever: the strip
    /// read as inert, which is exactly what was reported.</para>
    ///
    /// <para>0, not a small number: the countdown is the frame-driven <c>ToolTipClock</c> (seed on one frame, poll on
    /// the next), so 0 already means "the frame after the pointer arrives" rather than "inside the event". The
    /// per-element override is WinUI's own shape (<c>ToolTipService.InitialShowDelay</c>) and touches only these call
    /// sites — every other tooltip in the app keeps the service delay.</para></summary>
    internal const float TipDelayMs = 0f;

    /// <summary>The lens header's fixed vertical extent (pill height + its bottom gap). A CONSTANT because the vertical
    /// hero layout clips its rows beneath the sticky chrome by a computed inset — a header whose height depended on how
    /// many pills wrapped would desync that cut. Hence the row does not wrap and its pills ellipsise instead.</summary>
    internal const float HeaderExtent = PillHeight + Spacing.S;

    const float PillHeight = 28f;

    /// <summary>The two ends of a saved-date window, formatted in the culture's own month/day pattern. An absent
    /// endpoint prints as an ellipsis rather than as 1970: a half-open window is unreachable from the UI (the rail
    /// always sets both ends), but a filter state is a value anyone can construct and a lie is worse than a gap.</summary>
    internal static (string Start, string End) RangeParts(long afterMs, long beforeMs, CultureInfo culture)
        => (Stamp(afterMs, culture), Stamp(beforeMs, culture));

    static string Stamp(long unixMs, CultureInfo culture)
        => unixMs == 0L ? "\u2026"
         : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("MMM d", culture);

    /// <summary>"Liked Jul 27 – Aug 3" — the week lens, named the same way its bar's tooltip names it.</summary>
    internal static string WeekLabel(long afterMs, long beforeMs, CultureInfo culture)
    {
        var (start, end) = RangeParts(afterMs, beforeMs, culture);
        return Strings.Detail.LikedFacts.LensWeek(Strings.Detail.LikedFacts.WeekRange(start, end));
    }

    /// <summary>The group header the track list shows while a rail fact is lensing it: one pill per active lens, each
    /// with its own clear, and the count of what survived.
    ///
    /// <para>Null when no lens is on — the row is absent, not empty, so an unfiltered list keeps every pixel it had.</para>
    ///
    /// <para>One pill PER lens rather than one sentence, because the lenses are independent facets that genuinely
    /// combine ("this week" ∧ "vaultboy" is a real question). A single "clear" would then throw away a facet the user
    /// did not ask to lose; a per-pill clear retires exactly one.</para>
    ///
    /// <para>The Tag pill appears for a chip click too, and that is deliberate: the blend slice and the chip write the
    /// SAME facet, so a header that appeared for one and not the other would be describing the click rather than the
    /// state.</para></summary>
    internal static Element? Header(in TrackFilterState filter, int visibleCount, DetailHandlers h, CultureInfo culture)
    {
        var lenses = LikedFactsRules.ActiveLenses(filter);
        if (lenses == LikedFactsRules.LikedLens.None) return null;

        var kids = new List<Element>(4);
        if ((lenses & LikedFactsRules.LikedLens.Week) != 0)
            kids.Add(Pill("lens:week", WeekLabel(filter.AddedAfterMs, filter.AddedBeforeMs, culture),
                          LikedFactsRules.LikedLens.Week, h));
        if ((lenses & LikedFactsRules.LikedLens.Artist) != 0)
            // The display name when we have it, the id when we do not — a lens must always be able to say what it is.
            kids.Add(Pill("lens:artist", filter.ArtistName is { Length: > 0 } n ? n : filter.ArtistId ?? "",
                          LikedFactsRules.LikedLens.Artist, h));
        if ((lenses & LikedFactsRules.LikedLens.Tag) != 0)
            kids.Add(Pill("lens:tag", filter.Tag ?? "", LikedFactsRules.LikedLens.Tag, h));

        // The count is the VISIBLE row count, so it answers "and how much is that?" for whatever combination of lenses
        // is on — including zero, which is a real and useful answer ("that week, nothing by this artist").
        kids.Add(Caption(Strings.Detail.SongCount(visibleCount)) with
        {
            Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
        });

        return new BoxEl
        {
            Key = "lens-header", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Height = PillHeight, MinWidth = 0f,
            Margin = new Edges4(0f, 0f, 0f, Spacing.S),
            Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Children = kids.ToArray(),
        };
    }

    /// <summary>One lens, named, with its own clear. The label ellipsises rather than wrapping (see
    /// <see cref="HeaderExtent"/>), and the clear is a separate focusable button so keyboard users can drop one facet
    /// without touching the others.</summary>
    static Element Pill(string key, string label, LikedFactsRules.LikedLens lens, DetailHandlers h)
    {
        void Clear()
        {
            var live = h.Filters?.Peek() ?? TrackFilterState.Default;
            h.SetFilters?.Invoke(LikedFactsRules.ClearLens(live, lens));
        }

        Element close = new BoxEl
        {
            Key = "clear", Width = 22f, Height = 22f, Shrink = 0f, Corners = CornerRadius4.All(11f),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = WaveeMotion.ScaleStandard.Hover, PressScale = WaveeMotion.ScaleStandard.Press,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = Clear,
            Children = [Icon(Icons.ChromeClose, 10f, Tok.TextSecondary)],
        };

        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = 2f,
            Shrink = 1f, MinWidth = 0f, Height = PillHeight,
            Padding = new Edges4(Spacing.M, 0f, 3f, 0f), Corners = CornerRadius4.All(999f),
            Fill = Tok.AccentSubtle, BorderWidth = 1f, BorderColor = Tok.AccentSecondary,
            Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Children =
            [
                new TextEl(label)
                {
                    Size = 12f, Weight = 600, Color = Tok.TextPrimary,
                    Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                ToolTip.Wrap(close, Loc.Get(Strings.Detail.LikedFacts.LensClear), showDelayMs: TipDelayMs),
            ],
        };
    }
}
