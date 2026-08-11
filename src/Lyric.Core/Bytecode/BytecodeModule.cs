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

    /// <summary>Die vtable-Zeilen aus der Impls-Sektion. Leer, wenn das Modul keine Interfaces
    /// benutzt.</summary>
    public IReadOnlyList<BytecodeImpl> Impls { get; init; } = [];

    /// <summary>Die geschuetzten Regionen aus der Handlers-Sektion, innerste zuerst.</summary>
    public IReadOnlyList<BytecodeHandler> Handlers { get; init; } = [];

    /// <summary>Typ je globalem Slot. Der Index ist die Identitaet.</summary>
    public IReadOnlyList<BytecodeType> Globals { get; init; } = [];

    /// <summary>Die Funktion, die die Globals fuellt, im gemeinsamen Indexraum — oder
    /// <c>null</c>. Eine Runtime ruft sie <b>vor</b> dem Einstiegspunkt.</summary>
    public int? GlobalInit { get; init; }

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
    /// <summary>Trägt einen Index in die Types-Tabelle: Referenz auf eine Klasse oder auf ein
    /// Enum. Beide werden beim Laden gegen dieselbe Tabelle geprüft.</summary>
    public bool IsRef => Tag is TypeTag.Ref or TypeTag.Enum;
    public bool IsArray => Tag == TypeTag.Array;
    public bool IsOptional => Tag == TypeTag.Optional;

    /// <summary>Innerer Typ, wenn <see cref="IsArray"/> oder <see cref="IsOptional"/>. Inline statt
    /// über einen Tabellen-Index, weil keiner von beiden rekursiv sein kann (ADR-016).</summary>
    public BytecodeType? Element { get; init; }

    /// <summary>Der registrierte Name eines Host-Typs (<see cref="TypeTag.Host"/>). <c>null</c>
    /// bei allem anderen — ein Host-Typ hat keinen Tabellen-Eintrag, aus dem er kaeme.</summary>
    public string? HostName { get; init; }

    /// <summary>Parametertypen, wenn <see cref="Tag"/> <c>Fn</c> ist; <see cref="Element"/> haelt
    /// dann den Rueckgabetyp. Beides inline, weil ein Funktionstyp keinen Tabellen-Eintrag hat —
    /// er traegt seine Signatur selbst.</summary>
    public IReadOnlyList<BytecodeType> Parameters { get; init; } = [];

    public override string ToString() => Tag switch
    {
        TypeTag.Ref => $"&ty{TypeIndex}",
        TypeTag.Enum => $"enum ty{TypeIndex}",
        TypeTag.Array => $"{Element?.ToString() ?? "?"}[]",
        TypeTag.Optional => $"?{Element?.ToString() ?? "?"}",
        TypeTag.Fn => $"fn({string.Join(", ", Parameters)}) -> {Element?.ToString() ?? "?"}",
        _ => Tag.ToString().ToLowerInvariant(),
    };
}

/// <summary>Layout eines zusammengesetzten Typs. Der Feldindex ist die Position in
/// <see cref="FieldTypes"/>; Feldnamen stehen nicht im Bytecode.</summary>
public sealed class BytecodeTypeDef
{
    public required string Name { get; init; }
    public required IReadOnlyList<BytecodeType> FieldTypes { get; init; }

    /// <summary>Die Varianten, wenn dies ein <b>Enum</b>-Eintrag ist — sonst leer. Jede Variante ist
    /// selbst ein Layout-Eintrag; Slot 0 darin ist ihr Tag, der Index in dieser Liste.</summary>
    public IReadOnlyList<int> Variants { get; init; } = [];

    /// <summary>Die Methoden-Slots, wenn dies ein <b>Interface</b>-Eintrag ist — sonst leer. Der
    /// Index ist der Slot, auf den <c>callvirt</c> zeigt; die Namen stehen fuer Disassembler und
    /// Host-Bindung im Bytecode.</summary>
    public IReadOnlyList<string> MethodSlots { get; init; } = [];

    /// <summary>Wert-Semantik? Layout wie eine Klasse, aber jede Bindung kopiert.</summary>
    public bool IsStruct { get; init; }

    public bool IsEnum => Variants.Count > 0;

    public bool IsInterface => MethodSlots.Count > 0;
}

/// <summary>Eine geschuetzte Region einer Funktion. Bereiche sind <b>Block-Indizes</b>
/// <c>[Start, End)</c>, nicht Byte-Offsets — dieselbe Entscheidung wie bei den Sprungzielen.</summary>
public sealed class BytecodeHandler
{
    public required int Function { get; init; }
    public required int Start { get; init; }
    public required int End { get; init; }

    /// <summary>0 = catch, 1 = finally.</summary>
    public required int Kind { get; init; }

    /// <summary>Der gefangene Typ, oder <c>-1</c> fuer catch-all bzw. finally.</summary>
    public required int CatchType { get; init; }

    public required int Handler { get; init; }

    /// <summary>Slot fuer den gefangenen Wert, oder <c>-1</c>. Ueber einen Slot statt ueber den
    /// Stack, damit die Blockgrenzen-Invariante intakt bleibt.</summary>
    public required int Slot { get; init; }

    public bool IsFinally => Kind == 1;
}

/// <summary>Eine vtable-Zeile: Klasse erfuellt Interface, Slot fuer Slot mit einer Funktion aus dem
/// gemeinsamen Indexraum (erst Imports, dann Funktionen).</summary>
public sealed class BytecodeImpl
{
    public required int Type { get; init; }
    public required int Interface { get; init; }
    public required IReadOnlyList<int> Methods { get; init; }
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
