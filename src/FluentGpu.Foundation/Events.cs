namespace FluentGpu.Foundation;

/// <summary>
/// Keyboard event passed to node handlers during tunnel/bubble routing. <see cref="Handled"/> stops propagation.
/// (Slice shape; the full engine uses a ref-struct + by-ref delegate and carries modifiers/device/timestamp.)
/// </summary>
public sealed class KeyEventArgs
{
    public int KeyCode;
    public bool Handled;
    public KeyEventArgs(int keyCode) => KeyCode = keyCode;
}

/// <summary>
/// Character (text) input passed to <c>OnCharInput</c> handlers — the layout/IME-resolved Unicode codepoint, distinct
/// from the raw virtual-key of <see cref="KeyEventArgs"/> (Win32 splits WM_KEYDOWN from WM_CHAR; we mirror that).
/// </summary>
public sealed class CharEventArgs
{
    public int Codepoint;
    public bool Handled;
    public CharEventArgs(int codepoint) => Codepoint = codepoint;
}

/// <summary>Well-known virtual-key codes (Win32 VK_*) used by the slice input router.</summary>
public static class Keys
{
    public const int Back = 8;
    public const int Tab = 9;
    public const int Enter = 13;
    public const int Escape = 27;
    public const int Space = 32;
    public const int End = 35, Home = 36;
    public const int Left = 37, Up = 38, Right = 39, Down = 40;
}
