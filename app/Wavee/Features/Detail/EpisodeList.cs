using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The right-column content for a podcast SHOW on the shared detail surface (the episodes analogue of TrackList). Same
// ctor shape as TrackList so DetailShell swaps between them by DetailConfig.Content. Owns: a status filter
// (All/Unplayed/In-progress/Played) + a Newest/Oldest order (SelectorBars), a "Listen next" resume banner promoting the
// in-progress episode, and the episode list. Episodes play through the shared handler (Play(originalIndex) → the show
// context resolves the episode), so no new wiring. The model is Ready when this mounts (the shell mounts on Ready).
sealed class EpisodeList : Component
{
    readonly Signal<Route> _route;
    readonly Loadable<DetailModel> _full;
    readonly PlaybackBridge? _bridge;
    readonly DetailHandlers _h;
    readonly bool _showToolbar;
    readonly Signal<int> _status = new(0);   // 0 All · 1 Unplayed · 2 In progress · 3 Played
    readonly Signal<int> _order = new(0);    // 0 Newest · 1 Oldest

    public EpisodeList(Signal<Route> route, Loadable<DetailModel> full, PlaybackBridge? bridge, DetailHandlers h, bool showToolbar = true)
    { _route = route; _full = full; _bridge = bridge; _h = h; _showToolbar = showToolbar; }

    static float Pct(Episode e) => e.DurationMs > 0 ? Math.Clamp(e.ProgressMs / (float)e.DurationMs, 0f, 1f) : 0f;
    static bool InProgress(Episode e) { float p = Pct(e); return p > 0.01f && p < 0.98f; }
    static bool Played(Episode e) => Pct(e) >= 0.98f;

    public override Element Render()
    {
        var m = _full.Value.Value;                 // subscribe → episodes appear on load
        var eps = m.Episodes ?? Array.Empty<Episode>();
        int status = _status.Value;                // subscribe
        int order = _order.Value;                  // subscribe

        // Filtered + ordered ORIGINAL indices (so Play uses the show-context index regardless of view).
        var view = new List<int>(eps.Count);
        for (int i = 0; i < eps.Count; i++)
        {
            var e = eps[i];
            bool match = status switch { 1 => Pct(e) <= 0.01f, 2 => InProgress(e), 3 => Played(e), _ => true };
            if (match) view.Add(i);
        }
        if (order == 1) view.Reverse();            // Oldest first (episodes are newest-first by default)

        var children = new List<Element>(view.Count + 4);
        if (_showToolbar) children.Add(Toolbar(status, order));

        // "Listen next" resume banner — the most-progressed in-progress episode.
        int resume = -1; float best = 0f;
        for (int i = 0; i < eps.Count; i++)
            if (InProgress(eps[i]) && Pct(eps[i]) > best) { best = Pct(eps[i]); resume = i; }
        if (resume >= 0) { int ri = resume; children.Add(ResumeBanner(eps[resume], () => _h.Play(ri))); }

        children.Add(WaveeType.RailHeader(Loc.Get(Strings.Podcast.Episodes)));
        if (view.Count == 0)
            children.Add(new BoxEl { Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.XL),
                Children = [new TextEl(Loc.Get(Strings.Podcast.NoEpisodes)) { Size = 14f, Color = Tok.TextTertiary }] });
        else foreach (int oi in view) { int idx = oi; children.Add(EpisodeRow(eps[oi], () => _h.Play(idx))); }

        var body = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.M,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, PlayerDock.Reserve + WaveeSpace.XXL),
            Children = children.ToArray(),
        };
        return ScrollView(body) with { Grow = 1f };
    }

    Element Toolbar(int status, int order) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Margin = new Edges4(0f, 0f, 0f, WaveeSpace.XS),
        Children =
        [
            SelectorBar.Create(StatusLabels(), _status),
            new BoxEl { Grow = 1f },
            SelectorBar.Create(OrderLabels(), _order),
        ],
    };

    static string[] StatusLabels() =>
    [
        Loc.Get(Strings.Podcast.Filter.All), Loc.Get(Strings.Podcast.Filter.Unplayed),
        Loc.Get(Strings.Podcast.Filter.InProgress), Loc.Get(Strings.Podcast.Filter.Played),
    ];
    static string[] OrderLabels() => [Loc.Get(Strings.Podcast.Sort.Newest), Loc.Get(Strings.Podcast.Sort.Oldest)];

    static Element ResumeBanner(Episode e, Action resume) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.S,
        Children =
        [
            WaveeType.RailHeader(Loc.Get(Strings.Podcast.ListenNext)),
            new BoxEl
            {
                Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
                Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
                Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Width = 72f, Height = 72f, Shrink = 0f, Corners = CornerRadius4.All(8f), ClipToBounds = true,
                        Children = [Surfaces.Artwork(e.Image, e.Id.GetHashCode() & 0x7fffffff, 72f, 72f, 8f)] },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.XS,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Podcast.ContinueListening)) { Size = 10f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 80f },
                            new TextEl(e.Title) { Size = 15f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl($"{e.PublishedAt:MMM d} · {e.DurationMs / 60000} min") { Size = 12f, Color = Tok.TextSecondary },
                            ProgressBar(Pct(e)),
                        ],
                    },
                    ResumePill(resume),
                ],
            },
        ],
    };

    static Element EpisodeRow(Episode e, Action play)
    {
        float pct = Pct(e);
        var meta = new List<Element>(5)
        {
            new TextEl(e.PublishedAt.ToString("MMM d")) { Size = 12f, Color = Tok.TextTertiary },
            Dot(),
            new TextEl($"{e.DurationMs / 60000} min") { Size = 12f, Color = Tok.TextTertiary },
        };
        if (pct > 0.01f && pct < 0.98f)
        {
            meta.Add(Dot());
            meta.Add(new TextEl(Loc.Get(Strings.Podcast.InProgress)) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary });
        }
        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.S,
            Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card), HoverFill = Tok.FillSubtleSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Width = 56f, Height = 56f, Shrink = 0f, Corners = CornerRadius4.All(8f), ClipToBounds = true,
                            Children = [Surfaces.Artwork(e.Image, e.Id.GetHashCode() & 0x7fffffff, 56f, 56f, 8f)] },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, Gap = 4f,
                            Children =
                            [
                                new TextEl(e.Title) { Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                                new TextEl(e.Description ?? "") { Size = 12f, Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                            ],
                        },
                        PlayCircle(play),
                    ],
                },
                new BoxEl { Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Children = meta.ToArray() },
                pct > 0.01f ? ProgressBar(pct) : new BoxEl { Height = 0f },
            ],
        };
    }

    static Element Dot() => new TextEl("·") { Size = 12f, Color = Tok.TextTertiary };

    static Element ProgressBar(float pct) => new BoxEl
    {
        Direction = 0, Height = 3f, Corners = CornerRadius4.All(2f), Fill = Tok.FillSubtleTertiary, ClipToBounds = true,
        Children =
        [
            new BoxEl { Grow = Math.Max(0.001f, pct), Fill = Tok.AccentDefault },
            new BoxEl { Grow = Math.Max(0.001f, 1f - pct) },
        ],
    };

    static Element PlayCircle(Action play) => new BoxEl
    {
        Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), Fill = Tok.AccentDefault,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shadow = Elevation.Card,
        HoverScale = 1.06f, PressScale = 0.94f, OnClick = play,
        Children = [Icon(Icons.Play, 15f, Tok.TextOnAccentPrimary)],
    };

    static Element ResumePill(Action resume) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Shrink = 0f,
        Corners = CornerRadius4.All(18f), Padding = new Edges4(WaveeSpace.L, 8f, WaveeSpace.L, 8f),
        Fill = Tok.AccentDefault, HoverScale = 1.04f, PressScale = 0.96f, OnClick = resume,
        Children =
        [
            Icon(Icons.Play, 13f, Tok.TextOnAccentPrimary),
            new TextEl(Loc.Get(Strings.Podcast.Resume)) { Size = 13f, Weight = 700, Color = Tok.TextOnAccentPrimary },
        ],
    };
}
