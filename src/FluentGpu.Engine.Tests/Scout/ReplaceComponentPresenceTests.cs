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
/// A component anchor whose embed swaps to a DIFFERENT component type keeps its node (ReplaceComponent) but must drop the
/// old embed's bound-Visible writer: otherwise the old source keeps collapsing/expanding the new component, and every
/// swap stacks one more presence BindEffect on the anchor until it unmounts.
/// </summary>
public sealed class ReplaceComponentPresenceTests
{
    private sealed class PanelA : Component
    {
        public override Element Render() => new BoxEl();
    }

    private sealed class PanelB : Component
    {
        public override Element Render() => new BoxEl();
    }

    [Fact]
    public void TypeSwapDropsTheOldEmbedsBoundVisibleWriter()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, new StringTable());
        var aVisible = new Signal<bool>(true);
        var a = Embed.Comp(() => new PanelA()) with { Visible = aVisible };
        recon.ReconcileRoot(a, null);
        NodeHandle anchor = scene.Root;
        Assert.True(aVisible.HasSubscribers);

        recon.ReconcileRoot(Embed.Comp(() => new PanelB()), a);   // same anchor, new type, static (default) Visible
        Assert.Equal(anchor, scene.Root);
        Assert.False(aVisible.HasSubscribers);                     // the old writer is gone
        Assert.Equal(0, recon.NodeBindingCount);                   // nothing left bound on the anchor

        aVisible.Value = false;                                    // the OLD source moves
        recon.Runtime.Flush();
        Assert.False(scene.IsCollapsed(anchor));                   // PanelB stays visible
    }

    [Fact]
    public void RepeatedTypeSwapsKeepOnlyTheCurrentEmbedsPresenceBinding()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, new StringTable());
        var sources = new[] { new Signal<bool>(true), new Signal<bool>(true), new Signal<bool>(true), new Signal<bool>(true) };
        ComponentEl? prev = null;
        for (int i = 0; i < sources.Length; i++)
        {
            ComponentEl next = (i % 2 == 0 ? Embed.Comp(() => new PanelA()) : Embed.Comp(() => new PanelB())) with { Visible = sources[i] };
            recon.ReconcileRoot(next, prev);
            prev = next;
            for (int j = 0; j < i; j++) Assert.False(sources[j].HasSubscribers, $"swap {i}: source {j} still bound");
            Assert.True(sources[i].HasSubscribers);
        }
        NodeHandle anchor = scene.Root;

        sources[2].Value = false;                                  // a superseded source: no effect
        recon.Runtime.Flush();
        Assert.False(scene.IsCollapsed(anchor));

        sources[3].Value = false;                                  // the current embed's source still drives presence
        recon.Runtime.Flush();
        Assert.True(scene.IsCollapsed(anchor));
    }
}
