using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Forms;

/// <summary>Per-field configuration for <c>UseField</c> (timing, compound errors, async, and an explicit form join).</summary>
public sealed class FieldOptions<T>
{
    /// <summary>When errors first surface to the user. Default <see cref="ValidationTiming.OnTouched"/>.</summary>
    public ValidationTiming Timing = ValidationTiming.OnTouched;

    /// <summary>False (default) surfaces only the first failing rule (short-circuit, zero-alloc); true counts all
    /// failures into <see cref="FieldError.FailCount"/> (the first message is still what displays).</summary>
    public bool AllErrors;

    /// <summary>An async/server check (e.g. "email already taken"). Debounced by <see cref="AsyncDebounceMs"/> and
    /// cancel-stale via a per-field <see cref="CancellationTokenSource"/>; the result merges into the field's error
    /// race-immune (an out-of-order completion cannot corrupt state because it writes an equality-gated signal).</summary>
    public Func<T, CancellationToken, Task<MsgId>>? Async;

    /// <summary>Debounce window (ms) before an <see cref="Async"/> check runs after the value settles.</summary>
    public int AsyncDebounceMs = 400;

    /// <summary>Join this field to an explicit form. Null ⇒ the nearest <c>UseForm()</c> being built in the same render
    /// (or a <c>Ctx.Provide</c>d <see cref="FormScope.Context"/> for a nested subtree), else the field stands alone.</summary>
    public FormScope? Form;
}

/// <summary>The non-generic validation surface a control's CHROME consumes — decoupled from the field's value type
/// <c>T</c> so one EditableText-based path serves string/double/int fields alike. It bundles the gated error memo
/// (drives the invalid border + message), the touched action (a control calls it on blur), and the control-node signal
/// (published for focus-first-error). Obtained from <see cref="Field{T}.Binding"/>.</summary>
public readonly record struct FieldBinding(IReadSignal<FieldError> Error, Action MarkTouched, Signal<NodeHandle> Node);

/// <summary>
/// The reactive handle a control consumes via its <c>Field</c> prop. All members are signals/memos — reading them in a
/// control's render/bind subscribes only that control, with zero per-frame allocation. A control typically uses just
/// <see cref="Error"/> (to display) and <see cref="MarkTouched"/> (from its blur hook); the rest drive form orchestration.
/// </summary>
public sealed record Field<T>(
    /// <summary>The caller-owned value signal, unchanged (the controlled-control invariant).</summary>
    IReadSignal<T> Value,
    /// <summary>What the control DISPLAYS — gated by <see cref="ValidationTiming"/> (so a pristine field shows nothing).</summary>
    Memo<FieldError> Error,
    /// <summary>True validity, UNGATED — drives submit enabling and <see cref="FormScope.IsValid"/>.</summary>
    Memo<bool> IsValid,
    /// <summary>True while an async check is pending (drive a spinner).</summary>
    Memo<bool> IsValidating,
    /// <summary>Set true once the field has lost focus — read by the timing gate.</summary>
    Signal<bool> Touched,
    /// <summary>The control's node, published by the control in a layout effect; used for focus-first-error on submit.</summary>
    Signal<NodeHandle> Node,
    /// <summary>Mark the field touched — a control calls this from its <c>OnFocusChanged(false)</c> blur hook.</summary>
    Action MarkTouched,
    /// <summary>Inject a server error (the <c>Validation.MarkInvalid</c> analogue). Always displays, bypassing the
    /// touched/submit gate — a just-submitted server error must be visible even on a pristine field.</summary>
    Action<MsgId> SetServerError)
{
    /// <summary>The non-generic chrome binding (border + touched + node), passed to an input control's <c>Field</c> prop.</summary>
    public FieldBinding Binding => new(Error, MarkTouched, Node);
}
