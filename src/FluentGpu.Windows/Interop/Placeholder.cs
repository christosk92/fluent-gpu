namespace FluentGpu.Win32.Interop;

/// <summary>
/// SCAFFOLD. Owner of the hand-vtable COM layer (see design/subsystems/com-interop.md): vendored ComputeSharp
/// <c>ComPtr&lt;T&gt;</c> + ClangSharp-generated D3D12/DXGI/D2D1 bindings, and the generated DComp/DWrite/UIA vtables
/// (consume = <c>lpVtbl[n]</c> calli; implement = static <c>[UnmanagedCallersOnly(CallConvMemberFunction)]</c> + GCHandle).
/// Empty until the Windows leaves are implemented; the headless slice does not need it.
/// </summary>
public static class Win32InteropMarker;
