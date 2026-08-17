using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Lambdas + Closures — M4-Slice 4a (docs/Grammar.md §6.2). Bidirektionale
/// Inference: unannotated parameters take the context FnType (a call argument, a binding, a return, a
/// field); generic calls run in two phases (T from the eagerly typed arguments, U from the lambda
/// return). Block lambdas deliver values through 'return' and need an annotation or a context
/// (SEM0046); a 'return' checks against the lambda. Captures are recorded.
/// </summary>
public class LambdaTests
{
    private const string Prelude = """
        fn apply(f: fn(int) -> int): int { return f(1); }
        fn each(f: fn(int) -> void): void { f(1); }
        fn map<T, U>(xs: T[], f: fn(T) -> U): U[] { return [f(xs[0])]; }
        struct Handler { cb: fn(int) -> int }
        class Counter {
            n: int = 0,
            fn adder(): fn(int) -> int {
                return (d) => this.n + d;
            }
        }
        """;

    private static (TypeResult types, DiagnosticEngine de, Module module) Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        var module = new Parser(sm, id, de).ParseModule();
        comp.AddModule(module);
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);
        return (types, de, module);
    }

    private static List<BindingStmt> Bindings(IEnumerable<Stmt> stmts)
    {
        var acc = new List<BindingStmt>();
        void Walk(IEnumerable<Stmt> ss)
        {
            foreach (var s in ss)
                switch (s)
                {
                    case BindingStmt b: acc.Add(b); break;
                    case Block bl: Walk(bl.Statements); break;
                    case IfStmt i: Walk(i.Then.Statements); if (i.Else is Block eb) Walk(eb.Statements); break;
                }
        }
        Walk(stmts);
        return acc;
    }

    private static (LyrType type, DiagnosticEngine de) LastInit(string body)
    {
        var (types, de, module) = Check(Prelude + "\n" + body);
        var init = module.Declarations.OfType<FunctionDecl>()
            .Where(f => f.Body is not null)
            .SelectMany(f => Bindings(f.Body!.Statements))
            .Last().Initializer!;
        return (types.TypeOf(init), de);
    }

    private static DiagnosticEngine Diags(string body) => Check(Prelude + "\n" + body).de;

    private static void AssertClean(DiagnosticEngine de) =>
        Assert.False(de.HasErrors, string.Join("; ", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

    private static void AssertType(LyrType expected, LyrType actual) =>
        Assert.True(LyrType.Equal(expected, actual), $"expected '{TypeFacts.Display(expected)}', got '{TypeFacts.Display(actual)}'");

    // --- context from the call position ---

    [Fact]
    public void Call_argument_types_unannotated_params()
    {
        var (t, de) = LastInit("fn u() { let r = apply((x) => x + 1); }");
        AssertClean(de);
        AssertType(LyrType.Int, t);
    }

    [Fact]
    public void Generic_call_infers_T_then_types_the_lambda()
    {
        var (t, de) = LastInit("fn u(xs: int[]) { let ys = map(xs, (x) => x * 2); }");
        AssertClean(de);
        AssertType(new ArrayOf(LyrType.Int, null), t);
    }

    [Fact]
    public void Generic_U_is_inferred_from_the_lambda_return()
    {
        var (t, de) = LastInit("""fn u(xs: int[]) { let ys = map(xs, (x) => f"{x}"); }""");
        AssertClean(de);
        AssertType(new ArrayOf(LyrType.String, null), t); // U = string from the lambda body
    }

    [Fact]
    public void Lambda_body_type_mismatch_in_call_is_reported()
    {
        var de = Diags("""fn u() { let r = apply((x) => "nope"); }""");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    // --- context from a binding, a return or a field ---

    [Fact]
    public void Binding_context_types_the_lambda()
    {
        var (t, de) = LastInit("fn u() { let f: fn(int) -> int = (x) => x + 1; }");
        AssertClean(de);
        AssertType(new FnType([LyrType.Int], LyrType.Int), t);
    }

    [Fact]
    public void Binding_context_checks_the_body_type()
    {
        var de = Diags("fn u() { let f: fn(int) -> string = (x) => x + 1; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Return_position_provides_context()
    {
        AssertClean(Diags("fn mk(): fn(int) -> int { return (x) => x + 1; }"));
    }

    [Fact]
    public void Struct_field_provides_context()
    {
        AssertClean(Diags("fn u() { let h = Handler { cb = (x) => x * 3 }; }"));
    }

    [Fact]
    public void Annotated_lambda_needs_no_context()
    {
        var (t, de) = LastInit("fn u() { let f = (x: int) => x + 1; }");
        AssertClean(de);
        AssertType(new FnType([LyrType.Int], LyrType.Int), t);
    }

    [Fact]
    public void Unannotated_lambda_without_context_is_reported()
    {
        var de = Diags("fn u() { let f = (x) => x + 1; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0045");
    }

    [Fact]
    public void Nested_lambda_gets_context_through_the_outer_return()
    {
        var (t, de) = LastInit("fn u() { let f: fn(int) -> fn(int) -> int = (a) => (b) => a + b; }");
        AssertClean(de);
        AssertType(new FnType([LyrType.Int], new FnType([LyrType.Int], LyrType.Int)), t);
    }

    // --- block lambdas ---

    [Fact]
    public void Block_lambda_with_context_is_clean()
    {
        AssertClean(Diags("fn u() { let f: fn(int) -> int = (x) => { return x + 1; }; }"));
    }

    [Fact]
    public void Block_lambda_with_annotation_is_clean()
    {
        AssertClean(Diags("fn u() { let f = (x: int): int => { return x + 1; }; }"));
    }

    [Fact]
    public void Return_in_lambda_checks_against_the_lambda()
    {
        var de = Diags("""fn u() { let f: fn(int) -> int = (x) => { return "no"; }; }""");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001"); // string to int, on the lambda return
    }

    [Fact]
    public void Return_in_lambda_does_not_leak_into_the_function()
    {
        // 'return x' with an int belongs to the lambda; the enclosing function returns a string.
        AssertClean(Diags("""
            fn t(): string {
                let f: fn(int) -> int = (x) => { return x; };
                return "ok";
            }
            """));
    }

    [Fact]
    public void Value_returning_block_lambda_without_context_is_reported()
    {
        var de = Diags("fn u() { let f = (x: int) => { return x + 1; }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0046");
    }

    // --- valueless block lambdas without a context are void ---

    [Fact]
    public void Void_block_lambda_without_context_defaults_to_void()
    {
        var (t, de) = LastInit("fn u() { let f = () => { let y = 1; }; }");
        AssertClean(de);
        AssertType(new FnType([], LyrType.Void), t);
    }

    [Fact]
    public void Side_effect_block_lambda_without_context_is_clean()
    {
        AssertClean(Diags("fn u(xs: int[]) { let printAll = (ys: int[]) => { for (y in ys) { } }; }"));
    }

    [Fact]
    public void Bare_return_block_lambda_without_context_defaults_to_void()
    {
        var (t, de) = LastInit("fn u() { let f = (x: int) => { if (x < 0) { return; } }; }");
        AssertClean(de);
        AssertType(new FnType([LyrType.Int], LyrType.Void), t);
    }

    [Fact]
    public void Void_defaulted_block_lambda_returning_a_value_is_still_flagged()
    {
        // A value return in the body gives HasValueReturn and SEM0046, not the void default.
        var de = Diags("fn u() { let f = (x: int) => { let y = x; return y; }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0046");
    }

    [Fact]
    public void Non_void_block_lambda_needs_full_return_coverage()
    {
        var de = Diags("fn u() { let f: fn(int) -> int = (x) => { let y = x; }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0046");
    }

    [Fact]
    public void Void_block_lambda_needs_no_return()
    {
        AssertClean(Diags("fn u() { each((x) => { let y = x + 1; }); }"));
    }

    [Fact]
    public void Void_expression_lambda_discards_the_value()
    {
        AssertClean(Diags("fn u() { each((x) => x + 1); }")); // the value is discarded, which is no error
    }

    // --- Captures (ADR-011) ---

    [Fact]
    public void Captures_of_locals_and_params_are_recorded()
    {
        var (types, de, module) = Check(Prelude + """

            fn t(base: int) {
                var n = 0;
                let f: fn(int) -> int = (d) => base + n + d;
            }
            """);
        AssertClean(de);
        var lam = (LambdaExpr)module.Declarations.OfType<FunctionDecl>()
            .First(f => f.Name == "t").Body!.Statements.OfType<BindingStmt>().Last().Initializer!;
        var (captured, capturesThis) = types.CapturesOf(lam);
        Assert.False(capturesThis);
        Assert.Equal(["base", "n"], captured.Select(s => s.Name).Order().ToArray());
    }

    [Fact]
    public void This_capture_is_recorded()
    {
        var (types, de, module) = Check(Prelude);
        AssertClean(de);
        var counter = module.Declarations.OfType<ClassDecl>().First(c => c.Name == "Counter");
        var adder = counter.Members.OfType<FunctionDecl>().First(m => m.Name == "adder");
        var lam = (LambdaExpr)((ReturnStmt)adder.Body!.Statements[0]).Value!;
        var (captured, capturesThis) = types.CapturesOf(lam);
        Assert.True(capturesThis);
        Assert.Empty(captured);
    }

    // --- definite assignment across the lambda boundary ---

    [Fact]
    public void Unassigned_capture_at_creation_is_reported()
    {
        var de = Diags("fn t() { var x: int; let f: fn(int) -> int = (d) => x + d; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0018");
    }

    [Fact]
    public void Assigned_capture_is_clean()
    {
        AssertClean(Diags("fn t() { var x: int; x = 1; let f: fn(int) -> int = (d) => x + d; }"));
    }
}
