using System.Threading;

namespace FluentGpu.Media;

/// <summary>One atomic decision shared by both voice envelopes of a scheduled transition.</summary>
public sealed class AudioTransitionGate
{
    private int _state; // 0 scheduled, 1 render committed, 2 cancelled
    /// <summary>Create a gate at its scheduled mixer frame.</summary>
    public AudioTransitionGate(long startFrame) => StartFrame = startFrame;
    /// <summary>Scheduled start in the render timeline.</summary>
    public long StartFrame { get; }
    /// <summary>True once rendering has committed the transition.</summary>
    public bool IsCommitted => Volatile.Read(ref _state) == 1;
    /// <summary>True when cancelled before its first rendered frame.</summary>
    public bool IsCancelled => Volatile.Read(ref _state) == 2;
    /// <summary>Cancel atomically. False means the incoming transition already owns the boundary.</summary>
    public bool TryCancel() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
    internal bool TryCommit() => Interlocked.CompareExchange(ref _state, 1, 0) != 2;
}
