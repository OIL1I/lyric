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

        var guide = Section("Guide", SiteArea.Guide, GuideSources(repoRoot), repoRoot, links, broken);
        var reference = Section("Reference", SiteArea.Documentation,
            ["docs/Grammar.md", "docs/Bytecode.md"], repoRoot, links, broken);

        // The changelog renders with the repository fallback: its entries legitimately point at
        // files the site does not hold (README.md), and those belong on GitHub rather than in the
        // broken list.
        var changelogLinks = new LinkResolver(version, repositoryFallback: true);
        var changelogText = Changelog.ReadAll(repoRoot);
        var changelogPage = Render(changelogText, Changelog.Source, repoRoot, changelogLinks, broken);
        var project = new SiteSection("Project", SiteArea.Documentation, [changelogPage]);

        if (broken.Count > 0)
            throw new InvalidOperationException(
                "the documentation contains links the site cannot place:\n  " +
                string.Join("\n  ", broken));

        var model = StdlibExtractor.Extract(Path.Combine(repoRoot, "stdlib"), repoRoot);
        var stdlib = StdlibPages.Build(model, links);

        var entry = Changelog.EntryFor(changelogText, version)
            ?? throw new InvalidOperationException("the changelog holds no entry to show");
        var changes = new RecentChanges(entry.Title,
            MarkdownRenderer.Render(entry.Markdown, Changelog.Source, changelogLinks).Html);

        return new SiteContent(version, [guide, reference, project, stdlib], changes);
    }

    /// <summary>The guide chapters in the order of their numeric prefix, not in directory order.</summary>
    private static string[] GuideSources(string repoRoot) =>
        Directory
            .GetFiles(Path.Combine(repoRoot, "docs", "guide"), "*.md")
            .Select(f => "docs/guide/" + Path.GetFileName(f))
            .OrderBy(SitePaths.Order)
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToArray();

    private static SiteSection Section(string title, SiteArea area, string[] sources,
        string repoRoot, LinkResolver links, List<string> broken)
    {
        var pages = new List<SitePage>();
        foreach (var source in sources)
        {
            var path = Path.Combine([repoRoot, .. source.Split('/')]);
            pages.Add(Render(File.ReadAllText(path), source, repoRoot, links, broken));
        }
        return new SiteSection(title, area, pages.ToArray());
    }

    private static SitePage Render(string text, string source, string repoRoot,
        LinkResolver links, List<string> broken)
    {
        var rendered = MarkdownRenderer.Render(text, source, links, dropTitle: true);
        broken.AddRange(rendered.BrokenLinks.Select(l => $"{source}: {l}"));

        var sitePath = SitePaths.OfSource(source)
            ?? throw new InvalidOperationException($"{source} has no place on the site");

        return new SitePage(sitePath, Title(rendered, source), rendered.Html, rendered.Headings);
    }

    /// <summary>
    /// The first level-1 heading is the title. Without one the slug stands in, so a page is never
    /// nameless in the navigation.
    /// </summary>
    private static string Title(RenderedMarkdown rendered, string source) =>
        rendered.Headings.FirstOrDefault(h => h.Level == 1)?.Text
        ?? SitePaths.Slug(Path.GetFileNameWithoutExtension(source));
}
