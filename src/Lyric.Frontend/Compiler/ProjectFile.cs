using System.Text.Json;

namespace Lyric.Compiler;

/// <summary>A <c>lyric.json</c> that could not be understood. Carries the path, because the file a
/// tool complains about is rarely the one it was pointed at.</summary>
public sealed class ProjectFileException(string path, string message) : Exception(message)
{
    public string Path { get; } = path;
}

/// <summary>
/// What a project says about itself: read by every tool, executed by none.
///
/// <para>The counterpart of a build script. A script answers "what should be built" and only a build
/// can ask it; this answers "what is this project" — where the modules live, which segments belong
/// to a host — and a language server has to know that without running anything.</para>
///
/// <para>Everything here is OPTIONAL, and a project without the file behaves exactly as one did
/// before it existed: the directory of the entry file is the module root and there are no native
/// roots. That is what makes this an addition rather than a new requirement.</para>
/// </summary>
public sealed record ProjectFile
{
    /// <summary>The file name searched for, upwards from the entry file.</summary>
    public const string FileName = "lyric.json";

    /// <summary>Where the file was found. Every path it names is relative to this.</summary>
    public required string Directory { get; init; }

    /// <summary>Where the program's own modules live, absolute. Defaults to
    /// <see cref="Directory"/>.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Where the tests live, absolute — the directory only <c>lyric test</c> compiles.
    /// <c>null</c> when the file names none; the runner then tries <c>tests/</c> under
    /// <see cref="Directory"/> and treats its absence as "no tests".</summary>
    public string? TestRoot { get; init; }

    /// <summary>Module path segment to directory, absolute. Empty when the file names none.</summary>
    public required IReadOnlyDictionary<string, string> NativeRoots { get; init; }

    /// <summary>What was tolerated rather than rejected — an unknown key, most of the time. A caller
    /// prints these; ignoring them silently is how a typo becomes an afternoon.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// Looks for <see cref="FileName"/> in <paramref name="startDirectory"/> and upwards, and reads
    /// the first one found. <c>null</c> when there is none.
    /// </summary>
    /// <exception cref="ProjectFileException">A file was found and could not be understood. Not
    /// finding one is normal; finding a broken one is not.</exception>
    public static ProjectFile? Discover(string startDirectory)
    {
        var directory = new DirectoryInfo(System.IO.Path.GetFullPath(startDirectory));

        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate)) return Read(candidate);
            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Reads one file, without searching.</summary>
    public static ProjectFile Read(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(full)!;

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (IOException io)
        {
            throw new ProjectFileException(full, io.Message);
        }

        // Comments and trailing commas are allowed: this is a file people edit by hand, and the
        // usual objection to JSON as a project format is exactly those two.
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, options);
        }
        catch (JsonException json)
        {
            throw new ProjectFileException(full, json.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProjectFileException(full, "the file has to hold an object");

            var warnings = new List<string>();
            var sourceRoot = directory;
            string? testRoot = null;
            var nativeRoots = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "sourceRoot":
                        sourceRoot = Resolve(full, directory, "sourceRoot", property.Value);
                        break;

                    case "nativeRoots":
                        ReadNativeRoots(full, directory, property.Value, nativeRoots);
                        break;

                    case "testRoot":
                        testRoot = Resolve(full, directory, "testRoot", property.Value);
                        break;

                    // Tolerated rather than rejected, for the same reason the bytecode reader skips
                    // a section it does not know: a file written for a later version has to stay
                    // readable. The warning is what keeps a typo from being silent.
                    default:
                        warnings.Add($"unknown key '{property.Name}'");
                        break;
                }
            }

            return new ProjectFile
            {
                Directory = directory,
                SourceRoot = sourceRoot,
                TestRoot = testRoot,
                NativeRoots = nativeRoots,
                Warnings = warnings,
            };
        }
    }

    private static void ReadNativeRoots(string file, string directory, JsonElement element,
        Dictionary<string, string> into)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ProjectFileException(file,
                "'nativeRoots' has to be an object of segment to directory");

        foreach (var entry in element.EnumerateObject())
        {
            // The loader keys native roots by the FIRST segment of a module path, so a key with a
            // dot in it would name something that can never be looked up.
            if (entry.Name.Length == 0 || entry.Name.Contains('.'))
                throw new ProjectFileException(file,
                    $"'{entry.Name}' is not a module path segment: a native root owns one segment, "
                    + "and the compiler looks it up by the first segment of an import");

            if (!into.TryAdd(entry.Name, Resolve(file, directory, $"nativeRoots.{entry.Name}", entry.Value)))
                throw new ProjectFileException(file, $"'{entry.Name}' is named twice");
        }
    }

    /// <summary>A path from the file, made absolute against the file's own directory and checked to
    /// exist. A root that is not there is a mistake worth naming now rather than as "cannot find
    /// module" later.</summary>
    private static string Resolve(string file, string directory, string key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new ProjectFileException(file, $"'{key}' has to be a string");

        var raw = value.GetString()!;
        string resolved;
        try
        {
            resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, raw));
        }
        catch (ArgumentException)
        {
            throw new ProjectFileException(file, $"'{key}' is not a usable path: '{raw}'");
        }

        if (!System.IO.Directory.Exists(resolved))
            throw new ProjectFileException(file, $"'{key}' names '{raw}', which is not a directory");

        return resolved;
    }
}
