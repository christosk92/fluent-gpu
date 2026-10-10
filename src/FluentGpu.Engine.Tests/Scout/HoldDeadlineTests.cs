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
/// The touch long-press (arena Hold → context flyout) fires only once its ~500 ms timer elapses. A Hold that was the
/// arena's lone or top live member (a context-only box, a cursor-only child of a click+context card) used to win by
/// DEFAULT — last-standing on the first sub-slop jitter move, the up-sweep on a quick tap, the force-close on a pointer
/// cancel — and a Hold win IS the fire, so the menu opened the moment the finger landed, on a plain tap (alongside the
/// card's click), or on capture loss.
/// </summary>
public sealed class HoldDeadlineTests
{
    private const uint Finger = 7;

    private static InputEvent Touch(InputKind kind, float x, float y, uint ms)
        => new(kind, new Point2(x, y), 0, 0, KeyModifiers.None, PointerKind.Touch, false, ms, Finger, 1f);

    private static InputDispatcher Mount(Element root)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var strings = new StringTable();
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(root, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        return new InputDispatcher(scene);
    }

    [Fact]
    public void AJitterMove_DoesNotOpenTheContextBeforeTheLongPressTimer()
    {
        int contexts = 0;
        var trigger = ContextRequestTrigger.Pointer;
        var disp = Mount(new BoxEl { Width = 200, Height = 100, OnContextRequested = a => { contexts++; trigger = a.Trigger; } });

        disp.Dispatch(new[] { Touch(InputKind.PointerDown, 50, 50, 1000) });
        disp.Dispatch(new[] { Touch(InputKind.PointerMove, 51, 50, 1016) });   // sub-slop digitizer jitter
        Assert.Equal(0, contexts);

        disp.TickGestureArenas(600f);   // the finger stays down past the ~500 ms long-press window
        Assert.Equal(1, contexts);
        Assert.Equal(ContextRequestTrigger.Hold, trigger);

        disp.Dispatch(new[] { Touch(InputKind.PointerUp, 51, 50, 1700) });
        Assert.Equal(1, contexts);
    }

    [Fact]
    public void AQuickTapOrACancel_OnAContextOnlyBox_NeverOpensTheContext()
    {
        int contexts = 0;
        var disp = Mount(new BoxEl { Width = 200, Height = 100, OnContextRequested = _ => contexts++ });

        disp.Dispatch(new[] { Touch(InputKind.PointerDown, 50, 50, 1000) });
        disp.Dispatch(new[] { Touch(InputKind.PointerUp, 50, 50, 1048) });
        disp.TickGestureArenas(600f);   // a lifted finger leaves no armed Hold behind to fire later
        Assert.Equal(0, contexts);

        disp.Dispatch(new[] { Touch(InputKind.PointerDown, 50, 50, 3000) });
        disp.Dispatch(new[] { Touch(InputKind.PointerCancel, 50, 50, 3048) });   // capture loss before the timer
        disp.TickGestureArenas(600f);
        Assert.Equal(0, contexts);
    }

    [Fact]
    public void ATapOnANonClickableChildOfAClickContextCard_ClicksOnly()
    {
        int contexts = 0, clicks = 0;
        var disp = Mount(new BoxEl
        {
            Width = 200, Height = 100,
            OnClick = () => clicks++,
            OnContextRequested = _ => contexts++,
            Children = [new BoxEl { Width = 80, Height = 40, Cursor = CursorId.Hand }],
        });

        disp.Dispatch(new[] { Touch(InputKind.PointerDown, 20, 20, 1000) });   // hits the cursor-only child
        disp.Dispatch(new[] { Touch(InputKind.PointerUp, 20, 20, 1048) });
        disp.TickGestureArenas(600f);

        Assert.Equal(1, clicks);
        Assert.Equal(0, contexts);
    }
}
