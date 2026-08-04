using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

// The shell's content region paints the MUX ladder's rungs 1+2 (body plate + content pane) as ONE translucent surface
// (WaveeColors.ContentPaneMerged) instead of two stacked blended passes — see WaveeShell's content-region ZStack and
// docs/design/subsystems/theming.md §2.2bis. That is only pixel-identical because source-over is ASSOCIATIVE, so these
// pin exactly that: the merged surface must composite to the same bytes as pane-on-plate over ANY backdrop (the whole
// assumed Mica swing plus the opaque inactive-window fallback the deactivate swing lands on), it must stay translucent
// (α < 1, so live Mica still reads through), and the token must be wired to the ladder's real rungs.
public class ShellMergedRungTests
{
    static ThemePalette[] All => [Tok.WarmPalette, Tok.SlatePalette, Tok.NeutralPalette, Tok.AccentTintedPalette];

    static int MaxChannelDelta(in ColorF a, in ColorF b)
    {
        static int Ch(float f) => (int)MathF.Round(f * 255f);
        return Math.Max(Math.Abs(Ch(a.R) - Ch(b.R)), Math.Max(Math.Abs(Ch(a.G) - Ch(b.G)), Math.Abs(Ch(a.B) - Ch(b.B))));
    }

    static int Byte(float f) => (int)MathF.Round(f * 255f);

    [Fact]
    public void MergedRung_CompositesIdenticallyOverEveryBackdrop()
    {
        foreach (var p in All)
        {
            foreach (var (light, shell, set) in new (bool, ShellPalette, TokenSet)[]
            {
                (true, p.LightShell, p.Light),
                (false, p.DarkShell, p.Dark),
            })
            {
                var merged = ColorContrast.Over(shell.FileArea, shell.Toolbar);
                ColorF[] backdrops = light
                    ? new[] { MicaRef.LightDefault, MicaRef.LightBright, MicaRef.LightDim, set.WindowBackground }
                    : new[] { MicaRef.DarkDefault, MicaRef.DarkBright, MicaRef.DarkDim, set.WindowBackground };
                foreach (var bd in backdrops)
                {
                    // ONE pass vs TWO — the algebra the merge rests on, asserted to the byte (the renderer blends
                    // straight source-over into a BGRA8_UNORM target, i.e. in exactly this space).
                    var one = ColorContrast.Flatten(merged, bd);
                    var two = ColorContrast.Flatten(shell.FileArea, ColorContrast.Flatten(shell.Toolbar, bd));
                    Assert.True(MaxChannelDelta(one, two) == 0,
                        $"{p.Id}/{(light ? "light" : "dark")}: merged composite drifted {MaxChannelDelta(one, two)}/255 from the two-rung stack");
                }
            }
        }
    }

    [Fact]
    public void MergedRung_StaysTranslucent_SoMicaStillReadsThrough()
    {
        foreach (var p in All)
        {
            foreach (var shell in new[] { p.LightShell, p.DarkShell })
            {
                var merged = ColorContrast.Over(shell.FileArea, shell.Toolbar);
                float want = shell.FileArea.A + shell.Toolbar.A * (1f - shell.FileArea.A);
                Assert.Equal(want, merged.A, 5);
                Assert.True(merged.A < 1f, $"{p.Id}: a merged rung must not go opaque — the backdrop is the point");
                Assert.True(merged.A > shell.Toolbar.A, $"{p.Id}: the merge must be MORE opaque than the plate alone");
            }
        }
    }

    // The stock MUX values, so a palette-recipe change can never silently re-tune the surface the whole app sits on:
    // dark LayerFillColorDefault #4C3A3A3A over LayerOnMicaBaseAltFillColorDefault #733A3A3A == #9D3A3A3A (both rungs
    // carry the same rgb at sat 0, so only alpha compounds), light #80FFFFFF over #B3FFFFFF == #D9FFFFFF. Composited,
    // those are still the ladder's #303030 dark / #FCFCFC light.
    [Fact]
    public void MergedRung_NeutralPresetIsTheStockCompoundedAlpha()
    {
        var dark = ColorContrast.Over(Tok.NeutralPalette.DarkShell.FileArea, Tok.NeutralPalette.DarkShell.Toolbar);
        Assert.Equal(0x3A, Byte(dark.R));
        Assert.Equal(0x3A, Byte(dark.G));
        Assert.Equal(0x3A, Byte(dark.B));
        Assert.Equal(0x9D, Byte(dark.A));
        Assert.Equal(0x30, Byte(ColorContrast.Flatten(dark, MicaRef.DarkDefault).R));

        var lite = ColorContrast.Over(Tok.NeutralPalette.LightShell.FileArea, Tok.NeutralPalette.LightShell.Toolbar);
        Assert.Equal(0xFF, Byte(lite.R));
        Assert.Equal(0xD9, Byte(lite.A));
        Assert.Equal(0xFC, Byte(ColorContrast.Flatten(lite, MicaRef.LightDefault).R));
    }

    // The live token — WaveeColors.ContentPaneMerged is what WaveeShell's content pane actually paints, and it must be
    // the merge of the ACTIVE theme's two rungs (computed per read, so Tok.Epoch re-fires it like every sibling fill).
    [Fact]
    public void ContentPaneMerged_IsTheActiveThemesTwoRungs()
    {
        var shell = Tok.Theme == ThemeKind.Light ? Tok.Palette.LightShell : Tok.Palette.DarkShell;
        Assert.Equal(ColorContrast.Over(shell.FileArea, shell.Toolbar), WaveeColors.ContentPaneMerged);
        // …and the raw rungs stay published untouched: the shell still paints the plate in the corner cut-away and the
        // trailing gap, and the palette gates still assert the two-rung ladder.
        Assert.Equal(shell.Toolbar, WaveeColors.Toolbar);
        Assert.Equal(shell.FileArea, WaveeColors.FileArea);
    }
}
