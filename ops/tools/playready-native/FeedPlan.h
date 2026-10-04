// FeedPlan.h — the feeder's segment-index arithmetic, pulled out of PrSession.cpp's PlanTrack so it can be exercised
// by a dependency-free test exe (no <windows.h>, no WinRT, no CencMediaStream — just the numbers).
//
// THE PRODUCTION INCIDENT THIS FILE FIXES (proved from a warm-start log). A warm-start prefetch landed video segments
// 0 and 1, leaving the progress guard at lastIdx=1, lastCovEnd=8007. The first ABR tick then ran a representation
// switch at boundary 0, which truncated the buffered tail: coverage SHRANK to [0,4003). The next plan computed
// idx = SegmentOf(4003 + segLen/2) = 1 — the SAME index the guard already remembered — so the old rule
// `lastIdx == idx && cov.endMs <= lastCovEnd` read the shrink as "no growth" and stepped the floor to 2. Segment 2 was
// fetched, permanently skipping the real hole [4003, 8007) that the switch had just opened. Worse: the *next* plan
// compared the PRE-floor idx (1, recomputed fresh from the still-broken coverage) against lastIdx, which by then had
// also become 1 again (nothing ever landed to update it) — the floor never advanced a second time, so segment 2 was
// requested over and over, ~700 times a second, forever (until a seek reset the guard).
//
// This header is the arithmetic half of the fix:
//   * a coverage SHRINK (not just "no growth") is now its own case (Reset rule below) — the guard re-learns instead
//     of computing a stagnation floor from bookkeeping that describes a range which no longer exists;
//   * the floor is applied to the index BEFORE the stagnation test, and the test compares the index actually planned
//     (post-floor) with `==`, not `<=` — so a floor, once stepped, can step again when the same post-floor index comes
//     back with truly unchanged coverage, instead of being compared against a pre-floor number that never repeats.
//
// The other half — WHY the guard now resets reliably (CencMediaStream::CutGen(), a structural per-stream generation
// counter bumped by every code path that can shrink coverage) and WHY a planner fault now costs at most 4 fetches/s
// instead of ~700 — lives in PrSession.cpp (ResetTrackState, RunOneJob's SPIN BRAKE). This file only answers "given a
// guard and a coverage snapshot, what segment (if any) should be fetched next, and what does the guard become".
#pragma once

#include <cstdint>

namespace fgpr::plan {

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The segment grid, in isolation (PrSession.cpp's SegmentOfUnclamped mirrored here so this header stays pure).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
inline int32_t SegOf(int64_t ms, int32_t segLenMs)
{
    if (ms < 0) ms = 0;
    const int64_t idx = ms / segLenMs;
    return idx > (int64_t)INT32_MAX ? INT32_MAX : (int32_t)idx;
}

/// The progress guard: what the feeder remembers about the LAST plan for one track, across calls to `Next`.
/// `lastIdx`/`lastCovEnd` are the index and coverage-end that plan produced (set for real only once the fetch it
/// asked for actually lands — see `Landed`, called from PrSession.cpp's handleTrack). `floorIdx` is the lower bound a
/// stagnant index steps to, and stays in force until the caller decides a STRUCTURAL reset applies (a seek, a
/// re-attach, or — the piece that was missing before this fix — a coverage-cutting operation on the stream itself,
/// which `Next` cannot see and so cannot be its job to detect: PrSession.cpp resets the guard from
/// CencMediaStream::CutGen() instead).
struct Guard
{
    int32_t lastIdx = -1;
    int64_t lastCovEnd = -1;
    int32_t floorIdx = -1;
};

/// The buffered range containing the planning reference (PrSession.cpp's Coverage, trimmed to what this file needs).
/// -1/-1 means nothing covers the reference.
struct Cov
{
    int64_t startMs = -1;
    int64_t endMs = -1;
};

/// What `Next` decided for one track.
struct Out
{
    int32_t idx = -1;            // the segment to fetch, -1 = nothing to do right now
    bool atEnd = false;          // coverage from the reference reaches (or passes) endIndex: the track is done
    bool guardStepped = false;   // the stagnation rule fired on THIS call — the caller logs it once
    int32_t steppedPast = -1;    // the index the floor stepped past, for that one log line
};

inline void Reset(Guard& g) { g = Guard{}; }

/// Plan the next segment for one track.
///
/// `next` receives the guard state this call WOULD leave behind. The caller does not have to accept it: PrSession.cpp
/// PlanTrack's byte-cap block can still cancel a plan after this returns (the session is over its store budget), and
/// a cancelled plan must not have moved the floor — so the caller commits `next` into its FeederState guard ONLY for a
/// plan that survives. (The bug this replaces committed the floor unconditionally and then sometimes discarded the
/// fetch, which is a second, independent way the guard used to drift from reality.)
///
/// `segLenMs <= 0` (measured-length grids before the first segment has been parsed) cannot address anything yet:
/// plan nothing rather than divide by zero.
inline Out Next(const Guard& g, Guard& next, Cov cov, int64_t refMs, int64_t wantEndMs, int32_t segLenMs, int32_t endIndex)
{
    Out out;
    next = g;
    if (segLenMs <= 0) return out;

    const bool covered = cov.startMs >= 0 && cov.endMs >= 0;

    // RULE (a) — the incident above. A coverage range that SHRANK since the last plan (a truncating splice, e.g. a
    // representation switch) is not "the same range, unchanged": treat the guard as freshly reset so the stagnation
    // test below is computed from what is actually buffered now, not from an end position that no longer exists.
    Guard effective = g;
    if (covered && effective.lastCovEnd >= 0 && cov.endMs < effective.lastCovEnd)
        effective = Guard{};

    int32_t idx;
    if (!covered)
    {
        idx = SegOf(refMs, segLenMs);
        // RULE (b) — unchanged from the original behaviour. The segment containing the reference was already fetched
        // and STILL does not cover it (a grid that does not match the content): asking again cannot help. Wait for
        // the reference to move or for a structural reset.
        if (effective.lastIdx == idx && effective.lastCovEnd < 0) { next = effective; return out; }
    }
    else
    {
        if (cov.endMs >= wantEndMs) { next = effective; return out; }   // satisfied
        idx = SegOf(cov.endMs + segLenMs / 2, segLenMs);
        // RULE (c) — the floor fix. Apply the floor FIRST, then test stagnation against the index actually planned
        // (post-floor), with `==` — not `<=`. The bug this replaces tested the PRE-floor idx a second time one line
        // later (against a lastIdx that, by then, was also pre-floor), so a floor that had already stepped once never
        // stepped again: the same segment was replanned every tick.
        if (effective.floorIdx > idx) idx = effective.floorIdx;
        if (effective.lastIdx == idx && cov.endMs == effective.lastCovEnd)
        {
            effective.floorIdx = idx + 1;
            out.steppedPast = idx;
            idx = effective.floorIdx;
            out.guardStepped = true;
        }
    }

    if (idx >= endIndex) { out.atEnd = true; next = effective; return out; }
    if (covered && (int64_t)idx * (int64_t)segLenMs >= wantEndMs) { next = effective; return out; }

    out.idx = idx;
    next = effective;
    return out;
}

/// What the caller (PrSession.cpp handleTrack) records once a planned fetch actually lands: the guard learns the
/// REAL index and coverage-end, not the provisional ones `Next` predicted while the fetch was still on the wire.
inline void Landed(Guard& g, int32_t idx, int64_t covEndAfter)
{
    g.lastIdx = idx;
    g.lastCovEnd = covEndAfter;
}


/// The forward-window threshold the demand hook is re-armed at once the planner says SATISFIED (F037).
///
/// The planner measures "ahead" from a reference (the playhead, lifted to the delivery cursor) against `bufferAheadMs`;
/// the hook measures contiguous ahead from the delivery cursor on every delivered sample. Re-arming the hook at the full
/// `bufferAheadMs` therefore left a window - the cursor's lead over the reference plus the position's staleness, a few
/// tenths of a second out of every segment cycle - in which the hook said "below target" and the planner said "satisfied":
/// every delivered sample (about 80 a second) woke the feeder for a plan that fetched nothing. ExoPlayer's load control
/// avoids it with hysteresis (start loading below minBuffer, stop at maxBuffer); this is the same: after a satisfied plan
/// the next wake is one segment of playback away, never closer than the target allows.
///
/// `aheadFromCursorMs` is the track's contiguous reach from its own cursor (CencMediaStream::ContiguousAheadMs), the
/// hook's own measure. Never above `bufferAheadMs` (a track holding far more than the target still wakes at the target)
/// and never below 1 (0 means "the store's bufferAheadMs" to SetDemandBelowMs).
inline int64_t DemandBelowWhenSatisfied(int64_t aheadFromCursorMs, int64_t bufferAheadMs, int32_t segLenMs)
{
    const int64_t lowered = aheadFromCursorMs - (segLenMs > 0 ? (int64_t)segLenMs : 0);
    const int64_t below = lowered < bufferAheadMs ? lowered : bufferAheadMs;
    return below > 1 ? below : 1;
}

/// A covered range that reaches the presentation's duration (within `toleranceMs` — the last segment's samples end a
/// frame or two short of the manifest's rounded duration) has nothing left to fetch: the stream is COMPLETE, and the
/// planner's "satisfied" answer (cov.endMs >= wantEndMs, wantEndMs clamped to the duration) must say so, or a stream
/// that runs dry at the end waits for a segment that does not exist. 2026-09-22: the audio stream of a finished video
/// starved at its last sample and MEEndOfPresentation was held for 108 s.
inline bool ReachesEnd(int64_t covEndMs, int64_t durationMs, int64_t toleranceMs)
{
    if (durationMs <= 0 || covEndMs < 0) return false;
    return covEndMs + toleranceMs >= durationMs;
}

/// The native side's OWN SetCurrentTime (the carried start position applied to the engine timeline at attach, or the
/// fallback correction at CANPLAY): nobody on the managed side is waiting for its SEEKED, so that SEEKED is swallowed -
/// but never a USER seek's. The seek is tagged with the user seekSeq current when it was issued.
///   * A user seek issued to the engine afterwards cancels the tag (HTML5 seeking semantics: a new seek aborts the one in
///     flight, so the single SEEKED that follows belongs to the user's), so a stale tag can never swallow it.
///   * A SEEKED that finds the tag still set is the internal one. It clears the native `seeking` flag unless a user seek
///     has been posted since (seekSeq moved) - that seek still owns the flag and its own SEEKED follows.
struct InternalSeek
{
    bool pending = false;
    uint64_t userSeq = 0;

    struct Verdict
    {
        bool internal = false;        // swallow: do not raise FgPrEvent_Seeked
        bool clearsSeeking = true;    // reset the native `seeking` flag
    };

    void Issue(uint64_t currentUserSeq) { pending = true; userSeq = currentUserSeq; }
    void Cancel() { pending = false; }

    Verdict OnSeeked(uint64_t currentUserSeq)
    {
        if (!pending) return Verdict{ false, true };
        pending = false;
        return Verdict{ true, currentUserSeq == userSeq };
    }
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  A segment GET that failed: stall and retry, or the end of the track (F035).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// A transient failure (a dropped connection, a 5xx, a 200 with no body) and any 4xx but "this segment does not exist"
// used to latch the track's end index after three quick retries (3.5 s), truncating the presentation for the rest of
// the session. Neither reference engine ends a presentation on a segment failure (ExoPlayer's load-error policy and
// Shaka's streaming engine both back off and retry). The feeder now treats a failure as a STALL: it retries with a
// capped exponential back-off for as long as the plan keeps asking for that segment (the playhead, or the look-ahead
// window, still needs it), and a seek or any structural reset starts the count afresh.

/// The back-off before the first retry, and the longest wait between two retries.
constexpr int kFeedRetryBaseMs = 500;
constexpr int kFeedRetryMaxMs = 8000;

/// The wait before retry number `failCount` (1 = the first consecutive failure of one segment): 0.5 s, 1 s, 2 s, 4 s,
/// then 8 s for as long as it keeps failing.
inline int RetryDelayMs(int failCount)
{
    if (failCount < 1) failCount = 1;
    int ms = kFeedRetryBaseMs;
    for (int i = 1; i < failCount && ms < kFeedRetryMaxMs; i++) ms *= 2;
    return ms < kFeedRetryMaxMs ? ms : kFeedRetryMaxMs;
}

/// Whether a failed GET of segment `idx` ends the track. Only an answer of "no such segment" (404 / 410) can, and only
/// AT or BEYOND the manifest's segment count: a 404 inside the manifest is a CDN or signed-URL problem, not the end.
/// `segmentCount` is the presentation's effective count; INT32_MAX means unknown (neither the descriptor nor the
/// duration gave one), where the missing segment IS the only end signal there is, so a 404 / 410 latches as it always
/// did. Every other status (401 / 403 / 408 / 429 / 5xx, a transport failure with status 0, an empty 200) is a stall.
inline bool EndsTrack(int status, int32_t idx, int32_t segmentCount)
{
    if (status != 404 && status != 410) return false;
    return segmentCount == INT32_MAX || idx >= segmentCount;
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The engine said WAITING (F030).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// MF_MEDIA_ENGINE_EVENT_WAITING / STALLED / BUFFERINGSTARTED mean the playback clock stopped for want of data (a feeder
// underrun, a key wait, a decoder stall) while the engine still reads "Playing". The snapshot alone cannot say so, so
// the runtime thread keeps this flag and raises an event on each EDGE. The flag clears on PLAYING, SEEKED,
// BUFFERINGENDED, or a TIMEUPDATE whose position moved on from where the wait began (the clock is running again).
struct WaitGate
{
    /// How far past the wait's own position a TIMEUPDATE must read to prove the clock runs again: more than the jitter
    /// between the WAITING notification and the clock's last tick, far less than a TIMEUPDATE interval.
    static constexpr int64_t kAdvanceMs = 50;

    bool waiting = false;
    int64_t sinceMs = 0;   // the position the wait began at

    /// The engine started waiting at `positionMs`. True only on the edge (already waiting: nothing new to report).
    bool Enter(int64_t positionMs)
    {
        if (waiting) return false;
        waiting = true;
        sinceMs = positionMs;
        return true;
    }

    /// PLAYING, SEEKED or BUFFERINGENDED. True only when it ends a wait.
    bool Clear()
    {
        if (!waiting) return false;
        waiting = false;
        return true;
    }

    /// A TIMEUPDATE at `positionMs`. True only when it ends a wait (the position advanced past where it began).
    bool OnTimeUpdate(int64_t positionMs)
    {
        return waiting && positionMs >= sinceMs + kAdvanceMs ? Clear() : false;
    }

    /// An attach or detach: the next source starts with no wait and nothing to report.
    void Reset() { waiting = false; sinceMs = 0; }
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Pipelined segment GETs and the throughput clock (F041).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// One outstanding GET per track made every refill (after a seek or a representation switch) pay a full RTT + time-to-first-byte
// per segment before the next request could start. Shaka's SegmentPrefetch and ExoPlayer's chunk sampling both keep the NEXT chunk
// in flight while the current one lands. While a track's contiguous forward buffer is below a low watermark it may now keep TWO
// GETs outstanding; once the buffer is healthy it is back to one (a second concurrent GET only shares the link, it adds nothing).
// The two are appended strictly in segment order (the second waits for the first), so the store never sees a gap.

/// The most GETs one track may have outstanding.
constexpr int kMaxGetsPerTrack = 2;

/// The forward buffer below which a track pipelines: two segments, but never more than half the forward target (a short target
/// must not pipeline for the whole of its window). 0 when the segment length is not known yet.
inline int64_t PipelineLowWatermarkMs(int64_t segLenMs, int64_t bufferAheadMs)
{
    if (segLenMs <= 0) return 0;
    int64_t w = 2 * segLenMs;
    const int64_t half = bufferAheadMs / 2;
    if (half > 0 && w > half) w = half;
    return w < segLenMs ? segLenMs : w;   // at least one segment: below that the buffer is genuinely empty
}

/// How many GETs this track may have outstanding for this plan. `capRoom` is whether the byte budget has room for a second segment
/// on top of what is held; a SEEK plan is never pipelined (ApplyPendingSeek needs the target segment only, and the plan right after
/// it, with an empty buffer, pipelines).
inline int PipelineDepth(int64_t contiguousAheadMs, int64_t lowWatermarkMs, bool seekPlan, bool capRoom)
{
    if (seekPlan || !capRoom || lowWatermarkMs <= 0) return 1;
    return contiguousAheadMs < lowWatermarkMs ? kMaxGetsPerTrack : 1;
}

/// The segment a pipelined second GET fetches: the one after `idx`, when it exists (before `endIndex`) and the plan still wants it
/// (its start inside the forward target, `wantEndMs`). -1 = nothing to pipeline.
inline int32_t PipelinedNext(int32_t idx, int32_t endIndex, int32_t segLenMs, int64_t wantEndMs)
{
    if (idx < 0 || segLenMs <= 0 || idx == INT32_MAX) return -1;
    const int32_t next = idx + 1;
    if (next >= endIndex) return -1;
    if ((int64_t)next * (int64_t)segLenMs >= wantEndMs) return -1;
    return next;
}

/// The throughput clock. The ABR's estimate is bytes / elapsed over the session's cumulative counters, and each completed GET adds
/// ITS OWN bytes and ITS OWN duration. GETs that overlap on the wire (a video and an audio segment, or a pipelined pair) share the
/// link, so summing their durations would make the estimate read a fraction of the real speed (two GETs each at half speed: 2B
/// bytes over 4T summed ms = B/2T, against a real 2B/2T). Each GET is therefore charged only the part of its transfer interval not
/// already charged to an earlier one: the charges of GETs handled in order add up to the length of the UNION of their intervals, so
/// bytes / charged ms is the link's aggregate rate. Sequential GETs are charged their full duration, exactly as before. (A GET
/// handled out of completion order can be charged a little less than its share; the estimate then errs high by that sliver.)
struct BusyClock
{
    uint64_t busyUntilMs = 0;   // the end of the latest transfer charged so far

    /// The ms to charge for a GET whose body transferred over [startMs, endMs].
    uint64_t Charge(uint64_t startMs, uint64_t endMs)
    {
        if (endMs < startMs) endMs = startMs;
        const uint64_t from = startMs > busyUntilMs ? startMs : busyUntilMs;
        const uint64_t charged = endMs > from ? endMs - from : 0;
        if (endMs > busyUntilMs) busyUntilMs = endMs;
        return charged;
    }
};

} // namespace fgpr::plan
