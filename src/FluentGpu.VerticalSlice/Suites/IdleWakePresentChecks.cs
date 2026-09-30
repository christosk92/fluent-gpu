using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

/// <summary>gate.wake-present.idle-to-scroll — the live-app bug this proves fixed: across 44 real wheel glides the
/// app measured 20 missed-vblank markers, 19 of them landing on FRAME 0 of a glide (the very first frame after a
/// wheel notch arrives from deep idle; mid-glide pacing was clean — dtP95 8.5-9.0ms/120Hz, liveMissed=0). Three
/// candidate causes were on the table: (1) the render loop notices input a tick late, (2) the wheel notch is queued
/// behind a frame and only applied on the NEXT frame, (3) <c>AppHost.NotePresented</c>
/// mis-reports the wall-clock GAP since the loop's last present (which can be tens of seconds of legitimate idle —
/// nothing to show is not a missed vblank) as accumulated missed vsyncs, so the first present after any idle
/// stretch inherits a bogus jump. Root-caused to (3): <c>NotePresented</c> derived "missed" purely from
/// <c>qpc - prevPresentedQpc</c> with no notion of whether the loop was continuously trying to render in between.
///
/// <para>First fix (superseded): a plain <c>bool</c> latched at the two RunFrame exits that skip Paint entirely
/// (minimized, deep-idle) and consumed+cleared by the next <c>NotePresented</c>. Production scroll traces kept
/// showing the SAME marker on frame 0 of wheel glides and on idle frames after that fix shipped, for two reasons:
/// (i) it never covered a RunFrame turn that RAN Paint but elided the present itself (skip-submit's byte-identical
/// draw list, <c>ProductionGateBlocks</c>, device-lost recovery, …) — the app loop is frequently awake with nothing
/// to show (a playback tick, a diagnostics repaint), and every such turn still charged the NEXT real present for
/// the whole gap; (ii) under async present, the UI thread's idle-exit write could race the render thread's belated
/// <c>NotePresented</c> for a frame that was already in flight when the idle exit ran — that late call consumed
/// (cleared) the flag, so the REAL wake present that followed found it already gone and got charged anyway.</para>
///
/// <para>Current fix: <c>AppHost._lastNoPresentQpc</c> — a QPC timestamp, not a bool, written by every RunFrame/
/// Paint exit that submitted no present this turn (see that field's doc for the full enumeration) and never
/// cleared. <c>NotePresented</c> reads <c>_lastNoPresentQpc > prev</c> ("did a no-present turn happen after the
/// previous present") instead of latching+clearing — race-free because the verdict depends only on the two longs'
/// real QPC values, not on which thread happened to read or write them first.</para>
///
/// <para>This gate proves it in four parts, real wall-clock idle included (a real <see cref="Thread.Sleep"/>, not a
/// scripted clock — <c>NotePresented</c> reads <c>Stopwatch.GetTimestamp()</c> directly, so only a real gap
/// exercises it): (a) candidates 1/2 are NOT happening — the notch dispatched this RunFrame call is drained by the
/// SAME call's kernel Tick and the viewport moves and presents on frame 0, no frame skipped between notch and
/// motion, and a real ~60ms idle gap on the far side of it is not charged; (b) a RunFrame turn that ran but ELIDED
/// its present (hole (i) above) does not get the NEXT present charged for the gap it sits in; (c) the async race
/// (hole (ii) above), driven explicitly through the <c>*ForTest</c> seam since headless never goes async and so
/// cannot produce the real thread race — an idle stamp, then a LATE <c>NotePresented</c> for the frame that was
/// already in flight when the idle exit ran, then the loop's own next idle re-check (nothing changed — it is still
/// the same idle stretch), then a real multi-refresh gap, then the wake present: not charged, unlike the old bool
/// (whose clear-on-consume step (ii) would drop exactly this re-arm); (d) two genuinely back-to-back LIVE presents
/// several refresh periods apart with NO no-present turn anywhere between them DO count the real misses — the
/// mechanism only excuses a gap that a no-present turn actually sat inside, never a gap the loop was live through.
/// </para></summary>
static class IdleWakePresentChecks
{
    internal static void Run(StringTable strings)
    {
        IdleToScroll(strings);
        ElidedTurnBetweenPresents(strings);
        AsyncRaceOrdering(strings);
        LiveGapStillCounted(strings);
    }

    private static void IdleToScroll(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("idle-wake-present", new Size2(240, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ScrollProbe());

        // Mount + drive the loop to genuine quiescence — the same "streak/idleAgo" deep-idle state the live [wake]
        // census showed before every glide (record-only frames, no reason to present). Every early-out RunFrame
        // takes (minimized / no-active-work / production-gate) hands back a SPARSE FrameStats that never sets
        // MissedVsyncs (it defaults to 0), so the real cumulative counter is tracked here from the last frame that
        // actually Presented — not from whatever RunFrame happens to return last.
        long missedBefore = 0;
        void NoteIfPresented(in FrameStats s) { if (s.Presented) missedBefore = s.MissedVsyncs; }
        NoteIfPresented(host.RunFrame());
        int settle = 0;
        for (; settle < 200 && host.HasActiveWork; settle++) NoteIfPresented(host.RunFrame());
        bool wentIdle = !host.HasActiveWork && settle < 200;
        // The settle loop above stops the instant HasActiveWork reads false WITHOUT running RunFrame in that idle
        // state — so it never actually took the "no active work" branch that stamps _lastNoPresentQpc. One more
        // call does exactly that: a genuinely idle RunFrame, no different from the thousands the real app ran while
        // parked (streak=9337 in the live [wake] census) before the wheel notch that follows. Its own (sparse)
        // return is irrelevant — missedBefore already holds the last REAL cumulative value.
        host.RunFrame();

        var vp = FindScrollNode(host.Scene, host.Scene.Root);
        bool foundViewport = !vp.IsNull;
        host.Scene.TryGetScroll(vp, out var before);

        // A real idle stretch — several refresh periods long — with NO RunFrame call in between, exactly like the
        // real app parked in its OS wait. Long enough that the OLD formula (gap / vsync - 1) would have charged
        // several missed vsyncs; short enough to keep the gate fast.
        Thread.Sleep(60);

        // The wheel notch that wakes the loop from idle — queued to the input ring, drained this SAME RunFrame call.
        window.QueueInput(WheelEvent(new Point2(100f, 100f), 0, 0, 3000f));
        var f = host.RunFrame();

        host.Scene.TryGetScroll(vp, out var after);
        bool movedOnFrame0 = after.OffsetY > before.OffsetY + 0.5f;   // candidates 1/2 ruled out: no skipped frame
        bool presentedOnFrame0 = f.Presented;
        bool noArtifactMiss = f.MissedVsyncs == missedBefore;         // candidate 3 fixed: idle gap not charged

        Check("gate.wake-present.idle-to-scroll a wheel notch arriving during deep idle is drained by the SAME frame's kernel Tick (viewport moves + presents on frame 0 — no skipped frame between notch and motion), and a real multi-refresh idle gap is NOT charged to FrameStats.MissedVsyncs on the frame that ends it",
            wentIdle && foundViewport && movedOnFrame0 && presentedOnFrame0 && noArtifactMiss,
            $"wentIdle={wentIdle}(settle={settle}) foundViewport={foundViewport} before={before.OffsetY:0.##} after={after.OffsetY:0.##} presented={presentedOnFrame0} missedBefore={missedBefore} missedAfter={f.MissedVsyncs}");
    }

    /// <summary>Hole (i): a turn that RAN (Paint executed — reconcile/layout/record all happened) but elided the
    /// present itself (skip-submit's byte-identical draw list is the dominant case; ProductionGateBlocks and the
    /// device-lost recovery bails are the same shape) must not leave the NEXT present charged for the gap it sits
    /// inside. Driven through the <c>*ForTest</c> seam directly against <c>NotePresented</c>'s accounting — the
    /// elision PATH itself (skip-submit's hash compare, the gate) is exercised by the ordinary render gates
    /// elsewhere; this gate is scoped to the missed-vsync bookkeeping the elision feeds.</summary>
    private static void ElidedTurnBetweenPresents(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("idle-wake-present-elide", new Size2(240, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ScrollProbe());

        host.NotePresentedForTest(1);   // baseline present — establishes _prevPresentedQpc
        long missedBefore = host.MissedVsyncsTotalForTest;

        // Three awake-but-nothing-to-show turns (mirrors a playback tick / diagnostics repaint that each ran the
        // pipeline and elided the present), spread across a span that would have been several refresh periods
        // under the OLD wall-clock-gap formula.
        for (int i = 0; i < 3; i++) { Thread.Sleep(20); host.NoteNoPresentTurnForTest(); }
        Thread.Sleep(20);

        host.NotePresentedForTest(2);   // the next real present, on the far side of the elided stretch
        long missedAfter = host.MissedVsyncsTotalForTest;

        Check("gate.wake-present.elided-turn a RunFrame/Paint turn that ran but elided its present (skip-submit / ProductionGateBlocks shape) is not charged to the NEXT present's missed-vsync count, even across a multi-refresh-period gap",
            missedAfter == missedBefore,
            $"missedBefore={missedBefore} missedAfter={missedAfter}");
    }

    /// <summary>Hole (ii): the async-present race. Sequence (matches the field doc's race exactly): the UI thread's
    /// idle exit stamps <c>_lastNoPresentQpc</c>; THEN the render thread finally runs <c>NotePresented</c> for a
    /// frame that was already in flight when the idle exit ran (a "late" present, itself correctly excused since
    /// the stamp predates it); THEN the UI loop's own next idle re-check runs (same idle stretch — still nothing to
    /// do) and re-stamps; THEN a real multi-refresh gap; THEN the wake present. The OLD bool got exactly this
    /// wrong: its clear-on-consume step means the late NotePresented in the middle erases the flag, so a SUBSEQUENT
    /// idle re-check has to re-arm it before the wake present runs, or the wake gets charged — and if that re-check
    /// and the late NotePresented ever raced at the memory level (the scenario this fix removes), the re-arm could
    /// be the one that gets lost. The timestamp has nothing to lose: each write just needs a later QPC than the
    /// present it needs to excuse, and there is no clear step for a race to land on either side of.</summary>
    private static void AsyncRaceOrdering(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("idle-wake-present-race", new Size2(240, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ScrollProbe());

        host.NotePresentedForTest(1);   // T0: baseline present, well before anything below
        long missedBefore = host.MissedVsyncsTotalForTest;
        Thread.Sleep(5);

        host.NoteNoPresentTurnForTest();      // UI idle-exit stamp
        Thread.Sleep(5);
        host.NotePresentedForTest(2);         // late NotePresented of the previously in-flight frame — excused
        Thread.Sleep(5);
        host.NoteNoPresentTurnForTest();      // the loop's own next idle re-check — same idle stretch, re-stamps
        Thread.Sleep(60);                     // a real multi-refresh idle gap, same shape as the idle-to-scroll gate
        host.NotePresentedForTest(3);         // the wake present

        long missedAfter = host.MissedVsyncsTotalForTest;
        Check("gate.wake-present.async-race an idle stamp followed by a LATE NotePresented for an already-in-flight frame, the loop's own next idle re-check, a real idle gap, then the wake present — the wake present is not charged (the ordering the old consume-and-clear bool got wrong)",
            missedAfter == missedBefore,
            $"missedBefore={missedBefore} missedAfter={missedAfter}");
    }

    /// <summary>The subtlety every hole above must not swallow: two genuinely LIVE, back-to-back presents with NO
    /// no-present turn anywhere between them must still have their real gap counted. Three refresh periods
    /// (default 60 Hz ≈ 16.667 ms each ⇒ ~50 ms) with nothing stamped in between must charge at least 2 missed vsyncs
    /// (Thread.Sleep only guarantees a MINIMUM, so a loaded machine legitimately counts more) —
    /// proving the mechanism excuses a gap only when a no-present turn actually happened inside it, never merely
    /// because the loop was quiet for a while.</summary>
    private static void LiveGapStillCounted(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("idle-wake-present-livegap", new Size2(240, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ScrollProbe());

        host.NotePresentedForTest(1);            // A: nothing stamped before or after it
        long missedBefore = host.MissedVsyncsTotalForTest;
        Thread.Sleep(50);                        // ~3 default (60 Hz) refresh periods, no no-present turn in between
        host.NotePresentedForTest(2);             // B: a genuinely live present 3 periods after A
        long missedAfter = host.MissedVsyncsTotalForTest;

        Check("gate.wake-present.live-gap-counted two back-to-back live presents ~3 refresh periods apart with no no-present turn between them DO count the real misses (≥ 2), unlike an idle/elided gap",
            missedAfter - missedBefore >= 2,
            $"missedBefore={missedBefore} missedAfter={missedAfter} delta={missedAfter - missedBefore}");
    }
}
