# Component props contract — how a Component receives changing data

**Owner note:** the reconciler/component model itself is owned by [`reconciler-hooks.md`](./reconciler-hooks.md)
(see its §0bis AS-BUILT (2026-07) callout + §8bis for the mechanism); this doc is the **authoring contract** built
on it. Read it before passing data into `Embed.Comp`.

> **REWRITTEN (2026-07, flagship overhaul G4).** The pre-overhaul world was "everything a factory closure captures
> **freezes at mount**; changing data must be a signal, a context, or a re-key." The overhaul added a first-class
> **re-pushed props** channel, so a parent can now hand changing data to a child it embeds — the single largest source
> of app boilerplate is gone. This doc is rewritten around the FOUR delivery mechanisms below. The old
> `Props.Channel`/`EnabledChannel`/`SlotsChannel` per-control workarounds are **deleted** (G4d); the `ReuseGuard`
> tripwire survives, narrowed to the one remaining hazard (a *propless* component carrying caller-data in plain fields).
> The superseded frozen-props-remediation plan (`docs/plans/frozen-props-remediation-plan.md`) is historical.

## The model

A `Component` is autonomous: `Embed.Comp(() => new T { Field = value })` runs the factory **once, at first mount**,
and on every later parent re-render the reconciler **reuses the instance and discards the new factory**
(`Reconciler.UpdateElement`, the `ComponentEl` reuse branch). So a plain field / ctor arg set inside that factory
closure is **frozen at mount**. This is deliberate — fine-grained updates: a parent re-render must not cascade into
rebuilding every descendant. What changed in G4 is that **a parent can now legitimately push new prop values to the
reused instance** without rebuilding it, so "frozen field" is a mistake you opt out of, not an unavoidable law.

## The four ways to get changing data into a child (pick by shape)

### 1. Re-pushed props — a parent hands per-instance data to one child it embeds *(NEW, G4)*

Point-to-point: a parent that *knows* its child delivers a label / count / enabled flag / slots record straight into
the reused instance. Two surfaces, same substrate (`CompEntry.PropsSig` + the reuse-seam delivery — see
reconciler-hooks §8bis):

- **Transport record (untyped):** `Embed.Comp(props, () => new Core())` — props first, factory second
  (`ComponentEl.Comp<T,TProps>(TProps props, Func<T> factory) where TProps : class`). The child reads it with the
  **non-positional** `UseProps<T>()` (or `UsePropsOrDefault<T>()`) in `Render()` — reading subscribes the
  render-effect, so a changed props record (reference short-circuit → **record-equality gate** → `Runtime.Batch`
  coalesce) re-renders exactly that child. The propless `Embed.Comp(() => new T())` still exists for static children.
- **`[Props]` partial component (typed, generated):** mark the component `[Props]` and each live field `[Prop]`; the
  `PropsGenerator` emits per-field `Signal<T>` backing + subscribing partial getters + `XxxProp` bind accessors + a
  nested `PropsData` record + an `Of(...)` factory + `CurrentProps()`/`From(...)` snapshot helpers + a build-time
  `PropsManifest` skippability report. `ApplyProps` writes **only the changed fields** (per-field equality gate), so a
  parent re-render that changes one field notifies one field. **Delegate props ride a stable latest-write forwarder:**
  a fresh lambda every render does NOT re-render the child, but the wired handler always invokes the newest delegate
  (Compose Strong-Skipping shape). `ToggleSwitchCore`/`CheckBoxCore`/… are the shipped exemplars. Diagnostics:
  `FGSG001-005` (get-only partial / class partial / must-derive-Component / delegate >4 params degrades to a raw field
  / collection prop is reference-compared).

Use for: the common case — a control's caller-supplied label, count, isEnabled, header, slots. This is what ~19 kit
controls hand-rolled as `Props.Channel` providers before G4d.

### 2. A `Signal`/`Prop<T>` bind — a hot scalar on the compositor path

A high-frequency value (slider scrub, scroll offset, progress, a bound transform/opacity/fill) is a **bound
`Prop<T>`** or a signal the child reads. A bound `Transform`/`Opacity`/`Fill` updates the node **compositor-only** — no
render/reconcile/layout (the "slider tank" win); a bound `Width`/`Height`/`Text` triggers scoped relayout. Bind wiring
is **mount-only** — change the signal's *value*, never swap the signal (swap ⇒ re-key). Exemplar: `Slider.Create(
FloatSignal value)` (the one slider API), any `Signal<int> SelectedIndex`, `ItemsView` displacement.

### 3. Context (`Ctx.Provide` + `UseContext`/`UseRequiredContext`) — ambient / coordination

Broadcast state for a *subtree of many/unknown* consumers: theme `Epoch`, flow-direction, a `NavigationView`
`IndicatorTarget`, a `SplitView` `PaneLink`, a form scope. One provider, N opt-in consumers. `UseRequiredContext<T>`
throws (naming the type) when unprovided — use it when a missing provider is a bug, not a silent default. **Do NOT use
context for point-to-point parent→child data** — that's re-pushed props (1); context is for "who reads this is not
known to the writer." (reconciler-hooks §8bis draws the line.)

### 4. A `Key` remount — the item *identity* changed

When the item *set* changes (a different entity, a refiltered list), give the list/wrapper a changed `Key` and let it
mount fresh; `scrollKey` preserves scroll offset across the remount. Use for identity changes only — a re-key drops
transient state (open popup, focus, in-flight edit), so never re-key for a value that merely changed. Exemplar:
`DetailTracks.cs` (`Key = "list:...t{tier}:d{density}:q{query}:f{flags}"`).

Genuinely-static config (an initial open state, a fixed dimension, a one-time mount seed) may stay a plain field.

## Choosing the mechanism (decision table)

| The data is… | Use | Not |
|---|---|---|
| a per-instance value/flag/slots a parent hands its child | **re-pushed props (1)** — `Embed.Comp(props, …)` / `[Props]` | a hand-rolled `Ctx.Provide` channel (that's what G4d deleted) |
| a hot scalar (slider/scroll/progress/bound transform) | **a bind (2)** | `setState` per move (render churn) |
| ambient state broadcast to many/unknown consumers | **context (3)** | re-pushed props (there's no single child) |
| a change of item **identity** (the set changed) | **a `Key` remount (4)** | a bind/props (they update in place; identity needs a fresh mount) |
| genuinely static (mount seed) | a plain field | — |

## The ReuseGuard tripwire (`Hooks/ReuseGuard.cs`) — the legacy safety net

A DEBUG-only correctness tripwire (the reconciler twin of `RenderBudget`), now **narrowed to the residual hazard**: a
component that neither takes re-pushed props nor reads its caller-data through a signal/context, but carries it in a
**plain scalar field** an `Embed.Comp` factory sets. When the reconciler reuses such an instance it hands the live
instance the would-be replacement via `Component.DebugCheckReuse(next)`; the control overrides that to compare and
call `ReuseGuard.Violation(...)`.

- Gated by `ReuseGuard.CompiledIn` (`DEBUG || FLUENTGPU_DIAG`) → dead-code-eliminated in the shipping AOT binary, zero
  cost ("production safety == CI coverage", `validation.md` §0). Off at runtime by default; `FG_REUSE_GUARD=1` to arm,
  `FG_REUSE_GUARD_THROW=1` to hard-fail. Enforced by `gate.reuse.*` in the VerticalSlice (`FrozenPropProbe`).
- The **`FGRP001`** analyzer (frozen Element-as-field into `Embed.Comp`) + **`FGRP002`** (mount-snapshot `Prop.Of`
  capture) are the compile-time counterparts; they now recommend the re-pushed-props / `[Props]` fix.

## Authoring checklist for a new control

- Does any `Create` parameter carry data that can change while mounted (label, count, items, enabled, content)?
  → deliver it via **re-pushed props (1)** (`Embed.Comp(props, …)` or a `[Props]` core), or a **bind (2)** for a hot
  scalar — **not** a plain field through `Embed.Comp`, and **not** a hand-rolled context channel.
- Any plain field you *do* keep (a genuine mount seed) → list it; if it is scalar caller-data, add a
  `DebugCheckReuse` compare so misuse trips `ReuseGuard`.
- A list control whose *set* can change → its callers re-key (4); the `ItemsView` guard reports a missing re-key.
- Ambient state for many consumers → **context (3)**, and prefer `UseRequiredContext` when a missing provider is a bug.

## Status (2026-07)

Landed (flagship overhaul): the re-pushed-props substrate (`Embed.Comp(props, factory)` + `UseProps<T>` +
`CompEntry.PropsSig` + `IPropsHost`, G4c); the `[Props]`/`[Prop]` generator (G4e); all **17** former
`Props.Channel`/`EnabledChannel`/`SlotsChannel`/`RangedSliderProps` controls migrated and their channel statics
deleted (G4d, ledger-verified 0 remain in the kit). `ReuseGuard` + the `gate.reuse.*` gates survive as the legacy
tripwire for propless field-carrying components; `FGRP001/FGRP002` (Warning) are the compile-time lints.
