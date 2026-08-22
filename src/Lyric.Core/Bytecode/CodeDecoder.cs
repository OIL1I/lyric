using Lyric.Bytecode.Encoding;

namespace Lyric.Bytecode;

/// <summary>
/// Decodes the instruction stream of a function.
///
/// <para>One place for both readers, the load-time validator and the disassembler, so an immediate
/// cannot be read at two different lengths.</para>
/// </summary>
public static class CodeDecoder
{
    public static List<BytecodeInstruction> Decode(byte[] code)
    {
        var reader = new ByteReader(code);
        var instructions = new List<BytecodeInstruction>();

        while (!reader.AtEnd)
        {
            var offset = reader.Position;
            var raw = reader.U8();
            if (!System.Enum.IsDefined(typeof(Op), raw))
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"unknown opcode 0x{raw:X2} at code offset {offset}");

            var opcode = (Op)raw;
            instructions.Add(opcode switch
            {
                Op.Const => DecodeConst(reader, offset),

                Op.LoadLocal or Op.StoreLocal or Op.Call or Op.Branch or Op.NewObject or
                Op.NewVariant or Op.EnumAs or Op.StructCopy or Op.Throw or
                Op.MakeClosure or Op.CallIndirect or
                Op.LoadGlobal or Op.StoreGlobal =>
                    new BytecodeInstruction { Offset = offset, Opcode = opcode, Immediate = reader.ULeb() },

                // ldfld/stfld carry a type index and a field index; the type lets the loader check
                // the field against a layout. mkiface carries concrete type and interface,
                // callvirt interface and slot — two uleb128 each, the same shape.
                Op.CondBranch or Op.LoadField or Op.StoreField or Op.MakeInterface
                    or Op.CallVirt => new BytecodeInstruction
                {
                    Offset = offset, Opcode = opcode,
                    Immediate = reader.ULeb(), Immediate2 = reader.ULeb(),
                },

                Op.Convert => new BytecodeInstruction
                {
                    Offset = offset, Opcode = opcode, Type = reader.Tag(), ToType = reader.Tag(),
                },

                // 'not' is the only arithmetic or logical opcode without a tag: only bool is
                // valid. The array opcodes carry none either; their element type is on the array.
                Op.Not or Op.Pop or Op.Return or Op.ReturnValue or Op.Unreachable or
                Op.EndFinally or
                Op.LoadElem or Op.StoreElem or Op.ArrayLen or Op.ArrayConcat or Op.ArrayRepeat or
                Op.OptIsSome or Op.OptGet or Op.EnumTag =>
                    new BytecodeInstruction { Offset = offset, Opcode = opcode },

                // The fused arithmetic (3.6): the operation as a byte, the operand tag, the
                // destination slot, then one or two sources.
                Op.BinLocals or Op.BinConst => DecodeFusedBinary(reader, offset, opcode),

                // The fused branches (3.6): the comparison as a byte, the operand tag, one or
                // two slots, then the two block targets — Immediate/Immediate2 as on condbr, so a
                // consumer that only cares where control goes reads them in the same place.
                Op.BranchCompare or Op.BranchCompareConst => DecodeBranchCompare(reader, offset, opcode),

                // newarr carries the element type, possibly nested, then the element count.
                Op.NewArray => DecodeNewArray(reader, offset),

                // optnone/optsome carry only the inner type, which the decoder skips.
                Op.OptNone or Op.OptSome => DecodeWithType(reader, offset, opcode),

                _ => new BytecodeInstruction { Offset = offset, Opcode = opcode, Type = reader.Tag() },
            });
        }

        return instructions;
    }

    /// <summary>
    /// A fused compare-and-branch. The comparison byte is checked HERE rather than left to the
    /// validator: it decides nothing about the stream's length, but a decoder that let an
    /// arbitrary byte through would hand the interpreter an opcode it switches on.
    /// </summary>
    private static BytecodeInstruction DecodeBranchCompare(ByteReader reader, int offset, Op opcode)
    {
        var raw = reader.U8();
        var kind = (Op)raw;
        if (kind is not (Op.Lt or Op.Le or Op.Gt or Op.Ge or Op.Eq or Op.Ne))
            throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                $"{opcode} at code offset {offset}: 0x{raw:X2} is not a comparison");

        var tag = reader.Tag();
        var instruction = new BytecodeInstruction
        {
            Offset = offset, Opcode = opcode, Fused = kind, Type = tag,
            SlotA = (int)reader.ULeb(),
        };

        if (opcode == Op.BranchCompare)
            instruction = instruction with { SlotB = (int)reader.ULeb() };
        else
            instruction = ReadFusedConstant(reader, offset, instruction, tag);

        return instruction with { Immediate = reader.ULeb(), Immediate2 = reader.ULeb() };
    }

    /// <summary>
    /// A fused binary operation. As with the branches, the operation byte is checked here rather
    /// than left to the validator: it decides nothing about the stream's length, but a decoder
    /// that let an arbitrary byte through would hand the interpreter an opcode it switches on.
    /// </summary>
    private static BytecodeInstruction DecodeFusedBinary(ByteReader reader, int offset, Op opcode)
    {
        var raw = reader.U8();
        var kind = (Op)raw;
        if (!IsFusibleBinary(kind))
            throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                $"{opcode} at code offset {offset}: 0x{raw:X2} is not a binary operation");

        var tag = reader.Tag();
        var instruction = new BytecodeInstruction
        {
            Offset = offset, Opcode = opcode, Fused = kind, Type = tag,
            SlotDest = (int)reader.ULeb(), SlotA = (int)reader.ULeb(),
        };

        return opcode == Op.BinLocals
            ? instruction with { SlotB = (int)reader.ULeb() }
            : ReadFusedConstant(reader, offset, instruction, tag);
    }

    /// <summary>The operations a fused form may carry: the arithmetic and bitwise pair
    /// operations, and the comparisons. One list, because the decoder and the disassembler have
    /// to agree with the writer about which bytes are legal there.</summary>
    internal static bool IsFusibleBinary(Op kind) => kind
        is Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Rem
        or Op.Shl or Op.Shr or Op.BitAnd or Op.BitOr or Op.BitXor
        or Op.Lt or Op.Le or Op.Gt or Op.Ge or Op.Eq or Op.Ne;

    /// <summary>The immediate of a fused constant shape, in the encoding <c>const</c> uses for the
    /// same tag — one encoding for constants, wherever they stand.</summary>
    private static BytecodeInstruction ReadFusedConstant(ByteReader reader, int offset,
        BytecodeInstruction instruction, TypeTag tag) => tag switch
    {
        TypeTag.F32 => instruction with { FloatValue = reader.F32() },
        TypeTag.F64 => instruction with { FloatValue = reader.F64() },
        TypeTag.Bool => instruction with { BoolValue = reader.U8() != 0 },
        TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64 or
        TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64 or
        TypeTag.Char => instruction with { ConstBits = reader.ULeb() },
        _ => throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
            $"{instruction.Opcode} at code offset {offset}: {tag} is not a scalar a fused form "
            + "compares"),
    };

    /// <summary>The element type of a <c>newarr</c> is skipped rather than read: the decoder only
    /// has to walk the stream. A caller that needs the type reads it itself.</summary>
    private static BytecodeInstruction DecodeNewArray(ByteReader reader, int offset)
    {
        var start = reader.Position;
        SkipType(reader, offset);
        var typeLength = reader.Position - start;

        return new BytecodeInstruction
        {
            Offset = offset, Opcode = Op.NewArray,
            Immediate = reader.ULeb(), Immediate2 = (ulong)typeLength,
        };
    }

    /// <summary>An instruction that carries only a type (<c>optnone</c>, <c>optsome</c>).
    /// </summary>
    private static BytecodeInstruction DecodeWithType(ByteReader reader, int offset, Op opcode)
    {
        SkipType(reader, offset);
        return new BytecodeInstruction { Offset = offset, Opcode = opcode };
    }

    /// <summary>
    /// Skips an inline-encoded type in the instruction stream.
    ///
    /// <para>Total over every tag, with a throwing <c>default</c>: a tag treated as a scalar when
    /// it in fact carries an index desynchronizes the stream, and the failure then surfaces many
    /// bytes later as an unknown opcode.</para>
    /// </summary>
    private static void SkipType(ByteReader reader, int offset)
    {
        var tag = reader.Tag();
        switch (tag)
        {
            // Carry a uleb128 table index.
            case TypeTag.Ref or TypeTag.Enum or TypeTag.Interface or TypeTag.Struct:
                reader.ULeb();
                return;

            // A host type carries its name inline: read the length and discard that many bytes.
            case TypeTag.Host:
                reader.String();
                return;

            // Carry their inner type inline.
            case TypeTag.Array or TypeTag.Optional:
                SkipType(reader, offset);
                return;

            // fn(A, B) -> R: parameter count, then the types, then the return type.
            case TypeTag.Fn:
            {
                var count = reader.ULeb();
                for (var i = 0UL; i < count; i++) SkipType(reader, offset);
                SkipType(reader, offset);
                return;
            }

            case TypeTag.Void:
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"composite type over void at code offset {offset}");

            case TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64
                or TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64
                or TypeTag.F32 or TypeTag.F64
                or TypeTag.Bool or TypeTag.Char or TypeTag.String:
                return; // scalars stand on their own

            default:
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"unknown type tag 0x{(byte)tag:X2} at code offset {offset}");
        }
    }

    private static BytecodeInstruction DecodeConst(ByteReader reader, int offset)
    {
        var tag = reader.Tag();
        var instruction = new BytecodeInstruction { Offset = offset, Opcode = Op.Const, Type = tag };

        return tag switch
        {
            TypeTag.F32 => instruction with { FloatValue = reader.F32() },
            TypeTag.F64 => instruction with { FloatValue = reader.F64() },
            TypeTag.Bool => instruction with { BoolValue = reader.U8() != 0 },
            TypeTag.Void => throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                $"const of type void at code offset {offset}"),
            // Integers, char and the string-pool index share the uleb128 form.
            _ => instruction with { Immediate = reader.ULeb() },
        };
    }

    /// <summary>How many values the instruction takes off the stack and how many it leaves.
    /// <paramref name="callArity"/> and <paramref name="callReturnsValue"/> apply to <c>call</c>
    /// and come from the callee's signature.</summary>
    public static (int Pops, int Pushes) StackEffect(BytecodeInstruction instruction,
        int callArity, bool callReturnsValue, int variantArity = 0) =>
        Effect(instruction, instruction.Immediate, callArity, callReturnsValue, variantArity);

    private static (int Pops, int Pushes) Effect(BytecodeInstruction instruction, ulong immediate,
        int callArity, bool callReturnsValue, int variantArity) => instruction.Opcode switch
    {
        Op.Const or Op.LoadLocal => (0, 1),
        Op.StoreLocal or Op.Pop => (1, 0),

        Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Rem or
        Op.Shl or Op.Shr or Op.BitAnd or Op.BitOr or Op.BitXor or
        Op.Lt or Op.Le or Op.Gt or Op.Ge or Op.Eq or Op.Ne => (2, 1),

        Op.Neg or Op.Not or Op.BitNot or Op.Convert => (1, 1),

        // The fused forms read slots and write slots; nothing of theirs reaches the stack. That is
        // most of why they are worth having.
        Op.BranchCompare or Op.BranchCompareConst or Op.BinLocals or Op.BinConst => (0, 0),

        Op.Call => (callArity, callReturnsValue ? 1 : 0),

        Op.NewObject => (0, 1),
        Op.LoadField => (1, 1),
        Op.StoreField => (2, 0),

        // newarr takes as many values as its immediate says.
        Op.NewArray => ((int)instruction.Immediate, 1),
        Op.LoadElem => (2, 1),
        Op.StoreElem => (3, 0),
        Op.ArrayLen => (1, 1),
        Op.ArrayConcat or Op.ArrayRepeat => (2, 1),

        Op.OptNone => (0, 1),
        Op.OptSome or Op.OptIsSome or Op.OptGet => (1, 1),

        // newvariant takes the variant's payload fields; how many is in the Types section, so the
        // caller passes the count in.
        Op.NewVariant => (variantArity, 1),
        Op.EnumTag or Op.EnumAs => (1, 1),

        // mkiface lifts a value to its interface: one off, one on.
        Op.MakeInterface => (1, 1),
        // structcopy likewise: original off, copy on.
        Op.StructCopy => (1, 1),

        // Both closure opcodes carry a flag in the lowest immediate bit, because their stack
        // effect does not follow from the opcode alone: a closure without captures has no
        // environment, and a function value does not carry its signature.
        Op.MakeClosure => ((immediate & 1) == 1 ? 1 : 0, 1),
        Op.CallIndirect => (1 + (int)(immediate >> 1), (immediate & 1) == 1 ? 1 : 0),

        Op.LoadGlobal => (0, 1),
        Op.StoreGlobal => (1, 0),
        // callvirt takes the receiver plus the arguments; the count comes from the interface
        // slot's signature and is passed in.
        Op.CallVirt => (callArity, callReturnsValue ? 1 : 0),

        Op.Return or Op.Branch or Op.Unreachable or Op.EndFinally => (0, 0),
        // throw takes the value and leaves nothing; the block ends here.
        Op.Throw => (1, 0),
        Op.ReturnValue or Op.CondBranch => (1, 0),

        _ => throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
            $"no stack effect defined for opcode {instruction.Opcode}"),
    };

    public static bool IsTerminator(Op opcode) => opcode is
        Op.Return or Op.ReturnValue or Op.Branch or Op.CondBranch or Op.Unreachable;
}
