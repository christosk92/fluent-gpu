using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// How the protected runtime survives its own failures (F009, F010, F021), over <see cref="FakeRuntimeNative"/> +
/// <see cref="FakeSessionNative"/>: the idle teardown that stands down for a runtime somebody wants, a POISONED runtime (bring-up
/// failed, a device removed/reset, a hardware-DRM context reset) that is replaced by the next acquire even while a keep-alive
/// token holds it, the typed retryable error every live session gets with the HRESULT, and the renderer's adapter LUID that
/// reaches the native create. No DLL, no CDM, no GPU, no window; every wait is a bounded await.
/// </summary>
public sealed class ProtectedRuntimeRecoveryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly byte[] Pssh = { 9, 9, 9, 9 };
    private const string Kid = "0123456789abcdef0123456789abcdef";
    private static readonly int EFail = unchecked((int)0x80004005);
    private static readonly int EHandle = unchecked((int)0x80070006);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IDisposable
    {
        public readonly FakeRuntimeNative Native = new();
        public readonly FakeSessionNative Sessions = new();
        public readonly string StorePath = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        public readonly ProtectedVideoRuntime Runtime;
        /// <summary>What the renderer's adapter provider answers right now (packed LUID, 0 = none).</summary>
        public long Luid;

        /// <summary><paramref name="cooldownMs"/> is the bring-up retry cooldown (0 here, so a rebuild is immediate unless a test
        /// is about the cooldown).</summary>
        public Rig(int idleMs = 60_000, int cooldownMs = 0, long luid = 0)
        {
            Luid = luid;
            Runtime = new ProtectedVideoRuntime(Native, StorePath, idleMs, Sessions, () => Volatile.Read(ref Luid), cooldownMs);
        }

        public ProtectedVideoSession Session() => ProtectedVideoSession.Create(Runtime, Request());

        public void Dispose()
        {
            Runtime.Dispose();
            try { if (Directory.Exists(StorePath)) Directory.Delete(StorePath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ProtectedVideoRequest Request() => new()
    {
        InitUrl = "https://cdn.test/v/init.mp4",
        SegmentBaseUrl = "https://cdn.test/v/",
        SegmentPrefix = "seg_",
        SegmentSuffix = ".m4s",
        SegmentStride = 4,
        SegmentLengthMs = 4_000,
        DurationMs = 200_000,
        Pssh = Pssh,
        DefaultKid = Kid,
        StartPaused = true,
        LicenseRelay = _ => ValueTask.FromResult(new LicenseResponse(new byte[] { 1 })),
    };

    private static string KidOf(int i) => i.ToString("x32");

    // ── F009: the idle teardown ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnIdleTeardown_StandsDown_WhenAnAcquireLandedBeforeItsDetach_AndOnlyTheNextIdleWindowDestroys()
    {
        using var r = new Rig(idleMs: 150);
        Assert.True(r.Runtime.Acquire());
        int probes = 0;
        bool acquiredInWindow = false;
        r.Runtime.IdleTeardownProbe = () =>
        {
            if (Interlocked.Increment(ref probes) != 1) return;
            // A switch lands after the teardown decided to destroy and gives the runtime straight back: the reference is
            // already 0 again when the teardown looks, so only the wanted flag can say it was needed.
            acquiredInWindow = r.Runtime.Acquire();
            r.Runtime.Release();
        };

        r.Runtime.Release();   // the warm-idle window starts
        await r.Native.Destroyed.Task.WaitAsync(Bound, Ct);

        Assert.True(acquiredInWindow);
        Assert.Equal(2, Volatile.Read(ref probes));   // the first window stood down and re-armed; the second one destroyed
        Assert.Equal(1, r.Native.CreateCount);
        Assert.Equal(1, r.Native.DestroyCount);
        Assert.False(r.Runtime.IsRunning);
    }

    [Fact]
    public async Task AnAcquireDuringTheNativeDestroy_WaitsItOut_AndOnlyThenCreatesTheReplacement()
    {
        using var inDestroy = new ManualResetEventSlim(false);
        using var finishDestroy = new ManualResetEventSlim(false);
        using var r = new Rig(idleMs: 100);   // after the events: the runtime's own destroy at dispose still reaches them
        r.Native.DuringDestroy = _ => { inDestroy.Set(); finishDestroy.Wait(Bound); };
        Assert.True(r.Runtime.Acquire());
        ulong first = r.Native.LastRuntime;
        r.Runtime.Release();
        Assert.True(inDestroy.Wait(Bound, Ct));   // the idle teardown is inside the native join now

        Task<bool> racing = Task.Run(r.Runtime.Acquire, Ct);
        await Task.Delay(100, Ct);
        Assert.False(racing.IsCompleted);          // it is waiting for the destroy, not creating beside it
        Assert.Equal(1, r.Native.CreateCount);

        finishDestroy.Set();
        Assert.True(await racing.WaitAsync(Bound, Ct));

        Assert.Equal(2, r.Native.CreateCount);
        string[] calls = r.Native.Calls;
        Assert.True(Array.IndexOf(calls, "destroy:" + first) < Array.FindLastIndex(calls, c => c.StartsWith("create:", StringComparison.Ordinal)));
    }

    // ── F010: a poisoned runtime is replaced ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARuntimeFailedEvent_ThenAcquire_RecreatesTheRuntime_EvenWhileAKeepAliveTokenHoldsIt()
    {
        using var r = new Rig();
        Assert.True(r.Runtime.Acquire());
        IDisposable? keep = r.Runtime.TakeKeepAlive();
        Assert.NotNull(keep);
        ulong first = r.Native.LastRuntime;
        Assert.Equal(2, r.Runtime.References);

        r.Runtime.OnNativeEvent(0, PrNative.EvRuntimeFailed, EFail, 0, null);
        Assert.True(r.Runtime.Acquire());   // the dead runtime is not handed out

        Assert.Equal(2, r.Native.CreateCount);
        Assert.Equal(1, r.Native.DestroyCount);
        Assert.Contains("destroy:" + first, r.Native.Calls);
        Assert.NotEqual(first, r.Native.LastRuntime);
        Assert.False(r.Runtime.IsPoisoned);
        Assert.Null(r.Runtime.StartupError);
        Assert.Equal(3, r.Runtime.References);   // the keeper's reference and the first one stay counted, and now pin the new runtime
        keep!.Dispose();
    }

    [Fact]
    public void TakeKeepAlive_OnAPoisonedRuntime_BringsUpAFreshOne()
    {
        using var r = new Rig();
        using IDisposable? first = r.Runtime.TakeKeepAlive();
        Assert.NotNull(first);

        r.Runtime.OnNativeEvent(0, PrNative.EvRuntimeFailed, EFail, 0, null);
        using IDisposable? second = r.Runtime.TakeKeepAlive();

        Assert.NotNull(second);
        Assert.Equal(2, r.Native.CreateCount);
        Assert.Equal(1, r.Native.DestroyCount);
    }

    [Theory]
    [InlineData(unchecked((int)0x8004CD12))]   // DRM_E_TEE_INVALID_HWDRM_STATE
    [InlineData(unchecked((int)0x8004DD2E))]   // DRM_OEM_E_ASD_ACTIVE_DISPLAY_FAIL
    [InlineData(unchecked((int)0x887A0005))]   // DXGI_ERROR_DEVICE_REMOVED
    [InlineData(unchecked((int)0x887A0007))]   // DXGI_ERROR_DEVICE_RESET
    [InlineData(unchecked((int)0xC00D3E85))]   // MF_E_SHUTDOWN
    public void AResetClassSessionError_PoisonsTheRuntime_AndTheNextAcquireReplacesIt(int hr)
    {
        Assert.True(ProtectedRuntimeFaults.IsRuntimeReset(hr));
        using var r = new Rig(cooldownMs: 60_000);   // a reset of a WORKING runtime carries no bring-up cooldown
        Assert.True(r.Runtime.Acquire());

        r.Runtime.OnNativeEvent(99, PrNative.EvError, 3, hr, null);
        Assert.True(r.Runtime.Acquire());

        Assert.Equal(2, r.Native.CreateCount);
        Assert.Equal(1, r.Native.DestroyCount);
    }

    [Fact]
    public void AnOrdinarySessionError_DoesNotPoisonTheRuntime()
    {
        using var r = new Rig();
        Assert.True(r.Runtime.Acquire());
        Assert.False(ProtectedRuntimeFaults.IsRuntimeReset(EFail));

        r.Runtime.OnNativeEvent(99, PrNative.EvError, 3, EFail, null);

        Assert.False(r.Runtime.IsPoisoned);
        Assert.True(r.Runtime.Acquire());
        Assert.Equal(1, r.Native.CreateCount);
        Assert.Equal(0, r.Native.DestroyCount);
    }

    [Fact]
    public void AHardwareDrmResetThatSurfacesAsAFailedKeySession_PoisonsTheRuntime()
    {
        using var r = new Rig();
        r.Runtime.EnsureLicense(Pssh, KidOf(1), relay: null);
        ulong lic = r.Runtime.LicenseHandleFor(KidOf(1));
        Assert.NotEqual(0ul, lic);

        r.Runtime.OnNativeEvent(lic, PrNative.EvLicenseFailed, ProtectedRuntimeFaults.TeeInvalidHwDrmState, 0, KidOf(1));
        Assert.True(r.Runtime.Acquire());

        Assert.Equal(2, r.Native.CreateCount);
        Assert.Equal(0, r.Runtime.LicenseCount);   // the old key sessions went with the old runtime
    }

    [Fact]
    public void EnsureLicense_OnAPoisonedRuntime_ReplacesItFirst_SoTheKeySessionIsNotOpenedOnADeadCdm()
    {
        using var r = new Rig();
        r.Runtime.EnsureLicense(Pssh, KidOf(1), relay: null);
        r.Runtime.OnNativeEvent(99, PrNative.EvError, 3, ProtectedRuntimeFaults.DxgiDeviceRemoved, null);

        LicenseCacheState state = r.Runtime.EnsureLicense(Pssh, KidOf(2), relay: null);

        Assert.Equal(LicenseCacheState.Pending, state);
        Assert.Equal(2, r.Native.CreateCount);
        Assert.Equal(0ul, r.Runtime.LicenseHandleFor(KidOf(1)));
        Assert.NotEqual(0ul, r.Runtime.LicenseHandleFor(KidOf(2)));
    }

    [Fact]
    public async Task APoisonedRuntime_EndsEveryPendingBufferedWait()
    {
        using var r = new Rig();
        Assert.True(r.Runtime.Acquire());
        Task<bool> wait = r.Runtime.WaitBufferedAsync(7, new CancellationToken(canceled: false));

        r.Runtime.OnNativeEvent(8, PrNative.EvError, 3, ProtectedRuntimeFaults.DxgiDeviceReset, null);

        Assert.False(await wait.WaitAsync(Bound, Ct));
    }

    [Fact]
    public async Task APoisonedRuntime_IsDestroyedOffThread_WithoutWaitingForTheNextAcquire()
    {
        using var r = new Rig();
        Assert.True(r.Runtime.Acquire());
        ulong first = r.Native.LastRuntime;

        r.Runtime.OnNativeEvent(0, PrNative.EvRuntimeFailed, EFail, 0, null);
        await r.Native.Destroyed.Task.WaitAsync(Bound, Ct);

        Assert.Contains("destroy:" + first, r.Native.Calls);
        Assert.False(r.Runtime.IsRunning);
        Assert.Equal(1, r.Native.CreateCount);   // the replacement waits for a caller
    }

    [Fact]
    public void ABringUpFailure_IsNotRetriedInsideTheCooldown_AndSaysWhy()
    {
        using var r = new Rig(cooldownMs: 60_000);
        Assert.True(r.Runtime.Acquire());

        r.Runtime.OnNativeEvent(0, PrNative.EvRuntimeFailed, EFail, 0, null);

        Assert.False(r.Runtime.Acquire());        // a deterministic failure must not pay MFStartup + CDM + mfpmp.exe per open
        Assert.Equal(1, r.Native.CreateCount);
        Assert.Contains("80004005", r.Runtime.StartupError);
        Assert.Equal(EFail, r.Runtime.StartupHr);
        Assert.Equal(LicenseCacheState.Failed, r.Runtime.EnsureLicense(Pssh, KidOf(1), relay: null));
    }

    // ── F010: what live sessions are told ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryLiveSession_GetsATypedRetryableFailureCarryingTheHresult_EvenWhenItsNativeHandleIsGone()
    {
        using var r = new Rig();
        using ProtectedVideoSession failing = r.Session();
        using ProtectedVideoSession other = r.Session();

        r.Runtime.OnNativeEvent(failing.Handle, PrNative.EvError, 3, ProtectedRuntimeFaults.DxgiDeviceRemoved, null);

        foreach (ProtectedVideoSession s in new[] { failing, other })
        {
            s.Pump(default);
            Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
            Assert.Equal(ProtectedRuntimeFaults.DxgiDeviceRemoved, s.ErrorHr);
            Assert.True(s.ErrorNeedsRuntimeRebuild);
        }
        Assert.Contains("reset", other.Error.Peek());   // the other session never saw an error of its own

        // After the replacement the native handle is unknown to the new runtime: the snapshot fails, and the failure that
        // ended the session is still published instead of the pump returning in silence forever.
        using ProtectedVideoSession late = r.Session();
        r.Runtime.OnNativeEvent(late.Handle, PrNative.EvError, 3, ProtectedRuntimeFaults.TeeInvalidHwDrmState, null);
        r.Sessions.SnapshotHr = EHandle;
        late.Pump(default);
        Assert.Equal(ProtectedVideoState.Error, late.State.Peek());
        Assert.Equal(ProtectedRuntimeFaults.TeeInvalidHwDrmState, late.ErrorHr);
    }

    [Fact]
    public void ASessionStartedWhileTheRuntimeCannotBeBroughtUp_ReportsTheBringUpHresultAsRetryable()
    {
        using var r = new Rig(cooldownMs: 60_000);
        Assert.True(r.Runtime.Acquire());
        r.Runtime.OnNativeEvent(0, PrNative.EvRuntimeFailed, ProtectedRuntimeFaults.DxgiDeviceRemoved, 0, null);

        using ProtectedVideoSession s = r.Session();
        s.Pump(default);

        Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
        Assert.Equal(ProtectedRuntimeFaults.DxgiDeviceRemoved, s.ErrorHr);
        Assert.True(s.ErrorNeedsRuntimeRebuild);
    }

    [Fact]
    public void AMissingComponent_IsStillAPlainDrmFailure_NotARetryableRuntimeReset()
    {
        using var r = new Rig();
        r.Native.Available = false;

        using ProtectedVideoSession s = r.Session();
        s.Pump(default);

        Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
        Assert.False(s.ErrorNeedsRuntimeRebuild);
        Assert.Equal(0, s.ErrorHr);
    }

    [Fact]
    public void AResetClassFailure_IsRetryableInANonDrmCategory_WithTheHresultAsTheUnderlyingCode()
    {
        MediaError reset = ProtectedMediaSession.ProtectedFailure("The protected-video runtime was reset.",
            ProtectedRuntimeFaults.TeeInvalidHwDrmState, needsRuntimeRebuild: true, locus: null);
        Assert.Equal(MediaRecovery.Retryable, reset.Recovery);
        Assert.NotEqual(MediaErrorCategory.Drm, reset.Category);
        Assert.Equal(ProtectedRuntimeFaults.TeeInvalidHwDrmState, (int)reset.UnderlyingCode!.Value);

        // A bare reset-class HRESULT is enough even when the session did not flag it.
        MediaError byHr = ProtectedMediaSession.ProtectedFailure(null, ProtectedRuntimeFaults.DxgiDeviceRemoved, needsRuntimeRebuild: false, locus: null);
        Assert.Equal(MediaRecovery.Retryable, byHr.Recovery);
        Assert.Equal(ProtectedRuntimeFaults.DxgiDeviceRemoved, (int)byHr.UnderlyingCode!.Value);

        MediaError license = ProtectedMediaSession.ProtectedFailure("The PlayReady license was not granted (0x8004C600).",
            unchecked((int)0x8004C600), needsRuntimeRebuild: false, locus: null);
        Assert.Equal(MediaErrorCategory.Drm, license.Category);
        Assert.Equal(MediaRecovery.NeedsLicense, license.Recovery);
        Assert.Null(license.UnderlyingCode);
    }

    // ── F021: the renderer's adapter ─────────────────────────────────────────────────────────────────────────────────

    private const long LuidA = (1L << 32) | 0x2A;
    private const long LuidB = (2L << 32) | 0x7B;

    [Fact]
    public void TheRenderersAdapterLuid_ReachesTheNativeCreate()
    {
        using var r = new Rig(luid: LuidA);
        Assert.True(r.Runtime.Acquire());
        Assert.Equal(LuidA, r.Native.LastAdapterLuid);
    }

    [Fact]
    public void WithNoRendererDevice_TheNativeCreateGetsTheDefaultAdapter()
    {
        using var r = new Rig(luid: 0);
        Assert.True(r.Runtime.Acquire());
        Assert.Equal(0L, r.Native.LastAdapterLuid);
    }

    [Fact]
    public void AnIdleRuntime_CreatedForAnotherAdapter_IsRebuiltOnTheRenderersAdapter()
    {
        using var r = new Rig(luid: LuidA);
        Assert.True(r.Runtime.Acquire());
        r.Runtime.Release();
        r.Luid = LuidB;   // the user switched GPUs

        Assert.True(r.Runtime.Acquire());

        Assert.Equal(2, r.Native.CreateCount);
        Assert.Equal(new[] { LuidA, LuidB }, r.Native.AdapterLuids);
        Assert.Equal(1, r.Native.DestroyCount);
    }

    [Fact]
    public void AnAdapterSwitch_NeverEndsALiveSession()
    {
        using var r = new Rig(luid: LuidA);
        using ProtectedVideoSession live = r.Session();
        r.Luid = LuidB;

        Assert.True(r.Runtime.Acquire());   // e.g. the next video's prepare

        Assert.Equal(1, r.Native.CreateCount);
        Assert.Equal(0, r.Native.DestroyCount);
    }

    [Fact]
    public void AnUnknownAdapterOnEitherSide_IsNeverAReasonToRebuild()
    {
        using var r = new Rig(luid: 0);
        Assert.True(r.Runtime.Acquire());
        r.Runtime.Release();
        r.Luid = LuidB;   // the renderer's device came up after the runtime did

        Assert.True(r.Runtime.Acquire());
        Assert.Equal(1, r.Native.CreateCount);
    }
}
