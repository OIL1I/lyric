using System.Globalization;

namespace Lyric.Core;

/// <summary>
/// How a floating-point value is written as text — once, for the whole toolchain.
///
/// <para>The shortest decimal string that reads back as the same value: .NET's round-trip format
/// answers that, and every consumer here wants exactly it. What .NET spells differently from every
/// other language is the exponent MARKER — <c>1E+21</c> where C, Go, Python, JavaScript and Rust
/// all write <c>1e+21</c> — so the marker is lowercased on the way out.</para>
///
/// <para>It matters beyond taste because <c>std.string.fromFloat</c> backs the f-string lowering:
/// its output is program output, compared byte for byte by the conformance suite. The rendering is
/// therefore contract (spec §11.6), and a second runtime has one sentence to implement rather than
/// one library's habits to guess.</para>
/// </summary>
public static class Floats
{
    /// <summary>Scientific notation appears exactly where the round-trip form needs it: below
    /// <c>1e-4</c> and from <c>1e17</c> upwards. Non-finite values are <c>Infinity</c>,
    /// <c>-Infinity</c> and <c>NaN</c>; negative zero keeps its sign.</summary>
    public static string Render(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('E', StringComparison.Ordinal) ? text.Replace('E', 'e') : text;
    }

    /// <summary>The same for single precision, whose shortest form has fewer digits: <c>0.1f32</c>
    /// is <c>0.1</c>, not the <c>0.10000000149011612</c> its widening would show.</summary>
    public static string Render(float value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('E', StringComparison.Ordinal) ? text.Replace('E', 'e') : text;
    }
}
