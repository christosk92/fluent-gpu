# Component props contract — how a Component receives changing data

**Owner note:** the reconciler/component model itself is owned by [`reconciler-hooks.md`](./reconciler-hooks.md);
this doc is the **authoring contract** built on it. Read it before passing data into `Embed.Comp`.

## The rule

Components are **autonomous**. `Embed.Comp(() => new T { Field = value })` runs the factory **once, at first
mount**. On every later parent re-render the reconciler reuses the instance and **discards the new factory**
(`TreeReconciler.Update` → the `ComponentEl` reuse early-return in `Reconciler.cs`). So any plain field or ctor arg
set inside that factory closure is **frozen at mount** — a parent that later passes a new value silently keeps the
stale one. This is deliberate (fine-grained, near-zero-allocation updates — a parent re-render must not cascade into
rebuilding every descendant); the hazard is only that the *syntax* looks like a React prop that would flow.

**Changing data must reach a child by exactly one of:**

1. **A `Signal`/`Func` the child reads in `Render()`** — reading `.Value` subscribes it; a write re-renders just it.
   Exemplar: `LazyGrid.cs` (`int count = _count();`), `ItemsView` `DisplacementVersion`, any `Signal<int> SelectedIndex`.
2. **`Ctx.Provide` + `UseContext`** — context is signal-backed per provider node, so a provider value change
   re-renders the consuming component even though its own fields are frozen. Exemplar: `SelectorBar.cs` (the
   `Props.Channel` provider + core `UseContext`), `SettingsCard`, `ToggleSwitch`, `Expander` (`SlotsChannel`),
   `ComboBox`/`NumberBox` (`EnabledChannel`), `ToolTip` (`SlotsChannel`).
3. **A remount forced by a changed `Key`** — the item *set* changes, so re-key the list/wrapper and let it mount
   fresh; `scrollKey` preserves the scroll offset across the remount. Exemplar: `DetailTracks.cs` (`Key =
   "list:...t{tier}:d{density}:q{query}:f{flags}"`), `NowPlayingView` queue rail, `DiagnosticsPanel` log list.

Genuinely-static config (initial open state, a fixed dimension, a one-time mount seed) may stay a plain field.

## Choosing the mechanism

- **A scalar/flag that flips occasionally** (IsEnabled, a mode) → provider (2). Re-key works too but drops transient
  state (open popup, focus) on every flip.
- **Content/slots (an `Element`), or many props at once** → provider (2) carrying a slots record.
- **A collection whose length/identity changes** → re-key (3); `ItemsView.ItemCount`/`Items` freeze, so a growing or
  refiltered list without a matching Key goes stale (the shipped DiagnosticsPanel/queue-rail bug).
- **A frequently-changing value on the hot path** → a `Signal`/`Prop<T>` bind (1), never a re-key (remount churn).

## The ReuseGuard tripwire (`Hooks/ReuseGuard.cs`)

A DEBUG-only correctness tripwire, the reconciler twin of `RenderBudget`. When the reconciler reuses a component it
hands the live instance the would-be replacement via `Component.DebugCheckReuse(next)`; a control that carries caller
data in scalar fields overrides that to compare and call `ReuseGuard.Violation(...)`.

- Gated by the const `ReuseGuard.CompiledIn` (`DEBUG || FLUENTGPU_DIAG`) → dead-code-eliminated in the shipping AOT
  binary, zero cost, no probe allocation ("production safety == CI coverage", per `validation.md` §0).
- Off by default at runtime; turn it on with `FG_REUSE_GUARD=1`, or `FG_REUSE_GUARD_THROW=1` to hard-fail.
- Enforced by `gate.reuse.*` in the VerticalSlice (`FrozenPropProbe`): fires on a frozen scalar change under reuse,
  stays quiet on re-key / no-change, throws in strict mode, and is const-gated identically to `RenderBudget`.
- Instrumented controls (opt in via `ChecksReuse` + `DebugCheckReuse`): `ItemsView` (effective count), `DropDownButton`,
  `SplitButton`, `AppBarToggleButton`, `CommandBarFlyout`, `Popup` (scalar label/glyph/enabled/text).

## Authoring checklist for a new control

- Does any `Create` parameter carry data that can change while mounted (label, count, items, enabled, content)?
  → deliver it via a `Signal`/`Func` (1) or a provider channel (2); do **not** set it as a plain field through
  `Embed.Comp`. Pure element builders (a `static` method returning the tree, re-invoked each parent render — e.g.
  `Button.cs`) are inherently safe: there is no retained `ComponentEl` to freeze.
- Any plain field you *do* keep (a genuine mount seed) → list it, and if it is scalar caller-data add a
  `DebugCheckReuse` compare so misuse trips the guard.
- A list control whose set can change → document that callers must re-key (3); the `ItemsView` guard already reports a
  missing re-key.

## Status (2026-07-08)

Landed: the guard + gates; `Expander`/`SettingsExpander` (SlotsChannel); `ToolTip` (SlotsChannel); `ComboBox`/
`NumberBox` (EnabledChannel); `ItemsView` guard override; `DiagnosticsPanel` + `NowPlayingView` re-keys; guard
overrides on the 6 scalar hazard controls. Full provider conversion of the remaining element/list-slot fields
(`CommandBar`/`MenuBar`/`DropZone` content, the PARTIAL pickers/dialogs long tail) is batchable follow-up — the guard
and the `FluentGpu.SourceGen` analyzer (compile-time `Embed.Comp` frozen-arg lint) cover them meanwhile. The full
sweep + plan: [`docs/plans/frozen-props-remediation-plan.md`](../../docs/plans/frozen-props-remediation-plan.md).
