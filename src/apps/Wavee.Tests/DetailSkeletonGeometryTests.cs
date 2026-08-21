using System;
using System.IO;
using System.Runtime.CompilerServices;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// D49 — the detail page's LOADING geometry must be the loaded page's geometry.
///
/// <para>The defect: a detail page opened as a stack of shimmer ROWS at y=0. In the vertical / hero-system arm (narrow
/// windows, and every width once "Track page layout = Hero" is on) the hero and the list chrome are persistent PREFIX
/// ITEMS of the virtualized list, so they sit INSIDE the <c>Skel.Region</c> boundary and did not exist while the model
/// was Pending; when content landed, several hundred DIP of hero + toolbar + column header materialised above the rows
/// and shoved the whole list down the page.</para>
///
/// <para>The fix is arithmetic, not a second design: <c>DetailSkeleton.VerticalHeroBand</c> composes the same parts at
/// the same sizes from <see cref="DetailVerticalLayout"/>, and the band's HEIGHT is one pure function
/// (<see cref="DetailVerticalLayout.HeroBandHeight"/>) with two consumers — the skeleton and the loaded hero's own
/// pre-measure fallback. This file pins that arithmetic, and pins by SOURCE that both consumers really call it (a
/// re-introduced literal is exactly how the 420/320 constants drifted three compositions behind the hero).</para>
/// </summary>
public class DetailSkeletonGeometryTests
{
    const float LadderMin = 240f, LadderMax = 1400f;

    // The four hero emit predicates, as the two real pages present them.
    const bool Album = true;            // eyebrow "ALBUM · 2019", billed artists, meta line, no blurb
    const bool Playlist = true;         // owner row, meta line, description

    // ── the band's arithmetic ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band is the padded composition PLUS the toolbar row — never less than the artwork it has to hold.
    /// A band shorter than its own cover is the failure mode the 420/320 constants actually shipped: at 400 DIP the
    /// stacked artwork alone is 280 and the padding another 32.</summary>
    [Fact]
    public void HeroBand_AlwaysClearsTheArtworkPlusPaddingPlusToolbar()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
            foreach (bool rowFlow in new[] { false, true })
            {
                float band = DetailVerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true);
                float floor = DetailVerticalLayout.HeroPadFor(w)
                            + DetailVerticalLayout.ArtworkFor(w, rowFlow)
                            + DetailVerticalLayout.HeroBottomPad
                            + DetailVerticalLayout.ExpandedToolbarTopPad
                            + DetailVerticalLayout.ToolbarRowHeight
                            + DetailVerticalLayout.ExpandedToolbarBottomPad;
                Assert.True(band >= floor, $"band {band} < artwork floor {floor} at w={w} (rowFlow={rowFlow})");
            }
    }

    /// <summary>The band is the sum of the parts it declares — the same sum the skeleton's boxes lay out. Stacked adds
    /// artwork + gap + identity; row flow bottom-aligns them, so it is the taller of the two.</summary>
    [Theory]
    [InlineData(360f, false)]
    [InlineData(400f, false)]
    [InlineData(539f, false)]
    [InlineData(540f, true)]
    [InlineData(700f, true)]
    [InlineData(1200f, true)]
    public void HeroBand_IsExactlyTheCompositionItDeclares(float w, bool rowFlow)
    {
        float identity = DetailVerticalLayout.IdentityHeightFor(w, rowFlow, true, true, true, true);
        float art = DetailVerticalLayout.ArtworkFor(w, rowFlow);
        float hero = rowFlow ? MathF.Max(art, identity) : art + DetailVerticalLayout.HeroGapFor(w) + identity;
        float expected = DetailVerticalLayout.HeroPadFor(w) + hero + DetailVerticalLayout.HeroBottomPad
                       + DetailVerticalLayout.ExpandedToolbarTopPad
                       + DetailVerticalLayout.ToolbarRowHeight
                       + DetailVerticalLayout.ExpandedToolbarBottomPad;
        Assert.Equal(expected, DetailVerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true));
    }

    /// <summary>The identity column reserves the blocks the hero will actually emit — no more, no less. Title, accent
    /// rule and action row are unconditional; the optional four each cost exactly their row plus one inter-block gap.</summary>
    [Theory]
    [InlineData(700f, true)]
    [InlineData(400f, false)]
    public void IdentityHeight_ChargesOnlyForTheBlocksTheHeroEmits(float w, bool rowFlow)
    {
        float bare = DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, false, false, false);
        Assert.Equal(
            DetailVerticalLayout.TitleLineHeightFor(DetailVerticalLayout.TitleSizeFor(w)) * DetailVerticalLayout.TitleMaxLines
            + DetailVerticalLayout.AccentRuleRowHeight
            + DetailVerticalLayout.ActionRowHeight
            + 2f * DetailVerticalLayout.IdentityGap,
            bare);

        Assert.Equal(bare + DetailVerticalLayout.EyebrowRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, true, false, false, false));
        Assert.Equal(bare + DetailVerticalLayout.AttributionRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, true, false, false));
        Assert.Equal(bare + DetailVerticalLayout.MetaRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, false, true, false));
        Assert.Equal(
            bare + DetailVerticalLayout.DescriptionMaxLines(rowFlow) * DetailVerticalLayout.DescriptionLineHeight
                 + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, false, false, true));
        // The daylist flip-countdown row costs exactly its row plus one gap, like every other optional block.
        Assert.Equal(bare + DetailVerticalLayout.PulseRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, false, false, false, pulse: true));
    }

    /// <summary>An album (eyebrow + billed artists + meta, no blurb) and a playlist (owner + meta + description) both
    /// reserve a real band at every width the page can be — never a hairline, never something the 56-DIP context band
    /// could not collapse into.</summary>
    [Fact]
    public void HeroBand_IsCollapsibleAtEveryWidthForBothPageKinds()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            bool rowFlow = DetailVerticalLayout.RowFlow(w);
            float album = DetailVerticalLayout.HeroBandHeight(w, rowFlow, Album, true, true, false);
            float playlist = DetailVerticalLayout.HeroBandHeight(w, rowFlow, Playlist, true, true, true);
            foreach (float band in new[] { album, playlist })
            {
                Assert.True(band > DetailVerticalLayout.CompactIdentityHeight,
                    $"band {band} cannot collapse into the 56-DIP context band at w={w}");
                Assert.True(DetailVerticalLayout.CollapseDistance(band) > DetailVerticalLayout.CompactRevealBand,
                    $"collapse distance leaves no reveal window at w={w}");
            }
            // A description costs the page height; it can never make the hero SHORTER.
            Assert.True(playlist >= DetailVerticalLayout.HeroBandHeight(w, rowFlow, Playlist, true, true, false));
        }
    }

    /// <summary>Unmeasured width falls back to the same nominal column every other resolver in this file uses, so the
    /// first frame after a cold navigation reserves a plausible band rather than a degenerate one.</summary>
    [Fact]
    public void HeroBand_UnmeasuredUsesTheFallbackColumn()
    {
        bool rowFlow = DetailVerticalLayout.RowFlow(DetailVerticalLayout.FallbackW);
        Assert.Equal(
            DetailVerticalLayout.HeroBandHeight(DetailVerticalLayout.FallbackW, rowFlow, true, true, true, true),
            DetailVerticalLayout.HeroBandHeight(0f, rowFlow, true, true, true, true));
    }

    // ── the seams, pinned by source ──────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE function, TWO consumers. The skeleton's reserved band and the loaded hero's pre-measure fallback
    /// must both be <c>DetailVerticalLayout.HeroBandHeight</c> — that identity IS the parity claim, and it is the thing
    /// a well-meaning literal quietly breaks.</summary>
    [Fact]
    public void TheBandHeight_HasExactlyOneDefinitionAndBothConsumersCallIt()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        string tracks = Read(root, "Features", "Detail", "DetailTracks.cs");
        string skeleton = Read(root, "Features", "Detail", "DetailSkeleton.cs");

        Assert.Contains("DetailVerticalLayout.HeroBandHeight", tracks);
        Assert.Contains("DetailVerticalLayout.HeroBandHeight", skeleton);
        // …and the two hand-picked constants it replaced are gone from source, not merely unreferenced.
        Assert.DoesNotContain("VerticalHeaderFallbackHeight", tracks);
        Assert.DoesNotContain("RowFlowVerticalHeaderFallbackHeight", tracks);
    }

    /// <summary>The skeleton derives EVERY part of the hero from the shared resolver — artwork edge, copy measure,
    /// title rung + its paired line height, padding, gap and description cap — plus the app's own control geometry for
    /// the action row. A literal here is a hero that is one size in the shimmer and another when it lands.</summary>
    [Theory]
    [InlineData("DetailVerticalLayout.ArtworkFor")]
    [InlineData("DetailVerticalLayout.ContentWidthFor")]
    [InlineData("DetailVerticalLayout.TitleSizeFor")]
    [InlineData("DetailVerticalLayout.TitleLineHeightFor")]
    [InlineData("DetailVerticalLayout.HeroPadFor")]
    [InlineData("DetailVerticalLayout.HeroGapFor")]
    [InlineData("DetailVerticalLayout.DescriptionMaxLines")]
    [InlineData("DetailVerticalLayout.BucketW")]
    [InlineData("DetailVerticalLayout.ExpandedToolbarTopPad")]
    [InlineData("WaveeCta.PillHeight")]
    [InlineData("WaveeCta.IconButtonSize")]
    [InlineData("Surfaces.AccentRuleWidth")]
    public void TheSkeleton_ConsumesTheRealResolvers(string token)
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        Assert.Contains(token, Read(root, "Features", "Detail", "DetailSkeleton.cs"));
    }

    /// <summary>The row shimmer is the REAL row: the same <c>RowGrid</c> the list builds, at
    /// the active row style's density resolver, on the tier's own column set — so a loaded row replaces its placeholder
    /// in place. The reveal-ramp's placeholder is a BLANK grid of the same column tracks and row height (nothing
    /// painted — the crossing fades the real row in), so it needs no inset of its own.</summary>
    [Fact]
    public void TheRowShimmer_IsTheRealRowGeometry()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        string tracks = Read(root, "Features", "Detail", "DetailTracks.cs");

        Assert.Contains("rows[i] = RowGrid(EmptyTrack", tracks);          // the shimmer IS the real row builder
        Assert.Contains("float rowH = DetailTrackTableRules.RowHeightFor(density, set.Classic);", tracks); // …at the style's real density height
        Assert.Contains("TrackRow.ColGapFor(set.Tier)", tracks);
        Assert.Contains("Columns = tracks, RowHeight = rowH, Grow = 1f,", tracks);   // the ramp placeholder: same tracks, same height, blank
    }

    /// <summary>The vertical arm's shimmer leads with the hero band and the REAL chrome element, in the same order the
    /// loaded list carries them as items 0 and 1. Anything less and the page opens with rows at y=0.</summary>
    [Fact]
    public void TheVerticalShimmer_ReservesHeroThenChromeThenRows()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        string tracks = Read(root, "Features", "Detail", "DetailTracks.cs");

        Assert.Contains("DetailSkeleton.VerticalHeroBand(", tracks);
        Assert.Contains("VerticalShimmer(set, tracks, sort, labeled, tier, checkInset, contentFilterBar, rowH)", tracks);
        // …and the chrome it reserves is built by the SAME overload the vertical list's item 1 uses.
        Assert.Contains("Chrome(set, tracks, sort, labeled, tier, checkInset, contentFilterBar: contentFilterBar),", tracks);
        // The prefix count the shimmer mirrors is still two (hero, chrome).
        Assert.Equal(DetailVerticalItemRole.Hero, DetailVerticalLayout.ItemRole(0, 4));
        Assert.Equal(DetailVerticalItemRole.Chrome, DetailVerticalLayout.ItemRole(1, 4));
        Assert.Equal(DetailVerticalItemRole.ExpandableTrack, DetailVerticalLayout.ItemRole(2, 4));
    }

    /// <summary>The TWO-COLUMN arm gets its parity structurally and must keep it: the chrome (toolbar · chips · column
    /// header) is a SIBLING of the list body, and the rail is a sibling COLUMN of the whole track area — both outside
    /// the skeleton boundary, so both are reserved for free. Moving either inside would reproduce D49 on the wide
    /// layout, where it is worth hundreds of DIP.</summary>
    [Fact]
    public void TheTwoColumnArm_KeepsChromeAndRailOutsideTheSkeletonBoundary()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        Assert.Contains("Children = _verticalHeader ? [rightBody] : [chrome, rightBody],",
            Read(root, "Features", "Detail", "DetailTracks.cs"));

        string shell = Read(root, "Features", "Detail", "DetailShell.cs");
        // The rail's width ladder: mode 0 takes the config's own rail width, then 224, then 188.
        Assert.Contains("static float RailW(int mode, DetailConfig cfg) => mode switch { 0 => cfg.RailWidth, 1 => 224f, _ => 188f };", shell);
        // …and the rail is composed as `railFaded` beside `right`, never inside it.
        Assert.Contains("? [railFaded, DetailRailGrip(", shell);
        Assert.Contains(": [railFaded, right];", shell);
        Assert.Contains("Key = \"detail-rail-fade\"", shell);
        // The fade wrapper is a ROW child of [rail | right]. Height is the CROSS axis — AlignSelf=Stretch
        // takes the row's definite height; AlignItems=Stretch then hands that height to DetailRail.Build,
        // whose inner ScrollView grows into it. Direction=1 here makes height the MAIN axis, and Build's
        // root has no Grow, so the scroller collapses to 0 and ClipToBounds paints an empty column.
        Assert.Contains("Direction = 0, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch", shell);
        Assert.Contains("Children = [DetailRail.Build(", shell);
        // Cover/title sizes are CoverEdge(railW). Peek + a wrapper Width bind updates the column box but not
        // Build's frozen inner widths, so a drag looks like a no-op. Subscribe like LibraryPage's `_leftW.Value`.
        Assert.Contains("mode == 0 && resizableRail ? railWidthSignal.Value", shell);
        Assert.DoesNotContain("mode == 0 && resizableRail ? railWidthSignal.Peek()", shell);
    }

    static string Read(string root, params string[] parts) => File.ReadAllText(Path.Combine(root, Path.Combine(parts)));

    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "Features", "Detail", "DetailVerticalLayout.cs");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "Wavee");
            dir = dir.Parent;
        }
        return null!;
    }
}
