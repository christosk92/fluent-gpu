namespace FluentGpu.Scroll;

/// <summary>ONE target time shared by DirectManipulation, the kernel, and (later) the render-thread lease (plan
/// §5.1's <c>FrameClock</c> is the Windows-producer-facing lattice-snapped source; this is its portable/derived
/// projection — the shape <see cref="ScrollKernel.Tick"/> actually consumes). <see cref="FrameSec"/>/<see cref="DtSec"/>
/// are the physics clock (seconds, monotone); <see cref="PresentSec"/> is the predicted vblank this frame's pixels
/// land on (used by <see cref="ScrollPhysics.ResampleContact"/>'s caller to pick <c>tStar = FrameSec − latency</c>,
/// and by the future lease to self-tick between UI frames); <see cref="RefreshSec"/> is the display's frame period —
/// substituted for <see cref="DtSec"/> on the first tick after a body wakes (kills the zero-dt dead zone, §2.2).</summary>
public readonly record struct ScrollClock(double FrameSec, float DtSec, double PresentSec, float RefreshSec);
