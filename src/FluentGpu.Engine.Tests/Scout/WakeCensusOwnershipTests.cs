using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The wake census (<see cref="AnimEngine.NextDueMs"/>) counted a render-owned compositor loop and a held row as
/// due NOW. Neither asks the UI loop for a frame, so a looping meter beside a focused caret pinned the host's cadence
/// wait to 0 and the UI loop ran at the panel rate instead of sleeping between blinks.</summary>
public sealed class WakeCensusOwnershipTests
{
    const AnimChannel Ch = AnimChannel.TranslateX;

    private static (NodeHandle Node, AnimEngine Animation) Fixture(bool renderOwns)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new(0, 0, 200, 100);
        scene.Paint(root).VisualKind = VisualKind.Box;
        var node = scene.CreateNode(2);
        scene.AppendChild(root, node);
        scene.Bounds(node) = new(0, 0, 10, 10);
        scene.Paint(node).VisualKind = VisualKind.Box;
        var animation = new AnimEngine(scene) { RenderOwnsCompositor = renderOwns };
        animation.Keyframes(node, Ch, [new(0f, 0f, Easing.Linear), new(1f, 100f, Easing.Linear)], 1000f, loop: true);
        return (node, animation);
    }

    [Fact]
    public void ARenderOwnedLoop_IsNeverDueOnTheUiLoop()
    {
        var (_, animation) = Fixture(renderOwns: true);
        animation.Tick(16f);
        animation.Tick(16f);
        Assert.False(animation.HasUiWork);
        Assert.True(float.IsPositiveInfinity(animation.NextDueMs(0d)));
        Assert.Equal(1, animation.DisplayRateLoopCount);   // still visible to the diagnostics
    }

    [Fact]
    public void AUiOwnedLoop_IsDueEveryFrame_UntilHeld()
    {
        var (node, animation) = Fixture(renderOwns: false);
        animation.Tick(16f);
        animation.Tick(16f);
        Assert.Equal(0f, animation.NextDueMs(0d));          // control: a UI-owned display-rate loop owes every frame
        animation.SetHeld(node, Ch, true);
        Assert.True(float.IsPositiveInfinity(animation.NextDueMs(0d)));   // a held row asks for no frames
        animation.SetHeld(node, Ch, false);
        Assert.Equal(0f, animation.NextDueMs(0d));
    }

    [Fact]
    public void HandingTheCompositorToTheRenderThread_RefreshesTheMemoizedCensus()
    {
        var (_, animation) = Fixture(renderOwns: false);
        Assert.Equal(0f, animation.NextDueMs(0d));          // memoizes a UI-owned census
        animation.RenderOwnsCompositor = true;
        Assert.True(float.IsPositiveInfinity(animation.NextDueMs(0d)));
        animation.RenderOwnsCompositor = false;
        Assert.Equal(0f, animation.NextDueMs(0d));
    }
}
