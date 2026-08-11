using System.Globalization;
using Lyric.Bytecode;
using Lyric.Vm;

namespace Lyric.Embedding;

/// <summary>
/// Werte ueber die Host-Grenze: .NET → Lyric und zurueck (M10/E2).
///
/// <para><b>Nur Skalare und Strings.</b> Das ist dieselbe Linie, die die
/// <see cref="NativeRegistry"/> seit M6 zieht, und sie hat einen Grund: ein Objekt hat ein
/// <b>Layout</b>, und das nach aussen zu geben machte die Feldreihenfolge eines Moduls zum
/// oeffentlichen Vertrag. Dann koennte die Erreichbarkeitsanalyse nichts mehr streichen, und ein
/// Format-Bump braeche jeden Host. Host-Typen kommen in E4 ueber opake Handles — nicht ueber
/// Layout-Wissen.</para>
///
/// <para><b>Verlustfrei oder gar nicht.</b> Jede Wandlung prueft den Wertebereich des Zieltyps und
/// wirft, statt still abzuschneiden. Ein <c>long</c>, das als <c>int8</c> ankommt und dabei zu
/// <c>-1</c> wird, ist genau die Sorte Fehler, die erst drei Ebenen spaeter auffaellt. Innerhalb
/// von Lyric wickelt Arithmetik definiert um (§6.6) — aber das ist eine Rechnung des Programms,
/// keine stille Umdeutung an der Grenze.</para>
/// </summary>
internal static class Marshal
{
    /// <summary>Ein .NET-Wert als Lyric-Wert des erwarteten Typs.</summary>
    public static LyrValue ToLyric(object? value, BytecodeType expected, string what)
    {
        // Ein Host-Objekt (ADR-026) reist unveraendert: die VM sieht nur eine Referenz und fasst
        // sie nie an — genau wie bei einem 'string', der seit M6 in 'Ref' liegt. Kein Kopieren,
        // kein Wrappen; die Identitaet ist die Zusage.
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

    /// <summary>Ein Lyric-Wert als .NET-Wert vom Typ <typeparamref name="T"/>.</summary>
    public static T FromLyric<T>(LyrValue value, BytecodeType actual, string what) =>
        (T)FromLyric(value, actual, typeof(T), what)!;

    /// <summary>
    /// Wie <see cref="FromLyric{T}"/>, aber mit dem Zieltyp als Wert.
    ///
    /// <para>Gebraucht fuer Host-Funktionen (E3): deren Parametertypen stehen erst zur Laufzeit
    /// fest, und ein <c>FromLyric&lt;object&gt;</c> reichte nicht — es lieferte fuer ein
    /// <c>int32</c> ein <c>long</c>, und der Delegat erwartet ein <c>int</c>.</para>
    /// </summary>
    public static object? FromLyric(LyrValue value, BytecodeType actual, Type wanted, string what)
    {

        // 'void' hat keinen Wert. Ein Host, der trotzdem einen will, hat die Signatur falsch
        // gelesen — und ein stiller default(T) verstellte ihm den Blick darauf.
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

    /// <summary>Wie ein Typ in einer Meldung heisst — die Lyric-Schreibweise, nicht die des
    /// Tags.</summary>
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

        // Ein Host-Typ heisst so, wie der Host ihn registriert hat — mehr traegt er nicht, und
        // genau dieser Name landet in der erzeugten Deklaration.
        TypeTag.Host => type.HostName ?? "a host type",
        _ => type.Tag.ToString().ToLowerInvariant(),
    };

    private static double ToDouble(object value, string what, BytecodeType expected) => value switch
    {
        double d => d,
        float f => f,
        // Ganzzahlen sind hier erlaubt: '3' fuer einen 'float'-Parameter ist genau das, was ein
        // Host schreibt, und es ist verlustfrei. Der umgekehrte Weg — '3.5' fuer ein 'int' —
        // waere es nicht und ist unten ausgeschlossen.
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

    /// <summary>
    /// Passt der Wert in die Breite des Zieltyps?
    ///
    /// <para>Geprueft und nicht abgeschnitten: <c>300</c> als <c>int8</c> waere <c>44</c>, und das
    /// merkt niemand. Innerhalb von Lyric wickelt Arithmetik um (§6.6) — das ist eine Rechnung
    /// des Programms und etwas anderes als eine Umdeutung beim Uebergeben.</para>
    /// </summary>
    private static bool FitsIn(long value, TypeTag tag, object original) => tag switch
    {
        TypeTag.I8 => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        TypeTag.I16 => value is >= short.MinValue and <= short.MaxValue,
        TypeTag.I32 => value is >= int.MinValue and <= int.MaxValue,
        TypeTag.I64 => true,
        TypeTag.U8 => value is >= 0 and <= byte.MaxValue,
        TypeTag.U16 => value is >= 0 and <= ushort.MaxValue,
        TypeTag.U32 => value is >= 0 and <= uint.MaxValue,
        // 'uint' ist 64 Bit breit: jedes Bitmuster passt. Ein negatives 'long' waere aber ein
        // anderer Wert als der, den der Host meinte — 'ulong' geht deshalb oben unveraendert
        // durch, ein negatives Vorzeichen hier nicht.
        TypeTag.U64 => original is ulong || value >= 0,
        _ => true,
    };

    private static ScriptException Mismatch(string what, BytecodeType expected, object? value,
        string wanted) =>
        new("LYR-EMB0005",
            $"{what}: expected {wanted} for '{Describe(expected)}', got "
            + (value is null ? "null" : $"'{value.GetType().Name}'"), null);
}
