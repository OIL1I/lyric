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
/// Format specs in f-strings — `std.fmt`.
///
/// <para>`f"{avg:N2}"` becomes `std.fmt.formatFloat(avg, "N2")`. The spec language is .NET's, as the
/// specification requires, and it is passed through unchanged: Lyric invents no notation of its own
/// beside it.</para>
///
/// <para>TWO PROMISES MATTER MORE THAN THE FORMATS THEMSELVES: that without a spec the `fromXxx`
/// converters still run (a format call that only rebuilds the default would be a second route to the
/// same result), and that the output is INVARIANT. A number that looks different under a German locale
/// than under an English one is no formatting detail but a program that behaves differently per
/// machine.</para>
/// </summary>
public class FormatTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Out(string body)
    {
        var source = "import std.io.console { println };\nfn main(): int { " + body + " return 0; }";
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
        Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(output, TextWriter.Null));
        return output.ToString().ReplaceLineEndings("\n");
    }

    // ------------------------------------------------------------------ Zahlen

    [Fact]
    public void N_rounds_to_the_given_digits() =>
        // The case 'examples/stats.lyr' has been waiting for.
        Assert.Equal("12.35\n", Out("let avg = 12.3456; println(f\"{avg:N2}\");"));

    [Fact]
    public void F_is_fixed_point() =>
        Assert.Equal("12.346\n", Out("let avg = 12.3456; println(f\"{avg:F3}\");"));

    [Fact]
    public void D_pads_an_integer_with_zeroes() =>
        Assert.Equal("00042\n", Out("let n = 42; println(f\"{n:D5}\");"));

    [Fact]
    public void X_is_hexadecimal() =>
        Assert.Equal("2A\n", Out("let n = 42; println(f\"{n:X}\");"));

    [Fact]
    public void The_thousands_separator_is_invariant() =>
        // Invariant means a dot as the decimal separator and a comma as the thousands separator,
        // everywhere. Under a German locale it would be the other way round, and the same program would
        // print two different numbers on two machines.
        Assert.Equal("1,234.57\n", Out("let x = 1234.567; println(f\"{x:N2}\");"));

    // ------------------------------------------------------------------ without a spec

    [Fact]
    public void Without_a_spec_the_plain_converter_still_runs() =>
        // The most important promise of the file. Were std.fmt to run here too, there would be two routes
        // to the same result — exactly what the project rules forbid.
        Assert.Equal("42\n", Out("let n = 42; println(f\"{n}\");"));

    [Fact]
    public void A_program_without_format_specs_does_not_pull_in_std_fmt()
    {
        // The same rule as for the Display extensions: only what is used stands in the bytecode. 'std.fmt'
        // is a well-known module and is ALWAYS loaded — being loaded and landing in the module are two
        // different things.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "fn main(): int { let n = 42; return n; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var ir = ModuleLowerer.Lower(comp, binding, Semantics.Analyze(comp, binding, de), de,
            verify: true);

        Assert.NotNull(ir);
        Assert.DoesNotContain(ir!.Imports, i => i.Name.StartsWith("std.fmt", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ other types

    [Fact]
    public void A_string_spec_is_a_width() =>
        // .NET knows no standard formats for strings. Rather than inventing a second notation, the spec is
        // simply a width here: positive pads on the right, negative on the left.
        Assert.Equal("ab   |\n", Out("let s = \"ab\"; println(f\"{s:5}|\");"));

    [Fact]
    public void A_negative_width_pads_on_the_left() =>
        Assert.Equal("   ab|\n", Out("let s = \"ab\"; println(f\"{s:-5}|\");"));

    [Fact]
    public void Bool_and_char_take_a_width_too() =>
        Assert.Equal("true |x    |\n", Out("let b = true; let c = 'x'; println(f\"{b:5}|{c:5}|\");"));

    // ------------------------------------------------------------------ what may go wrong

    [Fact]
    public void An_invalid_spec_panics_with_the_spec_in_the_message()
    {
        // No silent fallback to the default representation: the spec stands as a literal in the source and
        // does not depend on the input. A '{x:Q9}' is written wrongly rather than unlucky, and a fallback
        // would carry the typo into the output.
        var panic = Assert.Throws<LyricPanic>(() =>
            Out("let n = 42; println(f\"{n:Q9}\");"));

        Assert.Contains("Q9", panic.Message);
    }

    // ------------------------------------------- narrow scalars in an f-string

    /// <summary>
    /// Every integer and floating-point type can be interpolated, not only <c>int</c> and <c>float</c>.
    ///
    /// <para>The compiler used to crash. The converters are called <c>fromInt</c> and <c>fromFloat</c> —
    /// singular, because Lyric has no overloading — and take the widest type. The lowering passed the
    /// value through unwidened, and the IR verifier threw "arg 0 is i8, expected i64" as an
    /// InternalCompilationException with a stack trace.</para>
    ///
    /// <para>EVERY type except <c>int</c> and <c>float</c> was affected. Found only because <c>char</c>
    /// happened to lie next to it. Hence a theory over all widths here rather than one example.</para>
    /// </summary>
    [Theory]
    [InlineData("int8", "-5", "-5")]
    [InlineData("int8", "-128", "-128")]
    [InlineData("int8", "127", "127")]
    [InlineData("int16", "-32768", "-32768")]
    [InlineData("int32", "70000", "70000")]
    [InlineData("uint8", "255", "255")]
    [InlineData("uint16", "300", "300")]
    [InlineData("uint32", "4000000000", "4000000000")]
    [InlineData("float32", "1.5", "1.5")]
    public void A_narrow_scalar_interpolates(string type, string literal, string expected) =>
        Assert.Equal(expected, Out($"let x: {type} = {literal}; println(f\"{{x}}\");").Trim());

    // ------------------------------------------- the float rendering itself

    /// <summary>
    /// What <c>fromFloat</c> writes, spelled out — spec §11.6. It backs the f-string lowering, so
    /// this IS program output, and the conformance suite compares it byte for byte.
    ///
    /// <para>The shortest form that reads back as the same value; scientific notation below
    /// <c>1e-4</c> and from <c>1e17</c> upwards; a LOWERCASE exponent marker with a sign, as every
    /// other language writes it. .NET's round-trip format is the only one that shouts
    /// <c>1E+21</c>, and nothing pinned it here until this table.</para>
    /// </summary>
    [Theory]
    [InlineData("1.0", "1")]                      // an integral value keeps no '.0'
    [InlineData("1.5", "1.5")]
    [InlineData("-0.0", "-0")]                    // negative zero keeps its sign
    [InlineData("0.1 + 0.2", "0.30000000000000004")]
    [InlineData("0.0001", "0.0001")]              // the last plain one going down
    [InlineData("0.00001", "1e-05")]              // the first scientific one
    [InlineData("1.0e16", "10000000000000000")]   // the last plain one going up
    [InlineData("1.0e17", "1e+17")]               // the first scientific one
    [InlineData("1.0e21", "1e+21")]
    [InlineData("1.0 / 0.0", "Infinity")]
    [InlineData("-1.0 / 0.0", "-Infinity")]
    [InlineData("0.0 / 0.0", "NaN")]
    public void A_float_renders_shortest_with_a_lowercase_exponent(string expression, string expected) =>
        Assert.Equal(expected, Out($"println(f\"{{{expression}}}\");").Trim());

    [Fact]
    public void A_narrow_scalar_takes_a_format_spec() =>
        // The second route: with a spec it goes through std.fmt rather than std.string. Two paths, the
        // same fault; without this test only one of them would be secured.
        Assert.Equal("-005", Out("let x: int8 = -5; println(f\"{x:D3}\");").Trim());
}
