using FluentGpu.Foundation;

namespace FluentGpu.Rhi.Headless;

/// <summary>What the headless backend can say about a frame without rasterizing it: which submit drew it (the device's frame
/// number), how many rounded rects it held, the fill of the first, and a hash of every rect fill and glyph run. Two frames of
/// different trees differ in <see cref="Hash"/>; a dissolve's two sources are told apart by it.</summary>
public readonly record struct HeadlessFrameSummary(int FrameNumber, int RectCount, ColorF FirstFill, ulong Hash);

/// <summary>The headless <see cref="IFrameSnapshot"/>: the <see cref="HeadlessFrameSummary"/> of the frame it captured, plus a
/// live count on its swapchain so a suite can prove the snapshot is released when the freeze ends.</summary>
public sealed class HeadlessFrameSnapshot : IFrameSnapshot
{
    private readonly HeadlessSwapchain _owner;

    internal HeadlessFrameSnapshot(HeadlessSwapchain owner, long id, Size2 size, in HeadlessFrameSummary summary)
    {
        _owner = owner; Id = id; SizePx = size; Summary = summary;
    }

    public long Id { get; }
    public Size2 SizePx { get; }
    public bool PremultipliedAlpha => true;
    public HeadlessFrameSummary Summary { get; }
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _owner.NoteSnapshotReleased();
    }
}

/// <summary>How one headless present reached the glass: a normal flip, a held (not shown) one, or a dissolve.</summary>
public enum HeadlessPresentKind : byte { Normal, Held, Dissolve }

/// <summary>One recorded present (opt-in: <see cref="HeadlessSwapchain.RecordPresents"/>). <see cref="Shown"/> is the frame that
/// drew into the back buffer; a Held present is NOT shown, so for it <see cref="Shown"/> is the frame that was refused. A
/// Dissolve shows <c>(1-T)·From + T·Shown</c>.</summary>
public readonly record struct HeadlessPresentRecord(HeadlessPresentKind Kind, HeadlessFrameSummary Shown, HeadlessFrameSummary From, float T, long SnapshotId);
