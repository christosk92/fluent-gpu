using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Scene;
using FluentGpu.Text;
using static FluentGpu.VerticalSlice.Harness.Gate;

// ── gate.media.el.geometry-only-pump (L2-08: F120 geometry-only pumps) ──────────────────────────────────────────────────
// A pump requested only because the surface's absolute rect moved (a drag, an animated placement) places the surface and
// stops: it never reaches Player.PumpVideo (the session's state / buffering / position / cue publish). The settle timer
// then runs ONE full pump after the motion ends, so the final geometry is still published even though the host goes idle.
// HeadlessScriptedPlayer.PumpVideoCalls counts the full pumps; the registry's content size is seeded by hand because the
// headless player has no composited surface to size.
// The same holds for a LAYOUT-driven rect change (a PiP edge resize, a pop-out live resize, a reflow): the area's
// OnBoundsChanged raises the geometry-class request, so a resize that leaves the downscale cap untouched is placement-only
// too (gate.media.el.geometry-only-pump.layout-resize).
// Frames are driven with Paint(0), not RunFrame: a poked transform raises no wake and a pending settle timer is a future deadline, so RunFrame takes
// its idle early-out before the pump and the headless frame clock (which only Paint advances) never reaches the 200 ms settle.
static partial class ControlsSuite
{
    static void GeometryOnlyPumpChecks(StringTable strings)
    {
        GeometryOnlyPumpMoveCheck(strings);
        GeometryOnlyPumpLayoutCheck(strings);
    }

    static void GeometryOnlyPumpMoveCheck(StringTable strings)
    {
        var (app, _, host, player, element) = RenderDietRig(strings, "l208-geometry-only", transport: false);
        using (app) using (host)
        {
            var reg = host.VideoSurfaces;
            int token = 0;
            for (int t = 1; t <= 16 && token == 0; t++) if (reg.IsPumpOwner(t, element)) token = t;
            var hole = FindVisual(host.Scene, host.Scene.Root, VisualKind.Video);
            if (token == 0 || hole.IsNull)
            {
                Check("gate.media.el.geometry-only-pump", false, $"rig: pumpToken={token} holeFound={!hole.IsNull}");
                return;
            }

            SizeI natural = player.NaturalSize.Peek();
            SizeI content = VideoStreamSizing.ContentSizeFor(natural, host.Scene.AbsoluteRect(hole), 1f);
            reg.SetContentSize(token, (uint)content.Width, (uint)content.Height);   // the session's cached size, as after a full pump
            for (int i = 0; i < 30; i++) host.Paint(0);                            // let any adoption turn and its settle timer finish

            int calls0 = player.PumpVideoCalls;
            long runs0 = reg.PumpInvocationCount;
            // Whole-tree translation: every clipping ancestor moves with the element, so the viewport and the fitted rect
            // keep their size (a pure move), exactly like a PiP drag.
            for (int i = 1; i <= 8; i++)
            {
                host.Scene.Paint(host.Scene.Root).LocalTransform = Affine2D.Translation(10f * i, 0f);
                host.Paint(0);
            }
            long motionRuns = reg.PumpInvocationCount - runs0;
            int motionCalls = player.PumpVideoCalls - calls0;

            for (int i = 0; i < 30; i++) host.Paint(0);                            // > the 200 ms settle window at any frame time
            int settledCalls = player.PumpVideoCalls - calls0 - motionCalls;

            Check("gate.media.el.geometry-only-pump", motionRuns > 0 && motionCalls == 0 && settledCalls >= 1,
                $"pumpsDuringMotion={motionRuns} fullPumpsDuringMotion={motionCalls} (want 0) fullPumpsAfterSettle={settledCalls} (want >= 1)");
        }
    }

    static void GeometryOnlyPumpLayoutCheck(StringTable strings)
    {
        var (app, window, host, player, element) = RenderDietRig(strings, "l208-layout-resize", transport: false);
        using (app) using (host)
        {
            var reg = host.VideoSurfaces;
            int token = 0;
            for (int t = 1; t <= 16 && token == 0; t++) if (reg.IsPumpOwner(t, element)) token = t;
            // Grow the window past the 640x360 natural size: the downscale cap stays off (the content size is the natural
            // size) for every window size this check resizes to, so only the rect, never the stream size, changes.
            window.ClientSizePx = new Size2(800, 500);
            for (int i = 0; i < 30; i++) host.Paint(0);
            var hole = FindVisual(host.Scene, host.Scene.Root, VisualKind.Video);
            if (token == 0 || hole.IsNull)
            {
                Check("gate.media.el.geometry-only-pump.layout-resize", false, $"rig: pumpToken={token} holeFound={!hole.IsNull}");
                return;
            }

            SizeI natural = player.NaturalSize.Peek();
            SizeI content = VideoStreamSizing.ContentSizeFor(natural, host.Scene.AbsoluteRect(hole), 1f);
            reg.SetContentSize(token, (uint)content.Width, (uint)content.Height);   // the session's cached size, as after a full pump
            for (int i = 0; i < 30; i++) host.Paint(0);                            // let the seeding turn and any settle timer finish

            int calls0 = player.PumpVideoCalls;
            long runs0 = reg.PumpInvocationCount;
            window.ClientSizePx = new Size2(900, 520);
            for (int i = 0; i < 2; i++) host.Paint(0);
            window.ClientSizePx = new Size2(1000, 560);
            for (int i = 0; i < 2; i++) host.Paint(0);
            window.ClientSizePx = new Size2(860, 500);
            for (int i = 0; i < 2; i++) host.Paint(0);
            long motionRuns = reg.PumpInvocationCount - runs0;
            int motionCalls = player.PumpVideoCalls - calls0;

            for (int i = 0; i < 30; i++) host.Paint(0);                            // > the 200 ms settle window at any frame time
            int settledCalls = player.PumpVideoCalls - calls0 - motionCalls;

            Check("gate.media.el.geometry-only-pump.layout-resize",
                content == natural && motionRuns > 0 && motionCalls == 0 && settledCalls >= 1,
                $"contentIsNatural={content == natural} pumpsDuringResize={motionRuns} fullPumpsDuringResize={motionCalls} (want 0) fullPumpsAfterSettle={settledCalls} (want >= 1)");
        }
    }
}
