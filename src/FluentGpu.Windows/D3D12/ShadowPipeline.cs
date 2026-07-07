using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>CPU mirror of the HLSL shadow <c>Inst</c> (laid out so each float4 sits on a 16-byte boundary → 80-byte stride).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadowInstance
{
    public float PosX, PosY, W, H;        // 0..15  : box rect (local)
    public float R, G, B, A;              // 16..31 : shadow color
    public float M11, M12, M21, M22;      // 32..47 : world transform (linear part)
    public float Dx, Dy, Radius, Opacity; // 48..63 : translation + corner radius + cumulative opacity
    public float Blur, Spread, OffX, OffY;// 64..79 : gaussian blur radius + spread + offset
}

/// <summary>
/// Soft drop-shadow pipeline — a rounded-box gaussian (the closed-form Evan-Wallace "fast rounded rectangle shadow":
/// erf of the box convolved with a gaussian, 4-tap vertical integral for the rounded corners). One instanced quad per
/// shadow, expanded by 3σ + spread so the falloff fits; drawn beneath the fills. Real soft shadow, not a gradient hack.
/// </summary>
internal sealed unsafe class ShadowPipeline : IDisposable
{
    private const int MaxInstances = 1024;
    private const int FrameCount = 2;   // double-buffered per frame-in-flight so frame N's CPU writes never race frame N-1's GPU reads

    private SdfSharedResources _shared = null!;
    private ID3D12PipelineState* _pso;
    private readonly ID3D12Resource*[] _instances = new ID3D12Resource*[FrameCount];
    private readonly ShadowInstance*[] _mapped = new ShadowInstance*[FrameCount];
    private int _cursor;
    private int _active;
    private ulong _activeGva;
    private int _dropped;

    public int DroppedInstances => _dropped;

    private const string Hlsl = """
struct Inst { float2 pos; float2 size; float4 color; float4 m; float2 t; float radius; float opacity; float blur; float spread; float2 offset; };
StructuredBuffer<Inst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut { float4 pos : SV_Position; float2 local : TEXCOORD0; float2 halfSize : TEXCOORD1; float sigma : TEXCOORD2; float corner : TEXCOORD3; float4 color : TEXCOORD4; float opacity : TEXCOORD5; };

VSOut VSMain(float2 corner : POSITION, uint iid : SV_InstanceID)
{
    Inst it = gInst[iid];
    float sigma = max(it.blur * 0.5, 0.5);
    float2 halfSize = it.size * 0.5 + it.spread;
    float2 he = halfSize + 3.0 * sigma;                       // quad half-extent (covers the gaussian tail)
    float2 center = it.pos + it.size * 0.5 + it.offset;       // shadow box centre (local space)
    float2 rel = (corner - 0.5) * (he * 2.0);                 // point relative to the shadow centre
    float2 lp = center + rel;
    float2 world = float2(it.m.x * lp.x + it.m.z * lp.y + it.t.x,
                          it.m.y * lp.x + it.m.w * lp.y + it.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.local = rel;
    o.halfSize = halfSize;
    o.sigma = sigma;
    o.corner = min(it.radius + it.spread, min(halfSize.x, halfSize.y));
    o.color = it.color;
    o.opacity = it.opacity;
    return o;
}

float gaussian(float x, float sigma) { return exp(-(x * x) / (2.0 * sigma * sigma)) * 0.3989422804 / sigma; }

float2 erf2(float2 x)
{
    float2 s = sign(x); float2 a = abs(x);
    float2 r = 1.0 + (0.278393 + (0.230389 + 0.078108 * (a * a)) * a) * a;
    r = r * r;
    return s - s / (r * r);
}

float shadowX(float x, float y, float sigma, float corner, float2 halfSize)
{
    float delta = min(halfSize.y - corner - abs(y), 0.0);
    float curved = halfSize.x - corner + sqrt(max(0.0, corner * corner - delta * delta));
    float2 integral = 0.5 + 0.5 * erf2((x + float2(-curved, curved)) * (0.70710678 / sigma));
    return integral.y - integral.x;
}

float4 PSMain(VSOut i) : SV_Target
{
    float low = i.local.y - i.halfSize.y;
    float high = i.local.y + i.halfSize.y;
    float start = clamp(-3.0 * i.sigma, low, high);
    float end = clamp(3.0 * i.sigma, low, high);
    float step = (end - start) / 4.0;
    float y = start + step * 0.5;
    float value = 0.0;
    [unroll] for (int s = 0; s < 4; s++)
    {
        value += shadowX(i.local.x, i.local.y - y, i.sigma, i.corner, i.halfSize) * gaussian(y, i.sigma) * step;
        y += step;
    }
    float aOut = i.color.a * saturate(value) * i.opacity;
    return float4(i.color.rgb * aOut, aOut);   // premultiplied
}
""";

    public void Init(ID3D12Device* device, SdfSharedResources shared)
    {
        _shared = shared;
        BuildPipeline(device);
        BuildBuffers(device);
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

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
                throw new InvalidOperationException($"shadow shader {entry} ({target}) failed: {msg}");
            }
        }
        return code;
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
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Shadow.CreateGraphicsPipelineState");
            _pso = pso;
        }
        vs->Release();
        ps->Release();
    }

    private void BuildBuffers(ID3D12Device* device)
    {
        for (int f = 0; f < FrameCount; f++)
        {
            _instances[f] = CreateUpload(device, (uint)(sizeof(ShadowInstance) * MaxInstances), "Shadow.InstanceUpload");
            void* ip; _instances[f]->Map(0, null, &ip);
            _mapped[f] = (ShadowInstance*)ip;   // persistently mapped
        }
    }

    private static ID3D12Resource* CreateUpload(ID3D12Device* device, uint bytes, string name)
    {
        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = bytes;
        rd.Height = 1;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
        rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* res;
        Check(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "Shadow.CreateCommittedResource");
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    /// <summary>Select this frame's instance buffer (by back-buffer index) and reset the cursor. The chosen buffer was
    /// last written FrameCount frames ago, whose GPU work the device has already fenced — so no CPU↔GPU race.</summary>
    public void BeginFrame(int frameIndex) { _active = ((frameIndex % FrameCount) + FrameCount) % FrameCount; _activeGva = _instances[_active]->GetGPUVirtualAddress(); _cursor = 0; _dropped = 0; }

    /// <summary>Record one run; shared SDF state and this pipeline's PSO can be rebound independently. Returns false
    /// when full (state untouched).</summary>
    public bool Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<ShadowInstance> instances, float vpW, float vpH,
                       bool bindSharedState = true, bool bindPipelineState = true)
    {
        int start = _cursor;
        int count = Math.Min(instances.Length, MaxInstances - start);
        if (count <= 0) { _dropped += instances.Length; return false; }
        _dropped += instances.Length - count;
        for (int i = 0; i < count; i++) _mapped[_active][start + i] = instances[i];
        _cursor += count;

        if (bindSharedState)
        {
            float* vp = stackalloc float[2] { vpW, vpH };
            cmd->SetGraphicsRootSignature(_shared.RootSignature);
            cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            var qv = _shared.QuadView;
            cmd->IASetVertexBuffers(0, 1, &qv);
        }
        if (bindPipelineState)
            cmd->SetPipelineState(_pso);
        cmd->SetGraphicsRootShaderResourceView(1, _activeGva + (ulong)(start * sizeof(ShadowInstance)));
        cmd->DrawInstanced(4, (uint)count, 0, 0);
        return true;
    }

    public void Dispose()
    {
        for (int f = 0; f < FrameCount; f++)
            if (_instances[f] != null) { _instances[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_instances[f], "Shadow.InstanceUpload"); _instances[f]->Release(); _instances[f] = null; }
        if (_pso != null) _pso->Release();
    }
}
