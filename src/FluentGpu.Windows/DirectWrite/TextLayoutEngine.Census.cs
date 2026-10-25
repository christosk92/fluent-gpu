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
// bytes its shape cache holds. One line per engine every 5 s, printed from the engine's own thread (its cache is read only
// there); only the key map is shared. Off: one static bool read per layout.
public sealed unsafe partial class TextLayoutEngine
{
    private static readonly ConcurrentDictionary<long, int> s_censusShapedBy = new();
    private static int s_censusNextBit = -1;
    private string? _censusName;
    private int _censusBit;
    private long _censusLastDump;
    private long _cLayouts, _cHits, _cMisses, _cFresh, _cCross, _cOwn, _cEmpty, _cSpanned, _cShapeTicks, _cSpannedTicks, _cWrapTicks;

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
            _cWrapTicks += now - t1;
            if (!cacheable)
            {
                // Empty text returns before shaping (t1 == 0: no store/wrap split); spanned text re-shapes on every layout.
                if (t1 == 0) _cEmpty++;
                else { _cSpanned++; _cSpannedTicks += t1 - t0; }
            }
            else
            {
                _cShapeTicks += t1 - t0;
                int prev = s_censusShapedBy.TryGetValue(key, out int v) ? v : 0;
                s_censusShapedBy.AddOrUpdate(key, static (_, b) => b, static (_, old, b) => old | b, _censusBit);
                if (prev == 0) _cFresh++;
                else if ((prev & _censusBit) != 0) _cOwn++;
                else _cCross++;
            }
        }
        if (now - _censusLastDump < Stopwatch.Frequency * 5) return;
        _censusLastDump = now;
        CensusDump();
    }

    private void CensusDump()
    {
        int entries = 0; long bytes = 0, chars = 0, glyphs = 0;
        _store?.Stats(out entries, out chars, out glyphs, out bytes);   // the store this engine uses (the shared one: process-wide)
        double f = 1000.0 / Stopwatch.Frequency;
        Console.Error.WriteLine(
            $"[shape-census] {_censusName} layouts={_cLayouts} hits={_cHits} misses={_cMisses} fresh={_cFresh} cross={_cCross} own={_cOwn} " +
            $"empty={_cEmpty} spanned={_cSpanned} spannedMs={_cSpannedTicks * f:F1} shapeMs={_cShapeTicks * f:F1} " +
            $"usPerShape={(_cFresh + _cCross + _cOwn == 0 ? 0 : _cShapeTicks * f * 1000 / (_cFresh + _cCross + _cOwn)):F1} " +
            $"wrapMs={_cWrapTicks * f:F1} usPerLayout={(_cLayouts == 0 ? 0 : _cWrapTicks * f * 1000 / _cLayouts):F1} " +
            $"storeEntries={entries} chars={chars} glyphs={glyphs} storeKB={bytes / 1024}");
        _cLayouts = _cHits = _cMisses = _cFresh = _cCross = _cOwn = _cEmpty = _cSpanned = _cShapeTicks = _cSpannedTicks = _cWrapTicks = 0;
    }
}
