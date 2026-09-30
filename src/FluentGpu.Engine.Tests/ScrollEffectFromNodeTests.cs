using FluentGpu.Scroll.Effects;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>EffectKind.MapFromNode: parallax/fade measured from the node's own position, clamped to its scope.</summary>
public class ScrollEffectFromNodeTests
{
    // A 52-tall header at y=1000 heading a section that ends at y=1400.
    static readonly EffectGeometry G = new(NodeY: 1000.0, NodeH: 52.0, ScopeEnd: 1400.0, Extent: 5000.0, Viewport: 800.0, Track: 0f, ThumbLen: 0f);

    [Theory]
    [InlineData(0.0, 0f)]      // header far below the top: no offset
    [InlineData(1000.0, 0f)]   // header's top meets the viewport top: still 0
    [InlineData(1036.0, 14.4f)] // half-way through the 72-DIP window at rate 0.4
    [InlineData(1072.0, 28.8f)] // window done: 72 * 0.4
    [InlineData(1300.0, 28.8f)] // held
    public void Parallax_is_measured_from_the_node(double offset, float expected)
        => Assert.Equal(expected, ScrollEffectEval.Evaluate(ScrollEffect.ParallaxFromNode(0.4f, 72f), offset, in G), 3);

    [Fact]
    public void Parallax_never_leaves_its_scope()
    {
        // Section ends 18 DIP below the header's bottom: the lag clamps at ScopeEnd − NodeH − NodeY = 1070 − 52 − 1000 = 18.
        var tight = G with { ScopeEnd = 1070.0 };
        Assert.Equal(18f, ScrollEffectEval.Evaluate(ScrollEffect.ParallaxFromNode(0.4f, 72f), 1072.0, in tight), 3);
    }

    [Theory]
    [InlineData(900.0, 1f)]
    [InlineData(1036.0, 0.5f)]
    [InlineData(1100.0, 0f)]
    public void Fade_is_measured_from_the_node(double offset, float expected)
        => Assert.Equal(expected, ScrollEffectEval.Evaluate(ScrollEffect.FadeFromNode(0.0, 72.0, 1f, 0f), offset, in G), 3);
}
