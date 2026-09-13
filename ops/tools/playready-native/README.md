# FluentGpu.PlayReady.Native

In-process Win32 native DLL for PlayReady-protected video via Media Foundation CDM/CENC. This is **not** a UWP sidecar or cross-process helper — the managed engine loads `FluentGpu.PlayReady.Native.dll` directly.

The ABI (`FgPlayReady.h`) is a **process-lifetime runtime with handles**, not a one-shot blocking open. `FgPrRuntimeCreate` brings Media Foundation, the D3D11 video device + DXGI manager, ONE CDM with its PMP host and ONE `IMFMediaEngine` (windowless swap-chain mode) up once, on an event-driven MTA runtime thread. `FgPrLicenseAcquire` opens one TEMPORARY CDM key session per KID and keeps it (8-entry LRU, never evicting a KID an attached session uses); the challenge goes up to managed through a non-blocking relay and the license comes back through `FgPrLicenseDeliver`. `FgPrSessionCreate` makes one session per source: a `CencMediaSource` over a byte-capped, time-windowed `SegmentStore`, fed by a demand-driven feeder that fetches video and audio segments in parallel. `FgPrSessionAttach` is the switch — one `SetSource` on the warm engine, starting at the carried position. Every handle is opaque; a stale one is `E_HANDLE`. Every native diagnostic line is an `FgPrEvent` on the managed callback (there is no native log file), and `FgPrProbeFile` runs the demuxer over a local fragmented MP4 with no CDM, GPU or network.

## Build

From this directory, with Visual Studio C++ tools installed:

```cmd
build.cmd arm64
build.cmd x64
```

Output: `out/{arch}/FluentGpu.PlayReady.Native.dll` (e.g. `out/arm64/FluentGpu.PlayReady.Native.dll`).

## Managed code

The C# integration lives in `src/FluentGpu.WindowsApi/Media/PlayReady/`. See [`docs/guide/playready-native.md`](../../../docs/guide/playready-native.md) for the end-to-end guide.

## Sources

| File | Role |
|---|---|
| `FgPlayReady.h` | The C ABI: runtime / license / session handles, `FgPrOpenDesc`, `FgPrSnapshot`, `FgPrEvent`, the probe seam |
| `PrInternal.h` | Shared internals: the runtime/license/session objects, the handle registry, the runtime work queue, event dispatch, the shared WinRT `HttpClient` + the cancellable two-phase GET |
| `PrRuntime.cpp` | `FgPrRuntime*`: MF + D3D11 + DXGI manager, `CreateAndPrepareCdm` + the desktop PMP host bridge, the protection manager, the media engine, its notify sink (engine events → `FgPrEvent`) and `cenc://fluentgpu/<session>` scheme handler, the runtime thread |
| `PrLicense.cpp` | `FgPrLicense*`: the KID table, `GenerateRequest`, the KeyMessage envelope parse + non-blocking relay, `Update`, key status → usable / expired |
| `PrSession.cpp` | `FgPrSession*` + `FgPrProbeFile`: sessions, attach/detach, transport + seek as posted work items, the demand-driven parallel feeder, representation switches, snapshots, keyframes, buffered ranges |
| `SegmentStore.h` | The per-session store: time-window retention against a byte budget, the pooled 64 KiB-granular segment buffers, the keyframe table, buffered ranges, `CanSeekTo` |
| `CencMediaSource.h` | The fMP4/CENC demuxer and the custom `IMFMediaSource` emitting encrypted CENC samples |
| `build.cmd` | MSVC build of the three translation units into one DLL, per architecture |
