using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;

using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

// PER-SEGMENT OP FOOTPRINTS for the tile replay (audit PERF r3 RASTER). A segment rastered into several tiles was decoded
// whole for EVERY tile: each primitive read (and, under a sub-pixel phase, copied and translated) only for the decode-time
// cull to drop it when it misses the tile — a 24-tile page decoded ~12 000 ops for ~1 500 it drew. The footprint table
// lists, once per segment and its bytes (cached under the row's ContentKey), every primitive's conservative footprint —
// SliceOpBounds, the very box (rect through its transform, + its per-kind halo) the decode-time cull tests — so a tile's
// replay skips a primitive whose footprint misses the replay's cull rect without touching its payload.
// EQUIVALENCE: a skipped primitive is one the decoder would have culled. The decoder's Cull keeps an op iff its box meets
// the cull rect (RepaintCull.Keep, inclusive edges); the skip drops it only when its box — moved by the replay shift, the
// translation DrawOpTranslate applies to its transform — misses the cull rect by more than FootprintSlackDip, which is far
// above the float error of that translation (one add per coordinate). Every DecodeOne primitive case does nothing before
// its cull but read and advance, so skipping it leaves every list, run and counter as the cull would. Scope ops (clips,
// stencil clips, layers), SetBlend, markers and any op too large to be moved (decoded unmoved) are never skipped.
public sealed unsafe partial class D3D12Device
{
    /// <summary>One primitive of a segment: its byte offset in the segment's stream and its footprint (window DIP, halo
    /// included, before the replay shift).</summary>
    private struct OpFoot
    {
        public int Pos;
        public float L, T, R, B;
    }

    /// <summary>How far outside the cull rect a footprint must lie to be skipped (DIP): a margin over the float error of the
    /// replay shift (≈ 1e-4 DIP), so a skip is always a primitive the decode-time cull drops.</summary>
    private const float FootprintSlackDip = 1f / 64f;

    // per slice id: the table, its length and the ContentKey it was built for (0 = none)
    private OpFoot[]?[] _footOf = new OpFoot[16][];
    private int[] _footN = new int[16];
    private ulong[] _footKey = new ulong[16];
    private int[] _footTurn = new int[16];
    // the table the current tile replay skips by (empty = decode every op)
    private OpFoot[]? _replayFoot;
    private int _replayFootN;
    private long _rcFootSkippedTally;
    private int[] _rastersOfRow = new int[64];

    /// <summary>RasterTiles: the footprint table of <paramref name="row"/>'s stream, built when the row's bytes are new to it
    /// (a ContentKey of 0 is never cached: rebuilt per turn) and <paramref name="rasters"/> of its tiles raster this turn —
    /// a table costs one pass over the ops, as one replay does, so a single raster of new bytes decodes them directly (the
    /// lyrics rows: one tile of changed content per turn, nothing to skip). Null when the skip is off or not worth it.</summary>
    private OpFoot[]? FootprintsOf(in CompositeFrame frame, in SliceRow row, int rasters, out int count)
    {
        count = 0;
        if (!TileDamage.OpFootprints || row.Id < 0) return null;
        int id = row.Id;
        if (_footOf.Length <= id)
        {
            int n = Math.Max(id + 1, _footOf.Length * 2);
            Array.Resize(ref _footOf, n); Array.Resize(ref _footN, n); Array.Resize(ref _footKey, n); Array.Resize(ref _footTurn, n);
        }
        bool fresh = row.ContentKey != 0 ? _footKey[id] == row.ContentKey : _footTurn[id] == _compositeTurn;
        if ((!fresh || _footOf[id] is null) && rasters < 2) return null;
        if (!fresh || _footOf[id] is null)
        {
            BuildFootprints(frame.StreamOf(in row), ref _footOf[id], out _footN[id]);
            _footKey[id] = row.ContentKey;
            _footTurn[id] = _compositeTurn;
        }
        count = _footN[id];
        return _footOf[id];
    }

    private void BuildFootprints(ReadOnlySpan<byte> stream, ref OpFoot[]? table, out int count)
    {
        table ??= new OpFoot[1024];   // one segment's ops: sized once for a page-sized segment, doubled past it
        int n = 0, pos = 0;
        while (pos + sizeof(int) <= stream.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(stream[pos..]);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > stream.Length) break;
            if (Skippable(op) && body <= _replayOp.Length - sizeof(int)
                && SliceOpBounds.TryGet(op, stream.Slice(pos + sizeof(int), body), out RectF b))
            {
                if (n == table.Length) Array.Resize(ref table, n * 2);
                table[n++] = new OpFoot { Pos = pos, L = b.X, T = b.Y, R = b.Right, B = b.Bottom };
            }
            pos += sizeof(int) + body;
        }
        count = n;
    }

    /// <summary>A primitive the tile replay hands to DecodeOne (not a scope op, a blend or a marker).</summary>
    private static bool Skippable(DrawOp op) => op switch
    {
        DrawOp.PushClip or DrawOp.PopClip or DrawOp.PushStencilClip or DrawOp.PopStencilClip or DrawOp.PushLayer
            or DrawOp.PopLayer or DrawOp.CompositeSlice or DrawOp.SetBlend => false,
        _ => true,
    };

    /// <summary>ReplayStream: the primitive at <paramref name="pos"/> provably misses the cull rect (advances the table
    /// cursor <paramref name="k"/>, which only moves forward with <paramref name="pos"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool FootprintMisses(int pos, ref int k, float sx, float sy)
    {
        if (!_cullActive) return false;
        OpFoot[] t = _replayFoot!;
        int n = _replayFootN;
        while (k < n && t[k].Pos < pos) k++;
        if (k >= n || t[k].Pos != pos) return false;
        ref readonly OpFoot f = ref t[k];
        RectF c = _cullRect;
        const float e = FootprintSlackDip;
        return f.L + sx > c.Right + e || f.R + sx < c.X - e || f.T + sy > c.Bottom + e || f.B + sy < c.Y - e;
    }
}
