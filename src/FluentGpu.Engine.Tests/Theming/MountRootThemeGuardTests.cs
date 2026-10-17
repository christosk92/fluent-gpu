using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>
/// The host baselines the re-theme epoch after the root mount, so a component that re-themes inside its first render
/// would silently leave the first frame on whatever rendered before it. The guard (FGGUARD builds, Debug) refuses it.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class MountRootThemeGuardTests
{
    private sealed class ThemingRoot : Component
    {
        public override Element Render()
        {
            Tok.Use(Tok.Theme == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);
            return new BoxEl { Width = 10f, Height = 10f };
        }
    }

    private sealed class PlainRoot : Component
    {
        public override Element Render() => new BoxEl { Width = 10f, Height = 10f };
    }

    static AppHost Host(HeadlessPlatformApp app, Component root)
    {
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("mount-theme-guard", new Size2(10, 10), 1f));
        window.Show();
        return new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
    }

    [Fact]
    public void A_root_that_re_themes_during_its_mount_is_refused()
    {
#if FGGUARD
        var before = Tok.Theme;
        var app = new HeadlessPlatformApp();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Host(app, new ThemingRoot()));
            Assert.Contains("Tok.Epoch", ex.Message);
        }
        finally
        {
            Tok.Use(before);
            app.Dispose();
        }
#else
        Assert.Skip("the guard is FGGUARD-only (Debug builds)");
#endif
    }

    [Fact]
    public void Theming_before_the_host_is_constructed_is_fine()
    {
        var before = Tok.Theme;
        var app = new HeadlessPlatformApp();
        Tok.Use(before == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);
        try
        {
            var host = Host(app, new PlainRoot());
            host.RunFrame();
            host.Dispose();
        }
        finally
        {
            Tok.Use(before);
            app.Dispose();
        }
    }
}
