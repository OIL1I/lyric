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
/// `std.string` as a proper type.
///
/// <para>THE ONE RULE EVERYTHING FOLLOWS FROM: the specification says "`char` is one Unicode code
/// point". Length, positions and iteration therefore count CODE POINTS; otherwise the length would
/// count something other than the iteration yields, and the type system would contradict itself. C#,
/// Java and JavaScript have exactly that contradiction: there a `char` is a UTF-16 unit, and the length
/// of an emoji is 2.</para>
///
/// <para>THE MOST EXPENSIVE TEST IN THE FILE IS THEREFORE THE ONE WITH THE EMOJI. All the others stay
/// green even if the implementation secretly counts UTF-16 units — ASCII makes no difference. Only a
/// character outside the BMP separates the two models.</para>
///
/// <para>Since 2.0 the API is METHODS ONLY — the deprecated free forms went with the cut — so every
/// fixture imports the module under an alias (`import std.string as strings;`), the idiom that makes
/// the extensions visible without binding a single free name.</para>
/// </summary>
public class StringTests
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

    private static long Eval(string body) =>
        Run($"import std.string as strings;\nfn main(): int {{ {body} }}").Exit;

    // ------------------------------------------------------------------ Codepoints

    [Fact]
    public void Length_counts_codepoints_not_utf16_units() =>
        // The core test. "a😀b" is three code points and four UTF-16 units; C# says 4 here.
        // Without this case every other promise of this file would be compatible with both models.
        Assert.Equal(3, Eval("return \"a\\u{1F600}b\".length();"));

    [Fact]
    public void An_astral_codepoint_is_one_character() =>
        // The complement to the length: the position is right too. Were charAt counting UTF-16 units, half
        // a surrogate pair would come out — a character without a partner that becomes U+FFFD on output.
        // Measured through the output, because 'char as int' is not an allowed cast in Lyric.
        Assert.Equal("\U0001F600\n", Run("""
            import std.string as strings;
            import std.io.console { println };
            fn main(): int {
                let s = "a\u{1F600}b";
                println(f"{s.charAt(1)}");
                return 0;
            }
            """).Out);

    [Fact]
    public void Iteration_visits_codepoints() =>
        Assert.Equal(3, Eval("""
            var n = 0;
            for (c in "a\u{1F600}b") { n = n + 1; }
            return n;
            """));

    [Fact]
    public void Substring_cuts_at_codepoint_boundaries() =>
        // Cuts through the middle of a surrogate pair would yield a broken string here; measured through
        // the length of the result, because that compares stably.
        Assert.Equal(2, Eval("return \"a\\u{1F600}b\".substring(1, 2).length();"));

    [Fact]
    public void IndexOf_returns_a_codepoint_position() =>
        // The return value has to serve as an argument for charAt and substring; with a position in UTF-16
        // units or bytes it would not.
        Assert.Equal(2, Eval("return \"a\\u{1F600}b\".indexOf(\"b\");"));

    // ------------------------------------------------------------------ the usual cases

    [Fact]
    public void Basic_queries_work() =>
        Assert.Equal(10, Eval("return \"Hallo Welt\".length();"));

    [Fact]
    public void Substring_takes_a_start_and_a_count() =>
        Assert.Equal(0, Run("""
            import std.string as strings;
            import std.io.console { println };
            fn main(): int { println("Hallo Welt".substring(6, 4)); return 0; }
            """).Exit);

    [Fact]
    public void Substring_produces_the_expected_text() =>
        Assert.Equal("Welt\n", Run("""
            import std.string as strings;
            import std.io.console { println };
            fn main(): int { println("Hallo Welt".substring(6, 4)); return 0; }
            """).Out);

    [Fact]
    public void IndexOf_reports_minus_one_when_absent() =>
        Assert.Equal(-1, Eval("return \"abc\".indexOf(\"z\");"));

    [Fact]
    public void Split_returns_the_parts() =>
        Assert.Equal(3, Eval("return \"a,b,c\".split(\",\").length;"));

    [Fact]
    public void Split_keeps_the_parts_in_order() =>
        Assert.Equal("b\n", Run("""
            import std.string as strings;
            import std.io.console { println };
            fn main(): int { println("a,b,c".split(",")[1]); return 0; }
            """).Out);

    [Fact]
    public void Case_conversion_is_ordinal_not_cultural() =>
        // Ordinal means the same program yields the same on every machine. Under a Turkish locale a
        // culture-dependent conversion would turn an 'i' into an 'İ' — the classic that breaks software on
        // some machines only.
        Assert.Equal("TITLE\n", Run("""
            import std.string as strings;
            import std.io.console { println };
            fn main(): int { println("title".toUpper()); return 0; }
            """).Out);

    [Fact]
    public void Trim_removes_surrounding_whitespace() =>
        Assert.Equal("x\n", Run("""
            import std.string as strings;
            import std.io.console { println };
            fn main(): int { println("   x   ".trim()); return 0; }
            """).Out);

    [Fact]
    public void Predicates_answer_both_ways()
    {
        Assert.Equal(1, Eval("if (\"abc\".startsWith(\"ab\")) { return 1; } return 0;"));
        Assert.Equal(0, Eval("if (\"abc\".startsWith(\"bc\")) { return 1; } return 0;"));
        Assert.Equal(1, Eval("if (\"abc\".endsWith(\"bc\")) { return 1; } return 0;"));
        Assert.Equal(0, Eval("if (\"abc\".contains(\"z\")) { return 1; } return 0;"));
    }

    // ------------------------------------------------------------------ what does NOT work

    [Fact]
    public void A_string_cannot_be_indexed()
    {
        // Deliberately no gap in the language but a decision: a code point position costs O(n), so
        // 'for (i…) s[i]' would be quadratic without the loop showing it. Rust forbids indexing for the
        // same reason.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "fn main(): int { let s = \"abc\"; let c = s[0]; return 0; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        var reported = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0007");

        // The message has to name the way out. One saying only "not indexable" would let the user believe
        // a feature is missing rather than that it was a decision.
        Assert.Contains("charAt", reported.Message);
        Assert.Contains("for (c in s)", reported.Message);
    }

    [Fact]
    public void An_array_can_still_be_indexed() =>
        // The counter-check: the fix must not have taken array indexing along with it.
        Assert.Equal(2, Run("fn main(): int { let xs = [1, 2, 3]; return xs[1]; }").Exit);
}
