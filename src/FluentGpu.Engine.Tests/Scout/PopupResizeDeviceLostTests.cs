using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A popup's resize is drained on the render thread at the top of a turn (the pre-turn, the children's drain, the top of the
/// submit), outside any catch: a ResizeBuffers on a removed device threw out of the render loop and killed the process instead of
/// waiting for the recovery rendezvous. A recorded loss now skips that one resize, as the host's own resize already did, and the
/// rest of the queue still runs; any other failure is a bug and still throws. Serial: it constructs a host.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class PopupResizeDeviceLostTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    // ResizeBuffers on a removed device: the D3D12 backend's Check throws exactly this.
    private sealed class PopupSwapchain(Size2 size, bool failResize) : ISwapchain
    {
        public int Resizes;
        public Size2 SizePx { get; private set; } = size;
        public void Resize(Size2 px)
        {
            if (failResize) throw new InvalidOperationException("ResizeBuffers failed: 0x887A0005");
            Resizes++;
            SizePx = px;
        }
        public void Present() { }
        public void Dispose() { }
    }

    private sealed class LossDevice : IGpuDevice
    {
        public bool Lost;
        public string BackendName => "popup-resize-loss-fake";
        public ISwapchain CreateSwapchain(in SwapchainDesc desc) => new PopupSwapchain(desc.SizePx, failResize: false);
        public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx) { }
        public void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h) { }
        public bool NoteIfDeviceLost() => Lost;
        public void Dispose() { }
    }

    private static PopupWindowSlot Slot(int token, ISwapchain swapchain)
        => new(token, new HeadlessPopupWindow(new PopupWindowDesc(default, default)), default, PopupWindowMaterial.None) { Swapchain = swapchain };

    private static (HeadlessPlatformApp App, AppHost Host, LossDevice Device) NewHost()
    {
        var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var device = new LossDevice();
        var window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
        return (app, host, device);
    }

    [Fact]
    public void ResizeOnARemovedDevice_IsSkipped_AndTheRestOfTheQueueStillRuns()
    {
        var (app, host, device) = NewHost();
        try
        {
            var lost = new PopupSwapchain(new Size2(100, 50), failResize: true);
            var healthy = new PopupSwapchain(new Size2(100, 50), failResize: false);
            device.Lost = true;   // removed between the UI's post and the render turn's drain
            host.PostPopupResizeForTest(Slot(1, lost), new Size2(140, 80));
            host.PostPopupResizeForTest(Slot(2, healthy), new Size2(160, 90));

            host.DrainPopupRenderActionsForTest();   // threw out of the render loop before the fix

            Assert.Equal(new Size2(100, 50), lost.SizePx);   // skipped: RecoverDevice rebuilds it
            Assert.Equal(1, healthy.Resizes);                // the op queued behind it was not lost
            Assert.Equal(new Size2(160, 90), healthy.SizePx);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void ResizeFailureOnAHealthyDevice_StillThrows()
    {
        var (app, host, _) = NewHost();
        try
        {
            host.PostPopupResizeForTest(Slot(1, new PopupSwapchain(new Size2(100, 50), failResize: true)), new Size2(140, 80));
            Assert.Throws<InvalidOperationException>(host.DrainPopupRenderActionsForTest);   // a genuine bug is never masked
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
