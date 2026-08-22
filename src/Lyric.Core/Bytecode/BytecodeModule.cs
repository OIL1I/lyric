namespace Lyric.Bytecode;

/// <summary>
/// A module read from <c>.lyrbc</c>.
///
/// <para>Not an <c>IrModule</c>: the bytecode is stack-based and the IR temp-based, and the temps
/// disappear into stack and local slots while emitting. The round-trip test therefore compares
/// bytes against bytes.</para>
///
/// <para>This is what the VM's loader consumes; the disassembler is text output over it.</para>
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

    /// <summary>The vtable rows from the Impls section. Empty when the module uses no
    /// interfaces.</summary>
    public IReadOnlyList<BytecodeImpl> Impls { get; init; } = [];

    /// <summary>The protected regions from the Handlers section, innermost first.</summary>
    public IReadOnlyList<BytecodeHandler> Handlers { get; init; } = [];

    /// <summary>The type of each global slot. The index is the identity.</summary>
    public IReadOnlyList<BytecodeType> Globals { get; init; } = [];

    /// <summary>The function that fills the globals, in the shared index space, or <c>null</c>.
    /// A runtime calls it before the entry point.</summary>
    public int? GlobalInit { get; init; }

    /// <summary>Index of the entry function in the shared index space, or <c>null</c> for a
    /// library module.</summary>
    public int? Start { get; init; }

    /// <summary>Positions from the SourceMap section, or <c>null</c> when the module carries none.
    /// A stripped module is valid; a runtime then names a function rather than a line.</summary>
    public BytecodeSourceMap? SourceMap { get; init; }

    /// <summary>The attribute rows from section 11 (format 3.2). Empty when the module carries
    /// none, and empty in every module a 3.1 compiler wrote.</summary>
    public IReadOnlyList<BytecodeAttribute> Attributes { get; init; } = [];

    /// <summary>Field names from section 12 (format 3.2). Required for types an attribute row
    /// references, permitted for any since 3.3; everywhere else field names stay out of the
    /// bytecode.</summary>
    public IReadOnlyList<BytecodeFieldNames> FieldNames { get; init; } = [];

    /// <summary>The opaque type names of section 14 (format 3.5), empty in every module written
    /// before it and in every module whose named types have no opaque field.</summary>
    public IReadOnlyList<BytecodeOpaqueFields> OpaqueFields { get; init; } = [];

    /// <summary>Local slot names from the DebugInfo section (id 13, format 3.3), one list per
    /// function, or <c>null</c> when the module carries none. A per-function list is either empty
    /// (the function says nothing) or exactly as long as its slot table; a compiler-created slot
    /// carries the empty string.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? SlotNames { get; init; }

    /// <summary>Global slot names from the DebugInfo section, empty when the module carries no
    /// section or the section says nothing about globals; otherwise exactly as long as
    /// <see cref="Globals"/>.</summary>
    public IReadOnlyList<string> GlobalNames { get; init; } = [];
}

/// <summary>What an attribute row describes.</summary>
public enum AttributeTargetKind : byte
{
    Function = 0,
    Type = 1,

    /// <summary>The module itself. The row's target index is 0 — the module is the file, so there
    /// is nothing to index.</summary>
    Module = 2,
}

/// <summary>
/// One attribute row: the struct type <see cref="Type"/> describes the target, with one value per
/// field of that type, in declaration order.
///
/// <para>The row is COMPLETE: a field the source did not write carries the field's literal
/// default, so a consumer never resolves one. That is also why there is no field index beside the
/// values — the position is the field index.</para>
/// </summary>
public sealed class BytecodeAttribute
{
    public required AttributeTargetKind TargetKind { get; init; }

    /// <summary>Index into Functions or Types depending on <see cref="TargetKind"/>; 0 for the
    /// module.</summary>
    public required int Target { get; init; }

    /// <summary>The attribute's struct type: an index into the Types section.</summary>
    public required int Type { get; init; }

    public required IReadOnlyList<BytecodeConstValue> Values { get; init; }
}

/// <summary>
/// A literal value in an attribute row: the tag, and a payload in the same encoding the
/// <c>const</c> opcode uses — integers and chars widened to 64 bits, floats as IEEE bit patterns,
/// strings through the pool.
/// </summary>
public sealed record BytecodeConstValue(TypeTag Tag)
{
    /// <summary>Integers two's-complement in 64 bits, chars zero-extended, bools 0/1, floats the
    /// IEEE bit pattern of the value (F32 widened to F64 bits when read back).</summary>
    public ulong Bits { get; init; }

    /// <summary>The value when <see cref="Tag"/> is <c>String</c>, and the qualified variant name
    /// (<c>Stage.Physics</c>) when it is <c>Enum</c>; <c>null</c> otherwise. An enum value carries
    /// both halves — the name here, the variant's tag in <see cref="Bits"/> — because a host
    /// reading a row wants the name and a program comparing one wants the number.</summary>
    public string? Text { get; init; }

    public long AsInt => (long)Bits;
    public double AsFloat => BitConverter.UInt64BitsToDouble(Bits);
    public bool AsBool => Bits != 0;
}

/// <summary>The field names of one type, in field order. Present only for types an attribute row
/// references.</summary>
public sealed class BytecodeFieldNames
{
    /// <summary>Index into the Types section.</summary>
    public required int Type { get; init; }

    public required IReadOnlyList<string> Names { get; init; }
}

/// <summary>
/// The names of the opaque types a type's fields were DECLARED with, in field order (section 14).
///
/// <para>Empty where a field's type is not opaque, and the list is either absent or exactly as
/// long as the field list — the position IS the field index, as in the Names section.</para>
/// </summary>
public sealed class BytecodeOpaqueFields
{
    /// <summary>Index into the Types section.</summary>
    public required int Type { get; init; }

    public required IReadOnlyList<string> Names { get; init; }
}

/// <summary>
/// A type in a signature position: the tag and, for a reference, the index into the type table.
///
/// <para>Its own type rather than a bare <see cref="TypeTag"/>, because a tag alone is not a
/// complete type: <c>0x40</c> without its index is incomplete.</para>
/// </summary>
/// <remarks>A <c>record class</c> rather than a <c>struct</c>: <see cref="Element"/> is another
/// <see cref="BytecodeType"/>, and a struct cannot contain itself.</remarks>
public sealed record BytecodeType(TypeTag Tag, int TypeIndex)
{
    public static BytecodeType Scalar(TypeTag tag) => new(tag, -1);
    /// <summary>Carries an index into the Types table: a reference to a class or an enum.
    /// </summary>
    public bool IsRef => Tag is TypeTag.Ref or TypeTag.Enum;
    public bool IsArray => Tag == TypeTag.Array;
    public bool IsOptional => Tag == TypeTag.Optional;

    /// <summary>The inner type when <see cref="IsArray"/> or <see cref="IsOptional"/>. Inline
    /// rather than through a table index, because neither can be recursive.</summary>
    public BytecodeType? Element { get; init; }

    /// <summary>The registered name of a host type. <c>null</c> for everything else; a host type
    /// has no table entry to take a name from.</summary>
    public string? HostName { get; init; }

    /// <summary>Parameter types when <see cref="Tag"/> is <c>Fn</c>; <see cref="Element"/> then
    /// holds the return type. Both inline, because a function type carries its own signature.
    /// </summary>
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

/// <summary>The layout of a composite type. The field index is the position in
/// <see cref="FieldTypes"/>; field names are not in the bytecode.</summary>
public sealed class BytecodeTypeDef
{
    public required string Name { get; init; }
    public required IReadOnlyList<BytecodeType> FieldTypes { get; init; }

    /// <summary>The variants when this is an enum entry, otherwise empty. Each variant is itself
    /// a layout entry whose slot 0 is its tag, the index in this list.</summary>
    public IReadOnlyList<int> Variants { get; init; } = [];

    /// <summary>The method slots when this is an interface entry, otherwise empty. The index is
    /// the slot <c>callvirt</c> addresses; the names are in the bytecode for the disassembler and
    /// for host binding.</summary>
    public IReadOnlyList<string> MethodSlots { get; init; } = [];

    /// <summary>Value semantics: the layout of a class, but every binding copies.</summary>
    public bool IsStruct { get; init; }

    public bool IsEnum => Variants.Count > 0;

    public bool IsInterface => MethodSlots.Count > 0;
}

/// <summary>A protected region of a function. Ranges are block indices <c>[Start, End)</c>, not
/// byte offsets.</summary>
public sealed class BytecodeHandler
{
    public required int Function { get; init; }
    public required int Start { get; init; }
    public required int End { get; init; }

    /// <summary>0 = catch, 1 = finally.</summary>
    public required int Kind { get; init; }

    /// <summary>The caught type, or <c>-1</c> for catch-all and for finally.</summary>
    public required int CatchType { get; init; }

    public required int Handler { get; init; }

    /// <summary>Slot for the caught value, or <c>-1</c>. Through a slot rather than the stack, so
    /// the block-boundary invariant holds.</summary>
    public required int Slot { get; init; }

    public bool IsFinally => Kind == 1;
}

/// <summary>A vtable row: a type implements an interface, slot by slot, with functions from the
/// shared index space.</summary>
public sealed class BytecodeImpl
{
    public required int Type { get; init; }
    public required int Interface { get; init; }
    public required IReadOnlyList<int> Methods { get; init; }
}

/// <summary>A host or native function, referenced by index from <c>call</c>.</summary>
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

    /// <summary>The type of each local slot. The first <see cref="ParamCount"/> are the
    /// parameters.</summary>
    public required IReadOnlyList<BytecodeType> SlotTypes { get; init; }

    /// <summary>Maximum operand stack depth, computed by the emitter so the loader knows the
    /// frame size without analysing.</summary>
    public required int MaxStack { get; init; }

    /// <summary>The byte offset of each block in <see cref="Code"/>. Jumps name block indices, so
    /// the loader checks a target with <c>index &lt; Count</c>.</summary>
    public required IReadOnlyList<int> BlockOffsets { get; init; }

    public required byte[] Code { get; init; }
}

/// <summary>A decoded instruction. Flat rather than a type hierarchy: there are only a handful of
/// operand shapes, and both readers want them without casts.</summary>
public sealed record BytecodeInstruction
{
    public required int Offset { get; init; }
    public required Op Opcode { get; init; }

    /// <summary>The operation's type tag; for <c>convert</c> the source type.</summary>
    public TypeTag? Type { get; init; }
    /// <summary><c>convert</c> only: the target type.</summary>
    public TypeTag? ToType { get; init; }

    /// <summary>Slot index, function index, block index, integer bit pattern, code point or
    /// string-pool index, depending on the opcode.</summary>
    public ulong Immediate { get; init; }
    /// <summary><c>condbr</c> only: the false branch.</summary>
    public ulong Immediate2 { get; init; }

    public double FloatValue { get; init; }
    public bool BoolValue { get; init; }

    /// <summary>Fused forms (3.6) only: what the instruction computes — the comparison for
    /// <c>brcmp</c>/<c>brcmpk</c>. The same <see cref="Op"/> value the unfused instruction would
    /// have carried, which is why the fused forms need no enumeration of their own. Meaningless
    /// for every other opcode.</summary>
    public Op Fused { get; init; }

    /// <summary>Fused forms only: the left operand's local slot.</summary>
    public int SlotA { get; init; }

    /// <summary>Fused forms only: the right operand's local slot, for the shapes that have one.
    /// The constant shapes carry their value in <see cref="ConstBits"/> or
    /// <see cref="FloatValue"/> instead and leave this at -1.</summary>
    public int SlotB { get; init; } = -1;

    /// <summary>Fused constant shapes only: the immediate's bit pattern, in the encoding
    /// <c>const</c> uses for the same tag — a float lands in <see cref="FloatValue"/>, a bool in
    /// <see cref="BoolValue"/>.</summary>
    public ulong ConstBits { get; init; }

    /// <summary>The fused ARITHMETIC forms only: the slot the result goes into. The fused
    /// branches have no destination and leave it at -1.</summary>
    public int SlotDest { get; init; } = -1;
}
