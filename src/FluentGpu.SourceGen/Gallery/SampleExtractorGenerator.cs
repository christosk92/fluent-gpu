using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.SourceGen.Gallery
{
    /// <summary>
    /// Never-drift sample generator (master plan §WS7 W7.1). For every
    /// <c>[FluentGpu.GalleryKit.Sample("title")]</c>-attributed <c>static</c> method in a partial class — either
    /// <c>(Knobs k) =&gt; Element</c> or <c>() =&gt; Element</c> — it extracts the method's VERBATIM (dedented) body text
    /// at compile time and emits, into the same partial type, a
    /// <c>public static readonly Sample {Method}Sample = new(title, description, "&lt;body&gt;", factory)</c>. The
    /// displayed code IS the compiled code by construction. A <c>()</c> method is wrapped as
    /// <c>(Knobs _) =&gt; M()</c>. No runtime cost (string constant + a static method-group delegate).
    ///
    /// <para>Diagnostics enforce the contract at each method: <c>FGG001</c> (not <c>static</c>), <c>FGG002</c> (wrong
    /// return type or parameters), <c>FGG003</c> (container is not <c>partial</c>).</para>
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class SampleExtractorGenerator : IIncrementalGenerator
    {
        private const string SampleAttr = "FluentGpu.GalleryKit.SampleAttribute";
        private const string ElementType = "FluentGpu.Dsl.Element";
        private const string KnobsType = "FluentGpu.GalleryKit.Knobs";

        private static readonly DiagnosticDescriptor NotStatic = new(
            "FGG001", "[Sample] method must be static",
            "[Sample] method '{0}' must be static — a sample factory is invoked without an instance.",
            "FluentGpu.Gallery", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor WrongShape = new(
            "FGG002", "[Sample] method has the wrong signature",
            "[Sample] method '{0}' must return FluentGpu.Dsl.Element and take either no parameters or a single "
            + "FluentGpu.GalleryKit.Knobs parameter.",
            "FluentGpu.Gallery", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor NotPartial = new(
            "FGG003", "[Sample] container must be partial",
            "[Sample] method '{0}' is in a type that is not declared 'partial' — the generated sample constant is "
            + "emitted into the same partial type, so the container (and any enclosing type) must be partial.",
            "FluentGpu.Gallery", DiagnosticSeverity.Error, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(
                    SampleAttr,
                    predicate: static (node, _) => node is MethodDeclarationSyntax,
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
                foreach (var group in all.Where(static m => m.Emit).GroupBy(static m => m.ContainerFqName, StringComparer.Ordinal))
                {
                    var members = group.ToImmutableArray();
                    string src = EmitContainer(members[0], members);
                    spc.AddSource(HintName(group.Key), SourceText.From(src, Encoding.UTF8));
                }
            });
        }

        private static DiagnosticDescriptor DescriptorFor(string id) => id switch
        {
            "FGG001" => NotStatic,
            "FGG002" => WrongShape,
            _ => NotPartial,
        };

        private static SampleModel? Extract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            if (ctx.TargetSymbol is not IMethodSymbol method) return null;
            if (ctx.TargetNode is not MethodDeclarationSyntax methodSyntax) return null;
            INamedTypeSymbol container = method.ContainingType;

            var diags = ImmutableArray.CreateBuilder<Diag>();
            Location loc = method.Locations.FirstOrDefault() ?? Location.None;

            if (!method.IsStatic) diags.Add(new Diag("FGG001", loc, method.Name));

            bool returnsElement = method.ReturnType.ToDisplayString() == ElementType;
            bool okParams = method.Parameters.Length == 0
                            || (method.Parameters.Length == 1 && method.Parameters[0].Type.ToDisplayString() == KnobsType);
            if (!returnsElement || !okParams) diags.Add(new Diag("FGG002", loc, method.Name));

            // Every enclosing type (the container and its ancestors) must be partial, or the emitted partial won't merge.
            bool allPartial = true;
            for (INamedTypeSymbol? t = container; t is not null; t = t.ContainingType)
            {
                bool anyDecl = false, thisPartial = true;
                foreach (SyntaxReference sr in t.DeclaringSyntaxReferences)
                {
                    anyDecl = true;
                    if (sr.GetSyntax(ct) is TypeDeclarationSyntax tds && !tds.Modifiers.Any(SyntaxKind.PartialKeyword))
                        thisPartial = false;
                }
                if (anyDecl && !thisPartial) { allPartial = false; break; }
            }
            if (!allPartial) diags.Add(new Diag("FGG003", loc, method.Name));

            // Attribute data: ctor arg 0 = title; named Description.
            AttributeData attr = ctx.Attributes[0];
            string title = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string ts ? ts : method.Name;
            string? description = attr.NamedArguments.FirstOrDefault(a => a.Key == "Description").Value.Value as string;

            string code = ExtractBody(methodSyntax, ct);

            // Factory: (Knobs) methods are a method group; () methods wrap to discard the Knobs argument.
            bool takesKnobs = method.Parameters.Length == 1;
            var container2 = BuildContainer(container);

            bool emit = diags.Count == 0;
            return new SampleModel(
                container.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                container2.Namespace, container2.Chain,
                method.Name, takesKnobs, title, description, code,
                diags.ToImmutable(), emit);
        }

        private static string ExtractBody(MethodDeclarationSyntax method, CancellationToken ct)
        {
            SourceText text = method.SyntaxTree.GetText(ct);
            string raw;
            if (method.Body is BlockSyntax block)
                raw = text.ToString(TextSpan.FromBounds(block.OpenBraceToken.Span.End, block.CloseBraceToken.SpanStart));
            else if (method.ExpressionBody is ArrowExpressionClauseSyntax arrow)
                raw = arrow.Expression.ToString();
            else
                raw = "";
            return Dedent(raw);
        }

        // Strip the common leading indentation and any leading/trailing blank lines — verbatim otherwise.
        private static string Dedent(string s)
        {
            var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0) return "";

            int min = int.MaxValue;
            foreach (string line in lines)
            {
                if (line.Trim().Length == 0) continue;
                int indent = 0;
                while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
                if (indent < min) min = indent;
            }
            if (min is int.MaxValue or 0) return string.Join("\n", lines);
            for (int i = 0; i < lines.Count; i++)
                lines[i] = lines[i].Length >= min ? lines[i].Substring(min) : lines[i].TrimStart(' ', '\t');
            return string.Join("\n", lines);
        }

        private static (string? Namespace, ImmutableArray<(string Keyword, string Name)> Chain) BuildContainer(INamedTypeSymbol type)
        {
            var chain = new List<(string, string)>();
            for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            {
                string keyword = t.IsRecord ? "record" : t.TypeKind == TypeKind.Struct ? "struct" : "class";
                // Name including generic arity (e.g. Widget<T>) — reconstruct type parameters if any.
                string name = t.TypeParameters.Length == 0
                    ? t.Name
                    : t.Name + "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">";
                chain.Insert(0, (keyword, name));
            }
            string? ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
            return (ns, chain.ToImmutableArray());
        }

        private static string EmitContainer(SampleModel any, ImmutableArray<SampleModel> members)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/>\n// Generated by FluentGpu.SourceGen (Gallery/SampleExtractorGenerator) — DO NOT EDIT.\n#nullable enable\n\n");

            int indent = 0;
            string Pad() => new string(' ', indent * 4);

            if (any.Namespace is not null)
            {
                sb.Append("namespace ").Append(any.Namespace).Append("\n{\n");
                indent++;
            }
            foreach (var (keyword, name) in any.Chain)
            {
                sb.Append(Pad()).Append("partial ").Append(keyword).Append(' ').Append(name).Append("\n").Append(Pad()).Append("{\n");
                indent++;
            }

            foreach (SampleModel m in members.OrderBy(static x => x.MethodName, StringComparer.Ordinal))
            {
                string factory = m.TakesKnobs
                    ? m.MethodName
                    : "static (global::FluentGpu.GalleryKit.Knobs _) => " + m.MethodName + "()";
                string desc = m.Description is null ? "null" : Quote(m.Description);
                sb.Append(Pad()).Append("public static readonly global::FluentGpu.GalleryKit.Sample ")
                  .Append(m.MethodName).Append("Sample = new global::FluentGpu.GalleryKit.Sample(")
                  .Append(Quote(m.Title)).Append(", ").Append(desc).Append(", ").Append(Quote(m.Code)).Append(", ")
                  .Append(factory).Append(");\n");
            }

            for (int i = 0; i < any.Chain.Length; i++) { indent--; sb.Append(Pad()).Append("}\n"); }
            if (any.Namespace is not null) sb.Append("}\n");
            return sb.ToString();
        }

        private static string HintName(string fqName)
        {
            var sb = new StringBuilder(fqName.Length + 16);
            foreach (char c in fqName)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            sb.Append(".Samples.g.cs");
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
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            sb.Append('"');
            return sb.ToString();
        }

        private readonly record struct Diag(string Id, Location Location, string Arg0);

        private readonly record struct SampleModel(
            string ContainerFqName, string? Namespace, ImmutableArray<(string Keyword, string Name)> Chain,
            string MethodName, bool TakesKnobs, string Title, string? Description, string Code,
            ImmutableArray<Diag> Diagnostics, bool Emit);
    }
}
