using System;
using System.Collections.Generic;

namespace FluentGpu.Foundation;

/// <summary>
/// UI-thread-affine formatted-string caches for the typed bound authoring API (<c>BoundItemScope&lt;T&gt;</c>,
/// <c>FluentGpu.Controls</c>). A virtualized row that formats "3:45" or "1,204 plays" on every recycle would mint a
/// fresh <see cref="string"/> per rebind — these caches turn that into a dictionary/array probe against a string the
/// process already owns, so the recycle-frame allocation is (at most) the cache's own bookkeeping, never per-row text.
///
/// Not thread-safe by design (same discipline as <see cref="StringTable"/>): every cache here is read and written only
/// from the UI/reconcile thread, exactly like the reconciler itself.
/// </summary>
public sealed class FormatCache<TKey> where TKey : notnull
{
    /// <summary>Hard cap on live entries. A cache that grows without bound (an unbounded key domain — free-text search
    /// results, live timestamps) would itself become the leak the cache exists to prevent; hitting the cap clears the
    /// whole table and starts over rather than evicting piecemeal (no LRU bookkeeping on the hot path).</summary>
    public const int Capacity = 4096;

    private readonly Dictionary<TKey, string> _map;
    private readonly IEqualityComparer<TKey>? _cmp;

    public FormatCache(IEqualityComparer<TKey>? comparer = null)
    {
        _cmp = comparer;
        _map = comparer is null ? new Dictionary<TKey, string>() : new Dictionary<TKey, string>(comparer);
    }

    /// <summary>Number of live entries — informational (tests / diagnostics), not read on the hot path.</summary>
    public int Count => _map.Count;

    /// <summary>Returns the cached string for <paramref name="key"/>, formatting and caching it on a miss.
    /// <paramref name="formatter"/> must be a pure function of <paramref name="key"/> (its result is reused for every
    /// future call with an equal key until the cache clears on overflow).</summary>
    public string Get(TKey key, Func<TKey, string> formatter)
    {
        if (_map.TryGetValue(key, out var cached)) return cached;
        if (_map.Count >= Capacity) _map.Clear();
        var value = formatter(key);
        _map[key] = value;
        return value;
    }
}

/// <summary>Non-generic entry points: the two dedicated caches the bound authoring API leans on constantly
/// (<c>BoundItemScope&lt;T&gt;.Number</c>/<c>.Duration</c>), plus the shared factory for a keyed cache.</summary>
public static class FormatCache
{
    /// <summary>Dense small-integer → string cache (row numbers, play counts, track counts): a plain grow-only
    /// <c>string[]</c> indexed by value, NEVER cleared (unlike the keyed <see cref="FormatCache{TKey}"/> — a UI's
    /// visible integer range is bounded and stable, so there is no unbounded-key-domain risk to guard against). Grows
    /// to the highest index seen, doubling from a floor of 64. Negative values and values that would grow the array
    /// past <see cref="MaxDenseValue"/> fall back to <see cref="int.ToString()"/> uncached (rare — a malformed/huge
    /// count — never on the steady-state row-number/track-count path).</summary>
    public const int MaxDenseValue = 1 << 20; // 1,048,576 — far past any realistic row/track/plays count

    private static string?[] _int = new string?[64];

    public static string Int(int value)
    {
        if (value < 0 || value > MaxDenseValue) return value.ToString();
        if (value >= _int.Length)
        {
            int newLen = _int.Length;
            while (newLen <= value) newLen *= 2;
            Array.Resize(ref _int, newLen);
        }
        return _int[value] ??= value.ToString();
    }

    /// <summary>"m:ss" (e.g. "3:45"). Keyed on whole seconds — sub-second precision never reaches the label.</summary>
    public static readonly FormatCache<int> MmSs = new(comparer: null);

    /// <summary>"h:mm:ss" for durations at or above one hour, "m:ss" below it (matches <see cref="MmSs"/>'s shape for
    /// the common case so a caller can switch caches without a display-format seam).</summary>
    public static readonly FormatCache<int> HhMmSs = new(comparer: null);

    /// <summary>Formats <paramref name="totalMs"/> through <see cref="MmSs"/>, caching by whole seconds.</summary>
    public static string DurationMmSs(long totalMs)
    {
        int totalSeconds = (int)Math.Max(0, totalMs / 1000);
        return MmSs.Get(totalSeconds, static s => $"{s / 60}:{s % 60:D2}");
    }

    /// <summary>Formats <paramref name="totalMs"/> through <see cref="HhMmSs"/>, caching by whole seconds; below one
    /// hour this returns the same "m:ss" shape <see cref="DurationMmSs"/> does.</summary>
    public static string DurationHhMmSs(long totalMs)
    {
        int totalSeconds = (int)Math.Max(0, totalMs / 1000);
        return HhMmSs.Get(totalSeconds, static s =>
        {
            int h = s / 3600, m = (s % 3600) / 60, sec = s % 60;
            return h > 0 ? $"{h}:{m:D2}:{sec:D2}" : $"{m}:{sec:D2}";
        });
    }

    /// <summary>Creates a new keyed cache for an arbitrary formatted-string domain (dates, "N plays", etc.), for use
    /// with <c>BoundItemScope&lt;T&gt;.Text&lt;TKey&gt;(keySel, cache)</c>. Hoist and reuse ONE instance per call site
    /// (a per-row cache defeats the point) — a static readonly field alongside the row template, exactly like
    /// <see cref="MmSs"/>/<see cref="HhMmSs"/> above.</summary>
    public static FormatCache<TKey> Create<TKey>(IEqualityComparer<TKey>? comparer = null) where TKey : notnull
        => new(comparer);
}
