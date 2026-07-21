using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.SourceGen.Gallery
{
    /// <summary>
    /// Gallery generator (master plan §WS7 W7.0). Collects every
    /// <c>[FluentGpu.GalleryKit.GalleryPage(key, title, category)] class X : Component</c> in the assembly and emits ONE
    /// <c>FluentGpu.Generated.GalleryRegistry</c> with <c>Pages</c> (a <c>GalleryPageInfo[]</c> table) + a
    /// <c>Element? Create(string key)</c> factory — compile-time, zero reflection (AOT-clean). Dormant unless a
    /// <c>[GalleryPage]</c> is present (ForAttributeWithMetadataName). It is the one source of truth the shell derives
    /// its nav tree, search index, and All-controls grid from — the compile IS the sync check.
    ///
    /// <para>Diagnostics: <c>FGG010</c> (duplicate key across the compilation) — Error; <c>FGG011</c> (not a
    /// <c>Component</c>) — Error; <c>FGG012</c> (no parameterless constructor) — Error.</para>
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class GalleryRegistryGenerator : IIncrementalGenerator
    {
        private const string PageAttr = "FluentGpu.GalleryKit.GalleryPageAttribute";
        private const string ComponentBase = "FluentGpu.Hooks.Component";

        private static readonly DiagnosticDescriptor DuplicateKey = new(
            "FGG010", "duplicate [GalleryPage] key",
            "duplicate [GalleryPage] key '{0}' — page keys must be unique across the assembly (a collision would shadow a "
            + "page in the registry). Rename one of the pages' keys.",
            "FluentGpu.Gallery", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor NotComponent = new(
            "FGG011", "[GalleryPage] class must derive Component",
            "[GalleryPage] class '{0}' must derive FluentGpu.Hooks.Component (a gallery page resolves to a page component).",
            "FluentGpu.Gallery", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor NoCtor = new(
            "FGG012", "[GalleryPage] class needs a parameterless constructor",
            "[GalleryPage] class '{0}' has no accessible parameterless constructor — the generated factory builds it as "
            + "'new {0}()'.",
            "FluentGpu.Gallery", DiagnosticSeverity.Error, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(
                    PageAttr,
                    predicate: static (node, _) => node is ClassDeclarationSyntax,
                    transform: static (ctx, ct) => Extract(ctx, ct))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);

            context.RegisterSourceOutput(models, static (spc, model) =>
            {
                foreach (Diag d in model.Diagnostics)
                    spc.ReportDiagnostic(Diagnostic.Create(DescriptorFor(d.Id), d.Location, d.Arg0));
            });

            var collected = models.Collect();
            context.RegisterSourceOutput(collected, static (spc, all) =>
            {
                var valid = all.Where(static m => m.Emit).ToImmutableArray();
                if (valid.Length == 0) return;

                var seen = new Dictionary<string, PageModel>();
                var emit = ImmutableArray.CreateBuilder<PageModel>();
                foreach (PageModel m in valid)
                {
                    if (seen.ContainsKey(m.Key))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(DuplicateKey, m.KeyLocation, m.Key));
                        continue;
                    }
                    seen.Add(m.Key, m);
                    emit.Add(m);
                }

                spc.AddSource("GalleryRegistry.g.cs", SourceText.From(Emit(emit.ToImmutable()), Encoding.UTF8));
            });
        }

        private static DiagnosticDescriptor DescriptorFor(string id) => id switch
        {
            "FGG011" => NotComponent,
            _ => NoCtor,
        };

        private static PageModel? Extract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

            var diags = ImmutableArray.CreateBuilder<Diag>();
            Location typeLoc = type.Locations.FirstOrDefault() ?? Location.None;

            AttributeData attr = ctx.Attributes[0];
            string key = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string k ? k : "";
            string title = attr.ConstructorArguments.Length > 1 && attr.ConstructorArguments[1].Value is string t ? t : "";
            string category = attr.ConstructorArguments.Length > 2 && attr.ConstructorArguments[2].Value is string c ? c : "";
            var named = attr.NamedArguments.ToImmutableDictionary(a => a.Key, a => a.Value);

            bool derivesComponent = false;
            for (INamedTypeSymbol? b = type.BaseType; b is not null; b = b.BaseType)
                if (b.ToDisplayString() == ComponentBase) { derivesComponent = true; break; }
            if (!derivesComponent) diags.Add(new Diag("FGG011", typeLoc, type.Name));

            // InstanceConstructors always includes the implicit public parameterless ctor when no explicit ctor exists.
            bool hasParameterlessCtor = false;
            foreach (IMethodSymbol cc in type.InstanceConstructors)
                if (cc.Parameters.Length == 0
                    && (cc.DeclaredAccessibility == Accessibility.Public || cc.DeclaredAccessibility == Accessibility.Internal))
                    hasParameterlessCtor = true;
            if (!hasParameterlessCtor) diags.Add(new Diag("FGG012", typeLoc, type.Name));

            string[] keywords = named.TryGetValue("Keywords", out var kw) && kw.Kind == TypedConstantKind.Array
                ? kw.Values.Select(v => v.Value as string ?? "").Where(s => s.Length > 0).ToArray()
                : System.Array.Empty<string>();

            bool emit = derivesComponent && hasParameterlessCtor && key.Length > 0;
            return new PageModel(
                key, type.Locations.FirstOrDefault() ?? Location.None,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                title, category,
                named.TryGetValue("Icon", out var i) && i.Value is string ic ? ic : "",
                keywords,
                named.TryGetValue("Order", out var o) && o.Value is int ov ? ov : 1000,
                // ShotMode / Level are byte-backed enums, so their TypedConstant values are boxed bytes (not int) — convert.
                named.TryGetValue("ShotMode", out var sm) && sm.Value is not null ? System.Convert.ToInt32(sm.Value) : 0,
                named.TryGetValue("Hidden", out var h) && h.Value is bool hv && hv,
                named.TryGetValue("Level", out var lv) && lv.Value is not null ? System.Convert.ToInt32(lv.Value) : 0,
                named.TryGetValue("WaveeUse", out var wu) && wu.Value is string wus ? wus : "",
                named.TryGetValue("WaveePath", out var wp) && wp.Value is string wps ? wps : "",
                diags.ToImmutable(), emit);
        }

        private static string Emit(ImmutableArray<PageModel> models)
        {
            var ordered = models.OrderBy(static x => x.FqTypeName, System.StringComparer.Ordinal).ToImmutableArray();
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/>\n// Generated by FluentGpu.SourceGen (Gallery/GalleryRegistryGenerator) — DO NOT EDIT.\n#nullable enable\n\n");
            sb.Append("namespace FluentGpu.Generated\n{\n");
            sb.Append("    /// <summary>Compile-time gallery registry (GalleryRegistryGenerator): every [GalleryPage]-tagged page.</summary>\n");
            sb.Append("    [global::System.CodeDom.Compiler.GeneratedCode(\"FluentGpu.SourceGen\", \"1.0\")]\n");
            sb.Append("    public static partial class GalleryRegistry\n    {\n");

            // Pages[] — the metadata table (with the mount factory) the shell derives everything from.
            sb.Append("        /// <summary>Every [GalleryPage] in the assembly, in stable (type-name) order.</summary>\n");
            sb.Append("        public static readonly global::FluentGpu.GalleryKit.GalleryPageInfo[] Pages = new global::FluentGpu.GalleryKit.GalleryPageInfo[]\n        {\n");
            foreach (PageModel m in ordered)
            {
                sb.Append("            new global::FluentGpu.GalleryKit.GalleryPageInfo(")
                  .Append(Quote(m.Key)).Append(", ").Append(Quote(m.Title)).Append(", ").Append(Quote(m.Category))
                  .Append(", static () => global::FluentGpu.Hooks.Embed.Comp(static () => new ").Append(m.FqTypeName).Append("()))");
                var inits = new List<string>();
                if (m.Icon.Length > 0) inits.Add("Icon = " + Quote(m.Icon));
                if (m.Keywords.Length > 0)
                    inits.Add("Keywords = new string[] { " + string.Join(", ", m.Keywords.Select(Quote)) + " }");
                if (m.Order != 1000) inits.Add("Order = " + m.Order.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (m.ShotMode != 0) inits.Add("ShotMode = (global::FluentGpu.GalleryKit.ShotMode)" + m.ShotMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (m.Hidden) inits.Add("Hidden = true");
                if (m.Level != 0) inits.Add("Level = (global::FluentGpu.GalleryKit.GalleryLevel)" + m.Level.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (m.WaveeUse.Length > 0) inits.Add("WaveeUse = " + Quote(m.WaveeUse));
                if (m.WaveePath.Length > 0) inits.Add("WaveePath = " + Quote(m.WaveePath));
                if (inits.Count > 0) sb.Append(" { ").Append(string.Join(", ", inits)).Append(" }");
                sb.Append(",\n");
            }
            sb.Append("        };\n\n");

            // Create(key) — the direct page factory (a switch; zero reflection).
            sb.Append("        /// <summary>Mount the page for <paramref name=\"key\"/>, or null if unregistered.</summary>\n");
            sb.Append("        public static global::FluentGpu.Dsl.Element? Create(string key) => key switch\n        {\n");
            foreach (PageModel m in ordered)
                sb.Append("            ").Append(Quote(m.Key)).Append(" => global::FluentGpu.Hooks.Embed.Comp(static () => new ").Append(m.FqTypeName).Append("()),\n");
            sb.Append("            _ => null,\n        };\n");

            sb.Append("    }\n}\n");
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char ch in s)
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    default: sb.Append(ch); break;
                }
            sb.Append('"');
            return sb.ToString();
        }

        private readonly record struct Diag(string Id, Location Location, string Arg0);

        private readonly record struct PageModel(
            string Key, Location KeyLocation, string FqTypeName, string Title, string Category,
            string Icon, string[] Keywords, int Order, int ShotMode, bool Hidden,
            int Level, string WaveeUse, string WaveePath,
            ImmutableArray<Diag> Diagnostics, bool Emit);
    }
}
