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
    // Color palette preset: neutral (default) | warm | slate | accent (OS-accent-tinted neutrals).
    public static readonly SettingKey<string> PaletteId = new("theme.palette", "neutral");
    public static readonly SettingKey<int> RowDensity = new("detail.rowdensity", 1);   // 0 Compact · 1 Default · 2 Cozy · 3 Comfortable
    // Track-detail page layout: 0 Automatic (metadata rail on wide windows, stacked hero on narrow) · 1 Stacked (the
    // hero composition at every width — the rail is never composed for track pages; podcasts keep the automatic layout).
    public static readonly SettingKey<int> DetailPageLayout = new("detail.page.layout", 0);
    public static readonly SettingKey<bool> DisableMarquee = new("appearance.marquee.disabled", false);
    public static readonly SettingKey<bool> DisableColorWashes = new("appearance.colorWashes.disabled", false);
    // Wide two-column detail pages: user-resizable left metadata rail. Album-like and playlist-like surfaces keep
    // separate widths because their authored defaults differ (280 vs 240 DIP). Responsive mid/narrow modes ignore these
    // values and retain their breakpoint widths; the saved width returns when the page is wide again.
    public static readonly SettingKey<float> DetailAlbumRailWidth = new("detail.rail.album.width", WaveeSize.RailAlbum);
    public static readonly SettingKey<float> DetailPlaylistRailWidth = new("detail.rail.playlist.width", WaveeSize.RailPlaylist);
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
    // Output-device persistence (Phase A): the chosen WASAPI endpoint id (empty = system default) + its friendly name
    // (used in the reconnect toast while the device is absent). AppDataSettings cannot round-trip null → empty means unset.
    public static readonly SettingKey<string> OutputDeviceId = new("playback.output.deviceId", "");
    public static readonly SettingKey<string> OutputDeviceName = new("playback.output.deviceName", "");
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
    // Body-cache capacity: 0=fixed bytes, 1=drive share (percent 0 means Auto), 2=unlimited.
    public static readonly SettingKey<int> AudioBodyCacheBudgetMode = new("audio.cache.body.budgetMode", 1);
    public static readonly SettingKey<long> AudioBodyCacheBudgetBytes = new("audio.cache.body.budgetBytes", 32L << 30);
    public static readonly SettingKey<int> AudioBodyCacheBudgetPercent = new("audio.cache.body.budgetPercent", 0);
    // Empty = AppData default. A custom value is the user-selected parent; Wavee owns its WaveeAudioCache child only.
    public static readonly SettingKey<string> AudioBodyCacheBasePath = new("audio.cache.body.basePath", "");
    // Crash observability: the newest Windows Error Reporting dump we've already surfaced into wavee.log / Diagnostics.
    public static readonly SettingKey<string> LastSeenCrashDumpPath = new("diagnostics.crash.lastDumpPath", "");
    public static readonly SettingKey<long> LastSeenCrashDumpTicksUtc = new("diagnostics.crash.lastDumpTicksUtc", 0L);
    // Notification center: the unix-ms watermark past which a remote-feed item counts as "new" (advanced on panel open).
    // Local-only read-state for the gander + what's-new feeds (no server mark-read endpoint). Works on both backends.
    public static readonly SettingKey<long> NotificationsGanderLastSeenMs = new("notifications.gander.lastSeenMs", 0L);
    public static readonly SettingKey<long> NotificationsWhatsNewLastSeenMs = new("notifications.whatsnew.lastSeenMs", 0L);
    // Runtime log-level overrides for the Diagnostics panel (WaveeLogLevel as int; -1 = build default). The env vars
    // WAVEE_LOG_LEVEL / WAVEE_LOG_FILE_LEVEL still win over these (resolved inside WaveeLog.Configure).
    public static readonly SettingKey<int> LogMinLevel = new("diagnostics.log.minLevel", -1);
    public static readonly SettingKey<int> LogFileMinLevel = new("diagnostics.log.fileMinLevel", -1);
}

// The LibraryPage's per-kind persisted state (the "Your Library" master–detail: albums/artists/podcasts). Keys are built
// per kind at runtime — plain record construction, AOT-clean — so the three kinds keep independent last-used state, and
// each key carries its own default so a missing key (or no store) degrades to it. Scope is per-page-global: multiple open
// tabs of one kind stay independent while live, then seed from the same saved values on a fresh launch. Filter text is
// intentionally NOT persisted (it starts empty each launch), so there is no key for it here.
static class LibraryStateKeys
{
    public static SettingKey<float> LeftW(string k) => new($"library.{k}.leftw", k == "artists" ? 280f : 340f);
    public static SettingKey<float> MidW(string k) => new($"library.{k}.midw", 440f);
    public static SettingKey<int> Sort(string k) => new($"library.{k}.sort", 0);
    public static SettingKey<bool> Desc(string k) => new($"library.{k}.desc", false);
    public static SettingKey<int> View(string k) => new($"library.{k}.view", 1);
    public static SettingKey<int> Size(string k) => new($"library.{k}.size", 1);
    public static SettingKey<string> Selected(string k) => new($"library.{k}.selected", "");
    // Artists-only: the discography (column 2) controls + the picked release (column 3).
    public static SettingKey<string> AlbumKey(string k) => new($"library.{k}.albumkey", "");
    public static SettingKey<int> AlbumSort(string k) => new($"library.{k}.album.sort", 0);
    public static SettingKey<bool> AlbumDesc(string k) => new($"library.{k}.album.desc", false);
    public static SettingKey<int> AlbumView(string k) => new($"library.{k}.album.view", 3);   // Grid — matches today's fixed grid
    public static SettingKey<int> AlbumSize(string k) => new($"library.{k}.album.size", 1);
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
