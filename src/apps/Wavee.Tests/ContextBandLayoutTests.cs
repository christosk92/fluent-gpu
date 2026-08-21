using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The text-chrome context band: fixed geometry, horizontal-overflow structure and the scroll spy (which section is
/// "here"), plus the source gates that keep the band's material and grammar from drifting back.
/// </summary>
public class ContextBandLayoutTests
{
    // ── the band's fixed geometry ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band is 56 DIP and it is the SAME 56 the detail collapse ladder targets. If these two ever
    /// diverge, the artist hero's PresentedH bind lands on a height its band does not fill and the page shows a strip
    /// of raw content between the hero and the band.</summary>
    [Fact]
    public void BandHeight_IsTheOneCollapseFloor()
    {
        Assert.Equal(56f, ContextBandLayout.Height);
        Assert.Equal(DetailVerticalLayout.CompactIdentityHeight, ContextBandLayout.Height);
    }

    /// <summary>ONE hairline, and the active mark is the tab-strip's 2-DIP rung. Both are load-bearing: a second line
    /// would show the seam between the band's two pinned strata, and a mark thicker than the tab strip's would make
    /// page wayfinding louder than app wayfinding.</summary>
    [Fact]
    public void BandEdges_AreOneHairlineAndOneTwoDipMark()
    {
        Assert.Equal(1f, ContextBandLayout.HairlineHeight);
        Assert.Equal(2f, ContextBandLayout.UnderlineHeight);
    }

    // ── the estimator ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LabelEstimate_IsMonotoneAndCountsBothPaddings()
    {
        float pad = ContextBandLayout.PivotPadX;
        Assert.Equal(2f * pad, ContextBandLayout.EstimateLabelWidth("", pad));
        Assert.Equal(2f * pad, ContextBandLayout.EstimateLabelWidth((string?)null, pad));
        float last = 0f;
        for (int n = 0; n <= 40; n++)
        {
            float w = ContextBandLayout.EstimateLabelWidth(n, pad);
            Assert.True(w >= last, $"estimate went backwards at {n}");
            last = w;
        }
        // A negative length is arithmetic noise, never a negative slot.
        Assert.Equal(2f * pad, ContextBandLayout.EstimateLabelWidth(-5, pad));
    }

    [Fact]
    public void ActionsWidth_IsTheSumPlusTheGapsBetween()
    {
        Assert.Equal(0f, ContextBandLayout.ActionsWidth(ReadOnlySpan<float>.Empty));
        Assert.Equal(40f, ContextBandLayout.ActionsWidth([40f]));
        Assert.Equal(40f + 60f + ContextBandLayout.ActionGap, ContextBandLayout.ActionsWidth([40f, 60f]));
        Assert.Equal(30f + 30f + 30f + 2f * ContextBandLayout.ActionGap,
            ContextBandLayout.ActionsWidth([30f, 30f, 30f]));
    }

    // ── horizontal overflow ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PivotOverflow_ScrollsWithAnAlphaFadeAndNeverDropsTrailingSections()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string pivot = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ContextBand.cs"));
        Assert.Contains("int shown = Math.Min(p.Items.Length, MaxItems)", pivot);
        Assert.Contains("Horizontal = true", pivot);
        Assert.Contains("SuppressScrollBar = true", pivot);
        Assert.Contains("AutoEdgeFade = true", pivot);
        Assert.Contains("EdgeCues = ScrollEdgeCues.None", pivot);
        Assert.DoesNotContain("p.Visible", pivot);
    }

    [Fact]
    public void ActivePivotTab_IsAutomaticallyRevealedWithoutMovingTheFixedClusters()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string pivot = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ContextBand.cs"));
        Assert.Contains("RevealActive(current)", pivot);
        Assert.Contains("ScrollIntoView.BringInto(Context, _tabViewport, node, Spacing.S", pivot);

        string artist = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ArtistCompactBar.cs"));
        Assert.Contains("MaxWidth = ContextBandLayout.TitleCap", artist);
        Assert.Contains("Grow = 1f, Basis = 0f, MinWidth = 0f", artist);
        Assert.Contains("Element actions = new BoxEl", artist);
        Assert.Contains("Shrink = 0f", artist);
        Assert.DoesNotContain("ContextBandLayout.Resolve", artist);
    }

    // ── the scroll spy ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>At the top of the page the FIRST section is the answer, not "none" — a pivot with no mark reads as
    /// broken, and the visitor genuinely is looking at section one.</summary>
    [Fact]
    public void AtRest_TheFirstSectionIsActive()
    {
        // Nothing has crossed: every top is below the band.
        Assert.Equal(0, ContextBandLayout.ActiveSection(
            [600f, 1200f, 1800f], ContextBandLayout.Height, 800f, atScrollEnd: false));
    }

    /// <summary>Arrival is early enough to describe what dominates the viewport: the incoming heading crosses the
    /// upper quarter of the usable region below the band, with the small probe retained as boundary tolerance.</summary>
    [Fact]
    public void ArrivalIsMeasuredAtTheUpperQuarterOfTheUsableViewport()
    {
        float band = ContextBandLayout.Height;
        const float viewport = 800f;
        float line = ContextBandLayout.SpyLine(band, viewport);
        Assert.Equal(250f, line);
        Assert.Equal(0, ContextBandLayout.ActiveSection(
            [-400f, line + 1f, 900f], band, viewport, atScrollEnd: false));
        Assert.Equal(1, ContextBandLayout.ActiveSection(
            [-400f, line, 900f], band, viewport, atScrollEnd: false));
    }

    [Fact]
    public void ActivationLine_TracksViewportHeightAndFailsSoftBeforeMeasurement()
    {
        float band = ContextBandLayout.Height;
        Assert.Equal(150f, ContextBandLayout.SpyLine(band, 400f));
        Assert.Equal(250f, ContextBandLayout.SpyLine(band, 800f));
        Assert.Equal(band + ContextBandLayout.SpyProbe, ContextBandLayout.SpyLine(band, 0f));
    }

    [Fact]
    public void ScrollEnd_RequiresRealOverflowAndMovement()
    {
        Assert.False(ContextBandLayout.IsAtScrollEnd(0f, 800f, 800f));
        Assert.False(ContextBandLayout.IsAtScrollEnd(0f, 800f, 1200f));
        Assert.False(ContextBandLayout.IsAtScrollEnd(380f, 800f, 1200f));
        Assert.True(ContextBandLayout.IsAtScrollEnd(392f, 800f, 1200f));
        Assert.True(ContextBandLayout.IsAtScrollEnd(400f, 800f, 1200f));
    }

    /// <summary>A short final shelf cannot reach the quarter line when there is not enough content below it. At the
    /// real lower limit it nevertheless owns the page, while an unrealized tail still cannot be invented.</summary>
    [Fact]
    public void AtScrollEnd_TheLastMeasuredSectionWinsBelowTheQuarterLine()
    {
        float band = ContextBandLayout.Height;
        const float viewport = 800f;
        float belowLine = ContextBandLayout.SpyLine(band, viewport) + 160f;

        Assert.Equal(1, ContextBandLayout.ActiveSection(
            [-700f, -40f, belowLine], band, viewport, atScrollEnd: false));
        Assert.Equal(2, ContextBandLayout.ActiveSection(
            [-700f, -40f, belowLine], band, viewport, atScrollEnd: true));
        Assert.Equal(1, ContextBandLayout.ActiveSection(
            [-700f, -40f, float.NaN], band, viewport, atScrollEnd: true));
    }

    /// <summary>Walking a whole page top to bottom: the active index is non-decreasing and lands on the last section.
    /// This is the property a hand-written "nearest section" scan gets wrong at the bottom of the page, where the last
    /// section is shorter than the viewport and never reaches the top edge.</summary>
    [Fact]
    public void ScrollingDown_AdvancesMonotonicallyAndEndsOnTheLastSection()
    {
        float[] contentTops = [0f, 700f, 1500f, 2100f, 2600f];
        float band = ContextBandLayout.Height;
        var tops = new float[contentTops.Length];
        int previous = 0;
        for (float offset = 0f; offset <= 2800f; offset += 5f)
        {
            for (int i = 0; i < tops.Length; i++) tops[i] = contentTops[i] - offset;
            int at = ContextBandLayout.ActiveSection(tops, band, 800f, atScrollEnd: false);
            Assert.True(at >= previous, $"active index went backwards at offset {offset}");
            previous = at;
        }
        Assert.Equal(contentTops.Length - 1, previous);
    }

    /// <summary>An unmeasured section (NaN) stops the scan instead of counting as arrived — otherwise a page whose
    /// lower half has not laid out yet would jump the mark to its final section on the first frame.</summary>
    [Fact]
    public void AnUnrealizedSection_StopsTheScan()
    {
        Assert.Equal(1, ContextBandLayout.ActiveSection(
            [-900f, -100f, float.NaN, float.NaN], ContextBandLayout.Height, 800f, atScrollEnd: false));
    }

    /// <summary>A scan that learned NOTHING (not even the first section has a measurement) reports −1 — "no answer,
    /// hold what you had" — rather than 0.
    ///
    /// <para>This is D40's tell, promoted to a contract. The spy's registry was being emptied after the frame that
    /// filled it, so every scan saw an all-unrealized page; answering 0 there published "you are in section one" as a
    /// positive fact derived from zero evidence, which made a DEAD spy look exactly like a working spy that was stuck
    /// on the first item. −1 is the honest answer and the live caller ignores it, so the mark holds instead of
    /// snapping home.</para></summary>
    [Fact]
    public void AScanThatLearnedNothing_HoldsTheLastAnswerInsteadOfSnappingToTheFirst()
    {
        Assert.Equal(-1, ContextBandLayout.ActiveSection(
            [float.NaN, float.NaN], ContextBandLayout.Height, 800f, atScrollEnd: false));
        Assert.Equal(-1, ContextBandLayout.ActiveSection(
            [float.NaN, -900f], ContextBandLayout.Height, 800f, atScrollEnd: true));
        // …but ONE realized section is evidence, and it answers normally.
        Assert.Equal(0, ContextBandLayout.ActiveSection(
            [-900f, float.NaN], ContextBandLayout.Height, 800f, atScrollEnd: true));
    }

    [Fact]
    public void AnEmptyPivot_HasNoActiveSection()
        => Assert.Equal(-1, ContextBandLayout.ActiveSection(
            ReadOnlySpan<float>.Empty, ContextBandLayout.Height, 800f, atScrollEnd: true));

    [Fact]
    public void StackedBiographyUsesNaturalHeight_AndTheSpyObservesTheRealEnd()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string biography = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ArtistPage.Biography.cs"));
        Assert.Contains("DetailLayoutBreakpoints.ModeFor", biography);
        Assert.Contains("Grow = wide ? 2f : 0f, Basis = wide ? 0f : float.NaN", biography);
        Assert.Contains("Grow = wide ? 1f : 0f, Basis = wide ? 0f : float.NaN", biography);
        Assert.Contains("artist-biography:stacked", biography);
        Assert.DoesNotContain("Grow = wide ? 2f : 1f, Basis = 0f", biography);

        string page = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ArtistPage.cs"));
        Assert.Contains("pageAtEnd", page);
        Assert.Contains("ContextBandLayout.IsAtScrollEnd(g.OffsetY, g.ViewportH, g.ContentH)", page);

        string pivot = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ContextBand.cs"));
        Assert.Contains("_ = _atScrollEnd.Value", pivot);
        Assert.Contains("_atScrollEnd.Peek()", pivot);
    }

    [Fact]
    public void ScrollTarget_ParksTheSectionUnderTheBandAndNeverGoesNegative()
    {
        Assert.Equal(944f, ContextBandLayout.ScrollTargetFor(400f, 600f, ContextBandLayout.Height));
        Assert.Equal(0f, ContextBandLayout.ScrollTargetFor(0f, -400f, ContextBandLayout.Height));
    }

    // ── source gates: the band's material and grammar ────────────────────────────────────────────────────────────

    /// <summary>THE OFFSET MODEL, pinned. The band paints NO fill — not an opaque one, not a translucent one — and
    /// there is no band colour left in the token layer to reach for.
    ///
    /// <para>This inverts the gate that used to live here. The band WAS an opaque flatten of the content layer
    /// (<c>ColorContrast.Flatten(FileArea, MicaRef.*Default)</c>), which is the most honest constant available and was
    /// still an APPROXIMATION of a surface it could not observe: live Mica takes its colour from the user's desktop,
    /// so on a dark wallpaper the reference tone rendered as a solid black slab across the page. Scrolled content is
    /// clipped at the band's lower edge now, so the band region shows the page's real ground and there is nothing left
    /// to drift.</para></summary>
    [Fact]
    public void BandMaterial_IsNothing_TheBandPaintsNoFill()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        // The token is GONE, not merely unused — a colour named for the band is an invitation to paint it.
        string tokens = File.ReadAllText(Path.Combine(root, "Design", "WaveeTokens.cs"));
        Assert.DoesNotContain("ColorF ContextBand", tokens);
        Assert.DoesNotContain("ContextBandOver", tokens);

        // …and neither band arm assigns a Fill anywhere. The hairline (a child BoxEl) is the band's only paint, and it
        // is asserted separately below.
        foreach (string file in new[] { "ContextBand.cs", "ArtistCompactBar.cs" })
        {
            string text = File.ReadAllText(Path.Combine(root, "Features", "Detail", file));
            Assert.DoesNotContain("Fill = ContextBand", text);
            Assert.DoesNotContain("WaveeColors.ContextBand", text);
        }
        string bandSrc = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ContextBand.cs"));
        // The row is unpainted AND still a hit target — the engine's hit test is geometric, never paint-derived.
        int row = bandSrc.IndexOf("public static Element Row(", StringComparison.Ordinal);
        Assert.True(row >= 0);
        string rowDecl = bandSrc[row..Math.Min(bandSrc.Length, row + 600)];
        Assert.DoesNotContain("Fill =", rowDecl);
        Assert.Contains("HitTestVisible = true", rowDecl);
    }

    /// <summary>The other half of the contract: if the band paints nothing, the page owes it a CLIP, and that clip
    /// must cover the band's WHOLE height or content shows through the gap.
    ///
    /// <para>The artist band is the identity row alone (56). The detail band is the identity row PLUS the tracklist's
    /// column row and the shared hairline, which is exactly what <c>StickyClipInset</c> already sums — so the two
    /// pages clip at two different numbers for one reason, and that reason is arithmetic rather than taste.</para></summary>
    [Fact]
    public void TheClipInset_CoversTheWholeBand()
    {
        // The artist arm: the band IS the identity row.
        Assert.Equal(ContextBandLayout.Height, ContextBandLayout.ClipInset);

        // The detail arm: identity row + column header + the band's one hairline, with nothing left over.
        Assert.Equal(ContextBandLayout.Height + ContextBandLayout.HairlineHeight
                     + DetailVerticalLayout.ChromeHeaderHeight,
                     DetailVerticalLayout.StickyClipInset());
        Assert.True(DetailVerticalLayout.StickyClipInset() > ContextBandLayout.ClipInset);

        // …and it grows with the optional Liked filter rail, which is part of the same pinned plate.
        Assert.Equal(DetailVerticalLayout.StickyClipInset() + 48f,
                     DetailVerticalLayout.StickyClipInset(contentFilterExtent: 48f));

        // The cut is feathered, not guillotined, and both paths use the SAME band so they dissolve identically.
        Assert.Equal(DetailVerticalLayout.StickyFadeBand, ContextBandLayout.ClipFadeBand);
        Assert.True(ContextBandLayout.ClipFadeBand > 0f);
    }

    /// <summary>Every surface that pins the band owes the clip, and the surface-colour scroll-edge cue must be OFF
    /// there: that cue paints an opaque gradient at the viewport's top edge in a colour resolved by an ANCESTOR walk,
    /// which on these pages sails past the ground (a ZStack sibling) and lands a wrong-tone slab over the band.</summary>
    [Fact]
    public void EveryBandSurface_ClipsItsContentAndOptsOutOfTheEdgeCue()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string artist = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ArtistPage.cs"));
        // scroll-v3 WP-R3: the raw ScrollBindDsl { ClipTopAtViewport = ContextBand.ClipInset } row became the
        // .ClipBelow(ContextBand.ClipInset) recipe (Dsl/ScrollBindDsl.cs ScrollRecipes.ClipBelow) — same POD row,
        // authored form only.
        Assert.Contains(".ClipBelow(ContextBand.ClipInset)", artist);
        Assert.Contains("ScrollEdgeCues.None", artist);

        string tracks = File.ReadAllText(Path.Combine(root, "Features", "Detail", "DetailTracks.cs"));
        // The trailing (album / show) path and the virtual (playlist / liked) path, each with its own mechanism.
        Assert.Contains("ClipTopAtViewport = stickyInset", tracks);
        Assert.Contains("ItemClipTopInset = stickyInset", tracks);
        Assert.Contains("ItemClipTopFadeBand = DetailVerticalLayout.StickyFadeBand", tracks);
        Assert.Contains("EdgeCues = ScrollEdgeCues.None", tracks);
    }

    /// <summary>THE REGISTRY EDGE (D40). The band's anchor registry is filled by <c>OnRealized</c>, which the
    /// reconciler fires while committing the tree a render returned; <c>UseEffect</c> bodies run after PRESENT — i.e.
    /// AFTER that. So the reset that drops a previous artist's nodes must be taken in RENDER, never scheduled as an
    /// effect: as an effect it erased the registrations of the very frame that made them, and since a node realizes
    /// exactly once nothing ever put them back. On the FIRST mount that left the band with a null viewport (the spy
    /// bailed at its guard, the underline never left "Top tracks") and every pivot click resolving a null node
    /// (nothing happened) — one cause, both symptoms.</summary>
    [Fact]
    public void TheAnchorRegistry_IsResetDuringRender_NotFromAnEffect()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        string artist = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ArtistPage.cs"));

        int reset = artist.IndexOf("_anchors.Reset()", StringComparison.Ordinal);
        Assert.True(reset >= 0, "the registry must still be dropped when the page changes identity");
        Assert.Equal(reset, artist.LastIndexOf("_anchors.Reset()", StringComparison.Ordinal));   // exactly one edge

        // It is guarded by the route the registry currently holds nodes for — a render-time compare, not a dep list.
        int guard = artist.LastIndexOf("_anchorsRoute", 0 + reset, StringComparison.Ordinal);
        Assert.True(guard >= 0 && reset - guard < 200, "the reset must be guarded by the _anchorsRoute compare");

        // And it is NOT inside any UseEffect/UseLayoutEffect body: no effect opener may precede it without its
        // closing `}, ` dep tail in between.
        foreach (string hook in new[] { "UseEffect(", "UseLayoutEffect(" })
        {
            int open = artist.LastIndexOf(hook, reset, StringComparison.Ordinal);
            if (open < 0) continue;
            Assert.Contains("}, ", artist[open..reset]);
        }
    }

    /// <summary>THE HIT EDGE (D40). The offset model's clip is not merely a paint: content guillotined at an unpainted
    /// band's lower edge is a LATER sibling than the band (the page column is hero → sentinel → body, and both hit
    /// walks keep the LAST matching child), so without an input dual those invisible rows win every click aimed at the
    /// band. The engine now gates the sticky cut in <c>InputDispatcher</c> — this pins that the app's whole band
    /// depends on it, in both walks, because the app-side symptom (a pivot that scrolls nothing, a Play that plays
    /// nothing) is silent.</summary>
    [Fact]
    public void TheStickyClip_GatesInputAsWellAsPaint()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        string engine = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!, "FluentGpu.Engine");
        if (!Directory.Exists(engine)) { Assert.Skip("engine sources not present"); return; }

        string dispatcher = File.ReadAllText(Path.Combine(engine, "Input", "InputDispatcher.cs"));
        // The gate exists and is applied by BOTH walks — the handler-gated Hit and the handler-less HitAny (which
        // resolves wheel / drop targets, so a miss there sends the scroll to the wrong scroller).
        Assert.Contains("private static bool ClipRectAdmits", dispatcher);
        Assert.Equal(3, Regex.Matches(dispatcher, @"ClipRectAdmits\b").Count);   // the definition + the two call sites
        // It keys off the sticky cut's sentinel sides, so a finite reveal/flight clip stays paint-only.
        Assert.Contains("NodePaint.StickyClipSpan", dispatcher);
        Assert.Contains("NodePaint.StickyClipSpan",
            File.ReadAllText(Path.Combine(engine, "Animation", "ScrollBindEval.cs")));
    }

    /// <summary>NO SHADOW anywhere in the band. Zune chrome carries none, an opaque surface needs none to be a
    /// boundary, and the three deleted floating objects all had one — so a returning <c>Shadow</c>/<c>Elevation</c> in
    /// these files is the old idiom growing back.</summary>
    [Fact]
    public void TheBandCarriesNoElevation()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        foreach (string file in new[] { "ContextBand.cs", "ContextBandLayout.cs", "ArtistCompactBar.cs" })
        {
            string text = File.ReadAllText(Path.Combine(root, "Features", "Detail", file));
            Assert.DoesNotContain("Shadow =", text);
            Assert.DoesNotContain("Elevation.", text);
        }
    }

    /// <summary>The deleted chrome is DELETED, not hidden: the artist page's tinted capsule bar (avatar + accent-pulled
    /// acrylic fallback) and the detail page's floating identity capsule / circle play FAB are gone from source. Each
    /// token below named a part of exactly one of them.</summary>
    [Theory]
    [InlineData("Features/Detail/ArtistCompactBar.cs", "AvatarSize")]
    [InlineData("Features/Detail/ArtistCompactBar.cs", "DarkFallbackPull")]
    [InlineData("Features/Detail/ArtistCompactBar.cs", "AcrylicFlyout")]
    [InlineData("Features/Detail/DetailVerticalHero.cs", "compactPill")]
    [InlineData("Features/Detail/DetailVerticalHero.cs", "compactArtwork")]
    [InlineData("Features/Detail/DetailVerticalHero.cs", "compactPlay")]
    [InlineData("Features/Detail/DetailVerticalLayout.cs", "CompactPillWidthCap")]
    [InlineData("Features/Detail/DetailVerticalLayout.cs", "CompactPlaySize")]
    public void TheOldStickyChrome_IsGoneFromSource(string relative, string token)
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        string text = File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.DoesNotContain(token, text);
    }

    /// <summary>The text-action grammar exists, is on the band's rung, and rides the accent INK ladder for its primary
    /// and toggled arms — and it is FENCED to context bands in prose, so the third CTA grammar cannot quietly become a
    /// general one.</summary>
    [Fact]
    public void TextAction_IsTheBandRungAndIsFencedToContextBands()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string cta = File.ReadAllText(Path.Combine(root, "Design", "WaveeCta.cs"));
        Assert.Contains("public static BoxEl TextAction(", cta);
        Assert.Contains("TextActionSize = 14f", cta);
        Assert.Contains("TextActionWeight = 600", cta);
        Assert.Contains("Tok.AccentTextPrimary", cta);
        Assert.Contains("Tok.TextSecondary", cta);
        Assert.Contains("AutomationRole.Button", cta);
        Assert.Contains("Focusable = true", cta);
        Assert.Contains("CursorId.Hand", cta);
        // The fence, in the file that owns the grammar.
        Assert.Contains("CONTEXT BAND", cta);
        // A text action must not caps-transform its localized label (Wave 3's rule) nor grow under the pointer.
        Assert.DoesNotContain("ToUpper", cta);
    }

    /// <summary>Only the band may speak the text-action grammar. A new call site outside these files means the third
    /// grammar has escaped its fence — which is the failure mode <c>WaveeCta</c>'s header exists to prevent.</summary>
    [Fact]
    public void OnlyContextBandSurfaces_CallTextAction()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string[] sanctioned =
        [
            "WaveeCta.cs",          // the definition
            "ContextBand.cs",       // the band's own pivot links read its rung constants
            "ArtistCompactBar.cs",  // the artist band
            "DetailTracks.cs",      // the detail band's Find / Filter / Play cluster
            "SaveButton.cs",        // the band's Follow toggle
        ];
        var offenders = new List<string>();
        var call = new Regex(@"WaveeCta\.TextAction\s*\(", RegexOptions.Compiled);
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            string name = Path.GetFileName(path);
            if (Array.IndexOf(sanctioned, name) >= 0) continue;
            if (call.IsMatch(File.ReadAllText(path))) offenders.Add(name);
        }
        Assert.True(offenders.Count == 0,
            "WaveeCta.TextAction is scoped to context bands; new call sites:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>src/apps/Wavee, located from THIS file's compile-time path (the MotionSystemTests idiom).</summary>
    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null!;
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        return Directory.Exists(app) ? app : null!;
    }
}
