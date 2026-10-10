using System.Runtime.InteropServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Layout;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A tile's want counts a glyph run by its <see cref="SliceOpBounds"/> footprint (the ink rect plus the
/// <see cref="RepaintCull.GlyphHalo"/>), and the per-tile replay skips a whole span-index entry whose bounds miss the
/// tile. The entry must therefore cover the footprint of every run it holds: a node box that stops short of the halo lets
/// the tile across a seam (or a partial raster's damage rect) fold the run into its want, cull the entry, and freeze the
/// overhanging ascenders or descenders out of a tile marked valid. Serial: it constructs a host.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class SpanIndexGlyphHaloTests
{
    private sealed class Probe : Component
    {
        public override Element Render() => new BoxEl
        {
            Grow = 1f, Direction = 1,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(200f, 200f, 0f, 0f),
                    Children = [new TextEl("Ag") { Size = 40f }],
                },
            ],
        };
    }

    [Fact]
    public void Every_span_index_entry_covers_the_glyph_halo_of_the_runs_it_holds()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("span-glyph-halo", new Size2(800, 600), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Probe());
        for (int i = 0; i < 5; i++) host.RunFrame();

        var scene = host.Scene;
        var slices = new SliceRecorder();
        var stats = SceneRecorder.Record(scene, new DrawList(), spans: new SpanTable(), slices: slices);
        var snapshot = scene.Recording.InlineSnapshot;
        Assert.NotNull(snapshot);
        var info = new FrameInfo(new Size2(800f, 600f), 1f, default, RepaintDamage: stats.RepaintDamage);
        var frame = slices.BuildComposite(new SliceTable(256, 64, 64), snapshot!, in info, 0, stats.RepaintDamage, withStreams: true);

        int checkedRuns = 0;
        foreach (ref readonly SliceRow row in frame.Slices)
        {
            if (row.SpanIndexCount <= 0 || row.SpanIndexStart + row.SpanIndexCount > frame.SliceSpans.Length) continue;
            ReadOnlySpan<SliceSpan> spans = frame.SliceSpans.Slice(row.SpanIndexStart, row.SpanIndexCount);
            ReadOnlySpan<byte> stream = frame.StreamOf(in row);
            float s = row.Frame.Scale > 0f ? row.Frame.Scale : 1f;
            float slack = RepaintCull.AaHaloDip * s + 1f;   // the replay's span-cull pad, plus a px of origin rounding
            foreach (SliceSpan en in spans)
            {
                if (en.HasMarker || en.ByteLength <= 0) continue;
                int pos = en.ByteStart, end = Math.Min(stream.Length, en.ByteStart + en.ByteLength);
                while (pos + sizeof(int) <= end)
                {
                    var op = (DrawOp)MemoryMarshal.Read<int>(stream[pos..]);
                    if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > stream.Length) break;
                    if (op is DrawOp.DrawGlyphRun or DrawOp.DrawGlyphRunGradient
                        && SliceOpBounds.TryGet(op, stream.Slice(pos + sizeof(int), body), out RectF fp))
                    {
                        checkedRuns++;
                        var px = new RectF(fp.X * s - row.Frame.OriginX, fp.Y * s - row.Frame.OriginY, fp.W * s, fp.H * s);
                        RectF b = en.Bounds;
                        Assert.True(px.X >= b.X - slack && px.Y >= b.Y - slack && px.Right <= b.Right + slack && px.Bottom <= b.Bottom + slack,
                            $"depth-{en.Depth} entry {b} misses glyph footprint {px}: a tile the run's halo reaches culls it");
                    }
                    pos += sizeof(int) + body;
                }
            }
        }
        Assert.True(checkedRuns > 0, "the text node's run sits inside a span-index entry");
    }
}
