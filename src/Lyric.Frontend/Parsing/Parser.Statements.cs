using Lyric.AST;
using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Parsing;

/// <summary>
/// The statement parser, recursive descent. Dispatch is on the first token; anything without a
/// statement keyword is an <c>ExprStmt</c>. Control statements hold their body as a <c>Block</c>.
/// Like the rest of the parser: never throw,
/// reporting errors as LYR-PAR#### plus an ErrorStmt or ErrorExpr, then carrying on as best it can.
/// </summary>
public sealed partial class Parser
{
    /// <summary>Entry point for exactly ONE statement; a block covers sequences.</summary>
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
        TokenKind.Defer => ParseDefer(),
        TokenKind.Throw => ParseThrow(),
        TokenKind.Try => ParseTry(),
        TokenKind.Match => ParseMatchStmt(),
        _ => ParseExprStmt(),
    };

    private Stmt ParseMatchStmt()
    {
        var kw = _buffer.Advance(); // 'match'
        var (scrutinee, arms, end) = ParseMatchCore();
        return new MatchStmt(scrutinee, arms, Span.Union(kw.Span, end));
    }

    private Block ParseBlock()
    {
        var open = _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open block");
        var stmts = new List<Stmt>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            var before = _buffer.Position;
            stmts.Add(ParseStmt());
            if (_buffer.Position == before && !_buffer.AtEnd)
                _buffer.Advance(); // force progress, so an unconsumed token cannot loop forever
        }
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close block");
        return new Block(stmts.ToArray(), Span.Union(open.Span, close.Span));
    }

    private Stmt ParseBinding()
    {
        var kw = _buffer.Advance(); // let / var
        var isMutable = kw.TokenKind == TokenKind.Var;

        // 'let (a, b) = …' — destructuring. The parenthesis decides, and at this position it can
        // introduce nothing else: a binding name is an identifier.
        if (_buffer.Check(TokenKind.LParen)) return ParseDestructuring(kw, isMutable);

        var nameTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0020",
            $"expected binding name, got {_buffer.Current.TokenKind}");
        TypeNode? type = _buffer.Match(TokenKind.Colon) ? ParseType() : null;
        Expr? init = _buffer.Match(TokenKind.Equal) ? ParseExpr(0) : null;
        var semi = ExpectSemicolon();
        return new BindingStmt(isMutable, _sm.Slice(nameTok.Span).ToString(), type, init,
            Span.Union(kw.Span, semi.Span));
    }

    /// <summary>
    /// <c>let (a, b) = paar;</c> (Sprache.md §4).
    ///
    /// <para>The pattern is parsed as an ordinary tuple pattern — the same one a
    /// <c>match</c> arm uses. Whatever holds there holds here: nested
    /// pattern uses, with <c>_</c> as a placeholder, and the arity has to match.</para>
    /// </summary>
    private Stmt ParseDestructuring(Token kw, bool isMutable)
    {
        // ParseOrPattern rather than ParsePattern: the latter is the test entry point and requires
        // the file to end after it.
        var pattern = ParseOrPattern();
        if (pattern is not TuplePattern tuple)
        {
            _de.Report("LYR-PAR0020", Severity.Error, pattern.Span,
                "a destructuring binding needs a tuple pattern like '(a, b)'");
            tuple = new TuplePattern([], pattern.Span);
        }

        TypeNode? type = _buffer.Match(TokenKind.Colon) ? ParseType() : null;

        // The initializer is required: without it there would be nothing to take apart, and the
        // definite-assignment analysis would have to track several names without a value.
        if (!_buffer.Match(TokenKind.Equal))
        {
            _de.Report("LYR-PAR0020", Severity.Error, _buffer.Current.Span,
                "a destructuring binding needs an initializer ('let (a, b) = …;')");
            var bad = ExpectSemicolon();
            return new DestructuringStmt(isMutable, tuple, type, new ErrorExpr(bad.Span),
                Span.Union(kw.Span, bad.Span));
        }

        var init = ParseExpr(0);
        var semi = ExpectSemicolon();
        return new DestructuringStmt(isMutable, tuple, type, init, Span.Union(kw.Span, semi.Span));
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

    // resume is an expression; 'resume co;' runs as an ExprStmt through ParseExprStmt.

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
        // CatchBinding: '_' | IDENTIFIER ':' TypeExpr | IDENTIFIER  ('_' is an identifier)
        var idTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0020",
            $"expected catch binding, got {_buffer.Current.TokenKind}");
        var text = _sm.Slice(idTok.Span).ToString();
        string? name = text == "_" ? null : text; // '_' means catch-all without a binding
        TypeNode? type = _buffer.Match(TokenKind.Colon) ? ParseType() : null;
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after catch binding");
        var body = ParseBlock();
        return new CatchClause(name, type, body, Span.Union(kw.Span, body.Span));
    }

    private Stmt ParseExprStmt()
    {
        // No struct initializer at the start of a statement: 'Foo { … };' would otherwise be
        // ambiguous with a block. In value positions (bindings, arguments) it stays allowed.
        var saved = _allowStructInit;
        _allowStructInit = false;
        var expr = ParseExpr(0);
        _allowStructInit = saved;
        var semi = ExpectSemicolon();
        return new ExprStmt(expr, Span.Union(expr.Span, semi.Span));
    }

    private Token ExpectSemicolon() =>
        _buffer.Expect(TokenKind.Semicolon, "LYR-PAR0016", "expected ';'");
}
