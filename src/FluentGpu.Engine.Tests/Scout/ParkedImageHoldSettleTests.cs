using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A hold-last-good swap on a parked KeepAlive page: the page's bound cover source changes while the page is detached,
/// so the hold's pin is skipped (the node is unreachable) and the new decode lands with no node tracking it. Re-pinning
/// a Ready entry on return raises no status event, so the hold must be settled by the reactivation itself, not left
/// drawing the old (or a low-res stand-in) picture until the source changes again.
/// Serial: it constructs a host (process-static seams).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ParkedImageHoldSettleTests
{
    /// <summary>Completes an id only once the test releases it, so the landing happens while the page is parked.</summary>
    private sealed class ReleaseDecoder : IImageDecoder
    {
        private readonly Dictionary<int, (int W, int H)> _pending = new();
        private readonly HashSet<int> _release = new();
        private byte[] _scratch = Array.Empty<byte>();

        public int LastBeginId { get; private set; }
        public void Release(int id) => _release.Add(id);

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            LastBeginId = id;
            _pending[id] = (Math.Max(1, targetW), Math.Max(1, targetH));
            return true;
        }

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            if (_release.Count == 0) return;
            foreach (int id in new List<int>(_release))
            {
                _release.Remove(id);
                if (!_pending.Remove(id, out var wh)) continue;
                int bytes = wh.W * wh.H * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                _scratch.AsSpan(0, bytes).Fill(0xFF);
                onPixels(id, _scratch.AsSpan(0, bytes), wh.W, wh.H);
                onComplete(id, true, wh.W, wh.H, ImageFailureKind.None, 1);
            }
        }
    }

    private sealed class Root : Component
    {
        public readonly Signal<string> Page = new("a");
        public readonly Signal<string> Src = new("scout/parked-hold-a");

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 200f, Height = 200f,
            Children =
            [
                Flow.KeepAlive(() => Page.Value, static k => k,
                    k => k == "a" ? Embed.Comp(() => new CoverPage(this)) : new BoxEl { Width = 200f, Height = 200f }),
            ],
        };
    }

    private sealed class CoverPage(Root root) : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = 200f, Height = 200f,
            Children = [new ImageEl { Source = root.Src, Width = 64f, Height = 64f }],   // bound: fires while parked
        };
    }

    [Fact]
    public void AHoldThatSettlesWhileParked_CommitsWhenThePageComesBack()
    {
        var strings = new StringTable();
        var dec = new ReleaseDecoder();
        var cache = new ImageCache(dec);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scout-parked-image-hold", new Size2(200, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var root = new Root();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, root, cache);

        host.RunFrame();
        int idA = dec.LastBeginId;
        dec.Release(idA);
        for (int i = 0; i < 20; i++) host.RunFrame();   // A lands and its reveal settles
        Assert.Single(device.LastImages);
        Assert.Equal(idA, device.LastImages[0].ImageId);

        root.Page.Value = "b";                          // park + detach the cover page
        for (int i = 0; i < 3; i++) host.RunFrame();

        root.Src.Value = "scout/parked-hold-b";         // the bound source fires on the parked page: hold-last-good on A
        host.RunFrame();
        int idB = dec.LastBeginId;
        Assert.NotEqual(idA, idB);
        dec.Release(idB);
        for (int i = 0; i < 5; i++) host.RunFrame();    // B lands while the page is still parked
        Assert.Equal(ImageState.Ready, cache.StateOf(new ImageHandle(idB)));

        root.Page.Value = "a";                          // back: the hold must settle onto B
        for (int i = 0; i < 40; i++) host.RunFrame();   // past the swap dissolve + release slack
        Assert.Single(device.LastImages);
        Assert.Equal(idB, device.LastImages[0].ImageId);
        Assert.Equal(0, cache.RefsOf(new ImageHandle(idA)));
        Assert.Equal(1, cache.RefsOf(new ImageHandle(idB)));
    }
}
