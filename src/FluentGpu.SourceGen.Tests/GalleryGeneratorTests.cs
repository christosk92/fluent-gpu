using FluentGpu.SourceGen.Gallery;
using Xunit;

namespace FluentGpu.SourceGen.Tests;

/// <summary>Golden tests for the two WS7 gallery generators. Golden = assert on generated source-text FRAGMENTS
/// (resilient to formatting drift) + generator diagnostics. Generators run with a BCL-only reference set and declare
/// their own trigger types in source, so there is no clash with the real GalleryKit/engine assemblies.</summary>
public sealed class GalleryGeneratorTests
{
    // Minimal in-source shims for the GalleryKit / engine surface the generators key off.
    private const string Shim = """
        namespace FluentGpu.Dsl { public abstract class Element { } public sealed class BoxEl : Element { } }
        namespace FluentGpu.Hooks {
            public abstract class Component { }
            public sealed class ComponentEl : FluentGpu.Dsl.Element { }
            public static class Embed { public static ComponentEl Comp(System.Func<FluentGpu.Dsl.Element> f) => null!; }
        }
        namespace FluentGpu.GalleryKit {
            public enum ShotMode : byte { Deterministic, Animated, Skip }
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class GalleryPageAttribute : System.Attribute {
                public GalleryPageAttribute(string key, string title, string category) { }
                public string Icon { get; set; } = "";
                public string[] Keywords { get; set; } = System.Array.Empty<string>();
                public int Order { get; set; }
                public ShotMode ShotMode { get; set; }
                public bool Hidden { get; set; }
            }
            public sealed class GalleryPageInfo {
                public GalleryPageInfo(string key, string title, string category, System.Func<FluentGpu.Dsl.Element> create) { }
                public string Icon { get; set; } = "";
                public string[] Keywords { get; set; } = System.Array.Empty<string>();
                public int Order { get; set; }
                public ShotMode ShotMode { get; set; }
                public bool Hidden { get; set; }
            }
            public sealed class Knobs { public bool Toggle(string l) => false; }
            public sealed class Sample {
                public Sample(string title, string? description, string code, System.Func<Knobs, FluentGpu.Dsl.Element> factory) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class SampleAttribute : System.Attribute {
                public SampleAttribute(string title) { }
                public string? Description { get; set; }
            }
        }
        """;

    // ── GalleryRegistryGenerator ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void GalleryRegistry_Emits_Pages_And_Create()
    {
        var (gen, _) = Harness.Generate(new GalleryRegistryGenerator(), Shim + """
            namespace Demo {
                [FluentGpu.GalleryKit.GalleryPage("Button", "Button", "Basic input", Icon = "", Order = 5)]
                public sealed class ButtonPage : FluentGpu.Hooks.Component { }
            }
            """);

        Assert.Contains("class GalleryRegistry", gen);
        Assert.Contains("Pages", gen);
        Assert.Contains("Create(string key)", gen);
        Assert.Contains("\"Button\"", gen);                         // key + title
        Assert.Contains("\"Basic input\"", gen);                    // category
        Assert.Contains("GalleryPageInfo", gen);
        Assert.Contains("Embed.Comp", gen);                         // the mount factory
        Assert.Contains("new global::Demo.ButtonPage()", gen);
        Assert.Contains("Order = 5", gen);
    }

    [Fact]
    public void GalleryRegistry_FGG010_On_Duplicate_Key()
    {
        var (_, diags) = Harness.Generate(new GalleryRegistryGenerator(), Shim + """
            namespace Demo {
                [FluentGpu.GalleryKit.GalleryPage("dup", "One", "Cat")]
                public sealed class One : FluentGpu.Hooks.Component { }
                [FluentGpu.GalleryKit.GalleryPage("dup", "Two", "Cat")]
                public sealed class Two : FluentGpu.Hooks.Component { }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGG010");
    }

    [Fact]
    public void GalleryRegistry_FGG011_When_Not_A_Component()
    {
        var (_, diags) = Harness.Generate(new GalleryRegistryGenerator(), Shim + """
            namespace Demo {
                [FluentGpu.GalleryKit.GalleryPage("x", "X", "Cat")]
                public sealed class NotAPage { }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGG011");
    }

    [Fact]
    public void GalleryRegistry_FGG012_When_No_Parameterless_Ctor()
    {
        var (_, diags) = Harness.Generate(new GalleryRegistryGenerator(), Shim + """
            namespace Demo {
                [FluentGpu.GalleryKit.GalleryPage("y", "Y", "Cat")]
                public sealed class ArgOnly : FluentGpu.Hooks.Component { public ArgOnly(int n) { } }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGG012");
    }

    // ── SampleExtractorGenerator ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Sample_Emits_Constant_With_Verbatim_Body_And_Method_Group_Factory()
    {
        var (gen, diags) = Harness.Generate(new SampleExtractorGenerator(), Shim + """
            namespace Demo {
                public partial class Host {
                    [FluentGpu.GalleryKit.Sample("Standard", Description = "a toggle")]
                    public static FluentGpu.Dsl.Element Foo(FluentGpu.GalleryKit.Knobs k)
                    {
                        var on = k.Toggle("Enabled");
                        return new FluentGpu.Dsl.BoxEl();
                    }
                }
            }
            """);

        Assert.Empty(diags);
        Assert.Contains("partial class Host", gen);
        Assert.Contains("FooSample", gen);                          // static readonly Sample {Method}Sample
        Assert.Contains("new global::FluentGpu.GalleryKit.Sample(", gen);
        Assert.Contains("\"Standard\"", gen);                       // title
        Assert.Contains("\"a toggle\"", gen);                       // description
        Assert.Contains("k.Toggle(", gen);                          // VERBATIM body fragment (escaped in the code string)
        Assert.Contains("Enabled", gen);
        Assert.Contains(", Foo)", gen);                             // (Knobs) method → method-group factory
    }

    [Fact]
    public void Sample_Wraps_Parameterless_Method()
    {
        var (gen, diags) = Harness.Generate(new SampleExtractorGenerator(), Shim + """
            namespace Demo {
                public partial class Host {
                    [FluentGpu.GalleryKit.Sample("Bare")]
                    public static FluentGpu.Dsl.Element Bar() => new FluentGpu.Dsl.BoxEl();
                }
            }
            """);
        Assert.Empty(diags);
        Assert.Contains("BarSample", gen);
        Assert.Contains("static (global::FluentGpu.GalleryKit.Knobs _) => Bar()", gen);   // () method wraps
    }

    [Fact]
    public void Sample_FGG001_On_NonStatic_Method()
    {
        var (_, diags) = Harness.Generate(new SampleExtractorGenerator(), Shim + """
            namespace Demo {
                public partial class Host {
                    [FluentGpu.GalleryKit.Sample("X")]
                    public FluentGpu.Dsl.Element Foo(FluentGpu.GalleryKit.Knobs k) => new FluentGpu.Dsl.BoxEl();
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGG001");
    }

    [Fact]
    public void Sample_FGG002_On_Wrong_Return_Or_Params()
    {
        var (_, diags) = Harness.Generate(new SampleExtractorGenerator(), Shim + """
            namespace Demo {
                public partial class Host {
                    [FluentGpu.GalleryKit.Sample("X")]
                    public static int Foo(FluentGpu.GalleryKit.Knobs k) => 0;
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGG002");
    }

    [Fact]
    public void Sample_FGG003_On_NonPartial_Container()
    {
        var (_, diags) = Harness.Generate(new SampleExtractorGenerator(), Shim + """
            namespace Demo {
                public class Host {
                    [FluentGpu.GalleryKit.Sample("X")]
                    public static FluentGpu.Dsl.Element Foo(FluentGpu.GalleryKit.Knobs k) => new FluentGpu.Dsl.BoxEl();
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGG003");
    }
}
