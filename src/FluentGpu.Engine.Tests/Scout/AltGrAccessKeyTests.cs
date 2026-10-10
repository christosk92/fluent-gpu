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
/// AltGr arrives from Win32 as Ctrl+Alt on the letter's WM_KEYDOWN. Access keys (Alt+letter mnemonics) must not claim
/// that chord: it is text for the focused field (euro sign, e-ogonek) or an app's Ctrl+Alt accelerator, never a menu.
/// </summary>
public sealed class AltGrAccessKeyTests
{
    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly InputDispatcher Disp;
        public NodeHandle Field;
        public int MenuOpens, FieldKeys, Accel;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            new TreeReconciler(Scene, new StringTable()).ReconcileRoot(new BoxEl
            {
                Children =
                [
                    new BoxEl { AccessKey = 'E', OnClick = () => MenuOpens++ },   // MenuBar "Edit" title
                    new BoxEl { Accelerator = new KeyAccelerator('E', KeyModifiers.Ctrl | KeyModifiers.Alt), OnClick = () => Accel++ },
                    new BoxEl { OnKeyDown = _ => FieldKeys++, OnRealized = n => Field = n },   // focused text field
                ],
            }, null);
            Disp = new InputDispatcher(Scene);
        }

        public void Key(int vk, KeyModifiers mods) =>
            Disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, vk, Mods: mods) });
    }

    [Fact]
    public void AltGrLetterReachesFocusedFieldNotAccessKey()
    {
        var r = new Rig();
        r.Disp.SetFocus(r.Field);
        r.Key(Keys.Ctrl, KeyModifiers.Ctrl);                          // AltGr's synthetic LCtrl
        r.Key(Keys.Alt, KeyModifiers.Ctrl | KeyModifiers.Alt);        // RAlt
        r.Key('E', KeyModifiers.Ctrl | KeyModifiers.Alt);             // AltGr+E
        Assert.Equal(0, r.MenuOpens);
        Assert.True(r.FieldKeys >= 1);
        Assert.Equal(r.Field, r.Disp.Focused);
    }

    [Fact]
    public void CtrlAltAcceleratorIsNotPreemptedByAccessKey()
    {
        var r = new Rig();
        r.Key('E', KeyModifiers.Ctrl | KeyModifiers.Alt);
        Assert.Equal(0, r.MenuOpens);
        Assert.Equal(1, r.Accel);
    }

    [Fact]
    public void PlainAltLetterStillInvokesAccessKey()
    {
        var r = new Rig();
        r.Disp.SetFocus(r.Field);
        r.Key(Keys.Alt, KeyModifiers.Alt);
        r.Key('E', KeyModifiers.Alt);
        Assert.Equal(1, r.MenuOpens);
        Assert.Equal(0, r.FieldKeys);
    }
}
