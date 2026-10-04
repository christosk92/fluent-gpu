// PrRuntime.cpp — the process-lifetime PlayReady runtime: FgPrRuntimeCreateOnAdapter / FgPrRuntimeDestroy / FgPrRuntimeUptimeMs /
// FgPrRuntimeSetVideoOutputFormat.
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

// What the CDM granted (F022). The security level a PlayReady CDM provisions is not a number any Media Foundation API reports:
// it follows the robustness the access request carried (none here: the video capabilities are empty, so the CDM's own default
// applies - software, SL2000 - and hardware, SL3000, would need the `.3000` key system and a "3000" video capability, which
// is deliberately NOT asked for: a stricter output-protection regime and no speed). Until now nothing recorded what was
// negotiated, so a box that had quietly landed on a different level looked the same in the log. The granted configuration the
// CDM hands back (capabilities, and the robustness inside them when it filled one in) is the nearest thing to an answer.
static bool SameKey(const PROPERTYKEY& a, const PROPERTYKEY& b) { return a.pid == b.pid && IsEqualGUID(a.fmtid, b.fmtid); }

static const char* EmeKeyName(const PROPERTYKEY& k)
{
    if (SameKey(k, MF_EME_INITDATATYPES)) return "initDataTypes";
    if (SameKey(k, MF_EME_DISTINCTIVEID)) return "distinctiveId";
    if (SameKey(k, MF_EME_PERSISTEDSTATE)) return "persistedState";
    if (SameKey(k, MF_EME_AUDIOCAPABILITIES)) return "audioCapabilities";
    if (SameKey(k, MF_EME_VIDEOCAPABILITIES)) return "videoCapabilities";
    if (SameKey(k, MF_EME_LABEL)) return "label";
    if (SameKey(k, MF_EME_SESSIONTYPES)) return "sessionTypes";
    if (SameKey(k, MF_EME_ROBUSTNESS)) return "robustness";
    if (SameKey(k, MF_EME_CONTENTTYPE)) return "contentType";
    return nullptr;
}

static std::string DescribePropertyStore(IPropertyStore* store, int depth);

static std::string DescribePropVariant(const PROPVARIANT& pv, int depth)
{
    switch (pv.vt)
    {
        case VT_UI4: return std::to_string((unsigned long)pv.ulVal);
        case VT_BSTR: return "\"" + fgpr::Narrow(pv.bstrVal ? std::wstring(pv.bstrVal) : std::wstring()) + "\"";
        case VT_LPWSTR: return "\"" + fgpr::Narrow(pv.pwszVal ? std::wstring(pv.pwszVal) : std::wstring()) + "\"";
        case VT_UNKNOWN:
        {
            winrt::com_ptr<IPropertyStore> inner;
            if (depth < 3 && pv.punkVal && SUCCEEDED(pv.punkVal->QueryInterface(IID_PPV_ARGS(inner.put()))) && inner)
                return DescribePropertyStore(inner.get(), depth + 1);
            return "<object>";
        }
        case VT_VECTOR | VT_UI4:
        {
            std::string out = "[";
            for (ULONG i = 0; i < pv.caul.cElems && i < 16; i++)
                out += std::string(i ? "," : "") + std::to_string((unsigned long)pv.caul.pElems[i]);
            return out + "]";
        }
        case VT_VECTOR | VT_BSTR:
        {
            std::string out = "[";
            for (ULONG i = 0; i < pv.cabstr.cElems && i < 16; i++)
                out += std::string(i ? "," : "") + fgpr::Narrow(pv.cabstr.pElems[i] ? std::wstring(pv.cabstr.pElems[i]) : std::wstring());
            return out + "]";
        }
        case VT_VECTOR | VT_VARIANT:
        {
            std::string out = "[";
            for (ULONG i = 0; i < pv.capropvar.cElems && i < 8; i++)
                out += std::string(i ? " " : "") + DescribePropVariant(pv.capropvar.pElems[i], depth);
            return out + "]";
        }
        default: return "<vt " + std::to_string((int)pv.vt) + ">";
    }
}

static std::string DescribePropertyStore(IPropertyStore* store, int depth)
{
    DWORD n = 0;
    if (!store || FAILED(store->GetCount(&n))) return "{?}";
    std::string out = "{";
    for (DWORD i = 0; i < n && i < 16; i++)
    {
        PROPERTYKEY key{};
        PROPVARIANT pv;
        PropVariantInit(&pv);
        if (FAILED(store->GetAt(i, &key)) || FAILED(store->GetValue(key, &pv))) { PropVariantClear(&pv); continue; }
        if (out.size() > 1) out += ' ';
        const char* name = EmeKeyName(key);
        out += name ? std::string(name) : "pid" + std::to_string((unsigned long)key.pid);
        out += '=';
        out += DescribePropVariant(pv, depth);
        PropVariantClear(&pv);
    }
    return out + "}";
}

/// Log, once per runtime (CreateAndPrepareCdm runs once per runtime), the key system the access object negotiated and the
/// configuration the CDM granted for the access request this file sent.
static void LogNegotiatedCdm(const wchar_t* requestedKeySystem, IMFContentDecryptionModuleAccess* access)
{
    std::string keySystem = "?";
    LPWSTR granted = nullptr;
    if (SUCCEEDED(access->GetKeySystem(&granted)) && granted) keySystem = fgpr::Narrow(std::wstring(granted));
    if (granted) CoTaskMemFree(granted);
    std::string config = "?";
    IPropertyStore* store = nullptr;
    if (SUCCEEDED(access->GetConfiguration(&store)) && store)
    {
        config = DescribePropertyStore(store, 0);
        store->Release();
    }
    LogLine("[eme-cdm] negotiated key system=" + keySystem + " (requested " + fgpr::Narrow(std::wstring(requestedKeySystem)) +
            "); granted configuration " + config + "; robustness requested: none, so the security level is the CDM's own default "
            "(presumably software, SL2000 - no Media Foundation API reports it; hardware SL3000 is deliberately not requested)");
}

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
        LogNegotiatedCdm(keySystem, cdmAccess);

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

static void OnEngineEvent(fgpr::Runtime& rt, uint64_t sessionHandle, uint64_t attachGen, DWORD ev, DWORD_PTR p1, DWORD p2, int64_t qpc);

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
    std::atomic<int64_t> m_lastTimeUpdateQpc{ 0 };

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
            case MF_MEDIA_ENGINE_EVENT_TIMEUPDATE:
            // The engine's own "the clock stopped for want of data" family: a mid-play stall must read as buffering, not as
            // a Playing source whose position keeps extrapolating (F030).
            case MF_MEDIA_ENGINE_EVENT_WAITING:
            case MF_MEDIA_ENGINE_EVENT_STALLED:
            case MF_MEDIA_ENGINE_EVENT_BUFFERINGSTARTED:
            case MF_MEDIA_ENGINE_EVENT_BUFFERINGENDED:
                break;
            default:
                return S_OK;
        }
        const int64_t qpc = fgpr::QpcNow();   // FIRSTFRAMEREADY's timestamp is taken HERE, not when the item runs
        if (ev == MF_MEDIA_ENGINE_EVENT_TIMEUPDATE)
        {
            // The engine's own clock tick drives the position sample (FgPrEvent_Position is ≤ 4 Hz). Coalesced here, so
            // however often the engine raises it the runtime thread wakes at most every 200 ms for it. 200 and not 250:
            // the managed side extrapolates from this sample for at most 500 ms, and a 250 ms gate that happens to drop
            // every second tick of a 250 ms engine interval would leave the sample up to that old.
            const int64_t last = m_lastTimeUpdateQpc.load(std::memory_order_acquire);
            if (last != 0 && fgpr::QpcToMs(qpc - last) < 200) return S_OK;
            m_lastTimeUpdateQpc.store(qpc, std::memory_order_release);
        }
        std::shared_ptr<fgpr::Runtime> rt = fgpr::RuntimeFor(m_runtime);
        if (!rt) return S_OK;
        // The attach generation is read BEFORE the attached session: an attach bumps the generation and then names its
        // session, so a stamp read first is never newer than the session read after it, and an event that belongs to an
        // earlier attach is dropped by OnEngineEvent whatever session handle it carries.
        const uint64_t attachGen = rt->attachGen.load(std::memory_order_acquire);
        const uint64_t session = rt->attached.load(std::memory_order_acquire);
        if (!session) return S_OK;
        fgpr::Runtime* raw = rt.get();   // the runtime thread owns a strong ref for as long as it runs items
        rt->queue.Post([raw, session, attachGen, ev, p1, p2, qpc] { OnEngineEvent(*raw, session, attachGen, ev, p1, p2, qpc); });
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

/// FG_PLAYREADY_ITA_PREFLIGHT=1 turns the one-time CDM ITA diagnostic (fgpr::RuntimeItaPreflight) on. Off by default: it
/// proves nothing the real topology does not, and costs several mfpmp.exe round trips plus a throw-away decrypter.
static bool ItaPreflightRequested()
{
    wchar_t value[4] = {};
    const DWORD n = GetEnvironmentVariableW(L"FG_PLAYREADY_ITA_PREFLIGHT", value, (DWORD)(sizeof(value) / sizeof(value[0])));
    return n == 1 && value[0] == L'1';
}

/// FG_PLAYREADY_TRUSTED_INPUT_REUSE=1 keeps ONE IMFTrustedInput for the CDM's lifetime instead of creating one per attach
/// (fgpr::Runtime::trustedInput). Off by default until validated on a box with two KIDs and a re-attach.
static bool TrustedInputReuseRequested()
{
    wchar_t value[4] = {};
    const DWORD n = GetEnvironmentVariableW(L"FG_PLAYREADY_TRUSTED_INPUT_REUSE", value, (DWORD)(sizeof(value) / sizeof(value[0])));
    return n == 1 && value[0] == L'1';
}

/// FG_PLAYREADY_NO_OPM_WINDOW=1 leaves the engine without the virtual OPM window (F264): the pre-change engine wiring, as a kill
/// switch for a box where the window turns out to matter. Off by default.
static bool OpmWindowDisabled()
{
    wchar_t value[4] = {};
    const DWORD n = GetEnvironmentVariableW(L"FG_PLAYREADY_NO_OPM_WINDOW", value, (DWORD)(sizeof(value) / sizeof(value[0])));
    return n == 1 && value[0] == L'1';
}

static HRESULT BringUp(fgpr::Runtime& rt)
{
    auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    rt.comInitialized = SUCCEEDED(hr);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) { LogLine("[runtime] CoInitializeEx(MTA) hr=" + hx(hr)); return hr; }
    hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(hr)) { LogLine("[runtime] MFStartup hr=" + hx(hr)); return hr; }
    rt.mfStarted = true;
    rt.mfReady.store(true, std::memory_order_release);   // the feeder may now build its Media Foundation source

    // ── D3D11 + MF DXGI manager (shared, multithread-protected). ─────────────────────────────────────────────────────
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    {
        // Land the device on the SAME adapter the renderer chose (the managed side passes its LUID), exactly as the clear
        // path's engine does: on a hybrid machine the default adapter can be the OTHER GPU. LUID 0, an adapter that is gone,
        // or one that refuses a video device falls back to the default adapter - decode on the wrong GPU beats no decode.
        winrt::com_ptr<IDXGIAdapter1> adapter;
        if (rt.adapterLuid != 0)
        {
            winrt::com_ptr<IDXGIFactory4> factory;
            LUID luid{};
            luid.LowPart = (DWORD)(rt.adapterLuid & 0xFFFFFFFFLL);
            luid.HighPart = (LONG)(rt.adapterLuid >> 32);
            if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(factory.put()))) ||
                FAILED(factory->EnumAdapterByLuid(luid, IID_PPV_ARGS(adapter.put()))))
            {
                adapter = nullptr;
                LogLine("[cenc] render adapter luid=" + std::to_string((long long)rt.adapterLuid) + " not found - default adapter");
            }
        }
        ID3D11Device* d3d = nullptr; ID3D11DeviceContext* ctx = nullptr;
        // An explicit adapter REQUIRES D3D_DRIVER_TYPE_UNKNOWN (HARDWARE + an adapter is E_INVALIDARG).
        hr = D3D11CreateDevice(adapter.get(), adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0,
                               D3D11_SDK_VERSION, &d3d, nullptr, &ctx);
        if (adapter && (FAILED(hr) || !d3d))
        {
            LogLine("[cenc] adapter-pinned D3D11CreateDevice hr=" + hx(hr) + " - default adapter");
            if (ctx) { ctx->Release(); ctx = nullptr; }
            if (d3d) { d3d->Release(); d3d = nullptr; }
            adapter = nullptr;
            hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, &ctx);
        }
        if (FAILED(hr) || !d3d)
        { LogLine("[cenc] D3D11CreateDevice hr=" + hx(hr)); if (ctx) ctx->Release(); return FAILED(hr) ? hr : E_FAIL; }
        if (ctx) ctx->Release();
        rt.d3d.attach(d3d);
        LogLine(std::string("[cenc] D3D11 video device on ") + (adapter ? "the renderer's adapter (pinned)" : "the default adapter"));
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

    // F264: the hidden virtual window the engine's output protection is tied to (Chromium's MF_MEDIA_ENGINE_OPM_HWND). It must exist
    // BEFORE the engine, which reads the handle once at creation; the managed pump moves it over the video's screen rect later
    // (FgPrSessionPlaceOpmWindow). Hardening with no proven effect, so a window that cannot be created never fails the bring-up.
    if (OpmWindowDisabled()) LogLine("[opm] virtual window disabled (FG_PLAYREADY_NO_OPM_WINDOW)");
    else
    {
        const HRESULT opmHr = rt.opm.Start();
        LogLine(SUCCEEDED(opmHr) ? std::string("[opm] virtual window created (MF_MEDIA_ENGINE_OPM_HWND)")
                                 : "[opm] virtual window NOT created hr=" + hx(opmHr) + " - the engine runs without OPM_HWND");
    }

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
    // F249: BGRA is the long-standing output; NV12 only when the managed side asked for it (FgPrRuntimeSetVideoOutputFormat, behind
    // --fg video-nv12, after its overlay probe said NV12 can take a plane).
    const DXGI_FORMAT outputFormat = rt.videoOutputFormat == FgPrVideoOutput_Nv12 ? DXGI_FORMAT_NV12 : DXGI_FORMAT_B8G8R8A8_UNORM;
    attrs->SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, outputFormat);
    LogLine(std::string("[cenc] engine output format ") + (outputFormat == DXGI_FORMAT_NV12 ? "NV12" : "BGRA"));
    if (HWND opmHwnd = rt.opm.Handle()) attrs->SetUINT64(MF_MEDIA_ENGINE_OPM_HWND, (UINT64)(uintptr_t)opmHwnd);
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
    // The ITA preflight is a diagnostic: opt-in, and here (once per runtime lifetime, before any session exists) rather
    // than in front of the first protected SetSource, where its PMP round trips sat on the open's critical path.
    if (ItaPreflightRequested()) fgpr::RuntimeItaPreflight(rt);
    rt.trustedInputReuse = TrustedInputReuseRequested();
    LogLine(std::string("[runtime] trusted input: ") + (rt.trustedInputReuse ? "one per CDM (reused across attaches)" : "one per attach"));
    return S_OK;
}

static void TearDown(fgpr::Runtime& rt)
{
    fgpr::SessionsShutdown(rt);
    fgpr::LicensesShutdown(rt);
    rt.trustedInput = nullptr;   // before the CDM that created it
    if (rt.engine)
    {
        HRESULT hr = rt.engine->Shutdown();
        LogLine("[runtime] engine Shutdown hr=" + fgpr::Hex(hr));
    }
    rt.protectedContent = nullptr;
    rt.engineEx = nullptr;
    rt.engine = nullptr;
    rt.opm.Stop();   // the engine that was given the window is gone
    rt.needKey = nullptr;
    rt.notify = nullptr;
    rt.extension = nullptr;
    rt.protectionManager = nullptr;
    rt.cdm = nullptr;
    rt.dxgiManager = nullptr;
    rt.d3d = nullptr;
    rt.mfReady.store(false, std::memory_order_release);
    if (rt.mfStarted) { MFShutdown(); rt.mfStarted = false; }
    if (rt.comInitialized) { CoUninitialize(); rt.comInitialized = false; }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Engine events → FgPrEvents (runtime thread).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

static void OnEngineEvent(fgpr::Runtime& rt, uint64_t sessionHandle, uint64_t attachGen, DWORD ev, DWORD_PTR p1, DWORD p2, int64_t qpc)
{
    if (!rt.Ready() || rt.attached.load(std::memory_order_acquire) != sessionHandle) return;
    // Posted for an earlier attach (this session detached and attached again since, or another session's attach replaced
    // it and ended): a late ERROR / EMPTIED / PAUSE of the previous load is not this attach's to report.
    if (rt.attachGen.load(std::memory_order_acquire) != attachGen) return;
    std::shared_ptr<fgpr::Session> sp = fgpr::SessionByHandle(sessionHandle);
    if (!sp) return;
    fgpr::Session& s = *sp;
    const uint64_t h = s.handle;
    // Attached but its source not on the engine yet (the attach is waiting for its init segments): whatever the engine
    // says now is about the PREVIOUS source — its pause, a late error — and never this session's.
    if (s.attachPending) return;

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
            // The start-position fallback runs BEFORE the event so the event can carry its price (startCorrectionMs, 0 = the engine
            // adopted the carried start): it is the switch budget's second-Start cost. The raise only queues, so the event still
            // precedes everything the correction's own seek makes the engine say.
            fgpr::SessionOnCanPlay(rt, s);
            Raise(h, FgPrEvent_CanPlay, s.startCorrectionMs);
            fgpr::SessionPublishHandle(rt, s, false);
            break;
        case MF_MEDIA_ENGINE_EVENT_FIRSTFRAMEREADY:
        {
            int64_t expected = 0;
            s.firstFrameQpc.compare_exchange_strong(expected, qpc, std::memory_order_acq_rel);
            // An open PAUSED (startPaused) never plays, so no PLAYING or PAUSE event ever moves it out of Loading: the
            // first frame being up IS the moment it is paused-and-ready. A playing source reports PLAYING itself.
            {
                int32_t loading = FgPrState_Loading;
                s.state.compare_exchange_strong(loading, rt.engine->IsPaused() ? FgPrState_Paused : FgPrState_Playing,
                                                std::memory_order_acq_rel);
            }
            fgpr::SessionSamplePosition(rt, s, false);
            {
                // The carried start went onto the engine timeline at attach: the clock should read about the start now.
                // `startCorrectionMs` != 0 means the engine ignored it and the CANPLAY fallback had to seek (a second
                // source Start); it is the number the switch budget tracks.
                const int64_t carried = s.startPositionMs.load(std::memory_order_acquire);
                if (carried > 0 && expected == 0)   // expected is still 0 only when THIS event stamped the first frame
                    fgpr::RaiseLog(h, "[cenc] first frame at " + std::to_string((long long)s.positionMs.load(std::memory_order_acquire)) +
                                      "ms, carried start " + std::to_string((long long)carried) + "ms, startCorrectionMs=" +
                                      std::to_string((long long)s.startCorrectionMs));
            }
            // b = the QPC FIRSTFRAMEREADY was stamped at (the same value as FgPrSnapshot.firstFrameQpc), so the managed side times the
            // first frame from the native clock rather than from whenever the event was dequeued.
            Raise(h, FgPrEvent_FirstFrame, s.positionMs.load(std::memory_order_acquire), s.firstFrameQpc.load(std::memory_order_acquire));
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
            fgpr::SessionSamplePosition(rt, s, false);
            if (s.waitGate.Clear()) Raise(h, FgPrEvent_Resumed, s.positionMs.load(std::memory_order_acquire));
            {
                // The carried start applied to the engine timeline at attach (and the CANPLAY fallback correction) is the
                // native side's own seek: nobody on the managed side is waiting for it, and a Seeked with no Seeking would
                // confuse the seek planner. It is tagged with the user seekSeq, so it clears the `seeking` flag it raised
                // and can never swallow a user seek's SEEKED (see plan::InternalSeek).
                const fgpr::plan::InternalSeek::Verdict v = s.internalSeek.OnSeeked(s.seekSeq.load(std::memory_order_acquire));
                if (v.internal)
                {
                    if (v.clearsSeeking) s.seeking.store(0, std::memory_order_release);
                    break;
                }
            }
            s.seeking.store(0, std::memory_order_release);
            Raise(h, FgPrEvent_Seeked, s.positionMs.load(std::memory_order_acquire), fgpr::MsSinceQpc(s.seekPostedQpc));
            break;
        case MF_MEDIA_ENGINE_EVENT_PLAYING:
            s.state.store(FgPrState_Playing, std::memory_order_release);
            fgpr::SessionSamplePosition(rt, s, false);
            if (s.waitGate.Clear()) Raise(h, FgPrEvent_Resumed, s.positionMs.load(std::memory_order_acquire));
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
        {
            // The engine's CURRENT error object is the authority. Loading a new source clears it, so an error notification
            // that outlived the source it was raised for (a detach's own empty SetSource, a replaced source's late failure)
            // finds no error here and is not this session's to report.
            winrt::com_ptr<IMFMediaError> err;
            if (FAILED(rt.engine->GetError(err.put())) || !err)
            {
                fgpr::RaiseLog(h, "[cenc] media ERROR notification code=" + std::to_string((int)p1) + " hr=" +
                                  fgpr::Hex((HRESULT)p2) + " with no current error - a previous source's, ignored");
                break;
            }
            const int64_t code = (int64_t)err->GetErrorCode();
            HRESULT hr = err->GetExtendedErrorCode();
            if (SUCCEEDED(hr)) hr = (HRESULT)p2;
            fgpr::RaiseLog(h, "[cenc] media ERROR code=" + std::to_string((long long)code) + " hr=" + fgpr::Hex(hr));
            s.errorHr.store((int32_t)hr, std::memory_order_release);
            s.state.store(FgPrState_Error, std::memory_order_release);
            Raise(h, FgPrEvent_Error, code, (int64_t)(int32_t)hr);
            break;
        }
        case MF_MEDIA_ENGINE_EVENT_TIMEUPDATE:
            // Playback time moved: refresh the snapshot, raise Position (rate-limited inside), and — should the swap chain
            // not have been ready at metadata / canplay / first frame — ask for the handle again.
            fgpr::SessionSamplePosition(rt, s, false);
            if (s.waitGate.OnTimeUpdate(s.positionMs.load(std::memory_order_acquire)))
                Raise(h, FgPrEvent_Resumed, s.positionMs.load(std::memory_order_acquire));   // the clock advances again
            fgpr::SessionPublishHandle(rt, s, false);
            break;
        case MF_MEDIA_ENGINE_EVENT_WAITING:
        case MF_MEDIA_ENGINE_EVENT_STALLED:
        case MF_MEDIA_ENGINE_EVENT_BUFFERINGSTARTED:
            // Only a source that is PLAYING with a frame up and no seek in flight can stall mid-play: the engine also says
            // WAITING while it opens and while a seek loads, and both already read as buffering (Loading / Seeking).
            if (s.state.load(std::memory_order_acquire) != FgPrState_Playing || s.firstFrameQpc.load(std::memory_order_acquire) == 0 ||
                s.seeking.load(std::memory_order_acquire) != 0)
                break;
            fgpr::SessionSamplePosition(rt, s, false);   // readyState and the buffered window as of the stall
            if (s.waitGate.Enter(s.positionMs.load(std::memory_order_acquire)))
            {
                fgpr::RaiseLog(h, "[cenc] engine waiting (event " + std::to_string((unsigned long)ev) + ") at " +
                                  std::to_string((long long)s.positionMs.load(std::memory_order_acquire)) + "ms, readyState=" +
                                  std::to_string(s.readyState.load(std::memory_order_acquire)) + " ahead=" +
                                  std::to_string((long long)s.bufferedAheadMs.load(std::memory_order_acquire)) + "ms");
                Raise(h, FgPrEvent_Waiting, s.positionMs.load(std::memory_order_acquire));
            }
            break;
        case MF_MEDIA_ENGINE_EVENT_BUFFERINGENDED:
            fgpr::SessionSamplePosition(rt, s, false);
            if (s.waitGate.Clear()) Raise(h, FgPrEvent_Resumed, s.positionMs.load(std::memory_order_acquire));
            break;
        case MF_MEDIA_ENGINE_EVENT_FORMATCHANGE:
            // A representation switch changed the decoded frame size: report it (snapshot + FgPrEvent_SizeChanged) and
            // nothing more. No UpdateVideoStream and no handle re-raise — the managed stream size follows the new natural size.
            // Every FORMATCHANGE is logged with the size the snapshot held and the size the engine reports now (F261): the second
            // `swap-chain handle=` of an open follows one of these, and a same-size event is what tells a decoder re-negotiation
            // (the type's aperture / aspect / colour differing from the SPS) from a real size change.
            {
                DWORD fcw = 0, fch = 0;
                rt.engine->GetNativeVideoSize(&fcw, &fch);
                fgpr::RaiseLog(h, "[cenc] FORMATCHANGE native size " + std::to_string(s.width.load(std::memory_order_acquire)) + "x" +
                                  std::to_string(s.height.load(std::memory_order_acquire)) + " -> " + std::to_string((unsigned long)fcw) +
                                  "x" + std::to_string((unsigned long)fch));
            }
            fgpr::SessionOnFormatChange(rt, s);
            break;
        case MF_MEDIA_ENGINE_EVENT_RESOURCELOST:
            // The device was lost, so the swap chain may have been re-created: re-query the handle and RE-RAISE it even
            // when the value is unchanged, so the presenter re-binds.
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
// It used to run on EVERY open, then on the FIRST attach of each runtime lifetime (still ahead of SetSource, on the
// critical path of the first protected open). It is now opt-in (FG_PLAYREADY_ITA_PREFLIGHT=1) and runs once at bring-up.
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

    // Event-driven: block until work arrives — an export's verb, a media-engine event, a license delivery, a feeder
    // completion. Nothing on this thread ever wakes on a timer.
    while (rt->queue.RunOnce(fgpr::LogWorkItemFailure)) {}
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Exports.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

__declspec(dllexport) int32_t __stdcall FgPrRuntimeCreateOnAdapter(const wchar_t* storePath, FgPrEventCallback cb, void* ctx,
                                                                         int64_t adapterLuid, FgPrRuntime* out)
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

    // The callback is set and the notifier running BEFORE the runtime thread exists: its first events (bring-up lines) queue
    // into the ring from the first instruction, and no producer thread ever enters the callback itself.
    fgpr::Sink().Set(cb, ctx);
    fgpr::SinkStart();

    std::shared_ptr<fgpr::Runtime> rt;
    try
    {
        rt = std::make_shared<fgpr::Runtime>();
        rt->handle = fgpr::NewHandle(fgpr::HandleKind::Runtime);
        rt->storePath = storePath;
        rt->adapterLuid = adapterLuid;
        rt->videoOutputFormat = fgpr::Reg().videoOutputFormat.load(std::memory_order_acquire);
        rt->createdQpc = fgpr::QpcNow();
        rt->thread = std::thread(RuntimeThreadMain, rt);
    }
    catch (...)
    {
        fgpr::SinkStop();
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
    // An Engine item, so it runs ahead of queued licence work: that work (a key session's Close above all) is drained first,
    // while the CDM is alive.
    rt->queue.Post([raw] { raw->queue.DrainMaintenance(fgpr::LogWorkItemFailure); TearDown(*raw); raw->queue.Stop(); });

    // Bounded join. A runtime thread wedged inside Media Foundation must not hang the host's shutdown: after 2 s the
    // thread is detached (it keeps its own strong ref and finishes, or dies with the process).
    bool joined = false;
    if (rt->thread.joinable())
    {
        if (rt->thread.get_id() == std::this_thread::get_id())
        {
            rt->thread.detach();   // Destroy called from inside a runtime work item: the teardown item runs after it
        }
        else
        {
            const DWORD wait = WaitForSingleObject((HANDLE)rt->thread.native_handle(), 2000);
            if (wait == WAIT_OBJECT_0) { rt->thread.join(); joined = true; }
            else
            {
                fgpr::RaiseLog(0, "[runtime] destroy: runtime thread did not finish within 2000ms - detached");
                rt->thread.detach();
            }
        }
    }

    // The join point for the feeder threads the runtime thread handed to the reaper instead of joining itself: none of
    // them may be left running when the host unloads this DLL. Only when the runtime thread was actually joined: its
    // TearDown has then drained every feeder, so this just ends the idle reaper thread and cannot block. A detached runtime
    // thread (wedged, or Destroy called from inside a work item) drains the reaper in its own TearDown (Reap joins inline
    // once it is closed), and ~Runtime -> ~FeederReaper joins when the last strong ref drops - the host never waits on a
    // feeder here, keeping the 2 s bound above.
    if (joined) rt->reaper.Shutdown();

    {
        std::lock_guard<std::mutex> g(fgpr::Reg().mx);
        if (fgpr::Reg().runtime == rt) fgpr::Reg().runtime = nullptr;
        fgpr::Reg().sessions.clear();
    }

    // The managed side frees its callback context the moment this returns. SinkStop flushes every event still queued into the
    // callback (the destroy's own lines and the last session events arrive in order, none lost), stops accepting (a straggling
    // MF thread's push is rejected), joins the notifier - bounded, so a callback wedged in managed code cannot hang the
    // host's shutdown - and only then clears the callback. Nothing enters the callback after this returns.
    fgpr::SinkStop();
}

__declspec(dllexport) int64_t __stdcall FgPrRuntimeUptimeMs(FgPrRuntime handle)
{
    std::shared_ptr<fgpr::Runtime> rt = fgpr::RuntimeFor(handle);
    return rt ? rt->UptimeMs() : 0;
}

__declspec(dllexport) int32_t __stdcall FgPrRuntimeSetVideoOutputFormat(int32_t format)
{
    fgpr::Reg().videoOutputFormat.store(format == FgPrVideoOutput_Nv12 ? FgPrVideoOutput_Nv12 : FgPrVideoOutput_Bgra, std::memory_order_release);
    return S_OK;
}

__declspec(dllexport) int32_t __stdcall FgPrSessionPlaceOpmWindow(FgPrRuntime rtHandle, FgPrSession sh, uint64_t hostWindow,
                                                                  int32_t left, int32_t top, int32_t right, int32_t bottom)
{
    if (right < left || bottom < top) return E_INVALIDARG;
    std::shared_ptr<fgpr::Runtime> rt = fgpr::RuntimeFor(rtHandle);
    if (!rt || !fgpr::IsKind(sh, fgpr::HandleKind::Session)) return E_HANDLE;
    // Ready() first: the window object is written by BringUp and read here from the UI thread, so it is only touched once the
    // bring-up has published (and never changed again). Only the session the engine is playing moves the window: a stale or
    // prepared session's placement would drag it away from the picture that is on screen.
    if (!rt->Ready() || rt->attached.load(std::memory_order_acquire) != sh) return S_FALSE;
    RECT placed{};
    const HRESULT hr = rt->opm.Place((HWND)(uintptr_t)hostWindow, left, top, right, bottom, &placed);
    if (hr == S_OK && rt->opm.FirstPlacement())
        fgpr::RaiseLog(sh, "[opm] virtual window over the video at screen (" + std::to_string(placed.left) + "," + std::to_string(placed.top) +
                           ") " + std::to_string(placed.right - placed.left) + "x" + std::to_string(placed.bottom - placed.top));
    return hr;
}
