using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A virtual list laid out at a zero-size viewport (a pane collapsed or covered before it ever had a size: Wavee's sidebar list when
/// the root remounts under the full-screen stage, which the EP14 transaction gate found) realizes its mount-hint window and must
/// stay put. The trim rule used to ask for a re-realize every frame (the window is "wider" than an empty viewport), the realize put
/// the same hint window back, and the layout was dirty on every frame forever - so a theme transaction's settle could never end quiet.
/// </summary>
public sealed class ZeroViewportRealizeTests
{
    private static ScrollState Realized(int first, int last, float viewport) => new()
    {
        ItemCount = 200, FirstRealized = first, LastRealized = last, ViewportW = 240f, ViewportH = viewport,
    };

    [Fact]
    public void A_zero_viewport_never_asks_to_trim_its_hint_window()
    {
        var sc = Realized(0, 16, viewport: 0f);
        var rw = new RealizeWindow(First: 0, Last: 2, CoverStart: 0, CoverEnd: 60, AnchorIndex: 0, VisibleFirst: 0, VisibleLast: -1);
        Assert.False(ScrollContentPose.NeedsRealize(in sc, in rw));
    }

    [Fact]
    public void A_sized_viewport_still_trims_a_surplus_window()
    {
        var sc = Realized(0, 60, viewport: 120f);
        var rw = new RealizeWindow(First: 0, Last: 6, CoverStart: 0, CoverEnd: 200, AnchorIndex: 0, VisibleFirst: 0, VisibleLast: 4);
        Assert.True(ScrollContentPose.NeedsRealize(in sc, in rw));
    }

    [Fact]
    public void A_zero_viewport_with_nothing_realized_still_realizes()
    {
        var sc = Realized(0, 0, viewport: 0f);
        var rw = new RealizeWindow(First: 0, Last: 2, CoverStart: 0, CoverEnd: 60, AnchorIndex: 0);
        Assert.True(ScrollContentPose.NeedsRealize(in sc, in rw));
    }
}
