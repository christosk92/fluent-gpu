// HandoverPolicy.h — the piece of the session hand-over that needs no Windows header: the reaper that joins stopped feeder
// threads.
//
// WHY. Destroy used to join the feeder thread on the runtime thread, so one slow GET stalled every engine event queued behind
// it; the join now happens on a reaper thread, with a join point at runtime teardown. (The deferred unload of the engine's
// source that lived here is gone with the unload itself: the runtime keeps ONE source for its life and each video is a program
// in it - PrSession.cpp "The pipeline".)
#pragma once

#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

namespace fgpr::handover {

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
