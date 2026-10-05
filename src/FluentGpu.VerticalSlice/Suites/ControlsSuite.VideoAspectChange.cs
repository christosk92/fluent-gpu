using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Scene;
using FluentGpu.Text;
using static FluentGpu.VerticalSlice.Harness.Gate;

// ── gate.media.el.aspect-change (L2-09: F127 same-pump aspect refit) ────────────────────────────────────────────────────
// A natural size published INSIDE the pump (first metadata, FORMATCHANGE, an ABR rung of another aspect) used to be placed
// against the hole as laid out for the PREVIOUS size, so the new frame was squashed into the old fit until the next render
// and layout. The pump now refits and re-places in the same turn (value-gated: at most one follow-up) and writes the hole's
// Margin straight away. HeadlessScriptedPlayer.NaturalSizeOnNextPump publishes the new size from inside PumpVideo; the gate
// reads the rect the second PumpVideo was handed and the hole's Margin after exactly ONE frame (a render-driven refit
// could only land on the following frame).
static partial class ControlsSuite
{
    static void VideoAspectChangeChecks(StringTable strings)
    {
        var (app, _, host, player, element) = RenderDietRig(strings, "l209-aspect-change", transport: false);
        using (app) using (host)
        {
            var reg = host.VideoSurfaces;
            int token = 0;
            for (int t = 1; t <= 16 && token == 0; t++) if (reg.IsPumpOwner(t, element)) token = t;
            var hole = FindVisual(host.Scene, host.Scene.Root, VisualKind.Video);
            if (token == 0 || hole.IsNull)
            {
                Check("gate.media.el.aspect-change", false, $"rig: pumpToken={token} holeFound={!hole.IsNull}");
                return;
            }
            for (int i = 0; i < 30; i++) host.RunFrame();   // settle the 16:9 fit and any settle timer

            NodeHandle area = host.Scene.Parent(hole);
            var portrait = new SizeI(480, 640);
            int calls0 = player.PumpVideoCalls;
            player.NaturalSizeOnNextPump = portrait;
            reg.RequestPump(token);
            host.RunFrame();

            RectF layoutArea = host.Scene.Bounds(area);
            RectF absArea = host.Scene.AbsoluteRect(area);
            Edges4 want = FluentGpu.Controls.Media.MediaPlayerElement.HoleInsets(layoutArea, portrait, VideoAspectMode.Uniform, 16.0 / 9.0, 1f);
            RectF wantRect = FluentGpu.Controls.Media.MediaPlayerElement.RectFromHoleInsets(absArea, layoutArea, want);
            RectF placed = player.LastPumpVideoRect;
            int pumps = player.PumpVideoCalls - calls0;
            Edges4 margin = host.Scene.Layout(hole).Margin;
            bool rectOk = player.NaturalSize.Peek() == portrait
                && MathF.Abs(placed.X - wantRect.X) < 0.51f && MathF.Abs(placed.Y - wantRect.Y) < 0.51f
                && MathF.Abs(placed.W - wantRect.W) < 0.51f && MathF.Abs(placed.H - wantRect.H) < 0.51f;
            Check("gate.media.el.aspect-change.same-pump-refit", want.Left > 0.5f && pumps >= 2 && rectOk,
                $"pillarbox={want.Left:0.#} pumps={pumps} (want >= 2) placed={placed.X:0.#},{placed.Y:0.#} {placed.W:0.#}x{placed.H:0.#} " +
                $"want={wantRect.X:0.#},{wantRect.Y:0.#} {wantRect.W:0.#}x{wantRect.H:0.#}");
            Check("gate.media.el.aspect-change.hole-margin-same-frame", margin == want,
                $"holeMargin=({margin.Left:0.#},{margin.Top:0.#},{margin.Right:0.#},{margin.Bottom:0.#}) " +
                $"want=({want.Left:0.#},{want.Top:0.#},{want.Right:0.#},{want.Bottom:0.#})");

            // The follow-up is one-shot: a settled size means a plain pump asks for no second PumpVideo.
            for (int i = 0; i < 30; i++) host.RunFrame();
            int calls1 = player.PumpVideoCalls;
            reg.RequestPump(token);
            host.RunFrame();
            int steady = player.PumpVideoCalls - calls1;
            Check("gate.media.el.aspect-change.no-loop", steady == 1,
                $"fullPumpsForOneRequestAtASettledSize={steady} (want 1)");
        }
    }
}
