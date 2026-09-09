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

**`Visible : Prop<bool>` — the presence channel (P1, layout.md §4.7).** Bound the same way (`Prop.Of(() =>
sig.Value)` or a signal-direct bind), but it does not write a paint/layout SCALAR — it flips the node between
mounted-and-flowing and collapsed (out of layout, paint and hit-test; CSS `display:none`, not `visibility:hidden`).
The bind is still mount-only and still equality-gated on the RESOLVED value, so it composes with the rest of this
list unchanged: re-pushed props (1) can still change WHAT a component renders while its own `Visible` bind (2)
independently governs WHETHER the resulting subtree is in flow. One consequence worth calling out here because it is
a props-contract concern, not just a layout one: collapsing a component's ancestor does **not** stop the component's
own render-effect (props keep landing, bindings keep settling) — it only pauses `UseInterval` (`entry.Hidden` folds
into the same `UseIsActive()` signal `Flow.KeepAlive` parking already writes). A component that wants to skip real
work while hidden should still gate on `UseIsActive()`/`UseActivation()` itself, exactly as it would for a
backgrounded KeepAlive tab — presence and KeepAlive parking are two edges into the ONE activation signal, not two
things to special-case separately.

### 3. Context (`Ctx.Provide` + `UseContext`/`UseRequiredContext`) — ambient / coordination

Broadcast state for a *subtree of many/unknown* consumers: theme `Epoch`, flow-direction, a `NavigationView`
`IndicatorTarget`, a `SplitView` `PaneLink`, a form scope. One provider, N opt-in consumers. `UseRequiredContext<T>`
throws (naming the type) when unprovided — use it when a missing provider is a bug, not a silent default. **Do NOT use
context for point-to-point parent→child data** — that's re-pushed props (1); context is for "who reads this is not
known to the writer." (reconciler-hooks §8bis draws the line.)

### 4. A `Key` remount — the item *identity* changed

Use a changed `Key` when the component represents a different identity and its transient state should reset
(for example a different detail entity). A remount drops focus, popup, editing and pager state. Changing collection
contents, order, filters, or metadata does not itself require a new component identity: a stable `BoundItemsSource`
reports its content/order revision and occurrence keys to the retained list. Use the list's documented update API.

Genuinely-static config (an initial open state, a fixed dimension, a one-time mount seed) may stay a plain field.

## Choosing the mechanism (decision table)

| The data is… | Use | Not |
|---|---|---|
| a per-instance value/flag/slots a parent hands its child | **re-pushed props (1)** — `Embed.Comp(props, …)` / `[Props]` | a hand-rolled `Ctx.Provide` channel (that's what G4d deleted) |
| a hot scalar (slider/scroll/progress/bound transform) | **a bind (2)** | `setState` per move (render churn) |
| ambient state broadcast to many/unknown consumers | **context (3)** | re-pushed props (there's no single child) |
| a different component identity whose local state should reset | **a `Key` remount (4)** | metadata/content revisions used as remount keys |
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
- A changing collection uses stable `BoundItems` / `ItemsView.CreateBound`; publish source revisions and preserve occurrence keys. A count-only mount seed is not a live data contract.
- Ambient state for many consumers → **context (3)**, and prefer `UseRequiredContext` when a missing provider is a bug.

## Retained shelf authoring (2026-09)

`PagedShelf.Create<T>` takes one immutable `IReadOnlyList<T>` snapshot and a `Func<T, int, float, Element>` card
builder. The control re-pushes items, title/header, custom pager, keys, maximum item count and visible-range callback
to the retained `IPropsHost` core. It projects those items into one stable `BoundItemsSource<T>` and renders through
`ItemsView.CreateBound`; the application does not maintain a second mutable shelf collection.

```csharp
PagedShelf.Create(cards,
    (card, index, width) => BuildCard(card, width),
    cardHeight: width => width + 48,
    title: title,
    keyOf: (card, index) => card.OccurrenceKey,
    onVisibleRange: (first, lastExclusive) => ReportVisible(first, lastExclusive));
```

Use the passed **current item** for labels and actions. Do not have retained callbacks index a list captured when
the shelf first mounted. Immutable snapshot replacement updates same-count contents in the same flush while keeping
the viewport, pager, fractional scroll position, open popup and occurrence focus. Source content revisions invalidate
measurement when content size changes; they do not remount the shelf. Growth/shrink clamps the retained pager to the
new range. `Responsive.Of` likewise re-pushes its builder instead of retaining an old parent closure.

### What a props record may compare — delegates, Elements, and the data gate

A props record's **equality is the re-render gate**: the reconciler delivers the new record through the child's props
signal, and a value-equal write is coalesced (`Signal<T>.SetIfChanged`), so the child is re-rendered exactly when its
props record says it changed. The default record `Equals` compares *every* member, which quietly makes the gate
unreachable for two member shapes:

- **A delegate member.** A lambda allocates a **fresh closure on every parent render** — equal `Method`, new `Target` —
  so `Delegate.Equals` is false forever. A record holding a `Func`/`Action` therefore never compares equal and the gate
  never holds: every parent render re-renders the child and everything it builds.
- **An `Element` member.** `Element` is a record whose `Children` is an **array**, compared by reference. A
  rebuilt-but-identical subtree is never equal, and a deep compare would be both costly and wrong (it would have to
  compare the handlers hanging off the tree).

So a props record that gates on data **defines `Equals`/`GetHashCode` explicitly** (records permit this) and splits its
members three ways:

| Member | Compared | Rule |
|---|---|---|
| the item snapshot (`Items`) | reference first, else the **clamped prefix** (`min(Count, MaxItems)`) element-by-element through `EqualityComparer<T>.Default` | immutable domain **records** therefore compare by VALUE — a parent that re-projects its array on every publication still gates, with no memoization at the call site. A non-record `T` degrades to per-element reference equality: safe (never falsely equal), just less effective |
| scalars that change what is rendered (`MaxItems`, `Fallback`, `Grow`, `Title`) | by value | — |
| delegates (`CardAt`, `KeyOf`, `CustomPager`, `OnVisibleRange`, `Build`) | **IGNORED** | see the contract below |

**The delegate contract.** A component captures its delegates as *behaviour*, not data: **what a card renders must be a
function of its item**, plus stable behaviour the closure captures (a navigate/play callback). State the card PAINTS —
"saved", "playing", a live accent — belongs **in the item**, on a signal the card itself reads, or behind its own
component; it must never be reached through the ignored closure, because a closure change alone schedules nothing. The
shelf still invokes the **newest** delegates the parent pushed (`PagedShelfCore._latest` is a plain field, deliberately
not a signal), so behaviour never goes stale — only the *scheduling* is gated. A re-push whose delegate **`Method`**
differs (a genuinely different lambda, not a fresh closure of the same one) trips the DEBUG-only report-only
`ReuseGuard.IgnoredDelegateChanged` once per component; changing behaviour that must repaint means changing the data or
re-keying the component.

**Chrome is not data.** `PagedShelf`'s `title`/`header` ride a **second** signal (`ShelfChrome`: title by value, header
by **reference**, pager presence as a bool), so a caller that rebuilds its `header:` Element on every render re-renders
the shelf's own header row and **not one card**. A shelf whose caller passes `title:` (or a stable header instance)
gates end to end. This is the honest split: an Element cannot be value-compared, but it also must not be allowed to drag
the card set with it.

**`Responsive.Of` has two overloads, and only one can gate.** `Of(build, …)` is **ungated by design** — its closure IS
its data channel (freezing it at mount is the stale-content bug class the box used to have), so it rebuilds whenever its
parent does. `Of(state, (state, width) => …, …)` gates on `state` by value and ignores the builder; hand it every value
the subtree paints as one immutable record or a tuple of scalars/records. `ResponsiveBox.Props.Gate` carries that state;
a null gate selects the ungated comparison.

Gates: `gate.shelf.props.*` (a new list instance of equal items rebuilds no cards; chrome refreshes without rebuilding
cards; a changed item and a changed `MaxItems` both pass the gate; the gate is hot-phase-allocation-free) and
`gate.responsive.props.*` (state-gated skip, ungated rebuild, state change rebuilds), in `ShelfBindingChecks`.

Sizing/layout/snap options that the factory still passes to the core constructor remain mount configuration. The
live data contract does not make every constructor argument dynamic. See the concrete `PagedShelf.Create` signature
and `ShelfProps<T>` for the live set, and `virtualization.md` for source revision and recycling semantics.

Mounted shelf binding gates cover current item/action delivery, live chrome, retained viewport/pager/focus,
park/resume, grow/shrink, content measurement and the existing hot-phase allocation gates. Pure app DTO tests cannot
establish those engine behaviors.

## Status (2026-07)

Landed (flagship overhaul): the re-pushed-props substrate (`Embed.Comp(props, factory)` + `UseProps<T>` +
`CompEntry.PropsSig` + `IPropsHost`, G4c); the `[Props]`/`[Prop]` generator (G4e); all **17** former
`Props.Channel`/`EnabledChannel`/`SlotsChannel`/`RangedSliderProps` controls migrated and their channel statics
deleted (G4d, ledger-verified 0 remain in the kit). `ReuseGuard` + the `gate.reuse.*` gates survive as the legacy
tripwire for propless field-carrying components; `FGRP001/FGRP002` (Warning) are the compile-time lints.
