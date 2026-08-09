using System;
using System.Collections.Generic;
using FluentGpu.Controls;   // Route
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// O3 — THE SHORTCUT BAND, RENDERED IN THE SIDEBAR.
//
// WHAT MOVED AND WHAT DID NOT. The customizable ≤6-tile shortcut band (default: one Home tile) used to render in the
// shell's 48-DIP toolbar row. Its RENDER SITE moved here — the sidebar's top region, in all three designs, expanded and
// 56-DIP rail, plus the narrow drawer. NOTHING ELSE MOVED: the list is still `SidebarCustomLayout.TopBar` /
// `EffectiveTopBar`, still mutated only through `AddTopBarItem`/`MoveTopBarItem`/`RemoveTopBarItem`, still edited by the
// customizer's Top bar card, and still carried by the same undo ring, rejection contract, autosave and wire. There is no
// schema change, no new command and no second source of truth.
//
// WHY IT IS ONE COMPONENT AND NOT A RENDERER BRANCH. `SidebarPane`/`SidebarPaneRail` must never branch on
// `Config.Design` (the ONE mode seam, iron rule 1), so the band reaches them the way every other piece of mode chrome
// does: two `SidebarPaneConfig` DELEGATES (`NavBand` for the expanded head, `RailHead` for the rail) that all three mode
// components set IDENTICALLY. The delegates are invoked inside the pane's own render — which is what makes the signals
// they read subscribe the pane (the `Document`/`Head`/`RailFooter` contract, iron rule 2).
//
// SELECTION. The band's mark is drawn STATICALLY inside the tile (a 3×16 accent bar in the row's own selection gutter,
// the `SidebarSelectionPill` geometry). It deliberately does NOT register with `SidebarPane`'s selection transaction:
// that transaction is keyed by ROUTE over realized PLAN rows, and a band tile for "home" plus a plan row for "home"
// would be two registrations under one key — the pane would fly its indicator between the band and the list.
//
// INSET. The band is FIXED HEAD CHROME above the scroll surface, not a plan row, so it carries the pane's horizontal
// inset itself — the same documented exception `SidebarPaneSearchHead` takes, and the only place the one-inset rule
// (`SidebarPaneMetrics.PanePad`) is duplicated on purpose. Rows land at 8 and their content at 8 + 6 = 14, exactly like
// the virtualized rows below them.
sealed class SidebarNavBand : Component
{
    // ── the two config seams. Both return NULL for an emptied band, so an emptied band costs no head chrome and no rail
    //    divider (the pane skips a null child; the rail inserts nothing). Both are invoked in the PANE's render.
    /// <summary>The expanded band, for <see cref="SidebarPaneConfig.NavBand"/>.</summary>
    public static Element? Head(SidebarPreferences? prefs, Signal<Route> route, Action<string, string?> go)
        => Visible(prefs)
            ? Embed.Comp(() => new SidebarNavBand(route, go, rail: false)) with { Key = "navband" }
            : null;

    /// <summary>The 56-DIP rail form, for <see cref="SidebarPaneConfig.RailHead"/>.</summary>
    public static Element? RailHead(SidebarPreferences? prefs, Signal<Route> route, Action<string, string?> go)
        => Visible(prefs)
            ? Embed.Comp(() => new SidebarNavBand(route, go, rail: true)) with { Key = "navband-rail" }
            : null;

    /// <summary>Reading <c>LayoutVersion</c> here is the SUBSCRIPTION for the caller — these statics run inside the pane's
    /// render, and <c>TopBar</c> is a plain property (the `ShellToolbar.AddShortcutBand` contract, verbatim). Without it a
    /// band emptied to zero tiles would leave its chrome mounted until something else re-rendered the pane.</summary>
    static bool Visible(SidebarPreferences? prefs)
    {
        if (prefs is null) return SidebarNavBandModel.Renders(SidebarCustomLayout.DefaultTopBar);
        _ = prefs.LayoutVersion.Value;
        return SidebarNavBandModel.Renders(prefs.TopBar);
    }

    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;
    /// <summary>The 56-DIP tile strip rather than the expanded rows. Frozen per mount — the two forms are two mounts (the
    /// pane keeps both layers alive for the cross-fade), never one component switching shape.</summary>
    readonly bool _rail;

    /// <summary>Reused shaping scratch — the band is ≤6 tiles, but this component re-renders on every navigation.</summary>
    readonly List<SidebarNavBandTile> _tiles = new(SidebarNavBandModel.MaxTiles);

    SidebarPreferences? _prefs;
    ActionServices? _acts;
    WaveeExtensionRegistry? _registry;
    IOverlayService? _menuOverlay;

    public SidebarNavBand(Signal<Route> route, Action<string, string?> go, bool rail)
    {
        _route = route; _go = go; _rail = rail;
    }

    public override Element Render()
    {
        _prefs = UseContext(SidebarPreferences.Slot);
        _acts = UseContext(ActionServices.Slot);
        _menuOverlay = UseContext(Overlay.Service);
        // The registry is the ONE lookup path for a bound Action tile (never AppActions.All — the M3 forward-compat
        // guardrail). Context first, then the action bag, so a host that provides only one of them still resolves.
        _registry = UseContext(WaveeExtensionRegistry.Slot) ?? _acts?.Extensions;

        var prefs = _prefs;
        // SUBSCRIBE: TopBar is a plain property on the document, so this read is what re-renders the band after an edit,
        // an undo or a template apply — exactly as the pane's row slots subscribe LayoutVersion through SubscribeEpoch.
        if (prefs is not null) _ = prefs.LayoutVersion.Value;
        // SUBSCRIBE: the selection mark. The band is a handful of tiles, so it takes the route fanout directly rather
        // than through the pane's per-row epoch sweep (which exists for a 10k-row realized window, not for six tiles).
        string sel = _route.Value.Name;

        var band = prefs?.TopBar ?? SidebarCustomLayout.DefaultTopBar;
        int n = SidebarNavBandModel.Shape(band, _tiles);
        // The band emptied under a mounted component (the `Visible` gate above runs in the PANE's render, which may not
        // have re-run). Draw nothing rather than an empty inset.
        if (n == 0) return new BoxEl { Height = 0f, Shrink = 0f };

        // The expanded form appends its own closing RULE (see BandRule): the rail already draws one under its head
        // tiles, and a band that is separated in one presentation and not in the other is the same chrome telling two
        // different stories about where the app's navigation ends and the document begins.
        var kids = new Element[_rail ? n : n + 1];
        for (int i = 0; i < n; i++)
        {
            var tile = _tiles[i];
            var item = band[tile.Index];
            kids[i] = _rail ? RailTile(item, in tile, sel) : Row(item, in tile, sel);
        }
        if (!_rail) kids[n] = BandRule();

        return _rail
            ? new BoxEl
            {
                // The rail's own rhythm (SidebarPaneRail's column gap + centring). No width of its own: the 40-DIP tiles
                // centre inside the 56-DIP strip, so the compact layer's geometry is untouched.
                Key = "navband-rail-strip",
                Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children = kids,
            }
            : new BoxEl
            {
                // FIXED HEAD CHROME: it sits OUTSIDE the padded list wrapper, so it carries the pane's horizontal inset
                // itself (the SidebarPaneSearchHead exception).
                //
                // BOTTOM PAD = 0, AND THAT IS THE FIX. The band used to end in 4, but the list it precedes carries
                // SidebarPaneMetrics.PanePad.Top = 8 of its own — so the authored "4" was never on screen; the real gap
                // was 4 + 8 = 12, a number nobody chose. The rhythm is now stated once and is literally what it says:
                //   last band row → 8 (BandRule's own lead-in) → the rule → 8 (the list's PanePad.Top) → first row.
                // Every DIP below the band belongs to exactly one owner, and the two halves are equal by construction.
                Key = "navband-rows",
                Direction = 1, Shrink = 0f,
                Padding = new Edges4(8f, 8f, 8f, 0f),
                Children = kids,
            };
    }

    /// <summary>The band's closing rule in the EXPANDED pane — the counterpart of the <c>SidebarRailItem.Divider()</c>
    /// that <see cref="SidebarPaneRail"/> already inserts under the rail's head tiles. Before this, the rail said "the
    /// navigation band ends here" and the expanded pane said nothing, so the same six shortcuts read as head chrome in
    /// one presentation and as the first six rows of the document in the other.
    /// <para>It speaks the PANE's divider language, not the rail's: <c>Tok.StrokeDividerDefault</c> spanning the row
    /// content box (<see cref="SidebarPaneMetrics.RowInset"/>), exactly like <c>SidebarSectionHeader.ExplicitDivider</c>
    /// — the rail's short centred 24-DIP tick exists because a 56-DIP strip has no row box to span. Its 8-DIP lead-in is
    /// the top half of the band's closing rhythm; the list's own <c>PanePad.Top</c> is the bottom half.</para></summary>
    static Element BandRule() => new BoxEl
    {
        Key = "navband-rule",
        Height = 1f, Shrink = 0f, HitTestVisible = false,
        Margin = new Edges4(SidebarPaneMetrics.RowInset.Left, 8f, SidebarPaneMetrics.RowInset.Right, 0f),
        Fill = Tok.StrokeDividerDefault,
    };

    // ── the expanded rows ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band's uniform row height: Cozy WITHOUT a subtitle, from the ONE ladder. Pinned rather than derived
    /// per row for the same reason every section pins one height — a mixed band would read as a ragged list against the
    /// uniform rows below it.</summary>
    static float RowHeight => SidebarRowMetrics.HeightFor(SidebarDensity.Cozy, hasSubtitle: false);

    static float ArtSize => SidebarRowMetrics.ArtFor(SidebarDensity.Cozy);

    Element Row(SidebarItemSpec item, in SidebarNavBandTile tile, string sel) => tile.Kind switch
    {
        SidebarNavBandTileKind.Entity => EntityRow(item, in tile, sel),
        SidebarNavBandTileKind.Track => TrackItemRow(item),
        SidebarNavBandTileKind.Action => ActionRow(item),
        _ => RouteRow(item, sel),
    };

    Element RouteRow(SidebarItemSpec item, string sel)
    {
        var dest = ShellNav.Dest(item.Key);
        string title = item.LabelOverride is { Length: > 0 } alias ? alias : dest.Title;
        bool selected = SidebarNavBandModel.SelectsRoute(item, sel);
        string key = item.Key;
        float height = RowHeight;

        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = title,
            Selected = selected,
            Density = SidebarDensity.Cozy,
            Height = height,
            Glyph = SidebarIcons.For(item, dest.Glyph),
            // The documented Home affordance: `Go("home", null)` is byte-for-byte what the shell's own Home entry point
            // does, so the tile, the sidebar row and any future keyboard verb stay on ONE navigation path.
            OnClick = () => _go(key, null),
            MenuOverlay = _menuOverlay,
            Menu = RowMenu(item),
            Overflow = _menuOverlay is not null && _prefs is not null,
        };
        return Mark(SidebarEntityRow.Create(spec), selected, height);
    }

    Element EntityRow(SidebarItemSpec item, in SidebarNavBandTile tile, string sel)
    {
        string? route = tile.RouteKey;
        bool resolvable = route is { Length: > 0 };
        string title = item.LabelOverride is { Length: > 0 } alias ? alias
            : item.FallbackTitle is { Length: > 0 } cached ? cached
            : route is { Length: > 0 } known ? ShellNav.Dest(known).Title
            : Loc.Get(SidebarPaneLoc.MissingEntity);
        bool selected = SidebarNavBandModel.SelectsRoute(item, sel);
        float height = RowHeight;

        Action? click = null;
        if (route is { Length: > 0 } target) click = () => _go(target, title);

        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = title,
            Selected = selected,
            // §C1.4's retention rule: an unresolvable target is VISIBLE-BUT-DISABLED (dimmed + inert), never removed and
            // never silently absent — a vanishing shortcut makes the user's own chrome look broken.
            Enabled = resolvable,
            Density = SidebarDensity.Cozy,
            Height = height,
            ArtSize = ArtSize,
            Leading = SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, item.Key, ArtSize,
                                       circular: item.EntityKind == SidebarEntityKind.Artist),
            OnClick = click,
            MenuOverlay = _menuOverlay,
            Menu = RowMenu(item),
            Overflow = resolvable && _menuOverlay is not null && _prefs is not null,
        };
        Element row = Mark(SidebarEntityRow.Create(spec), selected, height);
        return resolvable ? row : ToolTip.Wrap(row, Loc.Get(SidebarPaneLoc.MissingEntity));
    }

    /// <summary>A hand-placed TRACK (§C1.8.3): it PLAYS, it never navigates and it is never selected — a track has no
    /// detail route, which is the whole reason tracks are excluded from pins and navigation.</summary>
    Element TrackItemRow(SidebarItemSpec item)
    {
        string uri = item.Key;
        string title = item.LabelOverride is { Length: > 0 } alias ? alias
            : item.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(uri);
        var player = _acts?.Svc?.Player;
        Action? click = null;
        if (player is not null) click = () => { _ = player.PlayTrackAsync(uri); };

        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = title,
            Enabled = player is not null,
            Density = SidebarDensity.Cozy,
            Height = RowHeight,
            ArtSize = ArtSize,
            Leading = SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, uri, ArtSize),
            Track = true,
            OnClick = click,
            MenuOverlay = _menuOverlay,
            Menu = RowMenu(item),
            Overflow = player is not null && _menuOverlay is not null && _prefs is not null,
        };
        return SidebarEntityRow.WithPlayTrackHint(SidebarEntityRow.Create(spec));
    }

    /// <summary>A bound ACTION shortcut. Resolved ONLY through <see cref="WaveeExtensionRegistry"/> — no new UI looks up
    /// <c>AppActions.All</c>. An unavailable target renders visible-but-disabled with its REASON as the tooltip, and the
    /// row's disabled state and <c>Execute</c>'s refusal come from one resolution so they cannot disagree.</summary>
    Element ActionRow(SidebarItemSpec item)
    {
        var binding = item.Action;
        var reg = _registry;
        var acts = _acts;

        string label = item.LabelOverride ?? "";
        var icon = default(IconRef);
        bool enabled = false;
        // The default reason covers the two host-shaped cases (no registry / no action bag yet): the row still renders,
        // disabled, saying so.
        string? reason = Loc.Get(SidebarPaneLoc.ExtensionNotNow);
        Action? click = null;

        if (binding is null)
        {
            // An Action item with no binding at all: a half-authored or hand-edited document.
            reason = Loc.Get(SidebarPaneLoc.ExtensionMissing);
        }
        else
        {
            var bound = binding;   // non-nullable local: Execute takes it by `in`
            if (reg is not null && reg.TryGetAction(bound, out var descriptor))
            {
                if (label.Length == 0) label = descriptor.Label();
                icon = descriptor.Icon();
            }
            if (reg is not null && acts is { } services)
            {
                var resolution = reg.Resolve(services, bound);
                enabled = resolution.Available;
                reason = resolution.ReasonLocKey is { } key ? Loc.Get(key) : null;
                var registry = reg;
                if (enabled) click = () => registry.Execute(services, in bound);
            }
        }
        if (label.Length == 0) label = Loc.Get(SidebarPaneLoc.ExtensionManage);

        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = label,
            Enabled = enabled,
            Density = SidebarDensity.Cozy,
            Height = RowHeight,
            Leading = SidebarPaneIcon.Leading(item.IconOverride, icon, enabled),
            Gap = 12f,             // keep the bare-glyph rhythm even though the leading slot is authored
            OnClick = click,
            MenuOverlay = _menuOverlay,
            Menu = RowMenu(item),
            Overflow = enabled && _menuOverlay is not null && _prefs is not null,
        };
        Element row = SidebarEntityRow.Create(spec);
        return reason is { Length: > 0 } why ? ToolTip.Wrap(row, why) : row;
    }

    /// <summary>The band's static selection mark: the same 3×16 accent bar, at the same indent and the same vertical
    /// centre, as a realized plan row's indicator (<see cref="SidebarSelectionPill"/> owns those numbers). STATIC on
    /// purpose — see the file header: registering it in the pane's route-keyed selection transaction would make the
    /// indicator fly between this band and the list below it.</summary>
    static Element Mark(Element row, bool selected, float height) => ZStack(
        row,
        new BoxEl
        {
            Width = SidebarSelectionPill.PillW,
            Height = SidebarSelectionPill.PillH,
            Margin = new Edges4(SidebarRowMetrics.IndentFor(0),
                                MathF.Max(0f, (height - SidebarSelectionPill.PillH) * 0.5f), 0f, 0f),
            Corners = CornerRadius4.All(SidebarSelectionPill.PillW * 0.5f),
            Fill = Tok.AccentDefault,
            Opacity = selected ? 1f : 0f,
            HitTestVisible = false,
        });

    // ── the per-tile context menu ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Right-click a shortcut: exactly two verbs. Dropping it is undoable through the pin-style toast (restored
    /// at its FORMER index); adding and reordering belong to the customizer, not to a context menu.</summary>
    Func<ContextMenuModel?>? RowMenu(SidebarItemSpec item)
    {
        if (_prefs is not { } prefs) return null;
        string id = item.Id;
        string name = TileName(item);
        return () => new ContextMenuModel(
        [
            new MenuFlyoutItem(Loc.Get(SidebarNavBandLoc.Remove), ActionIcons.Resolve(ActionIcons.Remove), true,
                () => RemoveShortcut(prefs, id, name)),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem(Loc.Get(SidebarNavBandLoc.Customize), ActionIcons.Resolve(ActionIcons.Rename), true,
                () => _go(SidebarLayoutMenu.CustomizeRoute, SidebarLayoutMenu.TopBarFocusArg)),
        ]);
    }

    /// <summary>Drop a shortcut + raise the undo toast whose action restores it at its former index. Snapshots the item
    /// BEFORE the removal, because the index is only knowable while it is still in the band (the <c>PinActions.Unpin</c>
    /// precedent — the toast IS the undo surface here, not the activity log).</summary>
    void RemoveShortcut(SidebarPreferences prefs, string itemId, string name)
    {
        int at = prefs.TopBarIndexOf(itemId);
        if (at < 0) return;
        var removed = prefs.TopBar[at];
        if (prefs.RemoveTopBarShortcut(itemId) != SidebarRejectReason.None) return;
        Toast.Show(Loc.Format(SidebarNavBandLoc.Removed, ("name", name)), new ToastOptions
        {
            Severity = InfoBarSeverity.Informational,
            ActionLabel = Loc.Get(Strings.Sidebar.Pin.Undo),
            // A FORWARD command, not prefs.Undo(): the undo ring is shared with the customizer, so replaying it from a
            // toast could revert an unrelated later edit. Re-adding at `at` restores the exact band (the item's id is
            // free again, so AddTopBarItem keeps it) and is itself one undoable step.
            OnAction = () => prefs.AddTopBarShortcut(removed, at),
        });
    }

    /// <summary>A shortcut's display name — the alias, then the retained last-known title, then whatever its target can
    /// name itself. Never blank: the toast and the rail tooltip both have to be a sentence.</summary>
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

    // ── the 56-DIP rail tiles ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One band tile in the rail, on the SHARED 40-DIP tile (<see cref="SidebarRailItem"/>) so it has the same
    /// hit box, corner ladder and selected treatment as every planned tile below it. A 56-DIP strip has no room for text,
    /// so the tooltip IS the label — which is why every tile passes one.</summary>
    Element RailTile(SidebarItemSpec item, in SidebarNavBandTile tile, string sel)
    {
        string key = "navband:" + item.Id;
        bool selected = SidebarNavBandModel.SelectsRoute(item, sel);
        string label = TileName(item);

        switch (tile.Kind)
        {
            case SidebarNavBandTileKind.Entity:
            {
                string? route = tile.RouteKey;
                Element art = SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, item.Key,
                    SidebarRailItem.ArtEdge, circular: item.EntityKind == SidebarEntityKind.Artist);
                Action? click = null;
                if (route is { Length: > 0 } target) click = () => _go(target, label);
                return SidebarRailItem.Art(key, art, selected, click,
                    click is null ? Loc.Get(SidebarPaneLoc.MissingEntity) : label);
            }

            case SidebarNavBandTileKind.Track:
            {
                string uri = item.Key;
                var player = _acts?.Svc?.Player;
                Element art = SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, uri, SidebarRailItem.ArtEdge);
                Action? click = null;
                if (player is not null) click = () => { _ = player.PlayTrackAsync(uri); };
                return SidebarRailItem.Art(key, art, false, click, label);
            }

            case SidebarNavBandTileKind.Action:
            {
                var binding = item.Action;
                var reg = _registry;
                var icon = default(IconRef);
                bool enabled = false;
                string? reason = Loc.Get(SidebarPaneLoc.ExtensionNotNow);
                Action? click = null;
                if (binding is null)
                {
                    reason = Loc.Get(SidebarPaneLoc.ExtensionMissing);
                }
                else
                {
                    var bound = binding;
                    if (reg is not null && reg.TryGetAction(bound, out var descriptor)) icon = descriptor.Icon();
                    if (reg is not null && _acts is { } services)
                    {
                        var resolution = reg.Resolve(services, bound);
                        enabled = resolution.Available;
                        reason = resolution.ReasonLocKey is { } locKey ? Loc.Get(locKey) : null;
                        var registry = reg;
                        if (enabled) click = () => registry.Execute(services, in bound);
                    }
                }
                // Disabled ⇒ the tooltip IS the reason (the visible-but-disabled contract); enabled ⇒ the label. The
                // rail tile has no dimmed state of its own, so a null click is the inert form.
                string tip = !enabled && reason is { Length: > 0 } why ? why : label;
                return SidebarRailItem.Icon(key, SidebarIcons.Glyph(item.IconOverride, icon.Glyph ?? Icons.More),
                    false, click, tip);
            }

            default:
            {
                var dest = ShellNav.Dest(item.Key);
                string route = item.Key;
                return SidebarRailItem.Icon(key, SidebarIcons.For(item, dest.Glyph), selected,
                    () => _go(route, null), label);
            }
        }
    }
}

/// <summary>The nav band's loc KEYS as literals, in one place — the <c>SidebarPaneLoc</c> / <c>CzLoc</c> precedent.
/// Spelled here rather than through the generated <c>Strings</c> table for the same reason those are: the generator only
/// emits members for keys already present in <c>assets/loc/en-US.json</c>, and a key that has not landed yet must render
/// loudly as <c>[key]</c> instead of breaking the build.
///
/// <para>The four <c>sidebar.topbar.*</c> keys are REUSED VERBATIM from the toolbar band (no schema change, no new
/// keys). <see cref="CapReached"/> is surfaced by the customizer's Top bar card, which is why it lives with the band's
/// other keys even though the band itself never shows it.</para></summary>
static class SidebarNavBandLoc
{
    public const string Remove = "sidebar.topbar.remove";
    public const string Removed = "sidebar.topbar.removed";
    public const string Customize = "sidebar.topbar.customize";
    public const string CapReached = "sidebar.topbar.capReached";
}
