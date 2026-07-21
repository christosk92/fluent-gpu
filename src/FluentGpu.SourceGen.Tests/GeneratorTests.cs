using System.Linq;
using FluentGpu.SourceGen.Engine;
using FluentGpu.SourceGen.Localization;
using FluentGpu.SourceGen.Routing;
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

    // ── RouteTableGenerator ──────────────────────────────────────────────────────────────────────────────────────
    // Minimal shims for the trigger surface the generator keys off (BCL-only refs; no engine clash).
    private const string RouteShim = """
        namespace FluentGpu.Hooks { public abstract class Component { } }
        namespace FluentGpu.Controls {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class RouteAttribute : System.Attribute {
                public RouteAttribute(string key) { }
                public string? Title { get; set; } public string? Icon { get; set; } public string? Category { get; set; }
                public int Order { get; set; } public bool KeepAlive { get; set; } public bool ShowInNav { get; set; }
            }
            public sealed record Route(string Name, string? Arg = null);
        }
        """;

    [Fact]
    public void RouteTableGenerator_Emits_RegisterAll_With_Metadata_And_Parameterless_Factory()
    {
        var (gen, diags) = Harness.Generate(new RouteTableGenerator(), RouteShim + """
            namespace Test {
                [FluentGpu.Controls.Route("home", Title = "Home", Icon = "H", Category = "Nav", Order = 3, KeepAlive = true)]
                public sealed class HomePage : FluentGpu.Hooks.Component { }
            }
            """);
        Assert.Empty(diags);                                                     // clean page → no FGRT diagnostics
        Assert.Contains("class Routes", gen);
        Assert.Contains("RegisterAll(global::FluentGpu.Controls.RouteRegistry r)", gen);
        Assert.Contains("new global::FluentGpu.Controls.RouteDef(\"home\"", gen);
        Assert.Contains("Embed.Comp(static () => new global::Test.HomePage())", gen);  // parameterless factory
        Assert.Contains("Title = \"Home\"", gen);
        Assert.Contains("Icon = \"H\"", gen);
        Assert.Contains("Category = \"Nav\"", gen);
        Assert.Contains("Order = 3", gen);
        Assert.Contains("KeepAlive = true", gen);
    }

    [Fact]
    public void RouteTableGenerator_String_Ctor_Threads_Route_Arg()
    {
        var (gen, _) = Harness.Generate(new RouteTableGenerator(), RouteShim + """
            namespace Test {
                [FluentGpu.Controls.Route("detail")]
                public sealed class DetailPage : FluentGpu.Hooks.Component { public DetailPage(string arg) { } }
            }
            """);
        Assert.Contains("new global::Test.DetailPage(route.Arg ?? \"\")", gen);
    }

    [Fact]
    public void RouteTableGenerator_Route_Ctor_Threads_Route()
    {
        var (gen, _) = Harness.Generate(new RouteTableGenerator(), RouteShim + """
            namespace Test {
                [FluentGpu.Controls.Route("detail")]
                public sealed class DetailPage : FluentGpu.Hooks.Component { public DetailPage(FluentGpu.Controls.Route r) { } }
            }
            """);
        Assert.Contains("new global::Test.DetailPage(route)", gen);
    }

    [Fact]
    public void RouteTableGenerator_FGRT001_On_Duplicate_Keys()
    {
        var (_, diags) = Harness.Generate(new RouteTableGenerator(), RouteShim + """
            namespace Test {
                [FluentGpu.Controls.Route("dup")] public sealed class A : FluentGpu.Hooks.Component { }
                [FluentGpu.Controls.Route("dup")] public sealed class B : FluentGpu.Hooks.Component { }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGRT001");
    }

    [Fact]
    public void RouteTableGenerator_FGRT002_On_No_Routable_Ctor()
    {
        var (_, diags) = Harness.Generate(new RouteTableGenerator(), RouteShim + """
            namespace Test {
                [FluentGpu.Controls.Route("bad")]
                public sealed class BadPage : FluentGpu.Hooks.Component { public BadPage(int x) { } }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGRT002");
    }

    [Fact]
    public void RouteTableGenerator_FGRT003_When_Not_A_Component()
    {
        var (_, diags) = Harness.Generate(new RouteTableGenerator(), RouteShim + """
            namespace Test {
                [FluentGpu.Controls.Route("x")]
                public sealed class NotAPage { }
            }
            """);
        Assert.Contains(diags, d => d.Id == "FGRT003");
    }

    // ── LocalizationKeysGenerator — the control-kit neutral-registration opt-in (G5j) ────────────────────────────
    private const string LocJson = "{\"dialog\":{\"ok\":\"OK\"},\"media\":{\"off\":\"Off\",\"captionsIndexed\":\"Captions {n}\"}}";

    [Fact]
    public void LocalizationKeysGenerator_Emits_Compile_Safe_Keys()
    {
        var (gen, _) = Harness.Generate(new LocalizationKeysGenerator(), "",
            ("assets/loc/en-US.json", LocJson));
        Assert.Contains("public const string Ok = \"dialog.ok\"", gen);           // plain key const == dotted key
        Assert.Contains("public const string Off = \"media.off\"", gen);
        Assert.Contains("CaptionsIndexedKey = \"media.captionsIndexed\"", gen);    // parameterized key gets the Key const
        Assert.Contains("CaptionsIndexed(object n)", gen);                        // + a typed format method
    }

    [Fact]
    public void LocalizationKeysGenerator_Emits_Neutral_Registration_When_Opted_In()
    {
        var (gen, _) = Harness.Generate(new LocalizationKeysGenerator(), "",
            new[] { ("build_property.FluentGpuLocRegisterNeutral", "true") },
            ("assets/loc/en-US.json", LocJson));
        Assert.Contains("ModuleInitializer", gen);                                // registers at module load
        Assert.Contains("RegisterNeutral", gen);
        Assert.Contains("t[\"dialog.ok\"] = \"OK\"", gen);                         // neutral value baked in
        Assert.Contains("t[\"media.off\"] = \"Off\"", gen);
    }

    [Fact]
    public void LocalizationKeysGenerator_No_Neutral_Registration_By_Default()
    {
        var (gen, _) = Harness.Generate(new LocalizationKeysGenerator(), "",
            ("assets/loc/en-US.json", LocJson));
        Assert.Contains("public const string Ok", gen);                           // keys still emitted
        Assert.DoesNotContain("ModuleInitializer", gen);                          // but NO neutral registration
        Assert.DoesNotContain("RegisterNeutral", gen);
    }
}
