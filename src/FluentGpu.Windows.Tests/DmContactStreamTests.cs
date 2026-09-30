using System.Collections.Generic;
using FluentGpu.Pal.Windows;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The DirectManipulation touchpad producer's decision (<see cref="DmContactStream"/>) replayed over REAL per-Update
/// scripts from the owner's 2026-09-25 TpLog capture (<c>tplog-20260925-163000.csv</c>, 150 % scale, one Update per
/// 8.3 ms; each Update lists its viewport callbacks in the order DM raises them — the lift's whole-pixel snap
/// (<c>OnContentUpdated</c>) BEFORE the status edge). The defect: the snap was emitted as a Sample stamped at the End,
/// which read as a velocity collapse — 9 of 22 fast flicks held dead. The rules pinned here: an Update that ends in a
/// non-RUNNING status carries no motion, and the End carries DM's release verdict (INERTIA = moving, READY = at rest).
/// </summary>
public sealed class DmContactStreamTests
{
    private const float Scale150 = 1.5f;
    private const int Running = DmContactStream.Running, Ready = DmContactStream.Ready, Inertia = DmContactStream.Inertia;

    private readonly record struct Ev(ScrollGesture Phase, float Dx, float Dy, ContactRelease Release);

    private sealed class Sink : IDmContactSink
    {
        public readonly List<Ev> Events = new();
        public void OnContactEvent(ScrollGesture phase, float dipX, float dipY, ContactRelease release)
            => Events.Add(new Ev(phase, dipX, dipY, release));
    }

    /// <summary>One viewport callback inside an Update: a status edge, or a content update at content-space position
    /// (x, y) px (the TpLog's <c>x=</c>/<c>y=</c>; the transform carries its negation).</summary>
    private readonly record struct Cb(int Status, float X, float Y)
    {
        public bool IsStatus => Status >= 0;
    }

    private static Cb St(int status) => new(status, 0f, 0f);
    private static Cb At(float x, float y) => new(-1, x, y);

    /// <summary>Replays Updates (each a callback list, in DM's raise order) and returns every status effect raised.</summary>
    private static DmStatusEffects Replay(DmContactStream s, params Cb[][] updates)
    {
        var fx = DmStatusEffects.None;
        foreach (var u in updates)
        {
            foreach (var cb in u)
            {
                if (cb.IsStatus) fx |= s.OnStatus(cb.Status);
                else s.OnContent(1f, -cb.X, -cb.Y);
            }
            s.OnUpdateReturned(Scale150);
        }
        return fx;
    }

    private static Cb[] U(params Cb[] callbacks) => callbacks;

    private static void AssertSample(Ev e, float dxPx, float dyPx)
    {
        Assert.Equal(ScrollGesture.Sample, e.Phase);
        Assert.Equal(dxPx / Scale150, e.Dx, 0.01f);
        Assert.Equal(dyPx / Scale150, e.Dy, 0.01f);
    }

    /// <summary>Mode-1 contact at 270899852.276: two big RUNNING frames, three Updates with no change, then the Update at
    /// 270899902.202 whose content (dy +0.153 px — the whole-pixel snap) arrives BEFORE RUNNING→READY. The producer used
    /// to emit that snap as a 0.102 DIP Sample stamped at the End.</summary>
    [Fact]
    public void ReadySnap_ArrivingBeforeTheStatusEdge_IsNotASample()
    {
        var sink = new Sink();
        var s = new DmContactStream(sink);
        var fx = Replay(s,
            U(St(Running), At(10.108f, 21.092f)),      // 270899852.276 — the engage; the first content is the baseline
            U(At(518.975f, 1101.497f)),                // .563: dy +1080.405 px
            U(At(1117.375f, 2437.847f)),               // 270899868.895: dy +1336.35 px
            U(), U(), U(),                             // no-change Updates (no content callback)
            U(At(1117f, 2438f), St(Ready)));           // 270899902.202: the snap, then the lift edge

        Assert.Equal(4, sink.Events.Count);
        Assert.Equal(ScrollGesture.Begin, sink.Events[0].Phase);
        AssertSample(sink.Events[1], 508.867f, 1080.405f);    // 720.27 DIP
        AssertSample(sink.Events[2], 598.4f, 1336.35f);       // 890.90 DIP
        Assert.Equal(ScrollGesture.End, sink.Events[3].Phase);
        Assert.Equal(ContactRelease.Stopped, sink.Events[3].Release);   // RUNNING→READY: DM released it at rest
        Assert.Equal(DmStatusEffects.ResetViewport, fx);
    }

    /// <summary>Mode-2 (inertia configured) contact 24, caught out of DM inertia: RUNNING deltas up to 57.758 px up to
    /// +42.3 ms after the physical lift, then the Update at 270917460.8 carrying content dy 1.178 px (DM's first INERTIA
    /// frame) plus RUNNING→INERTIA. That Update emits no Sample; the End says the fingers released MOVING, and DM is to
    /// be stopped at the next pump (the coast is the engine's).</summary>
    [Fact]
    public void InertiaEdge_IsAMovingRelease_AndItsUpdateCarriesNoMotion()
    {
        var sink = new Sink();
        var s = new DmContactStream(sink);
        Replay(s,
            U(St(Inertia), At(3347f, 17062.607f)));     // 270917327.175: the previous flick's DM inertia (not a contact)
        Assert.Empty(sink.Events);

        (float X, float Y)[] run =
        {
            (3355.669f, 17079.447f), (3397.34f, 17176.984f), (3467.607f, 17313.592f), (3540.849f, 17473.133f),
            (3607.29f, 17646.83f), (3666.507f, 17821.943f), (3716.657f, 17988.846f), (3758.873f, 18143.998f),
            (3780.973f, 18247.621f), (3799.284f, 18339.07f), (3811.962f, 18416.08f), (3823.339f, 18482.332f),
            (3831.444f, 18540.09f),                                                   // 270917452.157: dy +57.758 px
        };
        var fx = Replay(s, U(St(Running), At(3347f, 17062.607f)));   // 270917335.491: INERTIA→RUNNING (the catch)
        foreach (var p in run) fx |= Replay(s, U(At(p.X, p.Y)));
        fx |= Replay(s,
            U(At(3831.641f, 18541.268f), St(Inertia)),  // 270917460.808: content dy +1.178 px, then RUNNING→INERTIA
            U(At(3847.277f, 18632.934f)));              // .844: DM's own inertia frame before the stop lands

        Assert.Equal(1 + run.Length + 1, sink.Events.Count);
        Assert.Equal(ScrollGesture.Begin, sink.Events[0].Phase);
        (float X, float Y) prev = (3347f, 17062.607f);
        for (int i = 0; i < run.Length; i++)
        {
            AssertSample(sink.Events[1 + i], run[i].X - prev.X, run[i].Y - prev.Y);
            prev = run[i];
        }
        var end = sink.Events[^1];
        Assert.Equal(ScrollGesture.End, end.Phase);
        Assert.Equal(ContactRelease.Moving, end.Release);
        Assert.True((fx & DmStatusEffects.StopAtNextPump) != 0);

        // The pump's Stop lands: INERTIA→READY is not a second lift.
        var after = Replay(s, U(At(3862f, 18720f), St(Ready)));
        Assert.Equal(1 + run.Length + 1, sink.Events.Count);
        Assert.Equal(DmStatusEffects.ResetViewport, after);
    }

    /// <summary>Mode-1 contact at 270898185.619: the fingers stop moving 49.8 ms before DM reports RUNNING→READY (the
    /// Update at 270898318.877, content dy +0.443 px first). Under the inertia configuration READY is DM's at-rest
    /// verdict: the End says Stopped and the snap is not a Sample.</summary>
    [Fact]
    public void ReadyAfterAPause_IsAStoppedRelease_WithNoSnapSample()
    {
        var sink = new Sink();
        var s = new DmContactStream(sink);
        (float X, float Y)[] run =
        {
            (23.09f, 66.449f), (56.046f, 152.236f), (147.77f, 376.188f), (120.412f, 644.874f), (113.616f, 897.704f),
            (111.074f, 1120.807f), (110.143f, 1309.934f), (109.502f, 1479.809f), (157.44f, 1459.141f), (197.34f, 1460.557f),
        };
        Replay(s, U(St(Running), At(1.946f, 3.737f)));                 // 270898185.619
        foreach (var p in run) Replay(s, U(At(p.X, p.Y)));             // .920 … 270898269.112
        Replay(s, U(), U(), U(), U(), U());                            // five Updates with no change
        var fx = Replay(s, U(At(197f, 1461f), St(Ready)));             // 270898318.877

        Assert.Equal(1 + run.Length + 1, sink.Events.Count);
        Assert.Equal(ScrollGesture.Begin, sink.Events[0].Phase);
        for (int i = 1; i <= run.Length; i++) Assert.Equal(ScrollGesture.Sample, sink.Events[i].Phase);
        AssertSample(sink.Events[run.Length], 197.34f - 157.44f, 1460.557f - 1459.141f);   // the last REAL movement
        Assert.Equal(ScrollGesture.End, sink.Events[^1].Phase);
        Assert.Equal(ContactRelease.Stopped, sink.Events[^1].Release);
        Assert.Equal(DmStatusEffects.ResetViewport, fx);
    }

    /// <summary>A content update is motion only once the Update that produced it has returned with the viewport still
    /// RUNNING — nothing is emitted from inside the COM callback.</summary>
    [Fact]
    public void ContentIsDelivered_WhenItsUpdateReturns_StillRunning()
    {
        var sink = new Sink();
        var s = new DmContactStream(sink);
        Replay(s, U(St(Running), At(0f, 0f)));
        s.OnContent(1f, 0f, -30f);                 // inside an Update: buffered
        Assert.Single(sink.Events);                // only the Begin so far
        s.OnUpdateReturned(Scale150);
        Assert.Equal(2, sink.Events.Count);
        AssertSample(sink.Events[1], 0f, 30f);
    }
}
