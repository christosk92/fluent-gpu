# Scroll rework — delete the stack, rebuild from first principles — implementation plan

Status: **APPROVED 2026-09-23.** Supersedes `docs/plans/scroll-v3-plan-2026-08-17.md`,
`docs/plans/compositor-scroll-implementation.md`, and `docs/plans/scroll-feel-rework-design.md` /
`scroll-feel-rework-v2-design.md` / `scroll-feel-v2.1-edge-momentum-addendum.md` in full — each carries a
one-line superseded banner at its top pointing here (§8). Source design: the Fable 5.1 architect's design
(summarized in §1–§9 below) plus the owner's approved plan
(`investigate-thoroughly-please-all-binary-wirth.md`, 2026-09-23). Owner decisions and the two points the
design left open are recorded in §0. Written for implementation subagents with **no other context**: every
wave names its files, gives the real code shape, and states the gate that proves it. Engine rules that bind
every change (`CLAUDE.md`): 0 managed allocations on every render-thread/hot-list path this plan adds; the
render thread owns every `ComPtr`; `FluentGpu.VerticalSlice`'s transitive closure stays TerraFX-free; no
source-text tests; no environment-variable behaviour switches; no legacy paths (replace, delete).

Abbreviations: `A:` = `src/FluentGpu.Engine/Hosting/AppHost.cs`, `R:` = `src/FluentGpu.Engine/Reconciler/Reconciler.cs`,
`FL:` = `src/FluentGpu.Engine/Layout/FlexLayout.cs`, `SR:` = `src/FluentGpu.Engine/Render/SceneRecorder.cs`,
`D:` = `src/FluentGpu.Windows/D3D12/D3D12Device.cs`, `VL:` = `src/FluentGpu.Engine/Reconciler/VirtualListEl.cs`,
`W32:` = `src/FluentGpu.Windows/Pal/Win32DirectManipulation.cs` / `Win32Platform.cs`. App paths are under
`C:\wavee\waveemusic\src\apps\Wavee\`.

---

## 0. Owner requirements and decisions

**Requirements (unconditional):**
- WinUI-grade feel, entirely in-engine — no `InteractionTracker`, no WinAppSDK, no new present layer.
- **No layout jumps, no blank rows**, even on the fastest fling.
- 10k–100k-row lists stay smooth.
- Delete everything old — no legacy path survives beside the new one.
- Diagnostics from day one, **runtime-switchable and live-tunable** from the Diagnostics page — no env vars.
- **No throttles, ramps, or holds that trade correctness for frame time** — fix the cost at its source.

**Decisions taken 2026-09-23:**
- Keep DirectManipulation as a **stripped contact producer only** (no inertia, no wedge ladder) — never a
  physics owner.
- Accept the **two-tier row model**: a new `ListRowEl` for flat rows; existing component rows stay for rich
  rows (drawer, episode).
- Ship **WinUI-exact** wheel feel (32 DIP/notch, 0.257 s cubic) as the default profile; `Glide` (56 DIP, 0.40 s,
  velocity carry) ships as a live-switchable preset, not a fallback.

**Points the design left open — resolved here as working decisions:**
- The compositor overlay row headroom (the render-thread lease/animation table capacity that both `ScrollPoser`
  and `RenderCompositorAnimations` reserve from) grows from today's 32 to **64**.
- Present-time prediction stays on **RefreshLattice**: `presentSec = tick + (1 + lat)·refresh`, the same law
  `RenderScrollLease`/`ScrollClock` already use (`src/FluentGpu.Engine/Scroll/ScrollClock.cs`). It is revisited
  only if Wave 3's probe data shows systematic phase error against the WinUI golden captures.

---

## 1. Root causes → replacing principles

Six defects, each evidenced against the working tree, each replaced by one structural rule — not a tuned
constant.

**1. Stateful dt integration → immutable analytic plan, evaluated statelessly.**
`AppHost.cs:676-680` rebuilds the kernel's `dt` from the wall clock when a frame stamp repeats
(`_prevScrollNowQpc`/`_scrollDtRepairedThisFrame`/`_scrollDtRepairs`); `ScrollBody`'s per-tick `Advance(dt)`
(`src/FluentGpu.Engine/Scroll/ScrollBody.cs`) integrates whatever `dt` it is handed, so a repeated, skipped, or
jittery tick corrupts the position it is threaded through, and the "repair" is a patch on the symptom, not the
defect. **Replacing principle: motion is `p(t)`, a closed-form function of one absolute time. There is no `dt`
anywhere in `Scroll.Motion`** — every consumer (UI virtualizer, render poser) calls `Eval(presentSec)` and gets
the same answer regardless of how many ticks fired or were skipped in between.

**2. Sampled at production, presented two refreshes later → the render thread poses itself.**
Offset is written by `ScrollKernel.Tick` inside `Paint` (the *production* tick) and carried into
`SceneScrollSink.Apply`, which stamps the content child's `LocalTransform` (`SceneScrollSink.cs:89-91`); by the
time that scene publishes and presents, real present time has moved by 1–2 refresh intervals and the pose is
stale. **Replacing principle: the render thread evaluates the plan at the *predicted present time*
(`ScrollPoser.Tick`, riding `RenderCompositorAnimations.Adopt/Tick`, `AppHost.cs:1191-1197`) and writes the
translate itself.** The UI thread never poses a pixel.

**3. Scroll intent rode the scene publication → a lock-free `PlanSlot`.**
A wheel notch today waits for `PacedInputWaitClassifier`'s deferrable classification, then a full UI
frame/publish cycle before the render thread ever sees it. **Replacing principle: wheel is removed from the
deferrable set; a replan writes directly into a per-viewport `PlanSlots` seqlock that the render thread reads
every tick** — no UI frame in the loop between notch and pose.

**4. Throttles hiding cost → make rows cheap, realize everything.**
`FrameBudget.cs` (whole file: `MotionUiSliceMs = 3f`, `Arm`/`Disarm`/`DeadlineTicks`) bounds realize + image
apply to 3 ms while a body is Drag/Ballistic; `ColdRealizeRamp` (`src/FluentGpu.Engine/Scene/ColdRealizeRamp.cs`,
consumed at `Reconciler.cs:280-325`) throttles how many rows a cold virtual window may realize per frame;
`DecodeScheduler.ScrollThrottled` (`src/FluentGpu.Engine/Media/Images/DecodeScheduler.cs:63-95,257-287`) caps
image-apply bytes to 512 KB/frame while scrolling; `VirtualListEl.Overscan=4`
(`VL.cs:38`) under-covers a fast fling. **Replacing principle: rows are cheap enough (`ListRowEl`, §5) that every
covered row realizes in the same frame, every frame — the only cost knob left is a velocity-sized overscan in
*pixels*, not a per-frame row cap.**

**5. Two writers to one number → one `ExtentService`.**
`FlexLayout.cs:1230-1263` (`PostAnchorShiftAndFrame`) posts an anchor shift *and* a frame update to the scroll
kernel in the same call when a measured-extent correction lands above the anchor — a second writer racing the
kernel's own integration — with a sub-pixel noise gate at `FL.cs:1256` papering over the jitter that produces.
**Replacing principle: `IExtentSource`/`Virtualizer` is the one writer of extent; a correction above the anchor
shifts the plan's coordinate frame (`ScrollPlan.Shifted`) in the *same call* that publishes the new coverage —
there is no second path to the offset.** Positions are `double`; the render thread only ever sees a small
`float` relative to a window origin.

**6. Constants-by-complaint → named feel profiles as data.**
`AcrylicScrollHold`'s `ScrollRefreshCadence = 4` (`src/FluentGpu.Engine/Render/AcrylicScrollHold.cs:37`),
`SelfBlurHoldAfterScrollTicks`'s ~0.12 s hold (`AppHost.cs:1538,4193`), `D3D12Device`'s blur-pin key
(`D.cs:4058-4104,4151-4172`) are each an independently-tuned magic number reacting to a symptom someone hit.
**Replacing principle: `ScrollFeel` is one struct behind a seqlock, exposed as `ScrollTunables` with named
presets (`WinUiExact`, `Glide`, `Snappy`), live-editable from the Diagnostics page and checked against golden
probe captures (`analyze.py`) — never a silent literal in a hot path.**

---

## 2. Architecture

```
 UI thread                                              Render thread (compositor tick)
 ─────────                                              ──────────────────────────────
 wheel (URGENT, never deferred)                          each tick:
 DM contact (touchpad) / touch / pen / keys / thumb        presentSec = tick + (1+lat)·refresh   (RefreshLattice)
       │                                                        │
       ▼                                                        ▼
 ScrollRouter                                             PlanSlots[vp].Read() ── seqlock POD ──▶ p = plan.Eval(presentSec, out v)
   hit-test last layout → latch → chain to parent               │
       │                                                        ▼
       ▼                                                 p' = clamp(p, Coverage.Start, Coverage.End − Coverage.Viewport)
 PlanAuthor.WheelNotch / Follow / Fling / Programmatic           │  (clamp ⇒ ScrollProbe.CoverageClamp — never a blank row)
   replans from displayed p(tNotch)                              ▼
       │                                                  trans = DevicePixelSnap(WindowOrigin − p', scale)
       ▼                                                         │
 PlanSlots[vp].Write(plan)  ── seqlock POD, lock-free ──────────►│
                                                                  ▼
 ── frame start (UI) ──                                   CompositorPaint(content).Transform = Translate(trans)
 Virtualizer:                                             ScrollEffectEval.Apply(sticky, parallax, fade, thumb) from
   p, v = plan.Eval(presentSec)                             the SAME p'
   overscanPx = clamp(|v|·lookaheadS, min, max)                  │
   realize ALL of [IndexAt(p−behind) .. IndexAt(p+vp+over)]      ▼
   measured correction above anchor ⇒ plan.Shifted(Δ)     feedback[vp] = (p', v, settled) ──────────────┐
     same frame + WindowOrigin += Δ                                                                       │
   publish Coverage{Start,End,Viewport,WindowOrigin}                                                      │
       │                                                                                                  │
       ▼                                                                                                  ▼
 SceneFramePublisher ──────────────────────────────────▶ PresentCadence.Decide → one present     ScrollMotionState[vp]
                                                                                                   (FLIP suppression,
                                                                                                    PagedShelf, Lyrics)
```

Everything left of the vertical seam is UI-thread and allocation-tolerant (bounded); everything right of it is
render-thread and zero-alloc per tick. `PlanSlots` is the only thing crossing the seam every tick; `Coverage` is
the only thing crossing back into presentation state; `ScrollMotionState`/`Feedback` is the only thing crossing
back into UI state.

---

## 3. Motion — `FluentGpu.Scroll.Motion` (new folder, pure POD, no `SceneStore`/TerraFX references)

```csharp
namespace FluentGpu.Scroll.Motion;

public enum SegKind : byte { Hold, Cubic, Decay, Spring, Follow, Glide }

/// <summary>One closed-form segment, valid on [T0,T1) (T1 = +inf for an open-ended tail). All times are absolute
/// QPC-derived seconds — never a duration, never relative to "now".</summary>
public readonly struct MotionSeg
{
    public readonly SegKind Kind;
    public readonly double T0, T1;
    public readonly double P0, P1;   // start / destination (Hold/Cubic/Glide); Decay/Spring: P0=start, V0=velocity
    public readonly double V0;       // initial velocity (Decay, Spring, Glide continuity, Follow extrapolation)
    public readonly double K;        // Decay rate (1/s) | Spring omega (rad/s)
    public readonly double Zeta;     // Spring damping ratio (1 = critically damped)

    public double Eval(double t, out double v)
    {
        switch (Kind)
        {
            case SegKind.Hold:
                v = 0;
                return P0;
            case SegKind.Cubic:
            {
                double u = Math.Clamp((t - T0) / (T1 - T0), 0.0, 1.0);
                double span = P1 - P0;
                v = span * (1.5 - 1.5 * u * u) / (T1 - T0);
                return P0 + span * (1.5 * u - 0.5 * u * u * u);
            }
            case SegKind.Decay:
            {
                double dt = t - T0;
                double e = Math.Exp(-K * dt);
                v = V0 * e;
                return P0 + V0 / K * (1.0 - e);
            }
            case SegKind.Spring:
                return SpringEval.CriticallyDamped(P0, P1, V0, K, Zeta, t - T0, out v);
            case SegKind.Follow:
                return FollowRing.Eval(this, t, out v);          // interpolate/extrapolate the contact ring
            case SegKind.Glide:
            {
                // Critically-damped spring to P1, velocity-continuous from V0 — same closed form as Spring
                // with Zeta pinned to 1; kept as a distinct Kind so the profile/probe can label it.
                return SpringEval.CriticallyDamped(P0, P1, V0, K, 1.0, t - T0, out v);
            }
            default: v = 0; return P0;
        }
    }
}

/// <summary>Immutable, ≤4 segments, POD — copied by value into a <see cref="PlanSlots"/> slot.</summary>
public readonly struct ScrollPlan
{
    public readonly int Vp;
    public readonly uint Gen;
    public readonly ulong Seq;
    public readonly MotionSeg S0, S1, S2, S3;
    public readonly byte Count;
    public readonly double Min, Max;
    public readonly OverpanPolicy Overpan;    // None (wheel/programmatic) | RubberBand (touch/touchpad)
    public readonly MotionKind Kind;          // Idle, Wheel, Drag, Fling, Programmatic, Thumb

    public double Eval(double t, out double v, out bool settled)
    {
        ref readonly MotionSeg seg = ref SegAt(t);
        double p = seg.Eval(t, out v);
        settled = ReferenceEquals(null, null) && t >= (Count > 0 ? LastSeg().T1 : 0) && v == 0;
        return p;
    }

    /// <summary>Coordinate-frame shift for an extent correction above the anchor: Eval'(t) = Eval(t) + delta,
    /// Min/Max shifted too. The ONLY way an extent write touches an in-flight plan.</summary>
    public ScrollPlan Shifted(double delta) => /* rebuild each segment's P0/P1 by +delta, Min/Max by +delta */ this;
}
```

`PlanAuthor` (pure statics, no instance state):
- **`WheelNotch(prev, tNotch, notchDip, feel)`** — `start = prev.Eval(tNotch)` (the *displayed* position at the
  notch's own time, never the last plan's stored destination); `dest = clamp(prevDest + notchDip·accel(gap),
  Min, Max)`; one `Cubic` segment `[tNotch, tNotch + feel.WheelDurationS]`. Spin acceleration is a pure function
  of notch-timestamp gaps (EMA). WinUiExact carries **no velocity, no floor, no landing** — every notch is a
  fresh cubic from the currently-displayed position. `Glide` expresses velocity carry purely as a different
  curve/dest rule in its `FeelProfile`, not a special case in `PlanAuthor`. Wheel never overpans.
- **`Follow(ring)`** — drag/touch contact: a ring of the last 8 `(t, pos)` samples; `Eval` interpolates between
  bracketing samples, or extrapolates at most `feel.FollowExtrapolateMaxS` (one refresh) past the last two. Past
  an edge: `RubberBand(excess) = c·excess·vp / (vp + c·excess)` (iOS `c = 0.55`), applied inside `Eval` — not a
  second pass over the result.
- **`Fling(ring, edges)`** — contact end: `V0` from the Follow ring via an impulse estimator over the last 40 ms;
  one `Decay` segment. If decay would cross `Min`/`Max`, the crossing time is solved analytically **at
  authoring time** and a `Spring` return (RubberBand policy) or `Hold` at the edge (None policy) is appended —
  there is nothing left to resolve once layout runs.
- **`Programmatic(prev, tNow, dest)`** — `Glide` from `prev.Eval(tNow)`, velocity-continuous; `Immediate` is a
  `Hold` at `dest`.
- **`Thumb(pointerPos)`** — `Hold` at the mapped position, re-authored per pointer sample.

Precision: `double` everywhere in `Scroll.Motion`; the render thread poses `float trans = (float)(WindowOrigin −
p)` — the only narrowing cast in the whole path, and it only ever sees a value relative to a moving window
origin (§6), never the raw absolute offset.

**Deletes:** `ScrollBody.cs`, `ScrollBodyOps.cs`, `ScrollPhysics.cs`, `ScrollClock.cs` (folded into
`RefreshLattice`, kept only as the present-time law), `WheelGlide.cs`, `ScrollSnap.cs`'s stateful half (the pure
snap-target math is ported into `PlanAuthor`).

---

## 4. Input — `FluentGpu.Scroll` (router/classifier), `FluentGpu.Windows.Pal` (DM producer)

```csharp
public enum ScrollSource : byte { MouseWheel, MouseWheelHiRes, Touchpad, Touch, Pen, Keyboard, Thumb, Programmatic }
public enum ScrollGesture : byte { Begin, Sample, End, Notch }

public readonly record struct ScrollInputEvent(
    ScrollSource Source, ScrollGesture Phase, long Qpc, Point2 PointerDip,
    float Dx, float Dy,           // notch units for Notch; DIP for Sample
    uint PointerId, KeyModifiers Mods);
```

One event kind replaces `Wheel`/`ScrollBegin`/`ScrollDelta`/`ScrollEnd` in `InputDispatcher`.

- **Wheel is urgent** — removed from `PacedInputWaitClassifier.IsDeferrable`
  (`src/FluentGpu.Windows/Pal/Win32Platform.cs:263-265`).
- **`WheelClassifier`** — a pure ~60-line static: device evidence → `Touchpad`; sub-notch delta → `MouseWheelHiRes`;
  else `MouseWheel`. Replaces the heuristic folded into today's `InputDispatcher` wheel case.
- **DirectManipulation kept only as a touchpad contact producer** — `Win32DirectManipulation` strips to
  `INTERACTION|TRANSLATION_X|TRANSLATION_Y`, **no `TRANSLATION_INERTIA`** (fixes the `UseOsInertiaStopFallback =
  true` bug at `Win32DirectManipulation.cs:81`), no wedge ladder (`EngageWedge`/`Suspended`/`InertiaStall`/
  `SilentOwner` recovery states — all deleted, they exist only because inertia used to live outside the engine).
  Falls back to hi-res wheel if no `RUNNING` transition arrives within 250 ms. ~946 LOC → ~300 LOC.
- **`ScrollRouter`** (UI thread) — hit-tests the last layout, picks the nearest scroller on the axis that can
  still move at `tNow`, else the outermost; latches (wheel until 300 ms of notch silence; a contact `Begin`→`End`
  span); chains to the parent only when the latched child is at its edge **and** the gesture started at that
  edge (WinUI-style shelf handoff — a shelf never hands off mid-spin); `Shift+wheel` routes horizontal.
- **Keyboard**: arrows = 56 DIP `Glide`; `Page` = `0.875·viewport` `Glide`; `Home`/`End` jump.
- **Scrollbar**: thumb drag = `Hold` per pointer sample; track click = page `Glide`; arrow buttons = line `Glide`.

**Deletes:** the wheel/ScrollBegin/Delta/End cases and the held-notch defense in `InputDispatcher`, the DM pacer
+ inertia ladder in `Win32DirectManipulation`, `InputDispatcher`'s scrollbar-drag/nearest-scrollable/keyboard-scroll
special cases (→ `ScrollRouter`).

---

## 5. Render-side posing — `ScrollPoser` (render thread, zero-alloc, capacity 64)

```csharp
namespace FluentGpu.Scroll;

/// <summary>Render-thread-owned. Rides the existing RenderCompositorAnimations.Adopt/Tick overlay seam
/// (AppHost.cs:1191-1197) — that call site's nowMs becomes RenderClock.PresentSec(tickQpc) for BOTH this and
/// ordinary animations. Replaces RenderScrollLease.</summary>
public sealed class ScrollPoser
{
    public const int Cap = 64;   // was RenderScrollLease.Cap = 32 (owner decision, §0)

    public void Adopt(in ScrollCoverageSnapshot cov, SceneRecordingSnapshot scene);

    public bool Tick(PlanSlots slots, SceneRecordingSnapshot scene, double presentSec, ScrollProbe probe)
    {
        // for each live slot:
        //   p  = slots.Read(vp).Eval(presentSec, out v, out settled);
        //   p' = Math.Clamp(p, cov.Start, cov.End - cov.Viewport);
        //   if (p' != p) probe.CoverageClamp(vp, presentSec, p, p');   // fails gate.scroll.no-blank
        //   trans = DevicePixelSnap((float)(cov.WindowOrigin - p'), scale);
        //   CompositorPaint(cov.ContentNode).Transform = Translate(trans);
        //   ScrollEffectEval evaluates every ScrollEffect row scoped to vp against p' (sticky/parallax/thumb);
        //   feedback[vp] = (p', v, settled);   // read back by ScrollHandle/ScrollMotionState next UI frame
        return HasActive;
    }

    public ReadOnlySpan<ScrollPose> Feedback { get; }
}

public readonly record struct ScrollCoverageSnapshot(
    int Vp, NodeHandle ContentNode, double WindowOrigin,
    double Start, double End, double Viewport, double ExtentTotal, NodeHandle EffectRange);
```

`ScrollCoverageSnapshot` is `RenderScrollLease`'s slot shape reborn without the lease/grant/revoke protocol — the
render thread always has a plan for a bound viewport, so there is nothing to grant. Translated span replay
(`SceneRecorder.cs:1529-1619`) is unchanged in mechanism and now covers *every* scroll frame (not just leased
ones): a scroll tick re-records only edge rows, everything mid-viewport is a rebased copy.
`PresentCadence.Decide` stays the sole presenter; `MotionDue = poser.HasActive || animations.HasActive`.

**Deletes:** `RenderScrollLease.cs`, `ScrollLease.cs`, `ScrollCommandPort.cs` (→ `PlanSlots`, §4/§6 wiring),
`ScrollActivity.cs`, `ScrollAnchoring.cs`'s stateful half (pure anchor math ports into `Virtualizer`, §6). The
`AppHost.cs:676-680` dt-repair fields (`_prevScrollNowQpc`/`_scrollDtRepairedThisFrame`/`_scrollDtRepairs`) are
deleted outright — there is no `dt` left to repair. `AppHost.cs:3861-3908`'s scroll-coincident-reconcile→snap
FLIP-suppression latch is replaced by the single `AnyUserScrollMoving` 2-frame latch (§7).

---

## 6. Content / virtualization

```csharp
namespace FluentGpu.Scroll;

public interface IExtentSource
{
    int Count { get; }
    double Total { get; }
    double OffsetOf(int i);                 // O(1) fixed, O(log n) Fenwick measured
    int IndexAt(double off);
    /// <summary>Returns the delta applied ABOVE anchorIndex (0 if i >= anchorIndex) — the ONE extent write path.</summary>
    double SetMeasured(int i, double extent, int anchorIndex);
}

public sealed class FixedExtent : IExtentSource     { /* n · stride, no table */ }
public sealed class MeasuredExtent : IExtentSource  { /* Fenwick tree over doubles; estimate for unmeasured rows */ }
```

`Virtualizer` (UI thread, one instance per bound viewport) replaces `FlexLayout.cs:888-1268`:

```csharp
public sealed class Virtualizer
{
    public void RunFrame(double presentSec, ScrollPlan plan, IExtentSource extent, ScrollFeel feel)
    {
        double p = plan.Eval(presentSec, out double v, out _);
        double overscanPx = Math.Clamp(Math.Abs(v) * feel.LookaheadS, feel.OverscanMinPx, feel.OverscanMaxPx);
        // asymmetric toward motion: more overscan ahead of v, a fixed floor behind
        int first = extent.IndexAt(p - feel.OverscanBehindPx);
        int last  = extent.IndexAt(p + Viewport + overscanPx);

        RealizeAll(first, last);   // every covered row, every frame — no ramp, no budget, no cap

        double windowOrigin = extent.OffsetOf(first);
        double delta = ArrangeRelativeTo(windowOrigin, first, last);   // measured correction above anchor
        if (delta != 0)
        {
            slots.Shift(vp, delta);           // plan.Shifted(delta), same frame
            windowOrigin += delta;
        }
        PublishCoverage(new ScrollCoverageSnapshot(vp, contentNode, windowOrigin,
            extent.OffsetOf(first), extent.OffsetOf(last + 1), Viewport, extent.Total, effectRangeNode));
    }
}
```

`ListRowEl` — one engine node per flat row, replacing per-item component trees for the two hot lists (track
tables, episode/track shelves):

```csharp
public readonly struct RowCell { public RectF Rect; public CellKind Kind; public TextRunHandle Text; public ImageHandle Image; public ColorRole Color; public bool Truncated; }
public enum CellKind : byte { Text, Image, Glyph, Rect }

public sealed class RowPaint
{
    public const int MaxCells = 8;
    public RowCell Cell0, Cell1, Cell2, Cell3, Cell4, Cell5, Cell6, Cell7;
    public byte CellCount;
    public bool Placeholder;   // final height, no type swap, no remount — same row, cells painted as grey bars
}
```

ASCII wireframe of a `ListRowEl`-backed track row (Wavee `Track.Table`), 8 cells + their hit rects:

```
 0        44          360                       560       640   700         820          900   960
 ├────────┼───────────┼─────────────────────────┼─────────┼─────┼───────────┼────────────┼─────┤
 │ #/▶ [0]│ art [1]    │ title [2]  ·  artist[3] │album[4] │♡[5] │ ⋯⋯ dur[6] │  ···   [7] │     │
 │ Glyph  │ Image      │ Text (2-line clamp)     │ Text    │Glyph│ Text     │ Glyph      │     │
 └────────┴────────────┴─────────────────────────┴─────────┴─────┴──────────┴────────────┴─────┘
   hit: play/pause        hit: open track            hit: open album  hit: like  —     hit: on-media menu
```

- One `SceneRecorder` span per row, ≤8 binds (one per non-Rect cell), pooled and **pre-built at mount** — slot
  reuse on recycle is a signal write, never a remount.
- **`Placeholder`** is the same row struct with `Placeholder = true`: final row height, cell rects unchanged,
  cells paint as grey bars. No type swap, no `Component` mount/unmount on placeholder↔real transitions — this is
  what makes "realize all covered rows every frame" affordable during a 100k-row fling.
- Rich rows (drawer, episode with description) stay ordinary component rows; `ListRowEl` is additive, not a
  replacement for every list in the app.

**Deletes:** `FlexLayout.cs:888-1268` (virtual-arrangement section, including `PostAnchorShiftAndFrame` at
1230-1263 and the `UsesMeasuredExtent` gate at 1266-1268), `ColdRealizeRamp.cs` and its consumption in
`Reconciler.cs:280-325`, `FrameBudget.cs` (whole file), `LazyGrid.cs`'s `LazyGridDrip` static class (`LazyGrid.cs:138`,
call sites at 414/427/431), `VirtualListEl`'s `KeepAliveCap` (`VL.cs:73`), `Overscan`/`RealizeOverscanImmediately`
(`VL.cs:38-45`), `StaggerColdRealize` (`VL.cs:79`) — all four become the single velocity-sized overscan above.

---

## 7. Motion signal

```csharp
public readonly record struct ScrollMotionState(MotionKind Kind, float SpeedDipPerS, bool UserDriven);
```

One per bound viewport, produced from `ScrollPoser.Feedback`; `AppHost` exposes `AnyUserScrollMoving` (true this
frame or last). **Only three consumers remain:**
- FLIP suppression — a 2-frame latch (replaces `AppHost.cs:3861-3908`'s scroll-coincident-reconcile→snap logic).
- `PagedShelf` (page-dot suppression while the shelf itself is mid-scroll).
- Lyrics follow (§9 — `Lyrics.UI.cs`'s bespoke resync state machine, replaced by `ScrollTo(..., Follow)`).

Every other scroll-reactive consumer today stops reacting to scroll entirely:
- `ImageCache.ScrollThrottled`/`DecodeScheduler.ScrollThrottled` (`ImageCache.cs:679-681`,
  `DecodeScheduler.cs:63-95,257-287`) — **deleted**, replaced by a permanent (not scroll-keyed) bytes/frame
  upload cap sized from the real GPU upload budget.
- `AcrylicScrollHold` (`AcrylicScrollHold.cs`, `ScrollRefreshCadence = 4`) — rewritten as
  **`AcrylicRefreshCadence`**, damage-keyed (refreshes on real damage overlap, not on a scroll-tick counter).
- The `D3D12Device` blur-pin scroll hold (`D3D12Device.cs:4058-4104,4151-4172`,
  `SelfBlurHoldAfterScrollTicks`/`_selfBlurHoldUntil` at `AppHost.cs:1538,4193`) — rewritten damage-keyed, same
  pattern as `AcrylicRefreshCadence`.
- `Reconciler.AncestorScrollLive` (`R.cs:418,3205`), `PeekMainScrollBusy` (`R.cs:760,963`), `ScrollMemory`
  (`R.cs:520-544,2962-3000,4523`) — **deleted**; `ScrollMemory`'s job (position restore across mount/unmount)
  moves to `ScrollHandle.Restore` (§9), the only place it belongs since it is app-visible state, not a
  reconciler-internal one.

---

## 8. Scroll-linked effects — `FluentGpu.Scroll.Effects`

```csharp
public enum EffectChannel : byte { TransX, TransY, Opacity, ClipTop, ScaleXY, ThumbPos }
public enum EffectKind : byte { Map, Sticky, StickyClip, Thumb }

public readonly record struct ScrollEffect(
    EffectChannel Channel, EffectKind Kind, double In0, double In1, float Out0, float Out1,
    Easing Ease, float Inset, int ScopeNode);

public readonly record struct EffectGeometry(
    double NodeY, double NodeH, double ScopeEnd, double Extent, double Viewport, float Track, float ThumbLen);

public static class ScrollEffectEval
{
    public static float Evaluate(in ScrollEffect e, double offset, in EffectGeometry g) => e.Kind switch
    {
        EffectKind.Map       => Ease(e.Ease, InverseLerp(e.In0, e.In1, offset)) * (e.Out1 - e.Out0) + e.Out0,
        EffectKind.Sticky    => (float)Math.Clamp(offset + e.Inset - g.NodeY, 0, g.ScopeEnd - g.NodeH - g.NodeY),
        EffectKind.StickyClip=> (float)Math.Max(0, offset + e.Inset - g.NodeY),
        EffectKind.Thumb     => (float)(offset / (g.Extent - g.Viewport) * (g.Track - g.ThumbLen)),
        _ => 0f,
    };
}
```

Same arithmetic runs on both threads — UI for hit-test/a11y and edge-flag signals, `ScrollPoser` for pixels —
against the *same* `p'`. Sticky clamps to a **declared scope ancestor** (`ScopeNode`), which removes the
`Track.Table.cs` `HeroRoot` workaround (§10). Element API (Wave 1): `el.OnScroll(ScrollEffect.Sticky(top: 56,
scope: "hero"))`, `.Parallax(...)`, `.Fade(...)`; edge flags surface as `Signal<bool>` derived from poser
feedback.

**Deletes:** `ScrollBindDsl`, `ScrollBind.cs`, `ScrollBindEval.cs`, `Dsl/ScrollBindDsl`, `ScrollEdgeCues`,
`Hooks/ScrollHooks` — replaced by `ScrollEffect` + `ScrollEffectEval` + the `.OnScroll`/`.Parallax`/`.Fade`
element API. The named-scroll-timeline mechanism (`generic-hookable-scroll-engine-design.md` §5.1,
`ScrollBindDsl.Timeline`) is ported forward as a named `ScopeNode` lookup rather than a separate publish/consume
protocol — a backdrop sibling names the scope it tracks instead of a scroller publishing under a name.

---

## 9. App surface — `ScrollHandle`

```csharp
public sealed class ScrollHandle
{
    public IReadSignal<double> Offset { get; }
    public IReadSignal<ScrollMotionState> Motion { get; }
    public double Extent { get; }
    public double Viewport { get; }

    public void ScrollTo(double offset, ScrollMove move = ScrollMove.Glide);      // Glide | Immediate | Follow
    public void ScrollBy(double delta, ScrollMove move = ScrollMove.Glide);
    public void BringIntoView(NodeHandle node, float align = float.NaN, ScrollMove move = ScrollMove.Glide);
    public void Restore(double offset);   // latched until extent can hold it (ScrollKey) — absorbs ScrollMemory
}
```

`IScrollController` becomes a thin adapter over `ScrollHandle` (unifies today's 11 controller call sites).
Lyrics follow becomes `ScrollTo(target, ScrollMove.Follow)` — deleting `Lyrics.UI.cs`'s bespoke `FollowMode`/
`FollowArm`/`FollowIntent` state machine (`Lyrics.UI.cs:139-143`, `SetFollowMode`/`ResetFollowState`/
`TickFollowState`/`BeginResync`/`DriveResync` at 1198-1251 — the "resync glide" the code's own comment at 1205
names). `Detail.ScrollSpy`'s duplicate mechanism (`Detail.ScrollSpy.cs`, consumed separately by `Detail.UI.Hero.cs`
and Artist's own `Detail.BandLayout.ActiveSection`) collapses onto one `ScrollHandle.Offset` reader per detail
page instead of two independent spy implementations.

**Scrollbars:** the thumb is `ScrollEffect.Thumb`, posed render-side — it never lags. Chrome fade/expand is a
pure `ScrollBarTimeline` carrying the WinUI timings from today's `ScrollBarChrome.cs`
(`ExpandBeginMs = 400`, `ContractBeginMs = 500`, fade `83 ms`).

---

## 10. Diagnostics — `FluentGpu.Scroll.Diag`, always compiled

```csharp
public enum ProbeLevel : byte { Off, Summary, Trace }

public static class ScrollProbe
{
    public static volatile ProbeLevel Level;   // set from the Diagnostics page; no #if, no env var

    public static void Input(in ScrollInputEvent e);
    public static void Plan(int vp, in ScrollPlan plan, double authoredAtSec);
    public static void Pose(int vp, long presentQpc, double p, double v, bool clamped, float snappedTrans);  // render thread, own SPSC ring
    public static void Coverage(int vp, double start, double end, double origin, int first, int last);
    public static void Extent(int vp, int index, double delta, bool anchoredSameFrame);
    public static void Cost(ProbePhase phase, int vp, long qpcTicks, int rows, int nodes);
    public static BurstSummary EndBurst();
    public static void ExportCsv(TextWriter w);   // qpc_ms,pane,kind,offset,delta,rendering_time_ms,device,note
}                                                  // pane=Wavee; kind maps to analyze.py notch|frame|raw_wheel|state_changed
```

Two fixed POD rings (UI 8k entries, render 8k entries); `Level == Off` costs one volatile byte read per call
site. `ScrollTunables` is a registry of `TunableF{Name, Min, Max, Default}` over one `ScrollFeel` struct behind a
seqlock; `FeelProfile` presets `WinUiExact{32 DIP, 0.257 s, cubic}`, `Glide{56 DIP, 0.40 s, cubic+carry}`,
`Snappy`; JSON round-trips through app settings.

**Diagnostics page — the "Scroll" card** (Wavee, `Screens/Diagnostics.UI.cs`, same `Card(title, rows)` idiom used
by every other card on the page):

```
┌─ Scroll ──────────────────────────────────────────────────────────────────┐
│  Level   [ Off | Summary | Trace ]         (Segmented.Create)             │
│                                                                            │
│  Profile [ WinUiExact ▾ ]   presets: WinUiExact · Glide · Snappy          │
│                                                                            │
│  WheelNotchDip     ├───────●───────────────┤  32.0                        │
│  WheelDurationS    ├──●─────────────────────┤  0.257                      │
│  LookaheadS        ├────●───────────────────┤  0.12                       │
│  OverscanMinPx / MaxPx  ├──●───┤ ├────●─────┤  96 / 640                  │
│  FollowExtrapolateMaxS  ├●─────────────────┤  0.0167                      │
│                                                                            │
│  [ Export CSV ]                              [ HUD ▢ ]                   │
│  ┌ HUD (last 240 poses) ──────────────────────────────────────────────┐  │
│  │ ▁▂▃▅▇█▇▅▃▂▁▁▂▃▅▇▇▅▃▂▁ … offset/velocity strip, clamp ticks in red │  │
│  └──────────────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────────────┘
```

`Diagnostics.ScrollTrace.cs`'s verdict engine (per-frame `liveMissed`/`liveZero`/dips computed from
`FrameStats.ScrollLiveMotion`/`ScrollDeltaDip`/`WheelNotches`) is **deleted**; its useful figures move into
`BurstSummary`. `FluentApp`'s `[scrollperf]` log tag (`FluentApp.cs:436,555`) is deleted, replaced by the probe's
`BurstSummary` line.

---

## 11. Full deletion map

| Area | Fate |
|---|---|
| `Scroll/ScrollBody.cs`, `ScrollBodyOps.cs`, `ScrollPhysics.cs`, `ScrollClock.cs`, `WheelGlide.cs`, `ScrollSnap.cs` (stateful half), `ScrollLease.cs`, `RenderScrollLease.cs`, `ScrollCommandPort.cs`, `ScrollActivity.cs`, `ScrollAnchoring.cs` (stateful half), `ScrollKernel.cs`, `ScrollController.cs`, `ScrollContentTransform.cs`, `ScrollInput.cs` (old event shape), `ScrollInputRouter.cs` (old), `IScrollSink.cs`/`SceneScrollSink.cs` (~21 files, ~5.7k LOC) | deleted → `Scroll.Motion` (§3) / `ScrollRouter` (§4) / `PlanSlots` / `ScrollHandle` (§9) / `Virtualizer` (§6) / `IExtentSource` (§6) / `ScrollPoser` (§5) / `Scroll.Effects` (§8) / `ScrollProbe` (§10) |
| `Animation/ScrollBind.cs`, `ScrollBindEval.cs`, `Dsl/ScrollBindDsl`, `ScrollEdgeCues`, `Hooks/ScrollHooks` | deleted → `ScrollEffect`/`ScrollEffectEval` (§8) |
| `Foundation/ScrollTrace`, `ScrollLog` (env-var knobs) | deleted → `ScrollProbe` (§10) |
| `RetainedScroll`, `RenderScrollClock`, `SceneRecordingSnapshot.RenderScroll`/`.Parity` | deleted → table shape becomes `ScrollCoverageSnapshot` (§5) |
| `AppHost.cs:676-680` (`_prevScrollNowQpc`/dt-repair fields), `:3861-3908` (FLIP-suppression latch), `:1538,4193` (`SelfBlurHoldAfterScrollTicks`), `:4190-4203` (scroll `FrameStats` fields) | deleted → `AnyUserScrollMoving` (§7) + `ScrollProbe` (§10); damage-keyed blur/acrylic refresh (§7) |
| `Reconciler.cs:418` (`AncestorScrollLive`), `:760,963` (`PeekMainScrollBusy`), `:520-544,2962-3000,4523` (`ScrollMemory`), `:280-325` (`ColdRealizeRamp` consumption) | deleted → `ScrollHandle.Restore` (§9) for the memory; realize-all removes the rest (§6) |
| `Scene/ColdRealizeRamp.cs`, `Hosting/FrameBudget.cs` (whole files) | deleted (§6) |
| `Controls/LazyGrid.cs`'s `LazyGridDrip` static class | deleted → `Virtualizer`'s velocity overscan (§6) |
| `Reconciler/VirtualListEl.cs`: `KeepAliveCap`, `Overscan`/`RealizeOverscanImmediately`, `StaggerColdRealize` | deleted → velocity overscan (§6) |
| `Render/SceneRecorder.cs`: `ScrollLeaseCapture` struct, `holdSelfBlurForAnyUserScroll` param, `directMovingScrollContent`/`IsDirectMovingScrollContent` | deleted → translated-span replay applies unconditionally to every scroll frame (§5) |
| `D3D12/D3D12Device.cs:4058-4104,4151-4172` blur-pin scroll hold; `Render/AcrylicScrollHold.cs` | rewritten damage-keyed; renamed `AcrylicRefreshCadence` (§7) |
| `Media/Images/DecodeScheduler.cs` `ScrollThrottled`/`ScrollApplyBytesPerFrame`; `Scene/ImageCache.cs` `ScrollThrottled` | deleted → permanent bytes/frame upload cap (§7) |
| `Layout/FlexLayout.cs:888-1268` (virtual arrangement, incl. `PostAnchorShiftAndFrame`) | deleted → `Virtualizer` (§6) |
| `Windows/Pal/Win32DirectManipulation.cs` pacer + inertia ladder (`EngageWedge`/`Suspended`/`InertiaStall`/`SilentOwner`) | rewritten as a stripped contact producer, ~946 → ~300 LOC (§4) |
| `Windows/Hosting/FluentApp.cs:436,555` `[scrollperf]` log lines | deleted → `BurstSummary` (§10) |
| `InputDispatcher` scrollbar-drag / nearest-scrollable / keyboard-scroll cases | → `ScrollRouter` (§4) |
| Controls: `ItemsView`, `LazyGrid`, `PagedShelf`, `ScrollBar`, `AnnotatedScrollBar`, `IScrollController` | rewritten over `ScrollHandle` + `ListRowEl` (§6, §9) |
| App `Entities/Track.Table.cs:1301,1331,1392-1401` (`HeroRoot`) | deleted → `ScrollEffect.Sticky`'s declared `ScopeNode` (§8) |
| App `Entities/Detail.UI.Hero.cs` binds, `Entities/Detail.ScrollSpy.cs` duplicate spy | rewritten over one `ScrollHandle.Offset` reader (§9) |
| App `Shell/Lyrics.UI.cs:139-143,1198-1251` (bespoke follow/resync glide) | deleted → `ScrollTo(..., Follow)` (§9) |
| App `Screens/Diagnostics.ScrollTrace.cs` (verdict engine) | deleted → `BurstSummary` (§10) |
| `FluentGpu.VerticalSlice`'s `ScrollKernelSuite`, `ScrollLeaseSuite`, scroll parts of `ScrollSuite`/`TouchSuite`, the four scroll xUnit files in `Engine.Tests` | deleted with their subjects → new suites, §12 |

---

## 12. Band-aid ledger

Every entry below is deleted outright, not deprecated. "Real fix" names the principle in §1–§9 that makes the
band-aid unnecessary.

| Band-aid | Where | Why it existed | Real fix |
|---|---|---|---|
| dt-repair (`_prevScrollNowQpc`/`_scrollDtRepairedThisFrame`/`_scrollDtRepairs`) | `AppHost.cs:676-680` | A repeated frame stamp corrupted the kernel's per-tick `dt` integration, so a wall-clock rebuild patched it after the fact | `p(t)` is stateless; there is no `dt` to repair (§1 #1, §3) |
| `FrameBudget` (`MotionUiSliceMs=3f`, `Arm`/`Disarm`/`DeadlineTicks`) | `Hosting/FrameBudget.cs` (whole file), armed at `AppHost.cs:680` | Bounded realize + image-apply cost during Drag/Ballistic because rows were too expensive to realize in full | `ListRowEl` makes rows cheap; realize every covered row every frame (§1 #4, §6) |
| FLIP-suppression via scroll-coincident-reconcile→snap latch | `AppHost.cs:3861-3908` | Ad hoc detection of "a FLIP shouldn't run mid-scroll" grown around the reconcile path | One `ScrollMotionState`-derived 2-frame latch (§7) |
| `SelfBlurHoldAfterScrollTicks` / `_selfBlurHoldUntil` (~0.12 s hold) | `AppHost.cs:1538,4193` | Held a blur-pin/acrylic snapshot stable for a fixed tail after any user scroll to avoid a visible re-blur | Damage-keyed `AcrylicRefreshCadence` — refresh on real damage overlap, not a scroll-tick timer (§7) |
| `Reconciler.AncestorScrollLive` | `Reconciler.cs:418,3205` | Let descendant work defer/gate itself when an ancestor viewport was scrolling | Rows are cheap; nothing downstream needs to know an ancestor is scrolling (§1 #4, §7) |
| `Reconciler.PeekMainScrollBusy` | `Reconciler.cs:760,963` | A host-wired `Func<bool>` so the reconciler could peek whether the main viewport was busy | Same as above — no consumer left once realize-all lands |
| `Reconciler.ScrollMemory` (private class + `_scrollMem`) | `Reconciler.cs:520-544,2962-3000,4523` | Persisted/restored scroll position keyed by (tab × page-slot) across mount/unmount, inside the reconciler | `ScrollHandle.Restore` — app-visible state belongs on the handle, not buried in the reconciler (§7, §9) |
| `FlexLayout.PostAnchorShiftAndFrame` + sub-pixel noise gate | `FlexLayout.cs:1230-1263` (gate at 1256) | Two writers (extent correction, kernel integration) raced the offset; the noise gate papered over the resulting jitter | One `ExtentService`/`Virtualizer` writer; a correction shifts the plan's frame in the same call (§1 #5, §6) |
| `ScrollLeaseCapture` / `holdSelfBlurForAnyUserScroll` / `directMovingScrollContent` | `SceneRecorder.cs:86-111,134,143,818,828,892,911,924,1460,1535,1617-1619,2947` | Gated span-reuse/translated-copy eligibility on whether a lease was live and whether self-blur should hold during scroll | Translated-span replay applies unconditionally to every scroll frame — no lease protocol needed once the render thread always has a plan (§5) |
| D3D12 blur-pin scroll hold (`BlurPinKey`/`CanUseStationaryCache`) | `D3D12Device.cs:4058-4104,4151-4172` | Position-independent pin key kept a blurred snapshot stable while its source scrolled, gated by the AppHost self-blur hold | Damage-keyed refresh, same principle as `AcrylicRefreshCadence` (§7) |
| `ImageCache.ScrollThrottled` / `DecodeScheduler.ScrollThrottled` + `ScrollApplyBytesPerFrame=512KB` | `ImageCache.cs:679-681`, `DecodeScheduler.cs:63-95,257-287` | Capped image-apply bytes/frame specifically while scrolling, to protect frame time from a burst of newly-realized rows | A permanent (not scroll-keyed) upload cap sized from the real GPU budget — the cap is real, the scroll-keying was the band-aid (§7) |
| `AcrylicScrollHold` (`ScrollRefreshCadence=4`) | `Render/AcrylicScrollHold.cs` (whole file, cadence at :37) | Refreshed a retained acrylic snapshot only every 4th tick during scroll to bound re-blur cost | `AcrylicRefreshCadence`, damage-keyed instead of tick-counted (§7) |
| `ColdRealizeRamp` (`NodeBudget`/`FrameBudgetMs`/`GrowShareOfFrame`/`CanCreateAnother`/`Target`) | `Scene/ColdRealizeRamp.cs` (whole file), consumed at `Reconciler.cs:280-325` | Throttled how many rows a cold virtual window could realize per frame, budgeted by measured node/ms-per-row | Cheap rows realize the whole window in one frame; no ramp needed (§1 #4, §6) |
| `LazyGridDrip.ClampWindow`/`Apply` | `Controls/LazyGrid.cs:138,414,427,431` | Grew/clamped the realized row window during scroll to avoid snapping the whole remaining window on an unchanged-offset frame | Velocity-sized overscan computed directly from `v` every frame — no incremental "drip" needed (§6) |
| `VirtualListEl.Overscan=4`/`RealizeOverscanImmediately` | `VirtualListEl.cs:38-45` | A small, fixed row-count overscan, too small for a fast fling and wasteful at rest | `overscanPx = clamp(\|v\|·lookaheadS, min, max)` — sized to the actual velocity (§6) |
| `VirtualListEl.KeepAliveCap=8` | `VirtualListEl.cs:73` | Bounded how many off-window rows kept live state, trading correctness (state loss) for memory | Not needed once row realize is cheap and unbounded within the covered window; keep-alive scope narrows to genuinely off-window rows only (§6) |
| `VirtualListEl.StaggerColdRealize` | `VirtualListEl.cs:79` | Spread cold-mount realize work across frames to avoid a first-frame spike | `ColdRealizeRamp` deletion removes the reason to stagger — the first frame realizes its whole window (§6) |
| `[scrollperf]` log tag | `Windows/Hosting/FluentApp.cs:436,555` | Ad hoc always-on log line reporting frames/clipE/clipD/fullHide counters during scroll | `ScrollProbe.BurstSummary`, structured and level-gated (§10) |
| `Diagnostics.ScrollTrace.cs` verdict engine | `Wavee/Screens/Diagnostics.ScrollTrace.cs` | App-side computation of `liveMissed`/`liveZero`/dips verdicts from `FrameStats` scroll fields | `ScrollProbe.BurstSummary` computes the same figures once, in the engine, off the real plan/pose stream (§10) |
| `Track.Table.HeroRoot` | `Wavee/Entities/Track.Table.cs:1301,1331,1392-1401` | A raw `BoxEl` wrapper worked around `ScrollBindEval.ApplyPin` clamping to the immediate parent only, because a bind couldn't sit on a component's rendered root | `ScrollEffect.Sticky` clamps to a declared `ScopeNode`, so any element — component root or not — can be the pin target (§8) |
| `Detail.ScrollSpy` duplicate mechanism | `Wavee/Entities/Detail.ScrollSpy.cs`, plus Artist's separate `Detail.BandLayout.ActiveSection` | Two independent "which section is active" trackers because there was no single cheap offset reader to share | One `ScrollHandle.Offset` reader per detail page (§9) |
| Lyrics bespoke follow/resync glide (`FollowMode`/`FollowArm`/`FollowIntent`, `TickFollowState`/`BeginResync`/`DriveResync`) | `Wavee/Shell/Lyrics.UI.cs:139-143,1198-1251` | Hand-rolled state machine to fight or yield to user scroll while auto-following playback position, with bespoke spring constants | `ScrollHandle.ScrollTo(target, ScrollMove.Follow)` — velocity-continuous follow is now a first-class move kind (§9) |

---

## 13. Test strategy and gates

**Pure unit tests (`FluentGpu.Engine.Tests`, `FluentGpu.Windows.Tests`):**
- `MotionSeg.Eval`/`ScrollPlan.Eval` are continuous across segment boundaries.
- Two evaluators (e.g. UI-side `Virtualizer` read and render-side `ScrollPoser` read) at arbitrary time sets
  agree exactly — `gate.scroll.dual-eval-identical`.
- `PlanAuthor.WheelNotch`'s replan matches the WinUI cubic within 5 % — `gate.scroll.wheel-notch-dip`.
- `PlanAuthor.Fling`'s edge crossing is pre-computed analytically, never resolved after layout —
  `gate.scroll.fling-edge-precomputed`.
- `ScrollPlan.Shifted` preserves `Eval − delta` at every sample time — `gate.scroll.shifted-preserves-eval`.
- `WheelClassifier` and `ScrollRouter` latch/chain decision tables — `gate.scroll.classifier-table`,
  `gate.scroll.router-latch-chain`.
- `ScrollTunables` round-trip through JSON byte-for-byte — `gate.scroll.tunables-json-roundtrip`.

**`FluentGpu.VerticalSlice` — new `ScrollMotionSuite`** (replaces `ScrollKernelSuite`/`ScrollLeaseSuite`/the
scroll parts of `ScrollSuite`/`TouchSuite`):
- `no-blank`: fling at 8000 DIP/s over 100k rows produces **zero** `ScrollProbe.CoverageClamp` events —
  `gate.scroll.no-blank`.
- `extent-stable`, `anchor-holds`: a measured-extent correction above the anchor never moves the anchor row on
  screen — `gate.scroll.anchor-holds`.
- `precision-deep`: at 5.6M DIP cumulative scroll, positional error stays under 0.01 px —
  `gate.scroll.precision-deep`.
- `100k-flat-zero-alloc`: a 100k-row fling allocates zero bytes on the render thread —
  `gate.scroll.render-alloc-zero`.
- `notch-to-present`: a wheel notch reaches a posed pixel in at most 2 presents — `gate.scroll.notch-to-present`.
- `sticky-grid`: the UI-side and render-side `ScrollEffectEval` evaluators agree bit for bit —
  `gate.scroll.effect-dual-eval`.

**Golden replay:** `ScrollProbe.ExportCsv` output fed headlessly through `analyze.py`, must stay within 5 % RMS
of the WinUI 3 pane capture — `gate.scroll.golden-replay`.

**In vivo (Wavee, packaged build):** the probe replays live onto a playlist, an artist page, and a 10k+ list;
`ExportCsv` → `analyze.py` against the WinUI capture. Acceptance: jitter ≤ 0.15, late = 0, clamps = 0 — the
owner's side-by-side verdict is final.

**`Wavee.Tests`:** the rewritten `ScrollSpy` view-model, Lyrics follow decisions (pure, engine-free per
`CLAUDE.md`'s no-source-text-tests rule — extract into a decision class the way `SetupGating`/`AppUpdateToasts`
already do), the Scroll card view-model, `ScrollTunables` persistence round-trip.

---

## 14. Waves

Parallel Sonnet agents work on disjoint files within a wave; **only the orchestrator builds and tests**. Every
wave ends green: `dotnet build src/FluentGpu.slnx` Debug and Release, a full `FluentGpu.VerticalSlice` run,
`Engine.Tests` and `Windows.Tests`, then `dotnet build Wavee.slnx` Debug and Release and `Wavee.Tests`. After
Wave 1 and each later wave, an arm64 NativeAOT publish with symbols goes to a side folder.

**Wave 0 — pure foundations (~2.5k LOC, nothing old touched).** Agents on disjoint new files:
- (a) `Scroll.Motion`: `MotionSeg`, `ScrollPlan`, `PlanAuthor`, `SpringEval`, `FollowRing` + unit tests (§3, §13).
- (b) `ScrollProbe`/`ScrollTunables`/`FeelProfile` + CSV export (§10).
- (c) `ScrollEffect` + `ScrollEffectEval` (§8).
- (d) `IExtentSource`/`FixedExtent`/`MeasuredExtent` on the existing Fenwick, now over `double` (§6).
- (e) `ListRowEl` + `RowPaint` + the recorder fast path (§6).
- (f) this design doc (already written here) plus the superseded banners (§15).

**Green criterion:** every new file compiles standalone in `FluentGpu.Engine`, unit tests from §13's pure list
pass, nothing else in the tree changed.

**Wave 1 — cut-over (~6k deleted / ~3.5k added).** Order: **(a) → (b)/(c)/(d)/(e) → (f)**.
- (a) delete the old `Scroll/*` stack (§11 row 1); add `PlanSlots`, `ScrollRouter`, `WheelClassifier`,
  `ScrollHandle`.
- (b) `ScrollPoser` + `ScrollCoverageSnapshot` + `RenderThread`/`AppHost.cs:1191-1197` wiring (present-time
  clock replaces `nowMs` for both scroll and ordinary animation) (§5).
- (c) `Virtualizer` + the `FlexLayout.cs:888-1268` cut + the `Reconciler.cs` ramp/budget deletions (§6, §11).
- (d) the PAL event (`ScrollInputEvent`), wheel urgency, DM rewrite (§4).
- (e) Controls (`ItemsView`, `LazyGrid`, `PagedShelf`, `ScrollBar`, `AnnotatedScrollBar`, `IScrollController`)
  rewritten over `ScrollHandle` + `ListRowEl` (§6, §9, §11).
- (f) app mechanical migration: the 102 sticky/effect sites, 11 controller call sites, 17 `OnScroll`, 9
  `ScrollIntoView`, `TableHost`'s scope wiring for `HeroRoot`'s replacement, Lyrics `Follow` (§9, §11).

**Green criterion:** the deletion map (§11) is fully applied — no reference to a deleted type remains; every
`gate.scroll.*` unit and VerticalSlice gate from §13 that doesn't depend on Wave 2/3 content passes.

**Wave 2 — content cost (~2.5k).**
- Wavee track rows → `ListRowEl`, pooled `TableSlot`, placeholder-in-paint (§6, App `Track.Table.cs` rewrite).
- Slot pre-build + velocity overscan wired end-to-end.
- Delete image `ScrollThrottled` + land the permanent upload cap (§7, §11).
- De-scroll blur pins / acrylic → `AcrylicRefreshCadence` (§7, §11).

**Green criterion:** `gate.scroll.no-blank`, `gate.scroll.render-alloc-zero`, `100k-flat` VerticalSlice runs pass
against real Wavee track/episode lists.

**Wave 3 — diagnostics surface + acceptance (~1.5k).**
- The Scroll card + persistence (§10).
- Delete `Diagnostics.ScrollTrace.cs` (§11).
- `gate.scroll.golden-replay` wired to `analyze.py`.
- `FrameStats` cleanup (remove the scroll fields listed in §11).
- The owner doc `docs/design/subsystems/scroll.md` + a `SPEC-INDEX.md` row for the engine-owned-scroll contract
  (today's row 80, "Engine-owned scroll + measured wheel distance", is rewritten to point here).

**Green criterion:** `check-canon.ps1` exit 0; `analyze.py` runs clean against a live probe export.

**Wave 4 — feel A/B + touch/pen (~0.8k).**
- The owner A/Bs `WinUiExact`/`Glide`/`Snappy` live, on-device.
- Touch goes through the same `Follow`/`Fling` path as touchpad contacts.
- `TouchSuite` gates rewritten against the new `Scroll.Motion` types.

**Green criterion:** owner's side-by-side verdict (§13 "In vivo"); `TouchSuite` green.

Per wave, the owner scrolls a playlist, an artist page, and a 10k+ list on the packaged arm64 build; the Scroll
card's CSV export feeds `analyze.py` against the WinUI probe captures. Final acceptance is the owner's
side-by-side verdict, not a gate count.

---

## 15. Superseded docs

The following carry a one-line banner at their top, added as part of this plan, pointing here:
- `docs/plans/scroll-v3-plan-2026-08-17.md`
- `docs/plans/compositor-scroll-implementation.md`
- `docs/plans/scroll-feel-rework-design.md`
- `docs/plans/scroll-feel-rework-v2-design.md`
- `docs/plans/scroll-feel-v2.1-edge-momentum-addendum.md`

`docs/plans/scroll-complete-rework-inventory-2026-08-17.md` keeps its existing "SUPERSEDED BY" banner (it
already points at `scroll-v3-plan-2026-08-17.md`, which now itself points here — a reader following either
chain lands on this document).

---

## 16. Out of scope

Tracked as follow-up reworks under the same "fix cost at its source, delete the throttle" rule — not touched by
any wave above:
- The image pipeline chains beyond deleting the scroll throttle: `DecodeScheduler`'s other caps, the
  `VramShedPolicy` hysteresis/cooldown loop, `ImageCache.ReadyGraceMs`.
- `AnimOwnerTable` → a single compose pass.
- The `FrameTimeSource` 34 ms clamp.
- A strict audit of the app's 376 timing constants.

---

## 17. Risks

- **Overscan sizing regression.** Velocity-sized overscan (§6) replaces four independent, separately-tuned caps
  (`LazyGridDrip`, `VirtualListEl.Overscan`, `ColdRealizeRamp`, `FrameBudget`) with one formula. If
  `OverscanMinPx`/`MaxPx`/`LookaheadS` are mis-set, a fast fling over a very cheap list could over-realize (GPU
  upload burst) or a very expensive list under-realize (a visible pop-in edge, not quite a blank row). Mitigated
  by `gate.scroll.no-blank` and `gate.scroll.render-alloc-zero` on real Wavee content, not synthetic rows.
- **`ListRowEl` migration scope.** Track/episode rows are the two hot lists; every other list stays component
  rows. If a third list turns out to need the same 10k+-row treatment later, it is new scope, not covered by
  this plan's waves.
- **DirectManipulation strip-down regressions on real touchpads.** Removing `TRANSLATION_INERTIA` and the wedge
  ladder changes what DM reports mid-gesture on hardware the dev machine doesn't have (precision touchpads with
  unusual firmware). The 250 ms hi-res-wheel fallback bounds the failure mode to "feels like a mouse wheel," not
  a stuck gesture — `TouchSuite` (Wave 4) is the only gate that can catch a real regression here, and it needs
  the owner's own hardware pass.
- **Damage-keyed acrylic/blur cadence under sustained scroll.** `AcrylicRefreshCadence`'s damage-keyed rule could
  refresh *more* often than the old fixed cadence-4 during a long, fast fling over acrylic-heavy content (every
  tick moves the damage rect), trading a scroll-time GPU cost regression for the correctness the old cadence
  traded away. No gate currently measures acrylic re-blur cost in isolation; add one if Wave 2's in-vivo pass
  shows a regression.
- **`RefreshLattice` present-time prediction phase error.** §0 keeps `presentSec = tick + (1+lat)·refresh` as a
  working decision, not a re-derivation. If Wave 3's golden-replay data shows systematic phase error against the
  WinUI captures, the prediction law itself — not just `ScrollFeel`'s tunables — needs revisiting, which is
  larger than a tunable change.
- **Two-tier row model's seam.** `ListRowEl` and component rows must interoperate inside the same virtualized
  list (e.g., a track table with an inline "up next" rich row). `ScrollEffect`'s `ScopeNode` and the recorder's
  span model need to treat a `RowPaint` span and a component subtree identically for hit-test and a11y purposes;
  this seam has no dedicated gate yet and should get one before Wave 2 lands the app-side migration.
