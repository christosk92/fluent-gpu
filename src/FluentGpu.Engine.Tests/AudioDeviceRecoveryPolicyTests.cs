using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The pure timing decision behind <see cref="AudioDeviceController"/> (Wavee #112). No threads, no clock: every call
/// passes its own <c>nowMs</c>. The first test is the livelock regression — in the shipped 0.2.8 controller a dead sink's
/// report every ~80 ms re-stamped the 250 ms trailing debounce forever, so the rebuild never ran.
/// </summary>
public sealed class AudioDeviceRecoveryPolicyTests
{
    [Fact]
    public void SinkFailureBurst_NeverPostponesADueRebuild()
    {
        var p = new AudioDeviceRecoveryPolicy();
        Assert.True(p.NoteSinkFailure(0, retryScheduled: false));    // the first report STARTS a request
        Assert.True(p.HasPending);

        // A dead sink keeps reporting every 50 ms for 2 s. None of it may push the due time past first + DebounceMs.
        for (long t = 50; t <= 2000; t += 50)
        {
            Assert.False(p.NoteSinkFailure(t, retryScheduled: false));
            int expected = (int)System.Math.Max(0, AudioDeviceRecoveryPolicy.DebounceMs - t);
            Assert.Equal(expected, p.DueInMs(t));
        }
        Assert.Equal(0, p.DueInMs(AudioDeviceRecoveryPolicy.DebounceMs));
    }

    [Fact]
    public void DeviceEventBurst_IsCappedAtOneSecond()
    {
        var p = new AudioDeviceRecoveryPolicy();
        // A flapping watcher raises every 100 ms for 2 s: the trailing window alone would defer forever.
        for (long t = 0; t <= 2000; t += 100)
        {
            p.NoteDeviceEvent(t);
            int trailing = AudioDeviceRecoveryPolicy.DebounceMs;
            int cap = (int)System.Math.Max(0, AudioDeviceRecoveryPolicy.MaxDebounceDeferralMs - t);
            Assert.Equal(System.Math.Min(trailing, cap), p.DueInMs(t));
        }
        Assert.Equal(0, p.DueInMs(AudioDeviceRecoveryPolicy.MaxDebounceDeferralMs));
        Assert.Equal(0, p.DueInMs(2000));
    }

    [Fact]
    public void TwoEventsWithin250ms_TrailingEdge()
    {
        var p = new AudioDeviceRecoveryPolicy();
        p.NoteDeviceEvent(0);
        Assert.Equal(250, p.DueInMs(0));
        p.NoteDeviceEvent(50);                 // re-stamps the trailing window (the 48000-then-44100 double notification)
        Assert.Equal(250, p.DueInMs(50));
        Assert.Equal(1, p.DueInMs(299));
        Assert.Equal(0, p.DueInMs(300));
        p.ClearPending();
        Assert.False(p.HasPending);
        Assert.Equal(int.MaxValue, p.DueInMs(300));
    }

    [Fact]
    public void Ladder_250_1000_3000_ThenNull()
    {
        var p = new AudioDeviceRecoveryPolicy();
        Assert.Equal(250, p.NextRetryDelayMs());
        Assert.Equal(1000, p.NextRetryDelayMs());
        Assert.Equal(3000, p.NextRetryDelayMs());
        Assert.Null(p.NextRetryDelayMs());
        Assert.Null(p.NextRetryDelayMs());     // stays exhausted
        p.ResetLadder();
        Assert.Equal(250, p.NextRetryDelayMs());
    }

    [Fact]
    public void DeviceEvent_ResetsLadder()
    {
        var p = new AudioDeviceRecoveryPolicy();
        Assert.Equal(250, p.NextRetryDelayMs());
        Assert.Equal(1000, p.NextRetryDelayMs());
        p.NoteDeviceEvent(5000);               // a real device event is new information: back to the first rung
        Assert.Equal(250, p.NextRetryDelayMs());
    }

    [Fact]
    public void SinkFailure_IgnoredWhileRetryScheduled()
    {
        var p = new AudioDeviceRecoveryPolicy();
        Assert.False(p.NoteSinkFailure(0, retryScheduled: true));
        Assert.False(p.HasPending);
        Assert.Equal(int.MaxValue, p.DueInMs(0));
        // ...and once the retry is gone, the next report starts a request again.
        Assert.True(p.NoteSinkFailure(10, retryScheduled: false));
        Assert.Equal(250, p.DueInMs(10));
    }
}
