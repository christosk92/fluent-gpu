using System.Globalization;
using System.Text;
using FluentGpu.Foundation;

namespace FluentGpu.Render.Tiles;

/// <summary>
/// The tile memory census (diagnostics): every resident tile surface by slice, and how much of each one its content
/// actually paints. The always-on <see cref="TileCensus"/> counts tiles and bytes; this says WHERE they are and whether
/// they hold anything — a static or effect slice requests every tile of its content's bounding box, so a tile the box
/// covers but no draw reaches still owns a surface (<c>emptyTiles</c>), and a tile its content only grazes holds a full
/// 2 MiB surface for a sliver (<c>painted%</c>). Owner thread (the render thread in async mode). Allocates: cold only.
/// </summary>
public sealed partial class SliceTable
{
    public string MemoryCensus(int windowW, int windowH)
    {
        const double MiB = 1024.0 * 1024.0;
        var perSlice = new Dictionary<int, (int Tiles, long Bytes, int Empty, long EmptyBytes, double Painted, double Area, int Visible, int Ahead, int Behind, int Stale)>();
        long total = 0, empty = 0; double painted = 0, area = 0;
        int tiles = 0, emptyTiles = 0, unknown = 0;
        for (int t = 0; t < _tiles.Length; t++)
        {
            if (!_used[t] || _tiles[t].Surface < 0) continue;
            ref readonly TileState ts = ref _tiles[t];
            int s = t / TileCap;
            long bytes = BytesOf(ts);
            double a = (double)ts.SurfW * ts.SurfH;
            int surf = ts.Surface;
            bool known = (uint)surf < (uint)_surfWantKey.Length && _surfWantKey[surf] != 0;
            RectF p = (uint)surf < (uint)_surfWantPaint.Length ? _surfWantPaint[surf] : default;
            int ops = (uint)surf < (uint)_surfWantOps.Length ? _surfWantOps[surf] : 0;
            double pa = 0;
            if (known && !p.IsEmpty)
            {
                float w = Math.Min(p.Right, ts.SurfW) - Math.Max(p.X, 0f), h = Math.Min(p.Bottom, ts.SurfH) - Math.Max(p.Y, 0f);
                pa = w > 0 && h > 0 ? (double)w * h : 0;
            }
            else if (!known) { pa = a; unknown++; }
            bool isEmpty = known && (ops == 0 || pa <= 0);
            tiles++; total += bytes; area += a; painted += pa;
            if (isEmpty) { emptyTiles++; empty += bytes; }
            var r = perSlice.GetValueOrDefault(s);
            r.Tiles++; r.Bytes += bytes; r.Area += a; r.Painted += pa;
            if (isEmpty) { r.Empty++; r.EmptyBytes += bytes; }
            if (ts.Order == 0) r.Visible++; else if (ts.Order == 1) r.Ahead++; else r.Behind++;
            if (ts.LastUsedFrame != _frame) r.Stale++;
            perSlice[s] = r;
        }
        int backedFree = 0;
        for (int surf = 0; surf < _surfBacked.Length; surf++)
            if (_surfBacked[surf] && _surfTile[surf] < 0) backedFree++;
        long windowBytes = (long)windowW * windowH * 4;
        var sb = new StringBuilder(2048);
        sb.Append("tile-census frame=").Append(_frame).Append(" window=").Append(windowW).Append('x').Append(windowH)
          .Append(" (").Append((windowBytes / MiB).ToString("0.0", CultureInfo.InvariantCulture)).Append(" MiB)")
          .Append(" resident=").Append(tiles).Append(" tiles ").Append((total / MiB).ToString("0.0", CultureInfo.InvariantCulture)).Append(" MiB")
          .Append(" = ").Append(windowBytes > 0 ? (total / (double)windowBytes).ToString("0.00", CultureInfo.InvariantCulture) : "-").Append("x window")
          .Append(" | empty ").Append(emptyTiles).Append(" tiles ").Append((empty / MiB).ToString("0.0", CultureInfo.InvariantCulture)).Append(" MiB")
          .Append(" | painted ").Append(area > 0 ? (painted * 100 / area).ToString("0", CultureInfo.InvariantCulture) : "-").Append("% of resident px")
          .Append(" | want-unknown ").Append(unknown)
          .Append(" | free-but-backed slots ").Append(backedFree).AppendLine();
        foreach (var kv in perSlice.OrderByDescending(k => k.Value.Bytes))
        {
            ref readonly SliceRow row = ref _rows[kv.Key];
            var v = kv.Value;
            sb.Append("  slice ").Append(row.Id).Append(' ').Append(row.Kind)
              .Append(" bounds=").Append(row.ContentBounds.X.ToString("0", CultureInfo.InvariantCulture)).Append(',').Append(row.ContentBounds.Y.ToString("0", CultureInfo.InvariantCulture))
              .Append(' ').Append(row.ContentBounds.W.ToString("0", CultureInfo.InvariantCulture)).Append('x').Append(row.ContentBounds.H.ToString("0", CultureInfo.InvariantCulture))
              .Append(" tiles=").Append(v.Tiles).Append(" (vis ").Append(v.Visible).Append(" ahead ").Append(v.Ahead).Append(" behind ").Append(v.Behind).Append(" stale ").Append(v.Stale).Append(')')
              .Append(' ').Append((v.Bytes / MiB).ToString("0.00", CultureInfo.InvariantCulture)).Append(" MiB")
              .Append(" empty=").Append(v.Empty).Append(' ').Append((v.EmptyBytes / MiB).ToString("0.00", CultureInfo.InvariantCulture)).Append(" MiB")
              .Append(" painted=").Append(v.Area > 0 ? (v.Painted * 100 / v.Area).ToString("0", CultureInfo.InvariantCulture) : "-").Append('%').AppendLine();
        }
        return sb.ToString();
    }
}
