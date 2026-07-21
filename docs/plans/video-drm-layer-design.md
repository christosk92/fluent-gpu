# Video DRM layer — design (sits on the Phase-1 compositing spine)

> **SUPERSEDING FINDING (2026-07-19 — IN-PROCESS CDM; the whole UWP-AppContainer-sidecar architecture below is
> SUPERSEDED, and native PlayReady is PRODUCTIONIZED into the unified media API as M5).** Native, **in-process**
> PlayReady protected video now renders end-to-end in the normal FluentGpu desktop process — **no UWP sidecar,
> no separate process, no MSIX install, no cross-process handle duplication.** The custom CENC `IMFMediaSource`
> → modern MF-CDM decryptor → decode → non-zero protected windowless-swapchain handle →
> `IVideoPresenter.BindSurfaceHandle` pipeline runs entirely inside the full-trust engine process (build
> `desktop-cdm-20260719-persist-v11`, Surface Laptop 7 ARM64; `tools/playready-uwp-helper/{Helper.cpp,
> CencMediaSource.h}` compiled by `build-desktop-dll.cmd` into `FluentGpu.PlayReady.Native.dll`). This removes
> the entire process/app-model boundary the pass-1..pass-3 blocks below wrestled with: the gate was **not** the
> app model (InboxOnly/UWP), **not** signing, and **not** the sandbox — it was three stacked content/wiring
> root causes. This design's `IProtectedVideoEngine` seam + `PlayReadyVideoEngine` (WinRT `MediaProtectionManager`)
> and the UWP AppContainer sidecar (`ProtectedVideoSidecar`/`SidecarPackage`/`FileSidecarChannel`/the Module
> system) are all superseded and DELETED; the in-process `DesktopProtectedVideoPlayer` is folded into the
> unified spec's `MfMediaPlayer` DRM code path.
>
> **The three stacked root causes, all of which had to fall (fix order v6→v11; full detail in memory
> `desktop-playready-solved.md`):**
>
> 1. **Wrong license server for the content (the deep one).** Axinom's `..._singlekey` vector is encrypted
>    with AXINOM's key, but the code licensed from `test.playready.microsoft.com`, which derives keys from the
>    PlayReady TEST KEY SEED. A wrong-key license still reports **USABLE** — it fails only *downstream*
>    (`MF_MEDIA_ENGINE_ERR_DECODE`/`MF_E_INVALIDREQUEST` on sample #0 for the sample-attribute path;
>    `DRM_E_CH_BAD_KEY 0x8004110E` before metadata for a protected topology). Offline proof method (reusable):
>    extract the WRMHEADER from the pssh, derive the test-seed key (SHA-256 A/B/C xor-fold), compute the
>    checksum = first 8 B of `AES-ECB(key, KID-guid-order)` and compare to `<CHECKSUM>`. Fix: POST to the
>    content's real server (`https://drm-playready-licensing.axprod.net/AcquireLicense` + the `X-AxDRM-Message`
>    JWT) — and, in the productionized build, the POST is the managed `WithDrm` relay, not a native hardcode.
> 2. **Protected-stream advertisement, the Firefox way — `MFWrapMediaType(clear, MFMediaType_Protected)` +
>    `MF_SD_PROTECTED=1`** on the stream descriptor's type (gecko `MFMediaEngineVideoStream::CreateMediaType` +
>    `GenerateStreamDescriptor`). This **refines** the earlier "never mark protected" finding: the failed
>    experiment was the raw `MF_MT_PROTECTED` *attribute* on an *unwrapped* type (→ legacy ITA/OTA topology →
>    `MF_E_TOPOLOGY_VERIFICATION_FAILED 0xC00D715B`). The *wrapped* type is the modern EME mechanism — without
>    it the engine wires encrypted samples straight into the decoder. Also matched Firefox: drop
>    `MF_MEDIA_ENGINE_USE_PMP_FOR_ALL_CONTENT` + `UseSoftwareProtectionLayer`, protection manager only via
>    post-create `SetContentProtectionManager` (A/B env vars `FG_CENC_NO_PROTECTED_WRAP` /
>    `FG_CENC_FORCE_SW_LAYER` / `FG_CENC_LEGACY_ENGINE_WIRING`).
> 3. **Persistent-license EME session + keyframe SPS/PPS prepend.** Axinom's token allows persistence, so the
>    server returns a PERSISTABLE license; the EME spec (which MFCdm implements faithfully) makes `Update()` on
>    a TEMPORARY session reject it with `MF_TYPE_ERR 0x80704005` even though the license is valid. Fix:
>    `CreateSession(PERSISTENT_LICENSE)` + declare both types in `MF_EME_SESSIONTYPES` (`FG_CENC_TEMP_SESSION=1`
>    restores temporary). Also ported from gecko `AnnexB.cpp`: after size-preserving AVCC→AnnexB, PREPEND
>    Annex-B SPS/PPS to every keyframe and grow `subsamples[0].clearBytes` by the prepended length.
>
> **Authoritative working reference:** the cloned Firefox tree `C:\WAVEE\gecko-dev\dom\media\platforms\wmf\*`
> (`MFMediaEngineStream`/`VideoStream`, `MFMediaSource`, `MFCDMProxy`, `MFContentProtectionManager`) — the
> source for every attribute/call above; `DRM_E_*` codes live in
> `Windows Kits\10\Include\*\winrt\Windows.Media.Protection.PlayReadyErrors.h`.
>
> **Productionized (M5 of the unified media API, `docs/plans/media-playback-api-spec.md` §9.2).** The
> in-process CDM is the DRM code path of the spec's `MfMediaPlayer`; the surface binds at the unchanged
> `IVideoPresenter.BindSurfaceHandle`; license acquisition is the managed `WithDrm`
> `Func<LicenseRequest, ValueTask<LicenseResponse>>` relay (native raises the challenge via `FgPlayReadyRunEx` +
> `DrmLicenseBridge` + a `[UnmanagedCallersOnly]` thunk → managed POST → native `Update()`), so the hardcoded
> Axinom URL/JWT/URL-rewrite are gone from `Helper.cpp`. Canon owner for the seam is
> `design/subsystems/media-pipeline.md §8` (DRM subsection). Everything below (the WinRT-topology recipe, the
> sidecar spike, the pass-1..pass-3 blocks) is retained only as the historical record of the disproven routes.

> **EMPIRICAL FINDINGS (2026-07-19, pass 3 — the custom CENC `IMFMediaSource`; PROTECTED VIDEO NOW RENDERS END TO END).**
> The last mile flagged by pass 2 (a custom encrypted-sample media source, Microsoft's `MediaEngineEMEUWPSample`
> `CdmMediaSource` model) is **built and PROVEN** on the Surface Laptop 7 (ARM64), mode `protected-custom` of
> `tools/playready-uwp-helper/` (`CencMediaSource.h` + Helper.cpp). An **in-app fragmented-MP4/CENC demuxer** (from
> scratch — parses `moov`/`trak`/`mdia`(`mdhd`)/`minf`/`stbl`/`stsd`/`encv`/`avcC`+`sinf`(`frma`/`schm`/`schi`/`tenc`)
> for codec/SPS-PPS/`default_KID`/scheme/IV-size + `moov` `pssh`; and per segment `moof`/`traf`/`tfhd`/`tfdt`/`trun`
> + `senc` + `mdat`) feeds a **custom `IMFMediaSource`/`IMFMediaStream`** that emits encrypted `IMFSample`s carrying the
> CENC attributes (`MFSampleExtension_Encryption_ProtectionScheme`, `..._Content_KeyID`, `..._Encryption_SampleID` (IV),
> `..._Encryption_SubSampleMappingSplit`) to the modern-CDM-backed `IMFMediaEngine` (source handed in via
> `MF_MEDIA_ENGINE_EXTENSION`; `SetContentProtectionManager` S_OK; license driven to **USABLE** from the init `pssh`).
> Verified on the Axinom singlekey vector (`default_KID = 4060a865-8878-4267-9cbf-91ae5bae1e72`, 576 samples): the
> engine reaches **LOADEDMETADATA/CANPLAY**, pulls all encrypted samples (`RequestSample` #0..#500), and
> **`GetVideoSwapchainHandle` returns a NON-ZERO handle**; the full-trust engine `DuplicateHandle`s it and composites it
> z-below the UI — the protected region captures **BLACK** (output protection working), framed by the rendered UI
> (`.tmp/uwp-protected-custom-printwindow.png`). **THE decisive fix:** the media type / stream descriptor must **NOT**
> be marked protected (`MF_MT_PROTECTED`/`MF_SD_PROTECTED`) — doing so forces the **legacy PMP ITA/OTA
> protected-topology** whose trust verification rejects the unsigned in-app source, failing `engine->Play()` with
> **`MF_E_TOPOLOGY_VERIFICATION_FAILED` (0xC00D715B)** *before any sample is pulled* (the same 0xC00D715B the WinRT
> `MediaPlayer` path hit in pass 1). Leaving the stream **clear-typed** and letting the **sample-level CENC attributes**
> drive the CDM decryptor insertion (the EME model) is what flips topology-verification failure into a decrypting,
> decoding, non-zero-handle producer — so pass-1's "0xC00D715B is a hard desktop wall" is **narrowed**: it is the wall
> for the *legacy protected-descriptor topology*, not for the EME/sample-attribute path. `SetPMPHostApp` S_FALSE,
> `UseSoftwareProtectionLayer`, creation-time protection manager, and `USE_PMP_FOR_ALL_CONTENT` were all tried and did
> NOT clear 0xC00D715B; only removing the descriptor markings did. **Net: native PlayReady protected video renders end
> to end (helper Playing + USABLE key + non-zero handle + engine binds & composites BLACK) — the milestone is complete.**

> **EMPIRICAL FINDINGS (2026-07-19 — the cross-process UWP-helper integration; supersede the pass-2 "not sufficient"
> conclusion for the UWP context).** The `SetPMPHostApp`/`GenerateRequest` walls that pass-1/pass-2 hit in
> **full-trust / LPAC / packaged** are **CLEARED in a GENUINE-UWP CoreApplication AppContainer** — and the
> cross-process surface pipe the milestone needs is proven end to end. Built + run on the Surface Laptop 7 (ARM64):
> a UWP AppContainer producer helper (`tools/playready-uwp-helper/`, C++/WinRT, same toolchain as
> `tools/playready-uwp-test/`) + a full-trust engine consumer (`FluentGpu.WindowsApp --uwp-video`).
>
> 1. **Cross-process DirectComposition surface-handle sharing WORKS (visually proven).** The helper creates a
>    shareable handle (`DCompositionCreateSurfaceHandle(COMPOSITIONOBJECT_ALL_ACCESS)` for a producer swap chain;
>    or `IMFMediaEngineEx::GetVideoSwapchainHandle` for a windowless-swapchain decode), publishes {PID, handle} in a
>    coord file; the full-trust engine `OpenProcess(PROCESS_DUP_HANDLE)`+`DuplicateHandle`s it FROM the helper and
>    feeds it straight to the spine's **unchanged `IVideoPresenter.BindSurfaceHandle`** →
>    `CreateSurfaceFromHandle`→`SetContent`, composited z-below the UI through the hole-punch. Screenshots
>    (`PrintWindow(PW_RENDERFULLCONTENT)`) show the exact producer pixels: a magenta/cyan test pattern, and a real
>    decoded Big Buck Bunny frame — **from a separate process, and from a genuine AppContainer (InboxOnly) helper
>    into the full-trust engine** (the lower-priv→full-trust duplication direction the DRM case needs).
> 2. **Native MF-CDM PlayReady reaches a USABLE key in genuine-UWP — the pass-2 wall is gone.** In the AppContainer
>    (`AppPolicyGetMediaFoundationCodecLoading = InboxOnly`): `IMFContentDecryptionModule` created (store path
>    accepted) → `GetService(IMFPMPHostApp)` **S_OK** (returns `MF_E_UNSUPPORTED_SERVICE` in full-trust) →
>    **`SetPMPHostApp` S_FALSE (success, NOT the full-trust `E_FAIL 0x80004005`)** → `CreateSession` S_OK →
>    **`GenerateRequest` S_OK (NOT the full-trust `DRM_E_LOGICERR 0x8004C3E8`)** → KeyMessage → license POST to the
>    MS public server **HTTP 200** → `Update()` S_OK → **key status = `MF_MEDIAKEY_STATUS_USABLE`.** This is the
>    first end-to-end native PlayReady key acquisition on this machine; pass-2's "necessary but not sufficient"
>    verdict holds for full-trust/LPAC but is **superseded for the true-UWP app model** — no PMP/PRND code-signing
>    cert was needed, only the InboxOnly app-model context. The WinRT `MediaProtectionManager`+`MediaPlayer` path
>    also reaches licensed (individualization + license 200 + MediaOpened) in the same helper.
> 3. **Surface source for the protected path.** WinRT `MediaPlayer` exposes **no** public raw-DComp-handle interop
>    (`IMediaPlayerSurfaceHandleProvider` is not in the 26100 SDK) — so the protected surface must come from
>    **`IMFMediaEngine`+CDM**, which (per finding 2) is now viable in UWP and yields a shareable windowless-swapchain
>    handle exactly like the clear path. The remaining, fully-scoped/de-risked step to a composited protected
>    surface (renders black = output protection working) is the media-engine EME wiring:
>    `IMFMediaEngineClassFactoryEx::CreateMediaKeys` + `MF_MEDIA_ENGINE_NEEDKEY_CALLBACK` + `IMFMediaKeySession`
>    (reusing the proven license exchange) + `IMFMediaEngineEME::SetMediaKeys` → `GetVideoSwapchainHandle` → the same
>    `BindSurfaceHandle`. Every prerequisite (key USABLE, cross-process/cross-privilege pipe) is proven.
>
> **Net:** the milestone's make-or-break — a protected-capable UWP helper producing a cross-process-shareable DComp
> surface the full-trust engine composites through `BindSurfaceHandle` — is proven for the pipe (with real video)
> and for native key acquisition; only the protected media-engine surface producer remains, and it is now an
> integration task, not an open research question.

> **EMPIRICAL FINDINGS (2026-07-19, pass 2 — the protected media-engine wiring; corrects finding 3 above).** The
> `protected` mode of `tools/playready-uwp-helper/` now implements the full `IMFMediaEngine`+EME path (built + run,
> Surface Laptop 7 ARM64). **Every COM step of the protected wiring SUCCEEDS at runtime** and the key is `USABLE`:
> modern `IMFContentDecryptionModule` created + `SetPMPHostApp` S_FALSE; a `MediaEngineProtectionManager`
> (`IMFContentProtectionManager` + WinRT `IMediaProtectionManager`) exposing the CDM's `IMediaProtectionPMPServer` via
> `Properties()["Windows.Media.Protection.MediaProtectionPMPServer"]`; `IMFMediaEngineProtectedContent::SetContentProtectionManager`
> **S_OK**; `SetSource` **S_OK**. **Two corrections to finding 3:** (a) the predicted legacy route
> `IMFMediaEngineClassFactoryEx::CreateMediaKeys` + `IMFMediaEngineEME::SetMediaKeys` returns
> **`MF_NOT_SUPPORTED_ERR` (0x80700009)** for `com.microsoft.playready[.recommendation]` — the in-proc EME software
> CDM is gone; the working wiring is the *modern* `IMFContentDecryptionModule` + `MF_MEDIA_ENGINE_CONTENT_PROTECTION_FLAGS`
> + `SetContentProtectionManager` (Microsoft's `MediaEngineEMEUWPSample` `MediaEngineProtectionManager`, ported
> verbatim). (b) **The producer does NOT yet reach a non-zero handle** — protected playback via a **URL or app byte
> stream never completes**: the MS test `.ismv` assets are served `application/octet-stream` so a URL `SetSource`
> fails resolution with **`MF_E_UNSUPPORTED_BYTESTREAM_TYPE` (0xC00D36C4)**; a content-typed HTTP byte stream
> (`MF_BYTESTREAM_CONTENT_TYPE=video/mp4` via `SetSourceFromByteStream`) AND URL-resolvable HLS (`.m3u8`, whose KID
> matches the proven-USABLE key) / Smooth (`.ism/manifest`) manifests all **hard-wedge inside `engine->Play()`** — no
> `LOADEDMETADATA`, no `NeedKey`, no `BeginEnableContent`; the PMP protected pipeline never returns and the wedge is so
> deep that neither an in-process timeout thread nor a watchdog `TerminateProcess` can abort it (external kill needed;
> `deploy-run.ps1` force-kills on its coord timeout). The **CLEAR control** — the same `IMFMediaEngine`→windowless
> swapchain→`GetVideoSwapchainHandle`→cross-process `DuplicateHandle`→`BindSurfaceHandle` path with no CDM — composites
> a real decoded frame (`.tmp/uwp-clear-printwindow.png`), proving the media-engine→handle→composite pipe is sound.
> **Root cause / last mile:** `MediaEngineEMEUWPSample` never uses a URL/byte-stream for protected content — it feeds a
> **custom MSE source** (`CdmMediaSource`) that demuxes in-app and routes encrypted samples to the CDM. Direct-URL
> protected playback through `IMFMediaEngine`+PMP is not viable here; the remaining step is that MSE/custom-source
> producer (a separate, larger effort — the same "DASH/MSE gap" flagged all along), OR running the engine on a
> message-pumped ASTA rather than the bare MTA worker (untested hypothesis for the `Play()` wedge).

> **EMPIRICAL FINDINGS (2026-07-18 — supersede §3/§5's central claim).** A buildable spike
> (`tools/playready-spike/`, interop in `src/FluentGpu.WindowsApi/Media/PlayReady/`) tested the WinRT
> `MediaProtectionManager` + `MediaPlayer` + `AdaptiveMediaSource` path against the Axinom PlayReady vector on a
> Surface Laptop 7 (ARM64, Win11). Result: **§5's thesis is WRONG.** "Own the `MediaPlayer`/`MediaProtectionManager`
> wiring directly, attach-before-Source, no `MediaPlayerElement`" does **not** clear `0xC00D715B` — it fails
> protected-topology build with `MF_E_TOPOLOGY_VERIFICATION_FAILED` and `ServiceRequested` NEVER fires, reproduced
> **both unpackaged and under packaged MSIX identity, and even after a successful proactive individualization.**
> This matches the still-open Microsoft bugs microsoft-ui-xaml#10873 / WindowsAppSDK#3059 — the WinRT protected
> path is broken on desktop, full stop. What IS proven on this machine: clear DASH plays headless (the WinRT stack
> works for non-DRM); **PlayReady HW DRM is supported**; WinRT individualization succeeds (hr=0) once a data-store
> path exists; and unpackaged individualization fails with `0x8004B8CF = MSPR_E_HWDRM_SUPPORTED_BUT_NO_PATHS`
> (no PlayReady data-store path). **Corrected direction:** native PlayReady must go **MF-direct** —
> `IMFMediaEngine` + `IMFContentDecryptionModule` (EME) with an **explicit store path** — which bypasses the broken
> WinRT topology wiring, supplies the store path unpackaged, and is the browser-proven path (it also yields the
> raw-DComp swapchain handle the spine's `BindSurfaceHandle` wants). Being built/verified in
> `tools/playready-mf-spike/`; this doc's §3/§5 recipe is retained only as the record of the disproven WinRT route.

> **EMPIRICAL FINDINGS (2026-07-18, pass 2 — the LPAC-hosted-CDM experiment).** `tools/playready-mf-spike/` was
> extended into a broker/child architecture that hosts the OS PlayReady CDM inside a **dedicated Less-Privileged
> AppContainer (LPAC)** child process, reproducing Firefox/Chromium's exact recipe: an AppContainer profile
> (`wavee.sb.cdm`), the full Chromium `kMediaFoundationCdm` capability set (`lpacCom`, `lpacMedia`,
> `lpacMediaFoundationCdmData`, `mediaFoundationCdmFiles`, `registryRead`, … + `internetClient`/
> `privateNetworkClientServer` derived by NAME → `S-1-15-3-1`/`S-1-15-3-3`), file ACLs (`SetEntriesInAcl` +
> `SetNamedSecurityInfo`) granting the AppContainer SID R+X on the self-contained publish dir and the AppContainer +
> `lpacMediaFoundationCdmData` SIDs full control on the CDM store + results dirs, and a `STARTUPINFOEX` proc-thread
> attribute list carrying **`PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`** + **`ALL_APPLICATION_PACKAGES_OPT_OUT`**
> (the true-LPAC bit). **All of that works** — the child verifiably runs in an LPAC (`TokenIsAppContainer` = YES),
> loads all self-contained runtime DLLs, reaches the network, and creates the OS PlayReady CDM with an explicit
> `MF_CONTENTDECRYPTIONMODULE_STOREPATH` (store-path blocker `0x8004B8CF` stays cleared). The exact fixed
> `SetPMPHostApp` pattern is in place: `cdm→QI(IMFGetService)→GetService(MF_CONTENTDECRYPTIONMODULE_SERVICE,
> IID_IMFPMPHost)` (**S_OK**; `IID_IMFPMPHostApp` returns `0xC00D36BA MF_E_UNSUPPORTED_SERVICE`, expected for
> non-UWP) → wrap in the Firefox `MFPMPHostWrapper`-equivalent shim → `cdm→SetPMPHostApp(shim)`.
> **RESULT: `SetPMPHostApp` still returns `E_FAIL` (0x80004005), so `GenerateRequest` still returns
> `0x8004C3E8 DRM_E_LOGICERR` — no KeyMessage, no license, no key.** Decisive detail: the shim's
> `QueryInterface`/`LockProcess`/`ActivateClassById` are **never invoked** (grep-confirmed) — the CDM rejects
> *internally* before it ever touches our host app. The failure is **identical** (a) inside the LPAC and (b) in a
> fully-privileged non-sandboxed process (`--inproc`), and is unchanged by removing `CoInitializeSecurity`, skipping
> the process-wide D3D11/DXGI manager, or by an (untrusted self-signed) Authenticode signature. So the wall is **not**
> the sandbox/capabilities/ACLs/attributes (a full-privilege process hits it too) and **not** our shim — it is a
> **CDM-internal protected-environment eligibility check inside `SetPMPHostApp` that our process cannot satisfy**.
> The remaining differentiator vs. every host that succeeds (Edge, the UWP `MediaEngineEMEUWPSample`, and
> Firefox/Chromium's signed CDM utility processes) is **process code-signing with a Microsoft-trusted PMP/PRND
> certificate (and/or a registered package identity)** — which a from-scratch third-party unpackaged exe cannot mint.
> Reproduce: `pwsh tools/playready-mf-spike/run-lpac.ps1` (publishes self-contained win-arm64 + runs the broker).
> **Honest status: native licensed PlayReady key acquisition is NOT yet achieved unpackaged; the LPAC hypothesis was
> necessary but not sufficient.** Next probes worth a spike before concluding it's impossible for third-parties:
> (1) obtain/trust a real PMP-eligible code-signing cert and re-test; (2) MSIX package identity + the
> `mediaFoundationCdm` capability in a manifest (a middle path between "unpackaged" and "UWP"); (3) capture a
> PlayReady/Media-Foundation ETW trace of `SetPMPHostApp` to read the concrete internal sub-status behind the E_FAIL.

**Status:** design, not implemented. **Builds on:** `docs/plans/video-compositing-spine-design.md`
(the DRM-free spine). **Owner doc for canon:** `design/subsystems/media-pipeline.md §8` gains a DRM
subsection when this lands. **Scope:** the component that *produces* a DirectComposition surface handle from a
DRM-protected Spotify video stream and feeds it into the spine's single DRM attach point —
`IVideoPresenter.BindSurfaceHandle(id, dcompSurfaceHandle)`. Nothing in the renderer, the present tree, the
hole-punch, the crossfade, or the registry changes.

Read alongside: the spine doc (§4 `IVideoPresenter`, §8 the "same seam" statement, §9 the #10918 note),
`src/FluentGpu.WindowsApi/Media/SystemMediaControls.cs` (the **proven** hand-bound-WinRT / no-CsWinRT
precedent this design copies), and `design/subsystems/com-interop.md` (generated/confined COM rules).

---

## 1. What this is — the licensed path, not circumvention

This designs **standard, licensed playback**: driving the OS's own DRM components (PlayReady via
`Windows.Media.Protection.MediaProtectionManager`; Widevine via the browser-provisioned CDM in WebView2) against
**Spotify's official license servers**, authenticated with the **user's own session** (Bearer + client-token).
It is exactly the capability Microsoft issue **microsoft-ui-xaml#10918** asks Microsoft to ship natively in the
Windows App SDK. There is **no key extraction, no de-obfuscation, no protection bypass** anywhere in this design
— the OS/CDM holds the keys and decrypts; our engine never sees a decrypted pixel or a content key. The audio
PlayPlay key-derivation machinery in the Wavee workspace is **out of scope** and unrelated (audio-only,
proprietary); this document is video-only and touches none of it.

The security posture is inherited from the spine's core bet — *"the engine touches no video pixels"*: protection
is enforced entirely **below the DComp surface handle** by Media Foundation / DWM / the GPU. A protected surface
is byte-for-byte the same primitive to our compositor as the spine's M0 test surface. That is *why* adding DRM
requires zero renderer change.

## 2. Where DRM plugs in — one seam, and the engine/app split

```
 App (Spotify-specific, the Wavee-on-FluentGpu port — ported ~verbatim from WaveeMusic)
   │  v9 JSON manifest ─▶ DASH MPD synthesis ─▶ PSSH/PRO + tenc/default_KID parse
   │  license POST to spclient …/playready-license  (Bearer + client-token)
   ▼
 Engine seam:  IProtectedVideoEngine.OpenAsync(ProtectedVideoRequest, IVideoPresenter)   ← Spotify-agnostic
   │  request carries { DashMpd string, AcquireLicenseAsync delegate, segment HttpHandler, initial size }
   ▼
 Windows impl (FluentGpu.WindowsApi/Media): PlayReadyVideoEngine
   │  AdaptiveMediaSource(MPD) ─▶ MediaSource ─▶ MediaPlayer + MediaProtectionManager(PlayReady)
   │  ServiceRequested ─▶ (SOAP challenge) ─▶ request.AcquireLicenseAsync(...) ─▶ ProcessResponse
   │  IMediaPlayerSurfaceHandleProvider.GetSurfaceHandle() ─▶ nuint dcompSurfaceHandle
   ▼
 Spine seam:  IVideoPresenter.BindSurfaceHandle(id, dcompSurfaceHandle)   ← UNCHANGED from Phase 1
```

Two boundaries make this clean and keep the engine reusable/Spotify-free:

- **The `AcquireLicenseAsync` delegate** injects the Spotify-specific license HTTP call into the generic engine.
  The engine hands the OS-generated SOAP/binary challenge to the delegate and gets bytes back; it never knows
  the endpoint, the auth, or that "Spotify" exists. This is the direct analogue of how
  `SystemMediaControls.ButtonDispatcher` injects host behavior into a generic OS-services component.
- **Manifest→MPD synthesis, PSSH/PRO extraction, tenc/`default_KID` parsing, and key-id formatting stay
  app-side**, ported near-verbatim from the proven, already-written WaveeMusic files
  (`SpotifyVideoManifest.cs`, `Mp4InitSegmentProtectionParser.cs`). They are pure C# (`System.Text.Json`,
  span parsing) with **no WinRT** — portable and unit-testable headlessly. The engine takes the finished MPD
  string; it does not parse Spotify's proprietary manifest.

### 2.1 The engine seam (portable)

**File:** `src/FluentGpu.Engine/Media/IProtectedVideoEngine.cs`

```csharp
namespace FluentGpu.Media;

// Spotify-agnostic. The app builds the MPD + supplies the license transport; the engine drives the OS DRM
// pipeline and binds the resulting protected surface into the spine's IVideoPresenter.
public sealed class ProtectedVideoRequest
{
    public required string DashMpd;                         // synthesized DASH MPD with PlayReady ContentProtection
    public required Uri     MpdBaseUri;                     // for AdaptiveMediaSource segment resolution
    public required double  StartPositionMs;
    public required (uint W, uint H) InitialSurfaceSize;

    // OS ⇒ app ⇒ OS. Given the DRM system's challenge (+ any SOAP headers), return the license bytes.
    // The app POSTs to Spotify's /playready-license (Bearer + client-token). Engine stays Spotify-free.
    public required Func<byte[] /*challenge*/, IReadOnlyDictionary<string,string> /*soapHeaders*/,
                         CancellationToken, Task<byte[]>> AcquireLicenseAsync;

    // Optional: custom transport for CDN segment GETs (auth headers / UA spoof), used by AdaptiveMediaSource.
    public System.Net.Http.HttpMessageHandler? SegmentHttpHandler;
}

public interface IProtectedVideoEngine : IAsyncDisposable
{
    // Opens the protected source, acquires the license via the request delegate, and binds the resulting
    // DComp surface handle into the presenter. Returns the surface id the spine drives (Place/SetVisible).
    Task<VideoSurfaceId> OpenAsync(ProtectedVideoRequest request, IVideoPresenter presenter,
                                   CancellationToken ct);

    void  Play();  void Pause();  void SeekTo(double positionMs);
    event Action FirstFrameDecoded;     // app flips VideoReady 0→1 (the spine's graded reveal, §6 spine)
    event Action<string> Failed;        // surfaces MediaFailed (code + hresult) for recovery/telemetry
}
```

The Windows implementation (`PlayReadyVideoEngine`, §3) lives in `FluentGpu.WindowsApi/Media/` beside
`SystemMediaControls` — the pillar that already owns hand-bound WinRT media interop. It references `Engine`
only (per the pillar's existing deps), takes `IVideoPresenter` across the seam, and hands it a `nuint` handle.

---

## 3. Primary engine — native PlayReady

The pipeline, revived from WaveeMusic's fully-written (currently dead-coded behind an early `return;`)
PlayReady path — every step below has a proven reference implementation.

### 3.1 Manifest → MPD (app-side, ported)

Spotify's v9 JSON manifest (`…/manifests/v9/json/sources/{manifestId}/options/supports_drm`, CORS-fenced —
requires the `Origin: https://xpui.app.spotify.com` / `Referer` spoof) is parsed into a DASH MPD by the ported
`SpotifyVideoManifest.BuildDashMpd(...)`. Reference behaviors to preserve verbatim:

- Select **MP4** profiles whose `key_system == "playready"`; extract `encryption_data` (base64 PSSH) and derive
  the PlayReady Object (PRO) from the PSSH box (`ExtractProFromPssh`).
- Prefer parsing the **init segment's** `tenc`/`pssh` boxes (`Mp4InitSegmentProtectionParser.Parse`, fetched via
  the app's authenticated segment GET) for the authoritative `default_KID` / IV size; fall back to manifest DRM
  data. This is what makes the MPD's `<ContentProtection>` correct.
- Emit `<ContentProtection>` twice: `urn:mpeg:dash:mp4protection:2011` (`cenc:default_KID`) **and** the PlayReady
  UUID `urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95` with `<mspr:pro>` / `<mspr:kid>` / `<cenc:pssh>`.
- Key-id byte order matters: `FormatCencKeyId` (hyphenated GUID) vs `FormatPlayReadyKeyId` (little-endian
  byte-swap of the first 8 bytes, then base64) — copy both exactly; a wrong swap = no license.
- Start conservative on adaptive sets (Wavee's `SelectDashVideoProfiles` picks a single ≤480p representation —
  `AdaptiveMediaSource` is stricter than MSE about mixed AVC levels). Widen later behind a gate.

**This is app-side and out of this repo's engine scope**, but it is the load-bearing input; unit tests for it are
specified in §11.

### 3.2 MPD → MediaPlayer + MediaProtectionManager (engine, the new work)

`PlayReadyVideoEngine.OpenAsync` performs, in order (the ordering defeats `0xC00D715B` — see §5):

1. `AdaptiveMediaSource.CreateFromStreamAsync(mpdStream, baseUri, "application/dash+xml", httpClient)` — the OS
   does DASH/ABR/segment-fetching for us (the single biggest reason to use WinRT MediaPlayer over raw MF, §7).
   The `httpClient` wraps `request.SegmentHttpHandler` when supplied (auth/UA for CDN segments).
2. `var mediaSource = MediaSource.CreateFromAdaptiveMediaSource(ams);`  →
   `var item = new MediaPlaybackItem(mediaSource);`  (or set `MediaSource` directly).
3. **Build and attach the protection manager BEFORE the source is set:**

   ```csharp
   var mpm = BuildProtectionManager();          // PlayReadyWinRTTrustedInput, UseSoftwareProtectionLayer, ids
   mpm.ServiceRequested += OnServiceRequested;  // license + individualization (§3.3)
   var player = new MediaPlayer { ProtectionManager = mpm };   // ← attach FIRST
   player.SetSurfaceSize(new Size(w, h));        // tell MF the render-target size (no XAML element)
   player.Source = item;                         // ← source LAST; topology now builds with the trust chain present
   ```

   `BuildProtectionManager` mirrors Wavee's proven setup exactly:
   - `Properties["Windows.Media.Protection.MediaProtectionSystemIdMapping"]` = { PlayReady system-id →
     `"Windows.Media.Protection.PlayReady.PlayReadyWinRTTrustedInput"` }.
   - `MediaProtectionSystemId` = PlayReady system id; `MediaProtectionContainerGuid` = `{9A04F079-…}`.
   - `UseSoftwareProtectionLayer = true` (software DRM / SL2000 path — works without a HW TEE; §9).
   - `PlayReadyStatics.CheckSupportedHardware(HardwareDRM)` probed for telemetry (may throw — swallow).
   - Hook `ComponentLoadFailed` → `Completion.Complete(false)` + telemetry.
4. On `MediaOpened` (topology built, first frame imminent): pull the DComp surface handle (§4) and
   `presenter.BindSurfaceHandle(id, handle)`; raise `FirstFrameDecoded` so the app opens the graded hole.
5. `MediaFailed` → parse `error.ExtendedErrorCode` (HRESULT) + `error.Error` (the `MediaPlaybackError` enum,
   e.g. `SourceNotSupported`) → raise `Failed` for recovery / fallback-to-Widevine (§8).

### 3.3 The license flow (`OnServiceRequested`) — proven, ported

Fires on an MF worker thread (like SMTC's `ButtonPressed`; §10). Two request types:

- **`PlayReadyLicenseAcquisitionServiceRequest`:**
  `var soap = req.GenerateManualEnablingChallenge();` → `byte[] challenge = soap.GetMessageBody();` + extract
  `soap.MessageHeaders` → `byte[] license = await request.AcquireLicenseAsync(challenge, headers, ct);` (the app
  POSTs to `/playready-license`) → `Exception? e = req.ProcessManualEnablingResponse(license);` →
  `args.Completion.Complete(e is null);`.
- **`PlayReadyIndividualizationServiceRequest`:** `await req.BeginServiceRequest();` (PlayReady calls the MS
  individualization server itself); on `MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED`
  (`0x8004B895`) chase `req.NextServiceRequest().BeginServiceRequest()`. Complete accordingly.

The `AcquireLicenseAsync` delegate is the **only** Spotify-aware line, and it lives in the app. Everything above
is generic PlayReady.

---

## 4. Getting the surface handle out of MediaPlayer (no XAML, no WinAppSDK)

The spine consumes a raw DComp surface **handle** (`nuint`), which it wraps via
`ICompositorInterop::CreateCompositionSurfaceForHandle` (spine §4). Two ways to obtain one from a WinRT
`MediaPlayer`, in order of preference:

1. **`IMediaPlayerSurfaceHandleProvider` (preferred).** The COM interop interface on `MediaPlayer` exposing
   `GetSurfaceHandle()` → an `HANDLE` created by MF via `DCompositionCreateSurfaceHandle`. This is the exact
   primitive WinUI's `MediaPlayerPresenter` uses internally (its private-UDK `GetVideoSwapchainHandle` variant),
   one level **below** the broken `MediaPlayerElement`. Feed the handle straight to `BindSurfaceHandle`; drive
   sizing with `MediaPlayer.SetSurfaceSize` + `PlaybackSession.NormalizedSourceRect`.
   **Uncertain — verify (§12):** the exact interop IID/name and whether `TerraFX.Interop.WinRT` projects it; if
   not, hand-declare it as a small `[GeneratedComInterface]` (cold-COM, sanctioned) or a manual vtable struct.
2. **`MediaPlayer.GetSurface(Compositor)` (fallback).** Returns a `MediaPlayerSurface` whose `CompositionSurface`
   is placed on a `SpriteVisual`. This needs a `Windows.UI.Composition.Compositor` and the `ICompositorInterop`
   bridge to our DComp visual — more WinRT surface, and it assumes a WinUI-Composition object we otherwise don't
   need. Use only if path 1's handle interop proves unavailable.

Either way the handle/surface is bound **once** (on `MediaOpened`); per-frame `Place`/`SetVisible` stay the
spine's job. Path 1 keeps us on the exact "surface handle" contract the spine was designed around.

---

## 5. The #10918 / `0xC00D715B` fix, precisely

The user's real error — `MediaFailed code="SourceNotSupported" hresult=0xC00D715B` — is **not** a codec/container
problem. `0xC00D715B` is `MF_E_TOPOLOGY_VERIFICATION_FAILED`: the **protected-media topology's Input-Trust-
Authority (ITA) verification failed**. In the Windows App SDK, `MediaPlayerElement` + `MediaProtectionManager`
regressed because the UWP-era protected-pipeline registration/activation was never ported to the packaged/
unpackaged WinAppSDK host — so the trusted pipeline can't assemble and topology verification fails.

We fix it structurally by **owning the pipeline** and never touching the WinAppSDK media/element path:

1. **Attach a fully-configured `MediaProtectionManager` to the `MediaPlayer` BEFORE `Source` is set** (§3.2 step
   3). If the trust chain (system-id mapping → `PlayReadyWinRTTrustedInput`, container GUID, software-layer flag)
   is present *when the topology is built*, ITA verification has what it needs and `0xC00D715B` does not occur.
   Setting `Source` first, or attaching an under-configured manager, is the classic repro.
2. **No `MediaPlayerElement`, no WinAppSDK NuGet, no CsWinRT, no packaged-identity assumption.** We drive the raw
   WinRT `MediaPlayer` and take its DComp surface **handle** (§4) — the same primitive WinUI uses beneath the
   broken element. The framework layer that regressed is simply not in our path.
3. **We control individualization + license timing** via `ServiceRequested` (§3.3), so provisioning completes
   before the license request, rather than depending on the element's implicit flow.

This is the "do it properly" the issue asks for: native PlayReady through `MediaPlayer`, wired the way UWP did,
in a from-scratch host. Worth contributing the working recipe back to #10918.

---

## 6. AOT / no-CsWinRT interop plan

The engine forbids CsWinRT and WindowsAppSDK. **Precedent that this is feasible:**
`src/FluentGpu.WindowsApi/Media/SystemMediaControls.cs` already hand-binds WinRT through
`TerraFX.Interop.WinRT` vtable structs with **zero CsWinRT, zero `ComWrappers` on the call-out path**, using:
`RoGetActivationFactory` / `RoActivateInstance` for activation, `HStringHandle` for strings, direct vtable calls
for properties/methods, and — critically — a **`StrategyBasedComWrappers` CCW for the one event callback**
(`add_ButtonPressed` takes an `ITypedEventHandler` realized from a managed object). The PlayReady design reuses
every one of these patterns.

**WinRT types to hand-project (the honest cost):**

| Type / interface | Use | Binding |
|---|---|---|
| `MediaPlayer` (`IMediaPlayer`, `…3/4/…`) | player, `SetSurfaceSize`, `Source`, events | activate + vtable calls (like `ISystemMediaTransportControls`) |
| `AdaptiveMediaSource` + `…Statics` | `CreateFromStreamAsync` (async → `IAsyncOperation`) | statics activation + **`IAsyncOperation` completion handler** (a CCW like `ButtonPressed`) |
| `MediaSource` / `MediaPlaybackItem` | wrap AMS into a source | statics activation |
| `MediaProtectionManager` (`IMediaProtectionManager`) + `PropertySet` | protection config, `ServiceRequested`/`ComponentLoadFailed` | activate + **event CCW** for `ServiceRequested` |
| `PlayReadyLicenseAcquisitionServiceRequest`, `PlayReadyIndividualizationServiceRequest`, `PlayReadySoapMessage`, `PlayReadyStatics` | the license/individualization flow | vtable calls; `GetMessageBody`/`ProcessManualEnablingResponse` take `byte[]` (IBuffer/array marshaling) |
| `IMediaPlayerSurfaceHandleProvider` (or `GetSurface(Compositor)`) | the surface handle | interop QI on `MediaPlayer`; **verify projection exists** |
| `IAsyncOperation<T>` / `IAsyncAction` | AMS create, individualization | a reusable `await`-able CCW+adapter (write once) |

**Effort assessment (honest):** this is materially larger than SMTC — a dozen-plus interfaces including two
event callbacks and an `IAsyncOperation` await-adapter — but every piece has a concrete template in
`SystemMediaControls.cs`, and the biggest subsystem (adaptive streaming) is done *by the OS*. **The load-bearing
unknown is whether `TerraFX.Interop.WinRT` already projects `Windows.Media.Playback`,
`…Streaming.Adaptive`, `…Protection`, and `…Protection.PlayReady`.** TerraFX is generated from Windows metadata,
so missing namespaces can be added by regenerating/extending, or the handful of needed interfaces hand-declared
as cold `[GeneratedComInterface]`s (com-interop.md's sanctioned path for cold COM). **A one-day spike to confirm
projection coverage must precede committing to this engine** (§12).

### 6.1 WinRT `MediaPlayer` vs. Media Foundation direct — recommendation

**Recommendation: WinRT `MediaPlayer` + `MediaProtectionManager` + `AdaptiveMediaSource`, hand-bound via the
SMTC pattern.** Justification:

- **`AdaptiveMediaSource` gives us DASH parsing, ABR, and segment fetching for free.** The MF-direct path
  (`IMFMediaEngine` + `IMFContentDecryptionModule`/`IMFContentProtectionManager`) is *pure COM* (a better AOT fit
  on paper, no WinRT projection risk) but forces us to **reimplement an adaptive-streaming stack** — the single
  largest chunk of work in any player. Not worth it to avoid WinRT interop we already have a proven pattern for.
- **The Spotify PlayReady wiring is already written and proven** against this exact WinRT surface in WaveeMusic;
  reviving it is far less risk than authoring an MF CDM integration from scratch.
- **MF-direct's one genuine win** (no WinRT projection dependency) is mitigated: worst case we hand-declare a
  bounded set of cold COM interfaces, which is strictly less work than an ABR engine.

Keep MF-direct on the shelf as the escape hatch **iff** the §12 spike finds WinRT projection intractable *and*
we're willing to build ABR — a large, separate effort, not Phase-1.

---

## 7. Fallback engine — Widevine via WebView2 EME (secondary)

Some Spotify video may be served **only** as Widevine/WebM. Note the corrected reason WaveeMusic's *live* path is
WebView2 EME with its PlayReady engine dead-coded: per the app author, they fell back to the HTML/WebView player
**because native PlayReady didn't work in their WinUI 3 / WinAppSDK stack** (the #10918 / `0xC00D715B`
topology-verification failure) — **not** because PlayReady content was unavailable. This is the escape hatch #10918
names, and it is exactly the failure this design routes around by owning the `MediaProtectionManager` wiring
directly (§3). See §12 risk 1. For genuinely Widevine-only content we still port WaveeMusic's proven live path:
an in-process **WebView2** hosting an HTML page that runs EME with `keySystem = 'com.widevine.alpha'`
(`initDataTypes: ['webm','cenc']`, `sessionTypes: ['temporary']` → L3 software), feeding WebM segments into MSE
`SourceBuffer`s, and marshaling each license challenge out to a native callback that POSTs to
`/widevine-license/v1/video/license`.

**Integration into the spine:** use WebView2 **visual hosting** —
`CoreWebView2Environment.CreateCoreWebView2CompositionControllerAsync(hwnd)` gives a
`CoreWebView2CompositionController` whose output is a composition **Visual** we parent under the spine's video
child visual (set `RootVisualTarget`). No airspace/HWND-child hack; the WebView2 output becomes the child visual
the hole reveals. (If handle parity with `BindSurfaceHandle` is awkward, add a sibling
`IVideoPresenter.BindChildVisual(id, IntPtr dcompVisual)` — same z-order/hole/Place contract, different content
source. Prefer reusing the existing seam if the controller can hand back a surface handle.)

**Why it must exist, and why it's secondary:**

- **Unavoidable:** there is **no embeddable, self-provisioned native Widevine CDM.** Google licenses Widevine
  CDMs to browser/device makers with a per-device keybox; a third-party native app cannot ship or provision one.
  On Windows the Widevine CDM exists **only inside Chromium/Edge/WebView2.** So "support Widevine" *necessarily*
  means "host a Chromium." This is a hard external constraint, not a design choice.
- **Costly (the user's own complaint):** WebView2 spins a full Chromium (multiple processes, hundreds of MB of
  working set) — heavy for a from-scratch, near-zero-alloc engine. Hence **PlayReady-native is primary; WebView2/
  Widevine is the fallback**, instantiated lazily only for Widevine-only content and torn down when idle.

**Engine selection (per track):** parse the manifest → if an `mp4`/`playready` profile **and** a
`/playready-license` endpoint are present, use `PlayReadyVideoEngine` (native, cheap). Otherwise fall back to the
WebView2/Widevine engine. Both implement `IProtectedVideoEngine`; the app picks one and the spine is oblivious.

---

## 8. Security & output protection

- **Where decryption happens:** entirely below the surface handle, in MF's protected pipeline (SL2000 software
  path with `UseSoftwareProtectionLayer=true`; SL3000 hardware path when a TEE + HW DRM are present and negotiated
  — `CheckSupportedHardware`). Higher resolutions/robustness may require SL3000; software caps apply. The engine
  never holds a key or a decrypted frame.
- **Hole-punch is identical for protected content.** A protected DComp surface composites exactly like an
  unprotected one; the spine's premultiplied-0 hole and z-order need **no change** (confirms spine §8's central
  claim). DWM enforces output protection (HDCP negotiation, capture exclusion) beneath the handle.
- **Capture/screenshot interaction (testing consequence):** DWM **excludes protected surfaces from screen
  capture** — they read back **black** in screenshots and screen recordings (including our own `--screenshot`
  path). This is expected DRM behavior, not a bug, but it means our golden-image gate **cannot** validate live
  protected pixels; it can only validate the poster/chrome around the hole (§11). Note it so nobody "fixes" a
  black protected region.

## 9. Threading & lifecycle

- **MediaPlayer owns its own MF worker threads** — decode, ABR, and rendering run off our UI/render threads by
  construction (the spine's "heavy continuous work is off our thread" bet holds for DRM too).
- **`ServiceRequested`/`ComponentLoadFailed`/`MediaOpened`/`MediaFailed`/`IAsyncOperation` completions fire on
  arbitrary MF/OS threads** (exactly like SMTC's `ButtonPressed`). License acquisition inside `ServiceRequested`
  is `await`ed network I/O — off the hot path; the `Completion.Complete(bool)` call returns control to MF.
  Marshal any state these touch, or hop to the UI thread, following the `ButtonDispatcher` precedent.
- **Surface handoff is one-time** (on `MediaOpened`): `BindSurfaceHandle` on the render thread (it's a
  render-thread-confined presenter call), queued into the frame's single phase-11 `Commit` like every other
  presenter mutation.
- **Teardown / registry switch:** when `VideoSurfaceRegistry` hands the live slot to another owner or the video
  closes, `await engine.DisposeAsync()` — detach events, `player.Source = null`, `player.Dispose()` (stops MF
  threads), dispose AMS/MPM/MPD stream (mirror Wavee's `CleanupPlayer` ordering), then `presenter.Destroy(id)`.
  The lower-priority owner falls back to poster/art (spine §7) with no black frame.

## 10. Canon, gates, and testing honesty

**Docs to reconcile (owner in parens):**
- `design/subsystems/media-pipeline.md §8` (**owner**) — add a **DRM subsection**: the `IProtectedVideoEngine`
  seam, the PlayReady-primary / Widevine-fallback split, the "protected surface == same hole-punch" statement,
  and the #10918/`0xC00D715B` note. Reconcile with the spine doc's `BindSurfaceHandle` replacement of
  `GetMediaPlayerSink`.
- `design/SPEC-INDEX.md §2` + `design/subsystems/README.md` ownership map — register `IProtectedVideoEngine`
  (owner: media-pipeline.md) and the Windows `PlayReadyVideoEngine` component. Run `check-canon.ps1` (exit 0).
- `design/subsystems/com-interop.md` — note the new cold-COM WinRT surface (MediaPlayer/PlayReady) and that it
  follows the `SystemMediaControls` call-out pattern (no hot-path `ComWrappers`; CCW only for event/async
  callbacks).

**What CAN be gated (headless, deterministic):**
- **Manifest→MPD synthesis** unit tests: golden MPD for a known v9 JSON input; assert both `<ContentProtection>`
  blocks, `cenc:default_KID`, `mspr:pro`/`mspr:kid`, segment timeline. Mirror WaveeMusic's
  `SpotifyWebEmeVideoManifestTests` and add the PlayReady-MPD equivalent.
- **PSSH/PRO extraction + `tenc`/`default_KID` parsing** unit tests over captured init-segment bytes.
- **Key-id formatting** (`FormatCencKeyId` vs `FormatPlayReadyKeyId` byte-swap) exact-vector tests.
- **Engine-selection** logic (playready-profile-present → native; else Widevine) as a pure function.

**What CANNOT be gated by the zero-alloc VerticalSlice/headless seams (be honest):**
- Actual protected playback needs a **real Spotify session, real license servers, and a GPU/DWM** — it is a
  **manual/integration** check, not a CI golden. Protected pixels also capture black (§8), so even a GPU
  screenshot can't diff the live frame. The zero-alloc gate applies to the **spine's** per-frame work (already
  covered in Phase 1); the DRM engine's allocations are cold (open/license/teardown), off the phase-6–13 hot
  path, and not frame-rate-critical — do **not** claim a zero-alloc gate over the media engine.
- Verification checklist (manual): PlayReady content plays with no `0xC00D715B`; license acquired once per track;
  seek/pause/teardown clean; Widevine-only content falls back to WebView2 and plays; memory returns to baseline
  after WebView2 teardown; HDCP/SL behavior on a HW-DRM machine vs a software-only VM.

## 11. Milestones / build order

1. **Spike (gating): TerraFX WinRT projection coverage** for `Windows.Media.Playback` / `…Streaming.Adaptive` /
   `…Protection` / `…Protection.PlayReady`, and the surface-handle interop (`IMediaPlayerSurfaceHandleProvider`).
   Decide: use existing projections, extend TerraFX, or hand-declare cold interfaces. **Blocks §6's recommendation.**
2. **App-side manifest→MPD + parsers ported** from WaveeMusic, with the unit tests (§10) green. No engine needed
   yet — pure C#.
3. **`IProtectedVideoEngine` seam + `PlayReadyVideoEngine`** (WinRT interop): AMS→MediaSource→MediaPlayer,
   `BuildProtectionManager`, `OnServiceRequested`, attach-before-Source, `MediaFailed` surfacing. Drive it with a
   **known-good PlayReady DASH test stream first** (not Spotify) to isolate interop from Spotify specifics.
4. **Surface handle → `BindSurfaceHandle`** — close the loop into the Phase-1 spine; first protected frame
   composites through the hole (captures black, but plays on screen). Manual GPU check.
5. **Real Spotify PlayReady content** end-to-end (Bearer + client-token license POST). Confirms no `0xC00D715B`.
6. **Widevine/WebView2 fallback** + engine selection + lazy instantiate/teardown + memory-return check.

PlayReady-first throughout; Widevine fallback last. Single-thread-correct (the spine's render-thread flip is a
later, gate-guarded step and unaffected here).

## 12. Open questions / risks

1. **Does our direct wiring actually clear `0xC00D715B`? (the real gating risk).** The "why did Wavee dead-code
   PlayReady?" question is now **answered**: per the app author, native PlayReady didn't work in their WinUI 3 /
   WinAppSDK stack (the #10918 topology-verification failure), so they escape-hatched to WebView2/Widevine — it was
   **a platform bug, not a content-availability problem**. That *raises* confidence PlayReady content exists (their
   dead code parses it because Spotify serves it) and shifts the whole bet onto one question: does owning the
   `MediaPlayer` + `MediaProtectionManager` wiring directly — attach-before-`Source`, PlayReady ITA + individualization
   configured, no `MediaPlayerElement`/WinAppSDK in the path (§3) — actually build a valid protected topology where
   theirs failed? **This is the spike that gates the effort:** stand up the minimal native protected-play path against
   one real PlayReady track and confirm it clears `0xC00D715B` and renders. If it does, native PlayReady is the cheap
   primary and WebView2's memory cost is avoided; if it doesn't, WebView2/Widevine becomes the common path (same seam,
   roles swap). Still worth a cheap secondary check: **measure the fraction of real tracks that resolve an
   mp4/playready profile**, since Widevine-only content forces the fallback regardless.
2. **TerraFX WinRT projection coverage** (§6/§11 spike) — the feasibility crux for the no-CsWinRT approach.
3. **`IMediaPlayerSurfaceHandleProvider` exact IID/availability** (§4) — the surface-handle interop must be
   confirmed; fallback is `GetSurface(Compositor)` with more WinRT surface.
4. **`IAsyncOperation` await-adapter** — one reusable CCW+continuation shim is needed for `CreateFromStreamAsync`
   and individualization; not hard, but new vs. SMTC (which had no async).
5. **CDN segment auth** — whether Spotify's video-CF segments need custom headers/UA through
   `AdaptiveMediaSource`'s `HttpClient`, or carry tokens in-URL. Determines whether `SegmentHttpHandler` is
   load-bearing.
6. **RoInitialize apartment** — MediaPlayer/MF threading vs. the UI STA; follow SMTC's tolerant
   `RoInitialize(SINGLETHREADED)` + `RPC_E_CHANGED_MODE` handling; confirm MediaPlayer is happy under it.
7. **HW-DRM (SL3000) resolution caps** on software-only machines — expected, but document the ceiling so a
   low-res software playback isn't mistaken for a bug.
```
