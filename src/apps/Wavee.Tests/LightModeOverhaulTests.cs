using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE LIGHT-MODE OVERHAUL (D44). The app's light theme was not one bad colour — the base palette is the WinUI light
/// dictionary verbatim and always was. It was a set of app-side mechanics each of which had been solved against a
/// DIFFERENT light surface than the one it ended up painting on, and whose light arms were in several cases the dark
/// arm's numbers copied across. These are the pins for the ones that are checkable as values or as source facts:
/// <list type="bullet">
///   <item>the DATA-DOT ink — a wire hue authored for a dark surface, re-graded hue-dependently for a light one;</item>
///   <item>the SELECTION ladder — three rungs that only ever go up, replacing an inversion;</item>
///   <item>the LIGHT ROW ladder — hover/press/zebra raised and solved against the art-derived page tone;</item>
///   <item>the WARM palette being reachable at all;</item>
///   <item>the shell wash stopping at the dock line, and no raw Camelot paint surviving anywhere.</item>
/// </list>
/// The page tone's own clamp lives with its siblings in <see cref="DetailPageToneTests"/>.
/// </summary>
public class LightModeOverhaulTests
{
    // ── D44.3 — the Camelot data dot ─────────────────────────────────────────────────────────────────────────────

    // Four inputs chosen for what each one proves, not for coverage. Yellow and cyan sit INSIDE the luminance band and
    // need the deep rung; red and magenta sit outside it and must NOT be darkened as far (that is what turns a colour
    // wheel into twelve browns). Grey has no hue at all.
    const uint WireYellow = 0xFFFFE119;   // ~52°
    const uint WireCyan   = 0xFF19E6E6;   // 180°
    const uint WireRed    = 0xFFE61919;   // 0°
    const uint WireGrey   = 0xFF9A9A9A;

    /// <summary>DARK IS A PASSTHROUGH. The wire colours already ARE the dark-surface answer — re-grading them would
    /// break the one property the Camelot wheel guarantees, which is that harmonically adjacent keys are adjacent
    /// hues.</summary>
    [Theory]
    [InlineData(WireYellow)]
    [InlineData(WireCyan)]
    [InlineData(WireRed)]
    [InlineData(WireGrey)]
    public void DataDotInk_IsAPassthroughInDark(uint argb)
        => Assert.Equal(WaveePalette.ToColor(argb), WaveePalette.DataDotInk(argb, ThemeKind.Dark));

    /// <summary>LIGHT DARKENS BY HUE BAND, which is the whole point: a single "darken by N" is the mistake this
    /// replaces. Yellow at L 0.5 is nearly invisible on a near-white row and needs about three rungs; red is already
    /// dark and needs about one. Both land at the S the wheel needs to stay legible as a wheel.</summary>
    [Theory]
    [InlineData(WireYellow, 0.30f)]
    [InlineData(WireCyan, 0.30f)]
    [InlineData(WireRed, 0.40f)]
    public void DataDotInk_ForcesTheHueBandsLightnessRung(uint argb, float expectedL)
    {
        var ink = WaveePalette.DataDotInk(argb, ThemeKind.Light);
        var (h, s, l) = WaveePalette.ToHsl(ink);
        Assert.Equal(expectedL, l, 3);
        Assert.Equal(0.65f, s, 3);
        // …and the HUE survives, or the dot has stopped identifying the key. Compared on the CIRCLE: red round-trips
        // through the HSL conversions as 359.99997°, which is the same hue as 0° and is not a drift.
        float drift = MathF.Abs(h - WaveePalette.ToHsl(WaveePalette.ToColor(argb)).H) % 360f;
        Assert.True(MathF.Min(drift, 360f - drift) < 0.5f, $"hue drifted by {drift}°");
    }

    /// <summary>The band members really are darker than the ones outside it — stated as the ordering rather than as two
    /// literals, because the ordering is the design and the literals are tuning.</summary>
    [Fact]
    public void DataDotInk_DarkensTheYellowCyanBandFurtherThanTheRest()
    {
        float yellow = WaveePalette.ToHsl(WaveePalette.DataDotInk(WireYellow, ThemeKind.Light)).L;
        float red = WaveePalette.ToHsl(WaveePalette.DataDotInk(WireRed, ThemeKind.Light)).L;
        Assert.True(yellow < red, $"yellow {yellow:F2} must darken further than red {red:F2}");
    }

    /// <summary>Every light dot clears a real legibility bar against the surface it lands on — the light page tone at
    /// its own forced lightness, which is the DARKEST light row host in the app. 3:1 is the WCAG non-text bar, which is
    /// the right one: this is a 6-DIP graphical token, not text.</summary>
    [Theory]
    [InlineData(WireYellow)]
    [InlineData(WireCyan)]
    [InlineData(WireRed)]
    [InlineData(WireGrey)]
    public void DataDotInk_ClearsTheNonTextContrastBarOnTheLightestRow(uint argb)
    {
        var host = WaveePalette.FromHsl(120f, WaveePalette.PageToneLightSMax, WaveePalette.PageToneLightL);
        float ratio = ColorContrast.Ratio(WaveePalette.DataDotInk(argb, ThemeKind.Light), host);
        Assert.True(ratio >= 3f, $"dot ratio {ratio:F2} on the light page tone is below the 3:1 non-text bar");
    }

    /// <summary>A greyscale swatch stays grey. Forcing S 0.65 on a hueless input would fabricate a red out of HSL's
    /// h == 0 fallback — the same "never invent a colour" rule the page tone and the chrome accent already keep.</summary>
    [Fact]
    public void DataDotInk_NeverInventsAHueForAGreySwatch()
    {
        var ink = WaveePalette.DataDotInk(WireGrey, ThemeKind.Light);
        Assert.Equal(ink.R, ink.G, 3);
        Assert.Equal(ink.G, ink.B, 3);
    }

    /// <summary>Alpha is carried through untouched — the dot's dimming is a node Opacity, not a colour property.</summary>
    [Fact]
    public void DataDotInk_PreservesAlpha()
        => Assert.Equal(0x80 / 255f, WaveePalette.DataDotInk(0x80FFE119, ThemeKind.Light).A, 3);

    /// <summary>No surface may paint a wire Camelot colour raw. The correction is theme-dependent, so a second call
    /// site that forgot it is invisible in dark and wrong in light — exactly the class of defect this wave existed to
    /// remove, and exactly the one a value test cannot catch.</summary>
    [Fact]
    public void NoSurface_PaintsARawCamelotColour()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        var offenders = new List<string>();
        foreach (string path in AppSources(root))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (!line.Contains("CamelotColor", StringComparison.Ordinal)
                    && !line.Contains("ToColor(argb)", StringComparison.Ordinal)) continue;
                // The pattern that is wrong: the swatch's Fill taking the wire colour without the theme correction.
                if (line.Contains("Fill = WaveePalette.ToColor(argb)", StringComparison.Ordinal))
                    offenders.Add(Path.GetFileName(path) + ": " + line.Trim());
            }
        }
        Assert.True(offenders.Count == 0,
            "a Camelot swatch must paint WaveePalette.DataDotInk, never the raw wire colour:\n  " + string.Join("\n  ", offenders));
    }

    // ── D44.5 — the selection ladder ─────────────────────────────────────────────────────────────────────────────

    /// <summary>THE ORDERING LAW, in both themes: hovering the row you are already on must read STRONGER than the row
    /// at rest, and pressing it must stay above rest too. The bug this replaces had selected-at-rest on the subtle
    /// SECONDARY rung and hovered-selected on the quieter TERTIARY one — pointing at your selection visibly deselected
    /// it. Measured as composited coverage over a common host, not as raw alpha, because the rungs are different inks.
    /// </summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void SelectionStates_OnlyEverGoUp(ThemeKind theme)
    {
        WithTheme(theme, () =>
        {
            ColorF host = theme == ThemeKind.Light
                ? ColorF.FromRgba(0xFC, 0xFC, 0xFC) : ColorF.FromRgba(0x2C, 0x2C, 0x2C);
            float rest = Delta(WaveeColors.SelectedRest, host);
            float hover = Delta(WaveeColors.SelectedHover, host);
            float pressed = Delta(WaveeColors.SelectedPressed, host);

            Assert.True(hover > rest, $"{theme}: hovered-selected ({hover:F4}) must read stronger than at rest ({rest:F4})");
            Assert.True(pressed > rest, $"{theme}: pressed-selected ({pressed:F4}) must stay above rest ({rest:F4})");
            // WinUI's own shape: press DIPS below hover, it does not overshoot it.
            Assert.True(pressed < hover, $"{theme}: pressed ({pressed:F4}) must sit below hover ({hover:F4})");
            // …and an unselected row's hover must not out-shout the selection it sits beside.
            Assert.True(rest > Delta(Tok.FillSubtleSecondary, host),
                $"{theme}: selection at rest is quieter than an unselected row's hover");

            static float Delta(in ColorF top, in ColorF host)
                => ColorContrast.LuminanceDelta(ColorContrast.Flatten(top, host), host);
        });
    }

    /// <summary>Selection is the ACCENT plate (WaveeAccent role 2, "you are here") and the hovered/pressed rungs are
    /// COMPOSED over it rather than swapped for a neutral — a row paints ONE fill, so the only way a state can be
    /// "the plate plus a veil" is source-over.</summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void SelectionStates_AreTheAccentPlateWithTheStandardVeilsOverIt(ThemeKind theme)
    {
        WithTheme(theme, () =>
        {
            Assert.Equal(Tok.AccentSubtle, WaveeColors.SelectedRest);
            Assert.Equal(ColorContrast.Over(Tok.FillSubtleSecondary, Tok.AccentSubtle), WaveeColors.SelectedHover);
            Assert.Equal(ColorContrast.Over(Tok.FillSubtleTertiary, Tok.AccentSubtle), WaveeColors.SelectedPressed);
        });
    }

    /// <summary>No sidebar surface may still author the inverted trio by hand. The inversion was identical in six
    /// files across three sidebar designs, which is precisely why it survived: each copy looked like a local choice.
    /// </summary>
    [Fact]
    public void NoSidebarSurface_StillAuthorsTheInvertedSelectionTrio()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        var offenders = new List<string>();
        foreach (string path in AppSources(root))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                string t = line.Trim();
                // The signature of the inversion: a HOVER fill that swaps DOWN to the quieter rung when selected.
                if (t.StartsWith("HoverFill", StringComparison.Ordinal)
                    && t.Contains("Tok.FillSubtleTertiary", StringComparison.Ordinal)
                    && (t.Contains("selected ?", StringComparison.Ordinal) || t.Contains("Selected ?", StringComparison.Ordinal))
                    && t.IndexOf("Tok.FillSubtleTertiary", StringComparison.Ordinal) < t.IndexOf(':'))
                    offenders.Add(Path.GetFileName(path) + ": " + t);
            }
        }
        Assert.True(offenders.Count == 0,
            "hovered-selected must read stronger than selected-at-rest (WaveeColors.SelectedHover):\n  " + string.Join("\n  ", offenders));
    }

    // ── D44.2 / D44.8 — the light row ladder ─────────────────────────────────────────────────────────────────────

    /// <summary>The light row rungs are RAISED and they are BLACK ink. They used to be WinUI's own 0x09/0x0C subtle
    /// values, which are correct against a near-white #FCFCFC pane and vanish against the art-derived page tone the
    /// detail lists actually sit on. The ordering (zebra &lt; hover &lt; pressed) is the part that must never move.</summary>
    [Fact]
    public void TheLightRowLadder_IsRaisedBlackInk_InOrder()
    {
        foreach (var palette in Tok.Presets)
        {
            var shell = palette.LightShell;
            Assert.True(shell.RowZebra.R < 0.5f && shell.RowHover.R < 0.5f && shell.RowPressed.R < 0.5f,
                $"{palette.Id}: the light row ladder must be black ink, not a white lift");
            Assert.True(shell.RowZebra.A < shell.RowHover.A, $"{palette.Id}: zebra is not below hover");
            Assert.True(shell.RowHover.A < shell.RowPressed.A, $"{palette.Id}: hover is not below pressed");
            Assert.True(shell.RowHover.A >= 0.045f, $"{palette.Id}: light hover α {shell.RowHover.A:F3} is back below the audible floor");
            // The merged rungs are COMPOSED, never eyeballed — source-over is associative, so painting one merged fill
            // is pixel-identical to stacking the two.
            Assert.Equal(ColorContrast.Over(shell.RowHover, shell.RowZebra), shell.RowHoverZebra);
            Assert.Equal(ColorContrast.Over(shell.RowPressed, shell.RowZebra), shell.RowPressedZebra);
        }
    }

    /// <summary>Every light row state stays legible against the DARKEST light host a row can land on — the art-derived
    /// page tone at its forced lightness. This is the host the rungs were re-solved against; the stock near-white pane
    /// is strictly easier.</summary>
    [Fact]
    public void TheLightRowStates_AreVisibleOnTheArtDerivedPageTone()
    {
        var tone = WaveePalette.FromHsl(120f, WaveePalette.PageToneLightSMax, WaveePalette.PageToneLightL);
        foreach (var palette in Tok.Presets)
        {
            var shell = palette.LightShell;
            float hover = ColorContrast.LuminanceDelta(ColorContrast.Flatten(shell.RowHover, tone), tone);
            Assert.True(hover >= 0.05f, $"{palette.Id}: hover moves the page tone by only {hover:P1}");
        }
    }

    // ── D44.7 — the Warm palette is reachable ────────────────────────────────────────────────────────────────────

    /// <summary>Settings has always offered a Warm swatch that persists <c>"warm"</c>; the app's resolver carried arms
    /// for the other three ids and silently fell through to Neutral, so every value <c>BuildWarmLight</c> composes was
    /// unreachable. Every id the picker offers must resolve to a real palette.</summary>
    [Theory]
    [InlineData("warm")]
    [InlineData("slate")]
    [InlineData("neutral")]
    [InlineData("accent")]
    public void EveryPaletteIdTheSettingsPickerOffers_Resolves(string id)
    {
        var palette = Tok.PaletteById(id);
        Assert.NotNull(palette);
        Assert.Equal(id, palette!.Id);
    }

    /// <summary>The app resolver DELEGATES to the engine's rather than restating its arms — a restated switch is
    /// precisely how "warm" went missing while the swatch that persists it stayed on screen. <c>WaveeTheme</c> is
    /// engine-bound (it reads the live OS accent), so this is a source gate rather than a value test.</summary>
    [Fact]
    public void ThePaletteResolver_DelegatesToTheEngine_RatherThanRestatingItsArms()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string text = File.ReadAllText(Path.Combine(root, "Design", "WaveeTheme.cs"));
        Assert.Contains("Tok.PaletteById(id)", text);
        // …and no hand-written per-id arms survive to drift out of sync with it.
        Assert.DoesNotContain("\"slate\" =>", text);
        Assert.DoesNotContain("\"neutral\" =>", text);

        // The picker's own id list is the other half of the contract: every swatch it offers must be an id the engine
        // knows. A swatch for an id the resolver cannot answer is the whole defect. The picker declares that list ONCE
        // (s_paletteIds — the swatch that shows an id and the writer that persists it both index it), so this reads the
        // declaration and FAILS when it goes missing, rather than quietly passing because a per-swatch literal it used
        // to grep for was refactored away.
        string settings = File.ReadAllText(Path.Combine(root, "Features", "Shell", "SettingsPage.General.cs"));
        var declared = Regex.Match(settings, @"s_paletteIds\s*=\s*\[(?<ids>[^\]]*)\]");
        Assert.True(declared.Success,
            "SettingsPage.General.cs no longer declares s_paletteIds — the palette-picker id gate cannot see what the picker offers.");
        var offered = Regex.Matches(declared.Groups["ids"].Value, "\"(?<id>[^\"]+)\"");
        Assert.NotEmpty(offered);
        foreach (Match m in offered)
            Assert.NotNull(Tok.PaletteById(m.Groups["id"].Value));
    }

    /// <summary>Warm really is a distinct answer — the tests above would pass just as well if the engine had quietly
    /// aliased it to Neutral — and it is a SANE light palette, not merely a different one.</summary>
    [Fact]
    public void TheWarmPalette_IsNotNeutralWearingADifferentName()
    {
        var warm = Tok.WarmPalette;
        Assert.NotEqual(Tok.NeutralPalette.Light.FillSolidBase, warm.Light.FillSolidBase);
        Assert.True(ColorContrast.MeetsAaText(warm.Light.TextPrimary, warm.Light.FillCardDefault));
        Assert.True(ColorContrast.MeetsAaText(warm.Light.TextSecondary, warm.Light.FillCardDefault));
    }

    /// <summary>An unknown or corrupt persisted id must still land on a real palette rather than throwing.</summary>
    [Fact]
    public void AnUnknownPaletteId_HasNoEnginePalette_SoTheAppMustFallBack()
        => Assert.Null(Tok.PaletteById("chartreuse"));

    // ── D44.4 — the shell wash stops at the dock ─────────────────────────────────────────────────────────────────

    /// <summary>The player dock paints nothing, so whatever the material layer paints under it IS the dock — and the
    /// Mix wash's ellipse centre sits at window y = 1.00, i.e. its PEAK landed exactly across the dock band. The wash
    /// layers are now hosted in a box inset by the dock reserve. A source gate because the defect is a LAYOUT fact
    /// about a component that needs a live viewport signal to render.</summary>
    [Fact]
    public void TheShellWash_IsClippedAboveThePlayerDock()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string text = File.ReadAllText(Path.Combine(root, "Features", "Shell", "ShellMaterialLayer.cs"));
        Assert.Contains("PlayerDock.Reserve", text);
        Assert.Contains("ClipToBounds = true", text);
        // The three washes go through the inset host; the flat tint deliberately does NOT (it is a uniform scrim with
        // no peak to land anywhere, so the dock carrying it is the page's colour reaching the whole window).
        Assert.Contains("WashHost(", text);
        int host = text.IndexOf("kids = [Tint(state.Tint), WashHost(", StringComparison.Ordinal);
        Assert.True(host > 0, "the wash layers must be mounted through the dock-inset host");
    }

    /// <summary>Only the Mix placement hangs off the bottom edge, which is what makes the inset safe: Hero and Weekly
    /// are TOP-anchored and therefore bit-for-bit unmoved by it, so Home's approved look above the dock is untouched.
    /// </summary>
    [Fact]
    public void OnlyTheMixWash_IsBottomAnchored()
    {
        Assert.True(ShellWashGeometry.Mix.AnchorBottom);
        Assert.False(ShellWashGeometry.Hero.AnchorBottom);
        Assert.False(ShellWashGeometry.Weekly.AnchorBottom);
    }

    // ── D44.10 — the dead hero-wash trio is gone ─────────────────────────────────────────────────────────────────

    /// <summary>A tuned-but-unreachable wash is worse than no wash: it is a second, contradictory answer to "how much
    /// colour does a light page take" sitting a few lines from the real one (the page tone). All three lost their last
    /// call site when the detail pages moved to the opaque art-derived ground.</summary>
    [Fact]
    public void TheDeadHeroWashTrio_IsDeleted()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string surfaces = File.ReadAllText(Path.Combine(root, "Design", "Surfaces.cs"));
        Assert.DoesNotContain("public static GradientSpec HeroWash(", surfaces);
        string palette = File.ReadAllText(Path.Combine(root, "Design", "WaveePalette.cs"));
        Assert.DoesNotContain("HeroBase(", palette);
        Assert.DoesNotContain("HeroWashColor(", palette);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    static void WithTheme(ThemeKind theme, Action body)
    {
        var priorPalette = Tok.Palette;
        var prior = Tok.Theme;
        try { Tok.Use(theme); body(); }
        finally { Tok.Use(priorPalette, prior); }
    }

    static IEnumerable<string> AppSources(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            yield return path;
        }
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
