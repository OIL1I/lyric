using Lyric.Core;

namespace Lyric.Ir
{
    public enum IrScalar
    {
        I8, I16, I32, I64,
        U8, U16, U32, U64,
        F32, F64,
        Bool, Char, String, Void
    }

    /// <summary>
    /// Basis der IR-Typen.
    /// </summary>
    public abstract record IrType
    {
        /// <summary>
        /// Typgleichheit zweier IR-Typen. Der <c>default</c>-Wurf ist Absicht und dieselbe
        /// Konvention wie bei <see cref="TypeLowering.Lower"/> („not lowerable in current version")
        /// und <c>IrPrinter.TypeStr</c> („not printable"): eine totale Funktion über das heutige
        /// Typ-Universum, die laut wird, sobald es wächst. Kommt ein zusammengesetzter Typ
        /// (Array, Tupel, Referenz) dazu, muss hier ein Fall her — der Wurf nennt die Stelle.
        /// </summary>
        /// <remarks>Ein <c>default</c>, der <c>false</c> liefert, wäre die schlechtere Wahl: der
        /// Verifier vergleicht Typen an rund zwanzig Stellen und hätte beim ersten nicht-skalaren
        /// Typ eine Flut falscher Typ-Mismatches gemeldet — der Fehler hätte nach IR-Bug
        /// ausgesehen statt nach Vergleichs-Bug.</remarks>
        public static bool Equal(IrType a, IrType b)
        {
            switch (a, b)
            {
                case (IrScalarType x, IrScalarType y):
                    return x.Kind == y.Kind;
                case (IrRefType x, IrRefType y):
                    return x.Type == y.Type;
                case (IrArrayType x, IrArrayType y):
                    return Equal(x.Element, y.Element);
                case (IrOptionalType x, IrOptionalType y):
                    return Equal(x.Inner, y.Inner);
                case (IrEnumType x, IrEnumType y):
                    return x.Type == y.Type;
                case (IrInterfaceType x, IrInterfaceType y):
                    return x.Type == y.Type;
                case (IrScalarType or IrRefType or IrArrayType or IrOptionalType or IrEnumType
                          or IrInterfaceType,
                      IrScalarType or IrRefType or IrArrayType or IrOptionalType or IrEnumType
                          or IrInterfaceType):
                    return false; // verschiedene Sorten — vergleichbar, nur eben ungleich
                default:
                    throw new InternalCompilationException(
                        $"ir-type: cannot compare {a.GetType().Name} with {b.GetType().Name}");
            }
        }
    }

    public sealed record IrScalarType(IrScalar Kind) : IrType;

    /// <summary>
    /// Referenz auf eine Instanz des Typs <see cref="Type"/> (Sprache.md §3.3, <c>class</c>).
    /// Zuweisung kopiert den Verweis, nicht das Objekt.
    ///
    /// <para><b>Nur die Id, nicht das Layout.</b> Die Feldliste steht einmal in
    /// <c>IrModule.Types</c>. Trüge der Typ sie selbst, müsste <see cref="IrType.Equal"/>
    /// strukturell vergleichen — und liefe bei <c>class Node { next: Node }</c> in eine
    /// Endlosschleife. So ist Gleichheit ein <c>int</c>-Vergleich und Rekursion kostenlos.</para>
    /// </summary>
    public sealed record IrRefType(TypeId Type) : IrType;

    /// <summary>
    /// Ein wachsendes Array (<c>T[]</c>, Sprache.md §4). Wie <see cref="IrRefType"/> eine Referenz:
    /// Zuweisung teilt das Array, sie kopiert es nicht.
    ///
    /// <para><b>Der Elementtyp steht inline</b>, nicht als Tabellen-Index — anders als bei einer
    /// Klasse. Das geht, weil ein Array-Typ nicht rekursiv sein kann: <c>int[][]</c> ist endlich
    /// tief, ein <c>class Node { next: Node }</c> nicht. Wo keine Rekursion droht, ist die
    /// Indirektion nur Kosten.</para>
    /// </summary>
    public sealed record IrArrayType(IrType Element) : IrType;

    /// <summary>
    /// <c>?T</c> (Sprache.md §7). Wie beim Array steht der innere Typ inline — auch ein Optional
    /// kann nicht rekursiv sein.
    ///
    /// <para><b>Nicht schachtelbar</b>: <c>??T</c> gibt es nicht. Die Laufzeit-Darstellung
    /// unterscheidet „kein Wert" an der leeren Referenz, und die kann nur eine Ebene tragen.</para>
    /// </summary>
    public sealed record IrOptionalType(IrType Inner) : IrType;

    /// <summary>
    /// Ein Enum (Sprache.md §3.4). Wie <see cref="IrRefType"/> über eine <see cref="TypeId"/> und
    /// nicht inline: ein Enum hat eine Deklaration und darf rekursiv sein
    /// (<c>enum Tree { Leaf, Node(Tree, Tree) }</c>).
    ///
    /// <para>Ein Wert dieses Typs ist zur Laufzeit die Instanz <b>einer</b> Variante; welcher,
    /// steht in deren Slot 0. Jede Variante hat ihr eigenes Layout — siehe
    /// <c>docs/Bytecode.md</c> §2.</para>
    /// </summary>
    public sealed record IrEnumType(TypeId Type) : IrType;

/// <summary>
/// Ein Wert, der ueber ein Interface angesprochen wird — Lyrics <c>dyn Trait</c>.
///
/// <para>Traegt wie <see cref="IrRefType"/> nur seine Id, nicht seine Methodenliste: sonst muesste
/// <c>IrType.Equal</c> strukturell vergleichen und liefe bei einem Interface, das sich selbst in
/// einer Signatur nennt, in eine Endlosschleife.</para>
///
/// <para><b>Zur Laufzeit ist das kein blosser Zeiger</b>, sondern ein Fat Pointer aus Objekt und
/// konkretem Typindex — <c>LyrValue</c> hat beide Felder ohnehin, und bei einer Referenz ist
/// <c>Bits</c> heute ungenutzt. Deshalb kostet ein Interface-Wert keine Allokation, und ein Objekt,
/// das nie ueber ein Interface laeuft, zahlt gar nichts. Die Alternative — ein Typ-Tag in Slot 0
/// jedes Objekts — haette jeden Feldindex verschoben und jedes Objekt ein Wort gekostet, auch die
/// Mehrzahl ohne Interface.</para>
/// </summary>
public sealed record IrInterfaceType(TypeId Type) : IrType;
}