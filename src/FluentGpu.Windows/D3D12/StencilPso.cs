using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// The tier-3 STENCIL PATH CLIP's shared D3D12 state (gpu-renderer.md §6). One place owns the DSV format, the two
/// depth/stencil descs, and the mechanical "clone this blended quad PSO into its EQUAL-tested variant" build — five
/// pipelines need the same clone (RoundRect's blended arm, Image, Gradient, and both glyph passes) and a second copy
/// of these six enum choices in each of them is exactly the drift the canon rules exist to prevent.
///
/// <para>Everything here is built LAZILY, on the first stencil scope of the process. An app that never clips to a path
/// creates no extra PSOs and pays no extra shader compiles at bring-up.</para>
///
/// <para><b>Why a stencil-TESTED clone at all:</b> D3D12 stencil state lives on the PSO, not as free command-list
/// state, so "draw this run, but masked" is a different pipeline object — there is no `OMSetStencilEnable`. The clone
/// differs from its parent in exactly three fields: <c>StencilEnable</c>, the EQUAL func with all-KEEP ops and a ZERO
/// write mask (a tested draw never mutates the mask), and <c>DSVFormat</c>. Blend, shaders, root signature, input
/// layout, rasterizer and RTV format are copied verbatim, so a clipped run is pixel-identical to an unclipped one
/// wherever the mask admits it.</para>
/// </summary>
internal static unsafe class StencilPso
{
    /// <summary>The ONE depth-stencil format of the tier: the device's DSV, every mask PSO and every tested clone must
    /// agree on it. Single-sample everywhere (this renderer never enables MSAA), so S6's "sample-count-matched DSV"
    /// requirement is satisfied trivially.</summary>
    public const DXGI_FORMAT DsvFormat = DXGI_FORMAT.DXGI_FORMAT_D24_UNORM_S8_UINT;

    /// <summary>The MASK pre-pass state: depth off, stencil always passes and applies <paramref name="passOp"/>
    /// (INCR_SAT on a push, DECR_SAT when an inner pop erases its own nesting level). INCR_SAT saturating at 255 is the
    /// documented max depth — deeper nesting degrades conservatively (an over-deep scope simply stops narrowing),
    /// never corrupts.</summary>
    public static D3D12_DEPTH_STENCIL_DESC Mask(D3D12_STENCIL_OP passOp)
    {
        D3D12_DEPTH_STENCIL_DESC ds = default;
        ds.DepthEnable = BOOL.FALSE;
        ds.DepthWriteMask = D3D12_DEPTH_WRITE_MASK.D3D12_DEPTH_WRITE_MASK_ZERO;
        ds.StencilEnable = BOOL.TRUE;
        ds.StencilReadMask = 0xFF;
        ds.StencilWriteMask = 0xFF;
        ds.FrontFace.StencilFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_ALWAYS;
        ds.FrontFace.StencilPassOp = passOp;
        ds.FrontFace.StencilFailOp = D3D12_STENCIL_OP.D3D12_STENCIL_OP_KEEP;
        ds.FrontFace.StencilDepthFailOp = D3D12_STENCIL_OP.D3D12_STENCIL_OP_KEEP;
        ds.BackFace = ds.FrontFace;   // CullMode is NONE on every pipeline here — both windings must apply the op
        return ds;
    }

    /// <summary>The TESTED-draw state: depth off, stencil EQUAL against the ref the device programs with
    /// <c>OMSetStencilRef</c> (= the live scope nesting depth), all ops KEEP, write mask 0.</summary>
    public static D3D12_DEPTH_STENCIL_DESC EqualTest()
    {
        D3D12_DEPTH_STENCIL_DESC ds = default;
        ds.DepthEnable = BOOL.FALSE;
        ds.DepthWriteMask = D3D12_DEPTH_WRITE_MASK.D3D12_DEPTH_WRITE_MASK_ZERO;
        ds.StencilEnable = BOOL.TRUE;
        ds.StencilReadMask = 0xFF;
        ds.StencilWriteMask = 0;
        ds.FrontFace.StencilFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_EQUAL;
        ds.FrontFace.StencilPassOp = D3D12_STENCIL_OP.D3D12_STENCIL_OP_KEEP;
        ds.FrontFace.StencilFailOp = D3D12_STENCIL_OP.D3D12_STENCIL_OP_KEEP;
        ds.FrontFace.StencilDepthFailOp = D3D12_STENCIL_OP.D3D12_STENCIL_OP_KEEP;
        ds.BackFace = ds.FrontFace;
        return ds;
    }

    /// <summary>
    /// Build the EQUAL-tested clone of a blended UNIT-QUAD pipeline — the shape RoundRect / Image / Gradient / both
    /// glyph passes all share byte-for-byte (one <c>POSITION</c> float2 element, TRIANGLE topology type, BGRA8_UNORM
    /// RT0, single sample, solid/no-cull, premultiplied source-over, depth off). Shaders come back from
    /// <see cref="ShaderCompiler"/>'s content-hashed DXBC disk cache, so on a warm machine this is a file read, not a
    /// D3DCompile.
    /// <para>Returns <c>null</c> on ANY failure. That is a supported outcome, not an error path: the device then draws
    /// that pipeline's runs unmasked-but-scissored inside the scope and counts them on
    /// <c>Diag "d3d12"/"stencilFallback"</c> — the same honest degradation the uncovered pipelines already take.</para>
    /// </summary>
    public static ID3D12PipelineState* TryBuildQuadEqualTest(ID3D12Device* device, ID3D12RootSignature* root,
        string hlsl, string vsEntry, string psEntry, string label, bool depthClip)
    {
        if (device == null || root == null) return null;
        ID3DBlob* vs = null, ps = null;
        try
        {
            vs = ShaderCompiler.Compile(hlsl, vsEntry, "vs_5_1", label);
            ps = ShaderCompiler.Compile(hlsl, psEntry, "ps_5_1", label);
            byte[] semantic = Encoding.ASCII.GetBytes("POSITION\0");
            fixed (byte* sem = semantic)
            {
                D3D12_INPUT_ELEMENT_DESC elem = default;
                elem.SemanticName = (sbyte*)sem;
                elem.Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT;
                elem.InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA;

                D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
                pd.pRootSignature = root;
                pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
                pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
                pd.InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = &elem, NumElements = 1 };
                pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
                pd.NumRenderTargets = 1;
                pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
                pd.DSVFormat = DsvFormat;
                pd.SampleDesc.Count = 1;
                pd.SampleMask = uint.MaxValue;
                pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
                pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
                pd.RasterizerState.DepthClipEnable = depthClip ? BOOL.TRUE : BOOL.FALSE;
                pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
                pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;   // premultiplied source-over
                pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
                pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
                pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
                pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
                pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
                pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
                pd.DepthStencilState = EqualTest();

                ID3D12PipelineState* pso;
                if ((int)device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso) < 0) return null;
                return pso;
            }
        }
        catch { return null; }
        finally
        {
            if (vs != null) vs->Release();
            if (ps != null) ps->Release();
        }
    }
}
