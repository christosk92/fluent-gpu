using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A keyed swap whose two branches name the SAME authored <see cref="ScrollHandle"/> (a page-owned handle across two arms of one page:
/// a cross-fade between a split and a one-column layout): the incoming viewport mounts while the outgoing one still fades out as an exit
/// orphan. The handle belongs to the live successor — a programmatic move on it lands on the incoming viewport and stays there for the
/// whole exit — and the leaving viewport keeps painting at the offset it showed, from a handle of its own (AppHost.ResolveScrollHandle).
/// Before, the host re-bound the one handle to each viewport in turn every frame, re-seeding both plans from whichever offset was shown last.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class SharedScrollHandleSwapTests
{
    private const float ViewH = 400f, RowH = 40f;

    // A long exit, so the orphan is still painting for every frame the fact runs.
    private static readonly LayoutTransition s_fade = new(TransitionChannels.Opacity, TransitionDynamics.Tween(2000f),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(2000f));

    private sealed class Arms : Component
    {
        public readonly Signal<string> Arm = new("a");
        public readonly ScrollHandle Shared = new();
        public readonly Dictionary<string, NodeHandle> Vp = new();

        public override Element Render()
        {
            string arm = Arm.Value;
            var rows = new Element[100];
            for (int i = 0; i < rows.Length; i++) rows[i] = new BoxEl { Height = RowH, Fill = ColorF.FromRgba(40, 40, (byte)(i % 2 == 0 ? 40 : 60)) };
            return new BoxEl
            {
                ZStack = true, Width = 300f, Height = ViewH,
                Children =
                [
                    new BoxEl
                    {
                        Key = "arm:" + arm, Animate = s_fade, Width = 300f, Height = ViewH,
                        Children =
                        [
                            new ScrollEl
                            {
                                Width = 300f, Height = ViewH, Handle = Shared, OnRealized = h => Vp[arm] = h,
                                Content = new BoxEl { Direction = 1, Children = rows },
                            },
                        ],
                    },
                ],
            };
        }
    }

    [Fact]
    public void ASharedHandleFollowsTheIncomingViewportWhileTheOutgoingOneFadesOut()
    {
        using var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("shared-scroll-handle", new Size2(300, ViewH), 1f));
        window.Show();
        var root = new Arms();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 4; i++) host.RunFrame();

        double Offset(string arm) => host.Scene.TryGetScroll(root.Vp[arm], out var s) ? s.Offset : double.NaN;

        root.Shared.ScrollTo(200.0, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) host.RunFrame();
        Assert.Equal(200.0, Offset("a"), 0.5);

        root.Arm.Value = "b";                                   // the swap: b mounts, a becomes an exit orphan
        for (int i = 0; i < 3; i++) host.RunFrame();
        Assert.True(host.Scene.IsLive(root.Vp["a"]), "the leaving viewport is still fading out");

        root.Shared.ScrollTo(800.0, ScrollMove.Immediate);      // a move on the shared handle is the incoming viewport's
        for (int i = 0; i < 6; i++) host.RunFrame();
        Assert.True(host.Scene.IsLive(root.Vp["a"]), "the leaving viewport is still fading out");
        Assert.Equal(800.0, Offset("b"), 0.5);
        Assert.Equal(200.0, Offset("a"), 0.5);                  // the leaving one paints where it was
        Assert.Equal(800.0, root.Shared.OffsetNow, 0.5);
    }
}
