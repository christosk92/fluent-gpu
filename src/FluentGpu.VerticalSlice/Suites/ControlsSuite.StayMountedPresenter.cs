using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

// ── gate.video.presenter-stays-mounted (F179: the stay-mounted presenter, headless) ─────────────────────────────────────
// Wavee's header promises ONE stay-mounted presenter for Docked, the in-window PiP and fullscreen, and that the immersive
// stage covering a docked video (and a pop-out hand-off) only HIDES it. The pure rule (MainWindowHole) is unit-tested in the
// app; this is the engine half, driven through a real headless AppHost: a MediaPlayerElement under a surface box whose
// Visible, Width and Height are bound thunks (Wavee's PipSurface shape) is walked through every placement, the immersive
// cover and the hand-off. Across the whole walk the SAME element instance must stay mounted (one factory run), keep its
// VideoSurface slot (the same registry token, still the pump owner), keep a live hole whenever the surface is visible, and
// the session must keep being pumped: a pump requested in every step reaches Player.PumpVideo (the inert pump while the
// surface is covered, the placed one otherwise), so no step leaves the control plane without a pump.
static partial class ControlsSuite
{
    static void StayMountedPresenterChecks(StringTable strings)
    {
        const int Docked = 0, Floating = 1, Fullscreen = 2, Detached = 3;
        var placement = new Signal<int>(Docked);
        var immersive = new Signal<bool>(false);
        FluentGpu.Controls.Media.MediaPlayerElement? element = null;
        int factories = 0;

        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("stay-mounted-presenter", new Size2(560, 360), 1f));
        window.Show();
        var player = new HeadlessScriptedPlayer { OpenTicks = 0, BufferTicks = 0, DefaultDuration = TimeSpan.FromSeconds(120) };
        player.OpenAsync(MediaSource.FromSamples(new ScriptedSampleSource(
            TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(20), new SizeI(640, 360)))).GetAwaiter().GetResult();
        player.PlayAsync().GetAwaiter().GetResult();

        var root = new W0fStaticProbe
        {
            Build = () =>
            {
                int resolved = placement.Value;   // a placement edge re-renders the surface, exactly like PipSurface's `resolved` read
                return new BoxEl
                {
                    Grow = 1f, Direction = 1,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, ClipToBounds = true, ZStack = true,
                            Visible = Prop.Of(() => !(placement.Value == Detached || (placement.Value == Docked && immersive.Value))),
                            Width = Prop.Of(() => placement.Value switch { Fullscreen => 560f, Floating => 224f, _ => 320f }),
                            Height = Prop.Of(() => placement.Value switch { Fullscreen => 360f, Floating => 126f, _ => 180f }),
                            BorderWidth = resolved == Floating ? 1f : 0f,
                            Children =
                            [
                                FluentGpu.Hooks.Embed.Comp(() =>
                                {
                                    factories++;
                                    return element = new FluentGpu.Controls.Media.MediaPlayerElement
                                    {
                                        Player = player, SuppressTransport = true, AutoHideTransportControls = false,
                                    };
                                }),
                            ],
                        },
                    ],
                };
            },
        };
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        using (app) using (host)
        {
            host.RunFrame();
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();     // Opening → Buffering
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();     // Buffering → Playing
            for (int i = 0; i < 8; i++) host.Paint(0);

            var reg = host.VideoSurfaces;
            var first = element;
            int FindToken()
            {
                if (element is null) return 0;
                for (int t = 1; t <= 16; t++) if (reg.IsPumpOwner(t, element)) return t;
                return 0;
            }
            int token = FindToken();
            if (first is null || token == 0)
            {
                Check("gate.video.presenter-stays-mounted", false, $"rig: element={first is not null} token={token}");
                return;
            }

            var steps = new (int Placement, bool Immersive)[]
            {
                (Docked, false), (Docked, true), (Docked, false),
                (Floating, false), (Floating, true), (Floating, false),
                (Fullscreen, false), (Fullscreen, true), (Fullscreen, false),
                (Floating, false), (Detached, false), (Detached, true), (Docked, false),
            };
            var failures = new List<string>();
            foreach (var (p, imm) in steps)
            {
                placement.Value = p;
                immersive.Value = imm;
                for (int i = 0; i < 30; i++) host.Paint(0);
                int calls0 = player.PumpVideoCalls;
                reg.RequestPump(token);                 // a session raise / state edge in this step
                for (int i = 0; i < 30; i++) host.Paint(0);

                bool visible = !(p == Detached || (p == Docked && imm));
                bool sameElement = ReferenceEquals(element, first) && factories == 1;
                bool sameSlot = FindToken() == token;
                bool pumped = player.PumpVideoCalls > calls0;
                bool hole = !visible || !FindVisual(host.Scene, host.Scene.Root, VisualKind.Video).IsNull;
                if (!(sameElement && sameSlot && pumped && hole))
                    failures.Add($"[placement={p} immersive={imm}] sameElement={sameElement} factories={factories} sameSlot={sameSlot} "
                        + $"pumped={pumped} (calls {calls0}->{player.PumpVideoCalls}) holeWhenVisible={hole}");
            }

            Check("gate.video.presenter-stays-mounted the same MediaPlayerElement and VideoSurface slot survive every placement, the immersive cover and the pop-out hand-off, and the session is pumped in every step",
                failures.Count == 0, failures.Count == 0 ? $"steps={steps.Length} factories={factories} token={token}" : string.Join("; ", failures));
        }
    }
}
