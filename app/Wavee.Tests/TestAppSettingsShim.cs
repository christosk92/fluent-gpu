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
    public static readonly SettingKey<long> AudioBodyCacheBudgetBytes = new("audio.cache.body.budgetBytes", 4L << 30);
}
