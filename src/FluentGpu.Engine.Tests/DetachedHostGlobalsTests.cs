using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A detached (pop-out) child host must neither publish nor clear the process-global, last-writer-wins seams the PRIMARY host
/// owns: <c>HostDispatch.Current</c> (MediaPlayer's UI marshal), the <c>InputHooks.Current.Default</c> mirrors (OpenUri,
/// Clipboard, drag / OS-drop), the <c>FrameClock</c> statics and the Mica <c>Theme.WindowBackground</c>. Before the fix the
/// child overwrote them at construction and nulled them on close, so after the first pop-out every later hyperlink, file drop
/// and MediaPlayer open ran with a dead seam; posts queued on the child at reap time never ran at all.
///
/// Driven through real headless hosts and the real reaper (<c>TickDetachedHosts</c>). Serial: every seam under test is
/// process-static, so a host built by a concurrently running class would change them under the assertions.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DetachedHostGlobalsTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    private sealed class Pair : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessWindow ParentWindow { get; }
        public HeadlessWindow ChildWindow { get; }
        public AppHost Parent { get; }
        public AppHost Child { get; }

        public Pair()
        {
            var strings = new StringTable();
            var device = new HeadlessGpuDevice();
            ParentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            ParentWindow.Show();
            Parent = new AppHost(App, ParentWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Parent.RunFrame();   // binds this thread as the UI thread (the reaper asserts it)

            ChildWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            ChildWindow.Show();
            Child = new AppHost(App, ChildWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot(),
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
            Parent.AdoptDetachedChild(Child);
        }

        /// <summary>Close the child window the way the OS does, then let the parent loop reap (dispose) it.</summary>
        public void CloseChild()
        {
            ChildWindow.IsClosed = true;
            Parent.TickDetachedHosts();
        }

        public void Dispose()
        {
            Parent.Dispose();   // disposes a still-adopted child
            App.Dispose();
        }
    }

    private sealed record Seams(
        object? Poster, object? OpenUri, object? Clipboard, object? GetDragState, object? DragEpoch,
        object? DragPosX, object? DragPosY, object? DragEnter, object? DragOver, object? DragLeave, object? Drop,
        object? DropFiles)
    {
        public static Seams Capture()
        {
            var d = InputHooks.Current.Default;
            return new Seams(HostDispatch.Current, d.OpenUri, d.Clipboard, d.GetDragState, d.DragEpoch, d.DragPosX, d.DragPosY,
                d.ExternalDragEnter, d.ExternalDragOver, d.ExternalDragLeave, d.ExternalDrop, d.ExternalDropFiles);
        }
    }

    private static void AssertSame(Seams before, Seams after)
    {
        Assert.Same(before.Poster, after.Poster);
        Assert.Same(before.OpenUri, after.OpenUri);
        Assert.Same(before.Clipboard, after.Clipboard);
        Assert.Same(before.GetDragState, after.GetDragState);
        Assert.Same(before.DragEpoch, after.DragEpoch);
        Assert.Same(before.DragPosX, after.DragPosX);
        Assert.Same(before.DragPosY, after.DragPosY);
        Assert.Same(before.DragEnter, after.DragEnter);
        Assert.Same(before.DragOver, after.DragOver);
        Assert.Same(before.DragLeave, after.DragLeave);
        Assert.Same(before.Drop, after.Drop);
        Assert.Same(before.DropFiles, after.DropFiles);
    }

    [Fact]
    public void OpeningAndClosingAChild_LeavesEveryProcessDefaultToThePrimaryHost()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var win = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        win.Show();
        using var parent = new AppHost(app, win, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new EmptyRoot());
        parent.RunFrame();
        var before = Seams.Capture();
        Assert.NotNull(before.Poster);
        Assert.NotNull(before.OpenUri);
        Assert.NotNull(before.DropFiles);

        var childWin = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
        childWin.Show();
        var child = new AppHost(app, childWin, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new EmptyRoot(),
            images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
        parent.AdoptDetachedChild(child);
        AssertSame(before, Seams.Capture());   // construction published nothing

        parent.RunFrame();
        parent.TickDetachedHosts();            // the child ticks a frame
        AssertSame(before, Seams.Capture());

        childWin.IsClosed = true;
        parent.TickDetachedHosts();            // reap: OnClosed -> Dispose (the identity-guarded clears)
        AssertSame(before, Seams.Capture());   // disposal cleared nothing
    }

    [Fact]
    public void AChildsFrames_DoNotRepublishTheFrameClockStatics()
    {
        using var p = new Pair();
        p.Parent.RunFrame();
        long frame = FluentGpu.Hooks.FrameClock.FrameQpc, present = FluentGpu.Hooks.FrameClock.PresentQpc;
        // A sentinel only the PRIMARY host's next RunFrame may overwrite.
        FluentGpu.Hooks.FrameClock.FrameQpc = frame + 12345;
        FluentGpu.Hooks.FrameClock.PresentQpc = present + 12345;
        try
        {
            p.Child.RunFrame();
            Assert.Equal(frame + 12345, FluentGpu.Hooks.FrameClock.FrameQpc);
            Assert.Equal(present + 12345, FluentGpu.Hooks.FrameClock.PresentQpc);
        }
        finally { FluentGpu.Hooks.FrameClock.FrameQpc = frame; FluentGpu.Hooks.FrameClock.PresentQpc = present; }
    }

    [Fact]
    public void PostsQueuedOnAChildAtReapTime_RunOnTheParent_AndLaterPostsForwardToo()
    {
        using var p = new Pair();
        int queued = 0, late = 0, fromCallback = 0;
        p.Child.OnClosed = () => p.Child.Post(() => fromCallback++);
        p.Child.Post(() => queued++);
        Assert.Equal(1, p.Child.PendingUiPostCount);

        p.CloseChild();
        Assert.Equal(0, p.Child.PendingUiPostCount);   // handed over, not stranded
        Assert.Equal(0, queued);                        // not run inline on the reaping call

        p.Child.Post(() => late++);                     // a worker still holding the dead child poster
        Assert.Equal(0, p.Child.PendingUiPostCount);

        p.Parent.RunFrame();
        Assert.Equal(1, queued);
        Assert.Equal(1, fromCallback);
        Assert.Equal(1, late);
    }

    [Fact]
    public void AChildsMicaInactiveSwap_IsHostLocal_AndNeverTouchesTheGlobalBackdrop()
    {
        var prior = Theme.WindowBackground;
        Theme.WindowBackground = ColorF.Transparent;   // a Mica app (FluentApp sets this before the first host)
        try
        {
            using var p = new Pair();
            p.Parent.RunFrame();
            int epoch = Tok.WindowBackgroundEpoch;
            Assert.True(p.Child.ClearForTest.A <= 0.004f);

            p.ChildWindow.IsActive = false;            // the pop-out loses focus; the main window stays active
            p.Child.RunFrame();

            Assert.Equal(epoch, Tok.WindowBackgroundEpoch);          // no global write, so no full repaint of the main window
            Assert.True(Theme.WindowBackground.A <= 0.004f);         // the main window keeps its live Mica
            Assert.True(p.Parent.ClearForTest.A <= 0.004f);
            Assert.True(p.Child.ClearForTest.A > 0.99f);             // the child alone shows the solid inactive fallback

            p.ChildWindow.IsActive = true;
            p.Child.RunFrame();
            Assert.True(p.Child.ClearForTest.A <= 0.004f);
            Assert.Equal(epoch, Tok.WindowBackgroundEpoch);
        }
        finally { Theme.WindowBackground = prior; }
    }
}
