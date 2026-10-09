using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <c>FlexLayout.Measure</c> broke a wrap row's lines at the raw offered width (<c>TryWrapMainLimit</c> ignored
/// MinWidth/MaxWidth) and only clamped the WIDTH afterwards, so the measured height kept the unclamped line count. The
/// column parent then arranged the row at the clamped width and <c>ArrangeWrap</c> broke lines there: a MaxWidth=400 chip
/// row in a 1000 column measured one line (32) and painted a second line over the next sibling; a MinWidth row measured a
/// line too many and left an empty band. The fix clamps the wrap limit by the main-axis Min/Max, so measure and arrange
/// count the same lines.
/// </summary>
public sealed class WrapMinMaxWidthTests
{
    private const float ChipW = 120f;
    private const float ChipH = 32f;
    private const float Gap = 8f;
    private const int Chips = 6;

    /// <summary>root(column, <paramref name="pageW"/>, Stretch) ⊃ [ box(wrap row, gap 8, Min/Max) ⊃ 6 × chip(120 × 32),
    /// next(h 20) ].</summary>
    private static (SceneStore Scene, NodeHandle Box, NodeHandle[] Chip, NodeHandle Next) Build(float pageW, float minW, float maxW)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var layout = new FlexLayout(scene, new HeadlessFontSystem(new StringTable()));

        var root = scene.CreateNode(1);
        scene.Layout(root).Direction = 1;   // column; AlignItems defaults to Stretch
        scene.Layout(root).Width = pageW;

        var box = scene.CreateNode(2);
        scene.AppendChild(root, box);
        scene.Layout(box).Direction = 0;
        scene.Layout(box).Wrap = true;
        scene.Layout(box).Gap = Gap;
        scene.Layout(box).MinW = minW;
        scene.Layout(box).MaxW = maxW;

        var chip = new NodeHandle[Chips];
        for (int i = 0; i < Chips; i++)
        {
            chip[i] = scene.CreateNode(3);
            scene.AppendChild(box, chip[i]);
            scene.Layout(chip[i]).Width = ChipW;
            scene.Layout(chip[i]).Height = ChipH;
        }

        var next = scene.CreateNode(4);
        scene.AppendChild(root, next);
        scene.Layout(next).Height = 20f;

        layout.Run(root, new Size2(pageW, 1000f));
        return (scene, box, chip, next);
    }

    // At the clamped 400 three chips fit a line (3 × 120 + 2 × 8 = 376), so six chips are [c0 c1 c2] / [c3 c4 c5].
    private static void AssertTwoLinesAt400(SceneStore scene, NodeHandle box, NodeHandle[] chip, NodeHandle next)
    {
        const float TwoLines = 2f * ChipH + Gap;   // 72
        var b = scene.AbsoluteRect(box);
        var n = scene.AbsoluteRect(next);
        Assert.Equal(400f, b.W, 2);
        Assert.Equal(TwoLines, b.H, 2);
        Assert.Equal(TwoLines, n.Y, 2);
        Assert.Equal(ChipH + Gap, scene.AbsoluteRect(chip[3]).Y, 2);
        foreach (var c in chip)
        {
            var r = scene.AbsoluteRect(c);
            Assert.True(r.Y + r.H <= n.Y + 0.01f, $"a chip (y={r.Y}, h={r.H}) paints over the next sibling (y={n.Y})");
        }
    }

    // Offered 1000 (one line of six), arranged at MaxWidth 400 (two lines): measure used to report one line (32).
    [Fact]
    public void AMaxWidthWrapRowMeasuresTheLinesItArrangesAtTheClampedWidth()
    {
        var (scene, box, chip, next) = Build(pageW: 1000f, minW: float.NaN, maxW: 400f);
        AssertTwoLinesAt400(scene, box, chip, next);
    }

    // Offered 300 (three lines of two), arranged at MinWidth 400 (two lines): measure used to report three lines (112).
    [Fact]
    public void AMinWidthWrapRowMeasuresTheLinesItArrangesAtTheClampedWidth()
    {
        var (scene, box, chip, next) = Build(pageW: 300f, minW: 400f, maxW: float.NaN);
        AssertTwoLinesAt400(scene, box, chip, next);
    }
}
