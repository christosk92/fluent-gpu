using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Storage;

// ── GalleryPrefs (G8c2) ───────────────────────────────────────────────────────────────────────────────────────────
// Lightweight, process-scoped Favorites + Recent state for the gallery shell — persisted through the WindowsApi Storage
// pillar (AppDataStore.ForUnpackaged, HKCU\Software\FluentGpu\Gallery). NOT a subsystem: two reactive Signal<string[]>
// (favorites = a stable-sorted key set; recent = most-recent-first, capped) seeded from disk on first touch and
// write-through-persisted on every mutation. Reading a signal's Value in a component subscribes it, so a star toggle
// or a page visit re-renders exactly the tiles/headers that show it. Store access is guarded (OperatingSystem.IsWindows
// + try/catch) so the headless --gallery-audit and any non-Windows host fall back to in-memory with no throw.
static class GalleryPrefs
{
    const string Publisher = "FluentGpu";
    const string Product = "Gallery";
    const string FavValue = "favorites";
    const string RecentValue = "recent";
    const int RecentCap = 20;
    const char Sep = '';   // unit separator — page keys never contain it

    static readonly object _gate = new();
    static Signal<string[]>? _favorites;
    static Signal<string[]>? _recent;
    static AppDataStore? _store;
    static bool _storeTried;

    static AppDataStore? Store
    {
        get
        {
            if (_storeTried) return _store;
            _storeTried = true;
            if (OperatingSystem.IsWindows())
                try { _store = AppDataStore.ForUnpackaged(Publisher, Product); } catch { _store = null; }
            return _store;
        }
    }

    /// <summary>The pinned page keys (stable-sorted). Reading <c>.Value</c> subscribes the caller.</summary>
    public static Signal<string[]> Favorites
    {
        get { Ensure(); return _favorites!; }
    }

    /// <summary>The recent page keys, most-recent-first (capped at 20). Reading <c>.Value</c> subscribes the caller.</summary>
    public static Signal<string[]> Recent
    {
        get { Ensure(); return _recent!; }
    }

    static void Ensure()
    {
        if (_favorites is not null) return;
        lock (_gate)
        {
            if (_favorites is not null) return;
            _recent = new Signal<string[]>(Load(RecentValue));
            _favorites = new Signal<string[]>(Load(FavValue).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }
    }

    public static bool IsFavorite(string key) => Array.IndexOf(Favorites.Value, key) >= 0;

    public static void ToggleFavorite(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        Ensure();
        var cur = _favorites!.Peek();
        string[] next = Array.IndexOf(cur, key) >= 0
            ? cur.Where(k => k != key).ToArray()
            : cur.Append(key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        _favorites.Value = next;
        Save(FavValue, next);
    }

    /// <summary>Record a page visit — moves <paramref name="key"/> to the front of Recent (dedup), caps at 20, persists.</summary>
    public static void RecordVisit(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        Ensure();
        var cur = _recent!.Peek();
        if (cur.Length > 0 && cur[0] == key) return;   // already newest — no churn
        var list = new List<string>(cur.Length + 1) { key };
        foreach (var k in cur) if (k != key && list.Count < RecentCap) list.Add(k);
        var next = list.ToArray();
        _recent.Value = next;
        Save(RecentValue, next);
    }

    static string[] Load(string value)
    {
        try
        {
            string? raw = Store?.GetString(value);
            if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
            return raw.Split(Sep, StringSplitOptions.RemoveEmptyEntries);
        }
        catch { return Array.Empty<string>(); }
    }

    static void Save(string value, string[] keys)
    {
        try { Store?.SetString(value, string.Join(Sep, keys)); } catch { /* in-memory only */ }
    }
}
