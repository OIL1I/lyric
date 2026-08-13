using Lyric.AST;
using Lyric.Resolver;

namespace Lyric.Ir.Lowering;

/// <summary>
/// What a HOST TYPE is, in ONE place.
///
/// <para>THE RULE: a <c>class</c> in a NATIVE module that has NO FIELD and NO METHOD BODY. Both
/// together, and both are necessary — a field-less class alone would be an ordinary (if useless) class
/// in user code, and a native module also contains ordinary classes with fields.</para>
///
/// <para>"No field" is the actual statement, not "empty": a host type has no layout this module knows.
/// Methods change nothing about that — they are natives and live at the host, exactly like the free
/// functions.</para>
///
/// <para>NO MARKER. <c>@host</c> would be clearer, but attributes are post-v1; introducing them would
/// mean making a grammar decision for a tooling topic. "An empty class in a native module" says the
/// same thing: a type without content, about which the module knows nothing.</para>
///
/// <para>WHY THIS FILE EXISTS. The question is asked at TWO places — when lowering a native signature
/// (<see cref="DeclaredTypes"/>, through the syntactic node) and when lowering a sema type at the call
/// site (<see cref="TypeTable"/>, through the symbol). With it in one place only, the same class gets
/// lowered once as a host type and once as an ordinary reference, and the verifier reports
/// <c>cannot compare IrRefType with IrHostType</c>.</para>
/// </summary>
internal static class HostTypes
{
    /// <summary>The name when <paramref name="symbol"/> is a host type, <c>null</c> otherwise.
    /// </summary>
    public static string? NameOf(TypeSymbol? symbol, Compilation? compilation)
    {
        if (compilation is null) return null;
        if (symbol is not { Kind: TypeSymbolKind.Class, Declaration: ClassDecl declaration })
            return null;

        // A field or a method body makes it an ordinary class: the module then knows a layout or code,
        // and neither belongs to the host.
        foreach (var member in declaration.Members)
            if (member is not FunctionDecl { Body: null }) return null;

        // Which module does the declaration stand in? The symbol table does not show that, so it is
        // searched: the module list is short, and this question arises only for an empty class.
        foreach (var module in compilation.Modules)
        {
            if (!ReferenceEquals(module.Members.LookupLocal(symbol.Name), symbol)) continue;
            return compilation.IsNative(module) ? symbol.Name : null;
        }

        return null;
    }
}
