using Lyric.Bytecode.Encoding;

namespace Lyric.Bytecode;

/// <summary>
/// Dekodiert den Instruktionsstrom einer Funktion.
///
/// <para>Eine Stelle für beide Leser: den Validator beim Laden und den Disassembler. Zwei
/// Dekodierer wären derselbe Drift-Fehler wie zwei Opcode-Tabellen — einer könnte ein Immediate
/// anders lang lesen als der andere und die Ausgabe wäre stillschweigend falsch.</para>
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
                Op.NewVariant or Op.EnumAs =>
                    new BytecodeInstruction { Offset = offset, Opcode = opcode, Immediate = reader.ULeb() },

                // ldfld/stfld tragen Typ- UND Feldindex. Der Typ ist zur Laufzeit redundant, aber
                // ohne ihn könnte der Loader den Feldindex nicht gegen ein Layout prüfen.
                Op.CondBranch or Op.LoadField or Op.StoreField => new BytecodeInstruction
                {
                    Offset = offset, Opcode = opcode,
                    Immediate = reader.ULeb(), Immediate2 = reader.ULeb(),
                },

                Op.Convert => new BytecodeInstruction
                {
                    Offset = offset, Opcode = opcode, Type = reader.Tag(), ToType = reader.Tag(),
                },

                // Not trägt als einziger arithmetisch/logischer Opcode kein Tag: nur bool ist
                // gültig, ein Tag wäre reine Redundanz. Die Array-Opcodes tragen ebenfalls keins:
                // ihr Elementtyp steht in der Temp-Tabelle bzw. am Array selbst.
                Op.Not or Op.Pop or Op.Return or Op.ReturnValue or Op.Unreachable or
                Op.LoadElem or Op.StoreElem or Op.ArrayLen or Op.ArrayConcat or Op.ArrayRepeat or
                Op.OptIsSome or Op.OptGet or Op.EnumTag =>
                    new BytecodeInstruction { Offset = offset, Opcode = opcode },

                // newarr trägt den Elementtyp (ggf. verschachtelt) und dann die Elementzahl.
                Op.NewArray => DecodeNewArray(reader, offset),

                // optnone/optsome tragen nur den inneren Typ; der Dekodierer ueberspringt ihn.
                Op.OptNone or Op.OptSome => DecodeWithType(reader, offset, opcode),

                _ => new BytecodeInstruction { Offset = offset, Opcode = opcode, Type = reader.Tag() },
            });
        }

        return instructions;
    }

    /// <summary>Der Elementtyp eines <c>newarr</c> wird übersprungen, nicht ausgewertet: der
    /// Dekodierer muss den Strom nur korrekt abschreiten. Wer den Typ braucht — der Disassembler —
    /// liest ihn über <see cref="SkipType"/> hinaus selbst.</summary>
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

    /// <summary>Eine Instruktion, die nur einen Typ trägt (<c>optnone</c>, <c>optsome</c>).</summary>
    private static BytecodeInstruction DecodeWithType(ByteReader reader, int offset, Op opcode)
    {
        SkipType(reader, offset);
        return new BytecodeInstruction { Offset = offset, Opcode = opcode };
    }

    /// <summary>Überspringt einen Typ im Strom: ein Tag, dann bei <c>Ref</c> ein Index und bei
    /// <c>Array</c>/<c>Optional</c> rekursiv der innere Typ.</summary>
    private static void SkipType(ByteReader reader, int offset)
    {
        var tag = reader.Tag();
        if (tag == TypeTag.Ref) reader.ULeb();
        else if (tag is TypeTag.Array or TypeTag.Optional) SkipType(reader, offset);
        else if (tag == TypeTag.Void)
            throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                $"composite type over void at code offset {offset}");
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
            // Ganzzahlen, char und der String-Pool-Index teilen sich die uleb128-Form.
            _ => instruction with { Immediate = reader.ULeb() },
        };
    }

    /// <summary>Wie viele Werte nimmt die Instruktion vom Stack, wie viele legt sie zurück?
    /// <paramref name="callArity"/> und <paramref name="callReturnsValue"/> gelten nur für
    /// <c>call</c> und kommen aus der Signatur der Callee.</summary>
    public static (int Pops, int Pushes) StackEffect(BytecodeInstruction instruction,
        int callArity, bool callReturnsValue, int variantArity = 0) => instruction.Opcode switch
    {
        Op.Const or Op.LoadLocal => (0, 1),
        Op.StoreLocal or Op.Pop => (1, 0),

        Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Rem or
        Op.Shl or Op.Shr or Op.BitAnd or Op.BitOr or Op.BitXor or
        Op.Lt or Op.Le or Op.Gt or Op.Ge or Op.Eq or Op.Ne => (2, 1),

        Op.Neg or Op.Not or Op.BitNot or Op.Convert => (1, 1),

        Op.Call => (callArity, callReturnsValue ? 1 : 0),

        Op.NewObject => (0, 1),
        Op.LoadField => (1, 1),
        Op.StoreField => (2, 0),

        // newarr nimmt so viele Werte, wie sein Immediate sagt — die einzige Instruktion mit
        // variabler Stack-Wirkung außer 'call'.
        Op.NewArray => ((int)instruction.Immediate, 1),
        Op.LoadElem => (2, 1),
        Op.StoreElem => (3, 0),
        Op.ArrayLen => (1, 1),
        Op.ArrayConcat or Op.ArrayRepeat => (2, 1),

        Op.OptNone => (0, 1),
        Op.OptSome or Op.OptIsSome or Op.OptGet => (1, 1),

        // newvariant nimmt die Nutzfelder der Variante — wie viele, steht in der Types-Sektion.
        // Deshalb reicht der Aufrufer sie herein, genau wie bei 'call'.
        Op.NewVariant => (variantArity, 1),
        Op.EnumTag or Op.EnumAs => (1, 1),

        Op.Return or Op.Branch or Op.Unreachable => (0, 0),
        Op.ReturnValue or Op.CondBranch => (1, 0),

        _ => throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
            $"no stack effect defined for opcode {instruction.Opcode}"),
    };

    public static bool IsTerminator(Op opcode) => opcode is
        Op.Return or Op.ReturnValue or Op.Branch or Op.CondBranch or Op.Unreachable;
}
