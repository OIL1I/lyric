using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Lyric.DocGen.Rendering;

/// <summary>One heading of a page, for the in-page table of contents and the navigation.</summary>
/// <param name="Anchor">The id Markdig assigned, without the leading '#'.</param>
public sealed record Heading(int Level, string Text, string Anchor);

/// <param name="BrokenLinks">Hrefs the resolver could not place. Empty is the expected state.</param>
public sealed record RenderedMarkdown(string Html, Heading[] Headings, string[] BrokenLinks);

/// <summary>
/// Markdown to HTML.
///
/// <para>The extensions are named one by one rather than taken as a bundle: the sources use tables,
/// fenced code, headings, lists and inline code, and an extension that nothing uses is a behaviour
/// nobody checks.</para>
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// Pipe tables for the reference tables, auto identifiers so every heading gets a stable anchor
    /// to link to.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoIdentifiers()
        .Build();

    /// <param name="fromSource">Repository-relative path of the document, for resolving its links.</param>
    public static RenderedMarkdown Render(string markdown, string fromSource, LinkResolver links)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        var broken = new List<string>();
        foreach (var link in document.Descendants<LinkInline>())
        {
            var href = link.Url;
            if (string.IsNullOrEmpty(href)) continue;

            var resolved = links.Resolve(fromSource, href);
            if (resolved is null) broken.Add(href);
            else link.Url = resolved;
        }

        var headings = document.Descendants<HeadingBlock>()
            .Select(h => new Heading(h.Level, Text(h.Inline), h.GetAttributes().Id ?? ""))
            .ToArray();

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        return new RenderedMarkdown(writer.ToString().ReplaceLineEndings("\n"), headings, broken.ToArray());
    }

    /// <summary>
    /// The plain text of an inline sequence. Emphasis and inline code contribute their content
    /// without their markup, so a heading reads the same in the navigation as on the page.
    /// </summary>
    private static string Text(ContainerInline? inline)
    {
        if (inline is null) return "";

        var sb = new StringBuilder();
        foreach (var child in inline.Descendants())
            sb.Append(child switch
            {
                LiteralInline literal => literal.Content.ToString(),
                CodeInline code => code.Content,
                _ => "",
            });

        return sb.ToString();
    }
}
