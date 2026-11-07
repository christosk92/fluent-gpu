using System.Runtime.CompilerServices;

namespace FluentGpu.Dsl;

/// <summary>Freezes the elements an object holds, in place (PERF r3 MODEL M4b, DESIGN 2.2 "escapes"). An element built during a
/// render lives on that render's tape and is gone when the render's reconcile returns; the objects that carry elements past that
/// point (a component's props, a component instance's fields, a context value) are frozen by the reconciler where it hands them
/// on, so what they hold is a heap copy valid forever, as a class element was. A type that holds elements registers its freezer
/// once (the source generator emits one per such type, from a module initializer); every other type is a no-op.</summary>
public static class ElementFreezer
{
    private static readonly Dictionary<Type, Action<object>> s_byType = new();
    private static readonly object s_gate = new();

    /// <summary>Register the in-place freezer of <paramref name="type"/> (generated code; the last registration wins).</summary>
    public static void Register(Type type, Action<object> freeze)
    {
        lock (s_gate) s_byType[type] = freeze;
    }

    [ThreadStatic] private static HashSet<object>? t_visiting;
    [ThreadStatic] private static int t_depth;

    /// <summary>Freeze every element <paramref name="o"/> holds (no-op for null, frozen, and element-free objects). An object
    /// graph with cycles (a component holding its owner) is walked once per pass.</summary>
    public static void FreezeObject(object? o)
    {
        if (o is null || !Tape.RegionsEnabled || o is string || o is Delegate) return;
        Action<object>? f = null;
        if (o is not IElementHolder) { lock (s_gate) s_byType.TryGetValue(o.GetType(), out f); if (f is null) return; }
        var visiting = t_visiting ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!visiting.Add(o)) return;
        t_depth++;
        try
        {
            if (o is IElementHolder h) h.FreezeElements();
            else f!(o);
        }
        finally
        {
            if (--t_depth == 0) visiting.Clear();
        }
    }

    /// <summary>The in-place freezer of a struct that holds elements (generated, one per nameable holder struct).</summary>
    public delegate void RefFreezer<T>(ref T value) where T : struct;

    private static class StructFreezer<T> where T : struct { public static RefFreezer<T>? F; }

    /// <summary>Register the freezer of the holder struct <typeparamref name="T"/> (generated code).</summary>
    public static void RegisterStruct<T>(RefFreezer<T> freeze) where T : struct => StructFreezer<T>.F = freeze;

    /// <summary>A frozen copy of a struct that holds elements (a template's record struct of parts), for a lambda that runs after this
    /// render to capture (FGEL001): <c>var frozen = ElementFreezer.Freeze(parts);</c>. The struct's freezer is generated; one the
    /// generator cannot name (a private nested struct) is not registered, and freezing it fails loudly instead of keeping tape
    /// elements: make the struct internal.</summary>
    public static T Freeze<T>(T value) where T : struct
    {
        if (!Tape.RegionsEnabled) return value;
        var f = StructFreezer<T>.F ?? throw new InvalidOperationException(
            $"ElementFreezer.Freeze<{typeof(T).Name}>: no generated freezer (a struct that holds elements must be internal or public).");
        f(ref value);
        return value;
    }

    /// <summary>Every item of a list of objects that may hold elements.</summary>
    public static void FreezeEach(System.Collections.IEnumerable? items)
    {
        if (items is null || !Tape.RegionsEnabled) return;
        foreach (var o in items) FreezeObject(o);
    }

    // ── helpers the generated freezers call ─────────────────────────────────────────────────────────────────────────────────
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Freeze(ref Element e) => e = e.Freeze();
    public static void Freeze(ref Element? e) { if (e is { } x) e = x.Freeze(); }
    public static void Freeze(ref ElementList l) => l = l.Freeze();
    public static void Freeze(Element[]? a) { if (a is null) return; for (int i = 0; i < a.Length; i++) a[i] = a[i].Freeze(); }
    public static void Freeze(Element?[]? a) { if (a is null) return; for (int i = 0; i < a.Length; i++) if (a[i] is { } x) a[i] = x.Freeze(); }
    internal static void FreezeAlways(Element[] a) { for (int i = 0; i < a.Length; i++) a[i] = a[i].Freeze(); }
    public static void Freeze(List<Element>? l) { if (l is null) return; for (int i = 0; i < l.Count; i++) l[i] = l[i].Freeze(); }
    public static void Freeze(List<Element?>? l) { if (l is null) return; for (int i = 0; i < l.Count; i++) if (l[i] is { } x) l[i] = x.Freeze(); }

    /// <summary>An element list typed as an interface, for a member the freezer holds by ref: an array or a List is frozen in place and
    /// returned; any other list (a collection expression's synthesized read-only list, a boxed <see cref="ElementList"/>, a lazy
    /// sequence) is copied into a frozen array, which the caller stores back (an array implements every list interface).</summary>
    public static IEnumerable<Element>? FreezeList(IEnumerable<Element>? l)
    {
        switch (l)
        {
            case null: return null;
            case Element[] a: Freeze(a); return a;
            case List<Element> list: Freeze(list); return list;
            default:
                if (!Tape.RegionsEnabled) return l;
                var copy = new List<Element>();
                foreach (var e in l) copy.Add(e.Freeze());
                return copy.ToArray();
        }
    }

    /// <summary>An element list typed as an interface: an array or a List is frozen in place; an ElementList (boxed) cannot be
    /// written back, so its owner must hold it as <see cref="ElementList"/> (the generator reports such a member).</summary>
    public static void Freeze(IReadOnlyList<Element>? l)
    {
        switch (l)
        {
            case Element[] a: Freeze(a); break;
            case List<Element> list: Freeze(list); break;
        }
    }

    /// <summary>A value of an element kind held by value (a struct member of type BoxEl, TextEl, …).</summary>
    public static void FreezeKind<T>(ref T v) where T : struct, IElement
    {
        if (!Tape.RegionsEnabled) return;
        var e = Element.From(in v).Freeze();
        v = e.As<T>();
    }
}

/// <summary>A type that freezes the elements it holds by hand (a generic type the generator does not reach).</summary>
public interface IElementHolder
{
    void FreezeElements();
}

/// <summary>A delegate parameter whose lambda runs LATER than the render that passes it (a skeleton region's content, a list row
/// builder, a component factory, an overlay's content): an element captured into it from that render must be frozen or built
/// inside it (analyzer FGEL001, PERF r3 MODEL M4b).</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class DeferredAttribute : Attribute { }

/// <summary><c>Freeze()</c> for the forms an element takes in app code (PERF r3 MODEL M4b): a value kind (<c>new BoxEl { … }.Freeze()</c>), an
/// array or list of elements (frozen in place, the same instance returned). See <see cref="Element.Freeze"/>.</summary>
public static class ElementFreezeExtensions
{
    public static Element Freeze<T>(this T element) where T : struct, IElement => Element.From(in element).Freeze();
    public static Element[] Freeze(this Element[] elements) { ElementFreezer.FreezeAlways(elements); return elements; }
    public static List<Element> Freeze(this List<Element> elements) { for (int i = 0; i < elements.Count; i++) elements[i] = elements[i].Freeze(); return elements; }
}

/// <summary>A delegate parameter the callee invokes during the call (never later): an element captured into it is read inside the same
/// render, so analyzer FGEL001 does not ask for a freeze (PERF r3 MODEL M4b). The callee also owns what the delegate stores into the
/// objects it is handed (a configure callback filling a dialog's Content): it freezes them before it keeps them, so FGEL003 does not ask
/// the caller to.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ImmediateAttribute : Attribute { }
