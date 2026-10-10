using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// <see cref="Flow"/>.For is layout-transparent: its rows flow on the enclosing container's axis with its Gap / Wrap /
/// AlignItems. Before the fix the For anchor kept <see cref="LayoutInput.Default"/> (a gap-less column), so a wrapping
/// HStack of bars rendered as one vertical column and a VStack(gap) lost the spacing between its rows.
/// </summary>
public sealed class ForParentFlowTests
{
    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        private readonly FlexLayout _layout;

        public Harness(Element root)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var strings = new StringTable();
            Recon = new TreeReconciler(Scene, strings);
            _layout = new FlexLayout(Scene, new HeadlessFontSystem(strings));
            Recon.ReconcileRoot(root, null);
            Solve();
        }

        public void Solve()
        {
            Recon.Runtime.Flush();
            _layout.Run(Scene.Root, new Size2(400f, 400f));
        }

        public NodeHandle Child(NodeHandle n, int i)
        {
            var c = Scene.FirstChild(n);
            for (; i > 0; i--) c = Scene.NextSibling(c);
            return c;
        }
    }

    private static IReadOnlyList<int> Upto(int n)
    {
        var xs = new List<int>(n);
        for (int i = 0; i < n; i++) xs.Add(i);
        return xs;
    }

    private static Element Bars(int n) => Flow.For(() => Upto(n), static i => i.ToString(), static (i, _) => new BoxEl { Width = 12f, Height = 36f });

    private sealed class ForRoot : Component
    {
        public override Element Render() => Bars(3);
    }

    private sealed class GapHost : Component
    {
        public Signal<float>? Gap;
        public override Element Render()
        {
            var gap = UseSignal(2f);
            Gap = gap;
            return new BoxEl { Direction = 1, Gap = gap.Value, Children = [Bars(3)] };
        }
    }

    [Fact]
    public void AWrappingRowLaysTheRowsSideBySideWithItsGapAndWraps()
    {
        var h = new Harness(new BoxEl { Direction = 0, Gap = 4f, Wrap = true, Width = 50f, Children = [Bars(5)] });
        var anchor = h.Scene.FirstChild(h.Scene.Root);
        var second = h.Scene.Bounds(h.Child(anchor, 1));
        var fourth = h.Scene.Bounds(h.Child(anchor, 3));
        Assert.Equal(16f, second.X);   // 12 + gap 4, on the row axis
        Assert.Equal(0f, second.Y);
        Assert.Equal(0f, fourth.X);    // 3 bars fit in 50 DIP: the 4th wraps to a new line
        Assert.Equal(40f, fourth.Y);   // 36 + gap 4
    }

    [Fact]
    public void AColumnsGapSeparatesTheRows()
    {
        var h = new Harness(new BoxEl { Direction = 1, Gap = 2f, Width = 50f, Children = [Bars(3)] });
        var anchor = h.Scene.FirstChild(h.Scene.Root);
        Assert.Equal(76f, h.Scene.Bounds(h.Child(anchor, 2)).Y);   // 2 × (36 + 2)
    }

    [Fact]
    public void AForRenderedAsAComponentRootFlowsWithTheContainerPastTheAnchor()
    {
        var h = new Harness(new BoxEl { Direction = 0, Gap = 6f, Children = [Embed.Comp(() => new ForRoot())] });
        var anchor = h.Scene.FirstChild(h.Scene.FirstChild(h.Scene.Root));   // root box → component anchor → For anchor
        var second = h.Scene.Bounds(h.Child(anchor, 1));
        Assert.Equal(18f, second.X);
        Assert.Equal(0f, second.Y);
    }

    [Fact]
    public void AParentReRenderThatChangesTheGapReachesTheRows()
    {
        var host = new GapHost();
        var h = new Harness(Embed.Comp(() => host));
        var anchor = h.Scene.FirstChild(h.Scene.FirstChild(h.Scene.Root));   // component anchor → box → For anchor
        Assert.Equal(38f, h.Scene.Bounds(h.Child(anchor, 1)).Y);

        host.Gap!.Value = 10f;
        h.Solve();
        Assert.Equal(46f, h.Scene.Bounds(h.Child(anchor, 1)).Y);
    }
}
