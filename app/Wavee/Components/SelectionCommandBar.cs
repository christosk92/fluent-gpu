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
// thumbnails + "N selected" + quick actions + Clear. Renders its own hit-test-passthrough overlay layer (mount it as a
// DIRECT ZStack child so it fills the pane) and measures THAT layer's width — the pane, not the bar — to collapse
// responsively: detailed (thumbs + labeled buttons) → icon-only buttons with tooltips → count + Play + an ellipsis
// flyout holding the remaining actions. Never clips at the window edges.
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
        var overlaySvc = UseContext(Overlay.Service);
        var menuAnchor = UseRef<NodeHandle>(default);
        var menuHandle = UseRef<OverlayHandle?>(null);
        var prevCount = UseRef(0);
        _ = _sel.Version.Value;
        int count = SelectedTrackCount();
        // Only for a MULTI-selection (2+) — a plain single click selects a row constantly during normal browsing and
        // must not summon the batch bar (matches the checkboxes' 2+ auto-show threshold).
        this.UseSoftReveal(key: count >= 2, dy: 14f, blur: 2f);
        // The count label's TextSwap must not pop on top of the bar's own soft-reveal: strip its Enter on the build
        // where the bar first becomes visible (tracked across renders, not derivable from count alone).
        bool barWasVisible = prevCount.Value >= 2;
        prevCount.Value = count;
        int fit = _fit.Value;

        Element bar = count >= 2
            ? BuildBar(svc, lib, overlaySvc, menuAnchor, menuHandle, count, fit, barWasVisible)
            : new BoxEl();

        return new BoxEl
        {
            Direction = 1, Grow = 1f,
            // When the batch bar is absent this component is still mounted as a full-bleed ZStack sibling. Click hit
            // testing already falls through its inert boxes, but wheel routing deliberately starts from the deepest
            // visual node (HitTestAny), so an invisible layout descendant could become a sibling route with no scroll
            // ancestor and swallow the wheel. Remove the entire inactive subtree from input; when visible, keep the
            // root's empty area pass-through while the actual bar/buttons remain interactive.
            HitTestVisible = count >= 2,
            HitTestPassThrough = true,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Padding = new Edges4(WaveeSpace.L, 0f, WaveeSpace.L, _bottomPadding),
            OnBoundsChanged = MeasureFit,
            Children =
            [
                // Fail-safe: even a mis-measured frame clips inside the pane instead of overflowing both edges.
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

    // The overlay layer's width IS the pane width (independent of the bar's content — no feedback loop).
    void MeasureFit(RectF r)
    {
        if (r.W <= 0f) return;
        int next = FitFor(r.W);
        if (next != _fit.Peek()) _fit.Value = next;
    }

    // 0 = detailed (thumbs + labeled buttons) · 1 = thumbs + icon-only buttons · 2 = count + Play + ellipsis menu.
    // fit0 needs the labeled bar's full intrinsic width (~1050px + overlay padding) — below that it would clip
    // inside the failsafe instead of collapsing.
    static int FitFor(float w) => w >= 1080f ? 0 : w >= 520f ? 1 : 2;

    Element BuildBar(Services? svc, LibraryBridge? lib, IOverlayService overlaySvc,
                     Ref<NodeHandle> menuAnchor, Ref<OverlayHandle?> menuHandle, int count, int fit, bool barWasVisible)
    {
        var kids = new List<Element>(12);
        if (fit <= 1)
        {
            var thumbs = BuildThumbs();
            if (thumbs.Length > 0)
                kids.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = thumbs });
        }
        // transitions.dev text-states swap on the count: keyed per count so a selection change remounts JUST the label
        // (old rises+blurs out, new enters from below). A keyed CHILD of the bar's HStack — the reconciler honors keys
        // only in child arrays. On the bar's first visible build the Enter is stripped (the bar itself soft-reveals).
        kids.Add(new BoxEl
        {
            Key = "selcount:" + count,
            Animate = barWasVisible ? MotionRecipes.TextSwap : MotionRecipes.TextSwap with { Enter = default },
            Children = [new TextEl(Strings.Detail.SelectedCount(count)) { Size = 13f, Weight = 600, Color = Tok.TextPrimary }],
        });
        kids.Add(Divider());
        kids.Add(Action(Icons.Play, Loc.Get(Strings.Detail.Play), fit, () => PlaySelected(svc)));
        if (fit <= 1)
        {
            kids.Add(Action(Icons.Next, Loc.Get(Strings.Detail.PlayNext), fit, () => PlayNextSelected(svc)));
            kids.Add(Action(Icons.Queue, Loc.Get(Strings.Detail.AddToEndOfQueue), fit, () => QueueSelected(svc)));
            kids.Add(Action(Icons.Add, Loc.Get(Strings.Detail.AddToPlaylist), fit, () => AddSelectedToPlaylist(lib)));
            kids.Add(Divider());
            kids.Add(Action(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), fit, SelectAllTracks));
        }
        else
        {
            // The remaining actions live behind "…" (WinUI CommandBar overflow) — the bar stays a fixed handful wide.
            kids.Add(ToolTip.Wrap(
                RoundBtn(Icons.More, () => ToggleMenu(svc, lib, overlaySvc, menuAnchor, menuHandle),
                         realized: h => menuAnchor.Value = h),
                Loc.Get(Strings.Common.More)));
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

    void ToggleMenu(Services? svc, LibraryBridge? lib, IOverlayService overlaySvc,
                    Ref<NodeHandle> menuAnchor, Ref<OverlayHandle?> menuHandle)
    {
        if (menuHandle.Value is { IsOpen: true } open) { open.Close(); return; }
        MenuFlyoutItem[] items =
        [
            new(Loc.Get(Strings.Detail.PlayNext), Icons.Next, true, () => PlayNextSelected(svc)),
            new(Loc.Get(Strings.Detail.AddToEndOfQueue), Icons.Queue, true, () => QueueSelected(svc)),
            new(Loc.Get(Strings.Detail.AddToPlaylist), Icons.Add, true, () => AddSelectedToPlaylist(lib)),
            MenuFlyoutItem.Separator,
            new(Loc.Get(Strings.Detail.SelectAll), Icons.Accept, true, SelectAllTracks),
        ];
        menuHandle.Value = overlaySvc.Open(
            () => menuAnchor.Value,
            () => MenuFlyout.Build(items, () => menuHandle.Value?.Close()),
            FlyoutPlacement.TopEdgeAlignedRight, ToolFx.Popup);
        menuHandle.Value.ClosedAction = () => menuHandle.Value = null;
    }

    // ── The batch actions (shared by the labeled/icon buttons and the overflow menu) ────────────────────────────────

    void PlaySelected(Services? svc)
    {
        var s = Sel();
        if (svc is null || s.Count == 0) return;
        _ = svc.Player.PlayTrackAsync(s[0]);
        for (int i = 1; i < s.Count; i++) _ = svc.Player.EnqueueAsync(s[i]);
        _sel.DeselectAll();
    }

    void PlayNextSelected(Services? svc)
    {
        var s = Sel();
        if (svc is null || s.Count == 0) return;
        int n = DetailQueueActions.PlayNext(svc.Player, s, s.Count);
        if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
        _sel.DeselectAll();
    }

    void QueueSelected(Services? svc)
    {
        var s = Sel();
        if (svc is null || s.Count == 0) return;
        int n = DetailQueueActions.AddToEnd(svc.Player, s, s.Count);
        if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
        _sel.DeselectAll();
    }

    void AddSelectedToPlaylist(LibraryBridge? lib)
    {
        var s = Sel();
        if (lib is null || s.Count == 0) return;
        var (_, name) = lib.AddToDefaultPlaylist(s);
        Toasts.Show(Strings.Detail.AddedToPlaylist(name), ToastSeverity.Success);
        _sel.DeselectAll();
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

    static Element RoundBtn(string glyph, Action onClick, Action<NodeHandle>? realized = null) => new BoxEl
    {
        Width = 36f, Height = 36f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(18f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onClick,
        OnRealized = realized,
        Children = [Icon(glyph, 13f, Tok.TextSecondary)],
    };

    Element ClearBtn() => ToolTip.Wrap(RoundBtn(Icons.Cancel, () => _sel.DeselectAll()), Loc.Get(Strings.Detail.ClearSelection));
}
