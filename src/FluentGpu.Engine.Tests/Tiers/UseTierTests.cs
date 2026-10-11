using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E22: <c>UseTier</c> on a headless host. The first mount resolves in its own frame (the first render already reads the final
/// tier); a crossing found after mount is stored pending and written by the host on the first frame that is NOT resized, so a
/// crossing from a resized frame lands exactly one frame later, once per crossing, and a source that crosses back before
/// the commit writes nothing. Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class UseTierTests
{
    private sealed class Probe : Component
    {
        private readonly TierLadder _ladder;
        private readonly TierSource _source;
        public int Renders;
        public int LastTier = -1;
        public readonly List<int> Seen = new();

        public Probe(TierLadder ladder, TierSource source) { _ladder = ladder; _source = source; }

        public override Element Render()
        {
            var tier = UseTier(_ladder, _source);
            int t = tier.Value;           // subscribes: the component re-renders on a committed crossing only
            Renders++;
            LastTier = t;
            Seen.Add(t);
            return new BoxEl { Width = 100f, Height = 40f };
        }
    }

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessWindow Window { get; }
        public AppHost Host { get; }
        public Probe Probe { get; }

        public Rig(Probe probe, float widthPx, bool composited = false)
        {
            var strings = new StringTable();
            Window = new HeadlessWindow(new WindowDesc("use-tier", new Size2(widthPx, 300), 1f, Composited: composited));
            Window.Show();
            Probe = probe;
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
            Host.RunFrame();
        }

        public void Frames(int n) { for (int i = 0; i < n; i++) Host.RunFrame(); }
        public void Resize(float widthPx) => Window.ClientSizePx = new Size2(widthPx, 300);

        /// <summary>Resize and run the two frames a crossing needs (the resized one, then the committing one), plus a settle.</summary>
        public void ResizeAndSettle(float widthPx) { Resize(widthPx); Frames(3); }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    private static TierLadder Ladder() => TierLadder.Of(new TierEdge(500f), new TierEdge(1000f));

    [Theory]
    [InlineData(400f, 0)]
    [InlineData(700f, 1)]
    [InlineData(1200f, 2)]
    public void TheFirstFrame_AlreadyReadsTheFinalTier_WithNoCorrectiveRender(float width, int expected)
    {
        using var rig = new Rig(new Probe(Ladder(), TierSource.ViewportWidth), width);
        Assert.Equal(new[] { expected }, rig.Probe.Seen.ToArray());   // the very first render saw the final tier
        rig.Frames(4);
        Assert.Equal(1, rig.Probe.Renders);                          // and nothing corrected it afterwards
        Assert.Equal(0, rig.Host.TiersForTest.PendingCount);
        Assert.Equal(0L, rig.Host.TiersForTest.CommittedWrites);     // a first mount is not a commit
    }

    [Fact]
    public void ACrossingFromAResizedFrame_LandsOneFrameLater()
    {
        using var rig = new Rig(new Probe(Ladder(), TierSource.ViewportWidth), 400f);
        rig.Frames(2);
        Assert.Equal(1, rig.Probe.Renders);

        rig.Resize(600f);
        rig.Host.RunFrame();                                          // the resized frame: the old tier is laid out at the new size
        Assert.Equal(1, rig.Probe.Renders);
        Assert.Equal(0, rig.Probe.LastTier);
        Assert.Equal(1, rig.Host.TiersForTest.PendingCount);          // found, stored, not written

        rig.Host.RunFrame();                                          // the first non-resized frame commits and reconciles it
        Assert.Equal(2, rig.Probe.Renders);
        Assert.Equal(1, rig.Probe.LastTier);
        Assert.Equal(0, rig.Host.TiersForTest.PendingCount);
        Assert.Equal(1L, rig.Host.TiersForTest.CommittedWrites);
    }

    [Fact]
    public void TheCrossingRequestsItsFrame_EvenWithNothingElseQueued()
    {
        using var rig = new Rig(new Probe(Ladder(), TierSource.ViewportWidth), 400f);
        rig.Frames(3);                                                // settled

        rig.Resize(600f);
        rig.Host.RunFrame();                                          // resized frame stores the crossing
        Assert.True(rig.Host.TiersForTest.HasPending);
        Assert.True(rig.Host.HasActiveWork);                          // the pending tier is work: the loop will run the next frame
        Assert.NotEqual(WakeReasons.None, rig.Host.CurrentWakeReasons & WakeReasons.FrameNeeded);
        rig.Host.RunFrame();
        Assert.False(rig.Host.TiersForTest.HasPending);               // committed
    }

    [Fact]
    public void ACrossing_FiresOncePerCrossing_AndNeverInsideATier()
    {
        using var rig = new Rig(new Probe(Ladder(), TierSource.ViewportWidth), 400f);
        rig.Frames(2);
        foreach (float w in new[] { 420f, 450f, 499f }) rig.ResizeAndSettle(w);     // inside tier 0
        Assert.Equal(new[] { 0 }, rig.Probe.Seen.ToArray());

        rig.ResizeAndSettle(600f);                                                  // 0 -> 1
        foreach (float w in new[] { 700f, 999f }) rig.ResizeAndSettle(w);           // inside tier 1
        rig.ResizeAndSettle(1200f);                                                 // 1 -> 2
        rig.ResizeAndSettle(900f);                                                  // 2 -> 1
        rig.ResizeAndSettle(400f);                                                  // 1 -> 0

        Assert.Equal(new[] { 0, 1, 2, 1, 0 }, rig.Probe.Seen.ToArray());            // exactly one render per crossing
        Assert.Equal(4L, rig.Host.TiersForTest.CommittedWrites);
    }

    [Fact]
    public void TheLadderHolds_ApplyThroughTheHook()
    {
        // Edge 500: widen at 540, narrow below 480.
        var ladder = TierLadder.Of(new TierEdge(500f, NarrowHold: 20f, WidenHold: 40f));
        using var rig = new Rig(new Probe(ladder, TierSource.ViewportWidth), 400f);
        rig.Frames(2);
        rig.ResizeAndSettle(530f);
        Assert.Equal(0, rig.Probe.LastTier);               // inside the widening band
        rig.ResizeAndSettle(560f);
        Assert.Equal(1, rig.Probe.LastTier);
        rig.ResizeAndSettle(485f);
        Assert.Equal(1, rig.Probe.LastTier);               // inside the narrowing band: holds
        rig.ResizeAndSettle(470f);
        Assert.Equal(0, rig.Probe.LastTier);
        Assert.Equal(new[] { 0, 1, 0 }, rig.Probe.Seen.ToArray());
    }

    [Fact]
    public void ASourceThatCrossesBack_BeforeTheCommit_WritesNothing()
    {
        using var rig = new Rig(new Probe(Ladder(), TierSource.ViewportWidth), 400f);
        rig.Frames(2);

        rig.Resize(600f);
        rig.Host.RunFrame();                                          // resized: pending tier 1
        Assert.Equal(1, rig.Host.TiersForTest.PendingCount);
        rig.Resize(400f);
        rig.Host.RunFrame();                                          // resized again: the commit is not allowed, and the source is back
        Assert.Equal(0, rig.Host.TiersForTest.PendingCount);          // cancelled
        rig.Frames(3);
        Assert.Equal(1, rig.Probe.Renders);                           // the consumer never saw the excursion
        Assert.Equal(0L, rig.Host.TiersForTest.CommittedWrites);
    }

    [Fact]
    public void TwoConsecutiveResizedFrames_BothKeepTheOldTier_AndTheNextOneCommits()
    {
        using var rig = new Rig(new Probe(Ladder(), TierSource.ViewportWidth), 400f);
        rig.Frames(2);

        rig.Resize(600f);
        rig.Host.RunFrame();
        Assert.Equal(0, rig.Probe.LastTier);
        rig.Resize(650f);                                             // a second size change: still resized, still pending
        rig.Host.RunFrame();
        Assert.Equal(0, rig.Probe.LastTier);
        Assert.Equal(1, rig.Host.TiersForTest.PendingCount);
        Assert.Equal(1, rig.Probe.Renders);

        rig.Host.RunFrame();                                          // the first non-resized frame
        Assert.Equal(1, rig.Probe.LastTier);
        Assert.Equal(2, rig.Probe.Renders);
    }

    [Fact]
    public void AnEstimateSource_FollowsTheSignalsItReads_AndCrossesOnce()
    {
        var est = new FloatSignal(400f);
        using var rig = new Rig(new Probe(Ladder(), TierSource.Estimate(() => est.Value)), 800f);
        Assert.Equal(new[] { 0 }, rig.Probe.Seen.ToArray());          // the estimate, not the viewport, drove the first frame
        rig.Frames(2);

        est.Value = 700f;
        rig.Frames(3);
        Assert.Equal(new[] { 0, 1 }, rig.Probe.Seen.ToArray());
        est.Value = 750f;                                             // same tier
        rig.Frames(3);
        Assert.Equal(2, rig.Probe.Renders);
    }

    [Fact]
    public void AMeasuredSource_IsSeededByTheEstimate_UntilTheSlotIsMeasured()
    {
        var slot = new Signal<float>(0f);                             // not measured yet
        using var rig = new Rig(new Probe(Ladder(), TierSource.Measured(slot, seed: () => 1200f)), 400f);
        Assert.Equal(new[] { 2 }, rig.Probe.Seen.ToArray());          // the seed, so the first frame is already in its tier
        rig.Frames(2);

        slot.Value = 300f;                                            // the real measure disagrees
        rig.Frames(3);
        Assert.Equal(new[] { 2, 0 }, rig.Probe.Seen.ToArray());
    }

    [Fact]
    public void AViewportHeightSource_UsesTheHeight()
    {
        var ladder = TierLadder.Of(new TierEdge(330f));               // the Short / Regular split
        using var rig = new Rig(new Probe(ladder, TierSource.ViewportHeight), 800f);
        Assert.Equal(0, rig.Probe.LastTier);                          // 300 tall
        rig.Window.ClientSizePx = new Size2(800f, 600f);
        rig.Frames(3);
        Assert.Equal(1, rig.Probe.LastTier);
    }
}
