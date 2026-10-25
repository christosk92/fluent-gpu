namespace FluentGpu.Text.DirectWrite;

// ── The shape store (PERF r3 UI, C4): ONE cache of shaped text for every engine in the process ─────────────────────────────
//
// Shaping (itemize -> fallback -> shape) is width-independent and depends only on the text and the shaping inputs, and every
// engine resolves its faces through FontRegistry.Shared, so a shape made by one engine is the shape every other engine would
// make. Before, each engine kept a private 2048-entry cache: the UI's measure engine shaped every string, the render thread's
// glyph engine shaped it again (--fg shape-census: every cacheable render-thread miss was a string the UI had shaped), and
// both kept a ShapeEntry object with three arrays whose fixed initial capacities made a 14-char string cost ~1.3 KB.
//
// Spanned text (rich runs) is kept too (C4 step 3): its key adds every span's EFFECTIVE shaping inputs (range, resolved family,
// weight, size: what the shaping loop reads; colours and decoration flags do not shape), and its record keeps the per-span
// trim ellipsis table the shape left behind. Before, a spanned run re-itemized and re-shaped on every layout on both threads.
//
// Here the records are structs in one array and the text, glyphs and break opportunities live in three slabs, each record
// owning an exact-size range: no object per shape, no allocation per shape once the slabs have grown. Records are LRU-evicted
// in batches like the old caches (same cap); an evicted record's ranges are dead space, reclaimed by compacting the slabs when
// the dead part outgrows the live one.
//
// Threads: the UI thread (measure), the render thread (glyph runs) and any other engine share it. Every operation holds one
// short lock, and a hit COPIES the record into the caller's working buffers under that lock (what the old RestoreShape did
// from a private entry), so a compaction or a reused range can never be read half-written. The engines' own buffers stay
// thread-confined.
public sealed unsafe partial class TextLayoutEngine
{
    internal sealed class ShapeStore
    {
        /// <summary>The process's store (every engine but a validator's or a self-test's private one).</summary>
        public static readonly ShapeStore Shared = new(ShapeCacheCap);

        private struct Rec
        {
            public long Key, Tick;
            public int TextOff, TextLen, GlyphOff, GlyphCount, BreakOff, BreakCount, SpanOff, SpanCount;
            public string Family; public int Weight; public float Size, CharSpacing, LineHeightArg; public int Stacking, LineBounds; public uint Features;
            public float Baseline, UnderlineY, UnderlineThickness, StrikeY, LineH;
            public ushort EllGid; public float EllAdv; public nint EllFace; public float EllSize;
        }

        private readonly object _gate = new();
        private readonly int _cap;
        private readonly Dictionary<long, int> _index;
        private Rec[] _recs = new Rec[256];
        private int _recHigh;
        private int[] _freeRecs = new int[64];
        private int _freeCount;
        private char[] _chars = new char[8192];
        private RunGlyph[] _glyphs = new RunGlyph[8192];
        private BreakOpp[] _breaks = new BreakOpp[8192];
        private SpanRec[] _spans = new SpanRec[256];
        private int _charsUsed, _glyphsUsed, _breaksUsed, _spansUsed, _charsDead, _glyphsDead, _breaksDead, _spansDead;

        /// <summary>A kept span: its effective shaping inputs (the key's part) and the trim ellipsis the shape resolved for it.</summary>
        private struct SpanRec
        {
            public SpanKey Key;
            public ushort EllGid; public nint EllFace; public float EllAdv, EllSize;
        }
        private long _tick;
        private readonly List<int> _evictScratch;
        // Keys handed to the prefetch workers (TextLayoutEngine.Prefetch.cs), false while queued, true once a worker runs it. A
        // measure that misses a RUNNING key waits for the worker's store instead of shaping the string a second time; a QUEUED key
        // it takes back (removes) and shapes itself, so it never waits behind the queue - the worker skips a job no longer here.
        private readonly Dictionary<long, bool> _inFlight = new();

        public ShapeStore(int cap)
        {
            _cap = cap;
            _index = new Dictionary<long, int>(Math.Min(cap, 4096));
            _evictScratch = new List<int>(cap / 4 + 1);
        }

        private bool Matches(in Rec r, ReadOnlySpan<char> text, string family, int weight, float size, float charSpacing, float lineHeight,
                             int stacking, int lineBounds, uint features, ReadOnlySpan<SpanKey> spans)
        {
            if (!(r.TextLen == text.Length && r.SpanCount == spans.Length && r.Weight == weight && r.Size == size && r.CharSpacing == charSpacing
                  && r.Features == features && r.Stacking == stacking && r.LineBounds == lineBounds && NanSafeEq(r.LineHeightArg, lineHeight)
                  && string.Equals(r.Family, family, StringComparison.Ordinal)
                  && text.SequenceEqual(_chars.AsSpan(r.TextOff, r.TextLen))))
                return false;
            for (int s = 0; s < spans.Length; s++)
                if (!_spans[r.SpanOff + s].Key.Equals(spans[s])) return false;
            return true;
        }

        /// <summary>A hit restores the shaped state into <paramref name="e"/>'s working buffers (glyphs, breaks, metrics, the
        /// ellipsis) exactly as the private cache's RestoreShape did, and returns the shaped line height.</summary>
        public bool TryRestore(long key, ReadOnlySpan<char> text, string family, int weight, float size, float charSpacing, float lineHeight,
                               int stacking, int lineBounds, uint features, ReadOnlySpan<SpanKey> spans, TextLayoutEngine e, out float lineH)
        {
            lock (_gate)
            {
                if (!_index.TryGetValue(key, out int i)) { lineH = 0f; return false; }
                ref Rec r = ref _recs[i];
                if (!Matches(in r, text, family, weight, size, charSpacing, lineHeight, stacking, lineBounds, features, spans)) { lineH = 0f; return false; }
                r.Tick = ++_tick;
                e.EnsureGlyphs(r.GlyphCount);
                Array.Copy(_glyphs, r.GlyphOff, e._glyphs, 0, r.GlyphCount);
                e._glyphCount = r.GlyphCount;
                e._breaks.Clear();
                if (e._breaks.Capacity < r.BreakCount) e._breaks.Capacity = r.BreakCount;
                for (int b = 0; b < r.BreakCount; b++) e._breaks.Add(_breaks[r.BreakOff + b]);
                e.Baseline = r.Baseline; e.UnderlineY = r.UnderlineY; e.UnderlineThickness = r.UnderlineThickness; e.StrikeY = r.StrikeY;
                e._textLen = r.TextLen; e._ellGid = r.EllGid; e._ellAdv = r.EllAdv; e._ellFace = r.EllFace; e._ellSize = r.EllSize;
                if (r.SpanCount > 0)
                {
                    e.EnsureSpanEllipses(r.SpanCount);
                    for (int s = 0; s < r.SpanCount; s++)
                    {
                        ref readonly SpanRec sr = ref _spans[r.SpanOff + s];
                        e._spanEllGid[s] = sr.EllGid; e._spanEllFace[s] = sr.EllFace; e._spanEllAdv[s] = sr.EllAdv; e._spanEllSize[s] = sr.EllSize;
                    }
                }
                e._spanEllCount = r.SpanCount;
                lineH = r.LineH;
                return true;
            }
        }

        /// <summary>Keep <paramref name="e"/>'s just-shaped state under <paramref name="key"/> (a key already present, from a
        /// collision or another thread's identical shape, is overwritten, as the private cache did).</summary>
        public void Store(long key, ReadOnlySpan<char> text, string family, int weight, float size, float charSpacing, float lineHeight,
                          int stacking, int lineBounds, uint features, ReadOnlySpan<SpanKey> spans, float lineH, TextLayoutEngine e)
        {
            int bc = e._breaks.Count;
            lock (_gate)
            {
                if (_index.TryGetValue(key, out int i)) Release(ref _recs[i]);
                else
                {
                    if (_index.Count >= _cap) EvictBatch();
                    i = TakeRecSlot();
                    _index[key] = i;
                }
                Reserve(text.Length, e._glyphCount, bc, spans.Length);
                ref Rec r = ref _recs[i];
                r.Key = key;
                r.TextOff = _charsUsed; r.TextLen = text.Length; text.CopyTo(_chars.AsSpan(_charsUsed)); _charsUsed += text.Length;
                r.GlyphOff = _glyphsUsed; r.GlyphCount = e._glyphCount; Array.Copy(e._glyphs, 0, _glyphs, _glyphsUsed, e._glyphCount); _glyphsUsed += e._glyphCount;
                r.BreakOff = _breaksUsed; r.BreakCount = bc;
                for (int b = 0; b < bc; b++) _breaks[_breaksUsed + b] = e._breaks[b];
                _breaksUsed += bc;
                r.SpanOff = _spansUsed; r.SpanCount = spans.Length;
                for (int s = 0; s < spans.Length; s++)
                    _spans[_spansUsed + s] = new SpanRec
                    {
                        Key = spans[s], EllGid = e._spanEllGid[s], EllFace = e._spanEllFace[s], EllAdv = e._spanEllAdv[s], EllSize = e._spanEllSize[s],
                    };
                _spansUsed += spans.Length;
                r.Family = family; r.Weight = weight; r.Size = size; r.CharSpacing = charSpacing; r.LineHeightArg = lineHeight;
                r.Stacking = stacking; r.LineBounds = lineBounds; r.Features = features;
                r.Baseline = e.Baseline; r.UnderlineY = e.UnderlineY; r.UnderlineThickness = e.UnderlineThickness; r.StrikeY = e.StrikeY;
                r.LineH = lineH; r.EllGid = e._ellGid; r.EllAdv = e._ellAdv; r.EllFace = e._ellFace; r.EllSize = e._ellSize;
                r.Tick = ++_tick;
                if (_inFlight.Remove(key)) Monitor.PulseAll(_gate);   // a measure waiting on this key's prefetch wakes to a hit
            }
        }

        /// <summary>Hand <paramref name="key"/> to a prefetch worker: false when it is stored already or a worker has it.</summary>
        public bool BeginInFlight(long key)
        {
            lock (_gate) return !_index.ContainsKey(key) && _inFlight.TryAdd(key, false);
        }

        /// <summary>A worker takes <paramref name="key"/> off the queue: true (now running) unless a measure took it back or it is stored.</summary>
        public bool ClaimInFlight(long key)
        {
            lock (_gate)
            {
                if (!_inFlight.ContainsKey(key)) return false;                                    // taken back by a measure
                if (_index.ContainsKey(key)) { _inFlight.Remove(key); Monitor.PulseAll(_gate); return false; }
                _inFlight[key] = true;
                return true;
            }
        }

        /// <summary>The worker is done with <paramref name="key"/> (stored, found stored, or failed): no measure waits on it any more.</summary>
        public void EndInFlight(long key)
        {
            lock (_gate) { if (_inFlight.Remove(key)) Monitor.PulseAll(_gate); }
        }

        /// <summary>A measure missed <paramref name="key"/>: if a worker is shaping it, wait (at most <paramref name="maxMs"/>) until the
        /// worker is done. True when it waited (the caller looks again); false when no worker had it (the caller shapes).</summary>
        public bool WaitInFlight(long key, int maxMs, out long waitedTicks)
        {
            waitedTicks = 0;
            lock (_gate)
            {
                if (!_inFlight.TryGetValue(key, out bool running)) return false;
                if (!running) { _inFlight.Remove(key); return false; }   // still queued: take it back and shape it here
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long deadline = t0 + System.Diagnostics.Stopwatch.Frequency * maxMs / 1000;
                while (_inFlight.ContainsKey(key))
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now >= deadline) break;
                    Monitor.Wait(_gate, 1);
                }
                waitedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                return true;
            }
        }

        // A record's ranges become dead space; its slot is reused by the next store.
        private void Release(ref Rec r)
        {
            _charsDead += r.TextLen; _glyphsDead += r.GlyphCount; _breaksDead += r.BreakCount; _spansDead += r.SpanCount;
            for (int s = 0; s < r.SpanCount; s++) _spans[r.SpanOff + s].Key.Family = null!;   // the slab keeps no dead string alive
            r.TextLen = r.GlyphCount = r.BreakCount = r.SpanCount = 0;
            r.Family = null!;
        }

        private int TakeRecSlot()
        {
            if (_freeCount > 0) return _freeRecs[--_freeCount];
            if (_recHigh == _recs.Length) Array.Resize(ref _recs, _recs.Length * 2);
            return _recHigh++;
        }

        private void FreeRecSlot(int i)
        {
            if (_freeCount == _freeRecs.Length) Array.Resize(ref _freeRecs, _freeRecs.Length * 2);
            _freeRecs[_freeCount++] = i;
        }

        // The old caches' batched LRU: drop every record not touched within the last 3/4 of a cache's worth of uses (one O(n)
        // pass buys ~cap/4 stores); when every record is recent, drop the single oldest.
        private void EvictBatch()
        {
            long threshold = _tick - _cap * 3 / 4;
            _evictScratch.Clear();
            int oldest = -1; long oldestTick = long.MaxValue;
            foreach (var kv in _index)
            {
                long t = _recs[kv.Value].Tick;
                if (t < threshold) _evictScratch.Add(kv.Value);
                if (t < oldestTick) { oldestTick = t; oldest = kv.Value; }
            }
            if (_evictScratch.Count > 1) { foreach (int i in _evictScratch) Evict(i); }
            else if (oldest >= 0) Evict(oldest);
        }

        private void Evict(int i)
        {
            ref Rec r = ref _recs[i];
            _index.Remove(r.Key);
            Release(ref r);
            FreeRecSlot(i);
        }

        // Room for one more record's ranges: compact first when the dead space outweighs the live part, then grow.
        private void Reserve(int chars, int glyphs, int breaks, int spans)
        {
            bool full = _charsUsed + chars > _chars.Length || _glyphsUsed + glyphs > _glyphs.Length || _breaksUsed + breaks > _breaks.Length
                        || _spansUsed + spans > _spans.Length;
            if (!full) return;
            if (_glyphsDead > _glyphsUsed / 2 || _charsDead > _charsUsed / 2 || _breaksDead > _breaksUsed / 2 || _spansDead > _spansUsed / 2) Compact();
            if (_charsUsed + chars > _chars.Length) Array.Resize(ref _chars, Math.Max(_charsUsed + chars, _chars.Length * 2));
            if (_glyphsUsed + glyphs > _glyphs.Length) Array.Resize(ref _glyphs, Math.Max(_glyphsUsed + glyphs, _glyphs.Length * 2));
            if (_breaksUsed + breaks > _breaks.Length) Array.Resize(ref _breaks, Math.Max(_breaksUsed + breaks, _breaks.Length * 2));
            if (_spansUsed + spans > _spans.Length) Array.Resize(ref _spans, Math.Max(_spansUsed + spans, _spans.Length * 2));
        }

        // Copy every live record's ranges to the front, in place (records are visited in offset order per slab, so a range only
        // ever moves down over dead space and never over a live range not yet moved).
        private void Compact()
        {
            var order = new List<int>(_index.Count);
            foreach (int i in _index.Values) order.Add(i);
            order.Sort((a, b) => _recs[a].GlyphOff.CompareTo(_recs[b].GlyphOff));
            int g = 0;
            foreach (int i in order) { ref Rec r = ref _recs[i]; Array.Copy(_glyphs, r.GlyphOff, _glyphs, g, r.GlyphCount); r.GlyphOff = g; g += r.GlyphCount; }
            order.Sort((a, b) => _recs[a].TextOff.CompareTo(_recs[b].TextOff));
            int c = 0;
            foreach (int i in order) { ref Rec r = ref _recs[i]; Array.Copy(_chars, r.TextOff, _chars, c, r.TextLen); r.TextOff = c; c += r.TextLen; }
            order.Sort((a, b) => _recs[a].BreakOff.CompareTo(_recs[b].BreakOff));
            int k = 0;
            foreach (int i in order) { ref Rec r = ref _recs[i]; Array.Copy(_breaks, r.BreakOff, _breaks, k, r.BreakCount); r.BreakOff = k; k += r.BreakCount; }
            order.Sort((a, b) => _recs[a].SpanOff.CompareTo(_recs[b].SpanOff));
            int q = 0;
            foreach (int i in order) { ref Rec r = ref _recs[i]; Array.Copy(_spans, r.SpanOff, _spans, q, r.SpanCount); r.SpanOff = q; q += r.SpanCount; }
            Array.Clear(_spans, q, _spansUsed - q);   // no stale family string survives past the live end
            _glyphsUsed = g; _charsUsed = c; _breaksUsed = k; _spansUsed = q;
            _glyphsDead = _charsDead = _breaksDead = _spansDead = 0;
        }

        /// <summary>For the census: live records, their chars and glyphs, and the bytes the store holds (slabs + records + index).</summary>
        public void Stats(out int entries, out long chars, out long glyphs, out long bytes)
        {
            lock (_gate)
            {
                entries = _index.Count;
                chars = _charsUsed - _charsDead;
                glyphs = _glyphsUsed - _glyphsDead;
                bytes = _chars.Length * 2L + _glyphs.Length * (long)System.Runtime.CompilerServices.Unsafe.SizeOf<RunGlyph>()
                        + _breaks.Length * (long)System.Runtime.CompilerServices.Unsafe.SizeOf<BreakOpp>()
                        + _spans.Length * (long)System.Runtime.CompilerServices.Unsafe.SizeOf<SpanRec>()
                        + _recs.Length * (long)System.Runtime.CompilerServices.Unsafe.SizeOf<Rec>() + _index.Count * 24L;
            }
        }
    }
}
