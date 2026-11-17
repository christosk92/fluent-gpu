using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>What one entry of a pack is (see <see cref="PackLayout"/>).</summary>
public enum PackEntryKind : byte
{
    /// <summary>A tile: <see cref="PackEntry.Cols"/> x <see cref="PackEntry.Rows"/> is its size class's smallest box, in cells.</summary>
    Tile,
    /// <summary>The whole row (a <c>full</c> item): <see cref="PackEntry.Extent"/> DIP tall when it is above 0, else
    /// <see cref="PackEntry.Rows"/> rows tall.</summary>
    Band,
    /// <summary>The whole row, <see cref="PackEntry.Extent"/> DIP tall: chrome inside a group (an add row, an empty-state row).</summary>
    Row,
    /// <summary>The whole row, <see cref="PackEntry.Extent"/> DIP tall, and the start of a group (Extent 0: an untitled break).</summary>
    GroupHead,
}

[Flags]
public enum PackFlags : byte
{
    None = 0,
    /// <summary>The theme's default size: grows first in the fill pass.</summary>
    Auto = 1,
    /// <summary>Open in place: at least <see cref="PackEntry.Extent"/> DIP tall and <see cref="PackEntry.OpenMinCols"/> wide.</summary>
    Open = 2,
    /// <summary>Keeps its place in the order, takes no room, is never realized.</summary>
    Hidden = 4,
    /// <summary>Its <see cref="PackEntry.Extent"/> follows a body that eases its own height (a measured full row whose body reflows when its
    /// rows change): a change of that extent alone re-packs at once, every tile following the body frame by frame, never a re-flow of its own
    /// over the body's (which would trail it, the body's content drawn past its box into the next tile).</summary>
    Follows = 8,
}

/// <summary>One entry of a pack, in order. Order is the truth: the packer never places an entry ahead of an earlier one.</summary>
public readonly record struct PackEntry(PackEntryKind Kind, byte Cols = 1, byte Rows = 1,
    PackFlags Flags = PackFlags.None, float Extent = 0f, byte OpenMinCols = 0);

/// <summary>The grid a pack is cut into. <see cref="Unit"/> &lt;= 0: square cells (the row unit is the column width; Zune).
/// <see cref="ToolStrip"/> &gt; 0: a strip of that height above every block of <see cref="ToolStripMinRows"/> rows or more
/// (Classic's Edit Home chips); it adds to the band's height.</summary>
public readonly record struct PackMetrics(int Columns, float Unit, float Gap, float GroupGap,
    float ToolStrip = 0f, int ToolStripMinRows = 2);

/// <summary>The result of <see cref="PackLayout.Pack"/>: grow-only tables, one instance reused per grid (no allocation once
/// it has grown to the largest pack it held). All rects are in content space (the pack's own top-left is 0,0).</summary>
public sealed class PackResult
{
    internal RectF[] _rects = [];
    internal byte[] _spanCols = [];
    internal float[] _spanRows = [];
    internal float[] _strip = [];
    internal int[] _bandOf = [];
    internal int[] _groupOf = [];
    internal int[] _bandFirst = [];
    internal float[] _bandTop = [];
    internal float[] _bandBottom = [];
    // scratch for the packer (per entry): the box in cells before the fill pass, and the block table of one band
    internal int[] _w = [], _h = [];
    internal int[] _blkStart = [], _blkCount = [], _blkW = [];
    internal bool[] _blkChosen = [];

    public int Count { get; internal set; }
    public int Columns { get; internal set; }
    public float Width { get; internal set; }
    public float Height { get; internal set; }
    public int BandCount { get; internal set; }
    /// <summary>The column width the pack was cut with.</summary>
    public float CellWidth { get; internal set; }
    /// <summary>The row unit the pack was cut with (the column width for square cells).</summary>
    public float Unit { get; internal set; }
    public float Gap { get; internal set; }

    /// <summary>Content space, per entry (a Hidden entry: an empty rect).</summary>
    public ReadOnlySpan<RectF> Rects => _rects.AsSpan(0, Count);
    /// <summary>Columns after the fill pass, per entry (whole-row kinds: the column count).</summary>
    public ReadOnlySpan<byte> SpanCols => _spanCols.AsSpan(0, Count);
    /// <summary>Rows, per entry: the box's height in row units without its tool strip; a fraction for a stack member.</summary>
    public ReadOnlySpan<float> SpanRows => _spanRows.AsSpan(0, Count);
    /// <summary>The tool strip the entry carries at its top (0: none).</summary>
    public ReadOnlySpan<float> ToolStripOf => _strip.AsSpan(0, Count);
    /// <summary>Entry to band; -1 for a Hidden entry.</summary>
    public ReadOnlySpan<int> BandOf => _bandOf.AsSpan(0, Count);
    /// <summary>Band to its first entry (monotonic).</summary>
    public ReadOnlySpan<int> BandFirst => _bandFirst.AsSpan(0, BandCount);
    public ReadOnlySpan<float> BandTop => _bandTop.AsSpan(0, BandCount);
    public ReadOnlySpan<float> BandBottom => _bandBottom.AsSpan(0, BandCount);
    /// <summary>Entry to its group ordinal (a group starts at each <see cref="PackEntryKind.GroupHead"/>).</summary>
    public ReadOnlySpan<int> GroupOf => _groupOf.AsSpan(0, Count);

    internal void Ensure(int n)
    {
        if (_rects.Length >= n) return;
        int c = Math.Max(n, Math.Max(16, _rects.Length * 2));
        Array.Resize(ref _rects, c); Array.Resize(ref _spanCols, c); Array.Resize(ref _spanRows, c);
        Array.Resize(ref _strip, c); Array.Resize(ref _bandOf, c); Array.Resize(ref _groupOf, c);
        Array.Resize(ref _bandFirst, c); Array.Resize(ref _bandTop, c); Array.Resize(ref _bandBottom, c);
        Array.Resize(ref _w, c); Array.Resize(ref _h, c);
        Array.Resize(ref _blkStart, c); Array.Resize(ref _blkCount, c); Array.Resize(ref _blkW, c); Array.Resize(ref _blkChosen, c);
    }

    /// <summary>Copies another result's tables (a grid keeps the previous pack to seed the rects of entries a re-pack moves).</summary>
    public void CopyFrom(PackResult o)
    {
        Ensure(Math.Max(o.Count, o.BandCount));
        o.Rects.CopyTo(_rects); o.SpanCols.CopyTo(_spanCols); o.SpanRows.CopyTo(_spanRows); o.ToolStripOf.CopyTo(_strip);
        o.BandOf.CopyTo(_bandOf); o.GroupOf.CopyTo(_groupOf);
        o.BandFirst.CopyTo(_bandFirst); o.BandTop.CopyTo(_bandTop); o.BandBottom.CopyTo(_bandBottom);
        Count = o.Count; Columns = o.Columns; Width = o.Width; Height = o.Height; BandCount = o.BandCount;
        CellWidth = o.CellWidth; Unit = o.Unit; Gap = o.Gap;
    }
}

public enum PackFaultKind : byte { None, EmptyCell, Overlap, RaggedBand, BelowMinSpan, OutOfOrder, OutOfGroup }

/// <summary>One way a pack breaks the bento (see <see cref="PackLayout.Verify"/>). <see cref="Where"/> is the offending area in
/// content space; <see cref="Entry"/> the entry nearest to it (-1 when none).</summary>
public readonly record struct PackFault(PackFaultKind Kind, int Group, int Band, int Entry, RectF Where);

/// <summary>
/// The bento packer (spec 4.4, the concept's <c>packGroup</c>): order is the truth and nothing is back-filled. A group is cut
/// into bands in reading order; a band takes the next tiles that fit across the columns and is as tall as the tallest of them;
/// a run of equal tiles wraps evenly (five in four columns is three and two); shorter tiles of one size stack in columns beside
/// a tall one and share the band's height; the columns left over go one at a time to the narrowest block (a block whose size
/// the user chose waits half a column longer than a theme default). So no group has an empty cell.
///
/// Every placed vertical slice (a group head, a row, a full band, a band of tiles) is one band of the result, so the band
/// tables are a monotonic partition of the pack's height that windowing and hit-testing search. Pure and allocation-free once
/// the <see cref="PackResult"/> has grown. The result depends on entries, metrics and width only.
/// </summary>
public static class PackLayout
{
    const float Eps = 0.5f;

    /// <summary>Packs <paramref name="entries"/> at <paramref name="width"/> into <paramref name="into"/>. O(n).</summary>
    public static void Pack(ReadOnlySpan<PackEntry> entries, in PackMetrics metrics, float width, PackResult into)
    {
        int n = entries.Length;
        into.Ensure(Math.Max(n, 1));
        int cols = Math.Max(1, metrics.Columns);
        float gap = MathF.Max(0f, metrics.Gap);
        width = MathF.Max(0f, width);
        float cw = MathF.Max(0f, (width - (cols - 1) * gap) / cols);
        float unit = metrics.Unit > 0f ? metrics.Unit : cw;
        into.Count = n; into.Columns = cols; into.Width = width; into.CellWidth = cw; into.Unit = unit; into.Gap = gap;
        into.BandCount = 0;

        var rects = into._rects; var spanCols = into._spanCols; var spanRows = into._spanRows; var strip = into._strip;
        var bandOf = into._bandOf; var groupOf = into._groupOf; var w = into._w; var h = into._h;

        // group ordinals and the per-entry cell box (clamped, with an open tile's growth)
        int g = 0;
        for (int i = 0; i < n; i++)
        {
            var e = entries[i];
            if (e.Kind == PackEntryKind.GroupHead && i > 0) g++;
            groupOf[i] = g;
            rects[i] = default; spanCols[i] = 0; spanRows[i] = 0f; strip[i] = 0f; bandOf[i] = -1;
            int cw0 = Math.Max(1, (int)e.Cols), rh = Math.Max(1, (int)e.Rows);
            if ((e.Flags & PackFlags.Open) != 0)
            {
                if (e.Extent > 0f) rh = Math.Max(rh, (int)MathF.Ceiling((e.Extent + gap) / (unit + gap) - 1e-4f));
                cw0 = Math.Max(cw0, Math.Min(cols, (int)e.OpenMinCols));
            }
            w[i] = Math.Min(cw0, cols); h[i] = rh;
        }

        float y = 0f;
        bool anyShown = false;     // a group gap goes above every shown head but the first
        bool needGap = false;      // something was placed in this group: the next slice keeps a gap from it
        int i0 = 0;
        while (i0 < n)
        {
            var e = entries[i0];
            if ((e.Flags & PackFlags.Hidden) != 0) { i0++; continue; }
            switch (e.Kind)
            {
                case PackEntryKind.GroupHead:
                {
                    bool hasEntries = false;
                    for (int j = i0 + 1; j < n && entries[j].Kind != PackEntryKind.GroupHead; j++)
                        if ((entries[j].Flags & PackFlags.Hidden) == 0) { hasEntries = true; break; }
                    float hh = MathF.Max(0f, e.Extent);
                    bool shown = hh > 0f || hasEntries;
                    if (shown && anyShown) y += MathF.Max(0f, metrics.GroupGap);
                    if (shown) anyShown = true;
                    rects[i0] = new RectF(0f, y, width, hh);
                    spanCols[i0] = (byte)cols; spanRows[i0] = 0f;
                    AddBand(into, i0, y, y + hh);
                    y += hh;
                    needGap = false;
                    i0++;
                    break;
                }
                case PackEntryKind.Row:
                {
                    if (needGap) y += gap;
                    anyShown = true;
                    float hh = MathF.Max(0f, e.Extent);
                    rects[i0] = new RectF(0f, y, width, hh);
                    spanCols[i0] = (byte)cols; spanRows[i0] = (hh + gap) / (unit + gap);
                    AddBand(into, i0, y, y + hh);
                    y += hh; needGap = true; i0++;
                    break;
                }
                case PackEntryKind.Band:
                {
                    if (needGap) y += gap;
                    anyShown = true;
                    float body = e.Extent > 0f ? e.Extent : h[i0] * unit + (h[i0] - 1) * gap;
                    float rowsEq = (body + gap) / (unit + gap);
                    float tall = metrics.ToolStrip > 0f && rowsEq >= metrics.ToolStripMinRows - 1e-3f ? metrics.ToolStrip : 0f;
                    rects[i0] = new RectF(0f, y, width, body + tall);
                    spanCols[i0] = (byte)cols; spanRows[i0] = rowsEq; strip[i0] = tall;
                    AddBand(into, i0, y, y + body + tall);
                    y += body + tall; needGap = true; i0++;
                    break;
                }
                default:
                {
                    // a run of tiles up to the next non-tile (hidden tiles inside the run are skipped in place)
                    int end = i0;
                    while (end < n && (entries[end].Kind == PackEntryKind.Tile || (entries[end].Flags & PackFlags.Hidden) != 0)
                           && entries[end].Kind != PackEntryKind.GroupHead) end++;
                    if (needGap) y += gap;
                    anyShown = true;
                    y = PackTiles(entries, into, i0, end, cols, cw, unit, gap, metrics, y);
                    needGap = true;
                    i0 = end;
                    break;
                }
            }
        }
        into.Height = y;
    }

    static void AddBand(PackResult r, int first, float top, float bottom)
    {
        int b = r.BandCount++;
        r._bandFirst[b] = first; r._bandTop[b] = top; r._bandBottom[b] = bottom;
        r._bandOf[first] = b;
    }

    static int NextVisible(ReadOnlySpan<PackEntry> entries, int i, int end)
    {
        while (i < end && (entries[i].Flags & PackFlags.Hidden) != 0) i++;
        return i;
    }

    // equal tiles from i (same cell box), counting visible entries only
    static int RunFrom(ReadOnlySpan<PackEntry> entries, int[] w, int[] h, int i, int end)
    {
        int r = 0, wi = w[i], hi = h[i];
        for (int k = i; k < end; k++)
        {
            if ((entries[k].Flags & PackFlags.Hidden) != 0) continue;
            if (w[k] != wi || h[k] != hi) break;
            r++;
        }
        return r;
    }

    // the index after `count` visible entries from i
    static int Advance(ReadOnlySpan<PackEntry> entries, int i, int count, int end)
    {
        while (i < end && count > 0) { if ((entries[i].Flags & PackFlags.Hidden) == 0) count--; i++; }
        return NextVisible(entries, i, end);
    }

    static float PackTiles(ReadOnlySpan<PackEntry> entries, PackResult r, int start, int end, int cols, float cw, float unit,
        float gap, in PackMetrics m, float y0)
    {
        var w = r._w; var h = r._h;
        var bs = r._blkStart; var bc = r._blkCount; var bw = r._blkW; var bch = r._blkChosen;
        float y = y0;
        bool any = false;
        int i = NextVisible(entries, start, end);
        while (i < end)
        {
            int nb = 0, used = 0;
            int H = h[i];
            // the band's height: the tallest of the tiles that fit across
            for (int j = i, u = 0; j < end; j++)
            {
                if ((entries[j].Flags & PackFlags.Hidden) != 0) continue;
                if (!(u + w[j] <= cols || j == i)) break;
                H = Math.Max(H, h[j]);
                u += w[j];
            }
            int take = int.MaxValue;   // a run of equal tiles wraps evenly: 5 in 4 columns is 3 + 2, never 4 + 1
            if (h[i] == H)
            {
                int L = RunFrom(entries, w, h, i, end), wi = w[i];
                int after = Advance(entries, i, L, end);
                if (L * wi > cols && (after >= end || h[after] > H))
                {
                    int bandsNeeded = (L * wi + cols - 1) / cols;
                    take = (L + bandsNeeded - 1) / bandsNeeded;
                }
            }
            int bandFirst = i;
            while (i < end && take > 0)
            {
                int xw = w[i], xh = h[i];
                if (xh > H || (nb > 0 && used + xw > cols)) break;
                if (xh == H)
                {
                    bs[nb] = i; bc[nb] = 1; bw[nb] = xw; bch[nb] = (entries[i].Flags & PackFlags.Auto) == 0; nb++;
                    i = Advance(entries, i, 1, end); take--; used += xw;
                    continue;
                }
                // shorter tiles stack in columns beside the tall ones; a few left over join the stacks rather than strand
                int cap = Math.Max(1, H / xh), R = RunFrom(entries, w, h, i, end), room = (cols - used) / xw;
                int need = (R + cap - 1) / cap;
                int nst = Math.Min(room, need);
                if (nst <= 0) break;
                int cnt = need <= room || R - room * cap <= room ? R : room * cap;
                for (int k = 0, left = cnt; k < nst; k++)
                {
                    int sz = (left + (nst - k) - 1) / (nst - k);
                    bool chosen = false;
                    for (int q = i, c = 0; c < sz && q < end; q++)
                    {
                        if ((entries[q].Flags & PackFlags.Hidden) != 0) continue;
                        if ((entries[q].Flags & PackFlags.Auto) == 0) chosen = true;
                        c++;
                    }
                    bs[nb] = i; bc[nb] = sz; bw[nb] = xw; bch[nb] = chosen; nb++;
                    i = Advance(entries, i, sz, end); left -= sz; used += xw;
                }
            }
            if (nb == 0)
            {
                // cannot happen with clamped spans; guard against a stall: the next tile forms a band alone
                bs[0] = i; bc[0] = 1; bw[0] = w[i]; bch[0] = true; nb = 1; used = w[i]; H = h[i];
                i = Advance(entries, i, 1, end);
            }
            // the fill pass: left-over columns one at a time to the narrowest block (a chosen size waits half a column)
            for (int extra = cols - used; extra > 0; extra--)
            {
                int best = 0; float bk = float.MaxValue;
                for (int b = 0; b < nb; b++)
                {
                    float k = bw[b] + (bch[b] ? 0.5f : 0f);
                    if (k < bk) { bk = k; best = b; }
                }
                bw[best]++;
            }
            if (any) y += gap;
            any = true;
            float tall = m.ToolStrip > 0f && H >= m.ToolStripMinRows ? m.ToolStrip : 0f;
            float hpx = H * unit + (H - 1) * gap + tall;
            int band = r.BandCount++;
            r._bandFirst[band] = bandFirst; r._bandTop[band] = y; r._bandBottom[band] = y + hpx;
            int col = 0;
            for (int b = 0; b < nb; b++)
            {
                int k = bc[b];
                float hh = (hpx - (k - 1) * gap) / k;
                float wpx = bw[b] * cw + (bw[b] - 1) * gap;
                float x = col * (cw + gap);
                int q = bs[b];
                for (int mi = 0; mi < k; mi++)
                {
                    q = NextVisible(entries, q, end);
                    float st = k == 1 ? tall : 0f;
                    r._rects[q] = new RectF(x, y + mi * (hh + gap), wpx, hh);
                    r._spanCols[q] = (byte)Math.Min(255, bw[b]);
                    r._spanRows[q] = (hh - st + gap) / (unit + gap);
                    r._strip[q] = st;
                    r._bandOf[q] = band;
                    q++;
                }
                col += bw[b];
            }
            y += hpx;
            i = NextVisible(entries, i, end);
        }
        return y;   // hidden tiles inside the run belong to no band
    }

    /// <summary>The bands that intersect [<paramref name="top"/>, <paramref name="bottom"/>): entries [first, last).
    /// Binary search; no allocation.</summary>
    public static void Window(PackResult r, float top, float bottom, out int first, out int last)
    {
        int bc = r.BandCount;
        if (bc == 0 || bottom <= top) { first = 0; last = 0; return; }
        var bt = r.BandTop; var bb = r.BandBottom; var bf = r.BandFirst;
        // first band whose bottom is past top
        int lo = 0, hi = bc;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (bb[mid] < top) lo = mid + 1; else hi = mid; }
        int b0 = lo;
        // last band whose top is before bottom
        lo = b0; hi = bc;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (bt[mid] < bottom) lo = mid + 1; else hi = mid; }
        int b1 = lo - 1;
        if (b0 >= bc || b1 < b0) { first = b0 >= bc ? r.Count : bf[b0]; last = first; return; }
        first = bf[b0];
        last = b1 + 1 < bc ? bf[b1 + 1] : r.Count;
    }

    /// <summary>The entry whose rect holds the point, or -1 (a gap, a group gap, outside the pack).</summary>
    public static int HitTest(PackResult r, float x, float y)
    {
        int bc = r.BandCount;
        if (bc == 0) return -1;
        var bt = r.BandTop; var bb = r.BandBottom;
        int lo = 0, hi = bc;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (bb[mid] <= y) lo = mid + 1; else hi = mid; }
        if (lo >= bc || bt[lo] > y) return -1;
        int first = r.BandFirst[lo], last = lo + 1 < bc ? r.BandFirst[lo + 1] : r.Count;
        var rects = r.Rects; var bandOf = r.BandOf;
        for (int i = first; i < last; i++)
        {
            if (bandOf[i] != lo) continue;
            var q = rects[i];
            if (x >= q.X && x < q.X + q.W && y >= q.Y && y < q.Y + q.H) return i;
        }
        return -1;
    }

    static bool IsTileLike(PackEntryKind k) => k is PackEntryKind.Tile or PackEntryKind.Band;
    static bool Ok(in PackEntry e, bool tilesOnly) => (e.Flags & PackFlags.Hidden) == 0 && (!tilesOnly || IsTileLike(e.Kind));

    /// <summary>The concept's <c>nearest</c>: the entry reached from <paramref name="from"/> by one step up or down (the
    /// entries above or below whose columns overlap first, then twice the horizontal distance). <paramref name="dx"/> = -1 / +1
    /// is simply the previous / next entry in order. <paramref name="tilesOnly"/> skips group heads and rows. -1 when none.</summary>
    public static int Nearest(PackResult r, ReadOnlySpan<PackEntry> entries, int from, int dx, int dy, bool tilesOnly)
    {
        int n = Math.Min(r.Count, entries.Length);
        if ((uint)from >= (uint)n) return -1;
        if (dy == 0)
        {
            if (dx == 0) return -1;
            for (int i = from + Math.Sign(dx); i >= 0 && i < n; i += Math.Sign(dx)) if (Ok(entries[i], tilesOnly)) return i;
            return -1;
        }
        var rects = r.Rects;
        var a = rects[from];
        float ax = a.X + a.W * 0.5f, ay = a.Y + a.H * 0.5f;
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (i == from || !Ok(entries[i], tilesOnly)) continue;
            var p = rects[i];
            if (p.H <= 0f) continue;
            if (dy < 0 ? p.Y + p.H > a.Y + 1f : p.Y < a.Y + a.H - 1f) continue;
            float overlap = MathF.Min(a.X + a.W, p.X + p.W) - MathF.Max(a.X, p.X);
            float dist = MathF.Abs(p.Y + p.H * 0.5f - ay) + (overlap > 0f ? 0f : MathF.Abs(p.X + p.W * 0.5f - ax)) * 2f;
            if (dist < bd) { bd = dist; best = i; }
        }
        return best;
    }

    /// <summary>Proof of the bento: in every band of tiles the column blocks start at the left edge, follow each other one gap
    /// apart and end at the right edge; every block spans the band's full height (its members one gap apart); nothing overlaps;
    /// no tile is narrower than its class (nor, standing alone, shorter); reading order equals entry order; every entry lies in
    /// its group's span. Whole-row kinds must be the full width. Returns the number of faults written (at most
    /// <paramref name="faults"/>' length; the count stops there).</summary>
    public static int Verify(ReadOnlySpan<PackEntry> entries, in PackMetrics metrics, PackResult r, Span<PackFault> faults)
    {
        int nf = 0;
        int n = Math.Min(entries.Length, r.Count);
        var rects = r.Rects; var bandOf = r.BandOf; var groupOf = r.GroupOf; var spanCols = r.SpanCols; var spanRows = r.SpanRows;
        float width = r.Width, gap = r.Gap;
        int cols = r.Columns;
        void Add(Span<PackFault> f, ref int k, PackFault x) { if (k < f.Length) f[k] = x; k++; }

        float prevBottom = float.NegativeInfinity;
        for (int b = 0; b < r.BandCount; b++)
        {
            float top = r.BandTop[b], bottom = r.BandBottom[b];
            int first = r.BandFirst[b], last = b + 1 < r.BandCount ? r.BandFirst[b + 1] : n;
            if (top < prevBottom - Eps)
                Add(faults, ref nf, new PackFault(PackFaultKind.Overlap, groupOf[first], b, first, new RectF(0f, top, width, prevBottom - top)));
            prevBottom = bottom;
            var k0 = entries[first].Kind;
            if (!IsTileLike(k0) || k0 == PackEntryKind.Band)
            {
                var q = rects[first];
                if (MathF.Abs(q.X) > Eps || MathF.Abs(q.W - width) > Eps)
                    Add(faults, ref nf, new PackFault(PackFaultKind.EmptyCell, groupOf[first], b, first, q));
                continue;
            }
            // tile band: walk the column blocks left to right (entries are in reading order)
            float expectX = 0f;
            float lastX = -1f, lastBottom = top - gap;
            int prevEntry = -1;
            for (int i = first; i < last; i++)
            {
                if (bandOf[i] != b) continue;
                var q = rects[i];
                var e = entries[i];
                if (spanCols[i] < Math.Min(cols, Math.Max(1, (int)e.Cols)))
                    Add(faults, ref nf, new PackFault(PackFaultKind.BelowMinSpan, groupOf[i], b, i, q));
                if (r.ToolStripOf[i] > 0f || MathF.Abs(q.H - (bottom - top)) <= Eps)
                {
                    // standing alone: never shorter than its class
                    if (spanRows[i] + 1e-3f < Math.Max(1, (int)e.Rows) && (e.Flags & PackFlags.Open) == 0)
                        Add(faults, ref nf, new PackFault(PackFaultKind.BelowMinSpan, groupOf[i], b, i, q));
                }
                if (prevEntry >= 0 && !(q.X > rects[prevEntry].X + Eps || (MathF.Abs(q.X - rects[prevEntry].X) <= Eps && q.Y > rects[prevEntry].Y + Eps)))
                    Add(faults, ref nf, new PackFault(PackFaultKind.OutOfOrder, groupOf[i], b, i, q));
                prevEntry = i;
                if (MathF.Abs(q.X - lastX) <= Eps)
                {
                    // the next member of the same stack
                    if (MathF.Abs(q.Y - (lastBottom + gap)) > Eps)
                        Add(faults, ref nf, new PackFault(q.Y < lastBottom + gap ? PackFaultKind.Overlap : PackFaultKind.RaggedBand, groupOf[i], b, i,
                            new RectF(q.X, lastBottom, q.W, MathF.Abs(q.Y - lastBottom))));
                }
                else
                {
                    // a new block: the previous block must have reached the band's bottom
                    if (lastX >= 0f && MathF.Abs(lastBottom - bottom) > Eps)
                        Add(faults, ref nf, new PackFault(PackFaultKind.RaggedBand, groupOf[i], b, prevEntry, new RectF(lastX, lastBottom, 1f, bottom - lastBottom)));
                    if (MathF.Abs(q.X - expectX) > Eps)
                        Add(faults, ref nf, new PackFault(q.X < expectX ? PackFaultKind.Overlap : PackFaultKind.EmptyCell, groupOf[i], b, i,
                            new RectF(MathF.Min(q.X, expectX), top, MathF.Abs(q.X - expectX), bottom - top)));
                    if (MathF.Abs(q.Y - top) > Eps)
                        Add(faults, ref nf, new PackFault(PackFaultKind.RaggedBand, groupOf[i], b, i, new RectF(q.X, top, q.W, q.Y - top)));
                    lastX = q.X;
                    expectX = q.X + q.W + gap;
                }
                lastBottom = q.Y + q.H;
            }
            if (lastX >= 0f)
            {
                if (MathF.Abs(lastBottom - bottom) > Eps)
                    Add(faults, ref nf, new PackFault(PackFaultKind.RaggedBand, groupOf[first], b, prevEntry, new RectF(lastX, lastBottom, 1f, bottom - lastBottom)));
                float right = expectX - gap;
                if (MathF.Abs(right - width) > Eps)
                    Add(faults, ref nf, new PackFault(right < width ? PackFaultKind.EmptyCell : PackFaultKind.Overlap, groupOf[first], b, prevEntry,
                        new RectF(MathF.Min(right, width), top, MathF.Abs(width - right), bottom - top)));
            }
        }
        // order and groups across bands: visible entries' bands never go backwards, and groups never interleave
        int lastBand = -1, lastGroup = 0;
        for (int i = 0; i < n; i++)
        {
            if ((entries[i].Flags & PackFlags.Hidden) != 0) continue;
            int b = bandOf[i];
            if (b < 0) { Add(faults, ref nf, new PackFault(PackFaultKind.OutOfOrder, groupOf[i], -1, i, default)); continue; }
            if (b < lastBand) Add(faults, ref nf, new PackFault(PackFaultKind.OutOfOrder, groupOf[i], b, i, rects[i]));
            if (groupOf[i] < lastGroup) Add(faults, ref nf, new PackFault(PackFaultKind.OutOfGroup, groupOf[i], b, i, rects[i]));
            lastBand = b; lastGroup = groupOf[i];
        }
        return nf;
    }
}
