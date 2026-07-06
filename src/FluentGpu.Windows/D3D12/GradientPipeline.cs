using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>CPU mirror of the HLSL gradient <c>Inst</c> (160-byte stride; each float4 on a 16-byte boundary).</summary>
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
}

/// <summary>
/// Linear/radial gradient fill pipeline — a real multi-stop gradient (≤4 stops) clipped by the same analytic rounded-box
/// SDF as the solid fill. One instanced quad per gradient rect; same blend/AA posture as <see cref="RoundRectPipeline"/>.
/// </summary>
internal sealed unsafe class GradientPipeline : IDisposable
{
    private const int MaxInstances = 512;
    private const int FrameCount = 2;   // double-buffered per frame-in-flight so frame N's CPU writes never race frame N-1's GPU reads

    private ID3D12RootSignature* _rootSig;
    private ID3D12PipelineState* _pso;
    private ID3D12Resource* _quad;
    private readonly ID3D12Resource*[] _instances = new ID3D12Resource*[FrameCount];
    private readonly GradientInstance*[] _mapped = new GradientInstance*[FrameCount];
    private D3D12_VERTEX_BUFFER_VIEW _quadView;
    private int _cursor;
    private int _active;

    private const string Hlsl = """
struct Inst { float2 pos; float2 size; float2 gstart; float2 gend; float4 c0; float4 c1; float4 c2; float4 c3; float4 offs; float4 m; float2 t; float radius; float opacity; float shape; float stopCount; float stroke; float pad; };
StructuredBuffer<Inst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut { float4 pos : SV_Position; float2 local : TEXCOORD0; float2 halfSize : TEXCOORD1; float radius : TEXCOORD2; float opacity : TEXCOORD3; float2 gstart : TEXCOORD4; float2 gend : TEXCOORD5; float2 shapeCount : TEXCOORD6; float4 c0 : TEXCOORD7; float4 c1 : TEXCOORD8; float4 c2 : TEXCOORD9; float4 c3 : TEXCOORD10; float4 offs : TEXCOORD11; float stroke : TEXCOORD12; };

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

    public void Init(ID3D12Device* device)
    {
        BuildRootSignature(device);
        BuildPipeline(device);
        BuildBuffers(device);
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    private static ID3DBlob* Compile(string entry, string target)
    {
        byte[] src = Encoding.ASCII.GetBytes(Hlsl);
        byte[] ent = Encoding.ASCII.GetBytes(entry + "\0");
        byte[] tgt = Encoding.ASCII.GetBytes(target + "\0");
        ID3DBlob* code = null; ID3DBlob* err = null;
        fixed (byte* ps = src) fixed (byte* pe = ent) fixed (byte* pt = tgt)
        {
            HRESULT hr = D3DCompile(ps, (nuint)src.Length, null, null, null, (sbyte*)pe, (sbyte*)pt, 0, 0, &code, &err);
            if ((int)hr < 0)
            {
                string msg = err != null ? Marshal.PtrToStringAnsi((nint)err->GetBufferPointer()) ?? "" : "";
                throw new InvalidOperationException($"gradient shader {entry} ({target}) failed: {msg}");
            }
        }
        return code;
    }

    private void BuildRootSignature(ID3D12Device* device)
    {
        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[2];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.Num32BitValues = 2;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_SRV;
        p[1].Anonymous.Descriptor.ShaderRegister = 0;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;

        D3D12_ROOT_SIGNATURE_DESC desc = default;
        desc.NumParameters = 2;
        desc.pParameters = p;
        desc.Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;
        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "Gradient.SerializeRootSignature");
        ID3D12RootSignature* rs;
        Check(device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "Gradient.CreateRootSignature");
        _rootSig = rs;
        sig->Release();
        if (err != null) err->Release();
    }

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
            pd.pRootSignature = _rootSig;
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

    private void BuildBuffers(ID3D12Device* device)
    {
        float* quad = stackalloc float[8] { 0, 0, 1, 0, 0, 1, 1, 1 };
        _quad = CreateUpload(device, sizeof(float) * 8, "Gradient.QuadUpload");
        void* qp; _quad->Map(0, null, &qp);
        Buffer.MemoryCopy(quad, qp, sizeof(float) * 8, sizeof(float) * 8);
        _quad->Unmap(0, null);
        _quadView = new D3D12_VERTEX_BUFFER_VIEW { BufferLocation = _quad->GetGPUVirtualAddress(), SizeInBytes = sizeof(float) * 8, StrideInBytes = sizeof(float) * 2 };

        for (int f = 0; f < FrameCount; f++)
        {
            _instances[f] = CreateUpload(device, (uint)(sizeof(GradientInstance) * MaxInstances), "Gradient.InstanceUpload");
            void* ip; _instances[f]->Map(0, null, &ip);
            _mapped[f] = (GradientInstance*)ip;   // persistently mapped
        }
    }

    private static ID3D12Resource* CreateUpload(ID3D12Device* device, uint bytes, string name)
    {
        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = bytes; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* res;
        Check(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "Gradient.CreateCommittedResource");
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    /// <summary>Select this frame's instance buffer (by back-buffer index) and reset the cursor. The chosen buffer was
    /// last written FrameCount frames ago, whose GPU work the device has already fenced — so no CPU↔GPU race.</summary>
    public void BeginFrame(int frameIndex) { _active = ((frameIndex % FrameCount) + FrameCount) % FrameCount; _cursor = 0; }

    /// <summary>Record one run; <paramref name="rebind"/> false skips the static state (still bound from a previous
    /// same-pipeline run — see RoundRectPipeline.Record). Returns false when full (state untouched).</summary>
    public bool Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<GradientInstance> instances, float vpW, float vpH, bool rebind = true)
    {
        int start = _cursor;
        int count = Math.Min(instances.Length, MaxInstances - start);
        if (count <= 0) return false;
        for (int i = 0; i < count; i++) _mapped[_active][start + i] = instances[i];
        _cursor += count;
        if (rebind)
        {
            float* vp = stackalloc float[2] { vpW, vpH };
            cmd->SetGraphicsRootSignature(_rootSig);
            cmd->SetPipelineState(_pso);
            cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView)
                cmd->IASetVertexBuffers(0, 1, qv);
        }
        cmd->SetGraphicsRootShaderResourceView(1, _instances[_active]->GetGPUVirtualAddress() + (ulong)(start * sizeof(GradientInstance)));
        cmd->DrawInstanced(4, (uint)count, 0, 0);
        return true;
    }

    public void Dispose()
    {
        for (int f = 0; f < FrameCount; f++)
            if (_instances[f] != null) { _instances[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_instances[f], "Gradient.InstanceUpload"); _instances[f]->Release(); _instances[f] = null; }
        if (_quad != null) { D3D12MemoryDiagnostics.Release(_quad, "Gradient.QuadUpload"); _quad->Release(); _quad = null; }
        if (_pso != null) _pso->Release();
        if (_rootSig != null) _rootSig->Release();
    }
}
