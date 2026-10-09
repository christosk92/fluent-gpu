using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The host's power ceiling (<c>AppHost.PowerCapFps</c>, Windows Energy Saver) reaches motion the render thread owns:
/// a display-cadence loop ticked every 120 Hz vblank advances at most once per ceiling interval, every other tick re-poses
/// the identical value (so the host elides that turn), and clearing the ceiling restores the panel rate at the absolute phase.</summary>
public sealed class PowerCeilingCompositorTests
{
    const AnimChannel Ch = AnimChannel.TranslateX;
    const double VblankMs = 1000.0 / 120.0;

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Animation) Fixture()
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
        var animation = new AnimEngine(scene) { RenderOwnsCompositor = true };
        // 0 -> 100 over 1000 ms, linear, looping, no cadence (display rate): the value is a tenth of the phase in ms.
        animation.Keyframes(node, Ch, [new(0f, 0f, Easing.Linear), new(1f, 100f, Easing.Linear)], 1000f, loop: true);
        return (scene, node, animation);
    }

    private static (SceneRecordingSnapshot Snapshot, RenderCompositorAnimations Renderer) Adopted(SceneStore scene, AnimEngine animation, float ceilingMs)
    {
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations { CeilingPeriodMs = ceilingMs };
        renderer.Adopt(desired, snapshot, 0);
        return (snapshot, renderer);
    }

    [Fact]
    public void A_render_owned_loop_advances_at_the_power_ceiling_not_the_panel_rate()
    {
        var (scene, node, animation) = Fixture();
        var (snapshot, renderer) = Adopted(scene, animation, (float)CadencePacing.LatticePeriodMs(1000.0 / 30, VblankMs));
        int changed = 0;
        float last = snapshot.Paint(node).LocalTransform.Dx;
        for (int i = 1; i <= 120; i++)   // one second of 120 Hz vblanks
        {
            renderer.Tick(snapshot, i * VblankMs);
            float dx = snapshot.Paint(node).LocalTransform.Dx;
            if (renderer.ChangedThisTick) changed++;
            else Assert.Equal(last, dx);   // a held tick re-poses the identical value: the host elides the turn
            last = dx;
        }
        Assert.InRange(changed, 29, 31);   // 30 fps on the lattice (every 4th vblank); before: 120
        Assert.True(renderer.HasActive);   // paced, not stopped
        snapshot.ReleaseResources();
    }

    [Fact]
    public void Clearing_the_ceiling_restores_the_panel_rate_at_the_absolute_phase()
    {
        var (scene, node, animation) = Fixture();
        var (snapshot, renderer) = Adopted(scene, animation, (float)(4 * VblankMs));
        for (int i = 1; i <= 6; i++) renderer.Tick(snapshot, i * VblankMs);
        // Held since the advance on tick 4 (33.3 ms): the pose is that tick's phase, not tick 6's (5.0).
        Assert.Equal(3.333f, snapshot.Paint(node).LocalTransform.Dx, 2);
        renderer.CeilingPeriodMs = 0f;
        for (int i = 7; i <= 10; i++)
        {
            renderer.Tick(snapshot, i * VblankMs);
            Assert.True(renderer.ChangedThisTick);
            Assert.Equal((float)(i * VblankMs / 10.0), snapshot.Paint(node).LocalTransform.Dx, 2);
        }
        snapshot.ReleaseResources();
    }

    [Theory]
    [InlineData(30, 120.0, 4)]   // 33.3 ms = four 120 Hz refreshes
    [InlineData(30, 144.0, 5)]   // 4.8 refreshes rounds to 5, as QuantizedWaitMs paces it
    [InlineData(30, 60.0, 2)]
    [InlineData(240, 60.0, 1)]   // never faster than the panel
    public void The_ceiling_lands_on_the_vblank_lattice(int fps, double hz, int refreshes)
        => Assert.Equal(refreshes * 1000.0 / hz, CadencePacing.LatticePeriodMs(1000.0 / fps, 1000.0 / hz), 6);
}
