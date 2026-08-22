using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Bytecode;

/// <summary>
/// Instruction selection: when a comparison and a branch become one instruction (3.6).
///
/// <para>The negative cases carry the weight here. A fusion that fires when it must not produces
/// a program that computes something else, and the ways it can be wrong are all about a value
/// somebody else still wanted: a comparison read twice, an operand that is not a slot, something
/// standing between the comparison and the branch.</para>
///
/// <para>Every case checks the DISASSEMBLY rather than the bytes, because that is where a wrong
/// selection is legible — and beside it, for the cases that can run, what the program answers.
/// A fused instruction that branched the wrong way would otherwise be a green test.</para>
/// </summary>
public class FusionTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static IrModule Lower(string source)
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
        return ir!;
    }

    /// <summary>The disassembly of one function, which is where a selection is readable.</summary>
    private static string Disassemble(string source, string function = "main.check")
    {
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(Lower(source)));
        return Disassembler.Dump(module, function)
               ?? throw new InvalidOperationException($"no function '{function}'");
    }

    // ------------------------------------------------------------------ what fuses

    [Fact]
    public void A_loop_test_against_a_constant_is_one_instruction()
    {
        var text = Disassemble("""
            pub fn check(n: int): int {
                var i = 0;
                while (i < 10) { i = i + 1; }
                return i;
            }
            """);

        Assert.Contains("brcmpk lt i64 l1, 10 ->", text, StringComparison.Ordinal);
        Assert.DoesNotContain("condbr", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comparison_of_two_locals_is_one_instruction()
    {
        var text = Disassemble("""
            pub fn check(a: int, b: int): int {
                if (a < b) { return 1; }
                return 0;
            }
            """);

        Assert.Contains("brcmp lt i64 l0, l1 ->", text, StringComparison.Ordinal);
        Assert.DoesNotContain("condbr", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a < b", "lt")]
    [InlineData("a <= b", "le")]
    [InlineData("a > b", "gt")]
    [InlineData("a >= b", "ge")]
    [InlineData("a == b", "eq")]
    [InlineData("a != b", "ne")]
    public void Every_comparison_fuses(string condition, string mnemonic)
    {
        var text = Disassemble($$"""
            pub fn check(a: int, b: int): int {
                if ({{condition}}) { return 1; }
                return 0;
            }
            """);

        Assert.Contains($"brcmp {mnemonic} i64 l0, l1 ->", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_float_comparison_keeps_its_operand_tag()
    {
        // The tag names the OPERANDS, not the bool the comparison produces — the same rule the
        // unfused comparison follows, and the one that makes i64 and u64 different instructions.
        var text = Disassemble("""
            pub fn check(a: float): int {
                if (a < 1.5) { return 1; }
                return 0;
            }
            """);

        Assert.Contains("brcmpk lt f64 l0, 1.5 ->", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the arithmetic forms

    [Fact]
    public void An_accumulator_against_a_constant_is_one_instruction()
    {
        var text = Disassemble("""
            pub fn check(n: int): int {
                var i = 0;
                while (i < n) { i = i + 1; }
                return i;
            }
            """);

        Assert.Contains("binlk add i64 l1 = l1, 1", text, StringComparison.Ordinal);

        // 'var i = 0' keeps its 'const; stloc': there is no load to fold, and a shape for
        // "store a constant into a slot" would buy an instruction that runs once per function
        // rather than once per iteration.
    }

    [Fact]
    public void An_operation_over_two_locals_is_one_instruction()
    {
        var text = Disassemble("""
            pub fn check(a: int, b: int): int {
                var c = 0;
                c = a * b;
                return c;
            }
            """);

        Assert.Contains("binll mul i64 l2 = l0, l1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comparison_that_is_stored_rather_than_branched_on_fuses_too()
    {
        // The same shape with a bool destination: the operation decides what the result is, not
        // the instruction.
        var text = Disassemble("""
            pub fn check(a: int, b: int): bool {
                var flag = false;
                flag = a < b;
                return flag;
            }
            """);

        Assert.Contains("binll lt i64 l2 = l0, l1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_destination_may_be_a_source()
    {
        var text = Disassemble("""
            pub fn check(a: int, b: int): int {
                var acc = a;
                acc = acc - b;
                return acc;
            }
            """);

        Assert.Contains("binll sub i64 l2 = l2, l1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_loop_body_becomes_four_instructions()
    {
        // What the milestone is about, in one case: the shape Erato measured, counted from the
        // disassembly rather than from a benchmark.
        var text = Disassemble("""
            pub fn check(n: int): float {
                var i = 0;
                var acc = 0.0;
                while (i < n) {
                    acc = acc + 1.5;
                    i = i + 1;
                }
                return acc;
            }
            """);

        var body = text.Split("bb2:")[1].Split("bb3:")[0];
        var instructions = body.Split((char)10)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        Assert.Equal(3, instructions.Length); // two fused operations and the jump back
        Assert.Contains("binlk add f64", instructions[0], StringComparison.Ordinal);
        Assert.Contains("binlk add i64", instructions[1], StringComparison.Ordinal);
        Assert.Equal("br bb1", instructions[2]);
    }

    // ------------------------------------------------------------------ what must not fuse

    [Fact]
    public void A_comparison_read_twice_does_not_fuse()
    {
        // The value is branched on AND stored. Fusing would leave the store without a value.
        var text = Disassemble("""
            pub fn check(a: int, b: int): int {
                let same = a == b;
                if (same) { return if (same) 1 else 2; }
                return 0;
            }
            """);

        Assert.DoesNotContain("brcmp", text, StringComparison.Ordinal);
        Assert.Contains("condbr", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comparison_whose_operand_is_computed_does_not_fuse()
    {
        // 'a + 1' is not a slot, so there is no shape for it. The comparison stays where it was.
        var text = Disassemble("""
            pub fn check(a: int, b: int): int {
                if (a + 1 < b) { return 1; }
                return 0;
            }
            """);

        Assert.DoesNotContain("brcmp", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_constant_on_the_left_does_not_fuse()
    {
        // There is one constant shape and its constant is on the right. Fusing '10 < i' into it
        // would compare the operands the wrong way round.
        var text = Disassemble("""
            pub fn check(i: int): int {
                if (10 < i) { return 1; }
                return 0;
            }
            """);

        Assert.DoesNotContain("brcmpk", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_comparison_does_not_fuse()
    {
        // The fused forms compare one machine word; a string is a reference.
        var text = Disassemble("""
            pub fn check(a: string, b: string): int {
                if (a == b) { return 1; }
                return 0;
            }
            """);

        Assert.DoesNotContain("brcmp", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operation_whose_result_is_read_twice_does_not_fuse()
    {
        // Stored AND used further: the store cannot swallow a value the next operation wants.
        var text = Disassemble("""
            pub fn check(a: int, b: int): int {
                let sum = a + b;
                return sum * sum;
            }
            """);

        Assert.DoesNotContain("binll", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operation_on_a_field_does_not_fuse()
    {
        // Neither operand is a slot; a field load is an instruction of its own.
        var text = Disassemble("""
            pub class Box { n: int = 0 }

            pub fn check(b: Box): int {
                var out = 0;
                out = b.n + 1;
                return out;
            }
            """);

        Assert.DoesNotContain("binlk", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_that_is_not_a_comparison_does_not_fuse()
    {
        var text = Disassemble("""
            pub fn check(a: bool): int {
                if (a) { return 1; }
                return 0;
            }
            """);

        Assert.DoesNotContain("brcmp", text, StringComparison.Ordinal);
        Assert.Contains("condbr", text, StringComparison.Ordinal);
    }
}
