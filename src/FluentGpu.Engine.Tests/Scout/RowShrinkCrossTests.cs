using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A row that overflows its definite width hands the deficit to its Shrink children in Arrange, which re-measures each
/// at its narrower width — and wrapped text gains lines there. Before the fix FlexLayout.Measure took the row's cross
/// from the UNSHRUNK measure, so the row (and the parent that stacked against it) reserved one line while the text was
/// arranged two lines tall: under AlignItems=Start the second line painted over the next sibling, under Stretch it
/// was squeezed into the one-line box. The InfoBar horizontal panel ([message Shrink=1, wrap][action]) is this row.
/// Driven headlessly through the shipping Measure/Arrange path.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class RowShrinkCrossTests
{
    private static readonly TextStyle Wrapping = new(default, 10f, 400, TextWrap.WrapWholeWords);

    // column 300 wide: [ row: [text Shrink=1, wrap (132 natural)][fixed 200x10] ] then a 10-tall sibling below.
    // 332 > 300 ⇒ the text shrinks to 100 and wraps to two 14-DIP lines.
    private static (SceneStore Scene, HeadlessFontSystem Fonts, NodeHandle Row, NodeHandle Text, NodeHandle Below) Rig(FlexAlign align, float width = 300f)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var strings = new StringTable();
        var fonts = new HeadlessFontSystem(strings);
        var scene = new SceneStore();
        var layout = new FlexLayout(scene, fonts);
        var root = scene.CreateNode(1);
        scene.Layout(root).Direction = 1;
        scene.Layout(root).Width = width;
        scene.Layout(root).Height = 1000f;
        var row = scene.CreateNode(2);
        scene.AppendChild(root, row);
        scene.Layout(row).Direction = 0;
        scene.Layout(row).AlignItems = align;
        var text = scene.CreateNode(3);
        scene.AppendChild(row, text);
        scene.Paint(text).VisualKind = VisualKind.Text;
        scene.Paint(text).Text = strings.Intern("aaaa bbbb cccc dddd eeee");
        scene.Layout(text).TextStyle = Wrapping;
        scene.Layout(text).FlexShrink = 1f;
        var action = scene.CreateNode(4);
        scene.AppendChild(row, action);
        scene.Layout(action).Width = 200f;
        scene.Layout(action).Height = 10f;
        var below = scene.CreateNode(5);
        scene.AppendChild(root, below);
        scene.Layout(below).Height = 10f;
        layout.Run(root, new Size2(width, 1000f));
        return (scene, fonts, row, text, below);
    }

    [Fact]
    public void AStartAlignedRowReservesTheLinesItsShrunkTextWrapsTo()
    {
        var (scene, _, row, text, below) = Rig(FlexAlign.Start);

        Assert.Equal(100f, scene.Bounds(text).W);
        Assert.Equal(28f, scene.Bounds(text).H);    // two lines at the shrunk width
        Assert.Equal(28f, scene.Bounds(row).H);     // was 14: the one-line unshrunk measure
        Assert.Equal(28f, scene.Bounds(below).Y);   // was 14: the second line painted over this sibling
    }

    [Fact]
    public void AStretchRowDoesNotSqueezeItsShrunkTextIntoOneLine()
    {
        var (scene, fonts, row, text, _) = Rig(FlexAlign.Stretch);

        var tb = scene.Bounds(text);
        float content = fonts.Measure(scene.Paint(text).Text, Wrapping, tb.W).Size.Height;
        Assert.Equal(content, tb.H);                // was 14 for 28 of content: the last line overflowed the box
        Assert.Equal(28f, scene.Bounds(row).H);
    }

    [Fact]
    public void ARowThatFitsKeepsItsOneLineHeight()
    {
        var (scene, _, row, text, below) = Rig(FlexAlign.Start, width: 400f);

        Assert.Equal(132f, scene.Bounds(text).W, 3);
        Assert.Equal(14f, scene.Bounds(text).H);
        Assert.Equal(14f, scene.Bounds(row).H);
        Assert.Equal(14f, scene.Bounds(below).Y);
    }
}
