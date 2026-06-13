using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;

namespace FluentGpu.WindowsApi.Network;

/// <summary>
/// The hand-declared COM surface for the <b>Network List Manager</b> (<c>netlistmgr.h</c>) — the one set of declarations
/// this pillar must author itself because TerraFX.Interop.Windows 10.0.26100.6 does <b>not</b> project <c>netlistmgr</c>
/// (verified: <c>INetworkListManager</c>, <c>INetworkListManagerEvents</c>, <c>NLM_CONNECTIVITY</c>, and
/// <c>CLSID_NetworkListManager</c> are all absent from the assembly, while the connection-point primitives
/// <c>IConnectionPointContainer</c>/<c>IConnectionPoint</c>, <c>IUnknown</c>, <c>CoCreateInstance</c>, <c>CLSCTX</c> are
/// present and reused).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two interop shapes, per the repo COM doctrine</b> (<c>design/dotnet10-csharp14-zero-alloc.md §4</c>; the V1 toast
/// pillar is the living exemplar — call-OUT via a hand vtable like <c>ToastNotifier</c>/<c>IToastNotifier</c>, call-IN via
/// <c>[GeneratedComInterface]</c>/<c>[GeneratedComClass]</c> like <c>ToastActivator</c>):
/// <list type="bullet">
/// <item><b>Call-OUT:</b> <see cref="INetworkListManager"/> is consumed (we call <i>into</i> the OS object), so it is a
/// blittable vtable struct indexed by slot — NO <c>[ComImport]</c>, NO <c>ComWrappers</c> on this path. We only declare
/// the three slots we use (<c>get_IsConnectedToInternet</c>, <c>GetConnectivity</c>, plus <c>IUnknown</c> for QI/Release);
/// the unused IDispatch and enumeration slots are reserved as opaque pointers so the vtable layout stays correct.</item>
/// <item><b>Call-IN:</b> <see cref="INetworkListManagerEvents"/> is implemented by us (the OS calls into our sink), so it
/// is a <c>[GeneratedComInterface]</c> the COM source generator builds the managed→native vtable for, with zero
/// reflection.</item>
/// </list>
/// </para>
/// <para>
/// <b>IID / CLSID provenance.</b> All four GUIDs are transcribed verbatim from the Windows SDK header
/// <c>netlistmgr.h</c> (10.0.26100.0): <c>INetworkListManager</c> <c>DCB00000-570F-4A9B-8D69-199FDBA5723B</c>,
/// <c>INetworkListManagerEvents</c> <c>DCB00001-570F-4A9B-8D69-199FDBA5723B</c>, <c>CLSID_NetworkListManager</c>
/// <c>DCB00C01-570F-4A9B-8D69-199FDBA5723B</c>. They are NOT available from TerraFX's <c>__uuidof&lt;T&gt;()</c> (which
/// only knows TerraFX-declared types), so they live here as explicit values.
/// </para>
/// <para>
/// <b>Vtable layout discipline.</b> <see cref="INetworkListManager"/> derives from <c>IDispatch</c> (which derives from
/// <c>IUnknown</c>), so its instance methods begin at vtable slot 7. The slot indices on
/// <see cref="INetworkListManager"/> are taken straight from the <c>netlistmgr.h</c> declaration order
/// (<c>get_IsConnectedToInternet</c> = slot 11, <c>GetConnectivity</c> = slot 13). Getting a slot index wrong silently
/// calls the wrong method, so the full ordered set is documented on the struct even though only three are typed.
/// </para>
/// </remarks>
internal static class NetworkListManagerComConstants
{
    /// <summary>IID of <c>INetworkListManager</c> (netlistmgr.h: <c>DCB00000-570F-4A9B-8D69-199FDBA5723B</c>).</summary>
    internal static readonly Guid IID_INetworkListManager = new("DCB00000-570F-4A9B-8D69-199FDBA5723B");

    /// <summary>IID of <c>INetworkListManagerEvents</c> (netlistmgr.h: <c>DCB00001-570F-4A9B-8D69-199FDBA5723B</c>) —
    /// the connection-point outgoing interface the sink registers against.</summary>
    internal static readonly Guid IID_INetworkListManagerEvents = new("DCB00001-570F-4A9B-8D69-199FDBA5723B");

    /// <summary>CLSID of the <c>NetworkListManager</c> coclass (netlistmgr.h:
    /// <c>DCB00C01-570F-4A9B-8D69-199FDBA5723B</c>) — passed to <c>CoCreateInstance</c>.</summary>
    internal static readonly Guid CLSID_NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    /// <summary>IID of <c>IConnectionPointContainer</c> (ocidl.h: <c>B196B284-BAB4-101A-B69C-00AA00341D07</c>). Stated
    /// explicitly (rather than via TerraFX's <c>__uuidof&lt;IConnectionPointContainer&gt;()</c>) so the connectivity
    /// connection-point QI is self-documenting and uses the same constant style as the netlistmgr IIDs above; the
    /// <c>IConnectionPointContainer</c> struct it is QI'd to is still TerraFX's.</summary>
    internal static readonly Guid IID_IConnectionPointContainer = new("B196B284-BAB4-101A-B69C-00AA00341D07");

    /// <summary><c>VARIANT_TRUE</c> (<c>wtypes.h</c>: a <c>VARIANT_BOOL</c> of <c>-1</c>). The NLM internet-connected
    /// getter returns a <c>VARIANT_BOOL</c> (a <c>short</c>); any nonzero value is "true", but the platform yields
    /// exactly <c>VARIANT_TRUE</c>.</summary>
    internal const short VARIANT_TRUE = -1;
}

/// <summary>
/// The bit-flags returned by <c>INetworkListManager::GetConnectivity</c> (<c>netlistmgr.h NLM_CONNECTIVITY</c>). The
/// values are OR-combined across the IPv4 and IPv6 stacks; <see cref="NetworkStatus.GetConnectivity"/> distills them into
/// the three-state <see cref="NetworkConnectivityLevel"/>. Declared here verbatim because TerraFX does not project it.
/// </summary>
[Flags]
internal enum NLM_CONNECTIVITY
{
    /// <summary>The underlying networks are in a disconnected state (no <c>*_CONNECTED</c> bit set).</summary>
    Disconnected = 0x0,
    /// <summary>IPv4 connectivity exists to the local subnet only, with no further traffic possible.</summary>
    Ipv4NoTraffic = 0x1,
    /// <summary>IPv6 connectivity exists to the local subnet only, with no further traffic possible.</summary>
    Ipv6NoTraffic = 0x2,
    /// <summary>Local IPv4 connectivity to the current subnet.</summary>
    Ipv4Subnet = 0x10,
    /// <summary>Local IPv4 connectivity to a routed network.</summary>
    Ipv4LocalNetwork = 0x20,
    /// <summary>IPv4 connectivity to the internet.</summary>
    Ipv4Internet = 0x40,
    /// <summary>Local IPv6 connectivity to the current subnet.</summary>
    Ipv6Subnet = 0x100,
    /// <summary>Local IPv6 connectivity to a routed network.</summary>
    Ipv6LocalNetwork = 0x200,
    /// <summary>IPv6 connectivity to the internet.</summary>
    Ipv6Internet = 0x400,
}

/// <summary>
/// A call-OUT vtable wrapper over a native <c>INetworkListManager</c> COM object (slot-indexed, like TerraFX's own
/// interface structs and the toast pillar's <c>IToastNotifier</c> consumption). It owns nothing; the holder
/// (<see cref="NetworkStatus"/>) AddRef/Release-s the underlying pointer. Only the slots this pillar calls are typed —
/// the rest are reserved so the slot indices stay honest (see the type remarks on layout discipline).
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal readonly unsafe struct INetworkListManager
{
    // The native object's vtable pointer (the first machine word of any COM object). `lpVtbl[i]` is the i-th function
    // pointer (stdcall, `this` is arg 0). Instances are only ever reached by reinterpret-casting a native COM pointer
    // via From(...) — never value-constructed — so this field is read through that aliasing, not assigned in C#.
#pragma warning disable CS0649 // Field is never assigned to — it aliases native memory (the COM object's vtable slot).
    private readonly void** _lpVtbl;
#pragma warning restore CS0649

    /// <summary>Reinterpret a raw COM pointer (from <c>CoCreateInstance</c>/QI) as this typed wrapper.</summary>
    internal static INetworkListManager* From(void* p) => (INetworkListManager*)p;

    /// <summary>Recover the <c>this</c> pointer for a vtable thunk call (the canonical TerraFX call-OUT idiom: a
    /// by-pointer-invoked struct method whose <c>this</c> is the COM object's address).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static INetworkListManager* Self(in INetworkListManager self)
        => (INetworkListManager*)Unsafe.AsPointer(ref Unsafe.AsRef(in self));

    // ── IUnknown (slots 0-2) ────────────────────────────────────────────────────────────────────────────────────
    /// <summary><c>IUnknown::QueryInterface</c> (slot 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int QueryInterface(Guid* riid, void** ppv)
        => ((delegate* unmanaged<INetworkListManager*, Guid*, void**, int>)_lpVtbl[0])(Self(in this), riid, ppv);

    /// <summary><c>IUnknown::Release</c> (slot 2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Release()
        => ((delegate* unmanaged<INetworkListManager*, uint>)_lpVtbl[2])(Self(in this));

    // ── IDispatch occupies slots 3-6 (GetTypeInfoCount/GetTypeInfo/GetIDsOfNames/Invoke); unused here. ───────────
    // ── INetworkListManager's OWN methods begin at slot 7, in netlistmgr.h MIDL declaration order (verified against
    //    the win32metadata RecompiledIdlHeaders source — alphabetical doc tables do NOT reflect vtable order):
    //      7  GetNetworks                 8  GetNetwork
    //      9  GetNetworkConnections      10  GetNetworkConnection
    //     11  get_IsConnectedToInternet  12  get_IsConnected
    //     13  GetConnectivity            14  SetSimulatedProfileInfo
    //     15  ClearSimulatedProfileInfo
    //    The two slots this pillar calls are 11 and 13. (Both are one less than a naive 1-based read of the
    //    IUnknown+IDispatch+own list would suggest — the off-by-one this comment exists to prevent.)

    /// <summary>
    /// <c>HRESULT get_IsConnectedToInternet(VARIANT_BOOL* pbIsConnected)</c> — vtable slot 11. Reports whether the
    /// machine has at least one interface with a route to the internet (the OS's NCSI verdict). <paramref name="pb"/>
    /// receives <c>VARIANT_TRUE</c> (-1) / <c>VARIANT_FALSE</c> (0).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int get_IsConnectedToInternet(short* pb)
        => ((delegate* unmanaged<INetworkListManager*, short*, int>)_lpVtbl[11])(Self(in this), pb);

    /// <summary>
    /// <c>HRESULT GetConnectivity(NLM_CONNECTIVITY* pConnectivity)</c> — vtable slot 13. Returns the OR-combined
    /// IPv4/IPv6 connectivity flags for all interfaces.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetConnectivity(NLM_CONNECTIVITY* pConnectivity)
        => ((delegate* unmanaged<INetworkListManager*, NLM_CONNECTIVITY*, int>)_lpVtbl[13])(Self(in this), pConnectivity);
}

/// <summary>
/// The connection-point sink interface FluentGpu <i>implements</i> so the Network List Manager can push connectivity
/// changes (<c>netlistmgr.h INetworkListManagerEvents</c>, IID <c>DCB00001-…</c>). It derives from <c>IUnknown</c> (NOT
/// <c>IDispatch</c> — unusual for an NLM events interface, but that is what the header declares), so it has exactly one
/// method beyond <c>IUnknown</c>. Declared <c>[GeneratedComInterface]</c> so the COM source generator emits the
/// managed→native vtable with no reflection (the toast pillar's <c>INotificationActivationCallback</c> is the exemplar).
/// </summary>
/// <remarks>
/// The native signature is <c>HRESULT ConnectivityChanged(NLM_CONNECTIVITY newConnectivity)</c>. The parameter is passed
/// <i>by value</i> (a 4-byte enum), and the method is <c>[PreserveSig]</c> returning the HRESULT directly so the
/// generated stub is a thin pass-through with no implicit allocation on the callback thread.
/// </remarks>
[GeneratedComInterface]
[Guid("DCB00001-570F-4A9B-8D69-199FDBA5723B")]
internal partial interface INetworkListManagerEvents
{
    /// <summary>Fired by NLM when overall connectivity changes. <paramref name="newConnectivity"/> is the new
    /// OR-combined <c>NLM_CONNECTIVITY</c>. Return S_OK.</summary>
    [PreserveSig]
    int ConnectivityChanged(NLM_CONNECTIVITY newConnectivity);
}
