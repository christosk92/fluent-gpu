using System.Runtime.InteropServices;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Audio.Runtime;
using Xunit;

namespace Wavee.Tests.Audio;

public class PlayPlayRuntimeVerifierTests
{
    [Fact]
    public void HashMismatch_ReturnsHashMismatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-ppv-" + Guid.NewGuid().ToString("N"));
        try
        {
            PlayPlayRuntimeTestFixtures.WriteRuntimeDir(dir, PlayPlayRuntimeTestFixtures.SampleManifest());
            var manifestPath = Path.Combine(dir, "playplay-runtime.json");
            var json = File.ReadAllText(manifestPath);
            var bad = json.Replace(PlayPlayRuntimeTestFixtures.SampleManifest().DllSha256, new string('b', 64));
            File.WriteAllText(manifestPath, bad);

            var result = PlayPlayRuntimeVerifier.VerifyDirectory(dir, allowUntrustedSignature: false);
            Assert.Equal(ProvisioningOutcome.HashMismatch, result.Outcome);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void UntrustedSignature_RequiresExplicitAllow()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = Path.Combine(Path.GetTempPath(), "wavee-ppv-" + Guid.NewGuid().ToString("N"));
        try
        {
            PlayPlayRuntimeTestFixtures.WriteRuntimeDir(dir, PlayPlayRuntimeTestFixtures.SampleManifest());
            var strict = PlayPlayRuntimeVerifier.VerifyDirectory(dir, allowUntrustedSignature: false);
            if (strict.SignatureTrust != SignatureTrust.Untrusted) return; // signed test PE unlikely
            Assert.False(strict.Ok);
            Assert.True(strict.NeedsUntrustedConfirmation);
            var allow = PlayPlayRuntimeVerifier.VerifyDirectory(dir, allowUntrustedSignature: true);
            Assert.True(allow.Ok);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
