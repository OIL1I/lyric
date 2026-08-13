using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// What a closure captures and what of it is shared rather than copied.
///
/// <para>The capture list itself is old; new is the question which symbols end up in a CELL. Answering
/// it is cheap — a captured <c>var</c> — but answering it wrongly is expensive: one cell too many
/// costs a heap allocation per call, one too few lets the closure and the function see different
/// values, and that shows only at runtime.</para>
/// </summary>
public class CaptureTests
{
    private static (TypeResult Types, DiagnosticEngine De, Module Ast) Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        var ast = new Parser(sm, id, de).ParseModule();
        comp.AddModule(ast);
        return (Semantics.Analyze(comp, comp.Resolve(), de), de, ast);
    }

    /// <summary>All lambdas of a module in source order.</summary>
    private static List<LambdaExpr> Lambdas(Module ast)
    {
        var found = new List<LambdaExpr>();
        void Walk(Node? n)
        {
            switch (n)
            {
                case null: return;
                case LambdaExpr lam: found.Add(lam); Walk(lam.Body); return;
                case Module m: foreach (var d in m.Declarations) Walk(d); return;
                case ClassDecl c2: foreach (var d in c2.Members) Walk(d); return;
                case StructDecl s2: foreach (var d in s2.Members) Walk(d); return;
                case FunctionDecl f: Walk(f.Body); return;
                case Block b: foreach (var st in b.Statements) Walk(st); return;
                case BindingStmt bd: Walk(bd.Initializer); return;
                case ReturnStmt r: Walk(r.Value); return;
                case ExprStmt es: Walk(es.Expr); return;
                case IfStmt f2: Walk(f2.Condition); Walk(f2.Then); Walk(f2.Else); return;
                case WhileStmt w: Walk(w.Condition); Walk(w.Body); return;
                case CallExpr c: Walk(c.Callee); foreach (var a in c.Arguments) Walk(a); return;
                case BinaryExpr b2: Walk(b2.Left); Walk(b2.Right); return;
                case AssignExpr a2: Walk(a2.Target); Walk(a2.Value); return;
            }
        }
        Walk(ast);
        return found;
    }

    private static (string[] Names, bool This) CapturesOf(string source, int lambdaIndex = 0)
    {
        var (types, de, ast) = Check(source);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        var (symbols, capturesThis) = types.CapturesOf(Lambdas(ast)[lambdaIndex]);
        return (symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(), capturesThis);
    }

    private static bool IsBoxed(string source, string local)
    {
        var (types, de, ast) = Check(source);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        var (symbols, _) = types.CapturesOf(Lambdas(ast)[0]);
        var symbol = symbols.Single(s => s.Name == local);
        return types.IsBoxed(symbol);
    }

    // ------------------------------------------------------------------ what is captured

    [Fact]
    public void An_outer_let_is_captured() =>
        Assert.Equal(["factor"], CapturesOf("""
            fn main(): int { let factor = 3; let f = (x: int) => x * factor; return f(2); }
            """).Names);

    [Fact]
    public void A_lambdas_own_parameter_is_not_a_capture() =>
        // The span test in RecordCaptures sorts out what is declared INSIDE. Without it every parameter
        // would be a capture and every closure would carry its own environment twice.
        Assert.Empty(CapturesOf("""
            fn main(): int { let f = (x: int) => x * 2; return f(2); }
            """).Names);

    [Fact]
    public void A_local_declared_inside_the_lambda_is_not_a_capture() =>
        Assert.Empty(CapturesOf("""
            fn main(): int { let f = (x: int): int => { let k = 2; return x * k; }; return f(2); }
            """).Names);

    [Fact]
    public void This_is_recorded_separately() =>
        // 'this' is no symbol but parameter 0, hence a flag of its own rather than an entry in the list.
        Assert.True(CapturesOf("""
            class C { n: int, fn get(): fn() -> int { return () => this.n; } }
            fn main(): int { return 0; }
            """).This);

    [Fact]
    public void A_global_is_not_captured() =>
        // A global lies in a module-wide slot and is reachable from everywhere; copying it into the
        // environment would be a second way of accessing the same thing.
        Assert.Empty(CapturesOf("""
            let g = 7;
            fn main(): int { let f = (x: int) => x + g; return f(1); }
            """).Names);

    // ------------------------------------------------------------------ what is shared

    [Fact]
    public void A_captured_var_is_boxed() =>
        Assert.True(IsBoxed("""
            fn main(): int { var n = 0; let f = (): int => { n += 1; return n; }; return f(); }
            """, "n"));

    [Fact]
    public void A_captured_let_is_not_boxed() =>
        // The case that saves the cell: 'factor' never changes, so copying is indistinguishable from
        // sharing.
        Assert.False(IsBoxed("""
            fn main(): int { let factor = 3; let f = (x: int) => x * factor; return f(2); }
            """, "factor"));

    [Fact]
    public void A_captured_parameter_is_not_boxed() =>
        // Parameters are immutable (LYR-SEM0019), so the same holds for them as for a let.
        Assert.False(IsBoxed("""
            fn outer(k: int): fn(int) -> int { return (x: int) => x + k; }
            fn main(): int { return outer(1)(2); }
            """, "k"));

    [Fact]
    public void A_var_that_is_never_captured_stays_in_its_slot()
    {
        // The counter-check: not every 'var' is boxed, only a captured one. Without this test a rule
        // "box every var" would pass all the other tests here as well.
        var (types, de, ast) = Check("""
            fn main(): int { var loose = 1; let c = 2; let f = (x: int) => x + c; loose += 1; return f(loose); }
            """);
        Assert.False(de.HasErrors);

        var (symbols, _) = types.CapturesOf(Lambdas(ast)[0]);
        Assert.DoesNotContain(symbols, s => s.Name == "loose");
    }

    [Fact]
    public void Two_lambdas_capturing_the_same_var_both_see_it_boxed()
    {
        // Shared means shared: two closures over the same 'var' have to get the same cell, not two.
        var (types, de, ast) = Check("""
            fn main(): int {
                var n = 0;
                let inc = (): int => { n += 1; return n; };
                let get = (): int => n;
                inc();
                return get();
            }
            """);
        Assert.False(de.HasErrors);

        var lambdas = Lambdas(ast);
        var first = types.CapturesOf(lambdas[0]).Symbols.Single();
        var second = types.CapturesOf(lambdas[1]).Symbols.Single();

        Assert.Same(first, second);
        Assert.True(types.IsBoxed(first));
    }
}
