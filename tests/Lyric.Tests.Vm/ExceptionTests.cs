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
}
