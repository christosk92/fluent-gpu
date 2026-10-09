using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A video hole under a <c>.StickyClip</c> band line: the composite cuts the UI hole at the line, and the posed hole it
/// publishes (<see cref="VideoPosedHole.EffClip"/>, what the DirectComposition video's viewport follows) must carry that line too, or
/// the video keeps showing through the transparent band above it. Two routes lost it: a sticky slice (or a child of a sticky group)
/// the line has wholly cut published no posed hole at all, and a sticky slice composited as a GROUP handed the holes it encloses a
/// clip without the line. Serial: it constructs hosts.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class StickyVideoHoleTests
{
    private const int Token = 7;
    private const float Line = 40f;   // the band line: viewport top + the StickyClip inset (the viewport sits at window y 0)

    private sealed class Root(bool group) : Component
    {
        public override Element Render()
        {
            Element hole = new BoxEl { Width = 200f, Height = 100f, VideoHole = true, VideoSurfaceId = Token };
            // group: a WhileStuck edge fade over a shelf (a child slice) - a layer slice with children, composited as one group
            // surface (a video hole below it is never distributable); else a plain sticky slice holding the hole itself.
            BoxEl body = group
                ? new BoxEl
                {
                    Direction = 1,
                    EdgeFade = new EdgeFadeSpec(EdgeMask.Top, 24f) { WhileStuck = true },
                    Children =
                    [
                        new ScrollEl
                        {
                            Width = 320f, Height = 100f, Horizontal = true, SuppressScrollBar = true, EdgeCues = ScrollEdgeCues.None,
                            Content = new BoxEl { Width = 600f, Height = 100f, Children = [hole] },
                        },
                        new BoxEl { Width = 320f, Height = 400f },
                    ],
                }
                : new BoxEl { Direction = 1, Children = [hole] };
            return new ScrollEl
            {
                Width = 320f, Height = 200f, SuppressScrollBar = true, EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl
                {
                    Direction = 1,
                    Children = [new BoxEl { Width = 320f, Height = 40f }, body.StickyClip(Line), new BoxEl { Width = 320f, Height = 800f }],
                },
            };
        }
    }

    // The posed holes the composite publishes once the page is scrolled to `offset` (the hole sits at content y 40..140).
    private static VideoPosedHole[] PosedAt(bool group, float offset)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("sticky-video-hole", new Size2(320, 200), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(group));
        host.RunFrame();
        SceneStore s = host.Scene;
        NodeHandle FindScroll(NodeHandle n)
        {
            if (n.IsNull || s.HasScroll(n)) return n;
            for (NodeHandle c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                NodeHandle r = FindScroll(c);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }
        host.TryGetScrollHandle(FindScroll(s.Root))!.ScrollTo(offset, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
        for (int i = 0; i < 3; i++) host.RunFrame();
        return host.Scene.Recording.PosedVideoHoles.ToArray();
    }

    // What the video placement shows of the hole: the hole cut by the clip the composite published with it.
    private static RectF Shown(in VideoPosedHole h) => h.EffClip.IsInfinite ? h.Hole : h.Hole.Intersect(h.EffClip);

    [Fact]
    public void AStickySliceTheLineCutWhole_StillPostsItsHole_UnderTheClipThatHidesIt()
    {
        // offset 110: the slice spans window -70..30, wholly above the line at 40, yet its bottom 30 DIP are inside the viewport.
        VideoPosedHole[] holes = PosedAt(group: false, offset: 110f);
        VideoPosedHole h = Assert.Single(holes);
        Assert.Equal(Token, h.Token);
        Assert.True(Shown(in h).IsEmpty, $"the video still shows at {Shown(in h)} above the band line");
    }

    [Fact]
    public void AStickyGroupsHoles_TakeTheBandLine()
    {
        // offset 60: the shelf spans window -20..80, the line cuts it at 40.
        VideoPosedHole h = Assert.Single(PosedAt(group: true, offset: 60f));
        Assert.False(h.EffClip.IsInfinite);
        Assert.True(h.EffClip.Y >= Line - 0.5f, $"the video's clip top {h.EffClip.Y} is above the band line {Line}");
    }

    [Fact]
    public void AChildTheStickyGroupCulls_StillPostsItsHole_UnderTheClipThatHidesIt()
    {
        // offset 110: the shelf spans window -70..30, wholly above the line; the group (its body runs on) still places.
        VideoPosedHole h = Assert.Single(PosedAt(group: true, offset: 110f));
        Assert.True(Shown(in h).IsEmpty, $"the video still shows at {Shown(in h)} above the band line");
    }
}
