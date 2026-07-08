using FluentGpu.Foundation;

namespace FluentGpu.Hooks;

/// <summary>
/// A DEBUG-only correctness tripwire for the autonomous-component contract — the reconciler twin of
/// <see cref="FluentGpu.Hosting.RenderBudget"/>. A reused <c>ComponentEl</c> never re-runs its factory
/// (<c>TreeReconciler.Update</c> early-returns), so a plain field/ctor-arg set through
/// <c>Embed.Comp(() =&gt; new T { Field = value })</c> is FROZEN at first mount — a parent that later passes a NEW
/// value silently keeps the stale one. This surfaces exactly that: when the reconciler REUSES an instance, it hands
/// the live component the would-be replacement (built by the discarded factory) via
/// <see cref="Component.DebugCheckReuse"/>; a control that carries caller data in scalar fields overrides that to
/// compare the frozen field and call <see cref="Violation"/> when it changed.
/// <para>
/// Cost discipline (matches <c>Diag</c> / <c>RenderBudget</c> / validation.md §0): the whole facility is gated by the
/// const <see cref="CompiledIn"/> (<c>false</c> unless <c>DEBUG</c> or <c>FLUENTGPU_DIAG</c>), so the reconciler's
/// <c>if (ReuseGuard.CompiledIn &amp;&amp; ReuseGuard.Enabled) { … }</c> guard is dead-code-eliminated in the shipping
/// AOT binary — zero bytes, zero probe allocation. When compiled in it is further gated at runtime by
/// <c>FG_REUSE_GUARD=1</c> (opt-in, like <c>FG_RENDER_DIAG</c>); a gate flips <see cref="Enabled"/> on directly.
/// "Production safety == CI coverage": the value is catching the regression in dev/CI, not in the customer's hands.
/// </para>
/// See <c>design/subsystems/component-props-contract.md</c> for the authoring contract this enforces.
/// </summary>
public static class ReuseGuard
{
    /// <summary>Compile-time master switch — <c>false</c> in release so <c>if (ReuseGuard.CompiledIn) { … }</c> guards
    /// fold away entirely (the const folds, exactly like <c>RenderBudget.CompiledIn</c> / <c>Diag.CompiledIn</c>).</summary>
    public const bool CompiledIn =
#if DEBUG || FLUENTGPU_DIAG
        true;
#else
        false;
#endif

    /// <summary>Runtime gate (only consulted when <see cref="CompiledIn"/>): <c>FG_REUSE_GUARD=1</c> turns the tripwire
    /// on. Off by default so normal debug runs and the slice stay quiet; the dedicated VerticalSlice gate sets it.</summary>
    public static bool Enabled = CompiledIn && Diag.EnvFlag("FG_REUSE_GUARD");

    /// <summary>When set, a detected violation THROWS <see cref="FrozenPropException"/> instead of only reporting —
    /// <c>FG_REUSE_GUARD_THROW=1</c>, or a gate scoping the strict path. Default report-only so surfacing a
    /// pre-existing violation cannot brick a debug run mid-migration.</summary>
    public static bool ThrowOnViolation = CompiledIn && Diag.EnvFlag("FG_REUSE_GUARD_THROW");

    /// <summary>Count of violations since the last <see cref="Reset"/> (gate accessor).</summary>
    public static int Violations { get; private set; }

    /// <summary>The most recent violation message (gate accessor).</summary>
    public static string? LastViolation { get; private set; }

    /// <summary>Reset the accumulators (between gate scenarios).</summary>
    public static void Reset() { Violations = 0; LastViolation = null; }

    /// <summary>Report a frozen-field violation: a control's <see cref="Component.DebugCheckReuse"/> override detected
    /// that <paramref name="field"/> (carrying caller data) changed on a reused instance. <paramref name="guidance"/>
    /// names the fix idiom. Reports to <see cref="Diag.Sink"/>/stderr and throws when <see cref="ThrowOnViolation"/>.</summary>
    public static void Violation(Component owner, string field, string guidance)
    {
        Violations++;
        string msg = $"[reuseguard] {owner.GetType().Name}.{field} changed on a REUSED component (fields freeze at mount). "
                   + guidance + " — see design/subsystems/component-props-contract.md";
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
        if (ThrowOnViolation) throw new FrozenPropException(msg);
    }

    /// <summary>Shorthand for the common scalar-field case (a label / glyph / flag that froze at mount and changed on
    /// reuse). Names the standard fix idioms.</summary>
    public static void ScalarChanged(Component owner, string field) =>
        Violation(owner, field, "route this control's caller data through a props provider (the SelectorBar idiom) or remount it with a changed Key");
}

/// <summary>Thrown by <see cref="ReuseGuard"/> in strict mode (<c>FG_REUSE_GUARD_THROW</c>) when a reused component's
/// frozen field carried changed caller data. Never thrown in release (the guard is compiled out).</summary>
public sealed class FrozenPropException(string message) : System.InvalidOperationException(message);
