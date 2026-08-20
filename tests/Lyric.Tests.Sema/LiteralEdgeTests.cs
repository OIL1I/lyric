using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// The literal edges the 2.0.1 audit measured (§3.1 of the specification): "fits" is exact,
/// the default type carries a range, and the two initializers that fix no type report
/// instead of crashing the lowering.
/// </summary>
public class LiteralEdgeTests
{
    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    [Fact]
    public void An_integer_literal_meets_a_float_only_exactly()
    {
        // 2^53+1 and 2^24+1 are the first integers their float widths cannot hold. Before the
        // wave both adapted with a silent rounding.
        var de = Check(
            "fn main(): int {\n"
            + "    let g: float = 9007199254740993;\n"
            + "    let h: float32 = 16777217;\n"
            + "    let ok: float = 9007199254740992;\n"
            + "    let ok32: float32 = 16777216;\n"
            + "    let _ = g + ok;\n    let _ = h + ok32;\n    return 0;\n}\n");
        Assert.Equal(2, de.Diagnostics.Count(d => d.Code == "LYR-SEM0001"));
    }

    [Fact]
    public void An_oversized_literal_in_an_int_context_is_an_error_not_a_reinterpretation()
    {
        // int64.max+1 held its BITS before the wave: the binding read -9223372036854775808.
        var de = Check("fn main(): int {\n    let x = 9223372036854775808;\n    let _ = x;\n    return 0;\n}\n");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001" && d.Message.Contains("does not fit 'int'"));
    }

    [Fact]
    public void The_same_magnitude_adapts_to_uint_positions()
    {
        // The counter-check: the range gate must not take the legal adaptations with it —
        // annotated uint bindings and uint operands hold the same magnitude.
        var de = Check(
            "fn main(): int {\n"
            + "    let u: uint = 9223372036854775808;\n"
            + "    let zero: uint = 0;\n"
            + "    let masked = zero | 18446744073709551615;\n"
            + "    let _ = u + masked;\n    return 0;\n}\n");
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void Null_and_empty_array_report_instead_of_crashing()
    {
        // 'let x = null;' and 'let xs = [];' CRASHED the compiler before the wave — the null
        // type and the empty array's error element flowed silently into the lowering.
        var de = Check("fn main(): int {\n    let x = null;\n    let xs = [];\n    return 0;\n}\n");
        Assert.Equal(2, de.Diagnostics.Count(d => d.Code == "LYR-SEM0010"));
    }

    [Fact]
    public void A_throws_clause_on_main_is_refused()
    {
        // The entry point declares nothing (§9.2) — the bare form is enough to trip the gate.
        var de = Check("fn main(): int throws {\n    return 0;\n}\n");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0021");
    }
}
