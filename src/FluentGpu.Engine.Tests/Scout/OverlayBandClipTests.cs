using System;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A connected-animation overlay (the live-overlay Hero fly) draws in the top band bounded by
/// SceneStore.OverlayClip, the page content region, so a flying cover never paints over the sidebar or window chrome.
/// The band walk only culls against that rect, so the recorder must also push it as a scissor; without the push a cover
/// straddling the region's edge was drawn whole, unscissored, over everything outside it.</summary>
public sealed class OverlayBandClipTests
{
    private const int CoverId = 7;

    [Fact]
    public void A_flying_cover_straddling_the_content_region_draws_under_the_band_scissor()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1); scene.Root = root;
        scene.Bounds(root) = new RectF(0f, 0f, 800f, 600f);

        // the overlay BeginFly creates: a bare image node, no ClipsToBounds, no clip-rect
        var ov = scene.CreateNode(8);
        ref NodePaint p = ref scene.Paint(ov);
        p.VisualKind = VisualKind.Image;
        p.ImageId = CoverId;
        scene.Bounds(ov) = new RectF(20f, 20f, 200f, 200f);   // pokes out above-left of the region (over the sidebar / title bar)
        scene.AddOverlay(ov);
        var region = new RectF(100f, 100f, 600f, 450f);
        scene.OverlayClip = region;

        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);

        RectF? scissor = ScissorAtCover(dl.Bytes);
        Assert.True(scissor.HasValue, "the overlay's cover was not recorded");
        RectF s = scissor.Value;
        Assert.False(s.IsInfinite, "the cover draws with no scissor: the band clip only culled it");
        Assert.True(s.X >= region.X - 0.5f && s.Y >= region.Y - 0.5f
                    && s.Right <= region.Right + 0.5f && s.Bottom <= region.Bottom + 0.5f,
            $"the cover's scissor ({s.X},{s.Y},{s.W},{s.H}) reaches outside the content region");
    }

    // The scissor in effect at the cover's DrawImage: the innermost push (a PushClip replaces the scissor), or the
    // unbounded sentinel when nothing is pushed. Null when the cover never draws.
    private static RectF? ScissorAtCover(ReadOnlySpan<byte> bytes)
    {
        Span<RectF> stack = stackalloc RectF[32];
        int depth = 0, pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            switch (op)
            {
                case DrawOp.PushClip:
                    stack[depth++] = MemoryMarshal.Read<ClipCmd>(bytes.Slice(pos)).DeviceRect;
                    break;
                case DrawOp.PopClip:
                    depth--;
                    break;
                case DrawOp.DrawImage:
                    if (MemoryMarshal.Read<DrawImageCmd>(bytes.Slice(pos)).ImageId == CoverId)
                        return depth > 0 ? stack[depth - 1] : RectF.Infinite;
                    break;
            }
            if (!RepaintStreamSafety.TryBodySize(op, out int body)) throw new InvalidOperationException($"unexpected op {op}");
            pos += body;
        }
        return null;
    }
}
