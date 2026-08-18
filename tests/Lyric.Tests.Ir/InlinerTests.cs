using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Ir;

/// <summary>
/// The inliner, observed on real lowered code: the call disappears, the verifier stays silent,
/// and the boundaries — handlers, recursion, size — hold. What inlined code COMPUTES is covered
/// by the VM tests, which run the whole pipeline with the pass on.
/// </summary>
public class InlinerTests
{
    /// <summary>Source to optimized IR, verifier on: a malformed splice throws here rather than
    /// surfacing as a wrong value later.</summary>
    private static IrModule Optimized(string source)
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
        if (de.HasErrors)
        {
            var writer = new StringWriter();
            de.RenderText(writer);
            Assert.Fail("source did not type-check:\n" + writer);
        }

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true, optimize: true);
        Assert.NotNull(ir);
        return ir!;
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static IrFunction Main(IrModule module) =>
        module.Functions.Single(f => f.Name == "main.main");

    private static IEnumerable<IrOp> Ops(IrFunction function) =>
        function.Blocks.SelectMany(b => b.Insts);

    // ------------------------------------------------------------------ the pass fires

    [Fact]
    public void A_small_callee_is_inlined_and_pruned()
    {
        var module = Optimized("""
            fn double(n: int): int { return n * 2; }
            fn main(): int { return double(21); }
            """);

        // The call is gone from main, and the callee — nobody calls it now — is gone from the
        // module: inlining and the reachability pruning cooperate in one run.
        Assert.DoesNotContain(Ops(Main(module)), op => op is Call);
        Assert.DoesNotContain(module.Functions, f => f.Name == "main.double");
    }

    [Fact]
    public void A_chain_of_small_calls_collapses_in_one_pass()
    {
        var module = Optimized("""
            fn a(n: int): int { return n + 1; }
            fn b(n: int): int { return a(n) + 1; }
            fn main(): int { return b(40); }
            """);

        Assert.DoesNotContain(Ops(Main(module)), op => op is Call);
        Assert.Single(module.Functions); // only main survives
    }

    [Fact]
    public void A_void_callee_is_inlined()
    {
        var module = Optimized("""
            class Counter { value: int }

            let state = Counter { value = 0 };

            fn bump(): void { state.value = state.value + 1; }
            fn main(): int { bump(); bump(); return state.value; }
            """);

        Assert.DoesNotContain(Ops(Main(module)), op => op is Call);
    }

    [Fact]
    public void A_callee_returning_from_two_blocks_routes_through_one_local()
    {
        var module = Optimized("""
            fn pick(n: int): int { if (n > 0) { return 1; } return 2; }
            fn main(): int { return pick(5); }
            """);

        var main = Main(module);
        Assert.DoesNotContain(Ops(main), op => op is Call);

        // Both returns feed the same synthetic local; the continuation reads it exactly once.
        var result = main.Locals.Single(l => l.Name == "__inl_ret");
        Assert.Equal(2, Ops(main).OfType<StoreLocal>().Count(s => s.Local == result.Id));
        Assert.Equal(1, Ops(main).OfType<LoadLocal>().Count(l => l.Local == result.Id));
    }

    // ------------------------------------------------------------------ the boundaries hold

    [Fact]
    public void A_recursive_callee_stays_a_call()
    {
        var module = Optimized("""
            fn fac(n: int): int { return if (n <= 1) 1 else n * fac(n - 1); }
            fn main(): int { return fac(5); }
            """);

        // The body was spliced into main once — but inside the copy the self call remains, and
        // the function itself therefore survives the pruning.
        Assert.Contains(module.Functions, f => f.Name == "main.fac");
        Assert.Contains(Ops(Main(module)), op => op is Call);
    }

    [Fact]
    public void A_caller_with_handlers_is_left_alone()
    {
        var module = Optimized("""
            class Boom :: [Throwable] { fn message(): string { return "boom"; } }

            fn risky(): int { return 1; }

            fn main(): int {
                try { return risky(); }
                catch (e: Boom) { return 0; }
            }
            """);

        // Handler ranges are contiguous block ranges; spliced blocks would land outside them.
        Assert.Contains(Ops(Main(module)), op => op is Call);
    }

    [Fact]
    public void A_callee_with_handlers_is_not_spliced()
    {
        var module = Optimized("""
            class Boom :: [Throwable] { fn message(): string { return "boom"; } }

            fn guarded(): int {
                try { return 1; }
                catch (e: Boom) { return 0; }
            }

            fn main(): int { return guarded(); }
            """);

        Assert.Contains(Ops(Main(module)), op => op is Call);
    }

    [Fact]
    public void A_large_callee_stays_a_call()
    {
        // Twenty-five additions put the body over the 24-op budget.
        var terms = string.Join(" + ", Enumerable.Repeat("n", 26));
        var module = Optimized(
            "fn wide(n: int): int { return " + terms + "; }\n" +
            "fn main(): int { return wide(1); }");

        Assert.Contains(Ops(Main(module)), op => op is Call);
    }

    // ------------------------------------------------------------------ what travels along

    [Fact]
    public void Spliced_instructions_keep_the_callee_spans()
    {
        const string source = """
            fn double(n: int): int { return n * 2; }
            fn main(): int { return double(21); }
            """;
        var module = Optimized(source);

        // The copied BinOp still points at 'n * 2' in the CALLEE, which is what makes a panic
        // name the right line after inlining.
        var multiply = Ops(Main(module)).OfType<BinOp>().Single(b => b.Kind == IrBinKind.Mul);
        Assert.Equal(source.IndexOf("n * 2", StringComparison.Ordinal), multiply.Span.Start);
    }

    [Fact]
    public void An_attributed_function_is_inlined_at_its_sites_but_survives_as_a_root()
    {
        var module = Optimized("""
            import std.core { OnFunction };

            pub struct Hook :: [OnFunction] { }

            @Hook
            pub fn tick(n: int): int { return n + 1; }

            fn main(): int { return tick(1); }
            """);

        // The call site is folded, but the row in section 11 promises the host a callable
        // function, so the pruning keeps it.
        Assert.DoesNotContain(Ops(Main(module)), op => op is Call);
        Assert.Contains(module.Functions, f => f.Name == "main.tick");
    }
}
