#if WAVEE_PLAYPLAY_LOCAL
using System;
using System.Collections.Generic;
using System.IO;
using Wavee;
using Wavee.PlayPlay;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Audio.Runtime;
using Xunit;

namespace Wavee.Tests.Audio;

public class PlayPlayLocalRuntimeTests
{
    [Fact]
    public void TryCreate_ReturnsFalseForMissingDll()
    {
        var asset = new Wavee.Backend.Audio.RuntimeAsset(
            Path.Combine(Path.GetTempPath(), "wavee-missing-" + Guid.NewGuid().ToString("N"), "Spotify.dll"),
            A.Config(),
            "test-x64");
        var logs = new List<string>();

        var ok = PlayPlayRuntime.TryCreate(asset, out var runtime, logs.Add);

        Assert.False(ok);
        Assert.Null(runtime);
    }

    [Fact]
    public void Locator_NeverAutoReturnsInstalledSpotify()
    {
        var settings = new FakeSettings();
        var candidates = PlayPlayRuntimeLocator.EnumerateCandidates(settings);
        Assert.DoesNotContain(candidates, c =>
            c.DllPath.Contains(Path.Combine("Spotify", "Spotify.dll"), StringComparison.OrdinalIgnoreCase)
            && c.Source != PlayPlayRuntimeLocateSource.EnvironmentOverride);
    }
}

sealed class FakeSettings : IAppSettings
{
    public T Get<T>(SettingKey<T> key) => key.Default;
    public void Set<T>(SettingKey<T> key, T value) { }
}
#endif
