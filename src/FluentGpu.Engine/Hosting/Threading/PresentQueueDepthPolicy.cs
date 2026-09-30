using System;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// The present-queue depth (DXGI maximum frame latency), chosen from MEASURED GPU margin instead of fixed.
///
/// <para><b>The trade-off.</b> Depth 1 is the lowest latency the swapchain offers: a frame is produced only once the
/// previous one has left the queue, so what reaches the glass is at most one refresh old. Its cost is throughput: the
/// CPU cannot record frame N+1 while the GPU is still executing frame N, so a frame whose GPU execution approaches or
/// exceeds a refresh period misses its vblank and the NEXT present waits a whole extra refresh — a GPU at 1.05 refreshes
/// per frame presents at ~half rate, not at 1/1.05 of it. Depth 2 lets one frame queue behind the one executing, which
/// turns that cliff into its true cost (GPU-bound frames present at 1/gpuTime) for exactly one extra refresh of latency.
/// So: depth 1 while the GPU has margin, depth 2 only while it does not.</para>
///
/// <para><b>The rule.</b> Fresh whole-frame GPU execution samples feed an EMA. Depth 2 engages when the EMA reaches
/// <see cref="EngageFraction"/> of the refresh period for <see cref="ConfirmSamples"/> consecutive fresh samples (a single
/// spike — a texture upload, a theme switch — never buys a refresh of latency); depth 1 returns when the EMA falls below
/// <see cref="ReleaseFraction"/> for the same run (hysteresis: a frame hovering at the threshold does not flip latency
/// every few frames). The present-time prediction follows the depth actually in force
/// (<c>presentQpc = tick + (1 + depth)·refresh</c>, <c>RefreshLattice</c>): a deeper queue shows each frame one refresh
/// later, and the poses must be evaluated for that vblank.</para>
///
/// <para>Pure and allocation-free: the host calls <see cref="Observe"/> on the render thread after each present and applies
/// a changed <see cref="Depth"/> to the device.</para>
/// </summary>
public struct PresentQueueDepthPolicy
{
    public const int Shallow = 1, Deep = 2;
    /// <summary>EMA fraction of the refresh period at/above which depth 2 engages (0.8: under 20 % margin a normal
    /// frame-to-frame variation already pushes frames past the vblank).</summary>
    public const double EngageFraction = 0.8;
    /// <summary>EMA fraction below which depth 1 returns.</summary>
    public const double ReleaseFraction = 0.6;
    /// <summary>Consecutive fresh samples a verdict must hold before the depth changes.</summary>
    public const int ConfirmSamples = 8;
    private const double Alpha = 0.2;

    private int _depth;
    private double _ema;
    private int _run;
    private ulong _lastSequence;

    /// <summary>The depth currently chosen (1 before any sample).</summary>
    public readonly int Depth => _depth == 0 ? Shallow : _depth;

    /// <summary>The smoothed GPU execution (ms) the decision is made on.</summary>
    public readonly double EmaMs => _ema;

    /// <summary>Folds one whole-frame GPU execution sample. <paramref name="sequence"/> identifies the sample (a repeat is
    /// ignored — the same retired frame read twice is one observation). Returns true when <see cref="Depth"/> changed.</summary>
    public bool Observe(double gpuMs, ulong sequence, double refreshMs)
    {
        if (_depth == 0) _depth = Shallow;
        if (sequence == 0 || sequence == _lastSequence || !(gpuMs > 0.0) || !(refreshMs > 0.0)) return false;
        _lastSequence = sequence;
        _ema = _ema == 0.0 ? gpuMs : _ema + Alpha * (gpuMs - _ema);
        int want = _depth == Shallow
            ? (_ema >= EngageFraction * refreshMs ? Deep : Shallow)
            : (_ema < ReleaseFraction * refreshMs ? Shallow : Deep);
        if (want == _depth) { _run = 0; return false; }
        if (++_run < ConfirmSamples) return false;
        _run = 0;
        _depth = want;
        return true;
    }
}
