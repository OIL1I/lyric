namespace Lyric.Bytecode;

/// <summary>
/// Ein gelesenes <c>.lyrbc</c>-Modul.
///
/// <para>Bewusst <b>kein</b> <c>IrModule</c>: der Weg dorthin zurück existiert nicht. Der Bytecode
/// ist stack-basiert, die IR temp-basiert — beim Emittieren verschwinden die Temps in Stack-Slots
/// und Local-Slots, und diese Information ist nicht rekonstruierbar. Der Round-Trip-Test vergleicht
/// deshalb Bytes gegen Bytes, nicht IR gegen IR.</para>
///
/// <para>Diese Struktur ist zugleich das, was der Loader der VM in M6 braucht — der Disassembler
/// ist nur eine Textausgabe darüber.</para>
/// </summary>
public sealed class BytecodeModule
{
    public required ushort VersionMajor { get; init; }
    public required ushort VersionMinor { get; init; }
    public required ulong Capabilities { get; init; }
    public required IReadOnlyList<string> Strings { get; init; }
    public required IReadOnlyList<BytecodeImport> Imports { get; init; }
    public required IReadOnlyList<BytecodeFunction> Functions { get; init; }

    /// <summary>Index der Einstiegsfunktion im gemeinsamen Indexraum (erst Imports, dann
    /// Funktionen), oder <c>null</c> bei einem Bibliotheks-Modul. Aus der Start-Sektion.</summary>
    public int? Start { get; init; }
}

/// <summary>Host-/Native-Funktion, per Index aus <c>call</c> referenziert (ADR-013, WASM-Modell).
/// Heute immer leer — das Lowering kennt noch keine externen Calls.</summary>
public sealed class BytecodeImport
{
    public required string Name { get; init; }
    public required IReadOnlyList<TypeTag> ParamTypes { get; init; }
    public required TypeTag ReturnType { get; init; }
}

public sealed class BytecodeFunction
{
    public required string Name { get; init; }
    public required int ParamCount { get; init; }
    public required TypeTag ReturnType { get; init; }

    /// <summary>Typ jedes Local-Slots. Die ersten <see cref="ParamCount"/> sind die Parameter —
    /// dieselbe Konvention wie in der IR.</summary>
    public required IReadOnlyList<TypeTag> SlotTypes { get; init; }

    /// <summary>Maximale Tiefe des Operanden-Stacks. Der Emitter rechnet sie aus, damit der Loader
    /// die Frame-Größe kennt, ohne selbst analysieren zu müssen.</summary>
    public required int MaxStack { get; init; }

    /// <summary>Byte-Offset jedes Blocks in <see cref="Code"/>. Sprünge nennen Block-<i>Indizes</i>;
    /// diese Tabelle löst sie auf. Damit prüft der Loader ein Sprungziel mit
    /// <c>index &lt; Count</c> statt einen Byte-Offset gegen Instruktionsgrenzen verifizieren zu müssen.</summary>
    public required IReadOnlyList<int> BlockOffsets { get; init; }

    public required byte[] Code { get; init; }
}

/// <summary>Eine dekodierte Instruktion. Flach statt als Typhierarchie: es gibt nur eine Handvoll
/// Operandenformen, und Decoder wie Disassembler wollen sie ohne Casts lesen.</summary>
public sealed record BytecodeInstruction
{
    public required int Offset { get; init; }
    public required Op Opcode { get; init; }

    /// <summary>Typ-Tag der Operation; bei <c>convert</c> der Quelltyp.</summary>
    public TypeTag? Type { get; init; }
    /// <summary>Nur <c>convert</c>: der Zieltyp.</summary>
    public TypeTag? ToType { get; init; }

    /// <summary>Slot-Index, Funktions-Index, Block-Index, Ganzzahl-Bitmuster, Codepoint oder
    /// String-Pool-Index — je nach Opcode.</summary>
    public ulong Immediate { get; init; }
    /// <summary>Nur <c>condbr</c>: der false-Zweig.</summary>
    public ulong Immediate2 { get; init; }

    public double FloatValue { get; init; }
    public bool BoolValue { get; init; }
}
