using Lyric.AST;
using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Parsing;

/// <summary>
/// The pattern parser plus <c>match</c> and <c>if</c> as expressions. Or-patterns
/// (<c>a | b</c>) are the outermost level; ranges (<c>0..=9</c>) and variants
/// sit below them. Pattern literals reuse <see cref="ParsePrimary"/>.
/// </summary>
public sealed partial class Parser
{
    /// <summary>Entry point for a single pattern, used by tests.</summary>
    public Pattern ParsePattern()
    {
        var pattern = ParseOrPattern();
        if (!_buffer.AtEnd)
            _de.Report("LYR-PAR0001", Severity.Error, _buffer.Current.Span,
                $"unexpected token after pattern: {_buffer.Current.TokenKind}");
        return pattern;
    }

    private Pattern ParseOrPattern()
    {
        var first = ParsePatternPrimary();
        if (!_buffer.Check(TokenKind.Pipe)) return first;
        var alts = new List<Pattern> { first };
        while (_buffer.Match(TokenKind.Pipe))
            alts.Add(ParsePatternPrimary());
        return new OrPattern(alts.ToArray(), Span.Union(first.Span, alts[^1].Span));
    }

    private Pattern ParsePatternPrimary()
    {
        var cur = _buffer.Current;
        switch (cur.TokenKind)
        {
            case TokenKind.IntLiteral:
            case TokenKind.FloatLiteral:
            case TokenKind.StringLiteral:
            case TokenKind.CharLiteral:
            case TokenKind.True:
            case TokenKind.False:
            case TokenKind.Null:
            case TokenKind.Minus: // negatives numerisches Literal
            {
                var low = ParsePatternLiteral();
                if (_buffer.Check(TokenKind.DotDot) || _buffer.Check(TokenKind.DotDotEqual))
                {
                    var inclusive = _buffer.Current.TokenKind == TokenKind.DotDotEqual;
                    _buffer.Advance();
                    var high = ParsePatternLiteral();
                    return new RangePattern(low, high, inclusive, Span.Union(low.Span, high.Span));
                }
                return new LiteralPattern(low, low.Span);
            }
            case TokenKind.Identifier:
                if (AtContextual("_")) { _buffer.Advance(); return new WildcardPattern(cur.Span); }
                return ParsePathPattern();
            case TokenKind.LParen:
                return ParseTuplePattern();
            default:
                _de.Report("LYR-PAR0033", Severity.Error, cur.Span, $"expected a pattern, got {cur.TokenKind}");
                if (cur.TokenKind is not (TokenKind.Eof or TokenKind.RParen or TokenKind.RBrace
                    or TokenKind.RBracket or TokenKind.Comma or TokenKind.FatArrow or TokenKind.Pipe))
                    _buffer.Advance();
                return new ErrorPattern(cur.Span);
        }
    }

    // A literal expression as a pattern value, optionally with a leading '-'.
    private Expr ParsePatternLiteral()
    {
        if (_buffer.Check(TokenKind.Minus))
        {
            var minus = _buffer.Advance();
            var atom = ParsePrimary();
            return new UnaryExpr(UnaryOp.Neg, atom, Span.Union(minus.Span, atom.Span));
        }
        return ParsePrimary();
    }

    private Pattern ParsePathPattern()
    {
        var first = _buffer.Advance(); // an identifier, not '_'
        var path = new List<string> { _sm.Slice(first.Span).ToString() };
        var last = first;
        while (_buffer.Match(TokenKind.Dot))
        {
            last = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
                $"expected identifier in pattern path, got {_buffer.Current.TokenKind}");
            path.Add(_sm.Slice(last.Span).ToString());
        }

        if (_buffer.Check(TokenKind.LParen)) // tuple variant: Circle(r)
        {
            _buffer.Advance();
            var elems = new List<Pattern>();
            if (!_buffer.Check(TokenKind.RParen))
                do { elems.Add(ParseOrPattern()); } while (_buffer.Match(TokenKind.Comma) && !_buffer.Check(TokenKind.RParen));
            var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' in variant pattern");
            return new VariantPattern(path.ToArray(), elems.ToArray(), null, Span.Union(first.Span, close.Span));
        }

        if (_buffer.Check(TokenKind.LBrace)) // struct variant: Triangle { a, b, c }
        {
            _buffer.Advance();
            var fields = new List<FieldPattern>();
            while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
            {
                fields.Add(ParseFieldPattern());
                if (!_buffer.Match(TokenKind.Comma)) break;
            }
            var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' in struct pattern");
            return new VariantPattern(path.ToArray(), null, fields.ToArray(), Span.Union(first.Span, close.Span));
        }

        // A single bare identifier is a binding or a unit variant, decided by the sema; a qualified
        // one is always a unit variant.
        if (path.Count == 1) return new BindingPattern(path[0], first.Span);
        return new VariantPattern(path.ToArray(), null, null, Span.Union(first.Span, last.Span));
    }

    private FieldPattern ParseFieldPattern()
    {
        var nameTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
            $"expected field name in pattern, got {_buffer.Current.TokenKind}");
        Pattern? sub = null;
        var end = nameTok.Span;
        if (_buffer.Match(TokenKind.Equal)) // x = Pattern
        {
            sub = ParseOrPattern();
            end = sub.Span;
        }
        return new FieldPattern(_sm.Slice(nameTok.Span).ToString(), sub, Span.Union(nameTok.Span, end));
    }

    private Pattern ParseTuplePattern()
    {
        var open = _buffer.Advance(); // '('
        var first = ParseOrPattern();
        if (_buffer.Check(TokenKind.Comma))
        {
            var elems = new List<Pattern> { first };
            while (_buffer.Match(TokenKind.Comma))
            {
                if (_buffer.Check(TokenKind.RParen)) break;
                elems.Add(ParseOrPattern());
            }
            var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close tuple pattern");
            return new TuplePattern(elems.ToArray(), Span.Union(open.Span, close.Span));
        }
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close pattern group");
        return first; // grouping
    }

    // --- match (§5/§6.2) ---

    private MatchExpr ParseMatchExpr()
    {
        var kw = _buffer.Advance(); // 'match'
        var (scrutinee, arms, end) = ParseMatchCore();
        return new MatchExpr(scrutinee, arms, Span.Union(kw.Span, end));
    }

    // Parses '(' Expr ')' '{' { MatchArm } '}'; the caller has consumed the 'match'.
    private (Expr Scrutinee, MatchArm[] Arms, Span End) ParseMatchCore()
    {
        _buffer.Expect(TokenKind.LParen, "LYR-PAR0019", "expected '(' after 'match'");
        var scrutinee = ParseExpr(0);
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after match scrutinee");
        _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open match body");

        var arms = new List<MatchArm>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            var before = _buffer.Position;
            var arm = ParseMatchArm();
            arms.Add(arm);
            if (_buffer.Position == before) { _buffer.Advance(); continue; }
            if (_buffer.Check(TokenKind.RBrace)) break;
            // Block arm: the comma is optional. Expression arm: required, except on the last arm.
            if (arm.Body is Block) _buffer.Match(TokenKind.Comma);
            else _buffer.Expect(TokenKind.Comma, "LYR-PAR0035", "expected ',' after match arm");
        }

        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close match body");
        return (scrutinee, arms.ToArray(), close.Span);
    }

    private MatchArm ParseMatchArm()
    {
        var pattern = ParseOrPattern();
        Expr? guard = _buffer.Match(TokenKind.If) ? ParseExpr(0) : null;
        _buffer.Expect(TokenKind.FatArrow, "LYR-PAR0034", $"expected '=>' in match arm, got {_buffer.Current.TokenKind}");
        Node body = _buffer.Check(TokenKind.LBrace) ? ParseBlock() : ParseExpr(0);
        return new MatchArm(pattern, guard, body, Span.Union(pattern.Span, body.Span));
    }

    // --- if as an expression: always needs an else ---

    private IfExpr ParseIfExpr()
    {
        var kw = _buffer.Advance(); // 'if'
        _buffer.Expect(TokenKind.LParen, "LYR-PAR0019", "expected '(' after 'if'");
        var cond = ParseExpr(0);
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after if-condition");
        var then = ParseExpr(0); // the branch is an expression, so a value is guaranteed
        _buffer.Expect(TokenKind.Else, "LYR-PAR0036", "if-expression requires an 'else' branch");
        var elseBranch = ParseExpr(0); // 'else if' falls out as a nested IfExpr; if is primary
        return new IfExpr(cond, then, elseBranch, Span.Union(kw.Span, elseBranch.Span));
    }
}
