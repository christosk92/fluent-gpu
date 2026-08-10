using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// Pins the immersive STAGE: its pure width ladder (<see cref="StageLayout"/>) and the material rules its renderers are
/// only allowed to speak.
///
/// <para>The arithmetic half is the <c>MergedChromeLayoutTests</c> pattern — every boundary is DERIVED from the
/// constants rather than written down, so retuning a threshold retunes the tests with it. The source half is the
/// <c>ContextBandLayoutTests</c> / <c>VoiceUnificationTests</c> pattern: the stage's whole premise is "everything is ink
/// on the scrim", and a token that flips with the theme, a raw RGBA, a second filled control or a conditionally-mounted
/// pane are each a silent way to lose that premise, so each one has to argue with a test.</para>
/// </summary>
public class StageLayoutTests
{
    const float SweepMax = 2600f;

    // ── the width ladder ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The one threshold, derived: the narrowest width the seed resolution calls WIDE is exactly
    /// <see cref="StageLayout.WideEnterW"/>, and everything below it is compact.</summary>
    [Fact]
    public void TheWideThreshold_IsTheDeclaredOne()
    {
        float first = FirstWidthWhere(l => l.Wide);
        Assert.Equal(StageLayout.WideEnterW, first);
        Assert.False(StageLayout.FromWidth(first - 1f).Wide);
        Assert.True(StageLayout.FromWidth(first).Wide);
    }

    /// <summary>A degenerate / not-yet-measured viewport resolves COMPACT, never a 352-DIP column inside a 0-DIP
    /// window. (The surface seeds its signal from <c>Viewport.Size.Peek()</c>, which is 0 on the very first render.)</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-100f)]
    [InlineData(1f)]
    public void ADegenerateWidth_IsCompact(float w) => Assert.False(StageLayout.FromWidth(w).Wide);

    /// <summary>Sweeping the width up 1 DIP at a time — threading the previous layout, exactly as the surface's
    /// viewport effect does — flips the stage EXACTLY ONCE. A second flip anywhere in the sweep is the thrash the
    /// hysteresis exists to prevent (and it would remount nothing, but it would re-render a surface that owns a
    /// measured LyricsView on every one of 2600 resize steps).</summary>
    [Fact]
    public void AnUpwardSweep_FlipsExactlyOnce()
    {
        var cur = StageLayout.FromWidth(0f);
        int flips = 0;
        for (float w = 0f; w <= SweepMax; w += 1f)
        {
            var next = StageLayout.Resolve(w, cur);
            if (next.Wide != cur.Wide) flips++;
            cur = next;
        }
        Assert.Equal(1, flips);
        Assert.True(cur.Wide);
    }

    /// <summary>And the same going DOWN.</summary>
    [Fact]
    public void ADownwardSweep_FlipsExactlyOnce()
    {
        var cur = StageLayout.FromWidth(SweepMax);
        int flips = 0;
        for (float w = SweepMax; w >= 0f; w -= 1f)
        {
            var next = StageLayout.Resolve(w, cur);
            if (next.Wide != cur.Wide) flips++;
            cur = next;
        }
        Assert.Equal(1, flips);
        Assert.False(cur.Wide);
    }

    /// <summary>Demotion is IMMEDIATE and promotion costs the reserve — the asymmetry that makes the sweep above flip
    /// once. Inside the band a compact stage stays compact while a wide one stays wide, which is the definition of
    /// hysteresis and the reason a window edge parked on the boundary does not strobe.</summary>
    [Fact]
    public void PromotionCostsTheReserve_DemotionIsFree()
    {
        float inBand = StageLayout.WideEnterW + StageLayout.PromotionHysteresisW * 0.5f;

        // Coming UP through the band from compact: still compact.
        Assert.False(StageLayout.Resolve(inBand, StageLayout.CompactStage).Wide);
        // Coming DOWN through the band from wide: still wide.
        Assert.True(StageLayout.Resolve(inBand, StageLayout.WideStage).Wide);
        // Past the reserve: promoted.
        Assert.True(StageLayout.Resolve(StageLayout.WideEnterW + StageLayout.PromotionHysteresisW,
            StageLayout.CompactStage).Wide);
        // Below the threshold: demoted on the spot, no reserve.
        Assert.False(StageLayout.Resolve(StageLayout.WideEnterW - 1f, StageLayout.WideStage).Wide);
    }

    /// <summary>Narrowing never ADDS. The stage is a two-stage ladder, so its richness score is monotone by
    /// construction — this pins that no future third stage can break it.</summary>
    [Fact]
    public void NarrowingNeverAdds()
    {
        int prev = int.MinValue;
        for (float w = 0f; w <= SweepMax; w += 1f)
        {
            int r = StageLayout.FromWidth(w).Richness;
            Assert.True(r >= prev, $"richness went DOWN as the window widened, at {w}");
            prev = r;
        }
    }

    // ── the sizes ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The wide stage's authored geometry: a 352 column carrying a 300 cover, a 56 filled play between two 40
    /// steps between two 32 satellites. The transport is a strict ladder outward from the primary — that ordering is
    /// what makes the cluster read as one control rather than five.</summary>
    [Fact]
    public void TheWideStage_IsTheAuthoredGeometry()
    {
        var w = StageLayout.WideStage;
        Assert.True(w.Wide);
        Assert.Equal(352f, w.ColumnWidth);
        Assert.Equal(300f, w.ArtSize);
        Assert.Equal(56f, w.PlayBox);
        Assert.Equal(40f, w.StepBox);
        Assert.Equal(32f, w.SatelliteBox);
        Assert.True(w.PlayBox > w.StepBox && w.StepBox > w.SatelliteBox);
    }

    /// <summary>The compact stage keeps the same ORDERING one rung down: 64 cover, 40 play, 32 steps.</summary>
    [Fact]
    public void TheCompactStage_IsTheSameLadderOneRungDown()
    {
        var c = StageLayout.CompactStage;
        var w = StageLayout.WideStage;
        Assert.False(c.Wide);
        Assert.Equal(64f, c.ArtSize);
        Assert.Equal(40f, c.PlayBox);
        Assert.Equal(32f, c.StepBox);
        Assert.True(c.ArtSize < w.ArtSize);
        Assert.True(c.PlayBox < w.PlayBox);
        Assert.True(c.StepBox < w.StepBox);
        Assert.True(c.PlayBox > c.StepBox);
    }

    /// <summary>The column BOX is the designed column plus its veil falloff, and the veil's hold stop is exactly where
    /// the designed column ends inside it — so the gradient never starts fading under the type. The compact stage has
    /// no column at all, so it claims no layout width.</summary>
    [Fact]
    public void TheColumnBox_CarriesTheVeilFalloff()
    {
        var w = StageLayout.WideStage;
        Assert.Equal(w.ColumnWidth + w.ColumnFalloff, w.LayoutWidth);
        Assert.True(w.ColumnFalloff > 0f);
        Assert.Equal(w.ColumnWidth / w.LayoutWidth, w.VeilHoldStop, 4);

        var c = StageLayout.CompactStage;
        Assert.Equal(0f, c.LayoutWidth);
        Assert.Equal(0f, c.VeilHoldStop);
    }

    // ── the fold ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The wide stage folds NOTHING; the compact stage folds exactly four controls — shuffle, repeat, the
    /// volume row and the output-device line — and nothing else. The seek is deliberately NOT on the list: a
    /// now-playing surface you cannot scrub is not a now-playing surface.</summary>
    [Fact]
    public void TheFoldedSet_IsExactlyTheFourSecondaryControls()
    {
        Assert.Equal(StageControl.None, StageLayout.WideStage.Folded);
        Assert.Equal(
            StageControl.Shuffle | StageControl.Repeat | StageControl.Volume | StageControl.OutputDevice,
            StageLayout.CompactStage.Folded);
    }

    /// <summary>Every folded control is REACHABLE: the compact stage always shows an overflow, and the wide stage never
    /// needs one. "Folded" means moved address, never lost — the Friends rule from the merged chrome row.</summary>
    [Fact]
    public void FoldedMeansMovedAddress_NeverLost()
    {
        Assert.True(StageLayout.CompactStage.ShowOverflow);
        Assert.False(StageLayout.WideStage.ShowOverflow);

        foreach (var c in new[] { StageControl.Shuffle, StageControl.Repeat, StageControl.Volume, StageControl.OutputDevice })
        {
            // In the row XOR in the "…" — the exact complement, at both widths.
            Assert.True(StageLayout.WideStage.Shows(c));
            Assert.False(StageLayout.CompactStage.Shows(c));
        }
    }

    /// <summary>The three derived reads the renderer actually branches on agree with <see cref="StageLayout.Folded"/>,
    /// so a call site can never disagree with the fold set.</summary>
    [Fact]
    public void TheDerivedShowReads_AgreeWithTheFoldSet()
    {
        var w = StageLayout.WideStage;
        Assert.True(w.ShowSatellites && w.ShowVolume && w.ShowDeviceLine);
        var c = StageLayout.CompactStage;
        Assert.False(c.ShowSatellites || c.ShowVolume || c.ShowDeviceLine);
    }

    /// <summary>Only two shapes exist at ANY width — the "one structure, one reflow flag" rule in arithmetic form.</summary>
    [Fact]
    public void TheLadderHasExactlyTwoShapes()
    {
        var seen = new HashSet<StageLayout>();
        for (float w = 0f; w <= SweepMax; w += 1f) seen.Add(StageLayout.FromWidth(w));
        Assert.Equal(2, seen.Count);
        Assert.Contains(StageLayout.WideStage, seen);
        Assert.Contains(StageLayout.CompactStage, seen);
    }

    // ── the material rules, as source scans ──────────────────────────────────────────────────────────────────────────

    /// <summary>The stage's renderers, by name. <c>ImmersiveLyricsSurface</c> is the host: it is in the INK scans (its
    /// chrome is stage chrome) but out of the PLATE scan, because its one <c>Tok.FillSolidBase</c> is the opaque floor
    /// UNDER the backdrop — the thing that stops the page below showing through — not a plate on the stage.</summary>
    static readonly string[] StageRenderers = ["StageChrome.cs", "StageIdentity.cs", "StagePanes.cs"];
    static readonly string[] StageInkFiles = ["StageChrome.cs", "StageIdentity.cs", "StagePanes.cs", "ImmersiveLyricsSurface.cs"];

    /// <summary>THE material rule: the stage is on media, so it paints the theme-INVARIANT on-media ink. A
    /// <c>Tok.Text*</c> rung flips with the theme and would be invisible on the stage's always-dark veils in one theme
    /// or the other — which is exactly the bug this surface's veils exist to prevent.</summary>
    [Fact]
    public void NoStageFile_PaintsAThemeFlippingTextRung()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        var offenders = new List<string>();
        foreach (string name in StageInkFiles)
        {
            string[] lines = File.ReadAllLines(StagePath(root, name));
            for (int i = 0; i < lines.Length; i++)
            {
                string l = Code(lines[i]);   // prose may NAME the rung it replaced; only CODE is scanned
                if (ThemeInk.IsMatch(l)) offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "the stage is on media — ink comes from WaveeOnMedia, never a theme-flipping Tok.Text rung:\n  "
            + string.Join("\n  ", offenders));
    }

    static readonly Regex ThemeInk = new(@"Tok\.(Text|AccentText)(Primary|Secondary|Tertiary|Disabled)", RegexOptions.Compiled);

    /// <summary>No hand-mixed colour. Every value the stage paints is derived from a token — the on-media ink, the
    /// engine's media stage, or the art-derived accent — so a palette change carries the whole surface with it.</summary>
    [Fact]
    public void NoStageFile_AuthorsARawColorLiteral()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        var offenders = new List<string>();
        foreach (string name in StageInkFiles)
        {
            string[] lines = File.ReadAllLines(StagePath(root, name));
            for (int i = 0; i < lines.Length; i++)
                if (RawColor.IsMatch(lines[i])) offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "stage colour is derived from tokens, never hand-mixed:\n  " + string.Join("\n  ", offenders));
    }

    static readonly Regex RawColor = new(@"ColorF\.(FromRgba|FromRgb)\s*\(", RegexOptions.Compiled);

    /// <summary>NO PLATES. The stage's only fills are the on-media GLASS interaction ramp, the one filled play button
    /// and the accent — never the theme's card / solid / subtle plate ladders, which is what would turn a surface whose
    /// premise is "ink on the scrim" back into a panel of boxes.</summary>
    [Fact]
    public void NoStageRenderer_PaintsAPlateLadder()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        var offenders = new List<string>();
        foreach (string name in StageRenderers)
        {
            string[] lines = File.ReadAllLines(StagePath(root, name));
            for (int i = 0; i < lines.Length; i++)
            {
                string l = Code(lines[i]);
                if (PlateLadder.IsMatch(l)) offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "the stage carries no plates — glass, the filled play, and the accent:\n  " + string.Join("\n  ", offenders));
    }

    static readonly Regex PlateLadder = new(@"Tok\.Fill(Card|Solid|Subtle)|WaveeColors\.Row", RegexOptions.Compiled);

    /// <summary>The filled play is the ONE filled control on the stage. It is expressed as the
    /// <c>WaveeOnMedia.LightButton*</c> ramp, and that ramp appears in exactly one place — <c>StageChrome.Play</c> —
    /// so a second filled control cannot appear without moving this test.</summary>
    [Fact]
    public void TheFilledPlay_IsTheOnlyFilledControl()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string chrome = File.ReadAllText(StagePath(root, "StageChrome.cs"));
        Assert.Contains("public static BoxEl Play(", chrome);
        Assert.Contains("WaveeOnMedia.LightButton", chrome);
        Assert.Contains("WaveeOnMedia.LightButtonInk", chrome);

        foreach (string name in StageInkFiles)
        {
            if (name == "StageChrome.cs") continue;
            string text = File.ReadAllText(StagePath(root, name));
            Assert.DoesNotContain("WaveeOnMedia.LightButton", text);
        }
    }

    /// <summary>Every stage surface reaches the interaction ramp through the on-media GLASS rungs, and those rungs are
    /// derived from the on-media ink rather than minted as a fourth white.</summary>
    [Fact]
    public void TheGlassRamp_IsDerivedFromTheOnMediaInk()
    {
        Assert.Equal(FluentGpu.Dsl.Tok.OnMediaPrimary.R, WaveeOnMedia.GlassHover.R);
        Assert.Equal(FluentGpu.Dsl.Tok.OnMediaPrimary.R, WaveeOnMedia.GlassPressed.R);
        Assert.Equal(0f, WaveeOnMedia.GlassRest.A);
        Assert.True(WaveeOnMedia.GlassRest.A < WaveeOnMedia.GlassHover.A);
        Assert.True(WaveeOnMedia.GlassHover.A < WaveeOnMedia.GlassPressed.A);
        // "White ~10%" — a hover rung any louder is a plate.
        Assert.True(WaveeOnMedia.GlassHover.A <= 0.12f);
    }

    /// <summary>The pane pivot speaks the SHARED rung — the context band's text-action metrics and its underline
    /// geometry — rather than a fourth private pivot. (It deliberately does not CALL <c>WaveeCta.TextAction</c>: that
    /// grammar is fenced to context bands, and a pivot is a tab, not an action.)</summary>
    [Fact]
    public void ThePivot_ReadsTheSharedRungConstants()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string chrome = File.ReadAllText(StagePath(root, "StageChrome.cs"));
        Assert.Contains("WaveeCta.TextActionSize", chrome);
        Assert.Contains("WaveeCta.TextActionWeight", chrome);
        Assert.Contains("WaveeCta.TextActionLineHeight", chrome);
        Assert.Contains("ContextBandLayout.UnderlineHeight", chrome);
        Assert.Contains("ContextBandLayout.UnderlineGap", chrome);
        // Colour-switch, not FLIP: the underline is always mounted and takes a transparent fill when inactive.
        Assert.Contains("active ? accent : ColorF.Transparent", chrome);
        // And the fence holds — the stage is NOT a text-action call site.
        foreach (string name in StageInkFiles)
            Assert.DoesNotContain("WaveeCta.TextAction(", File.ReadAllText(StagePath(root, name)));
    }

    /// <summary>The pane switch is OPACITY ONLY. Both panes stay mounted (their keys are unconditional), each carries
    /// the 250 ms control token, and hit testing follows the active one. A conditional mount here would rebuild
    /// LyricsView's measured document on every flip AND change this component's hook shape between renders.</summary>
    [Fact]
    public void ThePaneSwitch_IsOpacityOnly()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string panes = File.ReadAllText(StagePath(root, "StagePanes.cs"));
        Assert.Contains("Key = \"pane:lyrics\"", panes);
        Assert.Contains("Key = \"pane:queue\"", panes);
        Assert.Contains("Opacity = lyrics ? 1f : 0f", panes);
        Assert.Contains("Opacity = lyrics ? 0f : 1f", panes);
        Assert.Contains("HitTestVisible = lyrics", panes);
        Assert.Contains("HitTestVisible = !lyrics", panes);
        Assert.Equal(2, Regex.Matches(panes, @"Transition = MotionTok\.ControlNormal").Count);
        // No mount boundary anywhere near the panes: Flow.Show is the app's conditional-mount spelling.
        Assert.DoesNotContain("Flow.Show", panes);
    }

    /// <summary>The pane choice is a SESSION signal — static, so re-opening the stage lands on the pane you left it on
    /// — and it is not persisted anywhere (it is a view state, not a preference).</summary>
    [Fact]
    public void ThePaneChoice_IsASessionSignal()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string chrome = File.ReadAllText(StagePath(root, "StageChrome.cs"));
        Assert.Contains("static class StagePane", chrome);
        Assert.Contains("public static readonly Signal<int> Current", chrome);
        Assert.DoesNotContain("WaveeSettings.", chrome);
    }

    /// <summary>The ∞ Autoplay row drives the REAL seam: the same setting the Settings → Playback toggle and the rail's
    /// queue pill write, and the same cross-surface epoch, which <c>LiveSessionHost</c> binds into
    /// <c>PlaybackController.AutoplayEnabled</c>. A stage toggle that wrote nothing would be a dead control, which is
    /// the one thing this row was allowed to be only if no seam existed — and one does.</summary>
    [Fact]
    public void TheAutoplayRow_WritesTheRealSeam()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string panes = File.ReadAllText(StagePath(root, "StagePanes.cs"));
        Assert.Contains("WaveeSettings.AutoplayEnabled", panes);
        Assert.Contains("PlaybackPrefs.Bump()", panes);
    }

    /// <summary>Case is not part of the voice (the eyebrow rule) — and the stage has no caps role at all, because the
    /// quality badge that would have carried one is absent (the playing stream's format is not published to the UI).</summary>
    [Fact]
    public void NoStageFile_CapsTransformsItsText()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        foreach (string name in StageInkFiles)
            Assert.DoesNotContain("ToUpper", File.ReadAllText(StagePath(root, name)));
    }

    /// <summary>The pure allocator stays ENGINE-FREE, so this test class drives the real arithmetic instead of a copy
    /// of it — the <c>MergedChromeLayout</c> rule.</summary>
    [Fact]
    public void TheAllocator_IsEngineFree()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        foreach (string line in File.ReadAllLines(StagePath(root, "StageLayout.cs")))
        {
            if (!line.StartsWith("using ", StringComparison.Ordinal)) continue;
            Assert.Equal("using System;", line.Trim());
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The CODE half of a source line — everything before its first <c>//</c>. The stage files argue for their
    /// rules in prose, and that prose names the very tokens the scans forbid ("Tok.TextPrimary is dark there"); scanning
    /// comments would make the tests fail on their own documentation.</summary>
    static string Code(string line)
    {
        int i = line.IndexOf("//", StringComparison.Ordinal);
        return i < 0 ? line : line[..i];
    }

    static float FirstWidthWhere(Func<StageLayout, bool> predicate)
    {
        for (float w = 0f; w <= 4000f; w += 1f)
            if (predicate(StageLayout.FromWidth(w))) return w;
        return -1f;
    }

    static string StagePath(string root, string file) =>
        Path.Combine(root, "Features", "Player", file);

    /// <summary>src/apps/Wavee, located from THIS file's compile-time path (the MotionSystemTests idiom).</summary>
    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null!;
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        return Directory.Exists(app) ? app : null!;
    }
}
