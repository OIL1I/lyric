using System.Text;
using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Parsing
{
    /// <summary>
    /// An eager token buffer: pulls the complete token stream, f-string sub-tokens included, out of
    /// the lexer on construction and offers the parser lookahead (<see cref="Peek"/>) plus the
    /// '&gt;&gt;' split for nested generics (<see cref="SplitCurrentGreater"/>).
    ///
    /// <para>Doc comments are kept out of the token stream and collected in <see cref="DocComments"/>
    /// instead: the parser never sees them, so no production has to skip them.</para>
    /// </summary>
    public sealed class TokenBuffer
    {
        private readonly FileId _id;
        private readonly DiagnosticEngine _de;
        private readonly List<Token> _buffer = [];
        private readonly Dictionary<int, string> _docs = [];
        private int _pos = 0;

        public TokenBuffer(SourceManager sm, FileId id, DiagnosticEngine de)
        {
            _id = id;
            _de = de;

            var text = sm.GetText(id);
            var lexer = new Lexer(sm, id, de);
            var pending = new List<Span>();

            var current = lexer.Next();
            while (current.TokenKind != TokenKind.Eof)
            {
                if (current.TokenKind is TokenKind.DocComment)
                {
                    // A blank line ends a block: what stands before it belongs to nothing.
                    if (pending.Count > 0 && Separated(text, pending[^1].End, current.Span.Start))
                        pending.Clear();
                    pending.Add(current.Span);
                }
                else
                {
                    Attach(text, pending, current.Span.Start);
                    _buffer.Add(current);
                }
                current = lexer.Next();
            }

            Attach(text, pending, current.Span.Start);
            _buffer.Add(current); //Add Eof
        }

        /// <summary>
        /// The doc comment blocks of this file, keyed by the source offset of the token that follows
        /// them. A declaration span starts at its first token, so a lookup with
        /// <c>decl.Span.Start</c> finds the block written above it.
        /// </summary>
        public IReadOnlyDictionary<int, string> DocComments => _docs;

        /// <summary>
        /// Is there a blank line between the two offsets?
        ///
        /// <para>A blank line is a line break followed by a line holding nothing but whitespace, not
        /// merely a second line break: an ordinary '//' comment is no token and stays in the raw text
        /// between the two offsets, so counting breaks alone would read it as a blank line.</para>
        /// </summary>
        private static bool Separated(string text, int from, int to)
        {
            var blank = false; // only meaningful once the first break has been seen
            for (var i = from; i < to; i++)
            {
                if (text[i] == '\n')
                {
                    if (blank) return true;
                    blank = true;
                }
                else if (!char.IsWhiteSpace(text[i]))
                {
                    blank = false;
                }
            }
            return false;
        }

        /// <summary>Binds the collected block to <paramref name="offset"/> unless a blank line
        /// separates the two, and empties the block either way.</summary>
        private void Attach(string text, List<Span> pending, int offset)
        {
            if (pending.Count == 0) return;
            if (!Separated(text, pending[^1].End, offset))
                _docs[offset] = Join(text, pending);
            pending.Clear();
        }

        private static string Join(string text, List<Span> lines)
        {
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(Strip(text, line));
            }
            return sb.ToString();
        }

        /// <summary>Drops the '///' and one optional space behind it, plus trailing whitespace —
        /// a '\r' before the line break included.</summary>
        private static string Strip(string text, Span line)
        {
            var start = line.Start + 3;
            if (start < line.End && text[start] == ' ') start++;
            return text.AsSpan(start, line.End - start).TrimEnd().ToString();
        }

        public Token Peek(int offset = 0)
        {
            if (_pos + offset >= _buffer.Count) return _buffer.Last(); // return Eof
            return _buffer[_pos + offset];
        }

        public Token Current => _buffer[_pos];

        /// <summary>The current read index, for progress guards in recovery loops.</summary>
        public int Position => _pos;

        /// <summary>Resets the read head. Used to disambiguate <c>f&lt;int&gt;()</c> from
        /// <c>(f &lt; int) &gt; (…)</c>: read the type arguments speculatively first, and when no
        /// <c>(</c> follows, it was a comparison.</summary>
        public void Rewind(int position) => _pos = position;

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
