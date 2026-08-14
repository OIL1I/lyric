using Lyric.DocGen.Extraction;
using Lyric.DocGen.Rendering;

namespace Lyric.DocGen.Site;

/// <summary>
/// Assembles the whole site content: the guide, the specifications and the standard library
/// reference.
///
/// <para>Produces strings, touches no output directory. <see cref="SiteWriter"/> does that.</para>
/// </summary>
public static class SiteBuilder
{
    /// <exception cref="InvalidOperationException">A document contains a link the site cannot
    /// place, or the standard library does not parse.</exception>
    public static SiteContent Build(string repoRoot, string version)
    {
        var links = new LinkResolver(version);
        var broken = new List<string>();

        var guide = Section("Guide", GuideSources(repoRoot), repoRoot, links, broken);
        var reference = Section("Reference", ["docs/Grammar.md", "docs/Bytecode.md"], repoRoot, links, broken);

        if (broken.Count > 0)
            throw new InvalidOperationException(
                "the documentation contains links the site cannot place:\n  " +
                string.Join("\n  ", broken));

        var model = StdlibExtractor.Extract(Path.Combine(repoRoot, "stdlib"), repoRoot);
        var stdlib = StdlibPages.Build(model, links);

        return new SiteContent(version, [guide, reference, stdlib]);
    }

    /// <summary>The guide chapters in the order of their numeric prefix, not in directory order.</summary>
    private static string[] GuideSources(string repoRoot) =>
        Directory
            .GetFiles(Path.Combine(repoRoot, "docs", "guide"), "*.md")
            .Select(f => "docs/guide/" + Path.GetFileName(f))
            .OrderBy(SitePaths.Order)
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToArray();

    private static SiteSection Section(string title, string[] sources, string repoRoot,
        LinkResolver links, List<string> broken)
    {
        var pages = new List<SitePage>();
        foreach (var source in sources)
        {
            var path = Path.Combine([repoRoot, .. source.Split('/')]);
            var rendered = MarkdownRenderer.Render(File.ReadAllText(path), source, links, dropTitle: true);

            broken.AddRange(rendered.BrokenLinks.Select(l => $"{source}: {l}"));

            var sitePath = SitePaths.OfSource(source)
                ?? throw new InvalidOperationException($"{source} has no place on the site");

            pages.Add(new SitePage(sitePath, Title(rendered, source), rendered.Html, rendered.Headings));
        }
        return new SiteSection(title, pages.ToArray());
    }

    /// <summary>
    /// The first level-1 heading is the title. Without one the slug stands in, so a page is never
    /// nameless in the navigation.
    /// </summary>
    private static string Title(RenderedMarkdown rendered, string source) =>
        rendered.Headings.FirstOrDefault(h => h.Level == 1)?.Text
        ?? SitePaths.Slug(Path.GetFileNameWithoutExtension(source));
}
