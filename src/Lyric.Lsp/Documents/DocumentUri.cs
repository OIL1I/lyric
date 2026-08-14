namespace Lyric.Lsp.Documents;

/// <summary>
/// The conversion between a <c>file:</c> URI and a path on this machine.
///
/// <para><see cref="System.Uri"/> does not do this correctly for the form editors actually send.
/// It leaves <c>%3A</c> encoded, and <c>LocalPath</c> then yields <c>/c:/Users/x.lyr</c> — a
/// string that is not a path, with a leading separator that no file API strips. The same URI
/// written as <c>file:///c:/…</c> yields the right answer, so the fault appears only with the
/// encoded colon, which is the form VS Code uses.</para>
///
/// <para><see cref="System.Uri.AbsoluteUri"/> does not normalise the two forms into one either;
/// they stay different strings. That is why the identity of a document is its PATH and not its
/// URI — see <see cref="DocumentStore"/>.</para>
/// </summary>
public static class DocumentUri
{
    private const string FileScheme = "file://";

    /// <summary>How two paths are compared for identity. Windows file names do not distinguish
    /// case, so two spellings of one path are one document there and two everywhere else.</summary>
    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// The path a <c>file:</c> URI names.
    ///
    /// <para><c>false</c> for every other scheme, and that is a real case rather than a defensive
    /// one: an editor sends <c>untitled:</c> for a buffer that was never saved. Such a document
    /// has no path, cannot resolve an import relative to itself, and is therefore not analysed.
    /// </para>
    /// </summary>
    public static bool TryToFilePath(string? uri, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(uri)) return false;
        if (!uri.StartsWith(FileScheme, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = uri[FileScheme.Length..];

        // A file URI carries neither, but a client that appends one must not shift the path.
        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0) rest = rest[..cut];

        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var rawPath = slash < 0 ? string.Empty : rest[slash..];

        // 'file://localhost/x' and 'file:///x' name the same file.
        if (authority.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            authority = string.Empty;

        // Decoded PER SEGMENT: a '%2F' inside a file name decodes to a slash, and decoding the
        // whole string first would turn that slash into a directory boundary.
        var decoded = string.Join('/', rawPath.Split('/').Select(Uri.UnescapeDataString));

        if (authority.Length > 0)
        {
            // A UNC share. Only Windows has a spelling for it.
            if (!OperatingSystem.IsWindows()) return false;
            path = @"\\" + Uri.UnescapeDataString(authority) + decoded.Replace('/', '\\');
            return path.Length > 2;
        }

        if (decoded.Length == 0) return false;

        if (OperatingSystem.IsWindows() && HasDriveLetter(decoded))
        {
            path = decoded[1..].Replace('/', '\\');
            return true;
        }

        path = decoded;
        return true;
    }

    /// <summary>
    /// The URI for a path on this machine.
    ///
    /// <para>Produces the unencoded-colon form (<c>file:///C:/…</c>). Both forms are accepted by
    /// every client; this one is what <see cref="System.Uri"/> itself reads back correctly, so a
    /// URI this server hands out survives being fed to the naive conversion.</para>
    ///
    /// <para>The round trip that holds is PATH to URI to PATH. The other direction does not and is
    /// not needed: a URI the client sent is echoed back verbatim rather than rebuilt.</para>
    /// </summary>
    public static string FromFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    /// <summary>A decoded path of the shape <c>/c:/…</c>, which is what a Windows file URI decodes
    /// to before the leading separator comes off.</summary>
    private static bool HasDriveLetter(string decoded) =>
        decoded.Length >= 3
        && decoded[0] == '/'
        && char.IsAsciiLetter(decoded[1])
        && decoded[2] == ':';
}
