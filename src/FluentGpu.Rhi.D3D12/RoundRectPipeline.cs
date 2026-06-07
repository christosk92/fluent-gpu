using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>CPU mirror of the HLSL <c>Inst</c> struct. One instanced quad per rounded rect, with a world transform + opacity.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RectInstance
{
    public float PosX, PosY, W, H;
    public float RTL, RTR, RBR, RBL;
    public float R, G, B, A;
    public float M11, M12, M21, M22, Dx, Dy;   // 2x3 world transform (local→device)
    public float Opacity;
    public float StrokeWidth;   // 0 = filled; >0 = an SDF outline (focus ring / border) of this width. Keeps the 80-byte stride.
}

/// <summary>
/// The SDF rounded-rect pipeline (design/subsystems/gpu-renderer.md): a unit quad drawn instanced; instance data
/// (rect/radii/color) is read in the VS from a root StructuredBuffer; the PS evaluates the analytic rounded-box SDF
/// with single-pass AA. Shaders are compiled at runtime (D3DCompile → DXBC sm5.1; DXC→DXIL offline is the spec's eventual path).
/// </summary>
internal sealed unsafe class RoundRectPipeline : IDisposable
{
    private const int MaxInstances = 4096;

    private ID3D12RootSignature* _rootSig;
    private ID3D12PipelineState* _pso;
    private ID3D12Resource* _quad;          // 4-vertex unit quad (upload heap)
    private ID3D12Resource* _instances;     // structured buffer of RectInstance (upload heap, persistently mapped)
    private RectInstance* _mapped;
    private D3D12_VERTEX_BUFFER_VIEW _quadView;
    private int _cursor;

    private const string Hlsl = """
struct Inst { float2 pos; float2 size; float4 radii; float4 color; float4 m; float2 t; float opacity; float stroke; };
StructuredBuffer<Inst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut { float4 pos : SV_Position; float2 local : TEXCOORD0; float2 halfSize : TEXCOORD1; float radius : TEXCOORD2; float4 color : TEXCOORD3; float opacity : TEXCOORD4; float stroke : TEXCOORD5; };

VSOut VSMain(float2 corner : POSITION, uint iid : SV_InstanceID)
{
    Inst it = gInst[iid];
    float2 lp = it.pos + corner * it.size;                      // local-space point
    float2 world = float2(it.m.x * lp.x + it.m.z * lp.y + it.t.x,  // 2x3 affine: local → device
                          it.m.y * lp.x + it.m.w * lp.y + it.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.local = corner * it.size - it.size * 0.5;   // SDF coverage stays in local space (crisp under transform via fwidth)
    o.halfSize = it.size * 0.5;
    o.radius = it.radii.x;
    o.color = it.color;
    o.opacity = it.opacity;
    o.stroke = it.stroke;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float2 q = abs(i.local) - (i.halfSize - i.radius);
    float d = min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - i.radius;   // signed distance to the rounded-box edge
    float fw = max(fwidth(d), 1e-4);
    float cov;
    if (i.stroke > 0.0)
        cov = clamp(0.5 - (abs(d) - i.stroke * 0.5) / fw, 0.0, 1.0);   // outline: a band of width 'stroke' centred on the edge
    else
        cov = clamp(0.5 - d / fw, 0.0, 1.0);                            // fill: crisp ~1px linear AA
    float aOut = i.color.a * cov * i.opacity;
    return float4(i.color.rgb * aOut, aOut);   // premultiplied alpha
}
""";

    public void Init(ID3D12Device* device)
    {
        BuildRootSignature(device);
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
                throw new InvalidOperationException($"shader {entry} ({target}) failed: {msg}");
            }
        }
        return code;
    }

    private void BuildRootSignature(ID3D12Device* device)
    {
        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[2];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.ShaderRegister = 0;
        p[0].Anonymous.Constants.RegisterSpace = 0;
        p[0].Anonymous.Constants.Num32BitValues = 2;   // viewport (w,h)
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_SRV;
        p[1].Anonymous.Descriptor.ShaderRegister = 0;   // t0
        p[1].Anonymous.Descriptor.RegisterSpace = 0;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;

        D3D12_ROOT_SIGNATURE_DESC desc = default;
        desc.NumParameters = 2;
        desc.pParameters = p;
        desc.Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "SerializeRootSignature");
        ID3D12RootSignature* rs;
        Check(device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "CreateRootSignature");
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
            elem.SemanticIndex = 0;
            elem.Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT;
            elem.InputSlot = 0;
            elem.AlignedByteOffset = 0;
            elem.InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA;
            elem.InstanceDataStepRate = 0;

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

            // rasterizer: solid, no cull
            pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
            pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
            pd.RasterizerState.DepthClipEnable = BOOL.TRUE;

            // alpha blend on RT0
            pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
            pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;   // premultiplied
            pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
            pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;

            // depth/stencil off
            pd.DepthStencilState.DepthEnable = BOOL.FALSE;
            pd.DepthStencilState.StencilEnable = BOOL.FALSE;

            ID3D12PipelineState* pso;
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "CreateGraphicsPipelineState");
            _pso = pso;
        }
        vs->Release();
        ps->Release();
    }

    private void BuildBuffers(ID3D12Device* device)
    {
        // unit quad (triangle strip): (0,0)(1,0)(0,1)(1,1)
        float* quad = stackalloc float[8] { 0, 0, 1, 0, 0, 1, 1, 1 };
        _quad = CreateUpload(device, sizeof(float) * 8, "RoundRect.QuadUpload");
        void* qp; _quad->Map(0, null, &qp);
        Buffer.MemoryCopy(quad, qp, sizeof(float) * 8, sizeof(float) * 8);
        _quad->Unmap(0, null);
        _quadView = new D3D12_VERTEX_BUFFER_VIEW { BufferLocation = _quad->GetGPUVirtualAddress(), SizeInBytes = sizeof(float) * 8, StrideInBytes = sizeof(float) * 2 };

        _instances = CreateUpload(device, (uint)(sizeof(RectInstance) * MaxInstances), "RoundRect.InstanceUpload");
        void* ip; _instances->Map(0, null, &ip);
        _mapped = (RectInstance*)ip;   // persistently mapped
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
        rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE;
        ID3D12Resource* res;
        Check(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "CreateCommittedResource");
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    public void BeginFrame() => _cursor = 0;

    public void Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<RectInstance> instances, float vpW, float vpH)
    {
        int start = _cursor;
        int count = Math.Min(instances.Length, MaxInstances - start);
        if (count == 0) return;
        for (int i = 0; i < count; i++) _mapped[start + i] = instances[i];
        _cursor += count;

        float* vp = stackalloc float[2] { vpW, vpH };
        cmd->SetGraphicsRootSignature(_rootSig);
        cmd->SetPipelineState(_pso);
        cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
        cmd->SetGraphicsRootShaderResourceView(1, _instances->GetGPUVirtualAddress() + (ulong)(start * sizeof(RectInstance)));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView)
            cmd->IASetVertexBuffers(0, 1, qv);
        cmd->DrawInstanced(4, (uint)count, 0, 0);
    }

    public void Dispose()
    {
        if (_instances != null) { _instances->Unmap(0, null); D3D12MemoryDiagnostics.Release(_instances, "RoundRect.InstanceUpload"); _instances->Release(); _instances = null; }
        if (_quad != null) { D3D12MemoryDiagnostics.Release(_quad, "RoundRect.QuadUpload"); _quad->Release(); _quad = null; }
        if (_pso != null) _pso->Release();
        if (_rootSig != null) _rootSig->Release();
    }
}
