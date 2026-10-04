using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The pump REASON (L2-08, F120): a pump requested only because a tracked surface's absolute rect moved
/// (<see cref="VideoSurfaceRegistry.RequestGeometryPumps"/>) is a geometry-only turn, so the owner may place the surface
/// and skip the session publish; a native / transport / activation request (<see cref="VideoSurfaceRegistry.RequestPump"/>)
/// is always a full pump and upgrades a pending geometry request. The flag is only true while the pump runs.
/// </summary>
public sealed class VideoSurfaceGeometryPumpTests
{
    private static (SceneStore Scene, NodeHandle Node) Scene()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new RectF(0f, 0f, 320f, 180f);
        return (scene, node);
    }

    private static void Move(SceneStore scene, NodeHandle node, float dx)
        => scene.Paint(node).LocalTransform = Affine2D.Translation(dx, 0f);

    [Fact]
    public void AMovedRect_RunsAGeometryOnlyPump()
    {
        var (scene, node) = Scene();
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        bool? geometryOnly = null;
        reg.RegisterPump(token, new object(), _ => geometryOnly = reg.IsGeometryOnlyPump(token));
        reg.PumpPending(1f);                    // the registration's own establishing pump
        Assert.False(geometryOnly);             // a registration request is a full pump
        reg.SetGeometryNode(token, node);

        reg.RequestGeometryPumps(scene);        // adopting a node places it once
        geometryOnly = null;
        reg.PumpPending(1f);
        Assert.True(geometryOnly);

        Move(scene, node, 24f);
        geometryOnly = null;
        reg.RequestGeometryPumps(scene);
        reg.PumpPending(1f);
        Assert.True(geometryOnly);
    }

    [Fact]
    public void AScaleChange_RunsAGeometryOnlyPump()
    {
        // F129: a scale-in entrance / zoom FLIP on the tracked node (or an ancestor) changes where the hole paints while
        // the translation-only rect stays put, so the geometry scan must watch the TRANSFORMED rect.
        var (scene, node) = Scene();
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        bool? geometryOnly = null;
        reg.RegisterPump(token, new object(), _ => geometryOnly = reg.IsGeometryOnlyPump(token));
        reg.PumpPending(1f);
        reg.SetGeometryNode(token, node);
        reg.RequestGeometryPumps(scene);
        reg.PumpPending(1f);

        scene.Paint(node).OriginX = 0.5f;
        scene.Paint(node).OriginY = 0.5f;
        scene.Paint(node).LocalTransform = Affine2D.Scale(1.5f, 1.5f);
        geometryOnly = null;
        reg.RequestGeometryPumps(scene);
        reg.PumpPending(1f);
        Assert.True(geometryOnly);
    }

    [Fact]
    public void ANativeOrTransportRequest_IsAlwaysAFullPump()
    {
        var (scene, node) = Scene();
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        bool? geometryOnly = null;
        reg.RegisterPump(token, new object(), _ => geometryOnly = reg.IsGeometryOnlyPump(token));
        reg.SetGeometryNode(token, node);
        reg.RequestGeometryPumps(scene);
        reg.PumpPending(1f);

        reg.RequestPump(token);
        reg.PumpPending(1f);
        Assert.False(geometryOnly);
    }

    [Fact]
    public void AFullRequest_UpgradesAPendingGeometryRequest_InEitherOrder()
    {
        var (scene, node) = Scene();
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        int runs = 0;
        bool? geometryOnly = null;
        reg.RegisterPump(token, new object(), _ => { runs++; geometryOnly = reg.IsGeometryOnlyPump(token); });
        reg.SetGeometryNode(token, node);
        reg.RequestGeometryPumps(scene);
        reg.PumpPending(1f);
        int baseRuns = runs;

        // geometry first, then a native event in the same turn: ONE pump, and it is a full one
        Move(scene, node, 10f);
        reg.RequestGeometryPumps(scene);
        reg.RequestPump(token);
        reg.PumpPending(1f);
        Assert.Equal(baseRuns + 1, runs);
        Assert.False(geometryOnly);

        // native first, then the geometry request finds the pump already pending: still full
        Move(scene, node, 20f);
        reg.RequestPump(token);
        reg.RequestGeometryPumps(scene);
        reg.PumpPending(1f);
        Assert.Equal(baseRuns + 2, runs);
        Assert.False(geometryOnly);

        // the reason does not leak into the next turn
        Move(scene, node, 30f);
        reg.RequestGeometryPumps(scene);
        reg.PumpPending(1f);
        Assert.Equal(baseRuns + 3, runs);
        Assert.True(geometryOnly);
    }

    [Fact]
    public void TheReasonIsOnlyTrueWhileThePumpRuns_AndOnlyForItsOwnSlot()
    {
        var (scene, node) = Scene();
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        int other = reg.Acquire();
        bool otherSlotFlag = true;
        reg.RegisterPump(token, new object(), _ => otherSlotFlag = reg.IsGeometryOnlyPump(other));
        bool ownSlotFlag = false;
        reg.SetGeometryNode(token, node);
        reg.RequestGeometryPumps(scene);
        reg.PumpPending(1f);

        // a real geometry-only turn for `token`: its own slot reports it, `other` never does
        reg.UnregisterPump(1);
        reg.RegisterPump(token, new object(), _ => { ownSlotFlag = reg.IsGeometryOnlyPump(token); otherSlotFlag = reg.IsGeometryOnlyPump(other); });
        reg.PumpPending(1f);                            // the new registration's own (full) pump
        otherSlotFlag = true;
        Move(scene, node, 16f);
        reg.RequestGeometryPumps(scene);
        reg.PumpPending(1f);
        Assert.True(ownSlotFlag);
        Assert.False(otherSlotFlag);

        Assert.False(reg.IsGeometryOnlyPump(token));    // not inside a pump
        Assert.False(new VideoBinding(reg, token).IsGeometryOnlyPump);
        Assert.False(default(VideoBinding).IsGeometryOnlyPump);
        Assert.False(reg.IsGeometryOnlyPump(0));
    }

    [Fact]
    public void ALayoutDrivenGeometryRequest_IsAGeometryOnlyPump_AndAFullRequestUpgradesIt()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        var binding = new VideoBinding(reg, token);
        int runs = 0;
        bool? geometryOnly = null;
        reg.RegisterPump(token, new object(), _ => { runs++; geometryOnly = reg.IsGeometryOnlyPump(token); });
        reg.PumpPending(1f);                    // the registration's own establishing pump
        int baseRuns = runs;

        // the element's area OnBoundsChanged (a resize / reflow): geometry class, coalesced into ONE pump
        binding.RequestGeometryPump();
        binding.RequestGeometryPump();
        Assert.True(reg.HasPendingPumps);
        reg.PumpPending(1f);
        Assert.Equal(baseRuns + 1, runs);
        Assert.True(geometryOnly);

        // a full request in the same turn upgrades it, in either order
        binding.RequestGeometryPump();
        binding.RequestPump();
        reg.PumpPending(1f);
        Assert.Equal(baseRuns + 2, runs);
        Assert.False(geometryOnly);

        binding.RequestPump();
        binding.RequestGeometryPump();
        reg.PumpPending(1f);
        Assert.Equal(baseRuns + 3, runs);
        Assert.False(geometryOnly);

        // the reason does not leak into the next turn
        binding.RequestGeometryPump();
        reg.PumpPending(1f);
        Assert.Equal(baseRuns + 4, runs);
        Assert.True(geometryOnly);
    }

    [Fact]
    public void AGeometryRequest_ForAReleasedOrOutOfRangeOrInertToken_IsANoOp()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        int runs = 0;
        reg.RegisterPump(token, new object(), _ => runs++);
        reg.PumpPending(1f);
        int baseRuns = runs;

        reg.RequestGeometryPump(0);
        reg.RequestGeometryPump(-3);
        reg.RequestGeometryPump(17);                // past the 16-slot table
        reg.RequestGeometryPump(token + 1);     // never acquired
        Assert.False(reg.HasPendingPumps);

        reg.Release(token);
        reg.RequestGeometryPump(token);
        Assert.False(reg.HasPendingPumps);
        reg.PumpPending(1f);
        Assert.Equal(baseRuns, runs);

        default(VideoBinding).RequestGeometryPump();    // an inert binding never throws
        Assert.False(reg.HasPendingPumps);
    }

    [Fact]
    public void ContentSize_IsTheLastWrittenSize_AndEmptyUntilOne()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        var binding = new VideoBinding(reg, token);
        Assert.True(binding.ContentSize.IsEmpty);

        binding.SetContentSize(new SizeI(640, 360));
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);
        Assert.Equal(new SizeI(640, 360), reg.ContentSize(token));

        reg.Release(token);
        Assert.True(reg.ContentSize(token).IsEmpty);
        Assert.True(default(VideoBinding).ContentSize.IsEmpty);
    }
}
