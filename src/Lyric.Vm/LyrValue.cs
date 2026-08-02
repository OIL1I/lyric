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
