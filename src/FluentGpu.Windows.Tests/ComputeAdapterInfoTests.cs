using System;
using System.Collections.Generic;
using FluentGpu.WindowsApi.Devices;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The Devices pillar (<see cref="ComputeAdapters"/>): the pure decisions behind <see cref="ComputeAdapterInfo"/> —
/// vendor-id decoding (both Qualcomm id forms), driver-version formatting, display names, the restated DXCore attribute
/// GUIDs — plus one live, fail-soft DXCore round-trip in the style of <see cref="SingleInstanceGateTests"/>'s real
/// Win32 facts: enumerating GPUs never throws and returns only described hardware (an NPU list may be empty).
/// </summary>
public sealed class ComputeAdapterInfoTests
{
    [Theory]
    [InlineData(0x4D4F4351u, AdapterVendor.Qualcomm)]   // ACPI "QCOM" (little-endian ASCII)
    [InlineData(0x5143u, AdapterVendor.Qualcomm)]       // PCI "QC"
    [InlineData(0x8086u, AdapterVendor.Intel)]
    [InlineData(0x1002u, AdapterVendor.Amd)]
    [InlineData(0x10DEu, AdapterVendor.Nvidia)]
    [InlineData(0x1414u, AdapterVendor.Microsoft)]
    [InlineData(0u, AdapterVendor.Unknown)]
    [InlineData(0xFFFFu, AdapterVendor.Unknown)]
    [InlineData(0x14E4u, AdapterVendor.Unknown)]         // Broadcom: a real PCI vendor this table does not know
    public void FromPciId_decodes_the_known_vendors(uint vendorId, AdapterVendor expected)
        => Assert.Equal(expected, ComputeAdapterVendors.FromPciId(vendorId));

    [Fact]
    public void The_Qualcomm_acpi_id_is_QCOM_as_little_endian_ascii()
        => Assert.Equal(AdapterVendor.Qualcomm, ComputeAdapterVendors.FromPciId(BitConverter.ToUInt32("QCOM"u8)));

    [Theory]
    [InlineData(0x001F_0000_00D2_0005UL, "31.0.210.5")]
    [InlineData(0UL, "0.0.0.0")]
    [InlineData(0x0001_0002_0003_0004UL, "1.2.3.4")]
    [InlineData(ulong.MaxValue, "65535.65535.65535.65535")]
    public void FormatDriverVersion_prints_four_16_bit_fields_high_to_low(ulong packed, string expected)
        => Assert.Equal(expected, ComputeAdapterVendors.FormatDriverVersion(packed));

    [Theory]
    [InlineData(AdapterVendor.Qualcomm, "Qualcomm")]
    [InlineData(AdapterVendor.Intel, "Intel")]
    [InlineData(AdapterVendor.Amd, "AMD")]
    [InlineData(AdapterVendor.Nvidia, "NVIDIA")]
    [InlineData(AdapterVendor.Microsoft, "Microsoft")]
    [InlineData(AdapterVendor.Unknown, "Unknown")]
    public void DisplayName_names_every_vendor(AdapterVendor vendor, string expected)
        => Assert.Equal(expected, ComputeAdapterVendors.DisplayName(vendor));

    [Fact]
    public void Info_projects_vendor_and_driver_version_from_the_raw_fields()
    {
        var npu = new ComputeAdapterInfo(ComputeAdapterKind.Npu, 0x4D4F4351, 0x1, 0, 0, "Hexagon NPU",
            0x001F_0000_00D2_0005UL, IsHardware: true, IsIntegrated: true, DedicatedMemoryBytes: 0);
        Assert.Equal(AdapterVendor.Qualcomm, npu.Vendor);
        Assert.Equal("31.0.210.5", npu.DriverVersion);
        Assert.Equal(npu, npu with { });   // value equality (record struct)
    }

    [Fact]
    public void The_restated_attribute_guids_match_dxcore_interface_h()
    {
        Assert.Equal(new Guid("0C9ECE4D-2F6E-4F01-8C96-E89E331B47B1"), ComputeAdapters.DXCORE_ADAPTER_ATTRIBUTE_D3D12_GRAPHICS);
        Assert.Equal(new Guid("248E2800-A793-4724-ABAA-23A6DE1BE090"), ComputeAdapters.DXCORE_ADAPTER_ATTRIBUTE_D3D12_CORE_COMPUTE);
        Assert.Equal(new Guid("B69EB219-3DED-4464-979F-A00BD4687006"), ComputeAdapters.DXCORE_HARDWARE_TYPE_ATTRIBUTE_GPU);
        Assert.Equal(new Guid("E0B195DA-58EF-4A22-90F1-1F28169CAB8D"), ComputeAdapters.DXCORE_HARDWARE_TYPE_ATTRIBUTE_COMPUTE_ACCELERATOR);
        Assert.Equal(new Guid("D46140C4-ADD7-451B-9E56-06FE8C3B58ED"), ComputeAdapters.DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU);
        Assert.Equal(new Guid("66BDB96A-050B-44C7-A4FD-D144CE0AB443"), ComputeAdapters.DXCORE_HARDWARE_TYPE_ATTRIBUTE_MEDIA_ACCELERATOR);
    }

    [Fact]
    public void Enumerate_Gpu_is_fail_soft_and_returns_only_described_hardware()
    {
        // Real DXCore round-trip. A box without dxcore.dll (or a GPU) yields an empty list, never a throw.
        IReadOnlyList<ComputeAdapterInfo> gpus = ComputeAdapters.Enumerate(ComputeAdapterKind.Gpu);
        Assert.NotNull(gpus);
        foreach (ComputeAdapterInfo gpu in gpus)
        {
            Assert.Equal(ComputeAdapterKind.Gpu, gpu.Kind);
            Assert.True(gpu.IsHardware);
            Assert.False(string.IsNullOrWhiteSpace(gpu.Description));
        }
    }

    [Fact]
    public void Enumerate_Npu_and_an_undefined_kind_never_throw()
    {
        // Most PCs have no NPU: empty is a valid answer, and every entry that does come back is hardware.
        IReadOnlyList<ComputeAdapterInfo> npus = ComputeAdapters.Enumerate(ComputeAdapterKind.Npu);
        Assert.All(npus, n => Assert.True(n.IsHardware && n.Kind == ComputeAdapterKind.Npu));
        Assert.Empty(ComputeAdapters.Enumerate((ComputeAdapterKind)99));
    }
}
