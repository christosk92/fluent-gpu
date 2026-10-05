// SegmentStore.h — the byte-capped, TIME-windowed store that backs exactly ONE FgPrSession.
//
// WHY THIS EXISTS (wavee-0.3-video-engine-implementation.md §3.1.2, §3.2.1 change 3, §3.5). The old feeder bounded
// itself with two SAMPLE COUNTS: `kMaxSamplesAhead = 900` (the fetch-ahead ceiling) and `kRetainBehind = 300` (the
// history kept behind the playhead). A sample count is not a budget: 900 video samples is ~30 s at 30 fps but ~19 s
// at 48 fps, and 300 AAC samples is ~6 s while 300 video samples is ~10 s — so the two tracks drifted apart, the
// forward window moved with the frame rate, and nothing anywhere counted BYTES, which is the resource that actually
// runs out. This store replaces both with what mpv's `--demuxer-max-back-bytes` / `--demuxer-max-bytes` express:
//   * a forward target in PRESENTATION TIME  (`bufferAheadMs`,  default 60 000),
//   * a retention window in PRESENTATION TIME (`retainBehindMs`, default 30 000),
//   * a hard cap in BYTES                     (`storeBudgetBytes`; the managed side derives it from the selected representation's
//                                              bitrate, StoreBudgetForBitrate, 16-128 MiB; 0 = 32 MiB) that overrides both.
//
// It also owns the two things the seek planner needs and the old code threw away: the ascending KEYFRAME TABLE that
// `cenc::ParseSegment` fills from the sync samples it already marks, and the BUFFERED RANGE computation a scrub bar
// draws as its loaded band.
//
// Included from PrInternal.h (and, for the `cenc::Sample` definition it is written against, from CencMediaSource.h
// right after `namespace cenc` closes). It never includes a Media Foundation header of its own: everything here is
// pure C++ over already-demuxed samples, which is what lets FgPrProbeFile run the demuxer gate on a box with no GPU,
// no CDM and no network.
#pragma once

#include "CencMediaSource.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <limits>
#include <iterator>   // make_move_iterator — SpliceSamples moves incoming samples in without copying them
#include <memory>
#include <mutex>
#include <vector>

namespace fgpr {

// ── budget constants (the descriptor's 0 means "take the default") ──────────────────────────────────────────────────
inline constexpr size_t   kSlabBytes             = 64 * 1024;             // one slab; every pooled buffer is a multiple
inline constexpr uint64_t kDefaultStoreBudget    = 32ull * 1024 * 1024;   // §3.5 allocation table; what an unknown bitrate gets
inline constexpr uint64_t kMinStoreBudget        = 16ull * 1024 * 1024;   // F040: the floor of a derived budget
inline constexpr uint64_t kMaxStoreBudget        = 128ull * 1024 * 1024;  // F040: the ceiling (two live sessions => at most 256 MiB of media)
inline constexpr int64_t  kDefaultRetainBehindMs = 30000;
inline constexpr int64_t  kDefaultBufferAheadMs  = 60000;
inline constexpr int      kKeyframeCap           = 4096;                  // a 4-hour broadcast at a 4 s GOP
// How far apart two samples may start before the buffered RANGE (not CanSeekTo) calls it a hole. Segment boundaries
// rarely line up to the tick (a composition offset on the first frame, a rounding in tfdt), and in decode order a
// reordered P frame starts past the running reach of the frame before it; neither is missing media. Half a second is
// far below any real gap (a whole missing segment is seconds) and far above those artefacts.
inline constexpr int64_t  kContiguityToleranceMs = 500;

/// F040: the media byte budget one video representation needs to hold the WHOLE configured window (`aheadMs` + `retainMs` of
/// presentation time) at its declared bandwidth: window seconds x bytes per second x 1.2 headroom (VBR peaks over the declared
/// average), grossed up by 4/3 because the video track only owns three quarters of the budget (SegmentStore::VideoBudget; the
/// audio quarter is what an AAC track needs several times over), clamped to [kMinStoreBudget, kMaxStoreBudget]. The pool's idle
/// scratch is NOT part of it (it has its own cap, SlabPool::Give). Integer math only (headroom x gross-up = 8/5), so the managed
/// twin (ProtectedVideoSession.StoreBudgetFor) and the tests agree to the byte. An unknown bitrate keeps the 32 MiB default.
inline uint64_t StoreBudgetForBitrate(int64_t videoBitsPerSecond, int64_t aheadMs, int64_t retainMs)
{
    if (videoBitsPerSecond <= 0) return kDefaultStoreBudget;
    const int64_t window = (aheadMs > 0 ? aheadMs : kDefaultBufferAheadMs) + (retainMs > 0 ? retainMs : kDefaultRetainBehindMs);
    const uint64_t windowBytes = (uint64_t)window * (uint64_t)videoBitsPerSecond / 8000ull;   // ms x bit/s / 8000 = bytes
    const uint64_t budget = windowBytes / 5ull * 8ull;                                          // x 1.2 x 4/3
    return budget < kMinStoreBudget ? kMinStoreBudget : (budget > kMaxStoreBudget ? kMaxStoreBudget : budget);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  SlabPool — the 64 KiB slab allocation of §3.5, in the only shape a fragmented-MP4 parser can consume.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// `cenc::ParseSegment` walks a CONTIGUOUS segment body (moof + mdat, box offsets relative to the moof's own start), so
// a slab list with per-slab pointers is not an option: the parser would have to be given a gather-read API and every
// `rd32(p + off)` in the demuxer would have to learn about slab boundaries. That is a rewrite of the one piece of this
// code that is known to work. What IS achievable — and is what the allocation table is actually asking for — is that a
// segment fetch stops calling the allocator: buffers are taken from a pool in 64 KiB granules, filled, parsed, and
// GIVEN BACK with their capacity intact, so after the first few segments a fetch allocates nothing at all. The pool's
// own total is capped at twice the largest buffer it has seen (and by the session's `storeBudgetBytes`) so an idle
// session does not sit on megabytes of scratch, and it is NOT counted against the media byte cap (SegmentStore::MediaBytes).
class SlabPool
{
  public:
    void Configure(uint64_t capacityBytes)
    {
        std::lock_guard<std::mutex> g(m_mx);
        m_capacityBytes = capacityBytes ? capacityBytes : kDefaultStoreBudget;
    }

    /// A buffer with at least `minBytes` of capacity, sized to `minBytes`. Recycled when the pool holds one big
    /// enough; otherwise allocated once, rounded up to a whole number of slabs so it can be reused by a later,
    /// slightly larger segment instead of being thrown away.
    std::vector<uint8_t> Take(size_t minBytes)
    {
        const size_t want = ((minBytes + kSlabBytes - 1) / kSlabBytes) * kSlabBytes;
        {
            std::lock_guard<std::mutex> g(m_mx);
            // Best fit over a pool that never holds more than a handful of buffers: the smallest one that still fits,
            // so a 6 MiB scratch is not handed out for a 200 KiB audio segment and then trimmed.
            size_t best = m_free.size();
            for (size_t i = 0; i < m_free.size(); i++)
                if (m_free[i].capacity() >= minBytes && (best == m_free.size() || m_free[i].capacity() < m_free[best].capacity()))
                    best = i;
            if (best < m_free.size())
            {
                std::vector<uint8_t> out = std::move(m_free[best]);
                m_free.erase(m_free.begin() + (ptrdiff_t)best);
                m_pooledBytes -= out.capacity() < m_pooledBytes ? out.capacity() : m_pooledBytes;
                out.resize(minBytes);   // capacity already suffices — no reallocation
                return out;
            }
        }
        std::vector<uint8_t> fresh;
        fresh.reserve(want ? want : kSlabBytes);
        fresh.resize(minBytes);
        return fresh;
    }

    /// Hand a buffer back. `clear()` keeps the capacity, which is the whole point; a buffer that would push the pool
    /// past twice the largest segment buffer seen (one video + one audio scratch is all a fetch pair ever needs) or
    /// past the session budget is simply dropped.
    void Give(std::vector<uint8_t>&& buf)
    {
        if (buf.capacity() == 0) return;
        std::lock_guard<std::mutex> g(m_mx);
        if (buf.capacity() > m_largestCapacity) m_largestCapacity = buf.capacity();
        const uint64_t limit = std::min<uint64_t>(m_capacityBytes, 2ull * m_largestCapacity);
        if (m_pooledBytes + buf.capacity() > limit) return;   // over the pool cap: let it go
        m_pooledBytes += buf.capacity();
        buf.clear();
        m_free.push_back(std::move(buf));
    }

    uint64_t PooledBytes() const
    {
        std::lock_guard<std::mutex> g(m_mx);
        return m_pooledBytes;
    }

    void Clear()
    {
        std::lock_guard<std::mutex> g(m_mx);
        m_free.clear();
        m_pooledBytes = 0;
        m_largestCapacity = 0;
    }

  private:
    mutable std::mutex m_mx;
    std::vector<std::vector<uint8_t>> m_free;
    uint64_t m_pooledBytes = 0;
    uint64_t m_largestCapacity = 0;   // the biggest buffer ever given back: the pool holds at most twice this
    uint64_t m_capacityBytes = kDefaultStoreBudget;
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  KeyframeTable — every sync sample the demuxer has SEEN, ascending, fixed capacity, allocated once.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// `int64_t[4096]` per session (§3.5): a 4-minute video at one IDR per second is 240 entries. The table is APPEND-ONLY
// in the common case (segments arrive in order) but a seek makes the feeder jump backwards, so an out-of-order time is
// inserted in place rather than rejected — a scrub bar that lost the keyframes behind the playhead would refuse every
// backward Instant seek. Duplicates are dropped: re-parsing a segment after a seek must not grow the table forever.
class KeyframeTable
{
  public:
    void Append(int64_t ms)
    {
        if (ms < 0) return;
        std::lock_guard<std::mutex> g(m_mx);
        if (m_count > 0 && m_times[m_count - 1] == ms) return;
        if (m_count > 0 && m_times[m_count - 1] < ms)
        {
            if (m_count >= kKeyframeCap) return;   // full: the table is a hint, never a correctness input
            m_times[m_count++] = ms;
            return;
        }
        // Out of order (a backward seek re-fed an earlier segment): binary-search the insertion point.
        int lo = 0, hi = m_count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (m_times[mid] < ms) lo = mid + 1; else hi = mid;
        }
        if (lo < m_count && m_times[lo] == ms) return;    // already known
        if (m_count >= kKeyframeCap) return;
        for (int i = m_count; i > lo; i--) m_times[i] = m_times[i - 1];
        m_times[lo] = ms;
        m_count++;
    }

    int Count() const { std::lock_guard<std::mutex> g(m_mx); return m_count; }

    /// Copy up to `cap` entries out and return the TOTAL count, so a caller with a smaller buffer knows to re-ask.
    int CopyTo(int64_t* out, int cap) const
    {
        std::lock_guard<std::mutex> g(m_mx);
        const int n = cap < m_count ? cap : m_count;
        if (out && n > 0) memcpy(out, m_times, (size_t)n * sizeof(int64_t));
        return m_count;
    }

    /// The largest entry at or before `ms`, or -1 when the table has nothing that early.
    int64_t FloorOf(int64_t ms) const
    {
        std::lock_guard<std::mutex> g(m_mx);
        int64_t best = -1;
        for (int i = 0; i < m_count; i++)
        {
            if (m_times[i] > ms) break;
            best = m_times[i];
        }
        return best;
    }

    void Clear() { std::lock_guard<std::mutex> g(m_mx); m_count = 0; }

  private:
    mutable std::mutex m_mx;
    int64_t m_times[kKeyframeCap] = {};
    int m_count = 0;
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Sample-list algorithms — the time-window policy, kept next to the budget it enforces.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// What one demuxed sample costs the budget. `capacity()` rather than `size()` because that is the memory actually
/// held, and the CENC extension data (IV + subsample map) is counted because a 4K keyframe's map is not free.
inline uint64_t SampleFootprint(const cenc::Sample& s)
{
    return (uint64_t)s.data.capacity() + (uint64_t)s.iv.capacity() +
           (uint64_t)(s.subsamples.capacity() * sizeof(cenc::Subsample)) + (uint64_t)sizeof(cenc::Sample);
}

inline uint64_t SampleFootprint(const std::vector<cenc::Sample>& v)
{
    uint64_t n = 0;
    for (auto const& s : v) n += SampleFootprint(s);
    return n;
}

inline int64_t TicksToMs(uint64_t ticks, uint64_t timescale)
{
    return timescale ? (int64_t)((ticks * 1000ULL) / timescale) : 0;
}

inline uint64_t MsToTicks(int64_t ms, uint64_t timescale)
{
    if (ms < 0) ms = 0;
    return timescale ? ((uint64_t)ms * timescale) / 1000ULL : 0;
}

/// Can a seek to `targetMs` be served from what is ALREADY in `samples`? MOVED VERBATIM from
/// CencMediaStream::CanSeekTo — this is what turns a backward scrub into an instant reposition instead of three
/// serial TLS-handshaking GETs. Video needs a KEYFRAME at or before the target (that is what Start() repositions to);
/// audio only needs coverage. Both need CONTIGUOUS coverage past the target — a buffer with a hole in it (left by an
/// earlier forward seek) must not report the far side as seekable.
inline bool CanSeekToIn(const std::vector<cenc::Sample>& samples, uint64_t timescale, int64_t targetMs,
                        bool requireKeyframe)
{
    if (timescale == 0 || samples.empty()) return false;
    if (targetMs < 0) targetMs = 0;
    const uint64_t target = MsToTicks(targetMs, timescale);
    size_t anchor = samples.size();
    for (size_t i = 0; i < samples.size(); i++)
    {
        if (samples[i].timeTicks > target) break;
        if (!requireKeyframe || samples[i].keyframe) anchor = i;
    }
    if (anchor == samples.size()) return false;
    uint64_t reach = samples[anchor].timeTicks;
    for (size_t i = anchor; i < samples.size(); i++)
    {
        if (samples[i].timeTicks > reach) break;   // gap in the buffer — coverage ends here
        const uint64_t end = samples[i].timeTicks + samples[i].durTicks;
        if (end > reach) reach = end;
    }
    return reach > target;
}

/// The buffered ranges as ascending (startMs, endMs) PAIRS — FgPrSessionGetBuffered's answer. A sample whose start is
/// past the running reach (by more than kContiguityToleranceMs) begins a NEW pair: that is exactly the hole a forward
/// seek leaves, and reporting it as one continuous band is how a scrub bar ends up claiming a position it cannot
/// actually play. Writes at most `capPairs` pairs into `out` (2 * capPairs int64s) and returns the TOTAL number of pairs.
inline int ComputeBufferedPairs(const std::vector<cenc::Sample>& samples, uint64_t timescale, int64_t* out,
                                int capPairs)
{
    if (timescale == 0 || samples.empty()) return 0;
    const uint64_t tolerance = MsToTicks(kContiguityToleranceMs, timescale);
    int pairs = 0;
    uint64_t rangeStart = samples.front().timeTicks;
    uint64_t reach = samples.front().timeTicks;
    auto emit = [&](uint64_t startTicks, uint64_t endTicks) {
        if (out && pairs < capPairs)
        {
            out[pairs * 2 + 0] = TicksToMs(startTicks, timescale);
            out[pairs * 2 + 1] = TicksToMs(endTicks, timescale);
        }
        pairs++;
    };
    for (size_t i = 0; i < samples.size(); i++)
    {
        const uint64_t start = samples[i].timeTicks;
        const uint64_t end = start + samples[i].durTicks;
        if (start > reach + tolerance)
        {
            emit(rangeStart, reach);
            rangeStart = start;
            reach = end;
            continue;
        }
        if (end > reach) reach = end;
    }
    emit(rangeStart, reach);
    return pairs;
}

/// Where a video Start(reposition) to `targetTicks` begins delivering: the index of the keyframe at or before the target
/// (the latest one; on equal presentation time the later in decode order), else the earliest keyframe that starts
/// within `toleranceTicks` AFTER it (a first frame a composition offset past the grid boundary the target was
/// computed on), else `samples.size()` — "no keyframe can serve this target". It is NEVER sample 0 by default: a
/// fallback to the oldest retained sample (what Start did) delivers a mid-GOP P/B frame flagged discontinuous, and
/// the decoder has no reference for it (F034 / F046). Audio samples are all sync samples, so this is also the audio pick.
inline size_t PickStartKeyframe(const std::vector<cenc::Sample>& samples, uint64_t targetTicks, uint64_t toleranceTicks)
{
    size_t best = samples.size();
    uint64_t bestTime = 0;
    for (size_t i = 0; i < samples.size(); i++)
    {
        auto const& sample = samples[i];
        if (!sample.keyframe || sample.timeTicks > targetTicks) continue;
        if (best == samples.size() || sample.timeTicks >= bestTime)
        {
            best = i;
            bestTime = sample.timeTicks;
        }
    }
    if (best != samples.size()) return best;

    size_t after = samples.size();
    uint64_t afterTime = 0;
    for (size_t i = 0; i < samples.size(); i++)
    {
        auto const& sample = samples[i];
        if (!sample.keyframe || sample.timeTicks <= targetTicks || sample.timeTicks - targetTicks > toleranceTicks) continue;
        if (after == samples.size() || sample.timeTicks < afterTime)
        {
            after = i;
            afterTime = sample.timeTicks;
        }
    }
    return after;
}

/// The position a re-attach should resume `startMs` at (F046). When the buffer still has a keyframe the resume can
/// begin from (PickStartKeyframe), or `startMs` is not inside any buffered range (then the source's Start finds
/// nothing and the feeder fetches the containing segment), it is returned unchanged. When it IS inside a buffered
/// range whose keyframe at/before it was trimmed away (the byte cap evicts history up to the delivery cursor, and the
/// playhead runs a little behind the cursor), the resume moves forward to the first keyframe of that range: at most
/// one GOP later, instead of starting on a P/B frame with no reference.
inline int64_t ResumeStartMs(const std::vector<cenc::Sample>& samples, uint64_t timescale, int64_t startMs)
{
    if (timescale == 0 || samples.empty() || startMs < 0) return startMs;
    const uint64_t tolerance = MsToTicks(kContiguityToleranceMs, timescale);
    const uint64_t startTicks = MsToTicks(startMs, timescale);
    if (PickStartKeyframe(samples, startTicks, tolerance) != samples.size()) return startMs;

    int64_t pairs[2 * 64];
    const int total = ComputeBufferedPairs(samples, timescale, pairs, 64);
    const int n = total < 64 ? total : 64;
    int64_t rangeEnd = -1;
    for (int i = 0; i < n; i++)
        if (pairs[i * 2] - kContiguityToleranceMs <= startMs && startMs < pairs[i * 2 + 1]) { rangeEnd = pairs[i * 2 + 1]; break; }
    if (rangeEnd < 0) return startMs;   // not buffered: the feeder fetches it

    int64_t firstKf = -1;
    for (auto const& sample : samples)
    {
        if (!sample.keyframe || sample.timeTicks <= startTicks) continue;
        const int64_t ms = TicksToMs(sample.timeTicks, timescale);
        if (ms >= rangeEnd) continue;
        if (firstKf < 0 || ms < firstKf) firstKf = ms;
    }
    return firstKf >= 0 ? firstKf : startMs;
}

/// Trim history behind the playhead by TIME and by BYTES, keeping `next` pointing at the same sample. Replaces
/// `kRetainBehind = 300` (plan bug S4). Two rules, in this order:
///   1. drop anything that ends more than `retainBehindMs` before the playhead;
///   2. if the stream still exceeds its slice of `budgetBytes`, keep dropping the oldest — the byte cap is hard and a
///      long-GOP 4K stream must not be allowed to hold 30 s of history just because the clock says it may.
/// NOTHING at or after `next` is ever dropped: those samples have not been handed to Media Foundation yet, and the
/// ones before `next` that MF already took are copies (MakeSample memcpy's into an IMFMediaBuffer), so erasing them
/// here cannot pull memory out from under the pipeline. Returns how many samples were dropped.
///
/// KEYFRAME ALIGNED (F034 / F046; ExoPlayer's `discardTo(toKeyframe)`, Shaka's whole-GOP eviction). The two rules pick a
/// sample-exact cut, but what is left must still START on a sync sample: a buffer whose head is a P/B frame cannot be
/// repositioned to or re-attached from, and Start() used to fall back to sample 0 there and deliver it. So the cut is
/// moved BACK to the last keyframe at or before the playhead's GOP (keeping up to one extra GOP of history) whenever
/// the byte cap affords it, and otherwise FORWARD to the next keyframe at or before `next` (dropping up to one extra
/// GOP of already-delivered history): the byte rule always wins over the window and the reserve, never the reverse,
/// or the 2026-09-22 cap deadlock below returns. With no keyframe to land on the sample-exact cut stands.
///
/// `playheadHintTicks`: the real playhead (presentation ticks), when the caller knows it. The delivery cursor runs
/// ahead of the clock by MF's decode/render queue, so the window and the keyframe the playhead's GOP starts at are
/// measured from min(hint, cursor) - a re-attach resumes at the playhead, not the cursor. UINT64_MAX = the cursor.
///
/// `outByteBudgetCut` (optional): set to true iff the byte cap is what removed more than the (aligned) time window would
/// have. A pure rule-1 time trim is normal per-segment housekeeping and must be invisible to a caller tracking "did
/// coverage get cut out from under a reader" — the feeder's progress guard (PrSession.cpp) watches exactly that signal, and it
/// firing every segment would reset the guard so often it could never notice a REAL cut (TakeSamples, a truncating
/// SpliceSamples, or genuinely running out of budget).
///
/// `reserveBytes`: room rule 2 must leave FREE under the budget — the feeder passes the size of the segment it is
/// about to fetch. Without it the cap and the time window deadlocked (2026-09-22, 1080p): the store filled to exactly
/// the budget with ~30 s of history the 30 s window said to keep, the planner refused every fetch at `>= budget`,
/// rule 2 evicted nothing at `> budget`, and playback ran into the end of the buffer and starved forever ("video
/// starved at sample 672 — awaiting fetch", never followed by a fetch). History behind the playhead is the ONE thing
/// that may yield to forward demand; nothing at/after `next` ever does, so an all-ahead buffer still refuses.
inline size_t TrimBehindByTime(std::vector<cenc::Sample>& samples, size_t& next, uint64_t timescale,
                               int64_t retainBehindMs, uint64_t budgetBytes, uint64_t& bytesCounter,
                               bool* outByteBudgetCut = nullptr, uint64_t reserveBytes = 0,
                               uint64_t playheadHintTicks = (std::numeric_limits<uint64_t>::max)())
{
    if (outByteBudgetCut) *outByteBudgetCut = false;
    if (samples.empty() || next == 0) return 0;
    if (retainBehindMs < 0) retainBehindMs = 0;

    const uint64_t cursorTicks = next < samples.size()
        ? samples[next].timeTicks
        : samples.back().timeTicks + samples.back().durTicks;
    const uint64_t playheadTicks = playheadHintTicks < cursorTicks ? playheadHintTicks : cursorTicks;
    const uint64_t windowTicks = MsToTicks(retainBehindMs, timescale);
    const uint64_t cutoff = playheadTicks > windowTicks ? playheadTicks - windowTicks : 0;

    size_t drop = 0;
    uint64_t freed = 0;
    while (drop < next && (samples[drop].timeTicks + samples[drop].durTicks) <= cutoff)
    {
        freed += SampleFootprint(samples[drop]);
        drop++;
    }
    const size_t droppedByTime = drop;   // rule 1's tally, before rule 2 gets a chance to add to it
    // Rule 2: the byte cap overrides the time window — and it must leave `reserveBytes` free for the next fetch.
    const uint64_t cap = budgetBytes > reserveBytes ? budgetBytes - reserveBytes : 0;
    uint64_t held = bytesCounter > freed ? bytesCounter - freed : 0;
    while (drop < next && held > cap)
    {
        const uint64_t cost = SampleFootprint(samples[drop]);
        freed += cost;
        held = held > cost ? held - cost : 0;
        drop++;
    }

    // Keyframe alignment. `drop` is the sample-exact cut; `limit` is how far back we would like to keep history: not
    // past the keyframe the playhead's GOP starts at (nothing older is needed to resume from the playhead).
    if (drop > 0 && !(drop < samples.size() && samples[drop].keyframe))
    {
        const size_t scanEnd = std::min(next + 1, samples.size());
        size_t protect = samples.size();   // the last keyframe presented at or before the playhead
        for (size_t i = 0; i < scanEnd; i++)
            if (samples[i].keyframe && samples[i].timeTicks <= playheadTicks) protect = i;
        const size_t limit = protect < drop ? protect : drop;
        size_t back = samples.size();      // the lowest keyframe we may roll back to: the last one at or before `limit`
        for (size_t i = 0; i <= limit && i < samples.size(); i++)
            if (samples[i].keyframe) back = i;

        size_t aligned = samples.size();
        if (back != samples.size())
        {
            // Walk the keyframes from `back` up towards `drop`: the first (oldest) one whose extra kept history still
            // fits under the cap wins.
            uint64_t extra = 0;
            for (size_t j = back; j < drop; j++) extra += SampleFootprint(samples[j]);
            size_t c = back;
            while (c < drop)
            {
                if (held + extra <= cap) { aligned = c; break; }
                do { extra -= SampleFootprint(samples[c]); c++; } while (c < drop && !samples[c].keyframe);
            }
        }
        if (aligned != samples.size())
        {
            for (size_t j = aligned; j < drop; j++) freed -= SampleFootprint(samples[j]);
            drop = aligned;
        }
        else
        {
            // The cap cannot afford to keep any keyframe-led history: cut forward to the next keyframe the cursor has
            // not reached (more history out, which only helps the byte rule). None: the sample-exact cut stands.
            const size_t fmax = std::min(next, samples.size() - 1);
            size_t fwd = samples.size();
            for (size_t i = drop + 1; i <= fmax; i++)
                if (samples[i].keyframe) { fwd = i; break; }
            if (fwd != samples.size())
            {
                for (size_t j = drop; j < fwd; j++) freed += SampleFootprint(samples[j]);
                drop = fwd;
            }
        }
    }
    if (outByteBudgetCut && drop > droppedByTime) *outByteBudgetCut = true;
    if (drop == 0) return 0;

    samples.erase(samples.begin(), samples.begin() + (ptrdiff_t)drop);
    next -= drop;
    bytesCounter = bytesCounter > freed ? bytesCounter - freed : 0;
    return drop;
}

// ────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  RepBook / FormatGenAt — which video format each buffered sample belongs to, pure so a console test exe can drive it.
// ────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//
// A representation switch used to announce MEStreamFormatChanged when the new run was SPLICED, which is up to a whole
// buffer (30-60 s) before the first new sample reaches the decoder. Switching at the end of the buffer makes that gap the
// normal case, so every sample now carries the generation of the representation it was demuxed from (cenc::Sample::repGen)
// and the stream announces a change when DELIVERY crosses into a different generation: forwards over the splice, and
// backwards too (a seek back into old-representation samples re-announces the old format).

/// One video format generation: the init the samples of this generation were demuxed against, and the manifest
/// representation index it came from (-1 = the opening representation, whose index native never learns).
struct RepFormat
{
    uint16_t gen = 0;
    int32_t index = -1;
    cenc::InitInfo info;
};

/// The generations one video stream knows. `appendGen` is what freshly appended samples are stamped with (the newest
/// representation); `deliveredGen` is the one Media Foundation's current media type describes.
struct RepBook
{
    std::vector<RepFormat> formats;
    uint16_t appendGen = 0;
    uint16_t deliveredGen = 0;

    const RepFormat* Find(uint16_t gen) const
    {
        for (auto const& f : formats)
            if (f.gen == gen) return &f;
        return nullptr;
    }

    /// A generation number nothing uses: the one after `appendGen`, skipping any still held (the counter is 16 bits; a
    /// session would need 65536 switches in one book to wrap, and pruning keeps the book to the generations in use).
    uint16_t NextFreeGen() const
    {
        uint16_t g = appendGen;
        do { g = (uint16_t)(g + 1); } while (Find(g) != nullptr);
        return g;
    }

    /// Record a generation and make it the one new samples are stamped with.
    void Register(uint16_t gen, int32_t index, const cenc::InitInfo& info)
    {
        for (auto& f : formats)
            if (f.gen == gen) { f.index = index; f.info = info; appendGen = gen; return; }
        RepFormat f;
        f.gen = gen;
        f.index = index;
        f.info = info;
        formats.push_back(std::move(f));
        appendGen = gen;
    }

    /// Forget generations no buffered sample refers to (keeping the append and delivered ones): a long session of switches
    /// must not grow the book without bound.
    void Prune(const std::vector<cenc::Sample>& samples)
    {
        formats.erase(std::remove_if(formats.begin(), formats.end(), [&](const RepFormat& f) {
            if (f.gen == appendGen || f.gen == deliveredGen) return false;
            for (auto const& s : samples)
                if (s.repGen == f.gen) return false;
            return true;
        }), formats.end());
    }
};

/// The format generation of the sample delivery will hand over next, or `fallback` (the generation already delivered)
/// when the cursor has drained the buffer: nothing to announce until a sample arrives.
inline uint16_t FormatGenAt(const std::vector<cenc::Sample>& samples, size_t next, uint16_t fallback)
{
    return next < samples.size() ? samples[next].repGen : fallback;
}

// ────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  SpliceSamples / IsAscending / ContiguousAheadMs — pure, lock-free, over just the vector + cursor, so a console
//  test exe can exercise the splice policy without linking Media Foundation.
// ────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//
// ORDERING KEY DECISION: `samples` is kept ascending in `decodeTicks`, NOT `timeTicks`.
//
// Evidence:
//   * `cenc::ParseMoof` (CencMediaSource.h) walks each `trun` in the order the muxer wrote it — DECODE order — and
//     `runningDecodeTicks` only ever increases (`runningDecodeTicks += dur` per sample, CencMediaSource.h ~line 689).
//     `timeTicks` (presentation) is computed from that same decode time plus a per-sample composition offset
//     (`cto`), which is exactly what makes a B-frame's PRESENTATION time fall behind the frame before it in decode
//     order — decodeTicks never does that.
//   * The representation-switch feeder (PrSession.cpp ~511-523) reads `st.ticks[kVideo]` into `ParseSegment`'s
//     `runningDecodeTicks` and writes it back — that counter is NEVER reset across a segment append OR a quality
//     switch, so decodeTicks is monotonically non-decreasing across a stream's ENTIRE feed, not just within one
//     segment. Nothing else in the feed offers that guarantee.
//   * The existing coverage code already assumes decode-order storage and tolerates the resulting small
//     presentation reordering rather than requiring strict ascent: `kContiguityToleranceMs`'s own comment says "in
//     decode order a reordered P frame starts past the running reach of the frame before it; neither is missing
//     media", and `ComputeBufferedPairs` never breaks on a sample whose `timeTicks` is merely less than a LATER
///     sample's — it tracks a running MAX (`reach`) and only opens a new range when a start lands beyond
//     `reach + kContiguityToleranceMs`. `ContiguousAheadMs` below and the hole detector in CencMediaStream follow
//     the identical pattern for the same reason.
//   * "Already delivered" is a DECODE-order fact: delivery walks the vector and the vector is in decode order, so
//     the straddle filter drops `decodeTicks < samples[next].decodeTicks`. (It first filtered on `timeTicks` and lost
//     the B frames decoded after the cursor's P frame but displayed before it — FeedTests B18b.)
//   * The truncating splice's STALENESS test alone stays on `timeTicks`: its run starts at a keyframe, the earliest
//     presentation time of its GOP, and the boundary it is checked against was picked in presentation ms
//     (`NextSampleTimeMs()` in PrSession.cpp) — a keyframe earlier than the cursor's presentation time would rewind
//     the picture whatever its decode position.
//
// Equal decodeTicks CAN legitimately occur (a muxer emitting a zero-duration sample at a segment's tail), so
// `IsAscending` requires only NON-DECREASING order, not strict.

/// The result of one SpliceSamples call — everything a caller needs to update its own byte ledger and
/// generation counter without re-deriving it from the mutated vector.
struct SpliceOutcome
{
    size_t at = 0;               // the array index the incoming run landed at (meaningless when `stale`)
    size_t removed = 0;
    size_t added = 0;
    uint64_t freedBytes = 0;
    uint64_t addedBytes = 0;
    bool cutCoverage = false;    // buffered coverage was cut short — bump the caller's generation counter
    bool stale = false;          // truncating splice refused: the cursor is already past the boundary
};

/// Insert `incoming` (already in decode order — see the ORDERING KEY DECISION above) so `samples` stays
/// non-decreasing in `decodeTicks` and nothing the delivery cursor (`next`) has already reached, BY PRESENTATION
/// TIME, is queued again. `next` is updated to keep pointing at the same logical sample.
///
///  - Pure append / wholly-ahead replace / wholly-behind replace: today's behaviour (search the array for the run's
///    decode-time span, erase-and-insert there).
///  - NON-truncating straddle (the run's decode span overlaps `next`): the delivery point cannot be moved backward,
///    so every incoming sample whose PRESENTATION time is earlier than `samples[next]`'s is dropped from `incoming`
///    BEFORE the span is recomputed and the splice proceeds. If nothing survives, `samples`/`next` are left
///    untouched and `added == 0`.
///  - TRUNCATING (`truncateAfter`, a representation switch): the replacement must start at a keyframe and is
///    decoded from its first sample onward, so it can never be trimmed at the front — it lands whole or is refused.
///    Refused (`stale = true`, nothing mutated) when the run's first PRESENTATION time is behind what the cursor
///    has already reached: `samples[next].timeTicks` when the cursor is still inside the buffer, or
///    `samples.back().timeTicks` when it has drained the buffer (`next == samples.size()`) — landing at/after the
///    last delivered sample in that drained case is fine. Otherwise everything from the landing point to the end of
///    the vector is erased (the old representation's tail) and `incoming` takes its place; `cutCoverage` is set
///    whenever that erase was non-empty, because a truncating splice by definition discards whatever coverage used
///    to be there.
///  - `cutCoverage` is also set for a NON-truncating replace whose incoming run's presentation end lands earlier
///    than the range it replaced (a short demux re-fetch covering less than what it overwrote).
///  - Byte accounting uses `SampleFootprint`.
inline SpliceOutcome SpliceSamples(std::vector<cenc::Sample>& samples, size_t& next,
                                   std::vector<cenc::Sample>&& incoming, bool truncateAfter)
{
    SpliceOutcome out;
    if (incoming.empty()) return out;

    // The decode-time span [startTicks, endTicks) of `incoming` — the array-position search key.
    auto decodeSpan = [](const std::vector<cenc::Sample>& v, uint64_t& startTicks, uint64_t& endTicks) {
        startTicks = v.front().decodeTicks;
        endTicks = startTicks;
        for (auto const& s : v)
        {
            const uint64_t end = s.decodeTicks + s.durTicks;
            if (end > endTicks) endTicks = end;
        }
    };

    if (truncateAfter)
    {
        const bool stale = next < samples.size()
            ? incoming.front().timeTicks < samples[next].timeTicks
            : (!samples.empty() && incoming.front().timeTicks < samples.back().timeTicks);
        if (stale) { out.stale = true; return out; }

        uint64_t startTicks, endTicks;
        decodeSpan(incoming, startTicks, endTicks);
        size_t lo = samples.size();
        for (size_t i = 0; i < samples.size(); i++)
            if (samples[i].decodeTicks >= startTicks) { lo = i; break; }
        if (lo < next) lo = next;                    // never splice earlier than the delivery cursor
        const size_t hi = samples.size();             // cut everything from the landing point to the tail

        uint64_t freed = 0;
        for (size_t i = lo; i < hi; i++) freed += SampleFootprint(samples[i]);
        out.at = lo;
        out.removed = hi - lo;
        out.added = incoming.size();
        out.freedBytes = freed;
        for (auto const& s : incoming) out.addedBytes += SampleFootprint(s);
        out.cutCoverage = out.removed > 0;

        samples.erase(samples.begin() + (ptrdiff_t)lo, samples.begin() + (ptrdiff_t)hi);
        samples.insert(samples.begin() + (ptrdiff_t)lo, std::make_move_iterator(incoming.begin()),
                       std::make_move_iterator(incoming.end()));
        // `lo >= next` always holds here (clamped above), so the cursor's logical position never shifts.
        return out;
    }

    uint64_t startTicks, endTicks;
    decodeSpan(incoming, startTicks, endTicks);
    size_t lo = samples.size();
    for (size_t i = 0; i < samples.size(); i++)
        if (samples[i].decodeTicks >= startTicks) { lo = i; break; }
    size_t hi = lo;
    while (hi < samples.size() && samples[hi].decodeTicks < endTicks) hi++;

    if (lo < next && hi > next)
    {
        // Straddles the delivery cursor: Media Foundation already has everything before samples[next]. Delivery walks
        // the vector, and the vector is in DECODE order — so "already delivered" is a decode-time fact, not a
        // presentation-time one. Filtering on timeTicks (what this first did) also threw away the B frames that are
        // decoded AFTER the cursor's P frame but displayed BEFORE it: undelivered samples, two per GOP, silently lost
        // (FeedTests B18b: 38 samples where 40 went in).
        const uint64_t deliveredDecode = samples[next].decodeTicks;
        incoming.erase(std::remove_if(incoming.begin(), incoming.end(),
                                      [&](const cenc::Sample& s) { return s.decodeTicks < deliveredDecode; }),
                      incoming.end());
        if (incoming.empty()) return out;   // nothing survives: leave samples/next untouched, added == 0
        decodeSpan(incoming, startTicks, endTicks);
        lo = next;
        while (lo < samples.size() && samples[lo].decodeTicks < startTicks) lo++;
        hi = lo;
        while (hi < samples.size() && samples[hi].decodeTicks < endTicks) hi++;
    }

    uint64_t freed = 0;
    for (size_t i = lo; i < hi; i++) freed += SampleFootprint(samples[i]);
    out.at = lo;
    out.removed = hi - lo;
    out.added = incoming.size();
    out.freedBytes = freed;
    for (auto const& s : incoming) out.addedBytes += SampleFootprint(s);

    if (out.removed > 0)
    {
        // A short demux: the incoming run's presentation coverage ends earlier than what it replaced.
        // The max over the replaced range, not its last element: in decode order the last sample of a GOP is a B
        // frame whose presentation end is EARLIER than the P frame decoded before it.
        uint64_t oldEndTime = 0;
        for (size_t i = lo; i < hi; i++) { const uint64_t e = samples[i].timeTicks + samples[i].durTicks; if (e > oldEndTime) oldEndTime = e; }
        uint64_t newEndTime = 0;
        for (auto const& s : incoming) { const uint64_t e = s.timeTicks + s.durTicks; if (e > newEndTime) newEndTime = e; }
        if (newEndTime < oldEndTime) out.cutCoverage = true;
    }

    samples.erase(samples.begin() + (ptrdiff_t)lo, samples.begin() + (ptrdiff_t)hi);
    samples.insert(samples.begin() + (ptrdiff_t)lo, std::make_move_iterator(incoming.begin()),
                   std::make_move_iterator(incoming.end()));
    if (lo < next) next = next - out.removed + out.added;   // wholly behind the cursor: same logical sample
    return out;
}

/// True iff `samples` is non-decreasing in `decodeTicks` (see the ORDERING KEY DECISION comment above for why
/// decodeTicks, and why non-decreasing rather than strict).
inline bool IsAscending(const std::vector<cenc::Sample>& samples)
{
    for (size_t i = 1; i < samples.size(); i++)
        if (samples[i].decodeTicks < samples[i - 1].decodeTicks) return false;
    return true;
}

/// The contiguous PRESENTATION-time window starting at `samples[next]`, stopping at the first gap greater than
/// `kContiguityToleranceMs` — the demand hook's real question ("is the window ahead actually full", not "what is
/// the span between the first and last buffered sample", which is what AheadDurationMsLocked answers and which a
/// hole can satisfy without there being anything playable past it). Walked in ARRAY order (decode order) tracking a
/// running max reach, exactly like ComputeBufferedPairs, so B-frame reordering within a GOP is not mistaken for a
/// hole. 0 when `next` is out of range.
inline int64_t ContiguousAheadMs(const std::vector<cenc::Sample>& samples, size_t next, uint64_t timescale)
{
    if (timescale == 0 || next >= samples.size()) return 0;
    const uint64_t tolerance = MsToTicks(kContiguityToleranceMs, timescale);
    const uint64_t start = samples[next].timeTicks;
    uint64_t reach = start;
    for (size_t i = next; i < samples.size(); i++)
    {
        const uint64_t sStart = samples[i].timeTicks;
        if (sStart > reach + tolerance) break;   // hole: the contiguous window stops here
        const uint64_t sEnd = sStart + samples[i].durTicks;
        if (sEnd > reach) reach = sEnd;
    }
    return reach > start ? (int64_t)(((reach - start) * 1000ULL) / timescale) : 0;
}

/// ContiguousAheadMs, answered incrementally for the demand hook (F037). The hook asks once per DELIVERED sample (30 video
/// + ~47 audio a second) and the walk above is O(buffered samples) under the stream lock the decoder's RequestSample
/// needs. The answer only changes for two reasons: the buffer itself was mutated (an append, a trim, a flush: the
/// stream bumps `version`) or the cursor moved. So the run found by one walk is cached and reused while the cursor
/// walks through it, and the result is EXACTLY the fresh walk's (FeedTests C20b checks that against ContiguousAheadMs
/// over every cursor of a B-frame buffer, with and without a hole), not an approximation:
///   * a walk anchored at `a` tracks a running reach that starts at samples[a].start. Before processing sample i it
///     equals max(start_a, ends of [a, i)). A walk anchored later at n sees only max(start_n, ends of [n, i)), and the
///     two agree from sample n on exactly when the part the later walk never saw, `runMax` = max(start_a, ends of
///     [a, n)), is no larger than samples[n]'s own end (reach is a running max, so from there both are the same value
///     and break at the same sample). `runMax` is advanced as the cursor passes samples (amortised O(1)).
///   * when it IS larger (a reordered P frame ended after the B frame that follows it in decode order), the fresh
///     walk is run only until its reach catches up with `runMax` - a few samples - and then the cached answer applies.
///   * a cursor behind the anchor, at or past the cached run's hole, or in a changed buffer (or timescale) re-anchors
///     with a full walk.
/// Not thread-safe by itself: the stream calls it under its own lock.
struct ContiguousReach
{
    bool valid = false;
    uint64_t version = 0;   // the buffer mutation counter the cached run belongs to
    uint64_t scale = 0;     // the timescale it was walked at (a format crossing can change the stream's)
    size_t cursor = 0;      // the sample index `runMax` is aligned to
    uint64_t runMax = 0;    // max(start of the anchor, ends of [anchor, cursor)): what the cached walk's reach held at `cursor`
    uint64_t reach = 0;     // the cached run's final reach, ticks
    size_t hole = 0;        // the first index past the cached run (samples.size() when nothing broke it)

    static int64_t Ms(uint64_t reachTicks, uint64_t startTicks, uint64_t timescale)
    {
        return reachTicks > startTicks ? (int64_t)(((reachTicks - startTicks) * 1000ULL) / timescale) : 0;
    }

    /// Same answer as ContiguousAheadMs(samples, next, timescale); `ver` is the buffer's current mutation counter.
    int64_t Ahead(const std::vector<cenc::Sample>& samples, size_t next, uint64_t timescale, uint64_t ver)
    {
        if (timescale == 0 || next >= samples.size()) return 0;
        const uint64_t tolerance = MsToTicks(kContiguityToleranceMs, timescale);
        const uint64_t start = samples[next].timeTicks;
        if (valid && ver == version && timescale == scale && next >= cursor && next < hole)
        {
            for (size_t i = cursor; i < next; i++)
            {
                const uint64_t e = samples[i].timeTicks + samples[i].durTicks;
                if (e > runMax) runMax = e;
            }
            cursor = next;
            if (runMax <= start + samples[next].durTicks) return Ms(reach, start, timescale);
            // The anchor saw more than a walk from here would: replay the fresh walk until it has caught up.
            uint64_t f = start;
            for (size_t i = next; i < hole; i++)
            {
                const uint64_t sStart = samples[i].timeTicks;
                if (sStart > f + tolerance) return Ms(f, start, timescale);   // the fresh walk breaks here
                const uint64_t e = sStart + samples[i].durTicks;
                if (e > f) f = e;
                if (f >= runMax) return Ms(reach, start, timescale);          // caught up: identical from here on
            }
            return Ms(f, start, timescale);   // the cached run's own hole ends it (the cached reach was >= f there)
        }
        uint64_t r = start;
        size_t i = next;
        for (; i < samples.size(); i++)
        {
            const uint64_t sStart = samples[i].timeTicks;
            if (sStart > r + tolerance) break;   // hole: the contiguous window stops here
            const uint64_t sEnd = sStart + samples[i].durTicks;
            if (sEnd > r) r = sEnd;
        }
        valid = true;
        version = ver;
        scale = timescale;
        cursor = next;
        runMax = start;
        reach = r;
        hole = i;
        return Ms(r, start, timescale);
    }
};

/// ComputeBufferedPairs, cached per buffer mutation (F037): the feeder asks for the same pairs five or six times a job
/// (CoverageAt) and the walk is O(buffered samples) under the stream lock the decoder needs. The pairs depend only on
/// the sample vector, so one walk serves every ask until the stream's mutation counter `ver` moves. A request for
/// more pairs than the cache holds walks directly. Not thread-safe by itself: the stream calls it under its own lock.
struct BufferedPairsCache
{
    static constexpr int kCapPairs = 64;   // what CoverageAt asks for

    bool valid = false;
    uint64_t version = 0;
    uint64_t scale = 0;
    int total = 0;
    int64_t pairs[kCapPairs * 2] = {};

    /// Same contract as ComputeBufferedPairs: writes at most `capPairs` pairs into `out`, returns the TOTAL.
    int Get(const std::vector<cenc::Sample>& samples, uint64_t timescale, uint64_t ver, int64_t* out, int capPairs)
    {
        if (capPairs > kCapPairs) return ComputeBufferedPairs(samples, timescale, out, capPairs);
        if (!valid || version != ver || scale != timescale)
        {
            total = ComputeBufferedPairs(samples, timescale, pairs, kCapPairs);
            valid = true;
            version = ver;
            scale = timescale;
        }
        const int n = total < capPairs ? total : capPairs;
        if (out)
            for (int k = 0; k < n * 2; k++) out[k] = pairs[k];
        return total;
    }
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  SegmentStore — one per session: the pool, the keyframe table, the budget and the byte ledger.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Held by `std::shared_ptr` and referenced by the session, the CencMediaSource and both CencMediaStreams. That is
// deliberate: Media Foundation can (and does) hold a ref on the source past FgPrSessionDestroy, so the store must
// outlive the session object rather than be a raw pointer into it.
struct SegmentStore
{
    SlabPool pool;
    KeyframeTable keyframes;

    int64_t retainBehindMs = kDefaultRetainBehindMs;
    int64_t bufferAheadMs = kDefaultBufferAheadMs;
    // Atomic since F040: a representation change re-derives it (SetBudget, feeder thread) while the per-stream trim and the
    // snapshot read it on other threads.
    std::atomic<uint64_t> budgetBytes{ kDefaultStoreBudget };

    // The counted half of the memory budget (FgPrSnapshot.storeBytes). Video and audio are counted separately so the
    // per-stream trim can be told how much of the budget it is over.
    std::atomic<uint64_t> videoBytes{0};
    std::atomic<uint64_t> audioBytes{0};

    // The video representation index the DELIVERY cursor is in (what is on screen) and the one the feeder last spliced
    // in (what is being downloaded); -1 until an adaptive switch has happened. Written by the video CencMediaStream, read
    // by FgPrSessionSnapshot. The on-screen one is -1 again when delivery crosses back into the OPENING representation (whose
    // index native never learns). They live HERE, not on the Session: Media Foundation may keep a stream alive past its
    // session, and a stream holds the store by shared_ptr.
    std::atomic<int32_t> displayedRepresentation{-1};
    std::atomic<int32_t> downloadingRepresentation{-1};

    void Configure(int64_t retainMs, int64_t aheadMs, uint64_t budget)
    {
        retainBehindMs = retainMs > 0 ? retainMs : kDefaultRetainBehindMs;
        bufferAheadMs = aheadMs > 0 ? aheadMs : kDefaultBufferAheadMs;
        budgetBytes.store(budget > 0 ? budget : kDefaultStoreBudget, std::memory_order_release);
        pool.Configure(budgetBytes.load(std::memory_order_acquire));
    }

    /// F040: a representation change re-derives the budget (StoreBudgetForBitrate, computed by the caller). 0 = unchanged; a value is
    /// clamped to the derived range. Returns whether the budget moved. Safe from any thread; the trim and the cap gate pick it up on
    /// their next evaluation (a LOWER budget evicts history behind the playhead first, never forward media).
    bool SetBudget(uint64_t budget)
    {
        if (budget == 0) return false;
        const uint64_t clamped = budget < kMinStoreBudget ? kMinStoreBudget : (budget > kMaxStoreBudget ? kMaxStoreBudget : budget);
        if (budgetBytes.exchange(clamped, std::memory_order_acq_rel) == clamped) return false;
        pool.Configure(clamped);
        return true;
    }

    /// What the demuxed media itself costs the budget: the two tracks' sample lists. The byte CAP is judged on each
    /// track against its own slice (VideoBudget / AudioBudget) - the same slices the per-stream trim enforces - and the
    /// pool's idle scratch is not media (it has its own cap, see SlabPool::Give).
    uint64_t MediaBytes() const
    {
        return videoBytes.load(std::memory_order_relaxed) + audioBytes.load(std::memory_order_relaxed);
    }

    /// Everything the session holds resident for the snapshot / diagnostics: the media plus the pool's idle scratch.
    uint64_t Bytes() const { return MediaBytes() + pool.PooledBytes(); }

    /// Video gets three quarters of the byte budget, audio the rest: an AAC track is an order of magnitude smaller
    /// than the video it accompanies, and splitting the cap evenly would starve the video window for no gain.
    uint64_t VideoBudget() const { const uint64_t b = budgetBytes.load(std::memory_order_acquire); return b - b / 4; }
    uint64_t AudioBudget() const { return budgetBytes.load(std::memory_order_acquire) / 4; }
};

}   // namespace fgpr
