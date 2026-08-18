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
/// An attribute describes; it does nothing. These tests hold the second half of that sentence
/// where it counts — in the interpreter: a module carrying sections 11 and 12 runs exactly like
/// one without them, and a struct serving as an attribute stays an ordinary struct in code.
/// </summary>
public class AttributeRunTests
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
    public void An_attributed_module_runs_like_an_unattributed_one() =>
        Assert.Equal(42, Run("""
            module app;
            import std.core { OnType, OnFunction };

            struct Component :: [OnType] { }
            struct System :: [OnFunction] { order: int = 0 }

            @Component
            struct Health { value: int, max: int }

            @System { order = 10 }
            pub fn tick(): int { return 2; }

            fn main(): int {
                let h = Health { value = 40, max = 100 };
                return h.value + tick();
            }
            """));

    /// <summary>The attribute struct itself constructed as a value in the same program: one type
    /// table entry serves the row AND the code, and the row does not disturb the layout the
    /// instructions run against.</summary>
    [Fact]
    public void The_attribute_struct_stays_an_ordinary_value() =>
        Assert.Equal(7, Run("""
            module app;
            import std.core { OnFunction };

            struct System :: [OnFunction] { order: int = 0 }

            @System { order = 3 }
            pub fn tick(): int { return 0; }

            fn main(): int {
                let s = System { order = 7 };
                return s.order;
            }
            """));
}
