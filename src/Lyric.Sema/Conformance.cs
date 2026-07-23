using Lyric.AST;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// Interface-Konformanz als geteiltes Vokabular: welche Interfaces deklariert ein Typ,
/// implementiert er ein bestimmtes, ist ein Typ werfbar (§9)? Genutzt vom
/// <see cref="TypeChecker"/> (Constraints, throw/catch/throws) und vom
/// <see cref="ExceptionAnalyzer"/> (Propagation). Extend-Merge kommt in M4-4 dazu.
/// </summary>
internal static class Conformance
{
    /// <summary>Löst einen Constraint-/Interface-TypeNode zum Interface-Symbol auf (oder null).</summary>
    public static TypeSymbol? InterfaceOf(TypeNode node, BindingResult binding)
    {
        if (node is not NamedType nt) return null;
        var s = binding.Resolve(nt);
        if (s is ImportBindingSymbol ib) s = ib.Target;
        return s is TypeSymbol { Kind: TypeSymbolKind.Interface } it ? it : null;
    }

    public static IEnumerable<TypeSymbol> DeclaredInterfaces(TypeSymbol ts, BindingResult binding)
    {
        var list = ts.Declaration switch
        {
            StructDecl s => s.Interfaces,
            ClassDecl c => c.Interfaces,
            EnumDecl e => e.Interfaces,
            _ => (TypeNode[])[]
        };
        foreach (var t in list)
            if (InterfaceOf(t, binding) is { } iface) yield return iface;
    }

    public static bool Implements(TypeSymbol ts, TypeSymbol iface, BindingResult binding) =>
        DeclaredInterfaces(ts, binding).Any(it => ReferenceEquals(it, iface));

    /// <summary>Darf ein Wert dieses Typs geworfen/gefangen/deklariert werden (§9)?
    /// Throwable selbst, Typen mit Throwable in der Interface-Liste, Typ-Params mit
    /// Throwable-Constraint; Interfaces lenient (Signatur-Konformanz erst M4-4).</summary>
    public static bool IsThrowable(LyrType t, TypeSymbol? throwable, BindingResult binding)
    {
        if (throwable is null) return true; // Builtin fehlt → nicht kaskadieren
        return t switch
        {
            ErrorType => true,
            NamedRef { Symbol.Kind: TypeSymbolKind.Interface } => true,
            NamedRef nr => ReferenceEquals(nr.Symbol, throwable) || Implements(nr.Symbol, throwable, binding),
            GenericInstance gi => Implements(gi.Definition, throwable, binding),
            TypeParamType tp => tp.Param.Constraints.Any(c =>
                InterfaceOf(c, binding) is { } it && ReferenceEquals(it, throwable)),
            _ => false
        };
    }
}
