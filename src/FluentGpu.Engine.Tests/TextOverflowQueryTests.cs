using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The per-text-node trim query (TextOverflowQuery / AppHost.QueryTextOverflow): a single-line run with an ellipsis in a box
/// narrower than its natural width reads trimmed; the same run in a box that holds it does not; a wrapped run cut by MaxLines
/// reads trimmed vertically; a box node is not text. Headless font model (advance = 0.55 × size, line = 1.4 × size).
/// Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class TextOverflowQueryTests
{
    private const string Caption = "A fairly long caption";   // 21 chars × 7.7 = 161.7 DIP at 14

    private sealed class Root(Element content) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 600f, AlignItems = FlexAlign.Start,
            Children = [content, new BoxEl { Key = "box", Width = 20f, Height = 20f }],
        };
    }

    private static void With(Element content, Action<AppHost, NodeHandle> body)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("text-overflow", new Size2(800, 400), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(content));
        for (int i = 0; i < 4; i++) host.RunFrame();
        body(host, Find(host.Scene, "t"));
    }

    private static NodeHandle Find(SceneStore s, string key)
    {
        var keys = new List<(NodeHandle Node, string Key)>();
        s.CopyDebugKeys(keys);
        foreach (var (node, k) in keys)
            if (k == key) return node;
        Assert.Fail($"no node keyed '{key}'");
        return default;
    }

    [Fact]
    public void ASingleLineEllipsis_TooNarrow_IsTrimmed()
        => With(new TextEl(Caption) { Key = "t", Size = 14f, Width = 40f, Trim = TextTrim.CharacterEllipsis }, (host, t) =>
        {
            Assert.Equal(40f, host.Scene.Bounds(t).W);
            TextOverflow o = host.QueryTextOverflow(t);
            Assert.True(o.IsText);
            Assert.True(o.Trimmed);
            Assert.True(o.HorizontalOverflow);
            Assert.False(o.VerticalOverflow);
            Assert.Equal(161.7f, o.NaturalSize.Width, 0.1f);
            Assert.True(host.IsTextTrimmed(t));
            Assert.True(TextOverflowQuery.IsTrimmed(host.Scene, host.FontSystem, t));
        });

    [Fact]
    public void ASingleLineEllipsis_ThatFits_IsNotTrimmed()
        => With(new TextEl(Caption) { Key = "t", Size = 14f, Width = 200f, Trim = TextTrim.CharacterEllipsis }, (host, t) =>
        {
            TextOverflow o = host.QueryTextOverflow(t);
            Assert.True(o.IsText);
            Assert.False(o.Trimmed);
            Assert.False(host.IsTextTrimmed(t));
        });

    [Fact]
    public void AnAutoSizedRun_IsNotTrimmed()
        => With(new TextEl(Caption) { Key = "t", Size = 14f }, (host, t) => Assert.False(host.IsTextTrimmed(t)));

    [Fact]
    public void AWrappedRun_CutByMaxLines_IsTrimmedVertically()
        => With(new TextEl("aa bb cc dd ee ff gg hh") { Key = "t", Size = 14f, Width = 60f, Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, (host, t) =>
        {
            TextOverflow o = host.QueryTextOverflow(t);
            Assert.True(o.Trimmed);
            Assert.True(o.VerticalOverflow);
            Assert.True(o.NaturalSize.Height > host.Scene.Bounds(t).H + TextOverflowQuery.Tolerance);
        });

    [Fact]
    public void AWrappedRun_WithRoomForEveryLine_IsNotTrimmed()
        => With(new TextEl("aa bb cc") { Key = "t", Size = 14f, Width = 300f, Wrap = TextWrap.Wrap, MaxLines = 3 }, (host, t) =>
            Assert.False(host.IsTextTrimmed(t)));

    [Fact]
    public void ABox_IsNotText()
        => With(new TextEl(Caption) { Key = "t", Size = 14f }, (host, _) =>
        {
            TextOverflow o = host.QueryTextOverflow(Find(host.Scene, "box"));
            Assert.False(o.IsText);
            Assert.False(o.Trimmed);
        });
}
