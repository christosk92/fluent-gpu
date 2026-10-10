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
/// A modal ContentDialog traps Tab and its scrim blocks the pointer, but accelerator and access-key lookup scanned the
/// whole tree: with a Wavee playlist dialog focused, Ctrl+T opened a tab, Alt+Left navigated and Alt+letter opened a
/// MenuBar menu on the page behind the dialog. A modal focus scope now scopes chord lookup as well.
/// </summary>
public sealed class ModalChordScopeTests
{
    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly InputDispatcher Disp;
        public NodeHandle Dialog, DialogButton, Menu;
        public int PageAccel, PageAccess, DialogAccel, MenuAccel;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Recon = new TreeReconciler(Scene, new StringTable());
            Recon.ReconcileRoot(new BoxEl
            {
                Children =
                [
                    new BoxEl   // the page behind the dialog: the shell's chord nodes and a MenuBar access key
                    {
                        Children =
                        [
                            new BoxEl { OnClick = () => PageAccel++, Accelerator = new KeyAccelerator(Keys.T, KeyModifiers.Ctrl) },
                            new BoxEl { OnClick = () => PageAccess++, AccessKey = 'F' },
                        ],
                    },
                    new BoxEl   // the modal dialog
                    {
                        OnRealized = n => Dialog = n,
                        Children =
                        [
                            new BoxEl { OnClick = () => { }, OnRealized = n => DialogButton = n },
                            new BoxEl { OnClick = () => DialogAccel++, Accelerator = new KeyAccelerator(Keys.S, KeyModifiers.Ctrl) },
                        ],
                    },
                    new BoxEl   // a light-dismiss menu opened from the dialog, stacked above it
                    {
                        OnRealized = n => Menu = n,
                        Children = [new BoxEl { OnClick = () => MenuAccel++, Accelerator = new KeyAccelerator(Keys.M, KeyModifiers.Ctrl) }],
                    },
                ],
            }, null);
            Recon.Runtime.Flush();
            Disp = new InputDispatcher(Scene);
        }

        public void Key(int key, KeyModifiers mods = KeyModifiers.None) => Disp.Dispatch(new[]
        {
            new InputEvent(InputKind.Key, default, 0, key, Mods: mods),
            new InputEvent(InputKind.KeyUp, default, 0, key, Mods: mods),
        });
    }

    [Fact]
    public void ModalScope_BlocksThePageBehindsChords_ButNotTheDialogsOwn()
    {
        var r = new Rig();
        r.Disp.PushModalFocusScope(r.Dialog);
        r.Disp.SetFocus(r.DialogButton);

        r.Key(Keys.T, KeyModifiers.Ctrl);
        Assert.Equal(0, r.PageAccel);
        r.Key(Keys.F, KeyModifiers.Alt);
        Assert.Equal(0, r.PageAccess);
        r.Key(Keys.Alt, KeyModifiers.Alt);   // bare Alt tap: access-key mode
        r.Key(Keys.F);
        Assert.Equal(0, r.PageAccess);

        r.Key(Keys.S, KeyModifiers.Ctrl);
        Assert.Equal(1, r.DialogAccel);   // the dialog's own chord still fires

        r.Disp.RemoveFocusScope(r.Dialog);   // the dialog closes: the page owns its chords again
        r.Key(Keys.T, KeyModifiers.Ctrl);
        Assert.Equal(1, r.PageAccel);
        r.Key(Keys.F, KeyModifiers.Alt);
        Assert.Equal(1, r.PageAccess);
    }

    [Fact]
    public void AnOverlayStackedAboveTheModal_KeepsItsChords()
    {
        var r = new Rig();
        r.Disp.PushModalFocusScope(r.Dialog);
        r.Disp.PushFocusScope(r.Menu);

        r.Key(Keys.M, KeyModifiers.Ctrl);
        Assert.Equal(1, r.MenuAccel);
        r.Key(Keys.T, KeyModifiers.Ctrl);
        Assert.Equal(0, r.PageAccel);
    }

    [Fact]
    public void ANonModalTrap_LeavesWindowChordsLive()
    {
        var r = new Rig();
        r.Disp.PushFocusScope(r.Dialog);   // a light-dismiss flyout's Tab trap
        r.Disp.SetFocus(r.DialogButton);

        r.Key(Keys.T, KeyModifiers.Ctrl);
        Assert.Equal(1, r.PageAccel);
        r.Key(Keys.F, KeyModifiers.Alt);
        Assert.Equal(1, r.PageAccess);
    }
}
