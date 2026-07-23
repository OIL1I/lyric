using System.Text;

namespace Lyric.Sema;

/// <summary>Klassifikation, Anzeige und Konvertierbarkeit von Typen (Sprache.md §4).</summary>
public static class TypeFacts
{
    public static bool IsInteger(LyrType t) => t is PrimitiveType p && p.Kind is
        PrimitiveKind.Int or PrimitiveKind.Uint
        or PrimitiveKind.Int8 or PrimitiveKind.Int16 or PrimitiveKind.Int32 or PrimitiveKind.Int64
        or PrimitiveKind.Uint8 or PrimitiveKind.Uint16 or PrimitiveKind.Uint32 or PrimitiveKind.Uint64;

    public static bool IsFloat(LyrType t) => t is PrimitiveType p && p.Kind is
        PrimitiveKind.Float or PrimitiveKind.Float32 or PrimitiveKind.Float64;

    public static bool IsNumeric(LyrType t) => IsInteger(t) || IsFloat(t);
    public static bool IsBool(LyrType t) => t is PrimitiveType { Kind: PrimitiveKind.Bool };
    public static bool IsString(LyrType t) => t is PrimitiveType { Kind: PrimitiveKind.String };
    public static bool IsVoid(LyrType t) => t is PrimitiveType { Kind: PrimitiveKind.Void };

    private static readonly Dictionary<string, PrimitiveKind> Builtins = new()
    {
        ["int"] = PrimitiveKind.Int, ["uint"] = PrimitiveKind.Uint, ["float"] = PrimitiveKind.Float,
        ["int8"] = PrimitiveKind.Int8, ["int16"] = PrimitiveKind.Int16, ["int32"] = PrimitiveKind.Int32, ["int64"] = PrimitiveKind.Int64,
        ["uint8"] = PrimitiveKind.Uint8, ["uint16"] = PrimitiveKind.Uint16, ["uint32"] = PrimitiveKind.Uint32, ["uint64"] = PrimitiveKind.Uint64,
        ["float32"] = PrimitiveKind.Float32, ["float64"] = PrimitiveKind.Float64,
        ["bool"] = PrimitiveKind.Bool, ["char"] = PrimitiveKind.Char, ["string"] = PrimitiveKind.String, ["void"] = PrimitiveKind.Void
    };

    public static LyrType? FromBuiltinName(string name) =>
        Builtins.TryGetValue(name, out var kind) ? new PrimitiveType(kind) : null;

    /// <summary>Passt ein (ggf. negiertes) Ganzzahl-Literal in den Zieltyp? (Entscheidung ②a)</summary>
    public static bool IntLiteralFits(bool negative, ulong magnitude, PrimitiveKind target) => target switch
    {
        PrimitiveKind.Int8 => negative ? magnitude <= 128 : magnitude <= 127,
        PrimitiveKind.Int16 => negative ? magnitude <= 32768 : magnitude <= 32767,
        PrimitiveKind.Int32 => negative ? magnitude <= 2147483648 : magnitude <= 2147483647,
        PrimitiveKind.Int64 or PrimitiveKind.Int => negative ? magnitude <= 9223372036854775808 : magnitude <= 9223372036854775807,
        PrimitiveKind.Uint8 => !negative && magnitude <= 255,
        PrimitiveKind.Uint16 => !negative && magnitude <= 65535,
        PrimitiveKind.Uint32 => !negative && magnitude <= 4294967295,
        PrimitiveKind.Uint64 or PrimitiveKind.Uint => !negative,
        _ => false
    };

    public static string Display(LyrType t)
    {
        switch (t)
        {
            case PrimitiveType p: return p.Kind switch
            {
                PrimitiveKind.Int => "int", PrimitiveKind.Uint => "uint", PrimitiveKind.Float => "float",
                PrimitiveKind.Int8 => "int8", PrimitiveKind.Int16 => "int16", PrimitiveKind.Int32 => "int32", PrimitiveKind.Int64 => "int64",
                PrimitiveKind.Uint8 => "uint8", PrimitiveKind.Uint16 => "uint16", PrimitiveKind.Uint32 => "uint32", PrimitiveKind.Uint64 => "uint64",
                PrimitiveKind.Float32 => "float32", PrimitiveKind.Float64 => "float64",
                PrimitiveKind.Bool => "bool", PrimitiveKind.Char => "char", PrimitiveKind.String => "string", PrimitiveKind.Void => "void",
                _ => "?"
            };
            case NamedRef n: return n.Symbol.Name;
            case TypeParamType tp: return tp.Param.Name;
            case GenericInstance gi: return gi.Definition.Name + "<" + string.Join(", ", gi.Arguments.Select(Display)) + ">";
            case Optional o: return "?" + Display(o.Inner);
            case ArrayOf a: return Display(a.Element) + (a.Size is null ? "[]" : $"[{a.Size}]");
            case TupleOf tu: return "(" + string.Join(", ", tu.Elements.Select(Display)) + ")";
            case FnType f: return "fn(" + string.Join(", ", f.Parameters.Select(Display)) + ") -> " + Display(f.Return);
            case RangeOf r: return "range<" + Display(r.Element) + ">";
            case NullType: return "null";
            case NeverType: return "never";
            case ErrorType: return "<error>";
            default: return "<?>";
        }
    }
}
