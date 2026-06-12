using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Network;

/// <summary>
/// The connectivity pillar: a thin, cold-path wrapper over the Win32 <b>Network List Manager</b> COM API
/// (<c>netlistmgr.h</c>) exposing "are we online?", the coarse connectivity level, and a connection-point subscription
/// for change notifications. Hand-bound through TerraFX's connection-point primitives plus the locally declared
/// <see cref="INetworkListManager"/>/<see cref="INetworkListManagerEvents"/> (TerraFX does not project <c>netlistmgr</c>;
/// see <see cref="NetworkListManagerComConstants"/>). Zero CsWinRT, zero <c>ComWrappers</c> on the call-OUT path, zero
/// reflection — same doctrine as the V1 toast pillar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Network List Manager and not WinRT <c>NetworkInformation</c>.</b> <c>Windows.Networking.Connectivity</c> would
/// cost a CsWinRT/<c>ComWrappers</c> activation the repo forbids, and its <c>NetworkStatusChanged</c> static event is a
/// notorious source of leaks (the handler must be explicitly removed). The classic NLM COM object is identity-free,
/// AOT/trim-clean (flat vtable call-out + a generated sink), and its connection-point lifecycle (<c>Advise</c>/
/// <c>Unadvise</c>) is deterministic.
/// </para>
/// <para>
/// <b>Thread affinity &amp; the message-pump requirement.</b> Every member creates the NLM COM object on the CALLING
/// thread, so the calling thread must have COM initialized:
/// <list type="bullet">
/// <item><see cref="IsOnline"/> and <see cref="GetConnectivity"/> are one-shot: they create the manager, read, and
/// release it within the call. They defensively <c>CoInitializeEx(APARTMENTTHREADED)</c> and balance it, so they work
/// from a thread that has not yet initialized COM (e.g. the <c>--windowsapi-smoke</c> harness). They are safe to call
/// from any thread.</item>
/// <item><see cref="Subscribe"/> returns a live subscription whose connection point holds the manager for the
/// subscription's lifetime, so it does NOT balance the apartment. <b>It must be called from a thread that stays
/// COM-initialized and pumps messages</b> — NLM delivers <c>ConnectivityChanged</c> through the apartment's message
/// pump, so on an STA thread with no running pump (or after the thread exits) no callbacks arrive. The gallery's UI
/// thread satisfies this. The callback fires on that pump's thread; the sink does no marshalling (see
/// <see cref="NetworkListManagerEventSink"/>).</item>
/// </list>
/// </para>
/// <para>
/// References: <see href="https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/nn-netlistmgr-inetworklistmanager">INetworkListManager (netlistmgr.h)</see>,
/// <see href="https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/nn-netlistmgr-inetworklistmanagerevents">INetworkListManagerEvents</see>,
/// and the connection-point pattern (<c>IConnectionPointContainer::FindConnectionPoint</c> + <c>IConnectionPoint::Advise</c>).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public static unsafe class NetworkStatus
{
    // Either stack's internet bit means "online" — the same rule the event sink uses, so polled and pushed agree.
    private const NLM_CONNECTIVITY InternetMask = NLM_CONNECTIVITY.Ipv4Internet | NLM_CONNECTIVITY.Ipv6Internet;
    private const NLM_CONNECTIVITY LocalMask =
        NLM_CONNECTIVITY.Ipv4Subnet | NLM_CONNECTIVITY.Ipv4LocalNetwork |
        NLM_CONNECTIVITY.Ipv6Subnet | NLM_CONNECTIVITY.Ipv6LocalNetwork |
        NLM_CONNECTIVITY.Ipv4NoTraffic | NLM_CONNECTIVITY.Ipv6NoTraffic;

    /// <summary>
    /// <see langword="true"/> when the OS believes the machine has a route to the public internet
    /// (<c>INetworkListManager::get_IsConnectedToInternet</c>). This is the NCSI verdict — not a guarantee a particular
    /// host is reachable (a captive portal or DNS outage can still block traffic). Returns <see langword="false"/> if the
    /// NLM object cannot be created or the call fails (the conservative "assume offline" default). Safe on any thread.
    /// </summary>
    public static bool IsOnline
    {
        get
        {
            using var com = ComApartment.Enter();
            INetworkListManager* mgr = TryCreateManager();
            if (mgr == null)
                return false;
            try
            {
                short isConnected;
                int hr = mgr->get_IsConnectedToInternet(&isConnected);
                return hr >= 0 && isConnected != 0;   // VARIANT_TRUE is -1; any nonzero is "connected".
            }
            finally
            {
                mgr->Release();
            }
        }
    }

    /// <summary>
    /// The coarse internet-reachability level (<c>INetworkListManager::GetConnectivity</c> distilled into
    /// <see cref="NetworkConnectivityLevel"/>). Returns <see cref="NetworkConnectivityLevel.None"/> if the NLM object
    /// cannot be created or the call fails. Safe on any thread.
    /// </summary>
    public static NetworkConnectivityLevel GetConnectivity()
    {
        using var com = ComApartment.Enter();
        INetworkListManager* mgr = TryCreateManager();
        if (mgr == null)
            return NetworkConnectivityLevel.None;
        try
        {
            NLM_CONNECTIVITY flags;
            int hr = mgr->GetConnectivity(&flags);
            if (hr < 0)
                return NetworkConnectivityLevel.None;
            return Map(flags);
        }
        finally
        {
            mgr->Release();
        }
    }

    /// <summary>
    /// Map the raw <c>NLM_CONNECTIVITY</c> flags to the three-state level: an internet bit wins, else a local-network bit
    /// is <see cref="NetworkConnectivityLevel.LocalAccess"/>, else <see cref="NetworkConnectivityLevel.None"/>.
    /// </summary>
    internal static NetworkConnectivityLevel Map(NLM_CONNECTIVITY flags)
    {
        if ((flags & InternetMask) != 0)
            return NetworkConnectivityLevel.InternetAccess;
        if ((flags & LocalMask) != 0)
            return NetworkConnectivityLevel.LocalAccess;
        return NetworkConnectivityLevel.None;
    }

    /// <summary>
    /// Subscribe to connectivity changes via the NLM connection point. <paramref name="onlineChanged"/> is invoked with
    /// the new online/offline state (derived with the same internet-bit rule as <see cref="IsOnline"/>) each time NLM
    /// raises <c>ConnectivityChanged</c>. Dispose the returned object to <c>Unadvise</c> and release everything.
    /// </summary>
    /// <param name="onlineChanged">Handler invoked on connectivity change. <b>Invoked on the NLM callback thread</b>
    /// (the apartment's pump thread — see the type remarks); marshal to your UI thread yourself if needed. Must not be
    /// <see langword="null"/>.</param>
    /// <returns>An <see cref="IDisposable"/> that, when disposed, unsubscribes. Disposing is idempotent and releases the
    /// underlying NLM object and connection point.</returns>
    /// <remarks>
    /// <b>Call this from a COM-initialized, message-pumping thread</b> (the gallery's UI thread). It does NOT initialize
    /// or pump on your behalf — NLM events require a running pump in the apartment that created the connection point. If
    /// the manager cannot be created or <c>Advise</c> fails, a non-throwing inert subscription is returned (disposing it
    /// is a no-op), so a host without connectivity-event support degrades gracefully rather than crashing.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onlineChanged"/> is <see langword="null"/>.</exception>
    public static IDisposable Subscribe(Action<bool> onlineChanged)
    {
        ArgumentNullException.ThrowIfNull(onlineChanged);
        return NetworkStatusSubscription.Create(onlineChanged);
    }

    /// <summary>
    /// <c>CoCreateInstance(CLSID_NetworkListManager, IID_INetworkListManager)</c>. Returns <see langword="null"/> on
    /// failure (the cold-path callers all treat a null manager as "no info"). The caller owns one reference and must
    /// <c>Release</c> it.
    /// </summary>
    internal static INetworkListManager* TryCreateManager()
    {
        Guid clsid = NetworkListManagerComConstants.CLSID_NetworkListManager;
        Guid iid = NetworkListManagerComConstants.IID_INetworkListManager;
        void* ppv = null;
        // The NetworkListManager coclass is registered InprocServer32 (netprofm.dll), so an in-proc activation is the
        // correct (and house-consistent — cf. the Dialogs/Shell coclasses) context. TerraFX does not project the
        // composite CLSCTX_ALL #define as an enum member, so use the in-proc member directly.
        int hr = CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, &ppv);
        if (hr < 0 || ppv == null)
            return null;
        return INetworkListManager.From(ppv);
    }

    /// <summary>
    /// A scope-bounded COM apartment initialization for the one-shot pollers. <c>CoInitializeEx(APARTMENTTHREADED)</c> on
    /// <see cref="Enter"/>, balanced with <c>CoUninitialize</c> on <see cref="Dispose"/> for every <i>successful</i> init
    /// (the COM contract: <c>S_OK</c> AND <c>S_FALSE</c> each take a reference that must be balanced; only
    /// <c>RPC_E_CHANGED_MODE</c> — the thread is already in the other apartment model — takes no reference and must NOT
    /// be balanced).
    /// </summary>
    private readonly struct ComApartment : IDisposable
    {
        private readonly bool _initialized;
        private ComApartment(bool initialized) => _initialized = initialized;

        public static ComApartment Enter()
        {
            int hr = CoInitializeEx(null, (uint)COINIT.COINIT_APARTMENTTHREADED);
            // hr == S_OK (0)   → first init on this thread; balance with CoUninitialize.
            // hr == S_FALSE(1) → already initialized; STILL took a refcount → balance with CoUninitialize.
            // hr == RPC_E_CHANGED_MODE (<0) → already initialized as MTA; no refcount taken → do NOT balance, but COM is
            //                   usable so the CoCreateInstance still works.
            // other hr < 0     → init genuinely failed; nothing to balance; CoCreateInstance will fail and the caller
            //                   returns its conservative default.
            bool initialized = hr >= 0;   // S_OK or S_FALSE.
            return new ComApartment(initialized);
        }

        public void Dispose()
        {
            if (_initialized)
                CoUninitialize();
        }
    }
}

/// <summary>
/// The live connectivity subscription returned by <see cref="NetworkStatus.Subscribe"/>: it holds the NLM object, its
/// <c>IConnectionPoint</c>, the Advise cookie, and the AddRef-owned CCW pointer for the managed sink, releasing them all
/// on <see cref="Dispose"/> (<c>Unadvise</c> first). Created on, and intended to live and die on, the caller's
/// COM-initialized pump thread.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed unsafe class NetworkStatusSubscription : IDisposable
{
    /// <summary>The shared generated-COM marshaller used to realize the sink's native <c>IUnknown</c> (the sanctioned
    /// non-subclassed <see cref="StrategyBasedComWrappers"/> — same instance idiom as the toast activator).</summary>
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly NetworkListManagerEventSink _sink;   // kept alive so the CCW target is not collected.
    private INetworkListManager* _manager;        // held for the subscription's lifetime (see Create remarks).
    private IConnectionPoint* _connectionPoint;
    private nint _sinkUnknown;   // the IUnknown* passed to Advise (we own one ref for the subscription's lifetime).
    private uint _cookie;
    private bool _advised;

    private NetworkStatusSubscription(NetworkListManagerEventSink sink) => _sink = sink;

    /// <summary>
    /// Build the subscription: create the manager, QI its <c>IConnectionPointContainer</c>, find the
    /// <c>INetworkListManagerEvents</c> connection point, and <c>Advise</c> the sink. On any failure an inert (already
    /// disposed) subscription is returned so <see cref="NetworkStatus.Subscribe"/> never throws on a host that lacks
    /// NLM event support.
    /// <para>
    /// <b>Manager lifetime.</b> The manager reference is held for the whole subscription (released in
    /// <see cref="Dispose"/>), NOT dropped once the connection point is obtained: a connection point is a sub-object of
    /// the coclass and does not necessarily keep a strong reference back to it, so releasing the manager early could tear
    /// the connection point down underneath us. The transient <c>IConnectionPointContainer</c> QI is released
    /// immediately (it is only needed to reach the connection point).
    /// </para>
    /// </summary>
    internal static NetworkStatusSubscription Create(Action<bool> onlineChanged)
    {
        var sink = new NetworkListManagerEventSink(onlineChanged);
        var sub = new NetworkStatusSubscription(sink);

        INetworkListManager* mgr = NetworkStatus.TryCreateManager();
        if (mgr == null)
            return sub;   // inert: nothing to dispose.

        IConnectionPointContainer* cpc = null;
        bool keepManager = false;
        try
        {
            Guid iidCpc = NetworkListManagerComConstants.IID_IConnectionPointContainer;
            if (mgr->QueryInterface(&iidCpc, (void**)&cpc) < 0 || cpc == null)
                return sub;

            IConnectionPoint* cp = null;
            Guid iidEvents = NetworkListManagerComConstants.IID_INetworkListManagerEvents;
            if (cpc->FindConnectionPoint(&iidEvents, &cp) < 0 || cp == null)
                return sub;

            // Realize the sink's IUnknown via the generated ComWrappers (one ref we hold until Unadvise), then Advise.
            nint sinkUnknown = ComWrappers.GetOrCreateComInterfaceForObject(sink, CreateComInterfaceFlags.None);
            uint cookie;
            int hr = cp->Advise((IUnknown*)sinkUnknown, &cookie);
            if (hr < 0)
            {
                Marshal.Release(sinkUnknown);
                cp->Release();
                return sub;   // inert.
            }

            sub._manager = mgr;          // transfer ownership of the manager ref to the live subscription.
            sub._connectionPoint = cp;   // keep the connection point for Unadvise on dispose.
            sub._sinkUnknown = sinkUnknown;
            sub._cookie = cookie;
            sub._advised = true;
            keepManager = true;
            return sub;
        }
        finally
        {
            if (cpc != null) cpc->Release();
            // Release the manager ONLY on a failure path; the success path transferred its ref to the subscription.
            if (!keepManager) mgr->Release();
        }
    }

    /// <summary>Unadvise the sink, release the connection point, the sink CCW reference, and the manager. Idempotent.</summary>
    public void Dispose()
    {
        if (_advised && _connectionPoint != null)
        {
            _connectionPoint->Unadvise(_cookie);
            _cookie = 0;
            _advised = false;
        }
        if (_connectionPoint != null)
        {
            _connectionPoint->Release();
            _connectionPoint = null;
        }
        if (_sinkUnknown != 0)
        {
            Marshal.Release(_sinkUnknown);
            _sinkUnknown = 0;
        }
        if (_manager != null)
        {
            _manager->Release();
            _manager = null;
        }
        GC.KeepAlive(_sink);   // ensure the CCW target outlives the Release above.
    }
}
