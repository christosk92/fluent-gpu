using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>CPU mirror of the HLSL arc <c>Inst</c> (laid out so each float4 sits on a 16-byte boundary → 80-byte stride).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ArcInstance
{
    public float PosX, PosY, W, H;                 // 0..15  : box rect (local)
    public float R, G, B, A;                       // 16..31 : stroke color (un-premultiplied; PS premultiplies)
    public float M11, M12, M21, M22;               // 32..47 : world transform (linear part)
    public float Dx, Dy, Thickness, Opacity;       // 48..63 : translation + stroke thickness + cumulative opacity
    public float StartRad, SweepRad, RoundCaps, _pad; // 64..79 : arc start/sweep (radians) + round-cap flag (0/1) + pad
}

/// <summary>
/// SDF circular-arc stroke pipeline (ProgressRing). One instanced quad per arc, expanded over the local rect (the ring
/// + round caps are bounded by R + thickness/2 ≤ the box). The PS evaluates a ring SDF, trims it to the swept angular
/// band, and unions optional round caps — analytically anti-aliased via fwidth. Mirrors ShadowPipeline byte-for-byte
/// (same root sig shape, structured-buffer instances, triangle-strip quad, premultiplied alpha blend).
/// </summary>
internal sealed unsafe class ArcPipeline : IDisposable
{
    private const int MaxInstances = 1024;   // per-FRAME policy cap (not a memory reservation — see _arena)

    private SdfSharedResources _shared = null!;
    private ID3D12PipelineState* _pso;
    // Instance storage is the device's SHARED per-frame UploadArena (one persistently-mapped UPLOAD buffer per
    // frame-in-flight, bump-allocated by every pipeline) — not a private worst-case ring. MaxInstances survives as
    // this pipeline's per-frame POLICY cap (unchanged drop semantics), no longer as unconditionally resident memory.
    private UploadArena _arena = null!;
    private int _cursor;
    private int _dropped;

    public int DroppedInstances => _dropped;

    private const string Hlsl = """
struct Inst { float2 pos; float2 size; float4 color; float4 m; float2 t; float thickness; float opacity; float startRad; float sweepRad; float roundCaps; float pad; };
StructuredBuffer<Inst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut { float4 pos : SV_Position; float2 local : TEXCOORD0; float2 size : TEXCOORD1; float4 color : TEXCOORD2; float4 arc : TEXCOORD3; float opacity : TEXCOORD4; };

VSOut VSMain(float2 corner : POSITION, uint iid : SV_InstanceID)
{
    Inst it = gInst[iid];
    float2 center = it.pos + it.size * 0.5;                    // ring centre (local space)
    float2 lp = it.pos + corner * it.size;                     // expand the unit quad over the rect
    float2 rel = lp - center;                                  // point relative to the ring centre
    float2 world = float2(it.m.x * lp.x + it.m.z * lp.y + it.t.x,
                          it.m.y * lp.x + it.m.w * lp.y + it.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.local = rel;
    o.size = it.size;
    o.color = it.color;
    o.arc = float4(it.thickness, it.startRad, it.sweepRad, it.roundCaps);
    o.opacity = it.opacity;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float2 p = i.local;                                        // px relative to the ring centre
    float thickness = i.arc.x;
    float startRad = i.arc.y;
    float sweepRad = i.arc.z;
    float roundCaps = i.arc.w;
    float R = (min(i.size.x, i.size.y) - thickness) * 0.5;
    float dRing = abs(length(p) - R) - thickness * 0.5;
    float ang = atan2(p.x, -p.y); if (ang < 0.0) ang += 6.28318530718;   // 0 at top (12 o'clock), clockwise
    float a = ang - startRad; a = a - 6.28318530718 * floor(a / 6.28318530718);   // wrap to [0,2pi)
    float dBand = (a <= sweepRad) ? dRing : 1e9;
    float2 capS = R * float2(sin(startRad), -cos(startRad));
    float2 capE = R * float2(sin(startRad + sweepRad), -cos(startRad + sweepRad));
    float dCap = (roundCaps > 0.5) ? (min(length(p - capS), length(p - capE)) - thickness * 0.5) : 1e9;
    float d = min(dBand, dCap);
    float aa = max(fwidth(d), 1e-5);
    float cov = 1.0 - smoothstep(-aa, aa, d);                 // stroke coverage
    float aOut = i.color.a * cov * i.opacity;
    return float4(i.color.rgb * aOut, aOut);                  // premultiplied
}
""";

    public void Init(ID3D12Device* device, SdfSharedResources shared, UploadArena arena)
    {
        _shared = shared;
        _arena = arena;
        BuildPipeline(device);
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    private static ID3DBlob* Compile(string entry, string target)
        => ShaderCompiler.Compile(Hlsl, entry, target, "arc");

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
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Arc.CreateGraphicsPipelineState");
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
    public bool Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<ArcInstance> instances, float vpW, float vpH,
                       bool bindSharedState = true, bool bindPipelineState = true)
    {
        int count = Math.Min(instances.Length, MaxInstances - _cursor);
        if (count <= 0) { _dropped += instances.Length; return false; }
        // Arena full: record NOTHING (command-list state untouched, exactly like the over-cap path above). The
        // arena folded the demand into its growth target and the device arms one more full repaint, so the
        // dropped run returns within a bank-depth of frames.
        if (!_arena.TryReserve(count * sizeof(ArcInstance), out byte* dst, out ulong gva))
        { _dropped += instances.Length; return false; }
        _dropped += instances.Length - count;
        ArcInstance* slot = (ArcInstance*)dst;
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
        if (bindPipelineState)
            cmd->SetPipelineState(_pso);
        cmd->SetGraphicsRootShaderResourceView(1, gva);
        cmd->DrawInstanced(4, (uint)count, 0, 0);
        return true;
    }

    public void Dispose()
    {
        // No instance buffers to release: the shared UploadArena owns them (disposed by the device).
        if (_pso != null) _pso->Release();
    }
}
