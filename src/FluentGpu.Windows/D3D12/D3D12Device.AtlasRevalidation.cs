using FluentGpu.Foundation;
using FluentGpu.Hosting;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

// ── Re-validation probes for the UMA small-image atlas (default off; see EngineSwitches.ImageAtlas / ImagePlaced256Query) ──
//
// Two read-only questions the earlier Adreno work answered by assumption:
//   * ROW_MAJOR (--fg img-atlas=rowmajor-probe): is a CPU-writable ROW_MAJOR TEXTURE2D legal on this adapter, and which
//     create variants does it accept? D3D12 only requires ROW_MAJOR textures cross-adapter (CrossAdapterRowMajorTextureSupported)
//     and offers D3D12_TEXTURE_LAYOUT_64KB_STANDARD_SWIZZLE as the CPU-writable known layout where
//     StandardSwizzle64KBSupported. The earlier 0x80070057 is therefore the spec's answer, not a driver defect; this logs the
//     caps first and then the outcome of each variant, once.
//   * 256 px placement (--fg img-placed=256): is the 64 KiB the driver adds to a 256 KiB 256 px texture per resource or per heap?
//     GetResourceAllocationInfo for 1, 8 and 16 of them in one call answers it without allocating anything.
// Everything created here is released before the method returns; nothing is kept or sampled.
public sealed unsafe partial class D3D12Device
{
    /// <summary>Run the requested one-shot probes. Device creation, before the first frame.</summary>
    private void RunAtlasRevalidationProbes()
    {
        if (_device == null) return;
        try
        {
            if (EngineSwitches.ImageAtlas == ImageAtlasExperiment.RowMajorProbe) ProbeRowMajorVariants();
            if (EngineSwitches.ImagePlaced256Query) ProbePlacement256();
        }
        catch (Exception ex) { Diag.Line($"[d3d12] atlas re-validation probe threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private static D3D12_RESOURCE_DESC AtlasProbeDesc(int side, DXGI_FORMAT format, D3D12_TEXTURE_LAYOUT layout, ulong alignment = 0,
        D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE)
    {
        D3D12_RESOURCE_DESC td = default;
        td.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Alignment = alignment;
        td.Width = (ulong)side; td.Height = (uint)side; td.DepthOrArraySize = 1; td.MipLevels = 1;
        td.Format = format; td.SampleDesc.Count = 1;
        td.Layout = layout; td.Flags = flags;
        return td;
    }

    private void ProbeRowMajorVariants()
    {
        D3D12_FEATURE_DATA_D3D12_OPTIONS opts = default;
        bool haveOpts = (int)_device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS, &opts,
            (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS)) >= 0;
        bool crossAdapterRowMajor = haveOpts && opts.CrossAdapterRowMajorTextureSupported != 0;
        bool standardSwizzle = haveOpts && opts.StandardSwizzle64KBSupported != 0;
        Diag.Line($"[d3d12] rowmajor-probe caps: options={(haveOpts ? "ok" : "unavailable")} CrossAdapterRowMajorTextureSupported={crossAdapterRowMajor} StandardSwizzle64KBSupported={standardSwizzle} uma={_isUnifiedMemory}");

        const DXGI_FORMAT Bgra = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, Rgba = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        const D3D12_TEXTURE_LAYOUT Row = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        // (label, desc, heapFlags, needsCap) - a variant the spec does not allow without its cap is expected to fail; it is logged so the
        // log shows the cap, not a guess, decided the outcome.
        var variants = new (string Label, D3D12_RESOURCE_DESC Desc, D3D12_HEAP_FLAGS Heap, bool SpecAllowed)[]
        {
            ("row-major BGRA 1024 (today's refusal)", AtlasProbeDesc(1024, Bgra, Row), D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, crossAdapterRowMajor),
            ("row-major BGRA 1024 + CROSS_ADAPTER", AtlasProbeDesc(1024, Bgra, Row, 0, D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER),
                D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_SHARED | D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_SHARED_CROSS_ADAPTER, crossAdapterRowMajor),
            ("row-major BGRA 1024 alignment 64K", AtlasProbeDesc(1024, Bgra, Row, 65536), D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, crossAdapterRowMajor),
            ("row-major RGBA 1024", AtlasProbeDesc(1024, Rgba, Row), D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, crossAdapterRowMajor),
            ("row-major BGRA 512", AtlasProbeDesc(512, Bgra, Row), D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, crossAdapterRowMajor),
            ("64KB standard swizzle BGRA 1024", AtlasProbeDesc(1024, Bgra, D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_64KB_STANDARD_SWIZZLE),
                D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, standardSwizzle),
        };
        foreach (var v in variants)
        {
            D3D12_HEAP_PROPERTIES hp = default;
            hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_CUSTOM;
            hp.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY.D3D12_CPU_PAGE_PROPERTY_WRITE_BACK;
            hp.MemoryPoolPreference = D3D12_MEMORY_POOL.D3D12_MEMORY_POOL_L0;
            var desc = v.Desc;
            ID3D12Resource* tex = null;
            int hr = (int)_device->CreateCommittedResource(&hp, v.Heap, &desc, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, null,
                __uuidof<ID3D12Resource>(), (void**)&tex);
            string mapped = "-";
            if (hr >= 0 && tex != null)
            {
                void* p = null;
                int mhr = (int)tex->Map(0, null, &p);
                mapped = mhr >= 0 && p != null ? "map-ok" : $"map-hr=0x{(uint)mhr:X8}";
                if (mhr >= 0) tex->Unmap(0, null);
                tex->Release();
            }
            Diag.Line($"[d3d12] rowmajor-probe {v.Label}: create={(hr >= 0 ? "ok" : $"hr=0x{(uint)hr:X8}")} {mapped} specAllowed={v.SpecAllowed}");
        }
    }

    private void ProbePlacement256()
    {
        const DXGI_FORMAT Bgra = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        const D3D12_TEXTURE_LAYOUT Unknown = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        foreach (ulong alignment in new ulong[] { 0, 4096, 65536 })
        {
            var desc = AtlasProbeDesc(256, Bgra, Unknown, alignment);
            var one = _device->GetResourceAllocationInfo(0, 1, &desc);
            Diag.Line($"[d3d12] placed-256 probe alignment={alignment}: size={one.SizeInBytes} alignment={one.Alignment}");
        }
        var descs = stackalloc D3D12_RESOURCE_DESC[16];
        for (int i = 0; i < 16; i++) descs[i] = AtlasProbeDesc(256, Bgra, Unknown, 0);
        var single = _device->GetResourceAllocationInfo(0, 1, descs);
        foreach (uint n in new uint[] { 1, 8, 16 })
        {
            var total = _device->GetResourceAllocationInfo(0, n, descs);
            Diag.Line($"[d3d12] placed-256 probe n={n}: total={total.SizeInBytes} perTexture={total.SizeInBytes / n} vsSingle={single.SizeInBytes} " +
                      $"{(total.SizeInBytes < n * single.SizeInBytes ? "PER-HEAP padding (a placed 256 bucket could save bytes)" : "per-resource padding (no saving)")}");
        }
    }
}
