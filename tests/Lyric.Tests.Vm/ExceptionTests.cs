using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Exceptions and <c>defer</c>, over the whole pipeline.
///
/// <para>The core is always WHICH PATH WAS TAKEN: a thrown exception has to skip the rest of the
/// <c>try</c> body and arrive in the matching <c>catch</c>, not in the first one it finds.</para>
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
        // Without real unwinding the body would run on and yield 99.
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
        // The counter-check: without it the test above would pass even if everything were ALWAYS caught.
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
        // Two catch clauses, and the second kind is thrown. Without the type comparison the first would
        // catch.
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
        // 'middle' has no try: the exception has to discard its frame and land in main.
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
        // First 'b', registered last, then 'a'. The number distinguishes the orders: 1 then 2 would give
        // 12, 2 then 1 gives 21.
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
        // Go behaves the same way: a 'defer' must not change the already determined return value. Without
        // the rule this would be 1.
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
        // A defer runs on every scope exit, exceptions included. This one sits in the throwing function
        // and runs although that function never ends normally.
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
        // Two frames, both with a defer, and neither catches. The order is that of the unwinding: inner
        // first.
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
        // The regression that appeared while building: as long as 'throw' emitted the bodies inline AS
        // WELL, they ran twice — once there and once through the finally region.
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
        // The counter-check: the finally region must NOT be entered on the normal path.
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
        // The defer sits in an inner scope so it runs BEFORE the return; at function level it would run
        // afterwards, and the return value is settled by then.
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
        // 'catch (e)' without a type is a catch-all: CatchType stays null in the handler table and the VM
        // jumps in without comparing.
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
        // 'e' has the type 'Throwable', so an INTERFACE type: a fat pointer lies in the slot rather than a
        // bare object. Without it 'e.message()' would be a callvirt on a value that does not know its own
        // type, and the VM would read a type index nobody wrote.
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
        // TWO throwers, one catch-all. With only one the test would stay green even if the fat pointer
        // always carried the same type index.
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

        // Two different throwers, two different answers from the same callvirt.
        Assert.Equal(405, Run(Errors + program));
    }

    [Fact]
    public void A_typed_catch_still_gets_a_bare_reference()
    {
        // The counter-check to the fat pointer: a typed catch knows the type statically, its slot has it,
        // and the bare reference belongs there. Were the VM to lift here too, an interface value would lie
        // in the slot where the verifier expects a class reference, and the field access below would run
        // into nothing.
        Assert.Equal(7, Run(Errors + """

            fn risky(): int throws Boom { throw Boom { code = 7 }; }

            fn main(): int {
                try { let v = risky(); return 0; }
                catch (e: Boom) { return e.code; }
            }
            """));
    }

    // ------------------------------------------------------------------ the merge block

    [Fact]
    public void A_try_where_both_paths_return_needs_no_merge_block()
    {
        // One of the most common forms there is. The merge block was created unconditionally, stayed
        // without predecessors and was unreachable from the entry; the verifier rejects exactly that, as
        // there is no SimplifyCfg pass. A valid program made the compiler crash.
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
        // The counter-check: when ONE branch falls through, the merge block has to arise. A fix that never
        // creates it again would be red here.
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
        // The compiler crashed here: lowering a defer body enters a scope and pushes onto the same stack
        // EmitAllPendingDefers is iterating over, and .NET throws.
        //
        // The trigger was the most everyday form there is: a 'defer' and a 'return' in one if branch.
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
        // The order depends on iterating over a copy of the stack: a copy in the wrong direction would be
        // green in the test above and red here. Expected: the inner defer writes first (1*10), then the
        // outer one (+2), giving 12.
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
