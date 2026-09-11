using System;

namespace FluentGpu.Media;

/// <summary>
/// The pure timing decision behind <see cref="AudioDeviceController"/> (spec §7.9; Wavee #112) — WHEN the cold device
/// thread runs a sink rebuild, and how long it backs off when the rebuild finds no usable endpoint. No threads, no COM,
/// no clock of its own: every method takes <c>nowMs</c> (an <see cref="Environment.TickCount64"/>-style monotonic
/// millisecond count) so it is unit-tested deterministically.
/// <para><b>The livelock this prevents.</b> Two callers ask for a rebuild: the OS default-device watcher
/// (<see cref="NoteDeviceEvent"/>) and the render path, which reports a sink that has stopped accepting frames
/// (<see cref="NoteSinkFailure"/>). The watcher needs a TRAILING debounce — one physical jack switch fires two
/// notifications a few hundred ms apart (48000 then 44100), so the rebuild waits for <see cref="DebounceMs"/> of quiet
/// after the LATEST event. The shipped 0.2.8 controller applied that same re-stamping debounce to the render path's
/// report: a dead sink reports failure every ~80 ms, each report pushed the quiet window out again, and the rebuild never
/// ran — playback stayed silent, state read <c>Running</c>, until the next track rebuilt everything. Here a sink failure
/// may START a request but never re-stamps a pending one, and a watcher burst is bounded by
/// <see cref="MaxDebounceDeferralMs"/> measured from the FIRST event, so a due rebuild cannot be postponed forever by
/// either source.</para>
/// <para><b>The ladder.</b> When an attempt finds no ready endpoint (the new default device is not yet
/// <c>Initialize</c>-able, the feed could not be parked, the factory threw), the controller keeps the OLD sink and
/// schedules a retry after <see cref="RetryLadderMs"/>[attempt]; <see cref="NextRetryDelayMs"/> returns null once the
/// ladder is exhausted (→ <see cref="AudioDeviceState.Faulted"/>, recoverable). A successful rebuild or a fresh device
/// event resets the ladder. Sink-failure reports are ignored while a ladder retry is scheduled — the retry already
/// covers them, and letting them start a new request would collapse the ladder back to 250 ms.</para>
/// </summary>
public sealed class AudioDeviceRecoveryPolicy
{
    /// <summary>Retry back-off after an attempt that found no usable endpoint: 250 ms, 1 s, 3 s, then exhausted.</summary>
    public static readonly int[] RetryLadderMs = { 250, 1000, 3000 };

    /// <summary>Trailing quiet window after the LATEST request before a rebuild runs (covers the observed double
    /// default-device notification of a single physical switch).</summary>
    public const int DebounceMs = 250;

    /// <summary>Upper bound on how long a burst of requests may defer the rebuild, measured from the FIRST request of the
    /// burst — a flapping watcher (or anything re-stamping the window) can never postpone a due rebuild past this.</summary>
    public const int MaxDebounceDeferralMs = 1000;

    private long _firstRequestAt = long.MinValue;
    private long _lastRequestAt = long.MinValue;
    private int _attempt;

    /// <summary>A default-device event from the OS watcher: starts a request or re-stamps the pending one (trailing
    /// debounce), and resets the retry ladder — a genuine device event is new information, the back-off no longer applies.</summary>
    public void NoteDeviceEvent(long nowMs)
    {
        if (_firstRequestAt == long.MinValue) _firstRequestAt = nowMs;
        _lastRequestAt = nowMs;
        _attempt = 0;
    }

    /// <summary>A render-path report that the sink stopped accepting frames. May START a request (returns true — the
    /// caller wakes the cold thread); NEVER re-stamps a pending one (the 0.2.8 livelock), and is ignored while a ladder
    /// retry is already scheduled (<paramref name="retryScheduled"/>).</summary>
    public bool NoteSinkFailure(long nowMs, bool retryScheduled)
    {
        if (retryScheduled || _firstRequestAt != long.MinValue) return false;
        _firstRequestAt = _lastRequestAt = nowMs;
        return true;
    }

    /// <summary>True while a rebuild request is pending (not yet taken by <see cref="ClearPending"/>).</summary>
    public bool HasPending => _firstRequestAt != long.MinValue;

    /// <summary>Milliseconds until the pending request is due: the trailing debounce after the latest request, capped by
    /// <see cref="MaxDebounceDeferralMs"/> from the first. 0 when due now; <see cref="int.MaxValue"/> when nothing is pending.</summary>
    public int DueInMs(long nowMs)
    {
        if (!HasPending) return int.MaxValue;
        long trailing = _lastRequestAt + DebounceMs - nowMs;
        long cap = _firstRequestAt + MaxDebounceDeferralMs - nowMs;
        return (int)Math.Max(0, Math.Min(trailing, cap));
    }

    /// <summary>Take the pending request (the attempt is about to run). A request arriving during the attempt starts a
    /// fresh window.</summary>
    public void ClearPending() => _firstRequestAt = _lastRequestAt = long.MinValue;

    /// <summary>The next ladder delay, advancing the ladder; null once exhausted.</summary>
    public int? NextRetryDelayMs() => _attempt < RetryLadderMs.Length ? RetryLadderMs[_attempt++] : null;

    /// <summary>Reset the ladder after a successful rebuild.</summary>
    public void ResetLadder() => _attempt = 0;
}
