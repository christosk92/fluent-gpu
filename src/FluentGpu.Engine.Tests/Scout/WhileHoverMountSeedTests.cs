using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A WhileHover child that mounts inside an already-hovered card takes the card's hover on mount. The mount
/// seed used to be reachable only through the HoverScale/HoverOpacity props and never drove the node's own While* row,
/// so a decorative badge (HitTestVisible=false, no handlers) sat at rest until the pointer left and re-entered the card:
/// the card's edge had already fired and moving inside it fires no new one.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class WhileHoverMountSeedTests
{
    private static float SettledLiftAfterMountingIntoHoveredCard(BoxEl badge)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, new StringTable()) { Anim = anim };
        BoxEl Card(bool withBadge) => new() { OnClick = static () => { }, Children = withBadge ? [badge] : [] };

        var bare = Card(false);
        recon.ReconcileRoot(bare, null);
        var card = scene.Root;
        scene.Mark(card, NodeFlags.Hovered);                                  // the dispatcher's flag ...
        anim.SetHover(card, true);                                            // ... and AppHost.OnHoverChanged's edge pair
        anim.ApplyInteractionEdge(card, AnimEngine.InteractKind.Hover, true);

        recon.ReconcileRoot(Card(true), bare);                                // mounts after the card's edge already fired
        var mounted = scene.FirstChild(card);
        Assert.False(mounted.IsNull);
        for (int i = 0; i < 400 && anim.HasUiWork; i++) anim.Tick(16f);
        Assert.False(anim.HasUiWork);
        return scene.Paint(mounted).LocalTransform.Dy;
    }

    [Fact]
    public void ADecorativeWhileHoverChildMountedUnderAHoveredCardLifts()
    {
        float dy = SettledLiftAfterMountingIntoHoveredCard(
            new BoxEl { HitTestVisible = false, WhileHover = new MotionTarget { OffsetY = -4f } });
        Assert.Equal(-4f, dy, 2);   // was 0: never seeded, no further container edge comes
    }

    [Fact]
    public void AHoverRevealThatAlsoDeclaresWhileHoverSeedsItsOwnLift()
    {
        float dy = SettledLiftAfterMountingIntoHoveredCard(
            new BoxEl { HitTestVisible = false, HoverOpacity = 1f, WhileHover = new MotionTarget { OffsetY = -4f } });
        Assert.Equal(-4f, dy, 2);   // was 0: the reveal was seeded, its own While* row was not
    }

    [Fact]
    public void ANestedControlMountedUnderAHoveredCardStaysAtRest()
    {
        float dy = SettledLiftAfterMountingIntoHoveredCard(
            new BoxEl { OnClick = static () => { }, WhileHover = new MotionTarget { OffsetY = -4f } });
        Assert.Equal(0f, dy, 2);    // its own interaction scope: only its own pointer edge drives it
    }
}
