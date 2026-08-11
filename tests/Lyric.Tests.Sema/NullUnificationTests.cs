using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Ein <c>null</c>-Zweig macht den anderen optional: <c>if (c) 5 else null</c> ist <c>?int</c>.
///
/// <para><b>Der Anlass ist ein Befund aus M8b/S9.</b> Beim Bau von <c>std.option</c> war
/// <c>if (n &gt; 0) n * 10 else null</c> ein <c>LYR-SEM0016</c> — dieselbe Funktion mit zwei
/// <c>return</c>-Statements daneben ging. Die Widening-Regel <c>T</c> → <c>?T</c> (§4) griff nur
/// dort, wo ein Zieltyp bereits feststand (Zuweisung, Parameter, Rückgabe); bei einer
/// Arm-Unifikation entsteht der Ergebnistyp erst.</para>
///
/// <para><b>Warum <c>match</c> hier mitgeprüft wird</b>: gemeldet war nur der <c>if</c>-Fall. Beim
/// Nachmessen des Fixes zeigte sich, dass <c>match</c> eine <b>zweite</b> Unifikation hat, die den
/// Fall ebenfalls nicht kannte — dieselbe Frage an zwei Stellen mit zwei Antworten. Der Test hält
/// beide nebeneinander, damit die nächste Regel nicht wieder nur an einer landet.</para>
/// </summary>
public class NullUnificationTests
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

    private static void Compiles(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void Rejects(string source, string code) =>
        Assert.Contains(Check(source).Diagnostics, d => d.Code == code);

    // ------------------------------------------------------------------ if-Ausdruck

    [Fact]
    public void An_if_expression_with_a_null_branch_is_optional() =>
        Compiles("""
            fn f(n: int): ?int {
                return if (n > 0) n * 10 else null;
            }
            """);

    /// <summary>Die andere Reihenfolge. Ein Fix, der nur eine Seite kennt, bliebe mit nur einem
    /// der beiden Tests grün — und zwar zufällig, je nachdem welche Seite geprüft wird.</summary>
    [Fact]
    public void The_null_branch_may_come_first() =>
        Compiles("""
            fn f(n: int): ?int {
                return if (n > 0) null else n * 10;
            }
            """);

    /// <summary><c>?T</c> gegen <c>null</c> bleibt <c>?T</c> — Optionals verschachteln nicht, und
    /// ein <c>??T</c> gäbe es in Lyric ohnehin nicht.</summary>
    [Fact]
    public void An_already_optional_branch_does_not_nest() =>
        Compiles("""
            fn f(o: ?int, c: bool): ?int {
                return if (c) o else null;
            }
            """);

    /// <summary>
    /// Die Gegenprobe. Ohne sie bliebe der Test auch grün, wenn <c>Unify</c> ab jetzt <b>jede</b>
    /// Kombination durchwinkte.
    /// </summary>
    [Fact]
    public void Two_unrelated_branch_types_are_still_an_error() =>
        Rejects("""
            fn f(c: bool): int {
                return if (c) 1 else "eins";
            }
            """, "LYR-SEM0016");

    // ------------------------------------------------------------------ match-Ausdruck

    [Fact]
    public void A_match_arm_may_be_null() =>
        Compiles("""
            fn f(n: int): ?int {
                return match (n) {
                    0 => null,
                    _ => 99,
                };
            }
            """);

    [Fact]
    public void Two_unrelated_match_arms_are_still_an_error() =>
        Rejects("""
            fn f(n: int): int {
                return match (n) {
                    0 => "null",
                    _ => 99,
                };
            }
            """, "LYR-SEM0016");
}
