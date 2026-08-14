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

    private static SiteContent Site(params SiteSection[] sections) => new("v1.0.0", sections);

    private static SiteContent Guide() => Site(new SiteSection("Guide",
    [
        Page("guide/getting-started/", "Getting started"),
        Page("guide/functions/", "Functions"),
        Page("guide/control-flow/", "Control flow"),
    ]));

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
        var site = Site(new SiteSection("S", [Page(sitePath, "T")]));
        var html = Template.Page(site, site.Sections[0].Pages[0]);
        Assert.Contains($"href=\"{expected}site.css\"", html);
        Assert.Contains($"src=\"{expected}site.js\"", html);
    }

    [Fact]
    public void The_switcher_points_at_the_index_above_the_version()
    {
        var site = Site(new SiteSection("S", [Page("grammar/", "T")]));
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

    [Fact]
    public void Every_page_of_every_section_is_listed()
    {
        var site = Site(
            new SiteSection("Guide", [Page("guide/functions/", "Functions")]),
            new SiteSection("Reference", [Page("grammar/", "Grammar")]));

        var html = Template.Page(site, site.Sections[0].Pages[0]);
        Assert.Contains("<h2>Guide</h2>", html);
        Assert.Contains("<h2>Reference</h2>", html);
        Assert.Contains(">Grammar</a>", html);
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
            new SiteSection("Guide", [Page("guide/embedding/", "Embedding")]),
            new SiteSection("Reference", [Page("grammar/", "Grammar")]));

        Assert.DoesNotContain("class=\"neighbours\"", Template.Page(site, site.Sections[0].Pages[0]));
    }

    // ------------------------------------------------------------------ contents

    [Fact]
    public void The_contents_list_the_level_two_headings()
    {
        var site = Site(new SiteSection("S", [Page("grammar/", "T", "<p>x</p>",
            new Heading(1, "Title", "title"),
            new Heading(2, "First", "first"),
            new Heading(3, "Deeper", "deeper"),
            new Heading(2, "Second", "second"))]));

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
        var site = Site(new SiteSection("S", [Page("grammar/", "T", "<p>x</p>",
            new Heading(2, "Only", "only"))]));

        Assert.DoesNotContain("class=\"contents\"", Template.Page(site, site.Sections[0].Pages[0]));
    }

    // ------------------------------------------------------------------ escaping

    [Fact]
    public void A_title_with_markup_characters_is_escaped()
    {
        var site = Site(new SiteSection("S", [Page("grammar/", "List<T> & \"friends\"")]));
        var html = Template.Page(site, site.Sections[0].Pages[0]);

        Assert.Contains("List&lt;T&gt; &amp; &quot;friends&quot;", html);
        Assert.DoesNotContain("<h1>List<T>", html);
    }

    // ------------------------------------------------------------------ landings

    [Fact]
    public void The_version_root_forwards_to_the_first_page()
    {
        var html = Template.VersionLanding(Guide());
        Assert.Contains("url=guide/getting-started/", html);
        Assert.Contains("href=\"guide/getting-started/\"", html);
    }

    [Fact]
    public void The_site_root_forwards_to_the_landing_version()
    {
        var html = Template.SiteLanding(new VersionEntry("v1.0.0", true));
        Assert.Contains("url=v1.0.0/", html);
    }
}
