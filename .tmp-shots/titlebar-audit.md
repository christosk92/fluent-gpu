# Custom Titlebar / Win32-DWM Defect Ledger

Synthesis of three audits (win32-dwm-protocol, engine-interplay, winui-parity-ux) against the as-built working tree on `feat/winui-control-parity`. All file:line citations were re-verified against the live files. Nothing was REFUTED; no severities were downgraded by synthesis. Findings are deduped (the `cyTopHeight=1` DWM seam and the `SetTitle` no-op were each raised by two auditors and are merged into single entries).

Order: **fix-now (small surgical)** → **fix-now (larger)** → **document-as-divergence** → **defer / no-change notes**.

---

## A. Fix now — small, surgical, confirmed defects

### A1. [HIGH] DWM custom-frame margin: `cyTopHeight=1` must be `0` for the Mica case
**Merged from:** `mica-cytopheight-1` (win32-dwm-protocol, high) + `dwm-1px-top-line` (winui-parity-ux, note). Same root cause. The win32-dwm auditor's analysis (this is Terminal's *borderless* value, not its Mica value) governs the severity — treat as HIGH, not note.

**File:** `src/FluentGpu.Pal.Windows/Win32Theme.cs:81-86` (the `if (customFrame)` branch).

**Current code:**
```csharp
if (customFrame)
{
    // 1px top extension: keeps the DWM top border/shadow seam without re-summoning the system caption buttons.
    MARGINS m = new() { cyTopHeight = 1 };
    DwmExtendFrameIntoClientArea(hwnd, in m);
}
```

**Why it's wrong:** Windows Terminal's `_UpdateFrameMargins` sets `cyTopHeight = 1` only for the **borderless** case; for the Mica/transparent case it uses `(_useMica || _titlebarOpacity < 1.0) ? 0 : -frame.top` → **0**. This app is always Mica + composited (`Program.cs:144` forces `customFrame:true`; `FluentApp.cs:42` passes `mica:true`). The 1px non-zero top margin makes DWM own and paint a 1px caption-visual strip at the client top and shifts how the Win11 snap-layouts flyout anchors (it computes off the DWM-extended frame). The method's own comment (lines 71-73) already asserts the backdrop "still fills the whole window from `DWMWA_SYSTEMBACKDROP_TYPE` alone" — so the 1 is admittedly unnecessary.

**Change:** Pass an all-zero margin for the Mica custom-frame case (matching Terminal's `_useMica ? 0`):
```csharp
if (customFrame)
{
    // Mica fills the whole window from DWMWA_SYSTEMBACKDROP_TYPE=DWMSBT_MAINWINDOW alone; the top frame margin must be 0
    // for the composited/Mica case (Terminal's `_useMica ? 0`). Non-zero lets DWM paint a 1px caption strip and re-anchors the snap flyout.
    MARGINS m = new();   // {0,0,0,0}
    DwmExtendFrameIntoClientArea(hwnd, in m);
}
```
Leave the WM_NCCALCSIZE caption-strip recipe (Win32Platform.cs) untouched. Update the comment block at lines 69-73 to drop the "1px top sliver" rationale.

---

### A2. [HIGH] WM_NCLBUTTONDBLCLK on caption buttons double-fires through DefWindowProc
**Source:** `nclbuttondblclk-unhandled` (win32-dwm-protocol, high). Verified: the constants block (Win32Platform.cs:101-103) declares `WM_NCLBUTTONDOWN=0x00A1` and `WM_NCLBUTTONUP=0x00A2` but **not** `0x00A3`, and no `case` for it exists.

**File:** `src/FluentGpu.Pal.Windows/Win32Platform.cs` — constants at line 102-103, and a new case adjacent to WM_NCLBUTTONDOWN (lines 594-603).

**Why it's wrong:** A real double-click on the maximize button yields `NCLBUTTONDOWN → NCLBUTTONUP → NCLBUTTONDBLCLK → NCLBUTTONUP`. The DBLCLK has no case, so it falls through to `DefWindowProcW` at line 409/662 with `wParam=HTMAXBUTTON`, which posts a **second** `WM_SYSCOMMAND` (SC_MAXIMIZE/SC_RESTORE) unsynchronized with the engine glyph + region re-report. Net: the window toggles twice under a normal fast double-click; on HTCLOSE a second SC_CLOSE can post. Terminal explicitly eats WM_NCLBUTTONDBLCLK for HTMIN/HTMAX/HTCLOSE (returns 0).

**Change:**
1. Add to the constants (line 102-103, the WM_NC group):
   ```csharp
   WM_NCLBUTTONDBLCLK = 0x00A3,
   ```
2. Add a case identical to the WM_NCLBUTTONDOWN handler (eat it for engine buttons; defer to DefWindowProc for the drag band so double-click-to-maximize on HTCAPTION still works):
   ```csharp
   case WM_NCLBUTTONDBLCLK when _customFrame:
   {
       TitleBarHit ncHit = NcHitFromCode((long)(nuint)wParam);
       if (ncHit == TitleBarHit.Client) return false;   // HTCAPTION/resize → DefWindowProc keeps double-click-to-maximize
       _ncPress = ncHit;
       _queue.Enqueue(new InputEvent(InputKind.PointerDown, NcCenterDip(ncHit), 0, 0, Mods: Mods(), TimestampMs: Now()));
       PaintRequested?.Invoke();
       result = 0;
       return true;   // eat: never let DefWindowProc fire a second SYSCOMMAND on the engine buttons
   }
   ```
   This decomposes a double-click into down/up/down/up on the engine button (two clean engine clicks) and never reaches DefWindowProc for Min/Max/Close.

---

### A3. [HIGH] Maximized top edge (~8px) is dead for caption buttons + drag band (Fitts)
**Source:** `nchittest-maximized-fitts` (win32-dwm-protocol, high). Verified against WM_NCCALCSIZE (lines 517-523) and WM_NCHITTEST (lines 534-549).

**File:** `src/FluentGpu.Pal.Windows/Win32Platform.cs`, WM_NCHITTEST case, right after `ScreenToClient` (line 535).

**Why it's wrong:** When maximized, NCCALCSIZE insets the client top by `SM_CXPADDEDBORDER + SM_CYSIZEFRAME` (~8px @100%, line 522), so physical screen-top maps to client `pt.y ≈ -8`. `HitTestRegions` rejects any region with `py < rc.Y * s` (line 311) — the engine buttons/bar are reported at DIP y=0 — so `-8 < 0` ⇒ every region misses ⇒ returns 0 ⇒ DefWindowProc ⇒ HTCLIENT/HTNOWHERE, never HTCLOSE/HTMAX. The resize-band branch is also gated `!IsZoomed && pt.y >= 0` (line 538), so it's skipped too. The top ~8px strip — exactly the Fitts slam-zone — is dead when maximized.

**Change:** Clamp client y up to 0 when maximized, before the region tests, so the inset top rows fold onto the first hittable bar row:
```csharp
POINT pt = new() { x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF) };
ScreenToClient(hWnd, &pt);
if (IsZoomed(hWnd) && pt.y < 0) pt.y = 0;   // Fitts: fold the maximized top inset onto the first hittable row
int button = HitTestRegions(pt.x, pt.y, buttonsOnly: true);
...
```
Both the buttons-only pass and the Caption catch-all pass then resolve at y=0. (The resize band stays correctly skipped when zoomed — a maximized window doesn't top-resize.)

---

### A4. [LOW] System-menu right-click bolds Close as the default item (shell divergence + accidental-close)
**Source:** `ncrbuttonup-defaultitem-mismatch` (win32-dwm-protocol, low). Verified at line 632.

**File:** `src/FluentGpu.Pal.Windows/Win32Platform.cs:632` (inside the WM_NCRBUTTONUP handler).

**Why it's wrong:** `SetMenuDefaultItem(sys, SC_CLOSE, 0)` bolds Close as the default. The Win11 shell **right-click** caption menu has no default item (only the system-menu *double-click* path defaults to Close). Bolding both diverges visually and means a fast double right-click / Enter-after-open invokes Close.

**Change:** Delete line 632 (`SetMenuDefaultItem(sys, SC_CLOSE, 0);`) so the menu has no default item. Optional polish (same finding): add `TPM_RIGHTBUTTON` to the `TrackPopupMenu` flags at line 633 so right-button-up over an item selects it.

---

### A5. [LOW] NC press leaves `_ncPress` stuck when the release lands in the client area
**Source:** `ncpress-stuck-no-capture` (engine-interplay, low). Verified: WM_MOUSEMOVE (lines 455-458) clears `_ncInside` but not `_ncPress`; the WM_NCMOUSELEAVE cancel is guarded by `if (_ncInside)` (line 580), which is already false by then.

**File:** `src/FluentGpu.Pal.Windows/Win32Platform.cs`, WM_MOUSEMOVE handler (lines 455-458).

**Why it's wrong:** Press a caption button (NC, no SetCapture on the NC path), then drag held into the client and release there. The OS routes the move/up to the client; `_ncPress` is never cleared (NCLBUTTONUP/NCMOUSELEAVE don't fire, and the leave-cancel is skipped because `_ncInside` was already cleared by the client move). `_ncPress` stays set until the next WM_NCLBUTTONDOWN overwrites it. **Benign today** (a stale value can't fire a spurious click — every click requires a fresh NCLBUTTONDOWN that resets `_ncPress` first), but it is a real state-hygiene leak vs the captured client path.

**Change (lower-risk option (a) from the finding):** In WM_MOUSEMOVE, when crossing NC→client while a NC press is held, synthesize the same offscreen cancel the leave path does:
```csharp
case WM_MOUSEMOVE:
    if (_ncPress != TitleBarHit.Client)   // held NC press dragged into the client → cancel it (no capture on the NC path)
    {
        _ncPress = TitleBarHit.Client;
        _queue.Enqueue(new InputEvent(InputKind.PointerUp, OffscreenDip, 0, 0, TimestampMs: Now()));
    }
    _ncInside = false; _ncHover = TitleBarHit.Client;
    _queue.Enqueue(new InputEvent(InputKind.PointerMove, MousePt(lp), 0, 0, Mods: Mods(), TimestampMs: Now()));
    return true;
```
(Alternative (b) — SetCapture on the synthetic NC press — is higher-risk; prefer (a).)

---

### A6. [MEDIUM] Back button shipped visible-but-disabled; WinUI collapses it
**Source:** `back-button-shown-disabled-not-hidden` (winui-parity-ux, medium). Verified at Gallery.cs:261-262 (comment at line 254 confirms "visible, disabled — no Frame back-stack yet").

**File:** `src/FluentGpu.VerticalSlice/Gallery.cs:261-262` (the TitleBar props).

**Why it's wrong:** `ShowBackButton = true, BackEnabled = false` renders a greyed back chevron at launch. The real gallery binds `IsBackButtonVisible="{x:Bind rootFrame.CanGoBack}"` — at launch `CanGoBack=false`, so **no** back button shows; WinUI never shows a disabled back button. The titlebar spec §5.2/Open-Q 2 decided `ShowBackButton=false` for v1.

**Change:** Set `ShowBackButton = false` in Gallery.cs (drop the `BackEnabled` line or leave it harmless). This matches both WinUI's launch state and the spec's v1 decision. (Full fix — wire a Navigator and bind `ShowBackButton` to `CanGoBack` — is future work; the one-line v1 fix is what's in scope here.)

---

## B. Fix now — larger but confirmed (still this pass if budget allows)

### B1. [MEDIUM] WM_SIZE writes `_w/_h` on SIZE_MINIMIZED (0×0) → 0×0 swapchain resize + relayout churn
**Source:** `wm-size-minimized-corrupts-wh` (win32-dwm-protocol, medium). Verified at lines 421-435; AppHost.cs `EnsureSize` (≈892-905) drives `_swapchain.Resize((0,0))` + `_needFullLayout` on the changed size.

**File:** `src/FluentGpu.Pal.Windows/Win32Platform.cs:421-435` (WM_SIZE).

**Why it's wrong:** No `wParam` check. On minimize the OS sends WM_SIZE with width=height=0, so `_w=_h=0`; `ClientSizePx` returns (0,0); the host then resizes the swapchain to 0×0 and runs a full layout at 0×0 DIP — wasted/degenerate work, and a 0-dimension swapchain Resize can assert/clamp on D3D12. Secondary symptom (verifier-confirmed): the `_wasZoomed` edge-detect (lines 427-432) fires on a maximized→minimize transition (`IsZoomed` is false while iconic) and enqueues a spurious WindowStateChanged.

**Change:** Guard the body on SIZE_MINIMIZED — ignore it entirely (don't update `_w/_h`, don't run the zoom edge-detect, don't paint):
```csharp
case WM_SIZE:
    if ((nuint)wParam == SIZE_MINIMIZED) return true;   // 0×0 iconic size — don't churn the swapchain/layout
    _w = (int)(lp & 0xFFFF); _h = (int)((lp >> 16) & 0xFFFF);
    if (_customFrame)
    {
        bool zoomedNow = IsZoomed(hWnd);
        if (zoomedNow != _wasZoomed) { _wasZoomed = zoomedNow; _queue.Enqueue(new InputEvent(InputKind.WindowStateChanged, default, 0, 0, TimestampMs: Now())); }
    }
    PaintRequested?.Invoke();
    return true;
```
Add the constant: `SIZE_MINIMIZED = 1` (alongside the WM_SIZE-related consts). It is "larger" only because it touches the resize edge that the host layout/swapchain path consumes — verify a minimize→restore cycle still produces a correct restored frame (it will; restore sends a fresh WM_SIZE with real dims).

---

### B2. [MEDIUM] Back/pane buttons render 40×40 centered instead of WinUI's 40w × 44h @ y=2
**Source:** `back-pane-fill-height-mismatch` (winui-parity-ux, medium). Verified: `IconButton.Style` has no `Height` field (record at IconButton.cs:28-47); `IconButton.Create` sets root `Width = s.Size, Height = s.Size` (line 88); nav root centers in the 48px bar (TitleBar.cs:222 `AlignItems=Center`).

**Files:** `src/FluentGpu.Controls/IconButton.cs` (Style record + Create) and `src/FluentGpu.Controls/TitleBar.cs` (navStyle + nav-button wrap, lines 106-135).

**Why it's wrong:** At rest both are transparent (no diff), but on hover/press WinUI paints a 40×44 SubtleFill backplate at y=2..46 (`Width=40, VerticalAlignment=Stretch, Margin=2` in the 48px bar), whereas ours paints a centered 40×40 backplate at y=4..44 — 8px shorter, 2px lower. The spec (titlebar-spec.md:487) accepted the 40×40 shortcut, but it is a real, pixel-visible WinUI divergence.

**Change (non-breaking — add an optional `Height`):**
1. In `IconButton.Style` add a nullable height that defaults to the square size:
   ```csharp
   public float? Height { get; init; }   // null ⇒ square (= Size); set for stretched nav buttons (WinUI back/pane = 44h)
   ```
2. In `IconButton.Create`, use it for the root:
   ```csharp
   Width = s.Size, Height = s.Height ?? s.Size, ...
   ```
   (All existing call sites are unchanged because `Height` defaults to null ⇒ Size.)
3. In `TitleBar.cs:106-110`, build the nav style at 40×44 and give each nav button a 2px top/bottom margin so it sits at y=2 (matching WinUI's `Margin=2`):
   ```csharp
   var navStyle = IconButton.DefaultStyle with
   {
       Size = NavButtonSize,          // 40 width
       Height = 44f,                  // WinUI back/pane Stretch in the 48px bar = 44h @ y=2
       Foreground = active ? Tok.TextPrimary : Tok.TextTertiary,
   };
   ```
   Wrap each nav button (back at TitleBar.cs:114-123, pane at 126-135) with a 2px top/bottom margin (e.g. `Margin = new Edges4(0, 2, 0, 2)` on the applied BoxEl, or a y=2 offset) so the 44h backplate lands at y=2..46.

**Coupled with A6:** since the back button is being collapsed (`ShowBackButton=false`), only the **pane** (hamburger) button is live at launch — but apply the height fix to the shared `navStyle` so both are correct whenever shown.

---

## C. Document as divergence (notes — spec-acknowledged, no code change required this pass)

These are real differences from WinUI but were deliberately deferred in titlebar-spec.md. Record them in the spec's known-divergence / known-pixels list; do not silently "fix" without a scope decision.

- **C1. [LOW] Per-button nav margins off by 2px** (`missing-nav-button-margins`, winui-parity-ux). TitleBar applies only the root left padding of 2 (TitleBar.cs:223) and a single 14px `LeftHeaderPad` spacer (line 137), with no per-button `Margin=2`. Net: the left cluster sits ~2px left and the pane→icon gap is 14 vs WinUI's 16. **If B2's 2px nav-button wrap is implemented**, fold the horizontal side of it in too (2px before back, and make pane→icon total 16 = 2px button margin + 14px header pad) and this is closed for free; otherwise leave as a documented 2px offset.

- **C2. [LOW] No Compact (32px) mode** (`no-compact-mode`, winui-parity-ux). `CompactHeight=32f` is defined (TitleBar.cs:50) but never used; the root always renders `ExpandedHeight=48` (line 222). When narrow, WinUI collapses title/subtitle and keeps a full-width search; we keep the title and shrink/collapse the search (Gallery.cs:267-268). Spec §2.1 reserves CompactHeight but defers the trigger. Documented divergence.

- **C3. [NOTE] 1px DWM top seam** — covered by **A1**: once `cyTopHeight` becomes 0, the seam is gone; no separate note needed after A1 lands.

- **C4. [NOTE] App-identity icon is an accent grid glyph, not the app-tile image** (`app-icon-accent-glyph-not-image`, winui-parity-ux). `IconGlyph=Icons.Grid` tinted `AccentDefault` (Gallery.cs:259-260) vs WinUI's `ImageIconSource` (GalleryIcon.ico). Spec §2.3/§5.3 accepted divergence (no image-icon path in the titlebar). Defer.

- **C5. [NOTE] TitleBar plain-field props frozen at mount** (`titlebar-props-frozen-at-mount`, engine-interplay). Reconciler early-returns on ComponentEl reuse (Reconciler.cs:215-223) without re-applying `Title/IconGlyph/Show*/On*`/`Content` (TitleBar.cs:60-79 are plain fields set once in the factory). Not reachable in the current gallery (every prop is a constant or a closure over a stable signal). **Action:** add a one-line XML-doc on the TitleBar public fields stating they are mount-time config and reactive values must flow via signals/context. No behavior change.

---

## D. Defer (low value, future work, or larger model change)

- **D1. [LOW] `SetTitle` is a no-op → taskbar/Alt-Tab title frozen + mismatched** (merged: `settitle-noop-taskbar` win32-dwm + `settitle-noop-title-mismatch` winui-parity-ux). `Win32Window.SetTitle(StringId)` is empty (Win32Platform.cs:274); the OS title is set once at CreateWindowExW from `desc.Title` = "FluentGpu — Capability Gallery" (Program.cs:143), while the engine-drawn bar reads "FluentGpu Gallery" (Gallery.cs:258). With the caption stripped, the OS title is the only place the taskbar/Alt-Tab label comes from, and it can't be updated at runtime. **Two-part fix when scheduled:** (1) implement `SetTitle` via a `SetWindowTextW` P/Invoke (resolve the StringId to UTF-16 and call `SetWindowTextW(_hwnd, p)` — requires threading the StringTable/string to the window); (2) make the two strings identical (set `TitleBar.Title` to the same string passed to `FluentApp.Run`). Low impact for v1 (static gallery title); spec §3.1 Gap #9. Defer.

- **D2. [LOW] No WM_GETMINMAXINFO** (`no-getminmaxinfo`, win32-dwm). The handler is absent; the window is plain `WS_OVERLAPPEDWINDOW` (line 185) so the default min track size is small and basic snap works. Spec §3.2(f) mandates lowering `ptMinTrackSize` to ~500×330 epx for Win11 snap into narrow zones on small/high-DPI monitors. **Fix when scheduled:** add `case WM_GETMINMAXINFO when _customFrame:` that reads `MINMAXINFO* mmi = (MINMAXINFO*)lParam` and **only lowers** `ptMinTrackSize.x/.y` to `(int)(500*_scale)`/`(int)(330*_scale)` when they currently exceed those. Low because the default already permits basic snap. Defer.

- **D3. [LOW] First-frame search-box overshoot** (`first-frame-search-overshoot`, engine-interplay). `_availDip` seeds `float.PositiveInfinity` (TitleBar.cs:89), so frame 1 measures the search box at its 580 max regardless of window width; the measured-width feedback (PushRegions → UseLayoutEffect) corrects it on frame 2. Masked by root `ClipToBounds=true` when narrow; a one-frame width pop on cold start / re-mount. **Fix when scheduled:** seed `_availDip` with a finite estimate (e.g. `0f` so Content collapses then expands, or a small default), or have Content treat +inf as "collapsed/natural" rather than "use the 580 max". One-frame cosmetic; defer.

- **D4. [LOW] Top resize band eats the island's top sliver** (`top-resize-band-eats-island-top-sliver`, engine-interplay). In WM_NCHITTEST the top resize band (lines 538-547) is tested before the interactive islands (line 548). Caption buttons get a Fitts exemption (checked first, line 536) but the islands do not, so the top `rb` physical px of the search-box island return HTTOP. At 150% DPI `rb≈12px` and the centered ~32px search box's top edge is ~12px, so its uppermost sliver shows the N-S resize cursor / starts a resize. **Fix when scheduled:** run the `HitTestRegions(..., buttonsOnly:false)` island/Client check (or at least island rects) **before** the top-resize-band block, mirroring the button Fitts exemption — so an island claims HTCLIENT even in the top resize strip. Narrow trigger (top 1-3px of one island at high DPI, non-maximized); defer.

- **D5. [LOW] No autohide-taskbar gutter on maximize** (`nccalcsize-no-autohide-gutter`, win32-dwm, medium-flagged but spec-deferred). The maximized NCCALCSIZE inset (lines 517-523) adds only `SM_CXPADDEDBORDER + SM_CYSIZEFRAME` to the top; no `ABM_GETSTATE`/`ABM_GETAUTOHIDEBAREX` query and no 2px edge gutter. A maximized window covers an autohide taskbar and blocks its edge reveal. Spec titlebar-spec.md §3.2a + Open-Q #4 explicitly deferred this. **Fix when scheduled:** in the maximized branch, `SHAppBarMessage(ABM_GETSTATE)`; if `ABS_AUTOHIDE`, for each docked edge (via `ABM_GETAUTOHIDEBAREX` on the window's monitor) shrink that edge of `rgrc[0]` by 2px (Terminal's `AutohideTaskbarSize`). Listed in D rather than B because it is a documented spec deferral and needs the SHAppBar plumbing; promote to B if autohide-taskbar parity is in scope.

- **D6. [NOTE] Redundant Paint on NC mouse messages** (`ncmousemove-paint-redundant`, engine-interplay). Each WM_NC* mouse case calls `PaintRequested?.Invoke()` (lines 572/590/600/614) → `Paint(0)`, which renders the pre-dispatch state because the synthetic InputEvent is only drained at the next `PumpInto`. During normal (non-modal) operation that's one wasted full Paint per NC mouse message (the correct frame still lands via RunFrame). The call **is** needed to keep the window live during the OS modal move/size loop. **Optional cleanup:** gate the NC-case `PaintRequested` to only fire when inside a modal loop (or drop it from the NC mouse cases and keep it in WM_SIZE/WM_PAINT/WM_DPICHANGED). Pure perf; no correctness impact. Defer.

- **D7. [NOTE] No Ctrl+F search accelerator** (`no-ctrl-f-search-accelerator`, winui-parity-ux, medium-flagged). WinUI focuses the titlebar search on Ctrl+F; the AutoSuggestBox here (Gallery.cs:267-275) registers no accelerator and `GalleryApp.Render` has no global key hook. **Fix when scheduled:** register a global key hook (InputHooks.KeyPreview / top-level OnKeyDown) in GalleryApp that, on Ctrl+F, focuses the AutoSuggestBox node (capture via OnRealized → InputHooks FocusNode); set no hint tooltip. A real gallery-parity gap but a self-contained feature add — defer to a gallery-parity pass.

- **D8. [NOTE] QuerySubmit on no match silently no-ops** (`querysubmit-no-search-results-page`, winui-parity-ux). `NavigateToTitle` returns on no match (Gallery.cs:244 `if (key is null) return;`); WinUI always navigates (to a `SearchResultsPage` on no exact match). No results page exists here. Future gallery work; defer.

---

## E. Verified-correct — explicitly NOT defects (do not re-litigate)

These were investigated and confirmed sound as-built; recorded so they aren't re-opened:

- **`ncmouseleave-rearm-hover-gap`** (win32-dwm, note): TME_NONCLIENT re-arm on the next NCMOUSEMOVE is correct — the one-message gap coincides exactly with the pointer being outside the NC area (no hover to maintain). No fix.
- **`wm-ncactivate-vs-wm-activate-double`** (win32-dwm, note): as-built, only WM_ACTIVATE enqueues focus/blur (lines 491-494); WM_NCACTIVATE (640-645) just sets `_active` and returns DefWindowProc with `lParam=-1` (correct — suppresses classic NC repaint). No double-enqueue. No fix.
- **`state-poststate-staleness`** (win32-dwm, note): `ToggleMaximize` PostMessages async (295-296) and `State` reads live (288-289), but the only consumer (TitleBar max↔restore glyph) re-reads on WindowStateChanged emitted from WM_SIZE *after* the OS applied the change. Benign. No fix.
- **`caption-text-ramps-verified-ok`** (winui-parity-ux, note): CaptionButton glyph hover/press/inactive color ramps DO apply through the renderer (Reconciler.cs:1344-1345 → SceneRecorder ResolveTextColorCore via the interactive-ancestor caption box); NC events aren't `_active`-gated so inactive-window hover works (matches WinUI). No fix.
- **`autosuggest-popup-not-clipped-verified`** (winui-parity-ux, note): the suggestion list opens via the overlay service (`svc.Open(...)`, BottomStretch) and the tree is wrapped in OverlayHost (Gallery.cs:292), so the bar's `ClipToBounds=true` (TitleBar.cs:224) does not clip it. No fix.

(No findings were UNVERIFIABLE; none REFUTED. The engine-interplay auditor's broader "verified-correct" matrix — synthetic state×message lifecycle, center-point resize-safety, measured-width convergence, activation/dimming same-frame, handle stability, zero per-frame alloc — is corroborating context, not action items.)

---

## F. Verification

The fixes split into two classes: those exercisable by the headless slice/harness, and non-client (NC) behaviors that require a real HWND + DWM and must be smoke-tested manually (the user runs the app — verify logic via the harness, do not auto-launch WindowsApp).

**Headless / build gates (run after the edits):**
- **Canon drift gate** (any doc edits — titlebar-spec known-divergence updates for C/D items): `powershell -File design\check-canon.ps1` (exit 0 = clean).
- **Headless slice + alloc tripwire:** run the existing headless harness (`Pal.Headless`/`Rhi.Headless` slice) to confirm the TitleBar still mounts, regions still push, and **0 per-frame managed alloc on phases 6-13** holds. Specifically guards:
  - **B2 / C1** (IconButton `Height` field, navStyle 40×44, nav-button margins): the headless layout/region snapshot proves the nav buttons now arrange at 40×44 @ y=2 and the pushed NC region rects/center points (NcCenterDip) shift accordingly — assert against the recorded region table.
  - **A6** (`ShowBackButton=false`): headless region/render snapshot shows the back island is gone (only pane + icon + title remain) and `_back` is never realized.
  - **A5 / B1** logic paths don't regress the synthetic-input/state machine the headless input harness drives.
- **COM-leak gate** (if part of the standard suite): A1/B1 touch DWM/swapchain edges — confirm no new COM leak on minimize/restore and material re-apply.

**Manual smoke (NC — cannot run headless; need a composited Mica HWND on Win11):**
- **A1** (cyTopHeight=0): launch the gallery (Mica on). Inspect the top 1px row of the bar — no DWM caption-visual seam. Hover Maximize to summon the Win11 snap-layouts flyout — anchors flush to the bar, not offset by a DWM-extended frame. Compare maximize/restore transitions.
- **A2** (WM_NCLBUTTONDBLCLK): rapidly double-click Maximize — window toggles **once**, not twice. Double-click Close — fires once (no second SC_CLOSE). Double-click the drag band (HTCAPTION) — still maximizes (DefWindowProc path preserved).
- **A3** (maximized Fitts): maximize, then slam the pointer into the extreme screen-top-right corner and click — Close fires; repeat for Max/Min and for top-edge drag. No ~8px dead strip.
- **A4** (system menu default): right-click the drag band — the context menu has **no** bold/default item; Enter-after-open does not close the window.
- **A5** (NC press hygiene): press-and-hold a caption button, drag into the client, release — no stuck `_ncPress` (next caption click behaves normally; press the same button again immediately and confirm one clean click).
- **B1** (minimize): minimize then restore — no 0×0 swapchain churn / no degenerate frame; restored frame is correct; (with a debug counter) no spurious WindowStateChanged on the maximized→minimize edge.
- **B2** visual: hover the hamburger — the gray backplate is full-height (40×44 @ y=2), flush near the bar edges, not a centered 40×40 with top/bottom gaps.
