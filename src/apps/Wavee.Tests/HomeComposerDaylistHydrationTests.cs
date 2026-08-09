using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

public class HomeComposerDaylistHydrationTests
{
    const string Uri = "spotify:playlist:37i9dQZF1EP6YuccBxUcC1";

    [Fact]
    public async Task DuplicateOccurrences_FetchOneHeader_RefreshHomeOnce_AndKeepAccounting()
    {
        var source = Feed(Shallow(), duplicateGroup: true);
        var headers = new Dictionary<string, HomePlaylistHeader>(StringComparer.Ordinal);
        int fetches = 0, refreshes = 0;

        var exactCard = Exact();
        var refreshed = Feed(exactCard, duplicateGroup: true);
        var hydrator = new HomeDaylistHydrator(
            uri => headers.TryGetValue(uri, out var h) ? h : null,
            (uri, _) =>
            {
                fetches++;
                headers[uri] = Header();
                return Task.CompletedTask;
            },
            _ =>
            {
                refreshes++;
                return Task.FromResult(refreshed);
            });

        var result = await hydrator.ResolveAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(1, fetches);                  // the URI occurs in two groups + the section ledger
        Assert.Equal(1, refreshes);                // one invalidation/requery, never one per occurrence
        Assert.Equal(source.Groups.Sum(g => g.Cards.Count), result.Groups.Sum(g => g.Cards.Count));
        Assert.Equal(source.Sections!.Sum(s => s.Cards.Count), result.Sections!.Sum(s => s.Cards.Count));
        Assert.All(result.Groups.SelectMany(g => g.Cards), c =>
        {
            Assert.Equal("teen pop mid 2010s friday afternoon", c.Title);
            Assert.Equal(new[] { "teen pop", "mid 2010s", "friday afternoon" }, c.Meta!.Seeds);
            Assert.False(c.Meta.NeedsHydration);
        });
        Assert.Equal("teen pop mid 2010s friday afternoon", Assert.Single(result.Sections!).Cards[0].Title);
    }

    [Fact]
    public async Task ExactResidentHeader_SkipsHeaderNetwork_AndOverlaysWhenHomeRefreshFails()
    {
        var source = Feed(Shallow());
        int fetches = 0, refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header(),
            (_, _) => { fetches++; return Task.CompletedTask; },
            _ =>
            {
                refreshes++;
                return Task.FromException<LiveHomeResult>(new InvalidOperationException("home unavailable"));
            });

        var result = await hydrator.ResolveAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(0, fetches);
        Assert.Equal(1, refreshes);
        var groupCard = Assert.Single(Assert.Single(result.Groups).Cards);
        var sectionCard = Assert.Single(Assert.Single(result.Sections!).Cards);
        Assert.Equal("teen pop mid 2010s friday afternoon", groupCard.Title);
        Assert.Equal(groupCard.Title, sectionCard.Title);
        Assert.Equal("Exact description", groupCard.Subtitle);
        Assert.Equal("Spotify", groupCard.Meta!.OwnerName);
        Assert.Equal(50, groupCard.Meta.TrackCount);
        Assert.False(groupCard.Meta.NeedsHydration);
    }

    [Fact]
    public async Task RepeatedReads_RequeryHomeOnce_AndKeepOverlayingFromTheResidentHeader()
    {
        // The TTL-cached Home body recomposes the daylist as shallow on EVERY read (its `name` still equals
        // daylist_pretitle) while the store keeps the exact header — so a resident hit is the steady state, not the
        // exception. The requery invalidates and refetches UNCACHED and Home is polled on a 60 s timer, so spending one
        // per read pinned Home permanently off the Pathfinder TTL. A resident hit renders the card by itself.
        int fetches = 0, refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header(),
            (_, _) => { fetches++; return Task.CompletedTask; },
            _ => { refreshes++; return Task.FromResult(Feed(Exact())); });

        for (int read = 1; read <= 3; read++)
        {
            var result = await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);

            Assert.Equal(0, fetches);                       // resident header, so never the header network
            Assert.Equal(1, refreshes);                     // one requery for this identity — reads 2 and 3 add none
            var card = Assert.Single(Assert.Single(result.Groups).Cards);
            Assert.Equal("teen pop mid 2010s friday afternoon", card.Title);
            Assert.False(card.Meta!.NeedsHydration);
            Assert.Equal(card.Title, Assert.Single(Assert.Single(result.Sections!).Cards).Title);
        }
    }

    [Fact]
    public async Task NewExactTitleForTheSameUri_EarnsOneMoreRequery_ThenStopsAgain()
    {
        // A daylist retitles through the day, so the bound is per resolved IDENTITY, not per URI and not per read: a
        // genuinely new exact title is worth one more invalidating requery, and repeating that title is worth none.
        string title = "teen pop mid 2010s friday afternoon";
        int refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header() with { Title = title },
            (_, _) => Task.CompletedTask,
            _ => { refreshes++; return Task.FromResult(LiveHomeResult.Empty); });   // empty body ⇒ the raw source stays the basis

        var first = await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(title, Assert.Single(Assert.Single(first.Groups).Cards).Title);
        Assert.Equal(1, refreshes);

        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(1, refreshes);

        title = "indie folk late night";
        var retitled = await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(title, Assert.Single(Assert.Single(retitled.Groups).Cards).Title);
        Assert.Equal(2, refreshes);

        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(2, refreshes);
    }

    [Fact]
    public async Task HeaderThatStillEqualsGenericPretitle_DoesNotReleaseAsExact()
    {
        var source = Feed(Shallow(title: ""));
        int fetches = 0, refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => new HomePlaylistHeader("daylist", null, "Spotify", null, 50),
            (_, _) => { fetches++; return Task.CompletedTask; },
            _ => { refreshes++; return Task.FromResult(source); });

        var result = await hydrator.ResolveAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(1, fetches);                  // resident generic header is rejected; one refetch is tried
        Assert.Equal(0, refreshes);                // still generic after fetch, so no cache churn loop
        Assert.Same(source, result);
    }

    [Fact]
    public async Task HeaderFailure_PreservesRawHome_AndCancellationPropagates()
    {
        var source = Feed(Shallow());
        int refreshes = 0;
        var failing = new HomeDaylistHydrator(
            _ => null,
            (_, _) => Task.FromException(new InvalidOperationException("header unavailable")),
            _ => { refreshes++; return Task.FromResult(source); });

        var fallback = await failing.ResolveAsync(source, TestContext.Current.CancellationToken);
        Assert.Same(source, fallback);
        Assert.Equal(0, refreshes);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new HomeDaylistHydrator(
            _ => null,
            (_, ct) => Task.FromCanceled(ct),
            _ => Task.FromResult(source));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ResolveAsync(source, cts.Token));
    }

    static HomeCard Shallow(string title = "daylist") => new(
        Uri, title, "Generic description", null, HomeCardKind.Playlist,
        Meta: new HomeCardMeta(Format: "daylist", TrackCount: 50,
            Seeds: ["teen pop", "mid 2010s", "friday afternoon"], OwnerName: "Spotify",
            GenericTitle: "daylist", NeedsHydration: true));

    static HomeCard Exact() => Shallow() with
    {
        Title = "teen pop mid 2010s friday afternoon",
        Meta = Shallow().Meta! with { NeedsHydration = false },
    };

    static HomePlaylistHeader Header() => new(
        "teen pop mid 2010s friday afternoon", "Exact description", "Spotify", null, 50);

    static LiveHomeResult Feed(HomeCard card, bool duplicateGroup = false)
    {
        HomeGroup[] groups = duplicateGroup
            ? [new(HomeGroupKind.Hero, null, [card]), new(HomeGroupKind.QuickGrid, "Jump back in", [card])]
            : [new(HomeGroupKind.Hero, null, [card])];
        HomeSection[] sections =
        [
            new("spotify:section:daylist", "Your daylist", null, [card], 1, 1),
        ];
        return new LiveHomeResult(groups, null, "Good afternoon", sections);
    }
}
