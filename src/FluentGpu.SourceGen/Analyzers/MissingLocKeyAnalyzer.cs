using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using FluentGpu.SourceGen.Localization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FLLOC003 — a <c>Loc.Get</c>/<c>Format</c>/<c>Bind</c> string literal that is not in the base loc JSON (renders as
/// <c>[key]</c> at runtime). FLLOC005 — a base-JSON key that no literal and no <c>Strings.*</c> member references
/// (proposal to delete; Info, location on the JSON property).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingLocKeyAnalyzer : DiagnosticAnalyzer
{
    public const string MissingId = "FLLOC003";
    public const string UnusedId = "FLLOC005";

    private const string MetadataKey = "build_metadata.AdditionalFiles.FluentGpuLocBase";
    private const string PathConvention = "assets/loc/en-us.json";
    private const string AllowMarker = "loc-allow";

    private static readonly DiagnosticDescriptor MissingRule = new(
        id: MissingId,
        title: "Loc key is not in the base culture JSON",
        messageFormat: "The loc key '{0}' is not in the base culture JSON. Loc.Get will render [{0}].",
        category: "FluentGpu.Localization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnusedRule = new(
        id: UnusedId,
        title: "Loc key in the base JSON is unused",
        messageFormat: "The loc key '{0}' is not referenced by any Loc.* literal or Strings.* member. Delete it or add it to $unusedAllow.",
        category: "FluentGpu.Localization",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(MissingRule, UnusedRule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            AdditionalText? baseFile = null;
            foreach (AdditionalText text in start.Options.AdditionalFiles)
            {
                if (IsBase(text, start.Options.AnalyzerConfigOptionsProvider.GetOptions(text)))
                {
                    baseFile = text;
                    break;
                }
            }
            if (baseFile is null) return;

            string json = baseFile.GetText()?.ToString() ?? string.Empty;
            var pairs = TinyJsonReader.Parse(json, out _, out _);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in pairs) keys.Add(kv.Key);
            if (keys.Count == 0) return;

            var allow = new HashSet<string>(TinyJsonReader.ParseUnusedAllow(json), StringComparer.Ordinal);
            var used = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            start.RegisterSyntaxNodeAction(ctx => AnalyzeInvocation(ctx, keys, used), SyntaxKind.InvocationExpression);
            start.RegisterSyntaxNodeAction(ctx => AnalyzeLiteral(ctx, keys, used), SyntaxKind.StringLiteralExpression);
            start.RegisterSyntaxNodeAction(ctx => AnalyzeMember(ctx, keys, used), SyntaxKind.SimpleMemberAccessExpression);

            AdditionalText captured = baseFile;
            start.RegisterCompilationEndAction(end =>
            {
                var referenced = new HashSet<string>(used.Keys, StringComparer.Ordinal);
                foreach (string k in new List<string>(referenced))
                {
                    string sub = k + "Sub";
                    if (keys.Contains(sub)) referenced.Add(sub);
                }
                foreach (string key in keys)
                {
                    if (allow.Contains(key) || referenced.Contains(key)) continue;
                    end.ReportDiagnostic(Diagnostic.Create(UnusedRule, JsonKeyLocation(captured, key), key));
                }
            });
        });
    }

    internal static bool IsBase(AdditionalText text, AnalyzerConfigOptions opts)
    {
        if (opts.TryGetValue(MetadataKey, out string? v) &&
            v is not null &&
            v.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        string n = text.Path.Replace('\\', '/');
        return n.EndsWith(PathConvention, StringComparison.OrdinalIgnoreCase);
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context, HashSet<string> keys, ConcurrentDictionary<string, byte> used)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count == 0) return;
        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (symbol is null) return;
        string typeName = symbol.ContainingType?.Name ?? "";
        if (typeName is not ("Loc" or "Localization")) return;
        if (symbol.Name is not ("Get" or "Format" or "Bind" or "BindF")) return;

        var arg0 = invocation.ArgumentList.Arguments[0].Expression;
        if (arg0 is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
        {
            string key = lit.Token.ValueText;
            if (key.Length == 0) return;
            used.TryAdd(key, 0);
            if (keys.Contains(key) || HasAllowMarker(lit)) return;
            context.ReportDiagnostic(Diagnostic.Create(MissingRule, lit.GetLocation(), key));
        }
    }

    private static void AnalyzeLiteral(
        SyntaxNodeAnalysisContext context, HashSet<string> keys, ConcurrentDictionary<string, byte> used)
    {
        var lit = (LiteralExpressionSyntax)context.Node;
        if (IsStringsConstInitializer(lit, context.SemanticModel, context.CancellationToken)) return;
        string value = lit.Token.ValueText;
        if (value.Length == 0) return;
        if (keys.Contains(value)) used.TryAdd(value, 0);
    }

    private static bool IsStringsConstInitializer(
        LiteralExpressionSyntax lit, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (lit.Parent is not EqualsValueClauseSyntax) return false;
        SyntaxNode? n = lit.Parent.Parent;
        if (n is not VariableDeclaratorSyntax vd) return false;
        if (model.GetDeclaredSymbol(vd, ct) is not IFieldSymbol field) return false;
        for (INamedTypeSymbol? t = field.ContainingType; t is not null; t = t.ContainingType)
            if (t.Name == "Strings") return true;
        return false;
    }

    private static void AnalyzeMember(
        SyntaxNodeAnalysisContext context, HashSet<string> keys, ConcurrentDictionary<string, byte> used)
    {
        var access = (MemberAccessExpressionSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(access, context.CancellationToken).Symbol;
        if (symbol is null) return;
        if (!TryStringsKey(symbol, out string key)) return;
        if (keys.Contains(key)) used.TryAdd(key, 0);
    }

    internal static bool TryStringsKey(ISymbol symbol, out string key)
    {
        key = "";
        if (symbol is not (IFieldSymbol or IMethodSymbol or IPropertySymbol)) return false;
        var parts = new List<string>();
        for (ISymbol? s = symbol; s is not null; s = s.ContainingSymbol)
        {
            if (s is INamespaceSymbol) break;
            if (s is INamedTypeSymbol { Name: "Strings" })
            {
                if (parts.Count == 0) return false;
                parts.Reverse();
                string last = parts[parts.Count - 1];
                if (last.Length > 3 && last.EndsWith("Key", StringComparison.Ordinal))
                    parts[parts.Count - 1] = last.Substring(0, last.Length - 3);
                var sb = new StringBuilder();
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0) sb.Append('.');
                    sb.Append(ToCamel(parts[i]));
                }
                key = sb.ToString();
                return true;
            }
            if (s is INamedTypeSymbol or IFieldSymbol or IMethodSymbol or IPropertySymbol)
            {
                if (s.Name == ".ctor") continue;
                parts.Add(s.Name);
            }
        }
        return false;
    }

    private static string ToCamel(string pascal)
    {
        if (pascal.Length == 0) return pascal;
        return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
    }

    private static bool HasAllowMarker(LiteralExpressionSyntax lit)
    {
        var text = lit.SyntaxTree.GetText();
        int line = lit.GetLocation().GetLineSpan().StartLinePosition.Line;
        if (line < 0 || line >= text.Lines.Count) return false;
        return text.Lines[line].ToString().IndexOf(AllowMarker, StringComparison.Ordinal) >= 0;
    }

    private static Location JsonKeyLocation(AdditionalText file, string dottedKey)
    {
        SourceText? text = file.GetText();
        if (text is null) return Location.None;
        string leaf = dottedKey;
        int dot = dottedKey.LastIndexOf('.');
        if (dot >= 0) leaf = dottedKey.Substring(dot + 1);
        string needle = "\"" + leaf + "\"";
        string src = text.ToString();
        int i = src.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return Location.Create(file.Path, default, default);
        var span = new TextSpan(i, needle.Length);
        var line = text.Lines.GetLineFromPosition(i);
        var lineSpan = new LinePositionSpan(
            new LinePosition(line.LineNumber, i - line.Start),
            new LinePosition(line.LineNumber, i - line.Start + needle.Length));
        return Location.Create(file.Path, span, lineSpan);
    }
}
