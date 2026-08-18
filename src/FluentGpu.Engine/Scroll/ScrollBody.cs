using System;

namespace FluentGpu.Scroll;

/// <summary>The POD per-viewport body. Blittable — no managed references except the shared immutable
/// <c>Frame.SnapPoints</c> array (never mutated here, only ever replaced wholesale by a fresh <c>SetFrame</c>).
/// <see cref="ScrollKernel"/> keeps a growable slab of these indexed by scene node index; <see cref="Advance"/> is
/// the pure per-body time step (Ballistic/Driven/Bounce only — Drag is command-driven, not time-stepped) reused by
/// both <see cref="ScrollKernel.Tick"/> and, later, the render-thread fling lease (plan §6.1 pins exactly this
/// method + <see cref="IsSettled"/> for that hand-off).</summary>
public struct ScrollBody
{
    public int Node;
    public bool Bound;

    // Absolute offsets + overscroll bands, both axes (cross axis is normally pinned by layout, but nothing here
    // assumes that — chaining/AnchorShift can touch either).
    public float OffsetX, OffsetY;
    public float BandX, BandY;
    /// <summary>Band spring velocity — main axis only (Bounce activity); cross-axis overscroll doesn't spring.</summary>
    public float BandVelMain;
    /// <summary>Signed main-axis velocity (DIP/s) — Ballistic coast speed, Driven chase velocity, or the constant
    /// Autoscroll drive speed.</summary>
    public float Velocity;
    /// <summary>Main-axis Driven target (ScrollTo/ScrollBy/ThumbSet-as-Immediate/WheelNotch accumulator).</summary>
    public float Target;
    public float Zoom;

    public ScrollActivity Activity;
    public ScrollActivityFlags Flags;

    /// <summary>Last geometry/config this body received via <see cref="ScrollInputKind.SetFrame"/>.</summary>
    public ScrollFrameSpec Frame;

    /// <summary>Nearest same-axis ancestor viewport set by the router at ContactBegin (drag-time chaining, §2.2).
    /// -1 = none.</summary>
    public int ChainParent;
    /// <summary>The node (self or a chained child) that absorbed the last non-zero delta before lift — the fling on
    /// release seeds THIS body. -1 = none (defaults to self).</summary>
    public int LastAbsorbed;

    /// <summary>Drag anchor: the main-axis offset at ContactBegin/AnchorShift-rebase, against which resampled
    /// contact displacement is applied (<c>clamped = clamp(anchor + (x* − x0))</c>).</summary>
    public float DragAnchor;
    /// <summary>The main-axis position at ContactBegin (x0 in the anchor formula above).</summary>
    public float DragOrigin;
    /// <summary>Raw (unclamped) accumulated main-axis position during a drag — the anchor-relative resampled
    /// position for a touch/pen drag, or the 1:1 delta accumulator for a FrameDelta drag; also the running total a
    /// chained hand-off adds/removes excess from.</summary>
    public float DragRaw;
    /// <summary>0 = not dragging; 1 = touch/pen (<see cref="ScrollInputKind.ContactMove"/>, resampled every Tick);
    /// 2 = FrameDelta (DM RUNNING / hi-res fallback, applied 1:1 as each command arrives, no resampling).</summary>
    public byte DragMode;
    /// <summary>The last RAW resampled contact position (DragMode 1 only) — decoupled from <see cref="DragRaw"/> on
    /// purpose: <see cref="DragRaw"/> is rebased by <see cref="ScrollInputKind.AnchorShift"/> and reshaped by chain
    /// hand-off/clamping, but the per-tick resample-to-delta comparison (<c>delta = resample(t) − LastResampleX</c>)
    /// must track the finger's own continuous raw trajectory regardless of either — otherwise an AnchorShift mid-drag
    /// silently eats that much of the NEXT tick's real finger motion.</summary>
    public float LastResampleX;

    // Contact sample history — up to 5, chronological (T0/X0 oldest .. T4/X4 newest within ContactCount).
    public double T0, T1, T2, T3, T4;
    public float X0, X1, X2, X3, X4;
    public int ContactCount;

    public ScrollPhysics.ImpulseEstimator Impulse;
    /// <summary>The release velocity computed at this body's most recent <see cref="ScrollInputKind.ContactEnd"/> —
    /// distinct from <see cref="ScrollPhysics.ImpulseEstimator.Velocity"/> (which keeps changing as new samples
    /// arrive on a LATER drag); reported to the sink as <c>ScrollWrite.LastReleaseVelocity</c>.</summary>
    public float LastReleaseVelocity;

    // Driven params (0/0/0/0 = use the profile default: ζ=1 chase at feel.WheelHalflifeMs).
    public float DrivenHalflifeMs, DrivenZeta, DrivenOmega, DrivenSettleVel;

    /// <summary>Set by <c>ScrollKernel.SnapRetargetOnEntry</c> exactly when a fling was retargeted onto a snap grid
    /// (fresh at every Ballistic seed — never carries a stale value from an earlier, non-snap fling). While set,
    /// <see cref="Advance"/>'s Ballistic branch terminates on distance-to-<see cref="Target"/> rather than on the
    /// velocity floor alone, so the fling lands EXACTLY on the grid instead of wherever the natural exponential
    /// decay happened to cross the settle threshold (<see cref="ScrollPhysics.SnapLandEpsPx"/>).</summary>
    public bool SnapArmed;

    public float RestoreX, RestoreY;
    public bool RestorePending;

    /// <summary>Set when a Ballistic step this tick landed past the clamp against LAST frame's geometry — resolved
    /// in <see cref="ScrollKernel.Reclamp"/> once fresh geometry is known (plan §2.2 "hole 1").</summary>
    public bool EdgeHitPending;
    public bool Parked;

    /// <summary>Render-thread fling-lease sequence tag — reserved for Phase 6 (<c>TryLease</c>/<c>Return</c>); unused
    /// by the kernel itself in Phase 1.</summary>
    public uint LeaseSeq;

    /// <summary>True once this body has been ticked at least once since its most recent Idle→non-Idle transition —
    /// drives the "first tick after wake uses RefreshSec, never 0" rule in <see cref="Advance"/>.</summary>
    public bool Awake;

    public readonly bool Horizontal => Frame.Orientation != 0;
    /// <summary>Main-axis position (the axis <see cref="Frame"/> describes).</summary>
    public readonly float PositionMain => Horizontal ? OffsetX : OffsetY;
    /// <summary>Main-axis signed velocity.</summary>
    public readonly float VelocityMain => Velocity;
    /// <summary>Main-axis band.</summary>
    public readonly float BandMain => Horizontal ? BandX : BandY;

    /// <summary>Idle, no live band, no residual velocity — safe for a lease to hand back / for a body to be culled
    /// from the active list.</summary>
    public readonly bool IsSettled => Activity == ScrollActivity.Idle && BandX == 0f && BandY == 0f && BandVelMain == 0f && Velocity == 0f;

    private static void SetMain(ref ScrollBody b, float v) { if (b.Horizontal) b.OffsetX = v; else b.OffsetY = v; }
    private static void SetBandMain(ref ScrollBody b, float v) { if (b.Horizontal) b.BandX = v; else b.BandY = v; }

    /// <summary>Pure per-body time step — Ballistic (coast + edge → Bounce hand-off), Driven (chase to
    /// <see cref="Target"/>, hard-stop at extents for Wheel/Programmatic, off += v·dt for Autoscroll), Bounce
    /// (spring the band home). Idle/Drag are no-ops here (Drag is entirely command-driven — see
    /// <see cref="ScrollKernel"/>). dt is clamped to 34ms. On this body's first Advance since waking, a GENUINELY
    /// invalid (&lt;= 0) dt substitutes <paramref name="c"/>.RefreshSec (kills the zero-dt dead zone of the deleted
    /// ScrollIntegrator.cs:349 — a body must not sit at 0 progress forever because its very first sample landed on a
    /// zero-delta frame); an ALREADY-valid wake-time dt (a dt-sweep test's own clock, e.g. an 8.33ms
    /// <c>FixedFrameTimeSource</c>) is used AS-IS — substituting the assumed 60Hz RefreshSec there regardless made the
    /// sweep's very first tick silently use ~16.67ms no matter what dt was under test, breaking
    /// gate.snap.page-glide-dt-invariant. Once awake, an explicit dt=0 tick (a caller probing "no advance this tick",
    /// e.g. gate.kernel.programmatic-glide-retarget) stays a TRUE no-op — never substituted.</summary>
    public static void Advance(ref ScrollBody b, in ScrollClock c, in ScrollFeel feel)
    {
        float dt = Math.Clamp(c.DtSec, 0f, 0.034f);
        if (!b.Awake)
        {
            if (dt <= 0f && c.RefreshSec > 0f) dt = c.RefreshSec;
            b.Awake = true;
        }
        if (dt <= 0f) return;

        float viewport = b.Frame.ViewportMain;
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - viewport);

        switch (b.Activity)
        {
            case ScrollActivity.Ballistic:
            {
                float v0 = b.Velocity;
                float v = v0;
                float dpos = ScrollPhysics.CoastStep(ref v, dt, feel.FlingDecayPerS);
                float cur = b.PositionMain;
                float requested = cur + dpos;

                // Snap-armed fling: terminate on distance-to-Target, not on the velocity floor alone (the
                // exponential coast's natural rest is only reached at t=∞ — ending purely on FlingSettleVel leaves a
                // permanent v/k gap off the grid; ScrollPhysics.SnapLandEpsPx's doc has the full rationale).
                if (b.SnapArmed)
                {
                    float target = b.Target;
                    bool reached = MathF.Abs(target - cur) <= ScrollPhysics.SnapLandEpsPx
                        || (v0 >= 0f ? requested >= target - ScrollPhysics.SnapLandEpsPx : requested <= target + ScrollPhysics.SnapLandEpsPx)
                        || MathF.Abs(v) < feel.FlingSettleVel;
                    if (reached)
                    {
                        SetMain(ref b, Math.Clamp(target, 0f, maxOff));   // land exactly (clamp-safe)
                        b.Velocity = 0f;
                        b.Activity = ScrollActivity.Idle;
                        b.SnapArmed = false;
                        b.Awake = false;
                        break;
                    }
                }

                bool hitClamp = requested < 0f || requested > maxOff;
                float clamped = Math.Clamp(requested, 0f, maxOff);
                SetMain(ref b, clamped);
                b.Velocity = v;

                if (hitClamp)
                {
                    // Pin at the clamp, keep velocity/Activity as-is (paused, not decided) — the plan's "hole 1":
                    // resolution (continue if fresh geometry gives room / hand to the chain parent / Bounce / Idle)
                    // happens in ScrollKernel.Reclamp, AFTER layout, against possibly-fresher Frame geometry. A
                    // standalone Advance caller (the future render lease, which never sees layout growth) resolves
                    // immediately instead — see ScrollKernel.ResolveEdge, which Advance callers may invoke inline.
                    b.EdgeHitPending = true;
                }
                else if (MathF.Abs(v) < feel.FlingSettleVel)
                {
                    b.Activity = ScrollActivity.Idle;
                    b.Velocity = 0f;
                    b.Awake = false;
                }
                break;
            }

            case ScrollActivity.Driven:
            {
                float off = b.PositionMain;
                float vel = b.Velocity;
                bool autoscroll = (b.Flags & ScrollActivityFlags.Autoscroll) != 0;
                bool settled;
                if (autoscroll)
                {
                    off += vel * dt;
                    settled = vel == 0f;
                }
                else
                {
                    if (b.DrivenZeta > 0f && b.DrivenZeta < 0.999f && b.DrivenOmega > 0f)
                        ScrollPhysics.ChaseStepUnderdamped(ref off, ref vel, b.Target, b.DrivenZeta, b.DrivenOmega, dt);
                    else
                    {
                        float halflife = b.DrivenHalflifeMs > 0f ? b.DrivenHalflifeMs : feel.WheelHalflifeMs;
                        ScrollPhysics.ChaseStep(ref off, ref vel, b.Target, halflife, dt);
                    }
                    float settleVel = b.DrivenSettleVel > 0f ? b.DrivenSettleVel : feel.FlingSettleVel;
                    settled = MathF.Abs(off - b.Target) < 0.5f && MathF.Abs(vel) < settleVel;
                    if (settled) { off = b.Target; vel = 0f; }
                }

                bool hardStop = (b.Flags & (ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic)) != 0;
                if (hardStop)
                {
                    if (off < 0f) { off = 0f; vel = 0f; settled = true; }
                    else if (off > maxOff) { off = maxOff; vel = 0f; settled = true; }
                }

                SetMain(ref b, off);
                b.Velocity = vel;
                if (settled)
                {
                    b.Activity = ScrollActivity.Idle;
                    b.Flags &= ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll);
                    b.Awake = false;
                }
                break;
            }

            case ScrollActivity.Idle when (b.Flags & ScrollActivityFlags.Bouncing) != 0:
            {
                // "Bounce" is NOT a fifth ScrollActivity value (§2.1 pins exactly Idle/Drag/Ballistic/Driven —
                // overscroll is a property, Band ≠ 0, not a state): a released band spring settling with no live
                // contact is Activity=Idle with Flags.Bouncing set.
                float bandv = b.BandVelMain;
                float bandPos = b.BandMain;
                bool settled = ScrollPhysics.StepSpring(ref bandPos, ref bandv, dt, feel.SnapBackOmega, 0f);
                SetBandMain(ref b, bandPos);
                b.BandVelMain = bandv;
                if (settled)
                {
                    b.Flags &= ~ScrollActivityFlags.Bouncing;
                    b.Awake = false;
                }
                break;
            }

            case ScrollActivity.Idle:
            case ScrollActivity.Drag:
            default:
                b.Awake = b.Activity != ScrollActivity.Idle; // Drag stays "awake" (no RefreshSec substitution mid-drag)
                break;
        }
    }
}
