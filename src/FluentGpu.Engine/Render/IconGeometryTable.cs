using System.Threading;

namespace FluentGpu.Render;

/// <summary>
/// Interns SVG path strings into stable integer <c>pathId</c>s carrying flattened, view-box-normalized (0..1) contours,
/// and rasterizes them to R8 coverage masks on demand. The precedent for a <c>.Shared</c> side-table crossing the
/// render seam by int id is <c>SpanRunTable.Shared</c>/<c>StringTable</c>.
///
/// <para><b>Seam discipline (threading-render-seam.md §9):</b> the UI thread <see cref="Register"/>s (single writer,
/// mount time); the render thread <see cref="Rasterize"/>s the previous frame's ids concurrently. Entries are stored in
/// an append-only array grown under a lock and PUBLISHED via a release-store of the count — a reader acquire-loads the
/// count before indexing, so it only ever sees fully-written, immutable entries. Ids are never reused.</para>
///
/// <para>Rasterization is backend-side (lazy, on an atlas cache miss) or a golden-gate call — never a frame hot phase —
/// so the per-call crossing scratch inside <see cref="IconRaster"/> is the accepted posture (the zero-alloc tripwire
/// runs on phases 6–13, which only RECORD the <c>DrawIconMask</c> POD).</para>
/// </summary>
public sealed class IconGeometryTable
{
    public static readonly IconGeometryTable Shared = new();

    private sealed class Entry
    {
        public required float[] Coords;    // normalized 0..1, x/y interleaved
        public required int[] Starts;      // per-contour first-point index
        public required int[] Counts;      // per-contour point count
        public bool EvenOdd;
    }

    private readonly Dictionary<string, int> _map = new(StringComparer.Ordinal);   // UI-thread writer only
    private Entry?[] _entries = new Entry?[64];
    private int _count = 1;    // id 0 = "none"; published via Volatile
    private readonly ContourBuilder _builder = new();   // UI-thread parse scratch
    private readonly object _gate = new();
    private long _rasterCount;
    private int _version;

    /// <summary>Bumped on every new registration (diagnostics / gate observation).</summary>
    public int Version => Volatile.Read(ref _version);
    /// <summary>Total <see cref="Rasterize"/> calls this process — the retheme gate asserts this is UNCHANGED across a
    /// theme swap (masks are colorless, so a recolor must not re-raster).</summary>
    public long RasterCount => Interlocked.Read(ref _rasterCount);
    /// <summary>Number of distinct interned paths (diagnostics).</summary>
    public int RegisteredCount => Volatile.Read(ref _count) - 1;

    /// <summary>Intern a path string → a stable pathId (UI thread). <paramref name="nominalW"/>/<paramref name="nominalH"/>
    /// is the authoring view-box (Files icons are 16); the contours are normalized to 0..1 by it, so the raster target
    /// px is pure w×h. Re-registering the same string returns the existing id. 0 for empty input.</summary>
    public int Register(string? pathData, float nominalW = 16f, float nominalH = 16f, bool evenOdd = false)
    {
        if (string.IsNullOrEmpty(pathData)) return 0;
        if (_map.TryGetValue(pathData, out int existing)) return existing;

        IconPathParser.Parse(pathData, _builder);
        int floats = _builder.Coords.Count;
        var coords = new float[floats];
        float invW = nominalW > 0f ? 1f / nominalW : 1f;
        float invH = nominalH > 0f ? 1f / nominalH : 1f;
        for (int p = 0; p * 2 < floats; p++)
        {
            coords[p * 2] = _builder.Coords[p * 2] * invW;
            coords[p * 2 + 1] = _builder.Coords[p * 2 + 1] * invH;
        }
        var e = new Entry
        {
            Coords = coords,
            Starts = _builder.ContourStart.ToArray(),
            Counts = _builder.ContourCount.ToArray(),
            EvenOdd = evenOdd,
        };

        int id = _count;
        lock (_gate)
        {
            if (id >= _entries.Length)
            {
                var bigger = new Entry?[_entries.Length * 2];
                Array.Copy(_entries, bigger, _entries.Length);
                Volatile.Write(ref _entries, bigger);   // publish the grown array BEFORE the count
            }
            _entries[id] = e;                            // write the (immutable) slot...
            Volatile.Write(ref _count, id + 1);          // ...then release the count (reader acquire-load sees the slot)
        }
        _map[pathData] = id;
        Interlocked.Increment(ref _version);
        return id;
    }

    /// <summary>Rasterize <paramref name="pathId"/> at <paramref name="w"/>×<paramref name="h"/> device px into
    /// <paramref name="dst"/> (row-major R8). Render-thread-safe. No-op (clears) for an unknown id.</summary>
    public void Rasterize(int pathId, int w, int h, Span<byte> dst)
    {
        if ((uint)pathId >= (uint)Volatile.Read(ref _count))
        {
            int need = Math.Max(0, w * h);
            if (need > 0 && dst.Length >= need) dst.Slice(0, need).Clear();
            return;
        }
        var entries = Volatile.Read(ref _entries);
        var e = entries[pathId];
        if (e is null) return;
        IconRaster.Rasterize(e.Coords, e.Starts, e.Counts, e.EvenOdd, w, h, dst);
        Interlocked.Increment(ref _rasterCount);
    }

    /// <summary>Contour + point counts for an interned id (0/0 if unknown) — golden-gate observability.</summary>
    public (int Contours, int Points) ShapeOf(int pathId)
    {
        if ((uint)pathId >= (uint)Volatile.Read(ref _count)) return (0, 0);
        var e = Volatile.Read(ref _entries)[pathId];
        return e is null ? (0, 0) : (e.Starts.Length, e.Coords.Length / 2);
    }

    /// <summary>Normalized bounding box of an interned id (all-zero if unknown) — golden-gate observability.</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) BoundsOf(int pathId)
    {
        if ((uint)pathId >= (uint)Volatile.Read(ref _count)) return default;
        var e = Volatile.Read(ref _entries)[pathId];
        if (e is null || e.Coords.Length == 0) return default;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int p = 0; p * 2 < e.Coords.Length; p++)
        {
            float x = e.Coords[p * 2], y = e.Coords[p * 2 + 1];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        return (minX, minY, maxX, maxY);
    }
}
