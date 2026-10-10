using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The top bands (the lifted drag ghost, the connected-animation overlays, the drag chip) record into the root
/// slice's arena with no span table, so a moved band node has no prior extent to repaint. That used to force the whole
/// frame full, invalidating and re-rastering every retained tile of the window on every frame of a drag or a Hero fly.
/// The tiles it vacated already change their content want, so only those tiles re-raster.</summary>
public sealed class TopBandMoveRepaintTests
{
    private static readonly Size2 Window = new(2048f, 1024f);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMovedTopBandNodeReRastersOnlyTheTilesItLeftAndReached(bool overlay)
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1); scene.Root = root;
        scene.Bounds(root) = new RectF(0f, 0f, Window.Width, Window.Height);
        ref NodePaint rp = ref scene.Paint(root);
        rp.VisualKind = VisualKind.Box; rp.Fill = ColorF.FromRgba(10, 10, 12);
        var band = scene.CreateNode(1);
        if (!overlay) scene.AppendChild(root, band);   // a lifted row stays in its list; a fly overlay is standalone
        scene.Bounds(band) = new RectF(20f, 40f, 300f, 48f);
        ref NodePaint bp = ref scene.Paint(band);
        bp.VisualKind = VisualKind.Box; bp.Fill = ColorF.FromRgba(200, 100, 40);
        if (overlay) scene.AddOverlay(band);
        else scene.DragGhost = band;

        var host = new Composited();
        for (int i = 0; i < 3; i++) host.Turn(scene);
        Assert.Empty(host.Turn(scene).Rasters);   // settled: nothing re-rasters

        // a move inside the first tile (DragController.ApplyPresented / a fly tick)
        scene.Paint(band).LocalTransform = Affine2D.Translation(40f, 20f);
        scene.Mark(band, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        var near = host.Turn(scene);
        Assert.False(near.Full, "a top-band move forced the whole frame full");
        Assert.Equal([(0, 0)], near.Rasters);

        // a move into another tile: the tile it vacated re-rasters too
        scene.Paint(band).LocalTransform = Affine2D.Translation(1300f, 700f);
        scene.Mark(band, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        var far = host.Turn(scene);
        Assert.False(far.Full, "a top-band move forced the whole frame full");
        Assert.Equal([(0, 0), (1, 1)], far.Rasters);
    }

    /// <summary>Records and composites the way the host does: retained slice arenas, a tile table, the headless backend.</summary>
    private sealed class Composited
    {
        private readonly SliceRecorder _slices = new();
        private readonly SpanTable _spans = new();
        private readonly SliceTable _tiles = new(256, 64, 64);
        private readonly HeadlessGpuDevice _device = new();
        private readonly ISwapchain _target;

        public Composited() => _target = _device.CreateSwapchain(new SwapchainDesc(default, Window));

        public (bool Full, List<(int, int)> Rasters) Turn(SceneStore scene)
        {
            var stats = SceneRecorder.Record(scene, new DrawList(), spans: _spans, slices: _slices);
            scene.ClearRecordDirty();
            scene.ClearTransformDirty();
            var info = new FrameInfo(Window, 1f, default, RepaintDamage: stats.RepaintDamage);
            var frame = _slices.BuildComposite(_tiles, scene.Recording.InlineSnapshot!, in info, 0, stats.RepaintDamage, withStreams: true);
            var rasters = new List<(int, int)>();
            foreach (var r in frame.Rasters) rasters.Add((r.Key.Tx, r.Key.Ty));
            rasters.Sort();
            _device.SubmitComposite(in frame, _target);
            _slices.EndComposite(_tiles, frame.RasterDone);
            return (stats.RepaintDamage.IsFull, rasters);
        }
    }
}
