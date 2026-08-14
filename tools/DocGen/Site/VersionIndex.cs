using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lyric.DocGen.Site;

/// <param name="Version">The path segment, for example <c>v1.0.0</c> or <c>nightly</c>.</param>
/// <param name="Stable">A released version. <c>nightly</c> is not one.</param>
public sealed record VersionEntry(string Version, bool Stable);

/// <summary>
/// The list of versions the site holds, kept in <c>versions.json</c> at the site root.
///
/// <para>Written by MERGING rather than replacing. A build knows the version it produces and
/// nothing about the others, and a released version's directory is never touched again — dropping
/// the older entries here would take them out of the switcher while their pages stayed reachable.
/// </para>
/// </summary>
public sealed class VersionIndex
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public const string FileName = "versions.json";

    private readonly List<VersionEntry> _entries;

    private VersionIndex(IEnumerable<VersionEntry> entries) => _entries = entries.ToList();

    public static VersionIndex Empty() => new([]);

    /// <summary>The index at <paramref name="siteRoot"/>, or an empty one on the first build.</summary>
    public static VersionIndex Read(string siteRoot)
    {
        var path = Path.Combine(siteRoot, FileName);
        if (!File.Exists(path)) return Empty();

        var entries = JsonSerializer.Deserialize<VersionEntry[]>(File.ReadAllText(path), Json);
        return new VersionIndex(entries ?? []);
    }

    /// <summary>The same index with <paramref name="version"/> added, or updated if it was already
    /// listed — a nightly is rebuilt daily under the same name.</summary>
    public VersionIndex With(string version, bool stable) =>
        new(_entries.Where(e => e.Version != version).Append(new VersionEntry(version, stable)));

    /// <summary>
    /// The switcher order: <c>nightly</c> first, then the releases newest first. Sorted by version
    /// NUMBER rather than as text, or v1.10.0 would sort below v1.9.0.
    /// </summary>
    public VersionEntry[] Entries =>
        _entries
            .OrderByDescending(e => !e.Stable)
            .ThenByDescending(e => Number(e.Version))
            .ThenBy(e => e.Version, StringComparer.Ordinal)
            .ToArray();

    /// <summary>The newest release, or <c>null</c> when only prereleases exist.</summary>
    public VersionEntry? LatestStable =>
        Entries.FirstOrDefault(e => e.Stable);

    /// <summary>Where the site root points a visitor: the newest release, else whatever is there.</summary>
    public VersionEntry? Landing => LatestStable ?? Entries.FirstOrDefault();

    public void Write(string siteRoot)
    {
        Directory.CreateDirectory(siteRoot);
        var json = JsonSerializer.Serialize(Entries, Json).ReplaceLineEndings("\n") + "\n";
        File.WriteAllText(Path.Combine(siteRoot, FileName), json);
    }

    /// <summary>
    /// Compares <c>vMAJOR.MINOR.PATCH</c> numerically. An unparsable segment counts as -1, so an
    /// unexpected name sorts last instead of throwing.
    /// </summary>
    private static (int Major, int Minor, int Patch) Number(string version)
    {
        var parts = version.TrimStart('v').Split('.');
        return (Part(0), Part(1), Part(2));

        int Part(int i) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : -1;
    }
}
