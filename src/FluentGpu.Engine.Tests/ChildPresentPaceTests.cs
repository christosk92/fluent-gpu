using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Per-target present pacing (F097): a detached pop-out's presents keep their OWN evidence (the <c>child=</c> section of
/// <c>[render.pace]</c>, its <c>PresentLedger</c> rows) and its own present-queue depth, none of it folded into - or able to
/// retarget - the primary window's.
/// </summary>
public sealed class ChildPresentPaceTests
{
    private static readonly long Ms = Stopwatch.Frequency / 1000;

    [Fact]
    public void ChildPace_CountsItsOwnPresentsAndDeferrals_InTheWindow()
    {
        var pace = new ChildPresentPace(7);
        pace.BeginWindow();
        Assert.Null(pace.Describe());                        // an idle child adds nothing to the pace line

        pace.NoteSlotTake(0, opened: false);                 // busy: deferred, never waited
        pace.NoteSlotTake(2 * Ms, opened: true);
        pace.NotePresent(lagQpc: 9 * Ms, workQpc: 3 * Ms);
        pace.NoteSlotTake(0, opened: true);
        pace.NotePresent(lagQpc: 4 * Ms, workQpc: 5 * Ms);

        Assert.Equal(2, pace.Presents);
        Assert.Equal(1, pace.Deferred);
        string? line = pace.Describe();
        Assert.NotNull(line);
        Assert.StartsWith("t7(", line);
        Assert.Contains("presents=2", line);
        Assert.Contains("deferred=1", line);
        Assert.Contains("slotWaitMax=2.00", line);           // the child's OWN slot probe, max
        Assert.Contains("lagMax=9.00", line);
        Assert.Contains("workMax=5.00", line);

        // A new window rebases on the cumulative totals; the totals themselves keep counting.
        pace.BeginWindow();
        Assert.Null(pace.Describe());
        pace.NotePresent(Ms, Ms);
        Assert.Equal(3, pace.Presents);
        string? again = pace.Describe();
        Assert.NotNull(again);
        Assert.Contains("presents=1", again);
    }

    [Fact]
    public void ChildLedgerRows_AreSeparateFromThePrimaryRing_AndKeyedByTarget()
    {
        // Child publication seqs are numbered by the child's OWN seam: a row in the primary ring would answer "when did
        // publication N reach the glass" for the wrong window. Unique targets/seqs: the rings are process-wide.
        const int targetA = 9101, targetB = 9102;
        const ulong seq = 7_000_000UL;
        PresentLedger.RecordChild(targetA, seq, tickSeq: 11, tickQpc: 100, doneQpc: 150);
        PresentLedger.RecordChild(targetB, seq + 1, tickSeq: 12, tickQpc: 200, doneQpc: 250);

        Assert.True(PresentLedger.TryFindChildFirstAtOrAfter(targetA, seq, out PresentRecord a));
        Assert.Equal(seq, a.PublishSeq);
        Assert.Equal(targetA, a.Target);
        Assert.Equal(150, a.DoneQpc);

        Assert.True(PresentLedger.TryFindChildFirstAtOrAfter(targetB, seq, out PresentRecord b));
        Assert.Equal(targetB, b.Target);                     // never target A's row, though A's seq also satisfies >= seq
        Assert.Equal(seq + 1, b.PublishSeq);

        Assert.False(PresentLedger.TryFindChildFirstAtOrAfter(targetA, seq + 1, out _));   // A has nothing newer
        // The primary ring answers for the primary window alone.
        Assert.False(PresentLedger.TryFindFirstAtOrAfter(seq, out _));
    }

    [Fact]
    public void HeadlessDevice_PresentQueueDepth_IsPerSwapchain_AndNeverTouchesTheOthers()
    {
        var device = new HeadlessGpuDevice();
        var primary = new HeadlessSwapchain(new Size2(64, 64));
        var child = new HeadlessSwapchain(new Size2(64, 64));

        Assert.Equal(2, device.SetPresentQueueDepth(child, 2));
        Assert.Equal(2, device.PresentQueueDepthOf(child));
        Assert.Equal(1, device.PresentQueueDepthOf(primary));   // the child's policy never retargets the primary
        Assert.Equal(1, ((IGpuDevice)device).MaxFrameLatency);                // the headless contract (PresentQpc = FrameQpc + 2·refresh) holds

        Assert.Equal(1, device.SetPresentQueueDepth(child, 0)); // clamped to the policy bound 1..2
        Assert.Equal(2, device.SetPresentQueueDepth(child, 9));
    }
}
