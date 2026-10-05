using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

/// <summary>SeriesEl gates: chunking arithmetic, the overlap sample, the three shapes' baselines, headless decode, the
/// bound sample source's zero-allocation steady state, the empty-write release — and the WindowOccluded hook gate
/// (one WP owns <c>HeadlessGpuDevice.cs</c>, so one suite checks both; fullscreen-flagship-implementation.md §4.3.7/§4.4).</summary>
static class SeriesSuite
{
    public static void Run(StringTable strings)
    {
        RecordChecks();
        BoundChecks(strings);
        BlendBracketCheck(strings);
        OcclusionCheck(strings);
    }

    static float[] Ramp(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = (float)i / (n - 1); return a; }

    static HeadlessGpuDevice Decode(DrawList dl)
    {
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400f, 200f), 1f, ColorF.Transparent));
        return dev;
    }

    /// <summary>The writer + the headless decode, driven directly (the PathSuite.StreamSizeGate shape).</summary>
    static void RecordChecks()
    {
        var white = ColorF.FromRgba(255, 255, 255);
        foreach ((int n, int chunks) in new[] { (2, 1), (32, 1), (33, 2), (65, 3), (181, 6), (512, 17) })
        {
            var dl = new DrawList();
            dl.Series(new RectF(0f, 0f, 310f, 100f), new SeriesSpec(SeriesShape.Mirrored, white, null, 2f, float.NaN, 1f, 1f), Ramp(n), Affine2D.Identity, 1f);
            var dev = Decode(dl);
            bool count = dev.LastSeries.Count == chunks;
            bool overlap = true, index = true, total = true;
            for (int c = 0; c < dev.LastSeries.Count; c++)
            {
                var cmd = dev.LastSeries[c];
                index &= cmd.Index == c * 31;
                total &= cmd.Total == n;
                if (c > 0) { var prev = dev.LastSeries[c - 1]; overlap &= prev.S[prev.Count - 1] == cmd.S[0]; }   // a local: an inline array cannot be indexed on an rvalue
            }
            Check($"gate.series.record.chunks[{n}]", count && overlap && index && total,
                  $"chunks={dev.LastSeries.Count} expected={chunks} overlap={overlap} index={index} total={total}");
        }
        {
            var rect = new RectF(0f, 0f, 64f, 40f);
            var dl = new DrawList();
            dl.Series(rect, new SeriesSpec(SeriesShape.Baseline, white, null, 2f, float.NaN, 1f, 1f), Ramp(4), Affine2D.Identity, 1f);
            dl.Series(rect, new SeriesSpec(SeriesShape.Mirrored, white, null, 2f, float.NaN, 1f, 1f), Ramp(4), Affine2D.Identity, 1f);
            dl.Series(rect, new SeriesSpec(SeriesShape.Stroke, white, null, 3f, float.NaN, 1f, 1f), Ramp(4), Affine2D.Identity, 1f);
            var dev = Decode(dl);
            bool ok = dev.LastSeries.Count == 3 && dev.LastSeries[0].Baseline == 1f && dev.LastSeries[1].Baseline == 0.5f
                   && dev.LastSeries[2].Baseline == 1f && dev.LastSeries[2].Shape == 2 && dev.LastSeries[2].Thickness == 3f
                   && dev.LastSeries[0].Dx == 64f / 3f;
            Check("gate.series.record.shapes", ok, $"n={dev.LastSeries.Count}");
        }
        {
            // a 1-sample series paints nothing; an empty one paints nothing
            var dl = new DrawList();
            dl.Series(new RectF(0f, 0f, 64f, 40f), new SeriesSpec(SeriesShape.Baseline, white, null, 2f, float.NaN, 1f, 1f), [0.5f], Affine2D.Identity, 1f);
            dl.Series(new RectF(0f, 0f, 64f, 40f), new SeriesSpec(SeriesShape.Baseline, white, null, 2f, float.NaN, 1f, 1f), [], Affine2D.Identity, 1f);
            Check("gate.series.record.degenerate", Decode(dl).LastSeries.Count == 0, "1 and 0 samples emit no chunk");
        }
        {
            // a Polar loop stays inside its box: samples and Amplitude clamp to 0..1
            var dl = new DrawList();
            dl.Series(new RectF(0f, 0f, 64f, 64f), new SeriesSpec(SeriesShape.Polar, white, null, 2f, float.NaN, 1.5f, 1f), [0.5f, 2f, -1f, 0.5f], Affine2D.Identity, 1f);
            var dev = Decode(dl);
            var c = dev.LastSeries.Count == 1 ? dev.LastSeries[0] : default;
            Check("gate.series.record.polar-clamp", dev.LastSeries.Count == 1 && c.Amplitude == 1f && c.S[1] == 1f && c.S[2] == 0f && c.S[0] == 0.5f,
                  $"n={dev.LastSeries.Count} amp={c.Amplitude} s1={c.S[1]} s2={c.S[2]}");
        }
    }

    /// <summary>BoxEl.Blend brackets: an additive box NESTED in an additive box emits nothing of its own, the outer pair
    /// closes after ALL of the outer content, and a later additive sibling still opens its own pair (the depth stays
    /// balanced across the record).</summary>
    static void BlendBracketCheck(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var dev = new HeadlessGpuDevice();
        var host = Mount(strings, app, dev, new BlendProbe(), "blend", 320f, 240f);
        host.RunFrame();
        var b = dev.LastBlends;
        bool shape = b.Count == 4 && b[0] == 1 && b[1] == 0 && b[2] == 1 && b[3] == 0;
        Check("gate.blend.nested-bracket", shape, $"blends=[{string.Join(",", b)}] expected=[1,0,1,0]");
        host.Dispose();
    }

    sealed class BlendProbe : Component
    {
        static SeriesEl Wave() => new() { Width = 100f, Height = 40f, Shape = SeriesShape.Mirrored, Samples = new SeriesSamples([0.2f, 0.8f, 0.4f], 3, 1u) };
        public override Element Render() => new BoxEl
        {
            Width = 320f, Height = 240f,
            Children =
            [
                new BoxEl { Blend = PaintBlend.Additive, Width = 320f, Height = 100f, Children = [new BoxEl { Blend = PaintBlend.Additive, Width = 100f, Height = 40f, Children = [Wave()] }, Wave()] },
                new BoxEl { Blend = PaintBlend.Additive, Width = 320f, Height = 100f, Children = [Wave()] },
            ],
        };
    }

    /// <summary>The ListRowSuite.Mount body (ListRowSuite.cs:91-100) with the device kept so the gates can read LastSeries / the swapchain.</summary>
    static AppHost Mount(StringTable strings, HeadlessPlatformApp app, HeadlessGpuDevice device, Component probe, string title, float w, float h)
    {
        var fonts = new HeadlessFontSystem(strings);
        var window = new HeadlessWindow(new WindowDesc(title, new Size2(w, h), 1f));
        window.Show();
        var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();
        return host;
    }

    /// <summary>A bound source rewritten every frame: the change frame may pay a bounded one-time cost; steady frames
    /// allocate 0 bytes in phases 6–13 (the ListRowSuite.cs:135-161 gate shape). A version bump is a PAINT-ONLY frame —
    /// one bind fire + one re-record of the node, no reconcile and no relayout — so <c>FrameStats.Rendered</c>
    /// (<c>reconciled || layoutNeeded</c>, AppHost.Paint) is FALSE by contract, exactly like every other bound paint
    /// channel (HooksSuite's paint-only opacity/fill binds, gate.hit.bindable). The repaint evidence is
    /// <c>FrameStats.Presented</c> (the frame submitted instead of eliding) plus the new samples on the device.
    /// Release gate: the frame after an empty write emits NO series chunk.</summary>
    static void BoundChecks(StringTable strings)
    {
        var source = new SeriesSource(181);
        using var app = new HeadlessPlatformApp();
        var dev = new HeadlessGpuDevice();
        var host = Mount(strings, app, dev, new SeriesProbe(source), "series", 320f, 120f);
        for (int k = 0; k < 4; k++) { source.Fill(k * 0.01f); host.RunFrame(); }
        source.Fill(0.5f);
        var change = host.RunFrame();
        for (int k = 0; k < 3; k++) { source.Fill(0.5f + k * 0.001f); host.RunFrame(); }
        source.Fill(0.75f);
        var steady = host.RunFrame();
        float head = float.NaN;
        if (dev.LastSeries.Count > 0) { var first = dev.LastSeries[0]; head = first.S[0]; }   // a local: an inline array cannot be indexed on an rvalue
        Check("gate.series.bound.zero-alloc", steady.HotPhaseAllocBytes == 0 && steady.Presented && !steady.Rendered && head == 0.75f,
              $"changeFrameAlloc={change.HotPhaseAllocBytes}B steadyAlloc={steady.HotPhaseAllocBytes}B presented={steady.Presented} rendered={steady.Rendered} head={head} expected=0.75");
        Check("gate.series.bound.draws", dev.LastSeries.Count == 6, $"chunks={dev.LastSeries.Count} expected=6 (181 samples)");
        source.Clear();
        host.RunFrame();
        host.RunFrame();
        Check("gate.series.bound.empty-releases", dev.LastSeries.Count == 0, $"chunks={dev.LastSeries.Count} after Clear()");
        host.Dispose();
    }

    /// <summary>The headless swapchain's Occluded flag reaches InputHooks.WindowOccluded within a frame, both ways.</summary>
    static void OcclusionCheck(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var dev = new HeadlessGpuDevice();
        var probe = new OcclusionProbe();
        var host = Mount(strings, app, dev, probe, "occlusion", 16f, 16f);
        host.RunFrame();
        bool seeded = probe.Seen == false;
        HeadlessSwapchain swapchain = dev.PrimarySwapchain!;
        swapchain.Occluded = true;
        host.RunFrame(); host.RunFrame();
        bool rose = probe.Seen == true;
        swapchain.Occluded = false;
        host.RunFrame(); host.RunFrame();
        bool fell = probe.Seen == false;
        Check("gate.occlusion.hook-follows-swapchain", seeded && rose && fell, $"seeded={seeded} rose={rose} fell={fell}");
        host.Dispose();
    }

    /// <summary>The producer side the app mirrors (Visualizer.UI.cs SeriesSource): one buffer, one version signal.</summary>
    sealed class SeriesSource(int n)
    {
        public readonly float[] Buffer = new float[n];
        public readonly Signal<uint> Version = new(0u);
        public int Count = n;
        public void Fill(float v) { for (int i = 0; i < Buffer.Length; i++) Buffer[i] = v + 0.2f * (i % 5) / 5f; Version.Value = Version.Peek() + 1; }
        public void Clear() { Count = 0; Version.Value = Version.Peek() + 1; }
        public SeriesSamples Current => new(Buffer, Count, Version.Value);   // reading .Value subscribes the bind
    }

    sealed class SeriesProbe(SeriesSource source) : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = 320f, Height = 120f,
            Children = [new SeriesEl { Width = 320f, Height = 120f, Shape = SeriesShape.Mirrored, Samples = Prop.Of(() => source.Current) }],
        };
    }

    sealed class OcclusionProbe : Component
    {
        public bool? Seen;
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            Seen = hooks.WindowOccluded?.Value;   // .Value subscribes: the host's SetIfChanged re-renders this probe
            return new BoxEl { Width = 16f, Height = 16f };
        }
    }
}
