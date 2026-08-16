using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Wavee.Tests.Actions;

/// <summary>
/// The ROW AFFORDANCE gates — the two rules a track row's own controls must obey, both of which were broken in shipped
/// builds and both of which are invisible to a behavioural test from this project (the row is engine code:
/// FluentGpu.Controls elements, a virtualized list, an input dispatcher). So they are pinned by SOURCE SCAN, the same
/// technique <see cref="MenuGrammarTests"/> and <c>MotionSystemTests</c> use, and for the same reason: a scan cannot
/// prove a row renders, but it can prove the declaration that makes it correct is still there — which is exactly the
/// regression class (a new affordance added without it).
///
/// <para><b>Rule 1 — saved-ness is a FACT the row owes the reader.</b> Every detail-page profile emits the ♥ lane.
/// The hero/vertical profile used to hard-code <c>Heart: false</c>, and because that profile is FORCED at every width by
/// the "Hero" page-layout setting, an album or playlist page in it could not state whether a song was in the library at
/// any width at all (user report 2026-08-10: "on album/playlist pages it's not visible if a song is liked or not").</para>
///
/// <para><b>Rule 2 — a row's own affordance is never a handle for dragging the row.</b> Detail rows are
/// <c>Drag.Source</c>es, and the drag arm walks UP from the press target, so any button inside a row must set
/// <c>BlocksDragArm</c> or pressing it arms the row drag instead of firing the button. The heart, the expand chevron,
/// the trailing "…" (both the dedicated button and the one that shares the video lane) and the recommendation "+" were
/// all missing it.</para>
/// </summary>
public class RowAffordanceGrammarTests
{
    // ── Rule 1: the ♥ lane exists on every profile ───────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDetailProfile_EmitsTheHeartLane()
    {
        string setFor = Body(DetailTracks(), "ColumnSet SetFor(");

        // Both arms of SetFor (the vertical/hero profile and the standard one) must gate the heart on a TIER, never
        // hard-false it. `Heart: false` is the exact regression.
        Assert.DoesNotContain("Heart: false", setFor, StringComparison.Ordinal);
        Assert.Equal(2, Count(setFor, "Heart: tier <"));
    }

    [Fact]
    public void TheHeartLane_SurvivesToTheSameTierAsTheArtThumb()
    {
        string setFor = Body(DetailTracks(), "ColumnSet SetFor(");
        // The outline stays painted on unsaved rows (the like affordance), so the lane has to earn its width. It still
        // shares the art-thumb gate — dropping it earlier would hide saved-ness on the same narrow panes that keep art.
        Assert.Equal(2, Count(setFor, "Heart: tier < 5"));
        Assert.DoesNotContain("Heart: tier < 4", setFor, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeart_ShowsAtRestWhetherSavedOrNot()
    {
        string heart = Body(TrackRow(), "internal static Element Heart(");
        // Filled when saved, outline when not — both at rest. A hover-only unsaved heart left a dead gutter in the
        // left cluster (user report: "lots of dead space, especially if heart is not visible").
        Assert.DoesNotContain("Opacity = saved ? 1f : 0f", heart, StringComparison.Ordinal);
        Assert.DoesNotContain("HoverOpacity", heart, StringComparison.Ordinal);
        Assert.Contains("Icons.HeartFill", heart, StringComparison.Ordinal);
        Assert.Contains("Icons.Heart", heart, StringComparison.Ordinal);
    }

    // ── Rule 2: every row affordance blocks the row's drag arm ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("internal static Element Heart(")]
    [InlineData("internal static Element ExpandChevron(")]
    [InlineData("internal static Element MoreButton(")]
    [InlineData("internal static Element AddButton(")]
    [InlineData("internal static Element VideoMoreCell(")]
    public void EveryRowAffordance_BlocksTheRowsDragArm(string signature)
    {
        string body = Body(TrackRow(), signature);
        Assert.Contains("BlocksDragArm = true", body, StringComparison.Ordinal);
    }

    // ── Rule 3: a row only offers affordances its KIND can honour ───────────────────────────────

    /// <summary>The expand chevron opens the VERSIONS drawer (alternate versions + per-item audio format), and that
    /// content is track-only by decision. An EPISODE is a playable that rides the same row (<c>EpisodeAsTrack</c>,
    /// design §1.5), so an ungated chevron promised it a drawer that can only ever be empty — the lane is absent for
    /// it instead. Gated at the CELL, not in <c>SetFor</c>: the column set is per-TIER (both arms stay symmetric), the
    /// chevron is per-ROW.</summary>
    [Fact]
    public void TheExpandChevron_IsATrackAffordance()
    {
        string rowGrid = Body(DetailTracks(), "Element RowGrid(");
        Assert.Contains("EntityUri.KindOf(t.Uri) == EntityKind.Track", rowGrid, StringComparison.Ordinal);
    }

    /// <summary>The episode row's own grammar in the shared cell: the show in the subtitle (routed by the ref's KIND,
    /// so it opens the podcast page) and exactly ONE type token, in the lane the explicit badge owns.</summary>
    [Fact]
    public void AnEpisodeRow_StatesItsShowAndItsKind()
    {
        string meta = Body(TrackRow(), "static Element MetadataLine(");
        Assert.Contains("EntityUri.KindOf(t.Uri) == EntityKind.Episode", meta, StringComparison.Ordinal);
        Assert.Contains("(showAlbum || episode)", meta, StringComparison.Ordinal);          // the show is never dropped
        Assert.Contains("RichText.RouteForUri(album.Uri)", meta, StringComparison.Ordinal); // …and routes by kind
        Assert.Equal(1, Count(meta, "Strings.Detail.Badge.Episode"));                       // ONE token, not a lane

        // The Album LANE reads the same one route table, so the cell can never disagree with the subtitle.
        string album = Body(TrackRow(), "internal static Element AlbumLink(");
        Assert.Contains("RichText.RouteForUri(album.Uri)", album, StringComparison.Ordinal);
    }

    // ── source-scan machinery (the MenuGrammarTests shape) ───────────────────────────────────────────────────────────

    static string TrackRow() => File.ReadAllText(Path.Combine(AppRoot(), "Components", "TrackRow.cs"));
    static string DetailTracks() => File.ReadAllText(Path.Combine(AppRoot(), "Features", "Detail", "DetailTracks.cs"));

    /// <summary>The body of the member whose declaration contains <paramref name="signature"/>: from the declaration to
    /// the first line that closes a member at file scope — a 4-space <c>}</c> (a block member) or a 4-space <c>];</c>
    /// (an expression-bodied member ending in a collection expression). A signature that no longer exists fails loudly
    /// rather than matching nothing.</summary>
    static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"member not found (was it renamed?): {signature}");
        int block = source.IndexOf("\n    }", at, StringComparison.Ordinal);
        int expr = source.IndexOf("\n    ];", at, StringComparison.Ordinal);
        int end = block < 0 ? expr : expr < 0 ? block : Math.Min(block, expr);
        Assert.True(end > at, $"could not delimit the body of: {signature}");
        return source[at..end];
    }

    static int Count(string source, string needle)
    {
        int n = 0;
        for (int i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>The app's source root, resolved from THIS file's compile-time path — no build-output copying, no
    /// working-directory assumption.</summary>
    static string AppRoot([CallerFilePath] string here = "")
    {
        string actionsDir = Path.GetDirectoryName(here)!;                 // …/Wavee.Tests/Actions
        string tests = Path.GetDirectoryName(actionsDir)!;                // …/Wavee.Tests
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        Assert.True(Directory.Exists(app), $"app source root not found: {app}");
        return app;
    }
}
