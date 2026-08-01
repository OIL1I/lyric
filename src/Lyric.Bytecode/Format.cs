namespace Lyric.Bytecode;

/// <summary>
/// Die Konstanten des <c>.lyrbc</c>-Formats. Einzige Quelle für Magic, Version, Sektions-Ids,
/// Typ-Tags und Opcodes — <c>docs/Bytecode.md</c> ist gegen diese Datei geschrieben, und ein Test
/// bindet beide aneinander, damit die Spec nicht driftet.
/// </summary>
public static class Format
{
    /// <summary>"LYRB" — vier Bytes, nicht als Text interpretiert.</summary>
    public static ReadOnlySpan<byte> Magic => "LYRB"u8;

    /// <summary>Eine unbekannte Major-Version wird abgelehnt, eine unbekannte Minor toleriert
    /// (neue Sektionen sind überspringbar). Bis v1.0 darf Major frei springen — ADR-013.</summary>
    public const ushort VersionMajor = 1;
    public const ushort VersionMinor = 0;
}

/// <summary>
/// Sektions-Ids. Jede Sektion trägt ihre Byte-Länge, unbekannte Ids werden übersprungen — das ist
/// der Mechanismus, der die Source-Map strippbar macht und das Format erweiterbar hält, ohne die
/// Major-Version zu brechen. Sektionen erscheinen höchstens einmal und in aufsteigender Id-Reihenfolge
/// (Determinismus).
/// </summary>
public enum SectionId : byte
{
    Capabilities = 1,

    /// <summary>Konstantenpool. Hält <b>nur Strings</b>: Zahlen sind als LEB128-Immediate nicht
    /// größer als ein Pool-Index und sparen die Indirektion.</summary>
    Strings = 2,

    /// <summary>Reserviert für Layouts zusammengesetzter Typen (struct/class/enum). Wird in dieser
    /// Version <b>nicht</b> geschrieben — skalare Typen sind ein Byte und brauchen keine Tabelle.</summary>
    Types = 3,

    /// <summary>Host-/Native-Funktionen mit symbolischem Namen und Signatur (ADR-013, WASM-Modell).
    /// Heute immer leer: das Lowering kennt noch keine externen Calls.</summary>
    Imports = 4,

    Functions = 5,

    /// <summary>Optional und strippbar: PC → Datei/Zeile.</summary>
    SourceMap = 6,
}

/// <summary>
/// Typ-Tags, ein Byte. Werte ab 0x40 sind für zusammengesetzte Typen reserviert, damit deren
/// Einführung die bestehenden Tags nicht verschiebt.
/// </summary>
public enum TypeTag : byte
{
    I8 = 0x01, I16 = 0x02, I32 = 0x03, I64 = 0x04,
    U8 = 0x05, U16 = 0x06, U32 = 0x07, U64 = 0x08,
    F32 = 0x09, F64 = 0x0A,
    Bool = 0x0B, Char = 0x0C, String = 0x0D,
    Void = 0x0E,
}

/// <summary>
/// Opcodes, ein Byte.
///
/// <para><b>Ein Opcode pro Operation, der Typ steht als Tag-Byte dahinter</b> — nicht ein Opcode
/// pro (Operation × Typ) wie in der JVM. Bei zehn numerischen Typen wären das hundert Opcodes für
/// die Arithmetik allein; die Tabelle bliebe nicht mehr lesbar, und lesbar muss sie sein, weil
/// ADR-013 verlangt, dass jemand allein aus der Spec eine zweite Runtime schreiben kann. Der Tag
/// steht im <b>Instruktionsstrom</b>, nicht im Laufzeitwert: es bleibt statischer Dispatch, kein
/// polymorpher Opcode.</para>
///
/// <para><b>Sprungziele sind Block-Indizes</b>, nicht Byte-Offsets. Der Funktionskopf trägt die
/// Block-Offset-Tabelle, damit der Loader ein Ziel mit <c>index &lt; blockCount</c> prüfen kann —
/// ADR-013s „Validierung beim Load statt beim Call". Byte-Offsets bräuchten Fixup-Patching beim
/// Schreiben und eine Basic-Block-Rekonstruktion beim Prüfen (das CIL-Problem).</para>
/// </summary>
public enum Op : byte
{
    /// <summary><c>const &lt;type&gt; &lt;immediate&gt;</c> — Immediate je nach Typ: Ganzzahlen als
    /// uleb128 des Zweierkomplement-Bitmusters, f32/f64 als IEEE-754-Bitmuster (4/8 Byte LE),
    /// bool als ein Byte, char als uleb128-Codepoint, string als uleb128-Pool-Index.</summary>
    Const = 0x01,

    LoadLocal = 0x02,  // ldloc <uleb128 slot>
    StoreLocal = 0x03, // stloc <uleb128 slot>
    Pop = 0x04,        // verwirft den obersten Wert (verworfene Call-Rückgabe)

    Add = 0x10, Sub = 0x11, Mul = 0x12, Div = 0x13, Rem = 0x14,
    Shl = 0x15, Shr = 0x16, BitAnd = 0x17, BitOr = 0x18, BitXor = 0x19,

    /// <summary>Vergleiche: das Tag nennt den <b>Operandentyp</b>, das Ergebnis ist immer bool.</summary>
    Lt = 0x20, Le = 0x21, Gt = 0x22, Ge = 0x23, Eq = 0x24, Ne = 0x25,

    Neg = 0x30,
    /// <summary>Logisches Nicht. <b>Ohne</b> Typ-Tag — nur bool ist gültig, ein Tag wäre reine
    /// Redundanz. Die einzige Ausnahme von der Tag-Regel, hier bewusst dokumentiert.</summary>
    Not = 0x31,
    BitNot = 0x32,

    /// <summary><c>conv &lt;from&gt; &lt;to&gt;</c> — nur Numerik ↔ Numerik (Sprache.md §6.5).</summary>
    Convert = 0x33,

    /// <summary><c>call &lt;uleb128 index&gt;</c> in den gemeinsamen Indexraum: erst Imports, dann
    /// definierte Funktionen (WASM-Modell). Heute gibt es keine Imports, also ist der Index
    /// identisch zur <c>FunctionId</c> der IR.</summary>
    Call = 0x40,

    Return = 0x41,      // ret     — void
    ReturnValue = 0x42, // retval  — nimmt den obersten Wert
    Branch = 0x43,      // br <uleb128 block>
    CondBranch = 0x44,  // condbr <uleb128 ifTrue> <uleb128 ifFalse>
    Unreachable = 0x45,
}
