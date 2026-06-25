using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.SourceGen.Engine
{
    /// <summary>
    /// Engine DSL generator #1 — the seed of <c>ElementTypeIdGenerator</c> (design/subsystems/dsl-aot.md §2.1).
    ///
    /// <para>Wave 1 (this file) lands the FOUNDATION every other engine generator needs:</para>
    /// <list type="bullet">
    /// <item>It emits the engine-codegen MARKER ATTRIBUTES — <c>[Element]/[Prop]/[Modifier]/[ThemeTokens]/[Factory]/
    /// [FastPath]</c> — into the consuming compilation via post-initialization. They are <c>internal</c>, so each
    /// assembly that references the analyzer gets its own copy and the public types never clash across assemblies.</item>
    /// <item>It enforces <b>WGPU0003</b>: a hard compile error if two <c>Element</c>-derived records declare the same
    /// <c>ElementTypeId</c>. The reconciler dispatches on that id (an int compare, never <c>GetType()</c>), so a
    /// collision is a silent mis-dispatch — the "id guard" the source-generator investigation flagged as worth
    /// folding in regardless (zero runtime cost, real maintainability). It scans <c>Element</c>-derived records
    /// directly, so it needs no record changes to start working.</item>
    /// </list>
    ///
    /// <para>The typed-setter / factory emission (§2.1) and the DiffProps differ + Equals/GetHashCode (§2.2) land in
    /// later waves behind their own files and opt-in flags, once the records carry <c>[Element]/[Prop]</c>.</para>
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class ElementGenerator : IIncrementalGenerator
    {
        private const string ElementBase = "FluentGpu.Dsl.Element";

        private static readonly DiagnosticDescriptor DuplicateTypeId = new(
            id: "WGPU0003",
            title: "Duplicate ElementTypeId",
            messageFormat: "ElementTypeId {0} is declared by more than one Element type ({1}); the reconciler dispatches on this id, so each Element record needs a unique value",
            category: "FluentGpu.Dsl",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "design/subsystems/dsl-aot.md §2.1 (WGPU0003).");

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // The marker attributes the engine DSL generators trigger on. Emitted internal → per-assembly, no clash.
            context.RegisterPostInitializationOutput(static ctx =>
                ctx.AddSource("FluentGpu.CodeGen.Attributes.g.cs", SourceText.From(AttributeSource, Encoding.UTF8)));

            // WGPU0003: scan every Element-derived record's ElementTypeId, flag duplicates. No [Element] needed yet.
            var elements = context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => node is RecordDeclarationSyntax r && r.BaseList is not null,
                    transform: static (ctx, ct) => Extract(ctx, ct))
                .Where(static x => x is not null)
                .Select(static (x, _) => x!.Value)
                .Collect();

            context.RegisterSourceOutput(elements, static (spc, infos) => ReportDuplicates(spc, infos));
        }

        private static ElementTypeInfo? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
        {
            var decl = (RecordDeclarationSyntax)ctx.Node;
            if (ctx.SemanticModel.GetDeclaredSymbol(decl, ct) is not INamedTypeSymbol type) return null;
            if (type.IsAbstract) return null;
            if (!DerivesFromElement(type)) return null;

            IPropertySymbol? prop = type.GetMembers("ElementTypeId").OfType<IPropertySymbol>().FirstOrDefault();
            if (prop is null || prop.IsAbstract) return null;

            // Read the literal the override returns: `public override ushort ElementTypeId => 1;`.
            foreach (SyntaxReference sref in prop.DeclaringSyntaxReferences)
            {
                if (sref.GetSyntax(ct) is PropertyDeclarationSyntax pds && pds.ExpressionBody is not null)
                {
                    var expr = pds.ExpressionBody.Expression;
                    SemanticModel model = ctx.SemanticModel.Compilation.GetSemanticModel(pds.SyntaxTree);
                    Optional<object?> cv = model.GetConstantValue(expr, ct);
                    if (cv.HasValue && cv.Value is not null)
                    {
                        long value;
                        try { value = Convert.ToInt64(cv.Value); } catch { return null; }
                        // Carry an incremental-safe location (no SyntaxTree held in the pipeline model).
                        Location loc = expr.GetLocation();
                        FileLinePositionSpan fls = loc.GetLineSpan();
                        return new ElementTypeInfo(type.ToDisplayString(), value, fls.Path, expr.Span, fls.Span);
                    }
                    break;
                }
            }
            return null;
        }

        private static bool DerivesFromElement(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType)
                if (t.ToDisplayString() == ElementBase) return true;
            return false;
        }

        private static void ReportDuplicates(SourceProductionContext spc, ImmutableArray<ElementTypeInfo> infos)
        {
            var byId = new Dictionary<long, List<ElementTypeInfo>>();
            foreach (ElementTypeInfo info in infos)
            {
                if (!byId.TryGetValue(info.TypeId, out List<ElementTypeInfo>? list))
                {
                    list = new List<ElementTypeInfo>();
                    byId[info.TypeId] = list;
                }
                list.Add(info);
            }

            foreach (KeyValuePair<long, List<ElementTypeInfo>> kv in byId)
            {
                if (kv.Value.Count < 2) continue;
                string names = string.Join(", ", kv.Value.Select(i => i.TypeName).OrderBy(n => n, StringComparer.Ordinal));
                foreach (ElementTypeInfo info in kv.Value)
                {
                    Location loc = Location.Create(info.FilePath, info.Span, info.LineSpan);
                    spc.ReportDiagnostic(Diagnostic.Create(DuplicateTypeId, loc, kv.Key, names));
                }
            }
        }

        private readonly record struct ElementTypeInfo(
            string TypeName, long TypeId, string FilePath, TextSpan Span, LinePositionSpan LineSpan);

        private const string AttributeSource =
@"// <auto-generated/>
// Generated by FluentGpu.SourceGen (Engine/ElementGenerator) — DO NOT EDIT.
// The engine-codegen marker attributes (design/subsystems/dsl-aot.md §2). Internal so each referencing assembly
// owns its own copy: applying [Element]/[Prop]/... never produces a cross-assembly public-type clash.
#nullable enable

namespace FluentGpu.CodeGen
{
    /// <summary>Marks a `sealed partial record : Element` for engine codegen (ElementTypeId, typed setters,
    /// factories, DiffProps, Equals/GetHashCode). WGPU0001 errors if the shape is wrong.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [global::System.CodeDom.Compiler.GeneratedCode(""FluentGpu.SourceGen"", ""1.0"")]
    internal sealed class ElementAttribute : global::System.Attribute { }

    /// <summary>A diffable element property: contributes to the generated DiffProps bitmask + Equals/GetHashCode and
    /// gets a strongly-typed fluent setter. (`Element`-typed children are excluded — the subtree diff owns them.)</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    [global::System.CodeDom.Compiler.GeneratedCode(""FluentGpu.SourceGen"", ""1.0"")]
    internal sealed class PropAttribute : global::System.Attribute { }

    /// <summary>Marks an extension method that mutates an element via a single `with` — fused by the modifier generator.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    [global::System.CodeDom.Compiler.GeneratedCode(""FluentGpu.SourceGen"", ""1.0"")]
    internal sealed class ModifierAttribute : global::System.Attribute { }

    /// <summary>Marks the partial token class whose values bake into the per-theme data-section blob (ThemeBlobGenerator).</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [global::System.CodeDom.Compiler.GeneratedCode(""FluentGpu.SourceGen"", ""1.0"")]
    internal sealed class ThemeTokensAttribute : global::System.Attribute { }

    /// <summary>Marks an element ctor/method the factory generator should surface as a `UI.*` ergonomic factory.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method | global::System.AttributeTargets.Constructor, AllowMultiple = true, Inherited = false)]
    [global::System.CodeDom.Compiler.GeneratedCode(""FluentGpu.SourceGen"", ""1.0"")]
    internal sealed class FactoryAttribute : global::System.Attribute { }

    /// <summary>Hints that an element type is used inside a per-item list template — the differ/setter codegen emits
    /// its tightest path (WGPU0010 suggests it).</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [global::System.CodeDom.Compiler.GeneratedCode(""FluentGpu.SourceGen"", ""1.0"")]
    internal sealed class FastPathAttribute : global::System.Attribute { }
}
";
    }
}
