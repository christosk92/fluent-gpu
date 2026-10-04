// PrInternal.h — the internals shared by PrRuntime.cpp, PrLicense.cpp and PrSession.cpp.
//
// THE SPLIT (wavee-0.3-video-engine-implementation.md §3.1.1). The one-shot `FgPlayReadyRunEx` owned everything for
// the lifetime of ONE source and kept its state in ~60 process globals. Its replacement is three translation units
// over three object kinds, all reachable ONLY through a handle table:
//   * Runtime  (PrRuntime.cpp)  — MF, the D3D11 video device + DXGI manager, the CDM + PMP host, the ONE media engine,
//                                 its notify sink + scheme handler, and the MTA runtime thread with its work queue;
//   * License  (PrLicense.cpp)  — one open TEMPORARY CDM key session per KID, the non-blocking relay;
//   * Session  (PrSession.cpp)  — one CencMediaSource over one SegmentStore, its feeder thread and its snapshot.
// A stale handle resolves to nothing (E_HANDLE), which is the property the whole rework is for: a predecessor's
// teardown can no longer land on its successor.
//
// LOGGING. There is no log file any more (desktop-playready.log is retired). Every line that used to go through
// `LogLine` is an FgPrEvent_Log on the managed callback, which writes it into the app's one log under `[video.native]`.
// `LogLine` survives as a name only so the demuxer and the media source (CencMediaSource.h) keep their long-standing
// diagnostic lines unchanged; the source and its streams shadow it with a member that stamps their session handle.
#pragma once

// windows.h's min/max macros would turn every std::min / std::max / std::numeric_limits<T>::max below into a syntax
// error; nothing in this directory uses the macros.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_3.h>
#include <dxgi1_4.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfmediaengine.h>
#include <mfcontentdecryptionmodule.h>
#include <windows.media.protection.h>   // ABI::...::IMediaProtectionPMPServer (PMP-server sharing to the media engine)
#include <propsys.h>
#include <propvarutil.h>
#include <wincrypt.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <climits>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwctype>
#include <deque>
#include <functional>
#include <iomanip>
#include <memory>
#include <mutex>
#include <new>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Media.Protection.h>
#include <winrt/Windows.Web.Http.h>
#include <winrt/Windows.Web.Http.Filters.h>
#include <winrt/Windows.Web.Http.Headers.h>

#include "FgPlayReady.h"
#include "EventRing.h"
#include "OpmWindow.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "propsys.lib")
#pragma comment(lib, "crypt32.lib")

namespace WWH = winrt::Windows::Web::Http;

namespace fgpr {

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Small helpers — HRESULT text, clocks, string conversion.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

inline std::string Hex(HRESULT h)
{
    char b[16];
    snprintf(b, sizeof(b), "0x%x", (unsigned)(uint32_t)h);
    return std::string(b);
}

inline int64_t QpcNow()
{
    LARGE_INTEGER li;
    QueryPerformanceCounter(&li);
    return li.QuadPart;
}

inline int64_t QpcFrequency()
{
    static const int64_t f = [] { LARGE_INTEGER li; QueryPerformanceFrequency(&li); return li.QuadPart ? li.QuadPart : 1; }();
    return f;
}

inline int64_t QpcToMs(int64_t ticks) { return ticks * 1000 / QpcFrequency(); }
inline int64_t MsSinceQpc(int64_t startQpc) { return startQpc ? QpcToMs(QpcNow() - startQpc) : 0; }

/// Milliseconds since the Unix epoch — the unit IMFContentDecryptionModuleSession::GetExpiration reports in.
inline double UnixMsNow()
{
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    const uint64_t t100ns = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    return (double)((t100ns - 116444736000000000ULL) / 10000ULL);
}

inline std::wstring Widen(const std::string& s)
{
    if (s.empty()) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    std::wstring w((size_t)(n > 0 ? n : 0), L'\0');
    if (n > 0) MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), w.data(), n);
    return w;
}

inline std::string Narrow(const std::wstring& w)
{
    if (w.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s((size_t)(n > 0 ? n : 0), '\0');
    if (n > 0) WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}

/// The 16 big-endian tenc KID bytes as 32 lowercase hex chars — the form every license event carries in `text`.
inline std::wstring KidToHex(const uint8_t kid[16])
{
    static const wchar_t digits[] = L"0123456789abcdef";
    std::wstring out(32, L'0');
    for (int i = 0; i < 16; i++)
    {
        out[(size_t)i * 2] = digits[(kid[i] >> 4) & 0xF];
        out[(size_t)i * 2 + 1] = digits[kid[i] & 0xF];
    }
    return out;
}

/// 32 hex chars (dashes tolerated, case-insensitive) → 16 bytes. False on anything else.
inline bool HexToKid(const wchar_t* hex, uint8_t out[16])
{
    if (!hex) return false;
    int nibbles = 0;
    uint8_t acc = 0;
    for (const wchar_t* p = hex; *p; p++)
    {
        wchar_t c = *p;
        if (c == L'-' || c == L'{' || c == L'}') continue;
        int v;
        if (c >= L'0' && c <= L'9') v = c - L'0';
        else if (c >= L'a' && c <= L'f') v = 10 + (c - L'a');
        else if (c >= L'A' && c <= L'F') v = 10 + (c - L'A');
        else return false;
        if (nibbles >= 32) return false;
        acc = (uint8_t)((acc << 4) | v);
        if (nibbles & 1) out[nibbles / 2] = acc;
        nibbles++;
    }
    return nibbles == 32;
}

inline std::wstring NormalizeKidHex(const wchar_t* hex)
{
    uint8_t kid[16];
    return HexToKid(hex, kid) ? KidToHex(kid) : std::wstring();
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Events — the ONE log path, through a native ring.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Raise / RaiseLog never call managed code. They copy the event (POD plus the text) into EventRing.h's queue and return;
// ONE native notifier thread drains it into the managed callback in batches. So the runtime thread stays purely native
// (a managed GC or the app's log sink can no longer stall SetSource / Play / UpdateVideoStream), and an MF or CDM thread
// - CencMediaStream::RequestSample logs under the stream's m_mx - never waits on managed code. The only call native makes
// into managed synchronously is the licence relay (FgPrLicenseCallback), which is a different callback and returns at once.
//
// The sink is process-wide because there is one runtime per process (a second FgPrRuntimeCreateOnAdapter returns the existing
// handle). What makes FgPrRuntimeDestroy safe against the managed side freeing its GCHandle the moment Destroy returns is
// EventRing::Stop: it delivers whatever is still queued, joins the notifier, and rejects later pushes, so no call into the
// callback is in flight or can start once it returns.
// The callback is held as an integer: std::atomic over a FUNCTION pointer type leans on the atomic<T*> specialization
// with a function type for T, which is not something to discover at build time on one of two architectures.
struct EventSink
{
    std::atomic<uintptr_t> cb{ 0 };
    std::atomic<void*> ctx{ nullptr };
    EventRing ring{ FgPrEvent_Log };

    void Set(FgPrEventCallback callback, void* context)
    {
        ctx.store(context, std::memory_order_release);
        cb.store(reinterpret_cast<uintptr_t>(callback), std::memory_order_release);
    }
    FgPrEventCallback Get() const { return reinterpret_cast<FgPrEventCallback>(cb.load(std::memory_order_acquire)); }

    /// The notifier thread's delivery: one queued event into the managed callback. Runs on the notifier, no lock held.
    static void Deliver(void* /*self*/, const RingEvent& e);
};

inline EventSink& Sink()
{
    static EventSink* sink = new EventSink();   // never destroyed: an MF thread may still be unwinding at DLL detach
    return *sink;
}

inline void EventSink::Deliver(void* /*self*/, const RingEvent& e)
{
    EventSink& sink = Sink();
    FgPrEventCallback callback = sink.Get();
    if (callback) callback(sink.ctx.load(std::memory_order_acquire), e.handle, e.ev, e.a, e.b, e.text.c_str());
}

/// Start the notifier: call after Set(), before anything can raise. Idempotent while running.
inline void SinkStart() { Sink().ring.Start(&EventSink::Deliver, nullptr); }

/// Flush the ring into the callback, stop the notifier, and clear the callback. Nothing is delivered after this returns.
inline void SinkStop()
{
    EventSink& sink = Sink();
    sink.ring.Stop();
    sink.cb.store(0, std::memory_order_release);
    sink.ctx.store(nullptr, std::memory_order_release);
}

inline void Raise(uint64_t handle, int32_t ev, int64_t a = 0, int64_t b = 0, const wchar_t* text = nullptr)
{
    Sink().ring.Push(handle, ev, a, b, text, /*droppable*/ false);
}

inline void RaiseLog(uint64_t handle, const std::string& line)
{
    if (!Sink().Get()) return;   // FgPrProbeFile on a box with no runtime: nobody listens
    // A stack buffer covers every line the helper writes; only a pathological line pays a heap conversion.
    wchar_t stackBuf[1024];
    int n = line.empty() ? 0 : MultiByteToWideChar(CP_UTF8, 0, line.data(), (int)line.size(), stackBuf, 1023);
    if (n > 0 || line.empty())
    {
        stackBuf[n > 0 ? n : 0] = L'\0';
        Sink().ring.Push(handle, FgPrEvent_Log, 0, 0, stackBuf, /*droppable*/ true);
        return;
    }
    std::wstring w = Widen(line);
    Sink().ring.Push(handle, FgPrEvent_Log, 0, 0, w.c_str(), /*droppable*/ true);
}

}   // namespace fgpr

/// The historical diagnostic entry point, kept so the demuxer's and the source's lines read exactly as before.
/// Runtime-level (session 0); CencMediaSource / CencMediaStream shadow it with a member that stamps their session.
inline void LogLine(const std::string& s) { fgpr::RaiseLog(0, s); }

// The demuxer + IMFMediaSource (it includes SegmentStore.h right after `namespace cenc` closes).
#include "CencMediaSource.h"
#include "SegmentStore.h"
#include "FeedPlan.h"
#include "HandoverPolicy.h"
#include "WorkQueue.h"

namespace fgpr {

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Handles.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The top byte names the kind so a license handle passed where a session is expected is E_HANDLE, not a wrong
// object; the low 56 bits are a process-wide counter that never repeats, so a destroyed handle never comes back.
enum class HandleKind : uint64_t { Runtime = 1, License = 2, Session = 3 };

inline uint64_t NewHandle(HandleKind kind)
{
    static std::atomic<uint64_t> next{ 1 };
    return ((uint64_t)kind << 56) | (next.fetch_add(1, std::memory_order_acquire) & 0x00FFFFFFFFFFFFFFull);
}

inline bool IsKind(uint64_t h, HandleKind kind) { return h != 0 && (h >> 56) == (uint64_t)kind; }

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  WorkQueue (WorkQueue.h) — the runtime thread's two-lane queue: engine/transport work ahead of licence work.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
/// The runtime thread's ThrowHandler: a work item that threw is logged, never fatal to the thread. Called from inside the
/// queue's catch block, so `throw;` re-raises the exception being handled.
inline void LogWorkItemFailure()
{
    try { throw; }
    catch (winrt::hresult_error const& e) { RaiseLog(0, "[runtime] work item threw hr=" + Hex(e.code().value)); }
    catch (...) { RaiseLog(0, "[runtime] work item threw"); }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  HTTP — the shared client and a cancellable, two-phase GET.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

// ONE HttpClient for the whole process. A per-request client (what this used to construct) gets no connection pool and
// no TLS session cache, so every segment paid a fresh connect + full handshake — six serial handshakes before the first
// frame. WinRT's HttpClient is agile and safe to share across the feeder and the MTA loop. Deliberately leaked: a
// static WinRT object destroyed during DLL/CRT teardown would run its release on an already-torn-down apartment.
//
// NO WININET CACHE (F044). The default client's filter reads and writes the user's INetCache, so every multi-MB media
// segment was also written to disk during playback (cache bloat and disk churn) although the session keeps its own
// store: the SegmentStore is the cache. The cache also answered a repeated GET of one URL at ~0 ms, which is what hid
// the refetch livelock (F034) and poisoned the throughput estimate (HttpFetchTiming::fromStore); the livelock is gone,
// and with both behaviours at NoCache a segment is always a real transfer. The cache behaviour is per filter, not per
// request, and nothing else in this DLL uses the client, so init segments (small, refetched only per attach or switch)
// share it.
inline WWH::Filters::HttpBaseProtocolFilter MakeNoCacheFilter()
{
    WWH::Filters::HttpBaseProtocolFilter filter;
    filter.CacheControl().ReadBehavior(WWH::Filters::HttpCacheReadBehavior::NoCache);
    filter.CacheControl().WriteBehavior(WWH::Filters::HttpCacheWriteBehavior::NoCache);
    return filter;
}

inline WWH::HttpClient& SessionHttpClient()
{
    // Constructed once into static storage and never destroyed: a WinRT projection object released during CRT/DLL
    // teardown would call into an apartment that no longer exists.
    alignas(WWH::HttpClient) static unsigned char storage[sizeof(WWH::HttpClient)];
    static WWH::HttpClient* client = ::new (static_cast<void*>(storage)) WWH::HttpClient(MakeNoCacheFilter());
    return *client;
}

// One media-segment fetch, as the ABR estimator should see it: BODY bytes and BODY transfer time only. Connect + TLS +
// time-to-first-byte are excluded (they are RTT, not throughput — dash.js excludes them for exactly this reason), and
// so are init segments, which are small and RTT-dominated and would otherwise depress the estimate at startup.
struct HttpFetchTiming
{
    uint64_t headerMs = 0;    // connect + TLS + TTFB (diagnostic only; never folded into the throughput estimate)
    uint64_t transferMs = 0;  // response-body transfer
    uint64_t bytes = 0;
    // THROUGHPUT HYGIENE. Set from HttpResponseMessage::Source() the moment the response headers arrive (HttpFetch::
    // OnHeaders): true means the WinRT HTTP cache answered this request itself — no bytes crossed the network, and
    // `transferMs`/`headerMs` measure a cache lookup, not a transfer. Never inferred from `transferMs == 0`: a fast
    // CDN edge can legitimately answer in under a millisecond, and THAT is real throughput. This is what the
    // production incident actually was — the guard bug's repeated identical GETs for the same segment URL were being
    // answered out of that cache at ~0 ms, which is what poisoned the ABR estimate at ~1.3 Gbps. Every place that
    // folds `bytes`/`transferMs` into Session::bytesDownloaded / downloadElapsedMs must check this first. (The shared
    // client now runs with the cache at NoCache - SessionHttpClient - so this stays false in practice; the check is the
    // guard that keeps a future filter change from silently poisoning the estimate again.)
    bool fromStore = false;
};

/// Apply app-supplied request headers ("Name: Value\n" lines) — e.g. auth for a real CDN (M6). Moved verbatim.
inline void ApplyRequestHeaders(WWH::HttpRequestMessage const& req, const std::wstring& extraHeaders)
{
    for (size_t p = 0; p < extraHeaders.size(); )
    {
        size_t nl = extraHeaders.find(L'\n', p);
        std::wstring line = extraHeaders.substr(p, nl == std::wstring::npos ? std::wstring::npos : nl - p);
        p = nl == std::wstring::npos ? extraHeaders.size() : nl + 1;
        if (!line.empty() && line.back() == L'\r') line.pop_back();
        size_t colon = line.find(L':');
        if (colon == std::wstring::npos) continue;
        std::wstring name = line.substr(0, colon), value = line.substr(colon + 1);
        while (!value.empty() && iswspace(value.front())) value.erase(value.begin());
        while (!name.empty() && iswspace(name.back())) name.pop_back();
        if (!name.empty()) try { req.Headers().TryAppendWithoutValidation(winrt::hstring(name), winrt::hstring(value)); } catch (...) {}
    }
}

/// What a waiter blocks on to learn that ANY of several fetches finished (F041): each fetch begun with the set signals it
/// from Complete, and the waiter re-evaluates its own predicate (HttpFetch::IsDone) under the set's mutex. Lock order is
/// set.mx -> a fetch's m_mx (the predicate); a fetch signals only after releasing its own lock, so the two never invert.
struct FetchWaitSet
{
    std::mutex mx;
    std::condition_variable cv;

    void Signal()
    {
        { std::lock_guard<std::mutex> g(mx); }   // the waiter's predicate-check-to-wait window is closed under this lock
        cv.notify_all();
    }

    template <class Pred>
    void WaitUntil(Pred pred)
    {
        std::unique_lock<std::mutex> lk(mx);
        cv.wait(lk, pred);
    }
};

/// One GET, started and completed through WinRT completion handlers rather than a blocking `.get()`.
///
/// WHY TWO PHASES AND NOT `.get()`: the old HttpGetBytes blocked its thread on SendRequestAsync, then on
/// ReadAsBufferAsync, and polled a cancel predicate every 10 ms while doing so. Two blocking GETs on one thread are
/// SERIAL no matter how they are ordered (the second body read cannot start until the first returns), which is plan
/// bug S3: a far seek paid two round trips back to back. Here `Begin` returns as soon as the request is on the wire,
/// the header completion starts the body read from the thread pool, and the body completion signals the waiter — so a
/// video and an audio fetch begun together transfer TOGETHER on the shared client. `Cancel` is the cancellation token:
/// it calls IAsyncInfo::Cancel on whichever phase is in flight, the completion fires with AsyncStatus::Canceled, and
/// the waiter wakes. Nothing polls.
///
/// ResponseHeadersRead (not the default ResponseContentRead) is what splits the two phases apart: the send completes
/// once the response line + headers are in, so everything after it is pure body transfer (the ABR telemetry above).
class HttpFetch : public std::enable_shared_from_this<HttpFetch>
{
  public:
    using SendOp = winrt::Windows::Foundation::IAsyncOperationWithProgress<WWH::HttpResponseMessage, WWH::HttpProgress>;
    using ReadOp = winrt::Windows::Foundation::IAsyncOperationWithProgress<winrt::Windows::Storage::Streams::IBuffer, uint64_t>;

    std::wstring url;
    int status = 0;                  // HTTP status; 0 when the transport failed or the fetch was cancelled
    HRESULT hr = S_OK;               // transport failure (0 on an HTTP answer of any status)
    bool cancelled = false;
    HttpFetchTiming timing;
    std::vector<uint8_t> body;       // taken from the session's SlabPool; give it back with ReleaseBody

    /// `notify`, when given, is signalled the moment this fetch completes (success, failure or cancellation), so a caller
    /// with several fetches in flight can act on whichever finishes first instead of waiting for them in a fixed order.
    static std::shared_ptr<HttpFetch> Begin(const std::wstring& url, const std::wstring& headers,
                                            std::shared_ptr<SegmentStore> store,
                                            std::shared_ptr<FetchWaitSet> notify = nullptr)
    {
        auto f = std::make_shared<HttpFetch>();
        f->url = url;
        f->m_store = std::move(store);
        f->m_notify = std::move(notify);   // before the request is on the wire: Complete reads it from any thread
        try
        {
            WWH::HttpRequestMessage req{ WWH::HttpMethod::Get(), winrt::Windows::Foundation::Uri{ winrt::hstring(url) } };
            ApplyRequestHeaders(req, headers);
            f->m_headerStartMs = GetTickCount64();
            SendOp op = SessionHttpClient().SendRequestAsync(req, WWH::HttpCompletionOption::ResponseHeadersRead);
            bool cancelNow = false;
            {
                std::lock_guard<std::mutex> g(f->m_mx);
                f->m_current = op.as<winrt::Windows::Foundation::IAsyncInfo>();
                cancelNow = f->m_cancelRequested;
            }
            if (cancelNow) { try { op.Cancel(); } catch (...) {} }
            // Completed is set OUTSIDE the lock: on an operation that already finished it runs synchronously here.
            op.Completed([f](SendOp const& done, winrt::Windows::Foundation::AsyncStatus st) { f->OnHeaders(done, st); });
        }
        catch (winrt::hresult_error const& e) { f->Complete(0, e.code().value, false); }
        catch (...) { f->Complete(0, E_FAIL, false); }
        return f;
    }

    /// The cancellation token. Callable from ANY thread, any number of times, before or after completion.
    void Cancel()
    {
        winrt::Windows::Foundation::IAsyncInfo cur{ nullptr };
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_done) return;
            m_cancelRequested = true;
            cur = m_current;
        }
        // Outside the lock: Cancel may invoke the completion handler synchronously, and that handler takes m_mx.
        if (cur) { try { cur.Cancel(); } catch (...) {} }
    }

    void Wait()
    {
        std::unique_lock<std::mutex> lk(m_mx);
        m_cv.wait(lk, [this] { return m_done; });
    }

    /// Whether the fetch has completed. Once true the result fields (status, hr, cancelled, timing, body) are safe to read.
    bool IsDone()
    {
        std::lock_guard<std::mutex> g(m_mx);
        return m_done;
    }

    bool Ok() const { return !cancelled && status == 200 && !body.empty(); }

    void ReleaseBody()
    {
        if (m_store) m_store->pool.Give(std::move(body));
        body = std::vector<uint8_t>();
    }

  private:
    void OnHeaders(SendOp const& done, winrt::Windows::Foundation::AsyncStatus st)
    {
        using winrt::Windows::Foundation::AsyncStatus;
        if (st == AsyncStatus::Canceled) { Complete(0, S_OK, true); return; }
        if (st != AsyncStatus::Completed) { Complete(0, done.ErrorCode().value, IsCancelRequested()); return; }
        try
        {
            WWH::HttpResponseMessage resp = done.GetResults();
            timing.headerMs = GetTickCount64() - m_headerStartMs;
            // THROUGHPUT HYGIENE (see HttpFetchTiming::fromStore): the WinRT HTTP cache, not this class, is the
            // cache — Source() says whether IT answered from its own cache instead of the network.
            timing.fromStore = resp.Source() == WWH::HttpResponseMessageSource::Cache;
            status = (int)resp.StatusCode();
            m_bodyStartMs = GetTickCount64();
            ReadOp readOp = resp.Content().ReadAsBufferAsync();
            bool cancelNow = false;
            {
                std::lock_guard<std::mutex> g(m_mx);
                m_response = resp;   // the content stream belongs to the response: keep it alive through the read
                m_current = readOp.as<winrt::Windows::Foundation::IAsyncInfo>();
                cancelNow = m_cancelRequested;
            }
            if (cancelNow) { try { readOp.Cancel(); } catch (...) {} }
            auto self = shared_from_this();
            readOp.Completed([self](ReadOp const& r, winrt::Windows::Foundation::AsyncStatus rs) { self->OnBody(r, rs); });
        }
        catch (winrt::hresult_error const& e) { Complete(0, e.code().value, IsCancelRequested()); }
        catch (...) { Complete(0, E_FAIL, IsCancelRequested()); }
    }

    void OnBody(ReadOp const& r, winrt::Windows::Foundation::AsyncStatus st)
    {
        using winrt::Windows::Foundation::AsyncStatus;
        const int httpStatus = status;
        if (st == AsyncStatus::Canceled) { Complete(0, S_OK, true); return; }
        if (st != AsyncStatus::Completed) { Complete(0, r.ErrorCode().value, IsCancelRequested()); return; }
        try
        {
            auto buf = r.GetResults();
            timing.transferMs = GetTickCount64() - m_bodyStartMs;
            const uint32_t n = buf.Length();
            // Placed, not new'd: the body lands in a pooled 64 KiB-granular buffer (SegmentStore.h SlabPool).
            body = m_store ? m_store->pool.Take(n) : std::vector<uint8_t>(n);
            if (n) winrt::Windows::Storage::Streams::DataReader::FromBuffer(buf).ReadBytes(
                       winrt::array_view<uint8_t>(body.data(), body.data() + n));
            timing.bytes = n;
            Complete(httpStatus, S_OK, false);
        }
        catch (winrt::hresult_error const& e) { Complete(0, e.code().value, IsCancelRequested()); }
        catch (...) { Complete(0, E_FAIL, IsCancelRequested()); }
    }

    bool IsCancelRequested()
    {
        std::lock_guard<std::mutex> g(m_mx);
        return m_cancelRequested;
    }

    void Complete(int httpStatus, HRESULT transportHr, bool wasCancelled)
    {
        // Drop the operation + response references here: the operation holds the completion delegate, the delegate
        // holds a strong ref to this object, and this object held the operation — a cycle nobody else would break.
        winrt::Windows::Foundation::IAsyncInfo cur{ nullptr };
        WWH::HttpResponseMessage resp{ nullptr };
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_done) return;
            status = wasCancelled ? 0 : httpStatus;
            hr = transportHr;
            cancelled = wasCancelled;
            m_done = true;
            cur = std::move(m_current);
            resp = std::move(m_response);
        }
        m_cv.notify_all();
        if (m_notify) m_notify->Signal();
    }

    std::mutex m_mx;
    std::condition_variable m_cv;
    bool m_done = false;
    bool m_cancelRequested = false;
    winrt::Windows::Foundation::IAsyncInfo m_current{ nullptr };
    WWH::HttpResponseMessage m_response{ nullptr };
    std::shared_ptr<SegmentStore> m_store;
    std::shared_ptr<FetchWaitSet> m_notify;
    uint64_t m_headerStartMs = 0;
    uint64_t m_bodyStartMs = 0;
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The objects behind the handles.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

struct Runtime
{
    uint64_t handle = 0;
    std::wstring storePath;
    int64_t adapterLuid = 0;             // (HighPart << 32) | LowPart of the adapter the D3D11 device is created on; 0 = default
    int64_t createdQpc = 0;
    WorkQueue queue;
    std::thread thread;

    // S_FALSE while the thread is still bringing MF/D3D/CDM/engine up, S_OK once ready, the failing HRESULT otherwise.
    std::atomic<int32_t> bringUp{ S_FALSE };
    std::atomic<bool> shuttingDown{ false };

    // ── touched on the runtime thread ONLY ──────────────────────────────────────────────────────────────────────────
    bool comInitialized = false;
    bool mfStarted = false;
    /// MFStartup has returned (set by BringUp, early in the bring-up). The feeder builds its Media Foundation source as
    /// soon as the init segments land - possibly before the runtime thread is up (the early kick at FgPrSessionAttach) -
    /// and waits on this, never on the whole bring-up.
    std::atomic<bool> mfReady{ false };
    winrt::com_ptr<ID3D11Device> d3d;
    winrt::com_ptr<IMFDXGIDeviceManager> dxgiManager;
    UINT resetToken = 0;
    winrt::com_ptr<IMFContentDecryptionModule> cdm;
    winrt::com_ptr<IMFContentProtectionManager> protectionManager;
    winrt::com_ptr<IMFMediaEngine> engine;
    winrt::com_ptr<IMFMediaEngineEx> engineEx;
    winrt::com_ptr<IMFMediaEngineProtectedContent> protectedContent;
    winrt::com_ptr<IMFMediaEngineNotify> notify;
    winrt::com_ptr<IMFMediaEngineExtension> extension;
    winrt::com_ptr<IMFMediaEngineNeedKeyNotify> needKey;
    bool itaPreflightDone = false;

    /// The hidden virtual window handed to the engine as MF_MEDIA_ENGINE_OPM_HWND (F264). Started by BringUp BEFORE the engine
    /// is created and stopped by TearDown AFTER it is shut down (both on the runtime thread); FgPrSessionPlaceOpmWindow moves it
    /// from the UI thread (OpmWindow is internally synchronized). A runtime whose window could not be created simply has none.
    OpmWindow opm;

    /// The CDM-scope IMFTrustedInput (PrSession.cpp CompleteAttach), held only when `trustedInputReuse` is on: created by
    /// the first protected attach, handed to every later source, released with the CDM (PrRuntime.cpp TearDown; a CDM
    /// reset is a whole new Runtime, so there is no other reset point). Runtime thread only.
    winrt::com_ptr<IMFTrustedInput> trustedInput;
    /// FG_PLAYREADY_TRUSTED_INPUT_REUSE=1 (read once at bring-up). OFF by default: a second source over the same trusted
    /// input may make PMP reject the ITA proxy for another KID, so the default stays one trusted input per attach until
    /// the on-box check (two KIDs, then a re-attach) has passed. Runtime thread only.
    bool trustedInputReuse = false;

    /// The session whose source the engine is playing — or is about to play, while its init segments are still on the
    /// wire (PrSession.cpp CompleteAttach). Written on the runtime thread, read by the notify sink on MF threads.
    std::atomic<uint64_t> attached{ 0 };

    /// Bumped on the runtime thread (release) just before `attached` is set to a new session. The notify sink stamps each
    /// engine event with the value it read BEFORE it read `attached`, and the event is dropped when the stamp is no longer
    /// current: an event queued for an earlier attach (of this session or another) never lands on a later one, even when
    /// the same session handle is attached again.
    std::atomic<uint64_t> attachGen{ 0 };

    /// The session whose CencMediaSource the engine currently HOLDS (the last successful SetSource), which outlives
    /// `attached` after a detach: the old source stays loaded (paused) until a successor's own SetSource replaces it, so no
    /// empty SetSource ever races the successor's load, or until the grace armed in `releaseGate` runs out and it is
    /// unloaded. Runtime thread only.
    uint64_t engineSource = 0;

    /// The armed unload of `engineSource` (PrSession.cpp ScheduleEngineRelease). Runtime thread only.
    handover::ReleaseGate releaseGate;

    /// Joins destroyed sessions' feeder threads off the runtime thread (PrSession.cpp DestroyInternal); drained before
    /// Media Foundation is shut down and shut down by FgPrRuntimeDestroy.
    handover::FeederReaper reaper;

    bool Ready() const { return bringUp.load(std::memory_order_acquire) == S_OK && !shuttingDown.load(std::memory_order_acquire); }
    int64_t UptimeMs() const { return MsSinceQpc(createdQpc); }
};

struct License
{
    uint64_t handle = 0;
    uint64_t runtime = 0;
    std::wstring kidHex;                 // 32 lowercase hex chars; carried in `text` on every license event
    std::vector<uint8_t> pssh;           // the init data GenerateRequest was given
    FgPrLicenseCallback relay = nullptr;
    void* relayCtx = nullptr;

    // The open CDM key session. Created and closed on the runtime thread, read by KeyStatusChanged on the CDM's thread:
    // every access copies the pointer under `mx` (KeySession()).
    std::mutex mx;
    winrt::com_ptr<IMFContentDecryptionModuleSession> keySession;

    winrt::com_ptr<IMFContentDecryptionModuleSession> KeySession()
    {
        std::lock_guard<std::mutex> g(mx);
        return keySession;
    }

    std::atomic<int32_t> state{ 0 };     // FgPrLicenseState: 0 pending, 1 usable, 2 expired, < 0 the failing HRESULT
    int64_t acquireQpc = 0;
    std::atomic<int64_t> expiresInMs{ 0 };
    std::atomic<int64_t> lastUseQpc{ 0 };  // LRU
    std::atomic<int32_t> bindCount{ 0 };   // attached sessions using this KID — never evicted, never closed while > 0
    std::atomic<int32_t> restriction{ 0 }; // the output restriction last reported (MF_MEDIAKEY_STATUS value, 0 = none)
    std::atomic<bool> releaseRequested{ false };
    std::atomic<bool> closed{ false };
    std::atomic<bool> started{ false };    // StartAcquisition ran (once): the attach verb starts its own licence ahead of itself
};

/// The feeder's wake-up. Separate from Session and held by `shared_ptr` because the CencMediaStream demand hook holds
/// a copy: the hook runs under the stream's lock on an MF thread, and it must never own the Session (dropping the last
/// Session ref there would destroy the source — and the very mutex being held — from inside that lock).
struct FeederSignal
{
    std::mutex mx;                       // a LEAF lock: nothing is ever acquired while holding it
    std::condition_variable cv;
    bool kick = false;

    void Kick()
    {
        {
            std::lock_guard<std::mutex> g(mx);
            kick = true;
        }
        cv.notify_one();
    }
};

struct Session
{
    uint64_t handle = 0;
    uint64_t runtime = 0;

    // ── the descriptor, DEEP-COPIED in FgPrSessionCreate (the caller's pointers do not outlive the call) ─────────────
    std::wstring initUrl, segBase, segPrefix, segSuffix;
    std::wstring audioInitUrl, audioSegBase, audioSegPrefix, audioSegSuffix;
    std::wstring headers;
    std::wstring kidHex;                 // the descriptor's KID, else the init's tenc KID once parsed (diagnostics)
    std::vector<uint8_t> pssh;           // the descriptor's PSSH (licensing takes its own through FgPrLicenseAcquire)
    int32_t startNumber = 0;
    int32_t segmentCount = 0;            // 0 = derive from the duration; unknown both ways = unbounded (a 4xx ends it)
    int32_t segmentStride = 1;
    std::atomic<int32_t> segmentLengthMs{ 0 };   // 0 until known (derived from the first parsed segment)
    int64_t descDurationMs = 0;
    bool startPaused = false;

    std::shared_ptr<SegmentStore> store;

    // ── feeder control ──────────────────────────────────────────────────────────────────────────────────────────────
    // Lock order: feedMx → a CencMediaStream's m_mx. FeederSignal::mx is a LEAF (the stream's demand hook takes it while
    // holding the stream lock).
    std::thread feeder;
    std::shared_ptr<FeederSignal> signal = std::make_shared<FeederSignal>();
    std::atomic<bool> feedStop{ false };

    std::mutex feedMx;
    winrt::com_ptr<CencMediaSource> source;       // feedMx — rebuilt on a re-attach (MF shuts a replaced source down)
    cenc::InitInfo videoInfo, audioInfo;          // feedMx (the feeder writes, attach reads)
    bool haveAudio = false;                       // feedMx
    std::atomic<bool> initsLoaded{ false };
    std::atomic<int32_t> initHr{ S_OK };          // a failed init fetch/parse: the session is in Error
    bool wantInits = false;                       // feedMx — set by Prefetch/Attach
    bool streaming = false;                       // feedMx — attached: keep bufferAheadMs in front of the playhead
    int64_t prefetchAroundMs = -1;                // feedMx — FgPrSessionPrefetch's window: `prefetchSegments` from the
    int32_t prefetchSegments = 0;                 // feedMx   segment containing `prefetchAroundMs` (resolved at plan time)
    bool prefetchAnnounce = false;                // feedMx — a Prefetch is owed an FgPrEvent_Buffered even if nothing is fetched
    std::shared_ptr<HttpFetch> inflightVideo, inflightAudio;   // feedMx — what a seek cancels
    int inflightVideoIndex = -1;                  // feedMx
    int32_t videoEndIndex = INT_MAX, audioEndIndex = INT_MAX;  // feedMx — first index a track answered 404/410 for at or beyond
                                                               //   the segment count (cleared by a seek / structural reset)
    struct RepresentationRequest
    {
        bool pending = false;
        int32_t index = -1;
        int32_t retainMs = -1;                    // < 0 = append after the buffered end; >= 0 = land that far ahead of the playhead
        std::wstring initUrl, base, prefix, suffix;
    } rep;                                        // feedMx

    // ── the start position and the seek (runtime thread writes; feeder reads under feedMx) ──────────────────────────
    std::atomic<int64_t> startPositionMs{ 0 };
    std::atomic<uint64_t> seekSeq{ 0 };           // latest-wins: a reposition only applies if its seq is still current
    int64_t seekTargetMs = -1;                    // feedMx
    int32_t seekMode = FgPrSeekMode_Exact;        // feedMx
    int64_t seekKeyframeMs = -1;                  // feedMx
    bool seekNeedsFetch = false;                  // feedMx — registered as the feeder's next target
    uint64_t flushSeq = 0;                        // feedMx — the seek that last FLUSHED the streams (an unbuffered seek);
                                                  //   a fetch planned under an older seq must not splice into the flushed buffer
    int64_t seekPostedQpc = 0;                    // runtime thread

    // ── attach / transport (runtime thread) ─────────────────────────────────────────────────────────────────────────
    uint64_t license = 0;
    bool attachPending = false;                   // Attach arrived before the init segments: CompleteAttach finishes it
    int64_t attachPostedQpc = 0;
    bool metadataSeen = false;
    bool startCorrectionDone = false;
    fgpr::plan::InternalSeek internalSeek;        // a native-issued SetCurrentTime whose SEEKED is not raised as FgPrEvent_Seeked
    fgpr::plan::WaitGate waitGate;                // the engine's WAITING state (FgPrEvent_Waiting / FgPrEvent_Resumed edges)
    int64_t startCorrectionMs = 0;                // how far behind the carried start the engine clock was at CANPLAY (0 = adopted it)
    bool wantPlay = true;                         // the transport level to apply at attach / after a paused open
    std::atomic<int64_t> volumeMicro{ 1000000 };
    std::atomic<int64_t> rateMicro{ 1000000 };
    int32_t streamWidth = 0, streamHeight = 0;    // FgPrSessionSetStreamSize (0×0 = natural)
    int32_t handleTries = 0;
    int64_t lastPositionRaiseQpc = 0;
    int64_t lastPositionRaisedMs = -1;

    // ── the snapshot (atomics: FgPrSessionSnapshot copies them, alloc-free, from any thread) ────────────────────────
    std::atomic<int32_t> state{ FgPrState_Idle };
    std::atomic<int32_t> errorHr{ 0 };
    std::atomic<int32_t> readyState{ 0 };
    std::atomic<uint64_t> swapchainHandle{ 0 };
    std::atomic<int32_t> width{ 0 }, height{ 0 };
    std::atomic<int32_t> seeking{ 0 };
    // (the representation indices - on screen / downloading - live in the SegmentStore, written by the video stream)
    std::atomic<int64_t> positionMs{ 0 };
    std::atomic<int64_t> positionQpc{ 0 };
    std::atomic<int64_t> durationMs{ 0 };
    std::atomic<int64_t> bufferedAheadMs{ 0 };
    std::atomic<int64_t> retainedBehindMs{ 0 };
    std::atomic<int64_t> firstFrameQpc{ 0 };
    std::atomic<uint64_t> bytesDownloaded{ 0 };
    std::atomic<uint64_t> downloadElapsedMs{ 0 };

    int32_t SegLenMs() const
    {
        const int32_t v = segmentLengthMs.load(std::memory_order_acquire);
        return v > 0 ? v : (segmentStride > 1 ? segmentStride : 1) * 1000;
    }

    void Kick() { signal->Kick(); }
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The handle registry.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
struct Registry
{
    std::mutex mx;
    std::shared_ptr<Runtime> runtime;                                   // one per process
    std::unordered_map<uint64_t, std::shared_ptr<Session>> sessions;
};

inline Registry& Reg()
{
    static Registry* reg = new Registry();   // never destroyed: see Sink()
    return *reg;
}

inline std::shared_ptr<Runtime> RuntimeFor(uint64_t rt)
{
    if (!IsKind(rt, HandleKind::Runtime)) return nullptr;
    std::lock_guard<std::mutex> g(Reg().mx);
    auto r = Reg().runtime;
    return (r && r->handle == rt && !r->shuttingDown.load(std::memory_order_acquire)) ? r : nullptr;
}

inline std::shared_ptr<Session> SessionFor(uint64_t rt, uint64_t s)
{
    if (!IsKind(s, HandleKind::Session)) return nullptr;
    std::lock_guard<std::mutex> g(Reg().mx);
    auto r = Reg().runtime;
    if (!r || r->handle != rt) return nullptr;
    auto it = Reg().sessions.find(s);
    return it == Reg().sessions.end() ? nullptr : it->second;
}

/// The session a notify-sink event belongs to — no runtime-handle check, the sink already knows its runtime.
inline std::shared_ptr<Session> SessionByHandle(uint64_t s)
{
    if (!IsKind(s, HandleKind::Session)) return nullptr;
    std::lock_guard<std::mutex> g(Reg().mx);
    auto it = Reg().sessions.find(s);
    return it == Reg().sessions.end() ? nullptr : it->second;
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Cross-TU entry points.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

// PrRuntime.cpp
void RuntimeItaPreflight(Runtime& rt);                    // runtime thread: the one-time CDM ITA diagnostic

// PrLicense.cpp
void LicenseBind(uint64_t license, int delta);            // attach (+1) / detach (-1); closes a released KID at 0
void LicenseStartIfPending(Runtime& rt, uint64_t license);   // runtime thread: run the not-yet-started acquisition now (the attach verb)
std::shared_ptr<License> LicenseByHandle(uint64_t license);
void LicensesShutdown(Runtime& rt);                       // runtime thread, FgPrRuntimeDestroy: close every key session

// PrSession.cpp
winrt::com_ptr<::IUnknown> SessionSourceForUrl(const wchar_t* url);   // the scheme handler's resolver (any thread)
void SessionPublishHandle(Runtime& rt, Session& s, bool reRaise);      // runtime thread
void SessionOnFormatChange(Runtime& rt, Session& s);                   // runtime thread: a size report, never a re-publication
void SessionSamplePosition(Runtime& rt, Session& s, bool raiseNow);    // runtime thread
void SessionOnCanPlay(Runtime& rt, Session& s);                        // runtime thread
void SessionsShutdown(Runtime& rt);                                    // runtime thread, FgPrRuntimeDestroy

}   // namespace fgpr
