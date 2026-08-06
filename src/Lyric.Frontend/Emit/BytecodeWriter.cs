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
                    s.U8((byte)(type.IsEnum ? TypeKind.Enum
                        : type.IsInterface ? TypeKind.Interface
                        : type.IsStruct ? TypeKind.Struct
                        : TypeKind.Layout));

                    if (type.IsInterface)
                    {
                        // Ein Interface traegt keine Felder, sondern Slot-Namen. Sie stehen im
                        // Bytecode — anders als Feldnamen —, weil ein Disassembler sonst nur
                        // 'ty3#1' zeigen koennte und eine Fremd-Runtime beim Binden von
                        // Host-Implementierungen keinen Anhaltspunkt haette.
                        s.ULeb(type.MethodSlots.Length);
                        foreach (var slot in type.MethodSlots) s.String(slot);
                        continue;
                    }

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

        // Interface-Implementierungen. Ganz zuletzt, weil Sektions-Ids strikt aufsteigen
        // muessen und Impls (8) hinter Start (7) liegt.
        if (module.Impls.Count > 0)
            WriteSection(writer, SectionId.Impls, s =>
            {
                s.ULeb(module.Impls.Count);
                foreach (var impl in module.Impls)
                {
                    s.ULeb(impl.Type.Value);
                    s.ULeb(impl.Interface.Value);
                    s.ULeb(impl.Methods.Length);
                    // Funktionsindex im GEMEINSAMEN Raum (erst Imports, dann Funktionen) — wie
                    // bei 'call' und bei der Start-Sektion. Ein Import als vtable-Eintrag ist
                    // damit ausdrueckbar; ob eine Runtime das zulaesst, ist ihre Sache.
                    foreach (var method in impl.Methods)
                        s.ULeb(module.Imports.Count + method.Value);
                }
            });


        // Globale Slots samt ihrer Init-Funktion. Vor Handlers (9), hinter Start (7)? Nein:
        // Globals ist 10 und steht damit ganz am Ende — Sektions-Ids steigen strikt.
        if (module.Globals.Count > 0)
            WriteSection(writer, SectionId.Globals, s =>
            {
                s.ULeb(module.Globals.Count);
                foreach (var global in module.Globals) WriteType(s, global.Type);

                // 0 = keine Init-Funktion; sonst der Index im gemeinsamen Raum, um eins erhoeht.
                s.ULeb(module.GlobalInit is { } init
                    ? (ulong)(module.Imports.Count + init.Value + 1)
                    : 0UL);
            });

        // Geschuetzte Regionen. Ganz zuletzt: Sektions-Ids steigen strikt, Handlers (9) liegt
        // hinter Impls (8).
        var handlers = module.Functions
            .SelectMany((fn, index) => fn.Handlers.Select(h => (Function: index, Handler: h)))
            .ToList();

        if (handlers.Count > 0)
            WriteSection(writer, SectionId.Handlers, s =>
            {
                s.ULeb(handlers.Count);
                foreach (var (function, h) in handlers)
                {
                    s.ULeb(function);
                    s.ULeb(h.Start.Value);
                    s.ULeb(h.End.Value);
                    s.U8((byte)(h.Kind == IrHandlerKind.Finally ? 1 : 0));
                    // -1 als "kein Typ"/"kein Slot": im Strom als uleb128 0, der echte Index +1.
                    // Ein eigenes Praesenz-Byte waere ein Byte mehr fuer dieselbe Aussage.
                    s.ULeb(h.CatchType is { } t ? (ulong)(t.Value + 1) : 0UL);
                    s.ULeb(h.Handler.Value);
                    s.ULeb(h.Slot is { } slot ? (ulong)(slot.Value + 1) : 0UL);
                }
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

            case MakeInterface m:
                code.Opcode(Op.MakeInterface);
                code.ULeb(m.Concrete.Value);
                code.ULeb(m.Interface.Value);
                break;

            case CallVirt c:
                code.Opcode(Op.CallVirt);
                code.ULeb(c.Interface.Value);
                code.ULeb(c.Slot);
                break;

            case StructCopy c:
                code.Opcode(Op.StructCopy);
                code.ULeb(c.Type.Value);
                break;

            case MakeClosure m:
                code.Opcode(Op.MakeClosure);
                // Zielindex im gemeinsamen Aufruf-Indexraum (erst Imports, dann Funktionen) —
                // dieselbe Rechnung wie bei 'call'. Das UNTERSTE BIT sagt, ob ein Environment auf
                // dem Stack liegt: ein Leser muss die Stack-Wirkung beim Laden kennen (ADR-013),
                // und eine Closure ohne Captures hat kein Environment.
                code.ULeb(((ulong)(importCount + m.Target.Value) << 1)
                          | (m.Environment is null ? 0UL : 1UL));
                break;

            case CallIndirect c:
                code.Opcode(Op.CallIndirect);
                // Argumentzahl ohne den Aufgerufenen; unterstes Bit: liefert einen Wert. Dieselbe
                // Kodierung wie bei mkclosure und aus demselben Grund — bei 'call' steht beides in
                // der Zielsignatur, hier gibt es keine.
                code.ULeb(((ulong)c.Args.Length << 1) | (c.Dest is null ? 0UL : 1UL));
                break;

            case LoadGlobal l:
                code.Opcode(Op.LoadGlobal);
                code.ULeb(l.Global.Value);
                break;

            case StoreGlobal g:
                code.Opcode(Op.StoreGlobal);
                code.ULeb(g.Global.Value);
                break;

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

            case Throw t:
                code.Opcode(Op.Throw);
                // 0 = "steht erst zur Laufzeit fest"; der echte Index steht um eins erhoeht.
                code.ULeb(t.Concrete is { } thrown ? (ulong)(thrown.Value + 1) : 0UL);
                break;

            case EndFinally:
                code.Opcode(Op.EndFinally);
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
        if (type is IrInterfaceType i) w.ULeb(i.Type.Value);
        if (type is IrStructType v) w.ULeb(v.Type.Value);

        // Strukturell: Parameterzahl, Parametertypen, Rueckgabetyp. Als einziger zusammengesetzter
        // Typ ohne Tabellen-Eintrag — er hat keine Deklaration, an der eine Id haengen koennte.
        if (type is IrFunctionType f)
        {
            w.ULeb(f.Parameters.Length);
            foreach (var parameter in f.Parameters) WriteType(w, parameter);
            WriteType(w, f.Return);
        }
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
        IrInterfaceType => TypeTag.Interface,
        IrStructType => TypeTag.Struct,
        IrFunctionType => TypeTag.Fn,
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
