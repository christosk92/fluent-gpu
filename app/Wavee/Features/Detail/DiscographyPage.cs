using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Route helpers for the dedicated discography facet page: "disco:{kindInt}:{artistUri}". The uri may itself contain ':'
// (spotify uris), but the kind is a single leading digit, so parsing is unambiguous (kind at [6], uri from [8]).
static class DiscographyRoute
{
    public const int PreviewCap = 50;   // items shown on the artist page before "See all"; the facet page skips past these

    public static string Make(DiscographyKind kind, string artistUri) => $"disco:{(int)kind}:{artistUri}";
    public static bool Is(string name) => name.StartsWith("disco:", StringComparison.Ordinal);

    public static (DiscographyKind Kind, string Uri) Parse(string name)
    {
        int k = name.Length > 6 && char.IsDigit(name[6]) ? name[6] - '0' : 0;
        string uri = name.Length > 8 ? name[8..] : "";
        return ((DiscographyKind)k, uri);
    }

    public static string FacetTitle(DiscographyKind k) => k switch
    {
        DiscographyKind.Albums => "Albums",
        DiscographyKind.Singles => "Singles",
        DiscographyKind.Compilations => "Compilations",
        _ => "Releases",
    };

    public static string FacetWord(DiscographyKind k, int n) => k switch
    {
        DiscographyKind.Albums => n == 1 ? "album" : "albums",
        DiscographyKind.Singles => n == 1 ? "single" : "singles",
        DiscographyKind.Compilations => n == 1 ? "compilation" : "compilations",
        _ => n == 1 ? "release" : "releases",
    };
}

// The full discography facet page: a breadcrumb ("{Artist} › {Facet}") over the WHOLE facet — the uncapped, virtualized
// DiscoGrid in its own scroll view. ONE instance serves successive facet routes (ContentHost collapses the slot); the VC +
// the grid re-key on the route so a different facet reloads cleanly in place.
sealed class DiscographyPage : Component
{
    readonly Signal<Route> _route;
    public DiscographyPage(Signal<Route> route) { _route = route; }

    VirtualCollection<Album>? _vc;
    string _key = "";
    System.Threading.CancellationTokenSource? _cts;   // re-scoped per facet route; cancelled on route change + unmount
    bool _seeded;                                      // one-shot latch (per route): guards the provisional Seed

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        if (svc is null) return new BoxEl { Grow = 1f };

        var route = _route.Value;                    // subscribe → serve successive facet routes in place
        var (kind, uri) = DiscographyRoute.Parse(route.Name);
        string artistName = string.IsNullOrEmpty(route.Arg) ? "Artist" : route.Arg!;
        this.UseSoftReveal(dy: 0f, blur: 0f);

        var post = UsePost();
        if (_vc is null || _key != route.Name)
        {
            // New facet route → cancel the prior route's fetches/probe, re-scope the CTS + seed latch, rebuild the VC.
            _cts?.Cancel(); _cts?.Dispose();
            _cts = new System.Threading.CancellationTokenSource();
            _seeded = false;
            _key = route.Name;
            _vc = DiscoVc.Make(svc, uri, kind, post, _cts.Token);
            SeedProbe(svc, uri, kind, post, _cts);   // probe keyed on the route's uri+kind → shimmer-up-to-N instantly
        }
        // Cancel whatever CTS is current when the page unmounts (signal-free effect → runs once on mount).
        UseSignalEffect(() => Reactive.OnCleanup(() => { _cts?.Cancel(); _cts?.Dispose(); }));

        var pageScroll = UseSignal(0f);
        void Play(string u) => _ = svc.Player.PlayAsync(u, 0);

        // Breadcrumb: clickable artist name → back to the artist page, chevron, then the (non-clickable) facet title.
        Element breadcrumb = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f,
            Children =
            [
                new BoxEl
                {
                    OnClick = () => go("artist:" + uri, artistName),
                    Corners = CornerRadius4.All(6f), HoverFill = Tok.FillSubtleSecondary,
                    Padding = new Edges4(WaveeSpace.XS, WaveeSpace.XS, WaveeSpace.XS, WaveeSpace.XS),
                    Children = [ new TextEl(artistName) { Size = 28f, Weight = 700, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } ],
                },
                Icon(Icons.ChevronRight, 18f, Tok.TextTertiary),
                new TextEl(DiscographyRoute.FacetTitle(kind)) { Size = 28f, Weight = 700, Color = Tok.TextPrimary },
            ],
        };

        var content = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.L,
            Padding = new Edges4(32f, 40f, 32f, PlayerDock.Reserve + 40f),
            Children =
            [
                breadcrumb,
                // cap 0 = the whole facet; initialIndex skips past the items already previewed on the artist page.
                Embed.Comp(() => new DiscoGrid(_vc!, svc, go, Play, cap: 0, initialIndex: DiscographyRoute.PreviewCap)) with { Key = "disco-grid:" + route.Name },
            ],
        };

        var scroll = ScrollView(content) with
        {
            Grow = 1f, ScrollKey = route.Name,
            AutoEdgeFade = true,
            OnScrollGeometryChanged = (g => (long)(g.OffsetY / 24f), g => pageScroll.Value = g.OffsetY),
        };
        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)pageScroll, scroll);
    }

    // Total-only probe (limit 0 → NO network; resolves same-tick from the cached artist). Seeds the VC COUNT ONLY as
    // PROVISIONAL so the whole-facet grid shows shimmer-up-to-N instantly; the first real page reconciles the count. Behind a
    // one-shot latch (Seed bumps Version → an unlatched seed would re-render-loop); a cancelled/failed probe or a non-positive
    // total leaves _seeded clear so a re-nav retries. Captures the route's VC + token so a stale completion after a facet
    // switch is a no-op.
    async void SeedProbe(Services svc, string uri, DiscographyKind kind, Action<Action> post, System.Threading.CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var vc = _vc;
        try
        {
            var p = await svc.Library.GetDiscographyAsync(uri, kind, 0, 0, ct).ConfigureAwait(false);
            int total = p.Total;
            post(() =>
            {
                if (_seeded || ct.IsCancellationRequested || total <= 0 || vc is null || _vc != vc) return;
                _seeded = true;
                vc.Seed(total, default, provisional: true);
            });
        }
        catch { /* OCE (facet switch / nav away) or a failed probe → _seeded stays clear so a re-nav retries */ }
    }
}
