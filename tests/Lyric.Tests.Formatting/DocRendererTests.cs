using Lyric.Formatting;

namespace Lyric.Tests.Formatting;

/// <summary>
/// The document algebra against the renderer: what fits stays on its line, what does not
/// breaks — and only the renderer measures. These tests build documents by hand; whether the
/// right documents are built for Lyric syntax is the later slices' question.
/// </summary>
public class DocRendererTests
{
    private static Doc Call(string callee, params string[] args) =>
        Doc.GroupOf(
            Doc.From(callee),
            Doc.From("("),
            Doc.IndentOf(
                Doc.LineOrNothing,
                Doc.Join(Doc.Of(Doc.From(","), Doc.LineOrSpace),
                    args.Select(Doc.From).ToArray())),
            Doc.LineOrNothing,
            Doc.From(")"));

    [Fact]
    public void What_fits_stays_flat()
    {
        Assert.Equal("f(a, b, c)", DocRenderer.Render(Call("f", "a", "b", "c")));
    }

    [Fact]
    public void What_does_not_fit_breaks_with_one_argument_per_line()
    {
        var wide = new string('x', 40);
        var doc = Call("f", wide, wide, wide);

        Assert.Equal(
            $"f(\n    {wide},\n    {wide},\n    {wide}\n)",
            DocRenderer.Render(doc));
    }

    [Fact]
    public void The_width_is_the_boundary_exactly()
    {
        // 'aaaa(bbbb)' is ten columns. At width 10 it fits; at 9 it breaks.
        var doc = Call("aaaa", "bbbb");

        Assert.Equal("aaaa(bbbb)", DocRenderer.Render(doc, width: 10));
        Assert.Equal("aaaa(\n    bbbb\n)", DocRenderer.Render(doc, width: 9));
    }

    [Fact]
    public void An_inner_group_may_stay_flat_while_the_outer_breaks()
    {
        var inner = Call("g", "1", "2");
        var outer = Doc.GroupOf(
            Doc.From("f("),
            Doc.IndentOf(Doc.LineOrNothing,
                Doc.From(new string('x', 95)), Doc.From(","), Doc.LineOrSpace, inner),
            Doc.LineOrNothing,
            Doc.From(")"));

        var rendered = DocRenderer.Render(outer);
        Assert.Contains("\n    g(1, 2)", rendered);
    }

    [Fact]
    public void A_hard_line_forces_every_enclosing_group_to_break()
    {
        var doc = Doc.GroupOf(
            Doc.From("{"),
            Doc.IndentOf(Doc.NewLine, Doc.From("short;")),
            Doc.NewLine,
            Doc.From("}"));

        // Nine columns of content, yet never '{ short; }' — a statement block is never flat.
        Assert.Equal("{\n    short;\n}", DocRenderer.Render(doc));
    }

    [Fact]
    public void A_group_that_fits_alone_but_not_before_its_closer_breaks()
    {
        // The trailing ');' belongs to the line the group ends on. 96 + "g(1)" is 100 — but the
        // closer behind it is column 102, so the group must break. The naive fits-check that
        // measures only the group itself gets this wrong.
        var doc = Doc.Of(
            Doc.From(new string('x', 96)),
            Call("g", "1"),
            Doc.From(");"));

        var rendered = DocRenderer.Render(doc);
        Assert.Contains("\n", rendered);
    }

    [Fact]
    public void Indentation_never_leaves_trailing_whitespace()
    {
        var doc = Doc.GroupOf(
            Doc.From("{"),
            Doc.IndentOf(Doc.NewLine, Doc.NewLine, Doc.From("x;")),
            Doc.NewLine,
            Doc.From("}"));

        var rendered = DocRenderer.Render(doc);
        Assert.Equal("{\n\n    x;\n}", rendered);
        Assert.All(rendered.Split('\n'), line => Assert.Equal(line, line.TrimEnd()));
    }

    [Fact]
    public void Output_is_lf_only()
    {
        var doc = Doc.Of(Doc.From("a"), Doc.NewLine, Doc.From("b"));
        Assert.DoesNotContain('\r', DocRenderer.Render(doc));
    }

    [Fact]
    public void A_text_longer_than_the_width_is_never_broken()
    {
        // The renderer breaks BETWEEN parts, never inside a token. A 120-column string literal
        // stays one line; the soft limit is soft exactly here.
        var wide = new string('x', 120);
        Assert.Equal(wide, DocRenderer.Render(Doc.GroupOf(Doc.From(wide))));
    }

    [Fact]
    public void Join_puts_the_separator_between_neighbours_only()
    {
        var doc = Doc.Join(Doc.From(", "), [Doc.From("a"), Doc.From("b"), Doc.From("c")]);
        Assert.Equal("a, b, c", DocRenderer.Render(doc));
        Assert.Equal("", DocRenderer.Render(Doc.Join(Doc.From(", "), [])));
    }
}
