using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The retained animation hooks (UseSpring/UseTransition/UseKeyframes/UseDrivenAnimation and their Component
/// wrappers) key their deps cell to the CALLER's call site. They were keyed to their own line in RenderContext, so a
/// conditionally skipped one handed its stored deps to the next call of the same hook, and a real target change could
/// compare equal and never seed.</summary>
public sealed class AnimHookCallSiteTests
{
    private static (RenderContext Ctx, SceneStore Scene, NodeHandle Node, AnimEngine Anim) Host(RenderContext? ctx = null)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        var anim = new AnimEngine(scene);
        ctx ??= new RenderContext();
        ctx.Runtime = new ReactiveRuntime();
        ctx.Scene = scene;
        ctx.HostNode = node;
        ctx.Anim = anim;
        return (ctx, scene, node, anim);
    }

    private static void Settle(RenderContext c, AnimEngine anim)
    {
        foreach (var run in c.PendingLayoutEffects.ToArray()) run();
        c.PendingLayoutEffects.Clear();
        for (int i = 0; i < 20; i++) anim.Tick(16.67f);
    }

    private static void Fade(RenderContext c, AnimEngine anim, bool slide, float opacity)
    {
        c.BeginRender();
        if (slide) c.UseTransition(AnimChannel.TranslateX, 0f, 10f, 100f, Easing.Linear, 0.5f);
        c.UseTransition(AnimChannel.Opacity, 1f, opacity, 100f, Easing.Linear, opacity);
        c.EndRender();
        Settle(c, anim);
    }

    [Fact]
    public void Skipping_a_transition_still_seeds_the_next_transitions_new_target()
    {
        var (c, scene, node, anim) = Host();
        Fade(c, anim, slide: true, opacity: 1f);
        Fade(c, anim, slide: false, opacity: 0.5f);   // read the slide's deps (0.5) as its own and never seeded
        Assert.Equal(0.5f, scene.Paint(node).Opacity, 3);
    }

    private sealed class Card : Component
    {
        public bool Lift;
        public float Opacity = 1f;
        public override Element Render()
        {
            if (Lift) UseTransition(AnimChannel.TranslateY, 0f, -4f, 100f, Easing.Linear, 0.5f);
            UseTransition(AnimChannel.Opacity, 1f, Opacity, 100f, Easing.Linear, Opacity);
            return new BoxEl();
        }
    }

    [Fact]
    public void Component_wrappers_key_the_transition_to_the_components_call_site()
    {
        var card = new Card { Lift = true };
        var (c, scene, node, anim) = Host(card.Context);
        c.BeginRender(); card.Render(); c.EndRender(); Settle(c, anim);
        card.Lift = false; card.Opacity = 0.5f;
        c.BeginRender(); card.Render(); c.EndRender(); Settle(c, anim);
        Assert.Equal(0.5f, scene.Paint(node).Opacity, 3);
    }
}
