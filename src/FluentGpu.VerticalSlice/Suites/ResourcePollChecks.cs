using System;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

// gate.resource.poll-* — polling resources (ResourceOptions.RefetchIntervalMs / RefetchWhileInactive /
// RefetchOnActivate / BackoffOnError + Resource.Error). Every timing assertion rides the headless DETERMINISTIC frame
// clock (FixedFrameTimeSource, 16 ms per Paint; the host timer queue reads it), except the parked-window gate, whose
// whole point is that the host queue does NOT run while parked: there the System.Threading.Timer fallback is awaited
// as an event (hang guard only) and the frozen frame clock proves the host queue never drove it.
//
// Timing tolerance: a completion settles inside some RunFrame/Paint whose clock is within one frame of the clock read
// after it, and a due poll fires on the first Paint whose clock reaches it — so "elapsed" is the interval −1/+2 frames.
static class ResourcePollChecks
{
    const double Frame = 16.0;

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        PollFromCompletion(strings, fonts);
        PausesInactiveAndActivationOverdue(strings, fonts);
        ActivationNotOverdue(strings, fonts);
        WhileInactiveParkedWindow(strings, fonts);
        BackoffAndErrorSignal(strings, fonts);
        IntervalChangeRearms(strings, fonts);
        MutateRearmsAndDepsChange(strings, fonts);
    }

    static (HeadlessPlatformApp app, HeadlessWindow window, ResourceProbe probe, AppHost host) Mount(string name, ResourceOptions options, StringTable strings, HeadlessFontSystem fonts)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(200, 120), 1f)); window.Show();
        var probe = new ResourceProbe { Options = options };
        var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        return (app, window, probe, host);
    }

    /// <summary>Paint until a load is in flight; the frame clock at that point, or NaN after <paramref name="maxFrames"/>.</summary>
    static double PaintUntilFetching(AppHost host, ResourceProbe probe, int maxFrames)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            if (probe.Res.IsFetching.Peek()) return host.FrameClockMsForTest;
            host.Paint(0);
        }
        return probe.Res.IsFetching.Peek() ? host.FrameClockMsForTest : double.NaN;
    }

    static bool Within(double elapsed, double intervalMs) => elapsed >= intervalMs - Frame && elapsed <= intervalMs + 2 * Frame;

    // gate.resource.poll-from-completion — the next load starts RefetchIntervalMs after the previous one COMPLETED; a
    // slow load in flight is never cancelled or stacked by the cadence (40 frames ≫ the interval: still one live load).
    static void PollFromCompletion(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, _, probe, host) = Mount("res-poll", new ResourceOptions { RefetchIntervalMs = 100f }, strings, fonts);
        using (app) using (host)
        {
            probe.Load(0).SetResult("v0");
            bool ready0 = probe.PumpUntilSettled(host, 1) && probe.Res.Loadable.Value.Peek() == "v0" && !probe.Res.IsFetching.Peek();
            double t0 = host.FrameClockMsForTest;
            double t1 = PaintUntilFetching(host, probe, 40);
            bool firstAtInterval = Within(t1 - t0, 100);

            var load1 = probe.Load(1);
            for (int i = 0; i < 40; i++) host.Paint(0);                  // 640 ms with the poll load still pending
            Thread.Sleep(30);                                            // a stacked load would have started by now
            bool slowKept = probe.Started == 2 && !load1.Task.IsCompleted && probe.Res.IsFetching.Peek();

            load1.SetResult("v1");
            bool ready1 = probe.PumpUntilSettled(host, 2) && probe.Res.Loadable.Value.Peek() == "v1";
            double t2 = host.FrameClockMsForTest;
            double t3 = PaintUntilFetching(host, probe, 40);
            bool secondFromCompletion = Within(t3 - t2, 100);
            probe.Load(2);
            Check("gate.resource.poll-from-completion polls RefetchIntervalMs after each completion; a slow load is never cancelled by a tick",
                ready0 && firstAtInterval && slowKept && ready1 && secondFromCompletion,
                $"ready0={ready0} first={t1 - t0:0} slowKept={slowKept} started={probe.Started} ready1={ready1} second={t3 - t2:0}");
        }
    }

    // gate.resource.poll-pauses-inactive + gate.resource.poll-activate-overdue-once — window-inactive (app suspend:
    // Paint keeps running, so the frame clock and the host queue DO advance — a paused poll is really paused, not merely
    // undrained); re-activation fires the overdue poll once, and a deactivate/activate flicker while it is in flight
    // starts nothing more.
    static void PausesInactiveAndActivationOverdue(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, _, probe, host) = Mount("res-poll-pause", new ResourceOptions { RefetchIntervalMs = 100f }, strings, fonts);
        using (app) using (host)
        {
            probe.Load(0).SetResult("v0");
            bool ready0 = probe.PumpUntilSettled(host, 1);
            host.SetWindowActive(false); host.Paint(0);
            for (int i = 0; i < 30; i++) host.Paint(0);                  // 480 ms ≫ the interval
            Thread.Sleep(20);
            bool paused = !probe.Res.IsFetching.Peek() && probe.Started == 1;
            Check("gate.resource.poll-pauses-inactive an inactive component's poll is paused (default RefetchWhileInactive=false)",
                ready0 && paused, $"ready0={ready0} fetching={probe.Res.IsFetching.Peek()} started={probe.Started}");

            host.SetWindowActive(true); host.Paint(0);
            bool firedOnActivate = probe.Res.IsFetching.Peek();
            var load1 = probe.Load(1);
            host.SetWindowActive(false); host.Paint(0); host.SetWindowActive(true); host.Paint(0);   // flicker while in flight
            for (int i = 0; i < 3; i++) host.Paint(0);
            Thread.Sleep(30);
            bool once = probe.Started == 2 && !load1.Task.IsCompleted;
            load1.SetResult("v1");
            bool landed = probe.PumpUntilSettled(host, 2) && probe.Res.Loadable.Value.Peek() == "v1";
            Check("gate.resource.poll-activate-overdue-once re-activation fires an overdue poll once (deduped against the in-flight load)",
                firedOnActivate && once && landed, $"firedOnActivate={firedOnActivate} once={once} started={probe.Started} landed={landed}");
        }
    }

    // gate.resource.poll-activate-not-overdue — StaleTimeMs=0 (stale on Ready) does NOT make every activation refetch:
    // overdue is time since the last completion. The poll then still fires at its original due time.
    static void ActivationNotOverdue(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, _, probe, host) = Mount("res-poll-notdue", new ResourceOptions { RefetchIntervalMs = 1000f }, strings, fonts);
        using (app) using (host)
        {
            probe.Load(0).SetResult("v0");
            bool ready0 = probe.PumpUntilSettled(host, 1) && probe.Res.IsStale.Peek();
            double t0 = host.FrameClockMsForTest;
            host.SetWindowActive(false); host.Paint(0); host.Paint(0);
            host.SetWindowActive(true); host.Paint(0); host.Paint(0);
            Thread.Sleep(20);
            bool noRefetch = !probe.Res.IsFetching.Peek() && probe.Started == 1;
            double t1 = PaintUntilFetching(host, probe, 90);
            bool keptDue = Within(t1 - t0, 1000);
            probe.Load(1);
            Check("gate.resource.poll-activate-not-overdue activation within the interval does not refetch (StaleTimeMs=0 is not 'overdue'); the poll keeps its due time",
                ready0 && noRefetch && keptDue, $"ready0={ready0} noRefetch={noRefetch} started={probe.Started} due={t1 - t0:0}");
        }
    }

    // gate.resource.poll-while-inactive-parked — a MINIMIZED window never runs Paint, so host timers never drain. With
    // RefetchWhileInactive the poll rides a System.Threading.Timer that posts the refetch (posts drain while parked) and
    // the completion lands while still parked. Default (no RefetchWhileInactive): nothing polls while parked, and the
    // poll resumes after restore.
    static void WhileInactiveParkedWindow(StringTable strings, HeadlessFontSystem fonts)
    {
        {
            var (app, window, probe, host) = Mount("res-poll-parked", new ResourceOptions { RefetchIntervalMs = 60f, RefetchWhileInactive = true }, strings, fonts);
            using (app) using (host)
            {
                probe.Load(0).SetResult("v0");
                bool ready0 = probe.PumpUntilSettled(host, 1);
                window.Minimize(); host.RunFrame();                     // park edge: the activation watcher moves the poll off the host queue
                double clockAtPark = host.FrameClockMsForTest;
                bool polledParked = ResourceProbe.PumpUntil(host, () => probe.Started >= 2);
                if (polledParked) probe.Load(1).SetResult("v1");
                bool landedParked = polledParked && ResourceProbe.PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "v1");
                bool polledAgain = landedParked && ResourceProbe.PumpUntil(host, () => probe.Started >= 3);   // re-armed from the parked completion
                bool clockFrozen = host.FrameClockMsForTest == clockAtPark;  // no Paint ran: the host queue could not have driven it
                window.State = WindowState.Normal; host.RunFrame();
                Check("gate.resource.poll-while-inactive-parked RefetchWhileInactive keeps polling a minimized window (thread timer + post; host queue frozen)",
                    ready0 && polledParked && landedParked && polledAgain && clockFrozen,
                    $"ready0={ready0} polled={polledParked} landed={landedParked} again={polledAgain} clockFrozen={clockFrozen} started={probe.Started}");
            }
        }
        {
            var (app, window, probe, host) = Mount("res-poll-parked-off", new ResourceOptions { RefetchIntervalMs = 60f }, strings, fonts);
            using (app) using (host)
            {
                probe.Load(0).SetResult("v0");
                bool ready0 = probe.PumpUntilSettled(host, 1);
                window.Minimize(); host.RunFrame();
                long until = Environment.TickCount64 + 300;              // 5× the interval of wall time, parked
                while (Environment.TickCount64 < until) { host.RunFrame(); Thread.Sleep(5); }
                bool quietParked = probe.Started == 1 && !probe.Res.IsFetching.Peek();
                window.State = WindowState.Normal; host.RunFrame();
                double t = PaintUntilFetching(host, probe, 20);
                bool resumed = !double.IsNaN(t);
                if (resumed) probe.Load(1);
                Check("gate.resource.poll-parked-default a minimized window does not poll by default; the poll resumes after restore",
                    ready0 && quietParked && resumed, $"ready0={ready0} quietParked={quietParked} started={probe.Started} resumed={resumed}");
            }
        }
    }

    // gate.resource.poll-backoff + gate.resource.error-signal — after n consecutive failures the next poll waits
    // interval × 2^n (±10% jitter); a success resets it. Resource.Error carries the failure and clears on success.
    static void BackoffAndErrorSignal(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, _, probe, host) = Mount("res-poll-backoff", new ResourceOptions { RefetchIntervalMs = 100f }, strings, fonts);
        using (app) using (host)
        {
            probe.Load(0).SetException(new InvalidOperationException("e0"));
            bool failed0 = probe.PumpUntilSettled(host, 1) && probe.Res.Loadable.IsFailed;
            bool err0 = probe.Res.Error.Peek()?.Message == "e0" && ReferenceEquals(probe.Res.Error.Peek(), probe.Res.LastError);
            double t0 = host.FrameClockMsForTest;
            double t1 = PaintUntilFetching(host, probe, 40);
            double e1 = t1 - t0;
            bool back1 = e1 >= 180 - Frame && e1 <= 220 + 2 * Frame;     // 2× (±10%)

            probe.Load(1).SetException(new InvalidOperationException("e1"));
            bool failed1 = probe.PumpUntilSettled(host, 2);
            bool err1 = probe.Res.Error.Peek()?.Message == "e1";
            double t2 = host.FrameClockMsForTest;
            double t3 = PaintUntilFetching(host, probe, 60);
            double e2 = t3 - t2;
            bool back2 = e2 >= 360 - Frame && e2 <= 440 + 2 * Frame;     // 4× (±10%)

            probe.Load(2).SetResult("ok");
            bool ok = probe.PumpUntilSettled(host, 3) && probe.Res.Loadable.IsReady && probe.Res.Loadable.Value.Peek() == "ok";
            bool errCleared = probe.Res.Error.Peek() is null && probe.Res.LastError is null;
            double t4 = host.FrameClockMsForTest;
            double t5 = PaintUntilFetching(host, probe, 40);
            bool reset = Within(t5 - t4, 100);
            probe.Load(3);
            Check("gate.resource.poll-backoff consecutive failures double the poll delay (2×, 4×, ±10%); a success resets it",
                failed0 && back1 && failed1 && back2 && ok && reset,
                $"failed0={failed0} e1={e1:0} e2={e2:0} ok={ok} afterSuccess={t5 - t4:0}");
            Check("gate.resource.error-signal Resource.Error carries each failure and clears on success",
                err0 && err1 && errCleared, $"err0={err0} err1={err1} errCleared={errCleared}");
        }
    }

    // gate.resource.poll-interval-change — options are re-pointed each render: a changed interval re-arms from the last
    // completion WITHOUT forcing a fetch (an already-overdue due moves to a full interval from now); 0 turns it off.
    static void IntervalChangeRearms(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, _, probe, host) = Mount("res-poll-change", new ResourceOptions { RefetchIntervalMs = 1000f }, strings, fonts);
        using (app) using (host)
        {
            probe.Load(0).SetResult("v0");
            bool ready0 = probe.PumpUntilSettled(host, 1);
            for (int i = 0; i < 20; i++) host.Paint(0);                  // 320 ms: a 100 ms interval would be overdue
            probe.Options = new ResourceOptions { RefetchIntervalMs = 100f };
            probe.Rev.Value++; host.Paint(0);                            // re-render re-points the options
            double tc = host.FrameClockMsForTest;
            host.Paint(0); host.Paint(0);
            bool noForcedFetch = !probe.Res.IsFetching.Peek();
            double t1 = PaintUntilFetching(host, probe, 40);
            bool rearmed = Within(t1 - tc, 100);
            probe.Load(1).SetResult("v1");
            bool ready1 = probe.PumpUntilSettled(host, 2);
            probe.Options = new ResourceOptions { RefetchIntervalMs = 0f };
            probe.Rev.Value++; host.Paint(0);
            for (int i = 0; i < 30; i++) host.Paint(0);
            Thread.Sleep(20);
            bool off = !probe.Res.IsFetching.Peek() && probe.Started == 2;
            Check("gate.resource.poll-interval-change a changed interval re-arms without forcing a fetch; 0 stops polling",
                ready0 && noForcedFetch && rearmed && ready1 && off,
                $"ready0={ready0} noForcedFetch={noForcedFetch} rearmed={t1 - tc:0} ready1={ready1} off={off} started={probe.Started}");
        }
    }

    // gate.resource.poll-mutate-rearm + gate.resource.poll-deps-change — a no-refresh Mutate re-arms the poll from the
    // mutate (not the earlier completion); a deps change cancels the armed poll (its load re-arms on completion) and no
    // tick lands on the re-keyed load while it is in flight.
    static void MutateRearmsAndDepsChange(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, _, probe, host) = Mount("res-poll-mutate", new ResourceOptions { RefetchIntervalMs = 100f }, strings, fonts);
        using (app) using (host)
        {
            probe.Load(0).SetResult("v0");
            bool ready0 = probe.PumpUntilSettled(host, 1);
            for (int i = 0; i < 4; i++) host.Paint(0);                   // 64 ms into the 100 ms interval
            probe.Res.Mutate("m", refresh: false);
            double tm = host.FrameClockMsForTest;
            double t1 = PaintUntilFetching(host, probe, 40);
            bool fromMutate = Within(t1 - tm, 100);
            Check("gate.resource.poll-mutate-rearm a no-refresh Mutate re-arms the poll from the mutate",
                ready0 && fromMutate, $"ready0={ready0} sinceMutate={t1 - tm:0}");

            probe.Load(1).SetResult("v1");
            bool ready1 = probe.PumpUntilSettled(host, 2);
            host.Paint(0);
            probe.Key.Value = 1; host.Paint(0);                          // re-key → reload (load #2)
            var load2 = probe.Load(2);
            for (int i = 0; i < 30; i++) host.Paint(0);                  // ≫ the interval while the re-keyed load is pending
            Thread.Sleep(30);
            bool noTick = probe.Started == 3 && !load2.Task.IsCompleted;
            load2.SetResult("k1");
            bool landed = probe.PumpUntilSettled(host, 3) && probe.Res.Loadable.Value.Peek() == "k1";
            double t2 = host.FrameClockMsForTest;
            double t3 = PaintUntilFetching(host, probe, 40);
            bool rearmedFromReload = Within(t3 - t2, 100);
            probe.Load(3);
            Check("gate.resource.poll-deps-change a deps change cancels the armed poll; the re-keyed load re-arms it from its completion",
                ready1 && noTick && landed && rearmedFromReload,
                $"ready1={ready1} noTick={noTick} started={probe.Started} landed={landed} next={t3 - t2:0}");
        }
    }
}
