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
/// <c>a op= b</c> against <c>a = a op b</c>, for every target and every type where <c>op</c> is not a
/// machine instruction.
///
/// <para>THE RULE THIS FILE MEASURES: the two spellings are the same operation and must produce the
/// same instruction. They did not. <c>+</c> on a string lowers to <c>std.string.concat</c> and on an
/// array to <c>arrcat</c>, but three of the five compound-assignment paths emitted a bare
/// <c>BinOp</c> instead — so <c>s += "x"</c> became <c>add string</c>. The IR verifier rejects that
/// and runs in debug builds only; the bytecode reader did not check the tag at all. In a release
/// toolchain the module was therefore valid everywhere and the interpreter added the two references
/// as integers: every <c>s += …</c> silently produced the EMPTY STRING, and the array form reached
/// the host as a null-reference exception rather than a panic.</para>
///
/// <para>The tests deliberately measure the RESULT rather than the emitted instruction. What went
/// wrong was observable in a running program, so that is the altitude a regression has to be caught
/// at; a test against the instruction would go green again the moment a fourth path is added and
/// forgotten.</para>
/// </summary>
public class CompoundAssignTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Run(string source)
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
        Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(output, TextWriter.Null));
        return output.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>Prints the expression, so an empty result is distinguishable from a missing line.</summary>
    private static string Eval(string body) => Run($$"""
        import std.io.console { println };
        {{body}}
        """);

    // ------------------------------------------------------------------ string

    [Theory]
    // A plain local. The one the byte viewer hit: 'var line = ""; line += …' in a loop printed 604
    // blank lines because every '+=' overwrote the local with the empty string.
    [InlineData("fn main(): int { var s = \"A\"; s += \"B\"; println(s); return 0; }", "AB\n")]
    // A field. This path DID guard, with a 'LYR-IR0001' saying the compiler cannot do it yet — an
    // honest report of the same missing routing, and now unnecessary.
    [InlineData("class B { s: string = \"A\" }\n" +
                "fn main(): int { let b = B {}; b.s += \"B\"; println(b.s); return 0; }", "AB\n")]
    // An array element. Guarded like the field, for the same reason.
    [InlineData("fn main(): int { var xs = [\"A\"]; xs[0] += \"B\"; println(xs[0]); return 0; }", "AB\n")]
    // A captured variable: the cell path, unguarded, silently wrong.
    [InlineData("fn main(): int { var s = \"A\"; let f = () => { s += \"B\"; }; f(); println(s); return 0; }",
        "AB\n")]
    // A coroutine local: lives in the state object, again unguarded and silently wrong.
    [InlineData("fn gen(): Coroutine<string> { var s = \"A\"; s += \"B\"; yield s; }\n" +
                "fn main(): int { var c = gen(); println(f\"{resume c}\"); return 0; }", "AB\n")]
    public void A_compound_assignment_on_a_string_concatenates(string source, string expected) =>
        Assert.Equal(expected, Run("import std.io.console { println };\n" + source));

    [Fact]
    public void The_compound_form_agrees_with_the_written_out_form() =>
        // The invariant behind all of the above, stated once. Both lines have to reach the same
        // instruction; when they did not, only the second one was right.
        Assert.Equal("AB\nAB\n", Eval("""
            fn main(): int {
                var a = "A"; a += "B";
                var b = "A"; b = b + "B";
                println(a);
                println(b);
                return 0;
            }
            """));

    // ------------------------------------------------------------------ array

    [Fact]
    public void A_compound_assignment_on_an_array_concatenates() =>
        // Before the fix this was not merely wrong but a host-level crash: 'add array' produced a
        // value with no reference, and the 'arrlen' after it dereferenced it. An unhandled .NET
        // exception, not a Lyric panic — no exit code from the runner contract, no backtrace.
        Assert.Equal("3\n", Eval("""
            import std.string { fromInt };
            fn main(): int { var xs = [1, 2]; xs += [3]; println(fromInt(xs.length)); return 0; }
            """));

    // ------------------------------------------------------------------ the limit next door

    [Theory]
    [InlineData("var s = \"ab\"; s *= 3;", "string")]
    [InlineData("var xs = [1, 2]; xs *= 2;", "int[]")]
    public void Repetition_has_no_compound_form_yet(string body, string target)
    {
        // A DIFFERENT bug, in the type checker rather than in the lowering, and deliberately not
        // fixed here: 'a op= b' is typed as if b had to be assignable to a, which holds for every
        // arithmetic operator and not for the repetition overload, where the right operand is an
        // 'int' by design. 's = s * 3' compiles and works; 's *= 3' does not.
        //
        // Pinned rather than left as a note, because the lowering can now do it — the moment the
        // sema rule is corrected this test goes red and says so instead of leaving a path untested.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", $"fn main(): int {{ {body} return 0; }}");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        var reported = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0001");
        Assert.Contains($"to '{target}'", reported.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the control

    [Fact]
    public void Arithmetic_compound_assignment_is_unchanged() =>
        // The counter-check. The fix routes every compound assignment through one place; the numeric
        // case has to come out of it as the plain BinOp it always was.
        Assert.Equal("7\n", Eval("""
            import std.string { fromInt };
            fn main(): int {
                var n = 1;
                n += 2;
                n *= 3;
                n -= 2;
                n /= 1;
                println(fromInt(n));
                return 0;
            }
            """));
}
