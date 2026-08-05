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
        // Typnamen gehören hierher und nicht in die Types-Sektion: der String-Pool ist Sektion 2
        // und damit lange geschrieben, bevor Sektion 3 an der Reihe wäre.
        foreach (var type in module.Types) strings.Intern(type.Name);

        foreach (var function in module.Functions)
        {
            strings.Intern(function.Name);
            layouts.Add(StackScheduler.Schedule(function));
        }

        // Code vor dem Header schreiben: er füllt den String-Pool, und der steht als Sektion davor.
        var bodies = new List<byte[]>(module.Functions.Count);
        for (var i = 0; i < module.Functions.Count; i++)
            bodies.Add(WriteFunction(module.Functions[i], layouts[i], strings, module.Imports.Count));

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

        // Muss vor Imports und Functions stehen — Sektions-Ids sind aufsteigend, und beide dürfen
        // Referenztypen in ihren Signaturen nennen.
        if (module.Types.Count > 0)
            WriteSection(writer, SectionId.Types, s =>
            {
                s.ULeb(module.Types.Count);
                foreach (var type in module.Types)
                {
                    s.ULeb(strings.Intern(type.Name));
                    s.U8((byte)(type.IsEnum ? TypeKind.Enum : TypeKind.Layout));

                    if (type.IsEnum)
                    {
                        // Ein Enum traegt keine eigenen Felder — seine Varianten tun das.
                        s.ULeb(type.Variants.Length);
                        foreach (var variant in type.Variants) s.ULeb(variant.Value);
                        continue;
                    }

                    s.ULeb(type.FieldTypes.Length);
                    foreach (var field in type.FieldTypes) WriteType(s, field);
                }
            });

        // Symbolisch: Name + Signatur, gebunden beim Laden (ADR-013, WASM-Modell). Keine Adressen.
        WriteSection(writer, SectionId.Imports, s =>
        {
            s.ULeb(module.Imports.Count);
            foreach (var import in module.Imports)
            {
                s.String(import.Name);
                s.ULeb(import.ParamTypes.Length);
                foreach (var type in import.ParamTypes) WriteType(s, type);
                WriteType(s, import.ReturnType);
            }
        });

        WriteSection(writer, SectionId.Functions, s =>
        {
            s.ULeb(bodies.Count);
            foreach (var body in bodies) s.Raw(body);
        });

        // Fehlt bei Bibliotheks-Modulen. Muss nach Functions stehen: Sektionen sind aufsteigend.
        //
        // Der Index laeuft in den GEMEINSAMEN Raum (erst Imports, dann Funktionen) — derselbe, den
        // 'call' benutzt. Bytecode.md §Start (Id 7) sagt das seit jeher; der Writer schrieb bis
        // 2026-08-05 die nackte FunctionId. Aufgefallen ist es nie, weil beide Lesarten
        // zusammenfallen, sobald ein Modul keine Importe hat: examples/arith.lyr lief korrekt,
        // examples/hello.lyr haette eine spec-treue Fremd-Runtime in einen Import springen lassen.
        if (module.EntryFunction is { } entry)
            WriteSection(writer, SectionId.Start,
                s => s.ULeb(module.Imports.Count + entry.Value));

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

    private static byte[] WriteFunction(IrFunction function, FunctionLayout layout, StringPool strings, int importCount)
    {
        var code = new ByteWriter();
        var blockOffsets = new int[function.Blocks.Count];

        foreach (var block in function.Blocks)
        {
            blockOffsets[block.Id.Value] = code.Position;
            foreach (var op in block.Insts) WriteOp(code, function, layout, strings, op, importCount);
            WriteTerminator(code, layout, block.Terminator!);
        }

        var codeBytes = code.ToArray();
        var writer = new ByteWriter();

        writer.ULeb(strings.Intern(function.Name));
        writer.ULeb(function.ParamCount);
        WriteType(writer, function.ReturnType);

        writer.ULeb(layout.SlotTypes.Count);
        foreach (var type in layout.SlotTypes) WriteType(writer, type);

        writer.ULeb(layout.MaxStack);

        writer.ULeb(blockOffsets.Length);
        foreach (var offset in blockOffsets) writer.ULeb(offset);

        writer.ULeb(codeBytes.Length);
        writer.Raw(codeBytes);
        return writer.ToArray();
    }

    private static void WriteOp(ByteWriter code, IrFunction function, FunctionLayout layout,
        StringPool strings, IrOp op, int importCount)
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

            case CallImport k:
                // Gemeinsamer Indexraum: erst Imports, dann Funktionen. Die Arithmetik sitzt hier,
                // weil hier die Konvention lebt — die IR hält beide bewusst getrennt.
                code.Opcode(Op.Call);
                code.ULeb(k.Target.Value);
                break;

            case Call k:
                code.Opcode(Op.Call);
                code.ULeb(importCount + k.Target.Value);
                break;

            case NewObject n:
                code.Opcode(Op.NewObject);
                code.ULeb(n.Type.Value);
                break;

            case LoadField f:
                code.Opcode(Op.LoadField);
                code.ULeb(f.Type.Value);
                code.ULeb(f.Field.Value);
                break;

            case StoreField f:
                code.Opcode(Op.StoreField);
                code.ULeb(f.Type.Value);
                code.ULeb(f.Field.Value);
                break;

            case NewArray a:
                code.Opcode(Op.NewArray);
                WriteType(code, a.Element);
                code.ULeb(a.Elements.Length);
                break;

            case OptNone n:
                code.Opcode(Op.OptNone);
                WriteType(code, n.Inner);
                break;

            case OptSome s:
                code.Opcode(Op.OptSome);
                WriteType(code, s.Inner);
                break;

            case OptIsSome: code.Opcode(Op.OptIsSome); break;
            case OptGet: code.Opcode(Op.OptGet); break;

            case NewVariant v:
                code.Opcode(Op.NewVariant);
                code.ULeb(v.Variant.Value);
                break;

            case EnumTag: code.Opcode(Op.EnumTag); break;

            case EnumAs a:
                code.Opcode(Op.EnumAs);
                code.ULeb(a.Variant.Value);
                break;

            case LoadElem: code.Opcode(Op.LoadElem); break;
            case StoreElem: code.Opcode(Op.StoreElem); break;
            case ArrayLen: code.Opcode(Op.ArrayLen); break;
            case ArrayConcat: code.Opcode(Op.ArrayConcat); break;
            case ArrayRepeat: code.Opcode(Op.ArrayRepeat); break;

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

    /// <summary>Ein Typ im Bytecode: Tag, und bei zusammengesetzten der Index dahinter
    /// (Bytecode.md §3). Einzige Schreibstelle für Typen — <c>Tag</c> allein reicht seit 1.2 nicht
    /// mehr, und eine vergessene Stelle wäre ein um ein Byte verschobener Strom.</summary>
    internal static void WriteType(ByteWriter w, IrType type)
    {
        w.Tag(TagOf(type));
        if (type is IrRefType r) w.ULeb(r.Type.Value);
        // Der Elementtyp steht inline und rekursiv — int[][] ist 0x41 0x41 0x04.
        if (type is IrArrayType a) WriteType(w, a.Element);
        if (type is IrOptionalType o) WriteType(w, o.Inner);
        if (type is IrEnumType e) w.ULeb(e.Type.Value);
    }

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
        IrRefType => TypeTag.Ref,
        IrArrayType => TypeTag.Array,
        IrOptionalType => TypeTag.Optional,
        IrEnumType => TypeTag.Enum,
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
