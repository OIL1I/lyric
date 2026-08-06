using Lyric.AST;
using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Parsing;

/// <summary>
/// Modul- und Deklarations-Parser (Sprache.md §2/§3), Recursive-Descent.
///
/// Kontextuelle Keywords: <c>throws</c> und <c>type</c> stehen NICHT in der
/// Keyword-Liste (§1.4) — der Lexer liefert sie als Identifier, hier werden sie
/// nur an ihrer Position erkannt.
///
/// Member-Trennung (bewusst, weicht leicht von der EBNF ab — siehe Notiz im Code):
///   struct/class : Field braucht ',', block-bodied Methode nicht (matcht §3.2-Beispiel)
///   enum-Varianten: ',' getrennt
///   interface/extend/enum-Methoden: Sequenz ohne Trenner
/// </summary>
public sealed partial class Parser
{
    /// <summary>Slice-3-Einstieg: ganze Datei (optionaler Modul-Header + Top-Level-Decls).</summary>
    public Module ParseModule()
    {
        var start = _buffer.Current.Span;
        ModulePath? header = _buffer.Check(TokenKind.Module) ? ParseModuleHeader() : null;

        var decls = new List<Decl>();
        while (!_buffer.AtEnd)
        {
            var before = _buffer.Position;
            decls.Add(ParseTopLevelDecl());
            if (_buffer.Position == before) _buffer.Advance(); // Fortschritt erzwingen
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
            // Attribute sind post-v1 (Sprache.md §10). Die Syntax bleibt reserviert — der Lexer
            // erkennt '@name' weiterhin —, aber an einer Deklaration gibt es keinen Platz dafuer,
            // und §2.3 hatte nie einen. Eigene Meldung statt "expected a declaration": wer
            // '@test' schreibt, hat sich nicht vertippt, sondern etwas erwartet, das es gibt —
            // nur nicht in v1.
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
                var end = SynchronizeTopLevel(); // bis zum nächsten Decl-Start überspringen → nur EIN Fehler
                return new ErrorDecl(Span.Union(start, end));
        }
    }

    /// <summary>Recovery: konsumiert Tokens bis zum nächsten plausiblen Decl-Anfang
    /// (Decl-Keyword, contextuelles 'type' oder EOF). Liefert den Span des zuletzt
    /// übersprungenen Tokens.</summary>
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

    // --- Imports (§2.2) ---

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
            // 'throws' ohne Typ, wenn direkt der Body/';' folgt.
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

    // struct/class-Body: FieldDecl | FunctionDecl. Field braucht ',', block-bodied
    // Methode nicht (matcht §3.2-Beispiel; EBNF sagt strikt ',' — Inkonsistenz geflaggt).
    private Decl[] ParseTypeMembers()
    {
        var members = new List<Decl>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            var before = _buffer.Position;
            var member = ParseTypeMember();
            members.Add(member);
            if (_buffer.Position == before) { _buffer.Advance(); continue; } // Fortschritt erzwingen

            if (_buffer.Check(TokenKind.RBrace)) break;

            // Das ',' trennt Member. Nach etwas, das schon selbst geschlossen ist — ein Block-Body
            // endet auf '}', ein 'static let' auf ';' — ist es optional; nur Felder brauchen es
            // zwingend, sonst wäre `a: int b: int` eine gültige Zeile.
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

        // Member-Formen: [pub] [static] [mut] fn …  |  [pub] static let …  |  Feld.
        // 'static' steht vor 'mut', damit die Reihenfolge eindeutig ist — 'mut static fn' gibt es
        // nicht. Sema lehnt die Kombination ohnehin ab (ADR-014: ein static-Member hat keinen
        // Empfänger, den 'mut' betreffen könnte).
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

        if (_buffer.Check(TokenKind.LParen)) // Tuple-Variante
        {
            _buffer.Advance();
            var fields = new List<TypeNode>();
            if (!_buffer.Check(TokenKind.RParen))
                do { fields.Add(ParseType()); } while (_buffer.Match(TokenKind.Comma));
            var close = _buffer.Expect(TokenKind.RParen, "LYR-PAR0008", "expected ')' to close tuple variant");
            return new EnumVariant(name, fields.ToArray(), null, Span.Union(nameTok.Span, close.Span));
        }

        if (_buffer.Check(TokenKind.LBrace)) // Struct-Variante
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

    // Sequenz von FunctionDecl ohne Trenner (interface/extend/enum-Methoden).
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
            if (_buffer.Position == before) _buffer.Advance(); // Fortschritt erzwingen
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

    /// <summary>
    /// Eine Konstante hat <b>einen</b> Namen. Destructuring gibt es nur fuer lokale Bindungen: ein
    /// globaler Slot ist eine benannte Sache (P5c), und mehrere Namen aus einem Ausdruck zu
    /// ziehen hiesse, mehrere Slots aus einer Deklaration entstehen zu lassen.
    /// </summary>
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

    // --- Generics (§3.1) ---

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
        // Generic-Param-Listen schließen immer mit einem einfachen '>' (kein '>>').
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

    /// <summary>Kontextuelles Keyword: Identifier mit exakt diesem Text (z.B. 'throws', 'type').</summary>
    private bool AtContextual(string word) =>
        _buffer.Check(TokenKind.Identifier) && _sm.Slice(_buffer.Current.Span).SequenceEqual(word);
}
