using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FGRP007 — a bindable <c>Prop&lt;T&gt;</c> channel declared on an <c>Element</c> record WITHOUT an initializer.
/// <c>default(Prop&lt;T&gt;)</c> is a STATIC <c>default(T)</c> (an unbound, inert seed) — never the "unset / inherit"
/// sentinel authors reach for. A channel left uninitialized silently paints <c>default(T)</c> (a transparent color, a
/// zero corner radius, a collapsing transform) instead of the intended resting value, and the mistake is invisible at
/// the call site. Give every declared channel an explicit initializer (<c>= Tok.Foo</c>, <c>= 1f</c>, or an explicit
/// <c>= default</c> to affirm the zero value is intended).
/// <para>Severity <see cref="DiagnosticSeverity.Warning"/>. Only auto-property channels with an <c>init</c>/<c>set</c>
/// accessor are flagged; a computed (expression-bodied / get-only) property is not a defaulted channel. Positional
/// record parameters are required at construction, so they are never "unset" and are not flagged.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UninitializedPropChannelAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGRP007";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Prop<T> channel declared without an initializer",
        messageFormat: "Channel '{0}' has no initializer, so it defaults to default(Prop<T>) — a static default(T), "
                     + "not 'unset'. Give it an explicit initializer (e.g. = Tok.Foo / = 1f, or = default to affirm the "
                     + "zero value is intended).",
        category: "FluentGpu.Reactivity",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "default(Prop<T>) is an unbound static default(T), not an 'unset' sentinel. A Prop<T> channel on "
                   + "an Element record left without an initializer silently resolves to default(T) at paint time.",
        helpLinkUri: "https://github.com/christosk92/fluent-gpu/blob/main/docs/guide/reactivity.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeProperty, SyntaxKind.PropertyDeclaration);
    }

    private static void AnalyzeProperty(SyntaxNodeAnalysisContext context)
    {
        var decl = (PropertyDeclarationSyntax)context.Node;

        // Already has an initializer ⇒ explicit ⇒ fine.
        if (decl.Initializer is not null)
            return;

        // Must be an auto-property channel: an accessor list with init/set and no body, no expression body.
        if (decl.ExpressionBody is not null || decl.AccessorList is null)
            return;
        bool isSettableAuto = decl.AccessorList.Accessors.All(a => a.Body is null && a.ExpressionBody is null)
                              && decl.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)
                                                                     || a.IsKind(SyntaxKind.SetAccessorDeclaration));
        if (!isSettableAuto)
            return;

        if (context.SemanticModel.GetDeclaredSymbol(decl, context.CancellationToken) is not IPropertySymbol prop)
            return;
        if (!AnalyzerSemantics.IsPropType(prop.Type) || !AnalyzerSemantics.IsElementType(prop.ContainingType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, decl.Identifier.GetLocation(), prop.Name));
    }
}
