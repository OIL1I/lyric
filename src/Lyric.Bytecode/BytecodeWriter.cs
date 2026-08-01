using Lyric.Bytecode.Encoding;
using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Bytecode;

/// <summary>
/// <see cref="IrModule"/> → <c>.lyrbc</c>-Bytes.
///
/// <para><b>Deterministisch</b> (ADR-013): gleicher Input erzeugt byte-identischen Output. Dafür
/// braucht es drei Dinge — der String-Pool wird in Erst-Verwendungs-Reihenfolge aufgebaut (nicht in
/// Hash-Reihenfolge), es gibt keine Zeitstempel, und Sektionen erscheinen in aufsteigender
/// Id-Reihenfolge. Ohne das wären Golden-Tests und Bytecode-Diffs wertlos.</para>
///
/// <para>Der Aufbau ist zweistufig wie beim Lowering: erst <see cref="StackScheduler"/> je Funktion
/// (Slots, Stack-Platzierung, maximale Tiefe), dann die Emission. Die Trennung ist nötig, weil der
/// Funktionskopf Slot-Anzahl und Max-Tiefe <b>vor</b> dem Code trägt.</para>
/// </summary>
public static class BytecodeWriter
{
    public static byte[] Write(IrModule module)
    {
        var strings = new StringPool();
        var layouts = new List<FunctionLayout>(module.Functions.Count);

        // Namen zuerst internen, damit der Pool-Anfang stabil an der Funktionsreihenfolge hängt.
        foreach (var function in module.Functions)
        {
            strings.Intern(function.Name);
            layouts.Add(StackScheduler.Schedule(function));
        }

        // Code vor dem Header schreiben: er füllt den String-Pool, und der steht als Sektion davor.
        var bodies = new List<byte[]>(module.Functions.Count);
        for (var i = 0; i < module.Functions.Count; i++)
            bodies.Add(WriteFunction(module.Functions[i], layouts[i], strings));

        var writer = new ByteWriter();
        writer.Raw(Format.Magic);
        writer.U16(Format.VersionMajor);
        writer.U16(Format.VersionMinor);

        // Kein Capability-Bedarf: das Lowering kennt noch keine Imports. Die Bit-Zuordnung der
        // einzelnen Capabilities (ADR-007) entsteht mit der Stdlib in M8.
        WriteSection(writer, SectionId.Capabilities, s => s.ULeb(0UL));

        WriteSection(writer, SectionId.Strings, s =>
        {
            s.ULeb(strings.Count);
            foreach (var value in strings.InOrder) s.String(value);
        });

        // Leer, aber vorhanden: ADR-013 verlangt die Tabelle, das Lowering füllt sie erst, wenn es
        // externe Calls kennt (M6/M8). Die Rahmung jetzt festzulegen kostet nichts.
        WriteSection(writer, SectionId.Imports, s => s.ULeb(0));

        WriteSection(writer, SectionId.Functions, s =>
        {
            s.ULeb(bodies.Count);
            foreach (var body in bodies) s.Raw(body);
        });

        return writer.ToArray();
    }

    /// <summary>Sektion = Id, Byte-Länge, Inhalt. Die Länge erlaubt es einem Leser, eine unbekannte
    /// Sektion zu überspringen — das ist der Mechanismus hinter „Source-Map ist strippbar".</summary>
    private static void WriteSection(ByteWriter writer, SectionId id, Action<ByteWriter> body)
    {
        var payload = new ByteWriter();
        body(payload);
        var bytes = payload.ToArray();

        writer.U8((byte)id);
        writer.ULeb(bytes.Length);
        writer.Raw(bytes);
    }

    private static byte[] WriteFunction(IrFunction function, FunctionLayout layout, StringPool strings)
    {
        var code = new ByteWriter();
        var blockOffsets = new int[function.Blocks.Count];

        foreach (var block in function.Blocks)
        {
            blockOffsets[block.Id.Value] = code.Position;
            foreach (var op in block.Insts) WriteOp(code, function, layout, strings, op);
            WriteTerminator(code, layout, block.Terminator!);
        }

        var codeBytes = code.ToArray();
        var writer = new ByteWriter();

        writer.ULeb(strings.Intern(function.Name));
        writer.ULeb(function.ParamCount);
        writer.Tag(TagOf(function.ReturnType));

        writer.ULeb(layout.SlotTypes.Count);
        foreach (var type in layout.SlotTypes) writer.Tag(TagOf(type));

        writer.ULeb(layout.MaxStack);

        writer.ULeb(blockOffsets.Length);
        foreach (var offset in blockOffsets) writer.ULeb(offset);

        writer.ULeb(codeBytes.Length);
        writer.Raw(codeBytes);
        return writer.ToArray();
    }

    private static void WriteOp(ByteWriter code, IrFunction function, FunctionLayout layout,
        StringPool strings, IrOp op)
    {
        LoadSlotOperands(code, layout, IrShape.OperandsOf(op));

        switch (op)
        {
            case Const c:
                code.Opcode(Op.Const);
                code.Tag(TagOf(c.Type));
                WriteConstImmediate(code, strings, c);
                break;

            case BinOp b:
                code.Opcode(BinOpcode(b.Kind));
                // Das Tag nennt den OPERANDEN-Typ. Bei Vergleichen ist b.Type bool, aber die VM
                // muss wissen, was sie vergleicht (i64 und u64 sind verschiedene Maschinen-Ops).
                code.Tag(TagOf(function.Temps[b.Lhs.Value].Type));
                break;

            case UnOp u:
                switch (u.Kind)
                {
                    case IrUnKind.Neg: code.Opcode(Op.Neg); code.Tag(TagOf(u.Type)); break;
                    case IrUnKind.BitNot: code.Opcode(Op.BitNot); code.Tag(TagOf(u.Type)); break;
                    case IrUnKind.Not: code.Opcode(Op.Not); break; // nur bool, kein Tag nötig
                    default: throw new InternalCompilationException($"bytecode: unknown unop {u.Kind}");
                }
                break;

            case Lyric.Ir.Convert cv: // qualifiziert: kollidiert mit System.Convert
                code.Opcode(Op.Convert);
                code.Tag(TagOf(cv.From));
                code.Tag(TagOf(cv.To));
                break;

            case LoadLocal l:
                code.Opcode(Op.LoadLocal);
                code.ULeb(l.Local.Value); // IR-LocalId == Slot-Index (die ersten n Slots sind die Locals)
                break;

            case StoreLocal s:
                code.Opcode(Op.StoreLocal);
                code.ULeb(s.Local.Value);
                break;

            case Call k:
                code.Opcode(Op.Call);
                code.ULeb(k.Target.Value); // heute keine Imports -> Index == FunctionId
                break;

            default:
                throw new InternalCompilationException($"bytecode: unhandled op {op.GetType().Name}");
        }

        StoreOrDiscardDest(code, layout, IrShape.DestOf(op));
    }

    private static void WriteTerminator(ByteWriter code, FunctionLayout layout, IrTerminator terminator)
    {
        LoadSlotOperands(code, layout, IrShape.OperandsOf(terminator));

        switch (terminator)
        {
            case Return r:
                code.Opcode(r.Value is null ? Op.Return : Op.ReturnValue);
                break;
            case Branch b:
                code.Opcode(Op.Branch);
                code.ULeb(b.Target.Value);
                break;
            case CondBranch c:
                code.Opcode(Op.CondBranch);
                code.ULeb(c.IfTrue.Value);
                code.ULeb(c.IfFalse.Value);
                break;
            case Unreachable:
                code.Opcode(Op.Unreachable);
                break;
            default:
                throw new InternalCompilationException(
                    $"bytecode: unhandled terminator {terminator.GetType().Name}");
        }
    }

    /// <summary>Operanden, die in Slots liegen, kommen per <c>ldloc</c> auf den Stack. Der
    /// Scheduler garantiert, dass entweder <b>alle</b> Operanden schon auf dem Stack liegen (dann
    /// ist hier nichts zu tun) oder <b>keiner</b> — gemischt wäre nicht emittierbar, weil ein
    /// <c>ldloc</c> über einem bereits liegenden Operanden die Reihenfolge zerstörte.</summary>
    private static void LoadSlotOperands(ByteWriter code, FunctionLayout layout,
        IReadOnlyList<TempId> operands)
    {
        if (operands.Count == 0) return;
        if (layout.Placements[operands[0]] == Placement.Stack) return;

        foreach (var operand in operands)
        {
            code.Opcode(Op.LoadLocal);
            code.ULeb(layout.TempSlots[operand]);
        }
    }

    private static void StoreOrDiscardDest(ByteWriter code, FunctionLayout layout, TempId? dest)
    {
        if (dest is not { } temp) return;

        switch (layout.Placements[temp])
        {
            case Placement.Stack:
                break; // bleibt liegen, die nächste Instruktion konsumiert ihn
            case Placement.Slot:
                code.Opcode(Op.StoreLocal);
                code.ULeb(layout.TempSlots[temp]);
                break;
            case Placement.Discard:
                code.Opcode(Op.Pop);
                break;
        }
    }

    private static void WriteConstImmediate(ByteWriter code, StringPool strings, Const constant)
    {
        var kind = ((IrScalarType)constant.Type).Kind;
        switch (constant.Value)
        {
            // Zweierkomplement, nullerweitert auf 64 Bit — dieselbe Kodierung wie in der IR.
            case IntConst i: code.ULeb(i.Value); break;
            case FloatConst f when kind == IrScalar.F32: code.F32((float)f.Value); break;
            case FloatConst f: code.F64(f.Value); break;
            case BoolConst b: code.U8(b.Value ? (byte)1 : (byte)0); break;
            case CharConst c: code.ULeb((ulong)c.CodePoint); break;
            case StringConst s: code.ULeb(strings.Intern(s.Value)); break;
            default:
                throw new InternalCompilationException(
                    $"bytecode: unhandled const {constant.Value.GetType().Name}");
        }
    }

    private static Op BinOpcode(IrBinKind kind) => kind switch
    {
        IrBinKind.Add => Op.Add,
        IrBinKind.Sub => Op.Sub,
        IrBinKind.Mul => Op.Mul,
        IrBinKind.Div => Op.Div,
        IrBinKind.Rem => Op.Rem,
        IrBinKind.Shl => Op.Shl,
        IrBinKind.Shr => Op.Shr,
        IrBinKind.BitAnd => Op.BitAnd,
        IrBinKind.BitOr => Op.BitOr,
        IrBinKind.BitXor => Op.BitXor,
        IrBinKind.Lt => Op.Lt,
        IrBinKind.Le => Op.Le,
        IrBinKind.Gt => Op.Gt,
        IrBinKind.Ge => Op.Ge,
        IrBinKind.Eq => Op.Eq,
        IrBinKind.Ne => Op.Ne,
        _ => throw new InternalCompilationException($"bytecode: unknown binop {kind}")
    };

    internal static TypeTag TagOf(IrType type) => type switch
    {
        IrScalarType s => s.Kind switch
        {
            IrScalar.I8 => TypeTag.I8,
            IrScalar.I16 => TypeTag.I16,
            IrScalar.I32 => TypeTag.I32,
            IrScalar.I64 => TypeTag.I64,
            IrScalar.U8 => TypeTag.U8,
            IrScalar.U16 => TypeTag.U16,
            IrScalar.U32 => TypeTag.U32,
            IrScalar.U64 => TypeTag.U64,
            IrScalar.F32 => TypeTag.F32,
            IrScalar.F64 => TypeTag.F64,
            IrScalar.Bool => TypeTag.Bool,
            IrScalar.Char => TypeTag.Char,
            IrScalar.String => TypeTag.String,
            IrScalar.Void => TypeTag.Void,
            _ => throw new InternalCompilationException($"bytecode: unknown scalar {s.Kind}")
        },
        _ => throw new InternalCompilationException(
            $"bytecode: type not encodable: {type.GetType().Name}")
    };

    /// <summary>Konstantenpool für Strings. Erst-Verwendungs-Reihenfolge, damit der Output
    /// deterministisch ist — eine Hash-Reihenfolge wäre es nicht.</summary>
    private sealed class StringPool
    {
        private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
        private readonly List<string> _values = new();

        public int Count => _values.Count;
        public IReadOnlyList<string> InOrder => _values;

        public int Intern(string value)
        {
            if (_indices.TryGetValue(value, out var existing)) return existing;
            var index = _values.Count;
            _indices[value] = index;
            _values.Add(value);
            return index;
        }
    }
}
