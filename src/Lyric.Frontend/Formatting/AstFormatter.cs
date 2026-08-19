using Lyric.AST;
using Lyric.Core;

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
/// <para>Comments are not handled here yet; the caller decides what to do with the trivia
/// list. Error nodes throw: the formatter runs only on files the parser accepted, and printing
/// a recovery placeholder would write a hole into someone's file.</para>
/// </summary>
public sealed class AstFormatter
{
    private readonly string _source;

    private AstFormatter(string source) => _source = source;

    public static Doc Build(Module module, string source) =>
        new AstFormatter(source).ModuleDoc(module);

    // ------------------------------------------------------------------ module and declarations

    private Doc ModuleDoc(Module module)
    {
        var parts = new List<Doc>();

        foreach (var attribute in module.Attributes)
        {
            parts.Add(AttributeDoc(attribute));
            parts.Add(Doc.NewLine);
        }

        if (module.Header is { } header)
        {
            parts.Add(Doc.From($"module {string.Join(".", header.Segments)};"));
            parts.Add(Doc.NewLine);
            if (module.Declarations.Length > 0) parts.Add(Doc.NewLine);
        }

        for (var i = 0; i < module.Declarations.Length; i++)
        {
            if (i > 0)
            {
                parts.Add(Doc.NewLine);
                // Imports form one contiguous head; everything else breathes.
                if (module.Declarations[i - 1] is not ImportDecl || module.Declarations[i] is not ImportDecl)
                    parts.Add(Doc.NewLine);
            }

            parts.Add(DeclDoc(module.Declarations[i]));
        }

        parts.Add(Doc.NewLine); // the trailing newline of every formatted file
        return new Doc.Concat(parts);
    }

    private Doc DeclDoc(Decl decl) => decl switch
    {
        ImportDecl d => ImportDoc(d),
        FunctionDecl d => FunctionDoc(d),
        StructDecl d => TypeBodyDoc(Attributes(d.Attributes), d.IsPublic, "struct", d.Name,
            d.Generics, d.Interfaces, d.Members),
        ClassDecl d => TypeBodyDoc(Attributes(d.Attributes), d.IsPublic, "class", d.Name,
            d.Generics, d.Interfaces, d.Members),
        EnumDecl d => EnumDoc(d),
        InterfaceDecl d => InterfaceDoc(d),
        ExtendDecl d => ExtendDoc(d),
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
        GenericParam[] generics, TypeNode[] interfaces, Decl[] members)
    {
        var head = Doc.Of(attributes, Pub(isPublic), Doc.From($"{keyword} {name}"),
            GenericsDoc(generics), InterfaceListDoc(interfaces));

        if (members.Length == 0) return Doc.Of(head, Doc.From(" { }"));

        var body = new List<Doc>();
        for (var i = 0; i < members.Length; i++)
        {
            if (i > 0)
            {
                body.Add(Doc.NewLine);
                // A member with a body gets air; fields and constants sit together.
                if (HasBody(members[i - 1]) || HasBody(members[i])) body.Add(Doc.NewLine);
            }

            body.Add(MemberDoc(members[i]));
        }

        return Doc.Of(head, Doc.From(" {"),
            Doc.IndentOf(Doc.NewLine, new Doc.Concat(body)), Doc.NewLine, Doc.From("}"));
    }

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
            InterfaceListDoc(decl.Interfaces));

        if (decl.Variants.Length == 0 && decl.Methods.Length == 0)
            return Doc.Of(head, Doc.From(" { }"));

        var body = new List<Doc>();
        for (var i = 0; i < decl.Variants.Length; i++)
        {
            if (i > 0) body.Add(Doc.NewLine);
            body.Add(VariantDoc(decl.Variants[i]));

            // The ';' parts the variants from the methods; without methods every variant ends in
            // ',' — the trailing one is the grammar's own permission.
            var last = i == decl.Variants.Length - 1;
            body.Add(Doc.From(last && decl.Methods.Length > 0 ? ";" : ","));
        }

        foreach (var method in decl.Methods)
        {
            body.Add(Doc.NewLine);
            body.Add(Doc.NewLine);
            body.Add(FunctionDoc(method));
        }

        return Doc.Of(head, Doc.From(" {"),
            Doc.IndentOf(Doc.NewLine, new Doc.Concat(body)), Doc.NewLine, Doc.From("}"));
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

    private Doc InterfaceDoc(InterfaceDecl decl)
    {
        var head = Doc.Of(Pub(decl.IsPublic), Doc.From($"interface {decl.Name}"),
            GenericsDoc(decl.Generics));
        return MethodBodyDoc(head, decl.Members);
    }

    private Doc ExtendDoc(ExtendDecl decl)
    {
        var head = Doc.Of(Pub(decl.IsPublic), Doc.From("extend "), TypeDoc(decl.Target),
            InterfaceListDoc(decl.Interfaces));
        return MethodBodyDoc(head, decl.Methods);
    }

    /// <summary>A body holding methods only — interface and extend share the shape.</summary>
    private Doc MethodBodyDoc(Doc head, FunctionDecl[] methods)
    {
        if (methods.Length == 0) return Doc.Of(head, Doc.From(" { }"));

        var body = new List<Doc>();
        for (var i = 0; i < methods.Length; i++)
        {
            if (i > 0)
            {
                body.Add(Doc.NewLine);
                if (methods[i - 1].Body is not null || methods[i].Body is not null)
                    body.Add(Doc.NewLine);
            }

            body.Add(FunctionDoc(methods[i]));
        }

        return Doc.Of(head, Doc.From(" {"),
            Doc.IndentOf(Doc.NewLine, new Doc.Concat(body)), Doc.NewLine, Doc.From("}"));
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
        MatchStmt s => MatchDoc(s.Scrutinee, s.Arms),
        TryStmt s => TryDoc(s),
        ExprStmt s => Doc.Of(ExprDoc(s.Expr, Assign), Doc.From(";")),
        _ => throw new InternalCompilationException($"unreachable: unformatted {stmt.GetType().Name}"),
    };

    private Doc BlockDoc(Block block)
    {
        if (block.Statements.Length == 0) return Doc.From("{ }");

        return Doc.Of(Doc.From("{"),
            Doc.IndentOf(Doc.NewLine,
                Doc.Join(Doc.NewLine, block.Statements.Select(StmtDoc).ToArray())),
            Doc.NewLine, Doc.From("}"));
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

    private Doc MatchDoc(Expr scrutinee, MatchArm[] arms)
    {
        var body = new List<Doc>();
        foreach (var arm in arms)
        {
            if (body.Count > 0) body.Add(Doc.NewLine);

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

            body.Add(new Doc.Concat(line));
        }

        var head = Doc.Of(Doc.From("match ("), ExprDoc(scrutinee, Assign), Doc.From(") {"));
        if (arms.Length == 0) return Doc.Of(head, Doc.From(" }")); // parseable, if pointless

        return Doc.Of(head, Doc.IndentOf(Doc.NewLine, new Doc.Concat(body)),
            Doc.NewLine, Doc.From("}"));
    }

    // ------------------------------------------------------------------ expressions

    // The levels of Grammar.md §6.1; smaller binds tighter. An expression printed where at most
    // 'maxLevel' is allowed gets parentheses when its own level exceeds it.
    private const int Primary = 0;
    private const int Postfix = 1;
    private const int Prefix = 2;
    private const int CastLevel = 3;
    private const int Range = 7;
    private const int Coalesce = 15;
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

    private int LevelOf(Expr expr) => expr switch
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
        MatchExpr m => MatchDoc(m.Scrutinee, m.Arms),
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
