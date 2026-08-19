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
/// A declared function as a VALUE — <c>map(o, double)</c> rather than
/// <c>map(o, (n: int) =&gt; double(n))</c> — and a lambda in an f-string interpolation.
///
/// <para>Both gaps were found while building <c>std.option</c>, and both hit the same place: every
/// higher-order function in the stdlib. One forced the lambda detour, the other made exactly that detour
/// a syntax error at the most common place to write it.</para>
///
/// <para>THE FUNCTION AS A VALUE NEEDED NEITHER AN INSTRUCTION NOR AN OPCODE. <c>MakeClosure</c> takes
/// its environment optionally — the common case <c>(x) =&gt; x &gt; 0</c> captures nothing — and the VM
/// decides from the <c>HasEnvironment</c> bit whether slot 0 is occupied. A named function is a closure
/// without an environment, nothing more.</para>
/// </summary>
public class FunctionValueTests
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

    // ------------------------------------------------------------------ a function as a value

    [Fact]
    public void A_declared_function_can_be_passed_where_a_function_type_is_expected() =>
        Assert.Equal(16, Run("""
            import std.option { map };
            fn verdoppeln(n: int): int { return n * 2; }

            fn main(): int {
                let o: ?int = 8;
                return map(o, verdoppeln) ?? -1;
            }
            """).Exit);

    [Fact]
    public void A_declared_function_can_be_bound_to_a_local_of_function_type() =>
        Assert.Equal(42, Run("""
            fn verdoppeln(n: int): int { return n * 2; }

            fn main(): int {
                let f: fn(int) -> int = verdoppeln;
                return f(21);
            }
            """).Exit);

    /// <summary>
    /// The case showing that THIS function is really called rather than just any: two candidates with the
    /// same signature. With only one function the test would stay green if the <c>FunctionId</c> were
    /// always the same.
    /// </summary>
    [Fact]
    public void The_reference_names_which_function_runs() =>
        Assert.Equal("2\n30\n", Run("""
            import std.io.console { println };
            import std.option { map };

            fn verdoppeln(n: int): int { return n * 2; }
            fn verdreissigfachen(n: int): int { return n * 30; }

            fn zeige(f: fn(int) -> int) {
                println(f"{map(1, f) ?? -1}");
            }

            fn main(): int {
                zeige(verdoppeln);
                zeige(verdreissigfachen);
                return 0;
            }
            """).Out);

    /// <summary>
    /// A function referenced this way must not fall victim to the reachability analysis. It is never
    /// CALLED — it is only passed on, and the call stands there as a <c>callind</c> that does not know its
    /// target name.
    ///
    /// <para><c>Reachability</c> already treats <c>MakeClosure</c> as a root; the test holds that this
    /// applies to this new producer of <c>MakeClosure</c> too. Without it the fault would be a runtime
    /// crash at the user rather than a red test.</para>
    /// </summary>
    [Fact]
    public void A_function_used_only_as_a_value_survives_reachability_pruning() =>
        Assert.Equal(7, Run("""
            fn nurAlsWert(n: int): int { return n + 1; }

            fn anwenden(f: fn(int) -> int, x: int): int { return f(x); }

            fn main(): int {
                return anwenden(nurAlsWert, 6);
            }
            """).Exit);

    /// <summary>
    /// A generic function as a value is REJECTED, with a message that names the way out. The type
    /// arguments would have no call site to come from; silently taking some instance would be the
    /// dangerous answer.
    /// </summary>
    [Fact]
    public void A_generic_function_as_a_value_is_rejected_with_a_reason()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", """
            fn identisch<T>(x: T): T { return x; }

            fn main(): int {
                let f: fn(int) -> int = identisch;
                return f(1);
            }
            """);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        ModuleLowerer.Lower(comp, binding, types, de, verify: true);

        var diagnostic = Assert.Single(de.Diagnostics, d => d.Code == "LYR-IR0001");
        Assert.Contains("generic function", diagnostic.Message);
        Assert.Contains("lambda", diagnostic.Message);
    }

    // ------------------------------------------------------------------ Lambda im f-String

    /// <summary>
    /// <c>f"{map(o, (n: int) =&gt; n * 2)}"</c> was a syntax error: the <c>:</c> of the parameter
    /// annotation was read as the format spec separator. The lexer counted braces but not parentheses,
    /// and a lambda brings exactly those.
    /// </summary>
    [Fact]
    public void A_lambda_inside_an_interpolation_parses() =>
        Assert.Equal("16\n", Run("""
            import std.io.console { println };
            import std.option { map };

            fn main(): int {
                let o: ?int = 8;
                println(f"{map(o, (n: int) => n * 2) ?? -1}");
                return 0;
            }
            """).Out);

    /// <summary>
    /// The counter-check, and the more important one: a format spec at the top level has to stay one. A
    /// fix that no longer reads the <c>:</c> as a separator at all would be green with the test above alone
    /// and would have shut down every format spec in the language.
    /// </summary>
    [Fact]
    public void A_format_spec_at_the_top_level_still_separates() =>
        Assert.Equal("3.14\n2.00\n", Run("""
            import std.io.console { println };

            fn main(): int {
                let xs = [1, 2, 3];
                println(f"{3.14159:N2}");
                println(f"{xs[1]:N2}");
                return 0;
            }
            """).Out);

    [Fact]
    public void Interpolation_braces_escape_as_the_grammar_promises() =>
        // '{{'/'}}' fold to one literal brace, a lone '}' is text — the JSON case is the one
        // that motivated the escape. Promised by the grammar since 1.0, honored since v1.16.
        Assert.Equal("{n} is 7\njson: {\"k\": 7}\n}\n", Run("""
            import std.io.console { println };

            fn main(): int {
                let n = 7;
                println(f"{{n}} is {n}");
                println(f"json: {{\"k\": {n}}}");
                println(f"}");
                return 0;
            }
            """).Out);

    [Fact]
    public void A_block_lambda_without_annotation_infers_and_runs() =>
        // v1.13: no return type, no context — the type comes from the body's returns, including
        // through an open generic (U binds to what the block returns).
        Assert.Equal(48L, Run("""
            fn apply<T, U>(x: T, f: fn(T) -> U): U {
                return f(x);
            }

            fn main(): int {
                let double = (x: int) => {
                    if (x > 100) {
                        return x;
                    }
                    return x * 2;
                };
                let viaGeneric = apply(5, (n: int) => {
                    return n + 1;
                });
                return double(21) + viaGeneric;
            }
            """).Exit);
}
