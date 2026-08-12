using System.Globalization;
using Lyric.Bytecode;
using Lyric.Vm;

namespace Lyric.Embedding;

/// <summary>
/// Values across the host boundary: .NET to Lyric and back.
///
/// <para>Scalars, strings and registered host types cross. A Lyric object does not: its field
/// layout stays internal.</para>
///
/// <para>Every conversion checks the range of the target type and throws rather than truncating.
/// </para>
/// </summary>
internal static class Marshal
{
    /// <summary>A .NET value as a Lyric value of the expected type.</summary>
    public static LyrValue ToLyric(object? value, BytecodeType expected, string what)
    {
        // A host object travels unchanged: the VM holds the reference and never touches it. No
        // copy, no wrapper; identity is preserved.
        if (expected.Tag == TypeTag.Host)
        {
            if (value is null)
                throw Mismatch(what, expected, null, $"a '{expected.HostName}'");
            return LyrValue.FromHostObject(value);
        }

        if (expected.Tag == TypeTag.String)
        {
            if (value is string text) return LyrValue.FromString(text);
            throw Mismatch(what, expected, value, "a string");
        }

        if (value is null)
            throw Mismatch(what, expected, null, "a value (null crosses the boundary only for '?T', which E2 does not marshal)");

        return expected.Tag switch
        {
            TypeTag.Bool => value is bool b
                ? LyrValue.FromBool(b)
                : throw Mismatch(what, expected, value, "a bool"),

            TypeTag.Char => value is char c
                ? LyrValue.FromI64(c)
                : throw Mismatch(what, expected, value, "a char"),

            TypeTag.F32 => LyrValue.FromF32((float)ToDouble(value, what, expected)),
            TypeTag.F64 => LyrValue.FromF64(ToDouble(value, what, expected)),

            TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64
                or TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64
                => LyrValue.FromI64(ToInteger(value, expected.Tag, what, expected)),

            _ => throw new ScriptException("LYR-EMB0001",
                $"{what}: '{Describe(expected)}' cannot cross the host boundary yet — E2 marshals "
                + "scalars and strings only", null),
        };
    }

    /// <summary>A Lyric value as a .NET value of type <typeparamref name="T"/>.</summary>
    public static T FromLyric<T>(LyrValue value, BytecodeType actual, string what) =>
        (T)FromLyric(value, actual, typeof(T), what)!;

    /// <summary>
    /// As <see cref="FromLyric{T}"/>, but with the target type passed as a value.
    ///
    /// <para>Used for host functions, whose parameter types are only known at runtime.</para>
    /// </summary>
    public static object? FromLyric(LyrValue value, BytecodeType actual, Type wanted, string what)
    {

        // 'void' has no value; returning default(T) would hide a misread signature.
        if (actual.Tag == TypeTag.Void)
        {
            if (wanted == typeof(object) || Nullable.GetUnderlyingType(wanted) is not null)
                return default!;
            throw new ScriptException("LYR-EMB0002",
                $"{what}: the function returns 'void' — there is no value of type "
                + $"'{wanted.Name}' to give back", null);
        }

        if (actual.Tag == TypeTag.Host)
        {
            var host = value.Ref;
            if (host is not null && wanted.IsInstanceOfType(host)) return host;
            throw new ScriptException("LYR-EMB0003",
                $"{what}: the value is host type '{actual.HostName}', which is not a "
                + $"'{wanted.Name}'", null);
        }

        object boxed = actual.Tag switch
        {
            TypeTag.String => value.AsString,
            TypeTag.Bool => value.AsBool,
            TypeTag.Char => (char)value.AsI64,
            TypeTag.F32 => value.AsF32,
            TypeTag.F64 => value.AsF64,
            TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64 => value.AsU64,
            _ => value.AsI64,
        };

        if (wanted.IsInstanceOfType(boxed)) return boxed;

        try
        {
            return Convert.ChangeType(boxed, wanted, CultureInfo.InvariantCulture);
        }
        catch (Exception cause) when (cause is InvalidCastException or OverflowException
                                          or FormatException)
        {
            throw new ScriptException("LYR-EMB0003",
                $"{what}: the function returns '{Describe(actual)}', which does not fit "
                + $"'{wanted.Name}'", cause);
        }
    }

    /// <summary>How a type is named in a message: the Lyric spelling, not the tag's.</summary>
    public static string Describe(BytecodeType type) => type.Tag switch
    {
        TypeTag.Void => "void",
        TypeTag.Bool => "bool",
        TypeTag.Char => "char",
        TypeTag.String => "string",
        TypeTag.F32 => "float32",
        TypeTag.F64 => "float",
        TypeTag.I8 => "int8",
        TypeTag.I16 => "int16",
        TypeTag.I32 => "int32",
        TypeTag.I64 => "int",
        TypeTag.U8 => "uint8",
        TypeTag.U16 => "uint16",
        TypeTag.U32 => "uint32",
        TypeTag.U64 => "uint",
        TypeTag.Array => "an array",
        TypeTag.Optional => "an optional",
        TypeTag.Ref or TypeTag.Enum => "an object",

        // A host type is named as the host registered it; that name is what the generated
        // declaration carries.
        TypeTag.Host => type.HostName ?? "a host type",
        _ => type.Tag.ToString().ToLowerInvariant(),
    };

    private static double ToDouble(object value, string what, BytecodeType expected) => value switch
    {
        double d => d,
        float f => f,
        // Integers are accepted for a float parameter: the widening is lossless. The reverse is
        // rejected below.
        sbyte or byte or short or ushort or int or uint or long
            => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => throw Mismatch(what, expected, value, "a number"),
    };

    private static long ToInteger(object value, TypeTag tag, string what, BytecodeType expected)
    {
        if (value is double or float or decimal)
            throw Mismatch(what, expected, value,
                "an integer (a fractional value would silently lose its fraction)");

        long asLong;
        try
        {
            asLong = value switch
            {
                ulong u when tag is TypeTag.U64 => unchecked((long)u),
                sbyte or byte or short or ushort or int or uint or long or ulong
                    => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                _ => throw Mismatch(what, expected, value, "an integer"),
            };
        }
        catch (OverflowException cause)
        {
            throw new ScriptException("LYR-EMB0004",
                $"{what}: {value} does not fit '{Describe(expected)}'", cause);
        }

        if (!FitsIn(asLong, tag, value))
            throw new ScriptException("LYR-EMB0004",
                $"{what}: {value} does not fit '{Describe(expected)}'", null);

        return asLong;
    }

    /// <summary>Does the value fit the width of the target type? Checked, not truncated:
    /// <c>300</c> as <c>int8</c> would be <c>44</c>.</summary>
    private static bool FitsIn(long value, TypeTag tag, object original) => tag switch
    {
        TypeTag.I8 => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        TypeTag.I16 => value is >= short.MinValue and <= short.MaxValue,
        TypeTag.I32 => value is >= int.MinValue and <= int.MaxValue,
        TypeTag.I64 => true,
        TypeTag.U8 => value is >= 0 and <= byte.MaxValue,
        TypeTag.U16 => value is >= 0 and <= ushort.MaxValue,
        TypeTag.U32 => value is >= 0 and <= uint.MaxValue,
        // 'uint' is 64 bits wide, so every bit pattern fits; a negative 'long' would arrive as a
        // different value and is rejected.
        TypeTag.U64 => original is ulong || value >= 0,
        _ => true,
    };

    private static ScriptException Mismatch(string what, BytecodeType expected, object? value,
        string wanted) =>
        new("LYR-EMB0005",
            $"{what}: expected {wanted} for '{Describe(expected)}', got "
            + (value is null ? "null" : $"'{value.GetType().Name}'"), null);
}
