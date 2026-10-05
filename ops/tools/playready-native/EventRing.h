// EventRing.h — the native→managed event queue: producers PUSH, one notifier thread DELIVERS.
//
// WHY. Every native event and every diagnostic line used to enter managed code on the thread that raised it: the runtime
// thread (so a GC suspension or the app's log sink stalled the queue that runs SetSource / Play / UpdateVideoStream), and
// MF / CDM threads — CencMediaStream::RequestSample logged while holding the stream's m_mx, so a managed GC held the
// feeder's AppendSamples and every coverage query behind it. A producer now copies its event (POD plus the text) into
// this queue and returns; ONE notifier thread drains it into the managed callback in batches. Nothing native waits for
// managed code any more (the licence relay callback is the one deliberate exception and does not come through here).
//
// CONTRACT.
//   * Order. One FIFO and one consumer: events are delivered in the order they were pushed, so a session's events never
//     reorder. A producer holding a lock while it pushes (the stream's m_mx) is safe: Push takes only the queue's own
//     mutex for a few instructions and never calls out.
//   * No lock is held across a delivery. The callback may itself Push (re-entrancy is fine) and a slow callback only
//     delays later deliveries, never a producer.
//   * Bounded. Log lines are droppable: past kMaxPendingLogs queued lines the newest is dropped and counted, and the
//     consumer is told once ("N log line(s) dropped"). State events are never dropped for the log cap; only a consumer
//     wedged past kHardCap events sheds them too (counted the same way), so a stuck callback cannot grow memory without end.
//   * Flush on Stop. Stop() stops accepting, delivers everything already queued, and joins the notifier (bounded): when it
//     returns the callback is never called again, which is what lets the managed side free its context. A Push after Stop
//     (a straggling MF thread) is rejected and returns false. A Stop that has to abandon a wedged notifier (or is called on
//     the notifier itself) drops the undelivered backlog instead: events queued for the old callback context never reach the
//     context a later Start installs, and the abandoned thread can no longer mark that later run done.
//
// Standalone on purpose (std only, no Windows, no FgPlayReady.h): tests\FeedTests.cpp includes it directly.
#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <utility>

namespace fgpr {

/// One queued event. `text` is a copy the ring owns; the consumer sees `text.c_str()` for the length of the delivery.
struct RingEvent
{
    uint64_t handle = 0;
    int32_t ev = 0;
    int64_t a = 0;
    int64_t b = 0;
    std::wstring text;
    bool droppable = false;
};

class EventRing
{
  public:
    /// Called on the notifier thread, with no ring lock held.
    using Deliver = void (*)(void* ctx, const RingEvent& e);

    static constexpr size_t kMaxPendingLogs = 4096;
    static constexpr size_t kHardCap = 16384;
    /// How long Stop waits for the notifier to finish a flush before abandoning it (a callback wedged in managed code).
    static constexpr int kStopWaitMs = 2000;

    /// `logEv` is the event id the "lines dropped" notice is raised under (FgPrEvent_Log).
    explicit EventRing(int32_t logEv) : m_logEv(logEv) {}
    ~EventRing() { Stop(); }
    EventRing(const EventRing&) = delete;
    EventRing& operator=(const EventRing&) = delete;

    /// Begin accepting events and start the notifier. A second Start while running is a no-op.
    void Start(Deliver deliver, void* ctx)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_running) return;
        m_running = true;
        m_accepting = true;
        m_stopping = false;
        m_done = false;
        m_deliver = deliver;
        m_ctx = ctx;
        const uint64_t gen = m_gen.load(std::memory_order_acquire);
        m_thread = std::thread([this, gen] { Run(gen); });
    }

    /// Queue an event. Never blocks on the consumer and never calls out. False when it was not queued (not accepting,
    /// or dropped for the caps — the drop is counted and reported to the consumer).
    bool Push(uint64_t handle, int32_t ev, int64_t a, int64_t b, const wchar_t* text, bool droppable)
    {
        // The copy happens before the lock: the critical section is a push_back of an already-built item.
        RingEvent item;
        item.handle = handle;
        item.ev = ev;
        item.a = a;
        item.b = b;
        if (text) item.text = text;
        item.droppable = droppable;
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (!m_accepting) return false;
            if (m_items.size() >= kHardCap || (droppable && m_pendingLogs >= kMaxPendingLogs))
            {
                m_dropped++;
                m_totalDropped.fetch_add(1, std::memory_order_acq_rel);
            }
            else
            {
                if (droppable) m_pendingLogs++;
                m_items.push_back(std::move(item));
            }
            // A dropped item still wakes the consumer: it owes the "N dropped" notice.
        }
        m_cv.notify_one();
        return true;
    }

    /// Stop accepting, deliver everything queued, and join the notifier (bounded by kStopWaitMs; a notifier that does not
    /// finish in time is abandoned and delivers nothing further). Safe to call twice, from any thread. Called from the
    /// notifier itself (a callback that destroys the runtime) it cannot join: the notifier is detached and abandoned, so it
    /// delivers nothing after that callback returns.
    void Stop()
    {
        std::thread worker;
        bool clean = false;
        bool self = false;
        {
            std::unique_lock<std::mutex> lk(m_mx);
            if (!m_running) return;
            m_accepting = false;
            m_stopping = true;
            m_cv.notify_all();
            self = m_thread.get_id() == std::this_thread::get_id();
            if (!self) clean = m_doneCv.wait_for(lk, std::chrono::milliseconds(kStopWaitMs), [this] { return m_done; });
            if (!clean)
            {
                m_gen.fetch_add(1, std::memory_order_acq_rel);   // the notifier delivers nothing more
                // What it left behind belongs to the old callback context: a later Start must not deliver it to the new one.
                m_items.clear();
                m_pendingLogs = 0;
                m_dropped = 0;
            }
            worker = std::move(m_thread);
            m_running = false;
        }
        if (!worker.joinable()) return;
        if (clean) worker.join();
        else worker.detach();
    }

    /// Events queued and not yet handed to the consumer (diagnostics and tests).
    size_t Pending() const
    {
        std::lock_guard<std::mutex> g(m_mx);
        return m_items.size();
    }

    /// Total events and lines shed for the caps since construction (tests).
    uint64_t TotalDropped() const { return m_totalDropped.load(std::memory_order_acquire); }

  private:
    void Run(uint64_t gen)
    {
        std::deque<RingEvent> batch;
        for (;;)
        {
            uint64_t dropped = 0;
            bool stop = false;
            Deliver deliver = nullptr;
            void* ctx = nullptr;
            {
                std::unique_lock<std::mutex> lk(m_mx);
                m_cv.wait(lk, [&] { return m_gen.load(std::memory_order_acquire) != gen || m_stopping || m_dropped != 0 || !m_items.empty(); });
                if (m_gen.load(std::memory_order_acquire) != gen) return;   // abandoned by a Stop that timed out
                batch.swap(m_items);
                m_pendingLogs = 0;
                dropped = m_dropped;
                m_dropped = 0;
                stop = m_stopping;
                deliver = m_deliver;
                ctx = m_ctx;
            }

            // No lock is held from here: the consumer may block, allocate, or Push.
            if (deliver)
            {
                for (RingEvent& e : batch)
                {
                    if (m_gen.load(std::memory_order_acquire) != gen) return;
                    deliver(ctx, e);
                }
                if (dropped != 0)
                {
                    RingEvent note;
                    note.ev = m_logEv;
                    note.text = L"[events] " + std::to_wstring((unsigned long long)dropped) + L" native event(s) dropped (the managed consumer fell behind)";
                    if (m_gen.load(std::memory_order_acquire) == gen) deliver(ctx, note);
                }
            }
            batch.clear();

            if (stop)
            {
                std::lock_guard<std::mutex> g(m_mx);
                // A Push that raced Stop() cannot exist: accepting was cleared under this mutex before stopping was set.
                // An abandoned notifier (Stop timed out) must not mark a LATER run done: that would turn the next Stop's
                // bounded wait into an unbounded join on the new notifier.
                if (m_gen.load(std::memory_order_acquire) == gen)
                {
                    m_done = true;
                    m_doneCv.notify_all();
                }
                return;
            }
        }
    }

    const int32_t m_logEv;
    mutable std::mutex m_mx;
    std::condition_variable m_cv;       // producers -> the notifier
    std::condition_variable m_doneCv;   // the notifier -> Stop
    std::deque<RingEvent> m_items;
    size_t m_pendingLogs = 0;
    uint64_t m_dropped = 0;
    std::atomic<uint64_t> m_totalDropped{ 0 };
    std::atomic<uint64_t> m_gen{ 0 };
    bool m_running = false;
    bool m_accepting = false;
    bool m_stopping = false;
    bool m_done = false;
    Deliver m_deliver = nullptr;
    void* m_ctx = nullptr;
    std::thread m_thread;
};

}   // namespace fgpr
