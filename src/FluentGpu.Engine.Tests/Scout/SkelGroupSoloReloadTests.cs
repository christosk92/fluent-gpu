using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A grouped Skel.Region that reloaded ON ITS OWN (Ready → Pending → Ready while its siblings stayed Ready)
/// parked its reveal: the round waited for every registered member, so the real rows appeared with no entrance and the
/// stored reveal replayed on the settled rows when some sibling next loaded or unmounted. A round now waits only for the
/// members that are actually loading.</summary>
public sealed class SkelGroupSoloReloadTests
{
    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly AnimEngine Anim;
        public NodeHandle RootA, RootB;

        public Harness(Loadable<int> a, Loadable<int> b, object group)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Recon = new TreeReconciler(Scene, new StringTable());
            Anim = new AnimEngine(Scene) { RenderOwnsCompositor = true };
            Recon.Anim = Anim;
            Recon.ReconcileRoot(new BoxEl
            {
                Direction = 1, Width = 200f, Height = 200f,
                Children =
                [
                    Skel.Region(a, () => new BoxEl { Height = 20f }, v => new BoxEl { Height = 20f, OnRealized = n => RootA = n },
                                SkelReveal.FadeOnly, group: group),
                    Skel.Region(b, () => new BoxEl { Height = 20f }, v => new BoxEl { Height = 20f, OnRealized = n => RootB = n },
                                SkelReveal.FadeOnly, group: group),
                ],
            }, null);
            Recon.Runtime.Flush();
        }

        public bool Revealing(NodeHandle root) => Anim.TryGetTrackValue(root, AnimChannel.Opacity, out _);
    }

    [Fact]
    public void A_member_reloading_alone_reveals_at_once_and_never_replays_later()
    {
        var a = Loadable<int>.Pending(0);
        var b = Loadable<int>.Pending(0);
        var h = new Harness(a, b, new object());

        a.SetReady(1);
        b.SetReady(1);
        h.Recon.Runtime.Flush();
        Assert.True(h.Revealing(h.RootA));   // the first round: both members load, both reveal together
        Assert.True(h.Revealing(h.RootB));
        h.Anim.CancelAll(h.RootA);
        h.Anim.CancelAll(h.RootB);

        // A reloads while B stays Ready: nobody else is loading, so A's rows reveal on their own Ready edge.
        a.SetPending();
        h.Recon.Runtime.Flush();
        a.SetReady(2);
        h.Recon.Runtime.Flush();
        Assert.True(h.Revealing(h.RootA));
        h.Anim.CancelAll(h.RootA);

        // B reloads later: its round must not replay a reveal on A's long-settled rows.
        b.SetPending();
        h.Recon.Runtime.Flush();
        b.SetReady(2);
        h.Recon.Runtime.Flush();
        Assert.True(h.Revealing(h.RootB));
        Assert.False(h.Revealing(h.RootA));
    }

    [Fact]
    public void Members_reloading_together_still_reveal_together()
    {
        var a = Loadable<int>.Pending(0);
        var b = Loadable<int>.Pending(0);
        var h = new Harness(a, b, new object());
        a.SetReady(1);
        b.SetReady(1);
        h.Recon.Runtime.Flush();
        h.Anim.CancelAll(h.RootA);
        h.Anim.CancelAll(h.RootB);

        a.SetPending();
        b.SetPending();
        h.Recon.Runtime.Flush();
        a.SetReady(2);
        h.Recon.Runtime.Flush();
        Assert.False(h.Revealing(h.RootA));   // parked: B is still loading in the same round
        b.SetReady(2);
        h.Recon.Runtime.Flush();
        Assert.True(h.Revealing(h.RootA));
        Assert.True(h.Revealing(h.RootB));
    }
}
