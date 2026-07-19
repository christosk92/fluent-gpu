using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;   // __uuidof<T>

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The four PlayReady service-request interfaces that <c>TerraFX.Interop.WinRT</c> does NOT project, hand-bound as
/// direct vtable call-OUT wrappers — the exact "no CsWinRT, no <c>ComWrappers</c> on the call-out path, direct vtable
/// calls" doctrine that <c>Media/SystemMediaControls.cs</c> proves for the projected WinRT surface, extended by hand to
/// the unprojected PlayReady surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why manual vtable structs and not <c>[GeneratedComInterface]</c>.</b> These interfaces are CONSUMED (call-OUT):
/// the OS hands us an <c>IMediaProtectionServiceRequest*</c> and we QI it to the PlayReady shape and invoke methods.
/// Consuming a raw pointer through <c>[GeneratedComInterface]</c> would force a <c>ComWrappers.GetOrCreateObjectForComInstance</c>
/// object-wrap on the call-out path — precisely what the repo's cold-COM doctrine forbids there
/// (<c>design/subsystems/com-interop.md</c>: <c>ComWrappers</c> CCWs are for the IMPLEMENT/callback side only). A manual
/// vtable wrapper matches the proven <c>SystemMediaControls</c> call-out pattern exactly and gives precise control over
/// the <c>byte[]</c>/out-parameter ABI (<c>GetMessageBody</c> returns a <c>CoTaskMemAlloc</c>'d array;
/// <c>ProcessManualEnablingResponse</c> takes a raw <c>BYTE*</c>+length). The IMPLEMENT side (the
/// <c>ServiceRequested</c>/<c>MediaFailed</c> callbacks) DOES use <c>[GeneratedComInterface]</c>/<c>[GeneratedComClass]</c>,
/// as doctrine prescribes.
/// </para>
/// <para>
/// <b>Vtable slot layout.</b> Every interface here derives from <c>IInspectable</c>, so its vtable is
/// <c>[0]QueryInterface [1]AddRef [2]Release [3]GetIids [4]GetRuntimeClassName [5]GetTrustLevel</c> followed by the
/// interface's own methods in declaration order (read verbatim from the SDK header). The slot indices below are
/// annotated against that header.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.10240.0")]
internal static unsafe class PlayReadyServiceRequestInterop
{
    // ── IPlayReadyServiceRequest (base) — windows.media.protection.playready.h ───────────────────────────────────────
    //   [6] get_Uri  [7] put_Uri  [8] get_ResponseCustomData  [9] get_ChallengeCustomData  [10] put_ChallengeCustomData
    //   [11] BeginServiceRequest(IAsyncAction**)  [12] NextServiceRequest(IPlayReadyServiceRequest**)
    //   [13] GenerateManualEnablingChallenge(IPlayReadySoapMessage**)
    //   [14] ProcessManualEnablingResponse(UINT32 len, BYTE* bytes, HRESULT* result)

    /// <summary><c>IPlayReadyServiceRequest.BeginServiceRequest</c> (slot 11) — starts the async individualization
    /// request. <paramref name="action"/> receives an AddRef'd <c>IAsyncAction*</c> the caller owns.</summary>
    public static int BeginServiceRequest(IInspectable* req, IAsyncAction** action)
        => ((delegate* unmanaged<IInspectable*, void**, int>)Vtbl(req)[11])(req, (void**)action);

    /// <summary><c>IPlayReadyServiceRequest.NextServiceRequest</c> (slot 12) — the follow-up request to chase after
    /// <c>MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED</c>. <paramref name="next"/> receives an AddRef'd
    /// <c>IPlayReadyServiceRequest*</c> (as <c>IInspectable*</c>).</summary>
    public static int NextServiceRequest(IInspectable* req, IInspectable** next)
        => ((delegate* unmanaged<IInspectable*, void**, int>)Vtbl(req)[12])(req, (void**)next);

    /// <summary><c>IPlayReadyServiceRequest.GenerateManualEnablingChallenge</c> (slot 13) — produces the SOAP license
    /// challenge. <paramref name="soap"/> receives an AddRef'd <c>IPlayReadySoapMessage*</c> (as
    /// <c>IInspectable*</c>).</summary>
    public static int GenerateManualEnablingChallenge(IInspectable* req, IInspectable** soap)
        => ((delegate* unmanaged<IInspectable*, void**, int>)Vtbl(req)[13])(req, (void**)soap);

    /// <summary><c>IPlayReadyServiceRequest.ProcessManualEnablingResponse</c> (slot 14) — feeds the license-server
    /// response bytes back to PlayReady. The outer return is the call HRESULT; <paramref name="result"/> receives the
    /// per-response processing HRESULT (S_OK = license installed).</summary>
    public static int ProcessManualEnablingResponse(IInspectable* req, byte* bytes, uint length, int* result)
        => ((delegate* unmanaged<IInspectable*, uint, byte*, int*, int>)Vtbl(req)[14])(req, length, bytes, result);

    // ── IPlayReadySoapMessage — windows.media.protection.playready.h ─────────────────────────────────────────────────
    //   [6] GetMessageBody(UINT32* len, BYTE** bytes)  [7] get_MessageHeaders(IPropertySet**)  [8] get_Uri(IUriRuntimeClass**)

    /// <summary><c>IPlayReadySoapMessage.GetMessageBody</c> (slot 6). The returned array is <c>CoTaskMemAlloc</c>'d and
    /// owned by the caller — <see cref="GetMessageBodyManaged"/> copies then frees it.</summary>
    public static int GetMessageBody(IInspectable* soap, uint* length, byte** bytes)
        => ((delegate* unmanaged<IInspectable*, uint*, byte**, int>)Vtbl(soap)[6])(soap, length, bytes);

    /// <summary>Copy the SOAP challenge body out of <paramref name="soap"/> into a managed <c>byte[]</c>, freeing the
    /// native <c>CoTaskMemAlloc</c>'d buffer. Returns the license challenge bytes to POST to the license server.</summary>
    public static byte[] GetMessageBodyManaged(IInspectable* soap)
    {
        uint len = 0;
        byte* p = null;
        int hr = GetMessageBody(soap, &len, &p);
        if (hr < 0)
            throw new InvalidOperationException($"IPlayReadySoapMessage.GetMessageBody failed (0x{(uint)hr:X8}).");
        try
        {
            if (p == null || len == 0)
                return Array.Empty<byte>();
            var managed = new byte[len];
            Marshal.Copy((nint)p, managed, 0, (int)len);
            return managed;
        }
        finally
        {
            if (p != null)
                Marshal.FreeCoTaskMem((nint)p);   // WinRT array retvals are CoTaskMemAlloc'd; the caller frees them.
        }
    }

    // ── QI helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>QI <paramref name="unknown"/> to a PlayReady interface <paramref name="iid"/>. Returns an AddRef'd
    /// <c>IInspectable*</c> on success (caller releases), or <see langword="null"/> on <c>E_NOINTERFACE</c> — the QI is
    /// how we discriminate a license request from an individualization request.</summary>
    public static IInspectable* QueryInterface(IInspectable* unknown, Guid iid)
    {
        IInspectable* result = null;
        int hr = unknown->QueryInterface(&iid, (void**)&result);
        return hr >= 0 ? result : null;
    }

    /// <summary>Whether <paramref name="unknown"/> implements the interface identified by <paramref name="iid"/>
    /// (a QI that is immediately released — a pure type probe).</summary>
    public static bool Implements(IInspectable* unknown, Guid iid)
    {
        IInspectable* probe = QueryInterface(unknown, iid);
        if (probe == null)
            return false;
        probe->Release();
        return true;
    }

    private static void** Vtbl(IInspectable* p) => *(void***)p;
}
