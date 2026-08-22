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
/// The fused branches, executed (format 3.6).
///
/// <para>Which side a <c>brcmp</c> takes is the one thing a disassembly cannot show — swapped
/// targets read perfectly and run backwards. So these cases do not inspect anything; they let the
/// program answer, and every one of them has an answer that differs if the fusion is wrong in any
/// of the ways it could be: the wrong side, the wrong operand order, a comparison done as if it
/// were signed, a constant rebuilt at the wrong width.</para>
///
/// <para>Selection — WHEN a fusion happens — is tested where it is decided, in
/// <c>Lyric.Tests.Bytecode.FusionTests</c>.</para>
/// </summary>
public class FusedBranchTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static LoadedProgram Compile(string source)
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

        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
        return LoadedProgram.Load(module,
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null));
    }

    private static long Call(LoadedProgram program, string name, params LyrValue[] arguments) =>
        program.Invoke(program.IndexOfFunction(name), arguments).AsI64;

    [Theory]
    [InlineData(1, 2, 1)]
    [InlineData(2, 1, 0)]
    [InlineData(2, 2, 0)]
    public void A_fused_branch_takes_the_side_the_pair_took(long a, long b, long expected)
    {
        var program = Compile("""
            pub fn check(a: int, b: int): int {
                if (a < b) { return 1; }
                return 0;
            }
            """);

        Assert.Equal(expected, Call(program, "main.check",
            LyrValue.FromI64(a), LyrValue.FromI64(b)));
    }

    [Fact]
    public void The_operands_keep_their_order()
    {
        // 'a > b' fuses as gt with a first. Swapped, this would answer for 'b > a' — and both
        // arguments being different is what makes the case able to tell.
        var program = Compile("""
            pub fn check(a: int, b: int): int {
                if (a > b) { return 1; }
                return 0;
            }
            """);

        Assert.Equal(1, Call(program, "main.check", LyrValue.FromI64(9), LyrValue.FromI64(4)));
        Assert.Equal(0, Call(program, "main.check", LyrValue.FromI64(4), LyrValue.FromI64(9)));
    }

    [Fact]
    public void A_loop_counts_what_it_counted_before()
    {
        var program = Compile("""
            pub fn check(n: int): int {
                var i = 0;
                var sum = 0;
                while (i < n) {
                    sum = sum + i;
                    i = i + 1;
                }
                return sum;
            }
            """);

        Assert.Equal(45, Call(program, "main.check", LyrValue.FromI64(10)));
        Assert.Equal(0, Call(program, "main.check", LyrValue.FromI64(0)));
        Assert.Equal(0, Call(program, "main.check", LyrValue.FromI64(-5)));
    }

    [Fact]
    public void An_unsigned_comparison_stays_unsigned()
    {
        // The tag decides the machine operation. Compared as signed, the large value would be
        // negative and the answer would flip — which is what the fused form's own tag is for.
        var program = Compile("""
            pub fn check(a: uint, b: uint): int {
                if (a < b) { return 1; }
                return 0;
            }
            """);

        Assert.Equal(1, Call(program, "main.check",
            LyrValue.FromBits(1), LyrValue.FromBits(ulong.MaxValue)));
    }

    [Fact]
    public void A_narrow_constant_is_rebuilt_at_its_own_width()
    {
        // An int8 arrives in the instruction as 0x00..0xFF and has to be brought to the width
        // invariant before it is compared, exactly as 'const' does it. Without that, -1 as int8
        // would compare as 255.
        var program = Compile("""
            pub fn check(a: int8): int {
                if (a < 0 as int8) { return 1; }
                return 0;
            }
            """);

        Assert.Equal(1, Call(program, "main.check", LyrValue.FromI64(-1)));
        Assert.Equal(0, Call(program, "main.check", LyrValue.FromI64(1)));
    }

    [Fact]
    public void A_float_constant_keeps_its_precision()
    {
        var program = Compile("""
            pub fn check(a: float): int {
                if (a < 1.5) { return 1; }
                return 0;
            }
            """);

        Assert.Equal(1, Call(program, "main.check", LyrValue.FromF64(1.4999)));
        Assert.Equal(0, Call(program, "main.check", LyrValue.FromF64(1.5)));
    }

    [Fact]
    public void A_single_precision_constant_compares_in_single_precision()
    {
        // The one case that separates the two float encodings: 0.1f is not 0.1, and a constant
        // widened to double instead of rebuilt as a float would answer the other way.
        var program = Compile("""
            pub fn check(a: float32): int {
                if (a == 0.1 as float32) { return 1; }
                return 0;
            }
            """);

        Assert.Equal(1, Call(program, "main.check", LyrValue.FromF32(0.1f)));
    }

    [Fact]
    public void A_fused_branch_inside_a_protected_region_still_unwinds()
    {
        // The fused form ends a block, so it stands where a terminator stands; a handler range is
        // block-based and must still find it.
        var program = Compile("""
            import std.core { Exception };

            fn small(a: int): int throws Exception {
                if (a < 3) { throw Exception { text = "small" }; }
                return 1;
            }

            pub fn check(a: int): int {
                try {
                    return small(a);
                } catch (e) {
                    return 2;
                }
            }
            """);

        Assert.Equal(2, Call(program, "main.check", LyrValue.FromI64(1)));
        Assert.Equal(1, Call(program, "main.check", LyrValue.FromI64(9)));
    }
}
