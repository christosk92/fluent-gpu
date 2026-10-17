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

    private static readonly ColorF Green = new(0f, 1f, 0f, 1f);
    private static readonly ColorF Yellow = new(1f, 1f, 0f, 1f);

    /// <summary>One 40 × 40 DIP cell on a blue ground per feature the shell draws page content with, each holding a red or
    /// green 20 × 20 child whose centre pixel is probed.</summary>
    private sealed class ClippedCells : Component
    {
        private static BoxEl Child(ColorF c) => new() { Width = 20f, Height = 20f, Fill = c };

        public override Element Render() => new BoxEl
        {
            Direction = 0, Width = 400f, Height = 40f, Fill = Blue, Gap = 0f, AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl { Key = "clip", Width = 40f, Height = 40f, ClipToBounds = true, Children = [Child(Red)] },
                new BoxEl { Key = "rounded", Width = 40f, Height = 40f, ClipToBounds = true, Corners = CornerRadius4.All(8f), Children = [Child(Green)] },
                new ScrollEl { Key = "scroll", Width = 40f, Height = 40f, EdgeCues = ScrollEdgeCues.None, Content = new BoxEl { Direction = 1, Children = [Child(Yellow), Child(Red), Child(Red), Child(Red)] } },
                new BoxEl { Key = "group", Width = 40f, Height = 40f, OpacityGroup = true, Opacity = 1f, Children = [Child(Green)] },
                new BoxEl { Key = "clipgroup", Width = 40f, Height = 40f, ClipToBounds = true, Corners = CornerRadius4.All(8f), OpacityGroup = true, Children = [Child(Red)] },
                new BoxEl { Key = "outer", Width = 40f, Height = 40f, ClipToBounds = true, Children = [new BoxEl { Key = "inner", Width = 40f, Height = 40f, ClipToBounds = true, Children = [Child(Yellow)] }] },
            ],
        };
    }

    /// <summary>Text cells: a glyph run in a plain box, in a ClipToBounds box and in a scroll viewport, over a blue ground. Each
    /// cell must hold pixels that are not the ground (the glyphs).</summary>
    private sealed class ClippedText : Component
    {
        private static TextEl Glyphs() => new("WWW") { Size = 24f, Color = Yellow };

        public override Element Render() => new BoxEl
        {
            Direction = 0, Width = 400f, Height = 40f, Fill = Blue, AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl { Key = "plain", Width = 80f, Height = 40f, Children = [Glyphs()] },
                new BoxEl { Key = "clip", Width = 80f, Height = 40f, ClipToBounds = true, Children = [Glyphs()] },
                new ScrollEl { Key = "scroll", Width = 80f, Height = 40f, EdgeCues = ScrollEdgeCues.None, Content = new BoxEl { Children = [Glyphs()] } },
            ],
        };
    }

    [Fact]
    public void ClippedText_IsCaptured() => OnOffscreen(new Size2(400, 40), 1f, () => new ClippedText(), (surface, host) =>
    {
        byte[] px = surface.CaptureBgra(out int w, out int h);
        var failures = new List<string>();
        for (int cell = 0; cell < 3; cell++)
        {
            int lit = 0;
            for (int y = 0; y < 40; y++)
                for (int x = cell * 80; x < cell * 80 + 80; x++)
                    if (px[(y * w + x) * 4 + 2] > 128) lit++;   // red channel: the blue ground has none, yellow glyphs have it
            if (lit < 20) failures.Add($"cell {cell} ({new[] { "plain", "ClipToBounds", "scroll" }[cell]}) has {lit} glyph pixels");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }, directWrite: true);

    /// <summary>The shell's frame shape: a ZStack body, a clipped row that is a shared-layout group, a page column that is a clipped,
    /// layout-animated (Reveal) member of it, a band slot and a page clip under it, each holding a filled child.</summary>
    private sealed class FrameShape : Component
    {
        private static readonly LayoutTransition ColumnAnim = new(TransitionChannels.Position | TransitionChannels.Size,
            TransitionDynamics.Tween(300f, Easing.SmoothOut), SizeMode.Reveal,
            ExitDynamics: TransitionDynamics.Tween(300f, Easing.SmoothOut), SuppressDescendantTransitions: true);

        public override Element Render() => new BoxEl
        {
            ZStack = true, Width = 200f, Height = 200f, Fill = Blue, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    MorphId = "frame.row", Direction = 0, Grow = 1f, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = "column", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                            Animate = ColumnAnim, RelativeTo = "frame.row",
                            Children =
                            [
                                new BoxEl { Key = "slot", Direction = 1, Shrink = 0f, Height = 80f, ClipToBounds = true, Children = [new BoxEl { Key = "slotfill", Height = 80f, Fill = Red }] },
                                new BoxEl
                                {
                                    Key = "pageclip", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f, ClipToBounds = true, IsolateLayout = true,
                                    Children = [new BoxEl { Key = "pagefill", Height = 100f, Fill = Green }],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    [Fact]
    public void AClippedAnimatedColumnInAClippedMorphRow_IsCaptured() => OnOffscreen(new Size2(200, 200), 1f, () => new FrameShape(), (surface, host) =>
    {
        for (int i = 0; i < 40; i++) host.RunFrame();
        byte[] px = surface.CaptureBgra(out int w, out int h);
        AssertPixel(px, w, 100, 40, Red, "the band slot's fill");
        AssertPixel(px, w, 100, 120, Green, "the page clip's fill");
    });

    /// <summary>Scroll viewports nested in a chain of clipped boxes below a title strip, the way the shell's band and page sit: a
    /// horizontal viewport (the pivot run) and a vertical one (the page), each over a filled child.</summary>
    private sealed class ScrollsInClips : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 300f, Height = 300f, Fill = Blue,
            Children =
            [
                new BoxEl { Height = 48f, Fill = Blue },
                new BoxEl
                {
                    ZStack = true, Grow = 1f, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, Grow = 1f, ClipToBounds = true,
                            Children =
                            [
                                new BoxEl
                                {
                                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                                    Children =
                                    [
                                        new BoxEl
                                        {
                                            Direction = 1, Shrink = 0f, Height = 60f, ClipToBounds = true,
                                            Children =
                                            [
                                                new ScrollEl
                                                {
                                                    Key = "hscroll", Horizontal = true, Height = 60f, EdgeCues = ScrollEdgeCues.None,
                                                    Content = new BoxEl { Direction = 0, Height = 60f, Width = 120f, Fill = Red },
                                                },
                                            ],
                                        },
                                        new BoxEl
                                        {
                                            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f, ClipToBounds = true,
                                            Children =
                                            [
                                                new ScrollEl
                                                {
                                                    Key = "vscroll", Grow = 1f, EdgeCues = ScrollEdgeCues.None,
                                                    Content = new BoxEl { Direction = 1, Height = 900f, Fill = Green },
                                                },
                                            ],
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    [Fact]
    public void ScrollViewportsInNestedClips_AreCaptured() => OnOffscreen(new Size2(300, 300), 1f, () => new ScrollsInClips(), (surface, host) =>
    {
        byte[] px = surface.CaptureBgra(out int w, out int h);
        AssertPixel(px, w, 50, 70, Red, "the horizontal viewport's content (y 48..108)");
        AssertPixel(px, w, 50, 200, Green, "the vertical viewport's content");
    });

    /// <summary>A tree <paramref name="depth"/> boxes deep with a red 40 × 40 DIP leaf at the bottom of a clipped chain, the way
    /// the shell's page and band sit well below its chrome.</summary>
    private sealed class DeepTree(int depth) : Component
    {
        public override Element Render()
        {
            Element node = new BoxEl { Key = "leaf", Width = 40f, Height = 40f, Fill = Red };
            for (int i = 0; i < depth; i++)
                node = new BoxEl { Direction = 1, Width = 64f, Height = 64f, ClipToBounds = (i & 1) == 0, Children = [node] };
            return new BoxEl { Width = 64f, Height = 64f, Fill = Blue, Children = [node] };
        }
    }

    [Fact]
    public void ADeepTree_OnTheRecommendedStack_IsCaptured() => OnOffscreen(new Size2(64, 64), 1f, () => new DeepTree(120), (surface, host) =>
    {
        Assert.False(surface.RecordingStackExhausted);
        byte[] px = surface.CaptureBgra(out int w, out int h);
        AssertPixel(px, w, 20, 20, Red, "the leaf 120 boxes down");
    }, stackBytes: OffscreenSurface.UiThreadStackBytes);

    [Fact]
    public void ADeepTree_OnASmallStack_RefusesTheCaptureInsteadOfReturningABlankPage() => OnOffscreen(new Size2(64, 64), 1f, () => new DeepTree(120), (surface, host) =>
    {
        // The recorder drops the subtrees it has no stack for: the scene is laid out and the leaf is there, but nothing paints it.
        Assert.True(surface.RecordingStackExhausted);
        Assert.NotEqual(default, host.Scene.Bounds(Find(host.Scene, "leaf")));
        var e = Assert.Throws<InvalidOperationException>(() => surface.CaptureBgra(out _, out _));
        Assert.Contains("UiThreadStackBytes", e.Message);
    }, stackBytes: 512 * 1024);

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void ClippedContent_IsCaptured(float scale) => OnOffscreen(new Size2(400, 40), scale, () => new ClippedCells(), (surface, host) =>
    {
        byte[] px = surface.CaptureBgra(out int w, out int h);
        var failures = new List<string>();
        void Probe(int cell, ColorF c, string what)
        {
            int x = (int)MathF.Round((cell * 40 + 10) * scale), y = (int)MathF.Round(10 * scale);
            int i = (y * w + x) * 4;
            int eb = (int)MathF.Round(c.B * 255f), eg = (int)MathF.Round(c.G * 255f), er = (int)MathF.Round(c.R * 255f);
            if (Math.Abs(px[i] - eb) > 3 || Math.Abs(px[i + 1] - eg) > 3 || Math.Abs(px[i + 2] - er) > 3)
                failures.Add($"{what}: BGRA({px[i]},{px[i + 1]},{px[i + 2]},{px[i + 3]}) expected rgb({er},{eg},{eb})");
        }
        Probe(0, Red, "ClipToBounds box");
        Probe(1, Green, "rounded ClipToBounds box");
        Probe(2, Yellow, "scroll viewport");
        Probe(3, Green, "opacity group");
        Probe(4, Red, "rounded clipped opacity group");
        Probe(5, Yellow, "nested ClipToBounds");
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    });

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
    private static void OnOffscreen(Size2 sizeDip, float scale, Func<Component> root, Action<OffscreenSurface, AppHost> body, bool directWrite = false, int stackBytes = 16 * 1024 * 1024)
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
        }, stackBytes);
        thread.Start();
        thread.Join();
        failure?.Throw();
        if (skip is not null) Assert.Skip(skip);
    }
}
