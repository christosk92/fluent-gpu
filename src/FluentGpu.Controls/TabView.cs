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

        var headers = new Element[Tabs.Count];
        for (int i = 0; i < Tabs.Count; i++)
        {
            int idx = i;
            bool isSel = idx == sel;
            headers[i] = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                Padding = new Edges4(12, 7, 12, 7),
                Corners = Radii.OverlayTop,
                Fill = isSel ? Tok.FillSolidBase : ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary,
                Role = AutomationRole.Tab,
                OnClick = () => setSel(idx),
                Children =
                [
                    new TextEl(Tabs[idx]) { Size = 14f, Color = isSel ? Tok.TextPrimary : Tok.TextSecondary },
                ],
            };
        }

        var strip = new BoxEl
        {
            Direction = 0, Gap = 4f,
            Padding = new Edges4(8, 6, 8, 0),
            Children = headers,
        };

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
            Children = [strip, content],
        };
    }
}
