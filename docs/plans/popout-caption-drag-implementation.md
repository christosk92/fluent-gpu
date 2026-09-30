# Pop-out drag: caption region instead of the client-press hand-off (as-built, 2026-09-22)

## Problem

The pop-out video window's title band moved the window by handing a CLIENT press to the OS move loop:
`IPlatformWindow.BeginSystemMove` released capture and POSTED `WM_NCLBUTTONDOWN`/`HTCAPTION` (mpv's `begin_dragging`).
Under `EnableMouseInPointer(TRUE)` the loop never ended on release — the window kept trailing the cursor until the next
click ("holds my event as if I'm dragging but I'm already doing something else"), long drags restarted every 1.5–2.5 s,
and a later attempt to hand the loop a synthetic `WM_LBUTTONUP` was a workaround inside documented undefined behaviour
(WM_POINTERUP docs: "If an application selectively consumes some pointer input and passes the rest to DefWindowProc,
the resulting behavior is undefined").

## Evidence (throwaway probe, `scratchpad/ptrprobe`, SendInput-driven, one 400×300 topmost window)

| scenario | loop entered | window moved during the drag | loop ended on release |
|---|---|---|---|
| client `WM_POINTERDOWN` processed → posted caption press; pointer msgs consumed | ~500 ms AFTER release | no | no (next click) |
| same, pointer msgs → `DefWindowProc` while the loop runs | ~500 ms AFTER release | no | no (next click) |
| `WM_NCHITTEST` → `HTCAPTION`; client pointer msgs consumed | 63 ms after press | yes | yes (≤ 2 ms) |
| `HTCAPTION`; pointer msgs → `DefWindowProc` | 63 ms after press | yes | yes (≤ 2 ms) |

In both client-press scenarios no `WM_POINTERUP` reached the window at all, so nothing the WndProc does with it can
matter. The contact's fate is decided at the DOWN: a press that starts as processed client pointer input never gets a
legacy stream; a press that starts non-client gets the whole one. This is the mechanism WinUI 3 / Windows App SDK
(`InputNonClientPointerSource`, `NonClientRegionKind.Caption`), Chromium and Electron (draggable regions → `HTCAPTION`)
use, and the one this engine's own main-window title bar already used.

## Design (as built)

- **Removed outright** (no legacy paths): `IPlatformWindow.BeginSystemMove`, `InputHooks.WindowBeginMove`,
  `MediaPlayerElement.DragMovesWindow` (+ `OnMoveArm/OnMoveDrag`), the headless `BeginSystemMoveCount`, the gates
  `gate.media.el.drag-moves-window` / `gate.media.el.drag-click-still-a-click`, the Win32 posted-caption-press case,
  the in-loop pointer-down guard and the `WM_LBUTTONUP` forwarding. App side: `Video.PopOutDrag`, `PopOut.DragBy`,
  `StageInput.DragMovesWindow`, `PopOutDragTests`.
- **Added** `InputKind.WindowMoveSizeBegan` (Win32: `WM_ENTERSIZEMOVE`, every loop) →
  `InputDispatcher.OnWindowMoveSizeBegan` → `InputHooks.WindowMoveSizeBeganObserved`. Pairs with the existing
  `WindowMoveSizeEnded`. The pop-out subscribes and calls `PlayerChromeFeed.WindowMoveStarted()`; the element releases
  the hold on the existing `WindowMoveSizeEndedObserved`.
- **Caption regions synthesize real hover.** `Win32Platform.NcHitAtScreen` now resolves every region (not buttons
  only); `NcHitFromCode` maps `HTCAPTION → TitleBarHit.Caption`; `NcHover` over a Caption region enqueues a
  `PointerMove` at the pointer's REAL client-DIP position on `NcSyntheticPointerId` on every update and returns false
  (DefWindowProc keeps the press). `NcPress` falls through for Caption. The NC → client crossing (`WM_POINTERUPDATE`)
  and `WM_NCMOUSELEAVE` park the synthetic pointer offscreen on the reserved id (both events; the leave previously used
  id 0 — a latent bug). Result: the pop-out's band still gets `OnPointerMoveWithin`/`OnPointerExit`, so its
  `SetPointerOverControls` hold and the chrome auto-show work exactly as over client area.
- **App**: `PopOutContent` pushes ONE `TitleBarRegion(RectF(0, ResizeStripDip, w, 44 − ResizeStripDip), Caption)`
  in a layout effect keyed on viewport size + fullscreen (zero regions while fullscreen). The band keeps only
  `OnPointerMoveWithin`/`OnPointerExit`, `Opacity`/`HitTestVisible` follow the chrome signal, no `Cursor` (a caption
  shows the OS arrow). `PopOut.ResizeStripDip = 8f` keeps the top ~8 DIP answering `HTTOP`.
- **Logs**: `[window.loop] begin` at every `WM_ENTERSIZEMOVE`; `[window.move] end …` / `[window.size] end …` at exit
  (decided by `_sizedInMoveSizeLoop`; `paints` counts keep-alive frames, never motion). The app whitelists all three.
- **Modal keep-alive**: the "a move WE started is never throttled" exemption is gone with the concept; a live-video
  window's pure move still bypasses the composited-resize full defer (`!_hasLiveVideo`), and the chrome hold means no
  fade paints are needed during the loop anyway.

## Trade-offs

- Dragging by the PICTURE is gone. A picture region would have to be `HTCAPTION` too, which loses click, double-click
  and right-click to the NC path; synthesizing those from `WM_NCLBUTTONUP`/`WM_NCLBUTTONDBLCLK`/`WM_NCRBUTTONUP` is a
  possible follow-up, not built.
- Double-clicking the band maximizes the pop-out (DefWindowProc's caption behaviour, `WS_MAXIMIZEBOX` is in
  `WS_OVERLAPPEDWINDOW`). Standard title-bar behaviour; double-click again restores.

## Verification

Engine Debug + Release build, VerticalSlice full run, `FluentGpu.Windows.Tests`; app Debug + Release, `Wavee.Tests`.
Live: open the pop-out, hover the band (chrome appears and holds), press-drag (one `[window.loop] begin` and one
`[window.move] end` per gesture, the window stops where the button comes up, Aero Snap works), touch/pen drag the same,
release outside the window → still ends, top strip still resizes, corners still resize, chrome fades when the pointer
leaves the band.
