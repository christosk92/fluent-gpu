using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A detached pop-out presents through its OWN swapchain's direct route (<c>SubmitDrawList(..., target)</c>), never through
/// <c>SubmitComposite</c>: the composite route draws into the PRIMARY back buffer, waits on and drains the main window's
/// frame-latency credit, and numbers its tile surfaces into the one device-wide tile pool the main window also uses (F090 /
/// F229). The seam now carries the target so a backend can reject the misroute, and every composite frame carries the
/// identity of the slice table that numbered its surfaces. Serial: it constructs several hosts (process-static seams).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DetachedSubmitRouteTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    /// <summary>Something to paint, so a child frame has a non-empty draw list.</summary>
    private sealed class PaintedRoot : Component
    {
        public override Element Render() => new BoxEl { Grow = 1f, Fill = ColorF.FromRgba(20, 60, 120) };
    }

    private sealed class Pair : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessGpuDevice Device { get; } = new() { RejectNonPrimaryComposite = true };
        public HeadlessWindow ChildWindow { get; }
        public AppHost Parent { get; }
        public AppHost Child { get; }

        public Pair(Component childRoot)
        {
            var strings = new StringTable();
            var parentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            parentWindow.Show();
            Parent = new AppHost(App, parentWindow, Device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Parent.RunFrame();
            ChildWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            ChildWindow.Show();
            Child = new AppHost(App, ChildWindow, Device, new HeadlessFontSystem(strings), strings, childRoot,
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
            Parent.AdoptDetachedChild(Child);
        }

        public void Dispose()
        {
            Parent.Dispose();
            App.Dispose();
        }
    }

    [Fact]
    public void ChooseSubmitRoute_ADetachedChild_TakesTheDirectRoute_AndThePrimaryComposites()
    {
        Assert.Equal(AppHost.SubmitRoute.DirectOwnSwapchain, AppHost.ChooseSubmitRoute(isDetachedChild: true));
        Assert.Equal(AppHost.SubmitRoute.Composite, AppHost.ChooseSubmitRoute(isDetachedChild: false));
    }

    [Fact]
    public void ChildFrames_PresentThroughTheirOwnSwapchain_AndNeverComposite()
    {
        using var pair = new Pair(new PaintedRoot());
        var device = pair.Device;
        Assert.Equal(2, device.CreatedSwapchains.Count);
        var primary = device.CreatedSwapchains[0];
        var childTarget = device.CreatedSwapchains[1];
        int composites = device.CompositeFrameCount;
        Assert.True(composites >= 1);   // the parent's own first frame composited into the primary
        Assert.Equal(pair.Parent.UiSliceTable.OwnerId, device.LastCompositeOwner);
        Assert.Equal(0, device.DirectSubmitCount);

        for (int i = 0; i < 4; i++) pair.Parent.TickDetachedHosts();   // child frames only; the parent does not run

        Assert.Equal(composites, device.CompositeFrameCount);          // the child never reached SubmitComposite
        Assert.True(device.DirectSubmitCount >= 1);                     // ... it drew straight into its own swapchain
        Assert.Same(childTarget, device.LastDirectTarget);
        Assert.NotSame(primary, device.LastDirectTarget);
        Assert.True(childTarget.PresentCount >= 1);
    }

    [Fact]
    public void ChildFrames_DoNotPresentThePrimarySwapchain()
    {
        using var pair = new Pair(new PaintedRoot());
        var primary = pair.Device.CreatedSwapchains[0];
        int primaryPresents = primary.PresentCount;
        for (int i = 0; i < 4; i++) pair.Parent.TickDetachedHosts();
        Assert.Equal(primaryPresents, primary.PresentCount);
    }

    [Fact]
    public void HeadlessDevice_RejectsACompositeIntoANonPrimaryTarget_AndAcceptsThePrimary()
    {
        var device = new HeadlessGpuDevice { RejectNonPrimaryComposite = true };
        var primary = device.CreateSwapchain(new SwapchainDesc(default, new Size2(64, 64)));
        var secondary = device.CreateSwapchain(new SwapchainDesc(default, new Size2(64, 64)));

        Assert.Throws<InvalidOperationException>(() =>
        {
            var frame = new CompositeFrame(default, ReadOnlySpan<SliceRow>.Empty, ReadOnlySpan<byte>.Empty,
                ReadOnlySpan<TileRaster>.Empty, ReadOnlySpan<TilePlacement>.Empty, ReadOnlySpan<CompositeItem>.Empty, PresentParams.Full);
            device.SubmitComposite(in frame, secondary);
        });
        Assert.Equal(0, device.CompositeFrameCount);

        var ok = new CompositeFrame(default, ReadOnlySpan<SliceRow>.Empty, ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<TileRaster>.Empty, ReadOnlySpan<TilePlacement>.Empty, ReadOnlySpan<CompositeItem>.Empty, PresentParams.Full);
        device.SubmitComposite(in ok, primary);
        Assert.Equal(1, device.CompositeFrameCount);
    }

    [Fact]
    public void SliceTables_HaveDistinctNonZeroOwnerIds_AndFramesCarryTheirTablesId()
    {
        var a = new SliceTable(4, 4, 4);
        var b = new SliceTable(4, 4, 4);
        Assert.NotEqual(0, a.OwnerId);
        Assert.NotEqual(0, b.OwnerId);
        Assert.NotEqual(a.OwnerId, b.OwnerId);

        var device = new HeadlessGpuDevice { RejectNonPrimaryComposite = true };
        var primary = device.CreateSwapchain(new SwapchainDesc(default, new Size2(64, 64)));
        var stamped = new CompositeFrame(default, ReadOnlySpan<SliceRow>.Empty, ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<TileRaster>.Empty, ReadOnlySpan<TilePlacement>.Empty, ReadOnlySpan<CompositeItem>.Empty, PresentParams.Full,
            ReadOnlySpan<SliceSpan>.Empty, ReadOnlySpan<PushLayerCmd>.Empty, Span<byte>.Empty, ownerToken: b.OwnerId);
        Assert.Equal(b.OwnerId, stamped.OwnerToken);
        device.SubmitComposite(in stamped, primary);
        Assert.Equal(b.OwnerId, device.LastCompositeOwner);
    }

    [Fact]
    public void DrawListHash_IsContentSensitive_AndLengthPrefixed()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        ulong[] keys = [7UL, 9UL];
        ulong h = AppHost.DrawListHash(bytes, keys);
        Assert.Equal(h, AppHost.DrawListHash(bytes, keys));                       // deterministic
        byte[] changed = (byte[])bytes.Clone();
        changed[10] ^= 1;                                                         // a tail byte (past the last whole word)
        Assert.NotEqual(h, AppHost.DrawListHash(changed, keys));
        Assert.NotEqual(h, AppHost.DrawListHash(bytes, [7UL, 10UL]));             // a sort key
        Assert.NotEqual(AppHost.DrawListHash(bytes, [7UL]), AppHost.DrawListHash(bytes, [7UL, 0UL]));        // keys length is part of the hash
    }
}
