using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A bound <c>Visible</c> collapse clears NodeFlags.Visible on the collapsed node only. Keyboard input reached its
/// descendants anyway: Wavee collapses the player-bar dock under full-screen video, and a dock button focused by a click
/// kept focus (Space still clicked it), Tab cycled through the hidden buttons, and their accelerators kept firing.
/// </summary>
public sealed class CollapsedFocusTests
{
    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly InputDispatcher Disp;
        public readonly Signal<bool> DockShown = new(true);
        public NodeHandle Live, Dock, Hidden;
        public int Clicks, Accels;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Recon = new TreeReconciler(Scene, new StringTable());
            Recon.ReconcileRoot(new BoxEl
            {
                Children =
                [
                    new BoxEl { OnClick = () => { }, OnRealized = n => Live = n },   // the video's own transport
                    new BoxEl
                    {
                        Visible = Prop.Of(() => DockShown.Value), OnRealized = n => Dock = n,   // the player-bar dock
                        Children =
                        [
                            new BoxEl { OnClick = () => Clicks++, OnRealized = n => Hidden = n },
                            new BoxEl { OnClick = () => Accels++, Accelerator = new KeyAccelerator(Keys.K, KeyModifiers.Ctrl), AccessKey = 'S' },
                        ],
                    },
                ],
            }, null);
            Recon.Runtime.Flush();
            Disp = new InputDispatcher(Scene);
        }

        public void Collapse()
        {
            DockShown.Value = false;
            Recon.Runtime.Flush();
        }

        public void Key(int key, KeyModifiers mods = KeyModifiers.None) => Disp.Dispatch(new[]
        {
            new InputEvent(InputKind.Key, default, 0, key, Mods: mods),
            new InputEvent(InputKind.KeyUp, default, 0, key, Mods: mods),
        });
    }

    [Fact]
    public void FocusInsideACollapsedSubtreeIsDropped_AndSpaceDoesNotClickIt()
    {
        var r = new Rig();
        r.Disp.SetFocus(r.Hidden);   // a pointer press focused it before the collapse
        r.Collapse();
        Assert.True(r.Scene.IsCollapsed(r.Dock));

        r.Key(Keys.Space);
        Assert.Equal(0, r.Clicks);
        Assert.True(r.Disp.Focused.IsNull);
        Assert.Equal(default(NodeFlags), r.Scene.Flags(r.Hidden) & NodeFlags.Focused);
    }

    [Fact]
    public void TabAndFocusQueriesSkipStopsUnderACollapsedAncestor()
    {
        var r = new Rig();
        r.Collapse();

        for (int i = 0; i < 3; i++)
        {
            r.Disp.MoveFocus(forward: true);
            Assert.Equal(r.Live, r.Disp.Focused);
        }
        Assert.True(r.Disp.FirstFocusableIn(r.Dock).IsNull);
    }

    [Fact]
    public void AcceleratorsAndAccessKeysUnderACollapsedAncestorDoNotFire()
    {
        var r = new Rig();
        r.Key(Keys.K, KeyModifiers.Ctrl);
        Assert.Equal(1, r.Accels);   // the rig's chord fires while the dock is shown

        r.Collapse();
        r.Key(Keys.K, KeyModifiers.Ctrl);
        Assert.Equal(1, r.Accels);
        Assert.True(r.Scene.FindAccelerator(Keys.K, KeyModifiers.Ctrl).IsNull);
        Assert.True(r.Scene.FindAccessKey('S').IsNull);

        r.DockShown.Value = true;   // the reveal brings the chord back
        r.Recon.Runtime.Flush();
        r.Key(Keys.K, KeyModifiers.Ctrl);
        Assert.Equal(2, r.Accels);
    }
}
