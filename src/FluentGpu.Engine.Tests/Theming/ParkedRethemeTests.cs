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

namespace FluentGpu.Engine.Tests.Theming;

/// <summary>
/// A page parked by a KeepAlive while the theme changes owes the re-theme render. When it returns, the whole debt renders
/// in the activation flush, so the first frame of the returning page is in the new theme everywhere: a page whose
/// components re-theme over several frames shows the old and the new theme side by side (E12, themes plan §4).
/// </summary>
[Collection(SerialTestCollection.Name)]   // builds AppHosts and writes Tok's process-wide theme
public sealed class ParkedRethemeTests
{
    const int Cards = 200;

    private sealed class Root : Component
    {
        public readonly Signal<string> Page = new("a");
        public readonly ThemeKind[] Seen = new ThemeKind[Cards];

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children = [Flow.KeepAlive(() => Page.Value, static k => k, k => k == "a" ? Embed.Comp(() => new CardsPage(this)) : new BoxEl())],
        };
    }

    private sealed class CardsPage(Root root) : Component
    {
        public override Element Render()
        {
            var kids = new Element[Cards];
            for (int i = 0; i < Cards; i++)
            {
                int index = i;
                kids[i] = Embed.Comp(() => new Card(root, index)) with { Key = "card" + index };
            }
            return new BoxEl { Direction = 1, Children = kids };
        }
    }

    private sealed class Card(Root root, int index) : Component
    {
        public override Element Render()
        {
            root.Seen[index] = Tok.Theme;
            return new BoxEl { Height = 1f, Fill = Tok.TextPrimary };
        }
    }

    [Fact]
    public void A_page_parked_across_a_theme_change_returns_entirely_in_the_new_theme_on_its_first_frame()
    {
        var before = Tok.Theme;
        var other = before == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark;
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("parked-retheme", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            Assert.All(root.Seen, seen => Assert.Equal(before, seen));

            root.Page.Value = "b";             // page a parks
            for (int i = 0; i < 3; i++) host.RunFrame();
            Tok.Use(other);
            host.RequestThemeTransition(0f);   // RethemeAll: page a's cards owe the render while parked
            for (int i = 0; i < 3; i++) host.RunFrame();
            Assert.All(root.Seen, seen => Assert.Equal(before, seen));   // parked: nothing rendered yet

            root.Page.Value = "a";
            host.RunFrame();
            Assert.All(root.Seen, seen => Assert.Equal(other, seen));
        }
        finally
        {
            Tok.Use(before);
            host.Dispose();
            app.Dispose();
        }
    }
}
