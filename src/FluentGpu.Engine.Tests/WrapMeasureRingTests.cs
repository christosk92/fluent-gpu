using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <c>FlexLayout.ArrangeWrap</c> used to break lines on each child's <c>scene.Bounds</c>. A wrap container whose measure
/// is served by the cross-pass ring (or the within-pass memo) never visits its children (Measure, P4), so those Bounds
/// still hold the children's LAST ARRANGED rects — <c>Grow</c> tiles stretched to fill the previous, differently wide
/// line. Arrange then broke lines on the stretched widths while the container's height came from the cached measure:
/// narrowing a clean wrap row (730 → 700) overflowed the line, the last tile dropped onto an extra line the cached height
/// never counted, and it painted over the next sibling (seen on Wavee's podcast page, where a 30 DIP strip toggles beside
/// a row of three <c>Grow</c> tiles). The reverse direction (a row that wrapped at the narrow width, widened again)
/// produced an extra line the same way. The fix: <c>ArrangeWrap</c> takes base sizes from <c>Measure(child, lineWidth)</c>,
/// the call <c>MeasureWrap</c> counts lines with, so measure and arrange agree whatever the caches hold.
/// <para>Driven headlessly against the real <see cref="FlexLayout"/> + <see cref="SceneStore"/> through the shipping
/// Measure/Arrange path (no source-text reads). Only the strip sibling is ever marked dirty — the wrap row and its
/// tiles stay clean, so the second visit of each width rides the ring.</para>
/// </summary>
public sealed class WrapMeasureRingTests
{
    private const float PageW = 730f;
    private const float Gap = 12f;
    private const float TileH = 60f;
    private const float StripW = 30f;
    private const float NextH = 20f;

    private sealed class Harness
    {
        public SceneStore Scene = null!;
        public FlexLayout Layout = null!;
        public NodeHandle Root, Strip, Tiles, Next;
        public NodeHandle[] Tile = null!;

        /// <summary>One full layout pass with the strip at <paramref name="stripW"/>. The strip is the ONLY node marked
        /// dirty (it changes the wrap row's offered width without touching the wrap row's subtree); the frame boundary is
        /// simulated afterwards. Returns the pass's count of REAL measure solves (ring/memo hits are not counted).</summary>
        public int Pass(float stripW)
        {
            Scene.Layout(Strip).Width = stripW;
            Scene.Mark(Strip, NodeFlags.LayoutDirty);
            Layout.ResetFrameDiagCounters();
            Layout.Run(Root, new Size2(1000f, 1000f));
            Scene.ClearLayoutDirty();
            return Layout.DiagMeasure;
        }
    }

    /// <summary>root(column, 730) ⊃ [ band(row, 730, Start) ⊃ [ strip(Width toggles), tiles(wrap row, Grow=1, gap 12) ⊃ 3 ×
    /// tile(Grow=1, Basis=220) ⊃ leaf(<paramref name="leafW"/> × 60) ], next(730 × 20) ].</summary>
    private static Harness Build(float leafW)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var layout = new FlexLayout(scene, new HeadlessFontSystem(new StringTable()));

        var root = scene.CreateNode(1);
        scene.Layout(root).Direction = 1;
        scene.Layout(root).Width = PageW;
        scene.Layout(root).AlignItems = FlexAlign.Start;

        var band = scene.CreateNode(2);
        scene.AppendChild(root, band);
        scene.Layout(band).Direction = 0;
        scene.Layout(band).Width = PageW;
        scene.Layout(band).AlignItems = FlexAlign.Start;   // the wrap row keeps its measured height (no cross stretch)

        var strip = scene.CreateNode(3);
        scene.AppendChild(band, strip);
        scene.Layout(strip).Width = 0f;
        scene.Layout(strip).Height = 10f;

        var tiles = scene.CreateNode(4);
        scene.AppendChild(band, tiles);
        scene.Layout(tiles).Direction = 0;
        scene.Layout(tiles).Wrap = true;
        scene.Layout(tiles).Gap = Gap;
        scene.Layout(tiles).FlexGrow = 1f;

        var tile = new NodeHandle[3];
        for (int i = 0; i < 3; i++)
        {
            tile[i] = scene.CreateNode(5);
            scene.AppendChild(tiles, tile[i]);
            scene.Layout(tile[i]).FlexGrow = 1f;
            scene.Layout(tile[i]).FlexBasis = 220f;
            var leaf = scene.CreateNode(6);
            scene.AppendChild(tile[i], leaf);
            scene.Layout(leaf).Width = leafW;
            scene.Layout(leaf).Height = TileH;
        }

        var next = scene.CreateNode(7);
        scene.AppendChild(root, next);
        scene.Layout(next).Width = PageW;
        scene.Layout(next).Height = NextH;

        return new Harness { Scene = scene, Layout = layout, Root = root, Strip = strip, Tiles = tiles, Next = next, Tile = tile };
    }

    // Wrap break rule shared by MeasureWrap and ArrangeWrap: an item joins the line while cursor + gap + width ≤ line + 0.01.
    private static int ExpectedLines(float leafW, float lineW) => 3f * leafW + 2f * Gap <= lineW + 0.01f ? 1 : 2;

    private static void AssertLayout(Harness h, float stripW, float leafW, int pass)
    {
        float lineW = PageW - stripW;
        int lines = ExpectedLines(leafW, lineW);
        string at = $"pass {pass}, strip {stripW}, line {lineW}";

        var r0 = h.Scene.AbsoluteRect(h.Tile[0]);
        var r1 = h.Scene.AbsoluteRect(h.Tile[1]);
        var r2 = h.Scene.AbsoluteRect(h.Tile[2]);
        var next = h.Scene.AbsoluteRect(h.Next);

        float bandH = lines == 1 ? TileH : 2f * TileH + Gap;   // what MeasureWrap (and so the cached cross size) counts
        Assert.Equal(bandH, next.Y, 2);                          // the next sibling sits right below the measured height
        Assert.Equal(stripW, r0.X, 2);

        // The point of the regression: no tile may overflow the band onto the next sibling, whatever the caches hold.
        foreach (var r in new[] { r0, r1, r2 })
        {
            Assert.True(r.Y + r.H <= next.Y + 0.01f, $"{at}: a tile (y={r.Y}, h={r.H}) paints over the next sibling (y={next.Y})");
            Assert.True(r.X + r.W <= PageW + 0.01f, $"{at}: a tile (x={r.X}, w={r.W}) overflows the line");
        }

        Assert.Equal(0f, r0.Y, 2);
        Assert.Equal(0f, r1.Y, 2);
        if (lines == 1)
        {
            // One line of three Grow tiles filling it edge to edge.
            Assert.Equal(0f, r2.Y, 2);
            float each = (lineW - 2f * Gap) / 3f;
            Assert.Equal(each, r0.W, 2);
            Assert.Equal(each, r1.W, 2);
            Assert.Equal(each, r2.W, 2);
            Assert.Equal(PageW, r2.X + r2.W, 2);
        }
        else
        {
            // [t0 t1] / [t2]: two tiles share the first line, the lone Grow tile fills the second.
            Assert.Equal(TileH + Gap, r2.Y, 2);
            Assert.Equal((lineW - Gap) / 2f, r0.W, 2);
            Assert.Equal((lineW - Gap) / 2f, r1.W, 2);
            Assert.Equal(lineW, r2.W, 2);
            Assert.Equal(next.Y, r2.Y + r2.H, 2);
        }
    }

    private static void RunToggleSequence(float leafW)
    {
        var h = Build(leafW);
        // 730 → 700 → 730 → 700 …: every revisit of a width is served by the ring, and the children's Bounds then hold
        // the OTHER width's stretched arrange.
        float[] strips = [0f, StripW, 0f, StripW, 0f, StripW];
        int firstSolves = 0;
        for (int pass = 0; pass < strips.Length; pass++)
        {
            int solves = h.Pass(strips[pass]);
            if (pass == 0) firstSolves = solves;
            else if (pass >= 2)
                Assert.True(solves < firstSolves,
                    $"pass {pass} re-solved {solves} nodes vs {firstSolves} on the first pass: the wrap row was expected to ride the measure ring");
            AssertLayout(h, strips[pass], leafW, pass);
        }
    }

    // THE REGRESSION (the podcast page). Three Grow tiles that fit one line at both widths: the stale stretched widths
    // from the 730 arrange (3 × 235.3 + gaps = 730) no longer fit 700, and the third tile used to drop to an extra line.
    [Fact]
    public void ANarrowedCleanWrapRowKeepsItsTilesOnTheLineTheCachedHeightCounted()
    {
        RunToggleSequence(120f);
    }

    // The same stale-Bounds hazard in the other direction. At 700 three 230-wide tiles wrap [t0 t1] / [t2] and the lone
    // tile stretches to the whole 700 line; widening back to 730 (where they fit one line, 714 ≤ 730) rides the ring's
    // one-line height, but the stale 344 / 344 / 700 widths made arrange break into two lines again.
    [Fact]
    public void AWidenedCleanWrapRowArrangesTheSameLineCountItsCachedHeightCounted()
    {
        RunToggleSequence(230f);
    }
}
