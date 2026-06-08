using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>A WinUI ContentDialog: a modal dialog presented as a dimmed <see cref="Tok.FillSmoke"/> scrim over the host
/// content with a centered card (title, message, and a primary + Cancel button). Self-contained: a trigger button
/// toggles local <c>open</c> state; clicking the scrim, the primary, or Cancel dismisses it.</summary>
public sealed class ContentDialog : Component
{
    public string TriggerLabel = "Show dialog";
    public string Title = "Title";
    public string Message = "";
    public string PrimaryText = "OK";

    public static Element Create(string triggerLabel, string title, string message, string primaryText = "OK")
        => Embed.Comp(() => new ContentDialog { TriggerLabel = triggerLabel, Title = title, Message = message, PrimaryText = primaryText });

    public override Element Render()
    {
        var (open, setOpen) = UseState(false);

        // The trigger lives at the top-left and is always present.
        var trigger = new BoxEl
        {
            AlignItems = FlexAlign.Start,
            Children = [Button.Accent(TriggerLabel, () => setOpen(true))],
        };

        var children = new List<Element> { trigger };

        if (open)
        {
            // Footer: equal full-width columns. Each button stretches (Grow=1) so the two share the row evenly with an
            // 8px gap — never right-aligned auto-width. Both default to the standard style; only the designated default
            // action (PrimaryText) is accent. A 1px separator above + a slightly distinct row bg set the footer apart.
            var buttonRow = new BoxEl
            {
                Direction = 0,
                Gap = 8f,
                AlignItems = FlexAlign.Stretch,
                Children =
                [
                    Button.Accent(PrimaryText, () => setOpen(false)) with { Grow = 1f, Justify = FlexJustify.Center },
                    Button.Standard("Cancel", () => setOpen(false)) with { Grow = 1f, Justify = FlexJustify.Center },
                ],
            };

            var card = new BoxEl
            {
                Direction = 1,
                MinWidth = 320f,
                MaxWidth = 548f,
                MinHeight = 184f,   // ContentDialogMinHeight
                MaxHeight = 756f,   // ContentDialogMaxHeight
                Corners = Radii.OverlayAll,
                Fill = Tok.FillSolidBase,
                BorderColor = Tok.StrokeCardDefault,
                BorderWidth = 1f,
                Shadow = Elevation.Flyout,
                Children =
                [
                    // Content region (24px padding).
                    new BoxEl
                    {
                        Direction = 1,
                        Padding = Edges4.All(24f),
                        Gap = 12f,
                        Children =
                        [
                            new TextEl(Title) { Size = 20f, Bold = true, Color = Tok.TextPrimary },
                            new TextEl(Message) { Size = 14f, Color = Tok.TextPrimary },
                        ],
                    },
                    // 1px separator line above the footer (ContentDialogSeparatorBorderBrush = CardStrokeColorDefault).
                    new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault },
                    // CommandSpace: WinUI's command grid inherits the dialog background (TemplateBinding Background),
                    // so no distinct footer fill. Padding 24 all sides == ContentDialogPadding + the 24px CommandSpace top gap.
                    new BoxEl
                    {
                        Direction = 1,
                        Padding = Edges4.All(24f),
                        Children = [buttonRow],
                    },
                ],
            };

            // Full-bleed scrim doubles as the centering container; clicking it dismisses.
            var scrim = new BoxEl
            {
                Grow = 1f,
                Fill = Tok.FillSmoke,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                OnClick = () => setOpen(false),
                Children = [card],
            };
            children.Add(scrim);
        }

        return new BoxEl
        {
            Grow = 1f,
            MinHeight = 240f,
            ZStack = true,
            Children = children.ToArray(),
        };
    }
}
