using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A Skel.Region whose content is a keyed ScrollView derives its shimmer from that same ScrollEl. The shimmer must not
/// inherit the viewport's identity: with the ScrollKey copied, the shimmer restored the remembered offset into its own
/// short extent and its unmount on the Ready swap saved the clamped value over the remembered one, so the real page
/// always reopened near the top. The authored Handle and OnRealized were handed to the shimmer node as well.
/// </summary>
public sealed class SkelShimmerScrollIdentityTests
{
    private const string Key = "album:1";

    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly List<string> Events = new();
        public readonly List<NodeHandle> Realized = new();
        public readonly ScrollHandle Handle = new();
        public readonly Signal<bool> Pending = new(true);
        public NodeHandle Box;

        public Harness()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Recon = new TreeReconciler(Scene, new StringTable());
            // The host's two scroll-memory hooks: a keyed restore at mount, a keyed save at unmount.
            Recon.ScrollKeyChanged = (_, _, k) => { if (k == Key) Events.Add("restore"); };
            Recon.SaveScrollPosition = n => { if (Scene.TryGetScroll(n, out var sc) && sc.ScrollKey == Key) Events.Add("save"); };
            Recon.ReconcileRoot(new BoxEl
            {
                Width = 400f, Height = 400f, OnRealized = n => Box = n,
                Children =
                [
                    new SkelRegionEl(
                        Pending: () => Pending.Value,
                        Failed: () => false,
                        Content: () => new ScrollEl
                        {
                            ScrollKey = Key, Handle = Handle, OnRealized = n => Realized.Add(n), Grow = 1f,
                            Content = new BoxEl { Height = 2000f, Children = [new BoxEl { Width = 40f, Height = 20f }] },
                        },
                        ShimmerSource: null, OnFailed: null,
                        Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default, Group: null, SmoothResize: false),
                ],
            }, null);
            Recon.Runtime.Flush();
        }

        public void SetPending(bool value)
        {
            Pending.Value = value;
            Recon.Runtime.Flush();
        }

        public NodeHandle RegionChild => Scene.FirstChild(Scene.FirstChild(Box));
    }

    [Fact]
    public void OnlyTheRealViewportRestoresAndSavesTheScrollKey()
    {
        var h = new Harness();
        Assert.Empty(h.Events);                        // the shimmer neither restores the key...

        h.SetPending(false);
        Assert.Equal(["restore"], h.Events);           // ...nor saves its clamped offset over it on the Ready swap

        h.SetPending(true);                            // a reload: the real viewport saves where the user was
        h.SetPending(false);
        Assert.Equal(["restore", "save", "restore"], h.Events);
    }

    [Fact]
    public void TheShimmerViewportGetsNeitherTheAuthoredHandleNorOnRealized()
    {
        var h = new Harness();
        var shimmer = h.RegionChild;
        Assert.True(h.Scene.HasScroll(shimmer));
        Assert.False(h.Scene.TryGetAuthoredScrollHandle((int)shimmer.Raw.Index, out _));
        Assert.Empty(h.Realized);

        h.SetPending(false);
        var real = h.RegionChild;
        Assert.True(h.Scene.TryGetAuthoredScrollHandle((int)real.Raw.Index, out var authored));
        Assert.Same(h.Handle, authored);
        Assert.Equal([real], h.Realized);
    }
}
