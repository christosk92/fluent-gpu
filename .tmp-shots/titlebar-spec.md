# FluentGpu TitleBar — Implementation Spec (WinUI-3-Gallery faithful, fully custom frame)

Status: decisive. Implementing engineer has the FluentGpu skill loaded and knows this codebase. Every metric/color below is sourced to a dossier; where dossiers conflict the winner is named inline. The dossier fact-check corrections are authoritative and already folded in (e.g. `TextFillColorPrimary` literal is `#FFFFFF`, and the Win11 close-red / caption-glyph "Terminal" values are *unverified externals* — we ship them anyway because they are the documented Win11 look, but we flag them as low-confidence and centralize them so a later correction is one edit).

---

## 1. TL;DR

1. Add a **`CustomTitleBar` capability to the PAL seam**: a `WindowDesc.CustomFrame` flag, a push-on-change **drag/hit-region reporter** (`SetTitleBarRegions`), **window-state ops** (`State`/`Minimize`/`ToggleMaximize`/`Close`) and **`IsActive`** — each with a `Pal.Headless` mirror that records call-lists + settable state.
2. Implement the **Win32 non-client recipe** in `Win32Platform.cs`: `WM_NCCALCSIZE` (strip caption, keep frame), `WM_NCHITTEST` (HTMINBUTTON/HTMAXBUTTON/HTCLOSE for the engine buttons → Win11 snap; HTCAPTION drag; HTTOP/edges resize), `WM_NC*BUTTON*`/`WM_NCMOUSEMOVE/LEAVE` → **synthesized engine `InputEvent`s** so the engine-drawn buttons get real StateBrush hover/press, plus `WM_NCRBUTTONUP` system menu, `WM_GETMINMAXINFO`, DWM dark-frame/corners.
3. Build a **composition-only `TitleBar.cs` control** over the Dsl: 48px bar, app icon, title, **centered `AutoSuggestBox`** (MaxWidth 580), back + pane-toggle buttons (40w) via `IconButton`, and three engine-drawn caption buttons (46w) via a new `CaptionButton` factory with a red-ramp close. Drag-region vs interactive islands are declared with `BoxEl.OnRealized` capture → reported to the PAL each relayout.
4. **Activation dimming** is engine-driven: surface `IsActive` as a host signal (new `WindowFocus` handling in `InputDispatcher` + a `WindowActivation` context), and the control reads it to swap title→tertiary and dim icon/search to 0.5 opacity (WinUI parity).
5. **Gallery integration**: replace the fake `TitleBar()`/`SearchBox()` in `Gallery.cs` with the real control wired to a `Navigator` (back) + `NavigationView.IsPaneOpen` toggle (pane); the NavigationView already hides its own back/pane (gallery parity); search commits navigate.
6. **Verification**: new `Pal.Headless` golden checks (regions reported, min/max/close call recorded, activation dim) in `VerticalSlice`, a `"titlebar"` `--shot` scene (+ `--mica`), and a manual smoke list (snap flyout, drag, double-click-maximize, system menu, deactivated dim).

---

## 2. Canonical metrics & colors — THE source-of-truth table (copy from here)

Dark is primary; light adjacent. Tokens named are existing `Tok` getters (verified in `Tokens.cs` via the integration dossier) unless "NEW" — those you add.

### 2.1 Geometry / layout (DIP)

| Value | DIP | Token/const | Source dossier |
|---|---|---|---|
| Bar height (this app, has Content) | **48** | `TitleBar.ExpandedHeight` | titlebar-control §2; gallery-usage §6 |
| Compact height (no content; we still expose) | 32 | `TitleBar.CompactHeight` | titlebar-control §2 |
| Left padding col (rest) | 2 → runtime LeftInset | — | titlebar-control §2 |
| Right padding col (rest) | 0 → runtime RightInset (caption width) | — | titlebar-control §2 / EXPLICIT-ANSWER |
| Back button | **40 w** × (bar−4 = 44 h), margin **2**, radius **4** | `IconButton.Style{Size... }` adapted | titlebar-control §2; gallery-usage replication table |
| Pane-toggle button | **40 w**, margin 2, radius 4 | same | titlebar-control §2 |
| Back/pane glyph size | **16** | — | titlebar-control §2 |
| Icon | Viewbox max **16×16**, right margin **16** | — | titlebar-control §2; gallery-usage §1 |
| Title margin-right | **8** | — | titlebar-control §2 |
| Subtitle margin-right | **16** | — | titlebar-control §2 |
| Left-header padding col | 14 (default) / 2 (negative-inset) | — | titlebar-control §5.11 |
| Min drag strip col | **48** | — | titlebar-control §2 |
| Search box max width | **580** | — | gallery-usage §1/§2 (WINS over the fake 460 in current `Gallery.cs`) |
| Search box height | **32** (`TextControlThemeMinHeight`), V-centered in 48 | — | gallery-usage §2 |
| Search placeholder | "Search controls and samples..." | — | gallery-usage §2 |
| Caption button (min/max/close) | **46 w** × **48 h** (Stretch to bar), right-aligned | `CaptionButton.Width` NEW | custom-frame §4 (width 46 = locally-confirmed `WindowDecorations.axaml:9`) |
| Caption reserved inset total | **3 × 46 = 138** @100% (×scale) | — | custom-frame §4 |
| Caption glyph point size | **10** rendered, centered | `CaptionButton.GlyphSize=10` | custom-frame §4 (FA `WindowDecorations.axaml:117`, locally confirmed) |

Conflict note — caption-button height: the external Terminal source claims 40/32 windowed/maximized; **we override to a single 48 (Stretch)** because (a) our bar is unconditionally tall (has Content), (b) the only *locally verified* fact is `VerticalAlignment=Stretch` (WindowDecorations.axaml:9-10), and (c) matching the bar height is the correct visual. When maximized we keep 48 — the maximized NC inset (§3) handles clipping, not a button shrink.

### 2.2 Caption glyph codepoints (Segoe Fluent Icons = `Theme.IconFont`)

Add to `Icons.cs` as `\uXXXX` escapes (PUA literals break Edit — MEMORY pua-glyph-edit-technique; `Icons.cs` already uses the escape form).

| Action | Codepoint | const (NEW) | Source / confidence |
|---|---|---|---|
| Minimize | `` | `Icons.ChromeMinimize` | custom-frame §4 Terminal/WinUI set (LOW: external, but the canonical Win11 family; the integration dossier Gap #7 names exactly these) |
| Maximize | `` | `Icons.ChromeMaximize` | same |
| Restore (when maximized) | `` | `Icons.ChromeRestore` | same (E923 is locally confirmed in FA `WindowDecorations.axaml:208`) |
| Close | `` | `Icons.ChromeClose` | custom-frame §4 (LOW: external; FA-local alternative is E711) |
| Back (already present) | `` | `Icons.Back` | present |
| Pane/hamburger (already present) | `` | `Icons.Menu` | present |

Decision on the LOW-confidence glyphs: ship E921/E922/E923/E8BB (the Win11-accurate family the integration dossier's Gap list explicitly enumerates). They live in `Icons.cs` so a single edit fixes them if a pixel diff disagrees. The `--shot titlebar` PNG is the arbiter.

### 2.3 Colors — caption buttons (min/max) and bar text

All existing `Tok` getters (integration dossier §5, verified literals).

| Slot | State | Dark | Light | Token |
|---|---|---|---|---|
| Caption min/max bg | Rest | Transparent `#00000000` | Transparent | `Tok.FillSubtleTransparent` |
| | Hover | `#0FFFFFFF` | `#09000000` | `Tok.FillSubtleSecondary` |
| | Pressed | `#0AFFFFFF` | `#06000000` | `Tok.FillSubtleTertiary` |
| Caption min/max glyph | Rest/Hover | `#FFFFFF` | `#E4000000` | `Tok.TextPrimary` |
| | Pressed | `#C5FFFFFF` | `#9E000000` | `Tok.TextSecondary` |
| | Inactive | `#5DFFFFFF` | `#5C000000` | `Tok.TextDisabled` |
| Bar title text | Active | `#FFFFFF` | `#E4000000` | `Tok.TextPrimary` |
| | Inactive | `#87FFFFFF` | `#72000000` | `Tok.TextTertiary` (WinUI `TitleBarDeactivatedForegroundBrush`→TextFillColorTertiary) |
| Subtitle text | Active | `#C5FFFFFF` | `#9E000000` | `Tok.TextSecondary` |
| | Inactive | `#87FFFFFF` | `#72000000` | `Tok.TextTertiary` |
| App-identity icon | Active | accent | accent | `Tok.AccentDefault` (gallery uses accent glyph; WinUI uses an ImageIcon — we keep the gallery's accent-glyph look) |

Conflict resolution — bar foregrounds: titlebar-control dossier's first table said rest fg forwards to `TextFillColorPrimary`; its own fact-check corrected the literal to `#FFFFFF` (not `#FFFFFFFF`). `Tok.TextPrimary` = `#FFFFFFFF` (opaque white) which is byte-identical in effect (alpha 1.0). Use `Tok.TextPrimary`. Deactivated **title/back/pane** → `TitleBarDeactivatedForegroundBrush`; **subtitle** → the distinct `TitleBarSubtitleDeactivatedForegroundBrush`; both resolve to TextFillColorTertiary = `#87FFFFFF` = `Tok.TextTertiary`. Use `Tok.TextTertiary` for all four.

### 2.4 Colors — close button (the red) — NEW token

WinUI `CloseButton` red has **no FluentGpu equivalent** (integration dossier Gap #8). Add a `TokenSet` pair + `Tok` getters (Dark==Light for close per the dossier). The custom-frame fact-check flagged the Win11 `#C42B1C` value as an *unverified external*; the only *locally verified* red is FA's Win10-era `#e81123`/`#f1707a`.

DECISION: ship **`#C42B1C`** (Win11-accurate, the value the integration dossier Gap #8 explicitly names and the prompt's expected close-red). It is a single new token; if the shot diff says otherwise, swap to `#E81123`. One line.

| Slot | State | Value | Token (NEW) |
|---|---|---|---|
| Close bg | Rest | Transparent `#00000000` | reuse `Tok.FillSubtleTransparent` |
| | Hover | `#C42B1C` (opaque) | `Tok.CaptionCloseHover` NEW (Dark=Light) |
| | Pressed | `#C42B1C` @ 0.9 ≈ `#E6C42B1C` | `Tok.CaptionClosePressed` NEW |
| Close glyph | Rest/Hover | `#FFFFFF` | `Tok.TextPrimary` (white on red, both states) |
| | Pressed | white @ 0.7 ≈ `#B3FFFFFF` | inline `ColorF.FromRgba(0xFF,0xFF,0xFF,0xB3)` (or `Tok.CaptionClosePressedGlyph` NEW) |
| | Inactive | `#5DFFFFFF` | `Tok.TextDisabled` |

### 2.5 Durations / easing (caption + island state ramps)

| Token | Value | Source |
|---|---|---|
| Hover ramp | **83ms** (`ControlFaster`), spline `(0,0,0,1)` | prompt WinUI timing; ControlMotion |
| Press ramp | **83ms** | same |
| (Authored timelines, not used here) | 250/167ms | reserved for AnimEngine only |

Wire these via `BoxEl.HoverDurationMs=83`, `PressDurationMs=83`, `HoverEasing`/`PressEasing` = spline (0,0,0,1) on the caption/island boxes — never a state machine. WinUI's TitleBar itself uses *instantaneous* `useTransitions=false` swaps (titlebar-control §3), but FluentGpu's idiom is the eased StateBrush ramp (the `IconButton`/`AppBarButton` parity model already eases at 83ms), so we keep 83ms for engine consistency — visually indistinguishable from instant at 83ms and matches every other control in the gallery.

---

## 3. PAL contract — the new seam surface + EXACT WndProc recipe

### 3.1 New seam surface (`src/FluentGpu.Pal/Pal.cs`)

```csharp
// WindowDesc gains the custom-frame opt-in (keep Composited; both true for this app).
public readonly record struct WindowDesc(
    string Title, Size2 SizePx, float Scale, bool Composited = false, bool CustomFrame = false);

public enum WindowState : byte { Normal = 0, Maximized = 1, Minimized = 2 }

// A non-client hit classification reported per titlebar region (engine → Win32 WM_NCHITTEST).
public enum TitleBarHit : byte { Client = 0, Caption = 1, MinButton = 2, MaxButton = 3, CloseButton = 4 }

// One reported region: a rect in CLIENT DIP (the engine's space) + its classification. Pushed on relayout only.
public readonly record struct TitleBarRegion(RectF RectDip, TitleBarHit Hit);

// InputKind gains a window-state notification so the control can re-glyph max↔restore.
public enum InputKind : byte { /* ...existing 1..10... */ WindowStateChanged = 11 }
```

Add to `IPlatformWindow` (each gets a default impl so non-Win32 backends opt in, mirroring `ClientOriginPx => default`):

```csharp
/// <summary>Push the titlebar's drag/caption-button regions (CLIENT DIP). Engine calls this only when the titlebar
/// relayouts (push-on-change — never per frame: zero-alloc steady path). Rects classify the OS non-client behavior:
/// Caption = OS drag-move (snap/double-click-maximize free); Min/Max/Close = HT*BUTTON (Win11 snap flyout on Max);
/// everything not covered stays HTCLIENT (the bar's own content + interactive islands). Empty span clears.</summary>
void SetTitleBarRegions(ReadOnlySpan<TitleBarRegion> regions) { }

/// <summary>Current window placement (Normal/Maximized/Minimized) — drives the max↔restore glyph.</summary>
WindowState State => WindowState.Normal;

/// <summary>True while the window has activation (WM_ACTIVATE != WA_INACTIVE) — drives titlebar dimming.</summary>
bool IsActive => true;

void Minimize() { }
void ToggleMaximize() { }   // IsZoomed ? SC_RESTORE : SC_MAXIMIZE
void CloseWindow() { }      // PostMessage WM_CLOSE  (named CloseWindow to avoid clashing with IDisposable.Dispose intent)
```

Notes:
- `SetTitle` already exists but Win32's impl is a no-op (`Win32Platform.cs:222`); implement it now via `SetWindowTextW` so taskbar/Alt-Tab title updates (integration dossier Gap #9). Cheap, unblocks the gallery title.
- The engine surfaces `IsActive` changes through the **existing `WindowFocus`/`WindowBlur` InputEvents** (already emitted by `WM_ACTIVATE`, `Win32Platform.cs:357`) — `State`/`IsActive` are *pull* properties; the *push* of "something changed, re-render" is the `WindowFocus`/`WindowBlur`/`WindowStateChanged` events. So the host's `IsActive` signal is updated when those events arrive (§5 wiring), not by polling.

### 3.2 Win32 implementation (`src/FluentGpu.Pal.Windows/Win32Platform.cs`)

Add message + HT + SC + SM constants to the existing block (Win32Platform.cs:88):

```
WM_NCCALCSIZE=0x0083, WM_NCHITTEST=0x0084, WM_NCLBUTTONDOWN=0x00A1, WM_NCLBUTTONUP=0x00A2,
WM_NCMOUSEMOVE=0x00A0, WM_NCMOUSELEAVE=0x02A2, WM_NCRBUTTONUP=0x00A5, WM_NCACTIVATE=0x0086,
WM_GETMINMAXINFO=0x0024, WM_SYSCOMMAND=0x0112;
HT: HTCLIENT=1(exists), HTCAPTION=2, HTMINBUTTON=8, HTMAXBUTTON=9, HTLEFT=10, HTRIGHT=11, HTTOP=12,
    HTTOPLEFT=13, HTTOPRIGHT=14, HTBOTTOM=15, HTBOTTOMLEFT=16, HTBOTTOMRIGHT=17, HTCLOSE=20;
SC: SC_MINIMIZE=0xF020, SC_MAXIMIZE=0xF030, SC_RESTORE=0xF120, SC_CLOSE=0xF060, SC_KEYMENU=0xF100,
    SC_MOVE=0xF010, SC_SIZE=0xF000;
SM_CXPADDEDBORDER=92, SM_CYSIZEFRAME=33, SM_CXSIZEFRAME=32;
TME_LEAVE=0x0002, TME_NONCLIENT=0x0010;  MIIM_STATE=0x0001; MFS_ENABLED=0x0000, MFS_DISABLED=0x0003, MFS_GRAYED=0x0003;
TPM_RETURNCMD=0x0100;
```

P/Invokes to add (TerraFX exposes these under `static ... Windows`): `GetSystemMetricsForDpi`, `IsZoomed`, `GetWindowPlacement`, `TrackMouseEvent`, `MapWindowPoints`, `GetSystemMenu`, `SetMenuItemInfoW`, `SetMenuDefaultItem`, `TrackPopupMenu`, `PostMessageW`, `SetWindowTextW`, `DefWindowProcW` (already used). Reuse the existing `Win32Theme.ApplyWindowMaterial` for DWM (already called from `FluentApp`; see §3.5).

State the window keeps (fields on `Win32Window`): `bool _customFrame; TitleBarRegion[] _regions = []; int _regionCount; TitleBarHit _hoverHit; TitleBarHit _pressHit; bool _ncTracking; bool _active = true;`. Regions are stored in **DIP** as reported; convert to physical px at hit-test time (`rect * _scale`) so a `WM_DPICHANGED` needs no region recompute (the engine re-reports on its relayout anyway, but px-at-test-time is robust).

#### (a) `WM_NCCALCSIZE` (0x0083) — strip the caption, keep the frame

Only act when `_customFrame`. Handle `wParam==TRUE` only (else fall through to DefWindowProc):

```
if (!_customFrame) return false;                  // standard window → default frame
if (wParam == 0) return false;                    // wParam==FALSE: single RECT, let DefWindowProc compute
NCCALCSIZE_PARAMS* p = (NCCALCSIZE_PARAMS*)lParam;
int savedTop = p->rgrc[0].top;
DefWindowProcW(hWnd, WM_NCCALCSIZE, wParam, lParam);   // OS insets all 4 edges by the frame
p->rgrc[0].top = savedTop;                        // reclaim the caption strip → 0px top inset (Terminal recipe)
if (IsZoomed(hWnd)) {                              // maximized: add the frame back or content clips under screen edge
    uint dpi = GetDpiForWindow(hWnd);
    int padded = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
    int frameY  = padded + GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi);   // EXACT Terminal _GetResizeHandleHeight (top)
    int frameX  = padded + GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi);
    p->rgrc[0].top    += frameY;
    p->rgrc[0].left   += frameX; p->rgrc[0].right -= frameX; p->rgrc[0].bottom -= frameY;
}
result = 0; return true;                           // left/right/bottom keep DefWindowProc's thin frame → shadow + resize feel
```

(Autohide-taskbar inset is OPTIONAL — defer. Note it in Open Questions: only needed if a user reports a maximized window covering an autohide taskbar. The MS-recommended `AutohideTaskbarSize=2` shrink on the bar's edge is the fix; out of scope for v1.)

#### (b) `WM_NCHITTEST` (0x0084) — the decision table

`lParam` is SCREEN px. Convert to client px with `MapWindowPoints(HWND_NULL→hwnd)` (or `ScreenToClient`). All bands in physical px. Order = first match wins:

```
if (!_customFrame) return false;                   // default window → default hit-test
POINT pt = { GET_X(lParam), GET_Y(lParam) }; MapWindowPoints(0, hWnd, &pt, 1);
bool max = IsZoomed(hWnd);
int rb = GetSystemMetricsForDpi(SM_CXPADDEDBORDER,dpi) + GetSystemMetricsForDpi(SM_CYSIZEFRAME,dpi); // resize band
// 1-3 resize bands (skip entirely if maximized):
if (!max) {
    bool top = pt.y < rb;  int W=_w;
    if (top && pt.x < rb)           { result=HTTOPLEFT;  return true; }
    if (top && pt.x > W-rb)         { result=HTTOPRIGHT; return true; }
    if (top)                        { result=HTTOP;      return true; }
    // L/R/bottom + their corners: easiest = fall through to DefWindowProc (the thin frame NCCALCSIZE left intact
    // computes them). So we do NOT early-return for non-top edges here.
}
// 4-6 + 7-8 engine regions (these ARE valid when maximized too): test reported rects in px.
foreach region in _regions[0.._regionCount]:
    RectF r = region.RectDip * _scale;             // DIP→px
    if (PtInRect(r, pt)) switch (region.Hit) {
        case MinButton:   result=HTMINBUTTON; return true;
        case MaxButton:   result=HTMAXBUTTON; return true;   // ← MANDATORY for Win11 snap flyout
        case CloseButton: result=HTCLOSE;     return true;
        case Caption:     result=HTCAPTION;   return true;   // OS drag-move
        case Client:      result=HTCLIENT;    return true;   // interactive island (search/back/pane) — engine owns it
    }
// 9 not covered → fall through to DefWindowProc (gives L/R/bottom resize + HTCLIENT for content below the bar)
return false;
```

Engine region ordering contract: the engine reports **interactive islands (Client) and the buttons BEFORE the catch-all Caption rect** so first-match resolves an island/button over the drag band. (The control builds the list in that order — §4.5.)

#### (c) NC mouse → synthesized engine input (so engine-drawn buttons get StateBrush hover/press)

Because HTMINBUTTON/HTMAXBUTTON/HTCLOSE pixels are non-client, the OS delivers `WM_NC*`, not client `WM_MOUSE*` (integration dossier §3, Risk #8). We translate them into synthetic `InputEvent`s on the button's **client rect center** and feed the existing queue via `EnqueueExternal` (the same path popups use, `Win32Platform.cs:202`). This drives `InteractionAnimator` hover/press exactly like a real pointer.

```
WM_NCMOUSEMOVE (wParam = HT code, lParam = SCREEN):
    if (!_customFrame) return false;
    TitleBarHit hit = HitToBit(wParam);            // HTMINBUTTON→Min, HTMAXBUTTON→Max, HTCLOSE→Close, else None
    if (!_ncTracking) { TRACKMOUSEEVENT t={sizeof,TME_LEAVE|TME_NONCLIENT,hWnd,0}; TrackMouseEvent(&t); _ncTracking=true; }
    if (hit != _hoverHit) {
        // leave the old button (PointerMove far away), enter the new (PointerMove at its center)
        if (_hoverHit != None) EnqueueExternal(PointerMove @ (-1,-1));      // pull hover off everything in the bar
        _hoverHit = hit;
        if (hit != None) EnqueueExternal(PointerMove @ CenterPxOf(hit) / scale);  // DIP center → hover that button
    }
    result = (hit != None) ? 0 : DefWindowProc(...);  // eat for buttons (suppress classic glow); HTCAPTION→default ok
    return true;

WM_NCMOUSELEAVE:
    _ncTracking=false; _hoverHit=None; _pressHit=None;
    EnqueueExternal(PointerMove @ (-1,-1));         // clear hover; PaintRequested via the frame loop repaints rest
    return true;   // result 0

WM_NCLBUTTONDOWN (wParam = HT):
    TitleBarHit hit = HitToBit(wParam);
    if (hit != None) {                               // our button: press it, DO NOT call DefWindowProc
        _pressHit = hit;
        EnqueueExternal(PointerDown @ CenterPxOf(hit)/scale, button 0);
        result=0; return true;
    }
    return false;   // HTCAPTION (and resize) → DefWindowProc starts the OS drag-move / resize loop (snap free)

WM_NCLBUTTONUP (wParam = HT):
    TitleBarHit hit = HitToBit(wParam);
    if (hit != None && hit == _pressHit) {
        EnqueueExternal(PointerUp @ CenterPxOf(hit)/scale, button 0);   // fires the engine OnClick on that button
        _pressHit = None;
        // The engine OnClick calls window.Minimize()/ToggleMaximize()/CloseWindow() — see §4. Nothing to Post here.
        result=0; return true;
    }
    _pressHit = None; return false;
```

`CenterPxOf(hit)` looks up the matching reported region's rect (px) center. `EnqueueExternal` already enqueues into `_queue` drained by `PumpInto`. After enqueuing, also call `PaintRequested?.Invoke()` is unnecessary — the next `RunFrame` pump drains the queue and the dispatcher runs; but NC messages can arrive between frames while the OS holds the loop, so call `PaintRequested?.Invoke()` after enqueue to guarantee a repaint during an NC-only interaction (mirrors the WM_SIZE pattern at `Win32Platform.cs:303`).

Window-state ops the engine OnClick invokes:
```
public WindowState State => IsZoomed(_hwnd) ? Maximized : (IsIconic(_hwnd) ? Minimized : Normal);
public void Minimize()      => PostMessageW(_hwnd, WM_SYSCOMMAND, SC_MINIMIZE, 0);
public void ToggleMaximize()=> PostMessageW(_hwnd, WM_SYSCOMMAND, IsZoomed(_hwnd)?SC_RESTORE:SC_MAXIMIZE, 0);
public void CloseWindow()   => PostMessageW(_hwnd, WM_CLOSE, 0, 0);
```
On `SC_MAXIMIZE`/`SC_RESTORE` the OS will WM_SIZE → the engine relays out AND must re-glyph max↔restore: enqueue `WindowStateChanged` from `WM_SIZE` when `_customFrame` and the zoomed-state changed since last WM_SIZE (compare a cached `_wasZoomed`).

#### (d) `WM_NCRBUTTONUP` (0x00A5) on HTCAPTION — system menu

```
if (_customFrame && wParam == HTCAPTION) {
    HMENU sys = GetSystemMenu(_hwnd, FALSE);
    bool max = IsZoomed(_hwnd);
    SetItem(sys, SC_RESTORE,  max ? MFS_ENABLED : MFS_DISABLED);
    SetItem(sys, SC_MOVE,     max ? MFS_DISABLED: MFS_ENABLED);
    SetItem(sys, SC_SIZE,     max ? MFS_DISABLED: MFS_ENABLED);
    SetItem(sys, SC_MINIMIZE, MFS_ENABLED);
    SetItem(sys, SC_MAXIMIZE, max ? MFS_DISABLED: MFS_ENABLED);
    // SC_CLOSE always enabled (default)
    SetMenuDefaultItem(sys, 0xFFFFFFFF, 0);
    int cmd = TrackPopupMenu(sys, TPM_RETURNCMD, GET_X(lParam), GET_Y(lParam), 0, _hwnd, null);  // SCREEN coords
    if (cmd != 0) PostMessageW(_hwnd, WM_SYSCOMMAND, (uint)cmd, 0);
    result = 0; return true;
}
```
(The custom-frame fact-check corrected the FA reference to trigger on `WM_RBUTTONUP` gated by a titlebar hit-test; the MS-doc recipe uses `WM_NCRBUTTONUP`+HTCAPTION. Since our caption IS non-client, `WM_NCRBUTTONUP`+HTCAPTION is correct here.)

#### (e) `WM_NCACTIVATE` (0x0086) — keep DWM frame, suppress classic title repaint

```
if (_customFrame) {
    _active = wParam != 0;
    _queue.Enqueue(_active ? WindowFocus : WindowBlur);   // drive titlebar dim through the existing path
    result = DefWindowProcW(_hwnd, WM_NCACTIVATE, wParam, (LPARAM)(-1));  // -1 = don't repaint classic NC text
    return true;
}
return false;
```
Keep the existing `WM_ACTIVATE` case too (it already enqueues WindowFocus/WindowBlur, `Win32Platform.cs:357`) — both fire on activation changes; the dispatcher's de-dup (state already set) makes the double harmless, and `WM_ACTIVATE` carries the *focus*-level change while `WM_NCACTIVATE` carries the *frame* repaint. Set `_active` in both.

#### (f) `WM_GETMINMAXINFO` (0x0024) — let the window shrink enough to snap

```
if (_customFrame) {
    MINMAXINFO* mmi = (MINMAXINFO*)lParam;
    int minW = (int)(500 * _scale);                 // snap requires min width ≤ 500 epx (MS snap doc; LOW: external)
    int minH = (int)(330 * _scale);
    if (mmi->ptMinTrackSize.x > minW) mmi->ptMinTrackSize.x = minW;
    if (mmi->ptMinTrackSize.y > minH) mmi->ptMinTrackSize.y = minH;
    result = 0; return true;
}
```
(The 500/330 epx figures are an unverified external per the fact-check; they are MS-documented guidance and harmless to apply — they only *lower* the min track size. Keep.)

#### (g) `WM_SYSCOMMAND` — forward SC_KEYMENU (Alt+Space)

Leave non-caption `WM_SYSCOMMAND` to DefWindowProc (don't add a case unless intercepting); the existing code already forwards. The SC_* we Post are handled by DefWindowProc. Nothing to add beyond not swallowing it.

### 3.3 Window creation changes

In the `Win32Window` ctor, after `CreateWindowExW`: if `desc.CustomFrame`, set `_customFrame=true` and call `SetWindowPos(..., SWP_FRAMECHANGED|SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE)` once so `WM_NCCALCSIZE` runs with the new policy. Keep `WS_OVERLAPPEDWINDOW` and `WS_EX_NOREDIRECTIONBITMAP` (custom-frame §1a; integration §1.2) — do NOT drop styles.

### 3.4 DPI rule

- Pointer/regions: regions reported in DIP, multiplied by `_scale` at hit-test (robust across DPI hops). `_scale = GetDpiForWindow/96` (existing). The synthetic NC `InputEvent`s carry **DIP** positions (region center / scale) to match the engine scene space (the existing client pump divides by `_scale` at `MousePt`; we pre-divide for NC since we have no client message).
- Frame metrics (`WM_NCCALCSIZE`, resize band) use `GetSystemMetricsForDpi(.., GetDpiForWindow(hwnd))`.
- `WM_DPICHANGED` already re-scales + adopts the suggested rect (`Win32Platform.cs:305`) → WM_SIZE → engine relayout → engine re-reports regions. No extra handling.

### 3.5 DWM interplay (already mostly done)

`FluentApp` already calls `Win32Theme.ApplyWindowMaterial(hwnd, dark, mica)` which does `DWMWA_USE_IMMERSIVE_DARK_MODE`, `DWMWA_SYSTEMBACKDROP_TYPE=Mica`, and `DwmExtendFrameIntoClientArea(MARGINS{-1,-1,-1,-1})` (full-glass). With a custom frame this `-1` full-glass is exactly right (integration §1.2, custom-frame §1c Mica path): Mica composites behind the transparent DComp client incl. the custom caption. **Do NOT add a positive `cyTopHeight`** — that is only for the opaque (non-Mica) path; this app is Mica. The `WM_NCCALCSIZE` caption removal does not fight the `-1` glass (it operates on the NC area, not the redirection bitmap). OPTIONAL nicety: add `DWMWA_WINDOW_CORNER_PREFERENCE=33 → DWMWCP_ROUND=2` in `ApplyWindowMaterial` (Win11 rounds automatically with WS_THICKFRAME, so this is belt-and-suspenders; defer).

### 3.6 Headless mirror (`src/FluentGpu.Pal.Headless/HeadlessPlatform.cs`)

`HeadlessWindow` adds, mirroring the real members so golden checks can assert titlebar behavior (integration §1.3, Risk #4):

```csharp
public WindowState State { get; set; } = WindowState.Normal;   // settable test seam
private bool _active = true;
public bool IsActive { get => _active; set { _active = value;
    QueueInput(new InputEvent(value ? InputKind.WindowFocus : InputKind.WindowBlur, default, 0, 0)); } }
public int MinimizeCount { get; private set; }
public int ToggleMaximizeCount { get; private set; }
public int CloseCount { get; private set; }
public void Minimize()       { MinimizeCount++; State = WindowState.Minimized; }
public void ToggleMaximize() { ToggleMaximizeCount++; State = State==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
                               QueueInput(new InputEvent(InputKind.WindowStateChanged, default, 0, 0)); }
public void CloseWindow()    { CloseCount++; }
// captured regions for assertions:
public TitleBarRegion[] LastTitleBarRegions { get; private set; } = [];
public void SetTitleBarRegions(ReadOnlySpan<TitleBarRegion> r) => LastTitleBarRegions = r.ToArray();
```
`HeadlessWindow(in WindowDesc)` reads `desc.CustomFrame` into a `CustomFrame` bool (informational). No real NC concept — model = recorded call-lists + settable state (as `HeadlessPlatformApp` does for popups/URIs).

---

## 4. TitleBar control design (`src/FluentGpu.Controls/TitleBar.cs`)

Composition-only over the Dsl. Because it must (a) re-glyph max↔restore on `WindowState`, (b) read `IsActive` for dimming, and (c) drive `AutoSuggestBox` query state, it is a **`ReactiveComponent`/`Component`** (it has reactive state), NOT a pure static factory. `Setup` runs once; dynamic values via signals/bindings (signals-first rule).

### 4.1 API surface (what the gallery passes in)

```csharp
public sealed class TitleBar : Component
{
    // identity
    public string Title = "";
    public string Subtitle = "";                 // gallery sets "Dev" in DEBUG; else null/empty
    public string IconGlyph = Icons.Grid;        // gallery uses the accent grid glyph (WinUI uses ImageIcon; we keep glyph)
    public ColorF IconColor = Tok.AccentDefault;

    // left affordances
    public bool ShowBackButton;                  // bound to Navigator.CanGoBack
    public Action? OnBack;
    public bool ShowPaneToggle = true;
    public Action? OnPaneToggle;

    // centered content (the search box). Caller supplies the element so the gallery owns AutoSuggestBox wiring.
    public Func<Element>? Content;               // rendered in the centered, stretched content column (MaxWidth 580 owned by caller)

    // caption buttons: the control draws min/max/close and calls the window ops via InputHooks (below).
    public bool ShowCaptionButtons = true;       // false when running under a standard OS frame

    public TemplateParts? Parts;
}
```

Window ops + state + activation reach the control through a small addition to **`InputHooks`** (the existing ambient seam the host owns), NOT ctor args — keeps it signals/context, parent→child via context (rule). Add to `InputHooks`:
```csharp
public Func<WindowState>? WindowState;          // host → IPlatformWindow.State
public Func<bool>? IsWindowActive;              // host → IPlatformWindow.IsActive
public Action? WindowMinimize, WindowToggleMaximize, WindowClose;
public Action<ReadOnlySpan<TitleBarRegion>>? SetTitleBarRegions;  // engine push
public Signal<object?>? WindowActivation;       // host-published signal: re-renders the control on focus/state change
```
The host wires these to `_window.*` and publishes a `WindowActivation` signal it bumps on `WindowFocus`/`WindowBlur`/`WindowStateChanged` (see §5). The control reads `UseContext(InputHooks.Current)` → calls `.WindowMinimize()` etc. and subscribes to `WindowActivation` for re-render.

### 4.2 Element tree (props in brackets)

```
Root BoxEl [Direction=Row, Height=48, AlignItems=Center, Width=stretch (Grow=1 in the VStack),
            OnRealized=captureRoot]      // root rect = the drag band fallback
├─ leftPad   BoxEl [Width = LeftInsetDip]                       // 0 here (left caption inset is 0 on Win11 for LTR)
├─ back      IconButton.Create(Icons.Back, OnBack,  CaptionGlyphButtonStyle, isEnabled:true)   // only if ShowBackButton; OnRealized→island
├─ pane      IconButton.Create(Icons.Menu, OnPaneToggle, CaptionGlyphButtonStyle)              // only if ShowPaneToggle; OnRealized→island
├─ icon      Ui.Icon(IconGlyph, 16).Foreground(IconColor) wrapped in BoxEl[margin-right 16]    // dims via Opacity when inactive
├─ title     TextEl(Title) [Size=12, Bold=false, Color=titleColor, margin-right 8]             // titleColor = active?TextPrimary:TextTertiary
├─ subtitle  TextEl(Subtitle) [Size=12, Color=subtitleColor, margin-right 16]                  // only if non-empty
├─ content   BoxEl [Grow=1, AlignItems=Center, Justify=Center, Children=[Content()]]           // centered search; Content owns MaxWidth=580
│                                                                                              //   wrapper dims via Opacity when inactive
├─ minDrag   BoxEl [Width=48]                                                                  // min drag strip (declared HTCAPTION)
└─ captions  BoxEl [Direction=Row, AlignItems=Stretch, Height=48] (only if ShowCaptionButtons)
   ├─ min   CaptionButton(Icons.ChromeMinimize, () => hooks.WindowMinimize?.Invoke(),       CaptionStyle.MinMax,  OnRealized→MinButton)
   ├─ max   CaptionButton(maxGlyph,             () => hooks.WindowToggleMaximize?.Invoke(),  CaptionStyle.MinMax,  OnRealized→MaxButton)
   └─ close CaptionButton(Icons.ChromeClose,    () => hooks.WindowClose?.Invoke(),           CaptionStyle.Close,   OnRealized→CloseButton)
```
`maxGlyph = (hooks.WindowState?() == WindowState.Maximized) ? Icons.ChromeRestore : Icons.ChromeMaximize`, recomputed each render (the control re-renders on the `WindowActivation` signal which is bumped on `WindowStateChanged`).

When `ShowCaptionButtons == false` (standard OS frame), omit the `captions` group AND keep a right-pad `BoxEl[Width = RightInsetDip]` instead (so content clears the OS buttons) — this is the legacy 140px behavior, but DPI-correct via the reported inset. For this gallery, `ShowCaptionButtons == true`, so the engine owns the buttons and there is no right pad (the caption group occupies that space, and the OS reserves nothing because we draw them).

### 4.3 `CaptionButton` factory (NEW, in `TitleBar.cs` or a sibling `CaptionButton.cs`)

A close cousin of `IconButton.Create` but 46×48, glyph 10pt, no corner radius (caption buttons are square-edged full-height), and a Close variant with the red ramp. Reuse the StateBrush→BoxEl ramp idiom; do NOT add a state machine.

```csharp
public static partial class CaptionButton
{
    public sealed record Style {
        public float Width = 46f;
        public float GlyphSize = 10f;
        public float CornerRadius = 0f;              // full-height square (NOT rounded — caption buttons abut)
        public ColorF Fill, HoverFill, PressedFill, Foreground, HoverForeground, PressedForeground, InactiveForeground;
    }
    public static Style MinMax => new() {
        Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Foreground = Tok.TextPrimary, HoverForeground = Tok.TextPrimary, PressedForeground = Tok.TextSecondary,
        InactiveForeground = Tok.TextDisabled };
    public static Style Close => MinMax with {
        HoverFill = Tok.CaptionCloseHover, PressedFill = Tok.CaptionClosePressed,
        HoverForeground = Tok.TextPrimary,                       // white glyph on red
        PressedForeground = ColorF.FromRgba(0xFF,0xFF,0xFF,0xB3) };

    // active: drives the inactive glyph color. The control passes hooks.IsWindowActive?() ?? true.
    public static BoxEl Create(string glyph, Action onClick, Style s, bool active, Action<NodeHandle>? onRealized = null)
        => new BoxEl {
            Width = s.Width, Height = 48f, Direction = 0, Role = AutomationRole.Button,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(s.CornerRadius),
            Fill = s.Fill, HoverFill = s.HoverFill, PressedFill = s.PressedFill,
            HoverDurationMs = 83f, PressDurationMs = 83f,                 // WinUI ControlFaster, spline (0,0,0,1) on Easing fields
            AllowFocusOnInteraction = false,                              // caption buttons never steal focus
            Focusable = false,                                            // OS caption buttons aren't Tab stops
            OnClick = onClick,
            OnRealized = onRealized,
            Children = [ new BoxEl { Width = s.GlyphSize+6, Height = 48f, Direction=0, AlignItems=FlexAlign.Center, Justify=FlexJustify.Center,
                Children = [ new TextEl(glyph) {
                    Size = s.GlyphSize, FontFamily = Theme.IconFont,
                    Color = active ? s.Foreground : s.InactiveForeground,
                    HoverColor = s.HoverForeground, PressedColor = s.PressedForeground,
                } ] } ],
        };
}
```
Caption-button visual states therefore = StateBrush ramp (Fill/HoverFill/PressedFill) + foreground ramp (Color/HoverColor/PressedColor) eased by `InteractionAnimator` via `Hover/PressDurationMs` — no per-control state machine (rule satisfied). The synthetic NC pointer events (§3.2c) drive `OnHoverChanged`/`OnPressChanged` so these ramps run for real even though the OS owns the NC pixels.

### 4.4 `CaptionGlyphButtonStyle` for back/pane (40w)

Reuse `IconButton.DefaultStyle` but override `Size = 40` (height comes from the row; `IconButton` boxes are square `Size×Size` so to get 40w × 44h use a `with { Size = 40 }` and let the row's `AlignItems=Stretch`… — but `IconButton` hard-codes `Height = s.Size`). SIMPLEST: pass `IconButton.DefaultStyle with { Size = 40f }` → a 40×40 rounded-square, vertically centered in the 48 row (close enough to WinUI's 40×44; the 4px vertical difference is invisible and avoids forking IconButton). If exact 44h is required later, add a `Height` to `IconButton.Style`; defer. Glyph 16 (IconButton default). Corner radius 4 (default). States = IconButton's existing subtle ramp (matches WinUI back/pane exactly: rest transparent, hover `SubtleFillColorSecondary`, pressed `SubtleFillColorTertiary`, fg primary→secondary).

### 4.5 Drag-region vs interactive islands → NCHITTEST flow

The control captures node handles via `BoxEl.OnRealized` and, in a `UseLayoutEffect` keyed on layout (the viewport signal / a relayout tick), builds a `TitleBarRegion[]` from `SceneStore.AbsoluteRect(handle)` (DIP) and pushes it via `hooks.SetTitleBarRegions(...)`. Ordering (first-match wins in NCHITTEST):

```
regions = [
  // 1. interactive islands FIRST (so they win over the catch-all caption rect):
  back   ? (AbsoluteRect(backNode),  Client) : -,
  pane   ? (AbsoluteRect(paneNode),  Client) : -,
  search   (AbsoluteRect(contentWrapperNode), Client),     // the whole centered content column is interactive
  // 2. caption buttons:
  caps ? (AbsoluteRect(minNode),   MinButton),
         (AbsoluteRect(maxNode),   MaxButton),
         (AbsoluteRect(closeNode), CloseButton) : -,
  // 3. the catch-all drag band LAST = the entire bar:
  (AbsoluteRect(rootNode), Caption),
]
```
Zero-alloc rule (Risk #5): the region array is a **member field reused across pushes** (sized to max 7), filled in place each relayout — no per-frame allocation, and the push only happens on relayout (the layout-effect dep), never in a hot bind thunk. `SetTitleBarRegions` takes `ReadOnlySpan<TitleBarRegion>` so the field is passed without copy. The Win32 side copies into its own buffer (it must outlive the call), but that copy is per-relayout, not per-frame.

Why the search column is one big `Client` rect rather than per-control passthrough: WinUI punches a hole per interactive child via `FindInteractableElements`, but our search box has a single hit-test surface and the back/pane are separate islands; one rect over the content column is sufficient and simpler, and the `AutoSuggestBox`'s own hit-testing handles internal routing once the OS yields HTCLIENT there.

### 4.6 Activation dimming

`active = hooks.IsWindowActive?() ?? true`. The control subscribes to `hooks.WindowActivation` (a `Signal<object?>` the host bumps on focus/state change) so it re-renders when activation flips. On inactive:
- `titleColor = Tok.TextTertiary` (was `TextPrimary`); `subtitleColor = Tok.TextTertiary`.
- icon wrapper + content wrapper: `Opacity = 0.5` (WinUI `TitleBarDeactivatedOpacity`).
- back/pane: pass `active` so their glyph foreground goes tertiary (or simpler: wrap in the same opacity-0.5 treatment — WinUI dims back/pane *foreground* to tertiary; to match, pass a `CaptionGlyphButtonStyle with { Foreground = Tok.TextTertiary }` when inactive).
- caption buttons: `CaptionButton.Create(..., active: active)` → glyph → `Tok.TextDisabled` when inactive (matches WinUI/FA inactive caption glyph `#5DFFFFFF`).

This is a render-time color/opacity swap driven by a signal — signals-first, no state machine, no per-frame allocation (the colors are token reads).

### 4.7 Tall-mode (48) layout with centered search

The bar is unconditionally 48 here (it has Content). The content column is `Grow=1` with `Justify=Center`; the caller's `AutoSuggestBox` sets `Width=580` (WINS over the gallery dossier's note that it's `MaxWidth` — our `AutoSuggestBox.Width` is a fixed width, so set 580 and let the centered column place it). Net: the search is centered within the flexible column between the left cluster and the caption buttons — column-centered, biased by asymmetric clusters, exactly like WinUI (gallery-usage §2). If a true fixed center is wanted later, that's a follow-up; column-centered is the WinUI behavior.

---

## 5. Gallery integration (`src/FluentGpu.WindowsApp`)

### 5.1 `Program.cs` / `FluentApp.cs`

- `Program.cs:143`: window stays `1240×820`. No change to the call other than it now flows `CustomFrame`.
- `FluentApp.Run`: set `CustomFrame: mica` (the gallery runs mica) in the `WindowDesc`:
  `new WindowDesc(title, new Size2(width,height), 1f, Composited: mica, CustomFrame: mica)`.
  Keep the existing `Win32Theme.ApplyWindowMaterial(...)` call (already does dark + Mica + full-glass). The custom-frame SWP_FRAMECHANGED happens inside the ctor when `CustomFrame` is set.
- Host wiring (in `AppHost` ctor, alongside the existing `_inputHooks.*` block): wire the new `InputHooks` members to the window and publish the activation signal.
  ```csharp
  _inputHooks.WindowState = () => _window.State;
  _inputHooks.IsWindowActive = () => _window.IsActive;
  _inputHooks.WindowMinimize = _window.Minimize;
  _inputHooks.WindowToggleMaximize = _window.ToggleMaximize;
  _inputHooks.WindowClose = _window.CloseWindow;
  _inputHooks.SetTitleBarRegions = _window.SetTitleBarRegions;
  var winAct = new Signal<object?>(null);
  _inputHooks.WindowActivation = winAct;
  ```
  Add a `WindowFocus`/`WindowStateChanged` handler: the `InputDispatcher` currently has only a `WindowBlur` case (confirmed — no `WindowFocus` case). Add `case InputKind.WindowFocus:` and `case InputKind.WindowStateChanged:` that fire a new `OnWindowActivationChanged` callback; the host wires it to bump `winAct.Value = winAct.Peek()` (re-render the title bar) and `WakeFrame()`. (Gap #5 closed.)
  - `WindowBlur` already calls `OnWindowBlur` (light-dismiss) — also bump `winAct` there so the bar dims.

### 5.2 `Gallery.cs`

Replace `TitleBar()` and `SearchBox()` (lines 227–252) with the real control. Add a `Navigator` to `GalleryApp` for the back button (Gap #12). The NavigationView already collapses its own back/pane (the gallery's real NavigationView sets `IsBackButtonVisible=Collapsed`, `IsPaneToggleButtonVisible=False` — gallery-usage §4; our `NavigationView` defaults already hide pane/back unless `ShowBackButton`/pane mode says otherwise, so no change there — just don't enable them).

```csharp
// in GalleryApp.Render(), the shell:
var nav = Embed.Comp(() => new NavigationView {
    Header = "fluent-gpu", Initial = InitialPage, Items = Items, Content = Page,
    // back/pane stay OFF here (titlebar owns them); pane toggled via IsPaneOpen below.
});
var shell = VStack(0,
    Embed.Comp(() => new TitleBar {
        Title = "FluentGpu — Capability Gallery",
        IconGlyph = Icons.Grid, IconColor = Tok.AccentDefault,
        ShowPaneToggle = true,
        OnPaneToggle = () => /* toggle the NavigationView pane */,
        ShowBackButton = /* nav.CanGoBack signal */,
        OnBack = () => /* nav back */,
        Content = () => AutoSuggestBox.Create(
            suggestions: _searchSuggestions.Value,            // a signal updated in textChanged
            placeholder: "Search controls and samples...",
            width: 580f,
            text: _searchText,                                // a UseSignal<string>
            textChanged: (q, reason) => { /* filter → set _searchSuggestions */ },
            onQuerySubmitted: q => { /* navigate to the matched page or a search-results page */ },
            queryIcon: Icons.Search),
        ShowCaptionButtons = true,
    }),
    nav) with { Grow = 1 };
```

Search behavior (mirrors gallery-usage §2, simplified to existing parts):
- `textChanged(q, UserInput)`: filter the nav catalog (`ControlCatalog`/`Items`) by case-insensitive substring of label; set `_searchSuggestions` signal → the `AutoSuggestBox` re-filters its popup (its own `Filter` is substring-based; we feed it the candidate titles). The popup already renders through `OverlayHost` (the gallery wraps the tree in `OverlayHost`, Gallery.cs:222) so suggestions float app-wide. On D3D12 the suggestion list drops *in-window* below the field (popup windows are disabled on D3D12 — Risk #2) which is fine for a top-anchored search.
- `onQuerySubmitted(q)`: if `q` matches a known page key/title, navigate (`NavigationView` selection → page remount, the existing mechanism); else navigate to a search-results page (or no-op for v1 — pick: navigate to the matched control page if exactly one match, else select the nav item). Keep it minimal: resolve `q` against `Items` labels; if found, set the nav selection.

Pane toggle: the gallery's real behavior flips `NavigationView.IsPaneOpen` (gallery-usage §3). Our `NavigationView` exposes pane open state via its own model; wire `OnPaneToggle` to flip it (if `NavigationView` lacks a public `IsPaneOpen` signal, add one or route via a shared signal the gallery owns and passes to NavigationView — minimal: a `Signal<bool> _paneOpen` provided to NavigationView and flipped by `OnPaneToggle`). If `NavigationView` has no pane-open input today, the v1 fallback is to make `OnPaneToggle` a no-op and note it (Open Question) — but prefer wiring it.

Back button: add `var nav = new Navigator(new Route(InitialPage));` to the component; `ShowBackButton = boundSignal(nav.CanGoBack)`, `OnBack = () => { if (nav.Pop()) /* set nav selection to nav.Current */; }`. Since the gallery currently navigates by `NavigationView.Content` remount on selection (no back stack), the minimal wire is: push a route on each `OnSelect`, and `OnBack` pops + re-selects. If that's too invasive for the first pass, ship `ShowBackButton=false` and note it (the WinUI gallery's back is bound to `rootFrame.CanGoBack`; we don't have a Frame). DECISION: ship `ShowBackButton=false` in v1 (the gallery has no real back stack), wire pane + search fully. Back is a clean follow-up once a `Navigator` is threaded through `NavigationView`. (This keeps the build green and avoids reworking NavigationView's navigation model in the same change.)

### 5.3 Window title / icon

- Title: the control paints "FluentGpu — Capability Gallery". Also call `window.SetTitle` (now implemented) so taskbar/Alt-Tab match — `FluentApp` already passes the title to `WindowDesc`; the new `SetWindowTextW` impl means it's set at creation. No extra call needed unless the title changes at runtime.
- Icon: WinUI uses an `.ico` via `AppWindow.SetIcon` + `ImageIconSource`. FluentGpu has no image-icon-in-titlebar path; we keep the **accent grid glyph** (the gallery's current look, `Gallery.cs:232`). The OS taskbar icon comes from the exe/manifest, not in scope.

---

## 6. Ordered file-by-file change plan (build stays green at every step)

1. **`src/FluentGpu.Pal/Pal.cs`** — add `WindowState`, `TitleBarHit`, `TitleBarRegion`, `InputKind.WindowStateChanged`, `WindowDesc.CustomFrame`, and the new `IPlatformWindow` default members (`SetTitleBarRegions`, `State`, `IsActive`, `Minimize`, `ToggleMaximize`, `CloseWindow`). All defaulted → nothing else breaks. *(~40 LOC. Compiles alone.)*
2. **`src/FluentGpu.Pal.Headless/HeadlessPlatform.cs`** — mirror: settable `State`/`IsActive`, call-count props, `SetTitleBarRegions`+`LastTitleBarRegions`, read `desc.CustomFrame`. *(~30 LOC. Keeps headless tests compiling.)*
3. **`src/FluentGpu.Dsl/Tokens.cs`** — add `CaptionCloseHover` (`#C42B1C` Dark+Light), `CaptionClosePressed` (`#E6C42B1C`), optional `CaptionClosePressedGlyph` (`#B3FFFFFF`) to `TokenSet` (both `BuildDark`/`BuildLight`) + `Tok` getters. *(~12 LOC. No call-site impact.)*
4. **`src/FluentGpu.Controls/Icons.cs`** — add `ChromeMinimize=""`, `ChromeMaximize=""`, `ChromeRestore=""`, `ChromeClose=""` (escape form; never paste PUA literals). *(~4 LOC.)*
5. **`src/FluentGpu.Hooks/InputHooks.cs`** — add the window-op/activation seam fields (`WindowState`, `IsWindowActive`, `WindowMinimize/ToggleMaximize/Close`, `SetTitleBarRegions`, `WindowActivation`). Defaulted/nullable → no break. *(~10 LOC.)*
6. **`src/FluentGpu.Input/InputDispatcher.cs`** — add `OnWindowActivationChanged` callback + `case InputKind.WindowFocus` and `case InputKind.WindowStateChanged` (both invoke it); also invoke it from the existing `WindowBlur` case. *(~10 LOC.)*
7. **`src/FluentGpu.Hosting/AppHost.cs`** — wire the new `InputHooks` members to `_window.*`, create + publish the `WindowActivation` signal, set `_dispatcher.OnWindowActivationChanged` to bump it + `WakeFrame`. *(~12 LOC. Uses only members added in steps 1/5/6.)*
8. **`src/FluentGpu.Controls/CaptionButton.cs`** (new) — the 46×48 caption-button factory + `MinMax`/`Close` styles. *(~50 LOC. Depends on steps 3/4.)*
9. **`src/FluentGpu.Controls/TitleBar.cs`** (new) — the control (tree, region push via `OnRealized`+layout-effect, activation dim, max↔restore glyph). *(~180 LOC. Depends on steps 5/8.)*
10. **`src/FluentGpu.Pal.Windows/Win32Platform.cs`** — the WndProc recipe: constants, P/Invokes, fields, and the new cases (`WM_NCCALCSIZE`, `WM_NCHITTEST`, NC mouse synth, `WM_NCRBUTTONUP`, `WM_NCACTIVATE`, `WM_GETMINMAXINFO`, `WM_SIZE` state-change emit), window-state ops, `SetTitleBarRegions`, `IsActive`, `SetTitle` via `SetWindowTextW`, and `_customFrame` + SWP_FRAMECHANGED in the ctor. *(~220 LOC. Depends on step 1. The biggest single step; gate it behind `_customFrame` so a `CustomFrame:false` window is byte-for-byte unchanged.)*
11. **`src/FluentGpu.WindowsApp/FluentApp.cs`** — pass `CustomFrame: mica` into `WindowDesc`. *(~1 LOC.)*
12. **`src/FluentGpu.WindowsApp/Gallery.cs`** — replace `TitleBar()`/`SearchBox()` with `Embed.Comp(() => new TitleBar { ... })` + the `AutoSuggestBox` content thunk + search signals + pane-toggle wire. Remove the fake `SearchBox`/`TitleBar` statics and the 140px pad. *(~60 LOC net.)*
13. **`src/FluentGpu.WindowsApp/ShotScene.cs`** — add `"titlebar"` id rendering the gallery's titlebar over Mica (or the whole gallery — reuse `"gallery"`); add a focused/active and an inactive variant. *(~10 LOC.)*
14. **`src/FluentGpu.VerticalSlice/Program.cs`** — add `TitleBarChecks(strings)` golden checks (§7) and call it from the suite. *(~70 LOC. Depends on steps 2/8/9.)*

Order rationale: 1→2 keep both PAL backends compiling; 3–6 are additive leaves; 7 wires host using only added members; 8→9 build the control; **10 is the heavy Win32 step but is fully gated by `_customFrame`** so it can't regress the standard-frame path; 11–12 flip the gallery on; 13–14 verify. At each step the solution compiles (every PAL/Hooks addition is defaulted/nullable) and the standard-frame path is untouched until step 11.

---

## 7. Verification plan

### 7.1 Headless golden checks — `TitleBarChecks(strings)` in `VerticalSlice/Program.cs` (`Check(name, ok, detail)` idiom)

Build via the existing pattern: a real `AppHost` over `HeadlessPlatformApp`/`HeadlessWindow`/`HeadlessGpuDevice` (the `ClickNode`/`CenterOf`/`FindRole`/`Child` helpers exist at 1395–1548), `CustomFrame:true` in the `WindowDesc`.

| Check name | Asserts |
|---|---|
| `TB.1 caption buttons sized` | The 3 caption-button nodes are 46 wide × 48 tall (`AbsoluteRect`). |
| `TB.2 back/pane sized` | When `ShowBackButton`/`ShowPaneToggle`, those nodes are 40 wide. |
| `TB.3 title typography` | Title glyph run renders at Size 12, color `Tok.TextPrimary` (active) via `GlyphColor`. |
| `TB.4 regions reported` | After a frame, `HeadlessWindow.LastTitleBarRegions` contains: a `Caption` rect == the bar root rect, a `MinButton`/`MaxButton`/`CloseButton` rect each, and a `Client` rect for the content column — in island-before-caption order. |
| `TB.5 minimize click` | `ClickNode(min)` → `HeadlessWindow.MinimizeCount == 1`. |
| `TB.6 maximize toggles + reglyphs` | `ClickNode(max)` → `ToggleMaximizeCount == 1`, `State == Maximized`, and after the re-render the max glyph node resolves to `Icons.ChromeRestore` (`HasGlyph`). |
| `TB.7 close click` | `ClickNode(close)` → `CloseCount == 1`. |
| `TB.8 hover ramp (close red)` | Synthetic PointerMove over close → after `RunFrame`, the close box `Paint().Fill` eases toward `Tok.CaptionCloseHover` (compare A/RGB with tolerance, mid-ramp at 83ms). |
| `TB.9 activation dim` | Set `HeadlessWindow.IsActive=false` (emits WindowBlur+bumps activation) → re-render → title glyph color == `Tok.TextTertiary`, icon/content wrapper `Paint().Opacity == 0.5`, caption glyph color == `Tok.TextDisabled`. |
| `TB.10 standard frame unaffected` | A `CustomFrame:false` window reports NO regions (`LastTitleBarRegions` empty) and the control with `ShowCaptionButtons:false` emits a right-pad instead of caption buttons. |

These reuse `ColorClose`, `Near`, `GlyphColor`, `HasGlyph`, `CountGlyph`, `FindRole`/`Roles`, `CenterOf`, `ClickNode` verbatim.

### 7.2 `--shot` scenes (GPU pixels, `WindowsApp`)

- `dotnet run --project src/FluentGpu.WindowsApp -- --screenshot out\titlebar.png --shot titlebar --mica --frames 8 --w 1240 --h 820`
  — diff: bar height 48, caption buttons right-aligned, title at 12px, accent icon, centered search pill, **close-button red on hover** (the shot scene can force a hovered close by pre-pressing or by a variant that paints the close box with `Fill=Tok.CaptionCloseHover` to validate the red literal independent of hover timing — per control-fidelity.md §4 "slow the hover ms to catch mid-animation").
- Add an **inactive** variant (`titlebar-inactive`): renders with the activation signal forced inactive → title tertiary, search/icon at 0.5 opacity, caption glyphs disabled-gray. Diff against a deactivated WinUI gallery window.
- Regression: `--shot gallery --mica` (the whole gallery) must still render; the titlebar replaces the fake strip with no layout shift of the NavigationView below.

### 7.3 Manual smoke list (real window, user runs the app — MEMORY user-runs-app-themselves)

1. **Snap flyout**: hover the maximize button (or Win+Z) → Win11 snap-layouts flyout appears (proves HTMAXBUTTON).
2. **Drag**: press-drag the bar (empty area / min-drag strip) → window moves; drag to a screen edge → Win11 drag-to-snap.
3. **Double-click**: double-click the drag band → maximize; again → restore (DefWindowProc on HTCAPTION).
4. **System menu**: right-click the drag band → system menu with correct enable/disable (Restore enabled only when maximized; Move/Size/Maximize disabled when maximized); Alt+Space also opens it.
5. **Caption hover/press**: min/max/close show the engine StateBrush hover (gray; close = red) and pressed states via the synthesized NC pointer path; clicking each performs the action; max re-glyphs to restore when maximized.
6. **Deactivated dim**: click another window → title goes tertiary-gray, icon + search fade to 50%, caption glyphs gray (engine activation path); click back → restores.
7. **DPI hop**: drag the window between a 100% and a 150% monitor → caption buttons and bands stay correctly sized and hit-test correctly (regions re-reported on relayout).
8. **Resize edges + rounded corners + shadow**: window resizes from all edges; Win11 rounded corners + drop shadow present (DWM frame intact via the kept thin L/R/B inset).

---

## 8. Open questions (only materially-blocking; decided where possible)

1. **Caption glyph + close-red literals are LOW-confidence externals** (E921/E922/E923/E8BB; `#C42B1C`). Decided: ship them (Win11-accurate, the values the integration dossier enumerates), centralized in `Icons.cs`/`Tok` so the `--shot titlebar` pixel diff can correct any single one in one edit. Not blocking.
2. **Gallery back button has no back stack.** Decided: ship `ShowBackButton=false` in v1 (the FluentGpu gallery navigates by selection-remount, not a Frame; WinUI binds back to `rootFrame.CanGoBack` which we lack). Pane + search fully wired. Back is a clean follow-up once a `Navigator` is threaded into `NavigationView`. Not blocking the titlebar.
3. **`NavigationView.IsPaneOpen` input.** If `NavigationView` exposes no public pane-open toggle today, add a `Signal<bool> PaneOpen` input (the gallery owns it, `OnPaneToggle` flips it). This is the one spot that may touch `NavigationView.cs`; if it proves invasive, fall back to a no-op `OnPaneToggle` for v1. Prefer wiring it. (Minor, not blocking.)
4. **Autohide-taskbar maximized inset** (the `AutohideTaskbarSize=2` shrink): deferred to a follow-up; only matters for users with an autohide taskbar on the maximized monitor edge. Documented in §3.2a.

Everything else is decided in-spec.
