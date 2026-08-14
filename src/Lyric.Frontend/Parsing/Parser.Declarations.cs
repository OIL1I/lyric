using Lyric.AST;
using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Parsing;

/// <summary>
/// The module and declaration parser, recursive descent.
///
/// Contextual keywords: <c>throws</c> and <c>type</c> are not in the keyword list — the lexer
/// yields them as identifiers and they are recognised here by position.
///
/// Member separation: in a struct or class a field needs a ',', a block-bodied method does not.
/// Enum variants are separated by ','.
/// In an interface, extend or enum body the members form a sequence without separators.
/// </summary>
public sealed partial class Parser
{
    /// <summary>Entry point for a whole file: an optional module header plus top-level declarations.</summary>
    public Module ParseModule()
    {
        var start = _buffer.Current.Span;
        ModulePath? header = _buffer.Check(TokenKind.Module) ? ParseModuleHeader() : null;

        var decls = new List<Decl>();
        while (!_buffer.AtEnd)
        {
            var before = _buffer.Position;
            decls.Add(ParseTopLevelDecl());
            if (_buffer.Position == before) _buffer.Advance(); // force progress
        }

        var end = decls.Count > 0 ? decls[^1].Span : (header?.Span ?? start);
        return new Module(header, decls.ToArray(), Span.Union(start, end));
    }

    private ModulePath ParseModuleHeader()
    {
        var kw = _buffer.Advance(); // 'module'
        var segments = ParseDottedName();
        var semi = ExpectSemicolon();
        return new ModulePath(segments, Span.Union(kw.Span, semi.Span));
    }

    private Decl ParseTopLevelDecl()
    {
        if (_buffer.Check(TokenKind.Import)) return ParseImport();

        var start = _buffer.Current.Span;
        var isPublic = _buffer.Match(TokenKind.Pub);

        switch (_buffer.Current.TokenKind)
        {
            case TokenKind.Mut:
            case TokenKind.Fn:
                return ParseFunctionDecl(isPublic, start);
            case TokenKind.Struct:
                return ParseStructOrClass(isPublic, start, isClass: false);
            case TokenKind.Class:
                return ParseStructOrClass(isPublic, start, isClass: true);
            case TokenKind.Enum:
                return ParseEnum(isPublic, start);
            case TokenKind.Interface:
                return ParseInterface(isPublic, start);
            case TokenKind.Extend:
                return ParseExtend(isPublic, start);
            case TokenKind.Let:
            case TokenKind.Var:
                return ParseGlobalBinding(isPublic, start);
            // Attributes are post-v1. The syntax stays reserved, and this reports its own message
            // rather than "expected a declaration": someone writing '@test' expected something
            // that exists, just not yet.
            case TokenKind.AtIdentifier:
                _de.Report("LYR-PAR0038", Severity.Error, _buffer.Current.Span,
                    "attributes are not part of v1 (Sprache.md §10); '@test' and 'lyric test' " +
                    "arrive after v1.0");
                var skipped = SynchronizeTopLevel();
                return new ErrorDecl(Span.Union(start, skipped));

            default:
                if (AtContextual("type")) return ParseTypeAlias(isPublic, start);
                _de.Report("LYR-PAR0025", Severity.Error, _buffer.Current.Span,
                    $"expected a declaration, got {_buffer.Current.TokenKind}");
                var end = SynchronizeTopLevel(); // skip to the next declaration start, so only ONE error
                return new ErrorDecl(Span.Union(start, end));
        }
    }

    /// <summary>Recovery: consumes tokens up to the next plausible declaration start (a keyword,
    /// the contextual 'type', or EOF). Returns the span of the last skipped token.</summary>
    private Span SynchronizeTopLevel()
    {
        var span = _buffer.Current.Span;
        while (!_buffer.AtEnd)
        {
            if (_buffer.Current.TokenKind is TokenKind.Module or TokenKind.Import or TokenKind.Pub
                or TokenKind.Fn or TokenKind.Mut or TokenKind.Struct or TokenKind.Class or TokenKind.Enum
                or TokenKind.Interface or TokenKind.Extend or TokenKind.Let or TokenKind.Var)
                break;
            if (AtContextual("type")) break;
            span = _buffer.Advance().Span;
        }
        return span;
    }

    // --- imports ---

    private Decl ParseImport()
    {
        var kw = _buffer.Advance(); // 'import'
        var path = ParseDottedName();
        ImportClause? clause = null;
        if (_buffer.Check(TokenKind.LBrace)) clause = ParseSelectiveImport();
        else if (_buffer.Check(TokenKind.As)) clause = ParseAliasImport();
        var semi = ExpectSemicolon();
        return new ImportDecl(path, clause, Span.Union(kw.Span, semi.Span));
    }

    private ImportClause ParseSelectiveImport()
    {
        var open = _buffer.Advance(); // '{'
        var names = new List<string>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            names.Add(ExpectName("LYR-PAR0026", "import item"));
            if (!_buffer.Match(TokenKind.Comma)) break;
        }
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close import list");
        return new ImportSelective(names.ToArray(), Span.Union(open.Span, close.Span));
    }

    private ImportClause ParseAliasImport()
    {
        var asKw = _buffer.Advance(); // 'as'
        var nameTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
            $"expected alias name after 'as', got {_buffer.Current.TokenKind}");
        return new ImportAlias(_sm.Slice(nameTok.Span).ToString(), Span.Union(asKw.Span, nameTok.Span));
    }

    // --- Functions (§3.1) ---

    private FunctionDecl ParseFunctionDecl(bool isPublic, Span start, bool isStatic = false)
    {
        var isMut = _buffer.Match(TokenKind.Mut);
        _buffer.Expect(TokenKind.Fn, "LYR-PAR0032", $"expected 'fn', got {_buffer.Current.TokenKind}");
        var name = ExpectName("LYR-PAR0026", "function name");
        var generics = _buffer.Check(TokenKind.Less) ? ParseGenericParams() : [];

        _buffer.Expect(TokenKind.LParen, "LYR-PAR0019", "expected '(' after function name");
        var parameters = ParseParamList();
        _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' after parameters");

        TypeNode? returnType = _buffer.Match(TokenKind.Colon) ? ParseType() : null;

        ThrowsClause? throws = null;
        if (AtContextual("throws"))
        {
            var tk = _buffer.Advance();
            // 'throws' without a type when the body or ';' follows directly.
            TypeNode? thrown = _buffer.Check(TokenKind.LBrace) || _buffer.Check(TokenKind.Semicolon)
                ? null : ParseType();
            throws = new ThrowsClause(thrown, Span.Union(tk.Span, thrown?.Span ?? tk.Span));
        }

        Block? body = null;
        Span end;
        if (_buffer.Check(TokenKind.LBrace))
        {
            body = ParseBlock();
            end = body.Span;
        }
        else
        {
            end = _buffer.Expect(TokenKind.Semicolon, "LYR-PAR0016", "expected '{' or ';' to end function").Span;
        }

        return new FunctionDecl(isPublic, isMut, isStatic, name, generics, parameters, returnType, throws, body,
            Span.Union(start, end));
    }

    private Param[] ParseParamList()
    {
        var parameters = new List<Param>();
        if (_buffer.Check(TokenKind.RParen)) return parameters.ToArray();
        do
        {
            if (_buffer.Check(TokenKind.RParen)) break; // trailing comma
            var start = _buffer.Current.Span;

            // Attributes are post-v1, ON A PARAMETER too. Without this case the parser would read
            // '@noCapture' as a parameter name, then lose the body, and report a message about
            // native declarations to someone writing an attribute. The same message as on a
            // declaration, so both places say the same thing.
            while (_buffer.Check(TokenKind.AtIdentifier))
            {
                _de.Report("LYR-PAR0038", Severity.Error, _buffer.Current.Span,
                    "attributes are not part of v1 (Sprache.md §10); '@noCapture' and the others "
                    + "arrive after v1.0");
                _buffer.Advance();
            }

            var isParams = _buffer.Match(TokenKind.Params);
            var name = ExpectName("LYR-PAR0026", "parameter name");
            _buffer.Expect(TokenKind.Colon, "LYR-PAR0031", "expected ':' after parameter name");
            var type = ParseType();
            Expr? def = _buffer.Match(TokenKind.Equal) ? ParseExpr(0) : null;
            parameters.Add(new Param(isParams, name, type, def, Span.Union(start, def?.Span ?? type.Span)));
        } while (_buffer.Match(TokenKind.Comma));
        return parameters.ToArray();
    }

    // --- Structs / Classes (§3.2/§3.3) ---

    private Decl ParseStructOrClass(bool isPublic, Span start, bool isClass)
    {
        _buffer.Advance(); // 'struct' / 'class'
        var name = ExpectName("LYR-PAR0026", isClass ? "class name" : "struct name");
        var generics = _buffer.Check(TokenKind.Less) ? ParseGenericParams() : [];
        var interfaces = _buffer.Check(TokenKind.ColonColon) ? ParseInterfaceList() : [];
        _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open type body");
        var members = ParseTypeMembers();
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close type body");
        var span = Span.Union(start, close.Span);
        return isClass
            ? new ClassDecl(isPublic, name, generics, interfaces, members, span)
            : new StructDecl(isPublic, name, generics, interfaces, members, span);
    }

    // struct or class body: FieldDecl | FunctionDecl. A field needs a ',', a block-bodied method
    // does not.
    private Decl[] ParseTypeMembers()
    {
        var members = new List<Decl>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            var before = _buffer.Position;
            var member = ParseTypeMember();
            members.Add(member);
            if (_buffer.Position == before) { _buffer.Advance(); continue; } // force progress

            if (_buffer.Check(TokenKind.RBrace)) break;

            // The ',' separates members. After something already closed — a block body ends in
            // '}', a 'static let' in ';' — it is optional; only fields need it, or `a: int b: int`
            // would be a valid line.
            if (member is FunctionDecl { Body: not null } or StaticBindingDecl)
                _buffer.Match(TokenKind.Comma);
            else
                _buffer.Expect(TokenKind.Comma, "LYR-PAR0029", "expected ',' between members");
        }
        return members.ToArray();
    }

    private Decl ParseTypeMember()
    {
        var start = _buffer.Current.Span;

        // Member forms: [pub] [static] [mut] fn …  |  [pub] static let …  |  a field.
        // 'static' precedes 'mut', so the order is unambiguous; 'mut static fn' does not exist.
        // The sema rejects the combination anyway: a static member has no receiver for 'mut' to
        // apply to.
        var isPublic = _buffer.Check(TokenKind.Pub)
                       && _buffer.Peek(1).TokenKind is TokenKind.Fn or TokenKind.Mut or TokenKind.Static
            ? _buffer.Match(TokenKind.Pub)
            : false;

        if (_buffer.Check(TokenKind.Static))
        {
            _buffer.Advance();
            if (_buffer.Check(TokenKind.Let) || _buffer.Check(TokenKind.Var))
            {
                var binding = RequireNamedBinding(ParseBinding(), "static let");
                return new StaticBindingDecl(isPublic, binding, Span.Union(start, binding.Span));
            }
            return ParseFunctionDecl(isPublic, start, isStatic: true);
        }

        if (_buffer.Check(TokenKind.Fn) || _buffer.Check(TokenKind.Mut))
            return ParseFunctionDecl(isPublic, start);

        return ParseField();
    }

    private FieldDecl ParseField()
    {
        var start = _buffer.Current.Span;
        var name = ExpectName("LYR-PAR0026", "field name");
        _buffer.Expect(TokenKind.Colon, "LYR-PAR0031", "expected ':' after field name");
        var type = ParseType();
        Expr? def = _buffer.Match(TokenKind.Equal) ? ParseExpr(0) : null;
        return new FieldDecl(name, type, def, Span.Union(start, def?.Span ?? type.Span));
    }

    // --- Enums (§3.4) ---

    private Decl ParseEnum(bool isPublic, Span start)
    {
        _buffer.Advance(); // 'enum'
        var name = ExpectName("LYR-PAR0026", "enum name");
        var generics = _buffer.Check(TokenKind.Less) ? ParseGenericParams() : [];
        var interfaces = _buffer.Check(TokenKind.ColonColon) ? ParseInterfaceList() : [];
        _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open enum body");

        var variants = new List<EnumVariant>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.Check(TokenKind.Semicolon) && !_buffer.AtEnd)
        {
            var before = _buffer.Position;
            variants.Add(ParseEnumVariant());
            if (_buffer.Position == before) { _buffer.Advance(); continue; }
            if (!_buffer.Match(TokenKind.Comma)) break;
        }

        var methods = new List<FunctionDecl>();
        if (_buffer.Match(TokenKind.Semicolon))
            ParseMethodSequence(methods);

        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close enum body");
        return new EnumDecl(isPublic, name, generics, interfaces, variants.ToArray(), methods.ToArray(),
            Span.Union(start, close.Span));
    }

    private EnumVariant ParseEnumVariant()
    {
        var nameTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
            $"expected enum variant name, got {_buffer.Current.TokenKind}");
        var name = _sm.Slice(nameTok.Span).ToString();

        if (_buffer.Check(TokenKind.LParen)) // tuple variant
        {
            _buffer.Advance();
            var fields = new List<TypeNode>();
            if (!_buffer.Check(TokenKind.RParen))
                do { fields.Add(ParseType()); } while (_buffer.Match(TokenKind.Comma));
            var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close tuple variant");
            return new EnumVariant(name, fields.ToArray(), null, Span.Union(nameTok.Span, close.Span));
        }

        if (_buffer.Check(TokenKind.LBrace)) // struct variant
        {
            _buffer.Advance();
            var fields = new List<FieldDecl>();
            while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
            {
                fields.Add(ParseField());
                if (!_buffer.Match(TokenKind.Comma)) break;
            }
            var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close struct variant");
            return new EnumVariant(name, null, fields.ToArray(), Span.Union(nameTok.Span, close.Span));
        }

        return new EnumVariant(name, null, null, nameTok.Span); // Unit
    }

    // --- Interfaces (§3.5) ---

    private Decl ParseInterface(bool isPublic, Span start)
    {
        _buffer.Advance(); // 'interface'
        var name = ExpectName("LYR-PAR0026", "interface name");
        var generics = _buffer.Check(TokenKind.Less) ? ParseGenericParams() : [];

        // 'interface B :: [A]' — there is no interface inheritance. Without this case the parser
        // runs into a follow-up message about parameter parentheses, unrelated to the cause.
        //
        // The message names the way out, because there is one: two constraints side by side.
        if (_buffer.Check(TokenKind.ColonColon))
        {
            _de.Report("LYR-PAR0039", Severity.Error, _buffer.Current.Span,
                "an interface cannot extend another one — Lyric has no interface inheritance "
                + "(Sprache.md §7). Require both where you need both: '<T :: [A, B]>'");
            // The list is READ and discarded anyway, or the parser would stumble over '[A]' a
            // second time and report two errors for one cause.
            ParseInterfaceList();
        }

        _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open interface body");
        var members = new List<FunctionDecl>();
        ParseMethodSequence(members);
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close interface body");
        return new InterfaceDecl(isPublic, name, generics, members.ToArray(), Span.Union(start, close.Span));
    }

    // --- Extend (§3.6) ---

    private Decl ParseExtend(bool isPublic, Span start)
    {
        _buffer.Advance(); // 'extend'
        var target = ParseType();
        var interfaces = _buffer.Check(TokenKind.ColonColon) ? ParseInterfaceList() : [];
        _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open extend body");
        var methods = new List<FunctionDecl>();
        ParseMethodSequence(methods);
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close extend body");
        return new ExtendDecl(isPublic, target, interfaces, methods.ToArray(), Span.Union(start, close.Span));
    }

    // A sequence of FunctionDecl without separators (interface, extend and enum methods).
    private void ParseMethodSequence(List<FunctionDecl> methods)
    {
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            var before = _buffer.Position;
            var start = _buffer.Current.Span;
            var isPublic = _buffer.Check(TokenKind.Pub)
                           && _buffer.Peek(1).TokenKind is TokenKind.Fn or TokenKind.Mut
                           && _buffer.Match(TokenKind.Pub);
            methods.Add(ParseFunctionDecl(isPublic, start));
            if (_buffer.Position == before) _buffer.Advance(); // force progress
        }
    }

    // --- Global binding & type alias (§2.3) ---

    private Decl ParseGlobalBinding(bool isPublic, Span start)
    {
        if (_buffer.Check(TokenKind.Var))
            _de.Report("LYR-PAR0027", Severity.Error, _buffer.Current.Span,
                "global bindings must be immutable — use 'let', not 'var'");
        var binding = RequireNamedBinding(ParseBinding(), "a module-level 'let'");
        return new GlobalBindingDecl(isPublic, binding, Span.Union(start, binding.Span));
    }

    /// <summary>A constant has ONE name. Destructuring exists for local bindings only: a global
    /// slot is a named thing, and taking several names from one expression would let one
    /// declaration produce several slots.</summary>
    private BindingStmt RequireNamedBinding(Stmt parsed, string what)
    {
        if (parsed is BindingStmt named) return named;

        _de.Report("LYR-PAR0020", Severity.Error, parsed.Span,
            $"{what} needs a single name — destructuring is only allowed on local bindings");

        return new BindingStmt(false, "<error>", null, null, parsed.Span);
    }

    private Decl ParseTypeAlias(bool isPublic, Span start)
    {
        _buffer.Advance(); // contextual 'type'
        var name = ExpectName("LYR-PAR0026", "type alias name");
        _buffer.Expect(TokenKind.Equal, "LYR-PAR0028", "expected '=' in type alias");
        var aliased = ParseType();
        var semi = ExpectSemicolon();
        return new TypeAliasDecl(isPublic, name, aliased, Span.Union(start, semi.Span));
    }

    // --- generics ---

    private GenericParam[] ParseGenericParams()
    {
        _buffer.Advance(); // '<'
        var parameters = new List<GenericParam>();
        do
        {
            var nameTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
                $"expected type parameter, got {_buffer.Current.TokenKind}");
            TypeNode[] constraints = [];
            var end = nameTok.Span;
            if (_buffer.Match(TokenKind.ColonColon)) // T :: [I1, I2]
            {
                _buffer.Expect(TokenKind.LBracket, "LYR-PAR0030", "expected '[' after '::' in constraint");
                var cs = new List<TypeNode>();
                do { cs.Add(ParseType()); } while (_buffer.Match(TokenKind.Comma));
                end = _buffer.Expect(TokenKind.RBracket, "LYR-PAR0004", "expected ']' to close constraint list").Span;
                constraints = cs.ToArray();
            }
            parameters.Add(new GenericParam(_sm.Slice(nameTok.Span).ToString(), constraints,
                Span.Union(nameTok.Span, end)));
        } while (_buffer.Match(TokenKind.Comma));
        // A generic parameter list always closes with a plain '>', never a '>>'.
        _buffer.Expect(TokenKind.Greater, "LYR-PAR0009", "expected '>' to close type parameters");
        return parameters.ToArray();
    }

    private TypeNode[] ParseInterfaceList()
    {
        _buffer.Advance(); // '::'
        _buffer.Expect(TokenKind.LBracket, "LYR-PAR0030", "expected '[' after '::'");
        var interfaces = new List<TypeNode>();
        do { interfaces.Add(ParseType()); } while (_buffer.Match(TokenKind.Comma) && !_buffer.Check(TokenKind.RBracket));
        _buffer.Expect(TokenKind.RBracket, "LYR-PAR0004", "expected ']' to close interface list");
        return interfaces.ToArray();
    }

    // --- Shared helpers ---

    private string[] ParseDottedName()
    {
        var segments = new List<string> { ExpectName("LYR-PAR0026", "module path segment") };
        while (_buffer.Match(TokenKind.Dot))
            segments.Add(ExpectName("LYR-PAR0026", "module path segment"));
        return segments.ToArray();
    }

    private string ExpectName(string code, string what)
    {
        var tok = _buffer.Expect(TokenKind.Identifier, code, $"expected {what}, got {_buffer.Current.TokenKind}");
        return _sm.Slice(tok.Span).ToString();
    }

    /// <summary>A contextual keyword: an identifier with exactly this text (for example 'throws' or
    /// 'type').</summary>
    private bool AtContextual(string word) =>
        _buffer.Check(TokenKind.Identifier) && _sm.Slice(_buffer.Current.Span).SequenceEqual(word);
}
