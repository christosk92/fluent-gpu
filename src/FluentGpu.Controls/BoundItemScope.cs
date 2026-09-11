using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Typed bound-item authoring helpers for <see cref="ItemsView.CreateBound{T}"/> (Operation ultra-fast GPU engine,
/// P3 — <c>docs/design/subsystems/virtualization.md</c> §3.4 "Typed bound authoring"). Every extension here allocates
/// EXACTLY ONE closure — and, where noted, one small scratch object (<see cref="SpanBuffer"/>) — at TEMPLATE time
/// (the row template runs once per persistent slot); nothing further allocates on a rebind, a recycle, or a
/// steady-state scroll frame. The pattern behind every helper is the same: capture <c>scope.Item</c> (already the
/// per-slot equality-gated item signal — see <see cref="BoundItemsSource{T}.BindItem(IReadSignal{int}, ReactiveRuntime, int, IEqualityComparer{T}?)"/>)
/// in a thunk that reads <c>Item.Value</c> (subscribing) and projects it through the caller's pure selector.
///
/// <para><b>The one rule that matters:</b> a cell that is present/hidden FREQUENTLY (a chart glyph, a "playing now"
/// affordance, a badge that flips per row) is a <see cref="Show{T}(in BoundItemScope{T}, Func{T, bool})"/> bound to
/// <see cref="Element.Visible"/> — shape-stable, no remount, the P1 presence channel does the layout-flow work. A
/// state that is RARE or expensive to keep mounted (a spinner, a marquee, a retry line, a drawer) is
/// <see cref="ShowWhen{T}(in BoundItemScope{T}, Func{T, bool}, Func{Element})"/> — <c>Flow.Show</c>, which actually
/// mounts/unmounts the branch. Reaching for <c>ShowWhen</c> on a frequent flip defeats shape stability; reaching for
/// <c>Show</c> on a genuinely rare, heavy branch keeps dead weight mounted for nothing.</para>
/// </summary>
public static class BoundItemScopeExtensions
{
    /// <summary>Bound text: <c>sel(Item.Value)</c> on every read.</summary>
    public static Prop<string> Text<T>(this in BoundItemScope<T> scope, Func<T, string> sel)
    {
        var item = scope.Item;
        return Prop.Of(() => sel(item.Value));
    }

    /// <summary>Bound text through a bounded <see cref="FormatCache{TKey}"/>: resolves the item to a small/stable key
    /// via <paramref name="keySel"/>, then formats (or reuses a cached format of) that key. Hoist ONE
    /// <paramref name="cache"/> per call site (a static readonly field, like <see cref="FormatCache.MmSs"/>) — a
    /// per-row cache instance defeats the point.</summary>
    public static Prop<string> Text<T, TKey>(this in BoundItemScope<T> scope, Func<T, TKey> keySel, FormatCache<TKey> cache, Func<TKey, string> formatter)
        where TKey : notnull
    {
        var item = scope.Item;
        return Prop.Of(() => cache.Get(keySel(item.Value), formatter));
    }

    /// <summary>Bound color.</summary>
    public static Prop<ColorF> Color<T>(this in BoundItemScope<T> scope, Func<T, ColorF> sel)
    {
        var item = scope.Item;
        return Prop.Of(() => sel(item.Value));
    }

    /// <summary>Bound opacity.</summary>
    public static Prop<float> Opacity<T>(this in BoundItemScope<T> scope, Func<T, float> sel)
    {
        var item = scope.Item;
        return Prop.Of(() => sel(item.Value));
    }

    /// <summary>Bound presence (→ <see cref="Element.Visible"/>): for a cell that shows/hides FREQUENTLY. Shape-stable
    /// — the node stays mounted, the P1 presence channel removes it from layout/paint while hidden. Prefer
    /// <see cref="ShowWhen{T}(in BoundItemScope{T}, Func{T, bool}, Func{Element})"/> for a rare/expensive branch
    /// instead (a real mount/unmount, not a presence flip).</summary>
    public static Prop<bool> Show<T>(this in BoundItemScope<T> scope, Func<T, bool> sel)
    {
        var item = scope.Item;
        return Prop.Of(() => sel(item.Value));
    }

    /// <summary>Bound image source; a null/empty <paramref name="sel"/> result binds the empty string (the engine's
    /// "no image" sentinel — <see cref="ImageEl.Source"/> already treats an empty source as nothing to request).</summary>
    public static Prop<string> Image<T>(this in BoundItemScope<T> scope, Func<T, string?> sel)
    {
        var item = scope.Item;
        return Prop.Of(() => sel(item.Value) ?? string.Empty);
    }

    /// <summary>Bound spans: <paramref name="fill"/> refills ONE <see cref="SpanBuffer"/> — owned by THIS call site,
    /// allocated once at template time, reused (never reallocated except to grow) on every rebind — with the current
    /// item's spans. <see cref="SceneStore"/> copies out of the buffer on write (see the P2 bound-spans design), so
    /// reusing/overwriting it after a fire is always safe.</summary>
    public static Prop<TextSpans> Spans<T>(this in BoundItemScope<T> scope, Action<T, SpanBuffer> fill)
    {
        var item = scope.Item;
        var buffer = new SpanBuffer();
        return Prop.Of(() =>
        {
            buffer.Clear();
            fill(item.Value, buffer);
            return buffer.Current;
        });
    }

    /// <summary>Bound small-integer text via the dense, never-cleared <see cref="FormatCache.Int"/> cache (row
    /// numbers, track/play counts).</summary>
    public static Prop<string> Number<T>(this in BoundItemScope<T> scope, Func<T, int> sel)
    {
        var item = scope.Item;
        return Prop.Of(() => FormatCache.Int(sel(item.Value)));
    }

    /// <summary>Bound "m:ss" duration text (milliseconds in, cached by whole seconds via <see cref="FormatCache.MmSs"/>).</summary>
    public static Prop<string> Duration<T>(this in BoundItemScope<T> scope, Func<T, long> selMs)
    {
        var item = scope.Item;
        return Prop.Of(() => FormatCache.DurationMmSs(selMs(item.Value)));
    }

    /// <summary>Resolve the item to an arbitrary value (a non-string channel a control needs directly, e.g. a size or
    /// an enum) — the same one-closure shape as <see cref="Text{T}(in BoundItemScope{T}, Func{T, string})"/>, generic
    /// over the result type.</summary>
    public static Prop<TVal> Value<T, TVal>(this in BoundItemScope<T> scope, Func<T, TVal> sel)
    {
        var item = scope.Item;
        return Prop.Of(() => sel(item.Value));
    }

    /// <summary>An equality-gated derived signal for a SUB-COMPONENT that wants its own reactive input (e.g.
    /// <c>Equalizer.Of(item.Signal(t =&gt; t.IsPlaying))</c>) rather than a plain scene channel: a <see cref="Memo{T}"/>
    /// (same construction as the gated <c>BindItem</c> overload) that recomputes on every item change but notifies its
    /// subscriber only when the projected value differs under <paramref name="comparer"/>.</summary>
    public static IReadSignal<TVal> Signal<T, TVal>(this in BoundItemScope<T> scope, Func<T, TVal> sel, IEqualityComparer<TVal>? comparer = null)
    {
        var item = scope.Item;
        var runtime = scope.Row.Runtime ?? throw new InvalidOperationException(
            "BoundItemScope<T>.Signal requires a RowScope built by ItemsView.CreateBound (RowScope.Runtime is unset).");
        return new Memo<TVal>(runtime, () => sel(item.Value), comparer);
    }

    /// <summary>Wraps a per-item handler for a no-argument callback (<c>OnClick</c>, …): resolves <c>Item.Peek()</c> —
    /// NOT <c>.Value</c> — at INVOCATION time, so the handler never subscribes itself as a reactive read and always
    /// acts on whichever item currently occupies this slot (correct even after a recycle moved the slot to a
    /// different logical row between template-build and click).</summary>
    public static Action Invoke<T>(this in BoundItemScope<T> scope, Action<T> handler)
    {
        var item = scope.Item;
        return () => handler(item.Peek());
    }

    /// <summary>Wraps a per-item handler that also receives the raw pointer event args (<c>OnPointerPressed</c>,
    /// <c>OnPointerReleased</c>, …). Same at-invocation resolution as <see cref="Invoke{T}(in BoundItemScope{T}, Action{T})"/>.</summary>
    public static Action<PointerEventArgs> Invoke<T>(this in BoundItemScope<T> scope, Action<T, PointerEventArgs> handler)
    {
        var item = scope.Item;
        return e => handler(item.Peek(), e);
    }

    /// <summary>Wraps a per-item, per-span handler for <see cref="SpanTextEl.OnSpanClick"/>: resolves the CURRENT
    /// item and the clicked span index at invocation time — the index is already resolved against the row's live
    /// spans by the dispatcher (see the P2 bound-spans design), so this never needs position arithmetic.</summary>
    public static Action<int> InvokeSpan<T>(this in BoundItemScope<T> scope, Action<T, int> handler)
    {
        var item = scope.Item;
        return i => handler(item.Peek(), i);
    }

    /// <summary>A RARE/expensive branch: mounts/unmounts <paramref name="build"/>'s element (via <c>Flow.Show</c>)
    /// rather than keeping it always-mounted behind a presence flip. Use for a spinner, a marquee, a retry line, a
    /// drawer — anything heavy or seldom-shown; use <see cref="Show{T}(in BoundItemScope{T}, Func{T, bool})"/> instead
    /// for a cell that flips presence frequently (a chart glyph, a "now playing" cue).</summary>
    public static ShowEl ShowWhen<T>(this in BoundItemScope<T> scope, Func<T, bool> pred, Func<Element> build)
    {
        var item = scope.Item;
        return Flow.Show(() => pred(item.Value), build());
    }
}
