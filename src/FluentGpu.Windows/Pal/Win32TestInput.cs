namespace FluentGpu.Pal.Windows;

/// <summary>Wire format of the <c>--fg test-input</c> private window message (<c>RegisterWindowMessage("FluentGpu.TestInput")</c>),
/// which lets an out-of-process end-to-end driver feed a window real pointer input without touching the physical mouse
/// (the engine reads <c>WM_POINTER*</c> through <c>GetPointerInfo</c>, so posted <c>WM_MOUSE*</c> are ignored).
/// <para>wParam: low word = <see cref="Kind"/>; high word = signed wheel notch count (×120 units, only for <see cref="Kind.Wheel"/>).
/// lParam: low word = client X px (signed), high word = client Y px (signed).</para></summary>
internal static class Win32TestInput
{
    internal const string MessageName = "FluentGpu.TestInput";

    /// <summary>The reserved pointer id every test-input event carries: never a real contact (a real mouse is 0, the NC synthesis is
    /// 0xFFFFFFFE), so it cannot collide with or cancel a live pointer.</summary>
    internal const uint PointerId = 0xFFFFFFFD;

    internal enum Kind : ushort { Move = 0, Down = 1, Up = 2, Leave = 3, Wheel = 4, RightDown = 5, RightUp = 6 }

    internal static void Decode(nuint wParam, nint lParam, out Kind kind, out int xPx, out int yPx, out short wheelNotch)
    {
        kind = (Kind)(ushort)(wParam & 0xFFFF);
        wheelNotch = unchecked((short)(ushort)((wParam >> 16) & 0xFFFF));
        xPx = unchecked((short)(ushort)((ulong)lParam & 0xFFFF));
        yPx = unchecked((short)(ushort)(((ulong)lParam >> 16) & 0xFFFF));
    }

    internal static nuint EncodeW(Kind kind, short wheelNotch = 0) => (nuint)(((uint)(ushort)wheelNotch << 16) | (ushort)kind);

    internal static nint EncodeL(int xPx, int yPx) => (nint)(((uint)(ushort)yPx << 16) | (ushort)xPx);
}
