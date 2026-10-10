using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The caret blink runs on the host's timer clock. An idle host with a focused editor sleeps
/// <see cref="CaretBlinker.NextDueMs"/> of WALL time, while the frame delta it used to be fed is clamped to 34 ms, so the
/// caret gained 34 ms per ~500 ms wake and toggled only every ~4 s (15 wakes per toggle instead of one).</summary>
public sealed class CaretBlinkClockTests
{
    private static (SceneStore Scene, NodeHandle Node, CaretBlinker Blinker) Focused()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        var blinker = new CaretBlinker(scene);
        blinker.Focus(node);
        return (scene, node, blinker);
    }

    private static bool Visible(SceneStore scene, NodeHandle node)
        => scene.TryGetTextEdit(node, out var tes) && (tes.Flags & TextEditState.CaretVisible) != 0;

    [Fact]
    public void AnIdleHostSleepingNextDueMs_TogglesTheCaretOnEveryWake()
    {
        var (scene, node, blinker) = Focused();
        double now = 10_000.0;
        blinker.Tick(now);                                       // the focusing frame anchors the phase
        Assert.True(Visible(scene, node));
        for (int wake = 1; wake <= 4; wake++)
        {
            float due = blinker.NextDueMs();
            Assert.Equal(CaretBlinker.DefaultBlinkMs, due, 3);  // one wait per half-period, not 15
            now += due;                                          // the host sleeps exactly the blinker's own wait...
            blinker.Tick(now);                                   // ...and the frame it wakes for toggles
            Assert.Equal(wake % 2 == 0, Visible(scene, node));
        }
    }

    [Fact]
    public void ALongStall_NetsTheParityInOneTick()
    {
        var (scene, node, blinker) = Focused();
        blinker.Tick(0.0);
        blinker.Tick(3 * CaretBlinker.DefaultBlinkMs + 10.0);   // three half-periods: odd, so the caret ends hidden
        Assert.False(Visible(scene, node));
        Assert.Equal(CaretBlinker.DefaultBlinkMs - 10f, blinker.NextDueMs(), 3);
    }

    [Fact]
    public void ReFocus_ReanchorsThePhase_SoTimeSpentBlurredDoesNotCount()
    {
        var (scene, node, blinker) = Focused();
        blinker.Tick(0.0);
        blinker.Blur(node);
        blinker.Focus(node);
        blinker.Tick(60_000.0);                                  // the first tick after Focus anchors, it does not age
        Assert.True(Visible(scene, node));
        Assert.Equal(CaretBlinker.DefaultBlinkMs, blinker.NextDueMs(), 3);
    }
}
