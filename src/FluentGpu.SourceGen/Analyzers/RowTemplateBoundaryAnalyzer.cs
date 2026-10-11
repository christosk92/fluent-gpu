using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FGTH003 - a recycled row template builds a component, slot, provider or boundary. A bound row template
/// (<c>BoundRowTemplate&lt;T&gt;</c>, the template of <c>ItemsView.CreateBound</c>, <c>VirtualListEl.RowBind</c>) is built ONCE per
/// slot and recycled by an index-signal write; a keyed <c>Virtual.List</c> row (<c>VirtualListEl.RenderItem</c>) is rebuilt and
/// reconciled per index and must stay recyclable (<c>IsRecyclable</c> rejects components, <c>Show</c>, providers and boundaries). So
/// neither may contain <c>Embed.Comp</c>, <c>Slots.View</c> / <c>ctx.View</c>, <c>Flow.*</c>, <c>Ctx.Provide</c>, <c>Skel.*</c>
/// or the element records those produce (<c>ComponentEl</c>, <c>ContextProviderEl</c>, <c>ShowEl</c>, <c>ForEl</c>,
/// <c>KeepAliveEl</c>, <c>SkelRegionEl</c>, <c>ScrollEl</c>, <c>VirtualListEl</c>).
/// <para>Scope is the template lambda's own body: nested lambdas and local functions are a different scope (an
/// <c>item.ShowWhen(pred, () =&gt; ...)</c> builder is the sanctioned way to mount a rare branch). A row that needs its own
/// reactive boundary expresses it as binds on the slot's elements instead.</para>
/// <para>Severity <see cref="DiagnosticSeverity.Info"/>: existing lists mount components on purpose; a project whose rows are
/// clean promotes it with <c>dotnet_diagnostic.FGTH003.severity = warning</c>.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RowTemplateBoundaryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGTH003";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Recycled row template contains a component, slot, provider or boundary",
        messageFormat: "'{0}' inside a recycled row template. Bound rows are built once per slot and recycled by a signal write; "
                     + "keyed Virtual.List rows must stay recyclable. Express the variation as binds on the row's own elements.",
        category: "FluentGpu.Theming",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Row templates (BoundRowTemplate, ItemsView.CreateBound, VirtualListEl.RowBind/RenderItem, Virtual.List) may not "
                   + "contain Embed.Comp, Slots.View, ctx.View, Flow.*, Ctx.Provide, Skel.* or boundary elements.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeLambda,
            SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
    }

    private static void AnalyzeLambda(SyntaxNodeAnalysisContext context)
    {
        var lambda = context.Node;
        if (!IsRowTemplate(context, lambda)) return;

        CSharpSyntaxNode? body = lambda switch
        {
            LambdaExpressionSyntax l => (CSharpSyntaxNode?)l.Body,
            AnonymousMethodExpressionSyntax a => a.Block,
            _ => null,
        };
        if (body is null) return;

        // The body itself, then its descendants without crossing into a nested lambda / local function (another scope).
        foreach (var node in body.DescendantNodesAndSelf(n => n is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            string? what = node switch
            {
                InvocationExpressionSyntax inv => ForbiddenCall(context, inv),
                BaseObjectCreationExpressionSyntax creation => ForbiddenElement(context, creation),
                _ => null,
            };
            if (what is not null)
                context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), what));
        }
    }

    // ── which lambdas are row templates ──────────────────────────────────────────────────────────────────────────

    private static bool IsRowTemplate(SyntaxNodeAnalysisContext context, SyntaxNode lambda)
    {
        var converted = context.SemanticModel.GetTypeInfo(lambda, context.CancellationToken).ConvertedType;
        if (converted is INamedTypeSymbol { Name: "BoundRowTemplate" } bound && InNamespace(bound, "FluentGpu.Controls"))
            return true;
        if (converted is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } del
            || del.DelegateInvokeMethod is not { } invoke
            || !AnalyzerSemantics.IsElementType(invoke.ReturnType))
            return false;

        SyntaxNode? parent = lambda.Parent;
        while (parent is ParenthesizedExpressionSyntax) parent = parent.Parent;

        // ItemsView.CreateBound(..., template, ...) / Virtual.List|Grid|ListBound|GridBound(..., row)
        if (parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } }
            && context.SemanticModel.GetSymbolInfo(inv, context.CancellationToken).Symbol is IMethodSymbol m)
        {
            var owner = m.ContainingType;
            if (owner is { Name: "ItemsView" } && InNamespace(owner, "FluentGpu.Controls") && m.Name == "CreateBound") return true;
            if (owner is { Name: "Virtual" } && InNamespace(owner, "FluentGpu.Controls")) return true;
        }

        // new VirtualListEl { RenderItem = lambda } / { RowBind = lambda }
        if (parent is AssignmentExpressionSyntax { Left: IdentifierNameSyntax left, Parent: InitializerExpressionSyntax }
            && left.Identifier.ValueText is "RenderItem" or "RowBind"
            && context.SemanticModel.GetSymbolInfo(left, context.CancellationToken).Symbol is IPropertySymbol prop
            && prop.ContainingType is { Name: "VirtualListEl" })
            return true;

        return false;
    }

    // ── what a row may not contain ───────────────────────────────────────────────────────────────────────────────

    private static string? ForbiddenCall(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax inv)
    {
        if (context.SemanticModel.GetSymbolInfo(inv, context.CancellationToken).Symbol is not IMethodSymbol m) return null;
        var owner = m.ContainingType;
        if (owner is null) return null;
        string name = m.Name;
        if (owner.Name == "Embed" && InNamespace(owner, "FluentGpu.Hooks") && name == "Comp") return "Embed.Comp";
        if (owner.Name == "Slots" && InNamespace(owner, "FluentGpu.Theming") && name == "View") return "Slots.View";
        if (owner.Name == "ViewContext" && InNamespace(owner, "FluentGpu.Theming") && name == "View") return "ctx.View";
        if (owner.Name == "Flow" && InNamespace(owner, "FluentGpu.Hooks")) return "Flow." + name;
        if (owner.Name == "Ctx" && InNamespace(owner, "FluentGpu.Hooks") && name == "Provide") return "Ctx.Provide";
        if (owner.Name == "Skel" && InNamespace(owner, "FluentGpu.Hooks")) return "Skel." + name;
        return null;
    }

    private static string? ForbiddenElement(SyntaxNodeAnalysisContext context, BaseObjectCreationExpressionSyntax creation)
    {
        var type = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type;
        if (type is null) return null;
        switch (type.Name)
        {
            case "ComponentEl" or "ContextProviderEl" or "ShowEl" or "ForEl" or "KeepAliveEl" or "SkelRegionEl":
                if (InNamespace(type, "FluentGpu.Hooks")) return type.Name;
                break;
            case "ScrollEl":
                if (InNamespace(type, "FluentGpu.Dsl")) return type.Name;
                break;
            case "VirtualListEl":
                if (InNamespace(type, "FluentGpu.Reconciler")) return type.Name;
                break;
        }
        return null;
    }

    private static bool InNamespace(ISymbol symbol, string ns) => symbol.ContainingNamespace?.ToDisplayString() == ns;
}
