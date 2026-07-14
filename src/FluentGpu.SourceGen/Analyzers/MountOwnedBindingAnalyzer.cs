using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>FGRP002 — warns about the common mount-owned binding mistake: reading a reactive value before constructing
/// a <c>Prop.Of</c> thunk and capturing that by-value snapshot. Replacement thunks are deliberately ignored when an
/// element is reconciled, so the captured snapshot cannot become live. The rule is intentionally conservative: signal,
/// memo, delegate, and constant captures are valid stable inputs and are not reported.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MountOwnedBindingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGRP002";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Prop.Of captures a mount-time snapshot",
        messageFormat: "Prop.Of captures '{0}' by value; replacement thunks are ignored after mount. Read the source "
                     + "signal inside the thunk or use a signal-backed BoundItemsSource.",
        category: "FluentGpu.Reactivity",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Bound element channels are wired once at mount. Capturing an ordinary local or parameter in a "
                   + "Prop.Of thunk can silently retain the first render's value after later component renders.",
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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Of" }
            || invocation.ArgumentList.Arguments.Count != 1
            || invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || method.Name != "Of"
            || method.ContainingType?.Name != "Prop"
            || method.ContainingNamespace?.ToDisplayString() != "FluentGpu.Signals")
            return;

        var flow = context.SemanticModel.AnalyzeDataFlow(lambda);
        if (flow is null || !flow.Succeeded) return;

        foreach (var captured in flow.CapturedInside)
        {
            ITypeSymbol? type = captured switch
            {
                ILocalSymbol local when !local.IsConst => local.Type,
                IParameterSymbol parameter => parameter.Type,
                _ => null,
            };
            if (type is null || type.TypeKind == TypeKind.Delegate || IsSignalLike(type)) continue;

            var use = lambda.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .FirstOrDefault(id => SymbolEqualityComparer.Default.Equals(
                    context.SemanticModel.GetSymbolInfo(id, context.CancellationToken).Symbol, captured));
            SyntaxNode locationNode = use is null ? lambda : use;
            context.ReportDiagnostic(Diagnostic.Create(Rule, locationNode.GetLocation(), captured.Name));
        }
    }

    private static bool IsSignalLike(ITypeSymbol type)
    {
        if (IsReadSignal(type)) return true;
        if (type is INamedTypeSymbol named)
            foreach (var iface in named.AllInterfaces)
                if (IsReadSignal(iface)) return true;
        return false;
    }

    private static bool IsReadSignal(ITypeSymbol type)
        => type.Name == "IReadSignal"
           && type.ContainingNamespace?.ToDisplayString() == "FluentGpu.Signals";
}
