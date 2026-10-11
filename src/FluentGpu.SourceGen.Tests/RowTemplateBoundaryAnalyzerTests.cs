using FluentGpu.SourceGen.Analyzers;
using Microsoft.CodeAnalysis;
using Xunit;

namespace FluentGpu.SourceGen.Tests;

/// <summary>FGTH003: a recycled row template (BoundRowTemplate, ItemsView.CreateBound, Virtual.List, VirtualListEl.RowBind /
/// RenderItem) must not build a component, slot, provider or boundary. The test source declares small stand-ins for the
/// <c>FluentGpu.Controls</c> shapes (this project references the engine only); everything else resolves against the real engine.</summary>
public sealed class RowTemplateBoundaryAnalyzerTests
{
    private const string Prelude = """
        using FluentGpu.Dsl; using FluentGpu.Hooks; using FluentGpu.Signals; using FluentGpu.Theming; using FluentGpu.Reconciler; using FluentGpu.Controls; using System;
        namespace FluentGpu.Controls
        {
            public readonly ref struct RowStyleContext { }
            public delegate Element BoundRowTemplate<TRow>(TRow row, in RowStyleContext ctx);
            public static class ItemsView
            {
                public static Element CreateBound<T>(T source, BoundRowTemplate<T> template, int layout) => new BoxEl();
                public static Element CreateBound<T>(T source, Func<T, Element> template, int layout) => new BoxEl();
            }
            public static class Virtual
            {
                public static VirtualListEl List(int count, float extent, Func<int, Element> row) => new VirtualListEl();
            }
        }
        sealed class Leaf : Component { public override Element Render() => new BoxEl(); }
        sealed record RowProps(int N);

        """;

    private static System.Collections.Immutable.ImmutableArray<Diagnostic> Run(string body)
        => Harness.Analyze(new RowTemplateBoundaryAnalyzer(), Prelude + body);

    private static int Count(string body) => Harness.Count(Run(body), "FGTH003");

    [Fact]
    public void Fires_On_Embed_Comp_In_A_BoundRowTemplate()
    {
        var diags = Run("""
            static class C {
                static Element Build() => ItemsView.CreateBound(1, (int row, in RowStyleContext ctx) => new BoxEl { Children = [Embed.Comp(() => new Leaf())] }, 0);
            }
            """);
        Assert.Equal(1, Harness.Count(diags, "FGTH003"));
        Assert.StartsWith("Embed.Comp", Harness.SpanText(diags[0]));
        Assert.Equal(DiagnosticSeverity.Info, diags[0].Severity);
    }

    [Fact]
    public void Fires_On_Embed_Comp_In_A_Legacy_Func_CreateBound_Row()
    {
        Assert.Equal(1, Count("""
            static class C {
                static Element Build() => ItemsView.CreateBound(1, row => Embed.Comp(() => new Leaf()), 0);
            }
            """));
    }

    [Fact]
    public void Fires_On_Each_Forbidden_Construct()
    {
        // Embed.Comp, Slots.View, Flow.Show, Ctx.Provide: four distinct constructs in one row.
        Assert.Equal(4, Count("""
            static class C {
                static readonly ViewKey<RowProps> Key = new("k@1");
                static readonly Context<int> Ch = new(0);
                static Element Build() => ItemsView.CreateBound(1, (int row, in RowStyleContext ctx) => new BoxEl { Children = [
                    Embed.Comp(() => new Leaf()),
                    Slots.View(Key, new RowProps(1)),
                    Flow.Show(() => true, new BoxEl()),
                    Ctx.Provide(Ch, 1, new BoxEl()),
                ] }, 0);
            }
            """));
    }

    [Fact]
    public void Fires_On_ViewContext_View_And_Boundary_Elements()
    {
        Assert.Equal(2, Count("""
            static class C {
                static readonly ViewKey<RowProps> Key = new("k@1");
                static Element Build() => ItemsView.CreateBound(1, (int row, in RowStyleContext ctx) => {
                    var vc = new ViewContext(null!, null!, null!, null, false);
                    return new BoxEl { Children = [vc.View(Key, new RowProps(1)), new ScrollEl { Content = new BoxEl() }] };
                }, 0);
            }
            """));
    }

    [Fact]
    public void Fires_In_A_Keyed_Virtual_List_Row()
    {
        Assert.Equal(1, Count("""
            static class C {
                static Element Build() => Virtual.List(10, 40f, i => Embed.Comp(() => new Leaf()));
            }
            """));
    }

    [Fact]
    public void Fires_In_VirtualListEl_RenderItem_And_RowBind()
    {
        Assert.Equal(2, Count("""
            static class C {
                static Element A() => new VirtualListEl { RenderItem = i => Embed.Comp(() => new Leaf()) };
                static Element B() => new VirtualListEl { RowBind = idx => Embed.Comp(() => new Leaf()) };
            }
            """));
    }

    [Fact]
    public void Fires_On_A_Block_Bodied_Template()
    {
        Assert.Equal(1, Count("""
            static class C {
                static Element Build() => ItemsView.CreateBound(1, (int row, in RowStyleContext ctx) => {
                    var leaf = Embed.Comp(() => new Leaf());
                    return new BoxEl { Children = [leaf] };
                }, 0);
            }
            """));
    }

    [Fact]
    public void Silent_On_A_Plain_Row()
    {
        Assert.Equal(0, Count("""
            static class C {
                static Element Build() => ItemsView.CreateBound(1, (int row, in RowStyleContext ctx) => new BoxEl { Height = 40f }, 0);
                static Element V() => Virtual.List(10, 40f, i => new BoxEl { Height = 40f });
            }
            """));
    }

    [Fact]
    public void Silent_Outside_A_Row_Template()
    {
        Assert.Equal(0, Count("""
            static class C {
                static Element Page() => new BoxEl { Children = [Embed.Comp(() => new Leaf()), Flow.Show(() => true, new BoxEl())] };
                static Element Other(Func<int, Element> f) => f(0);
                static Element Use() => Other(i => Embed.Comp(() => new Leaf()));   // a lambda that is not a row template
            }
            """));
    }

    [Fact]
    public void Silent_In_A_Nested_Builder_Lambda_Inside_A_Row()
    {
        // item.ShowWhen(pred, () => ...) style: the nested builder is the sanctioned way to mount a rare branch.
        Assert.Equal(0, Count("""
            static class C {
                static Element Rare(Func<Element> build) => new BoxEl();
                static Element Build() => ItemsView.CreateBound(1, (int row, in RowStyleContext ctx) => new BoxEl { Children = [Rare(() => Embed.Comp(() => new Leaf()))] }, 0);
            }
            """));
    }
}
