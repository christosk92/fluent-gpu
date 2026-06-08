namespace FluentGpu.Text;

/// <summary>
/// One itemized run: a maximal span of the text with a single BiDi level AND a single script — the unit the shaper
/// consumes (one run = one face/script/direction). Produced by intersecting the BiDi-level runs with the script runs.
/// <see cref="ScriptId"/>/<see cref="ScriptShapes"/> are the opaque DirectWrite script id + shaping flags, carried as
/// portable scalars so this seam stays Windows/COM-free.
/// </summary>
public readonly record struct ItemRun(int Start, int Length, byte BidiLevel, ushort ScriptId, byte ScriptShapes)
{
    public bool IsRightToLeft => (BidiLevel & 1) != 0;
}

/// <summary>A UAX #14 line-break opportunity for one UTF-16 position: the break condition before/after the unit
/// (0 = Neutral, 1 = CanBreak, 2 = MayNotBreak, 3 = MustBreak) and whether it is whitespace.</summary>
public readonly record struct BreakOpp(byte BreakBefore, byte BreakAfter, bool IsWhitespace)
{
    public const byte Neutral = 0, CanBreak = 1, MayNotBreak = 2, MustBreak = 3;
}

/// <summary>
/// Text itemization seam (text.md §4.4): segment a string into BiDi × script runs and compute per-position line-break
/// opportunities (UAX #14). The DirectWrite leaf implements it over <c>IDWriteTextAnalyzer</c> + the callee
/// <c>IDWriteTextAnalysisSource/Sink</c> CCWs; a portable Unicode itemizer is the cross-platform follow-up. The shaper
/// (and, later, the layout engine's wrap) consume this output so measure and render segment identically.
/// </summary>
public interface ITextItemizer
{
    /// <summary>Itemize <paramref name="text"/> into <paramref name="runs"/> (BiDi × script segmentation, logical order)
    /// and <paramref name="breaks"/> (one entry per UTF-16 unit). Both lists are cleared and refilled.</summary>
    void Itemize(ReadOnlySpan<char> text, List<ItemRun> runs, List<BreakOpp> breaks);
}
