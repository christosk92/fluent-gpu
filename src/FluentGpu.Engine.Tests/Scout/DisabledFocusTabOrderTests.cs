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
/// A button that disables itself while it works (IsEnabled bound to !Busy) keeps keyboard focus. The next Tab must
/// step to the control after it (WinUI), not restart at the first stop of the window/scope.
/// </summary>
public sealed class DisabledFocusTabOrderTests
{
    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly InputDispatcher Disp;
        public NodeHandle First, Save, Last, Only;

        public Rig(bool single = false)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            new TreeReconciler(Scene, new StringTable()).ReconcileRoot(single
                ? new BoxEl { Children = [new BoxEl { OnClick = () => { }, OnRealized = n => Only = n }] }
                : new BoxEl
                {
                    Children =
                    [
                        new BoxEl { OnClick = () => { }, OnRealized = n => First = n },   // title-bar back button
                        new BoxEl { OnClick = () => { }, OnRealized = n => Save = n },    // Save, disables itself
                        new BoxEl { OnClick = () => { }, OnRealized = n => Last = n },    // control after Save
                        new BoxEl { OnClick = () => { } },
                    ],
                }, null);
            Disp = new InputDispatcher(Scene);
        }

        public void Tab(KeyModifiers mods = KeyModifiers.None) =>
            Disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab, Mods: mods) });
    }

    [Fact]
    public void DisabledFocusedNodeTabsToNextControl()
    {
        var r = new Rig();
        r.Disp.SetFocus(r.Save, visual: true);
        r.Scene.Mark(r.Save, NodeFlags.Disabled);   // the reconciler's IsEnabled=false write
        r.Tab();
        Assert.Equal(r.Last, r.Disp.Focused);
        Assert.Equal(default(NodeFlags), r.Scene.Flags(r.Save) & (NodeFlags.Focused | NodeFlags.FocusVisual));
    }

    [Fact]
    public void DisabledFocusedNodeShiftTabsToPreviousControl()
    {
        var r = new Rig();
        r.Disp.SetFocus(r.Save, visual: true);
        r.Scene.Mark(r.Save, NodeFlags.Disabled);
        r.Tab(KeyModifiers.Shift);
        Assert.Equal(r.First, r.Disp.Focused);
    }

    [Fact]
    public void DisabledOnlyStopClearsFocusOnTab()
    {
        var r = new Rig(single: true);
        r.Disp.SetFocus(r.Only, visual: true);
        r.Scene.Mark(r.Only, NodeFlags.Disabled);
        r.Tab();
        Assert.True(r.Disp.Focused.IsNull);
        Assert.Equal(default(NodeFlags), r.Scene.Flags(r.Only) & (NodeFlags.Focused | NodeFlags.FocusVisual));
    }
}
