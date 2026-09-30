namespace FluentGpu.Hosting;

/// <summary>What a UI-thread gap (the time between one Paint's end and the next Paint's start) was, by where the time
/// went: <see cref="Busy"/> — the thread was executing (posted actions, message dispatch, input, cold work, or code no
/// phase measures); <see cref="Blocked"/> — the thread sat in an explicit wait it asked for (the loop's
/// <c>WaitForWork</c>, up to the timeout it requested); <see cref="NotScheduled"/> — neither: the thread was runnable (a
/// wait's timeout had expired, or it was outside any wait) but did not run — OS pre-emption, a page fault, a
/// process-wide suspension. <see cref="Unknown"/> for a gap too short to call.</summary>
public enum UiGapVerdict : byte { Unknown, NotScheduled, Busy, Blocked }

/// <summary>The measured inputs of one UI gap, all in milliseconds of wall time.</summary>
/// <param name="GapMs">Previous Paint end → this Paint start.</param>
/// <param name="WaitRequestedMs">The loop's requested wait timeout(s) inside the gap, summed; negative when any wait was
/// requested INFINITE (block until a message).</param>
/// <param name="WaitBlockedMs">Wall time actually spent inside those waits.</param>
/// <param name="MessagesMs">Message dispatch (<c>PumpInto</c> — every WndProc handler).</param>
/// <param name="InputMs">Input dispatch (<c>InputDispatcher.Dispatch</c>).</param>
/// <param name="PostsMs">Cross-thread UI posts (<c>DrainUiPosts</c>).</param>
/// <param name="ColdMs">UI cold maintenance.</param>
/// <param name="RunMs">The thread's own running time over the whole gap from its cycle counter; NaN when unknown.</param>
public readonly record struct UiGapSample(float GapMs, float WaitRequestedMs, float WaitBlockedMs, float MessagesMs,
    float InputMs, float PostsMs, float ColdMs, float RunMs);

/// <summary>The gap split into the three causes (they sum to <see cref="UiGapSample.GapMs"/>, clamped at 0), plus the
/// part of the waits that overran a finite request (counted inside <see cref="NotScheduledMs"/>).</summary>
public readonly record struct UiGapBreakdown(UiGapVerdict Verdict, float BlockedMs, float RunningMs, float NotScheduledMs,
    float OverrunMs, bool RunFromCycles);

/// <summary>THE decision behind the always-on gap decomposition — pure, engine-free, allocation-free
/// (<c>UiGapClassifierTests</c>). The host measures; this names.</summary>
public static class UiGapClassifier
{
    /// <summary>A gap below this is not a hitch worth naming (a frame's ordinary scheduling noise).</summary>
    public const float MinGapMs = 4f;

    public static UiGapBreakdown Classify(in UiGapSample s)
    {
        float gap = Math.Max(0f, s.GapMs);
        float blockedTotal = Math.Max(0f, s.WaitBlockedMs);
        // A finite request bounds what the wait was FOR: past it the timer had expired and the thread was runnable. An
        // infinite request (block until a message) is all blocked by construction.
        float overrun = s.WaitRequestedMs < 0f ? 0f : Math.Max(0f, blockedTotal - s.WaitRequestedMs);
        float blocked = Math.Min(gap, blockedTotal - overrun);
        bool fromCycles = !float.IsNaN(s.RunMs);
        float wallBusy = Math.Max(0f, s.MessagesMs) + Math.Max(0f, s.InputMs) + Math.Max(0f, s.PostsMs) + Math.Max(0f, s.ColdMs);
        float running = fromCycles ? Math.Max(0f, s.RunMs) : wallBusy;
        running = Math.Min(running, Math.Max(0f, gap - blocked));   // a calibration rate can overshoot; the gap cannot
        float notScheduled = Math.Max(0f, gap - blocked - running);
        UiGapVerdict verdict = UiGapVerdict.Unknown;
        if (gap >= MinGapMs)
        {
            verdict = UiGapVerdict.NotScheduled;
            float best = notScheduled;
            if (running > best) { best = running; verdict = UiGapVerdict.Busy; }
            if (blocked > best) verdict = UiGapVerdict.Blocked;
        }
        return new UiGapBreakdown(verdict, blocked, running, notScheduled, overrun, fromCycles);
    }
}

/// <summary>The gap decomposition as <see cref="FrameStats.UiGap"/> carries it (filled on a slack frame —
/// <see cref="FrameStats.SlackCause"/> not None — default otherwise).</summary>
public readonly record struct UiGapReport
{
    public float GapMs { get; init; }
    public float GapWaitRequestedMs { get; init; }
    public float GapWaitBlockedMs { get; init; }
    public float GapMessagesMs { get; init; }
    public float GapInputMs { get; init; }
    public float GapPostsMs { get; init; }
    public float GapColdMs { get; init; }
    /// <summary>Wall time of the gap no measured segment (wait, messages, input, posts, cold) accounts for.</summary>
    public float GapOtherMs { get; init; }
    /// <summary>The thread's running time over the gap from its cycle counter; NaN when unknown.</summary>
    public float GapRunMs { get; init; }
    public float GapBlockedMs { get; init; }
    public float GapNotScheduledMs { get; init; }
    public float GapOverrunMs { get; init; }
    public UiGapVerdict GapVerdict { get; init; }
    /// <summary>QPC of the gap's start (the previous Paint's end) — aligns the gap with render-thread evidence.</summary>
    public long GapStartQpc { get; init; }
    /// <summary>How many loop waits fell inside the gap.</summary>
    public int GapWaits { get; init; }
}
