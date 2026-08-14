namespace Lyric.DocGen.Rendering;

/// <summary>
/// Turns a link as written in a markdown source into a link that works on the site.
///
/// <para>A relative <c>.md</c> link is correct in the repository and dead on the site, where the
/// same document lives at a different path. Rewriting happens here so the sources stay readable on
/// their own.</para>
///
/// <para>An unresolvable link yields <c>null</c> rather than a guess: the caller collects those, and
/// a dead link becomes a failing test instead of a 404.</para>
/// </summary>
/// <param name="version">The version segment the links point into, for example <c>v1.0.0</c>.</param>
public sealed class LinkResolver(string version)
{
    /// <summary>Left untouched: another host, a mail address, or a link that is already absolute.</summary>
    private static bool IsExternal(string href) =>
        href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith('/');

    /// <summary>
    /// The site URL for <paramref name="href"/> as written in <paramref name="fromSource"/>, or
    /// <c>null</c> when it points at something the site does not contain.
    /// </summary>
    /// <param name="fromSource">Repository-relative path of the document holding the link.</param>
    public string? Resolve(string fromSource, string href)
    {
        if (href.Length == 0) return null;
        if (IsExternal(href)) return href;
        if (href.StartsWith('#')) return href; // same page

        var (path, fragment) = Split(href);
        if (path.Length == 0) return null;

        var site = SitePaths.OfSource(Normalize(DirectoryOf(fromSource), path));
        return site is null ? null : SitePaths.Url(version, site) + fragment;
    }

    /// <summary>
    /// The directory of a path, with forward slashes. Not <c>Path.GetDirectoryName</c>: on Windows
    /// that returns the platform separator, and a '..' would then pop the whole path as one segment
    /// instead of one directory.
    /// </summary>
    private static string DirectoryOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized[..slash];
    }

    private static (string Path, string Fragment) Split(string href)
    {
        var hash = href.IndexOf('#');
        return hash < 0 ? (href, "") : (href[..hash], href[hash..]);
    }

    /// <summary>
    /// Resolves <c>.</c> and <c>..</c> against a base directory without touching the file system —
    /// the target need not exist for the answer to be well defined.
    /// </summary>
    private static string Normalize(string baseDir, string relative)
    {
        var segments = new List<string>();
        if (!relative.StartsWith('/'))
            segments.AddRange(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var segment in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        return string.Join("/", segments);
    }
}
