using Lyric.DocGen.Rendering;

namespace Lyric.Tests.DocGen;

/// <summary>
/// Where a document lands on the site. These paths are the URLs a reader bookmarks, so they are
/// pinned rather than left to whatever the code happens to produce.
/// </summary>
public class SitePathsTests
{
    [Theory]
    [InlineData("docs/guide/01-getting-started.md", "guide/getting-started/")]
    [InlineData("docs/guide/14-embedding.md", "guide/embedding/")]
    [InlineData("docs/Grammar.md", "grammar/")]
    [InlineData("docs/Bytecode.md", "bytecode/")]
    public void A_source_document_maps_to_its_site_path(string source, string expected) =>
        Assert.Equal(expected, SitePaths.OfSource(source));

    [Theory]
    [InlineData("README.md")]
    [InlineData("CONTRIBUTING.md")]
    [InlineData("docs/guide/notes.txt")]
    [InlineData("stdlib/std/math.lyr")]
    public void A_file_outside_the_site_maps_to_nothing(string source) =>
        Assert.Null(SitePaths.OfSource(source));

    [Fact]
    public void Backslashes_are_accepted_so_a_windows_path_maps_the_same()
    {
        Assert.Equal("guide/functions/", SitePaths.OfSource(@"docs\guide\03-functions.md"));
    }

    [Fact]
    public void A_module_maps_under_stdlib() =>
        Assert.Equal("stdlib/std.io.file/", SitePaths.OfModule("std.io.file"));

    [Fact]
    public void A_url_carries_the_version_as_a_path_segment() =>
        Assert.Equal("/v1.0.0/guide/functions/", SitePaths.Url("v1.0.0", "guide/functions/"));

    // ------------------------------------------------------------------ slugs and order

    [Theory]
    [InlineData("01-getting-started", "getting-started")]
    [InlineData("14-embedding", "embedding")]
    [InlineData("Grammar", "grammar")]
    [InlineData("Values And Types", "values-and-types")]
    [InlineData("a--b", "a-b")]
    [InlineData("trailing-", "trailing")]
    public void A_name_becomes_a_slug(string name, string expected) =>
        Assert.Equal(expected, SitePaths.Slug(name));

    [Fact]
    public void An_all_digit_name_keeps_its_digits()
    {
        // Stripping the ordering prefix must not leave an empty segment.
        Assert.Equal("2026", SitePaths.Slug("2026"));
    }

    [Theory]
    [InlineData("docs/guide/01-getting-started.md", 1)]
    [InlineData("docs/guide/14-embedding.md", 14)]
    [InlineData("docs/Grammar.md", int.MaxValue)]
    public void The_ordering_prefix_sorts_the_chapters(string source, int expected) =>
        Assert.Equal(expected, SitePaths.Order(source));

    [Fact]
    public void The_guide_chapters_sort_into_their_written_order()
    {
        var files = Directory
            .GetFiles(TestPaths.RepoRoot("docs", "guide"), "*.md")
            .Select(f => "docs/guide/" + Path.GetFileName(f))
            .ToArray();

        var sorted = files.OrderBy(SitePaths.Order).ToArray();
        Assert.Equal("guide/getting-started/", SitePaths.OfSource(sorted[0]));
        Assert.Equal("guide/diagnostics/", SitePaths.OfSource(sorted[^1]));

        // Every chapter has a prefix, or the ordering would be silently arbitrary.
        Assert.All(files, f => Assert.NotEqual(int.MaxValue, SitePaths.Order(f)));

        // And the prefixes are unique, or two chapters would tie.
        Assert.Equal(files.Length, files.Select(SitePaths.Order).Distinct().Count());
    }

    [Fact]
    public void No_two_chapters_share_a_site_path()
    {
        var paths = Directory
            .GetFiles(TestPaths.RepoRoot("docs", "guide"), "*.md")
            .Select(f => SitePaths.OfSource("docs/guide/" + Path.GetFileName(f)))
            .ToArray();

        Assert.Equal(paths.Length, paths.Distinct().Count());
    }
}
