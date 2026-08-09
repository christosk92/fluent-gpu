using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The what's-new timeline ──────────────────────────────────────────────────────────────────────────────────────────
// New releases from followed artists, grouped by day. A FIXED row on Home for the same reason as the artist podium: the
// data is the notification feed, not the home feed.
//
// It costs no new request and no new subscription. NotificationCenterBridge is activated once at the app root
// (WaveeApp.cs) and already owns the what's-new fetch, the unread diff against the persisted last-seen keys, and a signal
// that republishes on the UI thread — so reading Items here is a subscribe, not a second feed. Deliberately NOT calling
// EnsureFresh: go-live already primed it, and re-priming per Home mount would refetch on every navigation back.
sealed class HomeTimeline : Component
{
    // `.tlday { grid-template-columns: 96px minmax(0,1fr) }` — the day column is fixed so every rule lines up down the
    // module, which is what makes the pips read as one chronology.
    const float DayColumn = 96f;
    const int MaxRows = 8;

    public override Element Render()
    {
        var nc = UseContext(NotificationCenterBridge.Slot);
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        if (nc is null || svc is null) return new BoxEl();

        var items = nc.Items.Value;          // subscribe: a landed feed re-renders exactly this module
        int unread = nc.UnreadCount.Value;
        var releases = new List<NewReleaseNotification>(MaxRows);
        int total = 0;
        for (int i = 0; i < items.Count; i++)
            if (items[i] is NewReleaseNotification r)
            {
                total++;
                if (releases.Count < MaxRows) releases.Add(r);
            }
        if (releases.Count == 0) return new BoxEl();

        // Day groups against LOCAL midnight — the feed's timestamps are UTC epoch ms, and bucketing them without the
        // local-midnight conversion puts an evening release under "yesterday" for anyone east of UTC.
        var groups = new List<Element>(4);
        var rows = new List<Element>(MaxRows);
        long lastDay = long.MinValue;

        void Flush()
        {
            if (rows.Count == 0) return;
            groups.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.M, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Children =
                [
                    // `.when` — right-aligned, with the relative form beneath it in a lighter tier.
                    new BoxEl
                    {
                        Width = DayColumn, Shrink = 0f, Direction = 1, Gap = 0f,
                        AlignItems = FlexAlign.End, Padding = new Edges4(0f, Spacing.M, 0f, 0f),
                        Children = [.. DayLabel(lastDay)],
                    },
                    // `.tlrows` carries the rule the pips straddle.
                    new BoxEl
                    {
                        Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, MinWidth = 0f,
                                Children =
                                [
                                    new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                                    new BoxEl { Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [.. rows] },
                                ],
                            },
                        ],
                    },
                ],
            });
            rows = new List<Element>(MaxRows);
        }

        foreach (var release in releases)
        {
            long day = DayOf(release.Timestamp);
            if (rows.Count > 0 && day != lastDay) Flush();
            lastDay = day;
            var r = release;
            var row = HomeCards.TimelineRow(r, KindLabel(r), MetaLine(r), () => Navigate(r, go));
            rows.Add(row is BoxEl b ? b with { Key = "home-timeline:" + r.Id } : row);
        }
        Flush();

        Element module = new BoxEl
        {
            Direction = 1, Gap = HomeModuleLayout.HeadGap, MinWidth = 0f,
            Children =
            [
                Surfaces.SectionHeader(Loc.Get(Strings.Home.NewReleases),
                    unread > 0 ? Strings.Home.UnheardOf(unread, total) : null,
                    unread > 0 ? InfoBadge.Count(unread) : null),
                new BoxEl { Direction = 1, Gap = 0f, MinWidth = 0f, Children = [.. groups] },
            ],
        };
        return Responsive.Of(width => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, HomeModuleLayout.Gap(width)),
            Children = [module],
        }, fallback: HomeModuleLayout.FallbackWidth);
    }

    // "Release" / "Episode" — the server's coarse kind, not a guess from the title.
    static string KindLabel(NewReleaseNotification n)
        => Loc.Get(n.Kind == NewReleaseKind.Episode ? Strings.Podcast.Show : Strings.Detail.FactReleases);

    // The creator, plus a duration when the feed carried one (episodes do; albums do not).
    static string MetaLine(NewReleaseNotification n) => n.CreatorName;

    static void Navigate(NewReleaseNotification r, Action<string, string?>? go)
    {
        if (go is null) return;
        // Albums route to the album page, through the same uri→route resolver the rest of the app uses.
        if (r.Kind != NewReleaseKind.Episode)
        {
            if (RichText.RouteForUri(r.Uri) is { } route) go(route, r.Name);
            return;
        }
        // An EPISODE has no route of its own, and the what's-new feed carries only the episode's own
        // `spotify:episode:` uri — NewReleaseNotification has no parent-show field and the payload
        // SpotifyWhatsNewService.ParseEpisode reads takes only the show's NAME out of `podcastV2`. So "show:" + that uri
        // was a route the store cannot satisfy (it fetches `spotify:show:` only) and the destination rendered
        // DetailModel.Empty — a click that opened a blank page. Until an episode route exists, fall back to the web
        // player, which is exactly what NotificationPanel.ClickRelease does for this very notification.
        if (SpotifyLink.WebUrl(r.Uri) is { } web) LoginView.OpenUrl(web);
    }

    static long DayOf(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().Date.Ticks;

    // Today / Yesterday get the word alone; anything older gets the weekday+date with the relative form under it, which
    // is what lets the reader scan the column without doing arithmetic.
    static IEnumerable<Element> DayLabel(long dayTicks)
    {
        var day = new DateTime(dayTicks, DateTimeKind.Local);
        var today = DateTime.Now.Date;
        int delta = (int)(today - day).TotalDays;
        var c = System.Globalization.CultureInfo.CurrentCulture;

        string head = delta <= 0 ? Loc.Get(Strings.Detail.Today)
                    : delta == 1 ? Loc.Get(Strings.Detail.Yesterday)
                    : day.ToString("ddd d MMM", c);
        yield return Caption(head) with
        {
            Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        if (delta > 1)
            yield return Caption(Strings.Detail.DaysAgo(delta)) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
    }
}
