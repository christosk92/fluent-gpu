using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FluentGpu.SourceGen.Analyzers;

/// <summary>
/// PERF r3 MODEL M4b (DESIGN 2.2, escapes): an element built during a render lives on that render's tape and is gone when the
/// render's reconcile returns. Two shapes let one outlive it and are decidable at compile time; both are build ERRORS.
/// <list type="bullet">
/// <item><b>FGEL001</b>: a lambda that runs LATER (a <c>[Deferred]</c> parameter: skeleton region content, list row builders, a
/// component factory, an overlay's content; a boundary element's delegate member; an element's handler such as <c>OnClick</c>)
/// captures a local or parameter holding an element built outside it. Build the element inside the lambda, or freeze it where
/// it is captured (<c>var body = Compose(...).Freeze();</c>). A captured struct of parts that holds elements counts too: freeze
/// the copy the lambda captures (<c>var frozen = ElementFreezer.Freeze(parts);</c>; the struct must be nameable from the assembly,
/// internal or public, so the generated freezer can reach it).</item>
/// <item><b>FGEL002</b>: a static field or property holding an element is initialized without <c>Ui.Static(() =&gt; ...)</c>. A
/// static initializer runs on the type's first use, which can be inside a render.</item>
/// </list>
/// Active only where the compilation has <c>FluentGpu.Dsl.ElementFreezer</c> (the value-type element model).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ElementEscapeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Captured = new(
        id: "FGEL001",
        title: "A deferred lambda captures an element built outside it",
        messageFormat: "'{0}' holds an element built in this render, and this lambda runs later (after the render's tape region closed). "
                     + "Build the element inside the lambda, or freeze it where it is captured ('{0} = ... .Freeze()').",
        category: "FluentGpu.Elements",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Static = new(
        id: "FGEL002",
        title: "A static element is not built with Ui.Static",
        messageFormat: "Static '{0}' holds an element: initialize it with Ui.Static(() => ...) (a static initializer can run inside a render, "
                     + "and an element built there is gone when the render ends)",
        category: "FluentGpu.Elements",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Stored = new(
        id: "FGEL003",
        title: "A lambda stores an element in a field without freezing it",
        messageFormat: "This lambda stores an element in '{0}', which outlives the render the lambda runs in: freeze it where it is stored (Freeze())",
        category: "FluentGpu.Elements",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Captured, Static, Stored);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName("FluentGpu.Dsl.ElementFreezer") is null) return;
            var known = new Known(start.Compilation);
            start.RegisterOperationAction(c => AnalyzeLambda(c, known), OperationKind.AnonymousFunction);
            start.RegisterOperationAction(c => AnalyzeStores(c, known), OperationKind.AnonymousFunction);
            start.RegisterSymbolAction(c => AnalyzeStatic(c, known), SymbolKind.Field, SymbolKind.Property);
        });
    }

    private sealed class Known
    {
        public readonly INamedTypeSymbol? Element, ElementList, IElement, HeapElement, Deferred;
        public Known(Compilation c)
        {
            Element = c.GetTypeByMetadataName("FluentGpu.Dsl.Element");
            ElementList = c.GetTypeByMetadataName("FluentGpu.Dsl.ElementList");
            IElement = c.GetTypeByMetadataName("FluentGpu.Dsl.IElement");
            HeapElement = c.GetTypeByMetadataName("FluentGpu.Dsl.HeapElement");
            Deferred = c.GetTypeByMetadataName("FluentGpu.Dsl.DeferredAttribute");
        }

        public bool HoldsElement(ITypeSymbol? t, int depth = 0)
        {
            if (t is null || depth > 2) return false;
            if (Eq(t, Element) || Eq(t, ElementList)) return true;
            if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n) return HoldsElement(n.TypeArguments[0], depth + 1);
            if (t is IArrayTypeSymbol a) return HoldsElement(a.ElementType, depth + 1);
            if (t is INamedTypeSymbol nt)
            {
                if (nt.IsValueType && IElement is not null && nt.AllInterfaces.Any(i => Eq(i, IElement))) return true;   // BoxEl, TextEl, ...
                // a boundary element (HeapElement) is an object: capturing it is safe (its own members are frozen with the trees it rides)
                if (nt.TypeKind != TypeKind.Delegate && nt.IsGenericType && nt.TypeArguments.Length == 1
                    && nt.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
                    return HoldsElement(nt.TypeArguments[0], depth + 1);
                // a tuple of elements: (Element Head, Element Body)
                if (nt.IsTupleType) return nt.TupleElements.Any(f => HoldsElement(f.Type, depth + 1));
                // a struct of source code that holds elements (a template's record struct of parts): a copy made in this render, so
                // its elements are this render's unless it is frozen (ElementFreezer.Freeze(parts)). A class holder is not decided
                // here: a reference that reaches a deferred lambda was handed on by the reconciler (props, context), which froze it.
                if (nt.IsValueType && nt.TypeKind == TypeKind.Struct && !IsFrameworkType(nt))
                    return nt.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic && !f.IsConst && HoldsElement(f.Type, depth + 1));
            }
            return false;
        }

        public bool IsBoundaryOrKind(ITypeSymbol? t)
        {
            if (t is not INamedTypeSymbol nt) return false;
            if (nt.IsValueType && IElement is not null && nt.AllInterfaces.Any(i => Eq(i, IElement))) return true;
            for (var b = nt.BaseType; b is not null; b = b.BaseType) if (Eq(b, HeapElement)) return true;
            return false;
        }

        public bool IsDeferredParameter(IParameterSymbol? p)
        {
            if (p is null) return false;
            if (Deferred is not null && p.GetAttributes().Any(a => Eq(a.AttributeClass, Deferred))) return true;
            // a builder handed to the engine or the kit (a delegate that returns elements) runs when the engine asks for it: a list
            // row, a region's content, an overlay, a slot. Code in the same assembly is the caller's own helper: not decided here.
            // Every delegate handed to the engine or the kit runs when the engine calls it (a builder, a handler, an effect), unless the
            // parameter says it runs during the call ([Immediate]).
            if (p.Type.TypeKind == TypeKind.Delegate && p.ContainingAssembly?.Name is "FluentGpu.Engine" or "FluentGpu.Controls"
                && !p.GetAttributes().Any(a => a.AttributeClass?.Name == "ImmediateAttribute")) return true;
            // a delegate parameter of a boundary element's constructor (SkelRegionEl, ForEl, VirtualListEl, KeepAliveEl)
            return p.Type.TypeKind == TypeKind.Delegate && p.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } m
                   && IsBoundaryOrKind(m.ContainingType);
        }

        private static bool IsFrameworkType(INamedTypeSymbol t)
        {
            string ns = t.ContainingNamespace?.ToDisplayString() ?? "";
            return ns == "System" || ns.StartsWith("System.") || ns.StartsWith("Microsoft.");
        }

        private static bool Eq(ISymbol? a, ISymbol? b) => b is not null && SymbolEqualityComparer.Default.Equals(a, b);
    }

    private static void AnalyzeLambda(OperationAnalysisContext c, Known k)
    {
        var lambda = (IAnonymousFunctionOperation)c.Operation;
        if (!IsDeferred(lambda, k)) return;
        var declared = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var op in lambda.Body.DescendantsAndSelf())
            if (op is IVariableDeclaratorOperation d) declared.Add(d.Symbol);
        foreach (var p in lambda.Symbol.Parameters) declared.Add(p);
        var reported = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var op in lambda.Body.DescendantsAndSelf())
        {
            ISymbol? sym = op switch
            {
                ILocalReferenceOperation l => l.Local,
                IParameterReferenceOperation p => p.Parameter,
                _ => null,
            };
            if (sym is null || declared.Contains(sym) || reported.Contains(sym)) continue;
            // a local declared inside the lambda (a pattern variable: `x is { } tree`) is the lambda's own
            if (sym.DeclaringSyntaxReferences.Any(r => lambda.Syntax.Span.Contains(r.Span))) continue;
            // a nested lambda's own parameters are declared inside this one
            if (sym is IParameterSymbol ps && ps.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LambdaMethod } inner
                && IsInside(inner, lambda)) continue;
            ITypeSymbol? type = sym switch { ILocalSymbol l => l.Type, IParameterSymbol p => p.Type, _ => null };
            if (!k.HoldsElement(type)) continue;
            if (sym is ILocalSymbol local && FrozenAtDeclaration(local, c.CancellationToken)) continue;
            reported.Add(sym);
            c.ReportDiagnostic(Diagnostic.Create(Captured, op.Syntax.GetLocation(), sym.Name));
        }
    }

    /// <summary>FGEL003: inside ANY lambda (a row template, a builder, a handler: they run in another render's region, or none), an
    /// element stored into a field or a property of an object (a cache: <c>_rail ??= Rail()</c>) outlives that region unless frozen.</summary>
    private static void AnalyzeStores(OperationAnalysisContext c, Known k)
    {
        var lambda = (IAnonymousFunctionOperation)c.Operation;
        // a configure callback handed to an [Immediate] parameter fills the objects it is given; the callee freezes them before it keeps them
        bool ownsArgs = IsImmediateArgument(lambda);
        foreach (var op in lambda.Body.DescendantsAndSelf())
        {
            if (op is not IAssignmentOperation asg) continue;
            if (asg is ICompoundAssignmentOperation) continue;
            ISymbol? target = asg.Target switch
            {
                IFieldReferenceOperation f => f.Field,
                IPropertyReferenceOperation p when p.Property.SetMethod is { } => p.Property,
                _ => null,
            };
            if (target is null) continue;
            if (ownsArgs && asg.Target is IMemberReferenceOperation { Instance: IParameterReferenceOperation pr }
                && SymbolEqualityComparer.Default.Equals(pr.Parameter.ContainingSymbol, lambda.Symbol)) continue;
            // an object initializer of a NEW object inside the lambda is that object's own construction (an element's props)
            if (asg.Parent is IObjectOrCollectionInitializerOperation) continue;
            ITypeSymbol? type = target switch { IFieldSymbol f => f.Type, IPropertySymbol p => p.Type, _ => null };
            if (!k.HoldsElement(type)) continue;
            if (IsFrozenValue(asg.Value, k)) continue;
            c.ReportDiagnostic(Diagnostic.Create(Stored, asg.Syntax.GetLocation(), target.Name));
        }
    }

    /// <summary>A stored value that holds no tape element: a freeze call, or a tuple whose element-holding parts each are one
    /// (<c>(tier, Skeleton().Freeze())</c>).</summary>
    private static bool IsFrozenValue(IOperation value, Known k)
    {
        while (value is IConversionOperation conv) value = conv.Operand;
        if (value is ITupleOperation tuple)
            return tuple.Elements.All(e => !k.HoldsElement(e.Type) || IsFrozenValue(e, k));
        return value.Syntax is ExpressionSyntax v && IsFreezeCall(v);
    }

    private static bool IsImmediateArgument(IAnonymousFunctionOperation lambda)
    {
        IOperation? cur = lambda.Parent;
        while (cur is IDelegateCreationOperation or IConversionOperation or IParenthesizedOperation) cur = cur.Parent;
        return cur is IArgumentOperation { Parameter: { } p } && p.GetAttributes().Any(a => a.AttributeClass?.Name == "ImmediateAttribute");
    }

    private static bool IsInside(IMethodSymbol inner, IAnonymousFunctionOperation outer)
        => inner.DeclaringSyntaxReferences.Any(r => outer.Syntax.Span.Contains(r.Span));

    /// <summary>The lambda runs later: passed to a [Deferred] parameter or a boundary element's delegate, or assigned to a
    /// delegate member of an element (a handler, a skeleton proxy).</summary>
    private static bool IsDeferred(IAnonymousFunctionOperation lambda, Known k)
    {
        IOperation? cur = lambda.Parent;
        while (cur is IDelegateCreationOperation or IConversionOperation or IParenthesizedOperation) cur = cur.Parent;
        switch (cur)
        {
            case IArgumentOperation arg:
                return k.IsDeferredParameter(arg.Parameter);
            case ISimpleAssignmentOperation asg when asg.Target is IPropertyReferenceOperation pr:
                return pr.Property.Type.TypeKind == TypeKind.Delegate && k.IsBoundaryOrKind(pr.Property.ContainingType);
            case ISimpleAssignmentOperation asg2 when asg2.Target is IFieldReferenceOperation fr:
                return fr.Field.Type.TypeKind == TypeKind.Delegate && k.IsBoundaryOrKind(fr.Field.ContainingType);
        }
        return false;
    }

    /// <summary>A local whose only value is frozen where it is declared: <c>var x = ... .Freeze();</c> or <c>Ui.Static(...)</c>, never
    /// assigned again.</summary>
    private static bool FrozenAtDeclaration(ILocalSymbol local, System.Threading.CancellationToken ct)
    {
        foreach (var r in local.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax(ct) is not VariableDeclaratorSyntax v || v.Initializer?.Value is not { } init) return false;
            if (!IsFreezeCall(init)) return false;
            // every later assignment (`x = …`, `x ??= …`) must store a frozen value too
            var scope = v.FirstAncestorOrSelf<BlockSyntax>() ?? (SyntaxNode?)v.FirstAncestorOrSelf<MemberDeclarationSyntax>();
            if (scope is null) return true;
            foreach (var a in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (a.Left is IdentifierNameSyntax id && id.Identifier.Text == local.Name && !IsFreezeCall(a.Right)) return false;
            return true;
        }
        return false;
    }

    private static bool IsFreezeCall(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        if (e is ConditionalExpressionSyntax cond) return IsFreezeCall(cond.WhenTrue) && IsFreezeCall(cond.WhenFalse);
        if (e is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } co) return IsFreezeCall(co.Left) && IsFreezeCall(co.Right);
        if (e is LiteralExpressionSyntax lit && (lit.IsKind(SyntaxKind.NullLiteralExpression) || lit.IsKind(SyntaxKind.DefaultLiteralExpression))) return true;
        if (e is InvocationExpressionSyntax inv)
        {
            string name = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => "",
            };
            return name is "Freeze" or "Static";
        }
        if (e is ConditionalAccessExpressionSyntax ca) return IsFreezeCall(ca.WhenNotNull);
        return false;
    }

    private static void AnalyzeStatic(SymbolAnalysisContext c, Known k)
    {
        var sym = c.Symbol;
        if (!sym.IsStatic) return;
        ITypeSymbol? type = sym switch { IFieldSymbol f when !f.IsConst => f.Type, IPropertySymbol p => p.Type, _ => null };
        if (!k.HoldsElement(type)) return;
        foreach (var r in sym.DeclaringSyntaxReferences)
        {
            ExpressionSyntax? init = r.GetSyntax(c.CancellationToken) switch
            {
                VariableDeclaratorSyntax v => v.Initializer?.Value,
                PropertyDeclarationSyntax p => p.Initializer?.Value,
                _ => null,
            };
            if (init is null) continue;   // assigned elsewhere (a static constructor or a lazy ??=): not decidable here
            if (IsFreezeCall(init) || IsEmpty(init)) continue;
            c.ReportDiagnostic(Diagnostic.Create(Static, init.GetLocation(), sym.Name));
        }
    }

    /// <summary>An empty array or collection holds no element: <c>[]</c>, <c>Array.Empty&lt;Element&gt;()</c>, <c>default</c>.</summary>
    private static bool IsEmpty(ExpressionSyntax e)
        => e is CollectionExpressionSyntax { Elements.Count: 0 }
           || e is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Empty" } }
           || e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.DefaultLiteralExpression)
           // a default-constructed holder (a struct's empty value: `new()`): it holds no element until one is stored into it
           // a holder constructed from literals only (`new(ShowTrackArtist: false)`): a literal is never an element
           || e is BaseObjectCreationExpressionSyntax { Initializer: null } oc
              && (oc.ArgumentList is null || oc.ArgumentList.Arguments.All(a => a.Expression is LiteralExpressionSyntax));
}
