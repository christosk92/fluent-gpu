using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A KeepAlive page parks holding whatever its exit composed onto its root: neither a settle nor CancelAll
/// writes opacity or transform back. Coming back with an Enter that names fewer channels (a fade after a slide) left the
/// page shifted, and coming back with no Enter at all (reduced motion turned on while the page was parked) left it
/// transparent: a blank content card.</summary>
[Collection(SerialTestCollection.Name)]   // flips the process-wide Motion.ReducedMotion
public sealed class KeepAliveParkedPoseTests
{
    private static readonly LayoutTransition Slide = new(
        TransitionChannels.Position | TransitionChannels.Opacity, TransitionDynamics.Tween(100f, Easing.Linear),
        Enter: new EnterExit(Dx: 8f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: -8f, Opacity: 0f, Active: true));

    private static readonly LayoutTransition Fade = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(100f, Easing.Linear),
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true));

    private static readonly Context<string> PageName = new("");

    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly AnimEngine Anim;
        public readonly TreeReconciler Recon;
        public readonly Signal<string> Page = new("a");
        public readonly Dictionary<string, NodeHandle> Bodies = new();
        public LayoutTransition? Next;

        public ref NodePaint RootPaint(string page) => ref Scene.Paint(Scene.Parent(Bodies[page]));

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Anim = new AnimEngine(Scene);
            Recon = new TreeReconciler(Scene, new StringTable()) { Anim = Anim };
            Recon.ReconcileRoot(new BoxEl
            {
                Width = 320f, Height = 240f,
                Children =
                [
                    Flow.KeepAlive(() => Page.Value, static k => k,
                        // Wavee's page shape: the slot root is a context provider over the page box.
                        k => Ctx.Provide(PageName, k, new BoxEl { Width = 320f, Height = 240f, OnRealized = n => Bodies[k] = n }),
                        new KeepAliveOptions(TransitionFor: (_, _) => Next)),
                ],
            }, null);
        }

        public void Go(string page, LayoutTransition? transition)
        {
            Next = transition;
            Page.Value = page;
            Recon.Runtime.Flush();
            for (int i = 0; i < 400 && Anim.HasUiWork; i++) Anim.Tick(16f);
            Assert.False(Anim.HasUiWork);
            Recon.FinalizeKeepAliveTransitions();   // park the outgoing page now its exit settled
        }
    }

    [Fact]
    public void AFadeBackToAPageThatSlidOutLandsItAtItsAuthoredOffset()
    {
        var rig = new Rig();
        rig.Go("b", Slide);   // a slides out to Dx -8 and parks there
        rig.Go("a", Fade);    // the fade's Enter seeds opacity only

        ref NodePaint p = ref rig.RootPaint("a");
        Assert.Equal(1f, p.Opacity, 3);
        Assert.Equal(0f, p.LocalTransform.Dx, 2);   // was -8: the exit's terminal survived the park
    }

    [Fact]
    public void ReturningUnderReducedMotionShowsTheParkedPageOpaque()
    {
        bool prev = Motion.ReducedMotion;
        try
        {
            var rig = new Rig();
            rig.Go("b", Slide);           // a fades to 0 and parks
            Assert.Equal(0f, rig.RootPaint("a").Opacity, 3);
            Motion.ReducedMotion = true;  // the OS animation setting flips while a is parked
            rig.Go("a", Slide);           // no Enter runs under reduced motion

            ref NodePaint p = ref rig.RootPaint("a");
            Assert.Equal(1f, p.Opacity, 3);             // was 0: a blank page
            Assert.Equal(0f, p.LocalTransform.Dx, 2);   // was -8
        }
        finally { Motion.ReducedMotion = prev; }
    }
}
