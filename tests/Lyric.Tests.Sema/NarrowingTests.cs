using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Flow-Narrowing (Sprache.md §7): nach einer bewiesenen Nicht-Null-Prüfung ist ein <c>?T</c> ein
/// <c>T</c>.
///
/// <para>Jeder Fall steht hier <b>doppelt</b> — einmal erlaubt, einmal abgelehnt. Eine Regel, die
/// nur die erlaubten Fälle prüft, bliebe auch grün, wenn sie ausnahmslos alles einengte; und
/// genau das wäre der gefährliche Fehler, weil das Lowering hinter jedem Narrowing ein
/// <c>optget</c> ohne Prüfung erzeugt.</para>
/// </summary>
public class NarrowingTests
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

    private static void Narrows(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void DoesNotNarrow(string source) =>
        Assert.Contains(Check(source).Diagnostics, d => d.Code is "LYR-SEM0001" or "LYR-SEM0003");

    // ------------------------------------------------------------------ was eingeengt wird

    [Fact]
    public void An_if_narrows_its_then_branch() =>
        Narrows("fn main(): int { let x: ?int = 1; if (x != null) { return x; } return 0; }");

    [Fact]
    public void An_early_exit_narrows_what_follows() =>
        // 'if (x == null) { return; }' beweist für den Rest der Funktion, dass x einen Wert hat.
        Narrows("fn main(): int { let x: ?int = 1; if (x == null) { return 0; } return x; }");

    [Fact]
    public void A_while_narrows_its_body() =>
        // Sicher, obwohl die Schleife x ändern kann: die Bedingung wird vor JEDEM Durchlauf neu
        // geprüft, und eine Zuweisung im Rumpf hebt das Narrowing ab dort auf.
        Narrows("""
            fn main(): int {
                var x: ?int = 1;
                var s = 0;
                while (x != null) { s += x; x = null; }
                return s;
            }
            """);

    [Fact]
    public void The_right_side_of_and_sees_what_the_left_proved() =>
        // Kurzschluss: die rechte Seite läuft nur, wenn die linke wahr ist.
        Narrows("fn main(): int { let x: ?int = 1; if (x != null && x > 0) { return x; } return 0; }");

    [Fact]
    public void And_collects_facts_from_both_sides() =>
        Narrows("""
            fn main(): int {
                let a: ?int = 1;
                let b: ?int = 2;
                if (a != null && b != null) { return a + b; }
                return 0;
            }
            """);

    [Fact]
    public void Or_narrows_the_else_branch() =>
        // 'if (x == null || …) { return; }' — der else-Zweig wird nur erreicht, wenn BEIDE Seiten
        // falsch waren, also auch die Null-Prüfung.
        Narrows("fn main(): int { let x: ?int = 1; if (x == null || x < 0) { return 0; } return x; }");

    // ------------------------------------------------------------------ was NICHT eingeengt wird

    [Fact]
    public void An_assignment_undoes_the_narrowing() =>
        DoesNotNarrow("fn main(): int { var x: ?int = 1; if (x != null) { x = null; return x; } return 0; }");

    [Fact]
    public void An_assignment_undoes_it_inside_a_loop_too() =>
        // Die Hälfte, die 'while' überhaupt erst sicher macht — ohne sie wäre der Rumpf eine Lücke.
        DoesNotNarrow("""
            fn main(): int {
                var x: ?int = 1;
                var s = 0;
                while (x != null) { x = null; s += x; }
                return s;
            }
            """);

    [Fact]
    public void A_do_while_does_not_narrow_its_body() =>
        // Dort läuft der Rumpf VOR der ersten Prüfung — die Bedingung sagt am Rumpfanfang nichts.
        DoesNotNarrow("""
            fn main(): int {
                var x: ?int = 1;
                var s = 0;
                do { s += x; } while (x != null);
                return s;
            }
            """);

    [Fact]
    public void Or_does_not_narrow_the_then_branch() =>
        // Der then-Zweig wird erreicht, sobald EINE Seite wahr ist — welche, weiß niemand.
        DoesNotNarrow("""
            fn main(): int {
                let a: ?int = 1;
                let b: ?int = 2;
                if (a != null || b != null) { return a; }
                return 0;
            }
            """);

    [Fact]
    public void And_does_not_narrow_the_else_branch() =>
        // Spiegelbild: der else-Zweig wird erreicht, sobald eine Seite falsch ist.
        DoesNotNarrow("""
            fn main(): int {
                let a: ?int = 1;
                if (a != null && a > 0) { return 0; }
                return a;
            }
            """);

    [Fact]
    public void The_narrowing_ends_with_the_branch() =>
        DoesNotNarrow("fn main(): int { let x: ?int = 1; if (x != null) { } return x; }");
}
