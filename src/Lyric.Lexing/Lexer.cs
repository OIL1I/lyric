using Lyric.Core;

namespace Lyric.Lexing;

public sealed class Lexer
{

    private enum SuffixCategory
    {
        None, Invalid, Int, Float
    }
    
    private readonly SourceManager _sources;
    private readonly DiagnosticEngine _diagnostics;
    private readonly FileId _file;
    private readonly string _source;
    private int _pos;


    private char Current => _pos < _source.Length ? _source[_pos] : '\0';
    private char PeekAt(int offset) => _pos + offset < _source.Length ? _source[_pos + offset] : '\0';
    private static bool IsWhitespace(char c) => (c == ' ' || c == '\t' || c == '\r' || c == '\n');
    private static bool IsIdentifierStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
    private static bool IsIdentifierCont(char c) => IsIdentifierStart(c) || c is >= '0' and <= '9';

    private static bool IsHexDigit(char c) =>
        (c is >= '0' and <= '9') || (c is >= 'a' and <= 'f') || (c is >= 'A' and <= 'F');

    private static bool IsDecDigit(char c) => c is >= '0' and <= '9';
    private static bool IsOctalDigit(char c) => c is >= '0' and <= '7';
    private static bool IsBinaryDigit(char c) => c is '0' or '1';

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

    private static readonly HashSet<string> ValidIntSuffixes =
        ["i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64"];

    private static readonly HashSet<string> ValidFloatSuffixes =
        ["f32", "f64"];


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

        if (IsDecDigit(Current))
        {
            return ScanNumber(_pos);
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

    private Token ScanNumber(int numberStart)
    {
        var next = PeekAt(1);
        if (Current == '0' && (next == 'x' || next == 'X'))
            return ScanHexLiteral(numberStart);
        if (Current == '0' && (next == 'o' || next == 'O'))
            return ScanOctLiteral(numberStart);

        if (Current == '0' && (next == 'b' || next == 'B'))
            return ScanBinLiteral(numberStart);
        return ScanDecLiteral(numberStart);
    }

    private Token ScanHexLiteral(int numberStart)
    {
        return ScanNonDecLiteral(numberStart, IsHexDigit);
    }

    private Token ScanOctLiteral(int numberStart)
    {
        return ScanNonDecLiteral(numberStart, IsOctalDigit);
    }

    private Token ScanBinLiteral(int numberStart)
    {
        return ScanNonDecLiteral(numberStart, IsBinaryDigit);
    }

    private Token ScanNonDecLiteral(int numberStart, Func<char, bool> digitCheck)
    {
        _pos += 2; // Consume '0x' or '0X'
        if (Current == '_')
        {
            _pos++;
            while (digitCheck(Current)) _pos++;
            _diagnostics.Report(new Diagnostic("LYR-LEX0005", Severity.Error,
                new Span(_file, numberStart, _pos), "numeric literal separator '_' is not allowed to follow after a prefix"));
            return new Token(TokenKind.IntLiteral, new Span(_file, numberStart, _pos));
        }
        if (!digitCheck(Current))
        {
            _diagnostics.Report(new Diagnostic("LYR-LEX0004", Severity.Error,
                new Span(_file, numberStart, _pos), "empty integer literal after prefix"));
            return new Token(TokenKind.IntLiteral, new Span(_file, numberStart, _pos));
        }

        while (digitCheck(Current) || Current == '_')
        {
            _pos++;
        }

        switch (TryReadSuffix(out var suffixSpan))
        {
            case SuffixCategory.Invalid:
            case SuffixCategory.Float:
                var message = $"invalid suffix '{_source.Substring(suffixSpan.Start, suffixSpan.Length)}' on prefixed integer literal";
                _diagnostics.Report(new Diagnostic("LYR-LEX0003", Severity.Error, new Span(_file, numberStart, _pos), message));
                return new Token(TokenKind.IntLiteral, new Span(_file, numberStart, _pos));
            default:
                return new Token(TokenKind.IntLiteral, new Span(_file, numberStart, _pos));
        }
    }
    
    private Token ScanDecLiteral(int numberStart)
    {
        var isFloat = false;
        while (IsDecDigit(Current) || Current == '_') _pos++;
        if (Current == '.' && IsDecDigit(PeekAt(1)))
        {
            _pos++; //Consume '.'
            while (IsDecDigit(Current) || Current == '_') _pos++;
            isFloat = true;
        }
        if (Current is 'e' or 'E')
        {
            _pos++; //Consume 'e' or 'E'
            if (Current == '+' || Current == '-')
            {
                _pos++; //Consume '+' or '-'
            }
            if (!IsDecDigit(Current))
            {
                _diagnostics.Report(new Diagnostic("LYR-LEX0006", Severity.Error,
                    new Span(_file, numberStart, _pos), "expected exponent part to be a decimal number"));
                return new Token(TokenKind.IntLiteral, new Span(_file, numberStart, _pos));
            }

            while (IsDecDigit(Current) || Current == '_') _pos++;
            isFloat = true;
        }

        var message = "";
        switch (TryReadSuffix(out var span))
        {
            case SuffixCategory.Invalid:
                message = $"invalid suffix '{_source.Substring(span.Start, span.Length)}' on decimal literal";
                _diagnostics.Report(new Diagnostic("LYR-LEX0003", Severity.Error, new Span(_file, numberStart, _pos), message));
                return new Token(TokenKind.IntLiteral, new Span(_file, numberStart, _pos));
            case SuffixCategory.Int:
                if (isFloat)
                {
                    message = $"integer suffix '{_source.Substring(span.Start, span.Length)}' is not allowed on float literal";
                    _diagnostics.Report(new Diagnostic("LYR-LEX0003", Severity.Error,
                        new Span(_file, numberStart, _pos), message));
                    return new Token(TokenKind.FloatLiteral, new Span(_file, numberStart, _pos));
                }
                return new Token(TokenKind.IntLiteral, new Span(_file, numberStart, _pos));
            case SuffixCategory.Float:
                return new Token(TokenKind.FloatLiteral, new Span(_file, numberStart, _pos));
            default:
                var tk = isFloat ? TokenKind.FloatLiteral : TokenKind.IntLiteral;
                return new Token(tk, new Span(_file, numberStart, _pos));
        }
    }

    private SuffixCategory TryReadSuffix(out Span suffixSpan)
    {
        var start = _pos;
        if (Current is not ('i' or  'f' or 'u'))
        {
            suffixSpan = new Span(_file, start, _pos);
            return SuffixCategory.None;
        }
        _pos++;
        while (IsDecDigit(Current))
        {
            _pos++;
        }
        var suffix = _source.Substring(start, _pos - start);
        if (ValidIntSuffixes.Contains(suffix))
        {
            suffixSpan = new Span(_file, start, _pos);
            return SuffixCategory.Int;
        }

        if (ValidFloatSuffixes.Contains(suffix))
        {
            suffixSpan = new Span(_file, start, _pos);
            return SuffixCategory.Float;
        }
        suffixSpan = new Span(_file, start, _pos);
        return SuffixCategory.Invalid;
    }

    private void ReportBadCharacter(char badChar, Span span)
    {
        var message = (badChar >= 0x20)
            ? $"unexpected character '{badChar}'"
            : $"unexpected character U+{(int)badChar:x4}";
        _diagnostics.Report(new Diagnostic("LYR-LEX0001", Severity.Error, span, message));
    }
}