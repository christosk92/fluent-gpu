using FluentGpu.Foundation;

namespace FluentGpu.Pal;

/// <summary>
/// A POD, opaque-across-the-seam handle for one composited video surface. <c>0</c> == none. The portable core and
/// app never see the backing <c>IDCompositionVisual</c>/<c>IDCompositionSurface</c> — only this id. (Windows maps
/// <see cref="Value"/> → a child-visual slot; macOS would map it → an <c>AVPlayerLayer</c>.)
/// </summary>
public readonly record struct VideoSurfaceId(uint Value)
{
    public bool IsNone => Value == 0;
}

/// <summary>
/// The video-compositing PAL seam (<c>docs/plans/video-compositing-spine-design.md §4</c>,
/// <c>docs/plans/video-phase1-plan.md §4</c> — DRM-free spine). Composites externally-produced video as a sibling
/// DirectComposition visual the engine never paints into: a child visual z-BELOW the UI swapchain visual, revealed
/// through a premultiplied-0 hole-punch in the UI back buffer. The portable core references only this interface (it
/// stays TerraFX-free); every <c>IDCompositionVisual</c>/<c>IDCompositionSurface</c> ComPtr lives behind the Windows
/// leaf (<c>FluentGpu.Windows/Pal/DCompVideoPresenter.cs</c>), render-thread-confined like the rest of the device.
/// </summary>
/// <remarks>
/// Threading: every method is executed on the RENDER/submit thread (phase 11 for <see cref="Place"/>/
/// <see cref="SetVisible"/>/<see cref="Commit"/>); in the single-thread Phase-1 build that thread is the UI thread
/// (quarantine 0). Steady-state <see cref="Place"/>/<see cref="Commit"/> are 0 managed alloc (POD args, a
/// preallocated id→visual table); the only allocations are cold ComPtr roots on first surface creation.
/// </remarks>
public interface IVideoPresenter
{
    /// <summary>Allocate a video child visual (hidden until <see cref="BindSurfaceHandle"/> + <see cref="Place"/>).</summary>
    VideoSurfaceId CreateSurface();

    /// <summary>
    /// The surface-handoff seam. An external owner produces a DirectComposition surface HANDLE
    /// (<c>DCompositionCreateSurfaceHandle</c> on its side); the Windows impl wraps it via
    /// <c>IDCompositionDevice::CreateSurfaceFromHandle</c> and binds it as the child visual's content. Phase 1
    /// (DRM-free) passes an UNPROTECTED handle; the DRM phase passes a PROTECTED handle here — and NOTHING else in
    /// this seam or the renderer changes. This is the single DRM attach point.
    /// </summary>
    void BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle);

    /// <summary>
    /// Position/clip the child visual to <paramref name="deviceRect"/> (device px) at draw order <paramref name="z"/>.
    /// Queued for the frame's <see cref="Commit"/>; the matching hole-punch in the UI back buffer is the source of
    /// truth for the visible rect. <paramref name="opacity"/> is retained metadata (the graded reveal is done UI-side).
    /// </summary>
    void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z);

    /// <summary>Clip the placed content to a device-space viewport. This is distinct from <see cref="Place"/> so
    /// UniformToFill can place an oversized, centered frame and crop it to the element without distortion.</summary>
    void SetViewport(VideoSurfaceId id, RectF deviceRect) { }

    /// <summary>Show/hide the child visual (queued for the next <see cref="Commit"/>).</summary>
    void SetVisible(VideoSurfaceId id, bool visible);

    /// <summary>
    /// The externally-produced content's native pixel size (e.g. the decoder swapchain's 1920×1080). The presenter scales
    /// that content to exactly fill the <see cref="Place"/> device rect (the rect is already aspect-fit by the caller), so
    /// the frame fits its container instead of being shown 1:1 and cropped. A <c>0×0</c> size means "unknown yet" — the
    /// presenter then places 1:1 (the pre-scale fallback). Default no-op so headless/test presenters need not implement it.
    /// </summary>
    void SetContentSize(VideoSurfaceId id, uint width, uint height) { }

    /// <summary>Tear down one surface (removes the child visual, releases its content). Cold path.</summary>
    void Destroy(VideoSurfaceId id);

    /// <summary>
    /// Flush all queued <see cref="Place"/>/<see cref="SetVisible"/>/<see cref="BindSurfaceHandle"/> mutations into
    /// one <c>IDCompositionDevice::Commit</c> — the per-frame commit at phase 11 (the "two-clock tear" lock: the hole
    /// rides the same frame-turn's <c>Present</c>). No-op when nothing is dirty.
    /// </summary>
    void Commit();
}
