using System.Globalization;
using System.Text;

namespace Lyric.Core;

/// <summary>
/// Auflösung von Escape-Sequenzen (Sprache.md §1.5).
///
/// <para>Liegt in <c>Lyric.Core</c>, weil zwei Stufen sie brauchen: der Parser für String- und
/// Char-Literale, und das f-String-Lowering für die Textstücke zwischen den Löchern. Der Parser
/// speichert diese Stücke roh (siehe <c>InterpText</c>), also muss das Lowering sie auflösen — und
/// <c>Lyric.Ir</c> darf <c>Lyric.Parsing</c> nicht referenzieren, das wäre eine Schichtenumkehr.</para>
///
/// <para>Ungültige Sequenzen werden <b>übersprungen, nicht gemeldet</b>: die Funktion ist rein und
/// kennt weder Span noch DiagnosticEngine. Das entspricht dem bisherigen Verhalten des Parsers.</para>
/// </summary>
public static class Escapes
{
    // Als Codepunkte statt als Zeichen-Literale: eine Datei über Escape-Sequenzen, die selbst
    // voller Escape-Sequenzen ist, liest sich schlecht und lädt zu Tippfehlern ein.
    private const char Newline = (char)0x0A;
    private const char CarriageReturn = (char)0x0D;
    private const char Tab = (char)0x09;
    private const char Backslash = (char)0x5C;
    private const char DoubleQuote = (char)0x22;
    private const char SingleQuote = (char)0x27;
    private const char Nul = (char)0x00;

    public static string Resolve(string content)
    {
        // Ohne Backslash gibt es nichts zu tun — der häufige Fall, und er soll nichts allozieren.
        if (content.IndexOf(Backslash) < 0) return content;

        var result = new StringBuilder(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            if (content[i] != Backslash)
            {
                result.Append(content[i]);
                i++;
                continue;
            }

            i++; // den Backslash konsumieren
            if (i >= content.Length) break;

            switch (content[i])
            {
                case 'n': result.Append(Newline); i++; break;
                case 'r': result.Append(CarriageReturn); i++; break;
                case 't': result.Append(Tab); i++; break;
                case '0': result.Append(Nul); i++; break;
                case 'x': i = HexEscape(content, i, result); break;
                case 'u': i = UnicodeEscape(content, i, result); break;

                case var c when c == Backslash: result.Append(Backslash); i++; break;
                case var c when c == DoubleQuote: result.Append(DoubleQuote); i++; break;
                case var c when c == SingleQuote: result.Append(SingleQuote); i++; break;

                // Unbekannte Sequenz: das Zeichen bleibt stehen, der Backslash fällt weg.
                default: result.Append(content[i]); i++; break;
            }
        }
        return result.ToString();
    }

    /// <summary><c>xHH</c> — genau zwei Hex-Ziffern. <paramref name="i"/> zeigt auf das 'x'.</summary>
    private static int HexEscape(string content, int i, StringBuilder result)
    {
        if (i + 3 <= content.Length
            && int.TryParse(content.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out var value))
            result.Append((char)value);

        return Math.Min(i + 3, content.Length); // auch im Fehlerfall weiterlaufen
    }

    /// <summary><c>u{H…}</c> — beliebig viele Hex-Ziffern in geschweiften Klammern.
    /// <paramref name="i"/> zeigt auf das 'u'.</summary>
    private static int UnicodeEscape(string content, int i, StringBuilder result)
    {
        var start = Math.Min(i + 2, content.Length); // 'u{' überspringen
        var end = content.IndexOf('}', start);
        if (end < 0) end = content.Length;

        if (int.TryParse(content.AsSpan(start, end - start), NumberStyles.HexNumber, null,
                out var codePoint)
            && codePoint <= 0x10FFFF && codePoint is < 0xD800 or > 0xDFFF)
            result.Append(char.ConvertFromUtf32(codePoint));

        return Math.Min(end + 1, content.Length);
    }
}
