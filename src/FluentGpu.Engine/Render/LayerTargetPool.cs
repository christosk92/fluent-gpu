using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluentGpu.Render;

/// <summary>
/// Size bucket for the surface pool's transient and retained targets (tile surfaces cut at their painted bounds, group /
/// self-blur / acrylic-backdrop scratch — gpu-renderer.md §13).
///
/// Those boxes cluster in the few-hundred-pixel range, where next-power-of-two wastes up to 4x area — and on a UMA
/// adapter every wasted texel is pinned host memory. So this bucket is LINEAR in 64-px steps up to
/// <see cref="LinearCeiling"/> and only then falls back to powers of two:
///
///   px  &lt;= 2048 : ceil to a multiple of 64  (64, 128, 192, ... 2048)
///   px  &gt;  2048 : next power of two          (4096, 8192, ...)        — few very large buckets
///
/// The ceiling is 2048 rather than something small because the po2 step that matters most is the one that straddles a
/// typical window WIDTH: at 1195 px the po2 ladder jumps to 2048 — a 1.7x waste on that axis alone. Linear to 2048
/// makes that same band 1216 px wide.
///
/// Reuse is preserved despite the finer granularity because every scratch lease is BEST-FIT >= the bucket (the SMALLEST
/// free slot that fits), so a finer ladder shrinks the surface a COLD lease creates without fragmenting the WARM free
/// list.
/// </summary>
public static class LayerTargetBucket
{
    /// <summary>Smallest bucket (also the step below <see cref="LinearCeiling"/>).</summary>
    public const int MinDim = 64;

    /// <summary>The linear step used at or below <see cref="LinearCeiling"/>.</summary>
    public const int LinearStep = 64;

    /// <summary>Above this the ladder switches to powers of two. Set past a typical window dimension on purpose — see
    /// the class remarks on why a po2 step at 1195 -&gt; 2048 cancelled the saving for a full-width band.</summary>
    public const int LinearCeiling = 2048;

    /// <summary>Bucket one dimension. Monotone non-decreasing, always &gt;= <paramref name="px"/>, always &gt;=
    /// <see cref="MinDim"/>.</summary>
    public static int Dim(int px)
    {
        if (px <= MinDim) return MinDim;
        if (px <= LinearCeiling) return (px + (LinearStep - 1)) / LinearStep * LinearStep;
        int b = LinearCeiling;
        while (b < px) b <<= 1;
        return b;
    }

    /// <summary>Bytes a B8G8R8A8 target of these dimensions occupies (the census/budget unit).</summary>
    public static long Bytes(int w, int h) => (long)Math.Max(0, w) * Math.Max(0, h) * 4L;
}

/// <summary>What <see cref="LayerTargetTrim.Classify"/> decided for one pool slot on a fenced frame boundary.</summary>
public enum LayerTrimVerdict
{
    /// <summary>Leave the slot's resource live.</summary>
    Keep = 0,
    /// <summary>Retire it — move the resource to the fence-gated deferred-release queue.</summary>
    Retire = 1,
}

/// <summary>
/// The surface pool's IDLE-TRIM policy, extracted from the D3D12 leaf (<c>SurfacePool</c>) so it is one source of truth
/// for the leaf and the headless VerticalSlice gates (<c>gate.layerpool.*</c>).
///
/// <para><b>The problem it solves.</b> Scratch slots are created LAZILY on lease. On a UMA/iGPU adapter every GPU
/// allocation is pinned into resident write-combine segments, so a scratch target nothing has leased for seconds is
/// working set for nothing. A free slot idle past its tier's window (counted in SUBMITTED frames — a slot's idle count is
/// bumped once per frame it was not leased) is retired.</para>
///
/// <para><b>The fence rule is not part of this policy and must not be re-litigated here.</b> "Retire" means
/// <i>move the resource to the deferred-release queue</i>, never <i>Release() it now</i>. The actual release is gated
/// on <see cref="CanRelease"/> (the frame fence has passed the slot's last recorded use), which is the
/// threading-render-seam deferred-reclaim convention the whole renderer uses. Trimming therefore cannot free a
/// resource a submit in flight still references, no matter what <see cref="Classify"/> returns.</para>
/// </summary>
public static class LayerTargetTrim
{
    /// <summary>Trim window on a WEAK (UMA / iGPU / WARP) adapter: 120 submitted frames — ~1 s at 120 Hz, ~2 s at 60 Hz.
    /// Short because every byte here is resident host memory on such an adapter.</summary>
    public const int IdleFramesWeak = 120;

    /// <summary>Trim window on a discrete adapter: 600 submitted frames (~10 s). Dedicated VRAM makes an idle pooled
    /// target far cheaper, and re-creating one costs a real driver allocation, so the window stays generous.</summary>
    public const int IdleFramesStrong = 600;

    /// <summary>The trim window for the running adapter tier.</summary>
    public static int IdleFrames(bool weak) => weak ? IdleFramesWeak : IdleFramesStrong;

    /// <summary>May a retired resource be Released now? The frame fence must have passed its last recorded use — the
    /// deferred-reclaim convention (threading-render-seam.md). This is the ONLY gate on the actual release; the
    /// trim decision above never releases anything by itself.</summary>
    public static bool CanRelease(ulong lastUseFence, ulong completedFence) => lastUseFence <= completedFence;

    /// <summary>Trim verdict for ONE pool slot, evaluated on a fenced frame boundary.</summary>
    /// <param name="inUse">Leased by a pass still open this frame — always <see cref="LayerTrimVerdict.Keep"/>.</param>
    /// <param name="idleFrames">Consecutive submitted frames without a lease.</param>
    /// <param name="weak"><see cref="FluentGpu.Foundation.GpuProfile.IsWeak"/>.</param>
    public static LayerTrimVerdict Classify(bool inUse, int idleFrames, bool weak)
        => !inUse && idleFrames > IdleFrames(weak) ? LayerTrimVerdict.Retire : LayerTrimVerdict.Keep;
}

/// <summary>
/// POOLED-vs-IN-USE byte accounting for one compositor's surface pool — the honest half of the <c>gpu bytes</c> census.
/// <c>gpu bytes</c> is a single tracked-resource total, so a pool holding four idle scratch targets is indistinguishable
/// from one actively compositing four groups; on a UMA adapter, where all of it is resident host memory, that is the
/// difference between a working-set bug and a working-set cost. Every field is a plain sum, so building this is a
/// fixed-bucket walk with no allocation (the string form is only rendered by the census sampler's cadence).
/// </summary>
public readonly record struct LayerTargetCensus(
    long InUseBytes, int InUseCount,
    long FreeBytes, int FreeCount,
    long RetainedBytes, int RetainedCount,
    long RetiredBytes, int RetiredCount)
{
    /// <summary>Everything this pool currently holds, whether leased, idle, retained or awaiting its fence.</summary>
    public long TotalBytes => InUseBytes + FreeBytes + RetainedBytes + RetiredBytes;

    /// <summary>Slots holding a resource (retired entries are no longer slots, so they are not counted here).</summary>
    public int LiveCount => InUseCount + FreeCount + RetainedCount;

    /// <summary>Sum two pools' censuses (the device reports the surface pool + baked-blur as one line).</summary>
    public static LayerTargetCensus operator +(LayerTargetCensus a, LayerTargetCensus b) => new(
        a.InUseBytes + b.InUseBytes, a.InUseCount + b.InUseCount,
        a.FreeBytes + b.FreeBytes, a.FreeCount + b.FreeCount,
        a.RetainedBytes + b.RetainedBytes, a.RetainedCount + b.RetainedCount,
        a.RetiredBytes + b.RetiredBytes, a.RetiredCount + b.RetiredCount);

    /// <summary>Accumulate one slot. <paramref name="bytes"/> is the surface size, not the used sub-rect: the driver
    /// pins the whole surface, so the census must report the whole surface.</summary>
    public LayerTargetCensus WithSlot(long bytes, bool inUse, bool retained) => retained
        ? this with { RetainedBytes = RetainedBytes + bytes, RetainedCount = RetainedCount + 1 }
        : inUse
            ? this with { InUseBytes = InUseBytes + bytes, InUseCount = InUseCount + 1 }
            : this with { FreeBytes = FreeBytes + bytes, FreeCount = FreeCount + 1 };

    /// <summary>Accumulate one entry still on the fence-gated release queue.</summary>
    public LayerTargetCensus WithRetired(long bytes)
        => this with { RetiredBytes = RetiredBytes + bytes, RetiredCount = RetiredCount + 1 };

    /// <summary>Accumulate a whole release queue at once from its running totals (a queue owned by another thread is
    /// never enumerated by the census).</summary>
    public LayerTargetCensus WithRetiredTotal(long bytes, int count)
        => this with { RetiredBytes = RetiredBytes + bytes, RetiredCount = RetiredCount + count };

    /// <summary>One compact census token set — <c>inuse=3.5/1 free=7.0/2 retained=0.4/6 retire=0.0/0</c> in MiB (retained =
    /// tiles and retained self-blur / acrylic results) — for the
    /// <c>gpu</c> census line. Allocates a string, so it belongs on the census sampler's cadence, never per frame.</summary>
    public string ToDetail() =>
        $"inuse={Mib(InUseBytes)}/{InUseCount} free={Mib(FreeBytes)}/{FreeCount}" +
        $" retained={Mib(RetainedBytes)}/{RetainedCount} retire={Mib(RetiredBytes)}/{RetiredCount}";

    private static string Mib(long bytes)
        => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
