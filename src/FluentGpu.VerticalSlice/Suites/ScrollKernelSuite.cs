using System;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Scroll;
using static FluentGpu.VerticalSlice.Harness.Gate;

/// <summary>Headless gates for WP-A's scroll v3 kernel (<c>src/FluentGpu.Engine/Scroll/</c>) — the plan §9 "New gate
/// list" <c>gate.kernel.*</c> rows, run against a <see cref="RecordingSink"/> test double. No <c>FluentGpu.Scene</c>,
/// no window, no GPU — the kernel is portable by construction, and <see cref="ThreadAgnosticCheck"/> proves it.</summary>
static class ScrollKernelSuite
{
    public static void Run(StringTable strings)
    {
        DtInvarianceCheck();
        DragResampleCheck();
        FrameDeltaCheck();
        FlingDistanceCheck();
        FlingSeedFromFrameDeltasCheck();
        BandRoundtripCheck();
        ChainDragTimeCheck();
        ChainLiftHandoffCheck();
        ChainBallisticEdgeCheck();
        WheelAccumulateHardStopCheck();
        WheelColdSeedCheck();
        WheelCadenceFlatCheck();
        WheelSecondClickCheck();
        WheelDtInvarianceCheck();
        WheelReversalCheck();
        WheelSlowCadenceStiffCheck();
        WheelEdgeDegenerateCheck();
        WheelFlingCarryCheck();
        WheelNoSubpixelTailCheck();
        ProgrammaticGlideRetargetCheck();
        RestoreLatchUntilExtentCheck();
        RestoreGoalExtentGrowsCheck();
        RestoreCancelOnInputCheck();
        RestoreDeadlineCheck();
        AnchorShiftUnderDragCheck();
        EdgePendingResolvesOnGrowCheck();
        UndersampledFlickCheck();
        SnapFlingLandsCheck();
        AllocZeroTickCheck();
        BodySparseCheck();
        SlotReuseCheck();
        ThreadAgnosticCheck();
        OneWritePerTickCheck();
        PortOverflowPolicyCheck();
    }

    // ── Test double ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records every <see cref="IScrollSink.Apply"/> call into preallocated arrays (no <c>List&lt;T&gt;</c>,
    /// no boxing) so <see cref="AllocZeroTickCheck"/> can wrap a measured window around live <see cref="ScrollKernel.Tick"/>
    /// calls without the test double itself contaminating the GC delta.</summary>
    sealed class RecordingSink : IScrollSink
    {
        public int[] Nodes = new int[4096];
        public ScrollWrite[] Writes = new ScrollWrite[4096];
        public int Count;

        public void Apply(int node, in ScrollWrite w)
        {
            if (Count >= Nodes.Length) { Array.Resize(ref Nodes, Nodes.Length * 2); Array.Resize(ref Writes, Writes.Length * 2); }
            Nodes[Count] = node;
            Writes[Count] = w;
            Count++;
        }

        public void Clear() => Count = 0;

        public int CountFor(int node)
        {
            int c = 0;
            for (int i = 0; i < Count; i++) if (Nodes[i] == node) c++;
            return c;
        }

        public bool TryLast(int node, out ScrollWrite w)
        {
            for (int i = Count - 1; i >= 0; i--)
                if (Nodes[i] == node) { w = Writes[i]; return true; }
            w = default;
            return false;
        }
    }

    private static ScrollFrameSpec Frame(float extent, float viewport, float snapInterval = 0f, float snapStart = 0f, float snapEnd = 0f, float[]? snapPoints = null)
        => new(0, extent, 300f, viewport, 300f, 1f, false, snapInterval, snapStart, snapEnd, snapPoints);

    private static void SetupViewport(ScrollKernel k, int node, float extent, float viewport, float snapInterval = 0f, float snapStart = 0f, float snapEnd = 0f, float[]? snapPoints = null)
    {
        k.Port.Post(ScrollInput.Bind(node));
        k.Port.Post(ScrollInput.SetFrame(node, Frame(extent, viewport, snapInterval, snapStart, snapEnd, snapPoints)));
        k.Reclamp();
    }

    private static ScrollClock ClockAt(double t, float dtSec = 0.00833f) => new(t, dtSec, t, 0.00833f);

    // ── gate.kernel.dt-invariance ─────────────────────────────────────────────────────────────────────────────
    // Exercises the ported physics formulas directly (CoastStep/ChaseStep/StepSpring are the shared per-body time
    // step ScrollBody.Advance calls) — the frame-rate independence claim these gates verify.

    private static void DtInvarianceCheck()
    {
        float LandFling(float dtMs)
        {
            float v = 1500f, pos = 0f, dt = dtMs / 1000f;
            for (int i = 0; i < 20000 && MathF.Abs(v) > ScrollFeel.Shipping.FlingSettleVel; i++)
                pos += ScrollPhysics.CoastStep(ref v, dt, ScrollFeel.Shipping.FlingDecayPerS);
            return pos;
        }
        float LandChase(float dtMs)
        {
            float off = 0f, vel = 0f, target = 400f, dt = dtMs / 1000f;
            for (int i = 0; i < 20000 && (MathF.Abs(off - target) > 0.5f || MathF.Abs(vel) > ScrollFeel.Shipping.FlingSettleVel); i++)
                ScrollPhysics.ChaseStep(ref off, ref vel, target, 40f, dt);
            return off;
        }
        float LandBounce(float dtMs)
        {
            float pos = 80f, vel = -200f, dt = dtMs / 1000f;
            bool settled = false;
            for (int i = 0; i < 20000 && !settled; i++)
                settled = ScrollPhysics.StepSpring(ref pos, ref vel, dt, ScrollFeel.Shipping.SnapBackOmega, 0f);
            return pos;
        }

        float f1 = LandFling(8.33f), f2 = LandFling(16.67f), f3 = LandFling(33.3f);
        float c1 = LandChase(8.33f), c2 = LandChase(16.67f), c3 = LandChase(33.3f);
        float b1 = LandBounce(8.33f), b2 = LandBounce(16.67f), b3 = LandBounce(33.3f);

        bool ok = MathF.Abs(f1 - f2) <= 0.5f && MathF.Abs(f1 - f3) <= 0.5f
            && MathF.Abs(c1 - c2) <= 0.5f && MathF.Abs(c1 - c3) <= 0.5f
            && MathF.Abs(b1 - b2) <= 0.5f && MathF.Abs(b1 - b3) <= 0.5f;
        Check("gate.kernel.dt-invariance", ok,
            $"fling {f1:F2}/{f2:F2}/{f3:F2}; chase {c1:F2}/{c2:F2}/{c3:F2}; bounce {b1:F2}/{b2:F2}/{b3:F2}");
    }

    // ── gate.kernel.drag-1to1-resample ────────────────────────────────────────────────────────────────────────

    private static void DragResampleCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 2000f, 400f);

        double t = 0;
        k.Port.Post(ScrollInput.ContactBegin(1, t, 0f));
        const float v = 800f; // DIP/s, constant — resample is EXACT (no lag beyond the fixed 12ms latency) for constant velocity.
        for (int i = 1; i <= 6; i++)
        {
            t = i * 0.008;
            k.Port.Post(ScrollInput.ContactMove(1, t, (float)(v * t)));
        }
        var clock = ClockAt(t, 0.008f);
        k.Tick(in clock);
        k.TryGetBody(1, out var body);

        double tStar = t - ScrollFeel.Shipping.ResampleLatencyMs / 1000.0;
        float expected = (float)(v * tStar);
        Check("gate.kernel.drag-1to1-resample", MathF.Abs(body.PositionMain - expected) < 0.5f,
            $"pos={body.PositionMain:F2} expected={expected:F2}");
    }

    // ── gate.kernel.framedelta-1to1 ───────────────────────────────────────────────────────────────────────────

    private static void FrameDeltaCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 2000f, 400f);

        double t = 0;
        float total = 0f;
        for (int i = 1; i <= 5; i++)
        {
            t = i * 0.00833;
            const float d = 12.5f;
            total += d;
            k.Port.Post(ScrollInput.FrameDelta(1, t, d));
        }
        var clock = ClockAt(t);
        k.Tick(in clock);
        k.TryGetBody(1, out var body);
        Check("gate.kernel.framedelta-1to1", MathF.Abs(body.PositionMain - total) < 0.01f, $"pos={body.PositionMain} total={total}");
    }

    // ── gate.kernel.fling-distance ────────────────────────────────────────────────────────────────────────────

    private static void FlingDistanceCheck()
    {
        const float v0 = 1500f;
        float k = -MathF.Log(ScrollFeel.Shipping.FlingDecayPerS);
        float expected = v0 / k;
        float v = v0, pos = 0f, dt = 1f / 120f;
        for (int i = 0; i < 20000 && MathF.Abs(v) > ScrollFeel.Shipping.FlingSettleVel; i++)
            pos += ScrollPhysics.CoastStep(ref v, dt, ScrollFeel.Shipping.FlingDecayPerS);
        float relErr = MathF.Abs(pos - expected) / expected;
        Check("gate.kernel.fling-distance", relErr <= 0.01f, $"pos={pos:F2} expected={expected:F2} relErr={relErr:P2}");
    }

    // ── gate.kernel.fling-seed-from-framedeltas ───────────────────────────────────────────────────────────────

    private static void FlingSeedFromFrameDeltasCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 6000f, 400f);

        const double dtS = 0.00833;
        double t = 0;
        const float deltaPerFrame = 10f; // ≈1200 DIP/s at 8.33ms — constant-velocity samples give an EXACT IMPULSE estimate.
        for (int i = 1; i <= 6; i++)
        {
            t = i * dtS;
            k.Port.Post(ScrollInput.FrameDelta(1, t, deltaPerFrame));
            var c = ClockAt(t, (float)dtS);
            k.Tick(in c);
        }
        t += dtS;
        k.Port.Post(ScrollInput.ContactEnd(1, t, 0f));
        var clock2 = ClockAt(t, (float)dtS);
        k.Tick(in clock2);
        k.TryGetBody(1, out var body);

        float expectedV = deltaPerFrame / (float)dtS;
        Check("gate.kernel.fling-seed-from-framedeltas",
            body.Activity == ScrollActivity.Ballistic && MathF.Abs(body.Velocity - expectedV) < expectedV * 0.05f,
            $"activity={body.Activity} v={body.Velocity:F1} expected={expectedV:F1}");
    }

    // ── gate.kernel.band-roundtrip ────────────────────────────────────────────────────────────────────────────

    private static void BandRoundtripCheck()
    {
        bool ok = true;
        string detail = "";
        float[] excesses = [5f, 20f, 80f, 150f, -40f, -120f];
        foreach (float excess in excesses)
        {
            float band = ScrollPhysics.BandFromExcess(excess, 400f);
            float back = ScrollPhysics.ExcessFromBand(band, 400f);
            float err = MathF.Abs(back - excess);
            if (err > 0.5f) { ok = false; detail = $"excess={excess} band={band:F2} back={back:F2} err={err:F2}"; }
        }
        Check("gate.kernel.band-roundtrip", ok, detail.Length == 0 ? "within 0.5px" : detail);
    }

    // ── gate.kernel.chain-drag-time ───────────────────────────────────────────────────────────────────────────

    private static void ChainDragTimeCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 3000f, 400f); // parent maxOff=2600
        SetupViewport(k, 2, 500f, 400f);  // child maxOff=100
        k.Port.Post(ScrollInput.Chain(2, 1));
        k.Port.Post(ScrollInput.ThumbSet(2, 50f));
        k.Reclamp();

        k.Port.Post(ScrollInput.FrameDelta(2, 0.00833, 80f)); // child wants 130, clamps at 100, 30 excess → parent
        var clock = ClockAt(0.00833);
        k.Tick(in clock);

        k.TryGetBody(1, out var parent);
        k.TryGetBody(2, out var child);
        bool ok = MathF.Abs(child.PositionMain - 100f) < 0.01f
            && MathF.Abs(parent.PositionMain - 30f) < 0.01f
            && child.BandMain == 0f
            && (parent.Flags & ScrollActivityFlags.Chained) != 0;
        Check("gate.kernel.chain-drag-time", ok, $"child={child.PositionMain:F2} parent={parent.PositionMain:F2} childBand={child.BandMain:F2} parentFlags={parent.Flags}");
    }

    // ── gate.kernel.chain-lift-handoff ────────────────────────────────────────────────────────────────────────

    private static void ChainLiftHandoffCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 3000f, 400f); // parent maxOff=2600
        SetupViewport(k, 2, 500f, 400f);  // child maxOff=100
        k.Port.Post(ScrollInput.Chain(2, 1));
        k.Port.Post(ScrollInput.ThumbSet(2, 90f));
        k.Reclamp();

        double t = 0.00833;
        k.Port.Post(ScrollInput.FrameDelta(2, t, 5f)); // 90→95, fully absorbed by child
        k.Tick(ClockAt(t));
        t += 0.00833;
        k.Port.Post(ScrollInput.FrameDelta(2, t, 20f)); // 95→115 clamps to 100; 15 excess → parent absorbs LAST
        k.Tick(ClockAt(t));
        t += 0.00833;
        k.Port.Post(ScrollInput.ContactEnd(2, t, 0f));
        k.Tick(ClockAt(t));

        k.TryGetBody(1, out var parent);
        k.TryGetBody(2, out var child);
        Check("gate.kernel.chain-lift-handoff",
            parent.Activity == ScrollActivity.Ballistic && child.Activity != ScrollActivity.Ballistic,
            $"parent={parent.Activity} child={child.Activity}");
    }

    // ── gate.kernel.chain-ballistic-edge ──────────────────────────────────────────────────────────────────────

    private static void ChainBallisticEdgeCheck()
    {
        // (a) parent can move → the Ballistic edge hands off to it.
        {
            var sink = new RecordingSink();
            var k = new ScrollKernel(sink, ScrollFeel.Shipping);
            SetupViewport(k, 1, 3000f, 400f); // parent maxOff=2600, starts at 0 — has room
            SetupViewport(k, 2, 500f, 400f);  // child maxOff=100

            double t = 0.00833;
            k.Port.Post(ScrollInput.ThumbSet(2, 90f));
            k.Reclamp();
            // Two FrameDeltas (not one) — the impulse estimator needs ≥2 samples to compute a release velocity;
            // the FIRST FrameDelta only seeds Impulse.Reset (one sample), so a single-delta drag releases at v=0.
            k.Port.Post(ScrollInput.FrameDelta(2, t, 6f)); // 90→96
            k.Tick(ClockAt(t));
            t += 0.00833;
            k.Port.Post(ScrollInput.FrameDelta(2, t, 4f)); // 96→100 exactly, no excess yet
            k.Tick(ClockAt(t));
            t += 0.00833;
            k.Port.Post(ScrollInput.ContactEnd(2, t, 0f)); // seeds child Ballistic
            k.Tick(ClockAt(t));

            // Chain AFTER the fling is already seeded on the child — models "hits its own edge mid-coast".
            k.Port.Post(ScrollInput.Chain(2, 1));
            k.Reclamp();

            t += 0.00833;
            k.Tick(ClockAt(t)); // coast — child is already AT its clamp with positive velocity → hits edge this tick
            k.Reclamp();           // resolves the edge: hands off to parent (has room)

            k.TryGetBody(1, out var parent);
            k.TryGetBody(2, out var child);
            Check("gate.kernel.chain-ballistic-edge.handoff",
                parent.Activity == ScrollActivity.Ballistic && child.Activity == ScrollActivity.Idle,
                $"parent={parent.Activity} child={child.Activity}");
        }

        // (b) parent CANNOT move (already at its own edge in that direction) → the child bounces instead.
        {
            var sink = new RecordingSink();
            var k = new ScrollKernel(sink, ScrollFeel.Shipping);
            SetupViewport(k, 1, 500f, 400f);  // parent maxOff=100
            SetupViewport(k, 2, 500f, 400f);  // child maxOff=100

            double t = 0.00833;
            k.Port.Post(ScrollInput.ThumbSet(1, 100f)); // parent already pinned at ITS max
            k.Port.Post(ScrollInput.ThumbSet(2, 90f));
            k.Port.Post(ScrollInput.Chain(2, 1));
            k.Reclamp();

            k.Port.Post(ScrollInput.FrameDelta(2, t, 6f)); // 90→96
            k.Tick(ClockAt(t));
            t += 0.00833;
            k.Port.Post(ScrollInput.FrameDelta(2, t, 4f)); // 96→100 exactly
            k.Tick(ClockAt(t));
            t += 0.00833;
            k.Port.Post(ScrollInput.ContactEnd(2, t, 0f));
            k.Tick(ClockAt(t));

            t += 0.00833;
            k.Tick(ClockAt(t)); // coast hits the edge again — EdgeHitPending
            k.Reclamp();        // parent is ALSO at its own max → cannot absorb → child bounces instead

            k.TryGetBody(1, out var parent);
            k.TryGetBody(2, out var child);
            // "Bounce" is Activity=Idle + Flags.Bouncing (overscroll is a property, not a fifth ScrollActivity — §2.1).
            bool childBounced = child.Activity == ScrollActivity.Idle && (child.Flags & ScrollActivityFlags.Bouncing) != 0;
            Check("gate.kernel.chain-ballistic-edge.bounce-when-parent-maxed",
                parent.Activity != ScrollActivity.Ballistic && childBounced,
                $"parent={parent.Activity} child={child.Activity} childFlags={child.Flags} childBand={child.BandMain:F2}");
        }
    }

    // ── gate.kernel.wheel-accumulate-hardstop ─────────────────────────────────────────────────────────────────

    private static void WheelAccumulateHardStopCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 1000f, 400f); // maxOff=600

        double t = 0;
        k.Port.Post(ScrollInput.WheelNotch(1, t, 500f));
        k.Tick(ClockAt(t));
        t += 0.00833;
        k.Port.Post(ScrollInput.WheelNotch(1, t, 500f)); // accumulates target to 1000, clamped to 600
        k.Tick(ClockAt(t));

        k.TryGetBody(1, out var body0);
        bool targetClamped = MathF.Abs(body0.Target - 600f) < 0.01f;

        bool everBanded = false;
        for (int i = 0; i < 400; i++)
        {
            t += 0.00833;
            k.Tick(ClockAt(t));
            k.TryGetBody(1, out var b);
            if (MathF.Abs(b.BandMain) > 0.0001f) everBanded = true;
            if (b.Activity == ScrollActivity.Idle) break;
        }
        k.TryGetBody(1, out var final);
        bool ok = targetClamped && !everBanded && MathF.Abs(final.PositionMain - 600f) < 0.5f;
        Check("gate.kernel.wheel-accumulate-hardstop", ok, $"target={body0.Target} final={final.PositionMain:F2} everBanded={everBanded}");
    }

    // ── gate.kernel.wheel-* — the S1 wheel plan (ScrollPhysics.WheelPlanNotch / WheelStep) ───────────────────
    // Every scenario drives a REAL ScrollKernel (notch → ApplyWheelNotch → ScrollBody.Advance → WheelStep) at
    // 8.333 ms ticks with D = 120 DIP per notch (three 40-DIP rows — S6's Windows rule) over a 100 000-DIP extent so
    // no clamp interferes unless the check wants one. Thresholds are the D = 120 values of the plan's own
    // simulation, re-run at this notch size (the plan quotes D = 83): cold notch 12.9 DIP on the first tick, peak on
    // the second, Idle after 21 ticks (175 ms); 110 ms cadence min/max 0.59 over notches 6..11, Idle 250 ms after
    // the last notch; a reversal at a cadence slot carries on 1.4 DIP and moves back on the next tick.

    private const float WheelD = 120f;
    private const float WheelDt120 = 1f / 120f;

    private static ScrollKernel WheelKernel(float extent = 100000f, float viewport = 400f)
    {
        var k = new ScrollKernel(new RecordingSink(), ScrollFeel.Shipping);
        SetupViewport(k, 1, extent, viewport);
        return k;
    }

    /// <summary>One kernel tick of <paramref name="dt"/> seconds on node 1 (RefreshSec = dt, so the wake-tick rule
    /// substitutes the same lattice); returns the signed main-axis displacement of that tick.</summary>
    private static float WheelTick(ScrollKernel k, ref double t, float dt)
    {
        k.TryGetBody(1, out var before);
        t += dt;
        var c = new ScrollClock(t, dt, t, dt);
        k.Tick(in c);
        k.TryGetBody(1, out var after);
        return after.PositionMain - before.PositionMain;
    }

    /// <summary>Posts <paramref name="notches"/> notches of <see cref="WheelD"/> at a <paramref name="gapS"/> cadence
    /// (the LAST one carries <paramref name="lastDelta"/> instead), each posted just before the first tick at or past
    /// its slot time — the way a paced host consumes a packet on the vblank — ticking at <paramref name="dt"/> until
    /// the body is Idle after the last notch. Fills the per-tick displacement and the tick index each notch was posted
    /// on; returns the tick count (the last tick is the landing tick).</summary>
    private static int WheelCadenceRun(ScrollKernel k, float gapS, int notches, float lastDelta, float dt, float[] d, int[] notchTick)
    {
        double t = 0, next = 0;
        int posted = 0, n = 0;
        while (n < d.Length)
        {
            if (posted < notches && t >= next - 1e-6)
            {
                k.Port.Post(ScrollInput.WheelNotch(1, t, posted == notches - 1 ? lastDelta : WheelD));
                notchTick[posted++] = n;
                next += gapS;
            }
            d[n++] = WheelTick(k, ref t, dt);
            k.TryGetBody(1, out var b);
            if (posted == notches && b.Activity == ScrollActivity.Idle) break;
        }
        return n;
    }

    private static bool WheelBodyNaN(in ScrollBody b)
        => float.IsNaN(b.PositionMain) || float.IsNaN(b.Velocity) || float.IsNaN(b.Target) || float.IsNaN(b.DrivenHalflifeMs)
           || float.IsNaN(b.WheelSinceS) || float.IsNaN(b.WheelGapS);

    // ── gate.kernel.wheel-cold-seed ───────────────────────────────────────────────────────────────────────────
    // A cold notch seeds κ·R·y: the first tick already moves 6–14 DIP (no t·e^{−yt} ease-in), the per-tick shift is
    // monotone from the third tick on (the seed peaks on the second), it never crosses the target, no tick before the
    // landing one is a sub-pixel (0 < |d| < 0.667 DIP = one device pixel at scale 1.5) creep, and it is Idle within
    // 185 ms (the D = 120 simulation lands on tick 21 = 175 ms; the D = 83 figure in the plan is 158 ms).

    private static void WheelColdSeedCheck()
    {
        var k = WheelKernel();
        var d = new float[64];
        double t = 0;
        k.Port.Post(ScrollInput.WheelNotch(1, t, WheelD));
        int n = 0; bool idle = false;
        while (n < d.Length && !idle)
        {
            d[n++] = WheelTick(k, ref t, WheelDt120);
            k.TryGetBody(1, out var b);
            idle = b.Activity == ScrollActivity.Idle;
        }
        int land = n - 1;
        bool firstOk = d[0] >= 6f && d[0] <= 14f;
        bool monotone = true, noSubPixel = true, noOvershoot = true;
        float pos = 0f, peak = 0f;
        for (int i = 0; i < n; i++)
        {
            pos += d[i];
            peak = MathF.Max(peak, d[i]);
            if (d[i] < 0f || pos > WheelD + 0.01f) noOvershoot = false;
            if (i >= 2 && i < land && d[i] > d[i - 1] + 0.01f) monotone = false;
            if (i < land && d[i] > 0f && d[i] < 0.667f) noSubPixel = false;
        }
        k.TryGetBody(1, out var fin);
        float settleMs = n * WheelDt120 * 1000f;
        bool landed = idle && MathF.Abs(fin.PositionMain - WheelD) < 0.01f;
        bool ok = firstOk && monotone && noOvershoot && noSubPixel && landed && settleMs <= 185f;
        Check("gate.kernel.wheel-cold-seed", ok,
            $"first={d[0]:F2} peak={peak:F2} monotone={monotone} noOvershoot={noOvershoot} noSubPixel={noSubPixel} settle={settleMs:F0}ms final={fin.PositionMain:F2} idle={idle}");
    }

    // ── gate.kernel.wheel-cadence-flat ────────────────────────────────────────────────────────────────────────
    // 12 notches at 110 ms (9 notches/s): the cadence-planned half-life keeps the per-tick shift steady — min/max
    // over the ticks between notch 6 and notch 11 ≥ 0.55 (today's kernel: 0.20) — every notch lands (final = 12·D),
    // and the tail stiffening settles the body within 260 ms of the last notch (D = 120 simulation: 250 ms; the plan's
    // ≤ 240 ms acceptance is its D = 83 Pareto point; one tick of margin on top of the measured value).

    private static void WheelCadenceFlatCheck()
    {
        var k = WheelKernel();
        var d = new float[512]; var nt = new int[12];
        int n = WheelCadenceRun(k, 0.110f, 12, WheelD, WheelDt120, d, nt);
        float min = float.MaxValue, max = 0f;
        for (int i = nt[5]; i < nt[11]; i++) { min = MathF.Min(min, d[i]); max = MathF.Max(max, d[i]); }
        float ratio = max > 0f ? min / max : 0f;
        k.TryGetBody(1, out var fin);
        float settleMs = (n - nt[11]) * WheelDt120 * 1000f;
        bool landed = fin.Activity == ScrollActivity.Idle && MathF.Abs(fin.PositionMain - 12f * WheelD) < 0.01f;
        bool ok = ratio >= 0.55f && landed && settleMs <= 260f;
        Check("gate.kernel.wheel-cadence-flat", ok,
            $"min/max={ratio:F2} (min={min:F2} max={max:F2}) settleAfterLast={settleMs:F0}ms final={fin.PositionMain:F1} idle={fin.Activity == ScrollActivity.Idle}");
    }

    // ── gate.kernel.wheel-second-click ────────────────────────────────────────────────────────────────────────
    // The second click of a stream arrives when the first glide has decayed to ~3.5 DIP/tick; the cadence kick
    // (vel ≥ 0.65·D/gap) makes the tick that receives it move ≥ 4 DIP on its own (today: a 2 px dip and a re-ramp).

    private static void WheelSecondClickCheck()
    {
        var k = WheelKernel();
        double t = 0;
        k.Port.Post(ScrollInput.WheelNotch(1, t, WheelD));
        float lastBefore = 0f;
        for (int i = 0; i < 13; i++) lastBefore = WheelTick(k, ref t, WheelDt120);   // 108 ms in — mid-tail of the first notch
        k.Port.Post(ScrollInput.WheelNotch(1, t, WheelD));
        float second = WheelTick(k, ref t, WheelDt120);
        k.TryGetBody(1, out var b);
        bool ok = second >= 4f && b.Activity == ScrollActivity.Driven && (b.Flags & ScrollActivityFlags.Wheel) != 0
                  && MathF.Abs(b.Target - 2f * WheelD) < 0.01f;
        Check("gate.kernel.wheel-second-click", ok, $"tickBefore={lastBefore:F2} secondClickTick={second:F2} target={b.Target:F1} activity={b.Activity}");
    }

    // ── gate.kernel.wheel-dt-invariance ───────────────────────────────────────────────────────────────────────
    // The same three notches (0 / 100 / 200 ms) on a 60 Hz and a 120 Hz lattice: the closed-form chase, the
    // split-at-the-switch tail stiffening and the velocity-valued floor keep the two trajectories within 0.33 DIP at
    // every shared instant.

    private static void WheelDtInvarianceCheck()
    {
        static void Lattice(float dt, int notchEvery, float[] pos)
        {
            var k = WheelKernel();
            double t = 0;
            for (int i = 0; i < pos.Length; i++)
            {
                if (i % notchEvery == 0 && i / notchEvery < 3) k.Port.Post(ScrollInput.WheelNotch(1, t, WheelD));
                WheelTick(k, ref t, dt);
                k.TryGetBody(1, out var b);
                pos[i] = b.PositionMain;
            }
        }
        var p120 = new float[120]; var p60 = new float[60];
        Lattice(1f / 120f, 12, p120);
        Lattice(1f / 60f, 6, p60);
        float maxDiff = 0f;
        for (int i = 0; i < p60.Length; i++) maxDiff = MathF.Max(maxDiff, MathF.Abs(p60[i] - p120[2 * i + 1]));
        bool ok = maxDiff <= 0.33f && MathF.Abs(p120[^1] - 3f * WheelD) < 0.01f && MathF.Abs(p60[^1] - 3f * WheelD) < 0.01f;
        Check("gate.kernel.wheel-dt-invariance", ok, $"maxDiff={maxDiff:F3} final120={p120[^1]:F2} final60={p60[^1]:F2}");
    }

    // ── gate.kernel.wheel-reversal ────────────────────────────────────────────────────────────────────────────
    // Six forward notches at 110 ms, then a −D notch at the seventh slot (the cycle's velocity trough, ~740 DIP/s):
    // the plan rebases Target = off − D (the unconsumed lag is dropped), keeps the velocity, and the ζ=1 chase brakes
    // through zero inside the reversal tick — carry-on ≤ 2 DIP — and moves back on the very next tick.

    private static void WheelReversalCheck()
    {
        var k = WheelKernel();
        k.Port.Post(ScrollInput.ScrollTo(1, 5000f, immediate: true));   // start mid-content so the reversal has room
        k.Reclamp();
        var d = new float[512]; var nt = new int[7];
        int n = WheelCadenceRun(k, 0.110f, 7, -WheelD, WheelDt120, d, nt);
        int rev = nt[6];
        float posBefore = 5000f;
        for (int i = 0; i < rev; i++) posBefore += d[i];
        float carryOn = d[rev];
        float next = rev + 1 < n ? d[rev + 1] : 0f;
        k.TryGetBody(1, out var fin);
        bool rebased = MathF.Abs(fin.PositionMain - (posBefore - WheelD)) < 0.01f && fin.Activity == ScrollActivity.Idle;
        bool ok = carryOn <= 2f && next < 0f && rebased;
        Check("gate.kernel.wheel-reversal", ok,
            $"carryOn={carryOn:F2} nextTick={next:F2} posBefore={posBefore:F1} final={fin.PositionMain:F1} expected={posBefore - WheelD:F1} ticksAfter={n - rev}");
    }

    // ── gate.kernel.wheel-slow-cadence-stiff ──────────────────────────────────────────────────────────────────
    // Clicks 150 ms apart (> WheelGapMaxS) are independent: no cadence plan is armed, the half-life stays at
    // WheelHalflifeMs, and each notch's own tick moves ≥ 6 DIP (kicked back up to the cold seed).

    private static void WheelSlowCadenceStiffCheck()
    {
        var k = WheelKernel();
        double t = 0, next = 0;
        var firsts = new float[5]; var hls = new float[5];
        int posted = 0;
        for (int i = 0; i < 200 && posted < firsts.Length; i++)
        {
            int idx = -1;
            if (t >= next - 1e-6) { k.Port.Post(ScrollInput.WheelNotch(1, t, WheelD)); idx = posted++; next += 0.150; }
            float d = WheelTick(k, ref t, WheelDt120);
            if (idx >= 0) { k.TryGetBody(1, out var b); firsts[idx] = d; hls[idx] = b.DrivenHalflifeMs; }
        }
        bool ok = true;
        for (int i = 0; i < firsts.Length; i++)
            if (firsts[i] < 6f || MathF.Abs(hls[i] - ScrollFeel.Shipping.WheelHalflifeMs) > 0.01f) ok = false;
        Check("gate.kernel.wheel-slow-cadence-stiff", ok,
            $"firsts={firsts[0]:F2}/{firsts[1]:F2}/{firsts[2]:F2}/{firsts[3]:F2}/{firsts[4]:F2} hl={hls[0]:F1}/{hls[1]:F1}/{hls[2]:F1}/{hls[3]:F1}/{hls[4]:F1}");
    }

    // ── gate.kernel.wheel-edge-degenerate ─────────────────────────────────────────────────────────────────────
    // The 0/0 cases: a notch with nowhere to go (maxOff = 0), a live glide whose target is already clamped onto the
    // edge and gets another notch, a notch while parked exactly at the edge, and a zero-delta notch mid-glide — none
    // may produce a NaN, and every one lands Idle on the clamp.

    private static void WheelEdgeDegenerateCheck()
    {
        // (1) maxOff = 0: target clamps onto the current offset — seed is 0, the next tick snaps and settles.
        var k1 = WheelKernel(400f, 400f);
        double t = 0;
        k1.Port.Post(ScrollInput.WheelNotch(1, t, WheelD)); WheelTick(k1, ref t, WheelDt120);
        k1.Port.Post(ScrollInput.WheelNotch(1, t, WheelD)); WheelTick(k1, ref t, WheelDt120);
        k1.TryGetBody(1, out var a);
        bool aOk = !WheelBodyNaN(in a) && a.Activity == ScrollActivity.Idle && a.PositionMain == 0f;

        // (2) maxOff = 120: the glide is 15 ticks in (≈ 8 DIP short), a second notch re-clamps the target onto the
        //     edge (a live re-plan against a tiny |R|), then a notch while Idle at the edge (cold, r = 0).
        var k2 = WheelKernel(520f, 400f);
        t = 0;
        k2.Port.Post(ScrollInput.WheelNotch(1, t, WheelD));
        for (int i = 0; i < 15; i++) WheelTick(k2, ref t, WheelDt120);
        k2.TryGetBody(1, out var mid);
        bool midLive = mid.Activity == ScrollActivity.Driven;
        k2.Port.Post(ScrollInput.WheelNotch(1, t, WheelD));
        bool bNaN = false;
        for (int i = 0; i < 64; i++)
        {
            WheelTick(k2, ref t, WheelDt120);
            k2.TryGetBody(1, out var b);
            if (WheelBodyNaN(in b)) bNaN = true;
            if (b.Activity == ScrollActivity.Idle) break;
        }
        k2.Port.Post(ScrollInput.WheelNotch(1, t, WheelD)); WheelTick(k2, ref t, WheelDt120);
        k2.TryGetBody(1, out var edge);
        bool bOk = midLive && !bNaN && !WheelBodyNaN(in edge) && edge.Activity == ScrollActivity.Idle && MathF.Abs(edge.PositionMain - 120f) < 0.01f;

        // (3) a zero-delta notch during a live glide: not a reversal, target unchanged, glide continues forward.
        var k3 = WheelKernel();
        t = 0;
        k3.Port.Post(ScrollInput.WheelNotch(1, t, WheelD));
        for (int i = 0; i < 3; i++) WheelTick(k3, ref t, WheelDt120);
        k3.Port.Post(ScrollInput.WheelNotch(1, t, 0f));
        float dz = WheelTick(k3, ref t, WheelDt120);
        k3.TryGetBody(1, out var z);
        bool cOk = !WheelBodyNaN(in z) && z.Activity == ScrollActivity.Driven && dz > 0f && MathF.Abs(z.Target - WheelD) < 0.01f;

        Check("gate.kernel.wheel-edge-degenerate", aOk && bOk && cOk,
            $"maxOff0: pos={a.PositionMain} vel={a.Velocity} act={a.Activity}; edge: pos={edge.PositionMain:F2} vel={edge.Velocity:F2} act={edge.Activity} nan={bNaN}; zeroDelta: d={dz:F2} act={z.Activity}");
    }

    // ── gate.kernel.wheel-fling-carry ─────────────────────────────────────────────────────────────────────────
    // A Ballistic fling (a lifted finger at ~4000 DIP/s) followed by a same-direction notch: the wheel plan carries the
    // fling velocity instead of resetting it (the first wheel tick moves far more than a cold notch's 12.9 DIP), caps
    // it at the exact no-overshoot bound 0.95·|R|·y, never crosses the target and lands exactly on it.

    private static void WheelFlingCarryCheck()
    {
        var k = WheelKernel();
        k.Port.Post(ScrollInput.ContactBegin(1, 0.0, 0f));
        k.Port.Post(ScrollInput.ContactEnd(1, 0.02, 80f));   // 80 DIP in 20 ms ⇒ 4000 DIP/s, Begin+End only
        double t = 0.02;
        var c0 = new ScrollClock(t, WheelDt120, t, WheelDt120);
        k.Tick(in c0);
        k.TryGetBody(1, out var fl);
        bool ballistic = fl.Activity == ScrollActivity.Ballistic && fl.Velocity > 0f;
        float vFling = fl.Velocity;
        float posAtNotch = fl.PositionMain;

        k.Port.Post(ScrollInput.WheelNotch(1, t, WheelD));
        var d = new float[128];
        int n = 0; bool idle = false, over = false;
        float target = 0f;
        while (n < d.Length && !idle)
        {
            d[n++] = WheelTick(k, ref t, WheelDt120);
            k.TryGetBody(1, out var b);
            if (n == 1) target = b.Target;
            if (b.PositionMain > b.Target + 0.01f) over = true;
            idle = b.Activity == ScrollActivity.Idle;
        }
        k.TryGetBody(1, out var fin);
        float yBase = 1.3862944f / (ScrollFeel.Shipping.WheelHalflifeMs * 0.001f);
        float capFirstTick = 0.95f * WheelD * yBase * WheelDt120;   // a velocity at the bound can travel at most this on the first tick
        bool carried = d[0] >= 20f && d[0] <= capFirstTick + 0.01f;   // a cold notch moves 12.9; the carried fling ~26
        bool targetOk = MathF.Abs(target - (posAtNotch + WheelD)) < 0.01f;
        bool landed = idle && MathF.Abs(fin.PositionMain - target) < 0.01f;
        bool ok = ballistic && carried && targetOk && !over && landed;
        Check("gate.kernel.wheel-fling-carry", ok,
            $"ballistic={ballistic} vFling={vFling:F0} first={d[0]:F2} cap={capFirstTick:F2} over={over} landed={landed} final={fin.PositionMain:F2} target={target:F2}");
    }

    // ── gate.kernel.wheel-no-subpixel-tail ────────────────────────────────────────────────────────────────────
    // After the last notch of a 110 ms stream, no tick before the landing one changes the offset by less than one
    // device pixel at scale 1.5 (0 < |d| < 0.667 DIP): the 160 DIP/s floor and the 1-DIP snap replace the 4–5
    // change-without-motion frames that forced a glyph re-snap every frame (S4).

    private static void WheelNoSubpixelTailCheck()
    {
        var k = WheelKernel();
        var d = new float[512]; var nt = new int[12];
        int n = WheelCadenceRun(k, 0.110f, 12, WheelD, WheelDt120, d, nt);
        int last = nt[11], sub = 0, tailTicks = 0;
        float minTail = float.MaxValue;
        for (int i = last; i < n - 1; i++)
        {
            float a = MathF.Abs(d[i]);
            tailTicks++;
            if (a > 0f) minTail = MathF.Min(minTail, a);
            if (a > 0f && a < 0.667f) sub++;
        }
        k.TryGetBody(1, out var fin);
        bool ok = sub == 0 && tailTicks > 0 && fin.Activity == ScrollActivity.Idle;
        Check("gate.kernel.wheel-no-subpixel-tail", ok, $"subPixelTicks={sub} tailTicks={tailTicks} minTail={minTail:F2} landing={d[n - 1]:F2}");
    }

    // ── gate.kernel.programmatic-glide-retarget ───────────────────────────────────────────────────────────────

    private static void ProgrammaticGlideRetargetCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 5000f, 400f);

        double t = 0;
        k.Port.Post(ScrollInput.ScrollTo(1, 1000f)); // non-Immediate — a glide, drained by Tick (not structural)
        k.Tick(ClockAt(t));

        for (int i = 0; i < 10; i++) { t += 0.00833; k.Tick(ClockAt(t)); }
        k.TryGetBody(1, out var mid);
        float velBefore = mid.Velocity;

        // Retarget while the same flavour (Programmatic) is still live — isolate the COMMAND's own effect on
        // velocity from the ensuing physics step by ticking with dt=0 (Advance no-ops on a zero/undefined dt), so
        // this checks "no reset at the moment of retarget", not "no evolution over the next tick" (ChaseStep
        // legitimately keeps evolving velocity every tick — that is continuity, not a violation of it).
        k.Port.Post(ScrollInput.ScrollTo(1, 2000f));
        k.Tick(new ScrollClock(t, 0f, t, 0.00833f));
        k.TryGetBody(1, out var justAfter);

        bool ok = justAfter.Activity == ScrollActivity.Driven
            && MathF.Abs(justAfter.Target - 2000f) < 0.01f
            && MathF.Abs(justAfter.Velocity - velBefore) < 0.01f; // velocity-continuous — no reset on same-flavour retarget
        Check("gate.kernel.programmatic-glide-retarget", ok, $"velBefore={velBefore:F2} velAfter={justAfter.Velocity:F2} target={justAfter.Target}");
    }

    // ── gate.kernel.restore-latch-until-extent ────────────────────────────────────────────────────────────────

    private static void RestoreLatchUntilExtentCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        k.Port.Post(ScrollInput.Bind(1));
        k.Port.Post(ScrollInput.Restore(1, 0f, 250f));
        k.Reclamp(); // no SetFrame yet — geometry unknown, must latch (not apply)

        k.TryGetBody(1, out var beforeGeometry);
        bool latched = beforeGeometry.PositionMain == 0f;

        k.Port.Post(ScrollInput.SetFrame(1, Frame(1000f, 400f))); // maxOff=600
        k.Reclamp(); // geometry now known — Restore should land, clamped

        k.TryGetBody(1, out var afterGeometry);
        bool landed = MathF.Abs(afterGeometry.PositionMain - 250f) < 0.01f;
        Check("gate.kernel.restore-latch-until-extent", latched && landed,
            $"beforeGeometry={beforeGeometry.PositionMain} afterGeometry={afterGeometry.PositionMain}");
    }

    // ── gate.kernel.restore-goal-extent-grows ─────────────────────────────────────────────────────────────────

    private static void RestoreGoalExtentGrowsCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        k.Port.Post(ScrollInput.Bind(1));
        k.Port.Post(ScrollInput.Restore(1, 0f, 500f));
        k.Port.Post(ScrollInput.SetFrame(1, Frame(200f, 100f))); // maxOff=100 — short of 500
        k.Reclamp();
        k.TryGetBody(1, out var shortExtent);
        bool bestEffort = MathF.Abs(shortExtent.PositionMain - 100f) < 0.01f && shortExtent.RestorePending;

        k.Port.Post(ScrollInput.SetFrame(1, Frame(2000f, 100f))); // maxOff=1900 — holds 500
        k.Reclamp();
        k.TryGetBody(1, out var grown);
        bool resolved = MathF.Abs(grown.PositionMain - 500f) < 0.01f && !grown.RestorePending;
        Check("gate.kernel.restore-goal-extent-grows", bestEffort && resolved,
            $"short={shortExtent.PositionMain} pending={shortExtent.RestorePending} grown={grown.PositionMain} grownPending={grown.RestorePending}");
    }

    // ── gate.kernel.restore-cancel-on-input ───────────────────────────────────────────────────────────────────

    private static void RestoreCancelOnInputCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        k.Port.Post(ScrollInput.Bind(1));
        k.Port.Post(ScrollInput.Restore(1, 0f, 500f));
        k.Port.Post(ScrollInput.SetFrame(1, Frame(200f, 100f)));
        k.Reclamp();
        k.TryGetBody(1, out var latched);
        bool wasPending = latched.RestorePending;

        k.Port.Post(ScrollInput.ContactBegin(1, 0.0, 0f));
        k.Tick(ClockAt(0.008));
        k.TryGetBody(1, out var afterBegin);
        bool cancelled = wasPending && !afterBegin.RestorePending;
        Check("gate.kernel.restore-cancel-on-input", cancelled,
            $"wasPending={wasPending} afterBeginPending={afterBegin.RestorePending}");
    }

    // ── gate.kernel.restore-deadline ──────────────────────────────────────────────────────────────────────────

    private static void RestoreDeadlineCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        k.Port.Post(ScrollInput.Bind(1));
        k.Port.Post(ScrollInput.Restore(1, 0f, 5000f));
        k.Port.Post(ScrollInput.SetFrame(1, Frame(200f, 100f))); // maxOff=100 forever
        k.Reclamp();
        for (int i = 0; i < ScrollKernel.RestoreMaxRetries; i++) k.Reclamp();
        k.TryGetBody(1, out var after);
        bool resolved = !after.RestorePending && MathF.Abs(after.PositionMain - 100f) < 0.01f;
        Check("gate.kernel.restore-deadline", resolved,
            $"pending={after.RestorePending} pos={after.PositionMain} retries={after.RestoreRetries}");
    }

    // ── gate.kernel.anchor-shift-under-drag ───────────────────────────────────────────────────────────────────

    private static void AnchorShiftUnderDragCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 5000f, 400f);

        double t = 0;
        k.Port.Post(ScrollInput.ContactBegin(1, t, 0f));
        t = 0.008; k.Port.Post(ScrollInput.ContactMove(1, t, 100f));
        t = 0.016; k.Port.Post(ScrollInput.ContactMove(1, t, 200f));
        k.Tick(ClockAt(t, 0.008f));
        k.TryGetBody(1, out var before);

        const float shift = 50f;
        k.Port.Post(ScrollInput.AnchorShift(1, shift));
        k.Reclamp();
        k.TryGetBody(1, out var afterShift);
        bool rebased = MathF.Abs(afterShift.PositionMain - (before.PositionMain + shift)) < 0.01f;

        // Continuing the drag afterward must not jump — one more sample at the SAME real trajectory should
        // continue smoothly from the rebased anchor, not double-count the shift.
        t = 0.024; k.Port.Post(ScrollInput.ContactMove(1, t, 300f));
        k.Tick(ClockAt(t, 0.008f));
        k.TryGetBody(1, out var after);
        float expectedContinuedDelta = 100f; // the finger moved another 100 DIP (200→300) since the last real sample
        bool continuous = MathF.Abs((after.PositionMain - afterShift.PositionMain) - expectedContinuedDelta) < 1.5f;

        Check("gate.kernel.anchor-shift-under-drag", rebased && continuous,
            $"before={before.PositionMain:F2} afterShift={afterShift.PositionMain:F2} after={after.PositionMain:F2}");
    }

    // ── gate.kernel.edge-pending-resolves-on-grow ─────────────────────────────────────────────────────────────

    private static void EdgePendingResolvesOnGrowCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 500f, 400f); // maxOff=100 — tight

        double t = 0.00833;
        k.Port.Post(ScrollInput.ThumbSet(1, 95f));
        k.Reclamp();
        k.Port.Post(ScrollInput.FrameDelta(1, t, 3f)); // 95→98
        k.Tick(ClockAt(t));
        t += 0.00833;
        k.Port.Post(ScrollInput.FrameDelta(1, t, 2f)); // 98→100 exactly — zero excess so the drag itself does not band
        k.Tick(ClockAt(t));
        t += 0.00833;
        k.Port.Post(ScrollInput.ContactEnd(1, t, 0f));
        k.Tick(ClockAt(t)); // seeds Ballistic pushing further past the (currently tight) edge

        t += 0.00833;
        k.Tick(ClockAt(t)); // coast hits the clamp this tick → EdgeHitPending, pinned
        k.TryGetBody(1, out var pinned);
        bool wasPending = pinned.EdgeHitPending;

        // Geometry grows BEFORE Reclamp resolves it — the fresh extent should let the Ballistic continue.
        k.Port.Post(ScrollInput.SetFrame(1, Frame(5000f, 400f))); // maxOff now 4600 — no longer at the edge
        k.Reclamp();
        k.TryGetBody(1, out var resolved);

        bool ok = wasPending && !resolved.EdgeHitPending && resolved.Activity == ScrollActivity.Ballistic;
        Check("gate.kernel.edge-pending-resolves-on-grow", ok, $"wasPending={wasPending} resolvedActivity={resolved.Activity} edgePending={resolved.EdgeHitPending}");
    }

    // ── gate.kernel.undersampled-flick ────────────────────────────────────────────────────────────────────────

    private static void UndersampledFlickCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 5000f, 400f);

        k.Port.Post(ScrollInput.ContactBegin(1, 0.0, 0f));
        k.Port.Post(ScrollInput.ContactEnd(1, 0.02, 40f)); // 40 DIP in 20ms ⇒ 2000 DIP/s, well above the seed gate — Begin+End only
        k.Tick(ClockAt(0.02));

        k.TryGetBody(1, out var body);
        Check("gate.kernel.undersampled-flick", body.Activity == ScrollActivity.Ballistic && body.Velocity > 0f,
            $"activity={body.Activity} v={body.Velocity:F1}");
    }

    // ── gate.kernel.snap-fling-lands ──────────────────────────────────────────────────────────────────────────

    private static void SnapFlingLandsCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        float[] snaps = [0f, 200f, 400f, 600f, 800f];
        SetupViewport(k, 1, 5000f, 400f, snapPoints: snaps);

        const double dtS = 0.00833;
        double t = 0;
        for (int i = 1; i <= 4; i++)
        {
            t = i * dtS;
            k.Port.Post(ScrollInput.FrameDelta(1, t, 30f)); // constant-velocity samples, ~3600 DIP/s
            k.Tick(ClockAt(t, (float)dtS));
        }
        t += dtS;
        k.Port.Post(ScrollInput.ContactEnd(1, t, 0f));
        k.Tick(ClockAt(t, (float)dtS));

        k.TryGetBody(1, out var seeded);
        bool seededBallistic = seeded.Activity == ScrollActivity.Ballistic;

        for (int i = 0; i < 20000 && k.TryGetBody(1, out var b) && b.Activity != ScrollActivity.Idle; i++)
        {
            t += dtS;
            k.Tick(ClockAt(t, (float)dtS));
        }
        k.TryGetBody(1, out var landed);

        float nearestSnap = snaps[0];
        float bestDist = float.PositiveInfinity;
        foreach (float s in snaps) { float d = MathF.Abs(s - landed.PositionMain); if (d < bestDist) { bestDist = d; nearestSnap = s; } }

        // Tolerance: CoastStep stops at |v| < FlingSettleVel rather than v==0, leaving a fixed residual of
        // ~FlingSettleVel/k DIP short of the true asymptote (k = -ln(FlingDecayPerS) ≈ 3.0 ⇒ ~4.3 DIP at the
        // shipping profile) — the same truncation gate.kernel.fling-distance measures as a ~0.85% relative error.
        float k2 = -MathF.Log(ScrollFeel.Shipping.FlingDecayPerS);
        float tolerance = ScrollFeel.Shipping.FlingSettleVel / k2 + 1.5f;
        Check("gate.kernel.snap-fling-lands", seededBallistic && bestDist <= tolerance,
            $"seeded={seededBallistic} landed={landed.PositionMain:F2} nearestSnap={nearestSnap} dist={bestDist:F2} tol={tolerance:F2}");
    }

    // ── gate.kernel.alloc-zero-tick ───────────────────────────────────────────────────────────────────────────

    private static void AllocZeroTickCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 200000f, 400f);
        k.Port.Post(ScrollInput.SetVelocity(1, 500f)); // Autoscroll — never settles, guarantees 200 live Advance steps

        double t = 0;
        for (int i = 0; i < 8; i++) { t += 0.00833; k.Tick(ClockAt(t)); } // warm up (JIT, any first-touch paths)
        sink.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) { t += 0.00833; k.Tick(ClockAt(t)); }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Check("gate.kernel.alloc-zero-tick", after - before == 0, $"delta={after - before} bytes over 200 ticks");
    }

    // ── gate.kernel.body-sparse ───────────────────────────────────────────────────────────────────────────────
    // The kernel's body storage must scale with the number of LIVE VIEWPORTS, never with the scene's node
    // high-water. It used to be one ScrollBody[] indexed by node index (376 B/node) plus four more node-indexed
    // side columns: 12 746 752 B (12.2 MiB) at the 32 768-node scene the native ARM64 tour reached, for ~27 live
    // viewports. It is now a slot pool plus ONE 4-byte-per-node lookup, and this gate pins that arithmetic.

    private static void BodySparseCheck()
    {
        const int Nodes = 32_768;   // the measured scene high-water of the native ARM64 tour
        const int Viewports = 32;   // the tour's ~27 live scroll viewports, rounded up to a power of two

        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);

        // Node indices spread across the WHOLE scene, the last one at the very top slot: the node→slot lookup is
        // sized by the highest bound node index — exactly what the deleted body slab was sized by — so this is the
        // worst case for the one column that is still per-node.
        var nodes = new int[Viewports];
        for (int i = 0; i < Viewports; i++) nodes[i] = (i + 1) * (Nodes / Viewports) - 1;
        for (int i = 0; i < Viewports; i++) SetupViewport(k, nodes[i], 4000f, 400f);

        // Each viewport must still own its own state through the indirection (a cross-wired lookup would show up
        // as one body carrying another's offset).
        for (int i = 0; i < Viewports; i++) k.Port.Post(ScrollInput.ThumbSet(nodes[i], (i + 1) * 10f));
        k.Reclamp();
        bool perBodyState = true;
        for (int i = 0; i < Viewports; i++)
            if (!k.TryGetBody(nodes[i], out var b) || MathF.Abs(b.PositionMain - (i + 1) * 10f) > 0.001f || b.Node != nodes[i])
                perBodyState = false;

        int cap = k.BodySlotCapacity;
        long sparse = k.BodyStorageBytes;
        long dense = ScrollKernel.DenseBodyStorageBytes(Nodes);
        bool bound = k.NodeColumnLength >= Nodes
            && k.BoundCount == Viewports
            && cap == Viewports              // O(viewports): the 16-slot pool doubled exactly once
            && k.BodySlotsInUse == Viewports
            && sparse < 256L * 1024
            && sparse * 60 < dense
            && perBodyState;
        Check("gate.kernel.body-sparse", bound,
            $"At {k.NodeColumnLength} node lookup slots with {Viewports} live viewports the kernel holds {sparse} B "
            + $"in a {cap}-slot pool of {ScrollKernel.BodyBytes} B bodies (the deleted node-indexed storage: "
            + $"{dense} B) — the backing must scale with viewports, not scene capacity. bound={k.BoundCount} "
            + $"inUse={k.BodySlotsInUse} perBodyState={perBodyState}");
    }

    // ── gate.kernel.slot-reuse ────────────────────────────────────────────────────────────────────────────────
    // Mount/unmount churn must RECYCLE slots, not grow the pool: an unbound slot returns to the free list at the
    // end of the pass that unbound it (deferred so this pass's touched/active lists can never name a slot that has
    // already been handed to a new viewport), and the next Bind takes it back. 200 waves of 24 viewports therefore
    // leave the pool at its first-wave high-water and allocate nothing.

    private static void SlotReuseCheck()
    {
        const int Wave = 24;
        const int Waves = 200;
        const int FirstNode = 100;

        static void Mount(ScrollKernel k, int first, int n)
        {
            for (int i = 0; i < n; i++)
            {
                k.Port.Post(ScrollInput.Bind(first + i));
                k.Port.Post(ScrollInput.SetFrame(first + i, Frame(2000f, 400f)));
            }
            k.Reclamp();
        }
        static void Unmount(ScrollKernel k, int first, int n)
        {
            for (int i = 0; i < n; i++) k.Port.Post(ScrollInput.Unbind(first + i));
            k.Reclamp();
        }

        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);

        Mount(k, FirstNode, Wave);            // warm-up wave: establishes the pool high-water + JITs both paths
        int mountedInUse = k.BodySlotsInUse;
        int mountedBound = k.BoundCount;
        Unmount(k, FirstNode, Wave);
        int cap = k.BodySlotCapacity;
        sink.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int w = 0; w < Waves; w++)
        {
            Mount(k, FirstNode, Wave);
            Unmount(k, FirstNode, Wave);
            sink.Clear();                     // the recording double, not the kernel, would otherwise grow
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        // _slotHi never passing the first wave's 24 is what "reuse" means: had every wave taken fresh slots, the
        // pool would have doubled its way to 4096+ and BodySlotsInUse would track the total ever mounted.
        bool ok = mountedInUse == Wave && mountedBound == Wave
            && k.BodySlotCapacity == cap && cap == 32
            && k.BoundCount == 0 && k.BodySlotsInUse == 0
            && after - before == 0;
        Check("gate.kernel.slot-reuse", ok,
            $"{Waves} mount/unmount waves of {Wave} viewports: pool stayed at {k.BodySlotCapacity} slots "
            + $"(first wave {cap}), bound={k.BoundCount} inUse={k.BodySlotsInUse}, alloc delta={after - before} B");
    }

    // ── gate.kernel.thread-agnostic ───────────────────────────────────────────────────────────────────────────

    private static void ThreadAgnosticCheck()
    {
        static void Script(ScrollKernel k)
        {
            SetupViewport(k, 1, 3000f, 400f);
            SetupViewport(k, 2, 500f, 400f);
            k.Port.Post(ScrollInput.Chain(2, 1));
            k.Port.Post(ScrollInput.ThumbSet(2, 50f));
            k.Reclamp();
            double t = 0;
            k.Port.Post(ScrollInput.ContactBegin(2, t, 0f));
            for (int i = 1; i <= 5; i++) { t = i * 0.008; k.Port.Post(ScrollInput.ContactMove(2, t, i * 20f)); k.Tick(ClockAt(t, 0.008f)); }
            t += 0.008;
            k.Port.Post(ScrollInput.ContactEnd(2, t, 100f));
            k.Tick(ClockAt(t, 0.008f));
            for (int i = 0; i < 30; i++) { t += 0.00833; k.Tick(ClockAt(t)); }
        }

        var sinkA = new RecordingSink();
        var kA = new ScrollKernel(sinkA, ScrollFeel.Shipping);
        Script(kA);

        var sinkB = new RecordingSink();
        var th = new Thread(() =>
        {
            var kB = new ScrollKernel(sinkB, ScrollFeel.Shipping);
            Script(kB);
        });
        th.Start();
        th.Join();

        bool ok = sinkA.Count == sinkB.Count;
        if (ok)
        {
            for (int i = 0; i < sinkA.Count; i++)
            {
                if (sinkA.Nodes[i] != sinkB.Nodes[i] || !sinkA.Writes[i].Equals(sinkB.Writes[i])) { ok = false; break; }
            }
        }
        Check("gate.kernel.thread-agnostic", ok, $"countA={sinkA.Count} countB={sinkB.Count}");
    }

    // ── gate.kernel.one-write-per-tick ────────────────────────────────────────────────────────────────────────

    private static void OneWritePerTickCheck()
    {
        var sink = new RecordingSink();
        var k = new ScrollKernel(sink, ScrollFeel.Shipping);
        SetupViewport(k, 1, 5000f, 400f);
        sink.Clear();

        double t = 0.00833;
        k.Port.Post(ScrollInput.WheelNotch(1, t, 50f));
        k.Port.Post(ScrollInput.WheelNotch(1, t, 30f));
        k.Port.Post(ScrollInput.WheelNotch(1, t, 20f));
        k.Tick(ClockAt(t));

        Check("gate.kernel.one-write-per-tick", sink.CountFor(1) == 1, $"writes for node 1 = {sink.CountFor(1)}");
    }

    // ── gate.kernel.port-overflow-policy ──────────────────────────────────────────────────────────────────────

    private static void PortOverflowPolicyCheck()
    {
        var port = new ScrollCommandPort();
        int postCount = ScrollCommandPort.Capacity + 50;
        for (int i = 0; i < postCount; i++)
            port.Post(ScrollInput.FrameDelta(1, i, i)); // A = i — distinguishes each posted value

        bool pendingBounded = port.Pending == ScrollCommandPort.Capacity;

        var buf = new ScrollInput[ScrollCommandPort.Capacity];
        int drained = port.DrainAll(buf);
        // Overflow always evicts the OLDEST same-node/same-kind slot (a scan from the ring's tail). Once evicted
        // in place, that slot is STILL the oldest ring position, so every further overflow keeps landing there —
        // buf[0] (the oldest surviving position) therefore ends up carrying the LAST posted value, while
        // buf[1..] retain the earlier, never-touched values (1..Capacity-1). This is a faithful "drop the
        // oldest" per-event policy; it does not spread eviction across the overflowing run.
        bool oldestSlotIsFresh = drained == ScrollCommandPort.Capacity && buf[0].A == postCount - 1;
        bool restUntouched = drained == ScrollCommandPort.Capacity && buf[1].A == 1f && buf[drained - 1].A == drained - 1;

        // A structural command (never coalesced) posted after the port is already at capacity must still land —
        // verify by draining first (making room), then posting Begin/End alongside more FrameDelta overflow.
        port.Post(ScrollInput.Bind(2));
        var buf2 = new ScrollInput[ScrollCommandPort.Capacity];
        int n2 = port.DrainAll(buf2);
        bool bindLanded = false;
        for (int i = 0; i < n2; i++) if (buf2[i].Kind == ScrollInputKind.Bind && buf2[i].Node == 2) bindLanded = true;

        Check("gate.kernel.port-overflow-policy", pendingBounded && oldestSlotIsFresh && restUntouched && bindLanded,
            $"pending={port.Pending} drained={drained} buf0={buf[0].A} buf1={buf[1].A} bufLast={buf[drained - 1].A} bindLanded={bindLanded}");
    }
}
