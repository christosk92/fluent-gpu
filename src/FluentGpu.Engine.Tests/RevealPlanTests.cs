using System;
using FluentGpu.Animation;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The pure half of SizeMode.FlowReveal (docs/plans/smooth-reveal-implementation.md §1/§3): the visible-span
/// clamp, the Parallax lead, and the one reveal spring's shape (first frame, step bound, no overshoot, settle time).</summary>
public sealed class RevealPlanTests
{
    [Fact]
    public void Clamp_AFullyVisibleMoveIsUntouched()
    {
        Assert.True(RevealPlan.TryClamp(0f, 200f, 100f, 0f, 800f, out float from, out float to));
        Assert.Equal(0f, from);
        Assert.Equal(200f, to);
    }

    [Fact]
    public void Clamp_ATallExpandAnimatesOnlyTheVisibleSpan()
    {
        Assert.True(RevealPlan.TryClamp(0f, 1300f, 40f, 0f, 300f, out float from, out float to));
        Assert.Equal(0f, from);
        Assert.Equal(268f, to, 3);   // 300 + 8 slack − 40
    }

    [Fact]
    public void Clamp_ATallCollapseJumpsTheHiddenPartFirst()
    {
        Assert.True(RevealPlan.TryClamp(1300f, 0f, 40f, 0f, 300f, out float from, out float to));
        Assert.Equal(268f, from, 3);
        Assert.Equal(0f, to);
    }

    [Fact]
    public void Clamp_GrowthKeepsItsDeparture()
    {
        Assert.True(RevealPlan.TryClamp(120f, 240f, 0f, 0f, 800f, out float from, out float to));
        Assert.Equal(120f, from);
        Assert.Equal(240f, to);
    }

    [Fact]
    public void AboveView_OnlyWhenTheRegionStaysAboveTheTopThroughTheWholeMove()
    {
        Assert.True(RevealPlan.IsAboveView(200f, 0f, -900f, 0f));     // a collapse scrolled out of view: snap + anchor
        Assert.True(RevealPlan.IsAboveView(0f, 200f, -200f, 0f));     // its bottom lands exactly on the top edge
        Assert.False(RevealPlan.IsAboveView(0f, 200f, -150f, 0f));    // 50 DIP of it reaches into the view: animate
        Assert.False(RevealPlan.IsAboveView(0f, 200f, 400f, 0f));     // below the top
    }

    [Theory]
    [InlineData(-500f)]   // wholly above the view: animating it would slide what the user reads
    [InlineData(400f)]    // wholly below it: nothing to see
    public void Clamp_AnInvisibleRegionSnaps(float regionTop)
    {
        Assert.False(RevealPlan.TryClamp(0f, 200f, regionTop, 0f, 300f, out float from, out float to));
        Assert.Equal(200f, from);
        Assert.Equal(200f, to);
    }

    [Fact]
    public void ParallaxShift_LeadsByADampedShareCappedAt24()
    {
        Assert.Equal(0f, RevealPlan.ParallaxShift(100f, 100f));
        Assert.Equal(-3.5f, RevealPlan.ParallaxShift(90f, 100f), 3);
        Assert.Equal(-RevealPlan.ParallaxMaxDip, RevealPlan.ParallaxShift(0f, 400f));
    }

    [Theory]
    [InlineData(8.33f, 0.02f, 0.08f)]
    [InlineData(16.67f, 0.05f, 0.15f)]
    public void Spring_FirstFrameAndPerFrameStepAreBounded(float dtMs, float firstMax, float stepMax)
    {
        float first = RevealPlan.Progress(dtMs);
        float prev = 0f, maxStep = 0f;
        for (float t = dtMs; t < 700f; t += dtMs)
        {
            float p = RevealPlan.Progress(t);
            maxStep = MathF.Max(maxStep, p - prev);
            prev = p;
        }
        Assert.InRange(first, 0f, firstMax);
        Assert.InRange(maxStep, 0f, stepMax);
    }

    [Fact]
    public void Spring_IsCriticallyDampedAndSettlesOnTime()
    {
        float peak = 0f;
        for (float t = 0f; t < 900f; t += 1f) peak = MathF.Max(peak, RevealPlan.Progress(t));
        Assert.True(peak <= 1.0005f, $"overshoot peak={peak}");
        Assert.InRange(RevealPlan.Progress(240f), 0.95f, 1f);
        Assert.InRange(RevealPlan.Progress(340f), 0.99f, 1f);
    }
}
