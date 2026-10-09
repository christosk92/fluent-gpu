using System;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>Composite hooks (UseField, UseGesture, UseActivation) key every inner cell to the CALLER's call site, so a
/// conditionally skipped call keeps its cells and never hands them to the next call of the same hook (reactivity.md:
/// conditional hooks are legal). They were keyed to the composite's own source lines and told apart only by call order.</summary>
public sealed class CompositeHookCallSiteTests
{
    private static Field<string> Signup(RenderContext c, bool business, Signal<int> employees, Signal<string> email)
    {
        c.BeginRender();
        if (business) c.UseField(employees, Rules.Predicate<int>(n => n > 0, "validation.range"));
        var field = c.UseField(email, Rules.Required());
        c.EndRender();
        return field;
    }

    [Fact]
    public void Skipping_a_field_of_another_type_keeps_the_next_fields_cells()
    {
        var c = new RenderContext { Runtime = new ReactiveRuntime() };
        var employees = new Signal<int>(0);
        var email = new Signal<string>("");
        var first = Signup(c, business: true, employees, email);
        var second = Signup(c, business: false, employees, email);   // threw InvalidCastException: Ref<Field<int>?> → Ref<Field<string>?>
        Assert.Same(first, second);
        Assert.Same(email, second.Value);
    }

    private static Field<string> Contact(RenderContext c, bool withCompany, Signal<string> company, Signal<string> email)
    {
        c.BeginRender();
        if (withCompany) c.UseField(company, Rules.Required());
        var field = c.UseField(email, Rules.Required());
        c.EndRender();
        return field;
    }

    [Fact]
    public void Skipping_a_field_of_the_same_type_does_not_swap_fields()
    {
        var c = new RenderContext { Runtime = new ReactiveRuntime() };
        var company = new Signal<string>("");
        var email = new Signal<string>("");
        var first = Contact(c, withCompany: true, company, email);
        var second = Contact(c, withCompany: false, company, email);
        Assert.Same(first, second);          // was the company field: its value, rules and touched state
        Assert.Same(email, second.Value);
    }

    private static void Gestures(RenderContext c, bool canHold, Action<GestureEventArgs> hold, Action<GestureEventArgs> tap)
    {
        c.BeginRender();
        if (canHold) c.UseGesture(GestureType.Hold, hold);
        c.UseGesture(GestureType.Tap, tap);
        c.EndRender();
        foreach (var run in c.PendingLayoutEffects.ToArray()) run();
        c.PendingLayoutEffects.Clear();
    }

    [Fact]
    public void Skipping_a_gesture_routes_the_next_gestures_latest_handler()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        var c = new RenderContext { Runtime = new ReactiveRuntime(), Scene = scene, HostNode = node };
        int holds = 0, oldTaps = 0, newTaps = 0;
        Gestures(c, canHold: true, _ => holds++, _ => oldTaps++);
        Gestures(c, canHold: false, _ => holds++, _ => newTaps++);
        scene.GetGestureHandler(node, GestureType.Tap)!(new GestureEventArgs { Kind = GestureType.Tap });
        Assert.Equal((0, 1), (oldTaps, newTaps));   // was (1, 0): the Tap call wrote its handler into the Hold state
        scene.GetGestureHandler(node, GestureType.Hold)!(new GestureEventArgs { Kind = GestureType.Hold });
        Assert.Equal((1, 1), (holds, newTaps));     // a long-press runs the hold handler, not the tap one
    }

    private static void Lifecycle(RenderContext c, bool polling, Action onPollParked, Action onSyncParked)
    {
        c.BeginRender();
        if (polling) c.UseActivation(onDeactivated: onPollParked);
        c.UseActivation(onDeactivated: onSyncParked);
        c.EndRender();
    }

    [Fact]
    public void Skipping_an_activation_hook_fires_the_next_ones_callback_once()
    {
        var rt = new ReactiveRuntime();
        var active = new Signal<bool>(true);
        var c = new RenderContext { Runtime = rt, GetActiveSig = () => active };
        int poll = 0, sync = 0;
        Lifecycle(c, polling: true, () => poll++, () => sync++);
        Lifecycle(c, polling: false, () => poll++, () => sync++);
        active.Value = false;
        rt.Flush();
        Assert.Equal((1, 1), (poll, sync));   // was (0, 2): the skipped call's effect read the second call's callbacks
    }
}
