using System.Threading;

namespace FluentGpu.Render.Tiles;

/// <summary>
/// The retained-tile memory budget (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.6):
/// <c>TileBudgetBytes = clamp(WindowMultiplier × windowBytes, FloorBytes, CeilingBytes)</c> with the defaults
/// 5.0 × / 48 MiB / 128 MiB (≈ 78 MiB at 2560×1600) — raised 2026-09-24 from 3.5 × / 32 / 96: the artist page's
/// VISIBLE need (<c>TileCensus.VisibleNeedBytes</c>; ≈ 45 MiB at 1860×1230 by the plan's derivation, the scroll bench's
/// measurement is recorded in the plan's status note) exceeds the 32 MiB floor the old derivation gave (docs/plans/composite-fade-groups-implementation.md §3, owner decision 1). Every term is
/// LIVE-TUNABLE from the Diagnostics page / a probe — never an environment variable. <see cref="RetainedShare"/> bounds
/// the backend's RETAINED derived surfaces (group / self-blur / backdrop caches) to that share of the tile budget, on top
/// of it. Writers are the UI thread; the render thread reads once per turn (<see cref="Current"/>). Each property is
/// individually atomic; <see cref="Version"/> bumps on every write so a reader can notice a change without subscribing.
/// </summary>
public static class TileBudget
{
    /// <summary>Bytes one BGRA8 <see cref="TileGrid.W"/>×<see cref="TileGrid.H"/> tile surface holds (2 MiB).</summary>
    public const long TileBytes = (long)TileGrid.W * TileGrid.H * 4;

    public const long MiB = 1024L * 1024L;
    public const double DefaultWindowMultiplier = 5.0;
    public const long DefaultFloorBytes = 48 * MiB;
    public const long DefaultCeilingBytes = 128 * MiB;
    public const double DefaultRetainedShare = 0.25;
    /// <summary>The ceiling scales with the window (<see cref="CeilingFor"/>): the tiles covering the window times this.
    /// 1.5 keeps the whole visible set resident plus half again for the scroll-ahead/behind bands. 0 disables the scaling.</summary>
    public const double DefaultCeilingCoverMultiplier = 1.5;

    private static double s_windowMultiplier = DefaultWindowMultiplier;
    private static long s_floorBytes = DefaultFloorBytes;
    private static long s_ceilingBytes = DefaultCeilingBytes;
    private static long s_overrideBytes;
    private static double s_retainedShare = DefaultRetainedShare;
    private static double s_ceilingCoverMultiplier = DefaultCeilingCoverMultiplier;
    private static int s_version;

    /// <summary>The backend's retained derived surfaces (a group surface, a self-blur, an acrylic backdrop kept across turns
    /// under their content key) may hold at most this share of the tile budget (<see cref="RetainedBytesCap"/>): past it
    /// the least recently used retained result returns to the scratch pool first. Values outside [0, 4] are ignored.</summary>
    public static double RetainedShare
    {
        get => Volatile.Read(ref s_retainedShare);
        set { if (value >= 0.0 && value <= 4.0 && double.IsFinite(value)) { Volatile.Write(ref s_retainedShare, value); Bump(); } }
    }

    /// <summary>The retained-surface byte cap for a tile budget of <paramref name="budgetBytes"/>.</summary>
    public static long RetainedBytesCap(long budgetBytes) => budgetBytes <= 0 ? 0 : (long)(budgetBytes * RetainedShare);

    /// <summary>Budget = this × the window's BGRA8 byte size before the floor/ceiling clamp. Values ≤ 0 are ignored.</summary>
    public static double WindowMultiplier
    {
        get => Volatile.Read(ref s_windowMultiplier);
        set { if (value > 0.0 && double.IsFinite(value)) { Volatile.Write(ref s_windowMultiplier, value); Bump(); } }
    }

    /// <summary>The budget never drops below this (bytes). Values ≤ 0 are ignored.</summary>
    public static long FloorBytes
    {
        get => Volatile.Read(ref s_floorBytes);
        set { if (value > 0) { Volatile.Write(ref s_floorBytes, value); Bump(); } }
    }

    /// <summary>The budget never exceeds this (bytes). Values ≤ 0 are ignored.</summary>
    public static long CeilingBytes
    {
        get => Volatile.Read(ref s_ceilingBytes);
        set { if (value > 0) { Volatile.Write(ref s_ceilingBytes, value); Bump(); } }
    }

    /// <summary>A fixed budget in bytes that replaces the derivation (a measurement knob); 0 = derive from the window.
    /// Negative values are treated as 0.</summary>
    public static long OverrideBytes
    {
        get => Volatile.Read(ref s_overrideBytes);
        set { Volatile.Write(ref s_overrideBytes, value > 0 ? value : 0); Bump(); }
    }

    /// <summary>The window-scaled ceiling term (<see cref="CeilingFor"/>): the tiles covering the window times this multiplier,
    /// in bytes, raise the ceiling above <see cref="CeilingBytes"/> for very large windows. 0 turns the scaling off (the fixed
    /// ceiling alone); negative or non-finite values are ignored.</summary>
    public static double CeilingCoverMultiplier
    {
        get => Volatile.Read(ref s_ceilingCoverMultiplier);
        set { if (value >= 0.0 && double.IsFinite(value)) { Volatile.Write(ref s_ceilingCoverMultiplier, value); Bump(); } }
    }

    /// <summary>Bumped on every tunable write.</summary>
    public static uint Version => (uint)Volatile.Read(ref s_version);

    /// <summary>The live budget for a <paramref name="widthPx"/>×<paramref name="heightPx"/> window. The ceiling is
    /// <see cref="CeilingFor"/> (the fixed <see cref="CeilingBytes"/> raised for a window whose tiles do not fit it), so a
    /// 7680×4320 px window (3840×2160 DIP at 200 %) keeps its visible tiles resident.</summary>
    public static long Current(int widthPx, int heightPx)
    {
        long over = OverrideBytes;
        return over > 0 ? over : Compute(widthPx, heightPx, WindowMultiplier, FloorBytes, CeilingFor(widthPx, heightPx, CeilingBytes, CeilingCoverMultiplier));
    }

    /// <summary>How many <see cref="TileGrid.W"/>×<see cref="TileGrid.H"/> tiles cover a <paramref name="widthPx"/>×<paramref name="heightPx"/>
    /// window at a worst-case grid phase (one extra column and row: a viewport rarely starts on a tile boundary). Pure.</summary>
    public static int CoverTiles(int widthPx, int heightPx)
    {
        if (widthPx <= 0 || heightPx <= 0) return 0;
        long cols = (widthPx + TileGrid.W - 1) / TileGrid.W + 1L;
        long rows = (heightPx + TileGrid.H - 1) / TileGrid.H + 1L;
        long n = cols * rows;
        return n > int.MaxValue ? int.MaxValue : (int)n;
    }

    /// <summary>The window-scaled ceiling: <c>max(baseCeilingBytes, coverTiles × TileBytes × coverMultiplier)</c> (the base
    /// ceiling is the floor, so a window up to 4K at 100 % keeps today's 128 MiB). Pure.</summary>
    public static long CeilingFor(int widthPx, int heightPx, long baseCeilingBytes, double coverMultiplier)
    {
        if (coverMultiplier <= 0.0) return baseCeilingBytes;
        double scaled = CoverTiles(widthPx, heightPx) * (double)TileBytes * coverMultiplier;
        long bytes = scaled >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(scaled);
        return bytes > baseCeilingBytes ? bytes : baseCeilingBytes;
    }

    /// <summary>The tile surface cap (<c>SliceTable.SurfaceCap</c>) for a <paramref name="widthPx"/>×<paramref name="heightPx"/>
    /// window: the default-scaled ceiling in tiles, never below <paramref name="floorCap"/> (today's 64). Pure.</summary>
    public static int SurfaceCapFor(int widthPx, int heightPx, int floorCap = SliceTable.DefaultSurfaceCap)
    {
        long ceiling = CeilingFor(widthPx, heightPx, DefaultCeilingBytes, DefaultCeilingCoverMultiplier);
        long tiles = (ceiling + TileBytes - 1) / TileBytes;
        return tiles > floorCap ? (int)Math.Min(tiles, 4096) : floorCap;
    }

    /// <summary>The pure derivation: <c>clamp(multiplier × w·h·4, floor, ceiling)</c>. A ceiling below the floor
    /// resolves to the floor (the floor wins: never fewer tiles than a window needs to stay unblank).</summary>
    public static long Compute(int widthPx, int heightPx, double multiplier, long floorBytes, long ceilingBytes)
    {
        long windowBytes = (long)Math.Max(0, widthPx) * Math.Max(0, heightPx) * 4L;
        double raw = multiplier * windowBytes;
        long bytes = raw >= long.MaxValue ? long.MaxValue : (long)raw;
        if (bytes > ceilingBytes) bytes = ceilingBytes;
        if (bytes < floorBytes) bytes = floorBytes;
        return bytes;
    }

    /// <summary>How many tile surfaces <paramref name="budgetBytes"/> can hold resident.</summary>
    public static int MaxResidentTiles(long budgetBytes) => budgetBytes <= 0 ? 0 : (int)Math.Min(int.MaxValue, budgetBytes / TileBytes);

    /// <summary>Restore every tunable to its default.</summary>
    public static void ResetToDefaults()
    {
        Volatile.Write(ref s_windowMultiplier, DefaultWindowMultiplier);
        Volatile.Write(ref s_floorBytes, DefaultFloorBytes);
        Volatile.Write(ref s_ceilingBytes, DefaultCeilingBytes);
        Volatile.Write(ref s_overrideBytes, 0);
        Volatile.Write(ref s_retainedShare, DefaultRetainedShare);
        Volatile.Write(ref s_ceilingCoverMultiplier, DefaultCeilingCoverMultiplier);
        Bump();
    }

    private static void Bump() => Interlocked.Increment(ref s_version);
}
