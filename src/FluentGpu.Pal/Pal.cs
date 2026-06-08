using FluentGpu.Foundation;

namespace FluentGpu.Pal;

public enum InputKind : byte { PointerMove = 1, PointerDown = 2, PointerUp = 3, Key = 4, Wheel = 5, Char = 6 }

/// <summary>
/// POD input event drained from the host-owned ring once per frame (no C# events across the seam).
/// <paramref name="ScrollDelta"/> (Wheel only) is in DIP, oriented so positive = scroll toward the content end
/// (offset increases). The platform pump converts WM_MOUSEWHEEL notches → DIP and flips the sign there.
/// </summary>
public readonly record struct InputEvent(InputKind Kind, Point2 PositionPx, int Button, int KeyCode, float ScrollDelta = 0f);

/// <summary>Drained by the host each frame; the window writes POD events into it (move-coalesced).</summary>
public sealed class InputEventRing
{
    private InputEvent[] _buf = new InputEvent[64];
    private int _count;

    public void Write(in InputEvent e)
    {
        if (_count == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
        _buf[_count++] = e;
    }

    public ReadOnlySpan<InputEvent> Drain()
    {
        var span = _buf.AsSpan(0, _count);
        return span;
    }

    public void Clear() => _count = 0;
}

public interface IPlatformApp : IDisposable
{
    IPlatformWindow CreateWindow(in WindowDesc desc);
}

/// <summary><paramref name="Composited"/> = the window is composited with per-pixel alpha (WS_EX_NOREDIRECTIONBITMAP) so a DirectComposition swapchain can show the DWM Mica backdrop through transparent pixels.</summary>
public readonly record struct WindowDesc(string Title, Size2 SizePx, float Scale, bool Composited = false);

public interface IPlatformWindow : IDisposable
{
    NativeHandle Handle { get; }
    Size2 ClientSizePx { get; }
    float Scale { get; }

    /// <summary>Drain queued OS input/window events into the ring (once per frame).</summary>
    int PumpInto(InputEventRing ring);

    /// <summary>
    /// Block until platform work arrives or <paramref name="timeoutMs"/> elapses. Negative timeout means wait indefinitely.
    /// Real windows use this for event-driven idle; headless implementations may return immediately.
    /// </summary>
    void WaitForWork(int timeoutMs);

    /// <summary>
    /// Invoked by the platform when the OS demands an immediate repaint *outside* the app's frame loop —
    /// notably during the modal move/size loop (WM_SIZE/WM_PAINT), which otherwise blocks rendering until mouse-up.
    /// The host wires this to a pump-free paint so the window stays live during a live resize.
    /// </summary>
    Action? PaintRequested { get; set; }

    void SetCursor(CursorId id);                                   // L10 cursor seam
    void SetTitle(StringId title);
    void Show();
}

/// <summary>Versioned external-store-shaped locale seam (modeled on ISystemColors). L9.</summary>
public interface IPlatformLocale
{
    uint Epoch { get; }
}
