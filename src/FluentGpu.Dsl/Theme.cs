using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// Process-wide visual theme (slice). Colors mirror WinUI 3 Fluent dark resources; <see cref="Accent"/> is set from the
/// OS by the host at startup. The full engine bakes Light/Dark blobs delivered through RenderContext + HighContrast via PAL.
/// </summary>
public static class Theme
{
    public static bool Dark = true;

    // Accent (dark theme): fill = SystemAccentColorLight2 (set from the OS AccentPalette; default ≈ Light2 of #0078D4).
    // TextOnAccentFillColorPrimary in DARK theme is BLACK (light fill → dark text). Hover/Pressed = same shade @ .9/.8.
    public static ColorF Accent = ColorF.FromRgba(0x60, 0xCD, 0xFF);              // SystemAccentColorLight2 (default)
    public static ColorF AccentText = ColorF.FromRgba(0x00, 0x00, 0x00);         // TextOnAccentFillColorPrimary (dark)
    public static ColorF AccentBorder = ColorF.FromRgba(0x00, 0x00, 0x00, 0x23); // AccentControlElevation top stop

    // Neutral control (standard button) — exact WinUI dark ControlFillColor* / ControlStrokeColor* / TextFillColor*.
    public static ColorF ControlFill = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F);          // ControlFillColorDefault
    public static ColorF ControlFillHover = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x15);     // ControlFillColorSecondary
    public static ColorF ControlFillPressed = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x08);   // ControlFillColorTertiary
    public static ColorF ControlBorder = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x18);        // ControlStrokeColorSecondary
    public static ColorF ControlText = ColorF.FromRgba(0xFF, 0xFF, 0xFF);                // TextFillColorPrimary

    // Window surface (SolidBackgroundFillColorBase).
    public static ColorF WindowBackground = ColorF.FromRgba(0x20, 0x20, 0x20);
    public static ColorF WindowText = ColorF.FromRgba(0xF2, 0xF2, 0xF2);

    // Theme holds design TOKENS only. Control default styles live with each control (e.g. Button.AccentStyle),
    // built from these tokens and overrideable there.
}
