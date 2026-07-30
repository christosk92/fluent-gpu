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
// on the left, the omnibar in the centre, and the account/friends/bell/theme cluster on the right. Reads the
// shell route signals (home pill, back/forward-enabled) and PlaybackBridge (auth/user) so it reacts to navigation and login.
//
// RESPONSIVE (mirrors PlayerBar): the right cluster collapses at width thresholds (band-gated via the Viewport signal so
// it only re-renders when a threshold is crossed, not every resize frame) — otherwise the fixed account/icon cluster
// eats the row and the omnibar shrinks to an unusable sliver on a narrow window.
sealed class ShellToolbar : Component
{
    readonly Signal<Route> _route;
    readonly Signal<bool> _canBack;
    readonly Signal<bool> _canForward;
    readonly Action<string, string?> _go;
    readonly Action _back;
    readonly Action _forward;
    readonly Action _home;
    readonly Action _toggleSidebar;
    readonly Signal<string> _searchText;
    readonly Action _toggleTheme;
    readonly List<Route> _backHistory;
    readonly List<Route> _forwardHistory;

    public ShellToolbar(Signal<Route> route, Signal<bool> canBack, Signal<bool> canForward,
                        Action<string, string?> go, Action back, Action forward, Action home,
                        Signal<string> searchText, Action toggleSidebar, Action toggleTheme,
                        List<Route> backHistory, List<Route> forwardHistory)
    {
        _route = route; _canBack = canBack; _canForward = canForward;
        _go = go; _back = back; _forward = forward; _home = home;
        _searchText = searchText; _toggleSidebar = toggleSidebar; _toggleTheme = toggleTheme;
        _backHistory = backHistory; _forwardHistory = forwardHistory;
    }

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var ui = UseContext(ShellUi.Slot);   // the right-rail chrome state — the Friends button toggles the Friends panel
        var viewport = UseContextSignal(Viewport.Size);
        var layout = UseSignal(ToolbarLayout.FromWidth(viewport.Peek().Width));
        // Band-gate: recompute on every viewport move but only push a new value (→ re-render) when a threshold flips.
        UseSignalEffect(() =>
        {
            var next = ToolbarLayout.Resolve(viewport.Value.Width, layout.Peek(), initialized: true);
            if (!next.Equals(layout.Peek())) layout.Value = next;
        });
        return Embed.Comp(() => new ShellToolbarContent(this, layout, b, ui));
    }

    internal static IconButton.Style NavStyle => IconButton.DefaultStyle with { Size = 36f, Height = 32f };

    internal Element Bar(IReadSignal<ToolbarLayout> layout, PlaybackBridge? b, ShellUi? ui)
    {
        ToolbarLayout L = layout.Value;
        bool onHome = _route.Value.Name == "home";        // subscribe (home pill)
        var nav = NavStyle;

        var kids = new List<Element>
        {
            // ── left: sidebar toggle · back · forward · home ────────────────────────────
            // Back/forward use NavHistoryButton so they also support right-click/hold history flyouts.
            // The compact rail is centred at x=28 while the toolbar's 6-DIP inset put this 36-DIP button at x=24.
            // Shift only the hamburger's painted slot four DIPs; the negative trailing margin keeps every later item
            // (back/forward/home/search) at its existing position.
            IconButton.Create(Icons.Menu, _toggleSidebar, nav) with
            {
                Margin = new Edges4(4f, 0f, -4f, 0f),
            },
            Embed.Comp(() => new NavHistoryButton(Icons.Back,    _back,    _canBack,    _backHistory,    _go)),
        };
        if (L.ShowPrimaryNav)
        {
            kids.Add(Embed.Comp(() => new NavHistoryButton(Icons.Forward, _forward, _canForward, _forwardHistory, _go)));
            kids.Add(HomeButton(nav, onHome));
        }

            // ── centre: omnibar, CENTRED in the free space (the stock NavigationView search placement) ──
        kids.Add(new BoxEl
            {
                // Right margin keeps a clear gap between the omnibar and the account cluster — without it the search box's
                // trailing icon butts right up against the profile avatar at narrower widths (reads as overlap).
                Grow = 1f, Basis = 0f, Shrink = 1f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                ClipToBounds = true,
                Padding = new Edges4(8f, 0f, 8f, 0f), Margin = new Edges4(0f, 0f, Spacing.L, 0f),
                Children =
                [
                    // Fills the omnibar slot, capped at 480 (shrinks below that on a narrow window). Live as-you-type
                    // suggestions come from the Omnibar component (online searchSuggestions).
                    Embed.Comp(() => new FluentRichOmnibar(_searchText, _go)),
                ],
            });

            // ── right: account · friends · bell · theme (collapses by threshold) ──
        kids.Add(ProfileChip(b, L.ShowProfileName));
        if (L.ShowFriends) kids.Add(IconButton.Create(Icons.Friends, () => ui?.Toggle(RailMode.Friends), nav));
        if (L.ShowBell) kids.Add(Embed.Comp(() => new NotificationBell()));
        if (L.ShowThemeToggle)
        {
            kids.Add(new BoxEl { Width = 1f, Height = 20f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(4f, 0f, 4f, 0f) });
            kids.Add(IconButton.Create(Theme.Dark ? Icons.Moon : Icons.Sun, _toggleTheme, nav));
        }
        // Overflow: whatever dropped off the bar folds into a "⋯" MenuFlyout so it stays reachable. A plain MenuFlyout
        // (not CommandBarFlyout) gets the clean OverlayHost clip-reveal open — CommandBarFlyout layers its own
        // overflow-expand clip on top, which made the menu pop the empty chrome then fill in (two out-of-sync clips).
        var overflow = OverflowItems(L, ui);
        bool overflowBell = !L.ShowBell;   // when the bell collapses, the notification center folds into the ⋯ menu
        if (overflow.Count > 0 || overflowBell)
            kids.Add(Embed.Comp(() => new OverflowMenu(this, layout, ui)));

        var row = new BoxEl
        {
            // MUX tabbed-window chrome: this row IS the app-body PLATE (LayerOnMicaBaseAlt) — the same material as the
            // nav pane, the player dock and the content-pane backing, so the whole body reads as one continuous plate.
            // The UNPAINTED row is the one ABOVE this: the TAB RAIL in the title bar, where bare Mica Alt shows.
            // BOUND so the host's live re-theme (RethemeAll) re-fires it and follows a palette swap.
            Direction = 0, Height = 48f, AlignItems = FlexAlign.Center, Gap = 4f,
            Padding = new Edges4(6f, 0f, 6f, 0f), Fill = Prop.Of(() => WaveeColors.Toolbar),
            Children = kids.ToArray(),
        };
        return new BoxEl
        {
            ZStack = true,
            Height = 48f,
            Children =
            [
                row,
                // The commanding plate/content handoff needs the same quiet alpha seam as the player dock and tab rail.
                // Keeping it as an overlay preserves the toolbar's full 48-DIP content lane.
                new BoxEl
                {
                    Direction = 1, Justify = FlexJustify.End, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = 1f,
                            Fill = Prop.Of(() => Theme.Dark
                                ? Tok.StrokeDividerDefault
                                : ColorF.FromRgba(0, 0, 0, 0x0F)),
                        },
                    ],
                },
            ],
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
            return Embed.Comp(() => new ProfileMenu(b!, showName));   // avatar chip → account MenuFlyout → modal logout confirm
        if (auth == AuthStatus.Authenticating)
            return new BoxEl { Height = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f), Children = [ Caption(Loc.Get(Strings.Shell.Connecting)).Secondary() ] };
        return Button.Accent(Loc.Get(Strings.Shell.SignIn), () => { _ = b?.Session.ConnectAsync(); });
    }

    // The items currently dropped from the bar (by threshold), as plain MenuFlyout items. They stay reachable here.
    internal List<MenuFlyoutItem> OverflowItems(ToolbarLayout L, ShellUi? ui)
    {
        var items = new List<MenuFlyoutItem>(6);
        if (!L.ShowPrimaryNav)
        {
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Nav.Forward), Icons.Forward,
                Enabled: _canForward.Value, Invoke: _forward));
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Nav.Home), Icons.Home, Invoke: _home));
            items.Add(MenuFlyoutItem.Separator);
        }
        if (!L.ShowFriends) items.Add(new MenuFlyoutItem(Loc.Get(Strings.Shell.Friends), Icons.Friends, Invoke: () => ui?.Toggle(RailMode.Friends)));
        // Notifications, when collapsed, are handled by OverflowMenu (it anchors the panel to the ⋯ button) — not a plain item.
        if (!L.ShowThemeToggle) items.Add(new MenuFlyoutItem(Theme.Dark ? Loc.Get(Strings.Shell.LightTheme) : Loc.Get(Strings.Shell.DarkTheme), Theme.Dark ? Icons.Sun : Icons.Moon, Invoke: _toggleTheme));
        return items;
    }
}

// The reactive body: re-renders when the band-gated layout (or route/auth/back signals read in Bar) changes — but NOT
// on every resize frame, because ToolbarLayout only flips at a threshold. Delegates to the owner's Bar so the build
// logic stays in one place (Bar's signal reads subscribe THIS component's render).
sealed class ShellToolbarContent : Component
{
    readonly ShellToolbar _owner;
    readonly IReadSignal<ToolbarLayout> _layout;
    readonly PlaybackBridge? _b;
    readonly ShellUi? _ui;
    public ShellToolbarContent(ShellToolbar owner, IReadSignal<ToolbarLayout> layout, PlaybackBridge? b, ShellUi? ui)
    { _owner = owner; _layout = layout; _b = b; _ui = ui; }
    public override Element Render() => _owner.Bar(_layout, _b, _ui);
}

// Width thresholds for the right cluster (DIP). Drop least-essential first as the window narrows, so the omnibar keeps
// usable room: friends → profile name → bell → theme. Avatar always stays. Equality-comparable
// (record struct) so the band-gate only re-renders on a real threshold flip.
readonly record struct ToolbarLayout(bool ShowFriends, bool ShowProfileName, bool ShowBell, bool ShowThemeToggle,
    bool ShowPrimaryNav)
{
    public static ToolbarLayout FromWidth(float w) => new(
        ShowFriends:     w >= 1000f,
        ShowProfileName: w >= 900f,
        ShowBell:        w >= 800f,
        ShowThemeToggle: w >= 720f,
        ShowPrimaryNav: !ShellResponsiveLayout.ToolbarNarrowFor(w, current: false, initialized: false));

    public static ToolbarLayout Resolve(float w, ToolbarLayout current, bool initialized) => new(
        ShowFriends:     w >= 1000f,
        ShowProfileName: w >= 900f,
        ShowBell:        w >= 800f,
        ShowThemeToggle: w >= 720f,
        ShowPrimaryNav: !ShellResponsiveLayout.ToolbarNarrowFor(w, !current.ShowPrimaryNav, initialized));
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

    const int HistoryMenuMax = 8;

    public NavHistoryButton(string icon, Action primary, Signal<bool> canDo,
                            List<Route> history, Action<string, string?> go)
    { _icon = icon; _primary = primary; _canDo = canDo; _history = history; _go = go; }

    public override Element Render()
    {
        bool canDo = _canDo.Value;   // subscribe → re-render when enabled state changes
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void OpenFlyout(ContextRequestEventArgs _)
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
                () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return IconButton.Create(_icon, _primary, ShellToolbar.NavStyle, isEnabled: canDo)
            with { OnRealized = h => anchor.Value = h, OnContextRequested = OpenFlyout };
    }
}

// A "⋯" toolbar icon that opens a plain MenuFlyout below it via the overlay service — the same path DropDownButton uses,
// so it gets the engine's clean MenuPopupThemeTransition clip-reveal (NOT CommandBarFlyout's extra overflow-expand clip).
sealed class OverflowMenu : Component
{
    readonly ShellToolbar _owner;
    readonly IReadSignal<ToolbarLayout> _layout;
    readonly ShellUi? _ui;
    public OverflowMenu(ShellToolbar owner, IReadSignal<ToolbarLayout> layout, ShellUi? ui)
    { _owner = owner; _layout = layout; _ui = ui; }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var notifyHandle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var nc = UseContext(NotificationCenterBridge.Slot);

        // When the bell collapses into the overflow, its panel opens anchored to THIS ⋯ button (the same NotificationPanel).
        void OpenNotifications()
        {
            if (nc is null) return;
            notifyHandle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new NotificationPanel()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
            notifyHandle.Value.ClosedAction = () => notifyHandle.Value = null;
            nc.OnPanelOpened();
        }

        List<MenuFlyoutItem> BuildItems()
        {
            ToolbarLayout layout = _layout.Peek();
            var current = _owner.OverflowItems(layout, _ui);
            var list = new List<MenuFlyoutItem>(current.Count + 1);
            if (!layout.ShowBell)
            {
                int unread = nc?.UnreadCount.Peek() ?? 0;
                string label = unread > 0 ? Strings.Notifications.OverflowTitle(unread) : Loc.Get(Strings.Notifications.Title);
                list.Add(new MenuFlyoutItem(label, Icons.Bell, Invoke: OpenNotifications));
            }
            list.AddRange(current);
            return list;
        }

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(BuildItems(), () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return IconButton.Create(Icons.More, Toggle, ShellToolbar.NavStyle) with { OnRealized = h => anchor.Value = h };
    }
}

// The omnibar: an AutoSuggestBox with LIVE as-you-type suggestions (online searchSuggestions via the library seam),
// rendered Spotify-style — a leading search glyph per row + the typed substring brightened/bolded. Empty offline.
sealed class Omnibar : Component
{
    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    // The results are held HERE (not via UseResource, which resets to the seed each keystroke and flashes the popup
    // empty): a new fetch keeps the prior list visible + flips _loading until the fresh set lands — no "No results" flash.
    readonly Signal<IReadOnlyList<string>> _sugg = new(System.Array.Empty<string>());
    readonly Signal<bool> _loading = new(false);
    public Omnibar(Signal<string> text, Action<string, string?> go) { _text = text; _go = go; }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var post = UsePost();                 // marshal the async completion back to the UI thread (signal writes)
        string text = _text.Value.Trim();     // subscribe → the effect re-fires (re-fetches) when the query changes
        UseEffect(() => StartFetch(svc, post, text), text);

        return AutoSuggestBox.Create(System.Array.Empty<string>(), Loc.Get(Strings.Shell.SearchPlaceholder),
            grow: 1f, maxFillWidth: 480f, text: _text, suggestionsSignal: _sugg, loadingSignal: _loading,
            onQuerySubmitted: q => _go("search", string.IsNullOrWhiteSpace(q) ? null : q),
            onSuggestionChosen: q => _go("search", string.IsNullOrWhiteSpace(q) ? null : q),
            minHeight: 32f, cornerRadius: 0f, boldMatch: true, itemGlyph: Icons.Search);
    }

    void StartFetch(Services? svc, Action<Action> post, string q)
    {
        if (q.Length == 0 || svc is null) { _sugg.Value = System.Array.Empty<string>(); _loading.Value = false; return; }
        _loading.Value = true;                // KEEP _sugg (prior results) until the fresh set lands
        _ = Run();

        async System.Threading.Tasks.Task Run()
        {
            try
            {
                var s = await svc.Library.SuggestAsync(q).ConfigureAwait(false);
                post(() => { if (_text.Peek().Trim() == q) { _sugg.Value = s; _loading.Value = false; } });   // ignore stale (the box moved on)
            }
            catch { post(() => { if (_text.Peek().Trim() == q) _loading.Value = false; }); }
        }
    }
}

// Wavee's rich search content hosted by the reusable FluentGpu AutoSuggestBox. The field remains a real control (focus,
// editing, accessibility and popup lifetime); this component supplies only artwork-aware suggestion rows.
sealed class FluentRichOmnibar : Component
{
    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    readonly Signal<SearchSuggestions> _suggestions = new(SearchSuggestions.Empty);
    readonly Signal<bool> _loading = new(false);
    readonly Signal<int> _highlight = new(-1);

    public FluentRichOmnibar(Signal<string> text, Action<string, string?> go) { _text = text; _go = go; }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var post = UsePost();
        string text = _text.Value.Trim();
        UseEffect(() => StartFetch(svc, post, text), text);

        void Submit(string q)
        {
            var trimmed = q.Trim();
            _go("search", trimmed.Length == 0 ? null : trimmed);
        }

        bool InvokeSelection(int selection)
        {
            var suggestions = _suggestions.Peek();
            int queryCount = Math.Min(6, suggestions.Queries.Count);
            int itemCount = Math.Min(10, suggestions.Items.Count);
            if (selection < 0 || selection >= queryCount + itemCount) return false;

            if (selection < queryCount)
            {
                string query = suggestions.Queries[selection];
                _text.Value = query;
                _go("search", query);
                return true;
            }

            var item = suggestions.Items[selection - queryCount];
            switch (item.Kind)
            {
                case SearchSuggestionKind.Track:
                    if (svc is not null) _ = svc.Player.PlayTrackAsync(item.Uri);
                    break;
                case SearchSuggestionKind.Artist: _go("artist:" + item.Uri, item.Title); break;
                case SearchSuggestionKind.Album: _go("album:" + item.Uri, item.Title); break;
                case SearchSuggestionKind.Playlist: _go("pl:" + item.Uri, item.Title); break;
            }
            return true;
        }

        void MoveSelection(int delta)
        {
            var suggestions = _suggestions.Peek();
            int count = Math.Min(6, suggestions.Queries.Count) + Math.Min(10, suggestions.Items.Count);
            if (count == 0) { _highlight.Value = -1; return; }
            int current = _highlight.Peek();
            _highlight.Value = delta > 0
                ? (current + 1 >= count ? -1 : current + 1)
                : (current < 0 ? count - 1 : current - 1);
        }

        var presenter = new AutoSuggestBoxPresenter(
            Build: context => Embed.Comp(() => new OmnibarSuggestionsPopup(
                _text, _suggestions, _loading, context.Width, _highlight,
                selection => { if (InvokeSelection(selection)) context.Close(); },
                close: context.Close)),
            MoveSelection: MoveSelection,
            SubmitSelection: () => InvokeSelection(_highlight.Peek()),
            ResetSelection: () => _highlight.Value = -1);

        // Stock AutoSuggestBox metrics: a 32-DIP field at ControlCornerRadius (cornerRadius 0 resolves to Radii.Control
        // inside the box) with the control-default chrome — no pill, no elevation ring. 480 is the stock search cap.
        return AutoSuggestBox.Create(Array.Empty<string>(), Loc.Get(Strings.Shell.SearchPlaceholder),
            grow: 1f, maxFillWidth: 480f, text: _text, onQuerySubmitted: Submit,
            minHeight: 32f, cornerRadius: 0f, presenter: presenter,
            chrome: AutoSuggestBoxChrome.Standard);
    }

    void StartFetch(Services? svc, Action<Action> post, string q)
    {
        if (q.Length == 0 || svc is null) { _suggestions.Value = SearchSuggestions.Empty; _loading.Value = false; return; }
        _loading.Value = true;
        _ = Run();

        async System.Threading.Tasks.Task Run()
        {
            try
            {
                var s = await svc.Library.SuggestRichAsync(q).ConfigureAwait(false);
                post(() => { if (_text.Peek().Trim() == q) { _suggestions.Value = s; _loading.Value = false; } });
            }
            catch { post(() => { if (_text.Peek().Trim() == q) _loading.Value = false; }); }
        }
    }
}

// Retained for source compatibility with old snapshots; the live toolbar above uses FluentRichOmnibar.
sealed class RichOmnibar : Component
{
    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    readonly Signal<SearchSuggestions> _suggestions = new(SearchSuggestions.Empty);
    readonly Signal<bool> _loading = new(false);
    readonly Signal<float> _fieldWidth = new(0f);
    public RichOmnibar(Signal<string> text, Action<string, string?> go) { _text = text; _go = go; }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var overlay = UseContext(Overlay.Service);
        var post = UsePost();
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        string text = _text.Value.Trim();
        UseEffect(() => StartFetch(svc, post, text), text);
        UseEffect(() =>
        {
            if (text.Length == 0) ClosePopup();
            else OpenPopup();
        }, text);

        void ClosePopup()
        {
            handle.Value?.Close();
            handle.Value = null;
        }

        void OpenPopup()
        {
            if (handle.Value is { IsOpen: true }) return;
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new OmnibarSuggestionsPopup(_text, _suggestions, _loading, _fieldWidth, svc, _go, ClosePopup)),
                FlyoutPlacement.BottomStretch,
                // Real acrylic chrome: scrolling the artwork rows no longer re-blurs the backdrop — the compositor caches
                // the blurred snapshot per overlay and re-blurs only when content BEHIND the popup moves (AcrylicCompositor
                // retained-backdrop cache; design/subsystems/backdrop-effects-animation.md §2.3).
                new PopupOptions(Chrome: PopupChrome.Static));
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        void Submit(string q)
        {
            var trimmed = q.Trim();
            _go("search", trimmed.Length == 0 ? null : trimmed);
            ClosePopup();
        }

        const float fieldHeight = 36f;
        const float iconCol = AutoSuggestBox.QueryButtonWidth + AutoSuggestBox.QueryButtonLeftMargin + AutoSuggestBox.RightButtonMargin;
        float width = _fieldWidth.Value > 0f ? _fieldWidth.Value : 480f;
        var innerWidth = UseComputed(() => MathF.Max(16f, (_fieldWidth.Value > 0f ? _fieldWidth.Value : 480f) - iconCol));
        var editor = Embed.Comp(() => new EditableText
        {
            Text = _text,
            Width = width - iconCol,
            WidthSignal = innerWidth,
            Height = 32f,
            Placeholder = Loc.Get(Strings.Shell.SearchPlaceholder),
            Chromeless = true,
            OnCommit = Submit,
            OnCancel = ClosePopup,
        });

        var queryPlate = new BoxEl
        {
            Grow = 1f, Margin = new Edges4(1, 3, 1, 3),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Role = AutomationRole.Button,
            OnClick = () => Submit(_text.Peek()),
            Children =
            [
                new TextEl(Icons.Search)
                {
                    Size = AutoSuggestBox.IconFontSize, FontFamily = Theme.IconFont,
                    Color = Tok.TextSecondary, PressedColor = Tok.TextTertiary,
                },
            ],
        }.Interactive(Interaction.Subtle);

        return new BoxEl
        {
            Direction = 0,
            Width = float.NaN,
            Grow = 1f,
            Shrink = 1f,
            MaxWidth = 480f,
            Height = fieldHeight,
            MinHeight = fieldHeight,
            MaxHeight = fieldHeight,
            AlignItems = FlexAlign.Center,
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderColor = Tok.StrokeControlDefault,
            Fill = Tok.FillControlDefault,
            HoverFill = Tok.FillControlSecondary,
            PressedFill = Tok.FillControlTertiary,
            ClipToBounds = true,
            Role = AutomationRole.ComboBox,
            OnRealized = h => anchor.Value = h,
            OnBoundsChanged = r =>
            {
                if (r.W > 0f && MathF.Abs(r.W - _fieldWidth.Peek()) > 0.5f) _fieldWidth.Value = r.W;
            },
            Children =
            [
                new BoxEl { Grow = 1f, Basis = 0f, Shrink = 1f, ClipToBounds = true, AlignItems = FlexAlign.Stretch, Children = [editor] },
                new BoxEl
                {
                    Width = AutoSuggestBox.QueryButtonWidth,
                    Height = AutoSuggestBox.QueryButtonHeight,
                    Margin = new Edges4(AutoSuggestBox.QueryButtonLeftMargin, 0, AutoSuggestBox.RightButtonMargin, 0),
                    AlignItems = FlexAlign.Stretch,
                    Children = [queryPlate],
                },
            ],
        };
    }

    void StartFetch(Services? svc, Action<Action> post, string q)
    {
        if (q.Length == 0 || svc is null) { _suggestions.Value = SearchSuggestions.Empty; _loading.Value = false; return; }
        _loading.Value = true;
        _ = Run();

        async System.Threading.Tasks.Task Run()
        {
            try
            {
                var s = await svc.Library.SuggestRichAsync(q).ConfigureAwait(false);
                post(() => { if (_text.Peek().Trim() == q) { _suggestions.Value = s; _loading.Value = false; } });
            }
            catch { post(() => { if (_text.Peek().Trim() == q) _loading.Value = false; }); }
        }
    }
}

sealed class OmnibarSuggestionsPopup : Component
{
    readonly Signal<string> _text;
    readonly IReadSignal<SearchSuggestions> _suggestions;
    readonly IReadSignal<bool> _loading;
    readonly IReadSignal<float> _width;
    readonly Services? _svc;
    readonly Action<string, string?>? _go;
    readonly Action? _close;
    readonly IReadSignal<int>? _highlight;
    readonly Action<int>? _choose;

    public OmnibarSuggestionsPopup(Signal<string> text, IReadSignal<SearchSuggestions> suggestions, IReadSignal<bool> loading,
        IReadSignal<float> width, Services? svc, Action<string, string?> go, Action close)
    {
        _text = text; _suggestions = suggestions; _loading = loading; _width = width; _svc = svc; _go = go; _close = close;
    }

    public OmnibarSuggestionsPopup(Signal<string> text, IReadSignal<SearchSuggestions> suggestions, IReadSignal<bool> loading,
        IReadSignal<float> width, IReadSignal<int> highlight, Action<int> choose, Action? close = null)
    {
        _text = text; _suggestions = suggestions; _loading = loading; _width = width;
        _highlight = highlight; _choose = choose; _close = close;
    }

    public override Element Render()
    {
        string q = _text.Value.Trim();
        var s = _suggestions.Value;
        bool loading = _loading.Value;
        int highlighted = _highlight?.Value ?? -1;
        float width = _width.Value > 0f ? _width.Value : 720f;
        // Live path (FluentRichOmnibar) does not pass Services/go — resolve them from ambient context so row actions
        // (Play / Like / context menu) work the same as the retained RichOmnibar constructor.
        var svc = _svc ?? UseContext(Services.Slot);
        var acts = UseContext(ActionServices.Slot);
        var overlay = UseContext(Overlay.Service);
        var lib = UseContext(LibraryBridge.Slot);

        // No client-side re-filter: the server's fuzzy matching (apostrophes, word order) is authoritative;
        // a literal Contains() check would drop most of its hits. Staleness is handled at publish time.
        var rows = new List<Element>();
        int selectionIndex = 0;
        int queryCount = 0;
        foreach (var query in s.Queries)
        {
            rows.Add(QueryRow(query, q, selectionIndex, highlighted == selectionIndex));
            selectionIndex++;
            if (++queryCount >= 6) break;
        }

        int richCount = 0;
        foreach (var item in s.Items)
        {
            if (richCount == 0 && rows.Count > 0) rows.Add(Divider());
            rows.Add(RichRow(item, selectionIndex, highlighted == selectionIndex, svc, acts, overlay, lib));
            selectionIndex++;
            if (++richCount >= 10) break;
        }

        Element body;
        if (rows.Count == 0)
        {
            body = loading
                ? new BoxEl { Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight }
                : new BoxEl
                {
                    Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(24, 0, 24, 0),
                    Children = [new TextEl(Loc.Get(Strings.Search.NoResults)) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f }],
                };
        }
        else
        {
            body = new ScrollEl
            {
                Width = width,
                MinWidth = width,
                MaxHeight = 560f,
                ContentSized = true,
                Content = new BoxEl
                {
                    Direction = 1,
                    Width = width,
                    MinWidth = width,
                    Margin = new Edges4(-1, 0, -1, 0),
                    Children = rows.ToArray(),
                },
            };
        }

        // PopupChrome.Static supplies the acrylic plate + border + rounded corners + shadow + clip, so return just the
        // content with the 2px vertical breathing room the rows had inside the old plate.
        return new BoxEl
        {
            Direction = 1, Width = width, MinWidth = width, Padding = new Edges4(0, 2, 0, 2),
            Children = loading ? [ProgressBar.Indeterminate(width), body] : [body],
        };
    }

    Element QueryRow(string query, string typed, int selectionIndex, bool selected) => new BoxEl
    {
        MinHeight = AutoSuggestBox.ItemMinHeight,
        AlignItems = FlexAlign.Center,
        Padding = new Edges4(12, 0, 8, 0),
        Margin = new Edges4(4, 2, 4, 2),
        Corners = Radii.ControlAll,
        Role = AutomationRole.MenuItem,
        Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        OnClick = () =>
        {
            if (_choose is not null) { _choose(selectionIndex); return; }
            _text.Value = query;
            _go?.Invoke("search", query);
            _close?.Invoke();
        },
        Children = QueryContent(query, typed),
    };

    Element RichRow(SearchSuggestionItem item, int selectionIndex, bool selected,
                    Services? svc, ActionServices? acts, IOverlayService? overlay, LibraryBridge? lib)
    {
        bool circular = item.Kind == SearchSuggestionKind.Artist;
        float radius = circular ? 22f : 5f;
        bool saved = lib?.IsSaved(item.Uri) ?? false;
        Action play = () => PlayItem(item, svc);
        Action open = () =>
        {
            if (_choose is not null) { _choose(selectionIndex); return; }
            Invoke(item, svc);
        };
        // Trailing cluster: Play · Like · More — always visible (fills the empty gap before the type pill). More raises
        // the same context menu as right-click (ClickRequestsContext → WithContextMenu ancestor).
        var trailing = new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 2f,
            Children =
            [
                IconButton(Icons.Play, play),
                TrackRow.Heart(saved, () => lib?.ToggleSaved(item.Uri, item.Title)),
                MoreButton(acts is not null && overlay is not null),
                TypePill(TypeLabel(item.Kind)),
            ],
        };

        var row = new BoxEl
        {
            Direction = 0,
            Height = 58f,
            AlignItems = FlexAlign.Center,
            Gap = Spacing.M,
            Padding = new Edges4(12, 0, 10, 0),
            Margin = new Edges4(4, 2, 4, 2),
            Corners = Radii.ControlAll,
            Role = AutomationRole.MenuItem,
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            OnClick = open,
            Children =
            [
                new BoxEl
                {
                    Width = 44f, Height = 44f, Shrink = 0f,
                    Corners = CornerRadius4.All(radius), ClipToBounds = true,
                    Children = [Surfaces.Artwork(item.Image, item.Uri.GetHashCode() & 0x7fffffff, 44f, 44f, radius)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
                    Children =
                    [
                        new TextEl(item.Title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(item.Subtitle ?? TypeLabel(item.Kind)) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                trailing,
            ],
        };
        return acts is not null && overlay is not null
            ? row.WithContextMenu(overlay, () => Menus.Card(acts, item.Uri, item.Title))
            : row;
    }

    void PlayItem(SearchSuggestionItem item, Services? svc)
    {
        if (svc is null) return;
        if (item.Kind == SearchSuggestionKind.Track) _ = svc.Player.PlayTrackAsync(item.Uri);
        else _ = svc.Player.PlayAsync(item.Uri, 0);
        _close?.Invoke();
    }

    void Invoke(SearchSuggestionItem item, Services? svc = null)
    {
        svc ??= _svc;
        switch (item.Kind)
        {
            case SearchSuggestionKind.Track:
                if (svc is not null) _ = svc.Player.PlayTrackAsync(item.Uri);
                break;
            case SearchSuggestionKind.Artist:
                _go?.Invoke("artist:" + item.Uri, item.Title);
                break;
            case SearchSuggestionKind.Album:
                _go?.Invoke("album:" + item.Uri, item.Title);
                break;
            case SearchSuggestionKind.Playlist:
                _go?.Invoke("pl:" + item.Uri, item.Title);
                break;
        }
        _close?.Invoke();
    }

    static Element IconButton(string glyph, Action onClick) => new BoxEl
    {
        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f),
        HoverScale = 1.06f, PressScale = 0.94f,
        Cursor = CursorId.Hand, OnClick = onClick, Role = AutomationRole.Button,
        Children = [Icon(glyph, 14f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

    // Always-visible "…" — same ClickRequestsContext contract as TrackRow.MoreButton, without the hover-only fade
    // (omnibar rows are transient; the affordance needs to read at rest).
    static Element MoreButton(bool enabled) => new BoxEl
    {
        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f),
        HoverScale = 1.06f, PressScale = 0.94f,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        ClickRequestsContext = enabled,
        Role = AutomationRole.Button,
        Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

    static Element Divider() => new BoxEl
    {
        Height = 1f,
        Margin = new Edges4(16f, 4f, 16f, 4f),
        Fill = Tok.StrokeDividerDefault,
    };

    static Element TypePill(string type) => new BoxEl
    {
        Shrink = 0f,
        Padding = new Edges4(9f, 2f, 9f, 2f),
        Corners = CornerRadius4.All(10f),
        Fill = Tok.FillSubtleSecondary,
        Children = [new TextEl(type) { Size = 10f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 40f }],
    };

    static string TypeLabel(SearchSuggestionKind kind) => kind switch
    {
        SearchSuggestionKind.Track => Loc.Get(Strings.Search.TypeSong),
        SearchSuggestionKind.Artist => Loc.Get(Strings.Search.TypeArtist),
        SearchSuggestionKind.Album => Loc.Get(Strings.Search.TypeAlbum),
        SearchSuggestionKind.Playlist => Loc.Get(Strings.Search.TypePlaylist),
        _ => "",
    };

    static Element[] QueryContent(string text, string query)
    {
        var kids = new List<Element>(4)
        {
            new TextEl(Icons.Search) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, Margin = new Edges4(0, 0, 12, 0) },
        };

        int mi = query.Length > 0 ? text.IndexOf(query, StringComparison.OrdinalIgnoreCase) : -1;
        if (mi < 0)
        {
            kids.Add(new TextEl(text) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            return kids.ToArray();
        }

        if (mi > 0) kids.Add(Seg(text.Substring(0, mi), false, false));
        kids.Add(Seg(text.Substring(mi, query.Length), true, false));
        int after = mi + query.Length;
        kids.Add(after < text.Length ? Seg(text.Substring(after), false, true) : new BoxEl { Grow = 1f });
        return kids.ToArray();

        static Element Seg(string s, bool match, bool grow) => new TextEl(s)
        {
            Size = 14f,
            Weight = (ushort)(match ? 700 : 400),
            Color = match ? Tok.TextPrimary : Tok.TextSecondary,
            Grow = grow ? 1f : 0f,
            MaxLines = 1,
            Trim = TextTrim.CharacterEllipsis,
        };
    }

}
