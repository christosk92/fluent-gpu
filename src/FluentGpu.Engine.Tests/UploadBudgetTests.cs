using FluentGpu.Rhi;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The off-frame upload policy (docs/plans/scroll-gpu-retained-tiles-implementation.md §C): one live-tunable
/// per-turn byte budget, a turn meter that never starves a large image, and the fence-compare readiness rule (an image is
/// resident only when the completed copy fence has reached its upload fence — never a wait).</summary>
[Collection(SerialTestCollection.Name)]   // BytesPerTurn is a process-wide live tunable
public sealed class UploadBudgetTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void TheBudgetDefaultsTo8MiB_IsLiveTunable_AndClampsToItsRange()
    {
        long saved = UploadBudget.BytesPerTurn;
        try
        {
            UploadBudget.ResetToDefault();
            Assert.Equal(8 * MiB, UploadBudget.BytesPerTurn);

            uint v0 = UploadBudget.Version;
            UploadBudget.BytesPerTurn = 2 * MiB;
            Assert.Equal(2 * MiB, UploadBudget.BytesPerTurn);
            Assert.NotEqual(v0, UploadBudget.Version);

            UploadBudget.BytesPerTurn = 1;
            Assert.Equal(UploadBudget.MinBytesPerTurn, UploadBudget.BytesPerTurn);
            UploadBudget.BytesPerTurn = long.MaxValue;
            Assert.Equal(UploadBudget.MaxBytesPerTurn, UploadBudget.BytesPerTurn);
        }
        finally { UploadBudget.BytesPerTurn = saved; }
    }

    [Fact]
    public void ATurnAdmitsWhileJobsFit_ThenDefersTheRestToTheNextTurn()
    {
        var m = new UploadTurnMeter();
        m.Begin(8 * MiB);
        Assert.True(m.TryAdmit(3 * MiB));
        Assert.True(m.TryAdmit(5 * MiB));        // exactly fills the budget
        Assert.False(m.TryAdmit(1));             // nothing fits after that
        Assert.False(m.TryAdmit(2 * MiB));
        Assert.Equal(2, m.Admitted);
        Assert.Equal(2, m.Deferred);
        Assert.Equal(8 * MiB, m.SpentBytes);
        Assert.Equal(0, m.RemainingBytes);
        Assert.Equal(2 * MiB + 1, m.DeferredBytes);

        m.Begin(8 * MiB);                         // the next turn starts clean
        Assert.Equal(0, m.Admitted);
        Assert.Equal(0, m.Deferred);
        Assert.True(m.TryAdmit(2 * MiB));
    }

    [Fact]
    public void TheFirstJobOfATurnIsAlwaysAdmitted_SoAnImageLargerThanTheBudgetNeverStarves()
    {
        var m = new UploadTurnMeter();
        m.Begin(8 * MiB);
        Assert.True(m.TryAdmit(33 * MiB));        // a 4K cover: over the budget on its own, still progresses
        Assert.False(m.TryAdmit(64 * 1024));      // but it is the only job this turn
        Assert.Equal(1, m.Admitted);
    }

    [Theory]
    [InlineData(0ul, 0ul, UploadReadiness.NotStaged)]
    [InlineData(5ul, 0ul, UploadReadiness.NotStaged)]
    [InlineData(4ul, 5ul, UploadReadiness.InFlight)]
    [InlineData(5ul, 5ul, UploadReadiness.Resident)]
    [InlineData(9ul, 5ul, UploadReadiness.Resident)]
    public void ReadinessIsAFenceCompare(ulong completed, ulong imageFence, UploadReadiness expected)
    {
        Assert.Equal(expected, UploadFencePolicy.Classify(completed, imageFence));
        Assert.Equal(expected == UploadReadiness.Resident, UploadFencePolicy.IsResident(completed, imageFence));
    }

    [Fact]
    public void TheLedgerHandsOutIncreasingNonZeroFences_AndReadinessNeverWalksBackwards()
    {
        var ledger = new UploadFenceLedger();
        ulong a = ledger.NextSignal(), b = ledger.NextSignal(), c = ledger.NextSignal();
        Assert.True(a > 0 && b > a && c > b);
        Assert.True(ledger.HasInFlight);

        ledger.ObserveCompleted(b);
        Assert.True(ledger.IsResident(a));
        Assert.True(ledger.IsResident(b));
        Assert.Equal(UploadReadiness.InFlight, ledger.Classify(c));

        ledger.ObserveCompleted(a);               // a stale/reordered read
        Assert.True(ledger.IsResident(b));        // …does not un-ready an image

        ledger.ObserveCompleted(ulong.MaxValue);  // a removed device reports UINT64_MAX
        Assert.Equal(c, ledger.Completed);        // clamped to what was actually signaled
        Assert.False(ledger.HasInFlight);
        Assert.False(ledger.IsResident(0));       // never-staged stays not resident

        ledger.Reset();                           // device recovery: a new fence restarts
        Assert.Equal(UploadReadiness.InFlight, ledger.Classify(c));
        Assert.Equal(1ul, ledger.NextSignal());
    }
}
