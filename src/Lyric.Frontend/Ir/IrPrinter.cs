using System.Globalization;
using System.Text;
using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// A deterministic text dump of an <see cref="IrModule"/> or <see cref="IrFunction"/> for golden
/// snapshots and debug output, analogous to <c>AstDumper</c>. One instruction per line, two-space
/// indentation per level, and the newline is always '\n' — never AppendLine, whose CRLF would break
/// the snapshots on a Linux CI.
///
/// Format: the type stands at the destination (<c>t2: bool = lt t0, t1</c>) rather than in the
/// mnemonic, so every line is formattable from the instruction's fields alone, without a temp table
/// lookup. The one exception is <c>call</c>: the name and return type of the target function live at
/// the callee rather than at the call site and are resolved through the <see cref="CallContext"/>
/// (index to module.Functions).
///
/// The dump uses a <c>switch</c> rather than a visitor: the <c>default</c> throw forces completeness
/// as soon as a new instruction is added.
/// </summary>
public static class IrPrinter
{
    public static string Dump(IrModule module)
    {
        var sb = new StringBuilder();
        var ctx = CallContext.ForModule(module);
        WriteTypes(sb, module.Types);
        WriteGlobals(sb, module.Globals);
        WriteImpls(sb, module.Impls);
        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (i > 0 || module.Types.Count > 0) sb.Append('\n'); // a blank line between blocks
            WriteFunction(sb, module.Functions[i], ctx);
        }
        return sb.ToString();
    }

    /// <summary>A standalone dump of a single function. Without module context, call targets print as a
    /// raw <c>fN</c> index and their return type as <c>?</c>.</summary>
    public static string Dump(IrFunction function)
    {
        var sb = new StringBuilder();
        WriteFunction(sb, function, CallContext.None);
        return sb.ToString();
    }

    // --- call resolution: FunctionId to (name, return type) from the function list ---
    private readonly struct CallContext
    {
        private readonly IReadOnlyList<IrFunction>? _functions;
        private readonly IReadOnlyList<IrImport>? _imports;

        private CallContext(IReadOnlyList<IrFunction>? functions, IReadOnlyList<IrImport>? imports)
        {
            _functions = functions;
            _imports = imports;
        }

        public static CallContext ForModule(IrModule module) => new(module.Functions, module.Imports);
        public static CallContext None => new(functions: null, imports: null);

        public IrImport? ImportOf(ImportId id) =>
            _imports is null || id.Value < 0 || id.Value >= _imports.Count ? null : _imports[id.Value];

        public string NameOf(FunctionId id) =>
            _functions is null ? id.ToString() : _functions[id.Value].Name;

        public IrType? ReturnTypeOf(FunctionId id) =>
            _functions is null ? null : _functions[id.Value].ReturnType;
    }

    /// <summary>
    /// The type table as a block of its own at the head of the dump. Field names appear HERE ONLY —
    /// the instruction stream holds the index, because the index is what is executed.
    ///
    /// <para>That is also why <see cref="TypeStr"/> still works without context: whoever reads
    /// <c>&amp;ty0</c> looks it up once at the top, instead of every line repeating the type name. The
    /// rule that every line is formattable from the instruction's fields alone stays intact.</para>
    /// </summary>
    private static void WriteTypes(StringBuilder sb, IReadOnlyList<IrTypeDef> types)
    {
        for (var i = 0; i < types.Count; i++)
        {
            var def = types[i];
            if (def.FieldTypes.Length != def.FieldNames.Length)
                throw new InternalCompilationException(
                    $"ir-printer: type {def.Name} has {def.FieldTypes.Length} field types but {def.FieldNames.Length} names");

            if (def.IsInterface)
            {
                // An interface has no fields but slots. The index is the contract.
                sb.Append($"interface {new TypeId(i)} {def.Name} {{\n");
                for (var m = 0; m < def.MethodSlots.Length; m++)
                    sb.Append($"  #{m.ToString(CultureInfo.InvariantCulture)} {def.MethodSlots[m]}\n");
                sb.Append("}\n");
                continue;
            }

            // "struct" rather than "type", so the dump shows whether a binding copies.
            sb.Append($"{(def.IsStruct ? "struct" : "type")} {new TypeId(i)} {def.Name} {{\n");
            for (var f = 0; f < def.FieldTypes.Length; f++)
                sb.Append($"  {new FieldId(f)} {def.FieldNames[f]}: {TypeStr(def.FieldTypes[f])}\n");
            sb.Append("}\n");
        }
    }

    /// <summary>The global slots at the head, like the types: the index is what stands in the
    /// instruction stream, the name stands here only.</summary>
    private static void WriteGlobals(StringBuilder sb, IReadOnlyList<IrGlobal> globals)
    {
        for (var i = 0; i < globals.Count; i++)
            sb.Append($"global {new GlobalId(i)} {globals[i].Name}: {TypeStr(globals[i].Type)}\n");
    }

    /// <summary>The vtable rows. They form a block of their own, because they belong to no single type
    /// but to a pair.</summary>
    private static void WriteImpls(StringBuilder sb, IReadOnlyList<IrImpl> impls)
    {
        foreach (var impl in impls)
            sb.Append($"impl {impl.Type} :: {impl.Interface} " +
                      $"[{string.Join(", ", impl.Methods)}]\n");
    }

    // --- structure ---
    private static void WriteFunction(StringBuilder sb, IrFunction func, CallContext ctx)
    {
        sb.Append($"fn {func.Name} -> {TypeStr(func.ReturnType)} {{\n");
        sb.Append($"  params: {func.ParamCount.ToString(CultureInfo.InvariantCulture)}\n");
        sb.Append("  locals:\n");
        foreach (var loc in func.Locals)
            sb.Append($"    {loc.Id} {loc.Name}: {TypeStr(loc.Type)}\n");
        // The protected regions come before the blocks: reading an unwind bug, the first thing wanted
        // is which range is covered by whom.
        if (func.Handlers.Count > 0)
        {
            sb.Append("  handlers:\n");
            foreach (var h in func.Handlers)
                sb.Append($"    [{h.Start}, {h.End}) " +
                          (h.Kind == IrHandlerKind.Finally
                              ? $"finally -> {h.Handler}\n"
                              : $"catch {(h.CatchType is { } t ? t.ToString() : "*")} " +
                                $"-> {h.Handler}{(h.Slot is { } s2 ? $" into {s2}" : "")}\n"));
        }
        foreach (var block in func.Blocks)
            WriteBlock(sb, block, ctx);
        sb.Append("}\n");
    }

    private static void WriteBlock(StringBuilder sb, IrBlock block, CallContext ctx)
    {
        sb.Append($"  {block.Id}:\n");
        foreach (var op in block.Insts)
            sb.Append($"    {OpStr(op, ctx)}\n");
        if (block.Terminator is null)
            throw new InternalCompilationException($"ir-printer: block {block.Id} has no terminator");
        sb.Append($"    {TermStr(block.Terminator)}\n");
    }

    // --- instructions ---
    private static string OpStr(IrOp op, CallContext ctx) => op switch
    {
        Const c => $"{c.Dest}: {TypeStr(c.Type)} = const {ConstStr(c.Value)}",
        BinOp b => $"{b.Dest}: {TypeStr(b.Type)} = {IrNames.Bin(b.Kind)} {b.Lhs}, {b.Rhs}",
        UnOp u => $"{u.Dest}: {TypeStr(u.Type)} = {IrNames.Un(u.Kind)} {u.Operand}",
        Convert cv => $"{cv.Dest}: {TypeStr(cv.To)} = convert {TypeStr(cv.From)} {cv.Operand}",
        LoadLocal l => $"{l.Dest}: {TypeStr(l.Type)} = load {l.Local}",
        StoreLocal s => $"store {s.Local}, {s.Value}",
        Call k => CallStr(k, ctx),
        CallImport k => CallImportStr(k, ctx),
        NewObject n => $"{n.Dest}: {TypeStr(n.Result)} = newobj {n.Type}",
        LoadField f => $"{f.Dest}: {TypeStr(f.FieldType)} = loadfield {f.Object}, {f.Type}{f.Field}",
        StoreField f => $"storefield {f.Object}, {f.Type}{f.Field}, {f.Value}",

        NewArray a => $"{a.Dest}: {TypeStr(new IrArrayType(a.Element))} = newarr " +
                      $"{TypeStr(a.Element)} [{string.Join(", ", a.Elements)}]",
        LoadElem e => $"{e.Dest}: {TypeStr(e.Element)} = loadelem {e.Array}, {e.Index}",
        StoreElem e => $"storeelem {e.Array}, {e.Index}, {e.Value}",
        ArrayLen a => $"{a.Dest}: i64 = arraylen {a.Array}",
        ArrayConcat c => $"{c.Dest}: {TypeStr(new IrArrayType(c.Element))} = arrcat {c.Left}, {c.Right}",
        ArrayRepeat r => $"{r.Dest}: {TypeStr(new IrArrayType(r.Element))} = arrrep {r.Array}, {r.Count}",

        OptNone n => $"{n.Dest}: {TypeStr(new IrOptionalType(n.Inner))} = optnone",
        OptSome s => $"{s.Dest}: {TypeStr(new IrOptionalType(s.Inner))} = optsome {s.Value}",
        OptIsSome i => $"{i.Dest}: bool = optissome {i.Option}",
        OptGet g => $"{g.Dest}: {TypeStr(g.Inner)} = optget {g.Option}",

        NewVariant v => $"{v.Dest}: {TypeStr(new IrEnumType(v.Enum))} = newvariant {v.Variant}" +
                        $" [{string.Join(", ", v.Fields)}]",
        EnumTag t => $"{t.Dest}: i64 = enumtag {t.Value}",
        EnumAs a => $"{a.Dest}: {TypeStr(new IrRefType(a.Variant))} = enumas {a.Value}, {a.Variant}",

        MakeInterface m => $"{m.Dest}: {TypeStr(new IrInterfaceType(m.Interface))} = mkiface " +
                           $"{m.Value}, {m.Concrete}",
        CallVirt c => CallVirtStr(c),
        StructCopy c => $"{c.Dest}: {TypeStr(new IrStructType(c.Type))} = structcopy {c.Value}",
        LoadGlobal l => $"{l.Dest}: {TypeStr(l.Type)} = ldglobal {l.Global}",
        MakeClosure m => $"{m.Dest}: {TypeStr(m.Type)} = mkclosure {m.Target}" +
                         (m.Environment is { } env ? $", {env}" : " (no captures)"),
        CallIndirect c => (c.Dest is { } d ? $"{d}: {TypeStr(c.ReturnType)} = " : "") +
                          $"callind {c.Callee}({string.Join(", ", c.Args)})",
        StoreGlobal g => $"stglobal {g.Global}, {g.Value}",
        _ => throw new InternalCompilationException($"ir-printer: unhandled op {op.GetType().Name}")
    };

    /// <summary>Shows the interface and the slot rather than a method name: the slot is what stands in
    /// the bytecode.</summary>
    private static string CallVirtStr(CallVirt c)
    {
        var args = string.Join(", ", c.Args);
        var target = $"{c.Interface}#{c.Slot.ToString(CultureInfo.InvariantCulture)}";
        return c.Dest is { } dest
            ? $"{dest}: {TypeStr(c.ReturnType)} = callvirt {target}({args})"
            : $"callvirt {target}({args})";
    }

    private static string CallStr(Call k, CallContext ctx)
    {
        var args = string.Join(", ", k.Args);
        var target = ctx.NameOf(k.Target);
        if (k.Dest is not { } dest)
            return $"call {target}({args})";
        var ret = ctx.ReturnTypeOf(k.Target);
        return $"{dest}: {(ret is null ? "?" : TypeStr(ret))} = call {target}({args})";
    }

    /// <summary>Native calls show the SYMBOLIC NAME: it is what gets bound at load time.</summary>
    private static string CallImportStr(CallImport k, CallContext ctx)
    {
        var args = string.Join(", ", k.Args);
        var import = ctx.ImportOf(k.Target);
        var name = import?.Name ?? k.Target.ToString();

        if (k.Dest is not { } dest) return $"callimport {name}({args})";
        var ret = import?.ReturnType;
        return $"{dest}: {(ret is null ? "?" : TypeStr(ret))} = callimport {name}({args})";
    }

    private static string TermStr(IrTerminator term) => term switch
    {
        Return r => r.Value is { } v ? $"ret {v}" : "ret",
        Branch b => $"br {b.Target}",
        CondBranch c => $"condbr {c.Cond} -> {c.IfTrue}, {c.IfFalse}",
        Unreachable => "unreachable",
        Throw t => $"throw {t.Value}{(t.Concrete is { } c ? $", {c}" : "")}",
        EndFinally => "endfinally",
        _ => throw new InternalCompilationException($"ir-printer: unhandled terminator {term.GetType().Name}")
    };

    // --- formatting helpers ---
    // Internal: the ImportTable keys per-signature imports by this display, so the one type
    // spelling stays the one there is.
    internal static string TypeStr(IrType t) => t switch
    {
        IrScalarType s => IrNames.Scalar(s.Kind),
        IrRefType r => $"&{r.Type}",
        IrArrayType a => $"{TypeStr(a.Element)}[]",
        IrOptionalType o => $"?{TypeStr(o.Inner)}",
        IrEnumType e => $"enum {e.Type}",
        IrInterfaceType i => $"dyn {i.Type}",
        IrStructType v => $"val {v.Type}",
        IrHostType h => $"host {h.Name}",
        IrFunctionType f => $"fn({string.Join(", ", f.Parameters.Select(TypeStr))}) -> {TypeStr(f.Return)}",
        _ => throw new InternalCompilationException($"ir-printer: type not printable: {t.GetType().Name}")
    };

    private static string ConstStr(IrConstValue v) => v switch
    {
        IntConst i => i.Value.ToString(CultureInfo.InvariantCulture),
        FloatConst f => Floats.Render(f.Value),
        BoolConst b => b.Value ? "true" : "false",
        CharConst c => c.CodePoint.ToString(CultureInfo.InvariantCulture),
        StringConst s => Quote(s.Value),
        _ => throw new InternalCompilationException($"ir-printer: unhandled const {v.GetType().Name}")
    };

    // Escaping as in AstDumper.Quote; keep them consistent so string snapshots do not drift.
    private static string Quote(string s)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
