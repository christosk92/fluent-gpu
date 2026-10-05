# ops/e2e - automated end-to-end video test

`video-e2e.ps1` runs the gallery exe (`src/FluentGpu.WindowsApp`) with `--video-e2e`: a timed, thresholded, end-to-end test of
the video path on the real host (D3D12 + DirectComposition + Media Foundation). It is the measurement plan from
`docs/design/subsystems/video-engine-design.html` made executable. It opens real windows on your desktop (the gallery main
window plus a detached pop-out, the same `AppHost.OpenDetachedWindow` path Wavee's video pop-out uses) and closes them again.
It needs a live composited desktop and a GPU, so it is a local gate, not a CI job. The D3D12 debug layer is left off: it would
distort the timings.

```powershell
powershell -File ops\e2e\video-e2e.ps1              # build Release once, run (~90 s), print the table + verdict
powershell -File ops\e2e\video-e2e.ps1 -NoBuild     # reuse the existing Release exe
powershell -File ops\e2e\video-e2e.ps1 -Seconds 30 -Switches 20 -Cycles 40
```

Each run writes a timestamped folder under `ops/e2e/out/` (git-ignored): `video-e2e.md` (the table), `video-e2e.json` (the same
metrics plus raw per-sample series), `video-e2e.log` / `stderr.txt` (engine lines such as `[detached] reveal`,
`[video] stream.size`, `[render.pace]`) and `bear-b.mp4` (a copy of the clip, so a source switch is never a same-URL no-op).
The clip is `src/FluentGpu.Windows.Tests/Fixtures/video/bear-1280x720-av_frag.mp4`, looped.

**Exit code:** `0` every thresholded metric PASS, `2` at least one FAIL, `1` the run could not complete (a scenario timed out,
threw, or a metric could not be measured). The report is always written.

## What it measures

The main window runs the hidden `video-e2e` gallery page: a 100k-row virtualized list scrolled every frame through the same
command port a wheel drives. Present intervals come from the engine's `PresentLedger` (the render thread's fresh-present QPC
stamps, main window and detached child separately), compared with the display's vsync period.

| Scenario | What happens | Metrics (threshold) |
|---|---|---|
| S1 baseline | main window only, list scrolling, 10 s | present-interval p50/p95/p99/max, count of intervals > 1.5x and > 2x vsync, refresh rate (baseline for S2) |
| S2 pop-out with video | open a detached window hosting a `MediaPlayerElement` over a playing `MediaPlayer`; then `--seconds` (15) of list scrolling with the video playing | open -> first child present (< 150 ms); open -> first video bound and first frame visible (< 700 ms); main p99 interval <= S1 p99 + 1 vsync; 1000 ms latency/slot timeouts == 0; worst UI-thread loop gap < 50 ms; dropped frames < 1% (MF `GetStatistics`) |
| S3 UI actions | with the pop-out playing: `--cycles` (20) rounds of popup open, popup close, main-window resize | UI-thread block per action, max and p95 (< 50 ms, 5 ms stretch target reported); worst main present gap after each action (info); slot timeouts == 0 |
| S4 switching | `--switches` (10) source switches on the playing player, alternating two URLs and start positions | `OpenAsync` -> first frame visible, p95 (< 700 ms); dropped-frame percentage over the run (< 1%) |
| S5 placement churn | pop-out closed; the video element in the main window is animated in size and position for 5 s | decoder stream-size updates (`UpdateVideoStream` requests, `[video] stream.size`) <= 5; main present intervals (info) |
| S6 pop-out reopen | cold open of a new window, then 5 park/unpark (warm, parked-window reuse) cycles, plus hard close timings | cold open -> first present (< 150 ms); warm reopen -> first present, max (< 150 ms); park/unpark failures == 0 |

Notes on how to read it:

- "UI-thread block" is the action call plus the longest `RunFrame` + `TickDetachedHosts` turn it provokes (the loop's own wait
  excluded). "Worst UI-thread stall" is the longest gap between two consecutive loop turns, so it includes the (at most 16 ms)
  wait; a real stall shows as a large value.
- The MF rendered/dropped counters reach the player through the engines' ~1 Hz state pump, so they lag by up to a second; the
  percentage is a ratio and unaffected, the absolute counts undercount the tail of each source.
- The pop-out content carries a ticking bar so the child repaints every UI turn (the shape of a pop-out with a seek bar). The
  MF picture itself is composited by DWM outside the swapchain, so a static child would present almost nothing and its present
  interval would say nothing.
- Hole-vs-placement delta telemetry does not exist in the engine at the time of writing; S5 reports the compositor placement
  count (`[video.surface] place` lines) instead and says so in the table.
- Thresholds are the audit's targets and are never tuned by the probe; a FAIL is a result, not a probe bug.

Probe source: `src/FluentGpu.WindowsApp/Probes/VideoE2EProbe.cs` (+ the hidden page in `Pages/VideoE2EPage.cs`).
