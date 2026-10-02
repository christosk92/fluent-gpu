using System;
using System.Numerics;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>
/// The spectrum tap's single-producer / single-consumer MONO sample ring (spec §7.8). The RT render thread
/// <see cref="Write"/>s each rendered block (downmixed L+R/2) under a spectrum lease; the control tick
/// <see cref="TryCopyContentWindow"/>s the latency-aligned window by CONTENT frame (the mixer-domain
/// <c>BlockCtx.StartFrame</c> index space). Unlike <see cref="PcmRing"/> the reader never consumes: it addresses a window
/// ending at any content frame the writer has passed. The write index is MONOTONIC for the ring's life (never reset);
/// <see cref="Arm"/> publishes where the current content domain begins (ring index + content base + an arm epoch), so a
/// re-arm (demand edge, device rebuild, a block discontinuity) never makes an old window look valid — the reader rejects
/// anything before the arm or across an arm. Power-of-two capacity, <see cref="Volatile"/> publish/acquire plus one full
/// fence (the <c>AudioLevelMailbox</c> discipline), zero allocation on either side, no lock, no syscall.
/// </summary>
public sealed class SpectrumRing
{
    private readonly float[] _buf;
    private readonly int _mask, _maxBlock;
    private long _written;      // mono samples written, monotonic; the RT thread owns the write
    private long _armedAt;      // ring index of the first sample after the last Arm
    private long _armedBase;    // that sample's CONTENT frame
    private long _armEpoch;     // bumps per Arm (published last)

    /// <summary>Create a ring of at least <paramref name="minSamples"/> mono samples (rounded up to a power of two).
    /// <paramref name="maxBlock"/> is the largest block the producer writes in one call: the torn-read guard reserves it.</summary>
    public SpectrumRing(int minSamples, int maxBlock)
    {
        uint cap = BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, minSamples));
        _buf = new float[cap];
        _mask = (int)cap - 1;
        _maxBlock = Math.Max(1, maxBlock);
    }

    /// <summary>Capacity in mono samples.</summary>
    public int Capacity => _buf.Length;

    /// <summary>Mono samples written over the ring's life. Safe from either thread.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>The arm count (0 = never armed). Safe from either thread.</summary>
    public long ArmEpoch => Volatile.Read(ref _armEpoch);

    /// <summary>PRODUCER (RT): declare that the NEXT sample written is content frame <paramref name="contentBase"/>. Called on
    /// a demand edge, a render-epoch change and a block discontinuity (<c>TapSpectrumBlock</c>). The fields are written
    /// first and the epoch last; the reader checks the epoch around its whole read.</summary>
    public void Arm(long contentBase)
    {
        _armedAt = _written;
        _armedBase = contentBase;
        Volatile.Write(ref _armEpoch, _armEpoch + 1);
    }

    /// <summary>The newest content frame written (exclusive) in the CURRENT arm's domain — the reader's saturation point for
    /// a negative sync offset. Safe from either thread (a concurrent re-arm is caught by the reader's epoch check).</summary>
    public long NewestContent
    {
        get
        {
            long epoch = Volatile.Read(ref _armEpoch);
            long at = Volatile.Read(ref _armedAt), b = Volatile.Read(ref _armedBase), w = Volatile.Read(ref _written);
            return epoch == 0 ? 0L : b + (w - at);
        }
    }

    /// <summary>PRODUCER (RT): downmix <paramref name="interleaved"/> (<paramref name="channels"/> per frame) to mono and
    /// append. Wraps freely — the reader validates its own window. Alloc-free.</summary>
    public void Write(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 0) return;
        int frames = interleaved.Length / channels;
        if (frames <= 0) return;
        long w = _written;   // only this thread writes it — a plain read is fine
        float[] buf = _buf;
        int mask = _mask;
        if (channels == 2)
        {
            for (int f = 0, i = 0; f < frames; f++, i += 2)
                buf[(int)((w + f) & mask)] = 0.5f * (interleaved[i] + interleaved[i + 1]);
        }
        else
        {
            float inv = 1f / channels;
            for (int f = 0; f < frames; f++)
            {
                float acc = 0f;
                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++) acc += interleaved[baseIdx + c];
                buf[(int)((w + f) & mask)] = acc * inv;
            }
        }
        Volatile.Write(ref _written, w + frames);   // publish
    }

    /// <summary>CONSUMER (control): copy the <c>dst.Length</c> samples ending at content frame <paramref name="contentEndExclusive"/>
    /// (in the current arm's domain). False when the window starts before the arm, is not yet written, is already (or
    /// about to be — one <c>maxBlock</c> of guard) overwritten, was overwritten DURING the copy, or straddles a re-arm —
    /// the caller simply skips this tick. Alloc-free.</summary>
    public bool TryCopyContentWindow(long contentEndExclusive, Span<float> dst)
    {
        int n = dst.Length;
        if (n <= 0 || n > _buf.Length - _maxBlock) return false;
        long epoch = Volatile.Read(ref _armEpoch);
        if (epoch == 0) return false;
        long armedAt = Volatile.Read(ref _armedAt), armedBase = Volatile.Read(ref _armedBase);
        long written = Volatile.Read(ref _written);
        long end = armedAt + (contentEndExclusive - armedBase);     // content → ring index
        long start = end - n;
        if (start < armedAt || end > written) return false;          // before this arm / not yet written
        if (start < written - _buf.Length + _maxBlock) return false; // overwritten, or inside the producer's next block
        float[] buf = _buf;
        int s = (int)(start & _mask);
        int first = Math.Min(n, buf.Length - s);
        buf.AsSpan(s, first).CopyTo(dst);
        if (first < n) buf.AsSpan(0, n - first).CopyTo(dst[first..]);
        Thread.MemoryBarrier();   // payload reads complete before the validation reads (ARM64) — AudioLevelMailbox.cs:24
        long after = Volatile.Read(ref _written);
        return start >= after - _buf.Length + _maxBlock && Volatile.Read(ref _armEpoch) == epoch;
    }
}
