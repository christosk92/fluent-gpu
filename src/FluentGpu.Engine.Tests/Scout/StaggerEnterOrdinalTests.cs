using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>Element.Stagger delays each ENTERING child's Enter by its ordinal among the children entering in that
/// reconcile. A keyed diff mounts new children after every old one (removal comes later), so the live sibling index
/// counted the departing siblings: replacing a staggered row left the new tiles invisible for (old count × stagger). The
/// delay was also baked into LayoutTransition.DelayMs, which held every later Exit and FLIP move by index × stagger.</summary>
public sealed class StaggerEnterOrdinalTests
{
    private const float Per = 100f;
    private static readonly EnterExit Fade = new(Opacity: 0f, Active: true);

    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly AnimEngine Anim;
        public readonly TreeReconciler Recon;
        public readonly Dictionary<string, NodeHandle> Nodes = new();
        private Element? _last;

        public Harness()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Anim = new AnimEngine(Scene);
            Recon = new TreeReconciler(Scene, new StringTable()) { Anim = Anim };
        }

        public BoxEl Tile(string key, bool exit = false, LayoutTransition? layout = null) => new()
        {
            Key = key, Width = 20f, Height = 20f, Enter = Fade, Exit = exit ? Fade : null, Layout = layout,
            Transition = MotionTok.ControlNormal, OnRealized = n => Nodes[key] = n,
        };

        public void Render(params Element[] tiles)
        {
            var root = new BoxEl { Direction = 1, Stagger = Per, Children = tiles };
            Recon.ReconcileRoot(root, _last);
            _last = root;
        }

        public void Settle()
        {
            for (int i = 0; i < 400 && Anim.HasUiWork; i++) Anim.Tick(16f);
            Assert.False(Anim.HasUiWork);
        }

        public void Frames(int n) { for (int i = 0; i < n; i++) Anim.Tick(16f); }
        public float Opacity(string key) => Scene.Paint(Nodes[key]).Opacity;
    }

    [Fact]
    public void ReplacingEveryKeyedChildStaggersTheNewOnesFromZero()
    {
        var h = new Harness();
        h.Render(h.Tile("a0"), h.Tile("a1"), h.Tile("a2"), h.Tile("a3"));
        h.Settle();

        h.Render(h.Tile("b0"), h.Tile("b1"), h.Tile("b2"), h.Tile("b3"));
        h.Frames(3);   // ~32 ms of motion: under one stagger step
        Assert.True(h.Opacity("b0") > 0.01f, $"b0={h.Opacity("b0"):0.000}");   // was held 4 × 100 ms behind the departing a0..a3
        Assert.True(h.Opacity("b1") < 0.01f, $"b1={h.Opacity("b1"):0.000}");   // the stagger itself still applies
    }

    [Fact]
    public void APrependedChildEntersWithoutWaitingOnTheExistingRow()
    {
        var h = new Harness();
        h.Render(h.Tile("a0"), h.Tile("a1"), h.Tile("a2"));
        h.Settle();

        h.Render(h.Tile("new"), h.Tile("a0"), h.Tile("a1"), h.Tile("a2"));
        h.Frames(3);
        Assert.True(h.Opacity("new") > 0.01f, $"new={h.Opacity("new"):0.000}");   // was 3 × 100 ms: appended after a0..a2
    }

    [Fact]
    public void ARemovedChildsExitIsNotHeldByTheEntranceStagger()
    {
        var h = new Harness();
        h.Render(h.Tile("k0", exit: true), h.Tile("k1", exit: true), h.Tile("k2", exit: true), h.Tile("k3", exit: true));
        h.Settle();
        NodeHandle leaving = h.Nodes["k3"];

        h.Render(h.Tile("k0", exit: true), h.Tile("k1", exit: true), h.Tile("k2", exit: true));
        h.Frames(3);
        float op = h.Scene.Paint(leaving).Opacity;
        Assert.True(op < 0.99f, $"exit opacity={op:0.000}");   // was 1: SeedExit read the 300 ms baked into DelayMs
    }

    [Fact]
    public void ALayoutChildsMovesAreNotDelayedButItsEnterStillIs()
    {
        var h = new Harness();
        var flip = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Tween(200f, Easing.Linear));
        h.Render(h.Tile("c0", layout: flip), h.Tile("c1", layout: flip), h.Tile("c2", layout: flip));

        Assert.True(h.Anim.TryGetTransition(h.Nodes["c2"], out LayoutTransition spec));
        Assert.Equal(0f, spec.DelayMs);                       // AnimateBounds / SeedExit read this: was 2 × 100
        Assert.Equal(2f * Per, spec.Enter.DelayMs, 3);       // the entrance keeps its stagger
    }
}
