using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The main-window reveal gate (EP8, <see cref="AppHost.BeginRevealGate"/>): a hidden window is shown once, after its first frame
/// has presented with the application's fonts ready, never before; the material callback runs before the show; the watchdog shows
/// the window anyway when that frame is not coming; a stood-down present is not a reveal; and the gate re-arms for a later
/// show-from-hidden (tray restore). Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class RevealGateTests
{
    private sealed class Root : Component
    {
        public override Element Render() => new BoxEl { Width = 100f, Height = 100f, Fill = ColorF.FromRgba(40, 40, 40) };
    }

    private sealed class Rig : IDisposable
    {
        private readonly HeadlessPlatformApp _app = new();
        public readonly HeadlessWindow Window;
        public readonly HeadlessGpuDevice Device = new();
        public readonly AppHost Host;
        /// <summary>Ordered record of the host's calls around the show.</summary>
        public readonly List<string> Log = new();
        public RevealTiming? Revealed;

        public Rig()
        {
            var strings = new StringTable();
            Window = new HeadlessWindow(new WindowDesc("reveal-gate", new Size2(320, 240), 1f, Composited: true)) { IsVisible = false };
            Host = new AppHost(_app, Window, Device, new HeadlessFontSystem(strings), strings, new Root());
            Window.ShowObserved = () => Log.Add("show presents=" + Presents);
            Host.OnApplyThemeMaterial = dark => Log.Add("material");
            Host.MainRevealed += t => Revealed = t;
        }

        public int Presents => Device.PrimarySwapchain?.PresentCount ?? 0;

        /// <summary>Frames until the window has been shown (or <paramref name="max"/>), with a short real sleep so a watchdog can elapse.</summary>
        public int RunUntilShown(int showCalls = 1, int max = 200, int sleepMs = 0)
        {
            int n = 0;
            while (Window.ShowCalls < showCalls && n < max) { Host.RunFrame(); n++; if (sleepMs > 0) Thread.Sleep(sleepMs); }
            return n;
        }

        public void Dispose() { Host.Dispose(); _app.Dispose(); }
    }

    [Fact]
    public void Show_Happens_After_The_First_Present_With_Fonts_Ready_Never_Before()
    {
        using var rig = new Rig();
        bool fontsReady = false;
        rig.Host.BeginRevealGate(new RevealGateOptions { FirstFrameReady = () => fontsReady, TimeoutMs = 60_000 });
        Assert.True(rig.Host.IsRevealPending);

        for (int i = 0; i < 6; i++) rig.Host.RunFrame();
        Assert.Equal(0, rig.Window.ShowCalls);          // fonts not ready: frames ran, nothing shown
        Assert.True(rig.Host.IsRevealPending);
        Assert.True(rig.Presents > 0, "the hidden window should still have painted its frames");
        Assert.Empty(rig.Log);

        int presentsAtReady = rig.Presents;
        fontsReady = true;
        rig.RunUntilShown(max: 20);

        Assert.Equal(1, rig.Window.ShowCalls);
        Assert.False(rig.Host.IsRevealPending);
        Assert.Equal(2, rig.Log.Count);
        Assert.Equal("material", rig.Log[0]);            // the material and dark attribute are applied before Show
        Assert.StartsWith("show presents=", rig.Log[1]);
        Assert.True(rig.Presents > presentsAtReady, "the frame built with fonts ready must have presented before the show");
        Assert.NotNull(rig.Revealed);
        Assert.False(rig.Revealed!.Value.TimedOut);
        Assert.True(rig.Revealed.Value.ReadyMs >= 0);
    }

    [Fact]
    public void Without_A_Predicate_The_First_Present_Reveals()
    {
        using var rig = new Rig();
        rig.Host.BeginRevealGate();
        Assert.Equal(0, rig.Window.ShowCalls);           // nothing has run yet
        rig.RunUntilShown(max: 20);
        Assert.Equal(1, rig.Window.ShowCalls);
        Assert.True(rig.Presents > 0);
        Assert.False(rig.Revealed!.Value.TimedOut);
        Assert.Equal("material", rig.Log[0]);
    }

    [Fact]
    public void The_Watchdog_Reveals_When_Fonts_Never_Become_Ready()
    {
        using var rig = new Rig();
        rig.Host.BeginRevealGate(new RevealGateOptions { FirstFrameReady = static () => false, TimeoutMs = 40 });

        rig.RunUntilShown(max: 400, sleepMs: 5);

        Assert.Equal(1, rig.Window.ShowCalls);
        Assert.NotNull(rig.Revealed);
        Assert.True(rig.Revealed!.Value.TimedOut);
        Assert.Equal(-1d, rig.Revealed.Value.ReadyMs);
        Assert.Equal("material", rig.Log[0]);            // even a forced reveal applies the material first
        Assert.False(rig.Host.IsRevealPending);
        // Shown exactly once: further frames never show again.
        for (int i = 0; i < 5; i++) rig.Host.RunFrame();
        Assert.Equal(1, rig.Window.ShowCalls);
    }

    [Fact]
    public void A_Stood_Down_Present_Is_Not_A_Reveal()
    {
        using var rig = new Rig();
        rig.Host.BeginRevealGate(new RevealGateOptions { TimeoutMs = 60_000 });
        rig.Device.PrimarySwapchain!.PresentStandDown = true;   // the backend paints nothing for a covered / hidden target

        for (int i = 0; i < 8; i++) rig.Host.RunFrame();
        Assert.Equal(0, rig.Window.ShowCalls);
        Assert.False(rig.Device.PrimarySwapchain.HasPresentedContent);

        rig.Device.PrimarySwapchain.PresentStandDown = false;
        rig.Host.RequestFullRepaintOnce();
        rig.RunUntilShown(max: 20);
        Assert.Equal(1, rig.Window.ShowCalls);
        Assert.False(rig.Revealed!.Value.TimedOut);
    }

    [Fact]
    public void The_Gate_Re_Arms_For_A_Show_From_Hidden_And_Waits_For_A_New_Present()
    {
        using var rig = new Rig();
        rig.Host.BeginRevealGate();
        rig.RunUntilShown(max: 20);
        Assert.Equal(1, rig.Window.ShowCalls);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();

        rig.Window.Hide();                               // minimize to tray
        for (int i = 0; i < 4; i++) rig.Host.RunFrame();
        int presentsWhileHidden = rig.Presents;
        rig.Log.Clear();
        rig.Revealed = null;

        rig.Host.BeginRevealGate();                      // SetWindowVisible(true) under RevealOnFirstPresent
        Assert.False(rig.Device.PrimarySwapchain!.HasPresentedContent);   // the reveal exemption is re-armed
        Assert.False(rig.Window.IsVisible);              // still hidden until the new frame has presented
        rig.RunUntilShown(showCalls: 2, max: 20);

        Assert.Equal(2, rig.Window.ShowCalls);
        Assert.Equal(2, rig.Log.Count);
        Assert.Equal("material", rig.Log[0]);
        Assert.True(rig.Presents > presentsWhileHidden, "the restore must show a frame painted after the re-arm");
        Assert.False(rig.Revealed!.Value.TimedOut);
        Assert.True(rig.Window.IsVisible);
    }

    [Fact]
    public void A_Detached_Child_Cannot_Use_The_Main_Window_Gate()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var device = new HeadlessGpuDevice();
        var pw = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f)); pw.Show();
        using var parent = new AppHost(app, pw, device, new HeadlessFontSystem(strings), strings, new Root());
        var cw = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true));
        cw.Show();
        var child = new AppHost(app, cw, device, new HeadlessFontSystem(strings), strings, new Root(),
            images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
        parent.AdoptDetachedChild(child);
        Assert.Throws<InvalidOperationException>(() => child.BeginRevealGate());
    }
}
