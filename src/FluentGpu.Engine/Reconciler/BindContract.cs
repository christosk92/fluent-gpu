using System;
using FluentGpu.Foundation;

namespace FluentGpu.Reconciler;

/// <summary>
/// A DEBUG-only correctness tripwire for the bound-vs-static channel contract (WS1 P1; modeled on
/// <see cref="FluentGpu.Hooks.ReuseGuard"/>). A binding is created at mount (<c>TreeReconciler.BindNode</c>), re-wired
/// bound→bound on a re-render (<c>TreeReconciler.RewireBinds</c>), unwired on a bound→static flip (<c>RewireBinds</c>)
/// and wired on a static→bound flip (<c>TreeReconciler.WireGainedBinds</c>) — so a flip is LEGAL and no longer reported
/// (until 2026-10-20 it silently lost and <c>Flip</c> reported it here). What stays unsupported, and is reported:
/// <list type="bullet">
/// <item><see cref="Unwired"/> — after a flip, the node's BindEffect count differs from its element's bound
/// <c>Prop&lt;T&gt;</c> channel count (the generated <c>{T}Diff.BoundChannelCount</c>): a channel type the reconciler
/// does not wire, or one wired twice. Checked only on the flip path, so it costs nothing elsewhere.</item>
/// <item><see cref="MorphVisibleBind"/> — a shared-element (MorphId) node binding <c>Visible</c>.</item>
/// </list>
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

    /// <summary>When set, a violation THROWS <see cref="BindContractException"/> instead of only reporting —
    /// <c>--fg guards-throw</c>, or a gate scoping the strict path. Default report-only so surfacing a
    /// pre-existing violation cannot brick a debug run.</summary>
    public static bool ThrowOnViolation;

    /// <summary>Count of violations detected since the last <see cref="Reset"/> (gate accessor).</summary>
    public static int Violations { get; private set; }

    /// <summary>The most recent violation message (gate accessor).</summary>
    public static string? LastViolation { get; private set; }

    /// <summary>Reset the accumulators (between gate scenarios).</summary>
    public static void Reset() { Violations = 0; LastViolation = null; }

    /// <summary>After a static↔bound flip on a reused node of <paramref name="elementType"/>, the node carries
    /// <paramref name="actual"/> BindEffects for <paramref name="expected"/> bound channels — some channel is not wired
    /// by <c>TreeReconciler.BindNode</c> (or is wired twice), so its bound form would be ignored.</summary>
    public static void Unwired(string elementType, int expected, int actual)
        => Report($"[bindcontract] {elementType}: after a static↔bound flip the node carries {actual} binding(s) for "
                + $"{expected} bound Prop<T> channel(s). A bound channel BindNode does not wire is ignored; wire it in "
                + "TreeReconciler.BindNode (and its static write in WriteColumns).");

    /// <summary>P1 presence (<c>gate.presence.bindcontract-flip</c>): report a node that bound
    /// <see cref="FluentGpu.Dsl.Element.Visible"/> while also carrying a <see cref="FluentGpu.Dsl.Element.MorphId"/> —
    /// a shared-element (Hero) participant must stay mounted to fly; collapsing it out of layout mid-transition breaks
    /// <c>ConnectedAnimation</c> capture (its measured rect/art vanish). Same report/throw discipline as
    /// <see cref="Unwired"/>.</summary>
    public static void MorphVisibleBind(string elementType)
        => Report($"[bindcontract] {elementType} bound Visible while carrying a MorphId — a shared-element "
                + "participant must stay mounted to fly; collapsing it mid-transition breaks ConnectedAnimation "
                + "capture. Keep Visible static on a MorphId node, or drop MorphId.");

    private static void Report(string msg)
    {
        Violations++;
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
        if (ThrowOnViolation) throw new BindContractException(msg);
    }
}

/// <summary>Thrown by <see cref="BindContract"/> in strict mode (<c>--fg guards-throw</c>) on a violation. Never thrown
/// in release (the guard is compiled out).</summary>
public sealed class BindContractException(string message) : InvalidOperationException(message);
