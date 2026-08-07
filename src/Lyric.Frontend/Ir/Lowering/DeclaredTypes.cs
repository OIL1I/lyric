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
/// <para>Builtins sind laut <c>Types.cs</c> <see cref="NamedType"/> mit einelementigem Pfad.
/// Dazu kommt seit M8/S2 <c>T[]</c> ueber einem Primitiv — <c>split</c> liefert
/// <c>string[]</c>, <c>toChars</c> liefert <c>char[]</c>. Die Grenze bleibt scharf: ein Array
/// hat, anders als eine Klasse, <b>kein Layout</b>, das der Host kennen muesste. Alles andere
/// meldet sich als Scope-Grenze, statt still etwas Falsches zu liefern.</para>
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

        // '?T' in einer nativen Signatur: 'readText' liefert '?string', 'env' auch. Ein
        // Fehlschlag, der ein gewoehnlicher Zustand der Welt ist, gehoert in den Rueckgabewert
        // und nicht in eine Exception — dafuer muss der Typ ausdrueckbar sein.
        if (node is NullableType option) return new IrOptionalType(Lower(option.Inner));

        // 'T[]' in einer nativen Signatur. Der Elementtyp bleibt primitiv: ein Array von
        // Objekten wuerde vom Host verlangen, ein Modul-Layout zu kennen.
        if (node is ArrayType { Size: null } array
            && array.Element is NamedType { Path.Length: 1, TypeArguments.Length: 0 } element
            && TypeFacts.FromBuiltinName(element.Path[0]) is { } elementPrimitive)
            return new IrArrayType(TypeLowering.Lower(elementPrimitive));

        throw new UnsupportedConstructException(
            "non-primitive type in a declared signature is not supported by this compiler version yet",
            node.Span);
    }
}
