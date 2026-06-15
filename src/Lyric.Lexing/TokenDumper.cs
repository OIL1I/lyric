using System.Text;
using Lyric.Core;

namespace Lyric.Lexing;

public static class TokenDumper
{
    public static string Dump(IEnumerable<Token> tokens, SourceManager sources)
    {
        StringBuilder sb = new();
        foreach (var token in tokens)
        {
            sb.Append($"[{token.Span.Start}..{token.Span.End}) ");
            sb.Append(token.TokenKind.ToString());
            sb.Append
            (
                token.TokenKind == TokenKind.Eof ? "" : $" '{ToEscaped(sources.Slice(token.Span))}'"
            );
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ToEscaped(ReadOnlySpan<char> s)
    {
        StringBuilder sb = new();
        
        foreach (var c in s)
        {
            if (c == '\'')
                sb.Append("\\'");
            else if (c == '\\')
                sb.Append("\\\\");
            else if (c == '\n')
                sb.Append("\\n");
            else if (c == '\r')
                sb.Append("\\r");
            else if (c == '\t')
                sb.Append("\\t");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}