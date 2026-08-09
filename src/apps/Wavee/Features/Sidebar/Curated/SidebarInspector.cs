using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// THE INSPECTOR (REVISION 2: "Inspector = Properties + Preview tabs; Preview renders Expanded / Rail / Drawer").
//
// One card, two tabs. It is the SAME component whether it is the 320-DIP column (Full/Compact tiers) or the Narrow tier's
// bottom sheet — `sheet` only drops the card chrome the sheet already draws.
sealed class SidebarInspector : Component
{
    readonly SidebarCustomizerPage _page;
    readonly bool _sheet;

    public SidebarInspector(SidebarCustomizerPage page, bool sheet)
    {
        _page = page; _sheet = sheet;
    }

    public override Element Render()
    {
        int tab = _page.InspectorTab.Value;
        bool persistentPreview = SidebarCustomizerLayout.PreviewInline(_page.Tier);

        // Mounted per tab (keyed), not both-at-once: the Preview mounts a REAL CuratedSidebar, and keeping it alive behind
        // the Properties tab would keep a second pane planning rows for no visible reason.
        Element body = !persistentPreview && tab == 1
            ? Embed.Comp(() => new SidebarLivePreview(_page)) with { Key = "tab:preview" }
            : Embed.Comp(() => new SidebarPropertyPanel(_page)) with { Key = "tab:props" };

        var head = new BoxEl
        {
            Direction = 1, Shrink = 0f,
            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.S),
            Children =
            [
                // At the Canvas tier the Preview is its OWN region, so this head is a plain region label; below it the
                // two tabs are the only way to reach the preview, so the selector stays.
                persistentPreview
                    ? CzRow.GroupLabel(Loc.Get(CzLoc.Properties))
                    : SelectorBar.Create(
                        [Loc.Get(CzLoc.Properties), Loc.Get(CzLoc.Preview)],
                        _page.InspectorTab),
            ],
        };

        // The region CARD belongs to SidebarCustomizerPage.Body (one plate per region); the SHEET draws its own chrome.
        // Either way this component paints no surface — only its inner padding differs.
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                head,
                Divider(),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                    Padding = _sheet
                        ? new Edges4(Spacing.M, Spacing.XS, Spacing.M, 0f)
                        : new Edges4(Spacing.S, Spacing.XS, Spacing.S, 0f),
                    Children = [body],
                },
            ],
        };
    }
}

/// <summary>
/// THE LIVE PREVIEW (§C4.8 + REVISION 2's Preview tab): the REAL <c>CuratedSidebar</c>, mounted from the SAME document
/// signal as the docked pane — never a copy (§C4.3). Editing the outline therefore repaints the live pane, the preview and
/// the property panel in one frame.
///
/// Three forms, one per mode, each its OWN mount under a mode-derived Key: <c>inDrawer</c> is a frozen ctor argument (the
/// component-props-freeze contract), so switching to Drawer must REMOUNT rather than mutate a signal. Expanded and Rail
/// differ only by the compact signal, but they are keyed the same way for one predictable rule.
///
/// NON-INTERACTIVE by construction: the wrapper is <c>HitTestVisible = false</c> and the navigation delegate is a no-op,
/// so a click inside the preview can never navigate or drag (interaction is the LIVE pane's job). The wrapper is also an
/// <c>IsolateLayout</c> boundary (the engine primitive behind <c>ItemsView</c>'s RepaintBoundary option), so a preview
/// repaint cannot relayout the outline beside it.
/// </summary>
sealed class SidebarLivePreview : Component
{
    const float PaneWidth = 320f;
    const float DrawerWidth = 360f;

    readonly SidebarCustomizerPage _page;

    /// <summary>The preview pane's own presented-compact + width signals (never the shell's — the docked pane must not
    /// change shape because someone looked at the rail).</summary>
    readonly Signal<bool> _compact = new(false);
    readonly Signal<float> _width = new(PaneWidth);

    /// <summary>The route the preview highlights. Seeded with the customizer's own route: while this page is open the
    /// active destination IS the customizer, so nothing in the preview is "current" — showing a stale selection would be
    /// a lie, and inventing one would be worse.</summary>
    readonly Signal<Route> _route = new(new Route(SidebarLayoutMenu.CustomizeRoute));

    public SidebarLivePreview(SidebarCustomizerPage page) => _page = page;

    public override Element Render()
    {
        int mode = _page.PreviewMode.Value;
        _ = _page.Prefs?.LayoutVersion.Value ?? 0;   // the preview is a projection of the document too

        bool compact = mode == 1;
        bool drawer = mode == 2;
        float width = drawer ? DrawerWidth : PaneWidth;

        UseLayoutEffect(() =>
        {
            _compact.SetIfChanged(compact);
            _width.SetIfChanged(width);
        }, DepKey.From(mode));

        // A no-op navigation delegate: the preview must never move the app. Stable (a field-less static lambda) so the
        // frozen ctor argument is the same instance across renders.
        Element pane = Embed.Comp(() => new CuratedSidebar(_route, NoNav, _compact, _width, drawer))
            with { Key = "preview-pane:" + mode };

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, Gap = Spacing.S,
            Children =
            [
                // "LIVE PREVIEW" + the pane/rail/drawer pick as one compact pill group (R3.2 item 5). Segmented rather
                // than SelectorBar: three one-word forms with equal weight, and the pill group survives the 360-DIP column
                // without the tab underline reading as "these are pages".
                new BoxEl
                {
                    Direction = 1, Shrink = 0f, Gap = Spacing.XS,
                    Children =
                    [
                        CzRow.GroupLabel(Loc.Get(CzLoc.Preview)),
                        Segmented.Create(
                            [
                                new SegmentedItem(Loc.Get(CzLoc.PreviewExpanded)),
                                new SegmentedItem(Loc.Get(CzLoc.PreviewRail)),
                                new SegmentedItem(Loc.Get(CzLoc.PreviewDrawer)),
                            ],
                            _page.PreviewMode),
                    ],
                },
                new BoxEl
                {
                    // The bounded card the pane lives in: the preview is a WINDOW onto the pane, so it clips and never
                    // lets the pane's own scroller drive the inspector's height.
                    Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 120f, ClipToBounds = true,
                    // The well wears the SIDEBAR'S OWN SURFACE (round-2 defect 7). The pane paints no background of its
                    // own — the SHELL's ground is what shows under it — so a well filled with a generic card colour let
                    // the app wash show straight through and the preview never read as a sidebar. `FloatingChrome` IS
                    // that ground as a value, which is the right one here: this well sits on the content pane, one rung
                    // up, so it must repaint the chrome rung rather than inherit it.
                    Corners = Radii.CardAll, Fill = Prop.Of(() => WaveeColors.FloatingChrome),
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    HitTestVisible = false,
                    // The engine's repaint-boundary primitive (what ItemsView's RepaintBoundary option sets): the preview
                    // pane's own invalidations cannot escape and relayout the outline beside it.
                    IsolateLayout = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                            // The rail mode is 56 DIP wide by contract; the pane draws it itself, so the wrapper only has
                            // to stop stretching it.
                            Width = compact ? 56f : width,
                            AlignSelf = FlexAlign.Start,
                            Children = [pane],
                        },
                    ],
                },
                // The footer says what the preview IS: one document, no apply step (§C4.3). It replaces the page subtitle
                // that used to be repeated here, which said nothing about the preview at all.
                new TextEl(Loc.Get(CzLoc.PreviewHint))
                {
                    Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
                },
            ],
        };
    }

    static void NoNav(string key, string? arg) { }
}
