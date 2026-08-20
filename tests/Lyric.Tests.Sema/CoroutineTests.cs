using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Coroutine sema: Coroutine&lt;T&gt; as a type form, yield only in coroutines with a value type check
/// (SEM0038), only a bare return (SEM0039), resume as a prefix expression with the yield type as its
/// result (SEM0040), and return coverage suspended for coroutines. The full pipeline through
/// Semantics.Analyze.
/// </summary>
public class CoroutineTests
{
    private const string Prelude = """
        fn fibonacci(): Coroutine<int> {
            var a = 0;
            var b = 1;
            while (true) {
                yield a;
                let next = a + b;
                a = b;
                b = next;
            }
        }
        fn ticker(): Coroutine<void> {
            yield;
            yield;
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
                    case ForInStmt f: Walk(f.Body.Statements); break;
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

    // --- Grundmuster ---

    [Fact]
    public void Fibonacci_pattern_checks_clean()
    {
        AssertClean(Diags("")); // the prelude alone: no SEM0017 despite the Coroutine<int> return type
    }

    [Fact]
    public void Calling_a_coroutine_yields_the_coroutine_type()
    {
        var (t, de) = LastInit("fn u() { let co = fibonacci(); }");
        AssertClean(de);
        AssertType(new CoroutineOf(LyrType.Int), t);
    }

    [Fact]
    public void Resume_yields_the_element_type()
    {
        var (t, de) = LastInit("fn u() { let co = fibonacci(); let v = resume co; }");
        AssertClean(de);
        AssertType(LyrType.Int, t);
    }

    [Fact]
    public void Resume_composes_as_expression()
    {
        var (t, de) = LastInit("fn u() { let co = fibonacci(); let v = resume co + 1; }");
        AssertClean(de);
        AssertType(LyrType.Int, t); // (resume co) + 1
    }

    [Fact]
    public void Resume_as_statement_is_allowed()
    {
        AssertClean(Diags("fn u() { let t = ticker(); resume t; }"));
    }

    [Fact]
    public void Driving_loop_checks_clean()
    {
        AssertClean(Diags("""
            fn u() {
                let co = fibonacci();
                for (i in 0..10) {
                    let v = resume co;
                }
            }
            """));
    }

    // --- yield rules (SEM0038) ---

    [Fact]
    public void Yield_outside_coroutine_is_reported()
    {
        Assert.Contains(Diags("fn u(): int { yield 1; return 0; }").Diagnostics, d => d.Code == "LYR-SEM0038");
    }

    [Fact]
    public void Yield_in_lambda_is_reported()
    {
        Assert.Contains(Diags("fn co(): Coroutine<int> { let f = (x: int) => { yield 1; return x; }; yield 2; }")
            .Diagnostics, d => d.Code == "LYR-SEM0038");
    }

    [Fact]
    public void Yield_value_type_is_checked()
    {
        Assert.Contains(Diags("""fn co(): Coroutine<int> { yield "nope"; }""").Diagnostics,
            d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Bare_yield_requires_void_coroutine()
    {
        AssertClean(Diags("fn u() { let t = ticker(); }"));
        Assert.Contains(Diags("fn co(): Coroutine<int> { yield; }").Diagnostics, d => d.Code == "LYR-SEM0038");
    }

    // --- return rules (SEM0039) ---

    [Fact]
    public void Bare_return_ends_a_coroutine_early()
    {
        AssertClean(Diags("""
            fn take(xs: int[], limit: int): Coroutine<int> {
                var n = 0;
                for (x in xs) {
                    if (n >= limit) { return; }
                    yield x;
                    n += 1;
                }
            }
            """));
    }

    [Fact]
    public void Return_with_value_in_coroutine_is_reported()
    {
        Assert.Contains(Diags("fn co(): Coroutine<int> { yield 1; return 5; }").Diagnostics,
            d => d.Code == "LYR-SEM0039");
    }

    // --- resume rules (SEM0040) ---

    [Fact]
    public void Resume_on_non_coroutine_is_reported()
    {
        Assert.Contains(Diags("fn u(n: int) { let v = resume n; }").Diagnostics, d => d.Code == "LYR-SEM0040");
    }

    // --- the type form Coroutine<T> ---

    [Fact]
    public void Coroutine_needs_exactly_one_type_argument()
    {
        Assert.Contains(Diags("fn u(c: Coroutine) { }").Diagnostics, d => d.Code == "LYR-SEM0026");
        Assert.Contains(Diags("fn u(c: Coroutine<int, int>) { }").Diagnostics, d => d.Code == "LYR-SEM0026");
    }

    [Fact]
    public void Coroutine_type_substitutes_through_generics()
    {
        var (t, de) = LastInit("""
            fn identity<T>(x: T): T { return x; }
            fn u() { let co = identity(fibonacci()); }
            """);
        AssertClean(de);
        AssertType(new CoroutineOf(LyrType.Int), t); // T = Coroutine<int>
    }

    // --- next(): the safe pull (v2.2) ---

    [Fact]
    public void Next_yields_the_optional_of_the_element_type()
    {
        var (t, de) = LastInit("fn u() { let co = fibonacci(); let v = co.next(); }");
        AssertClean(de);
        AssertType(new Optional(LyrType.Int), t);
    }

    [Fact]
    public void Next_on_a_void_coroutine_answers_bool()
    {
        var (t, de) = LastInit("fn u() { let t = ticker(); let advanced = t.next(); }");
        AssertClean(de);
        AssertType(LyrType.Bool, t);
    }

    [Fact]
    public void Next_on_an_optional_yield_is_refused()
    {
        // '?T' from next and a yielded null would be indistinguishable; SEM0080 names the way out.
        Assert.Contains(Diags("""
            fn maybe(): Coroutine<?int> { yield null; yield 1; }
            fn u() { let co = maybe(); let v = co.next(); }
            """).Diagnostics, d => d.Code == "LYR-SEM0080");
    }

    [Fact]
    public void A_member_other_than_next_stays_unknown()
    {
        Assert.Contains(Diags("fn u() { let co = fibonacci(); let v = co.isDone(); }").Diagnostics,
            d => d.Code == "LYR-SEM0012");
    }
}
