# Scroll rework — app migration guide (engine Wave 1 → Wavee)

Status: **engine Wave 1 landed in `fluent-gpu` (uncommitted working tree, 2026-09-23).** This guide is derived from
the actual engine diff (`git diff HEAD` over `src/FluentGpu.Engine`, `src/FluentGpu.Controls`, `src/FluentGpu.Windows`)
plus the new, untracked `src/FluentGpu.Engine/Scroll/{Motion,Runtime,Effects,Extent,Diag}` folders — not from the
plan. Plan: `docs/plans/scroll-rework-implementation.md`. Every section lists what was removed or renamed, the
replacement, and before/after code. Namespaces: `FluentGpu.Scroll.Runtime` (handle, moves, motion state),
`FluentGpu.Scroll.Effects` (scroll-linked effects), `FluentGpu.Scroll.Motion` (plans, feel profiles),
`FluentGpu.Scroll.Diag` (probe, tunables).

The model in one paragraph: every viewport owns ONE `ScrollHandle`. Every input (wheel, touchpad, touch, pen, thumb,
keys) and every programmatic move authors an immutable closed-form `ScrollPlan` (`p(t)` of absolute time) through the
handle. The UI thread evaluates the plan at the frame's predicted present time (realize window, hit-testing, signals);
the render thread re-poses the content at each compositor tick's predicted present time. Nothing integrates per frame,
nothing throttles realization, and the only thing an app ever touches is the handle (plus declarative effects).

---

## 1. `ScrollController` / `IScrollController` / `IWheelTarget` → `ScrollHandle`

Removed: `FluentGpu.Scroll.ScrollController`, `ScrollAnimate`, `ScrollGlide`, `IWheelTarget`, `ScrollTargets`,
`ScrollControllerRegistry`, `SceneStore.ScrollControllers`; Controls `IScrollController`, `ScrollToRequest`,
`ScrollByRequest`.

| Old | New |
|---|---|
| `ScrollEl.Controller` / `VirtualListEl.Controller` | `ScrollEl.Handle` / `VirtualListEl.Handle` (`ScrollHandle?`) |
| `ListOptions.Controller`, `ListOptions.VerticalScrollController` | `ScrollOptions.Handle` (the `ItemsView`'s `Scroll = new ScrollOptions { Handle = … }`) |
| `ItemsView.ScrollHandle` / `ItemsView.VerticalScrollController` (component fields) | `ItemsView.Handle` (filled from `ScrollOptions.Handle`) |
| `ctrl.Offset` (`IReadSignal<float>`) | `handle.Offset` (`IReadSignal<double>`) |
| `ctrl.Extent` / `ctrl.Viewport` (`float`) | `handle.Extent` / `handle.Viewport` (`double`), plus signals `ExtentSignal`, `ViewportSignal`, `MaxOffsetSignal` |
| `ctrl.UserScrolling` | `handle.Motion.Value.UserDriven` (`ScrollMotionState`: `Kind`, `SpeedDipPerS`, `UserDriven`, `IsMoving`) |
| — | `handle.AtStart`, `handle.AtEnd`, `handle.CanScroll` (`IReadSignal<bool>`) |
| `ctrl.IsAttached` | `handle.IsBound` |
| `ctrl.Attach(scene, node, wake)` / `Detach()` | host-managed: the host binds the handle when the viewport node mounts and unbinds it on unmount. Never call `Bind`/`Unbind` from app code. |
| `ctrl.ScrollTo(o, ScrollAnimate.Glide)` | `handle.ScrollTo(o)` (`ScrollMove.Glide` default) |
| `ctrl.ScrollTo(o, ScrollAnimate.Immediate)` | `handle.ScrollTo(o, ScrollMove.Immediate)` |
| `ctrl.ScrollTo(o, new ScrollGlide(halflife, ζ, ω, settle))` | `handle.ScrollTo(o, ScrollMove.Follow)` for follow-style retargets (see §4); plain `Glide` otherwise. Glide constants are feel data (`MotionFeel.GlideOmega`), not per-call. |
| `ctrl.ScrollBy(d, animate)` | `handle.ScrollBy(d, ScrollMove.Glide/Immediate)` — a glide accumulates onto the plan's destination |
| `ctrl.WheelNotch(n)` | `handle.WheelNow(n)` (or `handle.Wheel(tNotch, n)` with a device time) |
| `ScrollController.WheelSampleSec()` | gone — the host owns the plan clock |
| `ctrl.BringIntoView(node, align, animate)` | `scene.BringIntoView(node, align, move)` (§3) or `handle.BringIntoView(itemTop, itemExtent, align, move, margin)` for content-space spans |
| `ctrl.OnGeometry(project, action)` | subscribe to `handle.Offset` / `handle.ExtentSignal` in an effect and project to a coarse key yourself (§6) |
| (new) | `handle.Restore(offset)` / `RestorePending`, `handle.SetSnap(grid)`, `handle.AutoScroll(dipPerS)`, `handle.ShiftFrame(delta)`, `handle.Stop(t)` |

Before:
```csharp
var ctrl = UseMemo(() => new ScrollController(), DepKey.Empty);
return new ScrollEl { Controller = ctrl, Content = body };
// …
ctrl.ScrollTo(0f, ScrollAnimate.Immediate);
bool busy = ctrl.UserScrolling;
```
After:
```csharp
var handle = UseMemo(() => new ScrollHandle(), DepKey.Empty);
return new ScrollEl { Handle = handle, Content = body };
// …
handle.ScrollTo(0.0, ScrollMove.Immediate);
bool busy = handle.Motion.Value.UserDriven;     // .Peek() outside a reactive read
```

A move issued before the handle is bound (the viewport has not mounted yet) is latched and lands the moment the extent
can hold it — no "post after mount" workaround is needed. A `ScrollTo` past today's extent lands at today's max and
keeps the raw target latched until the content grows to hold it; any user input drops the latch.

**AnnotatedScrollBar.** `AnnotatedScrollBarController` is now a thin adapter over a handle; the `SetValues` /
`SetIsScrollable` feed and the `ScrollToRequested` / `ScrollByRequested` / `WheelNotchRequested` events are gone (the
signals ARE the viewport's live state):
```csharp
// Before
var bar = new AnnotatedScrollBarController();
list = ItemsView.Create(n, row, layout, new ListOptions { VerticalScrollController = bar });
bar.ScrollToRequested += r => ctrl.ScrollTo(r.Offset, r.Animate ? ScrollAnimate.Glide : ScrollAnimate.Immediate);
// After
var bar = new AnnotatedScrollBarController();                       // mints a handle (or pass one in)
list = ItemsView.Create(n, row, layout, new ListOptions { Scroll = new ScrollOptions { Handle = bar.Handle } });
// bar.Offset / MaximumOffset / ViewportLength / IsScrollable are IReadSignal<double>/<bool> now (were <float>)
```

**Wheel routing from a header** (`Element.WheelTarget`): the property type changed from `IWheelTarget` to
`ScrollHandle`. An `ItemsViewController` is no longer an `IWheelTarget`:
```csharp
// Before: header.WheelTarget = itemsController;      // or a ScrollController
// After:  header with { WheelTarget = listHandle }   // the same handle the list's ScrollOptions.Handle carries
```

---

## 2. `Hooks.UseScroll` / `ScrollControllerChannel` — removed

There is no implicit "nearest ancestor viewport" context any more. Create the handle where the scroller is rendered
and hand it down explicitly — as a prop, or through your own context:
```csharp
// Before (descendant)
var ctrl = this.UseScroll();
ctrl?.ScrollBy(-200f);
// After (owner)
// (app) public static class PageScroll { public static readonly Context<ScrollHandle?> Channel = new(null); }
var handle = UseMemo(() => new ScrollHandle(), DepKey.Empty);
return Ctx.Provide(PageScroll.Channel, handle, new ScrollEl { Handle = handle, Content = … });
// After (descendant)
var handle = UseContext(PageScroll.Channel);
handle?.ScrollBy(-200.0);
```
`RenderContext.PeekMainScrollBusy` is gone too — read the page handle's `Motion` instead.

---

## 3. `ScrollIntoView` → `SceneScrollExtensions.BringIntoView` / `ScrollHandle`

Removed: `FluentGpu.Scroll.ScrollIntoView` (`Bring`, `BringInto`, both `ScrollTo` overloads).

| Old | New |
|---|---|
| `ScrollIntoView.Bring(ctx, node, margin, alignmentRatio, animate)` | `ctx.Scene.BringIntoView(node, alignmentRatio, animate ? ScrollMove.Glide : ScrollMove.Immediate, margin)` |
| `ScrollIntoView.BringInto(ctx, viewport, node, …)` | `ctx.Scene.BringIntoView(viewport, node, align, move, margin)` |
| `ScrollIntoView.ScrollTo(ctx, viewport, target, animate)` | `ctx.Scene.ScrollHandleFor(viewport)?.ScrollTo(target, move)` (or your own handle) |
| `ScrollIntoView.ScrollTo(ctx, viewport, target, ScrollGlide g)` (lyrics follow) | `handle.ScrollTo(target, ScrollMove.Follow)` |

`SceneScrollExtensions.BringIntoView` (namespace `FluentGpu.Scroll.Runtime`) resolves a REALIZED node to content
coordinates itself — including a virtualized row, whose layout position is relative to the realized window's arrange
origin (`ScrollState.WindowOrigin`) — so never compute `Bounds.Y` yourself for a row of a virtual list. For an item
that is not realized, resolve its content offset from the list model and call `handle.BringIntoView(itemTop,
itemExtent, align, move, margin)` (or `ItemsViewController.StartBringItemIntoView(index, …)`).

Before:
```csharp
ScrollIntoView.Bring(Context, rowNode, margin: 8f, alignmentRatio: 0f, animate: true);
```
After:
```csharp
Context.Scene!.BringIntoView(rowNode, align: 0f, ScrollMove.Glide, margin: 8.0);
```

---

## 4. Lyrics follow — `ScrollMove.Follow`

`ScrollMove.Follow` is a velocity-continuous glide that YIELDS to the user: while a user-driven plan (wheel, drag,
fling, thumb) is live and unsettled, a `Follow` request is ignored. The bespoke follow state machine (arm/intent/resync
glide with its own ζ/ω) is replaced by calling it on every lyric-line change:
```csharp
// Before
ScrollIntoView.ScrollTo(ctx, lyricsVp, target, new ScrollGlide(HalflifeMs: 140, Zeta: 0.9f, Omega: 22f, SettleVel: 4f));
// After
lyricsHandle.ScrollTo(target, ScrollMove.Follow);
```
Resuming follow after the user scrolled is simply the next `Follow` call once `lyricsHandle.Motion.Value.UserDriven`
is false again.

---

## 5. Scroll-linked effects: `ScrollBindDsl` / `ScrollRecipes` → `ScrollEffect`

Removed: `Element.ScrollBinds` (`ScrollBindDsl[]`), `ScrollBindDsl`, `ScrollRange`, `ScrollBindAnchor`,
`ScrollChannel`, `BindSink`, `ScrollBind`/`ScrollBindTable`/`ScrollBindEval`, `ScrollRecipes`
(`StretchFromTop`, `ParallaxY`, `Collapse`, `Sticky(onStuck)`, `ClipBelow(onFlag)`, `Reveal`),
`SceneStore.ScrollBinds`/`ScrollBindCount`/`ScrollObservers`, `ScrollState.ScrollFlags` and its bits
(`StuckTopBit`, `ScrolledFwdBit`, `MovingNowBit`, …).

Added: `Element.ScrollEffects` (`ScrollEffectSpec[]`), `Element.ScrollScope` (names a sticky containing block), the
runtime `ScrollEffect` record + `ScrollEffectEval`, and the DSL `FluentGpu.Scroll.Effects.ScrollEffectDsl`:
`.OnScroll(effect, scope)`, `.Sticky(top, scope)`, `.StickyClip(inset)`, `.Parallax(in0, in1, out0, out1)`,
`.ParallaxY(fraction, overPx)`, `.Fade(in0, in1, from, to)`, `.Reveal(start, overPx, dy)`. Channels: `TransX`,
`TransY`, `Opacity`, `ClipTop`, `ScaleXY`, `ThumbPos`. Effects are evaluated from the SAME device-pixel-snapped
position on the UI thread (hit-test) and the render thread (pixels), so a pinned header never drifts against its rows.

| Old | New |
|---|---|
| `new ScrollBindDsl { PinTop = 56f }` / `.Sticky(56f)` | `.Sticky(top: 56f)` (optionally `scope: "hero"`) |
| `new ScrollBindDsl { ClipTopAtViewport = 48f }` / `.ClipBelow(48f)` | `.StickyClip(48f)` |
| `.ParallaxY(0.5f, 300f)` | `.ParallaxY(0.5f, 300f)` (same name, now a `ScrollEffect.Parallax` map) |
| `From = Offset, To = Opacity, Range = ScrollRange.Px(a, b), OutStart = 1, OutEnd = 0` | `.Fade(a, b, from: 1f, to: 0f)` |
| `To = TransY/TransX` map | `.Parallax(a, b, out0, out1)` (Y) or `.OnScroll(new ScrollEffect(EffectChannel.TransX, EffectKind.Map, a, b, o0, o1, Easing.Linear, 0f, 0))` |
| `To = ScaleUniform` map | `.OnScroll(ScrollEffect.Scale(a, b, from, to, ease))` |
| `.Reveal(start, overPx, dy)` | `.Reveal(start, overPx, dy)` (fade + parallax pair) |
| `Range = ScrollRange.Frac(…)` (fraction of max offset) | compute the DIP range from the list's extent (`handle.MaxOffsetSignal`) when authoring |

Before:
```csharp
new BoxEl { Height = 48f, Fill = header }.Sticky(0f, onStuck: s => stuck.Value = s);
new BoxEl { Height = 4000f }.ClipBelow(48f);
heroImage.ParallaxY(0.5f, 320f);
```
After:
```csharp
using FluentGpu.Scroll.Effects;
new BoxEl { Height = 48f, Fill = header, ScrollScope = null }.Sticky(top: 0f);
new BoxEl { Height = 4000f }.StickyClip(48f);
heroImage.ParallaxY(0.5f, 320f);
```

**Sticky scope (replaces the `HeroRoot` workaround).** A sticky effect with no named scope clamps to the node's
immediate parent (CSS containing block). A component's rendered root has a mirroring anchor as its parent, so a
sticky placed on it would never pin (gate `e11virt.comp-pin` documents this). Name the real containing block instead:
```csharp
new BoxEl { ScrollScope = "table", Children = [ heroAndHeader, rows ] };
header.Sticky(top: 56f, scope: "table");        // clamps to the "table" ancestor's end, wherever the header sits
```

**No replacement (known gaps — raise with the engine before migrating the call site):**
- `onStuck` / `OnFlag` callbacks — the engine sets `NodeFlags.StickyPinned` on the node but raises no callback. Derive
  "stuck" from `handle.Offset` against the header's known content position.
- `ScrollFlags` bits (`ScrolledFwdBit` direction, `MovingNowBit`) — use `handle.Motion` (`Kind`, `SpeedDipPerS`,
  `IsMoving`) and the sign of successive `handle.Offset` values.

**One transform owner.** A static `Transform` matrix plus a `TransX`/`TransY`/`ScaleXY` effect on the same element
throws in DEBUG (`AssertSingleTransformOwner`).

---

## 6. `OnScrollGeometryChanged` — removed

Removed from `Element`, `VirtualListEl`, `ListOptions`, `ItemsView`; `ScrollGeometry` and the
`SetScrollObserver` scene seam are gone. Observe the handle's signals and project to a coarse key yourself:
```csharp
// Before
new ScrollEl { OnScrollGeometryChanged = (g => g.OffsetY > 200f ? 1 : 0, g => compact.Value = g.OffsetY > 200f) };
// After — bind the dependent visual straight off the handle's signal, projected to a coarse value
var handle = UseMemo(() => new ScrollHandle(), DepKey.Empty);
var compactBar = new BoxEl { Opacity = Prop.Of(() => handle.Offset.Value > 200.0 ? 1f : 0f), … };
return new ScrollEl { Handle = handle, Content = … };
```
(`handle.Offset` updates once per UI frame and coalesces equal values; a `Prop` thunk re-evaluates when it changes and
writes only when its projected value changes. A component that must RE-RENDER on a threshold should mirror the
projection into its own `Signal<bool>` so it re-renders only on the crossing, never per pixel.)

---

## 7. Virtualization knobs — `ItemsView` / `Virtual` / `VirtualListEl` / `ListOptions`

The realize window is sized in PIXELS from the plan's present-time velocity (`MotionFeel.LookaheadS`, clamped to
`OverscanMinPx..OverscanMaxPx`) and every covered row realizes in the same frame. Every row-count throttle is gone:

| Removed | Why / replacement |
|---|---|
| `VirtualListEl.Overscan`, `ListOptions.Overscan`, `ItemsView.OverscanItems` | velocity-sized pixel overscan (live-tunable: `ScrollTunables`) |
| `VirtualListEl.RealizeOverscanImmediately`, `CacheExtentPx` (VirtualListEl, ListOptions, ItemsView) | same |
| `VirtualListEl.KeepAliveCap`, `ListOptions.KeepAliveCap`, `ItemsView.KeepAliveCap` | per-item `ListOptions.KeepAlive` stays; the cap was a band-aid |
| `VirtualListEl.StaggerColdRealize`, `ListOptions.StaggerColdRealize`, `ItemsView.StaggerColdRealize` | the first frame realizes its whole window |
| `Virtual.ListBound(…, overscan)` / `Virtual.GridBound(…, overscan)` parameter | drop the argument |
| `ScrollState.Overscan`, `ExtentTableRef`, `AnchorKey`, `AnchorViewportDelta`, `PrevArrangedFirst/Last` | internal; `ScrollState.Extent` (`IExtentSource`), `AnchorIndex`, `WindowOrigin`, `WindowOriginIndex`, `CoverStart/End` |

Before / after:
```csharp
// Before
Virtual.ListBound(n, 56f, row, overscan: 8) with { StaggerColdRealize = true, KeepAliveCap = 16 };
ItemsView.CreateBound(src, row, layout, new ListOptions<T> { Overscan = 6, CacheExtentPx = 800f });
// After
Virtual.ListBound(n, 56f, row);
ItemsView.CreateBound(src, row, layout, new ListOptions<T> { });
```

**Anchoring.** A measured-extent correction ABOVE the first fully visible row (`ScrollState.AnchorIndex`) shifts the
live plan in the same call (`Virtualizer.ApplyMeasured` → `PlanSlots.Shift`), so the rows being read never jump.
`ItemsViewController.ScrollBy`/`PreserveAnchor` are the same instant frame shift (`ScrollHandle.ShiftFrame`) for
structural changes above the viewport.

**`ScrollKey` restore.** Unchanged spelling (`ScrollEl.ScrollKey`, `VirtualListEl.ScrollKey`,
`ScrollOptions.ScrollKey`), now backed by `ScrollPositionMemory` + `ScrollHandle.Restore` (latched until the extent can
hold the saved offset; while the list is still filling it chases the end). **Behaviour change:** the engine no longer
namespaces keys by the enclosing KeepAlive slot — compose the tab/slot identity into the key yourself when the same
content can be open twice (`$"{tabId}:artist:{uri}"`).

**Snap.** `ScrollEl.Snap` / `ScrollOptions.Snap` (`SnapSpec`) and post-mount `ScrollState.SnapInterval` writes still
work (the host republishes the grid to the handle every frame). A fling over a snap grid is re-solved AT AUTHORING to
land exactly on a snap value in finite time (velocity-continuous at the lift); wheel notches retarget onto the grid in
their direction; keyboard/programmatic moves are not snapped.

---

## 8. `PagedShelf`, `LazyGrid`, other controls

Public surface unchanged. `PagedShelf` drives its strip through its own `ScrollHandle` (the page-snap glide, the lift
debounce and the directional commit are unchanged in behaviour); `LazyGrid` restores and reveals through the page
handle (`Restore`, `ScrollTo`). `TabStrip`/`TabView`/`TreeView`/`ItemsView` edge auto-scroll uses
`ScrollHandle.AutoScroll` (a closed-form constant-velocity plan that stops at the edge). Nothing to migrate unless the
app reached into their internals.

---

## 9. Flat rows: `ListRowEl` for track rows (Wave 2 target)

`ListRowEl` (namespace `FluentGpu.Dsl`) is ONE scene node per row: up to 8 `RowCell`s (`Text`, `Image`, `Glyph`,
`Rect`) at row-local rects, one recorded span, recycled by ONE signal write, placeholder = same geometry with grey
bars. Use it for the hot 10k–100k lists (track tables, episode lists); rich rows (drawers, descriptions) stay
component rows — the two tiers mix in the same list.
```csharp
// primary/secondary/accent/rowHover/rowSelected/heartGlyph/iconFont: the app's own tokens
var rows = ItemsView.CreateBound(tracks, item => new ListRowEl(item.Cells((t, cells) =>
{
    cells.Add(new RowCell { Kind = RowCellKind.Text,  Rect = new RectF(12f, 18f, 28f, 20f), Text = FormatCache.Int(t.Number), Color = secondary });
    cells.Add(new RowCell { Kind = RowCellKind.Image, Rect = new RectF(48f, 8f, 40f, 40f), ImageSource = t.CoverUrl, Corners = new CornerRadius4(4f) });
    cells.Add(new RowCell { Kind = RowCellKind.Text,  Rect = new RectF(100f, 10f, 260f, 20f), Text = t.Title, Color = primary, FontSize = 14f, Trim = TextTrim.CharacterEllipsis });
    cells.Add(new RowCell { Kind = RowCellKind.Text,  Rect = new RectF(100f, 30f, 260f, 18f), Text = t.Artists, Color = secondary });
    cells.Add(new RowCell { Kind = RowCellKind.Glyph, Rect = new RectF(640f, 18f, 20f, 20f), Text = heartGlyph, FontFamily = iconFont, Color = accent });
    cells.Add(new RowCell { Kind = RowCellKind.Text,  Rect = new RectF(700f, 18f, 48f, 20f), Text = FormatCache.DurationMmSs(t.DurationMs), Color = secondary });
}))
{
    Height = 56f,
    HoverFill = rowHover,
    SelectedFill = item.Color(t => t.Selected ? rowSelected : ColorF.Transparent),
    Placeholder = item.Show(t => t.IsPlaceholder),
    OnCellClick = cell => { /* index-resolved: 2 = title, 4 = like, … */ },
}, RepeatLayout.Stack(56f), new ListOptions<Track> { Scroll = new ScrollOptions { Handle = tableHandle } });
```
`BoundItemScopeExtensions.Cells` allocates one `RowCellBuffer` per slot at template time and refills it on every
recycle (zero allocation). Cell rects come from the table's own column layout (the same numbers `RowMetrics` computes
today), not from measured text.

---

## 10. Input and PAL surface

- `InputKind.Wheel`, `ScrollBegin`, `ScrollDelta`, `ScrollEnd` and `ScrollDeviceClass` are gone: every scroll input
  is `InputKind.Scroll` carrying one `ScrollInputEvent` (`Source`, `Phase`, `Qpc`, `PointerDip`, `Dx`, `Dy`,
  `PointerId`, `Mods`); build one with `InputEvent.ForScroll(in e, pointerKind, timestampMs)`. `PointerVelSample` lost
  its `Seq`/`IsScrollPhase` fields.
- Wheel is URGENT: a real window delivers notches synchronously through `IPlatformWindow.SetScrollInputSink` — a notch
  authors its plan during message dispatch and the render thread poses it on its next tick.
- Element wheel handlers (`OnPointerWheel`, `WheelEventArgs`) keep their shape: `Delta` = notches ×
  `MotionFeel.WheelNotchDip`, positive toward the content end; `Handled` still stops the viewport.
- Wheel feel: WinUI-exact by default — 32 DIP per notch, a 0.257 s cubic per notch, notches ACCUMULATE onto the live
  glide's destination (a fast spin travels every notch; a reversal re-bases on what is shown).

---

## 11. Diagnostics: removed `FrameStats` scroll fields → `ScrollProbe` / `BurstSummary` / `ScrollTunables`

Removed from `FrameStats` (and what to read instead):

| Removed field | Replacement |
|---|---|
| `ScrollLiveMotion` | `BurstSummary.Presents` over the burst; per viewport `handle.Motion.Value.IsMoving`; host `AppHost.AnyUserScrollMoving` |
| `ScrollDeltaDip` | probe `Pose` records (offset per present) → `ExportCsv` / `BurstSummary.MaxJitter`/`AvgJitter` |
| `WheelNotches` | `BurstSummary.Notches` |
| `ScrollEdgePins`, `ScrollStructuralDip` | `BurstSummary.ExtentJumps` (a correction that did NOT anchor in the same frame) and probe `Extent` records |
| `ScrollContactHeld` | `handle.Motion.Value.Kind == MotionKind.Drag` |
| `ScrollZeroReason` | `BurstSummary.Verdict` (`Smooth`, `Clamped`, `Jumped`, `Late`, `Uneven`) + `CoverageClamps`, `LatePresents` |
| `ScrollDtRepaired` | gone — there is no dt |
| `LyricsScrollMode`, `LyricsUserScrollActive`, `MainScrollMode` | the lyrics/page handles' `Motion` (`Kind`, `UserDriven`) |
| `LyricsContentDirtyAtRecord`, `MainContentDirtyAtRecord` | `FrameStats.SpanMisses` (span-reuse miss census) |
| `StickyClipEvals`, `StickyClipDirties`, `StickyClipFullyHidden`, `PinDirties`, `ContinuousDirties`, `ScrollBindCount` | gone with `ScrollBindEval`; effect cost shows in probe `Cost` records (`ScrollCostPhase`) |
| `BlurSuppressedByScrollCount` | renamed `BlurHeldCount` (holds are damage-keyed now, not scroll-keyed) |

Also removed from `AppHost`: `ScrollKernel`, `ScrollSink`, `ProbeLyricsViewport`, `ProbeMainViewport`. Added:
`AppHost.TryGetScrollHandle(viewport)`, `AppHost.Plans` (`PlanSlots`), `AppHost.AnyUserScrollMoving`;
`AppHost.ScrollChrome` is now `FluentGpu.Scroll.Runtime.ScrollBarChrome`. The `[scrollperf]` log line and the
`FG_SCROLL_TRACE`/`ScrollTrace`/`ScrollLog` switches are gone — there are no environment switches.

**The Diagnostics "Scroll" card** (replaces `Diagnostics.ScrollTrace.cs`'s verdict engine) — the engine calls it
needs, with the UI left as comments:
```csharp
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;

// Level — Off | Summary | Trace (always compiled in; Off costs one volatile read per call site)
// UI: a 3-way segmented control bound to (int)ScrollProbe.Level
ScrollProbe.Level = ProbeLevel.Summary;

// Profile picker — the named presets (WinUiExact default, Glide, Snappy)
foreach (var (name, _) in FeelProfiles.All) { /* option */ }
ScrollTunables.ApplyProfile(name);            // ScrollTunables.ActiveProfileName reads back (or "Custom")

// Live sliders — every double field of MotionFeel, with range + WinUiExact default
foreach (var t in ScrollTunables.All)          // TunableF: Name, Min, Max, Default, Get, With
{
    double current = t.Get(ScrollTunables.Current);
    // UI: a slider [t.Min, t.Max] showing `current` (reset → t.Default); on change:
    //     ScrollTunables.Apply(t.With(ScrollTunables.Current, newValue));
}
// Poll ScrollTunables.Version to refresh the card after an external Apply (no change event by design).

// Persistence (app settings)
settings.ScrollFeelJson = ScrollTunables.ToJson();
if (ScrollTunables.TryFromJson(settings.ScrollFeelJson, out var feel)) ScrollTunables.Apply(feel);

// Burst summary (one line per burst — call at gesture end or on a timer; off the hot path, allocates)
var s = ScrollProbe.EndBurst(Stopwatch.GetTimestamp());
log.Info(s.FormatLine());   // notches, presents, clamps, jumps, late, jitter avg/max, cost avg/max, verdict

// CSV export for analyze.py (columns qpc_ms,pane,kind,offset,delta,rendering_time_ms,device,note)
using var w = File.CreateText(path);
ScrollProbe.ExportCsv(w, Stopwatch.Frequency, refreshHz: 120.0, dpiScale: 1.5);
```
Tunables are data (`MotionFeel`), read once per authored plan — a slider change affects the next notch/fling, never a
plan in flight.

---

## 12. Checklist for the Wavee migration

1. Replace every `Controller =` / `VerticalScrollController =` / `ScrollController` / `IScrollController` use with a
   `ScrollHandle` (§1); every `UseScroll()` with an explicit handle or app context (§2).
2. Replace every `ScrollIntoView.*` call (§3); Lyrics follow → `ScrollMove.Follow` and delete its follow state machine (§4).
3. Replace every `ScrollBinds`/`ScrollRecipes` use with `.Sticky`/`.StickyClip`/`.Parallax`/`.Fade`/`.Reveal`/`.OnScroll`
   (§5), a hero collapse/stretch with `.Collapse(over, minH, anchor)` / `.StretchFromTop()` (a leading-collapse root
   carries no `ClipToBounds` — it cuts at its presented edge itself, `scroll.md` §7.2); replace `Track.Table`'s
   `HeroRoot` wrapper with `ScrollScope` + `.Sticky(scope:)`; flag the `onStuck` sites (no direct replacement).
4. Drop every removed virtualization knob (§7); add the tab/slot identity to `ScrollKey`s shared across tabs.
5. Replace `OnScrollGeometryChanged` observers with coarse `handle.Offset` projections (§6).
6. Rebuild the Diagnostics scroll card on `ScrollProbe`/`ScrollTunables`; delete `Diagnostics.ScrollTrace.cs` and every
   read of the removed `FrameStats` fields (§11).
7. Wave 2: move the track table and episode rows to `ListRowEl` + `item.Cells(…)` (§9).
