using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI Expander: a clickable header row with a trailing chevron over a collapsible content panel. The header toggles
/// the open state (local <see cref="Component"/> state); the content panel is only rendered while expanded. The chevron
/// points down when collapsed and up when expanded. The whole control is a bordered, clipped card.
/// </summary>
public sealed class Expander : Component
{
    public string Header = "";
    public Element Content = new BoxEl { };
    public bool InitiallyExpanded = false;

    public static Element Create(string header, Element content, bool initiallyExpanded = false)
        => Embed.Comp(() => new Expander { Header = header, Content = content, InitiallyExpanded = initiallyExpanded });

    public override Element Render()
    {
        var (open, setOpen) = UseState(InitiallyExpanded);

        var header = new BoxEl
        {
            Direction = 0,
            MinHeight = 48f,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(16, 0, 16, 0),
            Fill = Tok.FillCardDefault,
            HoverFill = Tok.FillCardSecondary,
            OnClick = () => setOpen(!open),
            Role = AutomationRole.Expander,
            Children =
            [
                new TextEl(Header) { Size = 14f, Color = Tok.TextPrimary, Grow = 1 },
                new TextEl(open ? Icons.ChevronUp : Icons.ChevronDown) { Size = 12f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont },
            ],
        };

        var content = new BoxEl
        {
            Padding = Edges4.All(16),
            Fill = Tok.FillLayerDefault,
            Children = [Content],
        };

        return new BoxEl
        {
            Direction = 1,
            Corners = Radii.OverlayAll,
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault,
            ClipToBounds = true,
            Children = open ? new Element[] { header, content } : new Element[] { header },
        };
    }
}
