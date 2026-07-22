using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Xunit;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Direkte AST-Assertions gegen den Parser-Kontrakt — unabhängig vom AstDumper.
/// Die Golden-Tests sichern den *ganzen* Baum über den Dumper ab; diese Tests
/// prüfen einzelne Invarianten (Assoziativität, Präzedenz, Recovery) direkt am
/// Record-Baum, damit ein Dumper-Bug keinen Parser-Bug maskiert.
/// </summary>
public class ParserTests
{
    private static (Expr expr, DiagnosticEngine diag) Parse(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var expr = new Parser(sm, id, de).ParseExpression();
        return (expr, de);
    }

    private static (Stmt stmt, DiagnosticEngine diag) ParseStatement(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var stmt = new Parser(sm, id, de).ParseStatement();
        return (stmt, de);
    }

    // --- Assoziativität ---

    [Fact]
    public void Coalesce_is_right_associative()
    {
        var (expr, de) = Parse("a ?? b ?? c");
        Assert.False(de.HasErrors);
        var top = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal(BinaryOp.Coalesce, top.Operator);
        Assert.IsType<IdentifierExpr>(top.Left);            // a
        var right = Assert.IsType<BinaryExpr>(top.Right);   // (b ?? c)
        Assert.Equal(BinaryOp.Coalesce, right.Operator);
    }

    [Fact]
    public void Addition_is_left_associative()
    {
        var (expr, _) = Parse("a + b + c");
        var top = Assert.IsType<BinaryExpr>(expr);          // ((a + b) + c)
        Assert.Equal(BinaryOp.Add, top.Operator);
        var left = Assert.IsType<BinaryExpr>(top.Left);
        Assert.Equal(BinaryOp.Add, left.Operator);
        Assert.IsType<IdentifierExpr>(top.Right);           // c
    }

    [Fact]
    public void Assignment_is_right_associative()
    {
        var (expr, _) = Parse("a = b = c");
        var top = Assert.IsType<AssignExpr>(expr);
        Assert.Null(top.Operator);                          // plain '='
        Assert.IsType<AssignExpr>(top.Value);               // b = c
    }

    [Fact]
    public void Compound_assign_carries_base_operator()
    {
        var (expr, _) = Parse("a += b");
        var top = Assert.IsType<AssignExpr>(expr);
        Assert.Equal(BinaryOp.Add, top.Operator);
    }

    [Fact]
    public void Cast_is_left_associative()
    {
        var (expr, _) = Parse("x as int as float");
        var outer = Assert.IsType<CastExpr>(expr);          // (x as int) as float
        Assert.IsType<CastExpr>(outer.Operand);
    }

    // --- Präzedenz ---

    [Fact]
    public void Multiplication_binds_tighter_than_addition()
    {
        var (expr, _) = Parse("1 + 2 * 3");
        var top = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal(BinaryOp.Add, top.Operator);
        var right = Assert.IsType<BinaryExpr>(top.Right);
        Assert.Equal(BinaryOp.Mul, right.Operator);
    }

    [Fact]
    public void Postfix_binds_tighter_than_prefix()
    {
        var (expr, _) = Parse("-a!");
        var neg = Assert.IsType<UnaryExpr>(expr);           // -(a!)
        Assert.Equal(UnaryOp.Neg, neg.Operator);
        var unwrap = Assert.IsType<PostfixExpr>(neg.Operand);
        Assert.Equal(PostfixOp.ForceUnwrap, unwrap.Operator);
    }

    [Fact]
    public void Grouping_overrides_precedence()
    {
        var (expr, _) = Parse("(1 + 2) * 3");
        var top = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal(BinaryOp.Mul, top.Operator);
        var left = Assert.IsType<BinaryExpr>(top.Left);
        Assert.Equal(BinaryOp.Add, left.Operator);          // parens survived
    }

    // --- Typen ---

    [Fact]
    public void Nested_generics_split_the_double_gt()
    {
        var (expr, de) = Parse("x as List<List<int>>");
        Assert.False(de.HasErrors);                         // '>>' correctly split into two '>'
        var cast = Assert.IsType<CastExpr>(expr);
        var outer = Assert.IsType<NamedType>(cast.Type);
        Assert.Equal(["List"], outer.Path);
        var inner = Assert.IsType<NamedType>(Assert.Single(outer.TypeArguments));
        Assert.Equal(["List"], inner.Path);
        var leaf = Assert.IsType<NamedType>(Assert.Single(inner.TypeArguments));
        Assert.Equal(["int"], leaf.Path);
    }

    [Fact]
    public void Dotted_type_path_is_captured()
    {
        var (expr, _) = Parse("d as std.collections.Deque");
        var cast = Assert.IsType<CastExpr>(expr);
        var named = Assert.IsType<NamedType>(cast.Type);
        Assert.Equal(["std", "collections", "Deque"], named.Path);
    }

    [Fact]
    public void Fixed_size_array_type_keeps_the_size()
    {
        var (expr, _) = Parse("a as int[8]");
        var cast = Assert.IsType<CastExpr>(expr);
        var arr = Assert.IsType<ArrayType>(cast.Type);
        Assert.NotNull(arr.Size);
        Assert.Equal(8UL, arr.Size!.Value);
    }

    // --- f-Strings ---

    [Fact]
    public void Fstring_splits_into_text_and_hole_segments()
    {
        var (expr, de) = Parse("f\"a{b}c\"");
        Assert.False(de.HasErrors);
        var fstr = Assert.IsType<InterpolatedStringExpr>(expr);
        Assert.Collection(fstr.Segments,
            s => Assert.Equal("a", Assert.IsType<InterpText>(s).Text),
            s => Assert.IsType<IdentifierExpr>(Assert.IsType<InterpHole>(s).Expr),
            s => Assert.Equal("c", Assert.IsType<InterpText>(s).Text));
    }

    // --- Recovery / Diagnostics ---

    [Fact]
    public void Range_is_not_chainable()
    {
        var (_, de) = Parse("1..2..3");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-PAR0005");
    }

    [Fact]
    public void Tuple_has_no_upper_arity_limit()
    {
        var (expr, de) = Parse("(1, 2, 3, 4, 5)");
        Assert.False(de.HasErrors);
        Assert.Equal(5, Assert.IsType<TupleLitExpr>(expr).Elements.Length);
    }

    [Fact]
    public void Single_element_with_trailing_comma_is_not_a_tuple()
    {
        // Untergrenze bleibt: 1 Element ist Gruppierung, kein Tuple.
        var (_, de) = Parse("(x,)");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-PAR0010");
    }

    [Fact]
    public void Empty_array_parses_without_error()
    {
        var (expr, de) = Parse("[]");
        Assert.False(de.HasErrors);
        Assert.Empty(Assert.IsType<ArrayLitExpr>(expr).Elements);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("1 +")]
    [InlineData("* 3")]
    [InlineData("a.")]
    [InlineData("a[")]
    [InlineData("f\"{")]
    [InlineData("(x: ) => x")]
    [InlineData("x as")]
    [InlineData("(((((((((")]
    public void Parser_never_throws_and_reports_on_garbage(string source)
    {
        // Kontrakt: der Parser wirft nie — jeder Fehler geht als Diagnostic raus.
        var (expr, de) = Parse(source);
        Assert.NotNull(expr);
        Assert.True(de.HasErrors);
    }

    // --- Statements (§5) ---

    [Fact]
    public void Let_is_immutable_and_var_is_mutable()
    {
        Assert.False(Assert.IsType<BindingStmt>(ParseStatement("let x = 1;").stmt).IsMutable);
        Assert.True(Assert.IsType<BindingStmt>(ParseStatement("var x = 1;").stmt).IsMutable);
    }

    [Fact]
    public void Binding_captures_type_and_initializer()
    {
        var (stmt, de) = ParseStatement("let x: int = 42;");
        Assert.False(de.HasErrors);
        var binding = Assert.IsType<BindingStmt>(stmt);
        Assert.Equal("x", binding.Name);
        Assert.IsType<NamedType>(binding.Type);
        Assert.IsType<IntLiteralExpr>(binding.Initializer);
    }

    [Fact]
    public void Else_if_chains_as_nested_if()
    {
        var (stmt, de) = ParseStatement("if (a) {} else if (b) {} else {}");
        Assert.False(de.HasErrors);
        var outer = Assert.IsType<IfStmt>(stmt);
        var inner = Assert.IsType<IfStmt>(outer.Else);   // 'else if' → verschachteltes IfStmt
        Assert.IsType<Block>(inner.Else);                // finales 'else' → Block
    }

    [Fact]
    public void Block_collects_statements()
    {
        var (stmt, de) = ParseStatement("{ a(); b(); }");
        Assert.False(de.HasErrors);
        Assert.Equal(2, Assert.IsType<Block>(stmt).Statements.Length);
    }

    [Fact]
    public void Try_collects_typed_and_wildcard_catches()
    {
        var (stmt, de) = ParseStatement("try {} catch (e: E) {} catch (_) {}");
        Assert.False(de.HasErrors);
        var t = Assert.IsType<TryStmt>(stmt);
        Assert.Equal(2, t.Catches.Length);
        Assert.Equal("e", t.Catches[0].BindingName);
        Assert.IsType<NamedType>(t.Catches[0].BindingType);
        Assert.Null(t.Catches[1].BindingName);           // '_' bindet nicht
        Assert.Null(t.Catches[1].BindingType);
    }

    [Fact]
    public void Try_without_catch_reports()
    {
        var (_, de) = ParseStatement("try { x(); }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-PAR0023");
    }

    [Fact]
    public void Return_without_value_is_null()
    {
        Assert.Null(Assert.IsType<ReturnStmt>(ParseStatement("return;").stmt).Value);
    }

    [Fact]
    public void Defer_expression_form_wraps_in_exprstmt()
    {
        Assert.IsType<ExprStmt>(Assert.IsType<DeferStmt>(ParseStatement("defer f();").stmt).Body);
    }

    [Fact]
    public void Defer_block_form_holds_a_block()
    {
        Assert.IsType<Block>(Assert.IsType<DeferStmt>(ParseStatement("defer { f(); }").stmt).Body);
    }

    [Fact]
    public void Lambda_can_have_a_block_body()
    {
        var lambda = Assert.IsType<LambdaExpr>(Parse("(x) => { return x; }").expr);
        Assert.IsType<Block>(lambda.Body);
    }

    [Fact]
    public void Match_statement_parses_arms()
    {
        var (stmt, de) = ParseStatement("match (x) { 0 => a(), _ => b() }");
        Assert.False(de.HasErrors);
        var m = Assert.IsType<MatchStmt>(stmt);
        Assert.Equal(2, m.Arms.Length);
        Assert.IsType<LiteralPattern>(m.Arms[0].Pattern);
        Assert.IsType<WildcardPattern>(m.Arms[1].Pattern);
    }

    [Fact]
    public void Match_arm_guard_and_block_body()
    {
        var (stmt, de) = ParseStatement("match (x) { n if n > 0 => { use(n); }, _ => stop() }");
        Assert.False(de.HasErrors);
        var m = Assert.IsType<MatchStmt>(stmt);
        Assert.NotNull(m.Arms[0].Guard);           // 'if n > 0'
        Assert.IsType<Block>(m.Arms[0].Body);       // Block-Arm
    }

    [Theory]
    [InlineData("let")]
    [InlineData("let x")]
    [InlineData("if")]
    [InlineData("if (a)")]
    [InlineData("while ()")]
    [InlineData("for (x in) {}")]
    [InlineData("do {}")]
    [InlineData("{")]
    public void Statement_parser_never_throws_and_reports(string source)
    {
        var (stmt, de) = ParseStatement(source);
        Assert.NotNull(stmt);
        Assert.True(de.HasErrors);
    }

    // --- Declarations (§2/§3) ---

    private static (Module module, DiagnosticEngine diag) ParseModule(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var module = new Parser(sm, id, de).ParseModule();
        return (module, de);
    }

    [Fact]
    public void Module_captures_header_and_declarations()
    {
        var (m, de) = ParseModule("module app; fn main(): int { return 0; }");
        Assert.False(de.HasErrors);
        Assert.Equal(["app"], m.Header!.Segments);
        var fn = Assert.IsType<FunctionDecl>(Assert.Single(m.Declarations));
        Assert.Equal("main", fn.Name);
        Assert.NotNull(fn.Body);
    }

    [Fact]
    public void Import_alias_and_selective_forms()
    {
        var alias = Assert.IsType<ImportDecl>(Assert.Single(ParseModule("import a.b as C;").module.Declarations));
        Assert.Equal(["a", "b"], alias.Path);
        Assert.Equal("C", Assert.IsType<ImportAlias>(alias.Clause).Alias);

        var sel = Assert.IsType<ImportDecl>(Assert.Single(ParseModule("import a { x, y };").module.Declarations));
        Assert.Equal(["x", "y"], Assert.IsType<ImportSelective>(sel.Clause).Names);
    }

    [Fact]
    public void Function_captures_generics_params_return_throws()
    {
        var (m, de) = ParseModule("fn f<T>(x: T): int throws E { return 0; }");
        Assert.False(de.HasErrors);
        var fn = Assert.IsType<FunctionDecl>(m.Declarations[0]);
        Assert.Equal("T", Assert.Single(fn.Generics).Name);
        Assert.Equal("x", Assert.Single(fn.Parameters).Name);
        Assert.IsType<NamedType>(fn.ReturnType);
        Assert.IsType<NamedType>(fn.Throws!.Type);
    }

    [Fact]
    public void Abstract_function_has_no_body()
    {
        Assert.Null(Assert.IsType<FunctionDecl>(ParseModule("fn getHp(): int;").module.Declarations[0]).Body);
    }

    [Fact]
    public void Throws_without_type_is_any()
    {
        var fn = Assert.IsType<FunctionDecl>(ParseModule("fn risky() throws { }").module.Declarations[0]);
        Assert.NotNull(fn.Throws);
        Assert.Null(fn.Throws!.Type);   // 'throws' ohne Typ
    }

    [Fact]
    public void Params_parameter_flag_is_set()
    {
        var fn = Assert.IsType<FunctionDecl>(ParseModule("fn log(params xs: string[]) { }").module.Declarations[0]);
        Assert.True(Assert.Single(fn.Parameters).IsParams);
    }

    [Fact]
    public void Struct_captures_interfaces_and_ordered_members()
    {
        var (m, de) = ParseModule("struct V :: [Eq] { x: int, fn get(): int { return this.x; } }");
        Assert.False(de.HasErrors);
        var s = Assert.IsType<StructDecl>(m.Declarations[0]);
        Assert.Single(s.Interfaces);
        Assert.IsType<FieldDecl>(s.Members[0]);
        Assert.IsType<FunctionDecl>(s.Members[1]);
    }

    [Fact]
    public void Mut_method_flag_is_set()
    {
        var s = Assert.IsType<StructDecl>(ParseModule("struct S { mut fn go() { } }").module.Declarations[0]);
        Assert.True(Assert.IsType<FunctionDecl>(Assert.Single(s.Members)).IsMut);
    }

    [Fact]
    public void Enum_variant_shapes()
    {
        var (m, de) = ParseModule("enum E { A, B(int), C { x: int } }");
        Assert.False(de.HasErrors);
        var e = Assert.IsType<EnumDecl>(m.Declarations[0]);
        Assert.Equal(3, e.Variants.Length);
        Assert.Null(e.Variants[0].TupleFields);            // A: unit
        Assert.Null(e.Variants[0].StructFields);
        Assert.Single(e.Variants[1].TupleFields!);         // B(int)
        Assert.Single(e.Variants[2].StructFields!);        // C { x: int }
    }

    [Fact]
    public void Generic_constraints_parse()
    {
        var fn = Assert.IsType<FunctionDecl>(ParseModule("fn f<T :: [Ord, Eq]>(): void { }").module.Declarations[0]);
        Assert.Equal(2, Assert.Single(fn.Generics).Constraints.Length);
    }

    [Fact]
    public void Global_var_is_reported()
    {
        Assert.Contains(ParseModule("var x = 1;").diag.Diagnostics, d => d.Code == "LYR-PAR0027");
    }

    [Fact]
    public void Type_alias_parses()
    {
        var (m, de) = ParseModule("type Id = int;");
        Assert.False(de.HasErrors);
        Assert.Equal("Id", Assert.IsType<TypeAliasDecl>(m.Declarations[0]).Name);
    }

    [Theory]
    [InlineData("fn")]
    [InlineData("fn f(")]
    [InlineData("struct")]
    [InlineData("struct S {")]
    [InlineData("import")]
    [InlineData("enum E {")]
    [InlineData("pub")]
    [InlineData("1 + 2;")]
    public void Module_parser_never_throws_and_reports(string source)
    {
        var (m, de) = ParseModule(source);
        Assert.NotNull(m);
        Assert.True(de.HasErrors);
    }

    // --- Patterns + match + if-expression (§6.2/§6.3) ---

    private static (Pattern pattern, DiagnosticEngine diag) ParsePattern(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var pattern = new Parser(sm, id, de).ParsePattern();
        return (pattern, de);
    }

    [Fact]
    public void Wildcard_and_binding_patterns()
    {
        Assert.IsType<WildcardPattern>(ParsePattern("_").pattern);
        Assert.Equal("x", Assert.IsType<BindingPattern>(ParsePattern("x").pattern).Name);
    }

    [Fact]
    public void Tuple_variant_pattern()
    {
        var v = Assert.IsType<VariantPattern>(ParsePattern("Circle(r)").pattern);
        Assert.Equal(["Circle"], v.Path);
        Assert.Single(v.TupleElements!);
        Assert.Null(v.StructFields);
    }

    [Fact]
    public void Struct_variant_pattern()
    {
        var v = Assert.IsType<VariantPattern>(ParsePattern("Triangle { a, b, c }").pattern);
        Assert.Null(v.TupleElements);
        Assert.Equal(3, v.StructFields!.Length);
    }

    [Fact]
    public void Or_pattern_flattens_alternatives()
    {
        Assert.Equal(3, Assert.IsType<OrPattern>(ParsePattern("1 | 2 | 3").pattern).Alternatives.Length);
    }

    [Fact]
    public void Inclusive_range_pattern()
    {
        Assert.True(Assert.IsType<RangePattern>(ParsePattern("0..=9").pattern).IsInclusive);
    }

    [Fact]
    public void Qualified_path_is_variant_not_binding()
    {
        var v = Assert.IsType<VariantPattern>(ParsePattern("Shape.Circle").pattern);
        Assert.Equal(["Shape", "Circle"], v.Path);
        Assert.Null(v.TupleElements);   // Unit-Variante, aber qualifiziert
        Assert.Null(v.StructFields);
    }

    [Fact]
    public void Match_as_expression()
    {
        var (e, de) = Parse("match (n) { 0 => \"z\", _ => \"o\" }");
        Assert.False(de.HasErrors);
        Assert.Equal(2, Assert.IsType<MatchExpr>(e).Arms.Length);
    }

    [Fact]
    public void If_expression_branches_are_expressions()
    {
        var (e, de) = Parse("if (a) 1 else 2");
        Assert.False(de.HasErrors);
        var ifx = Assert.IsType<IfExpr>(e);
        Assert.IsType<IntLiteralExpr>(ifx.Then);   // Branch ist ein Ausdruck, kein Block
        Assert.IsType<IntLiteralExpr>(ifx.Else);
    }

    [Fact]
    public void Else_if_chain_is_nested_if_expression()
    {
        var ifx = Assert.IsType<IfExpr>(Parse("if (a) 1 else if (b) 2 else 3").expr);
        Assert.IsType<IfExpr>(ifx.Else);           // 'else if' → geschachteltes IfExpr
    }

    [Fact]
    public void If_expression_without_else_reports()
    {
        Assert.Contains(Parse("if (a) 1").diag.Diagnostics, d => d.Code == "LYR-PAR0036");
    }

    [Theory]
    [InlineData("(")]
    [InlineData("Circle(")]
    [InlineData("|")]
    [InlineData("Point {")]
    public void Pattern_parser_never_throws_and_reports(string source)
    {
        var (p, de) = ParsePattern(source);
        Assert.NotNull(p);
        Assert.True(de.HasErrors);
    }
}
