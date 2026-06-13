using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

/// <summary>A hook cell that owns a registration handle (a field's membership in a form), disposed on unmount.</summary>
internal sealed class DisposableCell : HookCell, IDisposableCell
{
    public IDisposable? Disposable;
    public void DisposeCell() { Disposable?.Dispose(); Disposable = null; }
}

/// <summary>A hook cell owning a mount-time reactive <see cref="Effect"/> (and an optional extra disposable — the async
/// field state's timer/CTS), torn down on unmount.</summary>
internal sealed class ReactiveEffectCell : HookCell, IDisposableCell
{
    public Effect? Effect;
    public IDisposable? Extra;
    public void DisposeCell() { Effect?.Dispose(); Extra?.Dispose(); }
}

/// <summary>
/// Form-validation hooks (form-validation.md). <c>UseField</c>/<c>UseForm</c> are built almost entirely from the public
/// hooks (<see cref="UseSignal{T}"/>/<see cref="UseComputed{T}"/>/<see cref="UseRef{T}"/>/<see cref="UsePost"/>/
/// <see cref="UseContext{T}"/>), so each call consumes a stable, fixed cell sequence (rules-of-hooks). The one private
/// addition is <see cref="UseRegistration"/>, which disposes the field's form membership on unmount via the existing
/// <see cref="IDisposableCell"/> cleanup path.
/// </summary>
public sealed partial class RenderContext
{
    /// <summary>Register once at mount and dispose on unmount (RunAllCleanups). Returns the live handle (or null).</summary>
    private IDisposable? UseRegistration(Func<IDisposable?> register)
    {
        DisposableCell cell;
        if (!_mounted) { cell = new DisposableCell { Disposable = register() }; _cells.Add(cell); }
        else cell = (DisposableCell)_cells[_cursor];
        _cursor++;
        return cell.Disposable;
    }

    private static class DefaultOptions<T>
    {
        public static readonly FieldOptions<T> Value = new();
    }

    private static bool Gate(ValidationTiming t, bool touched, bool submitted) => t switch
    {
        ValidationTiming.OnChange => true,
        ValidationTiming.OnSubmit => submitted,
        _ => touched || submitted,            // OnBlur / OnTouched / OnChangeAfterFirstError
    };

    /// <summary>Validate <paramref name="value"/> against <paramref name="rules"/> with default options. See the
    /// options overload for timing/async/compound-error/explicit-form control.</summary>
    public Field<T> UseField<T>(Signal<T> value, params Validator<T>[] rules)
        => UseField(value, DefaultOptions<T>.Value, rules);

    /// <summary>
    /// Create a reactive validation field over the caller-owned <paramref name="value"/> signal. The returned
    /// <see cref="Field{T}"/> exposes a gated error memo (what a control displays), an ungated validity memo (submit
    /// gating), touched/async state, and the control-node + touched plumbing. If a form is being built in this render
    /// (<c>UseForm()</c>), via <see cref="FieldOptions{T}.Form"/>, or provided through <see cref="FormScope.Context"/>,
    /// the field joins it (and deregisters on unmount).
    /// </summary>
    public Field<T> UseField<T>(Signal<T> value, FieldOptions<T> options, params Validator<T>[] rules)
    {
        var opts = options ?? DefaultOptions<T>.Value;
        var touched = UseSignal(false);
        var server = UseSignal(MsgId.None);
        var node = UseSignal<NodeHandle>(default);
        var validating = UseSignal(false);
        ValidationTiming timing = opts.Timing;
        bool allErrors = opts.AllErrors;

        // Resolve the form to join: an explicit one, the nearest one under construction this render, or a provided one.
        // (UseContext costs no cell, so this conditional resolution is rules-of-hooks-safe.)
        FormScope? form = opts.Form ?? FormScope.Building ?? UseContext(FormScope.Context);

        // What the control displays: gated by timing; a server error always shows (bypasses the gate).
        var error = UseComputed(() =>
        {
            MsgId srv = server.Value;                                  // always subscribe
            if (!srv.IsEmpty) return new FieldError(srv, 1);
            bool submitted = form?.SubmitAttempted is { } sa && sa.Value;
            if (!Gate(timing, touched.Value, submitted)) return FieldError.Valid;
            return Rules.FirstFailing(rules, value.Value, allErrors);  // reads value.Value (+ any sibling signals) → subscribes
        });

        // True validity (ungated) for submit gating: rules pass AND no server error.
        var isValid = UseComputed(() =>
        {
            if (!server.Value.IsEmpty) return false;
            return Rules.FirstFailing(rules, value.Value, false).IsValid;
        });

        var isValidating = UseComputed(() => validating.Value);

        // Stable Field handle (created once; its members are all persistent signals/memos).
        var fieldRef = UseRef<Field<T>?>(null);
        if (fieldRef.Value is null)
        {
            Action markTouched = () => touched.Value = true;
            Action<MsgId> setServer = m => server.Value = m;
            fieldRef.Value = new Field<T>(value, error, isValid, isValidating, touched, node, markTouched, setServer);
        }
        Field<T> field = fieldRef.Value;

        // Async/server validation (debounced + cancel-stale, off the paint path). The hook is always invoked so the
        // cell order is stable whether or not an async check is configured.
        UseAsyncValidation(value, opts, server, validating);

        // Join the form once at mount (deregistered on unmount). Kept unconditional for stable cell order.
        UseRegistration(() => form?.Register(new FieldEntry<T>(field)));

        return field;
    }

    /// <summary>
    /// Establish a <see cref="FormScope"/> for this component and mark it the "form under construction" so the
    /// <c>UseField</c> calls that follow in the same render auto-join it. For fields in a CHILD component, wrap that
    /// child in <c>Ctx.Provide(FormScope.Context, scope, child)</c>.
    /// </summary>
    public FormScope UseForm()
    {
        var scopeRef = UseRef<FormScope?>(null);
        FormScope scope = scopeRef.Value ??= new FormScope();

        scope.Membership = UseSignal(0);
        scope.SubmitAttempted = UseSignal(false);
        scope.Post = UsePost();
        scope.Hooks = UseContext(InputHooks.Current);
        scope.IsValid = UseComputed(scope.ComputeIsValid);
        scope.IsValidating = UseComputed(scope.ComputeIsValidating);

        FormScope.Building = scope;
        return scope;
    }

    /// <summary>
    /// Wire a field's async/server check (when <see cref="FieldOptions{T}.Async"/> is set). A mount-time reactive
    /// <see cref="Effect"/> subscribes to the value signal; on each change it (re)arms a single reused
    /// <see cref="Timer"/> for the debounce — so the per-keystroke path on the UI thread does no allocation. When the
    /// timer fires (off the UI thread) it cancels any stale request and runs the check, then posts the result back via
    /// the UI poster. Out-of-order completion is race-immune: the result lands in an equality-gated signal the field's
    /// error memo merges. The hook is always invoked (even with no async) so the cell sequence stays stable.
    /// </summary>
    private void UseAsyncValidation<T>(Signal<T> value, FieldOptions<T> opts, Signal<MsgId> server, Signal<bool> validating)
    {
        var stateRef = UseRef<AsyncFieldState<T>?>(null);

        ReactiveEffectCell cell;
        if (!_mounted)
        {
            cell = new ReactiveEffectCell();
            _cells.Add(cell);
            if (opts.Async is not null)
            {
                var st = new AsyncFieldState<T>(opts, server, validating, UsePost());
                stateRef.Value = st;
                cell.Extra = st;
                // Re-runs whenever the value changes; the first (mount) run is skipped inside OnValueChanged.
                cell.Effect = new Effect(Rt, () => st.OnValueChanged(value.Value));
            }
        }
        else cell = (ReactiveEffectCell)_cells[_cursor];
        _cursor++;
    }

    /// <summary>Per-field async state: a single reused debounce timer + a cancel-stale CTS. Created once; disposed on
    /// unmount via <see cref="ReactiveEffectCell"/>.</summary>
    private sealed class AsyncFieldState<T> : IDisposable
    {
        private readonly Func<T, CancellationToken, Task<MsgId>> _async;
        private readonly int _debounceMs;
        private readonly Signal<MsgId> _server;
        private readonly Signal<bool> _validating;
        private readonly Action<Action> _post;
        private readonly Timer _timer;
        private CancellationTokenSource? _cts;
        private T _latest = default!;
        private bool _primed;

        public AsyncFieldState(FieldOptions<T> opts, Signal<MsgId> server, Signal<bool> validating, Action<Action> post)
        {
            _async = opts.Async!;
            _debounceMs = Math.Max(0, opts.AsyncDebounceMs);
            _server = server;
            _validating = validating;
            _post = post;
            _timer = new Timer(static s => ((AsyncFieldState<T>)s!).Fire(), this, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>UI thread, inside the reactive flush. Records the latest value and (re)arms the debounce — both
        /// allocation-free. The very first call (the effect's initial run at mount) is skipped so a pristine field is
        /// not validated.</summary>
        public void OnValueChanged(T v)
        {
            _latest = v;
            if (!_primed) { _primed = true; return; }
            _validating.Value = true;                 // equality-gated; cleared when the result (or an error) lands
            _timer.Change(_debounceMs, Timeout.Infinite);
        }

        private void Fire()
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();   // off the UI thread / off the paint window
            T v = _latest;
            _ = RunAsync(v, cts);
        }

        private async Task RunAsync(T v, CancellationTokenSource cts)
        {
            try
            {
                MsgId result = await _async(v, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;
                _post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _server.Value = result;
                    _validating.Value = false;
                });
            }
            catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
            catch { _post(() => _validating.Value = false); }
        }

        public void Dispose() { _cts?.Cancel(); _timer.Dispose(); }
    }
}
