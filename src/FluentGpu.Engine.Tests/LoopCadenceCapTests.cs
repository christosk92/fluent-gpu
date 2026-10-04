using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>F245: the weak-GPU-tier ceiling on perpetual motion lives where a cadence RESOLVES (<see cref="TierCadenceCap"/>),
/// not at each call site. Looping rows are clamped to 30 Hz on a weak tier, frame-clock pollers to 60 Hz, a row that opted out
/// (<see cref="Cadence.WithoutTierCap"/>) and every other tier are untouched, and a slower cadence keeps its own rate.
/// <c>GpuProfile.IsWeak</c> is always false headlessly, so the engine's tier read goes through <c>WeakTierForTest</c>.</summary>
public sealed class LoopCadenceCapTests
{
    private static readonly Keyframe[] Ramp = [new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.Linear)];

    [Theory]
    [InlineData(0, true, false, 33)]    // a display-rate loop: the one the cap exists for
    [InlineData(17, true, false, 33)]   // a 60 Hz row
    [InlineData(33, true, false, 33)]   // already 30 Hz
    [InlineData(42, true, false, 42)]   // the 24 Hz battery loop keeps its own (slower) rate
    [InlineData(100, true, false, 100)] // a 10 Hz HUD likewise
    [InlineData(0, true, true, 0)]      // opted out
    [InlineData(17, true, true, 17)]
    [InlineData(0, false, false, 0)]    // any other tier is untouched
    [InlineData(17, false, false, 17)]
    public void LoopPeriod_IsClampedOnAWeakTierOnly_AndNeverShortened(int periodMs, bool weak, bool optedOut, int expected)
        => Assert.Equal(expected, TierCadenceCap.LoopPeriodMs(periodMs, weak, optedOut));

    [Fact]
    public void WeakLoopMinPeriod_IsTheThirtyHzPeriodTheSchedulerResolvesTo()
        => Assert.Equal((int)System.MathF.Round(1000f / TierCadenceCap.WeakLoopMaxHz), TierCadenceCap.WeakLoopMinPeriodMs);

    [Fact]
    public void WithoutTierCap_SetsOnlyTheOptOut_AndKeepsTheRate()
    {
        var c = Cadence.At(60f).WithoutTierCap();
        Assert.True(c.TierUncapped);
        Assert.Equal(CadenceKind.Hz, c.Kind);
        Assert.Equal(60f, c.Hz);
        Assert.False(Cadence.At(60f).TierUncapped);   // the default is capped
        Assert.True(Cadence.Display.WithoutTierCap().TierUncapped);
        Assert.Equal(CadenceKind.DisplayRate, Cadence.Display.WithoutTierCap().Kind);
    }

    // ── the engine: every looping row resolves through the cap ───────────────────────────────────

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Anim) Engine(bool weak)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        return (scene, node, new AnimEngine(scene) { WeakTierForTest = weak });
    }

    /// <summary>The wait the host would take (ms until the loop's next due sample) once the seed frame has stamped the row; 0 =
    /// a frame is due every frame (display rate).</summary>
    private static float DueMs(bool weak, Cadence cadence, bool loop = true)
    {
        var (_, node, anim) = Engine(weak);
        anim.Keyframes(node, AnimChannel.Opacity, Ramp, 1000f, loop: loop, cadence: cadence);
        anim.Tick(16.67f);   // the seed frame stamps a cadenced row as advance #0
        return anim.NextDueMs(0d);
    }

    [Fact]
    public void WeakTier_ClampsADisplayRateLoopAndAFastLoop_ToThirtyHz()
    {
        Assert.Equal(0f, DueMs(weak: false, Cadence.Display));                       // control: a strong tier free-runs it
        float capped = DueMs(weak: true, Cadence.Display);
        Assert.InRange(capped, 30f, 34f);                                            // one 30 Hz period
        Assert.InRange(DueMs(weak: false, Cadence.At(60f)), 14f, 18f);               // control: 60 Hz on a strong tier
        Assert.InRange(DueMs(weak: true, Cadence.At(60f)), 30f, 34f);                // the marquee's fast cadence is capped
    }

    [Fact]
    public void WeakTier_LeavesASlowerCadenceAndAnOptedOutLoopAlone()
    {
        Assert.InRange(DueMs(weak: true, Cadence.At(24f)), 38f, 43f);                // slower than the cap: its own rate
        Assert.Equal(0f, DueMs(weak: true, Cadence.Display.WithoutTierCap()));       // an explicit opt-out keeps display rate
        Assert.InRange(DueMs(weak: true, Cadence.At(60f).WithoutTierCap()), 14f, 18f);
    }

    [Fact]
    public void WeakTier_NeverCapsAOneShot()
    {
        // A one-shot is short and must look smooth: the cap is for PERPETUAL rows only.
        Assert.Equal(0f, DueMs(weak: true, Cadence.Display, loop: false));
    }

    [Fact]
    public void WeakTier_AlsoClampsADefaultCadenceLoopWhenThePowerPolicyIsUncapped()
    {
        var (_, node, anim) = Engine(weak: true);
        anim.DefaultLoopHz = 0f;   // "uncapped": a default loop would run at display rate on any other tier
        anim.Keyframes(node, AnimChannel.Opacity, Ramp, 1000f, loop: true);
        anim.Tick(16.67f);
        Assert.InRange(anim.NextDueMs(0d), 30f, 34f);
    }

    [Fact]
    public void ReSeedingARowDropsThePreviousTenantsOptOut()
    {
        var (_, node, anim) = Engine(weak: true);
        anim.Keyframes(node, AnimChannel.Opacity, Ramp, 1000f, loop: true, cadence: Cadence.Display.WithoutTierCap());
        anim.Keyframes(node, AnimChannel.Opacity, Ramp, 1000f, loop: true, cadence: Cadence.Display);   // same slot, no opt-out
        anim.Tick(16.67f);
        Assert.InRange(anim.NextDueMs(0d), 30f, 34f);
    }

    // ── frame-clock pollers ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Poller_OnAWeakTier_IsServedEverySecondRefreshAt120Hz()
    {
        Assert.True(TierCadenceCap.TryPollerWaitMs(weak: true, refreshMs: 1000.0 / 120.0, sincePresentMs: -1.0,
            dueMs: double.PositiveInfinity, out int wait));
        Assert.Equal(17, wait);   // ceil of two 8.33 ms refreshes
        Assert.True(TierCadenceCap.TryPollerWaitMs(true, 1000.0 / 120.0, 3.0, double.PositiveInfinity, out wait));
        Assert.InRange(wait, 1, 17);   // phased on the last present
    }

    [Fact]
    public void Poller_RoundsTheRefreshCountUp_SoItIsNeverFasterThanTheCap()
    {
        Assert.True(TierCadenceCap.TryPollerWaitMs(true, 1000.0 / 144.0, -1.0, double.PositiveInfinity, out int wait));
        Assert.Equal(21, wait);   // three 6.94 ms refreshes (48 Hz), not two (72 Hz, over the cap)
        Assert.True(wait >= TierCadenceCap.WeakPollerMinPeriodMs);
    }

    [Fact]
    public void Poller_IsNotDelayedByALaterRowOrCaretDue()
    {
        // The poller is due now: a later row (50 ms) or a caret half-period (500 ms) must not stretch its wait.
        Assert.True(TierCadenceCap.TryPollerWaitMs(true, 1000.0 / 120.0, -1.0, 50.0, out int wait));
        Assert.Equal(17, wait);
        Assert.True(TierCadenceCap.TryPollerWaitMs(true, 1000.0 / 120.0, -1.0, 500.0, out wait));
        Assert.Equal(17, wait);
    }

    [Fact]
    public void Poller_AnEarlierDueShortensTheWait()
    {
        Assert.True(TierCadenceCap.TryPollerWaitMs(true, 1000.0 / 120.0, -1.0, 5.0, out int wait));
        Assert.InRange(wait, 1, 9);   // one refresh, not the two-refresh cap
    }

    [Theory]
    [InlineData(false, 1000.0 / 120.0)]   // not weak
    [InlineData(true, 1000.0 / 60.0)]     // a 60 Hz panel's display tick IS the cap
    [InlineData(true, 1000.0 / 59.94)]
    [InlineData(true, 1000.0 / 30.0)]     // slower than the cap already
    [InlineData(true, 0.0)]               // refresh unknown
    public void Poller_HasNoCapToApply_OffAWeakTierOrOnASlowPanel(bool weak, double refreshMs)
        => Assert.False(TierCadenceCap.TryPollerWaitMs(weak, refreshMs, -1.0, double.PositiveInfinity, out _));
}
