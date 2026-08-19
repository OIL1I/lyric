using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Interface inheritance (v1.13): a parent list on an interface implies the parent's conformance
/// — for implementing types, for constraints, for throwability. The shape rules live here too:
/// at most one parent (LYR-SEM0078), only interfaces, no cycles, and no redeclaration of a chain
/// member (LYR-SEM0079).
/// </summary>
public class InterfaceInheritanceTests
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
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

    private const string Chain =
        """
        interface Named {
            fn name(): string;
        }

        interface Labeled :: [Named] {
            fn label(): string {
                return "[" + this.name() + "]";
            }
        }

        """;

    // --- implication ---

    [Fact]
    public void A_type_conforming_to_the_child_satisfies_a_parent_constraint()
    {
        var de = Check(Chain +
            """
            struct Tag :: [Labeled] {
                fn name(): string {
                    return "tag";
                }
            }

            fn describe<T :: [Named]>(x: T): string {
                return x.name();
            }

            fn main(): int {
                let _ = describe(Tag { });
                return 0;
            }
            """);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics));
    }

    [Fact]
    public void A_child_constraint_reaches_the_parents_members()
    {
        var de = Check(Chain +
            """
            fn describe<T :: [Labeled]>(x: T): string {
                return x.label() + x.name();
            }

            fn main(): int {
                return 0;
            }
            """);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics));
    }

    [Fact]
    public void A_missing_parent_member_names_the_implying_interface()
    {
        var de = Check(Chain +
            """
            struct Bad :: [Labeled] {
                x: int,
            }

            fn main(): int {
                return 0;
            }
            """);
        var error = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0020");
        Assert.Contains("'name' of interface 'Named' (implied by 'Labeled')", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_signature_mismatch_on_a_parent_member_names_the_implying_interface()
    {
        var de = Check(Chain +
            """
            struct Bad :: [Labeled] {
                fn name(): int {
                    return 1;
                }
            }

            fn main(): int {
                return 0;
            }
            """);
        var error = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0042");
        Assert.Contains("(implied by 'Labeled')", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_generic_parent_takes_the_childs_substitution()
    {
        var de = Check(
            """
            interface Eq2<T> {
                fn same(other: T): bool;
            }

            interface Ord2<T> :: [Eq2<T>] {
                fn less(other: T): bool;
            }

            struct P :: [Ord2<P>] {
                v: int,

                fn same(other: P): bool {
                    return this.v == other.v;
                }

                fn less(other: P): bool {
                    return this.v < other.v;
                }
            }

            fn eqCheck<T :: [Eq2<T>]>(a: T, b: T): bool {
                return a.same(b);
            }

            fn main(): int {
                let _ = eqCheck(P { v = 1 }, P { v = 2 });
                return 0;
            }
            """);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics));
    }

    [Fact]
    public void A_throws_clause_accepts_a_type_whose_chain_reaches_Throwable()
    {
        var de = Check(
            """
            interface AppError :: [Throwable] {
                fn code(): int;
            }

            class NetError :: [AppError] {
                fn message(): string {
                    return "down";
                }

                fn code(): int {
                    return 502;
                }
            }

            fn risky(): int throws NetError {
                throw NetError { };
            }

            fn main(): int {
                return 0;
            }
            """);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics));
    }

    [Fact]
    public void An_interface_value_does_not_convert_to_the_parents_type()
    {
        var de = Check(Chain +
            """
            fn toNamed(l: Labeled): Named {
                return l;
            }

            fn main(): int {
                return 0;
            }
            """);
        Assert.True(de.HasErrors, "a Labeled VALUE must not widen into Named — only concrete types imply");
    }

    // --- shape rules ---

    [Fact]
    public void A_second_parent_is_refused_toward_constraints()
    {
        var de = Check(
            """
            interface A {
                fn a(): int;
            }

            interface B {
                fn b(): int;
            }

            interface C :: [A, B] {
                fn c(): int;
            }

            fn main(): int {
                return 0;
            }
            """);
        var error = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0078");
        Assert.Contains("at most one parent", error.Message, StringComparison.Ordinal);
        Assert.Contains("<T :: [A, B]>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_interface_parent_is_refused()
    {
        var de = Check(
            """
            struct S {
                x: int,
            }

            interface OnStruct :: [S] {
                fn q(): int;
            }

            fn main(): int {
                return 0;
            }
            """);
        var error = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0078");
        Assert.Contains("only an interface", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_is_reported_on_every_participant_and_does_not_hang()
    {
        var de = Check(
            """
            interface A :: [B] {
                fn a(): int;
            }

            interface B :: [A] {
                fn b(): int;
            }

            fn main(): int {
                return 0;
            }
            """);
        Assert.Equal(2, de.Diagnostics.Count(d => d.Code == "LYR-SEM0078"));
    }

    [Fact]
    public void A_self_parent_is_its_own_message()
    {
        var de = Check(
            """
            interface C :: [C] {
                fn c(): int;
            }

            fn main(): int {
                return 0;
            }
            """);
        var error = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0078");
        Assert.Contains("cannot inherit itself", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Redeclaring_a_chain_member_is_refused_with_a_note()
    {
        var de = Check(Chain +
            """
            interface Renamed :: [Labeled] {
                fn name(): string;
            }

            fn main(): int {
                return 0;
            }
            """);
        var error = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0079");
        Assert.Contains("'Named'", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Notes);
    }

    [Fact]
    public void Two_instances_of_one_interface_are_still_two_conformances()
    {
        // The dedup across a conformance list keys on the INSTANCE, not the symbol: 'Mul<Vec2>'
        // beside 'Mul<float>' must reach the signature check that refuses the second — the pin
        // against a closure walk that would skip it as "already seen".
        var de = Check(
            """
            import std.core { Mul };

            struct Vec2 :: [Mul<Vec2>, Mul<float>] {
                x: float,

                fn mul(other: Vec2): Vec2 {
                    return Vec2 { x = this.x * other.x };
                }
            }

            fn main(): int {
                return 0;
            }
            """);
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0042");
    }

    [Fact]
    public void A_parent_written_out_beside_its_child_is_checked_once()
    {
        // 'Tag' misses 'name'; with Labeled AND Named declared, the closure walks Named once —
        // one error, not one per mention.
        var de = Check(Chain +
            """
            struct Tag :: [Labeled, Named] {
                x: int,
            }

            fn main(): int {
                return 0;
            }
            """);
        Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0020");
    }
}
