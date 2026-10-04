using System.Threading;

namespace FluentGpu.Render.Tiles;

/// <summary>
/// The retained-tile memory budget (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.6):
/// <c>TileBudgetBytes = clamp(WindowMultiplier × windowBytes, FloorBytes, CeilingBytes)</c> with the defaults
/// 5.0 × / 48 MiB / 128 MiB (≈ 78 MiB at 2560×1600; the weak tier's preset is a 72 MiB ceiling and a 0.15 retained share, <see cref="ApplyTierPreset"/>) — raised 2026-09-24 from 3.5 × / 32 / 96: the artist page's
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

    /// <summary>The weak-tier (UMA / iGPU) preset (F255): on those parts every tile surface is pinned SYSTEM memory that
    /// the video decoder and the rest of the process share, and a covered-window scroll needs far less than the discrete
    /// ceiling (the artist page's visible need is ~45 MiB, under the 48 MiB floor, which the floor still guarantees). The
    /// ceiling only bites a large window; the retained share bounds the group / blur / backdrop caches that ride on top.
    /// Applied once at host construction by <see cref="ApplyTierPreset"/>.</summary>
    public const long WeakCeilingBytes = 72 * MiB;
    public const double WeakRetainedShare = 0.15;

    private static double s_windowMultiplier = DefaultWindowMultiplier;
    private static long s_floorBytes = DefaultFloorBytes;
    private static long s_ceilingBytes = DefaultCeilingBytes;
    private static long s_overrideBytes;
    private static double s_retainedShare = DefaultRetainedShare;
    private static int s_version;
    private static bool s_weakPreset;

    /// <summary>Select the tier's defaults for the ceiling and the retained share: the weak preset
    /// (<see cref="WeakCeilingBytes"/>, <see cref="WeakRetainedShare"/>) or the discrete ones, and make
    /// <see cref="ResetToDefaults"/> restore THAT tier's values. Runs once at host construction, before any frame, so it
    /// overwrites nothing a live tuning session set; the window multiplier, floor and override are tier-independent.</summary>
    public static void ApplyTierPreset(bool weak)
    {
        Volatile.Write(ref s_weakPreset, weak);
        Volatile.Write(ref s_ceilingBytes, weak ? WeakCeilingBytes : DefaultCeilingBytes);
        Volatile.Write(ref s_retainedShare, weak ? WeakRetainedShare : DefaultRetainedShare);
        Bump();
    }

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

    /// <summary>Bumped on every tunable write.</summary>
    public static uint Version => (uint)Volatile.Read(ref s_version);

    /// <summary>The live budget for a <paramref name="widthPx"/>×<paramref name="heightPx"/> window.</summary>
    public static long Current(int widthPx, int heightPx)
    {
        long over = OverrideBytes;
        return over > 0 ? over : Compute(widthPx, heightPx, WindowMultiplier, FloorBytes, CeilingBytes);
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
        bool weak = Volatile.Read(ref s_weakPreset);
        Volatile.Write(ref s_ceilingBytes, weak ? WeakCeilingBytes : DefaultCeilingBytes);
        Volatile.Write(ref s_overrideBytes, 0);
        Volatile.Write(ref s_retainedShare, weak ? WeakRetainedShare : DefaultRetainedShare);
        Bump();
    }

    private static void Bump() => Interlocked.Increment(ref s_version);
}
