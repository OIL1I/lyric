using System.Globalization;
using System.Text;

namespace Lyric.Core;

/// <summary>
/// Resolution of escape sequences.
///
/// <para>Two stages need it: the parser for string and char literals, and the f-string lowering
/// for the text between the holes, which the parser stores raw.</para>
///
/// <para>An invalid sequence is skipped rather than reported: the function is pure and knows
/// neither span nor diagnostic engine.</para>
/// </summary>
public static class Escapes
{
    // Code points rather than character literals, so this file contains no escape sequences of
    // its own.
    private const char Newline = (char)0x0A;
    private const char CarriageReturn = (char)0x0D;
    private const char Tab = (char)0x09;
    private const char Backslash = (char)0x5C;
    private const char DoubleQuote = (char)0x22;
    private const char SingleQuote = (char)0x27;
    private const char Nul = (char)0x00;

    public static string Resolve(string content)
    {
        // Without a backslash there is nothing to do; this path allocates nothing.
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

                // Unknown sequence: the character stays, the backslash is dropped.
                default: result.Append(content[i]); i++; break;
            }
        }
        return result.ToString();
    }

    /// <summary><c>xHH</c> — exactly two hex digits. <paramref name="i"/> points at the 'x'.
    /// </summary>
    private static int HexEscape(string content, int i, StringBuilder result)
    {
        if (i + 3 <= content.Length
            && int.TryParse(content.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out var value))
            result.Append((char)value);

        return Math.Min(i + 3, content.Length); // auch im Fehlerfall weiterlaufen
    }

    /// <summary><c>u{H…}</c> — any number of hex digits in braces. <paramref name="i"/> points at
    /// the 'u'.</summary>
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
