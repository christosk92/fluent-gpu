using FluentGpu.Foundation;

namespace FluentGpu.Text;

/// <summary>
/// The culture-invariant case transform behind <see cref="TextCase"/> (E19). Pure, allocation-free span overloads for the
/// shaping paths, and a string overload for cold callers. Every mapping is the invariant simple case mapping, so the
/// UTF-16 length never changes and an index into the written text is the same index into the shaped text.
/// </summary>
public static class TextCaseMap
{
    /// <summary>Write <paramref name="src"/> transformed by <paramref name="mode"/> into <paramref name="dst"/>
    /// (<c>dst.Length &gt;= src.Length</c>); returns the length written (always <c>src.Length</c>). No allocation.</summary>
    public static int Apply(ReadOnlySpan<char> src, Span<char> dst, TextCase mode)
    {
        switch (mode)
        {
            case TextCase.Lower:
                return src.ToLowerInvariant(dst);
            case TextCase.Upper:
                return src.ToUpperInvariant(dst);
            default:
                src.CopyTo(dst);
                return src.Length;
        }
    }

    /// <summary>The transformed string; returns <paramref name="s"/> itself for <see cref="TextCase.AsWritten"/> or when
    /// nothing changes. Allocates only when the text actually changes (cold callers: a cache-miss shape).</summary>
    public static string Apply(string s, TextCase mode)
    {
        if (mode == TextCase.AsWritten || s.Length == 0) return s;
        return mode == TextCase.Lower ? s.ToLowerInvariant() : s.ToUpperInvariant();
    }
}

/// <summary>
/// A reusable, grow-only buffer a shaping path transforms text into (E19): <see cref="Get"/> returns the written text itself
/// for <see cref="TextCase.AsWritten"/> (no copy) and the transformed copy otherwise. Steady state allocates nothing; the
/// returned span is valid until the next <see cref="Get"/> on the same instance. Single-thread-confined like the engine
/// that owns it.
/// </summary>
public sealed class TextCaseScratch
{
    private char[] _buf = new char[128];

    public ReadOnlySpan<char> Get(ReadOnlySpan<char> text, TextCase mode)
    {
        if (mode == TextCase.AsWritten || text.IsEmpty) return text;
        if (_buf.Length < text.Length) _buf = new char[Math.Max(text.Length, _buf.Length * 2)];
        int n = TextCaseMap.Apply(text, _buf, mode);
        return new ReadOnlySpan<char>(_buf, 0, n);
    }
}
