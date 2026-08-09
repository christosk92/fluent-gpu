using System;
using System.Collections.Generic;
using FluentGpu.Animation;
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

/// <summary>One tab the "⌄" overflow menu has to name: its index in the shell's open-tab list plus the strip label.</summary>
readonly record struct ChromeTabRef(int Index, string Label, string Glyph);

/// <summary>
/// The MERGED chrome row's island builders — the one 48-DIP <see cref="TitleBar"/> that replaced Wavee's two 48-DIP
/// rows (the tab-strip title bar + the ShellToolbar). This is deliberately NOT a <see cref="Component"/>: its three
/// methods are handed to the bar as its <c>Tabs</c> / <c>CenterContent</c> / <c>Trailing</c> slot builders and run
/// INSIDE the bar's own render, so they must never call a hook. Everything reactive is a signal read (which subscribes
/// the bar) or a bound <see cref="Prop"/>; everything that needs hooks is a real child component
/// (<see cref="NavHistoryButton"/>, <see cref="ProfileMenu"/>,
/// <see cref="OverflowMenu"/>, <see cref="MergedSearchIsland"/>, <see cref="TabOverflowButton"/>).
///
/// <para><b>The bar memoises its tree.</b> Anything read here that changes what an island RENDERS must also be folded
/// into <see cref="ContentVersion"/>, or the memo hands back the stale tree; anything that changes an island's SIZE
/// must be in there too, or the non-client region report describes the old rect (newly covered pixels stay window-drag,
/// vacated pixels go dead). That is the whole reason this class exists as one owner.</para>
///
/// <para><b>The islands HUG, and every pixel inside one is permanently undraggable.</b> A title-bar island's laid-out
/// rect is reported WHOLESALE as <c>TitleBarHit.Client</c>, the engine exposes no app-side "start a window move" seam,
/// and a <c>Transform</c> cannot help (the report walks the island's own ancestor chain, not a descendant's translate).
/// So slack inside an island is dead window-drag space with no workaround — no Grow fillers, no gap between children,
/// no padding-out. Anything that should read as drag band has to live OUTSIDE the islands, in the bar's own grow bands.
/// <br/>That is also why the search is centred BETWEEN THE CLUSTERS rather than in the window: the only lever for true
/// window-centring is padding the lighter side inside the centre island, and that pad would be a variable-width dead
/// strip alongside the field — exactly the blind spot this contract exists to prevent. Real window-centring needs an
/// engine follow-up (the bar reporting an island's CONTENT rect, or a centre-bias knob).</para>
/// </summary>
sealed class MergedChromeRow
{
    // ── shell dependencies (signals + verbs; never frozen values) ────────────────────────────────────────────────────
    readonly Signal<bool> _canBack, _canForward;
    readonly Action<string, string?> _go;
    readonly Action _back, _forward, _toggleTheme;
    readonly Signal<string> _searchText;
    readonly List<Route> _backHistory, _forwardHistory;
    readonly IReadSignal<MergedChromeLayout> _layout;
    readonly Signal<bool> _searchExpanded;
    readonly IReadSignal<int> _searchFocusRequest;
    readonly Func<Element> _tabStrip;
    readonly Func<int> _tabsEpoch;                 // strip revision (tab set × KeepTabs) — the chevron's subscribe
    readonly Func<List<ChromeTabRef>> _hiddenTabs; // MRU-first, computed by the shell
    readonly Action<int> _activateTab;

    // PLAIN FIELDS refreshed on every shell render (the ShellToolbar idiom): reference-stable ambient services the
    // island builders cannot resolve themselves, because a slot builder runs outside any component's hook scope.
    internal PlaybackBridge? Bridge;
    internal ShellUi? Ui;
    internal ActionServices? Acts;

    public MergedChromeRow(
        Signal<bool> canBack, Signal<bool> canForward,
        Action<string, string?> go, Action back, Action forward,
        Signal<string> searchText, Action toggleTheme,
        List<Route> backHistory, List<Route> forwardHistory,
        IReadSignal<MergedChromeLayout> layout, Signal<bool> searchExpanded, IReadSignal<int> searchFocusRequest,
        Func<Element> tabStrip, Func<int> tabsEpoch, Func<List<ChromeTabRef>> hiddenTabs, Action<int> activateTab)
    {
        _searchFocusRequest = searchFocusRequest;
        _canBack = canBack; _canForward = canForward;
        _go = go; _back = back; _forward = forward;
        _searchText = searchText; _toggleTheme = toggleTheme;
        _backHistory = backHistory; _forwardHistory = forwardHistory;
        _layout = layout; _searchExpanded = searchExpanded;
        _tabStrip = tabStrip; _tabsEpoch = tabsEpoch; _hiddenTabs = hiddenTabs; _activateTab = activateTab;
    }

    // ── ContentVersion: the ONE thing the bar must be fed ────────────────────────────────────────────────────────────
    /// <summary>Every piece of state that changes an island's SIZE OR SHAPE, folded into one int. Read from the bar's
    /// render, so each read here also subscribes the bar — which is what makes the memo bust AND the non-client region
    /// report re-push in the same frame. Anything added to an island later belongs in this fold.</summary>
    public int ContentVersion()
    {
        var l = _layout.Value;                                   // the whole ladder (every gate + every width)
        bool expanded = _searchExpanded.Value;                   // the icon-mode search open latch
        int epoch = _tabsEpoch();                                // tab set × KeepTabs → the strip's own width
        int hidden = _hiddenTabs().Count;                        // the "⌄" chevron's presence + badge digits
        int auth = (int)(Bridge?.Auth.Value ?? AuthStatus.LoggedOut);   // avatar ↔ "Connecting…" ↔ Sign-in button
        // NOT folded in (deliberately): the unread count. It rides the profile chip's avatar as a fixed-footprint
        // badge overlay inside a 24-DIP ZStack, so it changes no island's SIZE and ProfileMenu re-renders itself off
        // NotificationCenterBridge.UnreadCount. Only SHAPE/SIZE state belongs in this fold.
        // The click-expansion is covered TWICE over, in both directions, and that is on purpose: bit 16 is the latch
        // itself, and every other term moves with it because the latch is now an INPUT to the ladder (the expansion
        // folds the name, the friends button and tabs to fund the field, so flags/SearchWidth/KeepTabs/hidden all
        // change on the same flip). Either alone would bump; the region report must never miss this one.
        int flags = (l.ShowName ? 1 : 0) | (l.ShowFriends ? 2 : 0)
                  | (l.ShowForward ? 4 : 0) | (l.SearchMode == MergedSearchMode.Icon ? 8 : 0) | (expanded ? 16 : 0)
                  | (l.SearchExpanded ? 32 : 0);
        return HashCode.Combine(flags, (int)l.SearchWidth, (int)l.TabMaxWidth, l.KeepTabs, epoch, hidden, auth);
    }

    // ── the LEADING island: back · forward · the tab strip · the "⌄" overflow ────────────────────────────────────────
    /// <summary>The bar has no leading slot, so Wavee's history nav lives at the head of the TABS island. (The
    /// HAMBURGER does not: it uses the bar's own <c>ShowPaneToggle</c> built-in, which sits in the fixed lead column —
    /// aligned with the 56-DIP compact rail's icon centre the way the two-row toolbar's was, and reported as its OWN
    /// Client region so the 14-DIP header pad after it stays real drag band.)
    /// <para>NO Gap and no spacer children: every DIP between two children of an island is undraggable dead space, so
    /// the affordances are contiguous and their own 12-DIP glyph padding supplies the breathing room. The buttons are
    /// 40×44 (the bar's own nav metric) rather than the toolbar's 36×32, so the island's 48-DIP band is all but fully
    /// covered by real hit targets instead of leaving an 8-DIP inert strip top and bottom.</para>
    /// Hugs: Shrink=1 + MinWidth=0 so the island is what gives when the row overruns (the caption cluster never moves).</summary>
    public Element Tabs()
    {
        var l = _layout.Value;
        var nav = ShellToolbar.BarNavStyle;
        var kids = new List<Element>(4)
        {
            Embed.Comp(() => new NavHistoryButton(Icons.Back, _back, _canBack, _backHistory, _go, nav)),
        };
        if (l.ShowForward)
            kids.Add(Embed.Comp(() => new NavHistoryButton(Icons.Forward, _forward, _canForward, _forwardHistory, _go, nav)));

        kids.Add(_tabStrip());
        // Conditional, not a self-collapsing child: an always-present zero-width chevron would still occupy a slot in a
        // hugging island. Its presence is in ContentVersion.
        if (_hiddenTabs().Count > 0)
            kids.Add(Embed.Comp(() => new TabOverflowButton(_tabsEpoch, _hiddenTabs, _activateTab)));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Height = TitleBar.ExpandedHeight,
            Shrink = 1f, MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }

    // ── the CENTRE island: the omnibar ───────────────────────────────────────────────────────────────────────────────
    /// <summary>The flexible column's hugging island — the field and NOTHING else. The bar centres it between the
    /// clusters, and the slack on both flanks stays part of the caption drag band because it lives in the bar's grow
    /// bands rather than in here (see the class remarks on why a window-centring guard pad cannot be paid for).</summary>
    public Element Center(IReadSignal<float> avail)
        => Embed.Comp(() => new MergedSearchIsland(_searchText, _go, _searchExpanded, _searchFocusRequest, _layout, avail));

    // ── the TRAILING island: profile · friends · "⋯" ─────────────────────────────────────────────────────────────────
    /// <summary>Three affordances, down from six. The bell is GONE — its unread badge rides the profile chip's avatar
    /// and its panel is a profile-menu row; the moon/sun toggle (and the divider that fenced it) is GONE — it is a
    /// profile-menu row beside Palette. Friends is the only thing that still moves with width, and it MOVES rather than
    /// drops: below <c>ChromeFriendsEnterW</c> it is a profile-menu row instead of a button (see
    /// <c>MergedChromeLayout.FriendsInMenu</c>), so the island shrinks without ever losing an affordance.</summary>
    public Element Trailing()
    {
        var l = _layout.Value;
        var nav = ShellToolbar.BarNavStyle;
        var kids = new List<Element>(3) { ProfileChip() };
        // ShellToolbar.BarNavMargin on every island affordance: 2 DIP a side, the same breathing room the bar's own
        // pane toggle carries. See that token for why the drag-band cost is accepted.
        if (l.FriendsInRow)
            kids.Add(IconButton.Create(Icons.Friends, ToggleFriends, nav)
                with { Margin = ShellToolbar.BarNavMargin });
        // Whatever dropped off the row folds into a "⋯" MenuFlyout so it stays reachable. A plain MenuFlyout (not
        // CommandBarFlyout) gets the clean OverlayHost clip-reveal open. Only Forward and the page Pin land here now —
        // everything identity-shaped moved into the profile flyout, which is where a user looks for it.
        var overflow = OverflowItems(l);
        if (overflow.Count > 0)
            kids.Add(Embed.Comp(() => new OverflowMenu(this, _layout)));

        return new BoxEl
        {
            // HUGS: Shrink=0, no padding, no Grow filler, and NO Gap — a gap between two island children is dead
            // window-drag space, so the affordances are contiguous and their glyph padding is the separation. Its rect
            // IS the reported Client region.
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
            Height = TitleBar.ExpandedHeight,
            Children = kids.ToArray(),
        };
    }

    /// <summary>Toggle the friend-activity rail. A METHOD, not a per-render lambda: the profile menu freezes its ctor
    /// args at mount, so the verb it is handed must be reference-stable AND resolve <see cref="Ui"/> at INVOKE time
    /// (the field is refreshed on every shell render).</summary>
    internal void ToggleFriends() => Ui?.Toggle(RailMode.Friends);

    Element ProfileChip()
    {
        var b = Bridge;
        var auth = b?.Auth.Value ?? AuthStatus.LoggedOut;   // subscribe (also folded into ContentVersion — it resizes)
        if (auth == AuthStatus.Authenticated)
            // The ladder goes in as a SIGNAL, not as frozen bools: a ComponentEl never re-runs its factory, so
            // ShowName/FriendsInMenu handed over as plain ctor args would freeze at mount. Reading the signal inside
            // ProfileMenu.Render subscribes IT, so the chip and its flyout track the ladder on their own.
            return Embed.Comp(() => new ProfileMenu(b!, _layout, _toggleTheme, ToggleFriends));
        if (auth == AuthStatus.Authenticating)
            return new BoxEl
            {
                Height = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
                Children = [Caption(Loc.Get(Strings.Shell.Connecting)).Secondary()],
            };
        return Button.Accent(Loc.Get(Strings.Shell.SignIn), () => { _ = b?.Session.ConnectAsync(); });
    }

    /// <summary>The items currently dropped from the row, as plain MenuFlyout rows: FORWARD (below the historical
    /// 520/560 nav band) and the page Pin. Nothing else — friends, notifications and the theme toggle all live in the
    /// PROFILE flyout now, at every width, so the "⋯" never carries an identity command.</summary>
    internal List<MenuFlyoutItem> OverflowItems(MergedChromeLayout l)
    {
        var items = new List<MenuFlyoutItem>(2);
        if (!l.ShowForward)
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Nav.Forward), Icons.Forward,
                Enabled: _canForward.Value, Invoke: _forward));
        // Every durable page keeps a discoverable absolute-state Pin/Unpin command, even when it is not a library
        // entity. The same canonical destination backs tab drag + the tab context menu, so these cannot mint different pins.
        if (Acts is { } acts && acts.CurrentDestination?.Invoke() is { } destination
            && PinActions.RowForDestination(acts, in destination) is { } pagePin)
        {
            if (items.Count > 0 && !items[^1].IsSeparator) items.Add(MenuFlyoutItem.Separator);
            items.Add(pagePin);
        }
        return items;
    }
}

/// <summary>
/// The merged row's centre island: Wavee's rich omnibar at the ladder's width, or — under pressure — a 32-DIP
/// magnifier that CLICK-expands into one (the <c>LibraryV3Search</c> affordance, re-used here at shell scale).
///
/// <para>A real component, not an inline element, because the expand needs hooks: a memoised
/// <see cref="TemplateParts"/> to capture the box's root handle, a layout effect to put the caret in the field on the
/// open edge, and one to hand focus back to the magnifier on Escape. Its WIDTH is a bound prop so the field tracks the
/// ladder (and the bar's live <c>CenterAvail</c> clamp) without a re-render; only the SHAPE change re-renders, and that
/// change is in <c>MergedChromeRow.ContentVersion</c>.</para>
/// </summary>
sealed class MergedSearchIsland : Component
{
    /// <summary>Open/close motion: the field's bounds tween over the Fast rung and the icon cross-fades
    /// (LibraryV3Search §3.2.16 authored 180/100 ms; both legs snapped to the ladder, keeping the enter:exit ratio).</summary>
    static readonly LayoutTransition FieldReveal = new(
        TransitionChannels.Bounds, TransitionDynamics.Tween(WaveeMotion.Fast, Easing.SmoothOut),
        Size: SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(WaveeMotion.Faster, Easing.SmoothOut));

    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    readonly Signal<bool> _expanded;
    readonly IReadSignal<int> _focusRequest;
    readonly IReadSignal<MergedChromeLayout> _layout;
    readonly IReadSignal<float> _avail;

    public MergedSearchIsland(Signal<string> text, Action<string, string?> go, Signal<bool> expanded,
                              IReadSignal<int> focusRequest, IReadSignal<MergedChromeLayout> layout,
                              IReadSignal<float> avail)
    { _text = text; _go = go; _expanded = expanded; _focusRequest = focusRequest; _layout = layout; _avail = avail; }

    /// <summary>The field's live width — <c>l.SearchWidth</c>, full stop, whenever the ladder resolved a field. Read
    /// through a bound prop, never frozen: the ladder moves on every resize band AND on the expand latch.
    ///
    /// <para><b>Why the measured column is no longer the clamp.</b> The ladder now funds the click-expansion IN PLACE
    /// (<c>MergedChromeLayout.Resolve</c>'s <c>searchExpanded</c> input), so <c>SearchWidth</c> is already sized
    /// against the row's real budget and clamping it again is not safety, it is a bug: <c>CenterAvail</c> is the
    /// column's ARRANGED width from the previous layout, and the column is one of three equal-weight grow siblings, so
    /// while the row is still collapsed it measures ≈ the 32-DIP icon plus a third of the slack. Taking the min of the
    /// two is what produced the reported 40-DIP "expanded" field (and, once the folds landed, a multi-frame ratchet
    /// toward the fixed point instead of one clean open).</para>
    ///
    /// <para>The clamp survives on the ICON branch only — the last-resort path: the one frame between the latch flip
    /// and the ladder's re-resolve, and the sub-<c>MinimumExpandedWidthFor</c> widths where the folds could not reach
    /// the floor and the row genuinely has nothing to give.</para></summary>
    float FieldWidth()
    {
        var l = _layout.Value;
        if (l.SearchMode == MergedSearchMode.Field) return l.SearchWidth;
        float target = ShellResponsiveLayout.ChromeSearchExpandedW;
        float avail = _avail.Value;
        if (float.IsFinite(avail) && avail > 0f) target = MathF.Min(target, avail);
        return MathF.Max(ShellResponsiveLayout.ChromeSearchIconW, target);
    }

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var fieldNode = UseRef<NodeHandle>(default);
        var buttonNode = UseRef<NodeHandle>(default);

        // ONE memoised parts map: mutating a TemplateParts bumps its Epoch and invalidates the engine's apply-once
        // prototype cache, so it must be built at mount and never per render.
        var parts = UseMemo(() =>
        {
            var p = new TemplateParts();
            p[AutoSuggestBox.PartRoot] = b => b with { OnRealized = h => fieldNode.Value = h };
            return p;
        }, DepKey.Empty);

        // "Is this a width the row wants a MAGNIFIER at?" — which is NOT the same question as "is SearchMode Icon?"
        // any more, because the latch is now an input to the ladder: while it is held the ladder answers Field, and
        // MergedChromeLayout.SearchExpanded is the flag saying that field exists ONLY because of the latch. Reading
        // SearchMode alone here would make the drop-the-latch effect below fire on the expansion's own output —
        // latch → Field → drop latch → Icon → latch … a per-frame oscillation.
        var ladder = _layout.Value;
        bool icon = ladder.SearchMode == MergedSearchMode.Icon || ladder.SearchExpanded;
        bool expanded = _expanded.Value;
        bool collapsed = icon && !expanded;

        // Widening back into Field mode drops the latch, so a later narrowing starts closed again (and the open flag
        // can never disagree with the shape the ladder asked for).
        UseLayoutEffect(() =>
        {
            if (!icon && _expanded.Peek()) _expanded.Value = false;
        }, DepKey.From(icon ? 1 : 0));

        void FocusField()
        {
            var node = fieldNode.Value;
            if (!node.IsNull) hooks.FocusNode?.Invoke(node, true);
        }

        // Focus the field once per icon-mode OPEN. Keyed on the open EDGE (false at mount), so a shell that starts in
        // Field mode never steals the caret. If it ever fails to land here the fallback target is EditableText.PartText
        // — do not ship the transition without checking that typing lands in the field.
        UseLayoutEffect(() =>
        {
            if (icon && expanded) FocusField();
        }, DepKey.From(icon && expanded ? 1 : 0));

        // Ctrl+K: a monotonic ticket rather than a bool, so a second press re-focuses. 0 = never requested, which is why
        // this cannot fire at mount either.
        int request = _focusRequest.Value;
        UseLayoutEffect(() =>
        {
            if (request > 0 && !collapsed) FocusField();
        }, DepKey.From(request));

        if (collapsed)
            return ToolTip.Wrap(new BoxEl
            {
                Key = "chrome-search-button",
                Width = ShellResponsiveLayout.ChromeSearchIconW, Height = 32f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                Animate = FieldReveal,
                OnRealized = h => buttonNode.Value = h,
                OnClick = () => _expanded.Value = true,
                // 16, the stock icon box every other glyph in this row uses (BarNavStyle.GlyphSize, the tab icons, the
                // omnibar's own magnifier). 14 made the collapsed search read a size smaller than its neighbours.
                Children = [Icon(Icons.Search, 16f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle), Loc.Get(Strings.Nav.Search));

        return new BoxEl
        {
            Key = "chrome-search-field",
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
            Width = Prop.Of(() => FieldWidth()),
            Animate = FieldReveal,
            // ONE Escape clears the query (the thing you want gone first); a second closes the expanded field and hands
            // focus back to the magnifier. In Field mode Escape only clears — there is no magnifier to return to.
            OnKeyDown = e =>
            {
                if (e.KeyCode != Keys.Escape) return;
                if (_text.Peek().Length > 0) { _text.SetIfChanged(""); }
                else if (_expanded.Peek())
                {
                    _expanded.Value = false;
                    var b = buttonNode.Value;
                    if (!b.IsNull) hooks.FocusNode?.Invoke(b, true);
                }
                else return;
                e.Handled = true;
            },
            Children = [Embed.Comp(() => new FluentRichOmnibar(_text, _go, parts))],
        };
    }
}

/// <summary>
/// The tab strip's "⌄" overflow: the tabs the ladder folded out of the strip, most-recently-used first, as a plain
/// MenuFlyout. Renders NOTHING (a zero-width, hit-test-free box) while everything fits, so the tabs island keeps
/// hugging its real content.
/// </summary>
sealed class TabOverflowButton : Component
{
    readonly Func<int> _epoch;
    readonly Func<List<ChromeTabRef>> _hidden;
    readonly Action<int> _activate;

    public TabOverflowButton(Func<int> epoch, Func<List<ChromeTabRef>> hidden, Action<int> activate)
    { _epoch = epoch; _hidden = hidden; _activate = activate; }

    public override Element Render()
    {
        _ = _epoch();                                   // subscribe: the hidden set is a plain list, this is its revision
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        var hidden = _hidden();
        int count = hidden.Count;
        if (count == 0) return new BoxEl { Width = 0f, Shrink = 0f, HitTestVisible = false };

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var rows = _hidden();
            var items = new MenuFlyoutItem[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                int index = rows[i].Index;
                items[i] = new MenuFlyoutItem(rows[i].Label, rows[i].Glyph, Invoke: () => _activate(index));
            }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        // Chevron + count on one plate (the count is the whole point of the affordance, so it is text rather than a
        // decorative badge — a two-digit count widens the plate, which is why ContentVersion folds it in). Height 32 and
        // a 12pt Caption count put it on the SAME band as the search field and the profile chip (both 32): at 28/11 it
        // sat a rung below everything beside it and read as a leftover badge rather than a control.
        return new BoxEl
        {
            Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Direction = 0, Gap = 3f, Padding = new Edges4(7f, 0f, 7f, 0f),
            Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = Toggle,
            OnRealized = h => anchor.Value = h,
            Children =
            [
                new TextEl(Icons.ChevronDownSmall) { Size = 10f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
                new TextEl(count.ToString()) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
            ],
        }.Interactive(Interaction.Subtle);
    }
}
