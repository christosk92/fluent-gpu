using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.Validation.SourceGen
{
    /// <summary>
    /// An <see cref="IIncrementalGenerator"/> that lowers a <c>[FluentGpu.Forms.Validatable] partial</c> type into a
    /// generated partial holding a <c>static readonly Validator&lt;T&gt;[]</c> for each annotated member — so an app
    /// declares rules once on a model and consumes them with <c>UseField(signal, MyForm.Email)</c>.
    ///
    /// <para>Pure ergonomics over the hand-written <c>Rules.*</c> path: it emits only static arrays of <c>Rules.*</c>
    /// calls (the IDENTICAL runtime contract), so there is zero reflection, zero DataAnnotations, and nothing new at
    /// runtime. Maps: [Required]/[MinLength]/[MaxLength]/[RegexMatch] on a <c>string</c> member; [Range] on a
    /// <c>double</c> member. A member with no recognized attribute (or a type/attribute mismatch) is simply skipped.</para>
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class ValidatorGenerator : IIncrementalGenerator
    {
        private const string ValidatableAttr = "FluentGpu.Forms.ValidatableAttribute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(
                    ValidatableAttr,
                    predicate: static (_, _) => true,
                    transform: static (ctx, ct) => Extract(ctx, ct))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);

            context.RegisterSourceOutput(models, static (spc, model) =>
                spc.AddSource(model.HintName + ".Validators.g.cs", SourceText.From(Emit(model), Encoding.UTF8)));
        }

        private static Model? Extract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

            var members = ImmutableArray.CreateBuilder<MemberRules>();
            foreach (ISymbol member in type.GetMembers())
            {
                ct.ThrowIfCancellationRequested();
                ITypeSymbol? memberType = member switch
                {
                    IPropertySymbol p => p.Type,
                    IFieldSymbol { AssociatedSymbol: null } f => f.Type,   // skip backing fields
                    _ => null,
                };
                if (memberType is null) continue;

                var rules = ImmutableArray.CreateBuilder<string>();
                foreach (AttributeData attr in member.GetAttributes())
                {
                    string? rule = MapAttribute(attr, memberType);
                    if (rule is not null) rules.Add(rule);
                }
                if (rules.Count > 0)
                    members.Add(new MemberRules(member.Name, TypeName(memberType), rules.ToImmutable()));
            }
            if (members.Count == 0) return null;

            return new Model(
                type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                TypeKeyword(type),
                type.Name,
                Sanitize(type.ToDisplayString()),
                members.ToImmutable());
        }

        private static string? MapAttribute(AttributeData attr, ITypeSymbol memberType)
        {
            string? name = attr.AttributeClass?.ToDisplayString();
            string tn = TypeName(memberType);
            bool isString = tn == "string" || tn == "string?";
            bool isDouble = tn == "double";
            string Key(string def) => Q(NamedString(attr, "LocKey") ?? def);

            return name switch
            {
                "FluentGpu.Forms.RequiredAttribute" when isString =>
                    $"global::FluentGpu.Forms.Rules.Required({Key("validation.required")})",
                "FluentGpu.Forms.MinLengthAttribute" when isString =>
                    $"global::FluentGpu.Forms.Rules.MinLength({CtorInt(attr, 0)}, {Key("validation.minlen")})",
                "FluentGpu.Forms.MaxLengthAttribute" when isString =>
                    $"global::FluentGpu.Forms.Rules.MaxLength({CtorInt(attr, 0)}, {Key("validation.maxlen")})",
                "FluentGpu.Forms.RangeAttribute" when isDouble =>
                    $"global::FluentGpu.Forms.Rules.Range({Dbl(CtorDouble(attr, 0))}, {Dbl(CtorDouble(attr, 1))}, {Key("validation.range")})",
                "FluentGpu.Forms.RegexMatchAttribute" when isString =>
                    $"global::FluentGpu.Forms.Rules.Matches(new global::System.Text.RegularExpressions.Regex({Q(CtorString(attr, 0))}), {Q(CtorString(attr, 1))})",
                _ => null,
            };
        }

        private static string Emit(Model m)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/>\n// Generated by FluentGpu.Validation.SourceGen — DO NOT EDIT.\n#nullable enable\n\n");
            bool hasNs = m.Namespace is not null;
            if (hasNs) sb.Append("namespace ").Append(m.Namespace).Append("\n{\n");

            string pad = hasNs ? "    " : "";
            sb.Append(pad).Append("partial ").Append(m.TypeKeyword).Append(' ').Append(m.TypeName).Append("\n").Append(pad).Append("{\n");
            // Nested in a `Validators` class so a generated array does not collide with the annotated member of the same
            // name. Usage: UseField(signal, MyForm.Validators.Email).
            sb.Append(pad).Append("    /// <summary>Generated [Validatable] rule sets — pass to UseField, e.g. <c>UseField(sig, ")
              .Append(m.TypeName).Append(".Validators.Member)</c>.</summary>\n");
            sb.Append(pad).Append("    public static class Validators\n").Append(pad).Append("    {\n");
            foreach (MemberRules mr in m.Members)
            {
                sb.Append(pad).Append("        /// <summary>Validators for <c>").Append(mr.Name).Append("</c>.</summary>\n");
                sb.Append(pad).Append("        public static readonly global::FluentGpu.Forms.Validator<")
                  .Append(mr.TypeName).Append(">[] ").Append(mr.Name).Append(" =\n");
                sb.Append(pad).Append("        {\n");
                foreach (string rule in mr.Rules)
                    sb.Append(pad).Append("            ").Append(rule).Append(",\n");
                sb.Append(pad).Append("        };\n");
            }
            sb.Append(pad).Append("    }\n");
            sb.Append(pad).Append("}\n");
            if (hasNs) sb.Append("}\n");
            return sb.ToString();
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────
        private static string TypeName(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        private static string TypeKeyword(INamedTypeSymbol t) =>
            t.IsRecord ? (t.IsValueType ? "record struct" : "record")
            : t.IsValueType ? "struct" : "class";

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private static string? NamedString(AttributeData a, string name)
        {
            foreach (var kv in a.NamedArguments)
                if (kv.Key == name && kv.Value.Value is string s) return s;
            return null;
        }

        private static int CtorInt(AttributeData a, int i) =>
            a.ConstructorArguments.Length > i && a.ConstructorArguments[i].Value is int v ? v : 0;

        private static double CtorDouble(AttributeData a, int i)
        {
            if (a.ConstructorArguments.Length <= i) return 0;
            object? v = a.ConstructorArguments[i].Value;
            return v switch { double d => d, float f => f, int n => n, long l => l, _ => 0 };
        }

        private static string CtorString(AttributeData a, int i) =>
            a.ConstructorArguments.Length > i && a.ConstructorArguments[i].Value is string s ? s : "";

        private static string Dbl(double d)
        {
            if (double.IsNaN(d)) return "double.NaN";
            if (double.IsPositiveInfinity(d)) return "double.PositiveInfinity";
            if (double.IsNegativeInfinity(d)) return "double.NegativeInfinity";
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            // ensure a double literal (int-looking values implicitly convert, but be explicit + unambiguous)
            return s.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0 ? s : s + ".0";
        }

        private static string Q(string s)
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

        private readonly record struct MemberRules(string Name, string TypeName, ImmutableArray<string> Rules);

        private readonly record struct Model(
            string? Namespace, string TypeKeyword, string TypeName, string HintName, ImmutableArray<MemberRules> Members);
    }
}
