using System.Text;

namespace Lyric.DocGen.Rendering;

/// <summary>
/// Where a source document ends up on the site.
///
/// <para>The single source of truth for URLs. The navigation, the cross-links and the version
/// switcher all derive their paths from here, so a change to the layout happens in one place rather
/// than in each of them.</para>
///
/// <para>Paths are directory-style and end in a slash, so a page can gain sub-pages later without
/// its own URL changing.</para>
/// </summary>
public static class SitePaths
{
    /// <summary>
    /// The site path of a repository-relative source file, or <c>null</c> when the file is not part
    /// of the site. Forward slashes on every platform.
    /// </summary>
    public static string? OfSource(string source)
    {
        var path = source.Replace('\\', '/');

        if (path.StartsWith("docs/guide/", StringComparison.Ordinal) &&
            path.EndsWith(".md", StringComparison.Ordinal))
            return $"guide/{Slug(Path.GetFileNameWithoutExtension(path))}/";

        return path switch
        {
            "docs/Grammar.md" => "grammar/",
            "docs/Bytecode.md" => "bytecode/",
            "docs/Pack.md" => "pack/",
            "CHANGELOG.md" => "changelog/",
            _ => null,
        };
    }

    /// <summary>The site path of a standard library module, for example <c>std.io.file</c>.</summary>
    public static string OfModule(string modulePath) => $"stdlib/{modulePath}/";

    /// <summary>
    /// The absolute URL of a site path within one version, for example
    /// <c>/v1.0.0/guide/functions/</c>. The version is a path segment rather than a query, so an
    /// older version stays reachable as a frozen directory.
    /// </summary>
    public static string Url(string version, string sitePath) => $"/{version}/{sitePath}";

    /// <summary>
    /// The ordering prefix of a guide chapter — <c>03-functions.md</c> gives 3 — or
    /// <see cref="int.MaxValue"/> when there is none, so unnumbered files sort last.
    /// </summary>
    public static int Order(string source)
    {
        var name = Path.GetFileNameWithoutExtension(source.Replace('\\', '/'));
        var digits = name.TakeWhile(char.IsAsciiDigit).ToArray();
        return digits.Length > 0 && int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    /// <summary>
    /// A file name as a URL segment: lowercase, without the ordering prefix, and with every run of
    /// non-alphanumeric characters collapsed into a single hyphen.
    /// </summary>
    public static string Slug(string name)
    {
        var start = 0;
        while (start < name.Length && char.IsAsciiDigit(name[start])) start++;
        while (start < name.Length && !char.IsAsciiLetterOrDigit(name[start])) start++;
        if (start == name.Length) start = 0; // an all-digit name keeps its digits

        var sb = new StringBuilder(name.Length);
        foreach (var c in name.AsSpan(start))
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        return sb.ToString().TrimEnd('-');
    }
}
