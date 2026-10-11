using FluentGpu.Dsl;
using FluentGpu.Theming;

namespace FluentGpu.Controls;

/// <summary>
/// The control style table's keys and resolver (EP15). <see cref="ControlStyleTable"/> lives in the engine (it is carried by
/// <c>ThemeDefinition.Controls</c> and the engine cannot see the controls); the keys live here, one per control that has a style
/// record, each declared by the control kind it restyles. A control resolves its default style with
/// <see cref="Resolve{TStyle}"/>: the active definition's entry when it has one (<c>Tok.Definition.Controls</c>, read at RENDER,
/// never captured at type load), else the control's built-in Fluent default (<c>Control.FluentStyle()</c>). So a theme swap
/// restyles every mounted control on the next frame, and an empty table (<see cref="ControlStyleTable.FluentDefault"/>) renders
/// exactly what the controls rendered before tables existed.
/// <para>Precedence for one control: an explicit per-instance <c>style:</c> argument, then the control's app-level static
/// override (<c>Slider.StyleOverride</c>, kept as the application's escape hatch), then the table entry, then the built-in
/// default. <c>Button.StyleHook</c> is gone: it was a global static no theme could carry; its replacement is
/// <see cref="ButtonStyle"/>.</para>
/// </summary>
public static class ControlStyles
{
    // ── keys (ids are stable: a theme file refers to them) ──────────────────────────────────────────────────────────

    /// <summary>Button: (appearance, size) to a style, or null to fall through to the composed default (the old StyleHook shape).</summary>
    public static readonly ControlStyleKey<Func<ButtonAppearance, ControlSize, Button.Style?>> ButtonStyle = new("button");
    public static readonly ControlStyleKey<Func<AppBarButton.Style>> AppBarButtonStyle = new("appbarbutton");
    public static readonly ControlStyleKey<Func<CaptionButton.Style>> CaptionMinMaxStyle = new("caption.minmax");
    public static readonly ControlStyleKey<Func<CaptionButton.Style>> CaptionCloseStyle = new("caption.close");
    public static readonly ControlStyleKey<Func<CheckBox.Style>> CheckBoxStyle = new("checkbox");
    public static readonly ControlStyleKey<Func<ColorPicker.Style>> ColorPickerStyle = new("colorpicker");
    public static readonly ControlStyleKey<Func<HyperlinkButton.Style>> HyperlinkButtonStyle = new("hyperlinkbutton");
    public static readonly ControlStyleKey<Func<IconButton.Style>> IconButtonStyle = new("iconbutton");
    public static readonly ControlStyleKey<Func<NumberBox.Style>> NumberBoxStyle = new("numberbox");
    public static readonly ControlStyleKey<Func<RadioButton.Style>> RadioButtonStyle = new("radiobutton");
    public static readonly ControlStyleKey<Func<RepeatButton.Style>> RepeatButtonStyle = new("repeatbutton");
    public static readonly ControlStyleKey<Func<Segmented.Style>> SegmentedStyle = new("segmented");
    public static readonly ControlStyleKey<Func<SettingsCard.Style>> SettingsCardStyle = new("settingscard");
    public static readonly ControlStyleKey<Func<Slider.Style>> SliderStyle = new("slider");
    public static readonly ControlStyleKey<Func<Splitter.Style>> SplitterStyle = new("splitter");
    public static readonly ControlStyleKey<Func<ToggleButton.Style>> ToggleButtonStyle = new("togglebutton");
    public static readonly ControlStyleKey<Func<ToggleSwitch.Style>> ToggleSwitchStyle = new("toggleswitch");

    /// <summary>The style for a control now: the active definition's provider, else <paramref name="builtIn"/> (the control's
    /// Fluent default). Reads <c>Tok.Definition</c> on every call; allocates only the style record itself.</summary>
    public static TStyle Resolve<TStyle>(ControlStyleKey<Func<TStyle>> key, Func<TStyle> builtIn) where TStyle : class
        => Tok.Definition.Controls.TryGet(key, out var provider) ? provider() : builtIn();

    /// <summary>The Button provider of the active definition, or null (the composed default applies).</summary>
    internal static Func<ButtonAppearance, ControlSize, Button.Style?>? ButtonProvider
        => Tok.Definition.Controls.TryGet(ButtonStyle, out var provider) ? provider : null;

    /// <summary>
    /// A table that registers EVERY control's built-in Fluent default explicitly. It renders identically to the empty table (that is
    /// what the control-style gates assert), and it is the starting point for a theme that restyles a few controls:
    /// <c>ControlStyles.FluentExplicit.With(ControlStyles.SliderStyle, static () =&gt; Slider.FluentStyle() with { ... })</c>.
    /// </summary>
    public static ControlStyleTable FluentExplicit { get; } = ControlStyleTable.Empty
        .With(ButtonStyle, static (_, _) => null)
        .With(AppBarButtonStyle, AppBarButton.FluentStyle)
        .With(CaptionMinMaxStyle, CaptionButton.FluentMinMax)
        .With(CaptionCloseStyle, CaptionButton.FluentClose)
        .With(CheckBoxStyle, CheckBox.FluentStyle)
        .With(ColorPickerStyle, ColorPicker.FluentStyle)
        .With(HyperlinkButtonStyle, HyperlinkButton.FluentStyle)
        .With(IconButtonStyle, IconButton.FluentStyle)
        .With(NumberBoxStyle, NumberBox.FluentStyle)
        .With(RadioButtonStyle, RadioButton.FluentStyle)
        .With(RepeatButtonStyle, RepeatButton.FluentStyle)
        .With(SegmentedStyle, Segmented.FluentStyle)
        .With(SettingsCardStyle, SettingsCard.FluentStyle)
        .With(SliderStyle, Slider.FluentStyle)
        .With(SplitterStyle, Splitter.FluentStyle)
        .With(ToggleButtonStyle, ToggleButton.FluentStyle)
        .With(ToggleSwitchStyle, ToggleSwitch.FluentStyle);
}
