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
    private const int MaxInstances = 1024;
    private const int FrameCount = 2;   // double-buffered per frame-in-flight so frame N's CPU writes never race frame N-1's GPU reads

    private ID3D12RootSignature* _rootSig;
    private ID3D12PipelineState* _pso;
    private ID3D12Resource* _quad;
    private readonly ID3D12Resource*[] _instances = new ID3D12Resource*[FrameCount];
    private readonly ArcInstance*[] _mapped = new ArcInstance*[FrameCount];
    private D3D12_VERTEX_BUFFER_VIEW _quadView;
    private int _cursor;
    private int _active;

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
                throw new InvalidOperationException($"arc shader {entry} ({target}) failed: {msg}");
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
        p[0].Anonymous.Constants.Num32BitValues = 2;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_SRV;
        p[1].Anonymous.Descriptor.ShaderRegister = 0;
        p[1].Anonymous.Descriptor.RegisterSpace = 0;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;

        D3D12_ROOT_SIGNATURE_DESC desc = default;
        desc.NumParameters = 2;
        desc.pParameters = p;
        desc.Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "Arc.SerializeRootSignature");
        ID3D12RootSignature* rs;
        Check(device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "Arc.CreateRootSignature");
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
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Arc.CreateGraphicsPipelineState");
            _pso = pso;
        }
        vs->Release();
        ps->Release();
    }

    private void BuildBuffers(ID3D12Device* device)
    {
        float* quad = stackalloc float[8] { 0, 0, 1, 0, 0, 1, 1, 1 };
        _quad = CreateUpload(device, sizeof(float) * 8, "Arc.QuadUpload");
        void* qp; _quad->Map(0, null, &qp);
        Buffer.MemoryCopy(quad, qp, sizeof(float) * 8, sizeof(float) * 8);
        _quad->Unmap(0, null);
        _quadView = new D3D12_VERTEX_BUFFER_VIEW { BufferLocation = _quad->GetGPUVirtualAddress(), SizeInBytes = sizeof(float) * 8, StrideInBytes = sizeof(float) * 2 };

        for (int f = 0; f < FrameCount; f++)
        {
            _instances[f] = CreateUpload(device, (uint)(sizeof(ArcInstance) * MaxInstances), "Arc.InstanceUpload");
            void* ip; _instances[f]->Map(0, null, &ip);
            _mapped[f] = (ArcInstance*)ip;   // persistently mapped
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
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "Arc.CreateCommittedResource");
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    /// <summary>Select this frame's instance buffer (by back-buffer index) and reset the cursor. The chosen buffer was
    /// last written FrameCount frames ago, whose GPU work the device has already fenced — so no CPU↔GPU race.</summary>
    public void BeginFrame(int frameIndex) { _active = ((frameIndex % FrameCount) + FrameCount) % FrameCount; _cursor = 0; }

    public void Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<ArcInstance> instances, float vpW, float vpH)
    {
        int start = _cursor;
        int count = Math.Min(instances.Length, MaxInstances - start);
        if (count <= 0) return;
        for (int i = 0; i < count; i++) _mapped[_active][start + i] = instances[i];
        _cursor += count;

        float* vp = stackalloc float[2] { vpW, vpH };
        cmd->SetGraphicsRootSignature(_rootSig);
        cmd->SetPipelineState(_pso);
        cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
        cmd->SetGraphicsRootShaderResourceView(1, _instances[_active]->GetGPUVirtualAddress() + (ulong)(start * sizeof(ArcInstance)));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView)
            cmd->IASetVertexBuffers(0, 1, qv);
        cmd->DrawInstanced(4, (uint)count, 0, 0);
    }

    public void Dispose()
    {
        for (int f = 0; f < FrameCount; f++)
            if (_instances[f] != null) { _instances[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_instances[f], "Arc.InstanceUpload"); _instances[f]->Release(); _instances[f] = null; }
        if (_quad != null) { D3D12MemoryDiagnostics.Release(_quad, "Arc.QuadUpload"); _quad->Release(); _quad = null; }
        if (_pso != null) _pso->Release();
        if (_rootSig != null) _rootSig->Release();
    }
}
