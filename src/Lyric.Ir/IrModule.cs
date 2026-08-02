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

    public List<IrBlock> Blocks { get; init; } = Blocks;
    public BlockId Entry { get; set; }
}

/// <summary>Eine nativ hinterlegte Funktion: Signatur in Lyric deklariert, Implementierung im
/// Host. Wird beim Laden über <see cref="Name"/> gebunden (ADR-013, WASM-Modell).</summary>
public record struct IrImport(string Name, IrType[] ParamTypes, IrType ReturnType);

public class IrModule(List<IrFunction> Functions)
{
    /// <summary>Native Funktionen, die dieses Modul aufruft — nur die tatsächlich benutzten.
    /// <c>CallImport</c> referenziert sie per Index.</summary>
    public List<IrImport> Imports { get; init; } = new();

    /// <summary>Call-Ziele referenzieren per <see cref="FunctionId"/> den Index in diese Liste.
    /// Namen müssen eindeutig sein — sie werden die Symbol-Namen im Bytecode (ADR-013).</summary>
    public List<IrFunction> Functions { get; init; } = Functions;

    /// <summary>Einstiegspunkt (<c>main</c>, Sprache.md §11), oder <c>null</c> für ein reines
    /// Bibliotheks-Modul. Wandert als Start-Sektion in den Bytecode: ohne sie müsste eine Runtime
    /// den Einstieg über eine Namenskonvention raten.</summary>
    public FunctionId? EntryFunction { get; set; }
}
