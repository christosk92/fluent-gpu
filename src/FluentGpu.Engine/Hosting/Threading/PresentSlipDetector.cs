using System.Threading;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Present-thread attestation that the present chain has fallen into the <b>60 Hz phase-lock attractor</b>, so the UI
/// loop can re-anchor itself to the compositor clock (design/subsystems/threading-render-seam.md §11.1.4).
///
/// <b>The attractor.</b> <see cref="DisplayPhaseGate"/> gives the UI loop a phase reference — the render thread's
/// present-ack — and while the gate is armed the loop sleeps toward <c>PhaseGateCeilingMs()</c> and wakes ONLY on that
/// ack. On a 120 Hz panel the ceiling rounds to 17 ms and a healthy ack interval is 8.33 ms, so the ceiling is dead
/// backstop and the ack alone paces production. That is exactly right until one vblank slips. After a single slipped
/// slot the render thread's frame-latency waitable settles one refresh later, acks start landing 16.67 ms apart — still
/// UNDER the 17 ms ceiling, so <see cref="DisplayPhaseGate.CeilingEscapes"/> structurally never fires — production
/// follows the acks at 60 Hz, and the next present is therefore also 16.67 ms late. Ack-paced production plus a ceiling
/// that never trips is a stable fixed point: measured live (<c>ops/diag/sessions/live-20260804-073148</c>) it held for
/// minutes on a 120 Hz panel, latW pinned near 15.9 ms with DXGI attesting exactly one missed vsync per present.
///
/// Nothing inside the gate can see this. The gate only knows "a publish is owed a present", and in the locked state
/// every publish IS presented — one refresh late, forever. The one place the signature is visible is the present site
/// itself: the interval between consecutive presents. This type classifies that interval.
///
/// <b>The bands and the hysteresis.</b> An interval is <i>healthy</i> below 1.5R, a <i>one-slip</i> in [1.5R, 2.5R),
/// and <i>other</i> at or above 2.5R. Only a sustained run of one-slips engages: three consecutive (~25 ms at 120 Hz)
/// is long enough that a single scheduling hiccup or a one-off compositor stall cannot trip it, and short enough that
/// the lock is broken before it is perceptible. Disengaging is asymmetric on purpose — the FIRST healthy interval
/// disengages, because the escape has then done its job and holding the compositor tick armed past that point is pure
/// busywork. Intervals at or above 2.5R never engage and reset an in-progress streak: those are idle gaps, occlusion,
/// a minimized window, or a GPU that genuinely cannot hold the panel rate. None of them is a phase problem, and forcing
/// a deliberately-slow loop to panel rate is the one way this could make things worse.
///
/// <b>Threading.</b> Every mutation happens on the thread that presents (the render thread under the async default);
/// the UI thread only ever READS <see cref="RephaseWanted"/> and <see cref="Episode"/> through volatile loads. That is
/// the same one-writer/one-reader shape as <see cref="DisplayPhaseGate"/>'s ack handshake, minus the handshake: nothing
/// here is a wake, so there is no StoreLoad pair to fence and no lost-wake hazard — a read that lands one present stale
/// costs at most one frame of delay in engaging or disengaging.
///
/// Zero allocation after construction: every field is a primitive, time is INJECTED by the caller (the same discipline
/// as <see cref="DisplayPhaseGate"/> and <c>DmManualUpdatePacer</c>), and nothing here reads a clock.
/// </summary>
internal sealed class PresentSlipDetector
{
    /// <summary>Consecutive one-slip intervals required to engage. ~25 ms at 120 Hz.</summary>
    private const int EngageStreak = 3;

    private long _prevQpc;      // present-thread only; 0 = no present seen yet
    private int _slipStreak;    // present-thread only
    private int _engaged;       // 1 while a sustained one-vblank slip is attested; UI thread reads this
    private int _episode;       // bumped on each engage, so a reader can tell one lock episode from the next

    /// <summary>Present thread. Called once per real present with the stamp taken immediately after <c>Present()</c>
    /// returned and the panel's measured refresh period in the same clock domain.</summary>
    /// <param name="nowQpc">Monotonic stamp of this present.</param>
    /// <param name="refreshQpc">Measured refresh period in the same ticks. Non-positive ⇒ classify nothing.</param>
    public void OnPresent(long nowQpc, long refreshQpc)
    {
        long prev = _prevQpc;
        _prevQpc = nowQpc;
        // No refresh period, no predecessor, or a non-monotonic stamp ⇒ there is no interval to classify. Stamp and
        // leave the state alone: a garbage sample must neither engage nor destroy an in-progress streak.
        if (refreshQpc <= 0 || prev == 0 || nowQpc <= prev) return;

        long d = nowQpc - prev;
        if (2 * d < 3 * refreshQpc)
        {
            // Healthy (< 1.5R). The chain is on the panel's phase; one such interval is enough to stand down.
            _slipStreak = 0;
            if (Volatile.Read(ref _engaged) != 0) Volatile.Write(ref _engaged, 0);
            return;
        }
        if (2 * d < 5 * refreshQpc)
        {
            // One slipped slot ([1.5R, 2.5R)) — the signature of the attractor. Engage only on a sustained run.
            if (_slipStreak < EngageStreak) _slipStreak++;
            if (_slipStreak >= EngageStreak && Volatile.Read(ref _engaged) == 0)
            {
                // Episode BEFORE engaged: a reader that samples the episode first and the flag second must never see
                // a fresh engage carrying the previous episode's ordinal (it would spend a stale escape budget).
                Volatile.Write(ref _episode, _episode + 1);
                Volatile.Write(ref _engaged, 1);
            }
            return;
        }
        // >= 2.5R: an idle gap, an occluded/minimized window, or a GPU that cannot hold the rate. Never a phase
        // problem, and never something to fix by forcing the loop to panel rate.
        _slipStreak = 0;
        if (Volatile.Read(ref _engaged) != 0) Volatile.Write(ref _engaged, 0);
    }

    /// <summary>UI thread. True while the present thread attests a sustained one-vblank slip, i.e. while the loop should
    /// take the compositor clock as its phase reference instead of the ack it is locked to.</summary>
    public bool RephaseWanted => Volatile.Read(ref _engaged) != 0;

    /// <summary>Monotonic ordinal of the current engage. A consumer that budgets work per lock episode compares this
    /// against what it last saw; a change means a NEW lock, not a continuation of the one it was already paying for.</summary>
    public int Episode => Volatile.Read(ref _episode);

    /// <summary>Test seam: consecutive one-slip intervals seen so far (mirrors <see cref="DisplayPhaseGate"/>'s
    /// <c>SinceTicksForTest</c> — the hysteresis is only observable from inside).</summary>
    internal int SlipStreakForTest => Volatile.Read(ref _slipStreak);
}
