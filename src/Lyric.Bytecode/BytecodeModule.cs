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
    public required IReadOnlyList<BytecodeTypeDef> Types { get; init; }
    public required IReadOnlyList<BytecodeImport> Imports { get; init; }
    public required IReadOnlyList<BytecodeFunction> Functions { get; init; }

    /// <summary>Index der Einstiegsfunktion im gemeinsamen Indexraum (erst Imports, dann
    /// Funktionen), oder <c>null</c> bei einem Bibliotheks-Modul. Aus der Start-Sektion.</summary>
    public int? Start { get; init; }
}

/// <summary>
/// Ein Typ an einer Signaturstelle: das Tag und, bei einer Referenz, der Index in die
/// Typ-Tabelle.
///
/// <para>Eigener Typ statt eines nackten <see cref="TypeTag"/>, weil ein Tag seit Format 1.2 nicht
/// mehr für sich steht — <c>0x40</c> ohne seinen Index ist keine vollständige Typangabe. Ein Feld,
/// das man zu lesen vergisst, wäre ein um ein Byte verschobener Strom.</para>
/// </summary>
/// <remarks>Ein <c>record class</c>, kein <c>struct</c>: <see cref="Element"/> ist wieder ein
/// <see cref="BytecodeType"/>, und ein Struct darf sich nicht selbst enthalten.</remarks>
public sealed record BytecodeType(TypeTag Tag, int TypeIndex)
{
    public static BytecodeType Scalar(TypeTag tag) => new(tag, -1);
    public bool IsRef => Tag == TypeTag.Ref;
    public bool IsArray => Tag == TypeTag.Array;
    public bool IsOptional => Tag == TypeTag.Optional;

    /// <summary>Innerer Typ, wenn <see cref="IsArray"/> oder <see cref="IsOptional"/>. Inline statt
    /// über einen Tabellen-Index, weil keiner von beiden rekursiv sein kann (ADR-016).</summary>
    public BytecodeType? Element { get; init; }

    public override string ToString() => Tag switch
    {
        TypeTag.Ref => $"&ty{TypeIndex}",
        TypeTag.Array => $"{Element?.ToString() ?? "?"}[]",
        TypeTag.Optional => $"?{Element?.ToString() ?? "?"}",
        _ => Tag.ToString().ToLowerInvariant(),
    };
}

/// <summary>Layout eines zusammengesetzten Typs. Der Feldindex ist die Position in
/// <see cref="FieldTypes"/>; Feldnamen stehen nicht im Bytecode.</summary>
public sealed class BytecodeTypeDef
{
    public required string Name { get; init; }
    public required IReadOnlyList<BytecodeType> FieldTypes { get; init; }
}

/// <summary>Host-/Native-Funktion, per Index aus <c>call</c> referenziert (ADR-013, WASM-Modell).</summary>
public sealed class BytecodeImport
{
    public required string Name { get; init; }
    public required IReadOnlyList<BytecodeType> ParamTypes { get; init; }
    public required BytecodeType ReturnType { get; init; }
}

public sealed class BytecodeFunction
{
    public required string Name { get; init; }
    public required int ParamCount { get; init; }
    public required BytecodeType ReturnType { get; init; }

    /// <summary>Typ jedes Local-Slots. Die ersten <see cref="ParamCount"/> sind die Parameter —
    /// dieselbe Konvention wie in der IR.</summary>
    public required IReadOnlyList<BytecodeType> SlotTypes { get; init; }

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
