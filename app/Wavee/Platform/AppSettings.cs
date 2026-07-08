using System;
using FluentGpu.WindowsApi.Storage;

namespace Wavee;

// A statically-typed persisted-setting key: its storage name + the default returned when the key is absent. Type-safe —
// a key can only be read/written as its declared T, so call sites can't mismatch types or fat-finger a magic string.
public sealed record SettingKey<T>(string Name, T Default);

// The app's persisted settings, abstracted away from the concrete (Windows-registry) backing store: call sites depend
// only on this interface + the typed keys, so the store is trivially fakeable in a test or swappable per platform.
public interface IAppSettings
{
    T Get<T>(SettingKey<T> key);
    void Set<T>(SettingKey<T> key, T value);
}

// Every persisted setting lives here as one statically-typed key — the single registry of what the app remembers.
// Storage names are an internal detail of the keys; nothing else references the raw strings.
static class WaveeSettings
{
    public static readonly SettingKey<float> SidebarWidth = new("sidebar.width", 300f);
    public static readonly SettingKey<bool> SidebarCollapsed = new("sidebar.collapsed", false);
    public static readonly SettingKey<bool> PlayerBarShowRemaining = new("playerbar.duration.remaining", true);
    // Theme preference: 0 = System (follow the OS live), 1 = Light, 2 = Dark. Default System so a fresh install matches
    // the OS; an explicit in-app toggle pins Light/Dark and stops following the OS. Seeded at startup before the first frame.
    public static readonly SettingKey<int> ThemeMode = new("theme.mode", 0);
    // Color palette preset: warm (default) | slate | neutral | accent (OS-accent-tinted neutrals).
    public static readonly SettingKey<string> PaletteId = new("theme.palette", "warm");
    public static readonly SettingKey<int> RowDensity = new("detail.rowdensity", 1);   // 0 Compact · 1 Default · 2 Cozy · 3 Comfortable
    // The saved / liked / followed library set (Mutations facet) — a newline-joined list of uris. The single in-process
    // outbox: every optimistic save/follow rewrites it. (A real source would reconcile server-side + revision conflicts.)
    public static readonly SettingKey<string> SavedLibrary = new("library.saved", "");
    // PlayPlay runtime pointer — empty string means unset (AppDataSettings cannot round-trip null strings).
    public static readonly SettingKey<string> PlaybackRuntimePath = new("playback.runtime.path", "");
    public static readonly SettingKey<string> PlaybackRuntimePackId = new("playback.runtime.packId", "");
    public static readonly SettingKey<bool> PlaybackRuntimeSetupDismissed = new("playback.runtime.dismissed", false);
    // Optional catalog-URL override (also settable via WAVEE_PLAYPLAY_CATALOG_URL). Empty = use the built-in default.
    public static readonly SettingKey<string> PlaybackRuntimeCatalogUrl = new("playback.runtime.catalogUrl", "");
    // Streaming quality preference (AudioQuality): 0 Normal 96 · 1 High 160 · 2 Very High 320 (3 Lossless is reserved —
    // shown disabled in the picker). Read per resolve, so a change applies from the next track (already-resolved tracks
    // keep their cached file selection).
    public static readonly SettingKey<int> PlaybackQuality = new("playback.quality", 2);
    // Volume persistence: when RememberVolume, SavedVolume (0..1) seeds the device volume at launch and is written back
    // (debounced) as the user adjusts it.
    public static readonly SettingKey<bool> RememberVolume = new("playback.volume.remember", true);
    public static readonly SettingKey<float> SavedVolume = new("playback.volume", 0.7f);
    public static readonly SettingKey<bool> EqualizerEnabled = new("playback.eq.enabled", false);
    public static readonly SettingKey<string> EqualizerPreset = new("playback.eq.preset", "flat");
    public static readonly SettingKey<string> EqualizerGains = new("playback.eq.gains", "0,0,0,0,0,0,0,0,0,0");
    public static readonly SettingKey<bool> CrossfadeEnabled = new("playback.crossfade.enabled", false);
    public static readonly SettingKey<int> CrossfadeMs = new("playback.crossfade.ms", 5000);
    public static readonly SettingKey<bool> AutoplayEnabled = new("playback.autoplay", true);
    public static readonly SettingKey<long> GaboGlobalSequence = new("telemetry.gabo.globalSequence", 0L);
    // On-disk playback caches (Phase 6): encrypted CDN bodies + DPAPI-wrapped PlayPlay license payloads.
    public static readonly SettingKey<bool> AudioBodyCacheEnabled = new("audio.cache.body.enabled", true);
    public static readonly SettingKey<bool> AudioKeyCacheEnabled = new("audio.cache.keys.enabled", true);
    public static readonly SettingKey<long> AudioBodyCacheBudgetBytes = new("audio.cache.body.budgetBytes", 4L << 30);   // 4 GB
    // Crash observability: the newest Windows Error Reporting dump we've already surfaced into wavee.log / Diagnostics.
    public static readonly SettingKey<string> LastSeenCrashDumpPath = new("diagnostics.crash.lastDumpPath", "");
    public static readonly SettingKey<long> LastSeenCrashDumpTicksUtc = new("diagnostics.crash.lastDumpTicksUtc", 0L);
}

// IAppSettings backed by the engine's AppDataStore (HKCU registry, unpackaged). Every access is DEFENSIVE — a storage
// failure (or no store at all) falls back to the key's default and never throws into the UI. Type dispatch is a closed
// switch over AppDataStore's supported scalars — AOT-clean (no reflection); unsupported T's fall back to the default.
sealed class AppDataSettings : IAppSettings
{
    readonly AppDataStore? _store;
    AppDataSettings(AppDataStore? store) => _store = store;

    public static IAppSettings ForUnpackaged(string publisher, string product)
    {
        try { return new AppDataSettings(AppDataStore.ForUnpackaged(publisher, product)); }
        catch { return new AppDataSettings(null); }   // storage unavailable → reads return defaults, writes no-op
    }

    public T Get<T>(SettingKey<T> key)
    {
        if (_store is null) return key.Default;
        try
        {
            object boxed = key.Default switch
            {
                float f  => (float)_store.GetDouble(key.Name, f),
                double d => _store.GetDouble(key.Name, d),
                bool b   => _store.GetBool(key.Name, b),
                int i    => _store.GetInt(key.Name, i),
                long l   => _store.GetLong(key.Name, l),
                string s => _store.GetString(key.Name, s) ?? s,
                _        => key.Default!,
            };
            return (T)boxed;
        }
        catch { return key.Default; }
    }

    public void Set<T>(SettingKey<T> key, T value)
    {
        if (_store is null || value is null) return;
        try
        {
            switch (value)
            {
                case float f:  _store.SetDouble(key.Name, f); break;
                case double d: _store.SetDouble(key.Name, d); break;
                case bool b:   _store.SetBool(key.Name, b); break;
                case int i:    _store.SetInt(key.Name, i); break;
                case long l:   _store.SetLong(key.Name, l); break;
                case string s: _store.SetString(key.Name, s); break;
            }
        }
        catch { }
    }
}
