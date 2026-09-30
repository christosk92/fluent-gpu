using System;

namespace FluentGpu.Hosting.Threading;

/// <summary>Decides what a CLOCK-PACED render turn does when the present slot is not free shortly after its tick.
/// A busy slot there means the previous present missed its vblank: the semaphore re-signals only after the NEXT vblank,
/// so waiting would present this tick's frame one vblank late — and every later turn inherits that phase (measured
/// 2026-09-29: runs of 55–86 turns one tick behind, frames costing 1–3 ms). While the frames FIT the early phase the turn
/// skips instead (the queued frame is this vblank's frame) and the next tick presents on time — Chromium viz's "swap
/// throttled" rule. Frames that do not fit keep today's wait (skipping them would halve the rate; the present-queue depth
/// policy owns that regime). A catch-up that does not hold backs off.
/// <para>Pure value, zero allocation, owned by the render thread (<c>RenderThread._catchUp</c>); the render thread feeds
/// it every paced present (<see cref="Observe"/>), asks it on a busy slot (<see cref="ShouldSkip"/>) and clears its phase
/// history when render motion ends (<see cref="Break"/>, alongside <see cref="MotionTickRun.Break"/>).</para></summary>
public struct SlotCatchUp
{
    /// <summary>How long a paced turn waits for the slot before calling it busy, as a fraction of the refresh. The retire
    /// lands 0.2–0.7 ms after the vblank (09-29 captures); 0.15·8.33 ms rounds up to 2 ms.</summary>
    public const double GraceFraction = 0.15;
    /// <summary>A frame (render work + GPU) must cost at most this share of the refresh for the early phase to hold — the
    /// rest is the early-phase wake (~0.1 ms) and DWM's latch margin. Below PresentQueueDepthPolicy.EngageFraction (0.8)
    /// so a GPU-bound frame reaches the depth policy before this one gives up on it.</summary>
    public const double FitFraction = 0.70;
    /// <summary>A catch-up followed by a busy slot within this many ticks did not hold.</summary>
    public const int HoldTicks = 2;
    /// <summary>Ticks without catch-up after one that did not hold (1 s at 120 Hz).</summary>
    public const int BackoffTicks = 120;
    private const double Alpha = 0.2;

    private double _costEmaMs;
    private bool _seeded;
    private long _lastCatchUpTick, _backoffUntilTick;

    /// <summary>The smoothed per-frame cost (render work + GPU, ms) the fit test compares against the refresh; 0 until the
    /// first paced present was observed.</summary>
    public readonly double CostEmaMs => _costEmaMs;

    /// <summary>A catch-up on paced tick <paramref name="tickSeq"/> would be refused because an earlier one did not hold.</summary>
    public readonly bool BackingOff(long tickSeq) => tickSeq < _backoffUntilTick;

    /// <summary>The bounded slot-take timeout of a paced turn: <see cref="GraceFraction"/> of the refresh, rounded UP to
    /// whole milliseconds (the wait API's unit), never below 1 ms.</summary>
    public static int GraceMs(double refreshMs) => Math.Max(1, (int)Math.Ceiling(refreshMs * GraceFraction));

    /// <summary>One presented paced turn: its render work (slot open → present returned) and the latest retired GPU
    /// execution time, ms. The turn's WAKE LAG is deliberately not cost: in the late phase each turn starts late because
    /// the previous one ran past the tick, so the wake lag IS the lateness — counting it made the policy judge frames
    /// "over budget" exactly while it was needed (live 2026-09-29: costEma 7.6–10.9 ms with GPU ≈ 2.3 ms, slot waits ~5.5 ms,
    /// no catch-up), a trap that fed itself. What the frame costs is what it costs from the moment it can start.</summary>
    public void Observe(double workMs, double gpuMs)
    {
        double cost = workMs + gpuMs;
        _costEmaMs = _seeded ? _costEmaMs + Alpha * (cost - _costEmaMs) : cost;
        _seeded = true;
    }

    /// <summary>The slot was busy past the grace on paced tick <paramref name="tickSeq"/>: true ⇒ skip this tick.</summary>
    public bool ShouldSkip(long tickSeq, double refreshMs)
    {
        if (tickSeq == 0 || tickSeq < _backoffUntilTick) return false;
        if (!_seeded || _costEmaMs > FitFraction * refreshMs) return false;
        if (_lastCatchUpTick != 0 && tickSeq - _lastCatchUpTick <= HoldTicks)
        {
            _backoffUntilTick = tickSeq + BackoffTicks;   // the early phase did not hold: stop trying for a while
            return false;
        }
        _lastCatchUpTick = tickSeq;
        return true;
    }

    /// <summary>Motion ended: forget the phase history (the next run starts clean; the cost EMA is kept).</summary>
    public void Break() { _lastCatchUpTick = 0; _backoffUntilTick = 0; }
}
