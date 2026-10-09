using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A SizeMode.Reflow exit closes along its PARENT's main axis, the axis the enter opened on and the one
/// FlexLayout.AddOrphanMain reads. It used to read the exiting node's own Direction: a default-row drawer in a column
/// eased its WIDTH to 0 (a sideways wipe), left LayoutInput.Height NaN, and gave the parent no closing height.</summary>
public sealed class ReflowExitAxisTests
{
    private static readonly LayoutTransition Drawer = new(TransitionChannels.Size | TransitionChannels.Opacity,
        TransitionDynamics.Tween(300f, Easing.Linear), SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true));

    private static (SceneStore scene, AnimEngine anim, NodeHandle node) ExitUnder(byte parentDirection, byte ownDirection)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var parent = scene.CreateNode(2);
        scene.AppendChild(root, parent);
        scene.Layout(parent).Direction = parentDirection;
        var node = scene.CreateNode(3);
        scene.AppendChild(parent, node);
        scene.Layout(node).Direction = ownDirection;
        scene.Layout(node).Width = float.NaN;                // declared auto on both axes
        scene.Layout(node).Height = float.NaN;
        scene.Bounds(node) = new(0, 40, 300, 120);
        var anim = new AnimEngine(scene);
        anim.SetTransition(node, Drawer);
        scene.Orphan(node, 400f);                            // Reconciler.Remove orphans BEFORE SeedExit
        anim.SeedExit(node, Drawer.Exit, Drawer);
        for (int i = 0; i < 6; i++) { anim.Tick(16.67f); anim.ReflowRoots.Clear(); }
        return (scene, anim, node);
    }

    [Fact]
    public void ADefaultRowDrawerInAColumnClosesItsHeight()
    {
        var (scene, _, node) = ExitUnder(parentDirection: 1, ownDirection: 0);
        Assert.True(float.IsNaN(scene.Layout(node).Width), $"exit eased the width: W={scene.Layout(node).Width}");
        Assert.InRange(scene.Layout(node).Height, 1f, 119f);
    }

    [Fact]
    public void AColumnDrawerInARowClosesItsWidth()
    {
        var (scene, _, node) = ExitUnder(parentDirection: 0, ownDirection: 1);
        Assert.True(float.IsNaN(scene.Layout(node).Height), $"exit eased the height: H={scene.Layout(node).Height}");
        Assert.InRange(scene.Layout(node).Width, 1f, 299f);
    }
}
