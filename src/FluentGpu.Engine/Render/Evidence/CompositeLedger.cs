using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;

namespace FluentGpu.Render.Evidence;

/// <summary>
/// One composite item as it was drawn in one turn (docs/plans/evidence-diagnostics-implementation.md §A.2): which node's
/// slice, what kind, its composite alpha, clip, placement, both analytic feathers (exactly — the pixel query evaluates them
/// with <see cref="EdgeFeatherMask"/>, the shader's C# port), its self-blur σ, its sticky band line and its group facts.
/// Device px throughout. Unmanaged.
/// </summary>
public struct ItemRecord
{
    /// <summary>The node the item's slice is cut at (a group: its layer slice's node; a backdrop: the acrylic slice's; a
    /// video hole: the slice that punched it).</summary>
    public int NodeIndex;
    public uint Gen;
    /// <summary>The <c>SliceTable</c> row the item samples (−1: none — a group, backdrop or hole).</summary>
    public int SliceId;
    /// <summary><see cref="CompositeKind"/>.</summary>
    public byte Kind;
    /// <summary>Distributed ancestor edge fades the item carries (0–2).</summary>
    public byte Inherited;
    /// <summary><see cref="ItemRecordFlags"/>.</summary>
    public byte Flags;
    /// <summary>The item's composite alpha, 0..255.</summary>
    public byte AlphaQ8;
    /// <summary>The device-px scissor (<see cref="ItemRecordFlags.ClipBounded"/> clear = unbounded).</summary>
    public short ClipX, ClipY, ClipW, ClipH;
    /// <summary>The whole-device-px translation placing slice space (tile origin included) in the window.</summary>
    public int TransDx, TransDy;
    /// <summary>The item's analytic feathers (device px; <see cref="EdgeFeather.IsNone"/> when absent).</summary>
    public EdgeFeather Feather1, Feather2;
    public float BlurSigma;
    /// <summary>The sticky band line's device-px top, or <see cref="short.MinValue"/> when the item carries none.</summary>
    public short StickyTopPx;
    /// <summary>A group: the items it encloses.</summary>
    public short GroupCount;
    /// <summary>The FEATHER QUAD SPLIT (<see cref="FeatherQuadSplit"/>): the device-px rect (LTRB) inside which both
    /// feathers are exactly 1, so the composite draws that part WITHOUT the per-pixel feather — the intersection of the
    /// feathers' <see cref="EdgeFeatherMask.UnitInterior"/>s (±<see cref="EdgeFeatherMask.Unbounded"/> when unbounded).</summary>
    public float InteriorL, InteriorT, InteriorR, InteriorB;
}

/// <summary>Bits of <see cref="ItemRecord.Flags"/>.</summary>
public static class ItemRecordFlags
{
    /// <summary>The item carries its slice marker's layer (alpha / feather / blur on the item itself).</summary>
    public const byte HasLayer = 1;
    /// <summary>A group item the backend re-drew from a retained surface (a group-cache hit).</summary>
    public const byte GroupHit = 2;
    /// <summary>A group item the backend rendered this turn (a group-cache miss).</summary>
    public const byte GroupRendered = 4;
    /// <summary>A Tiles/Region item degraded to direct raster this turn (its tiles did not fit the budget).</summary>
    public const byte Degraded = 8;
    /// <summary>The item's sticky clip was engaged (its band line applied).</summary>
    public const byte StickyEngaged = 16;
    /// <summary>The item's clip is bounded (the Clip fields are meaningful).</summary>
    public const byte ClipBounded = 32;
}

/// <summary>One tile a Tiles/Region item sampled this turn, with its raster ledger facts: when its pixels were rastered,
/// the content hash they were rastered for, the hash the current stream wants, and whether that makes it STALE.</summary>
public struct PlacementRecord
{
    public ulong RasterHash;
    public ulong WantHash;
    public int SliceId;
    public int RasterFrame;
    public short Tx, Ty;
    public short W, H;
    /// <summary>1 = valid and not re-rastered this turn, yet its raster hash differs from its want (a stale tile).</summary>
    public byte Stale;
    /// <summary>1 = rastered in this very turn.</summary>
    public byte RasteredNow;
}

/// <summary>The frame facts shared by every record of one ledger frame.</summary>
public struct CompositeFrameHeader
{
    /// <summary><c>SliceTable.Frame</c> — the frame the raster ledger's entries name.</summary>
    public int Frame;
    /// <summary>The scene publication this turn presented (0 when unknown).</summary>
    public ulong PublishSeq;
    /// <summary><c>Stopwatch</c> timestamp of the capture.</summary>
    public long Qpc;
    public float Scale;
    public int WidthPx, HeightPx;
    public int Items, Placements;
    /// <summary>Items / placements past the fixed capacity (counted, never stored).</summary>
    public int DroppedItems, DroppedPlacements;
    public int StaleTiles, ExposedMissing;
}

/// <summary>A read-only view of one ledger frame (a <see cref="CompositeFrameCopy"/>, or hand-built arrays in a test).</summary>
public readonly ref struct CompositeFrameView
{
    public readonly CompositeFrameHeader Header;
    public readonly ReadOnlySpan<ItemRecord> Items;
    public readonly ReadOnlySpan<PlacementRecord> Placements;

    public CompositeFrameView(CompositeFrameHeader header, ReadOnlySpan<ItemRecord> items, ReadOnlySpan<PlacementRecord> placements)
    {
        Header = header; Items = items; Placements = placements;
    }

    /// <summary>The placement of slice <paramref name="sliceId"/> covering slice-space device px (<paramref name="sx"/>,
    /// <paramref name="sy"/>): tile (tx, ty) covers [tx·W, tx·W + w) × [ty·H, ty·H + h).</summary>
    public bool TryPlacementAt(int sliceId, int sx, int sy, out PlacementRecord placement)
    {
        for (int i = 0; i < Placements.Length; i++)
        {
            ref readonly PlacementRecord p = ref Placements[i];
            if (p.SliceId != sliceId) continue;
            int x0 = p.Tx * TileGrid.W, y0 = p.Ty * TileGrid.H;
            if (sx >= x0 && sx < x0 + p.W && sy >= y0 && sy < y0 + p.H) { placement = p; return true; }
        }
        placement = default;
        return false;
    }
}

/// <summary>A reader-owned copy of one ledger frame (UI thread / exporter). Allocated once by its owner and refilled by
/// <see cref="CompositeLedger.CopyLatest"/> — the copy itself allocates nothing.</summary>
public sealed class CompositeFrameCopy
{
    public CompositeFrameHeader Header;
    public readonly ItemRecord[] Items = new ItemRecord[CompositeLedger.ItemsPerFrame];
    public readonly PlacementRecord[] Placements = new PlacementRecord[CompositeLedger.PlacementsPerFrame];

    /// <summary>False until the first copy landed.</summary>
    public bool HasFrame => Header.Frame != 0 || Header.Items != 0;

    public CompositeFrameView View => new(Header, Items.AsSpan(0, Math.Min(Header.Items, Items.Length)),
        Placements.AsSpan(0, Math.Min(Header.Placements, Placements.Length)));

    internal void CopyFrom(CompositeFrameCopy src)
    {
        Header = src.Header;
        src.Items.AsSpan(0, Math.Min(src.Header.Items, Items.Length)).CopyTo(Items);
        src.Placements.AsSpan(0, Math.Min(src.Header.Placements, Placements.Length)).CopyTo(Placements);
    }
}

/// <summary>
/// The composite item record (docs/plans/evidence-diagnostics-implementation.md §A.2): the painter-ordered items and the
/// tile placements of the most recent composite turn, published for readers. The producer (the recorder pair's owner)
/// fills a WORK frame record by record, then <see cref="Commit"/> swaps it with the PUBLISHED frame under a lock; a reader
/// copies the published frame under the same lock (<see cref="CopyLatest"/>). Two fixed frames — the design's 16-frame ring
/// is not needed because every consumer reads either the latest frame (the pixel picker) or the frame of an armed capture,
/// which the render thread copies in the same turn it presents it; the saved memory is ~1 MiB per compositing host.
/// Zero allocation after construction.
/// </summary>
public sealed class CompositeLedger
{
    public const int ItemsPerFrame = 256;
    public const int PlacementsPerFrame = 512;

    private CompositeFrameCopy _work = new();
    private CompositeFrameCopy _published = new();
    private readonly object _lock = new();

    /// <summary>PRODUCER ONLY. Start filling the work frame.</summary>
    public void Begin(int frame, ulong publishSeq, long qpc, float scale, int widthPx, int heightPx)
    {
        _work.Header = new CompositeFrameHeader
        {
            Frame = frame, PublishSeq = publishSeq, Qpc = qpc, Scale = scale, WidthPx = widthPx, HeightPx = heightPx,
        };
    }

    /// <summary>PRODUCER ONLY. Append one item (counted as dropped past <see cref="ItemsPerFrame"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddItem(in ItemRecord item)
    {
        ref CompositeFrameHeader h = ref _work.Header;
        if (h.Items < ItemsPerFrame) _work.Items[h.Items++] = item;
        else h.DroppedItems++;
    }

    /// <summary>PRODUCER ONLY. Append one placement (counted as dropped past <see cref="PlacementsPerFrame"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddPlacement(in PlacementRecord placement)
    {
        ref CompositeFrameHeader h = ref _work.Header;
        if (h.Placements < PlacementsPerFrame) _work.Placements[h.Placements++] = placement;
        else h.DroppedPlacements++;
    }

    /// <summary>PRODUCER ONLY. Publish the work frame with the turn's invariant counts.</summary>
    public void Commit(int staleTiles, int exposedMissing)
    {
        _work.Header.StaleTiles = staleTiles;
        _work.Header.ExposedMissing = exposedMissing;
        lock (_lock) (_work, _published) = (_published, _work);
    }

    /// <summary>Copy the most recently committed frame into <paramref name="dst"/> (any thread). False when nothing has
    /// been committed yet.</summary>
    public bool CopyLatest(CompositeFrameCopy dst)
    {
        lock (_lock)
        {
            if (!_published.HasFrame) return false;
            dst.CopyFrom(_published);
            return true;
        }
    }

    /// <summary>The <see cref="CompositeFrameHeader.Frame"/> of the most recently committed frame (0 = none).</summary>
    public int LatestFrame { get { lock (_lock) return _published.Header.Frame; } }

    /// <summary>Quantize an alpha to 0..255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Q8(float a) => (byte)Math.Clamp((int)MathF.Round(a * 255f), 0, 255);

    /// <summary>Clamp a device px to a short.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short S(float v) => (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);

    /// <summary>The record of one composite item: <paramref name="nodeIndex"/>/<paramref name="gen"/> name its slice's
    /// node, <paramref name="stickyTopPx"/> its band line (<see cref="short.MinValue"/> = none), <paramref name="flags"/>
    /// the caller's facts (degraded, group hit, …) — the clip / layer bits are derived here.</summary>
    public static ItemRecord Record(in CompositeItem it, int nodeIndex, uint gen, short stickyTopPx, byte flags)
    {
        bool bounded = !(it.Clip.W <= 0f && it.Clip.H <= 0f && it.Clip.X == 0f && it.Clip.Y == 0f);
        EdgeFeatherMask.UnitInterior(it.Feather, out float l1, out float t1, out float r1, out float b1);
        EdgeFeatherMask.UnitInterior(it.Feather2, out float l2, out float t2, out float r2, out float b2);
        byte f = flags;
        if (bounded) f |= ItemRecordFlags.ClipBounded;
        if (it.HasLayer != 0) f |= ItemRecordFlags.HasLayer;
        if (stickyTopPx != short.MinValue) f |= ItemRecordFlags.StickyEngaged;
        return new ItemRecord
        {
            NodeIndex = nodeIndex, Gen = gen, SliceId = it.SliceId, Kind = (byte)it.Kind, Inherited = it.Inherited,
            Flags = f, AlphaQ8 = Q8(it.Alpha),
            ClipX = S(it.Clip.X), ClipY = S(it.Clip.Y), ClipW = S(it.Clip.W), ClipH = S(it.Clip.H),
            TransDx = (int)MathF.Round(it.Transform.Dx), TransDy = (int)MathF.Round(it.Transform.Dy),
            Feather1 = it.Feather, Feather2 = it.Feather2, BlurSigma = it.BlurSigma,
            StickyTopPx = stickyTopPx, GroupCount = (short)Math.Clamp(it.GroupCount, 0, short.MaxValue),
            InteriorL = MathF.Max(l1, l2), InteriorT = MathF.Max(t1, t2), InteriorR = MathF.Min(r1, r2), InteriorB = MathF.Min(b1, b2),
        };
    }
}
