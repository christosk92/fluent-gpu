// PrRuntime.cpp — the process-lifetime PlayReady runtime: FgPrRuntimeCreate / FgPrRuntimeDestroy / FgPrRuntimeUptimeMs.
//
// WHAT MOVED HERE, AND WHY IT IS DONE ONCE (wavee-0.3-video-engine-implementation.md §1.3, §3.1.1). The one-shot
// FgPlayReadyRunEx did CoInitializeEx + MFStartup, D3D11CreateDevice + the DXGI manager, CreateAndPrepareCdm (which
// spins up mfpmp.exe), the protection manager and the IMFMediaEngine for EVERY source, and tore all of it down again
// when the source ended — so a song→video switch paid the whole bring-up, every time. All of that is below, moved
// verbatim, and now runs ONCE on the runtime's own MTA thread. Two things changed on the way:
//   * the MF_MEDIA_ENGINE_EXTENSION scheme handler resolves `cenc://fluentgpu/<sessionId>` to THAT session's source
//     (it was already parametrised by URL; it just only ever had one object to hand back), and
//   * the notify sink raises an FgPrEvent for every interesting media-engine event instead of setting a flag that a
//     100 ms / 60 ms / 80 ms poll read later.
// The runtime thread itself is an event-driven work queue (PrInternal.h WorkQueue) — the shape of the managed
// VideoMediaEngine.WakeEngine — not the 80 ms keep-alive loop, which is deleted.
#include "PrInternal.h"

using fgpr::Raise;

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  PMP host bridge (moved verbatim).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

// Missing from the Windows SDK (confirmed by Microsoft's PlayReady architect in media-foundation#37), but required
// by the normal Win32 IMFPMPHost -> IMFPMPHostApp bridge used by Firefox/Chromium.
static const GUID kGuidObjectStream =
    { 0x3e73735c, 0xe6c0, 0x481d, { 0x82, 0x60, 0xee, 0x5d, 0xb1, 0x34, 0x3b, 0x5f } };
static const GUID kGuidClassName =
    { 0x77631a31, 0xe5e7, 0x4785, { 0xbf, 0x17, 0x20, 0xf5, 0x7b, 0x22, 0x48, 0x02 } };
static const GUID kClsidEmeStoreActivate =
    { 0x2df7b51e, 0x797b, 0x4d06, { 0xbe, 0x71, 0xd1, 0x4a, 0x52, 0xcf, 0x84, 0x21 } };

// Ordinary desktop processes receive IMFPMPHost (not IMFPMPHostApp) from the OS CDM service. Adapt it using the exact
// browser-proven activation envelope: runtime-class name + optional object stream -> serialized MF attributes ->
// CLSID_EMEStoreActivate -> IMFActivate::ActivateObject. No UWP/package/AppContainer is involved.
struct DesktopPmpHostApp : winrt::implements<DesktopPmpHostApp, IMFPMPHostApp>
{
    winrt::com_ptr<IMFPMPHost> m_host;
    explicit DesktopPmpHostApp(IMFPMPHost* host) { m_host.copy_from(host); }

    IFACEMETHODIMP LockProcess() noexcept override { return m_host ? m_host->LockProcess() : E_FAIL; }
    IFACEMETHODIMP UnlockProcess() noexcept override { return m_host ? m_host->UnlockProcess() : E_FAIL; }

    IFACEMETHODIMP ActivateClassById(LPCWSTR id, IStream* input, REFIID riid, void** activated) noexcept override
    {
        if (!id || !activated || !m_host) return E_POINTER;
        *activated = nullptr;
        auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };
        LogLine("[pmp-wrap] ActivateClassById id=" + fgpr::Narrow(id));

        winrt::com_ptr<IMFAttributes> attrs;
        HRESULT hr = MFCreateAttributes(attrs.put(), 2);
        if (FAILED(hr)) return hr;
        if (FAILED(hr = attrs->SetString(kGuidClassName, id))) return hr;

        if (input)
        {
            STATSTG stat{};
            if (FAILED(hr = input->Stat(&stat, STATFLAG_NOOPEN | STATFLAG_NONAME))) return hr;
            if (stat.cbSize.HighPart != 0) return E_INVALIDARG;
            if (stat.cbSize.LowPart)
            {
                std::vector<uint8_t> blob(stat.cbSize.LowPart);
                ULONG read = 0;
                if (FAILED(hr = input->Read(blob.data(), (ULONG)blob.size(), &read))) return hr;
                if (read > blob.size()) return E_UNEXPECTED;
                if (FAILED(hr = attrs->SetBlob(kGuidObjectStream, blob.data(), read))) return hr;
            }
        }

        winrt::com_ptr<IStream> serialized;
        if (FAILED(hr = CreateStreamOnHGlobal(nullptr, TRUE, serialized.put()))) return hr;
        if (FAILED(hr = MFSerializeAttributesToStream(attrs.get(), 0, serialized.get()))) return hr;
        LARGE_INTEGER zero{};
        if (FAILED(hr = serialized->Seek(zero, STREAM_SEEK_SET, nullptr))) return hr;

        winrt::com_ptr<IMFActivate> activator;
        if (FAILED(hr = m_host->CreateObjectByCLSID(kClsidEmeStoreActivate, serialized.get(),
                                                    __uuidof(IMFActivate), (void**)activator.put())))
        {
            LogLine("[pmp-wrap] CreateObjectByCLSID(EMEStoreActivate) hr=" + hx(hr));
            return hr;
        }
        hr = activator->ActivateObject(riid, activated);
        LogLine("[pmp-wrap] ActivateObject hr=" + hx(hr));
        return hr;
    }
};

// Create + prepare the modern CDM (factory4 -> CDM factory -> access -> CDM with explicit store path -> SetPMPHostApp).
// Faithful to the PROVEN ProbeSetPmpHostAppInUwp sequence, but returns the CDM kept alive for the media engine.
// `failHr` receives the HRESULT of the step that failed (the runtime reports it as FgPrEvent_RuntimeFailed).
static IMFContentDecryptionModule* CreateAndPrepareCdm(const wchar_t* keySystem, const std::wstring& storePath,
                                                       HRESULT* failHr)
{
    auto hx = [](HRESULT h){ std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };
    IMFMediaEngineClassFactory* baseFactory = nullptr;
    IMFMediaEngineClassFactory4* factory4 = nullptr;
    IMFContentDecryptionModuleFactory* cdmFactory = nullptr;
    IMFContentDecryptionModuleAccess* cdmAccess = nullptr;
    IMFContentDecryptionModule* cdm = nullptr;
    HRESULT hr = S_OK;
    do {
        if (FAILED(hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&baseFactory)))) { LogLine("[eme-cdm] CoCreate factory hr=" + hx(hr)); break; }
        if (FAILED(hr = baseFactory->QueryInterface(IID_PPV_ARGS(&factory4)))) { LogLine("[eme-cdm] QI factory4 hr=" + hx(hr)); break; }
        GUID iidCdmFac = __uuidof(IMFContentDecryptionModuleFactory);
        if (FAILED(hr = factory4->CreateContentDecryptionModuleFactory(keySystem, iidCdmFac, (void**)&cdmFactory)) || !cdmFactory) { LogLine("[eme-cdm] CreateCdmFactory hr=" + hx(hr)); if (SUCCEEDED(hr)) hr = E_NOINTERFACE; break; }

        IPropertyStore* cfg = nullptr; PSCreateMemoryPropertyStore(IID_PPV_ARGS(&cfg));
        if (!cfg) { hr = E_OUTOFMEMORY; break; }
        auto setVecBstr = [&](const PROPERTYKEY& key, const wchar_t* one){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); BSTR* arr=(BSTR*)CoTaskMemAlloc(sizeof(BSTR)); arr[0]=SysAllocString(one); pv.vt=VT_VECTOR|VT_BSTR; pv.cabstr.cElems=1; pv.cabstr.pElems=arr; cfg->SetValue(key,pv); PropVariantClear(&pv); };
        auto setVecUI4x2 = [&](const PROPERTYKEY& key, ULONG a, ULONG b){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); ULONG* arr=(ULONG*)CoTaskMemAlloc(sizeof(ULONG)*2); arr[0]=a; arr[1]=b; pv.vt=VT_VECTOR|VT_UI4; pv.caul.cElems=2; pv.caul.pElems=arr; cfg->SetValue(key,pv); PropVariantClear(&pv); };
        auto setUI4      = [&](const PROPERTYKEY& key, ULONG v){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); pv.vt=VT_UI4; pv.ulVal=v; cfg->SetValue(key,pv); };
        auto setEmptyVec = [&](const PROPERTYKEY& key){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); pv.vt=VT_VECTOR|VT_VARIANT; pv.capropvar.cElems=0; cfg->SetValue(key,pv); };
        setVecBstr(MF_EME_INITDATATYPES, L"cenc");
        setEmptyVec(MF_EME_AUDIOCAPABILITIES);
        setEmptyVec(MF_EME_VIDEOCAPABILITIES);
        setUI4(MF_EME_DISTINCTIVEID, MF_MEDIAKEYS_REQUIREMENT_OPTIONAL);
        setUI4(MF_EME_PERSISTEDSTATE, MF_MEDIAKEYS_REQUIREMENT_OPTIONAL);
        // Both session types stay DECLARED — this is the CDM configuration every on-box proof ran with. Only TEMPORARY
        // sessions are ever created now (PrLicense.cpp: Spotify issues non-persistable streaming licenses); the
        // persistent-license A/B arm (FG_CENC_PERSIST_SESSION) that needed PERSISTENT_LICENSE here was deleted, but
        // narrowing a proven access configuration is not a change this rework makes blind.
        setVecUI4x2(MF_EME_SESSIONTYPES,
                    (ULONG)MF_MEDIAKEYSESSION_TYPE_TEMPORARY, (ULONG)MF_MEDIAKEYSESSION_TYPE_PERSISTENT_LICENSE);
        IPropertyStore* cfgArr = cfg;
        hr = cdmFactory->CreateContentDecryptionModuleAccess(keySystem, &cfgArr, 1, &cdmAccess);
        cfg->Release();
        if (FAILED(hr) || !cdmAccess) { LogLine("[eme-cdm] CreateCdmAccess hr=" + hx(hr)); if (SUCCEEDED(hr)) hr = E_NOINTERFACE; break; }

        IPropertyStore* cdmProps = nullptr; PSCreateMemoryPropertyStore(IID_PPV_ARGS(&cdmProps));
        if (!cdmProps) { hr = E_OUTOFMEMORY; break; }
        { PROPERTYKEY k = MF_CONTENTDECRYPTIONMODULE_STOREPATH; PROPVARIANT sp; InitPropVariantFromString(storePath.c_str(), &sp); cdmProps->SetValue(k, sp); PropVariantClear(&sp); }
        hr = cdmAccess->CreateContentDecryptionModule(cdmProps, &cdm);
        cdmProps->Release();
        if (FAILED(hr) || !cdm) { LogLine("[eme-cdm] CreateCdm hr=" + hx(hr)); cdm = nullptr; if (SUCCEEDED(hr)) hr = E_NOINTERFACE; break; }
        LogLine("[eme-cdm] IMFContentDecryptionModule CREATED.");

        // SetPMPHostApp (proven S_FALSE=success in genuine-UWP) — required before GenerateRequest / protected decode.
        IMFGetService* svc = nullptr;
        if (SUCCEEDED(cdm->QueryInterface(IID_PPV_ARGS(&svc))) && svc)
        {
            IMFPMPHostApp* hostApp = nullptr;
            HRESULT hrApp = svc->GetService(MF_CONTENTDECRYPTIONMODULE_SERVICE, __uuidof(IMFPMPHostApp), (void**)&hostApp);
            if (hostApp)
            {
                HRESULT hrSet = cdm->SetPMPHostApp(hostApp);
                LogLine("[eme-cdm] SetPMPHostApp(direct) hr=" + hx(hrSet));
                hostApp->Release();
            }
            else
            {
                winrt::com_ptr<IMFPMPHost> host;
                HRESULT hrHost = svc->GetService(MF_CONTENTDECRYPTIONMODULE_SERVICE, __uuidof(IMFPMPHost),
                                                 (void**)host.put());
                LogLine("[eme-cdm] direct IMFPMPHostApp hr=" + hx(hrApp) +
                        "; IMFPMPHost hr=" + hx(hrHost));
                if (SUCCEEDED(hrHost) && host)
                {
                    auto wrapper = winrt::make_self<DesktopPmpHostApp>(host.get());
                    HRESULT hrSet = cdm->SetPMPHostApp(wrapper.get());
                    LogLine("[eme-cdm] SetPMPHostApp(desktop wrapper) hr=" + hx(hrSet));
                    if (FAILED(hrSet)) { cdm->Release(); cdm = nullptr; hr = hrSet; }
                }
            }
            svc->Release();
        }
    } while (false);
    if (cdmAccess) cdmAccess->Release();
    if (cdmFactory) cdmFactory->Release();
    if (factory4) factory4->Release();
    if (baseFactory) baseFactory->Release();
    if (!cdm && failHr) *failHr = FAILED(hr) ? hr : E_FAIL;
    return cdm;
}

// The protection manager the media engine talks to: implements IMFContentProtectionManager + WinRT
// IMediaProtectionManager, exposes the CDM's PMP server, and routes content-enabler requests to the CDM.
// (Direct port of Microsoft's MediaEngineEMEUWPSample MediaEngineProtectionManager.)
struct MediaEngineProtectionManager
    : winrt::implements<MediaEngineProtectionManager, IMFContentProtectionManager, winrt::Windows::Media::Protection::IMediaProtectionManager>
{
    winrt::com_ptr<IMFContentDecryptionModule> m_cdm;
    winrt::Windows::Foundation::Collections::PropertySet m_props;

    MediaEngineProtectionManager(IMFContentDecryptionModule* cdm)
    {
        m_cdm.copy_from(cdm);
        winrt::com_ptr<IMFGetService> svc = m_cdm.as<IMFGetService>();
        winrt::com_ptr<ABI::Windows::Media::Protection::IMediaProtectionPMPServer> abiPmp;
        winrt::check_hresult(svc->GetService(MF_CONTENTDECRYPTIONMODULE_SERVICE, IID_PPV_ARGS(abiPmp.put())));
        winrt::Windows::Media::Protection::MediaProtectionPMPServer pmp{ nullptr };
        winrt::copy_from_abi(pmp, abiPmp.get());
        auto map = m_props.as<winrt::Windows::Foundation::Collections::IMap<winrt::hstring, winrt::Windows::Foundation::IInspectable>>();
        map.Insert(L"Windows.Media.Protection.MediaProtectionPMPServer", pmp);
        // Firefox's working desktop MFCDM path inserts ONLY the PMP-server property (gecko
        // MFContentProtectionManager::SetPMPServer) — the SW/HW protection layer is decided by the CDM's own
        // configuration, not the protection manager. Forcing UseSoftwareProtectionLayer here creates a protection
        // context that diverges from the CDM's PMP server and broke the protected-stream (MF_SD_PROTECTED + wrapped
        // MFMediaType_Protected) topology with DRM_E_CH_BAD_KEY. (The FG_CENC_FORCE_SW_LAYER A/B arm is deleted.)
        LogLine("[eme-pm] protection manager ready (PMP server from CDM; layer per CDM config).");
    }

    // IMFContentProtectionManager
    IFACEMETHODIMP BeginEnableContent(IMFActivate* enablerActivate, IMFTopology*, IMFAsyncCallback* callback, ::IUnknown* state) noexcept override
    {
        auto hx = [](HRESULT h){ std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };
        winrt::com_ptr<::IUnknown> obj;
        winrt::com_ptr<IMFAsyncResult> asyncResult;
        HRESULT hr = MFCreateAsyncResult(nullptr, callback, state, asyncResult.put());
        if (FAILED(hr)) return hr;
        hr = enablerActivate->ActivateObject(IID_PPV_ARGS(obj.put()));
        if (FAILED(hr)) { LogLine("[eme-pm] ActivateObject hr=" + hx(hr)); return hr; }
        GUID enablerType = GUID_NULL;
        winrt::com_ptr<IMFContentEnabler> enabler = obj.try_as<IMFContentEnabler>();
        if (enabler) enabler->GetEnableType(&enablerType);
        {
            wchar_t b[64] = {};
            StringFromGUID2(enablerType, b, 64);
            LogLine("[eme-pm] BeginEnableContent enablerType=" + fgpr::Narrow(b));
        }
        if (enablerType == MFENABLETYPE_MF_RebootRequired) return MF_E_REBOOT_REQUIRED;
        if (enablerType == MFENABLETYPE_MF_UpdateRevocationInformation) return MF_E_GRL_VERSION_TOO_LOW;
        if (enablerType == MFENABLETYPE_MF_UpdateUntrustedComponent) return HRESULT_FROM_WIN32(ERROR_INVALID_IMAGE_HASH);
        hr = m_cdm->SetContentEnabler(enabler.get(), asyncResult.get());
        LogLine("[eme-pm] SetContentEnabler hr=" + hx(hr));
        return hr;
    }
    IFACEMETHODIMP EndEnableContent(IMFAsyncResult*) noexcept override { return S_OK; }

    // IMediaProtectionManager (only Properties() is consumed by the media engine; events are unused).
    winrt::event_token ServiceRequested(winrt::Windows::Media::Protection::ServiceRequestedEventHandler const) { throw winrt::hresult_not_implemented(); }
    void ServiceRequested(winrt::event_token const) {}
    winrt::event_token RebootNeeded(winrt::Windows::Media::Protection::RebootNeededEventHandler const) { throw winrt::hresult_not_implemented(); }
    void RebootNeeded(winrt::event_token const) {}
    winrt::event_token ComponentLoadFailed(winrt::Windows::Media::Protection::ComponentLoadFailedEventHandler const) { throw winrt::hresult_not_implemented(); }
    void ComponentLoadFailed(winrt::event_token const) {}
    winrt::Windows::Foundation::Collections::PropertySet Properties() { return m_props; }
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The media engine's COM callbacks.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

static void OnEngineEvent(fgpr::Runtime& rt, uint64_t sessionHandle, DWORD ev, DWORD_PTR p1, DWORD p2, int64_t qpc);

// The notify sink. It used to set atomics (metadata/canplay/playing/error) that three polls read on the MTA thread
// (CANPLAY every 100 ms up to 45 s, the swap-chain handle every 60 ms up to 12 s, transport every 80 ms). Now every
// interesting event becomes ONE posted work item on the runtime thread, which raises the FgPrEvent and touches the
// engine — engine calls are never made from inside EventNotify, where the engine may hold its own locks.
//
// The event is attributed to the session attached AT NOTIFY TIME and dropped if a different session is attached by
// the time the item runs: a detach's own PAUSE (or a predecessor's late event) must never land on its successor.
struct MediaEngineNotify : public IMFMediaEngineNotify
{
    std::atomic<long> rc{1};
    uint64_t m_runtime = 0;

    explicit MediaEngineNotify(uint64_t runtime) : m_runtime(runtime) {}

    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(::IUnknown) || riid == __uuidof(IMFMediaEngineNotify)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (v == 0) delete this; return (ULONG)v; }
    HRESULT __stdcall EventNotify(DWORD ev, DWORD_PTR p1, DWORD p2) override
    {
        switch (ev)
        {
            case MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA:
            case MF_MEDIA_ENGINE_EVENT_CANPLAY:
            case MF_MEDIA_ENGINE_EVENT_FIRSTFRAMEREADY:
            case MF_MEDIA_ENGINE_EVENT_SEEKING:
            case MF_MEDIA_ENGINE_EVENT_SEEKED:
            case MF_MEDIA_ENGINE_EVENT_PLAYING:
            case MF_MEDIA_ENGINE_EVENT_PAUSE:
            case MF_MEDIA_ENGINE_EVENT_ENDED:
            case MF_MEDIA_ENGINE_EVENT_ERROR:
            case MF_MEDIA_ENGINE_EVENT_FORMATCHANGE:
            case MF_MEDIA_ENGINE_EVENT_RESOURCELOST:
                break;
            default:
                return S_OK;
        }
        const int64_t qpc = fgpr::QpcNow();   // FIRSTFRAMEREADY's timestamp is taken HERE, not when the item runs
        std::shared_ptr<fgpr::Runtime> rt = fgpr::RuntimeFor(m_runtime);
        if (!rt) return S_OK;
        const uint64_t session = rt->attached.load(std::memory_order_acquire);
        if (!session) return S_OK;
        fgpr::Runtime* raw = rt.get();   // the runtime thread owns a strong ref for as long as it runs items
        rt->queue.Post([raw, session, ev, p1, p2, qpc] { OnEngineEvent(*raw, session, ev, p1, p2, qpc); });
        return S_OK;
    }
};

// MF_MEDIA_ENGINE_EXTENSION: the `cenc://` scheme handler. It was already parametrised by the URL the engine passes
// in; the runtime now has many sources, so the URL names one: `cenc://fluentgpu/<sessionId>` → THAT session's current
// CencMediaSource (PrSession.cpp SessionSourceForUrl). An unknown or destroyed session is MF_E_UNSUPPORTED_BYTESTREAM_TYPE,
// which the engine reports as a source error on the attach that asked for it — never a different session's source.
struct CencEngineExtension : public IMFMediaEngineExtension
{
    std::atomic<long> rc{1};
    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(::IUnknown) || riid == __uuidof(IMFMediaEngineExtension)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (!v) delete this; return (ULONG)v; }
    HRESULT __stdcall CanPlayType(BOOL, BSTR, MF_MEDIA_ENGINE_CANPLAY* answer) override
    { if (answer) *answer = MF_MEDIA_ENGINE_CANPLAY_PROBABLY; return S_OK; }
    HRESULT __stdcall BeginCreateObject(BSTR url, IMFByteStream*, MF_OBJECT_TYPE type, IUnknown** cancelCookie,
                                        IMFAsyncCallback* callback, IUnknown* state) override
    {
        if (cancelCookie) *cancelCookie = nullptr;
        if (type != MF_OBJECT_MEDIASOURCE) return MF_E_UNSUPPORTED_BYTESTREAM_TYPE;
        winrt::com_ptr<::IUnknown> source = fgpr::SessionSourceForUrl(url);
        LogLine("[cenc-ext] BeginCreateObject url=" + fgpr::Narrow(url ? std::wstring(url) : std::wstring()) +
                (source ? " -> session source" : " -> NO SUCH SESSION"));
        if (!source) return MF_E_UNSUPPORTED_BYTESTREAM_TYPE;
        IMFAsyncResult* result = nullptr;
        HRESULT hr = MFCreateAsyncResult(source.get(), callback, state, &result);
        if (FAILED(hr)) return hr;
        result->SetStatus(S_OK);
        hr = MFInvokeCallback(result);
        result->Release();
        return hr;
    }
    HRESULT __stdcall CancelObjectCreation(IUnknown*) override { return S_OK; }
    HRESULT __stdcall EndCreateObject(IMFAsyncResult* result, IUnknown** ppObject) override
    {
        if (!ppObject) return E_POINTER;
        *ppObject = nullptr;
        HRESULT hr = result ? result->GetStatus() : E_UNEXPECTED;
        if (SUCCEEDED(hr) && result) { IUnknown* o = nullptr; result->GetObject(&o); *ppObject = o; }
        return hr;
    }
};

// The engine's NeedKey callback. The custom CENC source does not surface a PSSH to the engine, so on the proven wiring
// this has never fired; licensing is PrLicense.cpp's proactive, KID-keyed acquisition. It stays registered because it
// was part of that proven engine wiring, and a session attached with a still-PENDING license relies on the engine's
// own key-wait (the samples simply wait in the PMP decryptor) rather than on a callback here. It only logs.
struct EmeNeedKeyNotify : public IMFMediaEngineNeedKeyNotify
{
    std::atomic<long> rc{1};
    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(::IUnknown) || riid == __uuidof(IMFMediaEngineNeedKeyNotify)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (!v) delete this; return (ULONG)v; }
    void __stdcall NeedKey(const BYTE*, DWORD cb) override
    {
        LogLine("[eme] NeedKey initData=" + std::to_string(cb) + "B (licensing is proactive; the engine waits for the key)");
    }
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Bring-up and teardown (runtime thread).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

static HRESULT BringUp(fgpr::Runtime& rt)
{
    auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    rt.comInitialized = SUCCEEDED(hr);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) { LogLine("[runtime] CoInitializeEx(MTA) hr=" + hx(hr)); return hr; }
    hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(hr)) { LogLine("[runtime] MFStartup hr=" + hx(hr)); return hr; }
    rt.mfStarted = true;

    // ── D3D11 + MF DXGI manager (shared, multithread-protected). ─────────────────────────────────────────────────────
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    {
        ID3D11Device* d3d = nullptr; ID3D11DeviceContext* ctx = nullptr;
        if (FAILED(hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, &ctx)))
        { LogLine("[cenc] D3D11CreateDevice hr=" + hx(hr)); return hr; }
        if (ctx) ctx->Release();
        rt.d3d.attach(d3d);
    }
    // ID3D10Multithread::SetMultithreadProtected(TRUE): the media engine's decoder, the video processor and the DXGI
    // manager all share this device from different MF worker threads. The interface is reached by its IID and called
    // through the vtable because d3d10.h is not otherwise needed here — and the slot is FIVE, not three:
    // IUnknown occupies 0-2 (QueryInterface, AddRef, Release), then Enter (3), Leave (4), SetMultithreadProtected (5),
    // GetMultithreadProtected (6). Calling slot 3 would Enter the device's critical section and never leave it.
    {   void* mt = nullptr; GUID iidMt = { 0x9b7e4e00, 0x342c, 0x4106, {0xa1,0x9f,0x4f,0x27,0x04,0xf6,0x89,0xf0} };
        if (SUCCEEDED(rt.d3d->QueryInterface(iidMt, &mt)) && mt)
        { auto setProt = (int (STDMETHODCALLTYPE*)(void*, int))(*(void***)mt)[5]; setProt(mt, 1); ((::IUnknown*)mt)->Release(); } }
    if (FAILED(hr = MFCreateDXGIDeviceManager(&rt.resetToken, rt.dxgiManager.put()))) { LogLine("[cenc] MFCreateDXGIDeviceManager hr=" + hx(hr)); return hr; }
    if (FAILED(hr = rt.dxgiManager->ResetDevice(rt.d3d.get(), rt.resetToken))) { LogLine("[cenc] ResetDevice hr=" + hx(hr)); return hr; }

    // ── CDM (proven S_FALSE SetPMPHostApp) + protection manager. ────────────────────────────────────────────────────
    CreateDirectoryW(rt.storePath.c_str(), nullptr);
    HRESULT cdmHr = E_FAIL;
    IMFContentDecryptionModule* cdm = CreateAndPrepareCdm(L"com.microsoft.playready.recommendation", rt.storePath, &cdmHr);
    if (!cdm) { LogLine("[cenc] CDM creation failed hr=" + hx(cdmHr)); return cdmHr; }
    rt.cdm.attach(cdm);

    try
    {
        auto pm = winrt::make_self<MediaEngineProtectionManager>(rt.cdm.get());
        rt.protectionManager = pm.as<IMFContentProtectionManager>();
    }
    catch (winrt::hresult_error const& e) { LogLine("[cenc] protection manager ctor hr=" + hx(e.code().value)); return e.code().value; }

    // ── The ONE media engine, wired to the scheme handler, the DXGI manager and the CDM. ────────────────────────────
    winrt::com_ptr<IMFMediaEngineClassFactory> factory;
    if (FAILED(hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(factory.put()))))
    { LogLine("[cenc] CoCreate factory hr=" + hx(hr)); return hr; }

    rt.extension.attach(new CencEngineExtension());
    rt.notify.attach(new MediaEngineNotify(rt.handle));
    rt.needKey.attach(new EmeNeedKeyNotify());

    winrt::com_ptr<IMFAttributes> attrs;
    if (FAILED(hr = MFCreateAttributes(attrs.put(), 10))) return hr;
    attrs->SetUnknown(MF_MEDIA_ENGINE_CALLBACK, rt.notify.get());
    attrs->SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER, rt.dxgiManager.get());
    attrs->SetUnknown(MF_MEDIA_ENGINE_EXTENSION, rt.extension.get());
    attrs->SetUnknown(MF_MEDIA_ENGINE_NEEDKEY_CALLBACK, rt.needKey.get());
    // Firefox-exact protected wiring (gecko MFMediaEngineParent::CreateMediaEngine + RecvSetCDMProxy): the creation
    // attributes carry ONLY MF_MEDIA_ENGINE_ENABLE_PROTECTED_CONTENT; the protection manager is attached AFTER engine
    // creation via IMFMediaEngineProtectedContent::SetContentProtectionManager (below). The old wiring additionally
    // passed the manager as a creation attribute + USE_PMP_FOR_ALL_CONTENT; that A/B arm (FG_CENC_LEGACY_ENGINE_WIRING)
    // is deleted.
    attrs->SetUINT32(MF_MEDIA_ENGINE_CONTENT_PROTECTION_FLAGS, MF_MEDIA_ENGINE_ENABLE_PROTECTED_CONTENT);
    attrs->SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM);
    hr = factory->CreateInstance(0, attrs.get(), rt.engine.put());
    LogLine("[cenc] CreateInstance(engine) hr=" + hx(hr));
    if (FAILED(hr) || !rt.engine) return FAILED(hr) ? hr : E_NOINTERFACE;

    rt.protectedContent = rt.engine.try_as<IMFMediaEngineProtectedContent>();
    if (rt.protectedContent)
    {
        HRESULT hs = rt.protectedContent->SetContentProtectionManager(rt.protectionManager.get());
        LogLine("[cenc] SetContentProtectionManager hr=" + hx(hs));
        if (FAILED(hs)) return hs;
    }
    else { LogLine("[cenc] QI IMFMediaEngineProtectedContent FAILED."); return E_NOINTERFACE; }

    rt.engineEx = rt.engine.try_as<IMFMediaEngineEx>();
    if (!rt.engineEx) { LogLine("[cenc] QI IMFMediaEngineEx FAILED."); return E_NOINTERFACE; }
    // Windowless swap-chain mode: the engine presents decoded frames into its own DComp-shareable swap chain. It does so
    // ON ITS OWN — the old keep-alive's per-tick OnVideoStreamTick + UpdateVideoStream(nullptr, …) was never needed in
    // this mode (the clear VideoMediaEngine never makes those calls) and is deleted.
    hr = rt.engineEx->EnableWindowlessSwapchainMode(TRUE);
    LogLine("[cenc] EnableWindowlessSwapchainMode hr=" + hx(hr));
    // Preload AUTOMATIC so an attach that opens PAUSED (FgPrOpenDesc.startPaused) still loads to CANPLAY and can
    // FrameStep its first frame out; autoplay stays off — Play is always an explicit transport verb.
    rt.engine->SetPreload(MF_MEDIA_ENGINE_PRELOAD_AUTOMATIC);
    return S_OK;
}

static void TearDown(fgpr::Runtime& rt)
{
    fgpr::SessionsShutdown(rt);
    fgpr::LicensesShutdown(rt);
    if (rt.engine)
    {
        HRESULT hr = rt.engine->Shutdown();
        LogLine("[runtime] engine Shutdown hr=" + fgpr::Hex(hr));
    }
    rt.protectedContent = nullptr;
    rt.engineEx = nullptr;
    rt.engine = nullptr;
    rt.needKey = nullptr;
    rt.notify = nullptr;
    rt.extension = nullptr;
    rt.protectionManager = nullptr;
    rt.cdm = nullptr;
    rt.dxgiManager = nullptr;
    rt.d3d = nullptr;
    if (rt.mfStarted) { MFShutdown(); rt.mfStarted = false; }
    if (rt.comInitialized) { CoUninitialize(); rt.comInitialized = false; }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Engine events → FgPrEvents (runtime thread).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

static void OnEngineEvent(fgpr::Runtime& rt, uint64_t sessionHandle, DWORD ev, DWORD_PTR p1, DWORD p2, int64_t qpc)
{
    if (!rt.Ready() || rt.attached.load(std::memory_order_acquire) != sessionHandle) return;
    std::shared_ptr<fgpr::Session> sp = fgpr::SessionByHandle(sessionHandle);
    if (!sp) return;
    fgpr::Session& s = *sp;
    const uint64_t h = s.handle;

    switch (ev)
    {
        case MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA:
        {
            s.metadataSeen = true;
            const double dur = rt.engine->GetDuration();
            if (std::isfinite(dur) && dur > 0.0) s.durationMs.store((int64_t)(dur * 1000.0), std::memory_order_release);
            DWORD w = 0, hgt = 0;
            rt.engine->GetNativeVideoSize(&w, &hgt);
            if (w && hgt)
            {
                s.width.store((int32_t)w, std::memory_order_release);
                s.height.store((int32_t)hgt, std::memory_order_release);
            }
            s.readyState.store((int32_t)rt.engine->GetReadyState(), std::memory_order_release);
            Raise(h, FgPrEvent_Metadata, s.durationMs.load(std::memory_order_acquire),
                  ((int64_t)s.width.load(std::memory_order_acquire) << 32) | (int64_t)(uint32_t)s.height.load(std::memory_order_acquire));
            fgpr::SessionPublishHandle(rt, s, false);
            break;
        }
        case MF_MEDIA_ENGINE_EVENT_CANPLAY:
            s.readyState.store((int32_t)rt.engine->GetReadyState(), std::memory_order_release);
            Raise(h, FgPrEvent_CanPlay);
            fgpr::SessionOnCanPlay(rt, s);
            fgpr::SessionPublishHandle(rt, s, false);
            break;
        case MF_MEDIA_ENGINE_EVENT_FIRSTFRAMEREADY:
        {
            int64_t expected = 0;
            s.firstFrameQpc.compare_exchange_strong(expected, qpc, std::memory_order_acq_rel);
            fgpr::SessionSamplePosition(rt, s, false);
            Raise(h, FgPrEvent_FirstFrame, s.positionMs.load(std::memory_order_acquire));
            fgpr::SessionPublishHandle(rt, s, false);
            break;
        }
        case MF_MEDIA_ENGINE_EVENT_SEEKING:
            // FgPrEvent_Seeking is raised when the seek is POSTED (PrSession.cpp) — a seek that has to fetch first
            // reaches the engine only after its segment pair lands, and the managed side must see it in flight from
            // the start. The engine's own SEEKING only confirms the flag.
            s.seeking.store(1, std::memory_order_release);
            break;
        case MF_MEDIA_ENGINE_EVENT_SEEKED:
            s.seeking.store(0, std::memory_order_release);
            fgpr::SessionSamplePosition(rt, s, false);
            Raise(h, FgPrEvent_Seeked, s.positionMs.load(std::memory_order_acquire), fgpr::MsSinceQpc(s.seekPostedQpc));
            break;
        case MF_MEDIA_ENGINE_EVENT_PLAYING:
            s.state.store(FgPrState_Playing, std::memory_order_release);
            fgpr::SessionSamplePosition(rt, s, false);
            Raise(h, FgPrEvent_Playing);
            break;
        case MF_MEDIA_ENGINE_EVENT_PAUSE:
            if (!s.metadataSeen) break;   // a predecessor's late PAUSE: this source has not even loaded yet
            s.state.store(FgPrState_Paused, std::memory_order_release);
            fgpr::SessionSamplePosition(rt, s, false);
            Raise(h, FgPrEvent_Paused);
            break;
        case MF_MEDIA_ENGINE_EVENT_ENDED:
            // Natural end of media. This was the whole track-end deadlock: without an ENDED state the managed
            // side only ever saw "paused", so auto-advance never fired, FgPlayReadyStop was never called, and
            // the g_desktopRunning latch wedged every later session with ERROR_BUSY. Ended is a STATE, not a
            // shutdown — the session stays alive so a seek-after-end still works.
            if (!s.metadataSeen) break;
            s.state.store(FgPrState_Ended, std::memory_order_release);
            fgpr::SessionSamplePosition(rt, s, false);
            Raise(h, FgPrEvent_Ended);
            break;
        case MF_MEDIA_ENGINE_EVENT_ERROR:
            LogLine("[cenc] media ERROR code=" + std::to_string((int)p1) + " hr=" + fgpr::Hex((HRESULT)p2));
            s.errorHr.store((int32_t)p2, std::memory_order_release);
            s.state.store(FgPrState_Error, std::memory_order_release);
            Raise(h, FgPrEvent_Error, (int64_t)p1, (int64_t)(int32_t)p2);
            break;
        case MF_MEDIA_ENGINE_EVENT_FORMATCHANGE:
        case MF_MEDIA_ENGINE_EVENT_RESOURCELOST:
            // The swap chain may have been re-created (a representation switch changed the frame size, or the device
            // was lost): re-query the handle and RE-RAISE it even when the value is unchanged, so the presenter re-binds.
            fgpr::SessionPublishHandle(rt, s, true);
            break;
        default:
            break;
    }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The one-time ITA preflight (diagnostics only).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
namespace fgpr {

// Diagnose the real CDM-owned ITA before handing it to MediaEngine/PMP. This uses a separate trusted-input
// instance so RequestAccess/GetPolicy cannot disturb the instance cached by CencMediaSource.
// It used to run on EVERY open; it is a PMP round trip that proves nothing new after the first success, so the runtime
// runs it on its FIRST attach only and keeps the warm switch free of it.
void RuntimeItaPreflight(Runtime& rt)
{
    if (rt.itaPreflightDone || !rt.cdm) return;
    rt.itaPreflightDone = true;
    auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };
    IMFTrustedInput* probeTrustedInput = nullptr;
    HRESULT probeHr = rt.cdm->CreateTrustedInput(nullptr, 0, &probeTrustedInput);
    LogLine("[cenc-preflight] CreateTrustedInput hr=" + hx(probeHr));
    if (SUCCEEDED(probeHr) && probeTrustedInput)
    {
        ::IUnknown* unknown = nullptr;
        probeHr = probeTrustedInput->GetInputTrustAuthority(1, __uuidof(IMFInputTrustAuthority), &unknown);
        LogLine("[cenc-preflight] GetInputTrustAuthority hr=" + hx(probeHr));
        if (SUCCEEDED(probeHr) && unknown)
        {
            IMFInputTrustAuthority* ita = nullptr;
            probeHr = unknown->QueryInterface(IID_PPV_ARGS(&ita));
            LogLine("[cenc-preflight] QI IMFInputTrustAuthority hr=" + hx(probeHr));
            if (SUCCEEDED(probeHr) && ita)
            {
                IMFActivate* enabler = nullptr;
                probeHr = ita->RequestAccess(PEACTION_PLAY, &enabler);
                LogLine("[cenc-preflight] RequestAccess(PLAY) hr=" + hx(probeHr) +
                        " enabler=" + std::to_string(enabler != nullptr));
                if (enabler) enabler->Release();

                IMFOutputPolicy* policy = nullptr;
                probeHr = ita->GetPolicy(PEACTION_PLAY, &policy);
                LogLine("[cenc-preflight] GetPolicy(PLAY) hr=" + hx(probeHr) +
                        " policy=" + std::to_string(policy != nullptr));
                if (policy) policy->Release();

                IMFTransform* decrypter = nullptr;
                probeHr = ita->GetDecrypter(IID_PPV_ARGS(&decrypter));
                LogLine("[cenc-preflight] GetDecrypter hr=" + hx(probeHr) +
                        " decrypter=" + std::to_string(decrypter != nullptr));
                if (decrypter) decrypter->Release();
                ita->Reset();
                ita->Release();
            }
            unknown->Release();
        }
        probeTrustedInput->Release();
    }
}

}   // namespace fgpr

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  The runtime thread.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

static void RuntimeThreadMain(std::shared_ptr<fgpr::Runtime> rt)
{
    const int64_t t0 = fgpr::QpcNow();
    HRESULT hr = S_OK;
    try { hr = BringUp(*rt); }
    catch (winrt::hresult_error const& e) { hr = e.code().value; }
    catch (...) { hr = E_FAIL; }
    if (FAILED(hr))
    {
        rt->bringUp.store(hr, std::memory_order_release);
        LogLine("[runtime] bring-up FAILED hr=" + fgpr::Hex(hr));
        Raise(0, FgPrEvent_RuntimeFailed, (int64_t)(int32_t)hr);
        // Work already queued (an acquisition, an attach) runs next and fails itself on !Ready(). The engine objects
        // that did come up stay until FgPrRuntimeDestroy's teardown releases them on this same thread.
    }
    else
    {
        rt->bringUp.store(S_OK, std::memory_order_release);
        const int64_t ms = fgpr::MsSinceQpc(t0);
        LogLine("[runtime] ready in " + std::to_string((long long)ms) + "ms (MF + D3D11 video device + CDM/PMP + media engine)");
        Raise(0, FgPrEvent_RuntimeReady, ms);
    }

    // Event-driven: block until work arrives. The only timed wait is the ≤ 4 Hz sampler, and only while
    // SessionSampleAttached says an attached session needs it (playing, or its swap-chain handle not yet published).
    bool timed = false;
    while (rt->queue.RunOnce(timed ? 250 : -1))
        timed = rt->Ready() && fgpr::SessionSampleAttached(*rt);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Exports.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

__declspec(dllexport) int32_t __stdcall FgPrRuntimeCreate(const wchar_t* storePath, FgPrEventCallback cb, void* ctx,
                                                          FgPrRuntime* out)
{
    if (!out) return E_POINTER;
    *out = 0;
    std::lock_guard<std::mutex> g(fgpr::Reg().mx);
    if (fgpr::Reg().runtime)
    {
        // A second call returns the EXISTING runtime and does no work — unless that runtime is being destroyed right
        // now, in which case there is nothing valid to hand back until FgPrRuntimeDestroy returns.
        if (fgpr::Reg().runtime->shuttingDown.load(std::memory_order_acquire)) return HRESULT_FROM_WIN32(ERROR_BUSY);
        *out = fgpr::Reg().runtime->handle;
        return S_OK;
    }
    if (!storePath || !*storePath) return E_INVALIDARG;

    fgpr::Sink().ctx.store(ctx, std::memory_order_release);
    fgpr::Sink().cb.store(cb, std::memory_order_release);

    std::shared_ptr<fgpr::Runtime> rt;
    try
    {
        rt = std::make_shared<fgpr::Runtime>();
        rt->handle = fgpr::NewHandle(fgpr::HandleKind::Runtime);
        rt->storePath = storePath;
        rt->createdQpc = fgpr::QpcNow();
        rt->thread = std::thread(RuntimeThreadMain, rt);
    }
    catch (...)
    {
        fgpr::Sink().cb.store(nullptr, std::memory_order_release);
        fgpr::Sink().ctx.store(nullptr, std::memory_order_release);
        return E_OUTOFMEMORY;
    }
    fgpr::Reg().runtime = rt;
    *out = rt->handle;
    return S_OK;
}

__declspec(dllexport) void __stdcall FgPrRuntimeDestroy(FgPrRuntime handle)
{
    std::shared_ptr<fgpr::Runtime> rt;
    {
        std::lock_guard<std::mutex> g(fgpr::Reg().mx);
        rt = fgpr::Reg().runtime;
        if (!rt || rt->handle != handle) return;
    }
    bool expected = false;
    if (!rt->shuttingDown.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) return;   // idempotent

    fgpr::Runtime* raw = rt.get();
    rt->queue.Post([raw] { TearDown(*raw); raw->queue.Stop(); });

    // Bounded join. A runtime thread wedged inside Media Foundation must not hang the host's shutdown: after 2 s the
    // thread is detached (it keeps its own strong ref and finishes, or dies with the process).
    if (rt->thread.joinable())
    {
        if (rt->thread.get_id() == std::this_thread::get_id())
        {
            rt->thread.detach();   // Destroy called from inside a runtime work item: the teardown item runs after it
        }
        else
        {
            const DWORD wait = WaitForSingleObject((HANDLE)rt->thread.native_handle(), 2000);
            if (wait == WAIT_OBJECT_0) rt->thread.join();
            else
            {
                fgpr::RaiseLog(0, "[runtime] destroy: runtime thread did not finish within 2000ms — detached");
                rt->thread.detach();
            }
        }
    }

    {
        std::lock_guard<std::mutex> g(fgpr::Reg().mx);
        if (fgpr::Reg().runtime == rt) fgpr::Reg().runtime = nullptr;
        fgpr::Reg().sessions.clear();
    }

    // The managed side frees its callback context the moment this returns: stop new calls, then wait (bounded) for the
    // ones that already read the pointer on an MF or CDM thread.
    fgpr::Sink().cb.store(nullptr, std::memory_order_release);
    for (int i = 0; i < 500 && fgpr::Sink().inflight.load(std::memory_order_acquire) != 0; i++) Sleep(1);
    fgpr::Sink().ctx.store(nullptr, std::memory_order_release);
}

__declspec(dllexport) int64_t __stdcall FgPrRuntimeUptimeMs(FgPrRuntime handle)
{
    std::shared_ptr<fgpr::Runtime> rt = fgpr::RuntimeFor(handle);
    return rt ? rt->UptimeMs() : 0;
}
