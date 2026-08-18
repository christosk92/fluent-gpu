using System.Threading;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Present-thread attestation that <c>Present()</c> is not vsync-locked, so the UI loop must software-pace instead of
/// treating the present-ack (or a compositor clock that returns immediately) as a display phase reference.
///
/// <b>The failure.</b> On a real panel the swapchain's frame-latency waitable blocks ~one refresh before Present
/// returns, so acks land 8–16 ms apart and the UI can sleep on them. Remote sessions (RDP, Shadow, some VMs) often
/// have no vblank: DXGI interval-1 returns in microseconds, DWM's compositor clock succeeds without waiting, and the
/// loop free-runs at hundreds of FPS — a core of CPU for pixels the stream encoder will subsample to 60 Hz anyway.
/// About's FPS counter is the UI-loop rate (<c>UpdateFrameTiming</c>), which is how 400+ fps shows up with Task
/// Manager GPU still at 0%.
///
/// <b>The signal.</b> Two conditions, BOTH required to engage: the waitable's block time
/// (<c>PresentStats.LatencyWaitMs</c>) is sub-millisecond AND consecutive presents complete FASTER than a refresh
/// period (interval &lt; half a refresh). A vsync-locked swapchain can never complete two presents closer than one
/// refresh apart, so the interval condition is what makes this an attestation of "no vblank" rather than of "the UI
/// happens to be producing slower than the display" — the latter also drives the latency wait to ~0 (the queue is
/// never full), and floored the loop to 60 Hz on a real 120 Hz panel whenever a heavy page fill slowed production
/// (a self-sustaining latch: once floored, production stays slower than refresh and the wait stays ~0). The first
/// latency wait of 4 ms or more disengages (a genuine vsync block was observed). Stats that are not Valid classify
/// nothing — headless and the first DXGI/DWM samples must not trip a live 120 Hz path.
///
/// <b>Threading.</b> Mutated on the present thread; the UI thread only ever reads <see cref="Unpaced"/> through a
/// volatile load. Same one-writer/one-reader shape as <see cref="PresentSlipDetector"/>.
/// </summary>
internal sealed class PresentUnpacedDetector
{
    /// <summary>Consecutive sub-millisecond latency waits required to engage. ~20 ms at 400 fps — long enough that a
    /// single already-signalled waitable (the first frame after a skip-submit stretch) cannot trip a real panel.</summary>
    private const int EngageStreak = 8;
    private const double UnpacedWaitMs = 1.0;
    private const double PacedWaitMs = 4.0;

    private int _streak;
    private int _engaged;

    /// <summary>Presents landing closer than this fraction of a refresh period apart are impossible under vblank lock.</summary>
    private const double FreeRunIntervalFraction = 0.5;

    /// <summary>Present thread. Called once per real present after <c>SamplePresentStats</c> has published this
    /// frame's latency wait. <paramref name="presentIntervalMs"/> is the gap since the previous present (≤ 0 = unknown,
    /// classifies nothing); <paramref name="refreshMs"/> the panel refresh period.</summary>
    public void OnPresent(bool statsValid, double latencyWaitMs, double presentIntervalMs, double refreshMs)
    {
        if (!statsValid) return;
        if (latencyWaitMs >= PacedWaitMs)
        {
            _streak = 0;
            if (Volatile.Read(ref _engaged) != 0) Volatile.Write(ref _engaged, 0);
            return;
        }
        bool freeRunning = presentIntervalMs > 0.0 && refreshMs > 0.0 && presentIntervalMs < refreshMs * FreeRunIntervalFraction;
        if (latencyWaitMs < UnpacedWaitMs && freeRunning)
        {
            if (_streak < EngageStreak) _streak++;
            if (_streak >= EngageStreak && Volatile.Read(ref _engaged) == 0)
                Volatile.Write(ref _engaged, 1);
            return;
        }
        // A short wait with vblank-spaced presents (production slower than refresh), or an in-between wait: neither a
        // free-run nor a recovered vsync. Hold state, do not grow the streak.
        _streak = 0;
    }

    /// <summary>UI thread. True while Present is attested unpaced — software 60 Hz floor, no ack/compositor-clock
    /// wake as a phase reference.</summary>
    public bool Unpaced => Volatile.Read(ref _engaged) != 0;

    /// <summary>Test seam: consecutive unpaced waits seen so far.</summary>
    internal int StreakForTest => Volatile.Read(ref _streak);
}
