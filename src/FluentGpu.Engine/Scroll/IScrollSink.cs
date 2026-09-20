namespace FluentGpu.Scroll;

/// <summary>One POD write — the kernel's ONLY output shape. <see cref="ScrollKernel.Tick"/>/<see cref="ScrollKernel.Reclamp"/>
/// call <see cref="IScrollSink.Apply"/> exactly once per moved/touched body per call (plan §2.2's single-writer
/// invariant — the UI-side sink, <c>SceneScrollSink</c> (WP-B), turns this into the one <c>ApplyMotion</c> token
/// write into <c>ScrollState</c>).</summary>
public readonly record struct ScrollWrite(float OffsetX, float OffsetY, float BandX, float BandY, float Zoom,
    float VelocityMain, float VisualSpeedMain, ScrollActivity Activity, ScrollActivityFlags Flags, ScrollWriteMask Moved,
    float LastReleaseVelocity, ScrollWriteSource Writer /* Tick | Reclamp | Lease */);

/// <summary>The kernel's output port. Implemented by <c>SceneScrollSink</c> (WP-B, UI thread) and by test doubles
/// (<c>FakeSink</c> in <c>ScrollKernelSuite</c>). Never touches <c>SceneStore</c>/<c>NodeHandle</c> from THIS
/// namespace's point of view — the sink is the seam where node indices become real scene nodes.</summary>
public interface IScrollSink
{
    void Apply(int node, in ScrollWrite w);
}

/// <summary>Whole-kernel per-tick rollup, cheap to read every frame (e.g. to decide whether to suppress layout
/// transitions, or whether the wake reason <c>ScrollAnim</c> should stay armed). <see cref="AnyLiveMotion"/> is true
/// while any body is in continuous motion the host must budget a frame for — a Drag, a Ballistic fling, or a Driven
/// glide with the <c>Wheel</c> or <c>Programmatic</c> flavour (<c>AppHost.Paint</c> arms its frame budget on it, so a
/// wheel glide gets the same bounded realize deadline a fling does).</summary>
/// <param name="MaxAbsDeltaDip">The largest main-axis displacement any body produced this tick (DIP). Zero on a tick
/// that moved nothing; with <paramref name="AnyLiveMotion"/> true it is the per-frame "shift" a screen-capture probe
/// would measure, which is why the host surfaces it on <c>FrameStats</c>.</param>
/// <param name="WheelNotches">Wheel notches applied since the previous tick (a cadence marker for the same trace).</param>
/// <param name="EdgePins">Bodies whose Ballistic step this tick was pinned at a clamp (<c>ScrollBody.EdgeHitPending</c>),
/// awaiting <c>Reclamp</c>'s resolution against fresh geometry. Non-zero marks the frame whose displacement was cut short
/// by last frame's extent, not by the physics.</param>
/// <param name="MaxAbsStructuralDip">The largest main-axis rebase any body took this tick that is NOT motion (DIP): an
/// <c>AnchorShift</c> or a clamp correction after <c>SetFrame</c>. Excluded from <paramref name="MaxAbsDeltaDip"/> so a
/// virtualizer rebase after a window restore does not print as a scroll step.</param>
/// <param name="AnyContactHeld">A Drag body applied no delta this tick and its newest contact sample is older than the
/// resample latency plus one frame — the contact is down and still. Touch/pen (DragMode 1) can hold indefinitely; a
/// precise stream (DragMode 2) can only be held inside the <c>DragExtrapolateMaxMs</c> window before its release is
/// inferred, so for it this is true for at most a frame or so.</param>
/// <param name="ZeroReason">Why a body that should have coasted this tick (Ballistic at or above <c>FlingLandVel</c>, or
/// a fling landing) moved less than 0.05 DIP — the instrument for the zero frames mid-coast that carry no pin, hitch or
/// structural marker. <see cref="ScrollZeroReason.None"/> when every coasting body moved.</param>
public readonly record struct ScrollFrameSummary(bool AnyMoved, bool AnyUserActive, bool AnyLiveMotion, int ActiveCount, float MaxVisualSpeed,
                                                 float MaxAbsDeltaDip = 0f, int WheelNotches = 0, int EdgePins = 0,
                                                 float MaxAbsStructuralDip = 0f, bool AnyContactHeld = false,
                                                 ScrollZeroReason ZeroReason = ScrollZeroReason.None);

/// <summary>Why a coasting body produced a zero frame (<see cref="ScrollFrameSummary.ZeroReason"/>).</summary>
public enum ScrollZeroReason : byte
{
    None = 0,
    /// <summary>Advanced with <c>ScrollClock.DtSec</c> ≤ 0: the host handed the kernel no time this frame (an awake body
    /// treats dt = 0 as a no-op by contract), so the frame's travel is lost — the physics is per-frame dt.</summary>
    DtZero = 1,
    /// <summary>The body was active but <c>ScrollBody.Advance</c> did not run on it this tick.</summary>
    NotAdvanced = 2,
    /// <summary>Pinned at a clamp against last frame's geometry (<c>ScrollBody.EdgeHitPending</c>).</summary>
    Pinned = 3,
    /// <summary>Parked mid-coast (never ticked).</summary>
    Parked = 4,
    /// <summary>Advanced with a positive dt and still moved under 0.05 DIP.</summary>
    Other = 5,
}

/// <summary>Pillar-A sensor fields (plan §2.3), filled only when <c>ScrollTrace.CompiledIn &amp;&amp; ScrollTrace.Enabled</c>
/// — WP-A fills this struct every tick; WP-F wires it into the actual <c>ScrollTrace</c> rows (the kernel does NOT
/// call into <c>FluentGpu.Foundation.ScrollTrace</c> itself yet, per the WP-A task brief).</summary>
public struct ScrollKernelDiag
{
    public bool TrackingLagSampled;
    public float TrackingLagDip;
    public float TrackingVelocityDipPerMs;
    public double LastContactSampleSec;
    public byte GestureWord;
}
