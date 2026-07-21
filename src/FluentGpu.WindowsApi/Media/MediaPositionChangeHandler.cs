using System;
using System.Runtime.InteropServices;             // [Guid], [PreserveSig]
using System.Runtime.InteropServices.Marshalling; // [GeneratedComInterface], [GeneratedComClass]
using System.Runtime.Versioning;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;     // __uuidof<T>

namespace FluentGpu.WindowsApi.Media;

/// <summary>
/// The WinRT delegate FluentGpu <i>implements</i> so the OS can call back when the user drags the lock-screen /
/// SMTC scrub bar to request a seek — the position sibling of <see cref="MediaButtonHandler"/>. At the ABI this is
/// <c>Windows.Foundation.TypedEventHandler&lt;SystemMediaTransportControls, PlaybackPositionChangeRequestedEventArgs&gt;</c>,
/// wired through the source-generated COM path (<c>[GeneratedComInterface]</c>/<c>[GeneratedComClass]</c> +
/// <c>StrategyBasedComWrappers</c>), exactly like the button handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>The parameterized IID (the load-bearing constant).</b> As with <see cref="MediaButtonHandler"/>, the OS QIs the
/// handler for the <i>parameterized instance</i> IID of the closed <c>TypedEventHandler&lt;T,U&gt;</c>, computed with
/// the WinRT generic-instance GUID algorithm (RFC 4122 v5 / SHA-1 over the pinterface namespace GUID
/// <c>11F47AD5-7B73-42C0-ABAE-878B1E16ADEE</c> + the UTF-8 type signature)
/// <code>
/// pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.Media.SystemMediaTransportControls;{99fa3ff4-1742-42a6-902e-087d41f965ec});rc(Windows.Media.PlaybackPositionChangeRequestedEventArgs;{b4493f88-eb28-4961-9c14-335e44f3e125}))
/// </code>
/// where the args runtime class is named by its default-interface IID (<c>IPlaybackPositionChangeRequestedEventArgs</c>,
/// <c>b4493f88-…</c>, read from <c>TerraFX.Interop.Windows</c>). The derived value is <see cref="ParameterizedIid"/> =
/// <c>44E34F15-BDC0-50A7-ACE4-39E91FB753F1</c>. The derivation was cross-validated: the SAME algorithm reproduces the
/// button handler's published parameterized IID (<c>0557E996-7B23-5BAE-AA81-EA0D671143A4</c>) exactly (a regression
/// gate in <c>FluentGpu.Windows.Tests</c> locks this in). RISK: the registration in
/// <see cref="SystemMediaControls.PositionChangeRequested"/> is DELIBERATELY non-fatal — if the platform ever refuses
/// this IID (<c>E_NOINTERFACE</c>) the scrub bar simply stays display-only rather than crashing the app.
/// </para>
/// <para>
/// <b>The <c>Invoke</c> ABI.</b> Slot 3 (after the three <c>IUnknown</c> slots):
/// <c>HRESULT Invoke(TypedEventHandler* this, ISystemMediaTransportControls* sender,
/// IPlaybackPositionChangeRequestedEventArgs* args)</c>. Both pointers pass as raw <c>void*</c>; the method is
/// <c>[PreserveSig]</c> so the stub is a thin pass-through with no callback-thread allocation.
/// </para>
/// <para>
/// <b>Threading.</b> <c>Invoke</c> fires on an arbitrary OS thread; the handler reads the requested position off the
/// args and forwards the seconds value to the dispatch delegate supplied by <see cref="SystemMediaControls"/>, which
/// hops to the UI thread — it never raises the public event synchronously on the callback thread.
/// </para>
/// </remarks>
internal static class MediaPositionChangeHandlerConstants
{
    /// <summary>
    /// The parameterized IID of
    /// <c>TypedEventHandler&lt;SystemMediaTransportControls, PlaybackPositionChangeRequestedEventArgs&gt;</c>, derived
    /// via the WinRT generic-instance GUID algorithm (see <see cref="MediaPositionChangeHandler"/> remarks). RISK:
    /// derived, not from a header; the algorithm is regression-gated against the button handler's published IID.
    /// </summary>
    public const string ParameterizedIid = "44E34F15-BDC0-50A7-ACE4-39E91FB753F1";
}

/// <summary>
/// The implemented <c>TypedEventHandler</c> COM interface (parameterized IID
/// <c>44E34F15-BDC0-50A7-ACE4-39E91FB753F1</c>). <c>[GeneratedComInterface]</c> so the COM source generator emits the
/// managed→native vtable (IUnknown + <c>Invoke</c>) with no reflection.
/// </summary>
[GeneratedComInterface]
[Guid(MediaPositionChangeHandlerConstants.ParameterizedIid)]
internal partial interface IMediaPositionChangeRequestedHandler
{
    /// <summary>The OS callback. <paramref name="sender"/> is the <c>ISystemMediaTransportControls*</c>;
    /// <paramref name="args"/> is the <c>IPlaybackPositionChangeRequestedEventArgs*</c> carrying the requested
    /// position. Returns S_OK. Both pointers are owned by the caller for the duration of the call only.</summary>
    [PreserveSig]
    unsafe int Invoke(void* sender, void* args);
}

/// <summary>
/// The implementation of <see cref="IMediaPositionChangeRequestedHandler"/>. On <c>Invoke</c> it QIs the args pointer
/// to <c>IPlaybackPositionChangeRequestedEventArgs</c>, reads <c>get_RequestedPlaybackPosition</c> (a WinRT
/// <see cref="TimeSpan"/>), and forwards the requested position in SECONDS to the dispatch delegate supplied by
/// <see cref="SystemMediaControls"/> (never raising the public event on this arbitrary OS thread).
/// </summary>
[GeneratedComClass]
[SupportedOSPlatform("windows8.0")]
internal sealed partial class MediaPositionChangeHandler : IMediaPositionChangeRequestedHandler
{
    private readonly Action<double> _dispatch;

    /// <summary>Construct with the dispatch sink (<see cref="SystemMediaControls"/> passes a delegate that raises its
    /// <c>PositionChangeRequested</c> event, hopping to the UI thread first).</summary>
    public MediaPositionChangeHandler(Action<double> dispatch) => _dispatch = dispatch;

    /// <inheritdoc/>
    public unsafe int Invoke(void* sender, void* args)
    {
        try
        {
            if (args == null)
                return S_OK;

            var inspectable = (IInspectable*)args;
            Guid iid = __uuidof<IPlaybackPositionChangeRequestedEventArgs>();
            IPlaybackPositionChangeRequestedEventArgs* pArgs = null;
            int hr = inspectable->QueryInterface(&iid, (void**)&pArgs);
            if (hr < 0 || pArgs == null)
                return S_OK; // can't read the position — acknowledge the callback rather than failing the OS.

            try
            {
                TimeSpan requested;
                if (pArgs->get_RequestedPlaybackPosition(&requested) < 0)
                    return S_OK;
                _dispatch(requested.TotalSeconds);
            }
            finally
            {
                pArgs->Release();
            }

            return S_OK;
        }
        catch
        {
            // A managed exception must not propagate across the COM boundary; report success so the OS does not retry.
            return S_OK;
        }
    }

    private const int S_OK = 0;
}
