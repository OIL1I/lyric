using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Wo <c>?.</c> mit einem Aufruf <b>aufhört</b> (Sprache.md §7).
///
/// <para><b>Ein Glied kann eine Methode sein oder einen Funktions-Wert halten</b>, und nur im
/// ersten Fall gibt es eine Frage. Bei <c>f: ?fn() -&gt; int</c> sind es zwei — ob der Empfänger
/// da ist und ob das Feld belegt ist — und ein einzelnes <c>?</c>. Wer hier auspackt, beantwortet
/// die zweite stillschweigend mit ja, und das ist ein Aufruf auf null.</para>
///
/// <para>Die Sprache hat kein <c>?()</c>. Also gibt es die Form nicht, und die Meldung sagt das
/// samt Ausweg — statt der alten Auskunft über einen Zwischentyp, den niemand hingeschrieben
/// hat.</para>
/// </summary>
public class OptionalCallLimitTests
{
    private static IReadOnlyList<Diagnostic> Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de.Diagnostics;
    }

    /// <summary>Der gefährliche Fall: ohne die Meldung würde der Aufruf auf einem <c>null</c>
    /// stattfinden.</summary>
    [Fact]
    public void A_chain_onto_a_nullable_function_field_is_rejected()
    {
        var reported = Assert.Single(Check("""
            class Box { f: ?fn() -> int, }
            fn main(): int {
                let b: ?Box = Box { f = null };
                return b?.f() ?? -1;
            }
            """));

        Assert.Equal("LYR-SEM0062", reported.Code);
        Assert.Contains("holds a function value", reported.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Und der harmlose daneben. Er ist <b>ebenfalls</b> abgelehnt, weil die Unterscheidung an der
    /// Bindung hängt und nicht an der Nullbarkeit — ein indirekter Aufruf über ein
    /// <c>?.</c>-Glied ist keine Form der Sprache. Die Meldung nennt den Ausweg, und der ist eine
    /// Zeile.
    /// </summary>
    [Fact]
    public void A_chain_onto_a_plain_function_field_is_rejected_too() =>
        Assert.Contains(Check("""
            fn eins(): int { return 1; }
            class Box { f: fn() -> int, }
            fn main(): int {
                let b: ?Box = Box { f = eins };
                return b?.f() ?? -1;
            }
            """), d => d.Code == "LYR-SEM0062");

    /// <summary>Der Ausweg aus der Meldung muss tatsächlich funktionieren — sonst ist sie ein
    /// Hinweis ins Leere.</summary>
    [Fact]
    public void Reading_the_field_first_works()
    {
        var diagnostics = Check("""
            fn eins(): int { return 1; }
            class Box { f: fn() -> int, }
            fn main(): int {
                let b: ?Box = Box { f = eins };
                let g = b?.f;
                return if (g != null) g() else -1;
            }
            """);

        Assert.Empty(diagnostics);
    }

    /// <summary>Die Gegenprobe: eine METHODE bleibt aufrufbar. Ohne sie wäre eine zu strenge
    /// Bedingung grün.</summary>
    [Fact]
    public void A_method_is_still_callable_through_a_chain() =>
        Assert.Empty(Check("""
            class Box { fn get(): int { return 1; } }
            fn main(): int {
                let b: ?Box = Box { };
                return b?.get() ?? -1;
            }
            """));
}
