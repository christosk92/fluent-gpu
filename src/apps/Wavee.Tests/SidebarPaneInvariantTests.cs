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
}
