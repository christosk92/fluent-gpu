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
    int32_t lastIdx[2] = { -1, -1 };       // the progress guard (see PlanTrack)
    int64_t lastCovEnd[2] = { -1, -1 };
    int32_t floorIdx[2] = { -1, -1 };
    uint64_t floorSeekSeq = 0;
    int32_t failIdx[2] = { -1, -1 };       // transient-failure back-off, per track
    int32_t failCount[2] = { 0, 0 };
    int64_t lastBytesQpc = 0;
    uint64_t lastBytesRaised = 0;
    int64_t lastAhead = -1, lastBehind = -1;
};

enum class JobResult { Idle, Progress, Backoff };

static constexpr int kVideo = 0, kAudio = 1;

/// The reference position the fetch plan is built around: a pending seek's target, else — once the session is
/// attached — the playhead (the carried start position until the first frame lands), else the prefetch window.
static bool ReferenceLocked(Session& s, int64_t& refMs, int64_t& wantEndMs, bool& seekPlan)
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
    if (s.streaming)
    {
        refMs = s.firstFrameQpc.load(std::memory_order_acquire) != 0
            ? s.positionMs.load(std::memory_order_acquire)
            : s.startPositionMs.load(std::memory_order_acquire);
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
    int32_t idx = -1;     // the segment to fetch, -1 for none
    bool atEnd = false;   // coverage from the reference runs to the end of the track
};

/// Which segment this track needs next: the one containing the reference when it is not covered, else the one just
/// past the covered range — until that range reaches `wantEndMs` or the end of the track.
///
/// The progress guard: a segment whose samples do not actually extend the covered range (a real hole in the content,
/// a segment that demuxed short) would be planned again forever. When the same index comes back with no growth, the
/// plan steps past it and remembers a floor until the next seek.
static TrackPlan PlanTrack(Session& s, FeederState& st, int track, CencMediaStream* stream, int64_t refMs,
                           int64_t wantEndMs, int32_t endIndex)
{
    TrackPlan p;
    if (!stream) return p;
    const Coverage cov = CoverageAt(stream, refMs);
    const int64_t len = s.SegLenMs();
    int32_t idx;
    if (cov.startMs < 0)
    {
        idx = SegmentOfUnclamped(s, refMs);
        // Already fetched, and it STILL does not cover the reference (a grid that does not match the content): asking
        // again cannot help. Nothing to do until the reference moves or a seek resets the guard.
        if (st.lastIdx[track] == idx && st.lastCovEnd[track] < 0) return p;
    }
    else
    {
        if (cov.endMs >= wantEndMs) return p;                        // satisfied
        idx = SegmentOfUnclamped(s, cov.endMs + len / 2);
        if (st.lastIdx[track] == idx && cov.endMs <= st.lastCovEnd[track]) st.floorIdx[track] = idx + 1;
        if (st.floorIdx[track] > idx) idx = st.floorIdx[track];
    }
    if (idx >= endIndex)
    {
        p.atEnd = true;
        return p;
    }
    if (cov.startMs >= 0 && SegmentStartMs(s, idx) >= wantEndMs) return p;
    p.idx = idx;
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
// kbit/s. `rep` is -1 before a representation has been selected.
static void LogAbrSegment(Session& s, const char* track, int rep, int seg, const fgpr::HttpFetchTiming& t, int64_t aheadMs)
{
    const uint64_t ms = t.transferMs ? t.transferMs : 1;
    fgpr::RaiseLog(s.handle, std::string("[cenc-abr] ") + track + " rep=" + std::to_string(rep) + " seg=" + std::to_string(seg) +
            " bytes=" + std::to_string(t.bytes) + " transferMs=" + std::to_string(t.transferMs) +
            " headerMs=" + std::to_string(t.headerMs) + " kbps=" + std::to_string((t.bytes * 8ULL) / ms) +
            " aheadMs=" + std::to_string((long long)aheadMs));
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
}

// ── init segments ─────────────────────────────────────────────────────────────────────────────────────────────────
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
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.inflightVideo = vf;
        s.inflightAudio = af;
        s.inflightVideoIndex = -2;   // init: a seek never cancels it (the seek needs it too)
    }
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
                                         const Session::RepresentationRequest& req)
{
    Session& s = *sp;
    auto clearIfCurrent = [&]() {
        std::lock_guard<std::mutex> g(s.feedMx);
        if (s.rep.pending && s.rep.index == req.index && s.rep.initUrl == req.initUrl) s.rep.pending = false;
    };

    auto initFetch = fgpr::HttpFetch::Begin(req.initUrl, s.headers, s.store);
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.inflightVideo = initFetch;
        s.inflightVideoIndex = -3;   // a seek cancels it; the switch then stays PENDING, not rejected
    }
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

    // THE SWITCH BOUNDARY — at or before the segment the PLAYHEAD is in, NEVER forward of it.
    // This used to clamp UP to `nextSegment`, the initial-burst count captured before playback
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
    int32_t boundary = video->Ahead() == 0
        ? cursor   // the buffer is drained — the feeder cursor IS the playhead
        : (int32_t)(((uint64_t)std::max<int64_t>(nextTimeMs, 0) + (uint64_t)len - 1) / (uint64_t)len);
    boundary = std::clamp(boundary, 0, cursor);

    const std::wstring url = SegmentUrl(req.base, req.prefix, req.suffix, s, boundary);
    auto segFetch = fgpr::HttpFetch::Begin(url, s.headers, s.store);
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.inflightVideo = segFetch;
        s.inflightVideoIndex = -3;
    }
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
    s.bytesDownloaded.fetch_add(segFetch->timing.bytes, std::memory_order_acq_rel);
    s.downloadElapsedMs.fetch_add(std::max<uint64_t>(1, segFetch->timing.transferMs), std::memory_order_acq_rel);

    if (more.empty() || !more[0].keyframe)
    {
        fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch rejected: target segment did not begin with a keyframe");
        clearIfCurrent();
        return JobResult::Progress;
    }
    st.ticks[kVideo] = ticks;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        if (!(s.rep.pending && s.rep.index == req.index && s.rep.initUrl == req.initUrl))
            return JobResult::Progress;   // superseded by a newer request while this one was on the wire
        s.initUrl = req.initUrl;
        s.segBase = req.base;
        s.segPrefix = req.prefix;
        s.segSuffix = req.suffix;
        s.videoInfo = nextInfo;
        s.rep.pending = false;
        auto current = VideoStreamLocked(s);
        if (current) current->SwitchVideoRepresentation(nextInfo, std::move(more));
    }
    for (int64_t kf : keyframes) s.store->keyframes.Append(kf);
    s.activeRepresentation.store(req.index, std::memory_order_release);
    fgpr::RaiseLog(s.handle, "[cenc-feed] quality switch at segment index " + std::to_string(boundary) +
                             " (playhead t=" + std::to_string((long long)nextTimeMs) + "ms, segment=" +
                             std::to_string((long long)len) + "ms, cursor was " + std::to_string(cursor) +
                             ") -> representation " + std::to_string(req.index));
    Raise(s.handle, FgPrEvent_Representation, req.index);
    LogAbrSegment(s, "video", req.index, boundary, segFetch->timing, video->AheadDurationMs());
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
    if (rep.pending) return RunRepresentationSwitch(sp, st, rep);

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
        if (!ReferenceLocked(s, refMs, wantEndMs, seekPlan)) return JobResult::Idle;
        video = VideoStreamLocked(s);
        audio = s.haveAudio ? AudioStreamLocked(s) : nullptr;
        if (!video) return JobResult::Idle;
        const uint64_t seq = s.seekSeq.load(std::memory_order_acquire);
        if (seq != st.floorSeekSeq)
        {
            for (int t = 0; t < 2; t++) { st.floorIdx[t] = -1; st.lastIdx[t] = -1; st.lastCovEnd[t] = -1; }
            st.floorSeekSeq = seq;
        }
        const int32_t count = EffectiveSegmentCount(s);
        const int64_t durMs = EffectiveDurationMs(s);
        if (durMs > 0 && wantEndMs > durMs) wantEndMs = durMs;
        vp = PlanTrack(s, st, kVideo, video.get(), refMs, wantEndMs, std::min(count, s.videoEndIndex));
        if (audio) ap = PlanTrack(s, st, kAudio, audio.get(), refMs, wantEndMs, std::min(count, s.audioEndIndex));
        // The byte cap: past the budget, fetch only what the reference position itself needs — never starve playback,
        // never keep buffering ahead into memory the session was not given. History is trimmed FIRST: the playhead
        // has moved since the last append, and what fell out of the time window no longer counts.
        video->TrimNow();
        if (audio) audio->TrimNow();
        if (!seekPlan && s.store->Bytes() >= s.store->budgetBytes)
        {
            if (vp.idx >= 0 && vp.idx != SegmentOfUnclamped(s, refMs)) vp.idx = -1;
            if (ap.idx >= 0 && ap.idx != SegmentOfUnclamped(s, refMs)) ap.idx = -1;
        }
        vBase = s.segBase; vPrefix = s.segPrefix; vSuffix = s.segSuffix;
        videoInfo = s.videoInfo;
        if (audio) audioInfo = s.audioInfo;
        seekSeq = seq;
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
        // Satisfied (or at the end): wake on the normal forward target. Not satisfied but unable to fetch (the byte
        // cap): wake again only once the playhead has consumed one more segment.
        const Coverage vc = CoverageAt(video.get(), refMs);
        const bool satisfied = vp.atEnd || (vc.startMs >= 0 && vc.endMs >= wantEndMs);
        const int64_t below = satisfied ? 0 : std::max<int64_t>(1, vc.aheadMs - s.SegLenMs());
        video->SetDemandBelowMs(below);
        if (audio) audio->SetDemandBelowMs(below);
        return JobResult::Idle;
    }

    // ── fetch: both GETs are begun before either is waited on ──
    std::shared_ptr<fgpr::HttpFetch> vf, af;
    if (vp.idx >= 0) vf = fgpr::HttpFetch::Begin(SegmentUrl(vBase, vPrefix, vSuffix, s, vp.idx), s.headers, s.store);
    if (ap.idx >= 0) af = fgpr::HttpFetch::Begin(SegmentUrl(s.audioSegBase, s.audioSegPrefix, s.audioSegSuffix, s, ap.idx), s.headers, s.store);
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.inflightVideo = vf;
        s.inflightAudio = af;
        s.inflightVideoIndex = vp.idx >= 0 ? vp.idx : ap.idx;
    }
    if (vf) vf->Wait();
    if (af) af->Wait();
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.inflightVideo = nullptr;
        s.inflightAudio = nullptr;
        s.inflightVideoIndex = -1;
    }
    if (s.feedStop.load(std::memory_order_acquire)) return JobResult::Idle;

    bool landed = false;
    int nextBackoff = 0;
    auto handleTrack = [&](int t, const std::shared_ptr<fgpr::HttpFetch>& f, int32_t idx, const cenc::InitInfo& info,
                           const winrt::com_ptr<CencMediaStream>& stream) {
        if (!f) return;
        if (f->cancelled) { f->ReleaseBody(); return; }   // a newer seek (or teardown): the plan is rebuilt
        if (!f->Ok())
        {
            f->ReleaseBody();
            const bool definitive = f->status >= 400 && f->status < 500;
            if (!definitive && (st.failIdx[t] != idx || st.failCount[t] < 3))
            {
                st.failCount[t] = st.failIdx[t] == idx ? st.failCount[t] + 1 : 1;
                st.failIdx[t] = idx;
                const int wait = 500 << (st.failCount[t] - 1);
                if (wait > nextBackoff) nextBackoff = wait;
                fgpr::RaiseLog(s.handle, std::string("[cenc-feed] ") + (t == kVideo ? "video" : "audio") + " seg#" +
                                         std::to_string(idx) + " failed (HTTP " + std::to_string(f->status) + ", hr=" +
                                         fgpr::Hex(f->hr) + ") - retry " + std::to_string(st.failCount[t]) + "/3 in " +
                                         std::to_string(wait) + "ms");
                return;
            }
            // A 4xx (or a third transient failure) ends the track at this index. Audio running out while video has
            // not ends the audio stream cleanly, so the presentation still ends on the video rather than leaving a
            // stream that can never satisfy another request.
            {
                std::lock_guard<std::mutex> g(s.feedMx);
                if (t == kVideo) s.videoEndIndex = std::min(s.videoEndIndex, idx);
                else s.audioEndIndex = std::min(s.audioEndIndex, idx);
            }
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
        s.bytesDownloaded.fetch_add(f->timing.bytes, std::memory_order_acq_rel);
        s.downloadElapsedMs.fetch_add(std::max<uint64_t>(1, f->timing.transferMs), std::memory_order_acq_rel);
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
        {
            std::lock_guard<std::mutex> g(s.feedMx);
            // Always the CURRENT source's stream: a re-attach may have rebuilt the source while this GET was in flight.
            auto current = t == kVideo ? VideoStreamLocked(s) : AudioStreamLocked(s);
            if (current) current->AppendSamples(std::move(more));
        }
        if (t == kVideo)
        {
            for (int64_t kf : keyframes) s.store->keyframes.Append(kf);
            if (!keyframes.empty()) Raise(s.handle, FgPrEvent_Keyframes, idx);
        }
        LogAbrSegment(s, t == kVideo ? "video" : "audio",
                      t == kVideo ? s.activeRepresentation.load(std::memory_order_acquire) : -1, idx, f->timing,
                      stream ? stream->AheadDurationMs() : 0);
        landed = true;
        st.lastIdx[t] = idx;
        st.lastCovEnd[t] = CoverageAt(stream.get(), refMs).endMs;
    };
    handleTrack(kVideo, vf, vp.idx, videoInfo, video);
    handleTrack(kAudio, af, ap.idx, audioInfo, audio);

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
    return JobResult::Progress;
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
        // The engine refused the reposition: resolve the managed waiter with where playback actually is.
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

/// Unload whatever source the engine holds (an empty SetSource). Only ever called when no successor's SetSource is
/// about to replace it: two loads back to back would let the empty load's asynchronous error land after the real one.
static void ReleaseEngineSource(Runtime& rt, uint64_t logHandle)
{
    if (!rt.engine || rt.engineSource == 0) return;
    BSTR empty = SysAllocString(L"");
    HRESULT hs = rt.engineEx->SetSource(empty);
    SysFreeString(empty);
    rt.engineSource = 0;
    fgpr::RaiseLog(logHandle, "[cenc] engine source released: SetSource(empty) hr=" + fgpr::Hex(hs));
}

/// `releaseSource` = false when an attach of ANOTHER session replaces this one: that session's own SetSource unloads
/// this source (a paused one stays loaded until it does), so nothing loads twice in a row.
static void DetachInternal(Runtime& rt, Session& s, bool releaseSource)
{
    const bool wasSetSource = !s.attachPending;
    rt.attached.store(0, std::memory_order_release);   // first: the detach's own PAUSE / source events are dropped
    if (wasSetSource && rt.engine)
    {
        if (s.firstFrameQpc.load(std::memory_order_acquire) != 0)
            s.startPositionMs.store(s.positionMs.load(std::memory_order_acquire), std::memory_order_release);   // a re-attach resumes here
        HRESULT hp = rt.engine->Pause();
        fgpr::RaiseLog(s.handle, "[cenc] detach: Pause hr=" + fgpr::Hex(hp) + (releaseSource ? "" : " (the successor's source replaces this one)"));
        if (releaseSource && rt.engineSource == s.handle) ReleaseEngineSource(rt, s.handle);
    }
    s.attachPending = false;
    s.metadataSeen = false;
    {
        std::lock_guard<std::mutex> g(s.feedMx);
        s.streaming = false;
    }
    if (s.license) { fgpr::LicenseBind(s.license, -1); s.license = 0; }
    s.swapchainHandle.store(0, std::memory_order_release);
    s.seeking.store(0, std::memory_order_release);
    s.firstFrameQpc.store(0, std::memory_order_release);
    if (s.state.load(std::memory_order_acquire) != FgPrState_Error) s.state.store(FgPrState_Stopped, std::memory_order_release);
    Raise(s.handle, FgPrEvent_Detached);
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
            CencAudioFeed feed;
            if (s.haveAudio)
            {
                feed.info = s.audioInfo;
                if (oldAudio) feed.samples = oldAudio->TakeSamples(audioBytes);
            }
            const int64_t totalMs = EffectiveDurationMs(s);
            auto fresh = BuildCencSource(s.videoInfo, std::move(videoSamples), s.haveAudio ? &feed : nullptr, true,
                                         totalMs > 0 ? (uint64_t)totalMs * 10000ULL : 0ULL, s.handle, s.store, 0);
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
        fgpr::RuntimeItaPreflight(rt);
        // Desktop PMP bridge: the protected source exposes the CDM's trusted input so Media Foundation can obtain
        // the per-stream ITA/decrypter inside Windows' protected process.
        // The proactive EME session already supplied the PSSH. Match Firefox's working desktop MFCDM path and let
        // the CDM associate that session with the trusted input; passing the same PSSH again creates a second content
        // binding whose ITA proxy is rejected during protected-topology negotiation on some PlayReady implementations.
        IMFTrustedInput* trustedInput = nullptr;
        HRESULT hr = rt.cdm->CreateTrustedInput(nullptr, 0, &trustedInput);
        fgpr::RaiseLog(s.handle, "[cenc] CreateTrustedInput hr=" + fgpr::Hex(hr));
        if (FAILED(hr) || !trustedInput)
        {
            s.errorHr.store(FAILED(hr) ? hr : E_NOINTERFACE, std::memory_order_release);
            s.state.store(FgPrState_Error, std::memory_order_release);
            Raise(s.handle, FgPrEvent_Error, 4 /*MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED*/, (int64_t)(int32_t)(FAILED(hr) ? hr : E_NOINTERFACE));
            DetachInternal(rt, s);
            return;
        }
        source->m_trustedInput.attach(trustedInput);
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
    Raise(s.handle, FgPrEvent_Attached, attachMs);

    const double volume = (double)s.volumeMicro.load(std::memory_order_acquire) / 1000000.0;
    const double rate = (double)s.rateMicro.load(std::memory_order_acquire) / 1000000.0;
    HRESULT hv = rt.engine->SetVolume(volume);
    if (FAILED(hv)) fgpr::RaiseLog(s.handle, "[transport] VOLUME hr=" + fgpr::Hex(hv));
    if (rate != 1.0)
    {
        rt.engine->SetDefaultPlaybackRate(rate);
        HRESULT hrt = rt.engine->SetPlaybackRate(rate);
        if (FAILED(hrt)) fgpr::RaiseLog(s.handle, "[transport] RATE hr=" + fgpr::Hex(hrt));
    }
    if (s.wantPlay)
    {
        HRESULT hp = rt.engine->Play();
        fgpr::RaiseLog(s.handle, "[transport] PLAY hr=" + fgpr::Hex(hp) + " (attach)");
    }
}

static void DestroyInternal(Runtime& rt, const std::shared_ptr<Session>& sp)
{
    Session& s = *sp;
    if (rt.attached.load(std::memory_order_acquire) == s.handle) DetachInternal(rt, s);
    // Replaced by an attach that is still waiting for its inits: the engine still holds THIS source (paused). Nothing live
    // needs it and the successor's SetSource has not happened, so unload it now rather than keep its samples resident.
    else if (rt.engineSource == s.handle && rt.attached.load(std::memory_order_acquire) == 0) ReleaseEngineSource(rt, s.handle);

    // Stop the feeder: flag, cancel whatever is on the wire (the token completes the wait at once), wake, join.
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
    if (s.feeder.joinable())
    {
        if (s.feeder.get_id() == std::this_thread::get_id()) s.feeder.detach();
        else s.feeder.join();
    }

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
    const uint64_t prev = s.swapchainHandle.exchange(value, std::memory_order_acq_rel);
    if (prev != value || reRaise)
    {
        fgpr::RaiseLog(s.handle, "[cenc] " + std::to_string(nvw) + "x" + std::to_string(nvh) +
                                 " swap-chain handle=" + std::to_string(value) + " after " +
                                 std::to_string(s.handleTries) + " not-ready quer" + (s.handleTries == 1 ? "y" : "ies"));
        Raise(s.handle, FgPrEvent_Handle, (int64_t)value);
    }
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
}

void fgpr::SessionOnCanPlay(Runtime& rt, Session& s)
{
    if (!IsLive(rt, s) || s.startCorrectionDone) return;
    s.startCorrectionDone = true;
    // Belt and braces for the carried start position (S1). The source rewrites its first Start to it and reports the
    // actual start time in MESourceStarted; should the engine's clock not have adopted that, the playhead reads far
    // behind the start by CANPLAY — before any frame is presented — and one native seek puts it right. Its SEEKED is
    // not reported as FgPrEvent_Seeked (nobody on the managed side issued it).
    const int64_t start = s.startPositionMs.load(std::memory_order_acquire);
    const double now = rt.engine->GetCurrentTime();
    const int64_t nowMs = std::isfinite(now) ? (int64_t)(now * 1000.0) : 0;
    if (start > 0 && nowMs + s.SegLenMs() < start)
    {
        s.internalSeeks++;
        HRESULT hr = rt.engine->SetCurrentTime((double)start / 1000.0);
        fgpr::RaiseLog(s.handle, "[cenc] start position correction: engine at " + std::to_string((long long)nowMs) +
                                 "ms, carried start " + std::to_string((long long)start) + "ms -> SetCurrentTime hr=" + fgpr::Hex(hr));
        if (FAILED(hr)) s.internalSeeks--;
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
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Exports.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// Post a verb for a live session. E_HANDLE for an unknown handle, MF_E_SHUTDOWN when the runtime is going away.
static int32_t PostVerb(FgPrRuntime rtHandle, FgPrSession sh, std::function<void(Runtime&, const std::shared_ptr<Session>&)> fn)
{
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return E_HANDLE;
    std::shared_ptr<Runtime> rt = fgpr::RuntimeFor(rtHandle);
    if (!rt) return E_HANDLE;
    Runtime* raw = rt.get();
    const bool posted = rt->queue.Post([raw, sp, fn = std::move(fn)] {
        if (!raw->Ready()) return;
        fn(*raw, sp);
    });
    return posted ? S_OK : MF_E_SHUTDOWN;
}

static std::wstring CopyW(const wchar_t* s) { return s ? std::wstring(s) : std::wstring(); }

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
    return PostVerb(rtHandle, sh, [lic, postedQpc](Runtime& rt, const std::shared_ptr<Session>& sp) {
        Session& s = *sp;
        const uint64_t prev = rt.attached.load(std::memory_order_acquire);
        if (prev == s.handle) return;   // already attached (or attaching)
        if (prev)
        {
            std::shared_ptr<Session> old = fgpr::SessionByHandle(prev);
            if (old) DetachInternal(rt, *old, /*releaseSource*/ false);   // this attach's SetSource replaces it
            else rt.attached.store(0, std::memory_order_release);
        }
        // lic == 0 is legal: clear content, or a key already usable through another session's license.
        s.license = lic;
        if (lic) fgpr::LicenseBind(lic, +1);
        rt.attached.store(s.handle, std::memory_order_release);
        s.attachPending = true;
        s.attachPostedQpc = postedQpc;
        s.metadataSeen = false;
        s.startCorrectionDone = false;
        s.internalSeeks = 0;
        s.handleTries = 0;
        s.swapchainHandle.store(0, std::memory_order_release);
        s.firstFrameQpc.store(0, std::memory_order_release);
        s.readyState.store(0, std::memory_order_release);
        s.errorHr.store(0, std::memory_order_release);
        s.state.store(FgPrState_Loading, std::memory_order_release);
        {
            std::lock_guard<std::mutex> g(s.feedMx);
            s.wantInits = true;
            s.streaming = true;
        }
        if (!s.initsLoaded.load(std::memory_order_acquire)) s.initHr.store(S_OK, std::memory_order_release);   // retry a failed init
        s.Kick();
        CompleteAttach(rt, sp);   // at once when the inits are already parsed; otherwise the feeder completes it
    });
}

__declspec(dllexport) int32_t __stdcall FgPrSessionDetach(FgPrRuntime rtHandle, FgPrSession sh)
{
    return PostVerb(rtHandle, sh, [](Runtime& rt, const std::shared_ptr<Session>& sp) {
        if (rt.attached.load(std::memory_order_acquire) != sp->handle) return;
        DetachInternal(rt, *sp);
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
            // Attached but not loaded yet: the source's first Start (and the CANPLAY correction) must land on the NEW
            // target, not on the start position the attach carried.
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
        fgpr::RaiseLog(s.handle, "[cenc] stream size " + std::to_string(dst.right) + "x" + std::to_string(dst.bottom) +
                                 (s.streamWidth ? "" : " (natural)") + " hr=" + fgpr::Hex(hr));
    });
}

__declspec(dllexport) int32_t __stdcall FgPrSessionSelectRepresentation(FgPrRuntime rtHandle, FgPrSession sh, int32_t index,
                                                                        const wchar_t* initUrl, const wchar_t* base,
                                                                        const wchar_t* prefix, const wchar_t* suffix)
{
    if (!initUrl || !*initUrl) return E_INVALIDARG;
    std::shared_ptr<Session> sp = fgpr::SessionFor(rtHandle, sh);
    if (!sp) return E_HANDLE;
    {
        std::lock_guard<std::mutex> g(sp->feedMx);
        sp->rep.pending = true;
        sp->rep.index = index;
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
    out->activeRepresentation = s.activeRepresentation.load(std::memory_order_acquire);
    out->positionMs = s.positionMs.load(std::memory_order_acquire);
    out->positionQpc = s.positionQpc.load(std::memory_order_acquire);
    out->durationMs = s.durationMs.load(std::memory_order_acquire);
    out->bufferedAheadMs = s.bufferedAheadMs.load(std::memory_order_acquire);
    out->retainedBehindMs = s.retainedBehindMs.load(std::memory_order_acquire);
    out->firstFrameQpc = s.firstFrameQpc.load(std::memory_order_acquire);
    out->bytesDownloaded = s.bytesDownloaded.load(std::memory_order_acquire);
    out->downloadElapsedMs = s.downloadElapsedMs.load(std::memory_order_acquire);
    out->storeBytes = s.store ? s.store->Bytes() : 0;
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
