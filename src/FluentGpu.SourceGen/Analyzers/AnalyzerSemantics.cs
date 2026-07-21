using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// Shared semantic predicates for the FGRP reactivity analyzers (FGRP001–FGRP007). Centralizing the type tests keeps
/// every rule agreeing on what an <c>Element</c>, a signal, or a <c>Prop&lt;T&gt;</c> is — the drift that would let two
/// rules diverge on the same shape. These are pure symbol queries (no state), so they are safe under concurrent
/// analysis. All namespace matches are by fully-qualified display string against the engine's canonical namespaces.
/// </summary>
internal static class AnalyzerSemantics
{
    // ── Element (the DSL immutable record base) ──────────────────────────────────────────────────────────────────
    /// <summary>True for <c>FluentGpu.Dsl.Element</c> or any type deriving from it.</summary>
    public static bool IsElementType(ITypeSymbol? type)
    {
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
            if (t.Name == "Element" && t.ContainingNamespace?.ToDisplayString() == "FluentGpu.Dsl")
                return true;
        return false;
    }

    /// <summary>True for <c>Element</c>, an <c>Element[]</c>, or a single-arg generic COLLECTION of <c>Element</c>
    /// (the content-slot shape flagged by FGRP001). A delegate such as <c>Func&lt;Element&gt;</c> is deliberately
    /// excluded — a thunk is a STABLE reference that rebuilds its content on demand (the correct reactive pattern),
    /// not a frozen content snapshot.</summary>
    public static bool IsElementContent(ITypeSymbol type)
    {
        if (IsElementType(type)) return true;
        if (type is IArrayTypeSymbol array) return IsElementType(array.ElementType);
        if (type is INamedTypeSymbol { IsGenericType: true, TypeKind: not TypeKind.Delegate } named)
            return named.TypeArguments.Length == 1 && IsElementType(named.TypeArguments[0]);
        return false;
    }

    /// <summary>True for <c>FluentGpu.Dsl.TextEl</c> (the text element whose first ctor arg / <c>Text</c> prop is
    /// user-facing copy). Used by FGRP008 to scope the hardcoded-string lint to the text sink.</summary>
    public static bool IsTextEl(ITypeSymbol? type)
        => type is { Name: "TextEl" } && type.ContainingNamespace?.ToDisplayString() == "FluentGpu.Dsl";

    // ── Component (the hook host) ────────────────────────────────────────────────────────────────────────────────
    /// <summary>True for <c>FluentGpu.Hooks.Component</c> or any type deriving from it (the positional-cell hook host).</summary>
    public static bool DerivesFromComponent(ITypeSymbol? type)
    {
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
            if (t.Name == "Component" && t.ContainingNamespace?.ToDisplayString() == "FluentGpu.Hooks")
                return true;
        return false;
    }

    // ── Signals (the reactive source surface) ────────────────────────────────────────────────────────────────────
    /// <summary>True when <paramref name="type"/> is (or implements) <c>FluentGpu.Signals.IReadSignal&lt;T&gt;</c> —
    /// i.e. a <c>Signal&lt;T&gt;</c>, <c>Memo&lt;T&gt;</c>, <c>FloatSignal</c>, or the interface itself.</summary>
    public static bool IsSignalLike(ITypeSymbol? type)
    {
        if (type is null) return false;
        if (IsReadSignal(type)) return true;
        if (type is INamedTypeSymbol named)
            foreach (var iface in named.AllInterfaces)
                if (IsReadSignal(iface)) return true;
        return false;
    }

    public static bool IsReadSignal(ITypeSymbol type)
        => type.Name == "IReadSignal"
           && type.ContainingNamespace?.ToDisplayString() == "FluentGpu.Signals";

    // ── Prop<T> (the unified bindable element channel) ───────────────────────────────────────────────────────────
    /// <summary>True when <paramref name="type"/> is <c>FluentGpu.Signals.Prop&lt;T&gt;</c>.</summary>
    public static bool IsPropType(ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: "Prop", IsGenericType: true } n
           && n.ContainingNamespace?.ToDisplayString() == "FluentGpu.Signals";

    /// <summary>True when <paramref name="type"/> is the parameterless <c>System.Func&lt;TResult&gt;</c> (the bind-thunk
    /// delegate shape) — NOT <c>Func&lt;T,…&gt;</c>, <c>Action</c>, or any other delegate.</summary>
    public static bool IsFuncThunk(ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: "Func", IsGenericType: true, TypeArguments.Length: 1 } n
           && n.ContainingNamespace?.ToDisplayString() == "System";

    // ── Signal read shapes inside a lambda body (FGRP003) ────────────────────────────────────────────────────────
    /// <summary>True if <paramref name="node"/> contains at least one <c>signal.Peek()</c> call (receiver is
    /// signal-like).</summary>
    public static bool ContainsSignalPeek(SyntaxNode node, SemanticModel model)
    {
        foreach (var inv in node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            if (inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Peek" } ma
                && inv.ArgumentList.Arguments.Count == 0
                && IsSignalLike(model.GetTypeInfo(ma.Expression).Type))
                return true;
        return false;
    }

    /// <summary>True if <paramref name="node"/> contains any <c>signal.Value</c> access (a subscribing read; receiver
    /// is signal-like). Conservative: an access anywhere in the body — even a write or a nested lambda — counts, so
    /// FGRP003 only fires when the thunk NEVER touches <c>.Value</c>.</summary>
    public static bool ContainsSignalValue(SyntaxNode node, SemanticModel model)
    {
        foreach (var ma in node.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            if (ma.Name.Identifier.ValueText == "Value"
                && IsSignalLike(model.GetTypeInfo(ma.Expression).Type))
                return true;
        return false;
    }
}
