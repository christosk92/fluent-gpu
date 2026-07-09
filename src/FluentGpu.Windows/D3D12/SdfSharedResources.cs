using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Device-owned static state shared by the SDF pipelines with the common viewport+SRV root layout.</summary>
internal sealed unsafe class SdfSharedResources : IDisposable
{
    private ID3D12RootSignature* _rootSig;
    private ID3D12Resource* _quad;
    private D3D12_VERTEX_BUFFER_VIEW _quadView;

    public ID3D12RootSignature* RootSignature => _rootSig;
    public D3D12_VERTEX_BUFFER_VIEW QuadView => _quadView;

    // Reused viewport constants — Record() runs inside SubmitWithLayers' FlushSegment loop; stackalloc there
    // inlines into the loop and trips /GS (0xC0000409) under NativeAOT.
    private readonly float[] _vpConstants = new float[2];

    public void SetViewportConstants(ID3D12GraphicsCommandList* cmd, float vpW, float vpH)
    {
        _vpConstants[0] = vpW;
        _vpConstants[1] = vpH;
        fixed (float* vp = _vpConstants)
            cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
    }

    public void Init(ID3D12Device* device)
    {
        BuildRootSignature(device);
        BuildQuad(device);
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
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
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "Sdf.SerializeRootSignature");
        ID3D12RootSignature* rs;
        Check(device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "Sdf.CreateRootSignature");
        _rootSig = rs;
        sig->Release();
        if (err != null) err->Release();
    }

    private void BuildQuad(ID3D12Device* device)
    {
        float* quad = stackalloc float[8] { 0, 0, 1, 0, 0, 1, 1, 1 };
        _quad = CreateUpload(device, sizeof(float) * 8, "Sdf.QuadUpload");
        void* qp; _quad->Map(0, null, &qp);
        Buffer.MemoryCopy(quad, qp, sizeof(float) * 8, sizeof(float) * 8);
        _quad->Unmap(0, null);
        _quadView = new D3D12_VERTEX_BUFFER_VIEW
        {
            BufferLocation = _quad->GetGPUVirtualAddress(),
            SizeInBytes = sizeof(float) * 8,
            StrideInBytes = sizeof(float) * 2,
        };
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
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "Sdf.CreateCommittedResource");
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    public void Dispose()
    {
        if (_quad != null) { D3D12MemoryDiagnostics.Release(_quad, "Sdf.QuadUpload"); _quad->Release(); _quad = null; }
        if (_rootSig != null) { _rootSig->Release(); _rootSig = null; }
    }
}
