using Lyric.AST;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>Where a member came from. A caller that shows a list wants to say so; a caller that only
/// needs names can ignore it.</summary>
public enum MemberSource
{
    /// <summary>Declared in the type's own body.</summary>
    Own,

    /// <summary>Added by an <c>extend</c> block visible from the asking module.</summary>
    Extension,

    /// <summary>A default method of an interface the type conforms to.</summary>
    InterfaceDefault,
}

public readonly record struct MemberCandidate(Symbol Symbol, MemberSource Source);

/// <summary>
/// Which members a type or module offers.
///
/// <para>The type checker answers the neighbouring question — <em>does member X exist</em> — as a
/// lookup with diagnostics, and it looks in the same three places in the same order: the type's own
/// body, the visible extensions, the default methods of its interfaces. This is that walk without
/// the name, for a caller that has to show all of them.</para>
///
/// <para><b>The two are separate code and can drift.</b> Rebuilding the lookup on this enumeration
/// would remove that risk and is not done here: the lookup carries the diagnostics and the generic
/// substitution, and rewriting it to serve a list is a change to the type checker rather than an
/// addition beside it. What holds them together for now is a test that every member this offers can
/// actually be reached.</para>
///
/// <para>Nothing here reports. An enumeration has no position to report at, and a caller asking what
/// exists is not making a mistake.</para>
/// </summary>
public static class MemberFacts
{
    /// <summary>
    /// What can be reached through a VALUE of this type: fields, instance methods, visible extension
    /// methods, and the default methods of its interfaces.
    /// </summary>
    /// <param name="from">The module doing the asking. An extension is offered only when that module
    /// can see the one declaring it — the same rule the lookup applies, read from the same place.
    /// <c>null</c> asks without a module and sees every extension.</param>
    public static IEnumerable<MemberCandidate> OfInstance(
        Compilation compilation, BindingResult binding, TypeSymbol type, ModuleSymbol? from)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in type.Members.Symbols)
        {
            // A static member belongs to the type, and a field of the type is not reachable through
            // an instance in the other direction either — both are LYR-SEM0055 at the lookup.
            if (symbol is FunctionSymbol { IsStatic: true } or GlobalSymbol or EnumVariantSymbol) continue;
            if (seen.Add(symbol.Name)) yield return new MemberCandidate(symbol, MemberSource.Own);
        }

        foreach (var extension in VisibleExtensions(compilation, type, from))
        {
            // Static extensions belong to the type. The lookup is more permissive here — its
            // instance path falls through to the extension without checking, so a static one is
            // callable on a value today — and this side deliberately is not: offering it would teach
            // a pattern that reads as an oversight rather than as a rule.
            if (extension.IsStatic) continue;

            // An own member wins over an extension, as it does at the lookup: the type's own body is
            // searched first and never falls through.
            if (seen.Add(extension.Name))
                yield return new MemberCandidate(extension, MemberSource.Extension);
        }

        foreach (var iface in Interfaces(compilation, binding, type, from))
            foreach (var member in iface.Members.Symbols)
            {
                // Defaults only. An abstract member has no body and is provided by the type itself,
                // which means it was already offered above under its own name.
                if (member is not FunctionSymbol { Declaration: FunctionDecl { Body: not null } }) continue;
                if (seen.Add(member.Name))
                    yield return new MemberCandidate(member, MemberSource.InterfaceDefault);
            }
    }

    /// <summary>
    /// What can be reached through the type ITSELF: static methods, static constants, enum variants,
    /// and static extension methods.
    /// </summary>
    public static IEnumerable<MemberCandidate> OfType(
        Compilation compilation, TypeSymbol type, ModuleSymbol? from)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in type.Members.Symbols)
        {
            if (symbol is not (FunctionSymbol { IsStatic: true } or GlobalSymbol or EnumVariantSymbol))
                continue;
            if (seen.Add(symbol.Name)) yield return new MemberCandidate(symbol, MemberSource.Own);
        }

        foreach (var extension in VisibleExtensions(compilation, type, from))
            if (extension.IsStatic && seen.Add(extension.Name))
                yield return new MemberCandidate(extension, MemberSource.Extension);
    }

    /// <summary>
    /// What a module offers. Everything when the asking module is the one that declares them,
    /// otherwise what carries <c>pub</c>.
    /// </summary>
    public static IEnumerable<Symbol> OfModule(ModuleSymbol module, ModuleSymbol? from)
    {
        var own = ReferenceEquals(module, from);

        foreach (var symbol in module.Members.Symbols)
        {
            // An import binding is a name this module took in; it is not part of what the module
            // OFFERS, and offering it would put another module's names under this one's.
            if (symbol is ImportBindingSymbol) continue;

            if (own || Exported(symbol)) yield return symbol;
        }
    }

    private static bool Exported(Symbol symbol) => symbol switch
    {
        FunctionSymbol f => f.Visibility == Visibility.Public,
        TypeSymbol t => t.Visibility == Visibility.Public,
        GlobalSymbol g => g.Visibility == Visibility.Public,

        // An external symbol stands for a module outside the compilation; nothing is known about it
        // and nothing is claimed.
        _ => false,
    };

    private static IEnumerable<FunctionSymbol> VisibleExtensions(
        Compilation compilation, TypeSymbol type, ModuleSymbol? from) =>
        compilation.Extensions.MethodsFor(type)
            .Where(extension => from is null || compilation.Sees(from, extension.Module))
            .Select(extension => extension.Symbol);

    /// <summary>
    /// The interfaces a type conforms to: those in its own declaration, plus those an
    /// <c>extend</c> block adds — the same two sources the lookup walks.
    /// </summary>
    private static IEnumerable<TypeSymbol> Interfaces(
        Compilation compilation, BindingResult binding, TypeSymbol type, ModuleSymbol? from)
    {
        foreach (var iface in Conformance.DeclaredInterfaces(type, binding)) yield return iface;

        foreach (var block in compilation.Extensions.Blocks)
        {
            if (!ReferenceEquals(block.Target, type)) continue;
            if (from is not null && !compilation.Sees(from, block.Module)) continue;

            foreach (var node in block.Decl.Interfaces)
                if (Conformance.InterfaceOf(node, binding) is { } iface)
                    yield return iface;
        }
    }
}
