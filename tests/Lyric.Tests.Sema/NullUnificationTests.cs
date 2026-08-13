using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// A <c>null</c> branch makes the other one optional: <c>if (c) 5 else null</c> is <c>?int</c>.
///
/// <para>While building <c>std.option</c>, <c>if (n &gt; 0) n * 10 else null</c> was a
/// <c>LYR-SEM0016</c>, while the same function with two <c>return</c> statements next to it worked. The
/// widening rule <c>T</c> to <c>?T</c> applied only where a target type already stood — an assignment, a
/// parameter, a return; in an arm unification the result type only arises there.</para>
///
/// <para>WHY <c>match</c> IS CHECKED HERE TOO: only the <c>if</c> case was reported. Re-measuring the
/// fix showed that <c>match</c> has a SECOND unification which did not know the case either — the same
/// question at two places with two answers. The test holds both side by side, so the next rule does not
/// land at one of them again.</para>
/// </summary>
public class NullUnificationTests
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

    private static void Compiles(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void Rejects(string source, string code) =>
        Assert.Contains(Check(source).Diagnostics, d => d.Code == code);

    // ------------------------------------------------------------------ if-Ausdruck

    [Fact]
    public void An_if_expression_with_a_null_branch_is_optional() =>
        Compiles("""
            fn f(n: int): ?int {
                return if (n > 0) n * 10 else null;
            }
            """);

    /// <summary>The other order. A fix knowing only one side would stay green with only one of the two
    /// tests, and by accident, depending on which side is checked.</summary>
    [Fact]
    public void The_null_branch_may_come_first() =>
        Compiles("""
            fn f(n: int): ?int {
                return if (n > 0) null else n * 10;
            }
            """);

    /// <summary><c>?T</c> against <c>null</c> stays <c>?T</c>: optionals do not nest, and a <c>??T</c>
    /// would not exist in Lyric anyway.</summary>
    [Fact]
    public void An_already_optional_branch_does_not_nest() =>
        Compiles("""
            fn f(o: ?int, c: bool): ?int {
                return if (c) o else null;
            }
            """);

    /// <summary>
    /// The counter-check. Without it the test would stay green even if <c>Unify</c> accepted EVERY
    /// Kombination durchwinkte.
    /// </summary>
    [Fact]
    public void Two_unrelated_branch_types_are_still_an_error() =>
        Rejects("""
            fn f(c: bool): int {
                return if (c) 1 else "eins";
            }
            """, "LYR-SEM0016");

    // ------------------------------------------------------------------ match-Ausdruck

    [Fact]
    public void A_match_arm_may_be_null() =>
        Compiles("""
            fn f(n: int): ?int {
                return match (n) {
                    0 => null,
                    _ => 99,
                };
            }
            """);

    [Fact]
    public void Two_unrelated_match_arms_are_still_an_error() =>
        Rejects("""
            fn f(n: int): int {
                return match (n) {
                    0 => "null",
                    _ => 99,
                };
            }
            """, "LYR-SEM0016");
}
