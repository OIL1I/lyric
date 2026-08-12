using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Generische Enums: <c>enum Opt&lt;T&gt; { Some(T), None }</c> (Sprache.md §3.4, §12).
///
/// <para><b>Bis 2026-08-12 gab es sie im Lowering überhaupt nicht.</b> <c>TypeTable.InternEnum</c>
/// warf, sobald ein generisches Enum auch nur als <i>Parametertyp</i> vorkam — es musste keine
/// Variante konstruiert werden. Die Sema trug die Form da schon fast vollständig; es fehlte die
/// Verdrahtung.</para>
///
/// <para><b>Die tragende Zusicherung ist <see cref="Two_instantiations_do_not_share_an_entry"/>.</b>
/// <c>Opt&lt;int&gt;</c> und <c>Opt&lt;string&gt;</c> brauchen eigene Varianten-Layouts, weil die
/// VM zur Laufzeit keine Typen kennt (§12). Teilten sie sich einen Eintrag, läge ein <c>i64</c> in
/// einem String-Slot — dieselbe Klasse Loch wie die Konformanz-Lücke vom 2026-08-11, die in
/// Release still falsch rechnete und nur in Debug auffiel.</para>
/// </summary>
public class GenericEnumTests
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

        var report = new StringWriter();
        de.RenderText(report);
        Assert.False(de.HasErrors, "source did not compile: " + report);

        // verify: true — der IR-Verifier ist hier der eigentliche Zeuge. Ein geteiltes
        // Varianten-Layout faellt als Slot-Typkonflikt auf, lange bevor es einen falschen Wert
        // gibt.
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

    private const string Opt = "enum Opt<T> { Some(T), None }\n\n";

    // ------------------------------------------------------------------ Konstruktion

    [Fact]
    public void A_tuple_variant_is_constructed_and_matched() =>
        Assert.Equal(7, Run(Opt + """
            fn main(): int {
                let o = Opt<int>.Some(7);
                return match (o) { Some(v) => v, None => 0 };
            }
            """));

    [Fact]
    public void A_unit_variant_carries_its_instance() =>
        Assert.Equal(9, Run(Opt + """
            fn main(): int {
                let o = Opt<int>.None;
                return match (o) { Some(v) => v, None => 9 };
            }
            """));

    /// <summary>Eine Struct-Variante mit geschriebenen Argumenten. Der Parser musste dafür lernen,
    /// dass hinter <c>&lt;int&gt;</c> noch ein Segment stehen darf — die Argumente gehören dem
    /// Enum, die Variante hängt hinten dran.</summary>
    [Fact]
    public void A_struct_variant_takes_written_type_arguments() =>
        Assert.Equal(12, Run("""
            enum Ev<T> { Hit { at: T, n: int }, Miss }

            fn main(): int {
                let e = Ev<int>.Hit { at = 4, n = 3 };
                return match (e) { Hit { at, n } => at * n, Miss => 0 };
            }
            """));

    /// <summary>
    /// Und ohne geschriebene Argumente, wenn der Kontext sie hergibt — der Weg, den es vorher
    /// allein gab. Er darf durch den neuen nicht verdrängt worden sein.
    /// </summary>
    [Fact]
    public void The_instance_may_still_come_from_the_context() =>
        Assert.Equal(6, Run("""
            enum Ev<T> { Hit { at: T }, Miss }

            fn main(): int {
                let e: Ev<int> = Ev.Hit { at = 6 };
                return match (e) { Hit { at } => at, Miss => 0 };
            }
            """));

    /// <summary>
    /// Dasselbe für eine <b>Tuple</b>-Variante — und das ging bis heute nicht.
    ///
    /// <para>Die Struct-Form las den Kontext seit jeher, die Tuple-Form nicht: <c>Ev.Hit { … }</c>
    /// lief, <c>Opt.Some(7)</c> war ein Fehler. Eine Frage, zwei Antworten, je nach Form der
    /// Variante — genau das Muster, das dieses Projekt schon mehrfach auseinandergefahren ist.
    /// Jetzt geht beides über dieselbe Auflösung.</para>
    /// </summary>
    [Fact]
    public void A_tuple_variant_may_take_its_instance_from_the_context() =>
        Assert.Equal(7, Run(Opt + """
            fn main(): int {
                let o: Opt<int> = Opt.Some(7);
                return match (o) { Some(v) => v, None => 0 };
            }
            """));

    /// <summary>Und die Unit-Variante ebenso.</summary>
    [Fact]
    public void A_unit_variant_may_take_its_instance_from_the_context() =>
        Assert.Equal(3, Run(Opt + """
            fn main(): int {
                let o: Opt<int> = Opt.None;
                return match (o) { Some(v) => v, None => 3 };
            }
            """));

    /// <summary>
    /// <b>Wo der Kontext nicht hinreicht</b>: in eine Argumentposition. Der erwartete Typ wird
    /// dorthin nicht durchgereicht, also muss die Instanz dastehen.
    ///
    /// <para>Das ist keine Folge dieser Arbeit — es war vorher genauso — aber es ist der Rand, an
    /// den man beim Schreiben zuerst stößt. Der Test hält ihn fest, damit er beim nächsten Mal
    /// gemessen und nicht geraten wird.</para>
    /// </summary>
    [Fact]
    public void The_context_does_not_reach_into_an_argument_position() =>
        Assert.Contains(Check("""
            enum Ev<T> { Hit { at: T }, Miss }
            fn nimm(e: Ev<int>): int { return match (e) { Hit { at } => at, Miss => 0 }; }
            fn main(): int { return nimm(Ev.Hit { at = 6 }); }
            """), d => d.Code == "LYR-SEM0026");

    /// <summary>Dasselbe für die Tuple-Form, wo die Meldung <c>LYR-SEM0063</c> heißt und den
    /// direkten Ausweg nennt.</summary>
    [Fact]
    public void A_tuple_variant_without_arguments_says_to_write_them() =>
        Assert.Contains(Check(Opt + """
            fn nimm(o: Opt<int>): int { return match (o) { Some(v) => v, None => 0 }; }
            fn main(): int { return nimm(Opt.Some(5)); }
            """), d => d.Code == "LYR-SEM0063");

    // ------------------------------------------------------------------ die tragende Zusicherung

    /// <summary>
    /// <b>Zwei Instanziierungen, zwei Einträge.</b> Der <c>int</c>-Wert und der
    /// <c>string</c>-Wert stehen im selben Programm und müssen beide richtig herauskommen.
    ///
    /// <para>Teilten sie sich ein Varianten-Layout, läge ein <c>i64</c> in einem String-Slot. In
    /// Debug meldet der Verifier das; in Release — also dem, was ausgeliefert wird — liefe es
    /// durch und gäbe eine stille falsche Antwort. Genau diese Asymmetrie hat die
    /// Konformanz-Lücke vom 2026-08-11 so teuer gemacht.</para>
    /// </summary>
    [Fact]
    public void Two_instantiations_do_not_share_an_entry() =>
        // 7 aus dem int-Zweig, 100 nur wenn der string-Zweig wirklich einen String hält. Beides
        // in EINER Zahl, damit keine Hälfte unbemerkt danebenliegen kann.
        Assert.Equal(107, Run(Opt + """
            fn main(): int {
                let a = Opt<int>.Some(7);
                let b = Opt<string>.Some("hallo");

                let zahl = match (a) { Some(v) => v, None => 0 };
                let wort = match (b) { Some(s) => if (s == "hallo") 100 else -1, None => -1 };
                return zahl + wort;
            }
            """));

    /// <summary>Dieselbe Frage eine Ebene tiefer: <c>Opt&lt;Opt&lt;int&gt;&gt;</c>. Die innere
    /// Instanz wird beim Internieren der äußeren gebraucht.</summary>
    [Fact]
    public void An_instance_may_be_nested() =>
        Assert.Equal(3, Run(Opt + """
            fn main(): int {
                let o = Opt<Opt<int>>.Some(Opt<int>.Some(3));
                return match (o) {
                    Some(inner) => match (inner) { Some(v) => v, None => 0 },
                    None => 0,
                };
            }
            """));

    // ------------------------------------------------------------------ Rekursion und Generics

    /// <summary>
    /// Ein Enum, das sich über eine Variante selbst nennt. <b>Das ist die Endlosschleifen-Probe</b>:
    /// die Id muss in der Instanz-Registry stehen, <i>bevor</i> die Varianten interniert werden,
    /// sonst fordert <c>Node(Tree&lt;T&gt;, …)</c> genau die Instanz an, die gerade entsteht.
    /// </summary>
    [Fact]
    public void A_recursive_generic_enum_terminates() =>
        Assert.Equal(5, Run("""
            enum Tree<T> { Leaf, Node(T, Tree<T>, Tree<T>) }

            fn main(): int {
                let t = Tree<int>.Node(5, Tree<int>.Leaf, Tree<int>.Leaf);
                return match (t) { Node(v, l, r) => v, Leaf => 0 };
            }
            """));

    /// <summary>Ein generisches Enum in einer generischen Funktion — verschachtelte Substitution:
    /// das <c>T</c> des Enums ist das <c>T</c> der Funktion.</summary>
    [Fact]
    public void A_generic_function_over_a_generic_enum_works() =>
        Assert.Equal(7, Run(Opt + """
            fn hole<T>(o: Opt<T>, d: T): T { return match (o) { Some(v) => v, None => d }; }

            fn main(): int { return hole(Opt<int>.Some(4), 0) + hole(Opt<int>.None, 3); }
            """));

    /// <summary>Als Feld eines generischen Typs — die Instanz entsteht beim Internieren des
    /// Feld-Layouts.</summary>
    [Fact]
    public void It_works_as_a_field_of_a_generic_type() =>
        Assert.Equal(8, Run(Opt + """
            class Box<T> { v: Opt<T>, }

            fn main(): int {
                let b = Box<int> { v = Opt<int>.Some(8) };
                return match (b.v) { Some(x) => x, None => 0 };
            }
            """));

    /// <summary>Ein Guard über einer Payload-Bindung.</summary>
    [Fact]
    public void A_guard_over_a_payload_binding_works() =>
        Assert.Equal(2, Run(Opt + """
            fn main(): int {
                let o = Opt<int>.Some(1);
                return match (o) { Some(v) if v > 3 => 1, Some(v) => 2, None => 9 };
            }
            """));

    // ------------------------------------------------------------------ Gegenproben

    /// <summary>Ein nicht-generisches Enum geht unverändert seinen alten Weg. Ohne diesen Test
    /// bliebe unbemerkt, wenn die Umstellung auf Instanz-Einträge den Normalfall mitnimmt — und
    /// das ist die Mehrzahl allen Codes.</summary>
    [Fact]
    public void A_plain_enum_still_works() =>
        Assert.Equal(6, Run("""
            enum Shape { Circle(int), Tri { a: int, b: int }, Empty }

            fn main(): int {
                let c = Shape.Circle(6);
                let t = Shape.Tri { a = 1, b = 2 };
                let e = Shape.Empty;

                let x = match (c) { Circle(r) => r, Tri { a, b } => a, Empty => 0 };
                let y = match (t) { Circle(r) => 0, Tri { a, b } => a + b, Empty => 0 };
                let z = match (e) { Circle(r) => 9, Tri { a, b } => 9, Empty => 0 };
                return x + z * y;
            }
            """));

    [Fact]
    public void The_wrong_number_of_type_arguments_is_reported() =>
        Assert.Contains(Check(Opt + """
            fn main(): int { let o = Opt<int, string>.Some(1); return 0; }
            """), d => d.Code == "LYR-SEM0026");

    /// <summary>Ein falscher Payload-Typ wird an der Konstruktion bemerkt, nicht erst im
    /// <c>match</c>.</summary>
    [Fact]
    public void A_wrong_payload_type_is_rejected() =>
        Assert.Contains(Check(Opt + """
            fn main(): int { let o = Opt<int>.Some("nein"); return 0; }
            """), d => d.Code == "LYR-SEM0001");

    /// <summary>
    /// Ohne Argumente und ohne Kontext bleibt es ein Fehler — und die Meldung nennt jetzt
    /// <b>beide</b> Auswege. Vorher stand dort nur „from context", obwohl das Schreiben der
    /// Argumente der direktere ist.
    /// </summary>
    [Fact]
    public void Without_arguments_and_without_context_it_says_both_ways()
    {
        var reported = Assert.Single(Check("""
            enum Ev<T> { Hit { at: T }, Miss }
            fn main(): int { let e = Ev.Hit { at = 1 }; return 0; }
            """), d => d.Code == "LYR-SEM0026");

        Assert.Contains("write them", reported.Message, StringComparison.Ordinal);
    }
}
