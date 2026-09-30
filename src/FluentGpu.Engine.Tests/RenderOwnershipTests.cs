using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

#if FGGUARD
/// <summary>
/// Headless coverage for the render-ownership token (INCIDENT 2026-09,
/// detached-window-render-isolation-implementation.md §2.1/§2.2/§7.2): a device-mutating call must be legal on the
/// render thread OR on the UI thread while it holds the render loop parked (<see cref="RenderThread.Quiesce"/> …
/// <see cref="RenderThread.Resume"/>) or after the loop joined (<see cref="RenderThread.Dispose"/>), and illegal on an
/// unparked UI thread. <see cref="HeadlessGpuDevice"/> mirrors D3D12Device's <c>AssertDeviceOwner</c>/<c>_renderConfined</c>
/// gate so this is exercised without a real GPU.
/// </summary>
/// <remarks>FGGUARD-only: the asserts are <c>[Conditional("FGGUARD")]</c> and erased from Release, so there is nothing
/// to observe there.</remarks>
public sealed class RenderOwnershipTests
{
    [Fact]
    public void AssertRenderOwner_ThrowsOnUnparkedUi_PassesUnderQuiesce_AndAfterJoin() => OnFreshThread(() =>
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        Assert.Throws<ThreadConfinementViolation>(() => ThreadGuard.AssertRenderOwner());

        var rt = new RenderThread(new SceneFramePublisher(), _ => { }, async: false);
        rt.Quiesce();
        try { ThreadGuard.AssertRenderOwner(); }
        finally { rt.Resume(); }
        Assert.Throws<ThreadConfinementViolation>(() => ThreadGuard.AssertRenderOwner());

        rt.Dispose();
        ThreadGuard.AssertRenderOwner();   // adopted after the join
        rt.Quiesce(); rt.Resume();          // still legal after adopt (RenderThreadLifecycleTests does this)
    });

    [Fact]
    public void HeadlessDevice_CreateSwapchain_TripsOffTheRenderOwner_WhenConfined() => OnFreshThread(() =>
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var device = new HeadlessGpuDevice();
        device.MarkRenderConfined();
        Assert.Throws<ThreadConfinementViolation>(() => device.CreateSwapchain(new SwapchainDesc(default, new Size2(8, 8))));

        var rt = new RenderThread(new SceneFramePublisher(), _ => { }, async: false);
        rt.Quiesce();
        try { Assert.NotNull(device.CreateSwapchain(new SwapchainDesc(default, new Size2(8, 8)))); }
        finally { rt.Resume(); rt.Dispose(); }
    });

    /// <summary>The ownership token is [ThreadStatic] and a joined <see cref="RenderThread.Dispose"/> adopts it for good,
    /// so a pooled xUnit thread that ran another test's Dispose would already own it. Each fact gets a fresh thread.</summary>
    static void OnFreshThread(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() => { try { body(); } catch (Exception e) { failure = e; } });
        t.Start();
        t.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
#endif
