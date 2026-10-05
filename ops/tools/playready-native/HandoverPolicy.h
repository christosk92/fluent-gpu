// HandoverPolicy.h — the two pieces of the session hand-over that need no Windows header, so tests/FeedTests.cpp can
// exercise them: the deferred unload of the engine's source, and the reaper that joins stopped feeder threads.
//
// WHY. One media engine is shared by every session. A source switch used to be Detach (Pause + an EMPTY SetSource, an
// unload of the whole topology) immediately followed, a couple of UI frames later, by the successor's Attach (SetSource
// of the real source): two loads back to back, the first one's asynchronous notifications landing after the second, and
// the "an attach replaces the source" fast path never running. A detach now only pauses; the unload is armed here and
// runs after kReleaseGraceMs unless an attach takes the engine over first, in which case the successor's own SetSource
// is the only load. Destroy used to join the feeder thread on the runtime thread, so one slow GET stalled every engine
// event queued behind it; the join now happens on a reaper thread, with a join point at runtime teardown.
#pragma once

#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

namespace fgpr::handover {

/// How long a detached session's paused source stays loaded, waiting for a successor's attach to replace it. The managed
/// switch posts Detach, Destroy and the successor's Attach across at least two UI frames (about 33 ms), so a quarter of a
/// second covers it with room for a slow frame, and an unload nobody takes over still happens promptly.
constexpr int32_t kReleaseGraceMs = 250;

/// The runtime thread's record of an armed unload. Every member is touched on the runtime thread only. `Arm` returns the
/// token the timer item carries; `Cancel` (an attach took the engine over) and a later `Arm` both make an older token
/// stale, so a timer item that fires after either does nothing.
struct ReleaseGate
{
    uint64_t seq = 0;
    bool pending = false;

    uint64_t Arm() { pending = true; return ++seq; }
    void Cancel() { pending = false; }

    /// True exactly once per armed unload: when the item carrying `token` fires while it is still the armed one.
    bool Fire(uint64_t token)
    {
        if (!pending || token != seq) return false;
        pending = false;
        return true;
    }
};

/// Joins feeder threads OFF the runtime thread. `Reap` takes a thread that has already been told to stop (its stop flag
/// set, its GET cancelled, its wake-up kicked) and returns at once; one background thread joins it and then runs `tail`,
/// the part of the session teardown that must not overlap the feeder. `Drain` is the join point for runtime teardown
/// (nothing may still be inside Media Foundation when it is shut down) and `Shutdown` for FgPrRuntimeDestroy (no thread of
/// ours may outlive the DLL). After `Shutdown` a `Reap` joins inline on the caller, so no thread is ever left behind.
class FeederReaper
{
  public:
    FeederReaper() = default;
    FeederReaper(const FeederReaper&) = delete;
    FeederReaper& operator=(const FeederReaper&) = delete;
    ~FeederReaper() { Shutdown(); }

    void Reap(std::thread t, std::function<void()> tail)
    {
        if (!t.joinable()) { RunTail(tail); return; }
        if (t.get_id() == std::this_thread::get_id())
        {
            t.detach();   // a thread cannot join itself: it ends as soon as its own loop returns
            RunTail(tail);
            return;
        }
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (!m_closed)
            {
                try
                {
                    m_jobs.reserve(m_jobs.size() + 1);   // the push below cannot throw once the thread is inside the job
                    if (!m_thread.joinable()) m_thread = std::thread([this] { Run(); });
                    m_jobs.push_back(Job{ std::move(t), std::move(tail) });
                    m_cv.notify_one();
                    return;
                }
                catch (...) {}   // no thread / no memory: fall through to the inline join (t and tail are untouched)
            }
        }
        t.join();
        RunTail(tail);
    }

    /// Block until every thread handed over so far has been joined and its tail has run.
    void Drain()
    {
        std::unique_lock<std::mutex> lk(m_mx);
        m_idle.wait(lk, [this] { return m_jobs.empty() && !m_busy; });
    }

    /// Drain, then end the reaper thread. Idempotent.
    void Shutdown()
    {
        std::thread t;
        {
            std::lock_guard<std::mutex> g(m_mx);
            m_closed = true;
            t = std::move(m_thread);
        }
        m_cv.notify_all();
        if (t.joinable()) t.join();   // Run empties the list before it returns
    }

  private:
    struct Job
    {
        std::thread thread;
        std::function<void()> tail;
    };

    static void RunTail(const std::function<void()>& tail)
    {
        if (!tail) return;
        try { tail(); } catch (...) {}
    }

    void Run()
    {
        for (;;)
        {
            std::vector<Job> batch;
            {
                std::unique_lock<std::mutex> lk(m_mx);
                m_cv.wait(lk, [this] { return m_closed || !m_jobs.empty(); });
                if (m_jobs.empty()) return;   // closed and drained
                batch.swap(m_jobs);
                m_busy = true;
            }
            for (auto& j : batch)
            {
                j.thread.join();
                RunTail(j.tail);
            }
            {
                std::lock_guard<std::mutex> g(m_mx);
                m_busy = false;
            }
            m_idle.notify_all();
        }
    }

    std::mutex m_mx;
    std::condition_variable m_cv;     // work arrived / closed
    std::condition_variable m_idle;   // the list is empty and nothing is being joined
    std::vector<Job> m_jobs;
    std::thread m_thread;
    bool m_busy = false;
    bool m_closed = false;
};

}   // namespace fgpr::handover
