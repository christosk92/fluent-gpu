using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A hovered card under a <c>HoverElevateClipRoot</c> is parked by its scroll content's walk and hoisted into the clip
/// root's slice at the content's record-time offset. The content slice must then be pose-locked whatever that offset
/// was: a shelf resting at offset 0 records its content with no own offset, and a later pan must still re-record the
/// hoist instead of compositing the row away from a lifted card frozen in the outer slice.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class HoverElevateHoistPoseLockTests
{
    private static readonly ColorF[] Fills =
    [
        ColorF.FromRgba(220, 40, 40), ColorF.FromRgba(40, 90, 220), ColorF.FromRgba(40, 180, 90),
        ColorF.FromRgba(200, 160, 40), ColorF.FromRgba(150, 60, 200), ColorF.FromRgba(60, 200, 200),
    ];

    // The PagedShelf shape: a clipping HoverElevateClipRoot around a horizontal scroller whose content row holds the
    // HoverElevatePaint cells directly (the cell is the parked node; its card is the hover target).
    private sealed class Shelf : Component
    {
        public override Element Render()
        {
            var cells = new Element[Fills.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                ColorF fill = Fills[i];
                cells[i] = new BoxEl
                {
                    Direction = 1, Width = 100f, HoverElevatePaint = true,
                    Children = [ new BoxEl { Width = 100f, Height = 100f, Fill = fill, HoverFill = fill, OnClick = static () => { } } ],
                };
            }
            return new BoxEl
            {
                Width = 300f, Height = 100f, ClipToBounds = true, HoverElevateClipRoot = true,
                Children =
                [
                    new ScrollEl
                    {
                        Horizontal = true, Width = 300f, Height = 100f, SuppressScrollBar = true,
                        EdgeCues = ScrollEdgeCues.None,   // no offset-dependent chrome: only the pose lock can ask for a re-record
                        Content = new BoxEl { Direction = 0, Children = cells },
                    },
                ],
            };
        }
    }

    private static NodeHandle FindViewport(SceneStore scene)
    {
        for (int i = 0; i < scene.Capacity; i++)
        {
            var h = scene.HandleAt(i);
            if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) return h;
        }
        return default;
    }

    // Record the shelf at offset 0 with the pointer at `pointer`, pan it 40 DIP, and ask the retained slices whether the
    // pan can be a composite-only turn (the render thread's AppHost gate).
    private static (bool Before, bool After) PanAtRest(Point2 pointer)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("elevate-pose-lock", new Size2(640, 480), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Shelf());
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, pointer, 0, 0));
            for (int i = 0; i < 30 && (i < 2 || host.HasActiveWork); i++) host.RunFrame();

            var scene = host.Scene;
            var slices = new SliceRecorder();
            var spans = new SpanTable();
            SceneRecorder.Record(scene, new DrawList(), spans: spans, slices: slices);
            bool before = slices.PosesCompatible(scene.Recording.CaptureInline(scene, null));

            var vp = FindViewport(scene);
            Assert.False(vp.IsNull);
            host.TryGetScrollHandle(vp)!.ScrollTo(40.0, ScrollMove.Immediate);
            for (int i = 0; i < 30 && (i < 2 || host.HasActiveWork); i++) host.RunFrame();
            Assert.Equal(40.0, scene.ScrollRef(vp).Offset);
            bool after = slices.PosesCompatible(scene.Recording.CaptureInline(scene, null));
            return (before, after);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void AHoistedCardRecordedAtOffsetZeroPinsTheContentSoAPanReRecords()
    {
        var (before, after) = PanAtRest(new Point2(50f, 50f));   // over the first card: it stays hovered through the pan
        Assert.True(before);    // nothing moved yet: the retained slices still match
        Assert.False(after);    // the content moved under a hoist baked at its old offset: record, never composite
    }

    [Fact]
    public void WithNoHoverThePanStaysCompositeOnly()
    {
        var (before, after) = PanAtRest(new Point2(600f, 400f));  // off the shelf: nothing parked, nothing pinned
        Assert.True(before);
        Assert.True(after);
    }
}
