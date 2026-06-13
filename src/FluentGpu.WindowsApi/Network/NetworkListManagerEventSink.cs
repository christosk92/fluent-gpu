using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;

namespace FluentGpu.WindowsApi.Network;

/// <summary>
/// The managed implementation of <see cref="INetworkListManagerEvents"/> that the Network List Manager calls when overall
/// connectivity changes. Wired through the source-generated COM path (<c>[GeneratedComClass]</c> +
/// <see cref="StrategyBasedComWrappers"/>), exactly like the toast pillar's <c>ToastActivatorCallback</c> — NO
/// <c>[ComImport]</c>, NO <c>ComWrappers</c> subclassing, NO reflection.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> On <c>ConnectivityChanged</c> it derives an online/offline boolean from the new
/// <c>NLM_CONNECTIVITY</c> flags — using the SAME "an internet bit is set" rule as
/// <see cref="NetworkStatus.IsOnline"/>'s <c>get_IsConnectedToInternet</c>, so the event boolean and the polled property
/// never disagree — and forwards it to the subscriber's <see cref="Action{Boolean}"/>. It marshals nothing else and does
/// no dispatch of its own.
/// </para>
/// <para>
/// <b>Threading (read this before wiring a handler).</b> NLM raises this on a COM-supplied thread, NOT necessarily the
/// thread that subscribed. The <c>INetworkListManager</c> coclass is apartment-agile (<c>ThreadingModel=Both</c>,
/// free-threaded-marshaled), so where the callback lands depends on the subscribing apartment: from an MTA subscriber
/// (the gallery UI thread is MTA — its <c>Main</c> carries no <c>[STAThread]</c>) it arrives on an RPC worker thread; from
/// an STA subscriber it would be delivered via that apartment's message pump. Either way it is NOT guaranteed to be the
/// subscriber's UI thread. This sink therefore does NO thread marshalling: it invokes the subscriber delegate inline on
/// the callback thread, and the subscriber is responsible for hopping to its own UI thread if it needs to (the toast
/// pillar's <c>ActivationDispatcher</c> pattern is the model — kept out of this minimal API by design).
/// </para>
/// <para>
/// <b>Exception safety.</b> A managed exception must never cross the COM boundary; the subscriber delegate is invoked
/// inside a try/catch and any throw is swallowed (the platform only wants an HRESULT, and there is no caller to surface a
/// network-event handler fault to). <c>ConnectivityChanged</c> always returns S_OK.
/// </para>
/// </remarks>
[GeneratedComClass]
[SupportedOSPlatform("windows6.0.6000")]
internal sealed partial class NetworkListManagerEventSink : INetworkListManagerEvents
{
    // The internet-reachable mask: either stack's internet bit means "online" (matches get_IsConnectedToInternet).
    private const NLM_CONNECTIVITY InternetMask = NLM_CONNECTIVITY.Ipv4Internet | NLM_CONNECTIVITY.Ipv6Internet;

    private readonly Action<bool> _onlineChanged;

    /// <summary>Construct with the subscriber callback (invoked inline on the NLM callback thread — see type remarks).</summary>
    internal NetworkListManagerEventSink(Action<bool> onlineChanged) => _onlineChanged = onlineChanged;

    /// <inheritdoc/>
    public int ConnectivityChanged(NLM_CONNECTIVITY newConnectivity)
    {
        try
        {
            bool online = (newConnectivity & InternetMask) != 0;
            _onlineChanged(online);
        }
        catch
        {
            // Never let a handler exception propagate across the COM boundary.
        }
        return S.S_OK;
    }
}
