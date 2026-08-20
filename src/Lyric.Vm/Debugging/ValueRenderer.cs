using System.Globalization;
using System.Text;
using Lyric.Bytecode;

namespace Lyric.Vm.Debugging;

/// <summary>
/// Turns a <see cref="LyrValue"/> into what a debugger displays.
///
/// <para>A value carries no type tag, so the STATIC type decides the reading — the slot type, the
/// field type, the element type. That is the same rule the interpreter lives by: the tag stands
/// in the structure, never in the value.</para>
/// </summary>
internal static class ValueRenderer
{
    internal readonly record struct Rendered(string Value, string Type, bool Expandable);

    /// <summary>One child of an expandable value, still unrendered: the controller renders it
    /// when the consumer actually expands, and hands out handles then.</summary>
    internal readonly record struct Child(string Name, BytecodeType Type, LyrValue Value);

    public static Rendered Render(BytecodeModule module, BytecodeType type, LyrValue value)
    {
        var typeName = TypeName(module, type);
        switch (type.Tag)
        {
            case TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64:
                return new Rendered(value.AsI64.ToString(CultureInfo.InvariantCulture), typeName, false);
            case TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64:
                return new Rendered(value.AsU64.ToString(CultureInfo.InvariantCulture), typeName, false);
            case TypeTag.F32:
                return new Rendered(value.AsF32.ToString("R", CultureInfo.InvariantCulture), typeName, false);
            case TypeTag.F64:
                return new Rendered(value.AsF64.ToString("R", CultureInfo.InvariantCulture), typeName, false);
            case TypeTag.Bool:
                return new Rendered(value.AsBool ? "true" : "false", typeName, false);
            case TypeTag.Char:
                return new Rendered($"'{char.ConvertFromUtf32((int)value.Bits)}'", typeName, false);
            case TypeTag.String:
                return new Rendered(Quote(value.AsString), typeName, false);

            case TypeTag.Optional:
                if (!value.IsSome) return new Rendered("null", typeName, false);
                var inner = Render(module, type.Element!, value.Unwrap());
                return inner with { Type = typeName };

            case TypeTag.Array:
            {
                if (value.Ref is not LyrValue[] elements)
                    return new Rendered("null", typeName, false);
                return new Rendered($"{TypeName(module, type.Element!)}[{elements.Length}]",
                    typeName, elements.Length > 0);
            }

            case TypeTag.Ref or TypeTag.Struct:
            {
                if (value.Ref is null) return new Rendered("null", typeName, false);
                var def = module.Types[type.TypeIndex];
                return new Rendered(def.Name, typeName, def.FieldTypes.Count > 0);
            }

            case TypeTag.Enum:
            {
                if (value.Ref is null) return new Rendered("null", typeName, false);
                var def = module.Types[type.TypeIndex];
                var tag = (int)value.AsObject[0].AsI64;
                if (tag < 0 || tag >= def.Variants.Count)
                    return new Rendered($"<tag {tag}>", typeName, false);
                var variant = module.Types[def.Variants[tag]];
                return new Rendered($"{Unqualified(def.Name)}.{Unqualified(variant.Name)}",
                    typeName, variant.FieldTypes.Count > 1);
            }

            case TypeTag.Interface:
            {
                // A fat pointer: the object plus its concrete type. Rendered as the concrete
                // value, because that is what the person is looking at.
                if (value.Ref is null) return new Rendered("null", typeName, false);
                var concrete = Concrete(module, value.ConcreteType);
                var rendered = Render(module, concrete, LyrValue.FromObject(value.AsObject));
                return rendered with { Type = $"{typeName} ({rendered.Type})" };
            }

            case TypeTag.Fn:
            {
                // The index is stored plus one, so an all-zero slot reads as "no value".
                if (value.Bits == 0 && value.Ref is null)
                    return new Rendered("null", typeName, false);
                return new Rendered($"fn {CallableName(module, value.ClosureFunction)}",
                    typeName, false);
            }

            case TypeTag.Host:
                return new Rendered(value.Ref is null ? "null" : $"<{type.HostName}>",
                    typeName, false);

            default:
                return new Rendered($"<{type.Tag}>", typeName, false);
        }
    }

    /// <summary>The children of an expandable value. The static type again decides the reading:
    /// the layout's field types, the array's element type, the variant's payload.</summary>
    public static List<Child> Children(BytecodeModule module, BytecodeType type, LyrValue value)
    {
        switch (type.Tag)
        {
            case TypeTag.Optional when value.IsSome:
                return Children(module, type.Element!, value.Unwrap());

            case TypeTag.Array when value.Ref is LyrValue[] elements:
            {
                var children = new List<Child>(elements.Length);
                for (var i = 0; i < elements.Length; i++)
                    children.Add(new Child($"[{i}]", type.Element!, elements[i]));
                return children;
            }

            case TypeTag.Ref or TypeTag.Struct when value.Ref is not null:
            {
                var def = module.Types[type.TypeIndex];
                var names = FieldNames(module, type.TypeIndex);
                var slots = value.AsObject;

                var children = new List<Child>(def.FieldTypes.Count);
                for (var i = 0; i < def.FieldTypes.Count && i < slots.Length; i++)
                    children.Add(new Child(names is not null ? names[i] : $"f{i}",
                        def.FieldTypes[i], slots[i]));
                return children;
            }

            case TypeTag.Enum when value.Ref is not null:
            {
                var def = module.Types[type.TypeIndex];
                var tag = (int)value.AsObject[0].AsI64;
                if (tag < 0 || tag >= def.Variants.Count) return [];

                var variantIndex = def.Variants[tag];
                var variant = module.Types[variantIndex];
                var names = FieldNames(module, variantIndex);
                var slots = value.AsObject;

                // Slot 0 is the tag; the payload starts at 1.
                var children = new List<Child>(variant.FieldTypes.Count - 1);
                for (var i = 1; i < variant.FieldTypes.Count && i < slots.Length; i++)
                    children.Add(new Child(names is not null ? names[i] : $"f{i - 1}",
                        variant.FieldTypes[i], slots[i]));
                return children;
            }

            case TypeTag.Interface when value.Ref is not null:
                return Children(module, Concrete(module, value.ConcreteType),
                    LyrValue.FromObject(value.AsObject));

            default:
                return [];
        }
    }

    /// <summary>The concrete type behind a fat pointer, as a signature-position type, so the
    /// enum, struct and class paths above apply unchanged.</summary>
    private static BytecodeType Concrete(BytecodeModule module, int typeIndex)
    {
        var def = module.Types[typeIndex];
        var tag = def.IsEnum ? TypeTag.Enum : def.IsStruct ? TypeTag.Struct : TypeTag.Ref;
        return new BytecodeType(tag, typeIndex);
    }

    private static IReadOnlyList<string>? FieldNames(BytecodeModule module, int typeIndex)
    {
        foreach (var entry in module.FieldNames)
            if (entry.Type == typeIndex)
                return entry.Names;
        return null;
    }

    private static string CallableName(BytecodeModule module, int index)
    {
        if (index < 0) return "?";
        if (index < module.Imports.Count) return module.Imports[index].Name;
        var at = index - module.Imports.Count;
        return at < module.Functions.Count ? module.Functions[at].Name : $"fn{index}";
    }

    /// <summary>The display name of a type, in the language's spelling: the aliases where they
    /// exist, the declared name for a composite.</summary>
    public static string TypeName(BytecodeModule module, BytecodeType type) => type.Tag switch
    {
        TypeTag.I8 => "int8", TypeTag.I16 => "int16", TypeTag.I32 => "int32", TypeTag.I64 => "int",
        TypeTag.U8 => "uint8", TypeTag.U16 => "uint16", TypeTag.U32 => "uint32", TypeTag.U64 => "uint",
        TypeTag.F32 => "float32", TypeTag.F64 => "float",
        TypeTag.Bool => "bool", TypeTag.Char => "char", TypeTag.String => "string",
        TypeTag.Void => "void",
        TypeTag.Array => $"{TypeName(module, type.Element!)}[]",
        TypeTag.Optional => $"?{TypeName(module, type.Element!)}",
        TypeTag.Ref or TypeTag.Struct or TypeTag.Enum or TypeTag.Interface =>
            type.TypeIndex >= 0 && type.TypeIndex < module.Types.Count
                ? Unqualified(module.Types[type.TypeIndex].Name)
                : $"ty{type.TypeIndex}",
        TypeTag.Fn =>
            $"fn({string.Join(", ", type.Parameters.Select(p => TypeName(module, p)))}) -> " +
            TypeName(module, type.Element!),
        TypeTag.Host => type.HostName ?? "host",
        _ => type.Tag.ToString().ToLowerInvariant(),
    };

    /// <summary>Type names in the table are qualified (<c>std.collections.List&lt;int&gt;</c> is
    /// long); the segment after the last TOP-LEVEL dot reads better beside a value. Dots inside a
    /// generic suffix do not count — they qualify an argument, not this name — and the suffix
    /// itself stays: it is part of the name the monomorphizer minted.</summary>
    private static string Unqualified(string name)
    {
        var depth = 0;
        var cut = -1;
        for (var i = 0; i < name.Length; i++)
        {
            switch (name[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case '.' when depth == 0: cut = i; break;
            }
        }
        return cut < 0 ? name : name[(cut + 1)..];
    }

    private static string Quote(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }
}
