using System.Linq;
using FluentGpu.SourceGen.Engine;
using Xunit;

namespace FluentGpu.SourceGen.Tests;

/// <summary>Golden tests for the engine generators. Golden = assert on generated source-text FRAGMENTS (resilient to
/// formatting drift), not full-file snapshots. Generators run with a BCL-only reference set and declare their own
/// trigger types in source, so there is no clash with the real engine assembly.</summary>
public sealed class GeneratorTests
{
    // Minimal in-source shims for the [Props]/[Prop]/Component surface the generator keys off.
    private const string PropsShim = """
        namespace FluentGpu.Hooks {
            public sealed class PropsAttribute : System.Attribute { }
            public sealed class PropAttribute : System.Attribute { }
            public abstract class Component { }
        }
        """;

    // ── PropsGenerator ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void PropsGenerator_Emits_Signal_Getter_Accessor_ApplyProps_Of_PropsData()
    {
        var (gen, _) = Harness.Generate(new PropsGenerator(), PropsShim + """
            namespace Test {
                [FluentGpu.Hooks.Props]
                public partial class Widget : FluentGpu.Hooks.Component {
                    [FluentGpu.Hooks.Prop] public partial int Count { get; }
                }
            }
            """);

        Assert.Contains("_countProp", gen);                       // per-field signal backing
        Assert.Contains("Signal<int>", gen);
        Assert.Contains("partial int Count", gen);                // the implemented partial getter
        Assert.Contains("_countProp.Value", gen);                 //   subscribing read
        Assert.Contains("CountProp", gen);                        // IReadSignal<int> bind accessor
        Assert.Contains("ApplyProps", gen);                       // the IPropsHost sink
        Assert.Contains("record PropsData(", gen);                // the transport record
        Assert.Contains("ComponentEl Of(", gen);                  // the Of(...) embed factory
    }

    [Fact]
    public void PropsGenerator_Delegate_Prop_Uses_Stable_Forwarder()
    {
        var (gen, _) = Harness.Generate(new PropsGenerator(), PropsShim + """
            namespace Test {
                [FluentGpu.Hooks.Props]
                public partial class Widget : FluentGpu.Hooks.Component {
                    [FluentGpu.Hooks.Prop] public partial System.Action OnTap { get; }
                }
            }
            """);
        Assert.Contains("_onTapForwarder", gen);   // stable forwarder for a delegate prop
        Assert.Contains("_onTapLatest", gen);      // latest-write slot
    }

    [Fact]
    public void PropsGenerator_FGSG001_On_NonPartial_Or_Settable_Prop()
    {
        var (_, diags) = Harness.Generate(new PropsGenerator(), PropsShim + """
            namespace Test {
                [FluentGpu.Hooks.Props]
                public partial class Widget : FluentGpu.Hooks.Component {
                    [FluentGpu.Hooks.Prop] public int Count { get; set; }
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGSG001");
    }

    [Fact]
    public void PropsGenerator_FGSG002_On_NonPartial_Class()
    {
        var (_, diags) = Harness.Generate(new PropsGenerator(), PropsShim + """
            namespace Test {
                [FluentGpu.Hooks.Props]
                public class Widget : FluentGpu.Hooks.Component {
                    [FluentGpu.Hooks.Prop] public partial int Count { get; }
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGSG002");
    }

    [Fact]
    public void PropsGenerator_FGSG003_When_Not_A_Component()
    {
        var (_, diags) = Harness.Generate(new PropsGenerator(), PropsShim + """
            namespace Test {
                [FluentGpu.Hooks.Props]
                public partial class Widget {
                    [FluentGpu.Hooks.Prop] public partial int Count { get; }
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGSG003");
    }

    [Fact]
    public void PropsGenerator_FGSG005_On_Collection_Prop()
    {
        var (_, diags) = Harness.Generate(new PropsGenerator(), PropsShim + """
            namespace Test {
                [FluentGpu.Hooks.Props]
                public partial class Widget : FluentGpu.Hooks.Component {
                    [FluentGpu.Hooks.Prop] public partial System.Collections.Generic.List<int> Items { get; }
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGSG005");
    }

    // ── TokAccessorGenerator ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void TokAccessorGenerator_Emits_Forwarding_Getter_For_TokenSet_Field()
    {
        var (gen, _) = Harness.Generate(new TokAccessorGenerator(), """
            namespace FluentGpu.Dsl {
                public sealed record TokenSet { public int Foo { get; init; } }
                public static partial class Tok { public static TokenSet T => default!; }
            }
            """);
        Assert.Contains("Foo => T.Foo", gen);
    }

    // ── GlyphTableGenerator ──────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void GlyphTableGenerator_Emits_Const_Per_Glyph()
    {
        var (gen, _) = Harness.Generate(new GlyphTableGenerator(), "",
            ("glyphs.json", "{\"Play\":\"E768\",\"Pause\":\"E769\"}"));
        Assert.Contains("public const string Play", gen);
        Assert.Contains("E768", gen);
        Assert.Contains("public const string Pause", gen);
    }

    [Fact]
    public void GlyphTableGenerator_FGGLYPH002_On_Bad_Codepoint()
    {
        var (_, diags) = Harness.Generate(new GlyphTableGenerator(), "",
            ("glyphs.json", "{\"Play\":\"NOPE\"}"));
        Assert.Contains(diags, d => d.Id == "FGGLYPH002");
    }
}
