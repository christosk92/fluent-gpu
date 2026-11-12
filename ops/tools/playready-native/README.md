# FluentGpu.PlayReady.Native

In-process Win32 native DLL for PlayReady-protected video via Media Foundation CDM/CENC. This is **not** a UWP sidecar or cross-process helper — the managed engine loads `FluentGpu.PlayReady.Native.dll` directly.

The ABI (`FgPlayReady.h`) is a **process-lifetime runtime with handles**, not a one-shot blocking open. `FgPrRuntimeCreateOnAdapter` brings Media Foundation, the D3D11 video device + DXGI manager, ONE CDM with its PMP host and ONE `IMFMediaEngine` (windowless swap-chain mode) up once, on an event-driven MTA runtime thread. `FgPrLicenseAcquire` opens one TEMPORARY CDM key session per KID and keeps it (8-entry LRU, never evicting a KID an attached session uses, raising `FgPrEvent_LicenseEvicted` when it does; a key that goes dead is failed and evicted, a Pending license older than 8 s is replaced - `LicensePolicy.h`); the challenge goes up to managed through a non-blocking relay and the license comes back through `FgPrLicenseDeliver`. `FgPrSessionCreate` makes one session per source: a `CencMediaSource` over a byte-capped, time-windowed `SegmentStore`, fed by a demand-driven feeder that fetches video and audio segments in parallel. `FgPrSessionAttach` is the switch — one `SetSource` on the warm engine, starting at the carried position. Every handle is opaque; a stale one is `E_HANDLE`. Every native diagnostic line is an `FgPrEvent` on the managed callback (there is no native log file), and `FgPrProbeFile` runs the demuxer over a local fragmented MP4 with no CDM, GPU or network. The engine sets no `MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT` (a frame-server attribute): in windowless mode the Simple Video Renderer presents the decoder's NV12 textures itself and picks YUV vs RGB from its own overlay feedback.

## Build

From this directory, with Visual Studio C++ tools installed:

```cmd
build.cmd arm64
build.cmd x64
```

Output: `out/{arch}/FluentGpu.PlayReady.Native.dll` (e.g. `out/arm64/FluentGpu.PlayReady.Native.dll`). The sources are
UTF-8 and built with `/utf-8`. Run it from a shell whose `vcvarsall` target is the architecture you name — on an ARM64
box an x64-emulated shell still builds either one, because the script calls `vcvarsall` with the argument.

`FluentGpu.WindowsApp` builds the DLL for its own architecture whenever any source here is newer than the output (never
in an IDE's design-time build).
Both pick the architecture from `PlayReadyNative.props`: the explicit `RuntimeIdentifier`, else the running SDK's RID —
the architecture the managed process actually runs as, not `PROCESSOR_ARCHITECTURE`.

## Managed code

The C# integration lives in `src/FluentGpu.WindowsApi/Media/PlayReady/`. See [`docs/guide/playready-native.md`](../../../docs/guide/playready-native.md) for the end-to-end guide.

## Sources

| File | Role |
|---|---|
| `FgPlayReady.h` | The C ABI: runtime / license / session handles, `FgPrOpenDesc`, `FgPrSnapshot`, `FgPrEvent`, the probe seam |
| `PrInternal.h` | Shared internals: the runtime/license/session objects, the handle registry, the runtime work queue, event dispatch, the shared WinRT `HttpClient` + the cancellable two-phase GET |
| `PrRuntime.cpp` | `FgPrRuntime*`: MF + D3D11 + DXGI manager, `CreateAndPrepareCdm` + the desktop PMP host bridge, the protection manager, the media engine, its notify sink (engine events → `FgPrEvent`, OPMINFO → `FgPrEvent_OutputProtection`) and `cenc://fluentgpu/<session>` scheme handler, the runtime thread, `FgPrRuntimeQueryHdcp` (Media Foundation's `hdcp=` feature query under the negotiated key system; it runs the HDCP handshake, so the app asks it only once protected video is engaged) |
| `PrLicense.cpp` | `FgPrLicense*`: the KID table, `GenerateRequest`, the KeyMessage envelope parse + non-blocking relay, `Update`, key status → usable / expired |
| `PrSession.cpp` | `FgPrSession*` + `FgPrProbeFile`: sessions, attach/detach, transport + seek as posted work items, the demand-driven parallel feeder, representation switches, snapshots, keyframes, buffered ranges |
| `HandoverPolicy.h` | The session hand-over's pure parts: the armed (250 ms grace) unload of the engine's source that a successor's attach cancels, and the reaper that joins stopped feeder threads off the runtime thread |
| `FrameHealthPolicy.h` | Rendered-frame health and swap-chain handle ownership, pure (F066, F198): `FrameCounters` accumulates the engine's `FRAMES_RENDERED` / `FRAMES_DROPPED` (polled every 500 ms while a source plays) across MF's post-flush counter resets into `FgPrSnapshot.framesRendered/framesDropped`; `RenderedFrameWatch` is Chromium's no-rendered-frame hang check (10 s of playing, or since the last `UpdateVideoStream`, raises `FgPrEvent_Error` with `ERROR_TIMEOUT`); `RetiredHandles` holds the swap-chain handles the runtime owns once they are superseded (every `GetVideoSwapchainHandle` returns a fresh one) and closes each exactly once, 2 s after it was retired |
| `WorkQueue.h` | The runtime thread's queue, in two lanes: engine/transport items (attach, detach, seek, engine events) drain ahead of licence items (`StartAcquisition`, `ApplyLicense`, `CloseLicense`, PMP round trips), with a starvation bound (`kMaintenanceEveryN` engine items, then one licence item); the attaching or attached session's own licence is exempt (started in the attach verb, result applied on the engine lane), only other rows' licence work and closes yield |
| `EventRing.h` | The native→managed event queue: every `FgPrEvent` and log line is copied into it by the raising thread and delivered by ONE notifier thread, in order, in batches; no native thread (runtime, MF, CDM, feeder) ever enters managed code or waits for it. `Stop` flushes, joins (bounded) and rejects later pushes |
| `OpmWindow.h` | The hidden layered "virtual video window" handed to the engine as `MF_MEDIA_ENGINE_OPM_HWND` (F264): created on its own message-pump thread before the engine, moved over the video's screen rect by `FgPrSessionPlaceOpmWindow` (`SWP_ASYNCWINDOWPOS`, never waits on a native thread) and kept on the host window when it moves with no video pump (a `WINEVENT_OUTOFCONTEXT` location hook on its own thread), destroyed at teardown. `OpmScreenRect` is its pure geometry. `FG_PLAYREADY_NO_OPM_WINDOW=1` leaves it out |
| `SegmentStore.h` | The per-session store: time-window retention against a byte budget, the pooled 64 KiB-granular segment buffers, the keyframe table, buffered ranges, `CanSeekTo` |
| `CencMediaSource.h` | The fMP4/CENC demuxer and the custom `IMFMediaSource` emitting encrypted CENC samples |
| `build.cmd` | MSVC build of the three translation units into one DLL, per architecture |
| `PlayReadyNative.props` | The one architecture rule the projects that build or load the DLL import |
