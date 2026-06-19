# Component activation lifecycle — `UseIsActive` / `UseActivation` + parked-subtree quiescence

- **Date:** 2026-06-18
- **Status:** IMPLEMENTED (2026-06-18). Layers A–D + canon + golden checks (50b/50c) shipped; VerticalSlice green (481 checks, alloc gates zero); `check-canon.ps1` exit 0. App adoption (§11.6) intentionally left as the optional follow-on.
- **Scope:** `FluentGpu.Engine` (hooks, reconciler, host, animation) + canon docs. App wiring (`Wavee`) is illustrative.
- **Grounding:** synthesized from a 6-agent engine-wide research pass (citations inline, `file:line`).

## 1. Context & problem

`Flow.KeepAlive` (`ContentHost.cs:21`, `MaxEntries: 8`) parks a backgrounded tab's page subtree: it stays **mounted but detached** from the live scene tree. A recently-shipped fix (`Reconciler.cs:782-792` `SetSubtreeParked` + the `CompEntry.Parked`/`DeferredRender` gate at `RunComponent` `:448`) **suspends the parked subtree's component renders** so a backgrounded tab stops rebuilding on the signals it still subscribes to.

But parking suspends **only renders**. Everything else a component started keeps running, because it lives in separate effects/Tasks, not the render path (research Agent 4):

| Mechanism | On park | On minimize |
|---|---|---|
| Component render-effect | **pauses** (`Reconciler.cs:448`) | pauses (whole frame skipped) |
| Bound props, `UseSignalEffect` | keeps running | pauses (flush skipped) |
| `UseEffect`-committed subscriptions/timers, `UseAsyncResource` Tasks | **keep running** (park ≠ unmount; cleanup only on unmount) | keep running (Task pool) |
| `AnimEngine` tracks, `ScrollAnimator` | **keep ticking** (gate is `IsLive` only — `AnimEngine.cs:592`, `ScrollAnimator.cs:169`) | pause (tickers live inside `Paint`, skipped at the minimize gate `AppHost.cs:735`) |

Two consequences:
1. **No way for a developer to pause their own background work** (a poll, a periodic refresh, an OS subscription) when their page is backgrounded. `WindowsApiPage` (`WindowsApiPage.cs:71-143`) already **hand-rolls** an activation lifecycle ("the gallery exposes no per-component unmount-disposable hook"), proving the need; `UseAsyncResource` cancels on *unmount* but a parked page is never unmounted — the missing middle state.
2. **The engine itself wastes work on parked subtrees.** A looping `AnimEngine` track (skeleton shimmer, spinner) or a mid-fling `ScrollAnimator` entry on a parked tab keeps `HasActive` true → **defeats the idle wake-stop** (`AppHost.cs:387`) → burns CPU/battery while invisible. A developer cannot fix this (it's the engine's tickers).

Components also cannot see **window-minimized** state at all today (host-only: `AppHost.cs:803-805`).

## 2. Goals & non-goals

**Goals**
- A developer-facing, **notify-only** activation lifecycle: components learn when they go inactive/active and decide what to pause/resume.
- "Inactive" unifies two sources: **parked by `Flow.KeepAlive`** (per-component) **OR window minimized** (app-wide).
- The **engine auto-quiesces** a parked subtree's own `AnimEngine`/`ScrollAnimator` tickers so an invisible tab can't defeat idle wake-stop.
- Reuse the existing `SetSubtreeParked` chokepoint and the signals-first idioms; zero steady-state allocation.

**Non-goals**
- Auto-pausing developer work (engine can't know what's theirs — notify-only).
- Focus/blur as an activation input (a visible-but-unfocused window is still visible — `WindowBlur` is input-cancellation only, `InputDispatcher.cs:879-894`).
- Arbitrary same-screen occlusion (not reliably detectable on Windows; no cloak getter; not portably knowable).
- Pausing playback or any model-layer work (the playback clock is a `PeriodicTimer` in `Wavee.Core` outside the UI tree — `FakePlaybackProvider.cs:94` — and must keep running).

## 3. Decisions (from brainstorming)

| Question | Decision |
|---|---|
| API shape | **Both**: `UseIsActive() → IReadSignal<bool>` (reactive truth) **and** `UseActivation(onActivated, onDeactivated)` (transition callbacks). |
| Activation scope | Parked-by-KeepAlive **OR** window-minimized. Designed so suspend/resume can AND-in (app-side, opt-in). |
| Engine auto-quiesce | **Yes** — parked subtrees' `AnimEngine`/`ScrollAnimator` tickers are suspended by the same `SetSubtreeParked` edge. |
| Notify-only | Confirmed — engine signals; developer decides. |

## 4. Architecture (4 layers)

### Layer A — Host: app-wide window-visible signal (`AppHost.cs`, `Hooks/Context.cs`)
- A host-owned `Signal<bool> WindowVisible` (true unless minimized), exposed as an ambient signal exactly like `Viewport.Size`/`ImageEpoch` (`AppHost.cs:619-629`; `SetAmbient`).
- Written **on the UI thread** at the existing minimize/restore edge in `RunFrame` (`AppHost.cs:732-734`) — minimize is a per-frame `IsMinimized` pull (`:805`), not a discrete Win32 event. Value-eq-gated (no-op writes notify nobody).
- To fire `onDeactivated` *on* minimize (the gate early-returns before the flush, `:735-749`), the edge does one reactive flush before returning; the restore edge already forces a frame (`:733`).
- **Suspend/resume (opt-in, app-side):** the engine also exposes a public `SetWindowActive(bool)` host method. The engine cannot reference `FluentGpu.WindowsApi` (topology: WindowsApi → Engine), so the **app** wires `PowerSession.Suspending/Resumed` (`PowerSession.cs:175-194`) into it via `AppHost.Post` (`:677-682`, the thread-safe UI marshal — power callbacks arrive off-thread). Documented as an augmentation, not an engine dependency. `EnergySaverOn`/network are explicitly **not** inputs.

### Layer B — Reconciler: per-component active signal (`Reconciler.cs`)
- `CompEntry` gains a lazily-created `Signal<bool> ActiveSig` (default true), created the first time a component reads it (non-using components allocate nothing).
- Flipped in the **existing `SetSubtreeParked` walk** (`:782-792`) — the single park/un-park chokepoint, already reached on the deactivate (`:716`) and reactivate (`:703`) edges: `parked: true → ActiveSig.Value = false`; `parked: false → ActiveSig.Value = true` (alongside the existing `DeferredRender` replay). Value-eq-gated, so it never re-enters the suspended render.
- **Mount-under-parked-ancestor gap** (research Agent 2 §4g): `MountComponent` (`:427`) always starts `Parked=false`. A component mounted *into* an already-parked subtree must initialize inactive — the mount path checks the nearest ancestor's parked state (or a "currently parking" flag) and seeds `Parked`/`ActiveSig` accordingly.
- Lifetime: `ActiveSig` lives on `CompEntry`, dropped at `_comps.Remove` (`:1402`); the *consumer* (memo/effect) is a hook cell disposed by `RunAllCleanups` on unmount.

### Layer C — Hooks: the developer surface (`RenderContext` + `Component` wrappers)
- `IReadSignal<bool> UseIsActive()` → a value-gated `Memo<bool>` = `componentActiveSig.Value && windowVisible.Value` (`UseComputed` cell). De-bounces flapping; **zero steady-state allocation** (memo + closure created once at mount, cell-cached). Returns the memo (it implements `IReadSignal<bool>`).
- `void UseActivation(Action? onActivated = null, Action? onDeactivated = null)` → a **standalone** `Effect` (`UseSignalEffect` cell) over the memo, **not** a render-effect (critical: `RunComponent` is suspended while parked, so the notification must live in an independent computation that keeps running while parked). Fires `onDeactivated` on true→false, `onActivated` on false→true, **transitions only** (a `Ref<bool>` guards the initial state). Callbacks are stashed in a stable cell (the `UseGesture` forwarder precedent, `RenderContext.cs:99-128`) so fresh lambdas each render don't re-subscribe; invoked under `Reactive.Untrack` so a callback's signal reads don't subscribe the effect. Disposed via `RunAllCleanups`.

### Layer D — Engine auto-quiesce of parked tickers (`Reconciler.cs` + `Animation/*`)
- `SetSubtreeParked(node, parked)` marks each node in the parked subtree with a scene-level **parked marker** (a `NodeFlags` bit) and clears it on un-park.
- `AnimEngine.Tick` (`AnimEngine.cs:589-592`) and `ScrollAnimator.Tick` (`ScrollAnimator.cs:166-169`) **skip** tracks/entries whose node is parked, and **`HasActive` excludes parked tracks** — so a parked looping animation or mid-fling scroll no longer keeps the app awake (the idle wake-stop works). Time does not advance while parked; tracks resume cleanly on un-park (same as the minimize behavior, where `dtMs` is read inside `Paint`).
- Bound-prop effects firing on parked nodes are lower-impact (don't set `HasActive`) and are **out of scope for v1** (a developer pauses their own hot bind via `UseActivation`).

## 5. Public API

```csharp
// In a component:
if (UseIsActive().Value) { /* render the live-only affordance */ }   // reactive, subscribes

UseActivation(
    onActivated:   () => _poll.Start(),     // page foregrounded / window restored
    onDeactivated: () => _poll.Stop());     // page parked OR window minimized
```

Ambient (host-published, read by the hook): `Activation.IsActive` — namespaced (bare `IsActive` collides with `DragController.IsActive`).

## 6. Data flow

```
tab switch away → KeepAlive DeactivateKeepAliveEntry → SetSubtreeParked(true)
   → CompEntry.ActiveSig=false  +  NodeFlags.Parked set on subtree
   → UseIsActive memo flips false → UseActivation effect fires onDeactivated
   → AnimEngine/ScrollAnimator skip parked nodes; HasActive drops → idle wake-stop engages
window minimize → AppHost edge → WindowVisible=false (+ one flush) → every component's memo flips → onDeactivated
restore / tab switch back → reverse → ActiveSig/WindowVisible=true → onActivated; deferred renders replay; tickers resume
```

## 7. Edge cases & invariants

- **`onDeactivated` fires ONLY on the park/minimize edge — never on unmount/evict/replace.** Otherwise parked-then-evicted (`FreeKeepAliveEntry` `:757`) and transient non-cacheable entries (`:719`) double-fire. Unmount runs `UseEffect` cleanups as usual.
- **First mount = active**: neither callback fires at mount (you start work in `UseEffect`, pause in `onDeactivated`, resume in `onActivated`). The `Ref<bool>` captures the initial state.
- **Mount under a parked ancestor** initializes inactive (Layer B).
- **Rapid park→un-park in one tick**: the memo collapses the bool (value-gated); callbacks are edge-precise. If churn is a concern, callbacks may be scheduled to end-of-flush — decided in the plan.
- **Parked + minimized simultaneously** → single `onDeactivated` (the memo is one bool).
- **Nested KeepAlive** composes via the same `SetSubtreeParked` traversal (do not add a divergent walk).

## 8. Threading

Entirely UI-thread; **no render-thread-seam interaction**. Window state is read on the UI thread (`AppHost.IsMinimized`); `WindowEvent` (Activated/Occluded) drains in phases 1–2 on the UI thread (`architecture-spec.md:129-132`); the active signal is written on the UI thread in the frame loop (mirroring `Viewport.Size`). Honors the "UI thread owns no `ComPtr`" invariant (`SPEC-INDEX.md:46`) by construction. Off-thread power callbacks marshal via `AppHost.Post`.

## 9. Canon obligations

- **Owner:** `subsystems/reconciler-hooks.md §0bis` (hook semantics + the `IsActive` ambient) + `subsystems/pal-rhi.md` (window-visibility source via `IPlatformWindow.State`).
- **SPEC-INDEX.md §2:** add a "Component activation lifecycle" row (format per the `External-store`/`Prop<T>` rows at `:58`/`:70`).
- **subsystems/README.md §2.4:** add a `UseIsActive / UseActivation` ownership line; also close the pre-existing gap by adding `Flow.KeepAlive` to the control-flow line (`:128`).
- Document the engine auto-quiesce under the animation/scheduling subsystem (`backdrop-effects-animation.md` or `reconciler-hooks.md`, per the ownership map).
- Run `powershell -File design/check-canon.ps1` (must exit 0). The change is purely additive — no stale-token rule is tripped.

## 10. Testing (VerticalSlice golden checks — headless)

- `UseIsActive` flips false on KeepAlive park and true on reactivate (drive via the existing KeepAlive harness).
- `UseIsActive` flips false on `window.State = WindowState.Minimized` and true on restore (`HeadlessWindow.State` is settable — `HeadlessPlatform.cs:94`).
- `UseActivation` fires exactly once per transition, never at mount; combined park+minimize fires once.
- Auto-quiesce: a parked subtree with a `loop:true` animation drops `AnimEngine.HasActive` to false (no idle-wake-defeat); resumes on reactivate. Alloc tripwire stays green across a park/reactivate cycle.

## 11. Implementation layering (plan outline)

1. **Host window-visible signal** + ambient registration + minimize-edge write + `SetWindowActive`.
2. **Reconciler per-component `ActiveSig`** + `SetSubtreeParked` writes + mount-under-parked-ancestor seeding.
3. **Hooks** `UseIsActive` (memo) + `UseActivation` (standalone effect, stable callback cell, Untrack) on `RenderContext` + `Component`.
4. **Engine auto-quiesce**: `NodeFlags.Parked` marker + `AnimEngine`/`ScrollAnimator` Tick + `HasActive` exclusion.
5. **Canon** registration + `check-canon`.
6. **Golden checks** (§10). App adoption (Wavee `SeekTicker` minimize stand-down; `HomePage`/gallery examples) is illustrative, optional in this plan.

## 12. Cross-platform

Portable: the hook consumes the `IPlatformWindow.State`/`WindowEvent` PAL seam (already "Designed" in `macos-debt-ledger.md:37`), not any Win32 API. macOS maps `NSWindow.isMiniaturized` into the same seam. No new cross-platform debt; occlusion stays out of the portable contract (macOS `NSWindowOcclusionState` is richer but asymmetric).

## 13. Risks

- **Minimize-edge flush:** firing `onDeactivated` on minimize requires a flush before the early-return; must not regress the minimize idle behavior (`RecommendedWaitMs == -1`). Verify the flush is one-shot on the edge, not per-idle-frame.
- **`HasActive` exclusion of parked tracks** must be O(1)-ish (a parked-track counter, not a per-frame scan) to avoid a wake-path regression.
- **Mount-under-parked-ancestor** detection adds a small mount-time check; keep it cheap (nearest-ancestor parked flag, not a full walk).

## 14. Key file references

- Park/parked machinery: `Reconciler.cs:25` (`CompEntry`), `:442-461` (`RunComponent` gate `:448`), `:692-720` (deactivate/reactivate), `:782-792` (`SetSubtreeParked`), `:757-763` (free/evict), `:1402` (unmount).
- Reactive core: `Foundation/Signals/{Signal,Effect,Memo,ReactiveCore}.cs` (value-gated writes, memo de-bounce, `Reactive.Untrack`); hooks `RenderContext.cs:246-289` (`UseComputed`/`UseSignalEffect`/`EffectImpl`), `:99-128` (gesture forwarder precedent), `:172-179` (`RunAllCleanups`), `:316-333` (detached-context cache).
- Host/window: `AppHost.cs:268-274,329-341,714-805` (minimize edge/gate, `IsMinimized`), `:619-629` (ambient registration), `:677-682` (`Post`).
- PAL: `Seams/Pal/Pal.cs:33` (`WindowState`), `:340` (`State`); `Headless/Pal/HeadlessPlatform.cs:94` (settable `State`). Win32 `Win32Platform.cs:482-485,701-717,890-892`.
- Animation tickers: `Animation/AnimEngine.cs:169,589-592,653`; `Animation/ScrollAnimator.cs:117,166-169,404`.
- Power (app-side): `WindowsApi/Power/PowerSession.cs:175-194,229-234`; wiring template `WindowsApp/WindowsApiPage.cs:112-120,885-888`.
- Canon: `SPEC-INDEX.md:42-72`, `subsystems/README.md:124-146`, `reconciler-hooks.md §0bis`, `pal-rhi.md:101-102`, `macos-debt-ledger.md:37`.
- Consumers: `app/Wavee/Features/Shell/{ContentHost.cs:21,WaveeShell.cs:208}`, `SeekBar.cs:306-320`, `FakePlaybackProvider.cs:94`.
