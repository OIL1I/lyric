using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Ir;

/// <summary>
/// Devirtualization on real lowered code: a receiver that is provably one <c>mkiface</c> loses
/// its dispatch, one that arrives through a parameter keeps it. Semantics are covered by the VM
/// suite running the whole pipeline.
/// </summary>
public class DevirtualizerTests
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

    [Fact]
    public void A_provably_concrete_receiver_loses_its_dispatch()
    {
        // 'speak' answers the INTERFACE type, so the call in main is a callvirt in the lowering.
        // Inlining brings the mkiface into main, forwarding hands it to the call site, and the
        // devirtualizer turns the dispatch into the direct call — which the second inliner round
        // then folds away entirely.
        var module = Optimized("""
            interface Greeter { fn greet(): int; }

            struct Kind :: [Greeter] { n: int, fn greet(): int { return this.n; } }

            fn speak(): Greeter { return Kind { n = 42 }; }

            fn main(): int {
                let g = speak();
                return g.greet();
            }
            """);

        Assert.DoesNotContain(Ops(Main(module)), op => op is CallVirt);
    }

    [Fact]
    public void A_parameter_typed_receiver_keeps_its_dispatch()
    {
        // 'wide' is over the inline budget on purpose: its receiver arrives as a fat pointer
        // whose concrete type nobody in this function can prove.
        var pad = string.Join(" + ", Enumerable.Repeat("g.greet()", 26));
        var module = Optimized(
            "interface Greeter { fn greet(): int; }\n" +
            "struct Kind :: [Greeter] { n: int, fn greet(): int { return this.n; } }\n" +
            "fn wide(g: Greeter): int { return " + pad + "; }\n" +
            "fn main(): int { return wide(Kind { n = 1 }); }");

        var wide = module.Functions.Single(f => f.Name == "main.wide");
        Assert.Contains(Ops(wide), op => op is CallVirt);
    }

    [Fact]
    public void An_inherited_default_keeps_its_dispatch()
    {
        // The slot resolves to a default declared on the PARENT, whose receiver is the parent's
        // interface type — while the value at hand is the child's. Devirtualizing would put one
        // into the other's slot, and an interface value does not convert to its parent's type.
        // 'describe' is over the inline budget, so the direct call would survive to be seen.
        var pad = string.Join(" + ", Enumerable.Repeat("this.name()", 26));
        var module = Optimized(
            "interface Base { fn name(): int; fn describe(): int { return " + pad + "; } }\n" +
            "interface Child :: [Base] { fn extra(): int; }\n" +
            "class C :: [Child] { fn name(): int { return 1; } fn extra(): int { return 2; } }\n" +
            "fn main(): int { let c: Child = C { }; return c.describe(); }");

        Assert.Contains(Ops(Main(module)), op => op is CallVirt);
    }
}
