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
/// Hold-last-good settle onto the SAME picture at another decode size (a resize re-bucketing a cover, or a remounted cover
/// drawn from its resident rendition). The new id is Ready on the UI thread before its pixels are drawable on a discrete GPU
/// (the copy-queue batch is submitted inside the very turn that records the commit), so the settle frame must keep the held
/// texture under the new draw with a transparent placeholder, never the node's flat placeholder fill over the cover.
/// Serial: it constructs a host (process-static seams).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class SameSourceSettleTests
{
    /// <summary>Completes an id only once the test releases it, so the hold window is under the test's control.</summary>
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

    private sealed class Cover : Component
    {
        public readonly Signal<float> Px = new(64f);

        public override Element Render() => new BoxEl
        {
            Width = 200, Height = 200,
            Children = [new ImageEl { Source = "scout/same-source-cover", Width = Px.Value, Height = Px.Value }],
        };
    }

    [Fact]
    public void SameSourceSettle_KeepsTheHeldTextureUnderTheNewDecode_NotThePlaceholder()
    {
        var strings = new StringTable();
        var dec = new ReleaseDecoder();
        var cache = new ImageCache(dec);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scout-same-source-settle", new Size2(200, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var cover = new Cover();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, cover, cache);

        host.RunFrame();
        int idA = dec.LastBeginId;
        dec.Release(idA);
        for (int i = 0; i < 20; i++) host.RunFrame();   // A lands and its reveal settles

        cover.Px.Value = 128f;                          // a new decode bucket of the SAME source: hold-last-good
        host.RunFrame();
        int idB = dec.LastBeginId;
        Assert.NotEqual(idA, idB);
        Assert.Single(device.LastImages);
        Assert.Equal(idA, device.LastImages[0].ImageId);

        dec.Release(idB);
        host.RunFrame();                                // the settle frame
        var imgs = device.LastImages;
        Assert.Equal(2, imgs.Count);
        // The held texture, opaque for the window, so a frame that cannot sample B yet still shows the cover.
        Assert.Equal(idA, imgs[0].ImageId);
        Assert.Equal(1, imgs[0].Ready);
        Assert.Equal(ImageCache.SwapOutgoingEasing, imgs[0].FadeEasing);
        // B over it: a hard cut (no fade-in, same picture) whose stand-in for missing pixels is see-through.
        Assert.Equal(idB, imgs[1].ImageId);
        Assert.Equal(1, imgs[1].Ready);
        Assert.Equal(0f, imgs[1].Placeholder.A);
        Assert.True(float.IsNaN(imgs[1].FadeStartMs) || imgs[1].FadeDurationMs <= 0f,
            $"a same-source settle must not fade in (start={imgs[1].FadeStartMs} dur={imgs[1].FadeDurationMs})");
        Assert.True(cache.CrossFadeOf(new ImageHandle(idB)) >= 0.999f);

        for (int i = 0; i < 24; i++) host.RunFrame();   // past the window + release slack
        Assert.Single(device.LastImages);
        Assert.Equal(idB, device.LastImages[0].ImageId);
        Assert.Equal(0, cache.RefsOf(new ImageHandle(idA)));
        Assert.Equal(1, cache.RefsOf(new ImageHandle(idB)));
    }
}
