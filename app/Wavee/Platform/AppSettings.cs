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
    public static readonly SettingKey<int> RowDensity = new("detail.rowdensity", 1);   // 0 Compact · 1 Default · 2 Cozy · 3 Comfortable
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
