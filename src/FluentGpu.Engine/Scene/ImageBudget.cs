namespace FluentGpu.Scene;

/// <summary>The window-relative image-cache budget. Pure: no globals, no device.
/// <para>The cap the host builds the cache with (<see cref="GpuMemoryBudgets"/>) is a flat number per tier, whatever the window. A
/// 4K window holds a lot more on-screen art than an 800 x 600 one, and its unpinned LRU (the covers just scrolled past) was evicted
/// against the same small number. The budget is therefore <c>max(today, window-derived)</c>: three window-areas of 4-byte pixels,
/// clamped to [<see cref="Floor"/>, <see cref="WeakCeiling"/> / <see cref="Ceiling"/>] - and never below what the cache was built
/// with, so no window and no tier is ever given LESS than it has today. Pinned (on-screen) entries are never evicted by it either
/// way; it only bounds how many unpinned ones stay warm.</para></summary>
public static class ImageBudget
{
    /// <summary>Window areas of pixels the derived budget holds.</summary>
    public const double WindowMultiplier = 3.0;
    /// <summary>Lowest derived budget (a tiny window still keeps its scrolled-past covers warm).</summary>
    public const long Floor = 32L * 1024 * 1024;
    /// <summary>Highest derived budget on a weak (UMA / integrated) tier.</summary>
    public const long WeakCeiling = 64L * 1024 * 1024;
    /// <summary>Highest derived budget on a discrete tier.</summary>
    public const long Ceiling = 96L * 1024 * 1024;

    /// <summary>The budget a window of <paramref name="widthPx"/> x <paramref name="heightPx"/> earns on its own.</summary>
    public static long Derived(int widthPx, int heightPx, bool weak)
    {
        if (widthPx <= 0 || heightPx <= 0) return 0;
        double bytes = WindowMultiplier * widthPx * heightPx * 4.0;
        return Math.Clamp((long)Math.Min(bytes, (double)long.MaxValue / 2), Floor, weak ? WeakCeiling : Ceiling);
    }

    /// <summary>The cap in force: <c>max(<paramref name="baseBudget"/>, <see cref="Derived"/>)</c>. A base below <see cref="Floor"/> is
    /// an explicit choice (a host override, a test) and is left exactly as given.</summary>
    public static long Current(long baseBudget, int widthPx, int heightPx, bool weak)
        => baseBudget < Floor ? baseBudget : Math.Max(baseBudget, Derived(widthPx, heightPx, weak));
}
