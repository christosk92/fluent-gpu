using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The floating multi-select command bar: appears over a track list when ≥1 row is selected — overlapping stacked album
// thumbnails + "N selected" + quick actions + Clear. Self-measures its host pane and collapses responsively so it never
// clips at the window edges on narrow layouts.
sealed class SelectionCommandBar : Component
{
    readonly SelectionModel _sel;
    readonly Func<int, Track?> _trackAt;
    readonly float _bottomPadding;
    readonly Signal<int> _fit = new(0);

    public SelectionCommandBar(SelectionModel sel, Func<int, Track?> trackAt, float bottomPadding = WaveeSpace.XL)
    { _sel = sel; _trackAt = trackAt; _bottomPadding = bottomPadding; }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        _ = _sel.Version.Value;
        int count = SelectedTrackCount();
        this.UseSoftReveal(key: count >= 1, dy: 14f, blur: 2f);
        int fit = _fit.Value;

        Element bar = count >= 1
            ? BuildBar(svc, lib, count, fit)
            : new BoxEl();

        return new BoxEl
        {
            Direction = 1, Grow = 1f, HitTestPassThrough = true,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Padding = new Edges4(WaveeSpace.L, 0f, WaveeSpace.L, _bottomPadding),
            OnBoundsChanged = MeasureFit,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, Children = [bar] },
                    ],
                },
            ],
        };
    }

    void MeasureFit(RectF r)
    {
        if (r.W <= 0f) return;
        int next = FitFor(r.W);
        if (next != _fit.Peek()) _fit.Value = next;
    }

    static int FitFor(float w) => w >= 900f ? 0 : w >= 560f ? 1 : w >= 460f ? 2 : 3;

    Element BuildBar(Services? svc, LibraryBridge? lib, int count, int fit)
    {
        var thumbs = fit <= 1 ? BuildThumbs() : Array.Empty<Element>();
        var kids = new List<Element>(12);
        if (thumbs.Length > 0)
            kids.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = thumbs });
        kids.Add(new TextEl(Strings.Detail.SelectedCount(count)) { Size = 13f, Weight = 600, Color = Tok.TextPrimary });
        if (fit < 3) kids.Add(Divider());
        kids.Add(Action(Icons.Play, Loc.Get(Strings.Detail.Play), fit,
            () => { var s = Sel(); if (svc is not null && s.Count > 0) { _ = svc.Player.PlayTrackAsync(s[0]); for (int i = 1; i < s.Count; i++) _ = svc.Player.EnqueueAsync(s[i]); _sel.DeselectAll(); } }));
        kids.Add(Action(Icons.Next, Loc.Get(Strings.Detail.PlayNext), fit,
            () =>
            {
                var s = Sel();
                if (svc is not null && s.Count > 0)
                {
                    int n = DetailQueueActions.PlayNext(svc.Player, s, s.Count);
                    if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
                    _sel.DeselectAll();
                }
            }));
        kids.Add(Action(Icons.Queue, Loc.Get(Strings.Detail.AddToEndOfQueue), fit,
            () =>
            {
                var s = Sel();
                if (svc is not null && s.Count > 0)
                {
                    int n = DetailQueueActions.AddToEnd(svc.Player, s, s.Count);
                    if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
                    _sel.DeselectAll();
                }
            }));
        kids.Add(Action(Icons.Add, Loc.Get(Strings.Detail.AddToPlaylist), fit,
            () =>
            {
                var s = Sel();
                if (lib is not null && s.Count > 0)
                {
                    var (_, name) = lib.AddToDefaultPlaylist(s);
                    Toasts.Show(Strings.Detail.AddedToPlaylist(name), ToastSeverity.Success);
                    _sel.DeselectAll();
                }
            }));
        if (fit < 3)
        {
            kids.Add(Divider());
            kids.Add(Action(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), fit, SelectAllTracks));
        }
        kids.Add(ClearBtn());
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Padding = new Edges4(WaveeSpace.M, WaveeSpace.S, WaveeSpace.S, WaveeSpace.S),
            Corners = CornerRadius4.All(WaveeRadius.Card), Shadow = Elevation.Flyout, ClipToBounds = true,
            Acrylic = Tok.AcrylicFlyout, BorderWidth = 1f, BorderColor = Tok.StrokeFlyoutDefault,
            Children = kids.ToArray(),
        };
    }

    Element[] BuildThumbs()
    {
        var thumbs = new List<Element>(4);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _sel.ItemCount && thumbs.Count < 4; i++)
        {
            if (!_sel.IsSelected(i) || _trackAt(i) is not { } t) continue;
            string key = t.Image?.Url is { Length: > 0 } url ? url : t.Id;
            if (!seen.Add(key)) continue;
            thumbs.Add(Thumb(t, thumbs.Count));
        }
        return thumbs.ToArray();
    }

    int SelectedTrackCount()
    {
        int n = 0;
        for (int i = 0; i < _sel.ItemCount; i++) if (_sel.IsSelected(i) && _trackAt(i) is not null) n++;
        return n;
    }

    void SelectAllTracks()
    {
        int first = -1, last = -1;
        for (int i = 0; i < _sel.ItemCount; i++)
        {
            if (_trackAt(i) is null) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return;
        _sel.DeselectAll();
        _sel.SelectRange(first, last);
    }

    List<Track> Sel()
    {
        var list = new List<Track>();
        for (int i = 0; i < _sel.ItemCount; i++) if (_sel.IsSelected(i) && _trackAt(i) is { } t) list.Add(t);
        return list;
    }

    static Element Thumb(Track t, int i) => new BoxEl
    {
        Width = 32f, Height = 32f, Shrink = 0f, Corners = CornerRadius4.All(6f), ClipToBounds = true,
        Margin = new Edges4(i == 0 ? 0f : -14f, 0f, 0f, 0f),
        BorderWidth = 2f, BorderColor = Tok.FillCardSecondary,
        Children = [Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, 32f, 32f, 6f)],
    };

    static Element Divider() => new BoxEl
    {
        Width = 1f, Height = 22f, Fill = Tok.StrokeDividerDefault,
        Margin = new Edges4(WaveeSpace.XS, 0f, WaveeSpace.XS, 0f),
    };

    Element Action(string glyph, string label, int fit, Action onClick) => fit == 0
        ? ActionBtn(glyph, label, onClick)
        : ToolTip.Wrap(RoundBtn(glyph, onClick), label);

    static Element ActionBtn(string glyph, string label, Action onClick) => new BoxEl
    {
        Direction = 0, Height = 36f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
        Padding = new Edges4(WaveeSpace.M, 0f, WaveeSpace.M, 0f), Corners = CornerRadius4.All(18f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onClick,
        Children = [Icon(glyph, 14f, Tok.TextSecondary), new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextSecondary }],
    };

    static Element RoundBtn(string glyph, Action onClick) => new BoxEl
    {
        Width = 36f, Height = 36f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(18f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onClick,
        Children = [Icon(glyph, 13f, Tok.TextSecondary)],
    };

    Element ClearBtn() => ToolTip.Wrap(RoundBtn(Icons.Cancel, () => _sel.DeselectAll()), Loc.Get(Strings.Detail.ClearSelection));
}
