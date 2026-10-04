using FluentGpu.Foundation;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <see cref="SceneStore.AbsoluteTransformedRect(NodeHandle)"/> (L2-09, F129): the window-space rect a node actually paints
/// at, through the FULL affine walk. <see cref="SceneStore.AbsoluteRect"/> folds only translation, so a video placement
/// derived from it under a scaled ancestor would land away from the punched hole.
/// </summary>
public sealed class SceneStoreTransformedRectTests
{
    static NodeHandle Node(SceneStore scene, RectF bounds, NodeHandle parent = default)
    {
        NodeHandle n = scene.CreateNode(1);
        scene.Bounds(n) = bounds;
        scene.Paint(n).LocalTransform = Affine2D.Identity;
        scene.Paint(n).OriginX = 0.5f;
        scene.Paint(n).OriginY = 0.5f;
        if (!parent.IsNull) scene.AppendChild(parent, n);
        return n;
    }

    static void AssertRect(RectF want, RectF got, float tol = 0.01f)
    {
        Assert.True(MathF.Abs(want.X - got.X) <= tol && MathF.Abs(want.Y - got.Y) <= tol
                 && MathF.Abs(want.W - got.W) <= tol && MathF.Abs(want.H - got.H) <= tol,
            $"want {want} got {got}");
    }

    [Fact]
    public void TranslationOnlyChain_IsExactlyAbsoluteRect()
    {
        var scene = new SceneStore();
        var root = Node(scene, new RectF(10.3f, 20.7f, 400, 300));
        scene.Paint(root).LocalTransform = Affine2D.Translation(5.1f, -2.2f);
        scene.Paint(root).ChildShiftY = -40f;
        var mid = Node(scene, new RectF(7.7f, 3.3f, 300, 200), root);
        var leaf = Node(scene, new RectF(1.1f, 2.2f, 100, 50), mid);

        Assert.Equal(scene.AbsoluteRect(leaf), scene.AbsoluteTransformedRect(leaf));
        Assert.Equal(scene.AbsoluteRect(mid), scene.AbsoluteTransformedRect(mid));
    }

    [Fact]
    public void ScaledAncestor_ScalesAboutItsOriginAndMovesTheChild()
    {
        var scene = new SceneStore();
        var root = Node(scene, new RectF(100, 50, 400, 300));
        scene.Paint(root).LocalTransform = Affine2D.Scale(2f, 2f);   // about the centre (200,150 in root space)
        var child = Node(scene, new RectF(20, 10, 100, 50), root);

        // child-local x -> window: 100 + 200 + 2 * ((20 + x) - 200) = 2x - 60; y: 50 + 150 + 2 * ((10 + y) - 150) = 2y - 80
        AssertRect(new RectF(-60, -80, 200, 100), scene.AbsoluteTransformedRect(child));
        // The translation-only walk knows nothing of the scale: this is the misplacement the transformed rect removes.
        Assert.NotEqual(scene.AbsoluteRect(child).W, scene.AbsoluteTransformedRect(child).W);
    }

    [Fact]
    public void ScaleOnTheNodeItself_ScalesItsOwnExtent()
    {
        var scene = new SceneStore();
        var node = Node(scene, new RectF(40, 30, 200, 100));
        scene.Paint(node).LocalTransform = Affine2D.Scale(0.5f, 0.5f);

        // Centre (140,80) is fixed: the rect shrinks to 100x50 around it.
        AssertRect(new RectF(90, 55, 100, 50), scene.AbsoluteTransformedRect(node));
    }

    [Fact]
    public void ScaleWithNonCentreOrigin_UsesTheNodesOrigin()
    {
        var scene = new SceneStore();
        var node = Node(scene, new RectF(40, 30, 200, 100));
        scene.Paint(node).OriginX = 0f;
        scene.Paint(node).OriginY = 0f;
        scene.Paint(node).LocalTransform = Affine2D.Scale(2f, 2f);

        // The top-left corner is fixed.
        AssertRect(new RectF(40, 30, 400, 200), scene.AbsoluteTransformedRect(node));
    }

    [Fact]
    public void ChildShiftOfAScaledParent_IsScaledWithIt()
    {
        var scene = new SceneStore();
        var root = Node(scene, new RectF(0, 0, 200, 200));
        scene.Paint(root).OriginX = 0f;
        scene.Paint(root).OriginY = 0f;
        scene.Paint(root).LocalTransform = Affine2D.Scale(2f, 2f);
        scene.Paint(root).ChildShiftY = -10f;   // a scrolled viewport's content offset
        var child = Node(scene, new RectF(5, 20, 50, 40), root);

        // root-space child origin (5, 20 - 10) = (5, 10), doubled by the parent's scale.
        AssertRect(new RectF(10, 20, 100, 80), scene.AbsoluteTransformedRect(child));
    }

    [Fact]
    public void Rotation_YieldsTheAxisAlignedBoundingBox()
    {
        var scene = new SceneStore();
        var node = Node(scene, new RectF(0, 0, 100, 50));
        scene.Paint(node).LocalTransform = Affine2D.Rotation(MathF.PI / 2f);   // about the centre (50,25)

        AssertRect(new RectF(25, -25, 50, 100), scene.AbsoluteTransformedRect(node), 0.05f);
    }

    [Fact]
    public void ExplicitLocalSize_IsMappedThroughTheSameTransform()
    {
        var scene = new SceneStore();
        var root = Node(scene, new RectF(100, 50, 400, 300));
        scene.Paint(root).OriginX = 0f;
        scene.Paint(root).OriginY = 0f;
        scene.Paint(root).LocalTransform = Affine2D.Scale(2f, 2f);

        // A Reveal row presents a narrower extent than the final bounds; it must be scaled like the bounds are.
        AssertRect(new RectF(100, 50, 200, 600), scene.AbsoluteTransformedRect(root, 100f, 300f));
        // Translation-only: the explicit size replaces the bounds' size and the origin is unchanged.
        var plain = Node(scene, new RectF(7, 9, 400, 300));
        AssertRect(new RectF(7, 9, 123, 45), scene.AbsoluteTransformedRect(plain, 123f, 45f));
    }
}
