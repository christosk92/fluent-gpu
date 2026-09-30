using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>FGRP002 — warns about the common snapshot-binding mistake: reading a reactive value before constructing
/// a <c>Prop.Of</c> thunk and capturing that by-value snapshot (<c>var v = sig.Value; … Prop.Of(() =&gt; f(v))</c>).
/// The reconciler re-wires a bound channel whose thunk changed (bound→bound), so the snapshot refreshes only when the
/// ENCLOSING render re-runs: in a run-once scope (an <c>ItemsView.CreateBound</c> row template, a render that reads no
/// signal) the channel freezes at its first value, and where the render does re-run every change costs a component
/// re-render instead of a compositor-only bind fire. Read the signal inside the thunk.
/// <para>The rule is deliberately precise (promoted to Warning in G4f): it fires ONLY on a captured LOCAL whose own
/// initializer READS a signal's <c>.Value</c> — the true snapshot signature. Captures that are NOT snapshot bugs are
/// left alone: <c>this</c> and parameters (frozen at mount by design), a stable object reference read reactively
/// inside the thunk (<c>fb.Error.Value</c>), and a mount-stable value local (a factory/config arg like an icon role).
/// Signal / memo / delegate / const captures were already excluded.</para></summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MountOwnedBindingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGRP002";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Prop.Of captures a render-time snapshot",
        messageFormat: "Prop.Of captures '{0}' by value; it refreshes only when the enclosing render re-runs (never in a "
                     + "run-once template). Read the source signal inside the thunk or use a signal-backed BoundItemsSource.",
        category: "FluentGpu.Reactivity",
        // Promoted Info -> Warning in G4f alongside FGRP001. Since the bind re-wire (bound→bound, 2026-09-30) a snapshot
        // no longer freezes a re-rendering component's channel, but it still freezes a run-once template's and turns a
        // compositor-only bind fire into a component re-render everywhere else.
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A by-value snapshot captured in a Prop.Of thunk refreshes only when the enclosing render re-runs: "
                   + "a run-once template keeps its first value forever, and a re-rendering component pays a full "
                   + "re-render per change where reading the signal inside the thunk would be a single bind fire.",
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
            // Only a captured LOCAL can be a per-render snapshot; `this`/parameters freeze at mount by design.
            if (captured is not ILocalSymbol local || local.IsConst) continue;
            ITypeSymbol type = local.Type;
            if (type.TypeKind == TypeKind.Delegate || AnalyzerSemantics.IsSignalLike(type)) continue;

            // The true snapshot signature: the local's own initializer READS a signal's .Value (a value read OUTSIDE
            // the thunk). A local initialized from a plain value / another local / a loop index is mount-stable and
            // correctly capturable; a stable object ref read reactively inside the thunk is also fine.
            if (!LocalInitializerReadsSignalValue(local, context.SemanticModel, context.CancellationToken)) continue;

            var use = lambda.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .FirstOrDefault(id => SymbolEqualityComparer.Default.Equals(
                    context.SemanticModel.GetSymbolInfo(id, context.CancellationToken).Symbol, captured));
            SyntaxNode locationNode = use is null ? lambda : use;
            context.ReportDiagnostic(Diagnostic.Create(Rule, locationNode.GetLocation(), captured.Name));
        }
    }

    // True when <paramref name="local"/> is declared with an initializer that reads a signal's `.Value` — the
    // render-time snapshot only a re-run of the enclosing render can refresh. Pattern/foreach/uninitialized locals return false.
    private static bool LocalInitializerReadsSignalValue(ILocalSymbol local, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(ct) is VariableDeclaratorSyntax { Initializer.Value: { } init }
                && AnalyzerSemantics.ContainsSignalValue(init, model))
                return true;
        }
        return false;
    }
}
