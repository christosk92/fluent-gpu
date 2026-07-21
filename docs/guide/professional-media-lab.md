# Professional media lab

The gallery route `media-lab` is the manual, on-device verification surface for FluentGpu media. It uses public,
reproducible fixtures and shows the expected result beside live player diagnostics. A fixture is evidence for one or
more behaviors; it is not a screenshot demo.

## What the lab covers

| Behavior | Fixture family |
|---|---|
| Play, pause, resume, seek, rate, volume, resize | Progressive H.264 MP4 and Microsoft's clear 1080p60 MP4 |
| Early Play during Opening, animated loading/buffering transitions | `Opening · early Play intent + motion` |
| Responsive two-row controls, auto-hide/reveal and ellipsis overflow | `Player chrome · compact + overflow` |
| Borderless monitor fullscreen, F11/Escape and exact window restore | `Player chrome · true fullscreen` |
| Fit, crop, fill, native and custom display aspect | Progressive MP4 and DASH-IF rotating-logo ladder |
| Initial buffering, rebuffering, seeking and bounded network failure | DASH VOD/live plus the stable 404 scenario |
| DASH templates/timelines, padded numbers and independently-sized audio/video segments | Akamai BBB and DASH-IF `mixeddur` |
| HLS masters, renditions, byte ranges, discontinuities and low-latency parts | Apple's Bip Bop and advanced streams; parser unit fixtures |
| ABR catalog, auto/manual quality, AVC3 and HDR signaling | Akamai/DASH-IF ladders and Apple's HDR stream |
| Live DVR, moving window, live offset, low latency and Go Live | DASH-IF livesim2 |
| Alternate audio, text tracks, CEA-608, WebVTT/SRT and engine-rendered sidecars | DASH-IF and Apple caption/rendition fixtures plus parser tests |
| Audio-only adaptive playback and no-video composition | DASH-IF `audio.mpd` |
| PlayReady CENC/CBCS, protected composition, native transport acknowledgements | Axinom and Microsoft's official PlayReady fixtures |
| Typed source/network/codec/DRM failure | Negative and compatibility scenarios |

The source catalog is intentionally data-driven (`MediaTestCatalog` in `MediaLabPage.cs`). Every scenario records an
exact source, feature flags, an expected result and the publisher's reference page. The player panel exposes state,
buffering reason and progress, live offset, geometry, HDR, dropped frames, track counts, quality count, throughput,
rebuffer count and typed failures.

The default player chrome is an overlay, not a permanent row that steals video height. The seek rail keeps the full
width; the lower command row retains play/mute/time and collapses advanced commands into the `…` menu below 760 DIP.
That menu always provides aspect policy (Fit with black bars, Crop, Stretch, Native, 16:9, 4:3, 21:9 and 2.39:1),
speed, quality, audio, captions, chapters and fullscreen where the player reports those capabilities. While playback
advances the chrome fades after the configured idle delay (2.5 seconds by default); pointer movement, touch, focus and
keyboard input reveal it again. The idle-hide is a declarative `UseTimeout` restarted on activity — while an open
picker/menu **pins** the chrome (`IOverlayService.IsAnchorPinned`), the timeout does not collapse it out from under the
open flyout; closing the flyout re-arms the timer.

## Architecture notes (the G5g rebuild)

The `MediaPlayerElement` is built on the overhauled engine seams and is the flagship proof control:

- **Pure Render, engine-driven pump.** `Render` has no side effects. The per-frame video pump (viewport +
  `IMediaPlayer.PumpVideo`) is a callback registered once at mount and invoked by the engine each frame
  (`VideoSurfaceRegistry.PumpAll`); it reads the live laid-out area, so the video tracks layout without the control
  re-rendering. There is **no** whole-player `FrameClock.Tick` subscription — the playhead drives compositor binds
  (the seek fill/thumb) and a per-second quantized time label, so position advance re-renders neither the player nor
  the transport.
- **First-class fullscreen surface hand-off.** The fullscreen presentation shares the inline composited surface via an
  explicit single-writer ownership transfer on the registry (`TransferOwnership`); a non-owner pump is a no-op, so the
  two views never fight over the shared slot.
- **Controlled aspect + tokens.** Aspect/fullscreen are concrete signals (auto-materialized when a caller passes none),
  and every on-media ink/scrim/stage color reads a `Tok.*` media token — no hardcoded colors.
- **Windowed in-band captions.** Manifest-declared WebVTT is fetched **around the playhead** — a few segments behind
  (scrub-back headroom) and ahead (prefetch), advancing with playback — instead of prefetching the whole track upfront;
  it stays best-effort and off-thread, and TTML remains typed-unsupported. Sidecar WebVTT/SRT is unchanged.

## Source authorities

- [DASH-IF livesim2 assets](https://livesim2.dashif.org/assets) — live, low-delay, alternate audio, subtitles,
  CEA-608, trick-mode, mixed segment durations, AVC3 and rotating-video fixtures.
- [Apple HLS examples](https://developer.apple.com/streaming/examples/) — HLS masters with AVC/HEVC/AV1,
  Dolby Vision/HDR10, alternate audio and WebVTT.
- [Microsoft PlayReady test content](https://learn.microsoft.com/playready/advanced/testcontent/playready-test-content)
  — official CENC/CBCS, multi-key, codec and format coverage.
- [Akamai Big Buck Bunny DASH ladder](https://dash.akamaized.net/akamai/bbb_30fps/) — stable DASH VOD ladder.

These are third-party network fixtures. Availability and codec support can change. Parser, scheduler, subtitle and
state-machine behavior is therefore also covered by deterministic unit tests; the gallery is the real network/CDM/GPU
gate, not the only gate.

## Manual pass

1. Open **Professional Media Lab**, select a scenario, read its expected result, then choose **Run scenario**.
2. Exercise transport and the `…` menu. Verify Fit/black-bars, Crop, Stretch, Native and cinema presets. For adaptive
   sources, cycle audio, captions and quality; for live sources, seek behind the edge and press **Go live**.
3. Run the compact case and verify auto-hide/reveal. Run fullscreen and leave with Escape, F11 and the restore glyph.
   For the early-Play case, press Play before metadata resolves: Pause must appear immediately and playback must begin
   without a second click once the first frame is ready.
4. Compare the diagnostics with the scenario: a deliberate incompatibility must produce a typed error, never silent
   black or an infinite spinner.
5. Protected video may be black in screen capture because output protection is working. Judge it from the on-screen
   frame, advancing clock, non-zero protected handle and PlayReady log.
