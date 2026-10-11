using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// FGTH004 - a theme token frozen into a <c>const</c> or <c>static readonly</c> field. <c>Spacing</c>, <c>Radii</c>,
/// <c>Elevation</c>, the type ramp (<c>Ui.Caption</c> .. <c>Ui.Display</c>) and every <c>Tok</c> / <c>Theme</c> getter resolve
/// against the ACTIVE theme definition (<c>Tok.Definition</c>) at the moment they are read. A field initialiser runs once,
/// at type load, so the value it captured is the first theme's value for the rest of the process: a later
/// <c>Tok.Use(definition, kind)</c> changes every other reader and leaves this one behind (a stale radius, a gap that
/// never follows the theme). Read the token where the element is built instead (a property, a local, a method body).
/// <para>NOT flagged: reads inside a lambda or local function (the thunk runs later, theme-reactively - the
/// <c>Prop.Of(static () =&gt; Tok.TextPrimary)</c> idiom), <c>nameof</c>/<c>typeof</c>, a <c>const</c> or <c>readonly</c>
/// member of those types (theme-invariant by construction, e.g. <c>Tok.OnMediaPrimary</c>, <c>Tok.FocusThickness</c>) and
/// pure helper methods such as <c>Radii.Circle</c>.</para>
/// <para>Severity <see cref="DiagnosticSeverity.Warning"/> (the solution builds with warnings as errors).</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FrozenThemeTokenAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FGTH004";

    private const string DslNamespace = "FluentGpu.Dsl";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Theme token frozen into a const or static readonly field",
        messageFormat: "Field '{0}' captures '{1}' once, at type load. Theme tokens (Spacing, Radii, Elevation, the type "
                     + "ramp, Tok, Theme) resolve against the active theme definition when READ, so this field keeps the "
                     + "first theme's value after Tok.Use(definition, kind). Read the token where the element is built "
                     + "(a property, a local or a method body), or wrap it in a lambda.",
        category: "FluentGpu.Theming",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Spacing, Radii and the other token classes are static properties over Tok.Definition, not constants. "
                   + "A const or static readonly initialiser evaluates once at type load and cannot follow a theme "
                   + "definition swap.",
        helpLinkUri: "https://github.com/christosk92/fluent-gpu/blob/main/docs/design/subsystems/theming.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeField, SyntaxKind.FieldDeclaration);
    }

    private static void AnalyzeField(SyntaxNodeAnalysisContext context)
    {
        var field = (FieldDeclarationSyntax)context.Node;
        bool isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
        bool isStaticReadonly = field.Modifiers.Any(SyntaxKind.StaticKeyword) && field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
        if (!isConst && !isStaticReadonly)
            return;

        foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
        {
            ExpressionSyntax? initializer = variable.Initializer?.Value;
            if (initializer is null)
                continue;

            SyntaxNode? hit = FindThemeRead(initializer, context.SemanticModel, context.CancellationToken);
            if (hit is null)
                continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, hit.GetLocation(), variable.Identifier.ValueText, hit.ToString()));
        }
    }

    /// <summary>The first name in <paramref name="initializer"/> that reads a theme-dependent member eagerly, or null.</summary>
    private static SyntaxNode? FindThemeRead(ExpressionSyntax initializer, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (SyntaxNode node in initializer.DescendantNodesAndSelf(DescendInto))
        {
            if (node is not SimpleNameSyntax name)
                continue;
            ISymbol? symbol = model.GetSymbolInfo(name, ct).Symbol;
            if (symbol is not null && IsThemeDependent(symbol))
                return name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
        }
        return null;
    }

    // Do not look inside deferred code (a lambda / local function body runs later) or compile-time-only expressions.
    private static bool DescendInto(SyntaxNode node)
        => node is not AnonymousFunctionExpressionSyntax
           && node is not LocalFunctionStatementSyntax
           && node is not TypeOfExpressionSyntax
           && node is not InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } };

    private static bool IsThemeDependent(ISymbol symbol)
    {
        INamedTypeSymbol? type = symbol.ContainingType;
        if (type is null || type.ContainingNamespace?.ToDisplayString() != DslNamespace)
            return false;

        switch (type.Name)
        {
            case "Spacing":
            case "Radii":
            case "Elevation":
            case "Theme":
            case "Tok":
                // Properties resolve against the active definition on every read. A mutable field (Tok.T) is the live
                // token set. Constants and readonly fields are theme-invariant; methods (Radii.Circle) are pure helpers.
                return symbol is IPropertySymbol
                    || symbol is IFieldSymbol { IsConst: false, IsReadOnly: false };
            case "Ui":
                // Only the eight type-ramp factories read the definition's TypeRamp.
                return symbol is IMethodSymbol { Name: "Caption" or "Body" or "BodyStrong" or "BodyLarge" or "Subtitle" or "Title" or "TitleLarge" or "Display" };
            default:
                return false;
        }
    }
}
