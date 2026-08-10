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

    // ── the scrim ladder ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The stage is SINGLE-THEME art-dark, and its scrim is ONE continuous vertical system: a deepening at the
    /// top (the caption cluster), a deepening at the bottom (the pivot band and the transport), and a genuine PLATEAU at
    /// the base value through the middle where the lyrics read. The stops are FRACTIONS of the body height on purpose —
    /// that is what makes each deepening a feather hundreds of DIP long at any window size, where the boxed 88-DIP top
    /// veil it replaced was a band you could point at.</summary>
    [Fact]
    public void TheScrim_IsOneContinuousSystemWithAPlateau()
    {
        Assert.True(StageLayout.ScrimTopA > StageLayout.ScrimBaseA, "the top must be DEEPER than the base");
        Assert.True(StageLayout.ScrimBottomA > StageLayout.ScrimBaseA, "the bottom must be DEEPER than the base");
        Assert.True(StageLayout.ScrimBaseA > 0f && StageLayout.ScrimTopA < 1f);

        Assert.True(StageLayout.ScrimTopStop > 0f);
        Assert.True(StageLayout.ScrimTopStop < StageLayout.ScrimBottomStop);
        Assert.True(StageLayout.ScrimBottomStop < 1f);
        // Long feathers, both ends: a fifth of the surface each, minimum.
        Assert.True(StageLayout.ScrimTopStop >= 0.2f, "the top feather is too short to be edgeless");
        Assert.True(1f - StageLayout.ScrimBottomStop >= 0.2f, "the bottom feather is too short to be edgeless");
        // …and a real flat middle between them, not two ramps meeting.
        Assert.True(StageLayout.ScrimBottomStop - StageLayout.ScrimTopStop >= 0.3f);
    }

    /// <summary>The column shade is a PAINT layer, not a layout one — which is exactly why it can be much wider than the
    /// column BOX and feather to zero over a ramp the eye cannot locate. It must be wider than the box (otherwise it is
    /// the old boxed veil again) and its falloff must be a long multiple of the box's layout gutter.</summary>
    [Fact]
    public void TheColumnShade_IsWiderThanTheBoxAndFeathersToZero()
    {
        Assert.Equal(StageLayout.WideColumnW + StageLayout.ColumnShadeFalloffW, StageLayout.ColumnShadeW);
        Assert.True(StageLayout.ColumnShadeW > StageLayout.WideStage.LayoutWidth,
            "the shade must overhang the column BOX — a shade that stops at the box edge IS the edge");
        Assert.True(StageLayout.ColumnShadeFalloffW >= 2f * StageLayout.WideColumnFalloffW);
        Assert.True(StageLayout.ColumnShadeFalloffW >= 240f, "a short ramp to zero still reads as a smear");

        // The hold stop is exactly where the DESIGNED column ends inside the shade, so the type never sits on a moving
        // value; the mid stop is strictly inside the feather, which is what curves the ramp (a straight alpha line is
        // the shape the eye resolves as a Mach band).
        Assert.Equal(StageLayout.WideColumnW / StageLayout.ColumnShadeW, StageLayout.ColumnShadeHoldStop, 4);
        Assert.True(StageLayout.ColumnShadeMidStop > StageLayout.ColumnShadeHoldStop);
        Assert.True(StageLayout.ColumnShadeMidStop < 1f);
        Assert.True(StageLayout.ColumnShadeMidFrac > 0f && StageLayout.ColumnShadeMidFrac < 1f);
        Assert.True(StageLayout.ColumnShadeA > 0f && StageLayout.ColumnShadeA < StageLayout.ScrimBaseA);

        // The queue pane's local shade comes up out of ZERO before it is anywhere near the pane's content.
        Assert.True(StageLayout.PaneShadeFeatherStop > 0.1f && StageLayout.PaneShadeFeatherStop < 0.5f);
        Assert.True(StageLayout.PaneShadeA > 0f && StageLayout.PaneShadeA < StageLayout.ScrimBaseA);
    }

    /// <summary>The column's content span, and the reason it is authored in the pure allocator: the volume track is
    /// DERIVED from it rather than guessed at the call site.</summary>
    [Fact]
    public void TheColumnContent_IsTheDesignedColumnLessItsGutters()
    {
        Assert.Equal(StageLayout.WideColumnW - 2f * StageLayout.ColumnPadX, StageLayout.ColumnContentW);
        Assert.Equal(304f, StageLayout.ColumnContentW);
        // It still carries the cover with room to spare — the reason the gutter is 24 in the first place.
        Assert.True(StageLayout.ColumnContentW >= StageLayout.WideArtW);
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

    /// <summary>NO PLATES — meaning no THEME plates. The stage's only fills are the on-media GLASS interaction ramp,
    /// the one filled play button, the accent, and the on-media SCRIM ramp for a control that stands on ARTWORK rather
    /// than on the scrim's own deepened ground (<c>StageChrome.ScrimFab</c>, the way out). Never the theme's card /
    /// solid / subtle ladders, which is what would turn a surface whose premise is "ink on the scrim" back into a panel
    /// of boxes. The distinction is the LADDER SOURCE, not the presence of a fill: every value above comes off
    /// <c>WaveeOnMedia</c>, so it is theme-invariant by construction and moves with one token.</summary>
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
    /// derived from the on-media ink rather than minted as a fourth white.
    /// <para>GLASS IS A HOVER RAMP, NOT A GROUND: its rest rung is alpha ZERO. That is the property that makes it
    /// correct for a control standing on the scrim's own deepening and WRONG for one standing on artwork — which is
    /// why the exit uses the SCRIM ramp instead, whose rest rung is a real plate. Both ladders live in
    /// <c>WaveeOnMedia</c>; the second half of this test pins that they are two ramps and not one.</para></summary>
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

        // The SCRIM ramp: a real ground at rest, monotone through hover and press, and a ring that is not the ink.
        Assert.True(WaveeOnMedia.ScrimRest.A > 0f, "a plate whose rest rung is transparent is not a plate");
        Assert.True(WaveeOnMedia.ScrimRest.A < WaveeOnMedia.ScrimHover.A);
        Assert.True(WaveeOnMedia.ScrimHover.A < WaveeOnMedia.ScrimPressed.A);
        Assert.True(WaveeOnMedia.Stroke.A > 0f && WaveeOnMedia.Stroke.A < 0.5f);
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

    // ── the scrim's material rules, as source scans ──────────────────────────────────────────────────────────────────

    /// <summary>THE decision, in code: the stage is single-theme art-dark, so NOTHING on it branches on the theme. The
    /// deleted arm is the white light-theme base scrim — it is what forced every chrome region to bring its own boxed
    /// dark veil, and what left the theme-invariant white title sitting on a near-white ground.</summary>
    [Fact]
    public void NoStageFile_BranchesOnTheTheme()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        var offenders = new List<string>();
        foreach (string name in StageInkFiles)
        {
            string[] lines = File.ReadAllLines(StagePath(root, name));
            for (int i = 0; i < lines.Length; i++)
            {
                string l = Code(lines[i]);
                if (ThemeBranch.IsMatch(l)) offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "the stage is ALWAYS art-dark — a theme branch here is the two-world collage coming back:\n  "
            + string.Join("\n  ", offenders));
    }

    static readonly Regex ThemeBranch = new(@"Tok\.Theme|ThemeKind\.", RegexOptions.Compiled);

    /// <summary>ONE scrim system, not a patchwork. The chrome regions' boxed veils are GONE by name, and what replaced
    /// them is two full-bleed layers authored in <c>StageChrome</c> and mounted once by the surface.</summary>
    [Fact]
    public void TheScrim_IsTwoFullBleedLayersAndNoRegionVeils()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string chrome = File.ReadAllText(StagePath(root, "StageChrome.cs"));
        Assert.Contains("public static GradientSpec Scrim()", chrome);
        Assert.Contains("public static GradientSpec ColumnShade()", chrome);
        Assert.Contains("public static GradientSpec PaneShade()", chrome);
        foreach (string gone in new[] { "TopVeil(", "BottomVeil(", "HeaderVeil(", "ColumnVeil(", "PaneVeil(" })
            Assert.DoesNotContain(gone, chrome);

        // Mounted ONCE, in the backdrop stack — the surface is the only file that paints them.
        string surface = File.ReadAllText(StagePath(root, "ImmersiveLyricsSurface.cs"));
        Assert.Contains("Gradient = StageChrome.Scrim()", surface);
        Assert.Contains("Gradient = StageChrome.ColumnShade()", surface);
        // No region of stage chrome carries a gradient of its own any more. The queue pane is the ONE exception and it
        // is a mounted, cross-faded pane rather than a region — see StagePanes' header.
        Assert.DoesNotContain("Gradient =", File.ReadAllText(StagePath(root, "StageIdentity.cs")));
        Assert.Equal(1, Regex.Matches(File.ReadAllText(StagePath(root, "StagePanes.cs")), @"Gradient = ").Count);
    }

    /// <summary>Edge-invisibility, as a mechanism rather than a taste call: every shade either reaches its own boundary
    /// at alpha ZERO after a long feather, or ends at a WINDOW edge where there is no outside to contrast with. The two
    /// local shades are the first kind, and the assertion is on the literal terminating stop.</summary>
    [Fact]
    public void EveryShade_TerminatesAtZeroOrAWindowEdge()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string chrome = File.ReadAllText(StagePath(root, "StageChrome.cs"));
        // The column shade's FAR edge is inside the surface ⇒ it must land on nothing.
        Assert.Matches(new Regex(@"ColumnShade\(\)[\s\S]*?new GradientStop\(1f, Shade\(0f\)\)"), chrome);
        // The queue shade's NEAR edge is inside the surface ⇒ it must come up out of nothing.
        Assert.Matches(new Regex(@"PaneShade\(\)[\s\S]*?new GradientStop\(0f, Shade\(0f\)\)"), chrome);
    }

    // ── the column's composition ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The identity cluster is optically CENTRED in its column, and <c>Grow</c> is what makes that possible. A
    /// component's element is mounted under a host node carrying the scene's default layout, so a column that only
    /// declares a <c>Justify</c> takes its MEASURED height, sits at the top of a full-height host and distributes no
    /// free space at all — the cover pinned top-left with the window's lower half empty.
    /// <para>Anchoring it to the FLOOR (the shape this replaced) is not a neutral alternative: it puts a 300-DIP cover
    /// under the caption band on a short window and leaves a cover's worth of dead scrim above it on a tall one. A
    /// centre is only a centre if the gutters agree, so the padding is pinned SYMMETRIC here too — the previous shape
    /// carried 28 at the bottom and 0 at the top.</para></summary>
    [Fact]
    public void TheIdentityColumn_GrowsIntoItsHostAndCentresTheCluster()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string identity = File.ReadAllText(StagePath(root, "StageIdentity.cs"));
        // Both halves, in the same element: the Grow that gives the column the host's height, and the Justify that
        // spends it. Either one alone is the bug (a Justify without Grow is silently inert; Grow without a Justify
        // stretches the whitespace instead of placing the cluster).
        Assert.Matches(new Regex(@"Grow = 1f, MinHeight = 0f,\s*Direction = 1, Justify = FlexJustify\.Center,"), identity);
        // …and the vertical gutter is ONE constant, spent at both ends.
        Assert.Matches(new Regex(@"Padding = new Edges4\(ColumnPadX, ColumnPadY, L\.ColumnFalloff \+ ColumnPadX, ColumnPadY\)"),
            identity);
        Assert.DoesNotContain("ColumnPadBottom", identity);
    }

    /// <summary>THE GROW LEAK. <c>StageIdentity</c>'s wide column declares <c>Grow = 1</c> so it can fill its host and
    /// spend the free space VERTICALLY. But a component's anchor MIRRORS that <c>FlexGrow</c>
    /// (<c>Reconciler.MirrorParticipation</c>) and the anchor is also the flex item in the stage BAND — which is a ROW
    /// on the wide stage, where the very same number reads as "and half the free WIDTH". A declared <c>Width</c> is a
    /// flex BASIS, not a cap (<c>FlexLayout.ClampMain</c> clamps to Min/Max only), so the identity region silently grew
    /// past <see cref="StageLayout.LayoutWidth"/> and the pane region got less than the arithmetic anywhere else
    /// assumed — which is how a lyric line ended up clipping mid-word.
    /// <para>The band therefore OWNS the identity's horizontal participation: a wrapper with the authored width, no
    /// grow and no shrink, and <c>Direction = 1</c> so the anchor's mirrored grow can only ever be vertical.</para></summary>
    [Fact]
    public void TheIdentityRegion_ClaimsNoWidthBeyondItsColumn()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string surface = File.ReadAllText(StagePath(root, "ImmersiveLyricsSurface.cs"));
        Assert.Matches(new Regex(
                @"Key = ""stage:identity"",\s*Direction = 1, MinHeight = 0f, MinWidth = 0f,\s*Grow = 0f, Shrink = 0f,"),
            surface);
        // The width is the ladder's, and it is NOT authored in the compact shape (which claims no column at all).
        Assert.Contains("Width = L.Wide ? L.LayoutWidth : float.NaN", surface);
    }

    /// <summary>The READING COLUMN IS MEASURED, NEVER PREDICTED. It used to author its own <c>Width</c> from a
    /// viewport FORMULA — a second, private copy of the band's arithmetic — and any disagreement between that copy and
    /// the real layout lands as a column authored WIDER than the pane it sits in: the column shrinks flush to the
    /// window edge while the lyric rows inside keep the width they were measured at, and the text clips mid-word. It
    /// now GROWS into whatever the pane actually gives it, capped by <see cref="ImmersiveLyricsSurface.ColumnMaxW"/>,
    /// with the gutter spelled as real padding — and <c>FlexLayout</c> re-measures a grown row child at its FINAL main
    /// size, so a pre-shrink width is not representable.</summary>
    [Fact]
    public void TheReadingColumn_IsMeasuredNotPredicted()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string panes = File.ReadAllText(StagePath(root, "StagePanes.cs"));
        Assert.Contains("MaxWidth = ImmersiveLyricsSurface.ColumnMaxW", panes);
        Assert.Contains("ImmersiveLyricsSurface.ColumnGutter * 0.5f", panes);
        // The formula is GONE, and so is the viewport signal it needed: no CODE in this file predicts a width. (The
        // prose still names what it replaced — hence the Code() scan, exactly like the ink rules above.)
        var predictors = new List<string>();
        string[] paneLines = File.ReadAllLines(StagePath(root, "StagePanes.cs"));
        for (int i = 0; i < paneLines.Length; i++)
        {
            string l = Code(paneLines[i]);
            if (l.Contains("_viewport", StringComparison.Ordinal) || l.Contains("LayoutWidth", StringComparison.Ordinal))
                predictors.Add($"StagePanes.cs:{i + 1}: {paneLines[i].Trim()}");
        }
        Assert.True(predictors.Count == 0,
            "the reading column is measured — a viewport formula here is the clipping bug coming back:\n  "
            + string.Join("\n  ", predictors));

        // The pivot band appears three times and only three times: it IS the band (its height), and BOTH panes reserve
        // it at their bottom — so neither list's last row can sit under the pivot.
        Assert.Equal(3, Regex.Matches(panes, @"StageChrome\.PivotBandH").Count);
    }

    /// <summary>THE WAY OUT HAS TO BE VISIBLE. The stage's default control (<c>StageChrome.Glyph</c>) is PLATELESS —
    /// its rest fill is <c>GlassRest</c> at alpha ZERO — which is right on the scrim's deepened ground and wrong in the
    /// top band, the thinnest part of the scrim, sitting directly over whatever the cover happens to be. The exit is
    /// therefore a scrim-plated FAB at REST: a circle (the sanctioned on-media shape) carrying the on-media scrim ramp
    /// and the hairline ring, exactly as <c>MediaCard</c>'s cover FABs do over the same problem.</summary>
    [Fact]
    public void TheStageExit_IsAScrimPlatedFabNotAPlatelessGlyph()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string chrome = File.ReadAllText(StagePath(root, "StageChrome.cs"));
        Assert.Contains("public static BoxEl ScrimFab(", chrome);
        Assert.Matches(new Regex(@"ScrimFab\([\s\S]{0,900}?Fill = WaveeOnMedia\.ScrimRest"), chrome);
        Assert.Matches(new Regex(@"ScrimFab\([\s\S]{0,900}?BorderColor = WaveeOnMedia\.Stroke"), chrome);
        Assert.Matches(new Regex(@"ScrimFab\([\s\S]{0,900}?Corners = Radii\.Circle\(box\)"), chrome);
        // 40 DIP: the on-media FAB rung, comfortably past the 32 icon-button minimum because this one has to be found
        // over arbitrary artwork rather than merely clicked.
        Assert.Contains("public const float FabBox = 40f;", chrome);

        // The surface's top band uses it for BOTH of its buttons, and no longer reaches for the plateless glyph.
        // (Code-only: the prose above the call site names the shape it replaced.)
        string surface = File.ReadAllText(StagePath(root, "ImmersiveLyricsSurface.cs"));
        Assert.Contains("StageChrome.ScrimFab(", surface);
        foreach (string line in File.ReadAllLines(StagePath(root, "ImmersiveLyricsSurface.cs")))
            Assert.DoesNotContain("StageChrome.Glyph(", Code(line));
        // Escape stays the keyboard half of the same affordance.
        Assert.Contains("Keys.Escape", surface);
    }

    /// <summary>Every full-width row in the column is wrapped in a COLUMN, never the BoxEl default row. A row wrapper
    /// hands its single child the child's INTRINSIC main-axis size — which is what made the seek bar a stub, the volume
    /// rail a dash, and the elapsed/remaining pair collapse together with no space for the spacer between them.</summary>
    [Fact]
    public void EveryStackWrapper_IsAColumnSoItsRowSpansTheColumn()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string[] lines = File.ReadAllLines(StagePath(root, "StageIdentity.cs"));
        string[] keys = ["stage:identity-row", "stage:seek", "stage:transport", "stage:volume", "stage:device"];
        var offenders = new List<string>();
        int found = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string l = Code(lines[i]);
            // `… with { Key = … }` is a re-KEYED element, not a stack wrapper — the compact header's identity row is a
            // flex ITEM in a row and carries its own Grow/Basis, so it is deliberately out of this rule.
            if (l.Contains("with {", StringComparison.Ordinal)) continue;
            foreach (string k in keys)
            {
                if (!l.Contains("Key = \"" + k + "\"", StringComparison.Ordinal)) continue;
                found++;
                if (!l.Contains("Direction = 1", StringComparison.Ordinal))
                    offenders.Add($"StageIdentity.cs:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(found >= keys.Length, $"expected every stack wrapper to still exist (found {found})");
        Assert.True(offenders.Count == 0,
            "a stack wrapper defaulted to a ROW — its child will take its intrinsic width, not the column's:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>The volume rail is an AUTHORED length derived from the column's content span. <c>Slider.Create</c> takes
    /// a track LENGTH, not a stretch: a NaN there is not "fill the row", it is a NaN width on every part of the slider
    /// template — the whole of the "volume is a tiny dash" report.</summary>
    [Fact]
    public void TheVolumeTrack_IsDerivedFromTheColumnContentSpan()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string identity = File.ReadAllText(StagePath(root, "StageIdentity.cs"));
        Assert.Contains("StageLayout.ColumnContentW - WaveeCta.IconButtonSize - Spacing.S", identity);
        Assert.Contains("length: VolumeTrackW", identity);
        Assert.DoesNotContain("length: float.NaN", identity);
    }

    // ── the hover/press scope ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>EVERY BUTTON IS ITS OWN HOVER/PRESS SCOPE. An <c>OnContextRequested</c> handler sets ContextBit, which
    /// is in the dispatcher's hit-anywhere mask — so a menu attached straight to the identity column made the column
    /// itself the hit for every gap between controls, and the engine's hover cascade (reveal/scale descendants) plus its
    /// press cascade (then UNCONDITIONAL, every descendant with an interact row) lit the whole cluster at once. The fix
    /// is structural and this pins it: the menu goes on a ZStack SHELL whose first layer is a CHILDLESS shield that
    /// always wins the hit, so the cascade from the hit node reaches nothing while the shell stays an ancestor for the
    /// "⋯" button's ClickRequestsContext walk. The shield staying childless is the whole contract.
    /// <para>The engine has since grown the press half of the boundary (<c>AnimSuite</c> 58c: a container press stops at
    /// a nested interactive boundary). That does NOT retire the shield — the container is still the HIT, so it still
    /// owns the press and the hover for every gap, and its own non-boundary reveal descendants still follow it. Nor may
    /// the shell claim layout of its own beyond the content's: see
    /// <see cref="TheIdentityRegion_ClaimsNoWidthBeyondItsColumn"/>, the grow leak this shell is on the path of.</para></summary>
    [Fact]
    public void TheIdentityRegion_LeavesEveryButtonItsOwnHoverScope()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string identity = File.ReadAllText(StagePath(root, "StageIdentity.cs"));
        // The shield, spelled as ONE childless literal so "someone added a child to it" cannot pass unnoticed.
        Assert.Contains("new BoxEl { Key = ContextShieldKey }.WithContextMenu(", identity);
        // The shell is a ZStack (the shield must be able to sit UNDER the content, not beside it).
        Assert.Matches(new Regex(@"static BoxEl ContextScope\([\s\S]{0,400}?ZStack = true"), identity);
        // Exactly two attach points — the shell and the shield — and nothing else in the column owns a menu.
        Assert.Equal(2, Regex.Matches(identity, @"\.WithContextMenu\(").Count);
    }

    // ── the lyrics ink seam ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The lyrics were the ONLY thing on the stage painting theme ink, and that is the whole reason the base
    /// scrim used to flip. <c>LyricsInk</c> is the seam: a MODE (not a captured colour, so the rail still follows a live
    /// theme flip), passed by the stage as <c>onMedia: true</c> and left at its theme default by the rail.</summary>
    [Fact]
    public void TheStageLyrics_TakeTheOnMediaInkAndTheRailDoesNot()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string ink = File.ReadAllText(StagePath(root, "LyricsInk.cs"));
        Assert.Contains("readonly record struct LyricsInk", ink);
        Assert.Contains("WaveeOnMedia.Ink", ink);
        Assert.Contains("Tok.TextPrimary", ink);   // the theme arm still exists — the rail is not on media

        Assert.Contains("new LyricsView(large: true, onMedia: true", File.ReadAllText(StagePath(root, "StagePanes.cs")));

        string rail = File.ReadAllText(StagePath(root, "RightRail.cs"));
        Assert.Contains("new LyricsView(", rail);
        Assert.DoesNotContain("onMedia:", rail);
    }

    /// <summary>…and the reading surface itself no longer names a theme rung anywhere. The ONE fenced exception is the
    /// env-gated lyrics-search DEBUG panel, which is a developer surface on an opaque theme plate — white-on-media ink
    /// over a light solid plate would be the very invisibility this seam exists to remove.</summary>
    [Fact]
    public void TheLyricsReadingSurface_PaintsNoThemeInk()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string[] lines = File.ReadAllLines(StagePath(root, "LyricsView.cs"));
        var offenders = new List<string>();
        bool fenced = false;
        int fences = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("ink-scan: off", StringComparison.Ordinal)) { fenced = true; fences++; continue; }
            if (lines[i].Contains("ink-scan: on", StringComparison.Ordinal)) { fenced = false; fences++; continue; }
            if (fenced) continue;
            if (ThemeInk.IsMatch(Code(lines[i]))) offenders.Add($"LyricsView.cs:{i + 1}: {lines[i].Trim()}");
        }
        Assert.Equal(2, fences);   // one fenced span, closed — an unclosed fence would hide the whole tail of the file
        Assert.True(offenders.Count == 0,
            "the lyrics reading surface takes its ink from LyricsInk, so the stage can be on media:\n  "
            + string.Join("\n  ", offenders));

        string view = File.ReadAllText(StagePath(root, "LyricsView.cs"));
        Assert.Contains("_ink.Primary", view);
        Assert.Contains("_ink.Secondary", view);
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
