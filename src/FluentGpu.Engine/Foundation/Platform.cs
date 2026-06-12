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
    public static CursorId SizeWE => new(3);     // horizontal resize (splitter)
    public static CursorId SizeNS => new(4);     // vertical resize
    public static CursorId SizeNWSE => new(5);
    public static CursorId SizeNESW => new(6);
    public static CursorId SizeAll => new(7);    // pan / move
    public static CursorId Cross => new(8);      // precision select (ColorPicker spectrum)
    public static CursorId No => new(9);         // drop-forbidden
    public static CursorId Wait => new(10);
}
