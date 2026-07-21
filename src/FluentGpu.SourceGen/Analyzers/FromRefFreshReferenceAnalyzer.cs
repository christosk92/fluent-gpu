using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FGRP006 — a freshly-created reference passed to <c>DepKey.FromRef(...)</c>. <c>FromRef</c> keys off object IDENTITY
/// (<c>RuntimeHelpers.GetHashCode</c>), so a lambda, anonymous method, delegate creation, <c>new</c> object, or fresh
/// array written directly at the call site is a NEW identity every render — the dep gate then never matches and the
/// hook re-runs on every render, defeating the gate entirely. Hoist the reference (a mount-stable field / captured
/// local) or key on a value projection (<c>DepKey.From(...)</c> over the stable scalars) instead.
/// <para>Severity <see cref="DiagnosticSeverity.Warning"/>.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FromRefFreshReferenceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGRP006";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "DepKey.FromRef given a freshly-created reference",
        messageFormat: "DepKey.FromRef is given a freshly-created reference ({0}); a new identity every render never "
                     + "matches the stored dep, so the hook re-runs each render and the gate does nothing. Hoist the "
                     + "reference or key on a value projection (DepKey.From over the stable scalars).",
        category: "FluentGpu.Reactivity",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DepKey.FromRef compares object identity. A lambda/new/array literal at the call site allocates a "
                   + "distinct instance on every render, so the identity key always differs and the dep gate can never "
                   + "short-circuit — the effect/memo re-runs unconditionally.",
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

        // Cheap pre-filter: `<something>.FromRef(...)`.
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "FromRef" }
            || invocation.ArgumentList.Arguments.Count == 0)
            return;

        // Confirm FluentGpu.Hooks.DepKey.FromRef.
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || method.Name != "FromRef"
            || method.ContainingType?.Name != "DepKey"
            || method.ContainingType.ContainingNamespace?.ToDisplayString() != "FluentGpu.Hooks")
            return;

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (IsFreshReference(arg.Expression, out string kind))
                context.ReportDiagnostic(Diagnostic.Create(Rule, arg.Expression.GetLocation(), kind));
        }
    }

    // A creation expression written AT the call site is inherently a fresh reference each time the render runs.
    private static bool IsFreshReference(ExpressionSyntax expr, out string kind)
    {
        switch (expr)
        {
            case SimpleLambdaExpressionSyntax:
            case ParenthesizedLambdaExpressionSyntax:
                kind = "a lambda"; return true;
            case AnonymousMethodExpressionSyntax:
                kind = "an anonymous method"; return true;
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
                kind = "a new object"; return true;
            case ArrayCreationExpressionSyntax:
            case ImplicitArrayCreationExpressionSyntax:
                kind = "a new array"; return true;
            case CollectionExpressionSyntax:
                kind = "a collection expression"; return true;
            default:
                kind = ""; return false;
        }
    }
}
