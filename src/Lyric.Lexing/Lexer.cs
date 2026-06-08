using Lyric.Core;

namespace Lyric.Lexing;

public sealed class Lexer
{
    private readonly SourceManager _sources;
    private readonly DiagnosticEngine _diagnostics;
    private readonly FileId _file;
    private readonly string _source;
    private int _pos;


    private char Current => _pos < _source.Length ? _source[_pos] : '\0';
    private char PeekAt(int offset) => _pos + offset < _source.Length ? _source[_pos + offset] : '\0';
    private bool IsWhitespace(char c) => (c == ' ' || c == '\t' || c == '\r' || c == '\n');
    private bool IsIdentifierStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
    private bool IsIdentifierCont(char c) => IsIdentifierStart(c) || c is >= '0' and <= '9';


    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        { "module", TokenKind.Module },
        { "import", TokenKind.Import },
        { "as", TokenKind.As },
        { "pub", TokenKind.Pub },

        { "struct", TokenKind.Struct },
        { "class", TokenKind.Class },
        { "enum", TokenKind.Enum },
        { "interface", TokenKind.Interface },
        { "extend", TokenKind.Extend },

        { "fn", TokenKind.Fn },
        { "mut", TokenKind.Mut },
        { "let", TokenKind.Let },
        { "var", TokenKind.Var },
        { "params", TokenKind.Params },

        { "if", TokenKind.If },
        { "else", TokenKind.Else },
        { "while", TokenKind.While },
        { "do", TokenKind.Do },
        { "for", TokenKind.For },
        { "in", TokenKind.In },
        { "match", TokenKind.Match },

        { "break", TokenKind.Break },
        { "continue", TokenKind.Continue },
        { "return", TokenKind.Return },
        { "yield", TokenKind.Yield },
        { "resume", TokenKind.Resume },
        { "defer", TokenKind.Defer },

        { "try", TokenKind.Try },
        { "catch", TokenKind.Catch },
        { "throw", TokenKind.Throw },

        { "true", TokenKind.True },
        { "false", TokenKind.False },
        { "null", TokenKind.Null },

        { "this", TokenKind.This }
    };


    public Lexer(SourceManager pSourceManager, FileId fileId, DiagnosticEngine pDiagnosticEngine)
    {
        _sources = pSourceManager ?? throw new ArgumentNullException(nameof(pSourceManager));
        _diagnostics = pDiagnosticEngine ?? throw new ArgumentNullException(nameof(pDiagnosticEngine));
        _file = fileId;
        _source = _sources.GetText(_file);
        _pos = 0;
    }

    public Token Next()
    {
        SkipTrivia();
        if (Current == '\0')
        {
            return new Token(TokenKind.Eof, new Span(_file, _pos, _pos));
        }

        if (Current == '/' && PeekAt(1) == '/' && PeekAt(2) == '/')
        {
            return ScanDocComment(_pos);
        }

        if (IsIdentifierStart(Current))
        {
            return ScanIdentifier(_pos);
        }

        if (Current == '(')
        {
            _pos++;
            return new Token(TokenKind.LParen, new Span(_file, _pos - 1, _pos));
        }

        if (Current == ')')
        {
            _pos++;
            return new Token(TokenKind.RParen, new Span(_file, _pos - 1, _pos));
        }

        if (Current == '{')
        {
            _pos++;
            return new Token(TokenKind.LBrace, new Span(_file, _pos - 1, _pos));
        }

        if (Current == '}')
        {
            _pos++;
            return new Token(TokenKind.RBrace, new Span(_file, _pos - 1, _pos));
        }

        Span span = new(_file, _pos, _pos + 1);
        ReportBadCharacter(Current, span);
        _pos++;
        return new Token(TokenKind.BadChar, span);
    }

    private void SkipTrivia()
    {
        while (true)
        {
            while (IsWhitespace(Current))
            {
                _pos++;
            }

            if (Current == '/' && PeekAt(1) == '/' && PeekAt(2) != '/')
            {
                _pos += 2; // Consume '//'
                while (Current != '\n' && Current != '\0')
                {
                    _pos++;
                }

                continue;
            }

            if (Current == '/' && PeekAt(1) == '*')
            {
                var commentStart = _pos;
                _pos += 2;
                var depth = 1;
                while (depth > 0 && Current != '\0')
                {
                    if (Current == '/' && PeekAt(1) == '*')
                    {
                        _pos += 2;
                        depth++;
                    }
                    else if (Current == '*' && PeekAt(1) == '/')
                    {
                        _pos += 2;
                        depth--;
                    }
                    else
                    {
                        _pos++;
                    }
                }

                if (depth > 0)
                {
                    _diagnostics.Report(new Diagnostic("LYR-LEX0002", Severity.Error,
                        new Span(_file, commentStart, _pos), "unterminated block comment"));
                }

                continue;
            }

            break;
        }
    }

    private Token ScanIdentifier(int identifierStart)
    {
        while (IsIdentifierCont(Current))
        {
            _pos++;
        }

        var lexme = _source.Substring(identifierStart, _pos - identifierStart);
        if (Keywords.TryGetValue(lexme, out var kind))
            return new Token(kind, new Span(_file, identifierStart, _pos));

        return new Token(TokenKind.Identifier, new Span(_file, identifierStart, _pos));
    }

    private Token ScanDocComment(int start)
    {
        _pos += 3; //Consume '///'
        while (Current != '\n' && Current != '\0')
        {
            _pos++;
        }

        return new Token(TokenKind.DocComment, new Span(_file, start, _pos));
    }

    private void ReportBadCharacter(char badChar, Span span)
    {
        var message = (badChar >= 0x20)
            ? $"unexpected character '{badChar}'"
            : $"unexpected character U+{(int)badChar:x4}";
        _diagnostics.Report(new Diagnostic("LYR-LEX0001", Severity.Error, span, message));
    }
}