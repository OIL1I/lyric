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

        // Attributes at the top of the file bind to the HEADER when one follows, and to the first
        // declaration otherwise. The distinction has to fall here: once the header is parsed there
        // is no second place where module attributes could stand.
        var leading = ParseAttributeList();
        AttributeNode[] moduleAttributes = [];
        AttributeNode[] pending = [];
        if (_buffer.Check(TokenKind.Module)) moduleAttributes = leading;
        else pending = leading;

        ModulePath? header = _buffer.Check(TokenKind.Module) ? ParseModuleHeader() : null;

        var decls = new List<Decl>();
        while (!_buffer.AtEnd)
        {
            var before = _buffer.Position;
            decls.Add(ParseTopLevelDecl(pending));
            pending = [];
            if (_buffer.Position == before) _buffer.Advance(); // force progress
        }

        // Attributes with no declaration to bind to: at EOF the list would silently vanish.
        if (pending.Length > 0)
            _de.Report("LYR-PAR0042", Severity.Error, pending[0].Span,
                "an attribute must be followed by the declaration it applies to");

        var end = decls.Count > 0 ? decls[^1].Span : (header?.Span ?? start);
        return new Module(header, decls.ToArray(), Span.Union(start, end)) { Attributes = moduleAttributes };
    }

    /// <summary>
    /// Zero or more attributes: <c>@Name</c> or <c>@Name { field = expr, … }</c>, each an
    /// optionally dotted path. The VALUES are parsed as expressions; that they must be literals is
    /// a semantic rule, so the message can name the offending expression instead of refusing to
    /// read it.
    /// </summary>
    private AttributeNode[] ParseAttributeList()
    {
        if (!_buffer.Check(TokenKind.AtIdentifier)) return [];

        var attributes = new List<AttributeNode>();
        while (_buffer.Check(TokenKind.AtIdentifier))
        {
            var at = _buffer.Advance();
            var path = new List<string> { _sm.Slice(at.Span)[1..].ToString() }; // strip the '@'
            var pathEnd = at.Span;

            // The single-segment case: the name is the token minus its '@'.
            var nameSpan = new Span(at.Span.File, at.Span.Start + 1, at.Span.End);
            while (_buffer.Match(TokenKind.Dot))
            {
                var segment = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
                    $"expected attribute name, got {_buffer.Current.TokenKind}");
                path.Add(_sm.Slice(segment.Span).ToString());
                nameSpan = segment.Span;
                pathEnd = segment.Span;
            }

            var fields = new List<StructInitField>();
            var end = pathEnd;
            if (_buffer.Check(TokenKind.LBrace))
            {
                _buffer.Advance();
                while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
                {
                    var nameTok = _buffer.Expect(TokenKind.Identifier, "LYR-PAR0026",
                        $"expected field name, got {_buffer.Current.TokenKind}");
                    _buffer.Expect(TokenKind.Equal, "LYR-PAR0037",
                        "expected '=' in attribute arguments (':' is only for types)");
                    var value = ParseSubExpr();
                    fields.Add(new StructInitField(_sm.Slice(nameTok.Span).ToString(), value,
                        Span.Union(nameTok.Span, value.Span)) { NameSpan = nameTok.Span });
                    if (!_buffer.Match(TokenKind.Comma)) break;
                }
                end = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018",
                    "expected '}' to close attribute arguments").Span;
            }

            attributes.Add(new AttributeNode(path.ToArray(), fields.ToArray(),
                Span.Union(at.Span, end))
                { PathSpan = Span.Union(at.Span, pathEnd), NameSpan = nameSpan });
        }
        return attributes.ToArray();
    }

    private ModulePath ParseModuleHeader()
    {
        var kw = _buffer.Advance(); // 'module'
        var segments = ParseDottedName();
        var semi = ExpectSemicolon();
        return new ModulePath(segments, Span.Union(kw.Span, semi.Span));
    }

    private Decl ParseTopLevelDecl(AttributeNode[] pending)
    {
        // Attributes precede 'pub'. The ones handed down come from the top of a header-less file.
        var attributes = pending;
        if (_buffer.Check(TokenKind.AtIdentifier))
        {
            var parsed = ParseAttributeList();
            attributes = pending.Length == 0 ? parsed : [.. pending, .. parsed];
        }

        var start = attributes.Length > 0 ? attributes[0].Span : _buffer.Current.Span;

        if (_buffer.Check(TokenKind.Import))
        {
            RejectAttributes(attributes, "an import");
            return ParseImport();
        }

        var isPublic = _buffer.Match(TokenKind.Pub);

        switch (_buffer.Current.TokenKind)
        {
            case TokenKind.Mut:
            case TokenKind.Fn:
                return ParseFunctionDecl(isPublic, start) with { Attributes = attributes };
            case TokenKind.Struct:
                return WithAttributes(ParseStructOrClass(isPublic, start, isClass: false), attributes);
            case TokenKind.Class:
                return WithAttributes(ParseStructOrClass(isPublic, start, isClass: true), attributes);
            case TokenKind.Enum:
                return WithAttributes(ParseEnum(isPublic, start), attributes);
            case TokenKind.Interface:
                RejectAttributes(attributes, "an interface");
                return ParseInterface(isPublic, start);
            case TokenKind.Extend:
                RejectAttributes(attributes, "an extend block");
                return ParseExtend(isPublic, start);
            case TokenKind.Let:
            case TokenKind.Var:
                RejectAttributes(attributes, "a global binding");
                return ParseGlobalBinding(isPublic, start);

            default:
                if (AtContextual("type"))
                {
                    RejectAttributes(attributes, "a type alias");
                    return ParseTypeAlias(isPublic, isOpaque: false, start);
                }
                // 'opaque type X = int;' — contextual like 'type' itself: neither word is a
                // keyword, so neither is taken from anyone's identifiers.
                if (AtContextual("opaque") && PeekContextual(1, "type"))
                {
                    RejectAttributes(attributes, "a type alias");
                    _buffer.Advance(); // 'opaque'
                    return ParseTypeAlias(isPublic, isOpaque: true, start);
                }
                if (attributes.Length > 0)
                {
                    // '@Component' followed by something that opens no declaration. The list would
                    // vanish silently; instead the message says what an attribute may precede.
                    _de.Report("LYR-PAR0042", Severity.Error, attributes[0].Span,
                        "an attribute must be followed by the declaration it applies to");
                    var stop = SynchronizeTopLevel();
                    return new ErrorDecl(Span.Union(start, stop));
                }
                _de.Report("LYR-PAR0025", Severity.Error, _buffer.Current.Span,
                    $"expected a declaration, got {_buffer.Current.TokenKind}");
                var end = SynchronizeTopLevel(); // skip to the next declaration start, so only ONE error
                return new ErrorDecl(Span.Union(start, end));
        }
    }

    /// <summary>An attribute may precede a function, a struct, a class, an enum or the module
    /// header. Everywhere else the list is reported and dropped; the declaration itself parses
    /// on unharmed.</summary>
    private void RejectAttributes(AttributeNode[] attributes, string what)
    {
        if (attributes.Length == 0) return;
        _de.Report("LYR-PAR0042", Severity.Error, attributes[0].Span,
            $"an attribute cannot sit on {what} — a function, a struct, a class, an enum, "
            + "a member of one, or the module header carries one");
    }

    private static Decl WithAttributes(Decl decl, AttributeNode[] attributes) => decl switch
    {
        _ when attributes.Length == 0 => decl,
        StructDecl s => s with { Attributes = attributes },
        ClassDecl c => c with { Attributes = attributes },
        EnumDecl e => e with { Attributes = attributes },
        _ => decl, // recovery produced an ErrorDecl; the list is lost with the declaration
    };

    private static Decl WithMemberAttributes(Decl member, AttributeNode[] attributes) => member switch
    {
        _ when attributes.Length == 0 => member,
        FunctionDecl f => f with { Attributes = attributes },
        StaticBindingDecl sb => sb with { Attributes = attributes },
        FieldDecl fd => fd with { Attributes = attributes },
        _ => member, // recovery; the list is lost with the member
    };

    /// <summary>Recovery: consumes tokens up to the next plausible declaration start (a keyword,
    /// the contextual 'type', or EOF). Returns the span of the last skipped token.</summary>
    private Span SynchronizeTopLevel()
    {
        var span = _buffer.Current.Span;
        while (!_buffer.AtEnd)
        {
            if (_buffer.Current.TokenKind is TokenKind.Module or TokenKind.Import or TokenKind.Pub
                or TokenKind.Fn or TokenKind.Mut or TokenKind.Struct or TokenKind.Class or TokenKind.Enum
                or TokenKind.Interface or TokenKind.Extend or TokenKind.Let or TokenKind.Var
                or TokenKind.AtIdentifier)
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
        var spans = new List<Span>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            var (name, span) = ExpectNamed("LYR-PAR0026", "import item");
            names.Add(name);
            spans.Add(span);
            if (!_buffer.Match(TokenKind.Comma)) break;
        }
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close import list");
        return new ImportSelective(names.ToArray(), Span.Union(open.Span, close.Span))
            { NameSpans = spans.ToArray() };
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
        var name = ExpectNamed("LYR-PAR0026", "function name");
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

        return new FunctionDecl(isPublic, isMut, isStatic, name.Name, generics, parameters, returnType, throws, body,
            Span.Union(start, end)) { NameSpan = name.Span };
    }

    private Param[] ParseParamList()
    {
        var parameters = new List<Param>();
        if (_buffer.Check(TokenKind.RParen)) return parameters.ToArray();
        do
        {
            if (_buffer.Check(TokenKind.RParen)) break; // trailing comma
            var start = _buffer.Current.Span;

            // An attribute may not sit ON A PARAMETER. Without this case the parser would read
            // '@noCapture' as a parameter name, then lose the body, and report a message about
            // native declarations to someone writing an attribute.
            while (_buffer.Check(TokenKind.AtIdentifier))
            {
                _de.Report("LYR-PAR0038", Severity.Error, _buffer.Current.Span,
                    "an attribute cannot sit on a parameter — only a function, a struct, a class, "
                    + "an enum or the module header carries one");
                _buffer.Advance();
            }

            var isParams = _buffer.Match(TokenKind.Params);
            var name = ExpectNamed("LYR-PAR0026", "parameter name");
            _buffer.Expect(TokenKind.Colon, "LYR-PAR0031", "expected ':' after parameter name");
            var type = ParseType();
            Expr? def = _buffer.Match(TokenKind.Equal) ? ParseExpr(0) : null;
            parameters.Add(new Param(isParams, name.Name, type, def, Span.Union(start, def?.Span ?? type.Span))
                { NameSpan = name.Span });
        } while (_buffer.Match(TokenKind.Comma));
        return parameters.ToArray();
    }

    // --- Structs / Classes (§3.2/§3.3) ---

    private Decl ParseStructOrClass(bool isPublic, Span start, bool isClass)
    {
        _buffer.Advance(); // 'struct' / 'class'
        var name = ExpectNamed("LYR-PAR0026", isClass ? "class name" : "struct name");
        var generics = _buffer.Check(TokenKind.Less) ? ParseGenericParams() : [];
        var interfaces = _buffer.Check(TokenKind.ColonColon) ? ParseInterfaceList() : [];
        _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open type body");
        var members = ParseTypeMembers();
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close type body");
        var span = Span.Union(start, close.Span);
        return isClass
            ? new ClassDecl(isPublic, name.Name, generics, interfaces, members, span) { NameSpan = name.Span }
            : new StructDecl(isPublic, name.Name, generics, interfaces, members, span) { NameSpan = name.Span };
    }

    // struct or class body: FieldDecl | FunctionDecl. A field needs a ',', a block-bodied method
    // does not.
    private Decl[] ParseTypeMembers()
    {
        var members = new List<Decl>();
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            // Since 2.1 a member CARRIES its attribute list — the sema admits only the
            // row-less '@Deprecated' there, but which attributes exist where is its call,
            // not the grammar's.
            var attributes = _buffer.Check(TokenKind.AtIdentifier) ? ParseAttributeList() : [];

            var before = _buffer.Position;
            var member = WithMemberAttributes(ParseTypeMember(), attributes);
            members.Add(member);
            if (_buffer.Position == before) { _buffer.Advance(); continue; } // force progress

            if (_buffer.Check(TokenKind.RBrace)) break;

            // The ',' separates members, and only a FIELD needs it: without the rule `a: int b: int`
            // would be a valid line. Everything else has already closed itself — a block body ends
            // in '}', a bodiless method and a 'static let' end in ';'.
            if (member is FunctionDecl or StaticBindingDecl)
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
        var name = ExpectNamed("LYR-PAR0026", "field name");
        _buffer.Expect(TokenKind.Colon, "LYR-PAR0031", "expected ':' after field name");
        var type = ParseType();
        Expr? def = _buffer.Match(TokenKind.Equal) ? ParseExpr(0) : null;
        return new FieldDecl(name.Name, type, def, Span.Union(start, def?.Span ?? type.Span))
            { NameSpan = name.Span };
    }

    // --- Enums (§3.4) ---

    private Decl ParseEnum(bool isPublic, Span start)
    {
        _buffer.Advance(); // 'enum'
        var name = ExpectNamed("LYR-PAR0026", "enum name");
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
            ParseMethodSequence(methods, allowStatic: true);

        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close enum body");
        return new EnumDecl(isPublic, name.Name, generics, interfaces, variants.ToArray(), methods.ToArray(),
            Span.Union(start, close.Span)) { NameSpan = name.Span };
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
            return new EnumVariant(name, fields.ToArray(), null, Span.Union(nameTok.Span, close.Span))
                { NameSpan = nameTok.Span };
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
            return new EnumVariant(name, null, fields.ToArray(), Span.Union(nameTok.Span, close.Span))
                { NameSpan = nameTok.Span };
        }

        return new EnumVariant(name, null, null, nameTok.Span) { NameSpan = nameTok.Span }; // Unit
    }

    // --- Interfaces (§3.5) ---

    private Decl ParseInterface(bool isPublic, Span start)
    {
        _buffer.Advance(); // 'interface'
        var name = ExpectNamed("LYR-PAR0026", "interface name");
        var generics = _buffer.Check(TokenKind.Less) ? ParseGenericParams() : [];

        // 'interface B :: [A]' — B implies its parents: whoever conforms to B conforms to them
        // too. What the list does NOT do the sema explains where it matters; here it is just a
        // type list, shaped like the one on structs. (LYR-PAR0039 rejected this until v1.13.)
        var interfaces = _buffer.Check(TokenKind.ColonColon) ? ParseInterfaceList() : [];

        _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open interface body");
        var members = new List<FunctionDecl>();
        ParseMethodSequence(members, allowStatic: false, allowAttributes: false);
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close interface body");
        return new InterfaceDecl(isPublic, name.Name, generics, interfaces, members.ToArray(), Span.Union(start, close.Span))
            { NameSpan = name.Span };
    }

    // --- Extend (§3.6) ---

    private Decl ParseExtend(bool isPublic, Span start)
    {
        _buffer.Advance(); // 'extend'
        var target = ParseType();
        var interfaces = _buffer.Check(TokenKind.ColonColon) ? ParseInterfaceList() : [];
        _buffer.Expect(TokenKind.LBrace, "LYR-PAR0017", "expected '{' to open extend body");
        var methods = new List<FunctionDecl>();
        ParseMethodSequence(methods, allowStatic: true);
        var close = _buffer.Expect(TokenKind.RBrace, "LYR-PAR0018", "expected '}' to close extend body");
        return new ExtendDecl(isPublic, target, interfaces, methods.ToArray(), Span.Union(start, close.Span));
    }

    /// <summary>
    /// A sequence of FunctionDecl without separators (interface, extend and enum methods).
    ///
    /// <para>The modifier order is the one of FunctionDecl: 'pub' and 'static' are read here, 'mut'
    /// and 'fn' by ParseFunctionDecl.</para>
    /// </summary>
    /// <param name="allowStatic">False in an interface body, where a member is dispatched on a
    /// receiver and a static one has none.</param>
    /// <param name="allowAttributes">False in an interface body: deprecating an abstract member
    /// would raise conformance questions (do implementations inherit the clock?) nobody has
    /// answered — refused until someone needs it. Extend and enum methods carry attributes
    /// since 2.1, the sema admitting only '@Deprecated'.</param>
    private void ParseMethodSequence(List<FunctionDecl> methods, bool allowStatic,
        bool allowAttributes = true)
    {
        while (!_buffer.Check(TokenKind.RBrace) && !_buffer.AtEnd)
        {
            var attributes = _buffer.Check(TokenKind.AtIdentifier) ? ParseAttributeList() : [];
            if (attributes.Length > 0 && !allowAttributes)
            {
                RejectAttributes(attributes, "an interface member");
                attributes = [];
            }

            var before = _buffer.Position;
            var start = _buffer.Current.Span;
            var isPublic = _buffer.Check(TokenKind.Pub)
                           && _buffer.Peek(1).TokenKind is TokenKind.Fn or TokenKind.Mut or TokenKind.Static
                           && _buffer.Match(TokenKind.Pub);

            var isStatic = false;
            if (_buffer.Check(TokenKind.Static))
            {
                var kw = _buffer.Advance();

                // 'static let' is a StaticBinding, and that is a member of a struct or class body
                // only. Reported here rather than left to ParseFunctionDecl, which would fail on
                // the missing 'fn' and report three times about something else.
                if (_buffer.Check(TokenKind.Let) || _buffer.Check(TokenKind.Var))
                {
                    _de.Report("LYR-PAR0040", Severity.Error, kw.Span,
                        "a 'static let' is a member of a struct or class body only");
                    SkipMember();
                    continue;
                }

                if (!allowStatic)
                    _de.Report("LYR-PAR0041", Severity.Error, kw.Span,
                        "an interface member cannot be 'static' — it is dispatched on a receiver, "
                        + "and a static member has none. Declare it on the implementing type");

                // Read on either way: the rest of the member is well formed, and stopping here
                // would report every following one as well.
                isStatic = allowStatic;
            }

            methods.Add(ParseFunctionDecl(isPublic, start, isStatic) with { Attributes = attributes });
            if (_buffer.Position == before) _buffer.Advance(); // force progress
        }
    }

    /// <summary>Recovery inside a member sequence: consumes up to the end of the member, so one
    /// rejected member gives one message.</summary>
    private void SkipMember()
    {
        var depth = 0;
        while (!_buffer.AtEnd)
        {
            var kind = _buffer.Current.TokenKind;
            if (kind == TokenKind.LBrace) depth++;
            else if (kind == TokenKind.RBrace)
            {
                if (depth == 0) return; // the body's own '}'
                depth--;
            }
            else if (kind == TokenKind.Semicolon && depth == 0)
            {
                _buffer.Advance();
                return;
            }

            _buffer.Advance();
            if (depth == 0 && kind == TokenKind.RBrace) return; // the member's block ended
        }
    }

    // --- Global binding & type alias (§2) ---

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

        // The name is missing rather than wrong, so its span is the empty one at the start of what
        // stood there: inside the statement, which is what the containment rule asks for.
        return new BindingStmt(false, "<error>", null, null, parsed.Span)
            { NameSpan = parsed.Span with { End = parsed.Span.Start } };
    }

    private Decl ParseTypeAlias(bool isPublic, bool isOpaque, Span start)
    {
        _buffer.Advance(); // contextual 'type'
        var name = ExpectNamed("LYR-PAR0026", "type alias name");
        _buffer.Expect(TokenKind.Equal, "LYR-PAR0028", "expected '=' in type alias");
        var aliased = ParseType();
        var semi = ExpectSemicolon();
        return new TypeAliasDecl(isPublic, isOpaque, name.Name, aliased, Span.Union(start, semi.Span))
            { NameSpan = name.Span };
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
                Span.Union(nameTok.Span, end)) { NameSpan = nameTok.Span });
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

    private string ExpectName(string code, string what) => ExpectNamed(code, what).Name;

    /// <summary>
    /// The name and the span it stands at.
    ///
    /// <para>On failure <see cref="TokenBuffer.Expect"/> returns the offending token without
    /// consuming it, so the span is the position where the name was expected — inside the
    /// declaration being parsed, which keeps the containment every <see cref="INamedDecl"/>
    /// promises.</para>
    /// </summary>
    private (string Name, Span Span) ExpectNamed(string code, string what)
    {
        var tok = _buffer.Expect(TokenKind.Identifier, code, $"expected {what}, got {_buffer.Current.TokenKind}");
        return (_sm.Slice(tok.Span).ToString(), tok.Span);
    }

    /// <summary>A contextual keyword: an identifier with exactly this text (for example 'throws' or
    /// 'type').</summary>
    private bool AtContextual(string word) =>
        _buffer.Check(TokenKind.Identifier) && _sm.Slice(_buffer.Current.Span).SequenceEqual(word);

    private bool PeekContextual(int offset, string word)
    {
        var token = _buffer.Peek(offset);
        return token.TokenKind == TokenKind.Identifier && _sm.Slice(token.Span).SequenceEqual(word);
    }
}
