using Lyric.AST;
using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Parsing;

/// <summary>
/// Parser für Lyric (Sprache.md §4–§6). Strategie laut ROADMAP/STATUS:
/// Recursive-Descent für Typen (und später Statements/Declarations),
/// Pratt / Precedence-Climbing für Expressions.
///
/// Slice 1 deckt ab: Expressions (alle Operatoren mit korrekter Präzedenz aus
/// §6.1), TypeExpr (für Casts, Lambda-Annotations, Array-Größen), f-Strings und
/// Lambdas mit Expression-Body. Statements, Declarations, Patterns, if-/match-als
/// -Expression und Struct-Init folgen in späteren Slices.
///
/// Fehlerstrategie: nie werfen. Jeder Fehler geht als Diagnostic (LYR-PAR####) an
/// die <see cref="DiagnosticEngine"/>; der Parser liefert einen ErrorExpr/ErrorType
/// und macht bestmöglich weiter, damit ein Lauf mehrere Fehler meldet.
/// </summary>
public sealed partial class Parser
{
    private readonly TokenBuffer _buffer;
    private readonly SourceManager _sm;
    private readonly DiagnosticEngine _de;

    // Ob 'IDENT { … }' als Struct-Init gelesen werden darf. Ambient: am ExprStmt-Anfang
    // false (sonst mehrdeutig mit einem Block), in Delimitern via ParseSubExpr wieder true.
    private bool _allowStructInit = true;

    public Parser(SourceManager sm, FileId id, DiagnosticEngine de)
    {
        _sm = sm;
        _de = de;
        _buffer = new TokenBuffer(sm, id, de);
    }

    // ---------------------------------------------------------------------
    // Öffentlicher Einstieg (Slice 1: genau EIN Ausdruck)
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
    // Pratt-Kern: Binär-/Assign-/Range-/Cast-Operatoren (§6.1)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Bindungsstärken als (left, right). left &lt; right ⇒ links-assoziativ,
    /// left &gt; right ⇒ rechts-assoziativ. (-1, -1) ⇒ kein Infix-Operator.
    /// Werte spiegeln die Präzedenztabelle in Sprache.md §6.1 (höher = bindet stärker).
    /// </summary>
    private static (int left, int right) BindingPower(TokenKind op) => op switch
    {
        TokenKind.As => (27, 28),

        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => (25, 26),
        TokenKind.Plus or TokenKind.Minus => (23, 24),
        TokenKind.Shl or TokenKind.Shr => (21, 22),
        TokenKind.DotDot or TokenKind.DotDotEqual => (19, 20), // nicht-assoz.: unten explizit geprüft
        TokenKind.Amp => (17, 18),
        TokenKind.Caret => (15, 16),
        TokenKind.Pipe => (13, 14),
        TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual => (11, 12),
        TokenKind.EqualEqual or TokenKind.ExclamationEqual => (9, 10),
        TokenKind.AmpAmp => (7, 8),
        TokenKind.PipePipe => (5, 6),
        TokenKind.QuestionQuestion => (3, 2), // rechts-assoziativ

        // Assignments (rechts-assoziativ)
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
            if (leftBp < minBp) break; // deckt auch (-1, -1) ab

            // 'as': rechte Seite ist ein Typ, kein Ausdruck.
            if (op == TokenKind.As)
            {
                _buffer.Advance();
                var type = ParseType();
                left = new CastExpr(left, type, Span.Union(left.Span, type.Span));
                continue;
            }

            // Range: nicht verkettbar (Sprache.md §6.1).
            if (op is TokenKind.DotDot or TokenKind.DotDotEqual)
            {
                _buffer.Advance();
                var high = ParseExpr(rightBp);
                left = new RangeExpr(left, high, op == TokenKind.DotDotEqual, Span.Union(left.Span, high.Span));
                if (_buffer.Current.TokenKind is TokenKind.DotDot or TokenKind.DotDotEqual)
                    _de.Report("LYR-PAR0005", Severity.Error, _buffer.Current.Span, "range operator is not chainable");
                continue;
            }

            // Assignment (inkl. Compound): AssignExpr mit optionalem Basis-Operator.
            if (Operators.TryMapAssign(op, out var compound))
            {
                _buffer.Advance();
                var value = ParseExpr(rightBp);
                left = new AssignExpr(left, compound, value, Span.Union(left.Span, value.Span));
                continue;
            }

            // Restliche Binär-Operatoren.
            _buffer.Advance();
            var right = ParseExpr(rightBp);
            left = new BinaryExpr(left, Operators.MapBinary(op), right, Span.Union(left.Span, right.Span));
        }

        return left;
    }

    // ---------------------------------------------------------------------
    // Prefix (§6.1 Level 2) und Postfix (§6.1 Level 1)
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
        if (op is TokenKind.Resume) // 'resume co' (§8): Präfix wie await, bindet die Postfix-Kette
        {
            var kw = _buffer.Advance();
            var co = ParsePrefix();
            return new ResumeExpr(co, Span.Union(kw.Span, co.Span));
        }

        return ParsePostfix(ParsePrimary());
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
                return ParseIfExpr();      // if-Ausdruck (braucht else)
            case TokenKind.Match:
                return ParseMatchExpr();
            case TokenKind.LParen:
                return ParseParenOrTupleOrLambda();
            default:
            {
                _de.Report("LYR-PAR0002", Severity.Error, cur.Span, $"expected an expression, got {cur.TokenKind}");
                // Schluss-Token nicht schlucken: sie beenden umgebende Konstrukte und
                // dienen dort der Recovery.
                if (cur.TokenKind is not (TokenKind.Eof or TokenKind.RParen or TokenKind.RBracket
                    or TokenKind.RBrace or TokenKind.Comma or TokenKind.Semicolon))
                    _buffer.Advance();
                return new ErrorExpr(cur.Span);
            }
        }
    }

    /// <summary>
    /// '(' leitet drei Formen ein: Lambda <c>(params) =&gt; body</c>, Tuple-Literal
    /// <c>(a, b)</c> oder geklammerten Ausdruck <c>(expr)</c>. Lambdas werden per
    /// Lookahead auf ein '=&gt;' hinter der passenden ')' erkannt.
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
                if (_buffer.Check(TokenKind.RParen)) break; // Trailing-Comma tolerieren
                elems.Add(ParseSubExpr());
            }
            var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close tuple literal");
            var span = Span.Union(open.Span, close.Span);
            if (elems.Count < 2) // 1 Element = Gruppierung, kein Tuple (keine Obergrenze, Sprache.md §6.2)
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
                if (_buffer.Check(TokenKind.RBracket)) break; // Trailing-Comma
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
            if (_buffer.Check(TokenKind.RParen)) break; // Trailing-Comma
        }
        return args.ToArray();
    }

    /// <summary>
    /// Parst einen Ausdruck in einem Delimiter (Klammer/Argument/Index/Array/Hole):
    /// dort ist Struct-Init immer erlaubt, egal was der ambient-Flag außen sagt.
    /// </summary>
    private Expr ParseSubExpr()
    {
        var saved = _allowStructInit;
        _allowStructInit = true;
        var expr = ParseExpr(0);
        _allowStructInit = saved;
        return expr;
    }

    // Lookahead ab einem Identifier: ist es ein Struct-Init 'TypePath { … }'? Nur wenn erlaubt
    // und ein '{' direkt hinter dem (ggf. dotted, ggf. generischen) Typ-Pfad steht. Das '<'
    // wird nur als Typ-Argument-Liste gedeutet, wenn es balanciert schließt und ein '{' folgt —
    // sonst ist es ein Vergleich (a < b).
    private bool IsStructInitAhead()
    {
        if (!_allowStructInit) return false;
        var i = 1; // hinter dem aktuellen Identifier
        while (_buffer.Peek(i).TokenKind == TokenKind.Dot
               && _buffer.Peek(i + 1).TokenKind == TokenKind.Identifier)
            i += 2;
        if (_buffer.Peek(i).TokenKind == TokenKind.Less)
        {
            i = SkipTypeArgs(i);
            if (i < 0) return false;
        }
        return _buffer.Peek(i).TokenKind == TokenKind.LBrace;
    }

    // Überspringt ab Peek(start)=='<' eine balancierte Typ-Argument-Gruppe (Tiefe über '<'/'>',
    // '>>' schließt zwei). Rückgabe: Index hinter dem schließenden '>', oder -1 wenn nicht
    // balanciert / ein nicht-typ-artiges Token auftaucht (dann war '<' ein Vergleich).
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
                    break; // typ-artig, Tiefe unverändert
                default: return -1; // z.B. ';', '{', Literal, Operator → kein Typ-Arg
            }
            if (depth == 0) return i + 1; // sauber geschlossen
            if (depth < 0) return -1;      // über-geschlossen
        }
    }

    private Expr ParseStructInit()
    {
        var first = _buffer.Advance(); // erster IDENT
        var path = new List<string> { _sm.Slice(first.Span).ToString() };
        while (_buffer.Match(TokenKind.Dot))
            path.Add(_sm.Slice(_buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
                $"expected type name, got {_buffer.Current.TokenKind}").Span).ToString());

        TypeNode[] typeArgs = [];
        if (_buffer.Check(TokenKind.Less))
            typeArgs = ParseTypeArguments(out _);

        _buffer.Advance(); // '{' (durch IsStructInitAhead garantiert)
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
    // f-Strings (§1.5). Der Lexer liefert bereits die Sub-Tokens; hier nur
    // zusammensetzen: FStringStart { Chunk | InterpStart Expr [FormatSpec] InterpEnd } FStringEnd.
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
                segments.Add(new InterpText(_sm.Slice(t.Span).ToString(), t.Span)); // roh, Escapes bleiben stehen
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
            // Eof / Unerwartetes: Lexer hat den unterminierten f-String bereits gemeldet.
            end = t;
            break;
        }

        return new InterpolatedStringExpr(segments.ToArray(), Span.Union(start.Span, end.Span));
    }

    // ---------------------------------------------------------------------
    // Lambdas (§6.2). Slice 1: nur Expression-Body.
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
                parameters.Add(new LambdaParam(_sm.Slice(nameTok.Span).ToString(), type, pspan));
                if (!_buffer.Match(TokenKind.Comma)) break;
                if (_buffer.Check(TokenKind.RParen)) break; // Trailing-Comma
            }
        }
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after lambda parameters");

        TypeNode? returnType = null;
        if (_buffer.Match(TokenKind.Colon)) returnType = ParseType();

        _buffer.Expect(TokenKind.FatArrow, "LYR-PAR0012",
            $"expected '=>' in lambda, got {_buffer.Current.TokenKind}");

        // Body: Expression oder Block ('=> expr' bzw. '=> { ... }', Sprache.md §6.2).
        Node body = _buffer.Check(TokenKind.LBrace) ? ParseBlock() : ParseExpr(0);
        return new LambdaExpr(parameters.ToArray(), returnType, body, Span.Union(open.Span, body.Span));
    }

    /// <summary>
    /// Lookahead ab '(': balanciert Klammern bis zur passenden ')' und prüft, ob
    /// direkt danach ein '=&gt;' folgt. Nur dann ist es ein Lambda. Löst
    /// Lambda-vs-Tuple-vs-Grouping ohne Backtracking.
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
                    if (depth == 0) return _buffer.Peek(i + 1).TokenKind == TokenKind.FatArrow;
                    break;
                case TokenKind.Eof:
                    return false;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Typausdrücke (§4)
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
                return ParseTupleType();
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

    private TypeNode ParseTupleType()
    {
        var open = _buffer.Advance(); // '('
        var elems = new List<TypeNode>();
        do { elems.Add(ParseType()); } while (_buffer.Match(TokenKind.Comma) && !_buffer.Check(TokenKind.RParen));
        var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close tuple type");
        var span = Span.Union(open.Span, close.Span);
        if (elems.Count < 2) // keine Obergrenze (Sprache.md §4)
            _de.Report("LYR-PAR0010", Severity.Error, span, "tuple types need at least 2 elements");
        return new TupleType(elems.ToArray(), span);
    }

    private TypeNode[] ParseTypeArguments(out Span closeSpan)
    {
        _buffer.Expect(TokenKind.Less, "LYR-PAR0009", "expected '<' to open type arguments");
        var args = new List<TypeNode>();
        do { args.Add(ParseType()); } while (_buffer.Match(TokenKind.Comma));

        // Verschachtelte Generics: '>>', '>=' und '>>=' in einzelne '>' zerlegen.
        if (_buffer.Current.TokenKind is TokenKind.Shr or TokenKind.ShrEqual or TokenKind.GreaterEqual)
            _buffer.SplitCurrentGreater();

        closeSpan = _buffer.Expect(TokenKind.Greater, "LYR-PAR0009",
            $"expected '>' to close type arguments, got {_buffer.Current.TokenKind}").Span;
        return args.ToArray();
    }
}
