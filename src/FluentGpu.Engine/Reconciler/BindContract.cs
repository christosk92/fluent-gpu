using System;
using FluentGpu.Foundation;

namespace FluentGpu.Reconciler;

/// <summary>
/// A DEBUG-only correctness tripwire for the bound-vs-static channel contract (WS1 P1; modeled on
/// <see cref="FluentGpu.Hooks.ReuseGuard"/>). A binding is CREATED only at mount (<c>TreeReconciler.BindNode</c>) and
/// on a re-render is only ever RE-WIRED bound→bound (<c>TreeReconciler.RewireBinds</c>: a new thunk/signal on a
/// still-bound channel swaps the effect's source and re-runs it). So if a reused node's <c>Prop&lt;T&gt;</c> channel
/// FLIPS between static and bound across a re-render — <c>Fill = staticColor</c> one render, <c>Fill = someSignal</c>
/// the next (or the reverse) — the flip silently loses: a newly-bound channel is never wired, and a newly-static value
/// never takes because the mount-time bind effect keeps writing the column. This surfaces exactly that flip at the
/// reconciler's Update seam (the per-type generated <c>{T}Diff.FirstBoundFlip</c> compares each channel's
/// <c>IsBound</c>). A fresh-thunk same-shape re-render is NOT flagged — bound→bound keeps <c>IsBound</c> true, and a
/// changing thunk identity is the sanctioned re-render pattern: the reconciler re-wires it (<c>gate.bind.rewire-*</c>).
/// <para>
/// Cost discipline (matches <c>Diag</c> / <see cref="FluentGpu.Hooks.ReuseGuard"/>): the whole facility is gated by the
/// const <see cref="CompiledIn"/> (<c>false</c> unless <c>DEBUG</c>/<c>FLUENTGPU_DIAG</c>), so the reconciler's
/// <c>if (BindContract.CompiledIn &amp;&amp; BindContract.Enabled) { … }</c> guard folds away entirely in the shipping
/// AOT binary. When compiled in it defaults ON (kill-switch <c>--fg no-guards</c> disables it) and is report-only
/// unless <c>--fg guards-throw</c>. "Production safety == CI coverage": the value is catching the mistake in
/// dev/CI, not in the customer's hands.
/// </para>
/// </summary>
public static class BindContract
{
    /// <summary>Compile-time master switch — <c>false</c> in release so the reconciler's guard folds away.</summary>
    public const bool CompiledIn =
#if DEBUG || FLUENTGPU_DIAG
        true;
#else
        false;
#endif

    /// <summary>Runtime gate (only consulted when <see cref="CompiledIn"/>): defaults ON; <c>--fg no-guards</c> or code
    /// turns it off.</summary>
    public static bool Enabled = CompiledIn;

    /// <summary>When set, a detected flip THROWS <see cref="BindContractException"/> instead of only reporting —
    /// <c>--fg guards-throw</c>, or a gate scoping the strict path. Default report-only so surfacing a
    /// pre-existing flip cannot brick a debug run.</summary>
    public static bool ThrowOnViolation;

    /// <summary>Count of flips detected since the last <see cref="Reset"/> (gate accessor).</summary>
    public static int Violations { get; private set; }

    /// <summary>The most recent violation message (gate accessor).</summary>
    public static string? LastViolation { get; private set; }

    /// <summary>Reset the accumulators (between gate scenarios).</summary>
    public static void Reset() { Violations = 0; LastViolation = null; }

    /// <summary>Report a bound↔static flip on a reused node: <paramref name="channel"/> of <paramref name="elementType"/>
    /// changed its bound/static shape across a re-render. Reports to <see cref="Diag.Sink"/>/stderr; throws when
    /// <see cref="ThrowOnViolation"/>.</summary>
    public static void Flip(string elementType, string channel)
    {
        Violations++;
        string msg = $"[bindcontract] {elementType}.{channel} flipped between static and bound on a REUSED node "
                   + "(a bind is created only at mount and re-wired only bound→bound, so the flip silently loses). Keep the channel's bound/static shape "
                   + "stable across renders (bind a stable Prop<T>/signal), or remount with a changed Key.";
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
        if (ThrowOnViolation) throw new BindContractException(msg);
    }

    /// <summary>P1 presence (<c>gate.presence.bindcontract-flip</c>): report a node that bound
    /// <see cref="FluentGpu.Dsl.Element.Visible"/> while also carrying a <see cref="FluentGpu.Dsl.Element.MorphId"/> —
    /// a shared-element (Hero) participant must stay mounted to fly; collapsing it out of layout mid-transition breaks
    /// <c>ConnectedAnimation</c> capture (its measured rect/art vanish). Same report/throw discipline as
    /// <see cref="Flip"/>.</summary>
    public static void MorphVisibleBind(string elementType)
    {
        Violations++;
        string msg = $"[bindcontract] {elementType} bound Visible while carrying a MorphId — a shared-element "
                   + "participant must stay mounted to fly; collapsing it mid-transition breaks ConnectedAnimation "
                   + "capture. Keep Visible static on a MorphId node, or drop MorphId.";
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
        if (ThrowOnViolation) throw new BindContractException(msg);
    }
}

/// <summary>Thrown by <see cref="BindContract"/> in strict mode (<c>--fg guards-throw</c>) when a reused node's
/// bindable channel flipped between static and bound. Never thrown in release (the guard is compiled out).</summary>
public sealed class BindContractException(string message) : InvalidOperationException(message);
