using Lyric.DocGen.Rendering;

namespace Lyric.DocGen.Site;

/// <summary>
/// Reads the changelog and cuts out the entry the welcome page shows.
///
/// <para>The changelog's own rule holds here: entries start at <c>## vX.Y.Z</c> headings and are
/// newest first. A release shows ITS entry; a nightly, which has none, shows the newest one — the
/// welcome page then still says what changed last, which is the question it answers.</para>
/// </summary>
public static class Changelog
{
    public const string Source = "CHANGELOG.md";

    /// <summary>The full changelog as markdown, from the repository root.</summary>
    public static string ReadAll(string repoRoot) =>
        File.ReadAllText(Path.Combine(repoRoot, Source));

    /// <summary>
    /// The entry for <paramref name="version"/>, or the newest entry when there is none —
    /// including a nightly, whose changes have no entry until they are released.
    /// </summary>
    /// <returns>The heading text and the entry's markdown body, without the heading line.</returns>
    public static (string Title, string Markdown)? EntryFor(string markdown, string version)
    {
        var entries = Split(markdown);
        if (entries.Count == 0) return null;

        foreach (var entry in entries)
            if (entry.Title.StartsWith(version + " ", StringComparison.Ordinal)
                || entry.Title == version)
                return entry;

        return entries[0];
    }

    /// <summary>The entries in file order, newest first — the order the file keeps by its own
    /// rule.</summary>
    private static List<(string Title, string Markdown)> Split(string markdown)
    {
        var entries = new List<(string, string)>();
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');

        string? title = null;
        var body = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (title is not null) entries.Add((title, string.Join('\n', body).Trim('\n')));
                title = line[3..].Trim();
                body.Clear();
                continue;
            }
            if (title is not null) body.Add(line);
        }
        if (title is not null) entries.Add((title, string.Join('\n', body).Trim('\n')));
        return entries;
    }
}
