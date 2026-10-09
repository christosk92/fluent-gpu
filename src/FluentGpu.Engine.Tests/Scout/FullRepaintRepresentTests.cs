using System.Diagnostics;
using System.Threading;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A publication's forced full repaint (first frame, resize, theme, restore) is a one-shot: the render thread's motion
/// re-presents of that SAME publication (a shimmer / busy-bar loop while the UI is quiet) repaint only what moved, instead of
/// invalidating and re-rastering every retained tile of the window at the display rate. Serial: it constructs a host.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class FullRepaintRepresentTests
{
    private sealed class Root : Component
    {
        public override Element Render() => new BoxEl { Width = 200f, Height = 100f, Fill = ColorF.FromRgba(20, 60, 120) };
    }

    [Fact]
    public void AMotionRepresent_OfAFullRepaintPublication_DoesNotRepaintTheWholeWindowAgain()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var device = new HeadlessGpuDevice();
        var window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new Root());
        try
        {
            RenderThread thread = host.InstallRenderThreadForTest();
            host.RunFrame();
            // A render-owned loop and a one-shot full repaint, on ONE publication.
            host.Animation.Keyframes(host.Scene.Root, AnimChannel.Opacity, [new(0f, 1f), new(1f, 0.2f, Easing.Linear)], 1000f, loop: true);
            host.RequestFullRepaintOnce();
            ulong before = host.ScenePublishSeqForTest;
            host.RunFrame();
            ulong fullSeq = host.ScenePublishSeqForTest;
            Assert.True(fullSeq > before, "the full-repaint request publishes");

            // The UI goes quiet; the render loop re-presents that same publication on its own clock.
            int composites = device.CompositeFrameCount;
            var clock = Stopwatch.StartNew();
            while (device.CompositeFrameCount < composites + 3)
            {
                Assert.True(clock.ElapsedMilliseconds < 5000, "the render loop never re-presented the looping scene");
                Thread.Sleep(5);
            }
            thread.Quiesce();
            try
            {
                FrameInfo info = device.LastCompositeInfo;
                Assert.Equal(fullSeq, info.PublishSequence);   // a motion re-present, not a new publication
                Assert.False(info.RepaintDamage.IsFull,
                    $"a motion re-present re-forced the publication's full repaint ({info.RepaintDamage.FullReason})");
            }
            finally { thread.Resume(); }
        }
        finally
        {
            host.Dispose();
            app.Dispose();
        }
    }

    [Fact]
    public void TheForcedFull_IsKeptOnTheFirstSubmit_AndWhileACrossfadeIsLive()
    {
        RepaintDamageRegion full = default;
        full.ForceFull(RepaintFullReason.TargetInvalidated);
        Assert.True(AppHost.PublicationRepaintForTurn(in full, alreadySubmitted: false, crossfadesLive: false).IsFull);
        Assert.True(AppHost.PublicationRepaintForTurn(in full, alreadySubmitted: true, crossfadesLive: true).IsFull);
        Assert.True(AppHost.PublicationRepaintForTurn(in full, alreadySubmitted: true, crossfadesLive: false).IsEmpty);
    }

    [Fact]
    public void ACrossfadeRect_RidesEveryTurnOfItsPublication()
    {
        RepaintDamageRegion rects = default;
        rects.Add(new RectF(10, 10, 40, 40));
        var turn = AppHost.PublicationRepaintForTurn(in rects, alreadySubmitted: true, crossfadesLive: false);
        Assert.Equal(1, turn.Count);
        Assert.False(turn.IsFull);
    }
}
