using System.Collections.Generic;

namespace Wavee;

// Minimal settings seam for source-included Runtime tests (the full AppSettings.cs pulls FluentGpu.WindowsApi).
public interface IAppSettings
{
    T Get<T>(SettingKey<T> key);
    void Set<T>(SettingKey<T> key, T value);
}

public sealed record SettingKey<T>(string Name, T Default);

static class WaveeSettings
{
    public static readonly SettingKey<string> PlaybackRuntimePath = new("playback.runtime.path", "");
    public static readonly SettingKey<string> PlaybackRuntimePackId = new("playback.runtime.packId", "");
    public static readonly SettingKey<bool> PlaybackRuntimeSetupDismissed = new("playback.runtime.dismissed", false);
    public static readonly SettingKey<string> PlaybackRuntimeCatalogUrl = new("playback.runtime.catalogUrl", "");
    public static readonly SettingKey<bool> AudioBodyCacheEnabled = new("audio.cache.body.enabled", true);
    public static readonly SettingKey<bool> AudioKeyCacheEnabled = new("audio.cache.keys.enabled", true);
    public static readonly SettingKey<int> AudioBodyCacheBudgetMode = new("audio.cache.body.budgetMode", 1);
    public static readonly SettingKey<long> AudioBodyCacheBudgetBytes = new("audio.cache.body.budgetBytes", 32L << 30);
    public static readonly SettingKey<int> AudioBodyCacheBudgetPercent = new("audio.cache.body.budgetPercent", 0);
    public static readonly SettingKey<string> AudioBodyCacheBasePath = new("audio.cache.body.basePath", "");
    public static readonly SettingKey<string> LastSeenCrashDumpPath = new("diagnostics.crash.lastDumpPath", "");
    public static readonly SettingKey<long> LastSeenCrashDumpTicksUtc = new("diagnostics.crash.lastDumpTicksUtc", 0L);

    // ── sidebar (F.3.1) — MIRRORS src/apps/Wavee/Platform/AppSettings.cs VERBATIM ─────────────────────────────────────
    // Storage names and defaults must match the production keys exactly: the sidebar tests assert against these names and
    // the bootstrap/preferences code under test reads them by name. If you change one there, change it here.
    // Legacy v0 global pane keys — read only by the v0→v1 migration (SidebarBootstrap.MigrateLegacyPaneKeys).
    public static readonly SettingKey<float> SidebarWidth = new("sidebar.width", 300f);
    public static readonly SettingKey<bool> SidebarWidthUserSet = new("sidebar.width.userSet", false);
    public static readonly SettingKey<bool> SidebarCollapsed = new("sidebar.collapsed", false);
    public static readonly SettingKey<int> SidebarDesign = new("sidebar.design", 0);
    public static readonly SettingKey<bool> SidebarOnboardingSeen = new("sidebar.onboarding.seen", false);
    public static readonly SettingKey<int> SidebarBootstrapVersion = new("sidebar.bootstrap.version", 0);
}

// The per-design sidebar keys (F.3.1), mirroring the production SidebarKeys. Depends on SidebarDesignInfo.Slug/Tiers —
// so Features/Sidebar/SidebarDesign.cs must be source-included by Wavee.Tests.csproj alongside these tests.
static class SidebarKeys
{
    public static SettingKey<float> Width(SidebarDesign d)
        => new($"sidebar.{SidebarDesignInfo.Slug(d)}.width", SidebarDesignInfo.Tiers(d).Narrow);
    public static SettingKey<bool> WidthUserSet(SidebarDesign d)
        => new($"sidebar.{SidebarDesignInfo.Slug(d)}.width.userSet", false);
    public static SettingKey<bool> Collapsed(SidebarDesign d)
        => new($"sidebar.{SidebarDesignInfo.Slug(d)}.collapsed", false);

    public static readonly SettingKey<bool> ClassicPinnedOpen = new("sidebar.classic.section.pinned", true);
    public static readonly SettingKey<bool> ClassicLibraryOpen = new("sidebar.classic.section.library", true);
    public static readonly SettingKey<bool> ClassicPlaylistsOpen = new("sidebar.classic.section.playlists", true);

    public static readonly SettingKey<int> V3Filter = new("sidebar.v3.filter", 0);
    public static readonly SettingKey<int> V3Qualifier = new("sidebar.v3.qualifier", 0);
    public static readonly SettingKey<int> V3Sort = new("sidebar.v3.sort", 0);
    public static readonly SettingKey<bool> V3Desc = new("sidebar.v3.desc", false);
    public static readonly SettingKey<int> V3View = new("sidebar.v3.view", 1);
    public static readonly SettingKey<int> V3GridSize = new("sidebar.v3.size", 1);
    public static readonly SettingKey<bool> V3SearchOpen = new("sidebar.v3.search.open", false);

    public static readonly SettingKey<string> CuratedTemplateId = new("sidebar.curated.template", "wavee.curated.default");
    public static readonly SettingKey<bool> CuratedRailLabels = new("sidebar.curated.rail.labels", false);
}

/// <summary>An in-memory <see cref="IAppSettings"/> for tests: no registry, no file, no defaults magic beyond the key's
/// own. Shared by every sidebar test (bootstrap, preferences, settings-page models) so they all agree on the seam.</summary>
public sealed class MemoryAppSettings : IAppSettings
{
    readonly Dictionary<string, object> _values = new();

    public T Get<T>(SettingKey<T> key) =>
        _values.TryGetValue(key.Name, out var value) && value is T typed ? typed : key.Default;

    public void Set<T>(SettingKey<T> key, T value) { if (value is not null) _values[key.Name] = value; }

    /// <summary>True when the key has been WRITTEN (the real IAppSettings has no such probe — tests use it to assert that
    /// a code path deliberately did NOT write a key, e.g. "an existing install must not stomp sidebar.design").</summary>
    public bool WasWritten<T>(SettingKey<T> key) => _values.ContainsKey(key.Name);

    /// <summary>Number of distinct keys written — for "the bootstrap is idempotent" style assertions.</summary>
    public int WrittenCount => _values.Count;
}
