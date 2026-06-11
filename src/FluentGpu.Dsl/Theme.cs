using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// Back-compat facade over the semantic token table (<see cref="Tok"/>). The flat names here mirror what existing call
/// sites use; each forwards to the active <see cref="TokenSet"/>, so a theme swap (<see cref="Tok.Use"/>) re-themes them
/// too. New code should prefer <see cref="Tok"/> / <see cref="Type"/> directly. The host still injects the OS accent +
/// Mica background through the <see cref="Accent"/>/<see cref="WindowBackground"/> setters.
/// </summary>
public static class Theme
{
    public static bool Dark
    {
        get => Tok.Theme == ThemeKind.Dark;
        set => Tok.Use(value ? ThemeKind.Dark : ThemeKind.Light);
    }

    public static ColorF Accent { get => Tok.AccentDefault; set => Tok.SetAccent(value); }
    public static ColorF AccentText => Tok.TextOnAccentPrimary;
    public static ColorF AccentBorder => Tok.StrokeControlOnAccentDefault;

    public static ColorF ControlFill => Tok.FillControlDefault;
    public static ColorF ControlFillHover => Tok.FillControlSecondary;
    public static ColorF ControlFillPressed => Tok.FillControlTertiary;
    public static ColorF ControlBorder => Tok.StrokeControlSecondary;
    public static ColorF ControlText => Tok.TextPrimary;

    public static ColorF WindowBackground { get => Tok.WindowBackground; set => Tok.SetWindowBackground(value); }
    public static ColorF WindowText => Tok.TextPrimary;

    // Fonts. BodyFont is the default text face; IconFont is the symbol face for Icon()/IconButton/NavigationView.
    public static string BodyFont = "Segoe UI";
    public static string IconFont = "Segoe Fluent Icons";
}

/// <summary>Segoe Fluent glyph codepoints shared across LAYERS (a control and the engine recorder), so every
/// consumer renders literally the same icon. Control-local glyphs stay at their call sites (or
/// <c>Controls.Icons</c>); promote one here only when a layer below Controls needs it — today the scrollbar
/// arrows, drawn both by the ScrollBar/AnnotatedScrollBar templates and by the engine's overlay scrollbar
/// (<c>SceneRecorder.EmitScrollbar</c>).</summary>
public static class IconGlyphs
{
    public const string CaretLeftSolid8 = "";    // HorizontalDecrement (ScrollBar_themeresources.xaml:301)
    public const string CaretRightSolid8 = "";   // HorizontalIncrement (:258)
    public const string CaretUpSolid8 = "";      // VerticalDecrement (:387)
    public const string CaretDownSolid8 = "";    // VerticalIncrement (:344)
}
