using Lyric.DocGen.Rendering;

namespace Lyric.Tests.DocGen;

/// <summary>
/// Markdown to HTML, and what the page needs besides the HTML: the headings for a table of
/// contents, and the links that could not be placed.
///
/// <para>The feature tests cover exactly what the sources use — headings, tables, fenced code,
/// lists, inline code. An extension nothing uses would be behaviour nobody checks.</para>
/// </summary>
public class MarkdownRendererTests
{
    private static readonly LinkResolver Links = new("v1.0.0");

    private static RenderedMarkdown Render(string markdown, string from = "docs/guide/03-functions.md")
        => MarkdownRenderer.Render(markdown, from, Links);

    // ------------------------------------------------------------------ what the sources use

    [Fact]
    public void A_heading_becomes_a_heading_with_an_anchor()
    {
        var page = Render("## Values and types\n");
        Assert.Contains("<h2", page.Html);
        Assert.Contains("id=\"values-and-types\"", page.Html);
    }

    [Fact]
    public void A_table_becomes_a_table()
    {
        // 224 table rows across the sources, so this is the extension that matters most.
        var page = Render("| A | B |\n|---|---|\n| 1 | 2 |\n");
        Assert.Contains("<table>", page.Html);
        Assert.Contains("<th>A</th>", page.Html);
        Assert.Contains("<td>2</td>", page.Html);
    }

    [Fact]
    public void A_fenced_block_keeps_its_language()
    {
        // The class is what a highlighter later hooks onto; without it every block is plain text.
        var page = Render("```lyr\nfn main(): int { return 0; }\n```\n");
        Assert.Contains("<code class=\"language-lyr\">", page.Html);
        Assert.Contains("fn main()", page.Html);
    }

    [Fact]
    public void A_fence_without_a_language_still_renders()
    {
        var page = Render("```\nplain output\n```\n");
        Assert.Contains("<pre><code>", page.Html);
        Assert.Contains("plain output", page.Html);
    }

    [Fact]
    public void Lists_inline_code_and_emphasis_render()
    {
        var page = Render("- one `x`\n- **two**\n");
        Assert.Contains("<ul>", page.Html);
        Assert.Contains("<code>x</code>", page.Html);
        Assert.Contains("<strong>two</strong>", page.Html);
    }

    [Fact]
    public void Html_in_the_source_is_escaped_where_it_is_content()
    {
        // A generic like 'List<T>' inside inline code must not become a tag.
        var page = Render("Use `List<T>` for that.\n");
        Assert.Contains("List&lt;T&gt;", page.Html);
    }

    [Fact]
    public void Line_endings_are_normalised()
    {
        var page = Render("# A\r\n\r\ntext\r\n");
        Assert.DoesNotContain("\r", page.Html);
    }

    // ------------------------------------------------------------------ headings

    [Fact]
    public void Headings_come_out_in_order_with_their_level()
    {
        var page = Render("# Title\n\n## First\n\n### Deeper\n\n## Second\n");
        Assert.Equal([1, 2, 3, 2], page.Headings.Select(h => h.Level));
        Assert.Equal(["Title", "First", "Deeper", "Second"], page.Headings.Select(h => h.Text));
    }

    [Fact]
    public void A_heading_with_markup_reports_its_plain_text()
    {
        // The navigation shows text, not markup.
        var page = Render("## The `match` **expression**\n");
        var heading = Assert.Single(page.Headings);
        Assert.Equal("The match expression", heading.Text);
    }

    [Fact]
    public void The_reported_anchor_is_the_one_in_the_html()
    {
        var page = Render("## Optionals and null\n");
        var heading = Assert.Single(page.Headings);
        Assert.Equal("optionals-and-null", heading.Anchor);
        Assert.Contains($"id=\"{heading.Anchor}\"", page.Html);
    }

    // ------------------------------------------------------------------ links

    [Fact]
    public void A_relative_link_to_a_sibling_chapter_is_rewritten()
    {
        var page = Render("[next](04-control-flow.md)");
        Assert.Empty(page.BrokenLinks);
        Assert.Contains("href=\"/v1.0.0/guide/control-flow/\"", page.Html);
    }

    [Fact]
    public void A_source_path_with_backslashes_resolves_the_same()
    {
        // Path.GetDirectoryName returns the platform separator on Windows, which would make '..'
        // pop the whole path rather than one directory.
        var page = Render("[grammar](../Grammar.md)", @"docs\guide\03-functions.md");
        Assert.Empty(page.BrokenLinks);
        Assert.Contains("href=\"/v1.0.0/grammar/\"", page.Html);
    }

    [Fact]
    public void A_link_up_out_of_the_guide_is_rewritten()
    {
        var page = Render("[grammar](../Grammar.md#types)");
        Assert.Empty(page.BrokenLinks);
        Assert.Contains("href=\"/v1.0.0/grammar/#types\"", page.Html);
    }

    [Fact]
    public void An_external_link_is_left_alone()
    {
        var page = Render("[repo](https://github.com/lyriclang/lyric)");
        Assert.Contains("href=\"https://github.com/lyriclang/lyric\"", page.Html);
        Assert.Empty(page.BrokenLinks);
    }

    [Fact]
    public void A_same_page_anchor_is_left_alone()
    {
        var page = Render("[above](#values)");
        Assert.Contains("href=\"#values\"", page.Html);
        Assert.Empty(page.BrokenLinks);
    }

    [Fact]
    public void A_link_to_something_the_site_does_not_hold_is_reported()
    {
        // Not rewritten to a guess: the caller turns this into a failing build.
        var page = Render("[rules](../../CONTRIBUTING.md)");
        Assert.Equal(["../../CONTRIBUTING.md"], page.BrokenLinks);
        Assert.Contains("href=\"../../CONTRIBUTING.md\"", page.Html);
    }

    // ------------------------------------------------------------------ the real sources

    public static TheoryData<string> Sources()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(TestPaths.RepoRoot("docs"), "*.md"))
            data.Add("docs/" + Path.GetFileName(file));
        foreach (var file in Directory.GetFiles(TestPaths.RepoRoot("docs", "guide"), "*.md"))
            data.Add("docs/guide/" + Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Every_source_document_renders_without_a_broken_link(string source)
    {
        var page = MarkdownRenderer.Render(
            File.ReadAllText(TestPaths.RepoRoot(source.Split('/'))), source, Links);

        Assert.NotEmpty(page.Html);
        Assert.NotEmpty(page.Headings);
        Assert.Empty(page.BrokenLinks);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        var source = "docs/guide/06-enums-and-matching.md";
        var text = File.ReadAllText(TestPaths.RepoRoot(source.Split('/')));
        Assert.Equal(
            MarkdownRenderer.Render(text, source, Links).Html,
            MarkdownRenderer.Render(text, source, Links).Html);
    }

    [Fact]
    public void Anchors_are_unique_within_a_document()
    {
        // Two headings with the same anchor make one of them unreachable.
        foreach (var file in Directory.GetFiles(TestPaths.RepoRoot("docs", "guide"), "*.md"))
        {
            var source = "docs/guide/" + Path.GetFileName(file);
            var anchors = MarkdownRenderer.Render(File.ReadAllText(file), source, Links)
                .Headings.Select(h => h.Anchor).ToArray();

            Assert.Equal(anchors.Length, anchors.Distinct().Count());
        }
    }
}
