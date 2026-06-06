using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Pal.Headless;

public sealed class HeadlessPlatformApp : IPlatformApp
{
    public IPlatformWindow CreateWindow(in WindowDesc desc) => new HeadlessWindow(desc);
    public void Dispose() { }
}

/// <summary>Synthetic window: the test harness pushes input via <see cref="QueueInput"/>; the host drains it.</summary>
public sealed class HeadlessWindow : IPlatformWindow
{
    private readonly Queue<InputEvent> _queue = new();

    public HeadlessWindow(in WindowDesc desc)
    {
        ClientSizePx = desc.SizePx;
        Scale = desc.Scale <= 0 ? 1f : desc.Scale;
    }

    public NativeHandle Handle => new(0, NativeHandleKind.Headless);
    public Size2 ClientSizePx { get; }
    public float Scale { get; }
    public CursorId LastCursor { get; private set; }
    public bool Shown { get; private set; }

    public void QueueInput(in InputEvent e) => _queue.Enqueue(e);

    public int PumpInto(InputEventRing ring)
    {
        int n = 0;
        while (_queue.Count > 0) { ring.Write(_queue.Dequeue()); n++; }
        return n;
    }

    public void SetCursor(CursorId id) => LastCursor = id;
    public void SetTitle(StringId title) { }
    public void Show() => Shown = true;
    public void Dispose() { }
}
