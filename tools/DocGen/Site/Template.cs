using System.Net;
using System.Text;
using Lyric.DocGen.Rendering;

namespace Lyric.DocGen.Site;

/// <summary>
/// The HTML shell around a page body: head, sidebar, content, in-page contents, version switcher.
///
/// <para>String concatenation rather than a template engine. The shell is one page layout, and a
/// second templating language beside HTML would need its own syntax, its own errors and its own
/// tests.</para>
/// </summary>
public static class Template
{
    /// <summary>A complete page, with the whole site passed in because the sidebar lists every
    /// page and the neighbour links need the section this one sits in.</summary>
    public static string Page(SiteContent site, SitePage page)
    {
        var root = Root(page.SitePath);
        var sb = new StringBuilder();

        // Void elements are written self-closing. That is valid HTML5 and makes the whole page
        // parse as XML, which is what lets a test check the structure of every page produced.
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\" />\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n");
        sb.Append($"<title>{E(page.Title)} — Lyric {E(site.Version)}</title>\n");
        sb.Append($"<link rel=\"stylesheet\" href=\"{root}site.css\" />\n");
        sb.Append("</head>\n<body>\n");

        sb.Append(Header(site, root, AreaOf(site, page)));

        sb.Append("<div class=\"layout\">\n");
        sb.Append(Sidebar(site, page, root));
        sb.Append("<main>\n");
        sb.Append($"<h1>{E(page.Title)}</h1>\n");
        sb.Append(Contents(page));
        sb.Append(page.Html);
        sb.Append(Neighbours(site, page, root));
        sb.Append("</main>\n");
        sb.Append("</div>\n");

        sb.Append($"<script src=\"{root}site.js\"></script>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>
    /// The welcome page at the version root: the place where a visitor chooses between LEARNING
    /// (the guide) and LOOKING SOMETHING UP (the documentation), with what changed last underneath.
    ///
    /// <para>No sidebar. The choice between the two areas is this page's content; a navigation
    /// that already made it would ask the question and shout the answer.</para>
    /// </summary>
    public static string Welcome(SiteContent site)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\" />\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n");
        sb.Append($"<title>Lyric {E(site.Version)}</title>\n");
        sb.Append("<link rel=\"stylesheet\" href=\"site.css\" />\n");
        sb.Append("</head>\n<body>\n");

        sb.Append(Header(site, root: "", area: null));

        sb.Append("<main class=\"welcome\">\n");
        sb.Append("<h1>Lyric</h1>\n");
        sb.Append("<p class=\"lead\">A statically typed application language with a bytecode "
                  + "VM — standalone, and embeddable in a host with a capability-based "
                  + "sandbox.</p>\n");

        sb.Append("<nav class=\"cards\">\n");
        sb.Append($"<a class=\"card\" href=\"{E(site.EntryOf(SiteArea.Guide).SitePath)}\">\n");
        sb.Append("<h2>Guide</h2>\n<p>Learn the language chapter by chapter — from the first "
                  + "program to embedding, building and attributes.</p>\n</a>\n");
        sb.Append($"<a class=\"card\" href=\"{E(site.EntryOf(SiteArea.Documentation).SitePath)}\">\n");
        sb.Append("<h2>Documentation</h2>\n<p>The reference: the grammar, the bytecode format, "
                  + "the standard library and the changelog.</p>\n</a>\n");
        sb.Append("</nav>\n");

        sb.Append("<section class=\"changes\">\n");
        sb.Append($"<h2>What changed in {E(site.Changes.Title)}</h2>\n");
        sb.Append(site.Changes.Html);
        sb.Append("<p><a href=\"changelog/\">The full changelog →</a></p>\n");
        sb.Append("</section>\n");

        sb.Append("</main>\n");
        sb.Append("<script src=\"site.js\"></script>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>The shared top bar. <paramref name="area"/> marks the half the reader is in;
    /// the welcome page passes <c>null</c> and neither link is marked.</summary>
    private static string Header(SiteContent site, string root, SiteArea? area)
    {
        var sb = new StringBuilder();
        sb.Append("<header class=\"top\">\n");
        sb.Append($"<a class=\"brand\" href=\"{root}\">Lyric</a>\n");
        sb.Append("<nav class=\"areas\">\n");
        sb.Append(AreaLink(site, root, SiteArea.Guide, "Guide", area));
        sb.Append(AreaLink(site, root, SiteArea.Documentation, "Documentation", area));
        sb.Append("</nav>\n");
        sb.Append($"<span class=\"version\">{E(site.Version)}</span>\n");
        sb.Append($"<nav class=\"switcher\" data-versions=\"{root}../versions.json\"></nav>\n");
        sb.Append("</header>\n");
        return sb.ToString();
    }

    private static string AreaLink(SiteContent site, string root, SiteArea target, string label,
        SiteArea? current)
    {
        if (site.EntryOf(target) is not { } entry) return "";
        // 'current' rather than the sidebar's 'here': one marks the area, the other the page,
        // and a page test counting 'here' occurrences must keep finding exactly one.
        var marked = current == target ? " class=\"current\"" : "";
        return $"<a{marked} href=\"{root}{E(entry.SitePath)}\">{label}</a>\n";
    }

    private static SiteArea AreaOf(SiteContent site, SitePage page) =>
        site.Sections.First(s => s.Pages.Contains(page)).Area;

    /// <summary>The site root: forwards to the version a visitor should see.</summary>
    public static string SiteLanding(VersionEntry landing) =>
        Redirect($"{landing.Version}/", "Lyric documentation");

    private static string Redirect(string target, string title) =>
        "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n"
        + $"<meta http-equiv=\"refresh\" content=\"0; url={E(target)}\" />\n"
        + $"<link rel=\"canonical\" href=\"{E(target)}\" />\n<title>{E(title)}</title>\n"
        + $"</head>\n<body>\n<p><a href=\"{E(target)}\">{E(title)}</a></p>\n</body>\n</html>\n";

    /// <summary>Only the sections of the area the page is in: a reader inside the guide sees the
    /// chapters, a reader inside the documentation sees reference, project and standard library —
    /// the separation the welcome page establishes, kept while navigating.</summary>
    private static string Sidebar(SiteContent site, SitePage current, string root)
    {
        var area = AreaOf(site, current);
        var sb = new StringBuilder("<nav class=\"sidebar\">\n");
        foreach (var section in site.Sections.Where(s => s.Area == area))
        {
            sb.Append($"<h2>{E(section.Title)}</h2>\n<ul>\n");
            foreach (var page in section.Pages)
            {
                var here = page.SitePath == current.SitePath ? " class=\"here\"" : "";
                sb.Append($"<li><a href=\"{root}{E(page.SitePath)}\"{here}>{E(page.Title)}</a></li>\n");
            }
            sb.Append("</ul>\n");
        }
        return sb.Append("</nav>\n").ToString();
    }

    /// <summary>
    /// The headings of this page. Level 1 is left out — it is the page title and already stands at
    /// the top; a contents list whose first entry is the heading above it is noise.
    /// </summary>
    private static string Contents(SitePage page)
    {
        var entries = page.Headings.Where(h => h.Level == 2 && h.Anchor.Length > 0).ToArray();
        if (entries.Length < 2) return ""; // one entry is a list of itself

        var sb = new StringBuilder("<nav class=\"contents\">\n<ul>\n");
        foreach (var heading in entries)
            sb.Append($"<li><a href=\"#{E(heading.Anchor)}\">{E(heading.Text)}</a></li>\n");
        return sb.Append("</ul>\n</nav>\n").ToString();
    }

    /// <summary>Previous and next within the SAME section: a guide chapter leads to the next
    /// chapter, not into the specification.</summary>
    private static string Neighbours(SiteContent site, SitePage current, string root)
    {
        var section = site.Sections.FirstOrDefault(s => s.Pages.Contains(current));
        if (section is null) return "";

        var index = Array.IndexOf(section.Pages, current);
        var previous = index > 0 ? section.Pages[index - 1] : null;
        var next = index < section.Pages.Length - 1 ? section.Pages[index + 1] : null;
        if (previous is null && next is null) return "";

        var sb = new StringBuilder("<nav class=\"neighbours\">\n");
        if (previous is not null)
            sb.Append($"<a class=\"prev\" href=\"{root}{E(previous.SitePath)}\">{E(previous.Title)}</a>\n");
        if (next is not null)
            sb.Append($"<a class=\"next\" href=\"{root}{E(next.SitePath)}\">{E(next.Title)}</a>\n");
        return sb.Append("</nav>\n").ToString();
    }

    /// <summary>
    /// The way back to the version root from a page, as '../' per segment. Relative rather than
    /// absolute, so the whole tree can be opened from a local directory and not only from a server
    /// root.
    /// </summary>
    private static string Root(string sitePath) =>
        string.Concat(Enumerable.Repeat("../",
            sitePath.Count(c => c == '/') - (sitePath.EndsWith('/') ? 0 : 1)));

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
