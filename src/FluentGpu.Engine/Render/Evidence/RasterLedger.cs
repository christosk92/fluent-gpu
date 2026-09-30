namespace FluentGpu.Render.Evidence;

/// <summary>One tile raster the composite turn SCHEDULED (docs/plans/evidence-diagnostics-implementation.md §A.1): which
/// tile of which slice, when (the <c>SliceTable</c> frame), why (its invalidation reason and needed-set order), the
/// composite alpha its item carried that turn (the alpha BAKED into the bytes is inside <see cref="Hash"/>), whether the
/// backend reported it faithful and whether an inline scratch lease was refused while it rastered, and the content hash
/// of the bytes it was rastered from. Unmanaged, fixed size.</summary>
public struct RasterEntry
{
    /// <summary>The tile content hash the raster was scheduled for (<see cref="TileContentHash"/>; 0 = unknown).</summary>
    public ulong Hash;
    /// <summary><c>SliceTable.Frame</c> of the turn.</summary>
    public int Frame;
    /// <summary>The scene node the slice is cut at, and its generation.</summary>
    public int NodeIndex;
    public uint Gen;
    /// <summary>The <c>SliceTable</c> slot (a slice SEGMENT's row id).</summary>
    public int SliceId;
    public short Tx, Ty;
    /// <summary>The raster surface's device-px extent.</summary>
    public short W, H;
    /// <summary><c>InvalidationReason</c> at schedule.</summary>
    public byte Reason;
    /// <summary>Needed-set order: 0 visible, 1 ahead, 2 behind.</summary>
    public byte Order;
    /// <summary>The slice's composite item alpha this turn, 0..255 (255 when no item samples the slice).</summary>
    public byte AlphaQ8;
    /// <summary><see cref="RasterEntryFlags"/>.</summary>
    public byte Flags;
}

/// <summary>Bits of <see cref="RasterEntry.Flags"/>.</summary>
public static class RasterEntryFlags
{
    /// <summary>The backend reported the raster faithful (<c>CompositeFrame.RasterDone</c> = 1): the tile became valid.</summary>
    public const byte Faithful = 1;
    /// <summary>An inline scratch lease was refused while the tile rastered (<c>CompositeFrameFlags.RasterScratchRefused</c>) —
    /// an inline folded group inside it drew nothing.</summary>
    public const byte ScratchRefused = 2;
}

/// <summary>The raster ledger: every tile raster a recorder pair scheduled, newest last. <see cref="Capacity"/> = 4096
/// entries (160 KiB) — at the ~3 rasters a scrolling turn schedules, 20+ minutes of history. Written on the composite turn
/// (<c>SliceRecorder.EndComposite</c>), drained by the evidence exporter.</summary>
public sealed class RasterLedger() : DiagRing<RasterEntry>(RasterLedgerCapacity)
{
    public const int RasterLedgerCapacity = 4096;
}
