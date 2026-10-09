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
/// A mouse press on a click-less OnDrag node (a TextBox — OnPointerPressed + OnDrag; a Slider with its thumb tooltip off —
/// OnPointerDown + OnDrag) captures the gesture, and its release is the drag node's alone. The release resolved its
/// activation owner by walking past the click-less node, so clicking into a TextBox, or scrubbing a Slider and releasing
/// anywhere inside the card, clicked the clickable card it sat in (and released the row's OnPointerReleased).
/// </summary>
public sealed class CapturedDragReleaseOwnerTests
{
    // [0] card clicks, [1] card releases, [2] child drags
    static (InputDispatcher Disp, int[] Counts) Mount(bool textBoxShape)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var strings = new StringTable();
        var scene = new SceneStore();
        var counts = new int[3];
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Width = 300, Height = 100,
            OnClick = () => counts[0]++,
            OnPointerReleased = _ => counts[1]++,
            Children =
            [
                textBoxShape
                    ? new BoxEl { Width = 120, Height = 40, OnPointerPressed = _ => { }, OnDrag = _ => counts[2]++ }
                    : new BoxEl { Width = 120, Height = 40, OnPointerDown = _ => { }, OnDrag = _ => counts[2]++ },
            ],
        }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        return (new InputDispatcher(scene), counts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AClickOnAClickLessDragChild_ActivatesNeitherTheCardNorItsRelease(bool textBoxShape)
    {
        var (disp, counts) = Mount(textBoxShape);
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(60, 20), 0, 0) });
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(60, 20), 0, 0) });
        Assert.Equal(0, counts[0]);
        Assert.Equal(0, counts[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AScrubReleasedOverTheCardsPadding_DoesNotActivateTheCard(bool textBoxShape)
    {
        var (disp, counts) = Mount(textBoxShape);
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(60, 20), 0, 0) });
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(250, 80), 0, 0) });
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(250, 80), 0, 0) });
        Assert.Equal(1, counts[2]);   // the press captured the child and the move drove it
        Assert.Equal(0, counts[0]);
        Assert.Equal(0, counts[1]);
    }

    [Fact]
    public void APressOnTheCardReleasedOverTheDragChild_StillClicksTheCard()
    {
        var (disp, counts) = Mount(textBoxShape: false);
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(250, 80), 0, 0) });
        disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(60, 20), 0, 0) });
        Assert.Equal(1, counts[0]);   // the card owned the press (WinUI: it captured the pointer)
    }
}
