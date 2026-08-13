using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Flow narrowing applies in the <c>if</c> EXPRESSION too, not only in the statement.
///
/// <para><c>if (a == null) 0 else a</c> was a type error while
/// <c>if (a == null) { return 0; } return a;</c> worked next to it — the same proof about the same
/// value, two different answers.</para>
///
/// <para>The machinery was fully there (<c>NarrowingFacts</c>, <c>Apply</c>); it was not connected at
/// this one place. Noticed while building <c>std.fmt</c>, where <c>digitToChar</c> suggests exactly
/// this form.</para>
/// </summary>
public class ExpressionNarrowingTests
{
    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void Allowed(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join(" | ", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Fact]
    public void The_else_branch_knows_the_value_is_not_null() =>
        Allowed("""
            fn main(): int {
                let a: ?int = 5;
                let s = if (a == null) 0 else a;
                return s;
            }
            """);

    [Fact]
    public void The_then_branch_knows_it_too() =>
        Allowed("""
            fn main(): int {
                let a: ?int = 5;
                let s = if (a != null) a else 0;
                return s;
            }
            """);

    [Fact]
    public void Narrowing_does_not_leak_out_of_the_expression() =>
        // After the expression '?int' applies again. Otherwise the narrowing would be no proof about a
        // branch but a silent redeclaration, and an 'a = null' afterwards would suddenly be an error.
        Assert.Contains(Check("""
            fn main(): int {
                let a: ?int = 5;
                let s = if (a != null) a else 0;
                return a;
            }
            """).Diagnostics, d => d.Code == "LYR-SEM0001");

    [Fact]
    public void A_nested_expression_narrows_too() =>
        // The inner expression must not disturb the outer one: 'b' is checked in the then branch of 'a',
        // and afterwards 'a' still has to be narrowed there.
        Allowed("""
            fn main(): int {
                let a: ?int = 5;
                let b: ?int = 7;
                let s = if (a == null) (if (b == null) 0 else b) else a;
                return s;
            }
            """);

    [Fact]
    public void The_statement_form_still_works() =>
        // The counter-check: what worked before has to keep working. The snapshot handling in the
        // expression touches the same data structure as the statement.
        Allowed("""
            fn main(): int {
                let a: ?int = 5;
                if (a == null) { return 0; }
                return a;
            }
            """);
}
