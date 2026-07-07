using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>CPU mirror of the HLSL image <c>Inst</c> (96 B). Like <see cref="RectInstance"/> but sampled from a
/// per-image texture, with a premultiplied placeholder colour + cross-fade factor for the media-pipeline §7
/// placeholder→image blend (CrossFade=1 ⇒ full image), and an atlas sub-rect UV (origin+size in the page; (0,0,1,1)
/// for a standalone/whole-texture image, a cell for an atlas-packed thumbnail).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ImageInstance
{
    public float PosX, PosY, W, H;             // 0  local rect
    public float RTL, RTR, RBR, RBL;           // 16 per-corner radii
    public float M11, M12, M21, M22;           // 32 world transform (linear part)
    public float Dx, Dy;                       // 48 translation
    public float Opacity, CrossFade;           // 56
    public float PR, PG, PB, PA;               // 64 placeholder (premultiplied) for the cross-fade lerp
    public float UvX, UvY, UvW, UvH;           // 80 atlas sub-rect (origin + size); (0,0,1,1) = whole texture
}

/// <summary>
/// Textured-image pipeline (media-pipeline.md §2/§7): one instanced quad per image, sampling a per-image SRV bound via
/// a descriptor table, clipped by the same analytic rounded-box SDF as <see cref="RoundRectPipeline"/>, with a
/// premultiplied placeholder→image cross-fade. Premultiplied blend posture (ONE / INV_SRC_ALPHA), BGRA8 RT.
/// </summary>
internal sealed unsafe class ImagePipeline : IDisposable
{
    private const int MaxDraws = 1024;
    private const int FrameCount = 2;   // double-buffered per frame-in-flight so frame N's CPU writes never race frame N-1's GPU reads

    private ID3D12RootSignature* _rootSig;
    private ID3D12PipelineState* _pso;
    private ID3D12Resource* _quad;
    private readonly ID3D12Resource*[] _instances = new ID3D12Resource*[FrameCount];
    private readonly ImageInstance*[] _mapped = new ImageInstance*[FrameCount];
    private D3D12_VERTEX_BUFFER_VIEW _quadView;
    private int _cursor;
    private int _active;
    private ulong _activeGva;
    private int _dropped;

    public int DroppedInstances => _dropped;

    private const string Hlsl = """
struct Inst { float2 pos; float2 size; float4 radii; float4 m; float2 t; float opacity; float crossFade; float4 ph; float4 atlasUv; };
StructuredBuffer<Inst> gInst : register(t1);
Texture2D gTex : register(t0);
SamplerState gSamp : register(s0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float2 local : TEXCOORD1; float2 halfSize : TEXCOORD2; float4 radii : TEXCOORD3; float opacity : TEXCOORD4; float crossFade : TEXCOORD5; float4 ph : TEXCOORD6; float4 atlasUv : TEXCOORD7; };

VSOut VSMain(float2 corner : POSITION, uint iid : SV_InstanceID)
{
    Inst it = gInst[iid];
    float2 lp = it.pos + corner * it.size;
    float2 world = float2(it.m.x * lp.x + it.m.z * lp.y + it.t.x, it.m.y * lp.x + it.m.w * lp.y + it.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.uv = corner;                              // image fills the quad 0..1
    o.local = corner * it.size - it.size * 0.5;
    o.halfSize = it.size * 0.5;
    o.radii = it.radii;
    o.opacity = it.opacity;
    o.crossFade = it.crossFade;
    o.ph = it.ph;
    o.atlasUv = it.atlasUv;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float2 auv = i.atlasUv.xy + i.uv * i.atlasUv.zw;    // resolve the atlas sub-rect ((0,0,1,1) = whole texture)
    float4 img = gTex.Sample(gSamp, auv);               // premultiplied image
    float4 col = lerp(i.ph, img, saturate(i.crossFade)); // placeholder→image cross-fade, premultiplied space
    float2 s = sign(i.local);
    float r = (s.x < 0.0) ? (s.y < 0.0 ? i.radii.x : i.radii.w) : (s.y < 0.0 ? i.radii.y : i.radii.z);
    float2 q = abs(i.local) - (i.halfSize - r);
    float d = min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
    float fw = max(fwidth(d), 1e-4);
    float cov = clamp(0.5 - d / fw, 0.0, 1.0);          // rounded-corner clip, crisp ~1px AA
    return col * (cov * i.opacity);                     // scale rgb AND a (premultiplied)
}
""";

    public void Init(ID3D12Device* device)
    {
        BuildRootSignature(device);
        BuildPipeline(device);
        BuildBuffers(device);
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    private void BuildRootSignature(ID3D12Device* device)
    {
        // root: [0] constants b0 (viewport), [1] table (SRV t0 = this image), [2] root SRV t1 = instances; static sampler s0
        D3D12_DESCRIPTOR_RANGE range = default;
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1; range.BaseShaderRegister = 0; range.RegisterSpace = 0;
        range.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;

        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[3];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.ShaderRegister = 0; p[0].Anonymous.Constants.Num32BitValues = 2;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
        p[1].Anonymous.DescriptorTable.pDescriptorRanges = &range;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;
        p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_SRV;
        p[2].Anonymous.Descriptor.ShaderRegister = 1;
        p[2].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;

        D3D12_STATIC_SAMPLER_DESC samp = default;
        samp.Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        samp.AddressU = samp.AddressV = samp.AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samp.ShaderRegister = 0; samp.RegisterSpace = 0;
        samp.ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_ROOT_SIGNATURE_DESC rs = default;
        rs.NumParameters = 3; rs.pParameters = p;
        rs.NumStaticSamplers = 1; rs.pStaticSamplers = &samp;
        rs.Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&rs, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "Image.SerializeRootSignature");
        ID3D12RootSignature* root;
        Check(device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&root), "Image.CreateRootSignature");
        _rootSig = root; sig->Release(); if (err != null) err->Release();
    }

    private void BuildPipeline(ID3D12Device* device)
    {
        ID3DBlob* vs = ShaderCompiler.Compile(Hlsl, "VSMain", "vs_5_1");
        ID3DBlob* ps = ShaderCompiler.Compile(Hlsl, "PSMain", "ps_5_1");
        byte[] semantic = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        fixed (byte* sem = semantic)
        {
            D3D12_INPUT_ELEMENT_DESC elem = default;
            elem.SemanticName = (sbyte*)sem; elem.Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT;
            elem.InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA;

            D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
            pd.pRootSignature = _rootSig;
            pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
            pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
            pd.InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = &elem, NumElements = 1 };
            pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
            pd.NumRenderTargets = 1; pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            pd.SampleDesc.Count = 1; pd.SampleMask = uint.MaxValue;
            pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
            pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
            pd.RasterizerState.DepthClipEnable = BOOL.TRUE;
            pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
            pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;   // premultiplied
            pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
            pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
            pd.DepthStencilState.DepthEnable = BOOL.FALSE;
            pd.DepthStencilState.StencilEnable = BOOL.FALSE;
            ID3D12PipelineState* pso;
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Image.CreateGraphicsPipelineState");
            _pso = pso;
        }
        vs->Release(); ps->Release();
    }

    private void BuildBuffers(ID3D12Device* device)
    {
        float* quad = stackalloc float[8] { 0, 0, 1, 0, 0, 1, 1, 1 };
        _quad = CreateUpload(device, sizeof(float) * 8, "Image.QuadUpload");
        void* qp; _quad->Map(0, null, &qp); Buffer.MemoryCopy(quad, qp, 32, 32); _quad->Unmap(0, null);
        _quadView = new D3D12_VERTEX_BUFFER_VIEW { BufferLocation = _quad->GetGPUVirtualAddress(), SizeInBytes = 32, StrideInBytes = 8 };

        for (int f = 0; f < FrameCount; f++)
        {
            _instances[f] = CreateUpload(device, (uint)(sizeof(ImageInstance) * MaxDraws), "Image.InstanceUpload");
            void* ip; _instances[f]->Map(0, null, &ip); _mapped[f] = (ImageInstance*)ip;
        }
    }

    private static ID3D12Resource* CreateUpload(ID3D12Device* device, uint bytes, string name)
    {
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = bytes; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* res;
        Check(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "Image.CreateUpload");
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    /// <summary>Select this frame's instance buffer (by back-buffer index) and reset the cursor. The chosen buffer was
    /// last written FrameCount frames ago, whose GPU work the device has already fenced — so no CPU↔GPU race.</summary>
    public void BeginFrame(int frameIndex) { _active = ((frameIndex % FrameCount) + FrameCount) % FrameCount; _activeGva = _instances[_active]->GetGPUVirtualAddress(); _cursor = 0; _dropped = 0; }

    /// <summary>Bind the shared image-pass state ONCE (descriptor heap, root sig, PSO, viewport, topology, quad VB) — so
    /// the per-image draws don't re-bind the descriptor heap N times (the per-image churn that the acrylic scroll path
    /// turned into flicker). Call once per image pass, then <see cref="Draw"/> each image.</summary>
    public void Begin(ID3D12GraphicsCommandList* cmd, ID3D12DescriptorHeap* heap, float vpW, float vpH)
    {
        cmd->SetDescriptorHeaps(1, &heap);
        cmd->SetGraphicsRootSignature(_rootSig);
        cmd->SetPipelineState(_pso);
        float* vp = stackalloc float[2] { vpW, vpH };
        cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView) cmd->IASetVertexBuffers(0, 1, qv);
    }

    /// <summary>Draw one image: bind its per-image SRV (descriptor table) + its instance, then the quad.</summary>
    public void Draw(ID3D12GraphicsCommandList* cmd, D3D12_GPU_DESCRIPTOR_HANDLE srv, in ImageInstance inst)
    {
        if (_cursor >= MaxDraws) { _dropped++; return; }
        int slot = _cursor++;
        _mapped[_active][slot] = inst;
        cmd->SetGraphicsRootDescriptorTable(1, srv);
        cmd->SetGraphicsRootShaderResourceView(2, _activeGva + (ulong)(slot * sizeof(ImageInstance)));
        cmd->DrawInstanced(4, 1, 0, 0);
    }

    /// <summary>Draw a consecutive same-SRV image span with one descriptor bind and one instanced draw.</summary>
    public int DrawRange(ID3D12GraphicsCommandList* cmd, D3D12_GPU_DESCRIPTOR_HANDLE srv, ReadOnlySpan<ImageInstance> instances)
    {
        int count = Math.Min(instances.Length, MaxDraws - _cursor);
        if (count <= 0) { _dropped += instances.Length; return 0; }
        _dropped += instances.Length - count;
        int start = _cursor;
        for (int i = 0; i < count; i++) _mapped[_active][start + i] = instances[i];
        _cursor += count;
        cmd->SetGraphicsRootDescriptorTable(1, srv);
        cmd->SetGraphicsRootShaderResourceView(2, _activeGva + (ulong)(start * sizeof(ImageInstance)));
        cmd->DrawInstanced(4, (uint)count, 0, 0);
        return count;
    }

    public void Dispose()
    {
        for (int f = 0; f < FrameCount; f++)
            if (_instances[f] != null) { _instances[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_instances[f], "Image.InstanceUpload"); _instances[f]->Release(); _instances[f] = null; }
        if (_quad != null) { D3D12MemoryDiagnostics.Release(_quad, "Image.QuadUpload"); _quad->Release(); _quad = null; }
        if (_pso != null) _pso->Release();
        if (_rootSig != null) _rootSig->Release();
    }
}
