using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.Signals;

/// <summary>
/// A fire-on-demand async ACTION with a reactive in-progress state — the imperative sibling of <see cref="Loadable{T}"/>
/// (which loads a VALUE once at mount). Use it for a command kicked by an event (Play, Save, Like, a network call):
/// it runs the async op, exposes <see cref="IsRunning"/> so the UI renders a spinner / disables the trigger, GUARDS
/// against re-firing while in flight, and supports CANCELLATION. Completion is marshalled back to the UI thread via the
/// <c>post</c> delegate (so the state signal write lands in the host's batched flush, never off-thread); a completion
/// that lands AFTER the owning component unmounts writes a signal with no subscribers — a harmless no-op. Modeled on
/// <c>RenderContext.UseResource</c> (per-run <see cref="CancellationTokenSource"/> + post + cancelled-token guard).
///
/// Obtain one with <c>UseAsyncCommand()</c> (it wires <c>UsePost()</c> + unmount disposal). It does NOT cancel on
/// unmount by default — a started command (e.g. play) should run to completion; opt into <c>cancelOnUnmount: true</c>
/// for supersede/abort cases.
/// </summary>
public sealed class AsyncCommand
{
    private readonly Signal<bool> _running = new(false);
    private readonly Action<Action> _post;
    private CancellationTokenSource? _cts;

    public AsyncCommand(Action<Action> post) => _post = post;

    /// <summary>True while a run is in flight (reactive — subscribes the reader). Drive a spinner / IsEnabled off it.</summary>
    public bool IsRunning => _running.Value;
    /// <summary>Non-subscribing peek — for a guard inside an event handler.</summary>
    public bool IsRunningNow => _running.Peek();

    /// <summary>Start <paramref name="op"/>. NO-OP if a run is already in flight (the re-entry guard / "block the
    /// command"). <paramref name="op"/> receives a token cancelled by <see cref="Cancel"/> / a superseding
    /// <see cref="Restart"/>.</summary>
    public void Run(Func<CancellationToken, Task> op, Action<Exception>? onError = null)
    {
        if (_running.Peek()) return;
        Start(op, onError);
    }

    /// <summary>Cancel any in-flight run and start <paramref name="op"/> (the supersede case — e.g. search-as-you-type,
    /// where only the latest run matters).</summary>
    public void Restart(Func<CancellationToken, Task> op, Action<Exception>? onError = null)
    {
        _cts?.Cancel();
        Start(op, onError);
    }

    private void Start(Func<CancellationToken, Task> op, Action<Exception>? onError)
    {
        var cts = _cts = new CancellationTokenSource();
        _running.Value = true;
        _ = Core(op, onError, cts);
    }

    private async Task Core(Func<CancellationToken, Task> op, Action<Exception>? onError, CancellationTokenSource cts)
    {
        try { await op(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* cancelled / superseded */ }
        catch (Exception ex) { if (onError is not null && !cts.IsCancellationRequested) _post(() => onError(ex)); }
        finally
        {
            // Clear ONLY if this run is still the current one (a Restart already swapped in a newer CTS, which keeps
            // IsRunning true and owns the clear). Race-immune like the validation "_server" merge.
            _post(() =>
            {
                if (ReferenceEquals(_cts, cts)) { _running.Value = false; _cts = null; }
                cts.Dispose();
            });
        }
    }

    /// <summary>Cancel the in-flight run, if any. The op must honour the token; <see cref="IsRunning"/> clears on its
    /// completion (the finally posts the clear).</summary>
    public void Cancel() => _cts?.Cancel();
}

/// <summary>
/// A KEYED set of concurrent <see cref="AsyncCommand"/>-style runs (one in flight per key) — for per-item commands like
/// a track-row play/like where the spinner must show on the specific row whose op is running. <see cref="IsRunning"/>
/// is reactive: a shared version signal bumps on every start/finish so readers re-evaluate. Same threading/lifetime
/// contract as <see cref="AsyncCommand"/> (UI-thread mutation, post-marshalled completion, no cancel-on-unmount by
/// default). Obtain one with <c>UseAsyncCommands&lt;TKey&gt;()</c>.
///
/// Scale note: readers subscribe to the shared version, so they re-evaluate when ANY key changes — fine for a few
/// concurrent keys (one play at a time, a handful of likes); a per-key signal map is the upgrade if a list ever runs
/// many concurrent keyed ops.
/// </summary>
public sealed class AsyncCommandSet<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, CancellationTokenSource> _inFlight = new();
    private readonly Signal<int> _version = new(0);
    private readonly Action<Action> _post;

    public AsyncCommandSet(Action<Action> post) => _post = post;

    /// <summary>True while a run for <paramref name="key"/> is in flight (reactive).</summary>
    public bool IsRunning(TKey key) { _ = _version.Value; return _inFlight.ContainsKey(key); }
    /// <summary>Non-subscribing peek — for a guard inside an event handler.</summary>
    public bool IsRunningNow(TKey key) => _inFlight.ContainsKey(key);
    /// <summary>True while ANY key has a run in flight (reactive) — e.g. to show a list-wide activity cue.</summary>
    public bool AnyRunning() { _ = _version.Value; return _inFlight.Count > 0; }

    /// <summary>Start <paramref name="op"/> for <paramref name="key"/>. NO-OP if a run for that key is already in
    /// flight (per-key re-entry guard).</summary>
    public void Run(TKey key, Func<CancellationToken, Task> op, Action<Exception>? onError = null)
    {
        if (_inFlight.ContainsKey(key)) return;
        var cts = new CancellationTokenSource();
        _inFlight[key] = cts;
        _version.Value++;
        _ = Core(key, op, onError, cts);
    }

    private async Task Core(TKey key, Func<CancellationToken, Task> op, Action<Exception>? onError, CancellationTokenSource cts)
    {
        try { await op(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (onError is not null && !cts.IsCancellationRequested) _post(() => onError(ex)); }
        finally
        {
            _post(() =>
            {
                if (_inFlight.TryGetValue(key, out var c) && ReferenceEquals(c, cts)) { _inFlight.Remove(key); _version.Value++; }
                cts.Dispose();
            });
        }
    }

    /// <summary>Cancel the in-flight run for <paramref name="key"/> (if any).</summary>
    public void Cancel(TKey key) { if (_inFlight.TryGetValue(key, out var cts)) cts.Cancel(); }
    /// <summary>Cancel every in-flight run.</summary>
    public void CancelAll() { foreach (var cts in _inFlight.Values) cts.Cancel(); }
}
