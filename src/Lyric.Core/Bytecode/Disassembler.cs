using System.Globalization;
using System.Text;
using Lyric.Core;

namespace Lyric.Bytecode;

/// <summary>
/// Text output of a <see cref="BytecodeModule"/> — <c>lyric disasm</c>.
///
/// <para>Kept close to the <c>IrPrinter</c> format (block labels <c>bb0:</c>, the same mnemonics
/// and type names) so a disassembly and an IR dump can be compared side by side.</para>
///
/// <para>The newline is always <c>'\n'</c>, never <c>AppendLine</c>, so snapshots match across
/// platforms.</para>
/// </summary>
public static class Disassembler
{
    /// <summary>
    /// Disassembles the whole module, or with <paramref name="onlyFunction"/> a single function
    /// together with the module header.
    ///
    /// <para>The header stays when filtering: the instructions reference strings, types and
    /// imports by index.</para>
    ///
    /// <para>An unknown name returns <c>null</c>; the caller turns that into a diagnostic rather
    /// than printing nothing.</para>
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

        for (var g = 0; g < module.Globals.Count; g++)
            sb.Append($"  global g{N(g)}: {TypeName(module, module.Globals[g])}\n");

        if (module.GlobalInit is { } globalInit)
            sb.Append($"  globalinit: {CalleeName(module, globalInit)}\n");

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

        // The vtable rows, printed with the header because callvirt refers to them.
        foreach (var impl in module.Impls)
            sb.Append($"  impl {TypeRefName(module, (ulong)impl.Type)} :: " +
                      $"{TypeRefName(module, (ulong)impl.Interface)} [" +
                      $"{string.Join(", ", impl.Methods.Select(m => CalleeName(module, m)))}]\n");

        foreach (var import in module.Imports)
            sb.Append($"  import {import.Name}(" +
                      $"{string.Join(", ", import.ParamTypes.Select(p => TypeName(module, p)))})" +
                      $" -> {TypeName(module, import.ReturnType)}\n");

        // The attribute rows, values by field position. The names beside them come from section
        // 12 when the module carries it, so the line reads like the source that produced it.
        foreach (var attribute in module.Attributes)
        {
            var type = module.Types[attribute.Type];
            var target = attribute.TargetKind switch
            {
                AttributeTargetKind.Function => $"fn {module.Functions[attribute.Target].Name}",
                AttributeTargetKind.Type => $"type {module.Types[attribute.Target].Name}",
                _ => "module",
            };
            var names = module.FieldNames.FirstOrDefault(n => n.Type == attribute.Type)?.Names;
            var values = attribute.Values.Select((value, i) =>
                $"{(names is not null ? names[i] + " = " : "")}{ValueText(value)}");
            sb.Append($"  attribute @{type.Name}" +
                      $"{(attribute.Values.Count > 0 ? $" {{{string.Join(", ", values)}}}" : "")}" +
                      $" -> {target}\n");
        }

        // The field names, with the opaque type beside the fields that have one (section
        // 14): 'names Holder(hero: Entity, stage)'. Written out here because a trace nobody
        // can see is one nobody can check.
        foreach (var entry in module.FieldNames)
        {
            var opaque = module.OpaqueFields.FirstOrDefault(o => o.Type == entry.Type)?.Names;
            var fields = entry.Names.Select((name, i) =>
                opaque is not null && opaque[i].Length > 0 ? $"{name}: {opaque[i]}" : name);
            sb.Append($"  names {module.Types[entry.Type].Name}({string.Join(", ", fields)})\n");
        }

        for (var f = 0; f < module.Functions.Count; f++)
        {
            if (filter is not null && module.Functions[f].Name != filter) continue;
            sb.Append('\n');
            WriteFunction(sb, module, module.Functions[f], f);
        }

        return sb.ToString();
    }

    private static void WriteFunction(StringBuilder sb, BytecodeModule module,
        BytecodeFunction function, int index)
    {
        // Empty when the module carries no debug info, or none for this function; a slot line
        // then shows the type alone, exactly as it did before the section existed.
        var names = module.SlotNames is { } all && index < all.Count ? all[index] : [];

        sb.Append($"fn {function.Name} -> {TypeName(module, function.ReturnType)} {{\n");
        sb.Append($"  params: {N(function.ParamCount)}\n");
        sb.Append($"  maxstack: {N(function.MaxStack)}\n");
        sb.Append("  slots:\n");
        for (var i = 0; i < function.SlotTypes.Count; i++)
            sb.Append($"    l{N(i)}: {TypeName(module, function.SlotTypes[i])}" +
                      $"{(i < names.Count && names[i].Length > 0 ? $" ; {names[i]}" : "")}\n");

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

        // The fused forms print what they replace, so a disassembly reads like the four
        // instructions each stands for: 'binlk add i64 l0 = l0, 1'.
        Op.BinLocals =>
            $"binll {Mnemonic(i.Fused)} {TypeName(i.Type!.Value)} l{N((ulong)i.SlotDest)} = "
            + $"l{N((ulong)i.SlotA)}, l{N((ulong)i.SlotB)}",
        Op.BinConst =>
            $"binlk {Mnemonic(i.Fused)} {TypeName(i.Type!.Value)} l{N((ulong)i.SlotDest)} = "
            + $"l{N((ulong)i.SlotA)}, {FusedConstText(i)}",

        Op.BranchCompare =>
            $"brcmp {Mnemonic(i.Fused)} {TypeName(i.Type!.Value)} l{N((ulong)i.SlotA)}, "
            + $"l{N((ulong)i.SlotB)} -> bb{N(i.Immediate)}, bb{N(i.Immediate2)}",
        Op.BranchCompareConst =>
            $"brcmpk {Mnemonic(i.Fused)} {TypeName(i.Type!.Value)} l{N((ulong)i.SlotA)}, "
            + $"{FusedConstText(i)} -> bb{N(i.Immediate)}, bb{N(i.Immediate2)}",

        Op.NewVariant => $"newvariant {TypeRefName(module, i.Immediate)}",
        Op.StructCopy => $"structcopy {TypeRefName(module, i.Immediate)}",
        Op.LoadGlobal => $"ldglobal g{N(i.Immediate)}",
        Op.MakeClosure => $"mkclosure {CalleeName(module, (int)(i.Immediate >> 1))}" +
                          ((i.Immediate & 1) == 1 ? "" : " (no captures)"),
        Op.CallIndirect => $"callind {N(i.Immediate >> 1)}" +
                           ((i.Immediate & 1) == 1 ? "" : " (void)"),
        Op.StoreGlobal => $"stglobal g{N(i.Immediate)}",
        Op.MakeInterface => $"mkiface {TypeRefName(module, i.Immediate)} -> " +
                            $"{TypeRefName(module, i.Immediate2)}",
        Op.CallVirt => $"callvirt {SlotName(module, i.Immediate, i.Immediate2)}",
        Op.EnumTag => "enumtag",
        Op.Throw => "throw",
        Op.EndFinally => "endfinally",
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
        TypeTag.F32 or TypeTag.F64 => Floats.Render(i.FloatValue),
        TypeTag.Bool => i.BoolValue ? "true" : "false",
        TypeTag.String => $"s{N(i.Immediate)} {Quote(SafeString(module, i.Immediate))}",
        _ => N(i.Immediate),
    };

    /// <summary>The immediate of a fused constant shape. Its own renderer rather than
    /// <see cref="ConstText"/>: the bits live in a different field, because the branch targets
    /// occupy the one a <c>const</c> uses.</summary>
    private static string FusedConstText(BytecodeInstruction i) => i.Type switch
    {
        TypeTag.F32 or TypeTag.F64 => Floats.Render(i.FloatValue),
        TypeTag.Bool => i.BoolValue ? "true" : "false",
        _ => N(i.ConstBits),
    };

    private static string SafeString(BytecodeModule module, ulong index) =>
        index < (ulong)module.Strings.Count ? module.Strings[(int)index] : "<out of range>";

    private static string CalleeName(BytecodeModule module, int index)
    {
        if (index < module.Imports.Count) return module.Imports[index].Name;
        var defined = index - module.Imports.Count;
        return defined < module.Functions.Count ? module.Functions[defined].Name : $"f{N(index)}";
    }

    internal static string Mnemonic(Op opcode) => opcode switch
    {
        Op.Add => "add", Op.Sub => "sub", Op.Mul => "mul", Op.Div => "div", Op.Rem => "rem",
        Op.Shl => "shl", Op.Shr => "shr",
        Op.BitAnd => "and", Op.BitOr => "or", Op.BitXor => "xor",
        Op.Lt => "lt", Op.Le => "le", Op.Gt => "gt", Op.Ge => "ge", Op.Eq => "eq", Op.Ne => "ne",
        Op.Neg => "neg", Op.BitNot => "bitnot",
        _ => opcode.ToString().ToLowerInvariant(),
    };

    /// <summary>Prints <c>Interface#slot (name)</c>. The slot is what executes; the name is in the
    /// bytecode anyway.</summary>
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

    /// <summary>Field names are not in the bytecode, so this prints <c>Type#index</c>, which is
    /// what executes.</summary>
    private static string FieldName(BytecodeModule module, ulong type, ulong field) =>
        $"{TypeRefName(module, type)}#{N(field)}";

    /// <summary>A type in a signature position. References print the name from the type table
    /// rather than only the index.</summary>
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

    /// <summary>An attribute value the way the source wrote it: quoted strings, unsigned kinds
    /// without a sign, floats round-trippable.</summary>
    private static string ValueText(BytecodeConstValue value) => value.Tag switch
    {
        TypeTag.String => Quote(value.Text ?? ""),
        // The reader resolved the variant name beside the tag; a dump that showed the number
        // alone would make a reader count declarations to find out what it says.
        TypeTag.Enum => value.Text ?? value.AsInt.ToString(CultureInfo.InvariantCulture),
        TypeTag.Bool => value.AsBool ? "true" : "false",
        TypeTag.F32 or TypeTag.F64 => Floats.Render(value.AsFloat),
        TypeTag.Char => $"'{char.ConvertFromUtf32((int)value.Bits)}'",
        TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64 => N(value.Bits),
        _ => value.AsInt.ToString(CultureInfo.InvariantCulture),
    };

    // Escaping as in IrPrinter.Quote and AstDumper.Quote; keep the three consistent.
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
