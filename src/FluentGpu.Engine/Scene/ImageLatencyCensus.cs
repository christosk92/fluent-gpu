using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FluentGpu.Scene;

/// <summary>A fixed-bucket latency histogram (milliseconds). Allocation-free after construction; the percentile is the
/// upper edge of the bucket that holds it, so it answers "under N ms", which is what a latency budget asks.</summary>
public sealed class LatencyHistogram
{
    /// <summary>Upper bucket edges in ms; the last bucket is open-ended.</summary>
    public static readonly float[] Edges = [16f, 33f, 66f, 100f, 250f, 500f, 1000f, 2000f, 4000f];

    private readonly int[] _buckets = new int[Edges.Length + 1];
    public int Count { get; private set; }
    public float MaxMs { get; private set; }
    public double SumMs { get; private set; }

    public void Add(float ms)
    {
        if (!(ms >= 0f)) ms = 0f;
        int b = 0;
        while (b < Edges.Length && ms > Edges[b]) b++;
        _buckets[b]++;
        Count++;
        SumMs += ms;
        if (ms > MaxMs) MaxMs = ms;
    }

    /// <summary>The bucket edge under which <paramref name="q"/> (0..1) of the samples fall; +inf when it is the open
    /// bucket, NaN when empty.</summary>
    public float Quantile(float q)
    {
        if (Count == 0) return float.NaN;
        int want = System.Math.Max(1, (int)System.MathF.Ceiling(q * Count));
        int seen = 0;
        for (int b = 0; b < _buckets.Length; b++)
        {
            seen += _buckets[b];
            if (seen >= want) return b < Edges.Length ? Edges[b] : float.PositiveInfinity;
        }
        return float.PositiveInfinity;
    }

    public void Reset()
    {
        System.Array.Clear(_buckets);
        Count = 0; MaxMs = 0f; SumMs = 0.0;
    }

    /// <summary><c>n=… p50&lt;… p90&lt;… max=…</c> (allocates; log time only).</summary>
    public string Format()
    {
        if (Count == 0) return "n=0";
        return "n=" + Count.ToString(CultureInfo.InvariantCulture) + " p50<" + Edge(Quantile(0.5f)) + " p90<" + Edge(Quantile(0.9f))
             + " max=" + MaxMs.ToString("0", CultureInfo.InvariantCulture);

        static string Edge(float e) => float.IsPositiveInfinity(e) ? "inf" : e.ToString("0", CultureInfo.InvariantCulture);
    }
}

/// <summary>Always-on: where a picture's time goes between an image node asking for it and its reveal. The owner's
/// "covers pop in late" needs three answers that no other counter gives:
/// <list type="bullet">
/// <item><see cref="SourceWait"/>: a bound image node sat with an EMPTY source (its data had not landed: a row mounted
/// or rebound before its cover url was known) until a url arrived. This is the metadata half.</item>
/// <item><see cref="Fetch"/>: a cache MISS was requested until its first texture landed. This is the fetch/decode half.</item>
/// <item><see cref="RevealFull"/>/<see cref="RevealShort"/>/<see cref="RevealNone"/>: which reveal the landing got. The
/// authored fade (<see cref="FluentGpu.Foundation.ImageTransition.Default"/>), the short warm one, or none.</item>
/// </list>
/// Counters only: no per-image state beyond one timestamp on the cache entry and one captured local per bound image
/// node. A host drains it once per window (Wavee: <c>image.latency</c> at nav-end and scroll-end).</summary>
public sealed class ImageLatencyCensus
{
    public LatencyHistogram SourceWait { get; } = new();
    public LatencyHistogram Fetch { get; } = new();
    /// <summary>Bound image nodes whose source was already known at mount/rebind (no wait).</summary>
    public int SourceImmediate { get; private set; }
    /// <summary><c>Request</c> calls that found the key already cached (any state).</summary>
    public int Hits { get; private set; }
    /// <summary><c>Request</c> calls that created a new entry (a decode — disk or network — started).</summary>
    public int Misses { get; private set; }
    public int RevealFull { get; private set; }
    public int RevealShort { get; private set; }
    public int RevealNone { get; private set; }
    /// <summary>Pending decodes canceled because their last node let go (a de-realized row).</summary>
    public int Canceled { get; private set; }

    public static float MsSince(long startTicks) => startTicks == 0 ? 0f
        : (float)((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency);

    public void NoteSourceImmediate() => SourceImmediate++;
    public void NoteSourceWait(long emptySinceTicks) => SourceWait.Add(MsSince(emptySinceTicks));
    public void NoteHit() => Hits++;
    public void NoteMiss() => Misses++;
    public void NoteFetched(long requestedTicks) { if (requestedTicks != 0) Fetch.Add(MsSince(requestedTicks)); }
    public void NoteReveal(float revealMs, float authoredMs)
    {
        if (revealMs <= 0f) RevealNone++;
        else if (revealMs < authoredMs) RevealShort++;
        else RevealFull++;
    }
    public void NoteCanceled() => Canceled++;

    public bool IsEmpty => SourceWait.Count == 0 && Fetch.Count == 0 && SourceImmediate == 0 && Hits == 0 && Misses == 0
                           && RevealFull == 0 && RevealShort == 0 && RevealNone == 0 && Canceled == 0;

    public void Reset()
    {
        SourceWait.Reset(); Fetch.Reset();
        SourceImmediate = Hits = Misses = RevealFull = RevealShort = RevealNone = Canceled = 0;
    }

    /// <summary>One log line's payload (allocates; call at a window's end, never per frame).</summary>
    public string FormatLine()
    {
        var sb = new StringBuilder(192);
        sb.Append("srcWait(").Append(SourceWait.Format()).Append(") srcImmediate=").Append(SourceImmediate.ToString(CultureInfo.InvariantCulture));
        sb.Append(" fetch(").Append(Fetch.Format()).Append(") hits=").Append(Hits.ToString(CultureInfo.InvariantCulture));
        sb.Append(" misses=").Append(Misses.ToString(CultureInfo.InvariantCulture));
        sb.Append(" reveal(full=").Append(RevealFull.ToString(CultureInfo.InvariantCulture))
          .Append(" short=").Append(RevealShort.ToString(CultureInfo.InvariantCulture))
          .Append(" none=").Append(RevealNone.ToString(CultureInfo.InvariantCulture)).Append(')');
        sb.Append(" canceled=").Append(Canceled.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
