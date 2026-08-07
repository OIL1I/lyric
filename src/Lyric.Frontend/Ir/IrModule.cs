using Lyric.Core;
namespace Lyric.Ir;

public record struct IrLocal(LocalId Id, string Name, IrType Type);
public record struct IrTemp(TempId Id, IrType Type);

public class IrBlock(BlockId Id, List<IrOp> Insts)
{
    public BlockId Id { get; init; } = Id;
    public List<IrOp> Insts { get; init; } = Insts;

    /// <summary>Der Terminator liegt bewusst neben <see cref="Insts"/> und nicht als letztes
    /// Listenelement darin: <see cref="IrOp"/> und <see cref="IrTerminator"/> sind getrennte Typen,
    /// wodurch „Terminator mitten im Block" nicht darstellbar ist statt geprüft werden zu müssen.
    /// <c>null</c> ist nur während des Aufbaus erlaubt — ein fertiger Block hat einen Terminator
    /// (<c>IrVerifier</c> Phase 1).</summary>
    public IrTerminator? Terminator { get; set; } = null;
}

/// <summary>
/// Eine Funktion in der Mid-IR.
///
/// <para><b>Invarianten</b> (durchgesetzt von <c>IrVerifier</c>, weil sie tragend sind und man sie
/// beim Lowering leicht verletzt):</para>
/// <list type="bullet">
/// <item><b>Parameter-Konvention</b>: die ersten <see cref="ParamCount"/> Einträge in
/// <see cref="Locals"/> <b>sind</b> die Parameter, in Deklarations-Reihenfolge. Ohne diese
/// Konvention trägt die IR nirgends Parameter-Typen, und ein <c>Call</c> ist nicht typprüfbar —
/// der Verifier holt die erwarteten Argument-Typen genau dort.</item>
/// <item><b>Dichte Id-Tabellen</b>: <c>Locals[i].Id.Value == i</c>, ebenso für
/// <see cref="Temps"/> und <see cref="Blocks"/>. Die Id ist der Slot- bzw. Sprung-Index im
/// späteren Bytecode; eine Lücke oder Permutation äußert sich als Falsch-Slot-Read in der VM.</item>
/// <item><see cref="Entry"/> ist <c>Blocks[0].Id</c> und hat keine Prädecessoren — er ist der
/// einzige Ort für Parameter-Setup, ein Rücksprung dorthin würde es wiederholen.</item>
/// <item>Jedes Temp wird <b>genau einmal</b> definiert (SSA-light). Darauf beruht, dass
/// „auf jedem Pfad verfügbar" gleichbedeutend mit „die Definition dominiert den Use" ist.</item>
/// </list>
/// </summary>
/// <summary>Was eine geschuetzte Region tut, wenn es in ihr knallt.</summary>
public enum IrHandlerKind
{
    /// <summary>Faengt einen Typ (oder alles, wenn <c>CatchType</c> fehlt) und fuehrt weiter.</summary>
    Catch,

    /// <summary>Laeuft beim Abwickeln und gibt danach ab — der Traeger von <c>defer</c>.</summary>
    Finally,
}

/// <summary>
/// Eine geschuetzte Region: die Bloecke <c>[Start, End)</c> sind abgedeckt.
///
/// <para><b>Block-Indizes statt Byte-Bereichen</b>, dieselbe Entscheidung wie bei den Sprungzielen
/// (P5/ADR-013): ein Bereich prueft man mit zwei Vergleichen gegen die Blockzahl, statt
/// Byte-Offsets gegen Instruktionsgrenzen zu verifizieren.</para>
///
/// <para><b>Der gefangene Wert landet in <see cref="Slot"/>, nicht auf dem Stack.</b> CIL schiebt
/// ihn beim Betreten des Handlers auf den Operanden-Stack — das ginge hier nicht, weil der Stack an
/// jeder Blockgrenze leer ist (Bytecode.md §4). Ueber einen Slot bleibt die Invariante intakt und
/// der Handler-Block faengt an wie jeder andere.</para>
/// </summary>
/// <param name="CatchType">Der gefangene Typ, oder <c>null</c> fuer catch-all. Bei
/// <see cref="IrHandlerKind.Finally"/> immer <c>null</c>.</param>
/// <param name="Slot">Wohin der gefangene Wert geht. Bei <c>finally</c> und bei <c>catch (_)</c>
/// ohne Bindung <c>null</c>.</param>
public record struct IrHandler(BlockId Start, BlockId End, IrHandlerKind Kind,
    TypeId? CatchType, BlockId Handler, LocalId? Slot);

public class IrFunction(string Name, IrType ReturnType, int ParamCount, List<IrLocal> Locals, List<IrTemp> Temps, List<IrBlock> Blocks)
{
    public string Name { get; init; } = Name;
    public IrType ReturnType { get; init; } = ReturnType;

    /// <summary>Anzahl der Parameter. Die ersten <c>ParamCount</c> Einträge in
    /// <see cref="Locals"/> sind diese Parameter, in Reihenfolge — siehe Klassen-Doku.</summary>
    public int ParamCount { get; init; } = ParamCount;

    /// <summary>Benannte Slots der Funktion: zuerst die <see cref="ParamCount"/> Parameter, dann
    /// die lokalen Bindings. Dicht indiziert über <see cref="LocalId"/>.</summary>
    public List<IrLocal> Locals { get; init; } = Locals;

    /// <summary>Autorität für den Typ jedes Temps. Die <c>Type</c>-Felder auf den Instruktionen
    /// sind Kopien für den Printer; weichen sie hiervon ab, ist das ein Bug.</summary>
    public List<IrTemp> Temps { get; init; } = Temps;

    /// <summary>
    /// Die geschuetzten Regionen dieser Funktion, <b>innerste zuerst</b>.
    ///
    /// <para>Die Reihenfolge ist der Vertrag: beim Abwickeln nimmt die Runtime den ersten Eintrag,
    /// dessen Bereich den Fehlerort deckt und dessen Typ passt. Bei geschachtelten try-Bloecken
    /// entscheidet damit die Liste, nicht eine Bereichsgroessen-Rechnung.</para>
    /// </summary>
    public List<IrHandler> Handlers { get; init; } = new();

    public List<IrBlock> Blocks { get; init; } = Blocks;
    public BlockId Entry { get; set; }
}

/// <summary>Eine nativ hinterlegte Funktion: Signatur in Lyric deklariert, Implementierung im
/// Host. Wird beim Laden über <see cref="Name"/> gebunden (ADR-013, WASM-Modell).</summary>
public record struct IrImport(string Name, IrType[] ParamTypes, IrType ReturnType);

/// <summary>
/// Das Layout eines zusammengesetzten Typs. <see cref="FieldNames"/> ist reine Diagnose — im
/// Bytecode landen nur die Typen, und der Feldindex <b>ist</b> die Position in
/// <see cref="FieldTypes"/>.
///
/// <para>Beide Listen sind gleich lang; der Verifier setzt das durch, weil ein Auseinanderlaufen
/// sonst erst im Printer als Index-Ausnahme auffiele.</para>
/// </summary>
public record struct IrTypeDef(string Name, IrType[] FieldTypes, string[] FieldNames)
{
    /// <summary>
    /// Die Varianten, wenn dieser Eintrag ein <b>Enum</b> ist — sonst leer. Jede Variante ist
    /// selbst ein Eintrag mit eigenem Layout (Bytecode.md §2); der Enum nennt nur ihre Ids.
    ///
    /// <para><b>Slot 0 jeder Variante ist ihr Tag</b>, der Index in dieser Liste. Die Nutzfelder
    /// beginnen bei Slot 1.</para>
    /// </summary>
    public TypeId[] Variants { get; init; } = [];

    /// <summary>
    /// Die Methoden-Slots, wenn dieser Eintrag ein <b>Interface</b> ist — sonst leer. Der
    /// <b>Index</b> in dieser Liste ist der Slot, auf den <c>CallVirt</c> zeigt; die Namen stehen
    /// nur fuer Disassembler und Diagnose darin.
    ///
    /// <para>Die Reihenfolge kommt aus der Deklaration, nicht aus einer Symboltabelle — genau wie
    /// bei den Feldern einer Klasse, und aus demselben Grund: der Slot ist ein Vertrag, die
    /// Aufzaehlungsreihenfolge einer Map ist ein Implementierungsdetail.</para>
    /// </summary>
    public string[] MethodSlots { get; init; } = [];

    /// <summary>Wert-Semantik? Ein <c>struct</c> hat dasselbe Feld-Layout wie eine Klasse; der
    /// Unterschied ist, dass jede Bindung kopiert. Ein eigenes Flag statt eines eigenen
    /// Eintragstyps, weil sich am Layout selbst nichts aendert.</summary>
    public bool IsStruct { get; init; }

    public bool IsEnum => Variants.Length > 0;

    public bool IsInterface => MethodSlots.Length > 0;
}

/// <summary>
/// Eine vtable-Zeile: Klasse <paramref name="Type"/> erfuellt Interface
/// <paramref name="Interface"/>, und zwar Slot fuer Slot mit <paramref name="Methods"/>.
///
/// <para>Ein Eintrag je (Klasse, Interface)-Paar. <c>Methods</c> ist so lang wie die Slot-Liste des
/// Interfaces; ein Default-Methoden-Slot traegt die Funktion des Interfaces selbst, ein
/// ueberschriebener die der Klasse — die Aufloesungsreihenfolge (eigenes Member vor Default) faellt
/// im Lowering, nicht zur Laufzeit.</para>
/// </summary>
public record struct IrImpl(TypeId Type, TypeId Interface, FunctionId[] Methods);

/// <summary>Ein globaler Slot. <paramref name="Name"/> ist reine Diagnose — im Bytecode steht nur
/// der Typ, und der Index ist die Identitaet.</summary>
public record struct IrGlobal(string Name, IrType Type);

public class IrModule(List<IrFunction> Functions)
{
    /// <summary>Native Funktionen, die dieses Modul aufruft — nur die tatsächlich benutzten.
    /// <c>CallImport</c> referenziert sie per Index.</summary>
    public List<IrImport> Imports { get; init; } = new();

    /// <summary>Welche Capabilities dieses Modul <b>verlangt</b> (ADR-007). Was gewaehrt wird,
    /// entscheidet die Runtime beim Laden — der Compiler schreibt nur den Bedarf hinein.
    ///
    /// <para>Er steht IM Modul und nicht neben ihm, weil ein '.lyrbc' von woanders kommen kann:
    /// ein Host, der fremden Bytecode laedt, muss ohne den Compiler wissen, was das Programm
    /// anfassen will (ADR-013).</para></summary>
    public Capability Capabilities { get; init; } = Capability.None;

    /// <summary>Layouts der zusammengesetzten Typen. <see cref="IrRefType"/>, <c>NewObject</c>,
    /// <c>LoadField</c> und <c>StoreField</c> referenzieren sie per <see cref="TypeId"/>; dicht
    /// indiziert wie alle Tabellen hier.</summary>
    public List<IrTypeDef> Types { get; init; } = new();

    /// <summary>
    /// Globale Slots: Modul-<c>let</c> und <c>static let</c>. Der Name ist Diagnose, der
    /// <b>Index</b> ist der Vertrag — wie ueberall hier.
    /// </summary>
    public List<IrGlobal> Globals { get; init; } = new();

    /// <summary>
    /// Die Funktion, die alle Globals fuellt, oder <c>null</c>, wenn es keine gibt.
    ///
    /// <para>Eine Runtime ruft sie <b>vor</b> dem Einstiegspunkt. Die Reihenfolge darin ist
    /// Deklarationsreihenfolge — ein Global darf ein frueheres benutzen, ein spaeteres nicht.</para>
    /// </summary>
    public FunctionId? GlobalInit { get; set; }

    /// <summary>Die vtable-Zeilen. Landen als Impls-Sektion im Bytecode; die Runtime baut daraus
    /// beim Laden ihre Dispatch-Tabelle, damit <c>callvirt</c> ein Nachschlagen und kein Suchen
    /// ist.</summary>
    public List<IrImpl> Impls { get; init; } = new();

    /// <summary>Call-Ziele referenzieren per <see cref="FunctionId"/> den Index in diese Liste.
    /// Namen müssen eindeutig sein — sie werden die Symbol-Namen im Bytecode (ADR-013).</summary>
    public List<IrFunction> Functions { get; init; } = Functions;

    /// <summary>Einstiegspunkt (<c>main</c>, Sprache.md §11), oder <c>null</c> für ein reines
    /// Bibliotheks-Modul. Wandert als Start-Sektion in den Bytecode: ohne sie müsste eine Runtime
    /// den Einstieg über eine Namenskonvention raten.</summary>
    public FunctionId? EntryFunction { get; set; }
}
