# Render-Thread Animation — Design & Landing Plan

Scoping/design doc (**not canon**). The canonical threading model is
[`design/subsystems/threading-render-seam.md`](../design/subsystems/threading-render-seam.md); the canonical animation
model is [`design/subsystems/backdrop-effects-animation.md`](../design/subsystems/backdrop-effects-animation.md) §5 as
amended by [`animation-engine-rework-design.md`](./animation-engine-rework-design.md). This doc sizes one question the
owner asked — *can motion survive a busy UI thread the way a compositor's animations do?* — states the as-built facts
with `file:line`, evaluates the two candidate answers against the seam's binding contracts, recommends an order, and
lays out the steps with their gates. It changes no contract by itself; §3.15 lists every canon line an implementation
would have to amend.

Sibling/precedent docs: [`render-thread-seam-landing-plan.md`](./render-thread-seam-landing-plan.md) (the Cut A / Cut B
decision this doc re-opens), [`butter-smooth-resize-v2.md`](./butter-smooth-resize-v2.md),
[`generic-hookable-scroll-engine-design.md`](./generic-hookable-scroll-engine-design.md) (the slab idiom).

---

## 0. The one sentence, and the one honest caveat

**Every animated value in the engine is produced by a UI-thread call (`AppHost.cs:3349`) and consumed by a UI-thread
record (`AppHost.cs:3514`), so any UI-thread work — engine or app — stops motion for its whole duration; making motion
survive that means moving the compositor-channel rows, and the pixel production that reads them, behind the publish
seam onto the render thread's own clock.**

The caveat, stated before the design so it cannot be read as a promise: this buys **decoupled, not invincible.** A
sustained GPU stall still bounds back through the publisher's backpressure (`threading-render-seam.md` §11); a reflow
is layout and layout is the UI thread; blur is a render-target channel whose cost scales with the layer, not with the
number of animated rows. What it does buy is that a 200 ms app-side callback on the UI thread stops *producing new
scene content* without stopping *motion already in flight* — which is the complaint.

---

## 1. Problem statement — the as-built pipeline, cited

### 1.1 The frame is one UI-thread call

`AppHost.RunFrame` (`AppHost.cs:2617`) binds the UI role (`:2620-2622`), builds the one frame clock (`:2630-2643`),
pumps and dispatches input, applies the compositor-tick production gate (`ProductionGateBlocks`, `AppHost.cs:1600-1607`,
called at `:2861`), and then calls `Paint` (`AppHost.cs:2872`). `Paint` (`AppHost.cs:2911`) runs phases 3–12 inline on
that thread; it re-binds the UI role at `:2916` because it is also entered synchronously from the WndProc repaint path
(`_window.PaintRequested`, `AppHost.cs:2485`).

The ordering inside `Paint`, with the lines that matter here:

| Step | Line | What |
|---|---|---|
| frame delta | `AppHost.cs:3025` | `float dtMs = _frameTime.NextDeltaMs();` — the *only* time source the animator sees |
| anim timebase publish | `AppHost.cs:3026-3027` | `_frameClockMs += dtMs; _scene.AnimClockMs = _frameClockMs;` |
| 3 flush / 4 render / 5 reconcile | `AppHost.cs:3145-3200` | reactive flush → component renders → reconcile |
| 6 layout | `AppHost.cs:3290-3299` | scoped layout |
| 6.5 layout effects | `AppHost.cs:3300-3305` | `DrainLayoutEffects()`, `_connected.Tick65()` |
| 6.5 structural seeding | `AppHost.cs:3310-3341` | `SeedEnterReflow` / `SeedReflowResize` off `_anim.PendingEnter/ExitReflow` |
| FLIP play | `AppHost.cs:3348` | `if (capturedProjections) ApplyProjections(keepAliveSuppressed);` |
| **7 animation** | **`AppHost.cs:3349`** | **`_anim.Tick(dtMs);`** |
| 7 presence finalize | `AppHost.cs:3350` | `_reconciler.FinalizeKeepAliveTransitions();` |
| 7 tree finalizers | `AppHost.cs:3351` | `_inputHooks.RunAfterAnimations();` |
| 7 relayout/reflow | `AppHost.cs:3352-3354` | `RunIncrementalLayout(); RunReflowLayout(layoutSize); SolveDirtyAndReclamp();` |
| 7 orphan reclaim | `AppHost.cs:3367` | `ReclaimSettledOrphans();` (body `AppHost.cs:4336-4362`) |
| 7 other tickers | `AppHost.cs:3396,3399,3400,3407,3423,3444` | scrollbar chrome, RepeatButton, caret blink, drag ghost, gesture arenas, image crossfades |
| **8 record** | **`AppHost.cs:3514`** | **`SceneRecorder.Record(_scene, _drawList, _images, …)`** (entry `SceneRecorder.cs:558`) |
| 8/8b record | `AppHost.cs:3524-3525` | `RecordDetached`, `RecordPopupWindows` |
| **PUBLISH (13a)** | **`AppHost.cs:3645`** | **`_framePublishSeq = _renderSeam.Publish(_drawList.Bytes, _drawList.SortKeys, in submitInfo, …)`** |
| wake render | `AppHost.cs:3647-3651` | `_renderThread.WakeAsync()` (async, the default) or `DrainSync()` (force-sync) |

Everything above PUBLISH is UI-thread. Nothing below it produces an animated value.

### 1.2 The render thread today submits and presents a *finished* DrawList

`RenderThread.Loop` (`RenderThread.cs:67-113`) waits on an `AutoResetEvent`, handles the device-lost recover gate
(`:73-85`) and the resize park (`:88-95`), then `TryAcquire`s the latest published frame and calls one delegate:
`_submitPresent(rf)` (`:100`), which is `AppHost.SubmitPresentOnRenderThread` (`AppHost.cs:745-781`) — drain image
jobs, `SubmitDrawList` from the published arena, `Present()`, `NotePresented`, video hole-punch drain. It then
publishes its ack (`Volatile.Write(ref _presentAck, rf.PublishSeq)`, `RenderThread.cs:103`).

It has **no clock, no timer, and no wake of its own**: `WakeAsync` (`RenderThread.cs:129`) is the only thing that ever
starts a turn, and only `AppHost.Paint` calls it. If the UI thread does not publish, the render thread presents nothing
new — the last-presented frame simply stays on screen.

### 1.3 What the seam carries — Cut A, not canon Cut B

`RenderFrame` (`RenderFrame.cs:22-44`) is a POD header naming a publisher-slot arena: `PublishSeq`, `ArenaIndex`,
`ByteLen`, `SortLen`, `FrameInfo Submit`, `SuppressVsync`, `InteractivePresent`. Its own doc comment
(`RenderFrame.cs:6-18`) states the deviation plainly: canon §2.1 specifies a Cut-B `SceneFrame` carrying
`SnapshotColumns` so *record* runs on the render thread; **Cut A carries the finished DrawList instead.**

`SceneFramePublisher` (`SceneFramePublisher.cs`) is the triple-buffer: three `RenderFrame` slots plus three pinned
per-slot command/sort-key arenas (`:39-41`, allocated `:46-52`). `Publish` (`:62`) picks a free slot (`PickFreeSlot`,
`:147`), copies the DrawList in, folds forward un-consumed repaint damage (`:76-92`), and release-stores
`_publishedIdx` (`:103`). `TryAcquire` (`:107`) acquire-loads it, re-verifies the seq after the ~300 B header copy
(`:113-126`), dedups against `_lastConsumedSeq` (`:130`), and release-stores `_consumeIdx` / `_lastConsumedSeq`
(`:132-133`).

Two drift notes for whoever amends the canon: **(a)** there is **no `DrawListArenaRing` type in `src/`** — its role was
folded into the publisher's per-slot arenas, while `threading-render-seam.md:487`, `:1316`, `:1372` and
`subsystems/README.md:40` still name it; **(b)** canon §11.1 names `AppHost.PhaseGateBlocks()` and a `DisplayPhaseGate`
type — the as-built is `AppHost.ProductionGateBlocks()` (`AppHost.cs:1600`) over `_frameTickSeq` /
`_lastProducedTickSeq` (`AppHost.cs:1578-1579`) sampled from `IPlatformWindow.DisplayClock` (`AppHost.cs:2640`).

### 1.4 Where animation lives, and what it writes

`AnimEngine` (the class behind the `AnimScheduler.*.cs` partials) owns one `AnimValueSlab` of 64 B POD rows keyed
`(node, channel)` (`AnimValue.cs:98-131`, slab `:141`). The channel vocabulary is 21 entries (`AnimTypes.cs:18`):

```
TranslateX TranslateY ScaleX ScaleY Rotation Opacity SizeW SizeH
StrokeTrimStart StrokeTrimEnd ClipL ClipT ClipR ClipB
LayoutW LayoutH BlurSigma BrushFade HoverFade PressFade DisclosureProgress
```

`Tick(in AnimClock)` (`AnimScheduler.cs:59`) is three passes: **PASS 1** advances every live, non-parked row over the
slab's active-node chain and evaluates the analytical generator at absolute `ElapsedMs` (`:66-113`); **PASS 2** folds
each node's rows over its *current* `NodePaint` and composes (`:114-142`); **PASS 3** frees settled/dead rows
(`:143-149`).

The single write point is `Compose` (`AnimScheduler.cs:170`): it takes `ref NodePaint p = ref _scene.Paint(node)`
(`:172`) and writes `LocalTransform`, `Opacity`, `BlurSigma`, `PresentedW/H`, `StrokeTrimStart/End`, `ClipRect`, then
`_scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty)` (`:212`). Four channels bypass `NodePaint`
entirely and write side tables from PASS 1 (`IsSideTableChannel` / `WriteSideTable`, `AnimScheduler.cs:274-287`):
`BrushFade` → `SetBrushAnimT`, `HoverFade`/`PressFade` → `SetInteractT`, `DisclosureProgress` →
`SetVirtualDisclosureProgress` (which writes `ScrollState.DisclosureT`, `SceneStore.cs:1332-1340`).

`LayoutW`/`LayoutH` are the one deliberate exception to "animation never relays out" (`AnimTypes.cs:15-18`): `Compose`
writes `LayoutInput` and marks the **parent** `LayoutDirty` (`AnimScheduler.cs:224`), which the host re-solves at
`RunReflowLayout` (`AppHost.cs:3353`).

`AppHost` calls the dt-injected overload `Tick(float dtMs)` (`AnimScheduler.Parity.cs:104-113`), not the wall-clock
`RunFrame` — so `AnimClock`'s 1..40 ms clamp (`AnimClock.cs:29-30, 41-52`) is bypassed and the *host's* clamp is the
only one in force on the production path.

### 1.5 The recorder reads live UI-thread state, not a snapshot

`SceneRecorder.Record` (`SceneRecorder.cs:558`) takes the live `SceneStore`, the live `ImageCache`, theme values
resolved at the call site (`Tok.ScrollThumb`, `Tok.AcrylicFlyout.Fallback`, `AppHost.cs:3514-3515`), a
`FocusVisualStyle` and `TextEditStyle` built that frame (`AppHost.cs:3600-3603`), the popup skip-root list, the
reuse-block roots and the `SpanTable` (`AppHost.cs:3516-3520`). It composes the world transform inline per node from
`NodePaint.LocalTransform` (`SceneRecorder.cs:1252-1253`) and the opacity from `parentOpacity * ResolveOpacity(...)`
(`:1277`), and it **bakes both into every emitted command** — each opcode carries its own `Affine2D Transform` and
`float Opacity` (`DrawList.cs:213, 281, 284, 334, 346, 352, 358, 362`). It also uses **file-static mutable scratch**
for damage entries and acrylic ranges, with the comment "UI-thread-only static scratch (record is single-threaded)"
(`SceneRecorder.cs:154-163`).

Two consequences, both load-bearing later: **(a)** there is no per-node transform *indirection* in the command stream
that a consumer could re-point without re-recording; **(b)** record as written cannot run on a second thread without a
rewrite (statics, live caches, theme statics).

### 1.6 The §12.1 slicer is specified and not implemented

`threading-render-seam.md:956-972` specifies `ReconcileSlicer` — deadline-driven reconcile (`RenderPriorityPolicy`
16 ms), carry-forward, atomic-on-complete, input hit-testing the last-published-consistent topology. Grepping `src/`
for `ReconcileSlicer|ShouldYield|RenderPriorityPolicy` returns **nothing**. Neither do the machinery its own
§12.2/§12.4/§12bis depend on: `ReconcileScratch`, `EnterReconcile`/`AssertNotMidReconcile`, `StoreReadLedger`,
`ILaneScheduler` — all absent. The reconciler writes SceneStore columns directly and immediately as it diffs.

The **only** deadline-aware call sites that exist are `Reconciler.ReRealizeVirtuals(long deadlineTicks)`
(`Reconciler.cs:545`) and `DecodeScheduler.Pump(..., long)`, both fed by `FrameBudget.DeadlineTicks`
(`FrameBudget.cs:14-31`, `MotionUiSliceMs = 3f`) and armed only while a viewport is dragging or ballistic. That is a
scoped precedent, explicitly "not a general-purpose scheduler, just the one seam the plan needed"
(`FrameBudget.cs:11-12`).

`animation-engine-rework-design.md` §6.4 states the same thing from the animation side: phases 6.5 / 7 / 13 are
"all UI-thread (the render thread, post-PUBLISH, only records the value-copied columns)".

### 1.7 The two failure modes, precisely

**Freeze.** Any UI-thread work between two `Paint` calls — a 300-row page model mapped in a posted callback, a lock
wait, a heavy component render, a GC pause — delays `_anim.Tick` and `SceneRecorder.Record` by exactly its own
duration. The render thread has nothing new to present, so the last frame holds. Every animation freezes together:
page slide, fade, shimmer, hover, press, caret, scrollbar chrome.

**Lost time, not deferred time.** When the frame does arrive, `dtMs` is clamped:
`StopwatchFrameTimeSource.NextDeltaMs` returns `Math.Clamp(dt, 0f, 34f)` (`FrameTimeSource.cs:61`), deliberately, so a
hitched frame does not leap a decelerate curve (`:57-60`). So a 200 ms stall during a 250 ms page slide does not resume
"200 ms further along" — it resumes 34 ms further along. The animation both stops **and** loses ~166 ms of progress;
wall-clock duration stretches with UI load. `Tick(float)` takes the raw step with no second clamp
(`AnimScheduler.Parity.cs:107-110`), so the host's clamp is the only one on the production path.

### 1.8 What is *not* the problem (do not re-fix these)

- Present pacing is already phase-correct: production is gated to one frame per compositor tick
  (`AppHost.cs:1600-1607`, `:2861-2870`), and the publisher coalesces DropOldest with last-writer-wins
  (`SceneFramePublisher.cs:107-135`).
- Submit/present/fence-wait already run off the UI thread (`RenderThread.cs:100`, `AppHost.cs:745`).
- The animation math is already dt-deterministic (analytical spring sampled at absolute `t`,
  `animation-engine-rework-design.md` §6.6). The *scheduling* of the tick is what is coupled, not the integrator.
- The slab is already zero-alloc in phases 6–13 and O(active nodes) per tick (`AnimValue.cs:150-165`,
  `AnimScheduler.cs:66-113`).

---

## 2. Option (i) — land canon §12.1: time-sliced, carry-forward reconcile

### 2.1 What it is

Implement `ReconcileSlicer`/`ReconcileWorkLoop` as specified: a deadline (`RenderPriorityPolicy`, 16 ms) checked at
unit boundaries during phase 5; on overrun, carry the partially-reconciled scratch forward and publish only when the
diff is whole; input dispatch hit-tests the last-published-consistent topology.

### 2.2 Against the binding contracts

| Contract | Verdict |
|---|---|
| Single-writer confinement (§1.1) | **Unchanged.** Everything stays UI-thread. |
| `ThreadGuard` roles | **Unchanged.** No new role, no new assert site. |
| Consume-gated quarantine (§5) | **Unchanged in shape**, but a carried-forward pass must not free slots mid-pass — the canon already requires reserved-but-discarded slots to return *without* quarantine (§18, `DiscardRestart_MatchesSingleThreadedGolden`). |
| 0 managed alloc, phases 6–13 | **At risk in phase 5**, which is outside 6–13 and therefore not gated today; the carry-forward scratch must be arena-backed to keep it that way. |
| dt-determinism gates | **Unchanged** — the integrator is untouched. |

### 2.3 The real cost, honestly

The canon's own precondition — "backend ops **staged in scratch**, applied only on `Complete`"
(`threading-render-seam.md` §14, phase-5 row) — **does not exist**. The reconciler mutates SceneStore as it diffs.
Slicing therefore means either

- **(a)** introducing `ReconcileScratch` + the `EnterReconcile`/`AssertNotMidReconcile` guard arming and rerouting
  every backend write through it — a large, invasive change to the single most safety-critical file in the engine; or
- **(b)** publishing a half-built tree across a frame boundary — which the canon forbids in the same sentence that
  specifies the slicer, and for good reason (input hit-tests it, the recorder walks it).

### 2.4 What it does **not** fix

Even fully landed, the slicer bounds **the engine's own reconcile**. It does not bound:

- an app callback posted onto the UI thread (`AppHost.Post`) that runs to completion before the next frame;
- a component `Render()` body that takes 300 ms (phase 4 is not the slicer's unit boundary; a unit is one node);
- a lock wait taken inside a hook body;
- a GC pause.

The complaint that motivated this doc — "a 300-row page model mapped in a posted callback freezes every running
animation" — is the first category. **Option (i) does not address it.** It is worth landing on its own merits (it
bounds the one thing the engine controls, and it is prerequisite work for lanes/`UseTransition`), but it must not be
sold as the answer to this question.

---

## 3. Option (ii) — render-thread compositor animation

### 3.1 The shape, in one paragraph

The render thread gets its **own clock** and its **own copy of the compositor-channel rows**. Each turn it advances
those rows by its own delta, composes them, and produces a frame from the **last published scene snapshot** —
re-presenting with new transforms/opacities even when the UI thread has published nothing since the previous turn. The
UI thread keeps everything structural: what animates, when it starts, what it retargets to, when a node may be freed,
enter/exit, FLIP, reflow, relayout. Seeds and retargets cross UI→render as POD commands riding **the same publish
release-store** as the DrawList (no second handshake). Settle/done events cross render→UI on a bounded SPSC ring
drained at frame start.

**The render thread never writes `SceneStore`.** That is the invariant that keeps §1.1's confinement table true. It
writes its own compositor row set and apply table; the UI reads a value-copied *mirror* of that table once per frame.

### 3.2 Which channels move, which stay

| Channel | Owner after (ii) | Why |
|---|---|---|
| `TranslateX/Y`, `ScaleX/Y`, `Rotation` | **RENDER** | pure `NodePaint.LocalTransform`; consumed only by record and (mirrored) hit-test |
| `Opacity` | **RENDER** | pure paint |
| `ClipL/T/R/B` | **RENDER** | pure paint (`NodePaint.ClipRect`, `AnimScheduler.cs:198-208`) — see the sticky-clip caveat in §7 |
| `SizeW`/`SizeH` (presented) | **RENDER** | `PresentedW/H` is presentation-only by construction — **except** when `NodeFlags.Relayouting` is set, where `Compose` adds the node to `IncrementalRoots` (`AnimScheduler.cs:216-217`); that variant stays UI |
| `BlurSigma` | **RENDER** | already a render-target channel — but see §4.2's honest limit |
| `StrokeTrimStart/End` | **RENDER** | pure paint |
| `HoverFade`, `PressFade` | **RENDER** | side-table `InteractT`, read only by the recorder's hover/press composite |
| `BrushFade` | **RENDER** | side-table `BrushAnimT`, read only by the recorder |
| **`LayoutW`/`LayoutH`** | **UI** | writes `LayoutInput` + marks the parent `LayoutDirty` (`AnimScheduler.cs:220-236`). **The one `LayoutDirty` opt-in stays UI-side** — a reflow is layout. |
| **`DisclosureProgress`** | **UI** | writes `ScrollState.DisclosureT` (`SceneStore.cs:1332-1340`), which virtualization and layout read |
| `SizeW/H` **with `Relayouting`** | **UI** | seeds `IncrementalRoots` → scoped relayout |
| scroll offset (`ScrollBind`, `ScrollBindEval`) | **UI** | a separate slab with its own kernel; explicitly out of scope for this doc |

Structural work that stays UI-side regardless: `SeedEnter`/`SeedExit` (`AnimScheduler.Structural.cs:33-52`), FLIP
capture + `ApplyProjections` (`AppHost.cs:3348`), presence / `DetachGroup` retire (`DetachedAnimSlab.cs:66-105`),
`FinalizeKeepAliveTransitions` (`Reconciler.cs:1462-1476`), `ReclaimSettledOrphans` (`AppHost.cs:4336-4362`),
`SetSubtreeParked`, and every `Cancel`/`ClearNode` on unmount.

### 3.3 The seam commands (UI → render), POD

Carried in the published frame, in a third per-slot arena alongside the command bytes and sort keys, so they inherit
the publisher's one release-store (`SceneFramePublisher.cs:103`) — there is no second lock-free surface on this
direction, which is the §0 constraint that matters most.

```csharp
namespace FluentGpu.Hosting.Threading;

/// <summary>What a seam command does to the render thread's compositor row set. POD; no allocation.</summary>
public enum AnimSeamOp : byte
{
    Seed = 0,       // create-or-retarget (node, channel) with the baked Generator
    Retarget = 1,   // keep Position/Velocity, re-aim To + Gen (the velocity-continuous handoff, §6.7)
    SnapTo = 2,     // reduced-motion / cancel-to-value: place and free
    Cancel = 3,     // free (node, channel); the value falls back to the published base column
    Park = 4,       // KeepAlive-parked subtree: stop advancing, keep the row
    Unpark = 5,
    ClearNode = 6,  // unmount / re-reconcile: free every row on the node
    ClearAll = 7,   // device-lost / resize republish: drop everything and re-seed from this frame
}

/// <summary>One UI→render animation command. 48 B, blittable, no GC refs.
/// The Generator is BAKED on the UI thread at seed time (the Newton duration-solve, the (response,ζ)→(ω,ζ)
/// conversion, the origin/v0 fold — AnimValue.cs:60-79) exactly as today; the render thread only EVALUATES.
/// That keeps every parameter-resolution allocation on the UI edge, where it already lives.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AnimSeamCommand
{
    public NodeHandle Node;        //  8 — {u32 index, u32 gen}; gen-checked against the SNAPSHOT, never live SceneStore
    public AnimChannel Channel;    //  1
    public AnimSeamOp Op;          //  1
    public GenKind Kind;           //  1
    public byte OwnerTag;          //  1 — the Fork-1 owner partition, preserved across the seam
    public AnimFlags Flags;        //  2 — Loop / RmExempt / Additive / DisplayRate / … (Parked is Op-driven)
    public ushort DrivenSrc;       //  2 — SignalSource index; WallClock (0xFFFF) for the compositor set
    public float To;               //  4
    public float From;             //  4 — NaN ⇒ "from the render thread's CURRENT value" (the retarget case)
    public float Velocity;         //  4 — v0 injection on a fresh spring seed
    public float DelayMs;          //  4
    public Generator Gen;          // 16 — the baked 16 B tagged union (AnimValue.cs:62-84), copied verbatim
}
```

`RenderFrame` gains three fields — the only change to the carrier:

```csharp
public struct RenderFrame
{
    // … PublishSeq, ArenaIndex, ByteLen, SortLen, Submit, SuppressVsync, InteractivePresent (unchanged) …

    /// <summary>Count of AnimSeamCommands in this slot's anim arena (0 on a frame that seeded nothing).</summary>
    public int AnimCmdCount;

    /// <summary>Element count of this slot's compositor BASE column copy — the rest pose the render thread composes
    /// its rows over. Indexed by a compacted animated-node index owned by this snapshot.</summary>
    public int CompositorBaseLen;

    /// <summary>Monotonic epoch of the UI's compositor row set. The render thread stamps every back-channel event with
    /// the epoch it was acting on, so the UI drops events that predate a ClearAll / device-lost republish.</summary>
    public ulong AnimEpoch;
}
```

### 3.4 The back-channel (render → UI)

```csharp
public enum AnimSeamEventKind : byte { Settled = 0, NodeIdle = 1, GroupSettled = 2, Overflowed = 3 }

[StructLayout(LayoutKind.Sequential)]
public struct AnimSeamEvent          // 24 B
{
    public NodeHandle Node;          // 8
    public AnimChannel Channel;      // 1
    public AnimSeamEventKind Kind;   // 1
    public ushort Pad;               // 2
    public float FinalValue;         // 4 — the settled value the UI writes into the BASE column (the new rest pose)
    public ulong AnimEpoch;          // 8 — drop if != the UI's current epoch
}

/// <summary>Bounded SPSC ring: render writes, UI reads, drained once per frame at the top of Paint.
/// Fixed capacity, arena-backed. NEVER DropOldest — a lost Settled event would strand an exit orphan.
/// On overflow it latches a single Overflowed flag instead, and the UI falls back to the wall-clock backstops it
/// ALREADY has: AppHost.OrphanSettleTimeoutMs = 2000 (AppHost.cs:4335) and
/// Reconciler.KeepAliveExitWallTimeoutMs = 2000 (Reconciler.cs:1481). Degraded, bounded, never wrong.</summary>
public sealed class AnimSeamEventRing { /* Volatile head/tail, no lock, no alloc */ }
```

The overflow rule is the design's honesty valve. Those two 2 s backstops already exist and already exist *because* a
wedged exit track must not pin `OrphanCount > 0` forever (`AppHost.cs:4326-4335`, `Reconciler.cs:1478-1481`). Re-using
them as the overflow path costs nothing and introduces no new failure mode.

### 3.5 What the frame must carry, and the copy budget

The render thread needs two things the Cut-A frame does not carry.

**(1) The compositor base columns** — the rest pose each animated row composes over. `Compose` today folds over the
node's *live* `NodePaint` (`Accum.FromPaint`, `AnimScheduler.cs:120`), which is what preserves un-animated channels.
The render thread needs the same base:

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct CompositorBase          // 64 B
{
    public NodeHandle Node;           //  8 — gen-validated against this snapshot
    public Affine2D  LocalTransform;  // 24 — the AUTHORED rest transform
    public float     Opacity;         //  4
    public float     BlurSigma;       //  4
    public float     PresentedW, PresentedH; //  8
    public RectF     ClipRect;        // 16 — RectF.Infinite = none
}
```

**Budget: `O(animated nodes)`, not `O(NodeCount)`.** A page slide animates 1–3 nodes; a staggered shelf enter animates
20–40; the pathological realistic case (per-row hover fade across a realized 60-row list) is ~60 nodes × 2 channels.
At 64 B that is **≤ 4 KB/frame** — three orders of magnitude below canon §3.2's up-to-full-tree `WorldTransform` copy
(`NodeCount × 24 B`, 120 KB at 5 000 nodes, paid *per frame while a transform animates*). This is the single strongest
reason to prefer the compositor-group variant below over full Cut B: **it never needs the full-tree snapshot.**

**(2) A way to turn a changed row into changed pixels.** Two variants follow.

### 3.6a Variant A — canon Cut B: record on the render thread from `SnapshotColumns`

The render thread re-records the whole tree from a value-copied `SnapshotColumns` (canon §2.1/§3.1), applying its own
composed values instead of the published ones.

Cost, from §1.5's evidence rather than estimation:

- `SceneRecorder` must be parametrized on a snapshot view rather than `SceneStore`. It reads `Bounds`, `Paint`,
  `Flags`, topology, scroll state, `InteractionInfo`, edge-fade config and the sticky-clip signature
  (`Columns.cs:118-128`) live — and canon §3.1 **explicitly excludes** the last three from the snapshot
  ("input/UIA read live UI-thread state, never the snapshot").
- Its file-static scratch (`SceneRecorder.cs:154-168`) must become instance state.
- `ImageCache` (`_images`) and the theme statics (`Tok.*`) must become render-readable by handle.
- The full-tree `SnapshotColumns`/`WorldTransform` copy budget of canon §3.2 applies on any transform dirt — i.e. on
  every animating frame, which is every frame this feature is about.
- `render-thread-seam-landing-plan.md` §2/§4.1 already scored this "Large / high (recorder rewrite dominates)" and
  chose Cut A for exactly this reason. Nothing in the code has changed that judgement; the recorder has only grown
  (damage entries, acrylic ranges, span table, popup skip-roots, reuse-block roots).

### 3.6b Variant B — compositor groups: re-submit the published DrawList with a new apply table

The observation that makes this cheap: the recorder already knows which nodes own compositor rows (it walks
`NodePaint`), and the submit path already touches every instance. Add one indirection:

- On entering a node that owns at least one render-owned row, the recorder emits
  `PushCompositorGroupCmd(int slot, RectF groupBoundsDevice)` and the matching pop. Everything inside is recorded
  **at the group's rest pose** — the base transform/opacity, not the animated one.
- The published frame carries the `CompositorBase` array (§3.5) plus the seam commands; `slot` indexes it.
- Each render turn the RT advances its rows and composes per slot into a
  `CompositorApply { Affine2D Xf; float Opacity; float BlurSigma; RectF Clip; }` table; the submit path multiplies the
  group's live transform into each contained command's baked `Affine2D` and multiplies its opacity — the same
  arithmetic the recorder does at `SceneRecorder.cs:1252-1253` and `:1277`, moved to submit.
- Repaint damage for a turn the UI did not produce is `oldGroupBounds ∪ newGroupBounds` per slot, unioned into the
  frame's `RepaintDamageRegion` (`gpu-renderer.md` §13.1). The group bounds are exactly what makes this computable
  without re-recording.

What Variant B buys: no recorder rewrite, no `SnapshotColumns`, no full-tree copy, no `ImageCache`/`Tok` migration.
What it costs and cannot do:

- **It cannot re-record.** Anything whose *geometry* changes — reflow, relayout, a presented-size change that re-clips
  children, a text re-shape — is not expressible as a group transform. §3.2 already keeps those UI-side.
- **Group nesting.** Groups inside groups compose multiplicatively; the recorder must emit them properly nested and the
  submit path must keep a small stack (depth ≤ 8, `FGGUARD`-asserted).
- **Clip and blur inside a group** animate a `PushLayerCmd`'s parameters, not a quad transform, so the apply must patch
  those scalar fields too. Feasible; the fiddliest part.
- **Clean-span reuse** must treat a group's contents as clean under a changed group transform. That is exactly the
  canon's `TransformDirty` fast path (`threading-render-seam.md` §3.2, §4 R2), so the *concept* is blessed; the
  implementation is not.

**I am not certain Variant B's opacity multiply is exactly equivalent to `ResolveOpacity`'s hover/press composite**
(`SceneRecorder.cs:1277`) in every case — that ramp is resolved from side tables *inside* the walk and interacts with
`NodeFlags` and inheritance. Proving the equivalence is the step-1 spike deliverable (§5), not something asserted here.

### 3.7 The render-thread tick loop

```csharp
// RenderThread.Loop, after the device-lost and resize gates (RenderThread.cs:73-95), replacing the
// "TryAcquire → submit → ack" body at RenderThread.cs:98-104.
private void Loop()
{
    ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Render);
    while (true)
    {
        // The wait is no longer "wake or nothing". While any compositor row is live we wait on the DISPLAY CLOCK —
        // the same seam AppHost's wait already uses (PlatformWaitRequest.WakeOnDisplayClock / IPlatformWindow.
        // WakePresent, canon §11.1) — so the render thread paces ITSELF off vblank when the UI thread is silent.
        // With no live rows it blocks on _wake exactly as today: zero idle cost, no perpetual loop.
        if (_compositor.HasActive) _wake.WaitOne(_compositor.NextDueMs());
        else                       _wake.WaitOne();
        if (!_running) break;
        if (HandleDeviceLostGate()) continue;   // RenderThread.cs:73-85, unchanged, and STILL FIRST
        if (HandleResizeParkGate()) continue;   // RenderThread.cs:88-95, unchanged — no motion turn while parked

        bool fresh = _publisher.TryAcquire(out var rf);   // may be false: no new UI frame this turn
        if (fresh)
        {
            _compositor.ApplySeam(_publisher.AnimCommands(rf), _publisher.CompositorBase(rf), rf.AnimEpoch);
            _last = rf;                                    // the snapshot we keep re-presenting
        }
        else if (!_last.HasContent) continue;              // nothing ever published — nothing to do

        // The render thread's OWN clock: same 1..40 ms clamp discipline as AnimClock (AnimClock.cs:41-52) so a
        // scheduler hiccup on THIS thread cannot leap a curve, and the same absolute-t generator evaluation, so a
        // trajectory stays a pure function of (Gen, From, To, ElapsedMs) — dt-deterministic by construction.
        _renderClock.Advance(Stopwatch.GetTimestamp(), wasIdleOrThrottled: !fresh && _sleptLong);
        bool moved = _compositor.Tick(in _renderClock, out RepaintDamageRegion motionDamage);

        if (!fresh && !moved) continue;                    // nothing new to show: DO NOT present (no spin)

        var submit = _last.Submit;
        if (!fresh) submit = submit with { RepaintDamage = motionDamage, PublishSequence = _last.PublishSeq };
        _compositor.PublishApplyTable();                    // slot → {Xf, Opacity, BlurSigma, Clip}, read at submit
        _submitPresent(_last, in submit);                   // AppHost.SubmitPresentOnRenderThread, same shape
        Volatile.Write(ref _presentAck, _last.PublishSeq);  // a motion-only turn cannot ADVANCE the ack past _last
        _compositor.DrainSettledInto(_eventRing);           // render → UI back-channel
        if (_eventRing.HasPending) _windowWake?.Invoke();   // nudge the UI ONLY when something structural settled
    }
}
```

Two properties worth stating: a turn with no fresh publish and no motion presents nothing, so the loop cannot become a
free-running spin; and `PresentAck` never advances beyond `_last.PublishSeq`, so the UI-side one-frame-per-tick
accounting in `ProductionGateBlocks` is unperturbed.

### 3.8 The `AppHost` changes

```csharp
// ── Paint, top (before the reactive flush at AppHost.cs:3145) ────────────────────────────────────────────
// Drain the render→UI back-channel FIRST, so a settle observed on the render thread is visible to THIS frame's
// FinalizeKeepAliveTransitions (:3350) and ReclaimSettledOrphans (:3367), exactly as an in-process settle is today.
_anim.DrainSeamEvents(_renderSeam.Events);   // writes FinalValue into the BASE NodePaint; frees the UI-side shadow row

// ── Paint, phase 7 (replacing AppHost.cs:3349) ───────────────────────────────────────────────────────────
// The UI tick now advances ONLY the UI-owned channels (LayoutW/H, DisclosureProgress, Relayouting SizeW/H) plus the
// non-slab tickers. Render-owned rows advance on the render thread; the UI holds a read-only MIRROR of their last
// composed values for hit-test, retarget-from-current, and FLIP capture (§3.9).
// HEADLESS / SingleThread (AppHost.cs:2495 spawns no render thread): TickUiOwned falls back to the full Tick(dtMs)
// and the seam commands are applied INLINE, so every VerticalSlice gate keeps its meaning (§3.14).
_anim.TickUiOwned(dtMs);
_anim.ImportRenderMirror(_renderSeam.Mirror);   // acquire-read + value copy of the RT's published apply table

// ── Paint, publish (at AppHost.cs:3645) ──────────────────────────────────────────────────────────────────
// Two extra spans ride the SAME release-store. Zero new synchronization on this direction.
_framePublishSeq = _renderSeam.Publish(
    _drawList.Bytes, _drawList.SortKeys, in submitInfo,
    animCommands:   _anim.DrainSeamCommands(),   // ReadOnlySpan<AnimSeamCommand>, arena-backed, cleared after copy
    compositorBase: _anim.CompositorBase(),      // ReadOnlySpan<CompositorBase>, O(animated nodes)
    animEpoch:      _anim.SeamEpoch,
    suppressVsync: keepAlive, interactivePresent: interactivePresent);

// ── The wake math (AppHost.RecommendedWaitMsCore / ComputeWakeReasons, AppHost.cs:1192, :2022) ───────────
// A render-owned animation NO LONGER makes the UI thread a reason to wake: WakeReasons.Anim becomes "UI-owned rows
// only". AnimEngine.HasActive already excludes parked rows; it now also excludes render-owned ones. A page slide with
// no other work therefore leaves the UI thread ASLEEP while the render thread animates — the point of the design, and
// its largest behavioural change (see risk 3 in §7).
```

### 3.9 Hit-testing, and the accepted mismatch

`InputDispatcher.StepIntoNode` (`InputDispatcher.cs:3866-3894`) inverts `NodePaint.LocalTransform` per node
(`:3871-3879`) and accumulates the net scale (`:3891-3892`). If the render thread owns the animated transform, the UI
must hit-test against *something*. Three candidates:

- **The base (rest) pose** — zero cost, zero staleness, but a mid-slide click lands where the page *will be*.
- **The mirror** (the RT's last published apply table, imported at `ImportRenderMirror`) — correct as of the last UI
  frame; during a UI stall it is exactly as stale as everything else the UI is doing, and during a stall no input is
  being dispatched at all, because dispatch is UI-thread.
- A live acquire-read of the RT table — **rejected**: a second lock-free surface for no benefit, and hit-test results
  stop being reproducible frame-to-frame.

**Decision: hit-test the mirror.** The accepted mismatch is therefore **up to one UI frame of visual lead** during
render-driven motion: the visual may be a few pixels ahead of the hit box. This is not a new class of compromise. The
dispatcher already declines to mirror the hover/press grow, with the reason in the code — *"Interaction hover/press
grow is deliberately NOT mirrored — the hit target keeps its model box while a thumb visually grows (matches the
previous engine behavior and WinUI's layout-driven hit testing)"* (`InputDispatcher.cs:3862-3865`). This design widens
that same accepted mismatch from hover/press scale to all render-owned compositor channels, and bounds it at one UI
frame instead of leaving it open-ended.

**The drag ghost is excluded.** `DragController` writes `LocalTransform` from the UI thread on every move
(`DragController.cs:395`; resting-transform save/restore `:546`, `:572`), and `Compose` already refuses to write
transform or opacity on a `NodeFlags.DragGhost` node (`AnimScheduler.cs:186-192`). The render thread must skip any
`DragGhost` node, and the seam must carry a `Cancel` on the flag's rising edge. That is the right answer anyway: a
drag ghost's position comes from the pointer, not from a generator, and it is input-latency-bound.

### 3.10 Input-driven channels and their latency

`HoverFade`/`PressFade` are seeded on a dispatcher edge (`AnimEngine.SetHover`/`SetPress`,
`AnimScheduler.Hover.cs:22-34`) in phase 2 on the UI thread. Under this design the *edge* still takes one UI frame to
reach the render thread (it rides the next publish); the *ramp* then runs on the render clock.

- **Steady state (UI healthy):** identical to today — edge dispatched and published in the same frame, first ramp
  sample on the same present.
- **UI stalled:** the hover edge is not even *detected* until phase 2 runs, so nothing is gained for the edge; a ramp
  already in flight keeps running. That is the honest split — the render thread can continue motion it was told about;
  it cannot discover input.
- **Drag ghost:** excluded (§3.9).

`SetHover` also cascades to descendants (`AnimScheduler.Hover.cs:100-120`), so one pointer move can produce a burst of
seam commands. Size the command arena for that burst (a 60-row container hover ≈ 60 × 2 channels × 48 B ≈ 6 KB) and
grow it **only at publish**, never in phases 6–13 — the same rule and the same code shape as the publisher's DrawList
arena (`SceneFramePublisher.cs:66-67`).

### 3.11 Pacing when only the render thread animates

Today the UI thread is the sole pacer: `ProductionGateBlocks` (`AppHost.cs:1600-1607`) admits one production per
compositor tick sampled from `IPlatformWindow.DisplayClock` (`AppHost.cs:2640`). With the UI thread asleep during a
render-driven animation there is no producer to gate, so the render thread must pace itself.

**Decision:** the render loop's wait becomes display-clock-driven while any compositor row is live (§3.7). The seam
already exists and is already canon-owned by `threading-render-seam.md` §11.1 —
`PlatformWaitRequest.WakeOnDisplayClock` + `IPlatformWindow.WakePresent()` over `Win32CompositorClock` /
`DCompositionWaitForCompositorClock`, a runtime capability probe with a wall-clock fallback
(`subsystems/README.md:40`). The render thread subscribes to the same clock. Where the probe fails, the fallback is the
swapchain's own frame-latency waitable, which already paces `_submitPresent` (canon §11.1) — so the degraded path is
"paced by present", not "unpaced spin".

State plainly in the amendment: **the §11.1 invariant "never produce a frame while a published one is still
unpresented" is about the UI *producer* and remains true.** A motion-only turn is not a production; it re-presents an
already-published frame with a new apply table. The `PublishSeq`/`PresentAck` relationship is unchanged.

### 3.12 Lifetime: the part that is actually dangerous

The render thread advances rows keyed by `NodeHandle`; the UI thread frees nodes. Today PASS 1 protects itself with
`if (!_scene.IsLive(r.Node)) { _doneScratch.Add(s); continue; }` (`AnimScheduler.cs:75`) — a **live SceneStore read**
the render thread must never make. The replacement is the canon's existing machinery:

- The render thread validates a row's `NodeHandle` against the **snapshot's** captured generation, never against live
  SceneStore. A handle whose gen no longer matches is dropped — the belt (canon §5.2).
- Slot reclamation stays consume-gated (`QuarantineLedger`, canon §5.2): a node freed while producing frame `p` is
  reclaimed only when `_lastConsumedSeq > p`. The suspenders.
- **New property to gate:** the render thread may re-present `_last` across many turns without a new publish, so
  `_lastConsumedSeq` does not advance during a motion-only run. That is *safe* (the UI simply cannot reclaim) but it
  means an animating page with a stalled UI thread accumulates quarantined slots. The bound is the stall's length; the
  UI's next publish discharges it. Gate it (`gate.compositor.quarantine-nongrowing`, §6): the pending set must be
  bounded by the number of **frees**, not by the number of **render turns**.
- `ClearNode` on unmount must ride the seam *before* the freeing publish, so the render thread has dropped the row by
  the time the slot could be recycled. Because commands ride the same frame, that ordering is by construction.

### 3.13 Device-lost, resize, minimize

- **Device-lost.** The recover gate (`RenderThread.cs:73-85`) runs before any acquire and must stay first; the
  compositor tick sits after it. Recovery issues an internal `ClearAll`: after `RecoverDevice()` the UI republishes a
  full frame and re-seeds. A surviving compositor row is harmless CPU state, but its *group bounds* refer to a DrawList
  that no longer exists, so the apply table must be dropped and rebuilt from the next publish.
- **Resize.** `Quiesce`/`Resume` (`RenderThread.cs:141`, `:153`) already park the loop before the UI mutates the
  swapchain; the compositor tick is inside the loop, so it parks with it. On resume the post-resize relayout
  republishes and the seam commands in that publish re-seed. **A motion-only turn must not run while
  `_resizeQuiesce != 0`** — the §3.7 gate ordering guarantees it.
- **Minimize / occlusion.** The UI's minimize gate stops publishing; a render thread animating into a minimized window
  would burn power for nothing. Park the compositor on the same edge `AppHost` already owns (`UpdateWindowVisible`,
  `AppHost.cs:2892`): one broadcast `Park` command on minimize, `Unpark` on restore — no per-row cost.

### 3.14 Zero-alloc and determinism — how each gate stays green

| Gate | How it survives |
|---|---|
| 0 managed alloc, phases 6–13 (`gate.alloc.steady-zero`) | The seam-command and compositor-base arenas are pinned, geometric-grow, and grown **only at publish** — the same rule and code shape as `SceneFramePublisher.cs:66-67`. The render-side row set is a second `AnimValueSlab` instance grown only in `ApplySeam` (the render-side analogue of "grow only at reconcile", `AnimValue.cs:143-146`). The apply table is a fixed array sized to the base high-water. |
| dt-determinism (`animation-engine-rework-design.md` §6.6) | Untouched. The render thread evaluates the **same** `Generator.Eval` at absolute `ElapsedMs`; a trajectory is a function of `(Gen, From, To, ElapsedMs)`, so it is identical whichever thread sums the deltas. |
| Headless determinism replay | **`RenderLoopMode.SingleThread` must keep ticking every channel on one thread.** A Headless window never spawns a render thread (`AppHost.cs:2495`), so `TickUiOwned(dtMs)` falls back to today's full `Tick(dtMs)` and seam commands apply inline in publish → apply → tick → record order. This is the same single-thread pass-through shape seam Step 1 used (`render-thread-seam-landing-plan.md` §5 Step 1) and it is what keeps every VerticalSlice gate meaningful. |
| `ThreadGuard` confinement | The render thread writes **only** its own slab + apply table; it never touches `SceneStore`, `LayoutCache`, `ComponentTable`, the intern tables, or any UI structure. `AssertRender` on every render-side slab mutator; `AssertUi` on `DrainSeamEvents`/`ImportRenderMirror`/`DrainSeamCommands`. |
| Consume-gated quarantine | Unchanged in shape; one new bounded-growth property to gate (§3.12). |
| **One lock-free surface (§0)** | **Preserved on UI→render** (commands ride the publish release-store). The **back-channel ring and the mirror are new** cross-thread words — this is the one place the design genuinely widens §0's "exactly one lock-free fence surface", and the amendment must say so (see §3.15 and risk 2). |

### 3.15 Canon amendments this would require

| Doc | Line/§ | What changes |
|---|---|---|
| `SPEC-INDEX.md` | §2 row 46 (**Threading / frame phases**) | "record/batch/submit/present (8–11) run on a dedicated render thread reading an immutable `SceneFrame`" gains: *…and the render thread owns the compositor-channel `AnimValue` rows, advancing them on its own display-clock-paced tick between UI publishes.* |
| `SPEC-INDEX.md` | §2 row 86 (**Animation engine**) | The canonical value gains an ownership split: the slab is **two** instances — a UI-side authority (seeds, structural, `LayoutW/H`, `DisclosureProgress`) and a render-side compositor set (transform/opacity/blur/clip/presented-size/hover-press-brush fades) — with the POD seam command as the only writer of the latter. |
| `SPEC-INDEX.md` | §2 row 47 (**Quarantine constant**) | Add: reclaim is additionally bounded by motion-only render turns not advancing `_lastConsumedSeq`; the pending set is bounded by frees, not by turns. |
| `threading-render-seam.md` | §1.1 confinement table | New rows: *compositor `AnimValue` rows* → sole writer RENDER; *compositor apply table* → RENDER (write) → UI (read via mirror value-copy); *seam command arena* → UI (write) → RENDER (read), value-copied at PUBLISH; *`AnimSeamEventRing`* → RENDER (write) → UI (read). |
| `threading-render-seam.md` | §0, "exactly one lock-free fence surface left" | Must be amended honestly: the back-channel ring + the mirror are a **second** named surface. Either fold them into the publish/consume handshake (mirror published *in* the frame; events acknowledged by seq) or state two surfaces. **Folding is preferred and is a design constraint, not a detail.** |
| `threading-render-seam.md` | §2.1 `SceneFrame` | Add `AnimCmdCount`, `CompositorBaseLen`, `AnimEpoch`; note that Cut A's `RenderFrame` is where they land today. |
| `threading-render-seam.md` | §4 (R0–R5 ordering) | Insert **R1.5 COMPOSITOR TICK** between render-local epoch validation (R1) and record/submit: advance rows → compose apply table → compute motion damage. |
| `threading-render-seam.md` | §11 / §11.1 | State that a motion-only turn is not a *production* and does not violate "never produce a frame while a published one is still unpresented"; document the render thread's display-clock subscription and the frame-latency-waitable fallback. |
| `threading-render-seam.md` | §12.1 | Content unchanged, but add that it is **unimplemented** — canon currently reads as if `ReconcileSlicer` were in force (§1.6). |
| `threading-render-seam.md` | §14 phase map | Phase 7 splits: *7a animation (UI-owned channels)* → UI; *7b compositor tick* → RENDER (post-PUBLISH, once per render turn). |
| `threading-render-seam.md` | §18 test ledger | Add the gates of §6. |
| `animation-engine-rework-design.md` | §6.4 | "Animation occupies three slots in the 13-phase loop, **all UI-thread**" is the exact sentence that stops being true. It becomes: phase 6.5 and the structural half of phase 7 are UI-thread; the compositor half of 7 runs on the render thread's own clock; phase 13 retire stays UI (it mutates the reconciler). |
| `backdrop-effects-animation.md` | §5 preamble + §7 thread map | The §7 table's `7 animation | UI` row splits; the PUBLISH(13a) row gains the seam commands + compositor base; `BlurSigma` is called out as render-owned. |
| `architecture-spec.md` | §4.8 (13-phase loop) + its ⊳ note | Phase 7's one-liner "timelines write LocalTransform/Opacity" gains the ownership split; the ⊳ note gains the compositor tick. |
| `subsystems/README.md` | ownership map | New owned artifacts, one owner each: `AnimSeamCommand`, `AnimSeamEvent`, `AnimSeamEventRing`, `CompositorBase`, `CompositorApply` (threading doc); `PushCompositorGroupCmd` (**owned by `gpu-renderer.md`**, not the threading doc). Also fix the stale `DrawListArenaRing` row (§1.3 drift note (a)). |
| `gpu-renderer.md` | §3–§13 opcode set, §13.1 damage | Variant B only: the new group opcode + the "motion damage = old ∪ new group bounds" rule. |
| `validation.md` | gate ledger | The new gates of §6. |

`docs/design/check-canon.ps1` scans only `docs/design/`, so **this** plan file needs no gate run — but every edit above
does (`powershell -File docs\design\check-canon.ps1`, exit 0).

---

## 4. Recommendation

### 4.1 The call

**(i) then (ii-B); treat (ii-A) as out of scope for this problem.**

1. **Land option (i)'s cheap half now, and its expensive half on a different account.** The parts of §12.1 that need no
   `ReconcileScratch` — naming the 16 ms `RenderPriorityPolicy` constant and widening the existing `FrameBudget`
   deadline (`FrameBudget.cs:14-31`) beyond drag/ballistic — are days, not weeks, and bound the engine's own worst
   frames. The full carry-forward slicer should be sequenced with lanes/`UseTransition` (`reconciler-hooks.md` §6–§8),
   where its scratch pays for more than one feature. **It is not the answer to the question this doc was asked**, and
   §2.4 says exactly why.
2. **Then land option (ii) in Variant B (compositor groups).** It delivers the actual property — motion that survives a
   busy UI thread — for the channels carrying essentially all of the perceived motion (page slide, fade, hover/press
   scale, shimmer opacity), at a fraction of Cut B's cost and with no full-tree per-frame snapshot.
3. **Do not land Variant A (canon Cut B) for this.** The recorder rewrite it needs was already declined once on
   evidence (`render-thread-seam-landing-plan.md` §2), the evidence has only grown (§1.5), and Cut B's own
   `NodeCount × 24 B` transform copy (canon §3.2) is paid on exactly the frames this feature is about. If record ever
   shows on the UI-thread budget independently, revisit Cut B on *that* merit.

### 4.2 The limits that remain — decoupled, not invincible

- **A GPU stall still bounds back.** A sustained present-wait stalls the render thread, which is now the animator too;
  the publisher's degenerate backpressure (§11) then bounds the UI. Motion degrades with the GPU, as it must.
- **Blur is a render-target channel.** `BlurSigma` animating means an offscreen layer per frame
  (`Columns.cs:96-101`); moving its *row* to the render thread does not make its *cost* smaller. A blur-heavy motion
  that was GPU-bound stays GPU-bound.
- **Reflow is layout.** `LayoutW/H` and `DisclosureProgress` stay UI-side by construction (§3.2). The responsive
  "make room" reveal (`AppHost.cs:3310-3341`) freezes with the UI thread and always will.
- **Input discovery is UI-thread.** The render thread can continue a hover ramp; it cannot notice a new hover.
- **Hit boxes lag the visual by up to one UI frame** during render-driven motion (§3.9) — a widening of a mismatch the
  dispatcher already accepts by design.
- **Structural completion is UI-gated.** An exit orphan is reclaimed by the UI thread (`AppHost.cs:4336`); its
  *animation* finishes on time, its *teardown* waits for the UI. The two 2 s wall-clock backstops keep that from
  wedging.
- **Nothing here makes the app's own UI-thread work cheaper.** A 200 ms callback still costs 200 ms of input latency,
  of reconcile, of layout, and of any UI-owned channel. Only compositor motion is decoupled.

---

## 5. Landing plan

Engineer-days for one implementer who knows the codebase, review excluded. Each step is independently shippable and
independently gated; no step leaves the tree with a red gate. The order puts the riskiest unknown (§3.6b's group-apply
equivalence) first, before anything is committed to.

| # | Step | Files | Gate | Days |
|---|---|---|---|---|
| **0** | **`RenderPriorityPolicy` + budget widening (option (i)'s cheap half).** Name the 16 ms constant; extend `FrameBudget` arming beyond drag/ballistic to any frame whose predecessor overran; keep the two existing budgeted call sites. No behaviour change on steady frames. | `Hosting/FrameBudget.cs`, `Hosting/AppHost.cs` | `gate.budget.deadline-honored` | 2–3 |
| **1** | **SPIKE: group-apply equivalence (go/no-go).** Prove offline that `apply(groupXf, groupOpacity, clip, blur)` over a rest-pose-recorded subtree is **byte-identical** to recording that subtree at the animated pose — including `ResolveOpacity`'s hover/press composite (`SceneRecorder.cs:1277`), the origin conjugation (`:1252-1253`) and `CounterScaled`. Headless, no threads. **If this fails, Variant B fails and the recommendation reverts to (i)-only.** | `Render/SceneRecorder.cs` (extract the compose arithmetic into a pure static both sides call), `VerticalSlice/Suites/AnimSuite.cs` | `gate.compositor.apply-equivalence` | 4–6 |
| **2** | **Seam types + single-thread pass-through.** `AnimSeamCommand`, `AnimSeamEvent`, `AnimSeamEventRing`, `CompositorBase`, `CompositorApply`; `RenderFrame`'s three fields; the publisher's third per-slot arena; `AnimEngine.DrainSeamCommands`/`CompositorBase`/`DrainSeamEvents`. **Wired single-threaded** — the UI publishes commands and applies them inline on the same thread, exactly as seam Step 1 did. Zero behaviour change; every existing gate green unchanged. | `Hosting/Threading/{AnimSeam.cs (new), RenderFrame.cs, SceneFramePublisher.cs}`, `Animation/AnimScheduler.Seam.cs (new)`, `Hosting/AppHost.cs` | `gate.compositor.seam-roundtrip`; `gate.alloc.steady-zero` unchanged | 4–5 |
| **3** | **Render-side slab + compositor tick, still woken by the UI.** A second `AnimValueSlab` owned by `RenderThread`; `ApplySeam`, `Tick`, `PublishApplyTable`, the mirror import; channel ownership moves per §3.2. The render thread still turns only on a publish wake — **no self-pacing yet**, so frame production is unchanged. | `Hosting/Threading/{RenderThread.cs, CompositorAnim.cs (new)}`, `Animation/AnimScheduler.*.cs`, `Hosting/AppHost.cs`, `Input/InputDispatcher.cs` (mirror read) | `gate.compositor.mirror-parity`, `gate.compositor.determinism-two-thread`; `gate.seam.race` re-run | 6–8 |
| **4** | **Group recording + apply at submit (Variant B).** `PushCompositorGroupCmd`/`Pop`; the recorder emitting groups at rest pose for compositor-owned nodes; the submit-time apply + nesting stack; motion damage = old ∪ new group bounds. | `Render/{DrawList.cs, SceneRecorder.cs}`, `Render/` batcher, `Windows/D3D12/*` (submit-side apply) | `gate.compositor.group-damage`; `--repaint-identity` route parity; `--screenshot` visual parity | 6–9 |
| **5** | **Self-pacing: the render thread's own clock.** Display-clock subscription, `NextDueMs`, motion-only turns, the "no fresh publish and no motion ⇒ present nothing" rule, the minimize park, the resize/device-lost gate ordering, and the `WakeReasons.Anim` change. **This is the step that delivers the feature.** | `Hosting/Threading/RenderThread.cs`, `Pal/` display-clock seam, `Hosting/AppHost.cs` (wake math) | `gate.compositor.ui-stall-progress`, `gate.compositor.alloc-zero`, `gate.compositor.quarantine-nongrowing`, `gate.compositor.settle-backchannel`; 4-minute race soak | 5–7 |
| **6** | **Canon reconciliation.** Every row of §3.15, including the honest §0 amendment about the second lock-free surface (or the work to fold it into the first, if step 3 did not). | `docs/design/**`, `docs/plans/animation-engine-rework-design.md` | `check-canon.ps1` exit 0; full VerticalSlice green in **Debug and Release** | 2–3 |

**Total: 29–41 days**, of which step 1 (4–6) is the go/no-go spike and step 0 (2–3) is independently useful. The
recommended commitment is step 0 + step 1 first (**6–9 days**) with a decision point after the spike.

---

## 6. Test ledger

Named in the existing `gate.<area>.<name>` convention (`VerticalSlice/Suites/*.cs`; cf. `gate.seam.publish-consume`,
`gate.seam.race`, `gate.anim.activeChainMatchesDictionary`, `gate.alloc.steady-zero`).

| Gate | What it proves | Where | Build |
|---|---|---|---|
| `gate.compositor.apply-equivalence` | Applying a group transform/opacity/clip/blur to a rest-pose-recorded subtree is byte-identical to recording it at the animated pose, over 200 randomized trees × 500 randomized poses, including the hover/press opacity composite, the origin conjugation and `CounterScaled`. **The step-1 go/no-go.** | `AnimSuite.cs` | all |
| `gate.compositor.seam-roundtrip` | Seed / retarget / cancel / park / clear-node / snap round-trip UI→arena→apply and produce the same row state as a direct in-process seed; a reduced-motion `SnapTo` places and frees identically. | `AnimSuite.cs` | all |
| `gate.compositor.determinism-two-thread` | **The headless determinism replay across both threads.** The same script at dt ∈ {8.33, 16.67, 33.3} ms produces an identical value trace whether rows are ticked (a) single-thread inline (`RenderLoopMode.SingleThread`, the headless default) or (b) split UI/render with the render deltas injected to sum to the same total. Absolute-`t` evaluation makes this a *property*, so a failure means the split leaked state. | `AnimSuite.cs`, both arms headless | all |
| `gate.compositor.ui-stall-progress` | **The probe the feature exists for.** Start a 250 ms page slide; on frame 3 block the UI thread for **200 ms**; assert (a) the render thread presented ≥ 10 further frames during the stall, (b) the animated node's applied translate was **strictly monotonic** across them, (c) the value at stall-exit is within one render tick of the un-stalled golden at the same wall time — i.e. **no lost time**, the §1.7 defect is gone — and (d) no `ThreadGuard` throw, no quarantine growth beyond the frees issued. | new `Suites/CompositorSuite.cs` | FGGUARD |
| `gate.compositor.mirror-parity` | With one render turn per UI frame, the mirror the UI imports equals the single-thread golden at every frame of a 300-frame replay — so hit-test, retarget-from-current and FLIP capture see the numbers they see today. | `CompositorSuite.cs` | all |
| `gate.compositor.group-damage` | A group translated by Δ emits damage exactly `old ∪ new` (padded by the AA floor), the retained canvas shows no stale pixels, and `--repaint-identity` route parity still holds. | `DamageSuite.cs` | all |
| `gate.compositor.quarantine-nongrowing` | Under a UI stall spanning N render turns during motion, the `QuarantineLedger` pending set is bounded by the number of frees, not by the number of render turns; reclaim resumes on the next publish. | `DiagnosticsSuite.cs`, beside `gate.seam.race` | FGGUARD |
| `gate.compositor.alloc-zero` | A 600-frame motion-only run (render turns with no publish) allocates **0** managed bytes on the render thread and 0 on the UI drain path (`GC.GetAllocatedBytesForCurrentThread()` delta, per thread). | `CompositorSuite.cs` | all |
| `gate.compositor.settle-backchannel` | A seeded exit settles on the render thread, the event reaches the UI, and `ReclaimSettledOrphans` frees the orphan on the next frame. Forced ring overflow falls back to the 2 s wall-clock backstop and still reclaims — degraded, never wedged. | `CompositorSuite.cs` | FGGUARD |
| `gate.budget.deadline-honored` | (step 0) A synthetic realize backlog drains across frames with each frame's UI span within budget + one unit, and no work is lost. | `CoreSuite.cs` | all |
| screenshot parity | `WindowsApp --screenshot` on the gallery motion page, before/after, at a pinned animation time, is pixel-identical. | `--screenshot` | GPU |
| race soak | 4-minute randomized UI-stall soak under continuous motion: zero device-lost, zero `ThreadGuard`/`QuarantineLedger` throws, non-growing quarantine, present cadence within one refresh of the display clock. | manual / nightly | FGGUARD |

---

## 7. Risks, and what I could not verify

**Top three risks.**

1. **Group-apply equivalence may not hold** (§3.6b). `ResolveOpacity` composes hover/press ramps *inside* the walk with
   `NodeFlags` and inheritance (`SceneRecorder.cs:1277`), and the origin conjugation interacts with `CounterScaled`
   (mirrored in `InputDispatcher.cs:3883-3889`). If a group's opacity cannot be factored out of that composite,
   Variant B loses its cheapest channel and the design must either exclude opacity from groups (leaving fades
   UI-bound) or fall back to Cut B. **This is why step 1 is a go/no-go spike, and why nothing else is committed first.**
2. **The design widens §0's "exactly one lock-free fence surface."** The back-channel ring and the mirror are new
   cross-thread words. The mitigation (fold the mirror into the published frame; acknowledge events by seq) is stated
   as a design constraint, but if it proves impractical, the canon's central safety claim has to be honestly weakened —
   and that is the owner's decision, not an implementation detail.
3. **The UI thread stops waking for animation** (§3.8). A page slide with no other work would leave the UI thread
   asleep. Anything that *implicitly* relied on "an animation means a frame" must be audited and either moved to the
   render side or kept as an explicit UI wake reason: `_frameAfterPaint` follow-ups, image crossfade ticks
   (`_images.Tick`, `AppHost.cs:3444`), scrollbar chrome fade (`:3396`), caret blink (`:3400`), RepeatButton
   (`:3399`), gesture-arena timers (`:3423`), the connected-animation tick (`_connected.Tick65`, `AppHost.cs:3304`),
   and the ambient-FPS/scroll-hold pacing described in `backdrop-effects-animation.md` §5's as-built note. **I have not
   enumerated that dependency set exhaustively**; doing so is part of step 3 and could add days.

**Stated uncertainties — not smoothed over.**

- **I measured nothing.** Every number in §1.7 and §3.5 is derived from code, not from a capture. The claim that
  animated nodes are `O(1..60)` rather than `O(NodeCount)` is a structural argument about how the slab is populated,
  not a measurement of the driving app.
- I did not verify whether `ClipRect`'s sticky-viewport signature (`StickyClipSpan`, `Columns.cs:118-128`) can be
  animated by a render-owned row without confusing `InputDispatcher`'s sticky-clip input gate. §3.2 lists
  `ClipL/T/R/B` as render-owned; that may have to be narrowed to "non-sticky clips only".
- I did not verify how `ConnectedAnimation`/`DetachedAnimSlab` (`Animation/ConnectedAnimation.cs`,
  `DetachedAnimSlab.cs`, recorded via `SceneRecorder.RecordDetached`, `AppHost.cs:3524`) interacts with render-owned
  rows. Detached flies are recorded from a separate slab against a separate anchor and are behind `FG_DETACHED_FLY`
  (default-off), so I scoped them **out** — but "out of scope" is a choice, not a proof that they are unaffected.
- I did not verify the popup-window path (`RecordPopupWindows`, `AppHost.cs:3525`; per-popup DrawLists,
  `AppHost.cs:3983`), nor the detached-child-host routing (`DrainChildRenderSources`, `AppHost.cs:1413-1424`). A
  compositor group inside a popup subtree records into a *different* DrawList; whether one apply table indexes across
  all of them is unresolved.
- The `DrawListArenaRing` and `DisplayPhaseGate`/`PhaseGateBlocks` names in canon do not exist in `src/` (§1.3). I
  treated these as documentation drift rather than missing implementation, on the evidence that the publisher and
  `ProductionGateBlocks` do the described job — but I did not trace every canon claim about them.
- I did not open the driving app's source (it is a sibling repo, `christosk92/WaveeMusic`). The workload description in
  §1.7 is the one the owner supplied, restated against engine facts, not something I verified in Wavee.
