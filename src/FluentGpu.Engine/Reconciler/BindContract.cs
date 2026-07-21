using System;
using FluentGpu.Foundation;

namespace FluentGpu.Reconciler;

/// <summary>
/// A DEBUG-only correctness tripwire for the bound-vs-static channel contract (WS1 P1; modeled on
/// <see cref="FluentGpu.Hooks.ReuseGuard"/>). Bind wiring is MOUNT-ONLY (<c>TreeReconciler.BindNode</c> runs once, at
/// mount), so if a reused node's <c>Prop&lt;T&gt;</c> channel FLIPS between static and bound across a re-render —
/// <c>Fill = staticColor</c> one render, <c>Fill = someSignal</c> the next (or the reverse) — the flip silently loses:
/// a newly-bound channel is never wired, and a newly-static value never takes because the mount-time bind effect keeps
/// writing the column. This surfaces exactly that flip at the reconciler's Update seam (the per-type generated
/// <c>{T}Diff.FirstBoundFlip</c> compares each channel's <c>IsBound</c>). A fresh-thunk same-shape re-render is NOT
/// flagged — bound→bound keeps <c>IsBound</c> true, and a changing thunk identity is the sanctioned re-render pattern.
/// <para>
/// Cost discipline (matches <c>Diag</c> / <see cref="FluentGpu.Hooks.ReuseGuard"/>): the whole facility is gated by the
/// const <see cref="CompiledIn"/> (<c>false</c> unless <c>DEBUG</c>/<c>FLUENTGPU_DIAG</c>), so the reconciler's
/// <c>if (BindContract.CompiledIn &amp;&amp; BindContract.Enabled) { … }</c> guard folds away entirely in the shipping
/// AOT binary. When compiled in it defaults ON (kill-switch <c>FG_BIND_CONTRACT=0</c> disables it) and is report-only
/// unless <c>FG_BIND_CONTRACT_THROW=1</c>. "Production safety == CI coverage": the value is catching the mistake in
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

    /// <summary>Runtime gate (only consulted when <see cref="CompiledIn"/>): defaults ON, kill-switch
    /// <c>FG_BIND_CONTRACT=0</c> disables it.</summary>
    public static bool Enabled = CompiledIn && !Diag.EnvFlagDisabled("FG_BIND_CONTRACT");

    /// <summary>When set, a detected flip THROWS <see cref="BindContractException"/> instead of only reporting —
    /// <c>FG_BIND_CONTRACT_THROW=1</c>, or a gate scoping the strict path. Default report-only so surfacing a
    /// pre-existing flip cannot brick a debug run.</summary>
    public static bool ThrowOnViolation = CompiledIn && Diag.EnvFlag("FG_BIND_CONTRACT_THROW");

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
                   + "(bind wiring is mount-only, so the flip silently loses). Keep the channel's bound/static shape "
                   + "stable across renders (bind a stable Prop<T>/signal), or remount with a changed Key.";
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
        if (ThrowOnViolation) throw new BindContractException(msg);
    }
}

/// <summary>Thrown by <see cref="BindContract"/> in strict mode (<c>FG_BIND_CONTRACT_THROW</c>) when a reused node's
/// bindable channel flipped between static and bound. Never thrown in release (the guard is compiled out).</summary>
public sealed class BindContractException(string message) : InvalidOperationException(message);
