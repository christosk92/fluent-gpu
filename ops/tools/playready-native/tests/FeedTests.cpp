// FeedTests.cpp — a dependency-free console test exe for the PlayReady video feeder's pure arithmetic and buffer
// algorithms: fgpr::plan::Next (FeedPlan.h) and the SegmentStore.h sample-list algorithms (SpliceSamples,
// IsAscending, ComputeBufferedPairs, ContiguousAheadMs, TrimBehindByTime, CanSeekToIn).
//
// WHY THIS FILE EXISTS AND WHAT IT DOES NOT DO. FeedPlan.h and SegmentStore.h are pure C++ over already-demuxed
// cenc::Sample data — no Media Foundation call, no COM, no CDM, no network is on the path this file exercises. The
// tests below never construct a CencMediaStream or CencMediaSource and never touch the MF/WinRT runtime; they only
// call the free functions in namespace fgpr / fgpr::plan.
//
// WHY THE WINDOWS/D3D/MF/WINRT PREAMBLE IS HERE ANYWAY. CencMediaSource.h (included transitively through
// SegmentStore.h, see below) is not standalone: its own comment says it is "#included from PrInternal.h AFTER the
// platform headers (Media Foundation, C++/WinRT) and the log entry points (LogLine, fgpr::RaiseLog) are declared".
// The cenc::Sample definition this file needs sits BEFORE that MF/WinRT-dependent section (CencMediaStream /
// CencMediaSource, the custom IMFMediaStream/IMFMediaSource), but the compiler still fully parses and semantically
// checks the whole header in one translation unit, so the MF/WinRT types those later structs name must resolve.
// This preamble reproduces PrInternal.h's include list so this TU compiles exactly like the DLL's build.cmd
// translation units do; RaiseLog/LogLine are stubbed just below (never called from anywhere this file reaches —
// they only need to exist for CencMediaStream::LogLine and BuildCencSource's calls to resolve at compile time).
//
// INCLUDE-ORDER FINDING (report this — do not "fix" SegmentStore.h, it is a read-only file for this change).
// SegmentStore.h's own header comment says it is includable "from PrInternal.h (and ... from CencMediaSource.h
// right after `namespace cenc` closes)". Taken at face value that suggests `#include "SegmentStore.h"` alone is a
// safe, standalone way to pull in the feeder's buffer algorithms. It is NOT, for a fresh translation unit: pragma
// once creates a real ordering dependency. SegmentStore.h's line 23 is `#include "CencMediaSource.h"`, itself BEFORE
// any of SegmentStore.h's own namespace-fgpr content (SlabPool, KeyframeTable, SpliceOutcome, SpliceSamples, ...).
// If SegmentStore.h is the FIRST of the two included in a TU: SegmentStore.h's pragma-once guard is set, then its
// line 23 include of CencMediaSource.h runs, which reaches CencMediaSource.h's own line 793
// (`#include "SegmentStore.h"`) — a no-op because the guard is already set — and CencMediaSource.h then continues
// to define CencMediaStream, whose inline method `fgpr::SpliceOutcome SpliceLocked(...)` needs `fgpr::SpliceOutcome`
// as a COMPLETE type. That type is defined by SegmentStore.h's OWN body, which has not run yet (we are still inside
// processing SegmentStore.h's line-23 include) — a genuine "incomplete type" compile error. Including
// CencMediaSource.h FIRST (as PrInternal.h itself does, line 250 before line 251) does not have this problem:
// CencMediaSource.h defines cenc::Sample, then pulls in the whole of SegmentStore.h at line 793 (which is now the
// FIRST time SegmentStore.h is processed, so it runs fully, including its own body), and only after that does
// CencMediaSource.h define CencMediaStream / CencMediaSource, by which point fgpr::SpliceOutcome etc. are complete.
// This file therefore includes "CencMediaSource.h" first, exactly like PrInternal.h does, and does not include
// "SegmentStore.h" directly at all (CencMediaSource.h already pulls it in, and re-including it after is a no-op).

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_3.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfmediaengine.h>
#include <mfcontentdecryptionmodule.h>
#include <windows.media.protection.h>
#include <propsys.h>
#include <propvarutil.h>
#include <wincrypt.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Media.Protection.h>
#include <winrt/Windows.Web.Http.h>
#include <winrt/Windows.Web.Http.Headers.h>

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Minimal stand-ins for PrInternal.h's diagnostic entry points. CencMediaSource.h's CencMediaStream/CencMediaSource
//  member functions and BuildCencSource() call these by name (fgpr::RaiseLog, the free-function ::LogLine) — this
//  test exe never runs any code path that reaches them (no CencMediaStream is ever constructed here), so a no-op
//  body is all a compile-time contract needs. The real implementation (PrInternal.h) is NOT duplicated: no
//  EventSink, no FgPrEventCallback, no FgPlayReady.h — those are runtime-callback plumbing this file has no use for.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
namespace fgpr { inline void RaiseLog(uint64_t /*handle*/, const std::string& /*line*/) {} }
inline void LogLine(const std::string& /*s*/) {}

// The demuxer + IMFMediaSource header; it pulls in SegmentStore.h itself at the point that is actually safe (see the
// INCLUDE-ORDER FINDING above). Do not reorder these two lines and do not include "SegmentStore.h" directly first.
#include "../CencMediaSource.h"
#include "../SegmentStore.h"

#include "../FeedPlan.h"

#include <sstream>

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Tiny self-contained test harness — no framework, no dependency beyond what is already pulled in above.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
static int g_fail = 0;

template <typename T>
static std::string ToStr(const T& v)
{
    std::ostringstream oss;
    oss << v;
    return oss.str();
}
// bool prints as true/false, not 1/0 — every CHECK_EQ in this file compares an integral or bool.
static std::string ToStr(bool v) { return v ? "true" : "false"; }

#define CHECK(cond)                                                                                                  \
    do                                                                                                               \
    {                                                                                                                \
        if (!(cond))                                                                                                 \
        {                                                                                                            \
            std::printf("FAIL %s:%d: CHECK(%s)\n", __FILE__, __LINE__, #cond);                                      \
            ++g_fail;                                                                                                \
        }                                                                                                            \
    } while (0)

#define CHECK_EQ(a, b)                                                                                               \
    do                                                                                                               \
    {                                                                                                                \
        auto _cv_a = (a);                                                                                            \
        auto _cv_b = (b);                                                                                            \
        if (!(_cv_a == _cv_b))                                                                                       \
        {                                                                                                            \
            std::printf("FAIL %s:%d: CHECK_EQ(%s, %s) -> %s vs %s\n", __FILE__, __LINE__, #a, #b,                   \
                        ToStr(_cv_a).c_str(), ToStr(_cv_b).c_str());                                                 \
            ++g_fail;                                                                                                \
        }                                                                                                            \
    } while (0)

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Sample builder — a realistic run of cenc::Sample for one segment.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// Every call with the same (index, timescale, fps, segLenMs, bframes) is byte-for-byte deterministic (no clock, no
// randomness): tests rely on this to model a "re-fetch of the same segment" by calling MakeSegment again.
//
// Layout: `frameCount` samples per segment, `frameCount = (segLenMs * fps) / 1000` (rounded down to a multiple of 4
// when `bframes` so every segment is a whole number of 4-frame GOPs). `decodeTicks` is a strictly-increasing grid
// (frameDurTicks apart) that never resets across segments — `decodeTicks[j] = index*frameCount*frameDurTicks +
// j*frameDurTicks` — mirroring the real feeder's runningDecodeTicks counter (SegmentStore.h's ORDERING KEY DECISION
// comment). The vector itself is built in that same DECODE order (array index == decode order), matching the
// contract `SpliceSamples`/`IsAscending` are written against.
//
// `bframes = false`: presentation order == decode order (timeTicks == decodeTicks), one keyframe at the front.
// `bframes = true`: each 4-frame group is decoded I, P, B, B but DISPLAYED I, B, B, P — decodeTicks keeps climbing
// every sample (I=0,P=1,B=2,B=3 in decode-order steps) while timeTicks jumps to the DISPLAY slot
// (I=0, P=3, B=1, B=2) * frameDurTicks, so timeTicks visibly dips after every P frame. Every group's first sample
// (the I frame) is a keyframe.
static std::vector<cenc::Sample> MakeSegment(int index, uint32_t timescale, int fps, int segLenMs, bool bframes)
{
    std::vector<cenc::Sample> out;
    if (fps <= 0 || segLenMs <= 0 || timescale == 0) return out;

    const uint64_t frameDurTicks = (uint64_t)timescale / (uint64_t)fps;
    int frameCount = (segLenMs * fps) / 1000;
    if (bframes) frameCount = (frameCount / 4) * 4;
    if (frameCount < (bframes ? 4 : 1)) frameCount = bframes ? 4 : 1;

    const uint64_t base = (uint64_t)index * (uint64_t)frameCount * frameDurTicks;
    static const int kDisplaySlot[4] = { 0, 3, 1, 2 };   // decode I,P,B,B -> display I,B,B,P

    out.reserve((size_t)frameCount);
    for (int j = 0; j < frameCount; j++)
    {
        const bool isKeyframe = bframes ? (j % 4 == 0) : (j == 0);
        const int displaySlot = bframes ? ((j / 4) * 4 + kDisplaySlot[j % 4]) : j;

        cenc::Sample s;
        s.decodeTicks = base + (uint64_t)j * frameDurTicks;
        s.timeTicks = base + (uint64_t)displaySlot * frameDurTicks;
        s.durTicks = frameDurTicks;
        s.keyframe = isKeyframe;
        s.encrypted = true;
        // Small, non-empty payloads: real enough for SampleFootprint to be non-zero and for a keyframe to look
        // bigger than a delta frame, never realistic H.264 bitstream (not needed — nothing here decodes it).
        s.data.assign(isKeyframe ? 256u : 64u, (uint8_t)(0x40 + (j % 32)));
        s.iv.assign(8, (uint8_t)(index & 0xFF));
        s.subsamples.push_back(cenc::Subsample{ 8u, (uint32_t)(s.data.size() > 8 ? s.data.size() - 8 : 0) });
        out.push_back(std::move(s));
    }
    return out;
}

/// Append `seg` onto `buf` with a throwaway cursor — used to build a multi-segment buffer in tests that are not
/// themselves exercising SpliceSamples's cursor handling (group C, and building the "before" state in group B).
static void AppendSegment(std::vector<cenc::Sample>& buf, std::vector<cenc::Sample>&& seg)
{
    size_t discardCursor = 0;
    fgpr::SpliceSamples(buf, discardCursor, std::move(seg), false);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  A. fgpr::plan::Next
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// A1 — THE INCIDENT REPLAY (FeedPlan.h's own top-of-file comment). A warm-start prefetch lands segments 0 and 1
/// (guard ends at lastIdx=1, lastCovEnd=8007), then a truncating representation switch shrinks coverage to
/// [0,4003) — segLenMs is the log's 4003. The OLD rule (`lastIdx == idx && cov.endMs <= lastCovEnd`) read that
/// shrink as "no growth" and stepped the floor to 2, skipping the real hole [4003,8007) forever. The fix (Reset
/// rule) must plan idx 1 again here, not 2, and must not report guardStepped. Then, once segment 1 actually lands
/// again (Landed(1,8007)), planning must resume normally: 2, then 3.
static void Test_A1_IncidentReplay()
{
    using namespace fgpr::plan;
    const int32_t segLenMs = 4003;
    const int64_t wantEndMs = 60000;
    const int32_t endIndex = 1000;

    Guard g, next;

    // Warm-start prefetch: segment 0, then segment 1.
    Out o0 = Next(g, next, Cov{ -1, -1 }, /*refMs*/ 0, wantEndMs, segLenMs, endIndex);
    CHECK_EQ(o0.idx, 0);
    g = next;
    Landed(g, 0, 4003);

    Out o1 = Next(g, next, Cov{ 0, 4003 }, 0, wantEndMs, segLenMs, endIndex);
    CHECK_EQ(o1.idx, 1);
    g = next;
    Landed(g, 1, 8007);   // guard: lastIdx=1, lastCovEnd=8007

    // The representation switch: coverage SHRANK to [0,4003) without Next ever being told directly — it only sees
    // the new Cov on the next call, which is exactly the incident's timing.
    Out shrunk = Next(g, next, Cov{ 0, 4003 }, 0, wantEndMs, segLenMs, endIndex);
    CHECK(!shrunk.guardStepped);
    // THE KEY ASSERTION: the old buggy rule would have planned idx 2 here (the guard's stale lastIdx==1 read as
    // "already at idx 1, no growth" and stepped the floor past it). The fix must plan 1 — the segment that now
    // covers the real hole the switch just opened.
    CHECK_EQ(shrunk.idx, 1);
    g = next;
    Landed(g, 1, 8007);   // segment 1 re-fetched, coverage restored

    Out o2 = Next(g, next, Cov{ 0, 8007 }, 0, wantEndMs, segLenMs, endIndex);
    CHECK_EQ(o2.idx, 2);
    g = next;
    Landed(g, 2, 8007 + segLenMs);

    Out o3 = Next(g, next, Cov{ 0, 8007 + segLenMs }, 0, wantEndMs, segLenMs, endIndex);
    CHECK_EQ(o3.idx, 3);
}

/// A2 — genuine stagnation (no shrink, no growth): the SAME coverage end comes back for the SAME planned index.
/// The guard must step its floor exactly once (guardStepped, steppedPast == the stagnant index) and the segment
/// after the floor must never come back to the stagnant index on a later call with the same, still-stuck coverage.
static void Test_A2_GenuineStagnation()
{
    using namespace fgpr::plan;
    const int32_t segLenMs = 4000;
    const int64_t wantEndMs = 60000;
    const int32_t endIndex = 1000;

    Guard g, next;
    Landed(g, 2, 8000);   // segment 2 already landed; its coverage stalled at 8000

    Out first = Next(g, next, Cov{ 0, 8000 }, 0, wantEndMs, segLenMs, endIndex);
    CHECK(first.guardStepped);
    CHECK_EQ(first.steppedPast, 2);
    CHECK_EQ(first.idx, 3);
    g = next;

    // Same coverage, same guard-derived pre-floor index (2) — must NOT plan 2 again, and must not step a second
    // time on this call (the floor from the previous call already accounts for it).
    Out again = Next(g, next, Cov{ 0, 8000 }, 0, wantEndMs, segLenMs, endIndex);
    CHECK(!again.guardStepped);
    CHECK_EQ(again.idx, 3);
    CHECK(again.idx != 2);
}

/// A3 — floor + stagnation AT the floor index steps the floor again: this is the "pre-floor-comparison bug" the
/// header describes. Once floor=3 (from A2's scenario) and segment 3 itself lands with coverage that still does not
/// grow, the RECOMPUTED pre-floor index is back to 2 (identical to before) but the post-floor index is 3 — the fix
/// compares the index actually planned (post-floor, 3) against lastIdx (also 3 once Landed(3,...) ran), which is
/// what lets the floor step a second time to 4. The bug this replaces compared the stale PRE-floor number and never
/// stepped again, replanning segment 2 forever.
static void Test_A3_FloorStagnationAdvancesAgain()
{
    using namespace fgpr::plan;
    const int32_t segLenMs = 4000;
    const int64_t wantEndMs = 60000;
    const int32_t endIndex = 1000;

    Guard g, next;
    Landed(g, 2, 8000);
    Out stepOnce = Next(g, next, Cov{ 0, 8000 }, 0, wantEndMs, segLenMs, endIndex);
    CHECK(stepOnce.guardStepped);
    CHECK_EQ(stepOnce.idx, 3);
    g = next;
    CHECK_EQ(g.floorIdx, 3);

    // Segment 3 lands (planned above), but coverage is STILL stuck at 8000.
    Landed(g, 3, 8000);
    Out stepAgain = Next(g, next, Cov{ 0, 8000 }, 0, wantEndMs, segLenMs, endIndex);
    CHECK(stepAgain.guardStepped);
    CHECK_EQ(stepAgain.steppedPast, 3);
    CHECK_EQ(stepAgain.idx, 4);
}

/// A4 — plan-then-cancel does not commit. `Next` never mutates the `g` it is given (it only ever writes into
/// `next`); the real "cancel" contract lives in the CALLER's discipline of not copying `next` into its persisted
/// guard when the byte cap vetoes the fetch. This test proves that discipline is sufficient: replaying the exact
/// same call against an un-committed `g` reproduces the identical decision (including a stagnation step) rather
/// than silently advancing, and that `next`'s computed fields are themselves deterministic across the replay.
static void Test_A4_PlanThenCancelDoesNotCommit()
{
    using namespace fgpr::plan;
    const int32_t segLenMs = 4000;
    const int64_t wantEndMs = 60000;
    const int32_t endIndex = 1000;

    Guard g, next;
    g.lastIdx = 2;
    g.lastCovEnd = 8000;
    g.floorIdx = -1;
    const Guard gBefore = g;

    Out first = Next(g, next, Cov{ 0, 8000 }, 0, wantEndMs, segLenMs, endIndex);
    CHECK(first.guardStepped);
    CHECK_EQ(first.idx, 3);
    const Guard nextAfterFirst = next;

    // The byte-cap cancel: `next` is discarded, `g` is never assigned. `g` itself cannot have moved (Next takes it
    // by const&) — the interesting assertion is that replaying the call reproduces the SAME plan, proving no side
    // channel let the first call's stagnation step leak into the second.
    CHECK_EQ(g.lastIdx, gBefore.lastIdx);
    CHECK_EQ(g.lastCovEnd, gBefore.lastCovEnd);
    CHECK_EQ(g.floorIdx, gBefore.floorIdx);

    Out second = Next(g, next, Cov{ 0, 8000 }, 0, wantEndMs, segLenMs, endIndex);
    CHECK(second.guardStepped);
    CHECK_EQ(second.idx, first.idx);
    CHECK_EQ(second.steppedPast, first.steppedPast);
    CHECK_EQ(next.lastIdx, nextAfterFirst.lastIdx);
    CHECK_EQ(next.lastCovEnd, nextAfterFirst.lastCovEnd);
    CHECK_EQ(next.floorIdx, nextAfterFirst.floorIdx);
}

/// A5 — satisfied: coverage already reaches (or passes) wantEndMs. Nothing to fetch, and this is not "the end of
/// the track" (atEnd is specifically endIndex, a different concept).
static void Test_A5_Satisfied()
{
    using namespace fgpr::plan;
    Guard g, next;
    Out r = Next(g, next, Cov{ 0, 60000 }, 0, /*wantEndMs*/ 60000, 4000, 1000);
    CHECK_EQ(r.idx, -1);
    CHECK(!r.atEnd);
}

/// A6 — atEnd: the planned index (post-floor) reaches endIndex. `idx` stays -1 (the atEnd branch returns before
/// `out.idx` is ever assigned) — atEnd and "here is a segment to fetch" are mutually exclusive outcomes.
static void Test_A6_AtEnd()
{
    using namespace fgpr::plan;
    Guard g, next;
    Out r = Next(g, next, Cov{ 0, 12000 }, 0, /*wantEndMs*/ 1000000, /*segLenMs*/ 4000, /*endIndex*/ 3);
    CHECK(r.atEnd);
    CHECK_EQ(r.idx, -1);
}

/// A7 — uncovered reference: with nothing buffered at all, plan the segment containing the reference position.
/// Then, once that exact segment has already been fetched and STILL does not cover the reference (a grid that
/// simply does not match the content — lastCovEnd stays -1), asking again cannot help: no plan, wait for the
/// reference to move or a structural reset.
static void Test_A7_UncoveredReference()
{
    using namespace fgpr::plan;
    const int32_t segLenMs = 4000;
    const int64_t wantEndMs = 60000;
    const int32_t endIndex = 1000;

    Guard g1, next1;
    Out r1 = Next(g1, next1, Cov{ -1, -1 }, /*refMs*/ 5000, wantEndMs, segLenMs, endIndex);
    CHECK_EQ(r1.idx, SegOf(5000, segLenMs));
    CHECK_EQ(r1.idx, 1);

    Guard g2, next2;
    g2.lastIdx = 1;
    g2.lastCovEnd = -1;   // segment 1 already fetched, still doesn't cover refMs=5000
    Out r2 = Next(g2, next2, Cov{ -1, -1 }, 5000, wantEndMs, segLenMs, endIndex);
    CHECK_EQ(r2.idx, -1);
}

/// A8 — segLenMs <= 0 (a measured-length grid before the first segment is parsed) must plan nothing and must not
/// divide by zero / crash.
static void Test_A8_NonPositiveSegLen()
{
    using namespace fgpr::plan;
    Guard g, next;
    Out r1 = Next(g, next, Cov{ 0, 1000 }, 0, 60000, /*segLenMs*/ 0, 1000);
    CHECK_EQ(r1.idx, -1);
    Out r2 = Next(g, next, Cov{ 0, 1000 }, 0, 60000, /*segLenMs*/ -5, 1000);
    CHECK_EQ(r2.idx, -1);
}

/// A9 — coverage that GREW (not shrank, not stagnant) never resets the guard: a floor established by an earlier
/// stagnation step must still be enforced on the very next call even though coverage moved forward, proving the
/// Reset rule (Test_A1) is specifically a SHRINK detector, not "anything other than exact stagnation".
static void Test_A9_GrowthNeverResets()
{
    using namespace fgpr::plan;
    const int32_t segLenMs = 4000;
    const int64_t wantEndMs = 60000;
    const int32_t endIndex = 1000;

    Guard g, next;
    g.lastIdx = 1;
    g.lastCovEnd = 8000;
    g.floorIdx = 5;   // an earlier stagnation step already parked the floor well ahead

    Out r = Next(g, next, Cov{ 0, 9000 }, 0, wantEndMs, segLenMs, endIndex);   // GREW from 8000 to 9000
    // The pre-floor recompute (SegOf(9000+2000, 4000) = 2) is still below the floor — the floor must still win,
    // proving the guard was NOT reset by this growth.
    CHECK_EQ(r.idx, 5);
    CHECK(!r.guardStepped);
    CHECK_EQ(next.floorIdx, 5);
    CHECK_EQ(next.lastIdx, g.lastIdx);
    CHECK_EQ(next.lastCovEnd, g.lastCovEnd);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  B. fgpr::SpliceSamples / fgpr::IsAscending
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// B10 — pure append: incoming's decode span is entirely ahead of everything buffered. `next` (well ahead of the
/// whole existing buffer) is untouched, nothing is removed, ascending order holds.
static void Test_B10_PureAppend()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    size_t next = 5;
    auto incoming = MakeSegment(1, 1000, 25, 800, false);
    const size_t incomingCount = incoming.size();

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(incoming), false);
    CHECK(!out.cutCoverage);
    CHECK(!out.stale);
    CHECK_EQ(out.removed, (size_t)0);
    CHECK_EQ(out.added, incomingCount);
    CHECK_EQ(next, (size_t)5);
    CHECK(fgpr::IsAscending(buf));
    CHECK_EQ(buf.size(), (size_t)40);
}

/// B11 — THE non-truncating straddle: the buffer already holds seg0+seg1, the cursor sits mid-seg1, and seg1 is
/// re-appended (e.g. a demand-driven re-fetch that overlaps what is already there). Everything at/behind the
/// cursor's PRESENTATION time must survive untouched: `samples[next]` keeps its timeTicks, nothing with an earlier
/// timeTicks lands at or after `next`, ascending order holds, and the sample count is unchanged (a like-for-like
/// re-fetch, not a growth or a shrink).
static void Test_B11_NonTruncatingStraddle()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));
    const size_t originalSize = buf.size();

    size_t next = 25;   // mid-seg1
    const uint64_t deliveredTime = buf[next].timeTicks;

    auto reFetch = MakeSegment(1, 1000, 25, 800, false);   // deterministic: identical to what's already buffered
    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(reFetch), false);

    CHECK(!out.stale);
    CHECK_EQ(buf.size(), originalSize);
    CHECK_EQ(next, (size_t)25);
    CHECK_EQ(buf[next].timeTicks, deliveredTime);
    CHECK(fgpr::IsAscending(buf));
    for (size_t i = next; i < buf.size(); i++)
        CHECK(buf[i].timeTicks >= deliveredTime);
}

/// B12 — straddle where every incoming sample was ALREADY DELIVERED (decoded before the cursor): the buffer/cursor must
/// be left completely untouched (`added == 0`). What makes this the straddle branch and not a simple "wholly behind"
/// replace is the run's decode SPAN: its last sample's duration reaches past the cursor's decode time, so `hi > next`.
/// "Delivered" is a decode-order fact (delivery walks the decode-ordered vector) — see SegmentStore.h's ordering note.
static void Test_B12_StraddleAllBehindCursor()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));
    const size_t originalSize = buf.size();

    size_t next = 25;   // mid-seg1; decode grid is regular (40 ticks/sample) regardless of presentation reordering
    const uint64_t cursorDecode = buf[next].decodeTicks;
    const uint64_t cursorTime = buf[next].timeTicks;

    auto mk = [](uint64_t decodeT, uint64_t timeT) {
        cenc::Sample s;
        s.decodeTicks = decodeT;
        s.timeTicks = timeT;
        s.durTicks = 40;
        s.encrypted = false;
        s.data.assign(16, 0x11);
        return s;
    };
    std::vector<cenc::Sample> incoming;
    const uint64_t behindTime = cursorTime > 0 ? cursorTime - 1 : 0;
    incoming.push_back(mk(cursorDecode - 80, behindTime));   // decoded before the cursor: already delivered
    incoming.push_back(mk(cursorDecode - 40, behindTime));   // decoded before the cursor: already delivered…
    incoming.back().durTicks = 120;                          // …but its span reaches past the cursor => the straddle branch

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(incoming), false);
    CHECK_EQ(out.added, (size_t)0);
    CHECK_EQ(buf.size(), originalSize);
    CHECK_EQ(next, (size_t)25);
    CHECK_EQ(buf[next].timeTicks, cursorTime);
}

/// B13 — TRUNCATING at a boundary AHEAD of the cursor: a representation switch fetches a new seg3 while segments
/// 0..5 are buffered and the cursor is still inside seg1. Everything from seg3's start to the tail of the buffer
/// (seg3's old body AND seg4/seg5) must be erased and replaced by the new seg3 alone; cutCoverage is set because
/// real coverage was discarded; the cursor, well behind the cut, is untouched.
static void Test_B13_TruncatingAheadOfCursor()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    for (int i = 1; i <= 5; i++) AppendSegment(buf, MakeSegment(i, 1000, 25, 800, false));
    CHECK_EQ(buf.size(), (size_t)120);   // 6 segments * 20 samples

    size_t next = 25;   // mid-seg1
    auto newSeg3 = MakeSegment(3, 1000, 25, 800, false);   // same rung boundary (decode continues, keyframe-first)
    const uint64_t newSeg3FrontTime = newSeg3.front().timeTicks;

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(newSeg3), true);
    CHECK(!out.stale);
    CHECK(out.cutCoverage);
    CHECK_EQ(next, (size_t)25);
    CHECK(fgpr::IsAscending(buf));
    CHECK_EQ(buf.size(), (size_t)80);   // seg0+seg1+seg2 (60) + new seg3 (20); seg4/seg5 gone
    // Nothing beyond the new seg3's own coverage remains.
    for (auto const& s : buf) CHECK(s.timeTicks < newSeg3FrontTime + 800);
}

/// B14 — THE RE-DELIVERY INCIDENT: a truncating splice whose replacement starts BEHIND what the cursor already
/// consumed (a boundary computed before a slow GET landed, stale by the time the response arrives — see
/// SpliceSamples's own doc comment). Must be refused wholesale: `stale == true`, the buffer is byte-for-byte
/// unchanged (every timeTicks/decodeTicks compared, not just the size), `next` unchanged.
static void Test_B14_RedeliveryIncidentIsStale()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));
    const std::vector<cenc::Sample> before = buf;   // deep copy for the byte-for-byte comparison

    size_t next = 15;   // inside seg0, well ahead of the buffer's front
    auto newSeg0 = MakeSegment(0, 1000, 25, 800, false);   // front timeTicks = 0, behind samples[15]

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(newSeg0), true);
    CHECK(out.stale);
    CHECK_EQ(next, (size_t)15);
    CHECK_EQ(buf.size(), before.size());
    for (size_t i = 0; i < buf.size(); i++)
    {
        CHECK_EQ(buf[i].timeTicks, before[i].timeTicks);
        CHECK_EQ(buf[i].decodeTicks, before[i].decodeTicks);
    }
}

/// B15 — truncating on a DRAINED buffer (`next == size`): a replacement landing at/after the last delivered sample
/// is accepted (this is the normal "fetch the next rung ahead of playback" case, not a re-delivery); one landing
/// BEFORE the last delivered sample is still stale, by the drained-buffer rule (`samples.back().timeTicks`).
static void Test_B15_TruncatingOnDrainedBuffer()
{
    // Accepted: incoming starts at/after the drained buffer's last sample.
    {
        std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
        AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));
        size_t next = buf.size();   // drained
        auto newSeg2 = MakeSegment(2, 1000, 25, 800, false);   // front timeTicks 1600 >= back's 1560

        fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(newSeg2), true);
        CHECK(!out.stale);
    }
    // Stale: incoming starts before the drained buffer's last delivered sample.
    {
        std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
        AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));
        size_t next = buf.size();   // drained
        auto reSeg1 = MakeSegment(1, 1000, 25, 800, false);   // front timeTicks 800 < back's 1560

        fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(reSeg1), true);
        CHECK(out.stale);
    }
}

/// B16 — wholly-behind replace (a non-truncating splice whose incoming decode span sits entirely before the
/// cursor) keeps `next` pointing at the SAME LOGICAL sample: the array index shifts by (added - removed), and the
/// content at the new index is verified, not just the arithmetic.
static void Test_B16_WhollyBehindReplaceKeepsLogicalCursor()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);   // 20 samples, decode/time 0..800
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));               // +20, decode/time 800..1600

    size_t next = 25;   // seg1 local index 5
    const uint64_t cursorTimeBefore = buf[next].timeTicks;

    // A thinner re-fetch of segment 0's time range (10 fps instead of 25): fewer samples, same decode/time origin.
    auto thinSeg0 = MakeSegment(0, 1000, 10, 800, false);   // 8 samples, decode/time 0..800
    const size_t thinCount = thinSeg0.size();
    CHECK(thinCount < (size_t)20);

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(thinSeg0), false);
    CHECK_EQ(out.removed, (size_t)20);
    CHECK_EQ(out.added, thinCount);
    CHECK_EQ(next, (size_t)25 - 20 + thinCount);
    CHECK_EQ(buf[next].timeTicks, cursorTimeBefore);
    CHECK(fgpr::IsAscending(buf));
}

/// B17 — a short, non-truncating replacement (the incoming run's presentation coverage ends earlier than what it
/// replaced) sets cutCoverage even though nothing straddles the cursor. Modelled with an artificially long tail
/// sample already in the buffer (the "old" coverage really did reach further than the new run's own presentation
/// end), matching SpliceSamples's own doc comment: "a short demux re-fetch covering less than what it overwrote".
static void Test_B17_ShortReplacementCutsCoverage()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    auto seg1 = MakeSegment(1, 1000, 25, 800, false);
    seg1.back().durTicks = 440;   // old seg1's reported coverage actually reached 1560+440=2000
    AppendSegment(buf, std::move(seg1));
    CHECK_EQ(buf.back().timeTicks + buf.back().durTicks, (uint64_t)2000);

    size_t next = 0;
    auto newSeg1 = MakeSegment(1, 1000, 25, 800, false);   // normal 40-tick tail: ends at 1600, short of 2000

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(newSeg1), false);
    CHECK(out.cutCoverage);
    CHECK_EQ(out.removed, (size_t)20);
    CHECK_EQ(out.added, (size_t)20);
}

/// B18a-d — every one of B10-B13 again with bframes = true (decode order != presentation order within each GOP).
/// The decode grid stays regular (MakeSegment always steps decodeTicks by frameDurTicks regardless of `bframes`),
/// so the array-position (lo/hi) search behaves identically to the non-bframe cases; what these prove is that
/// IsAscending (decodeTicks-based) survives the presentation reordering, and that ComputeBufferedPairs does not
/// mistake a B-frame's dipping timeTicks for a hole.

static void Test_B18a_BFrames_PureAppend()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, true);
    size_t next = 5;
    auto incoming = MakeSegment(1, 1000, 25, 800, true);
    const size_t incomingCount = incoming.size();

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(incoming), false);
    CHECK(!out.cutCoverage);
    CHECK(!out.stale);
    CHECK_EQ(out.added, incomingCount);
    CHECK_EQ(next, (size_t)5);
    CHECK(fgpr::IsAscending(buf));
    CHECK_EQ(buf.size(), (size_t)40);

    int64_t pairs[4] = {};
    int n = fgpr::ComputeBufferedPairs(buf, 1000, pairs, 2);
    CHECK_EQ(n, 1);   // one contiguous range despite the B-frame timeTicks dip inside every GOP
}

static void Test_B18b_BFrames_NonTruncatingStraddle()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, true);
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, true));
    const size_t originalSize = buf.size();

    size_t next = 25;
    const uint64_t deliveredTime = buf[next].timeTicks;

    auto reFetch = MakeSegment(1, 1000, 25, 800, true);
    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(reFetch), false);

    CHECK(!out.stale);
    CHECK_EQ(buf.size(), originalSize);
    CHECK_EQ(next, (size_t)25);
    CHECK_EQ(buf[next].timeTicks, deliveredTime);
    CHECK(fgpr::IsAscending(buf));
    // Inserted samples respect the DECODE floor (delivery order), not the presentation one: the B frames decoded after
    // the cursor's P frame present EARLIER than it and are still undelivered — dropping them was a real bug here.
    const uint64_t deliveredDecode = buf[next].decodeTicks;
    for (size_t i = next; i < next + out.added; i++)
        CHECK(buf[i].decodeTicks >= deliveredDecode);
}

static void Test_B18c_BFrames_StraddleAllBehindCursor()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, true);
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, true));
    const size_t originalSize = buf.size();

    size_t next = 25;   // decode grid is regular even with bframes=true
    const uint64_t cursorDecode = buf[next].decodeTicks;
    const uint64_t cursorTime = buf[next].timeTicks;

    auto mk = [](uint64_t decodeT, uint64_t timeT) {
        cenc::Sample s;
        s.decodeTicks = decodeT;
        s.timeTicks = timeT;
        s.durTicks = 40;
        s.data.assign(16, 0x22);
        return s;
    };
    std::vector<cenc::Sample> incoming;
    const uint64_t behindTime = cursorTime > 0 ? cursorTime - 1 : 0;
    incoming.push_back(mk(cursorDecode - 80, behindTime));
    incoming.push_back(mk(cursorDecode - 40, behindTime));
    incoming.back().durTicks = 120;   // span reaches past the cursor's decode time => the straddle branch, nothing survives

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(incoming), false);
    CHECK_EQ(out.added, (size_t)0);
    CHECK_EQ(buf.size(), originalSize);
    CHECK_EQ(next, (size_t)25);
}

static void Test_B18d_BFrames_TruncatingAheadOfCursor()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, true);
    for (int i = 1; i <= 5; i++) AppendSegment(buf, MakeSegment(i, 1000, 25, 800, true));
    CHECK_EQ(buf.size(), (size_t)120);

    size_t next = 25;
    auto newSeg3 = MakeSegment(3, 1000, 25, 800, true);

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(newSeg3), true);
    CHECK(!out.stale);
    CHECK(out.cutCoverage);
    CHECK_EQ(next, (size_t)25);
    CHECK(fgpr::IsAscending(buf));
    CHECK_EQ(buf.size(), (size_t)80);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  C. Coverage helpers — ComputeBufferedPairs, ContiguousAheadMs, TrimBehindByTime
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// C19 — ComputeBufferedPairs: a hole (seg0 + seg2, seg1 missing) reports two pairs with the right ms bounds; the
/// fully contiguous buffer (seg0+seg1+seg2) collapses to one.
static void Test_C19_ComputeBufferedPairs()
{
    auto seg0 = MakeSegment(0, 1000, 25, 800, false);
    auto seg1 = MakeSegment(1, 1000, 25, 800, false);
    auto seg2 = MakeSegment(2, 1000, 25, 800, false);

    std::vector<cenc::Sample> withHole = seg0;
    withHole.insert(withHole.end(), seg2.begin(), seg2.end());

    int64_t pairs[8] = {};
    int n = fgpr::ComputeBufferedPairs(withHole, 1000, pairs, 4);
    CHECK_EQ(n, 2);
    CHECK_EQ(pairs[0], (int64_t)0);
    CHECK_EQ(pairs[1], (int64_t)800);
    CHECK_EQ(pairs[2], (int64_t)1600);
    CHECK_EQ(pairs[3], (int64_t)2400);

    std::vector<cenc::Sample> contiguous = seg0;
    contiguous.insert(contiguous.end(), seg1.begin(), seg1.end());
    contiguous.insert(contiguous.end(), seg2.begin(), seg2.end());

    int n2 = fgpr::ComputeBufferedPairs(contiguous, 1000, pairs, 4);
    CHECK_EQ(n2, 1);
    CHECK_EQ(pairs[0], (int64_t)0);
    CHECK_EQ(pairs[1], (int64_t)2400);
}

/// C20 — ContiguousAheadMs: with the cursor inside seg0, a hole (seg1 missing) stops the window at seg0's own end
/// — it must NOT span the hole to "see" seg2's coverage. The fully contiguous buffer reaches all the way to seg2's
/// end. Out-of-range `next` and a zero timescale both report 0 without touching the vector.
static void Test_C20_ContiguousAheadMs()
{
    auto seg0 = MakeSegment(0, 1000, 25, 800, false);
    auto seg2 = MakeSegment(2, 1000, 25, 800, false);
    std::vector<cenc::Sample> withHole = seg0;
    withHole.insert(withHole.end(), seg2.begin(), seg2.end());

    const size_t cursorInSeg0 = 5;   // timeTicks = 200
    CHECK_EQ(withHole[cursorInSeg0].timeTicks, (uint64_t)200);

    int64_t aheadWithHole = fgpr::ContiguousAheadMs(withHole, cursorInSeg0, 1000);
    CHECK_EQ(aheadWithHole, (int64_t)600);   // to seg0's own end (800), not spanning the hole into seg2

    auto seg1 = MakeSegment(1, 1000, 25, 800, false);
    std::vector<cenc::Sample> contiguous = seg0;
    contiguous.insert(contiguous.end(), seg1.begin(), seg1.end());
    contiguous.insert(contiguous.end(), seg2.begin(), seg2.end());

    int64_t aheadContiguous = fgpr::ContiguousAheadMs(contiguous, cursorInSeg0, 1000);
    CHECK_EQ(aheadContiguous, (int64_t)2200);   // to the very end of seg2

    int64_t aheadOutOfRange = fgpr::ContiguousAheadMs(contiguous, contiguous.size() + 10, 1000);
    CHECK_EQ(aheadOutOfRange, (int64_t)0);

    int64_t aheadZeroTimescale = fgpr::ContiguousAheadMs(contiguous, cursorInSeg0, 0);
    CHECK_EQ(aheadZeroTimescale, (int64_t)0);
}

/// C21 — TrimBehindByTime: a pure time-window trim (a generous byte budget) sets outByteBudgetCut == false and
/// keeps `next` pointing at the same logical sample; a tiny byte budget (isolated from the time window by a huge
/// retainBehindMs, so rule 1 alone would drop nothing) sets it true. Neither ever drops at/after `next` — verified
/// here by content identity, not just index arithmetic.
static void Test_C21_TrimBehindByTime()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));   // 40 samples, time 0..1600

    size_t next = 25;   // time 1000
    CHECK_EQ(buf[next].timeTicks, (uint64_t)1000);
    uint64_t bytesCounter = fgpr::SampleFootprint(buf);

    // Pure time-window trim: a huge byte budget so rule 2 (the byte cap) never fires.
    bool byteBudgetCut = true;   // deliberately pre-set to a wrong value: the call must clear it
    size_t dropped = fgpr::TrimBehindByTime(buf, next, 1000, /*retainBehindMs*/ 500,
                                            /*budgetBytes*/ (uint64_t)1 << 40, bytesCounter, &byteBudgetCut);
    CHECK(!byteBudgetCut);
    CHECK(dropped > 0);
    CHECK_EQ(buf[next].timeTicks, (uint64_t)1000);   // same logical sample, new index

    // Isolate the byte cap: a huge retainBehindMs makes rule 1's cutoff 0 (nothing qualifies), so any drop below is
    // rule 2's doing alone.
    uint64_t bytesCounter2 = fgpr::SampleFootprint(buf);
    bool byteBudgetCut2 = false;
    size_t dropped2 = fgpr::TrimBehindByTime(buf, next, 1000, /*retainBehindMs*/ 1000000,
                                             /*budgetBytes*/ 1, bytesCounter2, &byteBudgetCut2);
    CHECK(byteBudgetCut2);
    CHECK(dropped2 > 0);
    CHECK_EQ(buf[next].timeTicks, (uint64_t)1000);   // still the same logical sample
}

/// C21b — TrimBehindByTime's RESERVE: a store filled to exactly its budget with history the time window says to keep
/// must still evict the oldest history behind the playhead to leave `reserveBytes` free for the next fetch — the
/// 2026-09-22 deadlock (1080p: the cap refused every fetch at `>= budget`, rule 2 evicted nothing at `> budget`, the
/// 30 s window kept everything, playback starved at the end of the buffer). Nothing at/after `next` ever yields: a
/// buffer that is all look-ahead evicts nothing and the cap legitimately holds.
static void Test_C21b_TrimReserveEvictsInsideTheRetainWindow()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));   // 40 samples, time 0..1600
    size_t next = 25;                                             // playhead at 1000 ms: 25 samples behind, 15 ahead
    const uint64_t total = fgpr::SampleFootprint(buf);
    // Keyframes are bigger than delta frames in MakeSegment, so size the reserve as EXACTLY the first three samples'
    // footprint: rule 2 then stops the moment they are gone (held == cap), and the count is exact.
    const uint64_t first3 = fgpr::SampleFootprint(buf[0]) + fgpr::SampleFootprint(buf[1]) + fgpr::SampleFootprint(buf[2]);
    const uint64_t perSample = fgpr::SampleFootprint(buf[1]);

    // Exactly AT the budget, everything inside a huge retention window: the old rule frees nothing.
    uint64_t bytes = total;
    bool cut = false;
    CHECK_EQ(fgpr::TrimBehindByTime(buf, next, 1000, /*retainBehindMs*/ 1000000, /*budget*/ total, bytes, &cut, /*reserve*/ 0), (size_t)0);
    CHECK(!cut);

    // A reserve of three samples' worth: exactly the oldest three behind the playhead go, flagged as a byte cut, and
    // the playhead sample is untouched.
    const uint64_t before = buf[next].timeTicks;
    size_t dropped = fgpr::TrimBehindByTime(buf, next, 1000, 1000000, total, bytes, &cut, /*reserve*/ first3);
    CHECK_EQ(dropped, (size_t)3);
    CHECK(cut);
    CHECK_EQ(next, (size_t)22);
    CHECK_EQ(buf[next].timeTicks, before);
    CHECK_EQ(bytes, total - first3);

    // All look-ahead (playhead at the first sample): the reserve cannot evict what MF has not been handed yet.
    size_t next0 = 0;
    uint64_t bytes0 = fgpr::SampleFootprint(buf);
    CHECK_EQ(fgpr::TrimBehindByTime(buf, next0, 1000, 1000000, bytes0, bytes0, &cut, /*reserve*/ perSample * 10), (size_t)0);
    CHECK(!cut);

    // A reserve larger than the whole budget degrades to "evict all history", never to an underflow.
    size_t nextAll = 10;
    uint64_t bytesAll = fgpr::SampleFootprint(buf);
    CHECK_EQ(fgpr::TrimBehindByTime(buf, nextAll, 1000, 1000000, bytesAll, bytesAll, &cut, /*reserve*/ bytesAll * 2), (size_t)10);
    CHECK_EQ(nextAll, (size_t)0);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  main
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// A22 — plan::ReachesEnd: a covered range that reaches the duration (within the contiguity tolerance) is the END of
/// the track, so the feeder marks the stream complete instead of leaving a starved request parked forever
/// (2026-09-22: the audio stream of a finished video held MEEndOfPresentation for 108 s).
static void Test_A22_ReachesEnd()
{
    CHECK(fgpr::plan::ReachesEnd(217400, 217523, 500));     // last samples end a few frames short of the rounded duration
    CHECK(fgpr::plan::ReachesEnd(217523, 217523, 0));
    CHECK(fgpr::plan::ReachesEnd(220000, 217523, 0));       // past it (the last segment overhangs)
    CHECK(!fgpr::plan::ReachesEnd(213000, 217523, 500));    // a whole segment short: not the end
    CHECK(!fgpr::plan::ReachesEnd(217400, 0, 500));         // unknown duration: never
    CHECK(!fgpr::plan::ReachesEnd(-1, 217523, 500));        // uncovered reference: never
}

int main()
{
    struct TestCase { const char* name; void (*fn)(); };
    const TestCase tests[] = {
        { "A1_IncidentReplay", Test_A1_IncidentReplay },
        { "A2_GenuineStagnation", Test_A2_GenuineStagnation },
        { "A3_FloorStagnationAdvancesAgain", Test_A3_FloorStagnationAdvancesAgain },
        { "A4_PlanThenCancelDoesNotCommit", Test_A4_PlanThenCancelDoesNotCommit },
        { "A5_Satisfied", Test_A5_Satisfied },
        { "A6_AtEnd", Test_A6_AtEnd },
        { "A7_UncoveredReference", Test_A7_UncoveredReference },
        { "A8_NonPositiveSegLen", Test_A8_NonPositiveSegLen },
        { "A9_GrowthNeverResets", Test_A9_GrowthNeverResets },
        { "A22_ReachesEnd", Test_A22_ReachesEnd },
        { "C21b_TrimReserveEvictsInsideTheRetainWindow", Test_C21b_TrimReserveEvictsInsideTheRetainWindow },
        { "B10_PureAppend", Test_B10_PureAppend },
        { "B11_NonTruncatingStraddle", Test_B11_NonTruncatingStraddle },
        { "B12_StraddleAllBehindCursor", Test_B12_StraddleAllBehindCursor },
        { "B13_TruncatingAheadOfCursor", Test_B13_TruncatingAheadOfCursor },
        { "B14_RedeliveryIncidentIsStale", Test_B14_RedeliveryIncidentIsStale },
        { "B15_TruncatingOnDrainedBuffer", Test_B15_TruncatingOnDrainedBuffer },
        { "B16_WhollyBehindReplaceKeepsLogicalCursor", Test_B16_WhollyBehindReplaceKeepsLogicalCursor },
        { "B17_ShortReplacementCutsCoverage", Test_B17_ShortReplacementCutsCoverage },
        { "B18a_BFrames_PureAppend", Test_B18a_BFrames_PureAppend },
        { "B18b_BFrames_NonTruncatingStraddle", Test_B18b_BFrames_NonTruncatingStraddle },
        { "B18c_BFrames_StraddleAllBehindCursor", Test_B18c_BFrames_StraddleAllBehindCursor },
        { "B18d_BFrames_TruncatingAheadOfCursor", Test_B18d_BFrames_TruncatingAheadOfCursor },
        { "C19_ComputeBufferedPairs", Test_C19_ComputeBufferedPairs },
        { "C20_ContiguousAheadMs", Test_C20_ContiguousAheadMs },
        { "C21_TrimBehindByTime", Test_C21_TrimBehindByTime },
    };

    const int failBefore = g_fail;
    (void)failBefore;
    for (auto const& t : tests)
    {
        const int before = g_fail;
        t.fn();
        if (g_fail != before) std::printf("  ^-- in %s\n", t.name);
    }

    const int total = (int)(sizeof(tests) / sizeof(tests[0]));
    std::printf("%d test(s), %d failure(s)\n", total, g_fail);
    return g_fail;
}
