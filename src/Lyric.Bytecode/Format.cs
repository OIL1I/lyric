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
    /// <remarks>Auf 2.0 gehoben, weil die Types-Sektion für Enums ihre <b>Form</b> ändert (ein
    /// Eintrag trägt jetzt ein Kind-Byte). §2 erlaubt einer neuen Minor nur überspringbare
    /// Ergänzungen — eine geänderte Sektions-Form ist keine. ADR-013 deckt den Bruch vor v1.0
    /// ausdrücklich.</remarks>
    public const ushort VersionMajor = 2;
    public const ushort VersionMinor = 3;
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

    /// <summary>Layouts zusammengesetzter Typen: Name, Feldzahl, Feldtypen. Der Feldindex <b>ist</b>
    /// die Position in der Feldliste; Feldnamen stehen nicht im Bytecode. Über den Index sind auch
    /// rekursive Typen (<c>class Node { next: Node }</c>) kodierbar, die strukturell nicht endlich
    /// wären.</summary>
    Types = 3,

    /// <summary>Host-/Native-Funktionen mit symbolischem Namen und Signatur (ADR-013, WASM-Modell).</summary>
    Imports = 4,

    Functions = 5,

    /// <summary>Optional und strippbar: PC → Datei/Zeile.</summary>
    SourceMap = 6,

    /// <summary>Einstiegspunkt: <c>uleb128</c>-Index der Funktion, die eine Runtime aufruft
    /// (WASM-Modell). Fehlt bei Bibliotheks-Modulen. Ohne diese Sektion müsste eine Runtime den
    /// Einstieg über eine Namenskonvention raten — was ADR-013s Ziel widerspricht, dass die Spec
    /// allein zum Implementieren reicht.</summary>
    Start = 7,

    /// <summary>
    /// Interface-Implementierungen: welche Funktion erfuellt welchen Methoden-Slot welches
    /// Interfaces fuer welche Klasse. Die vtable-Zeilen, aus denen <c>callvirt</c> sein Ziel holt.
    ///
    /// <para>Eigene Sektion und <b>nicht</b> ein Feld im Klassen-Eintrag: §2 erlaubt einer neuen
    /// Minor nur ueberspringbare Ergaenzungen. Ein zusaetzliches Feld im Layout-Eintrag waere eine
    /// Formaenderung wie bei Enums (die 2.0 erzwang); eine neue Sektions-Id ist genau die
    /// Erweiterung, fuer die der Mechanismus da ist.</para>
    /// </summary>
    Impls = 8,

    /// <summary>
    /// Geschuetzte Regionen je Funktion: welcher Blockbereich von welchem Handler abgedeckt ist.
    ///
    /// <para>Eigene Sektion und <b>kein</b> Feld im Funktions-Eintrag: §2 erlaubt einer neuen Minor
    /// nur ueberspringbare Ergaenzungen, und ein zusaetzliches Feld im Funktionskopf waere eine
    /// Formaenderung. Dieselbe Ueberlegung wie bei Impls.</para>
    /// </summary>
    Handlers = 9,
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

    /// <summary>Referenz auf einen Typ der Types-Sektion; ein <c>uleb128</c>-Index folgt.
    /// Zuweisung kopiert den Verweis, nicht das Objekt. Wert-Semantik (<c>struct</c>) bekommt ein
    /// eigenes Tag — am Bytecode muss ablesbar bleiben, ob eine Zuweisung kopiert.</summary>
    Ref = 0x40,

    /// <summary>Array; der Elementtyp folgt <b>inline</b> als weiterer Typ, nicht als
    /// Tabellen-Index. Möglich, weil ein Array-Typ nicht rekursiv sein kann — <c>int[][]</c> ist
    /// endlich tief, <c>class Node { next: Node }</c> nicht.</summary>
    Array = 0x41,

    /// <summary>Optional (<c>?T</c>); der innere Typ folgt inline. <b>Nicht schachtelbar</b> —
    /// <c>??T</c> gibt es nicht, sonst wäre „kein Wert" mehrdeutig.</summary>
    Optional = 0x42,

    /// <summary>Enum; ein <c>uleb128</c>-Index auf einen Enum-Eintrag der Types-Sektion folgt.
    /// Anders als Array und Optional über einen Index, weil ein Enum wie eine Klasse eine
    /// Deklaration hat und rekursiv sein darf.</summary>
    Enum = 0x43,

    /// <summary>
    /// Interface-Typ (<c>dyn</c>); ein <c>uleb128</c>-Index auf einen Interface-Eintrag folgt.
    ///
    /// <para>Ein Wert dieses Typs ist ein <b>Fat Pointer</b>: Objekt plus konkreter Typindex.
    /// Siehe §4 „Darstellung eines Interface-Wertes".</para>
    /// </summary>
    Interface = 0x44,

    /// <summary>
    /// <c>struct</c> — <b>Wert-Semantik</b>; ein <c>uleb128</c>-Index auf einen Struct-Eintrag der
    /// Types-Sektion folgt.
    ///
    /// <para>Eigenes Tag neben <see cref="Ref"/>, weil am Bytecode ablesbar bleiben muss, ob eine
    /// Zuweisung kopiert. Das war schon bei der Einfuehrung von <c>0x40</c> so vorgesehen.</para>
    /// </summary>
    Struct = 0x45,
}

/// <summary>Art eines Types-Eintrags. Varianten eines Enums sind selbst
/// <see cref="Layout"/>-Einträge — der Enum nennt nur ihre Indizes.</summary>
public enum TypeKind : byte
{
    Layout = 0,
    Enum = 1,

    /// <summary>Ein Interface. Traegt keine Felder, sondern die Namen seiner Methoden-Slots — der
    /// <b>Index</b> in dieser Liste ist der Slot, auf den <c>callvirt</c> zeigt. Die Namen stehen
    /// nur fuer Disassembler und Diagnose darin.</summary>
    Interface = 2,

    /// <summary>
    /// Ein <c>struct</c>: dasselbe Feld-Layout wie <see cref="Layout"/>, aber <b>Wert-Semantik</b>.
    ///
    /// <para>Eigener Kind-Wert und nicht bloss ein anderes Typ-Tag an der Verwendungsstelle: der
    /// Loader muss <c>structcopy</c> gegen den Eintrag pruefen koennen, und „ist dieser Typ ein
    /// Wert-Typ" ist eine Eigenschaft der Deklaration, nicht der Verwendung.</para>
    /// </summary>
    Struct = 3,
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

    /// <summary><c>newobj &lt;uleb128 type&gt;</c> — legt eine Instanz an, Felder auf ihren Nullwert.</summary>
    NewObject = 0x50,

    /// <summary><c>ldfld &lt;uleb128 type&gt; &lt;uleb128 field&gt;</c> — ersetzt die Referenz durch
    /// den Feldwert.</summary>
    LoadField = 0x51,

    /// <summary><c>stfld &lt;uleb128 type&gt; &lt;uleb128 field&gt;</c> — nimmt Referenz und Wert,
    /// die <b>Referenz liegt unter dem Wert</b> (CIL-Reihenfolge, Bytecode.md §5).
    ///
    /// <para>Der Typ-Index ist zur Laufzeit redundant und steht trotzdem da: nur so prüft der
    /// Loader den Feldindex gegen ein Layout, ohne eine Datenfluss-Analyse zu fahren.</para></summary>
    StoreField = 0x52,

    /// <summary><c>newarr &lt;elementType&gt; &lt;uleb128 count&gt;</c> — nimmt <c>count</c> Werte
    /// vom Stack, das erste Element zuunterst. Ein Literal ist damit eine Instruktion, nicht
    /// <c>count</c> Stores.</summary>
    NewArray = 0x58,

    LoadElem = 0x59,  // ldelem  — Array, Index -> Element
    StoreElem = 0x5A, // stelem  — Array, Index, Wert (Referenz zuunterst)
    ArrayLen = 0x5B,  // arrlen  — Laenge als i64

    /// <summary><c>arrcat</c> / <c>arrrep</c> bilden <c>xs + ys</c> und <c>xs * n</c> ab
    /// (Sprache.md §6.5) — eingebaute Sprachsemantik. Beide liefern ein <b>neues</b> Array:
    /// <c>T[]</c> wächst nicht (ADR-016).</summary>
    ArrayConcat = 0x5C,
    ArrayRepeat = 0x5D,

    /// <summary>
    /// Optionals (Sprache.md §7). <c>??</c>, <c>??=</c> und <c>?.</c> haben <b>keine</b> eigenen
    /// Opcodes: sie werten ihre rechte Seite nur bedingt aus und lowern deshalb zu Verzweigungen
    /// über <see cref="OptIsSome"/> — wie <c>&amp;&amp;</c> und <c>||</c>. Ein Opcode müsste einen
    /// unausgewerteten Ausdruck transportieren, und das kann eine Stack-Maschine nicht.
    /// </summary>
    OptNone = 0x60,   // optnone <innerType>
    OptSome = 0x61,   // optsome <innerType>
    OptIsSome = 0x62, // optissome
    OptGet = 0x63,    // optget — Force-Unwrap 'expr!', panickt bei "kein Wert"

    /// <summary>
    /// Enums (Sprache.md §3.4). <c>match</c> hat <b>keinen</b> Opcode: es liest mit
    /// <see cref="EnumTag"/> das Tag und verzweigt darüber wie jede andere Fallunterscheidung.
    /// Eine Sprungtabelle wäre eine Optimierung, keine Semantik.
    ///
    /// <para>Dieselbe Form wie beim Optional — <c>optissome</c> prüft, <c>optget</c> löst ein;
    /// hier prüft <c>enumtag</c> und <c>enumas</c> löst ein.</para>
    /// </summary>
    NewVariant = 0x68, // newvariant <uleb128 variantType>
    EnumTag = 0x69,    // enumtag
    EnumAs = 0x6A,     // enumas <uleb128 variantType> — panickt bei falschem Tag

    // --- Interfaces (Format 2.1) -------------------------------------------------------------

    /// <summary><c>mkiface &lt;uleb128 concreteType&gt; &lt;uleb128 interfaceType&gt;</c> — hebt
    /// eine Objektreferenz auf ihren Interface-Typ. Der konkrete Typ steht zur Compile-Zeit fest;
    /// die Instruktion heftet ihn an den Wert, damit <c>callvirt</c> ihn spaeter findet.
    ///
    /// <para>Beide Indizes stehen dran, obwohl die Runtime nur den ersten braucht: so prueft der
    /// Loader die Implementierungs-Beziehung gegen die Impls-Sektion, ohne eine Datenflussanalyse
    /// zu fahren — ADR-013s „Validierung beim Load statt beim Call", dieselbe Begruendung wie beim
    /// Typ- und Feldindex am <c>ldfld</c>.</para></summary>
    MakeInterface = 0x70,

    /// <summary><c>callvirt &lt;uleb128 interfaceType&gt; &lt;uleb128 slot&gt;</c> — ruft die
    /// Implementierung des Slots am konkreten Typ des Empfaengers. Der Empfaenger liegt zuunterst
    /// wie bei jedem Methodenaufruf (Parameter 0, ADR-014).</summary>
    CallVirt = 0x71,

    // --- Structs (Format 2.2) ----------------------------------------------------------------

    /// <summary><c>structcopy &lt;uleb128 structType&gt;</c> — nimmt einen Struct-Wert und legt
    /// eine <b>unabhaengige Kopie</b> davon ab.
    ///
    /// <para>Die Kopie ist rekursiv ueber verschachtelte Structs und flach ueber alles andere: ein
    /// Feld vom Typ <c>class</c> oder <c>T[]</c> traegt eine Referenz, und die wird geteilt, nicht
    /// dupliziert (Sprache.md §3.2 — kopiert wird der Wert, nicht die Welt dahinter).</para>
    ///
    /// <para><b>Warum eine eigene Instruktion</b> und nicht ein implizites Kopieren im
    /// <c>stloc</c>: sonst haenge die Bedeutung von <c>stloc</c> am Typ seines Ziel-Slots, und der
    /// Opcode waere polymorph. Explizit ist es in der Disassembly sichtbar und beim Lesen des
    /// Formats eindeutig — dieselbe Entscheidung wie bei <c>mkiface</c>.</para></summary>
    StructCopy = 0x72,

    // --- Exceptions (Format 2.3) -------------------------------------------------------------

    /// <summary><c>throw</c> — nimmt den Wert vom Stack und beginnt das Abwickeln. Terminator:
    /// nach ihm laeuft im Block nichts mehr.</summary>
    Throw = 0x73,

    /// <summary><c>endfinally</c> — Ende einer <c>finally</c>-Region; die Abwicklung geht dort
    /// weiter, wo sie unterbrochen wurde. Terminator.
    ///
    /// <para>Lyric selbst hat kein <c>finally</c> (ADR-009); diese Region entsteht ausschliesslich
    /// aus <c>defer</c>. Das Format braucht den Traeger trotzdem, weil „laeuft auch beim
    /// Abwickeln" anders nicht ausdrueckbar ist.</para></summary>
    EndFinally = 0x74,
}
