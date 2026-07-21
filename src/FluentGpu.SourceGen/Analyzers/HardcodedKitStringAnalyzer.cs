using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FGRP008 — a hardcoded user-facing string in the control kit. The control kit (FluentGpu.Controls) is a shipped,
/// localizable SDK: every user-facing literal must route through a loc-key lookup (<c>Loc.Bind</c>/<c>Loc.Get</c>/
/// <c>Loc.Format</c> or the generated <c>Strings.*</c> keys) so an app can translate it and it re-resolves on a culture
/// change. This rule flags a bare <see langword="string"/> LITERAL flowing into a user-facing text sink:
/// <list type="bullet">
/// <item>the first constructor argument of <c>TextEl</c> (<c>new TextEl("Cut")</c>);</item>
/// <item>an assignment to a <c>Text</c> member (<c>x.Text = "Cut"</c> or <c>new TextEl { Text = "Cut" }</c>);</item>
/// <item>an assignment to an <c>AutomationName</c> member (the spoken accessibility label).</item>
/// </list>
///
/// <para><b>Assembly-scoped.</b> The shared analyzer assembly is referenced by every project, so this rule only arms
/// itself when the compilation IS the control kit (<c>AssemblyName == "FluentGpu.Controls"</c>) — app/gallery code is
/// free to hardcode strings.</para>
///
/// <para><b>Allow-list.</b> A literal is NOT flagged when it (a) is empty/whitespace, (b) contains no ASCII letter — a
/// glyph, codepoint, format specifier, separator, or number (<c>""</c>, <c>"{0}"</c>, <c>"16:9"</c>, <c>"·"</c>), or
/// (c) sits on a line carrying a <c>// loc-allow</c> marker comment (the deliberate-literal escape hatch: font family
/// names, AutomationRole identifiers, format templates, test scaffolds). Non-literal expressions (a <c>Strings.*</c>
/// const, a <c>Loc.*</c> call, an interpolation, a variable) are never flagged.</para>
///
/// <para>Severity <see cref="DiagnosticSeverity.Warning"/> (heuristic — never a build break; the kit keeps it at zero
/// as the extraction's standing gate).</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HardcodedKitStringAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGRP008";

    /// <summary>The one assembly this rule arms itself in. The shared analyzer is referenced everywhere; only the kit is
    /// held to the no-hardcoded-string contract.</summary>
    private const string KitAssemblyName = "FluentGpu.Controls";

    /// <summary>The line marker that opts a deliberate literal out of the rule.</summary>
    private const string AllowMarker = "loc-allow";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Hardcoded user-facing string in the control kit",
        messageFormat: "The literal {0} is user-facing text in the control kit — route it through a loc key "
                     + "(Loc.Bind(Strings.…) for a TextEl, or Loc.Get/Loc.Format) so it can be translated and "
                     + "re-resolves on a culture change. Add a '// loc-allow' comment if the literal is deliberate "
                     + "(a glyph, format string, or AutomationRole identifier).",
        category: "FluentGpu.Localization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "FluentGpu.Controls is a localizable SDK: user-facing strings must resolve through the loc pillar "
                   + "(generated Strings keys + Localization). See docs/guide/localizing-the-control-kit.md.",
        helpLinkUri: "https://github.com/christosk92/fluent-gpu/blob/main/docs/guide/localizing-the-control-kit.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            // Arm only inside the control kit. The shared analyzer assembly is referenced by every project.
            if (start.Compilation.AssemblyName != KitAssemblyName)
                return;
            start.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
            start.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        });
    }

    // `x.Text = "…"`, `new T { Text = "…" }`, `AutomationName = "…"`.
    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Right is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.StringLiteralExpression))
            return;

        string? member = assignment.Left switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,   // x.Text = "…"
            IdentifierNameSyntax id => id.Identifier.ValueText,                 // Text = "…"  (object initializer)
            _ => null,
        };
        if (member is not ("Text" or "AutomationName"))
            return;

        Report(context, lit);
    }

    // `new TextEl("…")` — the first positional argument is the text.
    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments.Count: > 0 } args)
            return;
        if (args.Arguments[0].Expression is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.StringLiteralExpression))
            return;

        // Confirm the constructed type is TextEl (bind semantically; cheap syntactic types like `new BoxEl(...)` skip).
        ITypeSymbol? type = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type;
        if (!AnalyzerSemantics.IsTextEl(type))
            return;

        Report(context, lit);
    }

    private static void Report(SyntaxNodeAnalysisContext context, LiteralExpressionSyntax lit)
    {
        string value = lit.Token.ValueText;
        if (IsAllowed(value) || HasAllowMarker(lit))
            return;
        context.ReportDiagnostic(Diagnostic.Create(Rule, lit.GetLocation(), lit.Token.Text));
    }

    /// <summary>Auto-allowed literals: empty/whitespace, or containing no ASCII letter (glyph / codepoint / format
    /// specifier / separator / number). A letter-bearing literal is real copy and must be extracted or marked.</summary>
    private static bool IsAllowed(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        foreach (char c in value)
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                return false;
        return true;
    }

    /// <summary>True when the literal's line carries a <c>// loc-allow</c> marker (deliberate-literal escape hatch).</summary>
    private static bool HasAllowMarker(LiteralExpressionSyntax lit)
    {
        var text = lit.SyntaxTree.GetText();
        int line = lit.GetLocation().GetLineSpan().StartLinePosition.Line;
        if (line < 0 || line >= text.Lines.Count) return false;
        return text.Lines[line].ToString().Contains(AllowMarker);
    }
}
