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

    // ── the native component ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AStaleBuild_MissingOneExport_IsUnavailable_AndNamesTheExport()
    {
        Assert.Null(PrRuntimeNative.FirstMissingExport(_ => true));
        Assert.Equal("FgPrSessionGetInitProtection",
            PrRuntimeNative.FirstMissingExport(name => name != "FgPrSessionGetInitProtection"));
        Assert.Equal("FgPrRuntimeCreate", PrRuntimeNative.FirstMissingExport(name => name.StartsWith("FgPlayReady", StringComparison.Ordinal)));
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
