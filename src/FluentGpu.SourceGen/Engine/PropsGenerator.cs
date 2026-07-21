using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.SourceGen.Engine
{
    /// <summary>
    /// Engine reactive generator — <c>PropsGenerator</c> (master plan §WS1 P5). Lowers a
    /// <c>[FluentGpu.Hooks.Props] partial class X : Component</c> with <c>[FluentGpu.Hooks.Prop] public partial T Name
    /// { get; }</c> members into the signal-backed live-props substrate, so authors declare plain typed props and get
    /// per-field reactivity + the <c>IPropsHost</c> delivery plumbing for free (zero reflection, AOT-clean). Dormant
    /// unless <c>[Props]</c> is present (ForAttributeWithMetadataName).
    ///
    /// <para>Per NON-delegate prop: a mount-allocated <c>Signal&lt;T&gt;</c> + a subscribing partial getter
    /// (<c>=&gt; _nameProp.Value</c>) + a <c>NameProp</c> <c>IReadSignal&lt;T&gt;</c> accessor (bind a live channel into a
    /// child prop/node — the forwarding-preserving path). Per DELEGATE prop (Action / Action&lt;T1..T4&gt; / Func): a
    /// latest-write slot behind a STABLE lazily-created forwarder — a fresh-but-equivalent lambda from the parent does
    /// NOT notify (no signal, no re-render); a wired handler holding the forwarder always invokes the NEWEST delegate.
    /// A delegate with &gt;4 parameters degrades to a raw latest field (FGSG004 Info).</para>
    ///
    /// <para>Also emitted into the same partial: a nested <c>PropsData</c> transport record (one positional param per
    /// prop, declared order); <c>void IPropsHost.ApplyProps(object)</c> (a reference short-circuit, then per-field
    /// equality-gated signal writes — only CHANGED fields notify — plus latest-write for delegates; the reconciler
    /// wraps the call in <c>Runtime.Batch</c>); an <c>Of(...)</c> factory (<c>Embed.Comp(new PropsData(...), () =&gt; new
    /// X())</c>); and <c>CurrentProps()</c> / <c>From(source)</c> snapshot helpers (the documented COLLAPSE path — the
    /// live-reactivity path is the typed <c>NameProp</c> accessors + record <c>with</c>). Finally, an assembly-wide
    /// <c>PropsManifest</c> (skippability report) constant summarizing which props became signals vs delegate
    /// forwarders.</para>
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class PropsGenerator : IIncrementalGenerator
    {
        private const string PropsAttr = "FluentGpu.Hooks.PropsAttribute";
        private const string PropAttr = "FluentGpu.Hooks.PropAttribute";
        private const string ComponentBase = "FluentGpu.Hooks.Component";
        private const int MaxForwarderArgs = 4;   // Action<T1..T4> / Func — beyond this, raw latest field (FGSG004)

        // ── diagnostics (FGSG family) ────────────────────────────────────────────────────────────────────────────
        private static readonly DiagnosticDescriptor NotPartialOrGetOnly = new(
            "FGSG001", "[Prop] must be a get-only partial property",
            "[Prop] '{0}' must be declared 'public partial T {0} {{ get; }}' — a get-only partial property the generator implements",
            "FluentGpu.Reactivity", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor ClassNotPartial = new(
            "FGSG002", "[Props] class must be partial",
            "[Props] class '{0}' must be declared 'partial' so the generator can emit the signal-backed props into it",
            "FluentGpu.Reactivity", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor ClassNotComponent = new(
            "FGSG003", "[Props] class must derive Component",
            "[Props] class '{0}' must derive FluentGpu.Hooks.Component (it receives re-pushed props as an IPropsHost component)",
            "FluentGpu.Reactivity", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor WideDelegate = new(
            "FGSG004", "delegate [Prop] with more than four parameters uses a raw latest field",
            "delegate [Prop] '{0}' has more than {1} parameters, so no stable forwarder is generated — the property returns "
            + "the latest delegate directly; a handler that captures it will hold a SNAPSHOT, not the newest. Reduce the "
            + "arity or wrap the arguments in a record.",
            "FluentGpu.Reactivity", DiagnosticSeverity.Info, true);

        private static readonly DiagnosticDescriptor CollectionProp = new(
            "FGSG005", "collection-typed [Prop] is always reference-compared",
            "[Prop] '{0}' is a collection type ({1}): its backing signal uses the DEFAULT comparer, so a mutated-in-place "
            + "collection never notifies and a fresh-but-equal one always re-renders. Use an immutable/keyed representation "
            + "(ImmutableArray / a keyed record) or a version stamp.",
            "FluentGpu.Reactivity", DiagnosticSeverity.Warning, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(
                    PropsAttr,
                    predicate: static (node, _) => node is ClassDeclarationSyntax,
                    transform: static (ctx, ct) => Extract(ctx, ct))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);

            context.RegisterSourceOutput(models, static (spc, model) =>
            {
                foreach (Diag d in model.Diagnostics)
                    spc.ReportDiagnostic(Diagnostic.Create(DescriptorFor(d.Id), d.Location, d.Arg0, d.Arg1));
                if (model.Emit)
                    spc.AddSource(model.HintName + ".Props.g.cs", SourceText.From(Emit(model), Encoding.UTF8));
            });

            // Skippability report (adjustment #13): one assembly-wide PropsManifest constant listing, per component,
            // which props became signals vs delegate forwarders. Compile-time const string, greppable, zero runtime cost.
            var manifest = models.Collect();
            context.RegisterSourceOutput(manifest, static (spc, all) =>
            {
                var emitted = all.Where(static m => m.Emit).ToImmutableArray();
                if (emitted.Length == 0) return;
                spc.AddSource("PropsManifest.g.cs", SourceText.From(EmitManifest(emitted), Encoding.UTF8));
            });
        }

        private static DiagnosticDescriptor DescriptorFor(string id) => id switch
        {
            "FGSG001" => NotPartialOrGetOnly,
            "FGSG002" => ClassNotPartial,
            "FGSG003" => ClassNotComponent,
            "FGSG004" => WideDelegate,
            _ => CollectionProp,
        };

        // Fully-qualified type spelling that keeps `global::` AND nullable reference annotations (so `string?` /
        // `Action?` round-trip exactly onto the partial property + the transport record).
        private static readonly SymbolDisplayFormat Fqn = SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        private static Model? Extract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

            var diags = ImmutableArray.CreateBuilder<Diag>();
            Location typeLoc = type.Locations.FirstOrDefault() ?? Location.None;

            bool isPartial = type.DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax(ct) is TypeDeclarationSyntax t && t.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
            if (!isPartial) diags.Add(new Diag("FGSG002", typeLoc, type.Name, null));

            bool derivesComponent = false;
            for (INamedTypeSymbol? b = type.BaseType; b is not null; b = b.BaseType)
                if (b.ToDisplayString() == ComponentBase) { derivesComponent = true; break; }
            if (!derivesComponent) diags.Add(new Diag("FGSG003", typeLoc, type.Name, null));

            var props = ImmutableArray.CreateBuilder<PropInfo>();
            foreach (ISymbol member in type.GetMembers())   // source order within the declaration
            {
                ct.ThrowIfCancellationRequested();
                if (member is not IPropertySymbol p) continue;
                if (!p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PropAttr)) continue;

                Location loc = p.Locations.FirstOrDefault() ?? typeLoc;
                if (!p.IsPartialDefinition || p.SetMethod is not null || p.GetMethod is null)
                {
                    diags.Add(new Diag("FGSG001", loc, p.Name, null));
                    continue;   // cannot implement it correctly — skip (its own unimplemented-partial error will show too)
                }

                string typeFqn = p.Type.ToDisplayString(Fqn);
                bool defaultable = p.Type.NullableAnnotation == NullableAnnotation.Annotated;
                string access = AccessibilityKeyword(p.DeclaredAccessibility);   // the partial impl MUST match the declaration

                if (p.Type is INamedTypeSymbol { TypeKind: TypeKind.Delegate } del && del.DelegateInvokeMethod is { } invoke)
                {
                    int argc = invoke.Parameters.Length;
                    bool wide = argc > MaxForwarderArgs;
                    if (wide) diags.Add(new Diag("FGSG004", loc, p.Name, MaxForwarderArgs.ToString()));
                    props.Add(PropInfo.Delegate(p.Name, typeFqn, access, defaultable, argc,
                        invoke.ReturnsVoid, invoke.ReturnsVoid ? "" : invoke.ReturnType.ToDisplayString(Fqn), wide));
                    continue;
                }

                if (IsUnstableCollection(p.Type, out string collLabel))
                    diags.Add(new Diag("FGSG005", loc, p.Name, collLabel));
                props.Add(PropInfo.Value(p.Name, typeFqn, access, defaultable));
            }

            bool emit = isPartial && derivesComponent && props.Count > 0;
            return new Model(
                type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                type.Name,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Sanitize(type.ToDisplayString()),
                props.ToImmutable(),
                diags.ToImmutable(),
                emit);
        }

        // Reference-compared BCL collection interfaces/types that a default-comparer signal cannot diff by value.
        // ImmutableArray<T> is deliberately excluded (value semantics). Arrays included.
        private static bool IsUnstableCollection(ITypeSymbol t, out string label)
        {
            label = "";
            if (t is IArrayTypeSymbol) { label = "array"; return true; }
            if (t is INamedTypeSymbol n && n.IsGenericType)
            {
                string open = n.ConstructedFrom.ToDisplayString();
                switch (open)
                {
                    case "System.Collections.Generic.List<T>":
                    case "System.Collections.Generic.IList<T>":
                    case "System.Collections.Generic.ICollection<T>":
                    case "System.Collections.Generic.IEnumerable<T>":
                    case "System.Collections.Generic.IReadOnlyList<T>":
                    case "System.Collections.Generic.IReadOnlyCollection<T>":
                    case "System.Collections.Generic.Dictionary<TKey, TValue>":
                    case "System.Collections.Generic.HashSet<T>":
                        label = open;
                        return true;
                }
            }
            return false;
        }

        private static string Emit(Model m)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/>\n// Generated by FluentGpu.SourceGen (Engine/PropsGenerator) — DO NOT EDIT.\n#nullable enable\n\n");
            bool hasNs = m.Namespace is not null;
            string pad = hasNs ? "    " : "";
            if (hasNs) sb.Append("namespace ").Append(m.Namespace).Append("\n{\n");

            sb.Append(pad).Append("partial class ").Append(m.TypeName).Append(" : global::FluentGpu.Hooks.IPropsHost\n").Append(pad).Append("{\n");
            string p2 = pad + "    ";

            // ── per-prop backing + accessors ──
            foreach (PropInfo p in m.Props)
            {
                if (!p.IsDelegate)
                {
                    sb.Append(p2).Append("private readonly global::FluentGpu.Signals.Signal<").Append(p.TypeFqn)
                      .Append("> ").Append(Field(p.Name)).Append(" = new(default!);\n");
                    sb.Append(p2).Append(p.Access).Append(" partial ").Append(p.TypeFqn).Append(' ').Append(p.Name)
                      .Append(" => ").Append(Field(p.Name)).Append(".Value;\n");
                    sb.Append(p2).Append("/// <summary>Bind-direct accessor for <c>").Append(p.Name)
                      .Append("</c> — forward this live channel into a child prop/node bind (compositor-only; no re-render).</summary>\n");
                    sb.Append(p2).Append(p.Access).Append(" global::FluentGpu.Signals.IReadSignal<").Append(p.TypeFqn)
                      .Append("> ").Append(p.Name).Append("Prop => ").Append(Field(p.Name)).Append(";\n\n");
                }
                else if (p.Wide)
                {
                    // >4 args: raw latest field (no stable forwarder — FGSG004). Still latest-write, still no re-render.
                    sb.Append(p2).Append("private ").Append(p.TypeFqn).Append(' ').Append(LatestField(p.Name)).Append(";\n");
                    sb.Append(p2).Append(p.Access).Append(" partial ").Append(p.TypeFqn).Append(' ').Append(p.Name)
                      .Append(" => ").Append(LatestField(p.Name)).Append(";\n\n");
                }
                else
                {
                    sb.Append(p2).Append("private ").Append(p.TypeFqn).Append(' ').Append(LatestField(p.Name)).Append(";\n");
                    sb.Append(p2).Append("private ").Append(p.TypeFqn).Append(' ').Append(FwdField(p.Name)).Append(";\n");
                    string args = ArgList(p.ArgCount);
                    string lam = p.ReturnsVoid
                        ? $"({args}) => {LatestField(p.Name)}?.Invoke({args})"
                        : $"({args}) => {LatestField(p.Name)} is null ? default({p.ReturnFqn})! : {LatestField(p.Name)}({args})";
                    sb.Append(p2).Append(p.Access).Append(" partial ").Append(p.TypeFqn).Append(' ').Append(p.Name)
                      .Append(" => ").Append(LatestField(p.Name)).Append(" is null ? null : (")
                      .Append(FwdField(p.Name)).Append(" ??= ").Append(lam).Append(");\n\n");
                }
            }

            // ── nested transport record ──
            sb.Append(p2).Append("/// <summary>Immutable transport for <c>").Append(m.TypeName)
              .Append("</c>'s re-pushed props (one positional per [Prop], declared order). Pass to <c>Embed.Comp(props, factory)</c>.</summary>\n");
            sb.Append(p2).Append("public sealed record PropsData(");
            for (int i = 0; i < m.Props.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(m.Props[i].TypeFqn).Append(' ').Append(m.Props[i].Name);
            }
            sb.Append(");\n\n");

            // ── the IPropsHost sink ──
            sb.Append(p2).Append("private object? __lastProps;\n");
            sb.Append(p2).Append("void global::FluentGpu.Hooks.IPropsHost.ApplyProps(object props)\n").Append(p2).Append("{\n");
            sb.Append(p2).Append("    if (props is not PropsData __p) return;\n");
            sb.Append(p2).Append("    if (object.ReferenceEquals(__lastProps, __p)) return;   // O(1) ref short-circuit (a memoized re-push)\n");
            sb.Append(p2).Append("    __lastProps = __p;\n");
            foreach (PropInfo p in m.Props)
            {
                if (!p.IsDelegate)
                    // Signal setter is equality-gated: only a CHANGED field notifies its subscribers.
                    sb.Append(p2).Append("    ").Append(Field(p.Name)).Append(".Value = __p.").Append(p.Name).Append(";\n");
                else
                    // Delegate: latest-write, no notification (a fresh lambda never re-renders; the forwarder invokes it).
                    sb.Append(p2).Append("    ").Append(LatestField(p.Name)).Append(" = __p.").Append(p.Name).Append(";\n");
            }
            sb.Append(p2).Append("}\n\n");

            // ── Of(...) factory ──
            int firstDefault = FirstDefaultableSuffix(m.Props);
            sb.Append(p2).Append("/// <summary>Embed this component with the given props re-pushed live (<c>Embed.Comp(new PropsData(...), () =&gt; new ")
              .Append(m.TypeName).Append("())</c>).</summary>\n");
            sb.Append(p2).Append("public static global::FluentGpu.Hooks.ComponentEl Of(");
            for (int i = 0; i < m.Props.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(m.Props[i].TypeFqn).Append(' ').Append(Camel(m.Props[i].Name));
                if (i >= firstDefault) sb.Append(" = default");
            }
            sb.Append(")\n");
            sb.Append(p2).Append("    => global::FluentGpu.Hooks.Embed.Comp(new PropsData(");
            for (int i = 0; i < m.Props.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Camel(m.Props[i].Name));
            }
            sb.Append("), static () => new ").Append(m.FqTypeName).Append("());\n\n");

            // ── snapshot helpers (Split/Merge story: SNAPSHOT path; the live path is the XxxProp accessors + record `with`) ──
            sb.Append(p2).Append("/// <summary>A SNAPSHOT of the current live props (no subscription). Documented COLLAPSE hazard: passing a\n");
            sb.Append(p2).Append("/// subset of this to a child freezes those fields at snapshot time — forward live via the typed <c>XxxProp</c>\n");
            sb.Append(p2).Append("/// accessors (bind-preserving) or a record <c>with</c> instead. See docs/guide/reactivity.md.</summary>\n");
            sb.Append(p2).Append("public PropsData CurrentProps() => new PropsData(");
            for (int i = 0; i < m.Props.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                PropInfo p = m.Props[i];
                sb.Append(p.IsDelegate ? LatestField(p.Name) : Field(p.Name) + ".Peek()");
            }
            sb.Append(");\n\n");
            sb.Append(p2).Append("/// <summary>Snapshot <paramref name=\"source\"/>'s current props (see <see cref=\"CurrentProps\"/> — same COLLAPSE hazard).</summary>\n");
            sb.Append(p2).Append("public static PropsData From(").Append(m.FqTypeName).Append(" source) => source.CurrentProps();\n");

            sb.Append(pad).Append("}\n");
            if (hasNs) sb.Append("}\n");
            return sb.ToString();
        }

        private static string EmitManifest(ImmutableArray<Model> models)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/>\n// Generated by FluentGpu.SourceGen (Engine/PropsGenerator) — [Props] skippability report.\n#nullable enable\n\n");
            sb.Append("namespace FluentGpu.Generated\n{\n");
            sb.Append("    /// <summary>Build-time [Props] skippability report (adjustment #13): per component, which props became\n");
            sb.Append("    /// signal-backed (re-render on change) vs delegate forwarders (latest-write, never re-render). Compile-time\n");
            sb.Append("    /// constant — greppable, zero runtime cost.</summary>\n");
            sb.Append("    [global::System.CodeDom.Compiler.GeneratedCode(\"FluentGpu.SourceGen\", \"1.0\")]\n");
            sb.Append("    internal static class PropsManifest\n    {\n");
            sb.Append("        public const string Report =\n");
            var lines = new List<string>();
            foreach (Model m in models.OrderBy(static x => x.FqTypeName, System.StringComparer.Ordinal))
            {
                var sig = m.Props.Where(static p => !p.IsDelegate).Select(static p => p.Name);
                var del = m.Props.Where(static p => p.IsDelegate)
                                 .Select(static p => p.Wide ? p.Name + "(raw)" : p.Name + "(fwd)");
                lines.Add($"{m.FqTypeName}: signals=[{string.Join(",", sig)}] delegates=[{string.Join(",", del)}]");
            }
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append("            ").Append(Quote(lines[i]));
                sb.Append(i == lines.Count - 1 ? ";\n" : " + \"\\n\" +\n");
            }
            sb.Append("    }\n}\n");
            return sb.ToString();
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────
        private static string Field(string name) => "_" + Camel(name) + "Prop";
        private static string LatestField(string name) => "_" + Camel(name) + "Latest";
        private static string FwdField(string name) => "_" + Camel(name) + "Forwarder";

        private static string ArgList(int n)
        {
            if (n == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(", "); sb.Append("__a").Append(i); }
            return sb.ToString();
        }

        // Index of the first parameter that may carry `= default`: the maximal TRAILING run of defaultable (nullable-
        // annotated) params (C# forbids an optional param before a required one). Returns Length when none qualify.
        private static int FirstDefaultableSuffix(ImmutableArray<PropInfo> props)
        {
            int i = props.Length;
            while (i > 0 && props[i - 1].Defaultable) i--;
            return i;
        }

        private static string Camel(string name) =>
            name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

        private static string AccessibilityKeyword(Accessibility a) => a switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Private => "private",
            _ => "internal",
        };

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
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

        private readonly record struct PropInfo(
            string Name, string TypeFqn, string Access, bool Defaultable, bool IsDelegate,
            int ArgCount, bool ReturnsVoid, string ReturnFqn, bool Wide)
        {
            public static PropInfo Value(string name, string typeFqn, string access, bool defaultable)
                => new(name, typeFqn, access, defaultable, false, 0, true, "", false);
            public static PropInfo Delegate(string name, string typeFqn, string access, bool defaultable, int argc, bool returnsVoid, string returnFqn, bool wide)
                => new(name, typeFqn, access, defaultable, true, argc, returnsVoid, returnFqn, wide);
        }

        private readonly record struct Diag(string Id, Location Location, string Arg0, string? Arg1);

        private readonly record struct Model(
            string? Namespace, string TypeName, string FqTypeName, string HintName,
            ImmutableArray<PropInfo> Props, ImmutableArray<Diag> Diagnostics, bool Emit);
    }
}
