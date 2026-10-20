using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A reused node whose <c>Prop&lt;T&gt;</c> channel flips between a static value and a bound (thunk/signal) form follows
/// the NEW form (reconciler-hooks.md §0bis, Reconciler.Rewire.cs): bound→static takes the static value and unwires the
/// channel's effect (no subscription left, a queued fire is dropped); static→bound wires the channel as mount does and
/// tracks the signal. Before the fix the new form silently lost — Wavee's playlist side column mounted with a static
/// Width 0, re-rendered with a Width bound to the insights toggle, and stayed at 0.
/// Each channel family is driven through the same scenarios by a small descriptor: <see cref="Fam.Static"/> builds the
/// element with a static encoded value k, <see cref="Fam.Bound"/> binds the channel to a signal carrying k, and
/// <see cref="Fam.Read"/> decodes k back off the scene.
/// </summary>
public sealed class BindFlipTests
{
    private static ColorF C(int k) => new(k / 255f, 0f, 0f, 1f);
    private static int Dc(ColorF c) => (int)MathF.Round(c.R * 255f);

    private sealed record Fam(
        string Name,
        Func<int, Element> Static,
        Func<Signal<int>, Element> Bound,
        Func<SceneStore, StringTable, NodeHandle, int> Read,
        Func<int, int> Norm);

    private static int Id(int k) => k;

    private static Fam Family(string name) => name switch
    {
        "Fill" => new(name,
            k => new BoxEl { Width = 10, Height = 10, Fill = C(k) },
            s => new BoxEl { Width = 10, Height = 10, Fill = Prop.Of(() => C(s.Value)) },
            (sc, _, n) => Dc(sc.Paint(n).Fill), Id),
        "HoverFill" => new(name,
            k => new BoxEl { Width = 10, Height = 10, HoverFill = C(k) },
            s => new BoxEl { Width = 10, Height = 10, HoverFill = Prop.Of(() => C(s.Value)) },
            (sc, _, n) => Dc(sc.Paint(n).HoverFill), Id),
        "BorderColor" => new(name,
            k => new BoxEl { Width = 10, Height = 10, BorderWidth = 1, BorderColor = C(k) },
            s => new BoxEl { Width = 10, Height = 10, BorderWidth = 1, BorderColor = Prop.Of(() => C(s.Value)) },
            (sc, _, n) => Dc(sc.Paint(n).BorderColor), Id),
        "Opacity" => new(name,
            k => new BoxEl { Width = 10, Height = 10, Opacity = k / 100f },
            s => new BoxEl { Width = 10, Height = 10, Opacity = Prop.Of(() => s.Value / 100f) },
            (sc, _, n) => (int)MathF.Round(sc.Paint(n).Opacity * 100f), Id),
        "Visible" => new(name,
            k => new BoxEl { Width = 10, Height = 10, Visible = (k & 1) == 1 },
            s => new BoxEl { Width = 10, Height = 10, Visible = Prop.Of(() => (s.Value & 1) == 1) },
            (sc, _, n) => sc.IsCollapsed(n) ? 0 : 1, k => k & 1),
        "Width" => new(name,
            k => new BoxEl { Height = 10, Width = k },
            s => new BoxEl { Height = 10, Width = Prop.Of(() => (float)s.Value) },
            (sc, _, n) => (int)sc.Layout(n).Width, Id),
        "Height" => new(name,
            k => new BoxEl { Width = 10, Height = k },
            s => new BoxEl { Width = 10, Height = Prop.Of(() => (float)s.Value) },
            (sc, _, n) => (int)sc.Layout(n).Height, Id),
        "TextColor" => new(name,
            k => new TextEl("x") { Color = C(k) },
            s => new TextEl("x") { Color = Prop.Of(() => C(s.Value)) },
            (sc, _, n) => Dc(sc.Paint(n).TextColor), Id),
        "Text" => new(name,
            k => new TextEl("t" + k) { Color = C(1) },
            s => new TextEl(Prop.Of(() => "t" + s.Value)) { Color = C(1) },
            (sc, st, n) => int.Parse(st.Resolve(sc.Paint(n).Text).AsSpan(1)), Id),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static TheoryData<string> Families => new()
    {
        "Fill", "HoverFill", "BorderColor", "Opacity", "Visible", "Width", "Height", "TextColor", "Text",
    };

    private static (SceneStore Scene, StringTable Strings, TreeReconciler Rec) NewRec()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var strings = new StringTable();
        return (scene, strings, new TreeReconciler(scene, strings));
    }

    private static int Read(Fam f, SceneStore s, StringTable st) => f.Read(s, st, s.Root);

    [Theory, MemberData(nameof(Families))]
    public void StaticToBoundWiresTheChannelAndFollowsTheSignal(string family)
    {
        var f = Family(family);
        var (scene, strings, rec) = NewRec();
        var sig = new Signal<int>(2);
        Element a = f.Static(1), b = f.Bound(sig);
        rec.ReconcileRoot(a, null);
        Assert.Equal(f.Norm(1), Read(f, scene, strings));
        Assert.Equal(0, rec.BindEffectCount(scene.Root));

        rec.ReconcileRoot(b, a);
        Assert.Equal(f.Norm(2), Read(f, scene, strings));   // the new effect ran once, inside the reconcile
        Assert.Equal(1, rec.BindEffectCount(scene.Root));
        Assert.Equal(1, sig.SubscriberCount);

        sig.Value = 5;
        rec.Runtime.Flush();
        Assert.Equal(f.Norm(5), Read(f, scene, strings));   // and keeps following the signal
    }

    [Theory, MemberData(nameof(Families))]
    public void BoundToStaticTakesTheStaticValueAndDropsTheSubscription(string family)
    {
        var f = Family(family);
        var (scene, strings, rec) = NewRec();
        var sig = new Signal<int>(2);
        Element a = f.Bound(sig), b = f.Static(5);
        rec.ReconcileRoot(a, null);
        Assert.Equal(f.Norm(2), Read(f, scene, strings));
        Assert.True(sig.HasSubscribers);

        rec.ReconcileRoot(b, a);
        Assert.Equal(f.Norm(5), Read(f, scene, strings));
        Assert.False(sig.HasSubscribers);
        Assert.Equal(0, rec.BindEffectCount(scene.Root));
        Assert.Equal(0, rec.NodeBindingCount);

        sig.Value = 8;                                        // the OLD source moves: nothing follows it
        rec.Runtime.Flush();
        Assert.Equal(f.Norm(5), Read(f, scene, strings));
    }

    [Theory, MemberData(nameof(Families))]
    public void FlipsBothWaysAndBackAgain(string family)
    {
        var f = Family(family);
        var (scene, strings, rec) = NewRec();
        var s1 = new Signal<int>(2);
        var s2 = new Signal<int>(6);
        Element e0 = f.Static(1), e1 = f.Bound(s1), e2 = f.Static(4), e3 = f.Bound(s2), e4 = f.Static(9);
        rec.ReconcileRoot(e0, null);
        rec.ReconcileRoot(e1, e0);
        Assert.Equal(f.Norm(2), Read(f, scene, strings));
        rec.ReconcileRoot(e2, e1);
        Assert.Equal(f.Norm(4), Read(f, scene, strings));
        Assert.False(s1.HasSubscribers);
        rec.ReconcileRoot(e3, e2);
        Assert.Equal(f.Norm(6), Read(f, scene, strings));
        s1.Value = 3; s2.Value = 7;
        rec.Runtime.Flush();
        Assert.Equal(f.Norm(7), Read(f, scene, strings));   // only the CURRENT source drives the channel
        Assert.Equal(1, rec.BindEffectCount(scene.Root));
        rec.ReconcileRoot(e4, e3);
        Assert.Equal(f.Norm(9), Read(f, scene, strings));
        Assert.False(s2.HasSubscribers);
        Assert.Equal(0, rec.TotalBindEffectCount);
    }

    [Theory, MemberData(nameof(Families))]
    public void RapidFlipFlopsNeverStackOrLeakAnEffect(string family)
    {
        var f = Family(family);
        var (scene, strings, rec) = NewRec();
        var sig = new Signal<int>(3);
        Element prev = f.Static(0);
        rec.ReconcileRoot(prev, null);
        for (int i = 1; i <= 60; i++)
        {
            bool bound = (i & 1) == 1;
            Element next = bound ? f.Bound(sig) : f.Static(i * 2);
            rec.ReconcileRoot(next, prev);
            prev = next;
            Assert.Equal(bound ? 1 : 0, rec.TotalBindEffectCount);
            Assert.Equal(bound ? 1 : 0, sig.SubscriberCount);
            Assert.Equal(f.Norm(bound ? sig.Peek() : i * 2), Read(f, scene, strings));
            if (bound) { sig.Value = sig.Peek() + 2; rec.Runtime.Flush(); Assert.Equal(f.Norm(sig.Peek()), Read(f, scene, strings)); }
        }
    }

    [Theory, MemberData(nameof(Families))]
    public void BoundToStaticInTheSameFrameAsAPendingFireDropsTheFire(string family)
    {
        var f = Family(family);
        var (scene, strings, rec) = NewRec();
        var sig = new Signal<int>(2);
        Element a = f.Bound(sig), b = f.Static(5);
        rec.ReconcileRoot(a, null);
        sig.Value = 9;                                        // queued for the next flush, not yet run
        rec.ReconcileRoot(b, a);                              // the flip disposes the queued effect
        rec.Runtime.Flush();
        Assert.Equal(f.Norm(5), Read(f, scene, strings));
        Assert.False(sig.HasSubscribers);
    }

    [Theory, MemberData(nameof(Families))]
    public void StaticToBoundInTheSameFrameAsASignalWriteReadsTheCurrentValue(string family)
    {
        var f = Family(family);
        var (scene, strings, rec) = NewRec();
        var sig = new Signal<int>(2);
        Element a = f.Static(1), b = f.Bound(sig);
        rec.ReconcileRoot(a, null);
        sig.Value = 4;                                        // written before the flip in the same frame
        rec.ReconcileRoot(b, a);
        rec.Runtime.Flush();
        Assert.Equal(f.Norm(4), Read(f, scene, strings));
        sig.Value = 6;
        rec.Runtime.Flush();
        Assert.Equal(f.Norm(6), Read(f, scene, strings));
    }

    [Theory, MemberData(nameof(Families))]
    public void AReusedKeyedChildInAListFlipsAndUnmountsClean(string family)
    {
        var f = Family(family);
        var (scene, strings, rec) = NewRec();
        var sig = new Signal<int>(2);
        Element Row(string key, Element el) => el with { Key = key };
        var l0 = new BoxEl { Children = [Row("a", f.Static(1)), Row("b", f.Static(3)), Row("c", f.Static(5))] };
        rec.ReconcileRoot(l0, null);
        NodeHandle bNode = scene.NextSibling(scene.FirstChild(scene.Root));

        // Reorder AND flip "b" to bound in one render: the keyed diff reuses b's node, which must now follow the signal.
        var l1 = new BoxEl { Children = [Row("b", f.Bound(sig)), Row("a", f.Static(1)), Row("c", f.Static(5))] };
        rec.ReconcileRoot(l1, l0);
        Assert.Equal(bNode, scene.FirstChild(scene.Root));
        Assert.Equal(f.Norm(2), f.Read(scene, strings, bNode));
        sig.Value = 7;
        rec.Runtime.Flush();
        Assert.Equal(f.Norm(7), f.Read(scene, strings, bNode));
        Assert.Equal(1, rec.TotalBindEffectCount);

        // Flip back while moving it to the end.
        var l2 = new BoxEl { Children = [Row("a", f.Static(1)), Row("c", f.Static(5)), Row("b", f.Static(4))] };
        rec.ReconcileRoot(l2, l1);
        Assert.Equal(f.Norm(4), f.Read(scene, strings, bNode));
        Assert.False(sig.HasSubscribers);

        // Bound again, then unmount the whole list: nothing stays subscribed or bound.
        var l3 = new BoxEl { Children = [Row("a", f.Static(1)), Row("c", f.Static(5)), Row("b", f.Bound(sig))] };
        rec.ReconcileRoot(l3, l2);
        Assert.True(sig.HasSubscribers);
        var l4 = new BoxEl { Children = [] };
        rec.ReconcileRoot(l4, l3);
        Assert.False(sig.HasSubscribers);
        Assert.Equal(0, rec.TotalBindEffectCount);
        Assert.Equal(0, rec.NodeBindingCount);
    }

    [Theory, MemberData(nameof(Families))]
    public void UnmountAfterAStaticToBoundFlipDisposesTheNewEffect(string family)
    {
        var f = Family(family);
        var (scene, _, rec) = NewRec();
        var sig = new Signal<int>(2);
        var p0 = new BoxEl { Children = [f.Static(1)] };
        var p1 = new BoxEl { Children = [f.Bound(sig)] };
        var p2 = new BoxEl { Children = [] };
        rec.ReconcileRoot(p0, null);
        rec.ReconcileRoot(p1, p0);
        Assert.Equal(1, sig.SubscriberCount);
        rec.ReconcileRoot(p2, p1);
        Assert.False(sig.HasSubscribers);
        Assert.Equal(0, rec.NodeBindingCount);
    }

    /// <summary>Unlinking from the middle, head and tail of a node's chain keeps the other channels bound.</summary>
    [Fact]
    public void UnwiringOneChannelKeepsTheNodesOtherBindings()
    {
        var (scene, _, rec) = NewRec();
        var op = new Signal<int>(50);
        var fill = new Signal<int>(2);
        var w = new Signal<int>(30);
        // Wiring order: Opacity (head), Fill (middle), Width (tail).
        BoxEl El(bool opB, bool fillB, bool wB) => new()
        {
            Height = 10,
            Opacity = opB ? Prop.Of(() => op.Value / 100f) : 0.25f,
            Fill = fillB ? Prop.Of(() => C(fill.Value)) : C(9),
            Width = wB ? Prop.Of(() => (float)w.Value) : 11f,
        };
        var all = El(true, true, true);
        rec.ReconcileRoot(all, null);
        Assert.Equal(3, rec.BindEffectCount(scene.Root));

        var noMiddle = El(true, false, true);
        rec.ReconcileRoot(noMiddle, all);
        Assert.Equal(2, rec.BindEffectCount(scene.Root));
        Assert.False(fill.HasSubscribers);
        Assert.Equal(9, Dc(scene.Paint(scene.Root).Fill));

        var noHead = El(false, false, true);
        rec.ReconcileRoot(noHead, noMiddle);
        Assert.Equal(1, rec.BindEffectCount(scene.Root));
        Assert.False(op.HasSubscribers);
        Assert.Equal(0.25f, scene.Paint(scene.Root).Opacity);

        w.Value = 44;                                         // the surviving tail still follows its signal
        rec.Runtime.Flush();
        Assert.Equal(44f, scene.Layout(scene.Root).Width);

        var back = El(true, true, false);                     // head + middle re-wired, tail unwired, in one render
        rec.ReconcileRoot(back, noHead);
        Assert.Equal(2, rec.BindEffectCount(scene.Root));
        Assert.False(w.HasSubscribers);
        Assert.Equal(11f, scene.Layout(scene.Root).Width);
        op.Value = 80; fill.Value = 12;
        rec.Runtime.Flush();
        Assert.Equal(0.8f, scene.Paint(scene.Root).Opacity, 3);
        Assert.Equal(12, Dc(scene.Paint(scene.Root).Fill));
    }

    /// <summary>The bound spans channel (SpanTextEl.Spans): wired on static→bound, unwired on bound→static.</summary>
    [Fact]
    public void SpansFlipBothWays()
    {
        var (scene, _, rec) = NewRec();
        var sig = new Signal<int>(1);
        TextSpans Spans(int k) => new TextSpan[] { new("s" + k) };
        string Shown()
        {
            Assert.True(scene.TryGetSpanText(scene.Root, out var spans));
            return spans[0].Text;
        }
        var a = new SpanTextEl(Spans(0));
        var b = new SpanTextEl(Prop.Of(() => Spans(sig.Value)));
        var c = new SpanTextEl(Spans(5));
        rec.ReconcileRoot(a, null);
        Assert.Equal("s0", Shown());

        rec.ReconcileRoot(b, a);
        Assert.Equal(1, rec.BindEffectCount(scene.Root));
        Assert.True(sig.HasSubscribers);
        Assert.Equal("s1", Shown());
        sig.Value = 2;
        rec.Runtime.Flush();
        Assert.Equal("s2", Shown());

        rec.ReconcileRoot(c, b);
        Assert.Equal(0, rec.BindEffectCount(scene.Root));
        Assert.False(sig.HasSubscribers);
        Assert.Equal("s5", Shown());
        sig.Value = 3;
        rec.Runtime.Flush();
        Assert.Equal("s5", Shown());
    }

    private sealed class Panel : Component
    {
        public override Element Render() => new BoxEl();
    }

    /// <summary>A reused component anchor's Visible (WriteAnchorColumns) flips both ways.</summary>
    [Fact]
    public void AnchorVisibleFlipsBothWays()
    {
        var (scene, _, rec) = NewRec();
        var vis = new Signal<bool>(false);
        ComponentEl a = Embed.Comp(() => new Panel()) with { Visible = true };
        ComponentEl b = Embed.Comp(() => new Panel()) with { Visible = vis };
        ComponentEl c = Embed.Comp(() => new Panel()) with { Visible = true };
        rec.ReconcileRoot(a, null);
        NodeHandle anchor = scene.Root;
        Assert.False(scene.IsCollapsed(anchor));

        rec.ReconcileRoot(b, a);
        Assert.Equal(anchor, scene.Root);
        Assert.True(scene.IsCollapsed(anchor));               // static→bound: follows the signal (false)
        vis.Value = true;
        rec.Runtime.Flush();
        Assert.False(scene.IsCollapsed(anchor));

        vis.Value = false;
        rec.Runtime.Flush();
        Assert.True(scene.IsCollapsed(anchor));
        rec.ReconcileRoot(c, b);                              // bound→static: takes the static true, drops the source
        Assert.False(scene.IsCollapsed(anchor));
        Assert.False(vis.HasSubscribers);
        Assert.Equal(0, rec.BindEffectCount(anchor));
        vis.Value = true; vis.Value = false;
        rec.Runtime.Flush();
        Assert.False(scene.IsCollapsed(anchor));
    }

    /// <summary>The unchanged path: a fresh-but-equal element (same signal) neither unwires nor re-wires anything.</summary>
    [Fact]
    public void AnEqualReRenderTouchesNoBinding()
    {
        var (scene, _, rec) = NewRec();
        var sig = new Signal<ColorF>(C(3));
        var a = new BoxEl { Width = 10, Height = 10, Fill = sig };
        var b = new BoxEl { Width = 10, Height = 10, Fill = sig };
        var c = new BoxEl { Width = 12, Height = 10, Fill = sig };   // changed static, same bound payload
        rec.ReconcileRoot(a, null);
        int unwires = rec.NodeBindingUnwireCount, wires = rec.NodeBindingWireCount;
        rec.ReconcileRoot(b, a);
        rec.ReconcileRoot(c, b);
        Assert.Equal(unwires, rec.NodeBindingUnwireCount);
        Assert.Equal(wires, rec.NodeBindingWireCount);
        Assert.Equal(1, rec.BindEffectCount(scene.Root));
        Assert.Equal(1, sig.SubscriberCount);
    }
}
