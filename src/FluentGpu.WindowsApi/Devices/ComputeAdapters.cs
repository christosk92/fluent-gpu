using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Devices;

/// <summary>
/// The Devices pillar: which hardware compute adapters (GPU, compute accelerator, NPU, media accelerator) this PC has,
/// read through <b>DXCore</b> (<c>dxcore.dll</c>, the adapter-enumeration API that, unlike DXGI, also sees MCDM
/// compute-only devices such as NPUs). A cold, one-shot snapshot for gating on-device AI features — "is there a
/// Qualcomm NPU, and which driver does it run?" — not a device-lifetime or per-frame API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Surface.</b> <see cref="IsSupported"/> (does DXCore load at all) and <see cref="Enumerate"/> (the hardware
/// adapters of one <see cref="ComputeAdapterKind"/>, as immutable <see cref="ComputeAdapterInfo"/> records). Both are
/// <b>fail-soft</b> in the <see cref="FluentGpu.WindowsApi.Network.NetworkStatus"/> sense: a missing <c>dxcore.dll</c>,
/// a failed factory, list or property call, or any other fault yields <see langword="false"/> / an empty list —
/// they never throw. A caller treats "empty" as "no such adapter".
/// </para>
/// <para>
/// <b>Threading.</b> DXCore is not apartment-bound and needs no <c>CoInitializeEx</c>: both members are safe on
/// <b>any thread</b>, concurrently. <see cref="Enumerate"/> takes roughly 1-5 ms (it creates the factory, builds a list
/// and reads a handful of properties per adapter), so call it once at startup or on demand from a background task —
/// it is NOT a per-frame call. Every COM pointer it acquires is released before it returns.
/// </para>
/// <para>
/// <b>Filtering.</b> On Windows 11 24H2 (build 26100) and later the factory implements <c>IDXCoreAdapterFactory1</c>,
/// and <see cref="Enumerate"/> asks for <c>CreateAdapterListByWorkload</c> with the workload that matches the kind
/// (<see cref="ComputeAdapterKind.Npu"/> → <c>MachineLearning</c>, <see cref="ComputeAdapterKind.Gpu"/> →
/// <c>Graphics</c>, compute accelerator → <c>Compute</c>, media accelerator → <c>Media</c>) and the matching
/// <c>DXCoreHardwareTypeFilterFlags</c> bit. Where the QI or that call fails (older builds), it falls back to
/// <c>IDXCoreAdapterFactory::CreateAdapterList</c> with the hardware-type attribute GUID for the kind; for
/// <see cref="ComputeAdapterKind.Gpu"/> an empty or failed hardware-type list retries with
/// <c>DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS</c>, which every DXCore build (Windows 10 2004+) understands. A build
/// that predates the hardware-type attributes therefore reports no NPU — the honest answer for an OS whose AI stack
/// could not use one anyway. Software adapters (WARP / Basic Render, <c>IsHardware == false</c>) are always skipped.
/// </para>
/// <para>
/// <b>Bindings.</b> <c>DXCoreCreateAdapterFactory</c>, the <c>IDXCoreAdapterFactory</c>/<c>IDXCoreAdapterFactory1</c>/
/// <c>IDXCoreAdapterList</c>/<c>IDXCoreAdapter</c> vtables, <c>DXCoreAdapterProperty</c>, <c>DXCoreHardwareID</c> and
/// the filter enums come from <c>TerraFX.Interop.Windows</c> (namespace <c>TerraFX.Interop.DirectX</c>). Only the
/// attribute GUIDs — <c>DEFINE_GUID</c>s TerraFX does not project — are restated locally from the Windows SDK header
/// (<c>dxcore_interface.h</c>, 10.0.26100.0), the same house pattern as
/// <see cref="FluentGpu.WindowsApi.Power.PowerSession"/>'s <c>ES_*</c>/<c>PBT_*</c> constants. AOT/trim-clean: flat
/// vtable call-out, no <c>ComWrappers</c>, no reflection. Reference:
/// <see href="https://learn.microsoft.com/en-us/windows/win32/dxcore/dxcore-enum-adapters">Using DXCore to enumerate adapters</see>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]   // dxcore.dll shipped in Windows 10 2004; Factory1/workload filter in 24H2 (26100).
public static unsafe class ComputeAdapters
{
    // ── dxcore_interface.h attribute GUIDs (DEFINE_GUID; not projected by TerraFX — restated from SDK 10.0.26100.0). ─────
    // Internal so FluentGpu.Windows.Tests can pin the restated bytes against the header's registry-format strings.
    // {0C9ECE4D-2F6E-4F01-8C96-E89E331B47B1} — adapter supports D3D12 graphics (every DXCore build).
    internal static readonly Guid DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS =
        new(0x0c9ece4d, 0x2f6e, 0x4f01, 0x8c, 0x96, 0xe8, 0x9e, 0x33, 0x1b, 0x47, 0xb1);
    // {248E2800-A793-4724-ABAA-23A6DE1BE090} — adapter supports D3D12 core compute (GPUs and MCDM compute devices).
    // Restated for completeness of the attribute set; not used as a filter (it does not distinguish an NPU from a GPU).
    internal static readonly Guid DXCORE_ADAPTER_ATTRIBUTE_D3D12_CORE_COMPUTE =
        new(0x248e2800, 0xa793, 0x4724, 0xab, 0xaa, 0x23, 0xa6, 0xde, 0x1b, 0xe0, 0x90);
    // {B69EB219-3DED-4464-979F-A00BD4687006} — hardware type: GPU.
    internal static readonly Guid DXCORE_HARDWARE_TYPE_ATTRIBUTE_GPU =
        new(0xb69eb219, 0x3ded, 0x4464, 0x97, 0x9f, 0xa0, 0x0b, 0xd4, 0x68, 0x70, 0x06);
    // {E0B195DA-58EF-4A22-90F1-1F28169CAB8D} — hardware type: compute accelerator.
    internal static readonly Guid DXCORE_HARDWARE_TYPE_ATTRIBUTE_COMPUTE_ACCELERATOR =
        new(0xe0b195da, 0x58ef, 0x4a22, 0x90, 0xf1, 0x1f, 0x28, 0x16, 0x9c, 0xab, 0x8d);
    // {D46140C4-ADD7-451B-9E56-06FE8C3B58ED} — hardware type: NPU.
    internal static readonly Guid DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU =
        new(0xd46140c4, 0xadd7, 0x451b, 0x9e, 0x56, 0x06, 0xfe, 0x8c, 0x3b, 0x58, 0xed);
    // {66BDB96A-050B-44C7-A4FD-D144CE0AB443} — hardware type: media accelerator.
    internal static readonly Guid DXCORE_HARDWARE_TYPE_ATTRIBUTE_MEDIA_ACCELERATOR =
        new(0x66bdb96a, 0x050b, 0x44c7, 0xa4, 0xfd, 0xd1, 0x44, 0xce, 0x0a, 0xb4, 0x43);

    /// <summary>A driver description longer than this is treated as garbage (the real ones are well under 256 bytes).</summary>
    private const int MaxDescriptionBytes = 4096;

    /// <summary>The cached <see cref="IsSupported"/> probe: 0 = not yet probed, 1 = supported, 2 = unsupported.</summary>
    private static int s_supported;

    /// <summary>
    /// <see langword="true"/> when <c>dxcore.dll</c> loads and <c>DXCoreCreateAdapterFactory</c> succeeds — any
    /// Windows 10 2004+ box. Probed once and cached (a racing first read may probe twice; the answer is the same).
    /// Never throws; safe on any thread.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            int state = Volatile.Read(ref s_supported);
            if (state == 0)
            {
                state = Probe() ? 1 : 2;
                Volatile.Write(ref s_supported, state);
            }
            return state == 1;
        }
    }

    /// <summary>
    /// The hardware adapters of one <paramref name="kind"/>, in DXCore's list order. Empty on any failure (DXCore
    /// missing, factory/list creation failed, an undefined <paramref name="kind"/>) — never throws (fail-soft, like
    /// <see cref="FluentGpu.WindowsApi.Network.NetworkStatus"/>). An adapter whose properties cannot be read is skipped
    /// rather than failing the whole call; software adapters are always skipped.
    /// </summary>
    /// <param name="kind">Which hardware type to list. <see cref="ComputeAdapterKind.Npu"/> may legitimately be empty
    /// (most PCs have no NPU); <see cref="ComputeAdapterKind.Gpu"/> is non-empty on any machine with a display driver.</param>
    /// <returns>A fresh, caller-owned list of snapshots (each with <see cref="ComputeAdapterInfo.Kind"/> ==
    /// <paramref name="kind"/>).</returns>
    /// <remarks>Safe on any thread, no COM initialization needed; takes ~1-5 ms — NOT a per-frame call.</remarks>
    public static IReadOnlyList<ComputeAdapterInfo> Enumerate(ComputeAdapterKind kind)
    {
        if (kind > ComputeAdapterKind.MediaAccelerator || !IsSupported)
            return Array.Empty<ComputeAdapterInfo>();

        IDXCoreAdapterFactory* factory = null;
        IDXCoreAdapterList* list = null;
        try
        {
            Guid factoryIid = __uuidof<IDXCoreAdapterFactory>();
            if (DXCoreCreateAdapterFactory(&factoryIid, (void**)&factory).FAILED || factory == null)
                return Array.Empty<ComputeAdapterInfo>();

            list = CreateList(factory, kind);
            if (list == null)
                return Array.Empty<ComputeAdapterInfo>();

            uint count = list->GetAdapterCount();
            var result = new List<ComputeAdapterInfo>((int)Math.Min(count, 16u));
            Guid adapterIid = __uuidof<IDXCoreAdapter>();
            for (uint i = 0; i < count; i++)
            {
                IDXCoreAdapter* adapter = null;
                try
                {
                    if (list->GetAdapter(i, &adapterIid, (void**)&adapter).FAILED || adapter == null)
                        continue;
                    if (TryRead(adapter, kind, out ComputeAdapterInfo info))
                        result.Add(info);
                }
                finally
                {
                    if (adapter != null) adapter->Release();
                }
            }
            return result;
        }
        catch
        {
            // Fail-soft contract: a fault anywhere in the DXCore call-out (DllNotFoundException / EntryPointNotFound on a
            // stripped OS image, an access violation surfaced as SEHException) reads as "no such adapter".
            return Array.Empty<ComputeAdapterInfo>();
        }
        finally
        {
            if (list != null) list->Release();
            if (factory != null) factory->Release();
        }
    }

    /// <summary>
    /// Build the adapter list for <paramref name="kind"/>: the 24H2 workload filter via <c>IDXCoreAdapterFactory1</c>
    /// when the factory implements it, else the hardware-type attribute list (plus the D3D12-graphics retry for GPUs).
    /// Returns an owned <c>IDXCoreAdapterList*</c> (caller releases) or <see langword="null"/>.
    /// </summary>
    private static IDXCoreAdapterList* CreateList(IDXCoreAdapterFactory* factory, ComputeAdapterKind kind)
    {
        Guid listIid = __uuidof<IDXCoreAdapterList>();
        IDXCoreAdapterList* list = null;

        // 24H2+: the workload + hardware-type filter.
        IDXCoreAdapterFactory1* factory1 = null;
        try
        {
            Guid factory1Iid = __uuidof<IDXCoreAdapterFactory1>();
            if (factory->QueryInterface(&factory1Iid, (void**)&factory1).SUCCEEDED && factory1 != null)
            {
                HRESULT hr = factory1->CreateAdapterListByWorkload(
                    WorkloadFor(kind), DXCoreRuntimeFilterFlags.None, HardwareTypeFor(kind), &listIid, (void**)&list);
                if (hr.SUCCEEDED && list != null)
                    return list;
                list = null;   // a failed call leaves nothing to release; fall through to the attribute path.
            }
        }
        finally
        {
            if (factory1 != null) factory1->Release();
        }

        // Pre-24H2 fallback: the hardware-type attribute for the kind.
        Guid attribute = HardwareTypeAttributeFor(kind);
        if (factory->CreateAdapterList(1, &attribute, &listIid, (void**)&list).SUCCEEDED && list != null)
        {
            if (kind != ComputeAdapterKind.Gpu || list->GetAdapterCount() > 0)
                return list;
            list->Release();   // an empty GPU list on a build without hardware-type attributes: retry below.
            list = null;
        }
        else
        {
            list = null;
        }

        if (kind == ComputeAdapterKind.Gpu)
        {
            Guid graphics = DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS;
            if (factory->CreateAdapterList(1, &graphics, &listIid, (void**)&list).SUCCEEDED && list != null)
                return list;
        }
        return null;
    }

    /// <summary>
    /// Read one adapter into a <see cref="ComputeAdapterInfo"/>. Returns <see langword="false"/> for a software adapter
    /// or one whose hardware id cannot be read. Optional properties a driver does not support read as 0 / false / "".
    /// </summary>
    private static bool TryRead(IDXCoreAdapter* adapter, ComputeAdapterKind kind, out ComputeAdapterInfo info)
    {
        info = default;

        // IsHardware is supported on every DXCore build; if a driver somehow omits it, keep the adapter (it was listed
        // under a hardware-type filter) rather than drop a real device.
        if (TryGet(adapter, DXCoreAdapterProperty.IsHardware, out byte isHardware) && isHardware == 0)
            return false;

        uint vendorId, deviceId, subSysId, revision;
        if (TryGet(adapter, DXCoreAdapterProperty.HardwareID, out DXCoreHardwareID hw))
        {
            vendorId = hw.vendorID;
            deviceId = hw.deviceID;
            subSysId = hw.subSysID;
            revision = hw.revision;
        }
        else if (TryGet(adapter, DXCoreAdapterProperty.HardwareIDParts, out DXCoreHardwareIDParts parts))
        {
            vendorId = parts.vendorID;
            deviceId = parts.deviceID;
            // Recombine into the PnP SUBSYS_ssssvvvv word that DXCoreHardwareID.subSysID carries.
            subSysId = ((parts.subSystemID & 0xFFFF) << 16) | (parts.subVendorID & 0xFFFF);
            revision = parts.revisionID;
        }
        else
        {
            return false;
        }

        TryGet(adapter, DXCoreAdapterProperty.DriverVersion, out ulong driverVersion);
        TryGet(adapter, DXCoreAdapterProperty.IsIntegrated, out byte isIntegrated);
        TryGet(adapter, DXCoreAdapterProperty.DedicatedAdapterMemory, out ulong dedicatedMemory);

        info = new ComputeAdapterInfo(
            kind, vendorId, deviceId, subSysId, revision,
            ReadDescription(adapter), driverVersion,
            IsHardware: true, IsIntegrated: isIntegrated != 0, DedicatedMemoryBytes: dedicatedMemory);
        return true;
    }

    /// <summary>
    /// Read a fixed-size property (<c>IsPropertySupported</c> then <c>GetProperty</c> with <c>sizeof(T)</c>). DXCore
    /// <c>bool</c> properties are one byte, read as <see cref="byte"/>; <c>uint64_t</c> as <see cref="ulong"/>.
    /// </summary>
    private static bool TryGet<T>(IDXCoreAdapter* adapter, DXCoreAdapterProperty property, out T value)
        where T : unmanaged
    {
        value = default;
        if (!adapter->IsPropertySupported(property))
            return false;
        T read = default;
        if (adapter->GetProperty(property, (nuint)sizeof(T), &read).FAILED)
            return false;
        value = read;
        return true;
    }

    /// <summary>
    /// <c>DXCoreAdapterProperty::DriverDescription</c>: <c>GetPropertySize</c>, then <c>GetProperty</c> into a stack (or,
    /// for an unusually long string, heap) buffer. The property is a NUL-terminated UTF-8 <c>CHAR</c> array (per the DXCore
    /// docs). Returns "" when unsupported or unreadable.
    /// </summary>
    private static string ReadDescription(IDXCoreAdapter* adapter)
    {
        if (!adapter->IsPropertySupported(DXCoreAdapterProperty.DriverDescription))
            return string.Empty;
        nuint size = 0;
        if (adapter->GetPropertySize(DXCoreAdapterProperty.DriverDescription, &size).FAILED || size == 0 || size > MaxDescriptionBytes)
            return string.Empty;

        int length = (int)size;
        Span<byte> buffer = length <= 512 ? stackalloc byte[512] : new byte[length];
        buffer = buffer[..length];
        fixed (byte* p = buffer)
        {
            if (adapter->GetProperty(DXCoreAdapterProperty.DriverDescription, size, p).FAILED)
                return string.Empty;
        }
        int nul = buffer.IndexOf((byte)0);
        if (nul >= 0)
            buffer = buffer[..nul];
        return Encoding.UTF8.GetString(buffer).Trim();
    }

    /// <summary>The <c>IDXCoreAdapterFactory1::CreateAdapterListByWorkload</c> workload for a kind.</summary>
    private static DXCoreWorkload WorkloadFor(ComputeAdapterKind kind) => kind switch
    {
        ComputeAdapterKind.Npu => DXCoreWorkload.MachineLearning,
        ComputeAdapterKind.ComputeAccelerator => DXCoreWorkload.Compute,
        ComputeAdapterKind.MediaAccelerator => DXCoreWorkload.Media,
        _ => DXCoreWorkload.Graphics,
    };

    /// <summary>The <c>DXCoreHardwareTypeFilterFlags</c> bit for a kind.</summary>
    private static DXCoreHardwareTypeFilterFlags HardwareTypeFor(ComputeAdapterKind kind) => kind switch
    {
        ComputeAdapterKind.Npu => DXCoreHardwareTypeFilterFlags.NPU,
        ComputeAdapterKind.ComputeAccelerator => DXCoreHardwareTypeFilterFlags.ComputeAccelerator,
        ComputeAdapterKind.MediaAccelerator => DXCoreHardwareTypeFilterFlags.MediaAccelerator,
        _ => DXCoreHardwareTypeFilterFlags.GPU,
    };

    /// <summary>The <c>DXCORE_HARDWARE_TYPE_ATTRIBUTE_*</c> GUID for a kind (the pre-24H2 <c>CreateAdapterList</c> filter).</summary>
    private static Guid HardwareTypeAttributeFor(ComputeAdapterKind kind) => kind switch
    {
        ComputeAdapterKind.Npu => DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU,
        ComputeAdapterKind.ComputeAccelerator => DXCORE_HARDWARE_TYPE_ATTRIBUTE_COMPUTE_ACCELERATOR,
        ComputeAdapterKind.MediaAccelerator => DXCORE_HARDWARE_TYPE_ATTRIBUTE_MEDIA_ACCELERATOR,
        _ => DXCORE_HARDWARE_TYPE_ATTRIBUTE_GPU,
    };

    /// <summary>One <c>DXCoreCreateAdapterFactory</c> round-trip (factory released at once). False on any failure,
    /// including <see cref="DllNotFoundException"/>/<see cref="EntryPointNotFoundException"/> when <c>dxcore.dll</c> is
    /// absent (TerraFX binds the export as a lazy <c>[DllImport("dxcore")]</c>).</summary>
    private static bool Probe()
    {
        IDXCoreAdapterFactory* factory = null;
        try
        {
            Guid iid = __uuidof<IDXCoreAdapterFactory>();
            return DXCoreCreateAdapterFactory(&iid, (void**)&factory).SUCCEEDED && factory != null;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (factory != null) factory->Release();
        }
    }
}
