using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// NOTHING IS SILENTLY LET THROUGH UNCHECKED.
///
/// <para>These tests protect an invariant rather than a single rule: <see cref="ErrorType"/> means
/// EXCLUSIVELY "a diagnostic has already been reported for this". Every consumer relies on it and stays
/// silent on <c>Error</c>; whoever uses the type for "I do not know what this is" turns it into a silent
/// pass.</para>
///
/// <para>That was the state: six constructs checked out completely although they are invalid, and took
/// every use along. The worst case was a typo in a module name — <c>import std.io.consle</c> — which
/// switched off the checking of every use of the import.</para>
///
/// <para>Every fixture here therefore ALSO contains a coarse follow-up error (<c>.nonsense</c>, a wrong
/// arity). Were the check to break off silently, the compiler would report nothing at all —
/// <see cref="Assert.True(bool, string)"/> on <c>HasErrors</c> is the actual statement, the code
/// comparison only the refinement.</para>
/// </summary>
public class SilentSkipTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void AssertReports(string source, string code)
    {
        var de = Check(source);
        Assert.True(de.HasErrors,
            "this source is invalid but was accepted without any diagnostic — " +
            "something was silently skipped:\n" + source);
        Assert.Contains(de.Diagnostics, d => d.Code == code);
    }

    // --- a type or module name in value position ---

    [Theory]
    // Looks like a constructor and is none: Lyric constructs through 'P { … }'. This used to yield Error,
    // and the arity, the argument types and the '.nonsense' all stayed unchecked.
    [InlineData("class P { hp: int }\nfn main(): int { return P(1, 2, 3).quatsch; }")]
    [InlineData("class P { hp: int }\nfn main(): int { let x = P; return x.quatsch; }")]
    [InlineData("import std.io.console;\nfn main(): int { let x = console; return x.quatsch; }")]
    public void A_type_or_module_in_value_position_is_reported(string source) =>
        AssertReports(source, "LYR-SEM0052");

    /// <summary>The counter-check, without which the fix would be a regression: as a member target a type
    /// or module name is legitimate, and both forms have to keep passing. That is what the obvious fix
    /// "just report it at the producer" fails on.</summary>
    [Theory]
    [InlineData("class P {\n  hp: int,\n  static fn make(): P { return P { hp = 1 }; }\n}\nfn main(): int { return P.make().hp; }")]
    [InlineData("import std.io.console;\nfn main(): int { console.println(\"hi\"); return 0; }")]
    public void A_type_or_module_as_a_member_target_stays_legal(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            "member access on a type/module name must stay legal, but got:\n" +
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    // --- Unauffindbare Module ---

    /// <summary>
    /// The most expensive case. A missing 'o' in <c>consle</c> counted as "external and opaque", a rule
    /// from before the <c>ModuleLoader</c> existed. The import stayed silent, and so did both
    /// <c>println</c> calls below, although one has the wrong argument type and the other the wrong
    /// arity.
    /// </summary>
    [Fact]
    public void A_typo_in_a_module_path_is_reported() =>
        AssertReports(
            """
            import std.io.consle { println };
            fn main(): int { println(42); println("a", "b"); return 0; }
            """,
            "LYR-RES0003");

    [Theory]
    [InlineData("import gibts.nicht;\nfn main(): int { return 0; }")]
    [InlineData("import gibts.nicht { thing };\nfn main(): int { return thing(1, 2, 3).quatsch; }")]
    [InlineData("import gibts.nicht { Thing };\nfn f(t: Thing): int { return t.quatsch; }\nfn main(): int { return 0; }")]
    public void An_unresolvable_module_is_reported(string source) =>
        AssertReports(source, "LYR-RES0003");

    /// <summary>An unknown SYMBOL in a known module was always reported. The test holds the asymmetry
    /// that existed before: symbol yes, module no.</summary>
    [Fact]
    public void An_unknown_symbol_in_a_known_module_is_reported() =>
        AssertReports("import std.io.console { printlnn };\nfn main(): int { return 0; }",
            "LYR-RES0004");

    // --- Attribute ---

    [Fact]
    public void An_attribute_is_not_an_expression() =>
        AssertReports("fn main(): int { let x = @test; return x.quatsch; }", "LYR-SEM0053");

    // --- Kontrolle ---

    /// <summary>Shows that the harness sees errors at all; otherwise every test above could be green for
    /// the wrong reason.</summary>
    [Fact]
    public void The_harness_reports_an_ordinary_type_error() =>
        AssertReports("fn main(): int { let s: string = 1; return 0; }", "LYR-SEM0001");
}
