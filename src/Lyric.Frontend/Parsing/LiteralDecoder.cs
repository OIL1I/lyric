using Lyric.AST;
using Lyric.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lyric.Parsing
{
    public static class LiteralDecoder
    {
        private static Dictionary<string, IntSuffix?> _intSuffixes = new Dictionary<string, IntSuffix?>() 
        { 
            { "i8", IntSuffix.I8 },{ "u8", IntSuffix.U8 },
            { "i16", IntSuffix.I16 },{ "u16", IntSuffix.U16 },
            { "i32", IntSuffix.I32 },{ "u32", IntSuffix.U32 },
            { "i64", IntSuffix.I64 },{ "u64", IntSuffix.U64 }
        };

        private static Dictionary<string, FloatSuffix?> _floatSuffixes = new Dictionary<string, FloatSuffix?>()
        {
            {"f32", FloatSuffix.F32 }, { "f64", FloatSuffix.F64 }
        };

        private static bool isValidDigit(char digit, ulong inBase) //inBase is only needed for 2, 8, 10 and 16
            => inBase switch
            {
                2 => digit is '0' or '1',
                8 => digit is '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7',
                10 => digit is '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9',
                16 => digit is '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9' or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'),
                _ => false
            };
        private static int digitValue(char digit) => digit switch
        {
            >= '0' and <= '9' => digit - '0',
            >= 'A' and <= 'F' => 10 + (digit - 'A'),
            >= 'a' and <= 'f' => 10 + (digit - 'a'),
            _ => 0
        };

        // The resolution lives in Lyric.Core: the f-string lowering needs it too, and Lyric.Ir
        // must not reference Lyric.Parsing.
        private static string ResolveEscapes(string content) => Escapes.Resolve(content);

        private static string StripQuotes(string text) //From lexer at least -> "\"..." (maybe unterminated)
        {
            if (String.IsNullOrEmpty(text)) return "";
            var end = text.Length >= 2 && text[0].Equals(text[^1]) ? text.Length-1 : text.Length;
            return text[1..end];
        }

        public static (ulong, IntSuffix?) DecodeInt(ReadOnlySpan<char> lexme, Span span, DiagnosticEngine de)
        {
            IntSuffix? suffix = null;
            ulong value = 0;
            var start = 0;
            ulong numBase = 10;

            if (lexme.Length > 2) //look for prefix
            {
                if (lexme[0] == '0')
                {
                    if (lexme[1] is 'x' or 'X' ) { start = 2; numBase = 16; }
                    if (lexme[1] is 'o' or 'O') { start = 2; numBase = 8; }
                    if (lexme[1] is 'b' or 'B') { start = 2; numBase = 2; }
                }
            }

            var suffixStart = start;
            while (suffixStart < lexme.Length && (isValidDigit(lexme[suffixStart], numBase) || lexme[suffixStart] == '_'))
            {
                suffixStart++;
            }
            if (suffixStart < lexme.Length)
            {
                var sufText = lexme[suffixStart..lexme.Length].ToString();
                _intSuffixes.TryGetValue(sufText, out suffix);
                if (suffix == null) de.Report("LYR-PAR0006", Severity.Error, span, $"invalid integer suffix: '{sufText}'");
            }

            foreach (char c in lexme[start..suffixStart])
            {
                if (c == '_') continue;
                ulong d = (ulong)digitValue(c);
                if (value > (ulong.MaxValue - d) / numBase)
                {
                    de.Report("LYR-PAR0007", Severity.Error, span, "integer literal too large");
                    return (0, suffix);
                }
                value = value * numBase + d;
            }
            return (value, suffix);
        }

        public static (double, FloatSuffix?) DecodeFloat(ReadOnlySpan<char> lexme, Span span, DiagnosticEngine de)
        {
            var ls = lexme.ToString().Replace("_", ""); //strip seperator
            var fidx = ls.IndexOf('f');
            FloatSuffix? suffix = null;
            if (fidx > 0)
            {
                _floatSuffixes.TryGetValue(ls[fidx..], out suffix);
                if (suffix == null) de.Report("LYR-PAR0006", Severity.Error, span, $"invalid float suffix: '{ls[fidx..]}'");
                ls = ls[0..fidx];
            }
            double.TryParse(ls, CultureInfo.InvariantCulture, out var value);
            if (double.IsInfinity(value))
            {
                de.Report("LYR-PAR0007", Severity.Error, span, "float literal too large");
                return (0.0d, suffix);
            }
            return (value, suffix);
        }

        public static string DecodeString(ReadOnlySpan<char> lexme, Span span, DiagnosticEngine de)
        {
            return ResolveEscapes(StripQuotes(lexme.ToString()));
        }

        public static int DecodeChar(ReadOnlySpan<char> lexme, Span span, DiagnosticEngine de)
        {
            var s = ResolveEscapes(StripQuotes(lexme.ToString()));
            return string.IsNullOrEmpty(s) ? 0 : Char.ConvertToUtf32(s, 0);
        }
    }
}
