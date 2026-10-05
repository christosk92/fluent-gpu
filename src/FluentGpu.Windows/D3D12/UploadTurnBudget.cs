namespace FluentGpu.Rhi.D3D12;

/// <summary>The image-upload drain's per-turn staging budget as a pure function (F255): a fixed byte count per present turn
/// is twice the bandwidth at 120 Hz that it is at 60 Hz, and on a weak (UMA / iGPU) part those CPU-swizzled writes land on
/// the render thread that also commits the video placement. So the budget is expressed per millisecond of display period.
/// The discrete tier keeps the flat <see cref="D3D12Device.UploadBytesPerTurn"/>. A live video surface is NOT an input: the
/// audit's halving while video played was withdrawn (owner decision, 2026-10-05 — video never lowers what the main window gets).
/// A pure function of its inputs (like <c>GpuMemoryBudgets.For</c> and <c>LayerTargetTrim.Classify</c>) so a unit test can
/// drive both tiers without a GPU, where <c>GpuProfile.IsWeak</c> is always false.</summary>
internal static class UploadTurnBudget
{
    /// <summary>The period the weak-tier rate is quoted against: one 120 Hz refresh.</summary>
    public const double ReferencePeriodMs = 1000.0 / 120.0;

    /// <summary>Bytes the weak tier stages per <see cref="ReferencePeriodMs"/> (so about 8 MB/s per ms of period: 1 MiB at
    /// 120 Hz, 2 MiB at 60 Hz).</summary>
    public const int WeakBytesPerReferencePeriod = 1024 * 1024;

    /// <summary>The weak-tier floor and ceiling per turn: a very fast panel never starves the drain below 256 KiB (a single
    /// job larger than the budget stages alone regardless), and a slow or unknown one never exceeds 4 MiB.</summary>
    public const int WeakMinBytes = 256 * 1024;
    public const int WeakMaxBytes = 4 * 1024 * 1024;

    /// <summary>Pixel bytes one drain turn may stage before it carries the next job over.</summary>
    /// <param name="weak">The GPU tier is known weak.</param>
    /// <param name="displayPeriodMs">The display refresh period in ms; not positive or not finite = unknown (120 Hz assumed).</param>
    public static int BytesPerTurn(bool weak, double displayPeriodMs)
    {
        if (!weak) return D3D12Device.UploadBytesPerTurn;
        double period = displayPeriodMs > 0.0 && double.IsFinite(displayPeriodMs) ? displayPeriodMs : ReferencePeriodMs;
        double bytes = WeakBytesPerReferencePeriod * (period / ReferencePeriodMs);
        return (int)Math.Clamp(bytes, WeakMinBytes, WeakMaxBytes);
    }
}
