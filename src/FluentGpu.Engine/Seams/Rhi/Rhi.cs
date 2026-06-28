using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Rhi;

/// <summary>Per-frame context handed to the device at submit. POD.</summary>
// Damage = the union (device/DIP px) of nodes whose transform moved this frame — the region-aware invalidation set for
// the in-app acrylic backdrop cache (default empty ⇒ nothing moved ⇒ reuse every cached blur). See AcrylicBackdropMath.
public readonly record struct FrameInfo(Size2 SizePx, float Scale, ColorF Clear, RectF Damage = default);

/// <summary><paramref name="DesktopAcrylic"/> = back this composited popup with a true desktop-sampling acrylic
/// (Windows.UI.Composition host backdrop) tinted by <paramref name="AcrylicTint"/> — the WinUI MenuFlyout material,
/// reached without the Windows App SDK. Ignored by backends that don't support it (they fall back to a plain swapchain).</summary>
public readonly record struct SwapchainDesc(NativeHandle PresentTarget, Size2 SizePx, bool Composited = false,
    bool DesktopAcrylic = false, ColorF AcrylicTint = default, float CornerRadiusPx = 0f);

/// <summary>
/// Graphics-first render hardware interface. Zero COM types cross this seam — generational handles + POD + spans only.
/// <see cref="SubmitDrawList"/> is the PRIMARY hot path: the leaf walks the POD opcode stream with concrete devirtualized
/// types. D3D12 is the reference backend; <c>Rhi.Headless</c> is the test backend; Metal slots in later behind this seam.
/// </summary>
public interface IGpuDevice : IDisposable
{
    string BackendName { get; }
    /// <summary>True when <see cref="CreateSwapchain"/> may be called for secondary popup targets and
    /// <see cref="SubmitDrawList(ReadOnlySpan{byte}, ReadOnlySpan{ulong}, in FrameInfo, ISwapchain)"/> can render to
    /// those targets. Headless and D3D12 support this; future backends can opt in without changing the host.</summary>
    bool SupportsSecondarySwapchains => false;
    ISwapchain CreateSwapchain(in SwapchainDesc desc);

    /// <summary>Record + batch + submit the per-frame DrawList. <paramref name="drawList"/> is the POD command stream.</summary>
    void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx);

    /// <summary>Diagnostic: wall-time (ms) spent BLOCKED on GPU fences — the frame fence (<c>WaitForFrame</c>) plus the
    /// present-latency waitable — inside the most recent <see cref="SubmitDrawList(ReadOnlySpan{byte}, ReadOnlySpan{ulong}, in FrameInfo)"/>.
    /// This UI-thread stall is what dominates measured "submit" time today (the render-thread seam will move it off the UI
    /// thread). The host folds it into <c>FrameStats.FenceWaitMs</c>. Default 0 for backends that don't block on a GPU fence
    /// (headless), so it reads as "no stall" rather than missing.</summary>
    double LastFenceWaitMs => 0;

    /// <summary>True when decoded image pixels are staged but not yet copied to their resident GPU texture, or when
    /// transient upload resources are awaiting fence-gated release. The host must NOT elide that submit, or the texture
    /// stays empty and deferred upload memory can remain resident until unrelated UI work happens. Default false (a
    /// headless/synchronous backend has nothing pending).</summary>
    bool HasPendingUploads => false;

    /// <summary>Record + batch + submit to a specific swapchain target (windowed popup HWNDs). Backends without
    /// secondary-swapchain support fall back to the primary target via the legacy overload.</summary>
    void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx, ISwapchain target)
        => SubmitDrawList(drawList, sortKeys, in ctx);

    /// <summary>Hand decoded PREMULTIPLIED BGRA8 pixels for <paramref name="imageId"/> to the backend (the
    /// media-pipeline §4.1 texture upload). The backend create-or-replaces a resident texture (or atlas page) keyed by
    /// id and samples it from the <c>DrawImage</c> opcode. <paramref name="pbgra8"/> is valid only for this call —
    /// the backend copies it into its texture-staging ring; it is never retained. Rows may not be 256-aligned; the
    /// backend pads. Called once per decode completion, before <see cref="SubmitDrawList"/>.</summary>
    void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h);

    /// <summary>Admission-aware image upload. Existing backends remain source-compatible through this default, which
    /// delegates to <see cref="UploadImage"/> and assumes success; bounded backends override it so the cache never marks
    /// a rejected texture Ready.</summary>
    ImageUploadResult TryUploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h)
    {
        UploadImage(imageId, pbgra8, w, h);
        return ImageUploadResult.Accepted;
    }

    /// <summary>The residency manager evicted <paramref name="imageId"/> — release its GPU texture (deferred behind the
    /// frame fence so an in-flight frame can't read freed memory). No-op if not resident.</summary>
    void EvictImage(int imageId) { }

    /// <summary>Suppress the frame-latency throttle wait at the start of the NEXT <see cref="SubmitDrawList"/> (self-
    /// resetting). The host calls this for a KEEP-ALIVE repaint fired synchronously from inside an OS modal move/size
    /// loop, where the WndProc thread would otherwise block up to a vblank on the latency waitable — injecting the
    /// drag-start/live-resize hitch. Default no-op: only a backend with a present-latency throttle (D3D12) honors it.</summary>
    void SuppressLatencyWaitOnce() { }

    /// <summary>Present the NEXT frame at SyncInterval 0 instead of the steady-state vsync interval (self-resetting). The
    /// host calls this for a KEEP-ALIVE repaint fired synchronously from inside an OS modal move/size loop: on a composited
    /// flip swapchain interval-0 is a cheap, tear-free hand-off (DWM still composites at vblank) so the WndProc thread isn't
    /// blocked up to a vblank in Present — the live-resize/move hitch the latency-wait skip alone doesn't remove. Default
    /// no-op: only a backend that presents to a real swapchain (D3D12) honors it.</summary>
    void SuppressVsyncOnce() { }
}

/// <summary>Geometry + motion parameters for a desktop-acrylic windowed popup's composition chrome. All px, relative to
/// the (shadow-inset-inflated) popup window. <paramref name="ContentRectPx"/> is the rounded menu plate inside the
/// window's shadow margins; the acrylic is rounded to it and the open slide = <c>ContentRectPx.H * ClosedRatio</c>.
/// <paramref name="OpensUp"/> = menu opens upward (anchored at its bottom). <paramref name="ClosedRatio"/> follows
/// WinUI's MenuPopupThemeTransition (0.5 root menu, 0.67 cascaded submenu).</summary>
public readonly record struct PopupChromeMetrics(
    RectF ContentRectPx, bool OpensUp, float ClosedRatio, float CornerRadiusPx, float BorderPx);

public interface ISwapchain : IDisposable
{
    Size2 SizePx { get; }
    void Resize(Size2 px);
    void Present();

    /// <summary>Configure the windowed popup's composition chrome (rounded acrylic content rect + outer shadow) for the
    /// current placement. Called on each placement before show. Default no-op: only a backdrop-backed backend honors it.</summary>
    void ConfigurePopupChrome(in PopupChromeMetrics m) { }

    /// <summary>Play the open motion: the whole composition root (acrylic + content + shadow) slides from the anchor edge
    /// to rest over 250ms cubic-bezier(0,0,0,1), no opacity fade — WinUI MenuPopupThemeTransition. Uses the configured
    /// metrics. Idempotent — runs once per open.</summary>
    void AnimatePopupOpen() { }

    /// <summary>Play the close motion: fade the WHOLE composition root (so the acrylic fades too, not just the engine
    /// content) opacity 1→0 over 83ms. The host keeps the window alive until <see cref="PopupAnimating"/> clears.</summary>
    void AnimatePopupClose() { }

    /// <summary>True while this popup's open/close motion is mid-flight. The host ORs this into <c>WakeReasons.PopupAnim</c>
    /// so the frame loop keeps presenting the popup until the composition animation commits + settles (and, for close,
    /// defers disposal until it clears).</summary>
    bool PopupAnimating => false;
}
