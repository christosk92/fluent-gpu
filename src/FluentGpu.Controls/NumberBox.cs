using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI NumberBox in spinner mode: a read-only value display flanked by down/up spin buttons that
/// decrement/increment the value by <see cref="Step"/>. No inline text editing — interaction is purely via the spinners.</summary>
public sealed class NumberBox : Component
{
    public double Initial = 0;
    public double Step = 1;

    public static Element Create(double initial = 0, double step = 1)
        => Embed.Comp(() => new NumberBox { Initial = initial, Step = step });

    public override Element Render()
    {
        var (val, setVal) = UseState(Initial);

        var value = new BoxEl
        {
            Padding = new Edges4(11, 0, 11, 0),
            Grow = 1f,
            Children = [new TextEl(val.ToString("0.##")) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f }],
        };

        var down = new BoxEl
        {
            Width = 32f, Height = 30f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverFill = Tok.FillSubtleSecondary,
            OnClick = () => setVal(val - Step),
            Children = [new TextEl(Icons.ChevronDown) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
        };

        var up = new BoxEl
        {
            Width = 32f, Height = 30f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverFill = Tok.FillSubtleSecondary,
            OnClick = () => setVal(val + Step),
            Children = [new TextEl(Icons.ChevronUp) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
        };

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 32f,
            Width = 160f,
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            ClipToBounds = true,
            Role = AutomationRole.Text,
            Children = [value, down, up],
        };
    }
}
