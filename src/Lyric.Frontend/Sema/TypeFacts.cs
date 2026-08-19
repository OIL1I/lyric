using Lyric.Resolver;
using System.Text;

namespace Lyric.Sema;

/// <summary>Classification, display and convertibility of types.</summary>
public static class TypeFacts
{
    /// <summary>
    /// An integer in the sense of the arithmetic rules, <c>char</c> INCLUDED.
    ///
    /// <para>A <c>char</c> is a Unicode code point and therefore a number; it counts as numeric, or
    /// <c>std.string</c> would have to descend into the host for "is this a digit?". The price is
    /// paid in the VM: every operation that PRODUCES a <c>char</c> checks the value range.</para>
    ///
    /// <para>The same question is answered a second time in <c>IrVerifier.IsInteger</c>, on
    /// <c>IrType</c> instead of <c>LyrType</c>, because the verifier has to check bytecode without
    /// the sema. A type added here has to be added there too, or the verifier rejects what the sema
    /// allows.</para>
    /// </summary>
    public static bool IsInteger(LyrType t) => t is PrimitiveType p && p.Kind is
        PrimitiveKind.Int or PrimitiveKind.Uint
        or PrimitiveKind.Int8 or PrimitiveKind.Int16 or PrimitiveKind.Int32 or PrimitiveKind.Int64
        or PrimitiveKind.Uint8 or PrimitiveKind.Uint16 or PrimitiveKind.Uint32 or PrimitiveKind.Uint64
        or PrimitiveKind.Char;

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

    /// <summary>Does an integer literal, possibly negated, fit into the target type?</summary>
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

        // 'c + 1' and 'let c: char = 65'. The bound is not that of an integer type but that of
        // Unicode, and it lives in Lyric.Core, because the VM applies the same rule to computed
        // results. What passes here is what the VM may produce.
        //
        // A literal is thereby rejected AT COMPILE TIME where the runtime would otherwise have to
        // panic: 'let c: char = 0xD800' is a type error, not a crash.
        PrimitiveKind.Char => !negative && magnitude <= long.MaxValue
                              && Core.Unicode.IsCodepoint((long)magnitude),

        _ => false
    };

    /// <summary>
    /// The <see cref="TypeSymbol"/> behind a named type; <c>null</c> when there is none — a scalar,
    /// an array, a function type.
    ///
    /// <para>A named type appears in two forms: <see cref="NamedRef"/> for <c>Box</c> and
    /// <see cref="GenericInstance"/> for <c>Box&lt;int&gt;</c>. Nearly every question asked of it —
    /// which kind, which conformance, which field — has the same answer for both, and handling them
    /// separately means forgetting the second one.</para>
    /// </summary>
    public static TypeSymbol? SymbolOf(LyrType type) => type switch
    {
        NamedRef named => named.Symbol,
        GenericInstance instance => instance.Definition,
        _ => null,
    };

    /// <summary>The kind of a named type: class, struct, enum or interface. <c>null</c> when it is
    /// not a named type.</summary>
    public static TypeSymbolKind? KindOf(LyrType type) => SymbolOf(type)?.Kind;

    /// <summary>Is this a named type of that kind? The case most callers need, instances
    /// included.</summary>
    public static bool Is(LyrType type, TypeSymbolKind kind) => KindOf(type) == kind;

    /// <summary>Is this a named type of ONE of these kinds?</summary>
    public static bool IsAny(LyrType type, params TypeSymbolKind[] kinds) =>
        KindOf(type) is { } actual && Array.IndexOf(kinds, actual) >= 0;

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
            case OpaqueRef o: return o.Symbol.Name;
            case TypeParamType tp: return tp.Param.Name;
            case GenericInstance gi: return gi.Definition.Name + "<" + string.Join(", ", gi.Arguments.Select(Display)) + ">";
            case Optional o: return "?" + Display(o.Inner);
            // A function type as an element type MUST be parenthesized: 'fn(int) -> void[]' would
            // otherwise read as a function returning 'void[]'. Without the parenthesis the sema
            // reported "cannot assign 'fn(int) -> void[]' to '(fn(int) -> void)[]'" — two displays
            // for types that ARE different but looked the same.
            case ArrayOf { Element: FnType } fnArray:
                return $"({Display(fnArray.Element)})" + (fnArray.Size is null ? "[]" : $"[{fnArray.Size}]");
            case ArrayOf a: return Display(a.Element) + (a.Size is null ? "[]" : $"[{a.Size}]");
            case TupleOf tu: return "(" + string.Join(", ", tu.Elements.Select(Display)) + ")";
            case FnType f: return "fn(" + string.Join(", ", f.Parameters.Select(Display)) + ") -> " + Display(f.Return);
            case RangeOf r: return "range<" + Display(r.Element) + ">";
            case CoroutineOf co: return "Coroutine<" + Display(co.Yield) + ">";
            case NullType: return "null";
            case NeverType: return "never";
            case ErrorType: return "<error>";
            default: return "<?>";
        }
    }
}
