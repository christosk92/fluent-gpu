using System.Globalization;

namespace FluentGpu.Render;

/// <summary>
/// Accumulates flattened polygon contours (all curves subdivided to line segments) for a parsed SVG path. Points are in
/// the path's own coordinate units (the icon view-box) — <see cref="IconGeometryTable"/> normalizes them to 0..1 at
/// register time. One builder is reused per <see cref="IconPathParser.Parse"/> call (no per-icon allocation churn once
/// its lists reach capacity). NOT thread-safe: interning is UI-thread-only.
/// </summary>
public sealed class ContourBuilder
{
    /// <summary>Flat point coordinates for EVERY contour, concatenated (x,y interleaved).</summary>
    public readonly List<float> Coords = new(256);
    /// <summary>Start index into <see cref="Coords"/> (in POINTS, i.e. ×2 for the float offset) of each contour.</summary>
    public readonly List<int> ContourStart = new(16);
    /// <summary>Point count of each contour (parallel to <see cref="ContourStart"/>).</summary>
    public readonly List<int> ContourCount = new(16);

    private int _curStart = -1;   // point index of the current subpath's first point
    private bool _open;

    public void Clear()
    {
        Coords.Clear(); ContourStart.Clear(); ContourCount.Clear();
        _curStart = -1; _open = false;
    }

    public void MoveTo(float x, float y)
    {
        EndContour();
        _curStart = Coords.Count / 2;
        _open = true;
        Coords.Add(x); Coords.Add(y);
    }

    public void LineTo(float x, float y)
    {
        if (!_open) { MoveTo(x, y); return; }
        // Drop a zero-length segment (compressed exports emit them; they add scanline noise).
        int n = Coords.Count;
        if (n >= 2 && Coords[n - 2] == x && Coords[n - 1] == y) return;
        Coords.Add(x); Coords.Add(y);
    }

    /// <summary>Close the current subpath. Fill contours implicitly close (the rasterizer wraps last→first), so this
    /// just terminates the contour — but it lets a following command open a fresh subpath.</summary>
    public void Close() => EndContour();

    private void EndContour()
    {
        if (_open && _curStart >= 0)
        {
            int count = (Coords.Count / 2) - _curStart;
            if (count >= 3) { ContourStart.Add(_curStart); ContourCount.Add(count); }
            else { Coords.RemoveRange(_curStart * 2, Coords.Count - _curStart * 2); }   // degenerate — drop it
        }
        _open = false; _curStart = -1;
    }

    /// <summary>Terminate the last open contour (call once after parsing).</summary>
    public void Finish() => EndContour();
}

/// <summary>
/// Full SVG path-data parser (M/m L/l H/h V/v C/c S/s Q/q T/t A/a Z/z, implicit command repetition, compressed number
/// tokenization, arcs → cubics). Emits flattened contours into a <see cref="ContourBuilder"/>.
///
/// <para>The number tokenizer is ported from the MIT-licensed fuzz-hardened parser in
/// <c>microsoft-ui-reactor Reactor/Charting/PathDataParser.cs</c>, then hardened for the Files-app export syntax:
/// a single decimal point per number (so <c>.2.51</c> tokenizes as two numbers, not one), a sign that is NOT preceded
/// by <c>e</c> terminates a number (so <c>-.3.28-.78</c> splits correctly), and arc flag args read a single 0/1 char.
/// Malformed input never throws — an unreadable number returns 0 and the walk advances (clamp-not-crash, validation.md).</para>
/// </summary>
public static class IconPathParser
{
    // Curve flatness in SQUARED path units (AGG distance-tolerance): a segment is "flat enough" when the summed
    // control-point deviation squared falls under this × the chord length squared. 0.03 ≈ 0.17 path-unit tolerance
    // (icons are authored at a 16-unit view-box → sub-pixel at 16 px, ~0.35 px at 32 px). Recursion is depth-capped.
    private const float FlatnessSq = 0.03f;
    private const int MaxDepth = 18;

    public static void Parse(string? pathData, ContourBuilder b)
    {
        b.Clear();
        if (string.IsNullOrWhiteSpace(pathData)) { b.Finish(); return; }

        string s = pathData!;
        int i = 0, n = s.Length;
        float cx = 0, cy = 0;       // current point
        float sx = 0, sy = 0;       // current subpath start (for Z)
        float lastCx = 0, lastCy = 0;   // last cubic control point (for S/s reflection)
        float lastQx = 0, lastQy = 0;   // last quadratic control point (for T/t reflection)
        char cmd = '\0';
        char prevCmd = '\0';

        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }

            if (IsCommand(c)) { cmd = c; i++; }
            else if (cmd == '\0') { i++; continue; }   // leading garbage
            // else: an implicit repeat of the previous command (numbers with no command letter).

            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    float x = ReadNum(s, ref i), y = ReadNum(s, ref i);
                    if (rel) { x += cx; y += cy; }
                    cx = x; cy = y; sx = x; sy = y;
                    b.MoveTo(cx, cy);
                    cmd = rel ? 'l' : 'L';   // subsequent coord pairs are implicit LineTo (SVG rule)
                    break;
                }
                case 'L':
                {
                    float x = ReadNum(s, ref i), y = ReadNum(s, ref i);
                    if (rel) { x += cx; y += cy; }
                    cx = x; cy = y;
                    b.LineTo(cx, cy);
                    break;
                }
                case 'H':
                {
                    float x = ReadNum(s, ref i);
                    if (rel) x += cx;
                    cx = x;
                    b.LineTo(cx, cy);
                    break;
                }
                case 'V':
                {
                    float y = ReadNum(s, ref i);
                    if (rel) y += cy;
                    cy = y;
                    b.LineTo(cx, cy);
                    break;
                }
                case 'C':
                {
                    float x1 = ReadNum(s, ref i), y1 = ReadNum(s, ref i);
                    float x2 = ReadNum(s, ref i), y2 = ReadNum(s, ref i);
                    float x = ReadNum(s, ref i), y = ReadNum(s, ref i);
                    if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                    FlattenCubic(b, cx, cy, x1, y1, x2, y2, x, y, 0);
                    lastCx = x2; lastCy = y2; cx = x; cy = y;
                    break;
                }
                case 'S':
                {
                    // Smooth cubic: first control = reflection of the previous cubic's 2nd control about the current point.
                    float x1, y1;
                    if (prevCmd is 'C' or 'c' or 'S' or 's') { x1 = 2 * cx - lastCx; y1 = 2 * cy - lastCy; }
                    else { x1 = cx; y1 = cy; }
                    float x2 = ReadNum(s, ref i), y2 = ReadNum(s, ref i);
                    float x = ReadNum(s, ref i), y = ReadNum(s, ref i);
                    if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                    FlattenCubic(b, cx, cy, x1, y1, x2, y2, x, y, 0);
                    lastCx = x2; lastCy = y2; cx = x; cy = y;
                    break;
                }
                case 'Q':
                {
                    float x1 = ReadNum(s, ref i), y1 = ReadNum(s, ref i);
                    float x = ReadNum(s, ref i), y = ReadNum(s, ref i);
                    if (rel) { x1 += cx; y1 += cy; x += cx; y += cy; }
                    FlattenQuad(b, cx, cy, x1, y1, x, y, 0);
                    lastQx = x1; lastQy = y1; cx = x; cy = y;
                    break;
                }
                case 'T':
                {
                    float x1, y1;
                    if (prevCmd is 'Q' or 'q' or 'T' or 't') { x1 = 2 * cx - lastQx; y1 = 2 * cy - lastQy; }
                    else { x1 = cx; y1 = cy; }
                    float x = ReadNum(s, ref i), y = ReadNum(s, ref i);
                    if (rel) { x += cx; y += cy; }
                    FlattenQuad(b, cx, cy, x1, y1, x, y, 0);
                    lastQx = x1; lastQy = y1; cx = x; cy = y;
                    break;
                }
                case 'A':
                {
                    float rx = ReadNum(s, ref i), ry = ReadNum(s, ref i), rot = ReadNum(s, ref i);
                    int large = ReadFlag(s, ref i), sweep = ReadFlag(s, ref i);
                    float x = ReadNum(s, ref i), y = ReadNum(s, ref i);
                    if (rel) { x += cx; y += cy; }
                    FlattenArc(b, cx, cy, rx, ry, rot, large != 0, sweep != 0, x, y);
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
        b.Finish();
    }

    private static bool IsCommand(char c)
        => c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v'
            or 'C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z';

    /// <summary>Read one number, tolerating the compressed export syntax (single decimal point per number; a sign not
    /// preceded by <c>e</c> ends the number). Returns 0 on a malformed token and leaves the cursor past it.</summary>
    private static float ReadNum(string s, ref int i)
    {
        int n = s.Length;
        while (i < n && (s[i] == ' ' || s[i] == ',' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        int start = i;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        bool dot = false;
        while (i < n)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') { i++; continue; }
            if (c == '.') { if (dot) break; dot = true; i++; continue; }
            if (c == 'e' || c == 'E')
            {
                i++;
                if (i < n && (s[i] == '+' || s[i] == '-')) i++;
                while (i < n && s[i] >= '0' && s[i] <= '9') i++;
                break;
            }
            break;
        }
        int len = i - start;
        if (len <= 0) { if (i == start && i < n) i++; return 0f; }   // never stall
        return float.TryParse(s.AsSpan(start, len), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }

    /// <summary>Read a single arc flag (0/1). SVG permits flags with no separator (e.g. <c>a5 5 0 015 5</c>), so a flag
    /// is exactly one character.</summary>
    private static int ReadFlag(string s, ref int i)
    {
        int n = s.Length;
        while (i < n && (s[i] == ' ' || s[i] == ',' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        if (i < n && (s[i] == '0' || s[i] == '1')) { int v = s[i] - '0'; i++; return v; }
        // Fall back to a full number read for oddly-formatted input.
        return ReadNum(s, ref i) != 0f ? 1 : 0;
    }

    private static void FlattenCubic(ContourBuilder b, float x0, float y0, float x1, float y1,
        float x2, float y2, float x3, float y3, int depth)
    {
        float dx = x3 - x0, dy = y3 - y0;
        float d1 = MathF.Abs((x1 - x3) * dy - (y1 - y3) * dx);
        float d2 = MathF.Abs((x2 - x3) * dy - (y2 - y3) * dx);
        if (depth >= MaxDepth || (d1 + d2) * (d1 + d2) <= FlatnessSq * (dx * dx + dy * dy))
        {
            b.LineTo(x3, y3);
            return;
        }
        float x01 = (x0 + x1) * 0.5f, y01 = (y0 + y1) * 0.5f;
        float x12 = (x1 + x2) * 0.5f, y12 = (y1 + y2) * 0.5f;
        float x23 = (x2 + x3) * 0.5f, y23 = (y2 + y3) * 0.5f;
        float xa = (x01 + x12) * 0.5f, ya = (y01 + y12) * 0.5f;
        float xb = (x12 + x23) * 0.5f, yb = (y12 + y23) * 0.5f;
        float xm = (xa + xb) * 0.5f, ym = (ya + yb) * 0.5f;
        FlattenCubic(b, x0, y0, x01, y01, xa, ya, xm, ym, depth + 1);
        FlattenCubic(b, xm, ym, xb, yb, x23, y23, x3, y3, depth + 1);
    }

    private static void FlattenQuad(ContourBuilder b, float x0, float y0, float x1, float y1,
        float x2, float y2, int depth)
    {
        float dx = x2 - x0, dy = y2 - y0;
        float d = MathF.Abs((x1 - x2) * dy - (y1 - y2) * dx);
        if (depth >= MaxDepth || d * d <= FlatnessSq * (dx * dx + dy * dy))
        {
            b.LineTo(x2, y2);
            return;
        }
        float x01 = (x0 + x1) * 0.5f, y01 = (y0 + y1) * 0.5f;
        float x12 = (x1 + x2) * 0.5f, y12 = (y1 + y2) * 0.5f;
        float xm = (x01 + x12) * 0.5f, ym = (y01 + y12) * 0.5f;
        FlattenQuad(b, x0, y0, x01, y01, xm, ym, depth + 1);
        FlattenQuad(b, xm, ym, x12, y12, x2, y2, depth + 1);
    }

    // SVG endpoint-parameterization arc → a sequence of cubic Béziers (≤90° each), then flattened. (SVG impl notes F.6.)
    private static void FlattenArc(ContourBuilder b, float x1, float y1, float rx, float ry, float phiDeg,
        bool largeArc, bool sweep, float x2, float y2)
    {
        if (rx == 0f || ry == 0f) { b.LineTo(x2, y2); return; }
        rx = MathF.Abs(rx); ry = MathF.Abs(ry);
        double phi = phiDeg * Math.PI / 180.0;
        double cosP = Math.Cos(phi), sinP = Math.Sin(phi);

        double dx2 = (x1 - x2) / 2.0, dy2 = (y1 - y2) / 2.0;
        double x1p = cosP * dx2 + sinP * dy2;
        double y1p = -sinP * dx2 + cosP * dy2;

        // Correct out-of-range radii (SVG F.6.6).
        double lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
        if (lambda > 1.0) { double s = Math.Sqrt(lambda); rx = (float)(rx * s); ry = (float)(ry * s); }

        double rx2 = (double)rx * rx, ry2 = (double)ry * ry;
        double num = rx2 * ry2 - rx2 * y1p * y1p - ry2 * x1p * x1p;
        double den = rx2 * y1p * y1p + ry2 * x1p * x1p;
        double co = den <= 0 ? 0 : Math.Sqrt(Math.Max(0.0, num / den));
        if (largeArc == sweep) co = -co;
        double cxp = co * (rx * y1p) / ry;
        double cyp = co * -(ry * x1p) / rx;
        double cx = cosP * cxp - sinP * cyp + (x1 + x2) / 2.0;
        double cy = sinP * cxp + cosP * cyp + (y1 + y2) / 2.0;

        double startAng = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        int segs = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2.0));
        if (segs < 1) segs = 1;
        double segAng = delta / segs;
        double t = 4.0 / 3.0 * Math.Tan(segAng / 4.0);

        double a = startAng;
        double px = x1, py = y1;
        for (int seg = 0; seg < segs; seg++)
        {
            double a2 = a + segAng;
            double cosA = Math.Cos(a), sinA = Math.Sin(a);
            double cosA2 = Math.Cos(a2), sinA2 = Math.Sin(a2);

            // Ellipse point + tangent, rotated by phi and translated to center.
            double e1x = cx + (cosP * rx * cosA - sinP * ry * sinA);
            double e1y = cy + (sinP * rx * cosA + cosP * ry * sinA);
            double e2x = cx + (cosP * rx * cosA2 - sinP * ry * sinA2);
            double e2y = cy + (sinP * rx * cosA2 + cosP * ry * sinA2);

            double d1x = -cosP * rx * sinA - sinP * ry * cosA;
            double d1y = -sinP * rx * sinA + cosP * ry * cosA;
            double d2x = -cosP * rx * sinA2 - sinP * ry * cosA2;
            double d2y = -sinP * rx * sinA2 + cosP * ry * cosA2;

            float c1x = (float)(px + t * d1x);   // c1 = e1 + t·tangent(e1)
            float c1y = (float)(py + t * d1y);
            float c2x = (float)(e2x - t * d2x);
            float c2y = (float)(e2y - t * d2y);
            FlattenCubic(b, (float)px, (float)py, c1x, c1y, c2x, c2y, (float)e2x, (float)e2y, 0);
            px = e2x; py = e2y;
            a = a2;
        }
    }

    private static double Angle(double ux, double uy, double vx, double vy)
    {
        double dot = ux * vx + uy * vy;
        double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double ang = Math.Acos(Math.Clamp(len == 0 ? 1 : dot / len, -1.0, 1.0));
        if (ux * vy - uy * vx < 0) ang = -ang;
        return ang;
    }
}
