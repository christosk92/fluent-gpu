using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The public offscreen capture path (OffscreenSurface): an AppHost over an offscreen surface renders through the real D3D12
/// device on WARP into a window nobody sees, and the captured BGRA holds the tree's pixels at the pinned DIP size × scale;
/// a resize re-sizes the capture; DirectWrite measures real text in an offscreen host and in a plain headless host (not the
/// headless 0.55 × size model); and the trim query reads DirectWrite's measure. Skipped where WARP is unavailable.
/// </summary>
public sealed class OffscreenSurfaceTests
{
    private static readonly ColorF Red = new(1f, 0f, 0f, 1f);
    private static readonly ColorF Blue = new(0f, 0f, 1f, 1f);

    /// <summary>A 64 × 48 DIP blue ground with a 32 × 24 DIP red rect in its top-left corner.</summary>
    private sealed class Rects : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 64f, Height = 48f, AlignItems = FlexAlign.Start, Fill = Blue,
            Children = [new BoxEl { Key = "red", Width = 32f, Height = 24f, Fill = Red }],
        };
    }

    private sealed class Texts(params Element[] items) : Component
    {
        public override Element Render() => new BoxEl { Direction = 1, Width = 600f, AlignItems = FlexAlign.Start, Children = items };
    }

    [Fact]
    public void ACapture_HoldsTheTreesPixels_AtScaleOne() => OnOffscreen(new Size2(64, 48), 1f, () => new Rects(), (surface, host) =>
    {
        Assert.True(surface.IsWarp);
        byte[] px = surface.CaptureBgra(out int w, out int h);
        Assert.Equal(64, w);
        Assert.Equal(48, h);
        AssertPixel(px, w, 10, 10, Red, "inside the red rect");
        AssertPixel(px, w, 31, 23, Red, "the red rect's last pixel");
        AssertPixel(px, w, 50, 10, Blue, "right of the red rect");
        AssertPixel(px, w, 10, 40, Blue, "below the red rect");
    });

    [Fact]
    public void ACapture_IsTheDipSizeTimesTheScale() => OnOffscreen(new Size2(64, 48), 2f, () => new Rects(), (surface, host) =>
    {
        byte[] px = surface.CaptureBgra(out int w, out int h);
        Assert.Equal(128, w);
        Assert.Equal(96, h);
        Assert.Equal(new RectF(0, 0, 32, 24), host.Scene.Bounds(Find(host.Scene, "red")));   // layout stays in DIP
        AssertPixel(px, w, 20, 20, Red, "DIP (10,10)");
        AssertPixel(px, w, 63, 47, Red, "the red rect's last pixel at 2×");
        AssertPixel(px, w, 70, 10, Blue, "DIP (35,5)");
        AssertPixel(px, w, 100, 80, Blue, "DIP (50,40)");
    });

    [Fact]
    public void AResize_ResizesTheNextCapture() => OnOffscreen(new Size2(64, 48), 1f, () => new Rects(), (surface, host) =>
    {
        surface.Resize(new Size2(80, 40));
        for (int i = 0; i < 3; i++) host.RunFrame();
        byte[] px = surface.CaptureBgra(out int w, out int h);
        Assert.Equal(80, w);
        Assert.Equal(40, h);
        AssertPixel(px, w, 10, 10, Red, "inside the red rect after the resize");
    });

    [Fact]
    public void AWrittenPng_IsAPng() => OnOffscreen(new Size2(64, 48), 1f, () => new Rects(), (surface, host) =>
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fgpu-offscreen-{Guid.NewGuid():N}.png");
        try
        {
            surface.WritePng(path, backdrop: new ColorF(1f, 1f, 1f, 1f));
            byte[] head = System.IO.File.ReadAllBytes(path);
            Assert.True(head.Length > 8 && head[0] == 137 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G');
        }
        finally { try { System.IO.File.Delete(path); } catch (System.IO.IOException) { } }
    });

    [Fact]
    public void DirectWrite_InAnOffscreenHost_MeasuresRealText() => OnOffscreen(new Size2(640, 200), 1f,
        () => new Texts(new TextEl("iiiiiiiiii") { Key = "narrow", Size = 14f }, new TextEl("WWWWWWWWWW") { Key = "wide", Size = 14f }),
        (surface, host) => AssertRealMetrics(host), directWrite: true);

    [Fact]
    public void DirectWrite_InAPlainHeadlessHost_MeasuresRealText()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("dwrite-headless", new Size2(640, 200), 1f));
        window.Show();
        using var fonts = new DirectWriteFontSystem(strings);
        var root = new Texts(new TextEl("iiiiiiiiii") { Key = "narrow", Size = 14f }, new TextEl("WWWWWWWWWW") { Key = "wide", Size = 14f });
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, root);
        for (int i = 0; i < 3; i++) host.RunFrame();
        AssertRealMetrics(host);
    }

    [Fact]
    public void TheTrimQuery_ReadsDirectWrite() => OnOffscreen(new Size2(640, 200), 1f,
        () => new Texts(
            new TextEl("A caption far too long for its box") { Key = "cut", Size = 14f, Width = 60f, Trim = TextTrim.CharacterEllipsis },
            new TextEl("Fits") { Key = "fits", Size = 14f, Width = 200f, Trim = TextTrim.CharacterEllipsis }),
        (surface, host) =>
        {
            TextOverflow cut = host.QueryTextOverflow(Find(host.Scene, "cut"));
            Assert.True(cut.IsText);
            Assert.True(cut.Trimmed);
            Assert.True(cut.NaturalSize.Width > 60f);
            Assert.False(host.IsTextTrimmed(Find(host.Scene, "fits")));
        }, directWrite: true);

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Real shaping: ten narrow glyphs measure narrower than ten wide ones (the headless model makes them equal), and
    /// neither is the headless 0.55 × size × 10 = 77 DIP.</summary>
    private static void AssertRealMetrics(AppHost host)
    {
        float narrow = host.Scene.Bounds(Find(host.Scene, "narrow")).W;
        float wide = host.Scene.Bounds(Find(host.Scene, "wide")).W;
        Assert.True(narrow > 0f && wide > 0f, $"unmeasured: narrow {narrow}, wide {wide}");
        Assert.True(wide > narrow * 1.5f, $"DirectWrite advances: 10×'W' = {wide} DIP should be far wider than 10×'i' = {narrow} DIP");
        Assert.True(Math.Abs(narrow - 77f) > 5f, $"10×'i' measured {narrow} DIP, the headless model's 77");
    }

    private static NodeHandle Find(SceneStore s, string key)
    {
        var keys = new List<(NodeHandle Node, string Key)>();
        s.CopyDebugKeys(keys);
        foreach (var (node, k) in keys)
            if (k == key) return node;
        Assert.Fail($"no node keyed '{key}'");
        return default;
    }

    /// <summary>A premultiplied BGRA pixel within 3 steps per channel of an opaque <paramref name="c"/>.</summary>
    private static void AssertPixel(byte[] px, int width, int x, int y, ColorF c, string what)
    {
        int i = (y * width + x) * 4;
        int b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
        int eb = (int)MathF.Round(c.B * 255f), eg = (int)MathF.Round(c.G * 255f), er = (int)MathF.Round(c.R * 255f);
        bool ok = Math.Abs(b - eb) <= 3 && Math.Abs(g - eg) <= 3 && Math.Abs(r - er) <= 3 && a >= 252;
        Assert.True(ok, $"{what}: pixel ({x},{y}) is BGRA({b},{g},{r},{a}), expected BGRA({eb},{eg},{er},255)");
    }

    /// <summary>Build a WARP offscreen surface and a host over it on a dedicated thread (the surface's window and device belong
    /// to the thread that made them), run a few frames, then the body; skipped where the surface cannot be created.</summary>
    private static void OnOffscreen(Size2 sizeDip, float scale, Func<Component> root, Action<OffscreenSurface, AppHost> body, bool directWrite = false)
    {
        ExceptionDispatchInfo? failure = null;
        string? skip = null;
        var thread = new Thread(() =>
        {
            OffscreenSurface? surface = null;
            HeadlessPlatformApp? app = null;
            AppHost? host = null;
            DirectWriteFontSystem? dwrite = null;
            try
            {
                var strings = new StringTable();
                if (!OffscreenSurface.TryCreate(strings, new OffscreenSurfaceOptions { SizeDip = sizeDip, Scale = scale }, out surface, out string? why))
                {
                    skip = "no offscreen surface: " + why;
                    return;
                }
                app = new HeadlessPlatformApp();
                if (directWrite) dwrite = surface.CreateFontSystem();
                FluentGpu.Text.IFontSystem fonts = dwrite is not null ? dwrite : new FluentGpu.Text.Headless.HeadlessFontSystem(strings);
                host = new AppHost(app, surface.Window, surface.Device, fonts, strings, root());
                surface.Attach(host);
                for (int i = 0; i < 3; i++) host.RunFrame();
                body(surface, host);
            }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally
            {
                host?.Dispose();
                surface?.Dispose();
                dwrite?.Dispose();
                app?.Dispose();
            }
        }, 16 * 1024 * 1024);
        thread.Start();
        thread.Join();
        failure?.Throw();
        if (skip is not null) Assert.Skip(skip);
    }
}
