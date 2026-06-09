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
/// WinUI via the per-state foreground ramps. Used inside a CommandBar; the whole control is one click target that
/// flips the checked state. <see cref="BoxEl.IsEnabled"/> gates hit-test/focus/keyboard; the disabled resting
/// surface/foreground stay control-chosen.</summary>
public sealed class AppBarToggleButton : Component
{
    public string Glyph = "";
    public string Label = "";
    public bool InitialChecked = false;
    public bool IsEnabled = true;

    public static Element Create(string glyph, string label, bool initiallyChecked = false, bool isEnabled = true) =>
        Embed.Comp(() => new AppBarToggleButton { Glyph = glyph, Label = label, InitialChecked = initiallyChecked, IsEnabled = isEnabled });

    public override Element Render()
    {
        var (on, setOn) = UseState(InitialChecked);
        bool enabled = IsEnabled;

        // Resting fill/foreground stay control-chosen per logical checked state; the engine IsEnabled gate stops
        // hit-test/focus/keyboard and drives the TextEl DisabledColor ramp. WinUI dims the foreground on press
        // (TextSecondary unchecked / TextOnAccentSecondary checked) via a per-state foreground — now carried by the
        // TextEl PressedColor ramp the interactive BoxEl inherits.
        ColorF fg = enabled ? (on ? Tok.TextOnAccentPrimary : Tok.TextPrimary)
                            : (on ? Tok.TextOnAccentDisabled : Tok.TextDisabled);
        ColorF pressedFg = on ? Tok.TextOnAccentSecondary : Tok.TextSecondary;
        ColorF disabledFg = on ? Tok.TextOnAccentDisabled : Tok.TextDisabled;
        ColorF fill = enabled ? (on ? Tok.AccentDefault : Tok.FillSubtleTransparent)
                              : (on ? Tok.AccentDisabled : Tok.FillSubtleTransparent);

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
            HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = on ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
            // Checked = accent elevation border (AppBarToggleButtonBorderBrushChecked); unchecked/disabled = transparent.
            BorderWidth = (on && enabled) ? 1f : 0f,
            BorderBrush = (on && enabled) ? Tok.AccentControlElevationBorder : null,
            IsEnabled = enabled,
            OnClick = () => setOn(!on),
            Role = AutomationRole.ToggleButton,
            Children =
            [
                new TextEl(Glyph) { Size = 16, Color = fg, PressedColor = pressedFg, DisabledColor = disabledFg, FontFamily = Theme.IconFont },
                new TextEl(Label) { Size = 12, Color = fg, PressedColor = pressedFg, DisabledColor = disabledFg },
            ],
        };
    }
}
