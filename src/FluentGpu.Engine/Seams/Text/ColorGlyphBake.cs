using FluentGpu.Foundation;

namespace FluentGpu.Text;

/// <summary>The two colour decisions the Windows glyph renderer makes while BAKING a colour-emoji (COLR/CPAL) run
/// into its per-quad <c>Colors</c> array, extracted here so the headless harness can pin them (the renderer itself is
/// DirectWrite-bound and cannot be instantiated in <c>FluentGpu.VerticalSlice</c>). Both are pure and allocation-free.
/// <para>Contract shared with the replay side: a per-quad colour with <c>A == 0</c> means "inherit the run colour at
/// replay" — that is how a plain (non-span, non-emoji) glyph and a FOREGROUND emoji layer (CPAL palette index
/// <c>0xFFFF</c>) both follow the run's fill, theme changes and fades without re-baking.</para></summary>
public static class ColorGlyphBake
{
    /// <summary>The baked colour of one COLR layer quad. A <paramref name="foreground"/> layer (palette index 0xFFFF)
    /// takes the enclosing span's colour — which is <c>A == 0</c> for a plain run, i.e. "inherit at replay" — while a
    /// palette layer keeps its CPAL <paramref name="palette"/> colour regardless of the run colour.</summary>
    public static ColorF LayerColor(bool foreground, ColorF palette, ColorF spanColor) => foreground ? spanColor : palette;

    /// <summary>Whether a baked per-quad colour list is worth RETAINING on the cached run: only when at least one quad
    /// actually overrides the run colour (<c>A &gt; 0</c>). An all-inherit list (a plain run, or an emoji whose every
    /// layer is foreground) stays <c>Colors = null</c>, so the overwhelming uniform-run case pays nothing at replay.</summary>
    public static bool RetainColors(ReadOnlySpan<ColorF> colors)
    {
        for (int i = 0; i < colors.Length; i++)
            if (colors[i].A > 0f) return true;
        return false;
    }
}
