using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// P1 presence: a component is active (UseIsActive / UseInterval / UseActivation) only while NO node on its ancestor chain
/// is collapsed. Revealing one collapsed node must not wake a component that still sits under another collapsed node,
/// whether that node is nested below the revealed one or an ancestor above it.
/// </summary>
public sealed class PresenceNestingTests
{
    private sealed class ActiveProbe : Component
    {
        public IReadSignal<bool>? Active;

        public override Element Render()
        {
            Active = UseIsActive();
            return new BoxEl { Width = 10, Height = 10 };
        }
    }

    [Fact]
    public void RevealingASectionKeepsAComponentUnderAStillCollapsedChildInactive()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var recon = new TreeReconciler(new SceneStore(), new StringTable());
        var expanded = new Signal<bool>(true);
        ActiveProbe? indicator = null, row = null;
        recon.ReconcileRoot(new BoxEl
        {
            Children =
            [
                new BoxEl
                {
                    Visible = Prop.Of(() => expanded.Value),   // the expandable section
                    Children =
                    [
                        new BoxEl { Visible = false, Children = [Embed.Comp(() => indicator = new ActiveProbe())] },
                        Embed.Comp(() => row = new ActiveProbe()),
                    ],
                },
            ],
        }, null);

        Assert.False(indicator!.Active!.Peek());   // mounted under a collapsed box
        Assert.True(row!.Active!.Peek());

        expanded.Value = false;
        recon.Runtime.Flush();
        Assert.False(indicator.Active.Peek());
        Assert.False(row.Active.Peek());

        expanded.Value = true;
        recon.Runtime.Flush();
        Assert.True(row.Active.Peek());
        Assert.False(indicator.Active.Peek());   // its own box is still collapsed
    }

    [Fact]
    public void RevealingAChildUnderACollapsedSectionKeepsItsComponentInactiveUntilTheSectionReveals()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var recon = new TreeReconciler(new SceneStore(), new StringTable());
        var section = new Signal<bool>(false);
        var inner = new Signal<bool>(false);
        ActiveProbe? probe = null;
        recon.ReconcileRoot(new BoxEl
        {
            Children =
            [
                new BoxEl
                {
                    Visible = Prop.Of(() => section.Value),
                    Children =
                    [
                        new BoxEl
                        {
                            Visible = Prop.Of(() => inner.Value),
                            Children = [Embed.Comp(() => probe = new ActiveProbe())],
                        },
                    ],
                },
            ],
        }, null);
        Assert.False(probe!.Active!.Peek());

        inner.Value = true;
        recon.Runtime.Flush();
        Assert.False(probe.Active.Peek());   // the section above is still collapsed

        section.Value = true;
        recon.Runtime.Flush();
        Assert.True(probe.Active.Peek());

        section.Value = false;
        recon.Runtime.Flush();
        Assert.False(probe.Active.Peek());
    }
}
