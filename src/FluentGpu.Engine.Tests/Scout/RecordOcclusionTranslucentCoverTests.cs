using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The record-time occlusion cull (SceneRecorder.IsOccludedByOpaqueChild) drops a node's own fill under a full-size
/// opaque child. A child only REPLACES those pixels when it draws fully opaque over its whole bounds. A partial non-group
/// opacity on the parent (its own Enter fade, or an ancestor's) draws the child at that same alpha. A hover/press opacity or a
/// translucent hover/press fill makes the child see-through under the pointer. An edge fade feathers the child's fill
/// to transparent. Each of these keeps the parent's fill.</summary>
public sealed class RecordOcclusionTranslucentCoverTests
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
    public void Fading_parent_keeps_its_fill()
        => Assert.True(ParentFillRecorded((s, p, _) => s.Paint(p).Opacity = 0.5f));

    [Fact]
    public void Inherited_partial_opacity_keeps_the_parent_fill()
        => Assert.True(ParentFillRecorded((s, p, _) =>
        {
            var g = s.CreateNode(1);
            s.Bounds(g) = new RectF(0f, 0f, 100f, 40f);
            s.Paint(g).Opacity = 0.5f;
            s.AppendChild(g, p);
            s.Root = g;
        }));

    [Fact]
    public void Opacity_group_parent_still_culls_its_fill()
        => Assert.False(ParentFillRecorded((s, p, _) => { s.Paint(p).Opacity = 0.5f; s.Paint(p).OpacityGroup = true; }));

    [Fact]
    public void Hover_opacity_child_keeps_the_parent_fill()
        => Assert.True(ParentFillRecorded((s, _, c) => { s.Paint(c).HoverOpacity = 0.6f; s.SetFlagBits(c, NodeFlags.Hovered); }));

    [Fact]
    public void Pressed_opacity_child_keeps_the_parent_fill()
        => Assert.True(ParentFillRecorded((s, _, c) => { s.Paint(c).PressedOpacity = 0.6f; s.SetFlagBits(c, NodeFlags.Pressed); }));

    [Fact]
    public void Translucent_hover_fill_child_keeps_the_parent_fill()
        => Assert.True(ParentFillRecorded((s, _, c) =>
        {
            s.Paint(c).HoverFill = new ColorF(1f, 1f, 1f, 0.4f);
            s.SetFlagBits(c, NodeFlags.Hovered);
        }));

    [Fact]
    public void Edge_fade_child_keeps_the_parent_fill()
        => Assert.True(ParentFillRecorded((s, _, c) => s.SetEdgeFade(c, new EdgeFadeSpec(EdgeMask.Left | EdgeMask.Right, 12f))));
}
