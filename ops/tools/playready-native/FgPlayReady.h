// FgPlayReady.h — the C ABI between FluentGpu.WindowsApi (managed) and FluentGpu.PlayReady.Native.dll.
//
// THE SHAPE, AND WHY IT CHANGED (wavee-0.3-video-engine-implementation.md §3.1.1). The old ABI was session-less:
// one blocking `FgPlayReadyRunEx` owned MF, the D3D11 device, the CDM, the PMP host, the CENC source and the media
// engine for the lifetime of ONE source, a process-global CAS latch answered ERROR_BUSY while it ran, and every
// transport verb was a slot applied by an 80 ms keep-alive tick. A song→video switch therefore paid MFStartup,
// D3D11CreateDevice, CDM + mfpmp.exe bring-up, a fresh license challenge and ten serial CDN GETs — every time.
//
// This ABI is a process-lifetime RUNTIME with HANDLES:
//   * FgPrRuntime  — created once: MF, the D3D11 video device + IMFDXGIDeviceManager, ONE IMFMediaEngine in
//                    windowless swap-chain mode, ONE IMFContentDecryptionModule with its PMP host, and ONE MTA
//                    thread with a work queue. Every verb below is a work item on that thread; nothing polls.
//   * FgPrLicense  — one per KID: an open CDM key session, acquired the moment a manifest is known (PlayReady's
//                    "proactive acquisition") and kept until it expires or is evicted.
//   * FgPrSession  — one per source: a CencMediaSource over a byte-capped, time-windowed SegmentStore, opened AT
//                    the carried start position. The switch is FgPrSessionAttach = ONE SetSource on the live engine.
//
// Every call is NON-BLOCKING unless its comment says otherwise; the only blocking export is FgPrRuntimeDestroy
// (it joins the runtime thread, bounded). Handles are opaque uint64_t and 0 is never a valid handle. A call with a
// stale handle is a no-op returning E_HANDLE, never a crash and never a wrong session (that is the whole point of
// the change: a predecessor's teardown can no longer land on its successor).
//
// Return convention: int32_t HRESULT. S_OK (0) on success; a COM-shaped failure otherwise (E_HANDLE 0x80070006 for
// an unknown handle, E_INVALIDARG 0x80070057 for a malformed descriptor, MF_E_* / DRM_E_* passed through verbatim).
// void-returning exports cannot fail observably.
//
// Threading: the exports may be called from ANY thread. State is published through FgPrSessionSnapshot (one atomic
// read of a POD — the same shape the managed VideoEngineSnapshot has) and through FgPrEventCallback, which fires on
// the runtime thread or an MF thread and must return promptly (the managed sink only wakes a pump).
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef uint64_t FgPrRuntime;    // one per process
typedef uint64_t FgPrLicense;    // one per KID
typedef uint64_t FgPrSession;    // one per source

// ── events ──────────────────────────────────────────────────────────────────────────────────────────────────────────

/// Log + state callback: EVERY native line goes here (the managed side writes it into the app's ONE log under the
/// `[video.native]` tag; desktop-playready.log is retired). `session` is the session handle the event belongs to, the
/// LICENSE handle for the FgPrEvent_License* events, or 0 for runtime-level events. `a`/`b` carry the event's numbers
/// (see FgPrEvent); `text` is a NUL-terminated UTF-16 detail string owned by the callee's frame — copy it or format it
/// out before returning, never retain the pointer. Never called per video frame.
typedef void(__stdcall* FgPrEventCallback)(void* ctx, uint64_t session, int32_t event, int64_t a, int64_t b,
                                           const wchar_t* text);

/// The license relay's completion. The managed side POSTs the challenge and calls this from ANY thread, LATER; the
/// native side never waits for it (the old `task.Wait(30 s)` on the CDM thread is gone). Exactly one call per
/// challenge: `license`/`licenseLen` on success with `hr` = 0, or `license` = null with a failure `hr`. Calling it
/// twice, or after FgPrRuntimeDestroy, is a no-op.
typedef void(__stdcall* FgPrLicenseDeliver)(void* deliverCtx, const uint8_t* license, int32_t licenseLen, int32_t hr);

/// The license relay itself: the CDM's challenge goes UP to managed. Invoked on the CDM's own thread and expected to
/// RETURN IMMEDIATELY (queue the POST, hand `deliver`+`deliverCtx` to it). Return 0 to say "accepted, a deliver is
/// coming", or a failure HRESULT to fail the acquisition now (no deliver will follow).
typedef int32_t(__stdcall* FgPrLicenseCallback)(void* ctx, FgPrLicense license, const uint8_t* challenge,
                                                int32_t challengeLen, const wchar_t* keyIdHex,
                                                FgPrLicenseDeliver deliver, void* deliverCtx);

enum FgPrEvent
{
    FgPrEvent_RuntimeReady = 1,      // a = ms spent in bring-up
    FgPrEvent_RuntimeFailed = 2,     // a = hr
    // The three license events carry the LICENSE handle in `session` and the KID (32 hex chars) in `text`: the
    // acquisition can complete before FgPrLicenseAcquire's caller has recorded the handle, so managed matches by KID.
    FgPrEvent_LicenseUsable = 10,    // a = ms since acquire; b = expires-in ms (0 = unknown)
    FgPrEvent_LicenseFailed = 11,    // a = hr
    FgPrEvent_LicenseExpired = 12,
    FgPrEvent_Bytes = 20,            // a = bytes downloaded (cumulative), b = ms transferring (cumulative) — ≤ 4 Hz
    FgPrEvent_Buffered = 21,         // a = forward buffered ms, b = backward retained ms
    FgPrEvent_Keyframes = 22,        // a = segment index parsed; the keyframe table grew (poll FgPrSessionGetKeyframes)
    FgPrEvent_Metadata = 30,         // a = duration ms, b = (width << 32) | height
    FgPrEvent_CanPlay = 31,
    FgPrEvent_FirstFrame = 32,       // MF_MEDIA_ENGINE_EVENT_FIRSTFRAMEREADY; a = presentation position ms
    FgPrEvent_Handle = 33,           // a = the DComp swapchain handle (re-raised on FORMATCHANGE/RESOURCELOST)
    FgPrEvent_Position = 34,         // a = position ms, b = QPC ticks of the sample — ≤ 4 Hz, never a pump trigger
    FgPrEvent_Seeking = 35,          // a = target ms
    FgPrEvent_Seeked = 36,           // a = landed ms, b = ms since the seek was posted
    FgPrEvent_Playing = 37,
    FgPrEvent_Paused = 38,
    FgPrEvent_Ended = 39,
    FgPrEvent_Error = 40,            // a = MF_MEDIA_ENGINE_ERR, b = hr
    FgPrEvent_Representation = 41,   // a = active representation index
    FgPrEvent_Attached = 42,         // a = ms from Attach to SetSource returning
    FgPrEvent_Detached = 43,
    FgPrEvent_Log = 44,              // a diagnostic line with no numbers of its own; `text` is the line
};

/// FgPrSnapshot.state — the lifecycle the managed ProtectedVideoState maps one-to-one.
enum FgPrState
{
    FgPrState_Idle = 0,
    FgPrState_Loading = 1,      // created, fetching / attaching; no first frame yet
    FgPrState_Playing = 2,
    FgPrState_Paused = 3,
    FgPrState_Stopped = 4,      // detached; the store survives
    FgPrState_Error = 5,
    FgPrState_Ended = 6,
};

/// FgPrSessionSeek modes.
enum FgPrSeekMode
{
    FgPrSeekMode_Exact = 0,       // SetCurrentTime — decode from the keyframe to the exact PTS (a commit)
    FgPrSeekMode_Keyframe = 1,    // SetCurrentTimeEx(APPROXIMATE) — present the keyframe ≤ target (a scrub preview)
};

// ── descriptors ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// The source of one session. `structSize` MUST be sizeof(FgPrOpenDesc): a mismatch is E_INVALIDARG (the old
/// per-field `offsetof` ABI-probing is gone — header and DLL ship together).
///
/// Segment addressing is arithmetic, exactly as Spotify names segments: the i-th media segment's URL is
/// `segmentBaseUrl + segmentPrefix + (startNumber + i*segmentStrideSeconds) + segmentSuffix`. `segmentLengthMs` is
/// the PRESENTATION length of one segment (Spotify's `segment_length` × 1000) and is what maps a position to an
/// index: `segmentOf(ms) = ms / segmentLengthMs`. The audio representation rides the SAME grid — only its four URL
/// parts differ.
typedef struct FgPrOpenDesc
{
    uint32_t structSize;

    const wchar_t* initUrl;             // video init segment (moov)
    const wchar_t* segmentBaseUrl;
    const wchar_t* segmentPrefix;
    const wchar_t* segmentSuffix;
    int32_t startNumber;                // the first segment's number/timestamp
    int32_t segmentCount;               // total segments in the presentation (0 = derive from durationMs)
    int32_t segmentStrideSeconds;       // the step between consecutive segment NUMBERS (1 for $Number$ content)
    int32_t segmentLengthMs;            // the presentation length of one segment; 0 = unstated (stride×1000 until the first
                                        // parsed segment, then measured from its sample durations)

    const wchar_t* audioInitUrl;        // null ⇒ video only
    const wchar_t* audioSegmentBaseUrl;
    const wchar_t* audioSegmentPrefix;
    const wchar_t* audioSegmentSuffix;

    const uint8_t* pssh;                // the PlayReady PSSH box (may be null: parsed from the init segment instead)
    int32_t psshLen;
    const wchar_t* keyIdHex;            // the content KID as 32 hex chars (may be null: parsed from the init segment)
    const wchar_t* httpHeaders;         // "Name: Value\n" lines applied to every segment fetch (CDN auth)

    int64_t durationMs;                 // the manifest's duration, 0 = unknown (the demuxer extrapolates)
    int64_t startPositionMs;            // the feeder starts at floor(start / segmentLengthMs); the source's first
                                        // Start lands at the keyframe ≤ this, so a switch at 1:23 never shows 0:00
    int32_t startPaused;                // 1 = open paused at startPositionMs and raise FirstFrame (FrameStep) without playing
    int64_t retainBehindMs;             // time-based retention behind the playhead (0 ⇒ 30 000)
    int64_t bufferAheadMs;              // forward buffering target (0 ⇒ 60 000)
    int64_t storeBudgetBytes;           // the SegmentStore cap for THIS session (0 ⇒ 32 MiB)
} FgPrOpenDesc;

/// One atomic read of a session's observable state — the same shape the managed VideoEngineSnapshot has, so the
/// protected and clear backends publish one seam. `structSize` MUST be sizeof(FgPrSnapshot).
typedef struct FgPrSnapshot
{
    uint32_t structSize;
    int32_t state;                  // FgPrState
    int32_t errorHr;
    int32_t readyState;             // 0 HAVE_NOTHING … 4 HAVE_ENOUGH_DATA
    uint64_t handle;                // the DComp swapchain handle, 0 until FgPrEvent_Handle
    int32_t width, height;          // the natural frame size
    int32_t seeking;                // 1 while a seek is in flight
    int32_t activeRepresentation;   // -1 when the source is not adaptive
    int64_t positionMs;
    int64_t positionQpc;            // QueryPerformanceCounter ticks AT which positionMs was sampled (extrapolate from here)
    int64_t durationMs;
    int64_t bufferedAheadMs;
    int64_t retainedBehindMs;
    int64_t firstFrameQpc;          // QPC of FIRSTFRAMEREADY for this source, 0 until it lands
    uint64_t bytesDownloaded;
    uint64_t downloadElapsedMs;
    uint64_t storeBytes;            // what the SegmentStore currently holds (the counted half of the memory budget)
} FgPrSnapshot;

/// What FgPrProbeFile answers about a local fragmented MP4 — the demuxer gate's read-out (tests only; no CDM, no GPU).
typedef struct FgPrProbeResult
{
    uint32_t structSize;
    int32_t keyframeCount;      // sync samples found (the caller's buffer may have held fewer)
    int32_t sampleCount;
    int32_t width, height;
    int32_t nalLengthSize;      // 4 is the only accepted value; anything else is reported and refused
    int32_t encrypted;          // 1 when the track carries a `tenc` / `senc` subsample map
    int32_t subsampleCount;     // subsample entries seen across the parsed segments (0 for clear content)
    int64_t durationMs;
} FgPrProbeResult;

// ── runtime ─────────────────────────────────────────────────────────────────────────────────────────────────────────

/// Create the process runtime: MFStartup, the D3D11 video device + IMFDXGIDeviceManager, ONE IMFMediaEngine in
/// windowless swap-chain mode with its notify sink, ONE CDM with its PMP host, and the MTA runtime thread. Returns
/// as soon as the thread is up; FgPrEvent_RuntimeReady (or FgPrEvent_RuntimeFailed) says when bring-up finished.
/// `storePath` is the CDM's MF_CONTENTDECRYPTIONMODULE_STOREPATH directory (created if absent). A second call
/// returns the EXISTING runtime handle and does no work.
__declspec(dllexport) int32_t __stdcall FgPrRuntimeCreate(const wchar_t* storePath, FgPrEventCallback cb, void* ctx,
                                                          FgPrRuntime* out);

/// Destroy the runtime: detach + destroy every session, release every license, shut the engine, the CDM, the D3D11
/// device and MF down, and JOIN the runtime thread (bounded 2 s). The ONLY blocking export. Idempotent.
__declspec(dllexport) void __stdcall FgPrRuntimeDestroy(FgPrRuntime rt);

/// How long the runtime thread has been alive, in ms — the timeline every `[video.native]` line is stamped with.
__declspec(dllexport) int64_t __stdcall FgPrRuntimeUptimeMs(FgPrRuntime rt);

// ── license cache (KID-keyed; the runtime owns the CDM; key sessions stay open until Expired or Release) ─────────────

/// Start (or join) a license acquisition for `keyIdHex`. Returns S_OK with an EXISTING handle when the KID is already
/// cached usable or pending — there is never a second challenge for the same KID. Otherwise opens a TEMPORARY CDM key
/// session, calls GenerateRequest("cenc", pssh) and invokes `relay` with the challenge from the CDM's thread; the
/// relay's `deliver` runs Update() and FgPrEvent_LicenseUsable follows when KeyStatusChanged says so. NON-BLOCKING:
/// no 200 ms poll, no 30 s wait. The table holds 8 KIDs, LRU-evicted, and never evicts a KID an attached session uses.
__declspec(dllexport) int32_t __stdcall FgPrLicenseAcquire(FgPrRuntime rt, const uint8_t* pssh, int32_t psshLen,
                                                           const wchar_t* keyIdHex, FgPrLicenseCallback relay,
                                                           void* relayCtx, FgPrLicense* out);

/// 0 pending, 1 usable, 2 expired, and a negative HRESULT when the acquisition failed.
__declspec(dllexport) int32_t __stdcall FgPrLicenseState(FgPrRuntime rt, FgPrLicense lic);

/// Drop one cached license (closes its CDM key session). A license an attached session still uses is kept.
__declspec(dllexport) void __stdcall FgPrLicenseRelease(FgPrRuntime rt, FgPrLicense lic);

// ── sessions ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// Create a session: the SegmentStore, the CencMediaSource and the feeder. NO engine call — the engine still plays
/// whatever is attached. The store is empty until FgPrSessionPrefetch or FgPrSessionAttach.
__declspec(dllexport) int32_t __stdcall FgPrSessionCreate(FgPrRuntime rt, const FgPrOpenDesc* desc, FgPrSession* out);

/// Fetch the init segments and `segments` media segments around `aroundMs` — video and audio IN PARALLEL, on the
/// shared HttpClient. Returns at once; FgPrEvent_Buffered says when the pair landed. Pass aroundMs < 0 for the
/// descriptor's startPositionMs. This is the prepare half of the managed IPreparableBackend.
__declspec(dllexport) int32_t __stdcall FgPrSessionPrefetch(FgPrRuntime rt, FgPrSession s, int64_t aroundMs,
                                                            int32_t segments);

/// THE SWITCH. Bind `lic` to the session and SetSource its CencMediaSource on the warm engine, detaching whatever was
/// attached first. The source starts at the keyframe ≤ the descriptor's startPositionMs. A license that is still
/// PENDING is attached anyway — the engine's own NeedKey path waits for the key rather than the open waiting for the
/// license. Non-blocking: FgPrEvent_Metadata → CanPlay → FirstFrame → Handle follow.
__declspec(dllexport) int32_t __stdcall FgPrSessionAttach(FgPrRuntime rt, FgPrSession s, FgPrLicense lic);

/// SetSource(null) + Pause. The session, its store and its keyframe table SURVIVE — a "back to the song and out
/// again" toggle is a re-attach, not a re-open.
__declspec(dllexport) int32_t __stdcall FgPrSessionDetach(FgPrRuntime rt, FgPrSession s);

/// Free the session and its store (detaching first when attached). Idempotent.
__declspec(dllexport) void __stdcall FgPrSessionDestroy(FgPrRuntime rt, FgPrSession s);

/// Transport. Applied on the runtime thread IMMEDIATELY as a posted work item — there is no keep-alive tick and no
/// applied-sequence to poll; the ack IS FgPrEvent_Playing / FgPrEvent_Paused.
__declspec(dllexport) int32_t __stdcall FgPrSessionPlay(FgPrRuntime rt, FgPrSession s);
__declspec(dllexport) int32_t __stdcall FgPrSessionPause(FgPrRuntime rt, FgPrSession s);

/// Seek, executed immediately on the runtime thread. `mode` is an FgPrSeekMode. `keyframeMs` is the planner's answer
/// — the keyframe the decoder should start at — or -1 to let the native side search its own table. When the target is
/// already in the store the source is repositioned NOW (flush, not recreate: no decoder is destroyed); otherwise the
/// seek is registered as the feeder's next target, the in-flight GET is CANCELLED (a token, not a 10 ms poll), the
/// video and audio segments at the target are fetched IN PARALLEL, and the reposition runs from the fetch's own
/// completion. Latest-wins: a newer seek supersedes an older one that has not landed.
__declspec(dllexport) int32_t __stdcall FgPrSessionSeek(FgPrRuntime rt, FgPrSession s, int64_t targetMs, int32_t mode,
                                                        int64_t keyframeMs);

__declspec(dllexport) int32_t __stdcall FgPrSessionSetVolume(FgPrRuntime rt, FgPrSession s, double volume);
__declspec(dllexport) int32_t __stdcall FgPrSessionSetRate(FgPrRuntime rt, FgPrSession s, double rate);

/// Size the engine's video stream to what the destination can actually show (the clear path's ContentSizeFor
/// discipline). Applied with IMFMediaEngineEx::UpdateVideoStream on the runtime thread; a 4K stream in a 640 px card
/// then allocates 640 px buffers. Passing 0×0 restores the natural size.
__declspec(dllexport) int32_t __stdcall FgPrSessionSetStreamSize(FgPrRuntime rt, FgPrSession s, int32_t width,
                                                                 int32_t height);

/// Switch the video representation (ABR). The four URL parts describe the new rung; the feeder applies it at the next
/// segment boundary and FgPrEvent_Representation reports the active index.
__declspec(dllexport) int32_t __stdcall FgPrSessionSelectRepresentation(FgPrRuntime rt, FgPrSession s, int32_t index,
                                                                        const wchar_t* initUrl, const wchar_t* base,
                                                                        const wchar_t* prefix, const wchar_t* suffix);

/// One atomic read of the session's state. `out->structSize` must be sizeof(FgPrSnapshot).
__declspec(dllexport) int32_t __stdcall FgPrSessionSnapshot(FgPrRuntime rt, FgPrSession s, FgPrSnapshot* out);

/// The keyframe table: every sync sample the demuxer has SEEN, in ascending presentation ms (segment starts always;
/// intra-segment IDRs as segments are parsed). Fills `out` up to `cap` entries and returns the TOTAL count, so a
/// caller with a smaller buffer knows to re-ask with a bigger one. A negative return is an HRESULT.
__declspec(dllexport) int32_t __stdcall FgPrSessionGetKeyframes(FgPrRuntime rt, FgPrSession s, int64_t* out,
                                                                int32_t cap);

/// The buffered ranges as ascending (startMs, endMs) PAIRS — what a scrub bar draws as its loaded band and what the
/// seek planner checks before it asks for a fetch. `capPairs` counts PAIRS (so `out` holds 2*capPairs int64s);
/// returns the TOTAL number of pairs.
__declspec(dllexport) int32_t __stdcall FgPrSessionGetBuffered(FgPrRuntime rt, FgPrSession s, int64_t* outPairs,
                                                               int32_t capPairs);

/// The protection the video INIT segment carried, for a source whose descriptor had no PSSH/KID (a test vector opened
/// from a bare MPD URL): the PlayReady `pssh` box and the `tenc` default KID. Returns 0 while the init segment has not
/// been parsed yet (wait for FgPrEvent_Buffered after FgPrSessionPrefetch), otherwise the PSSH length (which may exceed
/// `psshCap` — re-ask with a bigger buffer). `kidOut` receives the KID as 32 lower-case hex chars + NUL when
/// `kidCap` ≥ 33. The managed side then acquires the license by KID and attaches.
__declspec(dllexport) int32_t __stdcall FgPrSessionGetInitProtection(FgPrRuntime rt, FgPrSession s, uint8_t* psshOut,
                                                                     int32_t psshCap, wchar_t* kidOut, int32_t kidCap);

// ── test seam ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// Run ParseInit + ParseSegment over a LOCAL fragmented MP4 and report what the demuxer found — the engine's
/// CencDemuxTests gate. No CDM, no D3D device, no network, no runtime: callable on any box with the DLL present.
/// `outKeyframes` (may be null) receives up to `cap` ascending sync-sample times in ms.
__declspec(dllexport) int32_t __stdcall FgPrProbeFile(const wchar_t* path, int64_t* outKeyframes, int32_t cap,
                                                      FgPrProbeResult* out);

#ifdef __cplusplus
}
#endif
