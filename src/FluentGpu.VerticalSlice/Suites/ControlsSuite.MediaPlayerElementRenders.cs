using System;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── gate.media.el.render-diet.* (L2-07: MediaPlayerElement render diet) ─────────────────────────────────────────────────
// The element's Render rebuilds its children arrays, about a dozen closures, the handler delegates and the context-menu
// wrapper, then reconciles stage, chrome and transport. It must therefore run only for the inputs that change WHAT it
// builds: per-segment buffered-amount churn (F123), caption cue changes (F134) and the area a suppressed transport never
// shows (F124) must not reach it. MediaPlayerElement.RenderCount counts exactly its own Render runs, so each gate reads
// the delta across a burst of the churning input; a positive control (a mounted transport DOES depend on the area, a cue
// DOES reach the caption leaf) proves the burst really changed something the element could have reacted to.
static partial class ControlsSuite
{
    static (HeadlessPlatformApp App, HeadlessWindow Window, AppHost Host, HeadlessScriptedPlayer Player,
        FluentGpu.Controls.Media.MediaPlayerElement Element) RenderDietRig(StringTable strings, string name, bool transport)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(560, 360), 1f));
        window.Show();
        var player = new HeadlessScriptedPlayer { OpenTicks = 0, BufferTicks = 0, DefaultDuration = TimeSpan.FromSeconds(120) };
        player.OpenAsync(MediaSource.FromSamples(new ScriptedSampleSource(
            TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(20), new SizeI(640, 360)))).GetAwaiter().GetResult();
        player.PlayAsync().GetAwaiter().GetResult();
        // Wavee's shape: a host-owned transport (suppressed here) and no idle machine, so nothing but the churning input
        // under test can wake the element. The transport variant mounts the engine's own for the positive control.
        var element = new FluentGpu.Controls.Media.MediaPlayerElement
        {
            Player = player,
            SuppressTransport = !transport,
            AutoHideTransportControls = false,
        };
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, element);
        host.RunFrame();
        player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();     // Opening → Buffering
        player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();     // Buffering → Playing
        for (int i = 0; i < 8; i++) host.Paint(0);                      // settle layout, areaBounds and the leaves
        return (app, window, host, player, element);
    }

    static void MediaPlayerElementRendersChecks(StringTable strings)
    {
        // F123: the protected pump republishes BufferingInfo for every appended segment. The element reads only
        // (IsBuffering, Reason) through a memo, so once the reason is up, percent / buffered-amount churn re-renders
        // nothing in it. A Seeking reason behind a presented frame is silent (no overlay, no delay timer), so the burst
        // can never be confused with an overlay edge.
        {
            var (app, _, host, player, element) = RenderDietRig(strings, "l207-buffer", transport: false);
            using (app) using (host)
            {
                player.Core.SetBuffering(new BufferingInfo(BufferingReason.Seeking, 0.05, TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(10), false));
                host.Paint(0); host.Paint(0);
                int afterEdge = element.RenderCount;      // the (None → Seeking) edge itself may render once
                for (int i = 1; i <= 12; i++)
                {
                    player.Core.SetBuffering(new BufferingInfo(BufferingReason.Seeking, 0.05 + 0.02 * i,
                        TimeSpan.FromMilliseconds(500 + 200 * i), TimeSpan.FromSeconds(10), false));
                    host.Paint(0);
                }
                int churn = element.RenderCount - afterEdge;
                Check("gate.media.el.render-diet.buffering-churn", churn == 0,
                    $"elementRendersAcross12PercentTicks={churn} (want 0; the raw Buffering read re-rendered per tick)");
            }
        }

        // F134: a caption cue start or clear re-renders only the caption leaf. Positive control: the cue's text really
        // appears in the scene and disappears again, so the leaf did react.
        {
            var (app, _, host, player, element) = RenderDietRig(strings, "l207-cue", transport: false);
            using (app) using (host)
            {
                const string text = "Caption under test";
                int before = element.RenderCount;
                player.Core.SetActiveCue(new TimedCue(TimeSpan.Zero, TimeSpan.FromSeconds(5), text, new CueStyle(1f, false, 0u), null));
                host.Paint(0); host.Paint(0);
                bool shown = !FindTextNode(host.Scene, strings, host.Scene.Root, text).IsNull;
                player.Core.SetActiveCue(null);
                host.Paint(0); host.Paint(0);
                bool cleared = FindTextNode(host.Scene, strings, host.Scene.Root, text).IsNull;
                int renders = element.RenderCount - before;
                Check("gate.media.el.render-diet.caption-cues", shown && cleared && renders == 0,
                    $"cueShown={shown} cueCleared={cleared} elementRenders={renders} (want 0)");
            }
        }

        // F124: a suppressed transport (Wavee) does not make the element depend on the area; the hole still follows the
        // resize through SyncHoleLetterbox in the same solve. Positive control: with the engine transport mounted the
        // width tier DOES depend on the area, so the same resize re-renders the element.
        {
            var (app, window, host, _, element) = RenderDietRig(strings, "l207-resize", transport: false);
            using (app) using (host)
            {
                int before = element.RenderCount;
                window.ClientSizePx = new Size2(400, 300);
                for (int i = 0; i < 4; i++) host.RunFrame();
                window.ClientSizePx = new Size2(520, 240);
                for (int i = 0; i < 4; i++) host.RunFrame();
                int renders = element.RenderCount - before;

                var hole = FindVisual(host.Scene, host.Scene.Root, VisualKind.Video);
                var area = hole.IsNull ? NodeHandle.Null : host.Scene.Parent(hole);
                RectF areaRect = area.IsNull ? default : host.Scene.AbsoluteRect(area);
                RectF holeRect = hole.IsNull ? default : host.Scene.AbsoluteRect(hole);
                RectF expected = FluentGpu.Controls.Media.MediaPlayerElement.FitVideoRectSnapped(
                    areaRect, new SizeI(640, 360), VideoAspectMode.Uniform, 16.0 / 9.0, 1f);
                bool fitted = areaRect.W > 0f && Near(holeRect.X, expected.X) && Near(holeRect.Y, expected.Y)
                    && Near(holeRect.W, expected.W) && Near(holeRect.H, expected.H);
                Check("gate.media.el.render-diet.resize-no-area-subscription", renders == 0 && fitted,
                    $"elementRenders={renders} (want 0) holeFitsNewArea={fitted} hole={holeRect.X:0.#},{holeRect.Y:0.#},{holeRect.W:0.#}x{holeRect.H:0.#} "
                    + $"want={expected.X:0.#},{expected.Y:0.#},{expected.W:0.#}x{expected.H:0.#}");
            }

            var (app2, window2, host2, _, element2) = RenderDietRig(strings, "l207-resize-transport", transport: true);
            using (app2) using (host2)
            {
                int before = element2.RenderCount;
                window2.ClientSizePx = new Size2(400, 300);
                for (int i = 0; i < 4; i++) host2.RunFrame();
                int renders = element2.RenderCount - before;
                Check("gate.media.el.render-diet.resize-transport-control", renders > 0,
                    $"elementRenders={renders} (want > 0: a mounted transport reads the area width)");
            }
        }

        // The pure inputs and the structure (source scan, like the sibling gate.media.el.* shape checks): the element's
        // Render reads neither the raw Buffering value nor the active cue, and the caption slot is a permanently mounted
        // leaf rather than a presence-gated child.
        {
            string? el = ReadRepoFile("src/FluentGpu.Controls/Media/MediaPlayerElement.cs");
            string? cap = ReadRepoFile("src/FluentGpu.Controls/Media/MediaCaptionOverlay.cs");
            bool found = el is not null && cap is not null;
            bool noRawReads = el is not null
                && !el.Contains("Player.Buffering.Value;", StringComparison.Ordinal)
                && !el.Contains("Player.ActiveCue.Value;", StringComparison.Ordinal)
                && el.Contains("UseComputed(() => BufferingKeyOf(Player.Buffering.Value))", StringComparison.Ordinal);
            bool captionLeaf = el is not null && cap is not null
                && el.Contains("\"media-caption-slot\"", StringComparison.Ordinal)
                && cap.Contains("Player.ActiveCue.Value", StringComparison.Ordinal)
                && cap.Contains("class MediaCaptionOverlay : Component", StringComparison.Ordinal);
            bool gatedArea = el is not null
                && el.Contains("transportMounted ? areaBounds.Value : areaBounds.Peek()", StringComparison.Ordinal);
            Check("gate.media.el.render-diet.structure", found && noRawReads && captionLeaf && gatedArea,
                $"found={found} noRawReads={noRawReads} captionLeaf={captionLeaf} gatedArea={gatedArea}");
        }
    }
}
