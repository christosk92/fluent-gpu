using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The text-chrome context band: the pure allocator (what the 56-DIP bar carries at a width) and the scroll spy
/// (which section is "here"), plus the source gates that keep the band's material and grammar from drifting back.
///
/// <para>Modelled on <c>MergedChromeLayoutTests</c>: a width LADDER rather than a handful of spot checks, because the
/// defect class here is a band that behaves differently at two widths a few DIP apart — and because the one property
/// that actually matters for a resize drag (widening never removes anything) can only be shown by walking.</para>
/// </summary>
public class ContextBandLayoutTests
{
    // A representative artist pivot: the sections an artist page really renders, at their real English lengths.
    static readonly string[] ArtistPivot =
    [
        "Top tracks", "Albums", "Singles & EPs", "Compilations", "Appears on",
        "Music videos", "Biography", "Fans also like",
    ];

    static float[] PivotWidths(params string[] labels)
    {
        var w = new float[labels.Length];
        for (int i = 0; i < labels.Length; i++)
            w[i] = ContextBandLayout.EstimateLabelWidth(labels[i], ContextBandLayout.PivotPadX);
        return w;
    }

    static float ArtistActions()
        => ContextBandLayout.ActionsWidth(
        [
            ContextBandLayout.EstimateLabelWidth("Play", ContextBandLayout.ActionPadX),
            ContextBandLayout.EstimateLabelWidth("Following", ContextBandLayout.ActionPadX),
        ]);

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

    // ── the fit ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The title floor and cap are honoured in both directions: a one-character page name still claims the
    /// floor (a title slot narrower than that identifies nothing), and a 300-character playlist name never claims more
    /// than the cap (past it the room is better spent on the pivot, which does something).</summary>
    [Fact]
    public void Title_IsClampedToItsFloorAndCap()
    {
        var wide = ContextBandLayout.Resolve(1400f, 4f, 200f, PivotWidths(ArtistPivot));
        Assert.Equal(ContextBandLayout.TitleFloor, wide.TitleWidth);

        var huge = ContextBandLayout.Resolve(1400f, 5000f, 200f, PivotWidths(ArtistPivot));
        Assert.Equal(ContextBandLayout.TitleCap, huge.TitleWidth);
    }

    /// <summary>THE priority rule. At a width that cannot hold everything, the pivot is what is missing — never the
    /// title, never the actions. (The actions are not modelled as droppable at all: this asserts the band still
    /// budgets their full claim, i.e. the leftover handed to the pivot never borrows from them.)</summary>
    [Fact]
    public void UnderPressure_ThePivotYieldsAndTheTitleAndActionsDoNot()
    {
        var pivot = PivotWidths(ArtistPivot);
        float actions = ArtistActions();
        const float width = 560f;
        var tight = ContextBandLayout.Resolve(width, 240f, actions, pivot);

        Assert.True(tight.TitleWidth >= ContextBandLayout.TitleFloor);
        Assert.True(tight.PivotCount > 0, "the pivot vanished at a width that could still hold part of it");
        Assert.True(tight.PivotCount < ArtistPivot.Length);
        Assert.True(tight.PivotTruncated);

        // What the pivot actually claimed plus everything ahead of it still fits the band it was resolved against.
        float claimed = 0f;
        for (int i = 0; i < tight.PivotCount; i++)
            claimed += pivot[i] + (i > 0 ? ContextBandLayout.PivotGap : 0f);
        Assert.True(claimed + tight.TitleWidth + actions + 2f * ContextBandLayout.ClusterGap <= width + 0.01f,
            "the band allocated more than the width it was handed");
    }

    /// <summary>Below the title floor + the actions' claim there is nothing left to allocate, and the band says so by
    /// dropping the pivot entirely — it does NOT hand out a negative budget or a lone item that would clip. (The two
    /// survivors still overflow the arithmetic at that width; that is deliberate and is what the title's
    /// <c>Shrink</c>/ellipsis absorbs in the real row — the actions are never the thing that clips.)</summary>
    [Fact]
    public void BelowTheFloors_ThePivotIsAbsentEntirely()
    {
        var pivot = PivotWidths(ArtistPivot);
        var fit = ContextBandLayout.Resolve(360f, 240f, ArtistActions(), pivot);
        Assert.Equal(0, fit.PivotCount);
        Assert.True(fit.PivotTruncated);
        Assert.True(fit.TitleWidth >= ContextBandLayout.TitleFloor);
    }

    /// <summary>Truncation is from the RIGHT: the surviving items are always a PREFIX of the section order, so the
    /// pivot stays a walk down the page rather than an arbitrary subset of it.</summary>
    [Theory]
    [InlineData(400f)]
    [InlineData(560f)]
    [InlineData(720f)]
    [InlineData(900f)]
    [InlineData(1100f)]
    [InlineData(1400f)]
    public void Truncation_KeepsAPrefixOfTheSectionOrder(float width)
    {
        var pivot = PivotWidths(ArtistPivot);
        var fit = ContextBandLayout.Resolve(width, 200f, ArtistActions(), pivot);

        Assert.InRange(fit.PivotCount, 0, ArtistPivot.Length);
        Assert.Equal(fit.PivotCount < ArtistPivot.Length, fit.PivotTruncated);

        // The prefix property, restated as the thing the renderer relies on: item i is shown ⇒ every earlier item is.
        int shownDirect = ContextBandLayout.FitPivots(
            width - Math.Clamp(200f, ContextBandLayout.TitleFloor, ContextBandLayout.TitleCap)
                  - ArtistActions() - 2f * ContextBandLayout.ClusterGap,
            pivot);
        Assert.Equal(fit.PivotCount, shownDirect);
    }

    /// <summary>THE resize invariant, walked rather than spot-checked: widening the window never REMOVES a pivot item.
    /// A non-monotone allocator is invisible in a screenshot and unmissable under a drag.</summary>
    [Fact]
    public void WideningNeverRemovesAPivotItem()
    {
        var pivot = PivotWidths(ArtistPivot);
        float actions = ArtistActions();
        int previous = -1;
        for (float w = 0f; w <= 2000f; w += 1f)
        {
            var fit = ContextBandLayout.Resolve(w, 260f, actions, pivot);
            Assert.True(fit.PivotCount >= previous,
                $"pivot lost an item as the band widened to {w} ({previous} → {fit.PivotCount})");
            previous = fit.PivotCount;
        }
        Assert.Equal(ArtistPivot.Length, previous);   // the full pivot is reachable at desktop widths
    }

    /// <summary>A band with no room at all still returns a usable title slot and simply has no pivot — it must never
    /// return a negative slot or a count the renderer would index past.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-40f)]
    [InlineData(40f)]
    [InlineData(88f)]
    public void DegenerateWidths_ProduceNoPivotAndNoNegativeSlot(float width)
    {
        var fit = ContextBandLayout.Resolve(width, 300f, 200f, PivotWidths(ArtistPivot));
        Assert.True(fit.TitleWidth >= 0f);
        Assert.Equal(0, fit.PivotCount);
        Assert.True(fit.PivotTruncated);
    }

    [Fact]
    public void AnEmptyPivot_IsNeverReportedAsTruncated()
    {
        var fit = ContextBandLayout.Resolve(1400f, 200f, 200f, ReadOnlySpan<float>.Empty);
        Assert.Equal(0, fit.PivotCount);
        Assert.False(fit.PivotTruncated);
    }

    [Fact]
    public void FitPivots_NeverExceedsWhatItWasGiven()
    {
        Assert.Equal(0, ContextBandLayout.FitPivots(0f, [10f, 10f]));
        Assert.Equal(0, ContextBandLayout.FitPivots(-10f, [10f, 10f]));
        Assert.Equal(2, ContextBandLayout.FitPivots(100000f, [10f, 10f]));
        // Exactly enough for two: 10 + gap + 10.
        Assert.Equal(2, ContextBandLayout.FitPivots(20f + ContextBandLayout.PivotGap, [10f, 10f]));
        Assert.Equal(1, ContextBandLayout.FitPivots(20f + ContextBandLayout.PivotGap - 1f, [10f, 10f]));
    }

    // ── the scroll spy ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>At the top of the page the FIRST section is the answer, not "none" — a pivot with no mark reads as
    /// broken, and the visitor genuinely is looking at section one.</summary>
    [Fact]
    public void AtRest_TheFirstSectionIsActive()
    {
        // Nothing has crossed: every top is below the band.
        Assert.Equal(0, ContextBandLayout.ActiveSection([600f, 1200f, 1800f], ContextBandLayout.Height));
    }

    /// <summary>Arrival is measured against the BAND's lower edge, not the window's: a section whose top is under the
    /// bar is hidden behind the very chrome that names it, so it must already be the active one.</summary>
    [Fact]
    public void ArrivalIsMeasuredAgainstTheBandNotTheWindow()
    {
        float band = ContextBandLayout.Height;
        // Section 1's top sits 10 DIP below the viewport top — i.e. BEHIND a 56-DIP band.
        Assert.Equal(1, ContextBandLayout.ActiveSection([-400f, 10f, 900f], band));
        // Just below the band (+ the probe) it has not arrived yet.
        Assert.Equal(0, ContextBandLayout.ActiveSection([-400f, band + ContextBandLayout.SpyProbe + 1f, 900f], band));
        // Exactly at the probe edge it HAS.
        Assert.Equal(1, ContextBandLayout.ActiveSection([-400f, band + ContextBandLayout.SpyProbe, 900f], band));
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
            int at = ContextBandLayout.ActiveSection(tops, band);
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
        Assert.Equal(1, ContextBandLayout.ActiveSection([-900f, -100f, float.NaN, float.NaN], ContextBandLayout.Height));
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
        Assert.Equal(-1, ContextBandLayout.ActiveSection([float.NaN, float.NaN], ContextBandLayout.Height));
        Assert.Equal(-1, ContextBandLayout.ActiveSection([float.NaN, -900f], ContextBandLayout.Height));
        // …but ONE realized section is evidence, and it answers normally.
        Assert.Equal(0, ContextBandLayout.ActiveSection([-900f, float.NaN], ContextBandLayout.Height));
    }

    [Fact]
    public void AnEmptyPivot_HasNoActiveSection()
        => Assert.Equal(-1, ContextBandLayout.ActiveSection(ReadOnlySpan<float>.Empty, ContextBandLayout.Height));

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
        Assert.Contains("ClipTopAtViewport = ContextBand.ClipInset", artist);
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
