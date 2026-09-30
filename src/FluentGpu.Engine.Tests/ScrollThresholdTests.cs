using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The pure hysteresis rule behind <c>RenderContext.UseScrollThreshold</c> (Wavee Home's compact facet band —
/// docs/plans/wavee/home-redesign-implementation.md §E6): shown while offset stays at or above <c>exitAt</c>, shows
/// only once offset moves strictly past <c>enterAt</c>.</summary>
public sealed class ScrollThresholdTests
{
    // Enter/exit match the compact-band spec: shown > 64, hidden < 56.
    const double Enter = 64.0, Exit = 56.0;

    [Fact]
    public void HiddenStaysHiddenBelowEnter()
        => Assert.False(ScrollThreshold.Next(prev: false, offset: 63.999, Enter, Exit));

    [Fact]
    public void HiddenStaysHiddenExactlyAtEnter()
        // Strict '>' — exactly AT the enter threshold does NOT flip.
        => Assert.False(ScrollThreshold.Next(prev: false, offset: Enter, Enter, Exit));

    [Fact]
    public void HiddenFlipsToShownJustPastEnter()
        => Assert.True(ScrollThreshold.Next(prev: false, offset: 64.001, Enter, Exit));

    [Fact]
    public void ShownStaysShownAtExitBoundary()
        // Inclusive '>=' — exactly AT the exit threshold stays shown.
        => Assert.True(ScrollThreshold.Next(prev: true, offset: Exit, Enter, Exit));

    [Fact]
    public void ShownStaysShownAboveExit()
        => Assert.True(ScrollThreshold.Next(prev: true, offset: 60.0, Enter, Exit));

    [Fact]
    public void ShownFlipsToHiddenJustBelowExit()
        => Assert.False(ScrollThreshold.Next(prev: true, offset: 55.999, Enter, Exit));

    [Fact]
    public void ShownStaysShownEvenBelowEnter_AsLongAsAboveExit()
        // The dead band: once shown, offset can fall all the way to just above Exit without hiding —
        // even though it is well below Enter. This is the whole point of hysteresis.
        => Assert.True(ScrollThreshold.Next(prev: true, offset: 58.0, Enter, Exit));

    [Fact]
    public void HiddenStaysHiddenEvenInsideTheExitBand_UntilEnterIsCrossed()
        // Symmetric case: hidden at an offset that is >= Exit but <= Enter never auto-shows;
        // only crossing Enter (strictly) flips it.
        => Assert.False(ScrollThreshold.Next(prev: false, offset: 60.0, Enter, Exit));

    [Fact]
    public void OscillatingInsideTheDeadBandNeverFlips()
    {
        // Once shown, wobbling between Exit and Enter (inclusive/exclusive per the rule) holds shown.
        bool state = true;
        double[] wobble = { 60.0, 57.0, 63.0, 56.0, 64.0, 58.0 };
        foreach (var offset in wobble)
        {
            state = ScrollThreshold.Next(state, offset, Enter, Exit);
            Assert.True(state);
        }
    }

    [Fact]
    public void FullRoundTrip_EnterThenExit()
    {
        bool state = false;
        state = ScrollThreshold.Next(state, 0.0, Enter, Exit);
        Assert.False(state);
        state = ScrollThreshold.Next(state, 100.0, Enter, Exit);
        Assert.True(state);
        state = ScrollThreshold.Next(state, 56.0, Enter, Exit);   // still shown (inclusive exit boundary)
        Assert.True(state);
        state = ScrollThreshold.Next(state, 55.9, Enter, Exit);   // now hides
        Assert.False(state);
        state = ScrollThreshold.Next(state, 64.0, Enter, Exit);   // exactly at enter: still hidden (strict)
        Assert.False(state);
        state = ScrollThreshold.Next(state, 64.1, Enter, Exit);   // past enter: shows again
        Assert.True(state);
    }

    [Fact]
    public void EqualThresholds_DegradeToASingleNonHystereticFlip()
    {
        Assert.False(ScrollThreshold.Next(false, 60.0, 60.0, 60.0));
        Assert.True(ScrollThreshold.Next(false, 60.01, 60.0, 60.0));
        Assert.True(ScrollThreshold.Next(true, 60.0, 60.0, 60.0));
        Assert.False(ScrollThreshold.Next(true, 59.99, 60.0, 60.0));
    }
}
