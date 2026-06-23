using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Row 1 — the WaveeMusic navigation toolbar (NavigationToolbar.xaml): a 48px row with sidebar-toggle + back/forward/home
// on the left, the omnibar in the centre, and the account/friends/bell/theme/right-panel cluster on the right. Reads the
// shell route signals (home pill, back/forward-enabled) and PlaybackBridge (auth/user) so it reacts to navigation and login.
//
// RESPONSIVE (mirrors PlayerBar): the right cluster collapses at width thresholds (band-gated via the Viewport signal so
// it only re-renders when a threshold is crossed, not every resize frame) — otherwise the fixed account/icon cluster
// eats the row and the omnibar shrinks to an unusable sliver on a narrow window.
sealed class ShellToolbar : ReactiveComponent
{
    readonly Signal<Route> _route;
    readonly Signal<bool> _canBack;
    readonly Signal<bool> _canForward;
    readonly Action<string, string?> _go;
    readonly Action _back;
    readonly Action _forward;
    readonly Action _home;
    readonly Signal<string> _searchText;
    readonly Signal<bool> _sidebarCompact;
    readonly Action _toggleTheme;
    readonly List<Route> _backHistory;
    readonly List<Route> _forwardHistory;
    static readonly string[] NoSuggestions = Array.Empty<string>();

    public ShellToolbar(Signal<Route> route, Signal<bool> canBack, Signal<bool> canForward,
                        Action<string, string?> go, Action back, Action forward, Action home,
                        Signal<string> searchText, Signal<bool> sidebarCompact, Action toggleTheme,
                        List<Route> backHistory, List<Route> forwardHistory)
    {
        _route = route; _canBack = canBack; _canForward = canForward;
        _go = go; _back = back; _forward = forward; _home = home;
        _searchText = searchText; _sidebarCompact = sidebarCompact; _toggleTheme = toggleTheme;
        _backHistory = backHistory; _forwardHistory = forwardHistory;
    }

    public override Element Setup()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var viewport = UseContextSignal(Viewport.Size);
        var layout = UseSignal(ToolbarLayout.FromWidth(viewport.Peek().Width));
        // Band-gate: recompute on every viewport move but only push a new value (→ re-render) when a threshold flips.
        UseSignalEffect(() =>
        {
            var next = ToolbarLayout.FromWidth(viewport.Value.Width);
            if (!next.Equals(layout.Peek())) layout.Value = next;
        });
        return Embed.Comp(() => new ShellToolbarContent(this, layout, b));
    }

    internal Element Bar(ToolbarLayout L, PlaybackBridge? b)
    {
        bool onHome = _route.Value.Name == "home";        // subscribe (home pill)
        var nav = IconButton.DefaultStyle with { Size = 36f, Height = 32f };

        var kids = new List<Element>
        {
            // ── left: sidebar toggle · back · forward · home ────────────────────────────
            // Back/forward use NavHistoryButton so they also support right-click/hold history flyouts.
            IconButton.Create(Icons.Menu, () => _sidebarCompact.Value = !_sidebarCompact.Peek(), nav),
            Embed.Comp(() => new NavHistoryButton(Icons.Back,    _back,    _canBack,    _backHistory,    _go, nav)),
            Embed.Comp(() => new NavHistoryButton(Icons.Forward, _forward, _canForward, _forwardHistory, _go, nav)),
            HomeButton(nav, onHome),

            // ── centre: omnibar (left-aligned right after Home, like WaveeMusic — not centred) ──
            new BoxEl
            {
                Grow = 1f, Basis = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
                Padding = new Edges4(8f, 0f, 8f, 0f),
                Children =
                [
                    // Fills the omnibar slot, capped at 720 (shrinks below that on a narrow window).
                    AutoSuggestBox.Create(NoSuggestions, Loc.Get(Strings.Shell.SearchPlaceholder),
                        grow: 1f, maxFillWidth: 720f, text: _searchText,
                        onQuerySubmitted: q => _go("search", string.IsNullOrWhiteSpace(q) ? null : q),
                        onSuggestionChosen: q => _go("search", string.IsNullOrWhiteSpace(q) ? null : q),
                        minHeight: 36f, cornerRadius: 18f),
                ],
            },

            // ── right: account · friends · bell · theme · right-panel (collapses by threshold) ──
            ProfileChip(b, L.ShowProfileName),
        };
        if (L.ShowFriends) kids.Add(IconButton.Create(Mdl.Friends, () => { }, nav));
        if (L.ShowBell) kids.Add(BellButton(nav));
        if (L.ShowThemeToggle)
        {
            kids.Add(new BoxEl { Width = 1f, Height = 20f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(4f, 0f, 4f, 0f) });
            kids.Add(IconButton.Create(Theme.Dark ? Mdl.Moon : Mdl.Sun, _toggleTheme, nav));
        }
        // Overflow: whatever dropped off the bar folds into a "⋯" MenuFlyout so it stays reachable. A plain MenuFlyout
        // (not CommandBarFlyout) gets the clean OverlayHost clip-reveal open — CommandBarFlyout layers its own
        // overflow-expand clip on top, which made the menu pop the empty chrome then fill in (two out-of-sync clips).
        var overflow = OverflowItems(L);
        if (overflow.Count > 0) kids.Add(Embed.Comp(() => new OverflowMenu(overflow, nav)));
        kids.Add(IconButton.Create(Mdl.SplitView, () => { }, nav));   // always — the right-panel toggle

        return new BoxEl
        {
            // The WaveeMusic AddressBar chrome material (App.Theme.AddressBar.BackgroundBrush = LayerOnMicaBaseAlt,
            // App.xaml:41) — the SAME material as the sidebar and the selected tab, so the top chrome reads as one shell.
            Direction = 0, Height = 48f, AlignItems = FlexAlign.Center, Gap = 4f,
            Padding = new Edges4(6f, 0f, 6f, 0f), Fill = WaveeColors.Toolbar,
            Children = kids.ToArray(),
        };
    }

    Element HomeButton(IconButton.Style nav, bool onHome) => new BoxEl
    {
        Direction = 1, Width = 40f, AlignItems = FlexAlign.Center, Gap = 2f,
        Children =
        [
            IconButton.Create(Icons.Home, _home, nav),
            new BoxEl { Width = 16f, Height = 3f, Corners = CornerRadius4.All(2f), Fill = onHome ? Tok.AccentDefault : ColorF.Transparent },
        ],
    };

    Element ProfileChip(PlaybackBridge? b, bool showName)
    {
        var auth = b?.Auth.Value ?? AuthStatus.LoggedOut;   // subscribe
        if (auth == AuthStatus.Authenticated)
        {
            var name = b?.User.Value?.DisplayName ?? "";    // subscribe
            var pic = PersonPicture.Create("", 24f, displayName: name);
            return new BoxEl
            {
                Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Height = 32f,
                Padding = new Edges4(4f, 0f, showName ? 10f : 4f, 0f), Corners = CornerRadius4.All(WaveeRadius.Control),
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = () => { },
                Children = showName ? new Element[] { pic, Caption(name).Primary() } : new Element[] { pic },
            };
        }
        if (auth == AuthStatus.Authenticating)
            return new BoxEl { Height = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f), Children = [ Caption(Loc.Get(Strings.Shell.Connecting)).Secondary() ] };
        return Button.Accent(Loc.Get(Strings.Shell.SignIn), () => { _ = b?.Session.ConnectAsync(); });
    }

    // The items currently dropped from the bar (by threshold), as plain MenuFlyout items. They stay reachable here.
    List<MenuFlyoutItem> OverflowItems(ToolbarLayout L)
    {
        var items = new List<MenuFlyoutItem>(3);
        if (!L.ShowFriends) items.Add(new MenuFlyoutItem(Loc.Get(Strings.Shell.Friends), Mdl.Friends, Invoke: () => { }));
        if (!L.ShowBell) items.Add(new MenuFlyoutItem(Loc.Get(Strings.Shell.Notifications), Mdl.Bell, Invoke: () => { }));
        if (!L.ShowThemeToggle) items.Add(new MenuFlyoutItem(Theme.Dark ? Loc.Get(Strings.Shell.LightTheme) : Loc.Get(Strings.Shell.DarkTheme), Theme.Dark ? Mdl.Sun : Mdl.Moon, Invoke: _toggleTheme));
        return items;
    }

    Element BellButton(IconButton.Style nav) => new BoxEl
    {
        ZStack = true, Width = 36f, Height = 32f,
        Children =
        [
            IconButton.Create(Mdl.Bell, () => { }, nav),
            // full-size overlay so the unread pill floats at the top-right of the button
            new BoxEl
            {
                Width = 36f, Height = 32f, Direction = 1, Justify = FlexJustify.Start, HitTestVisible = false,
                Children = [ new BoxEl { Direction = 0, Justify = FlexJustify.End, Children = [ InfoBadge.Count(1) ] } ],
            },
        ],
    };
}

// The reactive body: re-renders when the band-gated layout (or route/auth/back signals read in Bar) changes — but NOT
// on every resize frame, because ToolbarLayout only flips at a threshold. Delegates to the owner's Bar so the build
// logic stays in one place (Bar's signal reads subscribe THIS component's render).
sealed class ShellToolbarContent : Component
{
    readonly ShellToolbar _owner;
    readonly IReadSignal<ToolbarLayout> _layout;
    readonly PlaybackBridge? _b;
    public ShellToolbarContent(ShellToolbar owner, IReadSignal<ToolbarLayout> layout, PlaybackBridge? b)
    { _owner = owner; _layout = layout; _b = b; }
    public override Element Render() => _owner.Bar(_layout.Value, _b);
}

// Width thresholds for the right cluster (DIP). Drop least-essential first as the window narrows, so the omnibar keeps
// usable room: friends → profile name → bell → theme. Avatar + the right-panel toggle always stay. Equality-comparable
// (record struct) so the band-gate only re-renders on a real threshold flip.
readonly record struct ToolbarLayout(bool ShowFriends, bool ShowProfileName, bool ShowBell, bool ShowThemeToggle)
{
    public static ToolbarLayout FromWidth(float w) => new(
        ShowFriends:     w >= 1000f,
        ShowProfileName: w >= 900f,
        ShowBell:        w >= 800f,
        ShowThemeToggle: w >= 720f);
}

// A toolbar nav button (Back or Forward) that fires its primary action on click and opens a history flyout on
// right-click or touch-hold (OnContextRequested). Shows the most recent HistoryMenuMax routes from the supplied
// list (most recent at top), plus a "View all history" item when the list exceeds the cap. Each item navigates
// via Go so back/forward state is rebuilt naturally (Go clears forward, then the user can go back to any item).
sealed class NavHistoryButton : Component
{
    readonly string _icon;
    readonly Action _primary;
    readonly Signal<bool> _canDo;
    readonly List<Route> _history;   // live reference — read at flyout-open time, not mount time
    readonly Action<string, string?> _go;
    readonly IconButton.Style _style;

    const int HistoryMenuMax = 8;

    public NavHistoryButton(string icon, Action primary, Signal<bool> canDo,
                            List<Route> history, Action<string, string?> go, IconButton.Style style)
    { _icon = icon; _primary = primary; _canDo = canDo; _history = history; _go = go; _style = style; }

    public override Element Render()
    {
        bool canDo = _canDo.Value;   // subscribe → re-render when enabled state changes
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void OpenFlyout(Point2 _)
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            if (_history.Count == 0) return;

            int count = Math.Min(_history.Count, HistoryMenuMax);
            bool hasMore = _history.Count > HistoryMenuMax;
            var items = new MenuFlyoutItem[count + (hasMore ? 2 : 0)];
            int idx = 0;
            for (int i = _history.Count - 1; i >= _history.Count - count; i--)
            {
                var r = _history[i];
                var (title, glyph) = ShellNav.Dest(r);
                items[idx++] = new MenuFlyoutItem(title, glyph, Invoke: () => _go(r.Name, r.Arg));
            }
            if (hasMore)
            {
                items[idx++] = MenuFlyoutItem.Separator;
                items[idx]   = new MenuFlyoutItem(Loc.Get(Strings.Nav.ViewAllHistory), Icons.Clock, Invoke: () => _go("history", null));
            }

            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return IconButton.Create(_icon, _primary, _style, isEnabled: canDo)
            with { OnRealized = h => anchor.Value = h, OnContextRequested = OpenFlyout };
    }
}

// A "⋯" toolbar icon that opens a plain MenuFlyout below it via the overlay service — the same path DropDownButton uses,
// so it gets the engine's clean MenuPopupThemeTransition clip-reveal (NOT CommandBarFlyout's extra overflow-expand clip).
sealed class OverflowMenu : Component
{
    readonly IReadOnlyList<MenuFlyoutItem> _items;
    readonly IconButton.Style _style;
    public OverflowMenu(IReadOnlyList<MenuFlyoutItem> items, IconButton.Style style) { _items = items; _style = style; }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(_items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return IconButton.Create(Icons.More, Toggle, _style) with { OnRealized = h => anchor.Value = h };
    }
}
