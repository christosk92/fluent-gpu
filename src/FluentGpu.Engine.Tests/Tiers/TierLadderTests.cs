using System;
using FluentGpu.Foundation;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E22: <see cref="TierLadder"/> is pure policy, so every rule is asserted without a host: direction-aware holds on both
/// edge directions, absent edges, the empty ladder, the aspect rule, multi-edge jumps and the no-oscillation property.
/// </summary>
public sealed class TierLadderTests
{
    // Classic's C-Breakpoints: Small at 528 and Standard at 1008 hold 40 on WIDENING; Huge at 1920 holds 40 on NARROWING;
    // the Huge 4K edge is absent (Classic draws no such band).
    private static TierLadder Classic() => TierLadder.Of(
        new TierEdge(528, NarrowHold: 0, WidenHold: 40),
        new TierEdge(1008, NarrowHold: 0, WidenHold: 40),
        new TierEdge(1920, NarrowHold: 40, WidenHold: 0),
        TierEdge.None);

    // WinUI / Zune: 24 on narrowing only, every edge present.
    private static TierLadder Zune() => TierLadder.Of(
        new TierEdge(640, 24), new TierEdge(1008, 24), new TierEdge(1920, 24), new TierEdge(3200, 24));

    [Fact]
    public void AFreshResolve_UsesTheEdgeItself_NotTheHold()
    {
        var l = Classic();
        Assert.Equal(0, l.Resolve(527.9f, -1));
        Assert.Equal(1, l.Resolve(528f, -1));    // a fresh launch at 528-567 lands in Small although widening would need 568
        Assert.Equal(1, l.Resolve(540f, -1));
        Assert.Equal(2, l.Resolve(1008f, -1));
        Assert.Equal(3, l.Resolve(1920f, -1));   // a fresh launch at 1920 is Huge
    }

    [Fact]
    public void Widening_CommitsAtEdgePlusWidenHold()
    {
        var l = Classic();
        Assert.Equal(0, l.Resolve(528f, 0));     // inside the band: the previous tier holds
        Assert.Equal(0, l.Resolve(567.9f, 0));
        Assert.Equal(1, l.Resolve(568f, 0));
        Assert.Equal(1, l.Resolve(1047.9f, 1));
        Assert.Equal(2, l.Resolve(1048f, 1));
    }

    [Fact]
    public void Narrowing_CommitsBelowEdgeMinusNarrowHold()
    {
        var l = Classic();
        // 528 / 1008 have no narrowing hold: back below the edge itself.
        Assert.Equal(1, l.Resolve(528f, 1));
        Assert.Equal(0, l.Resolve(527.9f, 1));
        Assert.Equal(2, l.Resolve(1008f, 2));
        Assert.Equal(1, l.Resolve(1007.9f, 2));
        // 1920 holds 40 on narrowing: Huge until below 1880.
        Assert.Equal(3, l.Resolve(1880f, 3));
        Assert.Equal(3, l.Resolve(1900f, 3));
        Assert.Equal(2, l.Resolve(1879.9f, 3));
    }

    [Fact]
    public void TheHugeEdge_WidensWithoutAHold_SoAMaximizeFrom1920LandsInHuge()
    {
        var l = Classic();
        Assert.Equal(3, l.Resolve(1920f, 2));    // WidenHold 0: from Standard, exactly 1920 is Huge
        Assert.Equal(3, l.Resolve(1920f, -1));   // and so is a fresh launch there
    }

    [Fact]
    public void BothDirections_AreIndependentPerEdge()
    {
        var l = Zune();
        Assert.Equal(1, l.Resolve(640f, 0));      // widening: no hold, at the edge
        Assert.Equal(1, l.Resolve(616f, 1));      // narrowing: 24 hold, still Small at 616
        Assert.Equal(0, l.Resolve(615.9f, 1));
    }

    [Fact]
    public void AnAbsentEdge_NeverCrosses_AndItsUpperTierIsUnreachable()
    {
        var l = Classic();
        Assert.Equal(3, l.Resolve(100_000f, 3));
        Assert.Equal(3, l.Resolve(100_000f, -1));
        Assert.Equal(3, l.Resolve(float.PositiveInfinity, 0));
        Assert.Equal(5, l.TierCount);             // the index space still has Huge4K; it is just unreachable
    }

    [Fact]
    public void AnAbsentEdgeInTheMiddle_LetsTheNextEdgeStillCross()
    {
        var l = TierLadder.Of(TierEdge.None, new TierEdge(1000f));
        Assert.Equal(0, l.Resolve(500f, -1));
        Assert.Equal(2, l.Resolve(1000f, -1));    // tier 1 (behind the absent edge) is skipped
    }

    [Fact]
    public void AnEmptyLadder_IsAlwaysTierZero()
    {
        Assert.Equal(0, TierLadder.Empty.Resolve(0f, -1));
        Assert.Equal(0, TierLadder.Empty.Resolve(5000f, 0));
        Assert.Equal(0, TierLadder.Empty.Resolve(5000f, 7));
        Assert.Equal(0, TierLadder.Empty.Resolve(float.NaN, -1));
        Assert.Equal(1, TierLadder.Empty.TierCount);
        Assert.Equal(0, TierLadder.Of().EdgeCount);
        var allAbsent = TierLadder.Of(TierEdge.None, TierEdge.None);
        Assert.Equal(0, allAbsent.Resolve(9999f, -1));
        Assert.Equal(0, allAbsent.Resolve(9999f, 0));
    }

    [Fact]
    public void ADragAcrossSeveralEdges_JumpsToTheHighestCrossedTier()
    {
        var l = Classic();
        Assert.Equal(3, l.Resolve(2400f, 0));     // 400 -> 2400 in one step (a maximize)
        Assert.Equal(0, l.Resolve(300f, 3));      // and back
    }

    [Fact]
    public void ANaNValue_KeepsThePreviousTier()
    {
        var l = Classic();
        Assert.Equal(2, l.Resolve(float.NaN, 2));
        Assert.Equal(0, l.Resolve(float.NaN, -1));
        Assert.Equal(4, l.Resolve(float.NaN, 99));   // clamped into the index space
    }

    [Fact]
    public void APreviousTierBeyondTheLadder_IsClamped()
    {
        var l = TierLadder.Of(new TierEdge(100f, 10f));
        Assert.Equal(1, l.Resolve(95f, 50));         // clamped to the top tier (1): 95 is inside the 10-DIP narrowing hold
        Assert.Equal(0, l.Resolve(89f, 50));
    }

    [Fact]
    public void Hysteresis_ADragBackAndForthAcrossABand_CrossesExactlyOncePerDirection()
    {
        var l = Classic();
        int tier = -1, changes = 0;
        // widen 500 -> 640, then narrow back to 500, in 1-DIP steps through the 528-568 band.
        for (float w = 500f; w <= 640f; w += 1f) { int t = l.Resolve(w, tier); if (t != tier) { if (tier >= 0) changes++; tier = t; } }
        Assert.Equal(1, tier);
        Assert.Equal(1, changes);                  // 0 -> 1, once, at 568 (not at 528)
        for (float w = 640f; w >= 500f; w -= 1f) { int t = l.Resolve(w, tier); if (t != tier) { changes++; tier = t; } }
        Assert.Equal(0, tier);
        Assert.Equal(2, changes);                  // 1 -> 0, once, at 527
        // A jitter inside the band never flips anything.
        tier = 0;
        for (int i = 0; i < 200; i++) Assert.Equal(0, tier = l.Resolve(540f + (i % 2) * 20f, tier));
    }

    [Fact]
    public void TheAspectRule_CapsAPortraitWindow_WithHysteresis()
    {
        var l = TierLadder.Of(new TierAspectRule(PortraitBelow: 0.8f, PortraitMaxTier: 1, Hold: 0.1f),
            new TierEdge(640f), new TierEdge(1008f), new TierEdge(1920f));
        Assert.Equal(3, l.Resolve(2000f, 1.78f, -1));   // landscape: the edges alone
        Assert.Equal(1, l.Resolve(2000f, 0.5f, -1));    // portrait: capped at tier 1
        Assert.Equal(1, l.Resolve(2000f, 0.85f, 1));    // already capped: held until Below + Hold
        Assert.Equal(3, l.Resolve(2000f, 0.95f, 1));    // released
        Assert.Equal(3, l.Resolve(2000f, 0.85f, -1));   // a fresh resolve has no hold
        Assert.Equal(0, l.Resolve(300f, 0.5f, 0));      // a tier already under the cap is untouched
        Assert.Equal(3, l.Resolve(2000f, float.NaN, -1));   // unknown aspect: the rule is off
        Assert.Equal(3, l.Resolve(2000f, 0));               // the two-argument form never applies the rule
    }

    [Fact]
    public void TheAspectRule_LiftsAVeryWideWindow_WithHysteresis()
    {
        var l = TierLadder.Of(new TierAspectRule(PortraitBelow: 0f, PortraitMaxTier: 3, LandscapeAbove: 2.5f, LandscapeMinTier: 2, Hold: 0.1f),
            new TierEdge(640f), new TierEdge(1008f), new TierEdge(1920f));
        Assert.Equal(1, l.Resolve(700f, 1.6f, -1));
        Assert.Equal(2, l.Resolve(700f, 3.0f, -1));     // a 32:9 strip at a small width still gets the Standard layout
        Assert.Equal(2, l.Resolve(700f, 2.45f, 2));     // held (floor already in effect, within the 0.1 hold)
        Assert.Equal(1, l.Resolve(700f, 2.3f, 2));      // released
    }

    [Fact]
    public void Construction_RejectsNonAscendingEdges_AndNegativeHolds_AndBadAspectTiers()
    {
        Assert.Throws<ArgumentException>(() => TierLadder.Of(new TierEdge(500f), new TierEdge(500f)));
        Assert.Throws<ArgumentException>(() => TierLadder.Of(new TierEdge(900f), new TierEdge(500f)));
        Assert.Throws<ArgumentException>(() => TierLadder.Of(new TierEdge(500f, NarrowHold: -1f)));
        Assert.Throws<ArgumentException>(() => TierLadder.Of(new TierEdge(float.PositiveInfinity)));
        Assert.Throws<ArgumentException>(() => TierLadder.Of(new TierAspectRule(1f, 9), new TierEdge(500f)));
        TierLadder.Of(TierEdge.None, new TierEdge(500f), TierEdge.None, new TierEdge(900f));   // absent edges are skipped by the ordering check
    }

    [Fact]
    public void Resolve_AllocatesNothing()
    {
        var l = TierLadder.Of(new TierAspectRule(0.8f, 1, Hold: 0.1f), new TierEdge(640f, 24), new TierEdge(1008f, 24), new TierEdge(1920f, 24));
        int tier = -1;
        for (int i = 0; i < 64; i++) tier = l.Resolve(300f + i * 40f, 1.6f, tier);   // warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) tier = l.Resolve(300f + (i % 64) * 40f, 1.6f, tier);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(tier >= 0);
    }
}
