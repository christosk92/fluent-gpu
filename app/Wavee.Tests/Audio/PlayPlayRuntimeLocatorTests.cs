using Wavee.SpotifyLive.Audio.Runtime;
using Xunit;

namespace Wavee.Tests.Audio;

public class PlayPlayRuntimeLocatorTests
{
    [Fact]
    public void Precedence_EnvOverridesSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-loc-" + Guid.NewGuid().ToString("N"));
        var settingsDir = Path.Combine(Path.GetTempPath(), "wavee-loc2-" + Guid.NewGuid().ToString("N"));
        var old = Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_SPOTIFY_DLL");
        try
        {
            PlayPlayRuntimeTestFixtures.WriteRuntimeDir(dir, PlayPlayRuntimeTestFixtures.SampleManifest());
            PlayPlayRuntimeTestFixtures.WriteRuntimeDir(settingsDir, PlayPlayRuntimeTestFixtures.SampleManifest());

            var settings = new LocatorFakeSettings();
            settings.Set(WaveeSettings.PlaybackRuntimePath, settingsDir);

            Environment.SetEnvironmentVariable("WAVEE_PLAYPLAY_SPOTIFY_DLL", Path.Combine(dir, "Spotify.dll"));
            var best = PlayPlayRuntimeLocator.FindBest(settings);
            Assert.NotNull(best);
            Assert.Equal(PlayPlayRuntimeLocateSource.EnvironmentOverride, best!.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAVEE_PLAYPLAY_SPOTIFY_DLL", old);
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(settingsDir, true); } catch { }
        }
    }

    [Fact]
    public void NeverAutoReturnsInstalledSpotify()
    {
        var settings = new LocatorFakeSettings();
        var candidates = PlayPlayRuntimeLocator.EnumerateCandidates(settings);
        Assert.DoesNotContain(candidates, c =>
            c.Source != PlayPlayRuntimeLocateSource.EnvironmentOverride
            && c.DllPath.Contains(Path.Combine("Spotify", "Spotify.dll"), StringComparison.OrdinalIgnoreCase));
    }
}

sealed class LocatorFakeSettings : IAppSettings
{
    readonly Dictionary<string, object> _vals = new();
    public T Get<T>(SettingKey<T> key) =>
        _vals.TryGetValue(key.Name, out var v) && v is T t ? t : key.Default;
    public void Set<T>(SettingKey<T> key, T value) => _vals[key.Name] = value!;
}
