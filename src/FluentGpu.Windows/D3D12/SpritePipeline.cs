using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Instance record for ONE sprite of a <see cref="DrawSpritesCmd"/> chunk — 16 floats (64 B), laid out exactly as
/// the HLSL <c>Inst</c> below. The decoder unpacks a chunk's ≤ 16 sprites into these; the chunk's transform and opacity
/// ride every instance (a chunk is small, so the duplication is a few hundred bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SpriteInstance
{
    public float X, Y, W, H;              // the Sprite geometry (node-local DIP)
    public float Rot, Soft, Kernel, Pad;
    public float M11, M12, M21, M22;      // the chunk transform
    public float Dx, Dy, RgbaBits, Opacity;   // RgbaBits = the packed 0xRRGGBBAA word reinterpreted as a float (asuint in HLSL)

    public static SpriteInstance From(in Sprite s, in DrawSpritesCmd c) => new()
    {
        X = s.X, Y = s.Y, W = s.W, H = s.H, Rot = s.Rot, Soft = s.Soft, Kernel = c.Kernel, Pad = 0f,
        M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
        Dx = c.Transform.Dx, Dy = c.Transform.Dy, RgbaBits = BitConverter.UInt32BitsToSingle(s.Rgba), Opacity = c.Opacity,
    };
}

/// <summary>The sprite lane (visualizer F5): one <c>DrawInstanced(4, sprites)</c> triangle-strip pass whose vertex shader
/// builds each sprite's rotated, AA-padded quad from <c>SV_VertexID</c> and whose pixel shader evaluates the kernel's SDF
/// (ellipse / capsule / streak / segment) with analytic <c>fwidth</c> coverage and the optional radial <c>Soft</c> falloff.
/// Rides the shared SDF root signature (viewport constants b0, instance SRV t0, VS-visible only — every PS input is a
/// <c>nointerpolation</c> VS output, exactly like <see cref="SeriesPipeline"/>) and the shared TRIANGLESTRIP topology; the
/// PSO declares no input layout. Premultiplied output; a SrcOver PSO and an Additive clone (DrawOp.SetBlend).</summary>
internal sealed unsafe class SpritePipeline : IDisposable
{
    private const int MaxInstances = 4096;   // per-FRAME policy cap; drops are counted (Diag "sprites"/"dropped")

    private SdfSharedResources _shared = null!;
    private ID3D12PipelineState* _pso;
    private ID3D12PipelineState* _psoAdd;
    private UploadArena _arena = null!;
    private int _cursor;
    private int _dropped;

    public int DroppedInstances => _dropped;

    private const string Hlsl = """
struct Inst {
    float4 geo;     // x, y, w, h
    float4 prm;     // rot, soft, kernel, pad
    float4 m;       // m11, m12, m21, m22
    float4 tc;      // dx, dy, rgba bits, opacity
};
StructuredBuffer<Inst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut
{
    float4 pos : SV_Position;
    float2 local : TEXCOORD0;                      // sprite-local DIP (the kernel frame: x along the bar / segment)
    nointerpolation float4 shape : TEXCOORD1;      // x = kernel, y = capsule half-axis (a), z = half width / radius (h), w = soft
    nointerpolation float2 radii : TEXCOORD2;      // the disc radii
    nointerpolation float4 color : TEXCOORD3;      // straight alpha
    nointerpolation float opacity : TEXCOORD4;
};

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Inst it = gInst[iid];
    float kernel = it.prm.z;
    const float pad = 1.0;                         // one DIP of AA room on every side of the kernel
    float ang = it.prm.x;
    float2 c, he, radii = float2(0.0, 0.0);
    float a = 0.0, h = 0.0;
    if (kernel < 0.5)
    {
        c = it.geo.xy; radii = abs(it.geo.zw); he = radii + pad; h = min(radii.x, radii.y);
    }
    else if (kernel < 2.5)
    {
        c = it.geo.xy;
        float halfLen = abs(it.geo.z) * 0.5; h = abs(it.geo.w) * 0.5; a = max(halfLen - h, 0.0);
        he = float2(a + h, h) + pad;
    }
    else
    {
        float2 p0 = it.geo.xy, p1 = it.geo.zw, d = p1 - p0;
        float L = length(d);
        c = (p0 + p1) * 0.5; ang = L > 1e-5 ? atan2(d.y, d.x) : 0.0;
        h = abs(it.prm.x) * 0.5; a = L * 0.5;
        he = float2(a + h, h) + pad;
    }
    float2 corner = float2((vid & 1) != 0 ? 1.0 : -1.0, (vid & 2) != 0 ? 1.0 : -1.0);
    float2 lp = corner * he;
    float cs = cos(ang), sn = sin(ang);
    float2 p = c + float2(lp.x * cs - lp.y * sn, lp.x * sn + lp.y * cs);
    float2 world = float2(it.m.x * p.x + it.m.z * p.y + it.tc.x, it.m.y * p.x + it.m.w * p.y + it.tc.y);
    VSOut o;
    o.pos = float4(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0, 0.0, 1.0);
    o.local = lp;
    o.shape = float4(kernel, a, h, it.prm.y);
    o.radii = radii;
    uint u = asuint(it.tc.z);
    o.color = float4((u >> 24) & 255u, (u >> 16) & 255u, (u >> 8) & 255u, u & 255u) / 255.0;
    o.opacity = it.tc.w;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float kernel = i.shape.x;
    float d, n;
    if (kernel < 0.5)
    {
        float2 r = max(i.radii, 1e-3);
        float q = length(i.local / r);
        d = (q - 1.0) * min(r.x, r.y);           // an ellipse SDF approximation, exact for a circle
        n = q;
    }
    else
    {
        float a = i.shape.y, h = max(i.shape.z, 1e-3);
        float2 qv = float2(i.local.x - clamp(i.local.x, -a, a), i.local.y);
        float dist = length(qv);
        d = dist - h;
        n = dist / h;
    }
    float cov = saturate(0.5 - d / max(fwidth(d), 1e-4));
    float glow = saturate(1.0 - n); glow *= glow;
    cov *= lerp(1.0, glow, saturate(i.shape.w));
    if (kernel >= 1.5 && kernel < 2.5)
    {
        float total = 2.0 * (i.shape.y + i.shape.z);
        cov *= saturate((i.local.x + total * 0.5) / max(total, 1e-3));   // the streak: tail (−W/2) transparent, head opaque
    }
    float alpha = i.color.a * cov * i.opacity;
    return float4(i.color.rgb * alpha, alpha);
}
""";

    public void Init(ID3D12Device* device, SdfSharedResources shared, UploadArena arena)
    {
        _shared = shared;
        _arena = arena;
        BuildPipelines(device);
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    private void BuildPipelines(ID3D12Device* device)
    {
        ID3DBlob* vs = ShaderCompiler.Compile(Hlsl, "VSMain", "vs_5_1", "sprites");
        ID3DBlob* ps = ShaderCompiler.Compile(Hlsl, "PSMain", "ps_5_1", "sprites");
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
        pd.pRootSignature = _shared.RootSignature;
        pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
        pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
        pd.InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = null, NumElements = 0 };   // SV_VertexID only
        pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pd.NumRenderTargets = 1;
        pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        pd.SampleDesc.Count = 1;
        pd.SampleMask = uint.MaxValue;
        pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
        pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
        pd.RasterizerState.DepthClipEnable = BOOL.TRUE;
        pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
        pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        pd.DepthStencilState.DepthEnable = BOOL.FALSE;
        pd.DepthStencilState.StencilEnable = BOOL.FALSE;
        ID3D12PipelineState* pso;
        Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Sprites.CreateGraphicsPipelineState");
        _pso = pso;
        // The ADDITIVE clone (DrawOp.SetBlend): colour ONE/ONE, alpha ZERO/ONE.
        pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ZERO;
        pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
        ID3D12PipelineState* psoAdd;
        Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&psoAdd), "Sprites.CreateGraphicsPipelineState(Additive)");
        _psoAdd = psoAdd;
        vs->Release();
        ps->Release();
    }

    /// <summary>Reset this frame's policy cap + drop counter (the shared <see cref="UploadArena"/> owns the memory).</summary>
    public void BeginFrame(int slot) { _ = slot; _cursor = 0; _dropped = 0; }

    /// <summary>Record one run (the Series contract: shared state and the PSO rebind independently; false when full,
    /// state untouched).</summary>
    public bool Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<SpriteInstance> instances, float vpW, float vpH,
                       bool bindSharedState = true, bool bindPipelineState = true, bool additive = false)
    {
        int count = Math.Min(instances.Length, MaxInstances - _cursor);
        if (count <= 0) { _dropped += instances.Length; return false; }
        if (!_arena.TryReserve(count * sizeof(SpriteInstance), out byte* dst, out ulong gva))
        { _dropped += instances.Length; return false; }
        _dropped += instances.Length - count;
        SpriteInstance* slot = (SpriteInstance*)dst;
        for (int i = 0; i < count; i++) slot[i] = instances[i];
        _cursor += count;
        if (bindSharedState)
        {
            cmd->SetGraphicsRootSignature(_shared.RootSignature);
            _shared.SetViewportConstants(cmd, vpW, vpH);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            var qv = _shared.QuadView;
            cmd->IASetVertexBuffers(0, 1, &qv);
        }
        if (bindPipelineState) cmd->SetPipelineState(additive ? _psoAdd : _pso);
        cmd->SetGraphicsRootShaderResourceView(1, gva);
        cmd->DrawInstanced(4, (uint)count, 0, 0); GpuDrawCount.Frame++;
        return true;
    }

    public void Dispose()
    {
        if (_pso != null) { _pso->Release(); _pso = null; }
        if (_psoAdd != null) { _psoAdd->Release(); _psoAdd = null; }
    }
}
