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
} // namespace fgpr::plan
