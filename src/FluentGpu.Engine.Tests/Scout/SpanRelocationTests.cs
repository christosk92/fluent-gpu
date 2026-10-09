using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// An exact span copy stores a row for the copied node only: its descendants' rows keep naming the arena buffer they were
/// RECORDED into, which the next swap retires. When that node later re-records (a hover inside it), its clean children
/// must still copy their bytes out of the prior buffer the copy carried them into — not miss on the retired buffer and
/// re-record the whole subtree.
/// </summary>
public sealed class SpanRelocationTests
{
    // root ─ p ─┬─ a
    //           └─ b ─┬─ c ─┬─ d
    //                 │     └─ e
    //                 └─ g
    private readonly record struct Tree(SceneStore Scene, NodeHandle A, NodeHandle D, NodeHandle G);

    private static Tree Build()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        var p = scene.CreateNode(1);
        var a = scene.CreateNode(1);
        var b = scene.CreateNode(1);
        var c = scene.CreateNode(1);
        var d = scene.CreateNode(1);
        var e = scene.CreateNode(1);
        var g = scene.CreateNode(1);
        scene.Root = root;
        scene.AppendChild(root, p);
        scene.AppendChild(p, a);
        scene.AppendChild(p, b);
        scene.AppendChild(b, c);
        scene.AppendChild(b, g);
        scene.AppendChild(c, d);
        scene.AppendChild(c, e);
        scene.Bounds(root) = new RectF(0, 0, 400, 200);
        scene.Bounds(p) = new RectF(0, 0, 400, 200);
        Box(scene, a, new RectF(0, 0, 50, 30), 0x20);
        Box(scene, b, new RectF(60, 0, 300, 150), 0x30);
        Box(scene, c, new RectF(0, 0, 100, 100), 0x40);
        Box(scene, d, new RectF(0, 0, 40, 40), 0x50);
        Box(scene, e, new RectF(50, 0, 40, 40), 0x60);
        Box(scene, g, new RectF(150, 0, 40, 40), 0x70);
        return new Tree(scene, a, d, g);
    }

    private static void Box(SceneStore scene, NodeHandle n, RectF bounds, byte shade)
    {
        scene.Bounds(n) = bounds;
        ref var paint = ref scene.Paint(n);
        paint = NodePaint.Default;
        paint.VisualKind = VisualKind.Box;
        paint.Fill = ColorF.FromRgba(shade, shade, shade);
    }

    private static void Repaint(SceneStore scene, NodeHandle n, byte red)
    {
        scene.Paint(n).Fill = ColorF.FromRgba(red, 2, 3);
        scene.Mark(n, NodeFlags.PaintDirty);
    }

    private static SceneRecordStats Record(SceneStore scene, DrawList dl, SpanTable spans)
    {
        var stats = SceneRecorder.Record(scene, dl, spans: spans, collectSpanReuseMisses: true);
        scene.ClearRecordDirty();
        return stats;
    }

    /// <summary>The same tree with the same paint, recorded from scratch: what a copy must reproduce byte for byte.</summary>
    private static (byte[] Bytes, ulong[] Sort) Fresh(byte aRed, byte gRed, byte? dRed = null)
    {
        var t = Build();
        Repaint(t.Scene, t.A, aRed);
        Repaint(t.Scene, t.G, gRed);
        if (dRed is { } r) Repaint(t.Scene, t.D, r);
        var dl = new DrawList();
        SceneRecorder.Record(t.Scene, dl, spans: new SpanTable());
        return (dl.Bytes.ToArray(), dl.SortKeys.ToArray());
    }

    [Fact]
    public void CleanChildCopiesWhenItsAncestorReRecordsAfterAnExactCopy()
    {
        var t = Build();
        var dl = new DrawList();
        var spans = new SpanTable();
        Record(t.Scene, dl, spans);
        // a changes twice: p re-records and b (with c, d, e, g) is exact-copied out of the prior buffer each time.
        Repaint(t.Scene, t.A, 1);
        Assert.Equal(1, Record(t.Scene, dl, spans).SpansReused);
        Repaint(t.Scene, t.A, 2);
        Assert.Equal(1, Record(t.Scene, dl, spans).SpansReused);

        // g changes: b re-records; a and c (with d, e) are clean and copy, only root, p, b and g record.
        Repaint(t.Scene, t.G, 4);
        var stats = Record(t.Scene, dl, spans);
        Assert.Equal(0, stats.SpanReuseMisses.ExactKey);
        Assert.Equal(2, stats.SpansReused);
        Assert.Equal(4, stats.SpansReRecorded);
        var fresh = Fresh(2, 4);
        Assert.True(dl.Bytes.SequenceEqual(fresh.Bytes));
        Assert.True(dl.SortKeys.SequenceEqual(fresh.Sort));
    }

    [Fact]
    public void CleanGrandchildCopiesThroughTwoReRecordingLevels()
    {
        var t = Build();
        var dl = new DrawList();
        var spans = new SpanTable();
        Record(t.Scene, dl, spans);
        Repaint(t.Scene, t.A, 1);
        Record(t.Scene, dl, spans);
        Repaint(t.Scene, t.G, 4);
        Record(t.Scene, dl, spans);
        Repaint(t.Scene, t.A, 2);
        Record(t.Scene, dl, spans);   // b copied again, with c's row (and d's, e's) still on an older buffer

        // d changes: b and c re-record; a, g and e copy.
        Repaint(t.Scene, t.D, 5);
        var stats = Record(t.Scene, dl, spans);
        Assert.Equal(0, stats.SpanReuseMisses.ExactKey);
        Assert.Equal(3, stats.SpansReused);
        Assert.Equal(5, stats.SpansReRecorded);
        var fresh = Fresh(2, 4, 5);
        Assert.True(dl.Bytes.SequenceEqual(fresh.Bytes));
        Assert.True(dl.SortKeys.SequenceEqual(fresh.Sort));
    }
}
