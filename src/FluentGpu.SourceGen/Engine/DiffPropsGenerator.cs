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
    /// Engine DSL generator #3 — <c>DiffPropsGenerator</c> (design/subsystems/dsl-aot.md §2.2): the no-reflection
    /// bitmask change-detector + a reference-equality helper, folding GEN-01 + GEN-10 of the source-gen investigation.
    ///
    /// <para><b>OPT-OUT (on by default).</b> EVERY concrete record deriving <c>Element</c> gets a generated differ —
    /// no annotation required. A record opts OUT with <c>[FluentGpu.CodeGen.NoCodegen]</c>. The diffable fields are all
    /// settable public data props; <c>Element</c>-typed props (and <c>Element[]</c>) are EXCLUDED — the reconciler's
    /// subtree diff owns children (the WGPU0002 carve-out). If a record marks any prop <c>[FluentGpu.CodeGen.Prop]</c>,
    /// only those are diffed (the explicit refinement).</para>
    ///
    /// <para><b>Emits</b> per record <c>T</c>: a <c>[Flags] enum {T}Columns : ulong</c> (one bit per diffable prop, cap
    /// 64) and <c>internal static class {T}Diff</c> with <c>{T}Columns Diff(T a, T b)</c> (a flat per-field compare, no
    /// <c>GetType()</c>, no boxing for value props) + a <c>RefEquals</c> helper (the GEN-10 reference-equality path; the
    /// record's synthesized value-equality is left UNCHANGED — flipping it is the canon-sensitive live migration).</para>
    ///
    /// <para>The emitted <c>Diff</c> is additive: wiring it into the reconciler's <c>WriteColumns</c> (the side-effect
    /// minefield) is a separate live migration. Per the source-gen investigation: build-later, off-hot-path CPU only —
    /// its memory/recorder-elision claims were refuted against the live reconciler.</para>
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class DiffPropsGenerator : IIncrementalGenerator
    {
        private const string PropAttr = "FluentGpu.CodeGen.PropAttribute";
        private const string OptOutAttr = "FluentGpu.CodeGen.NoCodegenAttribute";
        private const string ElementBase = "FluentGpu.Dsl.Element";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(static ctx =>
                ctx.AddSource("FluentGpu.CodeGen.NoCodegenAttribute.g.cs", SourceText.From(OptOutSource, Encoding.UTF8)));

            var models = context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => node is RecordDeclarationSyntax r && r.BaseList is not null,
                    transform: static (ctx, ct) => Extract(ctx, ct))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);

            context.RegisterSourceOutput(models, static (spc, model) =>
                spc.AddSource(model.HintName + ".DiffProps.g.cs", SourceText.From(Emit(model), Encoding.UTF8)));
        }

        private static Model? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
        {
            var decl = (RecordDeclarationSyntax)ctx.Node;
            if (ctx.SemanticModel.GetDeclaredSymbol(decl, ct) is not INamedTypeSymbol type) return null;
            if (type.IsAbstract) return null;
            if (!DerivesFromElement(type)) return null;
            if (type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == OptOutAttr)) return null; // opt-out

            var explicitProps = ImmutableArray.CreateBuilder<PropInfo>();
            var allProps = ImmutableArray.CreateBuilder<PropInfo>();
            foreach (ISymbol member in type.GetMembers())
            {
                ct.ThrowIfCancellationRequested();
                if (member is not IPropertySymbol p || p.IsStatic || p.IsIndexer) continue;
                if (IsElementShaped(p.Type)) continue; // children diff is the reconciler's job (WGPU0002 carve-out)
                var info = new PropInfo(p.Name, p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                if (p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PropAttr))
                    explicitProps.Add(info);
                if (p.DeclaredAccessibility == Accessibility.Public && p.SetMethod is not null)
                    allProps.Add(info); // settable (incl. init) public data props
            }
            // Prefer explicit [Prop] markers; otherwise diff every settable public data prop.
            ImmutableArray<PropInfo>.Builder chosen = explicitProps.Count > 0 ? explicitProps : allProps;
            if (chosen.Count == 0) return null;

            bool truncated = chosen.Count > 64; // ulong bitmask cap (dsl-aot.md §2.2)
            ImmutableArray<PropInfo> kept = truncated ? chosen.Take(64).ToImmutableArray() : chosen.ToImmutable();

            return new Model(
                type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                type.Name,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Sanitize(type.ToDisplayString()),
                kept,
                truncated);
        }

        private static bool DerivesFromElement(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType)
                if (t.ToDisplayString() == ElementBase) return true;
            return false;
        }

        private static bool IsElementShaped(ITypeSymbol t)
        {
            if (t is IArrayTypeSymbol arr) t = arr.ElementType;
            for (ITypeSymbol? cur = t; cur is not null; cur = cur.BaseType)
                if (cur.ToDisplayString() == ElementBase) return true;
            return false;
        }

        private static string Emit(Model m)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/>\n// Generated by FluentGpu.SourceGen (Engine/DiffPropsGenerator) — DO NOT EDIT.\n#nullable enable\n\n");
            bool hasNs = m.Namespace is not null;
            string pad = hasNs ? "    " : "";
            if (hasNs) sb.Append("namespace ").Append(m.Namespace).Append("\n{\n");

            sb.Append(pad).Append("/// <summary>Per-prop change bits for <c>").Append(m.TypeName).Append("</c> (generated DiffProps).</summary>\n");
            sb.Append(pad).Append("[global::System.Flags]\n");
            sb.Append(pad).Append("internal enum ").Append(m.TypeName).Append("Columns : ulong\n").Append(pad).Append("{\n");
            sb.Append(pad).Append("    None = 0,\n");
            for (int i = 0; i < m.Props.Length; i++)
                sb.Append(pad).Append("    ").Append(m.Props[i].Name).Append(" = 1UL << ").Append(i).Append(",\n");
            sb.Append(pad).Append("}\n\n");

            sb.Append(pad).Append("internal static class ").Append(m.TypeName).Append("Diff\n").Append(pad).Append("{\n");
            sb.Append(pad).Append("    /// <summary>No-reflection per-prop change mask (a flat compare; no GetType, no box for value props).")
              .Append(m.Truncated ? " NOTE: >64 diffable props — only the first 64 are masked." : "").Append("</summary>\n");
            sb.Append(pad).Append("    public static ").Append(m.TypeName).Append("Columns Diff(").Append(m.FqTypeName).Append(" a, ").Append(m.FqTypeName).Append(" b)\n");
            sb.Append(pad).Append("    {\n");
            sb.Append(pad).Append("        ").Append(m.TypeName).Append("Columns m = 0;\n");
            foreach (PropInfo p in m.Props)
            {
                sb.Append(pad).Append("        if (!global::System.Collections.Generic.EqualityComparer<").Append(p.TypeFqn)
                  .Append(">.Default.Equals(a.").Append(p.Name).Append(", b.").Append(p.Name).Append(")) m |= ")
                  .Append(m.TypeName).Append("Columns.").Append(p.Name).Append(";\n");
            }
            sb.Append(pad).Append("        return m;\n");
            sb.Append(pad).Append("    }\n\n");
            sb.Append(pad).Append("    /// <summary>GEN-10 reference-equality helper (opt-in; the record's value-equality is left intact).</summary>\n");
            sb.Append(pad).Append("    public static bool RefEquals(").Append(m.FqTypeName).Append(" a, ").Append(m.FqTypeName)
              .Append(" b) => global::System.Object.ReferenceEquals(a, b);\n");
            sb.Append(pad).Append("}\n");

            if (hasNs) sb.Append("}\n");
            return sb.ToString();
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private const string OptOutSource =
@"// <auto-generated/>
// Opt-out marker for the element codegen (DiffProps et al.). Apply to a record to exclude it. Internal per-assembly.
#nullable enable
namespace FluentGpu.CodeGen
{
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [global::System.CodeDom.Compiler.GeneratedCode(""FluentGpu.SourceGen"", ""1.0"")]
    internal sealed class NoCodegenAttribute : global::System.Attribute { }
}
";

        private readonly record struct PropInfo(string Name, string TypeFqn);

        private readonly record struct Model(
            string? Namespace, string TypeName, string FqTypeName, string HintName,
            ImmutableArray<PropInfo> Props, bool Truncated);
    }
}
