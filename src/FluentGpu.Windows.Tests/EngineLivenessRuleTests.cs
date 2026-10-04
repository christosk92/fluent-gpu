using FluentGpu.Media.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>The pure decision behind the real engine's one-way live latch (<c>VideoMediaEngine</c> cannot run headless,
/// and <see cref="FakeVideoEngine"/> mirrors the published flag without latching it). GetDuration is NaN before metadata
/// and after a detach: that is "not known yet", and must never read as live.</summary>
public sealed class EngineLivenessRuleTests
{
    private const double NaN = double.NaN;

    [Theory]
    // Before metadata / no source: NaN duration is not live, whatever the other answers say.
    [InlineData(false, 0u, NaN, false, false)]
    [InlineData(true, 0u, NaN, false, false)]
    [InlineData(false, 1u, NaN, false, false)]
    [InlineData(false, 4u, double.PositiveInfinity, true, false)]    // the event bit has not landed: nothing is judged
    [InlineData(true, 0u, double.PositiveInfinity, true, false)]     // HAVE_NOTHING (late event from the previous source)
    // After metadata: only +Infinity or the IS_LIVE characteristic make it live.
    [InlineData(true, 1u, NaN, false, false)]                        // post-metadata NaN: policy is NOT live
    [InlineData(true, 1u, double.PositiveInfinity, false, true)]
    [InlineData(true, 4u, 120.0, false, false)]                      // an ordinary VOD
    [InlineData(true, 1u, 120.0, true, true)]                        // finite sliding DVR window flagged live by the source
    [InlineData(true, 4u, NaN, true, true)]
    [InlineData(true, 4u, double.NegativeInfinity, false, false)]
    public void IsLive_TruthTable(bool metadataLoaded, uint readyState, double duration, bool charIsLive, bool expected)
    {
        Assert.Equal(expected, EngineLivenessRule.IsLive(metadataLoaded, readyState, duration, charIsLive));
    }
}
