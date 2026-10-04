using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// <see cref="ProtectedVideoRuntime"/> — the KID-keyed license cache, the non-blocking license relay, the buffered waits
/// and the warm-idle lifetime — driven through its internal constructor over <see cref="FakeRuntimeNative"/>. Never the
/// real DLL and never <see cref="ProtectedVideoRuntime.Shared"/>: no CDM, no GPU, no license server, no window. Native
/// events are injected with <see cref="ProtectedVideoRuntime.OnNativeEvent"/> exactly as the event thunk dispatches them;
/// the relay is driven with <see cref="ProtectedVideoRuntime.OnChallenge"/> and a <see cref="RecordingDelivery"/>.
/// Every wait is a bounded await — the warm-idle teardown is observed through <see cref="FakeRuntimeNative.Destroyed"/>.
/// </summary>
public sealed class ProtectedRuntimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly byte[] Pssh = { 1, 2, 3, 4 };

    /// <summary>A token that can never cancel — the no-registration path of <see cref="ProtectedVideoRuntime.WaitBufferedAsync"/>.</summary>
    private static readonly CancellationToken NeverCancels = new(canceled: false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Kid(int i) => i.ToString("x32");

    /// <summary>One runtime over a fresh fake, with its own CDM store directory under the temp path (removed on dispose).
    /// The default warm-idle window is long enough never to fire inside a test; lifetime tests shorten it.</summary>
    private sealed class Harness : IDisposable
    {
        public readonly FakeRuntimeNative Native = new();
        public readonly string StorePath = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        public readonly ProtectedVideoRuntime Runtime;

        public Harness(int idleMs = 60_000) => Runtime = new ProtectedVideoRuntime(Native, StorePath, idleMs);

        public LicenseCacheState Ensure(int kid, Func<LicenseRequest, ValueTask<LicenseResponse>>? relay = null)
            => Runtime.EnsureLicense(Pssh, Kid(kid), relay);

        public ulong HandleFor(int kid) => Runtime.LicenseHandleFor(Kid(kid));
        public LicenseCacheState StateFor(int kid) => Runtime.LicenseStateFor(Kid(kid));

        public void Dispose()
        {
            Runtime.Dispose();
            try { if (Directory.Exists(StorePath)) Directory.Delete(StorePath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ── the license cache ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureLicense_BringsTheRuntimeUpOnce_AndASecondCallJoinsThePendingKid()
    {
        using var h = new Harness();
        Assert.False(h.Runtime.IsRunning);

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));

        Assert.True(h.Runtime.IsRunning);
        Assert.Equal(1, h.Native.CreateCount);
        Assert.Equal(1, h.Native.AcquireCount);
        Assert.Equal(h.StorePath, h.Native.LastStorePath);
        Assert.True(Directory.Exists(h.StorePath));
        (string acquiredKid, ulong lic) = Assert.Single(h.Native.Acquired);
        Assert.Equal(Kid(1), acquiredKid);
        Assert.Equal(lic, h.HandleFor(1));
        Assert.Equal(0, h.Runtime.References);   // the manifest-time bring-up hands its reference straight back

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));
        Assert.Equal(1, h.Native.CreateCount);
        Assert.Equal(1, h.Native.AcquireCount);  // joined: never a second challenge for one KID
        Assert.Equal(1, h.Runtime.LicenseCount);
    }

    [Fact]
    public void EnsureLicense_WithNoKid_FailsWithoutTouchingNative()
    {
        using var h = new Harness();
        Assert.Equal(LicenseCacheState.Failed, h.Runtime.EnsureLicense(Pssh, string.Empty, relay: null));
        Assert.Equal(0, h.Native.CreateCount);
        Assert.Equal(0, h.Native.AcquireCount);
    }

    [Fact]
    public void LicenseUsableEvent_MarksTheRowUsable_AndTheNextEnsureReusesItWithNoAcquire()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong lic = h.HandleFor(1);

        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 12, 0, Kid(1));

        Assert.Equal(LicenseCacheState.Usable, h.StateFor(1));
        Assert.Equal(LicenseCacheState.Usable, h.Ensure(1));
        Assert.Equal(1, h.Native.AcquireCount);
        Assert.Equal(0, h.Native.ReleaseCount);
        Assert.Equal(lic, h.HandleFor(1));
    }

    [Fact]
    public void AUsableLicenseExpiringInsideTheGuard_IsReacquired_AndItsOldKeySessionReleased()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong first = h.HandleFor(1);
        h.Runtime.OnNativeEvent(first, PrNative.EvLicenseUsable, 12, LicenseCachePolicy.ExpiryGuardMs / 2, Kid(1));
        Assert.Equal(LicenseCacheState.Usable, h.StateFor(1));

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));

        Assert.Equal(2, h.Native.AcquireCount);
        Assert.Equal(new[] { first }, h.Native.Released);
        Assert.NotEqual(first, h.HandleFor(1));
    }

    [Fact]
    public void LicenseExpiredEvent_MarksExpired_AndTheNextEnsureReacquires()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong first = h.HandleFor(1);
        h.Runtime.OnNativeEvent(first, PrNative.EvLicenseUsable, 12, 0, Kid(1));

        h.Runtime.OnNativeEvent(first, PrNative.EvLicenseExpired, 0, 0, Kid(1));

        Assert.Equal(LicenseCacheState.Expired, h.StateFor(1));
        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));
        Assert.Equal(2, h.Native.AcquireCount);
        Assert.Equal(new[] { first }, h.Native.Released);
    }

    [Fact]
    public void ACompletionRacingTheHandleAssignment_IsMatchedByKidText_AndAdoptsTheHandle()
    {
        using var h = new Harness();
        // The CDM answers while FgPrLicenseAcquire is still returning: the row has no handle yet.
        h.Native.DuringAcquire = (lic, kid) => h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, kid);

        Assert.Equal(LicenseCacheState.Usable, h.Ensure(1));

        ulong acquired = Assert.Single(h.Native.Acquired).License;
        Assert.Equal(acquired, h.HandleFor(1));
        Assert.Equal(LicenseCacheState.Usable, h.StateFor(1));
    }

    [Fact]
    public void ACompletionForAnUnknownHandle_IsDropped_EvenWhenItsKidTextNamesARowThatAlreadyHasAHandle()
    {
        using var h = new Harness();
        h.Ensure(1);

        h.Runtime.OnNativeEvent(0xDEAD, PrNative.EvLicenseUsable, 5, 0, Kid(2));   // no such row
        h.Runtime.OnNativeEvent(0xDEAD, PrNative.EvLicenseUsable, 5, 0, Kid(1));   // a row, but not waiting for a handle

        Assert.Equal(LicenseCacheState.Pending, h.StateFor(1));
        Assert.Equal(LicenseCacheState.None, h.StateFor(2));
        Assert.Equal(1, h.Runtime.LicenseCount);
    }

    [Fact]
    public void LicenseFailedEvent_MarksFailed_AndTheNextEnsureReacquiresAndReleasesTheDeadHandle()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong dead = h.HandleFor(1);

        h.Runtime.OnNativeEvent(dead, PrNative.EvLicenseFailed, unchecked((int)0x8004C600), 0, Kid(1));

        Assert.Equal(LicenseCacheState.Failed, h.StateFor(1));
        Assert.Contains("8004C600", h.Runtime.LicenseFailureFor(Kid(1)));

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));
        Assert.Equal(2, h.Native.AcquireCount);
        Assert.Equal(new[] { dead }, h.Native.Released);
        ulong fresh = h.HandleFor(1);
        Assert.NotEqual(0UL, fresh);
        Assert.NotEqual(dead, fresh);
        Assert.Null(h.Runtime.LicenseFailureFor(Kid(1)));   // the fresh row carries no stale failure
    }

    [Fact]
    public void ALateFailureAfterUsable_IsNeverReapplied()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong lic = h.HandleFor(1);
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 12, 0, Kid(1));

        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseFailed, unchecked((int)0x8004C600), 0, Kid(1));

        Assert.Equal(LicenseCacheState.Usable, h.StateFor(1));
        Assert.Null(h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public void AFailedKeySessionOpen_MarksTheRowFailed_WithAMessage_AndARetryAcquiresAgain()
    {
        using var h = new Harness();
        h.Native.AcquireHr = unchecked((int)0x80070005);

        Assert.Equal(LicenseCacheState.Failed, h.Ensure(1));
        Assert.Equal(LicenseCacheState.Failed, h.StateFor(1));
        Assert.Contains("80070005", h.Runtime.LicenseFailureFor(Kid(1)));
        Assert.Equal(0UL, h.HandleFor(1));

        h.Native.AcquireHr = 0;
        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));
        Assert.Equal(2, h.Native.AcquireCount);
        Assert.Equal(0, h.Native.ReleaseCount);   // a failed open left no key session to close
    }

    [Fact]
    public void AThrowingKeySessionOpen_MarksTheRowFailed_WithTheExceptionMessage()
    {
        using var h = new Harness();
        h.Native.DuringAcquire = (_, _) => throw new InvalidOperationException("pmp host gone");

        Assert.Equal(LicenseCacheState.Failed, h.Ensure(1));
        Assert.Contains("pmp host gone", h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public void TheNinthKid_NeverEvictsALiveRowManagedSide_NativeIsTheSingleEvictionAuthority()
    {
        using var h = new Harness();
        for (int i = 0; i < LicenseCachePolicy.Capacity; i++) h.Ensure(i);
        ulong oldest = h.HandleFor(0);
        Assert.Equal(LicenseCachePolicy.Capacity, h.Runtime.LicenseCount);

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(LicenseCachePolicy.Capacity));

        // The managed cache holds no LRU of its own any more: it admits the ninth KID and waits for native (whose table
        // closes the victim's key session and raises EvLicenseEvicted) to say which row goes.
        Assert.Equal(LicenseCachePolicy.Capacity + 1, h.Runtime.LicenseCount);
        Assert.Equal(0, h.Native.ReleaseCount);
        Assert.Equal(oldest, h.HandleFor(0));
        Assert.Equal(LicenseCacheState.Pending, h.StateFor(0));
    }

    [Fact]
    public void ANativeEvictionEvent_DropsTheRowThatHoldsThatHandle_WithNoReleaseOfItsOwn_AndTheNextEnsureReacquires()
    {
        using var h = new Harness();
        for (int i = 0; i < LicenseCachePolicy.Capacity; i++) h.Ensure(i);
        ulong oldest = h.HandleFor(0);
        h.Ensure(LicenseCachePolicy.Capacity);
        h.Native.NativeCloses(oldest);   // native's LRU closed the key session…

        h.Runtime.OnNativeEvent(oldest, PrNative.EvLicenseEvicted, 0, 0, Kid(0));   // …and said so

        Assert.Equal(LicenseCacheState.None, h.StateFor(0));
        Assert.Equal(0UL, h.HandleFor(0));
        Assert.Equal(LicenseCachePolicy.Capacity, h.Runtime.LicenseCount);
        Assert.Equal(0, h.Native.ReleaseCount);   // native already closed it: the managed side never closes a key session twice
        for (int i = 1; i <= LicenseCachePolicy.Capacity; i++) Assert.Equal(LicenseCacheState.Pending, h.StateFor(i));

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(0));
        Assert.Equal(LicenseCachePolicy.Capacity + 2, h.Native.AcquireCount);
        Assert.NotEqual(oldest, h.HandleFor(0));
    }

    [Fact]
    public void ACompletionForAnEvictedKid_IsIgnored()
    {
        using var h = new Harness();
        for (int i = 0; i < LicenseCachePolicy.Capacity; i++) h.Ensure(i);
        ulong evicted = h.HandleFor(0);
        h.Ensure(LicenseCachePolicy.Capacity);
        h.Native.NativeCloses(evicted);
        h.Runtime.OnNativeEvent(evicted, PrNative.EvLicenseEvicted, 0, 0, Kid(0));
        Assert.Equal(LicenseCacheState.None, h.StateFor(0));

        h.Runtime.OnNativeEvent(evicted, PrNative.EvLicenseUsable, 12, 0, Kid(0));
        h.Runtime.OnNativeEvent(evicted, PrNative.EvLicenseFailed, unchecked((int)0x8004C600), 0, Kid(0));

        Assert.Equal(LicenseCacheState.None, h.StateFor(0));   // never resurrected
        Assert.Equal(0UL, h.HandleFor(0));
        Assert.Equal(LicenseCachePolicy.Capacity, h.Runtime.LicenseCount);
    }

    [Fact]
    public void AnEvictionEventNamingNoRow_IsANoOp_AndNeverMatchesByKid()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong lic = h.HandleFor(1);

        h.Runtime.OnNativeEvent(0xDEAD, PrNative.EvLicenseEvicted, 0, 0, Kid(1));   // another handle, this row's KID text
        h.Runtime.OnNativeEvent(0, PrNative.EvLicenseEvicted, 0, 0, Kid(1));        // no handle at all

        Assert.Equal(lic, h.HandleFor(1));
        Assert.Equal(LicenseCacheState.Pending, h.StateFor(1));
    }

    [Fact]
    public void AFullCache_TrimsADeadRow_ButNeverAPinnedOne()
    {
        using var h = new Harness();
        for (int i = 0; i < LicenseCachePolicy.Capacity; i++) h.Ensure(i);
        ulong pinned = h.HandleFor(0);
        ulong dead = h.HandleFor(1);
        h.Runtime.PinLicense(Kid(0), pinned: true);
        h.Runtime.OnNativeEvent(pinned, PrNative.EvLicenseFailed, unchecked((int)0x8004C600), 0, Kid(0));
        h.Runtime.OnNativeEvent(dead, PrNative.EvLicenseFailed, unchecked((int)0x8004C600), 0, Kid(1));

        h.Ensure(LicenseCachePolicy.Capacity);

        Assert.Equal(LicenseCacheState.Failed, h.StateFor(0));   // an attached session is decoding with it
        Assert.Equal(pinned, h.HandleFor(0));
        Assert.Equal(LicenseCacheState.None, h.StateFor(1));     // the dead, unpinned row made room
        Assert.Equal(new[] { dead }, h.Native.Released);
        Assert.Equal(LicenseCachePolicy.Capacity, h.Runtime.LicenseCount);
    }

    [Fact]
    public void WhenEveryKidIsPinned_NothingIsEvicted_AndTheNewKidIsStillAdmitted()
    {
        using var h = new Harness();
        for (int i = 0; i < LicenseCachePolicy.Capacity; i++)
        {
            h.Ensure(i);
            h.Runtime.PinLicense(Kid(i), pinned: true);
        }

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(LicenseCachePolicy.Capacity));

        Assert.Equal(0, h.Native.ReleaseCount);
        Assert.Equal(LicenseCachePolicy.Capacity + 1, h.Runtime.LicenseCount);
    }

    [Fact]
    public void StartLicense_StartsTheRequestsKid_AndSkipsARequestItCannotChallengeFor()
    {
        using var h = new Harness();

        Assert.Equal(LicenseCacheState.None,
            ProtectedMediaBackend.StartLicense(h.Runtime, new ProtectedVideoRequest { DefaultKid = Kid(1) }));   // no PSSH
        Assert.Equal(LicenseCacheState.None,
            ProtectedMediaBackend.StartLicense(h.Runtime, new ProtectedVideoRequest { Pssh = Pssh }));          // no KID
        Assert.Equal(0, h.Native.CreateCount);

        Assert.Equal(LicenseCacheState.Pending,
            ProtectedMediaBackend.StartLicense(h.Runtime, new ProtectedVideoRequest { DefaultKid = Kid(1), Pssh = Pssh }));
        Assert.Equal(Kid(1), Assert.Single(h.Native.Acquired).Kid);
    }

    // ── bring-up / lifetime ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheLastRelease_DestroysTheRuntimeAfterTheIdleWindow_ReleasingEveryLicenseFirst()
    {
        using var h = new Harness(idleMs: 200);
        Assert.True(h.Runtime.Acquire());
        Assert.True(h.Runtime.Acquire());
        Assert.Equal(1, h.Native.CreateCount);
        Assert.Equal(2, h.Runtime.References);
        ulong rt = h.Native.LastRuntime;
        h.Ensure(1);   // the runtime is already referenced: no bring-up, no warm-idle window
        ulong lic = h.HandleFor(1);

        h.Runtime.Release();
        Assert.Equal(1, h.Runtime.References);
        Assert.True(h.Runtime.IsRunning);
        h.Runtime.Release();
        Assert.Equal(0, h.Runtime.References);

        await h.Native.Destroyed.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);

        Assert.False(h.Runtime.IsRunning);
        Assert.Equal(1, h.Native.DestroyCount);
        string[] calls = h.Native.Calls;
        Assert.Equal("destroy:" + rt, calls[^1]);
        Assert.InRange(Array.IndexOf(calls, "release:" + lic), 0, calls.Length - 2);
        Assert.Equal(0, h.Runtime.LicenseCount);

        Assert.True(h.Runtime.Acquire());   // after the teardown, the next use brings it up again
        Assert.Equal(2, h.Native.CreateCount);
    }

    [Fact]
    public async Task AnAcquireInsideTheIdleWindow_CancelsTheTeardown_AndReusesTheRuntime()
    {
        using var h = new Harness(idleMs: 200);
        Assert.True(h.Runtime.Acquire());
        h.Runtime.Release();                 // the warm-idle window starts…
        Assert.True(h.Runtime.Acquire());    // …and a new session inside it cancels the teardown

        await Assert.ThrowsAsync<TimeoutException>(
            () => h.Native.Destroyed.Task.WaitAsync(TimeSpan.FromMilliseconds(600), Ct));
        Assert.True(h.Runtime.IsRunning);
        Assert.Equal(1, h.Native.CreateCount);
        Assert.Equal(0, h.Native.DestroyCount);

        h.Runtime.Release();
        await h.Native.Destroyed.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.False(h.Runtime.IsRunning);
        Assert.Equal(1, h.Native.CreateCount);
    }

    [Fact]
    public async Task AKeepAliveToken_KeepsTheRuntimeAndItsLicensesWarm_PastTheIdleWindow()
    {
        // D15's gap: a surface closed (no session left), the app's warm keeper still knows a video-capable row is
        // current/next, and the idle window is short enough to fire while nothing else holds a reference. Without a
        // token the runtime — and every license in it — is torn down; with one it must not be, however long the
        // window waits.
        using var h = new Harness(idleMs: 200);
        Assert.True(h.Runtime.Acquire());
        h.Ensure(1);
        ulong lic = h.HandleFor(1);
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 12, 0, Kid(1));
        Assert.Equal(LicenseCacheState.Usable, h.StateFor(1));

        IDisposable? token = h.Runtime.TakeKeepAlive();
        Assert.NotNull(token);
        Assert.Equal(2, h.Runtime.References);

        h.Runtime.Release();   // the session's own reference goes away — only the keep-alive is left
        Assert.Equal(1, h.Runtime.References);

        await Task.Delay(500, Ct);   // well past the 200 ms idle window
        Assert.True(h.Runtime.IsRunning);
        Assert.Equal(0, h.Native.DestroyCount);
        Assert.Equal(LicenseCacheState.Usable, h.StateFor(1));
        Assert.Equal(lic, h.HandleFor(1));

        token!.Dispose();      // the keeper let go: NOW the idle window (and eventual teardown) applies
        await h.Native.Destroyed.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.False(h.Runtime.IsRunning);
        Assert.Equal(0, h.Runtime.LicenseCount);
    }

    [Fact]
    public void AKeepAliveToken_DisposedTwice_ReleasesOnlyOnce()
    {
        using var h = new Harness(idleMs: 60_000);
        Assert.True(h.Runtime.Acquire());
        IDisposable? token = h.Runtime.TakeKeepAlive();
        Assert.Equal(2, h.Runtime.References);

        token!.Dispose();
        Assert.Equal(1, h.Runtime.References);
        token.Dispose();       // idempotent: must not drive References negative or double-release
        Assert.Equal(1, h.Runtime.References);
    }

    [Fact]
    public void AKeepAliveToken_FailsWhenTheNativeComponentIsMissing()
    {
        using var h = new Harness();
        h.Native.Available = false;

        Assert.Null(h.Runtime.TakeKeepAlive());
        Assert.False(h.Runtime.IsRunning);
        Assert.Equal(0, h.Runtime.References);
    }

    [Fact]
    public void AMissingNativeComponent_FailsAcquireWithAStartupError()
    {
        using var h = new Harness();
        h.Native.Available = false;

        Assert.False(h.Runtime.Acquire());

        Assert.False(h.Runtime.IsRunning);
        Assert.Equal(0, h.Native.CreateCount);
        Assert.Contains(PrNative.LibraryName, h.Runtime.StartupError);
        Assert.Equal(LicenseCacheState.Failed, h.Ensure(1));
        Assert.Equal(0, h.Native.AcquireCount);
    }

    [Fact]
    public void AFailedBringUp_FailsAcquireWithTheHresult_AndALaterBringUpClearsIt()
    {
        using var h = new Harness();
        h.Native.CreateHr = unchecked((int)0x887A0005);

        Assert.False(h.Runtime.Acquire());
        Assert.False(h.Runtime.IsRunning);
        Assert.Contains("887A0005", h.Runtime.StartupError);

        h.Native.CreateHr = 0;
        Assert.True(h.Runtime.Acquire());
        Assert.Null(h.Runtime.StartupError);
    }

    [Fact]
    public void ARuntimeFailedEvent_RecordsTheStartupError()
    {
        using var h = new Harness();
        h.Runtime.OnNativeEvent(0, PrNative.EvRuntimeFailed, unchecked((int)0x80004005), 0, null);
        Assert.Contains("80004005", h.Runtime.StartupError);
    }

    [Fact]
    public async Task Dispose_ReleasesEveryLicense_ThenDestroysTheRuntime_AndRefusesReuse()
    {
        using var h = new Harness();
        Assert.True(h.Runtime.Acquire());   // a live reference: Dispose does not wait for any idle window
        h.Ensure(1);
        h.Ensure(2);
        ulong a = h.HandleFor(1), b = h.HandleFor(2), rt = h.Native.LastRuntime;
        var wait = Assert.IsAssignableFrom<Task<bool>>(h.Runtime.WaitBufferedAsync(7, NeverCancels));

        h.Runtime.Dispose();

        string[] calls = h.Native.Calls;
        int destroyAt = Array.IndexOf(calls, "destroy:" + rt);
        Assert.Equal(calls.Length - 1, destroyAt);
        Assert.InRange(Array.IndexOf(calls, "release:" + a), 0, destroyAt - 1);
        Assert.InRange(Array.IndexOf(calls, "release:" + b), 0, destroyAt - 1);
        Assert.False(h.Runtime.IsRunning);
        Assert.Equal(0, h.Runtime.LicenseCount);
        Assert.False(await wait.WaitAsync(Bound, Ct));   // a pending buffered wait completes, never hangs

        Assert.False(h.Runtime.Acquire());
        Assert.Equal(LicenseCacheState.Failed, h.Ensure(3));
        Assert.Equal(1, h.Native.CreateCount);

        h.Runtime.Dispose();                             // idempotent
        Assert.Equal(1, h.Native.DestroyCount);
    }

    // ── buffered waits ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WaitBufferedAsync_CompletesTrue_OnBufferedMediaForItsOwnSession()
    {
        using var h = new Harness();
        var wait = Assert.IsAssignableFrom<Task<bool>>(h.Runtime.WaitBufferedAsync(7, NeverCancels));

        h.Runtime.OnNativeEvent(8, PrNative.EvBuffered, 4000, 0, null);   // another session
        h.Runtime.OnNativeEvent(7, PrNative.EvBuffered, 0, 0, null);      // nothing buffered yet
        Assert.False(wait.IsCompleted);

        h.Runtime.OnNativeEvent(7, PrNative.EvBuffered, 4000, 0, null);
        Assert.True(await wait.WaitAsync(Bound, Ct));
    }

    [Fact]
    public async Task WaitBufferedAsync_CompletesWhenCanceled_WithoutFaulting()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        Task wait = h.Runtime.WaitBufferedAsync(7, cts.Token);
        Assert.False(wait.IsCompleted);

        cts.Cancel();

        await wait.WaitAsync(Bound, Ct);
        Assert.True(wait.IsCompletedSuccessfully);   // a canceled prepare's wait ends quietly; it never throws
    }

    [Fact]
    public async Task WaitBufferedAsync_ASecondWaitSupersedesTheFirst_WhichCompletesFalse()
    {
        using var h = new Harness();
        var first = Assert.IsAssignableFrom<Task<bool>>(h.Runtime.WaitBufferedAsync(7, NeverCancels));
        var second = Assert.IsAssignableFrom<Task<bool>>(h.Runtime.WaitBufferedAsync(7, NeverCancels));

        Assert.False(await first.WaitAsync(Bound, Ct));
        Assert.False(second.IsCompleted);

        h.Runtime.OnNativeEvent(7, PrNative.EvBuffered, 1, 0, null);
        Assert.True(await second.WaitAsync(Bound, Ct));
    }

    [Fact]
    public async Task UnregisterSession_CompletesItsPendingWaitFalse()
    {
        using var h = new Harness();
        var wait = Assert.IsAssignableFrom<Task<bool>>(h.Runtime.WaitBufferedAsync(7, NeverCancels));

        h.Runtime.UnregisterSession(7);

        Assert.False(await wait.WaitAsync(Bound, Ct));
    }

    [Fact]
    public void WaitBufferedAsync_ForNoSession_CompletesAtOnce()
    {
        using var h = new Harness();
        Assert.True(h.Runtime.WaitBufferedAsync(0, NeverCancels).IsCompletedSuccessfully);
    }

    // ── the license relay (non-blocking; exactly one delivery) ───────────────────────────────────────────────────────

    private static Func<LicenseRequest, ValueTask<LicenseResponse>> Returning(byte[] license)
        => _ => ValueTask.FromResult(new LicenseResponse(license));

    [Fact]
    public async Task Relay_Success_DeliversTheLicenseBytesExactlyOnce_WithTheChallengeAndKid()
    {
        using var h = new Harness();
        LicenseRequest? seen = null;
        h.Ensure(1, req =>
        {
            seen = req;
            return ValueTask.FromResult(new LicenseResponse(new byte[] { 9, 8, 7 }));
        });
        var delivery = new RecordingDelivery();

        int hr = h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1, 2, 3, 4 }, Kid(1), delivery);

        Assert.Equal(0, hr);
        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(1, delivery.Calls);
        Assert.Equal(0, delivery.Hr);
        Assert.Equal(new byte[] { 9, 8, 7 }, delivery.Bytes);
        Assert.NotNull(seen);
        Assert.Equal(DrmSystem.PlayReady, seen!.System);
        Assert.Equal(Kid(1), seen.KeyId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, seen.Challenge.ToArray());
        Assert.Null(h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public async Task Relay_ReturnsAtOnce_AndDeliversOnlyWhenTheRelayCompletes()
    {
        using var h = new Harness();
        var answer = new TaskCompletionSource<LicenseResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Ensure(1, _ => new ValueTask<LicenseResponse>(answer.Task));
        var delivery = new RecordingDelivery();

        int hr = h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery);

        Assert.Equal(0, hr);
        Assert.Equal(0, delivery.Calls);   // the CDM thread is never held for the round trip

        answer.SetResult(new LicenseResponse(new byte[] { 5 }));
        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(1, delivery.Calls);
        Assert.Equal(0, delivery.Hr);
        Assert.Equal(new byte[] { 5 }, delivery.Bytes);
    }

    [Fact]
    public async Task Relay_ThrowingSynchronously_DeliversTheFailureHrOnce_AndRecordsTheMessage()
    {
        using var h = new Harness();
        h.Ensure(1, _ => throw new InvalidOperationException("server 403"));
        var delivery = new RecordingDelivery();

        int hr = h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery);

        Assert.Equal(0, hr);
        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(1, delivery.Calls);
        Assert.Equal(ProtectedVideoRuntime.LicenseRelayFailedHr, delivery.Hr);
        Assert.Empty(delivery.Bytes!);
        Assert.Contains("server 403", h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public async Task Relay_FaultingAsynchronously_DeliversTheFailureHrOnce_AndRecordsTheMessage()
    {
        static async ValueTask<LicenseResponse> Faulting(LicenseRequest _)
        {
            await Task.Yield();
            throw new InvalidOperationException("license server 503");
        }

        using var h = new Harness();
        h.Ensure(1, Faulting);
        var delivery = new RecordingDelivery();

        Assert.Equal(0, h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery));

        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(1, delivery.Calls);
        Assert.Equal(ProtectedVideoRuntime.LicenseRelayFailedHr, delivery.Hr);
        Assert.Empty(delivery.Bytes!);
        Assert.Contains("license server 503", h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public async Task Relay_ReturningAnEmptyLicense_DeliversTheFailureHrOnce()
    {
        using var h = new Harness();
        h.Ensure(1, Returning(Array.Empty<byte>()));
        var delivery = new RecordingDelivery();

        Assert.Equal(0, h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery));

        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(1, delivery.Calls);
        Assert.Equal(ProtectedVideoRuntime.LicenseRelayFailedHr, delivery.Hr);
        Assert.Empty(delivery.Bytes!);
        Assert.Contains("empty", h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public async Task Relay_Canceled_DeliversTheFailureHrOnce()
    {
        using var h = new Harness();
        h.Ensure(1, _ => ValueTask.FromCanceled<LicenseResponse>(new CancellationToken(canceled: true)));
        var delivery = new RecordingDelivery();

        Assert.Equal(0, h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery));

        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(1, delivery.Calls);
        Assert.Equal(ProtectedVideoRuntime.LicenseRelayFailedHr, delivery.Hr);
        Assert.Contains("canceled", h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public void NoRelayConfigured_ReturnsEFail_RecordsWhy_AndNeverDelivers()
    {
        using var h = new Harness();
        h.Ensure(1, relay: null);
        var delivery = new RecordingDelivery();

        int hr = h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery);

        Assert.Equal(PrNative.EFail, hr);
        Assert.Equal(0, delivery.Calls);
        Assert.Contains("relay", h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public void AChallengeForAnUnknownKid_ReturnsEFail_AndNeverDelivers()
    {
        using var h = new Harness();
        var delivery = new RecordingDelivery();

        Assert.Equal(PrNative.EFail, h.Runtime.OnChallenge(0x42, new byte[] { 1 }, Kid(9), delivery));
        Assert.Equal(0, delivery.Calls);
    }

    [Fact]
    public async Task AJoiningEnsureWithARelay_SuppliesItToThePendingRow()
    {
        using var h = new Harness();
        h.Ensure(1, relay: null);
        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1, Returning(new byte[] { 7 })));
        Assert.Equal(1, h.Native.AcquireCount);
        var delivery = new RecordingDelivery();

        Assert.Equal(0, h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery));

        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(new byte[] { 7 }, delivery.Bytes);
        Assert.Equal(0, delivery.Hr);
    }

    // ── stage A: a cached handle is vouched for by the native table before it is reused (F008) ──────────────────────────

    [Fact]
    public void AReusableRowWhoseKeySessionNativeHasClosed_IsDropped_AndReacquired_InsteadOfHandedOut()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong lic = h.HandleFor(1);
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 12, 0, Kid(1));
        h.Native.NativeCloses(lic);   // native's LRU closed it and the event has not reached the managed side (yet)

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));

        Assert.Equal(2, h.Native.AcquireCount);
        Assert.Contains(lic, h.Native.Released);
        Assert.NotEqual(lic, h.HandleFor(1));
        Assert.NotEqual(0UL, h.HandleFor(1));
    }

    [Fact]
    public void AUsableRowWhoseNativeStateIsExpiredOrFailed_IsReacquired()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong first = h.HandleFor(1);
        h.Runtime.OnNativeEvent(first, PrNative.EvLicenseUsable, 12, 0, Kid(1));
        h.Native.SetLicenseState(first, 2);   // native says expired; the managed expiry event was lost

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));
        ulong second = h.HandleFor(1);
        Assert.NotEqual(first, second);
        h.Runtime.OnNativeEvent(second, PrNative.EvLicenseUsable, 12, 0, Kid(1));
        h.Native.SetLicenseState(second, unchecked((int)0x80048005));   // a killed key: negative HRESULT

        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));
        Assert.Equal(3, h.Native.AcquireCount);
        Assert.Equal(new[] { first, second }, h.Native.Released);
    }

    [Fact]
    public void AValidReusableRow_IsProbedOnce_AndStillReusedWithNoAcquireOrRelease()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong lic = h.HandleFor(1);
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 12, 0, Kid(1));
        int before = h.Native.StateProbeCount;

        Assert.Equal(LicenseCacheState.Usable, h.Ensure(1));

        Assert.Equal(before + 1, h.Native.StateProbeCount);
        Assert.Equal(1, h.Native.AcquireCount);
        Assert.Equal(0, h.Native.ReleaseCount);
        Assert.Equal(lic, h.HandleFor(1));
    }

    [Fact]
    public void ValidatedLicenseHandleFor_ReturnsTheLiveHandle_AndZeroAfterDroppingARowNativeLost()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong lic = h.HandleFor(1);
        Assert.Equal(lic, h.Runtime.ValidatedLicenseHandleFor(Kid(1)));
        Assert.Equal(0UL, h.Runtime.ValidatedLicenseHandleFor(Kid(2)));   // no row
        Assert.Equal(0UL, h.Runtime.ValidatedLicenseHandleFor(null));

        h.Native.NativeCloses(lic);

        Assert.Equal(0UL, h.Runtime.ValidatedLicenseHandleFor(Kid(1)));
        Assert.Equal(LicenseCacheState.None, h.StateFor(1));
        Assert.Contains(lic, h.Native.Released);
    }

    [Fact]
    public void AProbeThatThrows_NeverCondemnsALicense()
    {
        using var h = new Harness();
        h.Ensure(1);
        ulong lic = h.HandleFor(1);
        h.Native.DuringStateProbe = _ => throw new InvalidOperationException("native fault");

        Assert.Equal(lic, h.Runtime.ValidatedLicenseHandleFor(Kid(1)));
        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));
        Assert.Equal(1, h.Native.AcquireCount);
    }

    // ── the Pending deadline (F011) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APendingRowOlderThanTheDeadline_IsReacquired_NotJoined_AndItsStalledRelayIsCancelled()
    {
        using var h = new Harness();
        long now = 1_000_000;
        h.Runtime.Clock = () => Volatile.Read(ref now);
        CancellationToken attemptToken = default;
        var staleAnswer = new TaskCompletionSource<LicenseResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Ensure(1, req =>
        {
            attemptToken = req.Cancel;
            req.Cancel.Register(static s => ((TaskCompletionSource<LicenseResponse>)s!).TrySetCanceled(), staleAnswer);
            return new ValueTask<LicenseResponse>(staleAnswer.Task);
        });
        ulong first = h.HandleFor(1);
        var staleDelivery = new RecordingDelivery();
        Assert.Equal(0, h.Runtime.OnChallenge(first, new byte[] { 1 }, Kid(1), staleDelivery));
        Assert.True(attemptToken.CanBeCanceled);   // the attempt carries a token the runtime owns
        Assert.False(attemptToken.IsCancellationRequested);

        Interlocked.Add(ref now, LicenseCachePolicy.PendingDeadlineMs - 1);
        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1));   // one ms short: still joins the attempt
        Assert.Equal(1, h.Native.AcquireCount);
        Assert.Equal(first, h.HandleFor(1));
        Assert.False(attemptToken.IsCancellationRequested);

        Interlocked.Add(ref now, 1);                            // the deadline: stale
        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1, Returning(new byte[] { 2 })));

        Assert.Equal(2, h.Native.AcquireCount);
        Assert.Equal(new[] { first }, h.Native.Released);
        Assert.NotEqual(first, h.HandleFor(1));
        Assert.True(attemptToken.IsCancellationRequested);      // the stalled POST was told to stop
        await staleDelivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(1, staleDelivery.Calls);
        Assert.Equal(ProtectedVideoRuntime.LicenseRelayFailedHr, staleDelivery.Hr);
        Assert.Null(h.Runtime.LicenseFailureFor(Kid(1)));       // a late failure of the old attempt never stains the fresh row
    }

    [Fact]
    public void ASupersededRelaysFailure_IsNeverAdoptedOntoTheFreshHandlelessRow()
    {
        using var h = new Harness();
        long now = 1_000_000;
        h.Runtime.Clock = () => Volatile.Read(ref now);
        // Completes on the cancelling thread (no RunContinuationsAsynchronously): the superseded attempt delivers its failure INSIDE
        // the Ensure that replaces the row, while the fresh row has no handle yet - the window native's LicenseFailed raced into.
        var staleAnswer = new TaskCompletionSource<LicenseResponse>();
        h.Ensure(1, req =>
        {
            req.Cancel.Register(static s => ((TaskCompletionSource<LicenseResponse>)s!).TrySetCanceled(), staleAnswer);
            return new ValueTask<LicenseResponse>(staleAnswer.Task);
        });
        ulong first = h.HandleFor(1);
        var staleDelivery = new EventRaisingDelivery(hr => h.Runtime.OnNativeEvent(first, PrNative.EvLicenseFailed, hr, 0, Kid(1)));
        Assert.Equal(0, h.Runtime.OnChallenge(first, new byte[] { 1 }, Kid(1), staleDelivery));

        Interlocked.Add(ref now, LicenseCachePolicy.PendingDeadlineMs);
        Assert.Equal(LicenseCacheState.Pending, h.Ensure(1, Returning(new byte[] { 2 })));

        Assert.Equal(1, staleDelivery.Calls);   // the old attempt did report, and the event reached the runtime
        ulong fresh = h.HandleFor(1);
        Assert.NotEqual(0UL, fresh);
        Assert.NotEqual(first, fresh);          // the fresh row holds ITS handle, not the old one's
        Assert.Equal(LicenseCacheState.Pending, h.StateFor(1));
        Assert.Null(h.Runtime.LicenseFailureFor(Kid(1)));
        h.Runtime.OnNativeEvent(fresh, PrNative.EvLicenseUsable, 5, 0, Kid(1));   // and it still hears the fresh licence
        Assert.Equal(LicenseCacheState.Usable, h.StateFor(1));
    }

    /// <summary>An <see cref="ILicenseDelivery"/> that runs <paramref name="onDeliver"/> with the delivered HRESULT, synchronously.</summary>
    private sealed class EventRaisingDelivery : ILicenseDelivery
    {
        private readonly Action<int> _onDeliver;
        private int _calls;

        public EventRaisingDelivery(Action<int> onDeliver) => _onDeliver = onDeliver;

        public int Calls => Volatile.Read(ref _calls);

        public void Deliver(ReadOnlySpan<byte> license, int hr)
        {
            Interlocked.Increment(ref _calls);
            _onDeliver(hr);
        }
    }

    [Fact]
    public void AnOldUsableRow_IsNeverStale_OnlyPendingOnesAre()
    {
        using var h = new Harness();
        long now = 1_000_000;
        h.Runtime.Clock = () => Volatile.Read(ref now);
        h.Ensure(1);
        ulong lic = h.HandleFor(1);
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 12, 0, Kid(1));

        Interlocked.Add(ref now, Hours(3));

        Assert.Equal(LicenseCacheState.Usable, h.Ensure(1));
        Assert.Equal(1, h.Native.AcquireCount);
    }

    private static long Hours(int hours) => hours * 3_600_000L;

    // ── the relay: a token per attempt, one retry on a transient failure (F011) ──────────────────────────────────────

    [Fact]
    public async Task Relay_ATransientFailure_IsRetriedOnce_AndTheSecondAttemptsLicenseIsDelivered()
    {
        using var h = new Harness();
        h.Runtime.RelayRetryBackoffMs = 0;
        int calls = 0;
        ValueTask<LicenseResponse> Flaky(LicenseRequest _)
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new System.Net.Http.HttpRequestException("connection reset", null, null);   // no HTTP status: a transport failure
            return ValueTask.FromResult(new LicenseResponse(new byte[] { 4, 2 }));
        }
        h.Ensure(1, Flaky);
        var delivery = new RecordingDelivery();

        Assert.Equal(0, h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery));

        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(1, delivery.Calls);
        Assert.Equal(0, delivery.Hr);
        Assert.Equal(new byte[] { 4, 2 }, delivery.Bytes);
        Assert.Null(h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
    public async Task Relay_ATransientFailureThatRepeats_FailsAfterTheSecondAttempt_WithTheRelaysOwnWords(System.Net.HttpStatusCode status)
    {
        using var h = new Harness();
        h.Runtime.RelayRetryBackoffMs = 0;
        int calls = 0;
        ValueTask<LicenseResponse> Failing(LicenseRequest _)
        {
            Interlocked.Increment(ref calls);
            throw new System.Net.Http.HttpRequestException("licence POST answered " + (int)status, null, status);
        }
        h.Ensure(1, Failing);
        var delivery = new RecordingDelivery();

        Assert.Equal(0, h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery));

        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(2, Volatile.Read(ref calls));   // exactly one retry
        Assert.Equal(1, delivery.Calls);
        Assert.Equal(ProtectedVideoRuntime.LicenseRelayFailedHr, delivery.Hr);
        Assert.Contains("answered " + (int)status, h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.BadRequest)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    [InlineData(System.Net.HttpStatusCode.NotFound)]
    public async Task Relay_APermanentFailure_IsNeverRetried(System.Net.HttpStatusCode status)
    {
        using var h = new Harness();
        h.Runtime.RelayRetryBackoffMs = 0;
        int calls = 0;
        ValueTask<LicenseResponse> Rejecting(LicenseRequest _)
        {
            Interlocked.Increment(ref calls);
            throw new System.Net.Http.HttpRequestException("licence POST answered " + (int)status, null, status);
        }
        h.Ensure(1, Rejecting);
        var delivery = new RecordingDelivery();

        Assert.Equal(0, h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery));

        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(ProtectedVideoRuntime.LicenseRelayFailedHr, delivery.Hr);
    }

    [Fact]
    public async Task Relay_AHungAttempt_IsCancelledByItsTimeoutToken_AndRetriedOnce()
    {
        using var h = new Harness();
        h.Runtime.RelayAttemptTimeoutMs = 50;
        h.Runtime.RelayRetryBackoffMs = 0;
        int calls = 0;
        CancellationToken firstToken = default;
        async ValueTask<LicenseResponse> HangsOnce(LicenseRequest request)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstToken = request.Cancel;
                await Task.Delay(Timeout.Infinite, request.Cancel);   // a stalled POST: only the token ends it
            }
            return new LicenseResponse(new byte[] { 6 });
        }
        h.Ensure(1, HangsOnce);
        var delivery = new RecordingDelivery();

        Assert.Equal(0, h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery));

        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal(0, delivery.Hr);
        Assert.Equal(new byte[] { 6 }, delivery.Bytes);
    }

    [Fact]
    public async Task Relay_AttemptsThatAlwaysHang_FailWithATimeoutMessage_AfterTheSecondAttempt()
    {
        using var h = new Harness();
        h.Runtime.RelayAttemptTimeoutMs = 30;
        h.Runtime.RelayRetryBackoffMs = 0;
        int calls = 0;
        async ValueTask<LicenseResponse> Hangs(LicenseRequest request)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.Infinite, request.Cancel);
            return new LicenseResponse(new byte[] { 1 });
        }
        h.Ensure(1, Hangs);
        var delivery = new RecordingDelivery();

        Assert.Equal(0, h.Runtime.OnChallenge(h.HandleFor(1), new byte[] { 1 }, Kid(1), delivery));

        await delivery.Delivered.Task.WaitAsync(Bound, Ct);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(ProtectedVideoRuntime.LicenseRelayFailedHr, delivery.Hr);
        Assert.Contains("timed out", h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public void IsTransientRelayFailure_ReadsTransportFailuresTimeoutsAndRetryableStatuses()
    {
        Assert.True(ProtectedVideoRuntime.IsTransientRelayFailure(new System.Net.Http.HttpRequestException("dns", null, null)));
        Assert.True(ProtectedVideoRuntime.IsTransientRelayFailure(new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.BadGateway)));
        Assert.True(ProtectedVideoRuntime.IsTransientRelayFailure(new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.RequestTimeout)));
        Assert.True(ProtectedVideoRuntime.IsTransientRelayFailure(new IOException("reset")));
        Assert.True(ProtectedVideoRuntime.IsTransientRelayFailure(new TimeoutException()));

        Assert.False(ProtectedVideoRuntime.IsTransientRelayFailure(new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.Forbidden)));
        Assert.False(ProtectedVideoRuntime.IsTransientRelayFailure(new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound)));
        Assert.False(ProtectedVideoRuntime.IsTransientRelayFailure(new InvalidOperationException("rejected")));
        Assert.False(ProtectedVideoRuntime.IsTransientRelayFailure(new OperationCanceledException()));
    }
}
