using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// EP20 / E20 (Classic tuning F10): the live light/dark switch is ONE 250 ms cross-fade on one timeline. Mid-fade (about
/// 125 ms in) a re-rendered fill, a bound colour channel, a token brush (the elevation gradient border every button carries), a
/// title-bar caption button and a KeepAlive page that comes back from its park mid-fade all show the lerp of their old and new
/// colour at the SAME progress; nothing snaps, and at the end every one of them is at its new colour with no fade left. The
/// control: a bound colour changed outside the theme window still snaps, as before. Serial: it builds hosts and flips the
/// process-wide token set.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class LiveFadeCompletenessTests
{
    // Unique widths identify the nodes under test.
    private const float StaticW = 101f, BoundW = 102f, BrushW = 103f, PageFillW = 104f, PageTextW = 105f, BoundTextW = 106f;

    private sealed class PageA : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 200f, Height = 60f,
            Children =
            [
                new BoxEl { Width = PageFillW, Height = 10f, Fill = Tok.FillCardDefault },                      // re-rendered on un-park
                new TextEl("page") { Width = PageTextW, Color = Prop.Of(() => Tok.TextSecondary) },             // bound, fired while parked
            ],
        };
    }

    private sealed class Root : Component
    {
        public readonly Signal<string> Page = new("a");

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 640f, Height = 400f,
            Children =
            [
                TitleBar.Create(new TitleBarOptions { Title = "fade", ShowCaptionButtons = true }),
                new BoxEl { Width = StaticW, Height = 10f, Fill = Tok.FillSubtleSecondary },                     // static, re-rendered
                new BoxEl { Width = BoundW, Height = 10f, Fill = Prop.Of(() => Tok.TextPrimary) },               // bound channel
                new TextEl("bound") { Width = BoundTextW, Color = Prop.Of(() => Tok.TextPrimary) },              // bound text colour
                new BoxEl { Width = BrushW, Height = 32f, BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder },   // token brush
                Flow.KeepAlive(() => Page.Value, static k => k,
                    k => k == "a" ? Embed.Comp(() => new PageA()) : new BoxEl { Width = 200f, Height = 60f }),
            ],
        };
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly AppHost Host;
        private readonly ThemeKind _themeAtStart = Tok.Theme;

        public Rig(Component root)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var window = new HeadlessWindow(new WindowDesc("live-fade", new Size2(640, 400), 1f));
            window.Show();
            Host = new AppHost(App, window, new HeadlessGpuDevice(), new HeadlessFontSystem(Strings), Strings, root);
            Paint(3);
        }

        public SceneStore Scene => Host.Scene;

        public void Paint(int frames = 1)
        {
            for (int i = 0; i < frames; i++) Host.Paint(0);
        }

        public NodeHandle Find(float width) => Find(Scene.Root, n => Scene.Layout(n).Width == width);

        public NodeHandle FindText(string text)
        {
            var id = Strings.Intern(text);
            return Find(Scene.Root, n => Scene.Paint(n).VisualKind == VisualKind.Text && Scene.Paint(n).Text == id);
        }

        private NodeHandle Find(NodeHandle n, Func<NodeHandle, bool> match)
        {
            if (n.IsNull) return n;
            if (match(n)) return n;
            for (var c = Scene.FirstChild(n); !c.IsNull; c = Scene.NextSibling(c))
            {
                var r = Find(c, match);
                if (!r.IsNull) return r;
            }
            return default;
        }

        public void Dispose()
        {
            Tok.Use(_themeAtStart);
            Host.Dispose();
            App.Dispose();
        }
    }

    private static ColorF Displayed(SceneStore scene, NodeHandle n, byte bit)
    {
        ref NodePaint p = ref scene.Paint(n);
        ColorF target = bit == BrushAnim.TextBit ? p.TextColor : p.Fill;
        return scene.TryGetBrushAnim(n, out var ba) && (ba.Channels & bit) != 0
            ? ColorF.LerpLinear(bit == BrushAnim.TextBit ? ba.TextFrom : ba.FillFrom, target, ba.T)
            : target;
    }

    private static float FadeT(SceneStore scene, NodeHandle n) => scene.TryGetBrushAnim(n, out var ba) ? ba.T : 1f;

    private static void AssertColor(ColorF expected, ColorF actual, string what)
    {
        const float eps = 2e-3f;
        Assert.True(MathF.Abs(expected.R - actual.R) < eps && MathF.Abs(expected.G - actual.G) < eps
                    && MathF.Abs(expected.B - actual.B) < eps && MathF.Abs(expected.A - actual.A) < eps,
            $"{what}: expected {expected}, displayed {actual}");
    }

    [Fact]
    public void AKindFlip_FadesEveryColourConsumerOnOneTimeline_IncludingAPageThatUnparksMidFade()
    {
        var root = new Root();
        using var rig = new Rig(root);
        var scene = rig.Scene;

        NodeHandle staticFill = rig.Find(StaticW), boundFill = rig.Find(BoundW), boundText = rig.Find(BoundTextW);
        NodeHandle brush = rig.Find(BrushW), caption = rig.FindText(Icons.ChromeClose);
        NodeHandle pageFill = rig.Find(PageFillW), pageText = rig.Find(PageTextW);
        foreach (var n in new[] { staticFill, boundFill, boundText, brush, caption, pageFill, pageText }) Assert.False(n.IsNull);

        ColorF staticOld = scene.Paint(staticFill).Fill, boundOld = scene.Paint(boundFill).Fill, boundTextOld = scene.Paint(boundText).TextColor;
        ColorF captionOld = scene.Paint(caption).TextColor, pageFillOld = scene.Paint(pageFill).Fill, pageTextOld = scene.Paint(pageText).TextColor;
        Assert.True(scene.TryGetBorderBrush(brush, out var brushOld));
        ColorF brushStop0Old = brushOld.Stops[0].Color;

        // Park page A (it is cached), then flip the kind with the page away.
        root.Page.Value = "b";
        rig.Paint(2);
        Assert.True((scene.Flags(pageFill) & NodeFlags.Parked) != 0);
        Tok.Use(Tok.Theme == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);
        rig.Paint();   // the theme flush: every live consumer starts its fade

        Assert.NotEqual(staticOld, scene.Paint(staticFill).Fill);   // the tokens really moved
        Assert.NotEqual(boundOld, scene.Paint(boundFill).Fill);
        Assert.NotEqual(captionOld, scene.Paint(caption).TextColor);

        // Run to about 125 ms (the 16 ms headless step), then bring page A back for the next frame.
        while (FadeT(scene, staticFill) < 0.40f) rig.Paint();
        root.Page.Value = "a";
        rig.Paint();

        float t = FadeT(scene, staticFill);
        Assert.InRange(t, 0.40f, 0.65f);
        Assert.True((scene.Flags(pageFill) & NodeFlags.Parked) == 0);

        // Every consumer is at the same progress …
        foreach (var n in new[] { boundFill, boundText, brush, caption, pageFill, pageText })
            Assert.Equal(t, FadeT(scene, n), 3);
        // … showing the lerp of its own two colours.
        AssertColor(ColorF.LerpLinear(staticOld, scene.Paint(staticFill).Fill, t), Displayed(scene, staticFill, BrushAnim.FillBit), "re-rendered fill");
        AssertColor(ColorF.LerpLinear(boundOld, scene.Paint(boundFill).Fill, t), Displayed(scene, boundFill, BrushAnim.FillBit), "bound fill");
        AssertColor(ColorF.LerpLinear(boundTextOld, scene.Paint(boundText).TextColor, t), Displayed(scene, boundText, BrushAnim.TextBit), "bound text");
        AssertColor(ColorF.LerpLinear(captionOld, scene.Paint(caption).TextColor, t), Displayed(scene, caption, BrushAnim.TextBit), "caption button glyph");
        AssertColor(ColorF.LerpLinear(pageFillOld, scene.Paint(pageFill).Fill, t), Displayed(scene, pageFill, BrushAnim.FillBit), "un-parked page fill");
        AssertColor(ColorF.LerpLinear(pageTextOld, scene.Paint(pageText).TextColor, t), Displayed(scene, pageText, BrushAnim.TextBit), "un-parked page bound text");
        // The token brush fades from the gradient it showed (the recorder blends its stops back toward it by 1 - T).
        Assert.True(scene.TryGetBrushAnim(brush, out var brushFade) && (brushFade.Channels & BrushAnim.BorderBrushBit) != 0);
        Assert.True(scene.TryGetBorderBrushFadeFrom(brush, out var brushFrom));
        Assert.Equal(brushStop0Old, brushFrom.Stops[0].Color);
        Assert.True(scene.TryGetBorderBrush(brush, out var brushNew));
        Assert.NotEqual(brushStop0Old, brushNew.Stops[0].Color);

        // The end: every consumer lands on its new colour, no fade left behind.
        rig.Paint(20);
        foreach (var n in new[] { staticFill, boundFill, boundText, brush, caption, pageFill, pageText })
            Assert.False(scene.TryGetBrushAnim(n, out _));
        Assert.False(scene.TryGetBorderBrushFadeFrom(brush, out _));
    }

    [Fact]
    public void APageParkedThroughTwoFlips_ComesBackOnItsFinalColours_WithNoStaleFade()
    {
        var root = new Root();
        using var rig = new Rig(root);
        var scene = rig.Scene;
        NodeHandle pageFill = rig.Find(PageFillW), pageText = rig.Find(PageTextW);
        root.Page.Value = "b";
        rig.Paint(2);
        ThemeKind start = Tok.Theme, other = start == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark;
        Tok.Use(other);
        rig.Paint(3);                                   // a bound fade seeded while parked, then a second flip mid-fade
        Tok.Use(start);
        rig.Paint(30);                                  // both timelines are over
        root.Page.Value = "a";
        rig.Paint();
        Assert.False(scene.TryGetBrushAnim(pageFill, out _));
        Assert.False(scene.TryGetBrushAnim(pageText, out _));
        Assert.Equal(Tok.TextSecondary, scene.Paint(pageText).TextColor);
        Assert.Equal(Tok.FillCardDefault, scene.Paint(pageFill).Fill);
    }

    private sealed class SignalRoot : Component
    {
        public readonly Signal<ColorF> Ink = new(ColorF.FromRgba(200, 30, 30));
        public override Element Render() => new BoxEl { Width = BoundW, Height = 10f, Fill = Ink };
    }

    [Fact]
    public void OutsideTheThemeWindow_ABoundColourStillSnaps()
    {
        var root = new SignalRoot();
        using var rig = new Rig(root);
        NodeHandle n = rig.Find(BoundW);
        var next = ColorF.FromRgba(30, 30, 200);
        root.Ink.Value = next;
        rig.Paint();
        Assert.Equal(next, rig.Scene.Paint(n).Fill);
        Assert.False(rig.Scene.TryGetBrushAnim(n, out _));
    }

    [Fact]
    public void ABoundColour_InsideTheThemeWindow_ButUnderASnapScope_LandsAtItsEnd()
    {
        var root = new Root();
        using var rig = new Rig(root);
        NodeHandle n = rig.Find(BoundW);
        using (SnapScope.Enter(rig.Host))
        {
            Tok.Use(Tok.Theme == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);
            rig.Paint();
        }
        Assert.False(rig.Scene.TryGetBrushAnim(n, out _));
    }
}
