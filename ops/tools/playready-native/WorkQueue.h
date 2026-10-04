// WorkQueue.h — the runtime thread's event-driven queue, with two priority lanes. Standalone on purpose (std only, no
// Windows, no FgPlayReady.h): tests\FeedTests.cpp includes it directly.
//
// WHY TWO LANES. Every verb, every media-engine event and every licence step used to be one item on ONE FIFO that the
// runtime thread drains, and the licence steps (CreateSession, GenerateRequest, Update, Close) are PMP round trips into
// mfpmp.exe. A licence step for a prepared NEXT video that was queued just ahead of a user's switch made the switch's
// attach, LOADEDMETADATA, Handle and FIRSTFRAMEREADY items wait behind those round trips. Engine/transport work (attach,
// detach, seek, engine events) now goes on the Engine lane and drains ahead of the Maintenance lane (licence work of
// OTHER rows). The attaching or attached session's own licence is exempt: the attach verb starts it (StartAcquisition)
// before anything else, and its relay result runs on the Engine lane while a session binds the KID.
//
// STARVATION BOUND. Maintenance work is never starved: while it waits, at most kMaintenanceEveryN consecutive Engine
// items run before ONE Maintenance item does. Engine items are short (a verb or an event); the bound is by item count so
// it needs no clock and tests exactly.
//
// ORDER. Each lane is FIFO, so two verbs on a session keep their order. An item on the other lane has no order
// relative to it (a licence step may now run after an engine item posted later); nothing couples the two, because a
// session's attach waits for the key on its own (the licence state), not for queue position.
#pragma once

#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <utility>
#include <vector>

namespace fgpr {

enum class Lane { Engine, Maintenance };

/// Consecutive Engine items that may run while a Maintenance item waits.
constexpr int kMaintenanceEveryN = 16;

// A condition variable over two deques. The thread blocks INDEFINITELY when there is nothing to do: the only timed wait is
// the one a PostAfter item asks for, and it ends when that item runs (the engine-source release's 250 ms grace,
// PrSession.cpp). Position is sampled when the media engine itself says time moved (MF_MEDIA_ENGINE_EVENT_TIMEUPDATE,
// PrRuntime.cpp), which is what replaces the old 80 ms keep-alive that re-asserted transport and re-presented frames.
class WorkQueue
{
  public:
    /// Called inside the catch block of an item that threw (it may `throw;` to classify the exception).
    using ThrowHandler = void (*)();

    bool Post(std::function<void()> fn, Lane lane = Lane::Engine)
    {
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_stopped) return false;
            (lane == Lane::Engine ? m_engine : m_maintenance).push_back(std::move(fn));
        }
        m_cv.notify_one();
        return true;
    }

    /// Run `fn` on the queue's thread no sooner than `delayMs` from now, behind whatever is queued by then (Engine lane:
    /// the delayed item is the engine-source release). An item still waiting when the queue stops is dropped (runtime
    /// teardown releases everything an item could have).
    bool PostAfter(int32_t delayMs, std::function<void()> fn)
    {
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_stopped) return false;
            m_delayed.push_back(Delayed{ std::chrono::steady_clock::now() + std::chrono::milliseconds(delayMs), std::move(fn) });
        }
        m_cv.notify_one();
        return true;
    }

    /// Block until work arrives and run everything queued, Engine lane first. False once stopped AND drained.
    bool RunOnce(ThrowHandler onThrow = nullptr)
    {
        {
            std::unique_lock<std::mutex> lk(m_mx);
            for (;;)
            {
                const auto next = PromoteDueLocked();
                if (m_stopped || !m_engine.empty() || !m_maintenance.empty()) break;
                if (next == std::chrono::steady_clock::time_point::max()) m_cv.wait(lk);
                else m_cv.wait_until(lk, next);
            }
            if (m_stopped && m_engine.empty() && m_maintenance.empty()) return false;
        }
        for (;;)
        {
            std::function<void()> fn;
            {
                std::lock_guard<std::mutex> g(m_mx);
                PromoteDueLocked();
                if (!PopLocked(fn)) break;
            }
            RunItem(fn, onThrow);
        }
        return true;
    }

    /// Runtime teardown: run every Maintenance item already queued, now, in order. Teardown is an Engine item, which
    /// would otherwise run ahead of the licence work posted before it; those steps (a key session's Close above all)
    /// must finish while the CDM is still alive.
    void DrainMaintenance(ThrowHandler onThrow = nullptr)
    {
        std::deque<std::function<void()>> batch;
        {
            std::lock_guard<std::mutex> g(m_mx);
            batch.swap(m_maintenance);
        }
        for (auto& fn : batch) RunItem(fn, onThrow);
    }

    void Stop()
    {
        {
            std::lock_guard<std::mutex> g(m_mx);
            m_stopped = true;
        }
        m_cv.notify_all();
    }

  private:
    struct Delayed
    {
        std::chrono::steady_clock::time_point due;
        std::function<void()> fn;
    };

    static void RunItem(const std::function<void()>& fn, ThrowHandler onThrow)
    {
        try { fn(); }
        catch (...) { if (onThrow) onThrow(); }
    }

    /// Delayed items whose time has come join the Engine lane. Returns the earliest due time still to come (max when none).
    std::chrono::steady_clock::time_point PromoteDueLocked()
    {
        const auto now = std::chrono::steady_clock::now();
        auto next = std::chrono::steady_clock::time_point::max();
        for (auto it = m_delayed.begin(); it != m_delayed.end(); )
        {
            if (it->due <= now) { m_engine.push_back(std::move(it->fn)); it = m_delayed.erase(it); }
            else { if (it->due < next) next = it->due; ++it; }
        }
        return next;
    }

    /// The next item to run: Engine first, except that a waiting Maintenance item is taken after kMaintenanceEveryN
    /// consecutive Engine items.
    bool PopLocked(std::function<void()>& out)
    {
        const bool haveEngine = !m_engine.empty();
        const bool haveMaintenance = !m_maintenance.empty();
        if (!haveEngine && !haveMaintenance) return false;
        if (haveMaintenance && (!haveEngine || m_engineStreak >= kMaintenanceEveryN))
        {
            out = std::move(m_maintenance.front());
            m_maintenance.pop_front();
            m_engineStreak = 0;
            return true;
        }
        out = std::move(m_engine.front());
        m_engine.pop_front();
        m_engineStreak = haveMaintenance ? m_engineStreak + 1 : 0;
        return true;
    }

    std::mutex m_mx;
    std::condition_variable m_cv;
    std::deque<std::function<void()>> m_engine;
    std::deque<std::function<void()>> m_maintenance;
    std::vector<Delayed> m_delayed;
    int m_engineStreak = 0;
    bool m_stopped = false;
};

}   // namespace fgpr
