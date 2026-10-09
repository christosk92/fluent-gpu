using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A non-cached KeepAlive page (ShouldCache false: every PageHost route without RouteDef.KeepAlive) must survive a
/// boundary re-run that did not move the token. RethemeAll schedules every boundary on a theme/accent change, and the
/// re-run minted a fresh "__transient:N" key each time, so the live page was unmounted and a new copy mounted: scroll,
/// component state and focus lost, Enter replayed. Navigating away and back must still remount it fresh.
/// </summary>
[Collection(SerialTestCollection.Name)]   // builds AppHosts; HostDispatch.Current is process-static (see SerialTestCollection)
public sealed class KeepAliveTransientRerunTests
{
    private sealed class Root : Component
    {
        public readonly Signal<string> Page = new("a");
        public int Mounts;

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children =
            [
                Flow.KeepAlive(() => Page.Value, static k => k, k => Embed.Comp(() => new PageComp(this)),
                    new KeepAliveOptions(ShouldCache: static _ => false)),
            ],
        };
    }

    private sealed class PageComp : Component
    {
        public PageComp(Root root) => root.Mounts++;
        public override Element Render() => new BoxEl { Width = 320f, Height = 240f };
    }

    [Fact]
    public void ARetheme_KeepsTheActiveTransientPage_ButReturningStillRemountsIt()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("keepalive-transient-rerun", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            Assert.Equal(1, root.Mounts);

            host.RequestThemeTransition(0f);   // RethemeAll: re-runs the boundary with the SAME token
            for (int i = 0; i < 3; i++) host.RunFrame();
            Assert.Equal(1, root.Mounts);      // updated in place, not torn down and remounted

            root.Page.Value = "b";
            for (int i = 0; i < 3; i++) host.RunFrame();
            Assert.Equal(2, root.Mounts);
            root.Page.Value = "a";             // a transient page is not cached: returning mounts it fresh
            for (int i = 0; i < 3; i++) host.RunFrame();
            Assert.Equal(3, root.Mounts);
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
