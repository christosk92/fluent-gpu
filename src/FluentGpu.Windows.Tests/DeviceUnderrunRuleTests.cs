using FluentGpu.Windows.Wasapi;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>The WASAPI device-underrun rule: a dry queue only counts on a running stream that has taken a full device buffer since its
/// last start or reset.</summary>
public sealed class DeviceUnderrunRuleTests
{
    private const uint Buffer = 4800;

    [Theory]
    [InlineData(0u, true, 0L, false)]        // first write after Start/Reset: the queue is legitimately empty
    [InlineData(0u, true, 4800L, false)]     // exactly one buffer written: still the prefill
    [InlineData(0u, true, 4801L, true)]      // past one buffer, queue dry: a real device underrun
    [InlineData(0u, false, 100000L, false)]  // stopped (pause drain, between Stop and Start): never counts
    [InlineData(1u, true, 100000L, false)]   // queue not empty
    [InlineData(4800u, true, 100000L, false)]
    public void IsUnderrun_Table(uint padding, bool started, long writtenSinceStart, bool expected)
        => Assert.Equal(expected, DeviceUnderrunRule.IsUnderrun(padding, started, writtenSinceStart, Buffer));

    [Fact]
    public void ResumeAfterPause_ReadsAsPrefillAgain_BecauseStartZeroesTheCounter()
    {
        // Steady play, then a pause/resume: Start zeroes the counter, so the first writes of the resume are not underruns.
        Assert.True(DeviceUnderrunRule.IsUnderrun(0, true, 200000, Buffer));
        Assert.False(DeviceUnderrunRule.IsUnderrun(0, true, 0, Buffer));
    }
}
