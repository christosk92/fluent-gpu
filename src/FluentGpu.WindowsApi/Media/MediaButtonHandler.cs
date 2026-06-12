using System;
using System.Runtime.InteropServices;             // [Guid], [PreserveSig]
using System.Runtime.InteropServices.Marshalling; // [GeneratedComInterface], [GeneratedComClass]
using System.Runtime.Versioning;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;     // __uuidof<T>

namespace FluentGpu.WindowsApi.Media;

/// <summary>
/// The WinRT delegate FluentGpu <i>implements</i> so the OS can call back when the user presses a media-transport
/// button — the one Media-pillar interface that is implemented rather than consumed. A WinRT delegate is, at the ABI,
/// a COM object with <c>IUnknown</c> + a single <c>Invoke</c> slot; this is
/// <c>Windows.Foundation.TypedEventHandler&lt;SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs&gt;</c>.
/// It is wired through the source-generated COM path (<c>[GeneratedComInterface]</c>/<c>[GeneratedComClass]</c> +
/// <c>StrategyBasedComWrappers</c>), exactly as the repo's cold-COM doctrine prescribes — NO <c>[ComImport]</c>, NO
/// <c>ComWrappers</c> subclassing, NO reflection (the same shape proven by
/// <c>Notifications/ToastActivator.cs</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The parameterized IID (the load-bearing constant).</b> A WinRT <c>TypedEventHandler&lt;T,U&gt;</c> does not use
/// the open generic's GUID (<c>9DE1C534-6AE1-11E0-84E1-18A905BCC53F</c>) on the wire — the OS QIs the handler for the
/// <i>parameterized instance</i> IID, computed with the documented WinRT generic-instance GUID algorithm: RFC 4122 v5
/// (SHA-1) over the WinRT pinterface namespace GUID <c>11F47AD5-7B73-42C0-ABAE-878B1E16ADEE</c> followed by the
/// UTF-8 type signature
/// <code>
/// pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.Media.SystemMediaTransportControls;{99fa3ff4-1742-42a6-902e-087d41f965ec});rc(Windows.Media.SystemMediaTransportControlsButtonPressedEventArgs;{b7f47116-a56f-4dc8-9e11-92031f4a87c2}))
/// </code>
/// where the two <c>rc(Name;{iid})</c> arguments are the sender/args runtime classes named by their <i>default
/// interface</i> IID (the <c>[Guid]</c> on <c>TerraFX.Interop.WinRT.ISystemMediaTransportControls</c> and
/// <c>ISystemMediaTransportControlsButtonPressedEventArgs</c> respectively), and the leading <c>{...}</c> is the open
/// <c>ITypedEventHandler`2</c> <c>[Guid]</c>. The resulting parameterized IID is
/// <see cref="ParameterizedIid"/> = <c>0557E996-7B23-5BAE-AA81-EA0D671143A4</c>. The algorithm was validated against
/// three published <c>IReference&lt;T&gt;</c> IIDs (<c>bool/int/Guid</c>) before this value was committed
/// (<see href="https://learn.microsoft.com/en-us/uwp/winrt-cref/winrt-type-system#parameterized-types">WinRT type
/// system — parameterized types</see>). RISK: this constant is derived, not copied from an SDK header — it is the
/// single most failure-prone value in the Media pillar; if <c>add_ButtonPressed</c> ever returns
/// <c>E_NOINTERFACE</c> (0x80004002) live, this IID is the first suspect.
/// </para>
/// <para>
/// <b>The <c>Invoke</c> ABI.</b> The native slot is
/// <c>HRESULT Invoke(TypedEventHandler* this, ISystemMediaTransportControls* sender,
/// ISystemMediaTransportControlsButtonPressedEventArgs* args)</c> (slot 3, after the three <c>IUnknown</c> slots —
/// confirmed against the <c>ITypedEventHandler&lt;,&gt;.Invoke</c> vtable in <c>TerraFX.Interop.Windows.dll</c>). The
/// generated stub passes the two interface pointers as raw <c>void*</c> (the COM source generator does not marshal
/// them), and the method is <c>[PreserveSig]</c> returning the HRESULT so the stub is a thin pass-through with no
/// implicit allocation on the callback thread.
/// </para>
/// <para>
/// <b>Threading.</b> <c>Invoke</c> fires on an arbitrary OS thread (a media-key / SMTC worker), so
/// <see cref="MediaButtonHandler"/> never raises the public event synchronously on that thread — it reads the pressed
/// button off the args and forwards it to the dispatch delegate supplied by <see cref="SystemMediaControls"/>, which
/// hops to the UI thread.
/// </para>
/// </remarks>
internal static class MediaButtonHandlerConstants
{
    /// <summary>
    /// The parameterized IID of
    /// <c>TypedEventHandler&lt;SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs&gt;</c>,
    /// derived via the WinRT generic-instance GUID algorithm (see <see cref="MediaButtonHandler"/> remarks for the
    /// signature string and validation). RISK: derived, not from a header.
    /// </summary>
    public const string ParameterizedIid = "0557E996-7B23-5BAE-AA81-EA0D671143A4";
}

/// <summary>
/// The implemented <c>TypedEventHandler</c> COM interface (parameterized IID
/// <c>0557E996-7B23-5BAE-AA81-EA0D671143A4</c>). Declared <c>[GeneratedComInterface]</c> so the COM source generator
/// emits the managed→native vtable (IUnknown + <c>Invoke</c>) with no reflection.
/// </summary>
[GeneratedComInterface]
[Guid(MediaButtonHandlerConstants.ParameterizedIid)]
internal partial interface IMediaTransportButtonPressedHandler
{
    /// <summary>The OS callback. <paramref name="sender"/> is the <c>ISystemMediaTransportControls*</c> that raised the
    /// event and <paramref name="args"/> is the <c>ISystemMediaTransportControlsButtonPressedEventArgs*</c> carrying the
    /// pressed button. Returns S_OK. Both pointers are owned by the caller for the duration of the call only.</summary>
    [PreserveSig]
    unsafe int Invoke(void* sender, void* args);
}

/// <summary>
/// The singleton implementation of <see cref="IMediaTransportButtonPressedHandler"/>. On <c>Invoke</c> it QIs the args
/// pointer to <c>ISystemMediaTransportControlsButtonPressedEventArgs</c>, reads <c>get_Button</c>, translates it to the
/// FluentGpu-local <see cref="MediaButton"/>, and forwards it to the dispatch delegate supplied by
/// <see cref="SystemMediaControls"/> (never raising the public event on this arbitrary OS thread).
/// </summary>
[GeneratedComClass]
[SupportedOSPlatform("windows8.0")]
internal sealed partial class MediaButtonHandler : IMediaTransportButtonPressedHandler
{
    private readonly Action<MediaButton> _dispatch;

    /// <summary>Construct with the dispatch sink (<see cref="SystemMediaControls"/> passes a delegate that raises its
    /// <c>ButtonPressed</c> event, optionally hopping to the UI thread first).</summary>
    public MediaButtonHandler(Action<MediaButton> dispatch) => _dispatch = dispatch;

    /// <inheritdoc/>
    public unsafe int Invoke(void* sender, void* args)
    {
        try
        {
            if (args == null)
                return S_OK;

            // The args pointer arrives as the handler's TArgs (a Pointer<ISystemMediaTransportControlsButtonPressedEventArgs>),
            // i.e. already an ISystemMediaTransportControlsButtonPressedEventArgs*. QI defensively to the exact IID
            // rather than assuming the static type, then read get_Button. (RISK: if the OS ever hands the IInspectable
            // base instead, the QI is what makes this robust — verified live in the showcase.)
            var inspectable = (IInspectable*)args;
            Guid iid = __uuidof<ISystemMediaTransportControlsButtonPressedEventArgs>();
            ISystemMediaTransportControlsButtonPressedEventArgs* pArgs = null;
            int hr = inspectable->QueryInterface(&iid, (void**)&pArgs);
            if (hr < 0 || pArgs == null)
                return S_OK; // can't read the button — acknowledge the callback rather than failing the OS.

            try
            {
                SystemMediaTransportControlsButton native;
                if (pArgs->get_Button(&native) < 0)
                    return S_OK;
                _dispatch(Translate(native));
            }
            finally
            {
                pArgs->Release();
            }

            return S_OK;
        }
        catch
        {
            // A managed exception must not propagate across the COM boundary; report success so the OS does not retry
            // (a thrown HRESULT here gains nothing — the button press is already consumed).
            return S_OK;
        }
    }

    private const int S_OK = 0;

    /// <summary>Translate the native WinRT button ordinal to the FluentGpu-local <see cref="MediaButton"/>. The native
    /// order is Play=0,Pause=1,Stop=2,Record=3,FastForward=4,Rewind=5,Next=6,Previous=7,ChannelUp=8,ChannelDown=9;
    /// anything the app does not model maps to <see cref="MediaButton.Unknown"/>.</summary>
    private static MediaButton Translate(SystemMediaTransportControlsButton b) => b switch
    {
        SystemMediaTransportControlsButton.SystemMediaTransportControlsButton_Play => MediaButton.Play,
        SystemMediaTransportControlsButton.SystemMediaTransportControlsButton_Pause => MediaButton.Pause,
        SystemMediaTransportControlsButton.SystemMediaTransportControlsButton_Stop => MediaButton.Stop,
        SystemMediaTransportControlsButton.SystemMediaTransportControlsButton_Previous => MediaButton.Previous,
        SystemMediaTransportControlsButton.SystemMediaTransportControlsButton_Next => MediaButton.Next,
        _ => MediaButton.Unknown,
    };
}
