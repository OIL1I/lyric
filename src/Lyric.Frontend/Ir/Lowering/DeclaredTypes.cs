using Lyric.AST;
using Lyric.Core;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Syntaktischer <see cref="TypeNode"/> → <see cref="IrType"/>.
///
/// <para>Nötig, weil die Sema die aufgelösten Signatur-Typen nicht in <c>TypeResult</c> ablegt —
/// dort stehen nur Ausdrucks-Typen. Für Rückgabetypen und die Parameter nativer Deklarationen
/// muss das Lowering den Knoten selbst lesen.</para>
///
/// <para>Builtins sind laut <c>Types.cs</c> <see cref="NamedType"/> mit einelementigem Pfad — mehr
/// als Primitives braucht der heutige Stand nicht, und alles andere meldet sich als Scope-Grenze
/// statt still etwas Falsches zu liefern.</para>
/// </summary>
internal static class DeclaredTypes
{
    private static readonly IrType VoidType = new IrScalarType(IrScalar.Void);

    public static IrType Lower(TypeNode? node)
    {
        if (node is null) return VoidType; // fehlender Rückgabetyp = void

        if (node is NamedType { Path.Length: 1, TypeArguments.Length: 0 } named
            && TypeFacts.FromBuiltinName(named.Path[0]) is { } primitive)
            return TypeLowering.Lower(primitive);

        throw new UnsupportedConstructException(
            "non-primitive type in a declared signature is not supported by this compiler version yet",
            node.Span);
    }
}
