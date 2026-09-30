using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;

namespace FluentGpu.Render;

// The evidence half of the slice recorder (docs/plans/evidence-diagnostics-implementation.md §A.1/§A.2/§A.4): the three
// ledgers — rasters, composite items + placements, walks. Enabled on the ONE recorder of a host that composites
// (EnableEvidence); every hook below is a cheap early-out on the other. The per-op content table and the per-tile wants
// they report are NOT evidence: they are the tile-validity mechanism itself (SliceRecorder.Content.cs), always on.
public sealed partial class SliceRecorder
{
    private bool _ev;
    private RasterLedger? _evRasters;
    private CompositeLedger? _evComposites;
    private WalkLedger? _evWalks;

    /// <summary>The raster ledger (null until <see cref="EnableEvidence"/>).</summary>
    public RasterLedger? RasterLedger => _evRasters;
    /// <summary>The composite item record of the latest turn (null until <see cref="EnableEvidence"/>).</summary>
    public CompositeLedger? CompositeLedger => _evComposites;
    /// <summary>The walk ledger (null until <see cref="EnableEvidence"/>).</summary>
    public WalkLedger? WalkLedger => _evWalks;

    /// <summary>The scene publication the next composite turn presents — stamped on its ledger frame by the host.</summary>
    public ulong EvidencePublishSeq { get; set; }

    /// <summary>Turn the evidence ledgers on for this recorder (the host's compositing pair; idempotent). Allocates the
    /// fixed rings once, at host construction — never inside a frame.</summary>
    public void EnableEvidence()
    {
        if (_ev) return;
        _evRasters = new RasterLedger();
        _evComposites = new CompositeLedger();
        _evWalks = new WalkLedger();
        _ev = true;
    }

    public bool EvidenceEnabled => _ev;

    /// <summary>The frame of the last record pass (the walk ledger's <c>Frame</c>; the TurnCost row joins on it).</summary>
    public uint PassFrame => _frame;

    // ── per-turn composite bookkeeping (BuildComposite) ──────────────────────────────────────────────────────────

    private float _evScale = 1f;
    private int _evWidthPx, _evHeightPx;
    private int[] _evItemNode = new int[64];
    private uint[] _evItemGen = new uint[64];
    private short[] _evItemSticky = new short[64];
    private byte[] _evRasterFlags = new byte[256];
    private byte[] _evItemFlags = new byte[64];

    /// <summary>BuildComposite start: the turn's raster scale and target size.</summary>
    private void EvBeginFrame(in FrameInfo info, float scale)
    {
        _evScale = scale;
        _evWidthPx = (int)info.SizePx.Width;
        _evHeightPx = (int)info.SizePx.Height;
    }

    /// <summary>AddItem: item <paramref name="index"/> comes from plan entry <paramref name="e"/> — its node and its sticky
    /// band line (the device-px top the item's clip took, <see cref="short.MinValue"/> when it carries none).</summary>
    private void EvNoteItem(int index, in Plan e)
    {
        if (index >= _evItemNode.Length)
        {
            int n = Math.Max(index + 1, _evItemNode.Length * 2);
            Array.Resize(ref _evItemNode, n); Array.Resize(ref _evItemGen, n); Array.Resize(ref _evItemSticky, n);
        }
        if ((uint)e.Slot < (uint)_slotCount && _recs[e.Slot].Live)
        {
            _evItemNode[index] = _recs[e.Slot].NodeIndex;
            _evItemGen[index] = _recs[e.Slot].Gen;
        }
        else { _evItemNode[index] = -1; _evItemGen[index] = 0; }
        _evItemSticky[index] = float.IsNaN(e.StickyY) ? short.MinValue
            : CompositeLedger.S(MathF.Floor(e.StickyY * _evScale) + MathF.Round(e.StickyDy * _evScale));
    }

    /// <summary>BuildComposite, after Resolve: the backend's per-raster / per-item flag spans for this turn, cleared.</summary>
    private Span<byte> EvRasterFlags(int count)
    {
        if (_evRasterFlags.Length < count) _evRasterFlags = new byte[Math.Max(count, _evRasterFlags.Length * 2)];
        Array.Clear(_evRasterFlags, 0, count);
        return _evRasterFlags.AsSpan(0, count);
    }

    private Span<byte> EvItemFlags(int count)
    {
        if (_evItemFlags.Length < count) _evItemFlags = new byte[Math.Max(count, _evItemFlags.Length * 2)];
        Array.Clear(_evItemFlags, 0, count);
        return _evItemFlags.AsSpan(0, count);
    }

    // ── the ledgers (EndComposite) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>EndComposite, after MarkRastered: one raster-ledger entry per scheduled raster (faithful or not).</summary>
    private void EvLedgerRasters(SliceTable table, ReadOnlySpan<byte> rasterDone, ReadOnlySpan<byte> rasterFlags)
    {
        if (!_ev || _evRasters is null) return;
        for (int i = 0; i < _rasterCount; i++)
        {
            ref readonly TileRaster tr = ref _rasters[i];
            bool done = i < rasterDone.Length && rasterDone[i] != 0;
            byte flags = done ? RasterEntryFlags.Faithful : (byte)0;
            if (i < rasterFlags.Length && (rasterFlags[i] & CompositeFrameFlags.RasterScratchRefused) != 0) flags |= RasterEntryFlags.ScratchRefused;
            table.SurfaceLedger(tr.Surface, out _, out _, out ulong want, out _, out _);
            int node = -1; uint gen = 0;
            if (table.IsLive(tr.Key.SliceId)) { ref SliceRow sr = ref table.Row(tr.Key.SliceId); node = sr.NodeIndex; gen = sr.Gen; }
            byte alpha = 255;
            for (int k = 0; k < _itemCount; k++)
                if (_items[k].SliceId == tr.Key.SliceId && _items[k].Kind is CompositeKind.Tiles or CompositeKind.Region or CompositeKind.Direct)
                { alpha = CompositeLedger.Q8(_items[k].Alpha); break; }
            _evRasters.Add(new RasterEntry
            {
                Hash = want, Frame = table.Frame, NodeIndex = node, Gen = gen, SliceId = tr.Key.SliceId,
                Tx = tr.Key.Tx, Ty = tr.Key.Ty, W = (short)Math.Min(tr.W, short.MaxValue), H = (short)Math.Min(tr.H, short.MaxValue),
                Reason = (byte)tr.Reason, Order = tr.Order, AlphaQ8 = alpha, Flags = flags,
            });
        }
    }

    /// <summary>EndComposite, before the table closes the turn: the turn's items (painter order) and placements (with their
    /// raster ledger facts) become the published composite record.</summary>
    private void EvCaptureComposite(SliceTable table, ReadOnlySpan<byte> itemFlags)
    {
        if (!_ev || _evComposites is null) return;
        CompositeLedger led = _evComposites;
        led.Begin(table.Frame, EvidencePublishSeq, Stopwatch.GetTimestamp(), _evScale, _evWidthPx, _evHeightPx);
        for (int i = 0; i < _itemCount; i++)
        {
            ref readonly CompositeItem it = ref _items[i];
            byte flags = 0;
            if (it.Kind == CompositeKind.Direct) flags |= ItemRecordFlags.Degraded;
            if (i < itemFlags.Length)
            {
                if ((itemFlags[i] & CompositeFrameFlags.ItemGroupHit) != 0) flags |= ItemRecordFlags.GroupHit;
                if ((itemFlags[i] & CompositeFrameFlags.ItemGroupRendered) != 0) flags |= ItemRecordFlags.GroupRendered;
            }
            int node = i < _evItemNode.Length ? _evItemNode[i] : -1;
            uint gen = i < _evItemGen.Length ? _evItemGen[i] : 0;
            short sticky = i < _evItemSticky.Length ? _evItemSticky[i] : short.MinValue;
            led.AddItem(CompositeLedger.Record(in it, node, gen, sticky, flags));
        }
        for (int p = 0; p < _placementCount; p++)
        {
            ref readonly TilePlacement tp = ref _placements[p];
            table.SurfaceLedger(tp.Surface, out ulong have, out int rasterFrame, out ulong want, out bool stale, out bool now);
            led.AddPlacement(new PlacementRecord
            {
                RasterHash = have, WantHash = want, SliceId = tp.Key.SliceId, RasterFrame = rasterFrame,
                Tx = tp.Key.Tx, Ty = tp.Key.Ty, W = (short)Math.Min(tp.W, short.MaxValue), H = (short)Math.Min(tp.H, short.MaxValue),
                Stale = stale ? (byte)1 : (byte)0, RasteredNow = now ? (byte)1 : (byte)0,
            });
        }
        led.Commit(table.StaleTiles, _exposedMissing);
    }

    // ── the walk ledger (BeginWalk / EndWalk) ────────────────────────────────────────────────────────────────────────

    private long[] _evWalkAt = new long[32];
    private int _sigMissWalks;

    /// <summary>BeginWalk: slot <paramref name="slot"/> re-records this pass because <paramref name="why"/>.</summary>
    private void EvNoteWalk(int slot, WalkWhy why, uint detail)
    {
        if (why == WalkWhy.SigMiss) _sigMissWalks++;
        if (!_ev || _evWalks is null) return;
        if (slot >= _evWalkAt.Length) Array.Resize(ref _evWalkAt, Math.Max(slot + 1, _evWalkAt.Length * 2));
        ref Rec r = ref _recs[slot];
        _evWalkAt[slot] = _evWalks.Add(new WalkEntry
        {
            Frame = (int)_frame, NodeIndex = r.NodeIndex, Gen = r.Gen, Bytes = -1, Detail = detail, Why = (byte)why, Role = (byte)r.Role,
        });
    }

    /// <summary>EndWalk: the walk of <paramref name="slot"/> closed with <paramref name="bytes"/> in its arena.</summary>
    private void EvWalkBytes(int slot, int bytes)
    {
        if (!_ev || _evWalks is null || slot >= _evWalkAt.Length) return;
        _evWalks.SetBytes(_evWalkAt[slot], bytes);
    }
}
