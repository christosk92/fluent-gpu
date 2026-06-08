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

        // Trailing 32x32 rounded chevron button: only this gets the subtle hover/press, not the whole header.
        var chevron = new BoxEl
        {
            Width = 32f,                                          // ExpanderChevronButtonSize = 32
            Height = 32f,
            Margin = new Edges4(20, 0, 8, 0),                     // ExpanderChevronMargin = 20,0,8,0
            Corners = Radii.ControlAll,                           // ControlCornerRadius = 4
            HoverFill = Tok.FillSubtleSecondary,                  // ExpanderChevronPointerOverBackground
            PressedFill = Tok.FillSubtleTertiary,                 // ExpanderChevronPressedBackground (was missing)
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children =
            [
                // ExpanderChevronGlyphSize = 12. ExpanderChevronForeground = TextFillColorSecondary.
                new TextEl(open ? Icons.ChevronUp : Icons.ChevronDown) { Size = 12f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont },
            ],
        };

        var header = new BoxEl
        {
            Direction = 0,
            MinHeight = 48f,
            AlignItems = FlexAlign.Center,
            // Chevron handles the right inset via its own margin.
            Padding = new Edges4(16, 0, 0, 0),
            // Header background does not change on hover — stays CardBackgroundFillColorDefault at rest and hover.
            Fill = Tok.FillCardDefault,
            // WinUI Expander header (ToggleButton) carries a 1px CardStrokeColorDefault border (ExpanderHeaderBorderBrush).
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault,
            OnClick = () => setOpen(!open),
            Role = AutomationRole.Expander,
            Children =
            [
                new TextEl(Header) { Size = 14f, Color = Tok.TextPrimary, Grow = 1 },
                chevron,
            ],
        };

        var content = new BoxEl
        {
            Direction = 1,                       // vertical content area: stretch the child to full width so wrapping text reserves its true height
            Padding = Edges4.All(16),
            Fill = Tok.FillCardSecondary,
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
