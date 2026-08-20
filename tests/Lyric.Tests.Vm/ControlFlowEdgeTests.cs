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
/// The control-flow edges the 2.0.1 audit measured: ranges at the type's boundaries, and
/// defer as a BLOCK affair (§7.2, §7.5 of the specification).
///
/// <para>Before the wave, '..=max' desugared to '..max+1' and wrapped to zero iterations; a
/// small-width range produced malformed IR only the Release verify-skip let run; a uint range
/// crossing 2⁶³ compared signed; and a loop body's defer registered once into the FUNCTION
/// scope — the last iteration's values, fired after the loop's aftermath.</para>
/// </summary>
public class ControlFlowEdgeTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static (long Exit, string Out) Run(string source)
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

        var diagnostics = new StringWriter();
        de.RenderText(diagnostics);
        Assert.False(de.HasErrors, "source did not compile: " + diagnostics);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var output = new StringWriter();
        var exit = Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(output, TextWriter.Null)).AsI64;
        return (exit, output.ToString().ReplaceLineEndings("\n"));
    }

    // ------------------------------------------------------------------ ranges at the edges

    [Fact]
    public void An_inclusive_range_reaches_the_type_maximum() =>
        Assert.Equal(3, Run("""
            fn main(): int {
                var n = 0;
                let hi = 9223372036854775807;
                for (_ in hi - 2..=hi) {
                    n = n + 1;
                    if (n > 5) { return 99; }
                }
                return n;
            }
            """).Exit);

    [Fact]
    public void A_small_width_range_runs_at_its_own_width() =>
        // Verified IR: before the wave this was malformed (u8 bounds in i64 fields) and only
        // the Release verify-skip let it run at all.
        Assert.Equal(6, Run("""
            fn main(): int {
                var n = 0;
                let lo: uint8 = 253;
                let top: uint8 = 255;
                for (_ in lo..=top) { n = n + 1; }
                for (_ in lo..top) { n = n + 1; }
                if (n != 5) { return 0; }
                return 6;
            }
            """).Exit);

    [Fact]
    public void A_uint_range_beyond_the_sign_bit_compares_unsigned() =>
        // On the signed adapters a bound beyond 2^63 reinterprets and the loop runs dry.
        Assert.Equal(3, Run("""
            fn main(): int {
                var n = 0;
                let zero: uint = 0;
                let big = zero - 1;
                for (_ in big - 2..=big) {
                    n = n + 1;
                    if (n > 5) { return 99; }
                }
                return n;
            }
            """).Exit);

    // ------------------------------------------------------------------ defer is the block's

    [Fact]
    public void A_loop_body_defer_runs_every_iteration_and_on_break() =>
        Assert.Equal("body-0\ndefer-0\ndefer-1\nbody-2\ndefer-2\ndefer-3\nafter\n", Run("""
            import std.io.console { println };

            fn main(): int {
                for (i in 0..4) {
                    defer println(f"defer-{i}");
                    if (i == 1) { continue; }
                    if (i == 3) { break; }
                    println(f"body-{i}");
                }
                println("after");
                return 0;
            }
            """).Out);

    [Fact]
    public void A_break_through_nested_blocks_drains_only_the_scopes_it_leaves() =>
        // The outer function-level defer stays scheduled; the nested block's and the loop
        // body's fire at the break, innermost first.
        Assert.Equal("inner\nouter\nafter\nfn\n", Run("""
            import std.io.console { println };

            fn main(): int {
                defer println("fn");
                for (_ in 0..3) {
                    defer println("outer");
                    {
                        defer println("inner");
                        break;
                    }
                }
                println("after");
                return 0;
            }
            """).Out);
}
