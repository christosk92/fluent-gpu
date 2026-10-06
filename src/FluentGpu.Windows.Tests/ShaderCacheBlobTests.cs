using System;
using FluentGpu.Rhi.D3D12;
using TerraFX.Interop.DirectX;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The shader cache's own <c>ID3DBlob</c> (a warm start serves cached DXBC without loading d3dcompiler_47.dll): the
/// pipelines read it through the ordinary COM vtable, so it must hand back the exact bytes and size, answer
/// QueryInterface for IUnknown/ID3DBlob only, and count references like any blob. No GPU, no DLL.
/// </summary>
public sealed unsafe class ShaderCacheBlobTests
{
    [Fact]
    public void Cached_blob_serves_the_exact_bytes_through_the_com_vtable_and_counts_references()
    {
        byte[] dxbc = new byte[1029];
        new Random(7).NextBytes(dxbc);
        dxbc[0] = (byte)'D'; dxbc[1] = (byte)'X'; dxbc[2] = (byte)'B'; dxbc[3] = (byte)'C';

        ID3DBlob* blob = ShaderCompiler.CachedBlob.Create(dxbc);
        Assert.Equal((nuint)dxbc.Length, blob->GetBufferSize());
        Assert.True(new ReadOnlySpan<byte>(blob->GetBufferPointer(), dxbc.Length).SequenceEqual(dxbc));
        Assert.Equal(0, (long)blob->GetBufferPointer() % 8);

        Guid blobIid = new(0x8BA5FB08, 0x5195, 0x40E2, 0xAC, 0x58, 0x0D, 0x98, 0x9C, 0x3A, 0x01, 0x02);
        void* same = null;
        Assert.Equal(0, (int)blob->QueryInterface(&blobIid, &same));
        Assert.True(same == blob);
        Guid other = Guid.NewGuid();
        void* none = (void*)1;
        Assert.Equal(unchecked((int)0x80004002), (int)blob->QueryInterface(&other, &none));
        Assert.True(none == null);

        Assert.Equal(3u, blob->AddRef());   // 1 (create) + 1 (QI) + 1
        Assert.Equal(2u, blob->Release());
        Assert.Equal(1u, blob->Release());
        Assert.Equal(0u, blob->Release());  // frees the block
    }
}
