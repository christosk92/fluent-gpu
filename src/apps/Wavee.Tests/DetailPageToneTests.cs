using System;
using System.IO;
using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// <c>WaveePalette.PageTone</c> — the detail pages' ONE art-derived ground.
///
/// <para>The two rules under test are the two the doc comment states, and they are the two that make the difference
/// between this and the version of the idea people complain about. <b>Complementary, not sampled</b>: the tone takes
/// the cover's HUE and nothing else, so a page is the record's colour without being a re-print of the record.
/// <b>The clamp is the point</b>: lightness is FORCED per theme and saturation is CAPPED, so a saturated cover cannot
/// produce a page that is hard to read and a dark cover cannot produce a page indistinguishable from every other dark
/// one. The forced lightness is also what licenses the standard <c>Tok</c> ink tokens on this plane in both themes —
/// there is no on-media ladder on these pages because polarity is guaranteed by construction, and THAT is the property
/// the polarity tests below actually protect.</para>
/// </summary>
public class DetailPageToneTests
{
    // A saturated red cover, graded the way the real wire grades one: the chroma lives in the BACKGROUND roles, and
    // textBrightAccent is the contrast-graded ink (pure white in every dark half) — see WaveePalette.Accent.
    static CoverColorPlane.Scheme SaturatedRed => new(
        BackgroundBase: 0xFF8A1220, BackgroundTintedBase: 0xFFC21027, TextBase: 0xFFFFFFFF,
        TextSubdued: 0xFFB3B3B3, TextBrightAccent: 0xFFFFFFFF);

    static CoverColorPlane.Scheme SaturatedTeal => new(
        BackgroundBase: 0xFF0B4F4A, BackgroundTintedBase: 0xFF0FA69B, TextBase: 0xFFFFFFFF,
        TextSubdued: 0xFFB3B3B3, TextBrightAccent: 0xFFFFFFFF);

    // A genuinely greyscale cover: every role neutral, which is what a black-and-white sleeve really grades to.
    static CoverColorPlane.Scheme Greyscale => new(
        BackgroundBase: 0xFF2B2B2B, BackgroundTintedBase: 0xFF454545, TextBase: 0xFFFFFFFF,
        TextSubdued: 0xFFB3B3B3, TextBrightAccent: 0xFFFFFFFF);

    static (float H, float S, float L) Hsl(ColorF c) => WaveePalette.ToHsl(c);

    [Fact]
    public void NoGrading_MeansNoTone()
    {
        Assert.Null(WaveePalette.PageTone(null, ThemeKind.Dark));
        Assert.Null(WaveePalette.PageTone(null, ThemeKind.Light));
    }

    /// <summary>The clamp box, stated as an assertion. A saturated cover lands INSIDE it in both themes — the tone is
    /// never the cover's own lightness, and its chroma is capped well below the cover's.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaturatedCover_LandsInsideTheClampBox(bool red)
    {
        var scheme = red ? SaturatedRed : SaturatedTeal;

        var dark = WaveePalette.PageTone(scheme, ThemeKind.Dark);
        Assert.NotNull(dark);
        var (_, ds, dl) = Hsl(dark!.Value);
        Assert.Equal(WaveePalette.PageToneDarkL, dl, 3);
        Assert.True(ds <= WaveePalette.PageToneDarkSMax + 1e-4f, $"dark saturation {ds} exceeded the cap");

        var light = WaveePalette.PageTone(scheme, ThemeKind.Light);
        Assert.NotNull(light);
        var (_, ls, ll) = Hsl(light!.Value);
        Assert.Equal(WaveePalette.PageToneLightL, ll, 3);
        Assert.True(ls <= WaveePalette.PageToneLightSMax + 1e-4f, $"light saturation {ls} exceeded the cap");
    }

    /// <summary>The hue SURVIVES the clamp — that is the whole point of clamping S and L rather than picking a
    /// palette. A red cover's tone is red and a teal cover's tone is teal, in both themes.</summary>
    [Fact]
    public void TheCoversHueSurvivesInBothThemes()
    {
        float coverHue = Hsl(WaveePalette.Accent(SaturatedRed)).H;
        foreach (var theme in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            var (h, _, _) = Hsl(WaveePalette.PageTone(SaturatedRed, theme)!.Value);
            Assert.True(MathF.Abs(h - coverHue) < 1.5f, $"{theme}: hue drifted {coverHue} → {h}");
        }
        // …and two different covers do not collapse onto the same page.
        var redTone = WaveePalette.PageTone(SaturatedRed, ThemeKind.Dark)!.Value;
        var tealTone = WaveePalette.PageTone(SaturatedTeal, ThemeKind.Dark)!.Value;
        Assert.True(MathF.Abs(Hsl(redTone).H - Hsl(tealTone).H) > 60f);
    }

    /// <summary>THE tone is NOT the cover's dominant colour. A page painted in the record's own background role is the
    /// version of this feature that makes artwork indistinguishable from the page it sits on.</summary>
    [Fact]
    public void TheToneIsComplementary_NotTheSampledCoverColour()
    {
        var dominant = WaveePalette.Accent(SaturatedRed);
        var tone = WaveePalette.PageTone(SaturatedRed, ThemeKind.Dark)!.Value;
        Assert.True(MathF.Abs(Hsl(dominant).L - Hsl(tone).L) > 0.05f);
        Assert.True(Hsl(dominant).S - Hsl(tone).S > 0.2f);
    }

    /// <summary>A cover with no hue worth using gets the NEUTRAL tone, not an invented tint — the same decision
    /// <c>ChromeAccent</c> makes for the Play button, for the same reason.</summary>
    [Fact]
    public void GreyscaleCover_GetsTheNeutralToneNotAnInventedTint()
    {
        Assert.Equal(WaveePalette.PageToneNeutralDark, WaveePalette.PageTone(Greyscale, ThemeKind.Dark));
        Assert.Equal(WaveePalette.PageToneNeutralLight, WaveePalette.PageTone(Greyscale, ThemeKind.Light));
        // The neutral answers really are neutral. Measured as CHANNEL SPREAD, not HSL saturation: near white, S is a
        // ratio with a vanishing denominator and reports 0.22 for a colour whose channels are three points apart.
        Assert.True(Spread(WaveePalette.PageToneNeutralDark) < 0.02f);
        Assert.True(Spread(WaveePalette.PageToneNeutralLight) < 0.04f);
        // …and the light one is WARM (red channel above blue), the direction the app's light palette already leans.
        Assert.True(WaveePalette.PageToneNeutralLight.R > WaveePalette.PageToneNeutralLight.B);

        static float Spread(ColorF c)
            => MathF.Max(c.R, MathF.Max(c.G, c.B)) - MathF.Min(c.R, MathF.Min(c.G, c.B));
    }

    /// <summary>POLARITY, which is what lets the pages drop their on-media ink ladder entirely: whatever the cover,
    /// the dark tone is dark enough for light ink and the light tone light enough for dark ink — by construction, not
    /// by measurement. Both are checked against the WCAG 4.5:1 body-text threshold using the engine's own ratio.</summary>
    [Fact]
    public void PolarityIsGuaranteedPerTheme_SoStandardInkTokensAreCorrect()
    {
        CoverColorPlane.Scheme[] covers = [SaturatedRed, SaturatedTeal, Greyscale, WaveePalette.Neutral];
        ColorF white = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
        ColorF black = ColorF.FromRgba(0, 0, 0);
        foreach (var cover in covers)
        {
            var dark = WaveePalette.PageTone(cover, ThemeKind.Dark)!.Value;
            var light = WaveePalette.PageTone(cover, ThemeKind.Light)!.Value;
            Assert.True(ColorContrast.Ratio(white, dark) >= 4.5f, "light ink failed on the dark tone");
            Assert.True(ColorContrast.Ratio(black, light) >= 4.5f, "dark ink failed on the light tone");
            // …and the two themes never cross over.
            Assert.True(ColorContrast.RelativeLuminance(dark) < ColorContrast.RelativeLuminance(light));
        }
    }

    /// <summary>The sticky context band's material is solved over the PAGE'S ground, not over the neutral Mica
    /// reference: on a tinted page the constant flatten reads as a grey plate. The two must differ for a live tone and
    /// must agree when there is none.</summary>
    [Fact]
    public void ContextBandMaterial_FollowsTheLiveTone()
    {
        Assert.Equal(WaveeColors.ContextBand, WaveeColors.ContextBandOver(null));
        var tone = WaveePalette.PageTone(SaturatedRed, ThemeKind.Dark)!.Value;
        var over = WaveeColors.ContextBandOver(tone);
        Assert.NotEqual(WaveeColors.ContextBand, over);
        // It is still a FLATTEN — opaque, and pulled toward the ground it sits on rather than replaced by it.
        Assert.Equal(1f, over.A);
    }

    /// <summary>The tone is mounted for BOTH page arms from ONE leaf, the washes it replaced are gone, and the leaf
    /// respects the two "no tone" inputs (the colour-washes preference and a cover with no grading).</summary>
    [Fact]
    public void ThePlane_IsOneLeafMountedByBothArms()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string shell = File.ReadAllText(Path.Combine(root, "Features", "Detail", "DetailShell.cs"));
        Assert.DoesNotContain("DetailWash", shell);
        // ONE construction, referenced by both arms' ZStacks.
        Assert.Contains("CoverPaletteLeaves.PageTonePlane(", shell);
        Assert.Contains("colorWashesDisabled", shell);
        Assert.Contains("DetailPageToneHeroOnly", shell);

        string leaves = File.ReadAllText(Path.Combine(root, "Design", "CoverPaletteLeaves.cs"));
        Assert.DoesNotContain("CoverKeyedWash", leaves);
        Assert.Contains("WaveePalette.PageTone", leaves);
        // The Watch subscription is INSIDE the leaf (never in a page Render) — that is what keeps a grading arrival to
        // one node — and the ground itself is a BOUND brush so it cross-fades on the compositor.
        Assert.Contains("plane.Watch(", leaves);
        Assert.Contains("Fill = Prop.Of(", leaves);
        Assert.Contains("BrushTransitionMs = WaveeMotion.Standard", leaves);
        // The background extension is the BAKED image blur (one derived texture, then an ordinary quad), never the
        // per-frame self-blur layer.
        Assert.Contains("BakedBlur", leaves);
        Assert.DoesNotContain(" Blur = ", leaves);   // BoxEl.Blur — the per-frame offscreen-RT Gaussian
    }

    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "Design", "WaveePalette.cs");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "Wavee");
            dir = dir.Parent;
        }
        return null!;
    }
}
