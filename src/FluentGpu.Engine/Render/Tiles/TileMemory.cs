namespace FluentGpu.Render.Tiles;

/// <summary>
/// How the backend allocates retained-tile textures. A tile texture is created when a slot is first rastered (or its extent
/// changes) and freed when the <see cref="SliceTable"/> trims the slot. Freeing a COMMITTED texture hands its memory back to
/// the driver: a fixed ~0.3 ms per release on the owner's UMA driver, whatever the size (<c>--fg heap-probe</c>: a 2 MiB tile
/// 0.26–0.31 ms, a 16.5–33 MiB heap 0.2–0.4 ms), in bursts of 8–24 when a page's slots trim together — a 3–7 ms render turn.
/// With <see cref="PlacedTiles"/> a tile texture is a PLACED resource in a pooled heap: freeing it returns its blocks to the
/// pool and frees no memory (a placed release ~2 µs, a placed create ~5–13 µs). While the app composites, empty heaps are released
/// while the heaps' free capacity (empty heaps + free runs inside partly used heaps) is above the pool's free-surface cap
/// (<c>LayerTargetTrim.FreeScratchCapBytes</c>: what the pool may keep of idle surfaces), one heap per housekeeping pass right
/// after the turn that freed it (<c>IGpuDevice.HeapReleaseDue</c>), never inside a turn (playback never goes idle, so keeping every
/// empty heap until idle would hold the session's high-water mark: +289 MB measured while playing). Once the app has composited
/// nothing for <see cref="HeapIdleReleaseMs"/>, every empty heap is released, one per idle tick, after an idle compaction has
/// moved the sparsest heap's tiles into the others.
/// </summary>
public static class TileMemory
{
    /// <summary>Tile textures are placed resources in pooled heaps (default). <c>--fg committed-tiles</c>: one committed
    /// texture per tile (the pre-heap behaviour, the A/B arm).</summary>
    public static bool PlacedTiles = true;

    /// <summary><c>--fg heap-probe</c>: once at device init, time committed / heap / placed creates and releases (with and
    /// without <c>D3D12_HEAP_FLAG_CREATE_NOT_ZEROED</c>) and print <c>[heap-probe]</c> lines.</summary>
    public static bool HeapProbe;

    /// <summary><c>--fg heap-log</c>: a <c>[tile-heaps]</c> line on stderr at every heap create / release (diagnostics).</summary>
    public static bool HeapLog;

    /// <summary>The app has composited nothing for this long before an empty tile heap is released (one per idle tick).</summary>
    public const long HeapIdleReleaseMs = 3_000;

    /// <summary>While the app composites, the free runs inside partly used heaps may hold this many heaps' worth before a turn
    /// moves the sparsest heap's surfaces into the others (one GPU copy per surface, recorded ahead of the turn's rasters).</summary>
    public const int FragmentCapHeaps = 1;

    /// <summary>At most one in-frame compaction this often.</summary>
    public const long FrameCompactSpacingMs = 250;

    /// <summary>While idle, one empty heap is released per idle tick, this far apart (each is one driver release).</summary>
    public const int HeapReleaseSpacingMs = 100;
}
