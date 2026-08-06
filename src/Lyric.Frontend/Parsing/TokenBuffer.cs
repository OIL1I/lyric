using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Parsing
{
    /// <summary>
    /// Eager-Token-Puffer: zieht den kompletten Token-Stream (inkl. f-String-Sub
    /// -Tokens) beim Bau aus dem Lexer und stellt dem Parser Lookahead
    /// (<see cref="Peek"/>) sowie die '&gt;&gt;'-Zerlegung für verschachtelte
    /// Generics (<see cref="SplitCurrentGreater"/>) bereit. DocComments werden
    /// verworfen (Semantik ist post-v1).
    /// </summary>
    public sealed class TokenBuffer
    {
        private readonly FileId _id;
        private readonly DiagnosticEngine _de;
        private readonly List<Token> _buffer = [];
        private int _pos = 0;

        public TokenBuffer(SourceManager sm, FileId id, DiagnosticEngine de)
        {
            _id = id;
            _de = de;

            var lexer = new Lexer(sm, id, de);
            var current = lexer.Next();
            while (current.TokenKind != TokenKind.Eof)
            {
                if (current.TokenKind is not TokenKind.DocComment)
                    _buffer.Add(current);
                current = lexer.Next();
            }
            _buffer.Add(current); //Add Eof
        }

        public Token Peek(int offset = 0)
        {
            if (_pos + offset >= _buffer.Count) return _buffer.Last(); // return Eof
            return _buffer[_pos + offset];
        }

        public Token Current => _buffer[_pos];

        /// <summary>Aktueller Lese-Index. Für Fortschritts-Guards in Recovery-Schleifen.</summary>
        public int Position => _pos;

        public Token Advance()
        {
            var c = Current;
            if (c.TokenKind != TokenKind.Eof)
                _pos++;
            return c;
        }

        public bool Check(TokenKind kind) => kind == Current.TokenKind;

        public bool Match(TokenKind kind)
        {
            if (Check(kind))
            {
                Advance();
                return true;
            }
            return false;
        }

        public Token Expect(TokenKind kind, string code, string message)
        {
            var c = Current;
            if (!Check(kind))
            {
                _de.Report(new Diagnostic(code, Severity.Error, Current.Span, message));
                return c;
            }
            return Advance();
        }

        public bool AtEnd => Current.TokenKind == TokenKind.Eof;

        public void SplitCurrentGreater()
        {
            var start = Current.Span.Start;
            var end = Current.Span.End;

            if (Current.TokenKind == TokenKind.Shr)
            {
                var span1 = new Span(_id, start, end - 1);
                var span2 = new Span(_id, start + 1, end);
                var gr1 = new Token(TokenKind.Greater, span1);
                var gr2 = new Token(TokenKind.Greater, span2);

                _buffer[_pos] = gr1;
                _buffer.Insert(_pos + 1, gr2);
                return;
            }
            else if (Current.TokenKind == TokenKind.GreaterEqual)
            {
                var span1 = new Span(_id, start, end - 1);
                var span2 = new Span(_id, start + 1, end);
                var gr = new Token(TokenKind.Greater, span1);
                var eq = new Token(TokenKind.Equal, span2);

                _buffer[_pos] = gr;
                _buffer.Insert(_pos + 1, eq);
                return;
            }
            else if (Current.TokenKind == TokenKind.ShrEqual)
            {
                var span1 = new Span(_id, start, end - 2);
                var span2 = new Span(_id, start + 1, end - 1);
                var span3 = new Span(_id, start + 2, end);
                var gr1 = new Token(TokenKind.Greater, span1);
                var gr2 = new Token(TokenKind.Greater, span2);
                var eq = new Token(TokenKind.Equal, span3);
                _buffer[_pos] = gr1;
                _buffer.InsertRange(_pos + 1, [gr2, eq]);
                return;
            }
        }
    }
}
