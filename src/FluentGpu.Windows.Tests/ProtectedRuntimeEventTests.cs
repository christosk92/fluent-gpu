using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The edges of <see cref="ProtectedVideoRuntime"/>'s event handling that a live CDM reaches only by accident: a license
/// event with no handle, an expiry naming a key session the row does not carry, a buffered wait that is cancelled or
/// whose session fails, and a native component that loads but is a stale build. Fakes only — no DLL, no CDM, no window.
/// </summary>
public sealed class ProtectedRuntimeEventTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly byte[] Pssh = { 1, 2, 3, 4 };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Kid(int i) => i.ToString("x32");

    private sealed class Harness : IDisposable
    {
        public readonly FakeRuntimeNative Native = new();
        public readonly string StorePath = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        public readonly ProtectedVideoRuntime Runtime;

        public Harness() => Runtime = new ProtectedVideoRuntime(Native, StorePath, idleMs: 60_000, sessionNative: new FakeSessionNative());

        public void Dispose()
        {
            Runtime.Dispose();
            try { if (Directory.Exists(StorePath)) Directory.Delete(StorePath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ── license events ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALicenseEventWithNoHandle_NeverAdoptsItselfOntoARowStillWaitingForOne()
    {
        using var h = new Harness();
        h.Native.DuringAcquire = (_, kid) =>
        {
            // Inside the acquire call the row is published with Handle = 0 — the one moment a 0 would match it by handle.
            h.Runtime.OnNativeEvent(0, PrNative.EvLicenseExpired, 0, 0, kid);
            h.Runtime.OnNativeEvent(0, PrNative.EvLicenseUsable, 5, 0, kid);
        };

        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);

        Assert.Equal(LicenseCacheState.Pending, h.Runtime.LicenseStateFor(Kid(1)));
        Assert.NotEqual(0ul, h.Runtime.LicenseHandleFor(Kid(1)));   // the real handle, from the acquire's return
    }

    [Fact]
    public void AnExpiryForAKeySessionTheRowDoesNotCarry_LeavesTheLiveRowUsable()
    {
        using var h = new Harness();
        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);
        ulong lic = h.Runtime.LicenseHandleFor(Kid(1));
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid(1));

        h.Runtime.OnNativeEvent(lic + 1000, PrNative.EvLicenseExpired, 0, 0, Kid(1));   // someone else's key session
        Assert.Equal(LicenseCacheState.Usable, h.Runtime.LicenseStateFor(Kid(1)));

        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseExpired, 0, 0, Kid(1));          // its own
        Assert.Equal(LicenseCacheState.Expired, h.Runtime.LicenseStateFor(Kid(1)));
    }

    [Fact]
    public void AnExpiryNeverTurnsAFailedRowIntoAnExpiredOne()
    {
        using var h = new Harness();
        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);
        ulong lic = h.Runtime.LicenseHandleFor(Kid(1));
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseFailed, unchecked((int)0x8004C600), 0, Kid(1));

        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseExpired, 0, 0, Kid(1));

        Assert.Equal(LicenseCacheState.Failed, h.Runtime.LicenseStateFor(Kid(1)));
    }

    [Theory]
    // rowExists, state, rowHandle, eventHandle → accepted
    [InlineData(true, LicenseCacheState.Usable, 7ul, 7ul, true)]
    [InlineData(true, LicenseCacheState.Pending, 7ul, 7ul, true)]
    [InlineData(true, LicenseCacheState.Usable, 7ul, 8ul, false)]
    [InlineData(true, LicenseCacheState.Usable, 0ul, 0ul, false)]
    [InlineData(true, LicenseCacheState.Failed, 7ul, 7ul, false)]
    [InlineData(true, LicenseCacheState.Expired, 7ul, 7ul, false)]
    [InlineData(false, LicenseCacheState.Usable, 7ul, 7ul, false)]
    public void AcceptExpiry_OnlyALiveRowsOwnNonZeroHandle(bool rowExists, LicenseCacheState state, ulong rowHandle,
        ulong eventHandle, bool accepted)
        => Assert.Equal(accepted, LicenseCachePolicy.AcceptExpiry(rowExists, state, rowHandle, eventHandle));

    // ── every key status maps (F045) ─────────────────────────────────────────────────────────────────────────────────

    private static int DeadHr(int status) => unchecked((int)(0x80048000u + (uint)status));

    [Fact]
    public void AUsableKeyThatTheCdmRevokes_TurnsTheRowFailed_WithAMessage_AndTheNextEnsureReacquires()
    {
        using var h = new Harness();
        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);
        ulong lic = h.Runtime.LicenseHandleFor(Kid(1));
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid(1));
        Assert.Equal(LicenseCacheState.Usable, h.Runtime.LicenseStateFor(Kid(1)));

        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseRevoked, DeadHr(6), 6, Kid(1));   // MF_MEDIAKEY_STATUS_RELEASED

        Assert.Equal(LicenseCacheState.Failed, h.Runtime.LicenseStateFor(Kid(1)));
        string? why = h.Runtime.LicenseFailureFor(Kid(1));
        Assert.Contains("revoked", why);
        Assert.Contains("80048006", why);

        // Native evicted the dead key session when it raised the event; the next ensure issues a fresh challenge.
        h.Native.NativeCloses(lic);
        Assert.Equal(LicenseCacheState.Pending, h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null));
        Assert.Equal(2, h.Native.AcquireCount);
        Assert.Equal(new[] { lic }, h.Native.Released);
        Assert.NotEqual(lic, h.Runtime.LicenseHandleFor(Kid(1)));
    }

    [Theory]
    [InlineData(3)]   // MF_MEDIAKEY_STATUS_OUTPUT_NOT_ALLOWED
    [InlineData(5)]   // MF_MEDIAKEY_STATUS_INTERNAL_ERROR
    [InlineData(6)]   // MF_MEDIAKEY_STATUS_RELEASED
    public void ARevocation_OfAnyDeadStatus_FailsTheRow_WithThatStatusesHresult(int status)
    {
        using var h = new Harness();
        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);
        ulong lic = h.Runtime.LicenseHandleFor(Kid(1));
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid(1));

        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseRevoked, DeadHr(status), status, Kid(1));

        Assert.Equal(LicenseCacheState.Failed, h.Runtime.LicenseStateFor(Kid(1)));
        Assert.Contains(DeadHr(status).ToString("X8"), h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public void ARevocationNamingAnotherKeySession_OrNoRow_LeavesTheLiveRowUsable_AndNeverAdoptsByKid()
    {
        using var h = new Harness();
        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);
        ulong lic = h.Runtime.LicenseHandleFor(Kid(1));
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid(1));

        h.Runtime.OnNativeEvent(lic + 1000, PrNative.EvLicenseRevoked, DeadHr(5), 5, Kid(1));   // a predecessor's key session
        h.Runtime.OnNativeEvent(0, PrNative.EvLicenseRevoked, DeadHr(5), 5, Kid(1));            // no handle
        h.Runtime.OnNativeEvent(0xDEAD, PrNative.EvLicenseRevoked, DeadHr(5), 5, Kid(2));       // no such row

        Assert.Equal(LicenseCacheState.Usable, h.Runtime.LicenseStateFor(Kid(1)));
        Assert.Null(h.Runtime.LicenseFailureFor(Kid(1)));
        Assert.Equal(LicenseCacheState.None, h.Runtime.LicenseStateFor(Kid(2)));
    }

    [Fact]
    public void ARevocationRacingTheHandleAssignment_IsNotAdoptedOntoTheRowByKid()
    {
        using var h = new Harness();
        // Inside the acquire the row has no handle yet: a revocation (which is about a key session that WAS usable) must not
        // adopt itself onto it the way a usable / failed completion may.
        h.Native.DuringAcquire = (_, kid) => h.Runtime.OnNativeEvent(0x7777, PrNative.EvLicenseRevoked, DeadHr(5), 5, kid);

        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);

        Assert.Equal(LicenseCacheState.Pending, h.Runtime.LicenseStateFor(Kid(1)));
        Assert.NotEqual(0x7777ul, h.Runtime.LicenseHandleFor(Kid(1)));
    }

    [Fact]
    public void ARevocationForAFailedRow_IsNotReapplied()
    {
        using var h = new Harness();
        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);
        ulong lic = h.Runtime.LicenseHandleFor(Kid(1));
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseFailed, unchecked((int)0x8004C600), 0, Kid(1));

        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseRevoked, DeadHr(5), 5, Kid(1));

        Assert.Contains("8004C600", h.Runtime.LicenseFailureFor(Kid(1)));   // the first failure's words stay
    }

    [Theory]
    [InlineData(7, 0)]   // OUTPUT_RESTRICTED in force
    [InlineData(2, 0)]   // OUTPUT_DOWNSCALED in force
    [InlineData(0, 7)]   // lifted again
    public void AnOutputRestriction_NeverChangesTheRowOrEvictsIt(int status, int previous)
    {
        using var h = new Harness();
        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);
        ulong lic = h.Runtime.LicenseHandleFor(Kid(1));
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid(1));

        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseRestricted, status, previous, Kid(1));
        h.Runtime.OnNativeEvent(0xDEAD, PrNative.EvLicenseRestricted, status, previous, Kid(1));   // no such key session: ignored

        Assert.Equal(LicenseCacheState.Usable, h.Runtime.LicenseStateFor(Kid(1)));
        Assert.Equal(lic, h.Runtime.LicenseHandleFor(Kid(1)));
        Assert.Equal(0, h.Native.ReleaseCount);
    }

    [Theory]
    // rowExists, state, rowHandle, eventHandle → accepted
    [InlineData(true, LicenseCacheState.Usable, 7ul, 7ul, true)]
    [InlineData(true, LicenseCacheState.Pending, 7ul, 7ul, true)]
    [InlineData(true, LicenseCacheState.Failed, 7ul, 7ul, false)]
    [InlineData(true, LicenseCacheState.Usable, 7ul, 8ul, false)]
    [InlineData(false, LicenseCacheState.Usable, 7ul, 7ul, false)]
    public void AcceptRevocation_MirrorsTheExpiryRule_ForTheFailedKeyStatuses(bool rowExists, LicenseCacheState state, ulong rowHandle,
        ulong eventHandle, bool accepted)
        => Assert.Equal(accepted, LicenseCachePolicy.AcceptRevocation(rowExists, state, rowHandle, eventHandle));

    // ── buffered waits ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACancelledBufferedWait_CompletesFalse_AndLeavesNothingInTheTable()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        Task<bool> wait = h.Runtime.WaitBufferedAsync(7, cts.Token);
        Assert.Equal(1, h.Runtime.PendingBufferedWaits);

        cts.Cancel();

        Assert.False(await wait.WaitAsync(Bound, Ct));
        Assert.Equal(0, h.Runtime.PendingBufferedWaits);
    }

    [Fact]
    public async Task AWaitOnAnAlreadyCancelledToken_CompletesFalseAtOnce()
    {
        using var h = new Harness();
        Task<bool> wait = h.Runtime.WaitBufferedAsync(7, new CancellationToken(canceled: true));

        Assert.False(await wait.WaitAsync(Bound, Ct));
        Assert.Equal(0, h.Runtime.PendingBufferedWaits);
    }

    [Fact]
    public async Task ACancellableWait_StillCompletesTrueOnMedia()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        Task<bool> wait = h.Runtime.WaitBufferedAsync(7, cts.Token);

        h.Runtime.OnNativeEvent(7, PrNative.EvBuffered, 4_000, 0, null);

        Assert.True(await wait.WaitAsync(Bound, Ct));   // the bool is visible on the cancellable path too
        cts.Cancel();                                  // a late cancel changes nothing
        Assert.True(await wait);
    }

    [Fact]
    public async Task AnErrorForTheSession_EndsItsBufferedWaitFalse()
    {
        using var h = new Harness();
        Task<bool> wait = h.Runtime.WaitBufferedAsync(7, CancellationToken.None);

        h.Runtime.OnNativeEvent(7, PrNative.EvError, 2, unchecked((int)0x80072EE7), null);

        Assert.False(await wait.WaitAsync(Bound, Ct));
        Assert.Equal(0, h.Runtime.PendingBufferedWaits);
    }

    [Fact]
    public async Task AnErrorForAnotherSession_LeavesTheWaitPending()
    {
        using var h = new Harness();
        Task<bool> wait = h.Runtime.WaitBufferedAsync(7, CancellationToken.None);

        h.Runtime.OnNativeEvent(8, PrNative.EvError, 2, unchecked((int)0x80072EE7), null);

        Assert.False(wait.IsCompleted);
        h.Runtime.CompleteBufferedWait(7, buffered: true);
        Assert.True(await wait.WaitAsync(Bound, Ct));
    }

    // ── events from the native notifier thread (F014 / F192) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ABatchDeliveredFromTheNotifierThread_AppliesInOrder_AndCompletesTheWait()
    {
        using var h = new Harness();
        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);
        ulong lic = h.Runtime.LicenseHandleFor(Kid(1));
        Task<bool> wait = h.Runtime.WaitBufferedAsync(7, CancellationToken.None);

        // The native ring hands events over from ONE notifier thread, in the order they were raised. Order is what makes a
        // revocation land on the row the usable event just made usable.
        await Task.Run(() =>
        {
            h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid(1));
            h.Runtime.OnNativeEvent(7, PrNative.EvBuffered, 4_000, 0, null);
            h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseRevoked, DeadHr(6), 6, Kid(1));
        }, Ct);

        Assert.True(await wait.WaitAsync(Bound, Ct));
        Assert.Equal(LicenseCacheState.Failed, h.Runtime.LicenseStateFor(Kid(1)));
        Assert.Contains("revoked", h.Runtime.LicenseFailureFor(Kid(1)));
    }

    [Fact]
    public void EventsFlushedAfterTheRuntimeIsDestroyed_FindNothingAndChangeNothing()
    {
        using var h = new Harness();
        h.Runtime.EnsureLicense(Pssh, Kid(1), relay: null);
        ulong lic = h.Runtime.LicenseHandleFor(Kid(1));
        h.Runtime.Dispose();

        // FgPrRuntimeDestroy flushes the native ring before it returns, so events for tables the managed side has already
        // cleared still arrive: a license row, a session, a device-removed error. None of them may throw or resurrect state.
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseUsable, 5, 0, Kid(1));
        h.Runtime.OnNativeEvent(lic, PrNative.EvLicenseRevoked, DeadHr(6), 6, Kid(1));
        h.Runtime.OnNativeEvent(7, PrNative.EvError, 2, unchecked((int)0x887A0005), null);
        h.Runtime.OnNativeEvent(7, PrNative.EvDetached, 0, 0, null);
        h.Runtime.OnNativeEvent(7, PrNative.EvBuffered, 4_000, 0, null);

        Assert.Equal(LicenseCacheState.None, h.Runtime.LicenseStateFor(Kid(1)));
        Assert.Equal(0, h.Runtime.PendingBufferedWaits);
    }

    // ── the native component ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AStaleBuild_MissingOneExport_IsUnavailable_AndNamesTheExport()
    {
        Assert.Null(PrRuntimeNative.FirstMissingExport(_ => true));
        Assert.Equal("FgPrSessionGetInitProtection",
            PrRuntimeNative.FirstMissingExport(name => name != "FgPrSessionGetInitProtection"));
        Assert.Equal("FgPrRuntimeCreateOnAdapter", PrRuntimeNative.FirstMissingExport(name => name.StartsWith("FgPlayReady", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheRequiredExports_AreExactlyTheOnesTheManagedSideBinds()
    {
        string[] bound = Array.ConvertAll(typeof(PrNative).GetMethods(
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly),
            m => m.Name);
        foreach (string export in PrRuntimeNative.RequiredExports) Assert.Contains(export, bound);
        foreach (string name in bound)
            if (name.StartsWith("FgPr", StringComparison.Ordinal)) Assert.Contains(name, PrRuntimeNative.RequiredExports);
    }
}
