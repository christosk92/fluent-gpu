using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>A WinUI AppBarToggleButton: like <see cref="AppBarButton"/> (a vertically-stacked 16px icon glyph
/// over a 12px label) but a stateful toggle. When checked it becomes a SOLID accent pill
/// (<see cref="Tok.AccentDefault"/> fill, white <see cref="Tok.TextOnAccentPrimary"/> icon/label, with
/// <see cref="Tok.AccentSecondary"/>/<see cref="Tok.AccentTertiary"/> hover/press) and gains the accent
/// elevation-border (AppBarToggleButtonBorderBrushChecked); unchecked it is transparent with the standard subtle
/// hover/press states. Press dims the foreground (TextSecondary unchecked, TextOnAccentSecondary checked) to match
/// WinUI. Used inside a CommandBar; the whole control is one click target that flips the checked state.</summary>
public sealed class AppBarToggleButton : Component
{
    public string Glyph = "";
    public string Label = "";
    public bool InitialChecked = false;
    public bool Disabled = false;

    public static Element Create(string glyph, string label, bool initiallyChecked = false, bool disabled = false) =>
        Embed.Comp(() => new AppBarToggleButton { Glyph = glyph, Label = label, InitialChecked = initiallyChecked, Disabled = disabled });

    public override Element Render()
    {
        var (on, setOn) = UseState(InitialChecked);
        bool dis = Disabled;

        // Disabled foreground/fill resolve up front (engine BoxEl has no IsEnabled gate). Press-state foreground dimming
        // (TextSecondary / TextOnAccentSecondary) lives on a per-state foreground in WinUI which the BoxEl text color
        // can't carry; the PressedFill swap conveys the press, and the rest/checked foreground tokens are applied here.
        ColorF fg = dis ? (on ? Tok.TextOnAccentDisabled : Tok.TextDisabled)
                        : (on ? Tok.TextOnAccentPrimary : Tok.TextPrimary);
        ColorF fill = dis ? (on ? Tok.AccentDisabled : Tok.FillControlDisabled)
                          : (on ? Tok.AccentDefault : Tok.FillSubtleTransparent);

        return new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Gap = 4,
            MinWidth = 68,
            MinHeight = 48,
            Padding = new Edges4(4, 6, 4, 6),
            Corners = Radii.ControlAll,
            Fill = fill,
            HoverFill = dis ? fill : (on ? Tok.AccentSecondary : Tok.FillSubtleSecondary),
            PressedFill = dis ? fill : (on ? Tok.AccentTertiary : Tok.FillSubtleTertiary),
            // Checked = accent elevation border (AppBarToggleButtonBorderBrushChecked); unchecked/disabled = transparent.
            BorderWidth = (on && !dis) ? 1f : 0f,
            BorderBrush = (on && !dis) ? Tok.AccentControlElevationBorder : null,
            OnClick = dis ? null : () => setOn(!on),
            HitTestVisible = !dis,
            Role = AutomationRole.ToggleButton,
            Children =
            [
                new TextEl(Glyph) { Size = 16, Color = fg, FontFamily = Theme.IconFont },
                new TextEl(Label) { Size = 12, Color = fg },
            ],
        };
    }
}
