using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Runtime HLSL → DXBC (sm5.1) compile via D3DCompile. The spec's eventual path is DXC → DXIL offline.</summary>
internal static unsafe class ShaderCompiler
{
    public static ID3DBlob* Compile(string source, string entry, string target)
    {
        byte[] src = Encoding.ASCII.GetBytes(source);
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
}
