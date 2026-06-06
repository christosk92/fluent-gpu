using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// Process-wide visual theme (slice). Colors mirror WinUI 3 Fluent dark resources; <see cref="Accent"/> is set from the
/// OS by the host at startup. The full engine bakes Light/Dark blobs delivered through RenderContext + HighContrast via PAL.
/// </summary>
public static class Theme
{
    public static bool Dark = true;

    // System accent (overwritten from the OS). Default = Windows default blue.
    public static ColorF Accent = ColorF.FromRgba(0x00, 0x78, 0xD4);
    public static ColorF AccentText = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
    public static ColorF AccentBorder = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x20);   // subtle top light line

    // Neutral control (standard button) — WinUI ControlFillColorDefault / ControlStrokeColorDefault over the backdrop.
    public static ColorF ControlFill = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x10);
    public static ColorF ControlBorder = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x18);
    public static ColorF ControlText = ColorF.FromRgba(0xFF, 0xFF, 0xFF);

    // Window surface.
    public static ColorF WindowBackground = ColorF.FromRgba(0x20, 0x20, 0x20);
    public static ColorF WindowText = ColorF.FromRgba(0xF2, 0xF2, 0xF2);

    // Default control styles — the framework's Fluent look. Computed from the live theme colors; override per-instance
    // by passing your own ButtonStyle, or swap these out wholesale. (HoverBackground/PressedBackground left auto.)
    public static ButtonStyle AccentButton => new() { Background = Accent, Foreground = AccentText, Border = AccentBorder };
    public static ButtonStyle StandardButton => new() { Background = ControlFill, Foreground = ControlText, Border = ControlBorder };
}
