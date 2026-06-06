using FluentGpu.Foundation;

namespace FluentGpu.Pal;

public enum InputKind : byte { PointerMove = 1, PointerDown = 2, PointerUp = 3, Key = 4 }

/// <summary>POD input event drained from the host-owned ring once per frame (no C# events across the seam).</summary>
public readonly record struct InputEvent(InputKind Kind, Point2 PositionPx, int Button, int KeyCode);

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

public readonly record struct WindowDesc(string Title, Size2 SizePx, float Scale);

public interface IPlatformWindow : IDisposable
{
    NativeHandle Handle { get; }
    Size2 ClientSizePx { get; }
    float Scale { get; }

    /// <summary>Drain queued OS input/window events into the ring (once per frame).</summary>
    int PumpInto(InputEventRing ring);

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
