using FluentGpu.Foundation;

namespace FluentGpu.Rhi;

/// <summary>Per-frame context handed to the device at submit. POD.</summary>
public readonly record struct FrameInfo(Size2 SizePx, float Scale, ColorF Clear);

public readonly record struct SwapchainDesc(NativeHandle PresentTarget, Size2 SizePx);

/// <summary>
/// Graphics-first render hardware interface. Zero COM types cross this seam — generational handles + POD + spans only.
/// <see cref="SubmitDrawList"/> is the PRIMARY hot path: the leaf walks the POD opcode stream with concrete devirtualized
/// types. D3D12 is the reference backend; <c>Rhi.Headless</c> is the test backend; Metal slots in later behind this seam.
/// </summary>
public interface IGpuDevice : IDisposable
{
    string BackendName { get; }
    ISwapchain CreateSwapchain(in SwapchainDesc desc);

    /// <summary>Record + batch + submit the per-frame DrawList. <paramref name="drawList"/> is the POD command stream.</summary>
    void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx);

    /// <summary>Hand decoded PREMULTIPLIED BGRA8 pixels for <paramref name="imageId"/> to the backend (the
    /// media-pipeline §4.1 texture upload). The backend create-or-replaces a resident texture (or atlas page) keyed by
    /// id and samples it from the <c>DrawImage</c> opcode. <paramref name="pbgra8"/> is valid only for this call —
    /// the backend copies it into its texture-staging ring; it is never retained. Rows may not be 256-aligned; the
    /// backend pads. Called once per decode completion, before <see cref="SubmitDrawList"/>.</summary>
    void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h);

    /// <summary>The residency manager evicted <paramref name="imageId"/> — release its GPU texture (deferred behind the
    /// frame fence so an in-flight frame can't read freed memory). No-op if not resident.</summary>
    void EvictImage(int imageId) { }

    /// <summary>Suppress the frame-latency throttle wait at the start of the NEXT <see cref="SubmitDrawList"/> (self-
    /// resetting). The host calls this for a KEEP-ALIVE repaint fired synchronously from inside an OS modal move/size
    /// loop, where the WndProc thread would otherwise block up to a vblank on the latency waitable — injecting the
    /// drag-start/live-resize hitch. Default no-op: only a backend with a present-latency throttle (D3D12) honors it.</summary>
    void SuppressLatencyWaitOnce() { }
}

public interface ISwapchain : IDisposable
{
    Size2 SizePx { get; }
    void Resize(Size2 px);
    void Present();
}
