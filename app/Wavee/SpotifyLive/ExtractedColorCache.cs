using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>
/// Durable, image-keyed cache of cover-extracted <see cref="Palette"/>s. A given cover image's extracted colors never
/// change, so a hit is good for ~half a year — the point of persistence is that the <c>fetchExtractedColors</c> round
/// trip happens once per image EVER, not once per launch (the in-memory Pathfinder SWR cache resets each run and holds
/// only 128 entries). Backed by one small JSON file in the app cache folder; a negative result (no palette / fallback)
/// is cached for a shorter window so a genuinely colorless cover doesn't refetch on every open but still recovers.
/// </summary>
public sealed class ExtractedColorCache
{
    static readonly TimeSpan HitTtl = TimeSpan.FromDays(180);   // colors are immutable per image — long-lived
    const int FlushDebounceMs = 1500;

    readonly string _path;
    readonly object _gate = new();
    readonly Dictionary<string, Entry> _map = new(StringComparer.Ordinal);
    readonly Func<long> _nowUnix;
    bool _loaded;
    bool _dirty;
    bool _flushScheduled;

    readonly record struct Entry(uint Bg, uint Tint, uint Light, uint Accent, bool HasPalette, long Ts);

    public ExtractedColorCache(string? path = null, Func<long>? nowUnix = null)
    {
        _path = path ?? DefaultPath();
        _nowUnix = nowUnix ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public static string DefaultPath()
    {
        try { return Path.Combine(FluentGpu.WindowsApi.Storage.AppDataStore.ForUnpackaged("Wavee", "Wavee").CacheFolder, "extracted-colors.json"); }
        catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "Cache", "extracted-colors.json"); }
    }

    /// <summary>A stable key for a cover URL: the image id (the hex after <c>/image/</c>) so different pre-sized URLs for
    /// the same cover share one entry; else the query-stripped URL, lowercased.</summary>
    public static string KeyForUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        int q = url.IndexOf('?');
        var s = q >= 0 ? url.AsSpan(0, q) : url.AsSpan();
        int img = s.LastIndexOf("/image/".AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (img >= 0) s = s[(img + "/image/".Length)..];
        else { int slash = s.LastIndexOf('/'); if (slash >= 0 && slash + 1 < s.Length) s = s[(slash + 1)..]; }
        return s.ToString().ToLowerInvariant();
    }

    /// <summary>Fresh cache lookup. Returns true on a fresh hit; <paramref name="palette"/> is null for a fresh
    /// negative-cached entry (a known-colorless cover). Returns false on a miss/expiry — the caller should fetch.</summary>
    public bool TryGet(string key, out Palette? palette)
    {
        palette = null;
        if (string.IsNullOrEmpty(key)) return false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!_map.TryGetValue(key, out var e)) return false;
            // A null response cannot distinguish a genuinely colorless cover from a transient API/auth/schema failure.
            // Old builds cached both for 14 days and suppressed EnsureAsync entirely. Discard legacy negative entries
            // so the next playlist read retries immediately; only successful palettes are authoritative.
            if (!e.HasPalette)
            {
                _map.Remove(key);
                _dirty = true;
                ScheduleFlushLocked();
                return false;
            }
            var ttl = HitTtl;
            if (_nowUnix() - e.Ts > (long)ttl.TotalSeconds) { return false; }
            palette = e.HasPalette ? new Palette(e.Bg, e.Tint, e.Light, e.Accent) : null;
            return true;
        }
    }

    /// <summary>Record a resolved palette (or null for a colorless/failed cover). Schedules a debounced disk flush.</summary>
    public void Set(string key, Palette? palette)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (palette is not { } p) { _map.Remove(key); return; }
            _map[key] = new Entry(p.BackgroundDark, p.TintedDark, p.Light, p.Accent, true, _nowUnix());
            _dirty = true;
            ScheduleFlushLocked();
        }
    }

    void EnsureLoadedLocked()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in entries.EnumerateObject())
            {
                var v = prop.Value;
                _map[prop.Name] = new Entry(
                    U(v, "bg"), U(v, "tint"), U(v, "light"), U(v, "accent"),
                    v.TryGetProperty("has", out var h) && h.ValueKind == JsonValueKind.True,
                    v.TryGetProperty("ts", out var t) && t.TryGetInt64(out var ts) ? ts : 0);
            }
        }
        catch { /* a corrupt/partial cache file is non-fatal — start empty and re-populate */ }

        static uint U(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.TryGetUInt32(out var u) ? u : 0;
    }

    void ScheduleFlushLocked()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(FlushDebounceMs).ConfigureAwait(false); } catch { }
            Flush();
        });
    }

    /// <summary>Write the cache to disk (atomic temp-then-replace). Best-effort — a failed write just means a re-fetch later.</summary>
    public void Flush()
    {
        KeyValuePair<string, Entry>[] snapshot;
        lock (_gate)
        {
            _flushScheduled = false;
            if (!_dirty) return;
            _dirty = false;
            snapshot = new KeyValuePair<string, Entry>[_map.Count];
            int i = 0;
            foreach (var kv in _map) snapshot[i++] = kv;
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteNumber("version", 1);
                    w.WritePropertyName("entries");
                    w.WriteStartObject();
                    foreach (var (key, e) in snapshot)
                    {
                        w.WritePropertyName(key);
                        w.WriteStartObject();
                        w.WriteBoolean("has", e.HasPalette);
                        if (e.HasPalette)
                        {
                            w.WriteNumber("bg", e.Bg);
                            w.WriteNumber("tint", e.Tint);
                            w.WriteNumber("light", e.Light);
                            w.WriteNumber("accent", e.Accent);
                        }
                        w.WriteNumber("ts", e.Ts);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                var tmp = _path + ".tmp";
                File.WriteAllBytes(tmp, ms.ToArray());
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch { /* best-effort persistence */ }
    }
}
