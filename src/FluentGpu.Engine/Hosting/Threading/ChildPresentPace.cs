using System;
using System.Diagnostics;
using System.Globalization;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Present-pacing evidence of ONE detached child (a video pop-out) - the child's half of the <c>[render.pace]</c> line.
/// Pacing is per target: the parent's counters, slot waits and lags describe the PRIMARY swapchain alone, and a child's
/// presents (they ride the parent's render thread through <c>extraDrain</c>) used to leave no trace at all, which is how a
/// child's slot stall stayed attributable only indirectly. Every figure here is the child's OWN: its non-blocking slot
/// probe (<see cref="NoteSlotTake"/>; a probe that found the slot busy is <see cref="Deferred"/> - the child never waits, the
/// shared thread must not block on a secondary window's vblank), its real presents with the worst turn-to-present lag and the
/// longest present work, the presents DXGI refused under the non-blocking secondary present (<see cref="Skipped"/>), and the
/// one-time attach line's target id (<see cref="Target"/>).
/// <para>Render thread only (writes and the 1 Hz window read), no lock; the cumulative totals are plain longs a test reads
/// after the thread joined. Zero allocation except <see cref="Describe"/>, which runs on the 1 Hz report path.</para>
/// </summary>
internal sealed class ChildPresentPace
{
    /// <summary>The process-wide id the attach line, the ledger rows (<see cref="PresentLedger.RecordChild"/>) and the pace
    /// line name this child by (1, 2, ...; 0 is the primary window).</summary>
    public int Target { get; }

    public ChildPresentPace(int target) => Target = target;

    private long _presents, _deferred, _skipped;       // cumulative
    private long _presents0, _deferred0, _skipped0;    // their values when the report window opened
    private long _slotSumQpc, _slotMaxQpc, _slotCount; // window
    private long _lagMaxQpc, _workMaxQpc;              // window

    /// <summary>Real presents of this child's swapchain (cumulative).</summary>
    public long Presents => _presents;

    /// <summary>Turns whose slot probe found the child's present slot busy (cumulative): its publication stayed pending for the
    /// next turn instead of blocking the shared render thread.</summary>
    public long Deferred => _deferred;

    /// <summary>Presents DXGI refused with <c>DXGI_ERROR_WAS_STILL_DRAWING</c> (cumulative; only under the non-blocking
    /// secondary present, <c>EngineSwitches.NonBlockingSecondaryPresent</c>): the frame stayed owed and was re-presented on a
    /// later turn instead of blocking the shared render thread inside Present.</summary>
    public long Skipped => _skipped;

    /// <summary>A non-blocking present of this child was refused (the queue was still full): nothing was queued.</summary>
    public void NoteSkipped() => _skipped++;

    /// <summary>One slot probe of this child: how long the take took (QPC ticks) and whether the slot was open.</summary>
    public void NoteSlotTake(long waitQpc, bool opened)
    {
        _slotCount++;
        _slotSumQpc += waitQpc;
        if (waitQpc > _slotMaxQpc) _slotMaxQpc = waitQpc;
        if (!opened) _deferred++;
    }

    /// <summary>One real present: the turn-to-present lag (a publication that sat deferred behind a busy slot counts from the
    /// first deferral) and the present's own work (slot open to Present returned), QPC ticks.</summary>
    public void NotePresent(long lagQpc, long workQpc)
    {
        _presents++;
        if (lagQpc > _lagMaxQpc) _lagMaxQpc = lagQpc;
        if (workQpc > _workMaxQpc) _workMaxQpc = workQpc;
    }

    /// <summary>A report window opens: rebase the window figures on the cumulative totals.</summary>
    public void BeginWindow()
    {
        _presents0 = _presents; _deferred0 = _deferred; _skipped0 = _skipped;
        _slotSumQpc = _slotMaxQpc = _slotCount = 0;
        _lagMaxQpc = _workMaxQpc = 0;
    }

    /// <summary>This window's figures as one pace-line token, or null when the child neither presented, was deferred nor was
    /// refused a present in it (an idle child adds nothing to the line).</summary>
    public string? Describe()
    {
        long presents = _presents - _presents0, deferred = _deferred - _deferred0, skipped = _skipped - _skipped0;
        if (presents == 0 && deferred == 0 && skipped == 0) return null;
        double toMs = 1000.0 / Stopwatch.Frequency;
        double slotAvg = _slotCount == 0 ? 0 : _slotSumQpc * toMs / _slotCount;
        return string.Create(CultureInfo.InvariantCulture,
            $"t{Target}(presents={presents} deferred={deferred} skipped={skipped} slotWaitAvg={slotAvg:F2} slotWaitMax={_slotMaxQpc * toMs:F2} lagMax={_lagMaxQpc * toMs:F2} workMax={_workMaxQpc * toMs:F2})");
    }
}
