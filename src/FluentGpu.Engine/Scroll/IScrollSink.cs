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
/// transitions, or whether the wake reason <c>ScrollAnim</c> should stay armed).</summary>
public readonly record struct ScrollFrameSummary(bool AnyMoved, bool AnyUserActive, bool AnyDragOrBallistic, int ActiveCount, float MaxVisualSpeed);

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
