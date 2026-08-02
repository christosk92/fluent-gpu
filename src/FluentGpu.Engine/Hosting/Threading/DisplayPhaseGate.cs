using System;
using System.Threading;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Production backpressure that keeps the UI loop in phase with the display: never produce a frame while a published
/// one is still unpresented (design/subsystems/threading-render-seam.md §11.1).
///
/// DropOldest makes over-production SAFE; it does not make it free, and the cost is temporal aliasing rather than
/// wasted work. Moving <c>Present()</c> to the render thread removed the UI thread's only vsync reference — in the
/// sync path the present block itself paced the loop — and a wall-clock cap can bound how OFTEN the loop produces but
/// never WHEN. Gating on the render thread's present-ack restores the reference, because the render loop blocks in the
/// swapchain's frame-latency waitable before presenting, so its acks land one refresh apart.
///
/// <b>The arm-then-recheck handshake, and why the fence is load-bearing.</b> The obvious form — read the ack, then arm
/// — loses wakes: a present landing between the two sees "not armed", skips the wake, and the UI sleeps to the stall
/// ceiling. Measured on the first implementation: ~16% of frames were produced about one refresh late, a distinct
/// secondary mode in the clock-skew distribution.
///
/// The fix is to arm FIRST and then re-read, but that is the classic StoreLoad (Dekker) shape — this side stores
/// <c>armed</c> then loads <c>ack</c>, while the render thread stores <c>ack</c> then loads <c>armed</c>. A
/// release-store followed by an acquire-load does NOT order StoreLoad on x86 or ARM, so without a FULL barrier on each
/// side both threads can still miss each other's write and the wake is lost exactly as before. Hence
/// <see cref="Interlocked.Exchange(ref int, int)"/> here (full fence, not merely a release) and an explicit
/// <see cref="Thread.MemoryBarrier"/> on the render side before it reads <see cref="IsArmed"/>. On ARM64 this is not
/// a theoretical concern.
///
/// Zero per-frame allocation: the ack is read through a delegate captured once at construction, and every field is a
/// primitive. Time is INJECTED rather than read from the clock so the stall ceiling is deterministically testable
/// (the same discipline as <c>DmManualUpdatePacer</c>).
/// </summary>
internal sealed class DisplayPhaseGate
{
    private readonly Func<ulong> _readAck;
    private int _armed;            // 1 while the UI is parked and the render thread must deliver a wake
    private bool _inStretch;       // a gated stretch is open; separate from _sinceTicks because 0 is a LEGAL stamp
    private long _sinceTicks;      // start of the current gated stretch, for the stall ceiling
    private long _gatedFrames;
    private long _ceilingEscapes;  // times the two-refresh liveness ceiling opened the gate (must be reported, never silent)

    /// <param name="readAck">Reads the render thread's present-ack (the publish seq it last presented). Invoked twice
    /// per gated decision — the second read is the recheck — so it must be cheap and side-effect free.</param>
    public DisplayPhaseGate(Func<ulong> readAck) => _readAck = readAck ?? throw new ArgumentNullException(nameof(readAck));

    /// <summary>True while the UI is parked on the gate. Read by the render thread's present callback to decide whether
    /// a wake is owed; it must issue a full barrier after publishing its ack and before reading this, or the handshake
    /// described on the type is broken.</summary>
    public bool IsArmed => Volatile.Read(ref _armed) != 0;

    /// <summary>Frames declined so far (diagnostic). Each is a frame DropOldest was about to discard.</summary>
    public long GatedFrames => Volatile.Read(ref _gatedFrames);

    /// <summary>Times the stall ceiling opened the gate because present-ack never arrived in time. The two-refresh
    /// escape stays for liveness, but every escape is an explicit signal — never a silent smoothness win.</summary>
    public long CeilingEscapes => Volatile.Read(ref _ceilingEscapes);

    /// <summary>UI thread. True ⇒ decline to produce this frame.</summary>
    /// <param name="publishSeq">Frames handed to the seam so far.</param>
    /// <param name="nowTicks">Caller's monotonic stamp (same domain as <paramref name="ceilingTicks"/>).</param>
    /// <param name="ceilingTicks">Stall ceiling. Past this the gate opens regardless: it must be an optimization, never
    /// a liveness dependency — an occluded, stalled or device-lost render thread stops acking and the loop still has to
    /// run input, timers and recovery.</param>
    public bool Blocks(ulong publishSeq, long nowTicks, long ceilingTicks)
    {
        if (publishSeq <= _readAck()) return Open(ceilingEscape: false);

        if (!_inStretch)
        {
            _inStretch = true;
            _sinceTicks = nowTicks;
            // Full fence, NOT Volatile.Write — see the type comment. Everything after this is ordered against the
            // render thread's symmetric barrier, which is what makes the recheck below authoritative.
            Interlocked.Exchange(ref _armed, 1);
            // Recheck: if the ack moved while we were arming, WE own the slot and no wake is coming (the callback may
            // already have run and seen us unarmed). Open immediately rather than sleeping to the ceiling.
            if (publishSeq <= _readAck()) return Open(ceilingEscape: false);
            _gatedFrames++;
            return true;
        }

        // Already armed from an earlier call in this stretch; the ack was re-read at the top, so only the ceiling is
        // left to check.
        if (nowTicks - _sinceTicks >= ceilingTicks) return Open(ceilingEscape: true);
        _gatedFrames++;
        return true;
    }

    /// <summary>UI thread. Arm the gate at PUBLISH time rather than waiting for the next <see cref="Blocks"/> poll, and
    /// report whether the frame just published is still owed a present.
    ///
    /// <b>Why this exists.</b> <see cref="Blocks"/> runs at the TOP of a frame, before the frame is produced; a producing
    /// cycle therefore left the gate DISARMED for its entire duration (record + layout + publish) and only re-armed on
    /// the next poll. The render thread's present-ack callback elides its wake whenever the gate is unarmed — so on
    /// exactly the frames that produced content, no wake was owed and the loop fell back to the wall-clock pace timer.
    /// A wall-clock cap can bound how OFTEN the loop produces but never WHEN, which is the whole phase problem: measured
    /// ~7.9 ms production against an 8.333 ms grid ⇒ ~4% of slots slip ⇒ 115 fps on a 120 Hz panel. Arming here closes
    /// the window: the ack that lands for THIS publish finds the gate armed and delivers the wake, so the next cycle
    /// starts on the display's phase.
    ///
    /// The handshake is the same fenced arm-then-recheck as <see cref="Blocks"/>, for the same reason — see the type
    /// comment. Arming without the recheck would lose the wake to a present that lands between the two operations.
    ///
    /// <b>Never counts.</b> <see cref="GatedFrames"/> is the census of frames DECLINED (frames DropOldest was about to
    /// discard). A publish is a frame that WAS produced, so this must not inflate it or the census stops meaning what
    /// the pacing argument uses it for. The stretch it opens is shared with <see cref="Blocks"/>: a subsequent poll
    /// inside the same stretch measures the stall ceiling from the PUBLISH stamp, which is the correct origin (the
    /// present is owed from the moment the frame was handed over, not from the next poll).</summary>
    /// <param name="publishSeq">The seq just returned by PUBLISH — the frame whose present is now owed.</param>
    /// <param name="nowTicks">Caller's monotonic stamp; becomes the stretch origin for the stall ceiling.</param>
    /// <returns>True when a present is still owed for <paramref name="publishSeq"/> (the gate is now armed).</returns>
    public bool ArmAtPublish(ulong publishSeq, long nowTicks)
    {
        if (publishSeq <= _readAck()) return Open(ceilingEscape: false);

        if (!_inStretch)
        {
            _inStretch = true;
            _sinceTicks = nowTicks;
            // Full fence, NOT Volatile.Write — identical ordering requirement to Blocks(); the recheck below is only
            // authoritative because of it.
            Interlocked.Exchange(ref _armed, 1);
            if (publishSeq <= _readAck()) return Open(ceilingEscape: false);
            return true;
        }

        // Already armed inside an open stretch (Blocks gated, then the ceiling escape produced this frame): the arm and
        // the stretch origin both stand. The ceiling is checked by Blocks alone — publishing must never open the gate.
        return true;
    }

    /// <summary>Open the gate and disarm. Idempotent; safe to call when already open.</summary>
    public bool Open() => Open(ceilingEscape: false);

    private bool Open(bool ceilingEscape)
    {
        if (_inStretch)
        {
            _inStretch = false;
            _sinceTicks = 0;
            Interlocked.Exchange(ref _armed, 0);
            if (ceilingEscape) _ceilingEscapes++;
        }
        else if (Volatile.Read(ref _armed) != 0)
        {
            // Defensive: armed without an open stretch should be unreachable, but leaving a stale arm would make the
            // render thread post wakes at a loop that is not waiting.
            Interlocked.Exchange(ref _armed, 0);
        }
        return false;
    }

    /// <summary>Test/diagnostic seam: the tick stamp at which the current gated stretch began (0 when open).</summary>
    internal long SinceTicksForTest => _inStretch ? _sinceTicks : 0;
}
