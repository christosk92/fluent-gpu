# PlayReady native integration

Protected video uses an in-process Win32 native DLL plus managed wrappers in the Windows API layer.

## Native DLL

Build and output layout: [`ops/tools/playready-native/README.md`](../../ops/tools/playready-native/README.md).

```cmd
cd ops\tools\playready-native
build.cmd arm64
build.cmd x64
```

Produces `out/{arch}/FluentGpu.PlayReady.Native.dll`. The C ABI is `ops/tools/playready-native/FgPlayReady.h`; every
export is documented there.

## Managed code

| Area | Path |
|---|---|
| The runtime + license cache | `src/FluentGpu.WindowsApi/Media/PlayReady/ProtectedVideoRuntime.cs`, `LicenseCachePolicy.cs` |
| One source | `ProtectedVideoSession.cs` (implements `IProtectedVideoPlayer`) |
| The `IMediaSession` / backend | `ProtectedMediaSession.cs`, `ProtectedMediaBackend.cs` |
| The ABI mirror | `ProtectedVideoNative.cs` |
| Gallery harness | `src/FluentGpu.WindowsApp/` (protected-video test entry points) |

Design context: [`docs/design/subsystems/media-pipeline.md`](../design/subsystems/media-pipeline.md) §8.4.

## The shape: a runtime, licenses, sessions

| Handle | Lifetime | Holds |
|---|---|---|
| `FgPrRuntime` | process; destroyed 30 s after its last session | MF, the D3D11 video device + DXGI manager, ONE `IMFMediaEngine` (windowless swap chain), ONE CDM + PMP host, the MTA runtime thread |
| `FgPrLicense` | per KID; until expired or LRU-evicted (8) | an open TEMPORARY CDM key session |
| `FgPrSession` | per source | a `CencMediaSource` over a byte-capped, time-windowed `SegmentStore` |

A host that wants a fast song→video switch does three things, all non-blocking:

1. At manifest time: `ProtectedMediaBackend.StartLicense(runtime, request)` (or `PrepareAsync`, which also prefetches
   the init + two segments at the start position, video and audio in parallel).
2. At the switch: `MediaPlayer.OpenAsync(source, new MediaOpenOptions { StartPosition = p, StartPaused = true })`. The
   prepared session is found by its init URL; the switch is one `SetSource` on the warm engine.
3. Watch the events, not a timer: `IProtectedVideoPlayer.Phase` / `FirstFrameEpoch` say when the frame at `p` is
   presentable; `IsSeeking` / `LastSeekLandedMs` say when a seek landed.

Every native event and every lifecycle decision goes to `ProtectedVideoRuntime.LogSink` as `[video]` /
`[video.native]` lines (always on; unset, `Console.Error`). There is no separate log file.

## Adaptive protected video

The protected source descriptor may carry a catalog of stable video representation IDs.
`FgPrSessionSelectRepresentation` switches rung; the snapshot reports downloaded bytes, cumulative transfer time,
forward buffer and the active representation, and `FgPrEvent_Representation` reports an applied switch.

`Auto` starts from the manifest's conservative representation, estimates throughput from completed downloads, and
switches only at a media-segment keyframe boundary. Downshifts are immediate; upgrades require buffer headroom and two
consecutive votes. Auto is capped by the rendered viewport height and, on metered connections, the app preference.
Selecting a resolution is a manual pin and deliberately overrides those Auto caps.

Only manifest-declared tracks and compatible representations are published. For Spotify this currently means H.264/MP4
video and AAC/MP4 audio; Opus/WebM profiles are rejected by the protected Media Foundation lane.

### The switch-boundary rule

A representation switch splices the target segment into the timeline **at its own presentation time**, at a segment
boundary that is **at or after the next undelivered sample and never forward of the feeder cursor**. Everything between
the playhead and the splice point stays in the old representation, so the timeline is contiguous and monotonic across a
switch; only the buffer past the splice is replaced. The store is spliced by time on **every** append (not just a
switch), because a seek also moves the feeder's cursor.

## Seeking

| Export | Meaning |
|---|---|
| `FgPrSessionSeek(rt, s, targetMs, mode, keyframeMs)` | `mode` 0 = exact (`SetCurrentTime`), 1 = keyframe (`SetCurrentTimeEx` + `MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE`); `keyframeMs` = the host planner's keyframe, or -1 |
| `FgPrSessionGetKeyframes(rt, s, out, cap)` | every sync sample the demuxer has seen, ascending ms; returns the total |
| `FgPrSessionGetBuffered(rt, s, outPairs, capPairs)` | buffered (start, end) ms pairs; returns the total |

A seek is applied **immediately** on the runtime thread — there is no tick and no applied-sequence to poll; the
acknowledgement is `FgPrEvent_Seeking` then `FgPrEvent_Seeked` (landed ms, ms since posted). It is latest-wins, never
queued: Media Foundation applies queued seeks FIFO, so a dragging thumb would leave the pipeline chasing positions the
user has already left. Use keyframe mode while a scrub thumb is down and exact on the commit.

Load-bearing properties:

* **Buffered targets never download.** The retained window is time-based (30 s behind, 60 s ahead, byte-capped), so
  a near seek repositions the source from the store at once.
* **A far seek is one parallel fetch.** The video and audio segments at the target are fetched concurrently; an
  in-flight fetch is cancelled with a token, and the reposition runs from the fetch's completion.
* **Flush, not recreate.** `CencMediaSource::Start` repositions each stream to the keyframe at or before the target with
  a discontinuity; no decoder is destroyed. A seek issued while paused is followed by one `FrameStep(TRUE)` (at rate 0
  the MF video renderer does not pre-roll).

## Download telemetry (media-segment-only)

`bytesDownloaded` / `downloadElapsedMs` accumulate **media segments only, body transfer only**:

* init segments are excluded — they are small and RTT-dominated, and counting them depressed the throughput estimate at
  exactly the moment the ABR needed it (dash.js excludes them for the same reason);
* connect, TLS and time-to-first-byte are excluded — the response-body read alone is measured;
* one `winrt::HttpClient` is shared by the whole runtime, so connections and TLS sessions are reused.

## Tests

`src/FluentGpu.Windows.Tests`: the license-cache policy, the session state machine over a fake player, the demuxer over
local fixtures (`FgPrProbeFile` — no CDM, no GPU, no network), and the runtime against the real DLL where the box has
Media Foundation.
