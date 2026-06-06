namespace FluentGpu.Foundation;

public enum NativeHandleKind : byte { None = 0, Hwnd = 1, NsView = 2, Headless = 3 }

/// <summary>POD across the PAL/RHI seam — never a boxed <c>object</c>. The D3D12 leaf switches on <see cref="Kind"/>.</summary>
public readonly record struct NativeHandle(nint Value, NativeHandleKind Kind)
{
    public static NativeHandle None => default;
}

public readonly record struct CursorId(int Value)
{
    public static CursorId Arrow => default;     // 0 = system arrow
    public static CursorId IBeam => new(1);
    public static CursorId Hand => new(2);
}
