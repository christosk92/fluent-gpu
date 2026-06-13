using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.SourceGen.Localization
{
    /// <summary>
    /// An <see cref="IIncrementalGenerator"/> that emits compile-safe localization keys from the base-culture JSON.
    ///
    /// <para>It reads the base-culture resource (e.g. <c>assets/loc/en-US.json</c>) supplied as an <c>AdditionalFiles</c>
    /// item, flattens its nested objects to dotted keys, and emits a nested <c>static partial class Strings</c> mirroring
    /// the JSON namespaces. Each leaf becomes a <c>const string</c> whose VALUE is the raw dotted key
    /// (<c>Strings.App.Title == "app.title"</c>) — so call sites use <c>L(Strings.Player.Queue)</c> instead of a raw
    /// string (typo-proof, refactor-safe, IntelliSense). A parameterized key additionally gets a typed format method
    /// (<c>Strings.Player.Added(track, playlist)</c>) routing to <c>Loc.Format("player.added", ("track", track), ...)</c>
    /// so a wrong/missing placeholder is a COMPILE error.</para>
    ///
    /// <para>It is AOT-clean by construction: it emits only <c>const string</c> fields and plain static methods that call
    /// the existing <c>Loc.Format</c> — zero runtime reflection, zero satellite assemblies. The generator itself is
    /// dependency-free (a hand-rolled JSON scanner, no System.Text.Json) so it loads cleanly in the netstandard2.0
    /// analyzer host.</para>
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class LocalizationKeysGenerator : IIncrementalGenerator
    {
        // The MSBuild item metadata that marks an AdditionalFiles entry as the base loc resource:
        //   <AdditionalFiles Include="assets\loc\en-US.json" FluentGpuLocBase="true" />
        // surfaced to the analyzer via <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="FluentGpuLocBase" />.
        private const string MetadataKey = "build_metadata.AdditionalFiles.FluentGpuLocBase";

        // Belt-and-suspenders fallback: if the metadata plumbing misbehaves on a given SDK, any AdditionalText whose path
        // ends with this convention is treated as the base resource.
        private const string PathConvention = "assets/loc/en-us.json";

        // The namespace + property to override the emitted namespace from MSBuild, if a consumer ever wants to:
        //   <CompilerVisibleProperty Include="FluentGpuLocNamespace" /> + <FluentGpuLocNamespace>X</FluentGpuLocNamespace>
        private const string NamespaceProperty = "build_property.FluentGpuLocNamespace";
        private const string DefaultNamespace = "FluentGpu.WindowsApp";

        private static readonly DiagnosticDescriptor MalformedJson = new(
            id: "FLLOC001",
            title: "Localization base JSON could not be fully parsed",
            messageFormat: "The localization base resource '{0}' could not be fully parsed ({1}); an empty Strings class was emitted",
            category: "FluentGpu.Localization",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor NoBaseResource = new(
            id: "FLLOC002",
            title: "No localization base resource found",
            messageFormat: "No AdditionalFiles entry was marked FluentGpuLocBase=\"true\" (and none matched the 'assets/loc/en-US.json' convention); the Strings class is empty",
            category: "FluentGpu.Localization",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // (text, isBase) pairs for every AdditionalText, deciding base-membership from metadata OR the path convention.
            var baseTexts = context.AdditionalTextsProvider
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Select(static (pair, ct) =>
                {
                    AdditionalText text = pair.Left;
                    var opts = pair.Right.GetOptions(text);
                    bool markedBase = opts.TryGetValue(MetadataKey, out string? v) &&
                                      v is not null &&
                                      (v.Equals("true", System.StringComparison.OrdinalIgnoreCase));
                    bool conventionBase = NormalizeSlashes(text.Path).EndsWith(PathConvention, System.StringComparison.OrdinalIgnoreCase);
                    bool isBase = markedBase || conventionBase;
                    if (!isBase) return default(BaseFile?);
                    SourceText? src = text.GetText(ct);
                    return new BaseFile(text.Path, src?.ToString() ?? string.Empty);
                })
                .Where(static x => x is not null)
                .Select(static (x, _) => x!.Value);

            // The (optional) namespace override property.
            var nsProvider = context.AnalyzerConfigOptionsProvider.Select(static (p, _) =>
                p.GlobalOptions.TryGetValue(NamespaceProperty, out string? ns) && !string.IsNullOrWhiteSpace(ns)
                    ? ns!.Trim()
                    : DefaultNamespace);

            var collected = baseTexts.Collect().Combine(nsProvider);

            context.RegisterSourceOutput(collected, static (spc, tuple) =>
            {
                ImmutableArray<BaseFile> files = tuple.Left;
                string ns = tuple.Right;

                if (files.IsDefaultOrEmpty)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(NoBaseResource, Location.None));
                    spc.AddSource("Strings.g.cs", EmitEmpty(ns));
                    return;
                }

                // Deterministic: pick the first base file by path order.
                BaseFile chosen = files.OrderBy(f => f.Path, System.StringComparer.Ordinal).First();

                var pairs = TinyJsonReader.Parse(chosen.Content, out bool hadError, out string? error);
                if (hadError)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(MalformedJson, Location.None, chosen.Path, error ?? "unknown error"));
                    // Still emit whatever parsed cleanly before the error (graceful, not a build break).
                }

                string source = Emit(ns, pairs);
                spc.AddSource("Strings.g.cs", SourceText.From(source, Encoding.UTF8));
            });
        }

        // A tree node: either an intermediate namespace (Children) or a leaf key (DottedKey set + placeholder list).
        private sealed class Node
        {
            public readonly string Segment;
            public readonly SortedDictionary<string, Node> Children = new(System.StringComparer.Ordinal);
            public string? DottedKey;            // non-null for a leaf
            public List<string>? Placeholders;   // non-null+non-empty for a parameterized leaf
            public Node(string segment) => Segment = segment;
        }

        private static string Emit(string ns, List<KeyValuePair<string, string>> pairs)
        {
            var root = new Node(string.Empty);
            foreach (var kv in pairs)
            {
                string dotted = kv.Key;
                string[] segs = dotted.Split('.');
                Node cur = root;
                for (int i = 0; i < segs.Length; i++)
                {
                    string seg = segs[i];
                    if (seg.Length == 0) continue;
                    if (!cur.Children.TryGetValue(seg, out Node? child))
                    {
                        child = new Node(seg);
                        cur.Children[seg] = child;
                    }
                    cur = child;
                }
                // The terminal node is the leaf for this key. If a key collides with an existing intermediate node
                // (e.g. both "a.b" and "a.b.c" exist) we still mark the leaf; the intermediate keeps its children.
                cur.DottedKey = dotted;
                var ph = IcuPlaceholders.Collect(kv.Value);
                if (ph.Count > 0) cur.Placeholders = ph;
            }

            var sb = new StringBuilder();
            sb.Append(Header());
            sb.Append("namespace ").Append(ns).Append(";\n\n");
            sb.Append("/// <summary>Compile-safe localization keys generated from the base-culture loc JSON (en-US.json).\n");
            sb.Append("/// Each leaf <c>const string</c> equals its dotted key (<c>Strings.App.Title == \"app.title\"</c>); a\n");
            sb.Append("/// parameterized key also exposes a typed format method routing to <c>Loc.Format</c>.</summary>\n");
            sb.Append("public static partial class Strings\n{\n");
            foreach (var child in root.Children.Values)
                EmitNode(sb, child, indent: 1);
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void EmitNode(StringBuilder sb, Node node, int indent)
        {
            string pad = new string(' ', indent * 4);
            string id = Identifiers.ToPascal(node.Segment);

            // A leaf with no children → const (+ optional typed method).
            bool isLeaf = node.DottedKey is not null;
            bool hasChildren = node.Children.Count > 0;

            if (isLeaf && !hasChildren)
            {
                EmitLeaf(sb, node, pad, id);
                return;
            }

            // Intermediate namespace node → nested static class. (If it is ALSO a leaf — a key that is a prefix of
            // another — we emit the const inside the class with a sanitized name to avoid colliding with the class.)
            sb.Append(pad).Append("public static class ").Append(id).Append("\n").Append(pad).Append("{\n");
            if (isLeaf)
            {
                // Rare prefix-collision case: emit the self key as a "_Self" const so it stays reachable.
                sb.Append(pad).Append("    /// <summary>The dotted key for this namespace node itself.</summary>\n");
                sb.Append(pad).Append("    public const string _Self = ").Append(Quote(node.DottedKey!)).Append(";\n");
            }
            foreach (var child in node.Children.Values)
                EmitNode(sb, child, indent + 1);
            sb.Append(pad).Append("}\n");
        }

        private static void EmitLeaf(StringBuilder sb, Node node, string pad, string id)
        {
            string key = node.DottedKey!;
            bool parameterized = node.Placeholders is { Count: > 0 };

            if (!parameterized)
            {
                // Plain key: a bare-name const equal to the dotted key.
                sb.Append(pad).Append("/// <summary>Key <c>").Append(Escape(key)).Append("</c>.</summary>\n");
                sb.Append(pad).Append("public const string ").Append(id).Append(" = ").Append(Quote(key)).Append(";\n");
                return;
            }

            // Parameterized key: a method/const cannot share a name, so the KEY const gets a "Key" suffix and the
            // typed format method keeps the bare Pascal name.
            List<string> ph = node.Placeholders!;
            sb.Append(pad).Append("/// <summary>Key <c>").Append(Escape(key)).Append("</c> (parameterized; use the ")
              .Append(id).Append("(...) method for typed formatting).</summary>\n");
            sb.Append(pad).Append("public const string ").Append(id).Append("Key = ").Append(Quote(key)).Append(";\n");

            // The typed format method.
            sb.Append(pad).Append("/// <summary>Formats <c>").Append(Escape(key)).Append("</c> with its named placeholders");
            sb.Append(" (").Append(string.Join(", ", ph.Select(Escape))).Append(").</summary>\n");
            sb.Append(pad).Append("public static string ").Append(id).Append("(");
            for (int i = 0; i < ph.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("object ").Append(Identifiers.ToParam(ph[i]));
            }
            sb.Append(") => global::FluentGpu.Localization.Loc.Format(").Append(Quote(key));
            for (int i = 0; i < ph.Count; i++)
            {
                sb.Append(", (").Append(Quote(ph[i])).Append(", ").Append(Identifiers.ToParam(ph[i])).Append(")");
            }
            sb.Append(");\n");
        }

        private static string EmitEmpty(string ns)
        {
            var sb = new StringBuilder();
            sb.Append(Header());
            sb.Append("namespace ").Append(ns).Append(";\n\n");
            sb.Append("/// <summary>Compile-safe localization keys (none found — no base resource).</summary>\n");
            sb.Append("public static partial class Strings\n{\n}\n");
            return sb.ToString();
        }

        private static string Header() =>
            "// <auto-generated/>\n" +
            "// Generated by FluentGpu.SourceGen (Localization) — DO NOT EDIT.\n" +
            "#nullable enable\n\n";

        private static string Quote(string s) => "\"" + Escape(s) + "\"";

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string NormalizeSlashes(string path) => path.Replace('\\', '/');

        // A base-resource file captured as a value (path + content) — equatable so the incremental pipeline caches it.
        private readonly struct BaseFile : System.IEquatable<BaseFile>
        {
            public readonly string Path;
            public readonly string Content;
            public BaseFile(string path, string content) { Path = path; Content = content; }
            public bool Equals(BaseFile other) => Path == other.Path && Content == other.Content;
            public override bool Equals(object? obj) => obj is BaseFile o && Equals(o);
            public override int GetHashCode() => unchecked((Path?.GetHashCode() ?? 0) * 397 ^ (Content?.GetHashCode() ?? 0));
        }
    }
}
