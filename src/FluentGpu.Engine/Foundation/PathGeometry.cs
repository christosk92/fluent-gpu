using System.Threading;

namespace FluentGpu.Foundation;

/// <summary>Which drawing operation each entry of a <see cref="PathData"/>'s verb stream performs, and therefore how
/// many <see cref="Point2"/> entries in the matching point stream it consumes: <see cref="MoveTo"/>/<see cref="LineTo"/>
/// consume 1 (the endpoint), <see cref="QuadTo"/> consumes 2 (one control point + the endpoint), <see cref="CubicTo"/>
/// consumes 3 (two control points + the endpoint), <see cref="Close"/> consumes 0 (it closes back to the current
/// subpath's <see cref="MoveTo"/> — no new point).</summary>
public enum PathVerb : byte { MoveTo = 0, LineTo = 1, QuadTo = 2, CubicTo = 3, Close = 4 }

/// <summary>The polygon fill rule (SVG/Direct2D semantics): <see cref="NonZero"/> winding (the default — a point is
/// filled when the signed edge-crossing count is non-zero) or <see cref="EvenOdd"/> (filled when the crossing count is
/// odd).</summary>
public enum FillRule : byte { NonZero = 0, EvenOdd = 1 }

/// <summary>Stroke end-cap shape (SVG/Direct2D <c>stroke-linecap</c>).</summary>
public enum LineCap : byte { Butt = 0, Round = 1, Square = 2 }

/// <summary>Stroke corner-join shape (SVG/Direct2D <c>stroke-linejoin</c>).</summary>
public enum LineJoin : byte { Miter = 0, Round = 1, Bevel = 2 }

/// <summary>
/// A path stroke's rendering parameters (gpu-renderer.md §5 <c>StrokePathCmd</c>). <see cref="MiterLimit"/> only
/// matters for <see cref="LineJoin.Miter"/> (Direct2D's <c>ID2D1StrokeStyle::GetMiterLimit</c> convention: the ratio of
/// miter length to stroke width beyond which the join falls back to bevel). <see cref="DashOn"/>/<see cref="DashOff"/>
/// are path-length units; both 0 (the default) means a solid stroke.
/// </summary>
public readonly record struct StrokeStyle(
    float Width, LineCap Cap = LineCap.Round, LineJoin Join = LineJoin.Round,
    float MiterLimit = 4f, float DashOn = 0f, float DashOff = 0f)
{
    /// <summary>No stroke at all (zero or negative width) — the tessellator emits no <c>StrokePathCmd</c>.</summary>
    public bool IsNone => Width <= 0f;
}

/// <summary>
/// A monotonically-increasing content-version stamp for a <see cref="PathData"/>. The only way to obtain a live one is
/// <see cref="Mint"/> — there is no public constructor — so a caller CANNOT construct a <see cref="PathData"/> (or call
/// <see cref="PathData.WithRule"/>) without naming a freshly-minted epoch at the call site. That makes a missed
/// content-version bump a COMPILE-TIME problem (you would have had to write <c>PathContentEpoch.Mint()</c> and simply
/// not use its result) rather than a silent stale geometry-cache bug: the path-realization cache key (gpu-renderer.md
/// §5.1, <c>PathRealizationKey</c>) folds in the epoch via the <see cref="PathData"/> it resolves to, so two instances
/// sharing a <see cref="PathData.GeometryId"/> but carrying different epochs correctly miss the tessellation cache
/// instead of replaying stale vertices.
/// </summary>
public readonly struct PathContentEpoch : IEquatable<PathContentEpoch>
{
    private static ulong s_next;

    /// <summary>The raw monotonic stamp. 0 (<see cref="IsNone"/>) is the sentinel for "never minted", i.e.
    /// <c>default(PathContentEpoch)</c>.</summary>
    public readonly ulong Value;

    private PathContentEpoch(ulong v) => Value = v;

    /// <summary>Mint a fresh, process-monotonic epoch. Call this exactly once per distinct content version — every
    /// <see cref="PathData"/> construction site must literally write <c>PathContentEpoch.Mint()</c>, or thread through
    /// one already minted for this exact content (as <c>PathGeometryTable</c> does when it interns).</summary>
    public static PathContentEpoch Mint() => new(Interlocked.Increment(ref s_next));

    /// <summary>True for <c>default(PathContentEpoch)</c> — a caller that bypassed <see cref="Mint"/>. A
    /// <see cref="PathData"/> built with a none epoch is treated as malformed input and degenerates to empty.</summary>
    public bool IsNone => Value == 0;

    public bool Equals(PathContentEpoch other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is PathContentEpoch other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => IsNone ? "PathContentEpoch.None" : $"PathContentEpoch({Value})";
    public static bool operator ==(PathContentEpoch left, PathContentEpoch right) => left.Equals(right);
    public static bool operator !=(PathContentEpoch left, PathContentEpoch right) => !left.Equals(right);
}

/// <summary>
/// An immutable, verb-preserving vector path: parallel <see cref="Verbs"/> and <see cref="Points"/> streams (the SVG/
/// Direct2D path-geometry shape — gpu-renderer.md §5), the <see cref="Rule"/> the tessellator and hit-test both fill
/// with (§5.1 — hit-test shares the fill RULE, not just the vertices), and a content <see cref="Epoch"/> that keys the
/// geometry-realization cache (§5.1).
///
/// <para>Deliberately a sealed CLASS, not a <c>record</c>: a record's compiler-generated <c>with</c> expression would
/// let a caller clone-and-tweak a <see cref="PathData"/> around the required epoch (e.g. swap in new <see cref="Points"/>
/// but keep the old <see cref="Epoch"/>) — exactly the silent-stale-cache bug <see cref="PathContentEpoch"/> exists to
/// make impossible. The only ways to get an instance are the one constructor and the explicit <see cref="WithRule"/>,
/// both of which demand a freshly-named epoch at the call site.</para>
///
/// <para>The ctor copies the input spans into private arrays: the caller's spans may point at stack or arena memory
/// that does not outlive the call (the array-bearing-sealed-class precedent is <c>SpanRun</c>/<c>SpanRunTable</c> in
/// this same file's sibling, SpanText.cs). Malformed input is clamped, never thrown — this repo's documented posture
/// for bad input (validation.md): a <c>default</c> (unminted) <see cref="PathContentEpoch"/>, a verb stream that does
/// not start with <see cref="PathVerb.MoveTo"/>, or a point count inconsistent with the verb stream (see
/// <see cref="PathVerb"/> for the exact per-verb counts) all produce an empty/degenerate instance — zero verbs, zero
/// points, an empty <see cref="ControlBounds"/> — instead of an exception.</para>
/// </summary>
public sealed class PathData
{
    private static readonly PathVerb[] s_emptyVerbs = [];
    private static readonly Point2[] s_emptyPoints = [];

    private readonly PathVerb[] _verbs;
    private readonly Point2[] _points;

    /// <summary>The content-version stamp named at construction. Two instances that are geometrically identical but
    /// minted with different epochs are DELIBERATELY treated as different cache keys (gpu-renderer.md §5.1) — the
    /// epoch, not a structural comparison, is the realization cache's source of truth.</summary>
    public PathContentEpoch Epoch { get; }

    /// <summary><c>PathGeometryTable</c>'s interning id for this instance, or 0 when not (yet) interned. Set exactly
    /// once, by the table, at registration time — no other caller should assign it.</summary>
    public int GeometryId { get; internal set; }

    /// <summary>The fill rule the tessellator and hit-test both honor.</summary>
    public FillRule Rule { get; }

    /// <summary>The bounding box of every point in <see cref="Points"/> — the PRE-flatten control-point hull (cubic/
    /// quadratic control points included, not just the on-curve endpoints) — suitable for a cheap cull/layout bound
    /// before any tessellation runs. <c>default</c> (empty) for a degenerate instance.</summary>
    public RectF ControlBounds { get; }

    /// <summary>The verb stream (the SVG command shape), parallel to <see cref="Points"/> per the per-verb point
    /// counts documented on <see cref="PathVerb"/>.</summary>
    public ReadOnlySpan<PathVerb> Verbs => _verbs;

    /// <summary>The point stream: on-curve endpoints and off-curve control points, in verb order.</summary>
    public ReadOnlySpan<Point2> Points => _points;

    /// <summary>Number of entries in <see cref="Verbs"/>.</summary>
    public int VerbCount => _verbs.Length;

    /// <summary>Number of entries in <see cref="Points"/>.</summary>
    public int PointCount => _points.Length;

    /// <summary>The one constructor. <paramref name="epoch"/> is positional-first with no default, and
    /// <see cref="PathContentEpoch"/> has no usable public constructor — so every construction site must literally
    /// write <c>PathContentEpoch.Mint()</c> (or thread through one already minted for this exact content).
    /// <paramref name="controlBounds"/> is the pre-flatten control-point hull — the union of every point in
    /// <paramref name="points"/> — which the caller (typically the parser, which is already walking the point list as
    /// it builds it) computes and hands in rather than having the ctor re-scan the copied array a second time.</summary>
    public PathData(PathContentEpoch epoch, ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<Point2> points,
        FillRule rule, in RectF controlBounds)
    {
        Epoch = epoch;
        Rule = rule;

        if (epoch.IsNone || !IsWellFormed(verbs, points))
        {
            _verbs = s_emptyVerbs;
            _points = s_emptyPoints;
            ControlBounds = default;
            return;
        }

        _verbs = verbs.ToArray();
        _points = points.ToArray();
        ControlBounds = controlBounds;
    }

    /// <summary>True iff <paramref name="verbs"/> is empty, or starts with <see cref="PathVerb.MoveTo"/> AND the point
    /// count it implies (per <see cref="PathVerb"/>'s documented per-verb counts) exactly matches
    /// <paramref name="points"/>'s length.</summary>
    private static bool IsWellFormed(ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<Point2> points)
    {
        if (verbs.Length == 0) return points.Length == 0;
        if (verbs[0] != PathVerb.MoveTo) return false;

        int needed = 0;
        for (int i = 0; i < verbs.Length; i++)
        {
            needed += verbs[i] switch
            {
                PathVerb.MoveTo or PathVerb.LineTo => 1,
                PathVerb.QuadTo => 2,
                PathVerb.CubicTo => 3,
                _ => 0,   // Close
            };
        }
        return needed == points.Length;
    }

    /// <summary>A copy of this path with a different <see cref="Rule"/>, keyed by a fresh <paramref name="epoch"/> —
    /// changing the fill rule changes what gets tessellated and hit-tested, so it demands a new cache key exactly like
    /// a geometry edit does. The result starts un-interned (<see cref="GeometryId"/> 0).</summary>
    public PathData WithRule(PathContentEpoch epoch, FillRule rule) => new(epoch, _verbs, _points, rule, ControlBounds);
}
