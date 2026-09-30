using System.Collections.Generic;
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
/// AOT binary — zero bytes, zero probe allocation. When compiled in it is simply ON: there is no env flag, because a
/// tripwire you have to remember to enable only ever fires for someone who already suspected the bug.
/// "Production safety == CI coverage": the value is catching the regression in dev/CI, not in the customer's hands.
/// </para>
/// <para>
/// ONE report is the exception to all of the above: <see cref="KeyIgnoredInSingleChildSlot"/> is UNCONDITIONAL — no
/// <see cref="CompiledIn"/> gate, no <see cref="Enabled"/> gate. Every OTHER report here names a bug the author can
/// only hit by writing new code wrong (a frozen field, a re-pushed delegate), so catching it in dev/CI is enough. This
/// one names a STRUCTURAL limit of the engine (a key that can never be honored in a single-child slot) that a caller
/// can trip with no code change at all — a click handler starts minting a different key — so "production safety ==
/// CI coverage" does not hold for it: the exact build that needs to see it is the Release build a user runs. See its
/// own doc for the dedupe that keeps that unconditional cost bounded.
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

    /// <summary>ON whenever the facility is compiled in — i.e. every DEBUG/diag build, with no env flag to remember.
    ///
    /// It used to be opt-in behind <c>FG_REUSE_GUARD=1</c>, which meant the tripwire only fired for whoever already
    /// suspected a frozen-props bug — the exact person who least needed telling. A guard nobody switches on catches
    /// nothing. It stays report-only (see <see cref="ThrowOnViolation"/>) and it is still const-folded out of the
    /// shipping AOT binary by <see cref="CompiledIn"/>, so being unconditional here costs release builds nothing.
    ///
    /// Still writable, because the VerticalSlice suite toggles it around its own negative cases.</summary>
    public static bool Enabled = CompiledIn;

    /// <summary>When set, a detected violation THROWS <see cref="FrozenPropException"/> instead of only reporting —
    /// <c>--fg guards-throw</c>, or a gate scoping the strict path. Default report-only so surfacing a
    /// pre-existing violation cannot brick a debug run mid-migration.</summary>
    public static bool ThrowOnViolation;

    /// <summary>Count of violations since the last <see cref="Reset"/> (gate accessor).</summary>
    public static int Violations { get; private set; }

    /// <summary>The most recent violation message (gate accessor).</summary>
    public static string? LastViolation { get; private set; }

    /// <summary>Reset the accumulators (between gate scenarios) — including <see cref="_reportedSites"/>, so a fresh
    /// scenario doesn't inherit another scenario's <see cref="KeyIgnoredInSingleChildSlot"/> dedupe state.</summary>
    public static void Reset() { Violations = 0; LastViolation = null; _reportedSites.Clear(); }

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

    /// <summary>Report a <c>Key</c> the reconciler structurally CANNOT honour: a keyed element sitting in a SINGLE-child
    /// slot (a component's root output, a provider body, a <c>Show</c> body). <c>TreeReconciler.ReconcileSingleChild</c>
    /// pairs old↔new by <c>ElementTypeId</c> alone — only <c>ReconcileChildren</c> reads <c>Key</c> — so a changed key
    /// there is a silently-dropped remount request and every field frozen at that child's mount stays frozen. The fix is
    /// to make it a keyed CHILD: wrap it in a container element (<c>new BoxEl { Children = [keyed] }</c>).
    /// <para>UNCONDITIONAL — unlike every other report on this type, this one is gated by neither <see cref="CompiledIn"/>
    /// nor <see cref="Enabled"/>: a Release build (where both are compiled-out/false) is exactly the build with no other
    /// way to see a remount request silently dropped. De-duplicated via <see cref="ShouldReportSite"/> (element type + a
    /// stable prefix of the NEW key, capped at <see cref="MaxReportedSites"/> distinct sites) so a key that legitimately
    /// varies every frame (a measured width or a generation counter baked into a root key — the audited sites the
    /// contract comment already calls out) logs ONCE per site, not once per frame.</para>
    /// <para>Report-only by design — it does NOT honour <see cref="ThrowOnViolation"/>. The semantic is unchanged (the
    /// subtree is still updated in place), so this is a diagnostic about a request that was dropped, not a corrupted
    /// state, and it must not brick a run for a pre-existing inert key.</para></summary>
    public static void KeyIgnoredInSingleChildSlot(string elementType, string? oldKey, string? newKey)
    {
        if (!ShouldReportSite(elementType, newKey)) return;
        Violations++;
        string msg = $"[reuseguard] {elementType}.Key '{oldKey}' → '{newKey}' in a SINGLE-child slot was IGNORED "
                   + "(ReconcileSingleChild pairs by element type; only ReconcileChildren honors Key), so the subtree was "
                   + "UPDATED in place instead of remounted and its frozen fields kept their mount-time values. "
                   + "Put remount keys on a CHILD of this slot — wrap the keyed element in a container "
                   + "(the content root of Skel.Region/Show/a provider/a component IS this slot) "
                   + "— see design/subsystems/component-props-contract.md";
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
    }

    /// <summary>Sites already reported by <see cref="KeyIgnoredInSingleChildSlot"/> this run — a plain dedupe latch, not
    /// bounded by <see cref="CompiledIn"/> because the report it guards isn't either. Cleared by <see cref="Reset"/>.</summary>
    private static readonly HashSet<string> _reportedSites = new(StringComparer.Ordinal);

    /// <summary>Cap on <see cref="_reportedSites"/> so a pathological key story (many genuinely distinct sites in one
    /// run) cannot grow this unbounded; a site past the cap just reports every time, exactly as if the dedupe did not
    /// exist — never worse than before this facility was added.</summary>
    private const int MaxReportedSites = 32;

    /// <summary>Length of the NEW-key prefix the dedupe keys on. A full-key dedupe would never collapse the case this
    /// exists for (a key whose tail moves every frame — a measured width, a generation counter): every frame mints a
    /// "new" full key and the set would grow forever. A short PREFIX collapses those to the site's stable head while
    /// still separating genuinely different call sites that happen to share an element type.</summary>
    private const int KeyPrefixLength = 24;

    /// <summary>True the FIRST time this (elementType, key-prefix) pair is seen — i.e. report — false on every repeat.
    /// A per-SITE latch, not a per-message-content one: see <see cref="KeyPrefixLength"/> for why it truncates.</summary>
    private static bool ShouldReportSite(string elementType, string? newKey)
    {
        string prefix = newKey is null ? "" : newKey.Length <= KeyPrefixLength ? newKey : newKey[..KeyPrefixLength];
        string site = elementType + "|" + prefix;
        if (_reportedSites.Contains(site)) return false;                  // already reported this site — stay quiet
        if (_reportedSites.Count < MaxReportedSites) _reportedSites.Add(site);   // room to latch it: the NEXT occurrence goes quiet
        return true;   // first occurrence — or the cap is full and this site can no longer be latched, so it reports every time
    }

    /// <summary>Report a re-pushed DELEGATE prop whose <c>Method</c> changed on a mounted component. A props record that
    /// gates re-renders on data (<c>ShelfProps</c>, <c>ResponsiveBox.Props</c>) IGNORES its delegate members, because a
    /// lambda allocates a fresh closure — equal <c>Method</c>, new <c>Target</c> — on every parent render, and gating on
    /// one means never gating at all. A different <c>Method</c> is the case that is NOT routine: the caller swapped in a
    /// genuinely different builder/handler, which therefore schedules no render of its own (it takes effect at the next
    /// data change). Fix by making what the delegate renders a function of the data, or by re-keying the component.
    /// <para>Report-only by design — it does NOT honour <see cref="ThrowOnViolation"/>: the newest delegate is still the
    /// one invoked, so nothing is corrupt; this only names a request that scheduled nothing.</para></summary>
    public static void IgnoredDelegateChanged(Component owner, string field)
    {
        Violations++;
        string msg = $"[reuseguard] {owner.GetType().Name}.{field} was re-pushed with a DIFFERENT delegate Method. "
                   + "Delegate props are excluded from the data gate (a fresh closure every render can never compare "
                   + "equal), so this change scheduled no re-render — it takes effect at the next data change. Make what "
                   + "the delegate renders a function of the data, or re-key the component "
                   + "— see design/subsystems/component-props-contract.md";
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
    }

    /// <summary>Shorthand for the common scalar-field case (a label / glyph / flag that froze at mount and changed on
    /// reuse). Names the standard fix idioms.</summary>
    public static void ScalarChanged(Component owner, string field) =>
        Violation(owner, field, "route this control's caller data through a props provider (the SelectorBar idiom) or remount it with a changed Key");
}

/// <summary>Thrown by <see cref="ReuseGuard"/> in strict mode (<c>--fg guards-throw</c>) when a reused component's
/// frozen field carried changed caller data. Never thrown in release (the guard is compiled out).</summary>
public sealed class FrozenPropException(string message) : System.InvalidOperationException(message);
