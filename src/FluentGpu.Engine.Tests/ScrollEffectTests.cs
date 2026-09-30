using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Effects;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Scroll rework Wave 0 part C: pure-arithmetic coverage for <see cref="ScrollEffectEval"/> — sticky
/// pin/clip release geometry, eased range maps, thumb-position bounds, and the bitwise determinism the design doc
/// requires (the UI thread and the render thread must never disagree on the same effect at the same offset).</summary>
public sealed class ScrollEffectTests
{
    // ── Sticky ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sticky_PinsAtInset_ScreenPositionStaysConstantOnceEngaged()
    {
        // Node at content y=200; screen position (from viewport top) is (NodeY - offset + shift). Once engaged that
        // must sit EXACTLY at the inset, and stay there across further scrolling, until release near the scope end.
        var e = ScrollEffect.Sticky(inset: 56f);
        var g = new EffectGeometry(NodeY: 200.0, NodeH: 40.0, ScopeEnd: 5000.0, Extent: 5000.0, Viewport: 800.0, Track: 0f, ThumbLen: 0f);
        foreach (double offset in new[] { 145.0, 200.0, 600.0, 4000.0 })
        {
            float shift = ScrollEffectEval.Evaluate(e, offset, g);
            double screenPos = g.NodeY - offset + shift;
            Assert.Equal(56.0, screenPos, 3);
        }
    }

    [Fact]
    public void Sticky_ClampsToZero_BeforeTheNodeReachesTheStickyLine()
    {
        var e = ScrollEffect.Sticky(inset: 0f);
        var g = new EffectGeometry(NodeY: 500.0, NodeH: 40.0, ScopeEnd: 5000.0, Extent: 5000.0, Viewport: 800.0, Track: 0f, ThumbLen: 0f);
        // offset far below the node's own position -> shift would be negative -> clamps to 0 (not yet pinned).
        float shift = ScrollEffectEval.Evaluate(e, offset: 10.0, g);
        Assert.Equal(0f, shift);
        Assert.False(ScrollEffectEval.StickyEngaged(e, offset: 10.0, g));
    }

    [Fact]
    public void Sticky_EngagesAndTracksOffset_OncePastTheLine()
    {
        var e = ScrollEffect.Sticky(inset: 0f);
        var g = new EffectGeometry(NodeY: 100.0, NodeH: 40.0, ScopeEnd: 5000.0, Extent: 5000.0, Viewport: 800.0, Track: 0f, ThumbLen: 0f);
        float shift = ScrollEffectEval.Evaluate(e, offset: 150.0, g);
        Assert.Equal(50f, shift);
        Assert.True(ScrollEffectEval.StickyEngaged(e, offset: 150.0, g));
    }

    [Fact]
    public void Sticky_ReleasesAtScopeEnd_AndStopsAdvancing()
    {
        // ScopeEnd - NodeH - NodeY = 300 - 40 - 100 = 160 is the release limit.
        var e = ScrollEffect.Sticky(inset: 0f);
        var g = new EffectGeometry(NodeY: 100.0, NodeH: 40.0, ScopeEnd: 300.0, Extent: 5000.0, Viewport: 800.0, Track: 0f, ThumbLen: 0f);
        float atLimit = ScrollEffectEval.Evaluate(e, offset: 260.0, g);   // shift would be exactly 160
        float pastLimit = ScrollEffectEval.Evaluate(e, offset: 5000.0, g); // shift would be huge without the clamp
        Assert.Equal(160f, atLimit);
        Assert.Equal(160f, pastLimit);
    }

    [Fact]
    public void Sticky_LimitNeverGoesNegative_WhenScopeIsSmallerThanTheNode()
    {
        var e = ScrollEffect.Sticky(inset: 0f);
        // ScopeEnd - NodeH - NodeY = 50 - 40 - 100 = -90 -> limit clamps to 0, so shift can never exceed 0 either.
        var g = new EffectGeometry(NodeY: 100.0, NodeH: 40.0, ScopeEnd: 50.0, Extent: 5000.0, Viewport: 800.0, Track: 0f, ThumbLen: 0f);
        float shift = ScrollEffectEval.Evaluate(e, offset: 1000.0, g);
        Assert.Equal(0f, shift);
    }

    // ── StickyClip ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StickyClip_IsZero_BeforeTheLineReachesTheNode()
    {
        var e = ScrollEffect.StickyClip(inset: 56f);
        var g = new EffectGeometry(NodeY: 500.0, NodeH: 40.0, ScopeEnd: 5000.0, Extent: 5000.0, Viewport: 800.0, Track: 0f, ThumbLen: 0f);
        Assert.Equal(0f, ScrollEffectEval.Evaluate(e, offset: 10.0, g));
        Assert.False(ScrollEffectEval.StickyEngaged(e, offset: 10.0, g));
    }

    [Fact]
    public void StickyClip_FollowsTheLine_ThenSaturatesAtTheNodeHeight_IgnoringTheScope()
    {
        // StickyClip has no scope limit (unlike Sticky): the clip line keeps advancing with the offset until the whole
        // node is hidden, and then holds at the node's own height — a fully clipped node is pixel-identical however far
        // past it the line is, so the value (and its dirty) freezes there instead of rewriting every frame.
        var e = ScrollEffect.StickyClip(inset: 0f);
        var g = new EffectGeometry(NodeY: 100.0, NodeH: 40.0, ScopeEnd: 110.0, Extent: 5000.0, Viewport: 800.0, Track: 0f, ThumbLen: 0f);
        Assert.Equal(25f, ScrollEffectEval.Evaluate(e, offset: 125.0, g));       // past ScopeEnd − NodeH − NodeY: no scope clamp
        Assert.Equal(40f, ScrollEffectEval.Evaluate(e, offset: 140.0, g));       // fully hidden
        Assert.Equal(40f, ScrollEffectEval.Evaluate(e, offset: 5000.0, g));      // and frozen there
        Assert.True(ScrollEffectEval.StickyEngaged(e, offset: 5000.0, g));
    }

    // ── Map ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Map_Linear_InterpolatesAndClampsOutsideRange()
    {
        var e = ScrollEffect.Fade(in0: 0.0, in1: 100.0, from: 1f, to: 0f);
        var g = default(EffectGeometry);
        Assert.Equal(1f, ScrollEffectEval.Evaluate(e, -50.0, g));
        Assert.Equal(0.5f, ScrollEffectEval.Evaluate(e, 50.0, g));
        Assert.Equal(0f, ScrollEffectEval.Evaluate(e, 100.0, g));
        Assert.Equal(0f, ScrollEffectEval.Evaluate(e, 500.0, g));
    }

    [Fact]
    public void Map_Eased_MatchesTheNamedCurveAtMidpoint()
    {
        var e = ScrollEffect.Scale(0.0, 100.0, 0f, 1f, Easing.EaseIn);
        var g = default(EffectGeometry);
        float actual = ScrollEffectEval.Evaluate(e, 50.0, g);
        float expected = Easings.Ease(Easing.EaseIn, 0.5f); // EaseIn: t*t -> 0.25
        Assert.Equal(expected, actual);
        Assert.Equal(0.25f, actual, 5);
    }

    [Fact]
    public void Map_DegenerateRange_IsInactiveAtOut0()
    {
        var e = ScrollEffect.Parallax(10.0, 10.0, 0f, 200f);
        var g = default(EffectGeometry);
        Assert.Equal(0f, ScrollEffectEval.Evaluate(e, 10.0, g));
        Assert.Equal(0f, ScrollEffectEval.Evaluate(e, 999.0, g));
    }

    // ── Thumb ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Thumb_TracksProportionally_WithinTrackMinusThumbLen()
    {
        var e = ScrollEffect.Thumb();
        var g = new EffectGeometry(NodeY: 0, NodeH: 0, ScopeEnd: 0, Extent: 1000.0, Viewport: 200.0, Track: 400f, ThumbLen: 40f);
        // range = 800, usable track = 360
        Assert.Equal(0f, ScrollEffectEval.Evaluate(e, 0.0, g));
        Assert.Equal(180f, ScrollEffectEval.Evaluate(e, 400.0, g));
        Assert.Equal(360f, ScrollEffectEval.Evaluate(e, 800.0, g));
    }

    [Fact]
    public void Thumb_ClampsPastTheExtent()
    {
        var e = ScrollEffect.Thumb();
        var g = new EffectGeometry(NodeY: 0, NodeH: 0, ScopeEnd: 0, Extent: 1000.0, Viewport: 200.0, Track: 400f, ThumbLen: 40f);
        Assert.Equal(360f, ScrollEffectEval.Evaluate(e, 5000.0, g));
        Assert.Equal(0f, ScrollEffectEval.Evaluate(e, -500.0, g));
    }

    [Fact]
    public void Thumb_IsZero_WhenExtentDoesNotExceedViewport()
    {
        var e = ScrollEffect.Thumb();
        var g = new EffectGeometry(NodeY: 0, NodeH: 0, ScopeEnd: 0, Extent: 200.0, Viewport: 200.0, Track: 400f, ThumbLen: 40f);
        Assert.Equal(0f, ScrollEffectEval.Evaluate(e, 0.0, g));
        var g2 = new EffectGeometry(NodeY: 0, NodeH: 0, ScopeEnd: 0, Extent: 100.0, Viewport: 200.0, Track: 400f, ThumbLen: 40f);
        Assert.Equal(0f, ScrollEffectEval.Evaluate(e, 0.0, g2));
    }

    // ── Determinism ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_IsBitIdentical_AcrossRepeatedCalls()
    {
        var e = ScrollEffect.Scale(0.0, 733.0, 0.8f, 1.25f, Easing.EaseInOut);
        var g = new EffectGeometry(NodeY: 12.5, NodeH: 300.25, ScopeEnd: 4000.75, Extent: 12345.5, Viewport: 812.0, Track: 300f, ThumbLen: 33f);
        float first = ScrollEffectEval.Evaluate(e, 271.375, g);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(first, ScrollEffectEval.Evaluate(e, 271.375, g));
    }

    [Fact]
    public async Task Evaluate_IsBitIdentical_AcrossThreads()
    {
        var effects = new[]
        {
            ScrollEffect.Sticky(56f),
            ScrollEffect.StickyClip(0f),
            ScrollEffect.Parallax(0.0, 500.0, 0f, -100f),
            ScrollEffect.Fade(0.0, 200.0, 1f, 0f),
            ScrollEffect.Scale(0.0, 733.0, 0.8f, 1.25f, Easing.Overshoot),
            ScrollEffect.Thumb(),
        };
        var g = new EffectGeometry(NodeY: 40.0, NodeH: 80.0, ScopeEnd: 3000.0, Extent: 9000.0, Viewport: 700.0, Track: 320f, ThumbLen: 48f);

        float[] baseline = new float[effects.Length];
        for (int i = 0; i < effects.Length; i++) baseline[i] = ScrollEffectEval.Evaluate(effects[i], 314.159, g);

        var tasks = new Task[8];
        var mismatches = new bool[8];
        for (int w = 0; w < tasks.Length; w++)
        {
            int worker = w;
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < effects.Length; i++)
                {
                    float v = ScrollEffectEval.Evaluate(effects[i], 314.159, g);
                    if (v != baseline[i]) mismatches[worker] = true;
                }
            }, TestContext.Current.CancellationToken);
        }
        await Task.WhenAll(tasks);
        Assert.DoesNotContain(true, mismatches);
    }

    // ── Snap ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void SnapToDevicePixel_IsIdentity_ForNonFiniteOrNonPositiveScale(float badScale)
    {
        Assert.Equal(12.34f, ScrollEffectEval.SnapToDevicePixel(12.34f, badScale));
    }

    [Fact]
    public void SnapToDevicePixel_RoundsToTheDeviceGrid()
    {
        // scale 1.5 -> device grid is 2/3 DIP; 10.2 DIP * 1.5 = 15.3 device px -> rounds to 15 -> /1.5 = 10.0
        float snapped = ScrollEffectEval.SnapToDevicePixel(10.2f, 1.5f);
        Assert.Equal(10.0f, snapped, 3);
    }
    [Fact]
    public void PaintOrder_OnlyAStickyPinLiftsItsNodeAboveItsSiblings_AStickyClipNeverDoes()
    {
        Assert.True(ScrollEffectEval.PinsAboveSiblings(EffectKind.Sticky));
        Assert.False(ScrollEffectEval.PinsAboveSiblings(EffectKind.StickyClip));
        Assert.False(ScrollEffectEval.PinsAboveSiblings(EffectKind.Map));
        Assert.False(ScrollEffectEval.PinsAboveSiblings(EffectKind.Collapse));
        // The engaged EDGE is still reported for both kinds (the signal is a UI decision, independent of paint order).
        var clip = ScrollEffect.StickyClip(56f);
        var geo = new EffectGeometry(0, 300, 300, 1000, 300, 0, 0);
        Assert.True(ScrollEffectEval.StickyEngaged(in clip, 0.0, in geo));
    }
}
