namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// SCAFFOLD (the reference Windows RHI backend — see design/subsystems/pal-rhi.md + gpu-renderer.md). Will implement
/// <c>FluentGpu.Rhi.IGpuDevice</c> over D3D12: DIRECT queue, RTV/DSV heaps, D3D12MA, the SDF/quad/glyph graphics PSOs
/// (HLSL→DXC→DXIL), <c>CopyBufferToTexture</c> staging ring, and DXGI flip-model present composed by DirectComposition.
/// The runnable slice currently goes through <c>FluentGpu.Rhi.Headless</c>; this is the single largest remaining item.
/// </summary>
public static class D3D12Backend;
