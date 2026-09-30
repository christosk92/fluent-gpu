using System;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <see cref="UiGapClassifier"/> — the pure decision behind the always-on UI-gap decomposition (FrameStats.UiGap):
/// a slack frame's gap is named Busy (the thread ran), Blocked (it sat in a wait it asked for, up to the timeout it asked
/// for) or NotScheduled (it was runnable — a wait's timeout had expired, or it was outside any wait — and did not run).
/// Replaces naming the gap by elimination ("Preempted" = no GC and no long wait), which could not tell the three apart.
/// </summary>
public sealed class UiGapClassifierTests
{
    static UiGapSample Sample(float gap, float requested = 0f, float blocked = 0f, float messages = 0f, float input = 0f,
                              float posts = 0f, float cold = 0f, float run = float.NaN)
        => new(gap, requested, blocked, messages, input, posts, cold, run);

    [Fact]
    public void Posts_that_ran_for_most_of_the_gap_are_Busy()
    {
        var b = UiGapClassifier.Classify(Sample(80f, requested: 8f, blocked: 6f, posts: 60f, run: 70f));
        Assert.Equal(UiGapVerdict.Busy, b.Verdict);
        Assert.Equal(70f, b.RunningMs, 3);
        Assert.Equal(6f, b.BlockedMs, 3);
        Assert.Equal(4f, b.NotScheduledMs, 3);
        Assert.True(b.RunFromCycles);
    }

    [Fact]
    public void An_infinite_wait_that_filled_the_gap_is_Blocked()
    {
        var b = UiGapClassifier.Classify(Sample(95f, requested: -1f, blocked: 93f, run: 1f));
        Assert.Equal(UiGapVerdict.Blocked, b.Verdict);
        Assert.Equal(93f, b.BlockedMs, 3);
        Assert.Equal(0f, b.OverrunMs, 3);
    }

    [Fact]
    public void A_wait_that_overran_its_8ms_request_to_90ms_without_running_is_NotScheduled()
    {
        // The timer expired at 8 ms; for the next 82 ms the thread was runnable and did not run.
        var b = UiGapClassifier.Classify(Sample(92f, requested: 8f, blocked: 90f, run: 0.3f));
        Assert.Equal(UiGapVerdict.NotScheduled, b.Verdict);
        Assert.Equal(82f, b.OverrunMs, 3);
        Assert.Equal(8f, b.BlockedMs, 3);
        Assert.True(b.NotScheduledMs > 80f);
    }

    [Fact]
    public void A_gap_outside_any_wait_where_the_thread_barely_ran_is_NotScheduled()
    {
        var b = UiGapClassifier.Classify(Sample(60f, run: 2f));
        Assert.Equal(UiGapVerdict.NotScheduled, b.Verdict);
        Assert.Equal(58f, b.NotScheduledMs, 3);
        Assert.Equal(0f, b.BlockedMs, 3);
    }

    [Fact]
    public void Without_a_cycle_counter_the_wall_measured_phases_stand_in_for_running_time()
    {
        var b = UiGapClassifier.Classify(Sample(60f, posts: 40f, messages: 10f));
        Assert.False(b.RunFromCycles);
        Assert.Equal(UiGapVerdict.Busy, b.Verdict);
        Assert.Equal(50f, b.RunningMs, 3);
        Assert.Equal(10f, b.NotScheduledMs, 3);
    }

    [Fact]
    public void A_gap_under_four_ms_is_not_named()
    {
        var b = UiGapClassifier.Classify(Sample(3.5f, run: 0f));
        Assert.Equal(UiGapVerdict.Unknown, b.Verdict);
    }

    [Fact]
    public void Running_time_is_clamped_to_the_gap_a_calibration_rate_can_overshoot()
    {
        var b = UiGapClassifier.Classify(Sample(20f, requested: 8f, blocked: 8f, run: 50f));
        Assert.Equal(12f, b.RunningMs, 3);
        Assert.Equal(0f, b.NotScheduledMs, 3);
    }

    [Fact]
    public void The_cycle_rate_is_the_running_maximum_of_samples_at_least_a_millisecond_long()
    {
        double rate = 0;
        rate = ThreadCycles.Calibrate(rate, 3_000_000, 1.0);    // 3 GHz-equivalent
        rate = ThreadCycles.Calibrate(rate, 1_000_000, 1.0);    // a pre-empted sample only lowers — ignored
        rate = ThreadCycles.Calibrate(rate, 9_000_000, 0.5);    // under a millisecond — ignored
        Assert.Equal(3_000_000.0, rate, 3);
        Assert.Equal(2f, ThreadCycles.ToMs(1_000_000, 7_000_000, rate), 3);
        Assert.True(float.IsNaN(ThreadCycles.ToMs(0, 7_000_000, rate)));
        Assert.True(float.IsNaN(ThreadCycles.ToMs(1, 7_000_000, 0.0)));
    }
}
