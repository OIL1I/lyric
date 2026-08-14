using System.Text.Json;
using System.Text.Json.Serialization;
using Lyric.DocGen.Extraction;
using Lyric.DocGen.Model;
using Lyric.DocGen.Site;

namespace Lyric.DocGen;

/// <summary>
/// The generator's entry point.
///
/// <para>Two commands: <c>model</c> dumps the extracted standard library as JSON, <c>site</c>
/// writes the whole documentation tree. The dump exists because it is what the golden tests compare
/// and what a person reads when a page looks wrong.</para>
/// </summary>
public static class Program
{
    /// <summary>
    /// Indented and with a trailing newline, so a snapshot diff shows lines rather than one string.
    /// Unicode is not escaped: the signatures carry '…' and the docs carry ordinary prose.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string Usage = """
        usage: docgen model <repo-root> [<output.json>]
               docgen site  <repo-root> <site-root> <version> [--stable]

        model   writes the extracted standard library as JSON
        site    writes the documentation for one version into <site-root>/<version>/

        <version> is 'nightly' or 'vMAJOR.MINOR.PATCH'. --stable marks it as a release, which
        makes the site root point at it.
        """;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "model" => Model(args),
                "site" => BuildSite(args),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (InvalidOperationException e)
        {
            return Fail(e.Message);
        }
        catch (IOException e)
        {
            return Fail(e.Message);
        }
    }

    private static int Model(string[] args)
    {
        if (args.Length is < 2 or > 3) return Fail(Usage);

        var repoRoot = Path.GetFullPath(args[1]);
        if (StdlibRoot(repoRoot) is not { } stdlib) return 1;

        var json = Serialize(StdlibExtractor.Extract(stdlib, repoRoot));
        if (args.Length == 3) File.WriteAllText(args[2], json);
        else Console.Out.Write(json);
        return 0;
    }

    private static int BuildSite(string[] args)
    {
        if (args.Length is < 4 or > 5) return Fail(Usage);
        if (args.Length == 5 && args[4] != "--stable") return Fail($"unknown option '{args[4]}'");

        var repoRoot = Path.GetFullPath(args[1]);
        var siteRoot = Path.GetFullPath(args[2]);
        var version = args[3];
        var stable = args.Length == 5;

        if (StdlibRoot(repoRoot) is null) return 1;
        if (stable && !IsRelease(version))
            return Fail($"'{version}' is not a vMAJOR.MINOR.PATCH version, so it cannot be stable");

        var content = SiteBuilder.Build(repoRoot, version);
        SiteWriter.Write(content, siteRoot, stable, Assets());

        Console.Error.WriteLine(
            $"docgen: {content.Pages.Count()} pages written to {Path.Combine(siteRoot, version)}");
        return 0;
    }

    /// <summary>Three components, all numeric — the versioning the project commits to from v1.0.</summary>
    private static bool IsRelease(string version) =>
        version.StartsWith('v')
        && version[1..].Split('.') is { Length: 3 } parts
        && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));

    private static string? StdlibRoot(string repoRoot)
    {
        var stdlib = Path.Combine(repoRoot, "stdlib");
        if (Directory.Exists(stdlib)) return stdlib;
        Fail($"no stdlib directory under {repoRoot}");
        return null;
    }

    /// <summary>The assets sit next to the binary, the same way the stdlib sits next to the
    /// compiler.</summary>
    private static string Assets() => Path.Combine(AppContext.BaseDirectory, "assets");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"docgen: {message}");
        return 1;
    }

    /// <summary>The model as JSON, with '\n' line endings on every platform so the output does not
    /// depend on where it was produced.</summary>
    public static string Serialize(DocModel model) =>
        JsonSerializer.Serialize(model, Json).ReplaceLineEndings("\n") + "\n";
}
