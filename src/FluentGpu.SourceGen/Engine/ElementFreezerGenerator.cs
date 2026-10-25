using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.SourceGen.Engine
{
    /// <summary>
    /// PERF r3 MODEL M4b (DESIGN 2.2, escapes): the in-place ELEMENT FREEZERS. An element built during a render lives on that
    /// render's tape; the objects that carry elements past the render (a component's props, a component instance's fields) are
    /// frozen by the reconciler where it hands them on (<c>FluentGpu.Dsl.ElementFreezer.FreezeObject</c>). This generator emits,
    /// for every class/record declared in the compilation that holds elements (directly, in a list, or through another such
    /// type), a freezer registered from a module initializer. Private fields are reached with <c>UnsafeAccessor</c>; a type that
    /// is not nameable from outside (a private nested component) with <c>UnsafeAccessorType</c>. Generic types are skipped (the
    /// engine's few implement <c>IElementHolder</c> by hand). Active only when the compilation references FluentGpu.Dsl.ElementFreezer.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class ElementFreezerGenerator : IIncrementalGenerator
    {
        private const string Freezer = "global::FluentGpu.Dsl.ElementFreezer";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var decls = context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => node is ClassDeclarationSyntax || node is RecordDeclarationSyntax,
                    transform: static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)ctx.Node, ct) as INamedTypeSymbol)
                .Where(static t => t is not null)
                .Collect();
            context.RegisterSourceOutput(context.CompilationProvider.Combine(decls), static (spc, pair) => Emit(spc, pair.Left, pair.Right!));
        }

        private enum Kind { None, Element, NullableElement, ElementList, ElementArray, ElementListRef, ElementListIface, ElementKindValue, Holder, HolderEach, Obj, HolderStruct, NullableHolderStruct }

        private static bool IsCandidate(INamedTypeSymbol t, Compilation comp)
        {
            if (t.IsStatic || IsGenericAnywhere(t)) return false;
            if (t.TypeKind == TypeKind.Class) return true;
            // a struct of this compilation that holds elements (a record struct of a template's parts) is frozen through the ref of the
            // member that holds it: it needs a nameable type for the accessor's ref parameter
            return t.TypeKind == TypeKind.Struct && !t.IsRefLikeType && Nameable(t, comp)
                   && !t.AllInterfaces.Any(i => i.ToDisplayString() == "FluentGpu.Dsl.IElement");
        }

        private sealed class Member
        {
            public string Field = "";
            public string FieldType = "";
            public Kind Kind;
            public INamedTypeSymbol? Struct;
        }

        private static void Emit(SourceProductionContext spc, Compilation comp, ImmutableArray<INamedTypeSymbol?> declared)
        {
            if (comp.GetTypeByMetadataName("FluentGpu.Dsl.ElementFreezer") is null) return;
            var element = comp.GetTypeByMetadataName("FluentGpu.Dsl.Element");
            var elementList = comp.GetTypeByMetadataName("FluentGpu.Dsl.ElementList");
            var ielement = comp.GetTypeByMetadataName("FluentGpu.Dsl.IElement");
            if (element is null || elementList is null || ielement is null) return;

            var types = new List<INamedTypeSymbol>();
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var t in declared)
                if (t is not null && seen.Add(t) && IsCandidate(t, comp)) types.Add(t);

            // Fixpoint: a source class holds elements when one of its members (or its base) does.
            var holders = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var members = new Dictionary<INamedTypeSymbol, List<Member>>(SymbolEqualityComparer.Default);
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var t in types)
                {
                    var ms = Members(t, element, elementList, ielement, holders, comp, null);
                    members[t] = ms;
                    bool holds = ms.Count > 0 || (t.BaseType is { } b && holders.Contains(b));
                    if (holds && holders.Add(t)) changed = true;
                }
            }
            // FGEL004: a member that mentions an element but is not one the freezer reaches (an IElementHolder freezes by hand)
            foreach (var t in types)
            {
                if (t.AllInterfaces.Any(i => i.ToDisplayString() == "FluentGpu.Dsl.IElementHolder")) continue;
                Members(t, element, elementList, ielement, holders, comp, spc);
            }
            if (holders.Count == 0) return;

            string asm = Sanitize(comp.AssemblyName ?? "Asm");
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/> ElementFreezerGenerator (PERF r3 MODEL M4b): in-place freezers of the types that hold elements.\n");
            sb.Append("#nullable disable\n#pragma warning disable\n");
            sb.Append("namespace FluentGpu.Generated\n{\n");
            sb.Append("    internal static class ElementFreezers_").Append(asm).Append("\n    {\n");
            var ordered = holders.OrderBy(t => t.ToDisplayString()).ToList();
            var index = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
            for (int i = 0; i < ordered.Count; i++) index[ordered[i]] = i;

            sb.Append("        [global::System.Runtime.CompilerServices.ModuleInitializer]\n");
            sb.Append("        internal static void Register()\n        {\n");
            foreach (var t in ordered)
            {
                if (t.IsAbstract || t.TypeKind == TypeKind.Struct) continue;
                sb.Append("            ").Append(Freezer).Append(".Register(");
                if (Nameable(t, comp)) sb.Append("typeof(").Append(Fq(t)).Append(")");
                else sb.Append("global::System.Type.GetType(\"").Append(RuntimeName(t)).Append("\", throwOnError: true)");
                sb.Append(", Freeze_").Append(index[t]).Append(");\n");
            }
            sb.Append("        }\n\n");

            foreach (var t in ordered)
            {
                int i = index[t];
                bool isStruct = t.TypeKind == TypeKind.Struct;
                bool nameable = Nameable(t, comp);
                string self = nameable ? Fq(t) : "object";
                string attr = nameable ? "" : "[global::System.Runtime.CompilerServices.UnsafeAccessorType(\"" + RuntimeName(t) + "\")] ";
                if (isStruct)
                    sb.Append("        private static void Freeze_").Append(i).Append("(ref ").Append(self).Append(" x)\n        {\n");
                else
                {
                    sb.Append("        private static void Freeze_").Append(i).Append("(object o)\n        {\n");
                    sb.Append("            var x = ").Append(nameable ? "(" + self + ")o" : "o").Append(";\n");
                    if (t.BaseType is { } b && index.TryGetValue(b, out int bi)) sb.Append("            Freeze_").Append(bi).Append("(o);\n");
                }
                int m = 0;
                foreach (var mem in members[t])
                {
                    string acc = "A" + i + "_" + m++;
                    string call = acc + (isStruct ? "(ref x)" : "(x)");
                    switch (mem.Kind)
                    {
                        case Kind.Element: case Kind.NullableElement: case Kind.ElementList:
                            sb.Append("            ").Append(Freezer).Append(".Freeze(ref ").Append(call).Append(");\n"); break;
                        case Kind.ElementListIface:
                            sb.Append("            { ref var l = ref ").Append(call).Append("; l = (").Append(mem.FieldType).Append(")").Append(Freezer)
                              .Append(".FreezeList(l); }\n"); break;
                        case Kind.ElementArray: case Kind.ElementListRef:
                            sb.Append("            ").Append(Freezer).Append(".Freeze(").Append(call).Append(");\n"); break;
                        case Kind.ElementKindValue:
                            sb.Append("            ").Append(Freezer).Append(".FreezeKind(ref ").Append(call).Append(");\n"); break;
                        case Kind.Holder: case Kind.Obj:
                            sb.Append("            ").Append(Freezer).Append(".FreezeObject(").Append(call).Append(");\n"); break;
                        case Kind.HolderEach:
                            sb.Append("            ").Append(Freezer).Append(".FreezeEach(").Append(call).Append(");\n"); break;
                        case Kind.HolderStruct:
                            sb.Append("            Freeze_").Append(index[mem.Struct!]).Append("(ref ").Append(call).Append(");\n"); break;
                        case Kind.NullableHolderStruct:
                            sb.Append("            { ref var n = ref ").Append(call).Append("; if (n.HasValue) { var v = n.GetValueOrDefault(); Freeze_")
                              .Append(index[mem.Struct!]).Append("(ref v); n = v; } }\n"); break;
                    }
                }
                sb.Append("        }\n");
                m = 0;
                foreach (var mem in members[t])
                {
                    string acc = "A" + i + "_" + m++;
                    sb.Append("        [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = \"")
                      .Append(mem.Field).Append("\")] private static extern ref ").Append(mem.FieldType).Append(' ').Append(acc)
                      .Append('(').Append(attr).Append(isStruct ? "ref " : "").Append(self).Append(" o);\n");
                }
                sb.Append('\n');
            }
            sb.Append("    }\n}\n");
            spc.AddSource("ElementFreezers.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        private static List<Member> Members(INamedTypeSymbol t, INamedTypeSymbol element, INamedTypeSymbol elementList, INamedTypeSymbol ielement,
                                            HashSet<INamedTypeSymbol> holders, Compilation comp, SourceProductionContext? report)
        {
            var list = new List<Member>();
            foreach (var sym in t.GetMembers())
            {
                if (sym.IsStatic) continue;
                ITypeSymbol? type; string? field;
                if (sym is IFieldSymbol f)
                {
                    if (f.IsConst) continue;
                    if (f.IsImplicitlyDeclared && f.AssociatedSymbol is null) continue;   // e.g. a primary-constructor capture: unnameable
                    type = f.Type; field = f.Name;
                    if (f.AssociatedSymbol is IPropertySymbol) continue;   // the property path below names it
                }
                else if (sym is IPropertySymbol p && IsAutoProperty(p))
                {
                    type = p.Type; field = "<" + p.Name + ">k__BackingField";
                }
                else continue;
                var kind = Classify(type, element, elementList, ielement, holders, comp);
                INamedTypeSymbol? st = null;
                if (kind == Kind.None && type is INamedTypeSymbol vt && vt.IsValueType)
                {
                    var under = vt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? vt.TypeArguments[0] as INamedTypeSymbol : vt;
                    if (under is not null && holders.Contains(under) && under.TypeKind == TypeKind.Struct)
                    { st = under; kind = ReferenceEquals(under, vt) ? Kind.HolderStruct : Kind.NullableHolderStruct; }
                }
                bool reachable = kind != Kind.None && Nameable(type, comp);   // the accessor's return type must be nameable
                if (!reachable)
                {
                    if (report is { } r && MentionsElement(type, element, elementList, ielement))
                        r.ReportDiagnostic(Diagnostic.Create(Unreachable, sym.Locations.FirstOrDefault(), t.Name, sym.Name, type.ToDisplayString()));
                    continue;
                }
                list.Add(new Member { Field = field!, FieldType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), Kind = kind, Struct = st });
            }
            return list;
        }

        private static bool IsAutoProperty(IPropertySymbol p)
        {
            if (p.IsIndexer || p.IsAbstract || p.GetMethod is null) return false;
            if (p.IsImplicitlyDeclared) return p.ContainingType.IsRecord;   // a positional record parameter's property
            foreach (var r in p.DeclaringSyntaxReferences)
            {
                var syn = r.GetSyntax();
                if (syn is ParameterSyntax) return p.ContainingType.IsRecord;   // a positional record parameter's property
                if (syn is not PropertyDeclarationSyntax decl) return false;
                if (decl.ExpressionBody is not null || decl.AccessorList is null) return false;
                foreach (var a in decl.AccessorList.Accessors)
                    if (a.Body is not null || a.ExpressionBody is not null) return false;
                return true;
            }
            return false;
        }

        private static Kind Classify(ITypeSymbol type, INamedTypeSymbol element, INamedTypeSymbol elementList, INamedTypeSymbol ielement,
                                     HashSet<INamedTypeSymbol> holders, Compilation comp)
        {
            if (SymbolEqualityComparer.Default.Equals(type, element)) return Kind.Element;
            if (SymbolEqualityComparer.Default.Equals(type, elementList)) return Kind.ElementList;
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nul
                && SymbolEqualityComparer.Default.Equals(nul.TypeArguments[0], element)) return Kind.NullableElement;
            if (type is IArrayTypeSymbol arr)
            {
                if (SymbolEqualityComparer.Default.Equals(arr.ElementType, element) || IsNullableOf(arr.ElementType, element)) return Kind.ElementArray;
                if (arr.ElementType is INamedTypeSymbol ae && ae.TypeKind == TypeKind.Class && IsHolderType(ae, holders, comp)) return Kind.HolderEach;
                return Kind.None;
            }
            if (type is INamedTypeSymbol nt)
            {
                if (nt.IsValueType)
                {
                    // a value element kind (BoxEl, TextEl, ...) held by value
                    if (nt.ContainingNamespace?.ToDisplayString() is "FluentGpu.Dsl" or "FluentGpu.Hooks"
                        && nt.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, ielement))) return Kind.ElementKindValue;
                    return Kind.None;
                }
                if (nt.IsGenericType && nt.TypeArguments.Length == 1)
                {
                    string def = nt.OriginalDefinition.ToDisplayString();
                    var arg = nt.TypeArguments[0];
                    bool listLike = def is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IReadOnlyList<T>"
                        or "System.Collections.Generic.IList<T>" or "System.Collections.Generic.IEnumerable<T>"
                        or "System.Collections.Generic.IReadOnlyCollection<T>" or "System.Collections.Generic.ICollection<T>";
                    if (listLike)
                    {
                        // a List<Element> is frozen in place; an interface-typed list may be a read-only one (a collection expression's
                        // synthesized list): it is replaced through the member's ref by a frozen array
                        if (SymbolEqualityComparer.Default.Equals(arg, element))
                            return def == "System.Collections.Generic.List<T>" ? Kind.ElementListRef : Kind.ElementListIface;
                        if (IsNullableOf(arg, element) && def == "System.Collections.Generic.List<T>") return Kind.ElementListRef;
                        if (arg is INamedTypeSymbol an && an.TypeKind == TypeKind.Class && IsHolderType(an, holders, comp)) return Kind.HolderEach;
                        return Kind.None;
                    }
                }
                if (nt.TypeKind == TypeKind.Class && IsHolderType(nt, holders, comp)) return Kind.Holder;
            }
            return Kind.None;
        }

        /// <summary>A class that may hold elements: a holder of this compilation, or a FluentGpu/Wavee class of a referenced assembly
        /// (its own generator registered it; an element-free one is a registry miss at run time).</summary>
        private static bool IsHolderType(INamedTypeSymbol t, HashSet<INamedTypeSymbol> holders, Compilation comp)
        {
            if (holders.Contains(t)) return true;
            if (SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, comp.Assembly)) return false;
            if (t.TypeKind != TypeKind.Class || t.SpecialType != SpecialType.None) return false;
            if (t.BaseType?.ToDisplayString() is "System.MulticastDelegate" or "System.Delegate") return false;
            string ns = t.ContainingNamespace?.ToDisplayString() ?? "";
            if (ns.StartsWith("FluentGpu.Signals") || ns.StartsWith("FluentGpu.Scene") || ns.StartsWith("FluentGpu.Rhi") || ns.StartsWith("FluentGpu.Render")) return false;
            // only an assembly that can build elements (it references the engine) has types that hold them
            var a = t.ContainingAssembly;
            if (a is null) return false;
            if (a.Name == "FluentGpu.Engine") return false;   // the engine's own holders are boundary elements and its internals
            foreach (var m in a.Modules)
                foreach (var r in m.ReferencedAssemblies)
                    if (r.Name == "FluentGpu.Engine") return true;
            return false;
        }

        private static bool IsNullableOf(ITypeSymbol t, INamedTypeSymbol element)
            => t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
               && SymbolEqualityComparer.Default.Equals(n.TypeArguments[0], element);

        /// <summary>The type mentions an element anywhere (itself, an array's element, a generic argument, a tuple member): a member of
        /// that type in a holder must be one the freezer can reach.</summary>
        private static bool MentionsElement(ITypeSymbol t, INamedTypeSymbol element, INamedTypeSymbol elementList, INamedTypeSymbol ielement, int depth = 0)
        {
            if (depth > 4) return false;
            if (SymbolEqualityComparer.Default.Equals(t, element) || SymbolEqualityComparer.Default.Equals(t, elementList)) return true;
            if (t is IArrayTypeSymbol a) return MentionsElement(a.ElementType, element, elementList, ielement, depth + 1);
            if (t is not INamedTypeSymbol n || n.TypeKind == TypeKind.Delegate) return false;
            if (n.IsValueType && n.ContainingNamespace?.ToDisplayString() is "FluentGpu.Dsl" or "FluentGpu.Hooks"
                && n.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, ielement))) return true;
            foreach (var arg in n.TypeArguments) if (MentionsElement(arg, element, elementList, ielement, depth + 1)) return true;
            if (n.IsTupleType) foreach (var f in n.TupleElements) if (MentionsElement(f.Type, element, elementList, ielement, depth + 1)) return true;
            return false;
        }

        private static readonly DiagnosticDescriptor Unreachable = new(
            id: "FGEL004",
            title: "A member holds elements the element freezer cannot reach",
            messageFormat: "'{0}.{1}' ({2}) holds elements that outlive the render that built them, but the generated freezer cannot reach it: hold them as Element, Element?, ElementList, Element[]/Element?[] or List<Element>/List<Element?>, make the type nameable, or implement IElementHolder",
            category: "FluentGpu.Elements",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static bool IsGenericAnywhere(INamedTypeSymbol t)
        {
            for (var c = t; c is not null; c = c.ContainingType) if (c.TypeParameters.Length > 0) return true;
            return false;
        }

        private static bool Nameable(ITypeSymbol t, Compilation comp)
        {
            if (t is IArrayTypeSymbol a) return Nameable(a.ElementType, comp);
            if (t is INamedTypeSymbol n)
            {
                for (var c = n; c is not null; c = c.ContainingType)
                {
                    if (c.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal) return false;
                    if (c.DeclaredAccessibility == Accessibility.Internal && !SymbolEqualityComparer.Default.Equals(c.ContainingAssembly, comp.Assembly)) return false;
                }
                foreach (var arg in n.TypeArguments) if (!Nameable(arg, comp)) return false;
                return true;
            }
            return t is ITypeParameterSymbol ? false : true;
        }

        private static string Fq(INamedTypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static string RuntimeName(INamedTypeSymbol t)
        {
            var parts = new List<string>();
            for (var c = t; c is not null; c = c.ContainingType) parts.Insert(0, c.MetadataName);
            string ns = t.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
            return ns + string.Join("+", parts) + ", " + t.ContainingAssembly.Name;
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return sb.ToString();
        }
    }
}
