using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>CPU mirror of the HLSL gradient <c>Inst</c> (192-byte stride; each float4 on a 16-byte boundary).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GradientInstance
{
    public float PosX, PosY, W, H;                 // 0
    public float StartX, StartY, EndX, EndY;       // 16 : gradient axis in local 0..1
    public float C0R, C0G, C0B, C0A;               // 32
    public float C1R, C1G, C1B, C1A;               // 48
    public float C2R, C2G, C2B, C2A;               // 64
    public float C3R, C3G, C3B, C3A;               // 80
    public float O0, O1, O2, O3;                   // 96 : stop offsets
    public float M11, M12, M21, M22;               // 112: world transform (linear)
    public float Dx, Dy, Radius, Opacity;          // 128: translation + corner radius + opacity
    public float Shape, StopCount, Stroke, Pad1;   // 144: 0=linear 1=radial, stop count, stroke width (0=fill, >0=border band)
    public float ClipX, ClipY, ClipW, ClipH;        // 160: tier-2 rounded clip
    public float ClipR, Pad2, Pad3, Pad4;           // 176: ClipW <= 0 = none
}

/// <summary>
/// Linear/radial gradient fill pipeline — a real multi-stop gradient (≤4 stops) clipped by the same analytic rounded-box
/// SDF as the solid fill. One instanced quad per gradient rect; same blend/AA posture as <see cref="RoundRectPipeline"/>.
/// </summary>
internal sealed unsafe class GradientPipeline : IDisposable
{
    private const int MaxInstances = 512;   // per-FRAME policy cap (not a memory reservation — see _arena)

    private SdfSharedResources _shared = null!;
    private ID3D12PipelineState* _pso;
    // Tier-3 stencil path clip (gpu-renderer.md S6): the EQUAL-tested clone, built lazily on the first stencil scope.
    private ID3D12PipelineState* _psoStencilTest;
    private bool _stencilTried;
    private ID3D12Device* _device;   // non-owning; the device outlives every pipeline
    // Instance storage is the device's SHARED per-frame UploadArena (one persistently-mapped UPLOAD buffer per
    // frame-in-flight, bump-allocated by every pipeline) — not a private worst-case ring. MaxInstances survives as
    // this pipeline's per-frame POLICY cap (unchanged drop semantics), no longer as unconditionally resident memory.
    private UploadArena _arena = null!;
    private int _cursor;
    private int _dropped;

    public int DroppedInstances => _dropped;

    private const string Hlsl = """
struct Inst { float2 pos; float2 size; float2 gstart; float2 gend; float4 c0; float4 c1; float4 c2; float4 c3; float4 offs; float4 m; float2 t; float radius; float opacity; float shape; float stopCount; float stroke; float pad; float4 clip; float clipR; float3 pad2; };
StructuredBuffer<Inst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut { float4 pos : SV_Position; float2 local : TEXCOORD0; float2 halfSize : TEXCOORD1; float radius : TEXCOORD2; float opacity : TEXCOORD3; float2 gstart : TEXCOORD4; float2 gend : TEXCOORD5; float2 shapeCount : TEXCOORD6; float4 c0 : TEXCOORD7; float4 c1 : TEXCOORD8; float4 c2 : TEXCOORD9; float4 c3 : TEXCOORD10; float4 offs : TEXCOORD11; float stroke : TEXCOORD12; float4 clip : TEXCOORD13; float clipR : TEXCOORD14; float2 world : TEXCOORD15; };

VSOut VSMain(float2 corner : POSITION, uint iid : SV_InstanceID)
{
    Inst it = gInst[iid];
    // Inflate the quad so the full coverage footprint (fill edge AA, or an outline band's outer half + AA) is rasterized
    // rather than clipped by the rect quad — otherwise rounded corners / pill ends read rough. See RoundRectPipeline.
    float margin = (it.stroke > 0.0 ? it.stroke * 0.5 : 0.0) + 2.0;
    float2 dir = corner * 2.0 - 1.0;
    float2 lp = it.pos + corner * it.size + dir * margin;
    float2 world = float2(it.m.x * lp.x + it.m.z * lp.y + it.t.x, it.m.y * lp.x + it.m.w * lp.y + it.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.local = corner * it.size - it.size * 0.5 + dir * margin;
    o.halfSize = it.size * 0.5;
    o.radius = it.radius;
    o.opacity = it.opacity;
    o.gstart = it.gstart; o.gend = it.gend;
    o.shapeCount = float2(it.shape, it.stopCount);
    o.c0 = it.c0; o.c1 = it.c1; o.c2 = it.c2; o.c3 = it.c3; o.offs = it.offs;
    o.stroke = it.stroke;
    o.clip = it.clip; o.clipR = it.clipR; o.world = world;
    return o;
}

float seg(float a, float b, float t) { return saturate((t - a) / max(b - a, 1e-5)); }

float4 PSMain(VSOut i) : SV_Target
{
    float2 q = abs(i.local) - (i.halfSize - i.radius);
    float d = min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - i.radius;
    float fw = max(fwidth(d), 1e-4);
    float cov;
    if (i.stroke > 0.0)
        cov = clamp(0.5 - (abs(d) - i.stroke * 0.5) / fw, 0.0, 1.0);   // border: a band of width 'stroke' centred on the edge
    else
        cov = clamp(0.5 - d / fw, 0.0, 1.0);                            // fill

    if (i.clip.z > 0.0)
    {
        float2 cp = i.world - (i.clip.xy + i.clip.zw * 0.5);
        float cr = min(i.clipR, min(i.clip.z, i.clip.w) * 0.5);
        float2 cq = abs(cp) - (i.clip.zw * 0.5 - cr);
        float cd = min(max(cq.x, cq.y), 0.0) + length(max(cq, 0.0)) - cr;
        cov *= clamp(0.5 - cd / max(fwidth(cd), 1e-4), 0.0, 1.0);
    }

    float2 uv = i.local / (i.halfSize * 2.0) + 0.5;
    float t;
    if (i.shapeCount.x < 0.5) { float2 dir = i.gend - i.gstart; t = saturate(dot(uv - i.gstart, dir) / max(dot(dir, dir), 1e-5)); }
    else { float2 rc = i.gstart; float2 rr = i.gend - i.gstart; t = saturate(length((uv - rc) / max(rr, float2(1e-5, 1e-5)))); }   // radial: origin gstart, per-axis radius gend-gstart (default .5,.5→.5,.5 = old centre-to-edge)

    float4 col = i.c0;
    if (i.shapeCount.y >= 1.5) col = lerp(col, i.c1, seg(i.offs.x, i.offs.y, t));
    if (i.shapeCount.y >= 2.5) col = lerp(col, i.c2, seg(i.offs.y, i.offs.z, t));
    if (i.shapeCount.y >= 3.5) col = lerp(col, i.c3, seg(i.offs.z, i.offs.w, t));

    float aOut = col.a * cov * i.opacity;
    return float4(col.rgb * aOut, aOut);
}
""";

    public void Init(ID3D12Device* device, SdfSharedResources shared, UploadArena arena)
    {
        _shared = shared;
        _arena = arena;
        _device = device;
        BuildPipeline(device);
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    private static ID3DBlob* Compile(string entry, string target)
        => ShaderCompiler.Compile(Hlsl, entry, target, "gradient");

    private void BuildPipeline(ID3D12Device* device)
    {
        ID3DBlob* vs = Compile("VSMain", "vs_5_1");
        ID3DBlob* ps = Compile("PSMain", "ps_5_1");
        byte[] semantic = Encoding.ASCII.GetBytes("POSITION\0");
        fixed (byte* sem = semantic)
        {
            D3D12_INPUT_ELEMENT_DESC elem = default;
            elem.SemanticName = (sbyte*)sem;
            elem.Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT;
            elem.InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA;

            D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
            pd.pRootSignature = _shared.RootSignature;
            pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
            pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
            pd.InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = &elem, NumElements = 1 };
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
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Gradient.CreateGraphicsPipelineState");
            _pso = pso;
        }
        vs->Release();
        ps->Release();
    }

    /// <summary>Reset this frame's policy cap + drop counter. Bank selection — and the fence discipline that makes
    /// writing that bank safe — belongs to the shared <see cref="UploadArena"/>, begun once per frame by the device.</summary>
    public void BeginFrame(int frameIndex) { _ = frameIndex; _cursor = 0; _dropped = 0; }

    /// <summary>Record one run; shared SDF state and this pipeline's PSO can be rebound independently. Returns false
    /// when full (state untouched).</summary>
    public bool Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<GradientInstance> instances, float vpW, float vpH,
                       bool bindSharedState = true, bool bindPipelineState = true, bool stencilTest = false)
    {
        int count = Math.Min(instances.Length, MaxInstances - _cursor);
        if (count <= 0) { _dropped += instances.Length; return false; }
        // Arena full: record NOTHING (command-list state untouched, exactly like the over-cap path above). The
        // arena folded the demand into its growth target and the device arms one more full repaint, so the
        // dropped run returns within a bank-depth of frames.
        if (!_arena.TryReserve(count * sizeof(GradientInstance), out byte* dst, out ulong gva))
        { _dropped += instances.Length; return false; }
        _dropped += instances.Length - count;
        GradientInstance* slot = (GradientInstance*)dst;
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
        // See RoundRectPipeline.Record: the stencil clone is bound unconditionally, never through the _boundPipe skip.
        ID3D12PipelineState* want = stencilTest ? StencilTestPso() : null;
        if (want != null) cmd->SetPipelineState(want);
        else if (bindPipelineState) cmd->SetPipelineState(_pso);
        cmd->SetGraphicsRootShaderResourceView(1, gva);
        cmd->DrawInstanced(4, (uint)count, 0, 0);
        return true;
    }

    private ID3D12PipelineState* StencilTestPso()
    {
        if (!_stencilTried)
        {
            _stencilTried = true;
            _psoStencilTest = StencilPso.TryBuildQuadEqualTest(_device, _shared.RootSignature, Hlsl, "VSMain", "PSMain", "gradient", depthClip: true);
        }
        return _psoStencilTest;
    }

    public void Dispose()
    {
        // No instance buffers to release: the shared UploadArena owns them (disposed by the device).
        if (_pso != null) _pso->Release();
        if (_psoStencilTest != null) { _psoStencilTest->Release(); _psoStencilTest = null; }
    }
}
