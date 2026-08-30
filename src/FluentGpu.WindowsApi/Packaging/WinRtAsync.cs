using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.WinRT.WinRT;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Packaging;

/// <summary>
/// Waits on a WinRT <c>IAsyncOperation</c>/<c>IAsyncOperationWithProgress</c> by polling <c>IAsyncInfo.get_Status</c>,
/// plus the <c>Cancel</c>/<c>Close</c> lifetime helpers that go with it.
/// </summary>
/// <remarks>
/// <para>
/// This is <c>WindowsGeolocationProvider.WaitForAsync</c> generalized: the same status-polling shape, but with the
/// per-poll hook a deployment needs (to sample a progress sink) and without geolocation's timeout/result mapping.
/// Geolocation keeps its own private copy — this file adds a helper, it does not refactor that one.
/// </para>
/// <para>
/// <b>Why polling and not <c>put_Completed</c>.</b> A completion handler is a call-IN COM object, and installing one
/// would put the continuation on an arbitrary WinRT thread. Polling <c>IAsyncInfo</c> keeps every WinRT touch on the
/// caller's own thread, which is what lets <see cref="PackageUpdater"/> confine all of this to one MTA worker.
/// </para>
/// <para>
/// <b>Threading contract.</b> <see cref="Wait"/> BLOCKS the calling thread and must be called on the same apartment
/// thread that created <paramref name="operation"/>. <see cref="WaitAsync"/> exists for callers that want the
/// <c>Task</c> shape; it runs the same blocking loop inline and hands back an already-completed task, so it must not
/// be called from a thread that may not block (never from the UI thread).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.10240.0")]
internal static unsafe class WinRtAsync
{
    private const int E_ABORT = unchecked((int)0x80004004);

    /// <summary>Ceiling on the post-<c>Cancel</c> drain in <see cref="Wait"/>. <c>IAsyncInfo.Close</c> is only legal on
    /// a terminal operation, so cancellation must not return while the operation is still <c>Started</c>; a deployment
    /// that ignores the cancel forever must not wedge the caller either, hence the bound.</summary>
    private static readonly TimeSpan CancelDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Blocks until <paramref name="operation"/> leaves <see cref="AsyncStatus.Started"/>, calling
    /// <paramref name="onPoll"/> once per poll tick.
    /// </summary>
    /// <param name="operation">The WinRT async operation (any interface — <c>IAsyncInfo</c> is obtained by QI).</param>
    /// <param name="onPoll">Invoked after each status read while the operation is still running; use it to sample a
    /// progress sink. Never invoked after the terminal status is observed.</param>
    /// <param name="pollInterval">How long to sleep between status reads.</param>
    /// <param name="ct">When signalled, the operation is cancelled, drained to a terminal status (bounded by
    /// <see cref="CancelDrainTimeout"/> so the caller's <c>Close</c> is legal), and <see cref="AsyncStatus.Canceled"/>
    /// is returned.</param>
    /// <returns>The terminal status and, for <see cref="AsyncStatus.Error"/>/<see cref="AsyncStatus.Canceled"/>, the
    /// HRESULT. A failure to reach <c>IAsyncInfo</c> at all is reported as <see cref="AsyncStatus.Error"/> with that
    /// HRESULT — this method never throws.</returns>
    internal static (AsyncStatus Status, int ErrorCode) Wait(
        nint operation,
        Action? onPoll,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        if (operation == 0)
            return (AsyncStatus.Error, E_ABORT);

        int queryHr = QueryAsyncInfo(operation, out nint asyncInfo);
        if (queryHr < 0 || asyncInfo == 0)
            return (AsyncStatus.Error, queryHr < 0 ? queryHr : E_ABORT);

        var info = (IAsyncInfo*)asyncInfo;
        try
        {
            while (true)
            {
                AsyncStatus status = AsyncStatus.Started;
                int hr = info->get_Status(&status);
                if (hr < 0)
                    return (AsyncStatus.Error, hr);

                switch (status)
                {
                    case AsyncStatus.Completed:
                        return (AsyncStatus.Completed, 0);
                    case AsyncStatus.Canceled:
                        return (AsyncStatus.Canceled, E_ABORT);
                    case AsyncStatus.Error:
                    {
                        TerraFX.Interop.Windows.HRESULT error;
                        int readHr = info->get_ErrorCode(&error);
                        return (AsyncStatus.Error, readHr < 0 ? readHr : error.Value);
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    // FIX: returning straight after Cancel() left the operation possibly still Started, and the caller's
                    // finally then called IAsyncInfo.Close() on a non-terminal operation — a contract violation. Cancel
                    // is a REQUEST; drain to a terminal status (bounded) so the caller's Close is always legal.
                    info->Cancel();
                    DrainToTerminal(info, pollInterval);
                    // Still reported as Canceled even if the drain observed Completed/Error: the caller asked to cancel,
                    // and the deployment result of a cancelled operation is not something callers act on.
                    return (AsyncStatus.Canceled, E_ABORT);
                }

                onPoll?.Invoke();

                // Thread.Sleep, not Task.Delay: the continuation must stay on this (apartment-bound) thread.
                Thread.Sleep(pollInterval);
            }
        }
        finally
        {
            info->Release();
        }
    }

    /// <summary>
    /// Poll <paramref name="info"/> until it leaves <see cref="AsyncStatus.Started"/>, for at most
    /// <see cref="CancelDrainTimeout"/>. Called only after <c>Cancel</c>, so that the operation the caller is about to
    /// <c>Close</c> has actually reached a terminal state. A failed status read ends the drain (nothing better is
    /// available); the caller's <c>Close</c> is best-effort in that case, exactly as it was before.
    /// </summary>
    private static void DrainToTerminal(IAsyncInfo* info, TimeSpan pollInterval)
    {
        // A zero/negative interval would spin; give the drain a floor of its own.
        TimeSpan step = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromMilliseconds(10);
        TimeSpan waited = TimeSpan.Zero;
        while (waited < CancelDrainTimeout)
        {
            AsyncStatus status = AsyncStatus.Started;
            if (info->get_Status(&status) < 0 || status != AsyncStatus.Started)
                return;
            Thread.Sleep(step);
            waited += step;
        }
    }

    /// <summary>
    /// <see cref="Wait"/> in <c>Task</c> clothing — the loop runs inline on the calling thread and the returned task
    /// is already complete. Provided for call sites that compose with other tasks; it does NOT move the work off the
    /// caller's thread.
    /// </summary>
    internal static Task<(AsyncStatus Status, int ErrorCode)> WaitAsync(
        nint operation,
        Action? onPoll,
        TimeSpan pollInterval,
        CancellationToken ct)
        => Task.FromResult(Wait(operation, onPoll, pollInterval, ct));

    /// <summary>Request cancellation of a running operation (<c>IAsyncInfo.Cancel</c>). Best-effort and null-tolerant;
    /// the operation still has to be waited to a terminal state.</summary>
    internal static void Cancel(nint operation)
    {
        if (operation == 0 || QueryAsyncInfo(operation, out nint asyncInfo) < 0 || asyncInfo == 0)
            return;
        var info = (IAsyncInfo*)asyncInfo;
        try
        {
            info->Cancel();
        }
        finally
        {
            info->Release();
        }
    }

    /// <summary>Close a completed operation (<c>IAsyncInfo.Close</c>) and release the operation pointer. Null-tolerant;
    /// this is the paired teardown for every operation obtained from a WinRT call.</summary>
    internal static void Close(nint operation)
    {
        if (operation == 0)
            return;
        if (QueryAsyncInfo(operation, out nint asyncInfo) >= 0 && asyncInfo != 0)
        {
            var info = (IAsyncInfo*)asyncInfo;
            try
            {
                info->Close();
            }
            finally
            {
                info->Release();
            }
        }
        ((IInspectable*)operation)->Release();
    }

    /// <summary>Release any <c>IUnknown</c>-derived pointer held as an <see cref="nint"/>. Null-tolerant.</summary>
    internal static void Release(nint value)
    {
        if (value != 0)
            ((IInspectable*)value)->Release();
    }

    /// <summary>
    /// Initialize the calling thread's WinRT apartment as MTA. <c>S_FALSE</c> (already initialized) and
    /// <c>RPC_E_CHANGED_MODE</c> (already in another apartment) are both treated as success — the caller only needs
    /// <i>an</i> apartment, and the deployment APIs are agile.
    /// </summary>
    internal static int EnsureRoInitializedMultiThreaded()
    {
        const int S_FALSE = 1;
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        int hr = RoInitialize(RO_INIT_TYPE.RO_INIT_MULTITHREADED);
        return hr >= 0 || hr == S_FALSE || hr == RPC_E_CHANGED_MODE ? 0 : hr;
    }

    private static int QueryAsyncInfo(nint operation, out nint asyncInfo)
    {
        asyncInfo = 0;
        IAsyncInfo* info = null;
        Guid iid = __uuidof<IAsyncInfo>();
        int hr = ((IInspectable*)operation)->QueryInterface(&iid, (void**)&info);
        if (hr >= 0 && info != null)
            asyncInfo = (nint)info;
        return hr < 0 ? hr : info == null ? E_ABORT : 0;
    }
}
