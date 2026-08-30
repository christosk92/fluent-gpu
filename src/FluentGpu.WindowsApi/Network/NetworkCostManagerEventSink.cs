using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;

namespace FluentGpu.WindowsApi.Network;

/// <summary>
/// The connection-point sink interface FluentGpu <i>implements</i> so the Network List Manager can push connection-cost
/// changes (<c>netlistmgr.h INetworkCostManagerEvents</c>, IID <c>DCB00009-…</c>; Windows 8+). Like
/// <see cref="INetworkListManagerEvents"/> it derives from <c>IUnknown</c> (not <c>IDispatch</c>), so it has exactly two
/// methods beyond <c>IUnknown</c>, in header order. Declared <c>[GeneratedComInterface]</c> so the COM source generator
/// emits the managed→native vtable with no reflection. TerraFX does not project it (verified against 10.0.26100.6).
/// </summary>
/// <remarks>
/// Native signatures: <c>HRESULT CostChanged(DWORD newCost, NLM_SOCKADDR* pDestAddr)</c> and
/// <c>HRESULT DataPlanStatusChanged(NLM_SOCKADDR* pDestAddr)</c>. The destination pointer is declared as an opaque
/// <see cref="nint"/> — this pillar never registers destination addresses (<c>SetDestinationAddresses</c> is not
/// called), so it is only ever tested for null ("the machine connection"). Both methods are <c>[PreserveSig]</c> so the
/// generated stubs are thin pass-throughs with no implicit allocation on the callback thread.
/// </remarks>
[GeneratedComInterface]
[Guid("DCB00009-570F-4A9B-8D69-199FDBA5723B")]
internal partial interface INetworkCostManagerEvents
{
    /// <summary>Fired when the connection cost changes. <paramref name="newCost"/> is the new <c>NLM_CONNECTION_COST</c>
    /// flags word — the same value <c>INetworkCostManager::GetCost</c> would return. Return S_OK.</summary>
    [PreserveSig]
    int CostChanged(uint newCost, nint pDestAddr);

    /// <summary>Fired when the data-plan status (usage, limits) changes; carries no cost payload. Return S_OK.</summary>
    [PreserveSig]
    int DataPlanStatusChanged(nint pDestAddr);
}

/// <summary>
/// The managed implementation of <see cref="INetworkCostManagerEvents"/> behind <see cref="NetworkStatus.SubscribeCost"/>.
/// Same source-generated COM shape as <see cref="NetworkListManagerEventSink"/> (<c>[GeneratedComClass]</c> +
/// <see cref="StrategyBasedComWrappers"/>) — NO <c>[ComImport]</c>, NO <c>ComWrappers</c> subclassing, NO reflection.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> <c>CostChanged</c> maps its payload through the SAME <see cref="NetworkStatus.MapCost"/> the
/// polled <see cref="NetworkStatus.ReadCostAsync"/> uses, so pushed and polled snapshots never disagree, and forwards it
/// to the subscriber. <c>DataPlanStatusChanged</c> carries no cost, so it re-reads <c>GetCost</c> on the callback thread
/// (<see cref="NetworkStatus.ReadCost"/>; the limit bits live in the cost word) and forwards that. Events for a
/// registered destination (non-null <c>pDestAddr</c>) are ignored — this pillar registers none, and only the machine
/// connection's cost is the subscriber's question.
/// </para>
/// <para>
/// <b>Threading.</b> Identical to <see cref="NetworkListManagerEventSink"/>: NLM raises this on a COM-supplied thread
/// (an RPC worker for an MTA subscriber), NOT the subscribing thread. The sink does no marshalling; the subscriber hops
/// to its own UI thread. A managed exception never crosses the COM boundary — the delegate runs inside a try/catch and
/// both methods always return S_OK.
/// </para>
/// </remarks>
[GeneratedComClass]
[SupportedOSPlatform("windows6.2")] // INetworkCostManagerEvents shipped with INetworkCostManager in Windows 8.
internal sealed partial class NetworkCostManagerEventSink : INetworkCostManagerEvents
{
    /// <summary>IID of <c>INetworkCostManagerEvents</c> (netlistmgr.h: <c>DCB00009-570F-4A9B-8D69-199FDBA5723B</c>) — the
    /// outgoing interface the cost connection point is looked up by. Kept beside the interface it identifies; the other
    /// netlistmgr IIDs live in <see cref="NetworkListManagerComConstants"/>.</summary>
    internal static readonly Guid IID_INetworkCostManagerEvents = new("DCB00009-570F-4A9B-8D69-199FDBA5723B");

    private readonly Action<NetworkCost> _costChanged;

    /// <summary>Construct with the subscriber callback (invoked inline on the NLM callback thread — see type remarks).</summary>
    internal NetworkCostManagerEventSink(Action<NetworkCost> costChanged) => _costChanged = costChanged;

    /// <inheritdoc/>
    public int CostChanged(uint newCost, nint pDestAddr)
    {
        try
        {
            if (pDestAddr == 0)
                _costChanged(NetworkStatus.MapCost((NLM_CONNECTION_COST)newCost));
        }
        catch
        {
            // Never let a handler exception propagate across the COM boundary.
        }
        return S.S_OK;
    }

    /// <inheritdoc/>
    public int DataPlanStatusChanged(nint pDestAddr)
    {
        try
        {
            if (pDestAddr == 0)
                _costChanged(NetworkStatus.ReadCost());
        }
        catch
        {
            // Never let a handler exception propagate across the COM boundary.
        }
        return S.S_OK;
    }
}
