using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Was eine Closure einfängt (ADR-011) und was davon geteilt statt kopiert wird (ADR-018).
///
/// <para>Die Capture-Liste selbst gibt es seit M4; neu ist die Frage, welche Symbole in einer
/// <b>Zelle</b> landen. Sie zu beantworten ist billig — ein <c>var</c>, das gefangen wird —, aber
/// sie falsch zu beantworten ist teuer: eine Zelle zu viel kostet eine Heap-Allokation pro
/// Aufruf, eine zu wenig lässt Closure und Funktion verschiedene Werte sehen, und das fällt erst
/// zur Laufzeit auf.</para>
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

    /// <summary>Alle Lambdas eines Moduls in Quelltext-Reihenfolge.</summary>
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

    // ------------------------------------------------------------------ was gefangen wird

    [Fact]
    public void An_outer_let_is_captured() =>
        Assert.Equal(["factor"], CapturesOf("""
            fn main(): int { let factor = 3; let f = (x: int) => x * factor; return f(2); }
            """).Names);

    [Fact]
    public void A_lambdas_own_parameter_is_not_a_capture() =>
        // Der Span-Test in RecordCaptures sortiert aus, was INNEN deklariert ist. Ohne ihn waere
        // jeder Parameter ein Capture und jede Closure traege ihr eigenes Environment doppelt.
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
        // 'this' ist kein Symbol, sondern Parameter 0 (ADR-014) — deshalb ein eigenes Flag und
        // kein Eintrag in der Liste.
        Assert.True(CapturesOf("""
            class C { n: int, fn get(): fn() -> int { return () => this.n; } }
            fn main(): int { return 0; }
            """).This);

    [Fact]
    public void A_global_is_not_captured() =>
        // Ein Global liegt in einem modulweiten Slot (P5c) und ist von ueberall erreichbar; es
        // ins Environment zu kopieren waere eine zweite Zugriffsart auf dieselbe Sache.
        Assert.Empty(CapturesOf("""
            let g = 7;
            fn main(): int { let f = (x: int) => x + g; return f(1); }
            """).Names);

    // ------------------------------------------------------------------ was geteilt wird

    [Fact]
    public void A_captured_var_is_boxed() =>
        Assert.True(IsBoxed("""
            fn main(): int { var n = 0; let f = (): int => { n += 1; return n; }; return f(); }
            """, "n"));

    [Fact]
    public void A_captured_let_is_not_boxed() =>
        // Der Fall, der die Zelle spart: 'factor' aendert sich nie, also ist Kopieren von Teilen
        // nicht unterscheidbar.
        Assert.False(IsBoxed("""
            fn main(): int { let factor = 3; let f = (x: int) => x * factor; return f(2); }
            """, "factor"));

    [Fact]
    public void A_captured_parameter_is_not_boxed() =>
        // Parameter sind unveraenderlich (LYR-SEM0019), also gilt fuer sie dasselbe wie fuer let.
        Assert.False(IsBoxed("""
            fn outer(k: int): fn(int) -> int { return (x: int) => x + k; }
            fn main(): int { return outer(1)(2); }
            """, "k"));

    [Fact]
    public void A_var_that_is_never_captured_stays_in_its_slot()
    {
        // Die Gegenprobe: nicht jedes 'var' wird geboxt, nur ein gefangenes. Ohne diesen Test
        // wuerde eine Regel „alle var boxen" alle anderen Tests hier ebenfalls bestehen.
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
        // Geteilt heisst geteilt: zwei Closures ueber demselben 'var' muessen dieselbe Zelle
        // bekommen, nicht zwei.
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
