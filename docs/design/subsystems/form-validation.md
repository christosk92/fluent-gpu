# Subsystem: Form validation (`FluentGpu.Forms`)

*Design-of-record for native form/input validation — the feature WinUI never gave a good answer to. Owns the
`Validator<T>`/`Field<T>`/`FormScope`/`MsgId`/`FieldError`/`ValidationTiming`/`FieldBinding` contracts, the
`UseField`/`UseForm` hooks, the `BoxEl.Validation` visual channel, the per-control `Field` prop convention, and the
optional `[Validatable]` source-gen accelerator. This is a **feature** subsystem (sits with `controls.md`/`theming.md`),
not `validation.md` — which owns the CI machinery (spikes + gates), a different thing entirely.*

> Precedence: `SPEC-INDEX.md` §2. This doc REFERENCES (never restates) artifacts it does not own:
> `Memo<T>`/`Signal<T>` (`reconciler-hooks.md`/foundations), `Loc`/`StringTable` (foundations + the Localization
> subsystem), the `BoxEl` record shape + bind channels (`reconciler-hooks.md` §0bis / `dsl-aot.md`), the a11y columns
> (`input-a11y.md` + `scene-memory.md`), and `Tok.SystemFillCritical` (`theming.md`).

## 0. Contract summary

- **The bet:** *validity is a derived reactive value, never stored error state* (the SolidJS/Preact insight, expressed
  in the engine's own equality-gated `Memo<T>`). This beats `INotifyDataErrorInfo` on every axis: cross-field and
  conditional rules are FREE (a rule that reads a sibling `Signal.Value` inside the field's error memo auto-subscribes,
  so the dependent field re-validates when the sibling moves — no `ErrorsChanged`, no dependency wiring); async is
  race-immune (an out-of-order completion lands in an equality-gated signal the memo merges); a11y is automatic from
  one handle; and it is reflection-free (NativeAOT) and zero-alloc on the keystroke path.
- **F1 — Rule.** `delegate MsgId Validator<in T>(T value)`; `MsgId.None` == valid. Pure delegate, no attributes/reflection.
  `Rules.*` factories: `Required`/`MinLength`/`MaxLength`/`Matches`/`Range`/`Predicate`/`When` (conditional) /`Equals`
  (cross-field). Each interns its message once (captured by value) → calling a rule per keystroke allocates nothing.
- **F2 — Message = a localization KEY.** `MsgId` carries a loc key (or `Msg.Literal` text), resolved via `Loc.Get` at
  the bound error `TextEl` thunk — i18n + culture-reactive for free, zero-alloc on a table hit. Built-in rules use
  no-arg keys (arg-bearing/`Loc.Format` messages would allocate, so they stay off the hot path).
- **F3 — Field.** `UseField(signal, rules…)` returns `Field<T>`: a **gated** `Memo<FieldError>` (`Error` — what a
  control DISPLAYS, silent per `ValidationTiming`), an **ungated** `Memo<bool>` (`IsValid` — submit gating), async
  `IsValidating`, `Touched`/`MarkTouched`, the control-`Node` signal, and `SetServerError`. `FieldBinding` is the
  non-generic bundle (`Error`+`MarkTouched`+`Node`) a control's chrome consumes (so string/double/int fields share one
  path).
- **F4 — Form.** `UseForm()` → `FormScope` (the `EditContext` analogue, signal-backed): a conjunction `IsValid` memo
  over registered fields (submit gating), `SubmitAttempted`, `Validate()` (reveal all + focus-first-error via `UsePost`,
  never a synchronous flush in an input handler), and `Register` returning an `IDisposable` (deregistered on unmount —
  no `FormScope` leak). Same-component fields auto-join via a thread-local "form under construction" (context resolves
  by scene-ancestor walk, which cannot see a sibling provider — so a thread-local, not `UseContext`, is the join path);
  a nested subtree joins via `Ctx.Provide(FormScope.Context, scope, child)`.
- **F5 — Timing.** `ValidationTiming` default `OnTouched` (silent until first blur, then live — no red on load, the
  WCAG-friendly UX). `OnChange`/`OnBlur`/`OnSubmit`/`OnChangeAfterFirstError` available per field.
- **F6 — Visual.** ONE new bound channel `Prop<ValidationState> Validation` on `BoxEl` (`{None,Warning,Error}`): the
  reconciler resolves `Error`→`Tok.SystemFillCritical` ON THE UI THREAD (the recorder stays theme-agnostic), writing a
  resolved `NodePaint.ValidationBorder` **equality-gated** (a no-op validity marks no `PaintDirty`); the recorder forces
  a SOLID error ring over any resting gradient border. The message row is a shared `FieldVisuals.MessageRow` — a
  `Flow.Show` that occupies ZERO layout space while valid and reveal-animates in (`LayoutTransition` Size=Reflow +
  Opacity) when an error appears.
- **F7 — Async.** `FieldOptions.Async` (a debounced server check): a mount-time reactive `Effect` subscribes the value,
  (re)arms one reused `Timer` (alloc-free per keystroke), and on fire cancels the stale `CancellationTokenSource`, runs
  the check, and `UsePost`s the result to `SetServerError`. Server errors bypass the touched/submit gate (a
  just-submitted server error always shows).
- **F8 — Source-gen (optional accelerator).** `[Validatable] partial` + `[Required]`/`[MinLength]`/`[MaxLength]`/
  `[Range]`/`[RegexMatch]` on members → `FluentGpu.Validation.SourceGen` emits a nested
  `static class Validators { static readonly Validator<T>[] Member; }`, lowering to the IDENTICAL `Validator<T>` path.
  Pure ergonomics; zero runtime footprint; the hand-written `Rules.*` delegate path needs it not.

## 1. As-built file map

- `src/FluentGpu.Engine/Forms/` — `Msg.cs` (`MsgId`/`Msg`), `Validator.cs` (`Validator<T>`/`FieldError`/
  `ValidationTiming`), `Rules.cs`, `Field.cs` (`Field<T>`/`FieldBinding`/`FieldOptions<T>`), `Form.cs`
  (`FormScope`/`IFieldEntry`), `ValidationAttributes.cs` (the `[Validatable]` family).
- `src/FluentGpu.Engine/Hooks/RenderContext.Validation.cs` — `UseField`/`UseForm` + async + the unmount-disposal cell.
- `src/FluentGpu.Engine/Dsl/Element.cs` — `enum ValidationState` + `BoxEl.Validation`.
- `src/FluentGpu.Engine/Scene/Columns.cs` — `NodePaint.ValidationBorder` (resolved color). `Render/SceneRecorder.cs` —
  the override. `Reconciler/Reconciler.cs` — the equality-gated bind.
- `src/FluentGpu.Controls/` — `FieldVisuals.cs` (the message row) + the `Field` prop on `EditableText`/`TextBox`/
  `PasswordBox`/`NumberBox`/`ComboBox`/`AutoSuggestBox`.
- `src/FluentGpu.Validation.SourceGen/` — the `[Validatable]` generator (isolated netstandard2.0 analyzer).
- Gates: `FluentGpu.VerticalSlice` checks **V1–V7** (zero-alloc rule eval, cross-field, touched-gating, submit, async
  merge, deregistration, source-gen) — all green.

## 2. AOT / zero-alloc

Zero reflection, zero `DataAnnotations`, zero expression trees. `Validator<T>` are concrete delegates (static-dispatch
call sites + instantiated generics; trimmer-safe, no `[DynamicDependency]`). On the keystroke path: `FieldError` is a
struct carrying a `MsgId` (never a built string); `FirstFailing` is an alloc-free `for`-loop over a mount-captured rule
array; the `Memo` absorbs no-op recomputes; the message resolves via no-arg `Loc.Get` (no-alloc on a hit — preload all
`validation.*` keys in every culture table so the `[key]` miss-path never fires); the async deps never ride a per-render
`object[]`. Footprint: delegates + 2 hooks + 1 context class + 1 channel + 1 recorder branch; the generator is build-time
only, 0 runtime footprint. The one cold edge is `Rules.Matches`'s `Regex` (interpreted under `TrimMode=full`, runs on
the blur/keystroke cold path only; apps may pass a `[GeneratedRegex]`).

## 3. Accessibility (honest scope)

Today only `AutomationRole → InteractionInfo.Role` is built; `DescribedBy`/`UseAnnounce`/`IsDataValidForForm` are
`input-a11y.md` design contracts, not yet code. v1 ships the VISUAL (a present-but-revealed-on-error, non-focusable
critical-color message node) + focus-first-error on submit; it does **not** yet announce "invalid" to Narrator (parity
with WinUI, not a regression). The activation seam, when the `input-a11y.md` columns land: set `DescribedBy`→error node,
flip `IsDataValidForForm=false` after touch, mark the error node `A11yLiveRegion=Polite` (polite on blur, never
assertive — assertive clips the next field's accessible name), one assertive `UseAnnounce(validation.summary)` on
submit. `IsDataValidForForm`/`IsRequiredForForm` are NET-NEW columns this subsystem is the first consumer of (a
coordinated `scene-memory.md` storage + `input-a11y.md` semantics edit), NOT pre-existing L6 columns.

## 4. Ownership / canon

This doc is the single owner of the F1–F8 contracts above. It fills the gap `controls.md` §5.7 already acknowledged
("Validation errors set `A11yInfo.{FullDescription,DescribedBy}`… a `UseAnnounce` reports the error") rather than
litigating a new contract. The `BoxEl.Validation` bindable channel is registered under the reactive element-prop owner
(`reconciler-hooks.md` §0bis / `dsl-aot.md`); the `[Validatable]` `ValidatorGenerator` is registered in `dsl-aot.md`'s
generator catalog. `validation.md` (CI machinery) carries a one-line disambiguation pointing here. Nothing is
superseded (ComboBox's display-only `ErrorText` is KEPT as a convenience that now also accepts a `Field<int>`), so
`check-canon.ps1` needs no new stale-token rule.
