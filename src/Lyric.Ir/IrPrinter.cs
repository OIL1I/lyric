using System.Globalization;
using System.Text;
using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// Deterministischer Text-Dump eines <see cref="IrModule"/>/<see cref="IrFunction"/> für
/// Golden-Snapshots und Debug-Ausgabe (analog zu <c>AstDumper</c>). Eine Instruktion pro
/// Zeile, 2-Space-Einrückung pro Ebene, Newline immer '\n' (nie AppendLine — CRLF bräche
/// die Snapshots auf Linux-CI).
///
/// Format (siehe P2-Grammatik): der Typ steht am Dest (<c>t2: bool = lt t0, t1</c>), nicht
/// im Mnemonic — so ist jede Zeile aus den Feldern der Instruktion allein formatierbar, ohne
/// Temp-Tabellen-Lookup. Einzige Ausnahme ist <c>call</c>: Name und Rückgabetyp der Ziel-
/// Funktion liegen bei der Callee, nicht an der Call-Stelle, und werden über den
/// <see cref="CallContext"/> aufgelöst (Index → module.Functions).
///
/// Der Dump ist bewusst über <c>switch</c> statt Visitor gelöst: der <c>default</c>-Wurf
/// erzwingt Vollständigkeit, sobald eine neue Instruktion hinzukommt.
/// </summary>
public static class IrPrinter
{
    public static string Dump(IrModule module)
    {
        var sb = new StringBuilder();
        var ctx = CallContext.ForModule(module);
        WriteTypes(sb, module.Types);
        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (i > 0 || module.Types.Count > 0) sb.Append('\n'); // Leerzeile zwischen Blöcken
            WriteFunction(sb, module.Functions[i], ctx);
        }
        return sb.ToString();
    }

    /// <summary>Standalone-Dump einer einzelnen Funktion. Ohne Modul-Kontext werden
    /// Call-Ziele als roher <c>fN</c>-Index und ihr Rückgabetyp als <c>?</c> gedruckt.</summary>
    public static string Dump(IrFunction function)
    {
        var sb = new StringBuilder();
        WriteFunction(sb, function, CallContext.None);
        return sb.ToString();
    }

    // --- Call-Auflösung: FunctionId -> (Name, ReturnType) aus der Funktionsliste ---
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
    /// Die Typ-Tabelle als eigener Block am Kopf des Dumps. Feldnamen erscheinen <b>nur</b> hier —
    /// im Instruktionsstrom steht der Index, weil der Index das ist, was ausgeführt wird.
    ///
    /// <para>Das ist auch der Grund, warum <see cref="TypeStr"/> weiterhin ohne Kontext auskommt:
    /// wer <c>&amp;ty0</c> liest, schlägt einmal oben nach, statt dass jede Zeile den Typnamen
    /// wiederholt. So bleibt die Regel „jede Zeile ist aus den Feldern der Instruktion allein
    /// formatierbar" intakt.</para>
    /// </summary>
    private static void WriteTypes(StringBuilder sb, IReadOnlyList<IrTypeDef> types)
    {
        for (var i = 0; i < types.Count; i++)
        {
            var def = types[i];
            if (def.FieldTypes.Length != def.FieldNames.Length)
                throw new InternalCompilationException(
                    $"ir-printer: type {def.Name} has {def.FieldTypes.Length} field types but {def.FieldNames.Length} names");

            sb.Append($"type {new TypeId(i)} {def.Name} {{\n");
            for (var f = 0; f < def.FieldTypes.Length; f++)
                sb.Append($"  {new FieldId(f)} {def.FieldNames[f]}: {TypeStr(def.FieldTypes[f])}\n");
            sb.Append("}\n");
        }
    }

    // --- Struktur ---
    private static void WriteFunction(StringBuilder sb, IrFunction func, CallContext ctx)
    {
        sb.Append($"fn {func.Name} -> {TypeStr(func.ReturnType)} {{\n");
        sb.Append($"  params: {func.ParamCount.ToString(CultureInfo.InvariantCulture)}\n");
        sb.Append("  locals:\n");
        foreach (var loc in func.Locals)
            sb.Append($"    {loc.Id} {loc.Name}: {TypeStr(loc.Type)}\n");
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

    // --- Instruktionen ---
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
        NewObject n => $"{n.Dest}: {TypeStr(new IrRefType(n.Type))} = newobj {n.Type}",
        LoadField f => $"{f.Dest}: {TypeStr(f.FieldType)} = loadfield {f.Object}, {f.Type}{f.Field}",
        StoreField f => $"storefield {f.Object}, {f.Type}{f.Field}, {f.Value}",
        _ => throw new InternalCompilationException($"ir-printer: unhandled op {op.GetType().Name}")
    };

    private static string CallStr(Call k, CallContext ctx)
    {
        var args = string.Join(", ", k.Args);
        var target = ctx.NameOf(k.Target);
        if (k.Dest is not { } dest)
            return $"call {target}({args})";
        var ret = ctx.ReturnTypeOf(k.Target);
        return $"{dest}: {(ret is null ? "?" : TypeStr(ret))} = call {target}({args})";
    }

    /// <summary>Native Aufrufe zeigen den <b>symbolischen Namen</b> — er ist das, was beim Laden
    /// gebunden wird, und damit die Information, die man beim Lesen braucht.</summary>
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
        _ => throw new InternalCompilationException($"ir-printer: unhandled terminator {term.GetType().Name}")
    };

    // --- Formatierungs-Helfer ---
    private static string TypeStr(IrType t) => t switch
    {
        IrScalarType s => IrNames.Scalar(s.Kind),
        IrRefType r => $"&{r.Type}",
        _ => throw new InternalCompilationException($"ir-printer: type not printable: {t.GetType().Name}")
    };

    private static string ConstStr(IrConstValue v) => v switch
    {
        IntConst i => i.Value.ToString(CultureInfo.InvariantCulture),
        FloatConst f => f.Value.ToString("R", CultureInfo.InvariantCulture),
        BoolConst b => b.Value ? "true" : "false",
        CharConst c => c.CodePoint.ToString(CultureInfo.InvariantCulture),
        StringConst s => Quote(s.Value),
        _ => throw new InternalCompilationException($"ir-printer: unhandled const {v.GetType().Name}")
    };

    // Escaping wie AstDumper.Quote — konsistent halten, damit String-Snapshots nicht driften.
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
