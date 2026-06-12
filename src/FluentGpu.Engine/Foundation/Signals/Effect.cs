namespace FluentGpu.Signals;

/// <summary>
/// A reactive side-effect: runs <paramref name="body"/> once now, then re-runs whenever a signal it READ changes
/// (deferred to the next <see cref="ReactiveRuntime.Flush"/>). A property binding (signal → scene node) is an Effect;
/// a component render is a specialized effect. Dispose to stop it (also runs registered cleanups).
/// </summary>
public sealed class Effect : Computation
{
    private readonly Action _body;

    public Effect(ReactiveRuntime runtime, Action body, Computation? owner = null, bool runNow = true)
        : base(runtime, owner)
    {
        _body = body;
        if (runNow) RunStale();
    }

    private protected override void OnStale() => Runtime.Schedule(this);

    internal override void RunStale() => RunComputation(_body);
}

/// <summary>
/// A subclassable reactive computation for callers that own the run body (e.g. a component render-effect that re-runs
/// <c>Render()</c> + reconciles its subtree). The host calls <see cref="Schedule"/> behaviour via the runtime; subclasses
/// implement <see cref="Execute"/>.
/// </summary>
public abstract class ManagedEffect : Computation
{
    protected ManagedEffect(ReactiveRuntime runtime, Computation? owner) : base(runtime, owner) { }

    /// <summary>The work this effect performs each (re-)run. Read signals here to subscribe; they re-run it on change.</summary>
    protected abstract void Execute();

    private protected override void OnStale() => Runtime.Schedule(this);

    internal override void RunStale() => RunComputation(Execute);
}
