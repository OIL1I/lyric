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
                case (IrStructType x, IrStructType y):
                    return x.Type == y.Type;
                case (IrCoroutineType x, IrCoroutineType y):
                    return Equal(x.Yield, y.Yield);
                case (IrFunctionType x, IrFunctionType y):
                    // Strukturell, und das terminiert: ein Funktionstyp kann sich nur ueber einen
                    // benannten Typ selbst enthalten, und der vergleicht ueber seine Id.
                    return x.Parameters.Length == y.Parameters.Length
                           && Equal(x.Return, y.Return)
                           && x.Parameters.Zip(y.Parameters).All(pair => Equal(pair.First, pair.Second));
                case (IrScalarType or IrRefType or IrArrayType or IrOptionalType or IrEnumType
                          or IrInterfaceType or IrStructType or IrFunctionType or IrCoroutineType,
                      IrScalarType or IrRefType or IrArrayType or IrOptionalType or IrEnumType
                          or IrInterfaceType or IrStructType or IrFunctionType or IrCoroutineType):
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

/// <summary>
/// Ein <c>struct</c>: dasselbe Layout wie <see cref="IrRefType"/>, aber <b>Wert-Semantik</b>
/// (Sprache.md §3.2). Zuweisung kopiert.
///
/// <para>Wie bei einer Klasse traegt der Typ nur seine Id. Das ist hier sogar zwingend: ein
/// struct darf sich nicht selbst enthalten — es waere unendlich gross —, und die Sema lehnt das
/// als <c>LYR-SEM0056</c> ab. Der Verzicht auf strukturellen Vergleich ist trotzdem derselbe
/// Gewinn wie bei P1.</para>
///
/// <para><b>Zur Laufzeit dasselbe Slot-Array wie ein Klassenobjekt.</b> Der Unterschied steckt
/// nicht in der Darstellung, sondern in den Instruktionen: an jedem Bindepunkt steht ein
/// <c>structcopy</c>. Die Alternative — Struct-Felder in die Slots des Umgebenden einbetten, wie
/// C# und Rust es tun — braucht Feldzugriffe ueber Teilbereiche und damit ein anderes
/// Layout-Modell; sie ist eine spaetere, formatneutrale Optimierung (Scalar Replacement), keine
/// Voraussetzung fuer Korrektheit.</para>
/// </summary>
public sealed record IrStructType(TypeId Type) : IrType;

/// <summary>
/// Ein <b>Funktionswert</b>: das, was in <c>fn(int) -> bool</c> steht, und was eine Closure ist.
///
/// <para><b>Zur Laufzeit ein Fat Pointer</b> aus Environment-Objekt und Funktionsindex — dieselbe
/// Bauart wie <see cref="IrInterfaceType"/>, aus demselben Grund: <c>LyrValue</c> hat beide Felder
/// ohnehin, also kostet ein Funktionswert keine zusaetzliche Allokation ueber sein Environment
/// hinaus. Eine Closure ohne Captures hat gar keins und ist damit reiner Index.</para>
///
/// <para>Der Typ traegt seine Signatur <b>strukturell</b>, anders als jeder benannte Typ hier.
/// Er muss es: <c>fn(int) -> bool</c> hat keine Deklaration, an der eine Id haengen koennte, und
/// zwei gleich geformte Funktionstypen aus verschiedenen Modulen sind derselbe Typ. Terminierend
/// bleibt der Vergleich, weil Rekursion nur ueber einen benannten Typ moeglich ist.</para>
/// </summary>
public sealed record IrFunctionType(IrType[] Parameters, IrType Return) : IrType;

/// <summary>
/// Eine <b>Coroutine</b>: <c>Coroutine&lt;T&gt;</c> aus Sprache.md §8.
///
/// <para>Zur Laufzeit ein <b>Fat Pointer</b> aus Zustandsobjekt und Index der Rumpf-Funktion —
/// dieselbe Darstellung wie eine Closure (ADR-018), und <c>resume co</c> ist damit ein
/// <c>callind</c>. Slot 0 des Objekts ist der Wiedereintrittspunkt, danach kommen Parameter und
/// Locals.</para>
///
/// <para><b>Der Typ traegt die Zustands-Id NICHT.</b> Er darf es nicht: <c>let c = counter();</c>
/// hat den Typ <c>Coroutine&lt;int&gt;</c>, und dort ist nicht mehr sichtbar, welche Coroutine ihn
/// erzeugt hat — zwei Coroutinen mit gleichem Yield-Typ sind fuer die Sema derselbe Typ. Welche
/// Rumpf-Funktion laeuft, kann deshalb nur der WERT wissen, nicht sein Typ. Genau dieselbe Frage
/// beantwortet ein Interface-Wert mit seinem konkreten Typindex (P3).</para>
///
/// <para><b>Kein VM-Eingriff.</b> Sprache.md §8 erlaubt <c>yield</c> nur im Coroutine-Rumpf, nicht
/// in Funktionen, die von dort gerufen werden — genau unter dieser Bedingung reicht eine
/// Compiler-Transformation in einen Zustandsautomaten, wie C#, Kotlin und Python sie machen. Lua
/// braucht fuer sein maechtigeres Modell echte separate Stacks in der Runtime; die Sema-Regel ist
/// bereits die Entscheidung dagegen.</para>
/// </summary>
public sealed record IrCoroutineType(IrType Yield) : IrType;
}
