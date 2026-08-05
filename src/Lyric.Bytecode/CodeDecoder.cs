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
                Op.NewVariant or Op.EnumAs or Op.StructCopy =>
                    new BytecodeInstruction { Offset = offset, Opcode = opcode, Immediate = reader.ULeb() },

                // ldfld/stfld tragen Typ- UND Feldindex. Der Typ ist zur Laufzeit redundant, aber
                // ohne ihn könnte der Loader den Feldindex nicht gegen ein Layout prüfen.
                // mkiface traegt konkreten Typ UND Interface, callvirt Interface UND Slot —
                // beide zwei uleb128, dieselbe Form wie ldfld.
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
    /// <summary>
    /// Ueberspringt einen inline kodierten Typ im Instruktionsstrom.
    ///
    /// <para><b>Total ueber alle Tags</b>, und das ist keine Stilfrage. Die erste Fassung war eine
    /// <c>else if</c>-Kette, die jedes nicht genannte Tag stillschweigend als Skalar behandelte —
    /// also den Index nicht las, den es traegt. Ein <c>?Enum</c> desynchronisierte damit den Strom
    /// und meldete sich viele Bytes spaeter als „unknown opcode 0x00": ein Fehler, der nichts mehr
    /// ueber seine Ursache sagt. Gefunden am 2026-08-05 beim Bau der Interfaces, vorhanden seit
    /// P3b. Der <c>default</c>-Wurf sorgt dafuer, dass die naechste Tag-Art beim Uebersehen laut
    /// wird statt still falsch.</para>
    /// </summary>
    private static void SkipType(ByteReader reader, int offset)
    {
        var tag = reader.Tag();
        switch (tag)
        {
            // Tragen einen uleb128-Tabellenindex hinter sich.
            case TypeTag.Ref or TypeTag.Enum or TypeTag.Interface or TypeTag.Struct:
                reader.ULeb();
                return;

            // Tragen ihren inneren Typ inline.
            case TypeTag.Array or TypeTag.Optional:
                SkipType(reader, offset);
                return;

            case TypeTag.Void:
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"composite type over void at code offset {offset}");

            case TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64
                or TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64
                or TypeTag.F32 or TypeTag.F64
                or TypeTag.Bool or TypeTag.Char or TypeTag.String:
                return; // Skalare stehen fuer sich

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

        // mkiface hebt einen Wert auf sein Interface: einer runter, einer rauf.
        Op.MakeInterface => (1, 1),
        // structcopy ebenso: Original runter, Kopie rauf.
        Op.StructCopy => (1, 1),
        // callvirt nimmt den Empfaenger plus die Argumente — wie viele, weiss nur der Aufrufer aus
        // der Signatur des Interface-Slots. Deshalb reicht er sie herein, genau wie bei 'call'.
        Op.CallVirt => (callArity, callReturnsValue ? 1 : 0),

        Op.Return or Op.Branch or Op.Unreachable => (0, 0),
        Op.ReturnValue or Op.CondBranch => (1, 0),

        _ => throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
            $"no stack effect defined for opcode {instruction.Opcode}"),
    };

    public static bool IsTerminator(Op opcode) => opcode is
        Op.Return or Op.ReturnValue or Op.Branch or Op.CondBranch or Op.Unreachable;
}
