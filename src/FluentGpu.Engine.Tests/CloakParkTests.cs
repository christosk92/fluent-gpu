using System;
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
/// A detached pop-out whose window DWM has cloaked (another virtual desktop, a shell transition) keeps <c>WS_VISIBLE</c> but
/// presents nothing, so the host parks it exactly like a hidden window - no Paint, no RenderMotion - once the cloak has held
/// for <see cref="CloakParkGate.EnterDelayMs"/>, and un-parks the instant it clears. The gate is pure; the host wiring is
/// driven through a real headless child + the headless window's cloak seam. Serial: it constructs several hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class CloakParkTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    [Fact]
    public void Gate_ParksOnlyAfterTheDebounce_AndUnparksImmediately()
    {
        var gate = new CloakParkGate();
        Assert.False(gate.Advance(false, 0));
        Assert.False(gate.Advance(true, 1000));                                   // cloak starts at 1000
        Assert.False(gate.Advance(true, 1000 + CloakParkGate.EnterDelayMs - 1));  // not yet held long enough
        Assert.True(gate.Advance(true, 1000 + CloakParkGate.EnterDelayMs));
        Assert.True(gate.Parked);
        Assert.True(gate.Advance(true, 5000));                                    // stays parked while cloaked
        Assert.False(gate.Advance(false, 5001));                                  // un-cloak: live at once
        Assert.False(gate.Parked);
    }

    [Fact]
    public void Gate_ShellTransitionFlaps_NeverPark()
    {
        var gate = new CloakParkGate();
        double t = 0;
        // Cloaked for 100 ms, clear for a frame, repeatedly: each cloak run restarts the debounce.
        for (int i = 0; i < 50; i++)
        {
            Assert.False(gate.Advance(true, t));
            t += 100;
            Assert.False(gate.Advance(true, t));
            t += 16;
            Assert.False(gate.Advance(false, t));
            t += 16;
        }
    }

    [Fact]
    public void Gate_MsUntilPark_IsInfiniteUnlessADebounceIsPending()
    {
        var gate = new CloakParkGate();
        Assert.Equal(double.PositiveInfinity, gate.MsUntilPark(0));                  // never cloaked
        gate.Advance(true, 1000);
        Assert.Equal(CloakParkGate.EnterDelayMs, gate.MsUntilPark(1000));            // the full debounce remains
        Assert.Equal(CloakParkGate.EnterDelayMs - 100, gate.MsUntilPark(1100));
        Assert.Equal(0.0, gate.MsUntilPark(1000 + CloakParkGate.EnterDelayMs + 50)); // due, not yet sampled: clamped at 0
        gate.Advance(true, 1000 + CloakParkGate.EnterDelayMs);
        Assert.Equal(double.PositiveInfinity, gate.MsUntilPark(5000));               // parked: nothing left to debounce
        gate.Advance(false, 5001);
        Assert.Equal(double.PositiveInfinity, gate.MsUntilPark(5001));               // uncloaked again
    }

    private static (AppHost Parent, AppHost Child, HeadlessWindow ParentWindow, HeadlessWindow ChildWindow, IDisposable Cleanup) Build()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var device = new HeadlessGpuDevice();
        var pw = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        pw.Show();
        var parent = new AppHost(app, pw, device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
        parent.RunFrame();
        var cw = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
        cw.Show();
        var child = new AppHost(app, cw, device, new HeadlessFontSystem(strings), strings, new EmptyRoot(),
            images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
        parent.AdoptDetachedChild(child);
        return (parent, child, pw, cw, new Teardown(parent, app));
    }

    private sealed class Teardown(AppHost parent, HeadlessPlatformApp app) : IDisposable
    {
        public void Dispose() { parent.Dispose(); app.Dispose(); }
    }

    // The headless timer clock only advances on a painted frame, so force one per step (16 ms each).
    private static void PaintFrames(AppHost host, int count)
    {
        for (int i = 0; i < count; i++) { host.RequestFullRepaintOnce(); host.RunFrame(); }
    }

    [Fact]
    public void ACloakedChild_ParksAfterTheDebounce_PollsForTheUncloak_AndUnparksWhenItClears()
    {
        var (parent, child, _, cw, td) = Build();
        using (td)
        {
            PaintFrames(child, 3);
            Assert.False(child.IsParked);

            cw.IsCloaked = true;
            PaintFrames(child, 3);
            Assert.False(child.IsParked);   // inside the debounce (3 x 16 ms)

            PaintFrames(child, (int)(CloakParkGate.EnterDelayMs / 16) + 4);
            Assert.True(child.IsParked);
            Assert.True(cw.IsVisible);      // the window itself is still "shown": the park is the cloak's alone

            // No message announces an un-cloak, so a cloak-parked child polls instead of blocking forever.
            int wait = child.RecommendedWaitMs();
            Assert.InRange(wait, 1, 250);

            cw.IsCloaked = false;
            child.RunFrame();
            Assert.False(child.IsParked);
            parent.TickDetachedHosts();     // and the parent loop keeps ticking it normally
            Assert.False(child.IsParked);
        }
    }

    [Fact]
    public void AnIdleCloakedChild_WakesWhenTheDebounceEnds()
    {
        var (_, child, _, cw, td) = Build();
        using (td)
        {
            PaintFrames(child, 3);
            Assert.Equal(-1, child.RecommendedWaitMs());   // uncloaked and idle: block until a message

            cw.IsCloaked = true;
            child.RunFrame();                              // first cloaked sample; no forced repaint, the UI has no work
            Assert.False(child.IsParked);
            // Render-thread-only motion never wakes the UI loop, so the idle wait itself must end with the debounce.
            Assert.InRange(child.RecommendedWaitMs(), 1, (int)CloakParkGate.EnterDelayMs);
        }
    }

    [Fact]
    public void AHiddenAndCloakedChild_StillBlocksForTheShowMessage()
    {
        var (_, child, _, cw, td) = Build();
        using (td)
        {
            PaintFrames(child, 3);
            cw.IsCloaked = true;
            PaintFrames(child, (int)(CloakParkGate.EnterDelayMs / 16) + 4);
            cw.Hide();
            child.RunFrame();
            Assert.True(child.IsParked);
            Assert.Equal(-1, child.RecommendedWaitMs());   // a hidden window's Show IS a message: no poll
        }
    }
}
