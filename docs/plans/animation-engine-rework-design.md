# Reworking the FluentGpu Animation Engine â€” `AnimValue`: one POD slot, one scheduler, one composition point, signals-first end-to-end

*Lead-architect plan, in the style and rigor of `docs/plans/generic-hookable-scroll-engine-design.md`. Canon owner reconciled by this rework: `design/subsystems/backdrop-effects-animation.md` Â§5. Sole research input: `docs/plans/animation-engine-research-dossier.md` (esp. Â§7 aâ€“h, Â§8, the open questions). Two judged fork decisions (composition model, authoring surface) are folded in as MADE premises, not re-litigated.*

---

## 1. Thesis

The animation engine's *value laws are already correct* â€” the `AnimChannel` axis vocabulary, the fold-to-`NodePaint` compose, the never-`LayoutDirty` discipline, the spring's velocity-handoff intent, and the channelâ†’dirty-axis taxonomy are all on the right side of every line WinUI's compositor model reaches only by convention. **What is fragile is everything *around* the laws: there is no single value primitive, no single scheduler, and no single composition point â€” so the same idea is implemented six-plus ways, by N uncoordinated writers, gated by accident of phase-7 call order, with reduced-motion as a process-global `bool` that an authoring branch can read (the conditional-hook crash) and tracks as GC-owned `class Track` on a `List` with O(n) scans and a per-tick `Dictionary` scratch.** This rework collapses the entire surface onto **one POD `AnimValue` slot keyed `(NodeHandle, AnimChannel)` carrying `{Position, Velocity, To, Generator}` that interpolates *from its own current value* and *auto-retargets on signal change***, sampled by **one pure `Generator.Eval(absolute-t)`** (the analytical closed-form spring replacing the sub-stepped Euler), scheduled by **one `AnimScheduler`** that owns the canonical `dt`, the per-source `CadenceClass`, and the wake decision (`min next-due`, not `any(HasActive)`), composed by **one owner-partitioned write per `(node,channel)`** (Fork 1), authored by **one declarative `Element` field set + two `DepKey` hooks** that bake to slab seeds at reconcile with zero closures (Fork 2), and read â€” never branched on â€” for reduced motion. This is the `ScrollBind` slab idiom (`generic-hookable-scroll-engine-design.md` Â§3â€“4) generalized from "scroll-offset source" to *any signal source*: scroll becomes one `SignalSourceKind`, the playhead another, a peer `AnimValue.Position` a third. The per-frame path stays index arithmetic over `ChunkedArena`-backed slabs â€” zero managed allocation on phases 6â€“13, structurally.

**The hybrid steer, calibrated honestly.** The corpus's "just works" target is *one model end-to-end: CSS/SwiftUI at the bottom, Framer at the top.* We adopt and reject precisely:

- **From CSS/WAAPI â€” ADOPTED:** auto-interpolate-from-current + retarget (the per-(element,property) running-transition slot); the compositor-property split as a *structural* ceiling (the `AnimChannel` enum *is* the compositor-safe set â€” "animate this" is incapable of triggering relayout, save the one `LayoutW/LayoutH` Reflow opt-in); `linear()` as a sampled LUT for cheap "spring feel" on the non-interruptible eased path; `@starting-style`/`allow-discrete` enter/exit; `composite: replace|add|accumulate` (already `CompositeOp` 1:1). **REJECTED:** the WAAPI `Animation`/`KeyframeEffect` object graph and `Promise finished` (object alloc); the speculative `scrollsnapchanging` double-fire.
- **From SwiftUI/Compose â€” ADOPTED:** signal-change â†’ auto-retarget-from-current as *the* recommended primitive (`UseSpringValue`/`UseAnimatedValue` keep `Pos+Vel`, never cancel+relaunch); spring-as-default with velocity handoff; `snapTo` vs `animateTo`; `matchedGeometryEffect` as the shared-element model; `rememberInfiniteTransition`'s "tie ambient life to the source, stop when gone" as the cadence keystone. **REJECTED:** Compose's coroutine cancel+relaunch retarget (loses velocity, allocates) â€” FluentGpu's in-place field mutation is strictly better.
- **From Motion (`C:/WAVEE/motion`) â€” ADOPTED:** the MotionValue *unit* (value+velocity) re-expressed as one `AnimValue` slab row whose "subscribers" are the existing `TransformDirty|PaintDirty` mark; the generator-as-the-animation contract `next(t) â†’ {value,done}` sampled at *absolute* time (the determinism keystone); the single ordered frameloop with a 40ms-clamped `FrameData` + `useDefaultElapsed`-after-idle + `useManualTiming` replay; the priority state machine (`whileHover < â€¦ < exit`) as a per-node `InteractionState` byte; stagger as a reconcile-time `delay = delayChildren + indexÂ·each` bake; AnimatePresence as a detach-group completion gate; `relativeTarget` as the one genuinely-missing FLIP refinement. **REJECTED:** the `MotionValue` `SubscriptionManager`/`dependents` collections, `buildTransform` *strings*, `Promise.all` orchestration, the O(nÂ²) `Array.from().indexOf` stagger, the persistent `ProjectionNode` object graph, the `Set<Process>` closure queues, the per-frame `sharedNodes: Map<string>` lookups.
- **From Framer-maximal â€” REJECTED as the *primary* model** (it was a dossier-explicit rejection): variants are **demoted** to named target-set *values* + an opt-in inherited cascade byte, not a magic React-style context; the declarative props are sugar that *bakes to the spine seed*, not a parallel runtime.
- **From WinUI â€” REJECTED wholesale, substitutes named:** string-parsed `ExpressionAnimation` â†’ the compiled POD `AnimValue`/`ScrollBind` edge (handle references, enum operators, baked floats); GC-reachability-owned lifetimes (`CompositionPropertySet` silently dies when collected) â†’ reconciler-owned gen-versioned handles with explicit free; `ConnectedAnimationService`'s "overlay samples a dead slot" â†’ the `DetachedAnimSlab` + fence-pinned `ImageHandle`; `InteractionTracker`'s "hand the compositor your scroll" â†’ "consume OS position/velocity as a signal, run the engine-owned integrator" (the honest `decoupled, not invincible` tradeoff the corpus already states).

**The honest non-overclaim** (mirroring `backdrop-effects-animation.md Â§8` and the scroll doc's Â§7 calibration): what *genuinely* unifies into one pipeline is the value primitive, the generator, the scheduler/cadence authority, the composition point, and the authoring surface â€” that collapses 6+ mechanisms. What *keeps a privileged branch* is host-driven FLIP (it needs the pre-reconcile measure only the host can stage); the single `LayoutW/LayoutH` Reflow `LayoutDirty` exception (a self-feeding loop guarded by target/echo guards); blur (an expensive RT channel, not a free compositor write); and the `DetachedAnimSlab`'s second render-walk (a recycled topology slot *cannot* outlive its node, so a value-copied snapshot is the only seam-safe form). The scheduler unifies the *wake authority*, but the per-source *cadence layer is net-new*, and the sub-refresh vsync-beat is a physical limit we eliminate for the common cases and *document*, not abstract away.

---

## 2. Problem â€” six band-aids over a missing spine, tied to file:line

The dossier's ranked catalog, restated as the design problem this doc dissolves. Each rank is a *symptom of one of five missing abstractions* â€” a value primitive, a scheduler, a composition point, a declarative bake, and reduced-motion-as-value â€” and the rework supplies all five.

### 2.1 Rank 1 â€” N uncoordinated `NodePaint` writers, last-writer-wins (most systemic)

Phase-7 call order is the *de-facto* per-channel arbiter. `AppHost.Paint` (`AppHost.cs:1121-1137`) runs, in source order: `_anim.Tick` â†’ `RunReflowLayout` â†’ `ReclaimSettledOrphans` â†’ `_connected.Settle` â†’ `_interact.Tick` â†’ `_scene.AdvanceBrushAnims` â†’ `_dispatcher.TickTouchpad` â†’ `_scrollAnim.Tick` â†’ `ScrollBindEval.ApplyPinAndFlagPass` â†’ `ScrollBindEval.RunObservers` â†’ `_repeat.Tick` â†’ `_caretBlinker.Tick` â†’ `_dispatcher.DragDrop.Tick` â†’ `_dispatcher.Drag.Tick` â†’ `_dispatcher.TickGestureArenas`. **Fifteen ticks, each a writer or a potential writer**, colliding only by *accident of disjoint node-sets*. Three independent non-mechanisms prevent corruption: (a) **disjoint storage** â€” `AnimEngine` writes `LocalTransform` directly (`AnimEngine.cs:702-726`), `InteractionAnimator` writes *progress scalars* the recorder folds at record-time (`SceneRecorder.cs:205-214`), scroll writes the *ContentNode*, sticky writes the *sticky node* (`ScrollBindEval.ApplyPin`); (b) **call order** â€” a later tick simply overwrites the byte a former wrote; (c) **`Accum.FromPaint`** (`AnimEngine.cs:111-137`) coordinates only *within* `AnimEngine`, and it does so by **decomposing the live matrix** â€” which is **lossy**: rotation reads `Atan2(M12,M11)` which is 0 for a scaled-only matrix (`:118`), and scale uses the polar `sqrt(M11Â²+M12Â²)` (`:116-117`) that disagrees with the raw-`M11` read elsewhere. A node that is both scroll-bound and `AnimEngine`-driven on transform round-trips through this lossy decompose and corrupts. **Missing abstraction:** a single composition point per `(node,channel)` with an *explicit owner partition* â€” Fork 1's owner column + (Phase 2) the single fold-and-write-once compose pass that reads `(OwnerTag,Priority)` and writes `LocalTransform`/`Opacity` *once*, deleting both `FromPaint` and the recorder's separate hover-scale multiply. (Hit-test must keep reading layout `Bounds`, never the composited scale â€” `InputDispatcher.cs:3752-3780` inverts `LocalTransform` but *excludes* interaction scale; this is the load-bearing invariant Fork 1 segregates into a separate `NodePaint.InteractionScale` field.)

### 2.2 Rank 2 â€” imperative `Seed` vs declarative target; hook/ticker fragmentation; the closure leak

Every motion is an imperative `Seed`/`Animate`/`Spring`/`Drive` on a `(node,channel)` pair (`RenderContext.cs:473-483`). "From current" works *only* because `CurrentValue`/`TryGetTrackValue` re-read the live column on each seed; retarget/interrupt/reduced-motion/lifetime are each hand-managed. **The closure leak:** `DrivenClockTable._sources` is a `List<Func<float>>` (`AnimEngine.cs:54-58`) that **never deregisters** â€” `UseDrivenAnimation` registers one closure per mount (`RenderContext.cs:483`, `a.Clocks.Register(source)`), unbounded. **The container model:** `class Track` is a 33-field heap reference type (`AnimEngine.cs:69-102`), one GC allocation per seeded animation, on a `List<Track>` (`:165`), with two O(n) linear scans gating every seed (`Find`/`Get`), a per-tick `Dictionary<NodeHandle,Accum> _scratch` (`:166`, the rehash-on-new-node phase-7 alloc hazard), and `SetNodeParked`/`HasTracks` as O(tracks) scans (`:182-189`). **Missing abstractions (four):** (i) a declarative signal-fed `AnimValue` slot that auto-retargets; (ii) index-based driven clocks (read `ScrollState`/media-clock by index, never `Func<float>`); (iii) one driver registry with a uniform `(Tick, HasActive, CadenceClass)` contract; (iv) a POD slab (`ChunkedArena`, `(node,channel)â†’head` index, free-list lifetime) replacing `List<Track>` + scan + `Dictionary`. (Core Â§1 lands i/iv; Core Â§2 lands ii/iii.)

### 2.3 Rank 3 â€” the conditional-hook / ReducedMotion crash

Hooks are positional with unchecked downcasts: `cell = (EffectCell)_cells[_cursor]` (`RenderContext.cs:293`), `(AsyncResourceCell<T>)_cells[_cursor]` (`:577`), over a `List<HookCell>` (`:159`). `Motion.ReducedMotion` (`Dsl/Motion.cs:24`) is a **process-wide mutable `bool` a drag-resize grip flips**. The natural authoring mistake â€” `if (Motion.ReducedMotion) return;` inside a `Use*` (which the *imperative recipes* `MotionRecipes.PopIn`/`Shake` legitimately do) â€” changes the hook-slot count between renders â†’ the next `UseLayoutEffect` cell is cast to the wrong type â†’ `EffectCellâ†’AsyncResourceCell` crash "on resize" (the resize being exactly what flipped the global). Nothing enforces the "read-as-value, never early-return in a `Use*`" rule; the codebase *teaches both habits*. **Missing abstraction:** reduced-motion as a VALUE read only by `Tick`/seed (and the reconcile bake), never a branch that changes hook count â€” structurally impossible to reintroduce (Core Â§4.e).

### 2.4 Rank 4 â€” bound-row remount flash

Enter/exit lifetime is keyed to *node mount* (`Reconciler.cs:1724`, `isMount && at.Enter.Active`). A selection/now-playing re-skin re-renders the realized window â†’ re-mounts rows â†’ replays their enter terminal â†’ the whole list flashes. The fix (`ItemsView.CreateBound` persistent bound slots) is an *opt-in the author must remember*. **Missing abstraction:** enter/exit keyed to *logical identity*, not node-mount â€” the engine recognizes "same logical row, re-skinned" via the bound-slot identity the reconciler already holds, and does NOT re-seed (Core Â§3.2 + Â§4-presence).

### 2.5 Rank 5 â€” connected-anim KeepAlive coupling + overshoot; Rank 6 â€” transparent-boundary Grow-mirror race; Rank 7 â€” ambient-FPS free-run; Rank 8 â€” full-page blur cost

- **Rank 5** (`ConnectedAnimation.cs`, entire): a *second animation system* (string-keyed dicts, `_scene.CreateNode(8)` live overlay, hand-managed retire/settle/cull) that does **not** use `AnimEngine`'s park lifecycle (`OnNodeParked` is `{}`). The fly only works because navigation parks the leaving page; without parking the reconciler recycles the home-cover slot into the detail-cover slot (same handle) â†’ `FindDest` excludes it â†’ silent no-fly. Overshoot is fought with a tightened default + a bespoke "near-identity AND not-moving" settle heuristic. Reverse/Back connected is unimplemented. **Missing:** shared-element flight as a first-class declarative transition on the unified spine, overlay = a `DetachedAnimSlab` row (Core Â§3.3) â€” dissolves the recycled-slot hazard because the snapshot is value-copied, not a live handle.
- **Rank 6** (`Reconciler.cs:507-512` `MirrorParticipation`, 7 hand call-sites): layout-transparency is a *convention, not a type*; miss the call (or call it before the async child mounts) and a `Grow=1`-only subtree collapses to 0 â†’ an empty list (the empty-Liked bug). Animation-adjacent because the skeletonâ†’real swap *is* the reveal edge. **Missing:** a solver-native `LayoutTransparent`/`BoundaryKind` byte the solver honors at measure time. *(This is owned by the layout subsystem; this rework references it as the presence-boundary substrate â€” Core Â§3.2 `BoundaryKind.Presence`.)*
- **Rank 7** (`AppHost.cs:1114-1119` Resync lurch; `WakeDiagnostics.cs` 16-bool union; the `AnimIsAmbient()`/scroll-grace/`LatencySensitiveWake` pile): no notion of per-source *cadence*. The loop asks each ticker "active?" then collapses to "anything animating â†’ display rate," with one global escape (`AmbientAnimationFps`). An auto-playing seek playhead sets `HasActive` every frame â†’ free-runs at 120Hz for a bar moving 3px/s (the MEMORY `high-CPU-ambient-fps-cap` bug); a 2Hz caret holds the loop at display rate. **Missing:** one frame scheduler with a per-source `CadenceClass {DisplayRate, Hz(n), Driven(signal), OneShot}` so wake is `min(next-due)`; a `Driven` source costs zero frames when its signal is unchanged (Core Â§2).
- **Rank 8** (`MotionRecipes` `UseSoftReveal` unconditionally seeds `BlurSigma`; the full-screen RT pass per reveal frame): the recipe is cost-blind (MEMORY `full-page-reveal-blur-cost`). **Mitigation:** blur as a cost-gated channel + the `KeepFade`/`SnapEnd` reduced-motion policy per token (Core Â§4.e, Â§10).

### 2.6 The spec divergence the rework lands

`backdrop-effects-animation.md:403` specs a single **64B `AnimTrack` POD in a `SlabAllocator<AnimTrack>`** with `ClockKind {Frame|Driven}`, `AnimFlags`, and a `NodeHandleâ†’TrackHead` O(1) map; Â§5.3 specs the `DetachedAnimSlab`; Â§5.7 the velocity-handoff `Retarget`; Â§5.8 `ContentSizeAnimator`. **None of that container model is built** â€” the implementation is `class Track` + `List` + scans + `Dictionary` + bool flags standing in for `AnimFlags`, exit is done via live `scene.Orphan` (Rank 5/the `HasTracks`-poll reclaim, `AppHost.cs` orphan path), and the Euler integrator (`backdrop-effects-animation.md:478-498`) is dt-path-dependent (`n = clamp(ceil(dt/4ms),1,8)`). **This rework is the chance to land the spec, not re-patch the divergence** â€” and to correct the spec where the implementation already moved past it (the live `AnimChannel` is the 17-entry transform set, not the doc's 11-entry `{â€¦BlurSigma,Exposure,WashAlpha,CrossFade,Tint}`; Â§4 reconciles this in Core Â§1.e).

---

## 3. The model â€” the named primitives

Eleven named primitives. The first three are the value spine; the next three the scheduler/composition layer; the last five the structural/authoring layer. Each is stated with its single owner and what it subsumes.

### 3.1 `AnimValue` â€” the one value slot (the SLOT)

One POD row per `(NodeHandle, AnimChannel)` carrying `{Position, Velocity, To, Generator}` plus classification, timeline, flags, and two intrusive chain links. It **is** Motion's MotionValue unit + CSS's per-(element,property) running-transition slot + SwiftUI's `animate*AsState` integrator, re-expressed as a slab row whose "subscribers" are the engine's existing `TransformDirty|PaintDirty` mark (no `SubscriptionManager`). It interpolates *from its own current `Position`* and *auto-retargets* on signal change (spring keeps `Velocity`; eased re-seeds `From := Position`). **Subsumes:** `class Track` (`AnimEngine.cs:69`), the `_tracks` `List`, the `Find`/`Get` scans, the `_scratch` `Dictionary`, the `BrushAnim` side-table (`SceneStore.AdvanceBrushAnims`), `InteractionAnim.HoverT/PressT`, and the `AnimValueCell` 16ms-step vestige (`RenderContext.cs:452`). Owner: Core Â§1 (data model in Â§4 below).

### 3.2 `Generator` â€” the one POD law, sampled at absolute time (the LAW)

A 16-byte tagged-union POD `{Eased | Spring | Inertia | Keyframes}` evaluated by one pure function `Generator.Eval(in Generator, kind, from, to, tMs) â†’ Sample{value, done}` returned **by value** (kills Motion's shared-mutable `{value,done}` aliasing). The spring is the **analytical closed form** (three regimes â€” under/critical/over-damped â€” lifted and generalized from the shipping `OverscrollPhysics.StepSpring`), sampled at absolute elapsed `t`; the sub-stepped Euler (`backdrop-effects-animation.md:478-498`) is **DELETED**. Inertia is the closed-form exponential coast (subsumes `OverscrollPhysics.CoastStep`). All parameter resolution (the Newton `FromResponse` duration-solve, `visualDuration/bounce`) happens in `BakeGenerator` at reconcile; `Eval` never solves, parses, iterates, or allocates. **This is why determinism is free** â€” `Eval` reads only `t = Î£ stepMs`, never chunk boundaries, so it is bit-stable across dt âˆˆ {8.33,16.67,33.3}. Owner: Core Â§1.bâ€“1.c.

### 3.3 The retarget/handoff rule (the CONTINUITY law)

On any signal-fed `To` change or interrupting tween: read the current `value(ElapsedMs)` and `velocity(ElapsedMs)` analytically; write `Position := value`, `Velocity := velocity`, `To := newTarget`, `ElapsedMs := 0`, re-bake `Generator.{A,B}` from the new `(x0=Positionâˆ’To, v0=Velocity)`. **Rebase, not frame-shift** â€” the closed form is parameterized by `(x0,v0,t)` from a fixed origin, so you cannot shift it without recomputing `(A,B)`; the existing `ReframePosition` frame-shift (`AnimEngine.cs:573`, used by FLIP) re-expresses as a rebase with the layout delta folded into `Position`. Spring keeps velocity (handoff); eased re-seeds `From := Position` (optionally reverse-shortened â€” **never both handoff and reverse-shorten**, the CSS L1 rule). Owner: Core Â§1.d.

### 3.4 `AnimScheduler` + `CadenceClass` (the WAKE authority)

One scheduler owning a `FrameClock` (Motion's `FrameData` â€” 40ms-clamped delta, first-frame forced to 1/60, `useDefaultElapsed`-after-idle, `useManualTiming` replay), a dense swap-buffered active-set of `Source {Handle, SourceTag, CadenceClass, NextDueMs}` rows, and the wake decision as `min(next-due over active sources)`. `CadenceClass {DisplayRate | Hz(n) | Driven(signalSlot) | OneShot | Paused}` is the per-source cadence: a `Driven` source contributes **zero frames when its bound signal is unchanged** (epoch compare) and is excluded from the time-due scan (event-woken). **Subsumes:** the 15-ticker phase-7 sequence's arm/disarm/`HasActive` bookkeeping, the 16-bool `WakeReasons` union (`ComputeWakeReasons`), the `RecommendedWaitMsCore` branch tree, `AnimIsAmbient()`, `LatencySensitiveWake`, the scroll-grace, the `AmbientAnimationFps` global (demoted to a per-source default), and the `Resync()` lurch (`AppHost.cs:1114-1119`, dissolved by the clock's idle guard). `WakeDiagnostics`'s public contract survives, repointed at the active-set. Owner: Core Â§2.

### 3.5 The `InteractionState` priority resolver (the GESTURE arbiter)

One per-node `StateByte {Hover, Pressed, Focus, Drag, InView, Exit}` written by input dispatch *as a signal*, + up to 6 baked `AnimTargetSet` rows referenced by a fixed 6-slot priority order (`Exit > Drag > Pressed > Hover > Focus > InView`). A `written`-mask resolver (Framer's `animateChanges`) runs in phase 7 *before* the spine `Tick`, claims each channel to the highest-priority active state, and `Retarget`s; releasing a state animates each freed channel **back to the next-lower writer** (the `everWritten & ~written` diff supplies "back to resting" for free). **Subsumes:** `InteractionAnimator` (entire â€” it is a hardcoded 2-state instance), the recorder hover-scale multiply (`SceneRecorder.cs:205-214`), the `BrushTransition`/`AdvanceBrushAnims` path, and the `HoverScale`/`PressScale`/`HoverOpacity`/`PressedOpacity`/`BrushTransitionMs` `Element` fields. Owner: Core Â§4.b.

### 3.6 Presence + `DetachedAnimSlab` (the LIFETIME primitive)

The one structural primitive: **a detach value-copies a node's paint columns into a stable gen-versioned slab row that outlives the recycled topology slot, pins its `ImageHandle` behind the deferred-delete fence, drives its `LocalTransform/Opacity/Aux` from `AnimValue` rows, and emits it in a second render-walk in phase 8.** Exit-presence detaches one leaving node; connected/shared-element detaches a snapshot of the source and springs it to the live dest rect; `popLayout` presence detaches and lets siblings reflow into the gap. A `DetachGroup {PendingCount, Mode, StructuralAnchor}` is the completion gate: structural removal is deferred until `PendingCount â†’ 0`. **Subsumes:** `scene.Orphan`/`ReclaimOrphan`/`OrphanCount` + the `HasTracks`-poll reclaim, the `_overlays`/`OverlayClip` band-clip, and `ConnectedAnimation`'s live `CreateNode(8)` overlay. Owner: Core Â§3.1â€“3.3.

### 3.7 Layout / `MorphId` FLIP (RETAINED, extended)

Host-driven FLIP (`CaptureProjections`/`ApplyProjections` + `LayoutTransition.Position` + `ReframePosition`) is **retained verbatim** â€” it needs the pre-reconcile measure only the host can stage. Extended with Motion's **`relativeTarget`** (a `RelativeFrame` side-row + a depth-ordered phase-6.5 pre-pass re-resolving a child's spring target against its *moving* projecting parent, coherence-group-capped) and a **virtualization safety rule** (`CaptureProjections` skips a slot whose `VirtualState.RecycleEpoch` changed â€” a recycled row is a *mount*, not a *move*; a cross-window reorder animates the `ExtentTable` delta, not a per-row transform). `Layout` is the friendly alias of the existing `Animate = LayoutTransition.AutoAll`; `MorphId` is unchanged; they compose (in-page FLIP + cross-page hero). Owner: Core Â§3.4.

### 3.8 The `Transition` spec (the IMPLICIT on-change rule)

A 12-byte interned POD `{TransitionDynamics, ChannelMask, DelayMs, Flags}` on the base `Element`, governing the on-change cross-fade of *any* channel it names (`ChannelMask = 0` â‡’ all animatable). It **subsumes `BrushTransitionMs`** (Fill/Color become channels). A bound channel whose `Effect` writes a new value, or a static channel the reconciler's column-diff sees change, routes through `Retarget(node, channel, newValue, Dynamics)` instead of a raw store. Owner: Core Â§4.a.

### 3.9 The `MotionToken` registry (the THEMED vocabulary)

One `MotionTokenId â†’ MotionTokenDef {TransitionDynamics, ReducedMotionPolicy}` per-theme `FrozenDictionary`, resolved once at theme load on theming's `Tok.*` source-gen machinery. **Subsumes** the three colliding token namespaces (`Dsl.Motion` `Fast=150`, `Dsl.Expressive` `Fast=250`, `Animation.MotionSprings`) â€” the `Motion.Fast != Expressive.Fast` footgun is deleted. `Foundation.Easing` (the Newton-bezier named curves) is **retained and extended** with a `linear()` sampled LUT + Back/Circ/Anticipate. Owner: Core Â§4.d.

### 3.10 `ReducedMotionPolicy` (the VALUE, never a branch)

A per-node/per-token `byte {SnapEnd, KeepFade, Exempt}` (default `KeepFade` â€” fades survive, transforms snap; WCAG "reduce, don't remove") read by the spine `Tick`/seed and the reconcile bake, **never by authoring code**. This structurally kills the conditional-hook crash (Rank 3): no `Use*` body branches on reduced-motion, so no hook-slot count varies. Owner: Core Â§3.7 + Â§4.e; canon Â§5.9/Â§5.10 reconciled.

### 3.11 `SignalSource` (the GENERIC source â€” the `ScrollBind` generalization)

A POD `{SignalSourceKind, RefIndex}` row read by index (no `Func<float>`), where `SignalSourceKind {ScrollOffset, ScrollBand, MediaClockMs, SignalFloat, NodeChannel}` is the closed set a `Driven` `AnimValue` row or a `Driven` `CadenceClass` binds to. **Subsumes** `DrivenClockTable`'s `List<Func<float>>` closure leak. This is the literal generalization of `generic-hookable-scroll-engine-design.md`'s "scroll-offset source" to *any signal source* â€” scroll is one kind, the playhead another, a peer `AnimValue.Position` a third. Owner: Core Â§1.a + Â§2.4.

---

## 4. Data model â€” the concrete blittable POD shapes

All new types live in `FluentGpu.Animation` (the natural sibling of `ScrollBind`/`AnimEngine`) and `FluentGpu.Scene` (the column refs). Every slab is `ChunkedArena`-backed (native, no LOH cliff), free-list recycled, reconciler-owned (not GC-reachable). Sizes are exact and `[StructLayout]`-pinned.

### 4.1 `AnimValue` â€” 64 B, the slab row

```csharp
namespace FluentGpu.Animation;

// 18-entry axis vocabulary: the live 17 (AnimEngine.cs:10) + Color (the 18th â€” RGBA, side-row storage, Â§4.4).
public enum AnimChannel : byte { TranslateX, TranslateY, ScaleX, ScaleY, Rotation, Opacity,
    SizeW, SizeH, StrokeTrimStart, StrokeTrimEnd, ClipL, ClipT, ClipR, ClipB, LayoutW, LayoutH, BlurSigma, Color }

public enum GenKind : byte { Eased, Spring, Inertia, Keyframes }

[Flags] public enum AnimFlags : ushort {
    None = 0, JustSeeded = 1<<0, Done = 1<<1, Loop = 1<<2, Parked = 1<<3,
    RestoreLayout = 1<<4, TrailingAnchor = 1<<5, Driven = 1<<6, RmExempt = 1<<7,
    Additive = 1<<8, Accumulate = 1<<9 }   // Additive â‡’ CompositeOp != Replace; Accumulate distinguishes Add vs Accumulate

[StructLayout(LayoutKind.Sequential)]                       // 64 bytes exactly â€” lands backdrop-effects-animation.md:403
public struct AnimValue
{
    public NodeHandle Node;            // 8   {u32 index, u32 gen} â€” gen-checked each Tick (IsLive)
    public AnimChannel Channel;        // 1   the axis this row drives
    public byte OwnerTag;              // 1   Fork-1 owner partition {None,Anim,Scroll,Interaction,Connected}
    public byte Priority;              // 1   Fork-1 Framer 7-slot arbitration (compose-pass input)
    public byte GenKind;               // 1   Generator discriminant {Eased,Spring,Inertia,Keyframes}
    public float Position;             // 4   the channel's live scalar (Track.Pos/.Value collapsed)
    public float Velocity;             // 4   units/s â€” analytical, for handoff
    public float To;                   // 4   rest target / last-keyframe value
    public float ElapsedMs;            // 4   absolute elapsed since (re)seed â€” the Generator's t
    public float DelayRemainingMs;     // 4   begin delay
    public Generator Gen;              // 16  tagged-union POD (Â§4.2)
    public float RestoreTo;            // 4   declared LayoutInput restored at settle (Reflow)
    public AnimFlags Flags;            // 2
    public ushort DrivenSrc;           // 2   index into SignalSourceTable; 0xFFFF = wall-clock
    public int NextOnNode;             // 4   next AnimValue on the SAME node (teardown + per-node fold)
    public int NextActive;             // 4   next active row in the scheduler's dense walk (-1 = tail)
}
```

The two `int` chains replace the entire `Dictionary`/`List` container model: `NextOnNode` is the per-node fold/teardown chain (mirrors `ScrollBind.NodeNext`), `NextActive` is the scheduler's dense advance walk. `OwnerTag`/`Priority` are written at reconcile and are exactly Fork 1's reconcile substrate (the owner column at slot resolution + the Framer arbitration byte).

### 4.2 `Generator` â€” 16 B, the tagged union

```csharp
[StructLayout(LayoutKind.Explicit, Size = 16)]   // GenKind on the AnimValue row selects the reading
public struct Generator
{
    // SPRING â€” coefficients BAKED at reconcile from (response,Î¶); origin & v0 fold into A,B at seed:
    [FieldOffset(0)]  public float Omega;     // natural freq Ï‰ = sqrt(k/m)
    [FieldOffset(4)]  public float Zeta;      // damping ratio Î¶
    [FieldOffset(8)]  public float A;         // pre-baked regime coefficient #1
    [FieldOffset(12)] public float B;         // coefficient #2
    // EASED â€” two-point or multi-keyframe span:
    [FieldOffset(0)]  public float From;
    [FieldOffset(4)]  public float DurationMs;
    [FieldOffset(8)]  public ushort EaseId;   // Foundation.Easings / linear()-LUT id; 0 = linear
    [FieldOffset(10)] public ushort KeyOffset;// keyframe-arena span start; 0 â‡’ two-point Fromâ†’To
    [FieldOffset(12)] public ushort KeyCount; // 0/2 â‡’ two-point
    // INERTIA â€” friction coast (subsumes OverscrollPhysics.CoastStep):
    [FieldOffset(0)]  public float V0;        // launch velocity px/s
    [FieldOffset(4)]  public float DecayK;    // k = -ln(decayPerS) > 0
    [FieldOffset(8)]  public float Boundary;  // optional clamp target (NaN = unbounded)
}
public readonly struct Sample { public readonly float Value; public readonly bool Done;
    public Sample(float v, bool d) { Value = v; Done = d; } }   // returned BY VALUE â€” no shared-mutable aliasing
```

### 4.3 The slab + the `(node,channel)â†’head` index + the signal-source table

```csharp
public sealed class AnimValueSlab            // reconciler-owned; mirrors ScrollBindTable (ScrollBind.cs:94)
{
    private AnimValue[] _rows;                // ChunkedArena-backed dense slab; grows ONLY at reconcile
    private int _count;
    private readonly Stack<int> _free;        // free-list â€” reconciler-owned slot recycling, NOT GC
    private readonly Dictionary<int,int> _headByNode;   // node INDEX â†’ head of its NextOnNode chain
    private int _active, _parked, _loop;      // census, maintained at flag-edge, never scanned

    public ref AnimValue At(int slot) => ref _rows[slot];
    public int  HeadOnNode(int nodeIndex) => _headByNode.TryGetValue(nodeIndex, out int h) ? h : -1;
    public bool HasActive => _active - _parked > 0;     // O(1) â€” replaces AnimEngine.HasActive
    public int  Alloc();  public void Free(int slot);   // free-list pop/grow ; push + unlink
}

// kills the DrivenClockTable List<Func<float>> leak â€” index dispatch, no delegate, reconciler-recycled
public enum SignalSourceKind : byte { ScrollOffset, ScrollBand, MediaClockMs, SignalFloat, NodeChannel }
public struct SignalSource { public SignalSourceKind Kind; public int RefIndex; }   // RefIndex = scroller/clock/slot idx
```

`Replace`-dedup (the old `Get` scan, `AnimEngine.cs:593`) becomes a `_headByNode[nodeIndex]` chain walk filtered on `Channel == ch && !Additive` â€” O(animations-on-this-node), typically 1â€“4. `Find`/`SetNodeParked`/`HasTracks`/`CancelAll` all walk `NextOnNode`, never `_tracks`. The `NodeHandleâ†’TrackHead` map the canon mandates (`backdrop-effects-animation.md:454`) **is** `_headByNode` + the per-node channel filter.

### 4.4 The Color / Brush side-row (linear-premultiplied)

`AnimChannel.Color` is RGBA, not a scalar, so it does **not** ride `Position`. It rides a sparse `Vector4 ColorPos, ColorTo, ColorVel` in a `_colorAux` slab aligned to the value slab by slot (exactly how `EffectAux` is a cold side slab). Endpoints are converted gammaâ†’linear and premultiplied **once at seed** (reconcile-cold) into `_colorAux`; per-frame is a 4-float lerp by eased `u`. The recorder consumes premultiplied-linear directly; the **text-gamma exception** is the recorder's draw-time concern (`NodeFlags.TextRun` applies the DirectWrite gamma curve at draw) â€” the animated value stays linear-premultiplied. **Subsumes** the `BrushAnim` side-table + `AdvanceBrushAnims` (`SceneStore`). The fold/compose branch on `if (ch == Color)` in `WriteComposited`, never letting the 4-vector leak into the scalar fold.

### 4.5 The Fork-1 owner column â€” permanent core infrastructure

Per Fork 1, the owner/priority data is *permanent substrate* (A's reconcile authority), not a stopgap. Storage = a per-node packed `OwnerWord {uint Bits; uint Gen}` with gen-stamped lazy-clear, registered as a **cold SoA scene column**, keyed by physical sink (`OwnChannel`, 5â€“7 entries). The finer 18-entry `AnimChannel` is carried per-row in `AnimValue.OwnerTag`/`Priority` for the scalar fold. **This dual resolution is intentional** (owner column by physical sink, contribution by axis) and is kept consistent in the canon (`backdrop-effects-animation.md Â§5`).

```csharp
namespace FluentGpu.Scene;
public enum OwnChannel : byte { Transform, Opacity, Blur, PresentedSize, Clip, InteractScale, Color }  // 5â€“7 physical sinks
public enum OwnerTag   : byte { None, Anim, Scroll, Interaction, Connected }
[StructLayout(LayoutKind.Sequential)] public struct OwnerWord { public uint Bits; public uint Gen; }   // 8 B/node, cold
```

### 4.6 The segregated `NodePaint.InteractionScale` field (the Fork-1 correctness invariant)

The single sharpest correctness move in Fork 1: the recorder's hover/press scale multiply folds into a **separate paint field**, `NodePaint.InteractionScale` (a scalar/`Affine2D`), **never `LocalTransform`**. Hit-test inverts `LocalTransform` and *excludes* interaction scale (`InputDispatcher.cs:3752-3780`) â€” folding interaction scale into `LocalTransform` would silently grow every hovered thumb's hit box. The recorder reads a precomputed `InteractionScale` (removing the per-frame `TryResolveInteractionProgress` walk); hit-test continues to invert `LocalTransform` only. Landed in Phase 1, preserved through Phase 2.

### 4.7 The detached/presence PODs

```csharp
[StructLayout(LayoutKind.Sequential)] public struct DetachedNode   // ~96 B; SlabAllocator<DetachedNode>, gen-versioned
{
    public Affine2D LocalTransform;   // 24  the snapshot transform the AnimValue rows write (no live parent â‡’ this IS world)
    public RectF    Bounds;           // 16  dest rect (fly) / own rect (exit)
    public float    Opacity;          // 4
    public float    Sx0;              // 4   seed scale (corner-morph progress)
    public EffectAux Aux;             // 32  BlurSigma/Exposure/WashAlpha/CrossFade/Tint/Chain (cold, canon Â§4)
    public ImageHandle PinnedImage;   // 4   fence-pinned for the row's life (0 = none)
    public BrushHandle Fill;          // 4   non-image surfaces (card/text-header fly)
    public int      TrackHead;        // 4   head of this row's AnimValue NextOnNode chain
    public uint     SortKey;          // 4   z at detach time (depth-band insert in the second walk)
    public ushort   ElementTypeId;    // 2   drives the opcode the walk emits
    public byte     VisualKind;       // 1
    public DetachFlags Flags;         // 1   Exit|Shared|PopLayout|CrossfadeOnly|PinsImage|Reduced
    public int      Group;            // 4   index into DetachGroup slab (-1 = solo)
    // CornerMorph {src,dst} carried as a side-row keyed by handle if 96B is over budget
}
public struct DetachGroup {           // 16 B â€” the presence/connected completion gate
    public int  PendingCount;         // tracks not yet AtRest; reclaim the whole group at 0
    public uint Gen;                  // ABA guard
    public DetachMode Mode;           // Wait | Sync | PopLayout
    public NodeHandle StructuralAnchor; // the keyed-child boundary whose removal this group defers
}
public struct PresenceSpec {          // 16 B, interned â€” attached to a BoundaryKind.Presence node
    public TrackSeed Enter, Exit;     // from-snapshotâ†’identity ; currentâ†’terminal
    public DetachMode Mode;
    public ReducedMotionPolicy Reduced;   // default KeepFade
}
```

`AnimValue` rows target a `DetachedNode` exactly as a live node: the target is a tagged union `{ Live(NodeHandle) | Detached(Handle) }` packed into the row's identity (top bit of the node word = detached). `applyChannel` switches on the tag â€” `Live` writes `SceneStore.Paint(h)`, `Detached` writes `_slab.Ref(h)`. **No second integrator** â€” the scheduler ticks one `AnimValue` set; detached rows are just rows whose target resolves to the slab.

### 4.8 The authoring lowering PODs (Fork 2)

```csharp
[StructLayout(LayoutKind.Sequential)] public readonly struct Transition   // 12 B, interned-friendly
{ public readonly TransitionDynamics Dynamics; public readonly uint ChannelMask; public readonly ushort DelayMs; public readonly byte Flags; }

[StructLayout(LayoutKind.Sequential, Size = 16)] public readonly struct AnimTargetSet   // gesture/variant baked target
{ public readonly uint ChannelMask; public readonly int ValueStart; public readonly byte Count, GeneratorId, Flags, _pad; }
// values in a ChunkedArena<float> with TWO regions: immortal (variants/recipes) + per-mount ephemeral (inline While*)

public struct NodeInteract {          // 12 B side-row â€” replaces InteractionAnim
    public byte StateByte, StatePrev;          // resolver runs only when StateByte != StatePrev
    public short Exit, Drag, Pressed, Hover, Focus, InView;   // AnimTargetSet handles, index = priority slot
}
public struct RelativeFrame {         // 24 B side-row â€” Motion relativeTarget (FLIP coherence)
    public NodeHandle RelativeParent; public RectF OriginBox;
}
public readonly struct DepKey { /* net-new: 16 B, up to 4 packed scalars (long/int/float/handle), [InlineArray] for >4 */ }
```

`Transition` and `AnimTargetSet` reuse the **same `uint` channel mask** as `AnimChannel` (â‰¤32 channels â€” the hard ceiling; brush adds 8 lanes â†’ ~25, mask stays `uint`). `DepKey` is **net-new** (a canon primitive not yet in `src/`; today `DepsEqual` compares `object[]`) and is load-bearing for the alloc tripwire â€” it replaces the `params object[]` deps that box value-type deps and allocate per render.

### 4.9 Why it fits the SoA store

- The `AnimValue` slab is **dense** (a slab, not a per-node column) because animations are O(active animations), not O(nodes) â€” it mirrors `ScrollBindTable`'s sparse-side-table discipline (`_headByNode` is sized to animating nodes).
- Every *output* is an existing `NodePaint`/`EffectAux` field (`LocalTransform`, `Opacity`, `BlurSigma`, `PresentedW/H`, `ClipRect`, `StrokeTrim`, `OriginX/Y`) + the two net-new cold columns (`OwnerWord`, `InteractionScale`) and the cold `_colorAux`/`EffectAux` slabs â€” no hot per-node column bloat for the common (non-animating) node.
- The signal-source table, `NodeInteract`, `RelativeFrame`, `DetachedNode`/`DetachGroup`/`PresenceSpec`, and the two `AnimTargetSet` arena regions are all `ChunkedArena` dense + free-list, freed on node-free (`OnFreeIndex`) â€” reconciler-owned lifetime, zero per-frame growth, no LOH.

---

## 5. DSL / API â€” the final field set, the hooks, and the reconcile bake

The authoring surface is Fork 2's verdict verbatim: **Proposal B's lowering** (mask+arena `AnimTargetSet`, the `written`-mask priority resolver, the detach-group presence gate) under **Proposal A's field discipline** (one base-`Element` `Transition?`, alias `Layout`/`MorphId`, `Prop<T>`-template hooks). Ten declarative fields + `MorphId` (unchanged) + two `DepKey` hooks replacing four vestigial ones. Every construct lowers to exactly one of two sinks; neither allocates per frame.

### 5.1 The two sinks

- **Sink A â€” the `Prop<T>` mount-`Effect`** (`Reconciler.cs:876-922`): a bound channel installs one closure *once at mount* (`AddBinding(node, new Effect(...))`), reused for the component's life â€” the established zero-steady-state-alloc idiom (`Transform`/`Fill`/`Opacity`/`Width` already do this). `UseSpringValue`/`UseAnimatedValue(signal)` add one effect that calls `spine.Retarget` on a `DepKey` edge.
- **Sink B â€” a spine seed at the reconcile edge** (`Reconciler.cs:1724`, the enter-seed site generalized): the bake walks the element's authoring PODs once per reconcile of that node and writes `AnimTargetSet` rows + `NodeInteract` wiring + `Enter`/`Exit` `PresenceSpec` into the slabs. Cold-path GC allowed (reconcile edge); per-frame is zero.

### 5.2 The bound `Element` field set (base `Element` unless marked)

| Field | Type | Sink | Subsumes / deletes (file:line) |
|---|---|---|---|
| `Transition?` | `Transition` (12B) | A + column-diff | `BrushTransitionMs` (`Element.cs:243,422`), `Button.cs:90,172`; `BrushAnim`+`AdvanceBrushAnims` (`SceneStore`) |
| `WhileHover?` | `MotionTarget`â†’`AnimTargetSet` | B | `HoverScale`/`HoverOpacity`/`HoverFill`/`HoverDurationMs`/`HoverEasing` (`Element.cs`) |
| `WhilePressed?` | `MotionTarget` | B | `PressScale`/`PressedOpacity`/`PressedFill` |
| `WhileFocus?` | `MotionTarget` | B | ad-hoc focus chrome |
| `WhileDrag?` | `MotionTarget` | B | (net-new â€” gesture grips/sliders are first-class; one `StateByte` bit + slot) |
| `WhileInView?` + `InViewMargin` | `MotionTarget` + `float` | B | the declarative form of `UseEntrance`/`UseSoftReveal`; folds the reduced-motion read into the resolver |
| `Enter?` / `Exit?` | `EnterExit` (**reused** `Foundation/LayoutTransition.cs`) | B | presence; recipe enter terminals; `scene.Orphan` exit |
| `Stagger?` | `Stagger` POD (on `ForEl`/`Show` only) | B | `PopInStaggered`/`SoftRevealStaggered`; Motion O(nÂ²) `indexOf` |
| `Layout?` | `LayoutTransition` (**alias** of the existing `Animate` field) | host FLIP | the friendly FLIP/shared-element spelling â€” NO new machinery |
| `VariantCascade` | `byte` (0 = none) | reconcile walk | the *entire* variant feature (rejects B's `Variants`/`AnimateTo`/`CascadeRoot` 3-field expansion) |
| `MorphId` | `string?` (**unchanged**) | registry | shared-element tag; composes with `Layout` |

**The single most important call (Fork 2):** do **NOT** introduce B's `Initial`/`Animate`/`Exit` triad â€” `BoxEl.Animate` already means `LayoutTransition?` (FLIP). Reusing that token for "resting target-set" is the parallel-concept drift the brief forbids. `Enter`/`Exit` reuse the shipping `EnterExit` POD verbatim; the resting target is **implicit** (the channel values the element declares + `Transition` drive it â€” SwiftUI-implicit, not Framer-explicit `animate=`), keeping "signals-first implicit core" honest.

`MotionTarget` is the author-facing sugar (a fluent builder, up to 6 inline `(channel,value)` pairs via `[InlineArray]`, a 7th+ spilling to the arena); it bakes to the 16B `AnimTargetSet` (mask+arena). **Rejected** A's fixed-struct NaN-sentinel target â€” it doesn't extend to the 8 brush lanes without bloating every row; the mask+arena does for free.

### 5.3 The hooks (not `Element` fields, but bound here)

```csharp
// scalar-dep, no boxing, no array alloc â€” DepKey is the net-new 16B scalar struct
public void UseSpringValue (AnimChannel ch, float target, in SpringParams k,  DepKey deps);
public void UseAnimatedValue(AnimChannel ch, float target, in TransitionDynamics dyn, DepKey deps);
```

Both lower via Sink A: one effect at mount; on a `DepKey` change it calls `spine.Retarget(HostNode, ch, target, â€¦)` keeping `Pos+Vel`. **Replaces** the four `params object[]`-dep hooks (`RenderContext.cs:473-483`: `UseSpring`/`UseTransition`/`UseKeyframes`/`UseDrivenAnimation`) and **deletes** `UseAnimatedValue(float,â€¦)` + `AnimValueCell` (`RenderContext.cs:452-469,:66` â€” the 16ms-step vestige that only advances on re-render, is dt-blind, and fails the determinism replay). `UseDrivenAnimation`'s `a.Clocks.Register(source)` (`:483`, the `List<Func<float>>` leak) â†’ an index-based `SignalSource`. `object[]` boxing is the #1 authoring GC offender; the `DepKey` swap is load-bearing for the alloc tripwire (the `object[]` path stays only for cold non-motion hooks during migration, with an analyzer deprecation warning on the motion overloads).

### 5.4 Authoring examples per tier, and how each bakes

**Tier 1 â€” trivial (implicit, zero hooks):** a selected pill whose fill and lift cross-fade because `Transition` governs them.

```csharp
new BoxEl {
    Transition = MotionTok.ControlFaster,                       // 83ms-ish dynamics token
    Fill   = Prop.Of(() => selected.Value ? Tok.AccentFill : Tok.SubtleFill),
    ScaleX = selected.Value ? 1.04f : 1f, ScaleY = selected.Value ? 1.04f : 1f,
}
```
*Bake:* reconcile sees `Fill` bound â†’ one Sink-A effect; on `selected` flip the effect calls `Retarget(node, FillR..FillA, premulLinear(color), ControlFaster.Dynamics)`. `ScaleX/Y` are static but `Transition` governs them (mask = ALL) â†’ the reconciler column-diff routes them through `Retarget`. Four brush lanes + two scale lanes retarget on one slab; zero per-frame alloc.

**Tier 2 â€” common (declarative gesture states):**

```csharp
new BoxEl {
    Transition   = MotionTok.ControlFast,
    WhileHover   = MotionTarget.Scale(1.03f).Fill(Tok.SubtleHover),
    WhilePressed = MotionTarget.Scale(0.98f).Fill(Tok.SubtlePressed),
    WhileFocus   = MotionTarget.Border(Tok.AccentFill),
    OnClick = ...,
}
```
*Bake:* reconcile writes 3 `AnimTargetSet` rows (ephemeral arena region: Scale+Fill = 6 floats; Scale+Fill = 6; Border = 4) into `NodeInteract.{Hover,Pressed,Focus}`. Zero `AnimValue` rows seeded until a state bit flips. On hover-enter, dispatch sets `StateByte` bit0 + marks `_stateDirty`; the `written`-mask resolver (phase 7, pre-`Tick`) retargets ScaleX/ScaleY/Fill[RGBA] toward the Hover set. Press-while-hovering: bit1 wins Scale+Fill (higher priority) â†’ the Hover set's claim is masked out (no fighting). Release press: Scale+Fill revert to Hover's values (still in `written` of the Hover slot). Release hover: revert to the base declared value (the `everWritten & ~written` diff).

**Tier 3 â€” expressive (presence + stagger + variant cascade):**

```csharp
new ForEl<Track>(tracks, t => new RowComponent(t) {
    Enter = MotionTok.SlideUpFade,                              // EnterExit POD, keyed to logical identity
}) { Stagger = new Stagger(DelayChildren: 0, Each: 40, From: StaggerFrom.Start) }
```
*Bake:* the reconciler iterates the realized child window with `index` and `count` known; child `i` gets `Enter` seeded with `DelayMs = DelayChildren + i*40` baked into its `SeedEnter` call (`seedDelay = DelayChildren + staggerIndex(i,count,From)*Each` â€” O(1), no Motion O(nÂ²) `indexOf`, reusing the `LayoutTransition.DelayMs` bake). `Enter` is keyed to the bound-slot logical id, so a now-playing re-skin re-renders the window but does **not** re-seed (Rank-4 fix). A `VariantCascade` byte a parent stamps is carried down the reconcile walk as an inherited value; a child with a `MotionTarget[]` indexed by the cascade byte resolves `targets[n]` and retargets (the *entire* variant feature â€” no `FrozenDictionary` per element, no React context).

### 5.5 The lowering, summarized (binds Core Â§3/Â§4)

`MotionTarget` â†’ 16B `AnimTargetSet` (mask + two-region arena); the resolver = the `written`-mask arbiter over the fixed 6-slot order (`Exit > Drag > Pressed > Hover > Focus > InView`), phase-7 pre-`Tick`, O(state-changed nodes), zero alloc; presence = the detach-group `PendingCount` gate; brush = 8 premultiplied-linear lanes added to `AnimChannel` (enum â†’ ~25, mask stays `uint`). The imperative `MotionRecipes` keep their **tuned value bodies** but route their *delivery* through declarative props (`PopIn`/`SoftReveal`/`IconSwapIn`/`ScaleOpen`/`BadgePop`/`SuccessCheck` â†’ `Enter`/`Exit` + `Transition` as named `MotionTok` values; `Shake`/`SuccessCheck` `Keyframe[]` literals â†’ the immortal keyframe arena referenced by `{offset,count}`; looping `SkeletonPulse` â†’ an ambient `CadenceClass.Hz` row tied to the skeleton node's life). Multi-step `Sequence` stays **out of v1** (the one honest residual â€” it bakes to a small POD step-list the spine advances by index; `Stagger` + `Enter`/`Exit` + detach-group continuations cover the high-value motions).

One residual the resolver needs beyond the target-sets, stated plainly (Fork 2 / Core Â§4 cost): on release, `restingValue(n, ch)` is the node's declared static value per channel â€” bound channels read the `Prop` thunk (cheap), but a static channel's element record is gone by reconcile-transient time, so the resting value is **baked into a companion `float[] restingByChannel` per gesture-bearing node at reconcile** (one small ephemeral arena row per interacted node). This is the single place the resolver carries reconcile-baked state beyond the target-sets â€” called out, not hidden.

*(Sections 6â€“12 â€” Evaluation & the frame loop, the Subsumption proof, the Behavior catalog, Reduced-motion & a11y, Risks/open questions, the Phased implementation outline, and the Documentation Impact Map â€” are Part 2.)*

## 6. Evaluation & the frame loop

### 6.1 The composition model â€” the load-bearing decision (Fork 1, resolved)

This is the analogue of the ScrollBind doc's Â§6.1: the one structural call the rest of the doc hangs on. **The decision is phased, with a hard rule: Proposal B's owner column is the permanent substrate; Proposal A's single compose pass is the end state and *consumes* B.** B is a strict, independently-shippable subset of A's reconcile-time machinery, so building it first wastes nothing; A alone is too invasive for one flag-day; B alone leaves Rank-1 only half-dissolved. The migration boundary is drawn precisely below.

**Why this order, decisively.** A's structural fix *requires* a reconcile-time owner/priority table to (a) drive its DEBUG single-owner assert and (b) seed `BaselineFor`'s static-channel reconciler claims. That table is exactly B's `(nodeIndex, OwnChannel) â†’ OwnerTag` column plus B's reconcile-time claim discipline. The `Source`/`Priority` bytes A's contribution row carries and the `OwnerTag`/`OwnChannel` enums B introduces are the same vocabulary at two resolutions. Ship B â‡’ you have built A's reconcile substrate, validated under the real gates, before touching a single hot-path writer. The phasing is *free*.

**The two phases and their binding boundary:**

| | Phase 1 â€” the owner column (ship now) | Phase 2 â€” the compose pass (the rework proper) |
|---|---|---|
| **What** | Per-node packed `OwnerWord {uint Bits; uint Gen}` cold SoA column, gen-stamped lazy-clear; reconcile-time `Claim(node, OwnChannel, OwnerTag, Priority)`; a `[Conditional("DEBUG")]` assert that names a double-owner. **Two A-shaping amendments landed up front:** (1) every claim carries `Priority : byte` from day one (A needs it for the Framer 7-slot arbitration; B's enums alone don't have it); (2) **`NodePaint.InteractionScale` is segregated into its own real paint field now** (adopt Â§4.b's `InteractScale` as a paint field, not advisory), and `SceneRecorder` (`SceneRecorder.cs:205-214`) reads a precomputed scalar. | Build A behind `FG_COMPOSE_PASS`. One fold-and-write-once pass per touched node reads `(OwnerTag, Priority)` + the per-channel `AnimChannel` contributions and writes `LocalTransform`/`Opacity`/`Blur`/etc. **once** with one `Mark`. `Accum.FromPaint` (`AnimEngine.cs:111-137`), `_scratch` (`:166`), and the `DrivenClockTable` closure list (`:54-59`) are deleted **only at the green flag flip.** |
| **Risk** | LOW, purely additive. Deletes nothing; every change is a `[Conditional]` claim/assert + one segregated field. Independently shippable behind existing gates. | HIGH, flag-day-shaped â€” converted writer-by-writer in dependency order (below), gated, then the flag is removed. |
| **Dissolves Rank-1?** | Partially â€” converts silent last-writer-wins into a loud reconcile-time assert; the writers still write independently. | YES, structurally â€” collision becomes impossible: one fold, one write, one `Mark`. This is the real dissolution. |

**Phase-2 writer-conversion order (dependency-sorted, riskiest last):** `AnimEngine` + recorder first (mechanical â€” the fold already exists as `Accum.Fold`); then `ConnectedAnimation` â†’ the detached-slab path (disjoint overlay node, lowest risk â€” Â§3 already deletes it wholesale onto `DetachedAnimSlab`); then the scroll triad last (`ScrollBindEval` / `ScrollAnimator` / `OverscrollPhysics` â€” the hottest path, the 60fps fling gate, the `scroll-winui-parity-gap` harness lock, and the sticky change-gate inversion at `ScrollBindEval.cs:138`).

**The four invariants that bind the core design (carried verbatim from Fork 1):**

1. **The owner/priority column is permanent core infrastructure**, not a stopgap â€” it is A's reconcile substrate. Storage = the packed `OwnerWord` registered as a cold SoA scene column.
2. **Interaction scale is segregated into its own paint field, never `LocalTransform`** â€” adopted in Phase 1, preserved through Phase 2. Non-negotiable: hit-test (`InputDispatcher.cs:3759`) continues to invert `LocalTransform` only, so a hovered thumb's grown visual never grows its hit box. A reviewer who folds interaction scale into `LocalTransform` silently breaks this; the segregated field makes it impossible.
3. **The end state is A's single fold-and-write-once compose pass** â€” one write + one `Mark` per touched node, scalar-channel contributions, *no matrix decompose anywhere* except the cold reconcile `BaselineFor` on authored static binds.
4. **`OwnChannel` is the partition granularity** (5â€“7 physical sinks), but the contribution rows carry the finer 17â†’25-entry `AnimChannel` for the scalar fold. The owner column is keyed by *physical sink*; the contribution by *axis*. This dual resolution is intentional and is kept consistent in the canon (`backdrop-effects-animation.md Â§5`).

### 6.2 The scheduler wake â€” `min(next-due)`, not `any(HasActive)`

The wake decision (Core Â§2) replaces the entire `ComputeWakeReasons()` 16-bool OR (`AppHost.cs:428-455`) and the `RecommendedWaitMsCore()` ambient/grace/HUD branch tree (`:372-388`). The host wait becomes:

```
double anim = _sched.NextDueMs(now);            // 0 = present-now, +INF = no timed anim
int residual = ResidualNonAnimWait();           // input(-1 idle) | images(0) | DynamicText HUD(Hz(10)) | reconcile-pending(0)
int w = anim == 0 ? 0 : min(ceil(anim), residual == -1 ? int.Max : residual);
_lastWaitWasIdleOrThrottle = (w != 0);          // any nonzero wait â†’ next frame is a resume â†’ FrameClock idle guard
return w == int.Max ? -1 : w;                   // -1 = block on a message
```

`NextDueMs` is a single linear scan over the dense active-set returning the soonest `_dueMs[slot] - now` across `Alive âˆ§ !Quiesced` sources, **skipping `Driven` sources** (they are event-woken by the signal write that calls `WakeFrame()`, never timer-due). The math falls straight out of the per-source `CadenceClass`:

- a lone 30Hz shimmer â‡’ `anim â‰ˆ 33ms`;
- add a live spring â‡’ `anim = 0` (`DisplayRate` present-now);
- a **paused playhead** â‡’ its source is `Driven`, skipped, contributes `+INF` â€” the loop blocks until the media-clock signal write wakes it. **Zero frames for a paused seek-bar, structurally, with no exemption list.** This is the principled replacement for `AmbientAnimationFps + AnimIsAmbient() + scroll-grace + LatencySensitiveWake` all at once â€” each was a heuristic *approximating* "does this source need a frame right now," which the `Driven`/`Hz` cadence answers as data.

The linear scan over ~tens of sources is cheaper than a min-heap's pointer-chasing and trivially zero-alloc; start linear, promote to a `NextDueMs`-keyed heap only if a profile demands it.

> **DEFERRAL (scroll-perf remediation, 2026-07):** this Cadence/`NextDueMs` model stays **deferred** — the as-built wake
> is still `ComputeWakeReasons` + the ambient-cap branch. The scroll-perf pass shipped two bounded interim fixes instead:
> (1) the ambient-FPS cap now also defers through the 0.45s post-scroll hold (`_mainScrollHoldUntil`) so slow wheel-notch
> scrolling over an ambient loop no longer oscillates 30Hz↔display-rate per notch (the step-up `Resync` lurch), and
> (2) the slab gained an intrusive active-node chain + a `Version`-memoized `LoopTrackCount`/`DisplayRateActive` census,
> so the repeated per-frame `ComputeWakeReasons` calls stop re-scanning every row. Both are strictly smaller than — and
> compatible with — this section: if ambient CPU still matters after them, the per-source cadence lands here as designed.
> As-built notes: `design/subsystems/backdrop-effects-animation.md` §5 (the two as-built paragraphs under the rework banner).

### 6.3 The FrameClock â€” determinism is a property of the clock, not of every animator

`AnimScheduler.RunFrame` produces one 24B `FrameClock` per frame and passes it by `in` to every tick. Its single load-bearing rule (Core Â§2.1, honoring dossier open-q 3/Â§7e):

> **`NowMs` advances by the *clamped* delta (1..40ms), never by raw wall-clock.** A 200ms GC stall advances `NowMs` by 40ms, not 200ms; a post-idle/throttle resume forces `delta = 1000/60` (`useDefaultElapsed`).

Because every `Generator` is sampled at absolute `NowMs`, and `NowMs` is a deterministic running sum of clamped quanta, a generator's trajectory is **bit-identical under the dt âˆˆ {8.33, 16.67, 33.3} replay gate** by construction. The headless replay injects `wallNowMs := lastNow + dtFixture` with `wasIdleOrThrottled = false`, so `delta == dtFixture` exactly â€” `useManualTiming` 1:1. This is *why* the per-track `JustSeeded` first-frame-skip + `DelayRemainingMs` defense (`AnimEngine.cs:81,621-634`) and the host `Resync()` lurch patch (`:1114-1119`) are **deleted, not ported**: they existed only to hide first-frame dt noise the clock now eliminates.

### 6.4 The per-frame loop â€” phases 6.5 / 7 / 13

Animation occupies three slots in the 13-phase loop, all UI-thread (the render thread, post-PUBLISH, only records the value-copied columns):

```
phase 6.5 (UI, after layout, before scheduler tick):
  (1) relativeTarget pre-pass â€” depth-ordered (parents first) Retarget over BoundsAnimated coherence groups (Â§3.4/Â§6.7)
  (2) presence enter      â€” newly-realized child under a Presence boundary with no EnterEpoch[logicalKey] â†’ seed Enter, record epoch
  (3) shared-element dest â€” SharedElementRegistry.TryTake(key) â†’ Detach snapshot + seed spring/fade to dest rect; HideDest once
  (4) content-size/placement â€” prev-WorldBounds vs new Bounds â†’ seed transform-to-identity (canon Â§5.8, unchanged)

phase 7 (UI, the ONE AnimScheduler tick â€” Â§6.2):
  clock = FrameClock.Advance(wallNow, resumedFromIdle)
  signals.SampleAll()                                  // refresh driven-signal epochs once; mark changed Driven sources due
  PASS 1 (advance): for each active AnimValue row in the dense NextActive chain (NOT a List scan):
     if !IsLive(Node): Free(slot); continue            // gen-checked self-prune
     if Parked: continue
     if Driven: Position = SampleKeyframes(SampleSignal(DrivenSrc) remapped)   // index switch, no closure
     else: ElapsedMs += stepMs; s = Generator.Eval(in Gen, GenKind, From, To, ElapsedMs)
           Position = s.Value; if GenKind==Spring: Velocity = SpringVelocity(...); if s.Done: Flags |= Done
  PASS 2 (compose): per touched node â€” Phase-1: fold _headByNode chain (Replace then Additive), seed Accum.Default (NOT FromPaint)
                                       Phase-2: the single compose pass reads (OwnerTag,Priority)+contributions, writes ONCE
     scene.Mark(node, TransformDirty | PaintDirty)      // NEVER LayoutDirty except the LayoutW/H Reflow opt-in
  PASS 3 (free): Done rows â†’ settle restores (Reflow LayoutInput, reveal NaN reset), free to the free-list

phase 13 (UI cleanup â†’ render Retire):
  for each DetachGroup g with all rows AtRest:
     Retire each row (free AnimValue rows; fence-unpin image; ReconcilerCommitRemoval(anchor) if g.Mode != PopLayout)
```

The interaction-state resolver (Â§4.b) runs **inside phase 7, before PASS 1** â€” `ResolveInteractionStates()` over `_stateDirty` nodes issues `Retarget` calls that seed/retarget the `AnimValue` rows the same pass then advances. This ordering is mandatory and couples to Â§6.1: **sources/resolvers only ever write the `AnimValue` slab; the compose pass (Phase 2) or the fold (Phase 1) is the sole writer of `NodePaint`.** No source writes `NodePaint` from inside its tick â€” that is the structural rule that makes the scheduler's tick order safe (it dissolves Rank-1's "phase-7 call order is the de-facto arbiter").

### 6.5 The generator `Eval` â€” pure, by-value, sampled at absolute `t`

One pure function evaluates all four generator kinds; `cur`/`to`/`tMs = ElapsedMs` come from the `AnimValue` row (Core Â§1.2):

```
Sample Eval(in Generator g, byte kind, float from, float to, float tMs):
  Eased:    u = clamp01(tMs/DurationMs); e = KeyCount>=2 ? SampleKeyframes(...) : from+(to-from)*Easings.Ease(EaseId,u); done = u>=1
  Spring:   EvalSpringAnalytical(in g, from, to, tMs)        // Â§6.6 â€” closed form, NO sub-stepping
  Inertia:  f = exp(-DecayKÂ·tMsÂ·1e-3); pos = from + V0Â·(1-f)/DecayK; done = |V0Â·f| <= 8px/s   (CoastStep closed form)
  Keyframes: as Eased KeyCount path, Loop-aware (u -= floor(u))
```

`Sample` is a `readonly struct (float Value, bool Done)` returned **by value** â€” killing Motion's shared-mutable `{value,done}` aliasing footgun. All param resolution (the Newton duration-solve `findSpring`, `visualDuration/bounce`) happens in `BakeGenerator(...)` at reconcile, writing `Omega/Zeta/A/B`; `Eval` never solves, parses, iterates, or allocates. The advance is `ElapsedMs += stepMs; Position = Eval(...).Value` â€” **no accumulator on `Position`, no dt in the spring math** (`ElapsedMs` is the only state advanced, and it is the injected `dtMs` summed).

### 6.6 The analytical spring + why the determinism switch is a *correctness fix* (dossier open-q 8, resolved)

The shipping critically-damped closed form already lives in `OverscrollPhysics.StepSpring` (`:145-153`); generalize to three regimes baked at seed into `{Omega, Zeta, A, B}` with `x0 = Position âˆ’ To`, `v0 = Velocity`:

- **Under-damped (Î¶<1):** `Ï‰d = Ï‰âˆš(1âˆ’Î¶Â²)`; `A = x0`, `B = (v0 + Î¶Ï‰Â·x0)/Ï‰d`; `value(t) = To + e^(âˆ’Î¶Ï‰t)(AÂ·cos Ï‰dÂ·t + BÂ·sin Ï‰dÂ·t)`; `velocity(t)` = its exact derivative.
- **Critically-damped (|Î¶âˆ’1|â‰¤1e-4):** `A = x0`, `B = v0 + Ï‰Â·x0`; `value(t) = To + (A + BÂ·t)Â·e^(âˆ’Ï‰t)`.
- **Over-damped (Î¶>1):** roots `r1,2 = âˆ’Ï‰(Î¶ âˆ“ âˆš(Î¶Â²âˆ’1))`; `value(t) = To + AÂ·e^(r1Â·t) + BÂ·e^(r2Â·t)`. **Footgun guard (dossier Â§7d):** clamp `min(Ï‰Â·t, 300)` before `exp` or a stiff over-damped spring NaNs past ~300 e-folds.
- **Rest test, per-channel (dossier Â§7d / `OverscrollPhysics:169`):** `Done` when `|To âˆ’ value| â‰¤ RestDelta âˆ§ |velocity| â‰¤ RestSpeed`, with transforms/scale/opacity `RestDelta â‰ˆ 1e-3` (sub-pixel transforms never settle at the 0.5px scroll threshold) and presented-size `â‰ˆ 0.5px`; snap exactly to `To`, zero velocity (gates that assert `final == 0` hold).

**This is a latent gate hole closed, not an epsilon cleanup.** The Euler integrator's sub-step count `n = clamp(ceil(dt/4ms),1,8)` (`AnimEngine.cs:648`) makes the trajectory **dt-path-dependent** (dt=8.33â†’n=3 vs dt=33.3â†’n=8 produce *different* `Position` sequences for the same elapsed time). The headless replay gate asserts *identical* traces â€” so any spring exercised by it would **diverge, not pass-by-epsilon**. The reason it has not fired: **the dt-replay gate does not currently seed a spring track** (the harness exercises only the integrator's transform path, per the ScrollBind doc's R3). So today's springs are *unverified under dt-replay and would fail if added*. The analytical form sampled at absolute `t` is bit-stable across any dt chunking by construction (`value(t)` reads only `t = Î£ stepMs`, never the chunk boundaries) â€” switching **closes the hole AND lets the gate finally cover springs.** Mandatory new gate: seed an under-damped spring, replay at the three dt's, assert identical `Position`/`Velocity` traces.

### 6.7 Retarget / velocity handoff â€” rebase, do not frame-shift (dossier open-q 2, resolved)

On any retarget (signal-fed new `To`, an interrupting tween, or a gesture-state edge):

1. Read the **current** `value(ElapsedMs)` and `velocity(ElapsedMs)` from the *existing* generator â€” analytical, exact, no second sample (Motion's "sample twice at t and tâˆ’10ms" handoff becomes one `Eval` + the derivative).
2. Write `Position := value`, `Velocity := velocity`, `To := newTarget`, `ElapsedMs := 0`, re-bake `{A,B}` from the new `(x0 = Position âˆ’ To, v0 = Velocity)`. Spring keeps velocity (handoff); eased re-seeds `From := Position` (optionally reverse-shortened â€” **never both handoff and reverse-shorten**, CSS L1).

**Why rebase, not the existing `ReframePosition` frame-shift (`AnimEngine.cs:573`):** the analytical form is parameterized by `(x0, v0, t)` from a *fixed origin* â€” you cannot "shift" it without recomputing `{A,B}`, so retarget *must* reset `ElapsedMs=0` and rebake. The FLIP frame-shift at `:573` re-expresses as: rebase with `Position` already including the layout delta (`Position := value + (fromAbs âˆ’ toAbs)`, `To := 0`, velocity carried) â€” velocity-continuous, identical visual result, now analytical. **Determinism tolerance:** rebase introduces a deliberate trajectory change *at the retarget instant*, but it is deterministic given the same retarget time, and retargets are driven by signal edges that are themselves dt-invariant (a signal change happens at the same logical frame regardless of dt). There is no "old vs new" float-rounding to reconcile because the analytical form was never in the gate (Â§6.6). The new spring gate must seed-then-retarget mid-flight and assert the post-retarget trace is identical across the three dt's â€” which holds because `(Position, Velocity)` at the retarget frame are computed from absolute `t` (bit-stable) and the rebake is pure arithmetic.

### 6.8 Why it stays zero-alloc on phases 6â€“13

- **No closures.** Driven sources read a signal by `SignalSource` index `switch` (Core Â§2.4), never `Clocks.Register(() => â€¦)`. The `DrivenClockTable` `List<Func<float>>` leak (`AnimEngine.cs:54-59`) is *deleted*, not refactored.
- **No per-tick dictionary.** The `_scratch` `Dictionary<NodeHandle,Accum>` (`:166`) is deleted; the fold walks `_headByNode` intrusive chains. The slab is `ChunkedArena`-backed, free-list-recycled; `Find`/`Get`/`SetNodeParked`/`HasTracks` go O(all-tracks)â†’O(node's-chain) (â‰ˆ1â€“4).
- **Managed delegates are edge-only.** Presence `OnFlag`-style callbacks, the observer escape hatch, and theme/reduced-motion epoch reads fire only on an edge (a state flip, a projected-key change) â€” the documented "GC at the reconcile/edge is allowed" rule.
- **The one new alloc hazard the tripwire must cover (Fork-1 Proposal-A):** `AnimValueSlab.Alloc()` grows `_rows` (`Array.Resize`) the first time an animation count is hit. This is a reconcile-edge alloc (allowed) *only if* seeding routes through reconcile; an imperative `Spring(...)` mid-frame that grows the slab trips the tripwire. Mitigation: size the slab at reconcile to `liveNodes Ã— maxConcurrentChannels` headroom; assert in DEBUG that `Alloc()` never grows during phases 6â€“13.

---

## 7. Subsumption proof

### 7.1 The honest table (SUBSUMED / RETAINED / DELETED)

Mirroring the ScrollBind doc's Â§7 and the dossier's Â§8 â€” what *genuinely* collapses into the unified pipeline, what keeps a privileged branch, what is removed.

| Current mechanism | Verdict | Reason |
|---|---|---|
| `AnimEngine` Eased/Spring/Driven tracks (`class Track` + `List<Track>` + `Find`/`Get` scans + `_scratch` dict) | **SUBSUMED** â†’ `AnimValue` 64B slab rows + `Generator` POD + `(node,channel)â†’head` index | The value laws stay; the container model is the root of every alloc/throughput/stale-field footgun, and is the `AnimTrack`-in-`SlabAllocator` the canon already mandated but no one built. Landing the spec, not re-patching. |
| Sub-stepped semi-implicit Euler spring (`AnimEngine.cs:646-658`) | **DELETED** â†’ the analytical closed-form spring (3 regimes) | `n = clamp(ceil(dt/4ms),1,8)` is dt-path-dependent â†’ fails the replay premise; the closed form sampled at absolute `t` is bit-stable and yields exact `velocity(t)` for handoff (Â§6.6). |
| `Accum.FromPaint` lossy matrix decompose (`:111-137`) | **DELETED** â†’ the slab is the per-channel source of truth | It existed only to recover un-animated channels from the live matrix (rotation reads 0 `:118`; scale polar-vs-raw `:116-117`). With the slab, a node's live channels *are* its `NextOnNode` rows; un-animated channels retain their last column value â€” nothing to recover, nothing to decompose. Dissolves Fork-1 fact #3. |
| `DrivenClockTable` (`List<Func<float>>`, never deregisters) | **DELETED** â†’ index-based `SignalSource` rows (`SignalKind {ScrollOffset, MediaClockMs, SignalFloat, NodeChannel}`) | The closure model leaks and is AOT-hostile; a driven row samples a signal handle/column by index, recycled on teardown. |
| `InteractionAnimator` (whole; hover/press progress scalars) | **DELETED** â†’ the `written`-mask priority resolver + baked `AnimTargetSet`s + the `NodeInteract` byte | It is a hardcoded 2-state instance of Framer's 7-slot priority machine; generalizes to the n-state resolver writing the same channels. |
| Recorder hover-scale/brush composite (`SceneRecorder.cs:205-214`) | **DELETED** â†’ scale/brush are spine channels folded by the one compose pass; recorder reads the segregated `InteractionScale` field (Phase 1) | The separate record-time multiply was the recorder coordinating one writer; the compose pass folds all writers once. Hit-test still reads `Bounds`/`LocalTransform` only (Â§6.1 invariant 2). |
| `BrushTransition`/`BrushAnim` side-table (`SceneStore.cs:119`, `AdvanceBrushAnims :515`) + `BoxEl/TextEl.BrushTransitionMs` + `Button.cs:90,172` | **SUBSUMED** â†’ `AnimChannel.Color` (8 premul-linear lanes) + the `Transition` field; `Button` emits `Transition = MotionTok.ControlFaster` | "Brush is just another animatable channel" (decided call 2). Fill/Color become channels; the implicit `Transition` governs their cross-fade. The hardcoded 83ms `BrushTransition` is the first thing subsumed. |
| `ConnectedAnimation.cs` (entire, 506 lines: `CreateNode(8)` live overlay, string-keyed dicts, per-frame cull-reapply, settle heuristic, `MaxFlightFrames` wedge, `ComputeFlyClip`, `DestMoved` chase) | **DELETED** â†’ a `DetachedAnimSlab` row + `SharedElementRegistry` + a fromâ†’to critically-damped spring (`Mot.ConnectedFly`) | A detached snapshot composed against the dest's live transform dissolves the KeepAlive/recycled-slot coupling, the cull, the band-clip, the wedge, and the settle heuristic; interruption = `Retarget` the running spring; reverse/Back is free (symmetric Register/TryTake). |
| Exit animation via `scene.Orphan` (live orphaned node) + `HasTracks`-poll reclaim + 2000ms backstop (`SceneStore.cs:357-384`, `AppHost.cs:1546`) | **DELETED** â†’ `DetachedAnimSlab.Detach` + the `DetachGroup.PendingCountâ†’0` completion gate | Keeping a live recycled-slot node alive is the canon's explicitly-rejected approach; the value-copied snapshot is the seam-safe form. |
| `SceneStore` overlay band-clip (`_overlays`/`OverlayClip`/`AddOverlay`/`OverlayCount`, `:402-422`) + `SceneRecorder.cs:78-125` overlay loop | **SUBSUMED** â†’ the single detached second-walk `RecordDetached()` with the clip baked at detach | One detached set, one second walk, one clip-snapshot â€” not a separate overlay list. |
| The 6+ phase-7 tickers (`AnimEngine`/`InteractionAnimator`/`ConnectedAnimation`/`RepeatTicker`/`CaretBlinker`/`ScrollAnimator`/`AdvanceBrushAnims`) each re-inventing arm/disarm/`HasActive` | **SUBSUMED** â†’ one `AnimScheduler` active-set dispatched by `SourceTag`, one `CadenceClass` per source | The 16-bool `WakeReasons` union + per-ticker `HasActive`/arm/prune collapse into one tagged, swap-buffered active-set with `min(next-due)` wake. |
| `AmbientAnimationFps` global cap + `AnimIsAmbient()` + `_scrollGraceUntil` + `LatencySensitiveWake` (`AppHost.cs:314-327,380,398`) | **SUBSUMED** â†’ per-source `CadenceClass {DisplayRate, Hz(n), Driven(slot), OneShot, Paused}` | The global heuristic + exemption pile become a per-source declared cadence; `Driven` self-gates to zero frames when idle; `AmbientAnimationFps` degrades to the *default* `Hz` for ambient sources. |
| The N-uncoordinated-`NodePaint`-writers convention (`AppHost.cs:1121-1137` call-order arbiter) | **SUBSUMED** â†’ the owner column (Phase 1) â†’ the single compose pass (Phase 2) | Disjoint-storage + call-order-arbitration become an enforced ownership partition, then one fold-and-write (Â§6.1). |
| `UseAnimatedValue(float, â€¦)` (steps `Elapsed += 16f` per re-render) + `AnimValueCell` (`RenderContext.cs:66,452-469`) | **DELETED** â†’ `UseAnimatedValue(AnimChannel, target, in TransitionDynamics, DepKey)` (signal-fed, spine-driven) | A strictly-worse fourth mechanism: render-cadence-coupled, dt-blind, 16ms literal fails replay. Callers (`PagedShelf`/`ScrollBar`/`PageHeader`/`AnimationPage`) migrate to the spine. |
| `UseSpring`/`UseTransition`/`UseKeyframes`/`UseDrivenAnimation` with `params object[] deps` (`RenderContext.cs:473-483`) | **SUBSUMED** â†’ two `DepKey` hooks (`UseSpringValue`/`UseAnimatedValue`) | `object[]` boxes value-type deps + allocates the array every render â€” the #1 authoring GC offender; `DepKey` (a 16B scalar struct, net-new in `src/`) compares a struct. |
| `Dsl.Motion` (`Fast=150`) vs `Dsl.Expressive` (`Fast=250`) vs `Animation.MotionSprings` (3 colliding token namespaces) | **SUBSUMED** â†’ one `MotionTok` registry (`MotionTokenId` â†’ `MotionTokenDef` per-theme `FrozenDictionary`) | `Motion.Fast != Expressive.Fast` is a live `using`-scope footgun; one themeable, reduced-motion-aware table removes it. |
| Imperative recipe library (`MotionRecipes` PopIn/Shake/SoftReveal/â€¦, ~15) | **RETAINED as value bodies, SUBSUMED as authoring path** | The tuned curves stay as named `MotionTok`/`EnterExit` values; delivery routes through declarative `Enter`/`Exit`/`Stagger`/`Transition` baked at reconcile, deleting the `NodeHandle`-capture/`OnRealized`/`Context.Anim`-null boilerplate. `Keyframe[]` literals (`Shake :80-86`) move to the immortal keyframe arena. |
| FLIP move (`AppHost.CaptureProjections`/`ApplyProjections` + `LayoutTransition.Position` + `ReframePosition`) | **RETAINED** (host-driven), extended with `relativeTarget` + the analytical-rebase handoff | The parent-relative capture/diff/seed-delta loop is correct and honors every constraint; only the nested-coherence refinement (`relativeTarget`) and the virtualization guard are added (Â§3.4). |
| `LayoutTransition` interned POD + `EnterExit` (incl. `EnterExit.Blur`, FA-3) | **RETAINED** verbatim | A clean, closure-free, orthogonally-composing declarative spec; `Enter`/`Exit` *reuse* it (Â§4 â€” do NOT introduce a new `Initial` field). |
| `Foundation.Easing` named-curve Newton-bezier set | **RETAINED**, extended with the `linear()` sampled-LUT + Back/Circ/Anticipate | The curve math + solver are stronger than Motion's; the gaps are a few named curves + arbitrary `linear()` for the cheap non-interruptible eased path. |
| `SkeletonDeriver` (derive shimmer from the real template) | **RETAINED** | The one-source-tree derived skeleton is a genuine win; reconcile-edge GC (allowed), not hot-path. |
| `MirrorParticipation` (7 hand call-sites, `Reconciler.cs:507-512`) | **SUBSUMED** â†’ a solver-native `BoundaryKind`/`LayoutTransparent` column the solver honors at measure time | Out of this rework's *core* scope (it's a layout-solver change, Rank 6), but the presence boundary (Â§3.2) reuses the same `BoundaryKind.Presence` byte â€” flagged as the shared seam to land together. |

### 7.2 The `BrushTransition` worked re-expression (the decided-call-2 proof)

The hardcoded brush path is the cleanest demonstration that "a control is just another animatable surface." **Today** (`Button.cs:90,172`):

```csharp
BrushTransitionMs = 83f                                   // a control-internal motion path
// â†’ SceneRecorder.ResolveSurface lerps Fill/Border via InteractionAnim.HoverT, a SEPARATE ColorF.LerpLinear
// â†’ SceneStore.BrushAnim side-table + AdvanceBrushAnims ticker advances HoverT/PressT
```

**After** â€” the control declares targets and an implicit transition; there is no control-internal motion path:

```csharp
new BoxEl {
    Transition   = MotionTok.ControlFaster,                       // 83ms-feel dynamics token; governs ALL declared channels
    Fill         = Prop.Of(() => selected.Value ? Tok.AccentFill : Tok.SubtleFill),
    WhileHover   = MotionTarget.Scale(1.03f).Fill(Tok.SubtleHover),
    WhilePressed = MotionTarget.Scale(0.98f).Fill(Tok.SubtlePressed),
}
```

**Bake-to-seed (zero-alloc, no closures):** reconcile installs one `Prop<T>` mount-`Effect` for `Fill` (Sink A). On a `selected` flip the effect calls `Retarget(node, FillR..FillA, premulLinear(color), ControlFaster.Dynamics)` â€” the four brush lanes retarget on the slab from their *current* premul-linear value (the cross-fade), velocity-continuous if a hover transition was mid-flight. `WhileHover`/`WhilePressed` bake to two `AnimTargetSet` rows (ephemeral arena: Scale+Fill = 6 floats each) stored in `NodeInteract.{Hover,Pressed}`; **zero rows seeded until a state bit flips.** On hover-enter, input dispatch sets `StateByte` bit0 and marks `_stateDirty`; the resolver retargets ScaleX/ScaleY/Fill[RGBA] toward the Hover set. Press-while-hovering: the higher-priority Pressed slot's `claim = ChannelMask & ~written` wins Scale+Fill, the Hover set's claim is masked out â€” *no fighting writers*. Release press: Scale+Fill revert to the Hover values (still in the Hover slot's `written`); release hover: revert to the base declared value via the `everWritten & ~written` diff. The recorder no longer composites brush or scale; the compose pass (Â§6.1) folds them. **`BrushTransition`, `BrushAnim`, `AdvanceBrushAnims`, the recorder brush-lerp, and the separate `ColorF.LerpLinear` are all gone** â€” Color is a channel, linear-premultiplied lerp, with the text-gamma exception applied at draw by the recorder (Core Â§1.e).

### 7.3 The deletion checklist (file:line â†’ replacement)

| Deleted / rewritten | File:line | Replaced by |
|---|---|---|
| `class Track` (33-field heap ref) + `List<Track> _tracks` | `AnimEngine.cs:69-102,165` | `AnimValue` 64B slab row + `AnimValueSlab` (`ChunkedArena` + free-list) |
| `Accum.FromPaint` lossy decompose | `AnimEngine.cs:111-137` | slab-as-source-of-truth; `Accum.Default` seed |
| `_scratch` `Dictionary<NodeHandle,Accum>` | `AnimEngine.cs:166` | `_headByNode` intrusive-chain fold |
| `DrivenClockTable` `List<Func<float>>` | `AnimEngine.cs:54-59` | `SignalSourceTable` (index-based) |
| `Find`/`Get` linear scans | `AnimEngine.cs:585-601` | `HeadOnNode(nodeIdx)` chain walk + channel filter |
| `JustSeeded` first-skip + `DelayRemainingMs` defense | `AnimEngine.cs:81,621-634` | `FrameClock` clamp + idle guard (Â§6.3) |
| sub-stepped Euler spring block | `AnimEngine.cs:646-658` | `EvalSpringAnalytical` (3 regimes, Â§6.6) |
| `SetNodeParked`/`HasTracks` O(tracks) scans | `AnimEngine.cs:182-189,546-550` | `_headByNode` chain walk + O(1) census counters |
| `ComputeWakeReasons()` (16 `HasActive` ORs) | `AppHost.cs:428-455` | `_sched.NextDueMs()` + residual mask |
| `RecommendedWaitMsCore()` ambient/grace/HUD tree | `AppHost.cs:372-388` | `min(NextDueMs, residual)` (Â§6.2) |
| `AnimIsAmbient()` / `LatencySensitiveWake` / `_scrollGraceUntil` | `AppHost.cs:398,320-327,314-316,380` | per-source `CadenceClass` |
| `_lastWaitMs` + `Resync()` lurch | `AppHost.cs:312,1114-1119` | `FrameClock` clamped `NowMs` + idle guard |
| 6+ independent phase-7 `Tick`/`HasActive` tickers | `AppHost.cs:437-448,1121-1137` | one `_sched.Tick(clock)` dispatch by `SourceTag` |
| `FrameTimeSource` / `ManualFrameTimeSource` | `FrameTimeSource.cs` (whole) | `FrameClock` / `FrameClockSource` |
| `ConnectedAnimation` (entire) | `Animation/ConnectedAnimation.cs:1-506` | `SharedElementRegistry` + `DetachedAnimSlab` rows + `Mot.ConnectedFly` |
| `InteractionAnimator` (entire) | `Animation/InteractionAnimator.cs` | `written`-mask resolver + `NodeInteract` + `AnimTargetSet` |
| `SceneStore.Orphan`/`ReclaimOrphan`/`IsOrphan`/`OrphanCount` | `SceneStore.cs:357-384` | `DetachedAnimSlab.Detach`/`Retire` |
| `SceneStore` `_overlays`/`OverlayClip`/`AddOverlay`/`OverlayCount`/`OverlayAt` | `SceneStore.cs:57,402-422` | detached second-walk with baked clip |
| `SceneStore` `BrushAnim` side-table + `AdvanceBrushAnims` | `SceneStore.cs:119,510-518` | `AnimChannel.Color` spine rows |
| `HasTracks`-poll orphan reclaim + 2000ms backstop | `AppHost.cs:1546` | `DetachGroup.PendingCountâ†’0` gate |
| `SceneRecorder` overlay loop + `ConnectedOverlay` cull + `OverlayClip` read | `SceneRecorder.cs:78-125,100,122` | `RecordDetached()` over the slab (Â§3.1) |
| recorder hover-scale/brush multiply | `SceneRecorder.cs:205-214` | compose pass folds; recorder reads segregated `InteractionScale` |
| `BrushTransitionMs` (Box + Text) | `Dsl/Element.cs:243,422` | `Transition` field |
| `Hover/PressScale` + `Hover/Pressed{Opacity,Fill}` + durations/easings (Box+Image, drifting) | `Dsl/Element.cs:55,212,233,374` | `WhileHover`/`WhilePressed` `MotionTarget` |
| `Dsl/Motion.cs` + `Dsl/Expressive.cs` (token classes) | whole files | `MotionTok` registry (`MotionTokenId` â†’ `MotionTokenDef`) |
| `UseAnimatedValue(float)` + `AnimValueCell` | `Hooks/RenderContext.cs:66,452-469` | spine-driven `UseAnimatedValue(AnimChannel,â€¦,DepKey)` |
| `UseSpring`/`UseTransition`/`UseKeyframes`/`UseDrivenAnimation` (`object[]` deps) | `Hooks/RenderContext.cs:473-483` | `UseSpringValue`/`UseAnimatedValue` (`DepKey`) |
| `Reconciler` UnmountSubtreeâ†’CancelAllâ†’Orphanâ†’SeedExit | `Reconciler.cs:1447-1454` | presence Detach + deferred structural removal (Â§3.2) |
| `Reconciler` `isMount && Enter.Active` â†’ SeedEnter | `Reconciler.cs:1724-1726` | logical-identity `EnterEpoch` gate (Rank-4 fix) |
| `Reconciler` `Connected.CaptureOnLeave` call | `Reconciler.cs:1464` | `SharedElementRegistry.Register` at phase 5 |
| `Button.BrushTransitionMs = 83f` | `Controls/Button.cs:90,172` | `Transition = MotionTok.ControlFaster` |

**Retained, repurposed, or extended (not deleted):** `LayoutTransition`/`EnterExit`/`TransitionDynamics` PODs (`Foundation/LayoutTransition.cs`); `Foundation/Easing.cs` (extend with `linear()`-LUT + Back/Circ/Anticipate); `SpringParams.FromResponse` (add `FromVisualDuration`/`FromDurationBounce` factories); `AppHost.CaptureProjections`/`ApplyProjections`/FLIP (+ analytical-rebase, + `VirtualState.RecycleEpoch` guard); `EffectAux` cold slab + `BlurSigma` self-blur (FA-2); `WakeDiagnostics` (input repointed to the active-set projection, public contract unchanged); `SkeletonDeriver`. `NodeFlags` freed by the deletions are returned to the free pool.

---

## 8. Behavior catalog coverage

Every motion the driving app (`C:/WAVEE/WaveeMusic`) and the dossier name, as a concrete authoring snippet + the primitive that expresses it. Eleven behaviors; each lowers to the slab with zero per-frame alloc.

### 8.1 Entrance (declarative, logical-identity-keyed)

```csharp
new RowComponent(track) { Enter = MotionTok.SlideUpFade }      // EnterExit POD; Opacity 0â†’1, TranslateY 8â†’0, Blurâ†’0
```
**Primitive:** `Enter` (reuses `EnterExit`). Baked at first realize *per logical key* (Â§3.2). A now-playing re-skin re-renders the realized window but does **not** re-seed (the `EnterEpoch[logicalKey]` already exists) â€” the Rank-4 list flash is structurally gone.

### 8.2 Exit / presence (deferred structural removal)

```csharp
Flow.For(items, row, presence: Presence.Default)              // opt-in boundary; Exit defers removal until the fade completes
// row roots carry: Exit = MotionTok.FadeBlurOut
```
**Primitive:** `Exit` + the `DetachGroup.PendingCount` gate over `DetachedAnimSlab`. The leaving row's paint columns value-copy into a detached row; the topology slot frees immediately; the structural removal commits when the exit tracks reach `AtRest` (`ReconcilerCommitRemoval(anchor)`). `Mode {Wait|Sync|PopLayout}` is z/layout policy on the detached set, not a code path.

### 8.3 Hover / press (gesture states, the `written`-mask resolver)

```csharp
new BoxEl {
    Transition = MotionTok.ControlFast,
    WhileHover = MotionTarget.Scale(1.03f).Fill(Tok.SubtleHover),
    WhilePressed = MotionTarget.Scale(0.98f).Fill(Tok.SubtlePressed),
    WhileFocus = MotionTarget.Border(Tok.AccentFill),
}
```
**Primitive:** `While*` `MotionTarget` â†’ `AnimTargetSet` + the fixed 7-slot priority resolver (Â§4.b). `InteractionAnimator` and the recorder hover-multiply are deleted; scale is segregated into `InteractionScale` (Â§6.1 invariant 2) so the grown thumb's hit box is unchanged.

### 8.4 Drag (gesture state + velocity handoff)

```csharp
new BoxEl { WhileDrag = MotionTarget.Scale(1.06f).Opacity(0.92f), /* slider grip / draggable card */ }
```
**Primitive:** `WhileDrag` (one more `StateByte` bit + slot). On release, the `PointerFsm` inertia velocity hands off into a spring's `Velocity` via `Retarget` (Â§6.7) â€” the grip springs back velocity-continuously. First-class because gesture grips/sliders are core in the driving app.

### 8.5 Shared-element / hero (the connected-cover fly)

```csharp
// source (grid thumb) and dest (now-playing hero) both:
new ImageEl { MorphId = "art:" + uri, Source = art }
```
**Primitive:** `MorphId` â†’ `SharedElementRegistry` + a `DetachedAnimSlab` row + a critically-damped fromâ†’to spring (`Mot.ConnectedFly`, 0.34s). Source `Register`s at phase 5 (pins the image); dest `TryTake`s at 6.5, detaches the snapshot, seeds Scale/Translate srcâ†’dest + a materialize-in opacity, hides the live dest once, reveals once at Retire. **Interruption (rapid re-nav):** the running spring's rows are found via the `(detached-target, channel)â†’head` index and `Retarget`ed â€” no flight stack. **Reverse/Back:** symmetric Register/TryTake, free. Honors the `connected-animation-keepalive` memory: KeepAlive parking is no longer required (the snapshot is value-copied, not a live handle the reconciler can recycle), and the default is a smooth no-overshoot critically-damped spring.

### 8.6 Collapsing / parallax header (via ScrollBind â€” the sibling subsystem)

```csharp
new BoxEl {
    ScrollBind = [ new() { From = ScrollChannel.Offset, To = BindSink.PresentedH, Range = ScrollRange.Px(0,120), OutStart=200, OutEnd=64 } ],
    Children = [ new BoxEl { Key="largeTitle", ScrollBind=[ new(){From=Offset,To=Opacity,Range=Px(0,120),OutStart=1,OutEnd=0} ] } ],
}
```
**Primitive:** `ScrollBind` (the generic-hookable-scroll-engine subsystem, Â§3.4 of that doc). **This rework does not re-own scroll-driven motion** â€” `ScrollBind` is the scroll-source specialization of the same "a property is a function of a signal" idea, and the `AnimValue`/`SignalSource` model generalizes its slab idiom to *any* signal. The two coexist with disjoint ownership: `ScrollBind` reads post-physics scroll offset geometry-only (dt-free); the `AnimScheduler` `Driven` cadence is what wakes a scroll-bound frame. Parallax, shy-header (`ArtistShyPill`), pull-to-refresh all live there.

### 8.7 Brush / color cross-fade (Color is a channel)

```csharp
new TextEl { Transition = MotionTok.Fade, Color = Prop.Of(() => nowPlaying.Value ? Tok.AccentText : Tok.PrimaryText) }
```
**Primitive:** `AnimChannel.Color` (8 premul-linear lanes) + the implicit `Transition`. Linear-premultiplied lerp; endpoint gammaâ†’linear+premultiply baked once at seed into `_colorAux`; the recorder applies the DirectWrite gamma at draw for text runs (the text-gamma exception, Core Â§1.e) â€” the *animated* value stays linear. Subsumes `BrushTransition` (Â§7.2).

### 8.8 Stagger (reconcile-baked, no O(nÂ²))

```csharp
Flow.For(tracks, t => new RowComponent(t) { Enter = MotionTok.SlideUpFade }) with { Stagger = new Stagger(DelayChildren:0, Each:40, From:Start) }
```
**Primitive:** `Stagger` on the keyed-child container. The reconciler knows `index`+`count`, so it bakes `seedDelayMs[i] = DelayChildren + staggerIndex(i,count,From)Â·Each` into each child's `Enter` seed `DelayMs` â€” `i` directly (or `|i âˆ’ count/2|` for Center), never Motion's `Array.from().sort().indexOf` O(nÂ²). Reuses the proven `LayoutTransition.DelayMs` bake path.

### 8.9 Skeleton reveal (derived shimmer + blur-reveal swap)

```csharp
Skel.Region(loadable, template)        // SkeletonDeriver derives the shimmer from `template`; blur-reveal on swap
```
**Primitive:** the retained `SkeletonDeriver` (one source tree) + an `Enter` blur-reveal terminal (FA-3 `EnterExit.Blur`) on the real content + the shimmer pulse as an ambient `Hz` cadence row tied to the skeleton node's life (so a backgrounded tab's shimmer stops, Â§6.2). Honors the `derived-skeleton-pattern` + `transparent-boundary-layout-mirror` memories (the region mirrors its active child's `Grow`; content is a plain `Grow=1 BoxEl`).

### 8.10 Ambient loops â€” caret / equalizer / playhead (cadence-classed)

```csharp
// caret:     a Hz(2) source on the TextEditState blink
// equalizer: a Hz(AmbientDefault) ambient loop (FG_ANIM_FPS / AmbientAnimationFps default)
// playhead:  a Driven(MediaClockMs) source â€” advances ONLY when the media clock signal moves
```
**Primitive:** `CadenceClass`. The caret is `Hz(2)` â€” it no longer holds the loop at 120Hz (the dossier Rank-7 bug). The auto-playing Wavee seek **playhead is `Driven(MediaClockMs)`** â€” a paused playhead costs zero frames, structurally, with no exemption (this is exactly the `high-CPU-ambient-fps-cap` memory's fix made principled: the ambient cap becomes a per-source cadence; `ambientFps:30` becomes `Hz(30)` on the loop source, and the playhead is `Driven`, not capped).

### 8.11 Now-playing recolor (signal-fed retarget-from-current)

```csharp
new BoxEl { Transition = MotionTok.Standard, Fill = Prop.Of(() => accent.Value) }    // accent recolors on track change
```
**Primitive:** the signals-first spine â€” `Fill` bound via Sink A; on the `accent` signal edge the effect calls `Retarget`, which reads the *current* premul-linear color as the implicit "from" and cross-fades the 4 lanes velocity-continuously (the North-Star "set a target, interpolate from current, auto-retarget"). For the *backdrop* recolor the existing opacity-crossfade of two baked RTs (canon Â§2.2) is retained â€” that is image-pipeline residency, not a channel animation.

---

## 9. Reduced-motion & a11y

### 9.1 The value-not-conditional model (decided call 5, dossier Rank 3 / open-q 7)

Reduced-motion is **a value read by `Tick`/seed (and the reconcile bake), never an authoring branch.** It is realized at three layers:

- **`ReducedMotionPolicy {SnapEnd | KeepFade | Exempt}`** baked per animated node at reconcile (from the node's `Transition`/state target's `MotionTokenDef.ReducedPolicy`):
  - `SnapEnd` â†’ the spine sets `Position = To, Velocity = 0, Done` for governed channels at the next tick (transforms snap; the cross-fade is dropped too);
  - `KeepFade` â†’ drop transform/scale springs, **keep `Opacity`/`Color`/`Aux.CrossFade`** (the cross-fade survives);
  - `Exempt` â†’ essential motion (a determinate progress ring, a spinner) animates normally.
- **The global gate** (`ReducedMotion.Enabled`, read from the OS via PAL `SPI_GETCLIENTAREAANIMATION` / macOS `accessibilityDisplayShouldReduceMotion`) is snapshotted **per frame** and consulted by the spine `Tick` only â€” never inside a `Use*` body.
- **A connected/shared-element fly under reduced-motion** = a `CrossfadeOnly` detach (instant opacity cross-fade at the dest, no transform spring, Â§3.7).

**Open-q 7 resolved â€” the default policy for an un-annotated `Transition`/state target is `KeepFade`.** Cross-fades (opacity, brush) survive reduced-motion (WCAG "reduce, don't remove"); transforms snap. An author opts a node up to `Exempt` or down to `SnapEnd` per token â€” cross-fades survive *without every author opting in.* (Honest interaction with Rank 8: a full-page reveal under reduced-motion still pays the cross-fade RT cost; for root-level reveals the token's `ReducedPolicy` should be `SnapEnd` â€” per-token, not global.)

### 9.2 The structural conditional-hook fix (Rank 3, made impossible)

Today the natural authoring mistake â€” `if (Motion.ReducedMotion) return;` inside a `Use*` â€” changes the hook-slot count between renders, so the next positional cell is cast to the wrong type (`EffectCellâ†’AsyncResourceCell`, `RenderContext.cs:159,293,577`), crashing "on resize" (the resize being exactly what flips the process-wide mutable `Dsl/Motion.cs:24` bool, e.g. a drag-grip â€” the `motion-hooks-reducedmotion-conditional` memory). The fix is structural, from two facts:

1. `While*`/`Transition`/`Enter`/`Exit`/`Stagger` are **declarative POD fields baked at reconcile** â€” they consume *zero* hook slots per state.
2. The two surviving motion hooks (`UseSpringValue`/`UseAnimatedValue`) take an **unconditional** slot and read reduced-motion only via the spine at tick.

Therefore **no authoring path's hook count can vary with `Motion.ReducedMotion`** â€” the mutable global is read only inside the spine `Tick`, never inside a `Use*` body. The crash is structurally eliminated. Honoring the same memory's second clause: motion *component-hooks* (`UseSoftReveal`/`UseEntrance`) read reduced-motion as a value and seed transitions at end-state; they never early-return.

**Defense-in-depth analyzer (optional, `FluentGpu.SourceGen/Validation/`):** `FG-MOTION-001` flags any early-return gated on `Motion.ReducedMotion` inside a method whose name starts `Use` â€” catching the regression at compile time even for new imperative-recipe authors. (The imperative recipes `PopIn`/`Shake` that legitimately read it become *declarative* `MotionTok` values, so they no longer branch at all.)

### 9.3 A11y coupling (referenced, not re-owned)

Reduced-motion is the only a11y surface this subsystem owns. Focus-visual motion rides `WhileFocus` (a target-set, not ad-hoc chrome); the focus *ring opcode* (`DrawFocusRing`) and `A11yInfo` are owned by `gpu-renderer.md`/`input-a11y.md` and are referenced, not changed. Announcements (`UseAnnounce`) are unaffected â€” animation is composited, never a layout/structure change that would perturb the UIA tree.

---

## 10. Risks / open questions

The dossier's eight open questions, each **resolved** here or **flagged** as a residual, plus the honest physical limits.

| # | Open question (dossier) | Resolution |
|---|---|---|
| 1 | Compose pass vs owner-partition-only | **RESOLVED â†’ both, phased (Â§6.1).** Owner column (Phase 1, the floor + A's substrate); single compose pass (Phase 2, the Rank-1 dissolution) consuming it. Preserves hit-test-reads-layout via the segregated `InteractionScale` field. |
| 2 | Analytical spring retarget = rebase or carry-state | **RESOLVED â†’ rebase (Â§6.7).** Read `value(t)`/`velocity(t)`, reset `ElapsedMs=0`, rebake `{A,B}`. The FLIP frame-shift re-expresses as a position-axis rebase; the new spring gate asserts dt-invariance before `ReframePosition`'s old form is deleted. |
| 3 | Cadence vs vsync (sub-refresh beat) | **PARTIALLY RESOLVED + flagged residual (Â§10.1).** Build the `PresentMode.OnDemand` interval-0 path (kills the beat for the lone-sub-refresh-source case); concurrent-with-DisplayRate is structurally absent; arbitrary-precision present-timing is ledgered PAL debt. |
| 4 | `relativeTarget` cost at list scale | **RESOLVED â†’ coherence-group cap (Â§6.7/Â§3.4).** Re-resolution runs only for nodes whose `RelativeParent` is itself animating this frame; common case (one moving container, N static rows) collapses to O(1). The depth-sort is over `BoundsAnimated` (tens), not all rows. |
| 5 | Presence boundary granularity | **RESOLVED â†’ both, layered (Â§3.2).** `BoundaryKind.Presence` byte on the transparent-boundary node kind; `For`/`Show` set it **opt-in** (`presence:`), not universally â€” zero-cost for the 10k-row lists that don't ask. Re-skin vs genuine removal keyed on `LogicalKey` (the Rank-4 fix). |
| 6 | Generator keyframe storage | **RESOLVED â†’ one arena, two regions (Â§4.b/Core Â§1.b).** An immortal region for variant/recipe keyframes (baked once), an ephemeral per-mount region (free-list by node index) for inline targets. |
| 7 | Reduced-motion exempt default | **RESOLVED â†’ `KeepFade` (Â§9.1).** Cross-fades survive without per-author opt-in; `Exempt`/`SnapEnd` are per-`MotionTokenDef` refinements. |
| 8 | Did the determinism gate ever cover springs? | **RESOLVED â†’ it did not; this is a correctness fix (Â§6.6).** The dt-replay gate seeds no spring today; under Euler a seeded spring would *diverge*. The analytical switch closes a latent gate hole AND lets the gate finally cover springs. |

### 10.1 The honest physical residuals (do not pretend to fix)

- **Sub-refresh vsync beat (open-q 3).** `Present()` is vsync-locked (`Rhi.cs:73`); a `Hz(30)` source on a 120Hz panel asking for a 33.3ms software wait then a vblank-quantized present beats between 24 and 30fps (`AppHost.cs:298-306`). **The clean fix exists for the dominant case:** generalize `SuppressVsyncOnce()` (`Rhi.cs:53-58`) to a scheduler-driven `PresentMode {VsyncLocked, OnDemand}` â€” present at `SyncInterval 0` (tearless on a DComp flip swapchain) whenever `NextDueMs > displayPeriod` (the soonest need is slower than the panel), letting the software wait be the sole pacing gate. This **deletes the documented beat for a lone shimmer/caret/30fps fade.** It is structurally absent when any `DisplayRate` source is concurrent (the wake is 0, full-refresh, correct). The **residual**: presenting at an *arbitrary* source cadence independent of wait granularity needs a hardware present-timing API (DXGI `IDXGISwapChain2::SetFrameLatencyWaitableObject` + present-time target, or `DCompositionWaitForCompositorClock`) wired into the PAL â€” **not in the v1 `ISwapchain.Present()` seam.** Honest statement for canon: *sub-refresh cadence is paced to ~one panel-period jitter (Â±8.33ms at 120Hz), an accepted documented limit, never on the latency-sensitive path* â€” the same "decoupled, not invincible" honesty the corpus applies to scroll. Ledger it as macOS/Windows present-timing parity debt.
- **Blur cost (Rank 8).** `BlurSigma` routes through a pooled offscreen RT; a full-page reveal blur is a full-screen RT pass per frame (~60fps, the `full-page-reveal-blur-cost` memory). Not abstractable away â€” it is fill-rate. The policy answer: the engine knows the node's bounds and should default root-level reveals to `blur:0` (opacity-only) and clamp/downsample above an area threshold, rather than every recipe re-deciding. The reduced-motion default `KeepFade` keeps the fade (and thus the RT cost) for full-page reveals â€” so root reveals carry a `SnapEnd` token (Â§9.1).
- **The `LayoutW/H` Reflow exception.** Animating a *genuine* layout dimension (the rare opt-in `LayoutW/LayoutH` channel â†’ `LayoutInput` + parent `LayoutDirty`) is the single sanctioned `LayoutDirty` escape â€” a self-feeding loop guarded by target/echo guards. It stays an explicit slow path, never reachable from the declarative compositor-channel surface (the `AnimChannel` enum the `While*`/`Transition` fields can name is exactly the compositor-safe set; "animate this" is structurally incapable of triggering relayout *except* via this one named opt-in).
- **The second detached render-walk (Â§3.8).** A recycled topology slot cannot outlive its node, so the value-copied snapshot + a separate phase-8 walk is the *only* seam-safe form for exit/connected. Cost is O(detached count), bounded by the Â§3.5d cap (8 default); a reorder-storm over a non-virtualized list is the one place it spikes â€” capped, excess rows snap. Not free, not removable.

### 10.2 New failure modes this rework introduces (contained)

- **Driven-source wake liveness (Core Â§2.8 Risk 2).** A `Driven` source is event-woken; it relies on the signal's write calling `WakeFrame()`. The media clock, scroll offset, and `Signal<float>` writes already wake the loop. A driven source bound to a signal whose write does *not* wake (a raw column poked outside the reactive runtime) would silently stall. Mitigation: only the four `SignalKind`s are bindable, all route through wake-on-write paths; DEBUG-assert that a registered `Driven` slot has a wake path. Contained to a closed enum.
- **Deferred structural removal changes reconcile timing (Â§3.8).** A removed keyed child stays *logically pending* until its exit completes; a parent that unmounts mid-exit must cascade-Retire its pending children's detached rows. Handled in `Retire` group accounting; needs a dt-replay gate over "unmount parent while child exits." A device-lost full-rebuild mid-exit Retires-without-commit (the anchor is gone â€” guarded by `IsLive(anchor)`).
- **Overshoot regression on connected fly (Â§3.8).** Deleting the connected settle heuristic relies on the analytical spring's `AtRest` being visually tight; the sub-pixel rest threshold must be re-tuned per the granular-scale footgun (Â§6.6) or a fly sticks for a frame.
- **`AnimChannel` enum ceiling (Â§4 cost).** Growth to ~25 (8 brush lanes + Tint) keeps the mask `uint` (â‰¤32) â€” but that is the hard ceiling. Per-gradient-stop animation later would overflow `uint`; the mask would go `ulong` (16B `AnimTargetSet` â†’ 24B). Flagged, not blocking.

---

## 11. Phased implementation outline

Each phase builds clean (`dotnet build src/FluentGpu.slnx`), passes `dotnet run --project src/FluentGpu.VerticalSlice` ("ALL CHECKS PASSED", zero-alloc gates green), and is independently shippable behind the gates. The build *order* ships single-thread-correct first, then flips the compose pass behind `FG_COMPOSE_PASS`, mirroring the ScrollBind doc's phasing and the corpus's "single-thread-correct first, then flip behind a green gate" discipline. Touching the new owner column / cadence enum / `AnimChannel` lanes means reconciling `backdrop-effects-animation.md Â§5` + `subsystems/README.md` + `SPEC-INDEX.md Â§2` and running `check-canon.ps1`.

**Phase 0 â€” `DepKey` + the value slab substrate (no behavior change).**
Land the net-new `DepKey` (16B scalar struct, `[InlineArray]`-backed for >4; `EffectImpl` gains a `DepKey` overload, `RenderContext.cs:289`). Add the `AnimValue` 64B POD + `AnimValueSlab` (`ChunkedArena` + free-list + `_headByNode`) + `SignalSourceTable` (index-based) in `FluentGpu.Animation`, **alongside** the existing `class Track`/`List` (parallel, unused). No `Tick` rewrite yet. *Gate:* build clean; a DEBUG self-check that `AnimValueSlab.Alloc` never grows in phases 6â€“13; `DepKey` content-`Equals` unit check.

**Phase 1 â€” Migrate `AnimEngine.Tick` to the slab + the analytical spring (the determinism fix).**
Rewrite `Tick` (`AnimEngine.cs:604-789`) onto the slab dense walk; lift `EvalSpringAnalytical` from `OverscrollPhysics.StepSpring` (3 regimes + the `min(Ï‰Â·t,300)` guard + per-channel rest); delete the Euler block (`:646-658`), `Find`/`Get` scans (`:585-601`), `_scratch` (`:166`), `JustSeeded`/delay-defense (`:621-634`). Keep `Accum.FromPaint` for now (deleted with the compose pass). *Gate:* **NEW mandatory determinism gate** â€” seed an under-damped spring, replay at dt âˆˆ {8.33,16.67,33.3}, assert identical `Position`/`Velocity` traces (this is the gate hole Â§6.6 closes); existing alloc tripwire + the connected/FLIP/scroll screenshots green.

**Phase 2 â€” The `FrameClock` + the `AnimScheduler` active-set (wake math first, tickers last).**
Add `FrameClock`/`FrameClockSource` (replace `FrameTimeSource.cs`); add the `Source`/`CadenceClass`/`SignalSource` slab. **Each existing ticker registers a `Source` but keeps its own `Tick`** (single-thread-correct first). Flip the host wait math to `min(NextDueMs, residual)` (`AppHost.cs:372-388`), delete `ComputeWakeReasons`/`AnimIsAmbient`/`LatencySensitiveWake`/`_scrollGrace`/`Resync` (`:312-327,398,428-455,1114-1119`). Repoint `WakeDiagnostics` input to the active-set projection (public contract unchanged). *Then* collapse the 6+ tickers into the one `_sched.Tick(clock)` dispatch (`:1121-1137`). *Gate:* **NEW** scheduler-tick + wake-decision alloc tripwire (`delta==0`); **NEW** `FrameClock` `NowMs`-trace determinism replay; the `high-CPU-ambient-fps-cap` regression check â€” a `Hz(2)` caret + a `Driven` paused playhead must idle the loop (assert `idleAgo` climbs).

**Phase 3 â€” Owner column + segregated `InteractionScale` (Fork-1 Phase 1, ship now).**
Add the packed `OwnerWord {uint Bits; uint Gen}` cold SoA column + reconcile-time `Claim(node, OwnChannel, OwnerTag, Priority)` + the `[Conditional("DEBUG")]` double-owner assert. Land `NodePaint.InteractionScale` as a real paint field; move the recorder (`SceneRecorder.cs:205-214`) to read a precomputed scalar (removes the per-frame `TryResolveInteractionProgress` walk). *Gate:* alloc tripwire; scroll/FLIP/connected screenshots green; canon-reconcile `foundations.md`/`subsystems/README.md` for the new cold column + paint field; `check-canon.ps1` exit 0.

**Phase 4 â€” The declarative authoring surface + `MotionTok` registry + the resolver.**
Add the bound `Element` field set (Fork-2: `Transition`, `WhileHover/Pressed/Focus/Drag/InView` + `InViewMargin`, `Enter`/`Exit` reusing `EnterExit`, `Stagger`, `Layout` alias, `VariantCascade`); the `AnimTargetSet` mask+arena (two regions); the `NodeInteract` byte + the `written`-mask priority resolver (phase 7, pre-`Tick`). Add `MotionTok` (`MotionTokenId`â†’`MotionTokenDef` per-theme `FrozenDictionary`, source-gen'd); delete `Dsl/Motion.cs` + `Dsl/Expressive.cs`. Extend `Foundation/Easing.cs` (`linear()`-LUT + Back/Circ/Anticipate). Replace the four `object[]` hooks + `UseAnimatedValue(float)`/`AnimValueCell` with `UseSpringValue`/`UseAnimatedValue(DepKey)`; migrate the four callers. Add `AnimChannel.Color` (8 premul-linear lanes) + `_colorAux`. **Delete `InteractionAnimator` + `BrushTransition`/`BrushAnim`/`AdvanceBrushAnims`**; `Button` â†’ `Transition = MotionTok.ControlFaster`. *Gate:* **NEW** gesture-state replay case (hoverâ†’pressâ†’release, dt âˆˆ {â€¦}, identical trace); **NEW** brush-channel cross-fade alloc + golden; the `bound-rows-for-components-in-lists` + `motion-hooks-reducedmotion-conditional` regression checks; `FG-MOTION-001` analyzer (optional) green.

**Phase 5 â€” Presence + the `DetachedAnimSlab` + the connected rebuild.**
Add `DetachedAnimSlab` (`DetachedNode` 96B + `DetachGroup` + the tagged-union `AnimValue.Target`); the `RecordDetached()` second walk (rewrite `SceneRecorder.cs:78-125`); presence enter/exit on the `BoundaryKind.Presence` boundary keyed to `LogicalKey` (rewrite `Reconciler.cs:1447-1454,1724-1726`). **Delete `ConnectedAnimation.cs` (entire)**, `SceneStore` orphan + overlay machinery (`:357-384,402-422`), the `HasTracks`-poll reclaim (`AppHost.cs:1546`); rebuild connected/hero as a `SharedElementRegistry` + detached row + `Mot.ConnectedFly`. *Gate:* **NEW** "unmount parent while child exits" dt-replay (deferred-removal cascade); connected-fly + reverse-nav + rapid-re-nav-interrupt screenshots; the `connected-animation-keepalive` smooth-no-overshoot check; detached-slab cap snap under a reorder-storm.

**Phase 6 â€” FLIP `relativeTarget` + the virtualization guard.**
Add the `RelativeFrame` side-row + the depth-ordered `relativeTarget` pre-pass with the coherence-group cap (Â§3.4); promote `Animate`â†’`Layout` (alias); guard `CaptureProjections` on `VirtualState.RecycleEpoch` (`AppHost.cs:1410`). Confirm the analytical-rebase handoff is dt-invariant, then delete `ReframePosition`'s field-shift form. *Gate:* **NEW** reorder dt-replay (nested coherence); the `library-master-detail-structure`/virtualized-reorder no-tear screenshot; `relativeTarget` O(animating-containers) alloc check.

**Phase 7 â€” The compose pass (Fork-1 Phase 2, behind `FG_COMPOSE_PASS`).**
Build A's single fold-and-write-once pass consuming the Phase-3 owner column. Convert writers in dependency order â€” `AnimEngine`+recorder, then the (already-detached) connected path, then the scroll triad (`ScrollBindEval`/`ScrollAnimator`/`OverscrollPhysics`, moving the sticky change-gate at `ScrollBindEval.cs:138` to a contribution-value gate). Delete `Accum.FromPaint` (`:111-137`) only when the flag is green; remove the fallback flag. *Gate:* the full scroll/fling 60fps + `scroll-winui-parity-gap` parity-lock harness on the converted config; alloc tripwire; the single-owner DEBUG assert fires on a seeded double-bind; then flag removal.

**Phase 8 (deferred, not v1) â€” `PresentMode.OnDemand` + the sub-refresh present.**
Generalize `SuppressVsyncOnce()` to the scheduler-driven `PresentMode` (Â§10.1) â€” interval-0 on-demand present when `NextDueMs > displayPeriod`. Ledger the arbitrary-precision present-timing API as PAL parity debt. *Gate:* a lone-`Hz(30)`-source effective-cadence measurement (no 24fps beat); no regression on the concurrent-`DisplayRate` path.

---

## 12. Documentation impact map

This section drives the doc sweep. Every doc and agent file that mentions `AnimEngine`/`AnimTrack`/`UseSpring`/`BrushTransition`/`MotionRecipes`/the motion tokens, plus the canon-ownership wiring, with **exactly what changes**. Run `check-canon.ps1` after each `design/` edit.

| Path | What changes |
|---|---|
| **`design/subsystems/backdrop-effects-animation.md` Â§5** (THE CANON OWNER â€” the biggest rewrite) | Rewrite Â§5 wholesale. **Â§5.1:** `class Track`â†’the `AnimValue` 64B slab + `(node,channel)â†’head` index; `AnimChannel` byte gains `Color` (RGBA side-row, Â§1.e) â†’ enum 17â†’~25 lanes (note the `uint` mask ceiling); replace the `IntegrationMode {Eased,Spring}` + the **sub-stepped Euler block (`:478-498`) with the analytical 3-regime closed form sampled at absolute `t`**; `Generator` POD + by-value `Eval`. **Â§5.2 (`UseImplicitTransition`):** rename/subsume to the base-`Element` `Transition` field + the column-diff Sink-A bake; brush is a channel. **Â§5.3 (`DetachedAnimSlab`):** the `DetachGroup.PendingCount` completion gate + the tagged-union target + `RecordDetached`. **Â§5.4 + Â§5.6 (connected/shared-element):** the `ConnectedAnimation`-deleted rebuild over the detached slab + `SharedElementRegistry`; single critically-damped `Mot.ConnectedFly`; reverse/interrupt now free. **Â§5.5 (`DrivenClock`):** `DrivenClockTable` `List<Func<float>>`â†’index-based `SignalSource`; reframe as the `Driven` cadence source. **Â§5.7 (springs/retarget):** rebase-not-frame-shift handoff (Â§6.7). **Â§5.8 (`animateContentSize`):** add `relativeTarget` + the virtualization `RecycleEpoch` guard. **Â§5.9 (reduced motion):** the value-not-conditional model + the structural conditional-hook fix + `KeepFade` default. **Â§5.10 (motion tokens):** fold `Dsl.Motion`/`Dsl.Expressive`/`MotionSprings` into the one `MotionTok` registry; `MotionTokenDef.ReducedPolicy`. **NEW Â§5.11:** the `AnimScheduler`/`FrameClock`/`CadenceClass` (land the unbuilt `ClockKind{Frame|Driven}` spec as `CadenceKind`) + the wake math + the owner-column/compose-pass composition model (Fork 1) + the `InteractionScale` segregation. Update the Â§7 13-phase table rows (6.5/7/13) and the Â§8 zero-alloc story (delete `_scratch`/closure-leak claims; add the slab-growth tripwire). The "**SHIPPED â€” interaction-driven compositor effects**" block (`:522-533`) becomes "the `written`-mask resolver"; FA-2/FA-3 (`EnterExit.Blur`, self-blur) retained. |
| **`design/SPEC-INDEX.md` Â§2** (canonical-value / owner table) | Rewrite the **Springs/retarget/shared-element/motion-tokens** row (currently keyed on `AnimTrack 48â†’64B`): canonical value becomes "**`AnimValue` 64B slab + analytical generator + `AnimScheduler`/`CadenceClass` + the owner-columnâ†’compose-pass composition model + `AnimChannel` w/ Color lanes + `MotionTok` registry; `ConnectedAnimation`/`InteractionAnimator`/`DrivenClockTable`/`BrushTransition` deleted**." Add a **new row: "Animation composition model"** â†’ owner `backdrop-effects-animation.md Â§5.11`: "*per-(node, OwnChannel) `OwnerWord` cold column + DEBUG single-owner assert (Phase 1); single fold-and-write-once compose pass (Phase 2); `InteractionScale` segregated paint field, hit-test inverts `LocalTransform` only.*" Add a **new row: "Frame cadence / wake authority"** â†’ owner `backdrop-effects-animation.md Â§5.11`: "*`min(next-due)` over a dense active-set; per-source `CadenceClass {DisplayRate, Hz(n), Driven(slot), OneShot, Paused}`; `FrameClock.NowMs` advances by the clamped 1..40ms delta.*" Update the `DepKey` row note (now landed in `src/`, the hot motion-hook path). Bump "Last reconciled". |
| **`design/subsystems/README.md`** (the contract-ownership map) | Line **39** (the doc one-liner): replace `AnimTrack/AnimEngine/DetachedAnimSlab/DrivenClock` with `AnimValue slab/AnimScheduler+CadenceClass/DetachedAnimSlab/SignalSource/owner-column+compose-pass`. Line **142** (`UseSpring / UseAnimatedValue / UseSharedElement / â€¦`): add `UseSpringValue`, mark `UseAnimatedValue` as the `DepKey` form; note the four `object[]` hooks deleted. Line **146**: replace `UseImplicitTransition`/`UseConnectedAnimation`/`UseDrivenAnimation` with the declarative field set (`Transition`/`While*`/`Enter`/`Exit`/`Stagger`/`Layout`/`VariantCascade`) + `UseReducedMotion` retained. Line **215** (the cross-cutting artifact row): rewrite as above (drop `AnimTrack 48â†’64B`, `SpringParams` stays; add the composition-model + cadence owners). Line **207** (Suspense keep-stale cross-fade) still points here â€” verify the detached-slab rename doesn't break the reference. **Â§2.5 source generators:** add `MotionTokenId` gen (reuse the theme `Tok.*` mechanism) + the optional `FG-MOTION-001` analyzer. |
| **`docs/design/check-canon.ps1`** (drift gate â€” supersession rules) | Add rules for the superseded tokens this rework retires (each with a `Why` pointing at SPEC-INDEX): `IntegrationMode` (the `{Eased,Spring}` MODE-flag form is superseded by the `GenKind` discriminant); `DrivenClockTable`/`List<Func<float>>` driven-clock (superseded by index `SignalSource`); `InteractionAnimator` as a runtime (superseded by the resolver); `BrushTransitionMs` (superseded by `Transition` + `AnimChannel.Color`); the three token namespaces `Dsl.Motion`/`Dsl.Expressive`/`MotionSprings` (superseded by `MotionTok`). Add the deleted-doc archival when Â§5 supersedes the sub-stepped-Euler text. **Procedure:** any live mention of these in prose needs `<!-- canon-allow: reason -->`; move no doc to `archive/` unless a whole doc is superseded (none is â€” `backdrop-effects-animation.md` is rewritten in place). |
| **`CLAUDE.md`** (project instructions) | In the "binding contracts" paragraph, the animation clause currently reads "Animation writes ONLY compositor columns â€¦ marks Transform/PaintDirty, NEVER LayoutDirty (the single LayoutW/H Reflow opt-in â€¦)". Add: "*all motion is one `AnimValue` slab + one `AnimScheduler` (per-source `CadenceClass`, `min(next-due)` wake) + the analytical closed-form spring sampled at absolute `t`; composition is owner-columnâ†’single-compose-pass; reduced-motion is a value, never a hook branch.*" Update the architecture-at-a-glance `backdrop-effects-animation` bullet to name the rework. Keep the "near-zero-allocation / decoupled-not-invincible / production-safety==CI-coverage" honesty clauses (the sub-refresh-vsync residual joins those physical limits). |
| **`.claude/skills/fluentgpu/SKILL.md`** (the skill â€” animation refs) | Rewrite the "**WinUI controls, animations, and states**" section's state model (lines ~138-151): "Hover/press visual states belong in `InteractionAnimator` data" â†’ "*belong in the declarative `WhileHover`/`WhilePressed` `MotionTarget` fields resolved by the `written`-mask priority resolver; there is no `InteractionAnimator` and no control-internal motion path*". "Explicit timelines belong in `AnimEngine`" â†’ "*authored timelines are `AnimValue` rows / `Generator` keyframes via `UseAnimatedValue`/`UseSpringValue`*". Update the file-map rows (lines ~90-91): "Control visual state / motion" â†’ the `Transition`/`While*` fields + `MotionTok`; "Explicit control timelines" â†’ the slab. Update the **Deeper docs** `motion-recipes.md` bullet (lines ~167-171): the recipes are now named `MotionTok`/`EnterExit` values delivered via `Enter`/`Exit`/`Stagger`, not imperative `MotionRecipes.*` calls; `Dsl.Expressive` tokens fold into `MotionTok`. |
| **`docs/guide/README.md`** (guide index + file map) | The "ðŸ¤– AGENT â€” file & ownership map" rows (lines ~111-112): "Control visual state / interaction motion" â†’ replace `InteractionAnimator`/`{Hover,Press}Scale`/`BrushTransition` with `WhileHover`/`WhilePressed`/`Transition`/`MotionTok` (note `InteractionAnimator` deleted); "Explicit control timelines" â†’ `AnimValue`/`Generator` via `UseSpringValue`/`UseAnimatedValue(DepKey)` (`ControlMotion` enter/exit presets become `MotionTok`). Update the motion-recipes index bullet (line ~144-146) to the declarative delivery. The "Three ways an update reaches the screen" table is unaffected (bindings unchanged). |
| **`docs/guide/motion-recipes.md`** (the how-to / Expressive Motion Kit) | Largest guide rewrite. The Kit moves from imperative `MotionRecipes.*` (handle-capture, `OnRealized`, `Context.Anim`) to **declarative delivery**: each recipe is a named `MotionTok` (`MotionTok.PopIn`/`SoftReveal`/`SuccessCheck`/â€¦) or an `EnterExit` value applied via `Enter`/`Exit`/`Stagger`/`Transition`. Document: the unified `MotionTok` registry replacing `Dsl.Expressive`/`Dsl.Motion`; `WhileHover`/`WhilePressed` for hover/press (replacing `UseHoverScale`); the `Keyframe[]` literals now live in the shared arena; `Sequence` is explicitly OUT of v1 (use `Stagger` + `Enter`/`Exit`). Keep the curve vocabulary (`Easing.SmoothOut`/`Overshoot`/`Pop` + the new `linear()`/Back/Circ/Anticipate) and the self-blur (`BoxEl.Blur`/`AnimChannel.BlurSigma`). Re-state the reduced-motion rule: read as a value, never `if (Motion.ReducedMotion) return;`. |
| **`docs/guide/control-fidelity.md`** (building WinUI-faithful controls) | Update the visual-state guidance: `StateBrush`/`InteractionAnimator` visual states â†’ the `WhileHover`/`WhilePressed` `MotionTarget` + `Transition = MotionTok.ControlFaster` model (the ~83ms `ControlFasterAnimationDuration` is a token, not a hardcoded `BrushTransitionMs`); "explicit `AnimEngine` timelines like checkmark stroke trim" â†’ `UseAnimatedValue`/`UseSpringValue` over the `AnimValue` slab. Note `InteractionAnimator`/`BrushTransition`/`{Hover,Press}Scale` are deleted; the control kit uses the *same* model as app authors (decided call 2). |
| **`docs/guide/skeleton-loading.md`** (skeleton/shimmer how-to) | Update the reveal edge: the blur-reveal swap is an `Enter` `EnterExit.Blur` terminal (FA-3 retained); the shimmer pulse is an ambient `Hz` `CadenceClass` row (so a backgrounded tab's shimmer stops). `SkeletonDeriver` + `Skel.Region` retained. Re-state the `transparent-boundary-layout-mirror` rule (mirror the active child's `Grow`; content is a plain `Grow=1 BoxEl`) since presence now rides the same `BoundaryKind`. |
| **`docs/guide/rendering-and-performance.md`** (frame pipeline / zero-alloc) | Add the `AnimScheduler` wake model (`min(next-due)`, per-source `CadenceClass`) to the frame-loop description; note the phase-7 single tick replacing the 6+ tickers; the `Driven` cadence ("a paused playhead costs zero frames"); the compose-pass (one write per node). Reinforce the zero-alloc gate covering the scheduler/slab path and the new spring determinism gate. |
| **`docs/guide/pitfalls.md`** (symptom â†’ cause â†’ fix) | Add entries: (1) "high CPU when a media clock is paused" â†’ was the ambient free-run; now use `Driven` cadence (no manual cap). (2) "list flashes on selection/now-playing re-skin" â†’ presence is logical-identity-keyed; no longer remount-replays `Enter`. (3) "`EffectCellâ†’AsyncResourceCell` crash on resize" â†’ never `if (Motion.ReducedMotion) return;` in a `Use*`; reduced-motion is a value (the `FG-MOTION-001` analyzer flags it). (4) "two writers fighting a transform" â†’ owner-column assert names the offender. |
| **`docs/guide/components-elements-layout.md`** (element zoo / hooks reference) | Update the `Element` field reference: add `Transition`, `WhileHover/Pressed/Focus/Drag/InView`+`InViewMargin`, `Enter`/`Exit`, `Stagger`, `Layout` (alias of the removed `Animate`), `VariantCascade`; remove `BrushTransitionMs`, `HoverScale`/`PressScale`+durations/easings, `HoverOpacity`/`PressedOpacity`/`HoverFill`/`PressedFill`. Update the hooks list: `UseSpringValue`/`UseAnimatedValue(DepKey)` in, `UseSpring`/`UseTransition`/`UseKeyframes`/`UseDrivenAnimation`/`UseAnimatedValue(float)`/`UseHoverScale` out. |
| **`design/subsystems/reconciler-hooks.md`** (DepKey + enter-seed + keep-stale) | Verify/extend the `DepKey` semantics now that it is the load-bearing motion-hook dep (Phase 0 lands it in `src/`); document the Sink-A `Prop<T>` mount-`Effect` bake template (`Reconciler.cs:883`) as the hook-lowering path; the enter-seed site (`:1654`) is now logical-identity-keyed (cross-reference Â§3.2). The Suspense "keep-stale cross-fade on the `DetachedAnim` slab" reference (SPEC-INDEX) must track the slab's new completion-gate shape. |
| **`design/subsystems/theming.md`** (token table pattern) | Note the `MotionTok` family rides the existing `Tok.*` `FrozenDictionary<TokenId,â€¦>` per-theme machinery (the motion-token *table pattern* is theming's, the *semantics* are `backdrop-effects-animation.md`'s, per the existing ownership split); a theme variant can ship all-`SnapEnd`/`KeepFade` tokens (reduced-motion as a theme). |
| **`design/subsystems/dsl-aot.md`** (source-gen) | Add the `MotionTokenId` generator (reuses the theme-blob/`Tok.*` gen) to the generator inventory; the optional `FG-MOTION-001` analyzer (the `if (Motion.ReducedMotion) return;`-in-`Use*` rule) alongside the WGPU family. |
| **`design/subsystems/input-a11y.md`** (scroll + gesture) | Cross-reference: the `AnimScheduler` `Driven` cadence is what wakes a `ScrollBind`/scroll-integrator frame (the scroll integrator stays owned here, untouched); `WhileDrag` velocity handoff consumes the `PointerFsm` inertia velocity into a spring `Velocity` (Â§8.4) â€” note the seam, no integrator change. |
| **`docs/plans/generic-hookable-scroll-engine-design.md`** (the sibling plan, Â§10 R1/R2/R3) | Update the R1/R2 forward-references: this animation rework *is* the engine-wide generalization R1 anticipated ("if it proves common, express stretch binds *as* driven tracks reading a signal source" â€” now the `AnimValue`+`SignalSource` model); the owner-partition R3 generalizes to the owner-columnâ†’compose-pass (Â§6.1). No contract change to ScrollBind itself; the two slabs coexist with disjoint ownership. |
| **`design/architecture-spec.md` Â§5.6** (structural enter/move/exit) | The Â§5.6 "structural enter/move/exit animations re-routed through this subsystem's handle-based API" reference now points at the declarative presence/`DetachedAnimSlab` path (exit defers structural removal via the completion gate, not `scene.Orphan`); verify the `Detached` flag + deferred-free (Â§5.4) wording matches the new slab lifecycle. |

**Sweep procedure (exhaustive):** after the field-set and deletion lands, grep the whole tree for the retired identifiers and reconcile every hit â€” `AnimTrack`, `IntegrationMode`, `InteractionAnimator`, `ConnectedAnimation`, `DrivenClockTable`, `BrushTransition(Ms)`, `Hover/PressScale`, `Hover/Pressed{Opacity,Fill}`, `UseImplicitTransition`, `UseConnectedAnimation`, `UseDrivenAnimation`, `UseAnimatedValue(float`, `UseSpring(`/`UseTransition`/`UseKeyframes`, `Dsl.Motion`, `Dsl.Expressive`, `MotionSprings`, `Accum.FromPaint`, `_scratch`, `scene.Orphan`/`OrphanCount`, `OverlayClip`/`AddOverlay`, `AdvanceBrushAnims`, `ComputeWakeReasons`/`AnimIsAmbient`/`LatencySensitiveWake`/`AmbientAnimationFps` â€” each is either deleted, renamed, or `canon-allow`-annotated. Then `powershell -File docs/design/check-canon.ps1` (exit 0) and the VerticalSlice gate.
