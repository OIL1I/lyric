using Lyric.AST;
using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Formatting;

/// <summary>
/// Builds the <see cref="Doc"/> of a parsed module: one method per node shape, none of them
/// measuring a column.
///
/// <para>Two facts about this AST drive the design. LITERALS have lost their spelling — an
/// <c>IntLiteralExpr</c> holds a <c>ulong</c>, not the <c>0xFF</c> or <c>1_000</c> someone
/// wrote — so every literal (f-strings included) is printed from its source span, verbatim.
/// And there are NO parenthesis nodes: <c>(a + b) * c</c> is plain nesting, so the printer
/// re-derives parentheses from the precedence table of Grammar.md §6.1. A group whose written
/// parentheses were redundant loses them; one that needs them gets them back — the reparse
/// invariant of the test suite is what holds that honest.</para>
///
/// <para>COMMENTS travel beside the tree, not in it: a single source-ordered cursor over the
/// merged comment stream (line, block and doc comments alike), consumed at the sequence
/// boundaries — between statements, declarations, members and match arms. A comment on the same
/// line as the element before it stays trailing; every other one stands on its own line, and
/// the blank lines around it follow the source. A comment INSIDE an expression is not lost but
/// surfaces at the next boundary — line-level fidelity, the gofmt trade.</para>
///
/// <para>BLANK LINES between elements are the user's, capped at one — except where the style
/// has an opinion: a blank always follows the module header and separates top-level
/// declarations and members with bodies, and imports sit together whatever the source did.
/// Error nodes throw: the formatter runs only on files the parser accepted.</para>
/// </summary>
public sealed class AstFormatter
{
    /// <summary>What separates two adjacent elements of a sequence.</summary>
    private enum Air
    {
        /// <summary>A blank line, whatever the source says.</summary>
        Forced,

        /// <summary>A blank line exactly when the source has one (or more).</summary>
        User,

        /// <summary>No blank line, whatever the source says.</summary>
        Never,
    }

    private readonly string _source;
    private readonly IReadOnlyList<Trivia> _comments;
    private readonly int[] _lineStarts;
    private int _next;   // the comment cursor: everything before it is already printed
    private int _lastEnd = -1; // source end of the last element printed at line level

    private AstFormatter(string source, IReadOnlyList<Trivia> comments)
    {
        _source = source;
        _comments = comments;

        var starts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
            if (source[i] == '\n')
                starts.Add(i + 1);
        _lineStarts = starts.ToArray();
    }

    /// <param name="comments">Every comment of the file, source-ordered — the lexer's trivia
    /// plus the doc-comment tokens, merged by the caller.</param>
    public static Doc Build(Module module, string source, IReadOnlyList<Trivia> comments) =>
        new AstFormatter(source, comments).ModuleDoc(module);

    // ------------------------------------------------------------------ lines and comments

    private int LineOf(int offset)
    {
        var index = Array.BinarySearch(_lineStarts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    private bool BlankBetween(int endOffset, int startOffset) =>
        LineOf(startOffset) - LineOf(endOffset) >= 2;

    private bool AnyCommentBefore(int position) =>
        _next < _comments.Count && _comments[_next].Span.Start < position;

    // A multi-line block comment may carry CRLF inside; the output contract is LF-only.
    private string CommentText(Trivia comment) =>
        _source.Substring(comment.Span.Start, comment.Span.Length)
            .Replace("\r\n", "\n").TrimEnd('\r');

    /// <summary>A comment on the line the previous element ended on trails it after one
    /// space; several chain. Bounded, so a comment behind the container's closing brace is not
    /// pulled inside.</summary>
    private void EmitTrailingComments(List<Doc> parts, int limit)
    {
        while (AnyCommentBefore(limit) && _lastEnd >= 0
               && LineOf(_comments[_next].Span.Start) == LineOf(_lastEnd))
        {
            var comment = _comments[_next++];
            parts.Add(Doc.From(" " + CommentText(comment)));
            _lastEnd = comment.Span.End;
        }
    }

    /// <summary>One comment on its own line, separated by what <paramref name="air"/> and the
    /// source agree on.</summary>
    private void EmitOwnLine(List<Doc> parts, Trivia comment, Air air)
    {
        if (parts.Count > 0)
        {
            parts.Add(Doc.NewLine);
            if (air == Air.Forced
                || (air == Air.User && _lastEnd >= 0 && BlankBetween(_lastEnd, comment.Span.Start)))
                parts.Add(Doc.NewLine);
        }

        parts.Add(Doc.From(CommentText(comment)));
        _lastEnd = comment.Span.End;
    }

    /// <summary>
    /// The sequence engine every line-level container runs on.
    ///
    /// <para>Per item: the previous line's trailing comments, then the pending own-line
    /// comments, then the item. The pending comments split into floating paragraphs, which
    /// keep the user's blank lines, and the group GLUED to the item — no blank line anywhere
    /// between it and the item — which travels with it: the air rule between two declarations
    /// applies before the doc comment of the second, not between the comment and its
    /// declaration. The container's tail comments follow the last item.</para>
    /// </summary>
    private Doc SequenceDoc<T>(IReadOnlyList<T> items, Func<T, Doc> print,
        Func<T, T, Air> airOf, int containerEnd) where T : Node
    {
        var parts = new List<Doc>();
        T? previous = default;

        foreach (var item in items)
        {
            EmitTrailingComments(parts, item.Span.Start);

            var pending = new List<Trivia>();
            while (AnyCommentBefore(item.Span.Start)) pending.Add(_comments[_next++]);

            // The glued suffix: walk back from the item while no blank line intervenes.
            var glued = pending.Count;
            var reach = item.Span.Start;
            while (glued > 0 && !BlankBetween(pending[glued - 1].Span.End, reach))
                reach = pending[--glued].Span.Start;

            for (var i = 0; i < glued; i++) EmitOwnLine(parts, pending[i], Air.User);

            var air = previous is not null ? airOf(previous, item) : Air.User;
            if (glued < pending.Count)
            {
                // A comment heading the unit marks a boundary the user drew: a pair that would
                // collapse (imports) keeps the user's blank line instead. Forced stays forced —
                // the doc comment of a declaration carries its declaration's air.
                if (air == Air.Never) air = Air.User;
                EmitOwnLine(parts, pending[glued], air);
                for (var i = glued + 1; i < pending.Count; i++)
                    EmitOwnLine(parts, pending[i], Air.User);
                air = Air.User; // the unit's separation is spent on its first comment
            }

            if (parts.Count > 0)
            {
                parts.Add(Doc.NewLine);
                if (air == Air.Forced
                    || (air == Air.User && _lastEnd >= 0 && BlankBetween(_lastEnd, item.Span.Start)))
                    parts.Add(Doc.NewLine);
            }

            parts.Add(print(item));
            _lastEnd = item.Span.End;
            previous = item;
        }

        EmitTrailingComments(parts, containerEnd);
        while (AnyCommentBefore(containerEnd)) EmitOwnLine(parts, _comments[_next++], Air.User);
        return new Doc.Concat(parts);
    }

    /// <summary>A braced, indented body around a sequence — or <c>{ }</c> when there is truly
    /// nothing, comments included, to put into it.</summary>
    private Doc BracedDoc(Doc head, bool empty, Func<Doc> body)
    {
        if (empty) return Doc.Of(head, Doc.From("{ }"));
        return Doc.Of(head, Doc.From("{"), Doc.IndentOf(Doc.NewLine, body()),
            Doc.NewLine, Doc.From("}"));
    }

    // ------------------------------------------------------------------ module and declarations

    private Doc ModuleDoc(Module module)
    {
        // Attributes, header and declarations in one sequence: the air rule knows the header
        // gets a blank after it, imports sit together, and everything else breathes.
        var items = new List<Node>();
        items.AddRange(module.Attributes);
        if (module.Header is { } header) items.Add(header);
        items.AddRange(module.Declarations);

        var content = SequenceDoc(items, ItemDoc, ModuleAir, _source.Length);
        return Doc.Of(content, Doc.NewLine); // the trailing newline of every formatted file

        Doc ItemDoc(Node item) => item switch
        {
            AttributeNode a => AttributeDoc(a),
            ModulePath h => Doc.From($"module {string.Join(".", h.Segments)};"),
            Decl d => DeclDoc(d),
            _ => throw new InternalCompilationException($"unreachable: {item.GetType().Name} at top level"),
        };
    }

    private static Air ModuleAir(Node previous, Node next) => (previous, next) switch
    {
        (ModulePath, _) => Air.Forced,
        (AttributeNode, _) => Air.Never,             // an attribute belongs to what follows it
        (ImportDecl, ImportDecl) => Air.Never,       // imports form one contiguous head
        // Two bodiless declarations group like interface members do: the standard library's
        // native declarations come in blocks a forced blank would tear apart.
        (FunctionDecl { Body: null }, FunctionDecl { Body: null }) => Air.User,
        (Decl, Decl) => Air.Forced,
        _ => Air.User,
    };

    private Doc DeclDoc(Decl decl) => decl switch
    {
        ImportDecl d => ImportDoc(d),
        FunctionDecl d => FunctionDoc(d),
        StructDecl d => TypeBodyDoc(Attributes(d.Attributes), d.IsPublic, "struct", d.Name,
            d.Generics, d.Interfaces, d.Members, d.Span),
        ClassDecl d => TypeBodyDoc(Attributes(d.Attributes), d.IsPublic, "class", d.Name,
            d.Generics, d.Interfaces, d.Members, d.Span),
        EnumDecl d => EnumDoc(d),
        InterfaceDecl d => MethodBodyDoc(
            Doc.Of(Pub(d.IsPublic), Doc.From($"interface {d.Name}"), GenericsDoc(d.Generics),
                InterfaceListDoc(d.Interfaces), Doc.Space),
            d.Members, d.Span),
        ExtendDecl d => MethodBodyDoc(
            Doc.Of(Pub(d.IsPublic), Doc.From("extend "), TypeDoc(d.Target),
                InterfaceListDoc(d.Interfaces), Doc.Space),
            d.Methods, d.Span),
        GlobalBindingDecl d => Doc.Of(Pub(d.IsPublic), StmtDoc(d.Binding)),
        StaticBindingDecl d => Doc.Of(Pub(d.IsPublic), Doc.From("static "), StmtDoc(d.Binding)),
        TypeAliasDecl d => Doc.Of(Pub(d.IsPublic),
            Doc.From($"type {d.Name} = "), TypeDoc(d.Aliased), Doc.From(";")),
        FieldDecl d => FieldDoc(d),
        _ => throw new InternalCompilationException($"unreachable: unformatted {decl.GetType().Name}"),
    };

    private static Doc Pub(bool isPublic) => isPublic ? Doc.From("pub ") : Doc.Nil;

    private Doc ImportDoc(ImportDecl decl)
    {
        var path = string.Join(".", decl.Path);
        return decl.Clause switch
        {
            null => Doc.From($"import {path};"),
            ImportAlias a => Doc.From($"import {path} as {a.Alias};"),
            ImportSelective s => Doc.GroupOf(
                Doc.From($"import {path} {{"),
                Doc.IndentOf(Doc.LineOrSpace,
                    Doc.Join(Doc.Of(Doc.From(","), Doc.LineOrSpace),
                        s.Names.Select(Doc.From).ToArray()),
                    Doc.WhenBroken(Doc.From(","))),
                Doc.LineOrSpace, Doc.From("};")),
            _ => throw new InternalCompilationException("unreachable: unknown import clause"),
        };
    }

    private Doc AttributeDoc(AttributeNode attribute)
    {
        var name = Doc.From("@" + string.Join(".", attribute.Path));
        if (attribute.Fields.Length == 0) return name;

        return Doc.GroupOf(name, Doc.From(" {"),
            Doc.IndentOf(Doc.LineOrSpace,
                Doc.Join(Doc.Of(Doc.From(","), Doc.LineOrSpace),
                    attribute.Fields.Select(InitFieldDoc).ToArray()),
                Doc.WhenBroken(Doc.From(","))),
            Doc.LineOrSpace, Doc.From("}"));
    }

    private Doc Attributes(AttributeNode[] attributes)
    {
        if (attributes.Length == 0) return Doc.Nil;
        var parts = new List<Doc>();
        foreach (var attribute in attributes)
        {
            parts.Add(AttributeDoc(attribute));
            parts.Add(Doc.NewLine);
        }

        return new Doc.Concat(parts);
    }

    private Doc FunctionDoc(FunctionDecl decl)
    {
        var head = new List<Doc>
        {
            Attributes(decl.Attributes),
            Pub(decl.IsPublic),
            decl.IsStatic ? Doc.From("static ") : Doc.Nil,
            decl.IsMut ? Doc.From("mut ") : Doc.Nil,
            Doc.From($"fn {decl.Name}"),
            GenericsDoc(decl.Generics),
            ParamListDoc(decl.Parameters),
        };

        if (decl.ReturnType is { } ret) head.Add(Doc.Of(Doc.From(": "), TypeDoc(ret)));
        if (decl.Throws is { } throws)
        {
            head.Add(Doc.From(" throws"));
            if (throws.Type is { } thrown) head.Add(Doc.Of(Doc.Space, TypeDoc(thrown)));
        }

        head.Add(decl.Body is { } body ? Doc.Of(Doc.Space, BlockDoc(body)) : Doc.From(";"));
        return new Doc.Concat(head);
    }

    private Doc GenericsDoc(GenericParam[] generics)
    {
        if (generics.Length == 0) return Doc.Nil;

        var parts = generics.Select(g => g.Constraints.Length == 0
            ? Doc.From(g.Name)
            : Doc.Of(Doc.From($"{g.Name} :: ["),
                Doc.Join(Doc.From(", "), g.Constraints.Select(TypeDoc).ToArray()),
                Doc.From("]")));
        return Doc.Of(Doc.From("<"), Doc.Join(Doc.From(", "), parts.ToArray()), Doc.From(">"));
    }

    private Doc ParamListDoc(Param[] parameters)
    {
        if (parameters.Length == 0) return Doc.From("()");

        // No trailing comma: the parameter grammar does not allow one.
        return Doc.GroupOf(Doc.From("("),
            Doc.IndentOf(Doc.LineOrNothing,
                Doc.Join(Doc.Of(Doc.From(","), Doc.LineOrSpace),
                    parameters.Select(ParamDoc).ToArray())),
            Doc.LineOrNothing, Doc.From(")"));
    }

    private Doc ParamDoc(Param parameter)
    {
        var parts = new List<Doc>();
        if (parameter.IsParams) parts.Add(Doc.From("params "));
        parts.Add(Doc.From($"{parameter.Name}: "));
        parts.Add(TypeDoc(parameter.Type));
        if (parameter.Default is { } fallback)
        {
            parts.Add(Doc.From(" = "));
            parts.Add(ExprDoc(fallback, Assign));
        }

        return new Doc.Concat(parts);
    }

    private Doc TypeBodyDoc(Doc attributes, bool isPublic, string keyword, string name,
        GenericParam[] generics, TypeNode[] interfaces, Decl[] members, Span whole)
    {
        var head = Doc.Of(attributes, Pub(isPublic), Doc.From($"{keyword} {name}"),
            GenericsDoc(generics), InterfaceListDoc(interfaces), Doc.Space);

        var closing = whole.End - 1;
        return BracedDoc(head, members.Length == 0 && !AnyCommentBefore(closing),
            () => SequenceDoc(members, MemberDoc, MemberAir, closing));
    }

    private static Air MemberAir(Decl previous, Decl next) =>
        HasBody(previous) || HasBody(next) ? Air.Forced : Air.User;

    private static bool HasBody(Decl member) => member is FunctionDecl { Body: not null };

    private Doc MemberDoc(Decl member) => member switch
    {
        // The comma belongs to the FIELD: a self-closing member may omit it, and this formatter
        // takes the permission.
        FieldDecl d => Doc.Of(FieldDoc(d), Doc.From(",")),
        _ => DeclDoc(member),
    };

    private Doc FieldDoc(FieldDecl decl)
    {
        var parts = new List<Doc> { Doc.From($"{decl.Name}: "), TypeDoc(decl.Type) };
        if (decl.Default is { } fallback)
        {
            parts.Add(Doc.From(" = "));
            parts.Add(ExprDoc(fallback, Assign));
        }

        return new Doc.Concat(parts);
    }

    private Doc InterfaceListDoc(TypeNode[] interfaces) =>
        interfaces.Length == 0
            ? Doc.Nil
            : Doc.Of(Doc.From(" :: ["),
                Doc.Join(Doc.From(", "), interfaces.Select(TypeDoc).ToArray()), Doc.From("]"));

    private Doc EnumDoc(EnumDecl decl)
    {
        var head = Doc.Of(Attributes(decl.Attributes), Pub(decl.IsPublic),
            Doc.From($"enum {decl.Name}"), GenericsDoc(decl.Generics),
            InterfaceListDoc(decl.Interfaces), Doc.Space);

        var closing = decl.Span.End - 1;
        if (decl.Variants.Length == 0 && decl.Methods.Length == 0 && !AnyCommentBefore(closing))
            return Doc.Of(head, Doc.From("{ }"));

        return BracedDoc(head, empty: false, () =>
        {
            var parts = new List<Doc>();

            // The variants own their line up to its end, so a trailing comment stays theirs;
            // a comment on a later line already belongs to the methods below.
            var index = 0;
            var variantsEnd = decl.Variants.Length == 0
                ? closing
                : LineEndOf(decl.Variants[^1].Span.End);
            if (decl.Methods.Length == 0) variantsEnd = closing;

            parts.Add(SequenceDoc(decl.Variants, Variant, (_, _) => Air.User, variantsEnd));

            if (decl.Methods.Length > 0)
            {
                parts.Add(Doc.NewLine);
                parts.Add(Doc.NewLine);
                _lastEnd = variantsEnd;
                parts.Add(SequenceDoc(decl.Methods, FunctionDoc, MethodAir, closing));
            }

            return new Doc.Concat(parts);

            // The ';' parts the variants from the methods; without methods every variant ends
            // in ',' — the trailing one is the grammar's own permission.
            Doc Variant(EnumVariant variant)
            {
                var last = ++index == decl.Variants.Length;
                return Doc.Of(VariantDoc(variant),
                    Doc.From(last && decl.Methods.Length > 0 ? ";" : ","));
            }
        });
    }

    /// <summary>The offset just past the last character of the line <paramref name="offset"/>
    /// lies on — before the newline itself.</summary>
    private int LineEndOf(int offset)
    {
        var line = LineOf(offset);
        return line + 1 < _lineStarts.Length ? _lineStarts[line + 1] - 1 : _source.Length;
    }

    private Doc VariantDoc(EnumVariant variant)
    {
        if (variant.TupleFields is { } tuple)
            return Doc.Of(Doc.From(variant.Name), Doc.From("("),
                Doc.Join(Doc.From(", "), tuple.Select(TypeDoc).ToArray()), Doc.From(")"));

        if (variant.StructFields is { } fields)
            return Doc.GroupOf(Doc.From(variant.Name + " {"),
                Doc.IndentOf(Doc.LineOrSpace,
                    Doc.Join(Doc.Of(Doc.From(","), Doc.LineOrSpace), fields.Select(FieldDoc).ToArray()),
                    Doc.WhenBroken(Doc.From(","))),
                Doc.LineOrSpace, Doc.From("}"));

        return Doc.From(variant.Name);
    }

    private static Air MethodAir(FunctionDecl previous, FunctionDecl next) =>
        previous.Body is not null || next.Body is not null ? Air.Forced : Air.User;

    /// <summary>A body holding methods only — interface and extend share the shape.</summary>
    private Doc MethodBodyDoc(Doc head, FunctionDecl[] methods, Span whole)
    {
        var closing = whole.End - 1;
        return BracedDoc(head, methods.Length == 0 && !AnyCommentBefore(closing),
            () => SequenceDoc(methods, FunctionDoc, MethodAir, closing));
    }

    // ------------------------------------------------------------------ statements

    private Doc StmtDoc(Stmt stmt) => stmt switch
    {
        Block b => BlockDoc(b),
        BindingStmt b => BindingDoc(b),
        DestructuringStmt d => DestructuringDoc(d),
        IfStmt s => IfStmtDoc(s),
        WhileStmt s => Doc.Of(Doc.From("while ("), ExprDoc(s.Condition, Assign),
            Doc.From(") "), BlockDoc(s.Body)),
        DoWhileStmt s => Doc.Of(Doc.From("do "), BlockDoc(s.Body),
            Doc.From(" while ("), ExprDoc(s.Condition, Assign), Doc.From(");")),
        ForInStmt s => Doc.Of(Doc.From($"for ({s.Variable} in "), ExprDoc(s.Iterable, Assign),
            Doc.From(") "), BlockDoc(s.Body)),
        BreakStmt => Doc.From("break;"),
        ContinueStmt => Doc.From("continue;"),
        ReturnStmt s => s.Value is null
            ? Doc.From("return;")
            : Doc.Of(Doc.From("return "), ExprDoc(s.Value, Assign), Doc.From(";")),
        YieldStmt s => s.Value is null
            ? Doc.From("yield;")
            : Doc.Of(Doc.From("yield "), ExprDoc(s.Value, Assign), Doc.From(";")),
        DeferStmt s => Doc.Of(Doc.From("defer "), StmtDoc(s.Body)),
        ThrowStmt s => Doc.Of(Doc.From("throw "), ExprDoc(s.Value, Assign), Doc.From(";")),
        MatchStmt s => MatchDoc(s.Scrutinee, s.Arms, s.Span),
        TryStmt s => TryDoc(s),
        ExprStmt s => Doc.Of(ExprDoc(s.Expr, Assign), Doc.From(";")),
        _ => throw new InternalCompilationException($"unreachable: unformatted {stmt.GetType().Name}"),
    };

    private Doc BlockDoc(Block block)
    {
        var closing = block.Span.End - 1;
        return BracedDoc(Doc.Nil, block.Statements.Length == 0 && !AnyCommentBefore(closing),
            () => SequenceDoc(block.Statements, StmtDoc, (_, _) => Air.User, closing));
    }

    private Doc BindingDoc(BindingStmt binding)
    {
        var parts = new List<Doc> { Doc.From(binding.IsMutable ? "var " : "let "), Doc.From(binding.Name) };
        if (binding.Type is { } type)
        {
            parts.Add(Doc.From(": "));
            parts.Add(TypeDoc(type));
        }

        if (binding.Initializer is { } init)
        {
            parts.Add(Doc.From(" = "));
            parts.Add(ExprDoc(init, Assign));
        }

        parts.Add(Doc.From(";"));
        return new Doc.Concat(parts);
    }

    private Doc DestructuringDoc(DestructuringStmt stmt)
    {
        var parts = new List<Doc>
        {
            Doc.From(stmt.IsMutable ? "var " : "let "), PatternDoc(stmt.Pattern),
        };
        if (stmt.Type is { } type)
        {
            parts.Add(Doc.From(": "));
            parts.Add(TypeDoc(type));
        }

        parts.Add(Doc.From(" = "));
        parts.Add(ExprDoc(stmt.Initializer, Assign));
        parts.Add(Doc.From(";"));
        return new Doc.Concat(parts);
    }

    private Doc IfStmtDoc(IfStmt stmt)
    {
        var parts = new List<Doc>
        {
            Doc.From("if ("), ExprDoc(stmt.Condition, Assign), Doc.From(") "), BlockDoc(stmt.Then),
        };

        if (stmt.Else is { } tail)
        {
            parts.Add(Doc.From(" else "));
            parts.Add(StmtDoc(tail)); // a Block, or the next IfStmt of an else-if ladder
        }

        return new Doc.Concat(parts);
    }

    private Doc TryDoc(TryStmt stmt)
    {
        var parts = new List<Doc> { Doc.From("try "), BlockDoc(stmt.Body) };
        foreach (var clause in stmt.Catches)
        {
            var binding = clause.BindingName is null
                ? Doc.From("_")
                : clause.BindingType is null
                    ? Doc.From(clause.BindingName)
                    : Doc.Of(Doc.From($"{clause.BindingName}: "), TypeDoc(clause.BindingType));
            parts.Add(Doc.Of(Doc.From(" catch ("), binding, Doc.From(") "), BlockDoc(clause.Body)));
        }

        return new Doc.Concat(parts);
    }

    private Doc MatchDoc(Expr scrutinee, MatchArm[] arms, Span whole)
    {
        var head = Doc.Of(Doc.From("match ("), ExprDoc(scrutinee, Assign), Doc.From(") "));
        var closing = whole.End - 1;
        return BracedDoc(head, arms.Length == 0 && !AnyCommentBefore(closing),
            () => SequenceDoc(arms, ArmDoc, (_, _) => Air.User, closing));
    }

    private Doc ArmDoc(MatchArm arm)
    {
        var line = new List<Doc> { PatternDoc(arm.Pattern) };
        if (arm.Guard is { } guard)
        {
            line.Add(Doc.From(" if "));
            line.Add(ExprDoc(guard, Assign));
        }

        line.Add(Doc.From(" => "));
        if (arm.Body is Block block)
        {
            line.Add(BlockDoc(block)); // a block arm closes itself; no comma
        }
        else
        {
            line.Add(ExprDoc((Expr)arm.Body, Assign));
            line.Add(Doc.From(","));
        }

        return new Doc.Concat(line);
    }

    // ------------------------------------------------------------------ expressions

    // The levels of Grammar.md §6.1; smaller binds tighter. An expression printed where at most
    // 'maxLevel' is allowed gets parentheses when its own level exceeds it.
    private const int Primary = 0;
    private const int Postfix = 1;
    private const int Prefix = 2;
    private const int CastLevel = 3;
    private const int Range = 7;
    private const int Assign = 16;

    private static (string Symbol, int Level) BinaryInfo(BinaryOp op) => op switch
    {
        BinaryOp.Mul => ("*", 4),
        BinaryOp.Div => ("/", 4),
        BinaryOp.Rem => ("%", 4),
        BinaryOp.Add => ("+", 5),
        BinaryOp.Sub => ("-", 5),
        BinaryOp.Shl => ("<<", 6),
        BinaryOp.Shr => (">>", 6),
        BinaryOp.BitAnd => ("&", 8),
        BinaryOp.BitXor => ("^", 9),
        BinaryOp.BitOr => ("|", 10),
        BinaryOp.Lt => ("<", 11),
        BinaryOp.Le => ("<=", 11),
        BinaryOp.Gt => (">", 11),
        BinaryOp.Ge => (">=", 11),
        BinaryOp.Eq => ("==", 12),
        BinaryOp.Ne => ("!=", 12),
        BinaryOp.LogicalAnd => ("&&", 13),
        BinaryOp.LogicalOr => ("||", 14),
        BinaryOp.Coalesce => ("??", 15),
        _ => throw new InternalCompilationException($"unreachable: unexpected {op}"),
    };

    private static int LevelOf(Expr expr) => expr switch
    {
        BinaryExpr b => BinaryInfo(b.Operator).Level,
        UnaryExpr or ResumeExpr => Prefix,
        PostfixExpr or CallExpr or IndexExpr or MemberExpr => Postfix,
        CastExpr => CastLevel,
        RangeExpr => Range,
        AssignExpr => Assign,
        // An if or a lambda extends to the end of the expression: as an operand it must be
        // parenthesized or the reparse reads past the operator. Treated as the loosest level.
        IfExpr or LambdaExpr => Assign,
        _ => Primary,
    };

    private Doc ExprDoc(Expr expr, int maxLevel)
    {
        var doc = ExprDocInner(expr);
        return LevelOf(expr) > maxLevel ? Doc.Of(Doc.From("("), doc, Doc.From(")")) : doc;
    }

    private Doc ExprDocInner(Expr expr) => expr switch
    {
        // Spelling lives in the source, not in the node.
        IntLiteralExpr or FloatLiteralExpr or StringLiteralExpr or CharLiteralExpr
            or InterpolatedStringExpr => Src(expr.Span),
        BoolLiteralExpr b => Doc.From(b.Value ? "true" : "false"),
        NullLiteralExpr => Doc.From("null"),
        ThisExpr => Doc.From("this"),
        IdentifierExpr i => Doc.From(i.Name),
        AtIdentifierExpr a => AtIdentifierDoc(a),
        TypePathExpr t => Doc.Of(Doc.From(string.Join(".", t.Path)), TypeArgsDoc(t.TypeArguments)),

        UnaryExpr u => Doc.Of(Doc.From(PrefixSymbol(u.Operator)), ExprDoc(u.Operand, Prefix)),
        ResumeExpr r => Doc.Of(Doc.From("resume "), ExprDoc(r.Coroutine, Prefix)),
        PostfixExpr p => Doc.Of(ExprDoc(p.Operand, Postfix), Doc.From(PostfixSymbol(p.Operator))),
        BinaryExpr b => BinaryDoc(b),
        AssignExpr a => AssignDoc(a),
        RangeExpr r => Doc.Of(ExprDoc(r.Low, Range - 1),
            Doc.From(r.IsInclusive ? "..=" : ".."), ExprDoc(r.High, Range - 1)),
        CastExpr c => Doc.Of(ExprDoc(c.Operand, CastLevel), Doc.From(" as "), TypeDoc(c.Type)),

        CallExpr c => CallDoc(c),
        IndexExpr i => Doc.Of(ExprDoc(i.Target, Postfix), Doc.From("["),
            ExprDoc(i.Index, Assign), Doc.From("]")),
        MemberExpr m => Doc.Of(ExprDoc(m.Target, Postfix),
            Doc.From(m.IsOptional ? "?." : "."), Doc.From(m.Member)),

        ArrayLitExpr a => ArrayDoc(a),
        TupleLitExpr t => Doc.GroupOf(Doc.From("("),
            Doc.IndentOf(Doc.LineOrNothing,
                Doc.Join(Doc.Of(Doc.From(","), Doc.LineOrSpace),
                    t.Elements.Select(e => ExprDoc(e, Assign)).ToArray())),
            Doc.LineOrNothing, Doc.From(")")),

        LambdaExpr l => LambdaDoc(l),
        IfExpr i => Doc.GroupOf(Doc.From("if ("), ExprDoc(i.Condition, Assign), Doc.From(") "),
            ExprDoc(i.Then, Assign), Doc.LineOrSpace, Doc.From("else "), ExprDoc(i.Else, Assign)),
        MatchExpr m => MatchDoc(m.Scrutinee, m.Arms, m.Span),
        StructInitExpr s => StructInitDoc(s),

        _ => throw new InternalCompilationException($"unreachable: unformatted {expr.GetType().Name}"),
    };

    private Doc AtIdentifierDoc(AtIdentifierExpr expr)
    {
        if (expr.Arguments is null) return Doc.From(expr.Name);
        return Doc.Of(Doc.From(expr.Name), Doc.From("("),
            Doc.Join(Doc.From(", "), expr.Arguments.Select(a => ExprDoc(a, Assign)).ToArray()),
            Doc.From(")"));
    }

    private static string PrefixSymbol(UnaryOp op) => op switch
    {
        UnaryOp.Not => "!",
        UnaryOp.Neg => "-",
        UnaryOp.BitNot => "~",
        UnaryOp.PreInc => "++",
        UnaryOp.PreDec => "--",
        _ => throw new InternalCompilationException($"unreachable: unexpected {op}"),
    };

    private static string PostfixSymbol(PostfixOp op) => op switch
    {
        PostfixOp.Inc => "++",
        PostfixOp.Dec => "--",
        PostfixOp.ForceUnwrap => "!",
        _ => throw new InternalCompilationException($"unreachable: unexpected {op}"),
    };

    private Doc BinaryDoc(BinaryExpr expr)
    {
        var (symbol, level) = BinaryInfo(expr.Operator);

        // Left-associative throughout, except ?? which associates right. The tighter side takes
        // level-1, so an equal level there regains its written parentheses.
        var (leftMax, rightMax) = expr.Operator == BinaryOp.Coalesce
            ? (level - 1, level)
            : (level, level - 1);

        return Doc.Of(ExprDoc(expr.Left, leftMax), Doc.From($" {symbol} "),
            ExprDoc(expr.Right, rightMax));
    }

    private Doc AssignDoc(AssignExpr expr)
    {
        var symbol = expr.Operator is { } op ? BinaryInfo(op).Symbol + "=" : "=";
        // Right-associative: 'a = b = c' keeps its shape, a parenthesized target regains parens.
        return Doc.Of(ExprDoc(expr.Target, Assign - 1), Doc.From($" {symbol} "),
            ExprDoc(expr.Value, Assign));
    }

    private Doc CallDoc(CallExpr call)
    {
        var head = Doc.Of(ExprDoc(call.Callee, Postfix),
            TypeArgsDoc(call.TypeArguments ?? []));
        if (call.Arguments.Length == 0) return Doc.Of(head, Doc.From("()"));

        // No trailing comma: the call grammar does not allow one.
        return Doc.GroupOf(head, Doc.From("("),
            Doc.IndentOf(Doc.LineOrNothing,
                Doc.Join(Doc.Of(Doc.From(","), Doc.LineOrSpace),
                    call.Arguments.Select(a => ExprDoc(a, Assign)).ToArray())),
            Doc.LineOrNothing, Doc.From(")"));
    }

    private Doc ArrayDoc(ArrayLitExpr array)
    {
        if (array.Elements.Length == 0) return Doc.From("[]");

        return Doc.GroupOf(Doc.From("["),
            Doc.IndentOf(Doc.LineOrNothing,
                Doc.Join(Doc.Of(Doc.From(","), Doc.LineOrSpace),
                    array.Elements.Select(e => ExprDoc(e, Assign)).ToArray()),
                Doc.WhenBroken(Doc.From(","))),
            Doc.LineOrNothing, Doc.From("]"));
    }

    private Doc LambdaDoc(LambdaExpr lambda)
    {
        var parts = new List<Doc>
        {
            Doc.From("("),
            Doc.Join(Doc.From(", "), lambda.Parameters.Select(p => p.Type is { } type
                ? Doc.Of(Doc.From($"{p.Name}: "), TypeDoc(type))
                : Doc.From(p.Name)).ToArray()),
            Doc.From(")"),
        };

        if (lambda.ReturnType is { } ret)
        {
            parts.Add(Doc.From(": "));
            parts.Add(TypeDoc(ret));
        }

        parts.Add(Doc.From(" => "));
        parts.Add(lambda.Body is Block block ? BlockDoc(block) : ExprDoc((Expr)lambda.Body, Assign));
        return new Doc.Concat(parts);
    }

    private Doc StructInitDoc(StructInitExpr init)
    {
        var head = Doc.Of(Doc.From(string.Join(".", init.Path)), TypeArgsDoc(init.TypeArguments));
        if (init.Fields.Length == 0) return Doc.Of(head, Doc.From(" { }"));

        return Doc.GroupOf(head, Doc.From(" {"),
            Doc.IndentOf(Doc.LineOrSpace,
                Doc.Join(Doc.Of(Doc.From(","), Doc.LineOrSpace),
                    init.Fields.Select(InitFieldDoc).ToArray()),
                Doc.WhenBroken(Doc.From(","))),
            Doc.LineOrSpace, Doc.From("}"));
    }

    private Doc InitFieldDoc(StructInitField field) =>
        Doc.Of(Doc.From($"{field.Name} = "), ExprDoc(field.Value, Assign));

    // ------------------------------------------------------------------ patterns

    private Doc PatternDoc(Pattern pattern) => pattern switch
    {
        WildcardPattern => Doc.From("_"),
        LiteralPattern l => LiteralPatternDoc(l),
        BindingPattern b => Doc.From(b.Name),
        VariantPattern v => VariantPatternDoc(v),
        TuplePattern t => Doc.Of(Doc.From("("),
            Doc.Join(Doc.From(", "), t.Elements.Select(PatternDoc).ToArray()), Doc.From(")")),
        RangePattern r => Doc.Of(ExprDoc(r.Low, Prefix),
            Doc.From(r.IsInclusive ? "..=" : ".."), ExprDoc(r.High, Prefix)),
        OrPattern o => Doc.Join(Doc.From(" | "), o.Alternatives.Select(PatternDoc).ToArray()),
        _ => throw new InternalCompilationException($"unreachable: unformatted {pattern.GetType().Name}"),
    };

    private Doc LiteralPatternDoc(LiteralPattern pattern) => pattern.Literal switch
    {
        BoolLiteralExpr b => Doc.From(b.Value ? "true" : "false"),
        NullLiteralExpr => Doc.From("null"),
        // A sign is a UnaryExpr around the literal; both spellings live in the span.
        _ => Src(pattern.Literal.Span),
    };

    private Doc VariantPatternDoc(VariantPattern pattern)
    {
        var head = Doc.From(string.Join(".", pattern.Path));
        if (pattern.TupleElements is { } tuple)
            return Doc.Of(head, Doc.From("("),
                Doc.Join(Doc.From(", "), tuple.Select(PatternDoc).ToArray()), Doc.From(")"));

        if (pattern.StructFields is { } fields)
            return Doc.Of(head, Doc.From(" { "),
                Doc.Join(Doc.From(", "), fields.Select(f => f.Pattern is { } inner
                    ? Doc.Of(Doc.From($"{f.Name} = "), PatternDoc(inner))
                    : Doc.From(f.Name)).ToArray()),
                Doc.From(" }"));

        return head;
    }

    // ------------------------------------------------------------------ types

    private Doc TypeDoc(TypeNode type) => type switch
    {
        NamedType n => Doc.Of(Doc.From(string.Join(".", n.Path)), TypeArgsDoc(n.TypeArguments)),
        NullableType n => Doc.Of(Doc.From("?"), TypeDoc(n.Inner)),
        // '(?T)[]' and '(fn(..) -> R)[]': without the parentheses the suffix would rebind.
        ArrayType a => Doc.Of(
            a.Element is NullableType or FunctionType
                ? Doc.Of(Doc.From("("), TypeDoc(a.Element), Doc.From(")"))
                : TypeDoc(a.Element),
            a.Size is { } size ? Doc.Of(Doc.From("["), Src(size.Span), Doc.From("]")) : Doc.From("[]")),
        TupleType t => Doc.Of(Doc.From("("),
            Doc.Join(Doc.From(", "), t.Elements.Select(TypeDoc).ToArray()), Doc.From(")")),
        FunctionType f => Doc.Of(Doc.From("fn("),
            Doc.Join(Doc.From(", "), f.Parameters.Select(TypeDoc).ToArray()),
            Doc.From(") -> "), TypeDoc(f.ReturnType)),
        _ => throw new InternalCompilationException($"unreachable: unformatted {type.GetType().Name}"),
    };

    private Doc TypeArgsDoc(TypeNode[] arguments) =>
        arguments.Length == 0
            ? Doc.Nil
            : Doc.Of(Doc.From("<"),
                Doc.Join(Doc.From(", "), arguments.Select(TypeDoc).ToArray()), Doc.From(">"));

    private Doc Src(Span span) => Doc.From(_source.Substring(span.Start, span.Length));
}
