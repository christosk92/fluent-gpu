using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// An overlay SplitView pane pushes a focus scope when it opens and pops it only when it closes. Navigating away with the
/// pane open PARKS the page (KeepAlive: detached but live), the pane's watcher is parked with it, and the scope stayed the
/// innermost live one: Tab on the next page cycled through the hidden pane's buttons and nothing on the shown page was
/// reachable by keyboard. A scope now traps only while its root is attached; coming back resumes it.
/// </summary>
public sealed class ParkedFocusScopeTests
{
    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly InputDispatcher Disp;
        public readonly Signal<string> Page = new("detail");
        public NodeHandle Pane, PaneClose, HomeButton;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Recon = new TreeReconciler(Scene, new StringTable());
            Recon.ReconcileRoot(new BoxEl { Children = [Flow.KeepAlive(() => Page.Value, static k => k, PageOf)] }, null);
            Recon.Runtime.Flush();
            Disp = new InputDispatcher(Scene);
        }

        Element PageOf(string k) => k == "detail"
            ? new BoxEl
            {
                Children =
                [
                    new BoxEl { OnClick = () => { } },
                    new BoxEl   // the open overlay pane (Insights sheet)
                    {
                        OnRealized = n => Pane = n,
                        Children = [new BoxEl { OnClick = () => { }, OnRealized = n => PaneClose = n }],
                    },
                ],
            }
            : new BoxEl { Children = [new BoxEl { OnClick = () => { }, OnRealized = n => HomeButton = n }] };

        public void Go(string page)
        {
            Page.Value = page;
            Recon.Runtime.Flush();
        }

        public void Tab() => Disp.Dispatch(new[]
        {
            new InputEvent(InputKind.Key, default, 0, Keys.Tab),
            new InputEvent(InputKind.KeyUp, default, 0, Keys.Tab),
        });
    }

    [Fact]
    public void AParkedPanesScope_DoesNotTrapTabOnTheNextPage()
    {
        var r = new Rig();
        r.Disp.PushFocusScope(r.Pane);   // SplitViewPaneWatcher's light-dismiss open leg
        r.Disp.SetFocus(r.PaneClose);

        r.Go("home");                     // the detail page is parked with the pane still open
        Assert.True(r.Scene.IsLive(r.Pane));
        r.Disp.DeactivateSubtree(r.Pane); // what AppHost.OnSubtreeDeactivated does for the parked page

        r.Tab();
        Assert.Equal(r.HomeButton, r.Disp.Focused);   // before the fix: r.PaneClose (the hidden pane)
        r.Tab();
        Assert.Equal(r.HomeButton, r.Disp.Focused);   // the shown page cycles on its own stops
    }

    [Fact]
    public void ComingBackWithThePaneOpen_ResumesTheTrap()
    {
        var r = new Rig();
        r.Disp.PushFocusScope(r.Pane);
        r.Go("home");
        r.Go("detail");                   // re-attached, the pane is still open

        r.Tab();
        Assert.Equal(r.PaneClose, r.Disp.Focused);
        r.Tab();
        Assert.Equal(r.PaneClose, r.Disp.Focused);   // still trapped: the page's other button is outside the pane
    }
}
