// FeedTests.cpp — a dependency-free console test exe for the PlayReady video feeder's pure arithmetic and buffer
// algorithms: fgpr::plan::Next (FeedPlan.h) and the SegmentStore.h sample-list algorithms (SpliceSamples,
// IsAscending, ComputeBufferedPairs, ContiguousAheadMs, TrimBehindByTime, CanSeekToIn, PickStartKeyframe,
// ResumeStartMs, SlabPool).
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
#include "../HandoverPolicy.h"
#include "../FrameHealthPolicy.h"
#include "../LicensePolicy.h"
#include "../EventRing.h"
#include "../WorkQueue.h"
#include "../OpmWindow.h"
#include <cstdlib>

#include <atomic>
#include <chrono>
#include <sstream>
#include <stdexcept>
#include <thread>

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

/// C19b — BufferedPairsCache (F037): the feeder asks for the same pairs five or six times a job, so one walk serves every
/// ask until the stream's mutation counter moves. Same answer as ComputeBufferedPairs, truncated by `capPairs` with the
/// TOTAL still returned, a counter bump refreshes it, and a request past the cache's capacity walks directly.
static void Test_C19b_BufferedPairsCacheFollowsTheVersion()
{
    auto seg0 = MakeSegment(0, 1000, 25, 800, false);
    auto seg1 = MakeSegment(1, 1000, 25, 800, false);
    auto seg2 = MakeSegment(2, 1000, 25, 800, false);
    std::vector<cenc::Sample> buf = seg0;
    buf.insert(buf.end(), seg2.begin(), seg2.end());   // seg1 missing: two ranges

    fgpr::BufferedPairsCache cache;
    int64_t got[8] = {}, want[8] = {};
    int n = cache.Get(buf, 1000, 1, got, 4);
    const int w = fgpr::ComputeBufferedPairs(buf, 1000, want, 4);
    CHECK_EQ(n, w);
    CHECK_EQ(n, 2);
    for (int k = 0; k < 4; k++) CHECK_EQ(got[k], want[k]);

    // The hole is filled behind the cache's back WITHOUT a counter bump: the cached answer is served (proof that a
    // repeated ask does not walk), and the bump is what refreshes it.
    buf.insert(buf.begin() + (std::ptrdiff_t)seg0.size(), seg1.begin(), seg1.end());
    n = cache.Get(buf, 1000, 1, got, 4);
    CHECK_EQ(n, 2);
    n = cache.Get(buf, 1000, 2, got, 4);
    CHECK_EQ(n, 1);
    CHECK_EQ(got[0], (int64_t)0);
    CHECK_EQ(got[1], (int64_t)2400);

    // A cap below the total writes only that many pairs but still reports the total.
    std::vector<cenc::Sample> three = MakeSegment(0, 1000, 25, 800, false);
    for (int i : { 2, 4 })
    {
        auto seg = MakeSegment(i, 1000, 25, 800, false);
        three.insert(three.end(), seg.begin(), seg.end());
    }
    int64_t two[8] = { -7, -7, -7, -7, -7, -7, -7, -7 };
    fgpr::BufferedPairsCache tinyCache;
    CHECK_EQ(tinyCache.Get(three, 1000, 1, two, 2), 3);
    CHECK_EQ(two[0], (int64_t)0);
    CHECK_EQ(two[3], (int64_t)2400);   // second pair: seg 2 = [1600, 2400)
    CHECK_EQ(two[4], (int64_t)-7);     // a third pair is never written
    CHECK_EQ(tinyCache.Get(three, 1000, 1, nullptr, 0), 3);

    // More pairs than the cache holds (70 isolated segments): the direct walk, equal to ComputeBufferedPairs.
    std::vector<cenc::Sample> many;
    for (int i = 0; i < 140; i += 2)
    {
        auto seg = MakeSegment(i, 1000, 25, 800, false);
        many.insert(many.end(), seg.begin(), seg.end());
    }
    std::vector<int64_t> big(200, 0), direct(200, 0);
    fgpr::BufferedPairsCache bigCache;
    CHECK_EQ(bigCache.Get(many, 1000, 1, big.data(), 100), 70);
    CHECK_EQ(fgpr::ComputeBufferedPairs(many, 1000, direct.data(), 100), 70);
    CHECK(big == direct);
    CHECK_EQ(bigCache.Get(many, 1000, 1, big.data(), 64), 70);   // the cached shape still reports the real total
}

/// C20b — ContiguousReach (F037) answers EXACTLY what the full ContiguousAheadMs walk answers, for every cursor, in
/// delivery order, after jumps in both directions, and across a buffer mutation: a B-frame buffer (a reordered P frame
/// ends after the B frame that follows it in decode order, the case the incremental run has to replay) with and without
/// a hole, plus a randomised buffer with holes and local reordering.
static void ExpectReachMatchesWalk(const std::vector<cenc::Sample>& buf, uint64_t timescale, uint64_t ver,
                                   fgpr::ContiguousReach& cache, size_t next, const char* what)
{
    const int64_t want = fgpr::ContiguousAheadMs(buf, next, timescale);
    const int64_t got = cache.Ahead(buf, next, timescale, ver);
    if (got != want)
        std::printf("  reach mismatch (%s) at next=%zu: incremental %lld, walk %lld\n", what, next, (long long)got, (long long)want);
    CHECK_EQ(got, want);
}

static void Test_C20b_ContiguousReachMatchesTheWalk()
{
    for (int bframes = 0; bframes < 2; bframes++)
    {
        for (int withHole = 0; withHole < 2; withHole++)
        {
            std::vector<cenc::Sample> buf;
            for (int i : { 0, 1, withHole ? 3 : 2, withHole ? 4 : 3 })
            {
                auto seg = MakeSegment(i, 1000, 25, 800, bframes != 0);
                buf.insert(buf.end(), seg.begin(), seg.end());
            }
            fgpr::ContiguousReach cache;
            uint64_t ver = 1;
            for (size_t n = 0; n < buf.size(); n++) ExpectReachMatchesWalk(buf, 1000, ver, cache, n, "ascending");
            const size_t jumps[] = { 3, 41, 7, 7, 60, 2, 79, 0, 33, 33, 34, 79, 12, 55 };
            for (size_t n : jumps) ExpectReachMatchesWalk(buf, 1000, ver, cache, n, "jumps");
            for (size_t n = buf.size(); n-- > 0;) ExpectReachMatchesWalk(buf, 1000, ver, cache, n, "descending");

            // An append (the stream bumps its counter): the run is re-anchored, not extended from a stale hole.
            auto more = MakeSegment(6, 1000, 25, 800, bframes != 0);
            buf.insert(buf.end(), more.begin(), more.end());
            ver++;
            for (size_t n = 0; n < buf.size(); n++) ExpectReachMatchesWalk(buf, 1000, ver, cache, n, "after append");
        }
    }

    // Out of range and a zero timescale answer 0 like the walk.
    std::vector<cenc::Sample> tiny = MakeSegment(0, 1000, 25, 800, false);
    fgpr::ContiguousReach tinyReach;
    CHECK_EQ(tinyReach.Ahead(tiny, tiny.size(), 1000, 1), (int64_t)0);
    CHECK_EQ(tinyReach.Ahead(tiny, 0, 0, 1), (int64_t)0);
    CHECK_EQ(tinyReach.Ahead(tiny, 5, 1000, 1), fgpr::ContiguousAheadMs(tiny, 5, 1000));

    // Randomised: holes, local reordering, long and short durations. A fixed LCG keeps it deterministic.
    uint64_t rng = 0x2545F4914F6CDD1Dull;
    auto draw = [&rng](uint64_t mod) { rng = rng * 6364136223846793005ull + 1442695040888963407ull; return (rng >> 33) % mod; };
    std::vector<cenc::Sample> rnd;
    uint64_t t = 0;
    for (int i = 0; i < 400; i++)
    {
        cenc::Sample s;
        t += 20 + draw(41);
        if (draw(37) == 0) t += 500 + draw(700);   // a hole (the tolerance is 500 ticks at timescale 1000)
        s.decodeTicks = (uint64_t)i * 40;
        s.timeTicks = t;
        s.durTicks = draw(9) == 0 ? 150 + draw(300) : 20 + draw(61);
        s.data.assign(16, 0x11);
        rnd.push_back(std::move(s));
    }
    for (size_t i = 0; i + 1 < rnd.size(); i++)
        if (draw(3) == 0) std::swap(rnd[i].timeTicks, rnd[i + 1].timeTicks);   // decode-order reordering
    for (int pass = 0; pass < 4; pass++)
    {
        fgpr::ContiguousReach rc;
        for (size_t n = 0; n < rnd.size(); n++)
            ExpectReachMatchesWalk(rnd, 1000, 1, rc, pass == 0 ? n : (size_t)draw(rnd.size()), "random");
    }
}

/// A10b — DemandBelowWhenSatisfied (F037): after a satisfied plan the hook never fires at the reach it was armed at, wakes
/// once a segment of playback has been consumed (never later than the target allows), and is never 0 ("the store's
/// target" to SetDemandBelowMs).
static void Test_A10b_DemandHysteresisAfterASatisfiedPlan()
{
    using namespace fgpr::plan;
    const int64_t target = 60000;
    const int32_t seg = 4000;
    CHECK_EQ(DemandBelowWhenSatisfied(62000, target, seg), (int64_t)58000);     // one segment below where it stands
    CHECK_EQ(DemandBelowWhenSatisfied(60000, target, seg), (int64_t)56000);
    CHECK_EQ(DemandBelowWhenSatisfied(200000, target, seg), target);            // far above the target: wake AT the target
    CHECK_EQ(DemandBelowWhenSatisfied(3000, target, seg), (int64_t)1);          // less than a segment: only a drain wakes it
    CHECK_EQ(DemandBelowWhenSatisfied(0, target, seg), (int64_t)1);
    CHECK_EQ(DemandBelowWhenSatisfied(61000, target, 0), target);               // an unknown segment length cannot go negative
    for (int64_t ahead = 1; ahead <= 200000; ahead += 997)
    {
        const int64_t below = DemandBelowWhenSatisfied(ahead, target, seg);
        CHECK(below >= 1);
        CHECK(below <= target);
        CHECK(!(ahead < below));   // the hook (ahead < below) stays silent at the reach it was armed at: no per-sample wake
    }
}

/// C21 — TrimBehindByTime: a pure time-window trim (a generous byte budget) sets outByteBudgetCut == false and
/// keeps `next` pointing at the same logical sample; a tiny byte budget (isolated from the time window by a huge
/// retainBehindMs, so rule 1 alone would drop nothing) sets it true. Neither ever drops at/after `next` — verified
/// here by content identity, not just index arithmetic. The time trim is keyframe-aligned (C24): the window here is
/// narrow enough (100 ms) that the first GOP (one 800 ms segment in this fixture) falls out whole.
static void Test_C21_TrimBehindByTime()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));   // 40 samples, time 0..1600

    size_t next = 25;   // time 1000
    CHECK_EQ(buf[next].timeTicks, (uint64_t)1000);
    uint64_t bytesCounter = fgpr::SampleFootprint(buf);

    // Pure time-window trim: a huge byte budget so rule 2 (the byte cap) never fires.
    bool byteBudgetCut = true;   // deliberately pre-set to a wrong value: the call must clear it
    size_t dropped = fgpr::TrimBehindByTime(buf, next, 1000, /*retainBehindMs*/ 100,
                                            /*budgetBytes*/ (uint64_t)1 << 40, bytesCounter, &byteBudgetCut);
    CHECK(!byteBudgetCut);
    CHECK_EQ(dropped, (size_t)20);                   // seg0 whole: the cut stops on seg1's keyframe, not mid-GOP at 22
    CHECK(buf[0].keyframe);
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

    // A reserve of three samples' worth: the byte rule must free at least the oldest three behind the playhead. They end
    // mid-GOP, so the cut moves FORWARD to the next keyframe the cursor has not reached (sample 20, seg1's first) rather
    // than leave a P frame at the head: it frees MORE than the reserve (the byte rule wins, never the other way
    // round), is flagged as a byte cut, and the playhead sample is untouched.
    const uint64_t before = buf[next].timeTicks;
    uint64_t first20 = 0;
    for (size_t i = 0; i < 20; i++) first20 += fgpr::SampleFootprint(buf[i]);
    size_t dropped = fgpr::TrimBehindByTime(buf, next, 1000, 1000000, total, bytes, &cut, /*reserve*/ first3);
    CHECK_EQ(dropped, (size_t)20);
    CHECK(cut);
    CHECK_EQ(next, (size_t)5);
    CHECK_EQ(buf[next].timeTicks, before);
    CHECK_EQ(bytes, total - first20);
    CHECK(buf[0].keyframe);
    CHECK(bytes + first3 <= total);   // at least the reserve was freed

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
//  D. Protected seek and segment store correctness (F034 / F040 / F046)
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// How many samples of `buf` present inside [fromMs, toMs) (timescale 1000).
static size_t CountInWindow(const std::vector<cenc::Sample>& buf, uint64_t fromMs, uint64_t toMs)
{
    size_t n = 0;
    for (auto const& s : buf) if (s.timeTicks >= fromMs && s.timeTicks < toMs) n++;
    return n;
}

/// The 4 s / 25 fps buffer of segments [first, last] (timescale 1000): segment k covers [k*4000, (k+1)*4000) ms.
static std::vector<cenc::Sample> MakeBuffer(int first, int last, bool bframes)
{
    std::vector<cenc::Sample> buf;
    for (int i = first; i <= last; i++) AppendSegment(buf, MakeSegment(i, 1000, 25, 4000, bframes));
    return buf;
}

/// C23 — F034: a backward seek to a position the buffer does not hold. The buffer holds [92 s,152 s) with the delivery
/// cursor at 120 s; the user seeks to 10 s. WITHOUT a flush the landed [8 s,12 s) run is inserted behind the cursor and
/// the very next trim (cutoff = 120 s - 30 s) erases it in the same call - the feeder then believes the target is
/// buffered and Start() has nothing to start from. WITH the flush (CencMediaStream::FlushForSeek: empty the samples,
/// cursor 0, byte ledger 0 - the pure equivalent below) the run lands in an empty buffer, the trim has nothing behind
/// a cursor at 0 to evict, and the keyframe pick finds the run's own keyframe.
static void Test_C23_BackwardSeekRunSurvivesTrim()
{
    // Root cause, pinned: no flush -> the run is history behind the cursor and the window trim erases it.
    {
        std::vector<cenc::Sample> buf = MakeBuffer(23, 37, false);
        size_t next = 700;   // 120 s
        CHECK_EQ(buf[next].timeTicks, (uint64_t)120000);
        fgpr::SpliceSamples(buf, next, MakeSegment(2, 1000, 25, 4000, false), false);   // the [8 s,12 s) run lands at index 0
        CHECK_EQ(CountInWindow(buf, 8000, 12000), (size_t)100);
        uint64_t bytes = fgpr::SampleFootprint(buf);
        bool cut = false;
        fgpr::TrimBehindByTime(buf, next, 1000, 30000, (uint64_t)1 << 40, bytes, &cut);
        CHECK_EQ(CountInWindow(buf, 8000, 12000), (size_t)0);
    }

    // The flush: the run survives, and Start finds its keyframe.
    {
        std::vector<cenc::Sample> buf;   // FlushForSeek: the old [92 s,152 s) buffer and its cursor are gone
        size_t next = 0;
        fgpr::SpliceSamples(buf, next, MakeSegment(2, 1000, 25, 4000, false), false);
        uint64_t bytes = fgpr::SampleFootprint(buf);
        bool cut = true;
        const size_t dropped = fgpr::TrimBehindByTime(buf, next, 1000, 30000, (uint64_t)1 << 40, bytes, &cut);
        CHECK_EQ(dropped, (size_t)0);
        CHECK(!cut);
        CHECK_EQ(CountInWindow(buf, 8000, 12000), (size_t)100);
        const size_t start = fgpr::PickStartKeyframe(buf, /*target 10 s*/ 10000, /*tolerance*/ 500);
        CHECK_EQ(start, (size_t)0);
        CHECK(buf[start].keyframe);
        CHECK_EQ(buf[start].timeTicks, (uint64_t)8000);
    }
}

/// C24 — F034 / F046: after ANY trim the buffer starts on a keyframe (or is empty), B-frames on and off, across time
/// windows, byte-forced cuts and playhead hints. The one allowed exception is a cap that cannot afford any
/// keyframe-led history while no keyframe sits between the cut and the cursor - covered separately below.
static void Test_C24_TrimKeepsKeyframeHead()
{
    for (int bf = 0; bf < 2; bf++)
    {
        const bool bframes = bf != 0;
        const int64_t windows[] = { 0, 100, 700, 1500, 6000, 1000000 };
        const size_t cursors[] = { 130, 250, 333, 401 };   // mid-GOP and on-GOP cursors in the 4 s / 100-sample segments
        for (int64_t window : windows)
            for (size_t cursor : cursors)
                for (int tight = 0; tight < 4; tight++)
                {
                    // tight 0: no byte pressure; 1..3: the reserve forces about 30 / 150 / 280 samples' worth out. A reserve
                    // that eats all history up to within a GOP of the cursor is the allowed exception (below), so skip it.
                    const size_t forced = tight == 0 ? 0 : (tight == 1 ? 30 : (tight == 2 ? 150 : 280));
                    if (forced + 100 > cursor) continue;
                    std::vector<cenc::Sample> buf = MakeBuffer(0, 5, bframes);   // 600 samples, 0..24 s
                    size_t next = cursor;
                    const uint64_t total = fgpr::SampleFootprint(buf);
                    uint64_t bytes = total;
                    uint64_t reserve = 0;
                    for (size_t i = 0; i < forced; i++) reserve += fgpr::SampleFootprint(buf[i]);
                    const uint64_t beforeTicks = buf[next].timeTicks;
                    bool cut = false;
                    const size_t dropped = fgpr::TrimBehindByTime(buf, next, 1000, window, total, bytes, &cut, reserve);
                    CHECK(buf.empty() || buf[0].keyframe);
                    CHECK_EQ(buf[next].timeTicks, beforeTicks);           // the cursor still names the same sample
                    CHECK(dropped <= cursor);                             // never at/after the cursor
                    CHECK_EQ(bytes, fgpr::SampleFootprint(buf));          // the ledger follows the erase
                    CHECK(fgpr::IsAscending(buf));
                }
    }

    // The playhead hint: the cursor sits 1.2 s past the playhead, in the NEXT GOP. Measured from the cursor the window
    // cut would land in the playhead's GOP; measured from the playhead (and aligned back to its keyframe) the playhead's
    // keyframe survives, so a re-attach at the playhead has something to start from.
    {
        std::vector<cenc::Sample> buf = MakeBuffer(0, 3, false);   // keyframes at 0, 4000, 8000, 12000 ms
        size_t next = 205;                                          // 8200 ms: 200 ms into GOP 2
        uint64_t bytes = fgpr::SampleFootprint(buf);
        const uint64_t playhead = 7000;                             // still in GOP 1 (4000..8000)
        std::vector<cenc::Sample> noHint = buf;
        size_t nextNoHint = next;
        uint64_t bytesNoHint = bytes;
        fgpr::TrimBehindByTime(buf, next, 1000, /*window*/ 100, (uint64_t)1 << 40, bytes, nullptr, 0, playhead);
        CHECK(buf[0].keyframe);
        CHECK_EQ(buf[0].timeTicks, (uint64_t)4000);                 // GOP 1's keyframe is kept
        CHECK(fgpr::PickStartKeyframe(buf, playhead, 500) < buf.size());
        fgpr::TrimBehindByTime(noHint, nextNoHint, 1000, 100, (uint64_t)1 << 40, bytesNoHint);
        CHECK_EQ(noHint[0].timeTicks, (uint64_t)8000);              // cursor-anchored: the playhead's GOP is gone
        CHECK(fgpr::PickStartKeyframe(noHint, playhead, 500) == noHint.size());
    }

    // The byte rule beats the alignment: a cap so tight that no keyframe-led history is affordable and no keyframe lies
    // between the cut and the cursor leaves the sample-exact cut (a non-keyframe head) rather than keep bytes.
    {
        std::vector<cenc::Sample> buf = MakeBuffer(0, 1, false);   // keyframes at 0 and 4000 ms
        size_t next = 120;                                          // 20 samples into GOP 1
        uint64_t bytes = fgpr::SampleFootprint(buf);
        bool cut = false;
        fgpr::TrimBehindByTime(buf, next, 1000, 1000000, /*budget*/ 1, bytes, &cut);
        CHECK(cut);
        CHECK_EQ(next, (size_t)0);                                  // everything behind the cursor went, GOP 1's keyframe included
        CHECK(!buf[0].keyframe);
    }
}

/// C25 — F034 / F040: the byte rule never evicts the ahead run. A flushed buffer holding only the landed target run has
/// its cursor at 0 (Start repositions to the run's first keyframe, index 0): whatever the budget, nothing is at or
/// behind the cursor to evict, nothing is flagged as a byte cut (the feeder's guard must not reset), and the run lives.
static void Test_C25_ByteRuleNeverEvictsAheadRun()
{
    for (uint64_t budget : { (uint64_t)1, (uint64_t)4096, (uint64_t)1 << 40 })
    {
        std::vector<cenc::Sample> buf;
        size_t next = 0;
        fgpr::SpliceSamples(buf, next, MakeSegment(2, 1000, 25, 4000, false), false);
        fgpr::SpliceSamples(buf, next, MakeSegment(3, 1000, 25, 4000, false), false);   // forward bytes >> the budget
        uint64_t bytes = fgpr::SampleFootprint(buf);
        bool cut = true;
        CHECK_EQ(fgpr::TrimBehindByTime(buf, next, 1000, 30000, budget, bytes, &cut, /*reserve*/ 1 << 20), (size_t)0);
        CHECK(!cut);
        CHECK_EQ(buf.size(), (size_t)200);
        CHECK_EQ(next, (size_t)0);
    }
}

/// C26 — PickStartKeyframe (what Start(reposition) delivers from): the keyframe at/before the target, else one a
/// composition offset after it, else NOTHING - never sample 0 by default.
static void Test_C26_PickStartKeyframe()
{
    std::vector<cenc::Sample> buf = MakeBuffer(1, 2, false);   // keyframes at 4000 and 8000 ms
    const uint64_t tol = 500;
    CHECK_EQ(fgpr::PickStartKeyframe(buf, 4000, tol), (size_t)0);       // exact
    CHECK_EQ(fgpr::PickStartKeyframe(buf, 6000, tol), (size_t)0);       // mid-GOP: the GOP's keyframe
    CHECK_EQ(fgpr::PickStartKeyframe(buf, 8000, tol), (size_t)100);     // on the second GOP's keyframe
    CHECK_EQ(fgpr::PickStartKeyframe(buf, 11000, tol), (size_t)100);
    CHECK_EQ(fgpr::PickStartKeyframe(buf, 3800, tol), (size_t)0);       // a first frame just past the target: within tolerance
    CHECK_EQ(fgpr::PickStartKeyframe(buf, 3000, tol), buf.size());      // a whole second early: nothing can serve it
    CHECK_EQ(fgpr::PickStartKeyframe(buf, 0, tol), buf.size());

    // A buffer whose head was cut mid-GOP: the target inside that GOP has nothing to start from (the old fallback
    // delivered sample 0, a P frame).
    std::vector<cenc::Sample> headless(buf.begin() + 10, buf.end());
    CHECK_EQ(fgpr::PickStartKeyframe(headless, 4800, tol), headless.size());
    CHECK_EQ(fgpr::PickStartKeyframe(headless, 8200, tol), (size_t)90);

    CHECK_EQ(fgpr::PickStartKeyframe(std::vector<cenc::Sample>(), 1000, tol), (size_t)0);   // empty: size() == 0 == "none"
}

/// C27 — F046: the re-attach resume point. The playhead's keyframe was trimmed away, so a Start at the playhead would
/// have begun on a P frame; the resume moves to the first buffered keyframe (at most one GOP). A position the buffer
/// does not hold, or one a keyframe can serve, is left alone.
static void Test_C27_ResumeStartMs()
{
    std::vector<cenc::Sample> buf = MakeBuffer(1, 3, false);   // keyframes at 4000, 8000, 12000
    std::vector<cenc::Sample> headless(buf.begin() + 25, buf.end());   // GOP 1's keyframe gone; head at 5000 ms
    CHECK_EQ(fgpr::ResumeStartMs(headless, 1000, 5600), (int64_t)8000);   // in the range, no keyframe at/before: the next one
    CHECK_EQ(fgpr::ResumeStartMs(headless, 1000, 7900), (int64_t)7900);   // a keyframe 100 ms after serves it (tolerance)
    CHECK_EQ(fgpr::ResumeStartMs(headless, 1000, 8000), (int64_t)8000);   // already servable
    CHECK_EQ(fgpr::ResumeStartMs(headless, 1000, 9500), (int64_t)9500);   // keyframe at/before exists
    CHECK_EQ(fgpr::ResumeStartMs(headless, 1000, 40000), (int64_t)40000); // not buffered: the feeder fetches it
    CHECK_EQ(fgpr::ResumeStartMs(headless, 1000, 1000), (int64_t)1000);   // before the range: not buffered either
    CHECK_EQ(fgpr::ResumeStartMs(buf, 1000, 5000), (int64_t)5000);        // keyframe 4000 at/before
    CHECK_EQ(fgpr::ResumeStartMs(std::vector<cenc::Sample>(), 1000, 5000), (int64_t)5000);
}

/// C28 — F040: the pool's idle scratch is bounded by twice the largest buffer it has been handed (not by the whole
/// session budget), and SegmentStore::MediaBytes() leaves it out of the media cap.
static void Test_C28_PoolCapAndMediaBytes()
{
    fgpr::SlabPool pool;
    pool.Configure((uint64_t)32 << 20);
    std::vector<uint8_t> a; a.reserve(1 << 20);
    const uint64_t cap = a.capacity();
    pool.Give(std::move(a));
    for (int i = 0; i < 4; i++)
    {
        std::vector<uint8_t> b; b.reserve(1 << 20);
        pool.Give(std::move(b));
    }
    CHECK(pool.PooledBytes() <= 2 * cap);
    CHECK(pool.PooledBytes() >= cap);

    auto store = std::make_shared<fgpr::SegmentStore>();
    store->Configure(30000, 60000, (uint64_t)32 << 20);
    store->videoBytes.store(10, std::memory_order_relaxed);
    store->audioBytes.store(5, std::memory_order_relaxed);
    std::vector<uint8_t> scratch; scratch.reserve(1 << 20);
    store->pool.Give(std::move(scratch));
    CHECK_EQ(store->MediaBytes(), (uint64_t)15);
    CHECK(store->Bytes() > store->MediaBytes());
    CHECK_EQ(store->VideoBudget() + store->AudioBudget(), store->budgetBytes);
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

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  D. Buffer-preserving representation switches: append at the buffered end, the retain boundary, format generations
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

static void StampGen(std::vector<cenc::Sample>& run, uint16_t gen)
{
    for (auto& s : run) s.repGen = gen;
}

/// D29 — APPEND: a representation switch whose first segment is the one right after everything buffered. The truncating
/// splice has nothing at or past the landing point to erase: removed == 0, freedBytes == 0, and - the point of it -
/// cutCoverage stays false, so the feeder's CutGen-driven ResetTrackState does not fire and nothing is refilled. The
/// cursor (still inside the old representation) and every old sample are untouched.
static void Test_D29_AppendAtTheBufferedEndErasesNothing()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    for (int i = 1; i <= 2; i++) AppendSegment(buf, MakeSegment(i, 1000, 25, 800, false));
    CHECK_EQ(buf.size(), (size_t)60);
    const std::vector<cenc::Sample> before = buf;

    size_t next = 25;   // mid-seg1: well behind the buffered end
    auto seg3 = MakeSegment(3, 1000, 25, 800, false);   // the NEW representation's first segment
    StampGen(seg3, 1);

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(seg3), true);
    CHECK(!out.stale);
    CHECK_EQ(out.removed, (size_t)0);
    CHECK_EQ(out.freedBytes, (uint64_t)0);
    CHECK(!out.cutCoverage);
    CHECK_EQ(out.added, (size_t)20);
    CHECK_EQ(out.at, (size_t)60);
    CHECK_EQ(buf.size(), (size_t)80);
    CHECK_EQ(next, (size_t)25);
    CHECK(fgpr::IsAscending(buf));
    for (size_t i = 0; i < 60; i++)
    {
        CHECK_EQ(buf[i].timeTicks, before[i].timeTicks);
        CHECK_EQ(buf[i].repGen, (uint16_t)0);
    }
    for (size_t i = 60; i < 80; i++) CHECK_EQ(buf[i].repGen, (uint16_t)1);
}

/// D30 — RETAIN: an upswitch that keeps 25 s of the old representation lands at the segment 25 s past the playhead, and
/// the truncating splice removes ONLY what lies past that point (the old representation's 15 s tail); the 25 s the
/// playhead can still reach stay exactly as they were. cutCoverage is set (real coverage was discarded), and the byte
/// ledger gets exactly the freed footprint back.
static void Test_D30_RetainBoundaryRemovesOnlyWhatLiesPastIt()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 1000, false);   // 1 s segments, 25 samples each
    for (int i = 1; i < 40; i++) AppendSegment(buf, MakeSegment(i, 1000, 25, 1000, false));
    CHECK_EQ(buf.size(), (size_t)1000);   // 40 s buffered
    uint64_t expectedFreed = 0;
    for (size_t i = 625; i < buf.size(); i++) expectedFreed += fgpr::SampleFootprint(buf[i]);

    size_t next = 0;   // the playhead at t = 0
    auto seg25 = MakeSegment(25, 1000, 25, 1000, false);   // playhead + 25 s
    StampGen(seg25, 1);

    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(seg25), true);
    CHECK(!out.stale);
    CHECK_EQ(out.at, (size_t)625);
    CHECK_EQ(out.removed, (size_t)375);   // seg25..seg39 of the old representation, nothing before
    CHECK_EQ(out.freedBytes, expectedFreed);
    CHECK(out.cutCoverage);
    CHECK_EQ(buf.size(), (size_t)650);
    CHECK_EQ(next, (size_t)0);
    CHECK(fgpr::IsAscending(buf));
    for (size_t i = 0; i < 625; i++) CHECK_EQ(buf[i].repGen, (uint16_t)0);          // the retained 25 s: old representation
    for (size_t i = 625; i < buf.size(); i++) CHECK_EQ(buf[i].repGen, (uint16_t)1); // then only the new one
}

/// D31 — FormatGenAt: the format generation delivery is about to hand over. It stays the OLD generation until the cursor
/// crosses the splice, flips at the first new-representation sample (where MEStreamFormatChanged is raised), reports the
/// fallback when the buffer is drained, and goes back to the old generation on a BACKWARD seek across the splice (the old
/// format is re-announced).
static void Test_D31_FormatGenAtFollowsTheDeliveryCursorAcrossTheSplice()
{
    std::vector<cenc::Sample> buf = MakeSegment(0, 1000, 25, 800, false);
    AppendSegment(buf, MakeSegment(1, 1000, 25, 800, false));   // 40 old-representation samples (generation 0)
    auto seg2 = MakeSegment(2, 1000, 25, 800, false);
    StampGen(seg2, 1);
    size_t next = 10;
    fgpr::SpliceOutcome out = fgpr::SpliceSamples(buf, next, std::move(seg2), true);   // appended behind the old buffer
    CHECK(!out.stale);
    CHECK_EQ(buf.size(), (size_t)60);

    CHECK_EQ(fgpr::FormatGenAt(buf, 0, 0), (uint16_t)0);
    CHECK_EQ(fgpr::FormatGenAt(buf, 10, 0), (uint16_t)0);
    CHECK_EQ(fgpr::FormatGenAt(buf, 39, 0), (uint16_t)0);    // the last old sample: still the old format
    CHECK_EQ(fgpr::FormatGenAt(buf, 40, 0), (uint16_t)1);    // the first new sample: the crossing
    CHECK_EQ(fgpr::FormatGenAt(buf, 59, 0), (uint16_t)1);
    CHECK_EQ(fgpr::FormatGenAt(buf, buf.size(), 1), (uint16_t)1);   // drained: nothing new to announce
    CHECK_EQ(fgpr::FormatGenAt(buf, 5, 1), (uint16_t)0);     // a backward seek across the splice re-announces the old one
}

/// D32 — RepBook: generations are registered (the newest becomes the append generation), found, pruned when nothing
/// refers to them (the append and delivered generations are never pruned), and a new number never collides with one in use.
static void Test_D32_RepBookRegistersPrunesAndNeverReusesAGenerationInUse()
{
    cenc::InitInfo low;
    low.width = 640;
    low.height = 360;
    cenc::InitInfo high;
    high.width = 1920;
    high.height = 1080;

    fgpr::RepBook book;
    book.Register(0, -1, low);
    CHECK_EQ(book.appendGen, (uint16_t)0);
    CHECK_EQ(book.NextFreeGen(), (uint16_t)1);
    book.Register(1, 3, high);
    CHECK_EQ(book.appendGen, (uint16_t)1);
    const fgpr::RepFormat* f1 = book.Find(1);
    CHECK(f1 != nullptr);
    if (f1)
    {
        CHECK_EQ(f1->index, 3);
        CHECK_EQ(f1->info.width, (uint32_t)1920);
    }
    CHECK(book.Find(2) == nullptr);

    // Generation 0 is still referenced by a buffered sample: kept.
    std::vector<cenc::Sample> oldRun = MakeSegment(0, 1000, 25, 800, false);
    book.deliveredGen = 0;
    book.Prune(oldRun);
    CHECK(book.Find(0) != nullptr);

    // Nothing refers to it any more and delivery has left it: pruned. The append generation stays.
    std::vector<cenc::Sample> newRun = MakeSegment(1, 1000, 25, 800, false);
    StampGen(newRun, 1);
    book.deliveredGen = 1;
    book.Prune(newRun);
    CHECK(book.Find(0) == nullptr);
    CHECK(book.Find(1) != nullptr);

    // The delivered generation survives with no sample referring to it (a drained buffer), and so does the newest.
    book.Register(2, 5, high);
    book.deliveredGen = 1;
    std::vector<cenc::Sample> none;
    book.Prune(none);
    CHECK(book.Find(1) != nullptr);
    CHECK(book.Find(2) != nullptr);

    // The counter is 16 bits: it wraps, and skips a number a held generation still uses.
    fgpr::RepBook wrapped;
    wrapped.appendGen = 65535;
    CHECK_EQ(wrapped.NextFreeGen(), (uint16_t)0);
    fgpr::RepFormat zero;
    zero.gen = 0;
    wrapped.formats.push_back(zero);
    CHECK_EQ(wrapped.NextFreeGen(), (uint16_t)1);
}

/// D33 — the generation is per SAMPLE state: it survives the time/byte trim of the head and the wholesale move a
/// re-attach does (TakeSamples hands the vector to a fresh stream).
static void Test_D33_RepGenSurvivesTrimAndRelocation()
{
    std::vector<cenc::Sample> buf;
    for (int i = 0; i < 3; i++)
    {
        auto seg = MakeSegment(i, 1000, 25, 800, false);
        StampGen(seg, (uint16_t)i);
        AppendSegment(buf, std::move(seg));
    }
    CHECK_EQ(buf.size(), (size_t)60);

    size_t next = 45;   // inside seg2 (generation 2)
    uint64_t bytes = fgpr::SampleFootprint(buf);
    const size_t dropped = fgpr::TrimBehindByTime(buf, next, 1000, /*retainBehindMs*/ 100, (uint64_t)1 << 40, bytes);
    CHECK(dropped > 0);
    CHECK_EQ(buf.front().repGen, (uint16_t)2);
    CHECK_EQ(buf[next].repGen, (uint16_t)2);
    CHECK_EQ(buf.back().repGen, (uint16_t)2);

    std::vector<cenc::Sample> relocated = std::move(buf);   // what TakeSamples does for a re-attach
    CHECK_EQ(relocated.back().repGen, (uint16_t)2);
    CHECK_EQ(relocated.front().repGen, (uint16_t)2);
}

/// E34 - the native side's own seek (the carried start on the engine timeline, the CANPLAY fallback) is tagged with the
/// user seekSeq (F024): its SEEKED is swallowed and clears the `seeking` flag it raised, it never swallows a USER seek's
/// SEEKED, and a user seek issued to the engine afterwards cancels the tag.
static void Test_E34_InternalSeekTag()
{
    using fgpr::plan::InternalSeek;

    // No native seek outstanding: every SEEKED is a user seek's.
    {
        InternalSeek t;
        auto v = t.OnSeeked(0);
        CHECK(!v.internal);
        CHECK(v.clearsSeeking);
    }
    // The carried start's own SEEKED: swallowed once, and the flag it raised (SEEKING sets seeking=1) is cleared, which
    // the old counter never did - the snapshot read Seeking until the next user seek completed.
    {
        InternalSeek t;
        t.Issue(3);
        auto v = t.OnSeeked(3);
        CHECK(v.internal);
        CHECK(v.clearsSeeking);
        CHECK(!t.pending);
        auto next = t.OnSeeked(3);   // the SEEKED after it belongs to somebody else
        CHECK(!next.internal);
        CHECK(next.clearsSeeking);
    }
    // A user seek was POSTED after the native one but not applied yet: the native SEEKED is still swallowed, but the
    // `seeking` flag belongs to the user seek and stays up until ITS SEEKED.
    {
        InternalSeek t;
        t.Issue(3);
        auto v = t.OnSeeked(4);
        CHECK(v.internal);
        CHECK(!v.clearsSeeking);
    }
    // A user seek APPLIED to the engine cancels the tag (the engine aborts the native seek in flight; the one SEEKED that
    // follows is the user's): it must never be swallowed.
    {
        InternalSeek t;
        t.Issue(3);
        t.Cancel();
        auto v = t.OnSeeked(4);
        CHECK(!v.internal);
        CHECK(v.clearsSeeking);
    }
    // Re-issuing (the fallback correction after the attach-time seek) simply re-tags.
    {
        InternalSeek t;
        t.Issue(1);
        t.Issue(2);
        auto v = t.OnSeeked(2);
        CHECK(v.internal);
        CHECK(v.clearsSeeking);
    }
}

/// F35 - a failed segment GET is a STALL with a capped exponential back-off, never a three-strikes end of the track.
static void Test_F35_RetryDelayBacksOffAndCaps()
{
    using fgpr::plan::RetryDelayMs;
    CHECK_EQ(RetryDelayMs(1), 500);
    CHECK_EQ(RetryDelayMs(2), 1000);
    CHECK_EQ(RetryDelayMs(3), 2000);
    CHECK_EQ(RetryDelayMs(4), 4000);
    CHECK_EQ(RetryDelayMs(5), 8000);
    CHECK_EQ(RetryDelayMs(6), 8000);          // capped: it keeps retrying at the cap for as long as the plan wants the segment
    CHECK_EQ(RetryDelayMs(1000000), 8000);    // and a very long stall can neither overflow nor shrink the wait
    CHECK_EQ(RetryDelayMs(0), 500);           // defensive: a count below 1 is the first retry
    CHECK_EQ(RetryDelayMs(-3), 500);
}

/// F35 - only a 404 / 410 at or beyond the segment count (or with no count to bound it) ends the track. Everything the
/// old code latched on (any other 4xx, a third transient failure) is retried: an expired signed URL (403) after a long
/// pause, a 429 / 408, a CDN 5xx, a transport failure (status 0) and a 404 inside the manifest.
static void Test_F35_OnlyANotFoundAtTheEndEndsTheTrack()
{
    using fgpr::plan::EndsTrack;
    // A bounded presentation of 100 segments (indices 0..99).
    CHECK(EndsTrack(404, 100, 100));
    CHECK(EndsTrack(410, 100, 100));
    CHECK(EndsTrack(404, 130, 100));
    CHECK(!EndsTrack(404, 99, 100));          // a hole inside the manifest is a CDN problem, not the end
    CHECK(!EndsTrack(410, 0, 100));
    CHECK(!EndsTrack(403, 100, 100));         // forbidden at the end is still not "no such segment": the URL expired
    CHECK(!EndsTrack(401, 50, 100));
    CHECK(!EndsTrack(403, 50, 100));
    CHECK(!EndsTrack(408, 100, 100));
    CHECK(!EndsTrack(429, 100, 100));
    CHECK(!EndsTrack(416, 100, 100));
    CHECK(!EndsTrack(500, 100, 100));
    CHECK(!EndsTrack(503, 100, 100));
    CHECK(!EndsTrack(0, 100, 100));           // transport failure
    CHECK(!EndsTrack(200, 100, 100));         // an empty 200 body
    // No count anywhere (descriptor and duration both unknown: INT32_MAX): the missing segment is the only end signal.
    CHECK(EndsTrack(404, 7, INT32_MAX));
    CHECK(EndsTrack(410, 0, INT32_MAX));
    CHECK(!EndsTrack(403, 7, INT32_MAX));
    CHECK(!EndsTrack(500, 7, INT32_MAX));
}

/// F30 - WAITING / STALLED / BUFFERINGSTARTED enter once (edge only); PLAYING, SEEKED, BUFFERINGENDED, or a TIMEUPDATE
/// whose position moved on end the wait; a TIMEUPDATE that repeats the stalled position does not.
static void Test_G36_WaitGate()
{
    using fgpr::plan::WaitGate;
    // Nothing to clear when nothing waits.
    {
        WaitGate g;
        CHECK(!g.Clear());
        CHECK(!g.OnTimeUpdate(99999));
    }
    // Edge only: a second WAITING while waiting reports nothing, and keeps the ORIGINAL position the wait began at.
    {
        WaitGate g;
        CHECK(g.Enter(10000));
        CHECK(g.waiting);
        CHECK(!g.Enter(10200));
        CHECK_EQ(g.sinceMs, (int64_t)10000);
        CHECK(g.Clear());
        CHECK(!g.waiting);
        CHECK(!g.Clear());
    }
    // The stalled clock repeating (or jittering within kAdvanceMs of) its position does not end the wait; moving on does.
    {
        WaitGate g;
        CHECK(g.Enter(10000));
        CHECK(!g.OnTimeUpdate(10000));
        CHECK(!g.OnTimeUpdate(10000 + WaitGate::kAdvanceMs - 1));
        CHECK(g.waiting);
        CHECK(g.OnTimeUpdate(10000 + WaitGate::kAdvanceMs));
        CHECK(!g.waiting);
        CHECK(!g.OnTimeUpdate(20000));         // already resumed: no second edge
    }
    // A backward position (the clock was reset by a seek) is not "advancing".
    {
        WaitGate g;
        CHECK(g.Enter(10000));
        CHECK(!g.OnTimeUpdate(500));
        CHECK(g.waiting);
    }
    // Reset (attach / detach) forgets a wait without reporting it; the next wait is a fresh edge.
    {
        WaitGate g;
        CHECK(g.Enter(10000));
        g.Reset();
        CHECK(!g.waiting);
        CHECK(!g.Clear());
        CHECK(g.Enter(3000));
        CHECK_EQ(g.sinceMs, (int64_t)3000);
    }
}

/// F45 / F11 - every MF_MEDIAKEY_STATUS is mapped: USABLE usable, EXPIRED expired, INTERNAL_ERROR / RELEASED /
/// OUTPUT_NOT_ALLOWED kill the key from Pending or Usable (a usable key still wins over a dead one for ANOTHER key of the
/// same license), the two output restrictions are reported (never fatal), STATUS_PENDING and unknown values decide
/// nothing; a failed license stays failed; a Pending license is stale from kPendingDeadlineMs on.
static void Test_H37_LicensePolicy()
{
    using namespace fgpr::licensing;
    auto fold = [](std::initializer_list<int32_t> statuses) {
        KeyStatusSummary s;
        for (int32_t v : statuses) s.Add(v);
        return s;
    };

    // The three dead statuses each kill a pending and a usable license, and name themselves in the HRESULT.
    for (int32_t dead : { kStatusInternalError, kStatusReleased, kStatusOutputNotAllowed })
    {
        KeyStatusSummary s = fold({ dead });
        CHECK(IsDeadStatus(dead));
        CHECK_EQ(s.deadStatus, dead);
        CHECK(NextAction(s, 0) == KeyAction::Kill);
        CHECK(NextAction(s, 1) == KeyAction::Kill);
        CHECK(NextAction(s, 2) == KeyAction::None);       // an expired license is not resurrected into a different dead state
        CHECK(NextAction(s, (int32_t)0x80004005) == KeyAction::None);   // already failed: never failed twice
        CHECK_EQ(DeadHr(dead), (int32_t)(0x80048000u + (uint32_t)dead));
        CHECK(DeadHr(dead) < 0);
    }

    // USABLE: announced once, revives an expired license, beats a dead status on another key, never revives a failed one.
    {
        KeyStatusSummary s = fold({ kStatusUsable });
        CHECK(NextAction(s, 0) == KeyAction::BecomeUsable);
        CHECK(NextAction(s, 2) == KeyAction::BecomeUsable);
        CHECK(NextAction(s, 1) == KeyAction::None);
        CHECK(NextAction(s, (int32_t)0x80048005) == KeyAction::None);
        KeyStatusSummary mixed = fold({ kStatusReleased, kStatusUsable });
        CHECK(NextAction(mixed, 0) == KeyAction::BecomeUsable);
        CHECK(NextAction(mixed, 1) == KeyAction::None);
    }

    // EXPIRED expires a pending or usable license, and only those.
    {
        KeyStatusSummary s = fold({ kStatusExpired });
        CHECK(NextAction(s, 0) == KeyAction::BecomeExpired);
        CHECK(NextAction(s, 1) == KeyAction::BecomeExpired);
        CHECK(NextAction(s, 2) == KeyAction::None);
    }

    // Output restrictions are reported but never change the license's state; RESTRICTED beats DOWNSCALED.
    {
        KeyStatusSummary s = fold({ kStatusUsable, kStatusOutputDownscaled });
        CHECK_EQ(s.restriction, kStatusOutputDownscaled);
        CHECK(NextAction(s, 1) == KeyAction::None);
        KeyStatusSummary r = fold({ kStatusOutputDownscaled, kStatusOutputRestricted });
        CHECK_EQ(r.restriction, kStatusOutputRestricted);
        CHECK(NextAction(r, 1) == KeyAction::None);
        CHECK(NextAction(r, 0) == KeyAction::None);
        KeyStatusSummary r2 = fold({ kStatusOutputRestricted, kStatusOutputDownscaled });
        CHECK_EQ(r2.restriction, kStatusOutputRestricted);
        CHECK_EQ(fold({ kStatusUsable }).restriction, 0);
    }

    // STATUS_PENDING, an empty batch and an unknown future value decide nothing.
    {
        CHECK(NextAction(fold({ kStatusPending }), 0) == KeyAction::None);
        CHECK(NextAction(fold({}), 0) == KeyAction::None);
        CHECK(NextAction(fold({ 99 }), 1) == KeyAction::None);
        CHECK_EQ(fold({ kStatusPending }).deadStatus, -1);
    }

    // The Pending deadline: the managed cache's 8 s, inclusive at the boundary.
    CHECK(!PendingStale(0));
    CHECK(!PendingStale(kPendingDeadlineMs - 1));
    CHECK(PendingStale(kPendingDeadlineMs));
    CHECK(PendingStale(kPendingDeadlineMs * 4));
    CHECK_EQ(kPendingDeadlineMs, (int64_t)8000);
}

/// F15 - the armed unload of the engine's source: it fires once, and an attach (Cancel) or a later detach (a new Arm)
/// makes every earlier timer item a no-op, so a successor's attach that took the engine over is never followed by an empty
/// load, and a re-armed unload is not run twice.
static void Test_I38_ReleaseGate()
{
    using namespace fgpr::handover;
    CHECK(kReleaseGraceMs > 0);
    {
        ReleaseGate g;
        CHECK(!g.Fire(0));                  // never armed
        const uint64_t t = g.Arm();
        CHECK(g.pending);
        CHECK(g.Fire(t));                   // the grace ran out with no successor: unload
        CHECK(!g.pending);
        CHECK(!g.Fire(t));                  // ...exactly once
    }
    {
        ReleaseGate g;
        const uint64_t t = g.Arm();
        g.Cancel();                         // a successor's attach took the engine over
        CHECK(!g.pending);
        CHECK(!g.Fire(t));                  // the timer item that fires afterwards does nothing
    }
    {
        ReleaseGate g;
        const uint64_t first = g.Arm();
        const uint64_t second = g.Arm();    // another detach before the first timer fired
        CHECK(first != second);
        CHECK(!g.Fire(first));              // the stale item is ignored...
        CHECK(g.pending);                   // ...and does not disarm the live one
        CHECK(g.Fire(second));
    }
    {
        ReleaseGate g;
        const uint64_t first = g.Arm();
        g.Cancel();
        const uint64_t second = g.Arm();    // attach, then detach again: only the newest timer may unload
        CHECK(!g.Fire(first));
        CHECK(g.Fire(second));
    }
}

/// F17 - a stopped feeder is joined OFF the caller: Reap returns while the thread is still running, the tail runs after
/// the join, Drain is the join point, Shutdown (and the destructor) leave no thread behind, and a Reap after Shutdown
/// joins inline so nothing is ever left running.
static void Test_I39_FeederReaper()
{
    using namespace fgpr::handover;
    using namespace std::chrono_literals;
    {
        FeederReaper reaper;
        std::atomic<bool> release{ false };
        std::atomic<bool> finished{ false };
        std::atomic<int> tails{ 0 };
        std::thread feeder([&] {
            while (!release.load()) std::this_thread::sleep_for(1ms);
            std::this_thread::sleep_for(5ms);
            finished.store(true);
        });
        reaper.Reap(std::move(feeder), [&] {
            if (finished.load()) tails.fetch_add(1);   // only counts when the join really happened first
        });
        CHECK(!finished.load());                       // Reap returned with the feeder still running: no join on the caller
        CHECK_EQ(tails.load(), 0);
        release.store(true);
        reaper.Drain();
        CHECK(finished.load());
        CHECK_EQ(tails.load(), 1);
    }
    {
        FeederReaper reaper;
        std::atomic<int> done{ 0 };
        std::atomic<int> tails{ 0 };
        for (int i = 0; i < 4; ++i)
            reaper.Reap(std::thread([&] { std::this_thread::sleep_for(10ms); done.fetch_add(1); }), [&] { tails.fetch_add(1); });
        reaper.Drain();
        CHECK_EQ(done.load(), 4);
        CHECK_EQ(tails.load(), 4);
        reaper.Drain();                                // an idle reaper drains at once
    }
    {
        FeederReaper reaper;
        std::atomic<int> tails{ 0 };
        reaper.Reap(std::thread([] {}), [] { throw std::runtime_error("tail"); });   // a throwing tail is contained...
        reaper.Reap(std::thread([] {}), [&] { tails.fetch_add(1); });                 // ...and does not stop the next one
        reaper.Drain();
        CHECK_EQ(tails.load(), 1);
        reaper.Reap(std::thread(), [&] { tails.fetch_add(1); });                      // nothing to join: the tail still runs
        reaper.Drain();
        CHECK_EQ(tails.load(), 2);
    }
    {
        FeederReaper reaper;
        std::atomic<bool> finished{ false };
        std::atomic<int> tails{ 0 };
        reaper.Reap(std::thread([&] { std::this_thread::sleep_for(20ms); finished.store(true); }), [&] { tails.fetch_add(1); });
        reaper.Shutdown();                             // the DLL-unload join point: everything handed over has finished
        CHECK(finished.load());
        CHECK_EQ(tails.load(), 1);
        reaper.Shutdown();                             // idempotent
        std::atomic<bool> late{ false };
        reaper.Reap(std::thread([&] { std::this_thread::sleep_for(5ms); late.store(true); }), [&] { tails.fetch_add(1); });
        CHECK(late.load());                            // after Shutdown the join is inline on the caller
        CHECK_EQ(tails.load(), 2);
    }
    {
        std::atomic<bool> finished{ false };
        {
            FeederReaper reaper;
            reaper.Reap(std::thread([&] { std::this_thread::sleep_for(20ms); finished.store(true); }), nullptr);
        }                                              // the destructor is a join point too
        CHECK(finished.load());
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  J. fgpr::EventRing — the native→managed event queue (EventRing.h). Producers push and return; one notifier thread
//  delivers, in order, with no lock held; Stop flushes. (PrInternal.h's EventSink is a thin wrapper over it.)
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
struct RingRecorder
{
    std::mutex mx;
    std::vector<fgpr::RingEvent> got;
    std::vector<std::thread::id> threads;
    std::atomic<bool> hold{ false };      // true: a delivery parks here (a managed GC / a slow log sink)
    std::atomic<bool> entered{ false };   // a delivery is in the callback
    fgpr::EventRing* reenter = nullptr;   // when set, delivering ev 7 pushes ev 8 (a callback that raises)

    size_t Count() { std::lock_guard<std::mutex> g(mx); return got.size(); }
};

static void RingRecord(void* ctx, const fgpr::RingEvent& e)
{
    using namespace std::chrono_literals;
    auto* r = static_cast<RingRecorder*>(ctx);
    r->entered.store(true);
    while (r->hold.load()) std::this_thread::sleep_for(1ms);
    {
        std::lock_guard<std::mutex> g(r->mx);
        r->got.push_back(e);
        r->threads.push_back(std::this_thread::get_id());
    }
    if (r->reenter && e.ev == 7) r->reenter->Push(1, 8, 0, 0, nullptr, false);
}

static bool WaitFor(const std::function<bool()>& pred, int timeoutMs)
{
    using namespace std::chrono_literals;
    const auto end = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    while (!pred())
    {
        if (std::chrono::steady_clock::now() >= end) return false;
        std::this_thread::sleep_for(1ms);
    }
    return true;
}

static void Test_J40_DeliversInOrderOffTheProducerThread_AndStopFlushes()
{
    RingRecorder rec;
    fgpr::EventRing ring(44);
    CHECK(!ring.Push(1, 1, 0, 0, nullptr, false));        // not started: nothing is accepted
    ring.Start(&RingRecord, &rec);
    for (int i = 0; i < 100; ++i)
    {
        std::wstring text = L"t" + std::to_wstring(i);
        CHECK(ring.Push(5, 1, i, i * 2, text.c_str(), false));   // `text` dies each iteration: the ring owns a copy
    }
    ring.Stop();                                          // flush: everything queued is delivered before it returns
    CHECK_EQ((int)rec.Count(), 100);
    bool ordered = true;
    bool copied = true;
    bool offThread = true;
    for (int i = 0; i < (int)rec.got.size(); ++i)
    {
        ordered = ordered && rec.got[i].a == i && rec.got[i].b == i * 2 && rec.got[i].handle == 5 && rec.got[i].ev == 1;
        copied = copied && rec.got[i].text == L"t" + std::to_wstring(i);
        offThread = offThread && rec.threads[i] != std::this_thread::get_id();
    }
    CHECK(ordered);
    CHECK(copied);
    CHECK(offThread);                                     // never on the producer's thread
    CHECK(!ring.Push(1, 1, 0, 0, nullptr, false));        // a straggler after Stop is rejected, never delivered
    ring.Stop();                                          // idempotent

    ring.Start(&RingRecord, &rec);                        // a rebuilt runtime starts the ring again
    CHECK(ring.Push(9, 2, 0, 0, L"again", false));
    ring.Stop();
    CHECK_EQ((int)rec.Count(), 101);
    CHECK(rec.got.back().text == L"again");
}

static void Test_J41_ASlowConsumerNeverBlocksAProducer_LogsShedStateEventsKept()
{
    RingRecorder rec;
    fgpr::EventRing ring(44);
    ring.Start(&RingRecord, &rec);
    rec.hold.store(true);
    CHECK(ring.Push(1, 2, 0, 0, nullptr, false));         // the notifier takes this one and parks inside the callback
    CHECK(WaitFor([&] { return rec.entered.load(); }, 5000));

    const size_t logs = fgpr::EventRing::kMaxPendingLogs + 500;
    const auto t0 = std::chrono::steady_clock::now();
    for (size_t i = 0; i < logs; ++i) ring.Push(1, 44, 0, 0, L"line", true);
    for (int i = 1; i <= 3; ++i) CHECK(ring.Push(1, 2, i, 0, nullptr, false));
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();
    CHECK(ms < 2000);                                     // the producer never waited for the parked consumer
    CHECK_EQ(ring.TotalDropped(), (uint64_t)500);         // only logs past the cap were shed

    rec.hold.store(false);
    ring.Stop();
    int logLines = 0, notices = 0;
    std::vector<int64_t> states;
    for (const fgpr::RingEvent& e : rec.got)
    {
        if (e.ev == 2) states.push_back(e.a);
        else if (e.ev == 44 && e.text.rfind(L"[events]", 0) == 0) ++notices;
        else if (e.ev == 44) ++logLines;
    }
    CHECK_EQ(logLines, (int)fgpr::EventRing::kMaxPendingLogs);
    CHECK_EQ(notices, 1);                                 // one "N event(s) dropped" notice, raised as a log
    CHECK_EQ((int)states.size(), 4);                      // none of the state events was dropped, and they stayed in order
    CHECK(states.size() == 4 && states[0] == 0 && states[1] == 1 && states[2] == 2 && states[3] == 3);
}

static void Test_J42_NoLockIsHeldAcrossADelivery()
{
    RingRecorder rec;
    fgpr::EventRing ring(44);
    rec.reenter = &ring;
    ring.Start(&RingRecord, &rec);
    CHECK(ring.Push(1, 7, 0, 0, nullptr, false));         // delivering 7 pushes 8 from inside the callback: deadlocks if a lock is held
    CHECK(WaitFor([&] { return rec.Count() >= 2; }, 5000));
    ring.Stop();
    CHECK_EQ((int)rec.Count(), 2);
    CHECK(rec.got.size() == 2 && rec.got[0].ev == 7 && rec.got[1].ev == 8);
}

static void Test_J43_ConcurrentProducersKeepTheirOwnOrder()
{
    RingRecorder rec;
    fgpr::EventRing ring(44);
    ring.Start(&RingRecord, &rec);
    constexpr int kProducers = 4;
    constexpr int kEach = 500;
    std::vector<std::thread> producers;
    for (int p = 0; p < kProducers; ++p)
        producers.emplace_back([&ring, p] { for (int i = 0; i < kEach; ++i) ring.Push((uint64_t)p, 3, i, 0, nullptr, false); });
    for (auto& t : producers) t.join();
    ring.Stop();
    CHECK_EQ((int)rec.Count(), kProducers * kEach);
    int64_t next[kProducers] = {};
    bool ordered = true;
    for (const fgpr::RingEvent& e : rec.got)
    {
        if (e.handle >= (uint64_t)kProducers) { ordered = false; continue; }
        ordered = ordered && e.a == next[e.handle];
        ++next[e.handle];
    }
    CHECK(ordered);
    for (int p = 0; p < kProducers; ++p) CHECK_EQ((int)next[p], kEach);
}

static void Test_J44_StopIsBoundedWhenTheConsumerIsWedged_AndDeliversNothingAfterwards()
{
    using namespace std::chrono_literals;
    RingRecorder rec;
    fgpr::EventRing ring(44);   // outlives the abandoned notifier below (the production sink is never destroyed)
    ring.Start(&RingRecord, &rec);
    rec.hold.store(true);
    for (int i = 0; i < 3; ++i) ring.Push(1, 2, i, 0, nullptr, false);
    CHECK(WaitFor([&] { return rec.entered.load(); }, 5000));

    const auto t0 = std::chrono::steady_clock::now();
    ring.Stop();                                          // the callback is wedged: Stop gives up after kStopWaitMs, never hangs
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();
    CHECK(ms < fgpr::EventRing::kStopWaitMs + 3000);
    CHECK(!ring.Push(1, 2, 9, 0, nullptr, false));

    rec.hold.store(false);                                // the wedge ends: the in-flight delivery completes, nothing after it
    std::this_thread::sleep_for(150ms);
    CHECK((int)rec.Count() <= 1);
}

static void Test_J45_AnAbandonedStopDropsItsBacklog_AndNeverTaintsTheNextRun()
{
    using namespace std::chrono_literals;
    RingRecorder old;
    RingRecorder fresh;
    fgpr::EventRing ring(44);   // outlives the abandoned notifier below (the production sink is never destroyed)
    ring.Start(&RingRecord, &old);
    old.hold.store(true);
    for (int i = 0; i < 3; ++i) ring.Push(1, 2, i, 0, nullptr, false);
    CHECK(WaitFor([&] { return old.entered.load(); }, 5000));
    ring.Push(1, 44, 0, 0, L"old line", true);            // queued behind the wedge too
    ring.Stop();                                          // abandons the wedged notifier; its backlog belongs to the old context

    ring.Start(&RingRecord, &fresh);                      // the poisoned-runtime rebuild: a new context on the same ring
    old.hold.store(false);                                // the old wedge ends while the new run is live
    CHECK(ring.Push(2, 5, 100, 0, nullptr, false));
    CHECK(WaitFor([&] { return fresh.Count() >= 1; }, 5000));
    std::this_thread::sleep_for(150ms);                   // time for any stale event to surface
    const auto t0 = std::chrono::steady_clock::now();
    ring.Stop();                                          // must flush and join the NEW notifier, bounded and prompt
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();
    CHECK(ms < 1000);
    CHECK_EQ((int)fresh.Count(), 1);                      // only the new event: nothing queued behind the old wedge
    CHECK(fresh.got.size() == 1 && fresh.got[0].handle == 2 && fresh.got[0].a == 100);
    CHECK((int)old.Count() <= 1);                         // the old context saw at most its in-flight delivery
}

/// F25 - engine/transport items drain ahead of licence (Maintenance) items, whatever the order they were posted in; each
/// lane stays FIFO; a delayed item (the engine-source release) joins the Engine lane.
static void Test_K46_EngineLaneDrainsAheadOfMaintenance()
{
    fgpr::WorkQueue q;
    std::vector<std::string> ran;
    q.Post([&] { ran.push_back("M1"); }, fgpr::Lane::Maintenance);
    q.Post([&] { ran.push_back("M2"); }, fgpr::Lane::Maintenance);
    q.Post([&] { ran.push_back("E1"); });
    q.Post([&] { ran.push_back("E2"); }, fgpr::Lane::Engine);
    q.PostAfter(0, [&] { ran.push_back("D"); });
    q.Stop();
    CHECK(q.RunOnce());
    CHECK(!q.RunOnce());
    const std::vector<std::string> expect{ "E1", "E2", "D", "M1", "M2" };
    CHECK(ran == expect);
}

/// F25 - the starvation bound: while a Maintenance item waits, at most kMaintenanceEveryN Engine items run before it.
static void Test_K47_MaintenanceIsNeverStarved()
{
    fgpr::WorkQueue q;
    std::vector<int> ran;   // engine items push their index, the maintenance item pushes -1
    const int engineItems = fgpr::kMaintenanceEveryN * 2 + 3;
    q.Post([&] { ran.push_back(-1); }, fgpr::Lane::Maintenance);
    for (int i = 0; i < engineItems; ++i) q.Post([&ran, i] { ran.push_back(i); });
    q.Stop();
    CHECK(q.RunOnce());
    CHECK_EQ((int)ran.size(), engineItems + 1);
    int at = -1;
    for (int i = 0; i < (int)ran.size(); ++i) if (ran[i] == -1) at = i;
    CHECK_EQ(at, fgpr::kMaintenanceEveryN);                  // exactly N engine items first, then the licence item
    for (int i = 0; i < (int)ran.size(); ++i)                // and every engine item still ran in its own FIFO order
    {
        if (i == at) continue;
        CHECK_EQ(ran[i], i < at ? i : i - 1);
    }
}

/// F25 - the streak only counts while a Maintenance item waits: after a long run of engine work with nothing else queued,
/// a licence item still yields to the engine items posted with it (it does not jump the queue on a stale streak).
static void Test_K48_StreakOnlyCountsWhileMaintenanceWaits()
{
    fgpr::WorkQueue q;
    std::vector<int> ran;
    for (int i = 0; i < fgpr::kMaintenanceEveryN * 2; ++i) q.Post([&ran] { ran.push_back(0); });
    CHECK(q.RunOnce());   // drains the engine lane on its own: no maintenance item waited, so no streak was counted
    CHECK_EQ((int)ran.size(), fgpr::kMaintenanceEveryN * 2);
    q.Post([&ran] { ran.push_back(-1); }, fgpr::Lane::Maintenance);
    q.Post([&ran] { ran.push_back(1); });
    q.Post([&ran] { ran.push_back(2); });
    q.Stop();
    CHECK(q.RunOnce());
    CHECK_EQ((int)ran.size(), fgpr::kMaintenanceEveryN * 2 + 3);
    const size_t base = (size_t)fgpr::kMaintenanceEveryN * 2;
    CHECK_EQ(ran[base], 1);
    CHECK_EQ(ran[base + 1], 2);
    CHECK_EQ(ran[base + 2], -1);
}

static int g_workQueueThrew = 0;
static void CountWorkQueueThrow()
{
    try { throw; }
    catch (const std::runtime_error&) { ++g_workQueueThrew; }
}

/// F25 - an item that throws goes to the handler and never stops the queue; DrainMaintenance (runtime teardown) runs the
/// licence items already queued, in order, and leaves the Engine lane alone; a stopped queue refuses new work.
static void Test_K49_ThrowDrainAndStop()
{
    g_workQueueThrew = 0;
    fgpr::WorkQueue q;
    std::vector<std::string> ran;
    q.Post([&] { ran.push_back("M1"); throw std::runtime_error("licence step"); }, fgpr::Lane::Maintenance);
    q.Post([&] { ran.push_back("M2"); }, fgpr::Lane::Maintenance);
    q.Post([&] { ran.push_back("E1"); });
    q.DrainMaintenance(&CountWorkQueueThrow);
    const std::vector<std::string> afterDrain{ "M1", "M2" };
    CHECK(ran == afterDrain);
    CHECK_EQ(g_workQueueThrew, 1);
    q.Post([&] { ran.push_back("E2"); throw std::runtime_error("engine step"); });
    q.Stop();
    CHECK(!q.Post([&] { ran.push_back("late"); }));
    CHECK(!q.Post([&] { ran.push_back("late"); }, fgpr::Lane::Maintenance));
    CHECK(!q.PostAfter(0, [&] { ran.push_back("late"); }));
    CHECK(q.RunOnce(&CountWorkQueueThrow));                  // drains what was queued before the stop
    const std::vector<std::string> all{ "M1", "M2", "E1", "E2" };
    CHECK(ran == all);
    CHECK_EQ(g_workQueueThrew, 2);
    CHECK(!q.RunOnce());                                     // stopped AND drained
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  L. The protected H.264 media type: the SPS parser and the attributes described from it (F261, F028)
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// The SPS bytes are produced by a small bit writer from named fields (so an expectation reads as the stream it came from, not
// as a hex literal), then pushed through the real cenc::ParseSps / ParseInit and fgpr's ComputeH264TypeAttrs. Nothing here
// creates an IMFMediaType: BuildH264ClearType only applies what ComputeH264TypeAttrs returns, and that is what these pin.

/// Writes an RBSP bit by bit and wraps it as an SPS NAL unit with emulation prevention, as an encoder would.
struct SpsWriter
{
    std::vector<uint8_t> bytes;
    size_t nbits = 0;

    void Bit(uint32_t b)
    {
        if (nbits % 8 == 0) bytes.push_back(0);
        if (b) bytes.back() = (uint8_t)(bytes.back() | (1u << (7 - nbits % 8)));
        nbits++;
    }
    void Bits(uint32_t v, int n) { for (int i = n - 1; i >= 0; i--) Bit((v >> i) & 1u); }
    void Ue(uint32_t v)
    {
        const uint32_t x = v + 1;
        int len = 0;
        while ((x >> (len + 1)) != 0) len++;
        Bits(0, len);
        Bits(x, len + 1);
    }
    void Se(int32_t v) { Ue(v > 0 ? (uint32_t)(2 * v - 1) : (uint32_t)(-2 * v)); }

    /// rbsp_trailing_bits, the NAL header (nal_ref_idc 3, type 7) and 0x03 after every 00 00 that precedes a byte <= 3.
    std::vector<uint8_t> Nal()
    {
        Bit(1);
        std::vector<uint8_t> out{ 0x67 };
        int zeros = 0;
        for (uint8_t c : bytes)
        {
            if (zeros >= 2 && c <= 3) { out.push_back(3); zeros = 0; }
            out.push_back(c);
            zeros = c == 0 ? zeros + 1 : 0;
        }
        return out;
    }
};

/// The fields of one synthetic SPS. The defaults are a 1080p High profile stream: coded 1920x1088, cropped to 1080, with a VUI.
struct SpsSpec
{
    int profile = 100;
    uint32_t widthMbs = 120, heightMapUnits = 68;   // 1920 x 1088 when frame_mbs_only
    bool frameMbsOnly = true;
    uint32_t cropBottomUnits = 4;                   // 4:2:0 frame coding: 2 luma rows per unit -> 8 rows
    int scalingMode = 0;                            // 0 none, 1 one flat list, 2 one list that ends at its first delta (-8)
    bool vui = true;
    uint32_t aspectIdc = 14;                        // 4:3
    bool signal = true, fullRange = false, colour = true;
    uint32_t primaries = 1, transfer = 1, matrix = 1;   // BT.709 everything
    bool timing = true;
    uint32_t units = 1001, scale = 60000;           // 60000 / (2 * 1001) = 29.97 fps
};

static std::vector<uint8_t> BuildSpsNal(const SpsSpec& sp)
{
    SpsWriter w;
    w.Bits((uint32_t)sp.profile, 8);
    w.Bits(0, 8);       // constraint flags
    w.Bits(40, 8);      // level_idc
    w.Ue(0);            // seq_parameter_set_id
    if (sp.profile == 100)
    {
        w.Ue(1);        // chroma_format_idc: 4:2:0
        w.Ue(0);        // bit_depth_luma_minus8
        w.Ue(0);        // bit_depth_chroma_minus8
        w.Bit(0);       // qpprime_y_zero_transform_bypass_flag
        if (sp.scalingMode == 0) w.Bit(0);
        else
        {
            w.Bit(1);   // seq_scaling_matrix_present_flag
            for (int i = 0; i < 8; i++)
            {
                if (i != 0) { w.Bit(0); continue; }
                w.Bit(1);   // list 0 present
                if (sp.scalingMode == 1) { for (int j = 0; j < 16; j++) w.Se(0); }
                else w.Se(-8);   // nextScale = 0: the list's remaining entries are not coded
            }
        }
    }
    w.Ue(0);            // log2_max_frame_num_minus4
    w.Ue(0);            // pic_order_cnt_type
    w.Ue(0);            // log2_max_pic_order_cnt_lsb_minus4
    w.Ue(1);            // max_num_ref_frames
    w.Bit(0);           // gaps_in_frame_num_value_allowed_flag
    w.Ue(sp.widthMbs - 1);
    w.Ue(sp.heightMapUnits - 1);
    w.Bit(sp.frameMbsOnly ? 1 : 0);
    if (!sp.frameMbsOnly) w.Bit(0);   // mb_adaptive_frame_field_flag
    w.Bit(1);           // direct_8x8_inference_flag
    if (sp.cropBottomUnits != 0) { w.Bit(1); w.Ue(0); w.Ue(0); w.Ue(0); w.Ue(sp.cropBottomUnits); }
    else w.Bit(0);
    if (!sp.vui) { w.Bit(0); return w.Nal(); }
    w.Bit(1);           // vui_parameters_present_flag
    w.Bit(1);           // aspect_ratio_info_present_flag
    w.Bits(sp.aspectIdc, 8);
    if (sp.aspectIdc == 255) { w.Bits(1, 16); w.Bits(1, 16); }
    w.Bit(0);           // overscan_info_present_flag
    if (sp.signal)
    {
        w.Bit(1);
        w.Bits(5, 3);   // video_format: unspecified
        w.Bit(sp.fullRange ? 1 : 0);
        if (sp.colour) { w.Bit(1); w.Bits(sp.primaries, 8); w.Bits(sp.transfer, 8); w.Bits(sp.matrix, 8); }
        else w.Bit(0);
    }
    else w.Bit(0);
    w.Bit(0);           // chroma_loc_info_present_flag
    if (sp.timing) { w.Bit(1); w.Bits(sp.units, 32); w.Bits(sp.scale, 32); w.Bit(1); }
    else w.Bit(0);
    return w.Nal();
}

/// The geometry, crop and every VUI group of a 1080p High profile SPS come back as written.
static void Test_L13a_SpsGeometryCropAndVui()
{
    cenc::SpsInfo i;
    const std::vector<uint8_t> whole = BuildSpsNal(SpsSpec{});
    CHECK(cenc::ParseSps(whole.data(), whole.size(), i));
    CHECK(i.valid);
    CHECK_EQ(i.codedWidth, 1920u);
    CHECK_EQ(i.codedHeight, 1088u);
    CHECK_EQ(i.cropBottom, 8u);
    CHECK_EQ(i.cropLeft + i.cropRight + i.cropTop, 0u);
    CHECK_EQ(i.DisplayWidth(), 1920u);
    CHECK_EQ(i.DisplayHeight(), 1080u);
    CHECK(i.HasCrop());
    CHECK(i.hasAspect);
    CHECK_EQ(i.sarWidth, 4u);
    CHECK_EQ(i.sarHeight, 3u);
    CHECK(i.hasRange);
    CHECK(!i.fullRange);
    CHECK(i.hasColour);
    CHECK_EQ((uint32_t)i.primaries, 1u);
    CHECK_EQ((uint32_t)i.transfer, 1u);
    CHECK_EQ((uint32_t)i.matrix, 1u);
    CHECK(i.hasTiming);
    CHECK_EQ(i.numUnitsInTick, 1001u);
    CHECK_EQ(i.timeScale, 60000u);

    SpsSpec custom;
    custom.aspectIdc = 255;
    custom.fullRange = true;
    custom.colour = false;
    cenc::SpsInfo c;
    const std::vector<uint8_t> nal = BuildSpsNal(custom);
    CHECK(cenc::ParseSps(nal.data(), nal.size(), c));
    CHECK(c.hasAspect);
    CHECK_EQ(c.sarWidth, 1u);          // an Extended_SAR carries its own width / height
    CHECK_EQ(c.sarHeight, 1u);
    CHECK(c.hasRange);
    CHECK(c.fullRange);
    CHECK(!c.hasColour);               // video_signal_type without colour_description: range only
}

/// Baseline profile has no chroma / bit-depth block and no VUI here: the geometry alone, no crop, no VUI-derived field.
static void Test_L13b_BaselineWithoutVuiOrCrop()
{
    SpsSpec sp;
    sp.profile = 66;
    sp.widthMbs = 80;
    sp.heightMapUnits = 45;
    sp.cropBottomUnits = 0;
    sp.vui = false;
    const std::vector<uint8_t> nal = BuildSpsNal(sp);
    cenc::SpsInfo i;
    CHECK(cenc::ParseSps(nal.data(), nal.size(), i));
    CHECK(i.valid);
    CHECK_EQ(i.codedWidth, 1280u);
    CHECK_EQ(i.codedHeight, 720u);
    CHECK(!i.HasCrop());
    CHECK(!i.hasAspect);
    CHECK(!i.hasRange);
    CHECK(!i.hasColour);
    CHECK(!i.hasTiming);
}

/// Scaling lists are skipped by the bits they take (a flat list is 16 se(0); a delta that makes nextScale 0 ends the list), and
/// a field-coded stream's crop unit is doubled: 2 units of 4 rows = the same 8 rows.
static void Test_L13c_ScalingListsAndFieldCodedCropUnits()
{
    for (int mode = 1; mode <= 2; mode++)
    {
        SpsSpec sp;
        sp.scalingMode = mode;
        const std::vector<uint8_t> nal = BuildSpsNal(sp);
        cenc::SpsInfo i;
        CHECK(cenc::ParseSps(nal.data(), nal.size(), i));
        CHECK_EQ(i.codedWidth, 1920u);
        CHECK_EQ(i.cropBottom, 8u);
        CHECK(i.hasTiming);             // the VUI behind the lists still parses: the bit count of the lists was right
        CHECK_EQ(i.numUnitsInTick, 1001u);
    }

    SpsSpec field;
    field.frameMbsOnly = false;
    field.heightMapUnits = 34;          // 34 map units of 2 macroblock rows = 1088
    field.cropBottomUnits = 2;
    const std::vector<uint8_t> nal = BuildSpsNal(field);
    cenc::SpsInfo f;
    CHECK(cenc::ParseSps(nal.data(), nal.size(), f));
    CHECK_EQ(f.codedHeight, 1088u);
    CHECK_EQ(f.cropBottom, 8u);
    CHECK_EQ(f.DisplayHeight(), 1080u);
}

/// A VUI whose bytes need emulation prevention (a 32-bit field of 00 00 00 01) is unescaped before it is read.
static void Test_L13d_EmulationPreventionIsRemoved()
{
    SpsSpec sp;
    sp.units = 1;
    sp.scale = 50;
    const std::vector<uint8_t> nal = BuildSpsNal(sp);
    bool escaped = false;
    for (size_t k = 0; k + 2 < nal.size(); k++)
        if (nal[k] == 0 && nal[k + 1] == 0 && nal[k + 2] == 3) escaped = true;
    CHECK(escaped);
    cenc::SpsInfo i;
    CHECK(cenc::ParseSps(nal.data(), nal.size(), i));
    CHECK(i.hasTiming);
    CHECK_EQ(i.numUnitsInTick, 1u);
    CHECK_EQ(i.timeScale, 50u);
}

/// Hostile input: not an SPS, empty, and every truncation of a valid one. Never a crash or an out-of-range read, and a result
/// that says valid never carries geometry other than the stream's.
static void Test_L13e_TruncatedAndForeignNalUnits()
{
    cenc::SpsInfo i;
    const uint8_t pps[] = { 0x68, 0xCE, 0x3C, 0x80, 0x00 };
    CHECK(!cenc::ParseSps(pps, sizeof(pps), i));
    CHECK(!i.valid);
    CHECK(!cenc::ParseSps(nullptr, 0, i));
    const std::vector<uint8_t> nal = BuildSpsNal(SpsSpec{});
    for (size_t len = 0; len < nal.size(); len++)
    {
        cenc::SpsInfo t;
        if (cenc::ParseSps(nal.data(), len, t))
        {
            CHECK(t.valid);
            CHECK_EQ(t.codedWidth, 1920u);
            CHECK_EQ(t.DisplayHeight(), 1080u);
        }
        else CHECK(!t.valid);
    }
    // Cropping the whole frame away is implausible and refused rather than reported as a zero-sized picture.
    SpsSpec absurd;
    absurd.heightMapUnits = 1;
    absurd.cropBottomUnits = 8;
    const std::vector<uint8_t> bad = BuildSpsNal(absurd);
    CHECK(!cenc::ParseSps(bad.data(), bad.size(), i));
}

/// An InitInfo as ParseInit would produce it for a 1080p stream: the SPS of `sp`, the given trex duration.
static cenc::InitInfo MakeVideoInfo(const SpsSpec& sp, uint32_t trexDuration)
{
    cenc::InitInfo info;
    info.width = 1920;
    info.height = 1080;
    info.timescale = 90000;
    info.defaultSampleDuration = trexDuration;
    const std::vector<uint8_t> nal = BuildSpsNal(sp);
    cenc::ParseSps(nal.data(), nal.size(), info.sps);
    return info;
}

/// F028: frame rate from the trex default duration, else the VUI timing; a placeholder duration is not a rate.
static void Test_L13f_FrameRate()
{
    H264TypeAttrs a = ComputeH264TypeAttrs(MakeVideoInfo(SpsSpec{}, 3000));
    CHECK(a.hasFrameRate);
    CHECK_EQ(a.frameRateNum, 30u);
    CHECK_EQ(a.frameRateDen, 1u);

    a = ComputeH264TypeAttrs(MakeVideoInfo(SpsSpec{}, 0));        // no trex duration: the VUI's 60000 / (2 * 1001), reduced
    CHECK(a.hasFrameRate);
    CHECK_EQ(a.frameRateNum, 30000u);
    CHECK_EQ(a.frameRateDen, 1001u);

    a = ComputeH264TypeAttrs(MakeVideoInfo(SpsSpec{}, 1));        // 1 tick at 90 kHz is 90000 fps: a placeholder, VUI wins
    CHECK(a.hasFrameRate);
    CHECK_EQ(a.frameRateNum, 30000u);

    SpsSpec noTiming;
    noTiming.timing = false;
    a = ComputeH264TypeAttrs(MakeVideoInfo(noTiming, 0));         // nothing states a rate: the attribute is left out
    CHECK(!a.hasFrameRate);

    uint32_t n = 0, d = 0;
    CHECK(ReduceFrameRate(90000, 3750, n, d));                    // 24 fps
    CHECK_EQ(n, 24u);
    CHECK_EQ(d, 1u);
    CHECK(!ReduceFrameRate(90000, 100, n, d));                    // 900 fps
    CHECK(!ReduceFrameRate(90000, 180000, n, d));                 // 0.5 fps
    CHECK(!ReduceFrameRate(0, 3000, n, d));
    CHECK(!ReduceFrameRate(90000, 0, n, d));
}

/// F261: pixel aspect ratio from pasp, else the VUI, else square pixels; aperture only when the SPS crop and the container
/// agree; colour from the VUI's code points with the unmapped ones left out.
static void Test_L13g_AspectApertureAndColour()
{
    // The default spec's VUI says 4:3 and no pasp is present.
    cenc::InitInfo info = MakeVideoInfo(SpsSpec{}, 3000);
    H264TypeAttrs a = ComputeH264TypeAttrs(info);
    CHECK_EQ(a.parNum, 4u);
    CHECK_EQ(a.parDen, 3u);
    CHECK(a.hasAperture);
    CHECK(a.hasPrimaries);
    CHECK_EQ(a.primaries, (uint32_t)MFVideoPrimaries_BT709);
    CHECK(a.hasTransfer);
    CHECK_EQ(a.transfer, (uint32_t)MFVideoTransFunc_709);
    CHECK(a.hasMatrix);
    CHECK_EQ(a.matrix, (uint32_t)MFVideoTransferMatrix_BT709);
    CHECK(a.hasRange);
    CHECK_EQ(a.range, (uint32_t)MFNominalRange_16_235);

    info.paspHSpacing = 1;              // the container's pasp outranks the VUI
    info.paspVSpacing = 1;
    a = ComputeH264TypeAttrs(info);
    CHECK_EQ(a.parNum, 1u);
    CHECK_EQ(a.parDen, 1u);

    cenc::InitInfo bare;                // no SPS at all, no pasp: square pixels, and nothing else is claimed
    bare.width = 1280;
    bare.height = 720;
    a = ComputeH264TypeAttrs(bare);
    CHECK_EQ(a.parNum, 1u);
    CHECK_EQ(a.parDen, 1u);
    CHECK(!a.hasAperture);
    CHECK(!a.hasFrameRate);
    CHECK(!a.hasPrimaries);
    CHECK(!a.hasTransfer);
    CHECK(!a.hasMatrix);
    CHECK(!a.hasRange);

    cenc::InitInfo disagree = MakeVideoInfo(SpsSpec{}, 3000);
    disagree.height = 1088;             // the container declares the uncropped height while the SPS crops 8 rows
    CHECK(!ComputeH264TypeAttrs(disagree).hasAperture);

    SpsSpec uncropped;
    uncropped.cropBottomUnits = 0;
    CHECK(!ComputeH264TypeAttrs(MakeVideoInfo(uncropped, 3000)).hasAperture);   // nothing to describe: the frame is the picture

    SpsSpec odd;
    odd.fullRange = true;
    odd.primaries = 9;                  // BT.2020
    odd.transfer = 16;                  // PQ
    odd.matrix = 9;                     // BT.2020 non-constant
    a = ComputeH264TypeAttrs(MakeVideoInfo(odd, 3000));
    CHECK_EQ(a.range, (uint32_t)MFNominalRange_0_255);
    CHECK_EQ(a.primaries, (uint32_t)MFVideoPrimaries_BT2020);
    CHECK_EQ(a.transfer, (uint32_t)MFVideoTransFunc_2084);
    CHECK_EQ(a.matrix, (uint32_t)MFVideoTransferMatrix_BT2020_10);

    SpsSpec unknown;
    unknown.primaries = 2;              // "unspecified": no Media Foundation value, so no attribute
    unknown.transfer = 99;
    unknown.matrix = 0;                 // identity (RGB): not a YUV matrix
    a = ComputeH264TypeAttrs(MakeVideoInfo(unknown, 3000));
    CHECK(!a.hasPrimaries);
    CHECK(!a.hasTransfer);
    CHECK(!a.hasMatrix);
}

static void PutBe32(std::vector<uint8_t>& v, uint32_t x)
{
    v.push_back((uint8_t)(x >> 24)); v.push_back((uint8_t)(x >> 16)); v.push_back((uint8_t)(x >> 8)); v.push_back((uint8_t)x);
}

/// An ISO box: size, four-character type, payload.
static std::vector<uint8_t> MakeBox(const char* type, const std::vector<uint8_t>& payload)
{
    std::vector<uint8_t> b;
    PutBe32(b, (uint32_t)(8 + payload.size()));
    for (int i = 0; i < 4; i++) b.push_back((uint8_t)type[i]);
    b.insert(b.end(), payload.begin(), payload.end());
    return b;
}

static void Append(std::vector<uint8_t>& dst, const std::vector<uint8_t>& src) { dst.insert(dst.end(), src.begin(), src.end()); }

/// The whole path: a real moov (mvhd, mvex/trex, trak/.../stsd/avc1 with avcC and pasp) goes through ParseInit, and what comes
/// out feeds ComputeH264TypeAttrs - the wiring from the container boxes to the type attributes, not just each half.
static void Test_L13h_ParseInitCarriesPaspSpsAndTrexIntoTheType()
{
    const std::vector<uint8_t> sps = BuildSpsNal(SpsSpec{});
    const uint8_t pps[] = { 0x68, 0xCE, 0x3C, 0x80 };

    std::vector<uint8_t> avcCPayload{ 1, 100, 0, 40, 0xFF, 0xE1 };
    avcCPayload.push_back((uint8_t)(sps.size() >> 8)); avcCPayload.push_back((uint8_t)sps.size());
    Append(avcCPayload, sps);
    avcCPayload.push_back(1); avcCPayload.push_back(0); avcCPayload.push_back((uint8_t)sizeof(pps));
    avcCPayload.insert(avcCPayload.end(), pps, pps + sizeof(pps));

    std::vector<uint8_t> paspPayload;
    PutBe32(paspPayload, 1);
    PutBe32(paspPayload, 1);

    std::vector<uint8_t> entry(78, 0);          // VisualSampleEntry header: width @24, height @26
    entry[24] = 1920 >> 8; entry[25] = 1920 & 0xFF;
    entry[26] = 1080 >> 8; entry[27] = 1080 & 0xFF;
    Append(entry, MakeBox("avcC", avcCPayload));
    Append(entry, MakeBox("pasp", paspPayload));

    std::vector<uint8_t> stsdPayload(8, 0);     // version/flags + entry_count
    stsdPayload[7] = 1;
    Append(stsdPayload, MakeBox("avc1", entry));

    std::vector<uint8_t> mdhd(24, 0);           // timescale @12
    mdhd[12] = 0; mdhd[13] = 1; mdhd[14] = 0x5F; mdhd[15] = 0x90;   // 90000
    std::vector<uint8_t> tkhd(84, 0);           // track_ID @12
    tkhd[15] = 1;
    std::vector<uint8_t> mvhd(100, 0);          // timescale @12
    mvhd[14] = 0x03; mvhd[15] = 0xE8;           // 1000
    std::vector<uint8_t> trex(24, 0);           // track_ID @4, default_sample_duration @12
    trex[7] = 1;
    trex[14] = 0x0B; trex[15] = 0xB8;           // 3000

    std::vector<uint8_t> stbl = MakeBox("stsd", stsdPayload);
    std::vector<uint8_t> minf = MakeBox("stbl", stbl);
    std::vector<uint8_t> mdia = MakeBox("mdhd", mdhd);
    Append(mdia, MakeBox("minf", minf));
    std::vector<uint8_t> trak = MakeBox("tkhd", tkhd);
    Append(trak, MakeBox("mdia", mdia));
    std::vector<uint8_t> moovPayload = MakeBox("mvhd", mvhd);
    Append(moovPayload, MakeBox("mvex", MakeBox("trex", trex)));
    Append(moovPayload, MakeBox("trak", trak));
    const std::vector<uint8_t> init = MakeBox("moov", moovPayload);

    cenc::InitInfo info;
    CHECK(cenc::ParseInit(init, info, cenc::TrackPick::Video));
    CHECK_EQ(info.width, 1920u);
    CHECK_EQ(info.height, 1080u);
    CHECK_EQ(info.paspHSpacing, 1u);
    CHECK_EQ(info.paspVSpacing, 1u);
    CHECK(info.sps.valid);
    CHECK_EQ(info.sps.codedHeight, 1088u);
    CHECK_EQ(info.defaultSampleDuration, 3000u);
    CHECK_EQ(info.timescale, (uint64_t)90000);

    const H264TypeAttrs a = ComputeH264TypeAttrs(info);
    CHECK(a.hasFrameRate);
    CHECK_EQ(a.frameRateNum, 30u);
    CHECK_EQ(a.frameRateDen, 1u);
    CHECK_EQ(a.parNum, 1u);             // pasp (1:1) over the VUI's 4:3
    CHECK_EQ(a.parDen, 1u);
    CHECK(a.hasAperture);
    CHECK(a.hasPrimaries);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  M. The virtual OPM window (F264)
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// OpmScreenRect is the pure part (client rect -> screen rect); the window itself is created and moved for real, because the
// only things worth pinning about it are Win32 facts: it exists, it is a hidden layered popup, a move lands where the video is,
// and Stop leaves nothing behind. A box with no window station skips the lifecycle test rather than failing it.

/// The screen rect is the client rect shifted by the host's client origin (negative on a monitor left of the primary), and a
/// degenerate rect still gives a 1x1 window, which is what lets the OPM place it on a monitor at all.
static void Test_M50_OpmScreenRect()
{
    const RECT a = fgpr::OpmScreenRect(POINT{ 100, 50 }, 10, 20, 650, 380);
    CHECK_EQ(a.left, (LONG)110);
    CHECK_EQ(a.top, (LONG)70);
    CHECK_EQ(a.right, (LONG)750);
    CHECK_EQ(a.bottom, (LONG)430);

    const RECT left = fgpr::OpmScreenRect(POINT{ -1920, 0 }, 0, 0, 1920, 1080);
    CHECK_EQ(left.left, (LONG)-1920);
    CHECK_EQ(left.right, (LONG)0);
    CHECK_EQ(left.bottom, (LONG)1080);

    const RECT empty = fgpr::OpmScreenRect(POINT{ 5, 5 }, 7, 7, 7, 7);
    CHECK_EQ(empty.right - empty.left, (LONG)1);
    CHECK_EQ(empty.bottom - empty.top, (LONG)1);
    const RECT inverted = fgpr::OpmScreenRect(POINT{ 0, 0 }, 10, 10, 4, 4);
    CHECK_EQ(inverted.right - inverted.left, (LONG)1);
    CHECK_EQ(inverted.bottom - inverted.top, (LONG)1);
}

/// Create -> a hidden layered popup -> moved over a host window's client rect -> follows the host when it moves -> refuses a host that is not a window -> Stop
/// destroys it, is idempotent, and a second window can be created afterwards (the class stays registered).
static void Test_M51_OpmWindowLifecycle()
{
    fgpr::OpmWindow w;
    const HRESULT hr = w.Start();
    if (FAILED(hr))
    {
        std::printf("  (M51 skipped: no window could be created here, hr=0x%08x)\n", (unsigned)hr);
        return;
    }
    const HWND h = w.Handle();
    CHECK(h != nullptr);
    CHECK(IsWindow(h) != FALSE);
    const LONG_PTR style = GetWindowLongPtrW(h, GWL_STYLE);
    CHECK((style & WS_VISIBLE) == 0);   // never shown
    CHECK((style & WS_POPUP) != 0);
    CHECK((GetWindowLongPtrW(h, GWL_EXSTYLE) & WS_EX_LAYERED) != 0);
    CHECK(w.Start() == S_OK);           // a second Start is a no-op

    CHECK(FAILED(w.Place(nullptr, 0, 0, 10, 10)));
    CHECK(FAILED(w.Place((HWND)(uintptr_t)1, 0, 0, 10, 10)));

    const HWND host = CreateWindowExW(0, L"STATIC", L"", WS_POPUP, 300, 200, 640, 360, nullptr, nullptr, nullptr, nullptr);
    CHECK(host != nullptr);
    if (host)
    {
        RECT placed{};
        CHECK(w.Place(host, 10, 20, 330, 200, &placed) == S_OK);
        CHECK(w.FirstPlacement());
        CHECK(!w.FirstPlacement());     // the one-time log gate
        // The move is posted to the window's own thread: wait for it to land (a DPI-virtualised box may differ by a pixel).
        RECT actual{};
        bool landed = false;
        for (int i = 0; i < 200 && !landed; i++)
        {
            GetWindowRect(h, &actual);
            landed = std::abs((int)(actual.left - placed.left)) <= 2 && std::abs((int)(actual.top - placed.top)) <= 2 &&
                     std::abs((int)((actual.right - actual.left) - (placed.right - placed.left))) <= 2 &&
                     std::abs((int)((actual.bottom - actual.top) - (placed.bottom - placed.top))) <= 2;
            if (!landed) std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        CHECK(landed);
        CHECK_EQ(placed.right - placed.left, (LONG)320);
        CHECK_EQ(placed.bottom - placed.top, (LONG)180);

        // The host moves with NO further Place (a drag, a same-DPI monitor hop): the window must follow it on its own, or the
        // OPM would keep attesting against the monitor the video was on before the move. The location hook delivers it to the
        // window's pump thread; the origin of the window is the host's new client origin plus the client offset.
        CHECK(SetWindowPos(host, nullptr, 700, 450, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE) != FALSE);
        POINT movedOrigin{ 0, 0 };
        CHECK(ClientToScreen(host, &movedOrigin) != FALSE);
        const RECT followed = fgpr::OpmScreenRect(movedOrigin, 10, 20, 330, 200);
        CHECK(followed.left != placed.left || followed.top != placed.top);   // the move really changed the target
        bool followedLanded = false;
        for (int i = 0; i < 200 && !followedLanded; i++)
        {
            GetWindowRect(h, &actual);
            followedLanded = std::abs((int)(actual.left - followed.left)) <= 2 && std::abs((int)(actual.top - followed.top)) <= 2 &&
                             std::abs((int)((actual.right - actual.left) - (followed.right - followed.left))) <= 2 &&
                             std::abs((int)((actual.bottom - actual.top) - (followed.bottom - followed.top))) <= 2;
            if (!followedLanded) std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        CHECK(followedLanded);

        DestroyWindow(host);
    }

    w.Stop();
    CHECK(IsWindow(h) == FALSE);
    CHECK(w.Handle() == nullptr);
    CHECK(w.Place(nullptr, 0, 0, 1, 1) == S_FALSE);   // no window: nothing to move
    w.Stop();                                          // idempotent

    fgpr::OpmWindow again;
    CHECK(SUCCEEDED(again.Start()));
    CHECK(again.Handle() != nullptr);
}

/// F66 - the engine's rendered/dropped counters accumulate across the resets MF applies after a flush, and a warm engine's
/// leftover totals are never counted into the next source.
static void Test_N52_FrameCounters()
{
    using fgpr::health::FrameCounters;
    {
        FrameCounters c;
        c.Observe(30, 1);
        c.Observe(75, 1);
        c.Observe(120, 4);
        CHECK_EQ(c.rendered, (int64_t)120);
        CHECK_EQ(c.dropped, (int64_t)4);
    }
    {
        FrameCounters c;
        c.Observe(100, 5);
        c.Observe(12, 0);                    // a seek flushed the engine: both restarted from zero, the new reading IS the delta
        CHECK_EQ(c.rendered, (int64_t)112);
        CHECK_EQ(c.dropped, (int64_t)5);
        c.Observe(40, 2);
        CHECK_EQ(c.rendered, (int64_t)140);
        CHECK_EQ(c.dropped, (int64_t)7);
    }
    {
        FrameCounters c;
        c.Observe(100, 10);
        c.Observe(130, 3);                   // either counter going backwards is a reset of both
        CHECK_EQ(c.rendered, (int64_t)230);
        CHECK_EQ(c.dropped, (int64_t)13);
    }
    {
        FrameCounters c;
        c.Observe(500, 20);
        c.Rebase(500, 20);                   // the next source reuses the warm engine, which still holds the old totals
        CHECK_EQ(c.rendered, (int64_t)0);
        c.Observe(520, 21);
        CHECK_EQ(c.rendered, (int64_t)20);
        CHECK_EQ(c.dropped, (int64_t)1);
        c.Observe(8, 0);
        CHECK_EQ(c.rendered, (int64_t)28);
        c.Reset();
        CHECK_EQ(c.rendered, (int64_t)0);
    }
    {
        FrameCounters c;                     // a 32-bit counter that wraps cannot be told from a reset: the new reading is the delta
        c.Observe(0xFFFFFFF0u, 0);
        c.Observe(0x10u, 0);
        CHECK_EQ(c.rendered, (int64_t)(0xFFFFFFF0ull + 0x10ull));
    }
}

/// F66 - the no-rendered-frame hang check: once, only after the timeout of PLAYING, never for a source that has rendered a frame, and
/// time outside PLAYING never counts.
static void Test_N53_RenderedFrameWatch()
{
    using fgpr::health::RenderedFrameWatch;
    using fgpr::health::kNoFrameTimeoutMs;
    {
        RenderedFrameWatch w;
        CHECK(!w.Observe(1000, true, 0));                          // the window opens
        CHECK(!w.Observe(1000 + kNoFrameTimeoutMs - 1, true, 0));
        CHECK(w.Observe(1000 + kNoFrameTimeoutMs, true, 0));       // elapsed with still no frame
        CHECK(!w.Observe(1000 + kNoFrameTimeoutMs * 3, true, 0));  // exactly once
    }
    {
        RenderedFrameWatch w;
        CHECK(!w.Observe(0, true, 0));
        CHECK(!w.Observe(5000, true, 1));                          // one frame: the watch is over for this source
        CHECK(!w.Observe(60000, true, 1));
    }
    {
        RenderedFrameWatch w;
        CHECK(!w.Observe(0, true, 0));
        CHECK(!w.Observe(8000, true, 0));
        CHECK(!w.Observe(9000, false, 0));                         // paused / seeking / waiting: not counting
        CHECK(!w.Observe(30000, false, 0));
        CHECK(!w.Observe(30500, true, 0));                         // resumed: a fresh window
        CHECK(!w.Observe(30500 + kNoFrameTimeoutMs - 1, true, 0));
        CHECK(w.Observe(30500 + kNoFrameTimeoutMs, true, 0));
    }
    {
        RenderedFrameWatch w;
        w.Restart(500);                                            // not counting: a no-op
        CHECK(!w.Observe(1000, true, 0));
        w.Restart(9000);                                           // UpdateVideoStream re-created the swap chain
        CHECK(!w.Observe(9000 + kNoFrameTimeoutMs - 1, true, 0));
        CHECK(w.Observe(9000 + kNoFrameTimeoutMs, true, 0));
        w.Reset();
        CHECK(!w.Observe(20000, true, 0));
        CHECK(w.Observe(20000 + kNoFrameTimeoutMs, true, 0));
    }
    {
        // No readable statistics = never a hang: PollFrameHealth feeds `playing && statsReadable`, so a renderer whose GetStatistics
        // fails or returns VT_EMPTY is never judged, however long it plays.
        RenderedFrameWatch w;
        for (int64_t t = 0; t <= kNoFrameTimeoutMs * 5; t += 500) CHECK(!w.Observe(t, false, 0));
        CHECK(!w.Observe(kNoFrameTimeoutMs * 5 + 500, true, 0));   // the counters become readable: the window opens only now
        CHECK(!w.Observe(kNoFrameTimeoutMs * 5 + 500 + kNoFrameTimeoutMs - 1, true, 0));
        CHECK(w.Observe(kNoFrameTimeoutMs * 5 + 500 + kNoFrameTimeoutMs, true, 0));
    }
}

/// F198 - the swap-chain handles the runtime owns: a retired handle is closed exactly once, only after its grace (or when the
/// retired list overflows), never while reinstated, and CloseAll takes the rest.
static void Test_N54_RetiredHandles()
{
    using fgpr::health::RetiredHandles;
    using fgpr::health::kHandleGraceMs;
    using fgpr::health::kMaxRetiredHandles;
    std::vector<uint64_t> closed;
    auto close = [&closed](uint64_t h) { closed.push_back(h); };
    {
        RetiredHandles r;
        r.Retire(0, 0, close);                                     // 0 = no handle: never retired
        CHECK_EQ(r.Count(), (size_t)0);
        r.Retire(0x10, 100, close);
        r.Retire(0x20, 600, close);
        r.Sweep(100 + kHandleGraceMs - 1, close);
        CHECK(closed.empty());                                     // still inside the grace
        r.Sweep(100 + kHandleGraceMs, close);
        CHECK_EQ(closed.size(), (size_t)1);
        CHECK_EQ(closed[0], (uint64_t)0x10);                       // only the one whose grace elapsed
        CHECK_EQ(r.Count(), (size_t)1);
        r.Sweep(600 + kHandleGraceMs, close);
        CHECK_EQ(closed.size(), (size_t)2);
        CHECK_EQ(r.Count(), (size_t)0);
        r.Sweep(1000000, close);
        CHECK_EQ(closed.size(), (size_t)2);                        // closed exactly once
    }
    closed.clear();
    {
        RetiredHandles r;
        r.Retire(0x10, 0, close);
        CHECK(r.Reinstate(0x10));                                  // MF handed back the identical handle: current again
        CHECK(!r.Reinstate(0x10));
        r.Sweep(1000000, close);
        CHECK(closed.empty());
    }
    {
        RetiredHandles r;
        for (uint64_t h = 1; h <= kMaxRetiredHandles + 2; ++h) r.Retire(h, 0, close);   // a burst of format changes
        CHECK_EQ(closed.size(), (size_t)2);                        // the oldest are closed at once, the newest kept
        CHECK_EQ(closed[0], (uint64_t)1);
        CHECK_EQ(closed[1], (uint64_t)2);
        CHECK_EQ(r.Count(), kMaxRetiredHandles);
        r.CloseAll(close);
        CHECK_EQ(r.Count(), (size_t)0);
        CHECK_EQ(closed.size(), (size_t)(2 + kMaxRetiredHandles));
        r.CloseAll(close);                                         // idempotent
        CHECK_EQ(closed.size(), (size_t)(2 + kMaxRetiredHandles));
    }
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
        { "C19b_BufferedPairsCacheFollowsTheVersion", Test_C19b_BufferedPairsCacheFollowsTheVersion },
        { "C20b_ContiguousReachMatchesTheWalk", Test_C20b_ContiguousReachMatchesTheWalk },
        { "A10b_DemandHysteresisAfterASatisfiedPlan", Test_A10b_DemandHysteresisAfterASatisfiedPlan },
        { "C21_TrimBehindByTime", Test_C21_TrimBehindByTime },
        { "C23_BackwardSeekRunSurvivesTrim", Test_C23_BackwardSeekRunSurvivesTrim },
        { "C24_TrimKeepsKeyframeHead", Test_C24_TrimKeepsKeyframeHead },
        { "C25_ByteRuleNeverEvictsAheadRun", Test_C25_ByteRuleNeverEvictsAheadRun },
        { "C26_PickStartKeyframe", Test_C26_PickStartKeyframe },
        { "C27_ResumeStartMs", Test_C27_ResumeStartMs },
        { "C28_PoolCapAndMediaBytes", Test_C28_PoolCapAndMediaBytes },
        { "D29_AppendAtTheBufferedEndErasesNothing", Test_D29_AppendAtTheBufferedEndErasesNothing },
        { "D30_RetainBoundaryRemovesOnlyWhatLiesPastIt", Test_D30_RetainBoundaryRemovesOnlyWhatLiesPastIt },
        { "D31_FormatGenAtFollowsTheDeliveryCursorAcrossTheSplice", Test_D31_FormatGenAtFollowsTheDeliveryCursorAcrossTheSplice },
        { "D32_RepBookRegistersPrunesAndNeverReusesAGenerationInUse", Test_D32_RepBookRegistersPrunesAndNeverReusesAGenerationInUse },
        { "D33_RepGenSurvivesTrimAndRelocation", Test_D33_RepGenSurvivesTrimAndRelocation },
        { "E34_InternalSeekTag", Test_E34_InternalSeekTag },
        { "F35_RetryDelayBacksOffAndCaps", Test_F35_RetryDelayBacksOffAndCaps },
        { "F35_OnlyANotFoundAtTheEndEndsTheTrack", Test_F35_OnlyANotFoundAtTheEndEndsTheTrack },
        { "G36_WaitGate", Test_G36_WaitGate },
        { "H37_LicensePolicy", Test_H37_LicensePolicy },
        { "I38_ReleaseGate", Test_I38_ReleaseGate },
        { "I39_FeederReaper", Test_I39_FeederReaper },
        { "J40_DeliversInOrderOffTheProducerThread_AndStopFlushes", Test_J40_DeliversInOrderOffTheProducerThread_AndStopFlushes },
        { "J41_ASlowConsumerNeverBlocksAProducer_LogsShedStateEventsKept", Test_J41_ASlowConsumerNeverBlocksAProducer_LogsShedStateEventsKept },
        { "J42_NoLockIsHeldAcrossADelivery", Test_J42_NoLockIsHeldAcrossADelivery },
        { "J43_ConcurrentProducersKeepTheirOwnOrder", Test_J43_ConcurrentProducersKeepTheirOwnOrder },
        { "J44_StopIsBoundedWhenTheConsumerIsWedged_AndDeliversNothingAfterwards", Test_J44_StopIsBoundedWhenTheConsumerIsWedged_AndDeliversNothingAfterwards },
        { "J45_AnAbandonedStopDropsItsBacklog_AndNeverTaintsTheNextRun", Test_J45_AnAbandonedStopDropsItsBacklog_AndNeverTaintsTheNextRun },
        { "K46_EngineLaneDrainsAheadOfMaintenance", Test_K46_EngineLaneDrainsAheadOfMaintenance },
        { "K47_MaintenanceIsNeverStarved", Test_K47_MaintenanceIsNeverStarved },
        { "K48_StreakOnlyCountsWhileMaintenanceWaits", Test_K48_StreakOnlyCountsWhileMaintenanceWaits },
        { "K49_ThrowDrainAndStop", Test_K49_ThrowDrainAndStop },
        { "L13a_SpsGeometryCropAndVui", Test_L13a_SpsGeometryCropAndVui },
        { "L13b_BaselineWithoutVuiOrCrop", Test_L13b_BaselineWithoutVuiOrCrop },
        { "L13c_ScalingListsAndFieldCodedCropUnits", Test_L13c_ScalingListsAndFieldCodedCropUnits },
        { "L13d_EmulationPreventionIsRemoved", Test_L13d_EmulationPreventionIsRemoved },
        { "L13e_TruncatedAndForeignNalUnits", Test_L13e_TruncatedAndForeignNalUnits },
        { "L13f_FrameRate", Test_L13f_FrameRate },
        { "L13g_AspectApertureAndColour", Test_L13g_AspectApertureAndColour },
        { "L13h_ParseInitCarriesPaspSpsAndTrexIntoTheType", Test_L13h_ParseInitCarriesPaspSpsAndTrexIntoTheType },
        { "M50_OpmScreenRect", Test_M50_OpmScreenRect },
        { "M51_OpmWindowLifecycle", Test_M51_OpmWindowLifecycle },
        { "N52_FrameCounters", Test_N52_FrameCounters },
        { "N53_RenderedFrameWatch", Test_N53_RenderedFrameWatch },
        { "N54_RetiredHandles", Test_N54_RetiredHandles },
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
