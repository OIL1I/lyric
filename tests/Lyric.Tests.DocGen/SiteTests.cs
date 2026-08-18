using System.Xml.Linq;
using Lyric.DocGen.Site;

namespace Lyric.Tests.DocGen;

/// <summary>
/// The site as a whole: what gets built out of the repository, and what lands on disk.
///
/// <para>The load-bearing test is <see cref="Writing_a_version_leaves_the_other_versions_alone"/>.
/// A released version is frozen after it is written and the deploy publishes what lies in the
/// output — a build that emptied the root would silently take every earlier release with it.</para>
/// </summary>
public class SiteTests
{
    private static string Assets() => TestPaths.RepoRoot("tools", "DocGen", "assets");

    private static SiteContent Build(string version = "v1.0.0") =>
        SiteBuilder.Build(TestPaths.RepoRoot(), version);

    // ------------------------------------------------------------------ content

    [Fact]
    public void The_site_has_the_four_sections_in_reading_order()
    {
        var site = Build();
        Assert.Equal(["Guide", "Reference", "Project", "Standard library"],
            site.Sections.Select(s => s.Title));

        // The areas carry the separation: the guide stands alone, everything else is
        // documentation. The welcome page and the sidebar both derive from this.
        Assert.Equal([SiteArea.Guide, SiteArea.Documentation, SiteArea.Documentation,
            SiteArea.Documentation], site.Sections.Select(s => s.Area));
    }

    [Fact]
    public void The_recent_changes_of_a_release_are_its_changelog_entry()
    {
        var changes = Build("v1.6.0").Changes;
        Assert.StartsWith("v1.6.0", changes.Title);
        Assert.Contains("Attributes", changes.Html);
    }

    /// <summary>A nightly has no entry of its own; the welcome page then shows the newest one,
    /// because "what changed last" is still the question it answers.</summary>
    [Fact]
    public void The_recent_changes_of_a_nightly_are_the_newest_entry()
    {
        var changes = Build("nightly").Changes;
        Assert.StartsWith("v", changes.Title);
    }

    [Fact]
    public void The_guide_chapters_follow_their_numbering()
    {
        var guide = Build().Sections[0];
        Assert.Equal("guide/getting-started/", guide.Pages[0].SitePath);
        Assert.Equal("guide/building/", guide.Pages[^1].SitePath);
        Assert.Equal(16, guide.Pages.Length);
        Assert.Equal("guide/attributes/", guide.Pages[14].SitePath);
    }

    [Fact]
    public void A_page_takes_its_title_from_its_first_heading()
    {
        var guide = Build().Sections[0];
        Assert.Equal("Getting started", guide.Pages[0].Title);
        Assert.Equal("Building a project", guide.Pages[^1].Title);
    }

    [Fact]
    public void The_body_no_longer_holds_the_title_heading()
    {
        // The shell prints it; twice would put the contents above the heading it belongs under.
        var page = Build().Sections[0].Pages[0];
        Assert.DoesNotContain("<h1>", page.Html);
        Assert.Contains(page.Headings, h => h.Level == 1);
    }

    [Fact]
    public void Every_standard_library_module_has_a_page()
    {
        var stdlib = Build().Sections[3];
        Assert.Equal(11, stdlib.Pages.Length);
        Assert.All(stdlib.Pages, p => Assert.StartsWith("stdlib/std.", p.SitePath));
    }

    [Fact]
    public void A_module_page_shows_a_signature_per_item()
    {
        var math = Build().Sections[3].Pages.Single(p => p.Title == "std.math");
        Assert.Contains("pub fn sqrt(value: float): float", math.Html);
        Assert.Contains("class=\"signature\"", math.Html);
        Assert.Contains("stdlib/std/math.lyr", math.Html);
    }

    [Fact]
    public void Item_anchors_are_unique_within_a_module()
    {
        // The kind is part of the anchor, so a class and a function of the same name do not collide.
        foreach (var page in Build().Sections[3].Pages)
        {
            var anchors = page.Headings.Where(h => h.Anchor.Length > 0).Select(h => h.Anchor).ToArray();
            Assert.Equal(anchors.Length, anchors.Distinct().Count());
        }
    }

    [Fact]
    public void No_two_pages_share_a_path()
    {
        var paths = Build().Pages.Select(p => p.SitePath).ToArray();
        Assert.Equal(paths.Length, paths.Distinct().Count());
    }

    /// <summary>
    /// A minimal repository: the documents the builder expects, and nothing else.
    /// </summary>
    private static DirectoryInfo SyntheticRepo(string guideBody)
    {
        var root = Directory.CreateTempSubdirectory("docgen-repo");
        var guide = Directory.CreateDirectory(Path.Combine(root.FullName, "docs", "guide"));
        var stdlib = Directory.CreateDirectory(Path.Combine(root.FullName, "stdlib"));

        File.WriteAllText(Path.Combine(guide.FullName, "01-intro.md"), "# Intro\n\n" + guideBody);
        File.WriteAllText(Path.Combine(root.FullName, "CHANGELOG.md"),
            "# Changelog\n\n## v1.0.0 — 2026-01-01\n\nchanged things\n");
        File.WriteAllText(Path.Combine(root.FullName, "docs", "Grammar.md"), "# Grammar\n\ntext\n");
        File.WriteAllText(Path.Combine(root.FullName, "docs", "Bytecode.md"), "# Bytecode\n\ntext\n");
        File.WriteAllText(Path.Combine(stdlib.FullName, "m.lyr"), "module std.m;\npub fn f(): void { }\n");
        return root;
    }

    [Fact]
    public void A_link_the_site_cannot_place_aborts_the_build()
    {
        // Reported rather than rewritten to a guess: a dead link becomes a failing build instead of
        // a 404 nobody notices.
        var root = SyntheticRepo("See the [rules](../../CONTRIBUTING.md).\n");
        try
        {
            var e = Assert.Throws<InvalidOperationException>(
                () => SiteBuilder.Build(root.FullName, "v1.0.0"));

            Assert.Contains("CONTRIBUTING.md", e.Message);
            Assert.Contains("docs/guide/01-intro.md", e.Message);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void A_link_between_two_chapters_does_not_abort_the_build()
    {
        // The counter-check: a builder that rejected every link would pass the test above.
        var root = SyntheticRepo("See the [grammar](../Grammar.md).\n");
        try
        {
            var content = SiteBuilder.Build(root.FullName, "v1.0.0");
            Assert.Contains("href=\"/v1.0.0/grammar/\"", content.Sections[0].Pages[0].Html);
        }
        finally { root.Delete(recursive: true); }
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public void Writing_a_version_leaves_the_other_versions_alone()
    {
        var root = Directory.CreateTempSubdirectory("docgen-site");
        try
        {
            SiteWriter.Write(Build("v1.0.0"), root.FullName, stable: true, Assets());
            var released = Path.Combine(root.FullName, "v1.0.0", "guide", "functions", "index.html");
            var before = File.ReadAllText(released);

            SiteWriter.Write(Build("nightly"), root.FullName, stable: false, Assets());

            Assert.True(File.Exists(released), "the released version was deleted by the nightly build");
            Assert.Equal(before, File.ReadAllText(released));

            // And both are in the switcher.
            Assert.Equal(["nightly", "v1.0.0"],
                VersionIndex.Read(root.FullName).Entries.Select(e => e.Version));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void A_page_removed_from_the_source_does_not_survive_in_its_own_version()
    {
        var root = Directory.CreateTempSubdirectory("docgen-site");
        try
        {
            SiteWriter.Write(Build(), root.FullName, stable: true, Assets());
            var stray = Path.Combine(root.FullName, "v1.0.0", "guide", "gone");
            Directory.CreateDirectory(stray);
            File.WriteAllText(Path.Combine(stray, "index.html"), "<p>stale</p>");

            SiteWriter.Write(Build(), root.FullName, stable: true, Assets());

            Assert.False(Directory.Exists(stray));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void The_written_tree_carries_a_page_per_entry_plus_the_landings_and_assets()
    {
        var root = Directory.CreateTempSubdirectory("docgen-site");
        try
        {
            var content = Build();
            SiteWriter.Write(content, root.FullName, stable: true, Assets());
            var version = Path.Combine(root.FullName, "v1.0.0");

            foreach (var page in content.Pages)
            {
                var file = Path.Combine([version, .. page.SitePath.Split('/', StringSplitOptions.RemoveEmptyEntries), "index.html"]);
                Assert.True(File.Exists(file), $"missing page {page.SitePath}");
            }

            Assert.True(File.Exists(Path.Combine(version, "index.html")));
            Assert.True(File.Exists(Path.Combine(version, "site.css")));
            Assert.True(File.Exists(Path.Combine(version, "site.js")));
            Assert.True(File.Exists(Path.Combine(root.FullName, "index.html")));
            Assert.True(File.Exists(Path.Combine(root.FullName, VersionIndex.FileName)));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void The_site_root_points_at_the_release_and_not_at_the_nightly()
    {
        var root = Directory.CreateTempSubdirectory("docgen-site");
        try
        {
            SiteWriter.Write(Build("v1.0.0"), root.FullName, stable: true, Assets());
            SiteWriter.Write(Build("nightly"), root.FullName, stable: false, Assets());

            Assert.Contains("url=v1.0.0/", File.ReadAllText(Path.Combine(root.FullName, "index.html")));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void Every_written_page_is_well_formed_markup()
    {
        // Void elements are self-closing, so the whole page parses as XML — a cheap structural check
        // over the complete output rather than over a sample.
        var root = Directory.CreateTempSubdirectory("docgen-site");
        try
        {
            var content = Build();
            SiteWriter.Write(content, root.FullName, stable: true, Assets());

            var files = Directory.GetFiles(root.FullName, "*.html", SearchOption.AllDirectories);
            // Every page, plus the version landing and the site landing. Derived rather than
            // hardcoded, so adding a chapter does not fail a test about markup.
            Assert.Equal(content.Pages.Count() + 2, files.Length);

            foreach (var file in files)
            {
                var html = File.ReadAllText(file).Replace("<!doctype html>", "", StringComparison.Ordinal);
                var error = Record.Exception(() => XDocument.Parse(html));
                Assert.True(error is null, $"{Path.GetRelativePath(root.FullName, file)}: {error?.Message}");
            }
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void A_version_that_is_not_one_directory_name_is_refused(string version)
    {
        // The version directory is deleted before it is rewritten, so '..' would take the directory
        // above the site root with it.
        var root = Directory.CreateTempSubdirectory("docgen-site");
        try
        {
            var marker = Path.Combine(root.FullName, "keep.txt");
            File.WriteAllText(marker, "keep");

            var content = Build() with { Version = version };
            Assert.Throws<InvalidOperationException>(
                () => SiteWriter.Write(content, Path.Combine(root.FullName, "inner"), true, Assets()));

            Assert.True(File.Exists(marker));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void Writing_twice_produces_the_same_bytes()
    {
        var root = Directory.CreateTempSubdirectory("docgen-site");
        try
        {
            SiteWriter.Write(Build(), root.FullName, stable: true, Assets());
            var first = Snapshot(root.FullName);

            SiteWriter.Write(Build(), root.FullName, stable: true, Assets());
            Assert.Equal(first, Snapshot(root.FullName));
        }
        finally { root.Delete(recursive: true); }
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory
            .GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText);

    [Fact]
    public void No_written_file_carries_a_carriage_return()
    {
        var root = Directory.CreateTempSubdirectory("docgen-site");
        try
        {
            SiteWriter.Write(Build(), root.FullName, stable: true, Assets());
            foreach (var file in Directory.GetFiles(root.FullName, "*.html", SearchOption.AllDirectories))
                Assert.DoesNotContain("\r", File.ReadAllText(file));
        }
        finally { root.Delete(recursive: true); }
    }
}
