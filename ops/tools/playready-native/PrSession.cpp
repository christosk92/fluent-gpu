// PrSession.cpp — one session per source: FgPrSessionCreate / Prefetch / Attach / Detach / Destroy / Play / Pause /
// Seek / SetVolume / SetRate / SetStreamSize / SelectRepresentation / Snapshot / GetKeyframes / GetBuffered /
// GetInitProtection, and the demuxer gate FgPrProbeFile.
//
// WHAT MOVED HERE AND WHAT CHANGED (wavee-0.3-video-engine-implementation.md §1.3, §3.1.1, §3.2.1, §3.3). The one-shot
// RunCustomSourceAttempt fetched the video init, the audio init and FOUR video + FOUR audio segments strictly in
// series, then built the source, spawned a feeder that slept 50 ms at a time against a sample-count ceiling, and ran a
// transport "reconcile" every 80 ms. This file keeps its pieces — BuildCencSource, the feeder's switch boundary rule,
// the seek gate, the paused-seek FrameStep — and replaces the SHAPE:
//   * S1 the carried start position: `startPositionMs` picks the first segment fetched AND the source's first Start;
//   * S2/S9 transport verbs are posted work items applied at once; there is no keep-alive tick and no applied-seq;
//   * S3 the video and audio GETs of a fetch are IN FLIGHT TOGETHER on the shared HttpClient, and a seek cancels an
//     in-flight GET with a cancellation token (HttpFetch::Cancel), not a 10 ms poll;
//   * S4 retention and fetch-ahead are presentation TIME against a BYTE budget (SegmentStore.h), not sample counts;
//   * the feeder is DEMAND-driven: it sleeps on a condition variable and is woken by the playhead draining the forward
//     window (the stream's demand hook), by a seek, a prefetch, an attach or a representation switch — never by a timer
//     (the only timed wait is the back-off after a failed GET);
//   * a seek whose target is not in the store becomes the feeder's next target, and the reposition runs from that
//     fetch's own completion, so FgPrSessionSeek never blocks and a seek issued right after Attach is never dropped.
#include "PrInternal.h"
#include "FeedPlan.h"

using fgpr::Raise;
using fgpr::Runtime;
using fgpr::Session;

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The segment grid.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Spotify addresses segments arithmetically: the i-th media segment is `base + prefix + (startNumber + i*stride) +
// suffix`, and the audio representation rides the SAME grid. Position → index is `ms / segmentLengthMs`.
//
// THE SEGMENT LENGTH RULE. A non-zero FgPrOpenDesc.segmentLengthMs is authoritative and never re-measured. When the
// manifest did not state it (0 — e.g. a test vector opened from a bare MPD URL), positions map with
// `segmentStrideSeconds * 1000` until the first media segment is parsed; that segment's length is then MEASURED from
// its own sample durations (tfdt + the summed trun durations, in the init's timescale) and used for the rest of the
// session (Session::SegLenMs reads it).

static int32_t SegmentOfUnclamped(const Session& s, int64_t ms)
{
    if (ms < 0) ms = 0;
    const int64_t idx = ms / s.SegLenMs();
    return idx > (int64_t)INT_MAX ? INT_MAX : (int32_t)idx;
}

static int64_t SegmentStartMs(const Session& s, int32_t idx) { return (int64_t)idx * s.SegLenMs(); }

/// The presentation's segment count: the descriptor's, else derived from its duration, else unbounded (a 4xx ends it).
static int32_t EffectiveSegmentCount(const Session& s)
{
    if (s.segmentCount > 0) return s.segmentCount;
    if (s.descDurationMs > 0)
    {
        const int64_t len = s.SegLenMs();
        const int64_t n = (s.descDurationMs + len - 1) / len;
        return n > (int64_t)INT_MAX ? INT_MAX : (int32_t)n;
    }
    return INT_MAX;
}

static int64_t EffectiveDurationMs(const Session& s)
{
    if (s.descDurationMs > 0) return s.descDurationMs;
    const int32_t n = EffectiveSegmentCount(s);
    return n != INT_MAX ? (int64_t)n * s.SegLenMs() : 0;
}

static std::wstring SegmentUrl(const std::wstring& base, const std::wstring& prefix, const std::wstring& suffix,
                               const Session& s, int32_t idx)
{
    const int64_t number = (int64_t)s.startNumber + (int64_t)idx * (int64_t)s.segmentStride;
    return base + prefix + std::to_wstring(number) + suffix;
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Coverage — what the store holds around a reference position.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

struct Coverage
{
    int64_t startMs = -1;   // the buffered range containing the reference (or -1 when none does)
    int64_t endMs = -1;
    int64_t aheadMs = 0;    // forward buffered from the reference
    int64_t behindMs = 0;   // retained behind the reference
    int pairs = 0;          // how many ranges the stream holds at all
};

/// The range containing `refMs`. A range that starts just AFTER the reference (within the contiguity tolerance)
/// also counts: a segment whose first presentation time is a frame past the grid boundary still covers a seek to that
/// boundary, and treating it as missing would refetch the segment that is already there.
static Coverage CoverageAt(CencMediaStream* stream, int64_t refMs)
{
    Coverage c;
    if (!stream) return c;
    int64_t buf[2 * 64];
    const int total = stream->BufferedPairs(buf, 64);
    const int n = total < 64 ? total : 64;
    c.pairs = total;
    for (int i = 0; i < n; i++)
    {
        const int64_t a = buf[i * 2], b = buf[i * 2 + 1];
        if (a - fgpr::kContiguityToleranceMs <= refMs && refMs < b)
        {
            c.startMs = a;
            c.endMs = b;
            c.aheadMs = b - (refMs > a ? refMs : a);
            c.behindMs = refMs > a ? refMs - a : 0;
            return c;
        }
    }
    return c;
}

static winrt::com_ptr<CencMediaStream> VideoStreamLocked(Session& s)
{
    return (s.source && !s.source->m_streams.empty()) ? s.source->m_streams[0] : nullptr;
}

static winrt::com_ptr<CencMediaStream> AudioStreamLocked(Session& s)
{
    return (s.source && s.source->m_streams.size() > 1) ? s.source->m_streams[1] : nullptr;
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The feeder thread.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// State only the feeder thread touches.
struct FeederState
{
    uint64_t ticks[2] = { 0, 0 };          // the demuxer's running decode ticks, per track
    fgpr::plan::Guard guard[2];            // the progress guard (see PlanTrack / FeedPlan.h)
    // STRUCTURAL reset identity, per track. `streamId` catches a re-attach rebuilding the source (a new
    // CencMediaStream object under the same track slot); `cutGen` mirrors CencMediaStream::CutGen(), which the
    // stream bumps on every code path that can SHRINK its buffered coverage (a truncating splice, TakeSamples, a
    // trim that reaches the delivery cursor). Those paths all live inside CencMediaSource.h and cannot reach feeder-
    // thread state directly — the feeder observes the generation instead of trying to enumerate the paths itself.
    const void* streamId[2] = { nullptr, nullptr };
    uint32_t cutGen[2] = { 0, 0 };
    uint64_t floorSeekSeq = 0;
    // The largest segment that has landed per track: the byte reserve the next fetch needs (TrimBehindByTime's
    // reserveBytes), so history behind the playhead is evicted to make room BEFORE the cap is weighed.
    uint64_t maxSegBytes[2] = { 0, 0 };
    bool capHeld = false;                  // the byte cap is currently refusing look-ahead fetches (logged on each edge)
    int32_t failIdx[2] = { -1, -1 };       // transient-failure back-off, per track
    int32_t failCount[2] = { 0, 0 };       // consecutive failures of failIdx (the back-off grows with it, never ends the track)
    bool feedStalled = false;              // FgPrEvent_FeedStalled is outstanding: a failing GET is being retried
    int64_t lastBytesQpc = 0;
    uint64_t lastBytesRaised = 0;
    int64_t lastAhead = -1, lastBehind = -1;
    // A representation-switch boundary that went STALE (the playhead passed it while the segment GET was in
    // flight): the next attempt for the SAME request must not retry the same boundary, or it goes stale again on
    // every tick. `switchBoundaryReqIndex` is which req.index the bound applies to — a different request (or a
    // completed/rejected one) resets it.
    int32_t switchMinBoundary = 0;
    int32_t switchBoundaryReqIndex = -1;
};

enum class JobResult { Idle, Progress, Backoff };

static constexpr int kVideo = 0, kAudio = 1;

/// The STRUCTURAL reset for one track: a seek, a re-attach (the stream object changed), or the stream's own
/// CutGen() moving — anything that can make the feeder's remembered coverage stale in a way `PlanTrack` cannot infer
/// from the numbers alone. Clears the progress guard AND the transient-failure back-off (a fresh generation deserves
/// a fresh try, not a retry count left over from the coverage that just got cut), AND the track's end-of-track latch:
/// a 404 that ended the track at one position says nothing about where the playhead is after a seek, a re-attach or a
/// coverage cut, and a latch that outlived them truncated the presentation for the rest of the session (F035). A track
/// that really ends there costs one more 404 to learn it again. feedMx must be held (the latch lives under it).
static void ResetTrackState(Session& s, FeederState& st, int t)
{
    fgpr::plan::Reset(st.guard[t]);
    st.failIdx[t] = -1;
    st.failCount[t] = 0;
    (t == kVideo ? s.videoEndIndex : s.audioEndIndex) = INT_MAX;
}

/// Ends an outstanding feed stall once no track is retrying any more (a retry landed, or a reset dropped the counts).
/// Feeder thread only; raises an event, so it is called with no lock held.
static void SettleFeedStall(Session& s, FeederState& st)
{
    if (!st.feedStalled || st.failCount[kVideo] != 0 || st.failCount[kAudio] != 0) return;
    st.feedStalled = false;
    Raise(s.handle, FgPrEvent_FeedRecovered);
}

/// Forces the next RaiseBuffered to announce even if the numbers happen to match what was last raised — a reset that
/// followed a coverage cut must not have its FgPrEvent_Buffered suppressed by stale lastAhead/lastBehind bookkeeping.
static void ResetAnnounce(FeederState& st)
{
    st.lastAhead = -1;
    st.lastBehind = -1;
}

/// The reference position the fetch plan is built around: a pending seek's target, else — once the session is
/// attached — the playhead (the carried start position until the first frame lands), else the prefetch window.
/// `cursorStream`, when given (the feeder's own plan, never a representation switch), lifts the live playhead to that
/// stream's delivery cursor: the demand hook measures ahead from the cursor, and the position is only refreshed on
/// TIMEUPDATE (250 ms or slower), so planning from the stale position disagreed with the hook for the cursor's lead
/// (F037, FeedPlan.h DemandBelowWhenSatisfied).
static bool ReferenceLocked(Session& s, int64_t& refMs, int64_t& wantEndMs, bool& seekPlan, CencMediaStream* cursorStream = nullptr)
{
    seekPlan = false;
    const int64_t len = s.SegLenMs();
    if (s.seekNeedsFetch)
    {
        // Decode starts at the keyframe the planner named; the target itself must be covered too.
        refMs = s.seekKeyframeMs >= 0 ? s.seekKeyframeMs : s.seekTargetMs;
        const int64_t target = s.seekTargetMs > refMs ? s.seekTargetMs : refMs;
        wantEndMs = SegmentStartMs(s, SegmentOfUnclamped(s, target)) + len;
        seekPlan = true;
        return true;
    }
    if (s.streaming && s.flushSeq != 0 && s.flushSeq == s.seekSeq.load(std::memory_order_acquire) &&
        s.seeking.load(std::memory_order_acquire) != 0)
    {
        // The target landed in the flushed stream but the engine has not confirmed the seek: positionMs is still the
        // PRE-seek position, the flush emptied it, and planning there would fetch the old position's segments as an
        // orphan run AHEAD of the cursor that the trim never evicts. Keep refilling around the target until SEEKED
        // (or until the refusal path resets `seeking`).
        refMs = s.seekKeyframeMs >= 0 ? s.seekKeyframeMs : s.seekTargetMs;
        wantEndMs = refMs + (s.store ? s.store->bufferAheadMs : fgpr::kDefaultBufferAheadMs);
        return true;
    }
    if (s.streaming)
    {
        const bool playing = s.firstFrameQpc.load(std::memory_order_acquire) != 0;
        refMs = playing ? s.positionMs.load(std::memory_order_acquire) : s.startPositionMs.load(std::memory_order_acquire);
        if (playing && cursorStream) refMs = std::max(refMs, cursorStream->NextSampleTimeMs());   // 0 when drained: the position wins
        wantEndMs = refMs + (s.store ? s.store->bufferAheadMs : fgpr::kDefaultBufferAheadMs);
        return true;
    }
    if (s.prefetchAroundMs >= 0)
    {
        refMs = s.prefetchAroundMs;
        wantEndMs = SegmentStartMs(s, SegmentOfUnclamped(s, refMs)) + (int64_t)(s.prefetchSegments > 0 ? s.prefetchSegments : 2) * len;
        return true;
    }
    return false;
}

struct TrackPlan
{
    int32_t idx = -1;                // the segment to fetch, -1 for none
    bool atEnd = false;              // coverage from the reference runs to the end of the track
    fgpr::plan::Guard next;          // the guard PlanTrack.cpp's caller commits IFF this plan survives the byte cap
    bool guardStepped = false;       // fgpr::plan::Out::guardStepped, surfaced for the one-line log
    int32_t steppedPast = -1;
    int64_t covStartMs = -1, covEndMs = -1;   // for the guard-stepped log line only
};

/// Which segment this track needs next: the one containing the reference when it is not covered, else the one just
/// past the covered range — until that range reaches `wantEndMs` or the end of the track. The arithmetic itself
/// (the progress guard, its floor, the coverage-shrink reset) lives in FeedPlan.h so it can be unit-tested without
/// any of this file's WinRT/CencMediaStream machinery; this function is just the adapter that reads the stream's
/// coverage and reports the candidate `next` guard state for the caller to commit or discard.
static TrackPlan PlanTrack(Session& s, FeederState& st, int track, CencMediaStream* stream, int64_t refMs,
                           int64_t wantEndMs, int32_t endIndex)
{
    TrackPlan p;
    p.next = st.guard[track];
    if (!stream) return p;
    const Coverage cov = CoverageAt(stream, refMs);
    p.covStartMs = cov.startMs;
    p.covEndMs = cov.endMs;
    const fgpr::plan::Out out = fgpr::plan::Next(st.guard[track], p.next, fgpr::plan::Cov{ cov.startMs, cov.endMs },
                                                 refMs, wantEndMs, s.SegLenMs(), endIndex);
    p.idx = out.idx;
    p.atEnd = out.atEnd;
    p.guardStepped = out.guardStepped;
    p.steppedPast = out.steppedPast;
    return p;
}

static void RaiseBuffered(Session& s, FeederState& st, CencMediaStream* video, int64_t refMs, bool force)
{
    const Coverage cov = CoverageAt(video, refMs);
    if (!force && cov.pairs == 0) return;   // nothing in the store: nothing to announce unless a Prefetch asked
    int64_t ahead = cov.aheadMs, behind = cov.behindMs;
    // "Media landed" must read as a > 0 even when the reference sits a few frames before the first sample.
    if (ahead <= 0 && cov.pairs > 0 && cov.startMs >= 0) ahead = 1;
    if (!force && ahead == st.lastAhead && behind == st.lastBehind) return;
    st.lastAhead = ahead;
    st.lastBehind = behind;
    s.bufferedAheadMs.store(ahead, std::memory_order_release);
    s.retainedBehindMs.store(behind, std::memory_order_release);
    Raise(s.handle, FgPrEvent_Buffered, ahead, behind);
}

static void RaiseBytes(Session& s, FeederState& st, bool flush)
{
    const uint64_t bytes = s.bytesDownloaded.load(std::memory_order_acquire);
    if (bytes == st.lastBytesRaised) return;
    if (!flush && fgpr::MsSinceQpc(st.lastBytesQpc) < 250) return;   // ≤ 4 Hz
    st.lastBytesRaised = bytes;
    st.lastBytesQpc = fgpr::QpcNow();
    Raise(s.handle, FgPrEvent_Bytes, (int64_t)bytes, (int64_t)s.downloadElapsedMs.load(std::memory_order_acquire));
}

// The always-on per-segment ABR ledger: every input the throughput estimate is built from, in one line, so a bad
// estimate can be read straight out of the log instead of inferred from a stalled picture. bytes*8/ms is exactly
// kbit/s. `rep` is -1 before a representation has been selected. `contigMs` (ContiguousAheadMs) sits next to the
// coverage-window `aheadMs` so a HOLE is visible straight in the ledger: aheadMs can read healthy from a coverage
// pair that starts past a gap, while contigMs — forward-buffered from the delivery cursor — reads the truth. `cached`
// is HttpFetchTiming::fromStore: a replayed segment must be visibly excluded from the throughput math, not just
// silently skipped (see the incident this file's guard fix addresses).
static void LogAbrSegment(Session& s, const char* track, int rep, int seg, const fgpr::HttpFetchTiming& t,
                          int64_t aheadMs, int64_t contigMs)
{
    const uint64_t ms = t.transferMs ? t.transferMs : 1;
    fgpr::RaiseLog(s.handle, std::string("[cenc-abr] ") + track + " rep=" + std::to_string(rep) + " seg=" + std::to_string(seg) +
            " bytes=" + std::to_string(t.bytes) + " transferMs=" + std::to_string(t.transferMs) +
            " headerMs=" + std::to_string(t.headerMs) + " kbps=" + std::to_string((t.bytes * 8ULL) / ms) +
            " aheadMs=" + std::to_string((long long)aheadMs) + " contigMs=" + std::to_string((long long)contigMs) +
            " cached=" + (t.fromStore ? "1" : "0"));
}

static void PostToRuntime(Session& s, std::function<void(Runtime&)> fn)
{
    std::shared_ptr<Runtime> rt = fgpr::RuntimeFor(s.runtime);
    if (!rt) return;
    Runtime* raw = rt.get();
    rt->queue.Post([raw, fn = std::move(fn)] { if (raw->Ready()) fn(*raw); });
}

static void CompleteAttach(Runtime& rt, const std::shared_ptr<Session>& sp);
static void ApplyPendingSeek(Runtime& rt, const std::shared_ptr<Session>& sp, uint64_t seq);
static void DetachInternal(Runtime& rt, Session& s, bool releaseSource = true);

/// Fail the session from the feeder: the state, the error, and one Error event.
static void FailSession(const std::shared_ptr<Session>& sp, HRESULT hr, int32_t mediaErr, const std::string& why)
{
    Session& s = *sp;
    fgpr::RaiseLog(s.handle, why + " hr=" + fgpr::Hex(hr));
    s.initHr.store((int32_t)hr, std::memory_order_release);
    s.errorHr.store((int32_t)hr, std::memory_order_release);
    s.state.store(FgPrState_Error, std::memory_order_release);
    Raise(s.handle, FgPrEvent_Error, mediaErr, (int64_t)(int32_t)hr);
    // An attach waiting on these inits must not hold the engine: release it (the Error state survives the detach).
    std::shared_ptr<Session> keep = sp;
    PostToRuntime(s, [keep](Runtime& rt) {
        if (rt.attached.load(std::memory_order_acquire) == keep->handle) DetachInternal(rt, *keep);
    });
}

/// Wire the feeder's wake-up into a freshly built source's streams.
static void InstallDemandHooks(Session& s, CencMediaSource* source)
{
    std::shared_ptr<fgpr::FeederSignal> signal = s.signal;
    for (auto const& stream : source->m_streams)
        if (stream) stream->m_demand = [signal] { signal->Kick(); };
    // The video stream announces a representation when DELIVERY reaches its first sample (the picture changes then, not
    // when the splice was queued). It publishes the index itself (SegmentStore); this only raises the event. The hook
    // runs under the stream's lock and holds nothing but the session handle.
    if (!source->m_streams.empty() && source->m_streams[0])
    {
        const uint64_t handle = s.handle;
        source->m_streams[0]->m_onRepDelivered = [handle](int32_t index) { Raise(handle, FgPrEvent_Representation, index); };
    }
}

/// Publish the GETs a job has just begun as what a seek (or the destroy) cancels, and cancel them at once when the session
/// is already stopping. The stop flag is read in the SAME critical section that publishes: DestroyInternal sets it and
/// only then reads the in-flight GETs under feedMx, so either it sees these GETs and cancels them, or it ran first and
/// the flag is visible here - a GET begun just as the destroy looked can never go unnoticed and run to completion.
static void PublishInflight(Session& s, const std::shared_ptr<fgpr::HttpFetch>& video, const std::shared_ptr<fgpr::HttpFetch>& audio,
                            int videoIndex)
{
    bool stopping = false;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.inflightVideo = video;
        s.inflightAudio = audio;
        s.inflightVideoIndex = videoIndex;
        stopping = s.feedStop.load(std::memory_order_acquire);
    }
    if (stopping)
    {
        if (video) video->Cancel();
        if (audio) audio->Cancel();
    }
}

// ── init segments ─────────────────────────────────────────────────────────────────────────────────────────────────
/// Block the feeder until the runtime thread has run MFStartup. The init GETs need nothing of the runtime (plain WinRT
/// HTTP), so an Attach kicks the feeder at once - before the runtime's CDM/PMP/engine bring-up has finished, and
/// possibly before it has even started MF - but the source the inits are turned into (MFCreateEventQueue ...) is a Media
/// Foundation object. Only MFStartup, early in the bring-up, is waited for, never the whole of it. False when the runtime
/// is gone or failed (or the session is stopping): there is nothing to build a source for.
static bool WaitForMediaFoundation(Session& s)
{
    for (;;)
    {
        if (s.feedStop.load(std::memory_order_acquire)) return false;
        std::shared_ptr<Runtime> rt = fgpr::RuntimeFor(s.runtime);
        if (!rt || rt->shuttingDown.load(std::memory_order_acquire)) return false;
        if (rt->mfReady.load(std::memory_order_acquire)) return true;
        if (FAILED((HRESULT)rt->bringUp.load(std::memory_order_acquire))) return false;   // MFStartup itself failed
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
}

// Both init GETs are begun together (they used to be two serial round trips before the first media byte).
static JobResult FetchInits(const std::shared_ptr<Session>& sp)
{
    Session& s = *sp;
    std::wstring videoUrl, audioUrl;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        videoUrl = s.initUrl;
        audioUrl = s.audioInitUrl;
    }
    auto vf = fgpr::HttpFetch::Begin(videoUrl, s.headers, s.store);
    std::shared_ptr<fgpr::HttpFetch> af = audioUrl.empty() ? nullptr : fgpr::HttpFetch::Begin(audioUrl, s.headers, s.store);
    PublishInflight(s, vf, af, -2);   // init: a seek never cancels it (the seek needs it too)
    vf->Wait();
    if (af) af->Wait();
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.inflightVideo = nullptr;
        s.inflightAudio = nullptr;
        s.inflightVideoIndex = -1;
    }
    if (s.feedStop.load(std::memory_order_acquire) || vf->cancelled) return JobResult::Idle;

    fgpr::RaiseLog(s.handle, "[cenc] init " + fgpr::Narrow(videoUrl) + " HTTP " + std::to_string(vf->status) + " (" +
                             std::to_string(vf->body.size()) + "B) in " + std::to_string(vf->timing.headerMs + vf->timing.transferMs) + "ms");
    cenc::InitInfo info;
    const bool videoOk = vf->status == 200 && !vf->body.empty() && cenc::ParseInit(vf->body, info, cenc::TrackPick::Video);
    vf->ReleaseBody();
    if (!videoOk)
    {
        const HRESULT hr = vf->status == 200 ? MF_E_INVALIDMEDIATYPE
                         : FAILED(vf->hr) ? vf->hr : HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        FailSession(sp, hr, vf->status == 200 ? 4 /*MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED*/ : 2 /*MF_MEDIA_ENGINE_ERR_NETWORK*/,
                    "[cenc] video init UNUSABLE (HTTP " + std::to_string(vf->status) + ") - ParseInit found no usable video sample entry / avcC");
        return JobResult::Idle;
    }
    {
        char cc[5] = { (char)(info.codec4cc >> 24), (char)(info.codec4cc >> 16), (char)(info.codec4cc >> 8), (char)info.codec4cc, 0 };
        fgpr::RaiseLog(s.handle, "[cenc] init parsed: " + std::to_string(info.width) + "x" + std::to_string(info.height) +
                " codec=" + std::string(cc) +
                " scheme=" + std::string(info.scheme == 1 ? "cbcs" : "cenc") +
                " enc=" + (info.encrypted ? "1" : "0") + " ivSize=" + std::to_string((int)info.perSampleIvSize) +
                " timescale=" + std::to_string(info.timescale) + " KID=" + fgpr::Narrow(fgpr::KidToHex(info.kid)) +
                " avcC=" + std::to_string(info.avcC.size()) + "B pssh=" + std::to_string(info.pssh.size()) + "B" +
                " nalLengthSize=" + std::to_string((int)info.nalLenSize));
    }

    // The AUDIO init segment: the video's own soundtrack, a separate single-track file under the same key. Its
    // absence is NOT a failure — the session then plays video only.
    cenc::InitInfo audioInfo;
    bool haveAudio = false;
    if (af)
    {
        if (af->status == 200 && !af->body.empty() && cenc::ParseInit(af->body, audioInfo, cenc::TrackPick::Audio))
        {
            haveAudio = true;
            char acc[5] = { (char)(audioInfo.codec4cc >> 24), (char)(audioInfo.codec4cc >> 16),
                            (char)(audioInfo.codec4cc >> 8), (char)audioInfo.codec4cc, 0 };
            fgpr::RaiseLog(s.handle, "[cenc] audio init parsed: codec=" + std::string(acc) +
                    " " + std::to_string(audioInfo.channels) + "ch/" + std::to_string(audioInfo.sampleRate) + "Hz" +
                    " asc=" + std::to_string(audioInfo.asc.size()) + "B" +
                    " enc=" + (audioInfo.encrypted ? "1" : "0") +
                    " timescale=" + std::to_string(audioInfo.timescale) +
                    " ivSize=" + std::to_string((int)audioInfo.perSampleIvSize));
        }
        else
        {
            fgpr::RaiseLog(s.handle, "[cenc] audio init UNUSABLE (HTTP " + std::to_string(af->status) + ", " +
                    std::to_string(af->body.size()) + "B) - continuing VIDEO ONLY");
        }
        af->ReleaseBody();
    }

    if (!WaitForMediaFoundation(s)) return JobResult::Idle;

    // The source is built as soon as the inits are known, with EMPTY sample lists: a stream that runs dry before its
    // track is complete parks the request as STARVED (not end-of-stream) and the feeder's appends release it. The
    // presentation duration is the WHOLE track's — deriving it from the samples in hand would declare the track to be
    // as long as what has been fetched (the "0:36 / 0:08" clock).
    const int64_t totalMs = EffectiveDurationMs(s);
    winrt::com_ptr<CencMediaSource> source;
    try
    {
        CencAudioFeed feed;
        if (haveAudio) feed.info = audioInfo;
        source = BuildCencSource(info, std::vector<cenc::Sample>(), haveAudio ? &feed : nullptr, true,
                                 totalMs > 0 ? (uint64_t)totalMs * 10000ULL : 0ULL, s.handle, s.store,
                                 (LONGLONG)s.startPositionMs.load(std::memory_order_acquire) * 10000LL);
    }
    catch (winrt::hresult_error const& e)
    {
        FailSession(sp, e.code().value, 4 /*MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED*/, "[cenc] BuildCencSource failed");
        return JobResult::Idle;
    }
    InstallDemandHooks(s, source.get());
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        if (s.kidHex.empty())
        {
            static const uint8_t zero[16] = {};
            if (memcmp(info.kid, zero, 16) != 0) s.kidHex = fgpr::KidToHex(info.kid);
        }
        s.videoInfo = std::move(info);
        s.audioInfo = std::move(audioInfo);
        s.haveAudio = haveAudio;
        s.source = source;
    }
    if (s.durationMs.load(std::memory_order_acquire) <= 0 && totalMs > 0) s.durationMs.store(totalMs, std::memory_order_release);
    s.initsLoaded.store(true, std::memory_order_release);

    // An Attach that arrived before the inits finishes now.
    std::shared_ptr<Session> keep = sp;
    PostToRuntime(s, [keep](Runtime& rt) { CompleteAttach(rt, keep); });
    return JobResult::Progress;
}

// ── a representation switch (ABR) ─────────────────────────────────────────────────────────────────────────────────
static JobResult RunRepresentationSwitch(const std::shared_ptr<Session>& sp, FeederState& st,
                                         const Session::RepresentationRequest& req, int& backoffMs)
{
    Session& s = *sp;
    auto clearIfCurrent = [&]() {
        std::lock_guard<std::mutex> g(s.feedMx);
        if (s.rep.pending && s.rep.index == req.index && s.rep.initUrl == req.initUrl) s.rep.pending = false;
    };

    // The stale-boundary lower bound only applies to the request it was computed for; a different request (or the
    // previous one completing/being rejected, both of which reset it below) starts over at boundary 0.
    if (st.switchBoundaryReqIndex != req.index)
    {
        st.switchMinBoundary = 0;
        st.switchBoundaryReqIndex = req.index;
    }

    auto initFetch = fgpr::HttpFetch::Begin(req.initUrl, s.headers, s.store);
    PublishInflight(s, initFetch, nullptr, -3);   // a seek cancels it; the switch then stays PENDING, not rejected
    initFetch->Wait();
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.inflightVideo = nullptr;
        s.inflightVideoIndex = -1;
    }
    if (s.feedStop.load(std::memory_order_acquire)) return JobResult::Idle;
    if (initFetch->cancelled)
    {
        fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch init fetch cancelled - switch still pending");
        return JobResult::Progress;
    }

    cenc::InitInfo nextInfo;
    cenc::InitInfo currentInfo;
    winrt::com_ptr<CencMediaStream> video;
    int64_t refMs = 0, wantEndMs = 0;
    bool seekPlan = false;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        currentInfo = s.videoInfo;
        video = VideoStreamLocked(s);
        ReferenceLocked(s, refMs, wantEndMs, seekPlan);
    }
    const bool compatible = initFetch->status == 200 && !initFetch->body.empty() &&
                            cenc::ParseInit(initFetch->body, nextInfo, cenc::TrackPick::Video) &&
                            nextInfo.timescale == currentInfo.timescale &&
                            memcmp(nextInfo.kid, currentInfo.kid, sizeof(currentInfo.kid)) == 0;
    initFetch->ReleaseBody();
    if (!compatible || !video)
    {
        fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch rejected: init/KID/timescale incompatible (HTTP " +
                                 std::to_string(initFetch->status) + ")");
        clearIfCurrent();
        return JobResult::Progress;
    }

    // THE SWITCH BOUNDARY. By default the new representation is APPENDED at the END of the buffer - `cursor`, the first
    // segment not yet buffered - so the splice erases nothing and the seconds already downloaded keep playing in the old
    // representation until delivery reaches the new one (Shaka's clearBufferSwitch=false, ExoPlayer's queue end). The
    // old rule - the segment after the playhead, truncating everything past it - threw away up to 60 s of downloaded video
    // on every switch, at the moment the link was slowest. A request with a retain window (`retainMs` >= 0) lands that far
    // ahead of the playhead instead, discarding the old buffer past it: an upswitch that should show soon, or a manual pin
    // (0 = right after the playhead). Either way the boundary is NEVER behind the playhead.
    // History of the playhead rule: it used to clamp UP to `nextSegment`, the initial-burst count captured before playback
    // started: a second in, the boundary computed 0, clamped up to 2, and the replacement segment
    // landed ~8s ahead. SwitchVideoRepresentation then erased every buffered sample under the
    // playhead and refilled from there, so video had a multi-second hole while audio (never
    // repositioned) and the clock ran on — the frozen picture.
    // CEIL, not floor: the splice must land at a boundary at or AFTER the next undelivered sample
    // so everything between the playhead and the splice stays continuous in the old representation
    // (no hole). And never past the feeder's own cursor, which is where the buffer ends.
    const int64_t len = s.SegLenMs();
    const Coverage cov = CoverageAt(video.get(), refMs);
    const int32_t cursor = cov.startMs >= 0 ? SegmentOfUnclamped(s, cov.endMs + len / 2) : SegmentOfUnclamped(s, refMs);
    const int64_t nextTimeMs = video->NextSampleTimeMs();
    int32_t boundary = cursor;   // append - and, when the buffer is drained, the feeder cursor IS the playhead
    if (req.retainMs >= 0 && video->Ahead() != 0)
    {
        boundary = (int32_t)(((uint64_t)std::max<int64_t>(nextTimeMs, 0) + (uint64_t)len - 1) / (uint64_t)len);
        boundary = std::max(boundary, SegmentOfUnclamped(s, refMs + (int64_t)req.retainMs));
    }
    boundary = std::clamp(boundary, 0, cursor);
    // A previous attempt at this SAME request went stale (the playhead passed the boundary it fetched while the GET
    // was in flight): retry no earlier than one past it, or the next attempt goes stale again on every tick. If that
    // lower bound is now past the buffered cursor, there is nowhere valid to land yet — wait for more buffer instead
    // of splicing past the buffered end (which would open the exact hole this file's other fix closes).
    boundary = std::max(boundary, st.switchMinBoundary);
    if (boundary > cursor)
    {
        fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch boundary " + std::to_string(boundary) +
                                 " exceeds buffered cursor " + std::to_string(cursor) + " - waiting for more buffer");
        backoffMs = 250;
        return JobResult::Backoff;
    }

    // Session fields are committed ONLY once the switch actually takes effect (a splice, or the end-of-track retarget):
    // a rejected or stale switch must leave s.initUrl/segBase/videoInfo pointing at what the buffer really holds.
    // feedMx must be held.
    auto commitSessionFields = [&]() {
        s.initUrl = req.initUrl;
        s.segBase = req.base;
        s.segPrefix = req.prefix;
        s.segSuffix = req.suffix;
        s.videoInfo = nextInfo;
        s.rep.pending = false;
        // A new representation is a new URL space: a 4xx latch from the OLD one must not end the new one before
        // it has even been tried. INT_MAX is Session's own unlatched default (see its declaration).
        s.videoEndIndex = INT_MAX;
    };

    if (boundary >= EffectiveSegmentCount(s))
    {
        // The buffer already reaches the end of the track: there is no segment left to fetch in the new representation,
        // so there is nothing to splice (and a GET past the end would only 404 and reject the switch, again and again).
        // The new representation still becomes the one the feeder fetches from and the stream stamps with, so a seek back
        // refetches in it, and it is published as the one being downloaded so the ABR's baseline matches.
        {
            std::lock_guard<std::mutex> g(s.feedMx);
            if (!(s.rep.pending && s.rep.index == req.index && s.rep.initUrl == req.initUrl)) return JobResult::Progress;
            auto current = VideoStreamLocked(s);
            if (!current) return JobResult::Progress;
            current->RetargetAppend(nextInfo, req.index);
            commitSessionFields();
        }
        st.switchMinBoundary = 0;
        st.switchBoundaryReqIndex = -1;
        fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch to representation " + std::to_string(req.index) +
                                 ": the buffer reaches the end of the track (landing segment " + std::to_string(boundary) +
                                 " is past it) - nothing to splice");
        Raise(s.handle, FgPrEvent_RepresentationQueued, req.index);
        return JobResult::Progress;
    }

    const std::wstring url = SegmentUrl(req.base, req.prefix, req.suffix, s, boundary);
    auto segFetch = fgpr::HttpFetch::Begin(url, s.headers, s.store);
    PublishInflight(s, segFetch, nullptr, -3);
    segFetch->Wait();
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.inflightVideo = nullptr;
        s.inflightVideoIndex = -1;
    }
    if (s.feedStop.load(std::memory_order_acquire)) return JobResult::Idle;
    if (segFetch->cancelled) return JobResult::Progress;   // a seek landed first: still pending
    if (!segFetch->Ok())
    {
        fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch rejected: target segment HTTP " + std::to_string(segFetch->status));
        segFetch->ReleaseBody();
        clearIfCurrent();
        return JobResult::Progress;
    }

    std::vector<cenc::Sample> more;
    std::vector<int64_t> keyframes;
    uint64_t ticks = st.ticks[kVideo];
    cenc::ParseSegment(segFetch->body, nextInfo, more, ticks, &keyframes);
    segFetch->ReleaseBody();
    if (!segFetch->timing.fromStore)   // the WinRT HTTP cache answered this GET itself: not real throughput
    {
        s.bytesDownloaded.fetch_add(segFetch->timing.bytes, std::memory_order_acq_rel);
        s.downloadElapsedMs.fetch_add(std::max<uint64_t>(1, segFetch->timing.transferMs), std::memory_order_acq_rel);
    }

    if (more.empty() || !more[0].keyframe)
    {
        fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch rejected: target segment did not begin with a keyframe");
        clearIfCurrent();
        return JobResult::Progress;
    }
    st.ticks[kVideo] = ticks;
    SwitchResult switchResult = SwitchResult::Rejected;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        if (!(s.rep.pending && s.rep.index == req.index && s.rep.initUrl == req.initUrl))
            return JobResult::Progress;   // superseded by a newer request while this one was on the wire
        auto current = VideoStreamLocked(s);
        if (!current) return JobResult::Progress;   // the source went away mid-fetch (detach/destroy raced in)
        // Session fields are committed ONLY once the splice actually happens — see SwitchResult below. Before this
        // fix they were written here unconditionally, ahead of a splice call that could still reject or go stale, so
        // a rejected/stale switch left s.initUrl/segBase/videoInfo pointing at a representation nothing had spliced.
        switchResult = current->SwitchVideoRepresentation(nextInfo, std::move(more), req.index);
        if (switchResult == SwitchResult::Spliced) commitSessionFields();
        else if (switchResult == SwitchResult::Rejected)
        {
            s.rep.pending = false;   // one-shot: shut down / wrong kind / empty — nothing to retry
        }
        // StaleBoundary: s.rep.pending stays true; the retry (with a higher switchMinBoundary) happens below.
    }

    if (switchResult == SwitchResult::StaleBoundary)
    {
        // The remembered lower bound stays alive; RunOneJob calls this again next tick (rep.pending is still set)
        // and the boundary computed above will be clamped up to at least boundary+1.
        st.switchMinBoundary = boundary + 1;
        fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch boundary " + std::to_string(boundary) +
                                 " went stale during the fetch (playhead passed it) - retrying at " +
                                 std::to_string(boundary + 1));
        return JobResult::Progress;
    }
    if (switchResult == SwitchResult::Rejected)
    {
        st.switchMinBoundary = 0;
        st.switchBoundaryReqIndex = -1;
        fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch rejected by the stream (shutdown, wrong kind, or an "
                                 "empty replacement) at segment index " + std::to_string(boundary));
        return JobResult::Progress;
    }

    // Spliced.
    st.switchMinBoundary = 0;
    st.switchBoundaryReqIndex = -1;
    for (int64_t kf : keyframes) s.store->keyframes.Append(kf);
    fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch at segment index " + std::to_string(boundary) +
                             " (playhead t=" + std::to_string((long long)nextTimeMs) + "ms, segment=" +
                             std::to_string((long long)len) + "ms, cursor was " + std::to_string(cursor) +
                             (req.retainMs < 0 ? ", append" : ", retain=" + std::to_string(req.retainMs) + "ms") +
                             ") -> representation " + std::to_string(req.index));
    // The picture changes later, when delivery reaches the new representation's first sample (the stream raises
    // FgPrEvent_Representation then); this announces what the downloader is on now.
    Raise(s.handle, FgPrEvent_RepresentationQueued, req.index);
    LogAbrSegment(s, "video", req.index, boundary, segFetch->timing, video->AheadDurationMs(), video->ContiguousAheadMs());
    // The splice can SHRINK coverage (a truncating splice past the playhead, or a boundary behind what was buffered):
    // that shrink is never announced by the normal landed-fetch path (this function returns before RunOneJob's own
    // RaiseBuffered call), so it is raised explicitly here, forced, so a shrink a viewer would notice is never silent.
    RaiseBuffered(s, st, video.get(), refMs, true);
    return JobResult::Progress;
}

// ── one fetch: the planned video and audio segments, IN PARALLEL ─────────────────────────────────────────────────────
static JobResult RunOneJob(const std::shared_ptr<Session>& sp, FeederState& st, int& backoffMs)
{
    Session& s = *sp;
    if (FAILED((HRESULT)s.initHr.load(std::memory_order_acquire))) return JobResult::Idle;

    bool wantInits = false;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        wantInits = s.wantInits;
    }
    if (!s.initsLoaded.load(std::memory_order_acquire)) return wantInits ? FetchInits(sp) : JobResult::Idle;

    // A representation switch waits for a pending seek (the seek is what the user is looking at).
    Session::RepresentationRequest rep;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        if (s.rep.pending && s.streaming && !s.seekNeedsFetch) rep = s.rep;
    }
    if (rep.pending) return RunRepresentationSwitch(sp, st, rep, backoffMs);

    // ── plan ──
    int64_t refMs = 0, wantEndMs = 0;
    bool seekPlan = false;
    TrackPlan vp, ap;
    winrt::com_ptr<CencMediaStream> video, audio;
    std::wstring vBase, vPrefix, vSuffix;
    uint64_t seekSeq = 0;
    cenc::InitInfo videoInfo, audioInfo;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        video = VideoStreamLocked(s);
        if (!ReferenceLocked(s, refMs, wantEndMs, seekPlan, video.get())) return JobResult::Idle;
        audio = s.haveAudio ? AudioStreamLocked(s) : nullptr;
        if (!video) return JobResult::Idle;
        const uint64_t seq = s.seekSeq.load(std::memory_order_acquire);
        // The STRUCTURAL reset (replaces the old seek-only reset). Every code path that can SHRINK a stream's
        // buffered coverage lives inside CencMediaSource.h/SegmentStore.h and has no way to reach this feeder-thread
        // state directly — so instead of trying to catch each one here, the feeder just compares against the
        // stream's own generation counter. A seek (seq), a re-attach (the stream OBJECT changed under the same
        // track slot — MF shuts a replaced source down and CompleteAttach rebuilds a fresh one), or a CutGen() bump
        // (a truncating splice / TakeSamples / a coverage-cutting trim) all reset the same way.
        CencMediaStream* trackStream[2] = { video.get(), audio.get() };
        for (int t = 0; t < 2; t++)
        {
            CencMediaStream* stream = trackStream[t];
            const void* sid = stream;
            const uint32_t gen = stream ? stream->CutGen() : st.cutGen[t];
            if (seq != st.floorSeekSeq || sid != st.streamId[t] || gen != st.cutGen[t])
            {
                ResetTrackState(s, st, t);
                ResetAnnounce(st);
            }
            st.streamId[t] = sid;
            st.cutGen[t] = gen;
        }
        st.floorSeekSeq = seq;
        const int32_t count = EffectiveSegmentCount(s);
        const int64_t durMs = EffectiveDurationMs(s);
        if (durMs > 0 && wantEndMs > durMs) wantEndMs = durMs;
        vp = PlanTrack(s, st, kVideo, video.get(), refMs, wantEndMs, std::min(count, s.videoEndIndex));
        if (audio) ap = PlanTrack(s, st, kAudio, audio.get(), refMs, wantEndMs, std::min(count, s.audioEndIndex));
        // The byte cap: past the budget, fetch only what the reference position itself needs — never starve playback,
        // never keep buffering ahead into memory the session was not given. History is trimmed FIRST, with a RESERVE
        // for the segment about to be fetched (1.5× the largest one landed on that track, floored so the first fetch
        // has room, capped at a quarter of the budget): the playhead has moved since the last append, and history
        // behind it yields to forward demand even inside the retention window. Without the reserve a 1080p store
        // filled to exactly the budget with 30 s of history, the cap refused every fetch, and playback starved
        // forever at the end of the buffer (2026-09-22).
        const uint64_t reserveCap = s.store->budgetBytes / 4;
        auto reserveFor = [&](int track, uint64_t floorBytes) {
            const uint64_t want = st.maxSegBytes[track] + st.maxSegBytes[track] / 2;
            return std::min<uint64_t>(std::max<uint64_t>(want, floorBytes), reserveCap);
        };
        // The trim measures the retention window, and the keyframe the playhead's GOP starts at, from the PLAYHEAD:
        // the delivery cursor runs ahead of the clock by MF's decode/render queue, and a re-attach resumes at the clock.
        // Known only once a first frame is out and no seek is pending (the position is the old one until it lands).
        const int64_t playheadMs = (s.streaming && !s.seekNeedsFetch && s.seeking.load(std::memory_order_acquire) == 0 && s.firstFrameQpc.load(std::memory_order_acquire) != 0)
            ? s.positionMs.load(std::memory_order_acquire) : -1;
        video->SetPlayheadHintMs(playheadMs);
        if (audio) audio->SetPlayheadHintMs(playheadMs);
        video->TrimNow(reserveFor(kVideo, 1ull << 20));
        if (audio) audio->TrimNow(reserveFor(kAudio, 256ull << 10));
        bool vpCancelled = false, apCancelled = false;
        // The cap is judged PER TRACK against the same slice the trim enforces (VideoBudget / AudioBudget), and on the
        // media alone (the pool's idle scratch has its own cap). It used to compare the 32 MiB TOTAL while the trim held
        // the video to its 24 MiB slice minus the reserve: at ~3.3 Mbps and up forward video outgrew its slice, the
        // gate stayed open, and the trim evicted all history (and a just-landed backward run) on every job.
        const bool vCap = !seekPlan && s.store->videoBytes.load(std::memory_order_relaxed) >= s.store->VideoBudget();
        const bool aCap = !seekPlan && audio && s.store->audioBytes.load(std::memory_order_relaxed) >= s.store->AudioBudget();
        const bool capHolds = vCap || aCap;
        if (vCap && vp.idx >= 0 && vp.idx != SegmentOfUnclamped(s, refMs)) { vp.idx = -1; vpCancelled = true; }
        if (aCap && ap.idx >= 0 && ap.idx != SegmentOfUnclamped(s, refMs)) { ap.idx = -1; apCancelled = true; }
        // Always-on, one line per EDGE (never per plan): a held cap is the one state in which "no fetch" is a
        // decision rather than a bug, and a log with neither line cannot tell the two apart.
        if (capHolds != st.capHeld)
        {
            st.capHeld = capHolds;
            fgpr::RaiseLog(s.handle, std::string("[cenc-feed] byte cap ") + (capHolds ? "holds" : "released") +
                                     ": video=" + std::to_string(s.store->videoBytes.load(std::memory_order_relaxed)) + "/" + std::to_string(s.store->VideoBudget()) +
                                     " audio=" + std::to_string(s.store->audioBytes.load(std::memory_order_relaxed)) + "/" + std::to_string(s.store->AudioBudget()) +
                                     " pool=" + std::to_string(s.store->pool.PooledBytes()) + " budget=" + std::to_string(s.store->budgetBytes) +
                                     " videoAheadMs=" + std::to_string(video ? video->ContiguousAheadMs() : 0) +
                                     " maxSeg=" + std::to_string(st.maxSegBytes[kVideo]) + "/" + std::to_string(st.maxSegBytes[kAudio]));
        }
        // Commit the candidate guard ONLY for a plan that survives the byte cap: a plan the byte cap discards must
        // not have moved the floor (the incident this replaces committed the floor and then sometimes threw the
        // fetch away — a second, independent way the guard used to drift from what was actually fetched).
        if (!vpCancelled) st.guard[kVideo] = vp.next;
        if (audio && !apCancelled) st.guard[kAudio] = ap.next;
        if (vp.guardStepped)
            fgpr::RaiseLog(s.handle, "[cenc-feed] progress guard stepped video past seg#" + std::to_string(vp.steppedPast) +
                                     " ref=" + std::to_string((long long)refMs) + "ms cov=[" +
                                     std::to_string((long long)vp.covStartMs) + "," + std::to_string((long long)vp.covEndMs) +
                                     ") lastCovEnd=" + std::to_string((long long)vp.next.lastCovEnd) +
                                     " floor=" + std::to_string(vp.next.floorIdx));
        if (audio && ap.guardStepped)
            fgpr::RaiseLog(s.handle, "[cenc-feed] progress guard stepped audio past seg#" + std::to_string(ap.steppedPast) +
                                     " ref=" + std::to_string((long long)refMs) + "ms cov=[" +
                                     std::to_string((long long)ap.covStartMs) + "," + std::to_string((long long)ap.covEndMs) +
                                     ") lastCovEnd=" + std::to_string((long long)ap.next.lastCovEnd) +
                                     " floor=" + std::to_string(ap.next.floorIdx));
        vBase = s.segBase; vPrefix = s.segPrefix; vSuffix = s.segSuffix;
        videoInfo = s.videoInfo;
        if (audio) audioInfo = s.audioInfo;
        seekSeq = seq;
    }
    SettleFeedStall(s, st);   // a seek / structural reset above dropped the retry counts: the stall (if any) is over
    // A track whose buffer already reaches the duration is at its end even when the planner only said "satisfied"
    // (wantEndMs is clamped to the duration, so satisfied-at-the-duration IS the end — plan::ReachesEnd).
    {
        const int64_t durMs = EffectiveDurationMs(s);
        if (!vp.atEnd && durMs > 0 && fgpr::plan::ReachesEnd(CoverageAt(video.get(), refMs).endMs, durMs, fgpr::kContiguityToleranceMs))
            vp.atEnd = true;
        if (audio && !ap.atEnd && durMs > 0 && fgpr::plan::ReachesEnd(CoverageAt(audio.get(), refMs).endMs, durMs, fgpr::kContiguityToleranceMs))
            ap.atEnd = true;
    }
    if (vp.atEnd && !video->IsComplete()) video->MarkComplete();
    if (audio && ap.atEnd && !audio->IsComplete()) audio->MarkComplete();

    if (vp.idx < 0 && ap.idx < 0)
    {
        if (seekPlan)
        {
            // Nothing left to fetch for the seek (it landed, or the track ends there): the reposition runs NOW, from
            // this completion — never from a tick.
            {
                std::lock_guard<std::mutex> g(s.feedMx);
                if (s.seekSeq.load(std::memory_order_acquire) == seekSeq) s.seekNeedsFetch = false;
            }
            std::shared_ptr<Session> keep = sp;
            PostToRuntime(s, [keep, seekSeq](Runtime& rt) { ApplyPendingSeek(rt, keep, seekSeq); });
            fgpr::RaiseLog(s.handle, "[cenc-feed] seek target buffered seq=" + std::to_string(seekSeq) +
                                     " segment=" + std::to_string(SegmentOfUnclamped(s, refMs)));
            return JobResult::Progress;
        }
        // A Prefetch whose window is already in the store is still owed its FgPrEvent_Buffered: the managed prepare
        // waits for it.
        bool announce = false;
        {
            std::lock_guard<std::mutex> g(s.feedMx);
            announce = s.prefetchAnnounce;
            s.prefetchAnnounce = false;
        }
        RaiseBuffered(s, st, video.get(), refMs, announce);
        RaiseBytes(s, st, true);
        // Satisfied: wake once the playhead has consumed one more segment (hysteresis, FeedPlan.h
        // DemandBelowWhenSatisfied), each track measured from its OWN cursor exactly as the hook measures it. Re-arming
        // at the full forward target instead left a window out of every segment cycle in which the hook (cursor) said
        // "below target" and this plan said "satisfied", so every delivered sample woke a job that fetched nothing (F037).
        // At the end of the track the hook is off anyway: wake on the normal target. Not satisfied but unable to fetch
        // (the byte cap): wake again only once the playhead has consumed one more segment.
        const Coverage vc = CoverageAt(video.get(), refMs);
        const bool satisfied = vp.atEnd || (vc.startMs >= 0 && vc.endMs >= wantEndMs);
        int64_t belowVideo = 0, belowAudio = 0;
        if (satisfied && !vp.atEnd)
        {
            const int64_t target = s.store ? s.store->bufferAheadMs : fgpr::kDefaultBufferAheadMs;
            belowVideo = fgpr::plan::DemandBelowWhenSatisfied(video->ContiguousAheadMs(), target, s.SegLenMs());
            if (audio) belowAudio = fgpr::plan::DemandBelowWhenSatisfied(audio->ContiguousAheadMs(), target, s.SegLenMs());
        }
        else if (!satisfied)
        {
            belowVideo = belowAudio = std::max<int64_t>(1, vc.aheadMs - s.SegLenMs());
        }
        video->SetDemandBelowMs(belowVideo);
        if (audio) audio->SetDemandBelowMs(belowAudio);
        return JobResult::Idle;
    }

    // ── fetch: both GETs are begun before either is waited on, and each track is APPENDED the moment ITS GET completes ──
    // (F041: the job used to wait for both and only then append either, so a starved track's downloaded segment sat
    // unappended behind the other track's still-running GET.) Both stay published as in flight until both are handled, so a
    // seek's cancel still finds every GET; the job is still one plan -> one round -> one replan, so nothing here can
    // overlap a representation switch or a second plan. A SEEK plan is the exception: it joins both GETs and then
    // handles video, then audio, as before. ApplyPendingSeek needs both tracks anyway, so an early append gains nothing,
    // and it would cost: a repeat seek into the same segment keeps the in-flight GETs (inflightVideoIndex == target) yet
    // flushes both streams, which would erase the track that had already landed and refetch it, over and over for as
    // long as a scrub stays inside one segment. (A non-seek job hit by a seek to exactly its in-flight segment can still
    // cost one refetch of its early-appended track; the seek-plan job that follows joins both tracks and settles.)
    // One segment per track per job stays deliberate: a second concurrent video GET would halve every fetch's measured
    // transfer rate, and bytesDownloaded / downloadElapsedMs (the ABR's throughput input) sum per-fetch times, so the
    // estimate would read half the real link speed.
    auto waitSet = std::make_shared<fgpr::FetchWaitSet>();
    std::shared_ptr<fgpr::HttpFetch> vf, af;
    if (vp.idx >= 0) vf = fgpr::HttpFetch::Begin(SegmentUrl(vBase, vPrefix, vSuffix, s, vp.idx), s.headers, s.store, waitSet);
    if (ap.idx >= 0) af = fgpr::HttpFetch::Begin(SegmentUrl(s.audioSegBase, s.audioSegPrefix, s.audioSegSuffix, s, ap.idx), s.headers, s.store, waitSet);
    PublishInflight(s, vf, af, vp.idx >= 0 ? vp.idx : ap.idx);

    bool landed = false;
    bool grew = false;   // any track whose coverage actually extended — the SPIN BRAKE below reads this
    int nextBackoff = 0;
    auto handleTrack = [&](int t, const std::shared_ptr<fgpr::HttpFetch>& f, int32_t idx, const cenc::InitInfo& info,
                           const winrt::com_ptr<CencMediaStream>& stream) {
        if (!f) return;
        if (f->cancelled) { f->ReleaseBody(); return; }   // a newer seek (or teardown): the plan is rebuilt
        if (!f->Ok())
        {
            f->ReleaseBody();
            if (!fgpr::plan::EndsTrack(f->status, idx, EffectiveSegmentCount(s)))
            {
                // Everything but "no such segment at or past the end" is a STALL, not the end of the track: a network
                // blip, a 5xx, an expired signed URL (401/403) or a 404 inside the manifest. Retry with a capped
                // exponential back-off for as long as the plan keeps asking for this segment (F035). The managed side is
                // told once (FgPrEvent_FeedStalled), so a stall with nothing left buffered reads as buffering.
                st.failCount[t] = st.failIdx[t] == idx ? st.failCount[t] + 1 : 1;
                st.failIdx[t] = idx;
                const int wait = fgpr::plan::RetryDelayMs(st.failCount[t]);
                if (wait > nextBackoff) nextBackoff = wait;
                fgpr::RaiseLog(s.handle, std::string("[cenc-feed] ") + (t == kVideo ? "video" : "audio") + " seg#" +
                                         std::to_string(idx) + " failed (HTTP " + std::to_string(f->status) + ", hr=" +
                                         fgpr::Hex(f->hr) + ") - retry #" + std::to_string(st.failCount[t]) + " in " +
                                         std::to_string(wait) + "ms");
                if (!st.feedStalled)
                {
                    st.feedStalled = true;
                    Raise(s.handle, FgPrEvent_FeedStalled, idx, f->status);
                }
                return;
            }
            // A 404 / 410 at or beyond the segment count (or with no count to bound it) ends the track at this index.
            // Audio running out while video has not ends the audio stream cleanly, so the presentation still ends on
            // the video rather than leaving a stream that can never satisfy another request.
            {
                std::lock_guard<std::mutex> g(s.feedMx);
                if (t == kVideo) s.videoEndIndex = std::min(s.videoEndIndex, idx);
                else s.audioEndIndex = std::min(s.audioEndIndex, idx);
            }
            st.failIdx[t] = -1;
            st.failCount[t] = 0;
            fgpr::RaiseLog(s.handle, std::string("[cenc-feed] ") + (t == kVideo ? "video" : "audio") + " seg#" +
                                     std::to_string(idx) + " HTTP " + std::to_string(f->status) + " - end of " +
                                     (t == kVideo ? "video" : "audio") + " feed at this index");
            return;
        }
        st.failIdx[t] = -1;
        st.failCount[t] = 0;

        std::vector<cenc::Sample> more;
        std::vector<int64_t> keyframes;
        uint64_t ticks = st.ticks[t];
        const int produced = cenc::ParseSegment(f->body, info, more, ticks, t == kVideo ? &keyframes : nullptr);
        f->ReleaseBody();
        if (f->timing.bytes > st.maxSegBytes[t]) st.maxSegBytes[t] = f->timing.bytes;   // the next fetch's reserve
        if (!f->timing.fromStore)   // the WinRT HTTP cache answered this GET itself: not real throughput
        {
            s.bytesDownloaded.fetch_add(f->timing.bytes, std::memory_order_acq_rel);
            s.downloadElapsedMs.fetch_add(std::max<uint64_t>(1, f->timing.transferMs), std::memory_order_acq_rel);
        }
        if (produced <= 0 || more.empty())
        {
            fgpr::RaiseLog(s.handle, std::string("[cenc-feed] ") + (t == kVideo ? "video" : "audio") + " seg#" +
                                     std::to_string(idx) + " demuxed no samples");
            return;
        }
        // The segment-length rule (see the top of this file): measure once, from the first parsed segment.
        if (t == kVideo && s.segmentLengthMs.load(std::memory_order_acquire) <= 0 && info.timescale > 0)
        {
            const uint64_t first = more.front().decodeTicks;
            if (ticks > first)
            {
                const int64_t measured = (int64_t)(((ticks - first) * 1000ULL) / info.timescale);
                if (measured > 0)
                {
                    int32_t expected = 0;
                    if (s.segmentLengthMs.compare_exchange_strong(expected, (int32_t)measured, std::memory_order_acq_rel))
                        fgpr::RaiseLog(s.handle, "[cenc-feed] segment length measured from seg#" + std::to_string(idx) +
                                                 ": " + std::to_string((long long)measured) + "ms");
                }
            }
        }
        st.ticks[t] = ticks;
        // Coverage before/after: what the SPIN BRAKE (below) and the progress guard (FeedPlan.h's Landed) actually
        // learn from this append — not what was merely fetched, which is what the old unconditional `landed=true`
        // conflated (a segment that demuxed and appended but did not extend anything is not "progress").
        const int64_t covBefore = CoverageAt(stream.get(), refMs).endMs;
        bool staleForFlush = false;
        {
            std::lock_guard<std::mutex> g(s.feedMx);
            // An unbuffered seek flushed the streams after this job was planned: `f->cancelled` only catches a GET that
            // was still on the wire (Cancel is a no-op on one that had completed, and a GET already fetching the target
            // segment is deliberately left alone), so data for the OLD position would splice into the buffer the seek
            // just emptied. Only the seek target's own segment survives - it is what the flushed buffer is waiting for.
            staleForFlush = s.flushSeq > seekSeq &&
                idx != SegmentOfUnclamped(s, s.seekKeyframeMs >= 0 ? s.seekKeyframeMs : s.seekTargetMs);
            // Always the CURRENT source's stream: a re-attach may have rebuilt the source while this GET was in flight.
            auto current = t == kVideo ? VideoStreamLocked(s) : AudioStreamLocked(s);
            if (current && !staleForFlush) current->AppendSamples(std::move(more));
        }
        if (staleForFlush)
        {
            fgpr::RaiseLog(s.handle, std::string("[cenc-feed] ") + (t == kVideo ? "video" : "audio") + " seg#" +
                                     std::to_string(idx) + " landed after the seek flush (planned under seq=" +
                                     std::to_string(seekSeq) + ") - dropped");
            return;
        }
        if (t == kVideo)
        {
            for (int64_t kf : keyframes) s.store->keyframes.Append(kf);
            if (!keyframes.empty()) Raise(s.handle, FgPrEvent_Keyframes, idx);
        }
        LogAbrSegment(s, t == kVideo ? "video" : "audio",
                      t == kVideo ? s.store->downloadingRepresentation.load(std::memory_order_acquire) : -1, idx, f->timing,
                      stream ? stream->AheadDurationMs() : 0, stream ? stream->ContiguousAheadMs() : 0);
        landed = true;
        const int64_t covAfter = CoverageAt(stream.get(), refMs).endMs;
        const bool wasUncovered = covBefore < 0;
        if ((wasUncovered && covAfter >= 0) || (!wasUncovered && covAfter > covBefore)) grew = true;
        fgpr::plan::Landed(st.guard[t], idx, covAfter);
    };
    {
        // Clears what a seek cancels once both GETs are handled - or when a handler throws.
        struct InflightClear
        {
            Session& owner;
            ~InflightClear()
            {
                std::lock_guard<std::mutex> g(owner.feedMx);
                owner.inflightVideo = nullptr;
                owner.inflightAudio = nullptr;
                owner.inflightVideoIndex = -1;
            }
        } inflightClear{ s };
        bool videoPending = vf != nullptr, audioPending = af != nullptr;
        while (videoPending || audioPending)
        {
            // A seek plan joins both GETs (video is then handled before audio): see the F041 note above the fetch.
            waitSet->WaitUntil([&] {
                const bool videoReady = !videoPending || vf->IsDone();
                const bool audioReady = !audioPending || af->IsDone();
                return seekPlan ? (videoReady && audioReady) : ((videoPending && videoReady) || (audioPending && audioReady));
            });
            if (s.feedStop.load(std::memory_order_acquire))
            {
                // Teardown cancels what is on the wire; make sure of the one that is still going and append nothing.
                if (videoPending) vf->Cancel();
                if (audioPending) af->Cancel();
                return JobResult::Idle;
            }
            if (videoPending && vf->IsDone()) { videoPending = false; handleTrack(kVideo, vf, vp.idx, videoInfo, video); }
            else { audioPending = false; handleTrack(kAudio, af, ap.idx, audioInfo, audio); }
        }
    }
    SettleFeedStall(s, st);   // a retry that landed (or a track that latched its end) may have ended the stall

    if (landed)
    {
        winrt::com_ptr<CencMediaStream> current;
        {
            std::lock_guard<std::mutex> g(s.feedMx);
            current = VideoStreamLocked(s);
            s.prefetchAnnounce = false;   // this Buffered is the announcement
        }
        RaiseBuffered(s, st, current.get(), refMs, true);
        RaiseBytes(s, st, false);
        video->SetDemandBelowMs(0);
        if (audio) audio->SetDemandBelowMs(0);
    }
    if (nextBackoff > 0)
    {
        // A track failed transiently (whatever the other one did): wait out the back-off before asking again, and keep
        // the playhead's demand hook from cutting it short on every delivered sample.
        const int64_t ahead = CoverageAt(video.get(), refMs).aheadMs;
        const int64_t below = std::max<int64_t>(1, ahead - s.SegLenMs());
        video->SetDemandBelowMs(below);
        if (audio) audio->SetDemandBelowMs(below);
        backoffMs = nextBackoff;
        return JobResult::Backoff;
    }

    // THE SPIN BRAKE. This function used to return Progress here unconditionally, which is what turned any planner
    // fault into a livelock: FeederMain's outer loop (`while (r == Progress) r = RunOneJob(...)`) re-enters at once
    // with no wait of any kind, so a fetch that lands but extends nothing — the production incident, a cancelled
    // fetch, a demux that produced zero samples, a 4xx that only just latched the end index — was retried as fast as
    // the store lock and a WinRT round trip allow (~700/s in the log this fixes, each re-splicing under the lock the
    // decoder needs).
    //
    // A seek that arrived WHILE this job's fetches were in flight is the one exception: it cancelled whatever was on
    // the wire, so `grew` is correctly false, but the seek's own target still needs a plan built for it right away —
    // waiting out a 250 ms back-off before replanning would show up as the seek not landing promptly.
    const bool seekChangedDuringJob = s.seekSeq.load(std::memory_order_acquire) != seekSeq;
    if (grew || seekChangedDuringJob) return JobResult::Progress;

    // No growth and no seek to chase: back off instead of spinning. With the structural (CutGen) reset above and the
    // floor fix in FeedPlan.h, a plan that replans the exact same segment now steps its floor and progresses on the
    // very next attempt, so this back-off is at most a single 250 ms beat before real progress resumes. Any future
    // planner fault that still manages to loop now degrades to 4 fetches/s instead of livelocking the stream lock.
    const int64_t ahead = CoverageAt(video.get(), refMs).aheadMs;
    const int64_t below = std::max<int64_t>(1, ahead - s.SegLenMs());
    video->SetDemandBelowMs(below);
    if (audio) audio->SetDemandBelowMs(below);
    backoffMs = 250;
    return JobResult::Backoff;
}

static void FeederMain(std::shared_ptr<Session> sp)
{
    Session& s = *sp;
    const HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);   // WinRT HTTP needs an apartment
    FeederState st;
    int backoffMs = 0;
    for (;;)
    {
        {
            std::unique_lock<std::mutex> lk(s.signal->mx);
            auto woken = [&] { return s.signal->kick || s.feedStop.load(std::memory_order_acquire); };
            // Indefinite unless a GET just failed transiently: the back-off is the ONE timed wait in the feeder.
            if (backoffMs > 0) s.signal->cv.wait_for(lk, std::chrono::milliseconds(backoffMs), woken);
            else s.signal->cv.wait(lk, woken);
            if (s.feedStop.load(std::memory_order_acquire)) break;
            s.signal->kick = false;
        }
        backoffMs = 0;
        while (!s.feedStop.load(std::memory_order_acquire))
        {
            JobResult r = JobResult::Idle;
            try { r = RunOneJob(sp, st, backoffMs); }
            catch (winrt::hresult_error const& e) { fgpr::RaiseLog(s.handle, "[cenc-feed] job threw hr=" + fgpr::Hex(e.code().value)); r = JobResult::Idle; }
            catch (...) { fgpr::RaiseLog(s.handle, "[cenc-feed] job threw"); r = JobResult::Idle; }
            if (r != JobResult::Progress) break;
        }
    }
    if (SUCCEEDED(hrCo)) CoUninitialize();
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Runtime-thread operations.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

static bool IsLive(Runtime& rt, Session& s)
{
    return rt.attached.load(std::memory_order_acquire) == s.handle && !s.attachPending;
}

static void ApplySeekToEngine(Runtime& rt, Session& s)
{
    int64_t target = 0, keyframe = -1;
    int32_t mode = FgPrSeekMode_Exact;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        target = s.seekTargetMs;
        keyframe = s.seekKeyframeMs;
        mode = s.seekMode;
    }
    // Scrub/preview seeks ask for APPROXIMATE: the engine lands on the nearest keyframe instead of decoding a
    // preroll to the exact frame, which is the difference between a scrub that tracks the thumb and one that
    // lurches. The commit seek is exact. NOTE the surface is never blanked here — in windowless swap-chain mode the
    // engine keeps the latest frame in its swap chain, so the old picture simply holds until the new one decodes.
    const bool approximate = mode == FgPrSeekMode_Keyframe;
    const int64_t landMs = approximate && keyframe >= 0 ? keyframe : target;
    // From here the next SEEKED is the USER seek's: a native-issued seek still in flight is aborted by this one (HTML5
    // seeking semantics), and its tag must not swallow the SEEKED that is coming.
    s.internalSeek.Cancel();
    HRESULT hr = approximate
        ? rt.engineEx->SetCurrentTimeEx((double)landMs / 1000.0, MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE)
        : rt.engine->SetCurrentTime((double)landMs / 1000.0);
    fgpr::RaiseLog(s.handle, "[transport] SEEK ms=" + std::to_string((long long)landMs) +
                             (approximate ? " mode=approximate" : " mode=exact") + " hr=" + fgpr::Hex(hr));
    if (SUCCEEDED(hr))
    {
        // At rate 0 the MF video renderer does not pre-roll, so a seek issued while PAUSED decodes nothing and the
        // surface keeps showing the pre-seek frame forever. One frame step forces the new frame out (Chromium's
        // shipped workaround for the same renderer behaviour).
        if (rt.engine->IsPaused())
        {
            HRESULT hrStep = rt.engineEx->FrameStep(TRUE);
            fgpr::RaiseLog(s.handle, "[transport] paused seek -> FrameStep hr=" + fgpr::Hex(hrStep));
        }
    }
    else
    {
        // The engine refused the reposition: resolve the managed waiter with where playback actually is. An unbuffered
        // seek's flush is holding delivery for a Start(reposition) that will now never come.
        {
            winrt::com_ptr<CencMediaStream> video, audio;
            {
                std::lock_guard<std::mutex> g(s.feedMx);
                video = VideoStreamLocked(s);
                audio = s.haveAudio ? AudioStreamLocked(s) : nullptr;
            }
            if (video) video->ReleaseReposition();
            if (audio) audio->ReleaseReposition();
        }
        s.seeking.store(0, std::memory_order_release);
        fgpr::SessionSamplePosition(rt, s, false);
        Raise(s.handle, FgPrEvent_Seeked, s.positionMs.load(std::memory_order_acquire), fgpr::MsSinceQpc(s.seekPostedQpc));
    }
}

static void ApplyPendingSeek(Runtime& rt, const std::shared_ptr<Session>& sp, uint64_t seq)
{
    Session& s = *sp;
    if (s.seekSeq.load(std::memory_order_acquire) != seq) return;   // latest-wins: superseded before it landed
    if (!IsLive(rt, s)) return;
    ApplySeekToEngine(rt, s);
}

/// Unload whatever source the engine holds (an empty SetSource). Normally called when no successor's SetSource is about
/// to replace it: two loads back to back would let the empty load's asynchronous error land after the real one. The one
/// exception is FgPrSessionAttach re-attaching the same session inside the release grace: the fresh source uses the same
/// cenc:// URL, so the old source must be shut down before its samples are taken over, and a late ERROR from the empty
/// load is filtered by the GetError()==null check in PrRuntime.cpp's media-event handler.
static void ReleaseEngineSource(Runtime& rt, uint64_t logHandle)
{
    if (!rt.engine || rt.engineSource == 0) return;
    BSTR empty = SysAllocString(L"");
    HRESULT hs = rt.engineEx->SetSource(empty);
    SysFreeString(empty);
    rt.engineSource = 0;
    fgpr::RaiseLog(logHandle, "[cenc] engine source released: SetSource(empty) hr=" + fgpr::Hex(hs));
}

/// Arm the unload of the source the engine still holds, a grace from now. A detach only PAUSES: the switch to another
/// video is a Detach, a Destroy and - at least two UI frames later - the successor's Attach, and an unload here would be
/// followed by that attach's real load a moment later (two loads back to back, the first one's asynchronous notifications
/// landing after the second). An attach inside the grace cancels the gate (FgPrSessionAttach) and its own SetSource is the
/// only load; with no successor the item fires and the engine is unloaded as before, just a quarter second later. The gate
/// token makes a fired item a no-op after a cancel or a later re-arm. Runtime thread only.
static void ScheduleEngineRelease(Runtime& rt)
{
    if (!rt.engine || rt.engineSource == 0) return;
    const uint64_t token = rt.releaseGate.Arm();
    Runtime* raw = &rt;   // the runtime thread runs the item and owns a strong ref for as long as it does
    rt.queue.PostAfter(fgpr::handover::kReleaseGraceMs, [raw, token] {
        if (!raw->releaseGate.Fire(token) || raw->attached.load(std::memory_order_acquire) != 0) return;
        ReleaseEngineSource(*raw, raw->engineSource);
    });
}

// ── F198: swap-chain handle ownership ────────────────────────────────────────────────────────────────────────────────
// IMFMediaEngineEx::GetVideoSwapchainHandle hands out a FRESH NT handle per call that the caller closes. The managed presenter's
// CreateSurfaceFromHandle takes its own reference and never owns the value, so the runtime owns every handle it publishes: it
// stays open while it is the session's published FgPrSnapshot.handle (a device recovery binds it again), and one that is replaced
// or whose session detaches is retired and closed health::kHandleGraceMs later, once the render thread has bound its successor.

static int64_t TickMs() { return (int64_t)GetTickCount64(); }

static void CloseSwapchainHandle(uint64_t handle)
{
    if (handle != 0) CloseHandle((HANDLE)(uintptr_t)handle);
}

/// `handle` is no longer any session's published handle: keep it open for the grace, then close it. Runtime thread.
static void RetireSwapchainHandle(Runtime& rt, uint64_t handle)
{
    if (handle == 0) return;
    rt.retiredHandles.Retire(handle, TickMs(), CloseSwapchainHandle);
    Runtime* raw = &rt;   // the runtime thread runs the item and owns a strong ref for as long as it does
    rt.queue.PostAfter((int32_t)(fgpr::health::kHandleGraceMs + 50), [raw] { raw->retiredHandles.Sweep(TickMs(), CloseSwapchainHandle); });
}

/// Publish a freshly queried handle as the session's current one, retiring the handle it replaces. An identical value is the
/// same handle (MF did not duplicate it), which is not a second reference to close. Returns the replaced value.
static uint64_t AdoptSwapchainHandle(Runtime& rt, Session& s, uint64_t value)
{
    const uint64_t prev = s.swapchainHandle.exchange(value, std::memory_order_acq_rel);
    if (prev == value) return prev;
    rt.retiredHandles.Reinstate(value);   // it was waiting out its grace: current again, never closed
    RetireSwapchainHandle(rt, prev);
    return prev;
}

// ── F066: rendered / dropped frames and the rendered-frame hang check ────────────────────────────────────────────────────

static bool ReadEngineStat(Runtime& rt, MF_MEDIA_ENGINE_STATISTIC id, uint32_t& out)
{
    PROPVARIANT pv;
    PropVariantInit(&pv);
    bool ok = false;
    if (SUCCEEDED(rt.engineEx->GetStatistics(id, &pv)) && (pv.vt == VT_UI4 || pv.vt == VT_UI8))
    {
        out = pv.vt == VT_UI4 ? (uint32_t)pv.ulVal : (uint32_t)pv.uhVal.QuadPart;
        ok = true;
    }
    PropVariantClear(&pv);
    return ok;
}

static bool ReadEngineFrameStats(Runtime& rt, uint32_t& rendered, uint32_t& dropped)
{
    return ReadEngineStat(rt, MF_MEDIA_ENGINE_STATISTIC_FRAMES_RENDERED, rendered) &&
           ReadEngineStat(rt, MF_MEDIA_ENGINE_STATISTIC_FRAMES_DROPPED, dropped);
}

/// A new source starts counting from whatever the (shared, warm) engine reads now: MF resets the counters itself once the new
/// source flushes in, which FrameCounters::Observe folds in as a decrease.
static void RebaseFrameStats(Runtime& rt, Session& s)
{
    uint32_t rendered = 0, dropped = 0;
    if (ReadEngineFrameStats(rt, rendered, dropped)) s.frames.Rebase(rendered, dropped);
}

/// One turn of the frame-health check (runtime thread, from the position sample the engine's TIMEUPDATE drives). The counters are
/// read at most every health::kStatsPollMs and only while the source plays video: not paused, seeking, waiting or starved, and
/// only once a stream rect was applied (a swap chain with nothing sized into it has nothing to render). The same turn judges
/// Chromium's rendered-frame detection: playing with NO frame rendered within health::kNoFrameTimeoutMs of PLAYING (or of the last
/// UpdateVideoStream) is a hang - a dead swap-chain handle, a stuck topology, a lost device that never raised an error - and is
/// reported as a typed decode error the managed side turns into a Retryable failure on a rebuilt runtime.
static void PollFrameHealth(Runtime& rt, Session& s)
{
    if (!rt.engineEx) return;
    const int64_t nowMs = TickMs();
    const bool playing = !s.attachPending && s.metadataSeen && s.state.load(std::memory_order_acquire) == FgPrState_Playing &&
                         s.seeking.load(std::memory_order_acquire) == 0 && !s.waitGate.waiting &&
                         s.readyState.load(std::memory_order_acquire) >= 3 &&
                         s.appliedStreamW.load(std::memory_order_acquire) > 0 && s.appliedStreamH.load(std::memory_order_acquire) > 0;
    if (playing && nowMs >= s.nextFrameStatsMs)
    {
        s.nextFrameStatsMs = nowMs + fgpr::health::kStatsPollMs;
        uint32_t rendered = 0, dropped = 0;
        s.frameStatsReadable = ReadEngineFrameStats(rt, rendered, dropped);
        if (s.frameStatsReadable)
        {
            s.frames.Observe(rendered, dropped);
            s.framesRendered.store(s.frames.rendered, std::memory_order_release);
            s.framesDropped.store(s.frames.dropped, std::memory_order_release);
        }
    }
    // A statistics read that fails or comes back empty is telemetry trouble, never a hang (Chromium skips its rendered-frame check
    // unless PopulateStatistics succeeded): the watch only counts time while the counters are actually readable.
    if (s.frameWatch.Observe(nowMs, playing && s.frameStatsReadable, s.frames.rendered))
    {
        fgpr::RaiseLog(s.handle, "[video.render] no frame rendered within " + std::to_string((long long)(fgpr::health::kNoFrameTimeoutMs / 1000)) +
                                 "s of playing (stream " + std::to_string(s.appliedStreamW.load(std::memory_order_acquire)) + "x" +
                                 std::to_string(s.appliedStreamH.load(std::memory_order_acquire)) + ", dropped=" +
                                 std::to_string((long long)s.frames.dropped) + "); the session fails and the runtime is rebuilt");
        s.errorHr.store(fgpr::health::kNoRenderedFrameHr, std::memory_order_release);
        s.state.store(FgPrState_Error, std::memory_order_release);
        Raise(s.handle, FgPrEvent_Error, fgpr::health::kNoRenderedFrameCode, (int64_t)fgpr::health::kNoRenderedFrameHr);
    }
}

/// `releaseSource` = false when the engine's source stays loaded after this detach: an attach of ANOTHER session replaces
/// it (that session's own SetSource unloads it, so nothing loads twice in a row), or the caller arms the grace unload
/// (DetachDeferRelease). True unloads it now - the session failed, there is nothing to hand over.
static void DetachInternal(Runtime& rt, Session& s, bool releaseSource)
{
    const bool wasSetSource = !s.attachPending;
    rt.attached.store(0, std::memory_order_release);   // first: the detach's own PAUSE / source events are dropped
    if (wasSetSource && rt.engine)
    {
        if (s.firstFrameQpc.load(std::memory_order_acquire) != 0)
            s.startPositionMs.store(s.positionMs.load(std::memory_order_acquire), std::memory_order_release);   // a re-attach resumes here
        HRESULT hp = rt.engine->Pause();
        fgpr::RaiseLog(s.handle, "[cenc] detach: Pause hr=" + fgpr::Hex(hp) + (releaseSource ? "" : " (source kept for a successor's SetSource, or released after the grace)"));
        if (releaseSource && rt.engineSource == s.handle) ReleaseEngineSource(rt, s.handle);
    }
    s.attachPending = false;
    s.metadataSeen = false;
    s.waitGate.Reset();
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.streaming = false;
    }
    if (s.license) { fgpr::LicenseBind(s.license, -1); s.license = 0; }
    RetireSwapchainHandle(rt, s.swapchainHandle.exchange(0, std::memory_order_acq_rel));   // F198: closed after the grace, not leaked
    s.appliedStreamW.store(0, std::memory_order_release);
    s.appliedStreamH.store(0, std::memory_order_release);
    s.seeking.store(0, std::memory_order_release);
    s.firstFrameQpc.store(0, std::memory_order_release);
    if (s.state.load(std::memory_order_acquire) != FgPrState_Error) s.state.store(FgPrState_Stopped, std::memory_order_release);
    Raise(s.handle, FgPrEvent_Detached);
    // A predecessor's paused source this session's attach never got to replace (the attach failed, or was released before
    // its inits landed) is nobody's now: unload it after the grace like any other detached source.
    if (releaseSource && rt.engineSource != 0 && rt.engineSource != s.handle) ScheduleEngineRelease(rt);
}

/// A detach with no successor named yet (FgPrSessionDetach, the destroy of the attached session): pause now and leave the
/// unload to the grace, so the Attach that follows a switch finds the engine to hand over instead of an empty one.
static void DetachDeferRelease(Runtime& rt, Session& s)
{
    DetachInternal(rt, s, /*releaseSource*/ false);
    ScheduleEngineRelease(rt);
}

static void CompleteAttach(Runtime& rt, const std::shared_ptr<Session>& sp)
{
    Session& s = *sp;
    if (rt.attached.load(std::memory_order_acquire) != s.handle || !s.attachPending) return;
    if (!s.initsLoaded.load(std::memory_order_acquire)) return;   // the feeder posts this again when they land
    if (FAILED((HRESULT)s.initHr.load(std::memory_order_acquire)))
    {
        DetachInternal(rt, s);
        return;
    }

    winrt::com_ptr<CencMediaSource> source;
    bool encrypted = false;
    try
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        if (s.source && s.source->m_handedToEngine.load(std::memory_order_acquire))
        {
            // A RE-attach. Media Foundation shuts a source down when the engine's source is replaced, and a shut-down
            // IMFMediaEventQueue cannot be revived — so the session builds a fresh CencMediaSource over the SAME
            // buffered samples (the store and the keyframe table are the session's, not the MF object's).
            uint64_t videoBytes = 0, audioBytes = 0;
            auto oldVideo = VideoStreamLocked(s);
            auto oldAudio = AudioStreamLocked(s);
            std::vector<cenc::Sample> videoSamples = oldVideo ? oldVideo->TakeSamples(videoBytes) : std::vector<cenc::Sample>();
            fgpr::RepBook videoBook = oldVideo ? oldVideo->TakeRepBook() : fgpr::RepBook{};
            CencAudioFeed feed;
            if (s.haveAudio)
            {
                feed.info = s.audioInfo;
                if (oldAudio) feed.samples = oldAudio->TakeSamples(audioBytes);
            }
            // The resume point is the playhead, a little behind the delivery cursor; if the byte cap evicted the keyframe
            // its GOP started at, the fresh source's first Start would have nothing to begin from. Resume at the first
            // keyframe of the buffered range instead (at most one GOP later); a position the buffer does not hold stays
            // put - Start finds no keyframe there and the feeder fetches it.
            {
                const int64_t resumeMs = s.startPositionMs.load(std::memory_order_acquire);
                const int64_t keyMs = fgpr::ResumeStartMs(videoSamples, s.videoInfo.timescale, resumeMs);
                if (keyMs != resumeMs)
                {
                    s.startPositionMs.store(keyMs, std::memory_order_release);
                    fgpr::RaiseLog(s.handle, "[cenc] re-attach: no keyframe at or before " + std::to_string((long long)resumeMs) +
                                             "ms was retained - resuming at the first buffered keyframe " + std::to_string((long long)keyMs) + "ms");
                }
            }
            const int64_t totalMs = EffectiveDurationMs(s);
            auto fresh = BuildCencSource(s.videoInfo, std::move(videoSamples), s.haveAudio ? &feed : nullptr, true,
                                         totalMs > 0 ? (uint64_t)totalMs * 10000ULL : 0ULL, s.handle, s.store, 0);
            if (!fresh->m_streams.empty() && fresh->m_streams[0]) fresh->m_streams[0]->AdoptRepBook(std::move(videoBook));
            InstallDemandHooks(s, fresh.get());
            s.source = fresh;
            fgpr::RaiseLog(s.handle, "[cenc] re-attach: fresh source over the retained buffer (" +
                                     std::to_string(videoBytes + audioBytes) + " bytes)");
        }
        source = s.source;
        if (source)
        {
            source->m_handedToEngine.store(true, std::memory_order_release);
            source->SetStartPosition100ns((LONGLONG)s.startPositionMs.load(std::memory_order_acquire) * 10000LL);
        }
        encrypted = s.videoInfo.encrypted || (s.haveAudio && s.audioInfo.encrypted);
    }
    catch (winrt::hresult_error const& e)
    {
        fgpr::RaiseLog(s.handle, "[cenc] re-attach BuildCencSource failed hr=" + fgpr::Hex(e.code().value));
        s.errorHr.store(e.code().value, std::memory_order_release);
        s.state.store(FgPrState_Error, std::memory_order_release);
        Raise(s.handle, FgPrEvent_Error, 4 /*MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED*/, (int64_t)(int32_t)e.code().value);
        DetachInternal(rt, s);
        return;
    }
    if (!source) { DetachInternal(rt, s); return; }

    if (encrypted)
    {
        // Desktop PMP bridge: the protected source exposes the CDM's trusted input so Media Foundation can obtain
        // the per-stream ITA/decrypter inside Windows' protected process.
        // The proactive EME session already supplied the PSSH. Match Firefox's working desktop MFCDM path and let
        // the CDM associate that session with the trusted input; passing the same PSSH again creates a second content
        // binding whose ITA proxy is rejected during protected-topology negotiation on some PlayReady implementations.
        //
        // With FG_PLAYREADY_TRUSTED_INPUT_REUSE=1 the trusted input is held at runtime (CDM) scope: the first protected
        // attach creates it and every later source shares it, so a switch skips the CreateTrustedInput round trip into
        // mfpmp.exe. The source's own ITA cache is still per source (BuildCencSource), so a new source asks the shared
        // trusted input for fresh ITAs. Off by default (Runtime::trustedInputReuse); the log line says which path ran.
        const bool reuse = rt.trustedInputReuse && rt.trustedInput;
        if (reuse)
        {
            source->m_trustedInput = rt.trustedInput;
            fgpr::RaiseLog(s.handle, "[cenc] trusted input REUSED (CDM scope)");
        }
        else
        {
            IMFTrustedInput* trustedInput = nullptr;
            HRESULT hr = rt.cdm->CreateTrustedInput(nullptr, 0, &trustedInput);
            fgpr::RaiseLog(s.handle, std::string("[cenc] CreateTrustedInput hr=") + fgpr::Hex(hr) +
                                     (rt.trustedInputReuse ? " (created once for the CDM)" : " (per attach)"));
            if (FAILED(hr) || !trustedInput)
            {
                s.errorHr.store(FAILED(hr) ? hr : E_NOINTERFACE, std::memory_order_release);
                s.state.store(FgPrState_Error, std::memory_order_release);
                Raise(s.handle, FgPrEvent_Error, 4 /*MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED*/, (int64_t)(int32_t)(FAILED(hr) ? hr : E_NOINTERFACE));
                DetachInternal(rt, s);
                return;
            }
            source->m_trustedInput.attach(trustedInput);
            if (rt.trustedInputReuse) rt.trustedInput = source->m_trustedInput;
        }
    }

    // The engine is shared by every session, so the DEFAULT rate is set explicitly for every source, 1.0 included, before
    // SetSource: with HTML5 load semantics a new source starts at defaultPlaybackRate, and skipping the 1.0 case let a
    // 1.5x session's default leak into the next video. The rate itself follows the SetSource below.
    const double rate = (double)s.rateMicro.load(std::memory_order_acquire) / 1000000.0;
    {
        const HRESULT hd = rt.engine->SetDefaultPlaybackRate(rate);
        if (FAILED(hd)) fgpr::RaiseLog(s.handle, "[transport] DEFAULT RATE hr=" + fgpr::Hex(hd));
    }

    const std::wstring url = L"cenc://fluentgpu/" + std::to_wstring(s.handle);
    BSTR burl = SysAllocString(url.c_str());
    HRESULT hr = rt.engineEx->SetSource(burl);
    SysFreeString(burl);
    s.attachPending = false;
    const int64_t attachMs = fgpr::MsSinceQpc(s.attachPostedQpc);
    fgpr::RaiseLog(s.handle, "[cenc] SetSource(" + fgpr::Narrow(url) + ") hr=" + fgpr::Hex(hr) + " attachMs=" +
                             std::to_string((long long)attachMs) + " start=" +
                             std::to_string((long long)s.startPositionMs.load(std::memory_order_acquire)) + "ms");
    if (FAILED(hr))
    {
        s.errorHr.store(hr, std::memory_order_release);
        s.state.store(FgPrState_Error, std::memory_order_release);
        Raise(s.handle, FgPrEvent_Error, 4 /*MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED*/, (int64_t)(int32_t)hr);
        s.attachPending = true;   // DetachInternal must not Pause/SetSource an engine that never took this source
        DetachInternal(rt, s);
        return;
    }
    rt.engineSource = s.handle;
    RebaseFrameStats(rt, s);   // F066
    Raise(s.handle, FgPrEvent_Attached, attachMs);

    const double volume = (double)s.volumeMicro.load(std::memory_order_acquire) / 1000000.0;
    HRESULT hv = rt.engine->SetVolume(volume);
    if (FAILED(hv)) fgpr::RaiseLog(s.handle, "[transport] VOLUME hr=" + fgpr::Hex(hv));
    HRESULT hrt = rt.engine->SetPlaybackRate(rate);
    if (FAILED(hrt)) fgpr::RaiseLog(s.handle, "[transport] RATE hr=" + fgpr::Hex(hrt));
    // THE CARRIED START POSITION goes onto the ENGINE's timeline now, after SetSource and before Play (the engine accepts
    // a seek before the source has loaded and applies it once it has - Chromium's StartPlayingFrom does the same), so its
    // first Start of the source already asks for the start position and the clock agrees with the samples from the first
    // Start. Applying it at the source layer alone left the engine clock at 0 and cost a second Stop/Start of the
    // protected pipeline once CANPLAY noticed. The native seek is tagged (plan::InternalSeek): its SEEKED is never raised
    // to the managed side and never swallows a user seek's.
    const int64_t carriedStartMs = s.startPositionMs.load(std::memory_order_acquire);
    if (carriedStartMs > 0)
    {
        s.internalSeek.Issue(s.seekSeq.load(std::memory_order_acquire));
        const HRESULT hst = rt.engine->SetCurrentTime((double)carriedStartMs / 1000.0);
        fgpr::RaiseLog(s.handle, "[cenc] carried start " + std::to_string((long long)carriedStartMs) +
                                 "ms -> engine timeline SetCurrentTime hr=" + fgpr::Hex(hst));
        if (FAILED(hst)) s.internalSeek.Cancel();
    }
    if (s.wantPlay)
    {
        HRESULT hp = rt.engine->Play();
        fgpr::RaiseLog(s.handle, "[transport] PLAY hr=" + fgpr::Hex(hp) + " (attach)");
    }
}

/// The part of a session's destroy that waits for its feeder to be gone (the reaper runs it after the join): release the
/// source and the download buffers the feeder was filling.
static void FinishDestroy(Session& s)
{
    winrt::com_ptr<CencMediaSource> source;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        source = std::move(s.source);
    }
    // Every stream still told: a stream left incomplete would park requests forever instead of reporting end-of-stream
    // (Media Foundation may still hold this source for a moment).
    if (source)
        for (auto const& stream : source->m_streams)
            if (stream) stream->MarkComplete();
    s.store->pool.Clear();
    fgpr::RaiseLog(s.handle, "[cenc] session destroyed (store held " + std::to_string(s.store->Bytes()) + " bytes)");
}

static void DestroyInternal(Runtime& rt, const std::shared_ptr<Session>& sp)
{
    Session& s = *sp;
    // The attached session's source stays loaded (paused) through the grace, so an attach that follows replaces it with
    // no empty load in between. A session that is not attached already detached: its source - or the one a successor is
    // waiting to replace - is on the grace timer or gone, and the destroy has nothing of the engine's to release.
    if (rt.attached.load(std::memory_order_acquire) == s.handle) DetachDeferRelease(rt, s);

    // Stop the feeder: flag, cancel whatever is on the wire (the token completes the wait at once), wake. Never JOIN it
    // here: this is the runtime thread, and a feeder in the middle of a GET or a parse would hold every engine event and
    // the successor's attach behind it. The reaper joins it and then runs the rest of the teardown, which must not
    // overlap the feeder. (A GET begun after this read sees feedStop in PublishInflight and cancels itself.)
    s.feedStop.store(true, std::memory_order_release);
    std::shared_ptr<fgpr::HttpFetch> v, a;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        v = s.inflightVideo;
        a = s.inflightAudio;
    }
    if (v) v->Cancel();
    if (a) a->Cancel();
    s.Kick();
    rt.reaper.Reap(std::move(s.feeder), [sp] { FinishDestroy(*sp); });
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Cross-TU entry points.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

winrt::com_ptr<::IUnknown> fgpr::SessionSourceForUrl(const wchar_t* url)
{
    if (!url) return nullptr;
    const std::wstring u(url);
    const size_t slash = u.find_last_of(L'/');
    if (slash == std::wstring::npos || slash + 1 >= u.size()) return nullptr;
    const uint64_t handle = _wcstoui64(u.c_str() + slash + 1, nullptr, 10);
    std::shared_ptr<Session> sp = SessionByHandle(handle);
    if (!sp) return nullptr;
    winrt::com_ptr<::IUnknown> unknown;
    std::lock_guard<std::mutex> g(sp->feedMx);
    if (sp->source) sp->source->QueryInterface(__uuidof(::IUnknown), unknown.put_void());
    return unknown;
}

void fgpr::SessionPublishHandle(Runtime& rt, Session& s, bool reRaise)
{
    if (!IsLive(rt, s) || !s.metadataSeen) return;
    if (!reRaise && s.swapchainHandle.load(std::memory_order_acquire) != 0) return;   // published: nothing to ask

    DWORD nvw = 0, nvh = 0;
    rt.engine->GetNativeVideoSize(&nvw, &nvh);
    if (!nvw || !nvh)
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        nvw = s.videoInfo.width ? s.videoInfo.width : 1280;
        nvh = s.videoInfo.height ? s.videoInfo.height : 720;
    }
    RECT dst = { 0, 0, (LONG)nvw, (LONG)nvh };
    if (s.streamWidth > 0 && s.streamHeight > 0) dst = { 0, 0, (LONG)s.streamWidth, (LONG)s.streamHeight };
    MFARGB border = { 0, 0, 0, 255 };
    // MF only creates the windowless swap chain once UpdateVideoStream has given it a NON-ZERO destination
    // rectangle, so the rect is logged with the first handle query: a {0,0,0,0} or 1x1 dst here is the bug, not
    // the handle. An invalid handle on a SUCCEEDED (typically S_FALSE) hr means "not ready yet", never "done".
    HRESULT hu = rt.engineEx->UpdateVideoStream(nullptr, &dst, &border);
    if (SUCCEEDED(hu))
    {
        // The echo the managed side waits for before it scales by this size (see FgPrSnapshot.streamWidth).
        const bool sizeChanged = s.appliedStreamW.load(std::memory_order_relaxed) != (int32_t)dst.right ||
                                 s.appliedStreamH.load(std::memory_order_relaxed) != (int32_t)dst.bottom;
        s.appliedStreamW.store((int32_t)dst.right, std::memory_order_release);
        s.appliedStreamH.store((int32_t)dst.bottom, std::memory_order_release);
        // F066: a re-sized (re-created) stream gets a fresh no-frame window. Only a CHANGED size: this runs on every call while no
        // handle has been obtained yet, and restarting each time would keep a source that never gets a swap chain from ever timing out.
        if (sizeChanged) s.frameWatch.Restart(TickMs());
        // A re-created swap chain of a source that is not playing holds no frame at this size and nothing presents one by
        // itself: ask for the current frame to be rendered into it (a playing source presents its own next frame).
        if (reRaise && s.state.load(std::memory_order_acquire) != FgPrState_Playing)
            rt.engineEx->UpdateVideoStream(nullptr, nullptr, nullptr);
    }
    HANDLE handle = nullptr;
    HRESULT hr = rt.engineEx->GetVideoSwapchainHandle(&handle);
    const bool usable = SUCCEEDED(hr) && handle != nullptr && handle != INVALID_HANDLE_VALUE;
    if (!usable)
    {
        if (++s.handleTries == 1)
            fgpr::RaiseLog(s.handle, "[cenc] first GetVideoSwapchainHandle hr=" + fgpr::Hex(hr) + " (not ready) dst={0,0," +
                                     std::to_string(dst.right) + "," + std::to_string(dst.bottom) + "} native=" +
                                     std::to_string(nvw) + "x" + std::to_string(nvh) + " update hr=" + fgpr::Hex(hu));
        return;
    }
    const uint64_t value = (uint64_t)(uintptr_t)handle;
    const uint64_t prev = AdoptSwapchainHandle(rt, s, value);   // F198: the runtime owns it; the one it replaces is retired
    if (prev != value || reRaise)
    {
        fgpr::RaiseLog(s.handle, "[cenc] " + std::to_string(nvw) + "x" + std::to_string(nvh) +
                                 " swap-chain handle=" + std::to_string(value) + " after " +
                                 std::to_string(s.handleTries) + " not-ready quer" + (s.handleTries == 1 ? "y" : "ies"));
        Raise(s.handle, FgPrEvent_Handle, (int64_t)value);
    }
}

// MF_MEDIA_ENGINE_EVENT_FORMATCHANGE (runtime thread): a representation switch changed the decoded frame size. It is a SIZE
// REPORT, as in Firefox (NotifyVideoResizing) and Chromium (OnVideoNaturalSizeChange): the new natural size goes into the
// snapshot and FgPrEvent_SizeChanged asks the managed pump for a look, and that is all. UpdateVideoStream is NOT called
// (the destination is the managed stream size, which the pump re-derives from the new natural size, so a repeat here is a
// redundant cross-process call that can only race the compositor's scale) and the handle is not re-raised: RESOURCELOST
// keeps SessionPublishHandle(reRaise). The handle is asked for once, to catch a swap chain that really was re-created.
void fgpr::SessionOnFormatChange(Runtime& rt, Session& s)
{
    if (!IsLive(rt, s) || !s.metadataSeen) return;

    DWORD nvw = 0, nvh = 0;
    rt.engine->GetNativeVideoSize(&nvw, &nvh);
    if (nvw && nvh && ((int32_t)nvw != s.width.load(std::memory_order_acquire) || (int32_t)nvh != s.height.load(std::memory_order_acquire)))
    {
        s.width.store((int32_t)nvw, std::memory_order_release);
        s.height.store((int32_t)nvh, std::memory_order_release);
        fgpr::RaiseLog(s.handle, "[cenc] format change: natural size " + std::to_string(nvw) + "x" + std::to_string(nvh));
        Raise(s.handle, FgPrEvent_SizeChanged, (int64_t)nvw, (int64_t)nvh);
    }

    if (s.swapchainHandle.load(std::memory_order_acquire) == 0) return;   // nothing published yet: the ordinary publication asks
    HANDLE handle = nullptr;
    const HRESULT hr = rt.engineEx->GetVideoSwapchainHandle(&handle);
    if (FAILED(hr) || handle == nullptr || handle == INVALID_HANDLE_VALUE) return;
    const uint64_t value = (uint64_t)(uintptr_t)handle;
    if (AdoptSwapchainHandle(rt, s, value) == value) return;   // F198
    fgpr::RaiseLog(s.handle, "[cenc] format change: swap-chain handle=" + std::to_string(value) + " (re-created)");
    Raise(s.handle, FgPrEvent_Handle, (int64_t)value);
}

void fgpr::SessionSamplePosition(Runtime& rt, Session& s, bool raiseNow)
{
    if (!IsLive(rt, s)) return;
    const double t = rt.engine->GetCurrentTime();
    const int64_t qpc = QpcNow();
    const int64_t ms = std::isfinite(t) && t > 0.0 ? (int64_t)(t * 1000.0) : 0;
    s.positionMs.store(ms, std::memory_order_release);
    s.positionQpc.store(qpc, std::memory_order_release);
    s.readyState.store((int32_t)rt.engine->GetReadyState(), std::memory_order_release);
    const double dur = rt.engine->GetDuration();
    if (std::isfinite(dur) && dur > 0.0) s.durationMs.store((int64_t)(dur * 1000.0), std::memory_order_release);

    winrt::com_ptr<CencMediaStream> video;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        video = VideoStreamLocked(s);
    }
    if (video)
    {
        const Coverage cov = CoverageAt(video.get(), ms);
        s.bufferedAheadMs.store(cov.aheadMs, std::memory_order_release);
        s.retainedBehindMs.store(cov.behindMs, std::memory_order_release);
    }

    if (raiseNow || (ms != s.lastPositionRaisedMs && MsSinceQpc(s.lastPositionRaiseQpc) >= 250))
    {
        s.lastPositionRaisedMs = ms;
        s.lastPositionRaiseQpc = qpc;
        Raise(s.handle, FgPrEvent_Position, ms, qpc);
    }

    PollFrameHealth(rt, s);   // F066: after the Position raise, so a hang's Error is the newest event
}

void fgpr::SessionOnCanPlay(Runtime& rt, Session& s)
{
    if (!IsLive(rt, s) || s.startCorrectionDone) return;
    s.startCorrectionDone = true;
    // FALLBACK for the carried start position (S1). CompleteAttach put it on the engine timeline before Play, so the clock
    // normally reads about the start by now and this does nothing. Should the engine not have adopted it (the source's
    // first-Start rewrite then began the samples at the start with the clock at 0), the playhead reads far behind the
    // start by CANPLAY - before any frame is presented - and one native seek puts it right, at the price of a second
    // source Start. `startCorrectionMs` records that price for the switch budget. The seek is tagged (plan::InternalSeek),
    // so its SEEKED is not reported as FgPrEvent_Seeked (nobody on the managed side issued it) and never swallows a user
    // seek's.
    const int64_t start = s.startPositionMs.load(std::memory_order_acquire);
    const double now = rt.engine->GetCurrentTime();
    const int64_t nowMs = std::isfinite(now) ? (int64_t)(now * 1000.0) : 0;
    if (start > 0 && nowMs + s.SegLenMs() < start)
    {
        s.internalSeek.Issue(s.seekSeq.load(std::memory_order_acquire));
        HRESULT hr = rt.engine->SetCurrentTime((double)start / 1000.0);
        fgpr::RaiseLog(s.handle, "[cenc] start position correction (fallback): engine at " + std::to_string((long long)nowMs) +
                                 "ms, carried start " + std::to_string((long long)start) + "ms -> SetCurrentTime hr=" + fgpr::Hex(hr));
        if (FAILED(hr)) s.internalSeek.Cancel();
        else s.startCorrectionMs = start - nowMs;
    }
    if (!s.wantPlay)
    {
        // An open PAUSED (FgPrOpenDesc.startPaused): at rate 0 the MF video renderer does not pre-roll, so nothing would
        // ever be presented. One frame step forces the first frame out and FIRSTFRAMEREADY follows.
        HRESULT hs = rt.engineEx->FrameStep(TRUE);
        fgpr::RaiseLog(s.handle, "[transport] paused open -> FrameStep hr=" + fgpr::Hex(hs));
    }
}

void fgpr::SessionsShutdown(Runtime& rt)
{
    std::vector<std::shared_ptr<Session>> mine;
    {
        std::lock_guard<std::mutex> g(Reg().mx);
        for (auto it = Reg().sessions.begin(); it != Reg().sessions.end(); )
        {
            if (it->second->runtime == rt.handle) { mine.push_back(it->second); it = Reg().sessions.erase(it); }
            else ++it;
        }
    }
    for (auto const& sp : mine) DestroyInternal(rt, sp);
    // F198: whatever the grace timers had not closed yet (their items are dropped once the queue stops) goes with the runtime.
    rt.retiredHandles.CloseAll(CloseSwapchainHandle);
    // The join point for the feeders just stopped: nothing may still be building a Media Foundation source when the
    // runtime shuts Media Foundation down. They are cancelled and kicked above, so this waits out one GET's cancellation.
    rt.reaper.Drain();
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Exports.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// Post a verb for a live session. E_HANDLE for an unknown handle, MF_E_SHUTDOWN when the runtime is going away. A verb that
/// reaches the runtime thread after a FAILED bring-up is not dropped in silence (the session would wait for an ack that can
/// never come): it raises FgPrEvent_Error with the bring-up HRESULT, which the managed side turns into a typed, retryable
/// failure. A runtime that is merely shutting down stays quiet - its owner is destroying it on purpose.
static int32_t PostVerb(FgPrRuntime rtHandle, FgPrSession sh, std::function<void(Runtime&, const std::shared_ptr<Session>&)> fn)
{
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return E_HANDLE;
    std::shared_ptr<Runtime> rt = fgpr::RuntimeFor(rtHandle);
    if (!rt) return E_HANDLE;
    Runtime* raw = rt.get();
    const bool posted = rt->queue.Post([raw, sp, fn = std::move(fn)] {
        if (!raw->Ready())
        {
            const HRESULT bring = (HRESULT)raw->bringUp.load(std::memory_order_acquire);
            if (FAILED(bring)) Raise(sp->handle, FgPrEvent_Error, 0, (int64_t)(int32_t)bring);
            return;
        }
        fn(*raw, sp);
    });
    return posted ? S_OK : MF_E_SHUTDOWN;
}

static std::wstring CopyW(const wchar_t* s) { return s ? std::wstring(s) : std::wstring(); }

/// What an attach asks of the feeder: the init segments, then the buffer-ahead window around the playhead. Any thread;
/// FgPrSessionAttach calls it on the caller's thread (so the GETs start at once) and again from its runtime-thread verb
/// (a Detach queued ahead of the Attach would have turned `streaming` off in between). Only the verb passes
/// `retryFailedInit`: forgetting an earlier failure is part of the attach bookkeeping, and the caller-thread kick would
/// otherwise erase a failure that happened during THIS attach's cold window before the verb could report it.
static void RequestInitsAndStream(Session& s, bool retryFailedInit)
{
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.wantInits = true;
        s.streaming = true;
    }
    if (retryFailedInit) s.initHr.store(S_OK, std::memory_order_release);   // retry a failed init (the feeder is idle until now)
    s.Kick();
}

__declspec(dllexport) int32_t __stdcall FgPrSessionCreate(FgPrRuntime rtHandle, const FgPrOpenDesc* desc, FgPrSession* out)
{
    if (!out) return E_POINTER;
    *out = 0;
    std::shared_ptr<Runtime> rt = fgpr::RuntimeFor(rtHandle);
    if (!rt) return E_HANDLE;
    if (!desc || desc->structSize != sizeof(FgPrOpenDesc) || !desc->initUrl || !*desc->initUrl) return E_INVALIDARG;
    if (desc->psshLen < 0 || (desc->psshLen > 0 && !desc->pssh)) return E_INVALIDARG;

    std::shared_ptr<Session> sp;
    try
    {
        sp = std::make_shared<Session>();
        Session& s = *sp;
        s.handle = fgpr::NewHandle(fgpr::HandleKind::Session);
        s.runtime = rtHandle;
        // Deep copies: the caller's strings do not outlive this call.
        s.initUrl = CopyW(desc->initUrl);
        s.segBase = CopyW(desc->segmentBaseUrl);
        s.segPrefix = CopyW(desc->segmentPrefix);
        s.segSuffix = desc->segmentSuffix && *desc->segmentSuffix ? std::wstring(desc->segmentSuffix) : std::wstring(L".m4s");
        s.audioInitUrl = CopyW(desc->audioInitUrl);
        s.audioSegBase = CopyW(desc->audioSegmentBaseUrl);
        s.audioSegPrefix = CopyW(desc->audioSegmentPrefix);
        s.audioSegSuffix = CopyW(desc->audioSegmentSuffix);
        s.headers = CopyW(desc->httpHeaders);
        s.kidHex = fgpr::NormalizeKidHex(desc->keyIdHex);
        if (desc->pssh && desc->psshLen > 0) s.pssh.assign(desc->pssh, desc->pssh + desc->psshLen);
        s.startNumber = desc->startNumber >= 0 ? desc->startNumber : 1;   // >= 0: Spotify's timestamped segments start at 0
        s.segmentCount = desc->segmentCount > 0 ? desc->segmentCount : 0;
        s.segmentStride = desc->segmentStrideSeconds > 0 ? desc->segmentStrideSeconds : 1;   // 1 = numbered; N = N-second timestamped step
        s.segmentLengthMs.store(desc->segmentLengthMs > 0 ? desc->segmentLengthMs : 0, std::memory_order_release);
        s.descDurationMs = desc->durationMs > 0 ? desc->durationMs : 0;
        s.startPositionMs.store(desc->startPositionMs > 0 ? desc->startPositionMs : 0, std::memory_order_release);
        s.startPaused = desc->startPaused != 0;
        s.wantPlay = !s.startPaused;
        s.store = std::make_shared<fgpr::SegmentStore>();
        s.store->Configure(desc->retainBehindMs, desc->bufferAheadMs, desc->storeBudgetBytes > 0 ? (uint64_t)desc->storeBudgetBytes : 0);
        const int64_t dur = EffectiveDurationMs(s);
        if (dur > 0) s.durationMs.store(dur, std::memory_order_release);
        s.feeder = std::thread(FeederMain, sp);
    }
    catch (...)
    {
        if (sp && sp->feeder.joinable()) { sp->feedStop.store(true); sp->Kick(); sp->feeder.join(); }
        return E_OUTOFMEMORY;
    }
    {
        std::lock_guard<std::mutex> g(fgpr::Reg().mx);
        fgpr::Reg().sessions[sp->handle] = sp;
    }
    Session& s = *sp;
    fgpr::RaiseLog(s.handle, "[cenc] session created: initUrl=" + fgpr::Narrow(s.initUrl) +
                             " segs=" + std::to_string(s.startNumber) + "+" + std::to_string(s.segmentStride) + "*i" +
                             " count=" + (s.segmentCount > 0 ? std::to_string(s.segmentCount) : std::string("derived")) +
                             " segLen=" + (desc->segmentLengthMs > 0 ? std::to_string(desc->segmentLengthMs) + "ms" : std::string("measured")) +
                             " audio=" + (s.audioInitUrl.empty() ? "none" : "yes") +
                             " descriptorPssh=" + std::to_string(s.pssh.size()) + "B descriptorKid=" +
                             (s.kidHex.empty() ? std::string("none") : fgpr::Narrow(s.kidHex)) +
                             " start=" + std::to_string((long long)s.startPositionMs.load()) + "ms" +
                             " paused=" + (s.startPaused ? "1" : "0") +
                             " window=-" + std::to_string((long long)s.store->retainBehindMs) + "/+" +
                             std::to_string((long long)s.store->bufferAheadMs) + "ms budget=" + std::to_string(s.store->budgetBytes));
    *out = sp->handle;
    return S_OK;
}

__declspec(dllexport) int32_t __stdcall FgPrSessionPrefetch(FgPrRuntime rtHandle, FgPrSession sh, int64_t aroundMs, int32_t segments)
{
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return E_HANDLE;
    Session& s = *sp;
    const int64_t around = aroundMs >= 0 ? aroundMs : s.startPositionMs.load(std::memory_order_acquire);
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.wantInits = true;
        s.prefetchAroundMs = around;
        s.prefetchSegments = segments > 0 ? segments : 2;
        s.prefetchAnnounce = true;
    }
    // A prepare after a failed init retries it (the network may be back).
    if (FAILED((HRESULT)s.initHr.load(std::memory_order_acquire)) && !s.initsLoaded.load(std::memory_order_acquire))
    {
        s.initHr.store(S_OK, std::memory_order_release);
        s.state.store(FgPrState_Loading, std::memory_order_release);
    }
    int32_t idle = FgPrState_Idle;
    s.state.compare_exchange_strong(idle, FgPrState_Loading, std::memory_order_acq_rel);
    s.Kick();
    return S_OK;
}

__declspec(dllexport) int32_t __stdcall FgPrSessionAttach(FgPrRuntime rtHandle, FgPrSession sh, FgPrLicense lic)
{
    if (lic != 0 && !fgpr::IsKind(lic, fgpr::HandleKind::License)) return E_HANDLE;
    const int64_t postedQpc = fgpr::QpcNow();
    // THE EARLY KICK. The feeder thread has existed since FgPrSessionCreate and the init/segment GETs need neither the
    // runtime, the CDM nor the engine, yet the kick used to live inside the runtime-thread verb below - queued behind the
    // cold bring-up (450-675 ms) and the licence's GenerateRequest round trip - so on a cold switch the ~170 ms init GET
    // and the first segment GETs only began once both were done. Kicking here, synchronously on the caller's thread, lets
    // the network overlap them; the verb then only does the attach bookkeeping and CompleteAttach (the source build waits
    // for MFStartup, see WaitForMediaFoundation). Like ExoPlayer/Shaka, download never waits for the key.
    // The early kick never resets a failure: a session that already failed its inits (a retried attach) is left idle and
    // the verb retries it in order, while a failure that lands during the cold window is THIS attach's and the verb
    // finishes it as an Error instead of retrying behind FailSession's back.
    bool priorInitFailure = false;
    if (std::shared_ptr<Session> early = fgpr::SessionFor(rtHandle, sh))
    {
        priorInitFailure = !early->initsLoaded.load(std::memory_order_acquire) &&
                           FAILED((HRESULT)early->initHr.load(std::memory_order_acquire));
        if (!priorInitFailure) RequestInitsAndStream(*early, /*retryFailedInit*/ false);
    }
    return PostVerb(rtHandle, sh, [lic, postedQpc, priorInitFailure](Runtime& rt, const std::shared_ptr<Session>& sp) {
        Session& s = *sp;
        const uint64_t prev = rt.attached.load(std::memory_order_acquire);
        if (prev == s.handle) return;   // already attached (or attaching)
        // THE HAND-OVER. A detach only paused the engine and armed the unload of its source; this attach's own SetSource
        // replaces that source, so the unload is cancelled and nothing loads twice in a row. If the engine still holds
        // THIS session's own previous source (a detach and a re-attach inside the grace), that one is unloaded first: the
        // fresh source goes in under the same cenc:// URL, and a load of the URL already loaded is not something to lean on.
        rt.releaseGate.Cancel();
        if (rt.engineSource == s.handle) ReleaseEngineSource(rt, s.handle);
        if (prev)
        {
            std::shared_ptr<Session> old = fgpr::SessionByHandle(prev);
            if (old) DetachInternal(rt, *old, /*releaseSource*/ false);   // this attach's SetSource replaces it
            else rt.attached.store(0, std::memory_order_release);
        }
        // lic == 0 is legal: clear content, or a key already usable through another session's license.
        s.license = lic;
        if (lic) fgpr::LicenseBind(lic, +1);
        // This session's own licence starts ahead of its attach (CreateSession/GenerateRequest before CreateTrustedInput): only
        // other rows' licence work yields to the engine lane. The queued Maintenance StartAcquisition is then a no-op.
        if (lic) fgpr::LicenseStartIfPending(rt, lic);
        rt.attachGen.fetch_add(1, std::memory_order_acq_rel);   // before `attached` names the session: see Runtime::attachGen
        rt.attached.store(s.handle, std::memory_order_release);
        s.attachPending = true;
        s.attachPostedQpc = postedQpc;
        s.metadataSeen = false;
        s.waitGate.Reset();
        s.startCorrectionDone = false;
        s.startCorrectionMs = 0;
        s.internalSeek.Cancel();
        s.handleTries = 0;
        RetireSwapchainHandle(rt, s.swapchainHandle.exchange(0, std::memory_order_acq_rel));   // F198
        s.appliedStreamW.store(0, std::memory_order_release);
        s.appliedStreamH.store(0, std::memory_order_release);
        s.frames.Reset(); s.frameWatch.Reset(); s.nextFrameStatsMs = 0; s.frameStatsReadable = false;   // F066: this attach counts from zero
        s.framesRendered.store(0, std::memory_order_release);
        s.framesDropped.store(0, std::memory_order_release);
        s.firstFrameQpc.store(0, std::memory_order_release);
        s.readyState.store(0, std::memory_order_release);
        s.errorHr.store(0, std::memory_order_release);
        s.state.store(FgPrState_Loading, std::memory_order_release);
        // An init failure that landed since the caller-thread kick (and was not there before it) belongs to this attach:
        // FailSession already raised its one Error. Put the Error state back over the Loading just stored (the order
        // makes this race-free: a failure after this check stores the same values itself) and release the attach, as
        // CompleteAttach does for a failed init. FailSession's queued detach is then a no-op, whichever of the two ran first.
        const HRESULT initFailure = (HRESULT)s.initHr.load(std::memory_order_acquire);
        if (!priorInitFailure && !s.initsLoaded.load(std::memory_order_acquire) && FAILED(initFailure))
        {
            s.errorHr.store((int32_t)initFailure, std::memory_order_release);
            s.state.store(FgPrState_Error, std::memory_order_release);
            DetachInternal(rt, s);
            return;
        }
        RequestInitsAndStream(s, priorInitFailure);   // the early kick already ran unless the inits had failed before this attach
        CompleteAttach(rt, sp);   // at once when the inits are already parsed; otherwise the feeder completes it
    });
}

__declspec(dllexport) int32_t __stdcall FgPrSessionDetach(FgPrRuntime rtHandle, FgPrSession sh)
{
    return PostVerb(rtHandle, sh, [](Runtime& rt, const std::shared_ptr<Session>& sp) {
        if (rt.attached.load(std::memory_order_acquire) != sp->handle) return;
        DetachDeferRelease(rt, *sp);   // pause now; the engine's source is unloaded after the grace unless an attach takes it over
    });
}

__declspec(dllexport) void __stdcall FgPrSessionDestroy(FgPrRuntime rtHandle, FgPrSession sh)
{
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return;
    {
        // The handle dies NOW: every later call with it is E_HANDLE, and the scheme handler can no longer resolve it.
        std::lock_guard<std::mutex> g(fgpr::Reg().mx);
        fgpr::Reg().sessions.erase(sh);
    }
    std::shared_ptr<Runtime> rt = fgpr::RuntimeFor(rtHandle);
    if (!rt) return;   // the runtime's teardown destroys whatever is left
    Runtime* raw = rt.get();
    if (!rt->queue.Post([raw, sp] { DestroyInternal(*raw, sp); }))
    {
        // The queue already stopped (runtime teardown ran): stop the feeder here so its thread does not outlive us.
        sp->feedStop.store(true, std::memory_order_release);
        sp->Kick();
        if (sp->feeder.joinable()) sp->feeder.join();
    }
}

__declspec(dllexport) int32_t __stdcall FgPrSessionPlay(FgPrRuntime rtHandle, FgPrSession sh)
{
    return PostVerb(rtHandle, sh, [](Runtime& rt, const std::shared_ptr<Session>& sp) {
        Session& s = *sp;
        s.wantPlay = true;
        if (!IsLive(rt, s)) return;   // applied at attach
        HRESULT hr = rt.engine->Play();
        fgpr::RaiseLog(s.handle, "[transport] PLAY hr=" + fgpr::Hex(hr) + " posMs=" +
                                 std::to_string((long long)s.positionMs.load(std::memory_order_acquire)));
    });
}

__declspec(dllexport) int32_t __stdcall FgPrSessionPause(FgPrRuntime rtHandle, FgPrSession sh)
{
    return PostVerb(rtHandle, sh, [](Runtime& rt, const std::shared_ptr<Session>& sp) {
        Session& s = *sp;
        s.wantPlay = false;
        if (!IsLive(rt, s)) return;
        HRESULT hr = rt.engine->Pause();
        fgpr::RaiseLog(s.handle, "[transport] PAUSE hr=" + fgpr::Hex(hr) + " posMs=" +
                                 std::to_string((long long)s.positionMs.load(std::memory_order_acquire)));
    });
}

__declspec(dllexport) int32_t __stdcall FgPrSessionSeek(FgPrRuntime rtHandle, FgPrSession sh, int64_t targetMs, int32_t mode,
                                                        int64_t keyframeMs)
{
    if (mode != FgPrSeekMode_Exact && mode != FgPrSeekMode_Keyframe) return E_INVALIDARG;
    if (targetMs < 0) targetMs = 0;
    const int64_t postedQpc = fgpr::QpcNow();
    return PostVerb(rtHandle, sh, [targetMs, mode, keyframeMs, postedQpc](Runtime& rt, const std::shared_ptr<Session>& sp) {
        Session& s = *sp;
        const uint64_t seq = s.seekSeq.fetch_add(1, std::memory_order_acq_rel) + 1;   // latest-wins
        s.seekPostedQpc = postedQpc;
        const int64_t kf = keyframeMs >= 0 && keyframeMs <= targetMs ? keyframeMs : -1;
        {
            std::lock_guard<std::mutex> g(s.feedMx);
            s.seekTargetMs = targetMs;
            s.seekMode = mode;
            s.seekKeyframeMs = kf;
        }
        s.seeking.store(1, std::memory_order_release);
        Raise(s.handle, FgPrEvent_Seeking, targetMs);

        if (!IsLive(rt, s))
        {
            // Not on the engine: the seek MOVES the start position the next attach opens at, and the store fetches around
            // it. There is no engine seek to wait for, so it lands now.
            s.startPositionMs.store(targetMs, std::memory_order_release);
            {
                std::lock_guard<std::mutex> g(s.feedMx);
                s.seekNeedsFetch = false;
                if (s.source && !s.source->m_handedToEngine.load(std::memory_order_acquire))
                    s.source->SetStartPosition100ns((LONGLONG)targetMs * 10000LL);
                s.prefetchAroundMs = targetMs;
                if (s.prefetchSegments <= 0) s.prefetchSegments = 2;
            }
            s.positionMs.store(targetMs, std::memory_order_release);
            s.positionQpc.store(fgpr::QpcNow(), std::memory_order_release);
            s.seeking.store(0, std::memory_order_release);
            Raise(s.handle, FgPrEvent_Seeked, targetMs, fgpr::MsSinceQpc(postedQpc));
            s.Kick();
            return;
        }

        if (!s.metadataSeen)
        {
            // Attached but not loaded yet: the source's first Start (and the CANPLAY fallback correction) must land on the
            // NEW target, not on the start position the attach carried.
            s.startPositionMs.store(targetMs, std::memory_order_release);
            std::lock_guard<std::mutex> g(s.feedMx);
            if (s.source) s.source->SetStartPosition100ns((LONGLONG)targetMs * 10000LL);
        }

        // The gate, evaluated against the CURRENT buffer (never a range sampled when the seek was issued). If the target
        // already lies in both tracks' buffers the engine is repositioned NOW with no download at all — video from a
        // keyframe at or before the target, audio from any sample, with contiguous coverage past it.
        bool buffered = false;
        std::shared_ptr<fgpr::HttpFetch> cancelVideo, cancelAudio;
        int32_t targetSegment = SegmentOfUnclamped(s, kf >= 0 ? kf : targetMs);
        {
            std::lock_guard<std::mutex> g(s.feedMx);
            auto video = VideoStreamLocked(s);
            auto audio = s.haveAudio ? AudioStreamLocked(s) : nullptr;
            buffered = video && video->CanSeekTo(kf >= 0 ? kf : targetMs, true) && video->CanSeekTo(targetMs, false) &&
                       (!audio || audio->CanSeekTo(targetMs, false));
            if (buffered)
            {
                s.seekNeedsFetch = false;
            }
            else
            {
                // Register the target as the feeder's NEXT fetch. The GET on the wire is cancelled with its token —
                // unless it already IS the target segment, which a rapid scrub over one segment would otherwise cancel
                // over and over without ever letting it land.
                s.seekNeedsFetch = true;
                if (s.inflightVideoIndex != targetSegment && s.inflightVideoIndex != -2)
                {
                    cancelVideo = s.inflightVideo;
                    cancelAudio = s.inflightAudio;
                }
                if (video) video->MarkIncomplete();
                if (audio) audio->MarkIncomplete();
                // FLUSH, in this same critical section (the feeder plans under feedMx, so it never sees a half-flushed
                // store): the target segment must land in an EMPTY buffer. Left in place, the run inserted behind the
                // delivery cursor was erased by the very append that landed it, and Start() fell back to sample 0.
                // Lock order feedMx -> stream m_mx, as everywhere else.
                s.flushSeq = seq;
                if (video) video->FlushForSeek();
                if (audio) audio->FlushForSeek();
            }
        }
        if (buffered)
        {
            int64_t bufStart = 0, bufEnd = 0;
            {
                std::lock_guard<std::mutex> g(s.feedMx);
                auto video = VideoStreamLocked(s);
                if (video) video->BufferedRangeMs(bufStart, bufEnd);
            }
            fgpr::RaiseLog(s.handle, "[cenc-feed] seek ms=" + std::to_string((long long)targetMs) +
                                     " served from buffer [" + std::to_string((long long)bufStart) + ".." +
                                     std::to_string((long long)bufEnd) + "]ms - no fetch (seq=" + std::to_string(seq) + ")");
            ApplySeekToEngine(rt, s);
            return;
        }
        if (cancelVideo) cancelVideo->Cancel();
        if (cancelAudio) cancelAudio->Cancel();
        fgpr::RaiseLog(s.handle, "[cenc-feed] seek prefetch requested ms=" + std::to_string((long long)targetMs) +
                                 " at segment index " + std::to_string(targetSegment) + " (seq=" + std::to_string(seq) + ")");
        s.Kick();
    });
}

__declspec(dllexport) int32_t __stdcall FgPrSessionSetVolume(FgPrRuntime rtHandle, FgPrSession sh, double volume)
{
    if (!std::isfinite(volume)) return E_INVALIDARG;
    volume = std::clamp(volume, 0.0, 1.0);
    const int64_t micro = (int64_t)(volume * 1000000.0);
    return PostVerb(rtHandle, sh, [micro](Runtime& rt, const std::shared_ptr<Session>& sp) {
        sp->volumeMicro.store(micro, std::memory_order_release);
        if (!IsLive(rt, *sp)) return;
        HRESULT hr = rt.engine->SetVolume((double)micro / 1000000.0);
        if (FAILED(hr)) fgpr::RaiseLog(sp->handle, "[transport] VOLUME hr=" + fgpr::Hex(hr));
    });
}

__declspec(dllexport) int32_t __stdcall FgPrSessionSetRate(FgPrRuntime rtHandle, FgPrSession sh, double rate)
{
    if (!std::isfinite(rate) || rate <= 0.0) return E_INVALIDARG;
    const int64_t micro = (int64_t)(rate * 1000000.0);
    return PostVerb(rtHandle, sh, [micro](Runtime& rt, const std::shared_ptr<Session>& sp) {
        sp->rateMicro.store(micro, std::memory_order_release);
        if (!IsLive(rt, *sp)) return;
        // Default first, as Chromium's SetPlaybackRate does: a rate set while the topology is still loading (or before a
        // pause/resume) reverts to the DEFAULT rate when the engine finishes, so the default must carry it too.
        const HRESULT hd = rt.engine->SetDefaultPlaybackRate((double)micro / 1000000.0);
        if (FAILED(hd)) fgpr::RaiseLog(sp->handle, "[transport] DEFAULT RATE hr=" + fgpr::Hex(hd));
        HRESULT hr = rt.engine->SetPlaybackRate((double)micro / 1000000.0);
        if (FAILED(hr)) fgpr::RaiseLog(sp->handle, "[transport] RATE hr=" + fgpr::Hex(hr));
    });
}

__declspec(dllexport) int32_t __stdcall FgPrSessionSetStreamSize(FgPrRuntime rtHandle, FgPrSession sh, int32_t width,
                                                                 int32_t height)
{
    if (width < 0 || height < 0) return E_INVALIDARG;
    return PostVerb(rtHandle, sh, [width, height](Runtime& rt, const std::shared_ptr<Session>& sp) {
        Session& s = *sp;
        s.streamWidth = width > 0 && height > 0 ? width : 0;
        s.streamHeight = width > 0 && height > 0 ? height : 0;
        if (!IsLive(rt, s) || !s.metadataSeen) return;   // applied by the handle publication after LOADEDMETADATA
        DWORD nvw = 0, nvh = 0;
        rt.engine->GetNativeVideoSize(&nvw, &nvh);
        RECT dst = { 0, 0, (LONG)(s.streamWidth ? (DWORD)s.streamWidth : nvw), (LONG)(s.streamHeight ? (DWORD)s.streamHeight : nvh) };
        if (dst.right <= 0 || dst.bottom <= 0) return;
        MFARGB border = { 0, 0, 0, 255 };
        HRESULT hr = rt.engineEx->UpdateVideoStream(nullptr, &dst, &border);
        if (SUCCEEDED(hr))
        {
            // The compositor scales the swap chain's CURRENT frame by the new size's factor, but that frame was rendered at the
            // old size: unless the source is playing (its next frame is rendered at the new size anyway), repaint so the frame
            // DirectComposition scales is one MF rendered for this destination (the clear path's StreamRect + Repaint).
            if (s.state.load(std::memory_order_acquire) != FgPrState_Playing)
                rt.engineEx->UpdateVideoStream(nullptr, nullptr, nullptr);
            // Echo AFTER the repaint was asked for: the managed side publishes the new content size on seeing this.
            s.appliedStreamW.store((int32_t)dst.right, std::memory_order_release);
            s.appliedStreamH.store((int32_t)dst.bottom, std::memory_order_release);
            s.frameWatch.Restart(TickMs());   // F066
        }
        // Rate-limited (one line a second; a failure always): the size used to log once per change, and a resize gesture
        // changed it every layout - a cross-thread log event per frame.
        const int64_t now = fgpr::QpcNow();
        if (FAILED(hr) || s.lastStreamLogQpc == 0 || now - s.lastStreamLogQpc >= fgpr::QpcFrequency())
        {
            s.lastStreamLogQpc = now;
            fgpr::RaiseLog(s.handle, "[cenc] stream size " + std::to_string(dst.right) + "x" + std::to_string(dst.bottom) +
                                     (s.streamWidth ? "" : " (natural)") + " hr=" + fgpr::Hex(hr));
        }
    });
}

__declspec(dllexport) int32_t __stdcall FgPrSessionSelectRepresentation(FgPrRuntime rtHandle, FgPrSession sh, int32_t index,
                                                                        const wchar_t* initUrl, const wchar_t* base,
                                                                        const wchar_t* prefix, const wchar_t* suffix,
                                                                        int32_t retainMs)
{
    if (!initUrl || !*initUrl) return E_INVALIDARG;
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return E_HANDLE;
    {
        std::lock_guard<std::mutex> g(sp->feedMx);
        sp->rep.pending = true;
        sp->rep.index = index;
        sp->rep.retainMs = retainMs < 0 ? -1 : std::min<int32_t>(retainMs, 600000);
        sp->rep.initUrl = initUrl;
        sp->rep.base = CopyW(base);
        sp->rep.prefix = CopyW(prefix);
        sp->rep.suffix = suffix && *suffix ? std::wstring(suffix) : std::wstring(L".m4s");
    }
    sp->Kick();
    return S_OK;
}

__declspec(dllexport) int32_t __stdcall FgPrSessionSnapshot(FgPrRuntime rtHandle, FgPrSession sh, FgPrSnapshot* out)
{
    if (!out) return E_POINTER;
    if (out->structSize != sizeof(FgPrSnapshot)) return E_INVALIDARG;
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return E_HANDLE;
    const Session& s = *sp;
    out->state = s.state.load(std::memory_order_acquire);
    out->errorHr = s.errorHr.load(std::memory_order_acquire);
    out->readyState = s.readyState.load(std::memory_order_acquire);
    out->handle = s.swapchainHandle.load(std::memory_order_acquire);
    out->width = s.width.load(std::memory_order_acquire);
    out->height = s.height.load(std::memory_order_acquire);
    out->seeking = s.seeking.load(std::memory_order_acquire);
    out->activeRepresentation = s.store ? s.store->displayedRepresentation.load(std::memory_order_acquire) : -1;
    out->downloadingRepresentation = s.store ? s.store->downloadingRepresentation.load(std::memory_order_acquire) : -1;
    out->positionMs = s.positionMs.load(std::memory_order_acquire);
    out->positionQpc = s.positionQpc.load(std::memory_order_acquire);
    out->durationMs = s.durationMs.load(std::memory_order_acquire);
    out->bufferedAheadMs = s.bufferedAheadMs.load(std::memory_order_acquire);
    out->retainedBehindMs = s.retainedBehindMs.load(std::memory_order_acquire);
    out->firstFrameQpc = s.firstFrameQpc.load(std::memory_order_acquire);
    out->bytesDownloaded = s.bytesDownloaded.load(std::memory_order_acquire);
    out->downloadElapsedMs = s.downloadElapsedMs.load(std::memory_order_acquire);
    out->storeBytes = s.store ? s.store->Bytes() : 0;
    out->streamWidth = s.appliedStreamW.load(std::memory_order_acquire);
    out->streamHeight = s.appliedStreamH.load(std::memory_order_acquire);
    out->framesRendered = s.framesRendered.load(std::memory_order_acquire);
    out->framesDropped = s.framesDropped.load(std::memory_order_acquire);
    return S_OK;
}

__declspec(dllexport) int32_t __stdcall FgPrSessionGetKeyframes(FgPrRuntime rtHandle, FgPrSession sh, int64_t* out, int32_t cap)
{
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return E_HANDLE;
    if (cap < 0 || (cap > 0 && !out)) return E_INVALIDARG;
    return sp->store->keyframes.CopyTo(out, cap);
}

__declspec(dllexport) int32_t __stdcall FgPrSessionGetBuffered(FgPrRuntime rtHandle, FgPrSession sh, int64_t* outPairs,
                                                               int32_t capPairs)
{
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return E_HANDLE;
    if (capPairs < 0 || (capPairs > 0 && !outPairs)) return E_INVALIDARG;
    winrt::com_ptr<CencMediaStream> video;
    {
        std::lock_guard<std::mutex> g(sp->feedMx);
        video = VideoStreamLocked(*sp);
    }
    return video ? video->BufferedPairs(outPairs, capPairs) : 0;
}

/// The PlayReady `pssh` box out of the init's concatenated pssh boxes; the whole concatenation when no box carries the
/// PlayReady system id (it is what GenerateRequest("cenc") was always given, and a CDM accepts a multi-box init).
static std::vector<uint8_t> PlayReadyPsshOf(const std::vector<uint8_t>& all)
{
    static const uint8_t prSystemId[16] = { 0x9A,0x04,0xF0,0x79,0x98,0x40,0x42,0x86,0xAB,0x92,0xE6,0x5B,0xE0,0x88,0x5F,0x95 };
    size_t off = 0;
    while (off + 28 <= all.size())
    {
        const uint32_t size = cenc::rd32(&all[off]);
        if (size < 28 || off + size > all.size()) break;
        if (cenc::rd32(&all[off + 4]) == cenc::fourcc("pssh") && memcmp(&all[off + 12], prSystemId, 16) == 0)
            return std::vector<uint8_t>(all.begin() + (ptrdiff_t)off, all.begin() + (ptrdiff_t)(off + size));
        off += size;
    }
    return all;
}

__declspec(dllexport) int32_t __stdcall FgPrSessionGetInitProtection(FgPrRuntime rtHandle, FgPrSession sh, uint8_t* psshOut,
                                                                     int32_t psshCap, wchar_t* kidOut, int32_t kidCap)
{
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return E_HANDLE;
    if (psshCap < 0 || kidCap < 0) return E_INVALIDARG;
    if (kidOut && kidCap > 0) kidOut[0] = L'\0';
    if (!sp->initsLoaded.load(std::memory_order_acquire)) return 0;
    std::vector<uint8_t> pssh;
    uint8_t kid[16] = {};
    {
        std::lock_guard<std::mutex> g(sp->feedMx);
        pssh = PlayReadyPsshOf(sp->videoInfo.pssh);
        memcpy(kid, sp->videoInfo.kid, 16);
    }
    if (psshOut && psshCap > 0 && !pssh.empty())
        memcpy(psshOut, pssh.data(), std::min<size_t>((size_t)psshCap, pssh.size()));
    if (kidOut && kidCap >= 33)
    {
        static const uint8_t zero[16] = {};
        if (memcmp(kid, zero, 16) != 0)
        {
            const std::wstring hex = fgpr::KidToHex(kid);
            memcpy(kidOut, hex.c_str(), 33 * sizeof(wchar_t));   // 32 chars + the NUL
        }
    }
    return (int32_t)pssh.size();
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The demuxer gate.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ParseInit + ParseMoof over a LOCAL self-contained fragmented MP4 — the init `moov` followed by `moof`/`mdat` runs in
// the SAME file (or a DASH init segment with its media segments concatenated behind it). No CDM, no D3D device, no
// network, no runtime: nothing below touches Media Foundation. The runtime's own demuxer code answers:
//   * the track is ONE trak — the first usable video track, else the first usable audio track (a muxed file's tracks are
//     never merged), and each moof contributes that track's traf only, matched by track_ID;
//   * every moof is parsed against the WHOLE file, so a data offset means what the file says (moof-relative, or a
//     tfhd base_data_offset from the file's first byte) rather than what a sliced copy happens to make it mean;
//   * keyframe times are PRESENTATION times on the edit-list-corrected timeline (the manifest's), ascending;
//   * `encrypted` is the track's tenc protection, and the subsample count comes from senc or saiz/saio.
// A video track whose AVC NAL length field is not 4 bytes is REFUSED with the value reported, before any sample is
// converted.

__declspec(dllexport) int32_t __stdcall FgPrProbeFile(const wchar_t* path, int64_t* outKeyframes, int32_t cap,
                                                      FgPrProbeResult* out)
{
    if (!path || !out) return E_POINTER;
    if (out->structSize != sizeof(FgPrProbeResult)) return E_INVALIDARG;
    if (cap < 0 || (cap > 0 && !outKeyframes)) return E_INVALIDARG;
    const uint32_t structSize = out->structSize;
    memset(out, 0, sizeof(FgPrProbeResult));
    out->structSize = structSize;

    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size)) { const DWORD e = GetLastError(); CloseHandle(file); return HRESULT_FROM_WIN32(e); }
    if (size.QuadPart <= 0 || size.QuadPart > (1LL << 30)) { CloseHandle(file); return E_INVALIDARG; }   // a gate fixture, not a movie
    std::vector<uint8_t> data;
    try { data.resize((size_t)size.QuadPart); }
    catch (...) { CloseHandle(file); return E_OUTOFMEMORY; }
    size_t got = 0;
    while (got < data.size())
    {
        DWORD chunk = 0;
        const DWORD want = (DWORD)std::min<size_t>(data.size() - got, 1u << 24);
        if (!ReadFile(file, data.data() + got, want, &chunk, nullptr) || chunk == 0) break;
        got += chunk;
    }
    CloseHandle(file);
    if (got != data.size()) return HRESULT_FROM_WIN32(ERROR_READ_FAULT);

    cenc::InitInfo info;
    if (!cenc::ParseInit(data, info, cenc::TrackPick::Any)) return MF_E_INVALIDMEDIATYPE;
    const bool video = info.kind == cenc::TrackKind::Video;
    out->width = (int32_t)info.width;
    out->height = (int32_t)info.height;
    out->nalLengthSize = video ? (int32_t)info.nalLenSize : 0;
    out->encrypted = info.encrypted ? 1 : 0;
    if (video && info.nalLenSize != 4) return MF_E_INVALIDMEDIATYPE;   // reported, refused

    std::vector<cenc::Sample> samples;
    std::vector<int64_t> keyframes;
    uint64_t ticks = 0, endTicks = 0;
    int64_t sampleCount = 0, subsampleCount = 0;
    cenc::ForEachBox(data.data(), data.size(), [&](const cenc::Box& b) {
        if (b.type != cenc::fourcc("moof")) return;
        samples.clear();
        const int produced = cenc::ParseMoof(data.data(), data.size(), b, info, samples, ticks, video ? &keyframes : nullptr);
        if (produced <= 0) return;
        sampleCount += produced;
        for (auto const& smp : samples)
        {
            subsampleCount += (int64_t)smp.subsamples.size();
            if (!smp.subsamples.empty()) out->encrypted = 1;
            const uint64_t e = smp.timeTicks + smp.durTicks;
            if (e > endTicks) endTicks = e;
        }
    });

    std::sort(keyframes.begin(), keyframes.end());
    keyframes.erase(std::unique(keyframes.begin(), keyframes.end()), keyframes.end());
    const int32_t n = (int32_t)std::min<size_t>(keyframes.size(), (size_t)INT_MAX);
    if (outKeyframes && cap > 0) memcpy(outKeyframes, keyframes.data(), (size_t)std::min(n, cap) * sizeof(int64_t));
    out->keyframeCount = n;
    out->sampleCount = (int32_t)std::min<int64_t>(sampleCount, INT_MAX);
    out->subsampleCount = (int32_t)std::min<int64_t>(subsampleCount, INT_MAX);
    out->durationMs = info.timescale ? (int64_t)((endTicks * 1000ULL) / info.timescale) : 0;
    return S_OK;
}
