using FluentGpu.Foundation;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Visualizer F6: the feedback spec's settle count (a stopped trail runs until it is below one 8-bit step), the
/// effective per-advance decay, and the composite item's feedback flag.</summary>
public sealed class FeedbackSpecTests
{
    [Theory]
    [InlineData(0.06f, 90)]      // ⌈ln(1/255)/ln(0.94)⌉ = 90
    [InlineData(0.5f, 8)]        // ⌈ln(1/255)/ln(0.5)⌉ = 8
    [InlineData(0.001f, 240)]    // capped
    public void Settle_turns_reach_one_8_bit_step(float decay, int expected)
        => Assert.Equal(expected, new FeedbackSpec(decay).SettleTurns);

    [Fact]
    public void No_decay_is_no_feedback()
    {
        Assert.True(new FeedbackSpec(0f).IsNone);
        Assert.Equal(0, new FeedbackSpec(0f).SettleTurns);
        Assert.False(default(CompositeItem).IsFeedback);
    }

    [Fact]
    public void A_bound_decay_overrides_the_spec_and_NaN_falls_back()
    {
        var spec = new FeedbackSpec(0.06f);
        Assert.Equal(0.06f, new FeedbackState(spec, Affine2D.Identity, float.NaN).EffectiveDecay);
        Assert.Equal(0.3f, new FeedbackState(spec, Affine2D.Identity, 0.3f).EffectiveDecay);
        Assert.Equal(1f, new FeedbackState(spec, Affine2D.Identity, 7f).EffectiveDecay);
    }
}
