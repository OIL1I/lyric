using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Konformanz gegen ein <b>generisches</b> Interface: <c>Src&lt;int&gt;</c> und
/// <c>Src&lt;string&gt;</c> sind dasselbe Symbol und verschiedene Anforderungen.
///
/// <para><b>Der Anlass ist der ernsteste Befund dieser Meilenstein-Arbeit.</b> Bis 2026-08-11
/// verglich die Konformanz nur das Interface-<i>Symbol</i>. Damit erfüllte
/// <c>class Ones :: [Src&lt;int&gt;]</c> ein <c>&lt;T :: [Src&lt;string&gt;]&gt;</c>, und der
/// Rumpf legte einen <c>i64</c> in einen <c>string</c>-Slot. Gemessen:</para>
///
/// <list type="bullet">
///   <item><description><b>Debug</b>: Verifier-Absturz
///     (<c>store of t1 (i64) into l1 (string)</c>).</description></item>
///   <item><description><b>Release</b> — also das, was ausgeliefert wird — <b>lief durch</b> und
///     lieferte eine stille falsche Antwort. Der Bytecode-Loader fing es ebenfalls
///     nicht.</description></item>
/// </list>
///
/// <para>Das ist kein fehlendes Feature, sondern ein Typprüfer, der ein Programm annimmt, dessen
/// Typen nicht halten. Dass .NET den Schaden eindämmt (ein leerer String statt eines
/// Speicherfehlers), ist Glück der Wertdarstellung und keine Zusage der Sprache.</para>
///
/// <para><b>Beide Richtungen stehen hier</b>: die falsche Instanziierung muss scheitern, und die
/// richtige muss weiter durchgehen. Ein Fix, der nur die erste Hälfte prüft, könnte
/// <c>Map&lt;K, V&gt;</c> und <c>Iterator&lt;T&gt;</c> unbenutzbar machen, ohne dass es
/// auffiele.</para>
/// </summary>
public class GenericConformanceTests
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

    private static void Rejects(string source)
    {
        var de = Check(source);
        Assert.Contains(de.Diagnostics, d => d.Code is "LYR-SEM0028" or "LYR-SEM0001");
    }

    private const string Interface = """
        interface Src<T> { fn hole(): T; }

        class Ones :: [Src<int>] { fn hole(): int { return 1; } }
        class Worte :: [Src<string>] { fn hole(): string { return "a"; } }

        """;

    // ------------------------------------------------------------------ Constraints

    /// <summary>Der gemeldete Fall.</summary>
    [Fact]
    public void A_constraint_rejects_the_wrong_type_argument() =>
        Rejects(Interface + """
            fn nimm<T :: [Src<string>]>(s: T): string { return s.hole(); }
            fn main(): int { let x = nimm(Ones { }); return 0; }
            """);

    /// <summary>Die Gegenprobe — ohne sie wäre ein Fix grün, der <b>alles</b> ablehnt.</summary>
    [Fact]
    public void A_constraint_accepts_the_right_type_argument() =>
        Compiles(Interface + """
            fn nimm<T :: [Src<string>]>(s: T): string { return s.hole(); }
            fn main(): int { let x = nimm(Worte { }); return 0; }
            """);

    /// <summary>
    /// Ein Constraint, der den eigenen Typ-Parameter nennt (ADR-024, der M4-Rest). Er ist der
    /// Grund, warum die volle Substitutionsabbildung durchgereicht wird und nicht ein Parameter
    /// nach dem anderen.
    /// </summary>
    [Fact]
    public void A_constraint_over_its_own_parameter_still_works() =>
        Compiles("""
            interface Eq<T> { fn gleich(other: T): bool; }
            class P :: [Eq<P>] { fn gleich(other: P): bool { return true; } }

            fn beide<T :: [Eq<T>]>(a: T, b: T): bool { return a.gleich(b); }
            fn main(): int { let x = beide(P { }, P { }); return 0; }
            """);

    /// <summary>Eine generische Klasse erfüllt das Interface mit <b>ihrem</b> Typargument:
    /// <c>Box&lt;int&gt;</c> ist ein <c>Src&lt;int&gt;</c> und kein <c>Src&lt;string&gt;</c>.
    /// </summary>
    [Fact]
    public void A_generic_class_conforms_with_its_own_type_argument() =>
        Compiles("""
            interface Src<T> { fn hole(): T; }
            class Box<T> :: [Src<T>] { v: T, fn hole(): T { return this.v; } }

            fn nimm<T :: [Src<int>]>(s: T): int { return s.hole(); }
            fn main(): int { return nimm(Box<int> { v = 1 }); }
            """);

    [Fact]
    public void A_generic_class_with_the_wrong_argument_is_rejected() =>
        Rejects("""
            interface Src<T> { fn hole(): T; }
            class Box<T> :: [Src<T>] { v: T, fn hole(): T { return this.v; } }

            fn nimm<T :: [Src<int>]>(s: T): int { return s.hole(); }
            fn main(): int { return nimm(Box<string> { v = "a" }); }
            """);

    // ------------------------------------------------------------------ Zuweisung

    /// <summary>
    /// Dieselbe Frage an der zweiten Stelle: die Zuweisung an einen Interface-Typ. Sie ging über
    /// denselben Vergleich und hatte deshalb dieselbe Lücke — <b>eine Frage, zwei Stellen</b>.
    /// </summary>
    [Fact]
    public void An_assignment_to_a_generic_interface_checks_the_type_argument() =>
        Rejects(Interface + """
            fn main(): int {
                let s: Src<string> = Ones { };
                return 0;
            }
            """);

    [Fact]
    public void An_assignment_with_the_right_argument_still_works() =>
        Compiles(Interface + """
            fn main(): int {
                let s: Src<string> = Worte { };
                return 0;
            }
            """);

    // ------------------------------------------------------------------ nicht-generisch

    /// <summary>
    /// Ein Interface <b>ohne</b> Typargumente vergleicht weiter über das Symbol — dort gibt es
    /// nichts zu unterscheiden. Ohne diesen Test bliebe unbemerkt, wenn der strengere Vergleich
    /// den gewöhnlichen Fall mitnimmt, und das ist die Mehrzahl allen Codes.
    /// </summary>
    [Fact]
    public void A_plain_interface_still_conforms() =>
        Compiles("""
            interface Zeigbar { fn zeig(): string; }
            class A :: [Zeigbar] { fn zeig(): string { return "a"; } }

            fn nimm<T :: [Zeigbar]>(x: T): string { return x.zeig(); }
            fn main(): int { let s = nimm(A { }); return 0; }
            """);

    /// <summary>Und über einen <c>extend</c>-Block (§3.6) — der zweite Weg zur Konformanz, der
    /// seit P9b durch dieselbe Funktion läuft.</summary>
    [Fact]
    public void Conformance_through_an_extend_block_still_works() =>
        Compiles("""
            interface Src<T> { fn hole(): T; }
            class Leer { }
            extend Leer :: [Src<int>] { fn hole(): int { return 0; } }

            fn nimm<T :: [Src<int>]>(s: T): int { return s.hole(); }
            fn main(): int { return nimm(Leer { }); }
            """);

    [Fact]
    public void An_extend_block_with_the_wrong_argument_is_rejected() =>
        Rejects("""
            interface Src<T> { fn hole(): T; }
            class Leer { }
            extend Leer :: [Src<int>] { fn hole(): int { return 0; } }

            fn nimm<T :: [Src<string>]>(s: T): string { return s.hole(); }
            fn main(): int { let x = nimm(Leer { }); return 0; }
            """);
}
