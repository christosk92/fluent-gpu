using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FGRP003 — a dead bind thunk. A parameterless lambda passed to <c>Prop.Of(...)</c> (the sanctioned inline-thunk
/// entry point for a bindable channel) whose body reads reactive state ONLY through <c>.Peek()</c> and never through
/// <c>.Value</c> subscribes to nothing: the reconciler wires it into a mount-time effect that runs once, reads a
/// snapshot, and — because no signal was subscribed — is never re-run. The channel silently freezes at the first
/// value. The fix is to read <c>.Value</c> (which subscribes) inside the thunk, or drop the thunk for a
/// signal-direct bind. Reuses <see cref="AnalyzerSemantics"/> (IsSignalLike / the Peek+Value body scans).
/// <para>Severity <see cref="DiagnosticSeverity.Warning"/>.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PeekOnlyBindThunkAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGRP003";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Bind thunk reads only .Peek() and never subscribes",
        messageFormat: "This Prop.Of bind thunk reads reactive state only via .Peek() and never via .Value, so it "
                     + "subscribes to nothing — it fires once at mount and goes dead. Read the source signal's .Value "
                     + "inside the thunk (that subscribes), or bind the signal directly.",
        category: "FluentGpu.Reactivity",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A bound element channel is wired once at mount into a tracking effect. The effect only re-runs "
                   + "when a signal it READ via .Value changes; .Peek() reads a value without subscribing, so a thunk "
                   + "that only peeks never updates after the first render.",
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

        // Cheap pre-filter: `<something>.Of(<lambda>)` with a single argument.
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Of" }
            || invocation.ArgumentList.Arguments.Count != 1
            || invocation.ArgumentList.Arguments[0].Expression is not ParenthesizedLambdaExpressionSyntax lambda)
            return;

        // Only parameterless thunks — a bind thunk is Func<T>, never Func<T,...> / Action<...>.
        if (lambda.ParameterList.Parameters.Count != 0)
            return;

        // Confirm FluentGpu.Signals.Prop.Of (not some other `.Of`).
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || method.Name != "Of"
            || method.ContainingType?.Name != "Prop"
            || method.ContainingNamespace?.ToDisplayString() != "FluentGpu.Signals")
            return;

        SyntaxNode body = lambda.Body;
        SemanticModel model = context.SemanticModel;

        // Dead IFF it peeks a signal at least once and never touches .Value anywhere in the body.
        if (AnalyzerSemantics.ContainsSignalPeek(body, model)
            && !AnalyzerSemantics.ContainsSignalValue(body, model))
            context.ReportDiagnostic(Diagnostic.Create(Rule, lambda.GetLocation()));
    }
}
