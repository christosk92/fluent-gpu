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
/// Access-key mode has no visual, so pointer input must leave it: Alt+click is not a bare Alt tap, and a click after a
/// stray Alt tap ends the mode. Otherwise the next letter typed into a field opens a MenuBar menu instead.
/// </summary>
public sealed class AccessKeyPointerExitTests
{
    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly InputDispatcher Disp;
        public NodeHandle Field;
        public int MenuOpens, FieldKeys;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            new TreeReconciler(Scene, new StringTable()).ReconcileRoot(new BoxEl
            {
                Children =
                [
                    new BoxEl { AccessKey = 'F', OnClick = () => MenuOpens++ },   // MenuBar "File" title
                    new BoxEl { OnKeyDown = _ => FieldKeys++, OnRealized = n => Field = n },   // text field
                ],
            }, null);
            Disp = new InputDispatcher(Scene);
        }

        public void Key(int vk, KeyModifiers mods) =>
            Disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, vk, Mods: mods) });

        public void KeyUp(int vk) =>
            Disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, vk) });

        public void Click(KeyModifiers mods = KeyModifiers.None) => Disp.Dispatch(new[]
        {
            new InputEvent(InputKind.PointerDown, new Point2(5f, 5f), 0, 0, Mods: mods),
            new InputEvent(InputKind.PointerUp, new Point2(5f, 5f), 0, 0, Mods: mods),
        });
    }

    [Fact]
    public void AltClickDoesNotEnterAccessKeyMode()
    {
        var r = new Rig();
        r.Key(Keys.Alt, KeyModifiers.Alt);   // Alt down
        r.Click(KeyModifiers.Alt);           // Alt+click
        r.KeyUp(Keys.Alt);                   // Alt up: not a bare tap
        r.Disp.SetFocus(r.Field);            // the click landed in the text box
        r.Key('F', KeyModifiers.None);
        Assert.Equal(0, r.MenuOpens);
        Assert.Equal(1, r.FieldKeys);
    }

    [Fact]
    public void ClickAfterAltTapLeavesAccessKeyMode()
    {
        var r = new Rig();
        r.Key(Keys.Alt, KeyModifiers.Alt);
        r.KeyUp(Keys.Alt);                   // stray bare Alt tap: access-key mode on
        r.Click();                           // pointer input exits it
        r.Disp.SetFocus(r.Field);
        r.Key('F', KeyModifiers.None);
        Assert.Equal(0, r.MenuOpens);
        Assert.Equal(1, r.FieldKeys);
    }
}
