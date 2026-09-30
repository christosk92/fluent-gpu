using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>E1 (<c>docs/plans/wavee/home-redesign-implementation.md</c> Workstream E, `C:\wavee\waveemusic`):
/// <see cref="AccentSet.From"/> derives a full accent palette from any color (page/cover accent), sharing its shade
/// tier math with <see cref="Tok"/>'s own live-accent ramp — these facts pin that equivalence plus the alpha tiers
/// and the ink-contrast guarantee.</summary>
public sealed class AccentSetTests
{
    // Tests mutate the live Tok accent override; always restore to the baked default so other tests (and test
    // ordering) don't observe a leaked override — xUnit v3 runs a class's facts on one collection but assembly-wide
    // parallelism means static Tok state must be reset within each fact, not relied upon across files.
    private static void WithAccent(ColorF @base, ThemeKind theme, System.Action assert)
    {
        var prevTheme = Tok.Theme;
        try
        {
            Tok.Use(theme);
            assert();
        }
        finally
        {
            Tok.Use(prevTheme);
        }
    }

    [Theory]
    [InlineData(ThemeKind.Dark)]
    [InlineData(ThemeKind.Light)]
    public void From_AppAccentBase_MatchesTokTiers(ThemeKind theme)
    {
        WithAccent(default, theme, () =>
        {
            // Push the SAME base through Tok.SetAccent (the live-override path) and AccentSet.From (the standalone
            // path) and assert they land on the identical fill/secondary/tertiary/subtle/text tiers — the "one shade
            // function" contract (no duplicated tier math between Tok.Accent* and AccentSet.From).
            var baseColor = ColorF.FromRgba(0x0F, 0x7B, 0x0F);   // an arbitrary saturated green, not the WinUI default
            Tok.SetAccent(baseColor);
            try
            {
                var set = AccentSet.From(baseColor);
                Assert.Equal(Tok.AccentDefault, set.Fill);
                Assert.Equal(Tok.AccentSecondary, set.FillSecondary);
                Assert.Equal(Tok.AccentTertiary, set.FillTertiary);
                Assert.Equal(Tok.AccentSubtle, set.Subtle);
                Assert.Equal(Tok.AccentTextPrimary, set.Text);
                Assert.Equal(Tok.OnAccent, set.Ink);
            }
            finally
            {
                Tok.SetAccent(null);
            }
        });
    }

    [Theory]
    [InlineData(0.05f, 0.05f, 0.05f)]   // near-black
    [InlineData(0.95f, 0.95f, 0.95f)]   // near-white
    [InlineData(0.90f, 0.10f, 0.10f)]   // saturated red
    [InlineData(0.10f, 0.90f, 0.10f)]   // saturated green
    [InlineData(0.10f, 0.10f, 0.90f)]   // saturated blue
    [InlineData(0.60f, 0.35f, 0.85f)]   // arbitrary mid-tone violet
    [InlineData(0.95f, 0.75f, 0.05f)]   // saturated yellow (near-white luminance, tests the boundary pick)
    public void InkContrast_MeetsAaAcrossASweep(float r, float g, float b)
    {
        WithAccent(default, ThemeKind.Dark, () =>
        {
            var set = AccentSet.From(new ColorF(r, g, b, 1f));
            float ratio = ColorContrast.Ratio(set.Ink, set.Fill);
            Assert.True(ratio >= 4.5f, $"Ink {set.Ink} on fill {set.Fill} only reached {ratio:F2}:1");
        });
    }

    [Fact]
    public void AlphaTiers_AreNinetyAndEighty()
    {
        WithAccent(default, ThemeKind.Dark, () =>
        {
            var set = AccentSet.From(ColorF.FromRgba(0x33, 0x66, 0xCC));
            Assert.Equal(1f, set.Fill.A, 3);
            Assert.Equal(0.90f, set.FillSecondary.A, 3);
            Assert.Equal(0.80f, set.FillTertiary.A, 3);
            Assert.Equal(0.16f, set.Subtle.A, 3);
            // Alpha aside, the RGB channels are the SAME shade as Fill (only the alpha tier changes).
            Assert.Equal(set.Fill.R, set.FillSecondary.R, 5);
            Assert.Equal(set.Fill.G, set.FillSecondary.G, 5);
            Assert.Equal(set.Fill.B, set.FillSecondary.B, 5);
        });
    }

    [Theory]
    [InlineData(ThemeKind.Dark)]
    [InlineData(ThemeKind.Light)]
    public void InkSecondary_UsesDarkPointFiveOrLightPointSevenAlpha(ThemeKind theme)
    {
        WithAccent(default, theme, () =>
        {
            // The fill is the theme's ramp shade (Light shades in dark theme), so the ink follows the FILL, not the
            // base: assert the rule per input rather than presuming which branch a base lands in.
            foreach (var @base in new[] { ColorF.FromRgba(0xF5, 0xF5, 0xF5), ColorF.FromRgba(0x10, 0x10, 0x60), ColorF.FromRgba(0x00, 0x78, 0xD4) })
            {
                var set = AccentSet.From(@base);
                Assert.Equal(ColorContrast.PickContrast(set.Fill), set.Ink);
                bool nearBlack = set.Ink == ColorContrast.NearBlackInk;
                Assert.Equal(nearBlack ? 0.5f : 0.7f, set.InkSecondary.A, 3);
                Assert.Equal(set.Ink with { A = 1f }, set.InkSecondary with { A = 1f });
            }
        });
    }
}
