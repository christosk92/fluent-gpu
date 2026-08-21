using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Accumulates a verb-preserving path (parallel <see cref="PathVerb"/> and <see cref="Point2"/> streams — see
/// <see cref="PathVerb"/> for the per-verb point counts) for <see cref="PathDataParser.Parse"/>. The sibling of
/// <see cref="ContourBuilder"/> (which flattens curves immediately for icons): this builder keeps curves as curves,
/// because the tessellator (gpu-renderer.md §5 step 1), not the parser, owns subdivision. Tracks the running
/// <see cref="Bounds"/> of every point appended — including off-curve control points — as it goes, so
/// <see cref="Finish"/> never needs a second pass over the point list.
///
/// <para>One builder is reused per <see cref="PathDataParser.Parse"/> call (no per-path allocation churn once its
/// lists reach capacity). NOT thread-safe: parsing is a UI-thread-only cold path (mount/registration time), the same
/// discipline as <see cref="ContourBuilder"/>.</para>
/// </summary>
public sealed class PathBuilder
{
    /// <summary>The verb stream built so far.</summary>
    public readonly List<PathVerb> Verbs = new(64);
    /// <summary>The point stream built so far (parallel to <see cref="Verbs"/> per its documented per-verb counts).</summary>
    public readonly List<Point2> Points = new(128);

    private float _minX, _minY, _maxX, _maxY;
    private bool _any;

    public void Clear()
    {
        Verbs.Clear();
        Points.Clear();
        _any = false;
        _minX = _minY = float.MaxValue;
        _maxX = _maxY = float.MinValue;
    }

    public void MoveTo(float x, float y)
    {
        Verbs.Add(PathVerb.MoveTo);
        Track(x, y);
    }

    public void LineTo(float x, float y)
    {
        Verbs.Add(PathVerb.LineTo);
        Track(x, y);
    }

    public void QuadTo(float x1, float y1, float x, float y)
    {
        Verbs.Add(PathVerb.QuadTo);
        Track(x1, y1);
        Track(x, y);
    }

    public void CubicTo(float x1, float y1, float x2, float y2, float x, float y)
    {
        Verbs.Add(PathVerb.CubicTo);
        Track(x1, y1);
        Track(x2, y2);
        Track(x, y);
    }

    /// <summary>Close the current subpath back to its <see cref="MoveTo"/> — consumes no point (see
    /// <see cref="PathVerb.Close"/>).</summary>
    public void Close() => Verbs.Add(PathVerb.Close);

    /// <summary>The bounding box of every point appended so far (control points included); <c>default</c> if none
    /// have been appended yet.</summary>
    public RectF Bounds => _any ? RectF.FromLTRB(_minX, _minY, _maxX, _maxY) : default;

    private void Track(float x, float y)
    {
        Points.Add(new Point2(x, y));
        if (!_any) { _minX = _maxX = x; _minY = _maxY = y; _any = true; return; }
        if (x < _minX) _minX = x; else if (x > _maxX) _maxX = x;
        if (y < _minY) _minY = y; else if (y > _maxY) _maxY = y;
    }

    /// <summary>Build the immutable <see cref="PathData"/> from the accumulated verbs/points. <paramref name="epoch"/>
    /// must be freshly minted for this content (<see cref="PathContentEpoch.Mint"/>) — or one already minted for this
    /// exact content, as <see cref="PathGeometryTable"/> threads through on a re-registration. Uses
    /// <see cref="CollectionsMarshal.AsSpan{T}"/> to hand the ctor a view of the lists' backing arrays with no extra
    /// copy — <see cref="PathData"/>'s own ctor still copies into its private storage, so this builder's lists remain
    /// safely reusable (<see cref="Clear"/>) for the next parse.</summary>
    public PathData Finish(PathContentEpoch epoch, FillRule rule)
        => new(epoch, CollectionsMarshal.AsSpan(Verbs), CollectionsMarshal.AsSpan(Points), rule, Bounds);
}

/// <summary>
/// Verb-preserving SVG path-data parser (gpu-renderer.md §5): the same command-dispatch loop, number tokenizer, and
/// clamp-not-crash posture as <see cref="IconPathParser"/>, but it emits <see cref="PathVerb"/>/<see cref="Point2"/>
/// pairs into a <see cref="PathBuilder"/> instead of flattening curves to line segments — flattening happens later,
/// once, in the tessellator, keyed by device scale (gpu-renderer.md §5 step 1 / §5.1), not on every parse.
///
/// <para>Reuses <see cref="IconPathParser"/>'s tokenizer (<see cref="IconPathParser.ReadNum"/>,
/// <see cref="IconPathParser.ReadFlag"/>, <see cref="IconPathParser.IsCommand"/>) and its arc parameterization
/// (<see cref="IconPathParser.ArcToCubics"/>) rather than reimplementing either — the two parsers can never disagree
/// on what a number or an arc means.</para>
/// </summary>
public static class PathDataParser
{
    // UI-thread-only reusable scratch (mount/registration time) — the same discipline as IconGeometryTable's
    // ContourBuilder field, just without an owning instance to hang it off (this parser is a static entry point).
    private static readonly PathBuilder s_builder = new();

    /// <summary>Parse SVG path-data (<c>M/m L/l H/h V/v C/c S/s Q/q T/t A/a Z/z</c>, implicit command repetition,
    /// compressed number tokenization, arcs → cubic spans ≤90°) into an immutable <see cref="PathData"/>.
    /// <paramref name="epoch"/> must be freshly minted (<see cref="PathContentEpoch.Mint"/>) for this exact content.
    /// <paramref name="viewBoxW"/>/<paramref name="viewBoxH"/> are informational only — unlike
    /// <see cref="IconGeometryTable"/>'s icon masks, path geometry is NOT normalized to 0..1: it stays in the
    /// author's own coordinate space, which is what the tessellator and hit-test both need. Malformed input never
    /// throws (clamp-not-crash, validation.md): an unreadable number reads as 0 and the walk advances.</summary>
    public static PathData Parse(ReadOnlySpan<char> pathData, PathContentEpoch epoch,
        FillRule rule = FillRule.NonZero, float viewBoxW = 0f, float viewBoxH = 0f)
    {
        var b = s_builder;
        b.Clear();
        if (pathData.IsEmpty) return b.Finish(epoch, rule);

        ReadOnlySpan<char> s = pathData;
        int i = 0, n = s.Length;
        float cx = 0, cy = 0;            // current point
        float sx = 0, sy = 0;            // current subpath start (for Z)
        float lastCx = 0, lastCy = 0;    // last cubic control point (for S/s reflection)
        float lastQx = 0, lastQy = 0;    // last quadratic control point (for T/t reflection)
        char cmd = '\0';
        char prevCmd = '\0';
        Span<float> arcSpans = stackalloc float[24];

        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }

            if (IconPathParser.IsCommand(c)) { cmd = c; i++; }
            else if (cmd == '\0') { i++; continue; }   // leading garbage
            // else: an implicit repeat of the previous command (numbers with no command letter).

            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    float x = IconPathParser.ReadNum(s, ref i), y = IconPathParser.ReadNum(s, ref i);
                    if (rel) { x += cx; y += cy; }
                    cx = x; cy = y; sx = x; sy = y;
                    b.MoveTo(cx, cy);
                    cmd = rel ? 'l' : 'L';   // subsequent coord pairs are implicit LineTo (SVG rule)
                    break;
                }
                case 'L':
                {
                    float x = IconPathParser.ReadNum(s, ref i), y = IconPathParser.ReadNum(s, ref i);
                    if (rel) { x += cx; y += cy; }
                    cx = x; cy = y;
                    b.LineTo(cx, cy);
                    break;
                }
                case 'H':
                {
                    float x = IconPathParser.ReadNum(s, ref i);
                    if (rel) x += cx;
                    cx = x;
                    b.LineTo(cx, cy);
                    break;
                }
                case 'V':
                {
                    float y = IconPathParser.ReadNum(s, ref i);
                    if (rel) y += cy;
                    cy = y;
                    b.LineTo(cx, cy);
                    break;
                }
                case 'C':
                {
                    float x1 = IconPathParser.ReadNum(s, ref i), y1 = IconPathParser.ReadNum(s, ref i);
                    float x2 = IconPathParser.ReadNum(s, ref i), y2 = IconPathParser.ReadNum(s, ref i);
                    float x = IconPathParser.ReadNum(s, ref i), y = IconPathParser.ReadNum(s, ref i);
                    if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                    b.CubicTo(x1, y1, x2, y2, x, y);
                    lastCx = x2; lastCy = y2; cx = x; cy = y;
                    break;
                }
                case 'S':
                {
                    // Smooth cubic: first control = reflection of the previous cubic's 2nd control about the current point.
                    float x1, y1;
                    if (prevCmd is 'C' or 'c' or 'S' or 's') { x1 = 2 * cx - lastCx; y1 = 2 * cy - lastCy; }
                    else { x1 = cx; y1 = cy; }
                    float x2 = IconPathParser.ReadNum(s, ref i), y2 = IconPathParser.ReadNum(s, ref i);
                    float x = IconPathParser.ReadNum(s, ref i), y = IconPathParser.ReadNum(s, ref i);
                    if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                    b.CubicTo(x1, y1, x2, y2, x, y);
                    lastCx = x2; lastCy = y2; cx = x; cy = y;
                    break;
                }
                case 'Q':
                {
                    float x1 = IconPathParser.ReadNum(s, ref i), y1 = IconPathParser.ReadNum(s, ref i);
                    float x = IconPathParser.ReadNum(s, ref i), y = IconPathParser.ReadNum(s, ref i);
                    if (rel) { x1 += cx; y1 += cy; x += cx; y += cy; }
                    b.QuadTo(x1, y1, x, y);
                    lastQx = x1; lastQy = y1; cx = x; cy = y;
                    break;
                }
                case 'T':
                {
                    float x1, y1;
                    if (prevCmd is 'Q' or 'q' or 'T' or 't') { x1 = 2 * cx - lastQx; y1 = 2 * cy - lastQy; }
                    else { x1 = cx; y1 = cy; }
                    float x = IconPathParser.ReadNum(s, ref i), y = IconPathParser.ReadNum(s, ref i);
                    if (rel) { x += cx; y += cy; }
                    b.QuadTo(x1, y1, x, y);
                    lastQx = x1; lastQy = y1; cx = x; cy = y;
                    break;
                }
                case 'A':
                {
                    float rx = IconPathParser.ReadNum(s, ref i), ry = IconPathParser.ReadNum(s, ref i);
                    float rot = IconPathParser.ReadNum(s, ref i);
                    int large = IconPathParser.ReadFlag(s, ref i), sweep = IconPathParser.ReadFlag(s, ref i);
                    float x = IconPathParser.ReadNum(s, ref i), y = IconPathParser.ReadNum(s, ref i);
                    if (rel) { x += cx; y += cy; }
                    int segs = IconPathParser.ArcToCubics(cx, cy, rx, ry, rot, large != 0, sweep != 0, x, y, arcSpans);
                    if (segs == 0)
                    {
                        b.LineTo(x, y);   // degenerate radius — same fallback as IconPathParser's FlattenArc
                    }
                    else
                    {
                        for (int seg = 0; seg < segs; seg++)
                        {
                            int o = seg * 6;
                            b.CubicTo(arcSpans[o], arcSpans[o + 1], arcSpans[o + 2], arcSpans[o + 3],
                                arcSpans[o + 4], arcSpans[o + 5]);
                        }
                    }
                    cx = x; cy = y;
                    break;
                }
                case 'Z':
                {
                    b.Close();
                    cx = sx; cy = sy;
                    break;
                }
                default:
                    i++;   // unknown command letter — skip
                    break;
            }
            prevCmd = cmd;
        }

        return b.Finish(epoch, rule);
    }
}
