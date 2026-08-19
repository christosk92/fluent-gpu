using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Features.Browse;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>THE one masthead ("Browse › Title") — mounted ONCE, above ContentHost's KeepAlive page-swap boundary,
/// so a page swap never double-exposes it (the bug G2c fixes: every browse-family page used to render its own copy
/// of <c>BrowseMasthead</c>, so each nav painted two "Browse ›" lines for the length of the swap).
///
/// <para>Content predicate: the band shows for a Browse-family route (<see cref="Wavee.Features.Browse.BrowseRoutes"/>/
/// <see cref="BrowseSectionRoutes"/>/<see cref="HomeSectionRoutes"/>) — even before a page publishes, the route's own
/// Arg carries a usable title via <see cref="DrillTrail"/> — OR a page has PUBLISHED to <see cref="ShellMasthead.Slot"/>
/// (owner non-null), which is how Search's empty-state directory gets a masthead despite having no route of its own.
/// A bare <c>search</c> route with nothing published collapses to zero height.</para>
///
/// <para>The pages themselves render NO masthead — they publish (title, caption, tools) to
/// <see cref="ShellMasthead.Slot"/> via the owner-token'd three-leg lifecycle (the ShellMaterial pattern), and this
/// band is the single renderer. The old per-page renderer (<c>BrowseMasthead</c>) is deleted; its Zune
/// trail-as-title grammar lives on inline below.</para></summary>
sealed class ShellMastheadBand : Component
{
    readonly Signal<Route> _route;
    public ShellMastheadBand(Signal<Route> route) => _route = route;

    public override Element Render()
    {
        var store = UseContext(ShellMasthead.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var route = _route.Value;
        var state = store?.Value;   // subscribe — a page's publish/clear must re-render this band

        bool familyRoute = BrowseRoutes.Is(route.Name)
            || BrowseSectionRoutes.Is(route.Name) || HomeSectionRoutes.Is(route.Name);
        bool published = state is { Owner: not null };
        if (!familyRoute && !published)
            return Collapsed();

        // liveTitle only counts when a page has actually published (an owner-less/default state must never override
        // the route-derived trail with a stale Title from a previous owner's leftover record).
        string? liveTitle = published ? state!.Title : null;
        var trail = DrillTrail.Of(route.Name, route.Arg, liveTitle);
        // The route-less directory case (Search's empty state — see the class doc-comment): an empty trail but a
        // live publish still has a title to show, just no ancestry to prefix it with.
        string? title = liveTitle ?? (trail.Count > 0 ? trail[^1].Label : null);
        if (string.IsNullOrWhiteSpace(title))
            return Collapsed();

        string? caption = published ? state!.Caption : null;
        // Tools row is UNCONDITIONAL (`tools ?? new BoxEl()`, BrowseMasthead's own idiom) — a page flipping
        // ToolsVisible must never change THIS subtree's shape, which would remount the band and replay a "mount"
        // that (unlike a page) it never has.
        Element? tools = published && state!.ToolsVisible
            ? Button.Create(Loc.Get(Strings.Browse.ShowAll), state.ToolsAction ?? (static () => { }),
                ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !state.ToolsLoading)
            : null;

        var lines = new List<Element>(2) { TitleRow(trail, go, title!) };
        if (!string.IsNullOrEmpty(caption))
            lines.Add(Caption(caption) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });

        var masthead = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children = lines.ToArray(),
        };
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.End,
            Padding = new Edges4(BrowseLayout.FrameX, BrowseLayout.FrameTop, BrowseLayout.FrameX, 0f),
            Children = [masthead, tools ?? new BoxEl()],
        };
    }

    // The collapsed shape MUST be the same node kind (a bare BoxEl) every time — never a different element type and
    // never absent — because this component sits ABOVE ContentHost's KeepAlive boundary: appearing/disappearing here
    // would not touch KeepAlive itself, but a conditionally-absent sibling is still the kind of shape change the
    // always-mounted contract exists to rule out for every node in this column. Height snaps to/from 0 with no
    // animation — the pages' own 2-frame activation suppression does not cover this node.
    static Element Collapsed() => new BoxEl { Height = 0f, MinWidth = 0f };

    /// <summary>The trail-as-title row (BrowseMasthead's ZUNE grammar, moved here verbatim): every crumb but the
    /// last renders dimmed + clickable, followed by a <c>›</c> separator, then the CURRENT title. The prefix carries
    /// NO Enter/Transition — it is stable ground that must not replay on every drill — and is keyed by the PARENT
    /// crumb's label so it never remounts while drilling under the same parent (Home › A → Home › B keeps one
    /// "Home" node). Only the current title animates, via the keyed <see cref="TitleSwap"/> ZStack below.</summary>
    static Element TitleRow(IReadOnlyList<DrillCrumb> trail, Action<string, string?> go, string title)
    {
        if (trail.Count <= 1)
            return TitleSwap(title);

        var segs = new List<Element>((trail.Count - 1) * 2);
        for (int i = 0; i < trail.Count - 1; i++)
        {
            var crumb = trail[i];
            string? routeName = crumb.RouteName;
            string? routeArg = crumb.RouteArg;
            var label = WaveeType.SurfaceDisplay(crumb.Label) with
            {
                Color = Tok.TextTertiary, HoverColor = Tok.TextSecondary, PressedColor = Tok.TextTertiary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
            };
            // OnClick/Role/Focusable/Cursor/FocusVisualMargin are BoxEl-only interactive props — the crumb wraps its
            // own TextEl label in a BoxEl for the click target, same as BreadcrumbBar.
            segs.Add(new BoxEl
            {
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                OnClick = routeName is { Length: > 0 } ? () => go(routeName, routeArg) : null,
                Children = [label],
            });
            segs.Add(WaveeType.SurfaceDisplay("›") with { Color = Tok.TextTertiary, Shrink = 0f });
        }
        var prefix = new BoxEl
        {
            Key = "masthead-prefix:" + trail[0].Label,
            Direction = 0, AlignItems = FlexAlign.End, Gap = Spacing.S,
            Children = segs.ToArray(),
        };

        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.End, Gap = Spacing.S,
            Children = [prefix, TitleSwap(title)],
        };
    }

    /// <summary>The current title as a keyed ZStack swap (the <c>DetailTrailing.CompactStatTile</c> value idiom): the
    /// OUTGOING title (a different Key) and the INCOMING one overlap inside the band instead of relabelling in place —
    /// old slides toward -X while the new one arrives from +X, Zune-style. Built from <see cref="MotionTok"/> (no bare
    /// millisecond literal); reduced motion is the engine's own KeepFade policy on the token, never a manual branch
    /// here.</summary>
    static readonly LayoutTransition TitleSwapAnim = new(
        TransitionChannels.Opacity,
        MotionTok.StandardEnter.ToDynamics(),
        Enter: new EnterExit(Dx: Spacing.XXL, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: -Spacing.XXL, Opacity: 0f, Active: true));

    static Element TitleSwap(string title) => ZStack(new BoxEl
    {
        Key = "masthead-title:" + title,
        Animate = TitleSwapAnim,
        Children =
        [
            WaveeType.SurfaceDisplay(title) with { MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        ],
    }) with { Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f };
}
