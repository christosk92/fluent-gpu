using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

using ColorF = FluentGpu.Foundation.ColorF;
using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// The composite pipelines of the retained tiled content layer (docs/plans/scroll-gpu-retained-tiles-implementation.md
/// §A.5): one root signature (56 root constants + one SRV table + point/linear clamp samplers — 57 of the 64 root DWORDs)
/// and the PSOs every composite pass draws with — the whole-pixel tile/region placement (an exact texel <c>Load</c>, times
/// group alpha, up to TWO analytic <see cref="EdgeFeatherMask"/> feathers (an item's own and a distributed ancestor fade's
/// — the exact product, gpu-renderer.md §13.1e) and an sdRoundRect clip), a bilinear placement for scaled surfaces, the
/// background / DestOut erase fills, the separable Gaussian + 2× downsample of self-blur, the dual-Kawase chain + the
/// AcrylicBrush recipe of in-app acrylic. HLSL: <c>composite.hlsl</c> (an embedded resource) with
/// <see cref="EdgeFeatherMask.Hlsl"/> prepended — one feather source for the GPU and the C# reference. Render-thread-owned.
/// Every draw is a 4-vertex triangle strip generated from <c>SV_VertexID</c> (no vertex buffer).
/// </summary>
internal sealed unsafe class SliceCompositor : IDisposable
{
    public enum Pso : byte { Load, LoadCopy, Sample, SampleCopy, Fill, FillCopy, Erase, Blur, Down2, KawaseDown, KawaseUp, Acrylic, LoadScreen, SampleScreen, Feedback, Count }

    /// <summary>The F6 feedback pass, appended to composite.hlsl: the previous trail sampled through the INVERSE warp
    /// (K[2], K[3] = affine rows mapping destination px → source px), multiplied by the keep fraction toward the fade colour
    /// (K[4].z, K[5]), minus a 4×4 ordered dither of K[4].w (1/255) so 8-bit residue dies instead of parking at one step.
    /// Outside the source it reads transparent (no clamp smear).</summary>
    private const string FeedbackHlsl = """
static const float gBayer4[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
float4 PSFeedback(V i) : SV_Target
{
    float2 p = i.pos.xy - K[0].xy;
    float2 s = float2(dot(K[2].xy, p) + K[2].z, dot(K[3].xy, p) + K[3].z);
    float2 ext = float2(K[2].w, K[3].w);   // the trail's extent in px; the pooled ping may be larger (K[4].xy = 1 / its size)
    bool outside = s.x < 0.0 || s.y < 0.0 || s.x > ext.x || s.y > ext.y;
    float2 uv = clamp(s, 0.5, ext - 0.5) * K[4].xy;   // never filter in a texel past the trail
    float4 c = outside ? float4(0, 0, 0, 0) : gSrc.SampleLevel(gLinear, uv, 0);
    c = lerp(K[5], c, K[4].z);
    int2 q = int2(i.pos.xy) & 3;
    float d = (gBayer4[q.y * 4 + q.x] + 0.5) / 16.0 * K[4].w;
    return saturate(c - d);
}
""";

    public const int ConstantCount = 56;

    private ID3D12RootSignature* _root;
    private readonly ID3D12PipelineState*[] _pso = new ID3D12PipelineState*[(int)Pso.Count];
    private Pso _bound = Pso.Count;
    private int _targetW, _targetH;

    /// <summary>The root constants of the next draw (K[0..13] of composite.hlsl).</summary>
    public readonly float[] K = new float[ConstantCount];

    public void Init(ID3D12Device* device)
    {
        _root = BuildRootSignature(device);
        string src = EdgeFeatherMask.Hlsl + "\n" + LoadHlsl() + "\n" + FeedbackHlsl;
        ID3DBlob* vs = ShaderCompiler.Compile(src, "VSQuad", "vs_5_1", "composite");
        ID3DBlob* load = ShaderCompiler.Compile(src, "PSLoad", "ps_5_1", "composite");
        ID3DBlob* sample = ShaderCompiler.Compile(src, "PSSample", "ps_5_1", "composite");
        ID3DBlob* fill = ShaderCompiler.Compile(src, "PSFill", "ps_5_1", "composite");
        ID3DBlob* blur = ShaderCompiler.Compile(src, "PSBlur", "ps_5_1", "composite");
        ID3DBlob* down = ShaderCompiler.Compile(src, "PSDown2", "ps_5_1", "composite");
        ID3DBlob* kd = ShaderCompiler.Compile(src, "PSKawaseDown", "ps_5_1", "composite");
        ID3DBlob* ku = ShaderCompiler.Compile(src, "PSKawaseUp", "ps_5_1", "composite");
        ID3DBlob* acr = ShaderCompiler.Compile(src, "PSAcrylic", "ps_5_1", "composite");
        ID3DBlob* fb = ShaderCompiler.Compile(src, "PSFeedback", "ps_5_1", "composite");
        _pso[(int)Pso.Load] = MakePso(device, vs, load, Blend.Over);
        _pso[(int)Pso.LoadCopy] = MakePso(device, vs, load, Blend.Copy);
        _pso[(int)Pso.Sample] = MakePso(device, vs, sample, Blend.Over);
        _pso[(int)Pso.SampleCopy] = MakePso(device, vs, sample, Blend.Copy);
        _pso[(int)Pso.LoadScreen] = MakePso(device, vs, load, Blend.Screen);
        _pso[(int)Pso.SampleScreen] = MakePso(device, vs, sample, Blend.Screen);
        _pso[(int)Pso.Fill] = MakePso(device, vs, fill, Blend.Over);
        _pso[(int)Pso.FillCopy] = MakePso(device, vs, fill, Blend.Copy);
        _pso[(int)Pso.Erase] = MakePso(device, vs, fill, Blend.DestOut);
        _pso[(int)Pso.Blur] = MakePso(device, vs, blur, Blend.Copy);
        _pso[(int)Pso.Down2] = MakePso(device, vs, down, Blend.Copy);
        _pso[(int)Pso.KawaseDown] = MakePso(device, vs, kd, Blend.Copy);
        _pso[(int)Pso.KawaseUp] = MakePso(device, vs, ku, Blend.Copy);
        _pso[(int)Pso.Acrylic] = MakePso(device, vs, acr, Blend.Over);
        _pso[(int)Pso.Feedback] = MakePso(device, vs, fb, Blend.Copy);
        vs->Release(); load->Release(); sample->Release(); fill->Release(); blur->Release(); down->Release();
        kd->Release(); ku->Release(); acr->Release(); fb->Release();
    }

    private static string LoadHlsl()
    {
        using var s = typeof(SliceCompositor).Assembly.GetManifestResourceStream("FluentGpu.Windows.D3D12.composite.hlsl")
            ?? throw new InvalidOperationException("composite.hlsl is not embedded in FluentGpu.Windows.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Bind the compositor for drawing into a <paramref name="targetW"/>×<paramref name="targetH"/> px target that
    /// is already bound (a render pass is open on it): root signature, the SRV heap, the full viewport, the topology.
    /// Every draw recorded by another pipeline since invalidates this — call again before the next composite draw.</summary>
    public void Bind(ID3D12GraphicsCommandList* cmd, ID3D12DescriptorHeap* heap, int targetW, int targetH)
    {
        cmd->SetGraphicsRootSignature(_root);
        cmd->SetDescriptorHeaps(1, &heap);
        D3D12_VIEWPORT vp = new() { TopLeftX = 0, TopLeftY = 0, Width = targetW, Height = targetH, MinDepth = 0, MaxDepth = 1 };
        cmd->RSSetViewports(1, &vp);
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        _bound = Pso.Count;
        _targetW = targetW; _targetH = targetH;
    }

    public int TargetW => _targetW;
    public int TargetH => _targetH;

    /// <summary>Set the scissor (target px, clamped to the target).</summary>
    public void Scissor(ID3D12GraphicsCommandList* cmd, int l, int t, int r, int b)
    {
        RECT sc = new()
        {
            left = Math.Clamp(l, 0, _targetW), top = Math.Clamp(t, 0, _targetH),
            right = Math.Clamp(r, 0, _targetW), bottom = Math.Clamp(b, 0, _targetH),
        };
        if (sc.right < sc.left) sc.right = sc.left;
        if (sc.bottom < sc.top) sc.bottom = sc.top;
        cmd->RSSetScissorRects(1, &sc);
    }

    /// <summary>Reset K to the neutral quad: destination = (<paramref name="x0"/>, <paramref name="y0"/>) –
    /// (<paramref name="x1"/>, <paramref name="y1"/>) target px, alpha 1, no feather, no rounded clip.</summary>
    public void Begin(float x0, float y0, float x1, float y1)
    {
        Array.Clear(K);
        K[0] = x0; K[1] = y0; K[2] = x1; K[3] = y1;
        K[4] = _targetW; K[5] = _targetH;
        K[12] = 1f;   // alpha
    }

    public void SourceOrigin(float sx, float sy) { K[6] = sx; K[7] = sy; }
    public void SampleMap(float x0, float y0, float sx, float sy) { K[8] = x0; K[9] = y0; K[10] = sx; K[11] = sy; }
    /// <summary>PSSample's far-edge clamp: uv never passes (<paramref name="maxU"/>, <paramref name="maxV"/>), the centre of
    /// the last texel written into a pooled (larger) surface. Shares K[9] with <see cref="Color"/>, which PSSample never reads.</summary>
    public void SampleClamp(float maxU, float maxV) { K[36] = maxU; K[37] = maxV; K[38] = 1f; K[39] = 0f; }
    public void Alpha(float a) => K[12] = a;

    public void RoundClip(in RectF rectPx, float radiusPx)
    {
        if (rectPx.W <= 0f || rectPx.H <= 0f) return;
        K[14] = radiusPx; K[15] = 1f;
        K[16] = rectPx.X; K[17] = rectPx.Y; K[18] = rectPx.Right; K[19] = rectPx.Bottom;
    }

    /// <summary>A rounded clip with a radius PER CORNER (top-left, top-right, bottom-right, bottom-left), for the video-hole
    /// erase (F078): the hole's own rounded rect, not the uniform <see cref="RoundClip"/>. It rides the feather's constants
    /// (K[5], K[6]) - selected by a NEGATIVE feather flag, K[3].y = -1 - so it is exclusive with <see cref="Feather"/>, which an
    /// erase never uses. Combines with <see cref="RoundClip"/> (a separate flag and rect).</summary>
    public void CornerClip(in RectF rectPx, in CornerRadius4 radiiPx)
    {
        if (rectPx.W <= 0f || rectPx.H <= 0f) return;
        K[13] = -1f;
        K[20] = rectPx.X; K[21] = rectPx.Y; K[22] = rectPx.Right; K[23] = rectPx.Bottom;
        K[24] = radiiPx.TopLeft; K[25] = radiiPx.TopRight; K[26] = radiiPx.BottomRight; K[27] = radiiPx.BottomLeft;
    }

    /// <summary>The analytic edge feather (target px — the caller translates it into the target's space).</summary>
    public void Feather(in EdgeFeather f)
    {
        if (f.IsNone) return;
        K[13] = 1f;
        EdgeFeatherMask.Pack(in f, K.AsSpan(20, EdgeFeatherMask.PackedFloats));
    }

    /// <summary>The SECOND analytic edge feather, multiplied with <see cref="Feather"/> (K[10..13] of composite.hlsl; its
    /// intensity 0 — the neutral quad — disables it).</summary>
    public void Feather2(in EdgeFeather f)
    {
        if (f.IsNone) return;
        EdgeFeatherMask.Pack(in f, K.AsSpan(40, EdgeFeatherMask.PackedFloats));
    }

    public void Color(in ColorF premultiplied) { K[36] = premultiplied.R; K[37] = premultiplied.G; K[38] = premultiplied.B; K[39] = premultiplied.A; }

    /// <summary>Record one quad with the current K and <paramref name="pso"/>, sampling <paramref name="srv"/> (ptr 0 = none).</summary>
    public void Draw(ID3D12GraphicsCommandList* cmd, Pso pso, D3D12_GPU_DESCRIPTOR_HANDLE srv)
    {
        if (_bound != pso) { cmd->SetPipelineState(_pso[(int)pso]); _bound = pso; }
        fixed (float* k = K) cmd->SetGraphicsRoot32BitConstants(0, ConstantCount, k, 0);
        if (srv.ptr != 0) cmd->SetGraphicsRootDescriptorTable(1, srv);
        cmd->DrawInstanced(4, 1, 0, 0);
        GpuDrawCount.Frame++;
    }

    private enum Blend : byte { Over, Copy, DestOut, Screen }

    private ID3D12RootSignature* BuildRootSignature(ID3D12Device* device)
    {
        D3D12_DESCRIPTOR_RANGE range = default;
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1;
        range.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;

        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[2];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.Num32BitValues = ConstantCount;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
        p[1].Anonymous.DescriptorTable.pDescriptorRanges = &range;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC* samp = stackalloc D3D12_STATIC_SAMPLER_DESC[2];
        for (int i = 0; i < 2; i++)
        {
            samp[i] = default;
            samp[i].Filter = i == 0 ? D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_POINT : D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR;
            samp[i].AddressU = samp[i].AddressV = samp[i].AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            samp[i].ShaderRegister = (uint)i;
            samp[i].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;
            samp[i].MaxLOD = float.MaxValue;
        }

        D3D12_ROOT_SIGNATURE_DESC desc = default;
        desc.NumParameters = 2;
        desc.pParameters = p;
        desc.NumStaticSamplers = 2;
        desc.pStaticSamplers = samp;
        desc.Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_NONE;

        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "Composite.SerializeRootSig");
        ID3D12RootSignature* rs;
        Check(device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "Composite.CreateRootSig");
        sig->Release();
        if (err != null) err->Release();
        return rs;
    }

    private ID3D12PipelineState* MakePso(ID3D12Device* device, ID3DBlob* vs, ID3DBlob* ps, Blend blend)
    {
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
        pd.pRootSignature = _root;
        pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
        pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
        pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pd.NumRenderTargets = 1;
        pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        pd.SampleDesc.Count = 1;
        pd.SampleMask = uint.MaxValue;
        pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
        pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
        pd.RasterizerState.DepthClipEnable = BOOL.TRUE;
        pd.DepthStencilState.DepthEnable = BOOL.FALSE;
        pd.DepthStencilState.StencilEnable = BOOL.FALSE;
        ref var rt = ref pd.BlendState.RenderTarget[0];
        rt.RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        switch (blend)
        {
            case Blend.Over:
                rt.BlendEnable = BOOL.TRUE;
                rt.SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE; rt.DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA; rt.BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
                rt.SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE; rt.DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA; rt.BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
                break;
            case Blend.Screen:
                // 1 − (1 − s)(1 − d) on premultiplied colour = s + d·(1 − s): ONE / INV_SRC_COLOR; alpha stays source-over.
                rt.BlendEnable = BOOL.TRUE;
                rt.SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE; rt.DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_COLOR; rt.BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
                rt.SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE; rt.DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA; rt.BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
                break;
            case Blend.DestOut:
                rt.BlendEnable = BOOL.TRUE;
                rt.SrcBlend = D3D12_BLEND.D3D12_BLEND_ZERO; rt.DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA; rt.BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
                rt.SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ZERO; rt.DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA; rt.BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
                break;
            default:
                rt.BlendEnable = BOOL.FALSE;
                break;
        }
        ID3D12PipelineState* pso;
        Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Composite.Pso");
        return pso;
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    public void Dispose()
    {
        for (int i = 0; i < _pso.Length; i++) if (_pso[i] != null) { _pso[i]->Release(); _pso[i] = null; }
        if (_root != null) { _root->Release(); _root = null; }
    }
}
