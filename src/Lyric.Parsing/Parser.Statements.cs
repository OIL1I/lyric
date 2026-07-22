using Lyric.AST;
using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Parsing;

/// <summary>
/// Statement-Parser (Sprache.md §5), Recursive-Descent. Dispatch über das erste
/// Token; alles ohne Statement-Keyword ist ein <c>ExprStmt</c>. Kontroll-Statements
/// halten ihren Rumpf als <c>Block</c>. Wie der Rest des Parsers: nie werfen —
/// Fehler als LYR-PAR#### plus ErrorStmt/ErrorExpr, dann bestmöglich weiter.
/// </summary>
public sealed partial class Parser
{
    /// <summary>Slice-2-Einstieg: genau EIN Statement (ein Block deckt Sequenzen ab).</summary>
    public Stmt ParseStatement()
    {
        var stmt = ParseStmt();
        if (!_buffer.AtEnd)
            _de.Report("LYR-PAR0001", Severity.Error, _buffer.Current.Span,
                $"unexpected token after statement: {_buffer.Current.TokenKind}");
        return stmt;
    }

    private Stmt ParseStmt() => _buffer.Current.TokenKind switch
    {
        TokenKind.LBrace => ParseBlock(),
        TokenKind.Let or TokenKind.Var => ParseBinding(),
        TokenKind.If => ParseIf(),
        TokenKind.While => ParseWhile(),
        TokenKind.Do => ParseDoWhile(),
        TokenKind.For => ParseForIn(),
        TokenKind.Break => ParseBreak(),
        TokenKind.Continue => ParseContinue(),
        TokenKind.Return => ParseReturn(),
        TokenKind.Yield => ParseYield(),
        TokenKind.Resume => ParseResume(),
        TokenKind.Defer => ParseDefer(),
        TokenKind.Throw => ParseThrow(),
        TokenKind.Try => ParseTry(),
        TokenKind.Match => ParseMatchDeferred(),
        _ => ParseExprStmt(),
    };

    private Block ParseBlock()
    {
        var open = _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open block");
        var stmts = new List<Stmt>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            var before = _buffer.Position;
            stmts.Add(ParseStmt());
            if (_buffer.Position == before && !_buffer.AtEnd)
                _buffer.Advance(); // Fortschritt erzwingen: verhindert Endlosschleife bei nicht-konsumiertem Token
        }
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close block");
        return new Block(stmts.ToArray(), Span.Union(open.Span, close.Span));
    }

    private BindingStmt ParseBinding()
    {
        var kw = _buffer.Advance(); // let / var
        var isMutable = kw.TokenKind == TokenKind.Var;
        var nameTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0020",
            $"expected binding name, got {_buffer.Current.TokenKind}");
        TypeNode? type = _buffer.Match(TokenKind.Colon) ? ParseType() : null;
        Expr? init = _buffer.Match(TokenKind.Equal) ? ParseExpr(0) : null;
        var semi = ExpectSemicolon();
        return new BindingStmt(isMutable, _sm.Slice(nameTok.Span).ToString(), type, init,
            Span.Union(kw.Span, semi.Span));
    }

    private Stmt ParseIf()
    {
        var kw = _buffer.Advance(); // if
        _buffer.Expect(TokenKind.LParen, "LYR-PAR0019", "expected '(' after 'if'");
        var cond = ParseExpr(0);
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after if-condition");
        var then = ParseBlock();

        Stmt? elseBranch = null;
        var end = then.Span;
        if (_buffer.Match(TokenKind.Else))
        {
            elseBranch = _buffer.Check(TokenKind.If) ? ParseIf() : ParseBlock(); // else-if kettet
            end = elseBranch.Span;
        }
        return new IfStmt(cond, then, elseBranch, Span.Union(kw.Span, end));
    }

    private Stmt ParseWhile()
    {
        var kw = _buffer.Advance(); // while
        _buffer.Expect(TokenKind.LParen, "LYR-PAR0019", "expected '(' after 'while'");
        var cond = ParseExpr(0);
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after while-condition");
        var body = ParseBlock();
        return new WhileStmt(cond, body, Span.Union(kw.Span, body.Span));
    }

    private Stmt ParseDoWhile()
    {
        var kw = _buffer.Advance(); // do
        var body = ParseBlock();
        _buffer.Expect(TokenKind.While, "LYR-PAR0022", "expected 'while' after do-block");
        _buffer.Expect(TokenKind.LParen, "LYR-PAR0019", "expected '(' after 'while'");
        var cond = ParseExpr(0);
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after while-condition");
        var semi = ExpectSemicolon();
        return new DoWhileStmt(body, cond, Span.Union(kw.Span, semi.Span));
    }

    private Stmt ParseForIn()
    {
        var kw = _buffer.Advance(); // for
        _buffer.Expect(TokenKind.LParen, "LYR-PAR0019", "expected '(' after 'for'");
        var varTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0020",
            $"expected loop variable, got {_buffer.Current.TokenKind}");
        _buffer.Expect(TokenKind.In, "LYR-PAR0021", "expected 'in' in for-loop");
        var iter = ParseExpr(0);
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after for-loop header");
        var body = ParseBlock();
        return new ForInStmt(_sm.Slice(varTok.Span).ToString(), iter, body, Span.Union(kw.Span, body.Span));
    }

    private Stmt ParseBreak()
    {
        var kw = _buffer.Advance();
        var semi = ExpectSemicolon();
        return new BreakStmt(Span.Union(kw.Span, semi.Span));
    }

    private Stmt ParseContinue()
    {
        var kw = _buffer.Advance();
        var semi = ExpectSemicolon();
        return new ContinueStmt(Span.Union(kw.Span, semi.Span));
    }

    private Stmt ParseReturn()
    {
        var kw = _buffer.Advance();
        Expr? value = _buffer.Check(TokenKind.Semicolon) ? null : ParseExpr(0);
        var semi = ExpectSemicolon();
        return new ReturnStmt(value, Span.Union(kw.Span, semi.Span));
    }

    private Stmt ParseYield()
    {
        var kw = _buffer.Advance();
        Expr? value = _buffer.Check(TokenKind.Semicolon) ? null : ParseExpr(0);
        var semi = ExpectSemicolon();
        return new YieldStmt(value, Span.Union(kw.Span, semi.Span));
    }

    private Stmt ParseResume()
    {
        var kw = _buffer.Advance();
        var coroutine = ParseExpr(0);
        Expr? value = _buffer.Match(TokenKind.Comma) ? ParseExpr(0) : null;
        var semi = ExpectSemicolon();
        return new ResumeStmt(coroutine, value, Span.Union(kw.Span, semi.Span));
    }

    private Stmt ParseDefer()
    {
        var kw = _buffer.Advance();
        Stmt body;
        if (_buffer.Check(TokenKind.LBrace))
        {
            body = ParseBlock();
        }
        else
        {
            var expr = ParseExpr(0);
            var semi = ExpectSemicolon();
            body = new ExprStmt(expr, Span.Union(expr.Span, semi.Span));
        }
        return new DeferStmt(body, Span.Union(kw.Span, body.Span));
    }

    private Stmt ParseThrow()
    {
        var kw = _buffer.Advance();
        var value = ParseExpr(0);
        var semi = ExpectSemicolon();
        return new ThrowStmt(value, Span.Union(kw.Span, semi.Span));
    }

    private Stmt ParseTry()
    {
        var kw = _buffer.Advance(); // try
        var body = ParseBlock();
        var catches = new List<CatchClause>();
        while (_buffer.Check(TokenKind.Catch))
            catches.Add(ParseCatch());
        if (catches.Count == 0)
            _de.Report("LYR-PAR0023", Severity.Error, Span.Union(kw.Span, body.Span),
                "try needs at least one catch clause");
        var end = catches.Count > 0 ? catches[^1].Span : body.Span;
        return new TryStmt(body, catches.ToArray(), Span.Union(kw.Span, end));
    }

    private CatchClause ParseCatch()
    {
        var kw = _buffer.Advance(); // catch
        _buffer.Expect(TokenKind.LParen, "LYR-PAR0019", "expected '(' after 'catch'");
        // CatchBinding: '_' | IDENTIFIER ':' TypeExpr | IDENTIFIER  ('_' ist ein Identifier)
        var idTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0020",
            $"expected catch binding, got {_buffer.Current.TokenKind}");
        var text = _sm.Slice(idTok.Span).ToString();
        string? name = text == "_" ? null : text; // '_' => catch-all ohne Binding
        TypeNode? type = _buffer.Match(TokenKind.Colon) ? ParseType() : null;
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after catch binding");
        var body = ParseBlock();
        return new CatchClause(name, type, body, Span.Union(kw.Span, body.Span));
    }

    private Stmt ParseExprStmt()
    {
        var expr = ParseExpr(0);
        var semi = ExpectSemicolon();
        return new ExprStmt(expr, Span.Union(expr.Span, semi.Span));
    }

    /// <summary>
    /// match ist Slice-4-Material (braucht Patterns). Bis dahin: klar melden und den
    /// gesamten <c>match (…) { … }</c>-Block balanciert überspringen, statt zu kaskadieren.
    /// </summary>
    private Stmt ParseMatchDeferred()
    {
        var kw = _buffer.Advance(); // match
        _de.Report("LYR-PAR0024", Severity.Error, kw.Span,
            "match statements are not yet implemented (planned for Slice 4)");
        SkipBalanced(TokenKind.LParen, TokenKind.RParen);
        var end = SkipBalanced(TokenKind.LBrace, TokenKind.RBrace);
        return new ErrorStmt(Span.Union(kw.Span, end));
    }

    private Token ExpectSemicolon() =>
        _buffer.Expect(TokenKind.Semicolon, "LYR-PAR0016", "expected ';'");

    /// <summary>Überspringt eine balancierte <paramref name="open"/>…<paramref name="close"/>
    /// -Gruppe und liefert den Span des zuletzt konsumierten Tokens. No-op, wenn das
    /// aktuelle Token nicht <paramref name="open"/> ist.</summary>
    private Span SkipBalanced(TokenKind open, TokenKind close)
    {
        var span = _buffer.Current.Span;
        if (!_buffer.Check(open)) return span;
        var depth = 0;
        while (!_buffer.AtEnd)
        {
            var t = _buffer.Advance();
            span = t.Span;
            if (t.TokenKind == open) depth++;
            else if (t.TokenKind == close && --depth == 0) break;
        }
        return span;
    }
}
