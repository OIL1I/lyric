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
/// Compound assignment through the operator interfaces — the limit v1.5.0 shipped with, closed:
/// <c>v += w</c> on an <c>Add&lt;T&gt;</c> type is the stored operator call, lowered whole, for
/// identifier targets. Field and element targets stay written out — the call would evaluate the
/// object or index a second time, and that is a language question, not an implementation slip.
/// </summary>
public class CompoundOperatorTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static (Compilation Comp, DiagnosticEngine Diagnostics) Front(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        return (comp, de);
    }

    private static long Run(string source)
    {
        var (comp, de) = Front(source);
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

    private static string[] Errors(string source)
    {
        var (comp, de) = Front(source);
        var binding = comp.Resolve();
        Semantics.Analyze(comp, binding, de);
        Assert.True(de.HasErrors, "the program compiled, but the test expects it to be refused");
        return de.Diagnostics.Select(d => d.Code).ToArray();
    }

    private const string Vec =
        """
        import std.core { Add };

        struct Vec2 :: [Add<Vec2>] {
            x: int,
            y: int,

            fn add(other: Vec2): Vec2 {
                return Vec2 { x = this.x + other.x, y = this.y + other.y };
            }
        }
        """;

    [Fact]
    public void A_struct_compound_calls_the_operator_method()
    {
        Assert.Equal(21, Run(Vec + """

            fn main(): int {
                var v = Vec2 { x = 1, y = 2 };
                let w = Vec2 { x = 10, y = 20 };
                v += w;
                v += w;
                return v.x;
            }
            """));
    }

    [Fact]
    public void A_captured_variable_takes_the_operator_compound()
    {
        Assert.Equal(11, Run(Vec + """

            fn main(): int {
                var v = Vec2 { x = 1, y = 2 };
                let bump = (): void => {
                    v += Vec2 { x = 10, y = 0 };
                };
                bump();
                return v.x;
            }
            """));
    }

    [Fact]
    public void A_field_target_stays_written_out()
    {
        Assert.Contains("LYR-SEM0003", Errors(Vec + """

            class Holder {
                v: Vec2,
            }

            fn main(): int {
                let h = Holder { v = Vec2 { x = 1, y = 2 } };
                h.v += Vec2 { x = 1, y = 1 };
                return h.v.x;
            }
            """));
    }

    [Fact]
    public void The_builtin_repetition_compound_is_untouched()
    {
        // 's *= 3' rides an opcode, not an interface; the operator path must not swallow it.
        Assert.Equal(6, Run("""
            import std.string as strings;

            fn main(): int {
                var s = "ab";
                s *= 3;
                return s.length();
            }
            """));
    }
}
