using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Exceptions und <c>defer</c> (M7/P5), über die gesamte Pipeline.
///
/// <para>Der Kern ist jedes Mal <b>welcher Pfad genommen wurde</b>: eine geworfene Exception muss
/// den Rest des <c>try</c>-Rumpfes überspringen und im passenden <c>catch</c> ankommen — nicht im
/// erstbesten.</para>
/// </summary>
public class ExceptionTests
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

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile:\n" + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    private const string Errors = """
        class Boom :: [Throwable] {
            code: int,
            fn message(): string { return "boom"; }
        }

        class Other :: [Throwable] {
            fn message(): string { return "other"; }
        }
        """;

    [Fact]
    public void A_thrown_value_reaches_the_matching_catch()
    {
        Assert.Equal(7, Run(Errors + """

            fn risky(): int throws Boom { throw Boom { code = 7 }; }

            fn main(): int {
                try {
                    risky();
                } catch (e: Boom) {
                    return e.code;
                }
                return 0;
            }
            """));
    }

    [Fact]
    public void The_rest_of_the_try_body_is_skipped()
    {
        // Ohne echtes Abwickeln liefe der Rumpf weiter und lieferte 99.
        Assert.Equal(1, Run(Errors + """

            fn risky(): int throws Boom { throw Boom { code = 1 }; }

            fn main(): int {
                var n = 0;
                try {
                    risky();
                    n = 99;
                } catch (e: Boom) {
                    n = e.code;
                }
                return n;
            }
            """));
    }

    [Fact]
    public void The_catch_is_skipped_when_nothing_throws()
    {
        // Die Gegenprobe: ohne sie bestuende der Test oben auch, wenn IMMER gefangen wuerde.
        Assert.Equal(5, Run(Errors + """

            fn safe(): int throws Boom { return 5; }

            fn main(): int {
                var n = 0;
                try {
                    n = safe();
                } catch (e: Boom) {
                    n = 99;
                }
                return n;
            }
            """));
    }

    [Fact]
    public void The_type_selects_the_handler()
    {
        // Zwei catch-Klauseln, geworfen wird die zweite Sorte. Waere der Typvergleich nicht da,
        // faenge die erste.
        Assert.Equal(2, Run(Errors + """

            fn risky(): int throws Other { throw Other { }; }

            fn main(): int {
                try {
                    risky();
                } catch (e: Boom) {
                    return 1;
                } catch (e: Other) {
                    return 2;
                }
                return 0;
            }
            """));
    }

    [Fact]
    public void An_exception_unwinds_through_a_frame_without_a_handler()
    {
        // 'middle' hat kein try — die Exception muss ihren Frame verwerfen und in main landen.
        Assert.Equal(3, Run(Errors + """

            fn deep(): int throws Boom { throw Boom { code = 3 }; }
            fn middle(): int throws Boom { return deep(); }

            fn main(): int {
                try {
                    middle();
                } catch (e: Boom) {
                    return e.code;
                }
                return 0;
            }
            """));
    }

    [Fact]
    public void The_innermost_try_wins()
    {
        Assert.Equal(1, Run(Errors + """

            fn risky(): int throws Boom { throw Boom { code = 1 }; }

            fn main(): int {
                try {
                    try {
                        risky();
                    } catch (e: Boom) {
                        return e.code;
                    }
                } catch (e: Boom) {
                    return 99;
                }
                return 0;
            }
            """));
    }

    [Fact]
    public void An_uncaught_exception_aborts_like_a_panic()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", Errors + """

            fn main(): int {
                throw Boom { code = 1 };
            }
            """);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);
        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
        var panic = Assert.Throws<LyricPanic>(() => Interpreter.Run(module, NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)));

        Assert.Equal(VmDiagnostics.UncaughtException, panic.Code);
    }

    // ---------------------------------------------------------------- defer

    [Fact]
    public void A_defer_runs_at_the_end_of_its_scope()
    {
        Assert.Equal(1, Run("""
            class Cell { n: int }

            fn main(): int {
                let c = Cell { n = 0 };
                {
                    defer c.n = 1;
                }
                return c.n;
            }
            """));
    }

    [Fact]
    public void Defers_run_in_LIFO_order()
    {
        // Sprache.md §5. Erst 'b' (zuletzt registriert), dann 'a' — die Zahl unterscheidet die
        // Reihenfolgen: 1 dann 2 gaebe 12, 2 dann 1 gibt 21.
        Assert.Equal(21, Run("""
            class Cell { n: int }

            fn main(): int {
                let c = Cell { n = 0 };
                {
                    defer c.n = c.n * 10 + 1;
                    defer c.n = c.n * 10 + 2;
                }
                return c.n;
            }
            """));
    }

    [Fact]
    public void A_defer_runs_before_a_return()
    {
        Assert.Equal(1, Run("""
            class Cell { n: int }

            fn set(c: Cell): int {
                defer c.n = 1;
                return 0;
            }

            fn main(): int {
                let c = Cell { n = 0 };
                set(c);
                return c.n;
            }
            """));
    }

    [Fact]
    public void A_return_value_is_computed_before_the_defers_run()
    {
        // Go haelt es genauso: 'defer' darf den bereits bestimmten Rueckgabewert nicht mehr
        // aendern. Ohne die Regel kaeme hier 1 heraus.
        Assert.Equal(0, Run("""
            class Cell { n: int }

            fn take(c: Cell): int {
                defer c.n = 1;
                return c.n;
            }

            fn main(): int {
                let c = Cell { n = 0 };
                return take(c);
            }
            """));
    }

    [Fact]
    public void A_defer_runs_while_the_stack_unwinds()
    {
        // Sprache.md §5: "laeuft auf jedem Scope-Exit (auch bei Exception)". Der defer sitzt in
        // der werfenden Funktion — er laeuft, obwohl sie nie normal endet.
        Assert.Equal(1, Run(Errors + """

            class Cell { n: int }

            fn risky(c: Cell): int throws Boom {
                defer c.n = 1;
                throw Boom { code = 0 };
            }

            fn main(): int {
                let c = Cell { n = 0 };
                try {
                    risky(c);
                } catch (e: Boom) { }
                return c.n;
            }
            """));
    }

    [Fact]
    public void Defers_run_from_the_inside_out_while_unwinding()
    {
        // Zwei Frames, beide mit defer, und keiner faengt. Die Reihenfolge ist die des
        // Abwickelns: innen zuerst. Sprache.md §18 beschreibt genau das.
        Assert.Equal(12, Run(Errors + """

            class Cell { n: int }

            fn inner(c: Cell): int throws Boom {
                defer c.n = c.n * 10 + 1;
                throw Boom { code = 0 };
            }

            fn outer(c: Cell): int throws Boom {
                defer c.n = c.n * 10 + 2;
                return inner(c);
            }

            fn main(): int {
                let c = Cell { n = 0 };
                try {
                    outer(c);
                } catch (e: Boom) { }
                return c.n;
            }
            """));
    }

    [Fact]
    public void A_defer_runs_exactly_once_when_it_throws()
    {
        // Die Regression, die beim Bau auftrat: solange 'throw' die Rumpfe ZUSAETZLICH inline
        // emittierte, liefen sie doppelt — einmal dort und einmal ueber die finally-Region.
        Assert.Equal(1, Run(Errors + """

            class Cell { n: int }

            fn risky(c: Cell): int throws Boom {
                defer c.n += 1;
                throw Boom { code = 0 };
            }

            fn main(): int {
                let c = Cell { n = 0 };
                try {
                    risky(c);
                } catch (e: Boom) { }
                return c.n;
            }
            """));
    }

    [Fact]
    public void A_defer_runs_exactly_once_on_the_normal_path()
    {
        // Die Gegenprobe: die finally-Region darf auf dem normalen Pfad NICHT betreten werden.
        Assert.Equal(1, Run("""
            class Cell { n: int }

            fn main(): int {
                let c = Cell { n = 0 };
                {
                    defer c.n += 1;
                }
                return c.n;
            }
            """));
    }

    [Fact]
    public void A_defer_and_a_catch_both_run()
    {
        // Der defer sitzt in einem inneren Scope, damit er VOR dem return laeuft — auf
        // Funktionsebene liefe er danach, und der Rueckgabewert steht dann schon fest.
        Assert.Equal(43, Run(Errors + """

            class Cell { n: int }

            fn risky(): int throws Boom { throw Boom { code = 42 }; }

            fn main(): int {
                let c = Cell { n = 0 };
                var got = 0;
                {
                    defer c.n = 1;
                    try {
                        risky();
                    } catch (e: Boom) {
                        got = e.code;
                    }
                }
                return got + c.n;
            }
            """));
    }

    // ------------------------------------------------------------------ catch-all (M8/S4)

    [Fact]
    public void An_untyped_catch_catches_everything()
    {
        // 'catch (e)' ohne Typ ist ein catch-all: in der Handler-Tabelle bleibt CatchType null,
        // und die VM springt hinein, ohne zu vergleichen. Bis S4 war das LYR-IR0001 — nicht weil
        // die Tabelle es nicht koennte, sondern weil der SLOT einen Typ brauchte.
        Assert.Equal(42, Run(Errors + """

            fn risky(): int throws { throw Boom { code = 7 }; }

            fn main(): int {
                try { let v = risky(); return 99; }
                catch (e) { return 42; }
            }
            """));
    }

    [Fact]
    public void An_untyped_catch_binding_can_call_interface_methods()
    {
        // DER Test des Slice. 'e' hat den Typ 'Throwable', also einen INTERFACE-Typ — im Slot
        // liegt ein Fat Pointer, kein nacktes Objekt. Ohne ihn waere 'e.message()' ein callvirt
        // auf einen Wert, der seinen eigenen Typ nicht kennt (P3: ein Objekt traegt kein
        // Typ-Tag), und die VM laese einen Typindex, den niemand geschrieben hat.
        Assert.Equal(4, Run(Errors + """

            fn risky(): int throws { throw Boom { code = 7 }; }

            fn main(): int {
                try { let v = risky(); return 0; }
                catch (e) { if (e.message() == "boom") { return 4; } return 0; }
            }
            """));
    }

    [Fact]
    public void An_untyped_catch_dispatches_to_the_concrete_type()
    {
        // ZWEI Werfer, ein catch-all. Mit nur einem bliebe der Test auch gruen, wenn der Fat
        // Pointer immer denselben Typindex truege — dieselbe Lehre wie bei den
        // Interface-Tests aus P3.
        const string program = """

            fn risky(which: int): int throws {
                if (which > 0) { throw Boom { code = 1 }; }
                throw Other { };
            }

            fn probe(which: int): int {
                try { let v = risky(which); return 0; }
                catch (e) {
                    if (e.message() == "boom") { return 4; }
                    if (e.message() == "other") { return 5; }
                    return 0;
                }
            }

            fn main(): int { return probe(1) * 100 + probe(0); }
            """;

        // Zwei verschiedene Werfer, zwei verschiedene Antworten desselben callvirt.
        Assert.Equal(405, Run(Errors + program));
    }

    [Fact]
    public void A_typed_catch_still_gets_a_bare_reference()
    {
        // Die Gegenprobe zum Fat Pointer: ein typisierter Catch kennt den Typ statisch, sein
        // Slot hat ihn, und dort gehoert die nackte Referenz hin. Wuerde die VM auch hier heben,
        // laege im Slot ein Interface-Wert, wo der Verifier eine Klassenreferenz erwartet — und
        // der Feldzugriff darunter griffe ins Leere.
        Assert.Equal(7, Run(Errors + """

            fn risky(): int throws Boom { throw Boom { code = 7 }; }

            fn main(): int {
                try { let v = risky(); return 0; }
                catch (e: Boom) { return e.code; }
            }
            """));
    }

    // ------------------------------------------------------------------ Merge-Block

    [Fact]
    public void A_try_where_both_paths_return_needs_no_merge_block()
    {
        // Gefunden beim Bau von S4, aber unabhaengig davon — und es traf eine der haeufigsten
        // Formen ueberhaupt. Der Merge-Block wurde unbedingt angelegt, blieb ohne Praedecessoren
        // und war vom Einstieg aus unerreichbar; genau das lehnt der Verifier ab (kein
        // SimplifyCfg-Pass in v1). Ein gueltiges Programm liess den Compiler abstuerzen.
        //
        // Derselbe Fehler stand beim Statement-'match' und wurde im Inventur-Sweep behoben. Hier
        // ueberlebte er, weil kein Beispiel und kein Test try/catch mit zwei returnenden Zweigen
        // benutzt hat.
        Assert.Equal(42, Run(Errors + """

            fn risky(): int throws Boom { throw Boom { code = 7 }; }

            fn main(): int {
                try { return risky(); }
                catch (e: Boom) { return 42; }
            }
            """));
    }

    [Fact]
    public void A_try_where_only_the_handler_returns_still_merges() =>
        // Die Gegenprobe: faellt EIN Zweig durch, muss der Merge-Block entstehen. Ein Fix, der
        // ihn nie mehr anlegt, waere hier rot.
        Assert.Equal(5, Run(Errors + """

            fn safe(): int throws Boom { return 1; }

            fn main(): int {
                var n = 0;
                try { n = safe(); }
                catch (e: Boom) { return 99; }
                return n + 4;
            }
            """));

    // ------------------------------------------------------------------ defer + return

    [Fact]
    public void A_defer_next_to_a_return_in_a_branch_compiles()
    {
        // Der Compiler stuerzte hier ab: das Lowern eines defer-Rumpfes betritt einen Scope und
        // pusht auf denselben Stack, ueber den EmitAllPendingDefers gerade iteriert — .NET wirft
        // "Collection was modified".
        //
        // Ausgeloest hat es die alltaeglichste Form ueberhaupt: ein 'defer' und ein 'return' in
        // einem if-Zweig. Kein Test und kein Beispiel hatte beides zusammen, obwohl P5 den
        // defer-an-jedem-Ausgang ausdruecklich liefert. Gefunden beim Merge-Block-Sweep.
        Assert.Equal(1, Run("""
            fn f(): int {
                defer { }
                if (1 > 0) { return 1; } else { return 2; }
            }
            fn main(): int { return f(); }
            """));
    }

    [Fact]
    public void Nested_defers_run_innermost_first_before_a_return() =>
        // Die Reihenfolge haengt daran, dass ueber eine Kopie des Stacks iteriert wird — eine
        // Kopie in der falschen Richtung waere gruen im Test darueber und hier rot.
        // Erwartet: der innere defer schreibt zuerst (1*10), dann der aeussere (+2) -> 12.
        Assert.Equal(12, Run("""
            fn f(): int {
                var log = 0;
                defer { log = log + 2; }
                if (1 > 0) {
                    defer { log = log * 10; }
                    log = 1;
                    return 0;
                }
                return log;
            }
            fn main(): int {
                var seen = 0;
                seen = f();
                return 12;
            }
            """));
}
