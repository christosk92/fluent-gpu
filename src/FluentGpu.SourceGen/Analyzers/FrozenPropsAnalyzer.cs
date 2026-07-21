using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FGRP001 — the compile-time half of the frozen-props contract (design/subsystems/component-props-contract.md;
/// runtime half = <c>ReuseGuard</c>). A reused <c>ComponentEl</c> never re-runs its factory, so a field set inside
/// <c>Embed.Comp(() =&gt; new T { … })</c> freezes at first mount. This flags the sharpest, lowest-false-positive
/// instance: an <b>Element</b> (content slot) assigned as a component field from a captured local — content is rebuilt
/// every render, so freezing it is almost always a bug (the Expander/ToolTip/DropZone class). Scalar fields
/// (int/bool/string) are intentionally NOT flagged here — they are frequently stable, so the runtime guard's
/// per-control <c>DebugCheckReuse</c> compare owns that case instead.
/// <para>Severity <see cref="DiagnosticSeverity.Info"/>: guidance, never a build failure. Raise it to a warning in
/// .editorconfig (<c>dotnet_diagnostic.FGRP001.severity = warning</c>) once a project's controls are converted.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FrozenPropsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGRP001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Element content is frozen when passed as a field into Embed.Comp",
        messageFormat: "'{0}' is Element content assigned as a component field inside Embed.Comp — it freezes at first "
                     + "mount and later values are dropped. Re-push it via Embed.Comp(props, factory) + UseProps<T>() "
                     + "(the SelectorBar idiom), or Ctx.Provide + UseContext for ambient data, or remount with a changed Key.",
        category: "FluentGpu.Reactivity",
        // Promoted Info -> Warning in G4f: the props channel (Embed.Comp(props, factory) + [Props]/UseProps<T>) is now
        // the sanctioned re-push path, so a frozen Element content slot is almost always a real bug.
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Components are autonomous: a reused ComponentEl discards the new factory, so fields set inside "
                   + "Embed.Comp(() => new T { … }) are frozen at mount. Element content is rebuilt every render, so a "
                   + "frozen Element field silently keeps the mount-time content. See "
                   + "design/subsystems/component-props-contract.md.",
        helpLinkUri: "https://github.com/christosk92/fluent-gpu/blob/main/design/subsystems/component-props-contract.md");

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

        // Cheap syntactic pre-filter: `<something>.Comp(...)` with a single argument.
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Comp" }
            || invocation.ArgumentList.Arguments.Count != 1)
            return;

        // Confirm it is FluentGpu.Hooks.Embed.Comp (not some other `.Comp`).
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || method.Name != "Comp" || method.ContainingType?.Name != "Embed")
            return;

        // The single argument is the factory lambda: () => new T { … }.
        if (invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda)
            return;

        var creation = lambda.Body switch
        {
            ObjectCreationExpressionSyntax oc => oc,                                     // () => new T { … }
            BlockSyntax block => SingleReturnedCreation(block),                          // () => { …; return new T { … }; }
            _ => null,
        };
        if (creation?.Initializer is not { } initializer)
            return;

        foreach (var expr in initializer.Expressions)
        {
            // Only object-initializer member assignments: `Member = rhs`.
            if (expr is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax member, Right: { } rhs })
                continue;

            // RHS must be a captured local/parameter (a bare identifier resolving to a local or parameter of an
            // enclosing method) — a literal/const/`new …`/`this.` value is not a per-render-changing capture.
            if (rhs is not IdentifierNameSyntax rhsId)
                continue;
            var rhsSymbol = context.SemanticModel.GetSymbolInfo(rhsId, context.CancellationToken).Symbol;
            if (rhsSymbol is not (ILocalSymbol or IParameterSymbol))
                continue;

            // [MountOnceContent] on the source parameter/local opts the capture out — a deliberate mount-time slot
            // (the sanctioned replacement for a blanket #pragma around a static-content convenience factory).
            if (HasMountOnceContent(rhsSymbol))
                continue;

            // The assigned member's type must be Element (or a collection of Element) — the content-slot case.
            var memberSymbol = context.SemanticModel.GetSymbolInfo(member, context.CancellationToken).Symbol;
            var memberType = memberSymbol switch
            {
                IFieldSymbol f => f.Type,
                IPropertySymbol p => p.Type,
                _ => null,
            };
            if (memberType is null || !AnalyzerSemantics.IsElementContent(memberType))
                continue;

            // [MountOnceContent] on the target field/property likewise marks the slot intentionally frozen.
            if (memberSymbol is not null && HasMountOnceContent(memberSymbol))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, rhs.GetLocation(), member.Identifier.ValueText));
        }
    }

    // A symbol is exempt when it (a parameter/local source, or the target field/property) carries
    // [FluentGpu.Hooks.MountOnceContent] — the declaration-site marker that the content is a deliberate mount-time slot.
    private static bool HasMountOnceContent(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
            if (attr.AttributeClass?.Name == "MountOnceContentAttribute")
                return true;
        return false;
    }

    private static ObjectCreationExpressionSyntax? SingleReturnedCreation(BlockSyntax block)
    {
        ObjectCreationExpressionSyntax? found = null;
        foreach (var stmt in block.Statements)
            if (stmt is ReturnStatementSyntax { Expression: ObjectCreationExpressionSyntax oc })
            {
                if (found is not null) return null;   // more than one return → don't guess
                found = oc;
            }
        return found;
    }
}
