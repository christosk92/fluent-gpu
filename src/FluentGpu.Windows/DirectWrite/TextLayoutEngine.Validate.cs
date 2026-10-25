using System.Diagnostics;

namespace FluentGpu.Text.DirectWrite;

// ── --fg shape-validate (PERF r3 UI, C4): a shape-store hit must lay out exactly what a fresh shape lays out ───────────────
//
// On every layout that restored its shape from the store, the same call is laid out again by this thread's validator engine,
// which has NO store (every layout itemizes and shapes for real), and the two results are compared field by field: the box
// (width, height, line count), the metrics (baseline, underline, strike), every laid glyph (glyph id, face, x, y, size, span),
// every cluster (source index, x, advance, line) and every line record. One summary line per engine every 5 s; the first
// mismatches are printed with their text. Diagnostic: off, it costs one static bool read per store hit.
public sealed unsafe partial class TextLayoutEngine
{
    [ThreadStatic] private static TextLayoutEngine? t_validator;
    private bool _isValidator;
    private long _vChecked, _vMismatches, _vLastDump;
    private static int s_vPrinted;

    private void ValidateHit(ReadOnlySpan<char> text, string family, int weight, float size, float maxWidth, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int stacking, int lineBounds, int overflowSuffixStart, int typography, float alignWidth)
    {
        var v = t_validator ??= new TextLayoutEngine(_registry, store: null) { _isValidator = true };
        v.Layout(text, family, weight, size, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, stacking, lineBounds,
            default, null, overflowSuffixStart, typography, alignWidth);
        string? why = Diff(v);
        _vChecked++;
        if (why is not null)
        {
            _vMismatches++;
            if (Interlocked.Increment(ref s_vPrinted) <= 20)
                Console.Error.WriteLine($"[shape-validate] MISMATCH {why}: \"{text.ToString()}\" family={family} weight={weight} size={size} maxW={maxWidth} wrap={wrap} trim={trim} maxLines={maxLines}");
        }
        long now = Stopwatch.GetTimestamp();
        if (_vLastDump == 0) _vLastDump = now;
        if (now - _vLastDump < Stopwatch.Frequency * 5) return;
        _vLastDump = now;
        Console.Error.WriteLine($"[shape-validate] {(Thread.CurrentThread.Name ?? "thread").Replace(' ', '_')}#{Environment.CurrentManagedThreadId} checked={_vChecked} mismatches={_vMismatches}");
    }

    private string? Diff(TextLayoutEngine v)
    {
        if (!Width.Equals(v.Width) || !Height.Equals(v.Height) || LineCount != v.LineCount) return "box";
        if (!Baseline.Equals(v.Baseline) || !UnderlineY.Equals(v.UnderlineY) || !UnderlineThickness.Equals(v.UnderlineThickness) || !StrikeY.Equals(v.StrikeY))
            return "metrics";
        if (_laidCount != v._laidCount) return "laid count";
        for (int i = 0; i < _laidCount; i++)
        {
            ref readonly var a = ref _laid[i]; ref readonly var b = ref v._laid[i];
            if (a.Gid != b.Gid || a.Face != b.Face || !a.X.Equals(b.X) || !a.Y.Equals(b.Y) || !a.Size.Equals(b.Size) || a.Span != b.Span) return "glyph " + i;
        }
        if (_clusterCount != v._clusterCount) return "cluster count";
        for (int i = 0; i < _clusterCount; i++)
        {
            ref readonly var a = ref _clusters[i]; ref readonly var b = ref v._clusters[i];
            if (a.Cluster != b.Cluster || !a.X.Equals(b.X) || !a.Advance.Equals(b.Advance) || a.Line != b.Line) return "cluster " + i;
        }
        if (_lineRecCount != v._lineRecCount) return "line count";
        for (int i = 0; i < _lineRecCount; i++)
        {
            ref readonly var a = ref _lines[i]; ref readonly var b = ref v._lines[i];
            if (a.StartChar != b.StartChar || a.EndChar != b.EndChar || a.VisibleBodyEnd != b.VisibleBodyEnd || a.SuffixStart != b.SuffixStart
                || !a.Top.Equals(b.Top) || !a.Height.Equals(b.Height) || !a.Width.Equals(b.Width) || a.FirstGlyph != b.FirstGlyph
                || a.GlyphCount != b.GlyphCount || a.FirstLaid != b.FirstLaid || a.LaidCount != b.LaidCount || !a.X.Equals(b.X))
                return "line " + i;
        }
        return null;
    }
}
