using System.Runtime.CompilerServices;

namespace FluentGpu.Dsl;

/// <summary>
/// The element operations that read the same on today's reference-type elements and on the value-type elements the PERF r3
/// MODEL migration moves to (scratch/p3model/DESIGN.md 2.2, 5.2). Code that inspects or compares elements uses these instead of
/// the C# operators that only work on classes, so it compiles unchanged when the element types become structs:
/// <list type="bullet">
/// <item><c>el.Is(out BoxEl b)</c> instead of <c>el is BoxEl b</c> (a type pattern needs a reference conversion);</item>
/// <item><c>el.As&lt;BoxEl&gt;()</c> instead of <c>(BoxEl)el</c>;</item>
/// <item><c>a.SameAs(b)</c> instead of <c>ReferenceEquals(a, b)</c> (identity: the same built element, not equal props);</item>
/// <item><c>el.IdentityHash()</c> instead of <c>RuntimeHelpers.GetHashCode(el)</c>;</item>
/// <item><c>el.Freeze()</c> where an element built in a render is kept for a later render. On reference-type elements it is the
/// identity; on value-type elements it copies the element out of the render's buffer to the heap.</item>
/// </list>
/// </summary>
public static class ElementCompat
{
    /// <summary>True when <paramref name="el"/> is a <typeparamref name="T"/>, which it outputs (default otherwise). The spelling of
    /// <c>el is T t</c> that also compiles for value-type elements; a property pattern becomes <c>el.Is(out T t) &amp;&amp; t.X ...</c>.</summary>
    public static bool Is<T>(this Element? el, out T value) where T : Element
    {
        if (el is T t) { value = t; return true; }
        value = null!;
        return false;
    }

    /// <summary>The element as a <typeparamref name="T"/>; throws when it is another kind (as the cast it replaces does).</summary>
    public static T As<T>(this Element el) where T : Element => (T)el;

    /// <summary>The same built element (identity, never prop equality). Two nulls are the same; null and an element are not.</summary>
    public static bool SameAs(this Element? a, Element? b) => ReferenceEquals(a, b);

    /// <summary>An identity hash consistent with <see cref="SameAs"/> (0 for null).</summary>
    public static int IdentityHash(this Element? el) => el is null ? 0 : RuntimeHelpers.GetHashCode(el);

    /// <summary>The element, valid for as long as it is referenced. Reference-type elements already are: the identity.</summary>
    public static T Freeze<T>(this T el) where T : Element => el;
}
