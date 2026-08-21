using System.Threading;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Interns SVG path strings into stable integer <c>geometryId</c>s carrying a parsed, verb-preserving
/// <see cref="PathData"/> (gpu-renderer.md §5/§5.1) — the path-lane analog of <see cref="IconGeometryTable"/> (which
/// interns flattened, normalized icon contours instead of retaining a verb stream). The precedent for a
/// <c>.Shared</c> side-table crossing the render seam by int id is <see cref="IconGeometryTable.Shared"/>/
/// <c>SpanRunTable.Shared</c>.
///
/// <para><b>Interning is what makes the realization cache key stable — mint exactly ONE epoch per distinct
/// registration.</b> The path-realization cache (gpu-renderer.md §5.1) keys on <c>(PathGeometryId, quantizedDeviceScale,
/// strokeWidthQ, ruleByte)</c> WITH the content-epoch folded in via the <see cref="PathData"/> a geometryId resolves
/// to. If re-registering the identical <c>(pathData, viewBoxW, viewBoxH, rule)</c> tuple minted a FRESH epoch on every
/// call, every re-registration (e.g. a component remounting and re-declaring the same icon-as-path) would silently
/// miss the tessellation cache and re-tessellate for no reason — defeating the entire point of interning. So
/// <see cref="Register"/> mints exactly one epoch the FIRST time a tuple is seen and hands back the SAME cached
/// <see cref="PathData"/> instance (same id, same epoch) on every later call with that tuple. Do not "optimize" this
/// into minting per call — that silently defeats the geometry cache it exists to stabilize.</para>
///
/// <para><b>Seam discipline (threading-render-seam.md §9):</b> the UI thread <see cref="Register"/>s (single writer,
/// mount time); the render thread <see cref="TryGet"/>s the previous frame's ids concurrently. Entries are stored in
/// an append-only array grown under a lock and PUBLISHED via a release-store of the count — a reader acquire-loads the
/// count before indexing, so it only ever sees fully-written, immutable entries. Ids are never reused.</para>
/// </summary>
public sealed class PathGeometryTable
{
    public static readonly PathGeometryTable Shared = new();

    // A tuple key (rather than a formatted string) avoids any culture/formatting pitfall in folding the view-box and
    // rule into the identity — float/enum equality on the literal values callers pass is exact and allocation-free.
    private readonly Dictionary<(string PathData, float ViewBoxW, float ViewBoxH, FillRule Rule), int> _map = new();
    private PathData?[] _entries = new PathData?[64];
    private int _count = 1;   // id 0 = "none"; published via Volatile
    private readonly object _gate = new();
    private int _version;

    /// <summary>Bumped on every new registration (diagnostics / gate observation).</summary>
    public int Version => Volatile.Read(ref _version);
    /// <summary>Number of distinct interned paths (diagnostics).</summary>
    public int RegisteredCount => Volatile.Read(ref _count) - 1;

    /// <summary>Intern SVG path-data into a stable geometryId (UI thread). Re-registering the same
    /// <paramref name="pathData"/>/<paramref name="viewBoxW"/>/<paramref name="viewBoxH"/>/<paramref name="rule"/>
    /// tuple returns the SAME id and the SAME <see cref="PathData"/> instance (same epoch) — see the type doc for why
    /// that invariant matters to the realization cache. 0 for empty/null input.</summary>
    public int Register(string pathData, float viewBoxW, float viewBoxH, FillRule rule)
    {
        if (string.IsNullOrEmpty(pathData)) return 0;
        var key = (pathData, viewBoxW, viewBoxH, rule);
        if (_map.TryGetValue(key, out int existing)) return existing;

        PathContentEpoch epoch = PathContentEpoch.Mint();   // exactly once per distinct registration — see type doc
        PathData data = PathDataParser.Parse(pathData, epoch, rule, viewBoxW, viewBoxH);

        int id = _count;
        lock (_gate)
        {
            if (id >= _entries.Length)
            {
                var bigger = new PathData?[_entries.Length * 2];
                Array.Copy(_entries, bigger, _entries.Length);
                Volatile.Write(ref _entries, bigger);   // publish the grown array BEFORE the count
            }
            _entries[id] = data;                          // write the (immutable) slot...
            Volatile.Write(ref _count, id + 1);           // ...then release the count (reader acquire-load sees the slot)
        }
        data.GeometryId = id;
        _map[key] = id;
        Interlocked.Increment(ref _version);
        return id;
    }

    /// <summary>Resolve a geometryId to its interned <see cref="PathData"/>. Render-thread-safe (acquire on count,
    /// entries array never moves — only grows into a fresh array that is itself published before the count). False
    /// (and a null-forgiving default <paramref name="path"/>) for an unknown/unregistered id, including 0.</summary>
    public bool TryGet(int geometryId, out PathData path)
    {
        if ((uint)geometryId >= (uint)Volatile.Read(ref _count))
        {
            path = null!;
            return false;
        }
        var entries = Volatile.Read(ref _entries);
        var e = entries[geometryId];
        path = e!;
        return e is not null;
    }
}
