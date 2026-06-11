using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI TabView: a horizontal strip of tab headers atop the selected tab's content. The selected header uses an
/// elevated solid fill with top-rounded corners and meets the content area flush; unselected headers are transparent with a
/// subtle hover. Selection is local state. Per-part restyling goes through <see cref="Parts"/> (see
/// <see cref="TemplateParts"/> for the contract).</summary>
public sealed class TabView : Component
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>Each tab's clickable header box (WinUI TabViewItem). The SAME modifier runs for every tab — branch on
    /// app state for per-tab styling. Owned: OnClick (select), Role.</summary>
    public const string PartTabItem = "TabItem";
    /// <summary>Each tab's header label — a <see cref="TextEl"/>, so style it via the generic map:
    /// <c>Parts.Set&lt;TextEl&gt;(TabView.PartTabLabel, t =&gt; t with { … })</c>. Owned: nothing (pure styling).</summary>
    public const string PartTabLabel = "TabLabel";
    /// <summary>Each tab's trailing close button (WinUI TabViewItem CloseButton). Owned: OnClick (close), Role.</summary>
    public const string PartTabCloseButton = "TabCloseButton";
    /// <summary>The trailing "+" add button (WinUI AddButton). Owned: OnClick, Role.</summary>
    public const string PartAddButton = "AddButton";
    /// <summary>The header strip row hosting the tabs + add button (the 1px divider below it is separate chrome).
    /// Owned: Children.</summary>
    public const string PartStrip = "Strip";
    /// <summary>The selected tab's content area. Owned: Children.</summary>
    public const string PartContent = "Content";

    public IReadOnlyList<string> Tabs = [];
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    public static Element Create(IReadOnlyList<string> tabs) => Embed.Comp(() => new TabView { Tabs = tabs });

    public override Element Render()
    {
        var (sel, setSel) = UseState(0);

        var headers = new Element[Tabs.Count + 1];
        for (int i = 0; i < Tabs.Count; i++)
        {
            int idx = i;
            bool isSel = idx == sel;
            Action select = () => setSel(idx);
            Action close = () => { if (sel >= idx && sel > 0) setSel(sel - 1); };

            // Selected header = TabViewItemHeaderForegroundSelected (TextPrimary); unselected = TextSecondary.
            // (WinUI sets the selected weight to SemiBold; the engine TextEl exposes only Bold, not a weight ramp.)
            var label = new TextEl(Tabs[idx]) { Size = 12f, Color = isSel ? Tok.TextPrimary : Tok.TextSecondary };

            // Per-tab close button (Segoe Fluent Icons Cancel). CloseButtonForeground = TextPrimary.
            var closeButton = new BoxEl
            {
                Direction = 0, Width = 32f, Height = 24f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button,
                OnClick = close,
                Children = [new TextEl(Icons.Cancel) { Size = 12f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont }],
            };
            closeButton = Parts.Apply(PartTabCloseButton, closeButton) with { OnClick = close, Role = AutomationRole.Button };

            var item = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                Padding = new Edges4(8, 3, 4, 3),
                MinHeight = 32f,                                  // TabViewItemMinHeight = 32
                Corners = Radii.OverlayTop,
                // Selected: TabViewItemHeaderBackgroundSelected = SolidBackgroundFillColorTertiary — FillCardDefault is the
                // nearest light layered fill (FillSolidBase reads too dark for the elevated selected tab). Unselected:
                // TabViewItemHeaderBackground (transparent layer-on-Mica). Hover: LayerOnMicaBaseAltFillColorSecondary →
                // FillControlSecondary (closer than FillSubtleSecondary); no hover change on the already-selected tab.
                Fill = isSel ? Tok.FillCardDefault : ColorF.Transparent,
                HoverFill = isSel ? Tok.FillCardDefault : Tok.FillControlSecondary,
                PressedFill = isSel ? Tok.FillCardDefault : Tok.FillControlTertiary,
                Role = AutomationRole.Tab,
                OnClick = select,
                Children = [Parts.Apply(PartTabLabel, label), closeButton],
            };
            // Parts: restyle anything; the select mechanics always win.
            headers[i] = Parts.Apply(PartTabItem, item) with { OnClick = select, Role = AutomationRole.Tab };
        }

        // Trailing "+" Add button. TabViewButtonForeground = TextPrimary.
        Action add = () => { };
        var addButton = new BoxEl
        {
            Direction = 0, Width = 32f, Height = 24f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            AlignSelf = FlexAlign.Center,
            Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button,
            OnClick = add,
            Children = [new TextEl(Icons.Add) { Size = 12f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont }],
        };
        headers[Tabs.Count] = Parts.Apply(PartAddButton, addButton) with { OnClick = add, Role = AutomationRole.Button };

        var strip = new BoxEl
        {
            Direction = 0, Gap = 4f,
            AlignItems = FlexAlign.End,
            Padding = new Edges4(8, 6, 8, 0),
            // WinUI draws a 1px bottom divider (StrokeDividerDefault) under the strip; the engine BoxEl border is uniform
            // (4 edges, no per-edge), so a thin full-width divider row is appended below the headers instead of a box border.
            Children = headers,
        };
        strip = Parts.Apply(PartStrip, strip) with { Children = headers };   // structure = the tab headers + add button

        // 1px baseline divider beneath the tab strip (TabView*Separator / StrokeDividerDefault).
        var stripDivider = new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault };

        var content = new BoxEl
        {
            Grow = 1f,
            Padding = new Edges4(16, 16, 16, 16),
            Fill = Tok.FillSolidBase,
            Children =
            [
                new TextEl(Tabs.Count > 0 ? $"Content of {Tabs[sel]}" : "")
                {
                    Size = 14f, Color = Tok.TextPrimary,
                },
            ],
        };
        content = Parts.Apply(PartContent, content) with { Children = content.Children };

        return new BoxEl
        {
            Direction = 1, Grow = 1f,
            Children = [strip, stripDivider, content],
        };
    }
}
