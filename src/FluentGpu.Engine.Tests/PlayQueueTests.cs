using System;
using System.Collections.Generic;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>M3 §8.1: the fully-functional <see cref="PlayQueue"/> — Add/InsertNext/Remove/Clear/GoTo/Next/Previous, the
/// Current/CurrentIndex signals, Repeat, Shuffle (a real permutation), per-item transitions, and PrefetchPolicy.</summary>
public sealed class PlayQueueTests
{
    private static MediaSource Src(int i) => MediaSource.FromBytes(new byte[] { (byte)i }).WithKind(MediaKind.PcmAudio);

    [Fact]
    public void Add_SetsCurrentToFirst_AndSignalsTrack()
    {
        var q = new PlayQueue();
        Assert.Equal(-1, q.CurrentIndex.Peek());
        var a = q.Add(Src(0));
        Assert.Equal(0, q.CurrentIndex.Peek());
        Assert.Equal(a, q.Current.Peek());
        q.Add(Src(1));
        Assert.Equal(2, q.Items.Count);
        Assert.Equal(0, q.CurrentIndex.Peek());   // adding later items doesn't move current
    }

    [Fact]
    public void Next_Previous_HonorRepeatModes()
    {
        var q = new PlayQueue();
        q.Add(Src(0)); q.Add(Src(1)); q.Add(Src(2));

        _ = q.NextAsync(); Assert.Equal(1, q.CurrentIndex.Peek());
        _ = q.NextAsync(); Assert.Equal(2, q.CurrentIndex.Peek());
        _ = q.NextAsync(); Assert.Equal(2, q.CurrentIndex.Peek());   // Repeat.Off: no advance past the end

        q.Repeat = RepeatMode.All;
        _ = q.NextAsync(); Assert.Equal(0, q.CurrentIndex.Peek());   // wraps
        _ = q.PreviousAsync(); Assert.Equal(2, q.CurrentIndex.Peek());

        q.Repeat = RepeatMode.One;
        _ = q.NextAsync(); Assert.Equal(2, q.CurrentIndex.Peek());   // stays on the same item
    }

    [Fact]
    public void InsertNext_InsertsAfterCurrent()
    {
        var q = new PlayQueue();
        var a = q.Add(Src(0)); q.Add(Src(1));   // [0,1], current 0
        q.InsertNext(Src(9));                   // → [0,9,1]
        Assert.Equal(3, q.Items.Count);
        Assert.Equal(a, q.Current.Peek());       // current unchanged
        Assert.Equal((byte)9, ((BytesSource)q.Items[1].Source).Bytes.Span[0]);
    }

    [Fact]
    public void Remove_FixesUpCurrentIndex()
    {
        var q = new PlayQueue();
        q.Add(Src(0)); q.Add(Src(1)); q.Add(Src(2));
        _ = q.GoToAsync(2);
        Assert.Equal(2, q.CurrentIndex.Peek());
        q.Remove(q.Items[0]);                    // removing before current shifts it down
        Assert.Equal(1, q.CurrentIndex.Peek());
        q.Remove(q.Items[q.CurrentIndex.Peek()]); // removing current clamps
        Assert.Equal(0, q.CurrentIndex.Peek());
    }

    [Fact]
    public void Clear_ResetsToEmpty()
    {
        var q = new PlayQueue();
        q.Add(Src(0)); q.Add(Src(1));
        q.Clear();
        Assert.Equal(0, q.Items.Count);
        Assert.Equal(-1, q.CurrentIndex.Peek());
        Assert.Null(q.Current.Peek());
    }

    [Fact]
    public void Shuffle_VisitsEveryItemExactlyOnce_AsAPermutation()
    {
        var q = new PlayQueue(shuffleSeed: 12345);
        const int n = 8;
        for (int i = 0; i < n; i++) q.Add(Src(i));
        Assert.Equal(0, q.CurrentIndex.Peek());

        q.Shuffle = true;
        var visited = new HashSet<int> { q.CurrentIndex.Peek() };
        for (int i = 0; i < n - 1; i++)
        {
            _ = q.NextAsync();
            visited.Add(q.CurrentIndex.Peek());
        }
        Assert.Equal(n, visited.Count);   // a true permutation — every index exactly once
    }

    [Fact]
    public void PerItemTransition_OverridesTheDefault()
    {
        var q = new PlayQueue { DefaultTransition = TransitionMode.Gapless };
        var a = q.Add(Src(0)); q.Add(Src(1));
        Assert.Equal(TransitionKind.Gapless, q.TransitionAfter(0).Kind);

        q.SetTransition(a, ScheduledTransition.Crossfade(TimeSpan.FromSeconds(6)));
        var t = q.TransitionAfter(0);
        Assert.Equal(TransitionKind.Crossfade, t.Kind);
        Assert.Equal(6, t.Overlap.TotalSeconds, 3);
    }

    [Fact]
    public void PeekNext_And_NextTargetIndex_AreConsistent_WithoutMutating()
    {
        var q = new PlayQueue();
        q.Add(Src(0)); q.Add(Src(1));
        Assert.Equal(1, q.NextTargetIndex());
        Assert.Equal(q.Items[1], q.PeekNext());
        Assert.Equal(0, q.CurrentIndex.Peek());   // peeking never advances
    }

    [Fact]
    public void PrefetchPolicy_DefaultsAndRoundTrips()
    {
        var q = new PlayQueue();
        Assert.Equal(2, q.Prefetch.LookaheadItems);
        Assert.Equal(30, q.Prefetch.MaxPrefetchTime.TotalSeconds, 3);
        q.Prefetch = new PrefetchPolicy(3, TimeSpan.FromSeconds(60));
        Assert.Equal(3, q.Prefetch.LookaheadItems);
        Assert.Equal(60, q.Prefetch.MaxPrefetchTime.TotalSeconds, 3);
    }
}
