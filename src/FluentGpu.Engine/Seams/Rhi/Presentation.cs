namespace FluentGpu.Rhi;

public partial interface IGpuDevice
{
    /// <summary>Render-owner thread: start <paramref name="motion"/> on <paramref name="target"/>'s window as a whole, on the
    /// compositor (<see cref="FluentGpu.Pal.WindowPresentationMotion"/>): its UI composition root and, when the window has one, its
    /// video layer. A target whose composition tree is not bound yet (no first present) takes it with the binding, so a starting
    /// hold applied before the window is shown is in place on its first composed frame. Default: no-op (a backend without a
    /// composition tree shows the window unanimated).</summary>
    void AnimatePresentation(ISwapchain target, in FluentGpu.Pal.WindowPresentationMotion motion) { }

    /// <summary>Render-owner thread, once per render turn of <paramref name="target"/>'s host (before its present): pose a moving
    /// presentation for this moment and commit it. True while it is still moving (the host keeps its render loop ticking).
    /// Default: false.</summary>
    bool TickPresentation(ISwapchain target) => false;
}
