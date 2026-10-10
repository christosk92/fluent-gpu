using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The record-time occlusion cull (SceneRecorder.IsOccludedByOpaqueChild) drops a node's own fill under a full-size
/// opaque child. That is sound only when the child REPLACES the pixels it covers over its whole bounds. An additive child
/// adds onto the parent's colour, a Screen boundary screens onto it, an acrylic surface frosts it (dropping its own
/// Fallback fill), a clip-rect scissors the child's own fill, and an additive parent adds its children onto itself: each
/// keeps the parent's fill. Serial: acrylic reads the static Materials policy.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class RecordOcclusionNonReplacingChildTests
{
    private static readonly ColorF ParentFill = new(0.80f, 0.12f, 0.16f, 1f);
    private static readonly ColorF ChildFill = new(0.12f, 0.62f, 0.24f, 1f);

    private static bool ParentFillRecorded(Action<SceneStore, NodeHandle, NodeHandle> knob)
    {
        var scene = new SceneStore();
        var parent = scene.CreateNode(1); scene.Root = parent;
        scene.Bounds(parent) = new RectF(0f, 0f, 100f, 40f);
        ref NodePaint pp = ref scene.Paint(parent);
        pp.VisualKind = VisualKind.Box; pp.Fill = ParentFill;
        var child = scene.CreateNode(1); scene.AppendChild(parent, child);
        scene.Bounds(child) = new RectF(0f, 0f, 100f, 40f);
        ref NodePaint cp = ref scene.Paint(child);
        cp.VisualKind = VisualKind.Box; cp.Fill = ChildFill;
        knob(scene, parent, child);

        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        ReadOnlySpan<byte> bytes = dl.Bytes;
        int pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            if (op == DrawOp.FillRoundRect && MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos)).Fill.Equals(ParentFill))
                return true;
            pos += PayloadSize(op);
        }
        return false;
    }

    private static int PayloadSize(DrawOp op) => op switch
    {
        DrawOp.FillRoundRect => Unsafe.SizeOf<FillRoundRectCmd>(),
        DrawOp.PushClip => Unsafe.SizeOf<ClipCmd>(),
        DrawOp.PopClip => 0,
        DrawOp.PushLayer => Unsafe.SizeOf<PushLayerCmd>(),
        DrawOp.PopLayer => Unsafe.SizeOf<PopLayerCmd>(),
        DrawOp.SetBlend => Unsafe.SizeOf<SetBlendCmd>(),
        _ => throw new InvalidOperationException("unexpected op " + op),
    };

    [Fact]
    public void Plain_opaque_child_culls_the_parent_fill()
        => Assert.False(ParentFillRecorded((_, _, _) => { }));

    [Fact]
    public void Additive_child_keeps_the_parent_fill()
        => Assert.True(ParentFillRecorded((s, _, c) => s.SetBlend(c, PaintBlend.Additive, LayerBlend.SrcOver)));

    [Fact]
    public void Additive_parent_keeps_its_own_fill()
        => Assert.True(ParentFillRecorded((s, p, _) => s.SetBlend(p, PaintBlend.Additive, LayerBlend.SrcOver)));

    [Fact]
    public void Screen_child_keeps_the_parent_fill()
        => Assert.True(ParentFillRecorded((s, _, c) => s.SetBlend(c, PaintBlend.SrcOver, LayerBlend.Screen)));

    [Fact]
    public void Acrylic_child_keeps_the_parent_fill()
        => Assert.True(ParentFillRecorded((s, _, c) =>
            s.SetAcrylic(c, new AcrylicSpec(ChildFill, 0.15f, 30f, 0.02f, 0.96f, ChildFill))));

    [Fact]
    public void Clip_rect_child_keeps_the_parent_fill()
        => Assert.True(ParentFillRecorded((s, _, c) => s.Paint(c).ClipRect = new RectF(0f, 0f, 50f, 40f)));
}
