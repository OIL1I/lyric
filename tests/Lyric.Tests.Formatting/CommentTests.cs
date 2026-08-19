using Lyric.Core;
using Lyric.Formatting;

namespace Lyric.Tests.Formatting;

/// <summary>
/// Comments through the formatter: none is ever lost, an own-line comment keeps its place and
/// its surrounding blank lines, a trailing comment stays trailing — and the one deliberate
/// trade, a comment inside an expression surfacing at the next statement boundary, is pinned
/// as a trade rather than discovered as a surprise.
/// </summary>
public class CommentTests
{
    private static string Format(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<test>", source);
        var de = new DiagnosticEngine(sm);
        var formatted = Formatter.Format(sm, id, de);

        Assert.False(de.HasErrors, "the test source did not parse");
        Assert.NotNull(formatted);

        var sm2 = new SourceManager();
        var id2 = sm2.AddVirtual("<test2>", formatted);
        var second = Formatter.Format(sm2, id2, new DiagnosticEngine(sm2));
        Assert.Equal(formatted, second);

        return formatted;
    }

    [Fact]
    public void An_own_line_comment_keeps_its_place_and_its_air()
    {
        Assert.Equal("""
            fn f(): int {
                let x = 1;

                // the interesting half
                let y = 2;
                return x + y;
            }

            """, Format("""
            fn f(): int {
                let x = 1;


                // the interesting half
                let y = 2;
                return x + y;
            }
            """));
    }

    [Fact]
    public void A_trailing_comment_stays_on_its_line()
    {
        Assert.Equal("""
            fn f(): int {
                let x = 1; // one
                return x; // done
            }

            """, Format("""
            fn f(): int {
                let x = 1;    // one
                return x;   // done
            }
            """));
    }

    [Fact]
    public void A_doc_comment_stays_glued_to_its_declaration()
    {
        Assert.Equal("""
            /// Adds one.
            /// The second line.
            fn inc(n: int): int {
                return n + 1;
            }

            /// The other one.
            fn dec(n: int): int {
                return n - 1;
            }

            """, Format("""
            /// Adds one.
            /// The second line.
            fn inc(n: int): int { return n + 1; }
            /// The other one.
            fn dec(n: int): int { return n - 1; }
            """));
    }

    [Fact]
    public void A_comment_before_the_closing_brace_stays_inside()
    {
        Assert.Equal("""
            fn f(): void {
                work();
                // nothing else to do
            }

            """, Format("fn f(): void { work();\n    // nothing else to do\n}"));
    }

    [Fact]
    public void An_empty_block_with_a_comment_is_not_collapsed()
    {
        Assert.Equal("""
            fn f(): void {
                // deliberately empty
            }

            """, Format("fn f(): void {\n    // deliberately empty\n}"));
    }

    [Fact]
    public void A_comment_inside_an_expression_surfaces_at_the_boundary()
    {
        // The documented trade: line-level fidelity. The comment is not lost, it moves to the
        // end of the statement it was written inside.
        var formatted = Format("fn f(): int {\n    return g(1, /* why one */ 2);\n}");

        Assert.Contains("/* why one */", formatted);
        Assert.Contains("return g(1, 2); /* why one */", formatted);
    }

    [Fact]
    public void Comments_between_members_and_variants_hold_their_lines()
    {
        Assert.Equal("""
            enum Shape {
                // the round one
                Circle(float),
                Rectangle(float, float), // the angular one
            }

            struct P {
                // position
                x: int,
                y: int,
            }

            """, Format("""
            enum Shape {
                // the round one
                Circle(float),
                Rectangle(float, float), // the angular one
            }
            struct P {
                // position
                x: int,
                y: int,
            }
            """));
    }

    [Fact]
    public void A_file_leading_and_file_trailing_comment_survive()
    {
        Assert.Equal("""
            // Copyright note.

            fn f(): void { }

            // The end.

            """, Format("""
            // Copyright note.

            fn f(): void { }

            // The end.
            """));
    }

    [Fact]
    public void A_block_comment_between_declarations_survives_whole()
    {
        var formatted = Format("""
            fn a(): void { }

            /* A wider
               explanation. */
            fn b(): void { }
            """);

        Assert.Contains("/* A wider\n   explanation. */", formatted);
    }

    [Fact]
    public void Match_arms_carry_their_comments()
    {
        Assert.Equal("""
            fn f(n: int): int {
                return match (n) {
                    // small
                    0 => 1,
                    _ => 2, // everything else
                };
            }

            """, Format("""
            fn f(n: int): int {
                return match (n) {
                    // small
                    0 => 1,
                    _ => 2, // everything else
                };
            }
            """));
    }
}
