using System;
using System.IO;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The license lifecycle as a <see cref="ProtectedVideoSession"/> lives it (F045, F008, F011): a key that dies mid-play ends
/// the session as a failed license, an output restriction is recorded without failing anything, and an attach never binds a
/// license handle the native table no longer holds. Fakes only — the same <see cref="FakeRuntimeNative"/> /
/// <see cref="FakeSessionNative"/> pair the session tests use; no DLL, no CDM, no GPU, no network.
/// </summary>
public sealed class ProtectedLicenseLifecycleTests
{
    private static readonly byte[] Pssh = { 7, 7, 7, 7 };
    private const string Kid = "0123456789abcdef0123456789abcdef";
    private const string OtherKid = "fedcba9876543210fedcba9876543210";

    private sealed class Rig : IDisposable
    {
        public readonly FakeRuntimeNative Runtime = new();
        public readonly FakeSessionNative Sessions = new();
        public readonly string StorePath = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        public readonly ProtectedVideoRuntime Rt;

        public Rig() => Rt = new ProtectedVideoRuntime(Runtime, StorePath, idleMs: 60_000, sessionNative: Sessions);

        public void Dispose()
        {
            Rt.Dispose();
            try { Directory.Delete(StorePath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ProtectedVideoRequest Request(string kid = Kid)
        => new()
        {
            InitUrl = "https://cdn.test/v/init.mp4",
            SegmentBaseUrl = "https://cdn.test/v/",
            SegmentPrefix = "seg_",
            SegmentSuffix = ".m4s",
            StartNumber = 0,
            SegmentStride = 4,
            SegmentLengthMs = 4_000,
            DurationMs = 200_000,
            Pssh = Pssh,
            DefaultKid = kid,
            StartPaused = true,
            LicenseRelay = _ => System.Threading.Tasks.ValueTask.FromResult(new LicenseResponse(new byte[] { 1 })),
        };

    private static int DeadHr(int status) => unchecked((int)(0x80048000u + (uint)status));

    [Fact]
    public void AKeyRevokedMidPlay_EndsTheSessionAsAFailedLicense_WithTheCdmsWords()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = ProtectedVideoSession.Create(rig.Rt, Request());
        s.Start(s.Request);
        ulong lic = rig.Rt.LicenseHandleFor(Kid);
        rig.Rt.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid);
        rig.Sessions.Snapshot.State = PrNative.StatePlaying;

        rig.Rt.OnNativeEvent(lic, PrNative.EvLicenseRevoked, DeadHr(6), 6, Kid);   // MF_MEDIAKEY_STATUS_RELEASED
        s.Pump(default);

        Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
        Assert.Equal(ProtectedVideoPhase.Failed, s.Phase);
        Assert.Contains("revoked", s.Error.Peek());
        Assert.Contains("80048006", s.Error.Peek());
    }

    [Fact]
    public void ARevocationOfAnotherKidsKey_LeavesThisSessionPlaying()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = ProtectedVideoSession.Create(rig.Rt, Request());
        s.Start(s.Request);
        rig.Rt.OnNativeEvent(rig.Rt.LicenseHandleFor(Kid), PrNative.EvLicenseUsable, 5, 0, Kid);
        rig.Rt.EnsureLicense(Pssh, OtherKid, relay: null);
        ulong other = rig.Rt.LicenseHandleFor(OtherKid);
        rig.Rt.OnNativeEvent(other, PrNative.EvLicenseUsable, 5, 0, OtherKid);
        rig.Sessions.Snapshot.State = PrNative.StatePlaying;

        rig.Rt.OnNativeEvent(other, PrNative.EvLicenseRevoked, DeadHr(5), 5, OtherKid);
        s.Pump(default);

        Assert.NotEqual(ProtectedVideoState.Error, s.State.Peek());
        Assert.Null(s.Error.Peek());
        Assert.Equal(LicenseCacheState.Usable, rig.Rt.LicenseStateFor(Kid));
    }

    [Fact]
    public void AnOutputRestriction_IsRecordedOnTheSessionDecodingWithThatKey_AndNeverFailsPlayback()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = ProtectedVideoSession.Create(rig.Rt, Request());
        s.Start(s.Request);
        ulong lic = rig.Rt.LicenseHandleFor(Kid);
        rig.Rt.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid);
        rig.Sessions.Snapshot.State = PrNative.StatePlaying;
        Assert.Equal(0, s.LicenseRestriction);

        rig.Rt.OnNativeEvent(lic, PrNative.EvLicenseRestricted, 7, 0, Kid);   // OUTPUT_RESTRICTED
        s.Pump(default);
        Assert.Equal(7, s.LicenseRestriction);
        Assert.NotEqual(ProtectedVideoState.Error, s.State.Peek());

        rig.Rt.OnNativeEvent(lic, PrNative.EvLicenseRestricted, 2, 7, Kid);   // downscaled instead
        Assert.Equal(2, s.LicenseRestriction);

        rig.Rt.OnNativeEvent(lic, PrNative.EvLicenseRestricted, 0, 2, Kid);   // lifted
        Assert.Equal(0, s.LicenseRestriction);
        Assert.Null(s.Error.Peek());
    }

    [Fact]
    public void ARestrictionOnAnotherKidsKey_IsNotRecordedHere()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = ProtectedVideoSession.Create(rig.Rt, Request());
        s.Start(s.Request);
        rig.Rt.EnsureLicense(Pssh, OtherKid, relay: null);
        ulong other = rig.Rt.LicenseHandleFor(OtherKid);

        rig.Rt.OnNativeEvent(other, PrNative.EvLicenseRestricted, 7, 0, OtherKid);

        Assert.Equal(0, s.LicenseRestriction);
    }

    [Fact]
    public void AnAttachNeverBindsALicenseHandleNativeHasClosed_ItReacquiresAndBindsTheFreshOne()
    {
        using var rig = new Rig();
        rig.Rt.EnsureLicense(Pssh, Kid, Request().LicenseRelay);
        ulong stale = rig.Rt.LicenseHandleFor(Kid);
        rig.Rt.OnNativeEvent(stale, PrNative.EvLicenseUsable, 5, 0, Kid);
        rig.Runtime.NativeCloses(stale);   // the native LRU closed it; no event has reached the managed side

        using ProtectedVideoSession s = ProtectedVideoSession.Create(rig.Rt, Request());
        s.Start(s.Request);

        ulong fresh = rig.Rt.LicenseHandleFor(Kid);
        Assert.NotEqual(0UL, fresh);
        Assert.NotEqual(stale, fresh);
        Assert.Equal(2, rig.Runtime.AcquireCount);
        Assert.Contains(stale, rig.Runtime.Released);
        string attach = Array.Find(rig.Sessions.Calls, c => c.StartsWith("attach:", StringComparison.Ordinal))!;
        Assert.EndsWith(":" + fresh, attach);   // the attach carries the fresh handle, never the dead one
    }

    [Fact]
    public void AnAttachOnALicenseNativeStillHolds_ReusesItWithNoSecondAcquire()
    {
        using var rig = new Rig();
        rig.Rt.EnsureLicense(Pssh, Kid, Request().LicenseRelay);
        ulong lic = rig.Rt.LicenseHandleFor(Kid);
        rig.Rt.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid);

        using ProtectedVideoSession s = ProtectedVideoSession.Create(rig.Rt, Request());
        s.Start(s.Request);

        Assert.Equal(lic, rig.Rt.LicenseHandleFor(Kid));
        Assert.Equal(1, rig.Runtime.AcquireCount);
        string attach = Array.Find(rig.Sessions.Calls, c => c.StartsWith("attach:", StringComparison.Ordinal))!;
        Assert.EndsWith(":" + lic, attach);
    }
}
