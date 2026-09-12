using System;
using System.IO;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace FluentGpu;

/// <summary>Explicit native pixel test for first-frame atlas growth; no healing/settling frame is allowed.</summary>
internal static class GlyphCapacityProbe
{
    public static int Run(string? outputDirectory)
    {
        try { return Drive(outputDirectory ?? ".tmp/glyph-capacity"); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[glyph-capacity] FAIL {ex}");
            return 1;
        }
    }

    private static int Drive(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using var app = new Win32App();
        using var window = app.CreateWindow(new WindowDesc("FluentGpu - glyph capacity identity",
            new Size2(1000, 640), 1f, Composited: false));
        window.Show();
        var events = new InputEventRing();
        window.PumpInto(events);
        events.Clear();
        var strings = new StringTable();
        using var device = new D3D12Device(strings);
        device.EnsureDeviceCreated();
        var swapchain = device.CreateSwapchain(new SwapchainDesc(window.Handle, window.ClientSizePx));
        float scale = window.Scale;
        int icon = IconGeometryTable.Shared.Register("M 8 0 L 10 5 L 16 6 L 12 10 L 13 16 L 8 13 L 3 16 L 4 10 L 0 6 L 6 5 Z");
        int pressure = IconGeometryTable.Shared.Register("M 0 0 L 16 0 L 16 16 L 0 16 Z");
        var baseline = Build(strings, icon, pressure, scale, grow: false, freshRuns: false);
        byte[] before = Submit(device, swapchain, window, baseline, 1, out int width, out int height);
        var initial = device.GlyphCapacityProbeState;
        Require(initial.Edge == 2048 && !initial.Pending && initial.Dropped == 0,
            $"baseline was not a healthy 2048 generation: {initial}");
        PngWriter.WriteBgra(Path.Combine(outputDirectory, "before-2048.png"), before, width, height);

        // Existing visible runs are encountered before pressure forces a new realization. This catches stale
        // normalized cached UVs as well as failure to upload the entire old+new occupied dirty band.
        var growth = Build(strings, icon, pressure, scale, grow: true, freshRuns: false);
        byte[] first = Submit(device, swapchain, window, growth, 2, out int fw, out int fh);
        var grown = device.GlyphCapacityProbeState;
        Require(grown.Edge == 4096 && grown.Epoch == initial.Epoch + 1 && !grown.Pending && grown.Dropped == 0,
            $"growth did not complete faithfully in one frame: {initial} -> {grown}");
        PngWriter.WriteBgra(Path.Combine(outputDirectory, "first-4096.png"), first, fw, fh);
        Require(width == fw && height == fh && before.AsSpan().SequenceEqual(first),
            "first growth frame differs from the 2048 baseline");

        // A harmless +1 DIP width on unwrapped, left-aligned runs changes the run cache key, forcing fresh
        // shaping against the already-grown atlas. The actual visible geometry and karaoke extent are identical.
        var reference = Build(strings, icon, pressure, scale, grow: true, freshRuns: true);
        byte[] fresh = Submit(device, swapchain, window, reference, 3, out int rw, out int rh);
        PngWriter.WriteBgra(Path.Combine(outputDirectory, "fresh-reference-4096.png"), fresh, rw, rh);
        Require(rw == width && rh == height && fresh.AsSpan().SequenceEqual(first),
            "grown cached frame differs from fresh shaping on the grown atlas");
        int changedPixels = 0;
        for (int i = 0; i < before.Length; i += 4)
            if (before[i] != before[0] || before[i + 1] != before[1] || before[i + 2] != before[2]) changedPixels++;
        Require(changedPixels > 2000, "capture is blank or does not exercise visible glyphs/icons");
        Console.Error.WriteLine($"[glyph-capacity] PASS pixels={width}x{height} scale={scale} " +
            $"edge={initial.Edge}->{grown.Edge} epoch={initial.Epoch}->{grown.Epoch} " +
            $"rows={grown.OccupiedRows} nonbackground={changedPixels} phases=4 first_frame_exact=true fresh_shape_exact=true");
        return 0;
    }

    private static byte[] Submit(D3D12Device device, ISwapchain swapchain, IPlatformWindow window,
        DrawList draw, ulong sequence, out int width, out int height)
    {
        RepaintDamageRegion damage = default;
        damage.ForceFull(RepaintFullReason.TargetInvalidated);
        var info = new FrameInfo(window.ClientSizePx, window.Scale, ColorF.FromRgba(18, 18, 22),
            FrameEpoch: sequence, RepaintDamage: damage, PublishSequence: sequence);
        device.SubmitDrawList(draw.Bytes, draw.SortKeys, in info, swapchain);
        swapchain.Present();
        Require(!device.LastPresentStoodDown, "present stood down");
        return device.CaptureBgra(out width, out height);
    }

    private static DrawList Build(StringTable strings, int icon, int pressure, float scale, bool grow, bool freshRuns)
    {
        var draw = new DrawList();
        var white = ColorF.FromRgba(240, 240, 240);
        var cyan = ColorF.FromRgba(40, 210, 245);
        var muted = ColorF.FromRgba(100, 110, 125);
        var family = strings.Intern("Segoe UI");
        var text = strings.Intern("Atlas: Latin cafe\u0301 — 中文 العربية Ελληνικά ♪");
        var lyric = strings.Intern("Every syllable stays sharp while scrolling");
        float extent = freshRuns ? 901f : 900f;
        for (int phase = 0; phase < 4; phase++)
        {
            float top = 20 + phase * 50 + phase * .25f / scale;
            draw.DrawGlyphRun(new RectF(24, top, extent, 45), white, text, family, 24, 400, 0, 0, 1,
                0, 0, 0, 0, Affine2D.Identity, 1);
        }
        draw.DrawGlyphRunGradient(new RectF(24, 260, extent, 65), lyric, family, 32, 600, 0, 0, 1,
            0, 0, 0, 0, Affine2D.Identity, 1, cyan, muted, .47f, .04f, 0);
        draw.DrawIconMask(new RectF(24, 355, 64, 64), cyan, icon, Affine2D.Identity, 1);
        if (grow)
        {
            // Physical dimensions deliberately independent of OS DPI. These masks are outside the target and
            // clipped at raster replay, but their atlas demand is discovered by the real preflight before drawing.
            draw.DrawIconMask(new RectF(10000, 10000, 1800 / scale, 1500 / scale), white, pressure, Affine2D.Identity, 1);
            draw.DrawIconMask(new RectF(10000, 10000, 1800 / scale, 700 / scale), white, pressure, Affine2D.Identity, 1);
        }
        return draw;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
