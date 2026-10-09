using System.Runtime.CompilerServices;
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
/// Unmounting a component must not scan every other live component. The reconciler used to keep a mount-ordered
/// <c>List&lt;Component&gt;</c> and <c>List.Remove</c> each unmounted one, an Equals call per live component ahead of it,
/// so tearing down a page of M components behind N live ones cost O(N·M) on the UI thread (Wavee: KeepAlive pages +
/// shell ahead of a transient page). Counting Equals calls makes the scan observable without timing.
/// </summary>
public sealed class LiveComponentsUnmountCostTests
{
    private const int Count = 400;

    private sealed class Calls { public long Equals; }

    private sealed class Leaf(Calls calls) : Component
    {
        public override Element Render() => new BoxEl();
        public override bool Equals(object? obj) { calls.Equals++; return ReferenceEquals(this, obj); }
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }

    private sealed class Rows(Calls calls, Signal<bool>? shown) : Component
    {
        public override Element Render()
        {
            if (shown is not null && !shown.Value) return new BoxEl();
            var kids = new Element[Count];
            for (int i = 0; i < Count; i++) kids[i] = Embed.Comp(() => new Leaf(calls));
            return new BoxEl { Children = kids };
        }
    }

    [Fact]
    public void UnmountingAPageDoesNotScanTheComponentsMountedBeforeIt()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, new StringTable());
        var calls = new Calls();
        var shown = new Signal<bool>(true);
        recon.ReconcileRoot(new BoxEl
        {
            Children = [Embed.Comp(() => new Rows(calls, null)), Embed.Comp(() => new Rows(calls, shown))],
        }, null);
        Assert.Equal(2 * Count + 2, recon.ComponentCount);
        Assert.Equal(2 * Count + 2, recon.LiveComponents.Count);

        calls.Equals = 0;
        shown.Value = false;                     // the second page's 400 leaves unmount behind the first page's 400
        recon.Runtime.Flush();

        Assert.Equal(Count + 2, recon.ComponentCount);
        Assert.Equal(Count + 2, recon.LiveComponents.Count);
        Assert.True(calls.Equals < 4 * Count, $"unmount made {calls.Equals} Equals calls for {Count} components");
    }
}
