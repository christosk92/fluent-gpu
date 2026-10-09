using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// Removing the focused node only nulled the dispatcher's focus handle on the next Dispatch, silently: no LostFocus reached
/// the node or its surviving ancestors, so an inline rename editor freed after Enter kept the IME sink/context and the
/// touch keyboard, and focus-within chrome never heard the leave. An exit orphan stays LIVE, so it never lost focus at all:
/// a second Enter during its fade re-fired the removed row's click. The rig wires the reconciler's removal hook to the
/// dispatcher exactly as AppHost does.
/// </summary>
public sealed class RemovedFocusLeaveTests
{
    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly InputDispatcher Disp;
        public readonly List<string> Edges = [];
        public NodeHandle Field;
        public int Clicks;
        private Element? _last;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Recon = new TreeReconciler(Scene, new StringTable()) { Anim = new AnimEngine(Scene) };
            Disp = new InputDispatcher(Scene);
            Recon.OnSubtreeRemoved = Disp.NotifySubtreeRemoved;   // AppHost's wiring
        }

        /// <summary>A row with a focus-within handler holding either the editor arm or the read arm, keyed apart.</summary>
        public void Render(bool editing, bool exit = false)
        {
            var root = new BoxEl
            {
                Width = 200, Height = 40,
                OnFocusChanged = g => Edges.Add(g ? "row+" : "row-"),
                Children = editing
                    ?
                    [
                        new BoxEl
                        {
                            Key = "edit", Width = 120, Height = 24, OnClick = () => Clicks++,
                            OnFocusChanged = g => Edges.Add(g ? "field+" : "field-"),
                            Exit = exit ? new EnterExit(Opacity: 0f, Active: true) : null,
                            OnRealized = n => Field = n,
                        },
                    ]
                    : [new BoxEl { Key = "read", Width = 120, Height = 24 }],
            };
            Recon.ReconcileRoot(root, _last);
            Recon.Runtime.Flush();
            _last = root;
        }

        public void Key(int key) => Disp.Dispatch(new[]
        {
            new InputEvent(InputKind.Key, default, 0, key),
            new InputEvent(InputKind.KeyUp, default, 0, key),
        });
    }

    [Fact]
    public void FreeingTheFocusedNodeFiresLostFocusOnItAndItsSurvivingAncestor()
    {
        var r = new Rig();
        r.Render(editing: true);
        r.Disp.SetFocus(r.Field);
        Assert.Equal(new[] { "field+", "row+" }, r.Edges);
        r.Edges.Clear();

        r.Render(editing: false);   // the commit: the keyed read arm replaces the editor
        Assert.False(r.Scene.IsLive(r.Field));
        Assert.Equal(new[] { "field-", "row-" }, r.Edges);   // heard at removal, before any further input
        Assert.True(r.Disp.Focused.IsNull);
    }

    [Fact]
    public void AnExitOrphanLosesFocus_SoEnterDuringItsFadeDoesNotReFireItsClick()
    {
        var r = new Rig();
        r.Render(editing: true, exit: true);
        r.Disp.SetFocus(r.Field, visual: true);
        r.Key(Keys.Enter);
        Assert.Equal(1, r.Clicks);   // the premise: Enter clicks the focused button
        r.Edges.Clear();

        r.Render(editing: false);   // the click removed it: the node orphans and fades, still LIVE
        Assert.True(r.Scene.IsLive(r.Field));
        Assert.NotEqual(default(NodeFlags), r.Scene.Flags(r.Field) & NodeFlags.Exiting);
        Assert.Equal(new[] { "field-", "row-" }, r.Edges);
        Assert.True(r.Disp.Focused.IsNull);

        r.Key(Keys.Enter);
        Assert.Equal(1, r.Clicks);
    }
}
