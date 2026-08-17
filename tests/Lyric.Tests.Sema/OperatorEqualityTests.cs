using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// What <c>==</c> and <c>!=</c> accept, and above all what they still reject.
///
/// <para>The operator desugars through <c>Equatable</c> — CONFORMANCE, not the method alone. The
/// pinned decision is nominal typing: a type with an <c>equals</c> method that never declared the
/// conformance stays rejected, because otherwise any method of that name silently becomes an
/// operator and the contract has no place to be named.</para>
/// </summary>
public class OperatorEqualityTests
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

    private static void AssertRejects(string source, string messagePart)
    {
        var de = Check(source);
        var found = de.Diagnostics.FirstOrDefault(d => d.Code == "LYR-SEM0059");
        Assert.NotNull(found);
        Assert.Contains(messagePart, found.Message);
    }

    private const string ConformingPoint = """
        import std.core { Equatable };

        struct Point :: [Equatable<Point>] {
            x: int,
            fn equals(other: Point): bool { return this.x == other.x; }
        }

        """;

    // ------------------------------------------------------------------ accepted

    [Fact]
    public void A_conforming_struct_compares()
    {
        AssertClean(ConformingPoint + """
            fn main(): int {
                let a = Point { x = 1 };
                let b = Point { x = 2 };
                return if (a == b || a != b) 1 else 0;
            }
            """);
    }

    [Fact]
    public void A_constrained_type_parameter_compares()
    {
        AssertClean("""
            import std.core { Equatable };

            fn same<T :: [Equatable<T>]>(a: T, b: T): bool { return a == b; }

            fn main(): int { return if (same(1, 2)) 1 else 0; }
            """);
    }

    // ------------------------------------------------------------------ still rejected, and why

    [Fact]
    public void The_method_alone_is_not_enough()
    {
        // The design pin. 'equals' exists and is callable; the conformance is missing, so the
        // OPERATOR stays undefined. Nominal, not structural.
        AssertRejects("""
            struct Point {
                x: int,
                fn equals(other: Point): bool { return this.x == other.x; }
            }

            fn main(): int {
                let a = Point { x = 1 };
                return if (a == a) 1 else 0;
            }
            """,
            "Equatable");
    }

    [Fact]
    public void The_rejection_names_the_conformance_to_add()
    {
        AssertRejects("""
            struct Point { x: int, }

            fn main(): int {
                let a = Point { x = 1 };
                return if (a == a) 1 else 0;
            }
            """,
            ":: [Equatable<Point>]");
    }

    [Fact]
    public void An_unconstrained_type_parameter_is_rejected()
    {
        AssertRejects("""
            fn same<T>(a: T, b: T): bool { return a == b; }

            fn main(): int { return if (same(1, 2)) 1 else 0; }
            """,
            "Equatable<T>");
    }

    [Fact]
    public void Optionals_still_compare_only_against_null()
    {
        AssertRejects("""
            fn main(): int {
                let a: ?int = 1;
                let b: ?int = 2;
                return if (a == b) 1 else 0;
            }
            """,
            "narrow");
    }

    [Fact]
    public void An_interface_value_does_not_compare()
    {
        // A fat pointer has no conformance list of its own; identity of interface values is not a
        // question this language answers.
        AssertRejects("""
            import std.core { Equatable };

            interface Named { fn name(): string; }

            class A :: [Named] {
                fn name(): string { return "a"; }
            }

            fn main(): int {
                let x: Named = A { };
                let y: Named = A { };
                return if (x == y) 1 else 0;
            }
            """,
            "'Named'");
    }

    [Fact]
    public void Mixed_user_types_stay_a_type_error()
    {
        // Two different types were never comparable, and the desugar must not change that: the
        // homogeneity check runs before Equatable is even considered.
        var de = Check("""
            import std.core { Equatable };

            struct A :: [Equatable<A>] { x: int, fn equals(other: A): bool { return true; } }
            struct B :: [Equatable<B>] { x: int, fn equals(other: B): bool { return true; } }

            fn main(): int {
                let a = A { x = 1 };
                let b = B { x = 1 };
                return if (a == b) 1 else 0;
            }
            """);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0003");
    }

    [Fact]
    public void Without_a_standard_library_the_answer_is_a_diagnostic()
    {
        // No std.core means no Equatable to desugar through. The compile must degrade into the
        // ordinary rejection, not into a crash or a silent BinOp the verifier then refuses.
        var de = Check("""
            struct Point { x: int, }

            fn main(): int {
                let a = Point { x = 1 };
                return if (a == a) 1 else 0;
            }
            """,
            withStdlib: false);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0059");
    }

    [Fact]
    public void Null_comparisons_are_untouched()
    {
        // 'o == null' is flow narrowing, not Equatable — it must not start requiring a conformance.
        AssertClean("""
            fn main(): int {
                let o: ?int = 5;
                if (o != null) { return o; }
                return 0;
            }
            """);
    }
}
