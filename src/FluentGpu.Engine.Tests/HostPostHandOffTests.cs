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

/// <summary><see cref="AppHost.HandOffPosts"/>: a host about to be disposed hands its queued UI posts (and every later one) to
/// another poster instead of dropping them. A poster that latches "a wake is queued" (an app's command inbox) would otherwise
/// never wake again after a post was lost with the host.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class HostPostHandOffTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => new BoxEl();
    }

    private static (HeadlessPlatformApp App, AppHost Host) NewHost()
    {
        var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("post-hand-off", new Size2(320, 240), 1));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new EmptyRoot());
        for (int i = 0; i < 2; i++) host.RunFrame();
        return (app, host);
    }

    [Fact]
    public void Queued_posts_run_through_the_target_in_order_and_later_posts_follow_them()
    {
        var (app, host) = NewHost();
        var ran = new List<int>();
        var handedOff = new List<Action>();
        try
        {
            host.Post(() => ran.Add(1));
            host.Post(() => ran.Add(2));
            host.HandOffPosts(handedOff.Add);
            Assert.Equal(2, handedOff.Count);   // both queued posts were handed over, none ran on the host
            Assert.Empty(ran);

            host.Post(() => ran.Add(3));         // a post after the hand-off goes straight to the target
            Assert.Equal(3, handedOff.Count);
            host.RunFrame();
            Assert.Empty(ran);                   // the host's own frame no longer drains them

            foreach (var a in handedOff) a();
            Assert.Equal(new[] { 1, 2, 3 }, ran);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void A_post_handed_off_before_dispose_is_not_lost_with_the_host()
    {
        var (app, host) = NewHost();
        int ran = 0;
        host.Post(() => ran++);
        host.HandOffPosts(static a => a());       // inline: the shape a headless mount uses when it ends
        Assert.Equal(1, ran);
        host.Dispose();
        app.Dispose();
        host.Post(() => ran++);                   // a pool thread that still holds the old poster
        Assert.Equal(2, ran);
    }

    [Fact]
    public void A_post_racing_the_hand_off_from_another_thread_is_never_dropped()
    {
        var (app, host) = NewHost();
        int ran = 0;
        try
        {
            using var go = new ManualResetEventSlim();
            var poster = new Thread(() => { go.Wait(); for (int i = 0; i < 2000; i++) host.Post(() => Interlocked.Increment(ref ran)); });
            poster.Start();
            go.Set();
            host.HandOffPosts(static a => a());
            poster.Join();
            Assert.Equal(2000, Volatile.Read(ref ran));
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
