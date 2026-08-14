using System.Net;
using System.Text;
using Lyric.DocGen.Model;
using Lyric.DocGen.Rendering;

namespace Lyric.DocGen.Site;

/// <summary>
/// One page per standard library module, built from the extracted model.
///
/// <para>The doc blocks are markdown like every other source in the site — they carry backticks,
/// paragraphs and lists — so they run through the same renderer rather than being pasted in as
/// text.</para>
/// </summary>
public static class StdlibPages
{
    public static SiteSection Build(DocModel model, LinkResolver links) =>
        new("Standard library", model.Modules.Select(m => Page(m, links)).ToArray());

    private static SitePage Page(DocModule module, LinkResolver links)
    {
        var body = new StringBuilder();
        var headings = new List<Heading>();

        // No <h1> in the body: the shell prints the title, as it does for a markdown page.
        headings.Add(new Heading(1, module.Path, ""));

        if (module.Doc is not null)
            body.Append(Prose(module.Doc, module.Path, links));

        foreach (var item in module.Items)
        {
            var anchor = Anchor(item);
            headings.Add(new Heading(2, item.Name, anchor));

            body.Append($"<section class=\"item\" id=\"{Escape(anchor)}\">\n");
            body.Append($"<h2><a href=\"#{Escape(anchor)}\">{Escape(item.Name)}</a> ");
            body.Append($"<span class=\"kind\">{item.Kind.ToString().ToLowerInvariant()}</span></h2>\n");
            body.Append($"<pre class=\"signature\"><code class=\"language-lyr\">{Escape(item.Signature)}</code></pre>\n");

            if (item.Doc is not null)
                body.Append(Prose(item.Doc, module.Path, links));

            if (item.Members.Length > 0)
                body.Append(Members(item, module.Path, links));

            body.Append($"<p class=\"source\">{Escape(item.Source.File)}:{item.Source.Line}</p>\n");
            body.Append("</section>\n");
        }

        return new SitePage(SitePaths.OfModule(module.Path), module.Path, body.ToString(),
            headings.ToArray());
    }

    private static string Members(DocItem item, string module, LinkResolver links)
    {
        var sb = new StringBuilder("<div class=\"members\">\n");
        foreach (var member in item.Members)
        {
            sb.Append("<div class=\"member\">\n");
            sb.Append($"<pre class=\"signature\"><code class=\"language-lyr\">{Escape(member.Signature)}</code></pre>\n");
            if (member.Doc is not null) sb.Append(Prose(member.Doc, module, links));
            sb.Append("</div>\n");
        }
        return sb.Append("</div>\n").ToString();
    }

    /// <summary>
    /// A doc block as markdown. Broken links inside a doc block are dropped rather than reported:
    /// a '.lyr' file has no place in the source tree to be relative to, so a relative link there is
    /// a mistake the author has to see in the output, not a build failure.
    /// </summary>
    private static string Prose(string doc, string module, LinkResolver links) =>
        MarkdownRenderer.Render(doc, $"stdlib/{module}.lyr", links).Html;

    /// <summary>
    /// The anchor of an item. The KIND is part of it, because a name can occur twice in a module —
    /// a class 'List' beside a function 'List' would otherwise collide and make one unreachable.
    /// </summary>
    private static string Anchor(DocItem item) =>
        $"{item.Kind.ToString().ToLowerInvariant()}-{SitePaths.Slug(item.Name)}";

    private static string Escape(string s) => WebUtility.HtmlEncode(s);
}
