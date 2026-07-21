using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FluentGpu.Hosting;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  Timing hooks — debounce / throttle / timeout / interval over the host's frame-clock HostTimerQueue.
//
//  All four are mount-once: one cell + (for debounce/throttle) one standalone watcher Effect + (for interval) one
//  activation watcher, allocated at mount and reused every render. A source change re-arms via a LAZY heap re-insert
//  guarded by a per-cell generation, so the steady render — and a quiet frame with an armed-but-not-due timer — allocate
//  nothing. Every cell is IDisposableCell + generation-guarded: RunAllCleanups (unmount) bumps the generation so a
//  callback that pops AFTER unmount is a no-op, and disposes any owned watcher/memo.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Imperative control over a pending debounce (returned from <see cref="RenderContext.UseDebouncedValue{T}(IReadSignal{T}, float, out DebounceHandle)"/>).</summary>
public readonly struct DebounceHandle
{
    private readonly IDebounceControl? _ctl;
    internal DebounceHandle(IDebounceControl ctl) => _ctl = ctl;

    /// <summary>Commit the source's current value to the debounced signal NOW and drop the pending fire — the
    /// "search on Enter" flush of a search-as-you-type field. No-op when nothing is pending.</summary>
    public void Flush() => _ctl?.Flush();

    /// <summary>Drop the pending fire without committing — the debounced signal keeps its last committed value.</summary>
    public void Cancel() => _ctl?.Cancel();
}

/// <summary>Imperative control over a one-shot <see cref="RenderContext.UseTimeout"/> — generation-guarded, so a stale
/// fire is a no-op.</summary>
public readonly struct TimerHandle
{
    private readonly ITimerControl? _ctl;
    internal TimerHandle(ITimerControl ctl) => _ctl = ctl;

    /// <summary>Cancel the pending fire (a generation bump — any armed heap entry becomes a no-op). Idempotent.</summary>
    public void Cancel() => _ctl?.Cancel();

    /// <summary>Re-arm from now for the hook's duration (restart the one-shot).</summary>
    public void Restart() => _ctl?.Restart();
}

internal interface IDebounceControl { void Flush(); void Cancel(); }
internal interface ITimerControl { void Cancel(); void Restart(); }

internal sealed class TimeoutCell : HookCell, IDisposableCell, ITimerControl
{
    public HostTimerQueue? Queue;
    public long Gen;
    public float Ms;
    public Action? Callback;      // latest closure (overwritten each render — a fresh lambda needs no re-arm)
    public DepKey Key;
    public bool HasKey;
    public readonly Action<long> Fire;

    public TimeoutCell() => Fire = OnFire;
    private void OnFire(long g) { if (g == Gen) Callback?.Invoke(); }

    public void Arm(float ms) { if (Queue is null) return; Gen++; Queue.Schedule(Queue.NowMs + MathF.Max(ms, 0f), Gen, Fire); }
    public void Cancel() => Gen++;
    public void Restart() { Cancel(); Arm(Ms); }
    public void DisposeCell() => Gen++;   // unmount → a due-after-unmount fire is a no-op
}

internal sealed class DebounceCell<T> : HookCell, IDisposableCell, IDebounceControl
{
    public HostTimerQueue? Queue;
    public long Gen;
    public float Ms;
    public Signal<T> Output = null!;
    public IReadSignal<T> Source = null!;
    public Effect? Watcher;
    public Memo<T>? OwnedSource;   // thunk (Func) form only — disposed on unmount
    public bool Started;
    public readonly Action<long> Fire;

    public DebounceCell() => Fire = OnFire;
    private void OnFire(long g) { if (g == Gen) Output.Value = Source.Peek(); }   // trailing-edge commit

    public void Arm() { if (Queue is null) return; Gen++; Queue.Schedule(Queue.NowMs + MathF.Max(Ms, 0f), Gen, Fire); }
    public void Flush() { Gen++; Output.Value = Source.Peek(); }   // commit now + cancel the pending fire
    public void Cancel() => Gen++;
    public void DisposeCell() { Gen++; Watcher?.Dispose(); OwnedSource?.Dispose(); }
}

internal sealed class ThrottleCell<T> : HookCell, IDisposableCell
{
    public HostTimerQueue? Queue;
    public long Gen;
    public float Ms;
    public Signal<T> Output = null!;
    public IReadSignal<T> Source = null!;
    public Effect? Watcher;
    public Memo<T>? OwnedSource;
    public bool Started, Cooling;
    public T Latest = default!;
    public readonly Action<long> Fire;

    public ThrottleCell() => Fire = OnFire;

    // A source change: emit immediately on the leading edge, then open a cooldown window that samples the LAST value.
    public void OnChange()
    {
        Latest = Source.Peek();
        if (!Cooling) { Cooling = true; Output.Value = Latest; Arm(); }   // leading edge
        // else: suppressed during the window — remembered in Latest for the trailing sample
    }

    private void OnFire(long g)
    {
        if (g != Gen) return;
        Cooling = false;
        if (!EqualityComparer<T>.Default.Equals(Output.Peek(), Latest)) Output.Value = Latest;   // trailing sample
    }

    public void Arm() { if (Queue is null) return; Gen++; Queue.Schedule(Queue.NowMs + MathF.Max(Ms, 0f), Gen, Fire); }
    public void DisposeCell() { Gen++; Watcher?.Dispose(); OwnedSource?.Dispose(); }
}

internal sealed class IntervalCell : HookCell, IDisposableCell
{
    public HostTimerQueue? Queue;
    public long Gen;
    public float Ms;
    public Action? Tick;
    public bool Enabled = true, Active = true, Armed;
    public Effect? ActiveWatcher;   // subscribes UseIsActive → pauses while parked/minimized
    public readonly Action<long> Fire;

    public IntervalCell() => Fire = OnFire;
    private bool Running => Enabled && Active && Queue is not null;

    private void OnFire(long g)
    {
        if (g != Gen) return;
        Armed = false;
        if (!Running) return;
        Tick?.Invoke();
        ReArm();   // repeat
    }

    private void ReArm() { Gen++; Armed = true; Queue!.Schedule(Queue.NowMs + MathF.Max(Ms, 1f), Gen, Fire); }

    /// <summary>Arm when it should be running and isn't; pause (invalidate the pending entry) when it shouldn't.</summary>
    public void Reconcile()
    {
        if (Running) { if (!Armed) ReArm(); }
        else if (Armed) { Gen++; Armed = false; }
    }

    public void SetEnabled(bool e) { if (Enabled == e) return; Enabled = e; Reconcile(); }
    public void SetActive(bool a) { if (Active == a) return; Active = a; Reconcile(); }
    public void DisposeCell() { Gen++; ActiveWatcher?.Dispose(); }
}

public sealed partial class RenderContext
{
    private HostTimerQueue? ResolveTimers()
        => ResolveContextSignal?.Invoke(AnchorNode, HostTimers.Current)?.Peek() as HostTimerQueue;

    // ── UseDebouncedValue ────────────────────────────────────────────────────────────────────────────────────────────
    // Trailing-edge: the returned signal follows the source after `ms` of quiet. Each source change re-arms; the last
    // value wins. Flush() commits immediately ("search on Enter"); Cancel() drops the pending fire.

    /// <summary>A read signal that follows <paramref name="source"/> after <paramref name="ms"/> of quiet (trailing-edge
    /// debounce). Zero re-render — driven by a standalone watcher effect + the host timer queue.</summary>
    public IReadSignal<T> UseDebouncedValue<T>(IReadSignal<T> source, float ms, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => UseDebouncedValue(source, ms, out _, __hf, __hl);

    /// <inheritdoc cref="UseDebouncedValue{T}(IReadSignal{T}, float)"/>
    /// <param name="handle">Imperative <see cref="DebounceHandle.Flush"/>/<see cref="DebounceHandle.Cancel"/> control.</param>
    public IReadSignal<T> UseDebouncedValue<T>(IReadSignal<T> source, float ms, out DebounceHandle handle, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        DebounceCell<T> cell;
        if (idx < 0)
        {
            cell = new DebounceCell<T> { Queue = ResolveTimers(), Source = source, Ms = ms, Output = new Signal<T>(source.Peek()) };
            RegisterCell(__k, cell, cleanupCapable: true);
            cell.Watcher = new Effect(Rt, () =>
            {
                _ = cell.Source.Value;   // subscribe to source
                if (!cell.Started) { cell.Started = true; return; }   // no arm at mount (output already seeded)
                cell.Arm();
            });
        }
        else cell = (DebounceCell<T>)_cells[idx];
        cell.Ms = ms;
        handle = new DebounceHandle(cell);
        return cell.Output;
    }

    /// <summary>Thunk form of <see cref="UseDebouncedValue{T}(IReadSignal{T}, float)"/>: <paramref name="source"/> is a
    /// getter over the signals to watch (wrapped in a memo that auto-tracks them).</summary>
    public IReadSignal<T> UseDebouncedValue<T>(Func<T> source, float ms, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => UseDebouncedValue(source, ms, out _, __hf, __hl);

    /// <inheritdoc cref="UseDebouncedValue{T}(Func{T}, float)"/>
    public IReadSignal<T> UseDebouncedValue<T>(Func<T> source, float ms, out DebounceHandle handle, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        DebounceCell<T> cell;
        if (idx < 0)
        {
            var memo = new Memo<T>(Rt, source);
            cell = new DebounceCell<T> { Queue = ResolveTimers(), Source = memo, OwnedSource = memo, Ms = ms, Output = new Signal<T>(memo.Peek()) };
            RegisterCell(__k, cell, cleanupCapable: true);
            cell.Watcher = new Effect(Rt, () =>
            {
                _ = cell.Source.Value;
                if (!cell.Started) { cell.Started = true; return; }
                cell.Arm();
            });
        }
        else cell = (DebounceCell<T>)_cells[idx];
        cell.Ms = ms;
        handle = new DebounceHandle(cell);
        return cell.Output;
    }

    // ── UseThrottledValue ────────────────────────────────────────────────────────────────────────────────────────────
    // Leading edge (emit the first change immediately) + a trailing sample of the last value at the end of the window.

    /// <summary>A read signal that follows <paramref name="source"/> at most once per <paramref name="ms"/>: the first
    /// change emits immediately (leading edge); further changes within the window are coalesced and the last value is
    /// emitted when the window closes (trailing sample). Zero re-render.</summary>
    public IReadSignal<T> UseThrottledValue<T>(IReadSignal<T> source, float ms, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        ThrottleCell<T> cell;
        if (idx < 0)
        {
            cell = new ThrottleCell<T> { Queue = ResolveTimers(), Source = source, Ms = ms, Output = new Signal<T>(source.Peek()), Latest = source.Peek() };
            RegisterCell(__k, cell, cleanupCapable: true);
            cell.Watcher = new Effect(Rt, () =>
            {
                _ = cell.Source.Value;
                if (!cell.Started) { cell.Started = true; return; }
                cell.OnChange();
            });
        }
        else cell = (ThrottleCell<T>)_cells[idx];
        cell.Ms = ms;
        return cell.Output;
    }

    /// <summary>Thunk form of <see cref="UseThrottledValue{T}(IReadSignal{T}, float)"/>.</summary>
    public IReadSignal<T> UseThrottledValue<T>(Func<T> source, float ms, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        ThrottleCell<T> cell;
        if (idx < 0)
        {
            var memo = new Memo<T>(Rt, source);
            cell = new ThrottleCell<T> { Queue = ResolveTimers(), Source = memo, OwnedSource = memo, Ms = ms, Output = new Signal<T>(memo.Peek()), Latest = memo.Peek() };
            RegisterCell(__k, cell, cleanupCapable: true);
            cell.Watcher = new Effect(Rt, () =>
            {
                _ = cell.Source.Value;
                if (!cell.Started) { cell.Started = true; return; }
                cell.OnChange();
            });
        }
        else cell = (ThrottleCell<T>)_cells[idx];
        cell.Ms = ms;
        return cell.Output;
    }

    // ── UseTimeout ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fire <paramref name="callback"/> once <paramref name="ms"/> from now, restarting whenever
    /// <paramref name="deps"/> change (<see cref="DepKey.Empty"/>/default = fire once, from mount). The returned
    /// <see cref="TimerHandle"/> can <see cref="TimerHandle.Cancel"/>/<see cref="TimerHandle.Restart"/> it. A due fire
    /// after the component unmounts is a no-op (generation-guarded).</summary>
    public TimerHandle UseTimeout(Action callback, float ms, DepKey deps = default, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        TimeoutCell cell;
        if (idx < 0)
        {
            cell = new TimeoutCell { Queue = ResolveTimers(), Callback = callback, Ms = ms, Key = deps, HasKey = true };
            RegisterCell(__k, cell, cleanupCapable: true);
            cell.Arm(ms);
        }
        else
        {
            cell = (TimeoutCell)_cells[idx];
            cell.Callback = callback;   // route to the latest closure
            cell.Ms = ms;
            if (cell.Key != deps) { cell.Key = deps; cell.Arm(ms); }   // deps change → restart
        }
        return new TimerHandle(cell);
    }

    // ── UseInterval ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fire <paramref name="tick"/> every <paramref name="ms"/> while <paramref name="enabled"/> AND the
    /// component is active — it auto-PAUSES while the component is parked by <c>Flow.KeepAlive</c> or the window is
    /// minimized/suspended (folds <see cref="UseIsActive"/>), and resumes cleanly on return. Zero re-render; the latest
    /// <paramref name="tick"/> closure is routed each render.</summary>
    public void UseInterval(Action tick, float ms, bool enabled = true, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        var active = UseIsActive();   // keyed to its own internal call site — pauses while parked/minimized
        int idx = LookupCell(__hf, __hl, out var __k);
        IntervalCell cell;
        if (idx < 0)
        {
            cell = new IntervalCell { Queue = ResolveTimers(), Enabled = enabled, Ms = ms, Tick = tick, Active = active.Peek() };
            RegisterCell(__k, cell, cleanupCapable: true);
            cell.ActiveWatcher = new Effect(Rt, () => cell.SetActive(active.Value));   // subscribe → pause/resume on activation change
            cell.Reconcile();   // initial arm if running
        }
        else
        {
            cell = (IntervalCell)_cells[idx];
            cell.Tick = tick;
            cell.Ms = ms;
            cell.SetEnabled(enabled);
        }
    }
}
