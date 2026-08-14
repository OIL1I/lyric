namespace Lyric.DocGen.Site;

/// <summary>
/// Writes a site content into a directory tree.
///
/// <para>Touches ONE version directory and the two files at the root. A released version is frozen
/// after it is written, and a build that emptied the whole output would take every earlier version
/// with it — the deploy publishes what lies here.</para>
/// </summary>
public static class SiteWriter
{
    /// <param name="siteRoot">The root holding all versions.</param>
    /// <param name="stable">Whether this version is a release; a nightly is not.</param>
    /// <param name="assets">Directory holding site.css and site.js.</param>
    public static void Write(SiteContent site, string siteRoot, bool stable, string assets)
    {
        var versionRoot = Path.Combine(siteRoot, site.Version);

        // Emptied so a page deleted at the source does not survive in the output. Only THIS
        // version's directory, never the root.
        if (Directory.Exists(versionRoot)) Directory.Delete(versionRoot, recursive: true);
        Directory.CreateDirectory(versionRoot);

        foreach (var page in site.Pages)
        {
            var directory = Path.Combine([versionRoot, .. page.SitePath.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
            Directory.CreateDirectory(directory);
            Write(Path.Combine(directory, "index.html"), Template.Page(site, page));
        }

        Write(Path.Combine(versionRoot, "index.html"), Template.VersionLanding(site));

        foreach (var asset in new[] { "site.css", "site.js" })
            File.Copy(Path.Combine(assets, asset), Path.Combine(versionRoot, asset), overwrite: true);

        var index = VersionIndex.Read(siteRoot).With(site.Version, stable);
        index.Write(siteRoot);

        if (index.Landing is { } landing)
            Write(Path.Combine(siteRoot, "index.html"), Template.SiteLanding(landing));
    }

    /// <summary>Always '\n', so the output does not depend on the machine that produced it.</summary>
    private static void Write(string path, string content) =>
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
}
