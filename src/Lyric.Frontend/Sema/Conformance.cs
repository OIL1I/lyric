using Lyric.AST;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// Interface conformance as shared vocabulary: which interfaces does a type declare, does it
/// implement a given one, is a type throwable? Used by the <see cref="TypeChecker"/> for
/// constraints and throw/catch/throws, and by the <see cref="ExceptionAnalyzer"/> for propagation.
///
/// <para>Public because the IR lowering asks the same question to build the vtable rows. Rebuilding
/// it there would be a second truth about when a type satisfies an interface, and the two answers
/// would have to agree exactly, or the runtime dispatches on something the sema never checked.</para>
/// </summary>
public static class Conformance
{
    /// <summary>Resolves a constraint or interface TypeNode to its interface symbol, or null.</summary>
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
        // The transitive closure, not just the written list: declaring an interface implies
        // conforming to its parents (§3.5). Every consumer — throwability, witness emission,
        // default lookup — wants exactly that implication, so it lives in ONE place.
        var seen = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
        foreach (var t in list)
            if (InterfaceOf(t, binding) is { } iface)
                foreach (var i in WithParents(iface, binding, seen))
                    yield return i;
    }

    /// <summary>The parent interfaces one interface declares, direct only.</summary>
    public static IEnumerable<TypeSymbol> ParentsOf(TypeSymbol iface, BindingResult binding)
    {
        if (iface.Declaration is not InterfaceDecl decl) yield break;
        foreach (var t in decl.Interfaces)
            if (InterfaceOf(t, binding) is { } parent) yield return parent;
    }

    /// <summary>An interface plus its transitive parents. The set breaks cycles — those are
    /// diagnosed separately (LYR-SEM0078), and the walk must survive them regardless.</summary>
    public static IEnumerable<TypeSymbol> WithParents(TypeSymbol iface, BindingResult binding,
        HashSet<TypeSymbol>? seen = null)
    {
        seen ??= new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
        if (!seen.Add(iface)) yield break;
        yield return iface;
        foreach (var p in ParentsOf(iface, binding))
            foreach (var t in WithParents(p, binding, seen))
                yield return t;
    }

    public static bool Implements(TypeSymbol ts, TypeSymbol iface, BindingResult binding) =>
        DeclaredInterfaces(ts, binding).Any(it => ReferenceEquals(it, iface));

    /// <summary>May a value of this type be thrown, caught or declared? Throwable itself, types with
    /// Throwable in their interface list, and type parameters with a Throwable constraint qualify;
    /// interfaces are treated leniently.</summary>
    public static bool IsThrowable(LyrType t, TypeSymbol? throwable, BindingResult binding)
    {
        if (throwable is null) return true; // the builtin is missing, so do not cascade
        return t switch
        {
            ErrorType => true,
            NamedRef { Symbol.Kind: TypeSymbolKind.Interface } => true,
            NamedRef nr => ReferenceEquals(nr.Symbol, throwable) || Implements(nr.Symbol, throwable, binding),
            GenericInstance gi => Implements(gi.Definition, throwable, binding),
            TypeParamType tp => tp.Param.Constraints.Any(c =>
                InterfaceOf(c, binding) is { } it
                && WithParents(it, binding).Any(i => ReferenceEquals(i, throwable))),
            _ => false
        };
    }
}
