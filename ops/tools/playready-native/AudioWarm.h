// AudioWarm.h — keep the default render endpoint's audio client warm for Media Foundation's audio sink (the race in
// MfAudioGuard.h is amplified when the sink's asynchronous audio-client set-up runs against a cold audio stack).
//
// One worker thread (MTA) per runtime. It warms once at bring-up and again whenever the default console render endpoint
// changes (IMMNotificationClient::OnDefaultDeviceChanged): IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eConsole),
// IMMDevice::Activate(IAudioClient), GetMixFormat, Initialize(shared, mix format), then release. Releasing (not keeping) the
// client: an initialized stream held for the runtime's lifetime would be an open stream on the user's device that does
// nothing; what the warm-up is for - the process's audio-service binding and the endpoint's activation - outlives it.
// It never runs on the runtime thread and the bring-up never waits for it. The attach waits for it ONLY while one is in flight
// (bounded): the first protected Start follows its SetSource, so a warm-up still running then is the one that matters.
#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>

namespace fgpr {

class AudioWarmer
{
  public:
    using LogFn = void (*)(const std::string&);
    using PhaseFn = void (*)(const wchar_t* name, int64_t qpc, int64_t durTicks);

    AudioWarmer() = default;
    AudioWarmer(const AudioWarmer&) = delete;
    AudioWarmer& operator=(const AudioWarmer&) = delete;
    ~AudioWarmer() { Stop(); }

    /// Start the worker and its first warm-up ("bringup"). Idempotent while running.
    void Start(LogFn log, PhaseFn phase)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_thread.joinable()) return;
        m_log = log;
        m_phase = phase;
        m_stop = false;
        m_pending = "bringup";
        m_busy = true;
        m_thread = std::thread([this] { Run(); });
    }

    /// Block until no warm-up is queued or running, at most `maxMs`. Returns how long it waited (0 when idle, the usual case).
    int64_t WaitIdle(int64_t maxMs)
    {
        std::unique_lock<std::mutex> lk(m_mx);
        if (!m_busy) return 0;
        const auto t0 = std::chrono::steady_clock::now();
        m_idleCv.wait_for(lk, std::chrono::milliseconds(maxMs), [this] { return !m_busy || m_stop; });
        return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();
    }

    /// Stop the worker (it unregisters the device notification on its own thread) and join it.
    void Stop()
    {
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (!m_thread.joinable()) return;
            m_stop = true;
        }
        m_cv.notify_all();
        m_thread.join();
        std::lock_guard<std::mutex> g(m_mx);
        m_busy = false;
        m_idleCv.notify_all();
    }

  private:
    /// The default-device notification: queue a warm-up. Called on an audio-service thread; only takes the leaf lock.
    class Notify final : public IMMNotificationClient
    {
      public:
        explicit Notify(AudioWarmer* owner) : m_owner(owner) {}
        STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IMMNotificationClient)) { *ppv = static_cast<IMMNotificationClient*>(this); AddRef(); return S_OK; }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        STDMETHODIMP_(ULONG) AddRef() override { return (ULONG)m_refs.fetch_add(1, std::memory_order_acq_rel) + 1; }
        STDMETHODIMP_(ULONG) Release() override
        {
            const long n = m_refs.fetch_sub(1, std::memory_order_acq_rel) - 1;
            if (n == 0) delete this;
            return (ULONG)n;
        }
        STDMETHODIMP OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR) override
        {
            if (flow == eRender && role == eConsole) m_owner->Kick("device-change");
            return S_OK;
        }
        STDMETHODIMP OnDeviceStateChanged(LPCWSTR, DWORD) override { return S_OK; }
        STDMETHODIMP OnDeviceAdded(LPCWSTR) override { return S_OK; }
        STDMETHODIMP OnDeviceRemoved(LPCWSTR) override { return S_OK; }
        STDMETHODIMP OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override { return S_OK; }

      private:
        std::atomic<long> m_refs{ 1 };
        AudioWarmer* m_owner;
    };

    void Kick(const char* reason)
    {
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_stop) return;
            m_pending = reason;
            m_busy = true;
        }
        m_cv.notify_all();
    }

    void Log(const std::string& s) const { if (m_log) m_log(s); }

    void Run()
    {
        const HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        IMMDeviceEnumerator* enumerator = nullptr;
        Notify* notify = nullptr;
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&enumerator));
        if (SUCCEEDED(hr))
        {
            notify = new (std::nothrow) Notify(this);
            if (notify && FAILED(enumerator->RegisterEndpointNotificationCallback(notify))) { notify->Release(); notify = nullptr; }
        }
        else Log("[audio.warm] no device enumerator hr=" + HexHr(hr) + " - no warm-up");

        for (;;)
        {
            std::string reason;
            {
                std::unique_lock<std::mutex> lk(m_mx);
                if (m_pending.empty() && m_busy) { m_busy = false; m_idleCv.notify_all(); }
                m_cv.wait(lk, [this] { return m_stop || !m_pending.empty(); });
                if (m_stop) break;
                reason.swap(m_pending);
            }
            if (enumerator) WarmOnce(enumerator, reason);
        }

        if (enumerator && notify) enumerator->UnregisterEndpointNotificationCallback(notify);
        if (notify) notify->Release();
        if (enumerator) enumerator->Release();
        if (SUCCEEDED(hrCo)) CoUninitialize();
    }

    void WarmOnce(IMMDeviceEnumerator* enumerator, const std::string& reason)
    {
        LARGE_INTEGER f, t0, t1, t2;
        QueryPerformanceFrequency(&f);
        QueryPerformanceCounter(&t0);
        if (m_phase) m_phase(L"audio.warm.begin", t0.QuadPart, 0);
        IMMDevice* device = nullptr;
        IAudioClient* client = nullptr;
        WAVEFORMATEX* mix = nullptr;
        HRESULT hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
        if (SUCCEEDED(hr)) hr = device->Activate(__uuidof(IAudioClient), CLSCTX_INPROC_SERVER, nullptr, reinterpret_cast<void**>(&client));
        QueryPerformanceCounter(&t1);
        if (SUCCEEDED(hr)) hr = client->GetMixFormat(&mix);
        // 20 ms shared-mode buffer, never started: Initialize is what reaches the endpoint's audio engine.
        if (SUCCEEDED(hr)) hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 200000, 0, mix, nullptr);
        QueryPerformanceCounter(&t2);
        if (mix) CoTaskMemFree(mix);
        if (client) client->Release();
        if (device) device->Release();
        const int64_t ticks = t2.QuadPart - t0.QuadPart;
        if (m_phase) m_phase(L"audio.warm.end", t2.QuadPart, ticks);
        const auto ms = [&](int64_t d) { return std::to_string((long long)(d * 1000 / (f.QuadPart ? f.QuadPart : 1))); };
        Log("[audio.warm] default render endpoint (console) reason=" + reason + " hr=" + HexHr(hr) + " ms=" + ms(ticks) +
            " (activate " + ms(t1.QuadPart - t0.QuadPart) + " + init " + ms(t2.QuadPart - t1.QuadPart) + ")");
    }

    static std::string HexHr(HRESULT h)
    {
        char b[16];
        snprintf(b, sizeof(b), "0x%x", (unsigned)(uint32_t)h);
        return b;
    }

    std::mutex m_mx;                    // a leaf: the notification takes it from an audio-service thread
    std::condition_variable m_cv, m_idleCv;
    std::thread m_thread;
    std::string m_pending;              // the next warm-up's reason ("" = none queued)
    bool m_busy = false;                // a warm-up is queued or running
    bool m_stop = false;
    LogFn m_log = nullptr;
    PhaseFn m_phase = nullptr;
};

}   // namespace fgpr
