using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scroll;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Visual-continuity audit S3 #18 (the sidebar rubber-banding ~50 px past its top edge on MOUSE-wheel input,
/// springing back over ~0.6 s) and S2 #7 (a list at offset 0 anchoring a row and throwing its header off the top).
/// Rubber-banding is a direct-manipulation affordance — touch and precision touchpad keep it — while a wheel clamps at
/// the extents (Flutter's pointerScroll clamps the wheel target even under BouncingScrollPhysics). A scroller at its
/// start edge does not anchor (Gecko ScrollAnchorContainer::ApplyAdjustments skips a zero scroll position).</summary>
public sealed class WheelOverscrollTests
{
    private sealed class NullSink : IScrollSink
    {
        public void Apply(int node, in ScrollWrite w) { }
    }

    private const int Node = 1;
    private static readonly ScrollClock Clock = new(0.0, 0.00833f, 0.0, 0.00833f);

    private static ScrollKernel BoundKernel(float startOffset)
    {
        var k = new ScrollKernel(new NullSink(), ScrollFeel.Shipping);
        k.Port.Post(ScrollInput.Bind(Node));
        k.Port.Post(ScrollInput.SetFrame(Node, new ScrollFrameSpec(0, 2000f, 300f, 400f, 300f, 1f, false, 0f, 0f, 0f, null)));
        k.Reclamp();
        if (startOffset != 0f)
        {
            k.Port.Post(ScrollInput.ScrollTo(Node, startOffset, immediate: true));
            k.Reclamp();
        }
        return k;
    }

    private static ScrollBody Body(ScrollKernel k)
    {
        Assert.True(k.TryGetBody(Node, out var b));
        return b;
    }

    [Fact]
    public void OnlyAMouseTaggedFallbackGestureIsAWheel()
    {
        Assert.True(ScrollInputRouter.IsWheelProducer((byte)ScrollDeviceClass.WheelHiResFallback, PointerKind.Mouse));
        Assert.False(ScrollInputRouter.IsWheelProducer((byte)ScrollDeviceClass.WheelHiResFallback, PointerKind.Touchpad));
        Assert.False(ScrollInputRouter.IsWheelProducer((byte)ScrollDeviceClass.Touchpad, PointerKind.Touchpad));
        Assert.False(ScrollInputRouter.IsWheelProducer((byte)ScrollDeviceClass.Touch, PointerKind.Touch));
        Assert.False(ScrollInputRouter.IsWheelProducer(0, PointerKind.Mouse));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DragPastTheTopEdge_BandsOnlyForDirectManipulation(bool wheel)
    {
        var k = BoundKernel(0f);
        for (int i = 0; i < 4; i++)
        {
            k.Port.Post(ScrollInput.FrameDelta(Node, i * 0.008, -30f, noOverscroll: wheel));
            k.Tick(in Clock);
        }
        var pinned = Body(k);
        Assert.Equal(0f, pinned.OffsetY);
        if (wheel) Assert.Equal(0f, pinned.BandY);
        else Assert.NotEqual(0f, pinned.BandY);

        // Reversal: the wheel scrolls back at once (its raw rests at the clamp); the touchpad first unwinds its stretch.
        k.Port.Post(ScrollInput.FrameDelta(Node, 0.032, 30f, noOverscroll: wheel));
        k.Tick(in Clock);
        var back = Body(k);
        if (wheel) Assert.Equal(30f, back.OffsetY, 3);
        else Assert.Equal(0f, back.OffsetY);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FlingIntoTheTopEdge_BouncesOnlyForDirectManipulation(bool wheel)
    {
        var k = BoundKernel(200f);
        double t = 0.0;
        float impulsePos = 0f;
        for (int i = 0; i < 6; i++)
        {
            // bug-B/A3: ScrollInputRouter.AccumulatePhaseDelta now posts a ScrollInput.ImpulseSample alongside every
            // FrameDelta it flushes (ScrollKernel.ApplyFrameDelta no longer feeds the release-velocity estimator
            // itself — see its doc). A test that posts raw FrameDelta commands directly, bypassing the router, must
            // replicate that pairing or the fling below releases at v=0 (Impulse never gets a 2nd sample).
            k.Port.Post(ScrollInput.FrameDelta(Node, t, -25f, noOverscroll: wheel));
            impulsePos += -25f;
            k.Port.Post(ScrollInput.ImpulseSample(Node, t, impulsePos, reset: i == 0));
            k.Tick(in Clock);
            t += 0.008;
        }
        k.Port.Post(ScrollInput.ContactEnd(Node, t - 0.008, 0f));
        bool bounced = false;
        float maxBand = 0f;
        for (int f = 0; f < 240; f++)
        {
            k.Tick(in Clock);
            k.Reclamp();
            var b = Body(k);
            if ((b.Flags & ScrollActivityFlags.Bouncing) != 0) bounced = true;
            maxBand = MathF.Max(maxBand, MathF.Abs(b.BandY));
        }
        var end = Body(k);
        Assert.Equal(0f, end.OffsetY);
        Assert.Equal(!wheel, bounced);
        if (wheel) Assert.Equal(0f, maxBand);
        else Assert.True(maxBand > 0f);
        Assert.True(end.IsSettled);
    }

    [Fact]
    public void AnchorCorrections_AreSuppressedAtTheStartEdge()
    {
        Assert.False(ScrollAnchoring.ShouldAdjust(0f, 120f));     // initial load / resting at the top: never anchor
        Assert.False(ScrollAnchoring.ShouldAdjust(0.2f, 120f));   // float noise at the edge is still the edge
        Assert.True(ScrollAnchoring.ShouldAdjust(200f, 120f));    // scrolled: keep the visible row where it is
        Assert.True(ScrollAnchoring.ShouldAdjust(200f, -40f));
        Assert.False(ScrollAnchoring.ShouldAdjust(200f, 0f));
        Assert.False(ScrollAnchoring.ShouldAdjust(200f, float.NaN));
    }
}
