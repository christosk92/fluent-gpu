// PrLicense.cpp — the KID-keyed license cache: FgPrLicenseAcquire / FgPrLicenseState / FgPrLicenseRelease.
//
// WHAT CHANGED (wavee-0.3-video-engine-implementation.md §1.3, §3.1.1). The one-shot helper opened a TEMPORARY key
// session per OPEN, called GenerateRequest, and then:
//   * HandleCdmKeyMessage invoked the managed relay SYNCHRONOUSLY on the CDM's own thread — and the managed side ran
//     the POST on Task.Run with task.Wait(30 s), so a CDM thread was parked for the whole round trip;
//   * DriveCdmLicenseProactive polled g_cdmUsable every 200 ms for up to 30 s before anything else could happen;
//   * the key session died with the source, so the same content re-ran the challenge on every open.
// Here a license is ONE CDM key session per KID, kept open across sessions until it expires, is released, or is
// LRU-evicted from an 8-entry table (never while an attached session uses it). The relay is NON-BLOCKING in both
// directions: KeyMessage hands the challenge up and RETURNS; the managed side calls FgPrLicenseDeliver later, from any
// thread, and Update() runs as a work item on the runtime thread; USABLE is reported when KeyStatusChanged says so.
// There is no poll and no wait anywhere in this file.
//
// LIFECYCLE (LicensePolicy.h holds the pure rules). A license is Pending until the CDM says USABLE; a Pending license
// older than kPendingDeadlineMs is STALE and is replaced rather than joined. A key that later goes INTERNAL_ERROR /
// RELEASED / OUTPUT_NOT_ALLOWED is killed (FgPrEvent_LicenseFailed while pending, FgPrEvent_LicenseRevoked once it was
// usable) and evicted, so the next FgPrLicenseAcquire re-issues it; OUTPUT_RESTRICTED / OUTPUT_DOWNSCALED raise
// FgPrEvent_LicenseRestricted. THIS table is the single eviction authority: when its LRU closes a key to admit a ninth
// KID it raises FgPrEvent_LicenseEvicted, and the managed cache drops that row.
#include "PrInternal.h"
#include "LicensePolicy.h"

using fgpr::Raise;

namespace {

constexpr size_t kLicenseCap = 8;

struct LicenseTable
{
    std::mutex mx;
    std::vector<std::shared_ptr<fgpr::License>> entries;
};

LicenseTable& Table()
{
    static LicenseTable* table = new LicenseTable();   // never destroyed (a CDM thread may unwind at DLL detach)
    return *table;
}

// The deliver half of the relay. `deliverCtx` is not a pointer to anything: it is an id into this registry, so a
// second call, a call after the license was dropped, or a call after FgPrRuntimeDestroy finds nothing and is a no-op
// — the ABI's "exactly one call per challenge; calling it twice, or after destroy, is a no-op", without trusting the
// managed side with a native pointer's lifetime.
struct DeliverRecord
{
    uint64_t runtime = 0;
    uint64_t license = 0;
};

struct DeliverRegistry
{
    std::mutex mx;
    std::unordered_map<uint64_t, DeliverRecord> live;
    uint64_t next = 1;
};

DeliverRegistry& Deliveries()
{
    static DeliverRegistry* reg = new DeliverRegistry();
    return *reg;
}

}   // namespace

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Helpers.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// Base64 text straight out of the CDM's UTF-16 XML (the challenge, a WRMHEADER KID) — never narrowed first.
static std::vector<uint8_t> Base64Decode(const std::wstring& w)
{
    DWORD n = 0;
    CryptStringToBinaryW(w.c_str(), (DWORD)w.size(), CRYPT_STRING_BASE64, nullptr, &n, nullptr, nullptr);
    std::vector<uint8_t> out(n);
    if (n) CryptStringToBinaryW(w.c_str(), (DWORD)w.size(), CRYPT_STRING_BASE64, out.data(), &n, nullptr, nullptr);
    out.resize(n);
    return out;
}

/// The content KID when the caller passed none: a v1 `pssh` lists its KIDs directly (big-endian, the tenc form); a
/// PlayReady v0 `pssh` carries them in the WRMHEADER (`<KID>b64</KID>` in v4.0, `<KID ... VALUE="b64">` from v4.1),
/// base64 of the GUID-ordered bytes. The license events carry the KID so the managed cache can match an acquisition
/// that completes before FgPrLicenseAcquire's caller has even stored the handle — so it must be recoverable here.
static bool KidFromPssh(const std::vector<uint8_t>& pssh, uint8_t kidOut[16])
{
    static const uint8_t prSystemId[16] = { 0x9A,0x04,0xF0,0x79,0x98,0x40,0x42,0x86,0xAB,0x92,0xE6,0x5B,0xE0,0x88,0x5F,0x95 };
    size_t off = 0;
    while (off + 32 <= pssh.size())
    {
        const uint32_t size = cenc::rd32(&pssh[off]);
        if (size < 32 || off + size > pssh.size()) break;
        const size_t end = off + size;
        if (cenc::rd32(&pssh[off + 4]) == cenc::fourcc("pssh"))
        {
            const uint8_t version = pssh[off + 8];
            const bool playReady = memcmp(&pssh[off + 12], prSystemId, 16) == 0;
            size_t p = off + 28;
            if (version >= 1 && p + 4 <= end)
            {
                const uint32_t kidCount = cenc::rd32(&pssh[p]);
                p += 4;
                if (kidCount >= 1 && p + 16 <= end) { memcpy(kidOut, &pssh[p], 16); return true; }
                p += 16ull * kidCount;
            }
            if (playReady && p + 4 <= end)
            {
                const uint32_t dataSize = cenc::rd32(&pssh[p]);
                p += 4;
                // PlayReady Object: [u32 size LE][u16 recordCount LE] then records [u16 type LE][u16 length LE][data].
                size_t q = p + 6;
                const size_t dataEnd = std::min<size_t>(end, p + dataSize);
                while (q + 4 <= dataEnd)
                {
                    const uint16_t type = (uint16_t)(pssh[q] | (pssh[q + 1] << 8));
                    const uint16_t len = (uint16_t)(pssh[q + 2] | (pssh[q + 3] << 8));
                    q += 4;
                    if (q + len > dataEnd) break;
                    if (type == 1 && len >= 2)
                    {
                        std::wstring xml(len / 2, L'\0');
                        memcpy(xml.data(), &pssh[q], (size_t)(len / 2) * 2);
                        for (size_t k = xml.find(L"<KID"); k != std::wstring::npos; k = xml.find(L"<KID", k + 4))
                        {
                            size_t gt = xml.find(L'>', k);
                            if (gt == std::wstring::npos) break;
                            const std::wstring tag = xml.substr(k, gt - k);
                            std::wstring b64;
                            size_t v = tag.find(L"VALUE=\"");
                            if (v != std::wstring::npos)
                            {
                                size_t e = tag.find(L'"', v + 7);
                                if (e != std::wstring::npos) b64 = tag.substr(v + 7, e - (v + 7));
                            }
                            else if (tag == L"<KID")
                            {
                                size_t e = xml.find(L"</KID>", gt);
                                if (e != std::wstring::npos) b64 = xml.substr(gt + 1, e - (gt + 1));
                            }
                            if (b64.empty()) continue;
                            std::vector<uint8_t> g = Base64Decode(b64);
                            if (g.size() != 16) continue;
                            // GUID byte order (Data1/2/3 little-endian) → the big-endian tenc form.
                            kidOut[0] = g[3]; kidOut[1] = g[2]; kidOut[2] = g[1]; kidOut[3] = g[0];
                            kidOut[4] = g[5]; kidOut[5] = g[4];
                            kidOut[6] = g[7]; kidOut[7] = g[6];
                            memcpy(kidOut + 8, g.data() + 8, 8);
                            return true;
                        }
                    }
                    q += len;
                }
            }
        }
        off = end;
    }
    return false;
}

std::shared_ptr<fgpr::License> fgpr::LicenseByHandle(uint64_t license)
{
    if (!IsKind(license, HandleKind::License)) return nullptr;
    std::lock_guard<std::mutex> g(Table().mx);
    for (auto const& e : Table().entries)
        if (e->handle == license) return e;
    return nullptr;
}

/// Mark an acquisition failed ONCE and say so. A license that was already usable is not downgraded by a late failure
/// of a renewal round trip — its key is still in the CDM.
static void FailLicense(fgpr::License& lic, HRESULT hr)
{
    if (SUCCEEDED(hr)) hr = E_FAIL;
    int32_t pending = 0;
    if (!lic.state.compare_exchange_strong(pending, (int32_t)hr, std::memory_order_acq_rel)) return;
    LogLine("[cdm] license kid=" + fgpr::Narrow(lic.kidHex) + " FAILED hr=" + fgpr::Hex(hr) + " after " +
            std::to_string((long long)fgpr::MsSinceQpc(lic.acquireQpc)) + "ms");
    Raise(lic.handle, FgPrEvent_LicenseFailed, (int64_t)(int32_t)hr, 0, lic.kidHex.c_str());
}

/// Close one key session (runtime thread). Idempotent.
static void CloseLicense(fgpr::License& lic)
{
    bool expected = false;
    if (!lic.closed.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) return;
    winrt::com_ptr<IMFContentDecryptionModuleSession> session;
    {
        std::lock_guard<std::mutex> g(lic.mx);
        session = std::move(lic.keySession);
    }
    if (session)
    {
        HRESULT hr = session->Close();
        LogLine("[cdm] key session closed kid=" + fgpr::Narrow(lic.kidHex) + " hr=" + fgpr::Hex(hr));
    }
}

static void PostClose(std::shared_ptr<fgpr::License> lic)
{
    std::shared_ptr<fgpr::Runtime> rt = fgpr::RuntimeFor(lic->runtime);
    if (!rt) return;   // the runtime's teardown closes everything still in the table
    fgpr::Runtime* raw = rt.get();
    if (!rt->queue.Post([raw, lic] { (void)raw; CloseLicense(*lic); }, fgpr::Lane::Maintenance)) CloseLicense(*lic);
}

/// Take a license out of the table and close its key session, unless an attached session still decrypts with it: then
/// it is only marked, and LicenseBind closes it at the last detach (a key still bound is never closed under a decoder).
static void EvictLicense(const std::shared_ptr<fgpr::License>& lic)
{
    if (lic->bindCount.load(std::memory_order_acquire) > 0)
    {
        lic->releaseRequested.store(true, std::memory_order_release);
        return;
    }
    {
        std::lock_guard<std::mutex> g(Table().mx);
        auto& v = Table().entries;
        v.erase(std::remove(v.begin(), v.end(), lic), v.end());
    }
    PostClose(lic);
}

/// A key status says this license will never decrypt again (INTERNAL_ERROR / RELEASED / OUTPUT_NOT_ALLOWED): fail it ONCE
/// from Pending or Usable, say so, and evict it so the next FgPrLicenseAcquire for the KID issues a fresh one. A pending
/// license reports the ordinary LicenseFailed; a usable one reports LicenseRevoked (the managed side deliberately ignores
/// a LicenseFailed that follows a usable license: that is a late renewal round trip, which leaves the key in place).
static void KillLicense(const std::shared_ptr<fgpr::License>& lic, int32_t status)
{
    const int32_t hr = fgpr::licensing::DeadHr(status);
    int32_t prev = lic->state.load(std::memory_order_acquire);
    do { if (prev != 0 && prev != 1) return; }
    while (!lic->state.compare_exchange_weak(prev, hr, std::memory_order_acq_rel));
    LogLine("[cdm] license kid=" + fgpr::Narrow(lic->kidHex) + " DEAD key status=" + std::to_string(status) + " hr=" +
            fgpr::Hex(hr) + (prev == 1 ? " (was usable)" : " (was pending)") + " after " +
            std::to_string((long long)fgpr::MsSinceQpc(lic->acquireQpc)) + "ms");
    if (prev == 0) Raise(lic->handle, FgPrEvent_LicenseFailed, (int64_t)hr, 0, lic->kidHex.c_str());
    else Raise(lic->handle, FgPrEvent_LicenseRevoked, (int64_t)hr, (int64_t)status, lic->kidHex.c_str());
    EvictLicense(lic);
}

// Read the key statuses and publish the transition. Runs on the CDM thread (KeyStatusChanged) and on the runtime
// thread (right after Update). USABLE is the ONLY thing that makes a license usable: right after a SUCCESSFUL Update()
// the key status is legitimately still pending — KeyStatusChanged fires a beat later — so a pending status is never an
// error here. Every other MF_MEDIAKEY_STATUS is mapped too (LicensePolicy.h): the three that kill a key fail and evict
// the license, the two output restrictions raise their own event, EXPIRED expires it.
static void QueryKeyStatus(const std::shared_ptr<fgpr::License>& licPtr)
{
    fgpr::License& lic = *licPtr;
    winrt::com_ptr<IMFContentDecryptionModuleSession> session = lic.KeySession();
    if (!session) return;
    MFMediaKeyStatus* st = nullptr; UINT n = 0;
    if (FAILED(session->GetKeyStatuses(&st, &n)) || !st) return;
    std::string text;
    fgpr::licensing::KeyStatusSummary sum;
    for (UINT i = 0; i < n; i++)
    {
        if (i) text += ",";
        text += std::to_string((int)st[i].eMediaKeyStatus);
        sum.Add((int32_t)st[i].eMediaKeyStatus);
        if (st[i].pbKeyId) CoTaskMemFree(st[i].pbKeyId);   // the caller owns every key id AND the array
    }
    CoTaskMemFree(st);
    LogLine("[cdm] KeyStatusChanged kid=" + fgpr::Narrow(lic.kidHex) + " -> status=" + (n ? text : std::string("<empty>")));

    // An output restriction is reported on the edge (and its lifting), never per status batch.
    const int32_t prevRestriction = lic.restriction.exchange(sum.restriction, std::memory_order_acq_rel);
    if (prevRestriction != sum.restriction)
    {
        LogLine("[cdm] license kid=" + fgpr::Narrow(lic.kidHex) + " output restriction status=" +
                std::to_string(sum.restriction) + " (was " + std::to_string(prevRestriction) + ")");
        Raise(lic.handle, FgPrEvent_LicenseRestricted, (int64_t)sum.restriction, (int64_t)prevRestriction, lic.kidHex.c_str());
    }

    const fgpr::licensing::KeyAction action = fgpr::licensing::NextAction(sum, lic.state.load(std::memory_order_acquire));
    if (action == fgpr::licensing::KeyAction::Kill)
    {
        KillLicense(licPtr, sum.deadStatus);
        return;
    }
    if (action == fgpr::licensing::KeyAction::BecomeUsable)
    {
        // A dead (failed) license is never revived by a racing status batch; a usable one is not announced twice.
        int32_t prev = lic.state.load(std::memory_order_acquire);
        do { if (prev < 0 || prev == 1) return; }
        while (!lic.state.compare_exchange_weak(prev, 1, std::memory_order_acq_rel));
        // Expiry as the CDM exposes it: ms since the Unix epoch, NaN when the license carries none (0 = unknown).
        double expiration = 0.0;
        int64_t expiresIn = 0;
        if (SUCCEEDED(session->GetExpiration(&expiration)) && std::isfinite(expiration) && expiration > 0.0)
        {
            const double left = expiration - fgpr::UnixMsNow();
            expiresIn = left > 0.0 ? (int64_t)left : 0;
        }
        lic.expiresInMs.store(expiresIn, std::memory_order_release);
        const int64_t ms = fgpr::MsSinceQpc(lic.acquireQpc);
        LogLine("[cdm] license kid=" + fgpr::Narrow(lic.kidHex) + " USABLE in " + std::to_string((long long)ms) +
                "ms expiresInMs=" + std::to_string((long long)expiresIn));
        Raise(lic.handle, FgPrEvent_LicenseUsable, ms, expiresIn, lic.kidHex.c_str());
    }
    else if (action == fgpr::licensing::KeyAction::BecomeExpired)
    {
        int32_t prev = lic.state.load(std::memory_order_acquire);
        do { if (prev != 0 && prev != 1) return; }
        while (!lic.state.compare_exchange_weak(prev, 2, std::memory_order_acq_rel));
        Raise(lic.handle, FgPrEvent_LicenseExpired, 0, 0, lic.kidHex.c_str());
    }
}

// Update() with the license the managed relay delivered (runtime thread).
// `deliveredQpc` is when the managed relay handed the license over (DeliverThunk): the gap to this item running is the runtime
// queue's wait (F226), logged with the Update() cost so the licence's native half is readable without subtracting log lines.
static void ApplyLicense(fgpr::Runtime& rt, const std::shared_ptr<fgpr::License>& lic, const std::vector<uint8_t>& license, int64_t deliveredQpc)
{
    if (!rt.Ready() || lic->closed.load(std::memory_order_acquire)) return;
    winrt::com_ptr<IMFContentDecryptionModuleSession> session = lic->KeySession();
    if (!session) { FailLicense(*lic, E_UNEXPECTED); return; }
    const int64_t waitedMs = fgpr::MsSinceQpc(deliveredQpc);
    const int64_t updateQpc = fgpr::QpcNow();
    HRESULT hu = session->Update(license.data(), (DWORD)license.size());
    LogLine("[cdm] Update() (relay) kid=" + fgpr::Narrow(lic->kidHex) + " license=" + std::to_string(license.size()) +
            "B hr=" + fgpr::Hex(hu) + " waitedMs=" + std::to_string((long long)waitedMs) +
            " updateMs=" + std::to_string((long long)fgpr::MsSinceQpc(updateQpc)));
    if (FAILED(hu))
    {
        // CRITICAL DIAGNOSTIC: dump a printable prefix of the relay license body so we can tell a genuine
        // license apart from a SOAP fault / error page.
        std::string head; head.reserve(600);
        for (size_t i = 0; i < license.size() && head.size() < 600; i++)
        {
            char c = (char)license[i];
            if (c == '\r' || c == '\n' || c == '\t') c = ' ';
            if (c >= 32 && c < 127) head.push_back(c);
        }
        LogLine("[cdm] relay license response head: " + head);
        // Surface a hard failure ONLY when Update() itself failed — a rejected license means every layer above would
        // otherwise spin on "Loading" forever. Do NOT treat "not usable yet" as an error here: the key status settles
        // ASYNCHRONOUSLY (KeyStatusChanged fires a beat later), so right after a SUCCESSFUL Update() the key is
        // legitimately still pending.
        FailLicense(*lic, hu);
        return;
    }
    QueryKeyStatus(lic);
}

/// The lane for a licence result: an attached session already binds this KID and its first frame waits on the Update, so
/// the result runs with the engine items; a licence nobody is attached to yields to them.
static fgpr::Lane LicenseLane(const fgpr::License& lic)
{
    return lic.bindCount.load(std::memory_order_acquire) > 0 ? fgpr::Lane::Engine : fgpr::Lane::Maintenance;
}

static void __stdcall DeliverThunk(void* deliverCtx, const uint8_t* license, int32_t licenseLen, int32_t hr)
{
    const uint64_t id = (uint64_t)(uintptr_t)deliverCtx;
    const int64_t deliveredQpc = fgpr::QpcNow();   // F226: when the relay handed the license over; ApplyLicense reports the queue wait from it
    DeliverRecord rec;
    {
        std::lock_guard<std::mutex> g(Deliveries().mx);
        auto it = Deliveries().live.find(id);
        if (it == Deliveries().live.end()) return;   // a second call, or after destroy: no-op
        rec = it->second;
        Deliveries().live.erase(it);
    }
    std::shared_ptr<fgpr::Runtime> rt = fgpr::RuntimeFor(rec.runtime);
    if (!rt) return;
    std::shared_ptr<fgpr::License> lic = fgpr::LicenseByHandle(rec.license);
    if (!lic) return;
    fgpr::Runtime* raw = rt.get();

    if (FAILED(hr) || !license || licenseLen <= 0)
    {
        // The relay produced no license: the key will never become usable. 0x80704005 (MF_TYPE_ERR) is the sentinel the
        // old relay path surfaced for "no license body" when the managed side reported success with nothing in it.
        const HRESULT failure = FAILED(hr) ? (HRESULT)hr : (HRESULT)0x80704005;
        LogLine("[cdm] managed relay produced no license kid=" + fgpr::Narrow(lic->kidHex) + " hr=" + fgpr::Hex(failure));
        rt->queue.Post([lic, failure] { FailLicense(*lic, failure); }, LicenseLane(*lic));
        return;
    }
    // ONE copy out of the managed buffer (§3.5: one byte[] per challenge) — the caller's memory is not ours after return.
    std::vector<uint8_t> bytes(license, license + (size_t)licenseLen);
    rt->queue.Post([raw, lic, bytes, deliveredQpc] { ApplyLicense(*raw, lic, bytes, deliveredQpc); }, LicenseLane(*lic));
}

// The session KeyMessage (CDM thread). The PlayReady KeyMessage is a UTF-16 XML envelope:
// <Challenge encoding="base64encoded">B64</Challenge> plus <HttpHeaders><HttpHeader><name/><value/>… Extract the
// base64 challenge AND the headers. The managed relay owns the license server, the token and the POST (native never
// holds a key/token), so the challenge goes UP and this function RETURNS — the old synchronous relay call plus the
// in-native Axinom POST fallback (FG_CENC_BAKED_AXINOM, FG_PLAYREADY_LICENSE_URL) are deleted.
static void HandleCdmKeyMessage(fgpr::License& lic, const BYTE* msg, DWORD cb, LPCWSTR destUrl)
{
    std::shared_ptr<fgpr::Runtime> rt = fgpr::RuntimeFor(lic.runtime);
    if (!rt || !lic.relay) return;   // destroyed: the managed relay context is gone, never call it

    std::wstring xml((const wchar_t*)msg, cb / sizeof(wchar_t));
    std::vector<uint8_t> challenge;
    std::vector<std::pair<std::wstring, std::wstring>> hdrs;
    size_t ctag = xml.find(L"<Challenge");
    size_t cgt = ctag != std::wstring::npos ? xml.find(L'>', ctag) : std::wstring::npos;
    size_t ce = xml.find(L"</Challenge>");
    if (cgt != std::wstring::npos && ce != std::wstring::npos)
    {
        std::wstring inner = xml.substr(cgt + 1, ce - (cgt + 1));
        // strip a possible CDATA wrapper
        size_t cd = inner.find(L"<![CDATA["); if (cd != std::wstring::npos) { inner = inner.substr(cd + 9); size_t e = inner.find(L"]]>"); if (e != std::wstring::npos) inner = inner.substr(0, e); }
        // trim whitespace
        while (!inner.empty() && iswspace(inner.front())) inner.erase(inner.begin());
        while (!inner.empty() && iswspace(inner.back())) inner.pop_back();
        challenge = Base64Decode(inner);
    }
    else challenge.assign(msg, msg + cb);
    // Parse <HttpHeader><name>..</name><value>..</value>
    size_t pos = 0;
    for (;;)
    {
        size_t ns = xml.find(L"<name>", pos); if (ns == std::wstring::npos) break;
        size_t ne = xml.find(L"</name>", ns); size_t vs = xml.find(L"<value>", ne); size_t ve = xml.find(L"</value>", vs);
        if (ne == std::wstring::npos || vs == std::wstring::npos || ve == std::wstring::npos) break;
        hdrs.emplace_back(xml.substr(ns + 6, ne - (ns + 6)), xml.substr(vs + 7, ve - (vs + 7)));
        pos = ve + 8;
    }
    std::string hdrNames;
    for (auto const& hd : hdrs) { if (!hdrNames.empty()) hdrNames += ","; hdrNames += fgpr::Narrow(hd.first); }
    LogLine("[cdm] KeyMessage kid=" + fgpr::Narrow(lic.kidHex) + " challenge=" + std::to_string(challenge.size()) +
            "B headers=[" + hdrNames + "] destUrl=" + fgpr::Narrow(destUrl ? std::wstring(destUrl) : std::wstring()));

    uint64_t id = 0;
    {
        std::lock_guard<std::mutex> g(Deliveries().mx);
        id = Deliveries().next++;
        Deliveries().live[id] = DeliverRecord{ lic.runtime, lic.handle };
    }
    const int32_t rc = lic.relay(lic.relayCtx, lic.handle, challenge.data(), (int32_t)challenge.size(),
                                 lic.kidHex.c_str(), &DeliverThunk, (void*)(uintptr_t)id);
    if (rc != 0)
    {
        {
            std::lock_guard<std::mutex> g(Deliveries().mx);
            Deliveries().live.erase(id);
        }
        LogLine("[cdm] managed relay refused the challenge rc=" + fgpr::Hex(rc));
        FailLicense(lic, (HRESULT)rc);
    }
}

struct CdmSessionCallbacks : public IMFContentDecryptionModuleSessionCallbacks
{
    std::atomic<long> rc{1};
    uint64_t m_license = 0;
    explicit CdmSessionCallbacks(uint64_t license) : m_license(license) {}
    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(::IUnknown) || riid == __uuidof(IMFContentDecryptionModuleSessionCallbacks)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (!v) delete this; return (ULONG)v; }
    HRESULT __stdcall KeyMessage(MF_MEDIAKEYSESSION_MESSAGETYPE, const BYTE* msg, DWORD cb, LPCWSTR url) override
    {
        std::shared_ptr<fgpr::License> lic = fgpr::LicenseByHandle(m_license);
        if (lic && !lic->closed.load(std::memory_order_acquire)) HandleCdmKeyMessage(*lic, msg, cb, url);
        return S_OK;
    }
    HRESULT __stdcall KeyStatusChanged() override
    {
        std::shared_ptr<fgpr::License> lic = fgpr::LicenseByHandle(m_license);
        if (lic && !lic->closed.load(std::memory_order_acquire)) QueryKeyStatus(lic);
        return S_OK;
    }
};

// Open the key session and generate the request (runtime thread). Moved from DriveCdmLicenseProactive minus its
// 200 ms × 30 s poll: GenerateRequest fires KeyMessage (possibly synchronously, on this thread), the relay takes the
// challenge, and this returns.
static void StartAcquisition(fgpr::Runtime& rt, const std::shared_ptr<fgpr::License>& lic)
{
    if (lic->closed.load(std::memory_order_acquire)) return;
    // Once: the attach verb starts its own licence ahead of itself, and the Maintenance item posted by FgPrLicenseAcquire is then a no-op.
    if (lic->started.exchange(true, std::memory_order_acq_rel)) return;
    // F226: how long this item sat in the runtime queue (acquire posted -> running): behind bring-up on a cold switch, ~0 warm.
    const int64_t waitedMs = fgpr::MsSinceQpc(lic->acquireQpc);
    if (!rt.Ready() || !rt.cdm)
    {
        const HRESULT bring = (HRESULT)rt.bringUp.load(std::memory_order_acquire);
        FailLicense(*lic, FAILED(bring) ? bring : MF_E_SHUTDOWN);
        return;
    }
    CdmSessionCallbacks* cb = new CdmSessionCallbacks(lic->handle);
    IMFContentDecryptionModuleSession* session = nullptr;
    // Session type MUST match the license's persistence or Update() rejects it with TypeError (MF_TYPE_ERR,
    // 0x80704005). Production = Spotify, which issues NON-persistable streaming licenses → TEMPORARY (verified: temp
    // session → Update() hr=0x0, key USABLE, plays). The persistent-license A/B arm (FG_CENC_PERSIST_SESSION, for the
    // Axinom entitlement's persistable test license) is deleted: TEMPORARY is the only session type created.
    const int64_t createQpc = fgpr::QpcNow();
    HRESULT hr = rt.cdm->CreateSession(MF_MEDIAKEYSESSION_TYPE_TEMPORARY, cb, &session);
    cb->Release();
    LogLine("[cenc] proactive CreateSession (temporary) kid=" + fgpr::Narrow(lic->kidHex) + " hr=" + fgpr::Hex(hr) +
            " waitedMs=" + std::to_string((long long)waitedMs) + " ms=" + std::to_string((long long)fgpr::MsSinceQpc(createQpc)));
    if (FAILED(hr) || !session) { FailLicense(*lic, FAILED(hr) ? hr : E_NOINTERFACE); return; }
    {
        std::lock_guard<std::mutex> g(lic->mx);
        lic->keySession.attach(session);   // kept alive so the key stays usable across every session that uses the KID
    }
    const int64_t generateQpc = fgpr::QpcNow();
    HRESULT hrg = session->GenerateRequest(L"cenc", lic->pssh.data(), (DWORD)lic->pssh.size());   // fires KeyMessage
    LogLine("[cenc] proactive GenerateRequest(cenc, " + std::to_string(lic->pssh.size()) + "B) hr=" + fgpr::Hex(hrg) +
            " ms=" + std::to_string((long long)fgpr::MsSinceQpc(generateQpc)));
    if (FAILED(hrg)) FailLicense(*lic, hrg);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Cross-TU.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

void fgpr::LicenseStartIfPending(Runtime& rt, uint64_t license)
{
    std::shared_ptr<License> lic = LicenseByHandle(license);
    if (!lic || lic->runtime != rt.handle) return;
    StartAcquisition(rt, lic);
}

void fgpr::LicenseBind(uint64_t license, int delta)
{
    std::shared_ptr<License> lic = LicenseByHandle(license);
    if (!lic) return;
    lic->lastUseQpc.store(QpcNow(), std::memory_order_release);
    int32_t n = lic->bindCount.fetch_add(delta, std::memory_order_acq_rel) + delta;
    if (n < 0) { lic->bindCount.store(0, std::memory_order_release); n = 0; }
    if (n == 0 && lic->releaseRequested.load(std::memory_order_acquire))
    {
        {
            std::lock_guard<std::mutex> g(Table().mx);
            auto& v = Table().entries;
            v.erase(std::remove(v.begin(), v.end(), lic), v.end());
        }
        PostClose(lic);
    }
}

void fgpr::LicensesShutdown(Runtime& rt)
{
    std::vector<std::shared_ptr<License>> mine;
    {
        std::lock_guard<std::mutex> g(Table().mx);
        auto& v = Table().entries;
        for (auto it = v.begin(); it != v.end(); )
        {
            if ((*it)->runtime == rt.handle) { mine.push_back(*it); it = v.erase(it); }
            else ++it;
        }
    }
    {
        std::lock_guard<std::mutex> g(Deliveries().mx);
        for (auto it = Deliveries().live.begin(); it != Deliveries().live.end(); )
        {
            if (it->second.runtime == rt.handle) it = Deliveries().live.erase(it);
            else ++it;
        }
    }
    for (auto const& lic : mine) CloseLicense(*lic);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Exports.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

__declspec(dllexport) int32_t __stdcall FgPrLicenseAcquire(FgPrRuntime rtHandle, const uint8_t* pssh, int32_t psshLen,
                                                           const wchar_t* keyIdHex, FgPrLicenseCallback relay,
                                                           void* relayCtx, FgPrLicense* out)
{
    if (!out) return E_POINTER;
    *out = 0;
    std::shared_ptr<fgpr::Runtime> rt = fgpr::RuntimeFor(rtHandle);
    if (!rt) return E_HANDLE;
    if (!relay || psshLen < 0 || (psshLen > 0 && !pssh)) return E_INVALIDARG;

    std::vector<uint8_t> initData;
    if (pssh && psshLen > 0) initData.assign(pssh, pssh + psshLen);

    uint8_t kid[16] = {};
    std::wstring kidHex;
    if (keyIdHex && *keyIdHex)
    {
        if (!fgpr::HexToKid(keyIdHex, kid)) return E_INVALIDARG;
        kidHex = fgpr::KidToHex(kid);
    }
    else
    {
        if (!KidFromPssh(initData, kid)) return E_INVALIDARG;   // no KID given and none recoverable from the PSSH
        kidHex = fgpr::KidToHex(kid);
    }
    if (initData.empty())
    {
        // No PSSH: build the PlayReady one from the KID — the proven fallback init data for GenerateRequest when the
        // content carries none. No LA_URL: the managed relay decides where the challenge is POSTed.
        initData = BuildPlayReadyPssh(kid, std::wstring());
    }

    std::shared_ptr<fgpr::License> lic;
    std::vector<std::shared_ptr<fgpr::License>> dropped;   // replaced by this acquisition (same KID, dead or stale): no event
    std::shared_ptr<fgpr::License> evicted;                // the LRU victim of a full table: the managed cache is told
    std::string staleNote;                                 // logged once the table lock is released
    {
        std::lock_guard<std::mutex> g(Table().mx);
        auto& v = Table().entries;
        for (auto it = v.begin(); it != v.end(); ++it)
        {
            auto const& e = *it;
            if (e->runtime != rtHandle || e->kidHex != kidHex) continue;
            const int32_t st = e->state.load(std::memory_order_acquire);
            // A Pending license older than the deadline is stale (a relay that hung, a CDM that never reported a status):
            // joining it would fail this open exactly as it failed the last one.
            const bool stalePending = st == 0 && fgpr::licensing::PendingStale(fgpr::MsSinceQpc(e->acquireQpc));
            if ((st == 0 && !stalePending) || st == 1)
            {
                // Cached usable or already pending: never a second challenge for the same KID.
                e->lastUseQpc.store(fgpr::QpcNow(), std::memory_order_release);
                *out = e->handle;
                return S_OK;
            }
            if (st == 2 && e->bindCount.load(std::memory_order_acquire) > 0) { *out = e->handle; return S_OK; }   // expired but in use: the session owns the recovery
            if (stalePending) staleNote = "[cdm] license kid=" + fgpr::Narrow(kidHex) + " pending for " +
                                          std::to_string((long long)fgpr::MsSinceQpc(e->acquireQpc)) + "ms: replaced by a fresh acquisition";
            if (e->bindCount.load(std::memory_order_acquire) > 0)
            {
                // Dead or stale but an attached session still holds it: it closes at that session's detach, and this
                // KID gets a fresh license beside it (never a key closed under a decoder).
                e->releaseRequested.store(true, std::memory_order_release);
                continue;
            }
            dropped.push_back(e);   // failed, stale or expired and unused: replaced by a fresh acquisition
            v.erase(it);
            break;
        }
        if (v.size() >= kLicenseCap)
        {
            // LRU among the entries no attached session uses; failed/expired ones go first.
            size_t victim = v.size();
            for (size_t i = 0; i < v.size(); i++)
            {
                if (v[i]->bindCount.load(std::memory_order_acquire) > 0) continue;
                if (victim == v.size()) { victim = i; continue; }
                const bool iDead = v[i]->state.load() != 0 && v[i]->state.load() != 1;
                const bool bestDead = v[victim]->state.load() != 0 && v[victim]->state.load() != 1;
                if ((iDead && !bestDead) ||
                    (iDead == bestDead && v[i]->lastUseQpc.load() < v[victim]->lastUseQpc.load()))
                    victim = i;
            }
            if (victim == v.size()) return HRESULT_FROM_WIN32(ERROR_TOO_MANY_OPEN_FILES);   // all 8 KIDs are in use
            evicted = v[victim];
            v.erase(v.begin() + (ptrdiff_t)victim);
        }
        lic = std::make_shared<fgpr::License>();
        lic->handle = fgpr::NewHandle(fgpr::HandleKind::License);
        lic->runtime = rtHandle;
        lic->kidHex = kidHex;
        lic->pssh = std::move(initData);
        lic->relay = relay;
        lic->relayCtx = relayCtx;
        lic->acquireQpc = fgpr::QpcNow();
        lic->lastUseQpc.store(lic->acquireQpc, std::memory_order_release);
        v.push_back(lic);
    }
    if (!staleNote.empty()) LogLine(staleNote);
    for (auto const& d : dropped) PostClose(d);
    if (evicted)
    {
        // The single eviction authority: the managed cache drops this row on the event (it no longer evicts live rows).
        LogLine("[cdm] license kid=" + fgpr::Narrow(evicted->kidHex) + " evicted (table full) to admit kid=" + fgpr::Narrow(kidHex));
        Raise(evicted->handle, FgPrEvent_LicenseEvicted, 0, 0, evicted->kidHex.c_str());
        PostClose(evicted);
    }

    fgpr::Runtime* raw = rt.get();
    if (!rt->queue.Post([raw, lic] { StartAcquisition(*raw, lic); }, fgpr::Lane::Maintenance))
    {
        FailLicense(*lic, MF_E_SHUTDOWN);
    }
    *out = lic->handle;
    return S_OK;
}

__declspec(dllexport) int32_t __stdcall FgPrLicenseState(FgPrRuntime rtHandle, FgPrLicense license)
{
    if (!fgpr::RuntimeFor(rtHandle)) return E_HANDLE;
    std::shared_ptr<fgpr::License> lic = fgpr::LicenseByHandle(license);
    if (!lic || lic->runtime != rtHandle) return E_HANDLE;
    // Asking is using: the managed cache probes a handle right before it hands it to an attach, and that attach must
    // not find the key evicted by a ninth KID in the window before the session binds it.
    lic->lastUseQpc.store(fgpr::QpcNow(), std::memory_order_release);
    return lic->state.load(std::memory_order_acquire);
}

__declspec(dllexport) void __stdcall FgPrLicenseRelease(FgPrRuntime rtHandle, FgPrLicense license)
{
    if (!fgpr::RuntimeFor(rtHandle)) return;
    std::shared_ptr<fgpr::License> lic = fgpr::LicenseByHandle(license);
    if (!lic || lic->runtime != rtHandle) return;
    EvictLicense(lic);   // an attached session still decrypting with this key keeps it until the last one detaches
}
