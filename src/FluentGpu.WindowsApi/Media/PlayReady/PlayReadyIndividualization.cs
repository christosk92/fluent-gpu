using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.WindowsApi.Notifications;   // HStringHandle (shared cold-COM helper)
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.WinRT.WinRT;    // RoActivateInstance

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// Proactive, out-of-band PlayReady <b>individualization</b> (provisioning): activate a bare
/// <c>PlayReadyIndividualizationServiceRequest</c> and run its <c>BeginServiceRequest</c> with NO
/// <see cref="PlayReadyProtectionManager"/> and NO <see cref="PlayReadyMediaPlayer"/> in the path, so the machine's
/// PlayReady stack gets a device certificate BEFORE any protected topology is built.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The spike isolates the protected-playback failure to
/// <c>MediaFailed / 0xC00D715B (MF_E_TOPOLOGY_VERIFICATION_FAILED)</c> with <c>ServiceRequested</c> NEVER firing — the
/// signature of a PlayReady stack that cannot instantiate <c>PlayReadyWinRTTrustedInput</c> because the machine is not
/// individualized. If the trusted-input component can't build, topology verification fails before any service request is
/// ever raised. Individualizing proactively (the standard PlayReady bootstrap the OS normally does lazily on first
/// protected playback) is the hypothesis-test fix: provision first, then let the protected topology build against a real
/// certificate.
/// </para>
/// <para>
/// <b>How the async completes.</b> <c>IPlayReadyServiceRequest.BeginServiceRequest</c> returns a WinRT
/// <c>IAsyncAction</c>; it is awaited via <see cref="WinRtAsync.AwaitActionAsync"/> — the same status-polling adapter the
/// license/individualization callbacks use (poll <c>IAsyncInfo.Status</c>; no derived
/// <c>IAsyncActionCompletedHandler</c> CCW). For the individualization request the OS performs the HTTP round-trip to the
/// Microsoft individualization server INTERNALLY inside <c>BeginServiceRequest</c> — we neither build nor POST a challenge
/// here; we only drive the action and interpret its terminal HRESULT.
/// </para>
/// <para>
/// <b>Interface shape.</b> <c>IPlayReadyIndividualizationServiceRequest</c>
/// (<see cref="PlayReadyGuids.IID_IPlayReadyIndividualizationServiceRequest"/>) is a MARKER interface —
/// <c>: public IInspectable</c> with no own methods (verified in <c>windows.media.protection.playready.h</c> line ~4551,
/// body <c>{ };</c>). <c>BeginServiceRequest</c> (slot 11) and <c>NextServiceRequest</c> (slot 12) live on the BASE
/// <c>IPlayReadyServiceRequest</c>, so we QI to that base to invoke them — exactly as
/// <see cref="ServiceRequestedHandler"/> does. No new vtable slots or IIDs were required; all are reused from
/// <see cref="PlayReadyServiceRequestInterop"/>/<see cref="PlayReadyGuids"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.10240.0")]
public static class PlayReadyIndividualization
{
    /// <summary>Ceiling on service-request iterations (the initial request + one <c>NextServiceRequest</c> follow-up).
    /// Chasing further than this risks an unbounded provisioning loop; past the cap we report the terminal state instead
    /// of looping (per the spike directive).</summary>
    private const int MaxIterations = 2;

    /// <summary>
    /// Ensure the machine's PlayReady stack is individualized (provisioned). Activates
    /// <c>PlayReadyIndividualizationServiceRequest</c> and drives <c>BeginServiceRequest</c>, chasing one
    /// <c>NextServiceRequest</c> if the OS reports <c>MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED</c>.
    /// </summary>
    /// <returns>
    /// <c>ok</c> — <see langword="true"/> iff an action completed with a success HRESULT (already-individualized or
    /// just-individualized both surface as <c>S_OK</c>); <c>hr</c> — the terminal HRESULT observed; <c>detail</c> — a
    /// human-readable trace of what happened. DRM HRESULTs are NEVER thrown — they are returned so the caller can branch.
    /// </returns>
    public static async Task<(bool ok, int hr, string detail)> EnsureIndividualizedAsync(CancellationToken ct)
    {
        // 1. Activate the bare request (default-constructible) and QI it to the individualization marker interface to
        //    confirm we got the right shape. No ProtectionManager / MediaPlayer involved.
        nint indivReq = ActivateIndividualizationRequest(out int actHr, out string actDetail);
        if (indivReq == 0)
            return (false, actHr, actDetail);

        try
        {
            // 2. Initial BeginServiceRequest → IAsyncAction → await.
            int hr = await BeginAndAwaitAsync(indivReq, ct).ConfigureAwait(false);
            if (hr >= 0)
                return (true, hr, $"Individualization completed on the initial request (hr=0x{(uint)hr:X8}).");

            if (unchecked((uint)hr) != unchecked((uint)PlayReadyGuids.MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED))
                return (false, hr, $"Initial BeginServiceRequest failed (hr=0x{(uint)hr:X8}); not the action-required sentinel.");

            // 3. "Action required": chase exactly one NextServiceRequest (iteration 2 of MaxIterations) and issue it.
            //    We do NOT manually POST a SOAP/enabling challenge here — this helper carries no HTTP transport, and the
            //    individualization service request drives its own server round-trip inside BeginServiceRequest. If a
            //    follow-up instead demands a manual enabling challenge, that is out of this helper's scope; we report it.
            nint next = NextServiceRequest(indivReq);
            if (next == 0)
                return (false, hr, "MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED, but NextServiceRequest() returned null — cannot chase.");

            try
            {
                int hr2 = await BeginAndAwaitAsync(next, ct).ConfigureAwait(false);
                if (hr2 >= 0)
                    return (true, hr2, $"Individualization completed on the follow-up request (hr=0x{(uint)hr2:X8}).");

                bool stillActionRequired =
                    unchecked((uint)hr2) == unchecked((uint)PlayReadyGuids.MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED);
                return (false, hr2, stillActionRequired
                    ? $"Still MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED after the follow-up; capped at {MaxIterations} iterations (further chasing likely needs a manual enabling challenge POST — out of scope here)."
                    : $"Follow-up BeginServiceRequest failed (hr=0x{(uint)hr2:X8}).");
            }
            finally
            {
                ReleasePtr(next);
            }
        }
        finally
        {
            ReleasePtr(indivReq);
        }
    }

    // ── activation ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>RoActivateInstance</c> the individualization request runtime class and QI to
    /// <c>IPlayReadyIndividualizationServiceRequest</c> (a pure type-confirmation QI; the returned pointer is used only
    /// through the base <c>IPlayReadyServiceRequest</c>). Returns the AddRef'd request as an <see cref="nint"/>, or 0 with
    /// <paramref name="hr"/>/<paramref name="detail"/> populated on failure. Never throws — activation/QI HRESULTs are
    /// reported, not raised.
    /// </summary>
    private static unsafe nint ActivateIndividualizationRequest(out int hr, out string detail)
    {
        IInspectable* insp = null;
        using (var hc = new HStringHandle(PlayReadyGuids.RuntimeClass_PlayReadyIndividualizationServiceRequest))
        {
            hr = RoActivateInstance(hc.Value, &insp);
        }
        if (hr < 0 || insp == null)
        {
            detail = $"RoActivateInstance(PlayReadyIndividualizationServiceRequest) failed (hr=0x{(uint)hr:X8}).";
            if (insp != null) insp->Release();
            return 0;
        }

        // Confirm the shape via QI to the individualization marker IID (released immediately — a pure probe).
        if (!PlayReadyServiceRequestInterop.Implements(insp, PlayReadyGuids.IID_IPlayReadyIndividualizationServiceRequest))
        {
            hr = unchecked((int)0x80004002); // E_NOINTERFACE
            detail = "Activated instance did not QI to IPlayReadyIndividualizationServiceRequest (E_NOINTERFACE).";
            insp->Release();
            return 0;
        }

        detail = "Activated PlayReadyIndividualizationServiceRequest.";
        return (nint)insp;
    }

    // ── begin + await ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>QI <paramref name="reqPtr"/> to the base <c>IPlayReadyServiceRequest</c>, call
    /// <c>BeginServiceRequest</c> (slot 11), and await the returned <c>IAsyncAction</c> via
    /// <see cref="WinRtAsync.AwaitActionAsync"/>. Returns the action's completion HRESULT (never throws for a DRM async
    /// error — <see cref="WinRtAsync"/> returns the code). No raw pointer crosses the <c>await</c>; the action is held as
    /// an <see cref="nint"/> and released in <c>finally</c>.</summary>
    private static async Task<int> BeginAndAwaitAsync(nint reqPtr, CancellationToken ct)
    {
        nint action = BeginServiceRequest(reqPtr);
        try
        {
            return await WinRtAsync.AwaitActionAsync(action, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleasePtr(action);
        }
    }

    /// <summary>QI to <c>IPlayReadyServiceRequest</c> and call <c>BeginServiceRequest</c>; return the AddRef'd
    /// <c>IAsyncAction*</c> as an <see cref="nint"/>. Mirrors <see cref="ServiceRequestedHandler"/>'s identical helper.</summary>
    private static unsafe nint BeginServiceRequest(nint reqPtr)
    {
        var insp = (IInspectable*)reqPtr;
        IInspectable* baseReq =
            PlayReadyServiceRequestInterop.QueryInterface(insp, PlayReadyGuids.IID_IPlayReadyServiceRequest);
        if (baseReq == null)
            throw new InvalidOperationException("QI IPlayReadyServiceRequest failed for BeginServiceRequest.");
        try
        {
            IAsyncAction* action = null;
            WinRtInterop.ThrowIfFailed(
                PlayReadyServiceRequestInterop.BeginServiceRequest(baseReq, &action), "BeginServiceRequest");
            return (nint)action;
        }
        finally
        {
            baseReq->Release();
        }
    }

    /// <summary>QI to <c>IPlayReadyServiceRequest</c> and call <c>NextServiceRequest</c> (slot 12); return the AddRef'd
    /// follow-up request as an <see cref="nint"/>, or 0 on failure/none.</summary>
    private static unsafe nint NextServiceRequest(nint reqPtr)
    {
        var insp = (IInspectable*)reqPtr;
        IInspectable* baseReq =
            PlayReadyServiceRequestInterop.QueryInterface(insp, PlayReadyGuids.IID_IPlayReadyServiceRequest);
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

    private static unsafe void ReleasePtr(nint p)
    {
        if (p != 0) ((IInspectable*)p)->Release();
    }
}
