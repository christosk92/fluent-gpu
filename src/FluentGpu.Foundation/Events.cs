namespace FluentGpu.Foundation;

/// <summary>Keyboard modifier state, captured per input event at the platform pump (Win32 GetKeyState).</summary>
[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>The physical pointer device class an event came from (WinUI PointerDeviceType) — controls touch-vs-mouse
/// behavioral splits (RatingControl hover scale, touch paddings).</summary>
public enum PointerKind : byte { Mouse = 0, Touch = 1, Pen = 2 }

/// <summary>
/// Keyboard event passed to node handlers during tunnel/bubble routing. <see cref="Handled"/> stops propagation.
/// Carries the modifier chord and the auto-repeat flag (Win32 lParam bit 30) so editors can do Ctrl+arrow word
/// navigation and Shift+arrow selection without re-querying the keyboard.
/// </summary>
public sealed class KeyEventArgs
{
    public int KeyCode;
    public KeyModifiers Mods;
    public bool IsRepeat;
    public bool Handled;
    public KeyEventArgs(int keyCode) => KeyCode = keyCode;
    public KeyEventArgs(int keyCode, KeyModifiers mods, bool isRepeat = false)
    {
        KeyCode = keyCode; Mods = mods; IsRepeat = isRepeat;
    }

    public bool Shift => (Mods & KeyModifiers.Shift) != 0;
    public bool Ctrl => (Mods & KeyModifiers.Ctrl) != 0;
    public bool Alt => (Mods & KeyModifiers.Alt) != 0;
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

/// <summary>
/// Position-aware pointer-press payload for <c>OnPointerPressed</c>: local coords + the click count (1/2/3 — the
/// dispatcher tracks double/triple-click timing and slop), the modifier chord, the button (0=left 1=right 2=middle)
/// and the device kind. Allocated only on an actual press (cold user-gesture edge).
/// </summary>
public sealed class PointerEventArgs
{
    public Point2 Local;
    public byte ClickCount = 1;
    public KeyModifiers Mods;
    public byte Button;
    public PointerKind Kind;
    public bool Handled;
}

/// <summary>
/// Drag-reorder lifecycle payload for <c>OnDragStarted</c>/<c>OnDragDelta</c>/<c>OnDragCompleted</c>: the pointer in
/// the dragged node's CURRENT box (<see cref="Local"/> stays ≈ the grab offset) and in window space, the accumulated
/// gesture translation since the arming press, and the smoothed pointer velocity (px/s, ~50ms EMA) for flick/settle
/// decisions. ONE instance is reused for the whole gesture (0 steady-state alloc per move) — handlers must copy
/// fields they keep, never hold the reference.
/// </summary>
public sealed class DragEventArgs
{
    /// <summary>Pointer position in the dragged node's CURRENT (moving) box.</summary>
    public Point2 Local;
    /// <summary>Pointer position in window space.</summary>
    public Point2 Absolute;
    /// <summary>Accumulated translation since the arming press — feed to <c>ReorderList.Update</c>.</summary>
    public float TotalDx, TotalDy;
    /// <summary>Smoothed pointer velocity (px/s; ~50ms exponential-moving-average horizon).</summary>
    public float VelocityX, VelocityY;
    public KeyModifiers Mods;
    public PointerKind Kind;
}

/// <summary>A keyboard accelerator chord (WinUI KeyboardAccelerator): <see cref="Key"/> + <see cref="Mods"/> invoke the
/// owning node's click handler from anywhere (dispatched after focused routing leaves the key unhandled).</summary>
public readonly record struct KeyAccelerator(int Key, KeyModifiers Mods);

/// <summary>Well-known virtual-key codes (Win32 VK_*) used by the input router.</summary>
public static class Keys
{
    public const int Back = 8;
    public const int Tab = 9;
    public const int Enter = 13;
    public const int Shift = 16, Ctrl = 17, Alt = 18;
    public const int Pause = 19, CapsLock = 20;
    public const int Escape = 27;
    public const int Space = 32;
    public const int PageUp = 33, PageDown = 34;
    public const int End = 35, Home = 36;
    public const int Left = 37, Up = 38, Right = 39, Down = 40;
    public const int PrintScreen = 44, Insert = 45, Delete = 46;
    // 0-9 (VK '0'..'9' == ASCII)
    public const int D0 = 48, D1 = 49, D2 = 50, D3 = 51, D4 = 52, D5 = 53, D6 = 54, D7 = 55, D8 = 56, D9 = 57;
    // A-Z (VK 'A'..'Z' == ASCII)
    public const int A = 65, B = 66, C = 67, D = 68, E = 69, F = 70, G = 71, H = 72, I = 73, J = 74, K = 75, L = 76,
                     M = 77, N = 78, O = 79, P = 80, Q = 81, R = 82, S = 83, T = 84, U = 85, V = 86, W = 87, X = 88,
                     Y = 89, Z = 90;
    public const int LeftWin = 91, RightWin = 92;
    /// <summary>The dedicated context-menu key (VK_APPS) — opens the focused element's context flyout.</summary>
    public const int Apps = 93;
    public const int F1 = 112, F2 = 113, F3 = 114, F4 = 115, F5 = 116, F6 = 117, F7 = 118, F8 = 119,
                     F9 = 120, F10 = 121, F11 = 122, F12 = 123;
    // Gamepad (VK_GAMEPAD_*) — translated by the dispatcher to activation/cancel/XY-focus.
    public const int GamepadA = 195, GamepadB = 196, GamepadX = 197, GamepadY = 198;
    public const int GamepadDPadUp = 203, GamepadDPadDown = 204, GamepadDPadLeft = 205, GamepadDPadRight = 206;
    public const int GamepadLeftThumbUp = 211, GamepadLeftThumbDown = 212, GamepadLeftThumbRight = 213, GamepadLeftThumbLeft = 214;

    /// <summary>True for VK 'A'..'Z' / '0'..'9' — the access-key (Alt mnemonic) candidates.</summary>
    public static bool IsAccessKeyCandidate(int vk) => (vk >= A && vk <= Z) || (vk >= D0 && vk <= D9);
}
