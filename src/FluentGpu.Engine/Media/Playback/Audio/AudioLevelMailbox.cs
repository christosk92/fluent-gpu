using System;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>Single audio-writer / control-reader level handoff. Readers never wait for an in-progress block.</summary>
internal struct AudioLevelMailbox
{
    private long _version, _bits, _epoch;
    internal void Publish(float rms, float peak, long epoch)
    {
        Interlocked.Increment(ref _version);
        Volatile.Write(ref _bits, (long)((ulong)(uint)BitConverter.SingleToInt32Bits(rms)
            | ((ulong)(uint)BitConverter.SingleToInt32Bits(peak) << 32)));
        Volatile.Write(ref _epoch, epoch);
        Interlocked.Increment(ref _version);
    }
    internal bool TryRead(out float rms, out float peak, out long epoch, out long version)
    {
        version = Volatile.Read(ref _version);
        long bits = Volatile.Read(ref _bits);
        epoch = Volatile.Read(ref _epoch);
        // Full fence keeps payload reads before the validation read, including on ARM64.
        Thread.MemoryBarrier();
        rms = BitConverter.Int32BitsToSingle((int)bits);
        peak = BitConverter.Int32BitsToSingle((int)(bits >> 32));
        return version != 0 && (version & 1) == 0 && version == Volatile.Read(ref _version);
    }
}
