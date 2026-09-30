using System.Threading;

namespace FluentGpu.Rhi;

/// <summary>
/// The ONE per-render-turn upload budget (docs/plans/scroll-gpu-retained-tiles-implementation.md §C): how many pixel
/// bytes of decoded images one turn may hand to the copy queue. Default 8 MiB. It is permanent and LIVE-TUNABLE
/// (Diagnostics page / probe; never an environment variable) and replaces the per-frame apply caps and every
/// scroll-keyed variant when P2 wires the copy-queue path. Writers: the UI thread; readers: the render thread, once per
/// turn (<see cref="UploadTurnMeter.Begin"/>).
/// </summary>
public static class UploadBudget
{
    public const long DefaultBytesPerTurn = 8L * 1024 * 1024;
    /// <summary>The smallest accepted budget (a single small thumbnail per turn still progresses).</summary>
    public const long MinBytesPerTurn = 64L * 1024;
    /// <summary>The largest accepted budget.</summary>
    public const long MaxBytesPerTurn = 256L * 1024 * 1024;

    private static long s_bytesPerTurn = DefaultBytesPerTurn;
    private static int s_version;

    /// <summary>The live per-turn budget in bytes, clamped to [<see cref="MinBytesPerTurn"/>, <see cref="MaxBytesPerTurn"/>].</summary>
    public static long BytesPerTurn
    {
        get => Volatile.Read(ref s_bytesPerTurn);
        set
        {
            Volatile.Write(ref s_bytesPerTurn, Math.Clamp(value, MinBytesPerTurn, MaxBytesPerTurn));
            Interlocked.Increment(ref s_version);
        }
    }

    /// <summary>Bumped on every write.</summary>
    public static uint Version => (uint)Volatile.Read(ref s_version);

    public static void ResetToDefault() => BytesPerTurn = DefaultBytesPerTurn;
}

/// <summary>
/// Meters one render turn's uploads against a budget. The first job of a turn is ALWAYS admitted, even when it alone
/// exceeds the budget (a large cover never starves); later jobs are admitted only while they fit. Everything not
/// admitted carries to the next turn and is counted. A POD value — zero allocation.
/// </summary>
public struct UploadTurnMeter
{
    private long _budget, _spent, _deferredBytes;
    private int _admitted, _deferred;

    /// <summary>Open a turn with <paramref name="budgetBytes"/> (normally <see cref="UploadBudget.BytesPerTurn"/>).</summary>
    public void Begin(long budgetBytes)
    {
        _budget = budgetBytes > 0 ? budgetBytes : 0;
        _spent = 0; _admitted = 0; _deferred = 0; _deferredBytes = 0;
    }

    /// <summary>Admit a job of <paramref name="bytes"/>, or defer it to the next turn.</summary>
    public bool TryAdmit(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (_admitted == 0 || _spent + bytes <= _budget)
        {
            _spent += bytes;
            _admitted++;
            return true;
        }
        _deferred++;
        _deferredBytes += bytes;
        return false;
    }

    public readonly long Budget => _budget;
    public readonly long SpentBytes => _spent;
    public readonly long RemainingBytes => _spent >= _budget ? 0 : _budget - _spent;
    public readonly int Admitted => _admitted;
    public readonly int Deferred => _deferred;
    public readonly long DeferredBytes => _deferredBytes;
}

/// <summary>Where an uploaded image stands relative to the copy-queue fence.</summary>
public enum UploadReadiness : byte
{
    /// <summary>No upload has been staged (image fence 0).</summary>
    NotStaged,
    /// <summary>Staged on the copy queue; its fence has not completed — the scene draws the placeholder.</summary>
    InFlight,
    /// <summary>The copy completed: the texture may be sampled.</summary>
    Resident,
}

/// <summary>
/// The pure fence-readiness rule (§C): an image is resident to the scene reader ONLY when
/// <c>completedUploadFence ≥ image.UploadFence</c> — a compare, never a wait. Until then the draw shows the placeholder
/// at final size and cross-fades on arrival (no layout pop). Fence value 0 is reserved for "never staged".
/// </summary>
public static class UploadFencePolicy
{
    public static UploadReadiness Classify(ulong completedUploadFence, ulong imageUploadFence)
        => imageUploadFence == 0 ? UploadReadiness.NotStaged
         : completedUploadFence >= imageUploadFence ? UploadReadiness.Resident
         : UploadReadiness.InFlight;

    public static bool IsResident(ulong completedUploadFence, ulong imageUploadFence)
        => imageUploadFence != 0 && completedUploadFence >= imageUploadFence;
}

/// <summary>
/// The copy queue's fence bookkeeping as a POD value: <see cref="NextSignal"/> hands out the strictly increasing value a
/// batch signals (never 0), <see cref="ObserveCompleted"/> folds a <c>GetCompletedValue</c> read in MONOTONICALLY (a
/// stale or reordered read never walks readiness backwards), and <see cref="IsResident"/> applies
/// <see cref="UploadFencePolicy"/> against the observed value. <see cref="Reset"/> after device recovery (a new fence
/// restarts at 0; every image must be re-staged).
/// </summary>
public struct UploadFenceLedger
{
    private ulong _lastSignaled, _completed;

    public readonly ulong LastSignaled => _lastSignaled;
    public readonly ulong Completed => _completed;

    /// <summary>Uploads staged but not yet completed.</summary>
    public readonly bool HasInFlight => _completed < _lastSignaled;

    public ulong NextSignal() => ++_lastSignaled;

    public void ObserveCompleted(ulong completedValue)
    {
        if (completedValue > _lastSignaled) completedValue = _lastSignaled;   // a fence cannot complete what was never signaled
        if (completedValue > _completed) _completed = completedValue;
    }

    public readonly bool IsResident(ulong imageUploadFence) => UploadFencePolicy.IsResident(_completed, imageUploadFence);

    public readonly UploadReadiness Classify(ulong imageUploadFence) => UploadFencePolicy.Classify(_completed, imageUploadFence);

    public void Reset() { _lastSignaled = 0; _completed = 0; }
}
