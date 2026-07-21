using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FGRP004 — a discarded element expression. <c>Element</c> is an immutable record: every DSL modifier
/// (<c>box.Rounded(8)</c>, a <c>with</c> clone, <c>Ui.Text(...)</c>) RETURNS a new element and mutates nothing. An
/// expression statement whose value is an <c>Element</c> — and is not an assignment — therefore throws that new
/// element away, which is always a mistake (the author expected an in-place mutation). Flags the invocation- and
/// object-creation-statement forms; assignments (<c>x = box.Rounded(8);</c>) are correctly excluded.
/// <para>Severity <see cref="DiagnosticSeverity.Warning"/>.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedElementAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGRP004";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Result of an element expression is discarded",
        messageFormat: "This expression produces an Element ('{0}') that is immediately discarded. Element records are "
                     + "immutable — a modifier returns a NEW element and mutates nothing, so the result must be used "
                     + "(returned, assigned, or added to a children list).",
        category: "FluentGpu.Reactivity",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "FluentGpu Element records are immutable. Fluent modifiers (Rounded/Padded/…) and `with` clones "
                   + "return a new element rather than mutating in place; discarding that value as a statement has no "
                   + "effect and is almost always a bug.",
        helpLinkUri: "https://github.com/christosk92/fluent-gpu/blob/main/docs/guide/reactivity.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeStatement, SyntaxKind.ExpressionStatement);
    }

    private static void AnalyzeStatement(SyntaxNodeAnalysisContext context)
    {
        var statement = (ExpressionStatementSyntax)context.Node;
        ExpressionSyntax expr = statement.Expression;
        SemanticModel model = context.SemanticModel;

        // The discarded expression must itself PRODUCE an Element.
        ITypeSymbol? type = model.GetTypeInfo(expr, context.CancellationToken).Type;
        if (!AnalyzerSemantics.IsElementType(type))
            return;

        // Two element-transformation shapes, both statements where discarding is a bug:
        //   1. a modifier call on an Element-typed receiver:   box.Rounded(8);   (foo with {…} lowers here too)
        //   2. a discarded element construction:               new BoxEl { … };
        // NOT flagged: a method that merely RETURNS an Element but is invoked on a non-element receiver for side
        // effects (e.g. a probe's `component.RenderWithHooks();` in a test) — that is a legitimate discard.
        bool flag = expr switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }
                => AnalyzerSemantics.IsElementType(model.GetTypeInfo(ma.Expression, context.CancellationToken).Type),
            ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax => true,
            _ => false,
        };
        if (!flag)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, expr.GetLocation(), type!.Name));
    }
}
