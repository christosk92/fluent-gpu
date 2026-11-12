// MfAudioGuard.h — an in-process guard for ONE Windows Media Foundation race: the policy engine reading the audio sink's
// trusted output before the sink's asynchronous audio-client set-up has filled it in.
//
// THE FAULT (crash 2026-11-12 21:19:27, mfcore.dll 10.0.26100.9882, ARM64). A protected start runs, on an RTWorkQ thread,
//   CMediaSession::HandleSourceStarted -> StartPlayback -> CPolicyEngine::NotifySessionStarted -> GetTAListFromTopology ->
//   CPETopologyWalker::BuildITAContexts -> TraceTopologyDownstream -> CAudStreamSink::GetOutputTrustAuthorityCount ->
//   CAudioClientWrapperBase::GetOutputTrustAuthorityCount
// and the last one, outside lie mode, calls the wrapper's inner IMFTrustedOutput without a null check:
//   +0x78  f9401a60  ldr x0,[x19,#0x30]       the inner IMFTrustedOutput (x19 = the wrapper's IMFTrustedOutput face)
//   +0x7c  91036261  add x1,x19,#0xD8         &the wrapper's cached count (the out parameter)
//   +0x80  f9400008  ldr x8,[x0]              <- the AV: x0 == 0
//   +0x84  f9400d08  ldr x8,[x8,#0x18]        vtable slot 3, IMFTrustedOutput::GetOutputTrustAuthorityCount
//   +0x88  aa0803ef  mov x15,x8               then __os_arm64x_dispatch_call_no_redirect (a `ret` in a native process), blr x15
//   +0x9c  18000c28  ldr w8,=0x88890026       AUDCLNT_E_RESOURCES_INVALIDATED (the literal at +0x220)
//          cmp w20,w8 ; b.ne +0xfc            equal -> SwitchToLieMode, then the call again through the wrapper's own vtable
//                                             otherwise: trace the failure, return the HRESULT (no write to the caller's count)
// The field is written by the sink's asynchronous audio-client set-up; the crash dump shows it non-null 0.8 s later, written
// while the faulting thread sat in the crash handler - so the write never needs the faulting thread. About 1 in 150
// protected starts, the policy engine gets there first.
//
// WHAT THE GUARD DOES. Arming (once per runtime): pin mfcore.dll and find that instruction sequence by scanning its executable
// sections, symbol-free. mfcore is an ARM64X image: the four words occur TWICE, once in the native ARM64 code and once in the
// ARM64EC copy (which calls through x11, `aa0803eb`, and never runs in a native ARM64 process). The guard arms only when the
// native form - the four words followed by `mov x15,x8` - occurs exactly once. Acting: a FIRST vectored handler that touches
// nothing but EXCEPTION_ACCESS_VIOLATION (read) at exactly that pc with x0 == 0 and x1 == x19 + 0xD8, i.e. this sequence
// with its operands. It then
//   1. waits (bounded, kWaitBudgetMs) for the asynchronous set-up to publish [x19+0x30]: when it lands, x0 := that pointer and
//      the faulting load re-executes as if the race had never happened (the dump says the writer does not need this thread);
//   2. otherwise x0 := a static stand-in IMFTrustedOutput whose GetOutputTrustAuthorityCount (slot 3) returns kStandInHr,
//      AUDCLNT_E_NOT_INITIALIZED. That is NOT the lie-mode constant, so mfcore takes its own failure path: the wrapper traces
//      and returns it (the cached count and the caller's out parameter are left alone), every caller up the chain propagates
//      it (each `tbz x,#31` falls into its trace-and-return), StartPlayback answers a failed NotifySessionStarted with
//      CMediaSession::_PolicyStop, and HandleSourceStarted completes the session's Start with the failure
//      (CompleteAsyncOperation). The media engine reports it as an ERROR; the runtime maps that ERROR to kStandInHr (the
//      engine may wrap it) and the managed side re-opens the video once (ProtectedRuntimeFaults.IsTransientStart).
//      Lie mode (AUDCLNT_E_RESOURCES_INVALIDATED) is deliberately NOT chosen: it latches the wrapper into pretending its
//      output is trusted for the rest of the session (SwitchToLieMode sets a flag nothing here can clear), and what the sink
//      renders in that mode is not ours to guess.
// Only the register changes: [x19+0x30] keeps whatever the set-up writes, lr/sp/fp (and the signed return address
// `pacibsp` pushed) are untouched, and the stand-in's methods are ordinary native ARM64 functions in this DLL (built without
// /guard:cf, so a valid indirect-call target). Every other exception: EXCEPTION_CONTINUE_SEARCH before anything else is read,
// so the app's crash gate (Wavee Crash.FaultGate, registered LAST) still sees every other fault.
#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <mfidl.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <string>

#pragma comment(lib, "kernel32.lib")

namespace fgpr::mfguard {

/// AUDCLNT_E_NOT_INITIALIZED: what the stand-in answers, and what the runtime reports for the start the guard failed.
constexpr HRESULT kStandInHr = (HRESULT)0x88890001L;
/// AUDCLNT_E_RESOURCES_INVALIDATED: the literal at +0x220 that sends the wrapper into lie mode. Never answered.
constexpr HRESULT kLieModeHr = (HRESULT)0x88890026L;
/// How long the handler waits for the real IMFTrustedOutput before it fails the start with the stand-in.
constexpr int64_t kWaitBudgetMs = 1000;

/// The four words from `ldr x0,[x19,#0x30]` to the vtable load, and the word that tells the native copy from the EC one.
constexpr uint32_t kPattern[4] = { 0xf9401a60u, 0x91036261u, 0xf9400008u, 0xf9400d08u };
constexpr uint32_t kNativeNext = 0xaa0803efu;   // mov x15,x8 (the native call through __os_arm64x_dispatch_call_no_redirect)
constexpr uint32_t kEcNext = 0xaa0803ebu;       // mov x11,x8 (the ARM64EC copy's call through x11)
constexpr uintptr_t kFaultOffset = 8;           // `ldr x8,[x0]` is the third word
constexpr uintptr_t kInnerOffset = 0x30;        // [x19 + 0x30]: the inner IMFTrustedOutput
constexpr uintptr_t kCountOffset = 0xD8;        // x1 = x19 + 0xD8: the out parameter

using LogFn = void (*)(const std::string&);
using FallbackFn = void (*)(void* ctx);

/// The stand-in inner IMFTrustedOutput. Static, never freed, reference counts ignored; every method fails with kStandInHr and
/// writes nothing through an out parameter it was not asked to clear (the count pointer is the wrapper's own cached count).
class StandInTrustedOutput final : public IMFTrustedOutput
{
  public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFTrustedOutput))
        {
            *ppv = static_cast<IMFTrustedOutput*>(this);
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP GetOutputTrustAuthorityCount(DWORD* /*count: left as it is*/) override { return kStandInHr; }
    STDMETHODIMP GetOutputTrustAuthorityByIndex(DWORD, IMFOutputTrustAuthority** ota) override
    {
        if (ota) *ota = nullptr;
        return kStandInHr;
    }
    STDMETHODIMP IsFinal(BOOL* isFinal) override
    {
        if (isFinal) *isFinal = FALSE;
        return kStandInHr;
    }
};

/// What a scan of one module found.
struct ScanResult
{
    int total = 0;              // the four words, anywhere executable
    int native = 0;             // ... followed by `mov x15,x8`
    int ec = 0;                 // ... followed by `mov x11,x8`
    uintptr_t nativeAt = 0;     // the first word of the (last) native match
    size_t scannedBytes = 0;
    uint32_t imageStamp = 0;    // the header's TimeDateStamp (a reproducible-build hash on /Brepro images) and size, for the log
    uint32_t imageSize = 0;
};

/// Scan every executable section of a mapped image for the sequence, 4-byte aligned. Symbol-free (headers only).
inline ScanResult ScanModule(HMODULE module)
{
    ScanResult r;
    auto* base = reinterpret_cast<const uint8_t*>(module);
    if (!base) return r;
    auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return r;
    auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return r;
    r.imageStamp = nt->FileHeader.TimeDateStamp;
    r.imageSize = nt->OptionalHeader.SizeOfImage;
    const IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; i++, sec++)
    {
        if (!(sec->Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;
        const size_t bytes = sec->Misc.VirtualSize ? sec->Misc.VirtualSize : sec->SizeOfRawData;
        if (sec->VirtualAddress == 0 || (size_t)sec->VirtualAddress + bytes > r.imageSize) continue;
        auto* words = reinterpret_cast<const uint32_t*>(base + sec->VirtualAddress);
        const size_t n = bytes / 4;
        r.scannedBytes += bytes;
        for (size_t w = 0; w + 4 < n; w++)
        {
            if (words[w] != kPattern[0] || words[w + 1] != kPattern[1] || words[w + 2] != kPattern[2] || words[w + 3] != kPattern[3])
                continue;
            r.total++;
            if (words[w + 4] == kNativeNext) { r.native++; r.nativeAt = reinterpret_cast<uintptr_t>(&words[w]); }
            else if (words[w + 4] == kEcNext) r.ec++;
        }
    }
    return r;
}

// ── the armed state (process-wide; never freed: an MF thread may be inside the handler at DLL detach) ────────────────────
struct GuardState
{
    std::atomic<uintptr_t> faultPc{ 0 };          // the armed `ldr x8,[x0]`; 0 = disarmed
    std::atomic<bool> enabled{ false };
    std::atomic<int> inflight{ 0 };               // handlers past the pc check (Disarm waits for them)
    std::atomic<uint64_t> realCatches{ 0 };       // resumed on the real IMFTrustedOutput
    std::atomic<uint64_t> standInCatches{ 0 };    // failed with the stand-in
    std::atomic<uintptr_t> log{ 0 };              // LogFn
    std::atomic<uintptr_t> fallback{ 0 };         // FallbackFn
    std::atomic<void*> fallbackCtx{ nullptr };
    StandInTrustedOutput* standIn = nullptr;
    PVOID handler = nullptr;                      // AddVectoredExceptionHandler's cookie (arming thread only)
    HMODULE pin = nullptr;                        // the scanned module, held while armed (arming thread only)
    std::string moduleName;
};

inline GuardState& State()
{
    static GuardState* g = new GuardState();
    return *g;
}

inline StandInTrustedOutput& StandIn()
{
    static StandInTrustedOutput s;
    return s;
}

inline void Log(const std::string& line)
{
    if (auto fn = reinterpret_cast<LogFn>(State().log.load(std::memory_order_acquire))) fn(line);
}

inline std::string HexU(uint64_t v)
{
    char b[24];
    snprintf(b, sizeof(b), "0x%llx", (unsigned long long)v);
    return b;
}

inline LONG CALLBACK Handler(EXCEPTION_POINTERS* ep)
{
#if defined(_M_ARM64)
    // The fast path: one code compare and one address compare for every exception the process raises.
    if (!ep || !ep->ExceptionRecord || !ep->ContextRecord) return EXCEPTION_CONTINUE_SEARCH;
    const EXCEPTION_RECORD* er = ep->ExceptionRecord;
    if (er->ExceptionCode != EXCEPTION_ACCESS_VIOLATION) return EXCEPTION_CONTINUE_SEARCH;
    GuardState& g = State();
    const uintptr_t pc = g.faultPc.load(std::memory_order_acquire);
    if (pc == 0 || reinterpret_cast<uintptr_t>(er->ExceptionAddress) != pc) return EXCEPTION_CONTINUE_SEARCH;
    CONTEXT* c = ep->ContextRecord;
    if (c->Pc != pc || c->X0 != 0 || c->X19 == 0 || c->X1 != c->X19 + kCountOffset) return EXCEPTION_CONTINUE_SEARCH;
    if (er->NumberParameters < 2 || er->ExceptionInformation[0] != 0 /*read*/ || er->ExceptionInformation[1] >= 0x10000)
        return EXCEPTION_CONTINUE_SEARCH;

    g.inflight.fetch_add(1, std::memory_order_acq_rel);
    if (!g.enabled.load(std::memory_order_acquire))
    {
        g.inflight.fetch_sub(1, std::memory_order_acq_rel);
        return EXCEPTION_CONTINUE_SEARCH;
    }

    // 1. The asynchronous set-up publishes the inner pointer from another thread: give it the budget. x19 is the live wrapper
    //    the faulting function was called on (it just loaded x0 from this very field).
    auto* field = reinterpret_cast<volatile uintptr_t*>(c->X19 + kInnerOffset);
    LARGE_INTEGER f, t0, now;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t0);
    uintptr_t inner = *field;
    int64_t waitedMs = 0;
    while (inner == 0)
    {
        QueryPerformanceCounter(&now);
        waitedMs = (now.QuadPart - t0.QuadPart) * 1000 / (f.QuadPart ? f.QuadPart : 1);
        if (waitedMs >= kWaitBudgetMs) break;
        Sleep(1);
        inner = *field;
    }
    std::atomic_thread_fence(std::memory_order_acquire);   // the object behind the published pointer, before its vtable load
    QueryPerformanceCounter(&now);
    waitedMs = (now.QuadPart - t0.QuadPart) * 1000 / (f.QuadPart ? f.QuadPart : 1);

    if (inner != 0)
    {
        c->X0 = inner;
        const uint64_t n = g.realCatches.fetch_add(1, std::memory_order_acq_rel) + 1;
        Log("[mf.guard] caught mfcore audio OTA race (x0=null) -> the real OTA landed after " + std::to_string((long long)waitedMs) +
            "ms, resumed on it (catch " + std::to_string((unsigned long long)n) + ")");
    }
    else
    {
        // 2. Nothing came: fail the start through mfcore's own error path.
        c->X0 = reinterpret_cast<DWORD64>(static_cast<IMFTrustedOutput*>(g.standIn));
        const uint64_t n = g.standInCatches.fetch_add(1, std::memory_order_acq_rel) + 1;
        Log("[mf.guard] caught mfcore audio OTA race (x0=null) -> stand-in hr=" + HexU((uint32_t)kStandInHr) + " (no OTA after " +
            std::to_string((long long)waitedMs) + "ms; the session start fails, catch " + std::to_string((unsigned long long)n) + ")");
        if (auto fb = reinterpret_cast<FallbackFn>(g.fallback.load(std::memory_order_acquire)))
            fb(g.fallbackCtx.load(std::memory_order_acquire));
    }
    g.inflight.fetch_sub(1, std::memory_order_acq_rel);
    return EXCEPTION_CONTINUE_EXECUTION;   // the `ldr x8,[x0]` re-executes against the pointer now in x0
#else
    (void)ep;
    return EXCEPTION_CONTINUE_SEARCH;
#endif
}

/// Arm on `module` (already loaded; pinned here for as long as the guard is armed). True when armed. One module at a time.
inline bool ArmOn(HMODULE module, const char* moduleName, LogFn log, FallbackFn fallback = nullptr, void* fallbackCtx = nullptr)
{
    GuardState& g = State();
    g.log.store(reinterpret_cast<uintptr_t>(log), std::memory_order_release);
#if !defined(_M_ARM64)
    (void)module; (void)fallback; (void)fallbackCtx;
    Log(std::string("[mf.guard] not armed (not an ARM64 process; module=") + moduleName + ")");
    return false;
#else
    if (g.handler) return true;
    if (!module) { Log(std::string("[mf.guard] not armed (") + moduleName + " not loaded)"); return false; }
    LARGE_INTEGER f, t0, t1;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t0);
    const ScanResult r = ScanModule(module);
    QueryPerformanceCounter(&t1);
    const long long scanMs = (long long)((t1.QuadPart - t0.QuadPart) * 1000 / (f.QuadPart ? f.QuadPart : 1));
    const std::string found = "pattern found " + std::to_string(r.total) + " time(s): native=" + std::to_string(r.native) +
                              " arm64ec=" + std::to_string(r.ec) + "; image " + HexU(r.imageStamp) + " size " + HexU(r.imageSize) +
                              ", scanned " + std::to_string((unsigned long long)(r.scannedBytes / 1024)) + " KiB in " +
                              std::to_string(scanMs) + "ms";
    if (r.native != 1)
    {
        Log(std::string("[mf.guard] not armed (") + found + ")");
        return false;
    }
    g.standIn = &StandIn();
    g.fallback.store(reinterpret_cast<uintptr_t>(fallback), std::memory_order_release);
    g.fallbackCtx.store(fallbackCtx, std::memory_order_release);
    g.moduleName = moduleName;
    const uintptr_t pc = r.nativeAt + kFaultOffset;
    g.faultPc.store(pc, std::memory_order_release);
    g.enabled.store(true, std::memory_order_release);
    // FIRST: ahead of every handler registered before it, including the app's crash gate (registered last), which must never
    // take this fault for a crash. Everything else the handler answers with CONTINUE_SEARCH.
    g.handler = AddVectoredExceptionHandler(1, &Handler);
    if (!g.handler)
    {
        g.enabled.store(false, std::memory_order_release);
        g.faultPc.store(0, std::memory_order_release);
        Log("[mf.guard] not armed (AddVectoredExceptionHandler failed err=" + std::to_string((unsigned long)GetLastError()) + ")");
        return false;
    }
    // Pin the module: the armed pc must stay the code it was scanned from.
    HMODULE pinned = nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(module), &pinned)) g.pin = pinned;
    Log(std::string("[mf.guard] armed module=") + moduleName + " rva=" + HexU(pc - reinterpret_cast<uintptr_t>(module)) + " (" + found + ")");
    return true;
#endif
}

/// Arm on the system mfcore.dll (loaded here if Media Foundation has not loaded it yet).
inline bool Install(LogFn log, FallbackFn fallback = nullptr, void* fallbackCtx = nullptr)
{
    HMODULE mfcore = LoadLibraryExW(L"mfcore.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    const bool armed = ArmOn(mfcore, "mfcore.dll", log, fallback, fallbackCtx);
    if (mfcore) FreeLibrary(mfcore);   // ArmOn holds its own reference while armed
    return armed;
}

/// Disarm: remove the handler, wait (bounded) for one still inside, release the module. Idempotent.
inline void Uninstall()
{
    GuardState& g = State();
    if (!g.handler) return;
    g.enabled.store(false, std::memory_order_release);
    RemoveVectoredExceptionHandler(g.handler);
    g.handler = nullptr;
    // A handler past the enabled check may still be waiting for the real OTA (kWaitBudgetMs); it uses the module's memory.
    for (int i = 0; i < 300 && g.inflight.load(std::memory_order_acquire) != 0; i++) Sleep(5);
    const bool drained = g.inflight.load(std::memory_order_acquire) == 0;
    g.faultPc.store(0, std::memory_order_release);
    g.fallback.store(0, std::memory_order_release);
    g.fallbackCtx.store(nullptr, std::memory_order_release);
    if (g.pin && drained) FreeLibrary(g.pin);   // a handler still inside keeps it (leaked, never unmapped under it)
    g.pin = nullptr;
    Log("[mf.guard] disarmed (" + g.moduleName + ": resumed on the real OTA " +
        std::to_string((unsigned long long)g.realCatches.load(std::memory_order_acquire)) + "x, failed with the stand-in " +
        std::to_string((unsigned long long)g.standInCatches.load(std::memory_order_acquire)) + "x)");
    g.log.store(0, std::memory_order_release);
}

}   // namespace fgpr::mfguard
