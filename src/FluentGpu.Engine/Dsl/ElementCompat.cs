using System.Runtime.CompilerServices;

namespace FluentGpu.Dsl;

/// <summary>The dual-compile forms (PERF r3 MODEL) on a NULLABLE element: <c>el.Is(out BoxEl b)</c>, <c>SameAs</c> and
/// <c>IdentityHash</c> read the same on <c>Element</c> (instance members) and <c>Element?</c> (these).</summary>
public static class ElementCompat
{
    public static bool Is<T>(this Element? el, out T value)
    {
        if (el is { } e) return e.Is(out value);
        value = default!;
        return false;
    }

    public static bool SameAs(this Element? a, Element? b) => a is { } x ? b is { } y && x.SameAs(y) : b is null;
    public static int IdentityHash(this Element? el) => el is { } e ? e.IdentityHash() : 0;

    /// <summary>The element as value kind <typeparamref name="T"/>, or null (the former <c>el as BoxEl</c>).</summary>
    public static T? AsKind<T>(this Element? el) where T : struct => el is { } e && e.Is(out T v) ? v : null;
    public static T? AsKind<T>(this Element el) where T : struct => el.Is(out T v) ? v : null;
}
