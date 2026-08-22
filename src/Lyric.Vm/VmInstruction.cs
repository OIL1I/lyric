using Lyric.Bytecode;

namespace Lyric.Vm;

/// <summary>
/// One decoded instruction, flat, for the interpreter's own array.
///
/// <para><b>Why this exists next to <see cref="BytecodeInstruction"/>.</b> That one is a
/// <c>record</c>, which is a class: an array of them is an array of REFERENCES, so reading an
/// opcode is a dependent load — the dispatch cannot begin until the pointer has arrived — and
/// every field after it is another hop into a heap object carrying a 16-byte header.</para>
///
/// <para>The interpreter already decodes once into an array of its own (<c>Prepared.From</c>),
/// so making that array flat costs nothing anywhere else. <see cref="BytecodeInstruction"/> stays
/// exactly as it is; it is the format's shape and the disassembler's, and neither is on a hot
/// path.</para>
///
/// <para><b>The nullable tags are unpacked here.</b> <c>TypeTag?</c> on a hot field means a
/// <c>Nullable&lt;T&gt;</c> read and a <c>.Value</c> unwrap on every arithmetic instruction. The
/// tag and its presence are separate fields instead, and only <c>Unary</c> — which genuinely
/// takes a <c>TypeTag?</c> — pays to put them back together.</para>
///
/// <para>Field order is by size, so the whole thing packs into 40 bytes with no padding holes:
/// three eight-byte values, one four-byte offset, then six single bytes.</para>
/// </summary>
internal readonly struct VmInstruction
{
    public readonly ulong Immediate;
    public readonly ulong Immediate2;
    public readonly double FloatValue;
    public readonly int Offset;

    public readonly Op Opcode;

    /// <summary>The operation's type tag; for <c>convert</c> the source type. Meaningless unless
    /// <see cref="HasType"/> — the opcode decides, and no opcode reads it when it is absent.
    /// </summary>
    public readonly TypeTag Type;

    /// <summary><c>convert</c> only: the target type.</summary>
    public readonly TypeTag ToType;

    public readonly bool BoolValue;
    public readonly bool HasType;
    public readonly bool HasToType;

    /// <summary>Fused forms (3.6) only: what the instruction computes — the comparison a
    /// <c>brcmp</c> performs. Meaningless for every other opcode.</summary>
    public readonly Op Fused;

    /// <summary>Fused forms only: the operand slots. The constant shapes leave
    /// <see cref="SlotB"/> at -1 and carry their value in <see cref="Immediate"/> or
    /// <see cref="FloatValue"/>.</summary>
    public readonly int SlotA;
    public readonly int SlotB;

    /// <summary>Fused constant shapes only: the immediate's bit pattern, in the encoding
    /// <c>const</c> uses for the same tag; a float arrives in <see cref="FloatValue"/>.</summary>
    public readonly ulong ConstBits;

    public VmInstruction(BytecodeInstruction source)
    {
        Immediate = source.Immediate;
        Immediate2 = source.Immediate2;
        FloatValue = source.FloatValue;
        Offset = source.Offset;
        Opcode = source.Opcode;

        HasType = source.Type.HasValue;
        Type = source.Type.GetValueOrDefault();

        HasToType = source.ToType.HasValue;
        ToType = source.ToType.GetValueOrDefault();

        BoolValue = source.BoolValue;

        Fused = source.Fused;
        SlotA = source.SlotA;
        SlotB = source.SlotB;

        // Its own field rather than Immediate: a fused branch's targets sit in
        // Immediate/Immediate2, as on condbr, so a consumer that only asks where control goes
        // reads them in the same place whichever branch it is looking at.
        ConstBits = source.ConstBits;
    }



    /// <summary>The tag as an optional again, for the one caller that wants it that way.</summary>
    public TypeTag? TypeOrNull => HasType ? Type : null;
}
