using System.Globalization;
using System.Text;

namespace Lyric.Bytecode;

/// <summary>
/// Textausgabe eines <see cref="BytecodeModule"/> — <c>lyric disasm</c>.
///
/// <para>Bewusst nah am <c>IrPrinter</c>-Format gehalten (Blocklabels <c>bb0:</c>, dieselben
/// Mnemonics und Typnamen), damit man Disassembly und IR-Dump nebeneinander legen und ohne
/// Übersetzungsarbeit vergleichen kann. Genau das macht <c>lyric lower</c> zum Debug-Werkzeug für
/// den Emitter.</para>
///
/// <para>Newline ist immer <c>'\n'</c>, nie <c>AppendLine</c> — sonst brechen die Snapshots auf
/// der Linux-CI. Dieselbe Regel wie im <c>IrPrinter</c>.</para>
/// </summary>
public static class Disassembler
{
    /// <summary>
    /// Disassembliert das ganze Modul, oder mit <paramref name="onlyFunction"/> nur eine Funktion
    /// samt Modulkopf.
    ///
    /// <para>Der Kopf bleibt auch beim Filtern stehen: die Instruktionen einer Funktion verweisen
    /// per Index auf Strings, Typen und Importe, und ohne die Tabellen davor ist die Ausgabe nicht
    /// lesbar.</para>
    ///
    /// <para>Ein unbekannter Name liefert <c>null</c> — der Aufrufer macht daraus eine Diagnose.
    /// Eine leere Ausgabe waere die schlechtere Antwort: sie sieht aus wie „die Funktion ist
    /// leer".</para>
    /// </summary>
    public static string? Dump(BytecodeModule module, string? onlyFunction)
    {
        if (onlyFunction is null) return Dump(module);
        return module.Functions.Any(f => f.Name == onlyFunction)
            ? Render(module, filter: onlyFunction)
            : null;
    }

    public static string Dump(BytecodeModule module) => Render(module, filter: null);

    private static string Render(BytecodeModule module, string? filter)
    {
        var sb = new StringBuilder();

        sb.Append($"module (format {N(module.VersionMajor)}.{N(module.VersionMinor)})\n");
        sb.Append($"  capabilities: 0x{module.Capabilities:x}\n");

        if (module.Strings.Count > 0)
        {
            sb.Append("  strings:\n");
            for (var i = 0; i < module.Strings.Count; i++)
                sb.Append($"    s{N(i)} {Quote(module.Strings[i])}\n");
        }

        if (module.Start is { } start)
            sb.Append($"  start: {CalleeName(module, start)}\n");

        foreach (var type in module.Types)
            sb.Append(type.IsInterface
                ? $"  interface {type.Name} {{{string.Join(", ", type.MethodSlots)}}}\n"
                : type.IsStruct
                ? $"  struct {type.Name}({string.Join(", ", type.FieldTypes.Select(f => TypeName(module, f)))})\n"
                : type.IsEnum
                ? $"  enum {type.Name} {{{string.Join(", ", type.Variants.Select(v => TypeRefName(module, (ulong)v)))}}}\n"
                : $"  type {type.Name}({string.Join(", ", type.FieldTypes.Select(f => TypeName(module, f)))})\n");

        // Die vtable-Zeilen. Sie stehen beim Kopf, weil callvirt sie braucht, um lesbar zu sein.
        foreach (var impl in module.Impls)
            sb.Append($"  impl {TypeRefName(module, (ulong)impl.Type)} :: " +
                      $"{TypeRefName(module, (ulong)impl.Interface)} [" +
                      $"{string.Join(", ", impl.Methods.Select(m => CalleeName(module, m)))}]\n");

        foreach (var import in module.Imports)
            sb.Append($"  import {import.Name}(" +
                      $"{string.Join(", ", import.ParamTypes.Select(p => TypeName(module, p)))})" +
                      $" -> {TypeName(module, import.ReturnType)}\n");

        foreach (var function in module.Functions)
        {
            if (filter is not null && function.Name != filter) continue;
            sb.Append('\n');
            WriteFunction(sb, module, function);
        }

        return sb.ToString();
    }

    private static void WriteFunction(StringBuilder sb, BytecodeModule module, BytecodeFunction function)
    {
        sb.Append($"fn {function.Name} -> {TypeName(module, function.ReturnType)} {{\n");
        sb.Append($"  params: {N(function.ParamCount)}\n");
        sb.Append($"  maxstack: {N(function.MaxStack)}\n");
        sb.Append("  slots:\n");
        for (var i = 0; i < function.SlotTypes.Count; i++)
            sb.Append($"    l{N(i)}: {TypeName(module, function.SlotTypes[i])}\n");

        var instructions = CodeDecoder.Decode(function.Code);
        var blockAt = new Dictionary<int, int>();
        for (var i = 0; i < function.BlockOffsets.Count; i++) blockAt[function.BlockOffsets[i]] = i;

        foreach (var instruction in instructions)
        {
            if (blockAt.TryGetValue(instruction.Offset, out var block))
                sb.Append($"  bb{N(block)}:\n");
            sb.Append($"    {Format(module, instruction)}\n");
        }

        sb.Append("}\n");
    }

    private static string Format(BytecodeModule module, BytecodeInstruction i) => i.Opcode switch
    {
        Op.Const => $"const {TypeName(i.Type!.Value)} {ConstText(module, i)}",
        Op.LoadLocal => $"ldloc {N(i.Immediate)}",
        Op.StoreLocal => $"stloc {N(i.Immediate)}",
        Op.Pop => "pop",

        Op.Convert => $"conv {TypeName(i.Type!.Value)} -> {TypeName(i.ToType!.Value)}",
        Op.Not => "not",

        Op.Call => $"call {CalleeName(module, (int)i.Immediate)}",
        Op.Return => "ret",
        Op.ReturnValue => "retval",
        Op.Branch => $"br bb{N(i.Immediate)}",
        Op.CondBranch => $"condbr bb{N(i.Immediate)}, bb{N(i.Immediate2)}",
        Op.Unreachable => "unreachable",

        Op.NewVariant => $"newvariant {TypeRefName(module, i.Immediate)}",
        Op.StructCopy => $"structcopy {TypeRefName(module, i.Immediate)}",
        Op.MakeInterface => $"mkiface {TypeRefName(module, i.Immediate)} -> " +
                            $"{TypeRefName(module, i.Immediate2)}",
        Op.CallVirt => $"callvirt {SlotName(module, i.Immediate, i.Immediate2)}",
        Op.EnumTag => "enumtag",
        Op.EnumAs => $"enumas {TypeRefName(module, i.Immediate)}",

        Op.OptNone => "optnone",
        Op.OptSome => "optsome",
        Op.OptIsSome => "optissome",
        Op.OptGet => "optget",

        Op.NewArray => $"newarr {N(i.Immediate)}",
        Op.LoadElem => "ldelem",
        Op.StoreElem => "stelem",
        Op.ArrayLen => "arrlen",
        Op.ArrayConcat => "arrcat",
        Op.ArrayRepeat => "arrrep",

        Op.NewObject => $"newobj {TypeRefName(module, i.Immediate)}",
        Op.LoadField => $"ldfld {FieldName(module, i.Immediate, i.Immediate2)}",
        Op.StoreField => $"stfld {FieldName(module, i.Immediate, i.Immediate2)}",

        _ => $"{Mnemonic(i.Opcode)} {TypeName(i.Type!.Value)}",
    };

    private static string ConstText(BytecodeModule module, BytecodeInstruction i) => i.Type switch
    {
        TypeTag.F32 or TypeTag.F64 => i.FloatValue.ToString("R", CultureInfo.InvariantCulture),
        TypeTag.Bool => i.BoolValue ? "true" : "false",
        TypeTag.String => $"s{N(i.Immediate)} {Quote(SafeString(module, i.Immediate))}",
        _ => N(i.Immediate),
    };

    private static string SafeString(BytecodeModule module, ulong index) =>
        index < (ulong)module.Strings.Count ? module.Strings[(int)index] : "<out of range>";

    private static string CalleeName(BytecodeModule module, int index)
    {
        if (index < module.Imports.Count) return module.Imports[index].Name;
        var defined = index - module.Imports.Count;
        return defined < module.Functions.Count ? module.Functions[defined].Name : $"f{N(index)}";
    }

    private static string Mnemonic(Op opcode) => opcode switch
    {
        Op.Add => "add", Op.Sub => "sub", Op.Mul => "mul", Op.Div => "div", Op.Rem => "rem",
        Op.Shl => "shl", Op.Shr => "shr",
        Op.BitAnd => "and", Op.BitOr => "or", Op.BitXor => "xor",
        Op.Lt => "lt", Op.Le => "le", Op.Gt => "gt", Op.Ge => "ge", Op.Eq => "eq", Op.Ne => "ne",
        Op.Neg => "neg", Op.BitNot => "bitnot",
        _ => opcode.ToString().ToLowerInvariant(),
    };

    /// <summary>Zeigt <c>Interface#slot (name)</c>. Der Slot ist das, was ausgefuehrt wird; der
    /// Name steht daneben, weil er im Bytecode ohnehin vorliegt und die Zeile ohne ihn kaum
    /// lesbar waere.</summary>
    private static string SlotName(BytecodeModule module, ulong iface, ulong slot)
    {
        var name = TypeRefName(module, iface);
        if (iface >= (ulong)module.Types.Count) return $"{name}#{N(slot)}";

        var slots = module.Types[(int)iface].MethodSlots;
        return slot < (ulong)slots.Count
            ? $"{name}#{N(slot)} ({slots[(int)slot]})"
            : $"{name}#{N(slot)}";
    }

    private static string TypeRefName(BytecodeModule module, ulong index) =>
        index < (ulong)module.Types.Count ? module.Types[(int)index].Name : $"ty{N(index)}";

    /// <summary>Feldnamen stehen nicht im Bytecode (Bytecode.md §2). Der Disassembler zeigt deshalb
    /// <c>Typ#index</c> — ehrlicher als ein erfundener Name, und es ist genau das, was ausgeführt
    /// wird.</summary>
    private static string FieldName(BytecodeModule module, ulong type, ulong field) =>
        $"{TypeRefName(module, type)}#{N(field)}";

    /// <summary>Ein Typ an einer Signaturstelle. Referenzen zeigen den Namen aus der Typ-Tabelle
    /// statt nur den Index — der Disassembler wird gelesen, nicht ausgeführt.</summary>
    private static string TypeName(BytecodeModule module, BytecodeType type) =>
        type.IsArray && type.Element is { } el ? $"{TypeName(module, el)}[]"
        : type.IsOptional && type.Element is { } opt ? $"?{TypeName(module, opt)}"
        : type.IsRef && type.TypeIndex >= 0 && type.TypeIndex < module.Types.Count
            ? $"&{module.Types[type.TypeIndex].Name}"
        : type.IsRef ? $"&ty{N(type.TypeIndex)}"
        : TypeName(type.Tag);

    private static string TypeName(TypeTag tag) => tag switch
    {
        TypeTag.I8 => "i8", TypeTag.I16 => "i16", TypeTag.I32 => "i32", TypeTag.I64 => "i64",
        TypeTag.U8 => "u8", TypeTag.U16 => "u16", TypeTag.U32 => "u32", TypeTag.U64 => "u64",
        TypeTag.F32 => "f32", TypeTag.F64 => "f64",
        TypeTag.Bool => "bool", TypeTag.Char => "char", TypeTag.String => "string",
        TypeTag.Void => "void",
        _ => tag.ToString().ToLowerInvariant(),
    };

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string N(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    // Escaping wie IrPrinter.Quote / AstDumper.Quote — konsistent halten.
    private static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
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
        return sb.Append('"').ToString();
    }
}
