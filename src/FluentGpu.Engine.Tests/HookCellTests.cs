using System;
using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Hooks;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Hook cells are keyed by call site plus a per-render ordinal taken from a render epoch and a one-entry cursor.</summary>
public sealed class HookCellTests
{
    private static Ref<int> Hook(RenderContext c) => c.UseRef(0);

    [Fact]
    public void Same_line_loop_hooks_are_stable_and_distinct_across_renders()
    {
        var c = new RenderContext();
        var first = new Ref<int>[5];
        c.BeginRender();
        for (int i = 0; i < 5; i++) first[i] = Hook(c);
        for (int i = 0; i < 5; i++) for (int j = i + 1; j < 5; j++) Assert.NotSame(first[i], first[j]);
        c.BeginRender();
        for (int i = 0; i < 5; i++) Assert.Same(first[i], Hook(c));
    }

    [Fact]
    public void Loop_grows_and_shrinks_keeping_earlier_cells()
    {
        var c = new RenderContext();
        c.BeginRender();
        var a = new[] { Hook(c), Hook(c) };
        c.BeginRender();
        Assert.Same(a[0], Hook(c));
        c.BeginRender();
        var g = new[] { Hook(c), Hook(c), Hook(c) };
        Assert.Same(a[0], g[0]); Assert.Same(a[1], g[1]);
        c.BeginRender();
        Assert.Same(a[0], Hook(c));
        c.BeginRender();
        var again = new[] { Hook(c), Hook(c), Hook(c) };
        Assert.Same(g[2], again[2]);
    }

    private static Ref<int> Late(RenderContext c) => c.UseRef(1);
    private static Ref<string> Other(RenderContext c) => c.UseRef("x");

    [Fact]
    public void Conditional_skip_and_late_first_hook()
    {
        var c = new RenderContext();
        c.BeginRender();   // a render with no hooks at all
        c.BeginRender();
        var late = Late(c);
        var other = Other(c);
        c.BeginRender();
        Assert.Same(other, Other(c));   // the skipped hook keeps its cell for when the branch returns
        c.BeginRender();
        Assert.Same(late, Late(c));
        Assert.Same(other, Other(c));
    }

    [Fact]
    public void Two_contexts_do_not_share_a_cursor()
    {
        var parent = new RenderContext(); var child = new RenderContext();
        parent.BeginRender();
        var p0 = Hook(parent);
        child.BeginRender();
        var c0 = Hook(child);
        var p1 = Hook(parent);
        Assert.NotSame(p0, p1);
        parent.BeginRender(); child.BeginRender();
        Assert.Same(p0, Hook(parent)); Assert.Same(p1, Hook(parent)); Assert.Same(c0, Hook(child));
    }

    [Fact]
    public void Five_thousand_looped_hooks_resolve_in_linear_time()
    {
        var c = new RenderContext();
        c.BeginRender();
        for (int i = 0; i < 5000; i++) Hook(c);
        c.BeginRender();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5000; i++) Hook(c);
        // Quadratic probing would be 12.5M dictionary probes (~hundreds of ms); linear is a few ms.
        Assert.True(sw.ElapsedMilliseconds < 250, sw.ElapsedMilliseconds + " ms");
    }
}
