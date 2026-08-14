using Lyric.DocGen.Site;

namespace Lyric.Tests.DocGen;

/// <summary>
/// The list behind the version switcher.
///
/// <para>Merging is the substance: a build knows the one version it produces and nothing about the
/// others. An index that replaced the list would drop every earlier release out of the switcher
/// while their pages stayed reachable.</para>
/// </summary>
public class VersionIndexTests
{
    [Fact]
    public void An_added_version_arrives()
    {
        var index = VersionIndex.Empty().With("v1.0.0", stable: true);
        var entry = Assert.Single(index.Entries);
        Assert.Equal("v1.0.0", entry.Version);
        Assert.True(entry.Stable);
    }

    [Fact]
    public void Adding_keeps_what_was_there()
    {
        var index = VersionIndex.Empty()
            .With("v1.0.0", true)
            .With("v1.1.0", true);

        Assert.Equal(["v1.1.0", "v1.0.0"], index.Entries.Select(e => e.Version));
    }

    [Fact]
    public void Adding_the_same_version_twice_updates_rather_than_duplicates()
    {
        // A nightly is rebuilt daily under the same name.
        var index = VersionIndex.Empty()
            .With("nightly", false)
            .With("nightly", false);

        Assert.Single(index.Entries);
    }

    [Fact]
    public void Nightly_comes_first_and_releases_follow_newest_first()
    {
        var index = VersionIndex.Empty()
            .With("v1.0.0", true)
            .With("nightly", false)
            .With("v1.2.0", true);

        Assert.Equal(["nightly", "v1.2.0", "v1.0.0"], index.Entries.Select(e => e.Version));
    }

    [Fact]
    public void Versions_sort_by_number_and_not_as_text()
    {
        // As text v1.9.0 would sort above v1.10.0, and the switcher would offer the wrong newest.
        var index = VersionIndex.Empty()
            .With("v1.9.0", true)
            .With("v1.10.0", true)
            .With("v1.10.1", true);

        Assert.Equal(["v1.10.1", "v1.10.0", "v1.9.0"], index.Entries.Select(e => e.Version));
    }

    [Fact]
    public void The_latest_stable_is_the_newest_release_and_never_a_prerelease()
    {
        var index = VersionIndex.Empty().With("v1.0.0", true).With("nightly", false);
        Assert.Equal("v1.0.0", index.LatestStable?.Version);
        Assert.Equal("v1.0.0", index.Landing?.Version);
    }

    [Fact]
    public void Without_a_release_the_landing_is_whatever_exists()
    {
        var index = VersionIndex.Empty().With("nightly", false);
        Assert.Null(index.LatestStable);
        Assert.Equal("nightly", index.Landing?.Version);
    }

    [Fact]
    public void An_empty_index_has_no_landing() => Assert.Null(VersionIndex.Empty().Landing);

    // ------------------------------------------------------------------ on disk

    [Fact]
    public void Reading_a_root_without_an_index_gives_an_empty_one()
    {
        var dir = Directory.CreateTempSubdirectory("docgen-index");
        try
        {
            Assert.Empty(VersionIndex.Read(dir.FullName).Entries);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_written_index_reads_back_and_a_later_build_adds_to_it()
    {
        var dir = Directory.CreateTempSubdirectory("docgen-index");
        try
        {
            VersionIndex.Empty().With("v1.0.0", true).Write(dir.FullName);

            // A second, independent build: it knows only its own version.
            VersionIndex.Read(dir.FullName).With("nightly", false).Write(dir.FullName);

            var reread = VersionIndex.Read(dir.FullName);
            Assert.Equal(["nightly", "v1.0.0"], reread.Entries.Select(e => e.Version));
            Assert.Equal("v1.0.0", reread.LatestStable?.Version);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void The_file_is_named_versions_json_and_uses_camel_case()
    {
        var dir = Directory.CreateTempSubdirectory("docgen-index");
        try
        {
            VersionIndex.Empty().With("v1.0.0", true).Write(dir.FullName);
            var json = File.ReadAllText(Path.Combine(dir.FullName, VersionIndex.FileName));

            // The switcher in site.js reads these two names.
            Assert.Contains("\"version\"", json);
            Assert.Contains("\"stable\"", json);
            Assert.DoesNotContain("\r", json);
        }
        finally { dir.Delete(recursive: true); }
    }
}
