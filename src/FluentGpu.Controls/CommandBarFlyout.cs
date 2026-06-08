using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI CommandBarFlyout: a trigger button that opens a contextual command toolbar — a horizontal row of
/// small icon buttons — anchored below it. Captures its own node as the flyout anchor; toggling re-click closes it.</summary>
public sealed class CommandBarFlyout : Component
{
    public string TriggerLabel = "Commands";

    public static Element Create(string triggerLabel = "Commands")
        => Embed.Comp(() => new CommandBarFlyout { TriggerLabel = triggerLabel });

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var h = UseRef<OverlayHandle?>(null);

        void Toggle()
        {
            if (h.Value is { IsOpen: true } o) { o.Close(); return; }
            h.Value = svc.Open(
                () => anchor.Value,
                () => new BoxEl
                {
                    Direction = 0,
                    Gap = 2f,
                    Padding = new Edges4(4, 4, 4, 4),
                    Children = new Element[]
                    {
                        IconBtn(Icons.Accept),
                        IconBtn(Icons.Share),
                        IconBtn(Icons.Tag),
                        IconBtn(Icons.Cancel),
                    },
                },
                FlyoutPlacement.BottomLeft);
        }

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            OnRealized = x => anchor.Value = x,
            OnClick = Toggle,
            Role = AutomationRole.Button,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 8f,
            MinHeight = 32f,
            Padding = new Edges4(11, 5, 11, 6),
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            HoverFill = Tok.FillControlSecondary,
            Children = new Element[]
            {
                new TextEl(TriggerLabel) { Size = 14f, Color = Tok.TextPrimary },
                new TextEl(Icons.ChevronDown) { Size = 10f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont },
            },
        };
    }

    static Element IconBtn(string glyph) => new BoxEl
    {
        Width = 34f,
        Height = 34f,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        HoverFill = Tok.FillSubtleSecondary,
        OnClick = () => { },
        Role = AutomationRole.Button,
        Children = new Element[]
        {
            new TextEl(glyph) { Size = 16f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont },
        },
    };
}
