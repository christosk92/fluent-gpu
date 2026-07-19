using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;   // __uuidof<T>

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// A reusable <c>await</c>-adapter for WinRT <c>IAsyncAction</c> and <c>IAsyncOperation&lt;T&gt;</c>, returning a clean
/// <see cref="Task"/>. It drives completion by polling <c>IAsyncInfo.Status</c> rather than installing a
/// parameterized <c>IAsyncOperationCompletedHandler&lt;T&gt;</c> CCW.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why polling.</b> A completed-handler CCW would need the DERIVED parameterized IID of
/// <c>IAsyncOperationCompletedHandler&lt;AdaptiveMediaSourceCreationResult&gt;</c>; a wrong derivation silently never
/// fires the callback and the task hangs forever — the single most failure-prone value in the whole path. Polling
/// <c>IAsyncInfo.Status</c> (a non-generic, TerraFX-projected interface, QI'd from any async object) is
/// deterministic, needs no derived IID, and is correct on any thread/apartment. These operations are cold (source
/// creation, individualization) and off any per-frame hot path, so the ~15 ms poll cadence is irrelevant to
/// performance. This is a deliberate, lower-risk implementation of the same await-adapter contract.
/// </para>
/// <para>
/// <b>Ownership.</b> These helpers never release the async object — the caller owns the <c>IAsyncAction*</c> /
/// <c>IAsyncOperation&lt;T&gt;*</c> it passed in (as an <see cref="nint"/>) and releases it after the returned task
/// completes. The <c>IAsyncInfo</c> QI'd for each status probe is released within the probe.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.10240.0")]
internal static class WinRtAsync
{
    private const int PollIntervalMs = 15;
    private const int E_ABORT = unchecked((int)0x80004004);

    /// <summary>Await an <c>IAsyncOperation&lt;T&gt;</c> (passed as <paramref name="asyncOp"/>) and return its
    /// <c>GetResults</c> pointer as an <see cref="nint"/> (AddRef-owned by the runtime's result; the caller owns it).
    /// For <c>CreateFromUriAsync</c> the result is an <c>IAdaptiveMediaSourceCreationResult*</c>.</summary>
    public static async Task<nint> AwaitOperationResultAsync(nint asyncOp, CancellationToken ct)
    {
        while (true)
        {
            AsyncStatus status = GetStatus(asyncOp);
            switch (status)
            {
                case AsyncStatus.Completed:
                    return GetOperationResults(asyncOp);
                case AsyncStatus.Error:
                    int hr = GetErrorCode(asyncOp);
                    throw new InvalidOperationException($"WinRT async operation failed (0x{(uint)hr:X8}).");
                case AsyncStatus.Canceled:
                    throw new OperationCanceledException(ct);
                default:
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Await an <c>IAsyncAction</c> (passed as <paramref name="action"/>) and return the completion HRESULT:
    /// S_OK on clean completion, or the async error code on failure (which the individualization flow inspects for the
    /// <c>MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED</c> sentinel). Never throws for an async Error — it returns the
    /// HRESULT so the caller can branch on it.</summary>
    public static async Task<int> AwaitActionAsync(nint action, CancellationToken ct)
    {
        while (true)
        {
            AsyncStatus status = GetStatus(action);
            switch (status)
            {
                case AsyncStatus.Completed:
                    return GetActionResults(action);
                case AsyncStatus.Error:
                    return GetErrorCode(action);
                case AsyncStatus.Canceled:
                    return E_ABORT;
                default:
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Await ANY WinRT async object (Action / Operation / OperationWithProgress) to terminal state by polling
    /// <c>IAsyncInfo</c>. Returns <c>S_OK</c> (0) on Completed, the error HRESULT on Error, <c>E_ABORT</c> on Canceled.
    /// Does NOT call <c>GetResults</c>, so it needs no per-shape vtable slot — ideal when the caller re-verifies the
    /// effect another way (e.g. <c>PackageManager.AddPackageAsync</c> → re-resolve the package). The async object is
    /// passed as an <see cref="nint"/> (no pointer crosses the await); the caller owns + releases it.</summary>
    public static async Task<int> AwaitStatusAsync(nint async, CancellationToken ct)
    {
        while (true)
        {
            AsyncStatus status = GetStatus(async);
            switch (status)
            {
                case AsyncStatus.Completed: return 0;
                case AsyncStatus.Error: return GetErrorCode(async);
                case AsyncStatus.Canceled: return E_ABORT;
                default:
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    // ── sync pointer probes (no pointers cross an await) ────────────────────────────────────────────────────────────

    private static unsafe AsyncStatus GetStatus(nint p)
    {
        IAsyncInfo* info = QueryAsyncInfo(p);
        try
        {
            AsyncStatus status;
            int hr = info->get_Status(&status);
            return hr >= 0 ? status : AsyncStatus.Error;
        }
        finally { info->Release(); }
    }

    private static unsafe int GetErrorCode(nint p)
    {
        IAsyncInfo* info = QueryAsyncInfo(p);
        try
        {
            TerraFX.Interop.Windows.HRESULT code;
            int hr = info->get_ErrorCode(&code);
            return hr >= 0 ? (int)code : hr;
        }
        finally { info->Release(); }
    }

    private static unsafe IAsyncInfo* QueryAsyncInfo(nint p)
    {
        var insp = (IInspectable*)p;
        IAsyncInfo* info = null;
        Guid iid = __uuidof<IAsyncInfo>();
        int hr = insp->QueryInterface(&iid, (void**)&info);
        if (hr < 0 || info == null)
            throw new InvalidOperationException($"QI IAsyncInfo failed (0x{(uint)hr:X8}).");
        return info;
    }

    /// <summary><c>IAsyncOperation&lt;T&gt;.GetResults</c> — vtable slot 8 (<c>[6]put_Completed [7]get_Completed
    /// [8]GetResults</c>). TerraFX projects the generic operation shape-only, so the call is hand-issued through the
    /// vtable. The result is the <c>TResult</c> pointer (here an <c>IAdaptiveMediaSourceCreationResult*</c>).</summary>
    private static unsafe nint GetOperationResults(nint op)
    {
        var insp = (IInspectable*)op;
        void** vtbl = *(void***)insp;
        void* result = null;
        int hr = ((delegate* unmanaged<IInspectable*, void**, int>)vtbl[8])(insp, &result);
        if (hr < 0)
            throw new InvalidOperationException($"IAsyncOperation.GetResults failed (0x{(uint)hr:X8}).");
        return (nint)result;
    }

    private static unsafe int GetActionResults(nint action)
    {
        var act = (IAsyncAction*)action;
        return act->GetResults();   // S_OK on clean completion (projected by TerraFX)
    }
}
