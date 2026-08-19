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
