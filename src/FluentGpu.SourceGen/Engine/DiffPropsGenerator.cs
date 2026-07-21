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
    /// Engine DSL generator #3 — <c>DiffPropsGenerator</c> (design/subsystems/dsl-aot.md §2.2): the no-reflection
    /// change-detector that lets the reconciler skip a redundant <c>WriteColumns</c> on a field-equal re-render.
    ///
    /// <para><b>OPT-OUT (on by default).</b> EVERY concrete record deriving <c>Element</c> gets a generated
    /// <c>{T}Diff.AnyChanged(a, b)</c> — no annotation. A record opts OUT with <c>[FluentGpu.CodeGen.NoCodegen]</c>.</para>
    ///
    /// <para><b>Correctness:</b> the comparison covers EVERY settable public data prop across the WHOLE inheritance
    /// chain — including the base <c>Element</c> animation/declarative fields (<c>Transition/WhileHover/Enter/Exit/
    /// Layout/Stagger/RelativeTo/ScrollBinds/MorphId</c>). Missing those (a declared-props-only diff) would mis-report an
    /// animation change as "unchanged" and the reconciler would wrongly skip <c>WriteColumns</c>, breaking FLIP/reflow.
    /// <c>Element</c>-typed props (and <c>Element[]</c>) are EXCLUDED — the reconciler's subtree diff owns children
    /// (WGPU0002 carve-out). The result is a single boolean OR-chain, so there is NO 64-prop bitmask cap that could drop
    /// a field.</para>
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
            // Generic Element records (e.g. Flow's ForEl<T>) get no DiffProps: a non-generic static Diff class can't name
            // the open type parameter, and these boundary elements bypass WriteColumns (MountFor/UpdateFor own them).
            if (type.TypeParameters.Length > 0) return null;
            if (!DerivesFromElement(type)) return null;
            if (type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == OptOutAttr)) return null; // opt-out

            var seen = new HashSet<string>();
            var explicitProps = ImmutableArray.CreateBuilder<PropInfo>();
            var allProps = ImmutableArray.CreateBuilder<PropInfo>();
            // Walk the WHOLE chain (the record + its bases, through Element) so inherited animation fields are diffed.
            for (INamedTypeSymbol? t = type; t is not null && IsOrDerivesElement(t); t = t.BaseType)
            {
                foreach (ISymbol member in t.GetMembers())
                {
                    ct.ThrowIfCancellationRequested();
                    if (member is not IPropertySymbol p || p.IsStatic || p.IsIndexer) continue;
                    if (!seen.Add(p.Name)) continue;                 // most-derived wins on a `new` shadow
                    if (IsElementShaped(p.Type)) continue;           // children are the subtree diff's job
                    string typeFqn = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var info = new PropInfo(p.Name, typeFqn, IsPropChannel(p.Type));
                    if (p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PropAttr))
                        explicitProps.Add(info);
                    if (p.DeclaredAccessibility == Accessibility.Public && p.SetMethod is not null)
                        allProps.Add(info);                          // settable (incl. init) public data props
                }
            }
            // Prefer explicit [Prop] markers; otherwise diff every settable public data prop.
            ImmutableArray<PropInfo>.Builder chosen = explicitProps.Count > 0 ? explicitProps : allProps;
            if (chosen.Count == 0) return null;

            return new Model(
                type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                type.Name,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Sanitize(type.ToDisplayString()),
                chosen.ToImmutable());
        }

        private static bool DerivesFromElement(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType)
                if (t.ToDisplayString() == ElementBase) return true;
            return false;
        }

        private static bool IsOrDerivesElement(INamedTypeSymbol t) =>
            t.ToDisplayString() == ElementBase || DerivesFromElement(t);

        private static bool IsElementShaped(ITypeSymbol t)
        {
            if (t is IArrayTypeSymbol arr) t = arr.ElementType;
            for (ITypeSymbol? cur = t; cur is not null; cur = cur.BaseType)
                if (cur.ToDisplayString() == ElementBase) return true;
            return false;
        }

        // A bindable channel is a FluentGpu.Signals.Prop<T> — the ONE shape whose static-vs-bound state (IsBound) the
        // BindContract tripwire checks for a mount-only-bind flip. Detected structurally (constructed generic named
        // Prop in FluentGpu.Signals) so the check is generated, never hand-listed per element type.
        private static bool IsPropChannel(ITypeSymbol t)
            => t is INamedTypeSymbol { Name: "Prop", IsGenericType: true } n
               && n.ContainingNamespace?.ToDisplayString() == "FluentGpu.Signals";

        private static string Emit(Model m)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/>\n// Generated by FluentGpu.SourceGen (Engine/DiffPropsGenerator) — DO NOT EDIT.\n#nullable enable\n\n");
            bool hasNs = m.Namespace is not null;
            string pad = hasNs ? "    " : "";
            if (hasNs) sb.Append("namespace ").Append(m.Namespace).Append("\n{\n");

            sb.Append(pad).Append("/// <summary>Generated change-detector for <c>").Append(m.TypeName)
              .Append("</c> (DiffProps): every settable data prop across the inheritance chain; Element-typed children excluded.</summary>\n");
            sb.Append(pad).Append("internal static class ").Append(m.TypeName).Append("Diff\n").Append(pad).Append("{\n");
            sb.Append(pad).Append("    /// <summary>True iff any diffable prop differs (no GetType, no box for value props). The reconciler\n");
            sb.Append(pad).Append("    /// skips a redundant WriteColumns when this is false — Children + bound channels are handled elsewhere.</summary>\n");
            sb.Append(pad).Append("    public static bool AnyChanged(").Append(m.FqTypeName).Append(" a, ").Append(m.FqTypeName).Append(" b)\n");
            for (int i = 0; i < m.Props.Length; i++)
            {
                PropInfo p = m.Props[i];
                sb.Append(pad).Append(i == 0 ? "        => " : "        || ")
                  .Append("!global::System.Collections.Generic.EqualityComparer<").Append(p.TypeFqn)
                  .Append(">.Default.Equals(a.").Append(p.Name).Append(", b.").Append(p.Name).Append(")")
                  .Append(i == m.Props.Length - 1 ? ";\n" : "\n");
            }

            // BindContract helper (DEBUG tripwire): the name of the FIRST bindable channel (a Prop<T>) whose bound-vs-
            // static shape FLIPPED between the two element versions, or null if none did. Bind wiring is mount-only, so a
            // static→bound or bound→static flip on a reused node silently loses; the reconciler reports it. Generated (not
            // hand-listed) so every element type + every Prop<T> channel is covered mechanically, with no drift.
            sb.Append("\n");
            sb.Append(pad).Append("    /// <summary>The first bindable <c>Prop&lt;T&gt;</c> channel whose <c>IsBound</c> flipped between\n");
            sb.Append(pad).Append("    /// <paramref name=\"a\"/> and <paramref name=\"b\"/> (mount-only bind wiring ⇒ a silent loss), or null. BindContract tripwire.</summary>\n");
            sb.Append(pad).Append("    public static string? FirstBoundFlip(").Append(m.FqTypeName).Append(" a, ").Append(m.FqTypeName).Append(" b)\n");
            sb.Append(pad).Append("    {\n");
            bool anyProp = false;
            for (int i = 0; i < m.Props.Length; i++)
            {
                PropInfo p = m.Props[i];
                if (!p.IsProp) continue;
                anyProp = true;
                sb.Append(pad).Append("        if (a.").Append(p.Name).Append(".IsBound != b.").Append(p.Name)
                  .Append(".IsBound) return \"").Append(p.Name).Append("\";\n");
            }
            if (!anyProp) sb.Append(pad).Append("        _ = a; _ = b;\n");
            sb.Append(pad).Append("        return null;\n");
            sb.Append(pad).Append("    }\n");

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

        private readonly record struct PropInfo(string Name, string TypeFqn, bool IsProp);

        private readonly record struct Model(
            string? Namespace, string TypeName, string FqTypeName, string HintName, ImmutableArray<PropInfo> Props);
    }
}
