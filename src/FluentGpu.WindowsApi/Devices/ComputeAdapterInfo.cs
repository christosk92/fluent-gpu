using System;
using System.Globalization;

namespace FluentGpu.WindowsApi.Devices;

/// <summary>
/// The kind of compute adapter to enumerate — one value per <c>DXCoreHardwareTypeFilterFlags</c> bit
/// (<c>dxcore_interface.h</c>): a GPU, a compute-only accelerator (MCDM), a neural processing unit, or a media
/// accelerator. Passed to <see cref="ComputeAdapters.Enumerate"/>.
/// </summary>
public enum ComputeAdapterKind : byte
{
    /// <summary>A graphics processor (<c>DXCoreHardwareTypeFilterFlags::GPU</c>).</summary>
    Gpu,
    /// <summary>A compute-only accelerator with no graphics engine (<c>DXCoreHardwareTypeFilterFlags::ComputeAccelerator</c>).</summary>
    ComputeAccelerator,
    /// <summary>A neural processing unit (<c>DXCoreHardwareTypeFilterFlags::NPU</c>) — the on-device AI target.</summary>
    Npu,
    /// <summary>A media (encode/decode) accelerator (<c>DXCoreHardwareTypeFilterFlags::MediaAccelerator</c>).</summary>
    MediaAccelerator,
}

/// <summary>
/// The hardware vendor behind a DXCore adapter, decoded from its vendor id by
/// <see cref="ComputeAdapterVendors.FromPciId"/>: the NPU vendors Windows ML knows plus the GPU trio. Anything else is
/// <see cref="Unknown"/> (the raw id stays available on <see cref="ComputeAdapterInfo.VendorId"/>).
/// </summary>
public enum AdapterVendor : byte
{
    /// <summary>A vendor id this table does not know.</summary>
    Unknown,
    /// <summary>Qualcomm — ACPI id <c>0x4D4F4351</c> ("QCOM" as little-endian ASCII) or PCI id <c>0x5143</c>.</summary>
    Qualcomm,
    /// <summary>Intel — PCI id <c>0x8086</c>.</summary>
    Intel,
    /// <summary>AMD — PCI id <c>0x1002</c>.</summary>
    Amd,
    /// <summary>NVIDIA — PCI id <c>0x10DE</c>.</summary>
    Nvidia,
    /// <summary>Microsoft — PCI id <c>0x1414</c> (the Basic Render/Display drivers and other Microsoft-owned adapters).</summary>
    Microsoft,
}

/// <summary>
/// One hardware compute adapter as DXCore reports it — an immutable snapshot produced by
/// <see cref="ComputeAdapters.Enumerate"/>. Software adapters (WARP / Basic Render) are never produced, so
/// <see cref="IsHardware"/> is <see langword="true"/> on every instance the enumerator returns; the field is kept so
/// the record mirrors the DXCore property set honestly.
/// </summary>
/// <param name="Kind">The hardware-type filter the adapter was enumerated under.</param>
/// <param name="VendorId">The raw vendor id (<c>DXCoreHardwareID.vendorID</c>): a 16-bit PCI id, or a 32-bit ACPI id
/// such as <c>0x4D4F4351</c> ("QCOM") for an ACPI-enumerated SoC block. Decoded by <see cref="Vendor"/>.</param>
/// <param name="DeviceId">The device id (<c>DXCoreHardwareID.deviceID</c>).</param>
/// <param name="SubSysId">The subsystem id (<c>DXCoreHardwareID.subSysID</c>, the PnP <c>SUBSYS_ssssvvvv</c> word).</param>
/// <param name="Revision">The hardware revision (<c>DXCoreHardwareID.revision</c>).</param>
/// <param name="Description">The driver's description string (<c>DXCoreAdapterProperty::DriverDescription</c>, e.g.
/// "Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Hexagon(TM) NPU"); empty when the driver reports none.</param>
/// <param name="DriverVersionRaw">The packed driver version (<c>DXCoreAdapterProperty::DriverVersion</c>, four 16-bit
/// fields high to low); formatted by <see cref="DriverVersion"/>. 0 when unsupported.</param>
/// <param name="IsHardware">True for a hardware adapter (<c>DXCoreAdapterProperty::IsHardware</c>).</param>
/// <param name="IsIntegrated">True for an adapter integrated into the SoC/CPU package
/// (<c>DXCoreAdapterProperty::IsIntegrated</c>); false when discrete or unreported.</param>
/// <param name="DedicatedMemoryBytes">Dedicated adapter memory in bytes (<c>DXCoreAdapterProperty::DedicatedAdapterMemory</c>);
/// 0 for a shared-memory (integrated / NPU) adapter or when unreported.</param>
public readonly record struct ComputeAdapterInfo(
    ComputeAdapterKind Kind,
    uint VendorId,
    uint DeviceId,
    uint SubSysId,
    uint Revision,
    string Description,
    ulong DriverVersionRaw,
    bool IsHardware,
    bool IsIntegrated,
    ulong DedicatedMemoryBytes)
{
    /// <summary>The decoded vendor (<see cref="ComputeAdapterVendors.FromPciId"/> of <see cref="VendorId"/>).</summary>
    public AdapterVendor Vendor => ComputeAdapterVendors.FromPciId(VendorId);

    /// <summary>The driver version as a dotted quad, e.g. "31.0.210.5"
    /// (<see cref="ComputeAdapterVendors.FormatDriverVersion"/> of <see cref="DriverVersionRaw"/>).</summary>
    public string DriverVersion => ComputeAdapterVendors.FormatDriverVersion(DriverVersionRaw);
}

/// <summary>
/// Pure helpers behind <see cref="ComputeAdapterInfo"/>: vendor-id decoding, driver-version formatting and vendor display
/// names. No OS call — unit-tested in <c>FluentGpu.Windows.Tests</c>.
/// </summary>
public static class ComputeAdapterVendors
{
    // Vendor ids. A PCI device reports its 16-bit PCI-SIG vendor id; an ACPI-enumerated SoC block reports its 4-character
    // ACPI vendor id packed little-endian into 32 bits ("QCOM" → 'Q'=0x51 'C'=0x43 'O'=0x4F 'M'=0x4D → 0x4D4F4351).
    // Qualcomm accepts both: which form DXCore returns for the Snapdragon X Elite Hexagon NPU (ACPI\VEN_QCOM) has not yet
    // been observed on the target box, so the ACPI id and Qualcomm's PCI id 0x5143 ("QC") both decode to Qualcomm.
    private const uint QualcommAcpi = 0x4D4F4351;   // "QCOM"
    private const uint QualcommPci = 0x5143;
    private const uint IntelPci = 0x8086;
    private const uint AmdPci = 0x1002;
    private const uint NvidiaPci = 0x10DE;
    private const uint MicrosoftPci = 0x1414;

    /// <summary>
    /// Decode a DXCore vendor id: <c>0x4D4F4351</c> ("QCOM", ACPI) and <c>0x5143</c> (PCI) → <see cref="AdapterVendor.Qualcomm"/>,
    /// <c>0x8086</c> → <see cref="AdapterVendor.Intel"/>, <c>0x1002</c> → <see cref="AdapterVendor.Amd"/>, <c>0x10DE</c> →
    /// <see cref="AdapterVendor.Nvidia"/>, <c>0x1414</c> → <see cref="AdapterVendor.Microsoft"/>; anything else →
    /// <see cref="AdapterVendor.Unknown"/>.
    /// </summary>
    /// <param name="vendorId">The raw <c>DXCoreHardwareID.vendorID</c>.</param>
    public static AdapterVendor FromPciId(uint vendorId) => vendorId switch
    {
        QualcommAcpi or QualcommPci => AdapterVendor.Qualcomm,
        IntelPci => AdapterVendor.Intel,
        AmdPci => AdapterVendor.Amd,
        NvidiaPci => AdapterVendor.Nvidia,
        MicrosoftPci => AdapterVendor.Microsoft,
        _ => AdapterVendor.Unknown,
    };

    /// <summary>
    /// Format a packed DXCore/DXGI driver version (<c>LARGE_INTEGER UMDVersion</c> layout: four 16-bit fields, high to
    /// low) as a dotted quad — <c>0x001F_0000_00D2_0005</c> → "31.0.210.5", 0 → "0.0.0.0". Culture-invariant.
    /// </summary>
    /// <param name="packed">The raw <c>DXCoreAdapterProperty::DriverVersion</c> value.</param>
    public static string FormatDriverVersion(ulong packed) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(packed >> 48) & 0xFFFF}.{(packed >> 32) & 0xFFFF}.{(packed >> 16) & 0xFFFF}.{packed & 0xFFFF}");

    /// <summary>The vendor's display name ("Qualcomm", "Intel", "AMD", "NVIDIA", "Microsoft"; "Unknown" otherwise).</summary>
    /// <param name="vendor">The decoded vendor.</param>
    public static string DisplayName(AdapterVendor vendor) => vendor switch
    {
        AdapterVendor.Qualcomm => "Qualcomm",
        AdapterVendor.Intel => "Intel",
        AdapterVendor.Amd => "AMD",
        AdapterVendor.Nvidia => "NVIDIA",
        AdapterVendor.Microsoft => "Microsoft",
        _ => "Unknown",
    };
}
