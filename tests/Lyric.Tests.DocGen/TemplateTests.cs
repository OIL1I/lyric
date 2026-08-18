using Lyric.DocGen.Rendering;
using Lyric.DocGen.Site;

namespace Lyric.Tests.DocGen;

/// <summary>
/// The page shell. Assembled from strings, so it is checked as strings — no output directory
/// involved.
/// </summary>
public class TemplateTests
{
    private static SitePage Page(string path, string title, string html = "<p>body</p>\n",
        params Heading[] headings) => new(path, title, html, headings);

    private static readonly RecentChanges Changes =
        new("v1.0.0 — 2026-01-01", "<p>changed things</p>\n");

    private static SiteContent Site(params SiteSection[] sections) =>
        new("v1.0.0", sections, Changes);

    private static SiteSection Section(string title, SiteArea area, params SitePage[] pages) =>
        new(title, area, pages);

    private static SiteContent Guide() => Site(Section("Guide", SiteArea.Guide,
        Page("guide/getting-started/", "Getting started"),
        Page("guide/functions/", "Functions"),
        Page("guide/control-flow/", "Control flow")));

    // ------------------------------------------------------------------ the shell

    [Fact]
    public void The_title_appears_once_in_the_head_and_once_as_the_heading()
    {
        var site = Guide();
        var html = Template.Page(site, site.Sections[0].Pages[1]);

        Assert.Contains("<title>Functions — Lyric v1.0.0</title>", html);
        Assert.Contains("<h1>Functions</h1>", html);
        Assert.Equal(1, html.Split("<h1>").Length - 1);
    }

    [Fact]
    public void The_heading_stands_before_the_body()
    {
        var site = Guide();
        var html = Template.Page(site, site.Sections[0].Pages[1]);
        Assert.True(html.IndexOf("<h1>", StringComparison.Ordinal)
                    < html.IndexOf("<p>body</p>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("grammar/", "../")]
    [InlineData("guide/functions/", "../../")]
    [InlineData("stdlib/std.io.file/", "../../")]
    public void Asset_links_reach_back_to_the_version_root(string sitePath, string expected)
    {
        var site = Site(Section("S", SiteArea.Documentation, Page(sitePath, "T")));
        var html = Template.Page(site, site.Sections[0].Pages[0]);
        Assert.Contains($"href=\"{expected}site.css\"", html);
        Assert.Contains($"src=\"{expected}site.js\"", html);
    }

    [Fact]
    public void The_switcher_points_at_the_index_above_the_version()
    {
        var site = Site(Section("S", SiteArea.Documentation, Page("grammar/", "T")));
        // From <root>/v1.0.0/grammar/ the index lies two levels up.
        Assert.Contains("data-versions=\"../../versions.json\"", Template.Page(site, site.Sections[0].Pages[0]));
    }

    // ------------------------------------------------------------------ navigation

    [Fact]
    public void The_current_page_is_marked_in_the_sidebar()
    {
        var site = Guide();
        var html = Template.Page(site, site.Sections[0].Pages[1]);

        Assert.Contains("href=\"../../guide/functions/\" class=\"here\"", html);
        Assert.Equal(1, html.Split("class=\"here\"").Length - 1);
    }

    /// <summary>The sidebar shows the AREA the page is in, and only that: the separation the
    /// welcome page establishes would be undone by a sidebar that lists everything.</summary>
    [Fact]
    public void The_sidebar_holds_the_own_area_and_not_the_other()
    {
        var site = Site(
            Section("Guide", SiteArea.Guide, Page("guide/functions/", "Functions")),
            Section("Reference", SiteArea.Documentation, Page("grammar/", "Grammar")),
            Section("Standard library", SiteArea.Documentation, Page("stdlib/std.core/", "std.core")));

        var inGuide = Template.Page(site, site.Sections[0].Pages[0]);
        Assert.Contains("<h2>Guide</h2>", inGuide);
        Assert.DoesNotContain("<h2>Reference</h2>", inGuide);
        Assert.DoesNotContain("<h2>Standard library</h2>", inGuide);

        var inDocs = Template.Page(site, site.Sections[1].Pages[0]);
        Assert.DoesNotContain("<h2>Guide</h2>", inDocs);
        Assert.Contains("<h2>Reference</h2>", inDocs);
        Assert.Contains("<h2>Standard library</h2>", inDocs);
    }

    [Fact]
    public void The_header_links_both_areas_and_marks_the_current_one()
    {
        var site = Site(
            Section("Guide", SiteArea.Guide, Page("guide/functions/", "Functions")),
            Section("Reference", SiteArea.Documentation, Page("grammar/", "Grammar")));

        var html = Template.Page(site, site.Sections[0].Pages[0]);
        Assert.Contains("class=\"current\" href=\"../../guide/functions/\">Guide</a>", html);
        Assert.Contains("href=\"../../grammar/\">Documentation</a>", html);
    }

    [Fact]
    public void Neighbours_are_the_pages_beside_it_in_the_same_section()
    {
        var site = Guide();
        var html = Template.Page(site, site.Sections[0].Pages[1]);
        Assert.Contains("class=\"prev\" href=\"../../guide/getting-started/\">Getting started</a>", html);
        Assert.Contains("class=\"next\" href=\"../../guide/control-flow/\">Control flow</a>", html);
    }

    [Fact]
    public void The_first_and_last_page_of_a_section_have_one_neighbour_each()
    {
        var site = Guide();
        var first = Template.Page(site, site.Sections[0].Pages[0]);
        var last = Template.Page(site, site.Sections[0].Pages[2]);

        Assert.DoesNotContain("class=\"prev\"", first);
        Assert.Contains("class=\"next\"", first);
        Assert.Contains("class=\"prev\"", last);
        Assert.DoesNotContain("class=\"next\"", last);
    }

    [Fact]
    public void Neighbours_do_not_cross_a_section_boundary()
    {
        // The last guide chapter must not lead into the specification.
        var site = Site(
            Section("Guide", SiteArea.Guide, Page("guide/embedding/", "Embedding")),
            Section("Reference", SiteArea.Documentation, Page("grammar/", "Grammar")));

        Assert.DoesNotContain("class=\"neighbours\"", Template.Page(site, site.Sections[0].Pages[0]));
    }

    // ------------------------------------------------------------------ contents

    [Fact]
    public void The_contents_list_the_level_two_headings()
    {
        var site = Site(Section("S", SiteArea.Documentation, Page("grammar/", "T", "<p>x</p>",
            new Heading(1, "Title", "title"),
            new Heading(2, "First", "first"),
            new Heading(3, "Deeper", "deeper"),
            new Heading(2, "Second", "second"))));

        var html = Template.Page(site, site.Sections[0].Pages[0]);
        Assert.Contains("href=\"#first\">First</a>", html);
        Assert.Contains("href=\"#second\">Second</a>", html);

        // Level 1 is the page title and level 3 is too fine for this list.
        Assert.DoesNotContain("href=\"#title\"", html);
        Assert.DoesNotContain("href=\"#deeper\"", html);
    }

    [Fact]
    public void A_page_with_fewer_than_two_headings_gets_no_contents()
    {
        // A list of one entry is a list of itself.
        var site = Site(Section("S", SiteArea.Documentation, Page("grammar/", "T", "<p>x</p>",
            new Heading(2, "Only", "only"))));

        Assert.DoesNotContain("class=\"contents\"", Template.Page(site, site.Sections[0].Pages[0]));
    }

    // ------------------------------------------------------------------ escaping

    [Fact]
    public void A_title_with_markup_characters_is_escaped()
    {
        var site = Site(Section("S", SiteArea.Documentation, Page("grammar/", "List<T> & \"friends\"")));
        var html = Template.Page(site, site.Sections[0].Pages[0]);

        Assert.Contains("List&lt;T&gt; &amp; &quot;friends&quot;", html);
        Assert.DoesNotContain("<h1>List<T>", html);
    }

    // ------------------------------------------------------------------ landings

    [Fact]
    public void The_welcome_page_offers_both_areas_and_the_recent_changes()
    {
        var site = Site(
            Section("Guide", SiteArea.Guide, Page("guide/getting-started/", "Getting started")),
            Section("Reference", SiteArea.Documentation, Page("grammar/", "Grammar")));

        var html = Template.Welcome(site);

        // The two doors, relative to the version root the page sits at.
        Assert.Contains("class=\"card\" href=\"guide/getting-started/\"", html);
        Assert.Contains("class=\"card\" href=\"grammar/\"", html);

        // What changed, with the entry's own heading and the way to the rest.
        Assert.Contains("What changed", html);
        Assert.Contains("v1.0.0 — 2026-01-01", html);
        Assert.Contains("<p>changed things</p>", html);
        Assert.Contains("href=\"changelog/\"", html);

        // The choice is the page's content; a sidebar would make it twice.
        Assert.DoesNotContain("class=\"sidebar\"", html);
    }

    [Fact]
    public void The_site_root_forwards_to_the_landing_version()
    {
        var html = Template.SiteLanding(new VersionEntry("v1.0.0", true));
        Assert.Contains("url=v1.0.0/", html);
    }
}
