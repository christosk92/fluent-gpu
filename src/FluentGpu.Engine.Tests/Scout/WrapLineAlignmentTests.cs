using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <c>FlexLayout.ArrangeWrap</c> placed every item at its line's top at its measured cross size and packed every line
/// from the start: it never read <c>AlignItems</c>, <c>AlignSelf</c> or <c>Justify</c>, so turning <c>Wrap</c> on moved a
/// short text beside a taller chip to the top of the line (Wavee's comment heading, the Detail rail CTA row, Search's
/// bottom-aligned facet tabs), left Stretch items unstretched and ignored a centered tooltip row's Justify. The fix aligns
/// each item on its LINE's cross size and justifies each line's leftover, exactly like the single-line path.
/// </summary>
public sealed class WrapLineAlignmentTests
{
    private const float BoxW = 200f;

    /// <summary>root(column, 200, Start) ⊃ box(wrap row, 200, gap 0, <paramref name="align"/>, <paramref name="justify"/>).</summary>
    private static (SceneStore Scene, FlexLayout Layout, NodeHandle Root) Build(FlexAlign align, FlexJustify justify)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var layout = new FlexLayout(scene, new HeadlessFontSystem(new StringTable()));

        var root = scene.CreateNode(1);
        scene.Layout(root).Direction = 1;
        scene.Layout(root).Width = BoxW;
        scene.Layout(root).AlignItems = FlexAlign.Start;   // the wrap row keeps its measured height

        var box = scene.CreateNode(2);
        scene.AppendChild(root, box);
        scene.Layout(box).Direction = 0;
        scene.Layout(box).Wrap = true;
        scene.Layout(box).Width = BoxW;
        scene.Layout(box).AlignItems = align;
        scene.Layout(box).Justify = justify;
        return (scene, layout, root);
    }

    private static NodeHandle Item(SceneStore scene, NodeHandle parent, float w, float h, FlexAlign self = FlexAlign.Auto)
    {
        var n = scene.CreateNode(3);
        scene.AppendChild(parent, n);
        scene.Layout(n).Width = w;
        scene.Layout(n).Height = h;
        scene.Layout(n).AlignSelf = self;
        return n;
    }

    private static NodeHandle Box(SceneStore scene, NodeHandle root) => scene.FirstChild(root);

    private static void Run(FlexLayout layout, NodeHandle root) => layout.Run(root, new Size2(BoxW, 1000f));

    // Two lines with different heights: each item centres on ITS line (line 1 is 32 tall, line 2 is 24 tall at y 32).
    [Fact]
    public void CenterAlignsEachItemOnItsOwnLine()
    {
        var (scene, layout, root) = Build(FlexAlign.Center, FlexJustify.Start);
        var box = Box(scene, root);
        var a = Item(scene, box, 80f, 16f);
        var b = Item(scene, box, 80f, 32f);
        var c = Item(scene, box, 80f, 8f);   // 80+80+80 > 200 → line 2
        var d = Item(scene, box, 80f, 24f);
        Run(layout, root);

        Assert.Equal(8f, scene.AbsoluteRect(a).Y, 2);
        Assert.Equal(0f, scene.AbsoluteRect(b).Y, 2);
        Assert.Equal(32f + 8f, scene.AbsoluteRect(c).Y, 2);
        Assert.Equal(32f, scene.AbsoluteRect(d).Y, 2);
        Assert.Equal(56f, scene.AbsoluteRect(box).H, 2);   // measured height unchanged: 32 + 24
    }

    // AlignItems End bottom-aligns (Search's facet tabs over the underline); AlignSelf Start overrides it per item.
    [Fact]
    public void EndAlignsToTheLineBottomAndAlignSelfOverrides()
    {
        var (scene, layout, root) = Build(FlexAlign.End, FlexJustify.Start);
        var box = Box(scene, root);
        var a = Item(scene, box, 40f, 16f);
        var b = Item(scene, box, 40f, 32f);
        var c = Item(scene, box, 40f, 16f, FlexAlign.Start);
        Run(layout, root);

        Assert.Equal(16f, scene.AbsoluteRect(a).Y, 2);
        Assert.Equal(0f, scene.AbsoluteRect(b).Y, 2);
        Assert.Equal(0f, scene.AbsoluteRect(c).Y, 2);
    }

    // Default Stretch fills the line's cross size unless the item has an explicit height.
    [Fact]
    public void StretchFillsTheLineUnlessTheItemHasAnExplicitCrossSize()
    {
        var (scene, layout, root) = Build(FlexAlign.Stretch, FlexJustify.Start);
        var box = Box(scene, root);
        var auto = scene.CreateNode(3);            // auto height: measures 16 from its leaf
        scene.AppendChild(box, auto);
        scene.Layout(auto).Width = 40f;
        Item(scene, auto, 40f, 16f);
        Item(scene, box, 40f, 32f);
        var fixedH = Item(scene, box, 40f, 16f);
        Run(layout, root);

        var r = scene.AbsoluteRect(auto);
        Assert.Equal(0f, r.Y, 2);
        Assert.Equal(32f, r.H, 2);
        Assert.Equal(16f, scene.AbsoluteRect(fixedH).H, 2);
    }

    // Justify distributes EACH line's own leftover: [80 80] leaves 40, the lone [80] on line 2 leaves 120.
    [Fact]
    public void CenterJustifyDistributesEachLinesLeftover()
    {
        var (scene, layout, root) = Build(FlexAlign.Start, FlexJustify.Center);
        var box = Box(scene, root);
        var a = Item(scene, box, 80f, 16f);
        var b = Item(scene, box, 80f, 16f);
        var c = Item(scene, box, 80f, 16f);
        Run(layout, root);

        Assert.Equal(20f, scene.AbsoluteRect(a).X, 2);
        Assert.Equal(100f, scene.AbsoluteRect(b).X, 2);
        Assert.Equal(60f, scene.AbsoluteRect(c).X, 2);
        Assert.Equal(16f, scene.AbsoluteRect(c).Y, 2);
    }

    // SpaceBetween on a wrap row: the first line spreads its two items to the edges; a lone item on the last line leads with 0.
    [Fact]
    public void SpaceBetweenSpreadsEachLineAndLeadsALoneItemWithZero()
    {
        var (scene, layout, root) = Build(FlexAlign.Start, FlexJustify.SpaceBetween);
        var box = Box(scene, root);
        var a = Item(scene, box, 80f, 16f);
        var b = Item(scene, box, 80f, 16f);
        var c = Item(scene, box, 80f, 16f);
        Run(layout, root);

        Assert.Equal(0f, scene.AbsoluteRect(a).X, 2);
        Assert.Equal(120f, scene.AbsoluteRect(b).X, 2);
        Assert.Equal(0f, scene.AbsoluteRect(c).X, 2);
    }
}
