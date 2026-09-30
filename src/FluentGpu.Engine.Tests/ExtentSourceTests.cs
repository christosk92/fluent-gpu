using System;
using FluentGpu.Scroll.Extent;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Scroll rework Wave 0 part D: <see cref="FixedExtent"/> and <see cref="MeasuredExtent"/> against a naive
/// prefix-sum oracle — the estimate-then-correct Fenwick contract (design doc §B.4) must survive 10k-100k rows and
/// arbitrary <c>SetMeasured</c>/<c>SetEstimate</c>/<c>Resize</c> traffic without ever drifting from a brute-force
/// prefix sum, and without ever shifting an index above the caller's declared anchor.</summary>
public sealed class ExtentSourceTests
{
    // ── FixedExtent ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FixedExtent_100kRows_TotalAndRoundTripAreExact()
    {
        const int n = 100_000;
        const double stride = 56.0;
        var f = new FixedExtent(n, stride);

        Assert.Equal((double)n * stride, f.Total);

        for (int i = 0; i < n; i += 137)   // sample every 137th row across the whole range
            Assert.Equal(i, f.IndexAt(f.OffsetOf(i)));
        Assert.Equal(n - 1, f.IndexAt(f.OffsetOf(n - 1)));
    }

    [Fact]
    public void FixedExtent_IndexAt_IsPreciseAtALargeOffset()
    {
        var f = new FixedExtent(100_000, 56.0);
        // 5.6e6 / 56 == 100000 exactly -> clamps to the last valid row (Count-1).
        Assert.Equal(99_999, f.IndexAt(5_600_000.0));
        Assert.Equal(99_999, f.IndexAt(5_599_999.0));
        Assert.Equal(0, f.IndexAt(55.9));
        Assert.Equal(1, f.IndexAt(56.0));
    }

    [Fact]
    public void FixedExtent_LeadingAndTrailingPad_AreExact()
    {
        var f = new FixedExtent(10, 20.0, leadingPad: 100.0, trailingPad: 40.0);
        Assert.Equal(100.0, f.OffsetOf(0));
        Assert.Equal(100.0 + 10 * 20.0 + 40.0, f.Total);
        Assert.Equal(0, f.IndexAt(50.0));      // inside the leading pad -> before row 0
        Assert.Equal(0, f.IndexAt(100.0));     // exactly row 0's start
        Assert.Equal(1, f.IndexAt(120.0));     // exactly row 1's start
    }

    [Fact]
    public void FixedExtent_SetMeasured_IsANoOp()
    {
        var f = new FixedExtent(10, 20.0);
        Assert.Equal(0.0, f.SetMeasured(3, 999.0, anchorIndex: 5));
        Assert.Equal(20.0, f.ExtentOf(3));
        Assert.True(f.IsMeasured(3));
    }

    // ── MeasuredExtent vs a naive prefix-sum oracle ──────────────────────────────────────────────────────

    private sealed class Oracle
    {
        public readonly double[] Extent;
        public Oracle(int n, double estimate) { Extent = new double[n]; Array.Fill(Extent, estimate); }
        public double Total => Sum(Extent.Length);
        public double OffsetOf(int i) => Sum(Math.Clamp(i, 0, Extent.Length));
        public int IndexAt(double off)
        {
            if (Extent.Length == 0 || off <= 0.0) return 0;
            double acc = 0.0;
            for (int i = 0; i < Extent.Length; i++)
            {
                double next = acc + Extent[i];
                if (next > off) return i;
                acc = next;
            }
            return Extent.Length - 1;
        }
        private double Sum(int upTo) { double s = 0.0; for (int i = 0; i < upTo; i++) s += Extent[i]; return s; }
    }

    [Fact]
    public void MeasuredExtent_MatchesNaiveOracle_UnderRandomSetMeasured()
    {
        const int n = 400;
        var rng = new Random(1234567);
        var oracle = new Oracle(n, 100.0);
        var m = new MeasuredExtent(n, 100.0);

        for (int op = 0; op < 1000; op++)
        {
            int i = rng.Next(n);
            double extent = rng.Next(1, 500);
            int anchor = rng.Next(n + 1);

            double expectedOld = oracle.Extent[i];
            double expectedDelta = i < anchor ? extent - expectedOld : 0.0;
            oracle.Extent[i] = extent;

            double actualDelta = m.SetMeasured(i, extent, anchor);

            Assert.Equal(expectedDelta, actualDelta, 9);
            Assert.Equal(oracle.Total, m.Total, 6);
            for (int probe = 0; probe <= n; probe += 37)
                Assert.Equal(oracle.OffsetOf(probe), m.OffsetOf(probe), 6);
        }

        // Final full sweep: every offset/IndexAt pair agrees with the oracle.
        for (int i = 0; i <= n; i++)
            Assert.Equal(oracle.OffsetOf(i), m.OffsetOf(i), 6);
        for (int i = 0; i < n; i++)
        {
            double off = oracle.OffsetOf(i);
            Assert.Equal(oracle.IndexAt(off), m.IndexAt(off));
        }
    }

    [Fact]
    public void SetMeasured_AboveAnchor_ReturnsDelta_BelowAnchor_ReturnsZero()
    {
        var m = new MeasuredExtent(10, 50.0);

        // i=2 < anchor=5 -> "above" the anchor (earlier index / smaller offset) -> nonzero delta returned.
        double above = m.SetMeasured(2, 90.0, anchorIndex: 5);
        Assert.Equal(40.0, above);

        // i=7 >= anchor=5 -> at/below the anchor -> the anchor's own offset is unaffected -> 0 returned,
        // even though the underlying table (and Total) still updates.
        double before = m.Total;
        double below = m.SetMeasured(7, 90.0, anchorIndex: 5);
        Assert.Equal(0.0, below);
        Assert.Equal(before + 40.0, m.Total);

        // i == anchor counts as "at/below" too (not above).
        double atAnchor = m.SetMeasured(5, 90.0, anchorIndex: 5);
        Assert.Equal(0.0, atAnchor);
    }

    [Fact]
    public void SetEstimate_RetargetsOnlyUnmeasuredRows_AnchoredDelta()
    {
        var m = new MeasuredExtent(6, 50.0);
        m.SetMeasured(1, 200.0, anchorIndex: 0);   // row 1 is now measured -> immune to SetEstimate
        m.SetMeasured(4, 300.0, anchorIndex: 0);   // row 4 is now measured -> immune to SetEstimate

        // Unmeasured rows: 0, 2, 3, 5. Anchor at index 3 -> rows 0 and 2 are "above" (index < 3).
        double delta = m.SetEstimate(80.0, anchorIndex: 3);
        Assert.Equal(2 * (80.0 - 50.0), delta, 9);

        Assert.Equal(80.0, m.ExtentOf(0));
        Assert.Equal(200.0, m.ExtentOf(1));   // untouched (measured)
        Assert.Equal(80.0, m.ExtentOf(2));
        Assert.Equal(80.0, m.ExtentOf(3));    // at the anchor -> retargeted, but delta not counted "above"
        Assert.Equal(300.0, m.ExtentOf(4));   // untouched (measured)
        Assert.Equal(80.0, m.ExtentOf(5));
        Assert.Equal(80.0, m.Estimate);
    }

    [Fact]
    public void Resize_Append_KeepsExistingOffsetsUnchanged()
    {
        var m = new MeasuredExtent(5, 100.0);
        m.SetMeasured(2, 250.0, anchorIndex: 0);
        double[] before = new double[6];
        for (int i = 0; i <= 5; i++) before[i] = m.OffsetOf(i);

        m.Resize(9);   // append 4 estimate rows at the end

        for (int i = 0; i <= 5; i++)
            Assert.Equal(before[i], m.OffsetOf(i), 9);
        Assert.Equal(9, m.Count);
        Assert.Equal(before[5] + 100.0 * 4, m.Total, 9);
        for (int i = 5; i < 9; i++)
        {
            Assert.False(m.IsMeasured(i));
            Assert.Equal(100.0, m.ExtentOf(i));
        }
    }

    [Fact]
    public void Resize_Shrink_Truncates()
    {
        var m = new MeasuredExtent(10, 20.0);
        m.SetMeasured(1, 90.0, anchorIndex: 0);
        m.Resize(4);
        Assert.Equal(4, m.Count);
        Assert.Equal(90.0 + 20.0 * 3, m.Total, 9);

        // Growing back past the truncated tail must not resurrect stale measured bits (finding-guard).
        m.Resize(10);
        for (int i = 4; i < 10; i++) Assert.False(m.IsMeasured(i));
    }

    [Fact]
    public void IndexAt_ClampsToValidRange()
    {
        var m = new MeasuredExtent(5, 10.0);
        Assert.Equal(0, m.IndexAt(-5.0));
        Assert.Equal(0, m.IndexAt(0.0));
        Assert.Equal(4, m.IndexAt(1000.0));   // past Total -> clamps to last row

        var empty = new MeasuredExtent(0, 10.0);
        Assert.Equal(0, empty.IndexAt(5.0));
    }
}
