using Lyric.AST;
using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Parsing;

/// <summary>
/// The Lyric parser.
/// Recursive descent for expressions, types, statements and declarations, with a Pratt loop for
/// the operator precedence.
///
/// Error strategy: never throw. Every error goes as a Diagnostic (LYR-PAR####) to
/// the <see cref="DiagnosticEngine"/>; the parser produces an ErrorExpr or ErrorType and carries
/// on as best it can, so one run reports several errors.
/// </summary>
public sealed partial class Parser
{
    private readonly TokenBuffer _buffer;
    private readonly SourceManager _sm;
    private readonly DiagnosticEngine _de;

    // Whether 'IDENT { … }' may be read as a struct initializer. Ambient: at the start of an
    // false at the start of a statement, where it would be ambiguous with a block, and true again
    // inside delimiters through ParseSubExpr.
    private bool _allowStructInit = true;

    public Parser(SourceManager sm, FileId id, DiagnosticEngine de)
    {
        _sm = sm;
        _de = de;
        _buffer = new TokenBuffer(sm, id, de);
    }

    /// <summary>
    /// The '///' blocks of this file, keyed by the source offset of what follows them. A side table
    /// rather than a field on <see cref="Decl"/>: the AST records stay untouched, and so does every
    /// pattern match over them.
    /// </summary>
    /// <remarks>Consumers look a declaration up through <see cref="DocOf"/>.</remarks>
    public IReadOnlyDictionary<int, string> DocComments => _buffer.DocComments;

    /// <summary>The doc comment written above <paramref name="node"/>, or <c>null</c>.</summary>
    public string? DocOf(Node node) => _buffer.DocComments.GetValueOrDefault(node.Span.Start);

    // ---------------------------------------------------------------------
    // Public entry point: exactly ONE expression.
    // ---------------------------------------------------------------------

    public Expr ParseExpression()
    {
        var expr = ParseExpr(0);
        if (!_buffer.AtEnd)
            _de.Report("LYR-PAR0001", Severity.Error, _buffer.Current.Span,
                $"unexpected token after expression: {_buffer.Current.TokenKind}");
        return expr;
    }

    // ---------------------------------------------------------------------
    // The Pratt core: binary, assignment, range and cast operators.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Binding powers as (left, right). left &lt; right means left-associative, left &gt; right
    /// means right-associative, and (-1, -1) means no infix operator.
    /// The values mirror the precedence table: higher binds tighter.
    /// </summary>
    private static (int left, int right) BindingPower(TokenKind op) => op switch
    {
        TokenKind.As => (27, 28),

        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => (25, 26),
        TokenKind.Plus or TokenKind.Minus => (23, 24),
        TokenKind.Shl or TokenKind.Shr => (21, 22),
        TokenKind.DotDot or TokenKind.DotDotEqual => (19, 20), // non-associative, checked explicitly below
        TokenKind.Amp => (17, 18),
        TokenKind.Caret => (15, 16),
        TokenKind.Pipe => (13, 14),
        TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual => (11, 12),
        TokenKind.EqualEqual or TokenKind.ExclamationEqual => (9, 10),
        TokenKind.AmpAmp => (7, 8),
        TokenKind.PipePipe => (5, 6),
        TokenKind.QuestionQuestion => (3, 2), // right-associative

        // Assignments, right-associative.
        TokenKind.Equal or TokenKind.PlusEqual or TokenKind.MinusEqual or TokenKind.StarEqual
            or TokenKind.SlashEqual or TokenKind.PercentEqual or TokenKind.ShlEqual or TokenKind.ShrEqual
            or TokenKind.AmpEqual or TokenKind.PipeEqual or TokenKind.CaretEqual or TokenKind.AmpAmpEqual
            or TokenKind.PipePipeEqual or TokenKind.QuestionQuestionEqual => (1, 0),

        _ => (-1, -1)
    };

    private Expr ParseExpr(int minBp)
    {
        var left = ParsePrefix();

        while (true)
        {
            var op = _buffer.Current.TokenKind;
            var (leftBp, rightBp) = BindingPower(op);
            if (leftBp < minBp) break; // covers (-1, -1) as well

            // 'as': the right-hand side is a type, not an expression.
            if (op == TokenKind.As)
            {
                _buffer.Advance();
                var type = ParseType();
                left = new CastExpr(left, type, Span.Union(left.Span, type.Span));
                continue;
            }

            // Range: not chainable.
            if (op is TokenKind.DotDot or TokenKind.DotDotEqual)
            {
                _buffer.Advance();
                var high = ParseExpr(rightBp);
                left = new RangeExpr(left, high, op == TokenKind.DotDotEqual, Span.Union(left.Span, high.Span));
                if (_buffer.Current.TokenKind is TokenKind.DotDot or TokenKind.DotDotEqual)
                    _de.Report("LYR-PAR0005", Severity.Error, _buffer.Current.Span, "range operator is not chainable");
                continue;
            }

            // Assignment, compound included: an AssignExpr with an optional base operator.
            if (Operators.TryMapAssign(op, out var compound))
            {
                _buffer.Advance();

                // The right of an '=' is a value position again, so a struct initializer is
                // allowed there. 'ParseExprStmt' turns the flag off for the whole statement,
                // because a statement must not begin with 'Foo { … }' — ambiguous with a block.
                // The ambiguity concerns the START only: no block can stand after an '='.
                var value = ParseSubExpr(rightBp);
                left = new AssignExpr(left, compound, value, Span.Union(left.Span, value.Span));
                continue;
            }

            // The remaining binary operators.
            _buffer.Advance();
            var right = ParseExpr(rightBp);
            left = new BinaryExpr(left, Operators.MapBinary(op), right, Span.Union(left.Span, right.Span));
        }

        return left;
    }

    // ---------------------------------------------------------------------
    // Prefix and postfix levels.
    // ---------------------------------------------------------------------

    private Expr ParsePrefix()
    {
        var op = _buffer.Current.TokenKind;
        if (op is TokenKind.Exclamation or TokenKind.Minus or TokenKind.Tilde or TokenKind.Inc or TokenKind.Dec)
        {
            var opTok = _buffer.Advance();
            var operand = ParsePrefix();
            return new UnaryExpr(Operators.MapPrefix(op), operand, Span.Union(opTok.Span, operand.Span));
        }
        if (op is TokenKind.Resume) // 'resume co': prefix like await, binds the postfix chain
        {
            var kw = _buffer.Advance();
            var co = ParsePrefix();
            return new ResumeExpr(co, Span.Union(kw.Span, co.Span));
        }

        return ParsePostfix(ParsePrimary());
    }

    /// <summary>
    /// Are these the type arguments of a call — <c>f&lt;int&gt;(…)</c> — or a comparison chain?
    ///
    /// <para>A pure token scan rather than speculative parsing.
    /// matters: <see cref="ParseType"/> reports diagnostics, and a guess that turns out wrong must
    /// leave no error behind. The scan here reports nothing; it counts brackets and looks at what
    /// follows the closing <c>&gt;</c>.</para>
    ///
    /// <para>The rule: they are type arguments when only tokens that can occur in a type expression
    /// stand between the <c>&lt;</c> and its match, and a <c>(</c> follows immediately.</para>
    ///
    /// <para>Conservative by design: in doubt it is a comparison. A misread comparison gives an
    /// understandable type error; a misread type argument list gives a parser error where the user
    /// suspects nothing.</para>
    /// </summary>
    private bool LooksLikeCallTypeArguments()
    {
        var depth = 0;

        for (var offset = 0; ; offset++)
        {
            switch (_buffer.Peek(offset).TokenKind)
            {
                case TokenKind.Less:
                    depth++;
                    break;

                case TokenKind.Greater:
                    depth--;
                    // Closed: the next token alone decides now.
                    if (depth == 0) return _buffer.Peek(offset + 1).TokenKind == TokenKind.LParen;
                    break;

                // What may occur in a type expression: named types with
                // Paths, arrays, optionals, function types, tuples.
                case TokenKind.Identifier:
                case TokenKind.Comma:
                case TokenKind.Dot:
                case TokenKind.LBracket:
                case TokenKind.RBracket:
                case TokenKind.Question:
                case TokenKind.Arrow:
                case TokenKind.Fn:
                case TokenKind.LParen:
                case TokenKind.RParen:
                    break;

                // Anything else cannot be a type, so the '<' was a comparison.
                default:
                    return false;
            }

            // A type argument list is short. The bound keeps a '<' anywhere in the source from
            // scanning half the buffer before giving up.
            if (offset > 64) return false;
        }
    }

    private Expr ParsePostfix(Expr operand)
    {
        while (true)
        {
            switch (_buffer.Current.TokenKind)
            {
                case TokenKind.Dot:
                {
                    _buffer.Advance();
                    var name = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0003",
                        $"expected member name after '.', got {_buffer.Current.TokenKind}");
                    operand = new MemberExpr(operand, _sm.Slice(name.Span).ToString(), false,
                        Span.Union(operand.Span, name.Span));
                    break;
                }
                case TokenKind.QuestionDot:
                {
                    _buffer.Advance();
                    var name = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0003",
                        $"expected member name after '?.', got {_buffer.Current.TokenKind}");
                    operand = new MemberExpr(operand, _sm.Slice(name.Span).ToString(), true,
                        Span.Union(operand.Span, name.Span));
                    break;
                }
                case TokenKind.LBracket:
                {
                    _buffer.Advance();
                    var index = ParseSubExpr();
                    var close = _buffer.Expect(TokenKind.RBracket, "LYR-PAR0004", "expected ']' to close index");
                    operand = new IndexExpr(operand, index, Span.Union(operand.Span, close.Span));
                    break;
                }
                // 'f<int>()' — explicit type arguments at a call site. Needed where the arguments
                // give nothing: a factory 'empty<T>(): List<T>' has none.
                case TokenKind.Less when LooksLikeCallTypeArguments():
                {
                    var typeArguments = ParseTypeArguments(out _);
                    _buffer.Expect(TokenKind.LParen, "LYR-PAR0008",
                        "expected '(' after type arguments");
                    var typedArgs = ParseArguments();
                    var typedClose = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008",
                        "expected ')' to close call");
                    operand = new CallExpr(operand, typedArgs,
                        Span.Union(operand.Span, typedClose.Span), typeArguments);
                    break;
                }

                case TokenKind.LParen:
                {
                    _buffer.Advance();
                    var args = ParseArguments();
                    var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close call");
                    operand = new CallExpr(operand, args, Span.Union(operand.Span, close.Span));
                    break;
                }
                case TokenKind.Inc:
                case TokenKind.Dec:
                case TokenKind.Exclamation:
                {
                    var opTok = _buffer.Advance();
                    operand = new PostfixExpr(operand, Operators.MapPostfix(opTok.TokenKind),
                        Span.Union(operand.Span, opTok.Span));
                    break;
                }
                default:
                    return operand;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Primary (§6.2)
    // ---------------------------------------------------------------------

    private Expr ParsePrimary()
    {
        var cur = _buffer.Current;
        switch (cur.TokenKind)
        {
            case TokenKind.IntLiteral:
            {
                var (value, suffix) = LiteralDecoder.DecodeInt(_sm.Slice(cur.Span), cur.Span, _de);
                _buffer.Advance();
                return new IntLiteralExpr(value, suffix, cur.Span);
            }
            case TokenKind.FloatLiteral:
            {
                var (value, suffix) = LiteralDecoder.DecodeFloat(_sm.Slice(cur.Span), cur.Span, _de);
                _buffer.Advance();
                return new FloatLiteralExpr(value, suffix, cur.Span);
            }
            case TokenKind.StringLiteral:
            {
                var value = LiteralDecoder.DecodeString(_sm.Slice(cur.Span), cur.Span, _de);
                _buffer.Advance();
                return new StringLiteralExpr(value, cur.Span);
            }
            case TokenKind.CharLiteral:
            {
                var value = LiteralDecoder.DecodeChar(_sm.Slice(cur.Span), cur.Span, _de);
                _buffer.Advance();
                return new CharLiteralExpr(value, cur.Span);
            }
            case TokenKind.True:
            case TokenKind.False:
                _buffer.Advance();
                return new BoolLiteralExpr(cur.TokenKind == TokenKind.True, cur.Span);
            case TokenKind.Null:
                _buffer.Advance();
                return new NullLiteralExpr(cur.Span);
            case TokenKind.Identifier:
                if (IsStructInitAhead()) return ParseStructInit();
                if (IsTypePathAhead()) return ParseTypePath();
                _buffer.Advance();
                return new IdentifierExpr(_sm.Slice(cur.Span).ToString(), cur.Span);
            case TokenKind.This:
                _buffer.Advance();
                return new ThisExpr(cur.Span);
            case TokenKind.AtIdentifier:
            {
                _buffer.Advance();
                var name = _sm.Slice(cur.Span).ToString();
                if (_buffer.Check(TokenKind.LParen))
                {
                    _buffer.Advance();
                    var args = ParseArguments();
                    var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close attribute arguments");
                    return new AtIdentifierExpr(name, args, Span.Union(cur.Span, close.Span));
                }
                return new AtIdentifierExpr(name, null, cur.Span);
            }
            case TokenKind.LBracket:
                return ParseArrayLit();
            case TokenKind.FStringStart:
                return ParseFString();
            case TokenKind.If:
                return ParseIfExpr();      // if expression, needs an else
            case TokenKind.Match:
                return ParseMatchExpr();
            case TokenKind.LParen:
                return ParseParenOrTupleOrLambda();
            default:
            {
                _de.Report("LYR-PAR0002", Severity.Error, cur.Span, $"expected an expression, got {cur.TokenKind}");
                // Closing tokens are not consumed: they end the surrounding construct and serve
                // its recovery.
                if (cur.TokenKind is not (TokenKind.Eof or TokenKind.RParen or TokenKind.RBracket
                    or TokenKind.RBrace or TokenKind.Comma or TokenKind.Semicolon))
                    _buffer.Advance();
                return new ErrorExpr(cur.Span);
            }
        }
    }

    /// <summary>
    /// '(' introduces three forms: a lambda <c>(params) =&gt; body</c>, a tuple literal
    /// <c>(a, b)</c> or a parenthesized expression <c>(expr)</c>. Lambdas are recognised by looking
    /// ahead for a '=&gt;' after the matching ')'.
    /// </summary>
    private Expr ParseParenOrTupleOrLambda()
    {
        if (IsLambdaAhead()) return ParseLambda();

        var open = _buffer.Advance(); // '('
        var first = ParseSubExpr();

        if (_buffer.Check(TokenKind.Comma))
        {
            var elems = new List<Expr> { first };
            while (_buffer.Match(TokenKind.Comma))
            {
                if (_buffer.Check(TokenKind.RParen)) break; // tolerate a trailing comma
                elems.Add(ParseSubExpr());
            }
            var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close tuple literal");
            var span = Span.Union(open.Span, close.Span);
            if (elems.Count < 2) // one element is a grouping, not a tuple; no upper bound
                _de.Report("LYR-PAR0010", Severity.Error, span, "tuple literals need at least 2 elements");
            return new TupleLitExpr(elems.ToArray(), span);
        }

        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close parenthesized expression");
        return first;
    }

    private ArrayLitExpr ParseArrayLit()
    {
        var open = _buffer.Advance(); // '['
        var elems = new List<Expr>();
        if (!_buffer.Check(TokenKind.RBracket))
        {
            while (true)
            {
                elems.Add(ParseSubExpr());
                if (!_buffer.Match(TokenKind.Comma)) break;
                if (_buffer.Check(TokenKind.RBracket)) break; // trailing comma
            }
        }
        var close = _buffer.Expect(TokenKind.RBracket, "LYR-PAR0004", "expected ']' to close array literal");
        return new ArrayLitExpr(elems.ToArray(), Span.Union(open.Span, close.Span));
    }

    private Expr[] ParseArguments()
    {
        var args = new List<Expr>();
        if (_buffer.Check(TokenKind.RParen)) return args.ToArray();
        while (true)
        {
            args.Add(ParseSubExpr());
            if (!_buffer.Match(TokenKind.Comma)) break;
            if (_buffer.Check(TokenKind.RParen)) break; // trailing comma
        }
        return args.ToArray();
    }

    /// <summary>
    /// Parses an expression inside a delimiter (parenthesis, argument, index, array, hole): a
    /// struct initializer is always allowed there, whatever the ambient flag says outside.
    /// </summary>
    private Expr ParseSubExpr(int minBindingPower = 0)
    {
        var saved = _allowStructInit;
        _allowStructInit = true;
        var expr = ParseExpr(minBindingPower);
        _allowStructInit = saved;
        return expr;
    }

    // Lookahead from an identifier: is this a struct initializer 'TypePath { … }'? Only when
    // allowed and a '{' follows the type path directly, dotted and generic paths included. The '<'
    // counts as a type argument list only when it closes balanced and a '{' follows; otherwise it
    // is a comparison (a < b).
    private bool IsStructInitAhead()
    {
        if (!_allowStructInit) return false;
        var i = 1; // past the current identifier
        while (_buffer.Peek(i).TokenKind == TokenKind.Dot
               && _buffer.Peek(i + 1).TokenKind == TokenKind.Identifier)
            i += 2;
        if (_buffer.Peek(i).TokenKind == TokenKind.Less)
        {
            i = SkipTypeArgs(i);
            if (i < 0) return false;

            // One more segment may follow the arguments: in 'Ev<int>.Hit { … }' the arguments
            // belong to the enum and the variant hangs off the back. Without this line a struct
            // variant of a generic enum cannot be written.
            if (_buffer.Peek(i).TokenKind == TokenKind.Dot
                && _buffer.Peek(i + 1).TokenKind == TokenKind.Identifier)
                i += 2;
        }
        return _buffer.Peek(i).TokenKind == TokenKind.LBrace;
    }

    /// <summary>
    /// Lookahead from an identifier: is this a type path WITH arguments in value position, that is
    /// segments joined by <c>.</c>, then <c>&lt;…&gt;</c>, then a <c>.</c> directly after?
    ///
    /// <para>Without arguments the <c>&lt;</c> is no type path: <c>P.neu()</c> is an ordinary
    /// identifier whose symbol happens to be a type and does not need this route. Hence there is NO
    /// optional <c>&lt;</c> here, unlike in <see cref="IsStructInitAhead"/>.</para>
    ///
    /// <para>The rule costs no ambiguity: a <c>.</c> after a comparison chain
    /// (<c>a &lt; b &gt; .c</c>) is not a valid expression anyway.</para>
    /// </summary>
    private bool IsTypePathAhead()
    {
        var i = 1; // past the current identifier
        while (_buffer.Peek(i).TokenKind == TokenKind.Dot
               && _buffer.Peek(i + 1).TokenKind == TokenKind.Identifier)
            i += 2;

        if (_buffer.Peek(i).TokenKind != TokenKind.Less) return false;

        i = SkipTypeArgs(i);
        return i >= 0 && _buffer.Peek(i).TokenKind == TokenKind.Dot;
    }

    private Expr ParseTypePath()
    {
        var first = _buffer.Advance(); // first IDENT
        var path = new List<string> { _sm.Slice(first.Span).ToString() };

        // The lookahead guaranteed 'IDENT (. IDENT)* <', so the loop ends at the '<'.
        while (_buffer.Match(TokenKind.Dot))
            path.Add(_sm.Slice(_buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
                $"expected type name, got {_buffer.Current.TokenKind}").Span).ToString());

        var typeArgs = ParseTypeArguments(out var close);
        return new TypePathExpr(path.ToArray(), typeArgs, Span.Union(first.Span, close));
    }

    // Skips a balanced type argument group starting at Peek(start)=='<' (depth over '<' and '>',
    // '>>' closes two). Returns the index past the closing '>', or -1 when it is unbalanced or a
    // non-type-like token appears, in which case the '<' was a comparison.
    private int SkipTypeArgs(int start)
    {
        var depth = 0;
        for (var i = start; ; i++)
        {
            switch (_buffer.Peek(i).TokenKind)
            {
                case TokenKind.Less: depth++; break;
                case TokenKind.Greater: depth--; break;
                case TokenKind.Shr: depth -= 2; break;
                case TokenKind.Identifier or TokenKind.Dot or TokenKind.Comma
                    or TokenKind.LBracket or TokenKind.RBracket or TokenKind.Question
                    or TokenKind.LParen or TokenKind.RParen or TokenKind.Fn or TokenKind.Arrow:
                    break; // type-like, depth unchanged
                default: return -1; // ';', '{', a literal or an operator is no type argument
            }
            if (depth == 0) return i + 1; // closed cleanly
            if (depth < 0) return -1;      // over-closed
        }
    }

    private Expr ParseStructInit()
    {
        var first = _buffer.Advance(); // first IDENT
        var path = new List<string> { _sm.Slice(first.Span).ToString() };
        while (_buffer.Match(TokenKind.Dot))
            path.Add(_sm.Slice(_buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
                $"expected type name, got {_buffer.Current.TokenKind}").Span).ToString());

        TypeNode[] typeArgs = [];
        if (_buffer.Check(TokenKind.Less))
        {
            typeArgs = ParseTypeArguments(out _);

            // 'Ev<int>.Hit { … }': the variant stands BEHIND the enum's arguments.
            while (_buffer.Match(TokenKind.Dot))
                path.Add(_sm.Slice(_buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
                    $"expected variant name, got {_buffer.Current.TokenKind}").Span).ToString());
        }

        _buffer.Advance(); // '{', guaranteed by IsStructInitAhead
        var fields = new List<StructInitField>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            var nameTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
                $"expected field name, got {_buffer.Current.TokenKind}");
            _buffer.Expect(TokenKind.Equal, "LYR-PAR0037", "expected '=' in struct initializer (':' is only for types)");
            var value = ParseSubExpr();
            fields.Add(new StructInitField(_sm.Slice(nameTok.Span).ToString(), value,
                Span.Union(nameTok.Span, value.Span)));
            if (!_buffer.Match(TokenKind.Comma)) break;
        }
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close struct initializer");
        return new StructInitExpr(path.ToArray(), typeArgs, fields.ToArray(), Span.Union(first.Span, close.Span));
    }

    // ---------------------------------------------------------------------
    // f-strings. The lexer already yields the sub-tokens; this only assembles them:
    // FStringStart { Chunk | InterpStart Expr [FormatSpec] InterpEnd } FStringEnd.
    // ---------------------------------------------------------------------

    private InterpolatedStringExpr ParseFString()
    {
        var start = _buffer.Advance(); // FStringStart
        var segments = new List<InterpSegment>();
        var end = start;

        while (true)
        {
            var t = _buffer.Current;
            if (t.TokenKind == TokenKind.FStringChunk)
            {
                _buffer.Advance();
                segments.Add(new InterpText(_sm.Slice(t.Span).ToString(), t.Span)); // raw, escapes stay as they are
                continue;
            }
            if (t.TokenKind == TokenKind.FStringInterpStart)
            {
                _buffer.Advance();
                var expr = ParseSubExpr();
                string? formatSpec = null;
                if (_buffer.Check(TokenKind.FStringFormatSpec))
                    formatSpec = _sm.Slice(_buffer.Advance().Span).ToString();
                var interpEnd = _buffer.Expect(TokenKind.FStringInterpEnd, "LYR-PAR0014",
                    "expected '}' to close interpolation");
                segments.Add(new InterpHole(expr, formatSpec, Span.Union(t.Span, interpEnd.Span)));
                continue;
            }
            if (t.TokenKind == TokenKind.FStringEnd)
            {
                end = _buffer.Advance();
                break;
            }
            // EOF or something unexpected: the lexer already reported the unterminated f-string.
            end = t;
            break;
        }

        return new InterpolatedStringExpr(segments.ToArray(), Span.Union(start.Span, end.Span));
    }

    // ---------------------------------------------------------------------
    // Lambdas.
    // ---------------------------------------------------------------------

    private LambdaExpr ParseLambda()
    {
        var open = _buffer.Advance(); // '('
        var parameters = new List<LambdaParam>();
        if (!_buffer.Check(TokenKind.RParen))
        {
            while (true)
            {
                var nameTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0013",
                    $"expected lambda parameter name, got {_buffer.Current.TokenKind}");
                TypeNode? type = null;
                if (_buffer.Match(TokenKind.Colon)) type = ParseType();
                var pspan = type is null ? nameTok.Span : Span.Union(nameTok.Span, type.Span);
                parameters.Add(new LambdaParam(_sm.Slice(nameTok.Span).ToString(), type, pspan)
                    { NameSpan = nameTok.Span });
                if (!_buffer.Match(TokenKind.Comma)) break;
                if (_buffer.Check(TokenKind.RParen)) break; // trailing comma
            }
        }
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after lambda parameters");

        TypeNode? returnType = null;
        if (_buffer.Match(TokenKind.Colon)) returnType = ParseType();

        _buffer.Expect(TokenKind.FatArrow, "LYR-PAR0012",
            $"expected '=>' in lambda, got {_buffer.Current.TokenKind}");

        // Body: an expression or a block, '=> expr' or '=> { ... }'.
        Node body = _buffer.Check(TokenKind.LBrace) ? ParseBlock() : ParseExpr(0);
        return new LambdaExpr(parameters.ToArray(), returnType, body, Span.Union(open.Span, body.Span));
    }

    /// <summary>
    /// Lookahead from '(': balances parentheses up to the matching ')' and checks whether a
    /// '=&gt;' follows directly. Only then is it a lambda. Resolves lambda vs tuple vs grouping
    /// without backtracking.
    /// </summary>
    private bool IsLambdaAhead()
    {
        var depth = 0;
        for (var i = 0; ; i++)
        {
            switch (_buffer.Peek(i).TokenKind)
            {
                case TokenKind.LParen:
                case TokenKind.LBracket:
                case TokenKind.LBrace:
                    depth++;
                    break;
                case TokenKind.RParen:
                case TokenKind.RBracket:
                case TokenKind.RBrace:
                    depth--;
                    if (depth == 0) return LambdaTailAhead(i + 1);
                    break;
                case TokenKind.Eof:
                    return false;
            }
        }
    }

    // After the closing ')': either '=>' directly OR ': TypeExpr =>' with a return annotation.
    // The type is skipped by token classification only, as in SkipTypeArgs.
    private bool LambdaTailAhead(int i)
    {
        if (_buffer.Peek(i).TokenKind == TokenKind.FatArrow) return true;
        if (_buffer.Peek(i).TokenKind != TokenKind.Colon) return false;
        var depth = 0;
        for (var j = i + 1; ; j++)
        {
            switch (_buffer.Peek(j).TokenKind)
            {
                case TokenKind.FatArrow when depth == 0: return true;
                case TokenKind.LParen or TokenKind.LBracket: depth++; break;
                case TokenKind.RParen or TokenKind.RBracket: depth--; if (depth < 0) return false; break;
                case TokenKind.Identifier or TokenKind.Dot or TokenKind.Comma or TokenKind.Question
                    or TokenKind.Fn or TokenKind.Arrow or TokenKind.Less or TokenKind.Greater
                    or TokenKind.Shr or TokenKind.IntLiteral:
                    break; // type-like
                default: return false; // ';', a literal or an operator is no lambda tail
            }
        }
    }

    // ---------------------------------------------------------------------
    // Type expressions.
    // ---------------------------------------------------------------------

    private TypeNode ParseType()
    {
        var qTok = _buffer.Current;
        var nullable = _buffer.Match(TokenKind.Question);

        var type = ParseTypeAtom();

        while (_buffer.Check(TokenKind.LBracket)) // T[] / T[N]
        {
            _buffer.Advance();
            IntLiteralExpr? size = null;
            if (_buffer.Check(TokenKind.IntLiteral))
            {
                var sizeTok = _buffer.Advance();
                var (value, suffix) = LiteralDecoder.DecodeInt(_sm.Slice(sizeTok.Span), sizeTok.Span, _de);
                size = new IntLiteralExpr(value, suffix, sizeTok.Span);
            }
            var close = _buffer.Expect(TokenKind.RBracket, "LYR-PAR0004", "expected ']' to close array type");
            type = new ArrayType(type, size, Span.Union(type.Span, close.Span));
        }

        return nullable ? new NullableType(type, Span.Union(qTok.Span, type.Span)) : type;
    }

    private TypeNode ParseTypeAtom()
    {
        var cur = _buffer.Current;
        switch (cur.TokenKind)
        {
            case TokenKind.Fn:
                return ParseFunctionType();
            case TokenKind.LParen:
                return ParseParenthesizedType();
            case TokenKind.Identifier:
            {
                _buffer.Advance();
                var path = new List<string> { _sm.Slice(cur.Span).ToString() };
                var end = cur.Span;
                while (_buffer.Match(TokenKind.Dot))
                {
                    var seg = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0011",
                        $"expected identifier in type path, got {_buffer.Current.TokenKind}");
                    path.Add(_sm.Slice(seg.Span).ToString());
                    end = seg.Span;
                }
                TypeNode[] args = [];
                if (_buffer.Check(TokenKind.Less))
                {
                    args = ParseTypeArguments(out var closeSpan);
                    end = closeSpan;
                }
                return new NamedType(path.ToArray(), args, Span.Union(cur.Span, end));
            }
            default:
                _de.Report("LYR-PAR0011", Severity.Error, cur.Span, $"expected a type, got {cur.TokenKind}");
                return new ErrorType(cur.Span);
        }
    }

    private FunctionType ParseFunctionType()
    {
        var start = _buffer.Advance(); // 'fn'
        _buffer.Expect(TokenKind.LParen, "LYR-PAR0008", "expected '(' after 'fn' in function type");
        var parameters = new List<TypeNode>();
        if (!_buffer.Check(TokenKind.RParen))
            do { parameters.Add(ParseType()); } while (_buffer.Match(TokenKind.Comma));
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' in function type");
        _buffer.Expect(TokenKind.Arrow, "LYR-PAR0015",
            $"expected '->' in function type, got {_buffer.Current.TokenKind}");
        var returnType = ParseType();
        return new FunctionType(parameters.ToArray(), returnType, Span.Union(start.Span, returnType.Span));
    }

    /// <summary>
    /// <c>(</c> in type position: either a TUPLE, from two elements on, or a plain GROUPING.
    ///
    /// <para>No conflict between the two, because Lyric has no 1-tuple: <c>TupleType</c> requires
    /// arity 2. Rust needs <c>(T,)</c> for this; here the spot is free.</para>
    ///
    /// <para>What the grouping is for: <c>fn(A) -&gt; R</c> is the only type in the language open
    /// to the right — <c>fn(int) -&gt; void[]</c> reads as a function returning <c>void[]</c>, and
    /// an array of function values could not be written at all. The precedence stays as it is.</para>
    /// </summary>
    private TypeNode ParseParenthesizedType()
    {
        var open = _buffer.Advance(); // '('

        var elems = new List<TypeNode>();
        var sawComma = false;
        do
        {
            elems.Add(ParseType());
            if (!_buffer.Match(TokenKind.Comma)) break;
            sawComma = true;
        } while (!_buffer.Check(TokenKind.RParen));

        var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close type");
        var span = Span.Union(open.Span, close.Span);

        // One element WITHOUT a comma is a grouping: the inner type moves up unchanged. With a
        // comma ('(T,)') a tuple was meant, and its second element is missing.
        if (elems.Count == 1 && !sawComma) return elems[0];

        if (elems.Count < 2) // no upper bound
            _de.Report("LYR-PAR0010", Severity.Error, span, "tuple types need at least 2 elements");

        return new TupleType(elems.ToArray(), span);
    }

    private TypeNode[] ParseTypeArguments(out Span closeSpan)
    {
        _buffer.Expect(TokenKind.Less, "LYR-PAR0009", "expected '<' to open type arguments");
        var args = new List<TypeNode>();
        do { args.Add(ParseType()); } while (_buffer.Match(TokenKind.Comma));

        // Nested generics: split '>>', '>=' and '>>=' into single '>' tokens.
        if (_buffer.Current.TokenKind is TokenKind.Shr or TokenKind.ShrEqual or TokenKind.GreaterEqual)
            _buffer.SplitCurrentGreater();

        closeSpan = _buffer.Expect(TokenKind.Greater, "LYR-PAR0009",
            $"expected '>' to close type arguments, got {_buffer.Current.TokenKind}").Span;
        return args.ToArray();
    }
}
