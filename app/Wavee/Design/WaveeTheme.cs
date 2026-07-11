using FluentGpu.Dsl;

namespace Wavee;

/// <summary>Wavee theme bootstrap helpers (palette id → <see cref="ThemePalette"/>, shared by Program + shell).</summary>
static class WaveeTheme
{
    public static ThemePalette ResolvePalette(string id) => id switch
    {
        "slate" => Tok.SlatePalette,
        "neutral" => Tok.NeutralPalette,
        "accent" => Tok.AccentTintedPalette,
        _ => Tok.NeutralPalette,
    };

    public static void ApplyPalette(string id, IAppSettings? settings = null)
    {
        Tok.Use(ResolvePalette(id), Tok.Theme);
        settings?.Set(WaveeSettings.PaletteId, id);
    }

    /// <summary>Apply + persist the theme-mode preference (0 System · 1 Light · 2 Dark) — the same resolution Program.cs
    /// runs at startup and WaveeApp runs on a live OS flip. System re-reads the OS theme (and accent) immediately.</summary>
    public static void ApplyThemeMode(int mode, IAppSettings? settings = null)
    {
        var kind = mode switch
        {
            1 => ThemeKind.Light,
            2 => ThemeKind.Dark,
            _ => FluentGpu.FluentApp.SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark,
        };
        Tok.Use(ResolvePalette(settings?.Get(WaveeSettings.PaletteId) ?? Tok.Palette.Id), kind);
        if (mode == 0)
        {
            // Prefer the exact OS accent ramp (theme-aware fills); else the base accent (SetAccent derives a ramp).
            if (FluentGpu.FluentApp.SystemAccentRamp() is { } ramp) Tok.SetAccent(in ramp);
            else if (FluentGpu.FluentApp.SystemAccent() is { } a) Tok.SetAccent(a);
        }
        settings?.Set(WaveeSettings.ThemeMode, mode);
    }
}
