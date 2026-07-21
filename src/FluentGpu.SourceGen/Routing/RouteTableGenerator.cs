using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.SourceGen.Routing
{
    /// <summary>
    /// Routing generator — <c>RouteTableGenerator</c> (master plan §WS3 P8). Collects every
    /// <c>[FluentGpu.Controls.Route("key")] class X : Component</c> in the assembly and emits ONE
    /// <c>FluentGpu.Generated.Routes.RegisterAll(RouteRegistry)</c> that adds a <c>RouteDef</c> per page — compile-time,
    /// zero reflection (AOT-clean). Dormant unless a <c>[Route]</c> is present (ForAttributeWithMetadataName). It kills
    /// the hand-synced page switch: the compile IS the sync check.
    ///
    /// <para>Factory shape by constructor: a parameterless ctor →
    /// <c>_ =&gt; Embed.Comp(() =&gt; new X())</c>; a <c>(Route)</c> ctor → <c>route =&gt; Embed.Comp(() =&gt; new X(route))</c>;
    /// a <c>(string)</c> ctor → <c>route =&gt; Embed.Comp(() =&gt; new X(route.Arg ?? ""))</c>. Route metadata
    /// (Title/Icon/Category/Order/KeepAlive/ShowInNav) that the attribute specifies is copied onto the <c>RouteDef</c>;
    /// unspecified fields keep the record's defaults.</para>
    ///
    /// <para>Diagnostics: <c>FGRT001</c> (duplicate key across the compilation) — Error; <c>FGRT002</c> (no
    /// parameterless / <c>(Route)</c> / <c>(string)</c> ctor) — Error; <c>FGRT003</c> (not a <c>Component</c>) — Error.</para>
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class RouteTableGenerator : IIncrementalGenerator
    {
        private const string RouteAttr = "FluentGpu.Controls.RouteAttribute";
        private const string ComponentBase = "FluentGpu.Hooks.Component";
        private const string RouteType = "FluentGpu.Controls.Route";

        // ── diagnostics (FGRT family) ────────────────────────────────────────────────────────────────────────────
        private static readonly DiagnosticDescriptor DuplicateKey = new(
            "FGRT001", "duplicate [Route] key",
            "duplicate [Route] key '{0}' — route keys must be unique across the assembly (a collision would shadow a page). "
            + "Rename one of the pages' keys.",
            "FluentGpu.Routing", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor NoCtor = new(
            "FGRT002", "[Route] page needs a routable constructor",
            "[Route] page '{0}' has no routable constructor — declare a parameterless ctor, a '(Route)' ctor, or a "
            + "'(string)' ctor so the generated factory can build it.",
            "FluentGpu.Routing", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor NotComponent = new(
            "FGRT003", "[Route] class must derive Component",
            "[Route] class '{0}' must derive FluentGpu.Hooks.Component (a route resolves to a page component).",
            "FluentGpu.Routing", DiagnosticSeverity.Error, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(
                    RouteAttr,
                    predicate: static (node, _) => node is ClassDeclarationSyntax,
                    transform: static (ctx, ct) => Extract(ctx, ct))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);

            // Per-model diagnostics (FGRT002/003) — reported at each page's location.
            context.RegisterSourceOutput(models, static (spc, model) =>
            {
                foreach (Diag d in model.Diagnostics)
                    spc.ReportDiagnostic(Diagnostic.Create(DescriptorFor(d.Id), d.Location, d.Arg0));
            });

            // Assembly-wide: one Routes.RegisterAll + the FGRT001 duplicate-key sweep.
            var collected = models.Collect();
            context.RegisterSourceOutput(collected, static (spc, all) =>
            {
                var valid = all.Where(static m => m.Emit).ToImmutableArray();
                if (valid.Length == 0) return;

                // Duplicate-key detection (first registration wins; the rest get FGRT001 + are skipped from emission).
                var seen = new Dictionary<string, RouteModel>();
                var emit = ImmutableArray.CreateBuilder<RouteModel>();
                foreach (RouteModel m in valid)
                {
                    if (seen.ContainsKey(m.Key))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(DuplicateKey, m.KeyLocation, m.Key));
                        continue;
                    }
                    seen.Add(m.Key, m);
                    emit.Add(m);
                }

                spc.AddSource("Routes.g.cs", SourceText.From(Emit(emit.ToImmutable()), Encoding.UTF8));
            });
        }

        private static DiagnosticDescriptor DescriptorFor(string id) => id switch
        {
            "FGRT002" => NoCtor,
            _ => NotComponent,
        };

        private enum Ctor { None, Parameterless, RouteArg, StringArg }

        private static RouteModel? Extract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

            var diags = ImmutableArray.CreateBuilder<Diag>();
            Location typeLoc = type.Locations.FirstOrDefault() ?? Location.None;

            // The [Route] attribute data (ctor arg 0 = key; named args carry the metadata + presence).
            AttributeData attr = ctx.Attributes[0];
            string key = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string k ? k : "";
            var named = attr.NamedArguments.ToImmutableDictionary(a => a.Key, a => a.Value);

            bool derivesComponent = false;
            for (INamedTypeSymbol? b = type.BaseType; b is not null; b = b.BaseType)
                if (b.ToDisplayString() == ComponentBase) { derivesComponent = true; break; }
            if (!derivesComponent) diags.Add(new Diag("FGRT003", typeLoc, type.Name));

            // Resolve the best routable ctor: (Route) > (string) > parameterless.
            Ctor ctor = Ctor.None;
            foreach (IMethodSymbol c in type.InstanceConstructors)
            {
                if (c.DeclaredAccessibility != Accessibility.Public && c.DeclaredAccessibility != Accessibility.Internal)
                    continue;
                if (c.Parameters.Length == 0) { if (ctor < Ctor.Parameterless) ctor = Ctor.Parameterless; }
                else if (c.Parameters.Length == 1)
                {
                    string pt = c.Parameters[0].Type.ToDisplayString();
                    if (pt == RouteType) ctor = Ctor.RouteArg;
                    else if (pt == "string" && ctor < Ctor.StringArg) ctor = Ctor.StringArg;
                }
            }
            if (ctor == Ctor.None) diags.Add(new Diag("FGRT002", typeLoc, type.Name));

            bool emit = derivesComponent && ctor != Ctor.None && key.Length > 0;
            return new RouteModel(
                key,
                type.Locations.FirstOrDefault() ?? Location.None,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                (int)ctor,
                named.TryGetValue("Title", out var t) && t.Value is string ts ? ts : null,
                named.TryGetValue("Icon", out var i) && i.Value is string ic ? ic : null,
                named.TryGetValue("Category", out var cat) && cat.Value is string cs ? cs : null,
                named.TryGetValue("Order", out var o) && o.Value is int ov ? ov : (int?)null,
                named.TryGetValue("KeepAlive", out var ka) && ka.Value is bool kv ? kv : (bool?)null,
                named.TryGetValue("ShowInNav", out var sn) && sn.Value is bool sv ? sv : (bool?)null,
                diags.ToImmutable(),
                emit);
        }

        private static string Emit(ImmutableArray<RouteModel> models)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/>\n// Generated by FluentGpu.SourceGen (Routing/RouteTableGenerator) — DO NOT EDIT.\n#nullable enable\n\n");
            sb.Append("namespace FluentGpu.Generated\n{\n");
            sb.Append("    /// <summary>Compile-time route table (RouteTableGenerator): registers every [Route]-tagged page.</summary>\n");
            sb.Append("    [global::System.CodeDom.Compiler.GeneratedCode(\"FluentGpu.SourceGen\", \"1.0\")]\n");
            sb.Append("    public static partial class Routes\n    {\n");
            sb.Append("        /// <summary>Add every generated <c>RouteDef</c> to <paramref name=\"r\"/> (call once at shell startup).</summary>\n");
            sb.Append("        public static void RegisterAll(global::FluentGpu.Controls.RouteRegistry r)\n        {\n");

            foreach (RouteModel m in models.OrderBy(static x => x.FqTypeName, System.StringComparer.Ordinal))
            {
                string factory = (Ctor)m.CtorKind switch
                {
                    Ctor.RouteArg => $"route => global::FluentGpu.Hooks.Embed.Comp(() => new {m.FqTypeName}(route))",
                    Ctor.StringArg => $"route => global::FluentGpu.Hooks.Embed.Comp(() => new {m.FqTypeName}(route.Arg ?? \"\"))",
                    _ => $"_ => global::FluentGpu.Hooks.Embed.Comp(static () => new {m.FqTypeName}())",
                };
                sb.Append("            r.Add(new global::FluentGpu.Controls.RouteDef(").Append(Quote(m.Key)).Append(", ").Append(factory).Append(")");
                var inits = new List<string>();
                if (m.Title is not null) inits.Add("Title = " + Quote(m.Title));
                if (m.Icon is not null) inits.Add("Icon = " + Quote(m.Icon));
                if (m.Category is not null) inits.Add("Category = " + Quote(m.Category));
                if (m.Order is int ord) inits.Add("Order = " + ord.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (m.KeepAlive is bool ka) inits.Add("KeepAlive = " + (ka ? "true" : "false"));
                if (m.ShowInNav is bool sn) inits.Add("ShowInNav = " + (sn ? "true" : "false"));
                if (inits.Count > 0) sb.Append(" { ").Append(string.Join(", ", inits)).Append(" }");
                sb.Append(");\n");
            }

            sb.Append("        }\n    }\n}\n");
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    default: sb.Append(c); break;
                }
            sb.Append('"');
            return sb.ToString();
        }

        private readonly record struct Diag(string Id, Location Location, string Arg0);

        private readonly record struct RouteModel(
            string Key, Location KeyLocation, string FqTypeName, int CtorKind,
            string? Title, string? Icon, string? Category, int? Order, bool? KeepAlive, bool? ShowInNav,
            ImmutableArray<Diag> Diagnostics, bool Emit);
    }
}
