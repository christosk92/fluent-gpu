using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using FluentGpu.Signals;

namespace FluentGpu.WindowsApi.Storage;

/// <summary>
/// A reactive, signal-backed view over an <see cref="AppDataStore"/> — the "improve, don't port" leg of the Storage
/// pillar. Each setting is a <see cref="Signal{T}"/> seeded from disk and WRITE-THROUGH-PERSISTED: a change to the signal
/// (from a bound control, code, etc.) is written back to the registry on the next reactive flush, with no explicit save
/// call. So an app binds a toggle/textbox straight to <c>settings.Bool("muted")</c> and it both restores and persists.
///
/// <para>Each key is cached one-Signal-per-key (a second <c>Bool("muted")</c> returns the same signal). The persist
/// hook is a free-standing <see cref="Effect"/> owned for the store's lifetime — so a <see cref="SettingsStore"/> is
/// intended to be a long-lived (process-scoped) object, not created-and-discarded per component (each instance roots
/// its persist effects). Use <see cref="AppDataStore"/> directly for one-shot typed reads/writes.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsStore
{
    private readonly AppDataStore _store;
    private readonly ReactiveRuntime _runtime;
    private readonly Dictionary<string, object> _cache = new(StringComparer.Ordinal);

    private SettingsStore(AppDataStore store, ReactiveRuntime runtime) { _store = store; _runtime = runtime; }

    /// <summary>A reactive store over <c>HKCU\Software\{publisher}\{product}\Settings</c>. The <paramref name="runtime"/>
    /// is the host reactive runtime (a component's <c>Context.Runtime</c>) that drives the write-through persist effects.</summary>
    public static SettingsStore ForUnpackaged(string publisher, string product, ReactiveRuntime runtime)
        => new(AppDataStore.ForUnpackaged(publisher, product), runtime ?? throw new ArgumentNullException(nameof(runtime)));

    /// <summary>The underlying typed container (one-shot reads/writes, folders, Keys/Clear).</summary>
    public AppDataStore Store => _store;

    /// <summary>A persisted string setting (REG_SZ), seeded from disk or <paramref name="fallback"/>.</summary>
    public Signal<string> String(string key, string fallback = "") => Bind(key, _store.GetString(key, fallback) ?? fallback, v => _store.SetString(key, v));
    /// <summary>A persisted bool setting (REG_DWORD).</summary>
    public Signal<bool> Bool(string key, bool fallback = false) => Bind(key, _store.GetBool(key, fallback), v => _store.SetBool(key, v));
    /// <summary>A persisted int setting (REG_DWORD).</summary>
    public Signal<int> Int(string key, int fallback = 0) => Bind(key, _store.GetInt(key, fallback), v => _store.SetInt(key, v));
    /// <summary>A persisted long setting (REG_QWORD).</summary>
    public Signal<long> Long(string key, long fallback = 0) => Bind(key, _store.GetLong(key, fallback), v => _store.SetLong(key, v));
    /// <summary>A persisted double setting (REG_QWORD via the IEEE-754 bit pattern — exact round-trip).</summary>
    public Signal<double> Double(string key, double fallback = 0) => Bind(key, _store.GetDouble(key, fallback), v => _store.SetDouble(key, v));

    private Signal<T> Bind<T>(string key, T initial, Action<T> persist)
    {
        if (_cache.TryGetValue(key, out object? existing)) return (Signal<T>)existing;
        var sig = new Signal<T>(initial);
        // Write-through: an effect re-runs whenever the signal changes and persists it. The FIRST run (the initial
        // load we just seeded from disk) is skipped so opening the store does not re-write every value.
        bool primed = false;
        _ = new Effect(_runtime, () => { T v = sig.Value; if (!primed) { primed = true; return; } persist(v); });
        _cache[key] = sig;
        return sig;
    }
}
