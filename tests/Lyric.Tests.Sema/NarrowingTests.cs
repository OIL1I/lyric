using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Flow narrowing: after a proven non-null check a <c>?T</c> is a
/// <c>T</c>.
///
/// <para>Every case stands here TWICE — once allowed, once rejected. A rule checking only the allowed
/// cases would stay green even if it narrowed everything without exception, and that would be the
/// dangerous fault, because the lowering produces an unchecked <c>optget</c> behind every
/// narrowing.</para>
/// </summary>
public class NarrowingTests
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

    private static void Narrows(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void DoesNotNarrow(string source) =>
        Assert.Contains(Check(source).Diagnostics, d => d.Code is "LYR-SEM0001" or "LYR-SEM0003");

    // ------------------------------------------------------------------ what is narrowed

    [Fact]
    public void An_if_narrows_its_then_branch() =>
        Narrows("fn main(): int { let x: ?int = 1; if (x != null) { return x; } return 0; }");

    [Fact]
    public void An_early_exit_narrows_what_follows() =>
        // 'if (x == null) { return; }' proves for the rest of the function that x has a value.
        Narrows("fn main(): int { let x: ?int = 1; if (x == null) { return 0; } return x; }");

    [Fact]
    public void A_while_narrows_its_body() =>
        // Sound although the loop may change x: the condition is re-checked before EVERY iteration, and an
        // assignment in the body drops the narrowing from there on.
        Narrows("""
            fn main(): int {
                var x: ?int = 1;
                var s = 0;
                while (x != null) { s += x; x = null; }
                return s;
            }
            """);

    [Fact]
    public void The_right_side_of_and_sees_what_the_left_proved() =>
        // Short circuit: the right side runs only when the left is true.
        Narrows("fn main(): int { let x: ?int = 1; if (x != null && x > 0) { return x; } return 0; }");

    [Fact]
    public void And_collects_facts_from_both_sides() =>
        Narrows("""
            fn main(): int {
                let a: ?int = 1;
                let b: ?int = 2;
                if (a != null && b != null) { return a + b; }
                return 0;
            }
            """);

    [Fact]
    public void Or_narrows_the_else_branch() =>
        // 'if (x == null || …) { return; }': the else branch is reached only when BOTH sides were false,
        // including the null check.
        Narrows("fn main(): int { let x: ?int = 1; if (x == null || x < 0) { return 0; } return x; }");

    // ------------------------------------------------------------------ what is NOT narrowed

    [Fact]
    public void An_assignment_undoes_the_narrowing() =>
        DoesNotNarrow("fn main(): int { var x: ?int = 1; if (x != null) { x = null; return x; } return 0; }");

    [Fact]
    public void An_assignment_undoes_it_inside_a_loop_too() =>
        // The half that makes 'while' sound at all; without it the body would be a hole.
        DoesNotNarrow("""
            fn main(): int {
                var x: ?int = 1;
                var s = 0;
                while (x != null) { x = null; s += x; }
                return s;
            }
            """);

    [Fact]
    public void A_do_while_does_not_narrow_its_body() =>
        // There the body runs BEFORE the first check, so the condition says nothing at its start.
        DoesNotNarrow("""
            fn main(): int {
                var x: ?int = 1;
                var s = 0;
                do { s += x; } while (x != null);
                return s;
            }
            """);

    [Fact]
    public void Or_does_not_narrow_the_then_branch() =>
        // The then branch is reached as soon as ONE side is true, and which one is unknown.
        DoesNotNarrow("""
            fn main(): int {
                let a: ?int = 1;
                let b: ?int = 2;
                if (a != null || b != null) { return a; }
                return 0;
            }
            """);

    [Fact]
    public void And_does_not_narrow_the_else_branch() =>
        // The mirror image: the else branch is reached as soon as one side is false.
        DoesNotNarrow("""
            fn main(): int {
                let a: ?int = 1;
                if (a != null && a > 0) { return 0; }
                return a;
            }
            """);

    [Fact]
    public void The_narrowing_ends_with_the_branch() =>
        DoesNotNarrow("fn main(): int { let x: ?int = 1; if (x != null) { } return x; }");
}
