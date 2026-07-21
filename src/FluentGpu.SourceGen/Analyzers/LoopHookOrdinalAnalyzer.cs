using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FGRP005 — a positional-cell hook invoked inside a loop body (an ordinal-keying LINT, NOT a guard). The keyed hook
/// substrate (G4) made conditional hooks legal, so <c>Use*</c> inside an <c>if</c> is fine and is deliberately NOT
/// flagged. A LOOP is different: every iteration re-invokes the SAME call site, so the hook cells key by per-line
/// ordinal and iteration index — reordering or inserting into the collection shifts each row's retained state onto a
/// different item. That is rarely intended. The remedy is to key rows structurally (<c>Flow.For</c>, which mounts a
/// keyed child per item) or to hoist the state out of the loop.
/// <para>Severity <see cref="DiagnosticSeverity.Info"/> — a compatibility note, never a build failure. Only loops in
/// the SAME render scope are flagged: a hook inside a lambda/local function nested in the loop (e.g. a
/// <c>Flow.For</c> item builder) is a separate scope and is skipped.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LoopHookOrdinalAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGRP005";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Positional-cell hook invoked inside a loop",
        messageFormat: "Hook '{0}' is invoked inside a loop body. Loop hooks key by per-line ordinal, so reordering or "
                     + "inserting into the collection shifts retained state between iterations. Key rows via Flow.For "
                     + "(a keyed child per item) or hoist the state out of the loop.",
        category: "FluentGpu.Reactivity",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Conditional hooks are legal on the keyed hook substrate, but a hook in a loop re-invokes the same "
                   + "call site every iteration; the cells key by ordinal + index, so a collection reorder moves state "
                   + "between items. Prefer Flow.For for per-item state.",
        helpLinkUri: "https://github.com/christosk92/fluent-gpu/blob/main/docs/guide/reactivity.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Cheap syntactic pre-filter: a bare or `this.`-qualified call whose name starts with "Use".
        string? name = invocation.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            _ => null,
        };
        if (name is null || !name.StartsWith("Use", System.StringComparison.Ordinal))
            return;

        // Confirm it resolves to a Use* hook on Component (positional-cell hooks live there).
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || !method.Name.StartsWith("Use", System.StringComparison.Ordinal)
            || !AnalyzerSemantics.DerivesFromComponent(method.ContainingType))
            return;

        // Walk out to the first structural boundary: a loop found first ⇒ flag; a lambda/local-function/member first
        // ⇒ a different render scope ⇒ skip.
        for (SyntaxNode? n = invocation.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                    context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), method.Name));
                    return;
                case SimpleLambdaExpressionSyntax:
                case ParenthesizedLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case BaseMethodDeclarationSyntax:
                case AccessorDeclarationSyntax:
                    return;
            }
        }
    }
}
