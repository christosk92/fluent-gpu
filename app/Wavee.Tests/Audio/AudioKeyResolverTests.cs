using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class AudioKeyResolverTests
{
    static AudioKeyResolver Make(FakeApKeySource ap, FakeDeriver? der, FakeLicense? lic, Func<RuntimeAsset?> runtime, AudioRuntimeStatusService status)
        => new(ap, der, runtime, lic, status, A.Ctx);

    [Fact]
    public async Task ApSuccess_ReturnsApKey_WithoutPlayPlay()
    {
        var ap = new FakeApKeySource { Key = A.Key16(1) };
        var der = new FakeDeriver(); var lic = new FakeLicense();
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        var key = await r.GetKeyAsync(A.File20(), A.Gid16());

        Assert.Equal(A.Key16(1), key.ToArray());
        Assert.Equal(1, ap.Calls);
        Assert.Equal(0, lic.Calls);   // PlayPlay never consulted when AP works
        Assert.Equal(0, der.Calls);
    }

    [Fact]
    public async Task ApRefused_FallsBackToPlayPlay()
    {
        var ap = new FakeApKeySource { Throw = new IOException("ap refused") };
        var lic = new FakeLicense { Obf = new byte[16] };
        var der = new FakeDeriver { Key = A.Key16(7) };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        var key = await r.GetKeyAsync(A.File20(), A.Gid16());

        Assert.Equal(A.Key16(7), key.ToArray());
        Assert.Equal(1, lic.Calls);
        Assert.Equal(1, der.Calls);
    }

    [Fact]
    public async Task NoDeriver_ThrowsNeverProvisioned()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var r = Make(ap, der: null, lic: null, A.Asset, new AudioRuntimeStatusService());

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(AudioKeyFailureReason.NeverProvisioned, ex.Reason);
    }

    [Fact]
    public async Task DeriverButNoPack_ThrowsProvisioningUnavailable()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var r = Make(ap, new FakeDeriver(), new FakeLicense(), runtime: () => null, new AudioRuntimeStatusService());

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(AudioKeyFailureReason.ProvisioningUnavailable, ex.Reason);
    }

    [Fact]
    public async Task License403_IsTyped_AndSetsStatus()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Reason = AudioKeyFailureReason.License403 };
        var status = new AudioRuntimeStatusService();
        var r = Make(ap, new FakeDeriver(), lic, A.Asset, status);

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));

        Assert.Equal(AudioKeyFailureReason.License403, ex.Reason);
        Assert.Equal(AudioKeyFailureReason.License403, status.Reason);   // surfaced, not silent
        Assert.True(status.HasIssue);
    }

    [Fact]
    public async Task DeriveEmulationFault_IsTyped()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Obf = new byte[16] };
        var der = new FakeDeriver { Reason = AudioKeyFailureReason.EmulationFault };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(AudioKeyFailureReason.EmulationFault, ex.Reason);
    }

    [Fact]
    public async Task Latch_SkipsPlayPlay_AfterAFailure_UntilProbeWindow()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Reason = AudioKeyFailureReason.License403 };
        var r = Make(ap, new FakeDeriver(), lic, A.Asset, new AudioRuntimeStatusService());

        await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(1, lic.Calls);

        // Second immediate attempt: the per-file latch is engaged → PlayPlay is skipped, AP-only → ApPermanent.
        var ex2 = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(1, lic.Calls);   // NOT consulted again
        Assert.Equal(AudioKeyFailureReason.ApPermanent, ex2.Reason);
    }

    [Fact]
    public async Task ConcurrentRequests_ForSameFile_Coalesce_ToOneApCall()
    {
        var ap = new FakeApKeySource { Key = A.Key16(3), DelayMs = 80 };
        var r = Make(ap, new FakeDeriver(), new FakeLicense(), A.Asset, new AudioRuntimeStatusService());

        var file = A.File20(); var gid = A.Gid16();
        var tasks = Enumerable.Range(0, 6).Select(_ => r.GetKeyAsync(file, gid)).ToArray();
        var keys = await Task.WhenAll(tasks);

        Assert.Equal(1, ap.Calls);   // Resource in-flight dedup
        Assert.All(keys, k => Assert.Equal(A.Key16(3), k.ToArray()));
    }
}
