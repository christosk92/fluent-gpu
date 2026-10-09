# Smooth reveal: SizeMode.FlowReveal, reveal bands, and the expanders built on them (implementation plan)

Status: PLANNED 2026-10-08 on branch `feat/smooth-reveal` (both repos); revised the same day (the nesting bound for
closes, §4.1 + rv.8c; the band commit handoff at 6.3, §10 + rv.band.8). Input: the expander investigation (owner
direction + design, summarised in §0). Engine plans live in `fluent-gpu/docs/plans/`; this one also carries the app
(WaveeMusic) work because the two land together (the ItemsView disclosure API changes under the sidebar in Phase 2).

Phases, each leaving BOTH repos building (Debug + Release, warnings are errors) and every gate green:

| Phase | Repos | What lands |
|---|---|---|
| 1 | engine | `SizeMode.FlowReveal` (layout once, motion at paint time), `MotionTok.Reveal`, `RevealPlan`, the flow pass (nested reveals combine, bounded by the content — never the laid-out height — so closes mirror opens) + `FlowCursor` in recorder / hit-test, host step 6.3 + the deferred layout offset clamp (presented scroll extent everywhere), scroll anchoring for reveals above the view, always-firing settle callbacks, recycle suppression, the engine `Expander` migrated, `RevealSuite` rv.1–rv.16 (incl. the rv.8c nested-close legs), `RevealPlanTests` + `FlowCursorTests`, the Expander gates updated, canon docs |
| 2 | engine + app | virtual **reveal bands** (multi-band, reversible, no snap-complete, no FrameClock watcher; `RevealBands` as named fields with explicit equality; a committed collapse stops presenting at 6.3 of its commit frame) replacing the `Disclosure*` columns + `AnimChannel.DisclosureProgress`; ItemsView controller rewrite; old `virtual-disclosure.*` gates replaced by `rv.band.1–8` (rv.band.8: tail collapse at the scroll end holds its offset through the commit frame), `RevealBandsTests`; Wavee sidebar migrated (concurrent keys, `SidebarDisclosures` reducer + tests, chevrons on the Reveal spring) |
| 3 | app + engine | `Design.Reveal` specs; track drawer (+ `DrawerExtentRule` wiring, corners, chevron), Recents drawer, Discography drawer, Blend card, the three un-animated sites (Logs row details, episode replies, album "Show all"), token swaps; `DrawerExtentRuleTests`; the WinUI disclosure tokens deleted from the engine |

Work-package rules (for the coding agents): a WP edits ONLY the files it lists; the code below is the code to write —
transcribe it, keep the surrounding file's comment density and idiom, never `git stash`, never build or run tests (the
orchestrator verifies once per phase). "Anchor" = a line that exists today; locate by text, not by line number.

---

## 0. Owner direction, and where this plan deviates from the investigation (the code said otherwise)

Owner: expanders must be SMOOTH, symmetric (same motion both ways), interruptible, siblings in lockstep, a clip reveal
(no fade-pop), no layout snap at start/end, no per-frame relayout. WinUI's Expander is a "what not to do".

Deviations from the report's sketch, each forced by the code:

1. **New mode name `SizeMode.FlowReveal`, not `SizeMode.Reveal`.** `SizeMode.Reveal` already exists (presented-size
   clip window via `AnimChannel.SizeW/SizeH`, siblings do NOT follow) and is the `Auto` default used by NavigationView,
   SplitView, CommandBar and the Wavee shell pane. Changing its meaning would move every one of those. FlowReveal is
   appended after `Auto` (no enum value moves).
2. **The row carries the PRESENTED extent `P`, not a delta.** One row `AnimChannel.RevealExtent` springs `P` (absolute
   DIP). The delta `P − layoutExtent` is derived every pass. A retarget (reverse, async growth) is then a plain spring
   retarget from the live value + velocity — no coordinate-frame shifting.
3. **Bands get 4 concurrent slots per viewport** (`RevealBands`, an `[InlineArray(4)]` inside `ScrollState`, so the
   render snapshot copies it by value). Each slot is its own row `AnimChannel.RevealBand0..3` on the viewport node.
4. **Expander keeps its always-mounted clip host** (height 0 ↔ auto) instead of an orphan exit: a reverse is then the
   SAME row retargeting. The content unmount at close uses the new engine settle callback (`AnimEngine.WhenSettled`),
   which replaces both FrameClock watcher components.
5. **Enter/Exit reveals (drawers) hand off on reopen**: a FlowReveal entrant that finds a revealing exit orphan of the
   same slot takes its extent + velocity and reclaims it (no two copies, no restart from 0).
6. **Track.Drawer needs no reserved versions height**: async growth retargets the live row (rv.12 pins it).
7. **Scroll chrome**: the thumb/chevrons measure the presented content (`sec.ContentH` patched on the recorder's local
   copy); `SliceRecorder.ChromeSig` is untouched (the viewport re-records every reveal tick anyway).
8. **Phase 2 carries the sidebar** because the ItemsView disclosure API changes shape there; doing the API in one
   phase and the sidebar in another would leave the app not building.
9. Not done (unverified cause in the report): pre-warming sidebar row icons. The realize overscan (§6.4) realizes rows a
   reveal pulls into view on the seed frame, which removes the mid-animation realization the late icon came from.
10. **Reveals seed at a new host step 6.3, right after layout** (not after `ApplyProjections`), and the flow pass runs
    there once before layout effects and the post-layout scroll sync. Two commit-frame clamps would otherwise see the
    laid-out extent: `FlexLayout`'s viewport arrange (`if (sc.Offset > max) sc.Offset = max;`) and
    `SyncScrollPlansMidFrame`'s `SetExtent` (which re-holds an Idle plan at the laid-out max). Layout now DEFERS its
    offset clamp while FlowReveal work is pending (`FlexLayout.DeferOffsetClamp`) and 6.3 clamps against the PRESENTED
    extent (§6.1/§6.4). `ApplyProjections` no longer starts or snaps reveal rows: 6.3 owns the FlowReveal height.
11. **Nested reveals combine, never sum.** A reveal presents its own spring `P_own`. Only while an inner reveal wrote a
    delta into it this pass is that bounded by what its CONTENT presents: `min(P_own, ContentExtent + Σ inner deltas)`.
    The bound is never the laid-out height `H`, because a collapse lays out at its FINAL height (a clip at `Height = 0`, a
    Resize that shrank), so `min(P, H + …)` would snap every close shut on its commit frame. Inner rows run first
    (deepest first), and a containing reveal is the stop for an inner delta (§4.1 `AddReveal`, worked open AND close).
12. **A reveal that snaps wholly above the view anchors the scroll.** Plain `ScrollEl` has no scroll anchoring, so the
    snap shifts the viewport's plan frame (`ScrollHandle.ShiftFrame`) by the amount the content below moved. The content
    the user is reading stays put (§4.1 `AnchorShift`; bands: §10.4).
13. **Settle callbacks always fire.** A toggle that never starts a row still settles: a suppressed projection mid-fling,
    a zero-height panel, a resize frame. The Expander's layout effect settles it when no row is live after the commit
    (§8). A node that dies with callbacks registered fires them once and drops them (`ClearForIndex`, §4.3). That covers
    a recycled drawer and an orphan reclaimed by a reopen.
14. **`RevealBands` is four named fields, not an `[InlineArray]`.** `ScrollState` is compared with the default struct
    equality (`SceneRecordingSnapshot.Parity` `ScrollEqual`). The runtime throws `NotSupportedException` from
    `Equals`/`GetHashCode` on any struct that holds an InlineArray field (§10.1).
15. **A band collapse hands off in the ENGINE, before the commit runs.** ItemsView's layout effect (6.5) is too late to
    clear the band: 6.3 runs first, and a stale band there subtracts a phantom extent from the presented content, so
    `SetExtent` re-holds a list scrolled to its end one band short (the report's "the clamp disagreed by exactly the band
    extent"). `BandSettled` marks the band committed (`AnimEngine.CommitRevealBand`) right before invoking the owner's
    commit, and a committed band presents nothing from the flush its rows leave the model (§10, rv.band.8).

---

## 1. Motion spec (the numbers)

`MotionTok.Reveal` = critically damped spring, `SpringParams.FromResponse(0.31f, 1f)` → ω = 2π/0.31 = 20.27 rad/s,
ζ = 1, no overshoot. Normalized progress `x(t) = 1 − (1 + ωt)·e^(−ωt)`:

| t | 8.33 ms | 16.67 ms | 50 ms | 100 ms | 150 ms | 235 ms | 330 ms |
|---|---|---|---|---|---|---|---|
| x | 1.3 % | 4.6 % | 26 % | 60 % | 81 % | 95 % | 99 % |

Max per-frame step = ω/e · dt = 6.2 % @120 Hz, 12.4 % @60 Hz. The SAME spring opens and closes (expand(t) +
collapse(t) = travel). Reverse mid-flight = retarget from the live value with its velocity (`AnimEngine.Spring` rebake).
Chevrons rotate on the same token (`MotionTokenId.Reveal`) so the glyph and the edge land together.

- **Visible-span clamp** (`RevealPlan.TryClamp`): only the part of a move above `viewBottom + 8 DIP` animates; the rest
  is applied instantly where nobody can see it. A region entirely below the view SNAPS. A region entirely ABOVE it
  (`RevealPlan.IsAboveView`) also snaps, and the same frame shifts the scroll frame by the amount the content below
  moved (`ScrollHandle.ShiftFrame`). The content the user is reading does not move: no commit snap is visible.
- **Nesting**: a reveal presents `P_own`; while an inner reveal runs inside it, `min(P_own, ContentExtent + Σ inner
  deltas)` (its content's PRESENTED bottom). An outer clip never shows more than its inner flow, and an inner reveal never
  pushes past the outer clip. The bound is the content, never the laid-out height, so a close (laid out at its final
  height) is the open mirrored: no commit snap either way.
- **Scroll end**: the scroll plan clamps against the PRESENTED content extent. Layout defers its own clamp while a reveal
  is pending, so a collapse at the end of a scroller rides the max-offset edge down. The commit frame holds the offset.
- **Anchors**: `SizeAnchor.Leading` (default, a wipe: content stays put, the edge moves) for list/tree rows and drawers;
  `SizeAnchor.Parallax` (new) for cards: the content trails the edge by `−min(24, 0.35·(contentExtent − P))` DIP.
- **Reduced motion**: snap size and chevron (the token's `ReducedMotionPolicy.SnapEnd`); no fade substitute.
- **Recycle**: a FlowReveal Enter/Exit inside a recycle (rebind flush) never seeds — the row is the same row.

---

## 2. Phase 1 data model (engine)

### 2.1 `src/FluentGpu.Engine/Foundation/LayoutTransition.cs`

Append to `SizeMode` AFTER `Auto` (replace the enum body's last line `Auto,`):

```csharp
    Auto,
    FlowReveal,    // lay out ONCE at the final size; the PRESENTED vertical extent springs old → new at paint time
                   // (AnimChannel.RevealExtent) and every following sibling/ancestor rides the difference
                   // (NodePaint.FlowDelta) — no per-frame layout, no render. The smooth disclosure mode
                   // (docs/plans/smooth-reveal-implementation.md). Vertical axis only.
```

Replace `public enum SizeAnchor : byte { Leading, Trailing }` and extend its doc:

```csharp
/// <summary>Which edge of a <see cref="SizeMode.Reflow"/> node its CONTENT is anchored to while the size animates.
/// <see cref="Leading"/> = content stays put, the far edge sweeps (a wipe). <see cref="Trailing"/> = the content's
/// end edge rides the animated edge (Reflow only). <see cref="Parallax"/> (SizeMode.FlowReveal only) = the content
/// trails the moving edge by a damped share of what is still hidden (RevealPlan.ParallaxShift) — a card unfolding
/// rather than being wiped. Applied by the recorder as a child-group offset — compositor-composed.</summary>
public enum SizeAnchor : byte { Leading, Trailing, Parallax }
```

### 2.2 `src/FluentGpu.Engine/Animation/AnimTypes.cs`

Append `RevealExtent` at the END of `AnimChannel` (after `GlyphWipeSplit`) and add one sentence to the enum's doc:
`RevealExtent is the PRESENTED vertical extent of a SizeMode.FlowReveal node (DIP) — a side-table row read by
AnimEngine.PropagateFlowReveals, never composed into NodePaint directly.`

```csharp
public enum AnimChannel : byte { TranslateX, TranslateY, ScaleX, ScaleY, Rotation, Opacity, SizeW, SizeH, StrokeTrimStart, StrokeTrimEnd, ClipL, ClipT, ClipR, ClipB, LayoutW, LayoutH, BlurSigma, BrushFade, HoverFade, PressFade, DisclosureProgress, GlyphWipeSplit, RevealExtent }
```

### 2.3 `src/FluentGpu.Engine/Animation/MotionTok.cs`

- `MotionTokenId`: append `Reveal,` as the LAST member, with the comment line
  `// The smooth disclosure (SizeMode.FlowReveal, reveal bands, their chevrons) — one critically damped spring both ways.`
- In `MotionTok`, above `Get`, add:

```csharp
    /// <summary>Response (s) of <see cref="MotionTokenId.Reveal"/>: ω = 2π/0.31 ≈ 20.3 rad/s, critically damped — 1.3 % of
    /// the travel on the first 120 Hz frame, 95 % at ~235 ms, 99 % at ~330 ms, no overshoot, the same curve open and close
    /// (docs/plans/smooth-reveal-implementation.md §1).</summary>
    public const float RevealResponseSec = 0.31f;
```

- In `Get`, before the `_ =>` arm: `MotionTokenId.Reveal => MotionTokenDef.SpringOf(SpringParams.FromResponse(RevealResponseSec, 1f)),`
- Accessor after `MediaChromeConceal`: `public static MotionTokenDef Reveal => Get(MotionTokenId.Reveal);`

### 2.4 `src/FluentGpu.Engine/Scene/Columns.cs`

In `NodePaint`, directly after `public float ChildShiftX, ChildShiftY;`:

```csharp
    // SizeMode.FlowReveal presented flow (rebuilt every frame a reveal runs by AnimEngine.PropagateFlowReveals; all zero at
    // rest). FlowDelta = this node's PRESENTED vertical extent minus its laid-out one: the deltas of the reveals inside it,
    // and for a node with its own reveal row (bounded by its content while an inner reveal runs) P − H (nested reveals
    // combine, never sum — AddReveal). A boundary keeps
    // 0, except a scroll content, which keeps the sum (the presented content extent the scroll plan clamps against).
    // FlowBits: FlowShiftsBit = a column whose children the recorder AND hit-testing walk with a running Y shift (each
    // child at +shift, then shift += child.FlowDelta — FlowCursor); FlowOrphanBit = a revealing exit orphan's presented
    // extent pushes the children laid out at/below FlowOrphanTop down by FlowOrphanDelta; FlowOwnsHBit / FlowOwnsShiftBit
    // = the pass wrote PresentedH / ChildShiftY and resets them; FlowBoundaryBit = the shift lives inside this node but its
    // own presented size is its laid-out one (a declared height, a scroll content, the root); FlowRevealBit = this node's
    // own reveal row set its FlowDelta this pass; FlowInnerBit = an inner reveal's delta stopped here (this node is a live
    // reveal containing it), so its own reveal is bounded by its content's presented bottom (AddReveal).
    public float FlowDelta;
    public float FlowOrphanTop, FlowOrphanDelta;
    public byte FlowBits;
    public const byte FlowShiftsBit = 1, FlowOrphanBit = 2, FlowOwnsHBit = 4, FlowOwnsShiftBit = 8, FlowBoundaryBit = 16, FlowRevealBit = 32, FlowInnerBit = 64;
```

In `ScrollState`, directly after `public float ItemClipTopFadeBand;` (before the Disclosure fields):

```csharp
    // SizeMode.FlowReveal: DIP the realize window reaches past the viewport bottom while reveals in this (vertical)
    // viewport present less than they lay out — the rows they pull up into view. Monotone over a reveal's flight, so the
    // rows realize once on its seed frame (AnimEngine.PropagateFlowReveals writes it; 0 at rest; UI-side only).
    public float RevealOverscan;
```

---

## 3. `RevealPlan` — the pure decisions (new file `src/FluentGpu.Engine/Animation/RevealPlan.cs`)

```csharp
using System;
using FluentGpu.Foundation;

namespace FluentGpu.Animation;

/// <summary>The pure decisions behind <see cref="SizeMode.FlowReveal"/> and the virtual reveal bands: which part of a
/// presented-extent move a viewer can see, the Parallax content lead, and the one reveal spring's shape. Engine-free, so
/// Engine.Tests pins it directly (RevealPlanTests). docs/plans/smooth-reveal-implementation.md §1/§3.</summary>
public static class RevealPlan
{
    /// <summary>Slack below the view's bottom edge (DIP) the visible span still animates through.</summary>
    public const float VisibleSlackDip = 8f;
    /// <summary>Parallax anchor: the content trails the moving edge by this share of the extent still hidden …</summary>
    public const float ParallaxShare = 0.35f;
    /// <summary>… capped at this many DIP.</summary>
    public const float ParallaxMaxDip = 24f;

    /// <summary>Clamp a presented-extent move <paramref name="p0"/> → <paramref name="p1"/> of a region whose top sits at
    /// <paramref name="regionTop"/> (window DIP) inside a view spanning [<paramref name="viewTop"/>,
    /// <paramref name="viewBottom"/>]. Only the part of the move above viewBottom + slack is visible: the spring drives
    /// [<paramref name="from"/>, <paramref name="to"/>] and the remainder is applied instantly where nobody can see it.
    /// False — the caller snaps — when the region lies wholly above the view (a reveal above the scroll anchor would slide
    /// the content the user is reading) or nothing of the move is visible at all.</summary>
    public static bool TryClamp(float p0, float p1, float regionTop, float viewTop, float viewBottom, out float from, out float to)
    {
        from = to = p1;
        if (IsAboveView(p0, p1, regionTop, viewTop)) return false;
        float limit = MathF.Max(0f, viewBottom + VisibleSlackDip - regionTop);
        from = MathF.Min(p0, MathF.Max(p1, limit));
        to = MathF.Min(p1, MathF.Max(p0, limit));
        if (MathF.Abs(from - to) >= 0.5f) return true;
        from = to = p1;
        return false;
    }

    /// <summary>True when a region at <paramref name="regionTop"/> stays wholly above the view's top edge
    /// <paramref name="viewTop"/> through the whole move <paramref name="p0"/> → <paramref name="p1"/>. The reveal then
    /// snaps, and the caller shifts the scroll frame by the change so the content being read stays put (scroll anchoring).</summary>
    public static bool IsAboveView(float p0, float p1, float regionTop, float viewTop)
        => regionTop + MathF.Max(p0, p1) <= viewTop;

    /// <summary>The Parallax child shift of a node presenting <paramref name="presented"/> of a content extent
    /// <paramref name="contentExtent"/>: the content leads by a damped share of what is still hidden, never past
    /// <see cref="ParallaxMaxDip"/>; 0 once fully presented.</summary>
    public static float ParallaxShift(float presented, float contentExtent)
        => -MathF.Min(ParallaxMaxDip, MathF.Max(0f, contentExtent - presented) * ParallaxShare);

    /// <summary>Normalized progress (0 → 1) of the <see cref="MotionTokenId.Reveal"/> spring <paramref name="tMs"/> after it
    /// leaves rest — the curve every reveal runs, sampled exactly as the scheduler samples it (gates + tests).</summary>
    public static float Progress(float tMs)
    {
        var g = Generators.BakeSpring(MotionTok.Reveal.Spring, x0: -1f, v0: 0f);
        return Generators.EvalSpring(in g, 1f, tMs, Generators.RestDelta, Generators.RestSpeed, out _).Value;
    }
}
```

---

## 4. The engine: `AnimEngine` FlowReveal partial (Phase 1)

### 4.1 New file `src/FluentGpu.Engine/Animation/AnimScheduler.FlowReveal.cs`

```csharp
using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  SizeMode.FlowReveal — layout ONCE, motion at paint time (docs/plans/smooth-reveal-implementation.md).
//
//  A FlowReveal node's size change (a mount, an unmount, a resize) lands in LAYOUT in one pass; one row
//  (AnimChannel.RevealExtent) springs the node's PRESENTED vertical extent old → new under MotionTok.Reveal. The host
//  seeds the rows right after layout (6.3) and PropagateFlowReveals folds every live row into NodePaint.FlowDelta on the
//  node and its ancestors (up to the nearest flow boundary or containing reveal). The recorder / hit-test walk
//  (FlowCursor) shifts every following sibling by it. Siblings, parents, the scroll extent and the scrollbar move in
//  lockstep with no per-frame layout, no component render and no allocation. Interruptible by construction: the row is a
//  spring on an ABSOLUTE extent, so a reverse or a content-growth retarget departs from the live value with its velocity.
//  Nested reveals combine, never sum: a reveal presents its own spring, bounded — only while an inner reveal runs inside
//  it — by its content's presented bottom (ContentExtent + inner deltas), never by its laid-out height (a close lays out
//  at its FINAL height, which would snap it shut).
//
//  Settle callbacks (WhenSettled) replace the FrameClock poller components that used to watch HasTracks: the tick (or a
//  snap, or the node's death) queues them, and the host drains them at the START of the next frame (outside the
//  zero-alloc phases).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    /// <summary>Nodes mounted this commit with a FlowReveal Enter. The host seeds their reveal right after layout (6.3);
    /// its projection pass then reads them as shove carriers and clears the list.</summary>
    public List<NodeHandle> PendingEnterReveal { get; } = new(8);
    /// <summary>FlowReveal exits orphaned this commit, with their former visual parent. The projection pass treats them
    /// as shove carriers (a sibling they moved must not position-FLIP on top of the presented flow).</summary>
    public List<(NodeHandle Node, NodeHandle VisualParent)> RevealExitCarriers { get; } = new(8);

    private readonly record struct SettleKey(uint Index, uint Gen, AnimChannel Channel);
    private readonly record struct RevealEntry(NodeHandle Node, int Depth, float Presented, float Target);

    private int _revealRows;                                    // live RevealExtent rows (the idle fast path's census)
    private readonly List<NodeHandle> _flowTouched = new(64);   // nodes whose flow columns the last pass wrote
    private readonly List<RevealEntry> _revealOrder = new(16);  // this pass's live reveals, deepest first
    private List<NodeHandle> _ovViewports = new(8), _ovPrevViewports = new(8);   // viewports given RevealOverscan
    private List<float> _ovAmount = new(8), _ovPrevAmount = new(8);              // (this pass / last pass)
    private bool _overscanGrew;
    private readonly Dictionary<SettleKey, Action> _settleCallbacks = new();
    private readonly Dictionary<uint, int> _settleIndexRefs = new();   // node index → callbacks registered on it
    private readonly List<Action> _settledQueue = new(32);
    private Action?[] _settledDrain = new Action?[32];

    /// <summary>True when this frame's pass raised a viewport's realize overscan past its realized window (the host
    /// re-realizes + re-lays out once, then re-runs the pass).</summary>
    public bool FlowOverscanGrew => _overscanGrew;

    /// <summary>True while any FlowReveal work is pending or running: a live reveal row, an entrant to seed, an exit
    /// orphaned this commit, a live reveal band. The host defers layout's scroll-offset clamp on these frames (6.3 clamps
    /// against the PRESENTED extent instead).</summary>
    public bool HasFlowRevealWork
        => _revealRows > 0 || PendingEnterReveal.Count > 0 || RevealExitCarriers.Count > 0;

    /// <summary>True while <paramref name="node"/> has a live reveal row (the Expander's always-fire check, gates).</summary>
    public bool IsRevealing(NodeHandle node) => _slab.NodeHasRows((int)node.Raw.Index) && Find(node, AnimChannel.RevealExtent) >= 0;

    /// <summary>Registered settle callbacks (diagnostics + gates: a dead node must leave none behind).</summary>
    public int SettleCallbackCount => _settleCallbacks.Count;

    // ── seeding ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A FlowReveal node mounted this commit (host 6.3, post-layout, before the tick): present it from 0. When
    /// its visual parent still paints a revealing EXIT orphan of the same slot (a reopen mid-close), present it from that
    /// orphan's live extent and velocity instead, and reclaim the orphan. A reverse never shows two copies or restarts
    /// from zero.</summary>
    public void SeedFlowRevealEnter(NodeHandle node)
    {
        if (!_scene.IsLive(node) || !TryGetTransition(node, out var spec)) return;
        float to = _scene.Bounds(node).H;
        float from = 0f, v0 = 0f;
        if (TakeRevealOrphan(node, out float orphanP, out float orphanV)) { from = orphanP; v0 = orphanV; }
        SeedFlowReveal(node, from, to, v0, in spec.Dynamics, enter: true);
    }

    /// <summary>A FlowReveal node's laid-out height changed this commit (host 6.3, from the FLIP capture).</summary>
    public void SeedFlowRevealResize(NodeHandle node, float fromH, float toH, in TransitionDynamics dyn)
        => SeedFlowReveal(node, fromH, toH, 0f, in dyn, enter: false);

    /// <summary>A FlowReveal node was just orphaned (Reconciler.Remove): present it from <paramref name="fromP"/> (its live
    /// extent, read BEFORE the unmount cancelled its rows) down to 0 with <paramref name="v0"/>. The row keeps the orphan
    /// alive; the host reclaims it when the row settles.</summary>
    public void SeedFlowRevealExit(NodeHandle node, float fromP, float v0, in LayoutTransition spec)
    {
        TransitionDynamics dyn = spec.ExitDynamics ?? spec.Dynamics;
        SeedFlowReveal(node, fromP, 0f, v0, in dyn, enter: true);
    }

    /// <summary>Land a FlowReveal node on its laid-out height <paramref name="toH"/> NOW (host 6.3 under suppressed
    /// projections: a user scroll in flight, a keep-alive switch). The row (if any) is cancelled; a node wholly above the
    /// view anchors the scroll like any snap; and the settle callback still fires: a snapped reveal is a settled one.</summary>
    public void SnapFlowReveal(NodeHandle node, float fromH, float toH)
    {
        const AnimChannel ch = AnimChannel.RevealExtent;
        int ex = Find(node, ch);
        float cur = ex >= 0 ? _slab.At(ex).Position : fromH;
        if (ex >= 0) Cancel(node, ch);
        RevealView(node, out float top, out float viewTop, out _, out NodeHandle viewport);
        if (RevealPlan.IsAboveView(cur, toH, top, viewTop)) AnchorShift(node, viewport, toH - cur);
        QueueSettled(node, ch);
    }

    /// <summary>The live presented extent + velocity of a node's reveal; its laid-out height and 0 at rest.</summary>
    public void ReadFlowReveal(NodeHandle node, out float presented, out float velocity)
    {
        int s = Find(node, AnimChannel.RevealExtent);
        if (s >= 0) { presented = _slab.At(s).Position; velocity = _slab.At(s).Velocity; return; }
        presented = _scene.IsLive(node) ? _scene.Bounds(node).H : 0f;
        velocity = 0f;
    }

    private void SeedFlowReveal(NodeHandle node, float p0, float p1, float v0, in TransitionDynamics dynIn, bool enter)
    {
        const AnimChannel ch = AnimChannel.RevealExtent;
        TransitionDynamics dyn = Normalize(dynIn);
        int ex = Find(node, ch);
        float cur = ex >= 0 ? _slab.At(ex).Position : p0;
        float vel = ex >= 0 ? _slab.At(ex).Velocity : v0;
        // Reduced motion is a VALUE read here (never a branch in authoring code).
        bool snap = FluentGpu.Dsl.Motion.ReducedMotion || (dyn.Kind == DynamicsKind.Tween && dyn.DurationMs <= 1f);
        RevealView(node, out float top, out float viewTop, out float viewBottom, out NodeHandle viewport);
        float from = p1, to = p1;
        if (!snap) snap = !RevealPlan.TryClamp(cur, p1, top, viewTop, viewBottom, out from, out to);
        if (snap)
        {
            // Wholly ABOVE the view (whatever made it snap, reduced motion included): the snap must not move what the user
            // is reading. Shift the scroll frame by what the content below just moved (an instant anchor shift, never a motion).
            if (RevealPlan.IsAboveView(cur, p1, top, viewTop)) AnchorShift(node, viewport, p1 - cur);
            if (ex >= 0) Cancel(node, ch);
            QueueSettled(node, ch);
            return;
        }
        if (ex >= 0 && MathF.Abs(from - cur) < 0.5f && dyn.Kind == DynamicsKind.Spring && _slab.At(ex).Kind == GenKind.Spring)
        {
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio));   // retarget: live value + velocity
            return;
        }
        // The visible window moved the departure point (or the dynamics changed): jump where nobody sees it, keep the speed.
        if (ex >= 0) Cancel(node, ch);
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: from, initialVelocity: vel);
        else
            Animate(node, ch, from, to, dyn.DurationMs, dyn.Easing);
        ClaimRevealClip(node);
        if (enter) MarkStartPending(node);
    }

    // The node clips its content at the presented extent for the life of the row — the reflow ClipAdded contract: a node
    // that declared ClipToBounds itself is never un-clipped at teardown (FreeSlot releases only what the row added).
    private void ClaimRevealClip(NodeHandle node)
    {
        int s = Find(node, AnimChannel.RevealExtent);
        if (s < 0 || !_scene.IsLive(node)) return;
        ref AnimValue r = ref _slab.At(s);
        if (r.Has(AnimFlags.ClipAdded) || (_scene.Flags(node) & NodeFlags.ClipsToBounds) != 0) return;
        _scene.Mark(node, NodeFlags.ClipsToBounds | NodeFlags.PaintDirty);
        r.Flags |= AnimFlags.ClipAdded;
    }

    // The node's top and the view it is seen through, in window DIP: the nearest vertical scroll viewport (returned in
    // `viewport`), else the root (viewport = Null). An exit orphan is detached, so its top is read through its former
    // visual parent.
    private void RevealView(NodeHandle node, out float top, out float viewTop, out float viewBottom, out NodeHandle viewport)
    {
        NodeHandle anchor = node;
        float localTop = 0f;
        if ((_scene.Flags(node) & NodeFlags.Exiting) != 0 && _scene.TryGetOrphanVisualParent(node, out var vp) && !vp.IsNull)
        {
            anchor = vp;
            localTop = _scene.Bounds(node).Y;
        }
        top = _scene.AbsoluteRect(anchor).Y + localTop;
        for (var p = anchor == node ? _scene.Parent(node) : anchor; !p.IsNull; p = _scene.Parent(p))
        {
            if ((_scene.Flags(p) & NodeFlags.Scrollable) == 0 || !_scene.TryGetScroll(p, out var sc) || sc.Orientation != 0) continue;
            RectF r = _scene.AbsoluteRect(p);
            viewTop = r.Y;
            viewBottom = r.Y + sc.ViewportH;
            viewport = p;
            return;
        }
        RectF root = _scene.Root.IsNull ? default : _scene.Bounds(_scene.Root);
        viewTop = root.Y;
        viewBottom = root.Bottom;
        viewport = NodeHandle.Null;
    }

    // Scroll anchoring for a reveal that snapped wholly above the view. Shift the viewport's plan frame by how far the
    // change moves the content that follows the node: a column hop carries all of it, a row / z-stack hop only what
    // reaches past its tallest other child, and a flow boundary absorbs it. Uses ScrollHandle.ShiftFrame, the same instant
    // rebase a measured correction above the anchor takes (it moves with a live plan, never starts a motion).
    private void AnchorShift(NodeHandle node, NodeHandle viewport, float d)
    {
        if (viewport.IsNull || MathF.Abs(d) < 0.5f || _scene.ScrollHandleFor(viewport) is not { } handle) return;
        NodeHandle child = node, parent = _scene.Parent(node);
        bool viaOrphan = (_scene.Flags(node) & NodeFlags.Exiting) != 0;
        if (viaOrphan && (!_scene.TryGetOrphanVisualParent(node, out parent) || parent.IsNull)) return;
        while (!parent.IsNull && _scene.IsLive(parent) && parent != viewport)
        {
            d = FlowContribution(parent, child, d, viaOrphan);
            if (MathF.Abs(d) < 0.5f) return;
            if (IsVerticalScrollContent(parent, out _)) { handle.ShiftFrame(d); return; }
            if (IsFlowBoundary(parent)) return;
            child = parent;
            parent = _scene.Parent(parent);
            viaOrphan = false;
        }
    }

    // A reopen mid-close: the entrant's visual parent still paints a revealing exit orphan of the SAME slot (same element
    // type, same laid-out top). Hand its extent + velocity to the entrant and reclaim it now (the reclaim frees the
    // orphan's slot, which fires and drops any settle callback registered on it — ClearForIndex).
    private bool TakeRevealOrphan(NodeHandle node, out float presented, out float velocity)
    {
        presented = velocity = 0f;
        if (_scene.OrphanCount == 0) return false;
        NodeHandle parent = _scene.Parent(node);
        if (parent.IsNull || _scene.OrphanChildrenOf(parent) is not { } orphans) return false;
        float top = _scene.Bounds(node).Y;
        ushort type = _scene.ElementTypeId(node);
        for (int i = orphans.Count - 1; i >= 0; i--)
        {
            NodeHandle o = orphans[i];
            if (!_scene.IsLive(o) || _scene.ElementTypeId(o) != type || MathF.Abs(_scene.Bounds(o).Y - top) > 1f) continue;
            int s = Find(o, AnimChannel.RevealExtent);
            if (s < 0) continue;
            presented = _slab.At(s).Position;
            velocity = _slab.At(s).Velocity;
            CancelAll(o);
            _scene.ReclaimOrphan(o);
            return true;
        }
        return false;
    }

    // ── settle callbacks ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Run <paramref name="callback"/> on the UI thread at the START of the next frame (before the reactive flush —
    /// never inside the zero-alloc phases) each time the row on <paramref name="node"/>/<paramref name="channel"/> comes to
    /// rest. That includes a reveal that snaps (reduced motion, off-screen, suppressed). When the node itself is freed
    /// (recycled, reclaimed, unmounted), the callback runs once more and is dropped. Null unregisters. One callback per
    /// (node, channel). Replaces the FrameClock poller components that polled <see cref="HasTracks"/>.</summary>
    public void WhenSettled(NodeHandle node, AnimChannel channel, Action? callback)
    {
        var key = new SettleKey(node.Raw.Index, node.Raw.Gen, channel);
        if (callback is null)
        {
            if (!_settleCallbacks.Remove(key)) return;
            if (_settleIndexRefs.TryGetValue(key.Index, out int refs) && refs > 1) _settleIndexRefs[key.Index] = refs - 1;
            else _settleIndexRefs.Remove(key.Index);
            return;
        }
        if (_settleCallbacks.TryAdd(key, callback))
            _settleIndexRefs[key.Index] = _settleIndexRefs.TryGetValue(key.Index, out int n) ? n + 1 : 1;
        else _settleCallbacks[key] = callback;
    }

    /// <summary>True while settle callbacks wait for the next frame's drain (the host keeps one more frame coming).</summary>
    public bool HasSettledCallbacks => _settledQueue.Count != 0;

    /// <summary>Host, frame start (before the reactive flush): invoke the callbacks the last frame queued, in order.</summary>
    public void DrainSettledCallbacks()
    {
        int n = _settledQueue.Count;
        if (n == 0) return;
        if (_settledDrain.Length < n) _settledDrain = new Action?[Math.Max(n, _settledDrain.Length * 2)];
        _settledQueue.CopyTo(_settledDrain);
        _settledQueue.Clear();
        for (int i = 0; i < n; i++)
        {
            Action cb = _settledDrain[i]!;
            _settledDrain[i] = null;
            cb();
        }
    }

    private void QueueSettled(NodeHandle node, AnimChannel channel)
    {
        if (_settleCallbacks.Count == 0) return;
        if (_settleCallbacks.TryGetValue(new SettleKey(node.Raw.Index, node.Raw.Gen, channel), out Action? cb))
            Enqueue(cb);
    }

    // A callback already queued this frame (a row that settled, then its node freed in the same frame) runs once.
    private void Enqueue(Action cb)
    {
        if (!_settledQueue.Contains(cb)) _settledQueue.Add(cb);
    }

    /// <summary>The slot at <paramref name="index"/> is being freed (ClearForIndex): whatever waited on one of its rows
    /// coming to rest is over. Queue each callback registered on it once and forget it, so nothing waits forever and nothing
    /// leaks. Covers a drawer recycled out from under its owner, an exit orphan reclaimed by a reopen, a viewport
    /// unmounted with bands in flight. (Removing during the enumeration is legal on .NET Core 3+.)</summary>
    private void FireSettleCallbacksForIndex(int index)
    {
        if (_settleCallbacks.Count == 0 || !_settleIndexRefs.Remove((uint)index)) return;
        foreach (var kv in _settleCallbacks)
        {
            if (kv.Key.Index != (uint)index) continue;
            Enqueue(kv.Value);
            _settleCallbacks.Remove(kv.Key);
        }
    }

    // ── the flow pass (host 6.3 and phase 7.05) ─────────────────────────────────────────────────────────────

    /// <summary>Rebuild the presented-flow columns (<see cref="NodePaint.FlowDelta"/>/<c>FlowBits</c>/<c>FlowOrphan*</c>,
    /// the owned PresentedH/ChildShiftY, and <see cref="ScrollState.RevealOverscan"/>) from the live reveal rows. The host
    /// runs it at 6.3 (right after a commit's layout, on the rows it just seeded) and at 7.05 (after the tick and the
    /// reflow re-solve, before record). False with zero work when nothing reveals and nothing did last pass; true
    /// otherwise (the host then re-clamps scroll extents against the PRESENTED content). Allocation-free: pre-sized
    /// lists, and the walk visits only the reveal nodes' ancestor chains.</summary>
    public bool PropagateFlowReveals()
    {
        _overscanGrew = false;
        if (_revealRows == 0 && _flowTouched.Count == 0 && _ovPrevViewports.Count == 0) return false;
        ResetFlow();
        if (_revealRows > 0)
        {
            CollectReveals();
            for (int i = 0; i < _revealOrder.Count; i++)
            {
                RevealEntry e = _revealOrder[i];
                AddReveal(e.Node, e.Presented, e.Target);
            }
            _revealOrder.Clear();
        }
        FinishFlow();
        return true;
    }

    // Undo the last pass: every column it wrote goes back to rest (the new pass rewrites what still reveals).
    private void ResetFlow()
    {
        for (int i = 0; i < _flowTouched.Count; i++)
        {
            NodeHandle n = _flowTouched[i];
            if (!_scene.IsLive(n)) continue;
            ref NodePaint p = ref _scene.Paint(n);
            if ((p.FlowBits & NodePaint.FlowOwnsHBit) != 0) p.PresentedH = float.NaN;
            if ((p.FlowBits & NodePaint.FlowOwnsShiftBit) != 0) p.ChildShiftY = 0f;
            p.FlowDelta = 0f;
            p.FlowOrphanTop = 0f;
            p.FlowOrphanDelta = 0f;
            p.FlowBits = 0;
            _scene.Mark(n, NodeFlags.PaintDirty);
        }
        _flowTouched.Clear();
    }

    // Every live, unparked RevealExtent row, ordered DEEPEST first: an inner reveal's delta must reach the reveal that
    // contains it before that one combines it with its own presented extent (AddReveal). Insertion sort into a pre-sized
    // list — a handful of rows, no allocation.
    private void CollectReveals()
    {
        _revealOrder.Clear();
        for (int nodeIndex = _slab.FirstActiveNode; nodeIndex >= 0; nodeIndex = _slab.NextActiveNode(nodeIndex))
            for (int s = _slab.HeadOnNode(nodeIndex); s >= 0; s = _slab.At(s).NextOnNode)
            {
                ref AnimValue r = ref _slab.At(s);
                if (r.Channel != AnimChannel.RevealExtent || r.Has(AnimFlags.Parked) || !_scene.IsLive(r.Node)) continue;
                var e = new RevealEntry(r.Node, DepthOf(r.Node), r.Position, r.To);
                int j = _revealOrder.Count;
                _revealOrder.Add(e);
                while (j > 0 && _revealOrder[j - 1].Depth < e.Depth) { _revealOrder[j] = _revealOrder[j - 1]; j--; }
                _revealOrder[j] = e;
            }
    }

    // Tree depth (an exit orphan counts through its former visual parent).
    private int DepthOf(NodeHandle n)
    {
        NodeHandle p = _scene.Parent(n);
        if ((_scene.Flags(n) & NodeFlags.Exiting) != 0 && _scene.TryGetOrphanVisualParent(n, out var vp) && !vp.IsNull) p = vp;
        int d = 1;
        for (; !p.IsNull; p = _scene.Parent(p)) d++;
        return d;
    }

    // One live reveal (deepest first). It presents its own spring. Only when an inner reveal's delta stopped here this pass
    // (FlowInnerBit; those deltas already sit in its FlowDelta) is that bounded by what its CONTENT presents: the children's
    // laid-out bottom (ContentExtent) plus the inner deltas. An outer clip then never shows more than its inner flow, and an
    // inner reveal never pushes past the outer clip: nested reveals COMBINE, never sum.
    // The bound is deliberately NOT the laid-out height: a close lays out at its FINAL height (a clip at Height 0, a Resize
    // that shrank), so min(P, H + inner) would present 0 from the seed frame on and snap every close shut. And with no
    // inner reveal there is no bound at all: a Resize whose content shrank outright (the Logs line unwrapping, a slot
    // shrinking) closes over the space it still presents, exactly as it opened. Its delta (presented − laid-out; an exit
    // orphan holds no layout space) then climbs the ancestor chain to the nearest flow boundary, or to a containing reveal,
    // which combines it in its own turn.
    private void AddReveal(NodeHandle node, float presented, float target)
    {
        bool orphan = (_scene.Flags(node) & NodeFlags.Exiting) != 0;
        float layoutExtent = orphan ? 0f : _scene.Bounds(node).H;
        ref NodePaint np = ref TouchFlow(node);
        bool bounded = !orphan && (np.FlowBits & NodePaint.FlowInnerBit) != 0;
        float shown = bounded ? MathF.Min(presented, ContentExtent(node) + np.FlowDelta) : presented;
        float d = shown - layoutExtent;
        np.FlowDelta = d;
        np.FlowBits |= NodePaint.FlowRevealBit;
        // The rows a reveal pulls up into view must stay realized for its whole flight. min(shown, To) is monotone over a
        // flight, so this only shrinks after the seed frame: one realize, never a per-frame one.
        float overscan = MathF.Max(0f, layoutExtent - MathF.Min(shown, target));
        NodeHandle child = node, parent;
        if (orphan)
        {
            if (!_scene.TryGetOrphanVisualParent(node, out parent) || parent.IsNull || !_scene.IsLive(parent)) return;
            if (IsColumnFlow(parent))
            {
                ref NodePaint vp = ref TouchFlow(parent);
                float top = _scene.Bounds(node).Y;
                if ((vp.FlowBits & NodePaint.FlowOrphanBit) == 0 || top < vp.FlowOrphanTop) vp.FlowOrphanTop = top;
                vp.FlowOrphanDelta += d;
                vp.FlowBits |= NodePaint.FlowOrphanBit | NodePaint.FlowShiftsBit;
            }
        }
        else parent = _scene.Parent(node);
        bool viaOrphan = orphan;
        while (!parent.IsNull && _scene.IsLive(parent))
        {
            float c = FlowContribution(parent, child, d, viaOrphan);
            ref NodePaint pp = ref TouchFlow(parent);
            pp.FlowDelta += c;
            if (!viaOrphan && IsColumnFlow(parent)) pp.FlowBits |= NodePaint.FlowShiftsBit;
            if (IsLiveReveal(parent))
            {
                pp.FlowBits |= NodePaint.FlowInnerBit;   // a containing reveal: shallower, so it runs later and combines this delta
                return;
            }
            if (IsVerticalScrollContent(parent, out NodeHandle viewport))
            {
                pp.FlowBits |= NodePaint.FlowBoundaryBit;
                AddOverscan(viewport, overscan);
                return;
            }
            if (IsFlowBoundary(parent)) { pp.FlowBits |= NodePaint.FlowBoundaryBit; return; }
            if (MathF.Abs(c) < 1e-3f) return;
            child = parent;
            d = c;
            viaOrphan = false;
            parent = _scene.Parent(parent);
        }
    }

    // A live, unparked reveal row on the node.
    private bool IsLiveReveal(NodeHandle n)
    {
        if (!_slab.NodeHasRows((int)n.Raw.Index)) return false;
        int s = Find(n, AnimChannel.RevealExtent);
        return s >= 0 && !_slab.At(s).Has(AnimFlags.Parked);
    }

    // How far a child's presented-extent change moves its PARENT's presented bottom: all of it in a column (children
    // stack); in a row / z-stack / grid only the part that reaches past the tallest other child.
    private float FlowContribution(NodeHandle parent, NodeHandle child, float d, bool childIsOrphan)
    {
        if (IsColumnFlow(parent)) return d;
        ref readonly RectF cb = ref _scene.Bounds(child);
        float childBottom = cb.Y + (childIsOrphan ? 0f : cb.H);
        float others = 0f;
        for (var s = _scene.FirstChild(parent); !s.IsNull; s = _scene.NextSibling(s))
        {
            if (s == child) continue;
            ref readonly RectF sb = ref _scene.Bounds(s);
            others = MathF.Max(others, sb.Y + sb.H);
        }
        return MathF.Max(childBottom + d, others) - MathF.Max(childBottom, others);
    }

    private bool IsColumnFlow(NodeHandle n)
        => (_scene.Flags(n) & NodeFlags.ZStack) == 0 && !_scene.HasGrid(n)
           && (_scene.Layout(n).Direction == 1 || IsVerticalScrollContent(n, out _));

    private bool IsVerticalScrollContent(NodeHandle n, out NodeHandle viewport)
    {
        viewport = _scene.Parent(n);
        return !viewport.IsNull && (_scene.Flags(viewport) & NodeFlags.Scrollable) != 0
            && _scene.TryGetScroll(viewport, out var sc) && sc.ContentNode == n && sc.Orientation == 0;
    }

    // Where a presented-extent change stops climbing (the shift lives INSIDE these, their own size does not change): the
    // root, a scroll viewport, a box with a DECLARED height, a Grow-filled child of a column whose height is DEFINITE, and
    // a node whose height another size row owns (SizeMode.Reveal / Relayout = SizeH, Reflow = LayoutH). A Grow child of a
    // content-sized column (a scroll content, an auto column) is NOT a boundary: there the free space is 0 and the child's
    // height follows its content, so a reveal inside it must reach the scroll content (presented extent, scrollbar,
    // overscan).
    private bool IsFlowBoundary(NodeHandle n)
    {
        if (n == _scene.Root || (_scene.Flags(n) & NodeFlags.Scrollable) != 0) return true;
        ref readonly LayoutInput li = ref _scene.Layout(n);
        if (!float.IsNaN(li.Height)) return true;
        if (li.FlexGrow > 0f)
        {
            NodeHandle p = _scene.Parent(n);
            if (!p.IsNull && _scene.Layout(p).Direction == 1 && IsDefiniteHeight(p)) return true;
        }
        if (!_slab.NodeHasRows((int)n.Raw.Index)) return false;
        return Find(n, AnimChannel.SizeH) >= 0 || Find(n, AnimChannel.LayoutH) >= 0;
    }

    // Does this node's height NOT follow its content: the root, a scroll viewport, a declared height, or a Grow child of a
    // definite column (recursively). A vertical scroll content (unbounded main axis) and an auto column are not definite.
    private bool IsDefiniteHeight(NodeHandle n)
    {
        for (int guard = 0; guard < 64 && !n.IsNull; guard++)
        {
            if (n == _scene.Root || (_scene.Flags(n) & NodeFlags.Scrollable) != 0) return true;
            if (IsVerticalScrollContent(n, out _)) return false;
            ref readonly LayoutInput li = ref _scene.Layout(n);
            if (!float.IsNaN(li.Height)) return true;
            NodeHandle p = _scene.Parent(n);
            if (li.FlexGrow <= 0f || p.IsNull || _scene.Layout(p).Direction != 1) return false;
            n = p;
        }
        return false;
    }

    private ref NodePaint TouchFlow(NodeHandle n)
    {
        bool seen = false;
        for (int i = 0; i < _flowTouched.Count; i++)
            if (_flowTouched[i] == n) { seen = true; break; }
        if (!seen) _flowTouched.Add(n);
        return ref _scene.Paint(n);
    }

    private void AddOverscan(NodeHandle viewport, float amount)
    {
        for (int i = 0; i < _ovViewports.Count; i++)
            if (_ovViewports[i] == viewport) { _ovAmount[i] += amount; return; }
        _ovViewports.Add(viewport);
        _ovAmount.Add(amount);
    }

    // Turn the accumulated deltas into what the recorder draws: the presented height of every revealing node and every
    // non-boundary ancestor, the Parallax child shift, the viewports' realize overscan. A boundary keeps its laid-out size
    // and reports FlowDelta 0 to its own parent's cursor. The exception is a scroll content: it keeps the sum, which is
    // the presented content extent the scroll plan and the scrollbar measure against.
    private void FinishFlow()
    {
        for (int i = 0; i < _flowTouched.Count; i++)
        {
            NodeHandle n = _flowTouched[i];
            ref NodePaint p = ref _scene.Paint(n);
            bool self = (p.FlowBits & NodePaint.FlowRevealBit) != 0;
            if (!self && (p.FlowBits & NodePaint.FlowBoundaryBit) != 0)
            {
                if (!IsVerticalScrollContent(n, out _)) p.FlowDelta = 0f;
                _scene.Mark(n, NodeFlags.PaintDirty);
                continue;
            }
            bool orphan = (_scene.Flags(n) & NodeFlags.Exiting) != 0;
            float h = orphan ? 0f : _scene.Bounds(n).H;
            float delta = p.FlowDelta;
            // Never steal a PresentedH another writer owns (a SizeMode.Reveal row, a scroll-effect collapse).
            if (MathF.Abs(delta) >= 1e-3f && float.IsNaN(p.PresentedH))
            {
                p.PresentedH = MathF.Max(0f, h + delta);
                p.FlowBits |= NodePaint.FlowOwnsHBit;
            }
            if (self && TryGetTransition(n, out var spec) && spec.Anchor == SizeAnchor.Parallax)
            {
                p.ChildShiftY = RevealPlan.ParallaxShift(h + delta, ContentExtent(n));
                p.FlowBits |= NodePaint.FlowOwnsShiftBit;
            }
            _scene.Mark(n, NodeFlags.PaintDirty);
        }
        FinishOverscan();
    }

    private float ContentExtent(NodeHandle n)
    {
        float e = 0f;
        for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
        {
            ref readonly RectF cb = ref _scene.Bounds(c);
            e = MathF.Max(e, cb.Y + cb.H);
        }
        return e > 0f ? e + _scene.Layout(n).Padding.Bottom : _scene.Bounds(n).H;
    }

    // Publish each viewport's realize overscan; a GROWN one that no longer covers the window marks the viewport for a
    // re-realize (the host runs it right after this pass, once). Viewports no reveal reached this pass go back to 0.
    private void FinishOverscan()
    {
        for (int i = 0; i < _ovViewports.Count; i++)
        {
            NodeHandle v = _ovViewports[i];
            if (!_scene.IsLive(v) || !_scene.TryGetScroll(v, out ScrollState sc)) continue;
            float amount = _ovAmount[i];
            float prev = 0f;
            for (int j = 0; j < _ovPrevViewports.Count; j++)
                if (_ovPrevViewports[j] == v) { prev = _ovPrevAmount[j]; break; }
            if (sc.RevealOverscan != amount) _scene.ScrollRef(v).RevealOverscan = amount;
            if (amount > prev + 0.5f && sc.ItemCount > 0 && sc.Extent is { } ext)
            {
                var feel = ScrollTunables.Current;
                var rw = Virtualizer.Plan(ext, sc.Offset, sc.Velocity, sc.ViewportMain + amount, in feel, sc.AnchorIndex);
                if (ScrollContentPose.NeedsRealize(in sc, in rw))
                {
                    _scene.Mark(v, NodeFlags.VirtualRangeDirty);
                    _overscanGrew = true;
                }
            }
        }
        for (int j = 0; j < _ovPrevViewports.Count; j++)
        {
            NodeHandle v = _ovPrevViewports[j];
            if (_ovViewports.Contains(v) || !_scene.IsLive(v) || !_scene.TryGetScroll(v, out ScrollState old)) continue;
            if (old.RevealOverscan != 0f) _scene.ScrollRef(v).RevealOverscan = 0f;
        }
        (_ovPrevViewports, _ovViewports) = (_ovViewports, _ovPrevViewports);
        (_ovPrevAmount, _ovAmount) = (_ovAmount, _ovPrevAmount);
        _ovViewports.Clear();
        _ovAmount.Clear();
    }
}
```

Worked check of the nesting rule (NestedProbe: `outer` clip { `inner` clip { 120 }, `innerTail` 40 }, then the
follower). `x(t)` is the §1 progress; both rows run the same spring, so with a common start `P_o = 160·x`, `P_i = 120·x`.

- **Open both (rv.8b).** Layout: inner 120, outer 160, `ContentExtent(outer) = 160`. Both rows seed at 0. Inner first (no
  inner of its own, unbounded): `d_i = 0 − 120 = −120`. It climbs into outer, stops there (outer is live) and sets
  `FlowInnerBit`. Outer, bounded: `shown = min(0, 160 − 120) = 0`, `d_o = 0 − 160 = −160`. The follower's shift is −160
  against a layout move of +160, so it is presented exactly where it started. Mid-flight (P_o = 80, P_i = 60):
  `min(80, 160 − 60) = 80`, `d_o = −80`. In general the bound is `40 + 120·x ≥ 160·x` for x ≤ 1, so the outer presents its
  own spring and the follower is monotone. The old plan summed: −280 on the seed frame, 120 DIP above start.
- **Close the outer over the open inner (rv.8c.1).** Layout: outer at `Height = 0` (laid out 0); inside it the inner is
  still 120 and the tail 40, so `ContentExtent(outer) = 160`. Only the outer row seeds (160 → 0); the inner has no row,
  nothing writes `FlowInnerBit`, so the outer is unbounded: `shown = P_o`, `d_o = P_o − 0`. The seed frame presents 160:
  the follower holds, then rides `160·(1 − x)`, the open mirrored. (The old `min(P, H + inner)` gave `min(160, 0 + 0) = 0`
  on the seed frame: the close snapped shut, rv.4 / rv.7 / rv.10a / cp3.b all failed.)
- **Close both together (rv.8c.2).** Layout: inner 0, tail at y 0, `ContentExtent(outer) = 40`; outer laid out 0. Inner
  first: `d_i = P_i − 0 = 120·(1 − x)` (positive: it presents more than it lays out); it stops at the outer and sets the bit.
  Outer, bounded: `shown = min(160·(1 − x), 40 + 120·(1 − x)) = 160·(1 − x)` (the bound is ≥ the spring for every x), so
  `d_o = 160·(1 − x)`. The seed frame presents 160 and the close is again the open mirrored.
- **Where the bound binds (continuity).** The bound only bites when the outer and inner run out of phase, e.g. the outer
  closes while the inner is still opening (P_i = 60 of 120, outer at rest at 160 presented as `160 + (60 − 120) = 100`):
  the outer's close seeds from its captured 160, and `min(160, ContentExtent 160 + (60 − 120)) = 100`, which is exactly
  what the previous frame presented. A min of two continuous curves is continuous, and when the inner settles its delta
  is 0, so the bound equals `ContentExtent` ≥ the outer's spring (it runs between extents its content had). Dropping the
  bound is then continuous too.

### 4.2 Edits in `src/FluentGpu.Engine/Animation/AnimScheduler.cs`

1. `IsSideTableChannel`: add `|| ch == AnimChannel.RevealExtent` to the expression.
2. `WriteSideTable`: add the case `case AnimChannel.RevealExtent: break;   // read straight off the row by PropagateFlowReveals`.
3. `RestDeltaFor`: `ch is AnimChannel.SizeW or AnimChannel.SizeH or AnimChannel.LayoutW or AnimChannel.LayoutH or AnimChannel.RevealExtent ? 0.5f : Generators.RestDelta`.
4. `CurrentValue` switch: add the arm `AnimChannel.RevealExtent => _scene.Bounds(node).H,` (every seed passes `initial`; this is the fallback).
5. `CollectAndFreeDone` — replace the foreach body:

```csharp
        foreach (int s in _settledScratch)
        {
            AnimChannel ch = _slab.At(s).Channel;
            if (ch == AnimChannel.RevealExtent) QueueSettled(_slab.At(s).Node, ch);   // a reveal at rest: its owner's callback
            SettleRestore(s);
            FreeSlot(s);
        }
```

6. `FreeSlot`: before `_slab.Free(slot);` add `if (r.Channel == AnimChannel.RevealExtent) _revealRows--;`
7. `CancelAll`: inside its loop add `if (_slab.At(s).Channel == AnimChannel.RevealExtent) _revealRows--;`
8. `Get`: after `int added = _slab.Add(idx, in seed);` add `if (ch == AnimChannel.RevealExtent) _revealRows++;`
9. `Tick`: the spring rest speed is per channel. `RevealExtent` is in DIP, so the normalized `Generators.RestSpeed` (0.01/s)
   held a 90-DIP reveal ~0.75 s and a 1300-DIP reveal ~1 s, well past the ~330 ms target, and kept the flow pass, scroll
   re-sync and re-record running for sub-pixel motion. The call is `Generators.EvalSpring(..., rd, RestSpeedFor(r.Channel), ...)`
   with `RestSpeedFor(RevealExtent) = 8f` DIP/s, next to `RestDeltaFor`. The 0.5-DIP rest distance decides when the row
   lands, and the snap to rest moves under 0.07 DIP per 120 Hz frame. The compositor never evaluates this row, so only
   `Tick` changes.

### 4.3 Edits in the other scheduler partials

- `AnimScheduler.Structural.cs`, `AnimateBounds`, inside `switch (mode)` after the `SizeMode.Reflow` case. The host's
  6.3 step owns the FlowReveal height: it seeds from the FLIP capture BEFORE layout effects, so the projection pass must
  neither re-seed nor snap it. Position still FLIPs as for any node.

```csharp
                case SizeMode.FlowReveal:   // the presented height was seeded at 6.3 (AppHost.SeedFlowRevealsPostLayout)
                    break;
```

- `AnimScheduler.Compositor.cs`, `IsCompositorRowStatic`: the channel line becomes
  `if (row.Channel is AnimChannel.LayoutW or AnimChannel.LayoutH or AnimChannel.DisclosureProgress or AnimChannel.RevealExtent) return false;`
- `AnimScheduler.Reflow.cs`: NO change. `RevealExtent` is deliberately NOT a structural channel, so
  `SnapStructuralToLayout` (suppressed / below-root projections) never cancels a reveal row behind the 6.3 step's back.
  The suppression decision for a FlowReveal belongs to 6.3 (`SnapFlowReveal`, which always queues the settle).
- `AnimScheduler.Parity.cs`, `ClearForIndex`: as the method's first statement add
  `FireSettleCallbacksForIndex(index);   // a dying node's settle callbacks run once and are dropped (smooth-reveal §4.1)`
  and append to its doc comment: "Settle callbacks registered on the slot (WhenSettled) are queued once and forgotten."

---

## 5. Reconciler hooks (Phase 1) — `src/FluentGpu.Engine/Reconciler/Reconciler.cs` + `Reconciler.Presence.cs`

### 5.1 Exit (`Remove`)

Replace the guard and the body of the exit branch (anchor: `if (!parked && Anim is { } anim && anim.TryGetTransition(node, out var spec) && spec.Exit.Active)`):

```csharp
        bool parked = (_scene.Flags(node) & NodeFlags.Parked) != 0;
        // A recycle (a rebind flush) never replays a FlowReveal: from the app's view it is the same persistent row.
        bool recycledReveal = SuppressBoundTransitions > 0;
        if (!parked && Anim is { } anim && anim.TryGetTransition(node, out var spec) && spec.Exit.Active
            && !(spec.Size == SizeMode.FlowReveal && recycledReveal))
        {
            // (keep the existing SizeMode.Reflow PendingExitReflow block and its comment unchanged here)
            // FlowReveal: read the live presented extent BEFORE the unmount below cancels the row, so an interrupted open
            // closes from where it stands (with its speed) instead of from the full height.
            bool flowReveal = spec.Size == SizeMode.FlowReveal;
            NodeHandle revealParent = flowReveal ? _scene.Parent(node) : NodeHandle.Null;
            float revealFrom = 0f, revealVelocity = 0f;
            if (flowReveal) anim.ReadFlowReveal(node, out revealFrom, out revealVelocity);
            UnmountSubtree(node);
            anim.CancelAll(node);
            _scene.Orphan(node, ExitMaxAgeMs(spec));
            anim.SeedExit(node, spec.Exit, spec);
            if (flowReveal)
            {
                anim.SeedFlowRevealExit(node, revealFrom, revealVelocity, in spec);
                anim.RevealExitCarriers.Add((node, revealParent));
            }
            return;
        }
```

(Keep every existing comment line of that branch; only the guard, the three FlowReveal lines before `UnmountSubtree`
and the `if (flowReveal)` block are new.)

### 5.2 Enter (three mount sites + presence)

At each of the three `isMount && …Enter.Active` sites (anchors: `if (isMount && at.Enter.Active)` in the BoxEl
`b.Animate` block; `if (isMount && dt.Enter.Active)` in the BoxEl declarative block; `if (isMount && dt.Enter.Active)`
in the ComponentEl anchor block), replace the `if` and its body with the pattern below (shown for `at`; use `dt` and
`danim` at the other two):

```csharp
                    if (isMount && at.Enter.Active && (at.Size != SizeMode.FlowReveal || SuppressBoundTransitions == 0))
                    {
                        anim.SeedEnter(node, at.Enter, at);
                        // SizeMode.Reflow enter: ease the layout size 0→natural AFTER layout so neighbours reflow as it
                        // reveals (host-driven; the natural size isn't known here, pre-layout).
                        if (at.Size == SizeMode.Reflow) anim.PendingEnterReflow.Add(node);
                        // SizeMode.FlowReveal enter: present 0 → laid-out height after layout (recycles never get here).
                        else if (at.Size == SizeMode.FlowReveal) anim.PendingEnterReveal.Add(node);
                    }
```

`Reconciler.Presence.cs` (the Visible false→true edge, already gated on `SuppressBoundTransitions == 0`): after
`if (dt.Size == SizeMode.Reflow) anim.PendingEnterReflow.Add(node);` add
`else if (dt.Size == SizeMode.FlowReveal) anim.PendingEnterReveal.Add(node);`

### 5.3 Realize window

In the virtual realize pass (anchor: `var rw = Virtualizer.Plan(ext, offset, velocity, viewport, in feel, sc.AnchorIndex);`)
replace `viewport` with `viewport + sc.RevealOverscan` (comment: `// + the rows a running reveal pulls into view`).

---

## 6. Host wiring (Phase 1)

The frame order this section builds (new steps in bold):

```
3−  DrainSettledCallbacks                      ← reveal settle callbacks the last frame queued
3–5 FlushToQuiescence (reconcile: Remove seeds FlowReveal EXITS; mounts queue PendingEnterReveal)
6   layout  (FlexLayout.DeferOffsetClamp = flowRevealFrame: no clamp against the LAID-OUT extent)
6.3 SeedFlowRevealsPostLayout  ← resizes (FLIP capture) + enters seeded; flow pass; scroll offsets clamped against the
                                 PRESENTED extent (SyncScrollPlansMidFrame) — before any layout effect reads them
6.5 DrainLayoutEffects; SyncScrollPlansMidFrame (presented extent)
    ApplyProjections (position FLIPs; FlowReveal shove carriers; never seeds/snaps a reveal row); clear carrier lists
7   Tick; RunIncrementalLayout; RunReflowLayout
7.05 RunFlowPass (flow pass; scroll sync against the presented extent; overscan realize); DeferOffsetClamp = false
7   … ReclaimSettledOrphans …; WakeFrame if settle callbacks are queued
7.7 PoseScrollUi (RefreshScrollExtents: presented extent; coverage ExtentTotal: presented)
```

### 6.1 `src/FluentGpu.Engine/Hosting/AppHost.cs` — frame order

1. Frame start. Immediately before `long tRx0 = Stopwatch.GetTimestamp();` (the line before `FlushToQuiescence();`):

```csharp
                _anim.DrainSettledCallbacks();   // 3− reveal settle callbacks the last frame queued (an Expander unmounting its
                                                 // panel, a band's collapse commit) — app code, so never inside phases 6–13
```

2. Layout's clamp deferral. Directly before `if (layoutNeeded && !_scene.Root.IsNull)` (the line after
   `_invalidator.BeginFrame(_timers.NowMs);`):

```csharp
            // SizeMode.FlowReveal: while a reveal is pending or running, layout must not clamp a scroll offset against the
            // LAID-OUT extent — 6.3 clamps it against the PRESENTED one (a collapse at the end of a scroller rides its edge
            // down instead of jumping in its commit frame). Reset after the 7.05 pass.
            bool flowRevealFrame = _anim.HasFlowRevealWork || (capturedProjections && CapturedFlowReveal());
            _layout.DeferOffsetClamp = flowRevealFrame;
```

3. Step 6.3. Directly after `long tSolve = Stopwatch.GetTimestamp();` and BEFORE `DrainLayoutEffects();`:

```csharp
            // 6.3 SizeMode.FlowReveal — BEFORE layout effects and the post-layout scroll sync: seed this commit's reveals
            // (resizes from the FLIP capture, enters), fold them into the presented flow, and clamp every scroll offset
            // against the PRESENTED extent. Effects and the sync below then see the geometry this frame presents.
            bool keepAliveSuppressed = _reconciler.ConsumeKeepAliveLayoutSuppressionFrame();   // (moved up from before ApplyProjections)
            if (flowRevealFrame && SeedFlowRevealsPostLayout(capturedProjections, Motion.LayoutTransitionsSuppressed || keepAliveSuppressed, layoutSize))
                reconciled = true;
```

   and DELETE the original line `bool keepAliveSuppressed = _reconciler.ConsumeKeepAliveLayoutSuppressionFrame();` above
   `if (capturedProjections) ApplyProjections(keepAliveSuppressed);` (the variable is now declared at 6.3, same scope).

4. Directly after `if (capturedProjections) ApplyProjections(keepAliveSuppressed);       // FLIP "Last+Invert+Play"`:

```csharp
            _anim.PendingEnterReveal.Clear();                  // seeded at 6.3, read by ApplyProjections as shove carriers
            _anim.RevealExitCarriers.Clear();
```

5. Directly after `RunReflowLayout(layoutSize);                       // 7 boundary-scoped re-solve …`:

```csharp
            // 7.05 presented flow (SizeMode.FlowReveal): fold the ticked reveal rows into the flow columns, re-clamp every
            // scroll extent against the PRESENTED content, and realize the rows a reveal pulls into view (seed frames only).
            RunFlowPass(layoutSize);
            _layout.DeferOffsetClamp = false;
```

6. Directly after `_connected.SyncDetached();                         // 7 flag-gated rebuild …` (after
   `ReclaimSettledOrphans()`, so a callback queued by a reclaim — ClearForIndex — also keeps a frame coming):

```csharp
            if (_anim.HasSettledCallbacks) WakeFrame();        // reveal settle callbacks drain at the next frame's start
```

7. New helpers (place them after `ApplyProjections`, before `IsReflowShoved`):

```csharp
    /// <summary>True when this commit's FLIP capture holds a SizeMode.FlowReveal node (its height may change in the
    /// layout about to run, so layout defers its scroll-offset clamp to 6.3).</summary>
    private bool CapturedFlowReveal()
    {
        foreach (var kv in _projectBefore)
            if (_scene.IsLive(kv.Key) && _anim.TryGetTransition(kv.Key, out var t) && t.Size == SizeMode.FlowReveal) return true;
        return false;
    }

    /// <summary>6.3 — SizeMode.FlowReveal seeds, right after layout. A captured FlowReveal node whose laid-out height
    /// changed springs its presented height from the old one; an entrant from 0 (or from the exit orphan it reclaims).
    /// Under suppression (a user scroll in flight, a keep-alive switch) each lands at once, and its settle callback still
    /// fires. Then the flow pass runs and every scroll plan is re-synced against the PRESENTED extent before any layout
    /// effect reads an offset. Returns true when the sync re-realized virtual rows (the caller marks the frame reconciled).</summary>
    private bool SeedFlowRevealsPostLayout(bool capturedProjections, bool suppressed, Size2 layoutSize)
    {
        if (capturedProjections)
            foreach (var kv in _projectBefore)
            {
                NodeHandle n = kv.Key;
                if (!_scene.IsLive(n) || !_anim.TryGetTransition(n, out var spec) || spec.Size != SizeMode.FlowReveal
                    || (spec.Channels & TransitionChannels.Size) == 0 || (spec.Axes & SizeAxes.Height) == 0) continue;
                float fromH = kv.Value.Rel.H, toH = _scene.Bounds(n).H;
                if (MathF.Abs(fromH - toH) < 0.5f) continue;
                if (suppressed || n == _dispatcher.Drag.ActiveNode) _anim.SnapFlowReveal(n, fromH, toH);
                else _anim.SeedFlowRevealResize(n, fromH, toH, in spec.Dynamics);
            }
        var enters = _anim.PendingEnterReveal;
        for (int i = 0; i < enters.Count; i++)
        {
            if (!_scene.IsLive(enters[i])) continue;
            if (suppressed) _anim.SnapFlowReveal(enters[i], 0f, _scene.Bounds(enters[i]).H);
            else _anim.SeedFlowRevealEnter(enters[i]);
        }
        return RunFlowPass(layoutSize, syncAlways: true);
    }

    /// <summary>The presented-flow pass and what hangs off it: fold the reveal rows into the flow columns, re-sync every
    /// scroll plan against the PRESENTED content extent, and re-realize + re-lay out once when the sync moved a window
    /// (<paramref name="syncAlways"/>: 6.3, where layout deferred its clamp) or a reveal's overscan grew. True when it
    /// re-realized.</summary>
    private bool RunFlowPass(Size2 layoutSize, bool syncAlways = false)
    {
        bool flowed = _anim.PropagateFlowReveals();
        if (!flowed && !syncAlways) return false;
        bool moved = SyncScrollPlansMidFrame();
        if (!(_anim.FlowOverscanGrew || (syncAlways && moved)) || _scene.Root.IsNull || !_reconciler.ReRealizeVirtuals()) return false;
        if (_runtime.HasPending) FlushRebindsToQuiescence();
        _reconciler.ConsumeReconciled();
        _invalidator.RunDirty(layoutSize);
        _scene.ClearLayoutDirty();
        _anim.PropagateFlowReveals();
        return true;
    }
```

Why both commit-frame clamps are now correct (rv.10a):
- (a) `FlexLayout`'s viewport arrange skips `if (sc.Offset > max) sc.Offset = max;` on a flow-reveal frame (§6.4), so the
  content is posed at the plan's offset. `RevealView` therefore reads the region's true screen top, and the
  visible-span clamp departs from the full extent. It no longer departs from a part of it.
- (b) 6.3 seeds the rows and runs the flow pass BEFORE `SyncScrollPlansMidFrame`. The content's `FlowDelta` already
  carries the reveal when `SetExtent(PresentedContentMain)` runs, so an Idle plan holding at the old max is not
  re-held at the laid-out max. The later syncs (2.5, 7.05, 7.7) each see a presented extent that only shrinks, so the
  offset rides the edge down monotonically.
- (c) Phase 2, bands: a committed collapse's band stops presenting at 6.3 of the very frame its rows leave the model
  (`RevealBand.Presents`, §10). The 6.3 sync therefore sees exactly the laid-out extent, which equals the presented one the
  frame before (band at 0). The offset of a list scrolled to its end holds through the commit frame (rv.band.8).
- With no reveal anywhere, `flowRevealFrame` is false: layout clamps exactly as before and 6.3 does not run.

### 6.2 `AppHost.cs` — `ApplyProjections` reveal-shove suppression

A sibling the commit moved BECAUSE a reveal's layout snapped is carried by the presented flow; a position FLIP on top
would double it. After the existing loop that fills `_reflowShoveFrames` from `_liveReflowScratch` (anchor: the loop
ending in `_reflowShoveFrames.TryAdd((int)p.Raw.Index, (int)c.Raw.Index);`), add:

```csharp
        // Reveal-shove frames (SizeMode.FlowReveal): the reveal's presented flow IS the sibling's motion, so a node moved
        // by a reveal's layout snap must not position-FLIP. Carriers: a projected FlowReveal node whose height changed this
        // commit, a FlowReveal entrant mounted this commit, a FlowReveal exit orphaned this commit (under its old parent).
        foreach (var kv in _projectBefore)
        {
            NodeHandle rn = kv.Key;
            if (!_scene.IsLive(rn) || !_anim.TryGetTransition(rn, out var rs) || rs.Size != SizeMode.FlowReveal) continue;
            if (MathF.Abs(kv.Value.Rel.H - _scene.Bounds(rn).H) < SizeEps) continue;
            AddShoveCarrier(rn, _scene.Parent(rn));
        }
        var revealEnters = _anim.PendingEnterReveal;
        for (int i = 0; i < revealEnters.Count; i++)
            if (_scene.IsLive(revealEnters[i])) AddShoveCarrier(revealEnters[i], _scene.Parent(revealEnters[i]));
        var revealExits = _anim.RevealExitCarriers;
        for (int i = 0; i < revealExits.Count; i++)
            if (_scene.IsLive(revealExits[i].VisualParent)) AddShoveCarrier(revealExits[i].Node, revealExits[i].VisualParent);
```

In the per-node loop change `if (_liveReflowScratch.Count > 0 && IsReflowShoved(n))` to
`if (_reflowShoveFrames.Count > 0 && IsReflowShoved(n))`. Add the helper next to `IsReflowShoved`:

```csharp
    /// <summary>Map every hop of <paramref name="carrier"/>'s chain (starting at <paramref name="parent"/>, its parent or an
    /// exit orphan's former visual parent) to the child that carries the move — IsReflowShoved's frame map.</summary>
    private void AddShoveCarrier(NodeHandle carrier, NodeHandle parent)
    {
        for (NodeHandle c = carrier, p = parent; !p.IsNull && _scene.IsLive(p); c = p, p = _scene.Parent(p))
            _reflowShoveFrames.TryAdd((int)p.Raw.Index, (int)c.Raw.Index);
    }
```

### 6.3 `src/FluentGpu.Engine/Hosting/AppHost.Scroll.cs` — presented extent

Add next to `TryGetScrollHandle`:

```csharp
    /// <summary>The PRESENTED main-axis content extent: the laid-out one plus the content node's live flow delta
    /// (SizeMode.FlowReveal, reveal bands). Every scroll plan clamps against this, so a collapse at the end of a list rides
    /// its edge down instead of clamping the offset in one frame. The laid-out extent at rest.</summary>
    private float PresentedContentMain(in ScrollState sc)
    {
        float main = sc.ContentMain;
        if (sc.Orientation == 0 && !sc.ContentNode.IsNull && _scene.IsLive(sc.ContentNode))
            main = MathF.Max(0f, main + _scene.Paint(sc.ContentNode).FlowDelta);
        return main;
    }
```

Every `SetExtent` site uses it (four sites; leave none on the laid-out extent):
- `RunScrollFrame`: `handle.SetExtent(sc.ContentMain * zoom, sc.ViewportMain);` →
  `handle.SetExtent(PresentedContentMain(in sc) * zoom, sc.ViewportMain);`, and
  `Virtualizer.Plan(ext, shown, v, sc.ViewportMain, in feel, sc.AnchorIndex)` →
  `Virtualizer.Plan(ext, shown, v, sc.ViewportMain + sc.RevealOverscan, in feel, sc.AnchorIndex)`.
- `SyncScrollPlansMidFrame`: `handle.SetExtent(peek.ContentMain * (peek.ZoomFactor > 0f ? peek.ZoomFactor : 1f), peek.ViewportMain);`
  → `handle.SetExtent(PresentedContentMain(in peek) * (peek.ZoomFactor > 0f ? peek.ZoomFactor : 1f), peek.ViewportMain);`
  and its `Virtualizer.Plan(..., sc.ViewportMain, ...)` → `sc.ViewportMain + sc.RevealOverscan`.
- `RefreshScrollExtents` (7.7, inside `PoseScrollUi`):
  `ResolveScrollHandle(idx).SetExtent(sc.ContentMain * zoom, sc.ViewportMain);` →
  `ResolveScrollHandle(idx).SetExtent(PresentedContentMain(in sc) * zoom, sc.ViewportMain);`. (Left on the laid-out
  extent, 7.7 would re-hold the plan at the laid-out max every reveal frame: the end-of-list jump this plan removes.)
- `FillScrollCoverage`: `double extentTotal = sc.Extent is { } ext ? ext.Total : sc.ContentMain;` →
  `double extentTotal = (sc.Extent is { } ext ? ext.Total : sc.ContentMain) + (sc.Orientation == 0 && !content.IsNull ? _scene.Paint(content).FlowDelta : 0f);`
  (comment: `// the PRESENTED extent: the thumb effect and the coverage's true end follow a running reveal`).

`SyncScrollPlansMidFrame` also re-poses the content when it moves an offset. Layout posed it for the pre-sync offset,
and a layout effect or the flow pass that reads `AbsoluteRect` this frame must see where the content will be drawn.
Directly after `sc.Offset = shown;` add:

```csharp
            // Re-pose the content for the moved offset NOW (layout posed it for the pre-sync one): a layout effect or the
            // flow pass reading AbsoluteRect this frame sees where the content is drawn. PoseScrollUi re-poses at 7.7 anyway.
            if (!sc.ContentNode.IsNull && _scene.IsLive(sc.ContentNode))
            {
                float trans = ScrollContentPose.Translate(sc.WindowOrigin, shown, _scene.DeviceScale);
                ref NodePaint cp = ref _scene.Paint(sc.ContentNode);
                Affine2D before = cp.LocalTransform;
                ScrollContentPose.WriteContentTransform(ref cp, in _scene.Bounds(sc.ContentNode), sc.Orientation == 1, trans, sc.ZoomFactor);
                if (!before.Equals(cp.LocalTransform)) _scene.Mark(sc.ContentNode, NodeFlags.TransformDirty);
            }
```

(`Affine2D` is `FluentGpu.Foundation`; add the `using` if the file lacks it.)

### 6.4 `src/FluentGpu.Engine/Layout/FlexLayout.cs`

1. Add the property (next to the other public instance members, near the top of the class):

```csharp
    /// <summary>Host-set for one frame while SizeMode.FlowReveal work is pending or running (AppHost step 6): the viewport
    /// arrange then does NOT clamp a scroll offset down to the LAID-OUT max. The host clamps it right after layout (6.3)
    /// against the PRESENTED extent, so a collapse at the end of a scroller rides its edge down. False otherwise and for
    /// every standalone layout run (unchanged behaviour).</summary>
    public bool DeferOffsetClamp { get; set; }
```

2. In the viewport arrange's content-translate block, replace `if (sc.Offset > max) sc.Offset = max;` with
   `if (sc.Offset > max && !DeferOffsetClamp) sc.Offset = max;   // a reveal frame: AppHost 6.3 clamps against the PRESENTED extent`
   and extend the comment above the block: "A shown offset past the new clamp lands on the clamp (the plan is
   re-clamped by the host's SetExtent in the same frame) — except on a SizeMode.FlowReveal frame
   (<see cref="DeferOffsetClamp"/>), where the host clamps against the presented extent at 6.3."
3. In the D1 realize-after-layout block (anchor: `var rw = Virtualizer.Plan(ext, sc.Offset, sc.Velocity, vpExtent, in feel, sc.AnchorIndex);`)
   replace `vpExtent` with `vpExtent + (horizontal ? 0f : sc.RevealOverscan)`.

---

## 7. `FlowCursor` + recorder + hit-test + presented geometry (Phase 1)

### 7.1 New file `src/FluentGpu.Engine/Scene/FlowCursor.cs`

```csharp
namespace FluentGpu.Scene;

/// <summary>The presented-flow walk ONE parent runs over its children (SizeMode.FlowReveal): each child draws and
/// hit-tests at its laid-out position plus a running Y shift; the shift grows by every earlier sibling's
/// <see cref="NodePaint.FlowDelta"/>, and a revealing exit orphan pushes the children laid out at/below its old top. Shared
/// verbatim by SceneRecorder.Walk, InputDispatcher.Hit/HitAny and SceneStore.PresentedAbsoluteRect so paint, input and
/// geometry queries agree. POD, allocation-free, O(1) per child.</summary>
internal struct FlowCursor
{
    private float _shift;
    private float _orphanTop, _orphanDelta;
    private bool _orphanPending;
    private bool _active;

    /// <summary>False at rest: every <see cref="Step"/> would return 0, so callers skip the walk entirely.</summary>
    public readonly bool Active => _active;

    /// <summary>The cursor for a parent whose paint is <paramref name="p"/>.</summary>
    public static FlowCursor For(in NodePaint p)
    {
        var c = new FlowCursor();
        if ((p.FlowBits & NodePaint.FlowShiftsBit) == 0) return c;
        c._active = true;
        c._orphanPending = (p.FlowBits & NodePaint.FlowOrphanBit) != 0;
        c._orphanTop = p.FlowOrphanTop;
        c._orphanDelta = p.FlowOrphanDelta;
        return c;
    }

    /// <summary>Advance over the child at <paramref name="ordinal"/> laid out at <paramref name="childTop"/> (parent-local)
    /// whose own flow delta is <paramref name="childFlowDelta"/>; returns the Y shift to draw / hit-test it at.
    /// <paramref name="clipTop"/>/<paramref name="clipBottom"/> (parent-local) bound it when a reveal band clips it, NaN
    /// otherwise (bands land in Phase 2). Call it for EVERY child in order, including ones the caller then skips.</summary>
    public float Step(int ordinal, float childTop, float childFlowDelta, out float clipTop, out float clipBottom)
    {
        clipTop = clipBottom = float.NaN;
        if (_orphanPending && childTop >= _orphanTop - 0.5f)
        {
            _shift += _orphanDelta;
            _orphanPending = false;
        }
        float s = _shift;
        _shift += childFlowDelta;
        return s;
    }
}
```

### 7.2 New file `src/FluentGpu.Engine/Scene/SceneStore.Flow.cs`

```csharp
using FluentGpu.Foundation;

namespace FluentGpu.Scene;

public sealed partial class SceneStore
{
    /// <summary>The rect <paramref name="h"/> is PRESENTED at (window DIP): <see cref="AbsoluteRect"/> plus every
    /// SizeMode.FlowReveal shift the recorder applies up its chain, at its presented height. Walks each shifting
    /// ancestor's children up to the chain node (O(depth × siblings)) — gates, diagnostics and cold geometry queries only.
    /// At rest it equals AbsoluteRect.</summary>
    public RectF PresentedAbsoluteRect(NodeHandle h)
    {
        RectF a = AbsoluteRect(h);
        float dy = 0f;
        for (NodeHandle n = h, parent = Parent(h); !parent.IsNull; n = parent, parent = Parent(parent))
        {
            var cursor = FlowCursor.For(in _paint[parent.Raw.Index]);
            if (!cursor.Active) continue;
            int ordinal = 0;
            for (var c = FirstChild(parent); !c.IsNull; c = NextSibling(c), ordinal++)
            {
                float s = cursor.Step(ordinal, _bounds[c.Raw.Index].Y, _paint[c.Raw.Index].FlowDelta, out _, out _);
                if (c == n) { dy += s; break; }
            }
        }
        ref readonly NodePaint p = ref _paint[h.Raw.Index];
        return new RectF(a.X, a.Y + dy, a.W, float.IsNaN(p.PresentedH) ? a.H : p.PresentedH);
    }
}
```

### 7.3 `src/FluentGpu.Engine/Render/SceneRecorder.cs`

1. Main child loop. Directly before `for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))` of the
   sticky/hover-elevate block (the loop that starts `if (hasItemBand && !bandStarted && childOrdinal >= itemBandPrefix)`), add
   `var flow = FlowCursor.For(in p);   // SizeMode.FlowReveal: children ride their earlier siblings' presented flow`.
   Then directly after `Affine2D activeChildWorld = childWorld;` add:

```csharp
                if (flow.Active)
                {
                    float flowShift = flow.Step(ordinal, scene.Bounds(c).Y, scene.Paint(c).FlowDelta, out float flowClipTop, out float flowClipBottom);
                    if (flowShift != 0f) activeChildWorld = activeChildWorld.Translate(0f, flowShift);
                    if (!float.IsNaN(flowClipTop))
                        activeChildClip = activeChildClip.Intersect(childWorld.TransformBounds(new RectF(0f, flowClipTop,
                            MathF.Max(1f, scene.Bounds(node).W), MathF.Max(0f, flowClipBottom - flowClipTop))));
                }
```

2. Pinned replay loop (anchor: `for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c), pinnedOrdinal++)`
   followed by `if ((scene.Flags(c) & NodeFlags.StickyPinned) != 0)`). Rewrite the loop head so every child steps a
   cursor, then the existing body runs for pinned children only:

```csharp
                var pinnedFlow = FlowCursor.For(in p);
                for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c), pinnedOrdinal++)
                {
                    float pinnedShift = 0f, pinnedClipTop = float.NaN, pinnedClipBottom = float.NaN;
                    if (pinnedFlow.Active)
                        pinnedShift = pinnedFlow.Step(pinnedOrdinal, scene.Bounds(c).Y, scene.Paint(c).FlowDelta, out pinnedClipTop, out pinnedClipBottom);
                    if ((scene.Flags(c) & NodeFlags.StickyPinned) == 0) continue;
                    // … the existing body of the old `if (pinned) { … }`, unchanged, EXCEPT: directly after
                    // `Affine2D pinnedWorld = childWorld;` add the two lines below.
                    if (pinnedShift != 0f) pinnedWorld = pinnedWorld.Translate(0f, pinnedShift);
                    if (!float.IsNaN(pinnedClipTop))
                        pinnedClip = pinnedClip.Intersect(childWorld.TransformBounds(new RectF(0f, pinnedClipTop,
                            MathF.Max(1f, scene.Bounds(node).W), MathF.Max(0f, pinnedClipBottom - pinnedClipTop))));
                }
```

3. `MixPaintReveal`: after `MixFloat(ref h, p.ChildShiftY);` add

```csharp
        MixFloat(ref h, p.FlowDelta);          // SizeMode.FlowReveal: the presented flow moves this node's later children
        MixFloat(ref h, p.FlowOrphanTop);
        MixFloat(ref h, p.FlowOrphanDelta);
        Mix(ref h, p.FlowBits);
```

4. Scroll chrome measures the presented content. In the chrome block (anchor:
   `if (overlapsRecordClip && (flags & NodeFlags.Scrollable) != 0 && scene.TryGetScroll(node, out var sec))`), as the
   block's first statement add `sec.ContentH = PresentedContentH(scene, in sec);   // thumb + chevrons follow a running reveal`,
   and add the helper next to `ScrollOverflows`:

```csharp
    /// <summary>The PRESENTED vertical content extent: the laid-out one plus the content node's live flow delta
    /// (SizeMode.FlowReveal) — what the scrollbar measures against while a reveal runs. The laid-out one at rest.</summary>
    internal static float PresentedContentH(SceneRecordingSnapshot scene, in ScrollState sc)
        => sc.Orientation != 0 || sc.ContentNode.IsNull || !scene.IsLive(sc.ContentNode)
            ? sc.ContentH
            : MathF.Max(0f, sc.ContentH + scene.Paint(sc.ContentNode).FlowDelta);
```

### 7.4 `src/FluentGpu.Engine/Input/InputDispatcher.cs` — `Hit` and `HitAny` (identical edit in both)

Before `for (var c = _scene.FirstChild(node); …; childOrdinal++)` add `var flow = FlowCursor.For(in np);`. Replace the
loop's first two statements (`if (hasItemBand && …) continue;` and `Point2 presentedPoint = childLocal;`) with:

```csharp
            float flowShift = 0f, flowClipTop = float.NaN, flowClipBottom = float.NaN;
            if (flow.Active)   // step EVERY child (the item-band skip below must not desync the cursor)
                flowShift = flow.Step(childOrdinal, _scene.Bounds(c).Y, _scene.Paint(c).FlowDelta, out flowClipTop, out flowClipBottom);
            if (hasItemBand && childOrdinal >= itemBandPrefix && !itemBandAllowsPoint) continue;
            if (!float.IsNaN(flowClipTop) && (childLocal.Y < flowClipTop || childLocal.Y >= flowClipBottom)) continue;
            Point2 presentedPoint = flowShift != 0f ? new Point2(childLocal.X, childLocal.Y - flowShift) : childLocal;
```

(the existing `if (hasDisclosure) { … }` block stays after it in Phase 1; Phase 2 deletes it).

---

## 8. The engine `Expander` migrated (Phase 1) — `src/FluentGpu.Controls/Expander.cs`

1. Class remarks: replace the "MOTION — a DELIBERATE divergence from WinUI …" paragraph with:

```
/// MOTION — NOT WinUI's (Expander.xaml snaps the layout space and slides only the content). The content clip wrapper's
/// declared Height toggles 0 ↔ NaN(auto): layout lands ONCE at the new size and <see cref="SizeMode.FlowReveal"/> springs
/// the PRESENTED height under <c>MotionTok.Reveal</c> (critically damped, ~0.31 s, the same spring both ways) while every
/// sibling below rides the difference at paint time — no per-frame layout, no component render, interruptible from the
/// live value with its velocity. <see cref="SizeAnchor.Parallax"/>: the panel trails the moving edge by a damped share of
/// what is still hidden (≤ 24 DIP), so it reads as unfolding rather than wiped. The chevron rotates on the same spring.
/// The panel stays mounted through a close and unmounts when the engine reports the reveal at rest
/// (<c>AnimEngine.WhenSettled</c>) — no control-local ticker, no per-frame watcher.
```

2. `ExpanderOptions.AnimateContentResize` doc: replace "replays the disclosure tween (333ms FluentPopOpen expand / 167ms
   collapse, `SizeMode.Reflow`)" with "reveals the new height (the FlowReveal spring)", and "a steady-open Expander whose
   content resizes re-lays out in one instant frame instead of replaying the disclosure motion" stays.
3. `PartClip` doc: "the SizeMode.Reflow host" → "the SizeMode.FlowReveal host"; drop the "also transform-owned mid-motion
   (Trailing anchor)" note, replace with "its children are shifted mid-motion (Parallax ChildShiftY) — do not add a
   ChildShift-owning scroll effect here".
4. Replace the WinUI timing comment block, `const float ChevronMs`, `static readonly LayoutTransition Reflow` and
   `ReflowNoResize` with:

```csharp
    // The one disclosure motion (docs/plans/smooth-reveal-implementation.md §1): layout snaps once, the presented height
    // springs under MotionTok.Reveal, siblings ride it at paint time; Parallax lets the panel trail the edge.
    static readonly LayoutTransition Reveal = new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(),
        Size: SizeMode.FlowReveal, Anchor: SizeAnchor.Parallax);
    // ExpanderOptions.AnimateContentResize=false's STEADY-state spec: no Size channel, so a settled-open clip host's content
    // change lands in the next layout as is. Only used while no toggle is in flight (see `animateResize` in Render).
    static readonly LayoutTransition RevealNoResize = Reveal with { Channels = TransitionChannels.None };
```

5. `Render()` — replace everything from `var (localOpen, setLocalOpen) = UseState(InitiallyExpanded);` down to (and
   including) the line `bool animateResize = Options.AnimateContentResize || isTransitioning;` with the block below.
   The open/close bookkeeping moves into ONE layout effect. Layout effects run at 6.5, after the host seeded this
   commit's reveal (6.3), so the effect can see whether a reveal row is live. When none is (a suppressed projection
   mid-fling, a zero-height panel, a resize frame, a missed capture), the motion is already over and the effect settles
   it now. The panel can no longer stay mounted forever waiting for a callback that never comes.

```csharp
        var (localOpen, setLocalOpen) = UseState(InitiallyExpanded);
        // Controlled (IsExpanded signal) or local state — reading the signal subscribes this component, so external
        // writes (an "expand all" button) re-render and run the full open/close motion.
        bool open = IsExpanded is { } ext ? ext.Value : localOpen;
        // The panel's MOUNT lags `open` on close: it stays mounted while the presented height closes over it and unmounts
        // when the reveal settles (the settle callback below, or the always-fire check in the layout effect).
        var shown = UseSignal(IsExpanded is { } init ? init.Peek() : InitiallyExpanded);
        var settings = ExpanderTemplateSettings.For(open);   // typed computed settings drive the chevron
        var chevronRef = UseRef<NodeHandle>(default);
        var clipRef = UseRef<NodeHandle>(default);
        var chevronSeeded = UseRef(false);
        var openNow = UseRef(open);     // the settle callback runs outside render: it reads the committed open state here
        var lastOpen = UseRef(open);    // the open state the previous commit rendered — a toggle render sees open != lastOpen
        // Up from a toggle until its reveal settles: keeps the Size channel on the clip while a toggle is in flight even
        // when ExpanderOptions.AnimateContentResize=false (only the steady state drops it).
        var transitioning = UseSignal(false);

        bool showContent = shown.Value;          // subscribe: the settle write re-renders this component

        // Settle: the panel unmounts when closed, and either way the toggle window ends. Idempotent: the engine callback
        // and the always-fire check below may both run for one toggle.
        Action onSettled = UseMemo<Action>(() => () =>
        {
            if (!openNow.Value) shown.Value = false;
            transitioning.Value = false;
        }, DepKey.Empty);

        // The open/close bookkeeping, at 6.5 (after the host seeded this commit's reveal at 6.3):
        //  • an EXTERNAL open (controlled-signal write, not a header click) mounts the panel; it reveals from 0 next frame;
        //  • ALWAYS-FIRE: with a toggle in flight and NO live reveal row after this commit's layout, nothing will ever call
        //    back — settle now.
        UseLayoutEffect(() =>
        {
            openNow.Value = open;
            if (lastOpen.Value != open) transitioning.Value = true;
            lastOpen.Value = open;
            if (open && !shown.Peek()) { shown.Value = true; return; }
            if (!transitioning.Peek()) return;
            var anim = Context.Anim;
            var node = clipRef.Value;
            if (anim is null || node.IsNull || !anim.IsRevealing(node)) onSettled();
        }, DepKey.From(open ? 1 : 0, showContent ? 1 : 0));

        // The chevron rides the SAME spring as the reveal, so the glyph and the edge land together; a mid-flight toggle
        // retargets from the live angle with its velocity. The first mount seeds the resting angle with no motion.
        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || chevronRef.Value.IsNull || !scene.IsLive(chevronRef.Value)) return;
            float to = settings.ChevronRotationDeg;
            if (!chevronSeeded.Value)
            {
                chevronSeeded.Value = true;
                anim.SeedValue(chevronRef.Value, AnimChannel.Rotation, to, MotionTokenId.Reveal, from: to);
                return;
            }
            anim.SeedValue(chevronRef.Value, AnimChannel.Rotation, to, MotionTokenId.Reveal);
        }, open);

        // The engine calls back (next frame start) when the clip's reveal rests, snapped ones included, and once more if
        // the clip node dies. One callback per clip node, unregistered on unmount.
        UseEffect(() =>
        {
            var anim = Context.Anim;
            var node = clipRef.Value;
            if (anim is null || node.IsNull) return null;
            anim.WhenSettled(node, AnimChannel.RevealExtent, onSettled);
            return () => anim.WhenSettled(node, AnimChannel.RevealExtent, null);
        }, DepKey.Empty);

        bool toggled = open != lastOpen.Value;
        bool animateResize = Options.AnimateContentResize || toggled || transitioning.Value;
```

   Toggle walk-through (all gated by cp3.* and rv.14):
   - Header click open: `toggle` writes open and `shown` in one batch, so the content mounts and the clip goes 0 → H in
     ONE commit. 6.3 seeds the row, the effect sees it live and waits, and the engine settle ends `transitioning`.
   - Close: the clip goes H → 0 with the content still mounted. 6.3 seeds the row, and the settle unmounts the panel.
   - Close mid-fling (`Motion.LayoutTransitionsSuppressed`): 6.3 snaps (`SnapFlowReveal`, which queues the settle), and
     the effect finds no row and settles at once. The panel unmounts on the next frame.
   - External open: the commit has no content yet (H stays 0, no row). The effect mounts the panel and returns without
     settling. The next commit reveals it; the effect (re-run on `showContent`) sees the row and waits.
   - `transitioning` starts false. The toggle RENDER still carries the Size channel, through `toggled`
     (`open != lastOpen`): `lastOpen` updates only in the layout effect, after that commit.

6. In the clip wrapper and its `parts` re-assertion: `Animate = animateResize ? Reflow : ReflowNoResize` →
   `Animate = animateResize ? Reveal : RevealNoResize` (both places). Comment above `Element[] clipKids`: replace the
   "SizeMode.Reflow track eases the LAYOUT height … Trailing anchor …" sentences with "the host's projection diffs old vs
   new size and the SizeMode.FlowReveal row springs the presented height; siblings ride it at paint time."
7. Children: replace the `Element[] children = closing ? … : …;` statement and its comment with
   `Element[] children = [header, contentClip];` (comment: `// The card root mirrors the template's root Grid: pure layout, NO fill/border/clip.`).
   Delete the now-unused `bool closing` line if still present.
8. Delete the classes `ExpanderCollapseWatcher` and `ExpanderResizeWatcher` (end of file).

---

## 9. Phase 1 gates

### 9.1 New file `src/FluentGpu.VerticalSlice/Suites/RevealSuite.cs` (+ registry)

`Harness/SuiteRegistry.cs`: add after the `anim` entry:
`new("reveal", "anim", FluentGpu.VerticalSlice.Suites.RevealSuite.Run),   // SizeMode.FlowReveal: layout once, motion at paint time (smooth-reveal plan §9)`

```csharp
using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

// ── SizeMode.FlowReveal — the smooth-reveal primitive (docs/plans/smooth-reveal-implementation.md §9). Every gate runs
// headless at a FIXED dt (8.33 / 16.67 ms) and reads PRESENTED geometry (SceneStore.PresentedAbsoluteRect), never layout:
// a FlowReveal lands layout once and moves only at paint time.
static class RevealSuite
{
    static readonly LayoutTransition Reveal = new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(), Size: SizeMode.FlowReveal);
    static readonly LayoutTransition RevealEnterExit = Reveal with { Enter = new EnterExit(Active: true), Exit = new EnterExit(Active: true) };

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        NoLayoutNoRenderNoAlloc(strings, fonts);
        FirstFrameAndStepBounds(strings, fonts);
        Mirror(strings, fonts);
        Lockstep(strings, fonts);
        TallRevealInViewport(strings, fonts);
        Reverse(strings, fonts);
        ConcurrentAndNested(strings, fonts);
        NestedClose(strings, fonts);
        HitTest(strings, fonts);
        ScrollClampAndAnchor(strings, fonts);
        RecycleSuppression(strings);
        AsyncGrowth(strings, fonts);
        ReducedMotionSnaps(strings, fonts);
        SuppressedCloseSettles(strings, fonts);
        SettleOnFree(strings);
        GrowChildOfScrollContent(strings, fonts);
    }

    /// <summary>The root column holds ONE inner column ("col": a 40-DIP head, N reveal clip wrappers whose declared Height
    /// toggles 0 ↔ auto over a body of BodyH[i], then two 30-DIP followers). The inner column is not the scene root, so its
    /// PresentedH is the presented parent bottom (the root is a flow boundary).</summary>
    sealed class RevealColumnProbe : Component
    {
        public required Signal<bool>[] Open;
        public required Signal<float>[] BodyH;
        public readonly Signal<int> Clicks = new(0);
        public override Element Render()
        {
            var kids = new List<Element>(Open.Length + 3) { new BoxEl { Key = "head", Height = 40f } };
            for (int i = 0; i < Open.Length; i++)
            {
                int k = i;
                kids.Add(new BoxEl
                {
                    Key = "clip" + k, Direction = 1, Height = Open[k].Value ? float.NaN : 0f, Animate = Reveal,
                    Children = [new BoxEl { Key = "body" + k, Height = Prop.Of(() => BodyH[k].Value), Fill = Tok.FillCardDefault }],
                });
            }
            kids.Add(new BoxEl { Key = "after", Height = 30f, OnClick = () => Clicks.Value = Clicks.Peek() + 1 });
            kids.Add(new BoxEl { Key = "after2", Height = 30f });
            return new BoxEl { Direction = 1, Children = [new BoxEl { Key = "col", Direction = 1, Width = 300f, Children = kids.ToArray() }] };
        }
    }

    sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly HeadlessWindow Window;
        public readonly AppHost Host;
        public Rig(StringTable strings, HeadlessFontSystem fonts, Component root, float dtMs, float w = 360f, float h = 640f)
        {
            Window = new HeadlessWindow(new WindowDesc("reveal", new Size2(w, h), 1f));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), fonts, strings, root, frameTime: new FixedFrameTimeSource(dtMs));
            for (int i = 0; i < 4; i++) Host.RunFrame();
        }
        public SceneStore Scene => Host.Scene;
        public NodeHandle Col => Child(Scene, Scene.Root, 0);
        public NodeHandle ColChild(int i) => Child(Scene, Col, i);
        public float Y(NodeHandle n) => Scene.PresentedAbsoluteRect(n).Y;
        public float LayoutY(NodeHandle n) => Scene.AbsoluteRect(n).Y;
        public bool Revealing(NodeHandle n) => Host.Animation.TryGetTrackValue(n, AnimChannel.RevealExtent, out _);
        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    static RevealColumnProbe Column(params float[] bodies)
    {
        var open = new Signal<bool>[bodies.Length];
        var body = new Signal<float>[bodies.Length];
        for (int i = 0; i < bodies.Length; i++) { open[i] = new Signal<bool>(false); body[i] = new Signal<float>(bodies[i]); }
        return new RevealColumnProbe { Open = open, BodyH = body };
    }

    // Run frames while `clip` reveals (cap 90), recording the PRESENTED y of `track` after each one.
    static List<float> Trace(Rig rig, NodeHandle clip, NodeHandle track)
    {
        var ys = new List<float>();
        for (int i = 0; i < 90 && (i < 2 || rig.Revealing(clip)); i++) { rig.Host.RunFrame(); ys.Add(rig.Y(track)); }
        return ys;
    }

    static float MaxStep(float start, List<float> ys)
    {
        float m = 0f, prev = start;
        foreach (float y in ys) { m = MathF.Max(m, MathF.Abs(y - prev)); prev = y; }
        return m;
    }

    // rv.1 + rv.2 — the whole point: once seeded, a reveal is paint-only.
    static void NoLayoutNoRenderNoAlloc(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(160f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();   // the commit frame: render + layout + seed (presents the old geometry)
        rig.Host.RunFrame();   // the first advance
        bool noLayout = true, noRender = true, noAlloc = true;
        int frames = 0;
        string first = "";
        while (rig.Revealing(clip) && frames < 60)
        {
            var s = rig.Host.RunFrame();
            frames++;
            if ((s.MeasureCount != 0 || s.ArrangeCount != 0) && first.Length == 0) first = $"layout@{frames} m={s.MeasureCount} a={s.ArrangeCount}";
            if (s.ComponentsRendered != 0) noRender = false;
            if (s.HotPhaseAllocBytes != 0) noAlloc = false;
            noLayout &= s.MeasureCount == 0 && s.ArrangeCount == 0;
        }
        Check("rv.1 FlowReveal: every animation tick after the seed runs NO layout pass and renders NO component",
            noLayout && noRender && frames > 5, $"frames={frames} render={!noRender} {first}");
        Check("rv.2 FlowReveal: every animation tick allocates nothing in phases 6–13", noAlloc, $"frames={frames}");
    }

    // rv.3 — no lurch: the commit frame holds, the first advance is small, no frame jumps, it lands on layout.
    static void FirstFrameAndStepBounds(StringTable strings, HeadlessFontSystem fonts)
    {
        foreach (float dt in new[] { 8.33f, 16.67f })
        {
            var probe = Column(200f);
            using var rig = new Rig(strings, fonts, probe, dt);
            var clip = rig.ColChild(1);
            var after = rig.ColChild(2);
            float y0 = rig.Y(after);
            probe.Open[0].Value = true;
            rig.Host.RunFrame();
            float yCommit = rig.Y(after);
            var ys = Trace(rig, clip, after);
            const float travel = 200f;
            bool fast = dt < 10f;
            float firstStep = ys.Count > 0 ? ys[0] - yCommit : -1f;
            float maxStep = MaxStep(yCommit, ys);
            bool holds = Near(yCommit, y0, 0.5f);
            bool first = firstStep >= 0f && firstStep <= travel * (fast ? 0.02f : 0.05f);
            bool step = maxStep <= travel * (fast ? 0.08f : 0.15f);
            bool lands = ys.Count > 0 && Near(ys[^1], rig.LayoutY(after), 0.5f) && Near(rig.LayoutY(after), y0 + travel, 0.5f);
            Check($"rv.3 FlowReveal @{dt:0.##}ms: the commit frame shows the old geometry, the first advance moves ≤{(fast ? 2 : 5)}% of the travel, no frame steps >{(fast ? 8 : 15)}%, and it lands on layout",
                holds && first && step && lands,
                $"y0={y0:0.0} commit={yCommit:0.0} first={firstStep:0.00} maxStep={maxStep:0.00} end={(ys.Count > 0 ? ys[^1] : -1f):0.0} layout={rig.LayoutY(after):0.0}");
        }
    }

    // rv.4 — symmetric: the close is the open mirrored (same spring, same frames).
    static void Mirror(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(200f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        var after = rig.ColChild(2);
        float y0 = rig.Y(after);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        var open = Trace(rig, clip, after);
        for (int i = 0; i < 10; i++) rig.Host.RunFrame();
        probe.Open[0].Value = false;
        rig.Host.RunFrame();
        var close = Trace(rig, clip, after);
        int n = Math.Min(open.Count, close.Count);
        float worst = 0f;
        for (int i = 0; i < n; i++) worst = MathF.Max(worst, MathF.Abs((open[i] - y0) + (close[i] - y0) - 200f));
        Check("rv.4 FlowReveal: collapse mirrors expand frame for frame (open(t) + close(t) = travel, within 1 px)",
            n > 10 && worst <= 1f, $"frames={n} worst={worst:0.00}");
    }

    // rv.5 — lockstep: followers move rigidly together, the parent bottom tracks the edge, the last frame is layout.
    static void Lockstep(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(180f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        var after = rig.ColChild(2);
        var after2 = rig.ColChild(3);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        bool rigid = true, parent = true;
        int frames = 0;
        float lastY = 0f;
        while (frames < 90 && (frames < 2 || rig.Revealing(clip)))
        {
            rig.Host.RunFrame();
            frames++;
            float p = rig.Scene.PresentedAbsoluteRect(clip).H;
            rigid &= Near(rig.Y(after2) - rig.Y(after), 30f, 0.01f);
            parent &= Near(rig.Scene.PresentedAbsoluteRect(rig.Col).H, 40f + p + 60f, 0.5f);
            lastY = rig.Y(after);
        }
        rig.Host.RunFrame();
        bool rest = Near(rig.Y(after), rig.LayoutY(after), 0.01f) && float.IsNaN(rig.Scene.Paint(rig.Col).PresentedH)
            && rig.Scene.Paint(rig.Col).FlowBits == 0;
        bool noSettleJump = Near(lastY, rig.LayoutY(after), 0.6f);
        Check("rv.5 FlowReveal lockstep: followers move rigidly, the parent's presented bottom tracks the edge, and the settle frame is layout (no jump, flow columns at rest)",
            rigid && parent && rest && noSettleJump, $"frames={frames} rigid={rigid} parent={parent} rest={rest} lastY={lastY:0.00} layout={rig.LayoutY(after):0.00}");
    }

    sealed class TallRevealProbe : Component
    {
        public readonly Signal<bool> Open = new(false);
        public override Element Render() => new ScrollEl
        {
            Width = 300f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Key = "head", Height = 40f },
                    new BoxEl { Key = "clip", Direction = 1, Height = Open.Value ? float.NaN : 0f, Animate = Reveal,
                                Children = [new BoxEl { Key = "body", Height = 1300f }] },
                    new BoxEl { Key = "after", Height = 30f },
                    new BoxEl { Key = "tail", Height = 400f },
                ],
            },
        };
    }

    // rv.6 — a 1300-DIP reveal in a 300-DIP view animates only what can be seen: the moving edge stays on screen.
    static void TallRevealInViewport(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new TallRevealProbe();
        using var rig = new Rig(strings, fonts, probe, 16.67f, 300f, 300f);
        var vp = FindScrollNode(rig.Scene, rig.Scene.Root);
        rig.Scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        var clip = Child(rig.Scene, content, 1);
        var after = Child(rig.Scene, content, 2);
        float top = rig.Scene.AbsoluteRect(vp).Y, bottom = top + sc.ViewportH;
        probe.Open.Value = true;
        rig.Host.RunFrame();
        int frames = 0, onScreen = 0;
        while (frames < 90 && (frames < 2 || rig.Revealing(clip)))
        {
            rig.Host.RunFrame();
            frames++;
            float y = rig.Y(after);
            if (y >= top && y <= bottom + RevealPlan.VisibleSlackDip + 0.5f) onScreen++;   // the clamp parks the edge in the slack
        }
        float share = frames == 0 ? 0f : onScreen / (float)frames;
        Check("rv.6 FlowReveal visible-span clamp: a 1300-DIP reveal in a 300-DIP view keeps its moving edge on screen for ≥70% of its frames, and lands on layout",
            share >= 0.7f && Near(rig.Y(after), rig.LayoutY(after), 0.5f), $"frames={frames} onScreen={onScreen} share={share:0.00}");
    }

    // rv.7 — interruptible: a reverse at ~50% departs from the live value with its velocity; a toggle storm ends at rest.
    static void Reverse(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(200f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        var after = rig.ColChild(2);
        float y0 = rig.Y(after);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        float prev = rig.Y(after), maxStep = 0f;
        for (int i = 0; i < 30 && rig.Y(after) - y0 < 100f; i++) { rig.Host.RunFrame(); maxStep = MathF.Max(maxStep, MathF.Abs(rig.Y(after) - prev)); prev = rig.Y(after); }
        probe.Open[0].Value = false;   // reverse mid-flight
        for (int i = 0; i < 60; i++) { rig.Host.RunFrame(); maxStep = MathF.Max(maxStep, MathF.Abs(rig.Y(after) - prev)); prev = rig.Y(after); }
        bool reversed = Near(rig.Y(after), y0, 0.5f) && !rig.Revealing(clip) && maxStep <= 200f * 0.15f;
        for (int i = 0; i < 12; i++) { probe.Open[0].Value = !probe.Open[0].Peek(); rig.Host.RunFrame(); rig.Host.RunFrame(); }
        for (int i = 0; i < 60; i++) rig.Host.RunFrame();
        bool open = probe.Open[0].Peek();
        bool storm = !rig.Revealing(clip) && Near(rig.Y(after), open ? y0 + 200f : y0, 0.5f) && rig.Scene.Paint(rig.Col).FlowBits == 0;
        Check("rv.7 FlowReveal reverse: a mid-flight reverse is continuous (no frame steps >15%) and lands closed; a 12-toggle storm ends at rest on layout",
            reversed && storm, $"maxStep={maxStep:0.00} y={rig.Y(after):0.0} y0={y0:0.0} storm={storm}");
    }

    sealed class NestedProbe : Component
    {
        public readonly Signal<bool> Outer = new(false), Inner = new(false);
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl
                {
                    Key = "col", Direction = 1, Width = 300f,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = "outer", Direction = 1, Height = Outer.Value ? float.NaN : 0f, Animate = Reveal,
                            Children =
                            [
                                new BoxEl { Key = "inner", Direction = 1, Height = Inner.Value ? float.NaN : 0f, Animate = Reveal,
                                            Children = [new BoxEl { Height = 120f }] },
                                new BoxEl { Key = "innerTail", Height = 40f },
                            ],
                        },
                        new BoxEl { Key = "after", Height = 30f },
                    ],
                },
            ],
        };
    }

    // rv.8 — any number at once; nested reveals compose.
    static void ConcurrentAndNested(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(100f, 150f);
        using (var rig = new Rig(strings, fonts, probe, 16.67f))
        {
            var c0 = rig.ColChild(1);
            var c1 = rig.ColChild(2);
            var after = rig.ColChild(3);
            float y0 = rig.Y(after);
            probe.Open[0].Value = true;
            probe.Open[1].Value = true;
            rig.Host.RunFrame();
            bool sums = true;
            for (int i = 0; i < 60; i++)
            {
                rig.Host.RunFrame();
                float p0 = rig.Scene.PresentedAbsoluteRect(c0).H, p1 = rig.Scene.PresentedAbsoluteRect(c1).H;
                sums &= Near(rig.Y(after) - y0, p0 + p1, 0.6f);
            }
            Check("rv.8a FlowReveal concurrency: two reveals in one column run together and the follower rides their SUM every frame",
                sums && Near(rig.Y(after), y0 + 250f, 0.5f) && !rig.Revealing(c0) && !rig.Revealing(c1), $"sums={sums} y={rig.Y(after) - y0:0.0}");
        }
        var nested = new NestedProbe();
        using (var rig = new Rig(strings, fonts, nested, 16.67f))
        {
            var outer = Child(rig.Scene, rig.Col, 0);
            var after = Child(rig.Scene, rig.Col, 1);
            float y0 = rig.Y(after), prev = y0;
            nested.Outer.Value = true;
            nested.Inner.Value = true;
            rig.Host.RunFrame();
            bool monotone = Near(rig.Y(after), y0, 0.5f);   // the seed frame: −160 against +160 (a sum would be −280)
            prev = rig.Y(after);
            for (int i = 0; i < 70; i++) { rig.Host.RunFrame(); monotone &= rig.Y(after) >= prev - 0.01f; prev = rig.Y(after); }
            var inner = Child(rig.Scene, outer, 0);
            Check("rv.8b FlowReveal nesting: an inner reveal inside an outer one COMBINES (min, never a sum): the seed frame holds the follower, it moves monotonically, and both land on layout",
                monotone && Near(rig.Y(after), y0 + 160f, 0.5f) && !rig.Revealing(outer) && !rig.Revealing(inner),
                $"monotone={monotone} y={rig.Y(after) - y0:0.0}");
        }
    }

    // rv.8c — nesting, the CLOSE legs. A collapse lays out at its FINAL height, so the nesting bound must come from what
    // the content presents (and apply only while an inner reveal runs), never from the laid-out height (§4.1 worked check).
    // (1) The outer closes over an inner one that stays open; (2) both close in one commit. Each commit frame holds the
    // follower where it was, each close is monotone and mirrors the both-open trace frame for frame, and both land on layout.
    static void NestedClose(StringTable strings, HeadlessFontSystem fonts)
    {
        var nested = new NestedProbe();
        using var rig = new Rig(strings, fonts, nested, 16.67f);
        var outer = Child(rig.Scene, rig.Col, 0);
        var after = Child(rig.Scene, rig.Col, 1);
        var inner = Child(rig.Scene, outer, 0);
        float y0 = rig.Y(after);
        nested.Outer.Value = true;
        nested.Inner.Value = true;
        rig.Host.RunFrame();
        var open = Trace(rig, outer, after);   // the reference: both open together (the follower rides y0 + P_outer)
        for (int i = 0; i < 10; i++) rig.Host.RunFrame();

        // Mirror + monotone + commit-hold check of one close leg against the open trace.
        bool CloseLeg(float yCommit, List<float> close, out float worst)
        {
            int n = Math.Min(open.Count, close.Count);
            worst = 0f;
            bool monotone = close.Count > 0 && close[0] <= yCommit + 0.01f;
            for (int i = 0; i < n; i++) worst = MathF.Max(worst, MathF.Abs((open[i] - y0) + (close[i] - y0) - 160f));
            for (int i = 1; i < close.Count; i++) monotone &= close[i] <= close[i - 1] + 0.01f;
            return Near(yCommit, y0 + 160f, 0.5f) && n > 10 && worst <= 1f && monotone;
        }

        // (1) the outer closes; the inner stays open (no inner row, so no bound).
        nested.Outer.Value = false;
        rig.Host.RunFrame();
        float yCommit1 = rig.Y(after);
        var close1 = Trace(rig, outer, after);
        for (int i = 0; i < 10; i++) rig.Host.RunFrame();
        bool mirrored1 = CloseLeg(yCommit1, close1, out float worst1);
        bool leg1 = mirrored1 && Near(rig.Y(after), y0, 0.5f) && !rig.Revealing(outer);
        Check("rv.8c.1 FlowReveal nesting, close over an open inner reveal: the commit frame holds, the close mirrors the open (within 1 px, monotone) and lands on layout",
            leg1, $"commit={yCommit1 - y0:0.0} frames={close1.Count} worst={worst1:0.00} y={rig.Y(after) - y0:0.0}");

        // (2) reopen the outer (the inner is still open), let it rest, then close BOTH in one commit (both rows live: the
        // bound applies, ContentExtent 40 + the inner's 120·(1−x) never falls below the outer's 160·(1−x)).
        nested.Outer.Value = true;
        for (int i = 0; i < 60; i++) rig.Host.RunFrame();
        bool reopened = Near(rig.Y(after), y0 + 160f, 0.5f);
        nested.Outer.Value = false;
        nested.Inner.Value = false;
        rig.Host.RunFrame();
        float yCommit2 = rig.Y(after);
        var close2 = Trace(rig, outer, after);
        for (int i = 0; i < 10; i++) rig.Host.RunFrame();
        bool mirrored2 = CloseLeg(yCommit2, close2, out float worst2);   // first: worst2 is read by the message below
        bool leg2 = reopened && mirrored2 && Near(rig.Y(after), y0, 0.5f)
            && !rig.Revealing(outer) && !rig.Revealing(inner) && rig.Scene.Paint(rig.Col).FlowBits == 0;
        Check("rv.8c.2 FlowReveal nesting, outer and inner closing together: the commit frame holds, the close mirrors the open (within 1 px, monotone) and both land on layout",
            leg2, $"reopened={reopened} commit={yCommit2 - y0:0.0} frames={close2.Count} worst={worst2:0.00} y={rig.Y(after) - y0:0.0}");
    }

    // rv.9 — input follows paint: mid-flight the follower is hit where it is DRAWN, not where layout put it.
    static void HitTest(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(200f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var after = rig.ColChild(2);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        for (int i = 0; i < 5; i++) rig.Host.RunFrame();
        var pr = rig.Scene.PresentedAbsoluteRect(after);
        var lr = rig.Scene.AbsoluteRect(after);
        var atPresented = rig.Host.Input.HitTest(new Point2(pr.X + pr.W * 0.5f, pr.Y + pr.H * 0.5f));
        var atLayout = rig.Host.Input.HitTest(new Point2(lr.X + lr.W * 0.5f, lr.Y + lr.H * 0.5f));
        Check("rv.9 FlowReveal hit-testing mirrors the presented flow: the follower is hit at its drawn rect and not at its laid-out one mid-flight",
            pr.Y < lr.Y - 20f && atPresented == after && atLayout != after, $"presentedY={pr.Y:0.0} layoutY={lr.Y:0.0} hitP={atPresented == after} hitL={atLayout == after}");
    }

    sealed class ScrollRevealProbe : Component
    {
        public readonly Signal<bool> Open = new(true), OpenTop = new(true);
        public override Element Render() => new ScrollEl
        {
            Width = 300f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Key = "topclip", Direction = 1, Height = OpenTop.Value ? float.NaN : 0f, Animate = Reveal, Children = [new BoxEl { Height = 200f }] },
                    new BoxEl { Key = "filler", Height = 1200f },
                    new BoxEl { Key = "clip", Direction = 1, Height = Open.Value ? float.NaN : 0f, Animate = Reveal, Children = [new BoxEl { Height = 200f }] },
                    new BoxEl { Key = "tail", Height = 50f },
                ],
            },
        };
    }

    // rv.10 — scrolling. (a) A collapse at the end rides the max-offset edge down: the commit frame HOLDS the offset (no
    // clamp against the laid-out extent), then it eases monotonically to the new max. (b) A reveal wholly above the
    // viewport snaps AND anchors: the scroll frame shifts by the change, so the content being read stays exactly where it
    // was, in both directions.
    static void ScrollClampAndAnchor(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new ScrollRevealProbe();
        using var rig = new Rig(strings, fonts, probe, 16.67f, 300f, 300f);
        var vp = FindScrollNode(rig.Scene, rig.Scene.Root);
        var handle = rig.Host.TryGetScrollHandle(vp)!;
        handle.ScrollTo(handle.MaxOffset, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var sc0);
        double start = sc0.Offset;
        probe.Open.Value = false;
        rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var scCommit);
        bool commitHolds = Math.Abs(scCommit.Offset - start) <= 0.5;
        double prev = scCommit.Offset, maxStep = 0;
        bool monotone = true;
        for (int i = 0; i < 70; i++)
        {
            rig.Host.RunFrame();
            rig.Scene.TryGetScroll(vp, out var sc);
            monotone &= sc.Offset <= prev + 0.01;
            maxStep = Math.Max(maxStep, Math.Abs(sc.Offset - prev));
            prev = sc.Offset;
        }
        bool edge = Near((float)start, 1350f, 1f) && commitHolds && Near((float)prev, 1150f, 1f) && monotone && maxStep <= 200 * 0.15;

        // (b) Content is now 200 + 1200 + 0 + 50; at offset 900 the filler covers the whole view.
        handle.ScrollTo(900, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var sc2);
        var topclip = Child(rig.Scene, sc2.ContentNode, 0);
        var filler = Child(rig.Scene, sc2.ContentNode, 1);
        float fy0 = rig.Y(filler);
        probe.OpenTop.Value = false;   // 200 DIP collapse wholly above the view
        rig.Host.RunFrame();
        bool snappedClose = !rig.Revealing(topclip);
        float fyClose = rig.Y(filler);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var sc3);
        bool closeAnchored = snappedClose && Near(fyClose, fy0, 0.5f) && Near(rig.Y(filler), fy0, 0.5f) && Near((float)sc3.Offset, 700f, 0.5f);
        probe.OpenTop.Value = true;    // and back open: the frame shifts the other way
        rig.Host.RunFrame();
        bool snappedOpen = !rig.Revealing(topclip);
        float fyOpen = rig.Y(filler);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var sc4);
        bool openAnchored = snappedOpen && Near(fyOpen, fy0, 0.5f) && Near(rig.Y(filler), fy0, 0.5f) && Near((float)sc4.Offset, 900f, 0.5f);
        Check("rv.10a FlowReveal scrolling: a collapse at the end holds the offset in its commit frame, then eases it down the presented max (monotone, no step >15%) to the new max",
            edge, $"offset {start:0.0}→commit {scCommit.Offset:0.0}→{prev:0.0} maxStep={maxStep:0.00} monotone={monotone}");
        Check("rv.10b FlowReveal scroll anchoring: a reveal wholly above the viewport snaps and shifts the offset by its change, so the content in view does not move (close and reopen)",
            closeAnchored && openAnchored,
            $"fillerY {fy0:0.0}→close {fyClose:0.0}/{sc3.Offset:0.0}→open {fyOpen:0.0}/{sc4.Offset:0.0} snapped={snappedClose}/{snappedOpen}");
    }

    // rv.11 — a recycle (a rebind flush) is the SAME row: no reveal on the remount, no orphan on the unmount.
    static void RecycleSuppression(StringTable strings)
    {
        var scene = new SceneStore();
        var rec = new TreeReconciler(scene, strings);
        var anim = new AnimEngine(scene);
        rec.Anim = anim;
        static Element Tree(bool drawer) => new BoxEl
        {
            Direction = 1,
            Children = drawer
                ? [new BoxEl { Key = "row", Height = 40f }, new BoxEl { Key = "drawer", Height = 80f, Animate = RevealEnterExit }]
                : [new BoxEl { Key = "row", Height = 40f }],
        };
        var t0 = Tree(false);
        rec.ReconcileRoot(t0, null);
        var t1 = Tree(true);
        using (rec.PushSuppressBoundTransitions()) rec.ReconcileRoot(t1, t0);
        bool enterSuppressed = anim.PendingEnterReveal.Count == 0;
        var t2 = Tree(false);
        using (rec.PushSuppressBoundTransitions()) rec.ReconcileRoot(t2, t1);
        bool exitSuppressed = scene.OrphanCount == 0;
        var t3 = Tree(true);
        rec.ReconcileRoot(t3, t2);
        bool enterSeeded = anim.PendingEnterReveal.Count == 1;
        anim.PendingEnterReveal.Clear();
        rec.ReconcileRoot(Tree(false), t3);
        bool exitOrphaned = scene.OrphanCount == 1 && anim.RevealExitCarriers.Count == 1;
        Check("rv.11 FlowReveal recycle: a mount/unmount under the recycle scope seeds no reveal and orphans nothing; the same edits outside it reveal",
            enterSuppressed && exitSuppressed && enterSeeded && exitOrphaned,
            $"enterSuppressed={enterSuppressed} exitSuppressed={exitSuppressed} enterSeeded={enterSeeded} exitOrphaned={exitOrphaned}");
    }

    // rv.12 — async content growth mid-flight retargets the live spring (no second animation, no jump).
    static void AsyncGrowth(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(120f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        var after = rig.ColChild(2);
        float y0 = rig.Y(after), prev = y0, maxStep = 0f;
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        bool monotone = true;
        for (int i = 0; i < 70; i++)
        {
            if (i == 4) probe.BodyH[0].Value = 240f;   // the content grows while the reveal runs
            rig.Host.RunFrame();
            float y = rig.Y(after);
            monotone &= y >= prev - 0.01f;
            maxStep = MathF.Max(maxStep, y - prev);
            prev = y;
        }
        Check("rv.12 FlowReveal async growth: a mid-flight content growth retargets the live row (monotone, no step >15%) and lands on the new layout",
            monotone && maxStep <= 240f * 0.15f && Near(rig.Y(after), y0 + 240f, 0.5f) && !rig.Revealing(clip),
            $"monotone={monotone} maxStep={maxStep:0.00} y={rig.Y(after) - y0:0.0}");
    }

    // rv.13 — reduced motion snaps the size (the token's SnapEnd policy, read as a value at the seed).
    static void ReducedMotionSnaps(StringTable strings, HeadlessFontSystem fonts)
    {
        bool prevReduced = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = true;
            var probe = Column(200f);
            using var rig = new Rig(strings, fonts, probe, 16.67f);
            var clip = rig.ColChild(1);
            var after = rig.ColChild(2);
            probe.Open[0].Value = true;
            rig.Host.RunFrame();
            Check("rv.13 FlowReveal reduced motion: the toggle lands on layout in its commit frame with no reveal row",
                !rig.Revealing(clip) && Near(rig.Y(after), rig.LayoutY(after), 0.01f), $"y={rig.Y(after):0.0} layout={rig.LayoutY(after):0.0}");
        }
        finally { Motion.ReducedMotion = prevReduced; }
    }

    static NodeHandle FindText(SceneStore s, StringTable strings, NodeHandle n, string text)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).VisualKind == VisualKind.Text && strings.Resolve(s.Paint(n).Text) == text) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindText(s, strings, c, text);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    sealed class ExpanderProbe : Component
    {
        public readonly Signal<bool> Open = new(true);
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                Embed.Comp(() => new Expander { Header = "Section", IsExpanded = Open, Content = new TextEl("rv-expander-body") { Size = 14f } }),
                new BoxEl { Key = "after", Height = 30f },
            ],
        };
    }

    // rv.14 — the settle ALWAYS fires. A close while projections are suppressed (as during a user scroll) starts no
    // reveal row, yet the panel unmounts at once and the toggle window ends: no poller, nothing stuck mounted. A reopen
    // after the suppression lifts reveals normally.
    static void SuppressedCloseSettles(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new ExpanderProbe();
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        NodeHandle Body() => FindText(rig.Scene, strings, rig.Scene.Root, "rv-expander-body");
        var body = Body();
        var clip = body.IsNull ? NodeHandle.Null : rig.Scene.Parent(rig.Scene.Parent(body));   // text → panel → clip wrapper
        bool mountedOpen = !clip.IsNull;
        bool noRow = false, unmounted = false;
        int frames = 0, pollers = 0;
        try
        {
            Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, true);
            probe.Open.Value = false;
            rig.Host.RunFrame();
            noRow = mountedOpen && !rig.Revealing(clip);
            while (frames < 4 && !Body().IsNull) { rig.Host.RunFrame(); frames++; pollers = Math.Max(pollers, rig.Host.FrameClockPollerCount); }
            unmounted = Body().IsNull;
        }
        finally { Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, false); }
        probe.Open.Value = true;
        bool reveals = false;
        for (int i = 0; i < 4 && !reveals; i++) { rig.Host.RunFrame(); reveals = mountedOpen && rig.Revealing(clip); }
        Check("rv.14 FlowReveal settle always fires: an Expander closed under suppressed projections starts no row, still unmounts its panel within 2 frames with no poller, and reopens with a reveal",
            mountedOpen && noRow && unmounted && frames <= 2 && pollers == 0 && reveals,
            $"mounted={mountedOpen} noRow={noRow} unmounted={unmounted} frames={frames} pollers={pollers} reveals={reveals}");
    }

    // rv.15 — a settle callback whose node dies (a drawer recycled away, an orphan reclaimed by a reopen) runs exactly
    // once and is forgotten: nothing waits forever, nothing leaks.
    static void SettleOnFree(StringTable strings)
    {
        var scene = new SceneStore();
        var rec = new TreeReconciler(scene, strings);
        var anim = new AnimEngine(scene);
        rec.Anim = anim;
        scene.OnFreeIndex = anim.ClearForIndex;   // AppHost wires the same fan-out (OnSceneSlotFreed)
        var t0 = new BoxEl { Direction = 1, Children = [new BoxEl { Key = "drawer", Height = 80f }] };
        rec.ReconcileRoot(t0, null);
        var drawer = Child(scene, scene.Root, 0);
        int runs = 0;
        anim.WhenSettled(drawer, AnimChannel.RevealExtent, () => runs++);
        bool registered = anim.SettleCallbackCount == 1;
        var t1 = new BoxEl { Direction = 1, Children = [] };
        rec.ReconcileRoot(t1, t0);   // no transition: the drawer is freed outright
        bool queued = anim.HasSettledCallbacks;
        anim.DrainSettledCallbacks();
        anim.DrainSettledCallbacks();
        Check("rv.15 FlowReveal settle on free: a callback registered on a node that is freed runs once and is dropped",
            registered && queued && runs == 1 && !anim.HasSettledCallbacks && anim.SettleCallbackCount == 0,
            $"registered={registered} queued={queued} runs={runs} left={anim.SettleCallbackCount}");
    }

    sealed class GrowRevealProbe : Component
    {
        public readonly Signal<bool> Open = new(false);
        public override Element Render() => new ScrollEl
        {
            Width = 300f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Key = "page", Direction = 1, Grow = 1f,
                        Children =
                        [
                            new BoxEl { Key = "head", Height = 40f },
                            new BoxEl { Key = "clip", Direction = 1, Height = Open.Value ? float.NaN : 0f, Animate = Reveal,
                                        Children = [new BoxEl { Height = 200f }] },
                            new BoxEl { Key = "tail", Height = 500f },
                        ],
                    },
                ],
            },
        };
    }

    // rv.16 — a Grow child of a scroll content follows its content (the content's main axis is unbounded), so it is NOT
    // a flow boundary: a reveal inside it reaches the scroll content, and the presented extent tracks it mid-flight.
    static void GrowChildOfScrollContent(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new GrowRevealProbe();
        using var rig = new Rig(strings, fonts, probe, 16.67f, 300f, 300f);
        var vp = FindScrollNode(rig.Scene, rig.Scene.Root);
        rig.Scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        var clip = Child(rig.Scene, Child(rig.Scene, content, 0), 1);
        probe.Open.Value = true;
        rig.Host.RunFrame();
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        float revealed = rig.Scene.PresentedAbsoluteRect(clip).H - rig.Scene.Bounds(clip).H;   // < 0 mid-flight
        float contentDelta = rig.Scene.Paint(content).FlowDelta;
        Check("rv.16 FlowReveal inside a Grow child of a scroll content reaches the content: its presented extent tracks the reveal mid-flight",
            revealed < -1f && Near(contentDelta, revealed, 0.5f), $"revealed={revealed:0.00} contentDelta={contentDelta:0.00}");
    }
}
```

Notes for the coder: `Motion` / `MotionSuppressionSource` are `FluentGpu.Dsl`; `FindScrollNode`, `Child`, `Near` come
from `Harness.Asserts`; `Tok` is `FluentGpu.Dsl.Tok`; `Expander` is `FluentGpu.Controls` (add `using FluentGpu.Controls;`);
`VisualKind` is `FluentGpu.Scene`. If an import is missing, add the `using` the sibling suites use
(`ZoneListSpikeChecks.cs` has the full list) — do not change any gate logic.

### 9.2 Expander gates — `src/FluentGpu.VerticalSlice/Suites/ControlsSuite.cs`

FlowReveal lands layout ONCE, so every Expander assertion that sampled `AbsoluteRect` mid-flight now samples
`PresentedAbsoluteRect`, and the Trailing-anchor / reflow-restore assertions become Parallax / no-watcher ones.

1. `D3ExpanderChecks`, cp3.a. Replace every `host.Scene.AbsoluteRect(sibling).Y` with `host.Scene.PresentedAbsoluteRect(sibling).Y`,
   every `host.Scene.AbsoluteRect(clip).H` with `host.Scene.PresentedAbsoluteRect(clip).H` and
   `host.Scene.AbsoluteRect(card).H` with `host.Scene.PresentedAbsoluteRect(card).H` in cp3.a/cp3.b (NOT in cp3.c/cp3.e).
   Replace `bool anchoredOpen = Near(shiftMid, clipHMid - contentExtent, 1.5f) && shiftMid < -4f;` with
   `bool anchoredOpen = shiftMid < -0.5f && shiftMid >= -RevealPlan.ParallaxMaxDip - 0.01f;   // Parallax: the panel trails the edge, ≤ 24 DIP`
   and update its comment + the check label to "the panel trails the reveal edge (Parallax)". `liRestoredOpen` stays.
   `noClickJump` keeps its meaning (the commit frame presents the old size: `clipHClick < 2f` holds since presented H = 0).
2. cp3.b: `anchoredClosing` → `bool anchoredClosing = shiftClosing < -0.5f && shiftClosing >= -RevealPlan.ParallaxMaxDip - 0.01f;`
   and `bool layoutCollapsed = siblingYClosing < siblingYCloseClick - 4f && siblingYClosing > siblingYCollapsed + 4f && clipHClosing > 4f && clipHClosing < clipHOpen - 4f;`
   is kept (it now reads presented values). The label: "…anchored to the reveal edge (Parallax), unmounts at settle".
3. `D3ExpanderAnimateContentResizeChecks`: replace every `host.Scene.AbsoluteRect(clip).H` with
   `host.Scene.PresentedAbsoluteRect(clip).H`. Replace the cp3.acr0b check with:

```csharp
            Check("cp3.acr0b — Expander: no frame-clock poller ever — neither a collapsed mount nor the open toggle (the engine settle callback replaced the watchers)",
                collapsedPollers == 0 && openingPollers == 0, $"collapsed={collapsedPollers} opening={openingPollers}");
```

   and in the doc comment of the method replace "(ExpanderResizeWatcher keeps `transitioning` up the whole time)" with
   "(the toggle window lasts until the engine reports the reveal at rest)". cp3.acr1–3 keep their numbers.
4. `ExpanderSettingsChecks` (W1-P3.a): replace "the clip wrapper's SizeMode.Reflow Trailing anchor keeps the panel's
   bottom edge on the reveal edge (ChildShiftY < 0 mid-flight, 0 at rest)" with "the clip wrapper's FlowReveal Parallax
   anchor trails the panel behind the reveal edge (ChildShiftY < 0 mid-flight, 0 at rest)". Logic unchanged.
5. `D3ExpanderWrapReflowChecks`: unchanged (it samples settled geometry).

### 9.3 Engine.Tests — new `src/FluentGpu.Engine.Tests/RevealPlanTests.cs` and `FlowCursorTests.cs`

```csharp
using System;
using FluentGpu.Animation;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The pure half of SizeMode.FlowReveal (docs/plans/smooth-reveal-implementation.md §1/§3): the visible-span
/// clamp, the Parallax lead, and the one reveal spring's shape (first frame, step bound, no overshoot, settle time).</summary>
public sealed class RevealPlanTests
{
    [Fact]
    public void Clamp_AFullyVisibleMoveIsUntouched()
    {
        Assert.True(RevealPlan.TryClamp(0f, 200f, 100f, 0f, 800f, out float from, out float to));
        Assert.Equal(0f, from);
        Assert.Equal(200f, to);
    }

    [Fact]
    public void Clamp_ATallExpandAnimatesOnlyTheVisibleSpan()
    {
        Assert.True(RevealPlan.TryClamp(0f, 1300f, 40f, 0f, 300f, out float from, out float to));
        Assert.Equal(0f, from);
        Assert.Equal(268f, to, 3);   // 300 + 8 slack − 40
    }

    [Fact]
    public void Clamp_ATallCollapseJumpsTheHiddenPartFirst()
    {
        Assert.True(RevealPlan.TryClamp(1300f, 0f, 40f, 0f, 300f, out float from, out float to));
        Assert.Equal(268f, from, 3);
        Assert.Equal(0f, to);
    }

    [Fact]
    public void Clamp_GrowthKeepsItsDeparture()
    {
        Assert.True(RevealPlan.TryClamp(120f, 240f, 0f, 0f, 800f, out float from, out float to));
        Assert.Equal(120f, from);
        Assert.Equal(240f, to);
    }

    [Fact]
    public void AboveView_OnlyWhenTheRegionStaysAboveTheTopThroughTheWholeMove()
    {
        Assert.True(RevealPlan.IsAboveView(200f, 0f, -900f, 0f));     // a collapse scrolled out of view: snap + anchor
        Assert.True(RevealPlan.IsAboveView(0f, 200f, -200f, 0f));     // its bottom lands exactly on the top edge
        Assert.False(RevealPlan.IsAboveView(0f, 200f, -150f, 0f));    // 50 DIP of it reaches into the view: animate
        Assert.False(RevealPlan.IsAboveView(0f, 200f, 400f, 0f));     // below the top
    }

    [Theory]
    [InlineData(-500f)]   // wholly above the view: animating it would slide what the user reads
    [InlineData(400f)]    // wholly below it: nothing to see
    public void Clamp_AnInvisibleRegionSnaps(float regionTop)
    {
        Assert.False(RevealPlan.TryClamp(0f, 200f, regionTop, 0f, 300f, out float from, out float to));
        Assert.Equal(200f, from);
        Assert.Equal(200f, to);
    }

    [Fact]
    public void ParallaxShift_LeadsByADampedShareCappedAt24()
    {
        Assert.Equal(0f, RevealPlan.ParallaxShift(100f, 100f));
        Assert.Equal(-3.5f, RevealPlan.ParallaxShift(90f, 100f), 3);
        Assert.Equal(-RevealPlan.ParallaxMaxDip, RevealPlan.ParallaxShift(0f, 400f));
    }

    [Theory]
    [InlineData(8.33f, 0.02f, 0.08f)]
    [InlineData(16.67f, 0.05f, 0.15f)]
    public void Spring_FirstFrameAndPerFrameStepAreBounded(float dtMs, float firstMax, float stepMax)
    {
        float first = RevealPlan.Progress(dtMs);
        float prev = 0f, maxStep = 0f;
        for (float t = dtMs; t < 700f; t += dtMs)
        {
            float p = RevealPlan.Progress(t);
            maxStep = MathF.Max(maxStep, p - prev);
            prev = p;
        }
        Assert.InRange(first, 0f, firstMax);
        Assert.InRange(maxStep, 0f, stepMax);
    }

    [Fact]
    public void Spring_IsCriticallyDampedAndSettlesOnTime()
    {
        float peak = 0f;
        for (float t = 0f; t < 900f; t += 1f) peak = MathF.Max(peak, RevealPlan.Progress(t));
        Assert.True(peak <= 1.0005f, $"overshoot peak={peak}");
        Assert.InRange(RevealPlan.Progress(240f), 0.95f, 1f);
        Assert.InRange(RevealPlan.Progress(340f), 0.99f, 1f);
    }
}
```

```csharp
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The presented-flow walk the recorder, hit-testing and PresentedAbsoluteRect share (FlowCursor).</summary>
public sealed class FlowCursorTests
{
    [Fact]
    public void AtRestTheCursorIsInactive()
    {
        var p = default(NodePaint);
        Assert.False(FlowCursor.For(in p).Active);
    }

    [Fact]
    public void AColumnShiftsEachChildByItsEarlierSiblingsDeltas()
    {
        var p = new NodePaint { FlowBits = NodePaint.FlowShiftsBit };
        var c = FlowCursor.For(in p);
        Assert.Equal(0f, c.Step(0, 0f, -50f, out _, out _));
        Assert.Equal(-50f, c.Step(1, 100f, 0f, out _, out _));
        Assert.Equal(-50f, c.Step(2, 130f, -10f, out _, out _));
        Assert.Equal(-60f, c.Step(3, 160f, 0f, out float clipTop, out _));
        Assert.True(float.IsNaN(clipTop));
    }

    [Fact]
    public void AnExitOrphanPushesTheChildrenLaidOutAtItsTop()
    {
        var p = new NodePaint { FlowBits = NodePaint.FlowShiftsBit | NodePaint.FlowOrphanBit, FlowOrphanTop = 40f, FlowOrphanDelta = 70f };
        var c = FlowCursor.For(in p);
        Assert.Equal(0f, c.Step(0, 0f, 0f, out _, out _));    // the row above the closing drawer
        Assert.Equal(70f, c.Step(1, 40f, 0f, out _, out _));  // laid out where the drawer was: still pushed down
        Assert.Equal(70f, c.Step(2, 70f, 0f, out _, out _));
    }
}
```

### 9.4 Canon docs (Phase 1)

- `docs/design/subsystems/backdrop-effects-animation.md`: after §5.8 add `### 5.8b SizeMode.FlowReveal — layout once,
  motion at paint time (AS-BUILT 2026-10)` with: the mode's contract (§0 item 1–2 of this plan), the row
  (`AnimChannel.RevealExtent` = presented extent), the pass (phase 7.05 `PropagateFlowReveals`, boundaries list from
  `IsFlowBoundary`), `FlowCursor` (recorder + hit-test + `PresentedAbsoluteRect`), the visible-span clamp, the anchors,
  reduced motion, recycle suppression, `WhenSettled` (always fires: snaps, suppressed toggles, node death), host step
  6.3 + `FlexLayout.DeferOffsetClamp` (the scroll clamp against the presented extent), the nesting rule
  (a reveal presents its own spring, bounded by `ContentExtent + inner deltas` only while an inner reveal runs, never by
  the laid-out height, which a close has already shrunk), scroll anchoring for a reveal above the view; "Gates: `rv.1`–`rv.16` (RevealSuite),
  `RevealPlanTests`, `FlowCursorTests`."
- `docs/design/subsystems/scene-memory.md` §2.7: add a fenced block listing the new `NodePaint` fields
  (`FlowDelta`, `FlowOrphanTop`, `FlowOrphanDelta`, `FlowBits` + the seven bit constants, `FlowInnerBit` included) and `ScrollState.RevealOverscan`,
  each one line, "SEMANTICS: backdrop-effects-animation.md §5.8b".
- `docs/design/SPEC-INDEX.md` §2: add a row `| **Flow reveal (SizeMode.FlowReveal / NodePaint.FlowDelta / FlowCursor)** *(AS-BUILT 2026-10)* | subsystems/backdrop-effects-animation.md §5.8b (the NodePaint/ScrollState columns stay owned by subsystems/scene-memory.md §2.7) | **Canonical value:** layout lands once; one RevealExtent row springs the presented height under MotionTok.Reveal (FromResponse(0.31, 1)); PropagateFlowReveals (phase 7.05) folds it into FlowDelta up to the nearest boundary; FlowCursor shifts later siblings in record AND hit-test; nested reveals combine (own spring, bounded by ContentExtent + inner deltas only while an inner reveal runs — never by the laid-out height, so closes mirror opens); host 6.3 seeds right after layout and the scroll plan clamps against the presented ContentH (layout defers its clamp); a reveal wholly above the view snaps with a scroll-anchor shift; reduced motion snaps; recycles never seed; WhenSettled always fires. Gates: rv.*. |`
- `docs/design/subsystems/README.md` ownership map: a row `| **Flow reveal** (SizeMode.FlowReveal, AnimChannel.RevealExtent, FlowCursor, AnimEngine.WhenSettled) | backdrop-effects-animation.md §5.8b / scene-memory.md (columns) — registered in SPEC-INDEX.md §2 |`.
- `.claude/skills/fluentgpu/SKILL.md`: in the "Where to change what" table add a row
  `| Disclosure / expand-collapse motion (SizeMode.FlowReveal, reveal bands, MotionTok.Reveal) | Animation/AnimScheduler.FlowReveal.cs + Scene/FlowCursor.cs; plan: docs/plans/smooth-reveal-implementation.md — use `SizeMode.FlowReveal`, never Reflow, for anything that opens/closes |`.
  The orchestrator runs `docs/design/check-canon.ps1` after this WP.

---

## 10. Phase 2 — virtual reveal bands (engine)

The sidebar's band is a range of virtual rows (separate item roots), so it cannot be one FlowReveal node. It becomes up
to four concurrent **reveal bands** per vertical viewport, each a spring on its presented height, folded into the same
flow pass and walked by the same `FlowCursor`. The model stays EXPANDED while a band closes; at rest the owner's commit
removes the rows. The zero-delta handoff is made in the ENGINE, before the commit runs: `ItemsViewController.BandSettled`
(frame start, `DrainSettledCallbacks`) marks the band COMMITTED (`AnimEngine.CommitRevealBand`, recording the item count),
then invokes the commit. A committed band keeps presenting its rows at 0 while they are still modelled. From the flush
that drops them (the viewport's `ItemCount` moved off `RevealBand.CommitCount`) it contributes NO delta and NO clip
anywhere: `AddBands`, `FlowCursor` (recorder, hit-test, `PresentedAbsoluteRect`). So the commit frame's 6.3 flow pass and
`SyncScrollPlansMidFrame` already see exactly the laid-out extent. ItemsView's layout effect (6.5) then only releases the
slot. (Clearing the band only at 6.5 is too late: 6.3 runs first with the stale band still registered. For the last
section `First + Count > ItemCount`, so its geometry is not refreshed and the content's delta is `0 - Extent`. Otherwise
it re-maps onto the next `Count` rows and subtracts THEIR extent. Either way `SetExtent(PresentedContentMain)` re-holds a
list scrolled to its end at a max one band too small: a permanent offset jump in the commit frame. rv.band.8 pins it.)

### 10.1 Data — `src/FluentGpu.Engine/Scene/Columns.cs`

Delete the five `Disclosure*` fields and their comment from `ScrollState`, and `DisclosureFirst = -1, DisclosureT = float.NaN,`
from `ScrollState.Default`. In their place:

```csharp
    // Virtual reveal bands (docs/plans/smooth-reveal-implementation.md §10): up to RevealBands.Capacity contiguous logical
    // row ranges whose PRESENTED height springs while the model stays laid out at full size. BandMask bit i = slot i live.
    // Plain named fields (no array, no InlineArray), so the render snapshot copies them with the row and the default
    // struct equality the parity check uses still works.
    public RevealBands Bands;
    public byte BandMask;
```

After the `ScrollState` struct add:

```csharp
/// <summary>One live virtual reveal band (see <see cref="ScrollState.Bands"/>): the logical rows [First, First+Count), their
/// content-local laid-out Top/Extent (refreshed every flow pass from the layout), and the PRESENTED height (0 … Extent,
/// written by the band's AnimChannel.RevealBand row). Opening = the band is revealing toward Extent (cleared at rest);
/// a closing band rests at 0 until its owner's commit removes the rows. Committed = that commit is running
/// (ItemsViewController.BandSettled marks it right before invoking it) and CommitCount = the viewport's ItemCount then:
/// once the count moves off it the rows are gone, and the band presents nothing at all (<see cref="Presents"/>).</summary>
public struct RevealBand : IEquatable<RevealBand>
{
    public int First, Count;
    public float Top, Extent;
    public float Presented;
    public bool Opening;
    public bool Committed;
    public int CommitCount;

    /// <summary>False for a committed collapse whose rows already left the model (<paramref name="itemCount"/> moved off
    /// <see cref="CommitCount"/>): it contributes no delta and no clip from that flush on, so the commit frame's flow pass
    /// and scroll sync see exactly the laid-out extent (the zero-delta handoff). True otherwise.</summary>
    public readonly bool Presents(int itemCount) => !Committed || itemCount == CommitCount;

    // float.Equals, not ==: a NaN Presented equals itself (the default struct equality's semantics, which parity relies on).
    public readonly bool Equals(RevealBand o)
        => First == o.First && Count == o.Count && Top.Equals(o.Top) && Extent.Equals(o.Extent)
           && Presented.Equals(o.Presented) && Opening == o.Opening && Committed == o.Committed && CommitCount == o.CommitCount;
    public override readonly bool Equals(object? obj) => obj is RevealBand o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(First, Count, Top, Extent, Presented, Opening, Committed, CommitCount);
}

/// <summary>The fixed per-viewport band slots. FOUR NAMED FIELDS, deliberately not an <c>[InlineArray]</c>: ScrollState is
/// compared with the default struct equality (SceneRecordingSnapshot.Parity <c>ScrollEqual</c>, every DEBUG/diag no-op
/// publication check and the snapshot tests), and the runtime throws NotSupportedException from Equals/GetHashCode on any
/// struct that holds an InlineArray field. Read with <see cref="Get"/>, write with <see cref="Set"/>.</summary>
public struct RevealBands : IEquatable<RevealBands>
{
    public const int Capacity = 4;
    public RevealBand Band0, Band1, Band2, Band3;

    public readonly RevealBand Get(int slot) => slot switch
    {
        0 => Band0,
        1 => Band1,
        2 => Band2,
        3 => Band3,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public void Set(int slot, in RevealBand band)
    {
        switch (slot)
        {
            case 0: Band0 = band; break;
            case 1: Band1 = band; break;
            case 2: Band2 = band; break;
            case 3: Band3 = band; break;
            default: throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    /// <summary><paramref name="mask"/> without the committed bands whose rows already left a model of
    /// <paramref name="itemCount"/> items (<see cref="RevealBand.Presents"/>): the bands the flow pass and every
    /// FlowCursor walk honour.</summary>
    public readonly byte PresentingMask(byte mask, int itemCount)
    {
        for (int i = 0; i < Capacity; i++)
            if ((mask & (1 << i)) != 0 && !Get(i).Presents(itemCount)) mask &= (byte)~(1 << i);
        return mask;
    }

    public readonly bool Equals(RevealBands o)
        => Band0.Equals(o.Band0) && Band1.Equals(o.Band1) && Band2.Equals(o.Band2) && Band3.Equals(o.Band3);
    public override readonly bool Equals(object? obj) => obj is RevealBands o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(Band0, Band1, Band2, Band3);
}
```

### 10.2 `src/FluentGpu.Engine/Scene/SceneStore.cs`

Delete `_activeVirtualDisclosureCount`, `HasActiveVirtualDisclosures`, `TryGetVirtualDisclosure`,
`BeginVirtualDisclosure`, `SetVirtualDisclosureProgress`, `VirtualDisclosureProgress`, `ClearVirtualDisclosure`.
In the node-free block (anchor: `if (_scroll.TryGet(idx, out var scroll) && float.IsFinite(scroll.DisclosureT))`)
replace the census decrement with:

```csharp
            if (_scroll.TryGet(idx, out var scroll) && scroll.BandMask != 0)
                for (int i = _revealBandViewports.Count - 1; i >= 0; i--)
                    if ((int)_revealBandViewports[i].Raw.Index == idx) _revealBandViewports.RemoveAt(i);
```

Add (where the disclosure API was):

```csharp
    // ── virtual reveal bands (docs/plans/smooth-reveal-implementation.md §10) ──────────────────────────────────
    private readonly List<NodeHandle> _revealBandViewports = new(4);

    /// <summary>True while any viewport holds a live reveal band (the recorder / hit-test census).</summary>
    public bool HasActiveRevealBands => _revealBandViewports.Count != 0;
    /// <summary>The viewports holding live bands (the flow pass walks these).</summary>
    internal List<NodeHandle> RevealBandViewports => _revealBandViewports;

    /// <summary>Resolve the live bands of the vertical viewport whose CONTENT node is <paramref name="content"/>, with the
    /// ordinal → logical-index mapping of its children (the persistent prefix, then the realized window).</summary>
    public bool TryGetRevealBands(NodeHandle content, out RevealBands bands, out byte mask, out int prefix, out int firstRealized)
    {
        bands = default; mask = 0; prefix = firstRealized = 0;
        if (_revealBandViewports.Count == 0 || content.IsNull || !IsLive(content)) return false;
        NodeHandle viewport = Parent(content);
        if (viewport.IsNull || !IsLive(viewport) || !_scroll.TryGet((int)viewport.Raw.Index, out var sc)
            || sc.ContentNode != content || sc.Orientation != 0 || sc.BandMask == 0) return false;
        mask = sc.Bands.PresentingMask(sc.BandMask, sc.ItemCount);   // a committed band whose rows left presents nothing
        if (mask == 0) return false;
        bands = sc.Bands;
        prefix = Math.Clamp(sc.PersistentPrefixCount, 0, sc.ItemCount);
        firstRealized = Math.Max(prefix, sc.FirstRealized);
        return true;
    }

    /// <summary>One band slot of a viewport (false when the slot is not live).</summary>
    public bool TryGetRevealBand(NodeHandle viewport, int slot, out RevealBand band)
    {
        band = default;
        if ((uint)slot >= RevealBands.Capacity || viewport.IsNull || !IsLive(viewport)
            || !_scroll.TryGet((int)viewport.Raw.Index, out var sc) || (sc.BandMask & (1 << slot)) == 0) return false;
        band = sc.Bands.Get(slot);
        return true;
    }

    /// <summary>Arm or retarget band <paramref name="slot"/> of a vertical virtual viewport.</summary>
    public bool SetRevealBand(NodeHandle viewport, int slot, int first, int count, float top, float extent, bool opening, float presented)
    {
        if ((uint)slot >= RevealBands.Capacity || viewport.IsNull || !IsLive(viewport) || first < 0 || count <= 0
            || !float.IsFinite(top) || !(extent > 0f) || !_scroll.TryGet((int)viewport.Raw.Index, out var snap)
            || snap.Orientation != 0 || snap.ContentNode.IsNull || !IsLive(snap.ContentNode)) return false;
        ref ScrollState sc = ref ScrollRef(viewport);
        bool wasAny = sc.BandMask != 0;
        sc.Bands.Set(slot, new RevealBand { First = first, Count = count, Top = top, Extent = extent, Opening = opening, Presented = presented });
        sc.BandMask |= (byte)(1 << slot);
        if (!wasAny) _revealBandViewports.Add(viewport);
        Mark(sc.ContentNode, NodeFlags.PaintDirty);
        return true;
    }

    /// <summary>The band row's write (AnimEngine side-table): the presented height this tick.</summary>
    public void SetRevealBandPresented(NodeHandle viewport, int slot, float presented)
    {
        if (!TryGetRevealBand(viewport, slot, out var band) || band.Presented == presented) return;
        ref ScrollState sc = ref ScrollRef(viewport);
        band.Presented = presented;
        sc.Bands.Set(slot, in band);
        if (!sc.ContentNode.IsNull && IsLive(sc.ContentNode)) Mark(sc.ContentNode, NodeFlags.PaintDirty);
    }

    /// <summary>Refresh a band's laid-out geometry (the flow pass, from the virtual layout).</summary>
    public void SetRevealBandGeometry(NodeHandle viewport, int slot, float top, float extent)
    {
        if (!TryGetRevealBand(viewport, slot, out var band) || (band.Top == top && band.Extent == extent)) return;
        ref ScrollState sc = ref ScrollRef(viewport);
        band.Top = top;
        band.Extent = extent;
        sc.Bands.Set(slot, in band);
        if (!sc.ContentNode.IsNull && IsLive(sc.ContentNode)) Mark(sc.ContentNode, NodeFlags.PaintDirty);
    }

    /// <summary>A closing band came to rest and its owner is about to remove its rows (ItemsViewController.BandSettled,
    /// frame start, right before the collapse commit). It keeps presenting the rows at 0 while they are still modelled; from
    /// the flush that drops them (ItemCount moves off <see cref="RevealBand.CommitCount"/>) it contributes no delta and no
    /// clip, so the commit frame's 6.3 flow pass and scroll sync see exactly the laid-out extent. Idempotent.</summary>
    public void CommitRevealBand(NodeHandle viewport, int slot)
    {
        if (!TryGetRevealBand(viewport, slot, out var band) || band.Committed) return;
        ref ScrollState sc = ref ScrollRef(viewport);
        band.Committed = true;
        band.CommitCount = sc.ItemCount;
        band.Presented = 0f;
        sc.Bands.Set(slot, in band);
        if (!sc.ContentNode.IsNull && IsLive(sc.ContentNode)) Mark(sc.ContentNode, NodeFlags.PaintDirty);
    }

    /// <summary>Move a band to the rows it covers now (its owner's model moved under it).</summary>
    public void SetRevealBandRange(NodeHandle viewport, int slot, int first, int count)
    {
        if (first < 0 || count <= 0 || !TryGetRevealBand(viewport, slot, out var band) || (band.First == first && band.Count == count)) return;
        ref ScrollState sc = ref ScrollRef(viewport);
        band.First = first;
        band.Count = count;
        sc.Bands.Set(slot, in band);
        if (!sc.ContentNode.IsNull && IsLive(sc.ContentNode)) Mark(sc.ContentNode, NodeFlags.PaintDirty);
    }

    /// <summary>Release one band. Repeated clears are harmless and never unbalance the census.</summary>
    public void ClearRevealBand(NodeHandle viewport, int slot)
    {
        if (!TryGetRevealBand(viewport, slot, out _)) return;
        ref ScrollState sc = ref ScrollRef(viewport);
        sc.BandMask &= (byte)~(1 << slot);
        sc.Bands.Set(slot, default);
        if (sc.BandMask == 0) _revealBandViewports.Remove(viewport);
        if (!sc.ContentNode.IsNull && IsLive(sc.ContentNode)) Mark(sc.ContentNode, NodeFlags.PaintDirty);
    }
```

(`SceneStore.cs` needs `using System.Collections.Generic;` — present already for other lists.)

### 10.3 Snapshot + publication key

- `src/FluentGpu.Engine/Scene/SceneRecordingSnapshot.cs`: rename the property `HasActiveVirtualDisclosures` →
  `HasActiveRevealBands` (and its copy line → `HasActiveRevealBands = source.HasActiveRevealBands;`). Replace
  `TryGetVirtualDisclosure` with:

```csharp
    public bool TryGetRevealBands(NodeHandle content, out RevealBands bands, out byte mask, out int prefix, out int firstRealized)
    {
        bands = default; mask = 0; prefix = firstRealized = 0;
        if (!HasActiveRevealBands || content.IsNull || !IsLive(content)) return false;
        var viewport = Parent(content);
        if (viewport.IsNull || !IsLive(viewport) || !TryGetScroll(viewport, out var scroll)
            || scroll.ContentNode != content || scroll.Orientation != 0 || scroll.BandMask == 0) return false;
        mask = scroll.Bands.PresentingMask(scroll.BandMask, scroll.ItemCount);   // the same rule as SceneStore's
        if (mask == 0) return false;
        bands = scroll.Bands;
        prefix = Math.Clamp(scroll.PersistentPrefixCount, 0, scroll.ItemCount);
        firstRealized = Math.Max(prefix, scroll.FirstRealized);
        return true;
    }
```

- `SceneRecordingSnapshot.Parity.cs`: the disclosure line becomes
  `if (HasActiveRevealBands != other.HasActiveRevealBands) return Fail(out mismatch, "HasActiveRevealBands");`.
  `ScrollEqual` keeps `a.Equals(b)` (complete by construction). Replace its comment with:

```csharp
    // ScrollState carries object references (Layout/SnapPoints/ScrollKey) that capture nulls out, so the default
    // structural comparison is both complete and correct here — and completeness is the point: a field this comparison
    // skipped would be a field an incremental capture could silently publish stale. The reveal bands compare through
    // RevealBands/RevealBand.Equals (explicit, NaN-safe). They are named fields, never an [InlineArray]: the runtime throws
    // from Equals/GetHashCode on any struct holding one, which would crash every parity check of a scene with a scroll row.
```

  Engine.Tests pins it (`RevealBandsTests`, §11.3).
- `Hosting/NoopPublicationGate.cs`: `bool VirtualDisclosures,` → `bool RevealBands,`.
- `Hosting/AppHost.cs` `BuildPublicationKey`: `_scene.HasActiveVirtualDisclosures` → `_scene.HasActiveRevealBands`.

### 10.4 AnimEngine bands

`Animation/AnimTypes.cs`: remove `DisclosureProgress` from `AnimChannel` and append `RevealBand0, RevealBand1,
RevealBand2, RevealBand3` after `RevealExtent` (doc: "RevealBand0..3 = the presented height of virtual reveal band
slot 0..3 on a viewport node — side-table rows written into ScrollState.Bands").

`Animation/AnimScheduler.cs`:
- `IsSideTableChannel`: replace `|| ch == AnimChannel.DisclosureProgress` with `|| IsRevealBand(ch)`.
- `WriteSideTable`: replace the DisclosureProgress case with
  `case >= AnimChannel.RevealBand0 and <= AnimChannel.RevealBand3: _scene.SetRevealBandPresented(node, (int)ch - (int)AnimChannel.RevealBand0, v); break;`
- `CurrentValue`: replace the DisclosureProgress arm with
  `>= AnimChannel.RevealBand0 and <= AnimChannel.RevealBand3 => _scene.TryGetRevealBand(node, (int)ch - (int)AnimChannel.RevealBand0, out var band) && float.IsFinite(band.Presented) ? band.Presented : 0f,`
- `RestDeltaFor`: add `or >= AnimChannel.RevealBand0 and <= AnimChannel.RevealBand3` (parenthesize: `ch is (AnimChannel.SizeW or … or AnimChannel.RevealExtent) or (>= AnimChannel.RevealBand0 and <= AnimChannel.RevealBand3) ? 0.5f : …`).
- `CollectAndFreeDone` body (extends §4.2 item 5):

```csharp
        foreach (int s in _settledScratch)
        {
            AnimChannel ch = _slab.At(s).Channel;
            NodeHandle owner = _slab.At(s).Node;
            if (ch == AnimChannel.RevealExtent) QueueSettled(owner, ch);   // a reveal at rest: its owner's callback
            else if (IsRevealBand(ch))
            {
                // An OPENING band is done: present the laid-out rows as they are. A closing one rests at 0 until its owner's
                // commit removes the rows (BandSettled marks it committed first: it stops presenting the flush they leave).
                int slot = (int)ch - (int)AnimChannel.RevealBand0;
                if (_scene.TryGetRevealBand(owner, slot, out var band) && band.Opening) _scene.ClearRevealBand(owner, slot);
                QueueSettled(owner, ch);
            }
            SettleRestore(s);
            FreeSlot(s);
        }
```

`Animation/AnimScheduler.Compositor.cs`: the channel line becomes
`if (row.Channel is AnimChannel.LayoutW or AnimChannel.LayoutH or AnimChannel.RevealExtent or (>= AnimChannel.RevealBand0 and <= AnimChannel.RevealBand3)) return false;`

`Animation/AnimScheduler.FlowReveal.cs` — `HasFlowRevealWork` gains `|| _scene.HasActiveRevealBands` (a frame with a live
band also defers layout's offset clamp). Add the code below, and change the pass's early-out to
`if (_revealRows == 0 && _flowTouched.Count == 0 && _ovPrevViewports.Count == 0 && !_scene.HasActiveRevealBands) return false;`,
and call `AddBands` for every band viewport after the reveal-row loop, before `FinishFlow()`:
`var bandViewports = _scene.RevealBandViewports; for (int i = 0; i < bandViewports.Count; i++) AddBands(bandViewports[i]);`):

```csharp
    // ── virtual reveal bands ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The row channel of band <paramref name="slot"/> on its viewport node.</summary>
    public static AnimChannel RevealBandChannel(int slot) => (AnimChannel)((int)AnimChannel.RevealBand0 + slot);

    private static bool IsRevealBand(AnimChannel ch) => ch >= AnimChannel.RevealBand0 && ch <= AnimChannel.RevealBand3;

    /// <summary>Arm (or REVERSE) band <paramref name="slot"/> of a vertical virtual viewport over the logical rows
    /// [<paramref name="first"/>, +<paramref name="count"/>) — the model must already hold the rows laid out (an expand
    /// inserts them first; a collapse keeps them until its commit). Springs the presented height toward the rows' extent
    /// (<paramref name="opening"/>) or 0 under MotionTok.Reveal, from the live value with its velocity, clamped to the
    /// visible span; snaps (and reports settled) under reduced motion or when nothing of it is visible. False when the
    /// viewport cannot carry a band (not a vertical virtual list, or the range is out of its model).</summary>
    public bool BeginRevealBand(NodeHandle viewport, int slot, int first, int count, bool opening)
    {
        if ((uint)slot >= RevealBands.Capacity || viewport.IsNull || !_scene.IsLive(viewport)
            || !_scene.TryGetScroll(viewport, out var sc) || sc.Orientation != 0 || sc.Layout is null
            || sc.ContentNode.IsNull || !_scene.IsLive(sc.ContentNode) || first < 0 || count <= 0 || first + count > sc.ItemCount)
            return false;
        float cross = MathF.Max(1f, _scene.Bounds(viewport).W);
        RectF a = sc.Layout.ItemRect(first, cross), z = sc.Layout.ItemRect(first + count - 1, cross);
        float top = a.Y, extent = z.Bottom - a.Y;
        if (!float.IsFinite(top) || !float.IsFinite(extent) || extent <= 0f) return false;

        AnimChannel ch = RevealBandChannel(slot);
        int ex = Find(viewport, ch);
        float cur = ex >= 0 ? _slab.At(ex).Position
            : _scene.TryGetRevealBand(viewport, slot, out var old) && float.IsFinite(old.Presented) ? old.Presented
            : opening ? 0f : extent;
        float vel = ex >= 0 ? _slab.At(ex).Velocity : 0f;
        float p1 = opening ? extent : 0f;
        RectF view = _scene.AbsoluteRect(viewport);
        float regionTop = _scene.AbsoluteRect(sc.ContentNode).Y + top;
        bool animate = !FluentGpu.Dsl.Motion.ReducedMotion
            && RevealPlan.TryClamp(cur, p1, regionTop, view.Y, view.Y + sc.ViewportH, out float from, out float to);
        if (!animate)
        {
            if (ex >= 0) Cancel(viewport, ch);
            if (opening) _scene.ClearRevealBand(viewport, slot);
            else _scene.SetRevealBand(viewport, slot, first, count, top, extent, opening: false, presented: 0f);
            // Wholly ABOVE the view: the snap moves every row below by (p1 − cur). Shift the scroll frame by the same
            // amount so the rows the user reads stay put. A collapse's later commit removes rows already presented at 0
            // (a zero-delta handoff), so it adds no second shift.
            if (RevealPlan.IsAboveView(cur, p1, regionTop, view.Y) && MathF.Abs(p1 - cur) >= 0.5f)
                _scene.ScrollHandleFor(viewport)?.ShiftFrame(p1 - cur);
            QueueSettled(viewport, ch);
            return true;
        }
        if (!_scene.SetRevealBand(viewport, slot, first, count, top, extent, opening, ex >= 0 ? cur : from)) return false;
        var spring = MotionTok.Reveal.Spring;
        if (ex >= 0 && MathF.Abs(from - cur) < 0.5f) Spring(viewport, ch, to, spring);   // reverse: live value + velocity
        else
        {
            if (ex >= 0) Cancel(viewport, ch);
            Spring(viewport, ch, to, spring, initial: from, initialVelocity: vel);
        }
        return true;
    }

    /// <summary>Move a live band to the rows it covers now (the owner's model moved under it).</summary>
    public void SetRevealBandRange(NodeHandle viewport, int slot, int first, int count)
        => _scene.SetRevealBandRange(viewport, slot, first, count);

    /// <summary>Release a band (its row, if any, too). Repeated clears are harmless.</summary>
    public void ClearRevealBand(NodeHandle viewport, int slot)
    {
        if ((uint)slot >= RevealBands.Capacity || viewport.IsNull) return;
        Cancel(viewport, RevealBandChannel(slot));
        _scene.ClearRevealBand(viewport, slot);
    }

    /// <summary>A closing band is at rest and its owner is about to remove its rows (ItemsViewController.BandSettled, frame
    /// start, right BEFORE the collapse commit). The band stops presenting the flush its rows leave the model
    /// (<see cref="RevealBand.Presents"/>), so the commit frame's 6.3 pass and scroll sync never subtract a phantom band.
    /// Its row (if a stray one is left) goes too.</summary>
    public void CommitRevealBand(NodeHandle viewport, int slot)
    {
        if ((uint)slot >= RevealBands.Capacity || viewport.IsNull) return;
        Cancel(viewport, RevealBandChannel(slot));
        _scene.CommitRevealBand(viewport, slot);
    }

    // A viewport's bands, folded into its content's flow: the laid-out geometry refreshed from the layout (rows above may
    // have opened or closed, realized rows report measured extents), Σ(presented − extent) onto the content, the overscan.
    // A committed band whose rows already left the model contributes nothing: this is the zero-delta handoff, made at 6.3
    // of the commit frame (before the scroll sync), not at ItemsView's 6.5 clear.
    private void AddBands(NodeHandle viewport)
    {
        if (!_scene.IsLive(viewport) || !_scene.TryGetScroll(viewport, out var sc) || sc.BandMask == 0
            || sc.ContentNode.IsNull || !_scene.IsLive(sc.ContentNode)) return;
        float cross = MathF.Max(1f, _scene.Bounds(viewport).W);
        float delta = 0f, overscan = 0f;
        for (int slot = 0; slot < RevealBands.Capacity; slot++)
        {
            if ((sc.BandMask & (1 << slot)) == 0) continue;
            RevealBand b = sc.Bands.Get(slot);
            if (!b.Presents(sc.ItemCount)) continue;
            if (sc.Layout is { } layout && b.Count > 0 && b.First + b.Count <= sc.ItemCount)
            {
                RectF a = layout.ItemRect(b.First, cross), z = layout.ItemRect(b.First + b.Count - 1, cross);
                float top = a.Y, extent = z.Bottom - a.Y;
                if (float.IsFinite(top) && float.IsFinite(extent) && extent > 0f)
                {
                    _scene.SetRevealBandGeometry(viewport, slot, top, extent);
                    b.Top = top;
                    b.Extent = extent;
                }
            }
            float p = float.IsNaN(b.Presented) ? b.Extent : Math.Clamp(b.Presented, 0f, b.Extent);
            delta += p - b.Extent;
            int s = Find(viewport, RevealBandChannel(slot));
            float target = s >= 0 ? _slab.At(s).To : p;
            overscan += MathF.Max(0f, b.Extent - MathF.Min(p, target));
        }
        ref NodePaint cp = ref TouchFlow(sc.ContentNode);
        cp.FlowDelta += delta;
        cp.FlowBits |= NodePaint.FlowBoundaryBit;
        AddOverscan(viewport, overscan);
    }
```

### 10.5 `FlowCursor` bands — `src/FluentGpu.Engine/Scene/FlowCursor.cs`

Add the fields and `SetBands`, and insert the band block at the top of `Step` (after `clipTop = clipBottom = float.NaN;`):

```csharp
    // Virtual reveal bands, sorted by First; _next = the first band this walk has not yet passed.
    private RevealBands _bands;
    private int _bandCount, _next, _prefix, _firstRealized;

    /// <summary>Attach a virtual viewport's live reveal bands (its CONTENT node's walk): the rows of a band clip to the band's
    /// presented height, and every row after a band shifts by its presented-minus-laid-out delta.</summary>
    public void SetBands(in RevealBands bands, byte mask, int prefix, int firstRealized)
    {
        _bandCount = 0;
        for (int i = 0; i < RevealBands.Capacity; i++)
        {
            RevealBand b = bands.Get(i);
            if ((mask & (1 << i)) == 0 || b.Count <= 0 || !(b.Extent > 0f)) continue;
            int j = _bandCount++;
            while (j > 0 && _bands.Get(j - 1).First > b.First) { _bands.Set(j, _bands.Get(j - 1)); j--; }
            _bands.Set(j, in b);
        }
        _prefix = prefix;
        _firstRealized = firstRealized;
        _next = 0;
        if (_bandCount > 0) _active = true;
    }

    private static float BandPresented(in RevealBand b) => float.IsNaN(b.Presented) ? b.Extent : Math.Clamp(b.Presented, 0f, b.Extent);
```

```csharp
        if (_bandCount > 0)
        {
            int logical = ordinal < _prefix ? ordinal : _firstRealized + (ordinal - _prefix);
            while (_next < _bandCount)
            {
                RevealBand passed = _bands.Get(_next);
                if (passed.First + passed.Count > logical) break;
                _shift += BandPresented(in passed) - passed.Extent;
                _next++;
            }
            if (_next < _bandCount)
            {
                RevealBand band = _bands.Get(_next);
                if (logical >= band.First)
                {
                    clipTop = band.Top + _shift;
                    clipBottom = clipTop + BandPresented(in band);
                }
            }
        }
```

(`using System;` at the top for `Math`.) Update the type doc: "…and (virtual lists) the rows of a reveal band clip to
the band's presented height and the rows after it ride its delta."

### 10.6 Recorder / hit-test / presented geometry — delete the disclosure paths, attach bands

- `Render/SceneRecorder.cs`: rename the `RecordAccumulator` field `HasActiveVirtualDisclosures` → `HasActiveRevealBands`
  (both initialisers: `HasActiveRevealBands = scene.HasActiveRevealBands`). Delete the whole `disclosure*` block
  (`int disclosureFirst = 0, …` through the `if (hasDisclosure) { … disclosureClip … }` block), the
  `if (hasDisclosure) { … }` block inside the main loop and the one inside the pinned loop. After each
  `FlowCursor.For(in p)` line (main loop and pinned loop) add:

```csharp
            if (stats.HasActiveRevealBands && scene.TryGetRevealBands(node, out RevealBands flowBands, out byte flowMask, out int flowPrefix, out int flowFirst))
                flow.SetBands(in flowBands, flowMask, flowPrefix, flowFirst);   // (pinnedFlow in the pinned loop)
```

- `Input/InputDispatcher.cs`: `HitTest`/`HitTestAny` callers pass `_scene.HasActiveRevealBands`; rename the parameter
  `anyDisclosure` → `anyBands` in `Hit`/`HitAny` (and their recursive calls); delete the `disclosure*` locals and the
  `if (hasDisclosure) { … }` block; after `var flow = FlowCursor.For(in np);` add
  `if (anyBands && _scene.TryGetRevealBands(node, out RevealBands bands, out byte bandMask, out int bandPrefix, out int bandFirst)) flow.SetBands(in bands, bandMask, bandPrefix, bandFirst);`
- `Scene/SceneStore.Flow.cs` `PresentedAbsoluteRect`: after `var cursor = FlowCursor.For(in _paint[parent.Raw.Index]);` add
  `if (TryGetRevealBands(parent, out var bands, out byte mask, out int prefix, out int firstRealized)) cursor.SetBands(in bands, mask, prefix, firstRealized);`

### 10.7 Reconciler / RenderContext seams deleted

- `Reconciler/Reconciler.cs`: delete `_beginVirtualDisclosureSeam`, `_completeVirtualDisclosureSeam`,
  `_clearVirtualDisclosureSeam`, the three `ctx.…VirtualDisclosure = …` lines, and the methods
  `BeginVirtualDisclosure`, `CompleteVirtualDisclosure`, `ClearVirtualDisclosure` (with their doc comments). ItemsView
  now talks to `Context.Anim` (the band API lives on AnimEngine, which owns the rows).
- `Hooks/RenderContext.cs`: delete the doc line and the three `…VirtualDisclosure` delegate fields.

### 10.8 ItemsView controller + view — `src/FluentGpu.Controls/ItemsView.cs` and `ListOptions.cs`

`ListOptions.cs` — replace `DisclosureOptions`:

```csharp
/// <summary>The disclosure seam of a bound virtual list (docs/plans/smooth-reveal-implementation.md §10). The owner starts
/// or reverses a band with <see cref="ItemsViewController.BeginDisclosure"/>; this record lets the view re-find each band
/// as the model moves under it.</summary>
public sealed record DisclosureOptions
{
    /// <summary>Re-render trigger for the model the bands index (a plan version).</summary>
    public IReadSignal<int>? Version { get; init; }
    /// <summary>A band's CURRENT range by its key, or null once its rows are gone (a committed collapse). Optional: without
    /// it a band keeps its armed range and a collapse clears when the count has dropped by the band.</summary>
    public Func<string, ItemDisclosureRange?>? ResolveRange { get; init; }
    /// <summary>Optional cold-path lifecycle trace. Invoked from controller/layout/effect work, never paint or input.</summary>
    public Action<ItemDisclosureDiagnostic>? Diagnostic { get; init; }
}
```

`ItemsView.cs` — controller. Replace the fields `CompleteDisclosureImpl` … `_nextDisclosureOperationId` and the
`DisclosureSpliceRange` property with:

```csharp
    internal Action<int>? ClearBandImpl;
    /// <summary>Marks the engine band committed (AnimEngine.CommitRevealBand) right before a collapse commit runs.</summary>
    internal Action<int>? CommitBandImpl;
    internal readonly Signal<int> DisclosureVersion = new(0);
    /// <summary>The live disclosure bands, by slot (the engine's RevealBands slots on this view's viewport).</summary>
    internal readonly ItemRevealBand?[] Bands = new ItemRevealBand?[RevealBands.Capacity];
    /// <summary>One settle callback per slot, registered with the engine by the mounted view (AnimEngine.WhenSettled).</summary>
    internal readonly Action[] BandSettledActions;
    internal Action<ItemDisclosureDiagnostic>? DisclosureDiagnostic;
    /// <summary>The item count the mounted view last laid out (a collapse's commit-time count).</summary>
    internal int ObservedCount;
    private long _nextDisclosureOperationId;

    public ItemsViewController()
    {
        BandSettledActions = new Action[RevealBands.Capacity];
        for (int i = 0; i < BandSettledActions.Length; i++)
        {
            int slot = i;
            BandSettledActions[i] = () => BandSettled(slot);
        }
    }
```

Replace `BeginDisclosure` … `Trace` (everything from `/// <summary>Begin or retarget one contiguous disclosure band.`
through the end of the private `Trace` method) with:

```csharp
    /// <summary>Begin a disclosure over one contiguous band, or REVERSE the one already running under the same key. Any
    /// number of keys run at once (four bands in flight per view; a fifth lands the oldest at its endpoint). Expand: the
    /// owner inserts the rows FIRST (in the same input turn), then calls this. Collapse: the rows stay until the band rests,
    /// then <paramref name="collapseCommit"/> runs exactly once (at the next frame's start) to remove them — a collapse
    /// reversed into an expand never commits. <paramref name="settled"/> runs when the latest direction comes to rest.</summary>
    public void BeginDisclosure(ItemDisclosureRange range, ItemDisclosureDirection direction,
                                Action? collapseCommit = null, Action? settled = null)
    {
        if (range.FirstIndex < 0) throw new ArgumentOutOfRangeException(nameof(range));
        if (range.Count <= 0) throw new ArgumentOutOfRangeException(nameof(range));
        if (string.IsNullOrEmpty(range.Key)) throw new ArgumentException("A stable logical key is required.", nameof(range));
        if (direction == ItemDisclosureDirection.Collapse && collapseCommit is null)
            throw new ArgumentNullException(nameof(collapseCommit));

        int slot = SlotOf(range.Key);
        bool reverse = slot >= 0 && Bands[slot]!.Direction != direction;
        if (slot < 0)
        {
            slot = FreeSlot();
            if (slot < 0) { slot = OldestSlot(); FinishNow(slot); }
            Bands[slot] = new ItemRevealBand();
        }
        var band = Bands[slot]!;
        band.Range = range;
        band.Direction = direction;
        band.CollapseCommit = direction == ItemDisclosureDirection.Collapse ? collapseCommit : null;
        band.Settled = settled;
        band.Phase = ItemRevealPhase.Pending;
        band.Spliced = false;
        band.OperationId = ++_nextDisclosureOperationId;
        Trace(reverse ? ItemDisclosureDiagnosticKind.Reversed : ItemDisclosureDiagnosticKind.Queued, band, -1);
        DisclosureVersion.Value = DisclosureVersion.Peek() + 1;
    }

    private int SlotOf(string key)
    {
        for (int i = 0; i < Bands.Length; i++)
            if (Bands[i] is { Phase: not ItemRevealPhase.Committed } b && string.Equals(b.Range.Key, key, StringComparison.Ordinal)) return i;
        return -1;
    }

    private int FreeSlot()
    {
        for (int i = 0; i < Bands.Length; i++) if (Bands[i] is null) return i;
        return -1;
    }

    private int OldestSlot()
    {
        int best = 0;
        for (int i = 1; i < Bands.Length; i++) if (Bands[i]!.OperationId < Bands[best]!.OperationId) best = i;
        return best;
    }

    // A fifth concurrent band: the OLDEST lands at its endpoint now — the one place a disclosure snaps (four in flight on
    // one list is already past anything a user starts by hand).
    private void FinishNow(int slot)
    {
        if (Bands[slot] is not { } band) return;
        ClearBandImpl?.Invoke(slot);
        Bands[slot] = null;
        if (band.Direction == ItemDisclosureDirection.Collapse && band.Phase != ItemRevealPhase.Committed) band.CollapseCommit?.Invoke();
        band.Settled?.Invoke();
    }

    /// <summary>The view armed band <paramref name="slot"/> against <paramref name="count"/> items.</summary>
    internal void BandArmed(int slot, int count)
    {
        if (Bands[slot] is not { } band) return;
        band.Phase = ItemRevealPhase.Running;
        Trace(ItemDisclosureDiagnosticKind.Armed, band, count);
    }

    /// <summary>The engine reported band <paramref name="slot"/> at rest (next-frame callback).</summary>
    internal void BandSettled(int slot)
    {
        if (Bands[slot] is not { Phase: ItemRevealPhase.Running } band) return;
        if (band.Direction == ItemDisclosureDirection.Collapse)
        {
            band.Phase = ItemRevealPhase.Committed;
            band.CountAtCommit = ObservedCount;
            band.Spliced = false;
            Trace(ItemDisclosureDiagnosticKind.Committing, band, ObservedCount);
            // The engine band goes committed BEFORE the owner removes the rows: from the flush that drops them it adds no
            // delta and no clip, so this frame's 6.3 pass and scroll sync see exactly the laid-out extent (a list scrolled
            // to its end keeps its offset). The view's layout effect only releases the slot afterwards.
            CommitBandImpl?.Invoke(slot);
            band.CollapseCommit?.Invoke();
        }
        else Bands[slot] = null;   // the engine cleared the band when it rested open
        Trace(ItemDisclosureDiagnosticKind.Settled, band, ObservedCount);
        band.Settled?.Invoke();
        DisclosureVersion.Value = DisclosureVersion.Peek() + 1;
    }

    /// <summary>The view could not arm band <paramref name="slot"/> (no range, not a vertical virtual list): it lands now —
    /// a collapse still commits exactly once.</summary>
    internal void BandFailed(int slot, int count)
    {
        if (Bands[slot] is not { } band) return;
        Bands[slot] = null;
        Trace(ItemDisclosureDiagnosticKind.FailedToArm, band, count);
        if (band.Direction == ItemDisclosureDirection.Collapse) band.CollapseCommit?.Invoke();
        band.Settled?.Invoke();
    }

    /// <summary>A committed collapse's rows left the layout: its slot is released (the engine band stopped presenting at
    /// 6.3 of the same frame — CommitBandImpl).</summary>
    internal void BandCleared(int slot, int count)
    {
        if (Bands[slot] is not { } band) return;
        Bands[slot] = null;
        Trace(ItemDisclosureDiagnosticKind.Cleared, band, count);
    }

    /// <summary>The view unmounted: every band lands (a pending collapse commits, so the model ends where it was asked to).</summary>
    internal void ResetBands()
    {
        for (int i = 0; i < Bands.Length; i++)
        {
            if (Bands[i] is not { } band) continue;
            Bands[i] = null;
            if (band.Direction == ItemDisclosureDirection.Collapse && band.Phase != ItemRevealPhase.Committed) band.CollapseCommit?.Invoke();
            band.Settled?.Invoke();
        }
    }

    /// <summary>The bands' STRUCTURAL edits, applied to a splicing layout in the render that first observes the new count —
    /// an expand's insertion, a committed collapse's removal — so every surviving row keeps its measured extent.</summary>
    internal void SpliceDisclosures(ISplicingVirtualLayout layout, int count)
    {
        int have = layout.ItemCount;
        if (have < 0 || have == count) return;
        int diff = count - have;
        for (int i = 0; i < Bands.Length; i++)
        {
            if (Bands[i] is not { Spliced: false } b) continue;
            if (b.Direction == ItemDisclosureDirection.Expand && b.Phase != ItemRevealPhase.Committed && diff == b.Range.Count)
            {
                layout.Splice(b.Range.FirstIndex, 0, b.Range.Count);
                b.Spliced = true;
                return;
            }
            if (b.Phase == ItemRevealPhase.Committed && diff == -b.Range.Count)
            {
                layout.Splice(b.Range.FirstIndex, b.Range.Count, 0);
                b.Spliced = true;
                return;
            }
        }
    }

    private void Trace(ItemDisclosureDiagnosticKind kind, ItemRevealBand band, int itemCount)
        => DisclosureDiagnostic?.Invoke(new ItemDisclosureDiagnostic(kind, band.OperationId, band.Range, band.Direction, itemCount));
```

Replace the type declarations after the controller (`ItemDisclosureDiagnosticKind`, `ItemDisclosureDiagnostic`,
`ItemDisclosureRequest`) with:

```csharp
public enum ItemDisclosureDiagnosticKind : byte { Queued, Reversed, Armed, Committing, Settled, Cleared, FailedToArm }

public readonly record struct ItemDisclosureDiagnostic(ItemDisclosureDiagnosticKind Kind, long OperationId,
    ItemDisclosureRange Range, ItemDisclosureDirection Direction, int ItemCount);

internal enum ItemRevealPhase : byte { Pending, Running, Committed }

/// <summary>One disclosure band the controller tracks (slot = the engine RevealBands slot on the view's viewport).</summary>
internal sealed class ItemRevealBand
{
    public ItemDisclosureRange Range;
    public ItemDisclosureDirection Direction;
    public Action? CollapseCommit;
    public Action? Settled;
    public ItemRevealPhase Phase;
    public long OperationId;
    public int CountAtCommit = -1;
    public bool Spliced;
}
```

`ItemsView.cs` — view (`Render`):

1. Delete the `PrepareExpand` block (anchor: `if (Controller is { } disclosureOwner && Disclosure?.PendingExpand?.Invoke() is { } preparedRange`
   … the `PrepareExpand(…)` call).
2. Replace the splice block (anchor: `if (layout is ISplicingVirtualLayout splicing && Controller?.DisclosureSpliceRange is { Count: > 0 } band)` and its body) with
   `if (layout is ISplicingVirtualLayout splicing && Controller is { } spliceOwner) spliceOwner.SpliceDisclosures(splicing, count);`
   (keep the comment above it; replace "the band a disclosure is about to insert" wording with "the bands' structural edits").
3. In the controller wiring block: replace `ctl.CompleteDisclosureImpl = expanded => Context.CompleteVirtualDisclosure?.Invoke(viewportNode.Value, expanded);`
   with the two lines
   `ctl.ClearBandImpl = slot => Context.Anim?.ClearRevealBand(viewportNode.Value, slot);` and
   `ctl.CommitBandImpl = slot => Context.Anim?.CommitRevealBand(viewportNode.Value, slot);`. In the cleanup lambda replace
   `ctl.CompleteDisclosureImpl = null;` with `ctl.ClearBandImpl = null; ctl.CommitBandImpl = null;` and add
   `ctl.ResetBands();` before `ctl.Selection = null;`.
4. After that cleanup `UseEffect`, add the settle registration:

```csharp
        // Band settle callbacks (the engine reports a band at rest at the next frame's start — no per-frame watcher).
        UseEffect(() =>
        {
            var ctl = Controller;
            var anim = Context.Anim;
            var vp = viewportNode.Value;
            if (ctl is null || anim is null || vp.IsNull) return null;
            for (int i = 0; i < RevealBands.Capacity; i++) anim.WhenSettled(vp, AnimEngine.RevealBandChannel(i), ctl.BandSettledActions[i]);
            return () => { for (int i = 0; i < RevealBands.Capacity; i++) anim.WhenSettled(vp, AnimEngine.RevealBandChannel(i), null); };
        }, DepKey.FromRef(Controller));
```

5. Replace the disclosure `UseLayoutEffect` (anchor: `// Seed after the changed item model has reconciled and laid out, but before paint` … through
   `}, DepKey.From(HashCode.Combine(disclosureVer, disclosureSourceVer, count)));`) with:

```csharp
        // Arm / move / clear the bands after the changed item model has reconciled and laid out, but before paint: an
        // expanding band therefore starts at 0 instead of flashing once at full height. A committed collapse's band stopped
        // presenting at 6.3 of the frame its rows left the model (CommitBandImpl, the zero-delta handoff); here its slot is
        // only released.
        UseLayoutEffect(() =>
        {
            if (Controller is not { } ctl || Context.Anim is not { } anim) return;
            var viewport = viewportNode.Value;
            if (viewport.IsNull) return;
            ctl.ObservedCount = count;
            var resolve = Disclosure?.ResolveRange;
            for (int slot = 0; slot < ctl.Bands.Length; slot++)
            {
                if (ctl.Bands[slot] is not { } band) continue;
                if (band.Phase == ItemRevealPhase.Committed)
                {
                    bool gone = resolve is not null
                        ? resolve(band.Range.Key) is null && count != band.CountAtCommit
                        : count <= band.CountAtCommit - band.Range.Count;
                    if (!gone) continue;
                    anim.ClearRevealBand(viewport, slot);
                    ctl.BandCleared(slot, count);
                    continue;
                }
                ItemDisclosureRange range = resolve?.Invoke(band.Range.Key) ?? band.Range;
                if (range.FirstIndex < 0 || range.Count <= 0 || range.FirstIndex + range.Count > count)
                {
                    if (band.Phase == ItemRevealPhase.Pending) { anim.ClearRevealBand(viewport, slot); ctl.BandFailed(slot, count); }
                    continue;
                }
                if (band.Phase == ItemRevealPhase.Running)
                {
                    if (range != band.Range) { band.Range = range; anim.SetRevealBandRange(viewport, slot, range.FirstIndex, range.Count); }
                    continue;
                }
                band.Range = range;
                if (anim.BeginRevealBand(viewport, slot, range.FirstIndex, range.Count, band.Direction == ItemDisclosureDirection.Expand))
                    ctl.BandArmed(slot, count);
                else
                {
                    anim.ClearRevealBand(viewport, slot);
                    ctl.BandFailed(slot, count);
                }
            }
        }, DepKey.From(HashCode.Combine(disclosureVer, disclosureSourceVer, count)));
```

6. Delete the watcher mount (`ItemsViewController? disclosureController = Controller; bool watchesDisclosure = …;
   Element[] rootChildren = watchesDisclosure ? … : …;`) → `Element[] rootChildren = [itemsHost];`, and delete the class
   `ItemsViewDisclosureWatcher`.
7. `ItemsView.cs` needs `using FluentGpu.Scene;` (already present) for `RevealBands`.

---

## 11. Phase 2 gates (engine)

### 11.1 `src/FluentGpu.VerticalSlice/Suites/ControlsSuite.cs`

Delete `VirtualDisclosureChecks`, `VirtualDisclosureFastPathChecks`, `VirtualDisclosureProbe` and their two calls in
`Run` (the band gates move to RevealSuite, §11.2).

### 11.2 `RevealSuite.cs` — band gates (append; add `BandChecks(strings, fonts);` and `BandFastPath(strings);` to `Run`)

```csharp
    /// <summary>A bound vertical ItemsView over labelled rows of authored heights; Publish swaps the model (the owner's
    /// commit). The 1:1 successor of ControlsSuite's VirtualDisclosureProbe.</summary>
    sealed class VirtualRevealProbe : Component
    {
        public readonly Signal<int> Count;
        public readonly Signal<int> SourceVersion = new(0);
        public readonly ItemsViewController Controller = new();
        public readonly List<ItemDisclosureDiagnostic> Diagnostics = [];
        public Func<string, ItemDisclosureRange?>? Resolve;
        public string[] Labels;
        public float[] Heights;

        public VirtualRevealProbe(string[] labels, float[] heights) { Labels = labels; Heights = heights; Count = new Signal<int>(labels.Length); }

        public void Publish(string[] labels, float[] heights)
        {
            void Mutate() { Labels = labels; Heights = heights; Count.Value = labels.Length; SourceVersion.Value = SourceVersion.Peek() + 1; }
            if (Context.Runtime is { } runtime) runtime.Batch(Mutate); else Mutate();
        }

        public void Remove(string label)
        {
            int i = Array.IndexOf(Labels, label);
            if (i < 0) return;
            var l = new List<string>(Labels); var h = new List<float>(Heights);
            l.RemoveAt(i); h.RemoveAt(i);
            Publish(l.ToArray(), h.ToArray());
        }

        public ItemDisclosureRange? Range(string key, string label)
        {
            int i = Array.IndexOf(Labels, label);
            return i < 0 ? null : new ItemDisclosureRange(key, i, 1);
        }

        public override Element Render() => Embed.Comp(() => new ItemsView
        {
            ItemCount = Labels.Length,
            ItemCountSignal = Count,
            BoundMode = true,
            RowTemplate = scope => new BoxEl
            {
                Height = Prop.Of(() => { _ = SourceVersion.Value; return scope.Index.Value < Heights.Length ? Heights[scope.Index.Value] : 0f; }),
                Fill = Tok.FillSubtleSecondary,
                Children = [new TextEl("") { Text = Prop.Of(() => { _ = SourceVersion.Value; return scope.Index.Value < Labels.Length ? Labels[scope.Index.Value] : ""; }), Size = 12f }],
            },
            Layout = RepeatLayout.VariableList(32f),
            HasExplicitLayout = true,
            SelectionMode = ItemsSelectionMode.None,
            Selector = SelectorVisual.None,
            Controller = Controller,
            Disclosure = new DisclosureOptions { Version = SourceVersion, ResolveRange = Resolve, Diagnostic = Diagnostics.Add },
            Grow = 1f,
        });
    }

    // (FindText is the Phase 1 helper above — reuse it, do not add a second copy.)
    static void BandChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        string[] five = ["A", "B", "C", "D", "E"];
        float[] fiveH = [28f, 36f, 44f, 32f, 40f];

        // rv.band.1 — collapse keeps the expanded model until rest, commits once, clears the band the frame the rows leave.
        {
            var probe = new VirtualRevealProbe(five, fiveH);
            using var rig = new Rig(strings, fonts, probe, 16.67f, 260f, 220f);
            int settled = 0, commits = 0;
            float DY() => rig.Y(FindText(rig.Scene, strings, rig.Scene.Root, "D"));
            float y0 = DY(), prev = y0, maxStep = 0f;
            probe.Controller.BeginDisclosure(new ItemDisclosureRange("band", 1, 2), ItemDisclosureDirection.Collapse,
                collapseCommit: () => { commits++; probe.Publish(["A", "D", "E"], [28f, 32f, 40f]); }, settled: () => settled++);
            bool monotone = true;
            int pollers = 0;
            for (int i = 0; i < 70; i++)
            {
                rig.Host.RunFrame();
                pollers = Math.Max(pollers, rig.Host.FrameClockPollerCount);
                float y = DY();
                monotone &= y <= prev + 0.01f;
                maxStep = MathF.Max(maxStep, prev - y);
                prev = y;
            }
            bool done = commits == 1 && settled == 1 && probe.Count.Peek() == 3 && !rig.Scene.HasActiveRevealBands;
            Check("rv.band.1 collapse: the rows stay modelled while the band closes (monotone, no step >15%), the commit runs once at rest, and the band clears with no jump",
                monotone && maxStep <= 80f * 0.15f && done && Near(prev, y0 - 80f, 0.6f) && pollers == 0,
                $"y {y0:0.0}→{prev:0.0} maxStep={maxStep:0.00} commits={commits} settled={settled} count={probe.Count.Peek()} bands={rig.Scene.HasActiveRevealBands} pollers={pollers}");
        }

        // rv.band.2 — expand from the inserted model.
        {
            var probe = new VirtualRevealProbe(["A", "D", "E"], [28f, 32f, 40f]);
            using var rig = new Rig(strings, fonts, probe, 16.67f, 260f, 220f);
            int settled = 0;
            float DY() => rig.Y(FindText(rig.Scene, strings, rig.Scene.Root, "D"));
            float y0 = DY();
            probe.Publish(five, fiveH);   // the owner inserts FIRST …
            probe.Controller.BeginDisclosure(new ItemDisclosureRange("band", 1, 2), ItemDisclosureDirection.Expand, settled: () => settled++);
            rig.Host.RunFrame();
            float yCommit = DY(), prev = yCommit;
            bool monotone = true;
            for (int i = 0; i < 70; i++) { rig.Host.RunFrame(); float y = DY(); monotone &= y >= prev - 0.01f; prev = y; }
            Check("rv.band.2 expand: the inserted rows reveal from 0 (the commit frame shows none of them), monotone, and the band clears at rest",
                Near(yCommit, y0, 0.6f) && monotone && Near(prev, y0 + 80f, 0.6f) && settled == 1 && !rig.Scene.HasActiveRevealBands,
                $"y {y0:0.0}→{yCommit:0.0}→{prev:0.0} settled={settled}");
        }

        // rv.band.3 — reverse mid-flight: a closing band reopens from where it stands; its commit never runs.
        {
            var probe = new VirtualRevealProbe(five, fiveH);
            using var rig = new Rig(strings, fonts, probe, 16.67f, 260f, 220f);
            int commits = 0, settled = 0;
            float DY() => rig.Y(FindText(rig.Scene, strings, rig.Scene.Root, "D"));
            float y0 = DY(), prev = y0, maxStep = 0f;
            var range = new ItemDisclosureRange("band", 1, 2);
            probe.Controller.BeginDisclosure(range, ItemDisclosureDirection.Collapse, collapseCommit: () => commits++, settled: () => settled++);
            for (int i = 0; i < 6; i++) { rig.Host.RunFrame(); maxStep = MathF.Max(maxStep, MathF.Abs(DY() - prev)); prev = DY(); }
            probe.Controller.BeginDisclosure(range, ItemDisclosureDirection.Expand, settled: () => settled++);
            for (int i = 0; i < 70; i++) { rig.Host.RunFrame(); maxStep = MathF.Max(maxStep, MathF.Abs(DY() - prev)); prev = DY(); }
            Check("rv.band.3 reverse: a closing band reverses into an expand continuously (no step >15%), its collapse commit never runs, it lands open",
                commits == 0 && settled == 1 && maxStep <= 80f * 0.15f && Near(prev, y0, 0.6f) && probe.Count.Peek() == 5
                && probe.Diagnostics.Exists(static d => d.Kind == ItemDisclosureDiagnosticKind.Reversed),
                $"commits={commits} settled={settled} maxStep={maxStep:0.00} y={prev:0.0}/{y0:0.0}");
        }

        // rv.band.4 — two bands at once, re-found by key after the first commit shifts the indices.
        {
            var probe = new VirtualRevealProbe(["A", "B", "C", "D", "E", "F"], [30f, 30f, 30f, 30f, 30f, 30f]);
            probe.Resolve = key => key == "b1" ? probe.Range(key, "B") : key == "b2" ? probe.Range(key, "E") : null;
            using var rig = new Rig(strings, fonts, probe, 16.67f, 260f, 260f);
            float FY() => rig.Y(FindText(rig.Scene, strings, rig.Scene.Root, "F"));
            float y0 = FY(), prev = y0;
            probe.Controller.BeginDisclosure(new ItemDisclosureRange("b1", 1, 1), ItemDisclosureDirection.Collapse, collapseCommit: () => probe.Remove("B"));
            probe.Controller.BeginDisclosure(new ItemDisclosureRange("b2", 4, 1), ItemDisclosureDirection.Collapse, collapseCommit: () => probe.Remove("E"));
            bool monotone = true;
            for (int i = 0; i < 80; i++) { rig.Host.RunFrame(); float y = FY(); monotone &= y <= prev + 0.01f; prev = y; }
            Check("rv.band.4 concurrency: two collapsing bands run together (monotone follower), both commit, both clear",
                monotone && probe.Count.Peek() == 4 && Near(prev, y0 - 60f, 0.6f) && !rig.Scene.HasActiveRevealBands,
                $"monotone={monotone} count={probe.Count.Peek()} y {y0:0.0}→{prev:0.0} bands={rig.Scene.HasActiveRevealBands}");
        }

        // rv.band.5 — 40 rows closing in a 300-DIP view: the moving edge stays on screen (visible-span clamp).
        {
            var labels = new string[45]; var heights = new float[45];
            for (int i = 0; i < 45; i++) { labels[i] = "r" + i; heights[i] = 30f; }
            var probe = new VirtualRevealProbe(labels, heights);
            using var rig = new Rig(strings, fonts, probe, 16.67f, 260f, 300f);
            var vp = probe.Controller.Viewport;
            float top = rig.Scene.AbsoluteRect(vp).Y;
            rig.Scene.TryGetScroll(vp, out var sc);
            float bottom = top + sc.ViewportH;
            probe.Controller.BeginDisclosure(new ItemDisclosureRange("big", 1, 40), ItemDisclosureDirection.Collapse, collapseCommit: () => { });
            int frames = 0, onScreen = 0;
            for (int i = 0; i < 70; i++)
            {
                rig.Host.RunFrame();
                if (!rig.Host.Animation.TryGetTrackValue(vp, AnimEngine.RevealBandChannel(0), out _) || !rig.Scene.TryGetRevealBand(vp, 0, out var b)) continue;
                frames++;
                rig.Scene.TryGetScroll(vp, out var s2);
                float edge = rig.Scene.AbsoluteRect(s2.ContentNode).Y + b.Top + b.Presented;
                if (edge >= top && edge <= bottom) onScreen++;
            }
            Check("rv.band.5 a 40-row band closing in a 300-DIP view keeps its moving edge on screen for ≥70% of its frames",
                frames > 5 && onScreen >= frames * 0.7f, $"frames={frames} onScreen={onScreen}");
        }

        // rv.band.8 — the TAIL band of a list scrolled to its end (the sidebar collapsing its last section at the bottom).
        // The offset rides the PRESENTED max down while the band closes, and the commit frame (the rows leave the model at
        // the frame start; ItemsView releases the slot only at 6.5) HOLDS it: the committed band adds nothing at 6.3, so the
        // scroll sync sees exactly the laid-out extent, never laid-out minus a phantom band.
        {
            // Every row at the layout's 32-DIP estimate, so realizing rows mid-flight measures nothing new: the only extent
            // change is the band (5 × 32 = 160 DIP).
            const float rowH = 32f, band = 5 * rowH;
            var labels = new string[20]; var heights = new float[20];
            for (int i = 0; i < 20; i++) { labels[i] = "t" + i; heights[i] = rowH; }
            var probe = new VirtualRevealProbe(labels, heights);
            using var rig = new Rig(strings, fonts, probe, 16.67f, 260f, 300f);
            var vp = probe.Controller.Viewport;
            var handle = rig.Host.TryGetScrollHandle(vp)!;
            handle.ScrollTo(handle.MaxOffset, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            for (int i = 0; i < 3; i++) rig.Host.RunFrame();
            rig.Scene.TryGetScroll(vp, out var s0);
            double start = s0.Offset;
            float startMax = 20 * rowH - s0.ViewportH;
            int frame = 0, commitFrame = -1, commits = 0;
            probe.Controller.BeginDisclosure(new ItemDisclosureRange("tail", 15, 5), ItemDisclosureDirection.Collapse,
                collapseCommit: () => { commits++; commitFrame = frame; probe.Publish(labels[..15], heights[..15]); });
            double prev = start, maxStep = 0, beforeCommit = double.NaN, atCommit = double.NaN;
            bool monotone = true;
            for (; frame < 80; frame++)
            {
                rig.Host.RunFrame();
                rig.Scene.TryGetScroll(vp, out var s);
                if (commitFrame == frame) { beforeCommit = prev; atCommit = s.Offset; }
                monotone &= s.Offset <= prev + 0.01;
                maxStep = Math.Max(maxStep, Math.Abs(s.Offset - prev));
                prev = s.Offset;
            }
            bool commitHolds = commitFrame >= 0 && Math.Abs(atCommit - beforeCommit) <= 0.5;
            Check("rv.band.8 a tail band collapsing in a list scrolled to its end: the offset rides the presented max down (monotone, no step >15%), the commit frame holds it, and it lands on the new max",
                startMax > band && Near((float)start, startMax, 1f) && commitHolds && monotone && maxStep <= band * 0.15f
                && Near((float)prev, startMax - band, 1f)
                && commits == 1 && probe.Count.Peek() == 15 && !rig.Scene.HasActiveRevealBands,
                $"offset {start:0.0}→{prev:0.0} commit@{commitFrame} {beforeCommit:0.0}→{atCommit:0.0} maxStep={maxStep:0.00} monotone={monotone} commits={commits} bands={rig.Scene.HasActiveRevealBands}");
        }
    }

    // rv.band.6/7 — the scene-level band API: hit-testing clips the band and maps the shifted suffix; the census balances.
    static void BandFastPath(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 100f, Height = 200f, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Width = 100f,
                    Children =
                    [
                        new BoxEl { Key = "A", Width = 100f, Height = 40f, OnClick = static () => { } },
                        new BoxEl { Key = "B", Width = 100f, Height = 40f, OnClick = static () => { } },
                        new BoxEl { Key = "C", Width = 100f, Height = 40f, OnClick = static () => { } },
                        new BoxEl { Key = "D", Width = 100f, Height = 40f, OnClick = static () => { } },
                        new BoxEl { Key = "E", Width = 100f, Height = 40f, OnClick = static () => { } },
                    ],
                },
            ],
        }, null);
        new FluentGpu.Layout.FlexLayout(scene, fonts).Run(scene.Root);
        var viewport = scene.Root;
        var content = Child(scene, viewport, 0);
        var b = Child(scene, content, 1);
        var c = Child(scene, content, 2);
        var d = Child(scene, content, 3);
        ref ScrollState scroll = ref scene.ScrollRef(viewport);
        scroll.Orientation = 0;
        scroll.ContentNode = content;
        scroll.ItemCount = 5;
        scroll.FirstRealized = 0;

        bool idle = !scene.HasActiveRevealBands;
        bool armed = scene.SetRevealBand(viewport, 0, 1, 2, 40f, 80f, opening: false, presented: 40f) && scene.HasActiveRevealBands;
        var dispatcher = new InputDispatcher(scene);
        var bodyHit = dispatcher.HitTest(new Point2(10f, 50f));
        var suffixHit = dispatcher.HitTest(new Point2(10f, 90f));
        scene.ClearRevealBand(viewport, 0);
        var restingHit = dispatcher.HitTest(new Point2(10f, 90f));
        scene.SetRevealBandPresented(viewport, 0, 10f);   // a late animation write after the clear is ignored
        bool cleared = !scene.HasActiveRevealBands;
        Check("rv.band.6 hit-testing clips a band to its presented height and maps the shifted suffix; a clear restores layout",
            idle && armed && bodyHit == b && suffixHit == d && restingHit == c && cleared,
            $"idle={idle} armed={armed} body={bodyHit == b} suffix={suffixHit == d} resting={restingHit == c} cleared={cleared}");

        var census = new SceneStore();
        var root = census.CreateNode(1);
        census.Root = root;
        var viewportA = census.CreateNode(1); var contentA = census.CreateNode(1);
        var viewportB = census.CreateNode(1); var contentB = census.CreateNode(1);
        census.AppendChild(root, viewportA); census.AppendChild(viewportA, contentA);
        census.AppendChild(root, viewportB); census.AppendChild(viewportB, contentB);
        ref ScrollState scrollA = ref census.ScrollRef(viewportA); scrollA.ContentNode = contentA; scrollA.ItemCount = 1;
        ref ScrollState scrollB = ref census.ScrollRef(viewportB); scrollB.ContentNode = contentB; scrollB.ItemCount = 1;
        bool both = census.SetRevealBand(viewportA, 0, 0, 1, 0f, 10f, false, 0f)
            && census.SetRevealBand(viewportB, 2, 0, 1, 0f, 10f, true, 10f) && census.HasActiveRevealBands;
        census.ClearRevealBand(viewportA, 0);
        bool oneRemains = census.HasActiveRevealBands;
        census.ClearRevealBand(viewportA, 0);
        bool repeatSafe = census.HasActiveRevealBands;
        census.FreeSubtree(viewportB);
        bool freeClears = !census.HasActiveRevealBands;
        Check("rv.band.7 concurrent, repeated-clear and viewport-free band census edges stay balanced",
            both && oneRemains && repeatSafe && freeClears, $"both={both} one={oneRemains} repeat={repeatSafe} free={freeClears}");
    }
```

(`FindScrollNode`/`Child`/`Near` from `Harness.Asserts`; add `using FluentGpu.Controls;` and `using FluentGpu.Input;` to
RevealSuite.cs for `ItemsView`, `RepeatLayout`, `InputDispatcher`.)

### 11.3 Engine.Tests — append to `FlowCursorTests.cs`

```csharp
    [Fact]
    public void ABandClipsItsRowsToThePresentedHeightAndShiftsTheSuffix()
    {
        var bands = new RevealBands();
        bands.Set(0, new RevealBand { First = 1, Count = 2, Top = 40f, Extent = 80f, Presented = 20f, Opening = true });
        var c = FlowCursor.For(default(NodePaint));
        c.SetBands(in bands, 0b1, prefix: 0, firstRealized: 0);
        Assert.True(c.Active);
        Assert.Equal(0f, c.Step(0, 0f, 0f, out float t0, out _));
        Assert.True(float.IsNaN(t0));
        Assert.Equal(0f, c.Step(1, 40f, 0f, out float t1, out float b1));
        Assert.Equal(40f, t1);
        Assert.Equal(60f, b1);
        c.Step(2, 80f, 0f, out _, out _);
        Assert.Equal(-60f, c.Step(3, 120f, 0f, out float t3, out _));
        Assert.True(float.IsNaN(t3));
    }

    [Fact]
    public void TwoBandsComposeInLogicalOrderWhateverTheirSlots()
    {
        var bands = new RevealBands();
        bands.Set(2, new RevealBand { First = 0, Count = 1, Top = 0f, Extent = 30f, Presented = 10f });    // −20
        bands.Set(0, new RevealBand { First = 2, Count = 1, Top = 60f, Extent = 30f, Presented = 0f });    // −30
        var c = FlowCursor.For(default(NodePaint));
        c.SetBands(in bands, 0b101, prefix: 0, firstRealized: 0);
        c.Step(0, 0f, 0f, out _, out _);
        Assert.Equal(-20f, c.Step(1, 30f, 0f, out _, out _));
        Assert.Equal(-20f, c.Step(2, 60f, 0f, out float clipTop, out float clipBottom));
        Assert.Equal(40f, clipTop);
        Assert.Equal(40f, clipBottom);
        Assert.Equal(-50f, c.Step(3, 90f, 0f, out _, out _));
    }
```

(`FlowCursor.For(default(NodePaint))` passes an rvalue to an `in` parameter — allowed.)

New file `src/FluentGpu.Engine.Tests/RevealBandsTests.cs`. It pins that `ScrollState` (with bands) stays comparable by
the default struct equality that `SceneRecordingSnapshot.EqualsForParity` uses:

```csharp
using System;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The reveal-band slots inside ScrollState (docs/plans/smooth-reveal-implementation.md §10.1): plain named fields
/// with explicit equality, so the parity check's default ScrollState comparison works and sees every band field.</summary>
public sealed class RevealBandsTests
{
    [Fact]
    public void ScrollStateWithBandsComparesByValue()
    {
        var a = ScrollState.Default;
        a.Bands.Set(1, new RevealBand { First = 3, Count = 2, Top = 90f, Extent = 64f, Presented = 20f, Opening = true });
        a.BandMask = 0b10;
        var b = a;
        Assert.True(a.Equals(b));                       // never throws (an [InlineArray] field would)
        var band = b.Bands.Get(1);
        band.Presented = 21f;
        b.Bands.Set(1, in band);
        Assert.False(a.Equals(b));                      // a band field difference is a ScrollState difference
    }

    [Fact]
    public void ANaNPresentedEqualsItself()
    {
        var a = new RevealBands();
        a.Set(0, new RevealBand { Count = 1, Extent = 10f, Presented = float.NaN });
        var b = a;
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ACommittedBandStopsPresentingTheFlushItsRowsLeave()
    {
        var bands = new RevealBands();
        bands.Set(0, new RevealBand { First = 15, Count = 5, Top = 480f, Extent = 160f, Presented = 0f, Committed = true, CommitCount = 20 });
        bands.Set(1, new RevealBand { First = 2, Count = 1, Top = 64f, Extent = 32f, Presented = 10f, Opening = true });
        Assert.True(bands.Get(0).Presents(20));                  // rows still modelled: it keeps presenting them at 0
        Assert.False(bands.Get(0).Presents(15));                 // the commit's flush dropped them: no delta, no clip
        Assert.Equal((byte)0b11, bands.PresentingMask(0b11, 20));
        Assert.Equal((byte)0b10, bands.PresentingMask(0b11, 15));
        Assert.True(bands.Get(1).Presents(15));                  // an uncommitted band never stops presenting
    }

    [Fact]
    public void SlotsOutsideTheCapacityThrow()
    {
        var bands = new RevealBands();
        Assert.Throws<ArgumentOutOfRangeException>(() => bands.Get(RevealBands.Capacity));
        Assert.Throws<ArgumentOutOfRangeException>(() => bands.Set(-1, default));
    }
}
```

### 11.4 Canon docs (Phase 2)

- `docs/design/subsystems/backdrop-effects-animation.md` §5.8b (added in Phase 1): append a "Virtual reveal bands"
  paragraph covering up to `RevealBands.Capacity` (4) bands per vertical viewport, one `AnimChannel.RevealBand0..3` row
  each, folded into the content's FlowDelta by `AddBands`, walked by `FlowCursor.SetBands`, the collapse commit at rest
  with a zero-delta handoff (the band is marked committed right before the commit and presents nothing from the flush
  its rows leave the model: `RevealBand.Committed/CommitCount/Presents`, so the commit frame's 6.3 scroll sync never
  subtracts a phantom band), and the above-view snap that shifts the scroll frame. Add "Gates: `rv.band.1`–`rv.band.8`,
  `RevealBandsTests`."
- `docs/design/subsystems/scene-memory.md` §2.7: add `ScrollState.Bands` (`RevealBands`: four named `RevealBand` fields
  with explicit NaN-safe equality, NEVER an `[InlineArray]`, because the parity check compares ScrollState by default
  struct equality) and `ScrollState.BandMask`, one line each.

---

## 12. Phase 2 — the Wavee sidebar (app)

### 12.1 New file `WaveeMusic/src/apps/Wavee/Shell/Sidebar.Disclosures.cs`

```csharp
// The sidebar's in-flight disclosures, by key (fluent-gpu docs/plans/smooth-reveal-implementation.md §12): any number run
// at once, and a toggle of a key already in flight REVERSES it — never queued behind another, never snapped to its end.
// Pure and engine-free so Wavee.Tests pins it (SidebarDisclosuresTests).

namespace Wavee;

/// <summary>The bookkeeping behind PaneView's disclosure choreography: which section/folder keys are opening or closing
/// right now, and the direction a chevron shows while they do.</summary>
public sealed class SidebarDisclosures
{
    /// <summary>One disclosure in flight: its band key ("section:id" / "folder:id"), the section/folder id, and the
    /// direction it is heading.</summary>
    public readonly record struct Entry(string Key, string Id, bool Folder, bool Open);

    /// <summary>What a user toggle means: start a new disclosure, reverse the one in flight, or nothing (already heading there).</summary>
    public enum Toggle : byte { Begin, Reverse, Ignore }

    private readonly List<Entry> _entries = new(4);

    public int Count => _entries.Count;

    /// <summary>Record a toggle of <paramref name="key"/> toward <paramref name="open"/>.</summary>
    public Toggle Start(string key, string id, bool folder, bool open)
    {
        int i = IndexOf(key);
        if (i < 0)
        {
            _entries.Add(new Entry(key, id, folder, open));
            return Toggle.Begin;
        }
        if (_entries[i].Open == open) return Toggle.Ignore;
        _entries[i] = _entries[i] with { Open = open };
        return Toggle.Reverse;
    }

    /// <summary>The disclosure under <paramref name="key"/> came to rest: forget it. False for a key nothing runs under.</summary>
    public bool Settled(string key)
    {
        int i = IndexOf(key);
        if (i < 0) return false;
        _entries.RemoveAt(i);
        return true;
    }

    public bool TryGet(string key, out Entry entry)
    {
        int i = IndexOf(key);
        entry = i < 0 ? default : _entries[i];
        return i >= 0;
    }

    /// <summary>The direction a chevron shows: an in-flight disclosure's, else <paramref name="fallback"/> (the persisted state).</summary>
    public bool IsOpen(string id, bool folder, bool fallback)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Folder == folder && string.Equals(_entries[i].Id, id, StringComparison.Ordinal)) return _entries[i].Open;
        return fallback;
    }

    private int IndexOf(string key)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (string.Equals(_entries[i].Key, key, StringComparison.Ordinal)) return i;
        return -1;
    }
}
```

(The app has `ImplicitUsings`; if `List<>` is not in scope add `using System.Collections.Generic;`.)

### 12.2 `Shell/Sidebar.UI.cs` (PaneView) — the choreography rewrite

1. Fields (`// ── disclosure ──`): delete `_activeDisclosureKey`, `_activeDisclosureId`, `_activeDisclosureIsFolder`,
   `_activeDisclosureOpen`, `_pendingExpandSection`, `_pendingExpandFolder`, `_activeDisclosureBand`, `_queuedDisclosure`;
   add `readonly SidebarDisclosures _disclosures = new();` and `Func<string, ItemDisclosureRange?>? _resolveDisclosure;`.
   Keep `_post`, `_flushPrefsCommit`, `_disclosureLog`.
2. Render: delete the `UseLayoutEffect` that settled a prepared expansion / ran `_queuedDisclosure` (anchor:
   `if (_activeDisclosureKey is { } active && _activeDisclosureOpen`). Keep `int rows = _rowCount.Value;` (it is read
   later). The `Disclosure = new DisclosureOptions { … }` initializer becomes:

```csharp
                Disclosure = new DisclosureOptions
                {
                    Version = _planVersion,
                    ResolveRange = _resolveDisclosure ??= ResolveDisclosureRange,
                    Diagnostic = _disclosureLog ??= LogDisclosure,
                },
```

3. `TryPublishStage`: delete the `preparedExpansion` lines and `if (_activeDisclosureKey is not null && !preparedExpansion) return;`
   (a collapse's rows stay in the DOCUMENT until its commit, so a publish mid-collapse still carries them; bands re-find
   their rows by key). The freeze line becomes
   `else if (_disclosures.Count == 0 && _deferredStage.TryHold(Drag.LiveRootlistDrag(), stage)) return;` and update the
   comment above it ("exempt while any disclosure is in flight").
4. Replace the whole `// ── disclosure choreography ──` region (from its header comment through `DisclosureSettled`,
   i.e. `PendingExpandRange`, `OnExpandStarted`, `OnExpandSettled`, `DisclosureOpen`, `TrySectionBodyRange`,
   `TryFolderDescendantRange`, `StartDisclosure`, `DisclosureSettled`) with:

```csharp
        // ── disclosure choreography ────────────────────────────────────────────────────────────────────────────────
        // Every section/folder opens and closes on the engine's reveal band (fluent-gpu smooth-reveal plan §10): the
        // presented height springs under MotionTok.Reveal while the rows below ride it, any number at once, and a click on
        // a key already in flight REVERSES it from where it stands. Expand: the rows enter the document and the plan first
        // (published on the click frame), then the band reveals them. Collapse: the rows stay until the band rests, then
        // the preference write commits. Reduced motion is the engine's (a value at the seed) — never branched on here.

        internal bool DisclosureOpen(string id, bool folder, bool fallback) => _disclosures.IsOpen(id, folder, fallback);

        bool TrySectionBodyRange(string sectionId, out ItemDisclosureRange range)
        {
            if (!SidebarRowGeometry.TrySectionBodyRange(Plan.Rows, sectionId, out int first, out int count))
            {
                range = default;
                return false;
            }
            range = new ItemDisclosureRange("section:" + sectionId, first, count);
            return true;
        }

        bool TryFolderDescendantRange(string folderId, out ItemDisclosureRange range)
        {
            if (!SidebarRowGeometry.TryFolderDescendantRange(Plan.Rows, Plan.Entries, folderId, out int first, out int count))
            {
                range = default;
                return false;
            }
            range = new ItemDisclosureRange("folder:" + folderId, first, count);
            return true;
        }

        bool TryRange(string id, bool folder, out ItemDisclosureRange range)
            => folder ? TryFolderDescendantRange(id, out range) : TrySectionBodyRange(id, out range);

        /// <summary>The band's rows in the PUBLISHED plan, by key — null once a committed collapse removed them.</summary>
        ItemDisclosureRange? ResolveDisclosureRange(string key)
        {
            if (key.StartsWith("section:", StringComparison.Ordinal))
                return TrySectionBodyRange(key.Substring("section:".Length), out var s) ? s : null;
            if (key.StartsWith("folder:", StringComparison.Ordinal))
                return TryFolderDescendantRange(key.Substring("folder:".Length), out var f) ? f : null;
            return null;
        }

        void StartDisclosure(string key, string id, bool folder, bool open, Action commit)
        {
            var toggle = _disclosures.Start(key, id, folder, open);
            if (toggle == SidebarDisclosures.Toggle.Ignore) return;
            if (toggle == SidebarDisclosures.Toggle.Reverse)
            {
                // Mid-flight reversal. Reopening a closing band: its collapse never committed (the document still holds the
                // rows), so it springs back open with no commit. Closing an opening band: the open already committed, so
                // the close carries the commit like any collapse.
                if (TryRange(id, folder, out var live))
                    _listController.BeginDisclosure(live, open ? ItemDisclosureDirection.Expand : ItemDisclosureDirection.Collapse,
                        collapseCommit: open ? null : WithPrefsCommit(commit), settled: () => DisclosureSettled(key));
                else
                {
                    if (!open) { commit(); SchedulePrefsCommit(); }
                    DisclosureSettled(key);
                }
            }
            else if (open)
            {
                // PUBLISH ON THE CLICK FRAME (input phase ⇒ a forward write), through any drag freeze: the band arms against
                // the inserted rows before the first expanded paint — the chevron and the rows move together.
                commit();
                SchedulePrefsCommit();
                _publishThroughFreeze = true;
                RepublishNow();
                if (TryRange(id, folder, out var opened))
                    _listController.BeginDisclosure(opened, ItemDisclosureDirection.Expand, settled: () => DisclosureSettled(key));
                else DisclosureSettled(key);   // nothing to disclose (an empty section)
            }
            else if (TryRange(id, folder, out var closing))
                _listController.BeginDisclosure(closing, ItemDisclosureDirection.Collapse,
                    collapseCommit: WithPrefsCommit(commit), settled: () => DisclosureSettled(key));
            else
            {
                commit();
                SchedulePrefsCommit();
                DisclosureSettled(key);
            }
            _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
            BumpDisclosureEpochs(id, folder, TryRange(id, folder, out var bumped) ? bumped : null);
        }

        void DisclosureSettled(string key)
        {
            if (!_disclosures.TryGet(key, out var entry)) return;
            _disclosures.Settled(key);
            _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
            BumpDisclosureEpochs(entry.Id, entry.Folder, TryRange(entry.Id, entry.Folder, out var band) ? band : null);
        }
```

5. `LogDisclosure`: delete `if (d.Kind is ItemDisclosureDiagnosticKind.Progress) return;` (the kind no longer exists;
   nothing per-frame is traced any more) and its comment.
6. `BumpDisclosureEpochs`, `SectionHeaderIndexOf`, `RepublishNow`, `WithPrefsCommit`, `SchedulePrefsCommit`,
   `ToggleSection`, `ActivateFolder` stay as they are.

### 12.3 `Shell/Sidebar.UI.Rows.cs` — the chevron rides the reveal spring

In `Chevron`: both `MotionTokenId.DisclosureChevron` → `MotionTokenId.Reveal`. Doc: replace "ONE glyph whose Rotation
rides `MotionTokenId.DisclosureChevron` (167 ms, cubic-bezier(.167,.167,0,1))" with "ONE glyph whose Rotation rides
`MotionTokenId.Reveal` — the SAME spring as the band it discloses, so the glyph and the rows land together". Add an
optional accent (used by the track table in Phase 3):

```csharp
        readonly bool _accent;

        Chevron(Func<bool> open, Func<int>? identity, string glyph, float size, float openDeg, bool accent = false)
        {
            _open = open; _identity = identity; _glyph = glyph; _size = size; _openDeg = openDeg; _accent = accent;
        }

        /// <summary>A DISCLOSURE chevron (a folder row / tree row): ChevronRight at rest, rotated 90° when expanded.
        /// <paramref name="accentWhenOpen"/> inks it with the accent while open (the track table's expand cell).</summary>
        public static Element Disclosure(Func<bool> open, float size = 10f, Func<int>? identity = null, bool accentWhenOpen = false)
            => Embed.Comp(() => new Chevron(open, identity, Icons.ChevronRight, size, 90f, accentWhenOpen));
```

and its glyph child becomes
`Children = [Icon(_glyph, _size) with { Color = _accent ? Prop.Of(() => _open() ? Tok.AccentTextPrimary : Tok.TextSecondary) : (Prop<ColorF>)Tok.TextTertiary }],`

### 12.4 `WaveeMusic/.claude/skills/wavee-sidebar/architecture.md`

Exemption 1 of the publish freeze: "**A live disclosure** (`_activeDisclosureKey is not null`)" → "**Any disclosure in
flight** (`_disclosures.Count != 0`)". And add one sentence after the list: "Disclosures run concurrently on the
engine's reveal bands; a click on a key in flight reverses it (`SidebarDisclosures`, fluent-gpu
`docs/plans/smooth-reveal-implementation.md` §10/§12)."

### 12.5 Wavee.Tests — new `WaveeMusic/src/apps/Wavee.Tests/SidebarDisclosuresTests.cs`

```csharp
// ── Wavee.Tests/SidebarDisclosuresTests.cs — the sidebar's concurrent, reversible disclosure bookkeeping ───────────────
//
// Pure: no scope, no engine. Pins Shell/Sidebar.Disclosures.cs (fluent-gpu smooth-reveal plan §12).

using Xunit;

namespace Wavee.Tests;

public sealed class SidebarDisclosuresTests
{
    [Fact]
    public void AFirstToggleBegins()
    {
        var d = new SidebarDisclosures();
        Assert.Equal(SidebarDisclosures.Toggle.Begin, d.Start("section:pinned", "pinned", folder: false, open: false));
        Assert.Equal(1, d.Count);
        Assert.False(d.IsOpen("pinned", folder: false, fallback: true));
    }

    [Fact]
    public void TheSameDirectionAgainIsIgnored()
    {
        var d = new SidebarDisclosures();
        d.Start("folder:f1", "f1", folder: true, open: true);
        Assert.Equal(SidebarDisclosures.Toggle.Ignore, d.Start("folder:f1", "f1", folder: true, open: true));
        Assert.Equal(1, d.Count);
    }

    [Fact]
    public void TheOppositeDirectionReversesInPlace()
    {
        var d = new SidebarDisclosures();
        d.Start("section:recent", "recent", folder: false, open: false);
        Assert.Equal(SidebarDisclosures.Toggle.Reverse, d.Start("section:recent", "recent", folder: false, open: true));
        Assert.Equal(1, d.Count);
        Assert.True(d.IsOpen("recent", folder: false, fallback: false));
    }

    [Fact]
    public void DifferentKeysRunConcurrently()
    {
        var d = new SidebarDisclosures();
        Assert.Equal(SidebarDisclosures.Toggle.Begin, d.Start("section:pinned", "pinned", false, open: false));
        Assert.Equal(SidebarDisclosures.Toggle.Begin, d.Start("section:library", "library", false, open: true));
        Assert.Equal(SidebarDisclosures.Toggle.Begin, d.Start("folder:f1", "f1", true, open: true));
        Assert.Equal(3, d.Count);
        Assert.False(d.IsOpen("pinned", false, fallback: true));
        Assert.True(d.IsOpen("library", false, fallback: false));
    }

    [Fact]
    public void ASectionAndAFolderWithTheSameIdAreDistinct()
    {
        var d = new SidebarDisclosures();
        d.Start("section:x", "x", folder: false, open: true);
        Assert.True(d.IsOpen("x", folder: false, fallback: false));
        Assert.False(d.IsOpen("x", folder: true, fallback: false));   // the folder falls back
    }

    [Fact]
    public void SettledForgetsTheKeyAndALateSettleIsANoOp()
    {
        var d = new SidebarDisclosures();
        d.Start("section:pinned", "pinned", false, open: false);
        Assert.True(d.Settled("section:pinned"));
        Assert.Equal(0, d.Count);
        Assert.False(d.Settled("section:pinned"));
        Assert.True(d.IsOpen("pinned", false, fallback: true));      // back to the persisted state
        Assert.False(d.TryGet("section:pinned", out _));
    }
}
```

---

## 13. Phase 3 — the app call sites

### 13.1 `WaveeMusic/src/apps/Wavee/Platform/Design.cs` — the shared specs

Directly after the `Motion` class (before `public static bool Reduced => …`):

```csharp
    /// <summary>THE disclosure motion (fluent-gpu docs/plans/smooth-reveal-implementation.md): layout lands once, the
    /// presented height springs under <c>MotionTok.Reveal</c> — critically damped, the same both ways, interruptible from
    /// where it stands — and everything below rides it in lockstep. A clip reveal, never a fade: nothing pops and nothing
    /// re-lays out per frame. Use these, never SizeMode.Reflow, for anything that opens and closes.</summary>
    public static class Reveal
    {
        /// <summary>An inline drawer that MOUNTS to open and unmounts to close (a list row's details, a reply thread): the
        /// edge wipes over content that stays put. A reopen mid-close continues from where the closing copy stands.</summary>
        public static readonly LayoutTransition Drawer = new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(),
            Size: SizeMode.FlowReveal, Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true),
            Anchor: SizeAnchor.Leading, SuppressDescendantTransitions: true);

        /// <summary>A card body that unfolds: the content trails the moving edge by a damped share of what is still hidden.</summary>
        public static readonly LayoutTransition Card = Drawer with { Anchor = SizeAnchor.Parallax };

        /// <summary>A box whose content height changes in place ("Show all", a wrapping line): the new height reveals.</summary>
        public static readonly LayoutTransition Resize = new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(),
            Size: SizeMode.FlowReveal);
    }
```

### 13.2 Track table — `Entities/Track.Table.cs`

1. Delete `s_drawerReveal`, `s_drawerPresence` and their comment. `s_checkShift`:
   `TransitionDynamics.Tween(MotionTok.DisclosureExpand.DurationMs, Easing.FluentDecelerate)` →
   `TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate)`.
2. TableHost fields, next to `_expanded`:

```csharp
        // The row whose drawer is CLOSING (still presented under it while its exit reveal runs) — its bottom corners stay
        // square until the engine reports the orphan at rest. "" = none. Plus the open drawer's node (captured at realize).
        readonly Signal<string> _drawerTail = new("");
        NodeHandle _drawerNode;
        string _drawerNodeKey = "";
```

3. `ToggleExpanded` and helpers (replace the method):

```csharp
        internal void ToggleExpanded(string rowKey, Track t)
        {
            string previous = _expanded.Peek();
            bool opening = !string.Equals(previous, rowKey, StringComparison.Ordinal);
            _expanded.Value = opening ? rowKey : "";
            if (opening && t.IsValid) Entities.Ensure(t, TrackFields.All);   // the drawer states EVERY fact
            if (previous.Length > 0) HoldDrawerTail(previous);
            CorrectDrawerExtents(previous, opening ? rowKey : "");
        }

        // A closing drawer is still PRESENTED under its row while its exit reveal runs, so the row keeps square bottom
        // corners until the engine reports the orphan at rest (WhenSettled on the node captured at realize). The engine
        // also fires it when that node dies (a recycle onto another track, a reopen reclaiming the orphan), so the tail
        // never sticks and the registration never leaks.
        void HoldDrawerTail(string key)
        {
            var node = _drawerNode;
            if (Context.Anim is not { } anim || Context.Scene is not { } scene || node.IsNull || !scene.IsLive(node)
                || !string.Equals(_drawerNodeKey, key, StringComparison.Ordinal)) return;
            _drawerTail.Value = key;
            anim.WhenSettled(node, AnimChannel.RevealExtent, () =>
            {
                anim.WhenSettled(node, AnimChannel.RevealExtent, null);
                if (string.Equals(_drawerTail.Peek(), key, StringComparison.Ordinal)) _drawerTail.Value = "";
            });
        }

        // Item 9 (DrawerExtentRule): a toggle changes its row's real extent, but the measured table only learns a REALIZED
        // row's new height from layout — a closing row scrolled out of the window would keep its open height. Write the
        // closing leg here; the opening row is realized (the user just clicked it) and measures itself.
        void CorrectDrawerExtents(string previous, string next)
        {
            Span<DrawerExtentWrite> writes = stackalloc DrawerExtentWrite[2];
            int n = DrawerExtentRule.For(previous, next, DisplayOfKey, ShapeValue.RowH, 0f, writes);
            for (int i = 0; i < n; i++)
            {
                int index = TrackStart + writes[i].Index;
                if (!_listCtl.IsItemRealized(index)) _listCtl.CorrectMeasuredExtent(index, writes[i].Extent);
            }
        }

        int DisplayOfKey(string key)
        {
            int count = _visible.Peek();
            for (int d = 0; d < count; d++)
                if (MembershipDiff.RowKeyMatches(key, DisplayTrack(d), ItemIdAt(d), d)) return d;
            return -1;
        }

        internal bool DrawerTail(Track t, int display)
        {
            string key = _drawerTail.Value;
            return key.Length > 0 && t.IsValid && MembershipDiff.RowKeyMatches(key, t, ItemIdAt(display), display);
        }
```

4. `DrawerBox`: the outer box `Animate = s_drawerReveal` → `Animate = Design.Reveal.Drawer`, and add
   `OnRealized = h => { _drawerNode = h; _drawerNodeKey = rowKey; },`. The inner "drawer-presence" box: delete
   `Animate = s_drawerPresence,` (it stays as the padded column; no fade, no drop). Update the comment above the specs
   in §8 of the file: "ONE spec on the clip box (Design.Reveal.Drawer); the presence box is plain padding."
5. `Skin` doc + param: the `open` parameter now means "square the bottom corners" — rename it to `squared` in the
   signature and the one `Corners =` use, and update the comment to "Bottom corners square while THIS row's drawer is
   open or still closing under it".
6. `TableSlot.Render`: after `bool isOpen = open.Value;` add
   `var tail = UseComputed(() => _host.DrawerTail(_trackItem!.Value, _scope.Index.Value - _start));` and pass
   `isOpen || tail.Value` as the `squared` argument to `_host.Skin(…)`. (Hook order is unconditional: both computeds run
   every render.)

`Entities/Track.UI.Bound.cs` `BoundExpandCell`: replace the `Glyph(…)` child with
`Sidebar.Chevron.Disclosure(() => r.P.Open, size: 12f, identity: () => r.P.Track.Slot, accentWhenOpen: true),`
and the doc: "the chevron ROTATES (ChevronRight → down) on the reveal spring with the drawer, inked accent while open;
a recycle onto another track re-seeds it without spinning".

### 13.3 Recents — `Entities/Recents.UI.cs`

Delete `DrawerReveal`, `DrawerPresence`, `ChildReveal` (and their comments). In `Drawer(...)`: the member boxes drop
`Animate = ChildReveal with { DelayMs = Design.Entrance.DelayMs(ordinal) },` (the rows are revealed by the clip, not
faded in one by one — `ordinal` stays used for the `ChildRow` call); the outer box `Animate = DrawerReveal` →
`Animate = Design.Reveal.Drawer`; the inner presence box drops `Animate = DrawerPresence`. Header comment of the section:
"ONE spec on the clip box (Design.Reveal.Drawer): the presented height springs, the rows below ride it; the inner box is
plain structure." (`Recents.Page.cs` already corrects the collapsed row's extent — unchanged.)

### 13.4 Discography — `Entities/Artist.Discography.cs`

Delete `s_drawerResize` and `s_drawerPresence`; the "disco-drawer" box `Animate = s_drawerResize` →
`Animate = Design.Reveal.Drawer` (its declared `Height = _verdict.SlotHeight` stays: a declared height is a flow
boundary for what is inside, and an album switch that changes the slot height reveals the difference); the inner ZStack
drops `Animate = s_drawerPresence`. The facet header `Expander` is migrated by the engine (Phase 1) — no change.

### 13.5 Blend card — `Entities/User.Facts.UI.cs` (`BlendCardHost`)

- Delete `s_bodyReveal`. `s_tailBarReveal` / `s_tailRowReveal`: `MotionTok.DisclosureExpand.ToDynamics()` →
  `MotionTok.Reveal.ToDynamics()` (their own entrance choreography inside the body stays).
- Delete the `_shown` and `_host` fields, `_shown = UseSignal(false);`, `_host = UseRef<NodeHandle>(default);`,
  `bool showBody = _shown.Value;`, `bool closing = showBody && !isOpen;`, the `var hostRef = _host;` line, the
  `if (closing) { … BlendCollapseWatcher … }` block, and the `BlendCollapseWatcher` class.
- The body host becomes (an always-present plain column; the BODY is the FlowReveal entrant, so open = mount and
  reveal, close = an exit reveal of the same box):

```csharp
            var host = new BoxEl
            {
                Key = "blend-body-host", Direction = 1, MinWidth = 0f,
                Children = isOpen && hasTail
                    ? [new BoxEl { Key = "blend-body", Direction = 1, MinWidth = 0f, Animate = Design.Reveal.Card,
                                   Children = [Body(in tail, _tailLabels!, culture, in filters)] }]
                    : [],
            };
```

- `Toggle()`: `if (_open is null) return; _open.Value = !_open.Peek();` (delete the `_shown` lines and comment).
- Class doc: "a disclosure that opens the tail IN PLACE at full width — the Card reveal (Design.Reveal.Card): the
  presented height springs and the cards below ride it."

### 13.6 The three un-animated sites

`Screens/LogsPage.UI.cs` `LogRow` — the row is ALWAYS the keyed column, so expanding only adds the details child:

```csharp
            var line = new BoxEl
            {
                // … every existing property unchanged …
                Animate = Design.Reveal.Resize,   // the message wraps when expanded: the taller line reveals, never snaps
            }.Interactive(Interaction.ListRow);
            Element[] children = [line];
            if (expanded)
            {
                // The details: Fields (only when non-empty), Exception (only when present), and the meta line (always).
                var detail = new List<Element>(3);
                string fieldText = LogView.FieldText(e.Fields);
                if (fieldText.Length > 0) detail.Add(DetailSection(Loc.Get(Strings.Logs.Fields), fieldText));
                if (e.Exception is { Length: > 0 } ex) detail.Add(DetailSection(Loc.Get(Strings.Logs.Exception), ex));
                detail.Add(Design.Type.MicroMeta(LogView.MetaLine(in e)) with { Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Margin = new Edges4(44f, 0f, 0f, 0f) });
                children = [line, new BoxEl
                {
                    Key = "details", Direction = 1, Gap = 4f, Padding = new Edges4(0f, 4f, 0f, Spacing.S),
                    Animate = Design.Reveal.Drawer, Children = detail.ToArray(),
                }];
            }
            return new BoxEl { Key = "logs:row:" + seq.ToString(CultureInfo.InvariantCulture), Direction = 1, Children = children };
```

(replaces `if (!expanded) return line;` through the old `return new BoxEl { … Children = detail.ToArray() };`.)

`Entities/Episode.Discussion.cs`:
- `ReplyThread(Element content)` → add `Key = "replies", Animate = Design.Reveal.Drawer,` to its BoxEl.
- `RepliesDisclosure`: delete `bool open = expanded.Value;` and replace
  `row.Add(Icon(open ? Icons.ChevronUp : Icons.ChevronDown, 12, Tok.TextTertiary));` with
  `row.Add(Sidebar.Chevron.Section(() => expanded.Value, size: 12f));   // rotates with the thread's reveal`.

`Entities/Album.UI.cs` `StackHost.Render`: the rows box becomes
`new BoxEl { Direction = 1, Gap = Spacing.XS, Animate = Design.Reveal.Resize, Children = rows }` (comment: "Show all"
lengthens it in place — the new height reveals).

### 13.7 Token swaps — `Entities/Track.Table.Chrome.cs`

`s_headerShift`: `TransitionDynamics.Tween(MotionTok.DisclosureExpand.DurationMs, Easing.FluentDecelerate)` →
`TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate)`. (The search box width stays SizeMode.Reflow:
it is horizontal, and FlowReveal is vertical-only. The filter flyout's sections are engine Expanders — migrated.)

### 13.8 Wavee.Tests — new `WaveeMusic/src/apps/Wavee.Tests/DrawerExtentRuleTests.cs`

```csharp
// ── Wavee.Tests/DrawerExtentRuleTests.cs — the drawer toggle's measured-extent writes (Entities/Track.Rules.cs) ─────────
//
// Pure: the decision TableHost.CorrectDrawerExtents applies on every toggle (fluent-gpu smooth-reveal plan §13.2).

using System;
using Xunit;

namespace Wavee.Tests;

public sealed class DrawerExtentRuleTests
{
    static int Display(string key) => key switch { "a" => 3, "b" => 7, _ => -1 };

    [Fact]
    public void OpeningWithNoPreviousDrawerWritesNothingUntilTheDrawerIsMeasured()
    {
        Span<DrawerExtentWrite> into = stackalloc DrawerExtentWrite[2];
        Assert.Equal(0, DrawerExtentRule.For("", "a", Display, 40f, 0f, into));
    }

    [Fact]
    public void ClosingWritesTheRowHeightBack()
    {
        Span<DrawerExtentWrite> into = stackalloc DrawerExtentWrite[2];
        Assert.Equal(1, DrawerExtentRule.For("a", "", Display, 40f, 0f, into));
        Assert.Equal(new DrawerExtentWrite(3, 40f), into[0]);
    }

    [Fact]
    public void ASwitchClosesTheOldRowFirstThenOpensTheNewOne()
    {
        Span<DrawerExtentWrite> into = stackalloc DrawerExtentWrite[2];
        Assert.Equal(2, DrawerExtentRule.For("a", "b", Display, 40f, 120f, into));
        Assert.Equal(new DrawerExtentWrite(3, 40f), into[0]);
        Assert.Equal(new DrawerExtentWrite(7, 160f), into[1]);
    }

    [Fact]
    public void AnUnchangedKeyAFilteredOutRowOrAnUnknownRowHeightWritesNothing()
    {
        Span<DrawerExtentWrite> into = stackalloc DrawerExtentWrite[2];
        Assert.Equal(0, DrawerExtentRule.For("a", "a", Display, 40f, 120f, into));
        Assert.Equal(0, DrawerExtentRule.For("gone", "", Display, 40f, 0f, into));
        Assert.Equal(0, DrawerExtentRule.For("a", "", Display, 0f, 0f, into));
    }
}
```

### 13.9 Engine — delete the WinUI disclosure tokens (`fluent-gpu`)

- `src/FluentGpu.Engine/Animation/MotionTok.cs`: delete `DisclosureExpand, DisclosureCollapse, DisclosureChevron,` from
  `MotionTokenId` (keep the line's other members), their three `Get` arms and their three accessors.
- `src/FluentGpu.Engine/Foundation/Easing.cs`: delete the enum members `FluentDisclosureCollapse` and
  `FluentDisclosureChevron` (and their doc lines) and their two arms in the evaluation switch. (Their only users were the
  deleted tokens; the engine Expander stopped using its private bezier in Phase 1.)

---

## 14. Gates per phase (what the orchestrator runs)

Every phase: engine `dotnet build src/FluentGpu.slnx` Debug + Release, `dotnet run --project src/FluentGpu.VerticalSlice`
("ALL CHECKS PASSED"), `dotnet test src/FluentGpu.Engine.Tests` Debug + Release; app `dotnet build Wavee.slnx` Debug +
Release and `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` Debug + Release; `docs/design/check-canon.ps1` after
Phases 1 and 2. Every existing VerticalSlice gate stays green as well. The ones nearest this change: ScrollSuite (offset
clamps: layout's clamp is deferred only on flow-reveal frames), ControlsSuite cp3.* (Expander), AnimSuite 23r.*
(Reflow), and the snapshot parity tests in Engine.Tests (`ScrollEqual` over a ScrollState that now carries bands).

| Gate | Phase | Pins |
|---|---|---|
| rv.1 / rv.2 | 1 | no layout, no component render, zero hot-phase alloc on every reveal tick |
| rv.3 (8.33 + 16.67 ms) | 1 | commit frame holds; first advance ≤2 % / ≤5 %; no step >8 % / >15 %; lands on layout |
| rv.4 | 1 | expand/collapse mirror within 1 px |
| rv.5 | 1 | rigid followers, parent presented bottom = edge, settle frame = layout, flow columns at rest |
| rv.6 | 1 | visible-span clamp: edge on screen ≥70 % of frames |
| rv.7 | 1 | reverse at ~50 % continuous; toggle storm ends at rest |
| rv.8a/b | 1 | concurrency sums; nesting COMBINES on open (seed frame holds, monotone — the bound, never a sum) |
| rv.8c.1/2 | 1 | nesting on CLOSE: an outer closing over an open inner, and both closing together — commit frame holds, mirror of the open within 1 px, monotone, lands on layout (the bound is the content, never the laid-out height) |
| rv.9 | 1 | hit-test at the presented rect |
| rv.10a | 1 | end-of-scroller collapse: the commit frame holds the offset (layout's clamp deferred, 6.3 sees the presented extent), then it rides the edge down monotonically |
| rv.10b | 1 | a reveal wholly above the viewport snaps AND anchors: the content in view does not move (close + reopen), the offset shifts by the change |
| rv.11 | 1 | recycle suppression (enter + exit) |
| rv.12 | 1 | async growth retargets |
| rv.13 | 1 | reduced motion snaps |
| rv.14 | 1 | settle always fires: an Expander closed under suppressed projections (no row) still unmounts within 2 frames, no poller; it reopens with a reveal |
| rv.15 | 1 | a settle callback on a freed node runs once and is dropped (no stuck drawer tail, no leak) |
| rv.16 | 1 | a Grow child of a scroll content is not a flow boundary: the content's presented extent tracks a reveal inside it |
| cp3.a/b/acr0b/acr1-3, W1-P3.a | 1 | the migrated Expander (presented geometry, Parallax, no pollers) |
| RevealPlanTests, FlowCursorTests | 1 (+2) | clamp, above-view, parallax, spring shape; cursor column/orphan (+bands) |
| RevealBandsTests | 2 | ScrollState with bands compares by value (no InlineArray throw), NaN-safe, slot bounds; a committed band stops presenting once the count moves (`Presents`, `PresentingMask`) |
| rv.band.1–7 | 2 | band collapse/expand/reverse/concurrency/visible span/hit-test/census |
| rv.band.8 | 2 | a tail band collapsing in a list scrolled to its end: offset rides the presented max down monotonically, the COMMIT FRAME holds it (the committed band adds nothing at 6.3), lands on the new max |
| SidebarDisclosuresTests | 2 | begin / reverse / ignore / concurrent / settled |
| DrawerExtentRuleTests | 3 | the drawer toggle's extent writes |
| AnimSuite 23r.* | all | UNCHANGED: SizeMode.Reflow remains (search box width, Skel regions) and keeps its gates |

---

## 15. Risks the orchestrator should watch in the live app (not gate-able headless)

- Retained tiles: a reveal re-records its scroll content every tick (as the old band did); a slow device may show the
  cost on the sidebar's 34-row band. If it does, the follow-up is a tail-slice translate (report §5) — not in this plan.
- Sticky headers inside a revealing sidebar band are shifted with their rows (as before).
- Focus: a collapse leaves keyboard focus on a row that is still modelled until the commit; the commit's removal moves
  focus as any removal does.
- Column parents with `Justify` center/end are approximated as start-justified by the flow pass (none of the migrated
  sites use them).
- A window resize that changes a revealing node's laid-out height mid-flight (text rewrap) keeps the row's old target.
  Resizes skip the FLIP capture, so 6.3 sees no change. The node lands on its new height when the row frees: one small
  jump, only during a live resize. A follow-up could retarget live rows whose layout moved, keyed on the seeded target.
- The nesting bound (§4.1) is continuous whenever the outer's spring stays within extents its content had. One case
  breaks that: a Resize whose content was REMOVED outright (no exit reveal) shrinking WHILE an inner reveal runs inside
  it. When the inner settles, the bound lifts and the outer can step out to its own spring. None of the migrated sites
  nest a reveal inside such a Resize: the Logs `line`, the Album rows box and the Discography slot hold plain content.
  (The facet Expander around the Discography grid keeps `AnimateContentResize = false`, so outside a toggle it carries no
  Size channel and is not a reveal.) Keep it that way, or give the removed content its own Drawer exit.
- Scroll anchoring applies only to a reveal WHOLLY above the view. A region straddling the top edge animates, and the
  content below it moves (the user is interacting with that region; this matches browsers' anchor-node choice).
