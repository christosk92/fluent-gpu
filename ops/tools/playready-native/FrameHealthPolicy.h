// FrameHealthPolicy.h - the three pieces of rendered-frame health and swap-chain handle ownership that need no Windows header,
// so tests/FeedTests.cpp can exercise them. Each mirrors a managed twin the clear engine runs (VideoEngineSeam.cs's
// VideoFrameCounters / RenderedFrameWatch, SwapchainHandleLedger.cs), so both backends apply the same rules.
//
// WHY (F066). Nothing could tell whether the media engine was rendering at all: a black or frozen surface (a lost device, a dead
// swap-chain handle, a stuck topology) looked exactly like healthy playback, and the dropped-frame count the player seam
// exposes was never fed. IMFMediaEngineEx::GetStatistics(FRAMES_RENDERED / FRAMES_DROPPED) is polled while the source plays
// (Chromium: every 500 ms), the counters are accumulated across the resets MF applies after a flush (Firefox), and a source that
// is playing with NO frame rendered within kNoFrameTimeoutMs is reported as a hang (Chromium's CheckRenderedVideoFrame).
//
// WHY (F198). IMFMediaEngineEx::GetVideoSwapchainHandle returns a FRESH NT handle on every call and the caller closes it
// (the MS sample's wil::unique_handle, Chromium's DuplicateHandle(DUPLICATE_CLOSE_SOURCE), Firefox's CloseHandle). The managed
// presenter's CreateSurfaceFromHandle takes its OWN reference and never owns the value, so every handle published and then
// replaced was a leaked kernel handle (and the composition surface behind it). The runtime owns them; a superseded handle is
// closed only after kHandleGraceMs, once the render thread has had every chance to bind its successor.
#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace fgpr::health {

/// How often the engine's FRAMES_RENDERED / FRAMES_DROPPED are read while a source plays (an in-proc property read).
constexpr int64_t kStatsPollMs = 500;

/// Chromium's kMinPlaybackTimeout: playing video with no rendered frame in this long is a hang, whatever the frame rate.
constexpr int64_t kNoFrameTimeoutMs = 10000;

/// How long a superseded swap-chain handle stays open before it is closed: several UI frames plus the managed registry's
/// bind retry backoff. The render thread binds the value it last read from the snapshot, some turns after it was published.
constexpr int64_t kHandleGraceMs = 2000;

/// The most retired handles kept open at once; beyond it the oldest is closed at once (a burst of format changes).
constexpr size_t kMaxRetiredHandles = 4;

/// HRESULT_FROM_WIN32(ERROR_TIMEOUT): what a rendered-frame hang is reported with (FgPrEvent_Error b).
constexpr int32_t kNoRenderedFrameHr = (int32_t)0x800705B4;

/// MF_MEDIA_ENGINE_ERR_DECODE: the error code (FgPrEvent_Error a) a rendered-frame hang is reported under.
constexpr int64_t kNoRenderedFrameCode = 3;

/// Accumulates the engine's rendered/dropped counters across the resets it applies: Media Foundation clears both after a
/// flush (a seek, a source reload), so a reading LOWER than the previous one means the engine restarted from zero and the
/// new reading IS the delta. Runtime thread only.
struct FrameCounters
{
    uint32_t lastRendered = 0, lastDropped = 0;
    int64_t rendered = 0, dropped = 0;

    void Reset() { *this = FrameCounters{}; }

    /// Start a new accumulation at the engine's CURRENT readings, counting nothing already on them (a warm engine reused for
    /// the next source may carry the previous source's totals until it resets them).
    void Rebase(uint32_t rawRendered, uint32_t rawDropped)
    {
        rendered = 0; dropped = 0;
        lastRendered = rawRendered; lastDropped = rawDropped;
    }

    void Observe(uint32_t rawRendered, uint32_t rawDropped)
    {
        if (rawRendered < lastRendered || rawDropped < lastDropped) { lastRendered = 0; lastDropped = 0; }
        rendered += (int64_t)(uint32_t)(rawRendered - lastRendered);
        dropped += (int64_t)(uint32_t)(rawDropped - lastDropped);
        lastRendered = rawRendered; lastDropped = rawDropped;
    }
};

/// The hang check. Only the FIRST frame is watched: once any frame has rendered the watch is over, so a legitimately static
/// picture never trips it. `playing` is true only while the source is playing video (not paused, seeking, starved or failed):
/// time outside it never counts and the window restarts when it resumes. Runtime thread only.
struct RenderedFrameWatch
{
    int64_t sinceMs = 0;
    bool counting = false, done = false;

    void Reset() { *this = RenderedFrameWatch{}; }

    /// The stream was re-sized or re-created: a swap chain that never presents again gets a fresh window from here.
    void Restart(int64_t nowMs) { if (counting) sinceMs = nowMs; }

    /// True exactly once: the turn the window elapsed with still no rendered frame.
    bool Observe(int64_t nowMs, bool playing, int64_t rendered, int64_t timeoutMs = kNoFrameTimeoutMs)
    {
        if (done) return false;
        if (rendered > 0) { done = true; counting = false; return false; }
        if (!playing) { counting = false; return false; }
        if (!counting) { counting = true; sinceMs = nowMs; return false; }
        if (nowMs - sinceMs < timeoutMs) return false;
        done = true; counting = false;
        return true;
    }
};

/// The swap-chain handles the runtime owns that no session publishes any more. A session's CURRENT handle is its published
/// FgPrSnapshot.handle (open for as long as it is published: a device recovery binds it again); when it is replaced or the
/// session detaches it is retired here and closed once it has waited kHandleGraceMs (or sooner only when more than
/// kMaxRetiredHandles are waiting). Runtime thread only; `close` is CloseHandle in production and a recorder in the tests.
class RetiredHandles
{
  public:
    template <typename Close>
    void Retire(uint64_t handle, int64_t nowMs, Close&& close)
    {
        if (handle == 0) return;
        m_list.push_back(Entry{ handle, nowMs });
        while (m_list.size() > kMaxRetiredHandles)
        {
            const uint64_t oldest = m_list.front().handle;
            m_list.erase(m_list.begin());
            close(oldest);
        }
    }

    /// MF handed back the identical handle value of one that is waiting here: it was never a different handle, so it is
    /// current again and must not be closed. True when it was waiting.
    bool Reinstate(uint64_t handle)
    {
        for (size_t i = 0; i < m_list.size(); ++i)
            if (m_list[i].handle == handle) { m_list.erase(m_list.begin() + (ptrdiff_t)i); return true; }
        return false;
    }

    template <typename Close>
    void Sweep(int64_t nowMs, Close&& close)
    {
        size_t keep = 0;
        for (size_t i = 0; i < m_list.size(); ++i)
        {
            if (nowMs - m_list[i].atMs >= kHandleGraceMs) close(m_list[i].handle);
            else m_list[keep++] = m_list[i];
        }
        m_list.resize(keep);
    }

    template <typename Close>
    void CloseAll(Close&& close)
    {
        std::vector<Entry> all;
        all.swap(m_list);
        for (const Entry& e : all) close(e.handle);
    }

    size_t Count() const { return m_list.size(); }

  private:
    struct Entry { uint64_t handle; int64_t atMs; };
    std::vector<Entry> m_list;
};

}   // namespace fgpr::health
