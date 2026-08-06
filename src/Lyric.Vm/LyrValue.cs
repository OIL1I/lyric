using Lyric.Bytecode;

namespace Lyric.Vm;

/// <summary>
/// Ein Laufzeitwert.
///
/// <para><b>Ohne Typ-Tag</b> — und das ist kein Versehen, sondern die Auszahlung einer
/// Format-Entscheidung: jeder Opcode trägt sein Typ-Tag im Instruktionsstrom (P5), also weiß der
/// Interpreter an jeder Stelle statisch, was auf dem Stack liegt. Ein Tag im Wert wäre eine zweite,
/// redundante Wahrheitsquelle und würde jede Operation um eine Prüfung verteuern.</para>
///
/// <para>Zahlen liegen in <see cref="Bits"/>, nicht im Heap — bei Spielelogik ist das der
/// Unterschied zwischen „gut genug" und GC-Druck bei jedem <c>+</c>. Nur Strings brauchen eine
/// Referenz; um deren Lebenszeit kümmert sich der .NET-GC (ADR-002).</para>
///
/// <para><b>Kodierung der Ganzzahlen</b>: immer auf 64 Bit erweitert — vorzeichenbehaftete Typen
/// vorzeichenerweitert, vorzeichenlose nullerweitert. Dadurch funktionieren Vergleiche und
/// Division direkt auf <c>long</c>/<c>ulong</c>, ohne die Breite zu kennen. Nach jeder Rechnung
/// stellt <see cref="Normalize"/> die Invariante wieder her.</para>
/// </summary>
public readonly struct LyrValue
{
    public readonly ulong Bits;
    public readonly object? Ref;

    private LyrValue(ulong bits, object? reference)
    {
        Bits = bits;
        Ref = reference;
    }

    public static LyrValue FromBits(ulong bits) => new(bits, null);
    public static LyrValue FromI64(long value) => new((ulong)value, null);
    public static LyrValue FromBool(bool value) => new(value ? 1UL : 0UL, null);
    public static LyrValue FromF64(double value) => new(BitConverter.DoubleToUInt64Bits(value), null);
    public static LyrValue FromF32(float value) => new(BitConverter.SingleToUInt32Bits(value), null);
    public static LyrValue FromString(string value) => new(0, value);

    /// <summary>Eine Objekt-Referenz: ein Slot je Feld. Kein Typ-Tag im Wert — der
    /// Instruktionsstrom trägt es, und der Loader hat Typ- und Feldindex geprüft. Um die
    /// Lebenszeit kümmert sich der .NET-GC (ADR-002).</summary>
    public static LyrValue FromObject(LyrValue[] fields) => new(0, fields);

    /// <summary>
    /// Marker für „dieses Optional hat einen Wert", wenn der Wert selbst keine Referenz ist.
    ///
    /// <para>Ein Objekt statt eines Bitmusters, weil es bei <c>?int</c> kein freies gibt: jedes
    /// <c>i64</c> ist eine gültige Zahl. Ein reserviertes Muster hieße, dass ein bestimmter Wert
    /// je nach Runtime mal ein Wert und mal keiner wäre — Bytecode.md §5 verbietet das
    /// ausdrücklich. Global geteilt, also kostet „some" keine Allokation.</para>
    /// </summary>
    private static readonly object SomeMarker = new();

    /// <summary>
    /// Ein Wert, der über ein Interface angesprochen wird: ein <b>Fat Pointer</b> aus dem Objekt
    /// (<see cref="Ref"/>) und dem Index seines konkreten Typs (<see cref="Bits"/>).
    ///
    /// <para>Das ist die Antwort auf ein Problem, das M6 und P1 geschaffen haben: ein Objekt trägt
    /// <b>kein</b> Typ-Tag, also kann ein <c>callvirt</c> die konkrete Klasse nicht aus dem Objekt
    /// zurückgewinnen. Sie an den Wert zu heften kostet nichts — <c>Bits</c> ist bei einer
    /// Referenz ohnehin ungenutzt —, während ein Tag in Slot 0 jeden Feldindex verschoben und
    /// jedes Objekt ein Wort gekostet hätte, auch die Mehrzahl ohne Interface. Rust macht es mit
    /// <c>dyn Trait</c> genauso.</para>
    ///
    /// <para>Ein <c>?SomeInterface</c> ist damit ebenfalls unproblematisch: der Fat Pointer trägt
    /// eine echte Referenz, also reicht sie wie bei jedem anderen Referenztyp als
    /// Anwesenheits-Marker.</para>
    /// </summary>
    public static LyrValue FromInterface(LyrValue instance, int concreteType) =>
        new((ulong)(uint)concreteType, instance.Ref);

    /// <summary>Der konkrete Typindex eines Interface-Wertes — das, worüber <c>callvirt</c>
    /// nachschlägt.</summary>
    public int ConcreteType => (int)(uint)Bits;

    /// <summary>
    /// Ein <b>Closure-Wert</b> (ADR-018): Environment plus Index der gehobenen Funktion.
    ///
    /// <para>Dieselbe Bauart wie <see cref="FromInterface"/>, und aus demselben Grund: die
    /// Referenz nimmt das Environment auf, <c>Bits</c> ist daneben frei. Eine Closure ohne
    /// Captures traegt damit gar keine Referenz und kostet keine Allokation — der haeufige Fall
    /// bei einem Filter wie <c>(x) =&gt; x &gt; 0</c>.</para>
    ///
    /// <para>Der Index wird um eins erhoeht abgelegt. Sonst waere eine Closure auf Funktion 0
    /// ohne Environment bitgleich mit <see cref="None"/>, und ein <c>?fn(…)</c> haette „kein
    /// Wert" nicht mehr von „die erste Funktion" unterscheiden koennen.</para>
    /// </summary>
    public static LyrValue FromClosure(LyrValue environment, int function) =>
        new((ulong)(uint)(function + 1), environment.Ref);

    /// <summary>Der Funktionsindex eines Closure-Wertes.</summary>
    public int ClosureFunction => (int)(uint)Bits - 1;

    /// <summary>Traegt diese Closure ein Environment? Ohne Captures gibt es keins.</summary>
    public bool HasEnvironment => Ref is not null;

    /// <summary>„Kein Wert" ist eine leere Referenz — einheitlich für alle <c>?T</c>.</summary>
    public static LyrValue None => default;

    /// <summary>Verpackt einen Wert. Ist er selbst eine Referenz, trägt sie sich selbst; sonst
    /// markiert <see cref="SomeMarker"/> die Anwesenheit und die Zahl bleibt in <see cref="Bits"/>.</summary>
    public static LyrValue Some(LyrValue value) =>
        value.Ref is not null ? value : new(value.Bits, SomeMarker);

    public bool IsSome => Ref is not null;

    /// <summary>Packt aus. Das Gegenstück zu <see cref="Some"/>: der Marker verschwindet, eine
    /// echte Referenz bleibt stehen.</summary>
    public LyrValue Unwrap() => ReferenceEquals(Ref, SomeMarker) ? FromBits(Bits) : this;

    public long AsI64 => (long)Bits;
    public ulong AsU64 => Bits;
    public bool AsBool => Bits != 0;
    public double AsF64 => BitConverter.UInt64BitsToDouble(Bits);
    public float AsF32 => BitConverter.UInt32BitsToSingle((uint)Bits);
    public string AsString => (string)(Ref ?? string.Empty);

    /// <summary>Die Feld-Slots einer Instanz. Wirft bei einer Null-Referenz — die kann in Format
    /// 1.2 nicht entstehen, weil das Lowering <c>newobj</c> und Feld-Initialisierung immer zusammen
    /// erzeugt und Optionals noch nicht gelowert werden. Sobald <c>?T</c> dazukommt, wird daraus
    /// eine echte Diagnose statt eines Wurfs.</summary>
    public LyrValue[] AsObject => (LyrValue[])(Ref
        ?? throw new InvalidOperationException("null object reference"));

    /// <summary>Stellt die Breiten-Invariante her: auf die Breite des Typs abschneiden, dann je
    /// nach Vorzeichen wieder auf 64 Bit erweitern. Ohne diesen Schritt würde <c>add i8</c> mit
    /// 200 + 100 nicht überlaufen, sondern 300 liefern.</summary>
    public static ulong Normalize(TypeTag tag, ulong bits) => tag switch
    {
        TypeTag.I8 => (ulong)(long)(sbyte)bits,
        TypeTag.I16 => (ulong)(long)(short)bits,
        TypeTag.I32 => (ulong)(long)(int)bits,
        TypeTag.I64 => bits,
        TypeTag.U8 => (byte)bits,
        TypeTag.U16 => (ushort)bits,
        TypeTag.U32 => (uint)bits,
        TypeTag.U64 => bits,
        TypeTag.Bool => bits != 0 ? 1UL : 0UL,
        TypeTag.Char => bits,
        _ => bits,
    };

    public static bool IsSigned(TypeTag tag) =>
        tag is TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64;

    public static bool IsInteger(TypeTag tag) =>
        tag is TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64
            or TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64;

    public static bool IsFloat(TypeTag tag) => tag is TypeTag.F32 or TypeTag.F64;
}
