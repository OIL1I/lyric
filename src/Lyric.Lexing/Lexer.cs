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
    private char Peek => _pos + 1 < _source.Length ? _source[_pos + 1] : '\0';

    private bool IsWhitespace(char c) => (c == ' ' || c == '\t' || c == '\r' || c == '\n');
    private bool IsIdentifierStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
    private bool IsIdentifierCont(char c) => IsIdentifierStart(c) || c is >= '0' and <= '9';


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

        if (IsIdentifierStart(Current))
        {
            return ScanIdentifier(_pos);
        }

        else if (Current == '(')
        {
            _pos++;
            return new Token(TokenKind.LParen, new Span(_file, _pos - 1, _pos));
        }

        else if (Current == ')')
        {
            _pos++;
            return new Token(TokenKind.RParen, new Span(_file, _pos - 1, _pos));
        }

        else if (Current == '{')
        {
            _pos++;
            return new Token(TokenKind.LBrace, new Span(_file, _pos - 1, _pos));
        }

        else if (Current == '}')
        {
            _pos++;
            return new Token(TokenKind.RBrace, new Span(_file, _pos - 1, _pos));
        }
        else
        {
            Span span = new(_file, _pos, _pos + 1);
            ReportBadCharacter(Current, span);
            _pos++;
            return new Token(TokenKind.BadChar, span);
        }
    }

    private void SkipTrivia()
    {
        while (true)
        {
            var blockCommentCounter = 0;
            while (IsWhitespace(Current))
            {
                _pos++;
            }

            if (Current == '/' && Peek == '/')
            {
                _pos += 2; // Consume '//'
                while (Current != '\n' && Current != '\0')
                {
                    _pos++;
                }
                continue;
            }
            // if (Current == '/' && Peek == '*')
            // {
            //     _pos += 2; // Consume '/*'
            //     blockCommentCounter++;
            //     while (blockCommentCounter > 0 && Current != '\0')
            //     {
            //         if (Current == '/' && Peek == '*') blockCommentCounter++;
            //         else if (Current == '*' && Peek == '/')
            //         {
            //             _pos += 2; //Consume '*/'
            //             blockCommentCounter--;
            //             continue;
            //         }
            //
            //         _pos++;
            //     }
            //
            //     continue;
            // }

            break;
        }
    }

    private Token ScanIdentifier(int identifierStart)
    {
        while (IsIdentifierCont(Current))
        {
            _pos++;
        }

        Span span = new(_file, identifierStart, _pos);
        return new Token(TokenKind.Identifier, span);
    }

    private void ReportBadCharacter(char badChar, Span span)
    {
        var message = (badChar >= 0x20)
            ? $"unexpected character '{badChar}'"
            : $"unexpected character U+{(int)badChar:x4}";
        _diagnostics.Report(new Diagnostic("LYR-LEX0001", Severity.Error, span, message));
    }
}