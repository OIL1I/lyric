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
/// <c>opaque type</c> at runtime: the value IS its underlying — the cast costs nothing, equality
/// is the underlying comparison, and the alias flows through generics, arrays and optionals like
/// any other type. Identity is enforced at compile time and only there.
/// </summary>
public class OpaqueTypeTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static long Run(string source)
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
        Assert.False(de.HasErrors, "source did not compile:\n" + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    [Fact]
    public void The_cast_is_free_and_the_value_survives_the_round_trip() =>
        Assert.Equal(42, Run("""
            opaque type Entity = int;

            fn main(): int {
                let e = 42 as Entity;
                return e as int;
            }
            """));

    [Fact]
    public void Equality_compares_the_underlying() =>
        Assert.Equal(2, Run("""
            opaque type Entity = int;

            fn main(): int {
                let a = 7 as Entity;
                let b = 7 as Entity;
                let c = 9 as Entity;
                var score = 0;
                if (a == b) {
                    score = score + 1;
                }
                if (a != c) {
                    score = score + 1;
                }
                return score;
            }
            """));

    [Fact]
    public void The_alias_flows_through_generics_arrays_and_optionals() =>
        Assert.Equal(16, Run("""
            import std.collections { List };

            opaque type Entity = int;

            fn pick(xs: Entity[], maybe: ?Entity): Entity {
                return maybe ?? xs[0];
            }

            fn main(): int {
                var held = List<Entity>.empty();
                held.push(1 as Entity);
                held.push(7 as Entity);

                let array: Entity[] = [held.get(1), 3 as Entity];
                let chosen = pick(array, 9 as Entity);
                let fallback = pick(array, null);
                return (chosen as int) + (fallback as int);
            }
            """));
}
