using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

// ── gate.video.overlay-verdict (F087, gpu-renderer.md §7.3) ─────────────────────────────────────────────────────────────
// The overlay mode promotes a video above the UI plane only when nothing paints over its rect. The composite plan's occlusion
// verdict (VideoPosedHole.Unoccluded, published with every posed hole) is that proof, judged on painter order: the ops recorded
// after the DrawVideo in its own segment, then every later segment / slice / backdrop / hole.
//   · a bare stage (the page under it is an EARLIER slice)            → clear: the video may be promoted.
//   · chrome recorded after the hole in the same segment             → covered: it stays an underlay.
//   · a LATER slice (a scroller) painted over part of the hole        → covered.
//   · a hole under an ancestor with Opacity < 1 (a page / card fade)   → covered: the underlay leaves part of the UI over the video and
//     the promoted DirectComposition visual has no opacity to reproduce that.
//   · a hole inside an ancestor's ROUNDED clip                         → covered: the presenter rounds only by the element's own corner
//     radius, so a promoted video would show square corners over the UI outside the ancestor's clip.
static partial class ControlsSuite
{
    sealed class VideoOverlayVerdictProbe : Component
    {
        public enum Scenario : byte { Bare, ChromeAfter, LaterSlice, FadedAncestor, RoundedClip }

        private readonly Scenario _scenario;
        public VideoOverlayVerdictProbe(Scenario scenario) { _scenario = scenario; }

        public override Element Render() => new BoxEl
        {
            Width = 480f, Height = 360f, ZStack = true,
            Children = Stack(),
        };

        private Element[] Stack()
        {
            if (_scenario == Scenario.LaterSlice)
                return
                [
                    new BoxEl
                    {
                        Width = 400f, Height = 225f, ZStack = true, Fill = VideoChromeOverHoleProbe.LetterboxFill,
                        Children = [new BoxEl { Width = 400f, Height = 225f, VideoHole = true, VideoSurfaceId = 7 }],
                    },
                    new ScrollEl
                    {
                        Width = 200f, Height = 100f, SuppressScrollBar = true, EdgeCues = ScrollEdgeCues.None,
                        Content = new BoxEl { Width = 200f, Height = 100f, Fill = VideoChromeOverHoleProbe.ChromeFill },
                    },
                ];
            // the page under the video lives in its own (Scroll) slice, an item BEFORE the stage's segment
            Element page = new ScrollEl
            {
                Width = 480f, Height = 360f, SuppressScrollBar = true, EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl { Width = 480f, Height = 360f, Fill = VideoChromeOverHoleProbe.PageFill },
            };
            Element hole = new BoxEl { Width = 400f, Height = 225f, VideoHole = true, VideoSurfaceId = 7 };
            Element[] stageChildren = _scenario == Scenario.ChromeAfter
                ? [hole, new BoxEl { Width = 400f, Height = 48f, Fill = VideoChromeOverHoleProbe.ChromeFill }]
                : [hole];
            BoxEl stage = new() { Width = 400f, Height = 225f, ZStack = true, Fill = VideoChromeOverHoleProbe.LetterboxFill, Children = stageChildren };
            if (_scenario == Scenario.FadedAncestor) stage = new BoxEl { Width = 400f, Height = 225f, ZStack = true, Opacity = 0.6f, Children = [stage] };
            else if (_scenario == Scenario.RoundedClip)
                stage = new BoxEl { Width = 400f, Height = 225f, ZStack = true, ClipToBounds = true, Corners = new CornerRadius4(32f, 32f, 32f, 32f), Children = [stage] };
            return [page, stage];
        }
    }

    // 1 = clear, 0 = covered, -1 = the scenario did not publish exactly one posed hole.
    static int OverlayVerdictOf(StringTable strings, VideoOverlayVerdictProbe.Scenario scenario)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("video-overlay-verdict", new Size2(480, 360), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VideoOverlayVerdictProbe(scenario));
        for (int i = 0; i < 3; i++) host.RunFrame();
        var holes = host.Scene.Recording.PosedVideoHoles;
        return holes.Length != 1 ? -1 : holes[0].Unoccluded ? 1 : 0;
    }

    static void VideoOverlayVerdictChecks(StringTable strings)
    {
        int bare = OverlayVerdictOf(strings, VideoOverlayVerdictProbe.Scenario.Bare);
        int chrome = OverlayVerdictOf(strings, VideoOverlayVerdictProbe.Scenario.ChromeAfter);
        int later = OverlayVerdictOf(strings, VideoOverlayVerdictProbe.Scenario.LaterSlice);
        int faded = OverlayVerdictOf(strings, VideoOverlayVerdictProbe.Scenario.FadedAncestor);
        int rounded = OverlayVerdictOf(strings, VideoOverlayVerdictProbe.Scenario.RoundedClip);
        Check("gate.video.overlay-verdict the composite's occlusion verdict of a video hole is clear only when nothing paints over it after the video's own DrawVideo: a bare stage over an earlier page slice is clear (the video may be promoted above the UI plane), chrome recorded after the hole in the same segment, a later slice painted over it, a hole under an Opacity<1 ancestor (the promoted visual has no opacity) and a hole inside an ancestor's rounded clip (the presenter rounds only by the element's own radius) are covered (the video stays a hole-punched underlay), so a promoted video can never hide UI",
            bare == 1 && chrome == 0 && later == 0 && faded == 0 && rounded == 0,
            $"bare={bare} chromeAfter={chrome} laterSlice={later} fadedAncestor={faded} roundedClip={rounded} (1 clear, 0 covered, -1 no single posed hole)");
    }
}
