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
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Row 1 — the WaveeMusic navigation toolbar (NavigationToolbar.xaml): a 48px row with sidebar-toggle + back/forward +
// the CUSTOMIZABLE shortcut band on the left, the omnibar in the centre, and the account/friends/bell/theme cluster on
// the right. Reads the shell route signals (selection mark, back/forward-enabled) and PlaybackBridge (auth/user) so it
// reacts to navigation and login.
//
// WHAT IS FIXED AND WHAT IS NOT. The hamburger, back, forward, the centred omnibar and the right-side chips are FIXED
// chrome — not user-editable. The slots between forward and the omnibar are the SHORTCUT BAND, rendered from
// SidebarPreferences.TopBar (one global list on the sidebar-layout document, so it shares that document's reducer, undo
// ring, autosave and rejection contract). Its default is exactly the single Home button this file used to hard-code, so
// an untouched install is a zero-pixel diff; an emptied band renders nothing, because Home is genuinely removable.
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
    readonly FloatSignal _leftChromeWidth = new();
    readonly FloatSignal _rightChromeWidth = new();

    // The shortcut band's services. PLAIN FIELDS refreshed on every Render (the mode-component pattern): these are
    // reference-stable singletons, and Bar()/OverflowItems() run inside the CHILD's render — a ctor arg would freeze at
    // mount, which is the whole hazard the component-props contract warns about.
    SidebarPreferences? _prefs;
    ActionServices? _acts;
    WaveeExtensionRegistry? _registry;
    IOverlayService? _menuOverlay;

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
        _prefs = UseContext(SidebarPreferences.Slot);
        _acts = UseContext(ActionServices.Slot);
        _menuOverlay = UseContext(Overlay.Service);
        // The registry is the ONE lookup path for a bound Action shortcut (never AppActions.All — the extension-platform
        // guardrail). Context first, then the action bag, so a host that provides only one of them still resolves.
        _registry = UseContext(WaveeExtensionRegistry.Slot) ?? _acts?.Extensions;
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
        string sel = _route.Value.Name;                  // subscribe (the selection mark under the active shortcut)
        var nav = NavStyle;

        var leftKids = new List<Element>
        {
            // ── left: sidebar toggle · back · forward · the customizable shortcut band ──────────
            // Back/forward use NavHistoryButton so they also support right-click/hold history flyouts.
            // The compact rail is centred at x=28 while the toolbar's 6-DIP inset put this 36-DIP button at x=24.
            // Shift only the hamburger's painted slot four DIPs; the negative trailing margin keeps every later item
            // (back/forward/the band/search) at its existing position.
            IconButton.Create(Icons.Menu, _toggleSidebar, nav) with
            {
                Margin = new Edges4(4f, 0f, -4f, 0f),
            },
            Embed.Comp(() => new NavHistoryButton(Icons.Back,    _back,    _canBack,    _backHistory,    _go)),
        };
        if (L.ShowPrimaryNav)
        {
            leftKids.Add(Embed.Comp(() => new NavHistoryButton(Icons.Forward, _forward, _canForward, _forwardHistory, _go)));
            AddShortcutBand(leftKids, nav, sel);
        }

        // ── right: account · friends · bell · theme (collapses by threshold) ──
        var rightKids = new List<Element> { ProfileChip(b, L.ShowProfileName) };
        if (L.ShowFriends) rightKids.Add(IconButton.Create(Icons.Friends, () => ui?.Toggle(RailMode.Friends), nav));
        if (L.ShowBell) rightKids.Add(Embed.Comp(() => new NotificationBell()));
        if (L.ShowThemeToggle)
        {
            rightKids.Add(new BoxEl { Width = 1f, Height = 20f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(4f, 0f, 4f, 0f) });
            rightKids.Add(IconButton.Create(Theme.Dark ? Icons.Moon : Icons.Sun, _toggleTheme, nav));
        }
        // Overflow: whatever dropped off the bar folds into a "⋯" MenuFlyout so it stays reachable. A plain MenuFlyout
        // (not CommandBarFlyout) gets the clean OverlayHost clip-reveal open — CommandBarFlyout layers its own
        // overflow-expand clip on top, which made the menu pop the empty chrome then fill in (two out-of-sync clips).
        var overflow = OverflowItems(L, ui);
        bool overflowBell = !L.ShowBell;   // when the bell collapses, the notification center folds into the ⋯ menu
        if (overflow.Count > 0 || overflowBell)
            rightKids.Add(Embed.Comp(() => new OverflowMenu(this, layout, ui)));

        void MeasureCluster(FloatSignal target, RectF r)
        {
            if (r.W > 0f && MathF.Abs(r.W - target.Peek()) > 0.5f) target.Value = r.W;
        }

        var leftCluster = new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            OnBoundsChanged = r => MeasureCluster(_leftChromeWidth, r),
            Children = leftKids.ToArray(),
        };
        var rightCluster = new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            OnBoundsChanged = r => MeasureCluster(_rightChromeWidth, r),
            Children = rightKids.ToArray(),
        };
        var guardWidth = Prop.Of(() => MathF.Max(_leftChromeWidth.Value, _rightChromeWidth.Value) + Spacing.S);

        var plate = new BoxEl
        {
            // MUX tabbed-window chrome: this row IS the app-body PLATE (LayerOnMicaBaseAlt) — the same material as the
            // nav pane, the player dock and the content-pane backing, so the whole body reads as one continuous plate.
            // The UNPAINTED row is the one ABOVE this: the TAB RAIL in the title bar, where bare Mica Alt shows.
            // BOUND so the host's live re-theme (RethemeAll) re-fires it and follows a palette swap.
            Grow = 1f, Fill = Prop.Of(() => WaveeColors.Toolbar), HitTestVisible = false,
        };
        return new BoxEl
        {
            ZStack = true,
            Height = 48f,
            Children =
            [
                plate,
                // Edge commands and the omnibar are independent overlay lanes. The middle lane reserves the larger
                // measured edge width on BOTH sides, so adding a route-only command (notably Pin/Unpin in the overflow)
                // can shrink the field on a tight window but can never move its window-centred axis.
                new BoxEl
                {
                    Direction = 0, Height = 48f, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(6f, 0f, 6f, 0f), HitTestPassThrough = true,
                    Children = [leftCluster],
                },
                new BoxEl
                {
                    Direction = 0, Height = 48f, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                    Children =
                    [
                        new BoxEl { Width = guardWidth, Shrink = 0f, HitTestVisible = false },
                        new BoxEl
                        {
                            Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
                            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            ClipToBounds = true, HitTestPassThrough = true,
                            Children = [Embed.Comp(() => new FluentRichOmnibar(_searchText, _go))],
                        },
                        new BoxEl { Width = guardWidth, Shrink = 0f, HitTestVisible = false },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Height = 48f, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                    Padding = new Edges4(6f, 0f, 6f, 0f), HitTestPassThrough = true,
                    Children = [rightCluster],
                },
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

    // ── the CUSTOMIZABLE shortcut band ───────────────────────────────────────────────────────────────────────────────
    // One tile per SidebarPreferences.TopBar entry, with the SIDEBAR's item semantics unforked: Route/Entity navigate,
    // Track plays, Action resolves through WaveeExtensionRegistry and renders visible-but-disabled with its reason when
    // unavailable (a vanishing shortcut makes the user's own chrome look broken). The band collapses into the "⋯" overflow
    // together with the primary nav, so nothing becomes unreachable on a narrow window.

    /// <summary>The artwork edge inside a cover tile — <c>SidebarCover.S28</c>, one of the six canonical sidebar art sizes,
    /// so a top-bar cover shares the sidebar's decode-cache bucket instead of minting a size of its own.</summary>
    const float CoverEdge = SidebarCover.S28;

    void AddShortcutBand(List<Element> kids, IconButton.Style nav, string sel)
    {
        var prefs = _prefs;
        // Subscribe to the layout document: TopBar is a plain property, so THIS read is what re-renders the toolbar after a
        // band edit or an undo (the SidebarPane.SubscribeEpoch contract). Discarded deliberately — the value is unused.
        if (prefs is not null) _ = prefs.LayoutVersion.Value;
        // No preference service (a probe / headless mount) still gets the built-in band rather than empty chrome.
        var band = prefs?.TopBar ?? SidebarCustomLayout.DefaultTopBar;
        for (int i = 0; i < band.Count; i++) kids.Add(ShortcutTile(band[i], nav, sel));
    }

    Element ShortcutTile(SidebarItemSpec item, IconButton.Style nav, string sel) => item.Target switch
    {
        SidebarItemTarget.Entity => EntityTile(item, sel),
        SidebarItemTarget.Track => TrackTile(item),
        SidebarItemTarget.Action => ActionTile(item, nav),
        _ => RouteTile(item, nav, sel),
    };

    Element RouteTile(SidebarItemSpec item, IconButton.Style nav, string sel)
    {
        var dest = ShellNav.Dest(item.Key);
        string key = item.Key;
        string title = item.LabelOverride is { Length: > 0 } alias ? alias : dest.Title;
        // "home" keeps the shell's OWN home entry point, so the tile, the overflow row and any future keyboard verb stay on
        // one path (Go("home", null) — identical to GoNav, but this is the documented affordance).
        Action click = string.Equals(key, "home", StringComparison.Ordinal) ? _home : () => _go(key, null);
        return TileColumn(item, IconButton.Create(SidebarIcons.For(item, dest.Glyph), click, nav),
            selected: string.Equals(key, sel, StringComparison.Ordinal), tooltip: title);
    }

    Element EntityTile(SidebarItemSpec item, string sel)
    {
        // The uri → route-key map is the pin scheme's (one owner): a playlist/album/artist/show id IS its route key. A uri
        // it refuses (an episode, a hand-edited document) has nowhere to navigate, so the tile renders dimmed and inert
        // rather than lying about a destination.
        string? route = SidebarPinId.FromUri(item.Key);
        string title = item.LabelOverride is { Length: > 0 } alias ? alias
            : item.FallbackTitle is { Length: > 0 } cached ? cached
            : route is { Length: > 0 } known ? ShellNav.Dest(known).Title
            : Loc.Get(SidebarPaneLoc.MissingEntity);
        Action? click = null;
        if (route is { Length: > 0 } target) click = () => _go(target, title);
        var cover = SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, item.Key, CoverEdge,
            circular: item.EntityKind == SidebarEntityKind.Artist);
        return TileColumn(item, CoverPlate(cover, click),
            selected: route is { Length: > 0 } r && string.Equals(r, sel, StringComparison.Ordinal),
            tooltip: click is null ? Loc.Get(SidebarPaneLoc.MissingEntity) : title);
    }

    Element TrackTile(SidebarItemSpec item)
    {
        string uri = item.Key;
        string title = item.LabelOverride is { Length: > 0 } alias ? alias
            : item.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(uri);
        var player = _acts?.Svc?.Player;
        Action? click = null;
        if (player is not null) click = () => { _ = player.PlayTrackAsync(uri); };
        var cover = SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, uri, CoverEdge);
        return TileColumn(item, CoverPlate(cover, click), selected: false, tooltip: title);
    }

    Element ActionTile(SidebarItemSpec item, IconButton.Style nav)
    {
        var binding = item.Action;
        string label = item.LabelOverride ?? "";
        var icon = default(IconRef);
        bool enabled = false;
        // The default reason covers the two host-shaped cases (no registry / no action bag yet): the tile still renders,
        // disabled, saying so.
        string? reason = Loc.Get(SidebarPaneLoc.ExtensionNotNow);
        Action? click = null;

        if (binding is null)
        {
            reason = Loc.Get(SidebarPaneLoc.ExtensionMissing);   // an Action item with no binding: a hand-edited document
        }
        else
        {
            var bound = binding;   // non-nullable local: Execute takes it by `in`
            var reg = _registry;
            if (reg is not null && reg.TryGetAction(bound, out var descriptor))
            {
                if (label.Length == 0) label = descriptor.Label();
                icon = descriptor.Icon();
            }
            if (reg is not null && _acts is { } services)
            {
                var resolution = reg.Resolve(services, bound);
                enabled = resolution.Available;
                reason = resolution.ReasonLocKey is { } key ? Loc.Get(key) : null;
                var registry = reg;
                if (enabled) click = () => registry.Execute(services, in bound);
            }
        }
        if (label.Length == 0) label = Loc.Get(SidebarPaneLoc.ExtensionManage);

        // An authored icon override is the user's explicit choice and resolves against the standard icon font; only the
        // DESCRIPTOR's own mark may carry a font override, and forwarding it is what keeps an app-local codepoint
        // (WaveeIcons) from rendering as tofu.
        string glyph = SidebarIcons.Glyph(item.IconOverride, icon.Glyph ?? Icons.More);
        var style = item.IconOverride is null && icon.Font is { Length: > 0 } font ? nav with { IconFont = font } : nav;
        // Disabled ⇒ the tooltip IS the reason (that is the whole visible-but-disabled contract); enabled ⇒ the label.
        string tip = !enabled && reason is { Length: > 0 } why ? why : label;
        return TileColumn(item, IconButton.Create(glyph, click ?? NoAction, style, isEnabled: enabled),
            selected: false, tooltip: tip);
    }

    static readonly Action NoAction = static () => { };

    /// <summary>The 40-DIP tile column: the affordance plus the 16×3 accent selection mark under it — IDENTICAL geometry to
    /// the hard-coded Home button this replaced, which is what makes a default (single Home) band a zero-pixel diff. The
    /// per-tile context menu hangs off the COLUMN so right-clicking anywhere in the tile (mark included) opens it.</summary>
    Element TileColumn(SidebarItemSpec item, Element affordance, bool selected, string tooltip)
    {
        var column = new BoxEl
        {
            Direction = 1, Width = 40f, AlignItems = FlexAlign.Center, Gap = 2f,
            Children =
            [
                affordance,
                new BoxEl
                {
                    Width = 16f, Height = 3f, Corners = CornerRadius4.All(2f),
                    Fill = selected ? Tok.AccentDefault : ColorF.Transparent,
                },
            ],
        };
        if (_menuOverlay is { } svc && _prefs is { } prefs)
            column = column.WithContextMenu(svc, () => TopBarMenu(prefs, item));
        return tooltip.Length > 0 ? ToolTip.Wrap(column, tooltip) : column;
    }

    /// <summary>A cover-bearing tile face on the icon-button footprint (36×32 at ControlCornerRadius), so an artwork
    /// shortcut sits on exactly the same rhythm as a glyph one. A null click is an unresolvable target: dimmed and inert,
    /// never removed.</summary>
    static Element CoverPlate(Element cover, Action? onClick) => new BoxEl
    {
        Width = 36f, Height = 32f, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        Role = AutomationRole.Button, Focusable = true,
        Cursor = onClick is null ? (CursorId?)null : CursorId.Hand,
        Opacity = onClick is null ? 0.5f : 1f,
        OnClick = onClick,
        Children = [cover],
    }.Interactive(Interaction.Subtle);

    /// <summary>Right-click a shortcut: exactly two verbs. Dropping it is undoable through the pin-style toast (restored at
    /// its FORMER index); adding and reordering belong to the customizer, not to a context menu.</summary>
    ContextMenuModel? TopBarMenu(SidebarPreferences prefs, SidebarItemSpec item)
    {
        string id = item.Id;
        string name = TileName(item);
        return new ContextMenuModel(
        [
            new MenuFlyoutItem(Loc.Get(TopBarLoc.Remove), ActionIcons.Resolve(ActionIcons.Remove), true,
                () => RemoveShortcut(prefs, id, name)),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem(Loc.Get(TopBarLoc.Customize), ActionIcons.Resolve(ActionIcons.Rename), true,
                () => _go(SidebarLayoutMenu.CustomizeRoute, SidebarLayoutMenu.TopBarFocusArg)),
        ]);
    }

    /// <summary>Drop a shortcut + raise the undo toast whose action restores it at its former index. Snapshots the item
    /// BEFORE the removal, because the index is only knowable while it is still in the band (the
    /// <c>PinActions.Unpin</c> precedent — the toast IS the undo surface here, not the activity log).</summary>
    void RemoveShortcut(SidebarPreferences prefs, string itemId, string name)
    {
        int at = prefs.TopBarIndexOf(itemId);
        if (at < 0) return;
        var removed = prefs.TopBar[at];
        if (prefs.RemoveTopBarShortcut(itemId) != SidebarRejectReason.None) return;
        Toast.Show(Loc.Format(TopBarLoc.Removed, ("name", name)), new ToastOptions
        {
            Severity = InfoBarSeverity.Informational,
            ActionLabel = Loc.Get(Strings.Sidebar.Pin.Undo),
            // A FORWARD command, not prefs.Undo(): the undo ring is shared with the customizer, so replaying it from a
            // toast could revert an unrelated later edit. Re-adding at `at` restores the exact band (the item's id is free
            // again, so AddTopBarItem keeps it) and is itself one undoable step.
            OnAction = () => prefs.AddTopBarShortcut(removed, at),
        });
    }

    /// <summary>A shortcut's display name — the alias, then the retained last-known title, then whatever its target can
    /// name itself. Never blank: the toast and the overflow row both have to be a sentence.</summary>
    string TileName(SidebarItemSpec item)
    {
        if (item.LabelOverride is { Length: > 0 } alias) return alias;
        if (item.FallbackTitle is { Length: > 0 } cached) return cached;
        return item.Target switch
        {
            SidebarItemTarget.Route => ShellNav.Dest(item.Key).Title,
            SidebarItemTarget.Entity => SidebarPinId.FromUri(item.Key) is { Length: > 0 } route
                ? ShellNav.Dest(route).Title
                : SidebarPaneText.ShortUri(item.Key),
            SidebarItemTarget.Track => SidebarPaneText.ShortUri(item.Key),
            _ => _registry is { } reg && item.Action is { } bound && reg.TryGetAction(bound, out var descriptor)
                ? descriptor.Label()
                : Loc.Get(SidebarPaneLoc.ExtensionManage),
        };
    }

    /// <summary>One overflow row per shortcut, for the widths where the whole band collapses. Null = a shortcut this host
    /// cannot invoke at all (no player, an unresolvable entity, a missing action) — a dead row would be worse than none.</summary>
    MenuFlyoutItem? ShortcutRow(SidebarItemSpec item)
    {
        string name = TileName(item);
        switch (item.Target)
        {
            case SidebarItemTarget.Entity:
                return SidebarPinId.FromUri(item.Key) is { Length: > 0 } route
                    ? new MenuFlyoutItem(name, SidebarIcons.For(item, ShellNav.Dest(route).Glyph),
                        Invoke: () => _go(route, name))
                    : null;
            case SidebarItemTarget.Track:
            {
                var player = _acts?.Svc?.Player;
                string uri = item.Key;
                return player is null ? null
                    : new MenuFlyoutItem(name, SidebarIcons.For(item, Icons.MusicNote),
                        Invoke: () => { _ = player.PlayTrackAsync(uri); });
            }
            case SidebarItemTarget.Action:
                // The descriptor builds its OWN row, so the row's disabled state and its reason can never disagree with
                // the tile's — one resolution path, two surfaces.
                return _registry is { } reg && _acts is { } acts && item.Action is { } bound
                    && reg.TryGetAction(bound, out var descriptor)
                        ? descriptor.ToMenuItem(acts, bound)
                        : null;
            default:
            {
                string key = item.Key;
                Action go = string.Equals(key, "home", StringComparison.Ordinal) ? _home : () => _go(key, null);
                return new MenuFlyoutItem(name, SidebarIcons.For(item, ShellNav.Dest(key).Glyph), Invoke: go);
            }
        }
    }

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
            // The shortcut band collapses with the primary nav, so every tile folds in here as a plain row — the same
            // "whatever dropped off the bar stays reachable" contract the rest of this menu keeps. It used to be the one
            // hard-coded Home row.
            var band = _prefs?.TopBar ?? SidebarCustomLayout.DefaultTopBar;
            for (int i = 0; i < band.Count; i++)
                if (ShortcutRow(band[i]) is { } row) items.Add(row);
            items.Add(MenuFlyoutItem.Separator);
        }
        if (!L.ShowFriends) items.Add(new MenuFlyoutItem(Loc.Get(Strings.Shell.Friends), Icons.Friends, Invoke: () => ui?.Toggle(RailMode.Friends)));
        // Notifications, when collapsed, are handled by OverflowMenu (it anchors the panel to the ⋯ button) — not a plain item.
        if (!L.ShowThemeToggle) items.Add(new MenuFlyoutItem(Theme.Dark ? Loc.Get(Strings.Shell.LightTheme) : Loc.Get(Strings.Shell.DarkTheme), Theme.Dark ? Icons.Sun : Icons.Moon, Invoke: _toggleTheme));
        // Every durable page gets a discoverable absolute-state Pin/Unpin command, even when it is not a library entity.
        // The same canonical destination also backs tab drag/context-menu, so these surfaces cannot mint different pins.
        if (_acts is { } acts && acts.CurrentDestination?.Invoke() is { } destination
            && PinActions.RowForDestination(acts, in destination) is { } pagePin)
        {
            if (items.Count > 0 && !items[^1].IsSeparator) items.Add(MenuFlyoutItem.Separator);
            items.Add(pagePin);
        }
        return items;
    }
}

/// <summary>The top-bar band's loc KEYS as literals, in one place — the <c>SidebarPaneLoc</c> / <c>CzLoc</c> precedent.
/// Spelled here rather than through the generated <c>Strings</c> table because the generator only emits members for keys
/// already present in <c>assets/loc/en-US.json</c>; a key that has not landed yet renders loudly as <c>[key]</c> instead of
/// breaking the build. Keys the band REUSES (<c>Strings.Sidebar.Pin.Undo</c>, <c>SidebarPaneLoc.*</c>) are not restated.</summary>
static class TopBarLoc
{
    public const string Remove = "sidebar.topbar.remove";
    public const string Removed = "sidebar.topbar.removed";
    public const string Customize = "sidebar.topbar.customize";
    /// <summary>Surfaced by the customizer when an add hits <c>SidebarLayoutReducer.MaxTopBarItems</c>. Declared here — the
    /// cap is the band's, so its message belongs with the band's other keys — and consumed by the customizer's Top bar card.</summary>
    public const string CapReached = "sidebar.topbar.capReached";
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
