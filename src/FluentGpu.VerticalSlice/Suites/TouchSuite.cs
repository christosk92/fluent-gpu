using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;




static class TouchSuite
{
    public static void Run(StringTable strings)
    {
        TouchGestureChecks(strings);
        TouchPhase2Checks(strings);
        ArenaCoreChecks(strings);
        ArenaConsumerChecks(strings);
        ArenaDeterminismChecks(strings);
        PinchZoomChecks(strings);
        TouchSnapOverscrollChecks(strings);
        Touch4SipChecks(strings);
        Touch4HoldWakeChecks(strings);
    }

    static void TouchGestureChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.touch.flick-decay-settle: a touch flick UP over a bound virtual list seeds a friction-decay fling that
        // moves the offset monotonically for many frames, re-realizes the virtual window (FirstRealized advances), then
        // settles at a BOUNDED natural rest — coast ≈ v0/k under WinUI-like friction (FlingDecayPerS=0.05,
        // k≈3.0/s), so it stops SHORT of the far 2800px content-end clamp (NOT a floaty drift to the end),
        // and within a bounded number of frames.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-fling", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;

            // Flick the finger up the viewport (offset increases as the content follows the finger upward). Start inside
            // the 400px-tall viewport (y < 400) so the touch-down lands ON the scroller, not on its bottom edge.
            TouchGesture(window, host, new Point2(150, 384), new Point2(150, 44), 16, pointerId: 7, msPerStep: 16f);
            host.Scene.TryGetScroll(vp, out var afterUp);
            float prevOff = afterUp.OffsetY;
            int firstAfterUp = afterUp.FirstRealized;

            int monotonicRun = 0, maxRun = 0, settledAt = -1;
            int firstRealizedSettled = firstAfterUp;
            bool decayRendered = false;
            for (int i = 0; i < 600 && settledAt < 0; i++)
            {
                var f = host.RunFrame();
                host.Scene.TryGetScroll(vp, out var sc);
                if (sc.OffsetY > prevOff + 0.01f) { monotonicRun++; if (monotonicRun > maxRun) maxRun = monotonicRun; if (f.Rendered) decayRendered = true; }
                else if (sc.Phase == 0) { settledAt = i; firstRealizedSettled = sc.FirstRealized; }
                else monotonicRun = 0;   // a non-advancing frame mid-fling (should not happen) breaks the run
                prevOff = sc.OffsetY;
            }
            host.Scene.TryGetScroll(vp, out var settled);
            float maxOff = MathF.Max(0f, settled.ContentH - settled.ViewportH);
            bool reRealized = settled.FirstRealized != firstAfterUp && decayRendered;
            // Windows-like friction: the fling coasts a BOUNDED distance forward and settles SHORT of the far clamp.
            bool coastedForward = settled.OffsetY > afterUp.OffsetY + 20f;
            bool boundedShortOfClamp = settled.OffsetY < maxOff - 100f;
            bool settledFast = settledAt >= 0 && settledAt < 160;   // comfortably below 2.7s at the fixed 60Hz clock
            Check("gate.touch.flick-decay-settle a touch flick decays monotonically (≥10 frames), re-realizes the window, then settles at a BOUNDED rest SHORT of the far clamp (WinUI-like friction, not a near-frictionless coast to the end)",
                maxRun >= 10 && settledFast && reRealized && coastedForward && boundedShortOfClamp,
                $"maxRun={maxRun} settledAt={settledAt} offset={afterUp.OffsetY:0}->{settled.OffsetY:0} (clamp={maxOff:0}) reRealized={reRealized}");
        }

        // gate.scroll.impulse-velocity (scroll-feel rework Phase 1, design §2): the IMPULSE (work-energy) release
        // estimator. (a) CONSTANT-velocity drag → the seeded fling speed equals the hand speed exactly (W = ½v² →
        // √(2W) = v). (b) An ACCELERATING drag queued TWO moves per frame — the ring coalesces each pair and deposits
        // the overwritten move into the velocity side ring — must match an independent in-gate replica of the IMPULSE
        // math over the full scripted sample stream: proves per-packet (pre-coalesce) fidelity survives frame
        // coalescing. (c) A drag that PAUSES ≥ AssumeStoppedMs before the lift seeds NO fling (Android
        // ASSUME_POINTER_STOPPED_TIME): the finger stopped before lifting.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("impulse-vel", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;

            // (a) constant velocity: 10 moves × 32 px @ 16 ms = 2000 px/s finger speed, up 16 ms after the last move.
            TouchGesture(window, host, new Point2(150, 384), new Point2(150, 64), 10, pointerId: 11, msPerStep: 16f);
            host.Scene.TryGetScroll(vp, out var scA);
            // The up frame's phase-7 tick already decayed the fresh seed once (16 ms of FlingDecayPerS) — fold that
            // into the expectation so the gate asserts the ESTIMATOR exactly, not the estimator minus one tick.
            float decay1 = MathF.Exp(MathF.Log(ScrollIntegrator.FlingDecayPerS) * 16f / 1000f);
            float vConst = MathF.Abs(scA.FlingVelocity);
            bool constExact = MathF.Abs(vConst - 2000f * decay1) <= 2000f * decay1 * 0.02f && scA.Phase == ScrollIntegrator.Fling;   // ±2%
            for (int i = 0; i < 400; i++) { host.RunFrame(); host.Scene.TryGetScroll(vp, out var s); if (s.Phase == 0) break; }

            // (b) accelerating, two moves per frame (8 ms stamps): quadratic profile y(t) sampled every 8 ms. The gate
            // replicates the estimator (cap-8 ring, 66 ms horizon, newest-pre-window baseline, ½ first term, √(2W)).
            uint t0 = s_touchClockMs; float y0 = 384f;
            Span<(uint T, float Y)> samples = stackalloc (uint, float)[17];
            samples[0] = (t0, y0);                                       // the down seeds the ring
            for (int i = 1; i <= 16; i++)
            {
                float tt = i / 16f;                                       // quadratic: slow start, fast finish
                samples[i] = ((uint)(t0 + i * 8), y0 - 320f * tt * tt);
            }
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(150, samples[0].Y), samples[0].T, 12));
            host.RunFrame();
            for (int i = 1; i <= 16; i += 2)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(150, samples[i].Y), samples[i].T, 12));
                if (i + 1 <= 16) window.QueueInput(Touch(InputKind.PointerMove, new Point2(150, samples[i + 1].Y), samples[i + 1].T, 12));
                host.RunFrame();                                          // the pair coalesces; the first feeds the side ring
            }
            uint tUp = (uint)(t0 + 16 * 8 + 8);
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(150, samples[16].Y), tUp, 12));
            host.RunFrame();
            s_touchClockMs = tUp + 1000;
            host.Scene.TryGetScroll(vp, out var scB);
            float vAccel = MathF.Abs(scB.FlingVelocity);

            // Independent replica of the estimator over the scripted stream (cap-8 ring → keep the NEWEST 8 samples).
            int keep = Math.Min(8, samples.Length);
            int start = samples.Length - keep;
            double w = 0.0, vprev = 0.0; bool firstSeg = true; int basei = -1;
            for (int i = start; i < samples.Length; i++)
            {
                if ((tUp - samples[i].T) > 40) { basei = i; continue; }   // pre-window → baseline (v2 §4.3: single 40ms IMPULSE window)
                if (basei < 0) { basei = i; continue; }                    // first in-window sample baselines
                double dt = (samples[i].T - samples[basei].T) / 1000.0;
                if (dt <= 0) { basei = i; continue; }
                double v = (samples[i].Y - samples[basei].Y) / dt;
                if (firstSeg) { w += 0.5 * v * Math.Abs(v); firstSeg = false; }
                else w += (v - vprev) * Math.Abs(v);
                vprev = v; basei = i;
            }
            float vExpected = (float)Math.Abs(Math.Sign(w) * Math.Sqrt(2.0 * Math.Abs(w))) * decay1;   // − one up-frame tick
            bool conforms = vExpected > 100f && MathF.Abs(vAccel - vExpected) <= MathF.Max(1f, vExpected * 0.02f);
            for (int i = 0; i < 400; i++) { host.RunFrame(); host.Scene.TryGetScroll(vp, out var s); if (s.Phase == 0) break; }

            // (c) pause-before-lift: a real drag, then the finger rests 60 ms (> AssumeStoppedMs=40), then lifts → no fling.
            uint t2 = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(150, 384), t2, 13)); host.RunFrame();
            for (int i = 1; i <= 8; i++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(150, 384 - i * 24), t2 + (uint)(i * 16), 13));
                host.RunFrame();
            }
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(150, 384 - 8 * 24), t2 + 8 * 16 + 60, 13));   // 60 ms rest
            host.RunFrame();
            s_touchClockMs = t2 + 8 * 16 + 60 + 1000;
            host.Scene.TryGetScroll(vp, out var scC);
            bool stoppedNoFling = scC.Phase != ScrollIntegrator.Fling || MathF.Abs(scC.FlingVelocity) < 1f;

            Check("gate.scroll.impulse-velocity the IMPULSE release estimator: constant-velocity drag reads the exact hand speed; an accelerating TWO-MOVES-PER-FRAME stream (ring-coalesced, side-ring fed) matches the independent work-energy replica; a ≥40ms pause before lift seeds NO fling",
                constExact && conforms && stoppedNoFling,
                $"const={vConst:0} (expect 2000±2%) accel={vAccel:0} vs replica={vExpected:0} pauseFling={(scC.Phase == ScrollIntegrator.Fling ? scC.FlingVelocity : 0f):0.0}");
        }

        // gate.scroll.mouse-wheel-eases-discrete: a synthetic PointerKind.Mouse stream remains on ONE eased TargetChase
        // path regardless of delta magnitude. Precision touchpad selection is device-tag based (never magnitude based)
        // and is covered separately by ScrollParityChecks. A mixed mouse stream must advance monotonically toward the
        // summed target, never enter Fling mode, and converge without a post-target coast.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("wheel-eases", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;

            // Mixed synthetic mouse deltas straddling the old magnitude threshold: device type keeps all on one path.
            float[] mixed = { 20f, 60f, 30f, 90f, 15f, 70f, 40f, 25f, 50f, 35f };   // sum = 435
            uint t = 1000; bool everFlung = false; float prev = 0f; bool monotonic = true;
            foreach (float d in mixed)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, ScrollDelta: d, TimestampMs: t));
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                if (s.Phase == ScrollIntegrator.Fling) everFlung = true;
                if (s.OffsetY + 0.01f < prev) monotonic = false;   // never jumps backward (no eased-vs-1:1 desync)
                prev = s.OffsetY; t += 16;
            }
            for (int i = 0; i < 60; i++) host.RunFrame();   // let the ease converge to the accumulated target
            host.Scene.TryGetScroll(vp, out var settled);
            float offSettled = settled.OffsetY;
            bool converged = offSettled >= 433f && offSettled <= 437f && settled.Phase == 0;
            // After the stream stops, no momentum/coast past the target.
            float coastMax = offSettled;
            for (int i = 0; i < 30; i++) { host.RunFrame(); host.Scene.TryGetScroll(vp, out var s); coastMax = MathF.Max(coastMax, s.OffsetY); }
            bool noMomentum = coastMax <= offSettled + 1f;
            Check("gate.scroll.mouse-wheel-eases-discrete a MIXED-magnitude PointerKind.Mouse stream eases monotonically via ONE TargetChase path to the summed target — no magnitude split, no post-target momentum",
                monotonic && !everFlung && converged && noMomentum,
                $"settled={offSettled:0} (expect 435) monotonic={monotonic} flung={everFlung} coastTo={coastMax:0} mode={settled.Phase}");
        }

        // gate.scroll.flick-into-edge-bounce: a genuine TOUCH flick whose fling REACHES a clamp converts the residual
        // velocity into a rubber-band bounce (WinUI/iOS) instead of stopping dead — the real trace showed a finger flick
        // hit off=0 and stopped flat ("no rubber band"). Reproduces it: scroll down a bit, then a short HARD finger flick
        // toward the top so the fling coasts into off=0 with residual speed → overscroll excursion (>1px) → springs to ~0.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("flick-bounce", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            // Move down to ~offset 384 via wheel, then let it settle (room above for the flick's fling to coast into off=0).
            uint t = 1000;
            for (int i = 0; i < 8; i++) { window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, ScrollDelta: 48f, TimestampMs: t)); host.RunFrame(); t += 16; }
            for (int i = 0; i < 60; i++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var atStart);
            float startOff = atStart.OffsetY;
            // Short HARD finger flick DOWN (finger y increases → content scrolls toward the TOP, offset → 0). 80px move
            // stays in-range (no overpan during the drag); fast (5ms/step) → high release velocity → the fling overshoots 0.
            TouchGesture(window, host, new Point2(150, 150), new Point2(150, 230), 4, pointerId: 11, msPerStep: 5f);
            float maxBand = 0f; int settledAt = -1; bool flung = false;
            for (int i = 0; i < 200 && settledAt < 0; i++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                if (s.Phase == ScrollIntegrator.Fling) flung = true;
                maxBand = MathF.Max(maxBand, MathF.Abs(s.OverscrollPx));
                if (i > 4 && MathF.Abs(s.OverscrollPx) < 0.1f && s.OffsetY <= 0.5f) settledAt = i;
            }
            Check("gate.scroll.flick-into-edge-bounce a touch flick whose fling reaches the clamp bounces (overscroll excursion > 1px) then springs back to ~0 (not a dead stop)",
                flung && maxBand > 1f && settledAt >= 0, $"startOff={startOff:0} flung={flung} maxBand={maxBand:0.0} settledAt={settledAt}");

            // A low residual speed is still live inertia, so crossing the clamp must hand it to the elastic spring too.
            // This catches the old 50 px/s bounce gate, which made a 25 px/s edge arrival stop perfectly flat.
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, ScrollDelta: 2f, TimestampMs: t));
            for (int i = 0; i < 60; i++) host.RunFrame();
            ref var slow = ref host.Scene.ScrollRef(vp);
            // Put the viewport within one 60 Hz coast step of the leading clamp. The preceding wheel is only a
            // convenient way to leave the completed bounce path in a valid scroll state; its device-scale mapping is
            // deliberately not part of this physics gate.
            slow.OffsetY = 0.1f;
            slow.FlingVelocity = -25f;
            slow.Phase = ScrollIntegrator.Fling;
            slow.PhaseFlags = 0;
            slow.FlingRetargeted = false;
            slow.FlingSnapTarget = float.NaN;
            slow.FlingFromOffset = slow.OffsetY;
            host.ScrollIntegratorForTest.Arm(vp);
            float slowBand = 0f;
            for (int i = 0; i < 60; i++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                slowBand = MathF.Max(slowBand, MathF.Abs(s.OverscrollPx));
            }
            Check("gate.scroll.slow-fling-into-edge-elastic a still-live 25 px/s fling crossing the clamp produces a subtle elastic handoff instead of stopping dead",
                slowBand > 0.01f, $"maxBand={slowBand:0.000}px bounceGate={ScrollIntegrator.FlingBounceMinPxPerS:0}px/s");
        }

        // gate.scroll.touch-overpan-bounce: the surviving rubber band lives on the genuine TOUCH path. A finger pan that
        // drags PAST the top edge (at offset 0) builds a damped overscroll EXCURSION (>1px) and, on release, the
        // critically-damped spring carries it back to ~0 (WinUI/iOS). The trackpad/wheel path has no band (a wheel message
        // carries no manipulation to rubber-band — it hard-clamps).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("overpan-bounce", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            // Drag the finger DOWN inside the viewport while already at the top (offset 0) → pulls past the top edge.
            TouchGesture(window, host, new Point2(150, 120), new Point2(150, 340), 12, pointerId: 9, msPerStep: 16f);
            host.Scene.TryGetScroll(vp, out var afterDrag);
            float maxBand = MathF.Abs(afterDrag.OverscrollPx);
            int settledAt = -1;
            for (int i = 0; i < 200 && settledAt < 0; i++)   // release → spring the band back to 0
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                maxBand = MathF.Max(maxBand, MathF.Abs(s.OverscrollPx));
                if (i > 4 && MathF.Abs(s.OverscrollPx) < 0.1f) settledAt = i;
            }
            Check("gate.scroll.touch-overpan-bounce a touch pan past the edge builds a rubber-band excursion (>1px) then springs back to ~0",
                maxBand > 1f && settledAt >= 0, $"maxBand={maxBand:0.0} settledAt={settledAt}");
        }

        // gate.touch.fling-alloc-steady-zero: a 30-frame fling allocates 0 managed bytes on the hot half. A large list
        // keeps the fling in steady decay across the whole window (never clamps), so all 30 frames run the integrator +
        // re-realize path. The gate list binds ONLY a struct channel (Fill) per row — the fling integrator, the phase-7
        // SetScrollOffset write, the virtual re-realize, and the slot-rebind flush must contribute 0 managed bytes. (A list
        // whose rows ALSO bind unique `$"row {idx}"` text streams a never-before-seen string into StringTable.Intern on each
        // cross-boundary recycle — the corpus's bounded-Gen0 reconcile edge, shared identically by wheel; that residual is
        // gated separately below as the SAME value under wheel, not attributed to the touch path.) Warm one full flick first
        // (JIT the gesture/integrator/realize path) outside the measured window.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-fling-alloc", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new BoundVirtualFillOnlyProbe());
            host.RunFrame();

            TouchFlick(window, host, new Point2(150, 384), new Point2(150, 44), 12, pointerId: 1, msPerStep: 16f, decayFrames: 12);   // warm
            for (int i = 0; i < 30; i++) host.RunFrame();   // drain any residual fling so the measured flick starts clean

            long worstDuringGesture = TouchGesture(window, host, new Point2(150, 384), new Point2(150, 44), 12, pointerId: 1, msPerStep: 16f);
            long worstDecay = 0;
            int flingFrames = 0;
            for (int i = 0; i < 30; i++)
            {
                var f = host.RunFrame();
                if (f.HotPhaseAllocBytes > worstDecay) worstDecay = f.HotPhaseAllocBytes;
                if ((host.CurrentWakeReasons & WakeReasons.ScrollAnim) != 0) flingFrames++;
            }
            Check("gate.touch.fling-alloc-steady-zero a 30-frame touch fling over a struct-only bound list allocates 0 managed bytes on the hot half (the integrator + virtual re-realize + slot-rebind stayed armed and clean the whole window)",
                worstDecay == 0 && flingFrames >= 25, $"worstDecayHotAlloc={worstDecay}B flingFrames={flingFrames}/30 worstGesture={worstDuringGesture}B");
        }

        // gate.touch.fling-realize-edge-is-wheel: a bound list whose rows ALSO bind unique per-row text (`$"row {idx}"`)
        // streams a fresh string into the interner on every cross-boundary recycle — a non-zero hot-half cost. This check
        // proves that residual is the PRE-EXISTING reconcile edge, NOT introduced by touch: a touch fling's worst decay-frame
        // alloc equals a NON-touch wheel scroll's worst alloc across the SAME 40px-row boundaries on the SAME list. (CLAUDE.md:
        // near-zero — bounded Gen0 at the render/reconcile edge.) The fling integrator itself adds nothing on top of the wheel.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-fling-edge", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new BoundVirtualProbe());
            host.RunFrame();

            TouchFlick(window, host, new Point2(150, 384), new Point2(150, 44), 12, pointerId: 1, msPerStep: 16f, decayFrames: 12);   // warm
            for (int i = 0; i < 30; i++) host.RunFrame();   // drain any residual fling so the measured flick starts clean

            // The wheel baseline: a non-touch scroll across the same row boundaries on this text-binding list.
            long wheelCrossAlloc = 0;
            {
                var wptr = new Point2(150, 200);
                for (int i = 0; i < 6; i++) { window.QueueInput(new InputEvent(InputKind.Wheel, wptr, 0, 0, 400f)); host.RunFrame(); }   // warm
                for (int i = 0; i < 20; i++) { window.QueueInput(new InputEvent(InputKind.Wheel, wptr, 0, 0, 400f)); var wf = host.RunFrame(); if (wf.HotPhaseAllocBytes > wheelCrossAlloc) wheelCrossAlloc = wf.HotPhaseAllocBytes; }
                window.QueueInput(new InputEvent(InputKind.Wheel, wptr, 0, 0, -10_000_000f)); host.RunFrame();   // back to top
                for (int i = 0; i < 6; i++) host.RunFrame();
            }

            long worstDuringGesture = TouchGesture(window, host, new Point2(150, 384), new Point2(150, 44), 12, pointerId: 1, msPerStep: 16f);
            long worstDecay = 0;
            int flingFrames = 0;
            for (int i = 0; i < 30; i++)
            {
                var f = host.RunFrame();
                if (f.HotPhaseAllocBytes > worstDecay) worstDecay = f.HotPhaseAllocBytes;
                if ((host.CurrentWakeReasons & WakeReasons.ScrollAnim) != 0) flingFrames++;
            }
            // The fling's per-frame edge cost must not EXCEED the wheel's (touch adds nothing); both are the shared recycle
            // string churn. wheelCrossAlloc>0 confirms the edge is genuinely exercised (else the comparison would be vacuous).
            Check("gate.touch.fling-realize-edge-is-wheel a touch fling's per-frame realize-edge alloc over a unique-text bound list is the SAME pre-existing reconcile-edge churn a non-touch wheel scroll incurs across the same row boundaries (touch adds nothing)",
                wheelCrossAlloc > 0 && worstDecay <= wheelCrossAlloc && flingFrames >= 25,
                $"worstDecayHotAlloc={worstDecay}B wheel-cross-boundary baseline={wheelCrossAlloc}B (touch≤wheel ⇒ the residual is the shared bound-row RE-REALIZE string churn, not the fling integrator) flingFrames={flingFrames}/30 worstGesture={worstDuringGesture}B");
        }

        // gate.touch.coalesce-flood: 500 moves for ONE contact queued WITHOUT interleaving a frame must coalesce to
        // exactly one surviving move for that id (Down/Up never coalesce). Tested two ways: (A) the ring contract
        // directly (drain a Down + 500 Moves + Up → exactly 1 PointerMove survives), and (B) end-to-end — a 500-move
        // flood pumped in one frame allocates 0 managed bytes on the hot half and lands the pan at the FINAL move only.
        {
            // (A) ring contract: one surviving move per id, Down/Up preserved, slab never grew.
            var ring = new InputEventRing();
            ring.Clear();
            ring.Write(Touch(InputKind.PointerDown, new Point2(10, 400), 1000, 3));
            for (int i = 0; i < 500; i++) ring.Write(Touch(InputKind.PointerMove, new Point2(10, 400 - i), (uint)(1001 + i), 3));
            ring.Write(Touch(InputKind.PointerUp, new Point2(10, 0), 1502, 3));
            var span = ring.Drain();
            int moves = 0, downs = 0, ups = 0;
            Point2 lastMovePos = default;
            foreach (ref readonly var e in span)
            {
                if (e.Kind == InputKind.PointerMove) { moves++; lastMovePos = e.PositionPx; }
                else if (e.Kind == InputKind.PointerDown) downs++;
                else if (e.Kind == InputKind.PointerUp) ups++;
            }
            bool ringOk = moves == 1 && downs == 1 && ups == 1 && Near(lastMovePos.Y, 400 - 499);   // the surviving move is the LAST queued
            Check("gate.touch.coalesce-flood.ring 500 unconsumed moves for one contact coalesce to exactly 1 surviving move (Down/Up preserved, the survivor is the latest)",
                ringOk, $"moves={moves} downs={downs} ups={ups} survivorY={lastMovePos.Y:0} drained={span.Length}");

            // (B) end-to-end: pump a 500-move flood in ONE frame → the ring keeps 1 move → the pump/coalesce/dispatch path
            // allocates 0 managed bytes on the hot half and the pan lands at the FINAL move only. Kept SUB-BOUNDARY (30px,
            // from offset 0) so the flood frame stays transform-only — isolating the coalescing 0-alloc from the separate
            // cross-boundary re-realize cost (which gate.touch.fling-alloc-steady-zero surfaces).
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-flood", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new BoundVirtualProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            // Warm the sub-boundary pan path with a few small in-window drags (claim + transform-only scroll), then settle
            // back to the top so the measured flood starts at offset 0 and its 30px stays within the realized window.
            for (int w = 0; w < 4; w++)
            {
                uint tw = s_touchClockMs;
                window.QueueInput(Touch(InputKind.PointerDown, new Point2(150, 200), tw, 5)); host.RunFrame();
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(150, 188), tw + 16, 5)); host.RunFrame();   // 12px: claims, sub-boundary
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(150, 200), tw + 32, 5)); host.RunFrame();   // back
                window.QueueInput(Touch(InputKind.PointerUp, new Point2(150, 200), tw + 48, 5)); host.RunFrame();
                s_touchClockMs = tw + 1000;
            }
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, -1_000_000f)); host.RunFrame();   // pin to the top
            for (int i = 0; i < 6; i++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var beforeFlood);
            float floodBaseOff = beforeFlood.OffsetY;

            uint t0 = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(150, 200), t0, 9));
            host.RunFrame();   // anchor the contact (the claim + scroll happens on the flood frame's single surviving move)
            for (int i = 0; i < 500; i++)   // 500 moves, NO interleaved frame → the ring coalesces to one before dispatch
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(150, 200 - (i + 1) * 0.06f), t0 + 1 + (uint)i, 9));   // total 30px (sub-boundary)
            var floodFrame = host.RunFrame();   // pumps all 500 → ring keeps 1 → dispatch sees Down(prev)+1 Move
            host.Scene.TryGetScroll(vp, out var afterFlood);
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(150, 200 - 500 * 0.06f), t0 + 502, 9));
            host.RunFrame();
            s_touchClockMs = t0 + 1000;

            // The pan landed at the FINAL move's delta only (one effective move): finger from y=200 to y=170 ⇒ +30px offset.
            float expectFloodOff = floodBaseOff + 30f;
            bool floodLanded = Near(afterFlood.OffsetY, expectFloodOff, 1.5f);
            Check("gate.touch.coalesce-flood.frame a 500-move single-frame flood allocates 0 managed bytes on the hot half and lands the pan at the final move only (one effective move)",
                floodFrame.HotPhaseAllocBytes == 0 && floodLanded,
                $"floodHotAlloc={floodFrame.HotPhaseAllocBytes}B off={afterFlood.OffsetY:0} expect={expectFloodOff:0}");
        }

        // gate.touch.tap-vs-pan: a below-slop touch down→up over a clickable row TAPS it (OnClick fires); a touch drag
        // over the same list claims the pan and NEVER clicks (the pressed candidate is cancelled — WinUI Pressed→Canceled,
        // never Released). Same probe, same row 0 — only the gesture differs.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-tap", new Size2(360, 460), 1f)); window.Show();
            var probe = new TouchTapPanProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var tapSc0);
            var row0 = host.Scene.FirstChild(tapSc0.ContentNode);   // first realized row
            var rowCenter = CenterOf(host.Scene, row0);

            // TAP: down + up at the same point (no slop crossing) over row 0.
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, rowCenter, t, 2));
            host.RunFrame();
            bool listPressInitiallyDelayed = (host.Scene.Flags(row0) & NodeFlags.Pressed) == 0;
            for (int i = 0; i < 7; i++) host.RunFrame();
            bool listPressAppearedAfterDelay = (host.Scene.Flags(row0) & NodeFlags.Pressed) != 0;
            window.QueueInput(Touch(InputKind.PointerUp, rowCenter, t + 128, 2));
            host.RunFrame();
            s_touchClockMs = t + 1000;
            int clicksAfterTap = probe.Row0Clicked;
            int pressedAfterTap = probe.Row0Pressed;
            host.Scene.TryGetScroll(vp, out var afterTap);
            bool tapDidNotScroll = Near(afterTap.OffsetY, 0f);

            // TAP-TO-STOP: a contact landing while the viewport is coasting belongs to the viewport. It arrests the
            // inertia but must not enter the row's press/click pipeline when lifted without moving.
            ref var stopping = ref host.Scene.ScrollRef(vp);
            stopping.Phase = ScrollIntegrator.Fling;
            stopping.FlingVelocity = 900f;
            window.QueueInput(Touch(InputKind.PointerDown, rowCenter, t + 160, 2)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, rowCenter, t + 176, 2)); host.RunFrame();
            bool stopTapSwallowed = probe.Row0Clicked == clicksAfterTap && probe.Row0Pressed == pressedAfterTap
                                    && host.Scene.ScrollRef(vp).Phase != ScrollIntegrator.Fling;

            window.QueueInput(Touch(InputKind.PointerDown, rowCenter, t + 240, 3)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerMove, new Point2(rowCenter.X, rowCenter.Y - 20f), t + 256, 3)); host.RunFrame();
            bool panBeforeDelayNeverPressed = (host.Scene.Flags(row0) & NodeFlags.Pressed) == 0;
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(rowCenter.X, rowCenter.Y - 20f), t + 272, 3)); host.RunFrame();

            // PAN: a drag starting on row 0 that crosses slop. The press is delivered on down then cancelled at the claim.
            TouchGesture(window, host, new Point2(rowCenter.X, rowCenter.Y), new Point2(rowCenter.X, rowCenter.Y - 180f), 12, pointerId: 2, msPerStep: 16f);
            int clicksAfterPan = probe.Row0Clicked;
            int pressedAfterPan = probe.Row0Pressed;
            host.Scene.TryGetScroll(vp, out var afterPan);
            bool panScrolled = afterPan.OffsetY > 100f;

            bool tapOk = clicksAfterTap == 1 && tapDidNotScroll;
            bool panOk = clicksAfterPan == clicksAfterTap && panScrolled;   // NO additional click from the pan
            Check("gate.touch.tap-vs-pan a below-slop touch tap fires OnClick; a touch pan over the same row scrolls and never clicks",
                tapOk && listPressInitiallyDelayed && listPressAppearedAfterDelay && panBeforeDelayNeverPressed && stopTapSwallowed && panOk,
                $"tapClicks={clicksAfterTap} delayed={listPressInitiallyDelayed}->{listPressAppearedAfterDelay} panNoFlash={panBeforeDelayNeverPressed} stopSwallowed={stopTapSwallowed} panClicks={clicksAfterPan} (scroll={afterPan.OffsetY:0}) pressedDelivered={pressedAfterPan}");

            // gate.touch.pan-cancels-press: the down chain SAW the press (OnPointerPressed fired on down) but the pan claim
            // delivered the cancel — so no click ever fired through the pan. (Pressed delivered ≥ 1, click count unchanged
            // by the pan: the press was Canceled, not Released.)
            bool pressDeliveredThenCancelled = pressedAfterPan >= 1 && clicksAfterPan == clicksAfterTap;
            Check("gate.touch.pan-cancels-press a claimed pan cancels the press it delivered to the down chain (press seen, no Released/click)",
                pressDeliveredThenCancelled, $"row0Pressed={pressedAfterPan} row0Clicked={clicksAfterPan} (tap baseline {clicksAfterTap})");

            // gate.touch.no-stuck-hover: after the tap AND the pan sequences, no node retains NodeFlags.Hovered (touch has
            // no resting hover — up/cancel clears the transient touch hover).
            var stuck = AnyHovered(host.Scene, host.Scene.Root);
            Check("gate.touch.no-stuck-hover no node retains hover after a touch tap + pan sequence (no resting touch hover)",
                stuck.IsNull, stuck.IsNull ? "none" : $"node {stuck.Raw.Index} still Hovered");
        }

        // gate.touch.two-contacts-independent: two concurrent touch contacts pan two sibling scrollers independently (the
        // per-PointerId capture map). Each frame advances BOTH ids over their own list; each viewport's offset tracks only
        // its own contact (a pan on the left never moves the right).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-two", new Size2(440, 360), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TwoListProbe());
            host.RunFrame();
            var left = ViewportWithItemCount(host.Scene, host.Scene.Root, TwoListProbe.LeftN);
            var right = ViewportWithItemCount(host.Scene, host.Scene.Root, TwoListProbe.RightN);
            var lrect = host.Scene.AbsoluteRect(left);
            var rrect = host.Scene.AbsoluteRect(right);
            float lx = lrect.X + lrect.W / 2f, rx = rrect.X + rrect.W / 2f;
            float startY = lrect.Y + lrect.H - 20f;

            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(lx, startY), t, 11));   // id 11 → left
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(rx, startY), t, 22));   // id 22 → right
            host.RunFrame();
            // Drag both up, interleaving one frame per step. Left moves at twice the right's rate so their offsets diverge.
            for (int s = 1; s <= 16; s++)
            {
                uint ts = t + (uint)(s * 16);
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(lx, startY - s * 12f), ts, 11));
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(rx, startY - s * 6f), ts, 22));
                host.RunFrame();
            }
            host.Scene.TryGetScroll(left, out var lsc);
            host.Scene.TryGetScroll(right, out var rsc);
            // Left dragged ~192px, right ~96px (clamped to the slop-claim). Both > 0 and the left out-scrolled the right.
            bool independent = lsc.OffsetY > 120f && rsc.OffsetY > 50f && lsc.OffsetY > rsc.OffsetY + 40f;
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(lx, startY - 16 * 12f), t + 300, 11));
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(rx, startY - 16 * 6f), t + 300, 22));
            host.RunFrame();
            s_touchClockMs = t + 1000;
            Check("gate.touch.two-contacts-independent two concurrent touch ids pan two sibling scrollers independently (per-PointerId capture)",
                independent, $"leftOff={lsc.OffsetY:0} rightOff={rsc.OffsetY:0}");
        }

        // gate.touch.per-id-cancel: with two contacts panning, a PointerCancel for ONE id ends only that contact (its pan
        // freezes, no fling); the OTHER id keeps panning. The cancelled list's offset stops advancing while the live one
        // continues.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-cancel", new Size2(440, 360), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TwoListProbe());
            host.RunFrame();
            var left = ViewportWithItemCount(host.Scene, host.Scene.Root, TwoListProbe.LeftN);
            var right = ViewportWithItemCount(host.Scene, host.Scene.Root, TwoListProbe.RightN);
            var lrect = host.Scene.AbsoluteRect(left);
            var rrect = host.Scene.AbsoluteRect(right);
            float lx = lrect.X + lrect.W / 2f, rx = rrect.X + rrect.W / 2f;
            float startY = lrect.Y + lrect.H - 20f;

            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(lx, startY), t, 11));
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(rx, startY), t, 22));
            host.RunFrame();
            // Both pans claim and advance for a few steps.
            for (int s = 1; s <= 6; s++)
            {
                uint ts = t + (uint)(s * 16);
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(lx, startY - s * 12f), ts, 11));
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(rx, startY - s * 12f), ts, 22));
                host.RunFrame();
            }
            host.Scene.TryGetScroll(left, out var lMid);
            float leftFrozenAt = lMid.OffsetY;

            // Cancel ONLY id 11 (the left). Its pan dies with no fling; id 22 keeps going.
            window.QueueInput(Touch(InputKind.PointerCancel, new Point2(lx, startY - 6 * 12f), t + 7 * 16, 11));
            host.RunFrame();
            for (int s = 7; s <= 16; s++)
            {
                uint ts = t + (uint)(s * 16);
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(lx, startY - s * 12f), ts, 11));   // stray moves for the cancelled id (ignored)
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(rx, startY - s * 12f), ts, 22));
                host.RunFrame();
            }
            host.Scene.TryGetScroll(left, out var lEnd);
            host.Scene.TryGetScroll(right, out var rEnd);
            bool leftFrozen = Near(lEnd.OffsetY, leftFrozenAt, 1f);   // the cancelled contact stopped moving its list
            bool rightContinued = rEnd.OffsetY > leftFrozenAt + 40f;  // the live contact kept panning past where the left froze
            // A capture-loss is not a flick: the cancelled list must NOT be flinging.
            host.RunFrame();
            host.Scene.TryGetScroll(left, out var lAfter);
            bool leftNoFling = Near(lAfter.OffsetY, lEnd.OffsetY, 0.6f) && lAfter.Phase == 0;
            Check("gate.touch.per-id-cancel a per-id PointerCancel ends only that contact (its pan freezes, no fling) while the other id keeps panning",
                leftFrozen && rightContinued && leftNoFling,
                $"leftFrozenAt={leftFrozenAt:0} leftEnd={lEnd.OffsetY:0} rightEnd={rEnd.OffsetY:0} leftMode={lAfter.Phase}");
        }

        // gate.touch.pressed-no-hover: a touch PointerDown drives the Pressed visual exactly like a mouse press (the node
        // gets NodeFlags.Pressed AND the InteractionAnimator arms PressT via OnPressChanged) but NEVER sets hover (touch has
        // no cursor); on PointerUp the node returns to Normal — Pressed clears and NO hover is left (NOT PointerOver, which
        // is where a mouse would rest). The probe is a lone Accent button at Scene.Root (clickable, non-scrollable ⇒ a pure
        // tap, no pan-claim).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-press", new Size2(240, 120), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new HoverProbe());
            host.RunFrame();
            var r = host.Scene.AbsoluteRect(host.Scene.Root);
            var center = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);

            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, center, t, 4));
            host.RunFrame();
            bool pressedOnDown = !AnyPressed(host.Scene, host.Scene.Root).IsNull;   // Pressed visual set
            bool noHoverOnDown = AnyHovered(host.Scene, host.Scene.Root).IsNull;    // never a touch hover
            bool animArmed = host.HasActiveWork;                                    // PressT easing (OnPressChanged → SetPress)

            window.QueueInput(Touch(InputKind.PointerUp, center, t + 16, 4));
            host.RunFrame();
            bool releasedToNormal = AnyPressed(host.Scene, host.Scene.Root).IsNull   // back to Normal: no Pressed…
                                    && AnyHovered(host.Scene, host.Scene.Root).IsNull;   // …and NOT PointerOver (no cursor)
            s_touchClockMs = t + 1000;

            Check("gate.touch.pressed-no-hover a touch down drives Pressed (+ the interaction animator) with no hover; up returns to Normal, never PointerOver",
                pressedOnDown && noHoverOnDown && animArmed && releasedToNormal,
                $"pressedDown={pressedOnDown} hoverDown={!noHoverOnDown} animArmed={animArmed} normalAfterUp={releasedToNormal}");
        }

        // gate.touch.thumb-drag: a touch press landing on the conscious overlay-scrollbar thumb drives the per-PointerId
        // _scrollDragNode thumb-drag (track-fraction → SetScrollOffset) — NOT a content pan — and reveals/expands the bar
        // for the contact's duration; releasing lets the bar fade. A plain (non-virtualized) 800px-over-200px scroller so
        // the drag machinery itself is isolated 0-alloc on the hot half (no realize-edge churn to confound it — that churn
        // is the shared bound-row reconcile edge gate.touch.fling-realize-edge-is-wheel already accounts for).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-thumb", new Size2(260, 260), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ScrollProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            // Warm the scroll content node's first dirty-Mark (a one-time per-node lazy init in the scene's dirty tracking):
            // v2 §2.1 moves the offset write to phase 7 (the integrator tick), so this init now lands inside the measured
            // hot half — prime it here (a throwaway wheel down then back to the top marks + resets the content), exactly as
            // the fling gates warm the tick-write path via pre-fling contact tracking. The measured drag asserts steady state.
            // NB: does NOT advance the shared s_touchClockMs (only the per-host frame clock) — a later gate's absolute
            // timestamps must stay put (gate.touch.flick-seed-gap-invariant sits knife-edge on AssumeStoppedMs=40).
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(130, 100), 0, 0, ScrollDelta: 48f, TimestampMs: s_touchClockMs));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(130, 100), 0, 0, ScrollDelta: -1_000_000f, TimestampMs: s_touchClockMs + 16));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc0);
            float maxOff = MathF.Max(0f, sc0.ContentH - sc0.ViewportH);   // 800 − 200 = 600
            float laneX = 194f;   // the 200-wide viewport's lane sits at x∈[188,200]; the thumb is at the top at offset 0

            // Touch-grab the thumb at the top of the lane, then drag DOWN the lane in small steps — the offset must track
            // the thumb the whole way (the scrollbar owns the contact; it never page-jumps a content pan).
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(laneX, 12f), t, 8));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var grabbed);
            bool revealedOnGrab = grabbed.PointerOverScrollbar && grabbed.FadeT > 0f;   // the lane press revealed+expanded the bar

            float lastOff = grabbed.OffsetY;
            int advanceSteps = 0, regressions = 0;
            long worstHot = 0;
            const int dragSteps = 60;
            for (int s = 1; s <= dragSteps; s++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(laneX, 12f + s * 4f), t + (uint)s, 8));
                var f = host.RunFrame();
                if (f.HotPhaseAllocBytes > worstHot) worstHot = f.HotPhaseAllocBytes;
                host.Scene.TryGetScroll(vp, out var dsc);
                if (dsc.OffsetY > lastOff + 0.5f) advanceSteps++;          // tracked the thumb down a step
                else if (dsc.OffsetY < lastOff - 0.5f) regressions++;       // a mid-drag regression = silent disengage
                lastOff = dsc.OffsetY;
            }
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(laneX, 12f + dragSteps * 4f), t + dragSteps + 1, 8));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var afterUp);
            // A thumb-drag maps track-fraction → offset, so ~240px of finger travel sweeps the thumb its full track travel
            // and the offset to the content-end clamp (max=600); a 4px-axis CONTENT pan would have moved the offset by only
            // ~240px and never clamped. Reaching the clamp — monotonically, no regressions, then holding once clamped — is
            // the proof the contact drove the thumb (not a pan) and never silently disengaged mid-travel.
            bool draggedToEnd = Near(afterUp.OffsetY, maxOff, 1f);
            bool trackedMonotonic = advanceSteps >= 25 && regressions == 0;
            // After release the contact-duration reveal ends (PointerOverScrollbar dropped) so the bar can fade on the idle timer.
            bool fadesAfterRelease = !afterUp.PointerOverScrollbar;
            s_touchClockMs = t + 1000;

            Check("gate.touch.thumb-drag a touch press on the overlay scrollbar thumb drives the per-id thumb-drag (offset tracks the thumb to the content end, not a content pan), reveals the bar for the contact, releases it to fade, and stays 0-alloc on the hot half",
                revealedOnGrab && trackedMonotonic && draggedToEnd && fadesAfterRelease && worstHot == 0,
                $"revealed={revealedOnGrab} advanced={advanceSteps}/{dragSteps} regress={regressions} off={afterUp.OffsetY:0}/max={maxOff:0} fades={fadesAfterRelease} worstHot={worstHot}B");
        }

        // gate.touch.lane-page-step: a touch tap on the lane BELOW the thumb pages the scroll like a mouse lane-click, but
        // (unlike a resting mouse cursor) must NOT leave the bar's PointerOverScrollbar latched — touch has no hover to
        // clear it, so the reveal would otherwise stick forever. The bar flashes from the page scroll then fades on idle.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-lane", new Size2(260, 260), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ScrollProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            float laneX = 194f;

            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(laneX, 188f), t, 3));   // lane, below the top-anchored thumb
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(laneX, 188f), t + 16, 3));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var afterTap);
            bool paged = afterTap.OffsetY > 1f;                       // the lane tap paged the content down
            bool notLatched = !afterTap.PointerOverScrollbar;         // the lane reveal was NOT left stuck on
            s_touchClockMs = t + 1000;

            Check("gate.touch.lane-page-step a touch tap on the scrollbar lane pages the scroll and never latches the bar reveal (touch has no hover to clear it)",
                paged && notLatched,
                $"pagedTo={afterTap.OffsetY:0} pointerOverScrollbar={afterTap.PointerOverScrollbar}");
        }

        // gate.touch.rating-focal-scale: the PointerKind.Touch tag reaches RatingControl's PressInfo end-to-end (the
        // previously-dead RatingControl.cs:243 branch) — a TOUCH down sets c_touchOverScale = 1.0 so the focal star grows
        // to 2×1.0 = 2.0, vs the mouse 2×0.8 = 1.6 that w1controls.11 verifies. Same probe/geometry, touch instead of mouse.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-rating", new Size2(320, 120), 1f)); window.Show();
            var root = new RatingProbe { Initial = RatingControl.NoValueSet };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, root);
            host.RunFrame();

            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var rr = host.Scene.AbsoluteRect(rating);
            var cell0 = Child(host.Scene, Child(host.Scene, rating, 0), 0);
            var star0 = new Point2(rr.X + 8f, rr.Y + rr.H / 2f);   // star 1 centre (StarCenter(0) = 8)

            // A touch down on star 0 fires OnPointerDown (Sweep → focal at the finger) AND OnPointerPressed (PressInfo →
            // the touch device scalar). The focal cell then scales to the TOUCH peak 2.0, proving e.Kind == Touch arrived.
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, star0, t, 6));
            host.RunFrame(); host.RunFrame();
            float touchFocal = host.Scene.Paint(cell0).LocalTransform.M11;
            window.QueueInput(Touch(InputKind.PointerUp, star0, t + 16, 6));
            host.RunFrame();
            s_touchClockMs = t + 1000;

            Check("gate.touch.rating-focal-scale a touch down reaches RatingControl PressInfo (kind=Touch end-to-end) → focal star scales to the 2.0 touch peak (vs 1.6 mouse)",
                Near(touchFocal, 2.0f, 0.05f),
                $"touchFocal={touchFocal:0.00} (expect 2.00, mouse peak 1.60)");
        }

        // gate.touch.light-dismiss: a TOUCH tap outside an open light-dismiss flyout dismisses it with the SAME semantics
        // as the mouse light-dismiss (test 64) — the press lands on the full-bleed scrim (it is the topmost hit-test
        // target while a popup is open), so the existing tap path fires the scrim's OnClick = CloseTop(LightDismiss),
        // and the tap is CONSUMED by the scrim: the behind-content the scrim covers never sees the press (no click-through —
        // WinUI CPopupRoot::OnPointerPressed sets Handled=didCloseAPopup, popup.cpp:5206). Then a touch contact landing on
        // the scrim that is lost mid-gesture (PointerCancel — the per-id WM_POINTERCAPTURECHANGED path) closes nothing and
        // leaks nothing (capture loss is not a tap), and WindowBlur still light-dismisses the overlay (e4popup.1 covers the
        // blur trigger itself; here it must hold with a live touch contact resting over the scrim).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch-dismiss", new Size2(480, 360), 1f)); window.Show();
            var probe = new TouchDismissProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var svc = probe.Service!;
            Func<Element> menu = () => new BoxEl
            {
                Width = 140, Direction = 1,
                Children = [new BoxEl { Height = 32, Role = AutomationRole.MenuItem, OnClick = () => svc.CloseTop(), Children = [Ui.Text("Item A")] }],
            };
            void Settle() { for (int i = 0; i < 16; i++) host.RunFrame(); }
            bool MenuOpen() => !FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;
            var outside = new Point2(460, 350);   // far from the BottomLeft popup over the (20,20) anchor — over the behind-content

            // (1) Open → touch tap on the scrim outside the popup dismisses it (down+up over the same scrim point, monotonic stamps).
            svc.Open(() => probe.Anchor, menu, FlyoutPlacement.BottomLeft);
            host.RunFrame();
            bool openedT = MenuOpen();
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, outside, t, 3));
            window.QueueInput(Touch(InputKind.PointerUp, outside, t + 16, 3));
            host.RunFrame();
            Settle();
            s_touchClockMs = t + 1000;
            bool touchDismissed = !MenuOpen();
            bool noClickThrough = probe.BehindClicks == 0;   // the scrim ate the tap — behind-content never clicked

            // (2) Per-id PointerCancel mid-gesture: a touch resting on the scrim that loses capture closes nothing, leaks nothing.
            svc.Open(() => probe.Anchor, menu, FlyoutPlacement.BottomLeft);
            host.RunFrame();
            uint t2 = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, outside, t2, 4));
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerCancel, outside, t2 + 16, 4));
            host.RunFrame();
            s_touchClockMs = t2 + 1000;
            bool cancelKeptOpen = MenuOpen() && probe.BehindClicks == 0;   // cancel is not a tap: no dismiss, no click-through

            // (3) WindowBlur still light-dismisses with a touch contact live over the scrim (modal would stay; this is light-dismiss).
            window.QueueInput(new InputEvent(InputKind.WindowBlur, default, 0, 0));
            host.RunFrame();
            Settle();
            bool blurDismissed = !MenuOpen();

            Check("gate.touch.light-dismiss a touch tap outside an open light-dismiss flyout dismisses it (mouse semantics) and is consumed by the scrim (no click-through); a per-id PointerCancel over the scrim dismisses nothing; WindowBlur still closes it with a live touch contact",
                openedT && touchDismissed && noClickThrough && cancelKeptOpen && blurDismissed,
                $"opened={openedT} touchDismiss={touchDismissed} noClickThrough={noClickThrough} (behindClicks={probe.BehindClicks}) cancelKeptOpen={cancelKeptOpen} blurDismiss={blurDismissed}");
        }
    }

    static void PinchZoomChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.touch4.pinch-out-doubles: spreading the two contacts to 2× their start separation about a fixed midpoint
        // doubles ZoomFactor (1 → 2), applies it as the content node's LocalTransform scale (M11 == M22 == ZoomFactor),
        // keeps the content point under the midpoint fixed (the focal-point offset), and marks NO relayout (every pinch
        // move frame renders transform-only — Rendered == false; the content's model Bounds stay 800px).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pinch-out", new Size2(200, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new PinchZoomProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            var content = sc0.ContentNode;
            float contentHBefore = host.Scene.Bounds(content).H;

            // Midpoint (100,100); A=(100,70) B=(100,130) ⇒ sep 60. Spread to A=(100,40) B=(100,160) ⇒ sep 120 = 2×.
            // Drive the move frames manually so each frame's Rendered flag is observable (no virtual re-realize on a plain
            // ScrollEl ⇒ a pure transform frame returns Rendered == false: the LayoutDirty-never-marked proof).
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(100, 70), t, 1)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(100, 130), t + 16, 2)); host.RunFrame();   // 2nd contact opens the pinch
            bool anyRelayout = false;
            for (int i = 1; i <= 10; i++)
            {
                float fa = 70f - (70f - 40f) * i / 10f;     // A: 70 → 40
                float fb = 130f + (160f - 130f) * i / 10f;   // B: 130 → 160
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(100, fa), t + 16 + (uint)i * 16, 1));
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(100, fb), t + 16 + (uint)i * 16, 2));
                var f = host.RunFrame();
                if (f.Rendered) anyRelayout = true;   // a relayout/reconcile would flip Rendered true
            }
            s_touchClockMs = t + 1000;
            host.Scene.TryGetScroll(vp, out var scZ);
            var lt = host.Scene.Paint(content).LocalTransform;
            float contentHAfter = host.Scene.Bounds(content).H;

            bool zoomDoubled = Near(scZ.ZoomFactor, 2.0f, 0.06f);
            bool transformScaled = Near(lt.M11, scZ.ZoomFactor, 0.01f) && Near(lt.M22, scZ.ZoomFactor, 0.01f);
            // Focal point under the midpoint (y=100, viewport-local 100) stays put: newOff = z·100 − 100 = 100 at z=2.
            bool focalFixed = Near(scZ.OffsetY, 100f, 8f);
            bool noRelayout = !anyRelayout && Near(contentHAfter, contentHBefore);
            Check("gate.touch4.pinch-out-doubles a 2× spread doubles ZoomFactor (1→2) applied as the content LocalTransform scale about the gesture midpoint (focal point fixed), with NO relayout (every move frame transform-only; model bounds unchanged)",
                zoomDoubled && transformScaled && focalFixed && noRelayout,
                $"zoom={scZ.ZoomFactor:0.00} M11={lt.M11:0.00} M22={lt.M22:0.00} offY={scZ.OffsetY:0} (focal~100) anyRelayout={anyRelayout} contentH {contentHBefore:0}->{contentHAfter:0}");
        }

        // gate.touch4.clamp: pinch FAR out clamps at MaxZoom (10.0); pinch FAR in clamps at MinZoom (0.1). The ScrollEl
        // defaults (Min 0.1 / Max 10.0 — ScrollPresenter.h:63-64) bound the factor; the transform never exceeds them.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pinch-clamp", new Size2(200, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new PinchZoomProbe());
            host.RunFrame();
            var vp = host.Scene.Root;

            // Spread WAY out: A 70→ -300, B 130→ 500 (sep 60 → 800 ≈ 13× ⇒ clamps at 10).
            PinchGesture(window, host, new Point2(100, 70), new Point2(100, 130), new Point2(100, -300), new Point2(100, 500), 12, 1, 2, 16f);
            host.Scene.TryGetScroll(vp, out var scMax);
            float maxZoom = scMax.ZoomFactor;
            float maxM11 = host.Scene.Paint(scMax.ContentNode).LocalTransform.M11;
            // Lift both to end the session (commit), reset to a fresh host for the pinch-in leg.
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(100, -300), s_touchClockMs, 1)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(100, 500), s_touchClockMs + 16, 2)); host.RunFrame();
            s_touchClockMs += 1000;

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("pinch-clamp2", new Size2(200, 200), 1f)); window2.Show();
            using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, new PinchZoomProbe());
            host2.RunFrame();
            var vp2 = host2.Scene.Root;
            // Pinch WAY in: A 40→98, B 160→102 (sep 120 → 4 ≈ 0.033× ⇒ clamps at 0.1).
            PinchGesture(window2, host2, new Point2(100, 40), new Point2(100, 160), new Point2(100, 98), new Point2(100, 102), 12, 3, 4, 16f);
            host2.Scene.TryGetScroll(vp2, out var scMin);
            float minZoom = scMin.ZoomFactor;

            bool maxOk = Near(maxZoom, 10f, 0.01f) && Near(maxM11, 10f, 0.01f);
            bool minOk = Near(minZoom, 0.1f, 0.01f);
            Check("gate.touch4.clamp pinch-out clamps ZoomFactor at MaxZoom (10.0) and pinch-in clamps at MinZoom (0.1) — the WinUI ScrollPresenter defaults",
                maxOk && minOk, $"maxZoom={maxZoom:0.00} (M11={maxM11:0.00}) minZoom={minZoom:0.000}");
        }

        // gate.touch4.sweeps-pan: a pinch sweeps the single-finger Pan — during a pinch the content does NOT scroll as a
        // pan (the offset follows the ZOOM focal math, not a finger-drag delta). A pure-spread pinch about a FIXED midpoint
        // reaches z≈2 and offset≈100 (the focal value), which a finger-pan (offset ≈ anchor − fingerDelta ≈ 30) could
        // never produce — so the Pan was swept, not co-driven. (No scroll-from-pan during the pinch.)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pinch-sweep", new Size2(200, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new PinchZoomProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            PinchGesture(window, host, new Point2(100, 70), new Point2(100, 130), new Point2(100, 40), new Point2(100, 160), 10, 1, 2, 16f);
            host.Scene.TryGetScroll(vp, out var sc);
            // The focal offset (≈100) is unreachable by a single-finger pan of the ≤30px finger travel (which would yield
            // ≈30) — so a non-zero, zoom-consistent offset with z≈2 proves the Pan was swept and the zoom drove the offset.
            bool zoomed = Near(sc.ZoomFactor, 2.0f, 0.06f);
            bool offsetIsZoomFocal = sc.OffsetY > 60f;   // » the ~30px a finger-pan would have produced
            Check("gate.touch4.sweeps-pan a pinch sweeps the single-finger Pan (the content scrolls by the zoom focal math, not a pan delta) — no scroll-from-pan during the pinch",
                zoomed && offsetIsZoomFocal, $"zoom={sc.ZoomFactor:0.00} offY={sc.OffsetY:0} (focal~100, a finger-pan would be ~30)");
        }

        // gate.touch4.per-id-cancel: a PointerCancel for ONE finger mid-pinch ends the session GRACEFULLY — the committed
        // ZoomFactor is kept (a partial pinch keeps its magnification), and the cancelled finger's stray moves + the
        // surviving finger no longer drive any zoom (the session is closed). No crash, deterministic.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pinch-cancel", new Size2(200, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new PinchZoomProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(100, 70), t, 1)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(100, 130), t + 16, 2)); host.RunFrame();
            // Spread partway (sep 60 → 90 ⇒ z ≈ 1.5).
            for (int i = 1; i <= 6; i++)
            {
                float fa = 70f - 15f * i / 6f, fb = 130f + 15f * i / 6f;
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(100, fa), t + 16 + (uint)i * 16, 1));
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(100, fb), t + 16 + (uint)i * 16, 2));
                host.RunFrame();
            }
            host.Scene.TryGetScroll(vp, out var scMid);
            float zoomAtCancel = scMid.ZoomFactor;
            // Cancel finger 1 mid-pinch.
            window.QueueInput(Touch(InputKind.PointerCancel, new Point2(100, 55), t + 200, 1)); host.RunFrame();
            host.Scene.TryGetScroll(vp, out var scAfterCancel);
            // Stray moves for BOTH ids after the cancel must not change the zoom (session closed).
            window.QueueInput(Touch(InputKind.PointerMove, new Point2(100, 200), t + 220, 2)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerMove, new Point2(100, 10), t + 236, 1)); host.RunFrame();
            host.Scene.TryGetScroll(vp, out var scStray);
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(100, 200), t + 260, 2)); host.RunFrame();
            s_touchClockMs = t + 1000;

            bool committed = zoomAtCancel > 1.2f && Near(scAfterCancel.ZoomFactor, zoomAtCancel, 0.02f);
            bool noPostCancelZoom = Near(scStray.ZoomFactor, scAfterCancel.ZoomFactor, 0.02f);
            Check("gate.touch4.per-id-cancel a per-id PointerCancel mid-pinch ends the session gracefully — the committed ZoomFactor is kept and post-cancel stray moves drive no further zoom",
                committed && noPostCancelZoom, $"zoomAtCancel={zoomAtCancel:0.00} afterCancel={scAfterCancel.ZoomFactor:0.00} afterStray={scStray.ZoomFactor:0.00}");
        }

        // gate.touch4.pan-continuation: when the FIRST finger lifts, the pinch commits and the SURVIVING finger continues
        // as a pan over the (now zoomed) content (WinUI continues the manipulation with the remaining contact). After a
        // pinch-out, lift one finger, then drag the survivor — the content scrolls (the offset moves).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pinch-cont", new Size2(200, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new PinchZoomProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            // Pinch out to z≈2 (both fingers DOWN at the end).
            PinchGesture(window, host, new Point2(100, 70), new Point2(100, 130), new Point2(100, 40), new Point2(100, 160), 10, 1, 2, 16f);
            host.Scene.TryGetScroll(vp, out var scZoomed);
            float offBeforeContinue = scZoomed.OffsetY;
            float zoomCommitted = scZoomed.ZoomFactor;
            // Lift finger 1 (B survives at y=160). The survivor re-anchors as an already-claimed pan from (100,160).
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(100, 40), t, 1)); host.RunFrame();
            // Drag the survivor UP by 60px → content scrolls down (offset increases).
            for (int i = 1; i <= 6; i++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(100, 160 - 10f * i), t + (uint)i * 16, 2));
                host.RunFrame();
            }
            host.Scene.TryGetScroll(vp, out var scPan);
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(100, 100), t + 200, 2)); host.RunFrame();
            s_touchClockMs = t + 1000;

            bool zoomKept = Near(scPan.ZoomFactor, zoomCommitted, 0.02f);   // the pan continuation never changes the factor
            bool survivorPanned = scPan.OffsetY > offBeforeContinue + 30f;  // dragging the survivor up scrolled the content
            Check("gate.touch4.pan-continuation lifting one pinch finger commits the zoom and the surviving finger continues as a pan over the zoomed content (WinUI manipulation continuation)",
                zoomKept && survivorPanned, $"zoomKept={scPan.ZoomFactor:0.00} (was {zoomCommitted:0.00}) off {offBeforeContinue:0}->{scPan.OffsetY:0}");
        }

        // gate.touch4.alloc-zero: a 20-frame pinch allocates 0 managed bytes on the hot half (the dispatch + transform +
        // re-realize machinery is fixed-storage; no LINQ/closures/boxing on the per-event path). Reuses the two-clock harness.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pinch-alloc", new Size2(200, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new PinchZoomProbe());
            host.RunFrame();
            // Warm the slab/arena once (first pinch primes any lazily-grown harness buffers), then measure the 2nd.
            PinchGesture(window, host, new Point2(100, 70), new Point2(100, 130), new Point2(100, 45), new Point2(100, 155), 8, 1, 2, 16f);
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(100, 45), s_touchClockMs, 1)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(100, 155), s_touchClockMs + 16, 2)); host.RunFrame();
            s_touchClockMs += 1000;

            long worst = PinchGesture(window, host, new Point2(100, 70), new Point2(100, 130), new Point2(100, 30), new Point2(100, 170), 20, 5, 6, 16f);
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(100, 30), s_touchClockMs, 5)); var fu = host.RunFrame();
            if (fu.HotPhaseAllocBytes > worst) worst = fu.HotPhaseAllocBytes;
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(100, 170), s_touchClockMs + 16, 6)); host.RunFrame();
            s_touchClockMs += 1000;
            Check("gate.touch4.alloc-zero a 20-frame pinch (down→spread→up) allocates 0 managed bytes on the hot half (fixed-storage dispatch + zoom transform + re-realize)",
                worst == 0, $"worstHotAlloc={worst}B");
        }

        // gate.touch4.dispatch-alloc-zero: the COMPANION instrument (the gate.arena.dispatch-alloc-zero pattern) — a
        // GetAllocatedBytesForCurrentThread delta wrapped DIRECTLY around host.Input.Dispatch over a full pinch, so the
        // measured window is the per-event PINCH path itself: the second-contact TryOpenPinch (OnSecondContact + the arena
        // sweep), UpdatePinch (the scale + the SetScrollOffset content-transform write) on every coalesced move pair, and the
        // pinch-end commit + pan continuation. A stray new[]/closure on any of those is caught here. Dispatch via a reused
        // 1-element buffer so the span itself never allocates; a warm pinch JITs the path, the second is the measured window.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pinch-dispatch-alloc", new Size2(200, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new PinchZoomProbe());
            host.RunFrame();
            var one = new InputEvent[1];

            // Drive one full pinch (down A, down B opens it, 20 spread move-pairs, lift both) through the real dispatcher.
            void DrivePinch(uint t0, uint idA, uint idB)
            {
                one[0] = Touch(InputKind.PointerDown, new Point2(100, 70), t0, idA); host.Input.Dispatch(one);
                one[0] = Touch(InputKind.PointerDown, new Point2(100, 130), t0 + 16, idB); host.Input.Dispatch(one);
                for (int i = 1; i <= 20; i++)
                {
                    float fa = 70f - 40f * i / 20f, fb = 130f + 40f * i / 20f;
                    one[0] = Touch(InputKind.PointerMove, new Point2(100, fa), t0 + 16 + (uint)i * 16, idA); host.Input.Dispatch(one);
                    one[0] = Touch(InputKind.PointerMove, new Point2(100, fb), t0 + 16 + (uint)i * 16, idB); host.Input.Dispatch(one);
                }
                one[0] = Touch(InputKind.PointerUp, new Point2(100, 30), t0 + 400, idA); host.Input.Dispatch(one);
                one[0] = Touch(InputKind.PointerUp, new Point2(100, 170), t0 + 416, idB); host.Input.Dispatch(one);
            }

            DrivePinch(s_touchClockMs, 1, 2);   // warm (JIT the pinch path; not measured)
            s_touchClockMs += 2000;
            uint t = s_touchClockMs;
            long before = GC.GetAllocatedBytesForCurrentThread();
            DrivePinch(t, 3, 4);                 // measured: the per-event pinch path only
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            s_touchClockMs = t + 2000;
            Check("gate.touch4.dispatch-alloc-zero the per-event pinch path itself — TryOpenPinch (OnSecondContact + arena sweep) + UpdatePinch (scale + SetScrollOffset transform) per coalesced move + the pinch-end commit/continuation, measured by a delta wrapped DIRECTLY around host.Input.Dispatch — allocates 0 managed bytes across a full pinch",
                delta == 0, $"dispatchDelta={delta}B (direct phase-2 dispatch delta over a 40-move pinch)");
        }
    }

    static void TouchSnapOverscrollChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.touch4.fling-snap-lands-on-snap: a flick over a snap-configured list (SnapInterval = RowH) retargets its
        // decay so the offset settles on an EXACT RowH multiple (interior — the content is large, so the snap target is
        // never the clamp). Without snapping the 0.95/s decay settles wherever v_min/k lands (an arbitrary sub-row offset).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch4-snap", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new SnapFlingProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            host.Scene.ScrollRef(vp).SnapInterval = SnapFlingProbe.RowH;   // survives reconcile (the patch never touches snap fields)

            // A modest flick (the 0.95/s decay is near-frictionless, so even a slow flick coasts many rows). Settle to the
            // snap with a generous frame budget (the free-fling gate uses 600; a snap target can be ~tens of rows away).
            TouchGesture(window, host, new Point2(150, 300), new Point2(150, 240), 10, pointerId: 51, msPerStep: 16f);
            int settledAt = -1;
            for (int i = 0; i < 4000; i++) { host.RunFrame(); host.Scene.TryGetScroll(vp, out var s); if (s.Phase == 0) { settledAt = i; break; } }
            host.Scene.TryGetScroll(vp, out var settled);
            float rem = settled.OffsetY % SnapFlingProbe.RowH;
            float distToSnap = MathF.Min(rem, SnapFlingProbe.RowH - rem);   // distance to the nearest RowH multiple
            float maxOff = MathF.Max(0f, settled.ContentH - settled.ViewportH);
            bool onSnap = distToSnap < 0.5f;
            bool interior = settled.OffsetY > SnapFlingProbe.RowH && settled.OffsetY < maxOff - SnapFlingProbe.RowH;   // a real snap, not the clamp
            bool settledMode = settled.Phase == 0 && settled.FlingVelocity == 0f;
            Check("gate.touch4.fling-snap-lands-on-snap a touch flick over a snap-configured virtual list (SnapInterval=RowH) retargets its friction decay to settle EXACTLY on a RowH multiple, interior to the content (not the clamp)",
                onSnap && interior && settledMode && settledAt >= 0,
                $"offset={settled.OffsetY:0.###} distToSnap={distToSnap:0.###} interval={SnapFlingProbe.RowH} interior={interior} mode={settled.Phase} settledAtFrame={settledAt}");
        }

        // gate.touch4.snap-fling-dt-invariant: the integrator-determinism sweep, extended to a SNAP fling. The same event
        // script replayed at dt ∈ {8.33, 16.67, 33.3} ms lands on the IDENTICAL snap offset (the snap value is a property
        // of the event-clock velocity + the snap grid, NOT the animation timestep — so unlike a free fling, which settles
        // at dt-dependent offsets, a SNAP fling settles at the same value every dt). And the arena resolution trace stays
        // identical across the sweep (the validation.md:1145 property — the snap landing is downstream of the arbitration).
        {
            string r833 = SnapFlingResolutionTrace(strings, 8.33f, out float o833);
            string r1667 = SnapFlingResolutionTrace(strings, 16.67f, out float o1667);
            string r333 = SnapFlingResolutionTrace(strings, 33.3f, out float o333);
            bool traceIdentical = r833 == r1667 && r1667 == r333;
            bool landingsIdentical = Near(o833, o1667, 0.5f) && Near(o1667, o333, 0.5f);   // dt-INVARIANT (the snap pins the landing)
            float interval = SnapFlingProbe.RowH;
            float rem = o1667 % interval; float dist = MathF.Min(rem, interval - rem);
            bool onSnap = dist < 0.5f;
            if (!traceIdentical) Console.WriteLine($"    [snap dt-sweep diff]\n8.33:\n{r833}16.67:\n{r1667}33.3:\n{r333}");
            Check("gate.touch4.snap-fling-dt-invariant the §12.6 integrator sweep extended to a SNAP fling: the same event script at dt ∈ {8.33,16.67,33.3}ms produces an IDENTICAL arena resolution trace AND lands on the IDENTICAL snap offset (the snap pins the landing dt-invariantly, unlike a free fling), the offset an exact snap multiple",
                traceIdentical && landingsIdentical && onSnap,
                $"traceIdentical={traceIdentical} landings=({o833:0.#},{o1667:0.#},{o333:0.#}) identical={landingsIdentical} distToSnap={dist:0.###}");
        }

        // gate.touch4.overscroll-springback: a touch pan dragging PAST the top clamp (at offset 0, finger pulls DOWN)
        // produces a transient damped displacement band (OverscrollPx != 0) WHILE OffsetY stays pinned at 0 — the band is a
        // separate visual term, the clamp contract is never relaxed. On release the band springs back to EXACTLY 0 (phase-7
        // StepSpring). Asserted via ScrollState (recorder/state, not pixels): the offset NEVER leaves [0, max] across the
        // whole drag+release, the band peaks while dragging, and it settles at 0.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch4-overscroll", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            float maxOff = MathF.Max(0f, sc0.ContentH - sc0.ViewportH);

            // Drive a pan past the TOP: down at y=120, then pull the finger DOWN (y increases) to y=300 — content wants to
            // go above offset 0 ⇒ clamps at 0, the excess becomes a negative band. Manual moves so we can read the band
            // mid-drag and confirm the offset never goes negative.
            uint t = s_touchClockMs;
            var ev = new InputEvent[1];
            ev[0] = Touch(InputKind.PointerDown, new Point2(150, 120), t, 61); host.Input.Dispatch(ev); host.RunFrame();
            float worstNegOffset = 0f, peakBand = 0f, worstOverMax = 0f;
            for (int i = 1; i <= 12; i++)
            {
                t += 16;
                ev[0] = Touch(InputKind.PointerMove, new Point2(150, 120 + i * 15), t, 61); host.Input.Dispatch(ev); host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                if (s.OffsetY < worstNegOffset) worstNegOffset = s.OffsetY;
                if (s.OffsetY - maxOff > worstOverMax) worstOverMax = s.OffsetY - maxOff;
                if (MathF.Abs(s.OverscrollPx) > MathF.Abs(peakBand)) peakBand = s.OverscrollPx;
            }
            host.Scene.TryGetScroll(vp, out var dragging);
            bool bandWhileDragging = MathF.Abs(dragging.OverscrollPx) > 1f;     // a real displacement past the edge
            bool offsetPinned = dragging.OffsetY == 0f;                          // the clamp held — offset never went negative
            float capLimit = TouchFlingSettleProbe_ViewportH(host, vp) * 0.1f;   // WinUI 10% overpan cap
            bool bandCapped = MathF.Abs(peakBand) <= capLimit + 0.5f;            // damping asymptotes to the cap

            // Release: the band springs back to exactly 0 (and the offset still never leaves [0, max]).
            t += 16;
            ev[0] = Touch(InputKind.PointerUp, new Point2(150, 300), t, 61); host.Input.Dispatch(ev); host.RunFrame();
            s_touchClockMs = t + 1000;
            bool offsetOk = true;
            float bandAfter = 1f;
            for (int i = 0; i < 200; i++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                if (s.OffsetY < -0.001f || s.OffsetY > maxOff + 0.001f) offsetOk = false;
                bandAfter = s.OverscrollPx;
                if (s.OverscrollPx == 0f && s.Phase == 0) break;
            }
            bool sprungToZero = bandAfter == 0f;
            Check("gate.touch4.overscroll-springback a touch pan past the top clamp shows a damped displacement band (peaks ≤ 10%-viewport cap) WHILE OffsetY stays pinned at 0 (offset never negative, never > max across the whole drag), then springs back to EXACTLY 0 on release",
                bandWhileDragging && offsetPinned && bandCapped && worstNegOffset == 0f && worstOverMax <= 0f && sprungToZero && offsetOk,
                $"peakBand={peakBand:0.##} cap={capLimit:0.##} offsetPinned={offsetPinned} worstNeg={worstNegOffset:0.##} sprungTo={bandAfter:0.###} offsetOk={offsetOk}");
        }

        // gate.touch4.wheel-hard-clamps-no-band: a WHEEL past the top edge (negative delta at offset 0) stays HARD-clamped
        // — OffsetY pinned at 0, and NO overscroll band is produced (the band is touch-pan-only; wheel/keyboard/programmatic
        // are never rubber-banded, the SetScrollOffset clamp contract). The existing fling-stops-at-clamp gate stays green.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch4-wheel-clamp", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            var ptr = new Point2(150, 200);
            // Already at offset 0 (top). A big negative wheel delta tries to go above the top: must stay 0 with no band.
            for (int i = 0; i < 5; i++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, -50_000f)); host.RunFrame(); }
            host.Scene.TryGetScroll(vp, out var top);
            bool topClamped = top.OffsetY == 0f && top.OverscrollPx == 0f;
            // And past the BOTTOM: scroll way down, then keep wheeling past max — pinned at max, still no band.
            for (int i = 0; i < 30; i++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 50_000f)); host.RunFrame(); }
            host.Scene.TryGetScroll(vp, out var bot);
            float maxOff = MathF.Max(0f, bot.ContentH - bot.ViewportH);
            bool botClamped = Near(bot.OffsetY, maxOff, 0.5f) && bot.OverscrollPx == 0f;
            Check("gate.touch4.wheel-hard-clamps-no-band a wheel past the top AND past the bottom stays hard-clamped (OffsetY pinned at the boundary) with NO overscroll band — the rubber band is touch-pan-only, the clamp contract is never relaxed for wheel/keyboard/programmatic",
                topClamped && botClamped,
                $"top=(off {top.OffsetY:0},band {top.OverscrollPx:0.##}) bottom=(off {bot.OffsetY:0}->max {maxOff:0},band {bot.OverscrollPx:0.##})");
        }

        // gate.touch4.alloc-zero: the overscroll drag + spring-back + a snap fling sequence allocates 0 managed bytes on
        // the hot half. The struct-only bound list (Fill channel only) keeps the reconcile edge clean, so the band write,
        // the phase-7 spring step, the snap retarget + decay, and the SetScrollOffset re-realize must all be 0-alloc.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch4-alloc", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new SnapFlingProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            host.Scene.ScrollRef(vp).SnapInterval = SnapFlingProbe.RowH;
            for (int i = 0; i < 30; i++) host.RunFrame();   // drain residual

            // Warm the overscroll + snap paths (JIT) outside the measured window.
            DriveOverscrollAndSnap(window, host, vp);
            for (int i = 0; i < 40; i++) host.RunFrame();

            long worst = DriveOverscrollAndSnap(window, host, vp);   // returns worst hot-phase alloc across the sequence
            for (int i = 0; i < 60; i++) { var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
            Check("gate.touch4.snap-overscroll-alloc-zero an overscroll pan-past-edge + spring-back + snap fling sequence allocates 0 managed bytes on the hot half (the band write, the phase-7 spring step, the snap retarget + friction decay, and the virtual re-realize stayed clean)",
                worst == 0, $"worstHotAlloc={worst}B");
        }
    }

    static float TouchFlingSettleProbe_ViewportH(AppHost host, NodeHandle vp)
    {
        host.Scene.TryGetScroll(vp, out var sc);
        return sc.Orientation == 1 ? sc.ViewportW : sc.ViewportH;
    }

    static long DriveOverscrollAndSnap(HeadlessWindow window, AppHost host, NodeHandle vp)
    {
        long worst = 0;
        var ev = new InputEvent[1];
        uint t = s_touchClockMs;
        // First flick up to an interior position (seeds a snap fling), let it settle.
        worst = Math.Max(worst, TouchGesture(window, host, new Point2(150, 360), new Point2(150, 140), 12, pointerId: 71, msPerStep: 16f));
        for (int i = 0; i < 120; i++) { var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; host.Scene.TryGetScroll(vp, out var s); if (s.Phase == 0) break; }
        // Now drag past the bottom: pull the finger UP hard from a low anchor so the content runs past max → band.
        t = s_touchClockMs;
        ev[0] = Touch(InputKind.PointerDown, new Point2(150, 360), t, 72); host.Input.Dispatch(ev); { var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
        for (int i = 1; i <= 10; i++)
        {
            t += 16;
            ev[0] = Touch(InputKind.PointerMove, new Point2(150, 360 - i * 18), t, 72); host.Input.Dispatch(ev);
            var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes;
        }
        t += 16;
        ev[0] = Touch(InputKind.PointerUp, new Point2(150, 180), t, 72); host.Input.Dispatch(ev); { var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
        s_touchClockMs = t + 1000;
        for (int i = 0; i < 60; i++) { var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; host.Scene.TryGetScroll(vp, out var s); if (s.OverscrollPx == 0f && s.Phase == 0) break; }
        return worst;
    }

    static string SnapFlingResolutionTrace(StringTable strings, float dtMs, out float settledOff)
    {
        var fonts = new HeadlessFontSystem(strings);
        var rec = new GestureArenaRecorder();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("det-snap-fling-dt", new Size2(360, 460), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new SnapFlingProbe(), frameTime: new FixedFrameTimeSource(dtMs));
        host.RunFrame();
        host.Scene.ScrollRef(host.Scene.Root).SnapInterval = SnapFlingProbe.RowH;
        host.Input.Arena.Recorder = rec;
        var vp = host.Scene.Root;
        // A modest, fixed flick (event clock identical across the dt sweep ⇒ identical sampled velocity ⇒ identical snap
        // target). Generous settle budget so even the finest dt (8.33ms ⇒ ~4× the frames of 33.3ms) fully lands.
        TouchGesture(window, host, new Point2(150, 300), new Point2(150, 240), 10, pointerId: 93, msPerStep: 16f);
        for (int i = 0; i < 8000; i++) { host.RunFrame(); host.Scene.TryGetScroll(vp, out var sc); if (sc.Phase == 0) break; }
        host.Scene.TryGetScroll(vp, out var settled);
        settledOff = settled.OffsetY;
        return rec.ResolutionSignature();
    }

    static void Touch4SipChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // (1) SHOW on touch focus / NOT on mouse focus: tap an EditableText with a touch contact ⇒ TryShowTouchKeyboard
        //     fires exactly once; a fresh field clicked with a MOUSE never raises the panel.
        int touchShow, mouseShow;
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sip-show", new Size2(360, 320), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchEditInScrollerProbe());
            host.RunFrame();
            var ti = (HeadlessTextInput)window.TextInput;
            var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.Text);
            var fr = host.Scene.AbsoluteRect(field);
            var p = new Point2(fr.X + 6f, fr.Y + fr.H / 2f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, p, t, 60)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, p, t + 16, 60)); host.RunFrame();
            s_touchClockMs = t + 1000;
            touchShow = ti.ShowCount;
        }
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sip-mouse", new Size2(360, 320), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchEditInScrollerProbe());
            host.RunFrame();
            var ti = (HeadlessTextInput)window.TextInput;
            var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.Text);
            ClickNode(host, window, field);   // mouse PointerDown+Up (PointerKind.Mouse) → focuses, must NOT show the SIP
            mouseShow = ti.ShowCount;
        }
        Check("gate.touch4.sip.show-on-touch a touch focus on an editable field shows the touch keyboard exactly once; a mouse focus never does",
            touchShow == 1 && mouseShow == 0, $"touchShow={touchShow} mouseShow={mouseShow}");

        // (2) HIDE on focus loss: after the touch focus, blur the field (focus → null) ⇒ TryHideTouchKeyboard fires.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sip-hide", new Size2(360, 320), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchEditInScrollerProbe());
            host.RunFrame();
            var ti = (HeadlessTextInput)window.TextInput;
            var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.Text);
            var fr = host.Scene.AbsoluteRect(field);
            var p = new Point2(fr.X + 6f, fr.Y + fr.H / 2f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, p, t, 61)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, p, t + 16, 61)); host.RunFrame();
            s_touchClockMs = t + 1000;
            int hideBefore = ti.HideCount;
            host.Input.SetFocus(NodeHandle.Null);   // focus leaves the editor for a non-editable target (WinUI hides the SIP)
            host.RunFrame();
            Check("gate.touch4.sip.hide-on-blur focus loss from the editor dismisses the touch keyboard",
                hideBefore == 0 && ti.HideCount >= 1 && !ti.TouchKeyboardShown,
                $"showCount={ti.ShowCount} hideBefore={hideBefore} hideCount={ti.HideCount} shown={ti.TouchKeyboardShown}");
        }

        // (3) REFLOW on Showing: touch-focus a field that sits low in a scroller, then fire a simulated InputPane Showing
        //     OccludedRect that covers it ⇒ the scroller scrolls the field's caret ABOVE the pane top (offset moved to
        //     expose the caret), and an empty (Hiding) rect leaves the clamp untouched (a hidden pane needs no reflow).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sip-reflow", new Size2(220, 240), 1f)); window.Show();
            var probe = new TouchSipReflowProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ti = (HeadlessTextInput)window.TextInput;
            var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.Text);
            var vp = FindScrollable(host.Scene, host.Scene.Root);
            // Touch-focus the field (its Showing-driven reflow uses the focused node).
            var fr0 = host.Scene.AbsoluteRect(field);
            var p = new Point2(fr0.X + 6f, fr0.Y + fr0.H / 2f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, p, t, 62)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, p, t + 16, 62)); host.RunFrame();
            s_touchClockMs = t + 1000;

            host.Scene.TryGetScroll(vp, out var sc0);
            float offBefore = sc0.OffsetY;
            var fieldBefore = host.Scene.AbsoluteRect(field);
            // The pane covers the bottom of the viewport from y = paneTop down — chosen to overlap the field's bottom edge
            // (the field is ~180 DIP down in a 240-tall viewport, so a paneTop above its bottom forces a reflow).
            float paneTop = fieldBefore.Y + 10f;   // pane top sits 10 DIP into the field ⇒ a real occlusion
            ti.FireOccludedRect(new RectF(0f, paneTop, 200f, 240f - paneTop));
            host.RunFrame();

            host.Scene.TryGetScroll(vp, out var sc1);
            float offAfter = sc1.OffsetY;
            var fieldAfter = host.Scene.AbsoluteRect(field);
            bool moved = offAfter > offBefore + 0.5f;          // the viewport scrolled the content up
            bool exposed = fieldAfter.Y + fieldAfter.H <= paneTop + 0.5f;   // the field's bottom now clears the pane top

            // Hiding (empty rect) must NOT move the offset further (a hidden pane has nothing to clear).
            ti.FireOccludedRect(default);
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc2);
            bool hideNoReflow = MathF.Abs(sc2.OffsetY - offAfter) < 0.5f;

            Check("gate.touch4.sip.reflow a Showing OccludedRect scrolls the focused editor's caret above the pane; Hiding leaves the clamp untouched",
                moved && exposed && hideNoReflow,
                $"off {offBefore:0.#}->{offAfter:0.#} fieldBottom {fieldBefore.Y + fieldBefore.H:0.#}->{fieldAfter.Y + fieldAfter.H:0.#} paneTop={paneTop:0.#} moved={moved} exposed={exposed} hideNoReflow={hideNoReflow}");
        }

        // (4) 0-ALLOC steady: the SIP show/reflow path runs at focus/pane edges, never per frame — steady frames after a
        //     touch focus + a Showing reflow allocate 0 managed bytes on the hot half (the seam is edge-driven).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sip-alloc", new Size2(220, 240), 1f)); window.Show();
            var probe = new TouchSipReflowProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ti = (HeadlessTextInput)window.TextInput;
            var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.Text);
            var fr = host.Scene.AbsoluteRect(field);
            var p = new Point2(fr.X + 6f, fr.Y + fr.H / 2f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, p, t, 63)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, p, t + 16, 63)); host.RunFrame();
            s_touchClockMs = t + 1000;
            ti.FireOccludedRect(new RectF(0f, fr.Y + 10f, 200f, 200f));
            host.RunFrame();
            long worst = 0;
            for (int i = 0; i < 10; i++) { var fs = host.RunFrame(); if (fs.HotPhaseAllocBytes > worst) worst = fs.HotPhaseAllocBytes; }
            Check("gate.touch4.sip.alloc-zero steady frames after a SIP touch-focus + Showing reflow allocate 0 managed bytes on the hot half",
                worst == 0, $"worstHotAlloc={worst}B");
        }
    }

    static void Touch4HoldWakeChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.touch4.hold-fires-context: row 0 of a scroller is BOTH clickable and context-requesting. (1) a STATIONARY
        // ≥500ms press fires the context request (the press held, no scroll, no click); (2) a SUB-500ms release fires the
        // CLICK and no context (the Hold timer never promoted); (3) a MOVE-PAST-SLOP claims the Pan (scroll), firing
        // NEITHER context nor click (the arena sweeps both the Hold and the Tap — WinUI Pressed→Canceled, never Released).
        {
            // (1) Stationary long-press → context.
            using var app1 = new HeadlessPlatformApp();
            var w1 = new HeadlessWindow(new WindowDesc("hold-ctx", new Size2(360, 360), 1f)); w1.Show();
            var p1 = new TouchHoldContextProbe();
            using var h1 = new AppHost(app1, w1, new HeadlessGpuDevice(), fonts, strings, p1);
            h1.RunFrame();
            var pt1 = CenterOf(h1.Scene, p1.Row0);
            uint t1 = s_touchClockMs;
            w1.QueueInput(Touch(InputKind.PointerDown, pt1, t1, 91));
            h1.RunFrame();
            // Hold idle for 600ms (=38 fixed 16ms frames > the 500ms HoldUs) so the timer promotes the Hold → context.
            // The press visual is still HELD through the fire (the finger has not lifted).
            for (int i = 0; i < 7; i++) h1.RunFrame();
            bool pressedDuringHold = (h1.Scene.Flags(p1.Row0) & NodeFlags.Pressed) != 0;
            for (int i = 0; i < 31; i++) h1.RunFrame();
            bool firedHeldStillPressed = p1.Contexts == 1 && (h1.Scene.Flags(p1.Row0) & NodeFlags.Pressed) != 0;
            w1.QueueInput(Touch(InputKind.PointerUp, pt1, t1 + 620, 91));
            h1.RunFrame();
            s_touchClockMs = t1 + 2000;
            bool stationaryFiresContext = p1.Contexts == 1 && p1.Clicks == 0 && pressedDuringHold && firedHeldStillPressed;

            // (2) Sub-500ms release → click, NOT context.
            using var app2 = new HeadlessPlatformApp();
            var w2 = new HeadlessWindow(new WindowDesc("hold-tap", new Size2(360, 360), 1f)); w2.Show();
            var p2 = new TouchHoldContextProbe();
            using var h2 = new AppHost(app2, w2, new HeadlessGpuDevice(), fonts, strings, p2);
            h2.RunFrame();
            var pt2 = CenterOf(h2.Scene, p2.Row0);
            uint t2 = s_touchClockMs;
            w2.QueueInput(Touch(InputKind.PointerDown, pt2, t2, 92)); h2.RunFrame();
            for (int i = 0; i < 12; i++) h2.RunFrame();   // ~192ms held, well under 500ms
            w2.QueueInput(Touch(InputKind.PointerUp, pt2, t2 + 200, 92)); h2.RunFrame();
            s_touchClockMs = t2 + 2000;
            bool quickReleaseClicks = p2.Clicks == 1 && p2.Contexts == 0;

            // (3) Move-past-slop → pan (scroll), no context, no click.
            using var app3 = new HeadlessPlatformApp();
            var w3 = new HeadlessWindow(new WindowDesc("hold-pan", new Size2(360, 360), 1f)); w3.Show();
            var p3 = new TouchHoldContextProbe();
            using var h3 = new AppHost(app3, w3, new HeadlessGpuDevice(), fonts, strings, p3);
            h3.RunFrame();
            var vp3 = h3.Scene.Root;
            var pt3 = CenterOf(h3.Scene, p3.Row0);
            h3.Scene.TryGetScroll(vp3, out var sc3before);
            uint t3 = s_touchClockMs;
            w3.QueueInput(Touch(InputKind.PointerDown, pt3, t3, 93)); h3.RunFrame();
            // Drag the finger UP 60px over several frames (content scrolls DOWN → OffsetY increases from the top clamp).
            // Crossing the 4px pan slop on the scroll axis claims the Pan and SWEEPS the Hold + Tap; then keep holding past
            // 500ms to prove the swept Hold never re-fires the context.
            for (int i = 1; i <= 6; i++)
            {
                w3.QueueInput(Touch(InputKind.PointerMove, new Point2(pt3.X, pt3.Y - i * 10f), t3 + (uint)(i * 16), 93));
                h3.RunFrame();
            }
            for (int i = 0; i < 38; i++) h3.RunFrame();   // hold past 500ms after the pan claim
            w3.QueueInput(Touch(InputKind.PointerUp, new Point2(pt3.X, pt3.Y - 60f), t3 + 700, 93)); h3.RunFrame();
            h3.Scene.TryGetScroll(vp3, out var sc3after);
            s_touchClockMs = t3 + 2000;
            bool movePansNoContext = p3.Contexts == 0 && p3.Clicks == 0 && sc3after.OffsetY > sc3before.OffsetY + 4f;

            using var app4 = new HeadlessPlatformApp();
            var w4 = new HeadlessWindow(new WindowDesc("hold-radial-cancel", new Size2(360, 360), 1f)); w4.Show();
            var p4 = new TouchHoldContextProbe();
            using var h4 = new AppHost(app4, w4, new HeadlessGpuDevice(), fonts, strings, p4);
            h4.RunFrame();
            var pt4 = CenterOf(h4.Scene, p4.Row0);
            uint t4 = s_touchClockMs;
            w4.QueueInput(Touch(InputKind.PointerDown, pt4, t4, 94)); h4.RunFrame();
            var cross = new Point2(pt4.X + InputDispatcher.TouchSlopPx + 4f, pt4.Y);
            w4.QueueInput(Touch(InputKind.PointerMove, cross, t4 + 16, 94)); h4.RunFrame();
            for (int i = 0; i < 38; i++) h4.RunFrame();
            w4.QueueInput(Touch(InputKind.PointerUp, cross, t4 + 700, 94)); h4.RunFrame();
            bool radialMoveCancelsBoth = p4.Contexts == 0 && p4.Clicks == 0;
            s_touchClockMs = t4 + 2000;

            Check("gate.touch4.hold-fires-context a stationary ≥500ms touch press on a clickable+context row opens its context flyout (press held through the fire); a sub-500ms release fires the click not the context; a move-past-slop claims the scroller pan (scrolls) and fires neither context nor click",
                stationaryFiresContext && quickReleaseClicks && movePansNoContext && radialMoveCancelsBoth,
                $"stationary(ctx={p1.Contexts} clk={p1.Clicks} heldPress={firedHeldStillPressed})={stationaryFiresContext} quick(clk={p2.Clicks} ctx={p2.Contexts})={quickReleaseClicks} pan(ctx={p3.Contexts} clk={p3.Clicks} dOff={sc3after.OffsetY - sc3before.OffsetY:0.#})={movePansNoContext} radial={radialMoveCancelsBoth}");
        }

        // gate.touch4.hold-wake: a STATIONARY hold with NO further input still resolves — the GestureHold wake bit keeps
        // frames coming until TickGestureArenas fires the ~500ms Hold (mirrors gate.wake.idle-attribution). After the fire
        // the bit clears (the Hold resolved); lifting + draining returns the idle mask to None. A bare context box (no
        // scroller/click) so the ONLY post-down wake reason attributable to the hold is GestureHold.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("hold-wake", new Size2(300, 240), 1f)); window.Show();
            var probe = new ArenaHoldProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var br = host.Scene.AbsoluteRect(host.Scene.Root);
            var p = new Point2(br.X + 40f, br.Y + 40f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, p, t, 94));
            host.RunFrame();
            // The down armed the Hold: the wake mask must light GestureHold so a stationary finger keeps frames coming.
            bool holdBitLit = (host.CurrentWakeReasons & WakeReasons.GestureHold) != 0;
            // Drive the loop ONLY while it claims active work (NO further input) — exactly the real idle loop. The Hold
            // must promote + fire purely off the wake-driven frames (capped well above the 500ms/16ms ≈ 32 frames).
            int pumped = 0;
            for (; pumped < 120; pumped++)
            {
                if (!host.HasActiveWork) break;
                host.RunFrame();
            }
            bool firedViaWake = probe.Contexts == 1;
            // After resolution the GestureHold bit is cleared even though the finger is STILL down (the Hold won; the arena
            // is resolved). The bare context handler queues no further work, so the loop is now idle.
            bool bitClearedAfter = (host.CurrentWakeReasons & WakeReasons.GestureHold) == 0;
            // Lift + drain: the idle mask returns fully to None (mirror gate.wake.idle-attribution).
            window.QueueInput(Touch(InputKind.PointerUp, p, t + (uint)(pumped + 2) * 16, 94));
            host.RunFrame();
            int drained = 0;
            for (; drained < 120; drained++) { if (!host.HasActiveWork) break; host.RunFrame(); }
            bool idleNone = host.CurrentWakeReasons == WakeReasons.None;
            s_touchClockMs = t + 4000;
            Check("gate.touch4.hold-wake a stationary hold with NO further input still resolves — the GestureHold wake bit keeps frames coming until the ~500ms Hold fires the context, then the bit clears (Hold resolved, finger still down) and after lifting the idle mask returns to None (mirrors gate.wake.idle-attribution)",
                holdBitLit && firedViaWake && bitClearedAfter && idleNone,
                $"holdBitLit={holdBitLit} firedViaWake={firedViaWake}(ctx={probe.Contexts} pumped={pumped}) bitClearedAfter={bitClearedAfter} idleNone={idleNone}(final={host.CurrentWakeReasons} drained={drained})");
        }

        // gate.touch4.rating-tap: a touch TAP on the 4th star of a 5-star RatingControl rates it 4 (OnPointerDown=Sweep
        // sets the preview at the tapped X; OnClick=Commit on release applies it — touch tap-to-rate, the commit-on-release
        // RatingControl.cpp lifecycle, here via the eager OnDrag implicit-capture single-recognizer fast-path).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rating-tap", new Size2(320, 120), 1f)); window.Show();
            var probe = new RatingTapProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            // The interactive star row (the Sweep/Commit OnDrag surface) is the Rating container's first child; its 4th
            // star centre in panel-local px is StarCenter(3) = 16·3.5 + 3·8 = 80 (StarsAt maps that to ceil(80/112·5)=4).
            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var starRow = Child(host.Scene, rating, 0);
            var sr = host.Scene.AbsoluteRect(starRow);
            float star4LocalX = RatingControlTemplateSettings.StarCenter(3, 16f, 8f);   // = 80
            var tapPt = new Point2(sr.X + star4LocalX, sr.Y + sr.H / 2f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, tapPt, t, 95)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, tapPt, t + 16, 95)); host.RunFrame();
            s_touchClockMs = t + 1000;
            float rated = probe.Val?.Value ?? -1f;
            Check("gate.touch4.rating-tap a touch tap on the 4th star of a 5-star RatingControl rates it 4 (OnPointerDown=Sweep preview + OnClick=Commit on release — touch tap-to-rate via the eager OnDrag single-recognizer capture)",
                Near(rated, 4f, 0.01f), $"rated={rated} (expected 4) starRowX={sr.X:0.#} tapX={tapPt.X:0.#}");
        }
    }

    static void TouchPhase2Checks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        const float Adv = 14f * 0.55f;   // headless advance model @ FontSize 14 (the EditableTextCoreChecks model)

        static NodeHandle TextVisual(SceneStore s, NodeHandle n)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (s.Paint(n).VisualKind == VisualKind.Text) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                var r = TextVisual(s, c);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }

        // gate.touch2.pressed-no-hover: a touch TAP on a Button drives the Pressed visual (NodeFlags.Pressed + the
        // InteractionAnimator's PressT — the OnPressChanged → SetPress arm, observed as a live animator census) on the
        // DOWN edge, then releases to Normal on UP (Pressed clears, hover NEVER set — touch has no cursor, so a released
        // tap settles to Normal, not PointerOver). A contact moving across the button ROW by sequential taps fires the
        // Pressed visual on each button it taps yet delivers ZERO hover callbacks the whole way (a tap never sets hover;
        // any transient move-hover would be cleared on up regardless — here there is no intra-tap move, so none is set).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch2-press", new Size2(320, 80), 1f)); window.Show();
            var probe = new TouchButtonRowProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            // Locate the three button nodes by role (left→right).
            var btns = new List<NodeHandle>();
            CollectRole(scene, scene.Root, AutomationRole.Button, btns);

            // TAP button 0: down → Pressed + PressT armed + NO hover; up → Normal (no Pressed, no resting hover).
            var b0 = CenterOf(scene, btns[0]);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, b0, t, 21));
            host.RunFrame();
            bool pressedOnDown = (scene.Flags(btns[0]) & NodeFlags.Pressed) != 0;
            bool pressArmed = host.HasActiveWork;                            // PressT easing armed (OnPressChanged → SetPress)
            bool noHoverOnDown = AnyHovered(scene, scene.Root).IsNull;       // a touch press never sets hover
            int pressedB0 = probe.Pressed[0];                               // OnPointerPressed delivered on the down edge
            window.QueueInput(Touch(InputKind.PointerUp, b0, t + 16, 21));
            host.RunFrame();
            bool releasedToNormal = (scene.Flags(btns[0]) & NodeFlags.Pressed) == 0 && AnyHovered(scene, scene.Root).IsNull;
            int clickedB0 = probe.Clicked[0];
            s_touchClockMs = t + 1000;

            // MOVE across the row by sequential taps on buttons 1 then 2 (the contact point travels right): each taps its
            // own button (Pressed visual + click), and across the WHOLE sequence not one hover callback ever fired.
            for (int i = 1; i < TouchButtonRowProbe.N; i++)
            {
                var c = CenterOf(scene, btns[i]);
                uint ti = s_touchClockMs;
                window.QueueInput(Touch(InputKind.PointerDown, c, ti, 21));
                host.RunFrame();
                bool pr = (scene.Flags(btns[i]) & NodeFlags.Pressed) != 0;
                window.QueueInput(Touch(InputKind.PointerUp, c, ti + 16, 21));
                host.RunFrame();
                if (!pr) probe.Pressed[i] = -1000;   // poison the assert if a tapped button never showed Pressed
                s_touchClockMs = ti + 1000;
            }
            bool everyTapPressed = probe.Pressed[0] == 1 && probe.Pressed[1] == 1 && probe.Pressed[2] == 1
                                   && probe.Clicked[0] == 1 && probe.Clicked[1] == 1 && probe.Clicked[2] == 1;
            bool noHoverEver = probe.HoverCallbacks == 0;                    // touch never delivered hover to any button
            bool noRestingHover = AnyHovered(scene, scene.Root).IsNull;     // and nothing rests hovered at the end

            Check("gate.touch2.pressed-no-hover a touch tap drives the Pressed visual (NodeFlags.Pressed + PressT armed) and releases to Normal on up; taps moving across a button row deliver zero hover callbacks and leave no resting hover (touch has no cursor)",
                pressedOnDown && pressArmed && noHoverOnDown && pressedB0 == 1 && clickedB0 == 1
                && releasedToNormal && everyTapPressed && noHoverEver && noRestingHover,
                $"pressedDown={pressedOnDown} pressArmed={pressArmed} hoverDown={!noHoverOnDown} normalAfterUp={releasedToNormal} pressed=[{probe.Pressed[0]},{probe.Pressed[1]},{probe.Pressed[2]}] clicked=[{probe.Clicked[0]},{probe.Clicked[1]},{probe.Clicked[2]}] hoverCallbacks={probe.HoverCallbacks} restingHover={!noRestingHover}");
        }

        // gate.touch2.light-dismiss: a TOUCH press outside an open light-dismiss flyout closes it AND is consumed by the
        // scrim (the behind-content never clicks through), and that observable outcome MATCHES the MOUSE path on the SAME
        // scene — the requirement is parity, so the scenario runs once by mouse and once by touch and the two outcomes are
        // compared (both dismiss == true, both no-click-through == true). The press lands on the full-bleed scrim (topmost
        // while a popup is open), whose OnClick = CloseTop(LightDismiss); the scrim eats the press so the behind surface
        // never sees it (WinUI CPopupRoot::OnPointerPressed Handled=didCloseAPopup, popup.cpp:5206).
        {
            (bool dismissed, int behindClicks) RunDismiss(bool touch)
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc(touch ? "touch2-dismiss-t" : "touch2-dismiss-m", new Size2(480, 360), 1f)); window.Show();
                var probe = new TouchDismissProbe();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
                host.RunFrame();
                var svc = probe.Service!;
                Func<Element> menu = () => new BoxEl
                {
                    Width = 140, Direction = 1,
                    Children = [new BoxEl { Height = 32, Role = AutomationRole.MenuItem, OnClick = () => svc.CloseTop(), Children = [Ui.Text("Item A")] }],
                };
                bool MenuOpen() => !FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;
                void Settle() { for (int i = 0; i < 16; i++) host.RunFrame(); }
                var outside = new Point2(460, 350);   // over the behind-content, far from the BottomLeft popup over (20,20)

                svc.Open(() => probe.Anchor, menu, FlyoutPlacement.BottomLeft);
                host.RunFrame();
                if (touch)
                {
                    uint t = s_touchClockMs;
                    window.QueueInput(Touch(InputKind.PointerDown, outside, t, 31));
                    window.QueueInput(Touch(InputKind.PointerUp, outside, t + 16, 31));
                    host.RunFrame();
                    s_touchClockMs = t + 1000;
                }
                else
                {
                    window.QueueInput(new InputEvent(InputKind.PointerDown, outside, 0, 0));
                    window.QueueInput(new InputEvent(InputKind.PointerUp, outside, 0, 0));
                    host.RunFrame();
                }
                Settle();
                return (!MenuOpen(), probe.BehindClicks);
            }

            var (mDismiss, mBehind) = RunDismiss(touch: false);
            var (tDismiss, tBehind) = RunDismiss(touch: true);
            bool mouseDismissedConsumed = mDismiss && mBehind == 0;
            bool touchDismissedConsumed = tDismiss && tBehind == 0;
            bool parity = mouseDismissedConsumed == touchDismissedConsumed && touchDismissedConsumed;

            Check("gate.touch2.light-dismiss a touch press outside an open flyout dismisses it and is consumed by the scrim (no click-through), MATCHING the mouse path's observable outcome on the same scene",
                parity, $"mouse(dismiss={mDismiss},behind={mBehind}) touch(dismiss={tDismiss},behind={tBehind}) parity={parity}");
        }

        // gate.touch2.caret-and-select: a touch TAP in an EditableText places the caret at the tapped offset (the OnDrag
        // node's OnPointerPressed → HandlePressed, ClickCount=1 → SetCaret at the hit), and a touch DRAG from inside the
        // text EXTENDS the selection (the OnDrag implicit-capture: TouchDown set _dragTarget=field, each move drives
        // HandleDrag → SetCaret(extend) — the selection LENGTH grows monotonically with the drag), while the editor
        // sitting INSIDE a scrollable does NOT pan (the OnDrag capture owns the contact; no pan candidate is armed).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch2-caret", new Size2(360, 320), 1f)); window.Show();
            var root = new TouchEditInScrollerProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            var ta = scene.AbsoluteRect(tn);
            var scroller = FindScrollable(scene, scene.Root);
            float wy = ta.Y + ta.H / 2f;

            // (1) TAP at offset 4 ("hell|o world") → caret lands at 4 (same HitTestText the mouse uses).
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(ta.X + 4f * Adv + 1f, wy), t, 12));
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(ta.X + 4f * Adv + 1f, wy), t + 16, 12));
            host.RunFrame();
            s_touchClockMs = t + 1000;
            int caretAfterTap = root.Edit!.Core.Active;   // captured at tap-time (the later drag moves the caret)
            bool caretAtTap = caretAfterTap == 4 && root.Edit!.SelectionLength == 0;

            // (2) DRAG from offset 1 rightward, reading the selection length after EACH step so it can be asserted to GROW
            // (the drag, not a tap, is extending the editor's selection). A big downward component on the drag is on the
            // scroll axis — proving the editor's OnDrag wins over a content pan even with vertical travel.
            scene.TryGetScroll(scroller, out var beforeDrag);
            float panBefore = beforeDrag.OffsetY;
            uint t2 = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(ta.X + 1f * Adv + 1f, wy), t2, 13));
            host.RunFrame();
            int prevLen = root.Edit!.SelectionLength;
            int growthSteps = 0, shrinkSteps = 0;
            for (int i = 1; i <= 6; i++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(ta.X + (1f + i) * Adv + 1f, wy + i * 12f), t2 + (uint)i * 16, 13));
                host.RunFrame();
                int len = root.Edit!.SelectionLength;
                if (len > prevLen) growthSteps++;
                else if (len < prevLen) shrinkSteps++;
                prevLen = len;
            }
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(ta.X + 7f * Adv + 1f, wy + 72f), t2 + 7 * 16, 13));
            host.RunFrame();
            s_touchClockMs = t2 + 1000;
            scene.TryGetScroll(scroller, out var afterDrag);
            bool selectionGrew = root.Edit!.SelectionStart == 1 && root.Edit!.SelectionLength == 6 && growthSteps >= 5 && shrinkSteps == 0;
            bool noPanWhileSelecting = Near(afterDrag.OffsetY, panBefore);   // the scroller never moved while drag-selecting

            Check("gate.touch2.caret-and-select a touch tap places the caret at the tapped offset; a touch drag extends the selection (length grows monotonically with the drag) and the editor inside a scrollable does not pan while drag-selecting",
                caretAtTap && selectionGrew && noPanWhileSelecting,
                $"caretAtTap={caretAtTap} (caretAfterTap={caretAfterTap}) sel(start={root.Edit!.SelectionStart},len={root.Edit!.SelectionLength}) grow={growthSteps} shrink={shrinkSteps} pan={afterDrag.OffsetY:0.#}->was {panBefore:0.#}");
        }

        // gate.touch2.thumb-drag: a touch press+drag on the conscious overlay-scrollbar THUMB scrolls PROPORTIONALLY — the
        // offset follows the track fraction (so the thumb sweeping its full travel drives the offset to the content-end
        // clamp), driven by the per-PointerId _scrollDragNode — and the conscious bar REVEALS for the contact's duration
        // (PointerOverScrollbar + FadeT>0 while held, dropped on release so it fades). A plain (non-virtual) 800px-over-200px
        // scroller isolates the drag machinery (no realize-edge churn).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch2-thumb", new Size2(260, 260), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ScrollProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            float maxOff = MathF.Max(0f, sc0.ContentH - sc0.ViewportH);   // 800 − 200 = 600
            float laneX = 194f;   // the 200-wide viewport's scrollbar lane (x∈[188,200]); the thumb rests at the top

            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(laneX, 12f), t, 14));   // grab the thumb at the top
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var grabbed);
            bool revealedDuringContact = grabbed.PointerOverScrollbar && grabbed.FadeT > 0f;   // conscious bar revealed+expanded

            float lastOff = grabbed.OffsetY;
            int advanceSteps = 0, regressions = 0;
            const int dragSteps = 60;
            for (int s = 1; s <= dragSteps; s++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(laneX, 12f + s * 4f), t + (uint)s, 14));   // sweep the lane
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var dsc);
                if (dsc.OffsetY > lastOff + 0.5f) advanceSteps++;
                else if (dsc.OffsetY < lastOff - 0.5f) regressions++;
                lastOff = dsc.OffsetY;
            }
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(laneX, 12f + dragSteps * 4f), t + dragSteps + 1, 14));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var afterUp);
            s_touchClockMs = t + 1000;
            // Track-fraction proportional drive: ~240px of finger travel sweeps the thumb its whole track and the offset to
            // the content-end clamp (a 4px-axis CONTENT pan would have moved the offset ~240px and never reached max=600).
            bool proportionalToEnd = Near(afterUp.OffsetY, maxOff, 1f) && advanceSteps >= 25 && regressions == 0;
            bool fadesAfterRelease = !afterUp.PointerOverScrollbar;   // contact-duration reveal ended ⇒ the bar can fade

            Check("gate.touch2.thumb-drag a touch press+drag on the overlay scrollbar thumb scrolls proportionally (offset follows the track fraction to the content end, not a content pan) and the conscious bar reveals during the contact",
                revealedDuringContact && proportionalToEnd && fadesAfterRelease,
                $"revealed={revealedDuringContact} advanced={advanceSteps}/{dragSteps} regress={regressions} off={afterUp.OffsetY:0}/max={maxOff:0} fadesAfterRelease={fadesAfterRelease}");
        }

        // gate.touch2.alloc-zero: HotPhaseAllocBytes==0 across the flyout-open/dismiss + caret-drag sequences, measured the
        // SAME way gate.touch.fling-alloc-steady-zero does — the HOT half, not the reconcile edge. A flyout open/dismiss
        // round-trip is run end-to-end for coverage, but the per-frame HOT path measured for 0-alloc is the steady frame
        // while the flyout is OPEN (overlay live, no churn). The OPEN build, the CloseTop component re-render, and the
        // subtree TEARDOWN are Rendered=true RECONCILE-edge frames — the corpus's documented bounded-Gen0 render/reconcile
        // edge (the same class gate.touch.fling-realize-edge-is-wheel attributes to the shared reconcile edge, not touch),
        // NOT a frame-phase-6–13 hot path — so they are excluded exactly like the cold press-arg edge (PointerEventArgs per
        // Events.cs) is. The caret half measures a steady touch drag-select MOVE frame the same way.
        {
            // (A) flyout open → steady-open hot frame → touch-dismiss (the round-trip runs; the OPEN steady frame is measured).
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("touch2-alloc-dismiss", new Size2(480, 360), 1f)); window.Show();
                var probe = new TouchDismissProbe();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
                host.RunFrame();
                var svc = probe.Service!;
                Func<Element> menu = () => new BoxEl
                {
                    Width = 140, Direction = 1,
                    Children = [new BoxEl { Height = 32, Role = AutomationRole.MenuItem, OnClick = () => svc.CloseTop(), Children = [Ui.Text("Item A")] }],
                };
                var outside = new Point2(460, 350);

                // Warm the full open/dismiss path once (JIT the overlay open + scrim tap + close driver + teardown), drain.
                svc.Open(() => probe.Anchor, menu, FlyoutPlacement.BottomLeft); host.RunFrame();
                { uint tw = s_touchClockMs; window.QueueInput(Touch(InputKind.PointerDown, outside, tw, 41)); window.QueueInput(Touch(InputKind.PointerUp, outside, tw + 16, 41)); host.RunFrame(); s_touchClockMs = tw + 1000; }
                for (int i = 0; i < 16; i++) host.RunFrame();

                // Re-open; let the open animation settle, then MEASURE the steady-open hot frames (overlay live, no churn).
                svc.Open(() => probe.Anchor, menu, FlyoutPlacement.BottomLeft);
                host.RunFrame();   // open frame: builds the popup subtree (cold reconcile edge) — not measured
                for (int i = 0; i < 8; i++) host.RunFrame();   // let the open clip/opacity animation settle
                long worstOpenSteady = 0;
                for (int i = 0; i < 8; i++) { var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worstOpenSteady) worstOpenSteady = f.HotPhaseAllocBytes; }

                // Exercise the dismiss (touch tap on the scrim) to run the path end-to-end; the CloseTop re-render + teardown
                // are reconcile-edge frames (not measured for 0-alloc, like every overlay open/close in the suite). Assert it
                // actually dismissed, so the round-trip is genuinely exercised (not a vacuous skip).
                uint t = s_touchClockMs;
                window.QueueInput(Touch(InputKind.PointerDown, outside, t, 42));
                window.QueueInput(Touch(InputKind.PointerUp, outside, t + 16, 42));
                host.RunFrame();
                for (int i = 0; i < 16; i++) host.RunFrame();   // settle the close fade + teardown
                bool dismissed = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;
                s_touchClockMs = t + 1000;

                // (B) caret drag-select hot half: warm the slab, then measure a steady drag-select MOVE frame (per-id slot
                // rebind + OnDrag drive + pooled rect slab — byte-identical to the mouse _dragTarget branch, proved clean).
                var window2 = new HeadlessWindow(new WindowDesc("touch2-alloc-caret", new Size2(360, 320), 1f)); window2.Show();
                var root = new TouchEditInScrollerProbe();
                using var host2 = new AppHost(app, window2, new HeadlessGpuDevice(), fonts, strings, root);
                host2.RunFrame();
                var scene2 = host2.Scene;
                var field2 = FindRole(scene2, scene2.Root, AutomationRole.Text);
                var tn2 = TextVisual(scene2, field2);
                var ta2 = scene2.AbsoluteRect(tn2);
                float wy2 = ta2.Y + ta2.H / 2f;

                uint t3 = s_touchClockMs;
                window2.QueueInput(Touch(InputKind.PointerDown, new Point2(ta2.X + 1f * Adv + 1f, wy2), t3, 43));
                host2.RunFrame();   // down: cold press args + caret place — not measured
                for (int i = 1; i <= 4; i++)   // warm the drag-select (pooled rect slab) over a few moves
                {
                    window2.QueueInput(Touch(InputKind.PointerMove, new Point2(ta2.X + (i % 2 == 0 ? 6f : 7f) * Adv + 1f, wy2 + 8f), t3 + (uint)i * 16, 43));
                    host2.RunFrame();
                }
                // Measure several steady drag-select MOVE frames (the caret moves back and forth inside the text, so the
                // selection re-extends each frame: per-id slot rebind + OnDrag drive + pooled rect slab, all 0-alloc). The
                // length VARIES across the back-and-forth moves — proof the measured frames were live drag-select, not no-ops.
                long worstCaret = 0;
                int minLen = int.MaxValue, maxLen = int.MinValue;
                for (int i = 0; i < 6; i++)
                {
                    float gx = ta2.X + (i % 2 == 0 ? 6f : 8f) * Adv + 1f;
                    window2.QueueInput(Touch(InputKind.PointerMove, new Point2(gx, wy2 + 8f), t3 + (uint)(6 + i) * 16, 43));
                    var f = host2.RunFrame();
                    if (f.HotPhaseAllocBytes > worstCaret) worstCaret = f.HotPhaseAllocBytes;
                    int len = root.Edit!.SelectionLength;
                    if (len < minLen) minLen = len;
                    if (len > maxLen) maxLen = len;
                }
                window2.QueueInput(Touch(InputKind.PointerUp, new Point2(ta2.X + 8f * Adv + 1f, wy2 + 8f), t3 + 13 * 16, 43));
                host2.RunFrame();
                bool caretDragLive = maxLen > minLen;   // the selection re-extended across the moves ⇒ live drag-select
                s_touchClockMs = t3 + 1000;

                Check("gate.touch2.alloc-zero the steady flyout-open hot frame AND steady touch caret drag-select frames allocate 0 managed bytes on phases 6–13 (the open-build/CloseTop-render/teardown reconcile edges + the cold press-args excluded, like the existing touch alloc gates)",
                    worstOpenSteady == 0 && worstCaret == 0 && dismissed && caretDragLive,
                    $"worstOpenSteadyHotAlloc={worstOpenSteady}B worstCaretDragHotAlloc={worstCaret}B dismissed={dismissed} caretDragLive={caretDragLive}");
            }
        }

        // gate.touch2.bar-reveals-during-pan: while touch never latches hover, the thin scrollbar INDICATOR must still reveal
        // THROUGHOUT a finger-down content pan (the WinUI TouchIndicator shows for the whole manipulation, not just the
        // post-lift fling). A content pan drives the viewport through SetScrollOffset (Offset == Target every move), so the
        // ScrollIntegrator can't infer motion from |Target − Offset|; the synchronous-move pulse reveals FadeT for each pan frame.
        // Contract: FadeT climbs > 0 DURING the drag moves (not only after lift) WITHOUT setting PointerOver (touch sets no
        // hover) and WITHOUT the 400ms full-gutter expand (ExpandT stays ~0) — exactly the lane-page-step flash. A plain
        // (non-virtual) 800px-over-200px ScrollProbe isolates the pan machinery from any realize-edge churn.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("touch2-pan-reveal", new Size2(260, 260), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ScrollProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            var vpRect = host.Scene.AbsoluteRect(vp);

            // A vertical content pan starting inside the content area (x at the viewport's content column, well clear of the
            // lane on the right edge; y at ~80% height so 12 upward 10px steps stay inside) up by 120px across 12 frames (one
            // move per RunFrame so the ring keeps each). Sample FadeT/PointerOver/ExpandT mid-drag.
            float panX = vpRect.X + 60f;
            float startY = vpRect.Y + vpRect.H * 0.8f;
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(panX, startY), t, 71));
            host.RunFrame();   // down: records the anchor, no claim yet, no reveal
            host.Scene.TryGetScroll(vp, out var atDown);
            float fadeAtDown = atDown.FadeT;   // baseline: the press alone reveals nothing (below-slop)

            float maxFadeDuringPan = 0f, maxExpandDuringPan = 0f, maxOffDuringPan = 0f;
            bool pointerOverEverSet = false;
            const int panSteps = 12;
            for (int s = 1; s <= panSteps; s++)
            {
                var p = new Point2(panX, startY - s * 10f);   // 10px/step ⇒ crosses the 4px slop on step 1, then keeps panning
                window.QueueInput(Touch(InputKind.PointerMove, p, t + (uint)(s * 16), 71));
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var dsc);
                if (dsc.FadeT > maxFadeDuringPan) maxFadeDuringPan = dsc.FadeT;
                if (dsc.ExpandT > maxExpandDuringPan) maxExpandDuringPan = dsc.ExpandT;
                if (dsc.OffsetY > maxOffDuringPan) maxOffDuringPan = dsc.OffsetY;
                if (dsc.PointerOver) pointerOverEverSet = true;
            }
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(panX, startY - panSteps * 10f), t + (uint)((panSteps + 1) * 16), 71));
            host.RunFrame();
            s_touchClockMs = t + 2000;

            bool panned = maxOffDuringPan > 50f;                       // the finger drag actually scrolled the content
            bool revealedDuringPan = maxFadeDuringPan > 0.5f;          // the thin indicator was VISIBLE mid-drag (the bug: stayed 0)
            bool noHoverLatched = !pointerOverEverSet;                 // touch never set PointerOver (no hover to clear on lift)
            bool noFullExpand = maxExpandDuringPan < 0.1f;             // a content pan never triggers the 400ms full-gutter expand
            Check("gate.touch2.bar-reveals-during-pan a finger-down content pan reveals the thin scrollbar indicator throughout the drag (FadeT>0 mid-pan, not only post-lift) without latching PointerOver or expanding the gutter",
                panned && revealedDuringPan && noHoverLatched && noFullExpand,
                $"maxFadeT_DURING_pan={maxFadeDuringPan:0.000} (down baseline {fadeAtDown:0.000}) maxExpandT={maxExpandDuringPan:0.000} pointerOver={pointerOverEverSet} pannedTo={maxOffDuringPan:0}");
        }
    }

    static void ArenaCoreChecks(StringTable strings)
    {
        // gate.arena.eager-win-sweeps-midstream: the moment a Drag member crosses slop (EagerAccept) it wins immediately
        // and sweeps a competing Tap mid-stream (no PointerUp wait) — §7A.2 rule 1. The Tap loser is reset (Reject + the
        // synthetic GestureRejected fired exactly once).
        {
            var arena = new GestureArena();
            int rejected = 0, won = 0;
            arena.OnMemberRejected = _ => rejected++;
            arena.OnMemberWon = _ => won++;
            int slot = arena.OpenArena(pointerId: 1);
            int tap = arena.Enroll(slot, NodeAt(1), GestureKind.Tap);     // innermost first (priority 0)
            int drag = arena.Enroll(slot, NodeAt(2), GestureKind.Drag);   // priority 1
            arena.CloseArena(slot);
            // Pre-cross: both Pending, nothing resolves.
            int r0 = arena.ResolveStep(slot);
            // Drag crosses slop → EagerAccept.
            arena.SetVote(drag, ArenaVote.EagerAccept);
            int r1 = arena.ResolveStep(slot);
            bool eagerOk = r0 < 0 && r1 == drag && arena.VoteOf(drag) == ArenaVote.EagerAccept
                          && arena.VoteOf(tap) == ArenaVote.Reject && rejected == 1 && won == 1;
            Check("gate.arena.eager-win-sweeps-midstream a Drag crossing slop (EagerAccept) wins immediately and sweeps the competing Tap mid-stream (the loser is Reject + one synthetic GestureRejected), with no PointerUp wait",
                eagerOk, $"r0={r0} r1={r1}(drag={drag}) tapVote={arena.VoteOf(tap)} rejected={rejected} won={won}");
        }

        // gate.arena.first-accept-waits-for-closed: a member that votes Accept does NOT win while the arena is still OPEN
        // (a recognizer ahead could still claim it); it wins only once Closed AND no lower-priority member is Pending —
        // §7A.2 rule 2. Here the innermost (priority 0) stays Pending, so even after Close the Accept (priority 1) waits;
        // once the innermost rejects, the Accept resolves.
        {
            var arena = new GestureArena();
            int slot = arena.OpenArena(pointerId: 2);
            int inner = arena.Enroll(slot, NodeAt(1), GestureKind.Pan);    // priority 0, stays Pending
            int outer = arena.Enroll(slot, NodeAt(2), GestureKind.Tap);    // priority 1, votes Accept
            arena.SetVote(outer, ArenaVote.Accept);
            int rOpen = arena.ResolveStep(slot);                            // OPEN → Accept must NOT win yet
            arena.CloseArena(slot);
            int rClosedPending = arena.ResolveStep(slot);                   // Closed but inner still Pending AHEAD → wait
            arena.SetVote(inner, ArenaVote.Reject);
            int rClosedClear = arena.ResolveStep(slot);                     // inner gone → Accept wins
            bool firstAcceptOk = rOpen < 0 && rClosedPending < 0 && rClosedClear == outer;
            Check("gate.arena.first-accept-waits-for-closed an Accept vote does not win while the arena is open, nor while a higher-priority member is still Pending ahead of it; it resolves only once Closed and the path ahead is clear (§7A.2 rule 2)",
                firstAcceptOk, $"open={rOpen} closedPending={rClosedPending} closedClear={rClosedClear}(outer={outer})");
        }

        // gate.arena.last-standing-while-pending: a lone surviving member wins by default even while still Pending (a Tap
        // that never moved), as soon as all others reject — §7A.2 rule 3 — provided the arena is NOT Held. This is the
        // single-recognizer common case: one member resolves synchronously the moment it is the last alive.
        {
            var arena = new GestureArena();
            int won = 0; arena.OnMemberWon = _ => won++;
            int slot = arena.OpenArena(pointerId: 3);
            int tap = arena.Enroll(slot, NodeAt(1), GestureKind.Tap);     // priority 0
            int pan = arena.Enroll(slot, NodeAt(2), GestureKind.Pan);    // priority 1
            int rTwo = arena.ResolveStep(slot);                           // two alive, both Pending → no winner
            arena.SetVote(pan, ArenaVote.Reject);                         // pan bows out (never overflowed/never moved)
            int rOne = arena.ResolveStep(slot);                          // Tap is last-standing → wins while Pending
            bool lastStandingOk = rTwo < 0 && rOne == tap && won == 1 && arena.VoteOf(tap) == ArenaVote.Accept;
            // Single-recognizer fast-path: one member alone is last-standing IMMEDIATELY (synchronous, §7A.5).
            var solo = new GestureArena();
            int s2 = solo.OpenArena(pointerId: 33);
            int only = solo.Enroll(s2, NodeAt(9), GestureKind.Tap);
            int rSolo = solo.ResolveStep(s2);
            bool soloSync = rSolo == only;
            Check("gate.arena.last-standing-while-pending the lone survivor wins by default while still Pending once all others reject (§7A.2 rule 3); a SINGLE enrolled recognizer is last-standing immediately (the synchronous single-recognizer common case, §7A.5)",
                lastStandingOk && soloSync, $"two={rTwo} one={rOne}(tap={tap}) won={won} solo={rSolo}(only={only})");
        }

        // gate.arena.up-sweep-clean-tap: a clean tap (no slop crossed, single contact, no eager) stays unresolved through
        // the move stream and resolves on the PointerUp sweep — the highest-priority Accept/Pending member wins, the rest
        // reject — §7A.2 rule 4. Two non-rejecting members: the up-sweep picks the innermost (priority 0).
        {
            var arena = new GestureArena();
            int slot = arena.OpenArena(pointerId: 4);
            int inner = arena.Enroll(slot, NodeAt(1), GestureKind.Tap);    // priority 0 → up-sweep favors it
            int outer = arena.Enroll(slot, NodeAt(2), GestureKind.Tap);    // priority 1
            arena.CloseArena(slot);
            arena.SetVote(inner, ArenaVote.Accept);                         // both report a clean tap on up
            arena.SetVote(outer, ArenaVote.Accept);
            int up = arena.ResolveUp(slot);
            bool upSweepOk = up == inner && arena.VoteOf(outer) == ArenaVote.Reject;
            Check("gate.arena.up-sweep-clean-tap an unresolved clean tap resolves on the PointerUp sweep to the highest-priority (innermost) Accept/Pending member; the rest reject (§7A.2 rule 4)",
                upSweepOk, $"up={up}(inner={inner}) outerVote={arena.VoteOf(outer)}");
        }

        // gate.arena.doubletap-hold-retroactive: a DoubleTap member sets Held after the first up, keeping the arena OPEN
        // across the inter-tap window — last-standing is SUPPRESSED while Held and the up-sweep does NOT resolve it. On
        // the inter-tap TIMEOUT the hold releases and the single-Tap member wins RETROACTIVELY — §7A.2 rule 5.
        {
            var arena = new GestureArena();
            int slot = arena.OpenArena(pointerId: 5);
            int tap = arena.Enroll(slot, NodeAt(1), GestureKind.Tap);          // the retroactive single-tap winner
            int dbl = arena.Enroll(slot, NodeAt(1), GestureKind.DoubleTap);   // requests the hold
            arena.CloseArena(slot);
            // First up: the DoubleTap is waiting for a 2nd tap (its FSM stays Pending — §7B OnUp's await-2nd-tap branch)
            // and requests Held; the single Tap is ALSO still Pending (it has not decided — it wins only retroactively if
            // no double forms). Both Pending + Held ⇒ nothing resolves (last-standing AND the up-sweep are suppressed).
            arena.SetHeld(slot, true);
            int held = arena.ResolveStep(slot);            // Held → last-standing suppressed, both Pending → stays open
            int heldUp = arena.ResolveUp(slot);            // up-sweep on a Held arena resolves NOTHING (waits for the 2nd tap)
            // Inter-tap timeout: no qualifying second tap arrived → the hold releases, single-tap wins retroactively
            // (the up-sweep that now runs picks the highest-priority non-rejected member — the Tap at priority 0).
            int released = arena.ResolveHoldRelease(slot);
            bool holdOk = held < 0 && heldUp < 0 && released == tap && !arena.ArenaAt(slot).Held;
            Check("gate.arena.doubletap-hold-retroactive a DoubleTap Held keeps the arena open across the inter-tap window (last-standing AND the up-sweep both suppressed); on the inter-tap timeout the hold releases and the single Tap wins retroactively (§7A.2 rule 5)",
                holdOk, $"held={held} heldUp={heldUp} released={released}(tap={tap}) stillHeld={arena.ArenaAt(slot).Held}");
        }

        // gate.arena.loser-reset-emits-nothing: a swept loser receives the synthetic GestureRejected — its vote becomes
        // Reject and its FSM is reset to Idle (it emits NOTHING) — §7A.5. Driven over a real PointerFsm bank: a Tap loser
        // swept by a Drag eager-win resets to Idle/Pending and would fire no Tapped.
        {
            var arena = new GestureArena();
            var fsms = new PointerFsm[GestureArena.MaxArenas * GestureArena.MaxMembersPerArena];
            int rejectFires = 0;
            // The reject/win sinks drive the FSM bank exactly as the dispatcher will (reset the loser; let the winner emit).
            arena.OnMemberRejected = slot => { fsms[slot].Reset(); rejectFires++; };
            int aSlot = arena.OpenArena(pointerId: 6);
            int tapM = arena.Enroll(aSlot, NodeAt(1), GestureKind.Tap);
            int dragM = arena.Enroll(aSlot, NodeAt(2), GestureKind.Drag);
            fsms[tapM].Init(GestureKind.Tap); fsms[dragM].Init(GestureKind.Drag);
            arena.CloseArena(aSlot);
            // The Tap saw the down and accumulated a tap (TapCount=1) but is STILL Pending (no move yet); the Drag, on a
            // separate finger path, crosses slop and EagerAccepts. The Tap is swept by the SYNTHETIC GestureRejected (not a
            // self-reject) — the arena's Sweep fires OnMemberRejected, which resets the Tap FSM to Idle/Pending and wipes
            // its tap accumulation so it emits no deferred Tapped (§7A.5).
            long t = 1_000_000;
            fsms[tapM].OnDown(new Point2(100, 100), t);     // Tap is Pending with TapCount=1 (a candidate tap)
            fsms[dragM].OnDown(new Point2(100, 100), t);
            t += 16_000;
            var dragVote = fsms[dragM].OnMove(new Point2(140, 100), t);   // +40px → Drag EagerAccept (the Tap never moved)
            arena.SetVote(tapM, fsms[tapM].Vote);            // still Pending
            arena.SetVote(dragM, dragVote);
            bool tapWasArmed = arena.VoteOf(tapM) == ArenaVote.Pending && fsms[tapM].TapCount == 1;   // armed BEFORE the sweep
            int winner = arena.ResolveStep(aSlot);
            bool loserResetOk = tapWasArmed && winner == dragM && rejectFires == 1
                               && fsms[tapM].Phase == GesturePhase.Idle && fsms[tapM].Vote == ArenaVote.Pending
                               && fsms[tapM].TapCount == 0;     // synthetic reset wiped the tap accumulation → no deferred Tapped
            Check("gate.arena.loser-reset-emits-nothing a Pending Tap loser is reset to Idle/Pending by the SYNTHETIC GestureRejected (the arena's sweep fires the reject sink exactly once; the tap accumulation is wiped → emits nothing) while the eager Drag wins (§7A.5)",
                loserResetOk, $"armedPre={tapWasArmed} winner={winner}(drag={dragM}) rejectFires={rejectFires} tapPhase={fsms[tapM].Phase} tapVote={fsms[tapM].Vote} tapCount={fsms[tapM].TapCount}");
        }

        // gate.arena.innermost-priority-tiebreak: two members resolve eligibly in the SAME step — the INNERMOST (earlier
        // enrollment, lower Priority) wins; the outer rejects. §7A.1 (innermost-first) + §7A.2 (ties broken by Priority).
        // Checked on both the eager path (two EagerAccept) and the first-accept path (two Accept, Closed).
        {
            var arena = new GestureArena();
            int slot = arena.OpenArena(pointerId: 7);
            int inner = arena.Enroll(slot, NodeAt(1), GestureKind.Drag);   // priority 0 (innermost)
            int outer = arena.Enroll(slot, NodeAt(2), GestureKind.Drag);   // priority 1
            arena.SetVote(inner, ArenaVote.EagerAccept);
            arena.SetVote(outer, ArenaVote.EagerAccept);                   // both eager in one frame
            int eagerWin = arena.ResolveStep(slot);

            var arena2 = new GestureArena();
            int slot2 = arena2.OpenArena(pointerId: 8);
            int inner2 = arena2.Enroll(slot2, NodeAt(1), GestureKind.Tap);
            int outer2 = arena2.Enroll(slot2, NodeAt(2), GestureKind.Tap);
            arena2.CloseArena(slot2);
            arena2.SetVote(inner2, ArenaVote.Accept);
            arena2.SetVote(outer2, ArenaVote.Accept);
            int acceptWin = arena2.ResolveStep(slot2);

            bool tiebreakOk = eagerWin == inner && arena.VoteOf(outer) == ArenaVote.Reject
                             && acceptWin == inner2 && arena2.VoteOf(outer2) == ArenaVote.Reject;
            Check("gate.arena.innermost-priority-tiebreak when two members resolve eligibly in one step the innermost (lower Priority, earlier enrollment) wins and the outer rejects — on both the eager-win and the first-accept paths (§7A.1 + §7A.2)",
                tiebreakOk, $"eagerWin={eagerWin}(inner={inner}) acceptWin={acceptWin}(inner2={inner2})");
        }

        // gate.arena.force-close-provisional: PointerCaptureLost (§7A.5) force-closes the arena — the highest-priority
        // non-rejected member wins by default, the rest reject — even mid-stream with everyone Pending.
        {
            var arena = new GestureArena();
            int slot = arena.OpenArena(pointerId: 9);
            int a = arena.Enroll(slot, NodeAt(1), GestureKind.Pan);     // priority 0
            int b = arena.Enroll(slot, NodeAt(2), GestureKind.Tap);    // priority 1
            int forced = arena.ForceClose(slot);                       // capture lost → provisional winner = innermost
            bool forceOk = forced == a && arena.VoteOf(b) == ArenaVote.Reject;
            Check("gate.arena.force-close-provisional PointerCaptureLost force-closes the arena: the highest-priority non-rejected member wins by default and the rest reject, even mid-stream (§7A.5)",
                forceOk, $"forced={forced}(a={a}) bVote={arena.VoteOf(b)}");
        }

        // gate.arena.fsm-clean-tap-stream: a real PointerFsm drives a clean below-slop down→up = an Accept vote (a tap);
        // a Pinch FSM fed a second contact = EagerAccept (the Phase-4 pinch trigger, signature exercised now). Pure §7B.
        {
            // Monotonic non-zero µs stamps (a 0 down-time is the "no prior down" sentinel, like the touch harness's
            // 1000ms-start clock; the plan documents 0-stamps as the vacuous-fling case).
            var tapFsm = new PointerFsm(); tapFsm.Init(GestureKind.Tap);
            long t = 1_000_000;
            tapFsm.OnDown(new Point2(50, 50), t);
            t += 10_000; tapFsm.OnMove(new Point2(52, 51), t);   // 2px, within slop
            t += 10_000; var tapVote = tapFsm.OnUp(new Point2(51, 50), t);
            bool tapOk = tapVote == ArenaVote.Accept && tapFsm.Phase == GesturePhase.Tapping
                        && tapFsm.Start.X == 50 && tapFsm.Start.Y == 50;   // Start buffered as the DOWN position (§7A.5)

            var pinchFsm = new PointerFsm(); pinchFsm.Init(GestureKind.Pinch);
            pinchFsm.OnDown(new Point2(100, 100), 1_000_000);
            var pinchPending = pinchFsm.Vote;                              // a lone contact: still Pending
            var pinchVote = pinchFsm.OnSecondContact(new Point2(200, 100), 1_005_000);
            bool pinchOk = pinchPending == ArenaVote.Pending && pinchVote == ArenaVote.EagerAccept
                          && pinchFsm.Phase == GesturePhase.Manipulating;

            // Hold: a long-press timer tick ≥ 500ms with the contact still down/within slop promotes to EagerAccept.
            var holdFsm = new PointerFsm(); holdFsm.Init(GestureKind.Hold);
            long hd = 1_000_000;
            holdFsm.OnDown(new Point2(70, 70), hd);
            var holdEarly = holdFsm.OnFrameTick(hd + 300_000);            // +300ms → not yet
            var holdLate = holdFsm.OnFrameTick(hd + 550_000);            // +550ms → fires
            bool holdOk = holdEarly == ArenaVote.Pending && holdLate == ArenaVote.EagerAccept;

            Check("gate.arena.fsm-votes a clean within-slop down→up FSM votes Accept (tap, Start buffered as the DOWN position); a Pinch FSM's second contact votes EagerAccept; a Hold FSM's ≥500ms tick promotes to EagerAccept (§7B)",
                tapOk && pinchOk && holdOk,
                $"tap={tapVote}/{tapFsm.Phase} startBuf=({tapFsm.Start.X},{tapFsm.Start.Y}) pinch={pinchVote}/{pinchFsm.Phase} holdEarly={holdEarly} holdLate={holdLate}");
        }

        // gate.arena.doubletap-second-tap-wins: the OTHER branch of rule 5 — a qualifying second tap arrives inside the
        // window, so the DoubleTap FSM reaches TapCount 2 and votes Accept; fed into the arena it wins over the single Tap.
        {
            var arena = new GestureArena();
            int slot = arena.OpenArena(pointerId: 10);
            int tapM = arena.Enroll(slot, NodeAt(1), GestureKind.Tap);
            int dblM = arena.Enroll(slot, NodeAt(1), GestureKind.DoubleTap);
            arena.CloseArena(slot);
            var dbl = new PointerFsm(); dbl.Init(GestureKind.DoubleTap);
            long t = 2_000_000;
            dbl.OnDown(new Point2(80, 80), t); t += 10_000;
            dbl.OnUp(new Point2(80, 80), t); t += 120_000;          // first tap (within slop); waits for the 2nd
            dbl.OnDown(new Point2(81, 80), t); t += 10_000;          // 2nd down inside DoubleClickMs window → TapCount→2
            var dblVote = dbl.OnUp(new Point2(80, 80), t);
            arena.SetVote(dblM, dblVote);
            arena.SetVote(tapM, ArenaVote.Reject);                  // the single-tap recognizer yields to the matched double
            int winner = arena.ResolveStep(slot);
            bool secondTapOk = dbl.TapCount == 2 && dblVote == ArenaVote.Accept && winner == dblM;
            Check("gate.arena.doubletap-second-tap-wins a qualifying second tap inside the window accumulates TapCount to 2 and the DoubleTap FSM votes Accept, winning the arena over the single Tap (§7A.2 rule 5, the matched branch)",
                secondTapOk, $"tapCount={dbl.TapCount} dblVote={dblVote} winner={winner}(dbl={dblM})");
        }

        // gate.arena.velocity-sampler-ring: the VelocitySampler ring measures a steady flick speed from monotonic
        // (dtMs,dPos) samples, and a 0/duplicate-stamp stream measures ZERO (the vacuous-fling guard the harness relies on).
        {
            var vs = new VelocitySampler();
            vs.Reset(new Point2(0, 200), 1000);
            // 10 monotonic samples, 16ms apart, -6px/sample on Y → 6px/16ms = 375 px/s upward (negative Y).
            uint ms = 1000;
            float y = 200;
            for (int i = 0; i < 10; i++) { ms += 16; y -= 6; vs.Sample(new Point2(0, y), ms); }
            var v = vs.Velocity();
            bool steadyOk = v.Y < -300f && v.Y > -450f && MathF.Abs(v.X) < 1f;   // ~ -375 px/s, no X drift

            // Zero/duplicate stamps → no ring entries → zero velocity (a 0-stamp gesture is a vacuous fling).
            var vz = new VelocitySampler();
            vz.Reset(new Point2(0, 200), 0);
            float yz = 200;
            for (int i = 0; i < 10; i++) { yz -= 30; vz.Sample(new Point2(0, yz), 0); }   // all 0-stamp
            var vzero = vz.Velocity();
            bool zeroOk = vzero.X == 0f && vzero.Y == 0f;
            Check("gate.arena.velocity-sampler-ring the VelocitySampler ring measures a steady flick speed (~-375px/s) from monotonic (dtMs,dPos) samples; a 0/duplicate-stamp stream measures exactly zero (the vacuous-fling guard, §7B)",
                steadyOk && zeroOk, $"steady=({v.X:0},{v.Y:0}) zero=({vzero.X:0},{vzero.Y:0})");
        }

        // gate.arena.churn-zero-alloc: 100 full arena lifecycles (open → enroll 3 → vote → resolve → free) allocate ZERO
        // managed bytes after warmup — the slab seats/member spans/votes are all fixed storage reused in place (the
        // GC.GetAllocatedBytesForCurrentThread delta the existing 0-alloc gates use). Proves the §7A.4 zero-per-frame-heap
        // claim for the coordinator. A SHARED arena instance is reused across the churn (seat recycle, no growth).
        {
            var arena = new GestureArena();
            arena.OnMemberRejected = static _ => { };   // non-null sinks (the dispatcher always wires them) — no closure alloc per call
            arena.OnMemberWon = static _ => { };
            // Warm: JIT the open/enroll/resolve/free path + let the sinks settle before measuring.
            ChurnArenas(arena, 4);
            long before = GC.GetAllocatedBytesForCurrentThread();
            ChurnArenas(arena, 100);
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            bool zeroAlloc = delta == 0;
            Check("gate.arena.churn-zero-alloc 100 full arena lifecycles (open→enroll 3→vote→resolve→free) allocate 0 managed bytes after warmup — slab seats, (offset,len) member spans, and in-place votes are reused with zero growth (§7A.4)",
                zeroAlloc, $"delta={delta}B over 100 arenas, openCount={arena.OpenArenaCount}");
        }

        // gate.arena.team: the §7A.3 selection team (SelectionDrag + Tap under a Tap captain). The prose (NOT the §7A.2
        // pseudocode) governs: internal members never reject each other pre-slop; the captain stands for the team; on a
        // CLEAN UP the captain picks Tap (tap-count, no movement); on a SLOP-CROSS the drag-extend teammate makes the team
        // eager-win and the captain picks the drag. Driven directly over the arena + a real PointerFsm bank (the gate.arena
        // unit pattern), with one NON-team competitor (an outer Pan) so the win still sweeps non-teammates.
        {
            // (A) PRE-SLOP non-rejection + CLEAN-UP picks Tap.
            var arena = new GestureArena();
            var fsms = new PointerFsm[GestureArena.MaxArenas * GestureArena.MaxMembersPerArena];
            int rejects = 0;
            arena.OnMemberRejected = slot => { fsms[slot].Reset(); rejects++; };
            int slotA = arena.OpenArena(pointerId: 21);
            int selDrag = arena.Enroll(slotA, NodeAt(1), GestureKind.SelectionDrag);   // innermost team member (drag-extend)
            int selTap = arena.Enroll(slotA, NodeAt(1), GestureKind.Tap);              // the captain
            int outPan = arena.Enroll(slotA, NodeAt(2), GestureKind.Pan);              // a NON-team competitor (outer scroller)
            arena.EnrollTeam(slotA, captainSlot: selTap, memberOffset: selDrag, memberLen: selTap - selDrag + 1);
            fsms[selDrag].Init(GestureKind.SelectionDrag); fsms[selTap].Init(GestureKind.Tap); fsms[outPan].Init(GestureKind.Pan);
            long t = 1_000_000;
            fsms[selDrag].OnDown(new Point2(60, 60), t); arena.SetVote(selDrag, fsms[selDrag].Vote);
            fsms[selTap].OnDown(new Point2(60, 60), t); arena.SetVote(selTap, fsms[selTap].Vote);
            fsms[outPan].OnDown(new Point2(60, 60), t); arena.SetVote(outPan, fsms[outPan].Vote);
            // A within-slop move: NObody crosses slop. ResolveStep must NOT internally reject a teammate (§7A.3) and must
            // NOT resolve (the team + the Pan are two live entries, both Pending).
            t += 16_000;
            fsms[selDrag].OnMove(new Point2(62, 61), t); arena.SetVote(selDrag, fsms[selDrag].Vote);   // within slop → Pending
            fsms[outPan].OnMove(new Point2(62, 61), t); arena.SetVote(outPan, fsms[outPan].Vote);
            int midResolve = arena.ResolveStep(slotA);
            bool preSlopNoReject = midResolve < 0 && rejects == 0
                                   && arena.VoteOf(selDrag) != ArenaVote.Reject && arena.VoteOf(selTap) != ArenaVote.Reject;
            // Clean up: the Tap captain votes Accept (within slop), the drag stays Pending, the outer Pan votes Reject (a
            // lone contact lifting is no pan). The up-sweep resolves to the team's CAPTAIN (not the innermost drag), the
            // drag teammate is RETAINED (not rejected — only the non-team Pan is swept), and the captain picks Tap.
            t += 16_000;
            arena.SetVote(selTap, fsms[selTap].OnUp(new Point2(61, 60), t));    // within slop → Accept
            arena.SetVote(outPan, fsms[outPan].OnUp(new Point2(61, 60), t));    // Pan → Reject
            int upWin = arena.ResolveUp(slotA);
            bool cleanUpPicksTap = upWin == selTap
                                   && arena.CaptainPick(slotA) == GestureKind.Tap
                                   && arena.VoteOf(selDrag) != ArenaVote.Reject   // teammate retained (no internal reject)
                                   && arena.VoteOf(outPan) == ArenaVote.Reject;   // the non-team competitor was swept

            // (B) SLOP-CROSS picks the drag. Fresh arena; the drag-extend teammate crosses slop → the team eager-wins.
            var arena2 = new GestureArena();
            var fsms2 = new PointerFsm[GestureArena.MaxArenas * GestureArena.MaxMembersPerArena];
            int rejects2 = 0;
            arena2.OnMemberRejected = slot => { fsms2[slot].Reset(); rejects2++; };
            int slotB = arena2.OpenArena(pointerId: 22);
            int selDrag2 = arena2.Enroll(slotB, NodeAt(1), GestureKind.SelectionDrag);
            int selTap2 = arena2.Enroll(slotB, NodeAt(1), GestureKind.Tap);
            int outPan2 = arena2.Enroll(slotB, NodeAt(2), GestureKind.Pan);
            arena2.EnrollTeam(slotB, captainSlot: selTap2, memberOffset: selDrag2, memberLen: selTap2 - selDrag2 + 1);
            fsms2[selDrag2].Init(GestureKind.SelectionDrag); fsms2[selTap2].Init(GestureKind.Tap); fsms2[outPan2].Init(GestureKind.Pan);
            long t2 = 2_000_000;
            fsms2[selDrag2].OnDown(new Point2(60, 60), t2); arena2.SetVote(selDrag2, fsms2[selDrag2].Vote);
            fsms2[selTap2].OnDown(new Point2(60, 60), t2); arena2.SetVote(selTap2, fsms2[selTap2].Vote);
            fsms2[outPan2].OnDown(new Point2(60, 60), t2); arena2.SetVote(outPan2, fsms2[outPan2].Vote);
            t2 += 16_000;
            arena2.SetVote(selDrag2, fsms2[selDrag2].OnMove(new Point2(110, 60), t2));   // +50px → SelectionDrag EagerAccept
            int dragWin = arena2.ResolveStep(slotB);
            // The team eager-wins via the captain; CaptainPick returns the drag (movement); the Tap teammate is retained;
            // the outer Pan is swept (it is not in the team).
            bool slopCrossPicksDrag = dragWin == selTap2
                                      && arena2.CaptainPick(slotB) == GestureKind.SelectionDrag
                                      && arena2.VoteOf(selTap2) != ArenaVote.Reject
                                      && arena2.VoteOf(outPan2) == ArenaVote.Reject
                                      && rejects2 == 1;   // exactly the non-team Pan was rejected (the teammate was not)

            Check("gate.arena.team a SelectionDrag+Tap selection team (captain=Tap) never internally rejects pre-slop; a clean up resolves to the CAPTAIN and CaptainPick=Tap (the drag teammate retained, the outer Pan swept); a slop-cross makes the team eager-win and CaptainPick=SelectionDrag (§7A.3, the prose semantics)",
                preSlopNoReject && cleanUpPicksTap && slopCrossPicksDrag,
                $"preSlopNoReject={preSlopNoReject}(mid={midResolve} rej={rejects}) cleanUp={cleanUpPicksTap}(win={upWin} sel={selTap} pick={arena.CaptainPick(slotA)}) slopCross={slopCrossPicksDrag}(win={dragWin} pick={arena2.CaptainPick(slotB)} rej2={rejects2})");
        }
    }

    static void ChurnArenas(GestureArena arena, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int slot = arena.OpenArena(pointerId: (uint)(i % GestureArena.MaxArenas));
            if (slot < 0) { continue; }   // (won't happen: each is freed before the next, but guard deterministically)
            int tap = arena.Enroll(slot, NodeAt(1), GestureKind.Tap);
            int drag = arena.Enroll(slot, NodeAt(2), GestureKind.Drag);
            arena.Enroll(slot, NodeAt(3), GestureKind.Pan);
            arena.CloseArena(slot);
            arena.SetVote(drag, ArenaVote.EagerAccept);
            arena.ResolveStep(slot);
            arena.CloseAndFree(slot);
        }
    }

    static NodeHandle NodeAt(uint i) => new NodeHandle(new Handle(i, 1));

    static void ArenaConsumerChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.arena.reorder-vs-pan: a horizontal CanDrag strip (item-drag axis = X) inside a vertical scroller (overflow
        // on Y). A horizontal touch drag on item 0 runs ALONG the item axis ⇒ the DragReorder member eager-wins the arena
        // (the item lifts: OnDragStarted + deltas fire; the lifted node carries the DragGhost flag; the list does NOT
        // scroll). A vertical touch drag on item 0 is CROSS-axis ⇒ the Pan member eager-wins (the scroller scrolls; no
        // drag starts). Deterministic, via the arena's axis-locked votes — YieldsToPan is bypassed on the touch path.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("arena-reorder", new Size2(360, 340), 1f)); window.Show();
            var probe = new ArenaReorderInScrollerProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var scroller = FindScrollable(scene, scene.Root);
            scene.TryGetScroll(scroller, out var sc0);
            // ContentNode → child 0 = the Direction=0 strip → its child 0 = item "a".
            var strip = Child(scene, sc0.ContentNode, 0);
            var itemA = Child(scene, strip, 0);
            var aCenter = CenterOf(scene, itemA);

            // (1) HORIZONTAL drag along the item axis ⇒ DragReorder wins. The drag stays within the strip's Y so the
            // gesture is purely along X (the item's reorder axis). 140px of X travel >> the 4px slop.
            float panBefore = sc0.OffsetY;
            TouchGesture(window, host, aCenter, new Point2(aCenter.X + 140f, aCenter.Y), 12, pointerId: 31, msPerStep: 16f);
            scene.TryGetScroll(scroller, out var afterReorder);
            bool reorderWon = probe.StartedA >= 1 && probe.DeltaA >= 1 && probe.CompletedA >= 1;
            bool listDidNotScroll = Near(afterReorder.OffsetY, panBefore);

            // (2) Fresh contact: a VERTICAL (cross-axis) drag on item 0 ⇒ Pan wins, the scroller scrolls, NO new drag.
            int startedBeforePan = probe.StartedA;
            var aCenter2 = CenterOf(scene, Child(scene, Child(scene, sc0.ContentNode, 0), 0));   // re-resolve the strip's item 0
            TouchGesture(window, host, aCenter2, new Point2(aCenter2.X, aCenter2.Y - 180f), 12, pointerId: 32, msPerStep: 16f);
            scene.TryGetScroll(scroller, out var afterPan);
            bool panWon = afterPan.OffsetY > 100f && probe.StartedA == startedBeforePan;   // scrolled + no new item-drag

            Check("gate.arena.reorder-vs-pan a horizontal touch drag-reorder of a list item inside a scroller resolves to DragReorder (item lifts, list does not scroll) while a cross-axis vertical drag pans (the scroller scrolls, no drag) — deterministic, via the arena's axis-locked votes (YieldsToPan subsumed)",
                reorderWon && listDidNotScroll && panWon,
                $"reorder(started={probe.StartedA} delta={probe.DeltaA} done={probe.CompletedA} scroll={afterReorder.OffsetY:0}->was {panBefore:0}) pan(scroll={afterPan.OffsetY:0} newStarts={probe.StartedA - startedBeforePan})");
        }

        // gate.arena.use-gesture: a UseGesture(Tap) component (its node is not otherwise clickable) receives the tap with
        // the correct position. The arena enrolls a Tap member purely from the §13 declaration; the up-sweep resolves it
        // and RouteGestureWin fires the handler with the up position. A below-slop down→up at a known point is the tap.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("arena-gesture", new Size2(320, 240), 1f)); window.Show();
            var probe = new UseGestureTapProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            // The probe's BoxEl IS the rendered root (HostNode = Scene.Root for the top-level component), so the gesture
            // sub lives on Scene.Root; a tap inside its rect hits it. Tap at a known offset inside the box.
            var br = host.Scene.AbsoluteRect(host.Scene.Root);
            var tapPt = new Point2(br.X + 30f, br.Y + 20f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, tapPt, t, 41));
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, tapPt, t + 16, 41));
            host.RunFrame();
            s_touchClockMs = t + 1000;
            bool tapReceived = probe.Taps == 1;
            bool positionOk = Near(probe.LastPos.X, tapPt.X) && Near(probe.LastPos.Y, tapPt.Y);
            Check("gate.arena.use-gesture a UseGesture(Tap) component receives the tap (arena Tap member enrolled from the §13 declaration, routed on the up-sweep) with the correct position",
                tapReceived && positionOk,
                $"taps={probe.Taps} pos=({probe.LastPos.X:0.#},{probe.LastPos.Y:0.#}) expected=({tapPt.X:0.#},{tapPt.Y:0.#}) subs={host.Scene.HasGestureSubs}");
        }

        // gate.arena.swipe-phone-settle: the iOS-model swipe resolution statics. Signed velocity projection opens a
        // fast short flick and a reversed flick cancels; the fresh-open threshold sits at 0.6×cluster; an open row
        // holds positional wobble but closes on a deliberate >=31px/s inward flick; a FullSwipe (Execute) side tracks
        // 1:1 to the full control width (no rubber band — the full-swipe zone needs the travel) while a Reveal side
        // keeps the bounded elastic; the full-swipe arm threshold is CLUSTER-TIED (cluster + 80..140 slack, ceilinged
        // by 0.5×controlW — ≈0.5×width on a phone-width row, no dead band past a small cluster on a desktop-width
        // row) and the release commit uses the PROJECTED rest against the same threshold (a reversed flick cancels
        // a positional arm).
        {
            float resisted = SwipeControlCore.ResistPastExtent(-168f, -68f, 68f);
            bool boundedGive = resisted < -68f && resisted > -81.6f;   // give, but below the 20%-extent asymptote
            bool fastShortOpens = SwipeControlCore.ShouldRestOpen(false, 30f, 68f, 600f);
            bool reversedCancels = !SwipeControlCore.ShouldRestOpen(false, 80f, 68f, -600f);
            bool freshAtSixTenths = SwipeControlCore.ShouldRestOpen(false, 41f, 68f, 0f)      // 0.6×68 = 40.8
                                 && !SwipeControlCore.ShouldRestOpen(false, 40f, 68f, 0f);
            bool openWobbleHolds = SwipeControlCore.ShouldRestOpen(true, 60f, 68f, 0f);
            bool inwardFlickCloses = !SwipeControlCore.ShouldRestOpen(true, 60f, 68f, -40f);
            bool halfwayCloses = !SwipeControlCore.ShouldRestOpen(true, 20f, 68f, 0f);
            bool executeFreeTravel = SwipeControlCore.DragOffset(-290f, 0f, 76f, false, true, 300f) == -290f   // 1:1 past the cluster…
                                  && SwipeControlCore.DragOffset(-400f, 0f, 76f, false, true, 300f) == -300f;  // …hard stop at controlW
            float revealResist = SwipeControlCore.DragOffset(-168f, 0f, 68f, false, false, 300f);
            bool revealBands = revealResist < -68f && revealResist > -81.6f;                   // Reveal keeps the elastic
            float armPhone = SwipeControlCore.ArmThreshold(300f, 68f);     // 68 + clamp(150−68, 80, 140) = 150 ⇒ 0.5×W where the cluster is near half-width
            float armDesktop = SwipeControlCore.ArmThreshold(900f, 90f);   // 90 + 140 slack cap = 230 ⇒ no dead band past a small cluster on a wide row
            float armCramped = SwipeControlCore.ArmThreshold(300f, 160f);  // 160 + 80 slack floor = 240 ⇒ arming stays a distinct step past resting open
            bool armThresholdTiers = armPhone == 150f && armDesktop == 230f && armCramped == 240f;
            bool armFlips = !SwipeControlCore.IsArmed(149f, armPhone) && SwipeControlCore.IsArmed(150f, armPhone);
            bool commitProjects = SwipeControlCore.ShouldCommit(SwipeControlCore.ProjectedDistance(80f, 600f), armPhone)
                               && !SwipeControlCore.ShouldCommit(SwipeControlCore.ProjectedDistance(80f, -600f), armPhone);
            float livePlate = SwipeControlCore.ExpandedPrimaryWidth(450f, 52f, 0f);
            bool plateFollowsReveal = livePlate == 430f && livePlate < 900f;   // 20px insets; never jumps to row width
            Check("gate.arena.swipe-phone-settle swipe resolution statics: signed flick projection (fast short opens, reversed cancels), 0.6x-cluster fresh-open threshold, halfway open-state hysteresis with the 31px/s inward-close floor, FullSwipe 1:1 travel to controlW vs Reveal bounded elastic, cluster-tied arm threshold (0.5xW phone / cluster+140 desktop / cluster+80 floor), projected-velocity commit, and armed primary width follows the live reveal",
                boundedGive && fastShortOpens && reversedCancels && freshAtSixTenths && openWobbleHolds && inwardFlickCloses && halfwayCloses && executeFreeTravel && revealBands && armThresholdTiers && armFlips && commitProjects && plateFollowsReveal,
                $"resisted={resisted:0.0} fastOpen={fastShortOpens} reverseCancel={reversedCancels} fresh0.6={freshAtSixTenths} wobble={openWobbleHolds} inwardClose={inwardFlickCloses} halfwayClose={halfwayCloses} execFree={executeFreeTravel} revealBand={revealResist:0.0} armTiers=({armPhone:0}/{armDesktop:0}/{armCramped:0}) armFlip={armFlips} commit={commitProjects} livePlate={livePlate:0}");
        }

        // gate.arena.swipe-in-scroller: a SwipeControl row (right reveal actions, a DragYieldsToPan cross-axis Drag) at the
        // top of a VERTICAL scroller — the canonical §7A race. A HORIZONTAL touch swipe along the row reveals the actions
        // (the swipe's X-axis Drag eager-wins the arena; the scroller does NOT scroll), while a VERTICAL drag on the SAME
        // row scrolls the list (the Y-axis-locked Pan eager-wins; the swipe stays closed). Both deterministic, via the
        // axis-locked Drag-vs-Pan votes — the OnDrag eager-capture is suppressed for DragYieldsToPan so Pan can compete.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("arena-swipe", new Size2(380, 340), 1f)); window.Show();
            var probe = new SwipeInScrollerProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var scroller = FindScrollable(scene, scene.Root);
            scene.TryGetScroll(scroller, out var sc0);
            // ContentNode → child 0 = the SwipeControl row. Its centre is the gesture anchor (well within the row's height).
            var row = Child(scene, sc0.ContentNode, 0);
            var rowCenter = CenterOf(scene, row);
            // Start a touch nearer the LEFT edge of the row so a +X swipe has room to travel along the row toward the right
            // (revealing the trailing/right actions which open on a leftward... — the engine reveals the right side on a
            // negative offset; we swipe LEFT, toward -X, to reveal the right actions).
            var swStart = new Point2(rowCenter.X + 120f, rowCenter.Y);

            // (1) HORIZONTAL swipe along the row (−X, toward the right reveal) ⇒ the swipe Drag wins: content slides, list
            // stays put. 150px of X travel >> the 4px slop and >> the 100px open threshold.
            float scrollBefore = sc0.OffsetY;
            TouchGesture(window, host, swStart, new Point2(swStart.X - 150f, swStart.Y), 12, pointerId: 51, msPerStep: 16f);
            for (int i = 0; i < 4; i++) host.RunFrame();   // let the open glide seed/advance
            scene.TryGetScroll(scroller, out var afterSwipe);
            float revealedX = MaxAbsTrackX(host, scroller);
            bool swipeOpened = revealedX > 1f;                       // content translated sideways (revealed)
            bool listHeld = Near(afterSwipe.OffsetY, scrollBefore);  // the list did NOT scroll

            // (2) Fresh contact: a VERTICAL drag on the SAME row ⇒ Pan wins, the list scrolls. (The swipe from (1) is still
            // open; a vertical drag must scroll the list regardless, NOT pan the swipe further.) 180px of Y travel up.
            var row2 = Child(scene, sc0.ContentNode, 0);
            var v2Start = CenterOf(scene, row2);
            float scrollBeforeV = afterSwipe.OffsetY;
            TouchGesture(window, host, v2Start, new Point2(v2Start.X, v2Start.Y - 180f), 12, pointerId: 52, msPerStep: 16f);
            scene.TryGetScroll(scroller, out var afterPan);
            bool listScrolled = afterPan.OffsetY > scrollBeforeV + 100f;   // the list scrolled on the vertical drag

            Check("gate.arena.swipe-in-scroller a horizontal touch swipe on a SwipeControl row inside a vertical scroller reveals the row's actions (the cross-axis Drag eager-wins; the list holds) while a vertical drag on the same row scrolls the list (the scroll-axis Pan eager-wins) — deterministic via the axis-locked Drag-vs-Pan votes (DragYieldsToPan)",
                swipeOpened && listHeld && listScrolled,
                $"swipe(revealX={revealedX:0.#} listOffset={afterSwipe.OffsetY:0}->was {scrollBefore:0}) pan(offset={afterPan.OffsetY:0}->was {scrollBeforeV:0})");
        }

        // gate.arena.swipe-wrapped-interactive-row: the Phase-D §7A.1 route-walk composition. A SwipeControl wraps a
        // CLICKABLE row (OnClick, no drag handler of its OWN) inside a vertical scroller — so the dispatcher must WALK
        // from the hit row to the wrapper's DragYieldsToPan Drag member for the swipe to arm. (a) a horizontal touch
        // swipe reveals the actions WITHOUT clicking the row; (b) a vertical drag scrolls the list; (c) a below-slop tap
        // still clicks the row (the Tap wins the up-sweep — the enrolled swipe Drag never crossed slop); (d) a MOUSE
        // press+horizontal-move never pans (mouse OnDrag is hit-node-only, the walk is touch-only, and the TouchOnly belt
        // no-ops PanMove). This is the exact composition RowSwipe ships on queue/eager rows.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("swipe-wrapped", new Size2(380, 340), 1f)); window.Show();
            var probe = new SwipeWrappedRowProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var scroller = FindScrollable(scene, scene.Root);
            scene.TryGetScroll(scroller, out var sc0);
            var row = Child(scene, sc0.ContentNode, 0);
            var rowCenter = CenterOf(scene, row);

            // (c) below-slop tap ⇒ the inner ROW clicks (the walk enrolls a swipe Drag member, but below slop nothing
            // eager-wins → the up-sweep resolves the row's Tap → OnClick). Done first, while the row is closed.
            uint tt = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, rowCenter, tt, 81));
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(rowCenter.X + 2f, rowCenter.Y), tt + 16, 81));
            host.RunFrame();
            s_touchClockMs = tt + 1000;
            bool tapClicked = probe.RowClicks == 1;

            // (a) horizontal swipe (−X toward the right reveal) ⇒ the walk-found wrapper Drag eager-wins: content slides,
            // the row does NOT click, the list holds. 150px of X travel >> the 4px slop and the 100px open threshold.
            int clicksBeforeSwipe = probe.RowClicks;
            float scrollBefore = sc0.OffsetY;
            var swStart = new Point2(rowCenter.X + 120f, rowCenter.Y);
            TouchGesture(window, host, swStart, new Point2(swStart.X - 150f, swStart.Y), 12, pointerId: 82, msPerStep: 16f);
            for (int i = 0; i < 4; i++) host.RunFrame();   // let the open glide seed/advance
            scene.TryGetScroll(scroller, out var afterSwipe);
            float revealedX = MaxAbsTrackX(host, scroller);
            bool swipeRevealed = revealedX > 1f;
            bool noClickOnSwipe = probe.RowClicks == clicksBeforeSwipe;
            bool listHeld = Near(afterSwipe.OffsetY, scrollBefore);

            // A press elsewhere in the list dismisses the open row through the host-level pointer observer.
            var outside = new Point2(20f, 300f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, outside, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, outside, 0, 0));
            host.RunFrame();
            for (int i = 0; i < 12; i++) host.RunFrame();
            bool outsidePressClosed = MaxAbsTrackX(host, scroller) < 1f;

            TouchGesture(window, host, swStart, new Point2(swStart.X - 150f, swStart.Y), 12, pointerId: 84, msPerStep: 16f);
            for (int i = 0; i < 4; i++) host.RunFrame();

            // Routed focus leaving the SwipeControl subtree closes it; no translated row may remain stranded after
            // the user tabs/clicks elsewhere.
            host.Input.SetFocus(NodeHandle.Null);
            for (int i = 0; i < 12; i++) host.RunFrame();
            bool focusLossClosed = MaxAbsTrackX(host, scroller) < 1f;

            TouchGesture(window, host, swStart, new Point2(swStart.X - 150f, swStart.Y), 12, pointerId: 85, msPerStep: 16f);
            for (int i = 0; i < 4; i++) host.RunFrame();
            window.IsActive = false; host.RunFrame();
            for (int i = 0; i < 12; i++) host.RunFrame();
            bool windowBlurClosed = MaxAbsTrackX(host, scroller) < 1f;
            window.IsActive = true; host.RunFrame();

            // (b) fresh contact: a vertical drag scrolls the list (the scroll-axis Pan eager-wins; the open-row tap shield
            // carries no drag handler, so the press still route-walks to the wrapper and the Pan competes normally).
            var v2 = CenterOf(scene, Child(scene, sc0.ContentNode, 0));
            float scrollBeforeV = afterSwipe.OffsetY;
            TouchGesture(window, host, v2, new Point2(v2.X, v2.Y - 180f), 12, pointerId: 83, msPerStep: 16f);
            scene.TryGetScroll(scroller, out var afterPan);
            bool listScrolled = afterPan.OffsetY > scrollBeforeV + 100f;

            // (d) MOUSE press + horizontal move on a FRESH host ⇒ NO pan: the content must not translate.
            using var appM = new HeadlessPlatformApp();
            var windowM = new HeadlessWindow(new WindowDesc("swipe-wrapped-mouse", new Size2(380, 340), 1f)); windowM.Show();
            var probeM = new SwipeWrappedRowProbe();
            using var hostM = new AppHost(appM, windowM, new HeadlessGpuDevice(), fonts, strings, probeM);
            hostM.RunFrame();
            var sceneM = hostM.Scene;
            var scrollerM = FindScrollable(sceneM, sceneM.Root);
            sceneM.TryGetScroll(scrollerM, out var scM);
            var rcM = CenterOf(sceneM, Child(sceneM, scM.ContentNode, 0));
            windowM.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(rcM.X + 120f, rcM.Y), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 5_000));
            hostM.RunFrame();
            for (int i = 1; i <= 12; i++)
            {
                windowM.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(rcM.X + 120f - i * 12f, rcM.Y), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 5_000 + (uint)i * 16));
                hostM.RunFrame();
            }
            windowM.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(rcM.X - 30f, rcM.Y), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 5_000 + 13 * 16));
            hostM.RunFrame();
            float mouseTrackX = MaxAbsTrackX(hostM, scrollerM);
            bool mouseNoSwipe = mouseTrackX < 1f;

            Check("gate.arena.swipe-wrapped-interactive-row a SwipeControl wrapping a CLICKABLE row (no drag handler of its own) inside a vertical scroller route-walks to the wrapper's Drag member: a horizontal touch swipe reveals the actions without clicking the row, a vertical drag scrolls, a below-slop tap still clicks the row, and a mouse press+move never pans (touch-only arena + the TouchOnly belt)",
                tapClicked && swipeRevealed && noClickOnSwipe && listHeld && outsidePressClosed && focusLossClosed && windowBlurClosed && listScrolled && mouseNoSwipe,
                $"tap(click={probe.RowClicks}) swipe(revealX={revealedX:0.#} click+={probe.RowClicks - clicksBeforeSwipe} listHeld={listHeld} outside={outsidePressClosed} focus={focusLossClosed} blur={windowBlurClosed}) pan(off={afterPan.OffsetY:0}->was {scrollBeforeV:0}) mouseTrackX={mouseTrackX:0.##}");
        }

        // gate.arena.swipe-recycle-reset: the bound-slot RECYCLE contract. An OPEN SwipeControl row snap-closes
        // (TranslateX → 0, NO glide) when its ResetKey signal bumps — a scrolled-off slot rebinding to a new track must
        // present it CLOSED the same frame. Open the row by a horizontal swipe, bump the key, assert the pan zeroed.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("swipe-reset", new Size2(380, 340), 1f)); window.Show();
            var probe = new SwipeResetProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var scroller = FindScrollable(scene, scene.Root);
            scene.TryGetScroll(scroller, out var sc0);
            var rc = CenterOf(scene, Child(scene, sc0.ContentNode, 0));
            var swStart = new Point2(rc.X + 120f, rc.Y);
            TouchGesture(window, host, swStart, new Point2(swStart.X - 150f, swStart.Y), 12, pointerId: 84, msPerStep: 16f);
            for (int i = 0; i < 4; i++) host.RunFrame();
            float openX = MaxAbsTrackX(host, scroller);
            bool opened = openX > 1f;

            probe.ResetKey.Value = probe.ResetKey.Peek() + 1;   // recycle bump → the snap-close effect fires
            for (int i = 0; i < 4; i++) host.RunFrame();
            float afterReset = MaxAbsTrackX(host, scroller);
            bool snappedClosed = afterReset < 1f;

            Check("gate.arena.swipe-recycle-reset an open SwipeControl row snap-closes (TranslateX → 0, no glide) when its ResetKey signal bumps — the bound-slot recycle contract (a reused slot presents the new track closed the same frame)",
                opened && snappedClosed,
                $"openX={openX:0.#} afterResetBump={afterReset:0.##}");
        }

        // gate.arena.flipview-flick-velocity: a FlipView (UseTouchAnimationsForAllNavigation) commits on REAL release
        // velocity through the MandatorySingle snap — a fast flick navigates to the adjacent page even short of 50%, while
        // a slow drag that stays under 50% springs back to the same page. This is the velocity-projected resting offset
        // (live + v/-ln(decay)) rounded to the nearest index, rail-bounded to one page (FlipView_Partial.cpp:1643-1699).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("flip-flick", new Size2(420, 280), 1f)); window.Show();
            var probe = new FlipFlickProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var center = CenterOf(scene, scene.Root);

            // (1) FAST flick LEFT (−X), short travel (90px << 200px = 50% of the 400px page) but high speed (8ms/step over
            // 9 steps ⇒ ~1250px/s) ⇒ the projected resting offset rails to the next page: navigates to index 1.
            TouchGesture(window, host, center, new Point2(center.X - 90f, center.Y), 9, pointerId: 61, msPerStep: 8f);
            for (int i = 0; i < 18; i++) host.RunFrame();   // let the navigate glide settle
            int afterFlick = probe.Selected;
            bool flickNavigated = afterFlick == 1;

            // (2) SLOW drag LEFT, short travel (60px << 50%) at LOW speed (60ms/step over 10 steps ⇒ ~100px/s) ⇒ the
            // projected resting offset stays under 50% ⇒ springs back to the SAME page (index 1, unchanged).
            var c2 = CenterOf(scene, scene.Root);
            TouchGesture(window, host, c2, new Point2(c2.X - 60f, c2.Y), 10, pointerId: 62, msPerStep: 60f);
            for (int i = 0; i < 18; i++) host.RunFrame();
            int afterSlow = probe.Selected;
            bool slowSprungBack = afterSlow == afterFlick;   // no further navigation (stayed on the page the flick reached)

            Check("gate.arena.flipview-flick-velocity a fast FlipView touch flick navigates the adjacent page even short of 50% (velocity-projected MandatorySingle snap) while a slow under-50% drag springs back to the same page (UseTouchAnimationsForAllNavigation, real arena velocity)",
                flickNavigated && slowSprungBack,
                $"flick→idx={afterFlick} (want 1) slow→idx={afterSlow} (want {afterFlick}, sprung-back)");
        }
    }

    static void ArenaDeterminismChecks(StringTable strings)
    {
        // gate.arena.determinism: a scripted multi-gesture sequence — tap, double-tap, hold, drag-reorder-in-scroller,
        // swipe-in-scroller, pan+fling — driven TWICE from a FRESH scene each time, with one recorder accumulating the
        // whole arbitration ledger per run. The two signatures (winner ids, vote order, sweep order) MUST be BIT-IDENTICAL
        // — the §12.6 "resolves to the same winner every run" property over the full ledger, not just the final winner.
        {
            var recA = new GestureArenaRecorder();
            var recB = new GestureArenaRecorder();
            int gesturesA = DriveArenaScript(strings, recA);
            int gesturesB = DriveArenaScript(strings, recB);
            string sigA = recA.Signature();
            string sigB = recB.Signature();
            bool identical = sigA == sigB;
            bool nonTrivial = recA.Count > 20 && !recA.Overflowed && !recB.Overflowed;   // the script exercised real arbitration
            bool sameShape = gesturesA == gesturesB && gesturesA == 6;
            if (!identical) Console.WriteLine("    [determinism diff]\n--- run A ---\n" + sigA + "--- run B ---\n" + sigB);
            Check("gate.arena.determinism a scripted tap/double-tap/hold/drag-reorder-in-scroller/swipe-in-scroller/pan+fling sequence run twice from a fresh scene produces a BIT-IDENTICAL arena trace (winner ids, vote order, sweep order) — the validation.md §12.6 L2 arena-resolution determinism property over the full ledger",
                identical && nonTrivial && sameShape,
                $"identical={identical} entriesA={recA.Count} entriesB={recB.Count} gesturesA={gesturesA} gesturesB={gesturesB} overflowA={recA.Overflowed}");
            s_arenaDeterminism = $"identical={identical} entriesA={recA.Count} entriesB={recB.Count} gesturesA={gesturesA} gesturesB={gesturesB}";
        }

        // gate.arena.determinism.integrator-sweep: the same pan+fling target, replayed at the three animation timesteps
        // validation.md:1145 names (dt ∈ {8.33, 16.67, 33.3} ms), must produce an IDENTICAL arena RESOLUTION trace
        // (winner + sweep order). The event script (positions + event stamps) is held fixed across the sweep; only the
        // animation dt varies — so the post-up fling decay lands at DIFFERENT offsets per dt (the gate asserts that too),
        // but the arena's arbitration (who wins the pan, in what order the losers are swept) is dt-invariant, exactly as
        // §12.6/validation.md:1145 demands ("same target + timestep ⇒ identical trace"; here, identical ACROSS timesteps
        // because the arbitration is on the event clock, not the animation clock — the integrator is downstream of it).
        {
            string res833 = FlingResolutionTrace(strings, dtMs: 8.33f, out float off833);
            string res1667 = FlingResolutionTrace(strings, dtMs: 16.67f, out float off1667);
            string res333 = FlingResolutionTrace(strings, dtMs: 33.3f, out float off333);
            bool traceIdentical = res833 == res1667 && res1667 == res333;
            bool nonEmptyTrace = res1667.Contains("WIN ") && res1667.Contains("Pan");   // the Pan really won (a real resolution)
            // Offsets may differ per dt — assert at least one pair DIFFERS so the sweep is genuinely varying the integrator
            // (a degenerate "all equal" would make the trace-identity vacuous). All three landing identically would still
            // pass trace-identity, but the differing-offset assertion proves the integrator actually ran at three rates.
            // The exact coast integral is intentionally almost frame-rate invariant; only the discrete settle cutoff
            // leaves a small timestep-dependent remainder. With the faster WinUI-like decay that remainder is subpixel,
            // so use a diagnostic epsilon rather than a visible-distance tolerance.
            bool offsetsVary = !Near(off833, off333, 0.01f) || !Near(off833, off1667, 0.01f);
            if (!traceIdentical) Console.WriteLine($"    [dt-sweep diff]\n8.33:\n{res833}16.67:\n{res1667}33.3:\n{res333}");
            Check("gate.arena.determinism.integrator-sweep the same pan+fling target replayed at dt ∈ {8.33,16.67,33.3}ms produces an IDENTICAL arena resolution trace (winner + sweep order) while the fling settles at dt-dependent offsets — the validation.md:1145 integrator-determinism sweep (arbitration is on the event clock, not the animation clock)",
                traceIdentical && nonEmptyTrace && offsetsVary,
                $"traceIdentical={traceIdentical} offsets=({off833:0.#},{off1667:0.#},{off333:0.#}) vary={offsetsVary} trace=[{res1667.Replace('\n', '|')}]");
            // Condense the winner line "WIN p91 m0 Pan" → "WIN Pan" (action token + gesture kind) for the summary echo.
            s_arenaIntegratorSweep = $"traceIdentical={traceIdentical} offsets=({off833:0.#},{off1667:0.#},{off333:0.#}) vary={offsetsVary} trace={CondenseWinTrace(res1667)}";
        }

        // gate.arena.fastpath-sync: the §7A.5 fast-path regression guard. A SINGLE-recognizer Slider touch drag (one
        // OnDrag member) must capture SYNCHRONOUSLY and fire OnDrag the SAME frame — the arena with one member is
        // last-standing immediately, so capture is NOT tentative and the value tracks from the first move (no deferral).
        // This is the §7A.5 "common single-recognizer case is unchanged in observable behavior" contract, asserted
        // explicitly (the regression bar: Slider/SwipeControl/EditableText must not regress under the arena).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("arena-fastpath", new Size2(320, 160), 1f)); window.Show();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new FastPathSliderProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var sliderNode = FindRole(scene, scene.Root, AutomationRole.Slider);
            var sr = scene.AbsoluteRect(sliderNode.IsNull ? scene.Root : sliderNode);
            // Press at 30% of the track, then ONE move to ~70%. The single OnDrag recognizer must capture on the first move
            // and the value must change THAT frame (synchronous capture; no across-frame deferral).
            uint t = s_touchClockMs;
            float y = sr.Y + sr.H / 2f;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(sr.X + sr.W * 0.30f, y), t, 1));
            host.RunFrame();
            float valAfterPress = probe.Val;
            // ONE move past slop → OnDrag must fire and the value must move THIS same frame; capture must be hard (resolved),
            // not tentative (a single-member arena resolves on enrollment via last-standing).
            window.QueueInput(Touch(InputKind.PointerMove, new Point2(sr.X + sr.W * 0.70f, y), t + 16, 1));
            host.RunFrame();
            float valAfterOneMove = probe.Val;
            bool tentativeMidDrag = host.Input.Arena.CaptureIsTentative;   // a lone OnDrag recognizer ⇒ NOT tentative
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(sr.X + sr.W * 0.70f, y), t + 32, 1));
            host.RunFrame();
            s_touchClockMs = t + 1000;
            bool movedSameFrame = valAfterOneMove > valAfterPress + 0.05f;   // OnDrag fired on the first move (synchronous)
            bool capturedSync = !tentativeMidDrag;                          // single recognizer → immediate hard capture
            Check("gate.arena.fastpath-sync a single-recognizer Slider touch drag captures synchronously and fires OnDrag the SAME frame (the lone member is last-standing immediately, capture is not tentative) — the §7A.5 fast-path regression guard",
                movedSameFrame && capturedSync,
                $"press={valAfterPress:0.00} after1move={valAfterOneMove:0.00} tentativeMidDrag={tentativeMidDrag}");
            s_arenaFastpath = $"press={valAfterPress:0.00} after1move={valAfterOneMove:0.00} tentativeMidDrag={tentativeMidDrag}";
        }

        // gate.arena.alloc-zero: a MULTI-recognizer competition gesture allocates 0 managed bytes on the FRAME HOT HALF —
        // the phase-7+ work the resolved gesture drives (the captured drag/reorder controller + the scroll write/virtual
        // re-realize + record) is slab/fixed storage. Drive the reorder-vs-pan race (DragReorder + Tap + Pan members
        // competing, axis-locked votes resolving) for 30 frames and assert HotPhaseAllocBytes==0 the whole window.
        // SCOPE (matters): HotPhaseAllocBytes = GetAllocatedBytesForCurrentThread delta captured INSIDE Paint (AppHost.cs:597)
        // → it spans phases 3-11 ONLY. The per-event arena arbitration itself — EnrollTouchArena / the StepTouchArena vote
        // loop / the PointerFsm bank / the selection-team enroll / UpSweepTouchArena — runs in phase-2 Dispatch (RunFrame,
        // BEFORE Paint), OUTSIDE this window. So this gate proves the DOWNSTREAM (post-resolution) path is 0-alloc; the
        // per-event coordinator surface is covered DIRECTLY by gate.arena.dispatch-alloc-zero below (a delta wrapped around
        // host.Input.Dispatch). No recorder is attached (the production path; the recorder is a test seam).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("arena-alloc", new Size2(360, 340), 1f)); window.Show();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new ArenaReorderInScrollerProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var scroller = FindScrollable(scene, scene.Root);
            scene.TryGetScroll(scroller, out var sc0);
            var strip = Child(scene, sc0.ContentNode, 0);
            var itemA = Child(scene, strip, 0);
            var aCenter = CenterOf(scene, itemA);

            // Down on the draggable item: opens the arena with DragReorder + Tap + Pan members all competing. Then 30 moves
            // along a DIAGONAL that stays just under BOTH axis slops for the first frames (keeps three members live and
            // re-voting — the genuine multi-recognizer competition) before crossing. The down + the first warm moves JIT
            // the arena/FSM path; the measured window is the steady competition + resolution.
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, aCenter, t, 71));
            host.RunFrame();   // open + enroll (cold; excluded from the measured window)
            // Warm a couple of move frames (JIT the StepTouchArena vote/resolve path) before measuring.
            for (int i = 1; i <= 2; i++) { window.QueueInput(Touch(InputKind.PointerMove, new Point2(aCenter.X + i * 1.0f, aCenter.Y + i * 1.0f), t + (uint)i * 16, 71)); host.RunFrame(); }

            long worst = 0;
            // 30 steady competition/resolution frames: a horizontal sweep (the reorder eager-wins partway, then the
            // captured drag drives the controller) — all on fixed storage. Each move is its own frame (no coalescing).
            for (int i = 3; i <= 32; i++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(aCenter.X + i * 6f, aCenter.Y), t + (uint)i * 16, 71));
                var f = host.RunFrame();
                if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes;
            }
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(aCenter.X + 33 * 6f, aCenter.Y), t + 33 * 16, 71));
            var fu = host.RunFrame();
            if (fu.HotPhaseAllocBytes > worst) worst = fu.HotPhaseAllocBytes;
            s_touchClockMs = t + 2000;
            Check("gate.arena.alloc-zero a 30-frame multi-recognizer competition (DragReorder vs Tap vs Pan, axis-locked votes resolving) allocates 0 managed bytes on the frame hot half (phases 3-11) — the resolved gesture's downstream drag/scroll/re-realize/record is slab/fixed storage (§7A.4); the per-event arbitration in phase-2 dispatch is gated by gate.arena.dispatch-alloc-zero",
                worst == 0, $"{worst} bytes worst over the 30-frame reorder-vs-pan race");
            s_arenaAllocZero = $"{worst} bytes";
        }

        // gate.arena.dispatch-alloc-zero: the COMPANION that closes gate.arena.alloc-zero's instrument gap. It measures the
        // GetAllocatedBytesForCurrentThread delta wrapped DIRECTLY around the phase-2 dispatch entrypoint — host.Input.Dispatch
        // (the public seam RunFrame calls at AppHost.cs:494, BEFORE Paint) — so the measured window IS the per-event arena
        // path the headline names: EnrollTouchArena (OpenArena + innermost-first Enroll + ArmMemberFsm = PointerFsm.Init/OnDown
        // + EnrollTeam), the StepTouchArena vote loop (PointerFsm.OnMove per live member + ResolveStep + the RouteGestureWin
        // sink), and UpSweepTouchArena (PointerFsm.OnUp + ResolveUp). Two REAL multi-recognizer scenes are driven through the
        // real dispatcher: (A) DragReorder-vs-Tap-vs-Pan (the full StepTouchArena vote loop + an eager-win sweep), and (B) a
        // selectable editor inside a scroller (the §7A.3 selection TEAM enroll: Tap/DoubleTap/SelectionDrag under a captain,
        // plus the VelocitySampler ring on the captured drag). The scene is laid out by RunFrame BEFORE measuring; the down +
        // a couple of warm moves JIT the path; the steady move stream + up is the measured window. A forced alloc on ANY of
        // those phase-2 paths (e.g. a stray new[]/closure in StepTouchArena) is caught HERE — the gap the audit demonstrated
        // with a `new byte[64]` in StepTouchArena that gate.arena.alloc-zero could not see. No recorder attached.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("arena-dispatch-alloc", new Size2(360, 340), 1f)); window.Show();
            var fonts = new HeadlessFontSystem(strings);

            // Dispatch directly via a reused 1-element buffer so the SPAN itself never allocates (the measured window must
            // contain only the dispatcher's own work, not the harness's plumbing). DispatchOne returns the dispatch's click
            // count (unused) — the call is the point. The buffer is filled in place per event.
            var one = new InputEvent[1];
            long deltaA = 0, deltaB = 0;

            // (A) The DragReorder-vs-Tap-vs-Pan race: opens an arena with three competing members and runs the full
            // StepTouchArena vote loop (axis-locked OnMove votes + ResolveStep) every move, then an eager-win sweep.
            {
                var probe = new ArenaReorderInScrollerProbe();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
                host.RunFrame();                                   // lay out the scene (cold; not measured)
                var scene = host.Scene;
                var scroller = FindScrollable(scene, scene.Root);
                scene.TryGetScroll(scroller, out var sc0);
                var strip = Child(scene, sc0.ContentNode, 0);
                var itemA = Child(scene, strip, 0);
                var aCenter = CenterOf(scene, itemA);

                // Helper: drive ONE complete horizontal drag-reorder gesture (down → slop-crossing moves → up) through the
                // real dispatcher. DragReorder eager-wins along the item axis, so this exercises ClaimTouchReorder →
                // DragController.Promote/Move/Complete (the drag-ghost SetShadow/ClearShadow round-trip) AND the FSM bank.
                void DriveReorder(uint t0)
                {
                    one[0] = Touch(InputKind.PointerDown, aCenter, t0, 71); host.Input.Dispatch(one);
                    for (int i = 1; i <= 14; i++) { one[0] = Touch(InputKind.PointerMove, new Point2(aCenter.X + i * 6f, aCenter.Y), t0 + (uint)i * 16, 71); host.Input.Dispatch(one); }
                    one[0] = Touch(InputKind.PointerUp, new Point2(aCenter.X + 15 * 6f, aCenter.Y), t0 + 15 * 16, 71); host.Input.Dispatch(one);
                }

                // WARM a FULL gesture first: besides JITting the claim/promote path, this seats the drag-ghost's shadow in
                // the SceneStore shadow side-table (a Dictionary whose backing is lazily grown on the first insert, then
                // freed-not-shrunk on ClearShadow at drag-up). The MEASURED gesture below reuses that freed slot at zero
                // alloc — the one-time side-table init is a cold edge (the same warm-up discipline the churn gate uses),
                // not steady per-event work. Without this warm, the first SetShadow's Dictionary growth shows as ~256B.
                DriveReorder(s_touchClockMs);
                s_touchClockMs += 2000;

                // MEASURED: a second identical complete drag-reorder gesture. The arena open/enroll/vote/resolve/sweep, the
                // PointerFsm bank, ClaimTouchReorder, and the SetShadow/ClearShadow round-trip all run on warmed storage.
                uint t = s_touchClockMs;
                long before = GC.GetAllocatedBytesForCurrentThread();
                DriveReorder(t);
                deltaA = GC.GetAllocatedBytesForCurrentThread() - before;
                s_touchClockMs = t + 2000;
            }

            // (B) The selection TEAM: a selectable editor inside a scroller. The down enrolls Tap + DoubleTap + SelectionDrag
            // (the editor's OnDrag) under a captain (EnrollTeam) and arms their FSMs; the drag-extend moves run the captured
            // OnDrag (the §7A.5 fast-path) while the VelocitySampler ring samples — all on fixed storage.
            {
                const float Adv = 14f * 0.55f;   // headless advance model @ FontSize 14 (the EditableTextCoreChecks model)
                static NodeHandle TextVis(SceneStore s, NodeHandle n)   // nearest Text-visual descendant (the Phase-2 helper, inlined)
                {
                    if (n.IsNull) return NodeHandle.Null;
                    if (s.Paint(n).VisualKind == VisualKind.Text) return n;
                    for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) { var r = TextVis(s, c); if (!r.IsNull) return r; }
                    return NodeHandle.Null;
                }
                var root = new TouchEditInScrollerProbe();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, root);
                host.RunFrame();                                   // lay out the scene (cold; not measured)
                var scene = host.Scene;
                var field = FindRole(scene, scene.Root, AutomationRole.Text);
                var tn = TextVis(scene, field);
                var ta = scene.AbsoluteRect(tn);
                float wy = ta.Y + ta.H / 2f;
                uint t = s_touchClockMs;

                // Down (opens the arena + enrolls the selection team + arms the FSMs) + 2 warm moves — excluded.
                one[0] = Touch(InputKind.PointerDown, new Point2(ta.X + 1f * Adv + 1f, wy), t, 17); host.Input.Dispatch(one);
                for (int i = 1; i <= 2; i++) { one[0] = Touch(InputKind.PointerMove, new Point2(ta.X + (1f + i) * Adv + 1f, wy), t + (uint)i * 16, 17); host.Input.Dispatch(one); }

                // Measured: 8 drag-extend moves (captured OnDrag + VelocitySampler ring) + the up.
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 3; i <= 10; i++) { one[0] = Touch(InputKind.PointerMove, new Point2(ta.X + (1f + i) * Adv + 1f, wy + i * 4f), t + (uint)i * 16, 17); host.Input.Dispatch(one); }
                one[0] = Touch(InputKind.PointerUp, new Point2(ta.X + 11f * Adv + 1f, wy + 44f), t + 11 * 16, 17); host.Input.Dispatch(one);
                deltaB = GC.GetAllocatedBytesForCurrentThread() - before;
                s_touchClockMs = t + 2000;
            }

            long worst = Math.Max(deltaA, deltaB);
            Check("gate.arena.dispatch-alloc-zero the per-event arena path itself — EnrollTouchArena / the StepTouchArena vote loop / the PointerFsm bank / the selection-team enroll / UpSweepTouchArena, measured by a GetAllocatedBytesForCurrentThread delta wrapped DIRECTLY around host.Input.Dispatch (phase-2, before Paint) — allocates 0 managed bytes across a reorder-vs-pan race AND a selection-team drag (§7A.4)",
                worst == 0, $"reorder-vs-pan={deltaA}B selection-team={deltaB}B (direct phase-2 dispatch delta)");
            s_arenaDispatchAllocZero = $"{worst} bytes";
        }
    }
    static string CondenseWinTrace(string sig)
    {
        foreach (var line in sig.Split('\n'))
        {
            string ln = line.Trim();
            if (ln.Length == 0) continue;
            var toks = ln.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return toks.Length >= 2 ? $"{toks[0]} {toks[^1]}" : ln;
        }
        return sig.Trim();
    }

    static int DriveArenaScript(StringTable strings, GestureArenaRecorder rec)
    {
        var fonts = new HeadlessFontSystem(strings);
        int legs = 0;

        // (1) TAP: a below-slop down→up on a clickable virtual-list row → the Tap member wins on the up-sweep.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("det-tap", new Size2(320, 420), 1f)); window.Show();
            var probe = new TouchTapPanProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            host.Input.Arena.Recorder = rec;   // attach after the mount frame (no mount-time arena activity to record)
            var row = host.Scene.Root;
            var rr = host.Scene.AbsoluteRect(row);
            var tapPt = new Point2(rr.X + 40f, rr.Y + 30f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, tapPt, t, 81));
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, tapPt, t + 16, 81));
            host.RunFrame();
            s_touchClockMs = t + 1000;
            legs++;
        }

        // (2) DOUBLE-TAP: two quick within-slop taps in a selectable EditableText → the DoubleTap team member wins.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("det-dbltap", new Size2(360, 320), 1f)); window.Show();
            var probe = new TouchEditInScrollerProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            host.Input.Arena.Recorder = rec;
            var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.Text);
            var tn = TextVisualNode(host.Scene, field.IsNull ? host.Scene.Root : field);
            var ta = host.Scene.AbsoluteRect(tn.IsNull ? field : tn);
            var p = new Point2(ta.X + 6f, ta.Y + ta.H / 2f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, p, t, 82)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, p, t + 16, 82)); host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerDown, p, t + 80, 82)); host.RunFrame();   // 2nd tap inside the inter-tap window
            window.QueueInput(Touch(InputKind.PointerUp, p, t + 96, 82)); host.RunFrame();
            s_touchClockMs = t + 1000;
            legs++;
        }

        // (3) HOLD: a finger down on a context-request box, held idle past the long-press window → the Hold member is
        // promoted to EagerAccept by the timer tick (TickGestureArenas on the held frames) and wins.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("det-hold", new Size2(280, 220), 1f)); window.Show();
            var probe = new ArenaHoldProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            host.Input.Arena.Recorder = rec;
            var br = host.Scene.AbsoluteRect(host.Scene.Root);
            var p = new Point2(br.X + 40f, br.Y + 40f);
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, p, t, 83));
            host.RunFrame();
            // Hold idle (no move) for 45 fixed 16ms frames (=720ms > the 500ms HoldUs) so the timer promotes the Hold.
            for (int i = 0; i < 45; i++) host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, p, t + 760, 83));
            host.RunFrame();
            s_touchClockMs = t + 2000;
            legs++;
        }

        // (4) DRAG-REORDER-IN-SCROLLER: a horizontal drag of a CanDrag item inside a vertical scroller → DragReorder
        // eager-wins (along the item axis), sweeping the Tap and the Pan.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("det-reorder", new Size2(360, 340), 1f)); window.Show();
            var probe = new ArenaReorderInScrollerProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            host.Input.Arena.Recorder = rec;
            host.Scene.TryGetScroll(FindScrollable(host.Scene, host.Scene.Root), out var sc0);
            var itemA = Child(host.Scene, Child(host.Scene, sc0.ContentNode, 0), 0);
            var aCenter = CenterOf(host.Scene, itemA);
            TouchGesture(window, host, aCenter, new Point2(aCenter.X + 140f, aCenter.Y), 12, pointerId: 84, msPerStep: 16f);
            legs++;
        }

        // (5) SWIPE-IN-SCROLLER: a horizontal swipe of a SwipeControl row inside a vertical scroller → the cross-axis
        // Drag eager-wins (along the row axis), sweeping the Pan.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("det-swipe", new Size2(380, 340), 1f)); window.Show();
            var probe = new SwipeInScrollerProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            host.Input.Arena.Recorder = rec;
            host.Scene.TryGetScroll(FindScrollable(host.Scene, host.Scene.Root), out var sc0);
            var row = Child(host.Scene, sc0.ContentNode, 0);
            var rowCenter = CenterOf(host.Scene, row);
            var swStart = new Point2(rowCenter.X + 120f, rowCenter.Y);
            TouchGesture(window, host, swStart, new Point2(swStart.X - 150f, swStart.Y), 12, pointerId: 85, msPerStep: 16f);
            legs++;
        }

        // (6) PAN+FLING: a vertical flick over a bound virtual list → the Pan member eager-wins (sweeping nothing else
        // on a bare list, but the Pan WIN is recorded); the fling decays downstream of the arena (on ScrollIntegrator).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("det-fling", new Size2(360, 460), 1f)); window.Show();
            var probe = new TouchFlingSettleProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            host.Input.Arena.Recorder = rec;
            TouchFlick(window, host, new Point2(150, 384), new Point2(150, 44), 16, pointerId: 86, msPerStep: 16f, decayFrames: 20);
            legs++;
        }

        return legs;
    }

    static string FlingResolutionTrace(StringTable strings, float dtMs, out float settledOff)
    {
        var fonts = new HeadlessFontSystem(strings);
        var rec = new GestureArenaRecorder();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("det-fling-dt", new Size2(360, 460), 1f)); window.Show();
        var probe = new TouchFlingSettleProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe, frameTime: new FixedFrameTimeSource(dtMs));
        host.RunFrame();
        host.Input.Arena.Recorder = rec;
        var vp = host.Scene.Root;
        // The event stamps (msPerStep:16) are the SAME across the dt sweep — only the animation clock differs. So the
        // velocity sampled at up is identical, the arena resolves identically, and only the post-up decay differs per dt.
        TouchGesture(window, host, new Point2(150, 384), new Point2(150, 44), 16, pointerId: 91, msPerStep: 16f);
        for (int i = 0; i < 200; i++)   // let the fling settle (more frames so the coarse dt also reaches the clamp)
        {
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc);
            if (sc.Phase == 0) break;   // settled (fling ended)
        }
        host.Scene.TryGetScroll(vp, out var settled);
        settledOff = settled.OffsetY;
        return rec.ResolutionSignature();
    }

}
