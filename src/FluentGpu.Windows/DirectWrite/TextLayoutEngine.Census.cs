using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FluentGpu.Text.DirectWrite;

// ── --fg shape-census (PERF r3 UI): what shaping costs, per engine ──────────────────────────────────────────────────────
//
// Inputs for the shaping model: per engine instance (the UI measure engine, the render thread's glyph engine, any other)
// its layouts, shape-cache hits and misses, a miss split into FRESH (no engine had shaped this key), CROSS (another engine
// already had: the same string shaped twice in the process) and OWN (this engine shaped it before and evicted it), the
// time a miss spends before wrapping (face resolve, ellipsis, itemize, fallback, shape, store) and the re-wrap time, and the
// bytes its shape cache holds. One line per engine every 10 s, printed from the engine's own thread (its cache is read only
// there); only the key map is shared. Off: one static bool read per layout.
public sealed unsafe partial class TextLayoutEngine
{
    private static readonly ConcurrentDictionary<long, int> s_censusShapedBy = new();
    private static int s_censusNextBit = -1;
    private string? _censusName;
    private int _censusBit;
    private long _censusLastDump;
    private long _cLayouts, _cHits, _cMisses, _cFresh, _cCross, _cOwn, _cUncacheable, _cShapeTicks, _cWrapTicks;

    private void CensusLayout(bool hit, bool cacheable, long key, long t0, long t1)
    {
        long now = Stopwatch.GetTimestamp();
        if (_censusName is null)
        {
            _censusName = (Thread.CurrentThread.Name is { Length: > 0 } n ? n.Replace(' ', '_') : "thread") + "#" + Environment.CurrentManagedThreadId;
            _censusBit = 1 << (Interlocked.Increment(ref s_censusNextBit) & 31);
            _censusLastDump = now;
        }
        _cLayouts++;
        if (hit) { _cHits++; _cWrapTicks += now - t0; }
        else
        {
            _cMisses++;
            _cShapeTicks += t1 - t0;
            _cWrapTicks += now - t1;
            if (!cacheable) _cUncacheable++;
            else
            {
                int prev = s_censusShapedBy.TryGetValue(key, out int v) ? v : 0;
                s_censusShapedBy.AddOrUpdate(key, static (_, b) => b, static (_, old, b) => old | b, _censusBit);
                if (prev == 0) _cFresh++;
                else if ((prev & _censusBit) != 0) _cOwn++;
                else _cCross++;
            }
        }
        if (now - _censusLastDump < Stopwatch.Frequency * 10) return;
        _censusLastDump = now;
        CensusDump();
    }

    private void CensusDump()
    {
        long bytes = 0, chars = 0, glyphs = 0;
        foreach (var e in _shapeCache.Values)
        {
            // object headers + fields (an estimate: 16 B header, the ShapeEntry's fields), the three arrays, the dictionary entry.
            bytes += 16 + 120 + 24 + e.Text.Length * 2L + 24 + e.Glyphs.Length * (long)Unsafe.SizeOf<RunGlyph>()
                     + 24 + e.Breaks.Length * (long)Unsafe.SizeOf<BreakOpp>() + 24;
            chars += e.TextLen; glyphs += e.GlyphCount;
        }
        double f = 1000.0 / Stopwatch.Frequency;
        Console.Error.WriteLine(
            $"[shape-census] {_censusName} layouts={_cLayouts} hits={_cHits} misses={_cMisses} fresh={_cFresh} cross={_cCross} own={_cOwn} " +
            $"uncacheable={_cUncacheable} shapeMs={_cShapeTicks * f:F1} usPerMiss={(_cMisses == 0 ? 0 : _cShapeTicks * f * 1000 / _cMisses):F1} " +
            $"wrapMs={_cWrapTicks * f:F1} usPerLayout={(_cLayouts == 0 ? 0 : _cWrapTicks * f * 1000 / _cLayouts):F1} " +
            $"entries={_shapeCache.Count} chars={chars} glyphs={glyphs} retainedKB={bytes / 1024}");
        _cLayouts = _cHits = _cMisses = _cFresh = _cCross = _cOwn = _cUncacheable = _cShapeTicks = _cWrapTicks = 0;
    }
}
