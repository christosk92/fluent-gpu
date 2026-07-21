using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;   // __uuidof<T>
using static TerraFX.Interop.WinRT.WinRT;        // WindowsGetStringRawBuffer, WindowsDeleteString

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The implemented <c>TypedEventHandler&lt;MediaPlayer, MediaPlayerFailedEventArgs&gt;</c> passed to
/// <c>IMediaPlayer.add_MediaFailed</c> — the diagnostic that surfaces the failure HRESULT so the harness can see
/// whether a failure is <c>0xC00D715B</c> (ITA/topology verification — the WinAppSDK regression) or something else.
/// </summary>
/// <remarks>
/// <b>RISK — derived parameterized IID (<c>// VERIFY</c>).</b> Like <c>MediaButtonHandler</c>, a WinRT generic
/// <c>TypedEventHandler&lt;T,U&gt;</c> is QI'd for the PARAMETERIZED instance IID, computed with the RFC-4122 v5 WinRT
/// algorithm over the type signature
/// <c>pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.Media.Playback.MediaPlayer;{381a83cb-6fff-499b-8d64-2885dfc1249e});rc(Windows.Media.Playback.MediaPlayerFailedEventArgs;{2744e9b9-a7e3-4f16-bac4-7914ebc08301}))</c>
/// (the two <c>rc(...)</c> IIDs are the default-interface IIDs of <c>MediaPlayer</c>/<c>MediaPlayerFailedEventArgs</c>
/// from <c>windows.media.playback.h</c>). The computed value is <see cref="ParameterizedIid"/> =
/// <c>362c45a7-3a0a-5e27-99ce-cff6d1b770e1</c>; the same algorithm was cross-checked by reproducing the well-known
/// <c>IMap&lt;String,Object&gt;</c> IID exactly, but this specific value is DERIVED, not copied from a header. Because
/// <c>MediaFailed</c> is a non-essential diagnostic (the authoritative pass signal is <c>ServiceRequested</c> firing),
/// its <c>add_MediaFailed</c> registration is best-effort: an <c>E_NOINTERFACE</c> here (⇒ suspect this IID) is logged,
/// not fatal.
/// </remarks>
internal static class MediaFailedComConstants
{
    /// <summary>Derived parameterized IID of <c>TypedEventHandler&lt;MediaPlayer, MediaPlayerFailedEventArgs&gt;</c>.
    /// RISK: computed, not copied from a header. <c>// VERIFY</c>.</summary>
    public const string ParameterizedIid = "362c45a7-3a0a-5e27-99ce-cff6d1b770e1";
}

/// <summary>The implemented <c>MediaFailed</c> handler interface (derived parameterized IID). IUnknown + <c>Invoke</c>.</summary>
[GeneratedComInterface]
[Guid(MediaFailedComConstants.ParameterizedIid)]
internal partial interface IMediaFailedHandlerNative
{
    /// <summary><paramref name="sender"/> is the <c>IMediaPlayer*</c>; <paramref name="args"/> is the
    /// <c>IMediaPlayerFailedEventArgs*</c>. Returns S_OK.</summary>
    [PreserveSig]
    unsafe int Invoke(void* sender, void* args);
}

/// <summary>Implementation of <see cref="IMediaFailedHandlerNative"/>: reads the error enum, extended HRESULT, and
/// message off the args and forwards them to the harness callback.</summary>
[GeneratedComClass]
[SupportedOSPlatform("windows10.0.10240.0")]
internal sealed unsafe partial class MediaFailedHandler : IMediaFailedHandlerNative
{
    private const int S_OK = 0;

    /// <summary>Callback: (extended HRESULT, MediaPlayerError ordinal, error message).</summary>
    private readonly Action<int, int, string> _onFailed;

    public MediaFailedHandler(Action<int, int, string> onFailed) => _onFailed = onFailed;

    /// <inheritdoc/>
    public int Invoke(void* sender, void* args)
    {
        try
        {
            if (args == null)
                return S_OK;

            var argsInsp = (IInspectable*)args;
            IMediaPlayerFailedEventArgs* pArgs = null;
            Guid iid = __uuidof<IMediaPlayerFailedEventArgs>();
            if (argsInsp->QueryInterface(&iid, (void**)&pArgs) < 0 || pArgs == null)
                return S_OK;
            try
            {
                TerraFX.Interop.Windows.HRESULT extended = default;
                pArgs->get_ExtendedErrorCode(&extended);

                MediaPlayerError error = default;
                pArgs->get_Error(&error);

                string message = string.Empty;
                HSTRING hs = default;
                if (pArgs->get_ErrorMessage(&hs) >= 0)
                {
                    message = HStringToString(hs);
                    WindowsDeleteString(hs);
                }

                try { _onFailed((int)extended, (int)error, message); }
                catch { /* harness sink must never fail the OS callback */ }
                return S_OK;
            }
            finally
            {
                pArgs->Release();
            }
        }
        catch
        {
            return S_OK;
        }
    }

    private static string HStringToString(HSTRING hs)
    {
        uint length;
        char* raw = WindowsGetStringRawBuffer(hs, &length);   // safe for a null HSTRING (returns empty)
        return raw == null || length == 0 ? string.Empty : new string(raw, 0, (int)length);
    }
}
