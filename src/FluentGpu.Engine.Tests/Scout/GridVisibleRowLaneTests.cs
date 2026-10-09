using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A virtual grid's visible band is whole rows. The band used to end at the FIRST cell of the row under the bottom edge
/// (VirtualLayoutExtent.IndexAt answers a row's first item), so the other cells of that partly visible row queued their
/// covers in the Overscan lane: behind every Visible decode, dropped under backpressure, and never promoted while the row
/// stayed at the bottom. Serial: it constructs a host (process-static seams).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class GridVisibleRowLaneTests
{
    /// <summary>Records the lane each source was queued at; never completes anything.</summary>
    private sealed class LaneDecoder : IImageDecoder
    {
        private readonly Dictionary<string, ImagePriority> _lane = new();

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            _lane[source] = priority;
            return true;
        }

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) { }
        public void Prioritize(int id, ImagePriority priority) { }
        public ImagePriority? LaneOf(string source) => _lane.TryGetValue(source, out var p) ? p : null;
    }

    // 5 columns of 40 DIP rows in a 150 DIP viewport: rows 0..2 fully visible, row 3 (items 15..19) partly visible.
    private sealed class Grid : Component
    {
        public bool Bound;

        public override Element Render() => new BoxEl
        {
            Width = 200f, Height = 150f,
            Children = [new VirtualListEl
            {
                ItemCount = 200, Width = 200f, Height = 150f,
                ItemLayout = new GridVirtualLayout(5, 40f),
                RenderItem = static i => Cell("grid/" + i),
                RowBind = Bound ? (Func<IReadSignal<int>, Element>)(sig => Cell(Prop.Of(() => "grid/" + sig.Value))) : null,
            }],
        };

        private static Element Cell(Prop<string> source) => new BoxEl
        {
            Width = 40f, Height = 40f,
            Children = [new ImageEl { Source = source, Width = 24f, Height = 24f }],
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryCellOfTheBottomVisibleRow_RequestsTheVisibleLane(bool bound)
    {
        var strings = new StringTable();
        var dec = new LaneDecoder();
        var cache = new ImageCache(dec);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scout-grid-visible-row", new Size2(200, 150), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new Grid { Bound = bound }, cache);
        for (int i = 0; i < 6; i++) host.RunFrame();

        for (int i = 0; i < 20; i++)
            Assert.True(dec.LaneOf("grid/" + i) == ImagePriority.Visible, $"item {i}: {dec.LaneOf("grid/" + i)}");
        // Row 4 starts at 160, below the viewport: the overscan halo keeps its lane.
        for (int i = 20; i < 25; i++)
            Assert.True(dec.LaneOf("grid/" + i) == ImagePriority.Overscan, $"item {i}: {dec.LaneOf("grid/" + i)}");
    }
}
