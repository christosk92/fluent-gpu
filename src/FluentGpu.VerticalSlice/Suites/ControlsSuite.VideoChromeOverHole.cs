using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── gate.video.chrome-over-hole (gpu-renderer.md §7.3 "Emit order", §13.1e) ─────────────────────────────────────────────
// The shipped defect: chrome recorded AFTER a video hole in the same slice segment — on-media transport, a mini-player's
// strip / ✕, captions, a loading notice — never showed over the video. The in-stream DrawVideo punches the hole inside
// the segment's tile and the chrome recorded after it paints back over it there; but the composite plan emitted the
// segment's EraseVideoHole item AFTER the segment, so that full-strength DestOut quad wiped the chrome every frame (the
// owner's log: `[media.chrome] show cause=enter/move`, nothing visible). The erase now goes BEFORE the punching segment:
// it clears what EARLIER items composited under the hole, then the segment composites over it.
//
// The headless model keeps no pixels, so the gate models the frame at three window points from the composed stream
// (every placed segment in painter order, HeadlessGpuDevice.LastComposedStream) and the erases recorded at their painter
// positions in it (LastHoleErases): the last writer at a point is the last fill covering it, or TRANSPARENT (the video
// shows) after the last DrawVideo punch / composite erase covering it.
//   · over the chrome   → the chrome. FAILS on the old order: the erase came after the segment, so after the chrome.
//   · in the bare hole  → transparent: the page (an EARLIER slice — the scroller's content) is cleared by the composite
//                         erase, the stage's own letterbox floor (same segment, before the hole) by the in-tile punch.
//   · outside the stage → the page: the erase never spills.
static partial class ControlsSuite
{
    sealed class VideoChromeOverHoleProbe : Component
    {
        public static readonly ColorF PageFill = ColorF.FromRgba(0x30, 0x60, 0x90);       // an earlier slice (scroll content)
        public static readonly ColorF LetterboxFill = ColorF.FromRgba(0x10, 0x10, 0x10);  // the stage floor: same segment, before the hole
        public static readonly ColorF ChromeFill = ColorF.FromRgba(0xFF, 0x80, 0x00);     // transport chrome: same segment, after the hole

        public override Element Render() => new BoxEl
        {
            Width = 480f, Height = 360f, ZStack = true,
            Children =
            [
                // The page under the video lives in its own (Scroll) slice, composited as an item BEFORE the stage's segment.
                new ScrollEl
                {
                    Width = 480f, Height = 360f, SuppressScrollBar = true, EdgeCues = ScrollEdgeCues.None,
                    Content = new BoxEl { Width = 480f, Height = 360f, Fill = PageFill },
                },
                // The video stage: its floor, the hole as its FIRST child, the chrome as a later sibling over the hole —
                // all one segment of the root slice.
                new BoxEl
                {
                    Width = 400f, Height = 225f, ZStack = true, Fill = LetterboxFill,
                    Children =
                    [
                        new BoxEl { Width = 400f, Height = 225f, VideoHole = true, VideoSurfaceId = 7 },
                        new BoxEl { Width = 400f, Height = 48f, Fill = ChromeFill },
                    ],
                },
            ],
        };
    }

    enum HoleTop : byte { Nothing, Page, Letterbox, Chrome, Video, Other }

    static void VideoChromeOverHoleChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("video-chrome-over-hole", new Size2(480, 360), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VideoChromeOverHoleProbe());
        for (int i = 0; i < 3; i++) host.RunFrame();

        var scene = host.Scene;
        var hole = FindVisual(scene, scene.Root, VisualKind.Video);
        var chrome = hole.IsNull ? NodeHandle.Null : scene.NextSibling(hole);
        var stage = hole.IsNull ? NodeHandle.Null : scene.Parent(hole);
        bool shaped = !hole.IsNull && !chrome.IsNull && !stage.IsNull;
        RectF holeR = shaped ? scene.AbsoluteRect(hole) : default;
        RectF chromeR = shaped ? scene.AbsoluteRect(chrome) : default;
        RectF stageR = shaped ? scene.AbsoluteRect(stage) : default;
        RectF over = holeR.Intersect(chromeR);
        var atChrome = new Point2(over.X + over.W * 0.5f, over.Y + over.H * 0.5f);
        // a point of the bare hole, clear of the chrome wherever the stack aligns it
        var atHole = new Point2(holeR.X + holeR.W * 0.5f, holeR.Y + holeR.H * 0.9f);
        if (chromeR.Contains(atHole)) atHole = new Point2(atHole.X, holeR.Y + holeR.H * 0.1f);
        // a point of the page clear of the stage (a window corner the stack leaves uncovered)
        var atPage = new Point2(470f, 350f);
        foreach (var corner in new[] { new Point2(470f, 350f), new Point2(10f, 350f), new Point2(470f, 10f), new Point2(10f, 10f) })
            if (!stageR.Contains(corner)) { atPage = corner; break; }
        // the three probe points sit where the scenario says they do
        shaped = shaped && !over.IsEmpty && !chromeR.Contains(atHole) && holeR.Contains(atHole) && !stageR.Contains(atPage);

        // The scenario's painter order in the composed stream: the page (an earlier item) and the stage's own ops.
        var erases = device.LastHoleErases;
        int eraseAt = erases.Count == 1 ? erases[0].ComposedOffset : -1;
        int enclosed = 0;
        foreach (var er in erases) if (er.GroupDepth > 0) enclosed++;
        FirstOffsets(device.LastComposedStream.Bytes, out int pageAt, out int letterboxAt, out int videoAt, out int chromeAt);
        bool scenario = shaped && erases.Count == 1 && enclosed == 0 && pageAt >= 0 && letterboxAt >= 0 && videoAt >= 0 && chromeAt >= 0
            && pageAt < eraseAt && letterboxAt < videoAt && videoAt < chromeAt;
        // The ordering contract itself: the erase precedes the segment that punched the hole (so its chrome).
        bool erasesFirst = eraseAt >= 0 && eraseAt <= letterboxAt && eraseAt < chromeAt;

        HoleTop topChrome = TopAt(device, atChrome), topHole = TopAt(device, atHole), topPage = TopAt(device, atPage);
        Check("gate.video.chrome-over-hole chrome recorded after a video hole in the same slice segment (on-media transport, a mini-player strip) stays visible over the video: the composite erases the hole BEFORE the segment that punched it — clearing the page an earlier slice composited under it — so the segment's own tile, whose hole already has the chrome painted back over it, composites last",
            scenario && erasesFirst && topChrome == HoleTop.Chrome && topHole == HoleTop.Video && topPage == HoleTop.Page,
            $"shaped={shaped} erases={erases.Count} enclosed={enclosed} at(page={pageAt} erase={eraseAt} letterbox={letterboxAt} "
            + $"video={videoAt} chrome={chromeAt}) top(chrome={topChrome} hole={topHole} page={topPage}) "
            + $"hole={holeR.X:0},{holeR.Y:0},{holeR.W:0}x{holeR.H:0} chrome={chromeR.X:0},{chromeR.Y:0},{chromeR.W:0}x{chromeR.H:0} "
            + $"items={host.UiSlices.LastItems.Length}");

        VideoHoleShapeChecks(strings);
    }

    /// <summary>The hole of <see cref="VideoHoleShapeChecks"/>: a stage whose hole is rounded on its TOP corners only and drawn
    /// at half opacity, at a size that lands on fractional device pixels at the gate's 1.5 scale.</summary>
    sealed class VideoRoundedHoleProbe : Component
    {
        public static readonly CornerRadius4 HoleCorners = new(24f, 24f, 0f, 0f);
        public const float HoleOpacity = 0.5f;

        public override Element Render() => new BoxEl
        {
            Width = 480f, Height = 360f, ZStack = true,
            Children =
            [
                new ScrollEl
                {
                    Width = 480f, Height = 360f, SuppressScrollBar = true, EdgeCues = ScrollEdgeCues.None,
                    Content = new BoxEl { Width = 480f, Height = 360f, Fill = VideoChromeOverHoleProbe.PageFill },
                },
                new BoxEl
                {
                    Width = 400f, Height = 225f, ZStack = true, Fill = VideoChromeOverHoleProbe.LetterboxFill,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 333.2f, Height = 187.3f, VideoHole = true, VideoSurfaceId = 7,
                            Corners = HoleCorners, Opacity = HoleOpacity,
                        },
                    ],
                },
            ],
        };
    }

    // ── gate.video.hole-erase-shape (gpu-renderer.md §7.3, F078 + F073 pixel rule R) ─────────────────────────────────────
    // The composite erase of a video hole used to be a full-strength, square, sub-pixel quad: under an attenuated or rounded
    // hole it cleared the page at 100% and in the four corner wedges outside the rounded shape, and its edges were not the
    // whole device pixels the DirectComposition video rect covers. The erase item now carries the punch's own strength
    // (VideoReady x opacity), the hole's rounded rect (per-corner radii) and whole-pixel edges (each of X, Y, Right and Bottom
    // rounded independently, away from zero). A square, opaque hole keeps the plain full-strength erase.
    static void VideoHoleShapeChecks(StringTable strings)
    {
        // baseline: the square, opaque hole of the chrome-over-hole scenario
        bool baseline;
        string baseDetail;
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("video-hole-shape-square", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new VideoChromeOverHoleProbe());
            for (int i = 0; i < 3; i++) host.RunFrame();
            var erases = device.LastHoleErases;
            baseline = erases.Count == 1 && Near(erases[0].Alpha, 1f, 0.001f) && erases[0].RoundRectPx.IsEmpty
                && erases[0].Radii.TopLeft == 0f && erases[0].Radii.BottomRight == 0f;
            baseDetail = erases.Count == 1 ? $"alpha={erases[0].Alpha:0.###} round={erases[0].RoundRectPx.W:0.#}" : $"erases={erases.Count}";
        }

        // the rounded, attenuated hole at scale 1.5 (window 720x540 px = 480x360 DIP)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("video-hole-shape-rounded", new Size2(720, 540), 1.5f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new VideoRoundedHoleProbe());
            for (int i = 0; i < 3; i++) host.RunFrame();

            var scene = host.Scene;
            var hole = FindVisual(scene, scene.Root, VisualKind.Video);
            RectF holeR = hole.IsNull ? default : scene.AbsoluteRect(hole);
            float s = device.LastCompositeInfo.Scale > 0f ? device.LastCompositeInfo.Scale : 1f;
            // pixel rule R, evaluated independently of the recorder: each edge to the nearest whole pixel, away from zero
            float x0 = MathF.Round(holeR.X * s, MidpointRounding.AwayFromZero), y0 = MathF.Round(holeR.Y * s, MidpointRounding.AwayFromZero);
            float x1 = MathF.Round(holeR.X * s + holeR.W * s, MidpointRounding.AwayFromZero);
            float y1 = MathF.Round(holeR.Y * s + holeR.H * s, MidpointRounding.AwayFromZero);

            var erases = device.LastHoleErases;
            bool one = !hole.IsNull && erases.Count == 1 && erases[0].GroupDepth == 0;
            CompositeHoleErase er = one ? erases[0] : default;
            bool whole = one && er.RectPx.X == MathF.Floor(er.RectPx.X) && er.RectPx.Y == MathF.Floor(er.RectPx.Y)
                && er.RectPx.W == MathF.Floor(er.RectPx.W) && er.RectPx.H == MathF.Floor(er.RectPx.H);
            bool ruleR = one && er.RectPx.X == x0 && er.RectPx.Y == y0 && er.RectPx.Right == x1 && er.RectPx.Bottom == y1;
            bool strength = one && Near(er.Alpha, VideoRoundedHoleProbe.HoleOpacity, 0.001f);
            float r = VideoRoundedHoleProbe.HoleCorners.TopLeft * s;
            bool radii = one && Near(er.Radii.TopLeft, r, 0.01f) && Near(er.Radii.TopRight, r, 0.01f)
                && er.Radii.BottomRight == 0f && er.Radii.BottomLeft == 0f;
            // the rounded rect IS the whole-pixel hole (nothing cut it), so the wedges outside the curve stay with the page
            bool shape = one && er.RoundRectPx.X == er.RectPx.X && er.RoundRectPx.Y == er.RectPx.Y
                && er.RoundRectPx.W == er.RectPx.W && er.RoundRectPx.H == er.RectPx.H;

            Check("gate.video.hole-erase-shape the composite erase of a video hole carries the punch's own strength (VideoReady x opacity), its rounded rect with per-corner radii and whole-pixel edges (pixel rule R: X, Y, Right and Bottom each rounded to the nearest device pixel), so a faded or rounded hole no longer clears the page at full strength / in the corner wedges; a square opaque hole stays the plain full-strength erase",
                baseline && one && whole && ruleR && strength && radii && shape,
                $"baseline={baseline} ({baseDetail}) erases={erases.Count} whole={whole} ruleR={ruleR} "
                + $"rect={er.RectPx.X:0.##},{er.RectPx.Y:0.##},{er.RectPx.W:0.##}x{er.RectPx.H:0.##} want={x0:0.##},{y0:0.##},{x1 - x0:0.##}x{y1 - y0:0.##} "
                + $"alpha={er.Alpha:0.###} radii={er.Radii.TopLeft:0.##}/{er.Radii.TopRight:0.##}/{er.Radii.BottomRight:0.##}/{er.Radii.BottomLeft:0.##} "
                + $"round={er.RoundRectPx.X:0.##},{er.RoundRectPx.Y:0.##},{er.RoundRectPx.W:0.##}x{er.RoundRectPx.H:0.##}");
        }
    }

    /// <summary>The composed-stream byte offset of the first page fill, letterbox fill, DrawVideo and chrome fill (−1 = none).</summary>
    static void FirstOffsets(ReadOnlySpan<byte> bytes, out int pageAt, out int letterboxAt, out int videoAt, out int chromeAt)
    {
        pageAt = letterboxAt = videoAt = chromeAt = -1;
        int pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes[pos..]);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > bytes.Length) break;
            ReadOnlySpan<byte> p = bytes.Slice(pos + sizeof(int), body);
            if (op == DrawOp.DrawVideo && videoAt < 0) videoAt = pos;
            else if (op == DrawOp.FillRoundRect)
            {
                ColorF fill = MemoryMarshal.Read<FillRoundRectCmd>(p).Fill;
                if (pageAt < 0 && ColorApprox(fill, VideoChromeOverHoleProbe.PageFill)) pageAt = pos;
                else if (letterboxAt < 0 && ColorApprox(fill, VideoChromeOverHoleProbe.LetterboxFill)) letterboxAt = pos;
                else if (chromeAt < 0 && ColorApprox(fill, VideoChromeOverHoleProbe.ChromeFill)) chromeAt = pos;
            }
            pos += sizeof(int) + body;
        }
    }

    /// <summary>What the composited frame shows at window DIP <paramref name="pt"/>: the composed stream replayed in painter
    /// order with the recorded composite erases interleaved at their offsets — a fill covering the point sets its colour, a
    /// DrawVideo / EraseRoundRect punch or a back-buffer EraseVideoHole covering it clears it to the video beneath.</summary>
    static HoleTop TopAt(HeadlessGpuDevice dev, Point2 pt)
    {
        float scale = dev.LastCompositeInfo.Scale > 0f ? dev.LastCompositeInfo.Scale : 1f;
        IReadOnlyList<CompositeHoleErase> erases = dev.LastHoleErases;
        ReadOnlySpan<byte> bytes = dev.LastComposedStream.Bytes;
        var clips = new List<RectF>();
        HoleTop top = HoleTop.Nothing;
        int e = 0, pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            for (; e < erases.Count && erases[e].ComposedOffset <= pos; e++) top = Erased(erases[e], pt, scale, top);
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes[pos..]);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > bytes.Length) break;
            ReadOnlySpan<byte> p = bytes.Slice(pos + sizeof(int), body);
            switch (op)
            {
                case DrawOp.PushClip: clips.Add(MemoryMarshal.Read<ClipCmd>(p).DeviceRect); break;
                case DrawOp.PushStencilClip: clips.Add(MemoryMarshal.Read<PushStencilClipCmd>(p).DeviceRect); break;
                case DrawOp.PopClip:
                case DrawOp.PopStencilClip:
                    if (clips.Count > 0) clips.RemoveAt(clips.Count - 1);
                    break;
                case DrawOp.FillRoundRect:
                {
                    var c = MemoryMarshal.Read<FillRoundRectCmd>(p);
                    if (c.Fill.A > 0f && c.Opacity > 0f && Covers(c.Transform.TransformBounds(c.Rect), clips, pt)) top = Classify(c.Fill);
                    break;
                }
                case DrawOp.DrawVideo:
                {
                    var c = MemoryMarshal.Read<DrawVideoCmd>(p);
                    if (c.VideoReady > 0f && c.Opacity > 0f && Covers(c.Transform.TransformBounds(c.Dst), clips, pt)) top = HoleTop.Video;
                    break;
                }
                case DrawOp.EraseRoundRect:
                {
                    var c = MemoryMarshal.Read<EraseRoundRectCmd>(p);
                    if (c.Strength > 0f && c.Opacity > 0f && Covers(c.Transform.TransformBounds(c.Rect), clips, pt)) top = HoleTop.Video;
                    break;
                }
            }
            pos += sizeof(int) + body;
        }
        for (; e < erases.Count; e++) top = Erased(erases[e], pt, scale, top);
        return top;

        static HoleTop Erased(in CompositeHoleErase er, Point2 pt, float scale, HoleTop top)
        {
            if (er.GroupDepth > 0) return top;   // it clears a group surface, not the back buffer
            var dip = new RectF(er.RectPx.X / scale, er.RectPx.Y / scale, er.RectPx.W / scale, er.RectPx.H / scale);
            return dip.Contains(pt) ? HoleTop.Video : top;
        }

        static bool Covers(RectF r, List<RectF> clips, Point2 pt)
        {
            if (!r.Contains(pt)) return false;
            foreach (var c in clips) if (!c.Contains(pt)) return false;
            return true;
        }

        static HoleTop Classify(ColorF fill)
            => ColorApprox(fill, VideoChromeOverHoleProbe.PageFill) ? HoleTop.Page
             : ColorApprox(fill, VideoChromeOverHoleProbe.LetterboxFill) ? HoleTop.Letterbox
             : ColorApprox(fill, VideoChromeOverHoleProbe.ChromeFill) ? HoleTop.Chrome
             : HoleTop.Other;
    }
}
