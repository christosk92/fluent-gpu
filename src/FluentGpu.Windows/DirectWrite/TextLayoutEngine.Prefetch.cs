using FluentGpu.Foundation;

namespace FluentGpu.Text.DirectWrite;

// ── Shape prefetch (PERF r3 UI, C4 step 4): a page's first-visit shaping runs on workers, in parallel with the layout pass ──
//
// A mount frame measures every new text node, and a node whose (text, style) the shape store has never seen itemizes and
// shapes inside its measure, one after another on the UI thread (--fg shape-census: ~60-70 per navigation, ~65 µs each in
// Debug). The layout pass now walks the subtrees it is about to solve first and hands each text node with no measure entry for
// its current text and style to the font system (FlexLayout.PrefetchText -> IFontSystem.PrefetchShape); the font system
// computes the node's shape key ON THE UI THREAD with the engine that will measure it (TextLayoutEngine.ShapeKeyFor: the same
// derivation Layout uses), marks the key in flight in the shape store, and queues the call to a small pool of worker threads,
// each with its own engine (its own itemizer and shaper) over the SAME store. A worker lays the call out (at an unbounded
// width: shaping is width-independent) and the store keeps the shape; the measure that reaches the node then restores it, or,
// when the worker is still on it, waits for the worker's store (ShapeStore.WaitInFlight, bounded) instead of shaping it again.
//
// Output: identical. The worker makes the same call the measure would (the same text, family, weight, size, spacing, line
// height, stacking, line bounds, spans, case and features), so the store keeps the same shape the measure would have made;
// --fg shape-validate re-checks every hit against a fresh shape. Without the pool (a prefetch switched off, a failed worker, a
// timed-out wait) the measure simply shapes, as before.
public sealed unsafe partial class TextLayoutEngine
{
    /// <summary>The longest a measure waits for a worker that is shaping its string (then it shapes the string itself).</summary>
    private const int PrefetchWaitMs = 20;

    /// <summary>A prefetch worker's engine: it never waits on an in-flight key (the one in flight is its own job's).</summary>
    private bool _isPrefetchWorker;

    internal readonly record struct ShapeJob(string Text, string Family, int Weight, float Size, float CharSpacing, float LineHeight, int Stacking,
        int LineBounds, SpanStyle[]? Spans, StringTable? Names, int OverflowSuffixStart, int Typography, long Key);

    /// <summary>The workers: started on the first prefetch, background threads, each owning an engine over the shared store.</summary>
    internal sealed class ShapePrefetchPool
    {
        public static readonly ShapePrefetchPool Shared = new();

        private readonly object _gate = new();
        private readonly Queue<ShapeJob> _jobs = new(256);
        private readonly SemaphoreSlim _signal = new(0);
        private int _started;
        public long Enqueued, Shaped;

        /// <summary>Worker count: a few (the pass consumes the shapes at the rate it measures; more workers than that only contend
        /// with the render thread), at least one.</summary>
        private static int Workers => Math.Clamp(Environment.ProcessorCount / 3, 1, 4);

        /// <summary>Start the workers (each builds its engine on its own thread right away), so the first page's prefetch finds them
        /// warm. Idempotent; the font system calls it when it is built.</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
                for (int i = 0; i < Workers; i++)
                    new Thread(Run) { IsBackground = true, Name = "fgpu-shape-" + i }.Start();
        }

        public void Enqueue(in ShapeJob job)
        {
            Start();
            lock (_gate) _jobs.Enqueue(job);
            Enqueued++;
            _signal.Release();
        }

        private void Run()
        {
            var engine = new TextLayoutEngine(FontRegistry.Shared, ShapeStore.Shared) { _isPrefetchWorker = true };
            while (true)
            {
                _signal.Wait();
                ShapeJob j;
                lock (_gate) { if (!_jobs.TryDequeue(out j)) continue; }
                if (!ShapeStore.Shared.ClaimInFlight(j.Key)) continue;   // a measure took it back, or it is stored already
                try
                {
                    engine.Layout(j.Text.AsSpan(), j.Family, j.Weight, j.Size, float.PositiveInfinity, 0, 0, 0, j.CharSpacing, j.LineHeight,
                        j.Stacking, j.LineBounds, j.Spans is null ? default : j.Spans.AsSpan(), j.Names, j.OverflowSuffixStart, j.Typography);
                    Interlocked.Increment(ref Shaped);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A prefetch is only a hint: the measure shapes the string itself.
                }
                finally { ShapeStore.Shared.EndInFlight(j.Key); }
            }
        }
    }

    /// <summary>Prefetch this call's shape (UI thread, on the engine that will measure it): its key is derived here exactly as
    /// <see cref="Layout"/> derives it, and a key neither stored nor in flight goes to the workers. False when nothing was queued.</summary>
    internal bool Prefetch(string text, string family, int weight, float size, float charSpacing, float lineHeight, int stacking, int lineBounds,
        SpanStyle[]? spans, StringTable? names, int overflowSuffixStart, int typography)
    {
        if (_store is null || text.Length == 0) return false;
        long key = ShapeKeyFor(text.AsSpan(), family, weight, size, charSpacing, lineHeight, stacking, lineBounds,
            spans is null ? default : spans.AsSpan(), names, typography);
        if (key == 0 || !_store.BeginInFlight(key)) return false;
        ShapePrefetchPool.Shared.Enqueue(new ShapeJob(text, family, weight, size, charSpacing, lineHeight, stacking, lineBounds, spans, names,
            overflowSuffixStart, typography, key));
        _cPrefetched++;
        return true;
    }
}
