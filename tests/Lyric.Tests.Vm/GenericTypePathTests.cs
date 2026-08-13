using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Ein Typpfad mit Argumenten in Wert-Position: <c>Pair&lt;int&gt;.of(3)</c> (Sprache.md §6.2).
///
/// <para><b>Bis 2026-08-12 gab es die Form nicht.</b> Der Parser las <c>Pair</c> als Bezeichner
/// und <c>&lt;</c> als Vergleich, und danach stolperte er über den Punkt. Eine statische Fabrik
/// auf einem generischen Typ war damit unerreichbar — <c>std.collections</c> trägt den Beleg als
/// Kommentar: <c>emptyList</c> ist eine freie Funktion, „weil eine statische Methode auf einer
/// generischen Instanz nicht ausdrückbar ist".</para>
///
/// <para><b>Die Erkennung kostet keine Mehrdeutigkeit.</b> Das <c>&lt;</c> gilt als
/// Typargument-Liste, wenn es balanciert schließt und ein <c>.</c> folgt — und ein Punkt hinter
/// einer Vergleichskette (<c>a &lt; b &gt; .c</c>) ist ohnehin kein gültiger Ausdruck. Dieselbe
/// Regel zieht §6.1 seit 2026-08-07 für <c>f&lt;int&gt;()</c>.</para>
/// </summary>
public class GenericTypePathTests
{
    private static long Run(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var diagnostics = new StringWriter();
        de.RenderText(diagnostics);
        Assert.False(de.HasErrors, "source did not compile: " + diagnostics);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        var lowering = new StringWriter();
        de.RenderText(lowering);
        Assert.True(ir is not null, "lowering failed: " + lowering);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

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

    private const string Pair = """
        struct Pair<T> {
            a: T,
            static fn of(x: T): Pair<T> { return Pair<T> { a = x }; }
            fn first(): T { return this.a; }
        }

        """;

    // ------------------------------------------------------------------ was jetzt geht

    [Fact]
    public void A_static_factory_on_a_generic_type_is_callable() =>
        Assert.Equal(3, Run(Pair + """
            fn main(): int { return Pair<int>.of(3).first(); }
            """));

    /// <summary>Der Rückgabetyp der Fabrik ist <c>Pair&lt;T&gt;</c> und muss als
    /// <c>Pair&lt;int&gt;</c> ankommen — ohne Substitution stünde dort ein <c>T</c>.</summary>
    [Fact]
    public void The_result_carries_the_type_argument() =>
        Assert.Equal(7, Run(Pair + """
            fn main(): int {
                let p: Pair<int> = Pair<int>.of(7);
                return p.a;
            }
            """));

    /// <summary>Zwei Instanziierungen sind zwei Funktionen. Ohne das teilte sich <c>int</c> und
    /// <c>bool</c> einen Rumpf, und die Monomorphisierung wäre keine.</summary>
    [Fact]
    public void Two_instantiations_do_not_share_a_body() =>
        Assert.Equal(1, Run("""
            struct Box<T> {
                v: T,
                static fn of(x: T): Box<T> { return Box<T> { v = x }; }
            }

            fn main(): int {
                let a = Box<int>.of(1);
                let b = Box<bool>.of(true);
                return if (b.v) a.v else 0;
            }
            """));

    /// <summary>Zwei Typparameter — die Argumente müssen der Reihe nach zugeordnet werden.
    /// </summary>
    [Fact]
    public void Two_type_parameters_keep_their_order() =>
        Assert.Equal(5, Run("""
            struct Two<A, B> {
                a: A, b: B,
                static fn of(x: A, y: B): Two<A, B> { return Two<A, B> { a = x, b = y }; }
            }

            fn main(): int {
                let t = Two<int, bool>.of(5, true);
                return if (t.b) t.a else 0;
            }
            """));

    /// <summary>Ein verschachteltes Typargument: <c>Pair&lt;Pair&lt;int&gt;&gt;</c> schließt mit
    /// <c>&gt;&gt;</c>, und der Lookahead muss das als zwei Ebenen lesen.</summary>
    [Fact]
    public void A_nested_type_argument_closes_with_a_shift_token() =>
        Assert.Equal(9, Run(Pair + """
            fn main(): int {
                let inner = Pair<int>.of(9);
                let outer = Pair<Pair<int>>.of(inner);
                return outer.a.a;
            }
            """));

    /// <summary>Eine Klasse statt eines Structs — derselbe Weg, anderer Speicher.</summary>
    [Fact]
    public void It_works_on_a_class_too() =>
        Assert.Equal(4, Run("""
            class Holder<T> {
                v: T,
                static fn of(x: T): Holder<T> { return Holder<T> { v = x }; }
            }

            fn main(): int { return Holder<int>.of(4).v; }
            """));

    // ------------------------------------------------------------------ counter-checks

    /// <summary>
    /// <b>Die wichtigste Zusicherung.</b> Ein <c>&lt;</c> bleibt ein Vergleich, wenn kein
    /// balanciertes <c>&gt;</c> mit folgendem <c>.</c> dahinter steht. Ohne diesen Test wäre eine
    /// zu gierige Erkennung grün — und sie kostete keine Diagnose, sondern eine falsche Deutung.
    /// </summary>
    [Fact]
    public void A_comparison_stays_a_comparison() =>
        Assert.Equal(1, Run("""
            fn main(): int {
                let a = 1;
                let b = 2;
                return if (a < b) 1 else 0;
            }
            """));

    /// <summary>Und die bösere Form: ein Vergleich, hinter dem tatsächlich ein Punkt steht.
    /// <c>(a &lt; b) == c.d</c> darf nicht als Typpfad gelesen werden.</summary>
    [Fact]
    public void A_comparison_followed_by_a_member_access_stays_a_comparison() =>
        Assert.Equal(1, Run("""
            struct Flag { v: bool, }

            fn main(): int {
                let a = 1;
                let b = 2;
                let f = Flag { v = true };
                return if ((a < b) == f.v) 1 else 0;
            }
            """));

    /// <summary>Der nicht-generische Fall geht weiter seinen alten Weg — er braucht den neuen
    /// Knoten nicht.</summary>
    [Fact]
    public void A_static_method_on_a_plain_type_still_works() =>
        Assert.Equal(2, Run("""
            struct P { n: int, static fn neu(): P { return P { n = 2 }; } }
            fn main(): int { return P.neu().n; }
            """));

    /// <summary>Und der generische Struct-Init, der seit P8 geht.</summary>
    [Fact]
    public void A_generic_struct_init_still_works() =>
        Assert.Equal(6, Run(Pair + """
            fn main(): int { return Pair<int> { a = 6 }.a; }
            """));

    // ------------------------------------------------------------------ Diagnosen

    /// <summary>
    /// Ohne Argumente sagt es das jetzt. Vorher meldete es „cannot assign 'int' to 'T'" — eine
    /// Auskunft über die Folge, nicht über die Ursache, und sie zeigte auf das Argument statt auf
    /// den fehlenden Typ.
    /// </summary>
    [Fact]
    public void A_generic_type_without_arguments_says_so()
    {
        var reported = Assert.Single(Check(Pair + """
            fn main(): int { return Pair.of(3).a; }
            """), d => d.Code == "LYR-SEM0063");

        Assert.Contains("Pair<T>.of", reported.Message, StringComparison.Ordinal);
    }

    /// <summary>Die falsche Anzahl Argumente ist eine eigene Meldung.</summary>
    [Fact]
    public void The_wrong_number_of_type_arguments_is_reported() =>
        Assert.Contains(Check(Pair + """
            fn main(): int { return Pair<int, bool>.of(3).a; }
            """), d => d.Code == "LYR-SEM0026");

    /// <summary>Ein falsches Typargument wird am Aufruf bemerkt, nicht erst im Rumpf.</summary>
    [Fact]
    public void A_wrong_argument_type_is_rejected() =>
        Assert.Contains(Check(Pair + """
            fn main(): int { return Pair<int>.of(true).a; }
            """), d => d.Code == "LYR-SEM0001");

    /// <summary>Eine Instanzmethode über den Typpfad bleibt abgelehnt (ADR-014) — sie braucht
    /// einen Empfänger, und daran ändern Typargumente nichts.</summary>
    [Fact]
    public void An_instance_method_through_a_type_path_is_still_rejected() =>
        Assert.Contains(Check(Pair + """
            fn main(): int { return Pair<int>.first(); }
            """), d => d.Code == "LYR-SEM0055");

    /// <summary>Ein Typpfad allein ist kein Wert — dieselbe Meldung wie für jeden Typnamen.
    /// </summary>
    [Fact]
    public void A_type_path_alone_is_not_a_value() =>
        Assert.Contains(Check(Pair + """
            fn main(): int { let x = Pair<int>; return 0; }
            """), d => d.Code is "LYR-SEM0052" or "LYR-PAR0002");
}
