using System;

namespace FluentGpu.Hosting;

/// <summary>
/// Pure, static, engine-free pacing arithmetic: turn "the earliest animation row is due in <c>dueMs</c>" into the
/// number of milliseconds the frame loop should wait, quantized to the window's refresh and phased on the last
/// present. This is the vblank-anchored half of the deleted <c>AppHost.AmbientFrameWaitMs</c>, kept verbatim in
/// spirit and separated from the host so the policy is unit-testable without a window
/// (<c>FluentGpu.Engine.Tests/CadencePacingTests.cs</c>).
///
/// <para><b>Why quantize at all.</b> A wall-clock budget is a timer that free-runs against the vblank: a 30 Hz row on
/// a 120 Hz panel asking for a flat 33.33 ms wait drifts through the 8.33 ms refresh window, so the frame actually
/// shown alternates between one produced just before a vblank and one produced just after — a slow beat that reads as
/// uneven shimmer/playhead motion even though the fps number is exactly right. Rounding the due time to a WHOLE number
/// of refreshes turns the cadence into what it always meant: "show every Nth vblank". 30 Hz on a 50 Hz panel is every
/// 2nd refresh = 25 fps; on a 120 Hz panel every 4th = 30 fps; on 144 Hz every 5th ≈ 28.8 fps. The rate a row asks for
/// is therefore an UPPER bound the panel can only round down to one of its own divisors — which is the point: a
/// non-divisor rate is exactly what beats against the vsync-locked present.</para>
///
/// <para><b>Why phase on the last present.</b> The present is vblank-locked, so anchoring the deadline to it lands
/// the wake just before a refresh instead of at an arbitrary offset into one. The modulo keeps the result inside
/// <c>(0, period]</c> no matter how stale the anchor is, so a stretch of skip-submitted (byte-identical) frames can
/// never drive the wait to 0 and free-spin the loop.</para>
/// </summary>
public static class CadencePacing
{
    /// <summary>The wait (ms, always ≥ 1) for a row due in <paramref name="dueMs"/>.
    /// <list type="bullet">
    /// <item><paramref name="refreshMs"/> ≤ 0 (refresh genuinely unknown — no present has completed, or a headless
    ///   device): no lattice to quantize onto, so <c>ceil(dueMs)</c> verbatim.</item>
    /// <item>otherwise <c>n = max(1, round(dueMs / refreshMs))</c> refreshes — never FASTER than the panel — and
    ///   <c>periodMs = n·refreshMs</c>.</item>
    /// <item><paramref name="sinceLastPresentMs"/> ≥ 0: phase the period on that anchor —
    ///   <c>ceil(periodMs − sinceLastPresentMs mod periodMs)</c>.</item>
    /// <item><paramref name="sinceLastPresentMs"/> &lt; 0 (nothing presented yet — pass −1): no anchor to phase on,
    ///   so <c>ceil(dueMs)</c>.</item>
    /// </list>
    /// The 1 ms floor is not cosmetic: a 0 turns the host loop into a pure poll for as long as the frame that would
    /// consume the due row stays out of reach.</summary>
    public static int QuantizedWaitMs(double dueMs, double refreshMs, double sinceLastPresentMs)
    {
        if (!(dueMs > 0.0)) dueMs = 0.0;   // NaN-safe: a non-positive/NaN due time is "now", floored to 1 below
        if (!(refreshMs > 0.0)) return Floor1(Math.Ceiling(dueMs));

        int n = (int)Math.Round(dueMs / refreshMs);
        if (n < 1) n = 1;                                  // never pace FASTER than the panel
        double periodMs = n * refreshMs;
        if (!(sinceLastPresentMs >= 0.0)) return Floor1(Math.Ceiling(dueMs));   // no present anchor yet (also NaN)
        return Floor1(Math.Ceiling(periodMs - sinceLastPresentMs % periodMs));
    }

    private static int Floor1(double ms) => ms < 1.0 ? 1 : ms > int.MaxValue ? int.MaxValue : (int)ms;
}
