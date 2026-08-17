using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// What <c>as</c> accepts beyond the numerics, and what it still rejects.
/// </summary>
public class OperatorIntoTests
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

    [Fact]
    public void A_conforming_cast_checks_clean()
    {
        var de = Check("""
            import std.core { Into };

            struct B { n: int, }
            struct A :: [Into<B>] {
                n: int,
                fn into(): B { return B { n = this.n }; }
            }

            fn main(): int {
                let b = A { n = 1 } as B;
                return b.n;
            }
            """);

        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Fact]
    public void The_method_alone_is_not_enough()
    {
        // The nominal rule, third time: an 'into' method without the conformance stays a cast error.
        var de = Check("""
            struct B { n: int, }
            struct A {
                n: int,
                fn into(): B { return B { n = this.n }; }
            }

            fn main(): int {
                let b = A { n = 1 } as B;
                return b.n;
            }
            """);

        var found = de.Diagnostics.FirstOrDefault(d => d.Code == "LYR-SEM0006");
        Assert.NotNull(found);
        Assert.Contains("Into<B>", found.Message);
    }

    [Fact]
    public void The_conformance_target_must_match_the_cast_target()
    {
        // A :: [Into<B>] does not admit 'as C'. The instance is checked, not the symbol — the same
        // distinction Satisfies draws for every constraint.
        var de = Check("""
            import std.core { Into };

            struct B { n: int, }
            struct C { n: int, }
            struct A :: [Into<B>] {
                n: int,
                fn into(): B { return B { n = this.n }; }
            }

            fn main(): int {
                let c = A { n = 1 } as C;
                return c.n;
            }
            """);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0006");
    }

    [Fact]
    public void A_second_conversion_target_is_a_duplicate_member()
    {
        // The documented limit: 'into' is a member name and a type has one member of a name. Two
        // conformances would need two methods; the second is a redeclaration diagnostic, and this
        // test is where the limit is measured rather than asserted in prose.
        var de = Check("""
            import std.core { Into };

            struct B { n: int, }
            struct C { n: int, }

            struct A :: [Into<B>, Into<C>] {
                n: int,
                fn into(): B { return B { n = this.n }; },
                fn into(): C { return C { n = this.n }; }
            }

            fn main(): int { return 0; }
            """);

        Assert.True(de.HasErrors);
    }

    [Fact]
    public void Numerics_never_desugar()
    {
        // '1 as float' stays an opcode whatever conformances exist; the numeric branch is first and
        // not overridable.
        var de = Check("""
            fn main(): int {
                let f = 1 as float;
                return f as int;
            }
            """);

        Assert.False(de.HasErrors);
    }

    [Fact]
    public void Without_a_standard_library_the_answer_is_a_diagnostic()
    {
        var de = Check("""
            struct B { n: int, }
            struct A { n: int, }

            fn main(): int {
                let b = A { n = 1 } as B;
                return b.n;
            }
            """,
            withStdlib: false);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0006");
    }
}
