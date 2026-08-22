using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Wavee.Tests;

// Part 5 of the playback-modules pass — the player bar's LIVE state. The bar is engine-bound (BoxEl/TextEl/Component),
// so it is not source-included here and cannot be instantiated; what CAN be pinned — and what actually regresses — is
// the set of decisions the surface encodes, so these are source gates against production text, the same discipline
// MenuGrammarTests and MotionSystemTests use.
//
// The decisions:
//   • the right-hand time slot states LIVE instead of counting down a duration that is 0 and stays 0,
//   • elapsed on the left is untouched (how long you have been listening is still a fact),
//   • the chip is the app's ONE badge shape (TrackRow.ClassicExplicitBadge's geometry), not a second badge language,
//   • it paints accent as CONTENT (WaveeAccent.Decor) and never accent as structure,
//   • the toggle-to-duration gesture goes with the label, because there is no duration to toggle to,
//   • the slot keeps its width, so a live playable starting does not reflow the seek row.
public class PlayerBarLiveTests
{
    static string PlayerBar() => File.ReadAllText(Path.Combine(AppSourceRoot()!, "Features", "Shell", "PlayerBar.cs"));

    static bool SourcesPresent => AppSourceRoot() is not null;

    /// <summary>The whole feature in one line: the right-hand slot consults the bridge's live signal and returns the
    /// pill instead of building the "-0:00" label. Reading <c>.Value</c> (not <c>.Peek()</c>) is the load-bearing half —
    /// the chip has to arrive and leave WITH the playable, not at the next unrelated re-render.</summary>
    [Fact]
    public void TheRightTimeSlot_RendersTheLivePill_OffTheBridgeSignal()
    {
        if (!SourcesPresent) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }
        string bar = PlayerBar();
        Assert.Contains("if (rightDuration && _b.IsLive.Value) return LivePill(_ink);", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("_b.IsLive.Peek()", bar, StringComparison.Ordinal);
    }

    /// <summary>Elapsed stays. The pill replaces the REMAINING label only — <c>rightDuration</c> is the guard, so the
    /// left-hand instance (<c>remaining: false</c>) never takes the branch and keeps counting.</summary>
    [Fact]
    public void ElapsedTime_IsUntouchedByLiveness()
    {
        if (!SourcesPresent) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }
        string bar = PlayerBar();
        Assert.Contains("new TimeText(b, remaining: false)", bar, StringComparison.Ordinal);
        // The guard is what confines the swap to the right slot; without it the elapsed label would vanish too.
        int at = bar.IndexOf("_b.IsLive.Value", StringComparison.Ordinal);
        Assert.True(at > 0, "the bar never reads the live signal");
        Assert.Contains("rightDuration && _b.IsLive.Value", bar, StringComparison.Ordinal);
    }

    /// <summary>ONE badge shape. The chip is the classic track table's content-rating word-mark geometry — a 14px box,
    /// a 2px corner (below the Radii control rung, so it reads as a MARK and not a lozenge), a 1px stroke and 9/12
    /// semibold type. A second badge language for the same job is exactly the drift the voice wave removed.</summary>
    [Fact]
    public void TheLivePill_IsTheAppsOneBadgeGeometry()
    {
        if (!SourcesPresent) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }
        string pill = LivePillBody();
        Assert.Contains("Height = 14f", pill, StringComparison.Ordinal);
        Assert.Contains("CornerRadius4.All(2f)", pill, StringComparison.Ordinal);
        Assert.Contains("BorderWidth = 1f", pill, StringComparison.Ordinal);
        Assert.Contains("Size = 9f", pill, StringComparison.Ordinal);
        Assert.Contains("LineHeight = 12f", pill, StringComparison.Ordinal);
        Assert.Contains("Weight = 600", pill, StringComparison.Ordinal);
    }

    /// <summary>Accent as CONTENT, never as structure: the pill's ink and stroke are the named decorative-accent role,
    /// not <c>Tok.AccentDefault</c> reached for directly (which would mean any of the three accent roles).</summary>
    [Fact]
    public void TheLivePill_PaintsTheNamedAccentInkRole()
    {
        if (!SourcesPresent) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }
        string pill = LivePillBody();
        Assert.Contains("WaveeAccent.Decor", pill, StringComparison.Ordinal);
        Assert.DoesNotContain("Tok.AccentDefault", pill, StringComparison.Ordinal);
        // The immersive stage passes its own theme-invariant on-media ink; the pill must honour it exactly as the time
        // labels do, or the chip would paint theme accent over a dark veil.
        Assert.Contains("ink ?? WaveeAccent.Decor", pill, StringComparison.Ordinal);
    }

    /// <summary>The word is localized, not a literal — "LIVE" is copy, and copy lives in the catalog.</summary>
    [Fact]
    public void TheLivePill_TakesItsWordFromTheCatalog()
    {
        if (!SourcesPresent) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }
        string pill = LivePillBody();
        Assert.Contains("Loc.Get(Strings.Play.Live)", pill, StringComparison.Ordinal);
        Assert.DoesNotContain("new TextEl(\"LIVE\")", pill, StringComparison.Ordinal);
    }

    /// <summary>The slot keeps the time label's 44 DIPs, so a live playable starting does not reflow the seek row
    /// around it (the seek bar between the two labels is compositor-bound; a width change there is a visible jump).
    /// </summary>
    [Fact]
    public void TheLivePill_KeepsTheTimeSlotWidth()
    {
        if (!SourcesPresent) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }
        Assert.Contains("Width = 44f", LivePillBody(), StringComparison.Ordinal);
    }

    /// <summary>No toggle. The right-hand label's click swaps remaining ↔ total duration; a live stream has neither, so
    /// the pill carries no <c>OnClick</c> and no hand cursor rather than offering a gesture that does nothing.</summary>
    [Fact]
    public void TheLivePill_OffersNoToggleGesture()
    {
        if (!SourcesPresent) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }
        string pill = LivePillBody();
        Assert.DoesNotContain("OnClick", pill, StringComparison.Ordinal);
        Assert.DoesNotContain("CursorId.Hand", pill, StringComparison.Ordinal);
    }

    /// <summary>The <c>LivePill</c> body: from its declaration to the end of the <c>TimeText</c> class. Sliced rather
    /// than read whole so the "does not contain" gates above cannot be satisfied — or broken — by unrelated text
    /// elsewhere in a 1200-line file.</summary>
    static string LivePillBody()
    {
        string bar = PlayerBar();
        int start = bar.IndexOf("static Element LivePill(", StringComparison.Ordinal);
        Assert.True(start > 0, "PlayerBar.cs no longer declares LivePill");
        int end = bar.IndexOf("/// <summary>The volume / mute glyph", start, StringComparison.Ordinal);
        Assert.True(end > start, "the TimeText class no longer ends where this slice expects");
        return bar[start..end];
    }

    /// <summary>src/apps/Wavee, located from THIS file's compile-time path — the test sources and the app sources are
    /// siblings in the repo. Null when the sources are not on disk (a binary-only run).</summary>
    static string? AppSourceRoot([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null;
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        return Directory.Exists(app) ? app : null;
    }
}
