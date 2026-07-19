using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;   // __uuidof<T>

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The kind of PlayReady service request the OS raised on <c>MediaProtectionManager.ServiceRequested</c> — the value the
/// harness observes to prove the protected topology built and ITA verification passed (rather than an immediate
/// <c>MediaFailed / 0xC00D715B</c>).
/// </summary>
public enum PlayReadyServiceRequestKind
{
    /// <summary>The request QI'd to neither PlayReady service-request shape (unexpected).</summary>
    Unknown = 0,
    /// <summary>A <c>PlayReadyIndividualizationServiceRequest</c> — PlayReady provisioning (calls the MS server itself).</summary>
    Individualization = 1,
    /// <summary>A <c>PlayReadyLicenseAcquisitionServiceRequest</c> — the SOAP license challenge/response flow.</summary>
    LicenseAcquisition = 2,
}

/// <summary>
/// The WinRT delegate FluentGpu <i>implements</i> so the OS can call back when the protected pipeline needs a service
/// request handled — the <c>IServiceRequestedEventHandler</c> passed to <c>IMediaProtectionManager.add_ServiceRequested</c>.
/// Unlike a generic <c>TypedEventHandler&lt;T,U&gt;</c>, this is a NAMED, non-generic WinRT delegate with a FIXED metadata
/// IID (<c>d2d690ba-cac9-48e1-95c0-d38495a84055</c>, copied from <c>windows.media.protection.h</c>), so — unlike
/// <c>MediaButtonHandler</c> — its CCW needs NO derived parameterized IID. It is wired through the source-generated COM
/// path exactly as <c>MediaButtonHandler</c> is (<c>[GeneratedComInterface]</c>/<c>[GeneratedComClass]</c> +
/// <c>StrategyBasedComWrappers</c>).
/// </summary>
internal static class ServiceRequestedComConstants
{
    /// <summary>IID of <c>IServiceRequestedEventHandler</c> (<c>windows.media.protection.h</c>, <c>: public IUnknown</c>
    /// — an IUnknown-based delegate, so <c>Invoke</c> is vtable slot 3). Copied from the SDK header, not derived.</summary>
    public const string IServiceRequestedEventHandlerIid = "d2d690ba-cac9-48e1-95c0-d38495a84055";
}

/// <summary>The implemented <c>IServiceRequestedEventHandler</c> COM interface. Declared <c>[GeneratedComInterface]</c>
/// so the COM source generator emits the managed→native vtable (IUnknown + <c>Invoke</c>) with no reflection.</summary>
[GeneratedComInterface]
[Guid(ServiceRequestedComConstants.IServiceRequestedEventHandlerIid)]
internal partial interface IServiceRequestedEventHandlerNative
{
    /// <summary>The OS callback. <paramref name="sender"/> is the <c>IMediaProtectionManager*</c> and
    /// <paramref name="args"/> is the <c>IServiceRequestedEventArgs*</c>. Returns S_OK. Both pointers are owned by the
    /// caller for the duration of the call.</summary>
    [PreserveSig]
    unsafe int Invoke(void* sender, void* args);
}

/// <summary>
/// The implementation of <see cref="IServiceRequestedEventHandlerNative"/>. On <c>Invoke</c> it reads the request +
/// completion off the args, classifies the request (license vs. individualization) by QI, <b>signals the harness
/// synchronously</b> (the pass evidence), then completes the request asynchronously off the MF worker thread:
/// individualization via <c>BeginServiceRequest</c> (chasing <c>NextServiceRequest</c> on the
/// <c>MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED</c> sentinel); license via <c>GenerateManualEnablingChallenge</c> →
/// host license POST → <c>ProcessManualEnablingResponse</c>. Ordering, ownership, and the "never raise on the OS
/// thread" discipline mirror <c>MediaButtonHandler</c>.
/// </summary>
[GeneratedComClass]
[SupportedOSPlatform("windows10.0.10240.0")]
internal sealed partial class ServiceRequestedHandler : IServiceRequestedEventHandlerNative
{
    private const int S_OK = 0;

    private readonly Action<PlayReadyServiceRequestKind> _onFired;
    private readonly Func<byte[], IReadOnlyDictionary<string, string>, CancellationToken, Task<byte[]>>? _acquireLicense;
    private readonly CancellationToken _ct;

    /// <summary>Construct with the harness signal callback, an optional license-transport delegate (the ONLY
    /// host/Spotify/Axinom-aware line — the engine never knows the endpoint), and a cancellation token.</summary>
    public ServiceRequestedHandler(
        Action<PlayReadyServiceRequestKind> onFired,
        Func<byte[], IReadOnlyDictionary<string, string>, CancellationToken, Task<byte[]>>? acquireLicense,
        CancellationToken ct)
    {
        _onFired = onFired;
        _acquireLicense = acquireLicense;
        _ct = ct;
    }

    /// <inheritdoc/>
    public unsafe int Invoke(void* sender, void* args)
    {
        try
        {
            if (args == null)
                return S_OK;

            var argsInsp = (IInspectable*)args;
            IServiceRequestedEventArgs* pArgs = null;
            Guid iidArgs = __uuidof<IServiceRequestedEventArgs>();
            if (argsInsp->QueryInterface(&iidArgs, (void**)&pArgs) < 0 || pArgs == null)
                return S_OK;

            IMediaProtectionServiceRequest* request = null;
            IMediaProtectionServiceCompletion* completion = null;
            try
            {
                if (pArgs->get_Request(&request) < 0 || request == null)
                    return S_OK;
                if (pArgs->get_Completion(&completion) < 0 || completion == null)
                    return S_OK;

                var reqInsp = (IInspectable*)request;
                PlayReadyServiceRequestKind kind =
                    PlayReadyServiceRequestInterop.Implements(reqInsp, PlayReadyGuids.IID_IPlayReadyLicenseAcquisitionServiceRequest)
                        ? PlayReadyServiceRequestKind.LicenseAcquisition
                        : PlayReadyServiceRequestInterop.Implements(reqInsp, PlayReadyGuids.IID_IPlayReadyIndividualizationServiceRequest)
                            ? PlayReadyServiceRequestKind.Individualization
                            : PlayReadyServiceRequestKind.Unknown;

                // The whole point: signal the harness the instant ServiceRequested fires — synchronously, before any
                // async work. A managed exception in the harness sink must not cross the COM boundary.
                try { _onFired(kind); }
                catch { /* harness sink must never fail the OS callback */ }

                // Transfer ownership of the AddRef'd request + completion to the background task and complete off the
                // MF worker thread (the WinRT pattern: return S_OK now; the pipeline waits on Completion.Complete).
                nint reqPtr = (nint)request;
                nint completionPtr = (nint)completion;
                request = null;       // ownership transferred — do NOT release in finally
                completion = null;
                _ = Task.Run(() => ProcessAsync(reqPtr, completionPtr, kind));
                return S_OK;
            }
            finally
            {
                if (request != null) request->Release();
                if (completion != null) completion->Release();
                pArgs->Release();
            }
        }
        catch
        {
            return S_OK;   // never propagate a managed exception across the COM boundary
        }
    }

    private async Task ProcessAsync(nint reqPtr, nint completionPtr, PlayReadyServiceRequestKind kind)
    {
        bool ok = false;
        try
        {
            ok = kind switch
            {
                PlayReadyServiceRequestKind.LicenseAcquisition => await DoLicenseAsync(reqPtr).ConfigureAwait(false),
                PlayReadyServiceRequestKind.Individualization => await DoIndividualizationAsync(reqPtr).ConfigureAwait(false),
                _ => false,
            };
        }
        catch
        {
            ok = false;
        }
        finally
        {
            Complete(completionPtr, ok);
            ReleasePtr(reqPtr);
            ReleasePtr(completionPtr);
        }
    }

    private static unsafe void ReleasePtr(nint p)
    {
        if (p != 0) ((IInspectable*)p)->Release();
    }

    // ── license flow ────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<bool> DoLicenseAsync(nint reqPtr)
    {
        if (_acquireLicense is null)
            return false;   // no license transport wired — signal fired, but we cannot complete the acquisition

        byte[] challenge = GenerateChallenge(reqPtr);
        // NOTE: the SOAP MessageHeaders IPropertySet is intentionally NOT enumerated here (kept off the cold-COM
        // surface); the host license-POST delegate sets the required SOAP Content-Type/SOAPAction itself.
        byte[] response = await _acquireLicense(challenge, EmptyHeaders, _ct).ConfigureAwait(false);
        int hr = ProcessResponse(reqPtr, response);
        return hr >= 0;
    }

    private static unsafe byte[] GenerateChallenge(nint reqPtr)
    {
        var reqInsp = (IInspectable*)reqPtr;
        IInspectable* baseReq = PlayReadyServiceRequestInterop.QueryInterface(reqInsp, PlayReadyGuids.IID_IPlayReadyServiceRequest);
        if (baseReq == null)
            throw new InvalidOperationException("QI IPlayReadyServiceRequest failed for the license request.");
        try
        {
            IInspectable* soap = null;
            WinRtInterop.ThrowIfFailed(
                PlayReadyServiceRequestInterop.GenerateManualEnablingChallenge(baseReq, &soap),
                "GenerateManualEnablingChallenge");
            try
            {
                return PlayReadyServiceRequestInterop.GetMessageBodyManaged(soap);
            }
            finally
            {
                if (soap != null) soap->Release();
            }
        }
        finally
        {
            baseReq->Release();
        }
    }

    private static unsafe int ProcessResponse(nint reqPtr, byte[] response)
    {
        var reqInsp = (IInspectable*)reqPtr;
        IInspectable* baseReq = PlayReadyServiceRequestInterop.QueryInterface(reqInsp, PlayReadyGuids.IID_IPlayReadyServiceRequest);
        if (baseReq == null)
            return unchecked((int)0x80004002); // E_NOINTERFACE
        try
        {
            fixed (byte* p = response)
            {
                int result;
                int hr = PlayReadyServiceRequestInterop.ProcessManualEnablingResponse(baseReq, p, (uint)response.Length, &result);
                return hr < 0 ? hr : result;
            }
        }
        finally
        {
            baseReq->Release();
        }
    }

    // ── individualization flow ──────────────────────────────────────────────────────────────────────────────────────

    private async Task<bool> DoIndividualizationAsync(nint reqPtr)
    {
        nint action = BeginServiceRequest(reqPtr);
        int hr;
        try { hr = await WinRtAsync.AwaitActionAsync(action, _ct).ConfigureAwait(false); }
        finally { ReleasePtr(action); }

        if (hr >= 0)
            return true;
        if (unchecked((uint)hr) != unchecked((uint)PlayReadyGuids.MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED))
            return false;

        // "Action required" — chase the follow-up request once (WaveeMusic recipe).
        nint next = NextServiceRequest(reqPtr);
        if (next == 0)
            return false;
        try
        {
            nint action2 = BeginServiceRequest(next);
            try { hr = await WinRtAsync.AwaitActionAsync(action2, _ct).ConfigureAwait(false); }
            finally { ReleasePtr(action2); }
            return hr >= 0;
        }
        finally
        {
            ReleasePtr(next);
        }
    }

    /// <summary>QI the request to <c>IPlayReadyServiceRequest</c> and call <c>BeginServiceRequest</c>; return the
    /// AddRef'd <c>IAsyncAction*</c> as an <see cref="nint"/>.</summary>
    private static unsafe nint BeginServiceRequest(nint reqOrBasePtr)
    {
        var insp = (IInspectable*)reqOrBasePtr;
        IInspectable* baseReq = PlayReadyServiceRequestInterop.QueryInterface(insp, PlayReadyGuids.IID_IPlayReadyServiceRequest);
        if (baseReq == null)
            throw new InvalidOperationException("QI IPlayReadyServiceRequest failed for BeginServiceRequest.");
        try
        {
            IAsyncAction* action = null;
            WinRtInterop.ThrowIfFailed(PlayReadyServiceRequestInterop.BeginServiceRequest(baseReq, &action), "BeginServiceRequest");
            return (nint)action;
        }
        finally
        {
            baseReq->Release();
        }
    }

    private static unsafe nint NextServiceRequest(nint reqPtr)
    {
        var insp = (IInspectable*)reqPtr;
        IInspectable* baseReq = PlayReadyServiceRequestInterop.QueryInterface(insp, PlayReadyGuids.IID_IPlayReadyServiceRequest);
        if (baseReq == null)
            return 0;
        try
        {
            IInspectable* next = null;
            int hr = PlayReadyServiceRequestInterop.NextServiceRequest(baseReq, &next);
            return hr >= 0 ? (nint)next : 0;
        }
        finally
        {
            baseReq->Release();
        }
    }

    private static unsafe void Complete(nint completionPtr, bool success)
    {
        var completion = (IMediaProtectionServiceCompletion*)completionPtr;
        completion->Complete((byte)(success ? 1 : 0));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
}
