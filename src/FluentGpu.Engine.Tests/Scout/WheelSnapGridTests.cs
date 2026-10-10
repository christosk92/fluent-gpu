using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A wheel notch over a viewport with snap points (a <c>ShelfSnap.Page</c> shelf, a <c>SnapSpec.Every</c> list) travels
/// exactly what it travels on a snapless viewport: the engine snaps FLINGS only (the <c>SnapSpec</c> contract), and a
/// control that wants a wheel to rest on a boundary re-snaps after the settle. Re-targeting each notch onto the NEAREST
/// grid value pinned every notch shorter than half a stride to the boundary it started from: a zero-length plan that
/// settled at once, so the next notch could not accumulate either and a page shelf never moved, however fast it spun.
/// </summary>
public sealed class WheelSnapGridTests
{
    // A page stride well past twice the notch distance (Wavee's page shelves run ≈330–612 DIP against a 64 DIP notch).
    private const double PageStride = 330.0;

    private static ScrollHandle Shelf(int node, bool snap)
    {
        var h = new ScrollHandle(new PlanSlots(), new ScrollViewportId(node, 1), static () => 0.0, horizontal: true);
        h.SetExtent(10_000.0, 640.0);
        if (snap) h.SetSnap(new SnapGrid(PageStride, 0.0, 0.0, null));
        return h;
    }

    [Fact]
    public void Wheel_OneNotchOverAPageGrid_TravelsTheNotchFromRest()
    {
        var h = Shelf(7, snap: true);
        h.Wheel(0.0, 1.0);
        ScrollPlan p = h.Plan;
        Assert.Equal(MotionKind.Wheel, p.Kind);
        Assert.Equal(ScrollTunables.Current.WheelNotchDip, p.Dest, 9);
        Assert.True(p.S0.T1 > p.S0.T0);   // a real cubic, not a zero-length plan that settles at once
    }

    [Fact]
    public void Wheel_FastSpinOverAPageGrid_AccumulatesLikeASnaplessViewport()
    {
        var snapped = Shelf(7, snap: true);
        var free = Shelf(9, snap: false);
        for (int i = 0; i < 10; i++)
        {
            double t = i * 0.030;
            snapped.Wheel(t, 1.0);
            free.Wheel(t, 1.0);
            Assert.Equal(free.Plan.Dest, snapped.Plan.Dest, 9);
        }
        // Past half a stride: the shelf's post-settle nearest re-snap now lands on the NEXT page, not back on page 0.
        Assert.True(snapped.Plan.Dest > PageStride / 2.0, $"dest={snapped.Plan.Dest}");
    }

    [Fact]
    public void Wheel_HiResPacketsOverAPageGrid_TravelTheTurnedDistance()
    {
        var snapped = Shelf(7, snap: true);
        var free = Shelf(9, snap: false);
        for (int i = 0; i < 12; i++)
        {
            double t = i * 0.080 / 3.0;   // three fractional packets per notch, four notches turned
            snapped.Wheel(t, 1.0 / 3.0);
            free.Wheel(t, 1.0 / 3.0);
        }
        Assert.Equal(free.Plan.Dest, snapped.Plan.Dest, 9);
        Assert.True(snapped.Plan.Dest > PageStride / 2.0, $"dest={snapped.Plan.Dest}");
    }
}
