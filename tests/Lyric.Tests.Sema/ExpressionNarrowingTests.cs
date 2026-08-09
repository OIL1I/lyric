using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Flow-Narrowing gilt auch im <c>if</c>-<b>Ausdruck</b>, nicht nur im Statement.
///
/// <para><c>if (a == null) 0 else a</c> war ein Typfehler, während
/// <c>if (a == null) { return 0; } return a;</c> daneben funktionierte — derselbe Beweis über
/// denselben Wert, zwei verschiedene Antworten.</para>
///
/// <para>Die Maschinerie war vollständig da (<c>NarrowingFacts</c>, <c>Apply</c>); sie war an
/// dieser einen Stelle nicht angeschlossen. Aufgefallen beim Bau von <c>std.fmt</c>, wo
/// <c>digitToChar</c> genau diese Form nahelegt — die Umgehung dort ist zurückgebaut.</para>
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
        // Nach dem Ausdruck gilt wieder '?int'. Sonst wäre das Narrowing kein Beweis über einen
        // Zweig, sondern eine stillschweigende Umdeklaration — und ein 'a = null' danach
        // plötzlich ein Fehler.
        Assert.Contains(Check("""
            fn main(): int {
                let a: ?int = 5;
                let s = if (a != null) a else 0;
                return a;
            }
            """).Diagnostics, d => d.Code == "LYR-SEM0001");

    [Fact]
    public void A_nested_expression_narrows_too() =>
        // Der innere Ausdruck darf den äusseren nicht stören: 'b' wird im then-Zweig von 'a'
        // geprüft, und danach muss 'a' dort immer noch narrow sein.
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
        // Die Gegenprobe: was vorher ging, muss weiter gehen. Der Snapshot-Umgang im Ausdruck
        // fasst dieselbe Datenstruktur an wie das Statement.
        Allowed("""
            fn main(): int {
                let a: ?int = 5;
                if (a == null) { return 0; }
                return a;
            }
            """);
}
