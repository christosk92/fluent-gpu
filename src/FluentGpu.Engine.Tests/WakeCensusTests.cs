using System;
using System.Text;
using FluentGpu.Hosting;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F242: the always-on <c>[wake]</c> census names who armed the host timers that fired (<c>timersSeen=</c>), what the UI frames
/// that published a scene were for (<c>uiPresents=</c>), and what made each render-thread submit run and whether it recorded
/// (<c>renderPresents=</c>, <c>recordedPresents=</c>). The pieces are pure or queue-local, so they are pinned here without a host.
/// </summary>
public sealed class WakeCensusTests
{
    private static void NoOp(long gen) { }

    private static string TimersSeen(HostTimerQueue q)
    {
        var sb = new StringBuilder();
        q.AppendTimersSeen(sb);
        return sb.ToString();
    }

    [Fact]
    public void TimersSeen_NamesEachOwnerWithItsFires_AndAnUnattributableTimerIsQuestionMarked()
    {
        double now = 0;
        var q = new HostTimerQueue(() => now);
        Assert.Equal(" | timersSeen=0", TimersSeen(q));

        q.Schedule(1, 1, NoOp, typeof(string));
        q.Schedule(1, 2, NoOp, typeof(string));
        q.Schedule(1, 3, NoOp, typeof(int));
        q.Schedule(1, 4, NoOp);                       // a static method group: no target, no owner given
        now = 5;
        q.Drain();

        Assert.Equal(" | timersSeen=3:String×2,Int32×1,?×1", TimersSeen(q));
        Assert.Equal(4, q.FiresInWindow);

        q.ResetFireCensus();
        Assert.Equal(" | timersSeen=0", TimersSeen(q));
        Assert.Equal(0, q.FiresInWindow);
    }

    [Fact]
    public void EveryWakeBit_PrintsUnderItsOwnName()
    {
        // A stale or missing name made a playing meter (FrameClockPaceable, bit 25) read as "budgetDeferredVirtuals".
        foreach (WakeReasons bit in Enum.GetValues<WakeReasons>())
        {
            if (bit == WakeReasons.None) continue;
            string name = bit.ToString();
            Assert.Equal(char.ToLowerInvariant(name[0]) + name[1..], WakeDiagnostics.ReasonName(bit));
        }
    }

    private sealed class TickOwner { }
    private sealed class PaceableOwner { }

    [Fact]
    public void APollerFrame_IsChargedOnlyToTheSubscribersOfTheClockWhoseBitItCarried()
    {
        FluentGpu.Hosting.Threading.ThreadGuard.BindCurrent(FluentGpu.Hosting.Threading.ThreadGuard.ThreadRole.Ui);
        var scene = new FluentGpu.Scene.SceneStore();
        var runtime = new FluentGpu.Signals.ReactiveRuntime();
        var tick = new FluentGpu.Signals.Signal<object?>(0L);
        var paceable = new FluentGpu.Signals.Signal<object?>(0L);
        using var onTick = new FluentGpu.Signals.Effect(runtime, () => _ = tick.Value) { DiagOwner = new TickOwner() };
        using var onPaceable = new FluentGpu.Signals.Effect(runtime, () => _ = paceable.Value) { DiagOwner = new PaceableOwner() };
        var diag = new WakeDiagnostics(tick, new FluentGpu.Animation.AnimEngine(scene), scene, static _ => { }, () => true,
            paceableSig: paceable);

        diag.Record(WakeReasons.FrameClockPoller, awake: true, rendered: true, reconciled: false, laidOut: false, minimized: false);
        diag.Record(WakeReasons.FrameClockPoller, awake: true, rendered: true, reconciled: false, laidOut: false, minimized: false);
        diag.Record(WakeReasons.FrameClockPaceable, awake: true, rendered: true, reconciled: false, laidOut: false, minimized: false);
        var sb = new StringBuilder();
        diag.AppendPollersSeen(sb);
        Assert.Equal(" | pollersSeen=2:TickOwner×2,PaceableOwner×1", sb.ToString());
    }

    [Fact]
    public void APaceableClockFrame_IsAPollerPresent()
        => Assert.Equal(WakeDiagnostics.UiPresentCause.Poller,
            WakeDiagnostics.ClassifyUiPresent(WakeReasons.FrameClockPaceable, reconciled: false, laidOut: false));

    [Fact]
    public void ACallbackWithATarget_IsAttributedToThatTargetsType_WhenNoOwnerIsPassed()
    {
        double now = 0;
        var q = new HostTimerQueue(() => now);
        var target = new FireTarget();
        q.Schedule(1, 1, target.Fire);
        now = 2;
        q.Drain();
        Assert.Equal(" | timersSeen=1:FireTarget×1", TimersSeen(q));
        Assert.Equal(1, target.Fires);
    }

    private sealed class FireTarget
    {
        public int Fires;
        public void Fire(long gen) => Fires++;
    }

    [Fact]
    public void OwnersPastTheTableCapacity_FoldIntoAnOverflowBucket_AndAreStillCounted()
    {
        double now = 0;
        var q = new HostTimerQueue(() => now);
        Type[] owners =
        [
            typeof(int), typeof(long), typeof(short), typeof(byte), typeof(sbyte), typeof(uint), typeof(ulong), typeof(ushort),
            typeof(float), typeof(double), typeof(char), typeof(bool), typeof(decimal),
        ];
        for (int i = 0; i < owners.Length; i++) q.Schedule(1, i, NoOp, owners[i]);
        now = 2;
        q.Drain();
        string line = TimersSeen(q);
        Assert.Contains(" | timersSeen=13:", line);   // 12 named + the overflow bucket
        Assert.EndsWith(",+×1", line);
        Assert.Equal(13, q.FiresInWindow);
    }

    [Fact]
    public void AFireWhoseGenerationWasBumped_IsStillCounted_ItWokeTheHostEvenIfTheCallbackIsAGuardedNoOp()
    {
        double now = 0;
        var q = new HostTimerQueue(() => now);
        int ran = 0;
        long gen = 7;
        q.Schedule(1, 6, g => { if (g == gen) ran++; }, typeof(WakeCensusTests));   // stale: armed under generation 6
        now = 2;
        q.Drain();
        Assert.Equal(0, ran);
        Assert.Equal(" | timersSeen=1:WakeCensusTests×1", TimersSeen(q));
    }

    [Fact]
    public void UiPresentCause_AutonomousWakesWinOverTheWorkShape_ThenReconcileLayoutSignalOther()
    {
        // A timer tick that also re-rendered a component is still a timer-driven present (the anonymous 30 Hz ticker).
        Assert.Equal(WakeDiagnostics.UiPresentCause.Timer,
            WakeDiagnostics.ClassifyUiPresent(WakeReasons.Timer | WakeReasons.FrameClockPoller, reconciled: true, laidOut: true));
        Assert.Equal(WakeDiagnostics.UiPresentCause.Poller,
            WakeDiagnostics.ClassifyUiPresent(WakeReasons.FrameClockPoller | WakeReasons.FrameNeeded, reconciled: true, laidOut: false));
        Assert.Equal(WakeDiagnostics.UiPresentCause.Reconcile,
            WakeDiagnostics.ClassifyUiPresent(WakeReasons.FrameNeeded, reconciled: true, laidOut: true));
        Assert.Equal(WakeDiagnostics.UiPresentCause.Layout,
            WakeDiagnostics.ClassifyUiPresent(WakeReasons.FrameNeeded, reconciled: false, laidOut: true));
        Assert.Equal(WakeDiagnostics.UiPresentCause.Signal,
            WakeDiagnostics.ClassifyUiPresent(WakeReasons.RuntimePending, reconciled: false, laidOut: false));
        Assert.Equal(WakeDiagnostics.UiPresentCause.Signal,
            WakeDiagnostics.ClassifyUiPresent(WakeReasons.FrameNeeded | WakeReasons.Anim, reconciled: false, laidOut: false));
        Assert.Equal(WakeDiagnostics.UiPresentCause.Other,
            WakeDiagnostics.ClassifyUiPresent(WakeReasons.WarmCadence, reconciled: false, laidOut: false));
    }

    [Fact]
    public void RenderPresentCause_FreshWins_ThenAnimScrollCrossfadeOther()
    {
        Assert.Equal(RenderPresentCause.Fresh, RenderPresentCensus.Classify(fresh: true, animChanged: true, scrollMoved: true, crossfades: true));
        Assert.Equal(RenderPresentCause.Anim, RenderPresentCensus.Classify(false, animChanged: true, scrollMoved: true, crossfades: true));
        Assert.Equal(RenderPresentCause.Scroll, RenderPresentCensus.Classify(false, false, scrollMoved: true, crossfades: true));
        Assert.Equal(RenderPresentCause.Crossfade, RenderPresentCensus.Classify(false, false, false, crossfades: true));
        Assert.Equal(RenderPresentCause.Other, RenderPresentCensus.Classify(false, false, false, false));
    }

    [Fact]
    public void RenderPresentCensus_PrintsTheWindowDelta_AndOpensTheNextWindow()
    {
        var census = new RenderPresentCensus();
        var sb = new StringBuilder();
        census.AppendWindow(sb);
        Assert.Equal(" renderPresents=0 recordedPresents=0 compositePresents=0", sb.ToString());

        census.Note(RenderPresentCause.Fresh, recorded: true);
        census.Note(RenderPresentCause.Anim, recorded: true);
        census.Note(RenderPresentCause.Scroll, recorded: false);
        census.Note(RenderPresentCause.Scroll, recorded: false);
        sb.Clear();
        census.AppendWindow(sb);
        Assert.Equal(" renderPresents=4:fresh×1,anim×1,scroll×2 recordedPresents=2 compositePresents=2", sb.ToString());

        census.Note(RenderPresentCause.Crossfade, recorded: true);
        sb.Clear();
        census.AppendWindow(sb);
        Assert.Equal(" renderPresents=1:crossfade×1 recordedPresents=1 compositePresents=0", sb.ToString());   // a delta, not a running total
    }
}
