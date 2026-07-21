using System;

namespace FluentGpu.Hooks;

/// <summary>
/// Marks a <c>partial</c> <see cref="Component"/> whose <c>[Prop]</c> partial properties get generated signal-backed
/// live-props storage — the compile-time sugar over the runtime re-pushed-props substrate (<see cref="IPropsHost"/> +
/// the reconciler reuse seam). The <c>PropsGenerator</c> (FluentGpu.SourceGen) emits, into the same partial type:
/// per non-delegate <c>[Prop]</c> a private <c>Signal&lt;T&gt;</c> + the subscribing partial getter + a
/// <c>XxxProp</c> <see cref="FluentGpu.Signals.IReadSignal{T}"/> bind-accessor (for forwarding a live channel into a
/// child bind); per delegate <c>[Prop]</c> a latest-write slot behind a STABLE forwarder (a fresh lambda from the
/// parent does NOT re-render — wired handlers always invoke the newest); a nested <c>PropsData</c> transport record;
/// an <c>Of(...)</c> factory; the <see cref="IPropsHost.ApplyProps"/> sink (per-field equality-gated — only CHANGED
/// fields notify); and <c>CurrentProps()</c>/<c>From(source)</c> snapshot helpers. Zero reflection; delivery is
/// reconcile-phase (outside the hot alloc window). Signals are allocated at MOUNT; the forwarder lazily, once.
///
/// <para>The type MUST be <c>partial</c> and derive <see cref="Component"/> (diagnostics FGSG002/FGSG003 otherwise).
/// See <c>docs/guide/reactivity.md</c> (the <c>[Props]</c> authoring section).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PropsAttribute : Attribute { }

/// <summary>
/// Marks a get-only <c>partial</c> property inside a <c>[Props]</c> <see cref="Component"/> as a re-pushed prop. The
/// <c>PropsGenerator</c> supplies the implementation half: a subscribing getter over a per-field <c>Signal&lt;T&gt;</c>
/// (non-delegate) or a latest-write forwarder (delegate — Action / Action&lt;T1..T4&gt; / Func shapes; a delegate with
/// more than four parameters degrades to a raw latest field, Info FGSG004). The property must be declared
/// <c>public partial T Name { get; }</c> (partial + get-only — FGSG001 otherwise). A collection-typed prop
/// (<c>List&lt;&gt;</c> / array / <c>IReadOnlyList&lt;&gt;</c> …) draws a Warning (FGSG005): the backing signal uses the
/// DEFAULT comparer, so a mutated-in-place collection never notifies and a fresh-but-equal one always churns — prefer
/// an immutable/keyed representation or a version stamp.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PropAttribute : Attribute { }

/// <summary>
/// Marks an <see cref="FluentGpu.Dsl.Element"/> content slot (a factory <c>Element</c>/<c>Element[]</c> parameter, or a
/// component field/property that receives one) as a <b>deliberate mount-time slot</b> — the content is built once by
/// the caller and frozen at first mount BY DESIGN, not by mistake. <c>FGRP001</c> (the frozen-Element-content analyzer)
/// treats an assignment sourced from, or targeting, a member marked with this attribute as intentional and does NOT
/// flag it — the sanctioned replacement for a blanket <c>#pragma warning disable FGRP001</c> around a convenience
/// factory (Expander/SplitButton/DropZone/ToggleSplitButton static-content overloads). It documents intent at the
/// declaration and keeps FGRP001 armed for genuinely accidental frozen content elsewhere. Callers needing per-render
/// content use the component's re-push slots overload (<c>Embed.Comp(props, factory)</c> + <c>UseProps</c>) or a
/// changed <c>Key</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class MountOnceContentAttribute : Attribute { }
