using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Wavee.Tests;

public class SidebarPaneInvariantTests
{
    static SidebarPaneFrameSnapshot Expanded(float preferred = 320f, float rendered = 320f) => new(
        SidebarDesign.Curated,
        UserCollapsed: false,
        PresentedCompact: false,
        PreferredExpandedWidth: preferred,
        RenderedPaneWidth: rendered,
        ExpandedOpacity: 1f,
        RailOpacity: 0f,
        ExpandedHitTestVisible: true,
        RailHitTestVisible: false);

    static SidebarPaneFrameSnapshot Compact(float rendered = ShellResponsiveLayout.CompactRailW) => new(
        SidebarDesign.LibraryV3,
        UserCollapsed: true,
        PresentedCompact: true,
        PreferredExpandedWidth: 340f,
        RenderedPaneWidth: rendered,
        ExpandedOpacity: 0f,
        RailOpacity: 1f,
        ExpandedHitTestVisible: false,
        RailHitTestVisible: true);

    [Fact]
    public void ExpandedTerminalState_IsValid()
    {
        var state = Expanded();
        Assert.Equal(SidebarPaneInvariantFault.None, SidebarPaneInvariant.Inspect(in state));
    }

    [Fact]
    public void CompactTerminalState_IsValid()
    {
        var state = Compact();
        Assert.True(SidebarPaneInvariant.IsValid(in state));
    }

    [Fact]
    public void ReportedTwentyFourDipSliver_IsRejected()
    {
        var state = Expanded(rendered: 24f);
        var fault = SidebarPaneInvariant.Inspect(in state);
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.ExpandedWidthOutOfRange));
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Theory]
    [InlineData(55.49f, false)]
    [InlineData(55.5f, true)]
    [InlineData(56.5f, true)]
    [InlineData(56.51f, false)]
    public void CompactWidth_UsesHalfDipTolerance(float rendered, bool valid)
    {
        var state = Compact(rendered);
        Assert.Equal(valid, SidebarPaneInvariant.IsValid(in state));
    }

    [Fact]
    public void WrongLayerOwnership_IsRejected()
    {
        var state = Expanded() with
        {
            ExpandedOpacity = 0f,
            RailOpacity = 1f,
            ExpandedHitTestVisible = false,
            RailHitTestVisible = true,
        };
        var fault = SidebarPaneInvariant.Inspect(in state);
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.LayerOpacityMismatch));
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.HitTestOwnerMismatch));
    }

    [Fact]
    public void ExpandedWidthMustMatchTheRememberedPreference()
    {
        var state = Expanded(preferred: 360f, rendered: 320f);
        Assert.True(SidebarPaneInvariant.Inspect(in state)
            .HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Fact]
    public void NonFiniteGeometry_IsRejectedWithoutFurtherClassification()
    {
        var state = Expanded() with { RenderedPaneWidth = float.NaN };
        Assert.Equal(SidebarPaneInvariantFault.NonFiniteValue, SidebarPaneInvariant.Inspect(in state));
    }

    /// <summary>THE PANE'S CONTEXT MENU MAY NOT HANG OFF THE PANE ROOT. <c>OnContextRequested</c> sets
    /// <c>InteractionInfo.ContextBit</c>, and ContextBit is in <c>InputDispatcher.Hit</c>'s hit-anywhere mask — an
    /// element with a context flyout is a hit-test target in its own right (the WinUI rule). A menu on the root
    /// therefore made the ROOT the press/hover owner for every dead spot in the sidebar (the rail's separator, the gap
    /// between tiles, the pane's padding), which points every engine cascade at a node whose subtree is the whole
    /// sidebar. The fix is the immersive stage's, verbatim (<c>StageIdentity.ContextScope</c>): a ZStack SHELL plus a
    /// CHILDLESS full-bleed SHIELD beneath the content — <c>Hit</c> takes the LAST matching child, so the content still
    /// wins wherever it hits, the shield takes everything else, and a cascade from a childless node reaches nothing.
    /// The shell keeps ContextBit as an ANCESTOR, which is all right-click-anywhere ever needed.
    /// <para>The shield staying CHILDLESS is the whole contract, so it is pinned as one literal.</para></summary>
    [Fact]
    public void ThePaneMenu_HangsOffAChildlessShieldAndNotTheRoot()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string pane = File.ReadAllText(Path.Combine(root, "Features", "Sidebar", "Pane", "SidebarPane.cs"));
        Assert.Contains("new BoxEl { Key = ContextShieldKey }.WithContextMenu(", pane);
        // Exactly two attach points — the shell and the shield — and nothing else in the pane owns a menu.
        Assert.Equal(2, Regex.Matches(pane, @"\.WithContextMenu\(").Count);
        // The shell is a ZStack: the shield has to sit UNDER the content, not beside it.
        Assert.Matches(new Regex(@"root = new BoxEl\s*\{\s*Grow = 1f, Direction = 1, ZStack = true"), pane);
    }

    /// <summary>src/apps/Wavee, located from THIS file's compile-time path (the StageLayoutTests idiom).</summary>
    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null!;
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        return Directory.Exists(app) ? app : null!;
    }
}
