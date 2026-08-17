using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// What the four ordering operators accept beyond numerics, and what they still reject.
///
/// <para>The same nominal rule as equality: conformance to <c>Ordered</c>, not a <c>compare</c>
/// method alone.</para>
/// </summary>
public class OperatorOrderingTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Check(string source, bool withStdlib = true)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);

        if (withStdlib)
            comp.ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de);

        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void AssertClean(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            "expected this to check clean, but got:\n"
            + string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Fact]
    public void A_string_comparison_checks_clean()
    {
        AssertClean("""
            fn main(): int { return if ("a" < "b") 1 else 0; }
            """);
    }

    [Fact]
    public void A_conforming_struct_checks_clean()
    {
        AssertClean("""
            import std.core { Ordered };

            struct M :: [Ordered<M>] {
                v: int,
                fn compare(other: M): int { return 0; }
            }

            fn main(): int {
                let a = M { v = 1 };
                return if (a <= a) 1 else 0;
            }
            """);
    }

    [Fact]
    public void The_method_alone_is_not_enough()
    {
        // Nominal, as with equality: 'compare' exists, the conformance does not, the operator
        // stays undefined.
        var de = Check("""
            struct M {
                v: int,
                fn compare(other: M): int { return 0; }
            }

            fn main(): int {
                let a = M { v = 1 };
                return if (a < a) 1 else 0;
            }
            """);

        var found = de.Diagnostics.FirstOrDefault(d => d.Code == "LYR-SEM0003");
        Assert.NotNull(found);
        Assert.Contains(":: [Ordered<M>]", found.Message);
    }

    [Fact]
    public void Bools_do_not_order()
    {
        // The stdlib gives bool Equatable and Hashable, deliberately no Ordered — 'true < false'
        // answers nothing anyone means. The operator follows the conformance, so it follows the
        // decision.
        var de = Check("""
            fn main(): int { return if (true < false) 1 else 0; }
            """);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0003");
    }

    [Fact]
    public void Mixed_types_stay_rejected()
    {
        var de = Check("""
            fn main(): int { return if ("a" < 1) 1 else 0; }
            """);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0003");
    }

    [Fact]
    public void An_unconstrained_type_parameter_is_rejected()
    {
        var de = Check("""
            fn smallest<T>(a: T, b: T): T { return if (a < b) a else b; }

            fn main(): int { return smallest(1, 2); }
            """);

        Assert.Contains(de.Diagnostics,
            d => d.Code == "LYR-SEM0003" && d.Message.Contains("Ordered<T>"));
    }

    [Fact]
    public void Without_a_standard_library_the_answer_is_a_diagnostic()
    {
        // No std.core means no Ordered — and for a string not even a compare to find. The old
        // behaviour, kept deliberately.
        var de = Check("""
            fn main(): int { return if ("a" < "b") 1 else 0; }
            """,
            withStdlib: false);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0003");
    }
}
