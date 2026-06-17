using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Row 1 — the WaveeMusic navigation toolbar (NavigationToolbar.xaml): a 48px row with sidebar-toggle + back/forward/home
// on the left, the omnibar in the centre, and the account/friends/bell/theme/right-panel cluster on the right. Reads the
// shell route signals (home pill, back-enabled) and PlaybackBridge (auth/user) so it reacts to navigation and login.
//
// RESPONSIVE (mirrors PlayerBar): the right cluster collapses at width thresholds (band-gated via the Viewport signal so
// it only re-renders when a threshold is crossed, not every resize frame) — otherwise the fixed account/icon cluster
// eats the row and the omnibar shrinks to an unusable sliver on a narrow window.
sealed class ShellToolbar : ReactiveComponent
{
    readonly Signal<Route> _route;
    readonly Signal<bool> _canBack;
    readonly Action<string, string?> _go;
    readonly Action _back;
    readonly Action _home;
    readonly Signal<string> _searchText;
    readonly Signal<bool> _sidebarCompact;
    readonly Action _toggleTheme;
    // Live omnibar content width — the search field caps at min(720, this) so it SHRINKS with the window. Basis=0 on the
    // omnibar is what lets it report a width below the field's own size (else a Grow=1 box floors at its content width).
    readonly Signal<float> _omniAvail = new(720f);

    static readonly string[] NoSuggestions = Array.Empty<string>();

    public ShellToolbar(Signal<Route> route, Signal<bool> canBack, Action<string, string?> go, Action back, Action home,
                        Signal<string> searchText, Signal<bool> sidebarCompact, Action toggleTheme)
    {
        _route = route; _canBack = canBack; _go = go; _back = back; _home = home;
        _searchText = searchText; _sidebarCompact = sidebarCompact; _toggleTheme = toggleTheme;
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
        bool canBack = _canBack.Value;                    // subscribe (back enabled)
        var nav = IconButton.DefaultStyle with { Size = 36f, Height = 32f };

        var kids = new List<Element>
        {
            // ── left: sidebar toggle · back · forward · home ────────────────────────────
            IconButton.Create(Icons.Menu, () => _sidebarCompact.Value = !_sidebarCompact.Peek(), nav),
            IconButton.Create(Icons.Back, _back, nav, isEnabled: canBack),
            IconButton.Create(Icons.Forward, () => { }, nav, isEnabled: false),
            HomeButton(nav, onHome),

            // ── centre: omnibar (left-aligned right after Home, like WaveeMusic — not centred) ──
            new BoxEl
            {
                // Basis=0 (CRITICAL): a Grow=1 box defaults to basis=content, so the 720 field would pin the omnibar's
                // min width at 720 and it could never report a width below 720 — the field never shrank (feedback
                // deadlock). Basis=0 sizes the omnibar to its ALLOCATED share so the field tracks the real free space.
                Grow = 1f, Basis = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
                Padding = new Edges4(8f, 0f, 8f, 0f),
                OnBoundsChanged = r => { float avail = MathF.Max(0f, r.W - 16f); if (MathF.Abs(avail - _omniAvail.Peek()) > 0.5f) _omniAvail.Value = avail; },
                Children =
                [
                    AutoSuggestBox.Create(NoSuggestions, "Search songs, artists, albums...",
                        width: 720f, widthSignal: _omniAvail, text: _searchText,
                        onQuerySubmitted: _ => _go("search", null),
                        onSuggestionChosen: _ => _go("search", null),
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
            return new BoxEl { Height = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f), Children = [ Caption("Connecting...").Secondary() ] };
        return Button.Accent("Sign in", () => { _ = b?.Session.ConnectAsync(); });
    }

    // The items currently dropped from the bar (by threshold), as plain MenuFlyout items. They stay reachable here.
    List<MenuFlyoutItem> OverflowItems(ToolbarLayout L)
    {
        var items = new List<MenuFlyoutItem>(3);
        if (!L.ShowFriends) items.Add(new MenuFlyoutItem("Friends", Mdl.Friends, Invoke: () => { }));
        if (!L.ShowBell) items.Add(new MenuFlyoutItem("Notifications", Mdl.Bell, Invoke: () => { }));
        if (!L.ShowThemeToggle) items.Add(new MenuFlyoutItem(Theme.Dark ? "Light theme" : "Dark theme", Theme.Dark ? Mdl.Sun : Mdl.Moon, Invoke: _toggleTheme));
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
