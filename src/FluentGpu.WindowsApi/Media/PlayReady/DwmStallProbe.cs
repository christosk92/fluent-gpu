using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Media;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The desktop compositor's freeze probe while protected video is around: does DWM stop composing for the whole machine when a
/// protected video starts or ends, and which step was running when it did?
/// <para><b>What it reads.</b> DWM's own frame ids (<c>DCompositionGetFrameId</c>, Windows 11): CREATED (DWM began a frame),
/// CONFIRMED (its CPU work and presents are done) and COMPLETED (its GPU work is done). They are system-wide, independent of our
/// rendering, and cost ~10 µs a read. <c>DwmGetCompositionTimingInfo</c> is no use here: on this Windows its global counters
/// advance by one per CALL and its compose time is the nearest vblank to the call, so it reports a healthy DWM whatever DWM does.
/// The probe never waits on the compositor clock either: a waiter makes DWM wake every vblank, which would change what is measured.</para>
/// <para><b>What counts as a stall</b> (one <c>[dwm.stall]</c> line each, over <see cref="EdgeMs"/>). DWM composes only when
/// something changed, so frame ids that stand still are normally an idle DWM (measured at rest: 16 creation gaps of 100-223 ms
/// in 20 s), and it reports COMPLETED lazily, on its next wake. So:
/// <c>kind=confirm</c> DWM had begun a frame (CREATED ahead of CONFIRMED) and its CPU side (a present, a kernel call) did not
/// finish: idle-proof, an idle DWM has no frame in flight (at rest the longest was 23 ms);
/// <c>kind=create</c> we presented after DWM's last new frame and DWM began no new one;
/// <c>kind=gpu</c> while such a present of ours waited, DWM's last confirmed frame's GPU work did not complete;
/// <c>kind=sched</c> this thread itself woke that late (the CPU, not DWM, stopped).
/// <c>create</c> and <c>gpu</c> need our own present as the proof that DWM was asked to compose: with our UI idle they cannot
/// tell a frozen DWM from an idle one and stay silent (<c>confirm</c> and <c>sched</c> do not depend on us). 250 ms after each
/// stall a second line names the protected-video step around its start (<see cref="ProtectedVideoTrace.DescribeAround"/>).</para>
/// <para><b>Cost.</b> No thread until the first protected session or runtime bring-up. Armed (a protected session alive, or
/// <see cref="ProtectedVideoTrace.LingerMs"/> after the last one or the last step): one high-resolution 8 ms timer wait and
/// three frame-id reads per sample, no allocation. Otherwise the thread is parked on an event.</para>
/// </summary>
internal static unsafe class DwmStallProbe
{
    private const double EdgeMs = 100.0;
    private const double SampleMs = 8.0;
    private const double SummaryDelayMs = 250.0;     // the native steps around a stall reach the trace through the event ring
    private const double OverrunMs = 10000.0;        // a stall still open when the linger ends is followed this much longer
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;

    private static int s_started;

    /// <summary>Start the probe thread once per process (the first protected session or runtime bring-up). Idempotent.</summary>
    internal static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref s_started, 1) != 0) return;
        try
        {
            new Thread(Run) { IsBackground = true, Name = "fg.dwm-probe", Priority = ThreadPriority.AboveNormal }.Start();
        }
        catch (Exception e) { Diag.Line("[dwm.probe] not started: " + e.Message); }
    }

    private struct Pending
    {
        public long Start;
        public long Due;
        public string? Kind;
    }

    // One DWM pipeline stage: since when it has owed progress (0 = it owes none), and what the counters read then.
    private struct Wait
    {
        public long Since;
        public ulong Created, Confirmed, Completed;
        public long Presents;

        public void Begin(long since, ulong created, ulong confirmed, ulong completed, long presents)
        {
            Since = since;
            Created = created;
            Confirmed = confirmed;
            Completed = completed;
            Presents = presents;
        }
    }

    private static void Run()
    {
        try
        {
            ulong probe;
            _ = DCompositionGetFrameId(COMPOSITION_FRAME_ID_TYPE.COMPOSITION_FRAME_ID_CREATED, &probe);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            Diag.Line("[dwm.probe] unavailable: dcomp.dll has no DCompositionGetFrameId (Windows 11) - no [dwm.stall] lines this run");
            return;
        }

        HANDLE timer = CreateWaitableTimerExW(null, null, CreateWaitableTimerHighResolution, TimerModifyState | Synchronize);
        var pending = new Pending[8];
        while (true)
        {
            ProtectedVideoTrace.WaitUntilActive();
            try { RunArmed(timer, pending); }
            catch (Exception e)
            {
                Diag.Line("[dwm.probe] sampling ended: " + e.GetType().Name + " " + e.Message);
                return;
            }
        }
    }

    private static bool Read(out ulong created, out ulong confirmed, out ulong completed)
    {
        ulong a = 0, b = 0, c = 0;
        bool ok = DCompositionGetFrameId(COMPOSITION_FRAME_ID_TYPE.COMPOSITION_FRAME_ID_CREATED, &a).SUCCEEDED
                  && DCompositionGetFrameId(COMPOSITION_FRAME_ID_TYPE.COMPOSITION_FRAME_ID_CONFIRMED, &b).SUCCEEDED
                  && DCompositionGetFrameId(COMPOSITION_FRAME_ID_TYPE.COMPOSITION_FRAME_ID_COMPLETED, &c).SUCCEEDED;
        created = a;
        confirmed = b;
        completed = c;
        return ok;
    }

    private static long FramePeriodTicks(ulong frameId)
    {
        COMPOSITION_FRAME_STATS stats = default;
        uint targets = 0;
        if (frameId != 0 && DCompositionGetStatistics(frameId, &stats, 0, null, &targets).SUCCEEDED && stats.framePeriod > 0)
            return (long)stats.framePeriod;
        return Stopwatch.Frequency / 60;
    }

    private static void Sleep(HANDLE timer, long sampleTicks100ns)
    {
        if (timer != HANDLE.NULL)
        {
            LARGE_INTEGER due = default;
            due.QuadPart = -sampleTicks100ns;   // relative
            if (SetWaitableTimer(timer, &due, 0, null, null, BOOL.FALSE))
            {
                WaitForSingleObject(timer, INFINITE);
                return;
            }
        }
        Thread.Sleep((int)SampleMs);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static void RunArmed(HANDLE timer, Pending[] pending)
    {
        var ci = CultureInfo.InvariantCulture;
        long f = Stopwatch.Frequency;
        long edge = (long)(EdgeMs * f / 1000.0);
        long sampleTicks = (long)(SampleMs * f / 1000.0);
        long overrun = (long)(OverrunMs * f / 1000.0);

        long now = Stopwatch.GetTimestamp();
        long armedAt = now;
        if (!Read(out ulong created, out ulong confirmed, out ulong completed)) { created = confirmed = completed = 0; }
        long period = FramePeriodTicks(completed);
        Diag.Line(string.Create(ci, $"[dwm.probe] armed qpc={Ms(now):F1} unixMs={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} refreshMs={Ms(period):F2} live={ProtectedVideoTrace.LiveSessions}"));

        Wait create = default, confirm = default, complete = default;
        long createdAt = now;                          // the sample at which CREATED last moved
        long presents = ProtectedVideoTrace.Presents;
        if (created > confirmed) confirm.Begin(now, created, confirmed, completed, presents);
        long lagSince = confirmed > completed ? now : 0;   // since when COMPLETED trails CONFIRMED (lazy at idle; see below)

        long prevSample = now, inactiveSince = 0;
        int samples = 0, stalls = 0;
        long maxLate = 0, maxCreate = 0, maxConfirm = 0, maxComplete = 0;

        while (true)
        {
            Sleep(timer, sampleTicks * 10_000_000 / f);
            now = Stopwatch.GetTimestamp();
            samples++;

            long late = now - prevSample - sampleTicks;
            if (late > maxLate) maxLate = late;
            if (late > edge)
            {
                long start = prevSample + sampleTicks;
                Report("sched", start, now, period, created, confirmed, completed, created, confirmed, completed,
                       ProtectedVideoTrace.Presents, ProtectedVideoTrace.Presents, pending);
                stalls++;
            }
            prevSample = now;

            if (Read(out ulong c, out ulong cf, out ulong cp))
            {
                presents = ProtectedVideoTrace.Presents;

                // CREATED: owed only once we presented after DWM's last new frame.
                if (c != created)
                {
                    if (create.Since != 0)
                    {
                        long gap = now - create.Since;
                        if (gap > maxCreate) maxCreate = gap;
                        if (gap > edge)
                        {
                            Report("create", create.Since, now, period, create.Created, create.Confirmed, create.Completed, c, cf, cp,
                                   create.Presents, presents, pending);
                            stalls++;
                        }
                    }
                    create.Since = 0;
                    createdAt = now;
                }
                else if (create.Since == 0)
                {
                    long lastPresent = ProtectedVideoTrace.LastPresentQpc;
                    if (lastPresent > createdAt) create.Begin(lastPresent, c, cf, cp, presents);
                }

                // CONFIRMED: owed while a begun frame's CPU side has not finished.
                if (cf != confirmed)
                {
                    if (confirm.Since != 0)
                    {
                        long gap = now - confirm.Since;
                        if (gap > maxConfirm) maxConfirm = gap;
                        if (gap > edge)
                        {
                            Report("confirm", confirm.Since, now, period, confirm.Created, confirm.Confirmed, confirm.Completed, c, cf, cp,
                                   confirm.Presents, presents, pending);
                            stalls++;
                        }
                    }
                    confirm.Since = 0;
                    if (c > cf) confirm.Begin(now, c, cf, cp, presents);
                }
                else if (confirm.Since == 0 && c > cf) confirm.Begin(now, c, cf, cp, presents);

                // COMPLETED: owed while a confirmed frame's GPU work has not finished AND a present of ours waits on DWM. DWM
                // updates COMPLETED lazily: it goes idle with a confirmed frame and reports it complete only when it next wakes
                // (measured at rest: confirmed ahead of completed in 186 of 2411 samples, once for 100.8 ms), so a lag alone is
                // an idle DWM. Counted from when our present met the lag.
                if (cp != completed)
                {
                    if (complete.Since != 0)
                    {
                        long gap = now - complete.Since;
                        if (gap > maxComplete) maxComplete = gap;
                        if (gap > edge)
                        {
                            Report("gpu", complete.Since, now, period, complete.Created, complete.Confirmed, complete.Completed, c, cf, cp,
                                   complete.Presents, presents, pending);
                            stalls++;
                        }
                    }
                    complete.Since = 0;
                    lagSince = cf > cp ? now : 0;
                }
                else if (lagSince == 0 && cf > cp) lagSince = now;
                if (complete.Since == 0 && lagSince != 0 && create.Since != 0)
                    complete.Begin(Math.Max(lagSince, create.Since), c, cf, cp, presents);

                created = c;
                confirmed = cf;
                completed = cp;
            }

            bool owed = ResolvePending(pending, now);
            if (ProtectedVideoTrace.IsActive(now)) { inactiveSince = 0; continue; }
            if (inactiveSince == 0) inactiveSince = now;
            bool open = create.Since != 0 || confirm.Since != 0 || complete.Since != 0;
            if ((open && now - inactiveSince < overrun) || owed) continue;
            break;
        }

        // Still-open waits at park: how long they had been owed (a stall that outlived the overrun is still on the record).
        if (create.Since != 0) maxCreate = Math.Max(maxCreate, now - create.Since);
        if (confirm.Since != 0) maxConfirm = Math.Max(maxConfirm, now - confirm.Since);
        if (complete.Since != 0) maxComplete = Math.Max(maxComplete, now - complete.Since);
        Diag.Line(string.Create(ci,
            $"[dwm.probe] park armedMs={Ms(now - armedAt):F0} samples={samples} stalls={stalls} maxLateMs={Ms(Math.Max(0, maxLate)):F1} maxCreateMs={Ms(maxCreate):F1} maxConfirmMs={Ms(maxConfirm):F1} maxGpuMs={Ms(maxComplete):F1}"));
    }

    private static void Report(string kind, long start, long end, long period,
                               ulong c0, ulong cf0, ulong cp0, ulong c1, ulong cf1, ulong cp1, long presents0, long presents1, Pending[] pending)
    {
        var ci = CultureInfo.InvariantCulture;
        long gap = end - start;
        long frames = period > 0 ? gap / period : 0;
        Diag.Line(string.Create(ci,
            $"[dwm.stall] ms={Ms(gap):F0} start={Ms(start):F1} frames={frames} kind={kind} created=+{c1 - c0} confirmed=+{cf1 - cf0} completed=+{cp1 - cp0} inflight={(long)(c1 - cp1)} ourPresents=+{presents1 - presents0}"));
        long due = end + (long)(SummaryDelayMs * Stopwatch.Frequency / 1000.0);
        for (int i = 0; i < pending.Length; i++)
        {
            if (pending[i].Kind is not null) continue;
            pending[i] = new Pending { Start = start, Due = due, Kind = kind };
            return;
        }
        // Eight summaries already owed: name this one now rather than drop it.
        Diag.Line(string.Create(ci, $"[dwm.stall] {ProtectedVideoTrace.DescribeAround(start)} start={Ms(start):F1} kind={kind}"));
    }

    // Write the summaries that are due. True while any is still owed.
    private static bool ResolvePending(Pending[] pending, long now)
    {
        bool owed = false;
        for (int i = 0; i < pending.Length; i++)
        {
            ref Pending p = ref pending[i];
            if (p.Kind is null) continue;
            if (now < p.Due) { owed = true; continue; }
            Diag.Line(string.Create(CultureInfo.InvariantCulture,
                $"[dwm.stall] {ProtectedVideoTrace.DescribeAround(p.Start)} start={Ms(p.Start):F1} kind={p.Kind}"));
            p = default;
        }
        return owed;
    }
}
