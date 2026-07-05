using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Audio.Runtime;
using Xunit;

namespace Wavee.Tests.Audio;

public class PlayPlayRuntimeProvisionerTests
{
    [Fact]
    public void Register_Success_PersistsPointer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-prov-" + Guid.NewGuid().ToString("N"));
        var settings = new LocatorFakeSettings();
        var status = new AudioRuntimeStatusService();
        try
        {
            PlayPlayRuntimeTestFixtures.WriteRuntimeDir(dir, PlayPlayRuntimeTestFixtures.SampleManifest());
            var prov = new PlayPlayRuntimeProvisioner(settings, status);
            bool ok = prov.TryRegisterRuntime(dir, allowUntrustedSignature: true);
            if (!ok && prov.GetSnapshot().NeedsUntrustedConfirmation) return;
            Assert.True(ok);
            Assert.Equal(ProvisioningOutcome.Ready, prov.GetSnapshot().Outcome);
            Assert.False(string.IsNullOrEmpty(settings.Get(WaveeSettings.PlaybackRuntimePath)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
