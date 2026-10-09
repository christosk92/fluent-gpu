using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A scaled scroll effect (a <c>ScrollEffect.Scale</c> map, a <c>.StretchFromTop()</c>) records INLINE with its pose
/// baked, and the slice recorder re-records it when that pose moves. When an unrelated repaint re-walks the containing
/// slice and the effect's clean ancestor is exact-copied from the prior buffer, the baked bytes come along without the
/// effect node's walk: the recorder must still watch their pose, or a later scroll is a composite-only turn that leaves
/// the effect frozen at its old scale.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class BakedPoseSpanCopyTests
{
    private static readonly ColorF PhotoFill = ColorF.FromRgba(200, 120, 40);
    private static readonly ColorF RowFill = ColorF.FromRgba(40, 40, 40);

    // A page scroller whose content is a hero (a plain box around a photo scaled by the scroll offset) over a tall row.
    private sealed class Page : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = 400f, Height = 400f,
            Children =
            [
                new ScrollEl
                {
                    Width = 400f, Height = 400f, SuppressScrollBar = true,
                    EdgeCues = ScrollEdgeCues.None,   // no offset-dependent chrome: only the baked pose can ask for a re-record
                    Content = new BoxEl
                    {
                        Direction = 1,
                        Children =
                        [
                            new BoxEl
                            {
                                Height = 200f,
                                Children = [ new BoxEl { Height = 200f, Fill = PhotoFill }.OnScroll(ScrollEffect.Scale(0.0, 200.0, 1f, 0.5f)) ],
                            },
                            new BoxEl { Height = 2000f, Fill = RowFill },
                        ],
                    },
                },
            ],
        };
    }

    // Record the page (pass 1 bakes the photo's pose), optionally repaint the row and record again (pass 2 re-walks the
    // content slice and exact-copies the clean hero), scroll 100 DIP, and ask whether that scroll can be composite-only.
    private static bool CompositeOnlyAfterScroll(bool repaintRow)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("baked-pose", new Size2(400, 400), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Page());
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            var photo = FindFill(scene, scene.Root, PhotoFill);
            var row = FindFill(scene, scene.Root, RowFill);
            Assert.False(photo.IsNull);
            Assert.False(row.IsNull);

            var slices = new SliceRecorder();
            var spans = new SpanTable();
            SceneRecorder.Record(scene, new DrawList(), spans: spans, slices: slices);
            scene.ClearRecordDirty();
            if (repaintRow)
            {
                scene.Paint(row).Fill = ColorF.FromRgba(41, 40, 40);
                scene.Mark(row, NodeFlags.PaintDirty);
                SceneRecorder.Record(scene, new DrawList(), spans: spans, slices: slices);
                scene.ClearRecordDirty();
            }
            Assert.True(slices.PosesCompatible(scene.Recording.CaptureInline(scene, null)));   // nothing moved yet

            Affine2D before = scene.Paint(photo).LocalTransform;
            var vp = FindViewport(scene);
            Assert.False(vp.IsNull);
            host.TryGetScrollHandle(vp)!.ScrollTo(100.0, ScrollMove.Immediate);
            for (int i = 0; i < 30 && (i < 2 || host.HasActiveWork); i++) host.RunFrame();
            Assert.Equal(100.0, scene.ScrollRef(vp).Offset);
            Assert.NotEqual(before, scene.Paint(photo).LocalTransform);   // the scale map posed the photo
            return slices.PosesCompatible(scene.Recording.CaptureInline(scene, null));
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void AScaledEffectBakedInlineReRecordsWhenItsPoseMoves()
        => Assert.False(CompositeOnlyAfterScroll(repaintRow: false));

    [Fact]
    public void AScaledEffectCopiedWithItsCleanAncestorStillReRecordsWhenItsPoseMoves()
        => Assert.False(CompositeOnlyAfterScroll(repaintRow: true));

    private static NodeHandle FindViewport(SceneStore scene)
    {
        for (int i = 0; i < scene.Capacity; i++)
        {
            var h = scene.HandleAt(i);
            if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) return h;
        }
        return default;
    }

    private static NodeHandle FindFill(SceneStore s, NodeHandle n, ColorF fill)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).Fill == fill) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindFill(s, c, fill);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
}
