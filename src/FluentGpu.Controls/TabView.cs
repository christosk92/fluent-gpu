using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI TabView: a horizontal strip of tab headers atop the selected tab's content. The selected header uses an
/// elevated solid fill with top-rounded corners and meets the content area flush; unselected headers are transparent with a
/// subtle hover. Selection is local state.</summary>
public sealed class TabView : Component
{
    public IReadOnlyList<string> Tabs = [];

    public static Element Create(IReadOnlyList<string> tabs) => Embed.Comp(() => new TabView { Tabs = tabs });

    public override Element Render()
    {
        var (sel, setSel) = UseState(0);

        var headers = new Element[Tabs.Count + 1];
        for (int i = 0; i < Tabs.Count; i++)
        {
            int idx = i;
            bool isSel = idx == sel;
            headers[i] = new BoxEl
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
                OnClick = () => setSel(idx),
                Children =
                [
                    // Selected header = TabViewItemHeaderForegroundSelected (TextPrimary); unselected = TextSecondary.
                    // (WinUI sets the selected weight to SemiBold; the engine TextEl exposes only Bold, not a weight ramp.)
                    new TextEl(Tabs[idx]) { Size = 12f, Color = isSel ? Tok.TextPrimary : Tok.TextSecondary },
                    // Per-tab close button (Segoe Fluent Icons Cancel). CloseButtonForeground = TextPrimary.
                    new BoxEl
                    {
                        Direction = 0, Width = 32f, Height = 24f,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = Radii.ControlAll,
                        HoverFill = Tok.FillSubtleSecondary,
                        PressedFill = Tok.FillSubtleTertiary,
                        Role = AutomationRole.Button,
                        OnClick = () => { if (sel >= idx && sel > 0) setSel(sel - 1); },
                        Children = [new TextEl(Icons.Cancel) { Size = 12f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont }],
                    },
                ],
            };
        }

        // Trailing "+" Add button. TabViewButtonForeground = TextPrimary.
        headers[Tabs.Count] = new BoxEl
        {
            Direction = 0, Width = 32f, Height = 24f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            AlignSelf = FlexAlign.Center,
            Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button,
            OnClick = () => { },
            Children = [new TextEl(Icons.Add) { Size = 12f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont }],
        };

        var strip = new BoxEl
        {
            Direction = 0, Gap = 4f,
            AlignItems = FlexAlign.End,
            Padding = new Edges4(8, 6, 8, 0),
            // WinUI draws a 1px bottom divider (StrokeDividerDefault) under the strip; the engine BoxEl border is uniform
            // (4 edges, no per-edge), so a thin full-width divider row is appended below the headers instead of a box border.
            Children = headers,
        };

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

        return new BoxEl
        {
            Direction = 1, Grow = 1f,
            Children = [strip, stripDivider, content],
        };
    }
}
