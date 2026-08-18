using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Ir;

/// <summary>
/// Scalar replacement, observed on real lowered code: the allocation disappears where the object
/// never leaves its frame, and stays where anything else touches it. What the rewritten code
/// COMPUTES — value semantics, mut write-back, copies — is covered by the VM suite, which runs
/// the whole pipeline with the pass on.
/// </summary>
public class ScalarReplacementTests
{
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

    private static string RepoRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static IrFunction Main(IrModule module) =>
        module.Functions.Single(f => f.Name == "main.main");

    private static IEnumerable<IrOp> Ops(IrFunction function) =>
        function.Blocks.SelectMany(b => b.Insts);

    // ------------------------------------------------------------------ the object dissolves

    [Fact]
    public void A_struct_that_never_leaves_the_frame_is_not_allocated()
    {
        var module = Optimized("""
            struct Vec2 { x: float, y: float }

            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < 10) {
                    let v = Vec2 { x = acc, y = 1.5 };
                    acc = acc + v.x + v.y;
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """);

        Assert.DoesNotContain(Ops(Main(module)), op => op is NewObject or StructCopy);
    }

    [Fact]
    public void The_inlined_method_result_and_the_argument_copy_dissolve_too()
    {
        // The full hot-loop shape: a.add(b) is inlined, the argument copy of b and the result
        // become locals, and the loop allocates nothing.
        var module = Optimized("""
            struct Vec2 {
                x: float,
                y: float,
                fn add(other: Vec2): Vec2 {
                    return Vec2 { x = this.x + other.x, y = this.y + other.y };
                }
            }

            fn main(): int {
                var a = Vec2 { x = 0.0, y = 0.0 };
                let b = Vec2 { x = 0.5, y = 0.25 };
                var i = 0;
                while (i < 10) {
                    a = a.add(b);
                    i = i + 1;
                }
                return if (a.x > 0.0) 0 else 1;
            }
            """);

        Assert.DoesNotContain(Ops(Main(module)), op => op is NewObject or StructCopy or Call);
    }

    [Fact]
    public void A_range_loop_carries_no_iterator_object()
    {
        // for-in builds a RangeIterator — a CLASS — whose next() is inlined; what remains of the
        // iterator is its two int fields as locals.
        var module = Optimized("""
            fn main(): int {
                var sum = 0;
                for (i in 0..100) {
                    sum = sum + i;
                }
                return if (sum > 0) 0 else 1;
            }
            """);

        Assert.DoesNotContain(Ops(Main(module)), op => op is NewObject);
    }

    // ------------------------------------------------------------------ the object stays

    [Fact]
    public void A_returned_struct_keeps_its_allocation()
    {
        // 'build' is over the inline budget, so it survives as a function — and inside it the
        // value escapes by return and keeps its allocation. Only inlining would have licensed
        // the caller-side dissolve, which is the whole point of the order "inline first,
        // scalarize after".
        var sum = string.Join(" + ", Enumerable.Repeat("s", 26));
        var module = Optimized(
            "struct Vec2 { x: float, y: float }\n" +
            "fn build(s: float): Vec2 {\n" +
            "    let n = " + sum + ";\n" +
            "    return Vec2 { x = n, y = s };\n" +
            "}\n" +
            "fn main(): int {\n" +
            "    let v = build(1.0);\n" +
            "    return if (v.x > 0.0) 0 else 1;\n" +
            "}");

        var build = module.Functions.Single(f => f.Name == "main.build");
        Assert.Contains(Ops(build), op => op is NewObject);
        Assert.Contains(Ops(Main(module)), op => op is Call);
    }

    [Fact]
    public void A_struct_stored_into_an_array_keeps_its_allocation()
    {
        var module = Optimized("""
            struct Vec2 { x: float, y: float }

            fn main(): int {
                let xs = [Vec2 { x = 1.0, y = 2.0 }];
                return if (xs[0].x > 0.0) 0 else 1;
            }
            """);

        Assert.Contains(Ops(Main(module)), op => op is NewObject);
    }

    [Fact]
    public void A_struct_handed_to_a_kept_call_keeps_its_allocation()
    {
        // 'wide' is over the inline budget, so the call survives — and a call argument escapes.
        var terms = string.Join(" + ", Enumerable.Repeat("v.x", 26));
        var module = Optimized(
            "struct Vec2 { x: float, y: float }\n" +
            "fn wide(v: Vec2): float { return " + terms + "; }\n" +
            "fn main(): int {\n" +
            "    let v = Vec2 { x = 1.0, y = 2.0 };\n" +
            "    return if (wide(v) > 0.0) 0 else 1;\n" +
            "}");

        Assert.Contains(Ops(Main(module)), op => op is Call);
        Assert.Contains(Ops(Main(module)), op => op is NewObject);
    }
}
