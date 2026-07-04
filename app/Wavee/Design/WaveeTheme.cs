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
        _ => Tok.WarmPalette,
    };

    public static void ApplyPalette(string id, IAppSettings? settings = null)
    {
        Tok.Use(ResolvePalette(id), Tok.Theme);
        settings?.Set(WaveeSettings.PaletteId, id);
    }
}
