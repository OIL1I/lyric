using Lyric.Lsp.Documents;

namespace Lyric.Tests.Lsp;

/// <summary>
/// The conversion between a URI and a path.
///
/// <para>The reason this is hand-written rather than delegated: <see cref="System.Uri"/> leaves
/// <c>%3A</c> encoded, so <c>LocalPath</c> answers <c>/c:/Users/x.lyr</c> for the form VS Code
/// sends — a string with a leading separator that is not a path and that no file API rejects
/// loudly. The same URI with an unencoded colon answers correctly, which is why the fault survives
/// every test written against the convenient spelling.</para>
///
/// <para>The expectations are per platform rather than skipped off Windows: a test that quietly
/// returns on the platform it was not written for is green without having asked anything.</para>
/// </summary>
public sealed class DocumentUriTests
{
    [Fact]
    public void Reads_the_encoded_colon_an_editor_actually_sends()
    {
        Assert.True(DocumentUri.TryToFilePath("file:///c%3A/Users/Olivier/a.lyr", out var path));

        Assert.Equal(OperatingSystem.IsWindows()
            ? @"c:\Users\Olivier\a.lyr"
            : "/c:/Users/Olivier/a.lyr", path);
    }

    [Fact]
    public void Reads_the_unencoded_colon_as_the_same_path()
    {
        Assert.True(DocumentUri.TryToFilePath("file:///c:/Users/Olivier/a.lyr", out var plain));
        Assert.True(DocumentUri.TryToFilePath("file:///c%3A/Users/Olivier/a.lyr", out var encoded));

        // The whole point: two spellings, one file. If these differ, one buffer opens twice.
        Assert.Equal(plain, encoded);
    }

    [Fact]
    public void Decodes_percent_escapes_in_the_path()
    {
        Assert.True(DocumentUri.TryToFilePath("file:///c%3A/O%20l/%C3%A4.lyr", out var path));

        Assert.EndsWith(OperatingSystem.IsWindows() ? @"O l\ä.lyr" : "O l/ä.lyr", path);
    }

    [Fact]
    public void Decodes_per_segment_so_an_escaped_separator_stays_in_the_name()
    {
        // '%2F' is a slash IN A FILE NAME. Decoding the whole string first would promote it to a
        // directory boundary and address a different file.
        Assert.True(DocumentUri.TryToFilePath("file:///tmp/a%2Fb.lyr", out var path));

        Assert.EndsWith("a/b.lyr", path);
    }

    [Fact]
    public void Treats_a_localhost_authority_as_no_authority()
    {
        Assert.True(DocumentUri.TryToFilePath("file://localhost/tmp/a.lyr", out var withHost));
        Assert.True(DocumentUri.TryToFilePath("file:///tmp/a.lyr", out var without));

        Assert.Equal(without, withHost);
    }

    [Theory]
    [InlineData("untitled:Untitled-1")]
    [InlineData("vscode-notebook-cell:/x.lyr")]
    [InlineData("http://example.invalid/a.lyr")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_everything_that_is_not_a_file(string? uri)
    {
        // A real case, not a defensive one: an editor sends 'untitled:' for a buffer that was never
        // saved. It has no path, so it is not analysed rather than analysed against a made-up one.
        Assert.False(DocumentUri.TryToFilePath(uri, out var path));
        Assert.Equal(string.Empty, path);
    }

    [Theory]
    [InlineData("a.lyr")]
    [InlineData("a b.lyr")]
    [InlineData("ä.lyr")]
    [InlineData("a#b.lyr")]
    public void A_path_survives_the_trip_to_a_uri_and_back(string name)
    {
        // The direction that must hold. The other one does not and is not needed: a URI the client
        // sent is echoed back verbatim rather than rebuilt from its path.
        var path = Path.Combine(Path.GetTempPath(), name);

        var uri = DocumentUri.FromFilePath(path);

        Assert.True(DocumentUri.TryToFilePath(uri, out var roundTripped));
        Assert.Equal(Path.GetFullPath(path), roundTripped, DocumentUri.PathComparer);
    }

    [Fact]
    public void A_unc_share_keeps_its_two_leading_separators()
    {
        var recognised = DocumentUri.TryToFilePath("file://server/share/a.lyr", out var path);

        if (!OperatingSystem.IsWindows())
        {
            // No spelling for it here, so it is refused rather than mangled into a relative path.
            Assert.False(recognised);
            return;
        }

        Assert.True(recognised);
        Assert.Equal(@"\\server\share\a.lyr", path);
    }
}
