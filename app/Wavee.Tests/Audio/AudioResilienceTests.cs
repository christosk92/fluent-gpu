using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class ProvisionerTests
{
    const string ManifestUrl = "https://cproducts.dev/r/manifest.json";
    const string PackUrl = "https://cdn/pack.bin";

    static string TempRoot() => Path.Combine(Path.GetTempPath(), "wavee-test-packs-" + Guid.NewGuid().ToString("N"));
    static byte[] ManifestBytes(params RuntimeManifestPack[] packs) =>
        System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new RuntimeManifest { Version = 1, Packs = packs }, RuntimeManifestJsonContext.Default.RuntimeManifest));

    static AudioRuntimeProvisioner Make(FakeHttpExchange http, AudioRuntimeStatusService status) =>
        new(http, status, log: null, packRoot: TempRoot());

    [Fact]
    public async Task ManifestUnavailable_IsTyped()
    {
        var status = new AudioRuntimeStatusService();
        var http = new FakeHttpExchange { Responder = _ => new HttpResp(404, new Dictionary<string, string>(), Array.Empty<byte>()) };
        var asset = await Make(http, status).ProvisionAsync();

        Assert.Null(asset);
        Assert.Equal(ProvisioningOutcome.ManifestUnavailable, status.Provisioning);
    }

    [Fact]
    public async Task HashMismatch_IsTyped_AndNotClobberedByArchFallback()
    {
        var packBytes = A.Bytes(4, 64);
        var wrongSha = Convert.ToHexString(new byte[32]);   // deliberately not the real hash
        var pack = A.Pack(wrongSha, PackUrl);
        var status = new AudioRuntimeStatusService();
        var http = new FakeHttpExchange
        {
            Responder = req => req.Url == ManifestUrl
                ? new HttpResp(200, new Dictionary<string, string>(), ManifestBytes(pack))
                : new HttpResp(200, new Dictionary<string, string>(), packBytes),
        };

        var asset = await Make(http, status).ProvisionAsync();

        Assert.Null(asset);
        // The specific failure must survive — this regressed before the fix (the arch-fallback overwrote it).
        Assert.Equal(ProvisioningOutcome.HashMismatch, status.Provisioning);
    }

    [Fact]
    public async Task ArchFiltered_WhenOnlyArm64Pack()
    {
        var pack = A.Pack(Convert.ToHexString(new byte[32]), PackUrl, arch: "Arm64");
        var status = new AudioRuntimeStatusService();
        var http = new FakeHttpExchange
        {
            Responder = req => new HttpResp(200, new Dictionary<string, string>(), req.Url == ManifestUrl ? ManifestBytes(pack) : Array.Empty<byte>()),
        };

        var asset = await Make(http, status).ProvisionAsync();

        Assert.Null(asset);
        Assert.Equal(ProvisioningOutcome.ArchUnsupported, status.Provisioning);
    }

    [Fact]
    public async Task ValidPack_Provisions_AndWritesVerifiedDll()
    {
        var packBytes = A.Bytes(9, 128);
        var sha = Convert.ToHexString(SHA256.HashData(packBytes));
        var pack = A.Pack(sha, PackUrl);
        var status = new AudioRuntimeStatusService();
        var http = new FakeHttpExchange
        {
            Responder = req => req.Url == ManifestUrl
                ? new HttpResp(200, new Dictionary<string, string>(), ManifestBytes(pack))
                : new HttpResp(200, new Dictionary<string, string>(), packBytes),
        };

        var asset = await Make(http, status).ProvisionAsync();

        Assert.NotNull(asset);
        Assert.Equal(ProvisioningOutcome.Ready, status.Provisioning);
        Assert.True(File.Exists(asset!.PackPath));
        Assert.Equal(packBytes, File.ReadAllBytes(asset.PackPath));   // exactly the verified bytes on disk
    }

    [Fact]
    public async Task BrotliPack_IsDecompressed_ThenVerified()
    {
        var raw = A.Bytes(2, 200);
        var sha = Convert.ToHexString(SHA256.HashData(raw));      // hash is of the DECOMPRESSED bytes
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var br = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true)) br.Write(raw);
            compressed = ms.ToArray();
        }
        var pack = A.Pack(sha, PackUrl, comp: "brotli");
        var status = new AudioRuntimeStatusService();
        var http = new FakeHttpExchange
        {
            Responder = req => req.Url == ManifestUrl
                ? new HttpResp(200, new Dictionary<string, string>(), ManifestBytes(pack))
                : new HttpResp(200, new Dictionary<string, string>(), compressed),
        };

        var asset = await Make(http, status).ProvisionAsync();

        Assert.NotNull(asset);
        Assert.Equal(ProvisioningOutcome.Ready, status.Provisioning);
        Assert.Equal(raw, File.ReadAllBytes(asset!.PackPath));
    }
}

public class PlaybackErrorPathTests
{
    [Fact]
    public async Task LocalPlay_ResolveFailure_SurfacesTypedError_NotSilentDrop()
    {
        var host = new SilentAudioHost();
        var proj = new NowPlayingProjection("dev");
        var resolver = new ThrowingResolver(AudioKeyFailureReason.License403);
        var controller = new PlaybackController(host, resolver, proj, EmptyContextResolver.Instance, "dev");
        PlaybackErrorInfo? surfaced = null;
        controller.OnPlaybackError = m => surfaced = m;

        await controller.PlayTrackAsync("spotify:track:abc");   // must not throw; must report

        Assert.Equal(1, resolver.Calls);
        Assert.Equal(AudioKeyFailureReason.License403, surfaced?.Reason);
        Assert.Equal(AudioKeyFailureReason.License403.ToUserMessage(), surfaced?.UserMessage);
        controller.Dispose();
    }

    [Fact]
    public async Task RetryCurrent_ReResolves()
    {
        var host = new SilentAudioHost();
        var proj = new NowPlayingProjection("dev");
        var resolver = new ThrowingResolver(AudioKeyFailureReason.Network);
        var controller = new PlaybackController(host, resolver, proj, EmptyContextResolver.Instance, "dev");
        int errors = 0;
        controller.OnPlaybackError = _ => errors++;

        await controller.PlayTrackAsync("spotify:track:abc");
        await controller.RetryCurrentAsync();

        Assert.Equal(2, resolver.Calls);   // retry actually re-attempts
        Assert.Equal(2, errors);
        controller.Dispose();
    }
}

public class ProjectionPrebufferingTests
{
    [Fact]
    public void Prebuffering_ReadsAsBuffering_ThenClearsOnPlaying()
    {
        var proj = new NowPlayingProjection("dev");

        proj.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
        Assert.True(proj.IsBuffering);       // instant-start head window shows the buffering edge
        Assert.True(proj.IsPrebuffering);

        proj.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Playing, 100));
        Assert.False(proj.IsBuffering);
        Assert.True(proj.IsPlaying);
        proj.Dispose();
    }
}

public class StatusServiceTests
{
    [Fact]
    public void KeyFailure_SetsIssue_FiresChanged_ThenClears()
    {
        var s = new AudioRuntimeStatusService();
        int changed = 0;
        s.Changed += () => changed++;

        Assert.False(s.HasIssue);
        s.SetKeyFailure(AudioKeyFailureReason.Network, "x");
        Assert.True(s.HasIssue);
        Assert.Equal(AudioKeyFailureReason.Network, s.Reason);

        s.ClearKeyFailure();
        Assert.False(s.HasIssue);
        Assert.True(changed >= 2);
    }
}

public class KeyValidationTests
{
    [Fact]
    public void ValidateKeyOnBodyRange_TrueForRightKey_FalseForWrong()
    {
        var key = A.Key16(1);
        var plain = new byte[0xc0];
        "OggS"u8.CopyTo(plain.AsSpan(SpotifyAesCtr.OggMagicOffset));
        var cipher = SpotifyAesCtr.Decrypt(plain, key, 0);   // CTR symmetric → ciphertext of the OggS-bearing page

        Assert.True(SpotifyAesCtr.ValidateKeyOnBodyRange(cipher, key));
        Assert.False(SpotifyAesCtr.ValidateKeyOnBodyRange(cipher, A.Key16(2)));   // wrong key → garbage → rejected
    }
}
