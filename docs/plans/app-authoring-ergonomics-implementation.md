# App-authoring ergonomics — implementation plan

Branch `feat/overlay-window`. Source: friction found building a small desktop overlay app (a borderless, topmost
usage widget) on the engine. Step 0 (overlay window shape) already landed on this branch; this plan covers the ten
follow-ups. Each section: the API, the mechanism, the edge cases, the gate.

Ground rules for every item: zero managed allocation in paint phases 6–13 at steady state (buffers grow, never
churn); reduced to the existing seams (no new runtime per feature); warnings are errors; every item gets a
VerticalSlice gate or a unit test; usage docs in `docs/guide/`, not `docs/design/`.

---

## 0 · Overlay window shape (DONE on this branch)

`WindowDesc.{Topmost,ToolWindow,Resizable}`, `AppOptions.{Topmost,ToolWindow,Resizable,Acrylic}`,
`FluentApp.{SetTopmost,Topmost,SetWindowMaterialAcrylic,WindowMaterialAcrylic,WindowBoundsPx,MoveWindowPx,ResizeWindow}`,
`Win32Theme.ApplyRoundCorners`. A custom-frame tool window drops `WS_SYSMENU` (DWM otherwise paints its small close
button over the reclaimed caption). Non-resizable = sizing hits → `HTBORDER`, no maximize box, thick frame kept.
Remaining: headless `HeadlessWindow` records the three flags (assertable), a gate that `AppOptions` maps onto
`WindowDesc` (owned by §2's agent since it touches the headless window).

---

## 1 · Declarative window drag regions (`BoxEl.WindowDrag`)

**Problem.** Making content draggable means copying `TitleBar`'s internals: `OnRealized` handle capture, a
`UseLayoutEffect` keyed on the viewport, a `TitleBarRegion[]` in priority order, `InputHooks.SetTitleBarRegions`.
Effect-keyed pushes go stale on scroll and self-resizing islands (`gate.titlebar.regions.content-version` exists
because of it). The platform keeps ONE list per window, so a second pusher overwrites `TitleBar`. Right-click on a
caption region always opens the system menu, so an app cannot put its own context menu on a drag surface.

**API.**
```csharp
public enum WindowDragRole : byte { None = 0, Drag = 1, NoDrag = 2 }
BoxEl.WindowDrag { get; init; }          // cold prop
el.WindowDragArea()   el.NoWindowDrag()  // Modifiers.cs sugar
```
`Drag` = this box's visible rect moves the window (HTCAPTION: OS move loop, double-click maximize when resizable,
snap). `NoDrag` = an interactive island inside a drag area stays client (buttons, text boxes). A node with
`OnContextRequested` (or a `ContextBit` ancestor) under a `Drag` area gets the app context menu on right-click
instead of the system menu; with no handler the system menu still opens (fallback).

**Mechanism.**
- Reconciler writes the role to a sparse SceneStore side table (`_windowDrag: Dictionary<int, WindowDragRole>` +
  `WindowDragVersion`), freed in `FreeSubtreeCore` like `_dropTargets`.
- Host collector at phase **7.8** (after `PoseScrollUi`, beside `SyncSpotlightBeforeRecord`) — gate: registry empty
  and nothing pushed last frame ⇒ zero work. For each entry that is `IsHitReachable` (excludes KeepAlive-parked
  pages, exit orphans, collapsed ancestors): rect = clip-aware visible rect (`EffectiveVisibleRect`-equivalent:
  every ancestor clip, translation + scroll pose; scale/rotate collapse to the bounding box), intersected with the
  client rect; empty ⇒ skipped. Nodes rendered into popup windows are skipped (out-of-window coordinates).
- Composition with app pushes (`TitleBar`, Wavee pop-out): the host stores the app list separately and pushes
  `[app non-Caption entries (islands, buttons)] + [engine NoDrag as Client] + [engine Drag as Caption] + [app
  Caption entries]` — caption buttons always win, TitleBar's catch-all band never shadows an engine island.
  Pushed only when the merged list differs from the last push (element-wise compare into a reused buffer).
- `TitleBarRegion` gains `TitleBarRegionFlags Flags = None` (`AppContextMenu`). Engine `Drag` entries set it.
  `WM_NCRBUTTONUP` over a flagged caption region queues a synthetic right PointerDown/PointerUp pair (one pointer id,
  the NC synthetic slot) at the click point and returns handled; `InputDispatcher` raises the nearest
  `OnContextRequested`; when nothing handles it, it calls a new `IPlatformWindow.ShowSystemMenu(Point2 screenPx)`
  (default no-op; Win32 = today's `TrackPopupMenu` code moved into a method). Keyboard Alt+Space untouched.

**Edge cases.** Needs `CustomFrame` (standard frame: DEBUG one-time diagnostic, no-op); fullscreen (no NC hit-test —
regions ignored, already the case); maximized (regions still valid); DPI hop (regions are DIP, scaled at hit-test);
detached child hosts collect their own; a NoDrag island outside any Drag area is harmless; a Drag box fully
scrolled out ⇒ no region; transient frame where a node is realized but not laid out ⇒ empty rect skipped; count cap
none (buffer grows, never shrinks).

**Gates** (`TitleBarSuite` or new `WindowDragSuite`): drag+island order; scroll moves/clips the region; parked page
excluded; coexists with `TitleBar` (buttons first, engine island before TitleBar's band); unchanged frames push
nothing (counter on `HeadlessWindow`); flag set on engine Drag regions; dispatcher falls back to `ShowSystemMenu`
when no handler (headless window records the call). Step-0 gate: `AppOptions` → `WindowDesc` flags recorded.

---

## 2 · Menus from buttons and small windows

**Problem.** `IOverlayService.Open` defaults to `ConstrainToRootBounds = true`, so a menu in a 340×300 window clips;
the host's "window only when it doesn't fit" policy (`NeedsPopupWindow`) already exists but only for unconstrained
opens. Opening a menu from an icon button needs a wrapper `BoxEl` for the anchor, a self-closing `OverlayHandle`
closure, toggle-on-reclick and reopen-during-fade handling — `DropDownButton` has all of it but only with a label.

**API.**
```csharp
IconButton.Menu(string glyph, Func<IReadOnlyList<MenuFlyoutItem>> items,
                FlyoutPlacement placement = FlyoutPlacement.BottomEdgeAlignedRight,
                string? toolTip = null, ControlSize size = ControlSize.Medium, bool isEnabled = true) : Element
PopupOptions.Menu   // static preset: windowed-when-escaping (ConstrainToRootBounds=false), light dismiss, focus trap
```
`IconButton.Menu` is a small Component (DropDownButton's logic without label/chevron): items factory runs at open
(live state), re-click closes, Down/F4/Enter/Space open with first-item focus, reopen during the close fade is safe,
`aria` role = button with `HasPopup`. Doc on `IOverlayService.Open`: menus should pass `PopupOptions.Menu`.

**Edge cases.** Re-click while open (light-dismiss scrim eats the click ⇒ must not immediately reopen: compare the
closing cause, as DropDownButton does); disabled; items factory returns empty ⇒ nothing opens; window too small ⇒
windowed popup (existing policy); keyboard open focuses first item.

**Gates** (`ControlsSuite`): opens windowed when the host window is smaller than the menu; reclick toggles closed;
empty items ⇒ no overlay; keyboard open focuses first.

---

## 3 · Size the window to its content (`SizeToContent`)

**Problem.** An overlay app hand-computes DIP heights per layout; `ResizeWindow` uses the raw DPI scale, so under
app zoom N DIP of window gives N/zoom DIP of layout space.

**API.**
```csharp
public enum SizeToContent : byte { Manual = 0, Width = 1, Height = 2, WidthAndHeight = 3 }
AppOptions.SizeToContent { get; init; }          // default Manual
FluentApp.SizeToContent / SetSizeToContent(mode)
```
**Mechanism.** `FlexLayout.MeasureNatural(root, availW)` (public wrapper over `BeginMeasurePass` + `Measure`). Host
phase right after the flex solve + layout effects, only on frames where layout ran: natural width at +Inf (or the
current width for `Height`), natural height at that width; px = `ceil(dip × effective Scale)` (zoom included);
clamp to `MinClientSizeDip` and the monitor work area; apply when it differs from the client size by ≥ 1 px through
the platform window (a new `IPlatformWindow.SetClientSizePx` default member; Win32 = `ResizeClientPhysical`). The
resulting WM_SIZE lands on the next frame (re-entrant paint is guarded already).

**Edge cases.** Content that fills the viewport (Grow / Stretch / a non-content-sized ScrollView) measures to its
minimum — natural size ≤ 0 on an axis ⇒ that axis is left alone; oscillation guard (A→B→A inside 4 frames latches
until the content epoch changes); the user resizing the frame (WM_EXITSIZEMOVE with a size change) switches the mode
to `Manual` (WPF semantics); skipped while maximized, fullscreen, parked, or inside the modal size loop; the window
is shown only after the first fit when `SizeToContent != Manual` and `StartHidden` is false (no flash at the
AppOptions size); work-area clamp uses the monitor the window is on.

**Gates.** Headless: content-sized root resizes the window to the natural size; `Height` keeps width; zoom 1.25 ⇒
px = ceil(dip × scale); Grow-only content leaves the window alone; manual resize flips to Manual; no-op frames issue
no resize (counter).

---

## 4 · Text alignment and tabular figures (`TextEl.TextAlign`, `TextEl.FontFeatures`)

**Problem.** No horizontal text alignment (numbers right-aligned need a wrapper `BoxEl{Justify=End}`, which breaks
for wrapped/stretched text) and no OpenType features (digits jitter as values tick).

**API.**
```csharp
public enum TextAlign : byte { Start = 0, Center = 1, End = 2, Justify = 3 }
[Flags] public enum FontFeatures : uint { None = 0, TabularFigures = 1, LiningFigures = 2, OldStyleFigures = 4,
    SlashedZero = 8, StylisticSet01 = 1u << 8, /* … */ StylisticSet20 = 1u << 27 }
TextEl.TextAlign, TextEl.FontFeatures; SpanTextEl too  // cold props (TextCold)
```
**Mechanism.** Both enter `TextStyle` (the measure-cache key + `SameLayoutInput`). Alignment is applied inside
`TextLayoutEngine.WrapAndPosition` per line once widths are known: offset = (alignWidth − line width without
trailing hanging whitespace) × {0, ½, 1}; Justify distributes the slack across whitespace clusters on every line
except the last and hard-broken ones. **`alignWidth` is a new explicit argument** through `IFontSystem` /
`TextLayoutEngine.Layout` = the laid-out box width (render, hit-test, caret, range rects, span artifacts all pass the
same value; +Inf ⇒ widest line, so measure is unchanged). Features: `DWriteTextShaper.Shape` passes one
`DWRITE_TYPOGRAPHIC_FEATURES` range to both `GetGlyphs` and `GetGlyphPlacements`; the shape cache key, the glyph
run key, `DrawGlyphRunCmd`/`GradientCmd` and `LayoutRun` carry the packed features + align.

**Edge cases.** NoWrap text measured at +Inf but arranged wider (Grow/Width/stretch) — the core case; wrapped text
whose box is wider than its widest line; ellipsis trim (alignment of the trimmed line uses the trimmed width);
underline/strike bars follow the line offset; selection/caret/hit-test parity with render (all read the same laid
tables); RTL: no paragraph direction yet — Start=left, End=right (documented); headless font system implements the
same offsets (uniform advances) so goldens cover it; a font without `tnum` ⇒ no change (OpenType ignores it).

**Gates** (`TextSuite`): end-aligned NoWrap text in a wider box — glyph x and hit-test agree; center on a 2-line
wrapped run; justify widens inter-word gaps except the last line; trailing spaces do not shift end alignment;
`TextStyle` inequality on align/features (cache keys separate); headless measure unchanged by align.

---

## 5 · Polling resources (`ResourceOptions.RefetchIntervalMs`)

**Problem.** Live data needs `Task.Run` + `UsePost` + a hand-rolled "fetching" signal; `UseInterval` pauses while
the window is parked; `Resource.LastError` is not reactive.

**API** (`ResourceOptions` additions):
```csharp
public float RefetchIntervalMs { get; init; }          // 0 = off; measured from the previous completion
public bool  RefetchWhileInactive { get; init; }      // keep polling while minimized/hidden/occluded
public bool  RefetchOnActivate { get; init; } = true; // refetch on re-activation if a poll is overdue
public bool  BackoffOnError { get; init; } = true;    // interval × 2^n (cap 8×, jitter ±10%) after n failures
Resource<T>.Error : IReadSignal<Exception?>            // reactive last error (null after a success)
```
**Mechanism.** The `ResourceCell` owns a poll timer on the host timer queue (Gen-guarded like `IntervalCell`), armed
on every settle (success or error). A tick while a load is in flight re-arms without calling `Refresh()` (that would
cancel the in-flight load and starve slow loaders). Inactive (`UseIsActive` false) pauses it unless
`RefetchWhileInactive`; because host timers do not drain while parked, the inactive path uses a
`System.Threading.Timer` that posts the refresh (posts drain while parked). On activation, an overdue poll fires
immediately (deduped against in-flight).

**Edge cases.** Mutate re-arms from the mutate; deps change / unmount cancel the poll (Gen bump); options re-pointed
each render — changing the interval re-arms without forcing a fetch; `StaleTimeMs = 0` does not make
`RefetchOnActivate` always fire (gate on time since last settle); errors keep previous data (existing semantics);
headless deterministic clock drives the gate.

**Gates** (`HooksSuite`/resource suite): polls at the interval from completion; slow loader never cancelled by a
tick; parked pauses (default) and continues with `RefetchWhileInactive`; activation fires an overdue poll once;
backoff doubles and resets on success; `Error` signal flips.

---

## 6 · Tray icons from pixels or a glyph

**Problem.** `NotifyIcon.SetIcon` takes only an `.ico` path; a live badge (a percentage, an alert dot) needs
hand-generated icon files.

**API** (`FluentGpu.WindowsApi.Shell`):
```csharp
bool NotifyIcon.SetIcon(ReadOnlySpan<byte> premultipliedBgra, int width, int height)
void NotifyIcon.SetIconSource(Func<TrayIconRequest, TrayIconBitmap> render)  // re-run on DPI/theme change
TrayIconRequest(int SizePx, bool LightTaskbar)
TrayIconBitmap(byte[] PremultipliedBgra, int Width, int Height)
static TrayIconBitmap TrayIconArt.Glyph(TrayIconRequest r, string glyph, ColorF? ink = null,
    string? badge = null, ColorF? badgeFill = null, string? fontPath = null)
```
**Mechanism.** `CreateDIBSection` 32bpp top-down + 1bpp mask → `CreateIconIndirect` → swap via the existing
`LoadIcon` swap order (new handle, `Modify(NIF_ICON)`, then `DestroyIcon(prev)`); bitmaps deleted after creation.
`SetIconSource` stores the callback (clears `_iconPath`), renders at `SmallIconSizeForDpi(TaskbarDpi)` and re-renders
from `RefreshShellState` (DPI / theme / explorer restart). `TrayIconArt` rasterizes with DirectWrite
(`CreateGlyphRunAnalysis` → aliased alpha texture, the GlyphRenderer pattern) into coverage, tints premultiplied; the
badge is a rounded pill in the bottom-right with 1–3 characters of condensed digits; ink defaults to white on dark /
#1B1B1B on light taskbars.

**Edge cases.** Alpha-0 pixels must be colour 0 (premultiplied contract, validated in DEBUG); span length ≠ w×h×4 ⇒
`ArgumentException`; `Segoe Fluent Icons` missing (Windows 10) ⇒ `Segoe MDL2 Assets` fallback, then `fontPath`;
badge longer than 3 chars ⇒ trimmed; sizes 16–48 px; disposed icon ⇒ no-op; re-add after `TaskbarCreated` reuses
the current handle.

**Gates** (`FluentGpu.Windows.Tests`, pure helpers): coverage→premultiplied tint; badge layout rect per size;
size-per-DPI already covered; argument validation.

---

## 7 · App template + importable build props

**Problem.** A new app copies Wavee's `Directory.Build.props` (EngineRoot, analyzer switches,
`ShouldUnsetParentConfigurationAndPlatform`, CA1416). AOT publish on ARM64 fails when `vswhere` is not on PATH. No
`dotnet new` template. The NuGet package packs `build/nuget/README.md` + `icon.png`, which live in `ops/build/nuget/`.

**Deliverables.**
- `FluentGpu.App.props` + `FluentGpu.App.targets` at the repo root. Props: TFM/LangVersion/Nullable/ImplicitUsings/
  AllowUnsafeBlocks/InvariantGlobalization/Deterministic/GC posture/`ShouldUnsetParentConfigurationAndPlatform=false`/
  `NoWarn CA1416`/Debug analyzers off/`FLUENTGPU_DIAG` passthrough/`EngineRoot` default + missing-checkout error.
  Targets (after the csproj body, so `OutputType` is known): `PublishAot=true` for `WinExe`/`Exe` only; publish RID
  defaults to `$(NETCoreSdkRuntimeIdentifier)`; engine `ProjectReference`s (opt-out `FluentGpuReferenceWindowsApi`);
  SourceGen analyzer reference; an inline task before native link that prepends the VS Installer folder to the
  MSBuild process PATH when `vswhere.exe` is not resolvable (Windows only).
- `templates/fluentgpu-app/` — a `dotnet new` template (`.template.config/template.json`, short name
  `fluentgpu-app`, parameter `EngineRoot`): csproj, Directory.Build.props importing the props/targets, Program.cs,
  App.cs (Mica window, title bar, one page), `.gitignore`.
- Package: fix the README/icon paths; ship `buildTransitive/FluentGpu.props` with the consumer defaults that apply to
  a PackageReference (CA1416, analyzer switches).
- `docs/guide/new-app.md`: template, props, publishing (ARM64), the overlay recipe (§0–§6 together).

**Edge cases.** Consumer already sets a property ⇒ props use `Condition="'$(X)' == ''"`; class library importing
the props gets no PublishAot (NETSDK1207); `EngineRoot` without trailing slash normalised; x64-emulated shell on
ARM64 (RID from the SDK, never PROCESSOR_ARCHITECTURE).

**Gates.** `dotnet new install templates/fluentgpu-app` + `dotnet new fluentgpu-app` + `dotnet build` (scripted check
in `ops/` run by the orchestrator, not CI).

---

## 8 · Capture the composed window (`--fg shot-window`)

**Problem.** `--shot`/`--screenshot` read the swapchain back: Mica/acrylic, the DWM border and DComp children are
missing, so an agent cannot see what the user sees.

**API.** `--fg shot-window=PATH` (+ `--fg shot-delay=MS`, default 1500; `--fg shot-exit` closes the app after the
capture; a comma cannot be used inside a switch value — `ApplyList` splits on commas). Public:
`FluentApp.CaptureWindowPng(string path, int padPx = 0) : bool`.

**Mechanism.** `FluentGpu.Windows/Hosting/WindowCapture.cs`: `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`
(physical px, PMv2 process ⇒ no virtualisation) → screen DC `BitBlt(SRCCOPY | CAPTUREBLT)` into a 32bpp DIB → alpha
forced opaque → `PngWriter`. Before capture: if the window is not foreground-visible, raise it topmost for the grab
and restore the previous z-order. Triggered from the FluentApp frame loop after the first composited present +
delay.

**Edge cases.** Minimized/hidden ⇒ returns false with a stderr line; window partly off-screen ⇒ clipped to the
virtual screen; negative multi-monitor coordinates; occluded ⇒ the temporary topmost; directory created; overwrite.

**Gates.** Unit test for the rect math (bounds ∩ virtual screen, padding); manual: gallery `--fg shot-window=…` with
Mica shows the backdrop.

---

## 9 · Smaller sharp edges

- **AppDataStore**: `ForUnpackaged` rejects `\`/`/` and surrounding whitespace in publisher/product
  (`ArgumentException` naming the parameter — they silently nested keys and folders); `RegistryPath` property
  (`HKCU\Software\<pub>\<prod>\Settings`); doc comment names the `\Settings` subkey; `docs/guide/windows-integration.md`
  gets a storage section.
- **Glyph discoverability**: `GlyphTableGenerator` emits a real XML doc per glyph (`Segoe Fluent Icons "Name"
  (U+XXXX)`) and `Icons.All : ReadOnlySpan<(string Name, string Glyph)>`-style table (a static array of a small
  record struct — allocation once); the gallery Icons page's C# copy action emits `Icons.Name` when the glyph is in
  `glyphs.json`; `theming.md` count fixed.
- Generator tests extended.

---

## Order and ownership

Parallel lanes on disjoint files; shared big files (`Element.cs`, `Reconciler.cs`, `AppHost.cs`,
`Win32Platform.cs`, `FluentApp.cs`, `EngineSwitches.cs`) are edited region-locally — re-read before each edit.
Lanes: §1 drag regions (+ step-0 headless gate) · §2 menus · §3 size-to-content · §4 text · §5 resources · §6 tray +
§8 capture · §7 template/props/docs · §9 sharp edges. The orchestrator alone builds, runs VerticalSlice and the
tests, and fixes integration fallout; then the overlay sample (`ClaudeUsageWidget`) adopts every item as the
end-to-end proof.
