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

// The floating multi-select command bar: appears over a track list when ≥1 row is selected — overlapping stacked album
// thumbnails + "N selected" + quick actions + Clear. Renders its own hit-test-passthrough overlay layer (mount it as a
// DIRECT ZStack child so it fills the pane) and measures THAT layer's width — the pane, not the bar — to collapse
// responsively: detailed (thumbs + labeled buttons) → icon-only buttons with tooltips → count + Play + an ellipsis
// flyout holding the remaining actions. Never clips at the window edges.
//
// The buttons and the "…" overflow are PROJECTIONS of the same AppAction singletons the right-click menu composes
// (Actions/TrackActions + Menus.TrackRows) — the bar and the context menu provably share definitions. Enablement /
// the Like glyph are LIVE: the predicates read signals inside this render. SelectAll/Clear stay bar-local.
sealed class SelectionCommandBar : Component
{
    readonly SelectionModel _sel;
    readonly Func<int, Track?> _trackAt;
    readonly Func<PlaylistHost?>? _host;    // the hosting playlist (editable detail lists) → Remove-from-playlist in the overflow
    readonly float _bottomPadding;
    readonly Signal<int> _fit = new(0);

    public SelectionCommandBar(SelectionModel sel, Func<int, Track?> trackAt, float bottomPadding = WaveeSpace.XL,
                               Func<PlaylistHost?>? host = null)
    { _sel = sel; _trackAt = trackAt; _bottomPadding = bottomPadding; _host = host; }

    public override Element Render()
    {
        var acts = UseContext(ActionServices.Slot);
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
            ? BuildBar(acts, overlaySvc, menuAnchor, menuHandle, count, fit, barWasVisible)
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

    // The action context for THIS render's selection: evaluated in-render, so every predicate an AppAction reads
    // (saved-state, clipboard, caps) subscribes → the bar re-skins live.
    ActionContext SelectionCtx(ActionServices acts) => new(ActionTarget.ForTracks(Sel(), _host?.Invoke()), acts);

    Element BuildBar(ActionServices? acts, IOverlayService overlaySvc,
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
        if (acts is not null)
        {
            var ctx = SelectionCtx(acts);
            kids.Add(ActionButton(TrackActions.Play, in ctx, fit));
            if (fit <= 1)
            {
                kids.Add(ActionButton(TrackActions.PlayNext, in ctx, fit));
                kids.Add(ActionButton(TrackActions.AddToQueue, in ctx, fit));
                kids.Add(ActionButton(TrackActions.ToggleLike, in ctx, fit));
                kids.Add(Divider());
                kids.Add(Action(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), fit, SelectAllTracks));
            }
            // The remaining actions live behind "…" at EVERY fit now — the same rows the right-click menu composes
            // (Add to playlist ▸, Copy links, Remove from this playlist, …), plus the collapsed primaries at fit 2.
            kids.Add(ToolTip.Wrap(
                RoundBtn(Icons.More, () => ToggleMenu(acts, overlaySvc, menuAnchor, menuHandle, fit),
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

    // ONE AppAction → a bar button in the existing chrome (labeled at fit 0, icon+tooltip at fit 1). Executes then
    // clears the selection (the batch bar contract).
    Element ActionButton(AppAction a, in ActionContext ctx, int fit)
    {
        var c = ctx;
        bool enabled = a.EnabledFor(c);
        var icon = ActionIcons.Resolve(a.IconKey, a.IsChecked?.Invoke(c) ?? false);
        System.Action onClick = enabled ? () => { a.Execute(c); _sel.DeselectAll(); } : static () => { };
        // The batch bar is glyph-only chrome; carry the icon font so custom-font marks (e.g. WaveeIcons Play next /
        // Play after) render in their own face rather than tofu in Segoe.
        return Action(icon.Glyph ?? "", a.Label(c), fit, onClick, icon.Font);
    }

    void ToggleMenu(ActionServices acts, IOverlayService overlaySvc,
                    Ref<NodeHandle> menuAnchor, Ref<OverlayHandle?> menuHandle, int fit)
    {
        if (menuHandle.Value is { IsOpen: true } open) { open.Close(); return; }
        var ctx = SelectionCtx(acts);   // open-time snapshot (menus close on invoke — one-shot is correct)
        var items = new List<MenuFlyoutItem>(12);
        if (fit >= 2)
        {
            // The primaries hidden at the narrowest fit stay one tap away.
            items.Add(WithDeselect(TrackActions.PlayNext.ToMenuItem(ctx)));
            items.Add(WithDeselect(TrackActions.AddToQueue.ToMenuItem(ctx)));
            items.Add(WithDeselect(TrackActions.ToggleLike.ToMenuItem(ctx)));
            items.Add(MenuFlyoutItem.Separator);
        }
        // The SAME rows the right-click track menu composes (Add to playlist ▸, Copy links, Remove from this playlist…).
        foreach (var row in Menus.TrackRows(in ctx, showGoToAlbum: false))
            items.Add(WithDeselect(row));
        items.Add(MenuFlyoutItem.Separator);
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.SelectAll), Icons.Accept, true, SelectAllTracks));
        menuHandle.Value = overlaySvc.Open(
            () => menuAnchor.Value,
            () => MenuFlyout.Create(items, () => menuHandle.Value?.Close()),
            FlyoutPlacement.TopEdgeAlignedRight, ToolFx.Popup);
        menuHandle.Value.ClosedAction = () => menuHandle.Value = null;
    }

    // Batch-bar contract: a top-level command/toggle clears the selection after it runs. SubMenu rows keep their
    // nested invokes untouched (an add-to-playlist keeps the selection — the toast confirms the action instead).
    MenuFlyoutItem WithDeselect(MenuFlyoutItem item)
    {
        if (item.Kind is MenuItemKind.Separator or MenuItemKind.SubMenu || item.Invoke is null) return item;
        var inner = item.Invoke;
        return item with { Invoke = () => { inner(); _sel.DeselectAll(); } };
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

    Element Action(string glyph, string label, int fit, Action onClick, string? font = null) => fit == 0
        ? ActionBtn(glyph, label, onClick, font)
        : ToolTip.Wrap(RoundBtn(glyph, onClick, font: font), label);

    static Element ActionBtn(string glyph, string label, Action onClick, string? font = null) => new BoxEl
    {
        Direction = 0, Height = 36f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
        Padding = new Edges4(WaveeSpace.M, 0f, WaveeSpace.M, 0f), Corners = CornerRadius4.All(18f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onClick,
        Children = [Icon(glyph, 14f, Tok.TextSecondary, family: font), new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextSecondary }],
    };

    static Element RoundBtn(string glyph, Action onClick, Action<NodeHandle>? realized = null, string? font = null) => new BoxEl
    {
        Width = 36f, Height = 36f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(18f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onClick,
        OnRealized = realized,
        Children = [Icon(glyph, 13f, Tok.TextSecondary, family: font)],
    };

    Element ClearBtn() => ToolTip.Wrap(RoundBtn(Icons.Cancel, () => _sel.DeselectAll()), Loc.Get(Strings.Detail.ClearSelection));
}
