using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.WindowsApi.Notifications;
using FluentGpu.WindowsApi.Packaging;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.WinRT.WinRT;
using Vtbl = FluentGpu.WindowsApi.Packaging.Vtbl;

// The Microsoft Store's in-app "rate and review" dialog (Windows.Services.Store.StoreContext.RequestRateAndReviewAppAsync)
// without CsWinRT, WindowsAppSDK or ComWrappers: RoGetActivationFactory plus hand-written vtable slots, built the way
// Packaging/PackageUpdater.cs drives Windows.Management.Deployment. The dialog is STARTED on the caller's (UI) thread,
// because a desktop process must hand the Store its owner window through IInitializeWithWindow first; the operation is
// then polled to completion on a worker through WinRtAsync.Wait, so the frame never blocks on the Store.

namespace FluentGpu.WindowsApi.Store;

/// <summary>
/// The outcome of <see cref="StoreRating.RequestAsync"/>. The first four mirror the WinRT
/// <c>Windows.Services.Store.StoreRateAndReviewStatus</c> ordinal for ordinal (<c>windows.services.store.h:3403-3408</c>,
/// SDK 10.0.26100); <see cref="Unavailable"/> is ours and means the dialog was never asked for.
/// </summary>
public enum StoreRatingStatus : byte
{
    /// <summary>The user submitted a rating or review (<c>StoreRateAndReviewStatus_Succeeded</c> = 0).</summary>
    Succeeded,

    /// <summary>The user closed the dialog without submitting (<c>StoreRateAndReviewStatus_CanceledByUser</c> = 1).</summary>
    CanceledByUser,

    /// <summary>The Store could not reach its service (<c>StoreRateAndReviewStatus_NetworkError</c> = 2).</summary>
    NetworkError,

    /// <summary>The request STARTED but gave no answer: the Store reported an error
    /// (<c>StoreRateAndReviewStatus_Error</c> = 3), the operation failed, the caller's token cancelled it, or its result
    /// could not be read.</summary>
    Error,

    /// <summary>The dialog was never requested, so nothing was shown: no owner window, an elevated process, no package
    /// identity, Windows older than 10 1809 (build 17763), a token already cancelled, or the Store context could not be
    /// activated, initialized with the owner, or asked (any failing HRESULT before the operation exists).</summary>
    Unavailable,
}

/// <summary>
/// <c>Windows.Services.Store.StoreContext.RequestRateAndReviewAppAsync</c> (Windows 10 1809+): the Store's own
/// rate-and-review dialog, owned by the app's window. Never throws.
/// </summary>
/// <remarks>
/// <para>
/// <b>The flow.</b> <c>RoGetActivationFactory("Windows.Services.Store.StoreContext", IStoreContextStatics)</c> →
/// <c>GetDefault</c> → QI <c>IInitializeWithWindow</c> → <c>Initialize(owner)</c> → QI <c>IStoreContext4</c> →
/// <c>RequestRateAndReviewAppAsync</c> → (worker) <see cref="WinRtAsync.Wait"/> → <c>GetResults</c> →
/// <c>IStoreRateAndReviewResult.get_Status</c> → <see cref="StoreRatingStatus"/>.
/// </para>
/// <para>
/// <b>Threading.</b> Everything up to and including <c>RequestRateAndReviewAppAsync</c> runs synchronously on the
/// CALLING thread, which must be the UI thread that owns <c>owner</c>: a desktop (non-CoreWindow) process has to give
/// the Store its owner window through <c>IInitializeWithWindow</c>, and Microsoft requires the request to come from the
/// UI thread. The statics, the context and its two interfaces are released on that thread before this method
/// returns. The operation itself is handed to one short-lived background MTA thread that polls <c>IAsyncInfo</c>
/// (WinRT async operations are agile, so they may be polled, read and closed from any apartment) and completes the
/// returned task from there; continuations run asynchronously, never inline on that worker. Nothing blocks the frame.
/// </para>
/// <para>
/// <b>Elevation.</b> An ELEVATED process is answered <see cref="StoreRatingStatus.Unavailable"/> without touching WinRT:
/// <c>InitializeWithWindow</c> followed by a Store request in an elevated process dies with a native access violation
/// that no managed handler can catch (microsoft-ui-xaml#10755). The token is read through the assembly's existing
/// <c>ProcessElevation</c> (<c>OpenProcessToken</c> + <c>GetTokenInformation(TokenElevation)</c>, <c>LibraryImport</c>).
/// </para>
/// <para>
/// <b>Errors.</b> A failure before the operation exists is <see cref="StoreRatingStatus.Unavailable"/>; once it exists
/// every failure is <see cref="StoreRatingStatus.Error"/>. Each non-answer writes one <c>[store-rating]</c> line through
/// <see cref="Diag.Line"/> with the reason and HRESULT, so an "unavailable" in the field can be explained.
/// </para>
/// <para>
/// <b>Cancellation.</b> <c>ct</c> is checked before anything starts (<see cref="StoreRatingStatus.Unavailable"/>), then on
/// every poll: a signalled token calls <c>IAsyncInfo.Cancel</c> (which closes the dialog), drains the operation to a
/// terminal state, and reports <see cref="StoreRatingStatus.Error"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.10240.0")]
public static unsafe class StoreRating
{
    /// <summary>Windows 10 1809: <c>IStoreContext4</c> arrived with <c>StoreContract</c> 4.0
    /// (<c>windows.services.store.h:12544</c>), which shipped in build 17763.</summary>
    private const int MinimumOsBuild = 17763;

    /// <summary>How often the worker samples the operation. The answer lands after the user dismisses the dialog, so a
    /// fifth of a second is invisible and keeps an open dialog to five wake-ups a second.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Shows the Store's rate-and-review dialog owned by <paramref name="owner"/> and completes with the user's answer.
    /// Call it on the UI thread that owns <paramref name="owner"/>; the wait happens on a worker. Never throws.
    /// </summary>
    /// <param name="owner">The top-level window (<c>HWND</c>) that owns the dialog. Zero is <see cref="StoreRatingStatus.Unavailable"/>.</param>
    /// <param name="ct">Cancels the request: before it starts, nothing is shown; while the dialog is up, the operation is
    /// cancelled and the result is <see cref="StoreRatingStatus.Error"/>.</param>
    /// <returns>The user's answer, <see cref="StoreRatingStatus.Error"/> when the started request gave none, or
    /// <see cref="StoreRatingStatus.Unavailable"/> when it could not be started.</returns>
    public static Task<StoreRatingStatus> RequestAsync(nint owner, CancellationToken ct = default)
    {
        nint operation;
        try
        {
            if (WhyUnavailable(owner, ct) is { } why)
                return Unavailable(why);

            operation = Start(owner, out string? failure);
            if (operation == 0)
                return Unavailable(failure ?? "the request returned no operation");
        }
        catch (Exception ex)
        {
            return Unavailable("threw " + ex.GetType().Name);
        }

        return WaitOnWorker(operation, ct);
    }

    /// <summary>The reasons to answer <see cref="StoreRatingStatus.Unavailable"/> without touching WinRT, or
    /// <see langword="null"/> when the request may go ahead. Order matters only for the reported reason.</summary>
    private static string? WhyUnavailable(nint owner, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return "cancelled before start";
        if (owner == 0)
            return "no owner window";
        // FIRST of the OS checks, and before any WinRT call: microsoft-ui-xaml#10755 is an uncatchable native AV.
        if (ProcessElevation.IsElevated())
            return "elevated process (microsoft-ui-xaml#10755)";
        if (!PackageIdentity.IsPackaged)
            return "no package identity";
        int build = PackageIdentity.OsBuild;
        if (build < MinimumOsBuild)
            return "Windows build " + build + " is older than " + MinimumOsBuild;
        return null;
    }

    /// <summary>
    /// Activation through <c>RequestRateAndReviewAppAsync</c>, on the calling (UI) thread. Returns the operation
    /// (<c>IAsyncOperation&lt;StoreRateAndReviewResult&gt;*</c>, one reference owned by the caller) or 0 with the failed
    /// step in <paramref name="failure"/>. Every other pointer is released here, on every path.
    /// </summary>
    private static nint Start(nint owner, out string? failure)
    {
        failure = null;
        void* statics = null;
        void* context = null;
        void* initializeWithWindow = null;
        void* context4 = null;
        try
        {
            int hr;
            using (var className = new HStringHandle(StoreAbi.RuntimeClassStoreContext))
            {
                Guid iidStatics = StoreAbi.IStoreContextStatics;
                hr = RoGetActivationFactory(className.Value, &iidStatics, &statics);
            }
            if (hr < 0 || statics == null)
            {
                failure = Step("RoGetActivationFactory(StoreContext)", hr);
                return 0;
            }

            hr = StoreAbi.GetDefault(statics, &context);
            if (hr < 0 || context == null)
            {
                failure = Step("IStoreContextStatics.GetDefault", hr);
                return 0;
            }

            Guid iidInitialize = StoreAbi.IInitializeWithWindow;
            hr = Vtbl.QueryInterface(context, &iidInitialize, &initializeWithWindow);
            if (hr < 0 || initializeWithWindow == null)
            {
                failure = Step("QueryInterface(IInitializeWithWindow)", hr);
                return 0;
            }

            hr = StoreAbi.Initialize(initializeWithWindow, owner);
            if (hr < 0)
            {
                failure = Step("IInitializeWithWindow.Initialize", hr);
                return 0;
            }

            Guid iidContext4 = StoreAbi.IStoreContext4;
            hr = Vtbl.QueryInterface(context, &iidContext4, &context4);
            if (hr < 0 || context4 == null)
            {
                failure = Step("QueryInterface(IStoreContext4)", hr);
                return 0;
            }

            void* operation = null;
            hr = StoreAbi.RequestRateAndReviewAppAsync(context4, &operation);
            if (hr < 0 || operation == null)
            {
                failure = Step("IStoreContext4.RequestRateAndReviewAppAsync", hr);
                return 0;
            }
            return (nint)operation;
        }
        finally
        {
            Vtbl.Release(context4);
            Vtbl.Release(initializeWithWindow);
            Vtbl.Release(context);
            Vtbl.Release(statics);
        }
    }

    /// <summary>Hand the started <paramref name="operation"/> to a background MTA thread that waits for it. If no thread
    /// can be had, the operation is cancelled and released here (not closed: <c>Close</c> is legal only on a terminal
    /// operation, and nothing is left to wait for it).</summary>
    private static Task<StoreRatingStatus> WaitOnWorker(nint operation, CancellationToken ct)
    {
        var done = new TaskCompletionSource<StoreRatingStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var worker = new Thread(() =>
            {
                StoreRatingStatus status;
                try
                {
                    status = Finish(operation, ct);
                }
                catch
                {
                    // Finish catches everything itself; this guard exists because a throw escaping a raw thread
                    // would end the process.
                    status = StoreRatingStatus.Error;
                }
                done.TrySetResult(status);
            })
            {
                IsBackground = true,
                Name = "FluentGpu.StoreRating",
            };
            // MTA before Start: the runtime joins the thread to the process MTA for its lifetime and leaves it at exit.
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            return done.Task;
        }
        catch (Exception ex)
        {
            WinRtAsync.Cancel(operation);
            WinRtAsync.Release(operation);
            Note("error: no worker thread (" + ex.GetType().Name + ")");
            return Task.FromResult(StoreRatingStatus.Error);
        }
    }

    /// <summary>
    /// The worker half: poll the operation to a terminal state, read <c>IStoreRateAndReviewResult.Status</c>, map it.
    /// Always closes and releases <paramref name="operation"/> (<see cref="WinRtAsync.Close"/>) and the result.
    /// </summary>
    private static StoreRatingStatus Finish(nint operation, CancellationToken ct)
    {
        void* result = null;
        try
        {
            (AsyncStatus status, int errorCode) = WinRtAsync.Wait(operation, null, PollInterval, ct);
            if (status != AsyncStatus.Completed)
            {
                Note(status == AsyncStatus.Canceled
                    ? "error: cancelled by the caller"
                    : "error: the operation failed, hr=0x" + ((uint)errorCode).ToString("X8"));
                return StoreRatingStatus.Error;
            }

            int hr = Vtbl.AsyncOperationGetResults((void*)operation, &result);
            if (hr < 0 || result == null)
            {
                Note("error: " + Step("GetResults", hr));
                return StoreRatingStatus.Error;
            }

            int raw = -1;   // StoreRateAndReviewStatus is a 32-bit enum (": int", windows.services.store.h:3403).
            hr = StoreAbi.GetStatus(result, &raw);
            if (hr < 0)
            {
                Note("error: " + Step("IStoreRateAndReviewResult.get_Status", hr));
                return StoreRatingStatus.Error;
            }

            switch (raw)
            {
                case 0: return StoreRatingStatus.Succeeded;
                case 1: return StoreRatingStatus.CanceledByUser;
                case 2: return StoreRatingStatus.NetworkError;
            }

            int extended = 0;
            _ = StoreAbi.GetExtendedError(result, &extended);
            Note("error: the Store answered status " + raw + ", extended hr=0x" + ((uint)extended).ToString("X8"));
            return StoreRatingStatus.Error;
        }
        catch (Exception ex)
        {
            Note("error: threw " + ex.GetType().Name);
            return StoreRatingStatus.Error;
        }
        finally
        {
            Vtbl.Release(result);
            WinRtAsync.Close(operation);
        }
    }

    private static Task<StoreRatingStatus> Unavailable(string why)
    {
        Note("unavailable: " + why);
        return Task.FromResult(StoreRatingStatus.Unavailable);
    }

    private static string Step(string step, int hr) => step + " hr=0x" + ((uint)hr).ToString("X8");

    /// <summary>One <c>[store-rating]</c> evidence line. Guarded so a throwing diagnostics sink can never break the
    /// never-throws contract.</summary>
    private static void Note(string line)
    {
        try
        {
            Diag.Line("[store-rating] " + line);
        }
        catch
        {
            // Diagnostics only.
        }
    }
}

/// <summary>
/// Hand-rolled call-OUT slots over <c>Windows.Services.Store</c> and <c>IInitializeWithWindow</c>, which
/// <c>TerraFX.Interop.Windows</c> does not project as WinRT (no <c>IStoreContext*</c> at all).
/// </summary>
/// <remarks>
/// <para>
/// <b>Slot discipline</b> (as in <c>Packaging/PackageManagerInterop.cs</c>): a WinRT interface derives from
/// <c>IInspectable</c>, so slots 0-2 are <c>IUnknown</c> (<c>QueryInterface</c>, <c>AddRef</c>, <c>Release</c>), 3-5
/// are <c>IInspectable</c> (<c>GetIids</c>, <c>GetRuntimeClassName</c>, <c>GetTrustLevel</c>), and the interface's own
/// methods follow in declaration order from slot 6. <c>IInitializeWithWindow</c> is a classic COM interface deriving
/// from <c>IUnknown</c> only, so its one method is slot 3. Every IID and slot below is copied from the C ABI vtable
/// structs of the Windows SDK 10.0.26100 headers <c>winrt/windows.services.store.h</c> and <c>um/ShObjIdl_core.h</c>,
/// with the header line quoted. <c>IAsyncOperation&lt;T&gt;.GetResults</c> (slot 8,
/// <c>windows.services.store.h:8418</c>; 6 <c>put_Completed</c>, 7 <c>get_Completed</c>) is the shared
/// <see cref="Vtbl.AsyncOperationGetResults"/>; QueryInterface and Release are <see cref="Vtbl"/>'s too.
/// </para>
/// <para>
/// IIDs that are documented here but never needed, because every pointer arrives typed from the call before it:
/// <c>IStoreRateAndReviewResult</c> <c>9d209d56-a6b5-4121-9b61-ee6d0fbdbdbb</c> (<c>windows.services.store.h:5132</c>)
/// and <c>IAsyncOperation&lt;StoreRateAndReviewResult&gt;</c> <c>844673ec-3402-5a20-becf-e92c3f9681ea</c>
/// (<c>windows.services.store.h:1683</c>).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
file static unsafe class StoreAbi
{
    /// <summary><c>RuntimeClass_Windows_Services_Store_StoreContext</c> — <c>windows.services.store.h:5654</c>.
    /// A statics-only class here: <c>RoGetActivationFactory</c> + <c>IStoreContextStatics.GetDefault</c>.</summary>
    internal const string RuntimeClassStoreContext = "Windows.Services.Store.StoreContext";

    /// <summary><c>IStoreContextStatics</c> — <c>MIDL_INTERFACE("9c06ee5f-15c0-4e72-9330-d6191cebd19c")</c>,
    /// <c>windows.services.store.h:4150</c>.</summary>
    internal static readonly Guid IStoreContextStatics = new("9c06ee5f-15c0-4e72-9330-d6191cebd19c");

    /// <summary><c>IStoreContext4</c> — <c>MIDL_INTERFACE("af9c6f69-bea1-4bf4-8e74-ae03e206c6b0")</c>,
    /// <c>windows.services.store.h:4065</c> (StoreContract 4.0, Windows 10 1809).</summary>
    internal static readonly Guid IStoreContext4 = new("af9c6f69-bea1-4bf4-8e74-ae03e206c6b0");

    /// <summary><c>IInitializeWithWindow</c> — <c>MIDL_INTERFACE("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")</c>,
    /// <c>ShObjIdl_core.h:32603</c>.</summary>
    internal static readonly Guid IInitializeWithWindow = new("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1");

    /// <summary><c>HRESULT IStoreContextStatics::GetDefault(IStoreContext** value)</c> — vtable slot <b>6</b>
    /// (<c>windows.services.store.h:12727</c>; slot 7 is <c>GetForUser</c>).</summary>
    internal static int GetDefault(void* statics, void** context)
        => ((delegate* unmanaged<void*, void**, int>)(*(void***)statics)[6])(statics, context);

    /// <summary><c>HRESULT IInitializeWithWindow::Initialize(HWND hwnd)</c> — vtable slot <b>3</b>
    /// (<c>ShObjIdl_core.h:32635</c>; <c>IUnknown</c> 0-2, no <c>IInspectable</c>).</summary>
    internal static int Initialize(void* initializeWithWindow, nint hwnd)
        => ((delegate* unmanaged<void*, nint, int>)(*(void***)initializeWithWindow)[3])(initializeWithWindow, hwnd);

    /// <summary><c>HRESULT IStoreContext4::RequestRateAndReviewAppAsync(IAsyncOperation&lt;StoreRateAndReviewResult&gt;** operation)</c>
    /// — vtable slot <b>6</b> (<c>windows.services.store.h:12570</c>; slot 7 is
    /// <c>SetInstallOrderForAssociatedStoreQueueItemsAsync</c>).</summary>
    internal static int RequestRateAndReviewAppAsync(void* context4, void** operation)
        => ((delegate* unmanaged<void*, void**, int>)(*(void***)context4)[6])(context4, operation);

    /// <summary><c>HRESULT IStoreRateAndReviewResult::get_ExtendedError(HRESULT* value)</c> — vtable slot <b>6</b>
    /// (<c>windows.services.store.h:14558</c>). Read only to explain a <c>StoreRateAndReviewStatus_Error</c>.</summary>
    internal static int GetExtendedError(void* result, int* value)
        => ((delegate* unmanaged<void*, int*, int>)(*(void***)result)[6])(result, value);

    /// <summary><c>HRESULT IStoreRateAndReviewResult::get_Status(StoreRateAndReviewStatus* value)</c> — vtable slot
    /// <b>9</b> (<c>windows.services.store.h:14564</c>; 6 <c>get_ExtendedError</c>, 7 <c>get_ExtendedJsonData</c>,
    /// 8 <c>get_WasUpdated</c>). Ordinals: Succeeded 0, CanceledByUser 1, NetworkError 2, Error 3
    /// (<c>windows.services.store.h:11470-11473</c>).</summary>
    internal static int GetStatus(void* result, int* value)
        => ((delegate* unmanaged<void*, int*, int>)(*(void***)result)[9])(result, value);
}
