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

/// <summary>Well-known virtual-key codes (Win32 VK_*) used by the slice input router.</summary>
public static class Keys
{
    public const int Tab = 9;
    public const int Enter = 13;
    public const int Escape = 27;
    public const int Space = 32;
    public const int Left = 37, Up = 38, Right = 39, Down = 40;
}
