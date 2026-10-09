using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A left press on a CanDrag row ARMS a drag that promotes only once the pointer leaves the 4px drag box. A per-pointer
/// PointerCancel (WM_POINTERLEAVE while held, capture loss, a lost WM_POINTERUP) must disarm it: Win32 follows the cancel
/// with an off-screen park move, which promoted the stale candidate into a drag with no button held, and the next click
/// then dropped it (OnDragCompleted for a reorder the user never made).
/// </summary>
public sealed class ArmedDragCancelTests
{
    [Fact]
    public void APerPointerCancel_DisarmsAnUnpromotedDrag_SoTheParkMoveAndTheNextClickStayPlain()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var strings = new StringTable();
        var scene = new SceneStore();
        int started = 0, completed = 0, canceled = 0, clicks = 0;
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Width = 200, Height = 60, CanDrag = true,
            OnClick = () => clicks++,
            OnDragStarted = _ => started++,
            OnDragCompleted = _ => completed++,
            OnDragCanceled = () => canceled++,
        }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        var disp = new InputDispatcher(scene);

        disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 30), 0, 0) });
        Assert.True(disp.Drag.IsArmed);

        // Win32's WM_POINTERLEAVE-while-held order (HeadlessWindow.QueuePointerLeaveWhileDown): cancel, then the park move.
        disp.Dispatch(new[]
        {
            new InputEvent(InputKind.PointerCancel, default, 0, 0),
            new InputEvent(InputKind.PointerMove, new Point2(-10000, -10000), 0, 0),
        });
        Assert.False(disp.Drag.IsArmed);
        Assert.False(disp.Drag.IsActive);

        // A hover move far from the old press point, then an ordinary click on the row: a new press arms its own candidate.
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(150, 30), 0, 0) });
        Assert.False(disp.Drag.IsActive);
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(150, 30), 0, 0) });
        Assert.True(disp.Drag.IsArmed);
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(150, 30), 0, 0) });

        Assert.Equal(0, started);
        Assert.Equal(0, completed);
        Assert.Equal(0, canceled);   // an armed-only candidate disarms silently
        Assert.Equal(1, clicks);
        Assert.False(disp.Drag.IsArmed);
        Assert.False(disp.Drag.IsActive);
    }
}
