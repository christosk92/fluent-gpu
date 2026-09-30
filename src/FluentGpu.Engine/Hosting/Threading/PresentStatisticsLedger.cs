using System.Runtime.CompilerServices;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Differences DXGI frame statistics (PresentStats.PresentCount / PresentRefreshCount) into the attested present ledger.
/// <para><b>Only DXGI's PAIRED counters are compared.</b> One <c>GetFrameStatistics</c> sample describes the last
/// DISPLAYED present — its <c>PresentCount</c> (present id) and <c>PresentRefreshCount</c> (the vblank it was shown at)
/// belong to the same present — and it lags the submit by about one flip (a sample taken right after <c>Present</c> often
/// still describes the previous one). Comparing that lagging count against the engine's own submitted count per sample
/// turned every stale sample into a permanent "drop" (the catch-up's negative delta was clamped away). Between two
/// samples: Dropped = max(0, ΔPresentCount − ΔPresentRefreshCount) — presents genuinely superseded before a vblank
/// (distinct present ids can never be shown on fewer vblanks otherwise); Displayed = ΔPresentCount − Dropped;
/// Repeated = max(0, ΔPresentRefreshCount − ΔPresentCount − idle) — vblanks that showed a stale frame although its
/// successor had been submitted in time (a late compositor latch).</para>
/// <para><b>Idle is not a repeat.</b> <c>idleRefreshes</c> is the number of vblanks between a present's submit and the
/// previous submit beyond one — refreshes where nothing newer existed: the app was idle, or its producer skipped a tick
/// (that is the render thread's own missed-tick counter, never a display repeat). It is banked by present id and
/// credited when the sample that retires that present arrives (it may come a flip or more after the submit), so a
/// lagging sample cannot charge the idle vblanks to a later present. A present superseded before display forwards its
/// credit with the present that replaced it (both ids retire in the same sample).</para>
/// Counters are uint and wrap; a DISJOINT sample (<c>valid == false</c>) or one taken before the first present was
/// displayed (<c>PresentRefreshCount</c> 0) resets the baseline and counts nothing. Zero allocation: a fixed 16-entry credit ring, inline in the struct.
/// </summary>
public struct PresentStatisticsLedger
{
    private const int CreditCapacity = 16;   // far beyond any present-queue depth: an id this old is long retired
    private const int CreditMask = CreditCapacity - 1;

    [InlineArray(CreditCapacity)]
    private struct CreditRing { private Credit _e0; }

    private struct Credit { public uint Id; public uint Idle; public bool Set; }

    private CreditRing _credits;
    private uint _lastPresentCount, _lastRefreshCount; private bool _baselined;
    public long PresentsDisplayed, PresentsDropped, VblanksRepeated;

    /// <summary>One submitted present: <paramref name="lastPresentId"/> is its id (<c>GetLastPresentCount</c> after the
    /// <c>Present</c> — the <c>PresentCount</c> domain), <paramref name="idleRefreshes"/> the vblanks between its submit
    /// and the previous submit beyond one; <paramref name="presentCount"/>/<paramref name="presentRefreshCount"/> are the
    /// frame-statistics sample taken right after it (<paramref name="valid"/> false = DISJOINT / unavailable).</summary>
    public void Observe(bool valid, uint presentCount, uint presentRefreshCount, uint lastPresentId, uint idleRefreshes)
    {
        ref Credit slot = ref _credits[(int)(lastPresentId & CreditMask)];
        slot.Id = lastPresentId; slot.Idle = idleRefreshes; slot.Set = true;

        // A sample from before anything reached the glass reads PresentRefreshCount 0: it is no baseline — differencing
        // the first real sample against it would charge every vblank since boot as a repeat.
        if (!valid || presentRefreshCount == 0) { _baselined = false; return; }
        if (_baselined)
        {
            uint dDisplayed = unchecked(presentCount - _lastPresentCount);
            uint dRefresh = unchecked(presentRefreshCount - _lastRefreshCount);
            // A stale sample (the same last-displayed present) retires nothing; a backwards step counts nothing.
            if (dDisplayed != 0 && dDisplayed < 0x8000_0000u && dRefresh < 0x8000_0000u)
            {
                long idle = TakeCredit(_lastPresentCount, presentCount);
                long dropped = dDisplayed > dRefresh ? dDisplayed - dRefresh : 0;
                long excess = (long)dRefresh - dDisplayed - idle;
                PresentsDisplayed += dDisplayed - dropped;
                PresentsDropped += dropped;
                if (excess > 0) VblanksRepeated += excess;
            }
        }
        _lastPresentCount = presentCount; _lastRefreshCount = presentRefreshCount; _baselined = true;
    }

    /// <summary>The <c>idleRefreshes</c> argument for a present submitted at <paramref name="submitQpc"/> whose
    /// predecessor was submitted at <paramref name="previousSubmitQpc"/>: the vblank boundaries crossed between the two
    /// submits, minus the one a steady cadence crosses. With a vblank anchor (<paramref name="vblankQpc"/> — any vblank
    /// time on the same grid, e.g. DXGI <c>SyncQPCTime</c>) the boundaries are counted on the grid, so a submit early in
    /// its interval and the next one late in the following interval is still one refresh; without one the gap is rounded
    /// to whole periods. 0 for the first submit, a non-positive gap or an unknown period.</summary>
    public static uint IdleRefreshes(long previousSubmitQpc, long submitQpc, long vblankQpc, long periodQpc)
    {
        if (previousSubmitQpc == 0 || periodQpc <= 0 || submitQpc <= previousSubmitQpc) return 0;
        long crossed = vblankQpc != 0
            ? FloorDiv(submitQpc - vblankQpc, periodQpc) - FloorDiv(previousSubmitQpc - vblankQpc, periodQpc)
            : (submitQpc - previousSubmitQpc + periodQpc / 2) / periodQpc;
        return crossed <= 1 ? 0u : (uint)Math.Min(crossed - 1, uint.MaxValue);
    }

    private static long FloorDiv(long a, long b) { long q = a / b; return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q; }

    /// <summary>Sums (and clears) the idle credit banked for the present ids in (<paramref name="from"/>,
    /// <paramref name="to"/>] — the presents this sample newly retired, displayed or superseded.</summary>
    private long TakeCredit(uint from, uint to)
    {
        if (unchecked(to - from) > CreditCapacity) from = unchecked(to - CreditCapacity);
        long sum = 0;
        for (uint id = unchecked(from + 1); ; id = unchecked(id + 1))
        {
            ref Credit c = ref _credits[(int)(id & CreditMask)];
            if (c.Set && c.Id == id) { sum += c.Idle; c.Set = false; }
            if (id == to) break;
        }
        return sum;
    }
}
