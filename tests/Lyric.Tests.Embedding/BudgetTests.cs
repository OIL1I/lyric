using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;
using Lyric.Vm;

namespace Lyric.Tests.Embedding;

/// <summary>
/// The instruction budget as a host sees it: the half of the sandbox that stops a script which
/// is not doing anything forbidden — just not stopping.
///
/// <para>The distinction the type carries is the point of this file. A panic says the script is
/// broken; a spent budget says it was still working. A mod loader disables the first and may hand
/// the second a bigger budget, and neither decision can be made from a string comparison on a
/// diagnostic code.</para>
/// </summary>
public class BudgetTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static LangVm Vm() => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        Capabilities = Capability.None,
    });

    private static ScriptInstance Instance(string source)
    {
        var vm = Vm();
        return vm.Instantiate(vm.Compile(source, "mod"));
    }

    private const string Endless = """
        pub fn update(dt: float): void {
            var n = 0;
            while (true) {
                n = n + 1;
            }
        }
        """;

    // ------------------------------------------------------------------ stopping a call

    [Fact]
    public void A_call_that_never_returns_is_stopped()
    {
        var instance = Instance(Endless);
        var budget = new ExecutionBudget(50_000);

        var stopped = Assert.Throws<ScriptBudgetException>(
            () => instance.CallVoid("update", budget, 0.016));

        Assert.Equal("LYR-CAP0002", stopped.Code);
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void A_stopped_call_is_still_a_panic_to_a_host_that_only_knows_panics()
    {
        // The inheritance is the compatibility promise: a host written before budgets existed
        // keeps catching everything it used to catch.
        var stopped = Assert.Throws<ScriptBudgetException>(
            () => Instance(Endless).CallVoid("update", new ExecutionBudget(50_000), 0.016));

        Assert.IsAssignableFrom<ScriptPanicException>(stopped);
        Assert.IsAssignableFrom<ScriptException>(stopped);
    }

    [Fact]
    public void A_broken_script_and_a_slow_one_are_different_types()
    {
        var instance = Instance("""
            import std.core { panic };

            pub fn broken(): void {
                panic("this script is wrong");
            }
            """);

        var panic = Assert.Throws<ScriptPanicException>(
            () => instance.CallVoid("broken", new ExecutionBudget(50_000)));

        Assert.IsNotType<ScriptBudgetException>(panic);
    }

    [Fact]
    public void A_call_that_fits_returns_normally()
    {
        var instance = Instance("pub fn add(a: int, b: int): int { return a + b; }");
        var budget = new ExecutionBudget(100_000);

        Assert.Equal(30, instance.Call<long>("add", budget, 10, 20));
        Assert.True(budget.Consumed > 0);
        Assert.True(budget.Remaining > 0);
    }

    [Fact]
    public void Without_a_budget_a_call_is_unmetered() =>
        Assert.Equal(30, Instance("pub fn add(a: int, b: int): int { return a + b; }")
            .Call<long>("add", 10, 20));

    // ------------------------------------------------------------------ a frame across scripts

    [Fact]
    public void One_budget_bounds_a_whole_frame()
    {
        // Two scripts, one kitty: what a host actually wants to bound is the frame, not a call.
        var vm = Vm();
        var source = "pub fn update(dt: float): void { var n = 0; while (true) { n = n + 1; } }";
        var first = vm.Instantiate(vm.Compile(source, "one"));
        var second = vm.Instantiate(vm.Compile(source, "two"));
        var frame = new ExecutionBudget(50_000);

        Assert.Throws<ScriptBudgetException>(() => first.CallVoid("update", frame, 0.016));
        var stopped = Assert.Throws<ScriptBudgetException>(
            () => second.CallVoid("update", frame, 0.016));

        // The second script gets nothing: the first spent the frame. Harsh and correct — the
        // alternative is a frame budget that is not one.
        Assert.Equal("LYR-CAP0002", stopped.Code);
        Assert.Equal(0, frame.Remaining);
    }

    [Fact]
    public void Reset_gives_the_next_frame_its_own_budget()
    {
        var instance = Instance("pub fn tick(): int { return 1; }");
        var budget = new ExecutionBudget(50_000);

        instance.Call<long>("tick", budget);
        var first = budget.Consumed;
        budget.Reset();
        instance.Call<long>("tick", budget);

        Assert.Equal(first, budget.Consumed);
    }

    // ------------------------------------------------------------------ loading

    [Fact]
    public void A_module_that_hangs_in_its_initializer_is_stopped_at_load()
    {
        // Before this, foreign code did not need a call to hang a host: the constant initializer
        // runs inside Instantiate.
        var vm = Vm();
        var module = vm.Compile("""
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
            """, "mod");

        var stopped = Assert.Throws<ScriptBudgetException>(
            () => vm.Instantiate(module, new ExecutionBudget(50_000)));

        Assert.Equal("LYR-CAP0002", stopped.Code);
    }

    [Fact]
    public void An_ordinary_module_instantiates_under_a_budget()
    {
        var vm = Vm();
        var budget = new ExecutionBudget(1_000_000);
        var instance = vm.Instantiate(vm.Compile("""
            let GREETING = "hello";

            pub fn greet(): string {
                return GREETING;
            }
            """, "mod"), budget);

        Assert.Equal("hello", instance.Call<string>("greet"));
        Assert.True(budget.Consumed > 0, "the initializer ran under the budget");
    }

    // ------------------------------------------------------------------ the attribute route

    [Fact]
    public void An_attribute_call_takes_a_budget_too()
    {
        // The per-frame path a game host actually uses: resolve once through the rows, call by
        // handle. It gets the same bound as the by-name form.
        var vm = Vm();
        var instance = vm.Instantiate(vm.Compile("""
            import std.core { OnFunction };

            pub struct Update :: [OnFunction] { }

            @Update
            pub fn onUpdate(dt: float): void {
                var n = 0;
                while (true) {
                    n = n + 1;
                }
            }
            """, "mod"));
        var use = instance.Attributes.OnFunctions("Update").Single();

        Assert.Throws<ScriptBudgetException>(
            () => instance.CallVoid(use, new ExecutionBudget(50_000), 0.016));
    }
}
