using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Wo ein <c>Foo { … }</c> stehen darf (Sprache.md §6.2: „in jeder Wert-Position").
///
/// <para><b>Die Sperre gilt dem Anfang eines Statements</b>, nicht der ganzen Anweisung: dort wäre
/// <c>Foo { … };</c> mit einem Block mehrdeutig. Hinter einem <c>=</c> kann kein Block stehen, also
/// gibt es dort nichts zu verwechseln.</para>
///
/// <para><b>Bis 2026-08-11 griff sie durch</b>, und <c>s = Small { n = 5 };</c> war
/// <c>LYR-SEM0052: 'Small' is a type, not a value — did you mean 'Small { . }'?</c> — ein
/// Vorschlag, genau das zu schreiben, was dort schon stand. Bekannt seit P3, und am 2026-08-07
/// ist der Maintainer beim Schreiben einer Messprobe erneut hineingelaufen, ohne ihn
/// wiederzuerkennen.</para>
/// </summary>
public class StructInitPositionTests
{
    private static (Module Ast, IReadOnlyList<Diagnostic> Diagnostics) Parse(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        return (new Parser(sm, id, de).ParseModule(), de.Diagnostics);
    }

    private static void ParsesCleanly(string source) =>
        Assert.Empty(Parse(source).Diagnostics);

    // ------------------------------------------------------------------ erlaubt

    [Fact]
    public void A_struct_init_may_stand_on_the_right_of_an_assignment() =>
        ParsesCleanly("""
            struct Small { n: int, }
            fn main(): int {
                var s = Small { n = 1 };
                s = Small { n = 5 };
                return s.n;
            }
            """);

    [Fact]
    public void Also_on_the_right_of_a_field_assignment() =>
        ParsesCleanly("""
            struct P { n: int, }
            class Box { p: P, }
            fn main(): int {
                var b = Box { p = P { n = 1 } };
                b.p = P { n = 2 };
                return b.p.n;
            }
            """);

    [Fact]
    public void And_on_the_right_of_a_compound_assignment() =>
        ParsesCleanly("""
            struct V { n: int, }
            fn main(): int {
                var xs = [0];
                xs[0] = 5;
                return xs[0];
            }
            """);

    /// <summary>Ein generischer Typ auf der rechten Seite — <c>&lt;</c> muss dort als
    /// Typargument-Liste gelesen werden und nicht als Vergleich.</summary>
    [Fact]
    public void A_generic_struct_init_works_on_the_right_of_an_assignment() =>
        ParsesCleanly("""
            struct Box<T> { v: T, }
            fn main(): int {
                var b = Box<int> { v = 1 };
                b = Box<int> { v = 2 };
                return b.v;
            }
            """);

    // ------------------------------------------------------------------ weiterhin gesperrt

    /// <summary>
    /// Die Gegenprobe, und sie ist die wichtigere: <b>am Anfang</b> eines Statements bleibt es
    /// gesperrt. Ohne sie wäre ein Fix grün, der die Mehrdeutigkeit mit einem Block wieder
    /// einführt — und die kostet keine Diagnose, sondern eine falsche Deutung.
    /// </summary>
    [Fact]
    public void A_statement_still_does_not_start_with_a_struct_init()
    {
        // 'Small { n = 5 };' am Statement-Anfang wird als Block gelesen; der Parser meldet, was
        // er dort wirklich sieht. Entscheidend ist, dass er es NICHT als Struct-Init nimmt.
        var (ast, _) = Parse("""
            struct Small { n: int, }
            fn main(): int {
                Small { n = 5 };
                return 0;
            }
            """);

        var main = ast.Declarations.OfType<FunctionDecl>().Single(f => f.Name == "main");
        Assert.DoesNotContain(main.Body!.Statements,
            s => s is ExprStmt { Expr: StructInitExpr });
    }

    /// <summary>Und ein Block bleibt ein Block — die Form, wegen der die Sperre existiert.
    /// </summary>
    [Fact]
    public void A_bare_block_still_parses_as_a_block() =>
        ParsesCleanly("""
            fn main(): int {
                { let x = 1; }
                return 0;
            }
            """);

    /// <summary>
    /// Ein Vergleich bleibt ein Vergleich. Das <c>&lt;</c> wird nur dann als Typargument-Liste
    /// gedeutet, wenn es balanciert schließt und ein <c>{</c> folgt (§6.1) — sonst wäre
    /// <c>a = b &lt; c</c> nicht mehr schreibbar.
    /// </summary>
    [Fact]
    public void A_comparison_on_the_right_of_an_assignment_stays_a_comparison() =>
        ParsesCleanly("""
            fn main(): int {
                let a = 1;
                let b = 2;
                var c = false;
                c = a < b;
                return if (c) 1 else 0;
            }
            """);
}
