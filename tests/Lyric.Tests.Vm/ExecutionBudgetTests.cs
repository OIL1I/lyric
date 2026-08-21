using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// The instruction budget: what a capability cannot express.
///
/// <para>A module that reaches nothing still owns the thread it runs on, and no load-time check
/// can see a loop that never ends. The budget is the second half of the sandbox — and it is
/// COUNTED rather than timed, so the tests here can assert exactly where a run stops.</para>
///
/// <para>The load case carries its own weight: the global initializer runs before a host has
/// called anything, so foreign code gets its first chance to hang there.</para>
/// </summary>
public class ExecutionBudgetTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static BytecodeModule Compile(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile: " + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
    }

    private static LoadedProgram Load(string source, ExecutionBudget? budget = null) =>
        LoadedProgram.Load(Compile(source), NativeRegistry.CreateDefault(TextWriter.Null,
            TextWriter.Null), Capability.All, budget);

    private const string Endless = """
        fn main(): int {
            var n = 0;
            while (true) {
                n = n + 1;
            }
            return n;
        }
        """;

    // ------------------------------------------------------------------ the object itself

    [Fact]
    public void A_budget_starts_full()
    {
        var budget = new ExecutionBudget(1000);

        Assert.Equal(1000, budget.Limit);
        Assert.Equal(1000, budget.Remaining);
        Assert.Equal(0, budget.Consumed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_budget_of_nothing_is_refused(long instructions) =>
        // Zero would stop before the first instruction — nobody means that, and a host that
        // computes its number would rather hear about the zero than watch every call fail.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionBudget(instructions));

    // ------------------------------------------------------------------ stopping a run

    [Fact]
    public void An_endless_loop_stops_at_the_budget()
    {
        var program = Load(Endless);
        var budget = new ExecutionBudget(10_000);

        var panic = Assert.Throws<LyricPanic>(() => program.RunEntry([], budget));

        Assert.Equal("LYR-CAP0002", panic.Code);
        Assert.Contains("10000", panic.Message);
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void The_stop_carries_the_frame_stack_like_any_panic()
    {
        // The callee is padded past the inline budget on purpose: a small one is spliced into its
        // caller, and a stack of one frame would prove nothing about the frames a budget panic
        // leaves standing.
        var program = Load("""
            fn spin(): int {
                var n = 0;
                n = n + 0;
                n = n + 1;
                n = n + 2;
                n = n + 3;
                n = n + 4;
                n = n + 5;
                n = n + 6;
                n = n + 7;
                n = n + 8;
                n = n + 9;
                n = n + 10;
                n = n + 11;
                n = n + 12;
                n = n + 13;
                n = n + 14;
                n = n + 15;
                n = n + 16;
                n = n + 17;
                n = n + 18;
                n = n + 19;
                n = n + 20;
                n = n + 21;
                n = n + 22;
                n = n + 23;
                n = n + 24;
                n = n + 25;
                n = n + 26;
                n = n + 27;
                n = n + 28;
                n = n + 29;

                while (true) {
                    n = n + 1;
                }
                return n;
            }

            fn main(): int {
                return spin();
            }
            """);

        var panic = Assert.Throws<LyricPanic>(() =>
            program.RunEntry([], new ExecutionBudget(5_000)));

        Assert.Contains(panic.CallStack, frame => frame.Contains("spin"));
        Assert.Contains(panic.CallStack, frame => frame.Contains("main"));
    }

    [Fact]
    public void A_budget_the_program_stays_under_changes_nothing()
    {
        var program = Load("fn main(): int { return 7; }");
        var budget = new ExecutionBudget(1_000_000);

        Assert.Equal(7, program.RunEntry([], budget).AsI64);
        Assert.True(budget.Consumed > 0, "a completed run still spends instructions");
        Assert.True(budget.Consumed < 1_000, $"a return of 7 cost {budget.Consumed} instructions");
    }

    [Fact]
    public void The_same_program_stops_at_the_same_instruction_twice()
    {
        // Counted, not timed: the whole reason to prefer a budget over a wall clock. Two runs of
        // one program under one limit have to agree, or a replay is worthless.
        var first = new ExecutionBudget(4_321);
        var second = new ExecutionBudget(4_321);

        Assert.Throws<LyricPanic>(() => Load(Endless).RunEntry([], first));
        Assert.Throws<LyricPanic>(() => Load(Endless).RunEntry([], second));

        Assert.Equal(first.Consumed, second.Consumed);
    }

    // ------------------------------------------------------------------ what the program cannot do about it

    [Fact]
    public void The_program_cannot_catch_its_own_stop()
    {
        // A catchable stop is one a hostile script sits out. It is a panic, and a panic is not a
        // Lyric exception.
        var program = Load("""
            import std.core { Exception };

            fn spin(): int throws Exception {
                var n = 0;
                while (true) {
                    n = n + 1;
                }
                return n;
            }

            fn main(): int {
                try {
                    return spin();
                } catch (other) {
                    return 1;
                }
            }
            """);

        var panic = Assert.Throws<LyricPanic>(() => program.RunEntry([], new ExecutionBudget(5_000)));
        Assert.Equal("LYR-CAP0002", panic.Code);
    }

    [Fact]
    public void A_defer_does_not_run_after_the_stop()
    {
        // The other half of the same property: a defer would be a place to keep working from.
        var output = new StringWriter();
        var program = LoadedProgram.Load(Compile("""
            import std.io.console { println };

            fn spin(): int {
                defer println("deferred");
                var n = 0;
                while (true) {
                    n = n + 1;
                }
                return n;
            }

            fn main(): int {
                return spin();
            }
            """), NativeRegistry.CreateDefault(output, TextWriter.Null), Capability.All);

        Assert.Throws<LyricPanic>(() => program.RunEntry([], new ExecutionBudget(5_000)));
        Assert.Equal("", output.ToString());
    }

    // ------------------------------------------------------------------ where the budget applies

    [Fact]
    public void The_global_initializer_is_covered()
    {
        // Foreign code runs BEFORE the host calls anything. Without this, a mod hangs the load.
        var module = Compile("""
            fn spin(): int {
                var n = 0;
                while (true) {
                    n = n + 1;
                }
                return n;
            }

            let TRAP = spin();

            pub fn tick(): int {
                return TRAP;
            }
            """);

        var panic = Assert.Throws<LyricPanic>(() => LoadedProgram.Load(module,
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null), Capability.All,
            new ExecutionBudget(5_000)));

        Assert.Equal("LYR-CAP0002", panic.Code);
    }

    [Fact]
    public void An_invoked_function_is_covered()
    {
        var program = Load("""
            pub fn spin(): int {
                var n = 0;
                while (true) {
                    n = n + 1;
                }
                return n;
            }
            """);
        var index = program.IndexOfFunction("main.spin");

        var panic = Assert.Throws<LyricPanic>(() =>
            program.Invoke(index, new ExecutionBudget(5_000)));

        Assert.Equal("LYR-CAP0002", panic.Code);
    }

    [Fact]
    public void Two_calls_share_one_budget()
    {
        // What a host bounds when it bounds a frame rather than a call: four scripts, one kitty.
        var program = Load("""
            pub fn work(rounds: int): int {
                var n = 0;
                var i = 0;
                while (i < rounds) {
                    n = n + i;
                    i = i + 1;
                }
                return n;
            }
            """);
        var index = program.IndexOfFunction("main.work");
        var budget = new ExecutionBudget(1_000_000);

        program.Invoke(index, budget, LyrValue.FromI64(100));
        var afterFirst = budget.Consumed;
        program.Invoke(index, budget, LyrValue.FromI64(100));

        Assert.True(budget.Consumed > afterFirst, "the second call draws from the same budget");
        Assert.Equal(afterFirst * 2, budget.Consumed);
    }

    [Fact]
    public void Reset_refills_between_frames()
    {
        var program = Load("pub fn tick(): int { return 1; }");
        var index = program.IndexOfFunction("main.tick");
        var budget = new ExecutionBudget(10_000);

        program.Invoke(index, budget);
        var spent = budget.Consumed;
        budget.Reset();

        Assert.Equal(10_000, budget.Remaining);
        Assert.Equal(0, budget.Consumed);

        program.Invoke(index, budget);
        Assert.Equal(spent, budget.Consumed);
    }

    [Fact]
    public void Without_a_budget_nothing_is_metered()
    {
        // The unmetered path is the loop that existed before budgets, and stays it.
        var program = Load("""
            pub fn work(): int {
                var n = 0;
                var i = 0;
                while (i < 100000) {
                    n = n + i;
                    i = i + 1;
                }
                return n;
            }
            """);

        Assert.Equal(4999950000, program.Invoke(program.IndexOfFunction("main.work")).AsI64);
    }
}
