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
/// A captured OnDrag gesture owner (FlipView / SwipeControl pan, Splitter grip, seek-bar scrub) whose contact dies with no
/// release — a per-pointer PointerCancel or a WindowBlur — learns it only through OnDragCanceled: its OnClick commit never
/// comes. The reconciler wrote that column only for CanDrag reorder sources, so the dispatcher's cancel found null and a
/// FlipView strip stayed frozen between two pages.
/// </summary>
public sealed class DragOwnerCancelTests
{
    static (InputDispatcher Disp, int[] Counts) MountAndDrag()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var strings = new StringTable();
        var scene = new SceneStore();
        var counts = new int[3];   // [0] drags, [1] clicks, [2] cancels
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Width = 200, Height = 60,
            OnDrag = _ => counts[0]++,
            OnClick = () => counts[1]++,
            OnDragCanceled = () => counts[2]++,
        }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        var disp = new InputDispatcher(scene);
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 30), 0, 0) });
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(140, 30), 0, 0) });
        Assert.Equal(1, counts[0]);   // the press captured the OnDrag owner and the move drove it
        return (disp, counts);
    }

    [Fact]
    public void APerPointerCancel_FiresTheCapturedDragOwnersOnDragCanceled_AndTheLateUpDoesNotClick()
    {
        var (disp, counts) = MountAndDrag();
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerCancel, default, 0, 0) });
        Assert.Equal(1, counts[2]);
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(140, 30), 0, 0) });
        Assert.Equal(0, counts[1]);   // capture loss is not a release: no commit
        Assert.Equal(1, counts[2]);
    }

    [Fact]
    public void AWindowBlur_FiresTheCapturedDragOwnersOnDragCanceled()
    {
        var (disp, counts) = MountAndDrag();
        disp.Dispatch(new[] { new InputEvent(InputKind.WindowBlur, default, 0, 0) });
        Assert.Equal(1, counts[2]);
        Assert.Equal(0, counts[1]);
    }
}
