using System.Runtime.CompilerServices;
using System.Text;
using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Xunit;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Golden tests for the parser. Every fixture (golden/&lt;name&gt;.lyr) contains exactly ONE top-level
/// form — an expression or a statement, with a block covering sequences. It is parsed and the AST dump,
/// plus rendered diagnostics for negative cases, is compared against the committed snapshot
/// (golden/&lt;name&gt;.ast).
///
/// Snapshots are NOT maintained by hand: produce them once with the environment variable
/// LYRIC_UPDATE_SNAPSHOTS=1, read them over, commit. From then on the comparison locks the AST.
/// </summary>
public class GoldenTests
{
    private static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LYRIC_UPDATE_SNAPSHOTS") is "1" or "true";

    // [CallerFilePath] yields this file's path at compile time, so snapshots are read and
    // written in the source tree rather than in the bin/ output.
    private static string GoldenDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "golden");

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string Dump(string displayName, string source, Func<Parser, Node> parse)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual(displayName, source);
        var de = new DiagnosticEngine(sm);
        var node = parse(new Parser(sm, id, de));

        var dump = Normalize(AstDumper.Dump(node, sm));
        if (!dump.EndsWith('\n')) dump += "\n";

        if (de.Count == 0) return dump;

        var sw = new StringWriter(new StringBuilder()) { NewLine = "\n" };
        de.RenderText(sw);
        return dump + "\n=== diagnostics ===\n" + Normalize(sw.ToString());
    }

    private static void Check(string name, Func<Parser, Node> parse)
    {
        var dir = GoldenDir();
        var inputPath = Path.Combine(dir, name + ".lyr");
        var snapshotPath = Path.Combine(dir, name + ".ast");

        Assert.True(File.Exists(inputPath), $"missing fixture: {inputPath}");

        var source = File.ReadAllText(inputPath, Encoding.UTF8);
        var actual = Dump(name + ".lyr", source, parse);

        if (UpdateMode)
        {
            File.WriteAllText(snapshotPath, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(snapshotPath),
            $"missing snapshot: {snapshotPath}\n" +
            "Run once with LYRIC_UPDATE_SNAPSHOTS=1 to generate it, then review and commit.");

        var expected = Normalize(File.ReadAllText(snapshotPath, Encoding.UTF8));
        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------------
    // Expressions, entered through ParseExpression.
    // ---------------------------------------------------------------------

    [Theory]
    // Positive cases: no diagnostic, only the AST dump.
    [InlineData("precedence")]        // arithmetic precedence and left associativity
    [InlineData("prefix_postfix")]    // prefix !/-/~/--  vs. postfix ++/!(unwrap)
    [InlineData("assignment")]        // right associativity and compound assignment
    [InlineData("logical_comparison")]// a long precedence chain < == && || ??
    [InlineData("bitwise_shift")]     // << >> & ^ | precedence
    [InlineData("range")]             // ..= against + precedence
    [InlineData("coalesce")]          // ?? right associativity
    [InlineData("cast")]              // 'as' plus a type expression, left associative
    [InlineData("postfix_chain")]     // . ?. ( ) [ ] Kette
    [InlineData("literals")]          // every literal class in one array literal
    [InlineData("fstring")]           // an InterpolatedStringExpr with a hole and a format spec
    [InlineData("fstring_plain")]     // an f-string without interpolation
    [InlineData("array_tuple")]       // an array literal and a tuple literal, nested
    [InlineData("tuple_big")]         // a tuple with 5 elements; there is no arity bound
    [InlineData("empty_array")]       // [] — the empty array literal
    [InlineData("lambda")]            // a LambdaExpr with a parameter type annotation
    [InlineData("nested_lambda")]     // rechts-verschachteltes Lambda
    [InlineData("lambda_block")]      // a lambda with a block body '=> { ... }'
    [InlineData("grouping")]          // parentheses override precedence
    [InlineData("atident")]           // an AtIdentifierExpr with arguments
    [InlineData("match_expr")]        // match as an expression, with or-pattern arms
    [InlineData("if_expr")]           // if/else as an expression
    [InlineData("if_expr_chain")]     // if/else-if/else as an expression
    [InlineData("struct_init")]       // Point { x = 1, y = 2 }
    [InlineData("struct_init_empty")] // Empty { }
    [InlineData("struct_init_nested")]// a nested struct initializer in a field value
    [InlineData("struct_init_qualified")] // dotted TypePath game.Player { … }
    // TypeExpr (§4) — via 'as'-Cast erreicht.
    [InlineData("type_generics")]     // a NamedType with type arguments
    [InlineData("type_nested_generics")] // the '>>' split for nested generics
    [InlineData("type_function")]     // FunctionType fn(...) -> R
    [InlineData("type_array")]        // ArrayType T[N]
    [InlineData("type_nullable")]     // NullableType ?T
    [InlineData("type_tuple")]        // TupleType (A, B, C)
    // Negative cases: the snapshot holds the AST dump, possibly an ErrorExpr, AND rendered diagnostics.
    [InlineData("unclosed_paren")]    // (1 + 2
    [InlineData("missing_rhs")]       // 1 +
    [InlineData("leading_operator")]  // * 3
    [InlineData("type_error")]        // x as 5 — a non-type after 'as'
    public void Golden_expression_matches_snapshot(string name)
        => Check(name, p => p.ParseExpression());

    // ---------------------------------------------------------------------
    // Statements, entered through ParseStatement.
    // ---------------------------------------------------------------------

    [Theory]
    // Positiv.
    [InlineData("let_binding")]       // let x: int = 42;
    [InlineData("var_binding")]       // var y = 1;
    [InlineData("let_no_init")]       // let z: int;
    [InlineData("block_nested")]      // { ... { ... } }
    [InlineData("if_else")]           // if/else
    [InlineData("if_elseif")]         // else-if-Kette
    [InlineData("while_loop")]        // while
    [InlineData("do_while")]          // do { ... } while (...);
    [InlineData("for_in")]            // for (x in ...) { }
    [InlineData("loop_jumps")]        // break; continue;
    [InlineData("return_value")]      // return expr;
    [InlineData("return_void")]       // return;
    [InlineData("yield_resume")]      // yield and resume, including resume with a value
    [InlineData("defer_block")]       // defer { ... }
    [InlineData("defer_expr")]        // defer expr;
    [InlineData("throw_stmt")]        // throw expr;
    [InlineData("try_catch")]         // try/catch (typed + wildcard)
    [InlineData("expr_stmt")]         // call();
    // Negativ.
    [InlineData("missing_semicolon")] // let x = 1
    [InlineData("try_no_catch")]      // try { } without a catch
    [InlineData("if_without_block")]  // if (a) b();
    [InlineData("match_stmt")]        // match (…) { arms } with a guard and a block arm
    [InlineData("struct_init_binding")] // let p = Point { … }; — a struct initializer in value position
    public void Golden_statement_matches_snapshot(string name)
        => Check(name, p => p.ParseStatement());

    // ---------------------------------------------------------------------
    // Declarations and modules, entered through ParseModule.
    // ---------------------------------------------------------------------

    [Theory]
    // Modul + Imports.
    [InlineData("module_header")]     // module a.b;
    [InlineData("import_simple")]     // import a.b;
    [InlineData("import_selective")]  // import a.b { x, y };
    [InlineData("import_alias")]      // import a.b as C;
    // Funktionen.
    [InlineData("fn_simple")]         // fn add(a, b): int { ... }
    [InlineData("fn_abstract")]       // fn getHp(): int;  (bodyless)
    [InlineData("fn_generic")]        // fn map<T, U>(...) plus a function type parameter
    [InlineData("fn_throws")]         // throws FileNotFound
    [InlineData("fn_throws_any")]     // throws without a type
    [InlineData("fn_variadic")]       // params-Parameter
    [InlineData("fn_default_param")]  // a default parameter value
    // Types.
    [InlineData("struct_decl")]       // Felder + Methoden + :: [Interfaces]
    [InlineData("class_decl")]        // a default field value and a mut fn
    [InlineData("enum_decl")]         // tuple, struct and unit variants plus a method
    [InlineData("interface_decl")]    // abstract and default methods
    [InlineData("extend_decl")]       // extend T :: [I] { ... }
    // Alias, Global, ganzes Modul.
    [InlineData("type_alias")]        // type X = int;
    [InlineData("global_let")]        // pub let ...
    [InlineData("module_full")]       // Header + Import + Struct + Fn
    // Negativ.
    [InlineData("global_var")]        // var at top level (LYR-PAR0027)
    [InlineData("bad_toplevel")]      // Ausdruck statt Deklaration
    public void Golden_module_matches_snapshot(string name)
        => Check(name, p => p.ParseModule());

    // ---------------------------------------------------------------------
    // Patterns, entered through ParsePattern.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("pat_wildcard")]       // _
    [InlineData("pat_literal")]        // 42
    [InlineData("pat_binding")]        // x
    [InlineData("pat_tuple_variant")]  // Circle(r)
    [InlineData("pat_struct_variant")] // Triangle { a, b, c }
    [InlineData("pat_tuple")]          // (a, b)
    [InlineData("pat_range")]          // 0..=9
    [InlineData("pat_or")]             // 1 | 2 | 3
    [InlineData("pat_qualified")]      // Shape.Circle
    [InlineData("pat_nested")]         // Wrapper(Circle(r), _)
    [InlineData("pat_field_sub")]      // Point { x = 0, y }
    [InlineData("pat_negative_range")] // -10..=10
    public void Golden_pattern_matches_snapshot(string name)
        => Check(name, p => p.ParsePattern());
}
